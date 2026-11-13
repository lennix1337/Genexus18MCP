using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Issue #593: on a large native GeneXus 18 Knowledge Base, `genexus_wire
    // action=status` and `action=index` answered only `Internal error` - no code,
    // no cause - while `action=doctor` worked and reported cache_exists=false.
    // Every failure path must now return a coded envelope with a
    // diagnosticContext, `status` must say when there is no index instead of
    // walking the tree, and a native KB root must be refused before any process
    // is launched, with the text-tree export named as the way forward.
    //
    // These tests never need gxwire.exe: the locator, launcher and cache path
    // are replaced through GxwireTool.GxwireToolSeams.
    public class GxwireErrorEnvelopeTests
    {
        private const string FakeExe = @"C:\fake\gxwire.exe";

        private sealed class Fixture : IDisposable
        {
            internal TempDir Tree { get; } = new TempDir();
            internal TempDir CacheRoot { get; } = new TempDir();
            internal string CachePath => Path.Combine(CacheRoot.Path, "index.bin");
            internal bool Launched { get; private set; }
            internal string? LaunchedArguments { get; private set; }
            internal IReadOnlyList<string>? LaunchedArgumentList { get; private set; }
            internal int? LaunchedTimeoutSeconds { get; private set; }

            internal Func<string, IReadOnlyList<string>, int, Task<GxwireProcessRun>> Launcher { get; set; } =
                (_, _, _) => Task.FromResult(new GxwireProcessRun("<status schema=\"gxwire.status/v1\" empty=\"true\"/>", "", false, 30, 0, 12));

            internal Func<string?> Locator { get; set; } = () => FakeExe;

            internal GxwireTool.GxwireToolSeams Seams => new GxwireTool.GxwireToolSeams
            {
                LocateExecutable = Locator,
                ResolveCachePath = _ => CachePath,
                Launch = (exe, args, timeout) =>
                {
                    Launched = true;
                    LaunchedArguments = string.Join(" ", args);
                    LaunchedArgumentList = args;
                    LaunchedTimeoutSeconds = timeout;
                    return Launcher(exe, args, timeout);
                }
            };

            internal void WriteIndex() => File.WriteAllText(CachePath, "cache");

            internal void MakeNativeKbRoot()
            {
                File.WriteAllText(Path.Combine(Tree.Path, "GXLOCK"), "");
                File.WriteAllText(Path.Combine(Tree.Path, "SampleKb.gxw"), "");
            }

            internal void MakeTextTree()
            {
                File.WriteAllText(Path.Combine(Tree.Path, "PInvoice.txt"), "Call(PProcessTax)\n");
            }

            public void Dispose()
            {
                Tree.Dispose();
                CacheRoot.Dispose();
            }
        }

        private static JObject Args(string action, string dir, params (string Key, string Value)[] extra)
        {
            var args = new JObject { ["action"] = action, ["dir"] = dir };
            foreach (var (key, value) in extra) args[key] = value;
            return args;
        }

        private static Task<JObject> Run(Fixture fx, JObject args) =>
            Program.ExecuteGxwireToolAsyncForTest(args, fx.Seams);

        private static void AssertCodedError(JObject result, string code)
        {
            Assert.Equal("error", result["status"]?.ToString());
            Assert.Equal(code, result["error"]?.ToString());
            Assert.Equal(code, result["code"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(result["message"]?.ToString()), "every error carries a human message");
            Assert.NotNull(result["diagnosticContext"] as JObject);
        }

        // --- timeout -------------------------------------------------------------

        [Fact]
        public async Task Timeout_ReportsCodeBudgetElapsedAndContext()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            fx.Launcher = (_, _, timeout) => Task.FromResult(
                new GxwireProcessRun("", "", timedOut: true, timeoutSeconds: timeout, exitCode: null, elapsedMs: 30_012));

            var result = await Run(fx, Args("callers", fx.Tree.Path, ("target", "PInvoice")));

            AssertCodedError(result, GxwireErrorCodes.Timeout);
            Assert.Equal(30, result["timeoutSeconds"]?.Value<int>());
            Assert.Equal(30_012, result["elapsedMs"]?.Value<long>());
            Assert.Contains(GxwireProcessRunner.TimeoutEnvVar, result["hint"]?.ToString());

            var ctx = (JObject)result["diagnosticContext"]!;
            Assert.Equal("callers", ctx["action"]?.ToString());
            Assert.Equal(fx.Tree.Path, ctx["kbRoot"]?.ToString());
            Assert.Equal(fx.CachePath, ctx["cachePath"]?.ToString());
            Assert.Equal(30, ctx["timeoutSeconds"]?.Value<int>());
            Assert.Equal(30_012, ctx["elapsedMs"]?.Value<long>());
            Assert.Null(ctx["exitCode"]);
        }

        [Fact]
        public async Task IndexTimeout_PointsAtAManualRunAndTheBudgetVariable()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.Launcher = (_, _, timeout) => Task.FromResult(
                new GxwireProcessRun("", "[gxwire] Building index from: tree", timedOut: true, timeoutSeconds: timeout, exitCode: null, elapsedMs: 900_000));

            var result = await Run(fx, Args("index", fx.Tree.Path));

            AssertCodedError(result, GxwireErrorCodes.Timeout);
            Assert.Equal(GxwireProcessRunner.DefaultIndexTimeoutSeconds, fx.LaunchedTimeoutSeconds);
            string hint = result["hint"]?.ToString() ?? "";
            Assert.Contains("gxwire.exe --index", hint);
            Assert.Contains("--cache-path", hint);
            Assert.Contains(GxwireProcessRunner.TimeoutEnvVar, hint);
            Assert.Contains("--index", result["next"]?.ToString());
        }

        [Fact]
        public async Task QueryTimeout_WithoutIndex_TellsTheCallerToIndexFirst()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            // No index on disk: gxwire parsed the whole tree inside the query budget.
            fx.Launcher = (_, _, timeout) => Task.FromResult(
                new GxwireProcessRun("", "", timedOut: true, timeoutSeconds: timeout, exitCode: null, elapsedMs: 30_000));

            var result = await Run(fx, Args("impact", fx.Tree.Path, ("target", "PInvoice")));

            AssertCodedError(result, GxwireErrorCodes.Timeout);
            Assert.Contains("action=index", result["hint"]?.ToString());
            Assert.False(result["diagnosticContext"]!["cacheExists"]!.Value<bool>());
        }

        // --- non-zero exit -------------------------------------------------------

        [Fact]
        public async Task NonZeroExit_ReportsExitCodeAndBoundedStderrTail()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            string noise = new string('x', 10_000) + "\nthread 'main' panicked at FINAL-LINE";
            fx.Launcher = (_, _, _) => Task.FromResult(
                new GxwireProcessRun("", noise, false, 30, exitCode: 101, elapsedMs: 250));

            var result = await Run(fx, Args("callers", fx.Tree.Path, ("target", "PInvoice")));

            AssertCodedError(result, GxwireErrorCodes.ExecutionFailed);
            Assert.Equal(101, result["exitCode"]?.Value<int>());
            string stderr = result["stderr"]?.ToString() ?? "";
            Assert.EndsWith("FINAL-LINE", stderr);
            Assert.True(stderr.Length <= GxwireTool.GxwireStderrTailChars + 1, $"stderr must be bounded, got {stderr.Length} chars");

            var ctx = (JObject)result["diagnosticContext"]!;
            Assert.Equal(101, ctx["exitCode"]?.Value<int>());
            Assert.Equal(250, ctx["elapsedMs"]?.Value<long>());
            Assert.True((ctx["stderrTail"]?.ToString() ?? "").Length <= GxwireTool.GxwireStderrTailChars + 1);
            Assert.Null(result["engineCode"]);
        }

        [Fact]
        public async Task EngineRefusal_SurfacesTheEngineCode()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            fx.Launcher = (_, _, _) => Task.FromResult(new GxwireProcessRun(
                "",
                "<error code=\"SymbolNotFound\" target=\"Nope\" message=\"not in graph\"/>",
                false, 30, exitCode: 1, elapsedMs: 5));

            var result = await Run(fx, Args("callers", fx.Tree.Path, ("target", "Nope")));

            AssertCodedError(result, GxwireErrorCodes.ExecutionFailed);
            Assert.Equal("SymbolNotFound", result["engineCode"]?.ToString());
            Assert.Contains("SymbolNotFound", result["message"]?.ToString());
        }

        [Theory]
        [InlineData("<error code=\"SymbolNotFound\" target=\"X\"/>", "SymbolNotFound")]
        [InlineData("{\n  \"error\": {\n    \"code\": \"SymbolNotFound\",\n    \"target\": \"X\"\n  }\n}", "SymbolNotFound")]
        [InlineData("schema: gxwire.error/v1\ncode: SymbolNotFound\ntarget: X\n", "SymbolNotFound")]
        [InlineData("plain text without a code", null)]
        [InlineData("", null)]
        public void EngineCode_IsLiftedFromEveryOutputFormat(string text, string? expected)
        {
            Assert.Equal(expected, GxwireTool.ExtractGxwireEngineCode(text));
        }

        // --- status without an index ----------------------------------------------

        [Fact]
        public async Task Status_WithoutIndex_ReportsCacheMissing_AndNeverLaunches()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();

            var result = await Run(fx, Args("status", fx.Tree.Path));

            AssertCodedError(result, GxwireErrorCodes.CacheMissing);
            Assert.False(fx.Launched, "status must not walk the tree when there is no index");
            Assert.Contains("action=index", result["hint"]?.ToString());
            Assert.Contains("action=index", result["next"]?.ToString());
            var ctx = (JObject)result["diagnosticContext"]!;
            Assert.False(ctx["cacheExists"]!.Value<bool>());
            Assert.Equal(fx.CachePath, ctx["cachePath"]?.ToString());
        }

        [Fact]
        public async Task Status_WithIndex_Launches()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();

            var result = await Run(fx, Args("status", fx.Tree.Path));

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.True(fx.Launched);
            Assert.Contains("--cache-path", fx.LaunchedArguments);
        }

        // --- native KB root ------------------------------------------------------

        [Theory]
        [InlineData("index")]
        [InlineData("status")]
        [InlineData("pack_task")]
        public async Task NativeKbRoot_IsRefusedBeforeLaunch_WithTextTreeExportGuidance(string action)
        {
            using var fx = new Fixture();
            fx.MakeNativeKbRoot();
            var args = Args(action, fx.Tree.Path, ("task", "orient"));

            var result = await Run(fx, args);

            AssertCodedError(result, GxwireErrorCodes.RequiresTextSourceTree);
            Assert.False(fx.Launched, "a native KB root must be refused before gxwire exists");
            string hint = result["hint"]?.ToString() ?? "";
            Assert.Contains("genexus_io", hint);
            Assert.Contains("export_kb_to_text", hint);
            Assert.Contains("action=index", hint);
            Assert.Equal("genexus_io", result["textTreeExport"]?["tool"]?.ToString());
            Assert.Equal("export_kb_to_text", result["textTreeExport"]?["action"]?.ToString());
            Assert.Equal("native", result["textTreeExport"]?["args"]?["format"]?.ToString());
            Assert.NotEmpty((JArray)result["evidence"]!);
            Assert.Equal(fx.Tree.Path, result["diagnosticContext"]?["kbRoot"]?.ToString());
        }

        [Fact]
        public async Task Doctor_OnNativeKbRoot_RunsButSaysNotToIndexIt()
        {
            using var fx = new Fixture();
            fx.MakeNativeKbRoot();
            fx.Launcher = (_, _, _) => Task.FromResult(new GxwireProcessRun(
                "<doctor schema=\"gxwire.doctor/v1\" status=\"ready\" cache_exists=\"false\"/>", "", false, 30, 0, 3));

            var result = await Run(fx, Args("doctor", fx.Tree.Path));

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.True(fx.Launched);
            Assert.True(result["textSourceTreeRequired"]?.Value<bool>());
            Assert.Contains("export_kb_to_text", result["hint"]?.ToString());
            Assert.Equal("genexus_io", result["textTreeExport"]?["tool"]?.ToString());
        }

        [Fact]
        public async Task Doctor_OnTextTree_CarriesNoNativeKbWarning()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.Launcher = (_, _, _) => Task.FromResult(new GxwireProcessRun(
                "<doctor schema=\"gxwire.doctor/v1\" status=\"ready\" cache_exists=\"false\"/>", "", false, 30, 0, 3));

            var result = await Run(fx, Args("doctor", fx.Tree.Path));

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.Null(result["textSourceTreeRequired"]);
        }

        // --- index on a text tree ------------------------------------------------

        [Fact]
        public async Task Index_OnTextTree_ReportsWhereTheIndexLandedAndItsSize()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.Launcher = (_, _, _) =>
            {
                fx.WriteIndex();
                return Task.FromResult(new GxwireProcessRun(
                    "",
                    "[gxwire] Building index from: tree\n[gxwire] Index built successfully! Nodes: 7, Cache: index.bin\n",
                    false, 900, 0, 1_234));
            };

            var result = await Run(fx, Args("index", fx.Tree.Path));

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.Equal(fx.CachePath, result["cachePath"]?.ToString());
            Assert.True(result["cacheExists"]?.Value<bool>());
            Assert.Equal(7, result["nodes"]?.Value<int>());
            Assert.Equal(1_234, result["elapsedMs"]?.Value<long>());
            Assert.Contains("Index built successfully", result["summary"]?.ToString());
            Assert.Null(result["warning"]);
        }

        [Fact]
        public async Task Index_ThatWritesNoFile_IsFlagged()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.Launcher = (_, _, _) => Task.FromResult(new GxwireProcessRun("", "", false, 900, 0, 10));

            var result = await Run(fx, Args("index", fx.Tree.Path));

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.False(result["cacheExists"]?.Value<bool>());
            Assert.Contains(GxwireErrorCodes.CacheMissing, result["warning"]?.ToString());
        }

        // --- unparsable / empty output ---------------------------------------------

        [Fact]
        public async Task JsonFormat_WithNonJsonOutput_ReportsStructuredCode()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            fx.Launcher = (_, _, _) => Task.FromResult(new GxwireProcessRun("this is not json", "", false, 30, 0, 4));

            var result = await Run(fx, Args("callers", fx.Tree.Path, ("target", "PInvoice"), ("format", "json")));

            AssertCodedError(result, GxwireErrorCodes.OutputUnparsable);
            Assert.Contains("this is not json", result["diagnosticContext"]?["stdoutHead"]?.ToString());
        }

        [Fact]
        public async Task EmptyOutput_ForAQueryAction_ReportsStructuredCode()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            fx.Launcher = (_, _, _) => Task.FromResult(new GxwireProcessRun("", "", false, 30, 0, 4));

            var result = await Run(fx, Args("callers", fx.Tree.Path, ("target", "PInvoice")));

            AssertCodedError(result, GxwireErrorCodes.OutputUnparsable);
            Assert.Equal(0, result["diagnosticContext"]?["exitCode"]?.Value<int>());
        }

        // --- binary / launcher / unexpected ----------------------------------------

        [Fact]
        public async Task MissingBinary_ReportsCodeAndContext()
        {
            using var fx = new Fixture();
            fx.Locator = () => null;

            var result = await Run(fx, Args("status", fx.Tree.Path));

            AssertCodedError(result, GxwireErrorCodes.BinaryNotFound);
            Assert.False(fx.Launched);
            Assert.Equal("status", result["diagnosticContext"]?["action"]?.ToString());
        }

        [Fact]
        public async Task LauncherThatThrows_IsReportedAsProcessStartFailed()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            fx.Launcher = (_, _, _) => throw new System.ComponentModel.Win32Exception(5, "Access is denied");

            var result = await Run(fx, Args("status", fx.Tree.Path));

            AssertCodedError(result, GxwireErrorCodes.ProcessStartFailed);
            Assert.Contains("Access is denied", result["message"]?.ToString());
            Assert.Equal("Win32Exception", result["diagnosticContext"]?["exceptionType"]?.ToString());
        }

        [Fact]
        public async Task UnexpectedException_IsAnEnvelope_NotAnEscapedThrow()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.Locator = () => throw new InvalidOperationException("locator exploded");

            // Before #593 this escaped to the stdio loop and reached the client as
            // the bare JSON-RPC `Internal error`.
            var result = await Run(fx, Args("index", fx.Tree.Path));

            AssertCodedError(result, GxwireErrorCodes.UnexpectedException);
            Assert.Contains("locator exploded", result["message"]?.ToString());
            Assert.Equal("InvalidOperationException", result["diagnosticContext"]?["exceptionType"]?.ToString());
            Assert.Equal("index", result["diagnosticContext"]?["action"]?.ToString());
        }

        [Fact]
        public async Task UnexpectedException_MessageIsBounded()
        {
            using var fx = new Fixture();
            fx.Locator = () => throw new InvalidOperationException(new string('m', 20_000));

            var result = await Run(fx, Args("status", fx.Tree.Path));

            AssertCodedError(result, GxwireErrorCodes.UnexpectedException);
            Assert.True(
                (result["message"]?.ToString() ?? "").Length <= GxwireTool.GxwireExceptionMessageChars + 128,
                "the exception detail must stay bounded inside the envelope");
        }

        [Fact]
        public async Task MalformedNumericArgument_DoesNotThrow()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            var args = Args("callers", fx.Tree.Path, ("target", "PInvoice"));
            args["token_budget"] = "lots";
            args["full"] = "maybe";

            var result = await Run(fx, args);

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.Contains("--token-budget 1200", fx.LaunchedArguments);
        }

        [Theory]
        [InlineData("target", "--cache-path")]
        [InlineData("target", "-x")]
        [InlineData("from", "--dir")]
        [InlineData("format", "--cache-path")]
        public async Task LeadingDashValue_IsRejectedBeforeLaunch(string key, string value)
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            var args = Args("path", fx.Tree.Path, ("from", "A"), ("to", "B"));
            args[key] = value;

            var result = await Run(fx, args);

            AssertCodedError(result, GxwireErrorCodes.InvalidArgument);
            Assert.False(fx.Launched, "a value that would parse as a flag must never reach gxwire");
        }

        [Fact]
        public async Task QuoteInjectionInTarget_StaysOneArgument()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            fx.WriteIndex();
            const string payload = "x\" --cache-path \"C:\elsewhere";

            var result = await Run(fx, Args("callers", fx.Tree.Path, ("target", payload)));

            Assert.Equal("ok", result["status"]?.ToString());
            var list = new List<string>(fx.LaunchedArgumentList!);
            Assert.Equal(payload, list[list.IndexOf("--callers") + 1]);
            Assert.Single(list, a => a == "--cache-path");
            Assert.Equal(fx.CachePath, list[list.IndexOf("--cache-path") + 1]);
        }

        [Fact]
        public async Task UnknownAction_ReportsCode()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();

            var result = await Run(fx, Args("explode", fx.Tree.Path));

            AssertCodedError(result, GxwireErrorCodes.UnknownAction);
            Assert.False(fx.Launched);
        }

        [Fact]
        public async Task MissingDirectory_ReportsKbContextRequired()
        {
            using var fx = new Fixture();

            var result = await Run(fx, Args("status", Path.Combine(fx.Tree.Path, "does-not-exist")));

            AssertCodedError(result, GxwireErrorCodes.KbContextRequired);
            Assert.False(fx.Launched);
        }

        // --- the envelope must survive response shaping ----------------------------

        [Fact]
        public async Task EveryErrorEnvelope_SurvivesResponseShaping()
        {
            using var fx = new Fixture();
            fx.MakeTextTree();
            var cases = new (string Name, Func<Task<JObject>> Produce)[]
            {
                ("cache-missing", () => Run(fx, Args("status", fx.Tree.Path))),
                ("timeout", () =>
                {
                    fx.WriteIndex();
                    fx.Launcher = (_, _, t) => Task.FromResult(new GxwireProcessRun("", "", true, t, null, 1));
                    return Run(fx, Args("status", fx.Tree.Path));
                }),
                ("exit-code", () =>
                {
                    fx.Launcher = (_, _, _) => Task.FromResult(new GxwireProcessRun("", new string('e', 5000), false, 30, 2, 1));
                    return Run(fx, Args("status", fx.Tree.Path));
                }),
                ("unparsable", () =>
                {
                    fx.Launcher = (_, _, _) => Task.FromResult(new GxwireProcessRun("nope", "", false, 30, 0, 1));
                    return Run(fx, Args("status", fx.Tree.Path, ("format", "json")));
                }),
                ("exception", () =>
                {
                    fx.Locator = () => throw new InvalidOperationException("boom");
                    return Run(fx, Args("status", fx.Tree.Path));
                }),
            };

            foreach (var (name, produce) in cases)
            {
                JObject envelope = await produce();
                string code = envelope["code"]?.ToString() ?? "";
                Assert.False(string.IsNullOrEmpty(code), name + " must carry a code");

                var shaped = Program.BuildToolResultContent(envelope, true, "genexus_wire", new JObject { ["action"] = "status" });

                Assert.True(shaped["isError"]?.Value<bool>(), name + " must be flagged isError");
                string text = shaped["content"]?[0]?["text"]?.ToString() ?? "";
                Assert.Contains(code, text);
                Assert.Contains("diagnosticContext", text);
            }

            using var native = new Fixture();
            native.MakeNativeKbRoot();
            var refusal = await Run(native, Args("index", native.Tree.Path));
            var shapedRefusal = Program.BuildToolResultContent(refusal, true, "genexus_wire", new JObject { ["action"] = "index" });
            Assert.Contains(GxwireErrorCodes.RequiresTextSourceTree, shapedRefusal["content"]?[0]?["text"]?.ToString());
        }
    }
}
