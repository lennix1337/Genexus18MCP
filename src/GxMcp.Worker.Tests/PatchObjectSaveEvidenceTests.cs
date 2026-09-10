using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatchObjectSaveEvidenceTests
    {
        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void IsolationGuard_DoesNotBlockPreviewOrPartOnly(bool required, bool dryRun)
        {
            Assert.Null(PatchPersistenceReceipt.ObjectSaveIsolationGuard("SyntheticPanel", required, dryRun));
        }

        [Fact]
        public void IsolationGuard_RejectsRequiredObjectSaveBeforeMutation()
        {
            string result = PatchPersistenceReceipt.ObjectSaveIsolationGuard("SyntheticPanel", true, false);
            Assert.Contains("ObjectSaveIsolationUnverified", result);
            var response = JObject.Parse(result);
            Assert.False(response.SelectToken("$..objectSaved")?.Value<bool>());
            Assert.False(response.SelectToken("$..writeAttempted")?.Value<bool>());
            Assert.False(response.SelectToken("$..partPersisted")?.Value<bool>());
        }

        [Theory]
        [InlineData(false, true, true, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, true, false, true)]
        [InlineData(true, true, true, false)]
        [InlineData(true, true, true, null)]
        public void RequiredSave_MissingEvidenceIsIncomplete(bool objectSaved, bool partPersisted, bool metadataUpdated, bool? siblingsIntact)
        {
            var payload = new JObject
            {
                ["requireObjectSave"] = true, ["_internalStatus"] = "Success",
                ["code"] = "AppliedPartOnly", ["objectSaved"] = objectSaved,
                ["partPersisted"] = partPersisted, ["metadataUpdated"] = metadataUpdated,
                ["otherPartsIntact"] = siblingsIntact
            };
            PatchPersistenceReceipt.RequireCompleteObjectSave(payload);
            Assert.Equal("Error", payload["_internalStatus"]?.ToString());
            Assert.Equal("ObjectSaveIncomplete", payload["code"]?.ToString());
            Assert.Equal(partPersisted, payload["partPersisted"]?.Value<bool>());
            Assert.False(payload["retrySafe"]?.Value<bool>());
            Assert.Null(payload["rolledBack"]);
        }

        [Fact]
        public void RequiredSave_CompleteEvidenceKeepsSuccess()
        {
            var payload = JObject.Parse("{requireObjectSave:true,code:'Applied',objectSaved:true,partPersisted:true,metadataUpdated:true,otherPartsIntact:true}");
            PatchPersistenceReceipt.RequireCompleteObjectSave(payload);
            Assert.Equal("Applied", payload["code"]?.ToString());
            Assert.Null(payload["retrySafe"]);
        }

        [Theory]
        [InlineData("42", "41")]
        [InlineData("42", "unavailable")]
        public void MetadataChanged_DoesNotTreatRegressionOrUnknownAsAdvance(string before, string after)
        {
            Assert.False(PatchPersistenceReceipt.MetadataChanged(before, after, null, null, metadataStampPersisted: true));
        }


        [Fact]
        public void RequiredObjectSave_WithoutBaseVersion_IsRejectedBeforeWrite()
        {
            var response = JObject.Parse(new PatchService(null, null).ApplyPatch(
                target: "SamplePanel",
                partName: "Events",
                operation: "Replace",
                content: "// updated",
                context: "// original",
                requireObjectSave: true));

            Assert.Equal("BaseVersionRequired", response["error"]?["code"]?.ToString());
            Assert.False(response["partPersisted"]!.Value<bool>());
            Assert.False(response["objectSaved"]!.Value<bool>());
            Assert.False(response["metadataUpdated"]!.Value<bool>());
        }

        [Fact]
        public void MetadataChanged_RevisionAdvance_IsSufficient()
        {
            Assert.True(PatchPersistenceReceipt.MetadataChanged(
                "41", "42", "2026-09-09T10:00:00Z", "2026-09-09T10:00:00Z",
                metadataStampPersisted: true));
        }

        [Fact]
        public void MetadataChanged_LastUpdateAdvance_IsSufficient()
        {
            Assert.True(PatchPersistenceReceipt.MetadataChanged(
                "42", "42", "2026-09-09T10:00:00Z", "2026-09-09T10:00:01Z",
                metadataStampPersisted: true));
        }

        [Fact]
        public void MetadataChanged_LastUpdateAdvance_WithoutDurableStamp_IsInsufficient()
        {
            Assert.False(PatchPersistenceReceipt.MetadataChanged(
                "42", "42", "2026-09-09T10:00:00Z", "2026-09-09T10:00:01Z",
                metadataStampPersisted: false));
        }

        [Fact]
        public void MetadataChanged_RevisionAdvance_WithoutDurableStamp_IsInsufficient()
        {
            Assert.False(PatchPersistenceReceipt.MetadataChanged(
                "42", "43", "2026-09-09T10:00:00Z", "2026-09-09T10:00:00Z",
                metadataStampPersisted: false));
        }

        [Fact]
        public void MetadataChanged_UnchangedEvidence_IsFalse()
        {
            Assert.False(PatchPersistenceReceipt.MetadataChanged(
                "42", "42", "2026-09-09T10:00:00Z", "2026-09-09T10:00:00Z"));
        }

        [Fact]
        public void AttachObjectSaveEvidence_ReportsPartialStateWithoutHidingPersistedPart()
        {
            var payload = new JObject();

            bool updated = PatchPersistenceReceipt.AttachObjectSaveEvidence(
                payload,
                partPersisted: true,
                objectSaved: false,
                revisionBefore: "42",
                revisionAfter: "42",
                lastUpdateBefore: "2026-09-09T10:00:00Z",
                lastUpdateAfter: "2026-09-09T10:00:00Z",
                otherPartsIntact: null);

            Assert.False(updated);
            Assert.True(payload["partPersisted"]!.Value<bool>());
            Assert.False(payload["objectSaved"]!.Value<bool>());
            Assert.False(payload["metadataUpdated"]!.Value<bool>());
            Assert.Equal("42", payload["revisionBefore"]!.ToString());
            Assert.Equal("42", payload["revisionAfter"]!.ToString());
        }

        [Fact]
        public void AttachObjectSaveEvidence_ExposesUnexpectedSiblingChanges()
        {
            var payload = new JObject();
            var changed = new JArray("Rules", "WebForm");

            PatchPersistenceReceipt.AttachObjectSaveEvidence(
                payload,
                partPersisted: true,
                objectSaved: true,
                revisionBefore: "42",
                revisionAfter: "43",
                lastUpdateBefore: null,
                lastUpdateAfter: null,
                otherPartsIntact: false,
                metadataStampPersisted: true,
                unexpectedChangedParts: changed);

            Assert.False(payload["otherPartsIntact"]!.Value<bool>());
            Assert.Equal(changed, payload["unexpectedChangedParts"]);
        }

        [Fact]
        public void AttachObjectSaveEvidence_ReportsMetadataStampEvidence()
        {
            var payload = new JObject();

            PatchPersistenceReceipt.AttachObjectSaveEvidence(
                payload,
                partPersisted: true,
                objectSaved: true,
                revisionBefore: "42",
                revisionAfter: "42",
                lastUpdateBefore: "2026-09-09T10:00:00Z",
                lastUpdateAfter: "2026-09-09T10:00:01Z",
                otherPartsIntact: true,
                metadataStampPersisted: false);

            Assert.False(payload["metadataStampPersisted"]!.Value<bool>());
            Assert.False(payload["metadataUpdated"]!.Value<bool>());
        }
    }
}
