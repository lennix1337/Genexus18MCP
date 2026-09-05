using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // End-to-end smoke tests against the published Gateway over stdio.
    // Skipped on CI (LiveKbFact gates on GXMCP_TEST_KB). Locally, set the env
    // var to a KB folder path to run the full chain. Each test spawns a fresh
    // Gateway process so state doesn't leak between cases.
    //
    // Coverage matches the v2.6.4 release items the unit tests can't reach:
    //   #1 analyze.explain → NotImplemented envelope (not a fake stub)
    //   #2 query Index pollution removed + _meta.match_quality present
    //   #3 read on invalid part → availableParts + hint
    //   #5 navigation on object with no For Each → NoNavigationBlocks
    //   #6 whoami baseline latency under 500ms
    //   #9 inner-payload error surfaces as result.isError=true
    //   #10 apply_pattern on non-eligible type → rejected in <500ms
    //   #17 apply_pattern { validate: true } → real build envelope, requires WWP
    [Trait("Category", "LiveE2E")]
    public class E2ELiveSmokeTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        // v2.6.9 — share one harness across all tests in the class. Each test
        // previously spawned + killed its own gateway/worker which left shared
        // SDK state (KB lock, COM registration) that crashed the next worker
        // boot mid-cycle. The fixture pattern is also more representative of
        // real MCP usage (one long-lived gateway, many tool calls).
        private readonly LiveGatewayHarness _h;
        private bool _initialized;

        public E2ELiveSmokeTests(LiveGatewayHarness h) { _h = h; }

        public async Task InitializeAsync()
        {
            if (_initialized) return;
            await _h.InitializeAsync();
            _initialized = true;
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task Whoami_BaselineUnder500ms_AndCarriesPlaybooks()
        {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await _h.CallToolAsync("genexus_whoami", new JObject { ["verbose"] = true });
            sw.Stop();
            var payload = LiveGatewayHarness.ParseToolPayload(resp);

            Assert.False(LiveGatewayHarness.IsToolError(resp));
            Assert.True(sw.ElapsedMilliseconds < 500,
                $"whoami baseline must be <500ms; got {sw.ElapsedMilliseconds}ms");
            Assert.NotNull(payload?["playbooks"]);
            Assert.NotNull(payload!["playbooks"]!["wwp_on_webpanel"]);
        }

        [LiveKbFact]
        public async Task AnalyzeExplain_ReturnsNotImplemented_NotStubResponse()
        {
                        // Pick any procedure as the analyze target.
            var list = await _h.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "Procedure",
                ["limit"] = 1
            });
            var listPayload = LiveGatewayHarness.ParseToolPayload(list);
            string procName = listPayload?["results"]?[0]?["name"]?.ToString()
                           ?? listPayload?["items"]?[0]?["name"]?.ToString();
            Assert.False(string.IsNullOrEmpty(procName), "KB must contain at least one procedure");

            var resp = await _h.CallToolAsync("genexus_analyze", new JObject
            {
                ["name"] = procName,
                ["mode"] = "explain",
                ["code"] = "for each\nendfor"
            });
            var payload = LiveGatewayHarness.ParseToolPayload(resp);
            string text = payload?.ToString(Newtonsoft.Json.Formatting.None) ?? "";

            // Regression: must NOT return the legacy stub string.
            Assert.DoesNotContain("Code analysis simulation", text);
            Assert.True(LiveGatewayHarness.IsToolError(resp),
                "explain mode must mark result.isError=true (it is NotImplemented)");
            Assert.Contains("NotImplemented", text, StringComparison.OrdinalIgnoreCase);
        }

        [LiveKbFact]
        public async Task Query_DoesNotPullIndexObjects_AndCarriesMatchQuality()
        {
                        var resp = await _h.CallToolAsync("genexus_query", new JObject
            {
                ["query"] = "Country",
                ["limit"] = 20
            });
            var payload = LiveGatewayHarness.ParseToolPayload(resp);
            var results = payload?["results"] as JArray ?? new JArray();

            int indexCount = results.Count(r => string.Equals(r["type"]?.ToString(), "Index", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, indexCount);

            // _meta.match_quality must be present in all query envelopes.
            var meta = payload?["_meta"] as JObject;
            Assert.NotNull(meta);
            Assert.NotNull(meta!["match_quality"]);
        }

        [LiveKbFact]
        public async Task Read_InvalidPart_ErrorHintsAvailableParts()
        {
                        // Find a procedure so we know which parts are valid.
            var list = await _h.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "Procedure",
                ["limit"] = 1
            });
            string procName = LiveGatewayHarness.ParseToolPayload(list)?["results"]?[0]?["name"]?.ToString()
                           ?? LiveGatewayHarness.ParseToolPayload(list)?["items"]?[0]?["name"]?.ToString();
            Assert.False(string.IsNullOrEmpty(procName));

            var resp = await _h.CallToolAsync("genexus_read", new JObject
            {
                ["name"] = procName,
                ["part"] = "BogusZzz"
            });
            var payload = LiveGatewayHarness.ParseToolPayload(resp);
            string text = payload?.ToString(Newtonsoft.Json.Formatting.None) ?? "";

            Assert.True(LiveGatewayHarness.IsToolError(resp));
            // The error envelope must mention valid parts (#3) and behaviour
            // must trip MCP isError (#9 — inner-payload error detection).
            Assert.True(
                text.Contains("Valid parts for", StringComparison.OrdinalIgnoreCase)
                || text.Contains("availableParts", StringComparison.OrdinalIgnoreCase),
                "read error must include availableParts / hint. payload=" + text);
        }

        [LiveKbFact(requiresNavigation: true)]
        public async Task Navigation_NoForEachBlocks_ReturnsNoNavigationBlocksStatus()
        {
            const int pageSize = 200;
            const int maxPageRequests = 1000;
            const int maxTransientRetries = 8;
            var maxDuration = TimeSpan.FromMinutes(2);
            var budget = System.Diagnostics.Stopwatch.StartNew();
            int offset = 0;
            int pageRequests = 0;
            int transientRetries = 0;
            var observed = new System.Collections.Generic.List<string>();
            JObject? hit = null;

            while (hit == null)
            {
                if (pageRequests >= maxPageRequests)
                    Assert.Fail($"Inconclusive: Procedure pagination reached the safety cap of {maxPageRequests} pages before a terminal page.");
                int remainingBeforePage = (int)Math.Max(0, (maxDuration - budget.Elapsed).TotalMilliseconds);
                if (remainingBeforePage <= 0)
                    Assert.Fail($"Inconclusive: Procedure navigation smoke exceeded its global {maxDuration.TotalSeconds:0}s budget after {pageRequests} page requests.");

                pageRequests++;
                JObject list;
                try
                {
                    list = await _h.CallToolAsync("genexus_list_objects", new JObject
                    {
                        ["typeFilter"] = "Procedure",
                        ["limit"] = pageSize,
                        ["offset"] = offset,
                        ["sort"] = "name"
                    }, timeoutMs: remainingBeforePage);
                }
                catch (TimeoutException ex)
                {
                    Assert.Fail($"Inconclusive: Procedure listing exceeded the global {maxDuration.TotalSeconds:0}s budget ({ex.Message}).");
                    throw;
                }
                var listPayload = LiveGatewayHarness.ParseToolPayload(list);
                var decision = NavigationListStateMachine.Evaluate(
                    listPayload, LiveGatewayHarness.IsToolError(list), offset);
                if (decision.Kind == NavigationListDecisionKind.Retry)
                {
                    transientRetries++;
                    if (transientRetries > maxTransientRetries)
                        Assert.Fail("Inconclusive: Procedure listing did not become complete after transient retries. Last reason: " + decision.Reason);

                    int? etaMs = listPayload?["etaMs"]?.Value<int?>();
                    int delayMs = etaMs.HasValue && etaMs.Value > 0
                        ? Math.Min(5000, Math.Max(250, etaMs.Value))
                        : Math.Min(2000, 250 * transientRetries);
                    int remainingBeforeDelay = (int)Math.Max(0, (maxDuration - budget.Elapsed).TotalMilliseconds);
                    if (remainingBeforeDelay <= 0)
                        Assert.Fail($"Inconclusive: Procedure listing exceeded its global {maxDuration.TotalSeconds:0}s budget while waiting for the index.");
                    await Task.Delay(Math.Min(delayMs, remainingBeforeDelay));
                    continue;
                }

                transientRetries = 0;
                if (decision.Kind == NavigationListDecisionKind.Fail)
                    Assert.Fail("Procedure listing failed: " + (listPayload?.ToString(Newtonsoft.Json.Formatting.None) ?? "<unparseable>"));
                if (decision.Kind == NavigationListDecisionKind.Exhausted)
                    break;

                if (listPayload == null)
                    throw new InvalidOperationException("Procedure listing returned no parseable payload.");
                var items = listPayload["results"] as JArray
                    ?? listPayload["items"] as JArray;
                if (items == null)
                    throw new InvalidOperationException("Procedure listing returned no result array after a process decision.");

                foreach (var item in items)
                {
                    int remainingBeforeNavigation = (int)Math.Max(0, (maxDuration - budget.Elapsed).TotalMilliseconds);
                    if (remainingBeforeNavigation <= 0)
                        Assert.Fail($"Inconclusive: Procedure navigation smoke exceeded its global {maxDuration.TotalSeconds:0}s budget after {pageRequests} page requests.");

                    string? name = item["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    JObject nav;
                    try
                    {
                        nav = await _h.CallToolAsync("genexus_analyze", new JObject
                        {
                            ["name"] = name,
                            ["mode"] = "navigation"
                        }, timeoutMs: remainingBeforeNavigation);
                    }
                    catch (TimeoutException ex)
                    {
                        Assert.Fail($"Inconclusive: navigation analysis exceeded the global {maxDuration.TotalSeconds:0}s budget ({ex.Message}).");
                        throw;
                    }
                    var navPayload = LiveGatewayHarness.ParseToolPayload(nav);
                    if (navPayload == null)
                    {
                        observed.Add(name + "=<unparseable>");
                        continue;
                    }

                    var levels = navPayload["levels"] as JArray;
                    string navStatus = navPayload["status"]?.ToString()
                        ?? navPayload["code"]?.ToString()
                        ?? (LiveGatewayHarness.IsToolError(nav) ? "error" : "unknown");
                    observed.Add(name + "=" + navStatus);
                    bool validNoBlocks = !LiveGatewayHarness.IsToolError(nav)
                        && levels != null
                        && levels.Count == 0
                        && string.Equals(navPayload["status"]?.ToString(), "NoNavigationBlocks", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(navPayload["hint"]?.ToString());
                    if (validNoBlocks)
                    {
                        hit = navPayload;
                        break;
                    }
                }

                if (hit != null) break;

                if (decision.NextOffset.HasValue)
                {
                    offset = decision.NextOffset.Value;
                    continue;
                }
                break;
            }

            Assert.True(hit != null,
                $"No Procedure returned NoNavigationBlocks after {pageRequests} page requests. Observed: " +
                string.Join(", ", observed.Take(40)));
            Assert.Equal("NoNavigationBlocks", hit!["status"]?.ToString());
            Assert.NotNull(hit["hint"]);
        }

        [LiveKbFact]
        public async Task ApplyPattern_OnProcedure_RejectedFast_WithValidParentTypes()
        {
            // Item #10 — non-eligible types must be rejected upfront (<500ms)
            // with the validParentTypes routing hint.
                        var list = await _h.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "Procedure",
                ["limit"] = 1
            });
            string proc = LiveGatewayHarness.ParseToolPayload(list)?["results"]?[0]?["name"]?.ToString()
                       ?? LiveGatewayHarness.ParseToolPayload(list)?["items"]?[0]?["name"]?.ToString();
            Assert.False(string.IsNullOrEmpty(proc));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await _h.CallToolAsync("genexus_apply_pattern", new JObject
            {
                ["name"] = proc,
                ["pattern"] = "WorkWithPlus"
            }, timeoutMs: 10_000);
            sw.Stop();
            var payload = LiveGatewayHarness.ParseToolPayload(resp);

            Assert.True(LiveGatewayHarness.IsToolError(resp));
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"apply_pattern rejection on Procedure must be <2s; got {sw.ElapsedMilliseconds}ms");
            Assert.NotNull(payload?["validParentTypes"]);
            Assert.Equal("Procedure", payload!["parentType"]?.ToString());
        }

        [LiveKbFact(requiresWWP: true)]
        public async Task ApplyPattern_Validate_HappyPath_OnWebPanel()
        {
            // Item #17 happy path — creates a disposable WebPanel, applies WWP
            // with validate:true, asserts the validation block is populated.
            // Tagged requiresWWP since direct-attach needs the WorkWithPlus
            // license. The disposable name is logged so the user can clean up
            // in the IDE if the test crashes before deletion.
                        // v2.6.9 — `Substring(0, 6)` previously sliced the HIGH-order hex digits
            // of Ticks, which change slowly (~hours). Two test runs in the same
            // window collided on the same disposable name, and the second run
            // failed at create_object with "Web Panel already exists". Take the
            // LAST 6 hex digits (low-order, ~100ns granularity) so the name is
            // unique across rapid re-runs.
            string ticksHex = DateTime.UtcNow.Ticks.ToString("X");
            string stamp = ticksHex.Substring(ticksHex.Length - 6).ToLowerInvariant();
            string wp = "TestVldWp" + stamp;

            var create = await _h.CallToolAsync("genexus_create_object", new JObject
            {
                ["type"] = "WebPanel",
                ["name"] = wp
            }, timeoutMs: 60_000);
            // v2.6.9 — when create fails (transient SDK / leftover-state collision),
            // surface the actual envelope so a follow-up triage doesn't have to
            // re-run with a custom diagnostic harness.
            if (LiveGatewayHarness.IsToolError(create))
            {
                var createPayload = LiveGatewayHarness.ParseToolPayload(create);
                throw new Xunit.Sdk.XunitException(
                    "WebPanel create must succeed. Envelope: "
                    + (createPayload?.ToString(Newtonsoft.Json.Formatting.None) ?? "<null>"));
            }

            var apply = await _h.CallToolAsync("genexus_apply_pattern", new JObject
            {
                ["name"] = wp,
                ["pattern"] = "WorkWithPlus",
                ["validate"] = true
            }, timeoutMs: 240_000);
            var payload = LiveGatewayHarness.ParseToolPayload(apply);

            Console.WriteLine($"[E2E disposable] WebPanel={wp} host={payload?["patternHost"]} — delete manually in IDE if leaked.");
            Console.WriteLine($"[E2E apply payload] {payload?.ToString(Newtonsoft.Json.Formatting.None)}");

            Assert.Equal("WebPanel", payload?["parentType"]?.ToString());
            Assert.Equal("webpanel-direct-attach", payload?["bindingMode"]?.ToString());
            Assert.NotNull(payload?["patternHost"]);

            var validation = payload?["validation"] as JObject;
            Assert.NotNull(validation);
            Assert.NotNull(validation!["status"]);
            Assert.NotNull(validation["durationMs"]);
            // Real build runs always exceed a few seconds — a sub-100ms
            // duration would mean we parsed a "Running" envelope as success
            // (the bug found and fixed in v2.6.4 dev).
            Assert.True(validation["durationMs"]!.ToObject<long>() > 2000,
                "validation must reflect a real build (>2s)");
        }

        [LiveKbFact]
        public async Task Edit_AutoDeclareVariables_CreatesVariablesOnSourceWrite()
        {
            string stamp = DateTime.UtcNow.Ticks.ToString("X").Substring(DateTime.UtcNow.Ticks.ToString("X").Length - 6).ToLowerInvariant();
            string proc = "TestAutoVar" + stamp;

            try
            {
                var create = await _h.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = "Procedure",
                    ["name"] = proc
                });
                Assert.True(!LiveGatewayHarness.IsToolError(create), "create failed: " + create?.ToString(Newtonsoft.Json.Formatting.None));

                var edit = await _h.CallToolAsync("genexus_edit", new JObject
                {
                    ["name"] = proc,
                    ["part"] = "Source",
                    ["mode"] = "full",
                    ["content"] = "&TotalCount = 100\r\n&DescriptionTag = 'LiveTest'",
                    ["autoDeclareVariables"] = true
                });
                Assert.True(!LiveGatewayHarness.IsToolError(edit), "edit failed: " + edit?.ToString(Newtonsoft.Json.Formatting.None));

                var readVars = await _h.CallToolAsync("genexus_read", new JObject
                {
                    ["name"] = proc,
                    ["part"] = "Variables"
                });
                var payload = LiveGatewayHarness.ParseToolPayload(readVars);
                string text = payload?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                Assert.True(text.IndexOf("TotalCount", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"readVars text missing TotalCount. payload={text}, raw={readVars?.ToString(Newtonsoft.Json.Formatting.None)}");
                Assert.True(text.IndexOf("DescriptionTag", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"readVars text missing DescriptionTag. payload={text}, raw={readVars?.ToString(Newtonsoft.Json.Formatting.None)}");
            }
            finally
            {
                await _h.CallToolAsync("genexus_delete_object", new JObject { ["name"] = proc, ["confirm"] = true });
            }
        }

        [LiveKbFact]
        public async Task Refactor_ExtractSubroutine_UpdatesSourceAndAddsSub()
        {
            string stamp = DateTime.UtcNow.Ticks.ToString("X").Substring(DateTime.UtcNow.Ticks.ToString("X").Length - 6).ToLowerInvariant();
            string proc = "TestExtSub" + stamp;

            try
            {
                var create = await _h.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = "Procedure",
                    ["name"] = proc
                });
                Assert.False(LiveGatewayHarness.IsToolError(create));

                var edit = await _h.CallToolAsync("genexus_edit", new JObject
                {
                    ["name"] = proc,
                    ["part"] = "Source",
                    ["mode"] = "full",
                    ["content"] = "&Counter = 1\r\n&Total = 50\r\n&Counter = &Counter + 1"
                });
                Assert.False(LiveGatewayHarness.IsToolError(edit));

                var extract = await _h.CallToolAsync("genexus_refactor", new JObject
                {
                    ["action"] = "ExtractSubroutine",
                    ["target"] = proc,
                    ["code"] = "&Total = 50",
                    ["subroutineName"] = "InitTotal",
                    ["dryRun"] = false
                });
                Assert.False(LiveGatewayHarness.IsToolError(extract));

                var readSource = await _h.CallToolAsync("genexus_read", new JObject
                {
                    ["name"] = proc,
                    ["part"] = "Source"
                });
                var payload = LiveGatewayHarness.ParseToolPayload(readSource);
                string source = payload?["content"]?.ToString() ?? payload?["source"]?.ToString() ?? payload?.ToString() ?? "";
                Assert.Contains("Do 'InitTotal'", source);
                Assert.Contains("Sub 'InitTotal'", source);
                Assert.Contains("&Total = 50", source);
                Assert.Contains("EndSub", source);
            }
            finally
            {
                await _h.CallToolAsync("genexus_delete_object", new JObject { ["name"] = proc, ["confirm"] = true });
            }
        }

        [LiveKbFact]
        public async Task Transfer_Export_WithDependencies_IncludesGraphClosure()
        {
            string stamp = DateTime.UtcNow.Ticks.ToString("X").Substring(DateTime.UtcNow.Ticks.ToString("X").Length - 6).ToLowerInvariant();
            string proc = "TestExport" + stamp;
            string tempXpz = Path.Combine(Path.GetTempPath(), proc + ".xpz");

            try
            {
                var create = await _h.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = "Procedure",
                    ["name"] = proc
                });
                Assert.False(LiveGatewayHarness.IsToolError(create));

                var export = await _h.CallToolAsync("genexus_transfer", new JObject
                {
                    ["action"] = "export",
                    ["targets"] = new JArray { proc },
                    ["includeDependencies"] = true,
                    ["outputFile"] = tempXpz
                });
                Assert.False(LiveGatewayHarness.IsToolError(export));

                var payload = LiveGatewayHarness.ParseToolPayload(export);
                var res = payload?["result"] as JObject ?? payload;
                Assert.True(res?["includeDependencies"]?.ToObject<bool>() == true, "export payload=" + payload?.ToString(Newtonsoft.Json.Formatting.None));
                Assert.NotNull(res?["dependenciesAdded"]);
                Assert.True(File.Exists(tempXpz), "Exported .xpz file must exist");
            }
            finally
            {
                if (File.Exists(tempXpz)) File.Delete(tempXpz);
                await _h.CallToolAsync("genexus_delete_object", new JObject { ["name"] = proc, ["confirm"] = true });
            }
        }
    }
}
