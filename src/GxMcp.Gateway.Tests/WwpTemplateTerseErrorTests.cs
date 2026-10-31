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
        public void ANonTemplateErrorDoesNotCarryTheViolationList()
        {
            var output = McpRouter.TrimErrorEnvelope(JObject.Parse(Refusal.Replace("WwpTemplateInvalid", "PropertyNotFound")), verbose: false);

            Assert.Equal("PropertyNotFound", (string?)output["code"]);
            Assert.Null(output["violations"]);
        }
    }
}
