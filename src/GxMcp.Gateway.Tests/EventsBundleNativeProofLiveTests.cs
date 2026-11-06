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
    // Native proof of an Events bundle audit: while its manifest is `pending`, only
    // the manifest's synthetic WebPanel may be saved. Runs only when
    // GXMCP_EVENTS_NATIVE_PROOF_OUT names the file that receives the sanitized result
    // (check name and pass/fail only), on a disposable KB with both write pins set.
    [Trait("Category", "ProcessSmoke")]
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
                string parts0 = await ReadPreservedPartsAsync(name);

                string marker = "// GXMCP native proof " + Guid.NewGuid().ToString("N");
                var preview = await EditEventsAsync(name, marker, token0, dryRun: true);
                var afterPreview = await ReadEventsAsync(name);
                Record("preview-can-apply", FieldBool(preview, "canApply") && FieldBool(preview, "isolationEligible"), preview);
                Record("preview-no-mutation",
                    !FieldBool(preview, "writeAttempted")
                    && Field(afterPreview, "versionToken")?.ToString() == token0
                    && Field(afterPreview, "source")?.ToString() == source0);

                var save = await EditEventsAsync(name, marker, token0, dryRun: false);
                Record("object-save-receipt",
                    !LiveGatewayHarness.IsToolError(save.Raw)
                    && FieldBool(save, "objectSaved") && FieldBool(save, "partPersisted")
                    && FieldBool(save, "otherPartsIntact")
                    && Field(save, "persistencePath")?.ToString() == "sdk_object_force_save_isolated", save);
                Record("receipt-other-object-metadata-intact",
                    FieldBool(save, "otherObjectMetadataIntact") && FieldBool(save, "verificationCompleted"), save);
                Record("receipt-one-save-no-retry", RevisionStep(save) == 1, save);
                Record("no-implicit-operations", IsEmptyArray(Field(save, "implicitOperations")));
                Record("save-preserves-rules-and-variables", await ReadPreservedPartsAsync(name) == parts0);

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
                Record("worker-restart-preserves-rules-and-variables", await ReadPreservedPartsAsync(name) == parts0);

                var broken = await EditEventsAsync(name, "Event 'GxMcpBroken'\n  &Undefined( = \nEndEvent", token1, dryRun: false);
                var afterBroken = await ReadEventsAsync(name);
                Record("syntax-error-rollback",
                    LiveGatewayHarness.IsToolError(broken.Raw)
                    && !FieldBool(broken, "objectSaved")
                    && Field(afterBroken, "source")?.ToString() == Field(afterReload, "source")?.ToString()
                    && Field(afterBroken, "versionToken")?.ToString() == token1);
                Record("syntax-error-typed-incomplete", Field(broken, "code")?.ToString() == "ObjectSaveIncomplete", broken);

                string concurrent = "// concurrent " + Guid.NewGuid().ToString("N");
                var race = await Task.WhenAll(
                    EditEventsAsync(name, concurrent + " a", token1, dryRun: false),
                    EditEventsAsync(name, concurrent + " b", token1, dryRun: false));
                int winners = race.Count(r => !LiveGatewayHarness.IsToolError(r.Raw) && FieldBool(r, "objectSaved"));
                int conflicts = race.Count(r => LiveGatewayHarness.IsToolError(r.Raw)
                    && new[] { "StaleObject", "VersionConflict" }.Contains(Field(r, "code")?.ToString()));
                Record("queue-one-winner-one-version-conflict", winners == 1 && conflicts == 1);
                Record("queue-loser-refused-before-write",
                    race.Where(r => LiveGatewayHarness.IsToolError(r.Raw)).All(r => !FieldBool(r, "writeAttempted")));
                Record("queue-preserves-rules-and-variables", await ReadPreservedPartsAsync(name) == parts0);

                // A save that outlives the synchronous call is answered by consulting the same operation, never by resending it.
                var beforeAsync = await ReadEventsAsync(name);
                string asyncMarker = "// GXMCP async proof " + Guid.NewGuid().ToString("N");
                var asyncResult = await EditEventsAsyncOperationAsync(name, asyncMarker, Field(beforeAsync, "versionToken")?.ToString() ?? string.Empty);
                var afterAsync = await ReadEventsAsync(name);
                Record("async-operation-result-consulted",
                    asyncResult != null
                    && asyncResult["objectSaved"]?.ToObject<bool?>() == true
                    && (Field(afterAsync, "source")?.ToString() ?? string.Empty).Contains(asyncMarker));
                if (_checks[_checks.Count - 1].Value<bool>("passed") == false)
                    _checks[_checks.Count - 1]["resultKeys"] = asyncResult == null ? null : new JArray(asyncResult.Properties().Select(p => p.Name).Take(40));

                var current = await ReadEventsAsync(name);
                string tokenNow = Field(current, "versionToken")?.ToString() ?? string.Empty;
                var same = await ReplaceUnchangedEventsAsync(name, marker, tokenNow);
                var afterSame = await ReadEventsAsync(name);
                Record("idempotent-save",
                    !LiveGatewayHarness.IsToolError(same.Raw)
                    && FieldBool(same, "idempotent") && FieldBool(same, "noContentChange")
                    && !FieldBool(same, "sourceChanged")
                    && Field(afterSame, "source")?.ToString() == Field(current, "source")?.ToString(), same);
                Record("idempotent-save-runs-the-guarded-save", RevisionStep(same) == 1, same);

                var otherPreviewRead = await ReadEventsAsync(other);
                var otherPreview = await EditEventsAsync(other, "// must be refused", Field(otherPreviewRead, "versionToken")?.ToString() ?? string.Empty, dryRun: true);
                Record("other-object-preview-refused-by-bundle-audit",
                    !FieldBool(otherPreview, "canApply") && otherPreview.Raw.ToString(Formatting.None).Contains("SdkBundleAuditFailed"), otherPreview);

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

        private void Record(string check, bool passed, ToolResult? evidence = null)
        {
            var entry = new JObject { ["check"] = check, ["passed"] = passed };
            if (!passed && evidence != null)
            {
                entry["code"] = Field(evidence, "code")?.ToString();
                entry["message"] = Field(evidence, "message")?.ToString();
                entry["reason"] = Field(evidence, "reason")?.ToString();
                foreach (string key in new[] { "objectSaved", "partPersisted", "otherPartsIntact", "persistencePath", "writeAttempted", "isToolError", "idempotent", "noContentChange", "sourceChanged", "otherObjectMetadataIntact", "verificationCompleted", "revisionBefore", "revisionAfter", "canApply" })
                {
                    var value = key == "isToolError" ? new JValue(LiveGatewayHarness.IsToolError(evidence.Raw)) : Field(evidence, key);
                    if (value != null) entry[key] = value.DeepClone();
                }
            }
            _checks.Add(entry);
        }

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

        private async Task<string> ReadPreservedPartsAsync(string objectName)
        {
            var parts = new List<string>();
            foreach (string part in new[] { "Rules", "Variables" })
            {
                var payload = LiveGatewayHarness.ParseToolPayload(await _harness.CallToolAsync("genexus_read", new JObject
                {
                    ["name"] = objectName,
                    ["part"] = part,
                    ["type"] = "WebPanel"
                }));
                parts.Add(part + "=" + (Field(payload, "source")?.ToString() ?? "<unreadable>"));
            }
            return string.Join("\n", parts);
        }

        private static long? RevisionStep(ToolResult result)
            => long.TryParse(Field(result, "revisionBefore")?.ToString(), out long before)
               && long.TryParse(Field(result, "revisionAfter")?.ToString(), out long after)
                ? after - before
                : (long?)null;

        // Starts the edit as an async operation, polls lifecycle status until it is terminal and returns the worker receipt
        // from lifecycle result (null when no terminal state or receipt is seen within three minutes).
        // genexus_lifecycle result wraps the Worker payload in `workerPayload` or `result`, as an object or as JSON text.
        internal static JObject? UnwrapOperationResult(JObject? envelope)
        {
            if (envelope == null) return null;
            if (envelope["objectSaved"] != null) return envelope;
            foreach (var key in new[] { "workerPayload", "result" })
            {
                var inner = envelope[key];
                JObject? obj = inner as JObject;
                if (obj == null && inner?.Type == JTokenType.String)
                {
                    try { obj = JToken.Parse(inner.ToString()) as JObject; }
                    catch (Newtonsoft.Json.JsonReaderException) { }
                }
                var found = UnwrapOperationResult(obj);
                if (found != null) return found;
            }
            return null;
        }

        private async Task<JObject?> EditEventsAsyncOperationAsync(string objectName, string content, string baseVersion)
        {
            var start = LiveGatewayHarness.ParseToolPayload(await _harness.CallToolAsync("genexus_edit", new JObject
            {
                ["name"] = objectName,
                ["type"] = "WebPanel",
                ["part"] = "Events",
                ["mode"] = "patch",
                ["operation"] = "Append",
                ["content"] = content,
                ["baseVersion"] = baseVersion,
                ["requireObjectSave"] = true,
                ["async"] = true
            }));
            string? operationId = (Field(start, "operationId") ?? Field(start, "job_id"))?.ToString();
            if (string.IsNullOrWhiteSpace(operationId)) return null;

            var terminal = new[] { "succeeded", "failed", "cancelled", "stalled" };
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                var status = LiveGatewayHarness.ParseToolPayload(await _harness.CallToolAsync("genexus_lifecycle", new JObject
                {
                    ["action"] = "status",
                    ["target"] = "op:" + operationId
                }));
                string state = ((Field(status, "status") ?? Field(status, "state"))?.ToString() ?? string.Empty).ToLowerInvariant();
                if (terminal.Contains(state))
                {
                    var result = LiveGatewayHarness.ParseToolPayload(await _harness.CallToolAsync("genexus_lifecycle", new JObject
                    {
                        ["action"] = "result",
                        ["target"] = "op:" + operationId
                    }));
                    return UnwrapOperationResult(result);
                }
                await Task.Delay(1000);
            }
            return null;
        }

        private async Task<ToolResult> ReplaceUnchangedEventsAsync(string objectName, string existingLine, string baseVersion)
        {
            var raw = await _harness.CallToolAsync("genexus_edit", new JObject
            {
                ["name"] = objectName,
                ["type"] = "WebPanel",
                ["part"] = "Events",
                ["mode"] = "patch",
                ["operation"] = "Replace",
                ["context"] = existingLine,
                ["content"] = existingLine,
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
            var result = payload["result"] as JObject;
            return payload[name] ?? result?[name] ?? (result?["result"] as JObject)?[name];
        }

        private static bool FieldBool(ToolResult result, string name) => Field(result, name)?.ToObject<bool?>() == true;

        private static bool IsEmptyArray(JToken? token) => token is JArray array && array.Count == 0;
    }
}
