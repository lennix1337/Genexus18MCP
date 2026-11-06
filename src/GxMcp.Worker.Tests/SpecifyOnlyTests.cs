using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // lifecycle action=specify_only: what is refused, what is planned, what is mutated and what the
    // receipt says. The SDK is replaced by FakeEnvironment, which records every call in order.
    public class SpecifyOnlyTests
    {
        private static readonly Guid ObjectGuid = new Guid("6f1b1f0e-9a3c-4c52-8d0e-2f7a1c5b9d11");
        private static readonly DateTime Built = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

        private static SpecifyOnlyRequest Request(string expectedVersion = null)
            => new SpecifyOnlyRequest { Guid = ObjectGuid, ExpectedVersion = expectedVersion };

        private static SpecifyOnlyFacts Facts(
            string type = "Procedure", bool separate = true, bool envCopy = true, bool stale = false,
            string version = "100:root", bool found = true, bool copyApi = true, bool suppressible = true, bool specifier = true,
            bool inProcessDisabled = false, bool noTargetModel = false)
            => new SpecifyOnlyFacts
            {
                ObjectFound = found,
                Name = "SampleProc",
                TypeName = type,
                Version = version,
                SeparateEnvironment = separate,
                EnvironmentCopyExists = envCopy,
                DesignLastUpdate = stale ? Built.AddMinutes(5) : Built,
                EnvironmentLastUpdate = envCopy ? Built : (DateTime?)null,
                CopyApiAvailable = copyApi,
                GenerationSuppressible = suppressible,
                SpecifierAvailable = specifier,
                InProcessDisabled = inProcessDisabled,
                NoTargetModel = noTargetModel
            };

        private sealed class FakeEnvironment : ISpecifyOnlyEnvironment
        {
            public readonly List<string> Calls = new List<string>();
            public SpecifyOnlyFacts Current;
            public Action OnCopy = () => { };
            public Func<SpecifyOnlyRunResult> OnSpecify = () => new SpecifyOnlyRunResult
            {
                SpecifierInvoked = true,
                SpecifierReturned = true,
                GenerationSuppressed = true,
                SectionsObserved = new List<string> { "Specification" }
            };
            public int FilesTouched;
            public List<string> TouchedNames = new List<string>();

            public FakeEnvironment(SpecifyOnlyFacts facts) { Current = facts; }

            public SpecifyOnlyFacts ReadFacts(Guid guid)
            {
                Calls.Add("read");
                var c = Current;
                return new SpecifyOnlyFacts
                {
                    ObjectFound = c.ObjectFound, Name = c.Name, TypeName = c.TypeName, Version = c.Version,
                    SeparateEnvironment = c.SeparateEnvironment, EnvironmentCopyExists = c.EnvironmentCopyExists,
                    DesignLastUpdate = c.DesignLastUpdate, EnvironmentLastUpdate = c.EnvironmentLastUpdate,
                    CopyApiAvailable = c.CopyApiAvailable, GenerationSuppressible = c.GenerationSuppressible,
                    SpecifierAvailable = c.SpecifierAvailable
                };
            }

            public void CopyToEnvironment(Guid guid)
            {
                Calls.Add("copy");
                OnCopy();
            }

            public SpecifyOnlyRunResult Specify(Guid guid)
            {
                Calls.Add("specify");
                return OnSpecify();
            }

            public SpecifyOnlyProbe ProbeGeneratorOutput(DateTime startedUtc)
            {
                Calls.Add("probe");
                return new SpecifyOnlyProbe { Count = FilesTouched, Names = TouchedNames.ToList() };
            }

            public bool Mutated => Calls.Contains("copy") || Calls.Contains("specify");
        }

        private static FakeEnvironment NeverBuilt()
        {
            var env = new FakeEnvironment(Facts(envCopy: false));
            // A real copy exists afterwards AND carries the design object's timestamp: the executor re-reads it.
            env.OnCopy = () =>
            {
                env.Current.EnvironmentCopyExists = true;
                env.Current.EnvironmentLastUpdate = env.Current.DesignLastUpdate;
            };
            return env;
        }

        // ---------- planner ----------

        [Theory]
        [InlineData("Procedure", true)]
        [InlineData("procedure", true)]
        [InlineData("WebPanel", true)]
        [InlineData("Web Panel", true)]
        [InlineData("DataProvider", false)]
        [InlineData("SDPanel", false)]
        [InlineData("API", true)]
        [InlineData("Transaction", false)]
        [InlineData("Table", false)]
        [InlineData("Attribute", false)]
        [InlineData("Domain", false)]
        [InlineData("SDT", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_types_that_cannot_alter_a_reorganization_may_have_their_environment_copy_created(string type, bool allowed)
            => Assert.Equal(allowed, SpecifyOnlyPlanner.AllowsEnvironmentCopy(type));

        [Fact]
        public void An_object_that_does_not_exist_is_refused_before_anything_else()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(found: false));
            Assert.Equal(SpecifyOnlyCodes.ObjectNotFound, plan.Refusal.Code);
            Assert.Equal(SpecifyOnlySteps.Failed, plan.Steps.Single(s => s.Name == SpecifyOnlySteps.ResolveObject).State);
            Assert.All(plan.Steps.Where(s => s.Name != SpecifyOnlySteps.ResolveObject), s => Assert.Equal(SpecifyOnlySteps.Skipped, s.State));
        }

        [Fact]
        public void A_version_that_differs_from_the_expected_one_is_a_conflict()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request("99:root"), Facts(version: "100:root"));
            Assert.Equal(SpecifyOnlyCodes.VersionConflict, plan.Refusal.Code);
            Assert.Contains("99:root", plan.Refusal.Message);
            Assert.Contains("100:root", plan.Refusal.Message);
            Assert.Equal(SpecifyOnlySteps.Failed, plan.Steps.Single(s => s.Name == SpecifyOnlySteps.CheckConcurrency).State);
        }

        [Fact]
        public void A_matching_or_absent_expected_version_proceeds()
        {
            Assert.Null(SpecifyOnlyPlanner.Plan(Request("100:root"), Facts()).Refusal);
            Assert.Null(SpecifyOnlyPlanner.Plan(Request(null), Facts()).Refusal);
            Assert.Null(SpecifyOnlyPlanner.Plan(Request("  "), Facts()).Refusal);
        }

        [Theory]
        [InlineData("100:root", "100:root", true)]
        [InlineData("100:root:0123abcd", "100:root", true)]   // the token genexus_read hands out for a part
        [InlineData("100:root:0123abcd:ffff", "100:root", false)] // exactly one extra segment, never two
        [InlineData("100:root:", "100:root", false)]               // an empty fingerprint is not a token
        [InlineData("101:root", "100:root", false)]
        [InlineData("100:rootx", "100:root", false)]           // a longer parent identity is another object version
        [InlineData("100:root0123abcd", "100:root", false)]    // not a ':' extension
        [InlineData("100", "100:root", false)]
        [InlineData("  100:root  ", "100:root", true)]
        [InlineData("", "100:root", true)]
        [InlineData(null, "100:root", true)]
        [InlineData("100:root", null, false)]
        public void An_expected_version_matches_the_object_version_or_a_part_token_that_extends_it(string expected, string current, bool matches)
            => Assert.Equal(matches, SpecifyOnlyPlanner.VersionMatches(expected, current));

        [Fact]
        public void The_receipt_agrees_with_the_planner_about_a_part_token()
        {
            var run = new SpecifyOnlyExecutor(new FakeEnvironment(Facts(version: "100:root"))).Execute(Request("100:root:f125a128"));
            Assert.Null(run.Plan.Refusal);
            Assert.True((bool)SpecifyOnlyReceiptBuilder.Build(Request("100:root:f125a128"), run, new JArray())["version"]["matchesExpected"]);
        }

        [Fact]
        public void The_specifier_and_the_generation_switch_must_both_exist()
        {
            Assert.Equal(SpecifyOnlyCodes.SpecifierUnavailable,
                SpecifyOnlyPlanner.Plan(Request(), Facts(specifier: false)).Refusal.Code);
            Assert.Equal(SpecifyOnlyCodes.GenerationNotSuppressible,
                SpecifyOnlyPlanner.Plan(Request(), Facts(suppressible: false)).Refusal.Code);
        }

        [Fact]
        public void The_in_process_pipeline_switched_off_is_refused_so_no_external_full_build_can_run()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(inProcessDisabled: true));
            Assert.Equal(SpecifyOnlyCodes.InProcessDisabled, plan.Refusal.Code);
            Assert.Contains("GXMCP_INPROCESS_BUILD", plan.Refusal.Message);
            Assert.Equal(SpecifyOnlySteps.Skipped, plan.Steps.Single(s => s.Name == SpecifyOnlySteps.Specify).State);
        }

        [Fact]
        public void A_kb_that_reports_no_environment_model_is_refused_rather_than_specified_against_nothing()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(noTargetModel: true));
            Assert.Equal(SpecifyOnlyCodes.EnvironmentUnavailable, plan.Refusal.Code);
            Assert.Equal(SpecifyOnlySteps.Skipped, plan.Steps.Single(s => s.Name == SpecifyOnlySteps.Specify).State);
        }

        [Fact]
        public void Refusals_are_decided_in_a_fixed_order_so_the_first_cause_is_the_one_reported()
        {
            // Everything wrong at once; fix one cause at a time and the next one in line appears.
            var all = Facts(type: "Transaction", envCopy: false, specifier: false, suppressible: false,
                inProcessDisabled: true, noTargetModel: true);
            Assert.Equal(SpecifyOnlyCodes.VersionConflict, SpecifyOnlyPlanner.Plan(Request("1:root"), all).Refusal.Code);
            Assert.Equal(SpecifyOnlyCodes.InProcessDisabled, SpecifyOnlyPlanner.Plan(Request(), all).Refusal.Code);
            all.InProcessDisabled = false;
            Assert.Equal(SpecifyOnlyCodes.EnvironmentUnavailable, SpecifyOnlyPlanner.Plan(Request(), all).Refusal.Code);
            all.NoTargetModel = false;
            Assert.Equal(SpecifyOnlyCodes.SpecifierUnavailable, SpecifyOnlyPlanner.Plan(Request(), all).Refusal.Code);
            all.SpecifierAvailable = true;
            Assert.Equal(SpecifyOnlyCodes.GenerationNotSuppressible, SpecifyOnlyPlanner.Plan(Request(), all).Refusal.Code);
            all.GenerationSuppressible = true;
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyUnsafe, SpecifyOnlyPlanner.Plan(Request(), all).Refusal.Code);
        }

        [Theory]
        [InlineData("SDPanel", false)]
        [InlineData("sdpanel", false)]
        [InlineData("SD Panel", false)]
        [InlineData("Procedure", true)]
        [InlineData("WebPanel", true)]
        [InlineData("Transaction", true)]
        [InlineData("", true)]
        [InlineData(null, true)]
        public void Smart_devices_main_objects_are_the_only_types_specify_only_never_runs(string type, bool supported)
            => Assert.Equal(supported, SpecifyOnlyPlanner.IsTypeSupported(type));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_sdpanel_is_refused_whatever_the_state_of_its_copy(bool copyExists)
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(type: "SDPanel", envCopy: copyExists));
            Assert.Equal(SpecifyOnlyCodes.TypeNotSupported, plan.Refusal.Code);
            Assert.Contains("offline database", plan.Refusal.Message);
            Assert.False(plan.NeedsEnvironmentCopy);
            Assert.Equal(SpecifyOnlySteps.Skipped, plan.Steps.Single(s => s.Name == SpecifyOnlySteps.Specify).State);
        }

        [Fact]
        public void The_type_refusal_comes_after_the_version_check_and_before_the_environment_checks()
        {
            Assert.Equal(SpecifyOnlyCodes.VersionConflict, SpecifyOnlyPlanner.Plan(Request("1:root"), Facts(type: "SDPanel")).Refusal.Code);
            Assert.Equal(SpecifyOnlyCodes.TypeNotSupported,
                SpecifyOnlyPlanner.Plan(Request(), Facts(type: "SDPanel", inProcessDisabled: true, noTargetModel: true, specifier: false)).Refusal.Code);
        }

        [Fact]
        public void A_current_copy_needs_no_copy_step()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts());
            Assert.Equal(SpecifyOnlyCopyState.Current, plan.CopyState);
            Assert.False(plan.NeedsEnvironmentCopy);
            Assert.Equal(SpecifyOnlySteps.Skipped, plan.Steps.Single(s => s.Name == SpecifyOnlySteps.CopyToEnvironment).State);
        }

        [Fact]
        public void A_kb_without_a_separate_environment_needs_no_copy()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(separate: false, envCopy: false));
            Assert.Equal(SpecifyOnlyCopyState.NotApplicable, plan.CopyState);
            Assert.Null(plan.Refusal);
            Assert.False(plan.NeedsEnvironmentCopy);
        }

        [Fact]
        public void A_missing_copy_of_a_safe_type_is_planned_as_a_creation()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(envCopy: false));
            Assert.Equal(SpecifyOnlyCopyState.Missing, plan.CopyState);
            Assert.True(plan.NeedsEnvironmentCopy);
            var copy = plan.Steps.Single(s => s.Name == SpecifyOnlySteps.CopyToEnvironment);
            Assert.True(copy.Mutates);
            Assert.Equal(SpecifyOnlySteps.Planned, copy.State);
        }

        [Fact]
        public void A_stale_copy_of_a_safe_type_is_planned_as_a_refresh()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(stale: true));
            Assert.Equal(SpecifyOnlyCopyState.Stale, plan.CopyState);
            Assert.True(plan.NeedsEnvironmentCopy);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_missing_or_stale_copy_of_a_type_that_can_change_a_reorganization_is_refused(bool stale)
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(type: "Transaction", envCopy: stale, stale: stale));
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyUnsafe, plan.Refusal.Code);
            Assert.False(plan.NeedsEnvironmentCopy);
            Assert.Contains("reorganization", plan.Refusal.Message);
        }

        [Fact]
        public void A_current_copy_of_any_type_is_specified_without_a_copy()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(type: "Transaction"));
            Assert.Null(plan.Refusal);
            Assert.False(plan.NeedsEnvironmentCopy);
        }

        [Fact]
        public void Without_the_per_object_copy_in_the_sdk_the_copy_is_refused_rather_than_guessed()
        {
            var plan = SpecifyOnlyPlanner.Plan(Request(), Facts(envCopy: false, copyApi: false));
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyUnavailable, plan.Refusal.Code);
        }

        // ---------- executor ----------

        [Fact]
        public void A_never_built_object_gets_its_copy_then_is_specified_in_that_order()
        {
            var env = NeverBuilt();
            var run = new SpecifyOnlyExecutor(env).Execute(Request());

            // plan, (copy), verify the copy, specify, re-read the version, probe the generator folder
            Assert.Equal(new[] { "read", "copy", "read", "specify", "read", "probe" }, env.Calls);
            Assert.True(run.CopyPerformed);
            Assert.Equal("created", run.CopyAction);
            Assert.Null(run.Failure);

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.True((bool)receipt["specified"]);
            Assert.Equal("Specified", (string)receipt["outcome"]);
            Assert.Equal(new[] { "resolve-object", "check-concurrency", "copy-to-environment", "specify", "verify-version" },
                receipt["stepsExecuted"].Select(t => (string)t).ToArray());
            Assert.False((bool)receipt["designObjectVersionChanged"]);
        }

        [Fact]
        public void A_stale_copy_is_reported_as_refreshed()
        {
            var env = new FakeEnvironment(Facts(stale: true));
            env.OnCopy = () => env.Current.EnvironmentLastUpdate = env.Current.DesignLastUpdate;
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            Assert.Equal("refreshed", run.CopyAction);
            Assert.Equal("refreshed", (string)SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray())["environmentCopy"]["action"]);
        }

        [Fact]
        public void A_current_object_is_specified_without_touching_the_environment_model()
        {
            var env = new FakeEnvironment(Facts());
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            Assert.DoesNotContain("copy", env.Calls);
            Assert.Contains("specify", env.Calls);
            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.Equal(new[] { "specification-output" }, receipt["persisted"].Select(p => (string)p["kind"]).ToArray());
        }

        [Fact]
        public void A_refused_request_mutates_nothing()
        {
            foreach (var facts in new[]
            {
                Facts(found: false), Facts(type: "Transaction", envCopy: false), Facts(specifier: false), Facts(suppressible: false)
            })
            {
                var env = new FakeEnvironment(facts);
                var run = new SpecifyOnlyExecutor(env).Execute(Request());
                Assert.False(env.Mutated);
                Assert.NotNull(run.Plan.Refusal);
                var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
                Assert.Equal("Refused", (string)receipt["outcome"]);
                Assert.False((bool)receipt["specified"]);
                Assert.Empty(receipt["persisted"]);
                Assert.False((bool)receipt["rollback"]["applicable"]);
                Assert.Equal(run.Plan.Refusal.Code, (string)receipt["refusal"]["code"]);
                Assert.Equal(run.Plan.Refusal.Code, (string)receipt["diagnostics"][0]["code"]);
            }
        }

        [Fact]
        public void An_unknown_object_has_an_unknown_environment_copy_state_not_a_not_applicable_one()
        {
            var run = new SpecifyOnlyExecutor(new FakeEnvironment(Facts(found: false))).Plan(Request());
            var receipt = SpecifyOnlyReceiptBuilder.BuildDryRun(Request(), run);
            Assert.Equal("unknown", (string)receipt["environmentCopy"]["state"]);
            Assert.Equal("WouldRefuse", (string)receipt["outcome"]);
        }

        [Fact]
        public void A_version_conflict_mutates_nothing_even_for_a_never_built_object()
        {
            var env = NeverBuilt();
            var run = new SpecifyOnlyExecutor(env).Execute(Request("1:root"));
            Assert.False(env.Mutated);
            Assert.Equal(SpecifyOnlyCodes.VersionConflict, run.Plan.Refusal.Code);
        }

        [Fact]
        public void A_dry_run_plans_the_steps_and_mutates_nothing()
        {
            var env = NeverBuilt();
            var run = new SpecifyOnlyExecutor(env).Plan(Request());
            Assert.False(env.Mutated);
            Assert.Equal(new[] { "read" }, env.Calls);

            var receipt = SpecifyOnlyReceiptBuilder.BuildDryRun(Request(), run);
            Assert.True((bool)receipt["dryRun"]);
            Assert.Equal("WouldSpecify", (string)receipt["outcome"]);
            Assert.Equal(JTokenType.Null, receipt["specified"].Type);
            Assert.Empty(receipt["persisted"]);
            Assert.Empty(receipt["stepsExecuted"]);
            Assert.Equal(new[] { "environment-copy", "specification-output" },
                receipt["wouldPersist"].Select(p => (string)p["kind"]).ToArray());
            Assert.Equal("create", (string)receipt["wouldPersist"][0]["action"]);
            Assert.Equal(new[] { "resolve-object", "check-concurrency", "copy-to-environment", "specify", "verify-version" },
                receipt["steps"].Select(s => (string)s["name"]).ToArray());
            Assert.Equal("planned", (string)receipt["steps"].Single(s => (string)s["name"] == "specify")["state"]);
            Assert.False((bool)receipt["evidence"]["mutationPerformed"]);
            Assert.Equal("missing", (string)receipt["environmentCopy"]["state"]);
            Assert.Equal("wouldCreateOrRefresh", (string)receipt["environmentCopy"]["action"]);
        }

        [Fact]
        public void A_dry_run_that_would_be_refused_says_so_with_the_reason()
        {
            var env = new FakeEnvironment(Facts(type: "Transaction", envCopy: false));
            var run = new SpecifyOnlyExecutor(env).Plan(Request());
            var receipt = SpecifyOnlyReceiptBuilder.BuildDryRun(Request(), run);
            Assert.Equal("WouldRefuse", (string)receipt["outcome"]);
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyUnsafe, (string)receipt["refusal"]["code"]);
            Assert.Empty(receipt["wouldPersist"]);
            Assert.False(env.Mutated);
        }

        [Fact]
        public void Every_receipt_lists_the_steps_the_operation_never_runs()
        {
            var run = new SpecifyOnlyExecutor(new FakeEnvironment(Facts())).Execute(Request());
            foreach (var receipt in new[]
            {
                SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray()),
                SpecifyOnlyReceiptBuilder.BuildDryRun(Request(), run)
            })
            {
                Assert.Equal(new[] { "generate", "compile", "reorganize", "deploy", "execute", "test" },
                    receipt["stepsNotExecuted"].Select(s => (string)s["step"]).ToArray());
                Assert.All(receipt["stepsNotExecuted"], s => Assert.False(string.IsNullOrEmpty((string)s["reason"])));
            }
        }

        [Fact]
        public void A_copy_the_sdk_rejects_stops_the_sequence_before_the_specifier()
        {
            var env = NeverBuilt();
            env.OnCopy = () => throw new InvalidOperationException("copy refused");
            var run = new SpecifyOnlyExecutor(env).Execute(Request());

            Assert.DoesNotContain("specify", env.Calls);
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyFailed, run.Failure.Code);
            Assert.True(run.CopyAttempted);
            Assert.False(run.CopyVerified);
            Assert.False(run.CopyPerformed);
            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.Equal("ExecutionFailed", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            // The reason is the copy's own, with its code, not the generic "the specifier did not run".
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyFailed, (string)receipt["diagnostics"][0]["code"]);
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyFailed, (string)receipt["refusal"]["code"]);
            // The SDK call was made, so the receipt does not claim "nothing was persisted": it names the attempt as unverified.
            var persisted = Assert.Single(receipt["persisted"]);
            Assert.Equal("environment-copy", (string)persisted["kind"]);
            Assert.False((bool)persisted["verified"]);
            Assert.False(string.IsNullOrEmpty((string)persisted["note"]));
            Assert.Equal("attempted-unverified", (string)receipt["environmentCopy"]["action"]);
            Assert.True((bool)receipt["rollback"]["applicable"]);
            Assert.False((bool)receipt["rollback"]["performed"]);
            Assert.Equal("failed", (string)receipt["steps"].Single(s => (string)s["name"] == "copy-to-environment")["state"]);
            Assert.Equal("skipped", (string)receipt["steps"].Single(s => (string)s["name"] == "specify")["state"]);
        }

        [Fact]
        public void A_copy_call_that_leaves_no_copy_behind_is_a_failure_not_a_success()
        {
            var env = NeverBuilt();
            env.OnCopy = () => { /* returns, but the environment model still has nothing */ };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            Assert.DoesNotContain("specify", env.Calls);
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyFailed, run.Failure.Code);
            Assert.Contains("no copy", run.Failure.Message); // the missing copy is named as such, not as a stale one
            Assert.True(run.CopyAttempted);
            Assert.False(run.CopyVerified);
            Assert.False(run.CopyPerformed);
        }

        [Fact]
        public void A_copy_that_exists_but_is_still_older_than_the_design_object_is_a_failure()
        {
            var env = NeverBuilt();
            env.OnCopy = () => env.Current.EnvironmentCopyExists = true; // the timestamp is never brought up to date
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            Assert.DoesNotContain("specify", env.Calls);
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyFailed, run.Failure.Code);
            Assert.Contains("older", run.Failure.Message);
            Assert.False(run.CopyVerified);
        }

        [Fact]
        public void A_stale_copy_that_stays_stale_after_the_refresh_is_a_failure()
        {
            var env = new FakeEnvironment(Facts(stale: true));
            env.OnCopy = () => { /* the refresh returns but changes nothing */ };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            Assert.DoesNotContain("specify", env.Calls);
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyFailed, run.Failure.Code);
            Assert.Equal("refreshed", run.CopyAction);
            Assert.False(run.CopyPerformed);
        }

        [Fact]
        public void A_copy_that_cannot_be_confirmed_because_the_reread_fails_stops_before_the_specifier()
        {
            var inner = NeverBuilt();
            var run = new SpecifyOnlyExecutor(new RereadFailsEnvironment(inner, failFromRead: 2)).Execute(Request());
            Assert.Equal(new[] { "read", "copy" }, inner.Calls);
            Assert.True(run.CopyAttempted);
            Assert.False(run.CopyVerified);
            Assert.False(run.CopyPerformed);
            Assert.Equal(SpecifyOnlyCodes.ExecutionFailed, run.Failure.Code);
            Assert.Equal(SpecifyOnlySteps.Failed, run.Plan.Steps.Single(s => s.Name == SpecifyOnlySteps.CopyToEnvironment).State);
            Assert.Equal(SpecifyOnlySteps.Skipped, run.Plan.Steps.Single(s => s.Name == SpecifyOnlySteps.Specify).State);
        }

        // Delegates to a FakeEnvironment but throws on the Nth and later ReadFacts calls.
        private sealed class RereadFailsEnvironment : ISpecifyOnlyEnvironment
        {
            private readonly FakeEnvironment _inner;
            private readonly int _failFromRead;
            private int _reads;

            public RereadFailsEnvironment(FakeEnvironment inner, int failFromRead) { _inner = inner; _failFromRead = failFromRead; }

            public SpecifyOnlyFacts ReadFacts(Guid guid)
            {
                if (++_reads >= _failFromRead) throw new InvalidOperationException("kb went away");
                return _inner.ReadFacts(guid);
            }

            public void CopyToEnvironment(Guid guid) => _inner.CopyToEnvironment(guid);
            public SpecifyOnlyRunResult Specify(Guid guid) => _inner.Specify(guid);
            public SpecifyOnlyProbe ProbeGeneratorOutput(DateTime startedUtc) => _inner.ProbeGeneratorOutput(startedUtc);
        }

        [Fact]
        public void A_specifier_that_throws_is_reported_after_the_copy_that_already_happened()
        {
            var env = NeverBuilt();
            env.OnSpecify = () => throw new InvalidOperationException("boom");
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());

            Assert.Equal("ExecutionFailed", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            // The steps tell where it stopped: the copy is done, the specifier failed, nothing after it ran.
            Assert.Equal("failed", (string)receipt["steps"].Single(s => (string)s["name"] == "specify")["state"]);
            Assert.Equal("skipped", (string)receipt["steps"].Single(s => (string)s["name"] == "verify-version")["state"]);
            Assert.Equal(new[] { "resolve-object", "check-concurrency", "copy-to-environment" },
                receipt["stepsExecuted"].Select(t => (string)t).ToArray());
            // The copy was written and the receipt must not hide it.
            Assert.Equal(new[] { "environment-copy" }, receipt["persisted"].Select(p => (string)p["kind"]).ToArray());
            Assert.True((bool)receipt["rollback"]["applicable"]);
            Assert.False((bool)receipt["rollback"]["performed"]);
            Assert.False(string.IsNullOrEmpty((string)receipt["rollback"]["reason"]));
        }

        [Fact]
        public void Specification_errors_make_the_object_not_specified_and_stay_itemized()
        {
            var env = new FakeEnvironment(Facts());
            env.OnSpecify = () => new SpecifyOnlyRunResult
            {
                SpecifierInvoked = true, SpecifierReturned = false, GenerationSuppressed = true,
                SectionsObserved = new List<string> { "Specification" }
            };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            var sdk = new JArray(new JObject { ["severity"] = "error", ["code"] = "spc0056", ["message"] = "Variable &x is not defined." });

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, sdk);
            Assert.Equal("SpecificationFailed", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            Assert.Equal("spc0056", (string)receipt["diagnostics"][0]["code"]);
        }

        [Fact]
        public void Warnings_alone_do_not_stop_an_object_from_being_specified()
        {
            var run = new SpecifyOnlyExecutor(new FakeEnvironment(Facts())).Execute(Request());
            var sdk = new JArray(new JObject { ["severity"] = "warning", ["code"] = "spc0022", ["message"] = "Unused variable." });
            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, sdk);
            Assert.True((bool)receipt["specified"]);
            Assert.Equal("spc0022", (string)receipt["diagnostics"][0]["code"]);
        }

        [Fact]
        public void An_object_edited_while_it_was_being_specified_is_not_certified()
        {
            var env = new FakeEnvironment(Facts(version: "100:root"));
            env.OnSpecify = () =>
            {
                env.Current.Version = "101:root"; // somebody saved the object during the specification
                return new SpecifyOnlyRunResult
                {
                    SpecifierInvoked = true, SpecifierReturned = true, GenerationSuppressed = true,
                    SectionsObserved = new List<string> { "Specification" }
                };
            };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            Assert.True(run.VersionChanged);

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.Equal("ConcurrentChange", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            Assert.True((bool)receipt["designObjectVersionChanged"]);
            Assert.False((bool)receipt["version"]["unchangedDuringRun"]);
            Assert.Equal("100:root", (string)receipt["version"]["current"]);
            Assert.Equal("101:root", (string)receipt["version"]["after"]);
            Assert.Contains(receipt["diagnostics"], d => (string)d["code"] == SpecifyOnlyCodes.ConcurrentChange);
        }

        [Fact]
        public void The_receipt_reports_the_version_the_caller_expected_and_the_one_it_found()
        {
            var run = new SpecifyOnlyExecutor(new FakeEnvironment(Facts(version: "100:root"))).Execute(Request("100:root"));
            var version = SpecifyOnlyReceiptBuilder.Build(Request("100:root"), run, new JArray())["version"];
            Assert.Equal("100:root", (string)version["expected"]);
            Assert.Equal("100:root", (string)version["current"]);
            Assert.True((bool)version["matchesExpected"]);
            Assert.True((bool)version["unchangedDuringRun"]);
        }

        // ---------- the "nothing was generated" evidence ----------

        [Fact]
        public void No_generation_is_proven_only_when_the_switch_was_off_no_such_section_opened_and_no_file_was_written()
        {
            var clean = new SpecifyOnlyExecutor(new FakeEnvironment(Facts())).Execute(Request());
            Assert.True(SpecifyOnlyReceiptBuilder.NoGenerationObserved(clean));
            var evidence = SpecifyOnlyReceiptBuilder.Build(Request(), clean, new JArray())["evidence"];
            Assert.True((bool)evidence["noGenerationObserved"]);
            Assert.True((bool)evidence["generationSuppressed"]);
            Assert.Equal(0, (int)evidence["generatorOutputTouched"]);
            Assert.Equal(new[] { "Specification" }, evidence["sdkSectionsObserved"].Select(s => (string)s).ToArray());
        }

        [Theory]
        [InlineData("generation")]
        [InlineData("compilation")]
        [InlineData("files")]
        [InlineData("notSuppressed")]
        public void Any_sign_of_generation_withdraws_the_proof(string sign)
        {
            var env = new FakeEnvironment(Facts());
            env.OnSpecify = () => new SpecifyOnlyRunResult
            {
                SpecifierInvoked = true, SpecifierReturned = true,
                GenerationSuppressed = sign != "notSuppressed",
                SectionsObserved = new List<string>
                {
                    "Specification",
                    sign == "generation" ? "Default (.NET Framework) Generation" : sign == "compilation" ? "Default Compilation" : "Specification"
                }
            };
            env.FilesTouched = sign == "files" ? 3 : 0;
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            Assert.False(SpecifyOnlyReceiptBuilder.NoGenerationObserved(run));
        }

        [Fact]
        public void A_probe_that_cannot_read_the_generator_folder_does_not_claim_proof()
        {
            var env = new FakeEnvironment(Facts()) { FilesTouched = -1 };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            Assert.False(SpecifyOnlyReceiptBuilder.NoGenerationObserved(run));
            Assert.Equal(JTokenType.Null,
                SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray())["evidence"]["generatorOutputTouched"].Type);
        }

        [Theory]
        [InlineData("Specification", false)]
        [InlineData("Default (.NET Framework) Generation", true)]
        [InlineData("Default (.NET Framework) Compilation", true)]
        [InlineData("Reorganization", true)]
        [InlineData("Deploy to Cloud", true)]
        [InlineData("Impact Analysis", true)]
        [InlineData("Creation Analysis", true)]
        [InlineData("Copy Model", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Section_names_that_belong_to_later_build_steps_are_recognized(string section, bool later)
            => Assert.Equal(later, SpecifyOnlyReceiptBuilder.IsGenerationOrLaterSection(section));

        // ---------- the verdict ----------

        private static SpecifyOnlyRunResult Spec(Action<SpecifyOnlyRunResult> tweak = null)
        {
            var result = new SpecifyOnlyRunResult
            {
                SpecifierInvoked = true, SpecifierReturned = true, GenerationSuppressed = true,
                SectionsObserved = new List<string> { "Specification" }
            };
            tweak?.Invoke(result);
            return result;
        }

        private static SpecifyOnlyRun RunWith(Func<SpecifyOnlyRunResult> specify)
        {
            var env = new FakeEnvironment(Facts()) { OnSpecify = specify };
            return new SpecifyOnlyExecutor(env).Execute(Request());
        }

        [Fact]
        public void A_clean_run_is_the_only_one_judged_specified()
        {
            var verdict = SpecifyOnlyJudge.Judge(RunWith(() => Spec()), specifierErrors: false);
            Assert.Equal(SpecifyOnlyVerdictKind.Specified, verdict.Kind);
            Assert.True(verdict.Succeeded);
            Assert.False(verdict.OwnDiagnostic);
        }

        [Fact]
        public void A_generation_section_fails_the_run_even_when_the_specifier_says_it_succeeded()
        {
            var run = RunWith(() => Spec(r => r.SectionsObserved.Add("Default (.NET Framework) Generation")));
            var verdict = SpecifyOnlyJudge.Judge(run, specifierErrors: false);
            Assert.Equal(SpecifyOnlyVerdictKind.GenerationObserved, verdict.Kind);
            Assert.Equal(SpecifyOnlyCodes.GenerationObserved, verdict.Code);
            Assert.True(verdict.OwnDiagnostic);

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.Equal("GenerationObserved", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            Assert.Equal(SpecifyOnlyCodes.GenerationObserved, (string)receipt["diagnostics"][0]["code"]);
            var generate = receipt["stepsNotExecuted"].Single(s => (string)s["step"] == "generate");
            Assert.Equal("Default (.NET Framework) Generation", (string)generate["contradictedBy"][0]);
            Assert.False((bool)receipt["evidence"]["noGenerationObserved"]);
        }

        [Fact]
        public void An_object_the_specifier_skipped_as_unreachable_is_not_reported_specified()
        {
            var run = RunWith(() => Spec(r => r.ObjectUnreachable = true));
            Assert.Equal(SpecifyOnlyVerdictKind.Unreachable, SpecifyOnlyJudge.Judge(run, specifierErrors: false).Kind);

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.Equal("Unreachable", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            Assert.Equal(SpecifyOnlyCodes.ObjectUnreachable, (string)receipt["diagnostics"][0]["code"]);
            Assert.Contains("spc0217", (string)receipt["diagnostics"][0]["message"]);
        }

        [Fact]
        public void Itemized_specifier_errors_are_the_diagnostics_so_the_judge_adds_none_of_its_own()
        {
            var run = RunWith(() => Spec(r => r.SpecifierReturned = false));

            var itemized = SpecifyOnlyJudge.Judge(run, specifierErrors: true);
            Assert.Equal(SpecifyOnlyVerdictKind.SpecificationFailed, itemized.Kind);
            Assert.False(itemized.OwnDiagnostic);

            // A failure return with no itemized line still has to say something.
            var silent = SpecifyOnlyJudge.Judge(run, specifierErrors: false);
            Assert.Equal(SpecifyOnlyVerdictKind.SpecificationFailed, silent.Kind);
            Assert.True(silent.OwnDiagnostic);
            Assert.Equal(SpecifyOnlyCodes.SpecificationFailed, silent.Code);
        }

        [Fact]
        public void The_judge_reports_the_most_fundamental_problem_first()
        {
            var run = RunWith(() => Spec(r =>
            {
                r.SectionsObserved.Add("Default Compilation");
                r.ObjectUnreachable = true;
                r.SpecifierReturned = false;
            }));
            run.VersionAfter = "101:root"; // and the object changed meanwhile

            Assert.Equal(SpecifyOnlyVerdictKind.GenerationObserved, SpecifyOnlyJudge.Judge(run, specifierErrors: true).Kind);
            run.Specify.SectionsObserved.Remove("Default Compilation");
            Assert.Equal(SpecifyOnlyVerdictKind.Unreachable, SpecifyOnlyJudge.Judge(run, specifierErrors: true).Kind);
            run.Specify.ObjectUnreachable = false;
            Assert.Equal(SpecifyOnlyVerdictKind.SpecificationFailed, SpecifyOnlyJudge.Judge(run, specifierErrors: true).Kind);
            Assert.Equal(SpecifyOnlyVerdictKind.SpecificationFailed, SpecifyOnlyJudge.Judge(run, specifierErrors: false).Kind);
            run.Specify.SpecifierReturned = true;
            Assert.Equal(SpecifyOnlyVerdictKind.ConcurrentChange, SpecifyOnlyJudge.Judge(run, specifierErrors: false).Kind);
        }

        [Fact]
        public void A_refused_request_and_an_unreadable_object_have_their_own_verdicts_and_still_render_a_receipt()
        {
            var refused = new SpecifyOnlyExecutor(new FakeEnvironment(Facts(found: false))).Execute(Request());
            var refusedVerdict = SpecifyOnlyJudge.Judge(refused, specifierErrors: false);
            Assert.Equal(SpecifyOnlyVerdictKind.Refused, refusedVerdict.Kind);
            Assert.Equal(SpecifyOnlyCodes.ObjectNotFound, refusedVerdict.Code);

            var unreadable = new SpecifyOnlyExecutor(new RereadFailsEnvironment(new FakeEnvironment(Facts()), failFromRead: 1)).Execute(Request());
            Assert.Null(unreadable.Plan);
            var failedVerdict = SpecifyOnlyJudge.Judge(unreadable, specifierErrors: false);
            Assert.Equal(SpecifyOnlyVerdictKind.ExecutionFailed, failedVerdict.Kind);
            Assert.Equal(SpecifyOnlyCodes.ExecutionFailed, failedVerdict.Code);

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), unreadable, new JArray());
            Assert.Equal("ExecutionFailed", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            Assert.Empty(receipt["steps"]);
            Assert.False((bool)receipt["rollback"]["applicable"]);
        }

        [Fact]
        public void A_specifier_that_never_ran_is_an_execution_failure_not_a_pass()
        {
            var run = RunWith(() => Spec(r => r.SpecifierInvoked = false));
            var verdict = SpecifyOnlyJudge.Judge(run, specifierErrors: false);
            Assert.Equal(SpecifyOnlyVerdictKind.ExecutionFailed, verdict.Kind);
            Assert.False(SpecifyOnlyReceiptBuilder.NoGenerationObserved(run));
        }

        [Fact]
        public void A_post_run_reread_that_fails_leaves_the_result_unverified_not_specified()
        {
            var inner = new FakeEnvironment(Facts());
            var run = new SpecifyOnlyExecutor(new RereadFailsEnvironment(inner, failFromRead: 2)).Execute(Request());

            Assert.Contains("specify", inner.Calls);
            Assert.False(run.VersionVerified);
            Assert.Null(run.VersionAfter);
            Assert.Equal(SpecifyOnlySteps.Failed, run.Plan.Steps.Single(s => s.Name == SpecifyOnlySteps.VerifyVersion).State);
            Assert.Equal(SpecifyOnlyVerdictKind.VersionUnverified, SpecifyOnlyJudge.Judge(run, specifierErrors: false).Kind);

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.Equal("VersionUnverified", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            Assert.Equal(SpecifyOnlyCodes.VersionUnverified, (string)receipt["diagnostics"][0]["code"]);
            Assert.Equal(JTokenType.Null, receipt["designObjectVersionChanged"].Type);
            Assert.DoesNotContain("verify-version", receipt["stepsExecuted"].Select(s => (string)s));
        }

        [Fact]
        public void An_object_deleted_while_it_was_being_specified_is_unverified_not_unchanged()
        {
            var env = new FakeEnvironment(Facts());
            env.OnSpecify = () =>
            {
                // Not found, and a version left over from the fake: an object that is gone never yields a version.
                env.Current.ObjectFound = false;
                return Spec();
            };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());

            Assert.Equal(SpecifyOnlyVerdictKind.VersionUnverified, SpecifyOnlyJudge.Judge(run, specifierErrors: false).Kind);
            Assert.Contains("no longer exists", run.Plan.Steps.Single(s => s.Name == SpecifyOnlySteps.VerifyVersion).Detail);
        }

        [Fact]
        public void A_version_that_could_not_be_computed_before_the_run_leaves_the_result_unverified()
        {
            var run = new SpecifyOnlyExecutor(new FakeEnvironment(Facts(version: null))).Execute(Request());
            Assert.Equal(SpecifyOnlyVerdictKind.VersionUnverified, SpecifyOnlyJudge.Judge(run, specifierErrors: false).Kind);
        }

        [Fact]
        public void A_specifier_that_returned_success_without_any_sign_of_having_worked_on_the_object_is_not_a_pass()
        {
            var run = RunWith(() => Spec(r => r.SectionsObserved.Clear()));

            var verdict = SpecifyOnlyJudge.Judge(run, specifierErrors: false);
            Assert.Equal(SpecifyOnlyVerdictKind.SpecificationNotObserved, verdict.Kind);
            Assert.Equal(SpecifyOnlyCodes.SpecificationNotObserved, verdict.Code);

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.Equal("SpecificationNotObserved", (string)receipt["outcome"]);
            Assert.False((bool)receipt["specified"]);
            Assert.Equal(SpecifyOnlyCodes.SpecificationNotObserved, (string)receipt["diagnostics"][0]["code"]);
        }

        [Theory]
        [InlineData("Specification", true)]
        [InlineData("specification", true)]
        [InlineData("Navigation", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_a_specification_section_shows_the_object_was_analyzed(string section, bool observed)
        {
            var spec = Spec(r => { r.SectionsObserved.Clear(); r.SectionsObserved.Add(section); });
            Assert.Equal(observed, SpecifyOnlyReceiptBuilder.SpecificationObserved(spec));
        }

        [Fact]
        public void Specification_files_written_by_the_call_are_enough_evidence_without_a_section()
        {
            var run = RunWith(() => Spec(r => { r.SectionsObserved.Clear(); r.SpecificationFiles.Add("SampleProc.ari"); }));
            Assert.Equal(SpecifyOnlyVerdictKind.Specified, SpecifyOnlyJudge.Judge(run, specifierErrors: false).Kind);
        }

        [Theory]
        [InlineData("Failed", false)]
        [InlineData("Cancelled", false)]
        [InlineData("TimedOut", false)]
        [InlineData("Error", false)]
        [InlineData("Succeeded", true)]
        [InlineData("Running", true)]
        [InlineData(null, true)]
        public void A_task_that_ended_failed_is_never_reported_as_a_pass_whatever_the_specifier_said_later(string taskStatus, bool specified)
        {
            var run = RunWith(() => Spec());

            Assert.Equal(specified, SpecifyOnlyJudge.Judge(run, specifierErrors: false, taskStatus).Succeeded);
            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray(), taskStatus);
            Assert.Equal(specified, (bool)receipt["specified"]);
            if (!specified) Assert.Equal("ExecutionFailed", (string)receipt["outcome"]);
        }

        [Fact]
        public void Files_that_appeared_in_the_generator_folder_are_a_warning_and_not_a_failure()
        {
            var env = new FakeEnvironment(Facts()) { FilesTouched = 2, TouchedNames = new List<string> { "a.cs", "web\\b.cs" } };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());

            var receipt = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray());
            Assert.Equal("Specified", (string)receipt["outcome"]);
            Assert.True((bool)receipt["specified"]);
            Assert.False((bool)receipt["evidence"]["noGenerationObserved"]);
            var warning = receipt["diagnostics"].Single(d => (string)d["code"] == SpecifyOnlyCodes.GeneratorOutputTouched);
            Assert.Equal("warning", (string)warning["severity"]);
            Assert.Contains("a.cs", (string)warning["message"]);
            Assert.Contains("2 entries", (string)warning["message"]);
        }

        [Fact]
        public void A_clean_generator_folder_adds_no_warning_and_the_receipt_says_whether_a_forced_respecification_was_asked()
        {
            var clean = SpecifyOnlyReceiptBuilder.Build(Request(), RunWith(() => Spec()), new JArray());
            Assert.DoesNotContain(clean["diagnostics"], d => (string)d["code"] == SpecifyOnlyCodes.GeneratorOutputTouched);
            Assert.False((bool)clean["evidence"]["rebuildApplied"]);

            var forced = SpecifyOnlyReceiptBuilder.Build(Request(), RunWith(() => Spec(r => r.RebuildApplied = true)), new JArray());
            Assert.True((bool)forced["evidence"]["rebuildApplied"]);
        }
        // ---------- the generation switch ----------

        private sealed class FakeSwitch
        {
            public bool Value;
            public bool Stuck;        // ignores attempts to change it
            public bool FailRead;
            public bool FailWrite;
            public readonly List<bool> Writes = new List<bool>();

            public bool Get()
            {
                if (FailRead) throw new InvalidOperationException("no read");
                return Value;
            }

            public void Set(bool value)
            {
                Writes.Add(value);
                if (FailWrite) throw new InvalidOperationException("no write");
                if (!Stuck) Value = value;
            }
        }

        [Fact]
        public void The_body_runs_with_the_switch_off_and_the_old_value_comes_back()
        {
            var sw = new FakeSwitch { Value = true };
            bool? seenInside = null;
            bool ran = SpecifyOnlyGenerationSwitch.RunWithSwitchOff(sw.Get, sw.Set, () => seenInside = sw.Value, out string failure);
            Assert.True(ran);
            Assert.Null(failure);
            Assert.False(seenInside);
            Assert.True(sw.Value);
            Assert.Equal(new[] { false, true }, sw.Writes);
        }

        [Fact]
        public void The_old_value_comes_back_even_when_the_body_throws()
        {
            var sw = new FakeSwitch { Value = true };
            Assert.Throws<InvalidOperationException>(() =>
                SpecifyOnlyGenerationSwitch.RunWithSwitchOff(sw.Get, sw.Set, () => throw new InvalidOperationException("boom"), out string ignored));
            Assert.True(sw.Value);
        }

        [Fact]
        public void A_switch_that_was_already_off_stays_off()
        {
            var sw = new FakeSwitch { Value = false };
            bool ran = SpecifyOnlyGenerationSwitch.RunWithSwitchOff(sw.Get, sw.Set, () => { }, out string failure);
            Assert.True(ran);
            Assert.False(sw.Value);
            Assert.Equal(new[] { false, false }, sw.Writes);
        }

        [Fact]
        public void A_switch_that_cannot_be_read_never_runs_the_body_and_is_never_touched()
        {
            var sw = new FakeSwitch { Value = true, FailRead = true };
            bool ran = SpecifyOnlyGenerationSwitch.RunWithSwitchOff(sw.Get, sw.Set, () => throw new Xunit.Sdk.XunitException("body ran"), out string failure);
            Assert.False(ran);
            Assert.Contains("could not be read", failure);
            Assert.Empty(sw.Writes);
        }

        [Fact]
        public void A_switch_that_stays_on_never_runs_the_body_and_the_old_value_is_restored()
        {
            var sw = new FakeSwitch { Value = true, Stuck = true };
            bool ran = SpecifyOnlyGenerationSwitch.RunWithSwitchOff(sw.Get, sw.Set, () => throw new Xunit.Sdk.XunitException("body ran"), out string failure);
            Assert.False(ran);
            Assert.Contains("could not be switched off", failure);
            Assert.Equal(new[] { false, true }, sw.Writes);
        }

        [Fact]
        public void A_switch_that_cannot_be_written_never_runs_the_body_and_the_failed_restore_does_not_throw()
        {
            var sw = new FakeSwitch { Value = true, FailWrite = true };
            bool ran = SpecifyOnlyGenerationSwitch.RunWithSwitchOff(sw.Get, sw.Set, () => throw new Xunit.Sdk.XunitException("body ran"), out string failure);
            Assert.False(ran);
            Assert.Contains("could not be turned off", failure);
            Assert.Equal(new[] { false, true }, sw.Writes); // the restore was attempted
        }

        [Fact]
        public async System.Threading.Tasks.Task Two_overlapping_calls_run_one_after_the_other_and_the_original_value_survives_both()
        {
            var sw = new FakeSwitch { Value = true };
            var inside = new System.Threading.ManualResetEventSlim(false);
            var release = new System.Threading.ManualResetEventSlim(false);

            var first = System.Threading.Tasks.Task.Run(() => SpecifyOnlyGenerationSwitch.RunWithSwitchOff(
                sw.Get, sw.Set, () => { inside.Set(); release.Wait(5000); }, out string f1));
            Assert.True(inside.Wait(5000));
            var second = System.Threading.Tasks.Task.Run(() => SpecifyOnlyGenerationSwitch.RunWithSwitchOff(
                sw.Get, sw.Set, () => { }, out string f2));

            try
            {
                Assert.NotSame(second, await System.Threading.Tasks.Task.WhenAny(
                    second, System.Threading.Tasks.Task.Delay(300)));
            }
            finally
            {
                release.Set();
            }
            var both = System.Threading.Tasks.Task.WhenAll(first, second);
            Assert.Same(both, await System.Threading.Tasks.Task.WhenAny(
                both, System.Threading.Tasks.Task.Delay(5000)));
            await both;

            Assert.True(sw.Value);
            Assert.Equal(new[] { false, true, false, true }, sw.Writes);
        }
        // ---------- the generator-output probe ----------

        private static string TempRoot()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "specify-only-probe-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            return root;
        }

        [Fact]
        public void The_probe_has_nothing_to_say_without_a_folder_and_finds_nothing_in_a_missing_one()
        {
            Assert.Equal(-1, SpecifyOnlyGeneratorProbe.CountTouchedSince(null, DateTime.UtcNow));
            Assert.Equal(-1, SpecifyOnlyGeneratorProbe.CountTouchedSince("  ", DateTime.UtcNow));
            Assert.Equal(0, SpecifyOnlyGeneratorProbe.CountTouchedSince(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "specify-only-missing-" + Guid.NewGuid().ToString("N")), DateTime.UtcNow));
        }

        [Fact]
        public void The_probe_counts_files_written_since_the_start_in_the_folder_and_its_web_folder()
        {
            string root = TempRoot();
            try
            {
                DateTime start = DateTime.UtcNow;
                string web = System.IO.Path.Combine(root, "web");
                System.IO.Directory.CreateDirectory(web);
                string oldFile = System.IO.Path.Combine(web, "old.cs");
                string newFile = System.IO.Path.Combine(web, "new.cs");
                string rootFile = System.IO.Path.Combine(root, "root.cs");
                System.IO.File.WriteAllText(oldFile, "x"); System.IO.File.SetLastWriteTimeUtc(oldFile, start.AddMinutes(-10));
                System.IO.File.WriteAllText(newFile, "x"); System.IO.File.SetLastWriteTimeUtc(newFile, start.AddSeconds(5));
                System.IO.File.WriteAllText(rootFile, "x"); System.IO.File.SetLastWriteTimeUtc(rootFile, start.AddSeconds(5));
                System.IO.Directory.SetCreationTimeUtc(root, start.AddMinutes(-30));
                System.IO.Directory.SetCreationTimeUtc(web, start.AddMinutes(-30));

                Assert.Equal(2, SpecifyOnlyGeneratorProbe.CountTouchedSince(root, start));
                Assert.Equal(0, SpecifyOnlyGeneratorProbe.CountTouchedSince(root, start.AddMinutes(1)));
            }
            finally { System.IO.Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void The_probe_counts_a_folder_that_appeared_since_the_start_a_first_generation_creates_web()
        {
            string root = TempRoot();
            try
            {
                DateTime start = DateTime.UtcNow.AddSeconds(-1);
                System.IO.Directory.SetCreationTimeUtc(root, start.AddMinutes(-30));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "web")); // created now, after start
                Assert.Equal(1, SpecifyOnlyGeneratorProbe.CountTouchedSince(root, start));
            }
            finally { System.IO.Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void The_probe_names_the_files_a_folder_gained_since_the_start_newest_first_and_capped()
        {
            string folder = TempRoot();
            try
            {
                DateTime start = DateTime.UtcNow;
                var files = new[] { "old.ari", "a.ari", "b.ari", "c.ari" };
                foreach (var name in files) System.IO.File.WriteAllText(System.IO.Path.Combine(folder, name), "x");
                System.IO.File.SetLastWriteTimeUtc(System.IO.Path.Combine(folder, "old.ari"), start.AddMinutes(-5));
                System.IO.File.SetLastWriteTimeUtc(System.IO.Path.Combine(folder, "a.ari"), start.AddSeconds(1));
                System.IO.File.SetLastWriteTimeUtc(System.IO.Path.Combine(folder, "b.ari"), start.AddSeconds(3));
                System.IO.File.SetLastWriteTimeUtc(System.IO.Path.Combine(folder, "c.ari"), start.AddSeconds(2));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(folder, "sub"));

                Assert.Equal(new[] { "b.ari", "c.ari", "a.ari" }, SpecifyOnlyGeneratorProbe.ListFilesSince(folder, start, 20));
                Assert.Equal(new[] { "b.ari" }, SpecifyOnlyGeneratorProbe.ListFilesSince(folder, start, 1));
                Assert.Empty(SpecifyOnlyGeneratorProbe.ListFilesSince(folder, start, 0));
                Assert.Empty(SpecifyOnlyGeneratorProbe.ListFilesSince(null, start, 20));
                Assert.Empty(SpecifyOnlyGeneratorProbe.ListFilesSince(System.IO.Path.Combine(folder, "missing"), start, 20));
            }
            finally { System.IO.Directory.Delete(folder, recursive: true); }
        }

        [Fact]
        public void The_receipt_names_the_specification_files_the_call_wrote()
        {
            var env = new FakeEnvironment(Facts());
            env.OnSpecify = () => new SpecifyOnlyRunResult
            {
                SpecifierInvoked = true, SpecifierReturned = true, GenerationSuppressed = true,
                SectionsObserved = new List<string> { "Specification" },
                SpecificationFiles = new List<string> { "SampleProc.ari" }
            };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());
            var persisted = SpecifyOnlyReceiptBuilder.Build(Request(), run, new JArray())["persisted"];
            Assert.Equal(new[] { "SampleProc.ari" }, persisted.Single(p => (string)p["kind"] == "specification-output")["files"].Select(f => (string)f).ToArray());
        }

        [Fact]
        public void The_probe_counts_a_root_that_was_itself_created_during_the_run()
        {
            string root = TempRoot();
            try
            {
                Assert.Equal(1, SpecifyOnlyGeneratorProbe.CountTouchedSince(root, DateTime.UtcNow.AddSeconds(-5)));
            }
            finally { System.IO.Directory.Delete(root, recursive: true); }
        }

        // ---------- the sections the SDK opened ----------

        [Fact]
        public void The_engine_remembers_every_section_the_sdk_opened_in_order()
        {
            var engine = new GxMcp.Worker.Helpers.InProcessBuildEngine((l, e) => { });
            foreach (var line in new[]
            {
                ">SSpecification:-:Specification",
                ">O1spc0010: Type mismatch in assignment (Binary=File)|pos",
                ">E0Specification:-:Specification",
                ">SDefault (.NET Framework) Generation:-:Default (.NET Framework) Generation",
                ">E1Default (.NET Framework) Generation:-:Default (.NET Framework) Generation",
                ">SDeveloperMenu Compilation:-:DeveloperMenu Compilation"
            })
                engine.LogMessageEvent(new Microsoft.Build.Framework.BuildMessageEventArgs(line, null, "test", Microsoft.Build.Framework.MessageImportance.Normal));

            Assert.Equal(new[] { "Specification", "Default (.NET Framework) Generation", "DeveloperMenu Compilation" }, engine.StartedSections);
            Assert.Equal(2, engine.StartedSections.Count(SpecifyOnlyReceiptBuilder.IsGenerationOrLaterSection));
        }

        [Fact]
        public void A_log_line_that_only_looks_like_a_section_end_or_a_message_opens_nothing()
        {
            var engine = new GxMcp.Worker.Helpers.InProcessBuildEngine((l, e) => { });
            foreach (var line in new[] { ">E1Specification:-:Specification", "Specification started", ">L>SNotASection" })
                engine.LogMessageEvent(new Microsoft.Build.Framework.BuildMessageEventArgs(line, null, "test", Microsoft.Build.Framework.MessageImportance.Normal));
            Assert.Empty(engine.StartedSections);
        }

        [Fact]
        public void The_engine_remembers_every_diagnostic_code_the_sdk_mentioned_whatever_the_shape_of_the_line()
        {
            var engine = new GxMcp.Worker.Helpers.InProcessBuildEngine((l, e) => { });
            foreach (var line in new[]
            {
                ">O2spc0217: Object is unreachable.|Artech.Architecture.Common.Location.SourcePosition;", // the shape the SDK really logs
                "Something unrelated mentioning GEN0001 in the middle of a sentence",
                "spc00189 is not a four-digit code, and xspc0020y is not a word of its own"
            })
                engine.LogMessageEvent(new Microsoft.Build.Framework.BuildMessageEventArgs(line, null, "test", Microsoft.Build.Framework.MessageImportance.Normal));

            Assert.True(engine.SawCode("spc0217"));
            Assert.True(engine.SawCode("SPC0217")); // the comparison ignores case
            Assert.True(engine.SawCode("gen0001"));
            Assert.False(engine.SawCode("spc0018")); // only a prefix of a longer number
            Assert.False(engine.SawCode("spc0020")); // only the inside of a longer word
            Assert.False(engine.SawCode("spc9999"));
        }

        // ---------- the status carries the receipt ----------

        [Fact]
        public void The_task_status_serializes_the_receipt_with_the_specifiers_own_diagnostics()
        {
            var env = new FakeEnvironment(Facts());
            env.OnSpecify = () => new SpecifyOnlyRunResult
            {
                SpecifierInvoked = true, SpecifierReturned = false, GenerationSuppressed = true,
                SectionsObserved = new List<string> { "Specification" }
            };
            var run = new SpecifyOnlyExecutor(env).Execute(Request());

            var status = new BuildService.BuildTaskStatus
            {
                TaskId = "abcd1234", Status = "Failed", SpecifyOnlyRequest = Request(), SpecifyOnlyRun = run,
                Errors = new List<string> { "error spc0056: Variable Customerid definition is incorrect [SampleProc, line 3]" },
                Warnings = new List<string> { "warning spc0022: Variable &unused is never used" }
            };
            var json = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });

            var receipt = (JObject)json["specifyOnly"];
            Assert.NotNull(receipt);
            Assert.Equal("SpecificationFailed", (string)receipt["outcome"]);
            var codes = receipt["diagnostics"].Select(d => (string)d["code"]).ToList();
            Assert.Contains("spc0056", codes);
            Assert.Contains("spc0022", codes);
            Assert.Equal("error", (string)receipt["diagnostics"].First(d => (string)d["code"] == "spc0056")["severity"]);
            Assert.Equal("warning", (string)receipt["diagnostics"].First(d => (string)d["code"] == "spc0022")["severity"]);
        }

        [Fact]
        public void Lines_the_runner_emitted_itself_are_not_reparsed_as_specifier_diagnostics()
        {
            var env = new FakeEnvironment(Facts(version: "1:root"));
            var run = new SpecifyOnlyExecutor(env).Execute(Request("2:root")); // version conflict
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = "abcd1234", Status = "Failed", SpecifyOnlyRequest = Request("2:root"), SpecifyOnlyRun = run,
                Errors = new List<string> { SpecifyOnlyCodes.Line(SpecifyOnlyCodes.VersionConflict, "The object changed.") }
            };
            var receipt = (JObject)JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore })["specifyOnly"];
            Assert.Single(receipt["diagnostics"]);
            Assert.Equal(SpecifyOnlyCodes.VersionConflict, (string)receipt["diagnostics"][0]["code"]);
        }

        [Fact]
        public void A_status_without_a_specify_only_run_has_no_receipt()
        {
            var json = JObject.FromObject(new BuildService.BuildTaskStatus { TaskId = "x", Status = "Running" },
                new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
            Assert.Null(json["specifyOnly"]);
        }

        [Fact]
        public void The_admission_key_keeps_specify_only_apart_from_plain_specify_of_the_same_target()
        {
            string plain = BuildService.BuildAdmissionKey("Build", ObjectGuid.ToString("D"), "none", 200, false, false, false, true, false, null);
            string strict = BuildService.BuildAdmissionKey("Build", ObjectGuid.ToString("D"), "none", 200, false, false, false, true, false, null, "specify_only:");
            string strictOtherVersion = BuildService.BuildAdmissionKey("Build", ObjectGuid.ToString("D"), "none", 200, false, false, false, true, false, null, "specify_only:2:root");
            Assert.NotEqual(plain, strict);
            Assert.NotEqual(strict, strictOtherVersion);
            Assert.Equal(plain, BuildService.BuildAdmissionKey("Build", ObjectGuid.ToString("D"), "none", 200, false, false, false, true, false, null, null));
        }

        // ---------- BuildService entry ----------

        private static BuildService ServiceWith(ISpecifyOnlyEnvironment environment)
            => new BuildService { SpecifyOnlyEnvironmentFactory = () => environment };

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("SampleProc")]
        [InlineData("Procedure:SampleProc")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public void Only_a_non_empty_guid_identifies_the_object(string target)
        {
            var env = new FakeEnvironment(Facts());
            var json = JObject.Parse(ServiceWith(env).SpecifyOnly(target, null, dryRun: false, queueLifecycle: true));
            Assert.Equal(SpecifyOnlyCodes.InvalidGuid, (string)json["error"]["code"]);
            Assert.Empty(env.Calls);
        }

        [Fact]
        public void Without_an_open_kb_the_request_is_refused()
        {
            var service = new BuildService { SpecifyOnlyEnvironmentFactory = () => null };
            var json = JObject.Parse(service.SpecifyOnly(ObjectGuid.ToString(), null, dryRun: true, queueLifecycle: true));
            Assert.Equal(SpecifyOnlyCodes.KbNotOpen, (string)json["error"]["code"]);
        }

        [Fact]
        public void The_service_dry_run_answers_with_the_plan_and_changes_nothing()
        {
            var env = NeverBuilt();
            var json = JObject.Parse(ServiceWith(env).SpecifyOnly(ObjectGuid.ToString("B"), null, dryRun: true, queueLifecycle: true));
            Assert.False(env.Mutated);
            // The canonical envelope: status ok, code DryRun, the plan under result.
            Assert.Equal("ok", (string)json["status"]);
            Assert.Equal("DryRun", (string)json["code"]);
            Assert.Equal(ObjectGuid.ToString("D"), (string)json["target"]);
            Assert.True((bool)json["result"]["dryRun"]);
            Assert.Equal("WouldSpecify", (string)json["result"]["outcome"]);
            Assert.Equal("WouldSpecify", (string)json["result"]["specifyOnly"]["outcome"]);
            Assert.Equal(ObjectGuid.ToString("D"), (string)json["result"]["specifyOnly"]["target"]["guid"]);
            // A plan has nothing to compare "after" with.
            Assert.Null(json["result"]["specifyOnly"]["version"]["after"]);
            Assert.Null(json["result"]["specifyOnly"]["version"]["unchangedDuringRun"]);
        }

        [Fact]
        public void The_service_refuses_a_conflicting_version_without_starting_a_task()
        {
            var env = NeverBuilt();
            var json = JObject.Parse(ServiceWith(env).SpecifyOnly(ObjectGuid.ToString(), "1:root", dryRun: false, queueLifecycle: true));
            Assert.False(env.Mutated);
            Assert.Equal("error", (string)json["status"]);
            Assert.Equal(SpecifyOnlyCodes.VersionConflict, (string)json["error"]["code"]);
            Assert.False((bool)json["specified"]);
            Assert.Equal("Refused", (string)json["outcome"]);
            Assert.Equal("Refused", (string)json["specifyOnly"]["outcome"]);
            Assert.Equal("100:root", (string)json["specifyOnly"]["version"]["current"]);
            Assert.Equal("1:root", (string)json["specifyOnly"]["version"]["expected"]);
            Assert.False((bool)json["specifyOnly"]["version"]["matchesExpected"]);
            // A refused call is not a plan: it reports the run fields, empty because nothing ran.
            Assert.NotNull(json["specifyOnly"]["version"]["after"]);
            Assert.False((bool)json["specifyOnly"]["dryRun"]);
        }

        [Fact]
        public void The_service_refuses_an_unsafe_environment_copy_without_starting_a_task()
        {
            var env = new FakeEnvironment(Facts(type: "Transaction", envCopy: false));
            var json = JObject.Parse(ServiceWith(env).SpecifyOnly(ObjectGuid.ToString(), null, dryRun: false, queueLifecycle: true));
            Assert.False(env.Mutated);
            Assert.Equal(SpecifyOnlyCodes.EnvironmentCopyUnsafe, (string)json["error"]["code"]);
        }

        [Fact]
        public void An_object_that_cannot_be_read_is_refused_with_a_receipt_and_nothing_is_started()
        {
            var inner = new FakeEnvironment(Facts());
            var json = JObject.Parse(ServiceWith(new RereadFailsEnvironment(inner, failFromRead: 1))
                .SpecifyOnly(ObjectGuid.ToString(), null, dryRun: false, queueLifecycle: true));
            Assert.Equal(SpecifyOnlyCodes.ExecutionFailed, (string)json["error"]["code"]);
            Assert.Equal("ExecutionFailed", (string)json["outcome"]);
            Assert.False((bool)json["specified"]);
            Assert.False(inner.Mutated);
        }

        [Fact]
        public void A_dry_run_over_an_object_that_cannot_be_read_says_it_would_be_refused()
        {
            var json = JObject.Parse(ServiceWith(new RereadFailsEnvironment(new FakeEnvironment(Facts()), failFromRead: 1))
                .SpecifyOnly(ObjectGuid.ToString(), null, dryRun: true, queueLifecycle: true));
            Assert.Equal("DryRun", (string)json["code"]);
            Assert.Equal("WouldRefuse", (string)json["result"]["outcome"]);
            Assert.Equal(SpecifyOnlyCodes.ExecutionFailed, (string)json["result"]["specifyOnly"]["refusal"]["code"]);
        }

        [Fact]
        public void Only_a_specify_only_request_has_an_admission_variant()
        {
            Assert.Null(BuildService.StrictAdmissionVariant(null));
            Assert.Equal("specify_only:", BuildService.StrictAdmissionVariant(Request()));
            Assert.Equal("specify_only:2:root", BuildService.StrictAdmissionVariant(Request("2:root")));
        }

        // ---------- the strict runner ----------

        private static BuildService.BuildTaskStatus StrictStatus()
            => new BuildService.BuildTaskStatus
            {
                TaskId = "strict001", Status = "Running", Action = "Build", StartedAt = DateTime.UtcNow,
                SpecifyOnly = true, SpecifyOnlyRequest = Request()
            };

        // Runs the strict runner against a fake environment; every line it sends to the task is recorded
        // as "E:<line>" (an error) or "I:<line>".
        private static InProcessBuildOutcome RunStrict(BuildService.BuildTaskStatus status, ISpecifyOnlyEnvironment environment, List<string> lines, Func<string> generatorPath = null)
        {
            var previous = InProcessBuildRunner.SpecifyOnlyEnvironmentForTests;
            InProcessBuildRunner.SpecifyOnlyEnvironmentForTests = (engine, path) => environment;
            try
            {
                var outcome = InProcessBuildRunner.RunSpecifyOnlyStrict(status, null, status.SpecifyOnlyRequest,
                    (line, isError) => lines.Add((isError ? "E:" : "I:") + line), generatorPath ?? (() => null));
                // What RunInProcessBuildPhase writes after the runner returns: the receipt only speaks for a finished task.
                status.Status = outcome == InProcessBuildOutcome.Succeeded ? "Succeeded" : "Failed";
                return outcome;
            }
            finally { InProcessBuildRunner.SpecifyOnlyEnvironmentForTests = previous; }
        }

        [Fact]
        public void The_strict_runner_succeeds_for_a_clean_specification_and_leaves_the_run_on_the_status()
        {
            var status = StrictStatus();
            var lines = new List<string>();

            var outcome = RunStrict(status, new FakeEnvironment(Facts()), lines);

            Assert.Equal(InProcessBuildOutcome.Succeeded, outcome);
            Assert.NotNull(status.SpecifyOnlyRun);
            Assert.Equal("Specified", status.SpecifyOnlyOutcome);
            Assert.True(status.SpecifyOnlySpecified);
            Assert.DoesNotContain(lines, l => l.StartsWith("E:"));
            Assert.Contains(lines, l => l.Contains("NOT run"));
        }

        [Fact]
        public void The_strict_runner_hands_the_environment_the_engine_and_the_generator_path()
        {
            var status = StrictStatus();
            var lines = new List<string>();
            Microsoft.Build.Framework.IBuildEngine seenEngine = null;
            Func<string> seenPath = null;
            Func<string> generatorPath = () => "C:\\somewhere";
            var previous = InProcessBuildRunner.SpecifyOnlyEnvironmentForTests;
            InProcessBuildRunner.SpecifyOnlyEnvironmentForTests = (engine, path) =>
            {
                seenEngine = engine;
                seenPath = path;
                return new FakeEnvironment(Facts());
            };
            try
            {
                InProcessBuildRunner.RunSpecifyOnlyStrict(status, null, status.SpecifyOnlyRequest, (l, e) => lines.Add(l), generatorPath);
            }
            finally { InProcessBuildRunner.SpecifyOnlyEnvironmentForTests = previous; }

            Assert.Same(generatorPath, seenPath);
            Assert.NotNull(seenEngine);
            // What the SDK logs through that engine reaches the task's lines.
            seenEngine.LogMessageEvent(new Microsoft.Build.Framework.BuildMessageEventArgs(
                "from the sdk", null, "test", Microsoft.Build.Framework.MessageImportance.Normal));
            Assert.Contains("from the sdk", lines);
        }

        [Fact]
        public void A_refusal_ends_the_task_failed_with_one_marked_error_line_carrying_the_code()
        {
            var status = StrictStatus();
            var lines = new List<string>();

            var outcome = RunStrict(status, new FakeEnvironment(Facts(found: false)), lines);

            Assert.Equal(InProcessBuildOutcome.FailedWithDiagnostics, outcome);
            var error = Assert.Single(lines, l => l.StartsWith("E:"));
            Assert.Contains(SpecifyOnlyCodes.LineMarker + SpecifyOnlyCodes.ObjectNotFound + "]", error);
            Assert.Equal("Refused", status.SpecifyOnlyOutcome);
        }

        [Fact]
        public void A_generation_section_ends_the_task_failed_with_the_runners_own_line()
        {
            var status = StrictStatus();
            var lines = new List<string>();
            var env = new FakeEnvironment(Facts())
            {
                OnSpecify = () => Spec(r => r.SectionsObserved.Add("Default (.NET Framework) Generation"))
            };

            var outcome = RunStrict(status, env, lines);

            Assert.Equal(InProcessBuildOutcome.FailedWithDiagnostics, outcome);
            Assert.Single(lines, l => l.StartsWith("E:") && l.Contains(SpecifyOnlyCodes.GenerationObserved));
            Assert.Equal("GenerationObserved", status.SpecifyOnlyOutcome);
            Assert.False(status.SpecifyOnlySpecified);
        }

        [Fact]
        public void Errors_the_sdk_itemized_are_not_repeated_as_a_runner_line_and_still_fail_the_task()
        {
            var status = StrictStatus();
            status.ErrorCount = 1; // what the engine's sink counted while the specifier ran
            status.Errors = new List<string> { "error spc0056: Variable &x is not defined [SampleProc, line 3]" };
            var lines = new List<string>();
            var env = new FakeEnvironment(Facts()) { OnSpecify = () => Spec(r => r.SpecifierReturned = false) };

            var outcome = RunStrict(status, env, lines);

            Assert.Equal(InProcessBuildOutcome.FailedWithDiagnostics, outcome);
            Assert.DoesNotContain(lines, l => l.StartsWith("E:"));
            Assert.Equal("SpecificationFailed", status.SpecifyOnlyOutcome);
            Assert.Equal("spc0056", (string)status.SpecifyOnlyReceipt["diagnostics"][0]["code"]);
        }

        // ---------- the task ----------

        [Fact]
        public void A_strict_task_without_the_in_process_pipeline_ends_failed_instead_of_falling_back_to_an_external_build()
        {
            var service = new BuildService(); // no KB service: the in-process pipeline is unavailable
            var status = StrictStatus();

            bool terminalized = service.RunInProcessBuildPhase(status, "Build", new List<string> { ObjectGuid.ToString("D") });

            Assert.True(terminalized); // true = no external MSBuild phase follows
            Assert.Equal("Failed", status.Status);
            Assert.Equal("spec_unavailable", status.FailureOrigin);
            Assert.Equal("ExecutionFailed", status.SpecifyOnlyOutcome);
            Assert.False(status.SpecifyOnlySpecified);
            Assert.Equal(SpecifyOnlyCodes.InProcessDisabled, (string)status.SpecifyOnlyReceipt["refusal"]["code"]);
            Assert.Contains(SpecifyOnlyCodes.LineMarker + SpecifyOnlyCodes.InProcessDisabled + "]", status.FullOutput.ToString());
        }

        [Fact]
        public void A_task_that_is_not_specify_only_still_falls_through_to_the_external_phase_when_the_pipeline_is_unavailable()
        {
            var service = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "plain001", Status = "Running", Action = "Build", StartedAt = DateTime.UtcNow };

            Assert.False(service.RunInProcessBuildPhase(status, "Build", new List<string> { "Something" }));
            Assert.Equal("Running", status.Status);
            Assert.Null(status.SpecifyOnlyReceipt);
            Assert.Null(status.SpecifyOnlyOutcome);
        }

        [Fact]
        public void A_task_that_ended_before_the_runner_recorded_anything_still_gets_a_receipt_that_says_why()
        {
            var status = StrictStatus();
            status.Status = "Error";
            status.Error = "KB Path not found in Environment (GX_KB_PATH)";

            BuildService.EnsureSpecifyOnlyRun(status);

            Assert.Equal("ExecutionFailed", status.SpecifyOnlyOutcome);
            Assert.False(status.SpecifyOnlySpecified);
            Assert.Contains("KB Path not found", (string)status.SpecifyOnlyReceipt["refusal"]["message"]);
        }

        [Fact]
        public void A_run_the_runner_already_recorded_is_never_overwritten_and_other_tasks_get_none()
        {
            var status = StrictStatus();
            status.SpecifyOnlyRun = new SpecifyOnlyExecutor(new FakeEnvironment(Facts())).Execute(Request());
            var recorded = status.SpecifyOnlyRun;
            status.Error = "late";

            BuildService.EnsureSpecifyOnlyRun(status);
            Assert.Same(recorded, status.SpecifyOnlyRun);

            var plain = new BuildService.BuildTaskStatus { TaskId = "plain002", Status = "Error", Error = "x" };
            BuildService.EnsureSpecifyOnlyRun(plain);
            Assert.Null(plain.SpecifyOnlyRun);
            Assert.Null(plain.SpecifyOnlyReceipt);
        }

        [Fact]
        public void A_task_is_created_strict_at_admission_so_a_queued_one_is_never_promoted_as_a_plain_build()
        {
            var request = Request("100:root");
            var strict = BuildService.CreateAdmissionStatus("adm00001", "Build", ObjectGuid.ToString("D"),
                new List<string> { ObjectGuid.ToString("D") }, "kb", queued: true, queuePosition: 1,
                specifyOnly: true, specifyOnlyRequest: request);
            Assert.Equal("Queued", strict.Status);
            Assert.True(strict.SpecifyOnly);
            Assert.Same(request, strict.SpecifyOnlyRequest);

            var plain = BuildService.CreateAdmissionStatus("adm00002", "Build", "SampleProc",
                new List<string> { "SampleProc" }, "kb", queued: true, queuePosition: 1);
            Assert.False(plain.SpecifyOnly);
            Assert.Null(plain.SpecifyOnlyRequest);
        }

        [Fact]
        public void The_in_process_runner_hands_a_specify_only_request_to_the_strict_runner_before_any_other_specify_path()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "InProcessBuildRunner.cs")
                .Replace("\r\n", "\n");
            const string dispatch = "if (specifyOnly && isBuildWithTargets && specifyOnlyRequest != null)\n                        return RunSpecifyOnlyStrict(status, kbHandle, specifyOnlyRequest, lineSink, generatorOutputPath);";
            int strictAt = source.IndexOf(dispatch, StringComparison.Ordinal);
            int legacyAt = source.IndexOf("ExecuteSpecifyResolved(kbHandle, targets, engine, lineSink, refuseStaleEnvironmentCopy)", StringComparison.Ordinal);
            Assert.True(strictAt > 0, "the strict dispatch is missing from InProcessBuildRunner.Run");
            Assert.True(legacyAt > strictAt, "the strict dispatch must come before the plain specify path");
        }

        [Fact]
        public void Build_hands_the_admission_both_the_strict_variant_and_the_request()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "BuildService.cs")
                .Replace("\r\n", "\n");
            int admit = source.IndexOf("var admission = AdmitBuildRequest(", StringComparison.Ordinal);
            Assert.True(admit > 0, "Build no longer admits through AdmitBuildRequest");
            string call = source.Substring(admit, 600);
            Assert.Contains("variant: StrictAdmissionVariant(specifyOnlyRequest)", call);
            Assert.Contains("specifyOnlyRequest: specifyOnlyRequest", call);
        }

        // ---------- the Worker command ----------

        private sealed class FakeEnvironmentManager : IEnvironmentManager
        {
            public string Active = "Default";
            public readonly List<string> Switched = new List<string>();
            public string GetActiveEnvironmentName() => Active;
            public void SetActiveEnvironment(string name) { Switched.Add(name); Active = name; }
        }

        [Fact]
        public void A_specify_only_for_another_environment_is_refused_without_switching_or_reading_anything()
        {
            var env = new FakeEnvironment(Facts());
            var manager = new FakeEnvironmentManager();
            var pipeline = new CompilationPipeline(ServiceWith(env), manager);

            var json = JObject.Parse(pipeline.ExecuteBuild("SpecifyOnly", null, new JObject
            {
                ["targetGuid"] = ObjectGuid.ToString(), ["environment"] = "Other", ["dryRun"] = true
            }));

            Assert.Equal(SpecifyOnlyCodes.EnvironmentNotSupported, (string)json["error"]["code"]);
            Assert.Empty(manager.Switched);
            Assert.Empty(env.Calls);
        }

        [Fact]
        public void A_specify_only_for_the_active_environment_reaches_the_service_without_ever_switching()
        {
            var env = new FakeEnvironment(Facts());
            var manager = new FakeEnvironmentManager();
            var pipeline = new CompilationPipeline(ServiceWith(env), manager);

            // The environment is compared without regard to case; the GUID may come as the target.
            var json = JObject.Parse(pipeline.ExecuteBuild("SpecifyOnly", ObjectGuid.ToString(), new JObject
            {
                ["environment"] = "DEFAULT", ["dryRun"] = true
            }));

            Assert.Equal("DryRun", (string)json["code"]);
            Assert.Equal(ObjectGuid.ToString("D"), (string)json["result"]["specifyOnly"]["target"]["guid"]);
            Assert.Empty(manager.Switched);
            Assert.Equal(new[] { "read" }, env.Calls);
        }

        [Fact]
        public void A_guid_passed_as_target_still_identifies_the_object_when_the_gateway_sends_null_fields()
        {
            var env = new FakeEnvironment(Facts());
            var pipeline = new CompilationPipeline(ServiceWith(env), new FakeEnvironmentManager());

            // What the Gateway sends for a caller that gave only target: every other field is an explicit JSON null.
            var json = JObject.Parse(pipeline.ExecuteBuild("SpecifyOnly", ObjectGuid.ToString(), new JObject
            {
                ["targetGuid"] = JValue.CreateNull(),
                ["expectedVersion"] = JValue.CreateNull(),
                ["environment"] = JValue.CreateNull(),
                ["dryRun"] = true
            }));

            Assert.Equal("DryRun", (string)json["code"]);
            Assert.Equal(ObjectGuid.ToString("D"), (string)json["result"]["specifyOnly"]["target"]["guid"]);
        }
        [Fact]
        public void A_specify_only_without_dryRun_reaches_the_service_as_a_real_request_and_carries_the_version()
        {
            var env = new FakeEnvironment(Facts(version: "100:root"));
            var pipeline = new CompilationPipeline(ServiceWith(env), new FakeEnvironmentManager());

            var json = JObject.Parse(pipeline.ExecuteBuild("SpecifyOnly", null, new JObject
            {
                ["targetGuid"] = ObjectGuid.ToString(), ["expectedVersion"] = "1:root"
            }));

            // The conflicting version is judged by the service, so it did receive expectedVersion and no dryRun.
            Assert.Equal(SpecifyOnlyCodes.VersionConflict, (string)json["error"]["code"]);
            Assert.False(env.Mutated);
        }

        [Theory]
        [InlineData("Running")]
        [InlineData("Queued")]
        [InlineData("")]
        [InlineData(null)]
        public void While_the_task_is_not_finished_it_publishes_no_verdict_even_if_the_run_is_already_recorded(string state)
        {
            var status = StrictStatus();
            status.Status = state;
            status.SpecifyOnlyRun = new SpecifyOnlyExecutor(new FakeEnvironment(Facts())).Execute(Request());

            Assert.Null(status.SpecifyOnlyOutcome);
            Assert.Null(status.SpecifyOnlySpecified);
            Assert.Null(status.SpecifyOnlyReceipt);

            status.Status = "Succeeded";
            Assert.Equal("Specified", status.SpecifyOnlyOutcome);
            Assert.True(status.SpecifyOnlySpecified);
        }

        [Fact]
        public void A_task_a_watchdog_ended_does_not_publish_a_pass_when_the_sdk_finishes_later()
        {
            var status = StrictStatus();
            status.SpecifyOnlyRun = new SpecifyOnlyExecutor(new FakeEnvironment(Facts())).Execute(Request());
            status.Status = "Failed";
            status.FailureOrigin = "watchdog";

            Assert.Equal("ExecutionFailed", status.SpecifyOnlyOutcome);
            Assert.False(status.SpecifyOnlySpecified);
            Assert.Equal("ExecutionFailed", (string)status.SpecifyOnlyReceipt["outcome"]);
        }

        [Fact]
        public void The_receipt_says_when_the_task_saw_more_diagnostics_than_it_kept()
        {
            var status = StrictStatus();
            status.Status = "Failed";
            status.SpecifyOnlyRun = new SpecifyOnlyExecutor(new FakeEnvironment(Facts()) { OnSpecify = () => Spec(r => r.SpecifierReturned = false) }).Execute(Request());
            status.Errors = Enumerable.Range(0, 50).Select(i => "error spc0056: Variable &v" + i + " is not defined [SampleProc, line " + i + "]").ToList();

            status.ErrorCount = 300; // HandleLine counts every error and keeps the first 50 lines
            var truncated = status.SpecifyOnlyReceipt;
            Assert.True((bool)truncated["diagnosticsTruncated"]);
            Assert.Equal(300, (int)truncated["diagnosticsTotals"]["errors"]);

            status.ErrorCount = 50; // nothing was dropped: the receipt says nothing about it
            Assert.Null(status.SpecifyOnlyReceipt["diagnosticsTruncated"]);
        }

        private static void HandleLine(BuildService service, BuildService.BuildTaskStatus status, string line, bool isError)
        {
            var method = typeof(BuildService).GetMethod("HandleLine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(service, new object[] { status, line, isError });
        }

        [Fact]
        public void The_runners_own_error_line_goes_through_the_tasks_line_handler_and_is_counted_as_an_error()
        {
            var service = new BuildService();
            var status = StrictStatus();
            status.SpecifyOnlyRun = new SpecifyOnlyExecutor(new FakeEnvironment(Facts(found: false))).Execute(Request());

            HandleLine(service, status, SpecifyOnlyCodes.Line(SpecifyOnlyCodes.ObjectNotFound, "No object with this GUID."), isError: true);
            status.Status = "Failed";

            Assert.Equal(1, status.ErrorCount);
            Assert.Contains(status.Errors, e => e.Contains(SpecifyOnlyCodes.LineMarker + SpecifyOnlyCodes.ObjectNotFound + "]"));
            // The marked line is the runner's own: the receipt reports it through the verdict, once, with its code.
            var diagnostics = status.SpecifyOnlyReceipt["diagnostics"];
            Assert.Single(diagnostics);
            Assert.Equal(SpecifyOnlyCodes.ObjectNotFound, (string)diagnostics[0]["code"]);
        }
        private static BuildService.BuildTaskStatus TaskById(string taskId)
        {
            var field = typeof(BuildService).GetField("_tasks", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            var tasks = (System.Collections.Concurrent.ConcurrentDictionary<string, BuildService.BuildTaskStatus>)field.GetValue(null);
            tasks.TryGetValue(taskId, out var status);
            return status;
        }

        [Fact]
        public void An_accepted_task_is_strict_from_admission_and_with_no_kb_ends_with_a_receipt_never_specified()
        {
            var env = new FakeEnvironment(Facts());
            var accepted = JObject.Parse(ServiceWith(env).SpecifyOnly(ObjectGuid.ToString("D"), null, dryRun: false, queueLifecycle: true));
            Assert.Equal("Accepted", (string)accepted["status"]);

            var status = TaskById((string)accepted["taskId"]);
            Assert.NotNull(status);
            Assert.True(status.SpecifyOnly);
            Assert.NotNull(status.SpecifyOnlyRequest);
            Assert.Equal(ObjectGuid, status.SpecifyOnlyRequest.Guid);

            // Whichever way the task ends without a KB (no KB path, or no in-process pipeline), the receipt exists.
            Assert.True(System.Threading.SpinWait.SpinUntil(() => status.SpecifyOnlyOutcome != null, TimeSpan.FromSeconds(30)),
                "the task never recorded how it ended; status=" + status.Status);
            Assert.Equal("ExecutionFailed", status.SpecifyOnlyOutcome);
            Assert.False(status.SpecifyOnlySpecified);
            Assert.DoesNotContain("specify", env.Calls); // the environment served the plan only
        }
    }
}
