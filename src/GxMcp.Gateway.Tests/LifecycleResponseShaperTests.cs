using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // v2.3.8 (Task 6.1): compact lifecycle status output by default.
    // Friction report 2026-05-15 #9: a failed build's full Errors[]/Warnings[]/Output
    // payload routinely overflows the assistant's tool-result budget. Compacting
    // returns counts + top-10 errors + warning dedup; opt out with compact=false.
    public class LifecycleResponseShaperTests
    {
        private static string MakeBuildStatus(int errors, int warnings, string? repeatedWarning = null)
        {
            var errArr = new JArray();
            for (int i = 0; i < errors; i++) errArr.Add(new JObject { ["message"] = $"err{i}: CS0246", ["location"] = $"file{i}.cs(10,5)" });
            var warnArr = new JArray();
            if (repeatedWarning != null)
                for (int i = 0; i < warnings; i++) warnArr.Add(new JObject { ["message"] = repeatedWarning, ["location"] = $"file{i}.cs(1,1)" });
            else
                for (int i = 0; i < warnings; i++) warnArr.Add(new JObject { ["message"] = $"warn{i}", ["location"] = $"f{i}.cs(1,1)" });

            return new JObject
            {
                ["Status"] = "Failed",
                ["Phase"] = "Done",
                ["ExitCode"] = 1,
                ["ErrorCount"] = errors,
                ["WarningCount"] = warnings,
                ["Errors"] = errArr,
                ["Warnings"] = warnArr,
                ["Output"] = new string('x', 8000),
                ["TaskId"] = "abc12345"
            }.ToString();
        }

        [Fact]
        public void Compact_True_CapsErrorsAndDropsOutput()
        {
            var raw = MakeBuildStatus(30, 5);
            var compact = LifecycleResponseShaper.Compact(raw, compact: true);
            var obj = JObject.Parse(compact);

            Assert.Null(obj["Output"]);
            Assert.Equal(30, obj["errorCount"].Value<int>());
            Assert.Equal(5, obj["warningCount"].Value<int>());
            Assert.True(((JArray)obj["errors"]).Count <= 10);
            Assert.True(obj["truncated"].Value<bool>());
        }

        [Fact]
        public void Compact_True_DedupsRepeatedWarning()
        {
            var raw = MakeBuildStatus(0, 6, repeatedWarning: "GAM nao sera reorganizado");
            var compact = LifecycleResponseShaper.Compact(raw, compact: true);
            var obj = JObject.Parse(compact);

            var warns = (JArray)obj["warnings"];
            Assert.Single(warns);
            Assert.Equal(6, warns[0]["count"].Value<int>());
            Assert.Equal("GAM nao sera reorganizado", warns[0]["message"].ToString());
        }

        [Fact]
        public void Compact_False_PreservesOriginal()
        {
            var raw = MakeBuildStatus(3, 2);
            var same = LifecycleResponseShaper.Compact(raw, compact: false);
            Assert.Equal(raw, same);
        }

        [Fact]
        public void Compact_NonJsonInput_ReturnsAsIs()
        {
            // Defensive: shaper must never throw on unexpected payloads (e.g. text error messages).
            var raw = "not json";
            var same = LifecycleResponseShaper.Compact(raw, compact: true);
            Assert.Equal(raw, same);
        }

        [Fact]
        public void Compact_SmallResponse_NoTruncation()
        {
            var raw = MakeBuildStatus(2, 1);
            var obj = JObject.Parse(LifecycleResponseShaper.Compact(raw, compact: true));
            Assert.False(obj["truncated"].Value<bool>());
            Assert.Equal(2, ((JArray)obj["errors"]).Count);
        }

        [Fact]
        public void Compact_True_SurfacesSuggestedRetry_WhenSuggestedRebuildTargetsPresent()
        {
            // FR (2026-05-21): BuildService already extracts the missing object
            // names from CS0246/CS2001 into SuggestedRebuildTargets. The compact
            // shaper must surface them as a ready-to-fire retry hint so the agent
            // doesn't have to scrape error[] paths by hand.
            var rawObj = JObject.Parse(MakeBuildStatus(2, 0));
            rawObj["SuggestedRebuildTargets"] = new JArray { "ConvenioValorizza", "PremioMerito", "ClasseTotalAlunos" };
            var compact = LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true);
            var obj = JObject.Parse(compact);

            var retry = obj["suggested_retry"] as JObject;
            Assert.NotNull(retry);
            Assert.Equal("build", retry!["action"]!.ToString());
            Assert.Equal("ConvenioValorizza,PremioMerito,ClasseTotalAlunos", retry["target"]!.ToString());
            Assert.Equal("direct", retry["includeCallees"]!.ToString());
            Assert.False(string.IsNullOrWhiteSpace(retry["hint"]!.ToString()));
        }

        [Fact]
        public void Compact_True_OmitsSuggestedRetry_WhenNoMissingObjects()
        {
            var raw = MakeBuildStatus(1, 0);
            var compact = LifecycleResponseShaper.Compact(raw, compact: true);
            var obj = JObject.Parse(compact);
            Assert.Null(obj["suggested_retry"]);
        }

        [Fact]
        public void ShouldCompact_DefaultsToTrue_AndHonorsExplicitFalseFlags()
        {
            Assert.True(LifecycleResponseShaper.ShouldCompact(null!));
            Assert.True(LifecycleResponseShaper.ShouldCompact(new JObject()));
            Assert.True(LifecycleResponseShaper.ShouldCompact(new JObject { ["compact"] = true }));
            Assert.False(LifecycleResponseShaper.ShouldCompact(new JObject { ["compact"] = false }));
            Assert.False(LifecycleResponseShaper.ShouldCompact(new JObject { ["compact"] = "false" }));
            Assert.False(LifecycleResponseShaper.ShouldCompact(new JObject { ["compact"] = "0" }));
        }

        // Friction 2026-05-22 item 10: build envelope classification. The async path
        // was previously matching "completed" (which the registry never emits) and
        // forcing "Build succeeded: 0w/0e/exit=0" into an <e>error{}> envelope.
        // ClassifyBuildOutcome is the single source of truth — covers all three
        // terminal cases.
        [Fact]
        public void ClassifyBuildOutcome_ZeroZeroExitClean_IsSuccess()
        {
            var payload = JObject.Parse(@"{""Status"":""Succeeded"",""ErrorCount"":0,""WarningCount"":0,""ExitCode"":0}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Success,
                LifecycleResponseShaper.ClassifyBuildOutcome(payload));
        }

        [Fact]
        public void ClassifyBuildOutcome_PartialSuccess_IsWarning()
        {
            var payload = JObject.Parse(@"{""Status"":""Failed"",""ErrorCount"":0,""WarningCount"":0,""ExitCode"":1,""PartialSuccess"":true}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.PartialSuccess,
                LifecycleResponseShaper.ClassifyBuildOutcome(payload));
        }

        [Fact]
        public void ClassifyBuildOutcome_RealFailure_IsError()
        {
            var payload = JObject.Parse(@"{""Status"":""Failed"",""ErrorCount"":3,""WarningCount"":1,""ExitCode"":1}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Error,
                LifecycleResponseShaper.ClassifyBuildOutcome(payload));
        }

        [Fact]
        public void ClassifyBuildOutcome_HonorsCompactShape_LowercaseCountsAndPartial()
        {
            // Post-Compact shape uses lowercase errorCount/warningCount + partial_success
            // bool on the outer object. Classifier must accept both shapes.
            var compactShape = JObject.Parse(@"{""Status"":""Failed"",""errorCount"":0,""warningCount"":0,""ExitCode"":1,""partial_success"":true}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.PartialSuccess,
                LifecycleResponseShaper.ClassifyBuildOutcome(compactShape));
        }

        [Fact]
        public void ClassifyBuildOutcome_NullPayload_IsError()
        {
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Error,
                LifecycleResponseShaper.ClassifyBuildOutcome(null));
        }

        [Fact]
        public void ClassifyBuildOutcome_StatusOnly_NoCounts_RespectsStatus()
        {
            var succ = JObject.Parse(@"{""Status"":""Succeeded""}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Success,
                LifecycleResponseShaper.ClassifyBuildOutcome(succ));
            var fail = JObject.Parse(@"{""Status"":""Failed""}");
            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Error,
                LifecycleResponseShaper.ClassifyBuildOutcome(fail));
        }

        [Fact]
        public void ClassifyBuildOutcome_BuildAllRequiresCompletionEvidence()
        {
            var payload = JObject.Parse(@"{""Status"":""Succeeded"",""Action"":""BuildAll"",""buildMode"":""BuildAll"",""ExitCode"":0,""ErrorCount"":0}");

            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Error,
                LifecycleResponseShaper.ClassifyBuildOutcome(payload));
        }

        [Fact]
        public void ClassifyBuildOutcome_BuildAllReorgOverridesPartialSuccess()
        {
            var payload = JObject.Parse(@"{""Status"":""ReorgRequired"",""Action"":""BuildAll"",""buildMode"":""BuildAll"",""buildAllDone"":false,""reorgRequired"":true,""PartialSuccess"":true}");

            Assert.Equal(LifecycleResponseShaper.BuildOutcome.Error,
                LifecycleResponseShaper.ClassifyBuildOutcome(payload));
        }

        // Production bug this catches: a non-build envelope (e.g. job status,
        // history result) silently being reshaped — losing fields the caller
        // depended on — because the shaper failed to gate on the build-shape
        // sentinel (Errors / Warnings / ErrorCount).
        [Fact]
        public void Compact_PassesThroughNonBuildEnvelope_Verbatim()
        {
            var raw = new JObject
            {
                ["status"] = "Running",
                ["jobId"] = "job-123",
                ["progress"] = new JObject { ["pct"] = 42 }
            }.ToString(Newtonsoft.Json.Formatting.None);

            var result = LifecycleResponseShaper.Compact(raw, compact: true);

            Assert.Equal(raw, result);
        }

        // Production bug this catches: an empty / whitespace string slips
        // through and tries to JObject.Parse(""), throwing an unhandled
        // exception inside the gateway's response pipeline.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void Compact_EmptyOrWhitespace_ReturnsAsIs(string? raw)
        {
            var result = LifecycleResponseShaper.Compact(raw!, compact: true);
            Assert.Equal(raw, result);
        }

        // Production bug this catches: PhaseFailure on WebAppConfig was
        // historically reported as "Build Failed: 0 errors, 0 warnings",
        // burning the user's time. Compact must surface phase_failure with
        // a tailored retry hint that mentions WebAppConfig recovery.
        [Fact]
        public void Compact_PhaseFailure_WebAppConfig_SurfacesTailoredRetryHint()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(0, 0));
            rawObj["PhaseFailure"] = new JObject
            {
                ["Name"] = "WebAppConfig",
                ["Message"] = "Could not write web.config"
            };

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            var pf = obj["phase_failure"] as JObject;
            Assert.NotNull(pf);
            Assert.Equal("WebAppConfig", pf!["Name"]!.ToString());

            var retry = obj["suggested_retry"] as JObject;
            Assert.NotNull(retry);
            Assert.Contains("WebAppConfig", retry!["hint"]!.ToString());
        }

        // Production bug this catches: PhaseFailure on a non-WebAppConfig
        // step (e.g. Reorg) was previously bucketed with WebAppConfig hints
        // — agents got a misleading "run the object" tip instead of
        // "check phase_failure.Message".
        [Fact]
        public void Compact_PhaseFailure_GenericPhase_SurfacesGenericHint()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(0, 0));
            rawObj["PhaseFailure"] = new JObject
            {
                ["Name"] = "Reorganization",
                ["Message"] = "DB schema drift"
            };

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            var retry = obj["suggested_retry"] as JObject;
            Assert.NotNull(retry);
            var hint = retry!["hint"]!.ToString();
            Assert.Contains("Reorganization", hint);
            Assert.DoesNotContain("WebAppConfig", hint);
        }

        // Production bug this catches: a failed-build envelope that ALSO had
        // SuggestedRebuildTargets must keep the CS0246/CS2001 retry hint
        // even when PhaseFailure is present — both got attached at different
        // times and one was overwriting the other.
        [Fact]
        public void Compact_PhaseFailureDoesNotOverwrite_SuggestedRebuildTargetsRetry()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(1, 0));
            rawObj["SuggestedRebuildTargets"] = new JArray { "FooObject" };
            rawObj["PhaseFailure"] = new JObject { ["Name"] = "WebAppConfig" };

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            var retry = obj["suggested_retry"] as JObject;
            Assert.NotNull(retry);
            // The CS0246 hint (which carries target=) wins over WebAppConfig.
            Assert.Equal("FooObject", retry!["target"]?.ToString());
            Assert.Equal("direct", retry["includeCallees"]?.ToString());
        }

        // Production bug this catches: PartialSuccess=true must override
        // Status so a caller branching on Status="Failed" doesn't treat a
        // partially-successful build as a hard failure.
        [Fact]
        public void Compact_PartialSuccess_SurfacesPartialSuccessFlagAndOverridesStatus()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(2, 1));
            rawObj["PartialSuccess"] = true;

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            Assert.True(obj["partial_success"]!.Value<bool>());
            Assert.Equal("PartialSuccess", obj["effective_status"]!.ToString());
            // Original Status is preserved for callers that want raw.
            Assert.Equal("Failed", obj["Status"]!.ToString());
        }

        // Production bug this catches: jobId / ElapsedSeconds / _meta were
        // dropped during the reshape, so callers asking for the raw payload
        // later via action=result lost the handle.
        [Fact]
        public void Compact_PropagatesJobIdElapsedSecondsAndMeta()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(1, 0));
            rawObj["jobId"] = "JOB-42";
            rawObj["ElapsedSeconds"] = 12.5;
            rawObj["_meta"] = new JObject { ["taskId"] = "T-1" };

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            Assert.Equal("JOB-42", obj["jobId"]!.ToString());
            Assert.Equal(12.5, obj["ElapsedSeconds"]!.Value<double>());
            Assert.Equal("T-1", obj["_meta"]!["taskId"]!.ToString());
        }

        [Fact]
        public void Compact_PropagatesResolvedEnvironment()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(0, 0));
            rawObj["Status"] = "Succeeded";
            rawObj["Environment"] = "NETCoreMySQL";

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            Assert.Equal("NETCoreMySQL", obj["Environment"]!.ToString());
        }

        // Production bug this catches: with very many duplicate warnings, the
        // sampleLocations array was unbounded and the "compact" envelope was
        // anything but. Cap at WarningSampleCap (3).
        [Fact]
        public void Compact_WarningSampleLocations_CappedAtThree()
        {
            var raw = MakeBuildStatus(0, 10, repeatedWarning: "spc0042");
            var obj = JObject.Parse(LifecycleResponseShaper.Compact(raw, compact: true));

            var warns = (JArray)obj["warnings"]!;
            Assert.Single(warns);
            Assert.Equal(10, warns[0]!["count"]!.Value<int>());
            var samples = (JArray)warns[0]!["sampleLocations"]!;
            Assert.Equal(LifecycleResponseShaper.WarningSampleCap, samples.Count);
        }

        // Production bug this catches: ErrorCount field absent on some
        // envelope shapes — shaper must fall back to Errors.Count rather
        // than emit errorCount=0 alongside a non-empty errors[] array.
        [Fact]
        public void Compact_MissingErrorCountField_FallsBackToErrorsArrayCount()
        {
            var rawObj = new JObject
            {
                ["Status"] = "Failed",
                ["Errors"] = new JArray
                {
                    new JObject { ["message"] = "e1" },
                    new JObject { ["message"] = "e2" }
                }
            };

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            Assert.Equal(2, obj["errorCount"]!.Value<int>());
            Assert.Equal(2, ((JArray)obj["errors"]!).Count);
        }

        // ── issue #42 — build-evidence gate passthrough ──────────────────────

        // A Succeeded build whose evidence gate found a gap (ok=false) must be
        // surfaced with effective_status=SucceededWithGaps so an agent branching
        // on Status="Succeeded" doesn't treat a build that emitted no fresh .cs
        // as a clean success.
        [Fact]
        public void Compact_GenerateEvidenceGap_SurfacesSucceededWithGapsAndHint()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(0, 0));
            rawObj["Status"] = "Succeeded";
            rawObj["GenerateEvidence"] = new JObject
            {
                ["ok"] = false,
                ["objectsChecked"] = 1,
                ["filesWritten"] = new JArray(),
                ["staleOrMissing"] = new JArray { "MyProc" }
            };
            rawObj["Hint"] = "Build reported Succeeded but no fresh .cs was found for: MyProc";

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            Assert.Equal("SucceededWithGaps", obj["effective_status"]!.ToString());
            var ev = obj["generateEvidence"] as JObject;
            Assert.NotNull(ev);
            Assert.False(ev!["ok"]!.Value<bool>());
            Assert.Contains("MyProc", obj["hint"]!.ToString());
        }

        // Issue #86: specify on unreachable object returns generateEvidence with ok=false
        // and effective_status=SucceededWithGaps.
        [Fact]
        public void Compact_SpecifyEvidenceUnreachable_SurfacesSucceededWithGaps()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(0, 1));
            rawObj["Status"] = "Succeeded";
            rawObj["Action"] = "Build";
            rawObj["Warnings"] = new JArray { "[specify-gap] Object unreachable or not found during specify: UnreachableProc" };
            rawObj["GenerateEvidence"] = new JObject
            {
                ["ok"] = false,
                ["objectsChecked"] = 1,
                ["objectsSpecified"] = 0,
                ["unreachable"] = new JArray
                {
                    new JObject { ["object"] = "UnreachableProc", ["reason"] = "unreachable" }
                },
                ["note"] = "Specify reported success but 1 object(s) were unreachable or not found in the Knowledge Base: UnreachableProc. The object was not specified by GeneXus."
            };
            rawObj["Hint"] = "Specification gap: object UnreachableProc was unreachable (spc0217) or not found.";

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            Assert.Equal("SucceededWithGaps", obj["effective_status"]!.ToString());
            var ev = obj["generateEvidence"] as JObject;
            Assert.NotNull(ev);
            Assert.False(ev!["ok"]!.Value<bool>());
            var un = ev!["unreachable"] as JArray;
            Assert.NotNull(un);
            Assert.Single(un!);
            Assert.Equal("UnreachableProc", un![0]?["object"]?.ToString());
        }

        // A Succeeded build whose gate passed (ok=true) passes evidence through
        // but must NOT stamp effective_status — it's a clean success.
        [Fact]
        public void Compact_GenerateEvidenceOk_PassesThroughWithoutEffectiveStatus()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(0, 0));
            rawObj["Status"] = "Succeeded";
            rawObj["GenerateEvidence"] = new JObject
            {
                ["ok"] = true,
                ["objectsChecked"] = 1,
                ["filesWritten"] = new JArray { "MyProc.cs" }
            };

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            Assert.NotNull(obj["generateEvidence"]);
            Assert.Null(obj["effective_status"]);
        }

        // P5 — objects edited but not yet successfully rebuilt are surfaced so an
        // agent can see stale generated code without a separate call.
        [Fact]
        public void Compact_StaleGenerated_PassedThrough()
        {
            var rawObj = JObject.Parse(MakeBuildStatus(0, 0));
            rawObj["staleGenerated"] = new JArray { "objecta", "objectb" };

            var obj = JObject.Parse(LifecycleResponseShaper.Compact(rawObj.ToString(), compact: true));

            var stale = obj["staleGenerated"] as JArray;
            Assert.NotNull(stale);
            Assert.Equal(2, stale!.Count);
        }
    }
}
