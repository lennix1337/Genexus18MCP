using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class WwpTemplateCheckTests
    {
        [Theory]
        [InlineData("Component", "component")]
        [InlineData("Regular", "template")]
        [InlineData("Report", "template")]
        [InlineData("Wizard", "template")]
        [InlineData("WizardStep", "step")]
        [InlineData("Trn.Selection", "selection")]
        [InlineData("Trn.Transaction", "transaction")]
        [InlineData("Trn.View", "view")]
        [InlineData("Trn.Association", "association")]
        [InlineData("Trn.AssociationSelection", "associationSelection")]
        [InlineData("Trn.Prompt", "prompt")]
        [InlineData("Trn.PromptMultiple", "promptMultiple")]
        public void ATemplateWithTheRootOfItsTypeIsValid(string type, string root)
        {
            Assert.Empty(WwpTemplatePropertyRules.Check(type, $"<{root} />"));
        }

        [Fact]
        public void TheNameAttributeOfTheRootIsNotRequired()
        {
            Assert.Empty(WwpTemplatePropertyRules.Check("Trn.Selection", "<selection />"));
            Assert.Empty(WwpTemplatePropertyRules.Check("Trn.Selection", "<selection Name=\"SynthTemplate\" />"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void AnEmptyTypeIsRefused(string type)
        {
            Assert.Equal(new[] { "TypeEmpty" }, Checks(WwpTemplatePropertyRules.Check(type, "<selection />")));
        }

        [Fact]
        public void AStoredTypeThatCannotBeReadIsRefusedWithoutCallingItEmpty()
        {
            JToken only = Assert.Single(WwpTemplatePropertyRules.Check(null, "<selection />"));

            Assert.Equal("TypeNotRead", only["check"]?.ToString());
            Assert.Contains("could not be read", only["message"]?.ToString());
            Assert.DoesNotContain("empty", only["message"]?.ToString());
        }

        [Fact]
        public void AnUnknownTypeIsRefusedAndTheMessageNamesTheKnownTypes()
        {
            JToken only = Assert.Single(WwpTemplatePropertyRules.Check("Trn.Panel", "<selection />"));

            Assert.Equal("TypeUnknown", only["check"]?.ToString());
            Assert.Contains("Trn.Selection", only["message"]?.ToString());
        }

        [Fact]
        public void TheTypeIsMatchedExactlyNotWithoutCase()
        {
            Assert.Equal(new[] { "TypeUnknown" }, Checks(WwpTemplatePropertyRules.Check("trn.selection", "<selection />")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        public void AnEmptyXmlIsRefused(string xml)
        {
            Assert.Equal(new[] { "XmlEmpty" }, Checks(WwpTemplatePropertyRules.Check("Trn.Selection", xml)));
        }

        [Fact]
        public void StoredXmlThatCannotBeReadIsRefusedWithoutCallingItEmpty()
        {
            JToken only = Assert.Single(WwpTemplatePropertyRules.Check("Trn.Selection", null));

            Assert.Equal("XmlNotRead", only["check"]?.ToString());
            Assert.Contains("could not be read", only["message"]?.ToString());
            Assert.DoesNotContain("empty", only["message"]?.ToString());
        }

        [Fact]
        public void MalformedXmlIsRefused()
        {
            Assert.Equal(new[] { "XmlMalformed" }, Checks(WwpTemplatePropertyRules.Check("Trn.Selection", "<selection><table></selection>")));
        }

        [Fact]
        public void XmlWithADocumentTypeIsRefusedBecauseDtdsAreNotProcessed()
        {
            const string withDtd = "<!DOCTYPE selection [<!ENTITY synth \"value\">]><selection />";

            JToken only = Assert.Single(WwpTemplatePropertyRules.Check("Trn.Selection", withDtd));

            Assert.Equal("XmlDtd", only["check"]?.ToString());
            Assert.Contains("DTDs are not accepted", only["message"]?.ToString());
            Assert.DoesNotContain("DtdProcessing", only["message"]?.ToString());
        }

        [Fact]
        public void ARootOfAnotherTypeIsRefusedAndTheMessageNamesTheExpectedRoot()
        {
            JToken only = Assert.Single(WwpTemplatePropertyRules.Check("Trn.Selection", "<transaction />"));

            Assert.Equal("RootMismatch", only["check"]?.ToString());
            Assert.Contains("<selection>", only["message"]?.ToString());
        }

        [Fact]
        public void AnUnknownTypeIsNotCheckedForItsRoot()
        {
            Assert.Equal(new[] { "TypeUnknown" }, Checks(WwpTemplatePropertyRules.Check("Trn.Panel", "<anything />")));
        }

        [Fact]
        public void EveryFailedCheckIsReported()
        {
            Assert.Equal(new[] { "TypeNotRead", "XmlMalformed" }, Checks(WwpTemplatePropertyRules.Check(null, "<selection>")));
        }

        [Fact]
        public void TheStoredValueStandsForTheOneTheCallDoesNotSend()
        {
            var sent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WWPTemplate_TemplateXml"] = "<transaction />" };

            (string type, string xml) = WwpTemplatePropertyRules.FinalState(sent,
                name => name == "WWPTemplate_Type" ? "Trn.Transaction" : "<selection />");

            Assert.Equal("Trn.Transaction", type);
            Assert.Equal("<transaction />", xml);
        }

        [Fact]
        public void TheSentValueWinsOverTheStoredOne()
        {
            var sent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WWPTemplate_Type"] = "Trn.View" };

            (string type, string xml) = WwpTemplatePropertyRules.FinalState(sent, name => "stored " + name);

            Assert.Equal("Trn.View", type);
            Assert.Equal("stored WWPTemplate_TemplateXml", xml);
        }

        [Fact]
        public void TheSentKeysAreMatchedWithoutCase()
        {
            var sent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["wwptemplate_type"] = "Trn.View" };

            (string type, _) = WwpTemplatePropertyRules.FinalState(sent, name => "stored " + name);

            Assert.Equal("Trn.View", type);
        }

        [Theory]
        [InlineData("WWPTemplate_Type", true)]
        [InlineData("wwptemplate_templatexml", true)]
        [InlineData("WWPTemplate_Category", false)]
        [InlineData("Description", false)]
        public void OnlyTheTypeAndTheXmlAreReadBackBeforeAWrite(string name, bool expected)
        {
            Assert.Equal(expected, WwpTemplatePropertyRules.TouchesFinalState(name));
        }

        private static string[] Checks(JArray violations) => violations.Select(v => v["check"]?.ToString()).ToArray();
    }
}
