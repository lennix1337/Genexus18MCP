using System;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class ObjectServiceSourceCacheTests
    {
        [Fact]
        public void FullMcpReadSeedsRawSourceCache()
        {
            var guid = Guid.NewGuid();
            var payload = new JObject
            {
                ["source"] = "parm(&CustomerId);"
            };

            Assert.True(ObjectService.CacheRawSourceFromReadPayload(
                guid, "Source", payload, offset: null, client: "mcp", minimize: false));

            var service = new GxMcp.Worker.Services.ObjectService(null, null);
            Assert.True(service.TryGetPartSourceRaw(guid.ToString(), "Source", out string source));
            Assert.Equal("parm(&CustomerId);", source);
        }

        [Fact]
        public void PaginatedReadDoesNotSeedRawSourceCache()
        {
            var guid = Guid.NewGuid();
            var payload = new JObject
            {
                ["source"] = "parm(&CustomerId);",
                ["truncated"] = true,
                ["isTruncatedByWorker"] = true
            };

            Assert.False(ObjectService.CacheRawSourceFromReadPayload(
                guid, "Source", payload, offset: null, client: "mcp", minimize: false));

            var service = new GxMcp.Worker.Services.ObjectService(null, null);
            Assert.False(service.TryGetPartSourceRaw(guid.ToString(), "Source", out _));
        }

        [Fact]
        public void FullJsonReadCacheCanServeRawSourceProbe()
        {
            var guid = Guid.NewGuid();
            var payload = new JObject
            {
                ["source"] = "parm(&CustomerId);"
            }.ToString(Newtonsoft.Json.Formatting.None);

            var cache = (IDictionary)typeof(ObjectService)
                .GetField("_readCache", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);
            Type entryType = cache.GetType().GetGenericArguments()[1];
            object entry = Activator.CreateInstance(entryType, nonPublic: true);
            entryType.GetProperty("Payload").SetValue(entry, payload);
            entryType.GetProperty("UpdatedUtc").SetValue(entry, DateTime.UtcNow);
            cache[ObjectService.BuildReadCacheKey(guid, "Source", null, 0, "mcp", false)] = entry;

            var service = new GxMcp.Worker.Services.ObjectService(null, null);
            Assert.True(service.TryGetPartSourceRaw(guid.ToString(), "Source", out string source));
            Assert.Equal("parm(&CustomerId);", source);
        }
    }
}