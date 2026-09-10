using System;
using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Regression (friction 2026-06-02): a bare name that matches both a Transaction
    // and its generated Table must resolve deterministically to the editable logic
    // object, not to whichever the index dictionary happened to enumerate first.
    // This nondeterminism was the root cause of genexus_inspect (which got the Table)
    // and genexus_analyze impact (which prefers the Transaction) silently resolving
    // DIFFERENT objects for the same name and appearing to contradict each other.
    public class ObjectResolutionPriorityTests
    {
        [Theory]
        [InlineData("11111111-1111-1111-1111-111111111111", "Pattern Settings", null, true, "11111111-1111-1111-1111-111111111111")]
        [InlineData("Pattern Settings:11111111-1111-1111-1111-111111111111", "PatternSettings", null, true, "11111111-1111-1111-1111-111111111111")]
        [InlineData("Pattern Settings:WorkWithPlus", "PatternSettings", null, true, null)]
        [InlineData("Pattern Settings:WorkWithPlus", "Module", null, false, null)]
        [InlineData("Module:WorkWithPlus", "Pattern Settings", null, false, null)]
        [InlineData("Pattern Settings:WorkWithPlus", "UnknownType", null, false, null)]
        [InlineData(":WorkWithPlus", null, null, false, null)]
        [InlineData("Pattern Settings:", null, null, false, null)]
        [InlineData("11111111-1111-1111-1111-111111111111", "Pattern Settings", "22222222-2222-2222-2222-222222222222", false, "22222222-2222-2222-2222-222222222222")]
        [InlineData("83476c1e-fa72-4229-9930-f51b954fca2d:1", "Pattern Settings", null, true, null)]
        public void TargetSyntaxPreservesTypedIdentity(string target, string type, string guid, bool valid, string normalizedGuid)
        {
            Assert.Equal(valid, ObjectService.NormalizeResolutionTarget(ref target, ref type, ref guid));
            Assert.Equal(normalizedGuid, guid);
            if (valid && target == "WorkWithPlus") Assert.True(ObjectService.ResolutionTypeMatches("Pattern Settings", type));
        }

        [Theory]
        [InlineData("Module", "Pattern Settings", false)]
        [InlineData("PatternSettings", "Pattern Settings", true)]
        [InlineData("Pattern Settings", "patternsettings", true)]
        [InlineData("Module", "UnknownType", false)]
        [InlineData("Transaction", "Table", false)]
        [InlineData("Transaction", "Trn", true)]
        [InlineData(null, "Pattern Settings", false)]
        [InlineData("Module", null, true)]
        public void ExplicitTypeNeverAcceptsAnotherType(string actual, string requested, bool matches)
        {
            Assert.Equal(matches, ObjectService.ResolutionTypeMatches(actual, requested));
        }

        private static SearchIndex.IndexEntry E(string name, string type)
            => new SearchIndex.IndexEntry { Name = name, Type = type };

        [Fact]
        public void TransactionWinsOverTable_RegardlessOfOrder()
        {
            var a = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("Acao", "Table"), E("Acao", "Transaction") });
            var b = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("Acao", "Transaction"), E("Acao", "Table") });
            Assert.Equal("Transaction", a.Type);
            Assert.Equal("Transaction", b.Type);
        }

        [Fact]
        public void TableReturnedWhenItIsTheOnlyMatch()
        {
            var only = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("Acao", "Table") });
            Assert.Equal("Table", only.Type);
        }

        [Fact]
        public void FolderAndFileRankLastBehindLogic()
        {
            var pick = ObjectService.PrioritizeNameMatches(new List<SearchIndex.IndexEntry>
            {
                E("X", "Folder"), E("X", "Image"), E("X", "Procedure")
            });
            Assert.Equal("Procedure", pick.Type);
        }

        [Fact]
        public void DeterministicAcrossLogicTypes()
        {
            // Two logic types colliding (rare) → stable tiebreak by type name, not
            // by enumeration order, so repeated calls always agree.
            var a = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("X", "WebPanel"), E("X", "Procedure") });
            var b = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("X", "Procedure"), E("X", "WebPanel") });
            Assert.Equal(a.Type, b.Type);
        }

        [Fact]
        public void IdentityCanSupplyTargetWhenNameIsOmitted()
        {
            Assert.Equal("Operations/ReverseOrder",
                ObjectService.ResolveTargetForIdentity(null, null, null, "Operations/ReverseOrder"));
            Assert.Equal("type-guid-2213",
                ObjectService.ResolveTargetForIdentity(null, null, "type-guid-2213", null));
            Assert.Equal("guid-2213",
                ObjectService.ResolveTargetForIdentity(null, "guid-2213", null, null));
            Assert.Equal("NamedObject",
                ObjectService.ResolveTargetForIdentity("NamedObject", "guid-ignored", null, null));
        }

        [Theory]
        [InlineData("11111111-1111-1111-1111-111111111111:42")]
        [InlineData("EntityKey(11111111-1111-1111-1111-111111111111, 42)")]
        [InlineData("11111111-1111-1111-1111-111111111111-42")]
        [InlineData("  EntityKey(11111111-1111-1111-1111-111111111111, 42)  ")]
        public void EntityKeyTextIsParsedIntoTypedIdentity(string raw)
        {
            Assert.True(ObjectService.TryParseEntityKey(raw, out var typeGuid, out var id));
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), typeGuid);
            Assert.Equal(42, id);
        }

        [Theory]
        [InlineData("EntityKey(11111111-1111-1111-1111-111111111111, -1)")]
        [InlineData("11111111-1111-1111-1111-111111111111:-1")]
        [InlineData("11111111-1111-1111-1111-111111111111--1")]
        [InlineData("prefix11111111-1111-1111-1111-111111111111-1")]
        [InlineData("EntityKey(11111111-1111-1111-1111-111111111111, 1")]
        [InlineData("11111111-1111-1111-1111-111111111111,1")]
        [InlineData("11111111-1111-1111-1111-111111111111:2147483648")]
        [InlineData("11111111-1111-1111-1111-111111111111:1suffix")]
        public void MalformedEntityKeysCannotResolveToAnotherIdentity(string raw)
        {
            Assert.False(ObjectService.TryParseEntityKey(raw, out _, out _));
        }

        [Fact]
        public void IndexEntryPersistsAllNativeIdentityFields()
        {
            var index = new SearchIndex();
            index.Objects["Procedure:ReverseOrder"] = new SearchIndex.IndexEntry
            {
                Guid = "22222222-2222-2222-2222-222222222222",
                EntityKey = "EntityKey(11111111-1111-1111-1111-111111111111, 42)",
                EntityTypeGuid = "11111111-1111-1111-1111-111111111111",
                EntityId = 42,
                Name = "ReverseOrder",
                Type = "Procedure",
                Path = "Root Module/Operations/ReverseOrder"
            };

            var roundTripped = SearchIndex.FromJson(index.ToJson());
            var entry = roundTripped.Objects["Procedure:ReverseOrder"];
            Assert.Equal("22222222-2222-2222-2222-222222222222", entry.Guid);
            Assert.Equal("EntityKey(11111111-1111-1111-1111-111111111111, 42)", entry.EntityKey);
            Assert.Equal(42, entry.EntityId);
            Assert.Equal("Root Module/Operations/ReverseOrder", entry.Path);
        }
    }
}
