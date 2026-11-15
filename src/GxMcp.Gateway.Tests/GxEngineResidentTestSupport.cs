using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// An in-memory <c>gx-engine serve</c>: the test decides, per request, whether it
    /// answers, ends, stays silent or breaks the protocol. No process and no waiting.
    /// </summary>
    internal sealed class FakeResidentEngine : IGxEngineResidentProcess
    {
        private readonly ChunkReader _stdout = new();
        private readonly ChunkReader _stderr = new();
        private readonly LineWriter _stdin;

        internal FakeResidentEngine(Action<FakeResidentEngine, string, JArray>? onRequest = null, int protocol = 1, bool ready = true)
        {
            OnRequest = onRequest ?? ((engine, id, _) => engine.Answer(id, "{}\n", string.Empty, 0));
            _stdin = new LineWriter(this);
            if (ready) WriteStdout($"{{\"gxe\":\"serve-ready\",\"protocol\":{protocol},\"version\":\"test\"}}\n");
        }

        /// <summary>Runs when a complete request line arrives.</summary>
        internal Action<FakeResidentEngine, string, JArray> OnRequest { get; set; }

        /// <summary>The request lines received, as written by the Gateway.</summary>
        internal List<string> RequestLines { get; } = new();

        internal bool FailWrites { get; set; }
        internal int KillCount { get; private set; }

        public TextWriter StandardInput => _stdin;
        public TextReader StandardOutput => _stdout;
        public TextReader StandardError => _stderr;
        public bool HasExited { get; private set; }

        public void Kill()
        {
            KillCount++;
            Exit();
        }

        public void Dispose() => Kill();

        /// <summary>The process ends: both streams reach their end.</summary>
        internal void Exit()
        {
            HasExited = true;
            _stdout.Complete();
            _stderr.Complete();
        }

        internal void WriteStdout(params string[] chunks) => _stdout.Feed(chunks);
        internal void WriteStderr(params string[] chunks) => _stderr.Feed(chunks);

        /// <summary>A complete, well-formed answer to request <paramref name="id"/>.</summary>
        internal void Answer(string id, string stdout, string stderr, int exit)
        {
            WriteStderr(Begin(id) + stderr + "\n{\"gxe\":\"serve-end\",\"id\":\"" + id + "\"}\n");
            WriteStdout(Begin(id) + stdout + "\n{\"gxe\":\"serve-end\",\"id\":\"" + id + "\",\"exit\":" + exit + "}\n");
        }

        /// <summary>The line the engine writes on both streams when it accepts request <paramref name="id"/>.</summary>
        internal static string Begin(string id) => "{\"gxe\":\"serve-begin\",\"id\":\"" + id + "\"}\n";

        private void Receive(string line)
        {
            RequestLines.Add(line);
            JObject request = JObject.Parse(line);
            OnRequest(this, request["id"]!.ToString(), (JArray)request["args"]!);
        }

        private sealed class LineWriter : TextWriter
        {
            private readonly FakeResidentEngine _engine;
            private readonly StringBuilder _line = new();

            internal LineWriter(FakeResidentEngine engine) => _engine = engine;

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                if (_engine.FailWrites || _engine.HasExited) throw new IOException("The pipe is being closed.");
                if (value != '\n')
                {
                    _line.Append(value);
                    return;
                }
                string line = _line.ToString();
                _line.Clear();
                _engine.Receive(line);
            }
        }

        private sealed class ChunkReader : TextReader
        {
            private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
            private string _pending = string.Empty;

            internal void Feed(string[] chunks)
            {
                foreach (string chunk in chunks)
                    if (chunk.Length != 0) _chunks.Writer.TryWrite(chunk);
            }

            internal void Complete() => _chunks.Writer.TryComplete();

            public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            {
                if (_pending.Length == 0)
                {
                    if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                    if (!_chunks.Reader.TryRead(out string? next)) return 0;
                    _pending = next;
                }
                int count = Math.Min(buffer.Length, _pending.Length);
                _pending.AsSpan(0, count).CopyTo(buffer.Span);
                _pending = _pending.Substring(count);
                return count;
            }
        }
    }

    /// <summary>Hands out fake engines in order and records what was launched.</summary>
    internal sealed class FakeResidentLauncher
    {
        private readonly Func<int, FakeResidentEngine> _create;

        internal FakeResidentLauncher(Func<int, FakeResidentEngine>? create = null) =>
            _create = create ?? (_ => new FakeResidentEngine());

        internal List<FakeResidentEngine> Engines { get; } = new();
        internal List<string> Executables { get; } = new();

        internal IGxEngineResidentProcess Launch(string executable)
        {
            Executables.Add(executable);
            FakeResidentEngine engine = _create(Engines.Count);
            Engines.Add(engine);
            return engine;
        }
    }

    /// <summary>A clock the test moves.</summary>
    internal sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
