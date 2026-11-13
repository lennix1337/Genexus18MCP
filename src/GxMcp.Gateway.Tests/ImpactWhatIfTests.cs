using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>genexus_impact whatIf=true: composition order, budget, failure isolation, read-only, verdict.</summary>
    public sealed class ImpactWhatIfTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

        private sealed class FakeWorker
        {
            internal readonly List<string> Calls = new List<string>();
            internal readonly List<JObject> CallArgs = new List<JObject>();
            internal Func<JObject, CancellationToken, Task<ImpactWhatIf.LegResult>> Impact = (_, _) => Task.FromResult(OkImpact("Customer", "Transaction", 3));
            internal Func<JObject, CancellationToken, Task<ImpactWhatIf.LegResult>> Db = (_, _) => Task.FromResult(OkReorgPreview(changes: 0, destructive: 0));

            internal ImpactWhatIf.LegRunner Runner => (tool, args, token) =>
            {
                Calls.Add(tool + (args["action"] != null ? ":" + args["action"] : string.Empty));
                CallArgs.Add(args);
                return tool == "genexus_impact" ? Impact(args, token) : Db(args, token);
            };
        }

        private static ImpactWhatIf.LegResult Ok(JObject result) =>
            new ImpactWhatIf.LegResult { Payload = new JObject { ["status"] = "ok", ["result"] = result } };

        private static ImpactWhatIf.LegResult Err(string code, string message = "boom") =>
            new ImpactWhatIf.LegResult
            {
                IsError = true, Code = code, Message = message,
                Payload = new JObject { ["status"] = "error", ["error"] = new JObject { ["code"] = code, ["message"] = message } }
            };

        private static ImpactWhatIf.LegResult OkImpact(string name, string type, int callers, string trust = "complete",
            string? truncatedBy = null)
        {
            var affected = new JArray(Enumerable.Range(1, callers).Select(i => new JObject
            {
                ["name"] = "Caller" + i.ToString("D3"), ["type"] = "Procedure", ["depth"] = i <= 2 ? 1 : 2
            }));
            return Ok(new JObject
            {
                ["root"] = new JObject { ["name"] = name, ["type"] = type, ["guid"] = "g-" + name },
                ["affected"] = affected,
                ["countsByType"] = new JObject { ["Procedure"] = callers },
                ["stats"] = new JObject
                {
                    ["affectedCount"] = callers,
                    ["truncated"] = truncatedBy != null,
                    ["truncatedBy"] = truncatedBy != null ? new JArray(truncatedBy) : new JArray()
                },
                ["fieldTrust"] = trust,
                ["coverageNote"] = trust == "complete" ? null : "not enriched"
            });
        }

        private static ImpactWhatIf.LegResult OkReorgPreview(int changes, int destructive, int tables = 1)
        {
            var entries = new JArray();
            for (int i = 0; i < tables; i++)
            {
                var tableChanges = new JArray();
                if (i == 0)
                    for (int c = 0; c < changes; c++)
                        tableChanges.Add(new JObject { ["field"] = c == 0 ? "added" : "type", ["destructive"] = false });
                var warnings = new JArray();
                if (i == 0)
                    for (int w = 0; w < destructive; w++) warnings.Add(new JObject { ["severity"] = "destructive" });
                entries.Add(new JObject { ["tableName"] = "T" + i, ["changes"] = tableChanges, ["warnings"] = warnings });
            }
            return Ok(new JObject
            {
                ["requiresReorganization"] = changes > 0,
                ["tables"] = entries,
                ["summary"] = new JObject { ["tables"] = tables, ["changes"] = changes, ["destructiveWarnings"] = destructive }
            });
        }

        private static Task<ImpactWhatIf.Outcome> Compose(FakeWorker worker, JObject? args = null, TimeSpan? budget = null,
            int? buildSeconds = 42, Action? onBuildEstimate = null) =>
            ImpactWhatIf.ComposeAsync(args ?? new JObject { ["name"] = "Customer", ["whatIf"] = true }, worker.Runner,
                () => { onBuildEstimate?.Invoke(); return buildSeconds; }, budget ?? Budget, CancellationToken.None);

        [Fact]
        public async Task ComposesCheapestFirst_IndexThenBuildHistoryThenSchema()
        {
            var worker = new FakeWorker();
            var order = new List<string>();
            worker.Impact = (a, t) => { order.Add("impact"); return Task.FromResult(OkImpact("Customer", "Transaction", 3)); };
            worker.Db = (a, t) => { order.Add("db"); return Task.FromResult(OkReorgPreview(0, 0)); };

            var outcome = await Compose(worker, onBuildEstimate: () => order.Add("build"));

            Assert.False(outcome.IsError);
            Assert.Equal(new[] { "impact", "build", "db" }, order);
            Assert.Equal(new[] { "genexus_impact", "genexus_db:reorg_preview" }, worker.Calls);
            Assert.Equal("up", worker.CallArgs[0]["direction"]!.ToString());
            Assert.Null(worker.CallArgs[0]["whatIf"]);
            Assert.Equal("Customer", worker.CallArgs[1]["name"]!.ToString());
            Assert.Equal("Transaction", worker.CallArgs[1]["type"]!.ToString());
            Assert.Null(worker.CallArgs[1]["deep"]);
        }

        [Theory]
        [InlineData("Transaction", "genexus_db:reorg_preview")]
        [InlineData("Table", "genexus_db:reorg_preview")]
        [InlineData("Attribute", "genexus_db:reorg_impact")]
        [InlineData("Domain", "genexus_db:reorg_impact")]
        [InlineData("Procedure", null)]
        [InlineData("WebPanel", null)]
        public async Task OnlyDatabaseCapableTargetsReachTheDatabaseLeg(string type, string? expectedDbCall)
        {
            var worker = new FakeWorker();
            worker.Impact = (_, _) => Task.FromResult(OkImpact("X", type, 1));
            worker.Db = (_, _) => Task.FromResult(Ok(new JObject { ["reorgLikelyNeeded"] = false }).WithPreviewShape());

            var outcome = await Compose(worker);

            Assert.Equal(expectedDbCall == null ? 1 : 2, worker.Calls.Count);
            if (expectedDbCall != null) Assert.Equal(expectedDbCall, worker.Calls[1]);
            else
            {
                var db = (JObject)outcome.Payload["database"]!;
                Assert.Equal("skipped", db["status"]!.ToString());
                Assert.Contains("not stored in the database", db["reason"]!.ToString());
                // A section that cannot apply is not a missing fact: the call can still be complete.
                Assert.True(outcome.Payload["complete"]!.Value<bool>());
            }
        }

        [Fact]
        public async Task BudgetExhaustedMidLeg_MarksThatSectionSkippedAndKeepsTheRest()
        {
            var worker = new FakeWorker();
            bool cancelled = false;
            worker.Db = async (_, token) =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return OkReorgPreview(0, 0);
            };

            var outcome = await Compose(worker, budget: TimeSpan.FromMilliseconds(300));

            var p = outcome.Payload;
            Assert.False(outcome.IsError);
            Assert.Equal("complete", p["callers"]!["status"]!.ToString());
            Assert.Equal("complete", p["build"]!["status"]!.ToString());
            Assert.Equal("skipped", p["database"]!["status"]!.ToString());
            Assert.Contains("budget ran out", p["database"]!["reason"]!.ToString());
            Assert.False(p["complete"]!.Value<bool>());
            Assert.Equal("unknown", p["verdict"]!.ToString());
            Assert.Equal(300, p["budgetMs"]!.Value<int>());
            Assert.True(p["elapsedMs"]!.Value<int>() < 5000);
            await Task.Delay(50);
            Assert.True(cancelled, "the late leg must be signalled to stop");
        }

        [Fact]
        public async Task ImpactLegTimeout_SkipsEverythingThatNeedsItsTargetIdentity()
        {
            var worker = new FakeWorker();
            worker.Impact = async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(30), token); return OkImpact("C", "Transaction", 1); };

            var outcome = await Compose(worker, budget: TimeSpan.FromMilliseconds(200));

            Assert.False(outcome.IsError);
            Assert.Equal(new[] { "genexus_impact" }, worker.Calls);
            Assert.Equal("skipped", outcome.Payload["callers"]!["status"]!.ToString());
            Assert.Equal("skipped", outcome.Payload["regeneration"]!["status"]!.ToString());
            Assert.Equal("skipped", outcome.Payload["database"]!["status"]!.ToString());
            Assert.Equal("complete", outcome.Payload["build"]!["status"]!.ToString());
            Assert.Equal("unknown", outcome.Payload["verdict"]!.ToString());
        }

        [Fact]
        public async Task FailingOrThrowingSection_DoesNotFailTheCall()
        {
            var worker = new FakeWorker();
            worker.Db = (_, _) => Task.FromResult(Err("ReorgFailed", "model busy"));
            var failed = await Compose(worker);
            Assert.False(failed.IsError);
            Assert.Equal("complete", failed.Payload["callers"]!["status"]!.ToString());
            Assert.Equal("skipped", failed.Payload["database"]!["status"]!.ToString());
            Assert.Contains("ReorgFailed", failed.Payload["database"]!["reason"]!.ToString());
            Assert.Equal("unknown", failed.Payload["verdict"]!.ToString());

            var thrower = new FakeWorker();
            thrower.Db = (_, _) => throw new InvalidOperationException("worker gone");
            var thrown = await Compose(thrower);
            Assert.False(thrown.IsError);
            Assert.Contains("worker gone", thrown.Payload["database"]!["reason"]!.ToString());
            Assert.Equal("complete", thrown.Payload["callers"]!["status"]!.ToString());

            var faulted = new FakeWorker();
            faulted.Db = async (_, _) => { await Task.Yield(); throw new InvalidOperationException("faulted later"); };
            var faultedOutcome = await Compose(faulted);
            Assert.False(faultedOutcome.IsError);
            Assert.Contains("faulted later", faultedOutcome.Payload["database"]!["reason"]!.ToString());
            Assert.Equal("complete", faultedOutcome.Payload["callers"]!["status"]!.ToString());

            var buildThrows =await ImpactWhatIf.ComposeAsync(new JObject { ["name"] = "Customer" }, new FakeWorker().Runner,
                () => throw new InvalidOperationException("registry"), Budget, CancellationToken.None);
            Assert.False(buildThrows.IsError);
            Assert.Equal("skipped", buildThrows.Payload["build"]!["status"]!.ToString());
        }

        [Theory]
        [InlineData("AmbiguousName")]
        [InlineData("ObjectNotFound")]
        [InlineData("MissingTarget")]
        [InlineData("IndexNotReady")]
        public async Task UnresolvableTarget_ReturnsTheImpactRefusalUnchangedAndRunsNothingElse(string code)
        {
            var worker = new FakeWorker();
            worker.Impact = (_, _) => Task.FromResult(Err(code, "no single target"));

            var outcome = await Compose(worker);

            Assert.True(outcome.IsError);
            Assert.Equal(code, outcome.Payload["error"]!["code"]!.ToString());
            Assert.Equal(new[] { "genexus_impact" }, worker.Calls);
        }

        [Fact]
        public async Task OtherImpactFailure_DegradesToSkippedSectionsNotAnError()
        {
            var worker = new FakeWorker();
            worker.Impact = (_, _) => Task.FromResult(Err("WorkerCrashed"));

            var outcome = await Compose(worker);

            Assert.False(outcome.IsError);
            Assert.Equal("skipped", outcome.Payload["callers"]!["status"]!.ToString());
            Assert.Contains("WorkerCrashed", outcome.Payload["callers"]!["reason"]!.ToString());
            Assert.Equal(new[] { "genexus_impact" }, worker.Calls);
        }

        [Fact]
        public async Task EveryLegIsAnObservation_NeverDeepNeverAWrite()
        {
            var worker = new FakeWorker();
            await Compose(worker);
            await Compose(new FakeWorker { Impact = (_, _) => Task.FromResult(OkImpact("A", "Attribute", 1)) });

            foreach (var args in worker.CallArgs)
                Assert.Null(args["deep"]);
            for (int i = 0; i < worker.Calls.Count; i++)
            {
                string tool = worker.Calls[i].Split(':')[0];
                Assert.True(OperationClassifier.IsReadOnly(tool, worker.CallArgs[i]), worker.Calls[i] + " must be a read");
            }
        }

        [Fact]
        public void WhatIfCall_IsClassifiedAsAnObservation()
        {
            var args = new JObject { ["name"] = "Customer", ["whatIf"] = true };
            Assert.True(OperationClassifier.IsReadOnly("genexus_impact", args));
            Assert.False(OperationClassifier.IsMutationCandidate("genexus_impact", args));
            Assert.Equal(OperationClassifier.OperationKind.ReadOnly, OperationClassifier.ClassifyTool("genexus_impact", args));
        }

        [Fact]
        public void OnlyAnExplicitBooleanTrueSelectsTheComposedMode()
        {
            Assert.True(Program.IsImpactWhatIf("genexus_impact", new JObject { ["whatIf"] = true }));
            Assert.False(Program.IsImpactWhatIf("genexus_impact", new JObject { ["whatIf"] = false }));
            Assert.False(Program.IsImpactWhatIf("genexus_impact", new JObject { ["name"] = "X" }));
            Assert.False(Program.IsImpactWhatIf("genexus_impact", new JObject { ["whatIf"] = "true" }));
            Assert.False(Program.IsImpactWhatIf("genexus_object_context", new JObject { ["whatIf"] = true }));
            Assert.False(Program.IsImpactWhatIf("genexus_impact", null));
        }

        [Fact]
        public async Task ResponseIsCompact_TopNCallersWithTruncationCounts()
        {
            var worker = new FakeWorker();
            worker.Impact = (_, _) => Task.FromResult(OkImpact("Customer", "Transaction", 200, truncatedBy: "maxNodes"));
            worker.Db = (_, _) => Task.FromResult(OkReorgPreview(changes: 2, destructive: 0, tables: 14));

            var outcome = await Compose(worker);
            var callers = (JObject)outcome.Payload["callers"]!;
            var db = (JObject)outcome.Payload["database"]!;

            Assert.Equal(200, callers["total"]!.Value<int>());
            Assert.Equal(ImpactWhatIf.TopN, ((JArray)callers["shown"]!).Count);
            Assert.Equal(190, callers["omitted"]!.Value<int>());
            Assert.Equal("Caller001", callers["shown"]![0]!["name"]!.ToString());
            Assert.Equal("partial", callers["status"]!.ToString());
            Assert.Equal(201, outcome.Payload["regeneration"]!["objects"]!.Value<int>());
            Assert.Equal(14, db["tablesTotal"]!.Value<int>());
            Assert.Equal(ImpactWhatIf.TopN, ((JArray)db["tables"]!).Count);
            Assert.Equal(4, db["omitted"]!.Value<int>());
            Assert.True(outcome.Payload.ToString(Newtonsoft.Json.Formatting.None).Length < 4000);
        }

        [Fact]
        public async Task FollowUps_NameOnlyPublishedToolsAndActions()
        {
            var outcome = await Compose(new FakeWorker());
            var text = string.Join(" ", new[] { "callers", "regeneration", "database", "build" }
                .Select(s => outcome.Payload[s]!["followUp"]!.ToString()));
            var tools = System.Text.RegularExpressions.Regex.Matches(text, @"genexus_[a-z0-9_]+")
                .Select(m => m.Value).Distinct().ToList();

            Assert.NotEmpty(tools);
            Assert.All(tools, t => Assert.Contains(t, ToolIdentity.CanonicalToolNames, StringComparer.OrdinalIgnoreCase));
            Assert.Contains("reorg_preview", ToolIdentity.ActionsFor("genexus_db"));
            Assert.Contains("sql_ddl", ToolIdentity.ActionsFor("genexus_db"));
            Assert.Contains("build", ToolIdentity.ActionsFor("genexus_lifecycle"));
            Assert.Contains("status", ToolIdentity.ActionsFor("genexus_lifecycle"));
        }

        public static IEnumerable<object?[]> VerdictCases() => new[]
        {
            // callers, trust, truncatedBy, type, db changes, destructive, expected verdict, reason fragment
            new object?[] { 3, "complete", null, "Procedure", 0, 0, "safe", "no pending schema change" },
            new object?[] { 3, "complete", null, "Transaction", 0, 0, "safe", "no pending schema change" },
            new object?[] { 3, "complete", null, "Transaction", 2, 0, "needs-reorg", "2 pending schema change(s)" },
            new object?[] { 3, "complete", null, "Transaction", 1, 1, "needs-reorg", "1 destructive" },
            new object?[] { 25, "complete", null, "Procedure", 0, 0, "wide-impact", "25 callers" },
            new object?[] { 24, "complete", null, "Procedure", 0, 0, "safe", "24 caller(s)" },
            new object?[] { 5, "complete", "maxNodes", "Procedure", 0, 0, "wide-impact", "5+ callers" },
            new object?[] { 30, "complete", null, "Transaction", 1, 0, "needs-reorg", "30 callers also affected" },
            new object?[] { 3, "partial:40", null, "Procedure", 0, 0, "unknown", "callers (partial)" },
            new object?[] { 3, "complete", "maxDepth", "Procedure", 0, 0, "unknown", "callers (partial)" },
            new object?[] { 0, "partial:unconfirmed", null, "Procedure", 0, 0, "unknown", "callers (partial)" },
        };

        [Theory]
        [MemberData(nameof(VerdictCases))]
        public async Task VerdictIsDerivedOnlyFromTheSectionFacts(
            int callers, string trust, string? truncatedBy, string type, int changes, int destructive, string verdict, string reason)
        {
            var worker = new FakeWorker();
            worker.Impact = (_, _) => Task.FromResult(OkImpact("X", type, callers, trust, truncatedBy));
            worker.Db = (_, _) => Task.FromResult(OkReorgPreview(changes, destructive));

            var outcome = await Compose(worker);

            Assert.Equal(verdict, outcome.Payload["verdict"]!.ToString());
            Assert.Contains(reason, outcome.Payload["verdictReason"]!.ToString());
        }

        [Fact]
        public async Task AttributeTargetUsesTheKbWideHeuristicAndNeverClaimsSafe()
        {
            var pending = new FakeWorker
            {
                Impact = (_, _) => Task.FromResult(OkImpact("Amount", "Attribute", 2)),
                Db = (_, _) => Task.FromResult(Ok(new JObject { ["reorgLikelyNeeded"] = true }))
            };
            var clean = new FakeWorker
            {
                Impact = (_, _) => Task.FromResult(OkImpact("Amount", "Attribute", 2)),
                Db = (_, _) => Task.FromResult(Ok(new JObject { ["reorgLikelyNeeded"] = false }))
            };

            var a = await Compose(pending);
            var b = await Compose(clean);

            Assert.Equal("needs-reorg", a.Payload["verdict"]!.ToString());
            Assert.Equal("kb", a.Payload["database"]!["scope"]!.ToString());
            Assert.Equal("partial", a.Payload["database"]!["status"]!.ToString());
            Assert.Equal("unknown", b.Payload["verdict"]!.ToString());
        }

        [Fact]
        public async Task BuildSection_ReportsHistoryOrSaysThereIsNone()
        {
            var with = await Compose(new FakeWorker(), buildSeconds: 73);
            var without = await Compose(new FakeWorker(), buildSeconds: null);

            Assert.Equal(73, with.Payload["build"]!["typicalSeconds"]!.Value<int>());
            Assert.Equal("complete", with.Payload["build"]!["status"]!.ToString());
            Assert.Equal("skipped", without.Payload["build"]!["status"]!.ToString());
            Assert.Contains("no build", without.Payload["build"]!["reason"]!.ToString());
            Assert.Null(without.Payload["build"]!["typicalSeconds"]);
        }

        [Fact]
        public async Task SelectorsAndCapsAreForwardedToTheImpactLeg()
        {
            var worker = new FakeWorker();
            var args = new JObject
            {
                ["whatIf"] = true, ["guid"] = "g1", ["type"] = "Transaction", ["kb"] = "dev",
                ["maxDepth"] = 4, ["maxNodes"] = 50, ["direction"] = "down"
            };

            await Compose(worker, args);

            var forwarded = worker.CallArgs[0];
            Assert.Equal("g1", forwarded["guid"]!.ToString());
            Assert.Equal(4, forwarded["maxDepth"]!.Value<int>());
            Assert.Equal(50, forwarded["maxNodes"]!.Value<int>());
            Assert.Equal("dev", forwarded["kb"]!.ToString());
            Assert.Equal("up", forwarded["direction"]!.ToString());
            Assert.Equal("dev", worker.CallArgs[1]["kb"]!.ToString());
        }

        [Fact]
        public void ToolResultParsing_ReadsPayloadAndErrorCode()
        {
            var ok = Program.ParseWhatIfLeg(new JObject
            {
                ["isError"] = false,
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "{\"status\":\"ok\",\"result\":{\"a\":1}}" })
            });
            Assert.False(ok.IsError);
            Assert.Equal(1, ok.Payload!["result"]!["a"]!.Value<int>());

            var err = Program.ParseWhatIfLeg(new JObject
            {
                ["isError"] = true,
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "{\"status\":\"error\",\"error\":{\"code\":\"AmbiguousName\",\"message\":\"two\"}}" })
            });
            Assert.True(err.IsError);
            Assert.Equal("AmbiguousName", err.Code);
            Assert.Equal("two", err.Message);

            var usage = Program.ParseWhatIfLeg(new JObject { ["isError"] = true, ["usageException"] = true, ["message"] = "bad", ["usageCode"] = "X" });
            Assert.True(usage.IsError);
            Assert.Equal("bad", usage.Message);
        }
    }

    internal static class ImpactWhatIfTestExtensions
    {
        /// <summary>Adds the reorg_preview result keys a Transaction/Table target would carry, for tests that only care about routing.</summary>
        internal static ImpactWhatIf.LegResult WithPreviewShape(this ImpactWhatIf.LegResult leg)
        {
            var result = (JObject)leg.Payload!["result"]!;
            result["tables"] ??= new JArray();
            result["summary"] ??= new JObject { ["changes"] = 0, ["destructiveWarnings"] = 0 };
            return leg;
        }
    }
}
