using System;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// genexus_create builds a Master Page: a WebPanel with WEB_COMP=Master and a form holding a ContentHolder, given to the
    /// object together before its first save (the SDK refuses either one without the other once the object is saved). The
    /// SDK part needs a live KB; these pin what does not: the type names, the form, the order of the steps in the create
    /// and the warning a create carries when the object was not placed where the caller asked.
    /// </summary>
    public class MasterPageCreateTests
    {
        [Theory]
        [InlineData("MasterPage")]
        [InlineData("masterpage")]
        [InlineData("MASTERPAGE")]
        [InlineData("Master Page")]
        [InlineData("  Master   Page  ")]
        [InlineData("master_page")]
        [InlineData("Master-Page")]
        [InlineData("Web Master Panel")]
        [InlineData("WebMasterPanel")]
        [InlineData("MasterPanel")]
        public void IsMasterPage_AcceptsEverySpelling(string type)
        {
            Assert.True(ObjectCreateTypes.IsMasterPage(type));
        }

        [Theory]
        [InlineData("WebPanel")]
        [InlineData("WebComponent")]
        [InlineData("Master")]
        [InlineData("MasterPageX")]
        [InlineData("Menu")]
        [InlineData("   ")]
        [InlineData("")]
        [InlineData(null)]
        public void IsMasterPage_RefusesEveryOtherType(string type)
        {
            Assert.False(ObjectCreateTypes.IsMasterPage(type));
        }

        [Fact]
        public void SupportedTypes_NamesMasterPage()
        {
            string[] listed = ObjectCreateTypes.SupportedTypes.Split(new[] { ", " }, StringSplitOptions.None);

            Assert.Contains(ObjectCreateTypes.MasterPageType, listed);
        }

        [Theory]
        [InlineData("Master", true)]
        [InlineData("master", true)]
        [InlineData("No", false)]
        [InlineData("Yes", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsMasterWebComp_AcceptsOnlyTheValueThatMakesAWebPanelAMasterPage(string webComp, bool expected)
        {
            Assert.Equal(expected, ObjectCreateTypes.IsMasterWebComp(webComp));
        }

        [Fact]
        public void MasterPageMinimalForm_HoldsAContentHolderInACell_AndNothingTheSdkDrops()
        {
            XDocument form = XDocument.Parse(ObjectCreateTypes.MasterPageMinimalForm);

            Assert.Equal("GxMultiForm", form.Root.Name.LocalName);
            XElement holder = Assert.Single(form.Descendants(), e => e.Name.LocalName == "contentholder");
            Assert.Equal("ContentHolder", (string)holder.Attribute("controlName"));
            Assert.Equal("cell", holder.Parent.Name.LocalName);
            // The SDK drops PanelType and generates the ids of the containers; a form that carries them fails the write check.
            Assert.DoesNotContain(form.Descendants().SelectMany(e => e.Attributes()), a => a.Name.LocalName == "PanelType");
            Assert.DoesNotContain(
                form.Descendants().Where(e => e.Name.LocalName != "Form").SelectMany(e => e.Attributes()),
                a => a.Name.LocalName == "id");
            Assert.Equal((string)form.Root.Attribute("rootId"), (string)form.Descendants().Single(e => e.Name.LocalName == "Form").Attribute("id"));
        }

        // The Worker's answer of a move into a Folder asked for as a Module (what genexus_create module=<folder> does).
        private static JObject KindMismatch(string resolvedKind) => new JObject
        {
            ["status"] = "error",
            ["error"] = new JObject
            {
                ["code"] = "DestinationKindMismatch",
                ["message"] = "'Root Module/SynthModule/SynthFolder' is a " + resolvedKind + ", not a " + (resolvedKind == "Folder" ? "Module" : "Folder") + ".",
                ["placement"] = new JObject { ["requested"] = "SynthFolder", ["resolvedKind"] = resolvedKind }
            }
        };

        [Fact]
        public void PlacementWarning_OfAFolderGivenAsAModule_NamesWhereTheObjectIsAndTheParameterToUse()
        {
            string warning = ObjectCreateTypes.BuildPlacementWarning("SynthFolder", "Module", KindMismatch("Folder"));

            Assert.Contains("created in the Root Module, NOT in 'SynthFolder'", warning);
            Assert.Contains("'Root Module/SynthModule/SynthFolder' is a Folder, not a Module", warning);
            Assert.Contains("Pass it as folder=SynthFolder (it is a Folder), not as module=SynthFolder.", warning);
            Assert.Contains("Move it with genexus_properties action=move, or delete it and repeat the create with the right destination.", warning);
        }

        [Fact]
        public void PlacementWarning_OfAMoveThatSucceeded_IsNull()
        {
            var moved = new JObject { ["status"] = "ok", ["result"] = new JObject { ["moved"] = true } };

            Assert.Null(ObjectCreateTypes.BuildPlacementWarning("SynthFolder", "Folder", moved));
        }

        [Fact]
        public void PlacementWarning_OfAKindMismatchWithoutTheResolvedKind_GivesTheReasonAndNoParameterAdvice()
        {
            var mismatch = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "DestinationKindMismatch", ["message"] = "'X' is a Folder, not a Module." }
            };

            string warning = ObjectCreateTypes.BuildPlacementWarning("SynthFolder", "Module", mismatch);

            Assert.Contains("'X' is a Folder, not a Module.", warning);
            Assert.DoesNotContain("Pass it as", warning);
        }

        [Fact]
        public void PlacementWarning_OfAnErrorWithoutAMessageOrAPlacementObject_StillSaysWhereTheObjectIs()
        {
            var bare = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "DestinationKindMismatch", ["placement"] = "not an object" }
            };

            string warning = ObjectCreateTypes.BuildPlacementWarning("SynthFolder", "Folder", bare);

            Assert.StartsWith("The object was created in the Root Module, NOT in 'SynthFolder'.", warning);
            Assert.DoesNotContain("Pass it as", warning);
        }

        [Fact]
        public void PlacementWarning_OfAModuleGivenAsAFolder_NamesTheParameterToUse()
        {
            string warning = ObjectCreateTypes.BuildPlacementWarning("SynthFolder", "Folder", KindMismatch("Module"));

            Assert.Contains("Pass it as module=SynthFolder (it is a Module), not as folder=SynthFolder.", warning);
        }

        [Fact]
        public void PlacementWarning_OfAnyOtherFailure_GivesTheReasonAndNoParameterAdvice()
        {
            var notFound = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "DestinationNotFound", ["message"] = "No Folder named 'SynthFolder' was found." }
            };

            string warning = ObjectCreateTypes.BuildPlacementWarning("SynthFolder", "Folder", notFound);

            Assert.Contains("NOT in 'SynthFolder': No Folder named 'SynthFolder' was found.", warning);
            Assert.DoesNotContain("Pass it as", warning);
            Assert.Contains("genexus_properties action=move", warning);
        }

        [Fact]
        public void PlacementWarning_WithoutAnAnswerFromTheMove_StillSaysWhereTheObjectIs()
        {
            string warning = ObjectCreateTypes.BuildPlacementWarning("SynthFolder", "Folder", null);

            Assert.StartsWith("The object was created in the Root Module, NOT in 'SynthFolder'.", warning);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void PlacementWarning_WithoutARequestedPlacement_IsNull(string requested)
        {
            Assert.Null(ObjectCreateTypes.BuildPlacementWarning(requested, "Folder", KindMismatch("Folder")));
        }

        [Fact]
        public void CreateObject_GivesTheObjectItsMasterPageDefinitionBeforeTheFirstSave_AndReadsItBackAfterIt()
        {
            // CreateObject needs a live KB, so the order of its steps is pinned by its source.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            int create = source.IndexOf("CreateObjectInstance(createType, name, options, out string seededDescription, out JObject typedMeta);", StringComparison.Ordinal);
            int apply = source.IndexOf("if (masterPage) ApplyMasterPageDefinition(newObj);", StringComparison.Ordinal);
            int save = source.IndexOf("newObj.Save();", apply, StringComparison.Ordinal);
            int verify = source.IndexOf("string notPersisted = VerifyMasterPagePersisted(newObj);", StringComparison.Ordinal);
            int remove = source.IndexOf("return RemoveUnverifiedMasterPage(newObj, name, notPersisted);", StringComparison.Ordinal);

            Assert.True(create > 0, "the SDK object is built from createType (the WebPanel type for a Master Page)");
            Assert.True(apply > create, "the definition is applied after the object exists");
            Assert.True(save > apply, "and before its first save");
            Assert.True(verify > save, "the saved object is read back after the save");
            Assert.True(remove > verify, "and an object that is not a Master Page is deleted again");
            Assert.Contains("Guid typeGuid = ResolveObjectTypeGuid(createType);", source);
        }

        [Fact]
        public void CreateObject_AnswersUnderTheTypeNameOfAMasterPage_WhateverTheSpellingAsked()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("if (masterPage) type = ObjectCreateTypes.MasterPageType;", source);
        }

        [Fact]
        public void TheSavedMasterPage_IsProvedByAFreshRead_AndOnlyAnObjectThatFailsTheProofIsDeletedAgain()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            // The proof: WEB_COMP and the place holder, read from a fresh instance; each failed check answers with its reason.
            Assert.Contains("if (!ObjectCreateTypes.IsMasterWebComp(ReadWebComp(fresh)))", source);
            Assert.Contains("\"The saved object is a \"", source);
            Assert.Contains("not a Master Page.\"", source);
            Assert.Matches(@"form\.IndexOf\(""<contentholder"", StringComparison\.OrdinalIgnoreCase\) < 0\)\s*return ""The saved Master Page does not hold the form's content place holder\."";", source);
            // A read that cannot be made proves nothing and deletes nothing: both ways it can fail (no instance, or an exception)
            // log and answer null, the answer that keeps the object.
            Assert.Matches(@"could not be read back; it is left in place unverified\.""\);\s*return null;", source);
            Assert.Matches(@"could not be verified \(""\s*\+\s*ex\.Message\s*\+\s*""\); it is left in place unverified\.""\);\s*return null;", source);
            // The removal is confirmed (an unconfirmed delete only previews it) and bound to the GUID of the saved object, and says
            // whether it worked.
            Assert.Matches(@"DeleteObject\(name, typeName \?\? ObjectCreateTypes\.MasterPageBaseType, confirm: true,\s*expectedGuid: saved\.Guid\.ToString\(""D""\)\)", source);
            Assert.Contains("code: \"MasterPageNotPersisted\"", source);
        }

        [Fact]
        public void TheDryRunOfAMasterPage_SaysWhatTheCreateBuilds()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("planned[\"creates\"] = \"A WebPanel with WEB_COMP=\"", source);
        }

        [Fact]
        public void ObjectAtomic_RefusesToCreateAMasterPage_BeforeAnyDryRunCanPassForIt()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "AtomicCreateService.cs");

            int refusal = source.IndexOf("if (!exists && ObjectCreateTypes.IsMasterPage(spec.Type))", StringComparison.Ordinal);
            int dryRun = source.IndexOf("if (dryRun)", refusal < 0 ? 0 : refusal, StringComparison.Ordinal);

            Assert.True(refusal > 0, "an object that does not exist is refused when the type is a Master Page");
            Assert.True(dryRun > refusal, "and the refusal comes before the dry run, which would otherwise pass");
            Assert.Contains("message: \"object_atomic does not create a Master Page: \"", source);
        }

        [Fact]
        public void ApplyMasterPageDefinition_SetsWebCompAndTheFormInMemory_AndRefusesWhatTheSdkDidNotAccept()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("PropertyService.ApplyPropertyValue(obj, \"WEB_COMP\", ObjectCreateTypes.MasterPageWebComp, null, obj);", source);
            Assert.Contains("WebFormXmlHelper.ApplyEditableXml(form, ObjectCreateTypes.MasterPageMinimalForm);", source);
            Assert.Contains("if (!ObjectCreateTypes.IsMasterWebComp(webComp))", source);
            Assert.Contains("throw new InvalidOperationException(\"The SDK did not accept WEB_COMP=", source);
        }

        [Fact]
        public void CreateObject_SaysAtTheTopOfTheResultThatTheObjectWasNotPlaced()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("placementWarning = ObjectCreateTypes.BuildPlacementWarning(requestedPlacement, requestedPlacementKind, moveResp);", source);
            Assert.Contains("responsePayload[\"placementApplied\"] = false;", source);
            Assert.Contains("responsePayload[\"warnings\"] = new JArray(placementWarning);", source);
        }
    }
}
