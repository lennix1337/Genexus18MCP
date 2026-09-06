using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class MutationRecoveryJournalTests
    {
        [Fact]
        public void RequirementSurvivesRegistryReopenAndConfirmationRemovesIt()
        {
            string path = Path.Combine(Path.GetTempPath(), "gx-recovery-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var first = new MutationRecoveryRegistry(path);
                first.RequireRead("kb", "Object", "Source", "op");
                Assert.True(File.Exists(path));

                var reopened = new MutationRecoveryRegistry(path);
                Assert.True(reopened.TryGet("kb", "Object", out var requirement));
                Assert.Equal("op", requirement.OperationId);
                Assert.True(reopened.ConfirmRead("kb", "Object", "Source"));

                var afterConfirm = new MutationRecoveryRegistry(path);
                Assert.False(afterConfirm.TryGet("kb", "Object", out _));
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
            }
        }

        [Fact]
        public void JournalIsVersionedAndKeepsIndependentParts()
        {
            string path = Path.Combine(Path.GetTempPath(), "gx-recovery-parts-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var registry = new MutationRecoveryRegistry(path);
                registry.RequireRead("kb", "Object", "Source", "source-op");
                registry.RequireRead("kb", "Object", "Rules", "rules-op");

                var root = JObject.Parse(File.ReadAllText(path));
                Assert.Equal("genexus-mutation-recovery/1", root["schemaVersion"]?.ToString());
                Assert.Equal(2, (root["entries"] as JArray)?.Count);

                var reopened = new MutationRecoveryRegistry(path);
                Assert.True(reopened.TryGet("kb", "Object", "Source", out var source));
                Assert.True(reopened.TryGet("kb", "Object", "Rules", out var rules));
                Assert.Equal("source-op", source.OperationId);
                Assert.Equal("rules-op", rules.OperationId);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void CorruptJournalFailsClosedInsteadOfDroppingTheFence()
        {
            string path = Path.Combine(Path.GetTempPath(), "gx-recovery-corrupt-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"schemaVersion\":\"genexus-mutation-recovery/1\",\"entries\":[");
                var registry = new MutationRecoveryRegistry(path);

                Assert.False(registry.IsHealthy);
                Assert.Equal("MutationRecoveryJournalUnavailable", MutationRecoveryRegistry
                    .BuildJournalBlockedEnvelope(registry.JournalError)["code"]?.ToString());
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }
}
