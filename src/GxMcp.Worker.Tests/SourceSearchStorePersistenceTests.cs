using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The source scan persisted every source it saw, whatever tier served it, stamped
    /// with the index entry's LastUpdate. After an external save the watcher bumps that
    /// timestamp while the guid|part cache still holds the old text, so the old text was
    /// written to the durable store certified as fresh and survived restarts. Only an SDK
    /// read in the same iteration may be persisted.
    /// </summary>
    public class SourceSearchStorePersistenceTests : IDisposable
    {
        private readonly string _tempDir;

        public SourceSearchStorePersistenceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-search-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void PartCacheHit_IsSearchedButNotPersistedWithTheEntryTimestamp()
        {
            // A non-primary part is used on purpose: the primary source alias is also
            // promoted into the index (IndexCacheService.PromoteSourceForSearch), which
            // has its own store write; this guard isolates the scan loop's write.
            var entry = new SearchIndex.IndexEntry
            {
                Name = "CachedOnlyProbe",
                Type = "WebPanel",
                Guid = Guid.NewGuid().ToString(),
                LastUpdate = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                // Same shape as ExplicitPartCannotBeDiscardedByUnrelatedIndexedSource.
                FullSource = "OtherPart()"
            };
            var index = new IndexCacheService();
            index.LoadFromEntries(new[] { entry });
            index.MarkIndexComplete(1);
            Assert.True(ObjectService.CacheRawSourceFromReadPayload(Guid.Parse(entry.Guid), "rules",
                new JObject { ["source"] = "CachedOnlyNeedle()" }, null, "mcp", false));
            Assert.Equal(0, SourceStoreService.Instance.StoredRecordCount);

            var raw = new SourceSearchService(index, new ObjectService(null, null))
                .SearchAsJson(new SourceSearchCriteria
                {
                    Pattern = "CachedOnlyNeedle",
                    Scope = new List<string> { "rules" },
                    MaxResults = 10,
                    TimeoutMs = 30000
                });
            var result = JObject.Parse(raw)["result"];
            Assert.NotNull(result);

            // The cached text is still searched...
            Assert.Equal(1, result["count"].Value<int>());
            // ...but it is not written to the durable store as fresh for entry.LastUpdate.
            Assert.Equal(0, SourceStoreService.Instance.StoredRecordCount);
            Assert.False(SourceStoreService.Instance.TryGetStoredAndFresh(entry, "rules", out _));
        }
    }
}
