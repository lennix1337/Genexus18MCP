using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class TerseDeleteRefusalTests
    {
        // The envelope ObjectService.DeleteNotSupportedForTypeResponse returns for a WorkWithPlus template.
        // The Worker test ObjectServiceDeleteRefusalTests pins its shape; keep the two in step.
        private const string TemplateRefusal =
            "{\"status\":\"error\",\"target\":\"SynthTemplate\"," +
            "\"error\":{\"code\":\"DeleteNotSupportedForType\"," +
            "\"message\":\"WorkWithPlus templates cannot be deleted with genexus_delete_object: the SDK refuses them, and only the WorkWithPlus Settings Editor deletes them. Nothing was deleted.\"," +
            "\"hint\":\"Delete the template in the IDE with the WorkWithPlus Settings Editor (Settings > Templates). genexus_delete_object does not remove WorkWithPlus templates.\"}," +
            "\"type\":\"WorkWithPlus for Web Template\",\"wouldDelete\":false,\"persisted\":false}";

        [Fact]
        public void Terse_refusal_keeps_wouldDelete_false_so_the_caller_sees_the_object_stays()
        {
            var trimmed = McpRouter.TrimErrorEnvelope(JObject.Parse(TemplateRefusal), verbose: false);

            Assert.Equal("DeleteNotSupportedForType", trimmed["code"]?.ToString());
            Assert.False(trimmed["wouldDelete"]?.ToObject<bool>());
            Assert.False(trimmed["persisted"]?.ToObject<bool>());
            Assert.Contains("Settings Editor", trimmed["hint"]?.ToString());
        }
    }
}
