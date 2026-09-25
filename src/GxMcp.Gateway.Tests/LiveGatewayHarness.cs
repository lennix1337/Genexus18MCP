using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Spawns the published Gateway over stdio for E2E tests gated by
    // [LiveKbFact]. Mirrors the JSON-RPC driver used by scripts under
    // scratch/usability_probe.js etc., but in C# so xunit can run it.
    //
    // Lifecycle: ctor spawns the process; IAsyncLifetime.InitializeAsync runs
    // the JSON-RPC initialize. Call() sends a tools/call request and awaits
    // the matching response. DisposeAsync closes stdin and gives the process
    // a graceful 2s before killing it (worker needs time to release the KB
    // lock; a 500ms kill leaves shared SDK state that crashes the next spawn).
    //
    // v2.6.9 — exposed as public so the test class can take it via
    // IClassFixture<LiveGatewayHarness>. Rapid per-test spawn cycles were
    // crashing the worker mid-boot on the next test ("Worker for KB
    // 'academicohomolog1' crashed/exited"); sharing the harness across all
    // tests in a class is the canonical xunit pattern for expensive resources.
    public sealed class LiveGatewayHarness : IAsyncLifetime, IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
            IntPtr processHandle,
            int flags,
            StringBuilder executablePath,
            ref int size);

        private Process? _process;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new();
        private int _nextId = 1;
        private readonly StringBuilder _stderrBuf = new StringBuilder();
        private readonly object _diagnosticsGate = new object();
        private readonly string? _gatewayLogPath;
        private readonly string? _summaryPath = Environment.GetEnvironmentVariable("GXMCP_LIVE_SUMMARY_PATH");
        private int? _gatewayPid;
        private int? _workerPid;
        private string? _lastKbAlias;
        private string? _lastSelectionState;
        private string? _lastLeaseState;
        private long _initializeMs;
        private long _kbOpenMs;
        private long _settleMs;
        private long _lastRpcMs;
        private long? _lastQueueWaitMs;
        private bool _initialized;
        private int _disposed;

        public LiveGatewayHarness()
        {
            // Resolve the published gateway from the repo root. We walk up from
            // the test bin directory because xunit copies bins to bin/Debug/...
            // Skip spawn entirely when GXMCP_TEST_KB is not set: IClassFixture
            // instances are constructed for every test class regardless of whether
            // any [LiveKbFact] inside actually runs, and spawning a doomed
            // gateway here just wastes ~5s per class on CI.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GXMCP_TEST_KB")))
            {
                return;
            }
            string exe = LocatePublishedGateway()
                ?? throw new InvalidOperationException(
                    "Published Gateway not found. Run build.ps1 before running LiveKbFact tests.");
            _gatewayLogPath = ResolveGatewayLogPath(exe);

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["GX_MCP_PORT"] = ResolveHttpPort().ToString();
            startInfo.EnvironmentVariables["GX_MCP_STDIO"] = "true";
            // GXMCP_TEST_KB opts xUnit into live discovery, but the Gateway
            // already receives the explicit KB through GX_CONFIG_PATH. Passing
            // both makes startup warmup and the configured default race to
            // spawn the same Worker, producing a false BusyReject.
            startInfo.EnvironmentVariables.Remove("GXMCP_TEST_KB");
            _process = new Process
            {
                StartInfo = startInfo
            };
            _process.OutputDataReceived += OnStdout;
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_diagnosticsGate) { _stderrBuf.AppendLine(e.Data); }
            };
            try
            {
                _process.Start();
                _gatewayPid = _process.Id;
                AssertProcessImage(_process, exe);
            }
            catch
            {
                StopOwnedProcess(_process);
                _process = null;
                throw;
            }
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Give the gateway ~800ms to set up stdio plumbing
            Thread.Sleep(800);
        }

        private static string? LocatePublishedGateway()
        {
            string? configured = Environment.GetEnvironmentVariable("GXMCP_LIVE_GATEWAY_EXE");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(configured))
                    throw new InvalidOperationException(
                        $"GXMCP_LIVE_GATEWAY_EXE must point to an existing absolute executable: {configured}");
                return Path.GetFullPath(configured);
            }

            // Search from test bin upward to find publish/GxMcp.Gateway.exe
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "publish", "GxMcp.Gateway.exe");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        private static string ResolveGatewayLogPath(string executablePath)
        {
            string directory = Environment.GetEnvironmentVariable("GXMCP_LOG_DIR") ??
                Path.GetDirectoryName(executablePath)!;
            return Path.Combine(directory, "gateway_debug.log");
        }

        internal static int ResolveDefaultRpcTimeoutMs()
        {
            string? configured = Environment.GetEnvironmentVariable("GXMCP_LIVE_RPC_TIMEOUT_MS");
            if (int.TryParse(configured, out int timeoutMs) && timeoutMs >= 1_000 && timeoutMs <= 7_200_000)
                return timeoutMs;
            return 240_000;
        }

        internal static int ResolveHttpPort()
        {
            string? configured = Environment.GetEnvironmentVariable("GX_MCP_PORT");
            if (int.TryParse(configured, out int requested) && requested is >= 1_024 and <= 65_535)
            {
                if (!CanBindPort(requested))
                    throw new InvalidOperationException($"GX_MCP_PORT {requested} is already in use; live harness refuses proxy mode.");
                return requested;
            }

            for (int candidate = 55_100; candidate <= 55_199; candidate++)
            {
                if (CanBindPort(candidate)) return candidate;
            }
            throw new InvalidOperationException("No free isolated live HTTP port was found in 55100..55199.");
        }

        private static bool CanBindPort(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void AssertProcessImage(Process process, string expectedPath)
        {
            string? actualPath = GetProcessImagePath(process);
            if (string.IsNullOrWhiteSpace(actualPath) ||
                !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Live Gateway process image mismatch (expected '{Path.GetFullPath(expectedPath)}', actual '{actualPath ?? "<unknown>"}').");
            }
        }

        private static string? GetProcessImagePath(Process process)
        {
            try
            {
                var buffer = new StringBuilder(32_768);
                int size = buffer.Capacity;
                if (QueryFullProcessImageName(process.Handle, 0, buffer, ref size))
                    return buffer.ToString();
            }
            catch { }

            try
            {
                process.Refresh();
                return process.MainModule?.FileName;
            }
            catch { return null; }
        }

        internal static string BuildRpcTimeoutDiagnostics(
            string method,
            int timeoutMs,
            bool processExited,
            string? stderrTail,
            string? gatewayLogPath)
        {
            string safeStderr = SanitizeDiagnostics(stderrTail);
            return $"RPC {method} timed out after {timeoutMs}ms; processExited={processExited}; " +
                $"gatewayLog={gatewayLogPath ?? "<unknown>"}; stderrTail={safeStderr}";
        }

        internal string DiagnosticsSummary()
            => GetDiagnosticsSnapshot().ToString(Newtonsoft.Json.Formatting.None);

        private JObject GetDiagnosticsSnapshot(string cleanupStatus = "active", bool? processExited = null)
        {
            bool exited = processExited ?? IsGatewayProcessExited();
            return new JObject
            {
                ["schemaVersion"] = "gxmcp-live-summary/1",
                ["gatewayPid"] = _gatewayPid.HasValue ? new JValue(_gatewayPid.Value) : JValue.CreateNull(),
                ["workerPid"] = _workerPid.HasValue ? new JValue(_workerPid.Value) : JValue.CreateNull(),
                ["initialized"] = _initialized,
                ["phase"] = _initialized ? "ready" : (_gatewayPid.HasValue ? "startup" : "not-started"),
                ["initializeMs"] = _initializeMs,
                ["kbOpenMs"] = _kbOpenMs,
                ["settleMs"] = _settleMs,
                ["coldStartMs"] = _initializeMs + _kbOpenMs + _settleMs,
                ["rpcMs"] = _lastRpcMs,
                ["queueWaitMs"] = _lastQueueWaitMs.HasValue ? new JValue(_lastQueueWaitMs.Value) : JValue.CreateNull(),
                ["kbAlias"] = _lastKbAlias ?? (JToken)JValue.CreateNull(),
                ["selectionState"] = _lastSelectionState ?? (JToken)JValue.CreateNull(),
                ["leaseState"] = _lastLeaseState ?? (JToken)JValue.CreateNull(),
                ["processExited"] = exited,
                ["cleanup"] = cleanupStatus,
                ["errorCount"] = CountGatewayErrors(_gatewayLogPath),
                ["gatewayLogPath"] = _gatewayLogPath ?? (JToken)JValue.CreateNull()
            };
        }

        private bool IsGatewayProcessExited()
        {
            if (_process == null) return true;
            try { return _process.HasExited; }
            catch { return true; }
        }

        private static int? CountGatewayErrors(string? logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath)) return null;
            try
            {
                int count = 0;
                foreach (var line in File.ReadLines(logPath))
                {
                    // SDK reflection probes are emitted through the Worker-Err
                    // transport even when their embedded severity is INFO. Do
                    // not count those member names (ErrorViewer, ErrorHandler,
                    // etc.) as live failures. The pre-open warmup is likewise
                    // expected because the harness opens the KB immediately
                    // after initialize.
                    if (line.IndexOf("[Warmup] Worker warmup failed: No Knowledge Base is open", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    if (line.IndexOf("[INFO]", StringComparison.OrdinalIgnoreCase) >= 0
                        && line.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) < 0
                        && line.IndexOf("[FATAL]", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (Regex.IsMatch(line, @"(?i)\b(error|exception|fatal|critical)\b")
                        && (line.IndexOf("[Worker-Err]", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("[FATAL]", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("Unhandled", StringComparison.OrdinalIgnoreCase) >= 0))
                        count++;
                }
                return count;
            }
            catch { return null; }
        }

        private void WriteDiagnosticsSummary(string cleanupStatus, bool processExited)
        {
            if (string.IsNullOrWhiteSpace(_summaryPath)) return;
            try
            {
                string? parent = Path.GetDirectoryName(_summaryPath);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                string temporary = _summaryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(
                    temporary,
                    GetDiagnosticsSnapshot(cleanupStatus, processExited).ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine,
                    new UTF8Encoding(false));
                File.Move(temporary, _summaryPath, true);
            }
            catch { }
        }

        private void RecordPayloadDiagnostics(JObject response)
        {
            var payload = ParseToolPayload(response);
            if (payload == null) return;
            if (payload["workerPid"]?.Type == JTokenType.Integer)
                _workerPid = payload["workerPid"]!.Value<int>();
            _lastKbAlias = payload["kbAlias"]?.ToString()
                ?? payload["result"]?["kbAlias"]?.ToString()
                ?? _lastKbAlias;
            _lastSelectionState = payload["selectionState"]?.ToString()
                ?? payload["result"]?["selectionState"]?.ToString()
                ?? payload["kb"]?["selectionState"]?.ToString()
                ?? payload["sessionSelection"]?["state"]?.ToString()
                ?? payload["kb"]?["sessionSelection"]?["state"]?.ToString()
                ?? _lastSelectionState;
            _lastLeaseState = payload["leaseState"]?.ToString() ?? _lastLeaseState;
            if (payload["queueWaitMs"]?.Type == JTokenType.Integer)
                _lastQueueWaitMs = payload["queueWaitMs"]!.Value<long>();
        }

        private static string SanitizeDiagnostics(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "<empty>";
            try
            {
                return Regex.Replace(
                    value,
                    @"(?i)\b(password|pwd|user\s*id|userid|token|secret|connection\s*string)\b\s*[:=]\s*[^\s;,\r\n]+",
                    "$1=<redacted>");
            }
            catch { return "<unavailable>"; }
        }

        public async Task InitializeAsync()
        {
            if (_initialized) return; // IClassFixture: only initialize once per class
            if (_process == null) return; // GXMCP_TEST_KB unset: harness is a no-op
            var initializeWatch = Stopwatch.StartNew();
            var init = await RpcAsync("initialize", new JObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "xunit-harness", ["version"] = "1" }
            }, timeoutMs: 30_000);
            initializeWatch.Stop();
            _initializeMs = initializeWatch.ElapsedMilliseconds;
            if (init?["result"] == null)
                throw new InvalidOperationException("Gateway initialize did not return a result. stderr: " + _stderrBuf);
            // Send notifications/initialized (no response expected)
            SendNotification("notifications/initialized", new JObject());
            // Open the test KB specified in GXMCP_TEST_KB
            string? testKb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB");
            if (!string.IsNullOrEmpty(testKb))
            {
                var openWatch = Stopwatch.StartNew();
                await CallToolAsync("genexus_kb", new JObject
                {
                    ["action"] = "open",
                    ["path"] = testKb
                }, timeoutMs: 60_000);
                openWatch.Stop();
                _kbOpenMs = openWatch.ElapsedMilliseconds;
            }
            // Allow worker bootstrap to settle (BulkIndex etc.)
            var settleWatch = Stopwatch.StartNew();
            // A cold GeneXus SDK/KB open can legitimately take over 15 seconds
            // before the first index-state probe is answerable. Keep the live
            // gate bounded, but do not turn a documented cold-start window into
            // a false harness failure.
            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < deadline)
            {
                var probe = await CallToolAsync("genexus_list_objects", new JObject
                {
                    ["limit"] = 1
                }, timeoutMs: 120_000);
                var payload = ParseToolPayload(probe);
                string? code = payload?["code"]?.ToString();
                string? status = payload?["status"]?.ToString();
                if (!string.Equals(code, "IndexNotReady", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "Indexing", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                await Task.Delay(500);
            }
            settleWatch.Stop();
            _settleMs = settleWatch.ElapsedMilliseconds;
            _initialized = true;
            WriteDiagnosticsSummary("active", processExited: false);
        }

        // IAsyncLifetime contract — xunit may use this path instead of
        // IDisposable when the fixture is torn down. Keep cleanup idempotent
        // so either lifecycle path releases the Gateway and its Worker.
        public Task DisposeAsync()
        {
            Dispose();
            return Task.CompletedTask;
        }

        public async Task<JObject> CallToolAsync(string name, JObject args, int timeoutMs = 0)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var resp = await RpcAsync("tools/call", new JObject
                {
                    ["name"] = name,
                    ["arguments"] = args ?? new JObject()
                }, timeoutMs);
                RecordPayloadDiagnostics(resp);
                return resp;
            }
            finally
            {
                watch.Stop();
                _lastRpcMs = watch.ElapsedMilliseconds;
            }
        }

        public static JObject? ParseToolPayload(JObject toolResponse)
        {
            try
            {
                string txt = toolResponse?["result"]?["content"]?[0]?["text"]?.ToString() ?? "{}";
                return JObject.Parse(txt);
            }
            catch { return null; }
        }

        public static bool IsToolError(JObject toolResponse)
            => toolResponse?["result"]?["isError"]?.ToObject<bool?>() == true;

        private async Task<JObject> RpcAsync(string method, JObject @params, int timeoutMs)
        {
            if (_process == null)
                throw new InvalidOperationException("LiveGatewayHarness has no process — GXMCP_TEST_KB was not set when the fixture was constructed.");
            if (timeoutMs <= 0) timeoutMs = ResolveDefaultRpcTimeoutMs();
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JObject>();
            _pending[id] = tcs;
            var env = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params
            };
            _process.StandardInput.WriteLine(env.ToString(Newtonsoft.Json.Formatting.None));
            _process.StandardInput.Flush();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                bool processExited = false;
                try { processExited = _process.HasExited; } catch { }
                string stderr;
                lock (_diagnosticsGate) { stderr = _stderrBuf.ToString(); }
                throw new TimeoutException(BuildRpcTimeoutDiagnostics(
                    method, timeoutMs, processExited, stderr, _gatewayLogPath) +
                    $"; lifecycle={DiagnosticsSummary()}");
            }
            return await tcs.Task;
        }

        private void SendNotification(string method, JObject @params)
        {
            if (_process == null) return;
            var env = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = @params
            };
            _process.StandardInput.WriteLine(env.ToString(Newtonsoft.Json.Formatting.None));
            _process.StandardInput.Flush();
        }

        private void OnStdout(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            if (!e.Data.StartsWith("{")) return;
            JObject msg;
            try { msg = JObject.Parse(e.Data); } catch { return; }
            var idTok = msg["id"];
            if (idTok == null || idTok.Type == JTokenType.Null) return;
            int id = idTok.ToObject<int>();
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(msg);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var process = Interlocked.Exchange(ref _process, null);
            if (process == null)
            {
                WriteDiagnosticsSummary("not-started", processExited: true);
                return;
            }
            bool stopped = StopOwnedProcess(process);
            WriteDiagnosticsSummary(stopped ? "passed" : "failed", stopped);
        }

        internal static bool StopOwnedProcess(Process process)
        {
            bool stopped = false;
            try
            {
                if (process.HasExited) return true;
                try { process.StandardInput.Close(); } catch { }
                // 2s grace lets the worker release the KB lock + drain its
                // EditSnapshotStore writes. 500ms was too aggressive — the
                // shared SDK state outlived the kill and crashed the next
                // spawn on rapid test-class cycles.
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                stopped = process.HasExited;
            }
            catch
            {
                try { stopped = process.HasExited; } catch { }
            }
            finally { process.Dispose(); }
            return stopped;
        }
    }
}
