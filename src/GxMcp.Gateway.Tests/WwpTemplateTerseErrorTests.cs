using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The terse envelope of a WorkWithPlus template write that was refused before anything was saved. The caller
    // corrects the template from the list of failed checks, so that list has to reach it.
    public class WwpTemplateTerseErrorTests
    {
        private const string Refusal = @"{status:'error',target:'SynthTemplate',
            error:{code:'WwpTemplateInvalid',message:'The WorkWithPlus template was not saved. Trn.Panel is not a WorkWithPlus template type.',hint:'Nothing was written.',
            violations:[{check:'TypeUnknown',message:'Trn.Panel is not a WorkWithPlus template type.'},{check:'RootMismatch',message:'The root of a Trn.Selection template must be <selection>.'}]}}";

        [Fact]
        public void TheRefusalKeepsEachFailedCheckInOrder()
        {
            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(Refusal), verbose: false);

            Assert.Equal("WwpTemplateInvalid", (string?)output["code"]);
            var checks = Assert.IsType<JArray>(output["violations"])
                .Select(v => (string?)v["check"])
                .ToArray();
            Assert.Equal(new[] { "TypeUnknown", "RootMismatch" }, checks);
        }

        [Fact]
        public void TheAliasConflictKeepsItsCodeMessageAndHintInTheTerseEnvelope()
        {
            const string conflict = @"{status:'error',target:'SynthTemplate',
                error:{code:'PropertyAliasConflict',message:'TemplateType and WWPTemplate_Type name the same WorkWithPlus template property (WWPTemplate_Type); send only one of them.',
                hint:'Send each property once, under its alias or under its SDK name.'}}";

            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(conflict), verbose: false);

            Assert.Equal("PropertyAliasConflict", (string?)output["code"]);
            Assert.Equal("TemplateType and WWPTemplate_Type name the same WorkWithPlus template property (WWPTemplate_Type); send only one of them.", (string?)output["message"]);
            Assert.Equal("Send each property once, under its alias or under its SDK name.", (string?)output["hint"]);
            Assert.Null(output["violations"]);
        }

        [Fact]
        public void ANonTemplateErrorDoesNotCarryTheViolationList()
        {
            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(Refusal.Replace("WwpTemplateInvalid", "PropertyNotFound")), verbose: false);

            Assert.Equal("PropertyNotFound", (string?)output["code"]);
            Assert.Null(output["violations"]);
        }

        [Fact]
        public void ATemplatePatchThatFindsNothingKeepsItsNextStepInsteadOfTheNearMatchInspection()
        {
            const string noMatch = @"{status:'error',target:'SynthTemplate',
                error:{code:'NoMatch',message:'The find text was not found in TemplateXml. Nothing was written.'},
                suggested_next_step:{action:'read_template_xml',hint:'genexus_properties action=get propertyName=TemplateXml'}}";

            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(noMatch), verbose: false);

            Assert.Equal("NoMatch", (string?)output["code"]);
            Assert.Equal("read_template_xml", (string?)output["suggested_next_step"]?["action"]);
        }

        [Fact]
        public void ANoMatchOfAnEditPatchStillGetsTheNearMatchInspection()
        {
            const string editNoMatch = @"{status:'error',target:'SynthSource',error:{code:'NoMatch',message:'Patch NoMatch.'}}";

            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(editNoMatch), verbose: false);

            Assert.Equal("inspect_near_match", (string?)output["suggested_next_step"]?["action"]);
        }
    }
}
