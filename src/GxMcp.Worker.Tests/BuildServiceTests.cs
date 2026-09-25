using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Plan 009 — characterization tests for BuildService's public entry points
    /// that were NOT already covered by the per-stream test files
    /// (BuildErrorCategoryTests, BuildNotifyOnFailureTests, BuildSegmentedTargetsTests,
    /// StatusWaitTests, FastIncrementalDecisionTests, GxObjectMappingTests,
    /// PhaseFailureExtractionTests, EdgeCaseRegressionTests, ReorgPreviewTests).
    /// These pin CURRENT behavior (bugs and all) — see report for suspected issues.
    /// </summary>
    [Trait("Category", "ProcessSmoke")]
    public class BuildServiceTests : BuildServiceTestBase
    {
        // ── ParseTargets (private static) ───────────────────────────────────

        private static List<string> InvokeParseTargets(string target)
        {
            var mi = typeof(BuildService).GetMethod("ParseTargets", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            return (List<string>)mi.Invoke(null, new object[] { target });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ParseTargets_NullOrBlank_ReturnsEmpty(string input)
        {
            Assert.Empty(InvokeParseTargets(input));
        }

        [Fact]
        public void ParseTargets_CommaSeparated_TrimsAndPreservesOrder()
        {
            var result = InvokeParseTargets(" A , B ,C");
            Assert.Equal(new[] { "A", "B", "C" }, result.ToArray());
        }

        [Fact]
        public void ParseTargets_SemicolonAndNewlineSeparated_AllSplitTogether()
        {
            var result = InvokeParseTargets("A;B\nC\r\nD");
            Assert.Equal(new[] { "A", "B", "C", "D" }, result.ToArray());
        }

        [Fact]
        public void ParseTargets_DedupesCaseInsensitively_KeepsFirstOccurrenceCasing()
        {
            var result = InvokeParseTargets("Foo,FOO,foo,Bar");
            Assert.Equal(new[] { "Foo", "Bar" }, result.ToArray());
        }

        // ── Build() entry point — Accepted envelope shape ───────────────────

        [Fact]
        public void Build_SingleTarget_ReturnsSingularAcceptedMessage()
        {
            var svc = new BuildService();
            string json = svc.Build("Build", "MyProc", "none", 200, false);
            var jo = JObject.Parse(json);

            Assert.Equal("Accepted", jo["status"]?.ToString());
            Assert.Contains("Build task started in background", jo["message"]?.ToString());
            Assert.Equal(new[] { "MyProc" }, jo["targets"]?.ToObject<string[]>());
            Assert.False(string.IsNullOrEmpty(jo["taskId"]?.ToString()));
        }

        [Fact]
        public void Build_MultipleTargets_ReturnsPluralBatchMessage()
        {
            var svc = new BuildService();
            string json = svc.Build("Build", "A,B,C", "none", 200, false);
            var jo = JObject.Parse(json);

            Assert.Equal("Accepted", jo["status"]?.ToString());
            Assert.Contains("Batch build started for 3 objects", jo["message"]?.ToString());
            Assert.Equal(new[] { "A", "B", "C" }, jo["targets"]?.ToObject<string[]>());
        }

        [Fact]
        public void Build_PlanTruncated_ReturnsBuildPlanTooLarge()
        {
            var fx = TestFixtures.LargeCallChain(depth: 250);
            var graph = new CallerGraphService(fx.Index);
            var svc = new BuildService();
            svc.SetCallerGraphService(graph);

            string json = svc.Build("Build", "N0", "transitive", 200, false);
            var jo = JObject.Parse(json);

            Assert.Equal("BuildPlanTooLarge", jo["status"]?.ToString());
            Assert.Equal("N0", ((JArray)jo["requested"])!.Single().ToString());
            Assert.Equal(200, jo["graph"]?["cap"]?.ToObject<int>());
        }

        [Fact]
        public void Specify_WrapsBuild_WithSpecifyOnlyAndNoCallees()
        {
            var svc = new BuildService();
            string json = svc.Specify("MyObj");
            var jo = JObject.Parse(json);
            Assert.Equal("Accepted", jo["status"]?.ToString());

            string taskId = jo["taskId"]?.ToString();
            Assert.False(string.IsNullOrEmpty(taskId));

            var status = GetTaskFromRegistry(taskId);
            Assert.NotNull(status);
            Assert.True(status.SpecifyOnly);
            Assert.Equal("Build", status.Action);
            Assert.Equal("MyObj", status.Target);
        }

        [Fact]
        public void Specify_RejectsUnresolvedTargetBeforeQueuing()
        {
            var idx = new IndexCacheService();
            idx.ReplaceAll(new[]
            {
                new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "Existing", Type = "Procedure", IsEnriched = true }
            });
            var svc = new BuildService();
            svc.SetIndexCacheService(idx);

            var response = JObject.Parse(svc.Specify("Missing"));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("SpecifyTargetUnresolved", response["error"]?["code"]?.ToString());
        }

        // ── BuildDryRun() ────────────────────────────────────────────────────

        [Fact]
        public void BuildDryRun_NoGraph_PreviewsOriginalTargets()
        {
            var svc = new BuildService();
            string json = svc.BuildDryRun("Build", "A,B", "transitive", 200);
            var jo = JObject.Parse(json);

            Assert.Equal("ok", jo["status"]?.ToString());
            Assert.Equal("DryRun", jo["code"]?.ToString());
            var wouldBuild = jo["result"]?["preview"]?["wouldBuild"]?.ToObject<string[]>();
            Assert.Equal(new[] { "A", "B" }, wouldBuild);
        }

        [Fact]
        public void BuildDryRun_WithGraph_ExpandsTransitiveCallees()
        {
            var fx = TestFixtures.SmallCallGraph();
            var graph = new CallerGraphService(fx.Index);
            var svc = new BuildService();
            svc.SetCallerGraphService(graph);

            string json = svc.BuildDryRun("Build", "A", "transitive", 200);
            var jo = JObject.Parse(json);
            var wouldBuild = jo["result"]?["preview"]?["wouldBuild"]?.ToObject<string[]>();
            Assert.Equal(new[] { "C", "B", "A" }, wouldBuild);
        }

        [Fact]
        public void BuildDryRun_Truncated_ReturnsErrEnvelope()
        {
            var fx = TestFixtures.LargeCallChain(depth: 250);
            var graph = new CallerGraphService(fx.Index);
            var svc = new BuildService();
            svc.SetCallerGraphService(graph);

            string json = svc.BuildDryRun("Build", "N0", "transitive", 200);
            var jo = JObject.Parse(json);

            Assert.Equal("error", jo["status"]?.ToString());
            Assert.Equal("BuildPlanTooLarge", jo["error"]?["code"]?.ToString());
            // requestedNodes/cap/includeCallees are passed via McpResponse.Err's
            // "extra" param, which merges at the top level of the envelope, not
            // nested under "error".
            Assert.Equal(200, jo["cap"]?.ToObject<int>());
        }

        [Fact]
        public void BuildDryRun_NonBuildAction_SkipsPlanExpansion()
        {
            var svc = new BuildService();
            string json = svc.BuildDryRun("RebuildAll", null, "transitive", 200);
            var jo = JObject.Parse(json);

            Assert.Equal("ok", jo["status"]?.ToString());
            var preview = jo["result"]?["preview"];
            Assert.Equal("RebuildAll", preview?["action"]?.ToString());
            Assert.Empty(preview?["wouldBuild"]?.ToObject<string[]>() ?? Array.Empty<string>());
        }

        // ── GetStatus() ──────────────────────────────────────────────────────

        [Fact]
        public void GetStatus_EmptyTaskId_ReturnsRecentTaskList()
        {
            var svc = new BuildService();
            string json = svc.GetStatus(null);
            var jo = JObject.Parse(json);
            Assert.NotNull(jo["tasks"]);
        }

        [Fact]
        public void GetStatus_UnknownTaskId_ReturnsNotFound()
        {
            var svc = new BuildService();
            string json = svc.GetStatus("does-not-exist-" + Guid.NewGuid());
            Assert.Contains("Task ID not found", json);
        }

        [Fact]
        public void GetStatus_WhileRunning_StripsOutputField()
        {
            var (svc, status, taskId) = RegisterTask("Running", output: "some big build log");
            string json = svc.GetStatus(taskId);
            var jo = JObject.Parse(json);
            Assert.Null(jo["Output"]);
            Assert.Null(jo["output"]);
        }

        [Fact]
        public void GetStatus_WhenTerminal_KeepsOutputField()
        {
            var (svc, status, taskId) = RegisterTask("Succeeded", output: "final log");
            string json = svc.GetStatus(taskId);
            var jo = JObject.Parse(json);
            Assert.Equal("final log", jo["Output"]?.ToString());
        }

        [Fact]
        public void GetStatus_PaginatesWarnings()
        {
            var (svc, status, taskId) = RegisterTask("Succeeded");
            status.Warnings.Add("w1");
            status.Warnings.Add("w2");
            status.Warnings.Add("w3");

            string json = svc.GetStatus(taskId, page: 1, pageSize: 1);
            var jo = JObject.Parse(json);
            var warnings = jo["warnings"]?.ToObject<string[]>();
            Assert.Single(warnings!);
            Assert.Equal("w1", warnings![0]);
            Assert.True(jo["_meta"]?["pagination"]?["has_more"]?.ToObject<bool>());
        }

        [Fact]
        public void GetStatus_Compact_AddsWarningsAggregated()
        {
            var (svc, status, taskId) = RegisterTask("Succeeded");
            status.Warnings.Add("error spc0022: missing var");
            string json = svc.GetStatus(taskId, compact: true);
            var jo = JObject.Parse(json);
            Assert.NotNull(jo["warningsAggregated"]);
        }

        // ── GetResult() ──────────────────────────────────────────────────────

        [Fact]
        public void GetResult_EmptyTaskId_ReturnsError()
        {
            var svc = new BuildService();
            string json = svc.GetResult(null);
            Assert.Contains("taskId required", json);
        }

        [Fact]
        public void GetResult_UnknownTaskId_ReturnsNotFound()
        {
            var svc = new BuildService();
            string json = svc.GetResult("does-not-exist-" + Guid.NewGuid());
            Assert.Contains("Task ID not found", json);
        }

        [Fact]
        public void GetResult_PaginatesErrorsAsItems()
        {
            var (svc, status, taskId) = RegisterTask("Failed");
            status.Errors.Add("err1");
            status.Errors.Add("err2");

            string json = svc.GetResult(taskId, page: 1, pageSize: 1);
            var jo = JObject.Parse(json);
            var items = jo["items"]?.ToObject<string[]>();
            Assert.Single(items!);
            Assert.Equal("err1", items![0]);
        }

        // ── Cancel() ─────────────────────────────────────────────────────────

        [Fact]
        public void Cancel_EmptyTaskId_ReturnsError()
        {
            var svc = new BuildService();
            string json = svc.Cancel(null);
            Assert.Contains("taskId required", json);
        }

        [Fact]
        public void Cancel_UnknownTaskId_ReturnsStructuredHint()
        {
            var svc = new BuildService();
            string json = svc.Cancel("does-not-exist-" + Guid.NewGuid());
            var jo = JObject.Parse(json);
            Assert.Equal("Unknown build taskId", jo["error"]?.ToString());
            Assert.NotNull(jo["hint"]);
        }

        [Fact]
        public void Cancel_NoLiveProcess_ReportsAlreadyFinished()
        {
            var (svc, status, taskId) = RegisterTask("Succeeded");
            string json = svc.Cancel(taskId);
            var jo = JObject.Parse(json);
            Assert.Equal("Succeeded", jo["status"]?.ToString());
            Assert.Equal("Task already finished", jo["message"]?.ToString());
        }

        [Fact]
        public void Cancel_LiveProcess_MarksCancelledSynchronously()
        {
            var (svc, status, taskId) = RegisterTask("Running");
            // A short-lived real process stands in for a spawned MSBuild.exe —
            // Cancel() flips Status=Cancelled synchronously before the
            // background KillProcessTree runs, regardless of whether the kill
            // itself succeeds.
            var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            status.Process = proc;
            try
            {
                string json = svc.Cancel(taskId);
                var jo = JObject.Parse(json);
                Assert.Equal("Cancelled", jo["status"]?.ToString());
                Assert.Equal(taskId, jo["taskId"]?.ToString());
                lock (status._lock) { Assert.Equal("Cancelled", status.Status); }
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                proc.Dispose();
            }
        }

        // ── GetLatestBuildSummary() ──────────────────────────────────────────

        [Fact]
        public void GetLatestBuildSummary_PicksMostRecentByEndTime()
        {
            // Far-future EndTime values guarantee these two entries outrank
            // anything a concurrently-running test writes into the shared
            // static registry (test isolation workaround for shared state).
            var (_, olderStatus, olderId) = RegisterTask("Succeeded");
            olderStatus.EndTime = "9999-01-01 00:00:00";
            olderStatus.Target = "OlderTarget";

            var (_, newerStatus, newerId) = RegisterTask("Failed");
            newerStatus.EndTime = "9999-06-01 00:00:00";
            newerStatus.Target = "NewerTarget";

            var summary = BuildService.GetLatestBuildSummary();
            Assert.NotNull(summary);
            Assert.Equal("NewerTarget", summary!["target"]?.ToString());
            Assert.Equal(newerId, summary["taskId"]?.ToString());
        }

        // ── NormalizeMissingObjectName (private static) ─────────────────────

        private static string InvokeNormalizeMissingObjectName(string symbol, IndexCacheService lookup = null)
        {
            var mi = typeof(BuildService).GetMethod("NormalizeMissingObjectName", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            return (string)mi.Invoke(null, new object[] { symbol, lookup });
        }

        [Theory]
        [InlineData("wsfoo", "foo")]
        [InlineData("foo_bc", "foo")]
        [InlineData("foo_level", "foo")]
        [InlineData("foo_detail", "foo")]
        [InlineData("foo_ws", "foo")]
        [InlineData("foo_impl", "foo")]
        [InlineData("foo_client", "foo")]
        [InlineData("foo_main", "foo")]
        public void NormalizeMissingObjectName_NoLookup_StripsKnownSuffixesAndWsPrefix(string input, string expected)
        {
            Assert.Equal(expected, InvokeNormalizeMissingObjectName(input, null));
        }

        [Fact]
        public void NormalizeMissingObjectName_NoLookup_DoesNotStripSingleLetterPrefixes()
        {
            // 'a'/'p' prefixes collide with real KB names — never stripped.
            Assert.Equal("acessoperfil", InvokeNormalizeMissingObjectName("acessoperfil", null));
            Assert.Equal("painelclassesala", InvokeNormalizeMissingObjectName("painelclassesala", null));
        }

        [Fact]
        public void NormalizeMissingObjectName_WithLookup_StrippedFormKnown_ReturnsStripped()
        {
            var idx = new IndexCacheService();
            idx.ReplaceAll(new[] { new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "foo", Type = "Procedure", IsEnriched = true } });
            Assert.Equal("foo", InvokeNormalizeMissingObjectName("wsfoo", idx));
        }

        [Fact]
        public void NormalizeMissingObjectName_WithLookup_StrippedUnknown_OriginalKnown_ReturnsOriginal()
        {
            var idx = new IndexCacheService();
            idx.ReplaceAll(new[] { new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "wsfoo", Type = "Procedure", IsEnriched = true } });
            Assert.Equal("wsfoo", InvokeNormalizeMissingObjectName("wsfoo", idx));
        }

        [Fact]
        public void NormalizeMissingObjectName_WithLookup_NeitherKnown_ReturnsNull()
        {
            var idx = new IndexCacheService();
            idx.ReplaceAll(Array.Empty<SearchIndex.IndexEntry>());
            Assert.Null(InvokeNormalizeMissingObjectName("wsfoo", idx));
        }

        // ── NormalizeErrorIdentifierCase (internal instance method) ─────────

        [Fact]
        public void NormalizeErrorIdentifierCase_NoIndexCacheService_ReturnsLineUnchanged()
        {
            var svc = new BuildService();
            string line = "&objcod is not defined";
            Assert.Equal(line, svc.NormalizeErrorIdentifierCase(line));
        }

        [Fact]
        public void NormalizeErrorIdentifierCase_KnownDifferentCasing_RewritesToAuthoredCasing()
        {
            var idx = new IndexCacheService();
            idx.ReplaceAll(new[] { new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "ObjCod", Type = "Attribute", IsEnriched = true } });
            var svc = new BuildService();
            svc.SetIndexCacheService(idx);

            string result = svc.NormalizeErrorIdentifierCase("&objcod is not defined");
            Assert.Equal("&ObjCod is not defined", result);
        }

        [Fact]
        public void NormalizeErrorIdentifierCase_UnknownToken_LeftUnchanged()
        {
            var idx = new IndexCacheService();
            idx.ReplaceAll(Array.Empty<SearchIndex.IndexEntry>());
            var svc = new BuildService();
            svc.SetIndexCacheService(idx);

            string line = "&NotInIndex is not defined";
            Assert.Equal(line, svc.NormalizeErrorIdentifierCase(line));
        }

        // ── HandleLine — TailLines cap + noise filtering ────────────────────

        private static void InvokeHandleLine(BuildService svc, BuildService.BuildTaskStatus status, string line, bool isError = false)
        {
            var mi = typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            mi.Invoke(svc, new object[] { status, line, isError });
        }

        [Fact]
        public void HandleLine_TailLines_CappedAt30_KeepsMostRecent()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "tail-cap" };
            for (int i = 0; i < 35; i++)
                InvokeHandleLine(svc, status, "line " + i);

            Assert.Equal(30, status.TailLines.Count);
            Assert.Equal("line 34", status.TailLines[status.TailLines.Count - 1]);
            Assert.Equal("line 5", status.TailLines[0]); // lines 0-4 rolled off
            Assert.Equal(35, status.LineCount); // LineCount is not capped
        }

        [Fact]
        public void HandleLine_ModuleCopyNoise_ExcludedFromTailLines_ButCountsTowardLineCount()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "noise" };
            InvokeHandleLine(svc, status, "Copying module Foo.dll");
            InvokeHandleLine(svc, status, "Real informative line");

            Assert.Equal(2, status.LineCount);
            Assert.Single(status.TailLines);
            Assert.Equal("Real informative line", status.TailLines[0]);
        }

        [Fact]
        public void HandleLine_ModuleCopyNoiseLine_WithErrorText_IsNotTreatedAsNoise()
        {
            // isNoise requires the noise-pattern match AND absence of error/warning
            // markers — a "Copying module" line that also carries "error:" must
            // still surface in TailLines.
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "noise-error" };
            InvokeHandleLine(svc, status, "Copying module Foo.dll error CS1002: ; expected", isError: true);

            Assert.Single(status.TailLines);
            Assert.Equal(1, status.ErrorCount);
        }

        // ── issue #32 item 5: spurious "not found in KB" warning suppression ──

        [Theory]
        [InlineData("warning: Objeto 'ApiCtlObjTransicionar' não foi encontrado na Knowledge Base.")]
        [InlineData("warning: Object 'ApiCtlObjTransicionar' was not found in the Knowledge Base")]
        public void HandleLine_ObjectNotFoundWarning_ForBuildTarget_IsSuppressed(string line)
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "nf-warn", Target = "ApiCtlObjTransicionar" };
            InvokeHandleLine(svc, status, line);

            Assert.Equal(0, status.WarningCount);
            Assert.Empty(status.Warnings);
        }

        [Fact]
        public void HandleLine_SpecifyExistingTarget_DemotesNotFoundDiagnostic()
        {
            var idx = new IndexCacheService();
            idx.ReplaceAll(new[]
            {
                new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "ExistingProc", Type = "Procedure", IsEnriched = true }
            });
            var svc = new BuildService();
            svc.SetIndexCacheService(idx);
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = "nf-specify",
                Target = "ExistingProc",
                SpecifyOnly = true
            };

            InvokeHandleLine(svc, status, "error: Object 'ExistingProc' was not found in the Knowledge Base", isError: true);

            Assert.Equal(0, status.ErrorCount);
            Assert.Equal(0, status.WarningCount);
            Assert.Contains("ExistingProc", status.NotFoundTargets);
        }

        [Fact]
        public void HandleLine_ObjectNotFoundWarning_ForUnrelatedObject_IsKept()
        {
            // Only the build target's spurious warning is dropped; a genuine "not found"
            // for some OTHER object stays visible.
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "nf-keep", Target = "SomethingElse" };
            InvokeHandleLine(svc, status, "warning: Object 'MissingDependency' was not found in the Knowledge Base");

            Assert.Equal(1, status.WarningCount);
            Assert.Single(status.Warnings);
        }

        [Fact]
        public void IsBuildTarget_MatchesTarget_TargetsList_AndCurrentObject()
        {
            var status = new BuildService.BuildTaskStatus
            {
                Target = "Proc1",
                CurrentObject = "Proc3",
                Targets = new List<string> { "Proc1", "Proc2" }
            };
            Assert.True(BuildService.IsBuildTarget("proc1", status));
            Assert.True(BuildService.IsBuildTarget("PROC2", status));
            Assert.True(BuildService.IsBuildTarget("Proc3", status));
            Assert.False(BuildService.IsBuildTarget("Nope", status));
            Assert.False(BuildService.IsBuildTarget("", status));
        }

        [Fact]
        public void BuildTaskStatus_LivenessBaselineIncludesObjectAndLineProgress()
        {
            var first = new BuildService.BuildTaskStatus
            {
                Status = "Running", Phase = "Specifying", TargetsDone = 0,
                CurrentObject = "ProcA", LineCount = 10
            };
            var nextObject = new BuildService.BuildTaskStatus
            {
                Status = "Running", Phase = "Specifying", TargetsDone = 0,
                CurrentObject = "ProcB", LineCount = 10
            };
            var nextLine = new BuildService.BuildTaskStatus
            {
                Status = "Running", Phase = "Specifying", TargetsDone = 0,
                CurrentObject = "ProcA", LineCount = 11
            };

            Assert.Equal(first.ComputeBaseline(), nextObject.ComputeBaseline());
            Assert.NotEqual(first.ComputeLivenessBaseline(), nextObject.ComputeLivenessBaseline());
            Assert.NotEqual(first.ComputeLivenessBaseline(), nextLine.ComputeLivenessBaseline());
        }

        [Fact]
        public void WatchdogFailure_PreservesPhaseInEnvelopeAndMessage()
        {
            var status = new BuildService.BuildTaskStatus
            {
                Status = "Running", Phase = "Generating", StartedAt = DateTime.UtcNow.AddSeconds(-3)
            };

            Assert.True(BuildService.TrySetWatchdogFailure(status,
                phase => "watchdog failure at phase '" + phase + "'"));
            Assert.Equal("Failed", status.Status);
            Assert.Equal("Generating", status.Phase);
            Assert.Equal("watchdog failure at phase 'Generating'", status.Error);
        }

        // ── issue #42: no-progress watchdog env parsing ─────────────────────

        [Fact]
        public void ResolveBuildNoProgressSeconds_Unset_ReturnsDefault180()
        {
            var prev = Environment.GetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC");
            Environment.SetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC", null);
            try { Assert.Equal(180, BuildService.ResolveBuildNoProgressSeconds()); }
            finally { Environment.SetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC", prev); }
        }

        [Theory]
        [InlineData("0", 0)]      // explicit disable
        [InlineData("-5", 0)]     // any non-positive disables
        [InlineData("10", 30)]    // clamped up to floor
        [InlineData("300", 300)]  // in range, verbatim
        [InlineData("999999", 3600)] // clamped down to ceiling
        [InlineData("garbage", 180)] // unparseable → default
        public void ResolveBuildNoProgressSeconds_ParsesAndClamps(string envValue, int expected)
        {
            var prev = Environment.GetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC");
            Environment.SetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC", envValue);
            try { Assert.Equal(expected, BuildService.ResolveBuildNoProgressSeconds()); }
            finally { Environment.SetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC", prev); }
        }

        // ── issue #42 (P5): EditDirtyTracker.GetDirty snapshot ──────────────

        [Fact]
        public void EditDirtyTracker_GetDirty_ReturnsSortedExplicitEditsOnly()
        {
            string kb = @"C:\fake-kb-getdirty-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            EditDirtyTracker.MarkDirty(kb, "Zeta");
            EditDirtyTracker.MarkDirty(kb, "alpha");
            EditDirtyTracker.MarkDirty(kb, "Mid");

            var dirty = EditDirtyTracker.GetDirty(kb);
            // normalized to lowercase + ordinal-ignorecase sort
            Assert.Equal(new[] { "alpha", "mid", "zeta" }, dirty.ToArray());

            // MarkClean drops it from the dirty snapshot (does not add "never built").
            EditDirtyTracker.MarkClean(kb, "Mid");
            Assert.Equal(new[] { "alpha", "zeta" }, EditDirtyTracker.GetDirty(kb).ToArray());
        }

        [Fact]
        public void EditDirtyTracker_GetDirty_UnknownKb_ReturnsEmptyNeverNull()
        {
            var dirty = EditDirtyTracker.GetDirty(@"C:\never-touched-" + Guid.NewGuid().ToString("N"));
            Assert.NotNull(dirty);
            Assert.Empty(dirty);
        }

        // ── lifecycle FIFO admission for concurrent build-class requests ────

        [Fact]
        public void Build_ConcurrentBuildRunning_QueuesByDefault()
        {
            // Seed a genuinely in-flight build (in _inFlightBuilds, as RunBuild does).
            // The public Worker entry point now joins the same FIFO used by the
            // Gateway; callers that need the old fail-fast behavior must opt out
            // explicitly with queueLifecycle:false.
            var (running, runningId) = SeedInFlightBuild("Build", "FirstProc");
            var prev = Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS");
            Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", null);
            try
            {
                var svc = new BuildService();
                string json = svc.Build("Build", "SecondProc", "none", 200, false);
                var jo = JObject.Parse(json);
                Assert.Equal("Queued", jo["status"]?.ToString());
                Assert.Equal(1, jo["queuePosition"]?.ToObject<int>());
                Assert.False(string.IsNullOrWhiteSpace(jo["operationId"]?.ToString()));
                Assert.NotEqual(runningId, jo["operationId"]?.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", prev);
                InFlightRegistry().TryRemove(runningId, out _);
            }
        }

        [Fact]
        public void Build_ConcurrentBuildRunning_OptOutEnvAllowsSecondBuild()
        {
            var (running, runningId) = SeedInFlightBuild("Build", "FirstProc");
            var prev = Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS");
            Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", "1");
            try
            {
                var svc = new BuildService();
                string json = svc.Build("Build", "SecondProc", "none", 200, false);
                var jo = JObject.Parse(json);
                Assert.Equal("Accepted", jo["status"]?.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", prev);
                InFlightRegistry().TryRemove(runningId, out _);
            }
        }

        [Fact]
        public void Build_SecondCallImmediatelyAfterFirst_IsQueuedSynchronously()
        {
            // The admission record is installed synchronously before Task.Run is
            // scheduled, so a second call cannot slip through a TOCTOU window. The
            // second call now receives a queue handle instead of BuildAlreadyRunning.
            var prevAllow = Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS");
            var prevKb = Environment.GetEnvironmentVariable("GX_KB_PATH");
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-race-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempKb);
            Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", null);
            Environment.SetEnvironmentVariable("GX_KB_PATH", tempKb);
            string firstTaskId = null;
            string secondTaskId = null;
            try
            {
                var svc = new BuildService();

                string firstJson = svc.Build("Build", "RaceFirstProc", "none", 200, false);
                var firstJo = JObject.Parse(firstJson);
                Assert.NotEqual("BuildAlreadyRunning", firstJo["status"]?.ToString());
                firstTaskId = firstJo["taskId"]?.ToString();
                Assert.False(string.IsNullOrEmpty(firstTaskId));

                string secondJson = svc.Build("Build", "RaceSecondProc", "none", 200, false);
                var secondJo = JObject.Parse(secondJson);
                secondTaskId = secondJo["taskId"]?.ToString();
                Assert.Equal("Queued", secondJo["status"]?.ToString());
                Assert.Equal(1, secondJo["queuePosition"]?.ToObject<int>());
                Assert.NotEqual(firstTaskId, secondTaskId);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", prevAllow);
                Environment.SetEnvironmentVariable("GX_KB_PATH", prevKb);
                try { Directory.Delete(tempKb, true); } catch { }
                if (firstTaskId != null)
                {
                    InFlightRegistry().TryRemove(firstTaskId, out _);
                    TasksRegistry().TryRemove(firstTaskId, out _);
                }
                if (secondTaskId != null)
                {
                    InFlightRegistry().TryRemove(secondTaskId, out _);
                    TasksRegistry().TryRemove(secondTaskId, out _);
                }
            }
        }

        [Fact]
        public void Build_LifecycleAdmission_QueuesAndCoalesces()
        {
            var (running, runningId) = SeedInFlightBuild("Build", "FirstProc");
            var prev = Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS");
            Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", null);
            try
            {
                var svc = new BuildService();
                var first = JObject.Parse(svc.Build(
                    "Build", " Alpha, Beta ", "transitive", 200, false,
                    notifyOnFailure: null, fastIncremental: false, specifyOnly: false,
                    fullDeploy: false, compileCheck: false, compileCheckCallers: null,
                    compileCheckTruncated: false, compileCheckGraphAvailable: true,
                    queueLifecycle: true));
                var duplicate = JObject.Parse(svc.Build(
                    "Build", "beta;alpha", "transitive", 200, false,
                    notifyOnFailure: null, fastIncremental: false, specifyOnly: false,
                    fullDeploy: false, compileCheck: false, compileCheckCallers: null,
                    compileCheckTruncated: false, compileCheckGraphAvailable: true,
                    queueLifecycle: true));
                var second = JObject.Parse(svc.Build(
                    "Build", "Gamma", "transitive", 200, false,
                    notifyOnFailure: null, fastIncremental: false, specifyOnly: false,
                    fullDeploy: false, compileCheck: false, compileCheckCallers: null,
                    compileCheckTruncated: false, compileCheckGraphAvailable: true,
                    queueLifecycle: true));

                Assert.Equal("Queued", first["status"]?.ToString());
                Assert.Equal("Queued", second["status"]?.ToString());
                Assert.Equal(1, first["queuePosition"]?.ToObject<int>());
                Assert.Equal(2, second["queuePosition"]?.ToObject<int>());
                Assert.Equal(first["operationId"]?.ToString(), duplicate["operationId"]?.ToString());
                Assert.True(duplicate["coalesced"]?.ToObject<bool>());
                Assert.NotEqual(runningId, first["operationId"]?.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", prev);
                InFlightRegistry().TryRemove(runningId, out _);
            }
        }

        [Fact]
        public void Build_LifecycleQueuePreservesSpecifyMode()
        {
            var (_, runningId) = SeedInFlightBuild("Build", "ActiveObject");
            var previous = Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS");
            Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", null);
            try
            {
                var svc = new BuildService();
                var response = JObject.Parse(svc.Specify("QueuedObject", queueLifecycle: true));
                var taskId = response["operationId"]?.ToString();
                Assert.Equal("Queued", response["status"]?.ToString());
                Assert.False(string.IsNullOrWhiteSpace(taskId));

                var status = GetTaskFromRegistry(taskId!);
                Assert.NotNull(status);
                Assert.True(status!.SpecifyOnly);
                Assert.Equal("Queued", status.Status);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", previous);
                InFlightRegistry().TryRemove(runningId, out _);
            }
        }

        [Fact]
        public void Build_LifecycleQueue_CancelsPendingRequest()
        {
            var (_, runningId) = SeedInFlightBuild("Build", "ActiveObject");
            var previous = Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS");
            Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", null);
            try
            {
                var svc = new BuildService();
                var queued = JObject.Parse(svc.Build(
                    "Build", "QueuedObject", "transitive", 200, false,
                    notifyOnFailure: null, fastIncremental: false, specifyOnly: false,
                    fullDeploy: false, compileCheck: false, compileCheckCallers: null,
                    compileCheckTruncated: false, compileCheckGraphAvailable: true,
                    queueLifecycle: true));
                string taskId = queued["operationId"]!.ToString();
                var cancelled = JObject.Parse(svc.Cancel(taskId));
                Assert.Equal("Cancelled", cancelled["status"]?.ToString());

                var status = GetTaskFromRegistry(taskId);
                Assert.NotNull(status);
                Assert.Equal("Cancelled", status!.Status);
                Assert.Null(status.QueuePosition);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS", previous);
                InFlightRegistry().TryRemove(runningId, out _);
            }
        }

        [Fact]
        public void GetActiveBuilds_IgnoresOrphanedRunningStatusNotInFlight()
        {
            // A "Running"-labelled task in _tasks that is NOT in _inFlightBuilds (e.g.
            // its thread died without terminalizing) must not count as active — otherwise
            // a crashed build would wedge every future build on the worker.
            var (_, orphan, _) = RegisterTask("Running");
            orphan.Action = "Build";
            Assert.DoesNotContain(BuildService.GetActiveBuilds(), b => ReferenceEquals(b, orphan));
        }

        // ── issue #42 hardening (D): concurrent-build reject scoped per KB ───

        [Fact]
        public void GetActiveBuilds_KbFilter_ExcludesBuildOnAnotherKb()
        {
            var (status, taskId) = SeedInFlightBuild("Build", "FirstProc");
            status.KbPath = @"C:\kbA";
            try
            {
                // Different KB → not returned (would-be reject on KB-B must not fire).
                Assert.DoesNotContain(BuildService.GetActiveBuilds(@"C:\kbB"), b => ReferenceEquals(b, status));
                // Same KB → returned.
                Assert.Contains(BuildService.GetActiveBuilds(@"C:\kbA"), b => ReferenceEquals(b, status));
                // Null filter (lifecycle-status view) → returned regardless of KB.
                Assert.Contains(BuildService.GetActiveBuilds(null), b => ReferenceEquals(b, status));
            }
            finally { InFlightRegistry().TryRemove(taskId, out _); }
        }

        // ── issue #42 hardening (A): incremental no-op is not a generation gap ─

        [Fact]
        public void GenerateEvidence_StaleButNotDirty_IsUpToDateNotGap()
        {
            // Explicit build of an UNCHANGED object: the incremental generator skipped
            // rewriting its .cs, so mtime stays old. Not dirty → up-to-date, not a gap.
            RunEvidenceScenario(
                objectName: "Unchanged",
                writeCsMtimeUtc: DateTime.UtcNow.AddHours(-2),
                dirty: false,
                assert: ev =>
                {
                    Assert.True((bool)ev["ok"]!);
                    Assert.Empty((JArray)ev["staleOrMissing"]!);
                    Assert.NotNull(ev["upToDate"]);
                    Assert.Contains((JArray)ev["upToDate"]!, t => (string)t["object"]! == "Unchanged");
                });
        }

        [Fact]
        public void GenerateEvidence_StaleAndDirty_IsGap()
        {
            // Object was edited this session (dirty) but the successful build left no
            // fresh .cs — the reporter's exact bug. Must be a gap.
            RunEvidenceScenario(
                objectName: "EditedProc",
                writeCsMtimeUtc: DateTime.UtcNow.AddHours(-2),
                dirty: true,
                assert: ev =>
                {
                    Assert.False((bool)ev["ok"]!);
                    Assert.Contains((JArray)ev["staleOrMissing"]!, t => (string)t["object"]! == "EditedProc"
                        && (string)t["reason"]! == "stale");
                });
        }

        [Fact]
        public void GenerateEvidence_MissingCs_IsGapEvenWhenNotDirty()
        {
            // No generated .cs at all for an explicit build target → always a gap,
            // regardless of dirty state.
            RunEvidenceScenario(
                objectName: "NeverEmitted",
                writeCsMtimeUtc: null,   // no file written
                dirty: false,
                assert: ev =>
                {
                    Assert.False((bool)ev["ok"]!);
                    Assert.Contains((JArray)ev["staleOrMissing"]!, t => (string)t["object"]! == "NeverEmitted"
                        && (string)t["reason"]! == "missing");
                });
        }

        [Fact]
        public void GenerateEvidence_Spc0217Unreachable_IsUnreachableNotGap()
        {
            // Successful build, no .cs on disk, but the build log carries spc0217 for the
            // target → the object is genuinely un-generatable (no callers, not main). Must
            // land in unreachable[] with reason "unreachable", NOT staleOrMissing, and NOT
            // flip ok:false. Dirty is irrelevant — an unreachable object never emits a .cs.
            RunEvidenceScenario(
                objectName: "OrphanProc",
                writeCsMtimeUtc: null,   // no file written
                dirty: true,
                unreachableInLog: true,
                assert: ev =>
                {
                    Assert.True((bool)ev["ok"]!);
                    Assert.Empty((JArray)ev["staleOrMissing"]!);
                    Assert.NotNull(ev["unreachable"]);
                    Assert.Contains((JArray)ev["unreachable"]!, t => (string)t["object"]! == "OrphanProc"
                        && (string)t["reason"]! == "unreachable");
                    Assert.Contains("spc0217", (string)ev["note"]!);
                });
        }

        // ── plan 045: referencedButNotBuilt "already built?" is O(1) set lookup ─

        [Fact]
        public void GenerateEvidence_ReferencedButNotBuilt_SkipsCalleeAlreadyInTargetSet()
        {
            // Batch build of A and B (chain A -> B -> C, includeCallees=none): B is a
            // callee of A but is ALSO an explicit build target, so it must NOT be
            // reported as referencedButNotBuilt (the checkSet.Contains "already built"
            // branch this plan swapped in for the old checkList.Any scan). C is a
            // callee of B, not built and has no generated .cs, so it SHOULD be reported.
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            // A and B both got fresh .cs from this "build"; C never did.
            File.WriteAllText(Path.Combine(web, "A.cs"), "class A {}");
            File.WriteAllText(Path.Combine(web, "B.cs"), "class B {}");

            var prevKb = Environment.GetEnvironmentVariable("GX_KB_PATH");
            Environment.SetEnvironmentVariable("GX_KB_PATH", tempKb);
            try
            {
                var fx = TestFixtures.SmallCallGraph(); // A -> B -> C
                var graph = new CallerGraphService(fx.Index);
                var svc = new BuildService();
                svc.SetCallerGraphService(graph);

                var status = new BuildService.BuildTaskStatus
                {
                    TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Action = "Build",
                    Target = "A,B",
                    Status = "Succeeded",
                    Phase = "Done",
                    StartedAt = DateTime.UtcNow.AddMinutes(-1),
                    BuildPlan = new BuildService.BuildPlan { IncludeCallees = "none", NodeCap = 200 },
                    DirtyAtStart = new List<string> { "a", "b" }
                };
                var mi = typeof(BuildService).GetMethod("AttachGenerateEvidence", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(mi);
                mi!.Invoke(svc, new object[] { status, "Build", new List<string> { "A", "B" } });

                Assert.NotNull(status.GenerateEvidence);
                var ev = (JObject)status.GenerateEvidence!;
                var referencedButNotBuilt = (JArray)ev["referencedButNotBuilt"]!;
                Assert.DoesNotContain(referencedButNotBuilt, t => string.Equals((string)t, "B", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(referencedButNotBuilt, t => string.Equals((string)t, "C", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_KB_PATH", prevKb);
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }

        // issue #103 item 4 — the generator emits setProp("Gx Control Type",...) only
        // sporadically (1 of 32 User Control objects in the reference KB carried it, IDE
        // builds included). Its absence is the norm, not a degradation, and must not raise
        // a warning: a detector that fires on every healthy build buries the real signal.
        [Fact]
        public void GenerateEvidence_IgnoresMissingGxControlTypeBinding()
        {
            string objectName = "Dashboard";
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, objectName + ".cs"), "class Dashboard {}");

            // Healthy JS as the IDE emits it: real control name, full property list,
            // no "Gx Control Type" binding anywhere.
            string js = "n=gx.uc.getNew(this,6,0,\"DashboardViewer\",\"DV1Container\",\"Dashboardviewer1\",\"DV1\");"
                      + "n.setProp(\"Class\",\"Class\",\"DashboardViewer\",\"str\");"
                      + "n.setProp(\"Visible\",\"Visible\",!0,\"bool\");";
            File.WriteAllText(Path.Combine(web, objectName.ToLowerInvariant() + ".js"), js);

            var previousKb = Environment.GetEnvironmentVariable("GX_KB_PATH");
            Environment.SetEnvironmentVariable("GX_KB_PATH", tempKb);
            try
            {
                Assert.Null(BuildService.DetectUserControlBindingDegradation(
                    objectName,
                    Path.Combine(web, objectName.ToLowerInvariant() + ".js"),
                    js));

                var svc = new BuildService();
                var status = new BuildService.BuildTaskStatus
                {
                    TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Action = "Build",
                    Target = objectName,
                    Status = "Succeeded",
                    Phase = "Done",
                    StartedAt = DateTime.UtcNow.AddSeconds(-1),
                    DirtyAtStart = new List<string> { objectName }
                };

                var method = typeof(BuildService).GetMethod("AttachGenerateEvidence", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);
                method!.Invoke(svc, new object[] { status, "Build", new List<string> { objectName } });

                var evidence = (JObject)status.GenerateEvidence!;
                Assert.Null(evidence["degradedUserControls"]);
                Assert.DoesNotContain(status.Warnings, w => w.Contains("user-control-degraded"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_KB_PATH", previousKb);
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }

        // The real degradation, as measured against IDE baselines: the control name comes
        // out as the literal string "this" and every setProp binding is gone.
        [Fact]
        public void GenerateEvidence_DetectsDegradedUserControlSignature()
        {
            string objectName = "GrafComercial";
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, objectName + ".cs"), "class GrafComercial {}");

            string js = "gx.uc.getNew(this,10,5,\"QueryViewer\",this.CmpContext+\"QV1Container\",\"this\",\"QV1\");"
                      + "i=this.QV1Container;i.setC2ShowFunction(function(n){n.show()});this.setUserControl(i);";
            string jsPath = Path.Combine(web, objectName.ToLowerInvariant() + ".js");
            File.WriteAllText(jsPath, js);

            var previousKb = Environment.GetEnvironmentVariable("GX_KB_PATH");
            Environment.SetEnvironmentVariable("GX_KB_PATH", tempKb);
            try
            {
                var direct = BuildService.DetectUserControlBindingDegradation(objectName, jsPath, js);
                Assert.NotNull(direct);
                Assert.Equal(1, direct!["userControlInstances"]!.Value<int>());
                Assert.Equal(0, direct["propertyBindings"]!.Value<int>());
                Assert.Contains((JArray)direct["placeholderControlNames"]!,
                    t => string.Equals((string)t, "QueryViewer", StringComparison.OrdinalIgnoreCase));

                var svc = new BuildService();
                var status = new BuildService.BuildTaskStatus
                {
                    TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Action = "Build",
                    Target = objectName,
                    Status = "Succeeded",
                    Phase = "Done",
                    StartedAt = DateTime.UtcNow.AddSeconds(-1),
                    DirtyAtStart = new List<string> { objectName }
                };

                var method = typeof(BuildService).GetMethod("AttachGenerateEvidence", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);
                method!.Invoke(svc, new object[] { status, "Build", new List<string> { objectName } });

                var evidence = (JObject)status.GenerateEvidence!;
                Assert.False(evidence["ok"]!.Value<bool>(), evidence.ToString());
                var degraded = (JArray)evidence["degradedUserControls"]!;
                Assert.Single(degraded);
                Assert.Equal(0, degraded[0]["propertyBindings"]!.Value<int>());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_KB_PATH", previousKb);
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }

        [Fact]
        public void GenerateEvidence_DetectsDegradedUserControlInUpToDateJavaScript()
        {
            const string objectName = "SamplePanel";
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);

            string js = "gx.uc.getNew(this,3,0,'SampleViewer','SV1Container','this','SV1');\n"
                      + "i=this.SV1Container;this.setUserControl(i);";
            File.WriteAllText(Path.Combine(web, objectName.ToLowerInvariant() + ".js"), js);

            string previousKb = Environment.GetEnvironmentVariable("GX_KB_PATH");
            Environment.SetEnvironmentVariable("GX_KB_PATH", tempKb);
            try
            {
                var svc = new BuildService();
                var status = new BuildService.BuildTaskStatus
                {
                    TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Action = "Build",
                    Target = objectName,
                    Status = "Succeeded",
                    Phase = "Done",
                    StartedAt = DateTime.UtcNow.AddMinutes(-1),
                    DirtyAtStart = new List<string>()
                };

                var method = typeof(BuildService).GetMethod("AttachGenerateEvidence", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);
                method!.Invoke(svc, new object[] { status, "Build", new List<string> { objectName } });

                var evidence = (JObject)status.GenerateEvidence!;
                Assert.False(evidence["ok"]!.Value<bool>(), evidence.ToString());
                var degraded = (JArray)evidence["degradedUserControls"]!;
                Assert.Single(degraded);
                Assert.Equal(1, degraded[0]["userControlInstances"]!.Value<int>());
                Assert.Equal(0, degraded[0]["propertyBindings"]!.Value<int>());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_KB_PATH", previousKb);
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }

        // Set up a temp KB, optionally write a generated .cs at a chosen mtime, run
        // AttachGenerateEvidence for a successful Build of <objectName>, and assert on
        // the resulting GenerateEvidence JObject.
        private static void RunEvidenceScenario(string objectName, DateTime? writeCsMtimeUtc, bool dirty, Action<JObject> assert, bool unreachableInLog = false)
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            if (writeCsMtimeUtc != null)
            {
                string f = Path.Combine(web, objectName + ".cs");
                File.WriteAllText(f, "class " + objectName + " {}");
                File.SetLastWriteTimeUtc(f, writeCsMtimeUtc.Value);
            }
            string logPath = null;
            if (unreachableInLog)
            {
                logPath = Path.Combine(tempKb, "build-test.log");
                File.WriteAllText(logPath,
                    ">L Specifying " + objectName + " ...\r\n" +
                    ">O2spc0217: Object is unreachable.|Artech.Architecture.Common.Location.SourcePosition;" +
                    "<SourcePosition><Line>0</Line><FullName>Procedure '" + objectName + "'</FullName></SourcePosition>\r\n");
            }
            var prevKb = Environment.GetEnvironmentVariable("GX_KB_PATH");
            Environment.SetEnvironmentVariable("GX_KB_PATH", tempKb);
            try
            {
                var svc = new BuildService();
                var status = new BuildService.BuildTaskStatus
                {
                    TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Action = "Build",
                    Target = objectName,
                    Status = "Succeeded",
                    Phase = "Done",
                    StartedAt = DateTime.UtcNow,   // build "started" now → old .cs is stale by absolute compare
                    FullLogPath = logPath,
                    DirtyAtStart = dirty ? new List<string> { objectName.ToLowerInvariant() } : new List<string>()
                };
                var mi = typeof(BuildService).GetMethod("AttachGenerateEvidence", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(mi);
                mi!.Invoke(svc, new object[] { status, "Build", new List<string> { objectName } });
                Assert.NotNull(status.GenerateEvidence);
                assert((JObject)status.GenerateEvidence!);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_KB_PATH", prevKb);
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }

        // ── Shared helpers ───────────────────────────────────────────────────

        private static ConcurrentDictionary<string, BuildService.BuildTaskStatus> TasksRegistry()
        {
            var fld = typeof(BuildService).GetField("_tasks", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(fld);
            return (ConcurrentDictionary<string, BuildService.BuildTaskStatus>)fld!.GetValue(null)!;
        }

        private static BuildService.BuildTaskStatus GetTaskFromRegistry(string taskId)
        {
            TasksRegistry().TryGetValue(taskId, out var status);
            return status;
        }

        private static ConcurrentDictionary<string, BuildService.BuildTaskStatus> InFlightRegistry()
        {
            var fld = typeof(BuildService).GetField("_inFlightBuilds", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(fld);
            return (ConcurrentDictionary<string, BuildService.BuildTaskStatus>)fld!.GetValue(null)!;
        }

        // Seed a genuinely in-flight build (as RunBuild does on entry) so P3c's
        // reject / P2b's activeBuilds see it as live.
        private static (BuildService.BuildTaskStatus status, string taskId) SeedInFlightBuild(string action, string target)
        {
            var taskId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = taskId,
                Action = action,
                Target = target,
                Status = "Running",
                Phase = "Compiling",
                StartedAt = DateTime.UtcNow
            };
            InFlightRegistry()[taskId] = status;
            return (status, taskId);
        }

        private static (BuildService svc, BuildService.BuildTaskStatus status, string taskId) RegisterTask(string statusValue, string output = null)
        {
            var svc = new BuildService();
            var taskId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = taskId,
                Action = "Build",
                Target = "X",
                Status = statusValue,
                Phase = statusValue == "Running" ? "Compiling" : "Done",
                StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StartedAt = DateTime.UtcNow,
                Output = output
            };
            TasksRegistry()[taskId] = status;
            return (svc, status, taskId);
        }
    }
}
