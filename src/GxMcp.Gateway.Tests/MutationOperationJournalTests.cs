using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class MutationOperationJournalTests
    {
        [Fact]
        public void RestartedStartedMutationBecomesUnknownWithoutPayloadPersistence()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            try
            {
                var first = new MutationOperationJournal(path);
                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    first.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash"));

                var restarted = new MutationOperationJournal(path);
                Assert.Equal(MutationOperationJournal.BeginResult.UnknownAfterRestart,
                    restarted.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash"));
                Assert.Equal(MutationOperationJournal.BeginResult.Conflict,
                    restarted.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "different-hash"));

                string persisted = File.ReadAllText(path);
                Assert.DoesNotContain("old source", persisted, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("C:\\KB\\Orders", persisted, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task CompletedMutationIsNeverExecutedAgainAfterMemoryEviction()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            int executions = 0;
            try
            {
                var first = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), path);
                await first.GetOrCompute("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash", () =>
                {
                    executions++;
                    return Task.FromResult(new Newtonsoft.Json.Linq.JObject { ["isError"] = false });
                });

                var restarted = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), path);
                await Assert.ThrowsAsync<UsageException>(() => restarted.GetOrCompute(
                    "C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash", () =>
                    {
                        executions++;
                        return Task.FromResult(new Newtonsoft.Json.Linq.JObject { ["isError"] = false });
                    }));
                Assert.Equal(1, executions);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void CorruptJournalRefusesAKeyedMutationInsteadOfStartingFresh()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-corrupt-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(path, "{\"schemaVersion\":\"genexus-mutation-operations/1\",\"entries\":[");
                var journal = new MutationOperationJournal(path);

                Assert.False(journal.IsHealthy);
                Assert.Equal(MutationOperationJournal.BeginResult.JournalUnavailable,
                    journal.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash"));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task IdempotencyCacheDoesNotExecuteFactoryWhenJournalIsUnavailable()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-cache-corrupt-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            int executions = 0;
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(path, "{\"schemaVersion\":\"genexus-mutation-operations/1\",\"entries\":[");
                var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), path);

                var error = await Assert.ThrowsAsync<UsageException>(() => cache.GetOrCompute(
                    "C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash", () =>
                    {
                        executions++;
                        return Task.FromResult(new JObject { ["isError"] = false });
                    }));

                Assert.Equal("operation_journal_unavailable", error.Code);
                Assert.Equal(0, executions);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void OperationJournalUsesVersionedEnvelopeWithoutPayloadOrKbPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-schema-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            try
            {
                var journal = new MutationOperationJournal(path);
                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    journal.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash"));

                JObject document = JObject.Parse(File.ReadAllText(path));
                Assert.Equal("genexus-mutation-operations/1", document["schemaVersion"]?.ToString());
                Assert.NotNull(document["entries"]);
                string persisted = document.ToString();
                Assert.DoesNotContain("C:\\KB\\Orders", persisted, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void InspectAndReconcileCloseUnknownFenceOnlyWithExplicitVerification()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-reconcile-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            try
            {
                var journal = new MutationOperationJournal(path);
                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    journal.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash"));

                var inspection = journal.Inspect("C:\\KB\\Orders", "genexus_edit", "op-key");
                Assert.True(inspection["journalHealthy"]?.ToObject<bool>());
                Assert.True(inspection["known"]?.ToObject<bool>());
                Assert.Equal("started", inspection["status"]?.ToString());
                Assert.True(inspection["recoveryRequired"]?.ToObject<bool>());

                var rejected = journal.Reconcile("C:\\KB\\Orders", "genexus_edit", "op-key", "");
                Assert.Equal("Rejected", rejected["status"]?.ToString());

                const string verification = "genexus_read confirmed version sha256:abc";
                var reconciled = journal.Reconcile("C:\\KB\\Orders", "genexus_edit", "op-key", verification);
                Assert.Equal("reconciled", reconciled["status"]?.ToString());
                Assert.False(reconciled["recoveryRequired"]?.ToObject<bool>());
                Assert.Equal(MutationOperationJournal.BeginResult.Completed,
                    journal.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash"));

                string persisted = File.ReadAllText(path);
                Assert.DoesNotContain(verification, persisted, StringComparison.Ordinal);
                Assert.Contains("VerificationHash", persisted, StringComparison.Ordinal);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void InspectUnknownOperationDoesNotCreateJournalEntry()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-inspect-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            try
            {
                var journal = new MutationOperationJournal(path);
                var inspection = journal.Inspect("C:\\KB\\Orders", "genexus_edit", "missing-key");

                Assert.Equal("not_found", inspection["status"]?.ToString());
                Assert.False(inspection["known"]?.ToObject<bool>());
                Assert.False(File.Exists(path));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ReconcileRejectsChangedTargetsOrRevisionUntilEvidenceMatches()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-evidence-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            try
            {
                var journal = new MutationOperationJournal(path);
                var original = MutationOperationEvidence.FromArguments(new JObject
                {
                    ["name"] = "Customer",
                    ["versionToken"] = "v1"
                });
                Assert.NotNull(original);
                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    journal.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash", original));

                var inspect = journal.Inspect("C:\\KB\\Orders", "genexus_edit", "op-key");
                Assert.True(inspect["evidenceBound"]?.ToObject<bool>());
                Assert.NotNull(inspect["targetIdsHash"]);
                Assert.NotNull(inspect["revisionHash"]);

                var changed = journal.Reconcile(
                    "C:\\KB\\Orders",
                    "genexus_edit",
                    "op-key",
                    "independent read",
                    MutationOperationEvidence.FromObserved(new JArray("Order"), "v1"));
                Assert.Equal("Rejected", changed["status"]?.ToString());
                Assert.Equal("operation_evidence_mismatch", changed["code"]?.ToString());

                var matching = journal.Reconcile(
                    "C:\\KB\\Orders",
                    "genexus_edit",
                    "op-key",
                    "independent read",
                    MutationOperationEvidence.FromObserved(new JArray("Customer"), "v1"));
                Assert.Equal("reconciled", matching["status"]?.ToString());
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ReconcileBindsModelAndEnvironmentIdentityWithoutPersistingRawValues()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-journal-context-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "operations.json");
            try
            {
                var journal = new MutationOperationJournal(path);
                var original = MutationOperationEvidence.FromArguments(new JObject
                {
                    ["name"] = "Customer",
                    ["versionToken"] = "v1",
                    ["modelId"] = "model-a",
                    ["environmentId"] = "env-a"
                });
                Assert.NotNull(original);
                Assert.Equal(MutationOperationJournal.BeginResult.Started,
                    journal.Begin("C:\\KB\\Orders", "genexus_edit", "op-key", "payload-hash", original));

                var changed = journal.Reconcile(
                    "C:\\KB\\Orders",
                    "genexus_edit",
                    "op-key",
                    "independent read",
                    MutationOperationEvidence.FromObserved(
                        new JArray("Customer"), "v1", "model-b", "env-a"));
                Assert.Equal("Rejected", changed["status"]?.ToString());
                Assert.Equal("operation_evidence_mismatch", changed["code"]?.ToString());

                var matching = journal.Reconcile(
                    "C:\\KB\\Orders",
                    "genexus_edit",
                    "op-key",
                    "independent read",
                    MutationOperationEvidence.FromObserved(
                        new JArray("Customer"), "v1", "model-a", "env-a"));
                Assert.Equal("reconciled", matching["status"]?.ToString());
                Assert.NotNull(matching["modelHash"]);
                Assert.NotNull(matching["environmentHash"]);

                string persisted = File.ReadAllText(path);
                Assert.DoesNotContain("model-a", persisted, StringComparison.Ordinal);
                Assert.DoesNotContain("env-a", persisted, StringComparison.Ordinal);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
