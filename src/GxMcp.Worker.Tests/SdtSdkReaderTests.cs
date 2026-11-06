using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services.Structure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The duck-typed SDK reads: member paths, the stored-property snapshot, the property catalog and the effective values,
    /// against doubles that expose the same member names as SDTLevel / SDTItem / SDTItemEntity / PropDefinition.
    /// </summary>
    public class SdtSdkReaderTests
    {
        private static FakeDefinition Definition(string name, string display, Type type = null, object defaultValue = null)
            => new FakeDefinition { Name = name, DisplayName = display, Category = "Serialization", PropertyType = type ?? typeof(string), DefaultValue = defaultValue };

        // root { debug (item), Items (level) { Id (item), Deep (level) { Leaf (item) } } }
        private static FakeMember BuildTree()
        {
            var root = new FakeMember { Name = "root", IsLeafItem = false };
            var debug = new FakeMember { Name = "debug" };
            debug.ItemEntity.Definitions.Add(Definition("idJsonInclude", "Json Null Serialization", defaultValue: "idJsonNoProperty"));
            debug.ItemEntity.Stored["idJsonName"] = "dbg";
            debug.ItemEntity.Stored["Name"] = "debug";
            var items = new FakeMember { Name = "Items", IsLeafItem = false };
            items.ItemEntity.Stored["Name"] = "Items";
            var id = new FakeMember { Name = "Id" };
            id.ItemEntity.Stored["Name"] = "Id";
            var deep = new FakeMember { Name = "Deep", IsLeafItem = false };
            var leaf = new FakeMember { Name = "Leaf" };
            deep.Items.Add(leaf);
            items.Items.Add(id);
            items.Items.Add(deep);
            root.Items.Add(debug);
            root.Items.Add(items);
            return root;
        }

        // ---- the stored-property snapshot

        [Fact]
        public void TheSnapshotKeysEveryMemberByPathWithItsKindAndItsStoredPropertiesOnly()
        {
            var members = SdtSdkReader.CaptureMembers(BuildTree());

            Assert.Equal(new[] { "$root", "Items", "Items/Deep", "Items/Deep/Leaf", "Items/Id", "debug" },
                members.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal("root", members["$root"]["kind"].ToString());
            Assert.Equal("item", members["debug"]["kind"].ToString());
            Assert.Equal("level", members["Items"]["kind"].ToString());
            Assert.Equal("level", members["Items/Deep"]["kind"].ToString());
            Assert.Equal("item", members["Items/Deep/Leaf"]["kind"].ToString());
            Assert.Equal("dbg", members["debug"]["properties"]["idJsonName"].ToString());
            // a default-valued property is not stored, so it is not in the snapshot
            Assert.Null(members["debug"]["properties"]["idJsonInclude"]);
        }

        [Fact]
        public void TheSnapshotKnowsThePositionOfEveryMemberAmongItsSiblings()
        {
            var members = SdtSdkReader.CaptureMembers(BuildTree());

            Assert.Equal(0, (int)members["debug"]["index"]);
            Assert.Equal(1, (int)members["Items"]["index"]);
            Assert.Equal(0, (int)members["Items/Id"]["index"]);
            Assert.Equal(1, (int)members["Items/Deep"]["index"]);
            Assert.Equal(0, (int)members["Items/Deep/Leaf"]["index"]);
            Assert.Null(members["$root"]["index"]);
        }

        [Fact]
        public void AReorderedStructureIsADifferenceEvenWhenEveryMemberKeepsItsProperties()
        {
            var tree = BuildTree();
            var before = SdtSdkReader.CaptureMembers(tree);

            var first = tree.Items[0];
            tree.Items.RemoveAt(0);
            tree.Items.Add(first);
            var after = SdtSdkReader.CaptureMembers(tree);

            Assert.False(JToken.DeepEquals(before, after));
            var diff = SdtItemPropertyEngine.Diff(before, after);
            Assert.Contains(diff, d => d["path"].ToString() == "debug" && d["change"]?.ToString() == "moved");
            Assert.Contains(diff, d => d["path"].ToString() == "Items" && d["change"]?.ToString() == "moved");
        }

        [Fact]
        public void ARootLevelPropertyChangedByASaveIsADifference()
        {
            var tree = BuildTree();
            var before = SdtSdkReader.CaptureMembers(tree);

            tree.ItemEntity.Stored["AttCollection"] = "True";
            var diff = SdtItemPropertyEngine.Diff(before, SdtSdkReader.CaptureMembers(tree));

            Assert.Single(diff);
            Assert.Equal("$root", diff[0]["path"].ToString());
            Assert.Equal("AttCollection", diff[0]["property"].ToString());
        }

        [Fact]
        public void ASnapshotChangesExactlyWhenAStoredPropertyChanges()
        {
            var tree = BuildTree();
            var before = SdtSdkReader.CaptureMembers(tree);
            Assert.True(JToken.DeepEquals(before, SdtSdkReader.CaptureMembers(tree)));

            tree.Items[0].ItemEntity.SetPropertyValue("idJsonInclude", "idJsonEmpty");
            var after = SdtSdkReader.CaptureMembers(tree);

            Assert.False(JToken.DeepEquals(before, after));
            var diff = SdtItemPropertyEngine.Diff(before, after);
            Assert.Single(diff);
            Assert.Equal("debug", diff[0]["path"].ToString());
            Assert.Equal("idJsonInclude", diff[0]["property"].ToString());
        }

        // ---- member path

        [Fact]
        public void APathResolvesIgnoringCaseAndReturnsTheNamesAsTheSdtStoresThem()
        {
            List<string> canonical;
            object member = SdtSdkReader.ResolveMember(BuildTree(), new[] { "items", "DEEP", "leaf" }, out canonical);

            Assert.Equal(new[] { "Items", "Deep", "Leaf" }, canonical.ToArray());
            Assert.Equal("Leaf", ((FakeMember)member).Name);
        }

        [Fact]
        public void AMissingMemberIsNamedWithTheMembersThatExistThere()
        {
            List<string> canonical;
            var error = Assert.Throws<SdtUserFacingException>(() =>
                SdtSdkReader.ResolveMember(BuildTree(), new[] { "Items", "Nope" }, out canonical));

            Assert.Equal("ItemNotFound", error.Code);
            Assert.Contains("'Nope'", error.Message);
            Assert.Contains("'Items'", error.Message);
            Assert.Contains("Id, Deep", error.Hint);
        }

        [Fact]
        public void ALeafItemHasNoMembersBelowIt()
        {
            List<string> canonical;
            var error = Assert.Throws<SdtUserFacingException>(() =>
                SdtSdkReader.ResolveMember(BuildTree(), new[] { "debug", "child" }, out canonical));

            Assert.Equal("ItemNotALevel", error.Code);
            Assert.Contains("Shorten itemPath to 'debug'", error.Hint);
        }

        [Fact]
        public void TwoMembersThatDifferOnlyByCaseAreAmbiguousUnlessTheCaseIsExact()
        {
            var root = new FakeMember { Name = "root", IsLeafItem = false };
            root.Items.Add(new FakeMember { Name = "Dup" });
            root.Items.Add(new FakeMember { Name = "dup" });
            List<string> canonical;

            var error = Assert.Throws<SdtUserFacingException>(() => SdtSdkReader.ResolveMember(root, new[] { "DUP" }, out canonical));
            Assert.Equal("AmbiguousItem", error.Code);

            object exact = SdtSdkReader.ResolveMember(root, new[] { "dup" }, out canonical);
            Assert.Equal("dup", ((FakeMember)exact).Name);
        }

        // ---- catalog

        [Fact]
        public void TheCatalogCarriesNamesTypeFlagsAndTheAllowedValuesOfAnEnumeration()
        {
            var member = new FakeMember { Name = "debug" };
            var json = Definition("idJsonInclude", "Json Null Serialization", defaultValue: "idJsonNoProperty");
            json.Standard.AddRange(new object[] { "idJsonEmpty", "idJsonJsonNull", "idJsonNoProperty" });
            json.StandardNames["idJsonEmpty"] = "Empty";
            json.StandardNames["idJsonJsonNull"] = "JSON null";
            json.StandardNames["idJsonNoProperty"] = "No Property";
            member.ItemEntity.Definitions.Add(json);
            member.ItemEntity.Definitions.Add(Definition("Required", "Required", typeof(bool), false));
            member.ItemEntity.Definitions.Add(Definition("Locked", "Locked"));
            member.ItemEntity.ReadOnly["Locked"] = true;
            member.ItemEntity.Definitions.Add(Definition("OnlyCollections", "Only Collections"));
            member.ItemEntity.NotApplicable["OnlyCollections"] = true;

            var catalog = SdtSdkReader.BuildCatalog(member, false);

            var byName = catalog.ToDictionary(p => p.NativeName);
            Assert.Equal(4, catalog.Count);
            var info = byName["idJsonInclude"];
            Assert.Equal("Json Null Serialization", info.DisplayName);
            Assert.Equal("Serialization", info.Category);
            Assert.True(info.IsEnumerated);
            Assert.Equal(new[] { "idJsonEmpty", "idJsonJsonNull", "idJsonNoProperty" }, info.Options.Select(o => o.Raw).ToArray());
            Assert.Equal(new[] { "Empty", "JSON null", "No Property" }, info.Options.Select(o => o.Label).ToArray());
            Assert.Equal("Boolean", byName["Required"].TypeName);
            Assert.False(byName["Required"].IsEnumerated);
            Assert.True(byName["Locked"].ReadOnly);
            Assert.False(byName["OnlyCollections"].Applicable);
            Assert.True(byName["idJsonInclude"].Applicable);
        }

        [Fact]
        public void TheAllowedValuesKeepTheObjectTheSdkListedSoTheStoredTypeSurvives()
        {
            var member = new FakeMember { Name = "debug" };
            var dimensions = Definition("AttNumDim", "Dimensions", typeof(int), 0);
            dimensions.Standard.AddRange(new object[] { 0, 1, 2 });
            member.ItemEntity.Definitions.Add(dimensions);

            var info = SdtSdkReader.BuildCatalog(member, false).Single();

            Assert.Equal(new object[] { 0, 1, 2 }, info.Options.Select(o => o.Value).ToArray());
            Assert.IsType<int>(info.Options[1].Value);
            Assert.Equal(new[] { "0", "1", "2" }, info.Options.Select(o => o.Raw).ToArray());
            Assert.False(info.CatalogIncomplete);
        }

        [Fact]
        public void APropertyWhoseAllowedValuesCannotBeListedIsMarkedIncompleteNotTreatedAsFreeText()
        {
            var member = new FakeMember { Name = "debug" };
            var json = Definition("idJsonInclude", "Json Null Serialization", defaultValue: "idJsonNoProperty");
            json.ThrowOnStandardValues = true;
            member.ItemEntity.Definitions.Add(json);

            var info = SdtSdkReader.BuildCatalog(member, false).Single();

            Assert.True(info.CatalogIncomplete);
            Assert.Contains("allowed values", info.IncompleteReason);
            Assert.False(info.IsEnumerated);
            Assert.Equal("PropertyCatalogIncomplete", SdtItemPropertyEngine.WriteBlock(info));
        }

        [Fact]
        public void APropertyWithoutADescriptorIsMarkedIncompleteSoItsDefaultsAreNotTrusted()
        {
            var member = new FakeMember { Name = "debug" };
            member.ItemEntity.Definitions.Add(Definition("Missing", "Missing"));
            member.ItemEntity.MissingDescriptor.Add("Missing");
            member.ItemEntity.Definitions.Add(Definition("Throwing", "Throwing"));
            member.ItemEntity.ThrowingDescriptor.Add("Throwing");
            member.ItemEntity.Definitions.Add(Definition("Fine", "Fine"));

            var byName = SdtSdkReader.BuildCatalog(member, false).ToDictionary(p => p.NativeName);

            Assert.True(byName["Missing"].CatalogIncomplete);
            Assert.Contains("no descriptor", byName["Missing"].IncompleteReason);
            Assert.True(byName["Throwing"].CatalogIncomplete);
            Assert.Contains("descriptor could not be read", byName["Throwing"].IncompleteReason);
            Assert.False(byName["Fine"].CatalogIncomplete);
            Assert.Null(SdtItemPropertyEngine.WriteBlock(byName["Fine"]));
        }

        [Fact]
        public void APropertyWhoseStoredTypeCannotBeReadIsMarkedIncomplete()
        {
            var member = new FakeMember { Name = "debug" };
            var untyped = Definition("Untyped", "Untyped");
            untyped.PropertyType = null;
            member.ItemEntity.Definitions.Add(untyped);

            var info = SdtSdkReader.BuildCatalog(member, false).Single();

            Assert.True(info.CatalogIncomplete);
            Assert.Contains("stored type", info.IncompleteReason);
        }

        [Fact]
        public void ADefinitionThatCannotBeReadIsSkippedNotFatal()
        {
            var member = new FakeMember { Name = "debug" };
            member.ItemEntity.Definitions.Add(Definition("Good", "Good"));
            member.ItemEntity.Definitions.Add(new FakeDefinition { Name = "" });

            var catalog = SdtSdkReader.BuildCatalog(member, false);

            Assert.Equal(new[] { "Good" }, catalog.Select(p => p.NativeName).ToArray());
        }

        // ---- effective values

        [Fact]
        public void TheEffectiveValueIsTheStoredOneElseTheDefaultAndSaysWhichOne()
        {
            var member = new FakeMember { Name = "debug" };
            member.ItemEntity.Definitions.Add(Definition("idJsonInclude", "Json Null Serialization", defaultValue: "idJsonNoProperty"));
            member.ItemEntity.Definitions.Add(Definition("idJsonName", "Json Name", defaultValue: ""));
            member.ItemEntity.Stored["idJsonName"] = "dbg";
            var catalog = SdtSdkReader.BuildCatalog(member, false);

            var effective = SdtSdkReader.ReadEffective(member, catalog);

            Assert.True(effective["idJsonInclude"].IsDefault);
            Assert.Equal("idJsonNoProperty", effective["idJsonInclude"].Raw);
            Assert.Equal("idJsonNoProperty", effective["idJsonInclude"].DefaultRaw);
            Assert.False(effective["idJsonName"].IsDefault);
            Assert.Equal("dbg", effective["idJsonName"].Raw);
        }

        // ---- the SDK's own typed names (a fact about the installed SDK, not about this code)

        [Fact]
        public void TheSdkDeclaresJsonNullSerializationWithItsThreeStoredValuesForItemsAndLevels()
        {
            var sdkAssembly = typeof(Artech.Genexus.Common.Objects.SDT).Assembly;

            foreach (bool isLevel in new[] { false, true })
            {
                var typed = SdtTypedCatalog.Load(sdkAssembly, isLevel);

                Assert.True(typed.ContainsKey("idJsonInclude"), "idJsonInclude missing for " + (isLevel ? "levels" : "items"));
                var entry = typed["idJsonInclude"];
                Assert.Equal("JsonNullSerialization", entry.Constant);
                Assert.Equal("Empty", entry.Members["idJsonEmpty"]);
                Assert.Equal("JsonNull", entry.Members["idJsonJsonNull"]);
                Assert.Equal("NoProperty", entry.Members["idJsonNoProperty"]);
            }
        }

        [Fact]
        public void AnAssemblyWithoutThePropertiesCatalogYieldsAnEmptyOneNotAnError()
        {
            Assert.Empty(SdtTypedCatalog.Load(typeof(SdtSdkReaderTests).Assembly, false));
            Assert.Empty(SdtTypedCatalog.Load(null, false));
        }
    }
}
