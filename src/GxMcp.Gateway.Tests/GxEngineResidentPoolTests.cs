using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The resident engine pool (GXMCP_ENGINE_RESIDENT): what it answers, when it gives a
    /// call back to the one-shot path, and that a build whose end was not observed is never
    /// handed back. The engine is an in-memory fake, except in the last tests, where a .cmd
    /// stub speaks the protocol through real pipes.
    /// </summary>
    public sealed class GxEngineResidentPoolTests : IDisposable
    {
        private const int Budget = 30;
        private readonly string _directory;
        private readonly string _executable;
        private readonly List<string> _log = new();

        public GxEngineResidentPoolTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "gxmcp-engine-resident-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _executable = Path.Combine(_directory, "gx-engine.exe");
            File.WriteAllText(_executable, "not a real engine");
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        private GxEngineResidentPool Pool(
            FakeResidentLauncher launcher, TimeProvider? time = null, TimeSpan? requestTimeout = null, TimeSpan? handshakeTimeout = null) =>
            new(launcher.Launch, _log.Add, time, requestTimeout: requestTimeout, handshakeTimeout: handshakeTimeout);

        private static string[] Query(string kb = @"C:\KBs\Alpha", params string[] more) =>
            new[] { "--diagnostics=json", "kb-query", kb, "--type", "Procedure" }.Concat(more).ToArray();

        private static string[] Build(string kb = @"C:\KBs\Alpha", params string[] more) =>
            new[] { "--diagnostics=json", "rebuild-all", "--kb", kb, "--output", kb + @"\.gx-engine\build" }.Concat(more).ToArray();

        private static void AssertNotServed(GxEngineResidentOutcome outcome)
        {
            Assert.Null(outcome.Run);
            Assert.Null(outcome.UnobservedMessage);
        }

        // ------------------------------------------------------------ answers

        [Fact]
        public async Task AnAnswer_CarriesTheCommandsOutputAndExitCode_AndTheRequestCarriesTheArguments()
        {
            string[] arguments = Query(@"C:\KBs\Açaí ""quoted""", "--name", "línea\nnova");
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((engine, id, _) =>
                engine.Answer(id, "{\"objects\":[]}\n", "warning: one\n", 3)));
            using var pool = Pool(launcher);

            GxEngineResidentOutcome outcome = await pool.TryRunAsync(_executable, arguments, Budget, CancellationToken.None);

            GxEngineProcessRun run = Assert.NotNull(outcome.Run);
            Assert.Equal("{\"objects\":[]}\n", run.StandardOutput);
            Assert.Equal("warning: one\n", run.StandardError);
            Assert.Equal(3, run.ExitCode);
            Assert.False(run.TimedOut);
            Assert.Equal(Budget, run.TimeoutSeconds);
            Assert.Equal(_executable, Assert.Single(launcher.Executables));

            string line = Assert.Single(launcher.Engines[0].RequestLines);
            Assert.DoesNotContain('\r', line);
            JObject request = JObject.Parse(line);
            Assert.Equal(new[] { "id", "args" }, request.Properties().Select(property => property.Name));
            Assert.Matches("^[0-9a-f]{32}$", request["id"]!.ToString());
            Assert.Equal(arguments, request["args"]!.Select(argument => argument.ToString()));
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("no final line break", "no final line break either")]
        [InlineData("line one\r\nline two\r\n", "\n\n")]
        [InlineData("{\"gxe\":\"serve-end\",\"id\":\"00000000000000000000000000000000\",\"exit\":0}\n", "{\"gxe\":\"serve-end\",\"id\":\"00000000000000000000000000000000\"}\n")]
        public async Task TheOutputBeforeTheTrailer_IsReturnedCharacterForCharacter(string stdout, string stderr)
        {
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((engine, id, _) => engine.Answer(id, stdout, stderr, 0)));
            using var pool = Pool(launcher);

            GxEngineProcessRun run = Assert.NotNull((await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run);

            Assert.Equal(stdout, run.StandardOutput);
            Assert.Equal(stderr, run.StandardError);
        }

        [Fact]
        public async Task AnAnswerThatArrivesInPieces_IsReassembled()
        {
            string large = new string('x', 50_000) + "\n";
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((engine, id, _) =>
            {
                string begin = FakeResidentEngine.Begin(id);
                engine.WriteStderr(begin.Substring(0, 9), begin.Substring(9) + "\n{\"gxe\":\"serve-", "end\",\"id\":\"" + id + "\"}", "\n");
                engine.WriteStdout(begin.Substring(0, 1), begin.Substring(1), large.Substring(0, 20_000), large.Substring(20_000),
                    "\n{\"gxe\":\"serve-end\",\"id\":\"" + id + "\",\"ex", "it\":0}\n");
            }));
            using var pool = Pool(launcher);

            GxEngineProcessRun run = Assert.NotNull((await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run);

            Assert.Equal(large, run.StandardOutput);
            Assert.Equal(string.Empty, run.StandardError);
            Assert.Equal(0, run.ExitCode);
        }

        [Fact]
        public async Task RequestsForOneKnowledgeBaseIdentity_ShareOneProcess_AndOtherIdentitiesGetTheirOwn()
        {
            var launcher = new FakeResidentLauncher();
            using var pool = Pool(launcher);

            foreach (string[] arguments in new[]
            {
                Query(@"C:\KBs\Alpha"),
                Query(@"c:\kbs\alpha\"),
                new[] { "kb-read", @"C:\KBs\Alpha", "PAlpha" },
                Build(@"C:\KBs\Alpha"),
            })
                Assert.NotNull((await pool.TryRunAsync(_executable, arguments, Budget, CancellationToken.None)).Run);
            Assert.Single(launcher.Engines);
            Assert.Equal(4, launcher.Engines[0].RequestLines.Count);

            Assert.NotNull((await pool.TryRunAsync(_executable, Query(@"C:\KBs\Beta"), Budget, CancellationToken.None)).Run);
            Assert.NotNull((await pool.TryRunAsync(_executable, Query(@"C:\KBs\Alpha", "--design-model", "4", "--kb-version", "2"), Budget, CancellationToken.None)).Run);
            Assert.NotNull((await pool.TryRunAsync(_executable, Query(@"C:\KBs\Alpha", "--design-model", "4", "--kb-version", "3"), Budget, CancellationToken.None)).Run);
            Assert.Equal(4, launcher.Engines.Count);
            Assert.Equal(4, pool.ProcessCount);
            Assert.All(launcher.Engines, engine => Assert.Equal(0, engine.KillCount));
        }

        [Theory]
        [InlineData("kb-write", @"C:\KBs\Alpha", "PAlpha", "--part", "Source", "--content-file", "x")]
        [InlineData("--version")]
        [InlineData("run", "project.json")]
        [InlineData("kb-query")]
        [InlineData("kb-query", "--type", "Procedure")]
        [InlineData("rebuild-all", "--output", "out")]
        [InlineData("--diagnostics=json")]
        public async Task ACallTheResidentPathDoesNotCarry_StartsNothing(params string[] arguments)
        {
            var launcher = new FakeResidentLauncher();
            using var pool = Pool(launcher);

            AssertNotServed(await pool.TryRunAsync(_executable, arguments, Budget, CancellationToken.None));

            Assert.Empty(launcher.Engines);
        }

        [Fact]
        public async Task AnArgumentTheRequestLineCannotCarryExactly_StartsNothing()
        {
            var launcher = new FakeResidentLauncher();
            using var pool = Pool(launcher);

            foreach (string name in new[] { "unpaired " + (char)0xD800 + " surrogate", "low " + (char)0xDC00, "nul " + (char)0 + " character" })
                AssertNotServed(await pool.TryRunAsync(_executable, Query(@"C:\KBs\Alpha", "--name", name), Budget, CancellationToken.None));
            Assert.Empty(launcher.Engines);

            // A paired surrogate is a character like any other.
            string paired = "emoji " + char.ConvertFromUtf32(0x1F600);
            Assert.NotNull((await pool.TryRunAsync(_executable, Query(@"C:\KBs\Alpha", "--name", paired), Budget, CancellationToken.None)).Run);
            Assert.Equal(paired, JObject.Parse(launcher.Engines[0].RequestLines[0])["args"]!.Last!.ToString());
        }

        // -------------------------------------------- reads fall back, safely

        [Fact]
        public async Task AReadWhoseProcessEnds_IsHandedBack_AndTheNextCallGetsANewProcess()
        {
            var launcher = new FakeResidentLauncher(index => index == 0
                ? new FakeResidentEngine((engine, _, _) => { engine.WriteStdout("{\"half\":"); engine.Exit(); })
                : new FakeResidentEngine());
            using var pool = Pool(launcher);

            AssertNotServed(await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None));
            Assert.Equal(1, launcher.Engines[0].KillCount);
            Assert.Equal(0, pool.ProcessCount);

            Assert.NotNull((await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run);
            Assert.Equal(2, launcher.Engines.Count);
        }

        [Fact]
        public async Task AReadThatExceedsTheBudget_KillsTheProcess_AndIsHandedBack()
        {
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((_, _, _) => { }));
            using var pool = Pool(launcher, requestTimeout: TimeSpan.FromMilliseconds(100));

            AssertNotServed(await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None));

            Assert.Equal(1, launcher.Engines[0].KillCount);
            Assert.Equal(0, pool.ProcessCount);
            Assert.Contains(_log, line => line.Contains("the read runs as a one-shot process"));
        }

        [Theory]
        [InlineData("another-request-id")]
        [InlineData("no-begin-line")]
        [InlineData("no-stderr-trailer")]
        [InlineData("write-fails")]
        public async Task AReadWhoseAnswerBreaksTheProtocol_KillsTheProcess_AndIsHandedBack(string fault)
        {
            var launcher = new FakeResidentLauncher(_ =>
            {
                var engine = new FakeResidentEngine((engine, id, _) =>
                {
                    switch (fault)
                    {
                        case "another-request-id":
                            engine.Answer(new string('0', 32), "{}\n", string.Empty, 0);
                            break;
                        case "no-begin-line":
                            engine.WriteStderr("\n{\"gxe\":\"serve-end\",\"id\":\"" + id + "\"}\n");
                            engine.WriteStdout("{}\n\n{\"gxe\":\"serve-end\",\"id\":\"" + id + "\",\"exit\":0}\n");
                            break;
                        case "no-stderr-trailer":
                            engine.WriteStderr(FakeResidentEngine.Begin(id));
                            engine.WriteStdout(FakeResidentEngine.Begin(id) + "{}\n\n{\"gxe\":\"serve-end\",\"id\":\"" + id + "\",\"exit\":0}\n");
                            break;
                    }
                });
                engine.FailWrites = fault == "write-fails";
                return engine;
            });
            using var pool = Pool(launcher, requestTimeout: TimeSpan.FromMilliseconds(100));

            AssertNotServed(await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None));

            Assert.True(launcher.Engines[0].KillCount >= 1);
            Assert.Equal(0, pool.ProcessCount);
        }

        [Fact]
        public async Task OutputLeftOnAStreamAfterAnAnswer_IsNeverPartOfTheNextAnswer()
        {
            int requests = 0;
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((engine, id, _) =>
            {
                engine.Answer(id, "{\"answer\":" + ++requests + "}\n", string.Empty, 0);
                // Written after the trailer of the first answer: it belongs to no request.
                if (requests == 1) engine.WriteStdout("late line\n");
            }));
            using var pool = Pool(launcher);

            Assert.Equal("{\"answer\":1}\n", (await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run!.Value.StandardOutput);
            // The second answer would start with the late line: the process is out of step.
            AssertNotServed(await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None));
            Assert.Equal(1, launcher.Engines[0].KillCount);

            Assert.Equal("{\"answer\":3}\n", (await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run!.Value.StandardOutput);
            Assert.Equal(2, launcher.Engines.Count);
        }

        [Fact]
        public async Task ABusyProcess_NeverQueuesACall()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((engine, id, _) =>
            {
                received.SetResult();
                release.Task.ContinueWith(released => engine.Answer(id, "first\n", string.Empty, 0), TaskScheduler.Default);
            }));
            using var pool = Pool(launcher);

            Task<GxEngineResidentOutcome> first = pool.TryRunAsync(_executable, Build(), Budget, CancellationToken.None);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The first request is still running: the second must come back at once, not wait for it.
            Task<GxEngineResidentOutcome> second = Task.Run(() => pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None));
            AssertNotServed(await second.WaitAsync(TimeSpan.FromSeconds(2)));
            release.SetResult();

            Assert.Equal("first\n", Assert.NotNull((await first).Run).StandardOutput);
            Assert.Single(launcher.Engines);
            Assert.Single(launcher.Engines[0].RequestLines);
        }

        [Fact]
        public async Task ACancelledRequest_KillsTheProcess_AndIsNotHandedBack()
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((_, _, _) => received.SetResult()));
            using var pool = Pool(launcher);
            using var cancellation = new CancellationTokenSource();

            Task<GxEngineResidentOutcome> pending = pool.TryRunAsync(_executable, Query(), Budget, cancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            GxEngineProcessRun run = Assert.NotNull((await pending).Run);

            Assert.Null(run.ExitCode);
            Assert.False(run.TimedOut);
            Assert.Equal(1, launcher.Engines[0].KillCount);
            Assert.Equal(0, pool.ProcessCount);
        }

        // ------------------------------------------- builds are never replayed

        [Theory]
        [InlineData("ends")]
        [InlineData("garbage")]
        [InlineData("write-fails")]
        public async Task ABuildWhoseEndIsNotObserved_IsReportedAsUnknown_NeverHandedBack(string fault)
        {
            var launcher = new FakeResidentLauncher(_ =>
            {
                var engine = new FakeResidentEngine((engine, _, _) =>
                {
                    if (fault == "garbage") engine.WriteStdout("{\"status\":\"complete\"}\n");
                    engine.Exit();
                });
                engine.FailWrites = fault == "write-fails";
                return engine;
            });
            using var pool = Pool(launcher);

            GxEngineResidentOutcome outcome = await pool.TryRunAsync(_executable, Build(), Budget, CancellationToken.None);

            Assert.Null(outcome.Run);
            string message = Assert.IsType<string>(outcome.UnobservedMessage);
            Assert.Contains("completion was not observed", message);
            Assert.Contains("reconcile before another build", message);
            Assert.True(launcher.Engines[0].KillCount >= 1);
            Assert.Equal(0, pool.ProcessCount);
        }

        [Fact]
        public async Task ABuildThatExceedsTheBudget_IsATimedOutRun_NeverHandedBack()
        {
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((_, _, _) => { }));
            using var pool = Pool(launcher, requestTimeout: TimeSpan.FromMilliseconds(100));

            GxEngineProcessRun run = Assert.NotNull((await pool.TryRunAsync(_executable, Build(), Budget, CancellationToken.None)).Run);

            Assert.True(run.TimedOut);
            Assert.Null(run.ExitCode);
            Assert.Equal(Budget, run.TimeoutSeconds);
            Assert.Equal(1, launcher.Engines[0].KillCount);
        }

        [Fact]
        public async Task ABuildWhoseProcessCannotStart_IsHandedBack_BecauseNothingWasSent()
        {
            var failing = new GxEngineResidentPool(_ => throw new InvalidOperationException("cannot start"), _log.Add);
            AssertNotServed(await failing.TryRunAsync(_executable, Build(), Budget, CancellationToken.None));

            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine(ready: false));
            using var pool = Pool(launcher, handshakeTimeout: TimeSpan.FromMilliseconds(100));
            AssertNotServed(await pool.TryRunAsync(_executable, Build(), Budget, CancellationToken.None));
            Assert.Empty(launcher.Engines[0].RequestLines);
            Assert.Equal(1, launcher.Engines[0].KillCount);
        }

        // ------------------------------------------- handshake and executable

        [Theory]
        [InlineData("protocol-2")]
        [InlineData("not-ready-line")]
        [InlineData("ends")]
        [InlineData("output-before-ready")]
        public async Task AnEngineThatDoesNotSpeakTheProtocol_IsNotUsed_AndNotStartedAgainUntilItChanges(string fault)
        {
            var launcher = new FakeResidentLauncher(_ =>
            {
                switch (fault)
                {
                    case "protocol-2":
                        return new FakeResidentEngine(protocol: 2);
                    case "output-before-ready":
                    {
                        var engine = new FakeResidentEngine(ready: false);
                        engine.WriteStdout("warning\n{\"gxe\":\"serve-ready\",\"protocol\":1,\"version\":\"test\"}\n");
                        return engine;
                    }
                    default:
                    {
                        var engine = new FakeResidentEngine(ready: false);
                        if (fault == "not-ready-line") engine.WriteStdout("{\"name\":\"Alpha\"}\n");
                        engine.Exit();
                        return engine;
                    }
                }
            });
            using var pool = Pool(launcher, handshakeTimeout: TimeSpan.FromSeconds(10));

            AssertNotServed(await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None));
            AssertNotServed(await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None));
            AssertNotServed(await pool.TryRunAsync(_executable, Build(@"C:\KBs\Other"), Budget, CancellationToken.None));

            FakeResidentEngine only = Assert.Single(launcher.Engines);
            Assert.Empty(only.RequestLines);
            Assert.True(only.KillCount >= 1);
            Assert.Contains(_log, line => line.Contains("resident mode is not available"));

            // A replaced executable is a different engine: it is tried again.
            File.WriteAllText(_executable, "a newer engine build");
            AssertNotServed(await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None));
            Assert.Equal(2, launcher.Engines.Count);
        }

        [Fact]
        public async Task AReplacedExecutable_IsNeverAnsweredByTheOldProcess()
        {
            var launcher = new FakeResidentLauncher(index => new FakeResidentEngine((engine, id, _) =>
                engine.Answer(id, $"engine {index}\n", string.Empty, 0)));
            using var pool = Pool(launcher);

            Assert.Equal("engine 0\n", (await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run!.Value.StandardOutput);
            File.WriteAllText(_executable, "a newer engine build");
            Assert.Equal("engine 1\n", (await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run!.Value.StandardOutput);

            Assert.Equal(1, launcher.Engines[0].KillCount);
            Assert.Single(launcher.Engines[0].RequestLines);
            Assert.Equal(1, pool.ProcessCount);
        }

        [Fact]
        public async Task AProcessThatEndedWhileIdle_IsReplacedBeforeAnythingIsSent_EvenForABuild()
        {
            var launcher = new FakeResidentLauncher();
            using var pool = Pool(launcher);
            Assert.NotNull((await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run);
            launcher.Engines[0].Exit();

            GxEngineProcessRun run = Assert.NotNull((await pool.TryRunAsync(_executable, Build(), Budget, CancellationToken.None)).Run);

            Assert.Equal(0, run.ExitCode);
            Assert.Equal(2, launcher.Engines.Count);
            Assert.Single(launcher.Engines[0].RequestLines);
            Assert.Single(launcher.Engines[1].RequestLines);
        }

        [Fact]
        public async Task AMissingExecutable_StartsNothing()
        {
            var launcher = new FakeResidentLauncher();
            using var pool = Pool(launcher);

            AssertNotServed(await pool.TryRunAsync(Path.Combine(_directory, "absent.exe"), Query(), Budget, CancellationToken.None));

            Assert.Empty(launcher.Engines);
        }

        // --------------------------------------------------------- lifetime

        [Fact]
        public async Task AnIdleProcess_IsEndedAfterTheIdleTimeout_AndNotBefore()
        {
            var clock = new ManualClock();
            var launcher = new FakeResidentLauncher();
            using var pool = Pool(launcher, clock);
            Assert.NotNull((await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run);

            clock.Now += pool.IdleTimeout - TimeSpan.FromSeconds(1);
            Assert.Equal(0, pool.SweepIdle());
            Assert.Equal(0, launcher.Engines[0].KillCount);

            // A request restarts the idle period.
            Assert.NotNull((await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run);
            clock.Now += pool.IdleTimeout - TimeSpan.FromSeconds(1);
            Assert.Equal(0, pool.SweepIdle());

            clock.Now += TimeSpan.FromSeconds(1);
            Assert.Equal(1, pool.SweepIdle());
            Assert.Equal(1, launcher.Engines[0].KillCount);
            Assert.Equal(0, pool.ProcessCount);

            Assert.NotNull((await pool.TryRunAsync(_executable, Query(), Budget, CancellationToken.None)).Run);
            Assert.Equal(2, launcher.Engines.Count);
        }

        [Fact]
        public async Task ABusyProcess_IsNotEndedByTheIdleSweep()
        {
            var clock = new ManualClock();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var launcher = new FakeResidentLauncher(_ => new FakeResidentEngine((engine, id, _) =>
            {
                received.SetResult();
                release.Task.ContinueWith(released => engine.Answer(id, "done\n", string.Empty, 0), TaskScheduler.Default);
            }));
            using var pool = Pool(launcher, clock);

            Task<GxEngineResidentOutcome> build = pool.TryRunAsync(_executable, Build(), Budget, CancellationToken.None);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Now += pool.IdleTimeout + pool.IdleTimeout;
            Assert.Equal(0, pool.SweepIdle());
            release.SetResult();

            Assert.Equal("done\n", Assert.NotNull((await build).Run).StandardOutput);
            Assert.Equal(0, launcher.Engines[0].KillCount);
        }

        [Fact]
        public async Task Dispose_EndsEveryProcess_AndLaterCallsStartNothing()
        {
            var launcher = new FakeResidentLauncher();
            var pool = Pool(launcher);
            Assert.NotNull((await pool.TryRunAsync(_executable, Query(@"C:\KBs\Alpha"), Budget, CancellationToken.None)).Run);
            Assert.NotNull((await pool.TryRunAsync(_executable, Query(@"C:\KBs\Beta"), Budget, CancellationToken.None)).Run);

            pool.Dispose();

            Assert.All(launcher.Engines, engine => Assert.Equal(1, engine.KillCount));
            Assert.Equal(0, pool.ProcessCount);
            AssertNotServed(await pool.TryRunAsync(_executable, Query(@"C:\KBs\Alpha"), Budget, CancellationToken.None));
            Assert.Equal(2, launcher.Engines.Count);
        }

        [Fact]
        public async Task TheProcessCap_EndsTheLeastRecentlyUsedIdleProcess()
        {
            var clock = new ManualClock();
            var launcher = new FakeResidentLauncher();
            using var pool = Pool(launcher, clock);
            for (int i = 0; i <= GxEngineResidentPool.MaxProcesses; i++)
            {
                clock.Now += TimeSpan.FromSeconds(1);
                Assert.NotNull((await pool.TryRunAsync(_executable, Query($@"C:\KBs\Kb{i}"), Budget, CancellationToken.None)).Run);
            }

            Assert.Equal(GxEngineResidentPool.MaxProcesses, pool.ProcessCount);
            Assert.Equal(1, launcher.Engines[0].KillCount);
            Assert.All(launcher.Engines.Skip(1), engine => Assert.Equal(0, engine.KillCount));
        }

        [Fact]
        public void TheSharedPool_UsesTheProductionBudgets()
        {
            Assert.Equal(TimeSpan.FromSeconds(120), GxEngineResidentPool.Shared.IdleTimeout);
            Assert.Equal(TimeSpan.FromSeconds(GxEngineClient.ProbeTimeoutSeconds), GxEngineResidentPool.Shared.HandshakeTimeout);
            // Null: each request uses the Engine.TimeoutSeconds budget of its call.
            Assert.Null(GxEngineResidentPool.Shared.RequestTimeoutOverride);
            Assert.Equal(1, GxEngineResidentPool.ProtocolVersion);
        }

        // ------------------------------------------------- a real child process

        /// <summary>
        /// A .cmd that speaks the protocol with fixed bytes: ready line, then one prepared
        /// answer per request line. The request ids are fixed through the pool's test seam.
        /// </summary>
        private string CreateProtocolStub(params (string Stdout, string Stderr, int Exit)[] answers)
        {
            var script = new StringBuilder("@echo off\r\ntype \"%~dp0ready.txt\"\r\n");
            File.WriteAllText(Path.Combine(_directory, "ready.txt"), "{\"gxe\":\"serve-ready\",\"protocol\":1,\"version\":\"stub\"}\n");
            for (int i = 0; i < answers.Length; i++)
            {
                string id = RequestId(i);
                string begin = FakeResidentEngine.Begin(id);
                File.WriteAllText(Path.Combine(_directory, $"a{i}.err"), begin + answers[i].Stderr + "\n{\"gxe\":\"serve-end\",\"id\":\"" + id + "\"}\n");
                File.WriteAllText(Path.Combine(_directory, $"a{i}.out"),
                    begin + answers[i].Stdout + "\n{\"gxe\":\"serve-end\",\"id\":\"" + id + "\",\"exit\":" + answers[i].Exit + "}\n");
                script.Append("set /p line=\r\n");
                script.Append("echo %line%>> \"%~dp0requests.txt\"\r\n");
                script.Append($"type \"%~dp0a{i}.err\" 1>&2\r\n");
                script.Append($"type \"%~dp0a{i}.out\"\r\n");
            }
            // Stay alive like the engine does, until stdin closes or the process is killed.
            script.Append("set /p line=\r\n");
            string path = Path.Combine(_directory, "engine.cmd");
            File.WriteAllText(path, script.ToString(), Encoding.ASCII);
            return path;
        }

        private static string RequestId(int index) => index.ToString("x32");

        /// <summary>Starts real children and remembers their process ids.</summary>
        private static Func<string, IGxEngineResidentProcess> RealLauncher(List<int> processIds) => executable =>
        {
            IGxEngineResidentProcess process = GxEngineResidentProcess.Start(executable);
            processIds.Add(((GxEngineResidentProcess)process).ProcessId);
            return process;
        };

        /// <summary>Whether the operating system still runs the process, asked of the system, not of our handle.</summary>
        private static bool IsRunning(int processId)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                return !process.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        [Fact]
        public async Task ARealChildProcess_AnswersThroughItsPipes_IsReused_AndEndsWithThePool()
        {
            string stub = CreateProtocolStub(
                ("{\"objects\":[{\"name\":\"Alpha\"}],\"total\":1}\n", "", 0),
                ("", "{\"category\":\"input\",\"message\":\"object 'PMissing' was not found\",\"location\":null}\n", 1));
            var started = new List<int>();
            int next = 0;
            var pool = new GxEngineResidentPool(RealLauncher(started), _log.Add, newRequestId: () => RequestId(next++));
            try
            {
                GxEngineProcessRun first = Assert.NotNull((await pool.TryRunAsync(stub, Query(_directory), Budget, CancellationToken.None)).Run);
                GxEngineProcessRun second = Assert.NotNull((await pool.TryRunAsync(
                    stub, new[] { "--diagnostics=json", "kb-read", _directory, "PMissing" }, Budget, CancellationToken.None)).Run);

                Assert.Equal("{\"objects\":[{\"name\":\"Alpha\"}],\"total\":1}\n", first.StandardOutput);
                Assert.Equal(string.Empty, first.StandardError);
                Assert.Equal(0, first.ExitCode);
                Assert.Equal(string.Empty, second.StandardOutput);
                Assert.Equal("{\"category\":\"input\",\"message\":\"object 'PMissing' was not found\",\"location\":null}\n", second.StandardError);
                Assert.Equal(1, second.ExitCode);

                int processId = Assert.Single(started);
                Assert.Equal(1, pool.ProcessCount);
                using (var running = System.Diagnostics.Process.GetProcessById(processId))
                    Assert.False(running.HasExited, "the resident process serves both requests and stays");
                string[] requests = File.ReadAllLines(Path.Combine(_directory, "requests.txt"));
                Assert.Equal(2, requests.Length);
                Assert.Equal("kb-query", JObject.Parse(requests[0])["args"]![1]!.ToString());
                Assert.Equal(_directory, JObject.Parse(requests[1])["args"]![2]!.ToString());

                pool.Dispose();
                Assert.False(IsRunning(processId), "a resident process must not outlive the pool");
            }
            finally
            {
                pool.Dispose();
            }
        }

        [Fact]
        public async Task ARealChildProcess_IsEndedByTheIdleSweep()
        {
            string stub = CreateProtocolStub(("{}\n", "", 0));
            var clock = new ManualClock();
            var started = new List<int>();
            using var pool = new GxEngineResidentPool(RealLauncher(started), _log.Add, clock, newRequestId: () => RequestId(0));
            Assert.NotNull((await pool.TryRunAsync(stub, Query(_directory), Budget, CancellationToken.None)).Run);

            clock.Now += pool.IdleTimeout;
            Assert.Equal(1, pool.SweepIdle());

            Assert.False(IsRunning(Assert.Single(started)), "an idle resident process must be ended");
        }

        [Fact]
        public async Task ARealChildProcess_ThatEndsMidAnswer_IsHandedBackForARead()
        {
            // The stub answers one request; the second finds the process gone mid-exchange.
            string stub = Path.Combine(_directory, "engine.cmd");
            File.WriteAllText(Path.Combine(_directory, "ready.txt"), "{\"gxe\":\"serve-ready\",\"protocol\":1,\"version\":\"stub\"}\n");
            File.WriteAllText(Path.Combine(_directory, "half.out"), FakeResidentEngine.Begin(RequestId(0)) + "{\"objects\":[");
            File.WriteAllText(stub,
                "@echo off\r\ntype \"%~dp0ready.txt\"\r\nset /p line=\r\ntype \"%~dp0half.out\"\r\nexit /b 9\r\n", Encoding.ASCII);
            var started = new List<int>();
            using var pool = new GxEngineResidentPool(RealLauncher(started), _log.Add, newRequestId: () => RequestId(0));

            AssertNotServed(await pool.TryRunAsync(stub, Query(_directory), Budget, CancellationToken.None));

            Assert.False(IsRunning(Assert.Single(started)));
            Assert.Equal(0, pool.ProcessCount);
        }
    }
}
