using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Native proof of an Events bundle audit whose manifest is still `pending`: only
    // the manifest's synthetic WebPanel may be saved. Runs only when
    // GXMCP_EVENTS_NATIVE_PROOF_OUT names the file that receives the sanitized result
    // (check name and pass/fail only), on a disposable KB with both write pins set.
    [Trait("Category", "LiveEventsNativeProof")]
    public sealed class EventsBundleNativeProofLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private const string ProofOutVariable = "GXMCP_EVENTS_NATIVE_PROOF_OUT";
        private const string ProofObjectVariable = "GXMCP_EVENTS_PROOF_OBJECT";
        private const string OtherObjectVariable = "GXMCP_EVENTS_PROOF_OTHER_OBJECT";

        private readonly LiveGatewayHarness _harness;
        private readonly List<JObject> _checks = new List<JObject>();

        public EventsBundleNativeProofLiveTests(LiveGatewayHarness harness)
        {
            _harness = harness;
        }

        public Task InitializeAsync()
            => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProofOutVariable))
                ? Task.CompletedTask
                : _harness.InitializeAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task PendingBundle_SavesOnlyTheSyntheticObject_WithVerifiedIsolation()
        {
            string? outPath = Environment.GetEnvironmentVariable(ProofOutVariable);
            if (string.IsNullOrWhiteSpace(outPath)) return;

            string name = Environment.GetEnvironmentVariable(ProofObjectVariable) ?? "McpU10AuditProbe01";
            string other = Environment.GetEnvironmentVariable(OtherObjectVariable) ?? "McpU10AuditOther01";
            var created = new List<string>();
            try
            {
                int? countBefore = await CountObjectsAsync();
                Record("object-count-readable-before", countBefore.HasValue);

                await CreateWebPanelAsync(name, "synthetic", created);
                await CreateWebPanelAsync(other, "other", created);

                var initial = await ReadEventsAsync(name);
                string token0 = Field(initial, "versionToken")?.ToString() ?? string.Empty;
                string source0 = Field(initial, "source")?.ToString() ?? string.Empty;
                Record("initial-read-has-version-token", token0.Length > 0);

                string marker = "// GXMCP native proof " + Guid.NewGuid().ToString("N");
                var preview = await EditEventsAsync(name, marker, token0, dryRun: true);
                var afterPreview = await ReadEventsAsync(name);
                Record("preview-can-apply", FieldBool(preview, "canApply") && FieldBool(preview, "isolationEligible"));
                Record("preview-no-mutation",
                    !FieldBool(preview, "writeAttempted")
                    && Field(afterPreview, "versionToken")?.ToString() == token0
                    && Field(afterPreview, "source")?.ToString() == source0);

                var save = await EditEventsAsync(name, marker, token0, dryRun: false);
                Record("object-save-receipt",
                    !LiveGatewayHarness.IsToolError(save.Raw)
                    && FieldBool(save, "objectSaved") && FieldBool(save, "partPersisted")
                    && FieldBool(save, "otherPartsIntact")
                    && Field(save, "persistencePath")?.ToString() == "object_save");
                Record("no-implicit-operations", IsEmptyArray(Field(save, "implicitOperations")));

                var fresh = await ReadEventsAsync(name);
                string token1 = Field(fresh, "versionToken")?.ToString() ?? string.Empty;
                Record("fresh-reread-has-marker",
                    (Field(fresh, "source")?.ToString() ?? string.Empty).Contains(marker) && token1.Length > 0 && token1 != token0);

                var stale = await EditEventsAsync(name, "// stale " + Guid.NewGuid().ToString("N"), token0, dryRun: false);
                Record("stale-token-rejected",
                    LiveGatewayHarness.IsToolError(stale.Raw)
                    && new[] { "StaleObject", "VersionConflict", "VersionCheckUnavailable" }.Contains(Field(stale, "code")?.ToString()));

                var reload = await _harness.CallToolAsync("genexus_worker_reload", new JObject { ["mode"] = "soft" });
                var afterReload = await ReadEventsAsync(name);
                Record("worker-restart-reread",
                    !LiveGatewayHarness.IsToolError(reload)
                    && (Field(afterReload, "source")?.ToString() ?? string.Empty).Contains(marker)
                    && Field(afterReload, "versionToken")?.ToString() == token1);

                var broken = await EditEventsAsync(name, "Event 'GxMcpBroken'\n  &Undefined( = \nEndEvent", token1, dryRun: false);
                var afterBroken = await ReadEventsAsync(name);
                Record("syntax-error-rollback",
                    LiveGatewayHarness.IsToolError(broken.Raw)
                    && !FieldBool(broken, "objectSaved")
                    && Field(afterBroken, "source")?.ToString() == Field(afterReload, "source")?.ToString()
                    && Field(afterBroken, "versionToken")?.ToString() == token1);

                string concurrent = "// concurrent " + Guid.NewGuid().ToString("N");
                var race = await Task.WhenAll(
                    EditEventsAsync(name, concurrent + " a", token1, dryRun: false),
                    EditEventsAsync(name, concurrent + " b", token1, dryRun: false));
                int winners = race.Count(r => !LiveGatewayHarness.IsToolError(r.Raw) && FieldBool(r, "objectSaved"));
                int conflicts = race.Count(r => LiveGatewayHarness.IsToolError(r.Raw)
                    && new[] { "StaleObject", "VersionConflict" }.Contains(Field(r, "code")?.ToString()));
                Record("queue-one-winner-one-version-conflict", winners == 1 && conflicts == 1);

                var current = await ReadEventsAsync(name);
                string tokenNow = Field(current, "versionToken")?.ToString() ?? string.Empty;
                var same = await EditFullEventsAsync(name, Field(current, "source")?.ToString() ?? string.Empty, tokenNow);
                var afterSame = await ReadEventsAsync(name);
                Record("idempotent-save",
                    !LiveGatewayHarness.IsToolError(same.Raw)
                    && Field(afterSame, "source")?.ToString() == Field(current, "source")?.ToString());

                var otherRead = await ReadEventsAsync(other);
                var otherSave = await EditEventsAsync(other, "// must be refused", Field(otherRead, "versionToken")?.ToString() ?? string.Empty, dryRun: false);
                var otherAfter = await ReadEventsAsync(other);
                Record("other-object-refused-while-pending",
                    LiveGatewayHarness.IsToolError(otherSave.Raw)
                    && !FieldBool(otherSave, "objectSaved")
                    && Field(otherAfter, "source")?.ToString() == Field(otherRead, "source")?.ToString());

                foreach (string target in created.ToArray())
                {
                    var delete = await _harness.CallToolAsync("genexus_delete_object", new JObject { ["name"] = target, ["confirm"] = true });
                    if (!LiveGatewayHarness.IsToolError(delete)) created.Remove(target);
                }
                Record("synthetic-objects-removed", created.Count == 0);
                int? countAfter = await CountObjectsAsync();
                Record("object-count-unchanged", countBefore.HasValue && countAfter == countBefore);
            }
            finally
            {
                foreach (string target in created)
                {
                    try { await _harness.CallToolAsync("genexus_delete_object", new JObject { ["name"] = target, ["confirm"] = true }); }
                    catch { /* reported by synthetic-objects-removed */ }
                }
                WriteProof(outPath!);
            }

            var failed = _checks.Where(c => c.Value<bool>("passed") == false).Select(c => c.Value<string>("check")).ToArray();
            Assert.True(failed.Length == 0, "native proof checks failed: " + string.Join(", ", failed));
        }

        private sealed class ToolResult
        {
            public JObject Raw = new JObject();
            public JObject? Payload;
        }

        private void Record(string check, bool passed)
            => _checks.Add(new JObject { ["check"] = check, ["passed"] = passed });

        private void WriteProof(string path)
        {
            var proof = new JObject
            {
                ["total"] = _checks.Count,
                ["failed"] = _checks.Count(c => c.Value<bool>("passed") == false),
                ["checks"] = new JArray(_checks)
            };
            File.WriteAllText(path, proof.ToString(Formatting.Indented) + "\n");
        }

        private async Task CreateWebPanelAsync(string objectName, string label, List<string> created)
        {
            var create = await _harness.CallToolAsync("genexus_create", new JObject
            {
                ["action"] = "object",
                ["type"] = "WebPanel",
                ["name"] = objectName
            });
            bool ok = !LiveGatewayHarness.IsToolError(create);
            if (ok) created.Add(objectName);
            Record("create-" + label + "-webpanel", ok);
        }

        private async Task<JObject?> ReadEventsAsync(string objectName)
            => LiveGatewayHarness.ParseToolPayload(await _harness.CallToolAsync("genexus_read", new JObject
            {
                ["name"] = objectName,
                ["part"] = "Events",
                ["type"] = "WebPanel"
            }));

        private async Task<ToolResult> EditEventsAsync(string objectName, string content, string baseVersion, bool dryRun)
        {
            var raw = await _harness.CallToolAsync("genexus_edit", new JObject
            {
                ["name"] = objectName,
                ["type"] = "WebPanel",
                ["part"] = "Events",
                ["mode"] = "patch",
                ["operation"] = "Append",
                ["content"] = content,
                ["baseVersion"] = baseVersion,
                ["requireObjectSave"] = true,
                ["dryRun"] = dryRun
            });
            return new ToolResult { Raw = raw, Payload = LiveGatewayHarness.ParseToolPayload(raw) };
        }

        private async Task<ToolResult> EditFullEventsAsync(string objectName, string content, string baseVersion)
        {
            var raw = await _harness.CallToolAsync("genexus_edit", new JObject
            {
                ["name"] = objectName,
                ["type"] = "WebPanel",
                ["part"] = "Events",
                ["mode"] = "full",
                ["content"] = content,
                ["baseVersion"] = baseVersion,
                ["requireObjectSave"] = true
            });
            return new ToolResult { Raw = raw, Payload = LiveGatewayHarness.ParseToolPayload(raw) };
        }

        private async Task<int?> CountObjectsAsync()
        {
            var payload = LiveGatewayHarness.ParseToolPayload(await _harness.CallToolAsync("genexus_list_objects", new JObject { ["limit"] = 1 }));
            foreach (string key in new[] { "total", "totalCount", "count" })
            {
                var value = Field(payload, key);
                if (value != null && value.Type == JTokenType.Integer) return value.ToObject<int>();
            }
            return null;
        }

        private static JToken? Field(ToolResult result, string name) => Field(result.Payload, name);

        private static JToken? Field(JObject? payload, string name)
        {
            if (payload == null) return null;
            return payload[name] ?? payload["result"]?[name] ?? payload["result"]?["result"]?[name];
        }

        private static bool FieldBool(ToolResult result, string name) => Field(result, name)?.ToObject<bool?>() == true;

        private static bool IsEmptyArray(JToken? token) => token is JArray array && array.Count == 0;
    }
}
