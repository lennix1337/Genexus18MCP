using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // What the default (terse) error projection delivers for a protected Events save that committed the Events
    // source but could not clear another object that changed while it ran.
    public class EventsSaveConcurrentEditTerseTests
    {
        private static JObject CommittedButAnotherObjectChanged() => JObject.Parse(@"{
            ""code"":""ObjectSaveIncomplete"",
            ""message"":""The Events source was committed and read back, and the other parts of the object are intact, but 1 other object(s) changed while the save ran and could not be placed outside its window (see unexpectedChangedObjectDetails)."",
            ""reasonCode"":""OtherObjectsChangedDuringSave"",
            ""savePhase"":""post-commit-verification"",
            ""objectSaved"":true,""partPersisted"":true,""persisted"":true,""verified"":false,
            ""writeVerifiedForTarget"":true,""retrySafe"":false,
            ""otherPartsIntact"":true,""otherObjectMetadataIntact"":false,
            ""unexpectedChangedObjects"":[""00000000-0000-0000-0000-0000000000b2""],
            ""unexpectedChangedObjectDetails"":[{""guid"":""00000000-0000-0000-0000-0000000000b2"",""name"":""Procedure01"",""type"":""Procedure"",""change"":""modified"",""verdict"":""blocking"",""reason"":""InsideSaveWindow"",""versionBefore"":3,""versionAfter"":4}],
            ""unexpectedChangedObjectDetailsTruncated"":true,
            ""concurrentChanges"":[{""guid"":""00000000-0000-0000-0000-0000000000c3"",""name"":""Procedure02"",""verdict"":""concurrent"",""reason"":""AfterSaveWindow""}],
            ""concurrentChangeCount"":1,""concurrentChangesTruncated"":false,
            ""saveWindow"":{""calibrated"":true,""stampClockOffsetSeconds"":0.0,""seconds"":66.0}
        }");

        // The save was refused before the commit and its rollback undid the other object's change: the final fields are
        // clean, and only the pre-commit ones still name the object that caused the refusal.
        private static JObject RefusedBeforeTheCommitAndRolledBack() => JObject.Parse(@"{
            ""code"":""ObjectSaveIncomplete"",
            ""message"":""Isolated Events save failed during pre-commit-verification: The pre-commit SDK object state did not match the requested isolated change: 1 other object(s) in the Knowledge Base changed (see preCommitUnexpectedChangedObjectDetails)."",
            ""savePhase"":""pre-commit-verification"",
            ""objectSaved"":false,""persisted"":false,""verified"":false,
            ""transactionRolledBack"":true,""rollbackVerified"":true,
            ""otherPartsIntact"":true,""otherObjectMetadataIntact"":true,
            ""unexpectedChangedObjects"":[],
            ""preCommitUnexpectedChangedObjects"":[""00000000-0000-0000-0000-0000000000b2""],
            ""preCommitUnexpectedChangedObjectDetails"":[{""guid"":""00000000-0000-0000-0000-0000000000b2"",""name"":""Procedure01"",""type"":""Procedure"",""change"":""modified"",""verdict"":""blocking"",""reason"":""InsideSaveWindow"",""versionBefore"":3,""versionAfter"":4}],
            ""preCommitUnexpectedChangedObjectDetailsTruncated"":true,
            ""saveWindow"":{""calibrated"":true,""stampClockOffsetSeconds"":0.0,""seconds"":66.0}
        }");

        [Fact]
        public void TrimErrorEnvelope_KeepsWhichObjectsChanged_TheSaveWindowAndTheVerdictOnTheTarget()
        {
            var input = CommittedButAnotherObjectChanged();

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (string key in new[]
            {
                "reasonCode", "savePhase", "objectSaved", "partPersisted", "persisted", "verified", "writeVerifiedForTarget", "retrySafe",
                "otherPartsIntact", "otherObjectMetadataIntact", "unexpectedChangedObjects", "unexpectedChangedObjectDetails",
                "unexpectedChangedObjectDetailsTruncated", "concurrentChanges", "concurrentChangeCount", "concurrentChangesTruncated",
                "saveWindow"
            })
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
            Assert.Equal("ObjectSaveIncomplete", (string)trimmed["code"]!);
            Assert.StartsWith("The Events source was committed and read back", (string)trimmed["message"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsTheObjectThatCausedARefusalBeforeTheCommit_EvenWhenTheRollbackUndidIt()
        {
            var input = RefusedBeforeTheCommitAndRolledBack();

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (string key in new[]
            {
                "savePhase", "transactionRolledBack", "rollbackVerified", "otherObjectMetadataIntact", "unexpectedChangedObjects",
                "preCommitUnexpectedChangedObjects", "preCommitUnexpectedChangedObjectDetails",
                "preCommitUnexpectedChangedObjectDetailsTruncated", "saveWindow"
            })
                Assert.True(JToken.DeepEquals(input[key], trimmed[key]), key);
            Assert.Empty((JArray)trimmed["unexpectedChangedObjects"]!);
            Assert.Equal("00000000-0000-0000-0000-0000000000b2", (string)trimmed["preCommitUnexpectedChangedObjectDetails"]![0]!["guid"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_VerboseMode_PassesTheEnvelopeThrough()
        {
            var input = CommittedButAnotherObjectChanged();

            var verbose = McpRouter.TrimErrorEnvelope(input, verbose: true);

            Assert.True(JToken.DeepEquals(input, verbose));
        }

        [Fact]
        public void TrimErrorEnvelope_DoesNotInventTheFieldsWhenTheWorkerSentNone()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(
                JObject.Parse("{code:'ObjectSaveIncomplete',message:'verification failed',otherObjectMetadataIntact:true}"), verbose: false);

            foreach (string key in new[]
            {
                "unexpectedChangedObjects", "preCommitUnexpectedChangedObjects", "preCommitUnexpectedChangedObjectDetails",
                "concurrentChanges", "saveWindow", "writeVerifiedForTarget"
            })
                Assert.False(trimmed.ContainsKey(key), key);
        }
    }
}
