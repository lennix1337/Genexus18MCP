using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// GXMCP_ENGINE_RESIDENT at the Gateway's engine entry point (GxEngineClient.InvokeAsync)
    /// and through the routed operations: off, nothing changes; on, an answer is the answer
    /// the one-shot process gives; a read the resident process fails runs as a one-shot
    /// process; a build that was sent is never run again.
    ///
    /// The one-shot engine is a .cmd stub that records each start in <c>starts.txt</c> and
    /// prints prepared files; the resident engine is the in-memory fake.
    /// </summary>
    public sealed class EngineResidentRoutingTests : EngineRoutingTestBase, IDisposable
    {
        private const string QueryAnswer =
            "{\"objects\":[{\"name\":\"Alpha\",\"type\":\"Procedure\"},{\"name\":\"Beta\",\"type\":\"Procedure\"}],\"total\":2,\"truncated\":false}\n";
        private const string BuildReport =
            "{\"catalog_load_ms\":5,\"read_ms\":1,\"spec_ms\":2,\"emit_ms\":3,\"compile_ms\":4,\"total_ms\":10,\"procedures_count\":1,\"transactions_count\":1,\"sdts_count\":0,\"webpanels_count\":1,\"total_objects\":3,\"errors\":[],\"warnings\":[],\"emitted_files\":[\"a.cs\",\"b.cs\"]}\n";
        private const string QueryArgs = @"{ ""query"": ""type:Procedure"", ""limit"": 10, ""fields"": [""name"", ""type""] }";
        private const string BuildArgs = @"{ ""action"": ""build"", ""wait_until_done"": true }";

        private readonly string _directory;
        private readonly List<string> _log = new();

        public EngineResidentRoutingTests()
        {
            _directory = NewTempDir();
            SetEngineEnv(GxEngineResidentPool.EnabledEnvVar, null);
        }

        public new void Dispose()
        {
            GxEngineResidentPool.SharedOverrideForTest?.Dispose();
            GxEngineResidentPool.SharedOverrideForTest = null;
            base.Dispose();
            TryDeleteDirectory(_directory);
        }

        /// <summary>The one-shot engine: records each start and prints the prepared answer.</summary>
        private void UseOneShotStub(string stdout, string stderr = "", int exitCode = 0)
        {
            File.WriteAllText(Path.Combine(_directory, "stdout.txt"), stdout);
            File.WriteAllText(Path.Combine(_directory, "stderr.txt"), stderr);
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(_directory, "engine.cmd",
                "@echo off\r\necho %* >> \"%~dp0starts.txt\"\r\n"
                + "type \"%~dp0stdout.txt\"\r\ntype \"%~dp0stderr.txt\" 1>&2\r\nexit /b " + exitCode + "\r\n"));
        }

        /// <summary>The command line of every one-shot process that started.</summary>
        private string[] OneShotStarts()
        {
            string path = Path.Combine(_directory, "starts.txt");
            return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        }

        private FakeResidentLauncher UseResident(
            Func<int, FakeResidentEngine>? create = null, bool enabled = true, TimeSpan? requestTimeout = null)
        {
            var launcher = new FakeResidentLauncher(create);
            GxEngineResidentPool.SharedOverrideForTest = new GxEngineResidentPool(
                launcher.Launch, _log.Add, requestTimeout: requestTimeout, handshakeTimeout: TimeSpan.FromMilliseconds(200));
            if (enabled) SetEngineEnv(GxEngineResidentPool.EnabledEnvVar, "1");
            return launcher;
        }

        private static FakeResidentEngine Answering(string stdout, string stderr = "", int exit = 0) =>
            new((engine, id, _) => engine.Answer(id, stdout, stderr, exit));

        private static FakeResidentEngine Ending() => new((engine, _, _) => engine.Exit());

        private static FakeResidentEngine Silent() => new((_, _, _) => { });

        private Task<GxEngineInvocation> InvokeAsync(params string[] arguments) =>
            GxEngineClient.InvokeAsync(EngineBackendSettings.Resolve(null), arguments, CancellationToken.None);

        private string[] KbQuery => new[] { "--diagnostics=json", "kb-query", _directory, "--type", "Procedure" };

        // ------------------------------------------------- opt-in off: untouched

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("yes")]
        public async Task WithoutTheOptIn_TheResidentPoolIsNeverConsulted_AndEveryCallStartsItsOwnProcess(string? value)
        {
            UseOneShotStub(QueryAnswer);
            FakeResidentLauncher launcher = UseResident(enabled: false);
            SetEngineEnv(GxEngineResidentPool.EnabledEnvVar, value);

            Assert.Null(GxEngineResidentPool.Current);
            GxEngineInvocation first = await InvokeAsync(KbQuery);
            GxEngineInvocation second = await InvokeAsync(KbQuery);

            Assert.True(first.Ok && second.Ok);
            Assert.Equal(QueryAnswer, first.StandardOutput);
            Assert.Empty(launcher.Executables);
            string[] starts = OneShotStarts();
            Assert.Equal(2, starts.Length);
            Assert.All(starts, start => Assert.Contains("kb-query", start));
        }

        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData(" TRUE ")]
        public void TheOptIn_IsOneOrTrue(string value)
        {
            SetEngineEnv(GxEngineResidentPool.EnabledEnvVar, value);
            Assert.True(GxEngineResidentPool.Enabled);
            Assert.Same(GxEngineResidentPool.Shared, GxEngineResidentPool.Current);
        }

        // ------------------------------------ same answer through both paths

        [Theory]
        [InlineData(QueryAnswer, "", 0)]
        [InlineData("", "{\"category\":\"input\",\"message\":\"object 'PAlpha' was not found in the active model\",\"location\":null}\n", 1)]
        [InlineData("", "{\"category\":\"version_scope\",\"message\":\"model 9 is not a Design model\",\"location\":null}\n", 4)]
        [InlineData("", "{\"category\":\"sql_credentials_unavailable\",\"message\":\"SQL authentication requires a user\",\"location\":null}\n", 5)]
        [InlineData(BuildReport, "compile path: csc-direct\n", 3)]
        [InlineData("no final line break", "warning without line break", 0)]
        public async Task TheSameEngineOutput_GivesTheSameInvocation_OneShotAndResident(string stdout, string stderr, int exit)
        {
            UseOneShotStub(stdout, stderr, exit);
            GxEngineInvocation oneShot = await InvokeAsync(KbQuery);
            Assert.Single(OneShotStarts());

            FakeResidentLauncher launcher = UseResident(_ => Answering(stdout, stderr, exit));
            GxEngineInvocation resident = await InvokeAsync(KbQuery);

            Assert.Single(launcher.Engines);
            Assert.Single(OneShotStarts());
            Assert.Equal(exit == 0, resident.Ok);
            Assert.Equal(oneShot.Ok, resident.Ok);
            Assert.Equal(oneShot.Executable, resident.Executable);
            Assert.Equal(oneShot.StandardOutput, resident.StandardOutput);
            Assert.Equal(oneShot.StandardError, resident.StandardError);
            Assert.Equal(oneShot.ExitCode, resident.ExitCode);
            Assert.Equal(oneShot.TimedOut, resident.TimedOut);
            Assert.Equal(oneShot.ErrorCode, resident.ErrorCode);
            Assert.Equal(oneShot.ErrorMessage, resident.ErrorMessage);
            Assert.Equal(stdout, resident.StandardOutput);
            // A successful invocation carries no stderr on either path.
            Assert.Equal(exit == 0 ? string.Empty : stderr, resident.StandardError);
        }

        [Fact]
        public async Task ARoutedQuery_IsTheSameToolResult_OneShotAndResident()
        {
            UseOneShotStub(QueryAnswer);
            JObject oneShot = (await DispatchAsync("genexus_query", null, QueryArgs, _directory))!;
            Assert.Single(OneShotStarts());

            FakeResidentLauncher launcher = UseResident(_ => Answering(QueryAnswer));
            JObject resident = (await DispatchAsync("genexus_query", null, QueryArgs, _directory))!;

            Assert.Single(OneShotStarts());
            Assert.Equal(2, ((JArray)PayloadOf(resident)["results"]!).Count);
            Assert.True(JToken.DeepEquals(oneShot, resident), $"one-shot: {oneShot}\nresident: {resident}");
            string[] sent = JObject.Parse(Assert.Single(launcher.Engines[0].RequestLines))["args"]!.Select(a => a.ToString()).ToArray();
            Assert.Equal(new[] { "--diagnostics=json", "kb-query", _directory, "--type", "Procedure", "--limit", "10" }, sent);
        }

        // --------------------------------------------- reads fall back to one-shot

        [Fact]
        public async Task AReadWhoseResidentProcessEnds_RunsAsAOneShotProcess()
        {
            UseOneShotStub(QueryAnswer);
            FakeResidentLauncher launcher = UseResident(_ => Ending());

            JObject result = (await DispatchAsync("genexus_query", null, QueryArgs, _directory))!;

            Assert.False(result["isError"]!.Value<bool>());
            Assert.Equal(2, ((JArray)PayloadOf(result)["results"]!).Count);
            Assert.Single(launcher.Engines[0].RequestLines);
            Assert.True(launcher.Engines[0].KillCount >= 1);
            Assert.Contains("kb-query", Assert.Single(OneShotStarts()));
        }

        [Fact]
        public async Task AReadWhoseResidentProcessExceedsTheBudget_RunsAsAOneShotProcess()
        {
            UseOneShotStub(QueryAnswer);
            FakeResidentLauncher launcher = UseResident(_ => Silent(), requestTimeout: TimeSpan.FromMilliseconds(100));

            JObject result = (await DispatchAsync("genexus_query", null, QueryArgs, _directory))!;

            Assert.False(result["isError"]!.Value<bool>());
            Assert.True(launcher.Engines[0].KillCount >= 1);
            Assert.Contains("kb-query", Assert.Single(OneShotStarts()));
        }

        [Fact]
        public async Task AnEngineWithoutResidentMode_IsAskedOnce_AndEveryCallStillRunsAsAOneShotProcess()
        {
            // A real child: this stub answers `serve` like any other command and exits.
            UseOneShotStub(QueryAnswer);
            GxEngineResidentPool.SharedOverrideForTest = new GxEngineResidentPool(GxEngineResidentProcess.Start, _log.Add);
            SetEngineEnv(GxEngineResidentPool.EnabledEnvVar, "1");

            GxEngineInvocation first = await InvokeAsync(KbQuery);
            GxEngineInvocation second = await InvokeAsync(KbQuery);

            Assert.True(first.Ok && second.Ok);
            Assert.Equal(QueryAnswer, first.StandardOutput);
            Assert.Equal(QueryAnswer, second.StandardOutput);
            string[] starts = OneShotStarts().Select(start => start.Trim()).ToArray();
            Assert.Equal(3, starts.Length);
            Assert.Equal("serve", starts[0]);
            Assert.All(starts.Skip(1), start => Assert.Contains("kb-query", start));
            Assert.Contains(_log, line => line.Contains("resident mode is not available"));
        }

        // ------------------------------------------------ builds: never replayed

        [Fact]
        public async Task ABuildAnsweredByTheResidentProcess_IsServed_AndNoOtherProcessStarts()
        {
            UseOneShotStub(BuildReport);
            FakeResidentLauncher launcher = UseResident(_ => Answering(BuildReport));

            JObject result = (await DispatchAsync("genexus_lifecycle", "build", BuildArgs, _directory))!;

            Assert.False(result["isError"]!.Value<bool>());
            Assert.Equal("Success", PayloadOf(result)["status"]!.ToString());
            Assert.Empty(OneShotStarts());
            JArray sent = (JArray)JObject.Parse(Assert.Single(launcher.Engines[0].RequestLines))["args"]!;
            Assert.Equal("rebuild-all", sent[1]!.ToString());
        }

        [Theory]
        [InlineData("sdk")]
        [InlineData("error")]
        public async Task ABuildWhoseResidentProcessEnds_RequiresReconciliation_AndIsNotRunAgain(string fallback)
        {
            SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
            UseOneShotStub(BuildReport);
            FakeResidentLauncher launcher = UseResident(_ => Ending());

            JObject? result = await DispatchAsync("genexus_lifecycle", "build", BuildArgs, _directory);

            // Not null: a null result would hand the build to the SDK path.
            Assert.NotNull(result);
            Assert.True(result!["isError"]!.Value<bool>());
            JObject payload = PayloadOf(result);
            Assert.Equal("EngineObservationFailed", payload["error"]!.ToString());
            Assert.True(payload["reconciliationRequired"]!.Value<bool>());
            Assert.True(payload["mutationOutcomeUnknown"]!.Value<bool>());
            Assert.Contains("completion was not observed", payload["message"]!.ToString());
            Assert.Single(launcher.Engines);
            Assert.Single(launcher.Engines[0].RequestLines);
            Assert.Empty(OneShotStarts());
        }

        [Fact]
        public async Task ABuildWhoseResidentProcessExceedsTheBudget_RequiresReconciliation_AndIsNotRunAgain()
        {
            UseOneShotStub(BuildReport);
            FakeResidentLauncher launcher = UseResident(_ => Silent(), requestTimeout: TimeSpan.FromMilliseconds(100));

            JObject? result = await DispatchAsync("genexus_lifecycle", "build", BuildArgs, _directory);

            Assert.NotNull(result);
            JObject payload = PayloadOf(result!);
            Assert.Equal("EngineTimeout", payload["error"]!.ToString());
            Assert.True(payload["reconciliationRequired"]!.Value<bool>());
            Assert.True(payload["engine"]!["timedOut"]!.Value<bool>());
            Assert.True(launcher.Engines[0].KillCount >= 1);
            Assert.Empty(OneShotStarts());
        }

        [Fact]
        public async Task ABuildWhoseResidentProcessNeverBecameReady_RunsAsAOneShotProcess_BecauseNothingWasSent()
        {
            UseOneShotStub(BuildReport);
            FakeResidentLauncher launcher = UseResident(_ => new FakeResidentEngine(ready: false));

            JObject result = (await DispatchAsync("genexus_lifecycle", "build", BuildArgs, _directory))!;

            Assert.False(result["isError"]!.Value<bool>());
            Assert.Equal("Success", PayloadOf(result)["status"]!.ToString());
            Assert.Empty(launcher.Engines[0].RequestLines);
            Assert.Contains("rebuild-all", Assert.Single(OneShotStarts()));
        }
    }
}
