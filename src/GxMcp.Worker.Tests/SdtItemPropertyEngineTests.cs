using System;
using System.Linq;
using GxMcp.Worker.Services.Structure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The get_item_property / set_item_property protocol over a store double: what is read, what a preview shows, which
    /// requests are refused before anything is written, and what happens when the write goes wrong half way.
    /// </summary>
    public class SdtItemPropertyEngineTests
    {
        private const string Sdt = "SampleResponse";

        private static JObject Parse(string envelope) => JObject.Parse(envelope);
        private static string Status(JObject o) => o["status"]?.ToString();
        private static string Code(JObject o) => Status(o) == "ok" ? o["code"]?.ToString() : o["error"]?["code"]?.ToString();
        private static JObject Result(JObject o) => (JObject)o["result"];

        private static JObject Set(FakeSdtStore store, string property, string value, bool dryRun = false, string version = "100:root")
            => Parse(SdtItemPropertyEngine.Set(store, Sdt, property, value, dryRun, version));

        private static JObject Get(FakeSdtStore store, string property = null)
            => Parse(SdtItemPropertyEngine.Get(store, Sdt, property));

        // ------------------------------------------------------------------ read

        [Fact]
        public void ReadReturnsTheEffectiveValueTheStoredIdTheDefaultAndTheVersionToken()
        {
            var store = new FakeSdtStore();

            var json = Get(store, "JsonNullSerialization");

            Assert.Equal("ok", Status(json));
            Assert.Equal("SdtItemProperty", Code(json));
            var result = Result(json);
            Assert.Equal("debug", result["itemPath"][0].ToString());
            Assert.Equal("item", result["kind"].ToString());
            Assert.Equal("JsonNullSerialization", result["property"].ToString());
            Assert.Equal("idJsonInclude", result["nativeName"].ToString());
            Assert.Equal("Json Null Serialization", result["displayName"].ToString());
            Assert.Equal("NoProperty", result["value"].ToString());
            Assert.Equal("idJsonNoProperty", result["rawValue"].ToString());
            Assert.True(result["isDefault"].Value<bool>());
            Assert.Equal("NoProperty", result["defaultValue"].ToString());
            Assert.True(result["writable"].Value<bool>());
            Assert.Equal("enum", result["type"].ToString());
            Assert.Equal(new[] { "Empty", "JsonNull", "NoProperty" }, result["allowedValues"].Select(v => v["value"].ToString()).ToArray());
            Assert.Equal("100:root", result["versionToken"].ToString());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply"));
        }

        [Fact]
        public void AnExplicitValueIsReadAsNotDefault()
        {
            var store = new FakeSdtStore();
            store.Target[FakeSdtStore.Native] = "idJsonEmpty";

            var result = Result(Get(store, "idJsonInclude"));

            Assert.Equal("Empty", result["value"].ToString());
            Assert.Equal("idJsonEmpty", result["rawValue"].ToString());
            Assert.False(result["isDefault"].Value<bool>());
            Assert.Equal("NoProperty", result["defaultValue"].ToString());
        }

        [Fact]
        public void ABooleanPropertyIsReadAsAJsonBoolean()
        {
            var store = new FakeSdtStore();
            store.Target["Required"] = "True";

            var result = Result(Get(store, "Required"));

            Assert.Equal(JTokenType.Boolean, result["value"].Type);
            Assert.True(result["value"].Value<bool>());
            Assert.Equal("boolean", result["type"].ToString());
        }

        [Fact]
        public void WithoutAPropertyEveryBrowsableOneIsListedWithItsWritability()
        {
            var store = new FakeSdtStore();

            var result = Result(Get(store));

            var listed = result["properties"].ToDictionary(p => p["nativeName"].ToString());
            // a property the IDE hides is not listed; reading it by name still works (see the next test)
            Assert.Equal(store.Catalog.Count(p => p.Browsable), listed.Count);
            Assert.False(listed.ContainsKey("Hidden"));
            Assert.True(listed["idJsonInclude"]["writable"].Value<bool>());
            Assert.False(listed["Name"]["writable"].Value<bool>());
            Assert.Equal("StructuralPropertyNotSupported", listed["Name"]["notWritableReason"].ToString());
            Assert.Equal("PropertyReadOnly", listed["Locked"]["notWritableReason"].ToString());
            Assert.Equal("PropertyNotApplicable", listed["CollectionOnly"]["notWritableReason"].ToString());
            Assert.Equal("UnsupportedPropertyType", listed["Opaque"]["notWritableReason"].ToString());
            Assert.Equal("UnsupportedPropertyType", listed["NoType"]["notWritableReason"].ToString());
            Assert.Equal("PropertyCatalogIncomplete", listed["Mystery"]["notWritableReason"].ToString());
            Assert.Equal("100:root", result["versionToken"].ToString());
        }

        [Fact]
        public void AHiddenPropertyCanBeReadByNameButIsNotWritable()
        {
            var json = Get(new FakeSdtStore(), "Hidden");

            Assert.Equal("ok", Status(json));
            Assert.False(Result(json)["writable"].Value<bool>());
            Assert.Equal("PropertyHidden", Result(json)["notWritableReason"].ToString());
        }

        [Fact]
        public void APropertyThatDoesNotExistIsRefusedWithSuggestions()
        {
            var json = Get(new FakeSdtStore(), "JsonNullSerialisation");

            Assert.Equal("error", Status(json));
            Assert.Equal("PropertyNotFound", Code(json));
            Assert.Contains("JsonNullSerialization", json["suggestions"].Select(s => s.ToString()));
        }

        [Fact]
        public void ARefusedLookupOfTheMemberIsReturnedAsIs()
        {
            var store = new FakeSdtStore { ReadsBeforeFailing = 0 };

            var json = Get(store, "JsonNullSerialization");

            Assert.Equal("error", Status(json));
            Assert.Equal("FreshReadUnavailable", Code(json));
            Assert.False(json["persisted"].Value<bool>());
        }

        // --------------------------------------------------------------- preview

        [Fact]
        public void APreviewShowsExactlyTheRequestedChangeAndWritesNothing()
        {
            var store = new FakeSdtStore();
            var neighbourBefore = store.Neighbour.ToDictionary(p => p.Key, p => p.Value);

            var json = Set(store, "JsonNullSerialization", "Empty", dryRun: true);

            Assert.Equal("ok", Status(json));
            Assert.Equal("SdtItemPropertyPreview", Code(json));
            var result = Result(json);
            Assert.True(result["dryRun"].Value<bool>());
            Assert.False(result["persisted"].Value<bool>());
            Assert.True(result["willChange"].Value<bool>());
            var changes = (JArray)result["changes"];
            Assert.Single(changes);
            Assert.Equal("debug", changes[0]["path"].ToString());
            Assert.Equal("idJsonInclude", changes[0]["nativeName"].ToString());
            Assert.Equal("idJsonNoProperty", changes[0]["from"]["rawValue"].ToString());
            Assert.Equal("idJsonEmpty", changes[0]["to"]["rawValue"].ToString());
            Assert.Equal("Empty", changes[0]["to"]["value"].ToString());
            Assert.Equal("100:root", result["versionToken"].ToString());
            Assert.Empty((JArray)result["implicitLifecycleActions"]);
            // nothing was applied, nothing was restored, the stored state is untouched
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply") || entry.StartsWith("restore") || entry == "note");
            Assert.Empty(store.Target);
            Assert.Equal(neighbourBefore, store.Neighbour);
            Assert.Equal("100:root", store.Version);
        }

        [Fact]
        public void APreviewDoesNotNeedAVersionButRefusesAStaleOne()
        {
            var store = new FakeSdtStore();

            var without = Parse(SdtItemPropertyEngine.Set(store, Sdt, "JsonNullSerialization", "Empty", true, null));
            Assert.Equal("SdtItemPropertyPreview", Code(without));

            var stale = Set(store, "JsonNullSerialization", "Empty", dryRun: true, version: "99:root");
            Assert.Equal("error", Status(stale));
            Assert.Equal("VersionConflict", Code(stale));
            Assert.Equal("100:root", stale["currentVersion"].ToString());
        }

        [Fact]
        public void APreviewOfTheValueAlreadyInEffectListsNoChange()
        {
            var store = new FakeSdtStore();

            var result = Result(Set(store, "JsonNullSerialization", "NoProperty", dryRun: true));

            Assert.False(result["willChange"].Value<bool>());
            Assert.Empty((JArray)result["changes"]);
        }

        // ----------------------------------------------------------------- write

        [Fact]
        public void AWriteChangesOnlyTheRequestedPropertyAndReportsTheVerifiedResult()
        {
            var store = new FakeSdtStore();
            var neighbourBefore = store.Neighbour.ToDictionary(p => p.Key, p => p.Value);

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("ok", Status(json));
            Assert.Equal("SdtItemPropertyUpdated", Code(json));
            var result = Result(json);
            Assert.True(result["persisted"].Value<bool>());
            Assert.True(result["persistedVerified"].Value<bool>());
            Assert.Equal("idJsonNoProperty", result["before"]["rawValue"].ToString());
            Assert.True(result["before"]["isDefault"].Value<bool>());
            Assert.Equal("Empty", result["after"]["value"].ToString());
            Assert.Equal("idJsonEmpty", result["after"]["rawValue"].ToString());
            Assert.False(result["after"]["isDefault"].Value<bool>());
            var stored = (JArray)result["storedChanges"];
            Assert.Single(stored);
            Assert.Equal("debug", stored[0]["path"].ToString());
            Assert.Equal("idJsonInclude", stored[0]["property"].ToString());
            Assert.Equal(JTokenType.Null, stored[0]["from"].Type);
            Assert.Equal("idJsonEmpty", stored[0]["to"].ToString());
            Assert.True(result["otherMembersUnchanged"].Value<bool>());
            Assert.True(result["otherPartsUnchanged"].Value<bool>());
            Assert.Equal("100:root", result["beforeVersion"].ToString());
            Assert.Equal(store.Version, result["versionToken"].ToString());
            Assert.NotEqual("100:root", store.Version);
            Assert.Empty((JArray)result["implicitLifecycleActions"]);
            // the store holds exactly the new value and the neighbour is untouched
            Assert.Equal("idJsonEmpty", store.Target[FakeSdtStore.Native]);
            Assert.Equal(neighbourBefore, store.Neighbour);
            Assert.Equal(new[] { "read", "apply:idJsonEmpty", "read", "note" }, store.Log.ToArray());
        }

        [Fact]
        public void AWriteOfTheDefaultValueOverAnExplicitOneIsStoredAndCountsAsOneChange()
        {
            // GeneXus 18 U16 keeps the value explicitly stored even though it equals the default.
            var store = new FakeSdtStore();
            store.Target[FakeSdtStore.Native] = "idJsonEmpty";

            var json = Set(store, "JsonNullSerialization", "No Property");

            Assert.Equal("SdtItemPropertyUpdated", Code(json));
            var stored = (JArray)Result(json)["storedChanges"];
            Assert.Single(stored);
            Assert.Equal("idJsonEmpty", stored[0]["from"].ToString());
            Assert.Equal("idJsonNoProperty", stored[0]["to"].ToString());
            Assert.Equal("idJsonNoProperty", store.Target[FakeSdtStore.Native]);
            Assert.False(Result(json)["after"]["isDefault"].Value<bool>());
        }

        [Fact]
        public void AnSdkThatDropsAStoredValueEqualToTheDefaultIsAcceptedAsOneChangeToo()
        {
            var store = new FakeSdtStore { DropsDefaultValues = true };
            store.Target[FakeSdtStore.Native] = "idJsonEmpty";

            var json = Set(store, "JsonNullSerialization", "No Property");

            Assert.Equal("SdtItemPropertyUpdated", Code(json));
            var stored = (JArray)Result(json)["storedChanges"];
            Assert.Single(stored);
            Assert.Equal("idJsonEmpty", stored[0]["from"].ToString());
            Assert.Equal(JTokenType.Null, stored[0]["to"].Type);
            Assert.Empty(store.Target);
            Assert.True(Result(json)["after"]["isDefault"].Value<bool>());
        }

        [Fact]
        public void ABooleanAndATextPropertyAreWrittenWithTheirOwnTypes()
        {
            var store = new FakeSdtStore();

            var required = Result(Set(store, "Required", "true"));
            Assert.Equal(JTokenType.Boolean, required["after"]["value"].Type);
            Assert.Equal("True", store.Target["Required"]);

            var name = Result(Set(store, "Json Name", "debugInfo", version: store.Version));
            Assert.Equal("debugInfo", name["after"]["value"].ToString());
            Assert.Equal("debugInfo", store.Target["idJsonName"]);
        }

        [Fact]
        public void AWriteWithoutAVersionIsRefusedBeforeAnything()
        {
            var store = new FakeSdtStore();

            var json = Parse(SdtItemPropertyEngine.Set(store, Sdt, "JsonNullSerialization", "Empty", false, null));

            Assert.Equal("ExpectedVersionRequired", Code(json));
            Assert.Equal("100:root", json["currentVersion"].ToString());
            Assert.False(json["persisted"].Value<bool>());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply"));
        }

        [Fact]
        public void AStaleVersionIsRefusedBeforeAnythingIsApplied()
        {
            var store = new FakeSdtStore();

            var json = Set(store, "JsonNullSerialization", "Empty", version: "99:root");

            Assert.Equal("VersionConflict", Code(json));
            Assert.Equal("99:root", json["expectedVersion"].ToString());
            Assert.Equal("100:root", json["currentVersion"].ToString());
            Assert.False(json["persisted"].Value<bool>());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply"));
            Assert.Empty(store.Target);
        }

        [Fact]
        public void AVersionThatMovesInsideTheTransactionIsRefusedWithoutAnyRestore()
        {
            var store = new FakeSdtStore { ConflictUnderLock = true };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("VersionConflict", Code(json));
            Assert.Equal("999:root", json["currentVersion"].ToString());
            Assert.False(json["persisted"].Value<bool>());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("restore"));
            Assert.Empty(store.Target);
        }

        [Fact]
        public void AValueAlreadyInEffectIsReportedUnchangedAndNotSaved()
        {
            var store = new FakeSdtStore();
            store.Target[FakeSdtStore.Native] = "idJsonEmpty";

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyUnchanged", Code(json));
            var result = Result(json);
            Assert.True(result["unchanged"].Value<bool>());
            Assert.False(result["persisted"].Value<bool>());
            Assert.Equal("100:root", result["versionToken"].ToString());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply"));
            Assert.Equal("100:root", store.Version);
        }

        [Fact]
        public void TheDefaultValueIsUnchangedWhenNothingWasEverStored()
        {
            var store = new FakeSdtStore();

            var json = Set(store, "JsonNullSerialization", "NoProperty");

            Assert.Equal("SdtItemPropertyUnchanged", Code(json));
            Assert.Empty(store.Target);
        }

        // --------------------------------------------------------------- guards

        [Theory]
        [InlineData("Name", "StructuralPropertyNotSupported")]
        [InlineData("Locked", "PropertyReadOnly")]
        [InlineData("Collection Only", "PropertyNotApplicable")]
        [InlineData("Opaque", "UnsupportedPropertyType")]
        [InlineData("No Type", "UnsupportedPropertyType")]
        [InlineData("Hidden", "PropertyHidden")]
        [InlineData("Mystery", "PropertyCatalogIncomplete")]
        public void APropertyThatCannotBeWrittenIsRefusedBeforeAnything(string property, string expectedCode)
        {
            var store = new FakeSdtStore();

            var json = Set(store, property, "anything");

            Assert.Equal("error", Status(json));
            Assert.Equal(expectedCode, Code(json));
            Assert.False(json["persisted"].Value<bool>());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply"));
            Assert.Empty(store.Target);
        }

        [Fact]
        public void AValueOutsideTheEnumerationIsRefusedWithTheAllowedValues()
        {
            var store = new FakeSdtStore();

            var json = Set(store, "JsonNullSerialization", "Everything");

            Assert.Equal("InvalidPropertyValue", Code(json));
            Assert.Equal(3, ((JArray)json["allowedValues"]).Count);
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply"));
        }

        [Fact]
        public void ARequestWithoutPropertyOrValueIsRefused()
        {
            var store = new FakeSdtStore();

            Assert.Equal("PropertyRequired", Code(Set(store, null, "Empty")));
            Assert.Equal("PropertyRequired", Code(Set(store, "  ", "Empty")));
            Assert.Equal("ValueRequired", Code(Parse(SdtItemPropertyEngine.Set(store, Sdt, "JsonNullSerialization", null, false, "100:root"))));
            Assert.Equal("PropertyNotFound", Code(Set(store, "NoSuchProperty", "x")));
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply"));
        }

        [Fact]
        public void AMemberThatCannotBeResolvedIsReturnedAsAPersistedFalseRefusal()
        {
            var store = new FakeSdtStore { ReadsBeforeFailing = 0 };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("error", Status(json));
            Assert.False(json["persisted"].Value<bool>());
        }

        // --------------------------------------------------- failures and rollback

        [Fact]
        public void AFailureBeforeTheSaveLeavesNothingToRestore()
        {
            var store = new FakeSdtStore { FailBeforeSave = new InvalidOperationException("the SDK rejected the value") };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyUpdateFailed", Code(json));
            Assert.Contains("the SDK rejected the value", json["error"]["message"].ToString());
            Assert.False(json["persisted"].Value<bool>());
            Assert.Equal("transaction-verified", json["rollback"].ToString());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("restore"));
            Assert.Empty(store.Target);
        }

        [Fact]
        public void AFailureBeforeAnythingWasMutatedNeedsNoReconciliationAtAll()
        {
            var store = new FakeSdtStore
            {
                FailBeforeSave = new InvalidOperationException("the object could not be opened for writing"),
                FailPhase = SdtWritePhase.NotStarted
            };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyUpdateFailed", Code(json));
            Assert.False(json["persisted"].Value<bool>());
            Assert.Equal("not-needed", json["rollback"].ToString());
            // one read before the write; no read and no restore afterwards
            Assert.Equal(new[] { "read", "apply:idJsonEmpty" }, store.Log.ToArray());
        }

        [Fact]
        public void ASaveThatFailsAndIsRolledBackByTheTransactionIsVerifiedNotRestoredTwice()
        {
            var store = new FakeSdtStore { FailAtSave = new InvalidOperationException("disk full") };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyUpdateFailed", Code(json));
            Assert.False(json["persisted"].Value<bool>());
            Assert.Equal("transaction-verified", json["rollback"].ToString());
            Assert.Equal("100:root", json["versionToken"].ToString());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("restore"));
            Assert.Empty(store.Target);
        }

        [Fact]
        public void AFailureAfterTheSaveLandedRestoresThePreviousValueAndProvesIt()
        {
            var store = new FakeSdtStore { FailAfterPersist = new InvalidOperationException("the commit failed") };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyUpdateFailed", Code(json));
            Assert.False(json["persisted"].Value<bool>());
            Assert.Equal("verified", json["rollback"].ToString());
            // the value was default before, so the undo is a reset, not a write of the old value
            Assert.Contains("restore:reset", store.Log);
            Assert.Empty(store.Target);
        }

        [Fact]
        public void TheUndoOfAnExplicitValueWritesThatValueBack()
        {
            var store = new FakeSdtStore { FailAfterPersist = new InvalidOperationException("the commit failed") };
            store.Target[FakeSdtStore.Native] = "idJsonJsonNull";

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("verified", json["rollback"].ToString());
            Assert.Contains("restore:idJsonJsonNull", store.Log);
            Assert.Equal("idJsonJsonNull", store.Target[FakeSdtStore.Native]);
        }

        [Fact]
        public void ARollbackThatCannotBeConfirmedSaysSoAndKeepsPersistedTrue()
        {
            var store = new FakeSdtStore
            {
                FailAfterPersist = new InvalidOperationException("the commit failed"),
                RestoreSucceeds = false
            };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyUpdateFailed", Code(json));
            Assert.Equal("unverified", json["rollback"].ToString());
            Assert.True(json["persisted"].Value<bool>());
            Assert.Contains("Read get_item_property", json["error"]["hint"].ToString());
            // the receipt shows what the KB holds now against what it held before: exactly the requested property
            var stored = (JArray)json["storedChanges"];
            Assert.Single(stored);
            Assert.Equal("debug", stored[0]["path"].ToString());
            Assert.Equal("idJsonInclude", stored[0]["property"].ToString());
            Assert.Equal("idJsonEmpty", stored[0]["to"].ToString());
        }

        [Fact]
        public void AnotherWritersChangeIsNotReportedAsThePersistedWriteOfThisRequest()
        {
            // Our transaction was rolled back, then another writer changed the neighbour, and the restore is refused.
            var store = new FakeSdtStore
            {
                FailAtSave = new InvalidOperationException("disk full"),
                RestoreSucceeds = false,
                AfterTransactionRollback = s => s.Neighbour["Required"] = "True"
            };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyUpdateFailed", Code(json));
            Assert.Equal("unverified", json["rollback"].ToString());
            // the requested value is NOT in the KB: persisted says so, and the other writer's change is shown apart
            Assert.False(json["persisted"].Value<bool>());
            var stored = (JArray)json["storedChanges"];
            Assert.Single(stored);
            Assert.Equal("other", stored[0]["path"].ToString());
            Assert.Equal("Required", stored[0]["property"].ToString());
            Assert.Empty(store.Target);
        }

        [Fact]
        public void APersistedValueThatSomeoneElseOverwroteAndCouldNotBeRestoredIsNotCalledPersisted()
        {
            var store = new FakeSdtStore
            {
                AfterCommit = s => s.Target[FakeSdtStore.Native] = "idJsonJsonNull",
                RestoreSucceeds = false
            };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyNotPersisted", Code(json));
            Assert.Equal("unverified", json["rollback"].ToString());
            Assert.False(json["persisted"].Value<bool>());
            Assert.Equal("idJsonJsonNull", ((JArray)json["storedChanges"])[0]["to"].ToString());
        }

        [Fact]
        public void ADifferenceInAnotherObjectPartIsNamedInTheReceipt()
        {
            var store = new FakeSdtStore { SideEffect = s => s.OtherPartsEqual = false };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("unverified", json["rollback"].ToString());
            Assert.True(json["otherPartsChanged"].Value<bool>());
        }

        [Fact]
        public void TheReceiptListsAtMostTwentyStoredChangesAndSaysHowManyThereAre()
        {
            var store = new FakeSdtStore
            {
                FailAtSave = new InvalidOperationException("disk full"),
                RestoreSucceeds = false,
                AfterTransactionRollback = s => { for (int i = 0; i < 25; i++) s.Neighbour["p" + i.ToString("00")] = "x"; }
            };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal(20, ((JArray)json["storedChanges"]).Count);
            Assert.Equal(25, json["storedChangesTotal"].Value<int>());
        }

        [Fact]
        public void ASaveThatChangesAnotherMemberIsRejectedInsideTheTransaction()
        {
            var store = new FakeSdtStore { SideEffect = s => s.Neighbour["idJsonName"] = "damaged" };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyNotPersisted", Code(json));
            Assert.Contains("more than the requested property", json["mismatchDetail"].ToString());
            Assert.False(json["persisted"].Value<bool>());
            Assert.Equal("transaction-verified", json["rollback"].ToString());
            Assert.Equal("keep", store.Neighbour["idJsonName"]);
            Assert.Empty(store.Target);
            Assert.DoesNotContain("note", store.Log);
        }

        [Fact]
        public void ASaveThatAddsAPropertyToAnotherMemberIsRejected()
        {
            var store = new FakeSdtStore { SideEffect = s => s.Neighbour["Required"] = "True" };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyNotPersisted", Code(json));
            Assert.Contains("\"path\":\"other\"", json["mismatchDetail"].ToString());
            Assert.False(store.Neighbour.ContainsKey("Required"));
        }

        [Fact]
        public void ASaveThatAlsoChangesAnotherPropertyOfTheSameMemberIsRejected()
        {
            var store = new FakeSdtStore { SideEffect = s => s.Target["idJsonName"] = "renamed" };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyNotPersisted", Code(json));
            Assert.Contains("more than the requested property", json["mismatchDetail"].ToString());
            Assert.Empty(store.Target);
        }

        [Fact]
        public void ASaveThatStoresTheValueUnderAnotherPropertyIsRejected()
        {
            var store = new FakeSdtStore { StoreUnderKey = "idJsonIncludeShadow" };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyNotPersisted", Code(json));
            Assert.Contains("different property than requested", json["mismatchDetail"].ToString());
            Assert.Empty(store.Target);
        }

        [Fact]
        public void AValueThatOnlyReadsAsRequestedBecauseADefaultMovedIsNotACommittedWrite()
        {
            // The save stores nothing, but the property now reads as requested because its default moved.
            var store = new FakeSdtStore { SwallowWrites = true, SideEffect = s => s.Defaults[FakeSdtStore.Native] = "idJsonEmpty" };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyNotPersisted", Code(json));
            Assert.Contains("left the stored properties unchanged", json["mismatchDetail"].ToString());
        }

        [Fact]
        public void ASaveThatMovesAnotherObjectPartIsRejectedAndNotConfirmedAsRestored()
        {
            var store = new FakeSdtStore { SideEffect = s => s.OtherPartsEqual = false };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyNotPersisted", Code(json));
            Assert.Contains("another object part", json["mismatchDetail"].ToString());
            // the structure itself is back, but a part outside it still differs: that cannot be called verified. The requested
            // value is not in the KB, so persisted says false, and the difference outside the structure is named apart.
            Assert.Equal("unverified", json["rollback"].ToString());
            Assert.False(json["persisted"].Value<bool>());
            Assert.True(json["otherPartsChanged"].Value<bool>());
        }

        [Fact]
        public void APersistedValueThatDiffersFromTheStagedOneIsUndoneAndReported()
        {
            var store = new FakeSdtStore { AfterCommit = s => s.Target[FakeSdtStore.Native] = "idJsonJsonNull" };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("SdtItemPropertyNotPersisted", Code(json));
            Assert.False(json["persistedVerified"].Value<bool>());
            Assert.False(json["persisted"].Value<bool>());
            Assert.Equal("verified", json["rollback"].ToString());
            Assert.Contains("idJsonJsonNull", json["mismatchDetail"].ToString());
            Assert.Contains("idJsonEmpty", json["mismatchDetail"].ToString());
            Assert.Contains("restore:reset", store.Log);
            Assert.DoesNotContain("note", store.Log);
            Assert.Empty(store.Target);
        }

        [Fact]
        public void APostSaveStateThatCannotBeReadIsNeverReportedAsVerified()
        {
            var store = new FakeSdtStore { ReadsBeforeFailing = 1 };

            var json = Set(store, "JsonNullSerialization", "Empty");

            Assert.Equal("FreshReadUnavailable", Code(json));
            Assert.False(json["persistedVerified"].Value<bool>());
            Assert.Equal("unverified", json["rollback"].ToString());
            Assert.Equal(JTokenType.Null, json["persisted"].Type);
            Assert.DoesNotContain("note", store.Log);
        }

        // ------------------------------------------------------------------ diff

        [Fact]
        public void TheDiffNamesEveryMemberAndPropertyThatDiffers()
        {
            var before = new JObject
            {
                ["a"] = new JObject { ["kind"] = "item", ["properties"] = new JObject { ["x"] = "1", ["y"] = "2" } },
                ["gone"] = new JObject { ["kind"] = "item", ["properties"] = new JObject() },
                ["kind"] = new JObject { ["kind"] = "item", ["properties"] = new JObject() }
            };
            var after = new JObject
            {
                ["a"] = new JObject { ["kind"] = "item", ["properties"] = new JObject { ["x"] = "1", ["y"] = "3", ["z"] = "9" } },
                ["new"] = new JObject { ["kind"] = "item", ["properties"] = new JObject() },
                ["kind"] = new JObject { ["kind"] = "level", ["properties"] = new JObject() }
            };

            var diff = SdtItemPropertyEngine.Diff(before, after);

            var summary = diff.Select(d => (d["path"] + "|" + (d["property"] ?? d["change"]) + "|" + d["from"] + "|" + d["to"])).ToArray();
            Assert.Equal(new[]
            {
                "a|y|2|3",
                "a|z||9",
                "gone|memberRemoved||",
                "kind|kindChanged||",
                "new|memberAdded||"
            }, summary);
        }

        [Fact]
        public void IdenticalSnapshotsHaveNoDiffRegardlessOfPropertyOrder()
        {
            var one = new JObject { ["a"] = new JObject { ["kind"] = "item", ["properties"] = new JObject { ["x"] = "1", ["y"] = "2" } } };
            var two = new JObject { ["a"] = new JObject { ["kind"] = "item", ["properties"] = new JObject { ["y"] = "2", ["x"] = "1" } } };
            Assert.Empty(SdtItemPropertyEngine.Diff(one, two));
        }
    }
}
