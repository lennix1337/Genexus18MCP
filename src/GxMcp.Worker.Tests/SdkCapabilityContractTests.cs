using System;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class SdkCapabilityContractTests
    {
        [Fact]
        public void CapabilityProbeIsHonestAboutPersistenceEvidence()
        {
            var response = JObject.Parse(new SdkProbeService().Capabilities());

            Assert.Equal("genexus-sdk-capabilities/1", response["schemaVersion"]?.ToString());
            var capabilities = response["capabilities"] as JArray;
            Assert.NotNull(capabilities);
            Assert.Contains(capabilities.Values<JObject>(), item => item["capability"]?.ToString() == "authoring.transaction");
            Assert.Contains(capabilities.Values<JObject>(), item => item["status"]?.ToString() == "deferred");
            foreach (var capability in capabilities.Values<JObject>())
            {
                Assert.Contains(capability["status"]?.ToString(), new[] { "available_unverified", "unavailable", "deferred" });
                Assert.False(capability["evidence"]?["persistenceVerified"]?.Value<bool>() ?? true);
            }
        }
    }
}
