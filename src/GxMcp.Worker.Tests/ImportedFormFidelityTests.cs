using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The SDK exports a form without the ids of its layout containers and with the images and variables of a control by
    /// name (a Web Master Panel is exported this way), and it generates the ids and resolves the references to local keys when
    /// it stores the imported form. The check that follows an XPZ import must not read that normalization as a lost write: it
    /// used to, and then deleted the object it had just imported. Every other difference, an id that differs or that the
    /// import dropped, and a reference that the SDK did not resolve, must still fail.
    /// </summary>
    public class ImportedFormFidelityTests
    {
        private const string SynthGuid = "11111111-1111-1111-1111-111111111111";
        private const string ImageType = "9fb193d9-64a4-4d30-b129-ff7c76830f7e";

        // A Web Master Panel as the SDK exports it: no ids on the layout containers, theme classes by name.
        private const string ExportedMasterForm =
            "<GxMultiForm rootId=\"2\" version=\"html:15.0.0;layout:17.11.0\"><Form id=\"2\" type=\"layout\"><detail><layout>" +
            "<table controlName=\"MainTable\" class=\"ContainerFluid PageTable\" tableType=\"Responsive\"><row><cell>" +
            "<contentholder controlName=\"ContentHolder\" /></cell></row></table></layout></detail></Form></GxMultiForm>";

        // The same form as the KB reads it back after the import: ids generated, theme classes resolved to <guid>-<int>.
        private const string ImportedMasterForm =
            "<GxMultiForm rootId=\"2\" version=\"html:15.0.0;layout:17.11.0\">\r\n" +
            "  <Form id=\"2\" type=\"layout\">\r\n" +
            "    <detail>\r\n" +
            "      <layout id=\"11111111-1111-1111-1111-111111111111\">\r\n" +
            "        <table id=\"22222222-2222-2222-2222-222222222222\" controlName=\"MainTable\" class=\"d52f9833-90c9-4096-8e42-27485741f78a-443 d52f9833-90c9-4096-8e42-27485741f78a-179\" tableType=\"Responsive\">\r\n" +
            "          <row id=\"33333333-3333-3333-3333-333333333333\">\r\n" +
            "            <cell id=\"44444444-4444-4444-4444-444444444444\">\r\n" +
            "              <contentholder controlName=\"ContentHolder\" />\r\n" +
            "            </cell>\r\n" +
            "          </row>\r\n" +
            "        </table>\r\n" +
            "      </layout>\r\n" +
            "    </detail>\r\n" +
            "  </Form>\r\n" +
            "</GxMultiForm>";

        private static string Table(string tableAttributes) =>
            "<GxMultiForm rootId=\"2\"><Form id=\"2\" type=\"layout\"><detail><layout><table " + tableAttributes + "><row><cell>" +
            "<contentholder controlName=\"ContentHolder\" /></cell></row></table></layout></detail></Form></GxMultiForm>";

        // The payload of a user control's custom properties, as its own XML (so the variable's ampersand is escaped inside it).
        private static string Payload(string itemsValue) =>
            new XElement("Properties",
                new XElement("Property", new XElement("Name", "ControlName"), new XElement("Value", "SynthMenu")),
                new XElement("Property", new XElement("Name", "Items"), new XElement("Value", itemsValue))).ToString(SaveOptions.DisableFormatting);

        // A master with an image and a user control bound to a variable. In the package they are named; with ids and keys the KB
        // holds the resolved form.
        private static string RichForm(string imageReference, string itemsValue, bool generatedIds)
        {
            var cell = new XElement("cell",
                new XElement("image", new XAttribute("controlName", "Logo"), new XAttribute("image", imageReference)),
                new XElement("ucw", new XAttribute("controlName", "SynthMenu"),
                    new XAttribute(CustomPropertiesPayload.AttributeName, Payload(itemsValue))),
                new XElement("contentholder", new XAttribute("controlName", "ContentHolder")));
            var row = new XElement("row", cell);
            var table = new XElement("table", new XAttribute("controlName", "MainTable"), new XAttribute("tableType", "Responsive"), row);
            var layout = new XElement("layout", table);
            if (generatedIds)
            {
                layout.Add(new XAttribute("id", SynthGuid));
                table.Add(new XAttribute("id", "22222222-2222-2222-2222-222222222222"));
                row.Add(new XAttribute("id", "33333333-3333-3333-3333-333333333333"));
                cell.Add(new XAttribute("id", "44444444-4444-4444-4444-444444444444"));
            }
            return new XElement("GxMultiForm", new XAttribute("rootId", "2"),
                new XElement("Form", new XAttribute("id", "2"), new XAttribute("type", "layout"), new XElement("detail", layout)))
                .ToString(SaveOptions.DisableFormatting);
        }

        private static string NamedImage(string name) => ImageType + "-" + name;

        [Fact]
        public void ImportedWebFormMatches_AcceptsTheIdsTheSdkGeneratedForAMasterPageForm()
        {
            bool matches = TransferService.ImportedWebFormMatches(ExportedMasterForm, ImportedMasterForm, out string diff);

            Assert.True(matches, diff);
        }

        [Fact]
        public void StrictComparison_StillReadsTheGeneratedIdsAsADifference()
        {
            bool matches = XmlEquivalence.AreEquivalent(ExportedMasterForm, ImportedMasterForm, out string diff);

            Assert.False(matches);
            Assert.Contains("Attribute count differs", diff);
            Assert.Contains("[id]", diff);
        }

        [Fact]
        public void ImportedWebFormMatches_StillRejectsADifferentIdWhereTheImportedFormAndThePackageBothCarryOne()
        {
            string expected = Table("id=\"147358da\" controlName=\"MainTable\"");
            string imported = Table("id=\"99999999\" controlName=\"MainTable\"");

            Assert.False(TransferService.ImportedWebFormMatches(expected, imported, out _));
        }

        [Fact]
        public void ImportVerification_ComparesTheFirstReadAndTheRepairWithTheImportedFormRule()
        {
            // VerifyImportedWebForms needs a live KB, so the two comparisons it makes are pinned by their source.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "TransferService.cs");

            Assert.Contains("if (ImportedWebFormMatches(expected.ExpectedXml, actualXml, out diff)) continue;", source);
            Assert.Contains("repairedHere = ImportedWebFormMatches(expected.ExpectedXml, repairedXml, out repairedDiff);", source);
            Assert.DoesNotContain("XmlEquivalence.AreEquivalent(expected.ExpectedXml", source);
        }

        [Fact]
        public void ImportedForm_ToleratesTheGeneratedIdsAlongsideTheImplicitWorkWithPlusParametersNode()
        {
            // The check for the empty Parameters node WorkWithPlus adds to a web component runs only when the plain comparison
            // fails, and it must apply the same rule about ids or a form with both normalizations would be rejected.
            const string expected =
                "<GxMultiForm rootId=\"2\"><Form id=\"2\" type=\"layout\"><detail><layout><table controlName=\"MainTable\"><row><cell>" +
                "<webComponent controlName=\"Part\" /></cell></row></table></layout></detail></Form></GxMultiForm>";
            const string persisted =
                "<GxMultiForm rootId=\"2\"><Form id=\"2\" type=\"layout\"><detail><layout id=\"L1\"><table id=\"T1\" controlName=\"MainTable\"><row id=\"R1\"><cell id=\"C1\">" +
                "<webComponent controlName=\"Part\"><parameters /></webComponent></cell></row></table></layout></detail></Form></GxMultiForm>";

            Assert.True(XmlEquivalence.AreEquivalent(expected, persisted, XmlEquivalenceOptions.ImportedForm, out string diff), diff);
        }

        [Fact]
        public void ImportedForm_StillRejectsADifferentIdWhereBothSidesCarryOne()
        {
            string expected = Table("id=\"147358da\" controlName=\"MainTable\"");
            string persisted = Table("id=\"99999999\" controlName=\"MainTable\"");

            bool matches = XmlEquivalence.AreEquivalent(expected, persisted, XmlEquivalenceOptions.ImportedForm, out string diff);

            Assert.False(matches);
            Assert.Contains("Attribute 'id' differs", diff);
        }

        [Fact]
        public void ImportedForm_AcceptsTheSameIdOnBothSides_AndStillComparesTheRest()
        {
            // Two separate strings: the same instance on both sides is short-circuited before any comparison.
            string expected = Table("id=\"147358da\" controlName=\"MainTable\"");
            string persisted = Table("id=\"147358da\" controlName=\"MainTable\"");
            string persistedWithAnotherName = Table("id=\"147358da\" controlName=\"OtherTable\"");

            Assert.True(XmlEquivalence.AreEquivalent(expected, persisted, XmlEquivalenceOptions.ImportedForm, out string diff), diff);
            Assert.False(XmlEquivalence.AreEquivalent(expected, persistedWithAnotherName, XmlEquivalenceOptions.ImportedForm, out _));
        }

        [Fact]
        public void ImportedForm_StillRejectsAnIdTheImportDropped()
        {
            string expected = Table("id=\"147358da\" controlName=\"MainTable\"");
            string persisted = Table("controlName=\"MainTable\"");

            Assert.False(XmlEquivalence.AreEquivalent(expected, persisted, XmlEquivalenceOptions.ImportedForm, out _));
        }

        [Fact]
        public void ImportedForm_OnlyToleratesGeneratedIdsOnTheImportedSide()
        {
            // The first document is the package's form and the second the imported one; the other way round the ids belong
            // to the package, which the import then lost.
            Assert.False(XmlEquivalence.AreEquivalent(ImportedMasterForm, ExportedMasterForm, XmlEquivalenceOptions.ImportedForm, out _));
        }

        [Fact]
        public void ImportedForm_StillRejectsAControlTheImportDropped()
        {
            string imported = ImportedMasterForm.Replace("<contentholder controlName=\"ContentHolder\" />", string.Empty);

            bool matches = TransferService.ImportedWebFormMatches(ExportedMasterForm, imported, out string diff);

            Assert.False(matches);
            Assert.Contains("contentholder", diff);
        }

        [Fact]
        public void ImportedForm_StillRejectsAnotherAttributeDifference()
        {
            string imported = ImportedMasterForm.Replace("tableType=\"Responsive\"", "tableType=\"Fixed\"");

            Assert.False(TransferService.ImportedWebFormMatches(ExportedMasterForm, imported, out _));
        }

        [Fact]
        public void ImportedForm_StillRejectsAnAttributeTheSdkAddedBesidesTheId()
        {
            string imported = ImportedMasterForm.Replace("controlName=\"MainTable\"", "controlName=\"MainTable\" invented=\"1\"");

            Assert.False(TransferService.ImportedWebFormMatches(ExportedMasterForm, imported, out _));
        }

        // ---- the images and variables the package names and the SDK stores by key ----------------------------------------

        [Fact]
        public void ImportedWebFormMatches_AcceptsAMasterWithAnImageAndAVariableTheSdkResolvedToKeys()
        {
            string package = RichForm(NamedImage("SynthModule.synth_logo"), "&synthItems", generatedIds: false);
            string stored = RichForm(ImageType + "-74", "var:5", generatedIds: true);

            bool matches = TransferService.ImportedWebFormMatches(package, stored, out string diff);

            Assert.True(matches, diff);
        }

        [Fact]
        public void TheSameMasterIsRejectedByEveryOtherComparison_SoOnlyTheImportToleratesTheResolvedReferences()
        {
            string package = RichForm(NamedImage("SynthModule.synth_logo"), "&synthItems", generatedIds: false);
            string stored = RichForm(ImageType + "-74", "var:5", generatedIds: true);

            Assert.False(XmlEquivalence.AreEquivalent(package, stored, out _));
            Assert.False(XmlEquivalence.AreEquivalent(package, stored, XmlEquivalenceOptions.Layout, out string diff));
            Assert.Contains("image", diff);
        }

        [Fact]
        public void EveryOtherComparison_StillRejectsAVariableNamedInACustomProperty_WhenItIsTheOnlyDifference()
        {
            // The image is the same on both sides, so the variable is the only thing that tells the two forms apart.
            string package = RichForm(ImageType + "-74", "&synthItems", generatedIds: false);
            string stored = RichForm(ImageType + "-74", "var:5", generatedIds: false);

            Assert.False(XmlEquivalence.AreEquivalent(package, stored, out _));
            Assert.False(XmlEquivalence.AreEquivalent(package, stored, XmlEquivalenceOptions.Layout, out string diff));
            Assert.Contains(CustomPropertiesPayload.AttributeName, diff);
            Assert.True(XmlEquivalence.AreEquivalent(package, stored, XmlEquivalenceOptions.ImportedForm, out string importedDiff), importedDiff);
        }

        [Fact]
        public void ImportedForm_AcceptsAnImageAndAVariableHeldByKey_EvenWhenTheStoredFormGeneratedNoId()
        {
            string package = RichForm(NamedImage("SynthLogo"), "&synthItems", generatedIds: false);
            string storedWithKeysOnly = RichForm(ImageType + "-74", "var:5", generatedIds: false);

            Assert.True(XmlEquivalence.AreEquivalent(package, storedWithKeysOnly, XmlEquivalenceOptions.ImportedForm, out string diff), diff);
        }

        [Theory]
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-SynthModule.synth_logo", "00000000-64a4-4d30-b129-ff7c76830f7e-74")]   // another object type
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-SynthModule.synth_logo", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-OtherModule.other")] // still a name
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-46", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-74")]   // two different keys
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-74", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-SynthModule.synth_logo")] // reversed: a name in the stored form
        [InlineData("synth_logo", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-74")]                                        // not a reference
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-Synth Logo", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-74")]  // not an object name
        public void ImportedForm_StillRejectsAnImageThatIsNotTheSameObject(string packageImage, string storedImage)
        {
            string package = RichForm(packageImage, "&synthItems", generatedIds: false);
            string stored = RichForm(storedImage, "var:5", generatedIds: true);

            Assert.False(TransferService.ImportedWebFormMatches(package, stored, out _));
        }

        [Fact]
        public void ImportedForm_DoesNotApplyTheImageRuleToAnotherAttribute()
        {
            string named = "<GxMultiForm><Form id=\"2\"><detail><layout><textblock caption=\"" + NamedImage("SynthModule.synth_logo") + "\" /></layout></detail></Form></GxMultiForm>";
            string keyed = "<GxMultiForm><Form id=\"2\"><detail><layout><textblock caption=\"" + ImageType + "-74\" /></layout></detail></Form></GxMultiForm>";

            Assert.False(XmlEquivalence.AreEquivalent(named, keyed, XmlEquivalenceOptions.ImportedForm, out _));
        }

        [Fact]
        public void ImportedForm_AcceptsAVariableNamedInACustomPropertyAndHeldByKey()
        {
            string package = RichForm(NamedImage("SynthLogo"), "&synthItems", generatedIds: false);
            string stored = RichForm(NamedImage("SynthLogo"), "var:5", generatedIds: false);

            Assert.True(XmlEquivalence.AreEquivalent(package, stored, XmlEquivalenceOptions.ImportedForm, out string diff), diff);
        }

        [Theory]
        [InlineData("&synthItems", "plain text")]      // not a key
        [InlineData("&synthItems", "var:five")]        // not a key
        [InlineData("plain text", "var:5")]            // not a name
        [InlineData("var:5", "&synthItems")]           // reversed: a name in the stored form
        [InlineData("var:5", "var:6")]                 // two keys
        public void ImportedForm_StillRejectsACustomPropertyThatReallyDiffers(string packageValue, string storedValue)
        {
            string package = RichForm(NamedImage("SynthLogo"), packageValue, generatedIds: false);
            string stored = RichForm(NamedImage("SynthLogo"), storedValue, generatedIds: false);

            Assert.False(XmlEquivalence.AreEquivalent(package, stored, XmlEquivalenceOptions.ImportedForm, out _));
        }

        [Theory]
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-SynthModule.synth_logo", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-74", true)]
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-synth_logo", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-1", true)]
        [InlineData("9FB193D9-64A4-4D30-B129-FF7C76830F7E-synth_logo", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-1", true)]
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-74", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-74", false)]
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-74", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-synth_logo", false)]
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-a..b", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-1", false)]
        [InlineData("", "9fb193d9-64a4-4d30-b129-ff7c76830f7e-1", false)]
        [InlineData("9fb193d9-64a4-4d30-b129-ff7c76830f7e-synth_logo", null, false)]
        public void IsNamedVsKeyedReferencePair_AcceptsOnlyANameInTheFirstAndTheResolvedKeyInTheSecond(string named, string keyed, bool expected)
        {
            Assert.Equal(expected, XmlEquivalence.IsNamedVsKeyedReferencePair(named, keyed));
        }

        [Theory]
        [InlineData("&synthItems", "var:5", true)]
        [InlineData("&synthItems", "att:15", false)]
        [InlineData("var:5", "&synthItems", false)]
        [InlineData("&synthItems", "var:x", false)]
        [InlineData("synthItems", "var:5", false)]
        [InlineData("", "var:5", false)]
        [InlineData("&synthItems", null, false)]
        public void IsVariableNamedVsKeyed_AcceptsOnlyAVariableNameInTheFirstAndItsKeyInTheSecond(string named, string keyed, bool expected)
        {
            Assert.Equal(expected, XmlEquivalence.IsVariableNamedVsKeyed(named, keyed));
        }
    }
}
