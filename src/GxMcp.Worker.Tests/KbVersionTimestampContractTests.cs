using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class KbVersionTimestampContractTests
    {
        [Fact]
        public void CreationTimestampMetadata_IsExplicitlyUnavailable()
        {
            var result = new JObject
            {
                ["lastUpdate"] = "2026-09-10T12:00:00.0000000Z",
                ["lastUpdateSource"] = "sdk:KBVersion.LastUpdate"
            };

            KbVersionService.AddCreationTimestampMetadata(result);

            Assert.True(result.ContainsKey("createdAt"));
            Assert.Equal(JTokenType.Null, result["createdAt"]?.Type);
            Assert.False((bool)result["createdAtAvailable"]);
            Assert.Equal("unavailable:sdk-KBVersion", result["createdAtSource"]?.ToString());
            Assert.Contains("does not expose", result["createdAtNote"]?.ToString());
            Assert.Equal("2026-09-10T12:00:00.0000000Z", result["lastUpdate"]?.ToString());
            Assert.Equal("sdk:KBVersion.LastUpdate", result["lastUpdateSource"]?.ToString());
        }
    }
}
