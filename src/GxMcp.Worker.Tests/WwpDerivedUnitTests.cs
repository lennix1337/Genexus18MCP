using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The SDK-free rules of the regeneration unit: which derived objects are helpers, whether they
    // agree with their callers, and whether the caller's version tokens still hold.
    public class WwpDerivedUnitTests
    {
        private static WwpObjectState State(string name, string type, params string[] partsAndTexts)
        {
            var state = new WwpObjectState { Name = name, Type = type, Guid = Guid.NewGuid(), QualifiedName = name };
            for (int i = 0; i < partsAndTexts.Length; i += 2) state.Parts[partsAndTexts[i]] = partsAndTexts[i + 1];
            return state;
        }

        private static WwpObjectState Parent(string events) => State("Trn", "Transaction",
            "Events", events,
            "Variables", "&Cond_A : Attribute:A\r\n&Cond_B : Attribute:B");

        private static WwpObjectState Helper(string rules) => State("LoadDVComboTrn", "Procedure",
            "Rules", rules,
            "Variables", "&Cond_A : Attribute:A\r\n&Cond_B : Attribute:B");

        [Fact]
        public void ADerivedProcedureThatTheParentCalls_IsAHelper()
        {
            WwpObjectState parent = Parent("&x = LoadDVComboTrn(&Cond_A, &Cond_B)");
            WwpObjectState helper = Helper("parm(in:&Cond_A, in:&Cond_B);");

            Assert.True(WwpDerivedUnit.IsHelper(helper, new[] { parent, helper }));
        }

        [Fact]
        public void ADerivedProcedureNothingCalls_IsNotAHelper_AndNeitherIsADerivedWebPanel()
        {
            WwpObjectState parent = Parent("// LoadDVComboTrn(&Cond_A)");
            WwpObjectState procedure = Helper("parm(in:&Cond_A);");
            WwpObjectState webPanel = State("LoadDVComboTrn", "WebPanel", "Events", "x");

            Assert.False(WwpDerivedUnit.IsHelper(procedure, new[] { parent, procedure }));
            Assert.False(WwpDerivedUnit.IsHelper(webPanel, new[] { State("Caller", "Transaction", "Events", "LoadDVComboTrn(&a)"), webPanel }));
        }

        [Fact]
        public void AHelperCalledOnlyByAnotherDerivedObject_IsStillAHelper()
        {
            WwpObjectState grid = State("WWTrn", "WebPanel", "Events", "LoadDVComboTrn(&Cond_A, &Cond_B)", "Variables", "&Cond_A : Attribute:A\r\n&Cond_B : Attribute:B");
            WwpObjectState helper = Helper("parm(in:&Cond_A, in:&Cond_B);");

            Assert.True(WwpDerivedUnit.IsHelper(helper, new[] { Parent("nothing"), grid, helper }));
        }

        [Fact]
        public void AHelperOnTheOldContract_MakesTheWholeUnitInconsistent()
        {
            // The reported desync: the parent already calls with two arguments, the helper still declares one.
            WwpObjectState parent = Parent("LoadDVComboTrn(&Cond_A, &Cond_B)");
            WwpObjectState staleHelper = Helper("parm(in:&Cond_A);");

            JObject contract = WwpDerivedUnit.EvaluateContract(new[] { staleHelper }, new[] { parent, staleHelper });

            Assert.False((bool)contract["consistent"]);
            Assert.Equal("argumentCount", (string)contract["helpers"][0]["mismatches"][0]["kind"]);
        }

        [Fact]
        public void ARegeneratedHelper_MakesTheUnitConsistent()
        {
            WwpObjectState parent = Parent("LoadDVComboTrn(&Cond_A, &Cond_B)");
            WwpObjectState helper = Helper("parm(in:&Cond_A, in:&Cond_B);");

            Assert.True((bool)WwpDerivedUnit.EvaluateContract(new[] { helper }, new[] { parent, helper })["consistent"]);
        }

        [Fact]
        public void ASecondCallerThatDisagrees_FailsTheUnitEvenWhenTheParentAgrees()
        {
            WwpObjectState parent = Parent("LoadDVComboTrn(&Cond_A, &Cond_B)");
            WwpObjectState other = State("WWTrn", "WebPanel", "Events", "LoadDVComboTrn(&Cond_B, &Cond_A)",
                "Variables", "&Cond_A : Attribute:A\r\n&Cond_B : Attribute:B");
            WwpObjectState helper = Helper("parm(in:&Cond_A, in:&Cond_B);");

            JObject contract = WwpDerivedUnit.EvaluateContract(new[] { helper }, new[] { parent, other, helper });

            Assert.False((bool)contract["consistent"]);
            Assert.Contains(contract["helpers"][0]["mismatches"].Select(m => (string)m["kind"]), kind => kind == "order");
        }

        private static List<WwpDerivedUnit.VersionedObject> Objects(string hostFallback = null) => new List<WwpDerivedUnit.VersionedObject>
        {
            new WwpDerivedUnit.VersionedObject { Key = "WorkWithPlus:WorkWithPlusTrn", Name = "WorkWithPlusTrn", Current = "h1", Fallback = hostFallback },
            new WwpDerivedUnit.VersionedObject { Key = "Transaction:Trn", Name = "Trn", Current = "p1" },
            new WwpDerivedUnit.VersionedObject { Key = "Procedure:LoadDVComboTrn", Name = "LoadDVComboTrn", Current = "d1" }
        };

        [Fact]
        public void MatchingTokens_ByKey_PassWithNothingStaleOrMissing()
        {
            var supplied = JObject.Parse("{'WorkWithPlus:WorkWithPlusTrn':'h1','Transaction:Trn':'p1','Procedure:LoadDVComboTrn':'d1'}");

            List<JObject> stale = WwpDerivedUnit.CompareVersions(Objects(), supplied, out List<string> missing);

            Assert.Empty(stale);
            Assert.Empty(missing);
        }

        [Fact]
        public void AMovedObject_IsListedWithBothTokens_AndNothingElseIsFlagged()
        {
            var supplied = JObject.Parse("{'WorkWithPlus:WorkWithPlusTrn':'h1','Transaction:Trn':'p0','Procedure:LoadDVComboTrn':'d1'}");

            List<JObject> stale = WwpDerivedUnit.CompareVersions(Objects(), supplied, out List<string> missing);

            JObject entry = Assert.Single(stale);
            Assert.Equal("Transaction:Trn", (string)entry["object"]);
            Assert.Equal("p0", (string)entry["expected"]);
            Assert.Equal("p1", (string)entry["current"]);
            Assert.Empty(missing);
        }

        [Fact]
        public void AnObjectWithoutAToken_IsMissing_NotSkipped()
        {
            var supplied = JObject.Parse("{'WorkWithPlus:WorkWithPlusTrn':'h1','Transaction:Trn':'p1'}");

            WwpDerivedUnit.CompareVersions(Objects(), supplied, out List<string> missing);

            Assert.Equal(new[] { "Procedure:LoadDVComboTrn" }, missing.ToArray());
        }

        [Fact]
        public void TheHostBaseVersion_StandsInForTheHostToken_Only()
        {
            var supplied = JObject.Parse("{'Transaction:Trn':'p1','Procedure:LoadDVComboTrn':'d1'}");

            WwpDerivedUnit.CompareVersions(Objects("h1"), supplied, out List<string> missing);

            Assert.Empty(missing);
        }

        [Fact]
        public void ABareName_IsAcceptedOnlyWhenTheNameIsUnambiguousInTheUnit()
        {
            var unique = JObject.Parse("{'WorkWithPlusTrn':'h1','Trn':'p1','LoadDVComboTrn':'d1'}");
            WwpDerivedUnit.CompareVersions(Objects(), unique, out List<string> uniqueMissing);
            Assert.Empty(uniqueMissing);

            var objects = Objects();
            objects.Add(new WwpDerivedUnit.VersionedObject { Key = "WebPanel:LoadDVComboTrn", Name = "LoadDVComboTrn", Current = "w1" });
            WwpDerivedUnit.CompareVersions(objects, unique, out List<string> ambiguousMissing);
            Assert.Equal(new[] { "Procedure:LoadDVComboTrn", "WebPanel:LoadDVComboTrn" }, ambiguousMissing.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
    }
}
