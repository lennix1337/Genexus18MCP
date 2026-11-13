using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

/// <summary>
/// genexus_edit validationMode=native: the opt-in, budgeted, fail-soft engine diagnostics. The engine is
/// a .cmd stub that prints canned JSON, so the Gateway-to-process contract runs without cargo or a KB.
/// </summary>
public sealed class EngineNativeDiagnosticsTests : EngineRoutingTestBase
{
    private const string Ok = "{\"name\":\"PAlpha\",\"type\":\"Procedure\",\"status\":\"ok\",\"errors\":[]}";
    private const string Errors =
        "{\"name\":\"PAlpha\",\"type\":\"Procedure\",\"status\":\"errors\",\"errors\":[{\"line\":2,\"column\":9,\"message\":\"expected an expression\"}]}";

    private const string Args = "{\"name\":\"PAlpha\",\"part\":\"Source\",\"mode\":\"full\",\"content\":\"x\",\"validationMode\":\"native\"}";

    [Theory]
    [InlineData("genexus_edit", "native", true)]
    [InlineData("GENEXUS_EDIT", "NATIVE", true)]
    [InlineData("genexus_edit", "specify", false)]
    [InlineData("genexus_edit", null, false)]
    [InlineData("genexus_read", "native", false)]
    public void OnlyAnExplicitNativeEditRequestEngagesTheStep(string tool, string? mode, bool expected)
    {
        var args = new JObject { ["name"] = "P" };
        if (mode != null) args["validationMode"] = mode;
        Assert.Equal(expected, EngineRoutedOperations.WantsNativeDiagnostics(tool, args));
        Assert.False(EngineRoutedOperations.WantsNativeDiagnostics("genexus_edit", null));
    }

    [Fact]
    public async Task ACleanSource_ReportsOkWithSyntaxScope_AndPinsTheActiveVersion()
    {
        var run = await RunAsync(Args, Ok);
        Assert.Equal("ok", run.Block["status"]!.ToString());
        Assert.Equal("syntax", run.Block["scope"]!.ToString());
        Assert.Null(run.Block["errors"]);
        Assert.True(run.Block["elapsedMs"]!.Value<long>() >= 0);
        Assert.Contains("kb-diagnose", run.Arguments);
        Assert.Contains("PAlpha --design-model 4 --kb-version 2", run.Arguments);
    }

    [Fact]
    public async Task ASyntaxError_ReportsLineColumnAndMessage()
    {
        var run = await RunAsync(Args, Errors);
        Assert.Equal("errors", run.Block["status"]!.ToString());
        var error = (JObject)run.Block["errors"]![0]!;
        Assert.Equal(2, error["line"]!.Value<int>());
        Assert.Equal(9, error["column"]!.Value<int>());
        Assert.Equal("expected an expression", error["message"]!.ToString());
        Assert.Contains("validationMode=specify", run.Block["note"]!.ToString());
    }

    [Fact]
    public async Task ManyErrors_AreCappedAndFlagged()
    {
        var many = new JArray();
        for (int i = 1; i <= EngineRoutedOperations.NativeDiagnosticsMaxErrors + 3; i++)
            many.Add(new JObject { ["line"] = i, ["column"] = 1, ["message"] = new string('x', 900) });
        string json = new JObject
        {
            ["name"] = "PAlpha", ["type"] = "Procedure", ["status"] = "errors", ["errors"] = many
        }.ToString();
        var run = await RunAsync(Args, json);
        var errors = (JArray)run.Block["errors"]!;
        Assert.Equal(EngineRoutedOperations.NativeDiagnosticsMaxErrors, errors.Count);
        Assert.True(run.Block["truncated"]!.Value<bool>());
        Assert.True(errors[0]["message"]!.ToString().Length < 900);
    }

