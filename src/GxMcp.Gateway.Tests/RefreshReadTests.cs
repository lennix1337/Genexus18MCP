using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class RefreshReadTests
    {
        // Issue #830: the read help must say which copy of the text a caller receives, so a buffer is not read as persisted.
        [Theory]
        [InlineData("readFreshness.textSource")]
        [InlineData("in-memory-buffer")]
        [InlineData("persisted-model")]
        public void GenexusReadHelpDocumentsEachTextSourceLabel(string term) =>
            Assert.Contains(term, ToolHelpCatalog.Get("genexus_read") ?? string.Empty);

        [Fact]
        public void GatewayReplayAgeIsAddedToWorkerReadAge()
        {
            var result = new JObject
            {
                ["content"] = new JArray(new JObject
                {
                    ["type"] = "text",
                    ["text"] = new JObject
                    {
                        ["readFreshness"] = new JObject { ["origin"] = "sdk-object-state", ["ageMs"] = 12 }
                    }.ToString()
                })
            };

            Program.AttachGatewaySemanticCacheAge(result, 30);

            Assert.Equal(42, JObject.Parse(result["content"]![0]!["text"]!.ToString())["readFreshness"]?["ageMs"]?.Value<long>());
            Assert.Equal(42, result["_meta"]?["readFreshnessAgeMs"]?.Value<long>());
        }

        [Fact]
        public void RefreshFencesGatewayCacheFillsThatRaceWithTheWorkerRefresh()
        {
            long now = 0;
            var cache = new SemanticCacheStore(16, System.TimeSpan.FromMinutes(30), () => now);
            var refreshArgs = new JObject { ["name"] = "SyntheticReport", ["refresh"] = true };
            cache.Set("kb|genexus_read:stale-alias", new JObject { ["source"] = "old" });

            Assert.True(Program.InvalidateRefreshReadScope(cache, "kb", "genexus_read", refreshArgs));
            Assert.False(cache.TryGet("kb|genexus_read:stale-alias", out _));

            long revisionDuringRefresh = cache.GetRevision("kb");
            var ordinaryArgs = new JObject { ["name"] = "SyntheticReport" };
            string inFlightKey = Program.CreateSemanticCacheKey(
                "kb", "genexus_read", ordinaryArgs, false, false,
                revisionDuringRefresh, null, null)!;
            cache.Set(inFlightKey, new JObject { ["source"] = "old-worker-cache" });
            Assert.True(cache.TryGet(inFlightKey, out _));

            Assert.True(Program.InvalidateRefreshReadScope(cache, "kb", "genexus_read", refreshArgs));
            Assert.False(cache.TryGet(inFlightKey, out _));
            Assert.Equal(revisionDuringRefresh + 1, cache.GetRevision("kb"));
        }

        [Fact]
        public void RefreshWithoutResolvedKbScopeDoesNotClearOtherClients()
        {
            var cache = new SemanticCacheStore(16, System.TimeSpan.FromMinutes(30), () => 0);
            cache.Set("other-kb|genexus_read:stale-alias", new JObject { ["source"] = "keep" });
            var refreshArgs = new JObject { ["name"] = "SyntheticReport", ["refresh"] = true };

            Assert.False(Program.InvalidateRefreshReadScope(cache, "", "genexus_read", refreshArgs));
            Assert.True(cache.TryGet("other-kb|genexus_read:stale-alias", out _));
            Assert.Equal(0, cache.GetRevision("other-kb"));
        }

        [Theory]
        [InlineData("single", "ExtractSource")]
        [InlineData("full", "ExtractFullObject")]
        [InlineData("parts", "ExtractParts")]
        [InlineData("batch", "BatchRead")]
        public void RefreshIsForwardedForEveryReadShapeAndCannotBeCached(string shape, string action)
        {
            var args = new JObject { ["name"] = "SyntheticReport", ["refresh"] = true };
            if (shape == "single") args["part"] = "Source";
            if (shape == "parts") args["parts"] = new JArray("Rules", "Source");
            if (shape == "batch") { args.Remove("name"); args["targets"] = new JArray("SyntheticReport"); }
            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_read", args)!);
            Assert.Equal(action, routed["action"]?.ToString());
            Assert.True(routed["refresh"]!.Value<bool>());
            Assert.Null(Program.CreateSemanticCacheKey("kb", "genexus_read", args, false, false));
            Assert.Null(Program.CreateSemanticCacheKey("kb", "genexus_read", args, false, false, 1, "design", "default"));
            args["refresh"] = false;
            Assert.NotNull(Program.CreateSemanticCacheKey("kb", "genexus_read", args, false, false));
            Assert.NotNull(Program.CreateSemanticCacheKey("kb", "genexus_read", args, false, false, 1, "design", "default"));
        }
    }
}
