using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>The streams and lifetime of one <c>gx-engine serve</c> process.</summary>
    internal interface IGxEngineResidentProcess : IDisposable
    {
        TextWriter StandardInput { get; }
        TextReader StandardOutput { get; }
        TextReader StandardError { get; }
        bool HasExited { get; }

        /// <summary>Kills the process and what it spawned. Never throws.</summary>
        void Kill();
    }

    /// <summary>A real <c>gx-engine serve</c> child process.</summary>
    internal sealed class GxEngineResidentProcess : IGxEngineResidentProcess
    {
        private readonly Process _process;

        private GxEngineResidentProcess(Process process)
        {
            _process = process;
            ProcessId = process.Id;
        }

        /// <summary>The operating-system id of the child, readable after it is disposed.</summary>
        internal int ProcessId { get; }

        internal static IGxEngineResidentProcess Start(string executable)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // A one-shot process receives its arguments as UTF-16; a request line carries
                // them as UTF-8, without a byte order mark in front of the first request.
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            // The output encodings stay unset, as in GxEngineClient.InvokeAsync: both paths
            // decode the engine's bytes the same way.
            startInfo.ArgumentList.Add("serve");
            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("the process did not start.");
            }
            catch
            {
                process.Dispose();
                throw;
            }
            return new GxEngineResidentProcess(process);
        }

        public TextWriter StandardInput => _process.StandardInput;
        public TextReader StandardOutput => _process.StandardOutput;
        public TextReader StandardError => _process.StandardError;

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (InvalidOperationException) { return true; }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
        }

        public void Kill() => GxEngineProcessRunner.TryKillProcessTree(_process);

        public void Dispose()
        {
            Kill();
            _process.Dispose();
        }
    }

    /// <summary>
    /// What the resident path did with one invocation. The default value means the resident
    /// path did not serve it and nothing was started: the caller runs the one-shot process.
    /// </summary>
    internal readonly struct GxEngineResidentOutcome
    {
        private GxEngineResidentOutcome(GxEngineProcessRun? run, string? unobserved)
        {
            Run = run;
            UnobservedMessage = unobserved;
        }

        /// <summary>The command ran to an observed end (or to its budget, or to a cancellation).</summary>
        internal GxEngineProcessRun? Run { get; }

        /// <summary>
        /// A build was sent and its end was not observed. Its outcome is unknown, so the
        /// caller must not run it again.
        /// </summary>
        internal string? UnobservedMessage { get; }

        internal static GxEngineResidentOutcome NotServed => default;
        internal static GxEngineResidentOutcome Completed(GxEngineProcessRun run) => new(run, null);
        internal static GxEngineResidentOutcome Unobserved(string message) => new(null, message);
    }

    /// <summary>
    /// Opt-in resident engine processes (<c>GXMCP_ENGINE_RESIDENT=1</c>): one long-lived
    /// <c>gx-engine serve</c> per Knowledge Base identity instead of one process per call.
    /// The engine keeps nothing read from a Knowledge Base between requests, so an answer is
    /// as fresh as a one-shot answer (see src/gx-engine/src/serve.rs).
    ///
    /// Rules:
    /// <list type="bullet">
    /// <item><description>A request is sent only to a process that is idle; a busy process
    /// never queues a call, the call runs as a one-shot process as before.</description></item>
    /// <item><description>A read that the resident process fails to answer (it exits, breaks the
    /// protocol, exceeds the budget, or does not start) kills that process and runs as a
    /// one-shot process.</description></item>
    /// <item><description>A build is different: once its request is on its way, a failure to
    /// observe the end leaves an unknown outcome. It is reported as such and never run again
    /// (docs/engine_backend.md, reconciliation).</description></item>
    /// <item><description>A process is killed when its executable file changes, after
    /// <see cref="DefaultIdleTimeout"/> without a request, and when the Gateway exits. The
    /// engine also ends on its own when its stdin closes.</description></item>
    /// </list>
    /// </summary>
    internal sealed class GxEngineResidentPool : IDisposable
    {
        internal const string EnabledEnvVar = "GXMCP_ENGINE_RESIDENT";

        /// <summary>The framing version this client speaks (serve.rs, PROTOCOL).</summary>
        internal const int ProtocolVersion = 1;

        /// <summary>A resident process without a request for this long is ended.</summary>
        internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(120);

        /// <summary>The ready line must arrive within the version-probe budget.</summary>
        internal static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(GxEngineClient.ProbeTimeoutSeconds);

        /// <summary>Resident processes kept at once (Knowledge Bases times active versions).</summary>
        internal const int MaxProcesses = 4;

        /// <summary>The engine refuses a longer request line (serve.rs, MAX_REQUEST_BYTES).</summary>
        private const int MaxRequestBytes = 1 << 20;

        private const string BuildCommand = "rebuild-all";

        /// <summary>Reads: running one again on a one-shot process is safe.</summary>
        private static readonly HashSet<string> ReadCommands = new(StringComparer.Ordinal)
        {
            "kb-read", "kb-query", "kb-environment", "kb-list-procedures", "kb-list-objects",
            "kb-versions", "kb-inspect", "kb-diagnose",
        };

        private static readonly Regex ReadyLine = new(
            @"\A\{""gxe"":""serve-ready"",""protocol"":(\d{1,9}),""version"":""[^""\n]*""\}\n\z",
            RegexOptions.CultureInvariant);

        internal static GxEngineResidentPool Shared { get; } = new(GxEngineResidentProcess.Start, Program.Log);

        /// <summary>Test seam: replaces <see cref="Shared"/> while the opt-in is set.</summary>
        internal static GxEngineResidentPool? SharedOverrideForTest;

        /// <summary>The pool to use, or null while the opt-in is off (the default).</summary>
        internal static GxEngineResidentPool? Current => Enabled ? SharedOverrideForTest ?? Shared : null;

        internal static bool Enabled
        {
            get
            {
                string? value = Environment.GetEnvironmentVariable(EnabledEnvVar)?.Trim();
                return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        private readonly Func<string, IGxEngineResidentProcess> _launch;
        private readonly Action<string> _log;
        private readonly TimeProvider _time;
        private readonly TimeSpan _idleTimeout;
        private readonly TimeSpan _handshakeTimeout;
        private readonly TimeSpan? _requestTimeout;
        private readonly Func<string> _newRequestId;
        private readonly object _sync = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);
        private ITimer? _sweeper;
        private bool _disposed;

        /// <param name="requestTimeout">Test seam; production uses Engine.TimeoutSeconds of each call.</param>
        /// <param name="newRequestId">Test seam; production uses a new GUID per request.</param>
        internal GxEngineResidentPool(
            Func<string, IGxEngineResidentProcess> launch,
            Action<string> log,
            TimeProvider? time = null,
            TimeSpan? idleTimeout = null,
            TimeSpan? handshakeTimeout = null,
            TimeSpan? requestTimeout = null,
            Func<string>? newRequestId = null)
        {
            _launch = launch ?? throw new ArgumentNullException(nameof(launch));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _time = time ?? TimeProvider.System;
            _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
            _handshakeTimeout = handshakeTimeout ?? DefaultHandshakeTimeout;
            _requestTimeout = requestTimeout;
            _newRequestId = newRequestId ?? (() => Guid.NewGuid().ToString("N"));
        }

        internal TimeSpan IdleTimeout => _idleTimeout;
        internal TimeSpan HandshakeTimeout => _handshakeTimeout;
        internal TimeSpan? RequestTimeoutOverride => _requestTimeout;

        /// <summary>Resident processes currently kept.</summary>
        internal int ProcessCount
        {
            get { lock (_sync) return _entries.Values.Count(entry => entry.Process != null); }
        }

        private sealed class Entry
        {
            internal Entry(string key) => Key = key;
            internal string Key { get; }
            internal SemaphoreSlim Gate { get; } = new(1, 1);
            internal IGxEngineResidentProcess? Process;
            internal string? Stamp;
            internal DateTimeOffset LastUsed;
        }

        /// <summary>
        /// The command and the Knowledge Base identity of an invocation: the executable, the
        /// Knowledge Base folder and the active version the Gateway named. False for anything
        /// the resident path does not carry.
        /// </summary>
        internal static bool TryDescribe(string executable, IReadOnlyList<string> arguments, out string key, out bool isBuild)
        {
            key = string.Empty;
            isBuild = false;
            if (arguments.Any(argument => argument == null || !IsTransportable(argument))) return false;
            int index = arguments.Count > 0 && arguments[0] == "--diagnostics=json" ? 1 : 0;
            if (index >= arguments.Count) return false;
            string command = arguments[index];
            string? kb;
            if (command == BuildCommand)
            {
                isBuild = true;
                kb = ValueAfter(arguments, index + 1, "--kb");
            }
            else if (ReadCommands.Contains(command))
            {
                kb = index + 1 < arguments.Count ? arguments[index + 1] : null;
            }
            else
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(kb) || kb.StartsWith("--", StringComparison.Ordinal)) return false;
            key = string.Join("|",
                NormalizePath(executable), NormalizePath(kb),
                ValueAfter(arguments, index + 1, "--design-model") ?? "-",
                ValueAfter(arguments, index + 1, "--kb-version") ?? "-");
            return true;
        }

        private static string? ValueAfter(IReadOnlyList<string> arguments, int from, string flag)
        {
            for (int i = from; i + 1 < arguments.Count; i++)
                if (arguments[i] == flag) return arguments[i + 1];
            return null;
        }

        private static string NormalizePath(string path)
        {
            string full;
            try { full = Path.GetFullPath(path.Trim()); }
            catch { full = path.Trim(); }
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }

        /// <summary>Whether the UTF-8 request line carries exactly this UTF-16 argument.</summary>
        private static bool IsTransportable(string argument)
        {
            for (int i = 0; i < argument.Length; i++)
            {
                char c = argument[i];
                if (c == '\0') return false;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= argument.Length || !char.IsLowSurrogate(argument[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Identity of the executable file: a replaced binary is never answered by the old process.</summary>
        private static string? StampOf(string executable)
        {
            try
            {
                var info = new FileInfo(executable);
                return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>
        /// Runs one invocation on the resident process of its Knowledge Base identity, when
        /// that process is free. See the class remarks for what each outcome means.
        /// </summary>
        internal async Task<GxEngineResidentOutcome> TryRunAsync(
            string executable, IReadOnlyList<string> arguments, int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (!TryDescribe(executable, arguments, out string key, out bool isBuild))
                return GxEngineResidentOutcome.NotServed;
            string line = new JObject { ["id"] = string.Empty, ["args"] = new JArray(arguments) }.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(line) + 64 > MaxRequestBytes) return GxEngineResidentOutcome.NotServed;
            string? stamp = StampOf(executable);
            if (stamp == null) return GxEngineResidentOutcome.NotServed;

            Entry? entry = Reserve(key, stamp);
            if (entry == null) return GxEngineResidentOutcome.NotServed;
            try
            {
                // Up to here nothing has been sent: every failure leaves the one-shot path safe.
                if (entry.Process == null || entry.Stamp != stamp || entry.Process.HasExited)
                {
                    Stop(entry, entry.Process == null ? null : "its process ended or its executable changed");
                    GxEngineResidentOutcome? notStarted = await StartAsync(entry, executable, stamp, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                    if (notStarted.HasValue)
                    {
                        Forget(entry);
                        return notStarted.Value;
                    }
                }
                GxEngineResidentOutcome outcome = await ExchangeAsync(entry, arguments, isBuild, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (entry.Process == null) Forget(entry);
                return outcome;
            }
            finally
            {
                entry.LastUsed = _time.GetUtcNow();
                entry.Gate.Release();
            }
        }

        /// <summary>The idle entry for <paramref name="key"/>, entered; null when it is busy or cannot exist.</summary>
        private Entry? Reserve(string key, string stamp)
        {
            lock (_sync)
            {
                if (_disposed || _unsupported.Contains(stamp)) return null;
                if (!_entries.TryGetValue(key, out Entry? entry))
                {
                    if (_entries.Count >= MaxProcesses && !EvictIdleLocked()) return null;
                    entry = new Entry(key) { LastUsed = _time.GetUtcNow() };
                    _entries[key] = entry;
                    _sweeper ??= _time.CreateTimer(_ => SweepIdle(), null, _idleTimeout / 4, _idleTimeout / 4);
                }
                return entry.Gate.Wait(0) ? entry : null;
            }
        }

        private bool EvictIdleLocked()
        {
            foreach (Entry candidate in _entries.Values.OrderBy(entry => entry.LastUsed).ToList())
            {
                if (!candidate.Gate.Wait(0)) continue;
                _entries.Remove(candidate.Key);
                Stop(candidate, "another Knowledge Base needed its slot");
                candidate.Gate.Release();
                return true;
            }
            return false;
        }

        private void Forget(Entry entry)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(entry.Key, out Entry? current) && ReferenceEquals(current, entry))
                    _entries.Remove(entry.Key);
            }
        }

        private void Stop(Entry entry, string? reason)
        {
            IGxEngineResidentProcess? process = entry.Process;
            entry.Process = null;
            entry.Stamp = null;
            if (process == null) return;
            try { process.Dispose(); }
            catch (Exception ex) { _log($"[Engine] resident gx-engine cleanup failed: {ex.GetType().Name}: {ex.Message}"); }
            if (reason != null) _log($"[Engine] resident gx-engine ended: {reason}");
        }

        /// <summary>
        /// Starts the process and reads its ready line. Null: the process is ready. Otherwise
        /// the outcome to return; nothing was sent to it.
        /// </summary>
        private async Task<GxEngineResidentOutcome?> StartAsync(
            Entry entry, string executable, string stamp, int timeoutSeconds, CancellationToken cancellationToken)
        {
            IGxEngineResidentProcess process;
            try
            {
                process = _launch(executable);
            }
            catch (Exception ex)
            {
                _log($"[Engine] resident gx-engine did not start ({ex.GetType().Name}: {ex.Message}); using one process per call");
                return GxEngineResidentOutcome.NotServed;
            }

            Task<string?> ready = ReadFirstOutputAsync(process.StandardOutput);
            string? failure;
            try
            {
                string? first = await ready.WaitAsync(_handshakeTimeout, _time, cancellationToken).ConfigureAwait(false);
                Match match = ReadyLine.Match(first ?? string.Empty);
                failure = first == null ? "it ended before its ready line"
                    : !match.Success ? "its first output was not the ready line alone"
                    : match.Groups[1].Value != ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        ? $"it speaks protocol {match.Groups[1].Value}, this Gateway speaks {ProtocolVersion}"
                    : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Abandon(process, ready);
                return GxEngineResidentOutcome.Completed(new GxEngineProcessRun(string.Empty, string.Empty, null, timedOut: false, timeoutSeconds: timeoutSeconds));
            }
            catch (TimeoutException)
            {
                failure = $"no ready line within {_handshakeTimeout.TotalSeconds:0.###}s";
            }
            catch (Exception ex)
            {
                failure = $"reading its ready line failed ({ex.GetType().Name}: {ex.Message})";
            }

            if (failure != null)
            {
                Abandon(process, ready);
                // The same binary would fail the same way on every call.
                lock (_sync) _unsupported.Add(stamp);
                _log($"[Engine] resident mode is not available for '{executable}': {failure}; using one process per call until the executable changes");
                return GxEngineResidentOutcome.NotServed;
            }

            entry.Process = process;
            entry.Stamp = stamp;
            _log($"[Engine] resident gx-engine started for '{executable}'");
            return null;
        }

        /// <summary>Kills a process whose stream reads may still be pending, and observes them.</summary>
        private void Abandon(IGxEngineResidentProcess process, Task pending)
        {
            try { process.Dispose(); }
            catch (Exception ex) { _log($"[Engine] resident gx-engine cleanup failed: {ex.GetType().Name}: {ex.Message}"); }
            _ = pending.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task<GxEngineResidentOutcome> ExchangeAsync(
            Entry entry, IReadOnlyList<string> arguments, bool isBuild, int timeoutSeconds, CancellationToken cancellationToken)
        {
            IGxEngineResidentProcess process = entry.Process!;
            string id = _newRequestId();
            string line = new JObject { ["id"] = id, ["args"] = new JArray(arguments) }.ToString(Formatting.None);
            Task<(string Stdout, string Stderr, int Exit)?> exchange = ExchangeCoreAsync(process, id, line);
            string failure;
            bool timedOut = false;
            try
            {
                TimeSpan budget = _requestTimeout ?? TimeSpan.FromSeconds(timeoutSeconds);
                (string Stdout, string Stderr, int Exit)? answer = await exchange.WaitAsync(budget, _time, cancellationToken).ConfigureAwait(false);
                if (answer != null)
                {
                    return GxEngineResidentOutcome.Completed(new GxEngineProcessRun(
                        answer.Value.Stdout, answer.Value.Stderr, answer.Value.Exit, timedOut: false, timeoutSeconds: timeoutSeconds));
                }
                failure = "the process ended before it finished its answer";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // As for a one-shot process: a cancelled request kills the engine.
                Abandon(process, exchange);
                entry.Process = null;
                entry.Stamp = null;
                _log("[Engine] resident gx-engine ended: its request was cancelled");
                return GxEngineResidentOutcome.Completed(new GxEngineProcessRun(string.Empty, string.Empty, null, timedOut: false, timeoutSeconds: timeoutSeconds));
            }
            catch (TimeoutException)
            {
                timedOut = true;
                failure = $"no answer within the {timeoutSeconds}s budget (Engine.TimeoutSeconds)";
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            Abandon(process, exchange);
            entry.Process = null;
            entry.Stamp = null;
            if (!isBuild)
            {
                _log($"[Engine] resident gx-engine ended ({failure}); the read runs as a one-shot process");
                return GxEngineResidentOutcome.NotServed;
            }
            _log($"[Engine] resident gx-engine ended during a build ({failure}); the build is not run again");
            return timedOut
                ? GxEngineResidentOutcome.Completed(new GxEngineProcessRun(string.Empty, string.Empty, null, timedOut: true, timeoutSeconds: timeoutSeconds))
                : GxEngineResidentOutcome.Unobserved(
                    $"gx-engine (resident) was sent the build, but its completion was not observed: {failure}. "
                    + "The process was ended; reconcile before another build.");
        }

        /// <summary>
        /// Sends the request and reads both streams from their begin line to their trailer.
        /// Null: a stream ended, or did not start with the begin line of this request.
        /// </summary>
        private static async Task<(string Stdout, string Stderr, int Exit)?> ExchangeCoreAsync(
            IGxEngineResidentProcess process, string id, string line)
        {
            string begin = "{\"gxe\":\"serve-begin\",\"id\":\"" + id + "\"}\n";
            string quotedId = Regex.Escape(id);
            var stdoutTrailer = new Regex(@"\n\{""gxe"":""serve-end"",""id"":""" + quotedId + @""",""exit"":(\d{1,3})\}\n\z", RegexOptions.CultureInvariant);
            var stderrTrailer = new Regex(@"\n\{""gxe"":""serve-end"",""id"":""" + quotedId + @"""\}\n\z", RegexOptions.CultureInvariant);

            // Both streams are read while the request is written and runs: a command that
            // fills one pipe must never wait on the other (GxEngineProcessRunner, issue #498).
            Task<(string Text, Match Trailer)?> stdout = ReadFrameAsync(process.StandardOutput, begin, stdoutTrailer);
            Task<(string Text, Match Trailer)?> stderr = ReadFrameAsync(process.StandardError, begin, stderrTrailer);
            try
            {
                await process.StandardInput.WriteAsync(line + "\n").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                // A stream that is out of step ends the exchange at once; the other read ends
                // when the caller kills the process.
                Task<(string Text, Match Trailer)?> first = await Task.WhenAny(stdout, stderr).ConfigureAwait(false);
                if (await first.ConfigureAwait(false) == null) return null;
                (string Text, Match Trailer)? stdoutFrame = await stdout.ConfigureAwait(false);
                (string Text, Match Trailer)? stderrFrame = await stderr.ConfigureAwait(false);
                if (stdoutFrame == null || stderrFrame == null) return null;
                int exit = int.Parse(stdoutFrame.Value.Trailer.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return (stdoutFrame.Value.Text, stderrFrame.Value.Text, exit);
            }
            finally
            {
                _ = stdout.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _ = stderr.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        /// <summary>The longest trailer line: its fixed text, a 64-character id and the exit code.</summary>
        private const int MaxTrailerChars = 128;

        /// <summary>
        /// Reads one answer from <paramref name="reader"/>: it must start with
        /// <paramref name="begin"/> and end with <paramref name="trailer"/>; returns the text
        /// between them. Null: the stream ended, or what it carries does not start with
        /// <paramref name="begin"/> (output that is not part of this request).
        /// </summary>
        private static async Task<(string Text, Match Trailer)?> ReadFrameAsync(TextReader reader, string begin, Regex trailer)
        {
            var text = new StringBuilder();
            char[] chunk = new char[8192];
            bool began = false;
            while (true)
            {
                int read = await reader.ReadAsync(chunk.AsMemory()).ConfigureAwait(false);
                if (read == 0) return null;
                text.Append(chunk, 0, read);
                if (!began)
                {
                    int known = Math.Min(text.Length, begin.Length);
                    if (!begin.AsSpan(0, known).SequenceEqual(text.ToString(0, known))) return null;
                    if (known < begin.Length) continue;
                    began = true;
                }
                if (chunk[read - 1] != '\n') continue;
                int tailLength = Math.Min(text.Length - begin.Length, MaxTrailerChars);
                Match match = trailer.Match(text.ToString(text.Length - tailLength, tailLength));
                if (match.Success)
                    return (text.ToString(begin.Length, text.Length - begin.Length - match.Length), match);
            }
        }

        /// <summary>What a new process writes first, up to its first line break. Null: it ended before one.</summary>
        private static async Task<string?> ReadFirstOutputAsync(TextReader reader)
        {
            var text = new StringBuilder();
            char[] chunk = new char[512];
            while (true)
            {
                int read = await reader.ReadAsync(chunk.AsMemory()).ConfigureAwait(false);
                if (read == 0) return null;
                text.Append(chunk, 0, read);
                if (chunk.AsSpan(0, read).Contains('\n') || text.Length > 4096) return text.ToString();
            }
        }

        /// <summary>Ends every process that has been idle for <see cref="IdleTimeout"/>; returns how many.</summary>
        internal int SweepIdle()
        {
            var idle = new List<Entry>();
            lock (_sync)
            {
                DateTimeOffset now = _time.GetUtcNow();
                foreach (Entry entry in _entries.Values.ToList())
                {
                    if (now - entry.LastUsed < _idleTimeout || !entry.Gate.Wait(0)) continue;
                    _entries.Remove(entry.Key);
                    idle.Add(entry);
                }
                if (_entries.Count == 0)
                {
                    _sweeper?.Dispose();
                    _sweeper = null;
                }
            }
            foreach (Entry entry in idle)
            {
                Stop(entry, $"idle for {_idleTimeout.TotalSeconds:0.###}s");
                entry.Gate.Release();
            }
            return idle.Count;
        }

        /// <summary>Ends every resident process; later calls are not served.</summary>
        public void Dispose()
        {
            List<Entry> entries;
            lock (_sync)
            {
                _disposed = true;
                entries = _entries.Values.ToList();
                _entries.Clear();
                _sweeper?.Dispose();
                _sweeper = null;
            }
            // A request in flight is not waited for: killing its process ends it.
            foreach (Entry entry in entries) Stop(entry, null);
        }
    }
}
