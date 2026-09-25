using System;
using System.Collections;
using System.Reflection;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class ObjectServiceSourceCacheTests
    {
        [Fact]
        public void TryReadPartSourceRaw_ReportsSdkReadFailureEvidence()
        {
            var service = new ObjectService(null, null);

            Assert.False(service.TryReadPartSourceRaw(null, "Source", out string source, out string error));
            Assert.Null(source);
            Assert.Contains("null", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EmptyRawSourceCache_HitSkipsSourceResolution_AndCanBeInvalidated()
        {
            var guid = Guid.NewGuid();
            string key = guid.ToString("N") + "|source|raw";
            var setter = typeof(ObjectService).GetMethod(
                "SetEmptyRawSourceCache",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(setter);
            setter.Invoke(null, new object[] { key });

            var service = new ObjectService(null, null);
            Assert.True(service.TryGetPartSourceRaw(guid.ToString(), "Source", out string source));
            Assert.Equal(string.Empty, source);

            ObjectService.InvalidateAllReadCaches();
            Assert.False(service.TryGetPartSourceRaw(guid.ToString(), "Source", out _));
        }

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
        public void LargeFullMcpRead_SeedsBoundedSearchCache_WithoutPersistingFullSource()
        {
            var guid = Guid.NewGuid();
            string source = new string('x', 256 * 1024 + 1);
            var payload = new JObject
            {
                ["source"] = source,
                ["truncated"] = false,
                ["isTruncatedByWorker"] = false,
                ["isBase64"] = false
            };

            Assert.True(ObjectService.CacheRawSourceFromReadPayload(
                guid, "Source", payload, offset: null, client: "mcp", minimize: false));

            var service = new GxMcp.Worker.Services.ObjectService(null, null);
            Assert.True(service.TryGetPartSourceRaw(guid.ToString(), "Source", out string cached));
            Assert.Equal(source, cached);

            var indexCache = new IndexCacheService();
            indexCache.LoadFromEntries(new[]
            {
                new GxMcp.Worker.Models.SearchIndex.IndexEntry
                {
                    Guid = guid.ToString(),
                    Name = "LargeReadNotPersisted",
                    Type = "Procedure"
                }
            });
            indexCache.MarkIndexComplete(1);
            var indexedService = new ObjectService(new KbService(indexCache), null);
            Assert.False(indexedService.TryPromoteCompleteSourceRead(
                guid, "Source", payload, offset: null, client: "mcp", minimize: false));
            Assert.Null(indexCache.GetIndex().Objects.Values.Single().FullSource);
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
        public void FullMcpRead_CanPromoteCompleteSourceToLoadedIndex()
        {
            var indexCache = new IndexCacheService();
            var guid = Guid.NewGuid();
            indexCache.LoadFromEntries(new[]
            {
                new GxMcp.Worker.Models.SearchIndex.IndexEntry
                {
                    Guid = guid.ToString(),
                    Name = "ReadPromoted",
                    Type = "Procedure"
                }
            });
            indexCache.MarkIndexComplete(1);

            var service = new ObjectService(new KbService(indexCache), null);
            var payload = new JObject
            {
                ["source"] = "parm(&CustomerId);",
                ["truncated"] = false,
                ["isTruncatedByWorker"] = false,
                ["isBase64"] = false
            };

            Assert.True(service.TryPromoteCompleteSourceRead(
                guid, "Source", payload, offset: null, client: "mcp", minimize: false));
            Assert.Equal("parm(&CustomerId);", indexCache.GetIndex().Objects.Values.Single().FullSource);
        }

        [Fact]
        public void PaginatedRead_CannotPromoteCompleteSourceToLoadedIndex()
        {
            var indexCache = new IndexCacheService();
            var guid = Guid.NewGuid();
            indexCache.LoadFromEntries(new[]
            {
                new GxMcp.Worker.Models.SearchIndex.IndexEntry
                {
                    Guid = guid.ToString(),
                    Name = "ReadNotPromoted",
                    Type = "Procedure"
                }
            });
            indexCache.MarkIndexComplete(1);

            var service = new ObjectService(new KbService(indexCache), null);
            var payload = new JObject
            {
                ["source"] = "parm(&CustomerId);",
                ["truncated"] = true,
                ["isTruncatedByWorker"] = true,
                ["isBase64"] = false
            };

            Assert.False(service.TryPromoteCompleteSourceRead(
                guid, "Source", payload, offset: null, client: "mcp", minimize: false));
            Assert.Null(indexCache.GetIndex().Objects.Values.Single().FullSource);
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

        [Fact]
        public void ReadCacheKey_BindsPartAndExactPaginationWindow()
        {
            var guid = Guid.NewGuid();
            string source = ObjectService.BuildReadCacheKey(guid, "Source", null, null, "mcp", false);
            string events = ObjectService.BuildReadCacheKey(guid, "Events", null, null, "mcp", false);
            string window = ObjectService.BuildReadCacheKey(guid, "Events", 318, 20, "mcp", false);
            string otherWindow = ObjectService.BuildReadCacheKey(guid, "Events", 318, 21, "mcp", false);

            Assert.NotEqual(source, events);
            Assert.NotEqual(window, otherWindow);
        }

        [Fact]
        public void ReadCache_StaysBoundedPastCap()
        {
            // The suite is serial, but earlier fixtures can leave >20 entries under
            // the default cap=256. Start empty before lowering the cap: one eviction
            // per insertion does not retroactively shrink an already larger cache.
            string prefix = "cap-" + Guid.NewGuid().ToString("N") + "|";
            var setter = typeof(ObjectService).GetMethod(
                "SetReadCache",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(setter);
            var cache = (IDictionary)typeof(ObjectService)
                .GetField("_readCache", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);
            try
            {
                ObjectService.InvalidateAllReadCaches();
                Environment.SetEnvironmentVariable("GXMCP_READ_CACHE_MAX", "20");
                for (int i = 0; i < 30; i++)
                {
                    setter.Invoke(null, new object[] { prefix + i, "payload-" + i });
                    System.Threading.Thread.Sleep(20);
                }
                int mine = cache.Keys.Cast<string>().Count(k => k.StartsWith(prefix));
                Assert.True(mine <= 20);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_READ_CACHE_MAX", null);
                foreach (var key in cache.Keys.Cast<string>().Where(k => k.StartsWith(prefix)).ToList())
                    cache.Remove(key);
            }
        }

        [Fact]
        public void ResolveReadCacheMaxEntries_DefaultsFloorsAndOverrides()
        {
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_READ_CACHE_MAX", null);
                Assert.Equal(256, ObjectService.ResolveReadCacheMaxEntries());

                Environment.SetEnvironmentVariable("GXMCP_READ_CACHE_MAX", "3"); // below floor
                Assert.Equal(16, ObjectService.ResolveReadCacheMaxEntries());

                Environment.SetEnvironmentVariable("GXMCP_READ_CACHE_MAX", "40");
                Assert.Equal(40, ObjectService.ResolveReadCacheMaxEntries());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_READ_CACHE_MAX", null);
            }
        }
    }
}
