using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The terse error projection keeps the first message line and an allowlist of fields. The refusals and rollback
    // receipts of genexus_structure get_item_property / set_item_property carry their cause in fields, so each must
    // survive it: without them the caller sees a code and cannot tell what to send next or what was restored.
    public class SdtItemPropertyTerseErrorTests
    {
        private static JObject Trim(string json) => McpRouter.TrimErrorEnvelope(JObject.Parse(json), verbose: false);

        private static void AssertEveryFieldSurvives(string input)
        {
            var trimmed = Trim(input);
            foreach (var property in JObject.Parse(input).Properties())
                Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
        }

        [Fact]
        public void ARollbackReceiptKeepsTheVersionsTheDetailAndTheRollbackVerdict()
        {
            AssertEveryFieldSurvives(@"{""code"":""SdtItemPropertyNotPersisted"",
                ""message"":""The persisted SDT member did not match the request; the prior value was restored and verified."",
                ""hint"":""Read get_item_property again through a fresh SDK instance before another write."",
                ""persisted"":false,""persistedVerified"":false,""rollback"":""verified"",
                ""beforeVersion"":""639295082760000000:abc"",""versionToken"":""639295084110000000:abc"",
                ""mismatchDetail"":""The persisted value is 'idJsonJsonNull' but 'idJsonEmpty' was requested."",
                ""implicitLifecycleActions"":[]}");
        }

        [Fact]
        public void ARefusedValueKeepsTheValuesTheEnumerationAccepts()
        {
            AssertEveryFieldSurvives(@"{""code"":""InvalidPropertyValue"",
                ""message"":""'Maybe' is not an allowed value. Allowed: Empty (idJsonEmpty), NoProperty (idJsonNoProperty)."",
                ""persisted"":false,
                ""allowedValues"":[{""value"":""Empty"",""label"":""Empty"",""rawValue"":""idJsonEmpty""},{""value"":""NoProperty"",""label"":""No Property"",""rawValue"":""idJsonNoProperty""}]}");
        }

        [Fact]
        public void AnUnknownPropertyKeepsTheCloseSpellings()
        {
            AssertEveryFieldSurvives(@"{""code"":""PropertyNotFound"",
                ""message"":""Property 'JsonNullSerialisation' is not exposed by the item 'Tags'. Did you mean JsonNullSerialization?"",
                ""persisted"":false,""suggestions"":[""JsonNullSerialization"",""XmlNullSerialization""]}");
        }

        [Fact]
        public void AnUnconfirmedRollbackKeepsWhatTheKbHoldsAgainstWhatItHeldBefore()
        {
            AssertEveryFieldSurvives(@"{""code"":""SdtItemPropertyUpdateFailed"",""message"":""the commit failed"",
                ""persisted"":false,""rollback"":""unverified"",
                ""storedChanges"":[{""path"":""other"",""property"":""Required"",""from"":null,""to"":""True""}],
                ""storedChangesTotal"":25,""otherPartsChanged"":true}");
        }

        [Fact]
        public void AStaleTokenKeepsTheTokenThatIsCurrent()
        {
            AssertEveryFieldSurvives(@"{""code"":""VersionConflict"",""message"":""The SDT changed after expectedVersion was captured."",
                ""persisted"":false,""expectedVersion"":""639000000000000000:abc"",""currentVersion"":""639295082760000000:abc""}");
        }
    }
}
