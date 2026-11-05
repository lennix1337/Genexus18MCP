using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using GxMcp.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.SharedHost.TestHost
{
    // A scripted stand-in for the Worker child the broker starts. It speaks the same
    // stdio framing as GxMcp.Worker in shared-child mode: UTF-8 without BOM, one
    // JSON-RPC 2.0 frame per '\n'-terminated line in both directions, optionally
    // preceded by the plain-text WORKER_HANDSHAKE_* lines the broker skips.
    //
    // SDK-like requests run one at a time on a single thread, after sdk_ready, the way
    // the real child serializes SDK work. The fake/* control methods answer at once.
    internal sealed class FakeChild
    {
        internal const string ExitMethod = "fake/exit";
        internal const string StatusMethod = "fake/status";
        internal const string SetWorkingSetMethod = "fake/setWorkingSet";
        internal const string SdkReadyMethod = "notifications/worker/sdk_ready";

        internal const int ExitCodeBadScript = 2;

        private const int PageBytes = 4096;
        private const int ChunkBytes = 1024 * 1024;

        private readonly FakeChildScript _script;
        private readonly TextWriter _stderr;
        private readonly StreamWriter _stdout;
        private readonly object _stdoutGate = new object();
        private readonly BlockingCollection<JObject> _sdkQueue = new BlockingCollection<JObject>();
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly List<byte[]> _ballast = new List<byte[]>();
        private readonly object _ballastGate = new object();
        private readonly string _kb;
        private long _completed;
        private int _workingSetTargetMb;

        private FakeChild(FakeChildScript script, string kb, Stream stdout, TextWriter stderr)
        {
            _script = script;
            _kb = kb ?? string.Empty;
            _stderr = stderr ?? TextWriter.Null;
            _stdout = new StreamWriter(stdout, new UTF8Encoding(false), 64 * 1024) { AutoFlush = true, NewLine = "\n" };
        }

        internal static int Run(string[] args, Stream stdin, Stream stdout, TextWriter stderr)
        {
            if (!FakeChildScript.TryResolve(args, Environment.GetEnvironmentVariable, out FakeChildScript script, out string error))
            {
                try { stderr?.WriteLine("[FakeChild] " + error); } catch { }
                return ExitCodeBadScript;
            }
            var child = new FakeChild(script, ReadArgument(args, "--kb"), stdout, stderr);
            return child.Serve(stdin);
        }

        private int Serve(Stream stdin)
        {
            if (_script.Handshake)
            {
                WriteLine("WORKER_HANDSHAKE_START");
                WriteLine("WORKER_HANDSHAKE_FEATURES soft_reload=true single_instance=true");
            }
            SetWorkingSet(_script.WorkingSetMb);

            new Thread(ReadyLoop) { IsBackground = true, Name = "FakeChildReady" }.Start();
            var sdkThread = new Thread(SdkLoop) { IsBackground = true, Name = "FakeChildSdk" };
            sdkThread.Start();

            using (var reader = new StreamReader(stdin, new UTF8Encoding(false), false, 64 * 1024))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject frame;
                    try { frame = JsonIngress.ParseObject(line); }
                    catch
                    {
                        Log("ignored malformed stdin line");
                        continue;
                    }
                    JToken id = frame["id"];
                    if (id == null || id.Type == JTokenType.Null) continue; // notifications need no answer
                    string method = frame.Value<string>("method") ?? string.Empty;
                    if (string.Equals(method, ExitMethod, StringComparison.Ordinal))
                    {
                        Exit(frame);
                        return 0; // not reached; Exit terminates the process
                    }
                    if (string.Equals(method, StatusMethod, StringComparison.Ordinal))
                    {
                        Respond(id, BuildStatus());
                        continue;
                    }
                    if (string.Equals(method, SetWorkingSetMethod, StringComparison.Ordinal))
                    {
                        JToken mb = frame["params"]?["mb"];
                        if (mb == null || mb.Type != JTokenType.Integer || mb.Value<long>() < 0 || mb.Value<long>() > FakeChildScript.MaxWorkingSetMb)
                        {
                            RespondError(id, -32602, "params.mb must be an integer in [0, " + FakeChildScript.MaxWorkingSetMb + "].");
                            continue;
                        }
                        SetWorkingSet((int)mb.Value<long>());
                        Respond(id, BuildStatus());
                        continue;
                    }
                    _sdkQueue.Add(frame);
                }
            }
            // stdin closed: the broker stopped this child. Requests already accepted
            // still get their answer, then the process exits 0.
            _sdkQueue.CompleteAdding();
            sdkThread.Join();
            return 0;
        }

        private void ReadyLoop()
        {
            if (_script.ReadyDelayMs > 0) Thread.Sleep(_script.ReadyDelayMs);
            WriteLine(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = SdkReadyMethod,
                ["params"] = new JObject { ["kb"] = _kb }
            }.ToString(Formatting.None));
            _ready.Set();
        }

        private void SdkLoop()
        {
            _ready.Wait();
            foreach (JObject request in _sdkQueue.GetConsumingEnumerable())
            {
                string method = request.Value<string>("method") ?? string.Empty;
                int durationMs = _script.ResolveDuration(method, request["params"] as JObject);
                var started = Stopwatch.StartNew();
                if (durationMs > 0) Thread.Sleep(durationMs);
                long sequence = Interlocked.Increment(ref _completed);
                Respond(request["id"], new JObject
                {
                    ["fake"] = true,
                    ["method"] = method,
                    ["scriptedDurationMs"] = durationMs,
                    ["elapsedMs"] = started.ElapsedMilliseconds,
                    ["sequence"] = sequence,
                    ["pid"] = Process.GetCurrentProcess().Id,
                    // Echoed so routing assertions can tell which attachment the broker
                    // stamped on the request it forwarded.
                    ["attachmentId"] = request["_meta"]?["attachmentId"]?.DeepClone()
                });
            }
        }

        private void Exit(JObject frame)
        {
            JObject parameters = frame["params"] as JObject;
            int exitCode = 0;
            JToken code = parameters?["exitCode"];
            if (code != null && code.Type == JTokenType.Integer) exitCode = (int)code.Value<long>();
            bool respond = parameters?["respond"]?.Type != JTokenType.Boolean || parameters["respond"].Value<bool>();
            if (respond) Respond(frame["id"], new JObject { ["exiting"] = true, ["exitCode"] = exitCode });
            lock (_stdoutGate) { try { _stdout.Flush(); } catch { } }
            Environment.Exit(exitCode);
        }

        private JObject BuildStatus()
        {
            long workingSet = 0;
            try
            {
                using (var self = Process.GetCurrentProcess())
                {
                    self.Refresh();
                    workingSet = self.WorkingSet64;
                }
            }
            catch { }
            return new JObject
            {
                ["pid"] = Process.GetCurrentProcess().Id,
                ["ready"] = _ready.IsSet,
                ["workingSetBytes"] = workingSet,
                ["workingSetTargetMb"] = Volatile.Read(ref _workingSetTargetMb),
                ["queued"] = _sdkQueue.Count,
                ["completed"] = Interlocked.Read(ref _completed)
            };
        }

        // Holds roughly mb MiB of committed, touched memory so the broker's
        // Process.WorkingSet64 reading of this child reflects the scripted figure.
        // Lowering the target releases the ballast; the OS may trim the working set later.
        private void SetWorkingSet(int mb)
        {
            lock (_ballastGate)
            {
                while (_ballast.Count > mb) _ballast.RemoveAt(_ballast.Count - 1);
                while (_ballast.Count < mb)
                {
                    var chunk = new byte[ChunkBytes];
                    for (int offset = 0; offset < chunk.Length; offset += PageBytes) chunk[offset] = 1;
                    _ballast.Add(chunk);
                }
                Volatile.Write(ref _workingSetTargetMb, mb);
            }
            if (mb == 0) GC.Collect();
        }

        private void Respond(JToken id, JObject result)
            => WriteLine(new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToString(Formatting.None));

        private void RespondError(JToken id, int code, string message)
            => WriteLine(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            }.ToString(Formatting.None));

        private void WriteLine(string line)
        {
            lock (_stdoutGate)
            {
                try { _stdout.WriteLine(line); }
                catch (IOException) { }
            }
        }

        private void Log(string message)
        {
            try { _stderr.WriteLine("[FakeChild] " + message); } catch { }
        }

        private static string ReadArgument(string[] args, string name)
        {
            if (args == null) return string.Empty;
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return string.Empty;
        }
    }
}
