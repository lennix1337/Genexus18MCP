using System;
using System.Reflection;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class RefreshReadTests
    {
        [Fact]
        public void RefreshCannotReplayAnObjectReaderCachedResponse()
        {
            ObjectReader.InvalidateAll();
            var request = new ObjectReadRequest { Target = "SyntheticRefresh439", PartName = "Source", TypeFilter = "Procedure" };
            var cache = typeof(ObjectReader).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var entryType = typeof(ObjectReader).GetNestedType("CacheEntry", BindingFlags.NonPublic);
            var entry = Activator.CreateInstance(entryType);
            entryType.GetProperty("Payload").SetValue(entry, "{\"source\":\"old\"}");
            entryType.GetProperty("UpdatedUtc").SetValue(entry, DateTime.UtcNow);
            cache.GetType().GetProperty("Item").SetValue(cache, entry, new object[] { ObjectReader.BuildCacheKey(request) });
            try
            {
                var reader = new ObjectReader(null);
                var cached = JObject.Parse(reader.Read(request));
                Assert.Equal("old", cached["source"]?.ToString());
                Assert.Equal("worker-object-reader-cache", cached["readFreshness"]?["origin"]?.ToString());
                Assert.True(cached["readFreshness"]!["ageMs"].Value<long>() >= 0);
                request.Refresh = true;
                var refreshed = JObject.Parse(reader.Read(request));
                Assert.NotEqual("old", refreshed["source"]?.ToString());
                Assert.NotNull(refreshed["error"]);
            }
            finally { ObjectReader.InvalidateAll(); }
        }

        // Issue #653: a write marks the managed read caches dirty through
        // RemoveReadCacheEntries, but the ObjectReader response cache is keyed by the
        // request (name/type/part shape), has no object-identity index and is only
        // cleared by a refresh read - so a plain read after a successful edit was
        // served from it until its TTL expired.
        [Fact]
        public void WriteInvalidationAlsoDropsTheObjectReaderResponseCache()
        {
            ObjectReader.InvalidateAll();
            var request = new ObjectReadRequest { Target = "SyntheticEdit653", FullObject = true, TypeFilter = "Procedure" };
            var cache = typeof(ObjectReader).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var entryType = typeof(ObjectReader).GetNestedType("CacheEntry", BindingFlags.NonPublic);
            var entry = Activator.CreateInstance(entryType);
            entryType.GetProperty("Payload").SetValue(entry, "{\"source\":\"pre-edit\"}");
            entryType.GetProperty("UpdatedUtc").SetValue(entry, DateTime.UtcNow);
            cache.GetType().GetProperty("Item").SetValue(cache, entry, new object[] { ObjectReader.BuildCacheKey(request) });
            try
            {
                var reader = new ObjectReader(null);
                Assert.Equal("pre-edit", JObject.Parse(reader.Read(request))["source"]?.ToString());

                ObjectService.RemoveReadCacheEntries(Guid.NewGuid(), "Source");

                var afterWrite = JObject.Parse(reader.Read(request));
                Assert.NotEqual("pre-edit", afterWrite["source"]?.ToString());
                Assert.NotNull(afterWrite["error"]);
            }
            finally { ObjectReader.InvalidateAll(); }
        }

        [Fact]
        public void ReadProvenanceDoesNotTurnErrorsIntoFreshSuccess()
        {
            var observedAt = DateTime.UtcNow.AddSeconds(-4).ToString("o");
            var payload = new JObject
            {
                ["source"] = "synthetic",
                ["readFreshness"] = new JObject
                {
                    ["origin"] = "sdk-object-state",
                    ["observedAtUtc"] = observedAt,
                    ["sdkCacheRefreshConfirmed"] = false
                }
            };
            var cached = JObject.Parse(ObjectService.DescribeReadFreshness(payload.ToString(), false, "worker-object-reader-cache"));
            Assert.False(cached["readFreshness"]!["sdkCacheRefreshConfirmed"].Value<bool>());
            Assert.Equal("worker-object-reader-cache", cached["readFreshness"]?["origin"]?.ToString());
            DateTime returnedObservation = cached["readFreshness"]!["observedAtUtc"].Value<DateTime>().ToUniversalTime();
            DateTime expectedObservation = DateTime.Parse(observedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            Assert.Equal(expectedObservation, returnedObservation);
            Assert.InRange(cached["readFreshness"]!["ageMs"].Value<long>(), 3000L, 30000L);

            var fresh = JObject.Parse(ObjectService.DescribeReadFreshness("{\"source\":\"synthetic\"}", false));
            Assert.Equal("sdk-object-state", fresh["readFreshness"]?["origin"]?.ToString());
            Assert.True(fresh["readFreshness"]!["ageMs"].Value<long>() >= 0);
            string error = "{\"status\":\"error\",\"error\":{\"code\":\"FreshReadUnavailable\"}}";
            Assert.Equal(error, ObjectService.DescribeReadFreshness(error, true));
        }

        // Issue #830: an ordinary read returned the SDK object's in-memory text labelled `sdk-object-state`, which reads as
        // the persisted model. The read now says which copy of the text the caller holds.
        [Fact]
        public void ReadLabelsWhetherItsTextIsTheInMemoryBufferOrThePersistedModel()
        {
            var buffer = JObject.Parse(ObjectService.DescribeReadFreshness("{\"source\":\"typed\"}", false));
            Assert.Equal("sdk-object-state", buffer["readFreshness"]?["origin"]?.ToString());
            Assert.Equal("in-memory-buffer", buffer["readFreshness"]?["textSource"]?.ToString());

            var reloaded = JObject.Parse(ObjectService.DescribeReadFreshness("{\"source\":\"saved\"}", true));
            Assert.Equal("sdk-refresh", reloaded["readFreshness"]?["origin"]?.ToString());
            Assert.Equal("persisted-model", reloaded["readFreshness"]?["textSource"]?.ToString());

            // A Worker cache replays the stored text, so it keeps the label of the read that produced that text.
            var replayed = JObject.Parse(ObjectService.DescribeReadFreshness(buffer.ToString(), false, "worker-object-service-cache"));
            Assert.Equal("worker-object-service-cache", replayed["readFreshness"]?["origin"]?.ToString());
            Assert.Equal("in-memory-buffer", replayed["readFreshness"]?["textSource"]?.ToString());

            // A cached payload that carries no label says so instead of inheriting a guess.
            var unlabelled = JObject.Parse(ObjectService.DescribeReadFreshness("{\"source\":\"old\"}", false, "worker-object-reader-cache"));
            Assert.Equal("unknown", unlabelled["readFreshness"]?["textSource"]?.ToString());
        }

        [Fact]
        public void ComRefreshFailsClosedAndOtherDriverShapesRemainAvailable()
        {
            var error = JObject.Parse(ObjectService.FreshReadUnsupportedResponse("SyntheticReport", refresh: true, isComDriver: true));
            Assert.Equal("FreshReadUnavailable", error["error"]?["code"]?.ToString());
            Assert.Null(ObjectService.FreshReadUnsupportedResponse("SyntheticReport", refresh: false, isComDriver: true));
            Assert.Null(ObjectService.FreshReadUnsupportedResponse("SyntheticReport", refresh: true, isComDriver: false));
        }

        [Fact]
        public void EveryServiceReadRefreshesSameIdentityAndEvictsAliasesBeforeReturningBody()
        {
            string src = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ObjectService.cs"));
            Assert.Equal(3, SourceAssert.Count(src, "refresh ? FindObjectFresh(target, typeFilter, guid, entityKey, path)"));
            Assert.Contains("RemoveReadCacheEntries(seed.Guid, null);", src);
            Assert.Contains("ObjectReader.InvalidateAll();", src);
            Assert.Contains("DescribeReadFreshness(cachedPayload, false, \"worker-object-service-cache\")", src);
            Assert.Contains("fresh.Guid != seed.Guid || object.ReferenceEquals(fresh, seed)", src);
            Assert.Contains("EvictRetainedInstance(seed);", src);
            Assert.Contains("RemoveFromCaches(obj)", src);
            Assert.Contains("if (!InvalidateCache(seed))", src);
            Assert.Contains("code: \"FreshReadUnavailable\"", src);
            Assert.Contains("FreshReadUnsupportedResponse", src);
            string dispatcher = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs"));
            Assert.Contains("args?[\"path\"]?.ToString(), conditional,\n                    args?[\"refresh\"]?.ToObject<bool?>() ?? false", dispatcher);
            string reader = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ObjectReader.cs"));
            Assert.Contains("if (!request.Refresh && !IsPatternSettingsRead(request) && IsCacheable(result))", reader);
            Assert.Contains("DescribeReadFreshness(cachedResult, false, \"worker-object-reader-cache\")", reader);
            Assert.Contains("FreshReadUnsupportedResponse(", reader);
            string batch = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "BatchService.cs"));
            Assert.Contains("FreshReadUnsupportedResponse(", batch);
            string commandDispatcher = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs"));
            Assert.Contains("FreshReadUnsupportedResponse(", commandDispatcher);
            string inspection = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ObjectInspectionModule.cs"));
            Assert.Equal(5, SourceAssert.Count(inspection, "Refresh = args[\"refresh\"]?.ToObject<bool?>() ?? false"));
            string gateway = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Gateway", "Program.ToolDispatch.cs"));
            string invalidate = "InvalidateRefreshReadScope(_semanticCache, kbScope, tName, tArgs);";
            int beforeRefresh = gateway.IndexOf(invalidate, StringComparison.Ordinal);
            int workerCall = beforeRefresh < 0 ? -1
                : gateway.IndexOf("innerResult = await SendWorkerCommandAsync", beforeRefresh, StringComparison.Ordinal);
            int afterRefresh = workerCall < 0 ? -1 : gateway.IndexOf(invalidate, workerCall, StringComparison.Ordinal);
            Assert.True(beforeRefresh >= 0 && workerCall > beforeRefresh && afterRefresh > workerCall,
                "Refresh must invalidate before dispatch and advance the generation again after the Worker returns.");
            Assert.Contains("hitMeta[\"cacheSource\"] = \"gateway-semantic-cache\"", gateway);
            Assert.Contains("hitMeta[\"cacheAgeMs\"] = cacheAgeMs", gateway);
        }
    }
}
