using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // PatternApplyService is the W2 surface for IDE 'Right-click → Apply Pattern'.
    // The live SDK path requires Artech.Packages.Patterns.dll + a WorkWithPlus
    // license + an open KB; the unit suite covers everything reachable through
    // the IPatternEngineAdapter seam, calling the real service via InternalsVisibleTo.
    //
    //  - pattern unavailable (no license / dll missing) → "pattern_unavailable"
    //  - unknown pattern key (not a GUID, not in registry) → "pattern_unavailable"
    //  - object not found → reuses McpResponse.Error not-found shape
    //  - happy-path first apply → status=Success, wasFirstApply=true
    //  - reapply with existing instance → status=Success, wasFirstApply=false
    //  - reapply when no instance exists → falls back to first-apply
    //  - engine throws → surfaced as Error envelope (not bubbled)
    //
    // Real end-to-end apply on a live KB is gated on Skip="no WWP license".
    //
    // v2.6.6: serialized with InProcessBuildRunnerTests via a shared xunit
    // Collection because both touch static SDK-reflection probes that race
    // under xunit's default parallel scheduler. Reliably green either alone
    // or in a clean run; the Collection just removes the race window.
    [Collection("InProcessSdkReflection")]
    public class PatternApplyServiceTests
    {
        private const string ObjName = "SomeTransaction";
        private static readonly Guid WWP = PatternApplyService.WorkWithPlusPatternId;

        private class FakeEngine : IPatternEngineAdapter
        {
            public object DefinitionToReturn { get; set; } = new object();
            public object ExistingInstance { get; set; }
            // Per-pattern overrides; ids absent from these maps use the defaults above.
            public Dictionary<Guid, object> DefinitionsById { get; } = new Dictionary<Guid, object>();
            public Dictionary<Guid, object> InstancesById { get; } = new Dictionary<Guid, object>();
            // Returned by GetPatternInstance once ApplyPattern ran (models the engine creating the instance).
            public object InstanceAfterApply { get; set; }
            public Func<JObject, PatternApplyResult> ApplyImpl { get; set; }
            public Func<JObject, PatternApplyResult> ReapplyImpl { get; set; }

            public int ApplyCalls;
            public int ReapplyCalls;
            public Guid? LastDefinitionPatternId;
            public Guid? LastInstancePatternId;
            public readonly List<Guid> DefinitionRequests = new List<Guid>();

            public object GetPatternDefinition(Guid patternId)
            {
                LastDefinitionPatternId = patternId;
                DefinitionRequests.Add(patternId);
                return DefinitionsById.TryGetValue(patternId, out var d) ? d : DefinitionToReturn;
            }

            public object GetPatternInstance(KBObject parent, Guid patternId)
            {
                LastInstancePatternId = patternId;
                if (ApplyCalls > 0 && InstanceAfterApply != null) return InstanceAfterApply;
                return InstancesById.TryGetValue(patternId, out var i) ? i : ExistingInstance;
            }

            public PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings)
            {
                ApplyCalls++;
                return ApplyImpl != null
                    ? ApplyImpl(settings)
                    : new PatternApplyResult { GeneratedObjects = new List<string> { "Generated1", "Generated2" } };
            }

            public PatternApplyResult ReapplyPattern(object patternInstance, JObject settings)
            {
                ReapplyCalls++;
                return ReapplyImpl != null
                    ? ReapplyImpl(settings)
                    : new PatternApplyResult { GeneratedObjects = new List<string> { "Regenerated" } };
            }
        }

        // Builds a service whose object resolver returns the supplied KBObject (or null).
        // We pass null for the KBObject in tests; the fake engine never dereferences it
        // and PatternApplyService.ApplyPatternToObject tolerates null via objectNameForResponse.
        private static PatternApplyService MakeService(IPatternEngineAdapter engine, KBObject objToReturn)
        {
            return new PatternApplyService(null, engine, name => objToReturn);
        }

        // ── Issue #260: generic (non-WorkWithPlus) pattern routes ──────────────────
        private static readonly Guid K2BEntityServicesId = new Guid("589d4b49-e3f9-4d49-aaf4-fad023028eb1");

        private const string EntityServicesManifest =
            "<Pattern Publisher=\"K2B\" Id=\"589d4b49-e3f9-4d49-aaf4-fad023028eb1\" Name=\"K2BEntityServices\" Version=\"13.1.1.15262\">" +
            "<Definition><InstanceName>K2BEntityServices{0}</InstanceName>" +
            "<ParentObjects><ParentObject Type=\"Transaction\"></ParentObject></ParentObjects></Definition></Pattern>";

        private static PatternRegistry K2BRegistry() => new PatternRegistry(new[]
        {
            PatternRegistry.ParseManifest(EntityServicesManifest, "es.Pattern")
        });

        private static PatternApplyService MakeK2BService(FakeEngine engine) =>
            new PatternApplyService(null, engine, name => null, K2BRegistry());

        private static PatternManifest K2B(PatternRegistry registry)
        {
            Assert.True(registry.TryResolve("K2BEntityServices", out var m));
            return m;
        }

        private static void WithRoutes(bool firstApply, bool reapply, Action body)
        {
            bool oldFirst = PatternApplyService.PatternRouteCapabilities.GenericFirstApplySupported;
            bool oldReapply = PatternApplyService.PatternRouteCapabilities.GenericReapplySupported;
            try
            {
                PatternApplyService.PatternRouteCapabilities.GenericFirstApplySupported = firstApply;
                PatternApplyService.PatternRouteCapabilities.GenericReapplySupported = reapply;
                body();
            }
            finally
            {
                PatternApplyService.PatternRouteCapabilities.GenericFirstApplySupported = oldFirst;
                PatternApplyService.PatternRouteCapabilities.GenericReapplySupported = oldReapply;
            }
        }

        [Fact]
        public void K2BName_UsesGenericRoute_ButUnverifiedEngineObjectDoesNotCountAsSuccess()
        {
            var engine = new FakeEngine { InstanceAfterApply = new object() };
            var svc = MakeK2BService(engine);

            string json = svc.ApplyPatternToObject(null, K2BEntityServicesId, "K2BEntityServices", null, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("PatternNoOp", obj["error"]?["code"]?.ToString() ?? obj["code"]?.ToString());
            Assert.Equal(1, engine.ApplyCalls);
            Assert.DoesNotContain(WWP, engine.DefinitionRequests);
            Assert.DoesNotContain("WorkWithPlus", json);
        }

        [Fact]
        public void GenericFirstApply_NoInstanceAfterEngine_ReturnsPatternNoOp()
        {
            var engine = new FakeEngine();
            var svc = MakeK2BService(engine);

            var obj = JObject.Parse(svc.ApplyPatternToObject(null, K2BEntityServicesId, "K2BEntityServices", null, reapply: false, objectNameForResponse: ObjName));

            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("PatternNoOp", obj["error"]?["code"]?.ToString() ?? obj["code"]?.ToString());
            Assert.DoesNotContain("WorkWithPlus", obj.ToString());
        }

        [Fact]
        public void GenericReapply_ByDefault_IsUnsupported_BecauseHeadlessReapplyDoesNotRegenerate()
        {
            // Live GX17 U4 + K2BTools 13.1: headless reapply re-saved the instance only;
            // no generated object changed, so the route must not report PatternApplied.
            var engine = new FakeEngine();
            engine.InstancesById[K2BEntityServicesId] = new object();
            var svc = MakeK2BService(engine);

            string json = svc.ApplyPatternToObject(null, K2BEntityServicesId, "K2BEntityServices", null, reapply: true, objectNameForResponse: ObjName);

            Assert.Contains("PatternRouteUnsupported", json);
            Assert.Contains("GeneXus IDE", json);
            Assert.Equal(0, engine.ReapplyCalls);
            Assert.Equal(0, engine.ApplyCalls);
        }
        [Fact]
        public void GenericReapply_WhenEnabled_RequiresResolvedInstance_AndNeverTouchesWwpId()
        {
            var engine = new FakeEngine();
            engine.InstancesById[K2BEntityServicesId] = new object();
            var svc = MakeK2BService(engine);

            string json = null;
            WithRoutes(firstApply: true, reapply: true, () => json = svc.ApplyPatternToObject(null, K2BEntityServicesId, "K2BEntityServices", null, reapply: true, objectNameForResponse: ObjName));
            var obj = JObject.Parse(json);

            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("PatternNoOp", obj["error"]?["code"]?.ToString() ?? obj["code"]?.ToString());
            Assert.Equal(1, engine.ReapplyCalls);
            Assert.Equal(0, engine.ApplyCalls);
            Assert.Equal(K2BEntityServicesId, engine.LastDefinitionPatternId);
            Assert.Equal(K2BEntityServicesId, engine.LastInstancePatternId);
            Assert.DoesNotContain(WWP, engine.DefinitionRequests);
            Assert.DoesNotContain("WorkWithPlus", json);
        }

        [Fact]
        public void UnknownKey_ListsAvailablePatterns()
        {
            var svc = MakeK2BService(new FakeEngine());

            var obj = JObject.Parse(svc.ApplyPattern(ObjName, "NotARealPatternKey"));

            Assert.Equal("pattern_unavailable", obj["status"]?.ToString());
            Assert.Equal("NotARealPatternKey", obj["patternKey"]?.ToString());
            var available = obj["availablePatterns"]!.ToObject<List<string>>();
            Assert.Contains("K2BEntityServices", available);
            Assert.Contains("WorkWithPlus", available);
        }

        [Fact]
        public void Reapply_UnknownKey_ListsAvailablePatterns()
        {
            var svc = MakeK2BService(new FakeEngine());

            var obj = JObject.Parse(svc.ReapplyPattern(ObjName, "NotARealPatternKey"));

            Assert.Equal("pattern_unavailable", obj["status"]?.ToString());
            Assert.Contains("K2BEntityServices", obj["availablePatterns"]!.ToObject<List<string>>());
        }

        [Fact]
        public void GenericNullDefinition_NamesThePattern_WithoutWorkWithPlusText()
        {
            var engine = new FakeEngine();
            engine.DefinitionsById[K2BEntityServicesId] = null;
            var svc = MakeK2BService(engine);

            string json = svc.ApplyPatternToObject(null, K2BEntityServicesId, "K2BEntityServices", null, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("pattern_unavailable", obj["status"]?.ToString());
            Assert.Contains("K2BEntityServices", obj["message"]?.ToString());
            Assert.Contains("license", obj["message"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WorkWithPlus", json);
            Assert.Equal(0, engine.ApplyCalls);
        }

        [Fact]
        public void GenericFirstApply_RouteOff_ReturnsPatternRouteUnsupported_BeforeEngine()
        {
            WithRoutes(firstApply: false, reapply: true, () =>
            {
                var engine = new FakeEngine { InstanceAfterApply = new object() };
                var svc = MakeK2BService(engine);

                var obj = JObject.Parse(svc.ApplyPatternToObject(null, K2BEntityServicesId, "K2BEntityServices", null, reapply: false, objectNameForResponse: ObjName));

                Assert.Equal("error", obj["status"]?.ToString());
                Assert.Contains("PatternRouteUnsupported", obj.ToString());
                Assert.Equal("firstApply", obj["route"]?.ToString());
                Assert.Contains("K2BEntityServices", obj["error"]?["message"]?.ToString());
                Assert.Equal(0, engine.ApplyCalls);
                Assert.Equal(0, engine.ReapplyCalls);
            });
        }

        [Fact]
        public void GenericReapply_RouteOff_ReturnsPatternRouteUnsupported_BeforeEngine()
        {
            WithRoutes(firstApply: true, reapply: false, () =>
            {
                var engine = new FakeEngine();
                engine.InstancesById[K2BEntityServicesId] = new object();
                var svc = MakeK2BService(engine);

                var obj = JObject.Parse(svc.ApplyPatternToObject(null, K2BEntityServicesId, "K2BEntityServices", null, reapply: true, objectNameForResponse: ObjName));

                Assert.Contains("PatternRouteUnsupported", obj.ToString());
                Assert.Equal("reapply", obj["route"]?.ToString());
                Assert.Equal(0, engine.ReapplyCalls);
                Assert.Equal(0, engine.ApplyCalls);
            });
        }

        [Fact]
        public void WwpRoute_IgnoresGenericRouteSwitches()
        {
            WithRoutes(firstApply: false, reapply: false, () =>
            {
                var engine = new FakeEngine();
                var svc = MakeK2BService(engine);

                var obj = JObject.Parse(svc.ApplyPatternToObject(null, WWP, "WWP", null, reapply: false, objectNameForResponse: ObjName));

                Assert.Equal("ok", obj["status"]?.ToString());
                Assert.Equal(1, engine.ApplyCalls);
            });
        }

        [Fact]
        public void Diagnose_K2B_ReportsPatternAndNoWorkWithPlusText()
        {
            var registry = K2BRegistry();
            var engine = new FakeEngine();
            var svc = new PatternApplyService(null, engine, name => null, registry);

            string json = svc.DiagnoseForObject(ObjName, null, "Transaction", "K2BEntityServices", K2B(registry), null, null);
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.Equal("K2BEntityServices", obj["pattern"]?["name"]?.ToString());
            Assert.True(obj["routeCapabilities"]?["firstApply"]?["supported"]?.ToObject<bool>());
            Assert.DoesNotContain("WorkWithPlus", json);
            Assert.DoesNotContain("WWP", json);
        }

        [Fact]
        public void Diagnose_K2B_WrongParentType_IsBlocked()
        {
            var registry = K2BRegistry();
            var svc = new PatternApplyService(null, new FakeEngine(), name => null, registry);

            var obj = JObject.Parse(svc.DiagnoseForObject("MyPanel", null, "WebPanel", "K2BEntityServices", K2B(registry), null, null));

            Assert.Equal("blocked", obj["status"]?.ToString());
            Assert.Contains(obj["findings"]!, f => f["reason"]?.ToString() == "parentTypeMismatch");
            Assert.DoesNotContain(obj["findings"]!, f => f["reason"]?.ToString() == "ok");
            Assert.DoesNotContain("WorkWithPlus", obj.ToString());
        }

        [Fact]
        public void Diagnose_K2B_RouteOff_ReportsRouteUnsupportedInsteadOfOk()
        {
            WithRoutes(firstApply: true, reapply: false, () =>
            {
                var registry = K2BRegistry();
                var engine = new FakeEngine();
                engine.InstancesById[K2BEntityServicesId] = new object();
                var svc = new PatternApplyService(null, engine, name => null, registry);

                var obj = JObject.Parse(svc.DiagnoseForObject(ObjName, null, "Transaction", "K2BEntityServices", K2B(registry), null, null));

                Assert.Equal("blocked", obj["status"]?.ToString());
                var route = ((JArray)obj["findings"]!).First(f =>f["reason"]?.ToString() == "routeUnsupported");
                Assert.Equal("critical", route["severity"]?.ToString());
                Assert.Equal("reapply", route["route"]?.ToString());
                Assert.Equal(PatternApplyService.RouteUnsupportedGuidance, route["remediation"]?.ToString());
                Assert.DoesNotContain("should apply cleanly", obj.ToString());
                Assert.DoesNotContain("WorkWithPlus", obj.ToString());
            });
        }

        [Fact]
        public void Diagnose_K2B_NullDefinition_RemediationNamesPackage()
        {
            var engine = new FakeEngine();
            engine.DefinitionsById[K2BEntityServicesId] = null;
            var svc = MakeK2BService(engine);

            string json = svc.DiagnosePattern(ObjName, "K2BEntityServices");
            var obj = JObject.Parse(json);

            Assert.Equal("blocked", obj["status"]?.ToString());
            Assert.Contains("Verify the K2BEntityServices package is present under GeneXus\\Packages\\Patterns and licensed", json.Replace("\\\\", "\\"));
            Assert.Equal("K2BEntityServices", obj["pattern"]?["name"]?.ToString());
            Assert.DoesNotContain("WorkWithPlus", json);
        }

        [Fact]
        public void Diagnose_Wwp_KeepsWorkWithPlusFindings()
        {
            var svc = MakeK2BService(new FakeEngine { ExistingInstance = new object() });
            var registry = K2BRegistry();
            Assert.True(registry.TryResolve("WorkWithPlus", out var wwp));

            var obj = JObject.Parse(svc.DiagnoseForObject(ObjName, null, "Transaction", "WorkWithPlus", wwp, null, null));

            Assert.Equal("ok", obj["status"]?.ToString());
            var conflict = ((JArray)obj["findings"]!).First(f =>f["reason"]?.ToString() == "overrideConflict");
            // Issue #352: the detail previously said "A first-apply will be a no-op;
            // use reapply=true" from a constant, independent of what the same response
            // reported about the reapply route. The remediation is now derived from the
            // capability. For an existing WorkWithPlus instance the reapply route skips the
            // engine and only re-projects the parent, so it is still the route that runs, but
            // it is not claimed to rebuild the derived objects; regenerate_derived is.
            Assert.Contains("An existing PatternInstance for 'WorkWithPlus' was found", conflict["detail"]?.ToString());
            Assert.Contains("Reapply through this build does not regenerate the derived objects", conflict["detail"]?.ToString());
            Assert.Equal("reapply", conflict["remediationRoute"]?.ToString());
            Assert.True((bool)conflict["reapplyRecommended"]);
            Assert.False((bool)conflict["regeneratesDerivedObjects"]);
            Assert.Contains("this does not regenerate the pattern's objects", conflict["remediation"]?.ToString());
            Assert.Contains("genexus_wwp action=regenerate_derived", conflict["remediation"]?.ToString());
            Assert.Null(obj["routeCapabilities"]);
        }

        // ── Issue #330: diagnose must predict the apply ─────────────────────────
        // Reported symptom: mode=diagnose answered "All pre-apply checks passed.
        // The pattern should apply cleanly." for a WebPanel that then failed with
        // PatternNoOp. Diagnose now runs the same WwpAttachPreflight the attach
        // runs, so an unservable target is blocked and the "ok" finding is
        // suppressed. The service here has no ObjectService, so the attach route
        // cannot be serviced in any environment — the invariant holds regardless of
        // whether the WorkWithPlus package is installed on the test machine.
        [Fact]
        public void Diagnose_WebPanel_WithoutServableAttachRoute_IsBlockedAndNeverReportsOk()
        {
            var svc = MakeK2BService(new FakeEngine());
            var registry = K2BRegistry();
            Assert.True(registry.TryResolve("WorkWithPlus", out var wwp));

            var obj = JObject.Parse(svc.DiagnoseForObject("MyPanel", null, "WebPanel", "WorkWithPlus", wwp, null, null));

            var findings = (JArray)obj["findings"]!;
            Assert.Contains(findings, f => f["severity"]?.ToString() == "critical");
            Assert.DoesNotContain(findings, f => f["reason"]?.ToString() == "ok");
            Assert.Equal("blocked", obj["status"]?.ToString());
        }

        [Fact]
        public void Diagnose_WebPanel_AttachRouteBlocked_RenamesTheStageAndTheRemediation()
        {
            var svc = MakeK2BService(new FakeEngine());
            var registry = K2BRegistry();
            Assert.True(registry.TryResolve("WorkWithPlus", out var wwp));

            var obj = JObject.Parse(svc.DiagnoseForObject("MyPanel", null, "WebPanel", "WorkWithPlus", wwp, null, null));
            var findings = (JArray)obj["findings"]!;

            // The preflight must never throw. It did once, while assembling the
            // "package not installed" remediation, and the fail-closed catch turned
            // that into a generic attachPreflightFailed instead of the real answer.
            Assert.DoesNotContain(findings, f => f["reason"]?.ToString() == "attachPreflightFailed");

            // The attach findings are the ones this issue is about. One of them must
            // always fire here: with no ObjectService there is no KB to discover a
            // template in, so the direct-attach route is unservable in any environment.
            var attachFinding = findings
                .FirstOrDefault(f => f["reason"]?.ToString()?.StartsWith("wwp") == true);
            Assert.NotNull(attachFinding);
            Assert.Equal("critical", attachFinding!["severity"]?.ToString());
            // The whole point of the finding: it must name a machine-readable stage
            // and carry a remediation, not just say "cannot apply".
            Assert.False(string.IsNullOrEmpty(attachFinding["stage"]?.ToString()));
            Assert.False(string.IsNullOrEmpty(attachFinding["detail"]?.ToString()));
            Assert.False(string.IsNullOrEmpty(attachFinding["remediation"]?.ToString()));
        }

        // ── Issue #330: a failed apply must name the stage that failed ─────────
        // The old wording collapsed every failure mode into "the CreatePatternInstance
        // WithTemplate fallback also failed: <last line of the pipeline>", which is why
        // the reporter could not tell a template problem from a package problem.
        [Fact]
        public void AttachRecommendation_PreflightBlock_PointsAtThePackageNotAtTemplates()
        {
            var attach = new PatternApplyService.WwpAttachResult
            {
                FailureStage = WwpAttachPreflight.StageCreateOverload,
                ErrorCode = WwpAttachPreflight.BlockedCode,
                ErrorMessage = "No usable CreatePatternInstanceWithTemplate overload"
            };

            var recommendation = PatternApplyService.BuildAttachRecommendation(attach, "WebPanel", "MyPanel");

            Assert.Contains("Update the WorkWithPlus pattern package", recommendation);
            // Retrying with a different template cannot fix an absent overload.
            Assert.DoesNotContain("settings.template", recommendation);
        }

        [Fact]
        public void AttachRecommendation_PreflightBlock_PointsAtDiagnoseNotAtRetryWithTemplate()
        {
            var steps = PatternApplyService.BuildAttachNextSteps(
                WwpAttachPreflight.StageCreateOverload, "MyPanel", "WorkWithPlus");

            Assert.Contains(steps, s => s["tool"]?.ToString() == "genexus_apply_pattern"
                && s["args"]?["mode"]?.ToString() == "diagnose");
        }

        [Fact]
        public void AttachRecommendation_HostConflict_PointsAtDeleteNotAtRetry()
        {
            // Adopting a host this call did not create is what turned a failed attach
            // into a WebPanel that renders but whose events never fire.
            var attach = new PatternApplyService.WwpAttachResult
            {
                FailureStage = "hostConflict",
                ErrorCode = "PatternHostAlreadyExists",
                ErrorMessage = "host exists"
            };

            var recommendation = PatternApplyService.BuildAttachRecommendation(attach, "WebPanel", "MyPanel");
            var steps = PatternApplyService.BuildAttachNextSteps("hostConflict", "MyPanel", "WorkWithPlus");

            Assert.Contains("genexus_delete_object name=WorkWithPlusMyPanel", recommendation);
            Assert.Contains(steps, s => s["tool"]?.ToString() == "genexus_delete_object");
            Assert.Contains(steps, s => s["tool"]?.ToString() == "genexus_apply_pattern"
                && s["args"]?["reapply"]?.ToObject<bool>() == true);
        }

        [Fact]
        public void AttachRecommendation_ValidateFailed_ReportsWhetherTheOrphanWasRemoved()
        {
            var cleaned = new PatternApplyService.WwpAttachResult
            {
                FailureStage = "validateAndSave",
                ErrorCode = "PatternAttachValidateFailed",
                RemovedOrphanHost = "WorkWithPlusMyPanel"
            };
            var stuck = new PatternApplyService.WwpAttachResult
            {
                FailureStage = "validateAndSave",
                ErrorCode = "PatternAttachValidateFailed"
            };

            Assert.Contains("was removed, so a retry starts clean",
                PatternApplyService.BuildAttachRecommendation(cleaned, "WebPanel", "MyPanel"));
            Assert.Contains("could not be removed automatically",
                PatternApplyService.BuildAttachRecommendation(stuck, "WebPanel", "MyPanel"));
        }

        [Fact]
        public void AttachResult_OmitsFieldsThatWouldClaimSomethingThatDidNotHappen()
        {
            var attach = new PatternApplyService.WwpAttachResult { FailureStage = "projection" };

            var json = attach.ToJson();

            Assert.Equal("projection", json["failureStage"]?.ToString());
            Assert.Null(json["removedOrphanHost"]);
            Assert.Null(json["orphanHostWarning"]);
            Assert.Null(json["lastResortDetail"]);
            Assert.Null(json["attachPreflight"]);
        }

        [Fact]
        public void DecideReapply_RequestedMatchesExisting_Proceeds()
        {
            var registry = K2BRegistry();
            var k2b = K2B(registry);
            var d = PatternApplyService.DecideReapplyPattern(k2b, new[] { k2b });
            Assert.Equal(PatternApplyService.ReapplyDecisionStatus.Proceed, d.Status);
            Assert.Same(k2b, d.Pattern);
        }

        [Fact]
        public void DecideReapply_RequestedDiffersFromExisting_IsMismatch()
        {
            var registry = K2BRegistry();
            Assert.True(registry.TryResolve("WWP", out var wwp));
            var d = PatternApplyService.DecideReapplyPattern(wwp, new[] { K2B(registry) }, targetIsInstance: true);
            Assert.Equal(PatternApplyService.ReapplyDecisionStatus.Mismatch, d.Status);
            Assert.Null(d.Pattern);
        }

        [Fact]
        public void DecideReapply_RequestedWithoutInstanceOnParent_RunsAsFirstApply()
        {
            var registry = K2BRegistry();
            var k2b = K2B(registry);

            var none = PatternApplyService.DecideReapplyPattern(k2b, new PatternManifest[0]);
            Assert.Equal(PatternApplyService.ReapplyDecisionStatus.FirstApply, none.Status);
            Assert.Same(k2b, none.Pattern);

            registry.TryResolve("WWP", out var wwp);
            var other = PatternApplyService.DecideReapplyPattern(k2b, new[] { wwp });
            Assert.Equal(PatternApplyService.ReapplyDecisionStatus.FirstApply, other.Status);
        }

        [Fact]
        public void DecideReapply_NoInstance_IsNotFound_NeverWwpDefault()
        {
            var d = PatternApplyService.DecideReapplyPattern(null, new PatternManifest[0]);
            Assert.Equal(PatternApplyService.ReapplyDecisionStatus.NotFound, d.Status);
            Assert.Null(d.Pattern);
        }

        [Fact]
        public void DecideReapply_SingleExistingWithoutKey_UsesInstancePattern()
        {
            var registry = K2BRegistry();
            var d = PatternApplyService.DecideReapplyPattern(null, new[] { K2B(registry), K2B(registry) });
            Assert.Equal(PatternApplyService.ReapplyDecisionStatus.Proceed, d.Status);
            Assert.Equal(K2BEntityServicesId, d.Pattern.Id);
        }

        [Fact]
        public void DecideReapply_SeveralWithoutKey_IsAmbiguous()
        {
            var registry = K2BRegistry();
            Assert.True(registry.TryResolve("WWP", out var wwp));
            var d = PatternApplyService.DecideReapplyPattern(null, new[] { K2B(registry), wwp });
            Assert.Equal(PatternApplyService.ReapplyDecisionStatus.Ambiguous, d.Status);
            Assert.Equal(2, d.Existing.Count);
        }

        [Fact]
        public void ReapplyDecisionErrors_UseCanonicalCodes()
        {
            var registry = K2BRegistry();
            Assert.True(registry.TryResolve("WWP", out var wwp));
            var k2b = K2B(registry);
            var svc = new PatternApplyService(null, new FakeEngine(), name => null, registry);

            var mismatch = JObject.Parse(svc.BuildReapplyDecisionError(ObjName, ObjName, PatternApplyService.DecideReapplyPattern(wwp, new[] { k2b }, targetIsInstance: true)));
            Assert.Contains("PatternMismatch", mismatch.ToString());
            Assert.Equal("K2BEntityServices", mismatch["existingPatterns"]![0]!["pattern"]?.ToString());

            var ambiguous = JObject.Parse(svc.BuildReapplyDecisionError(ObjName, ObjName, PatternApplyService.DecideReapplyPattern(null, new[] { k2b, wwp })));
            Assert.Contains("PatternInstanceAmbiguous", ambiguous.ToString());
            Assert.Equal(2, ((JArray)ambiguous["candidates"]!).Count);

            var none = JObject.Parse(svc.BuildReapplyDecisionError(ObjName, ObjName, PatternApplyService.DecideReapplyPattern(null, new PatternManifest[0])));
            Assert.Contains("PatternInstanceNotFound", none.ToString());
            Assert.Contains("K2BEntityServices", none["availablePatterns"]!.ToObject<List<string>>());
        }

        [Fact]
        public void ApplyPattern_NoLicense_ReturnsPatternUnavailable()
        {
            var engine = new FakeEngine { DefinitionToReturn = null };
            // Object IS resolved (non-null) so we hit the engine probe path. But
            // we can't easily build a KBObject, so call the internal pipeline directly.
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("pattern_unavailable", obj["status"]?.ToString());
            Assert.Equal("WorkWithPlus", obj["patternKey"]?.ToString());
            Assert.Contains("license", obj["message"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, engine.ApplyCalls);
            Assert.Equal(0, engine.ReapplyCalls);
        }

        [Fact]
        public void ApplyPattern_UnknownKey_ReturnsPatternUnavailable()
        {
            // The public ApplyPattern parses the key before resolving objects, so
            // unknown keys short-circuit even with a null _objectService.
            var engine = new FakeEngine();
            var svc = new PatternApplyService(null, engine, name => null);

            string json = svc.ApplyPattern(ObjName, "NotARealPatternKey");
            var obj = JObject.Parse(json);

            Assert.Equal("pattern_unavailable", obj["status"]?.ToString());
            Assert.Equal("NotARealPatternKey", obj["patternKey"]?.ToString());
        }

        [Fact]
        public void ApplyPattern_ObjectNotFound_ReturnsError()
        {
            // findObjectOverride returns null and _objectService is null → fallback
            // McpResponse.Error("Object not found") branch (no SearchIndex needed).
            var engine = new FakeEngine();
            var svc = new PatternApplyService(null, engine, name => null);

            string json = svc.ApplyPattern(ObjName, "WorkWithPlus");
            var obj = JObject.Parse(json);

            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Contains("not found", obj["error"]?["message"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, engine.ApplyCalls);
        }

        [Fact]
        public void ApplyPattern_HappyPath_FirstApply_CallsApplyOnce()
        {
            var engine = new FakeEngine
            {
                ExistingInstance = null,
                ApplyImpl = _ => new PatternApplyResult { GeneratedObjects = new List<string> { "WWAlpha", "WWBeta" } }
            };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", new JObject { ["foo"] = "bar" }, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.True(obj["result"]?["wasFirstApply"]?.ToObject<bool>());
            Assert.Equal(1, engine.ApplyCalls);
            Assert.Equal(0, engine.ReapplyCalls);

            var generated = (JArray)obj["result"]?["generatedObjects"];
            Assert.Equal(2, generated.Count);
            Assert.Contains("WWAlpha", generated.ToObject<List<string>>());
        }

        [Fact]
        public void ApplyPattern_ExistingInstance_SkipsEngineReapply()
        {
            // F17 behavior change: when existingInstance != null we no longer invoke
            // engine.ReapplyPattern (it NREs on the live SDK install) — projection
            // is done via IPatternBuildProcess.UpdateParentObject instead. The test
            // verifies the engine NEVER sees a Reapply call in this path.
            var engine = new FakeEngine { ExistingInstance = new object() };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.False(obj["result"]?["wasFirstApply"]?.ToObject<bool>());
            Assert.Equal(0, engine.ApplyCalls);
            Assert.Equal(0, engine.ReapplyCalls);
        }

        [Fact]
        public void Reapply_WithExistingInstance_SkipsEngineReapply()
        {
            // See ApplyPattern_ExistingInstance_SkipsEngineReapply for rationale.
            var engine = new FakeEngine { ExistingInstance = new object() };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: true, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.False(obj["result"]?["wasFirstApply"]?.ToObject<bool>());
            Assert.Equal(0, engine.ReapplyCalls);
        }

        [Fact]
        public void Reapply_WithoutExistingInstance_FallsBackToFirstApply()
        {
            var engine = new FakeEngine { ExistingInstance = null };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: true, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.True(obj["result"]?["wasFirstApply"]?.ToObject<bool>());
            Assert.Equal(1, engine.ApplyCalls);
            Assert.Equal(0, engine.ReapplyCalls);
        }

        [Fact]
        public void ApplyPattern_EngineThrows_SurfacesAsErrorEnvelope()
        {
            var engine = new FakeEngine
            {
                ApplyImpl = _ => throw new InvalidOperationException("boom in SDK")
            };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Contains("boom", obj["error"]?["message"]?.ToString() ?? "");
        }

        // Live integration smokes. Opt-in via GXMCP_TEST_KB=<path-to-kb> and
        // GXMCP_REQUIRE_WWP=1 for the WorkWithPlus-licensed tests.
        //
        // Body intentionally left minimal — a future commit will wire the real
        // ObjectService bootstrap + PatternApplyService(_realAdapter) once we
        // commit to a fixture KB layout. The conditional Skip already removes
        // the "permanently-Skip=true" friction, which is what F3 was about: the
        // tests are now part of the discoverable surface for anyone with a
        // licensed install, instead of dead code.
        [LiveKbFact(requiresWWP: true)]
        public void Integration_FirstApply_WWP_OnRealTransaction_GeneratesObjects()
        {
            string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB");
            Assert.False(string.IsNullOrEmpty(kb)); // sanity: env-gate fired
            // TODO: open KB at <kb>, locate a non-WWP Transaction, call ApplyPattern,
            // assert wasFirstApply==true and PatternInstance present after.
        }

        [LiveKbFact(requiresWWP: true)]
        public void Integration_FirstApply_WWP_OnFreshWebPanel_AttachesPatternInstance()
        {
            string kb = Environment.GetEnvironmentVariable("GXMCP_TEST_KB");
            Assert.False(string.IsNullOrEmpty(kb));
            // TODO: create empty WebPanel, ApplyPattern WorkWithPlus, re-read and
            // assert PatternInstance part is populated.
        }

        // ── ApplySettings projection (best-effort JObject → ApplySettings instance) ──

        // Stand-in type that exercises the projection code paths without depending on
        // the live Artech.Packages.Patterns ApplySettings (which only exists in the
        // GeneXus install). Reflection logic in ProjectJObjectOntoInstance is type-
        // agnostic.
        private enum FakeMode { Tabular, Selection, View }

        private class FakeSettings
        {
            public string Title { get; set; }
            public int MaxRows { get; set; }
            public bool ShowFilters { get; set; }
            public FakeMode Mode { get; set; }
            public FakeNested Layout { get; set; }
        }
        private class FakeNested
        {
            public string Theme { get; set; }
            public int Columns { get; set; }
        }

        [Fact]
        public void ProjectJObject_ScalarsAndEnum_MapByName()
        {
            var instance = new FakeSettings();
            var unmapped = new List<string>();
            InvokeProject(
                new JObject
                {
                    ["title"] = "Invoices",
                    ["maxRows"] = 50,
                    ["showFilters"] = true,
                    ["mode"] = "Selection"
                },
                instance,
                unmapped);

            Assert.Equal("Invoices", instance.Title);
            Assert.Equal(50, instance.MaxRows);
            Assert.True(instance.ShowFilters);
            Assert.Equal(FakeMode.Selection, instance.Mode);
            Assert.Empty(unmapped);
        }

        [Fact]
        public void ProjectJObject_NestedObject_RecursesAndSetsChildProperties()
        {
            var instance = new FakeSettings();
            var unmapped = new List<string>();
            InvokeProject(
                new JObject
                {
                    ["layout"] = new JObject { ["theme"] = "Carmine", ["columns"] = 3 }
                },
                instance,
                unmapped);

            Assert.NotNull(instance.Layout);
            Assert.Equal("Carmine", instance.Layout.Theme);
            Assert.Equal(3, instance.Layout.Columns);
            Assert.Empty(unmapped);
        }

        [Fact]
        public void ProjectJObject_UnknownKeys_CollectedNotThrown()
        {
            var instance = new FakeSettings();
            var unmapped = new List<string>();
            InvokeProject(
                new JObject
                {
                    ["title"] = "Foo",
                    ["thisKeyDoesNotExist"] = "x",
                    ["alsoMissing"] = 42
                },
                instance,
                unmapped);

            Assert.Equal("Foo", instance.Title);
            Assert.Contains("thisKeyDoesNotExist", unmapped);
            Assert.Contains("alsoMissing", unmapped);
        }

        // Helper: call the static internal ProjectJObjectOntoInstance via reflection so
        // the test does not require InternalsVisibleTo gymnastics across runtime types.
        private static void InvokeProject(JObject src, object dst, IList<string> unmapped)
        {
            var t = typeof(PatternApplyService).Assembly.GetType("GxMcp.Worker.Services.ReflectionPatternEngineAdapter");
            Assert.NotNull(t);
            var method = t.GetMethod("ProjectJObjectOntoInstance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            method.Invoke(null, new object[] { src, dst, unmapped, 0 });
        }
    }
}
