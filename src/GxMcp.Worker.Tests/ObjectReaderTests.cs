using System;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ObjectReaderTests
    {
        [Theory]
        [InlineData("guid")]
        [InlineData("entityKey")]
        [InlineData("target")]
        public void UntypedIdentitiesBypassCacheEvenForSource(string selector)
        {
            var request = new ObjectReadRequest { PartName = "Source" };
            if (selector == "guid") request.Guid = "11111111-1111-1111-1111-111111111111";
            if (selector == "entityKey") request.EntityKey = "11111111-1111-1111-1111-111111111111-1";
            if (selector == "target") request.Target = "11111111-1111-1111-1111-111111111111";
            Assert.True(ObjectReader.IsPatternSettingsRead(request));
            request.PartName = null;
            Assert.True(ObjectReader.IsPatternSettingsRead(request));
            request.TypeFilter = "Procedure";
            Assert.False(ObjectReader.IsPatternSettingsRead(request));
        }

        [Fact]
        public void SettingsPagesNeverUseWorkerReadCache()
        {
            Assert.True(ObjectReader.IsPatternSettingsRead(new ObjectReadRequest { TypeFilter = "Pattern Settings" }));
            Assert.True(ObjectReader.IsPatternSettingsRead(new ObjectReadRequest { PartName = "PatternSettings" }));
            Assert.True(ObjectReader.IsPatternSettingsRead(new ObjectReadRequest { Target = "PatternSettings:WorkWithPlus" }));
            Assert.True(ObjectReader.IsPatternSettingsRead(new ObjectReadRequest { RequestedParts = new[] { "Rules", "PatternSettings" } }));
            Assert.False(ObjectReader.IsPatternSettingsRead(new ObjectReadRequest { TypeFilter = "Procedure", PartName = "Source" }));
        }

        [Fact]
        public void CacheSeparatesHomonymsAndResponseShapes()
        {
            var request = new ObjectReadRequest { Target = "WorkWithPlus", TypeFilter = "Module" };
            string moduleKey = ObjectReader.BuildCacheKey(request);
            request.TypeFilter = "Pattern Settings";
            string settingsKey = ObjectReader.BuildCacheKey(request);
            Assert.NotEqual(moduleKey, settingsKey);
            request.FullObject = true;
            string fullKey = ObjectReader.BuildCacheKey(request);
            Assert.NotEqual(settingsKey, fullKey);
            request.FullObject = false;
            request.RequestedParts = new[] { "Rules", "Events" };
            string partsKey = ObjectReader.BuildCacheKey(request);
            Assert.NotEqual(settingsKey, partsKey);
            request.RequestedParts = new[] { "Rules", "Source" };
            Assert.NotEqual(partsKey, ObjectReader.BuildCacheKey(request));
        }

        [Fact]
        public void ObjectReader_RejectsNullOrEmptyTarget()
        {
            var reader = new ObjectReader(null);
            string result = reader.Read(new ObjectReadRequest { Target = "" });

            var json = JObject.Parse(result);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("MissingTarget", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ObjectReader_RejectsNullRequest()
        {
            var reader = new ObjectReader(null);
            string result = reader.Read(null);

            var json = JObject.Parse(result);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("InvalidRequest", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ObjectReader_Invalidate_RemovesCachedKeys()
        {
            var reader = new ObjectReader(null);
            reader.Invalidate("CustomerTransaction", "Rules");

            bool cached = reader.TryGetCached("CustomerTransaction", "Rules", null, null, "mcp", out _);
            Assert.False(cached);
        }
    }
}
