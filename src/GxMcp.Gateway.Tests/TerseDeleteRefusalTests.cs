using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class TerseDeleteRefusalTests
    {
        // The envelope ObjectService.DeleteNotSupportedForTypeResponse returns for a WorkWithPlus template. The Worker
        // test ObjectServiceDeleteRefusalTests pins the same message and hint, so the two literals stay in step.
        private const string Message = "WorkWithPlus templates cannot be deleted with genexus_delete_object. Delete them with the WorkWithPlus Settings Editor. Nothing was deleted.";
        private const string Hint = "Delete the template with the WorkWithPlus Settings Editor in the IDE. genexus_delete_object does not remove WorkWithPlus templates.";

        private const string TemplateRefusal =
            "{\"status\":\"error\",\"target\":\"SynthTemplate\"," +
            "\"error\":{\"code\":\"DeleteNotSupportedForType\"," +
            "\"message\":\"" + Message + "\"," +
            "\"hint\":\"" + Hint + "\"}," +
            "\"type\":\"WorkWithPlus for Web Template\",\"wouldDelete\":false,\"persisted\":false}";

        [Fact]
        public void Terse_refusal_keeps_the_code_the_reason_and_wouldDelete_false()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(JObject.Parse(TemplateRefusal), verbose: false);

            Assert.Equal("DeleteNotSupportedForType", trimmed["code"]?.ToString());
            Assert.Equal(Message, trimmed["message"]?.ToString());
            Assert.Equal(Hint, trimmed["hint"]?.ToString());
            Assert.Equal("SynthTemplate", trimmed["target"]?.ToString());
            Assert.Equal("WorkWithPlus for Web Template", trimmed["type"]?.ToString());
            Assert.False(trimmed["wouldDelete"]?.ToObject<bool>());
            Assert.False(trimmed["persisted"]?.ToObject<bool>());
        }
    }
}
