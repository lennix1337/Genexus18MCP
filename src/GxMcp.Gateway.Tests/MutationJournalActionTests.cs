using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public class MutationJournalActionTests
    {
        [Fact]
        public async Task JournalStatusIsReachableThroughMcpWithoutSelectingKb()
        {
            var response = await Program.ProcessMcpRequest(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = "journal-status", ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_connection_recover",
                    ["arguments"] = new JObject { ["action"] = "journal_status" }
                }
            }, "journal-no-kb-" + Guid.NewGuid().ToString("N"));
            Assert.Null(response!["error"]);
            var payload = JObject.Parse(response["result"]!["content"]![0]!["text"]!.Value<string>()!);
            Assert.NotNull(payload["healthy"]);
            Assert.NotNull(payload["pendingCount"]);
        }

        [Fact]
        public void RepairDefaultsToPreviewAndNeverStartsAWorker()
        {
            string root = Path.Combine(Path.GetTempPath(), "gx-journal-action-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "journal.json");
                var registry = new MutationRecoveryRegistry(path);
                registry.RequireRead("synthetic", "SyntheticProcedure", "Source", "op");
                string before = File.ReadAllText(path);
                var preview = Program.HandleMutationJournalAction(registry, new JObject { ["action"] = "journal_repair" });
                Assert.True(preview!["dryRun"]!.Value<bool>());
                Assert.False(preview["persisted"]!.Value<bool>());
                Assert.Equal(before, File.ReadAllText(path));
                var applied = Program.HandleMutationJournalAction(registry, new JObject { ["action"] = "journal_repair", ["dryRun"] = false });
                Assert.True(applied!["persisted"]!.Value<bool>());
                Assert.True(applied["verified"]!.Value<bool>());
                Assert.Equal(1, registry.Count);
                Assert.Null(Program.HandleMutationJournalAction(registry, new JObject()));
                Assert.NotNull(Program.HandleMutationJournalAction(registry,
                    new JObject { ["action"] = "journal_repair", ["force"] = true })!["error"]);
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("journal_status", true, "file.read")]
        [InlineData("journal_repair", true, "file.read")]
        [InlineData("journal_repair", false, "file.write")]
        public void JournalActionsAreGatewayOnlyAndUncached(string action, bool dryRun, string effect)
        {
            var args = new JObject { ["action"] = action, ["dryRun"] = dryRun };
            var contract = OperationClassifier.Describe("genexus_connection_recover", args);
            bool preview = action == "journal_status" || dryRun;
            Assert.Equal(preview ? OperationClassifier.OperationKind.ReadOnly : OperationClassifier.OperationKind.Mutating, contract.Kind);
            Assert.Equal(effect, contract.Effects);
            Assert.Equal("gateway", contract.Execution);
            Assert.Equal(preview ? "safe" : "operation_key", contract.Retry);
            Assert.Equal("never", contract.Cache);
            Assert.Equal(preview ? Array.Empty<string>() : new[] { "files" }, contract.Invalidation);
            Assert.Equal(action == "journal_repair", contract.PreviewSupported);
            Assert.False(OperationClassifier.RequiresSessionLease("genexus_connection_recover", args));
        }

        [Fact]
        public void JournalRepairMissingOrNullDryRunIsAReadOnlyPreview()
        {
            var missing = new JObject { ["action"] = "journal_repair" };
            var nullValue = new JObject { ["action"] = "journal_repair", ["dryRun"] = JValue.CreateNull() };

            foreach (JObject args in new[] { missing, nullValue })
            {
                var contract = OperationClassifier.Describe("genexus_connection_recover", args);
                Assert.Equal(OperationClassifier.OperationKind.ReadOnly, contract.Kind);
                Assert.Equal("file.read", contract.Effects);
                Assert.Equal("gateway", contract.Execution);
                Assert.Equal("safe", contract.Retry);
                Assert.Equal("never", contract.Cache);
                Assert.Empty(contract.Invalidation);
                Assert.True(contract.PreviewSupported);
            }
        }

        [Theory]
        [InlineData(false, 0, "token", true)]
        [InlineData(true, 0, "token", false)]
        [InlineData(false, 20, "token", false)]
        [InlineData(false, 0, "", false)]
        public void PartialOrUnversionedReadsCannotClearFence(bool truncated, int offset, string token, bool expected)
            => Assert.Equal(expected, Program.IsCompleteMutationRecoveryRead(new JObject
            { ["truncated"] = truncated, ["offset"] = offset, ["versionToken"] = token }));

        [Fact]
        public void GatewayContentTruncationCannotConfirmButDerivedMetadataTrimmingCan()
        {
            var payload = new JObject { ["versionToken"] = "token", ["isTruncated"] = true };
            Assert.True(Program.IsCompleteMutationRecoveryRead(payload));
            payload["truncatedByGateway"] = true;
            Assert.False(Program.IsCompleteMutationRecoveryRead(payload));
            payload.Remove("truncatedByGateway");
            payload["source"] = new string('x', 2000);
            var (guarded, truncated) = new ResponseSizeGuard(100, _ => { }).Apply(payload, "genexus_read", new JObject());
            Assert.True(truncated);
            Assert.False(Program.IsCompleteMutationRecoveryRead(guarded));
        }

        [Fact]
        public void ToolSpecificRecoveryPartsAndPartsArrayAreEnumerated()
        {
            var variableTargets = Program.EnumerateMutationRecoveryTargets(
                "genexus_variable",
                new JObject { ["name"] = "Panel", ["action"] = "add" }).ToList();
            Assert.Equal(new[] { ("Panel", "Variables") }, variableTargets);

            var formTargets = Program.EnumerateMutationRecoveryTargets(
                "genexus_edit_form",
                new JObject { ["name"] = "Panel", ["action"] = "add_button" }).ToList();
            Assert.Contains(("Panel", "WebForm"), formTargets);

            var layoutTargets = Program.EnumerateMutationRecoveryTargets(
                "genexus_layout",
                new JObject { ["name"] = "Panel", ["action"] = "set_property" }).ToList();
            Assert.Contains(("Panel", "WebForm"), layoutTargets);
            var reportTargets = Program.EnumerateMutationRecoveryTargets(
                "genexus_layout",
                new JObject { ["name"] = "Report", ["action"] = "add_printblock" }).ToList();
            Assert.Contains(("Report", "Layout"), reportTargets);

            var multiPartTargets = Program.EnumerateMutationRecoveryTargets(
                "genexus_read",
                new JObject
                {
                    ["name"] = "Panel",
                    ["parts"] = new JArray("Variables", "Rules")
                }).ToList();
            Assert.Equal(new[] { ("Panel", "Rules"), ("Panel", "Variables") }, multiPartTargets);
            Assert.DoesNotContain(("Panel", "Source"), multiPartTargets);
        }

        [Fact]
        public void IoBatchMutationsEnumerateAffectedObjectFences()
        {
            var deleteTargets = Program.EnumerateMutationRecoveryTargets(
                "genexus_io",
                new JObject
                {
                    ["action"] = "delete_kb_objects",
                    ["targets"] = new JArray("Procedure:OldProc", "Transaction:OldTrans")
                }).ToList();
            Assert.Equal(new[] { "Procedure:OldProc", "Transaction:OldTrans" },
                deleteTargets.Select(item => item.Target));
            Assert.All(deleteTargets, item => Assert.Equal("Source", item.Part));

            var importTargets = Program.EnumerateMutationRecoveryTargets(
                "genexus_io",
                new JObject
                {
                    ["action"] = "import_text_to_kb",
                    ["name"] = "ImportedProcedure",
                    ["part"] = "Rules"
                }).ToList();
            Assert.Equal(("ImportedProcedure", "Rules"), Assert.Single(importTargets));
        }

        [Fact]
        public void ManifestImportWithoutObjectSelectorUsesKbLevelRecoveryFence()
        {
            var targets = Program.EnumerateMutationRecoveryTargets(
                "genexus_io",
                new JObject
                {
                    ["action"] = "import_text_to_kb",
                    ["inputPath"] = "C:/manifests/import.json"
                }).ToList();

            Assert.Equal(("__KB__", "KB"), Assert.Single(targets));

            var wildcardTargets = Program.EnumerateMutationRecoveryTargets(
                "genexus_io",
                new JObject
                {
                    ["action"] = "import_text_to_kb",
                    ["name"] = "*"
                }).ToList();
            Assert.Equal(("__KB__", "KB"), Assert.Single(wildcardTargets));
        }

        [Fact]
        public void ManifestImportTimeoutRegistersKbFenceWithoutPartReadClaim()
        {
            var registry = new MutationRecoveryRegistry();
            var args = new JObject
            {
                ["action"] = "import_text_to_kb",
                ["inputPath"] = "C:/manifests/import.json"
            };
            var payload = new JObject();

            Assert.True(Program.RegisterTimeoutRecoveryFence(
                registry, "manifest-kb", "genexus_io", args, operationId: null, timeoutPayload: payload));
            Assert.True(payload["reReadRequired"]?.Value<bool>());
            Assert.Equal("kb", payload["recoveryScope"]?.ToString());
            var wireResult = Program.BuildToolResultContent(
                payload, isError: true, toolName: "genexus_io", toolArgs: args);
            var wirePayload = JObject.Parse(wireResult["content"]![0]!["text"]!.Value<string>()!);
            Assert.True(wirePayload["reReadRequired"]?.Value<bool>());
            Assert.Equal("kb", wirePayload["recoveryScope"]?.ToString());
            Assert.True(registry.TryGet(
                "manifest-kb", MutationRecoveryRegistry.KbRecoveryTarget,
                MutationRecoveryRegistry.KbRecoveryPart, out var requirement));

            var blocked = MutationRecoveryRegistry.BuildBlockedEnvelope(requirement);
            Assert.Equal("kb", blocked["recoveryScope"]?.ToString());
            Assert.Empty((JArray)blocked["affectedParts"]!);
            Assert.Empty((JArray)blocked["error"]?["nextSteps"]!);
            Assert.Empty(registry.FindForRead(
                "manifest-kb", new JObject { ["name"] = "ImportedProcedure" }, "Source"));
        }

        [Fact]
        public void KbManifestFenceRequiresVerifiedOperationEvidence()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(
                "manifest-kb", MutationRecoveryRegistry.KbRecoveryTarget,
                MutationRecoveryRegistry.KbRecoveryPart, "manifest-op");
            Assert.True(registry.TryGet(
                "manifest-kb", MutationRecoveryRegistry.KbRecoveryTarget,
                MutationRecoveryRegistry.KbRecoveryPart, out var requirement));

            Assert.Empty(registry.FindForRead(
                "manifest-kb", new JObject { ["name"] = "ImportedProcedure" }, "Source"));
            Assert.False(registry.ConfirmRead(
                "manifest-kb", requirement.Target, requirement.Part, requirement));
            Assert.True(registry.ConfirmVerifiedOperationRead(requirement));
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void FullObjectReadWithVersionedNestedResultIsCompleteRecoveryEvidence()
        {
            var payload = new JObject
            {
                ["code"] = "FullObjectRead",
                ["result"] = new JObject
                {
                    ["versionToken"] = "v-complete",
                    ["offset"] = 0
                }
            };

            Assert.True(Program.IsFullObjectMutationRecoveryRead(payload));
            Assert.True(Program.IsCompleteMutationRecoveryRead(payload));

            ((JObject)payload["result"]!)["truncated"] = true;
            Assert.False(Program.IsFullObjectMutationRecoveryRead(payload));
            Assert.False(Program.IsCompleteMutationRecoveryRead(payload));
        }

        [Fact]
        public void RecoveryHintRequestsCompleteAffectedPartRead()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead("kb", "Panel", "Variables", "op-1");
            Assert.True(registry.TryGet("kb", "Panel", "Variables", out var requirement));
            var envelope = MutationRecoveryRegistry.BuildBlockedEnvelope(requirement);
            var next = envelope["error"]?["nextSteps"]?[0]?["args"];
            Assert.Equal("Panel", next?["name"]?.ToString());
            Assert.Equal("Variables", next?["part"]?.ToString());
            Assert.Equal(0, next?["limit"]?.Value<int>());
        }

        [Fact]
        public void OnlyTerminalPersistedAndRereadOperationCanReconcileFence()
        {
            var complete = new JObject
            {
                ["status"] = "Completed",
                ["workerPayload"] = new JObject
                {
                    ["persisted"] = true,
                    ["rereadConfirmed"] = true
                }
            };
            Assert.True(Program.IsTerminalPersistedReread(complete));

            ((JObject)complete["workerPayload"]!)["rereadConfirmed"] = false;
            Assert.False(Program.IsTerminalPersistedReread(complete));
            ((JObject)complete["workerPayload"]!)["rereadConfirmed"] = true;
            complete["status"] = "Running";
            Assert.False(Program.IsTerminalPersistedReread(complete));
        }
    }
}