    [Fact]
    public async Task AnEngineThatCannotCheckTheObject_PassesItsReasonThrough()
    {
        string json = "{\"name\":\"PAlpha\",\"type\":\"WebPanel\",\"status\":\"unavailable\",\"reason\":\"native diagnostics cover Procedure source only\"}";
        var run = await RunAsync(Args, json);
        AssertUnavailable(run.Block, "Procedure source only");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"name\":\"Other\",\"type\":\"Procedure\",\"status\":\"ok\",\"errors\":[]}")]
    [InlineData("{\"name\":\"PAlpha\",\"type\":\"Procedure\",\"status\":\"ok\",\"errors\":[{\"message\":\"m\"}]}")]
    [InlineData("{\"name\":\"PAlpha\",\"type\":\"Procedure\",\"status\":\"errors\",\"errors\":[]}")]
    [InlineData("{\"name\":\"PAlpha\",\"type\":\"Procedure\",\"status\":\"errors\",\"errors\":[{\"line\":0,\"message\":\"m\"}]}")]
    [InlineData("{\"name\":\"PAlpha\",\"type\":\"Procedure\",\"status\":\"errors\",\"errors\":[{\"line\":1}]}")]
    [InlineData("{\"name\":\"PAlpha\",\"type\":\"Procedure\",\"status\":\"fine\",\"errors\":[]}")]
    public async Task AnAnswerOutsideTheContract_IsUnavailable_NeverAnInventedResult(string json)
    {
        var run = await RunAsync(Args, json);
        AssertUnavailable(run.Block, "not recognized");
    }

    [Fact]
    public async Task AnEngineRefusal_IsUnavailableWithItsExitCode()
    {
        var run = await RunAsync(Args, "", exitCode: 1, stderr: "object 'PAlpha' was not found in the active model");
        AssertUnavailable(run.Block, "exit 1");
        Assert.Contains("not found in the active model", run.Block["reason"]!.ToString());
    }

    [Fact]
    public async Task ASlowEngine_IsKilledAtTheBudget_AndReportsUnavailable_WithoutRetry()
    {
        var clock = Stopwatch.StartNew();
        var run = await RunAsync(Args, Ok, sleepSeconds: 20, budgetMs: 400);
        clock.Stop();
        AssertUnavailable(run.Block, "400 ms budget");
        Assert.True(clock.ElapsedMilliseconds < 10_000, $"returned after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(1, run.Invocations);
    }

    [Fact]
    public async Task ACancelledRequest_IsUnavailable_NotAnException()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var run = await RunAsync(Args, Ok, token: cancelled.Token);
        AssertUnavailable(run.Block, "cancelled");
    }

    [Fact]
    public async Task AMissingEngine_IsUnavailable()
    {
        var run = await RunAsync(Args, Ok, missingEngine: true);
        AssertUnavailable(run.Block, "not found");
    }

    [Fact]
    public async Task BackendSdk_NeverStartsTheEngine()
    {
        SetEngineEnv(EngineBackendSettings.BackendEnvVar, "sdk");
        var run = await RunAsync(Args, Ok);
        AssertUnavailable(run.Block, "Engine.Backend=sdk");
        Assert.Equal(0, run.Invocations);
    }

    [Theory]
    [InlineData("{\"name\":\"PAlpha\",\"validationMode\":\"native\",\"dryRun\":true}", "dryRun")]
    [InlineData("{\"targets\":[{\"name\":\"A\"}],\"validationMode\":\"native\"}", "single-object")]
    [InlineData("{\"changeSet\":{\"action\":\"apply\"},\"validationMode\":\"native\"}", "single-object")]
    [InlineData("{\"validationMode\":\"native\"}", "`name`")]
    [InlineData("{\"name\":\"--design-model\",\"validationMode\":\"native\"}", "`name`")]
    [InlineData("{\"name\":\"Ação\",\"validationMode\":\"native\"}", "`name`")]
    [InlineData("{\"name\":\"PAlpha\",\"validationMode\":\"native\",\"kb\":\"other\"}", "different Knowledge Base")]
    public async Task UnsupportedRequests_AreUnavailable_WithoutStartingTheEngine(string args, string reason)
    {
        var run = await RunAsync(args, Ok);
        AssertUnavailable(run.Block, reason);
        Assert.Equal(0, run.Invocations);
    }

    [Fact]
    public async Task AKbThatIsNotANativeSqlKb_IsUnavailable_WithoutStartingTheEngine()
    {
        var run = await RunAsync(Args, Ok, nativeKb: false);
        AssertUnavailable(run.Block, "knowledgebase.connection");
        Assert.Equal(0, run.Invocations);
    }

    [Fact]
    public async Task AFailingActiveModelLookup_StillYieldsABlock_NeverAnException()
    {
        var run = await RunAsync(Args, Ok, activeModelThrows: true);
        // The snapshot is optional (the engine then serves a KB without versions); the call still completes.
        Assert.Contains(run.Block["status"]!.ToString(), new[] { "ok", "unavailable" });
    }

    [Fact]
    public async Task Attach_LeavesAnUnrequestedOrFailedEditUntouched_AndStartsNoEngine()
    {
        string directory = NewTempDir();
        try
        {
            string marker = Path.Combine(directory, "ran.txt");
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, CreateStub(directory, "engine.cmd",
                $"@echo off\r\necho x > \"{marker}\"\r\nexit /b 0\r\n"));
            JObject Success() => Result("{\"status\":\"success\",\"name\":\"PAlpha\"}");
            JObject unrequested = Success();
            await Program.AttachNativeDiagnosticsAsync(unrequested, "genexus_edit", JObject.Parse("{\"name\":\"PAlpha\"}"), CancellationToken.None);
            JObject specify = Success();
            await Program.AttachNativeDiagnosticsAsync(specify, "genexus_edit", JObject.Parse("{\"name\":\"PAlpha\",\"validationMode\":\"specify\"}"), CancellationToken.None);
            JObject failed = Result("{\"error\":\"boom\"}", isError: true);
            await Program.AttachNativeDiagnosticsAsync(failed, "genexus_edit", JObject.Parse(Args), CancellationToken.None);
            await Program.AttachNativeDiagnosticsAsync(null, "genexus_edit", JObject.Parse(Args), CancellationToken.None);

            Assert.True(JToken.DeepEquals(Success(), unrequested));
            Assert.True(JToken.DeepEquals(Success(), specify));
            Assert.Equal("{\"error\":\"boom\"}", failed["content"]![0]!["text"]!.ToString());
            Assert.False(File.Exists(marker));
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public async Task Attach_FoldsTheBlockIntoTextAndStructuredContent_WithoutChangingTheEdit()
    {
        JObject result = Result("{\"status\":\"success\",\"name\":\"PAlpha\",\"bytes\":12}");
        result["structuredContent"] = JObject.Parse("{\"status\":\"success\"}");
        // No Knowledge Base is open in the test process: the block is "unavailable", and still folded.
        await Program.AttachNativeDiagnosticsAsync(result, "genexus_edit", JObject.Parse(Args), CancellationToken.None);

        JObject payload = JObject.Parse(result["content"]![0]!["text"]!.ToString());
        Assert.Equal("success", payload["status"]!.ToString());
        Assert.Equal(12, payload["bytes"]!.Value<int>());
        Assert.Equal("unavailable", payload["nativeDiagnostics"]!["status"]!.ToString());
        Assert.NotNull(payload["nativeDiagnostics"]!["reason"]);
        Assert.Equal("unavailable", result["structuredContent"]!["nativeDiagnostics"]!["status"]!.ToString());
        Assert.False(result["isError"]!.Value<bool>());
    }

    [Fact]
    public async Task Attach_ANonSuccessEditResult_IsAnnotatedUnavailable_WithoutDiagnosing()
    {
        JObject result = Result("{\"status\":\"NoChange\",\"name\":\"PAlpha\"}");
        await Program.AttachNativeDiagnosticsAsync(result, "genexus_edit", JObject.Parse(Args), CancellationToken.None);
        JObject payload = JObject.Parse(result["content"]![0]!["text"]!.ToString());
        AssertUnavailable((JObject)payload["nativeDiagnostics"]!, "plain success");
    }

    [Fact]
    public async Task Attach_AnUnparsableEditText_IsLeftAsIs()
    {
        JObject result = Result("<<not json>>");
        await Program.AttachNativeDiagnosticsAsync(result, "genexus_edit", JObject.Parse(Args), CancellationToken.None);
        Assert.Equal("<<not json>>", result["content"]![0]!["text"]!.ToString());
    }

    private static JObject Result(string text, bool isError = false) => new()
    {
        ["isError"] = isError,
        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text })
    };

    private static void AssertUnavailable(JObject block, string reasonFragment)
    {
        Assert.Equal("unavailable", block["status"]!.ToString());
        Assert.Contains(reasonFragment, block["reason"]!.ToString());
        Assert.Null(block["errors"]);
    }

    private sealed record Run(JObject Block, string Arguments, int Invocations);

    private async Task<Run> RunAsync(
        string args, string stdout, int exitCode = 0, string stderr = "", int sleepSeconds = 0, int budgetMs = 5000,
        bool nativeKb = true, bool missingEngine = false, bool activeModelThrows = false, CancellationToken token = default)
    {
        string directory = NewTempDir();
        try
        {
            if (nativeKb) File.WriteAllText(Path.Combine(directory, "knowledgebase.connection"), "");
            File.WriteAllText(Path.Combine(directory, "stdout.json"), stdout);
            File.WriteAllText(Path.Combine(directory, "stderr.txt"), stderr);
            string sleep = sleepSeconds > 0 ? $"ping -n {sleepSeconds + 1} 127.0.0.1 >nul\r\n" : "";
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, missingEngine
                ? Path.Combine(directory, "no-such-engine.exe")
                : CreateStub(directory, "engine.cmd",
                    "@echo off\r\necho %* >> \"%~dp0args.txt\"\r\n" + sleep
                    + "type \"%~dp0stdout.json\"\r\ntype \"%~dp0stderr.txt\" 1>&2\r\nexit /b " + exitCode + "\r\n"));
            var operations = new EngineRoutedOperations(() => null, _ => { },
                (payload, error, _, _) => new JObject { ["isError"] = error },
                (kb, _) => activeModelThrows
                    ? throw new InvalidOperationException("worker busy")
                    : Task.FromResult<EngineActiveModel?>(new EngineActiveModel(kb, 4, 2, "branch")));
            JObject block = await operations.DiagnoseSavedObjectAsync(JObject.Parse(args), directory, "kb-alias", budgetMs, token);
            string argumentFile = Path.Combine(directory, "args.txt");
            string text = File.Exists(argumentFile) ? File.ReadAllText(argumentFile) : "";
            return new Run(block, text, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally { TryDeleteDirectory(directory); }
    }
}
