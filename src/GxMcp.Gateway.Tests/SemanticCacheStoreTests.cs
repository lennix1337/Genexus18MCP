using System;
using System.Threading;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A4 — the semantic cache used to be an unbounded ConcurrentDictionary in a
    /// gateway that lives forever, so long read-only sessions grew without bound.
    /// SemanticCacheStore adds a cap (LRU eviction by last access) and an
    /// absolute TTL swept on read/Set. These tests drive both via the internal ctor.
    /// </summary>
    public class SemanticCacheStoreTests
    {
        private static JObject Envelope(string marker) => new JObject { ["marker"] = marker };

        [Fact]
        public void Set_BeyondCap_EvictsLeastRecentlyAccessed()
        {
            var store = new SemanticCacheStore(maxEntries: 2, ttl: TimeSpan.FromMinutes(30));

            store.Set("a", Envelope("a"));
            store.Set("b", Envelope("b"));
            store.Set("c", Envelope("c")); // cap=2 → 'a' (oldest) must go

            Assert.False(store.TryGet("a", out _));
            Assert.True(store.TryGet("b", out _));
            Assert.True(store.TryGet("c", out _));
        }

        [Fact]
        public void TryGet_UpdatesAccess_RecentlyUsedSurvivesEviction()
        {
            var store = new SemanticCacheStore(maxEntries: 2, ttl: TimeSpan.FromMinutes(30));

            store.Set("old", Envelope("old"));
            store.Set("hot", Envelope("hot"));

            // Touch 'hot' so its last-access is newer than 'old'.
            Assert.True(store.TryGet("hot", out _));
            Thread.Sleep(20); // TickCount64 has ms resolution — guarantee a visible gap.

            store.Set("new", Envelope("new")); // evicts 'old', not the touched 'hot'

            Assert.False(store.TryGet("old", out _));
            Assert.True(store.TryGet("hot", out _));
            Assert.True(store.TryGet("new", out _));
        }

        [Fact]
        public void Set_AfterTtlWithoutAccess_EntryExpires()
        {
            var store = new SemanticCacheStore(maxEntries: 8, ttl: TimeSpan.FromMilliseconds(80));

            store.Set("k", Envelope("k"));
            Assert.True(store.TryGet("k", out _)); // alive before expiry

            Thread.Sleep(150);

            // Lazy expiry on read...
            Assert.False(store.TryGet("k", out _));

            // ...and on the next Set's sweep.
            store.Set("fresh", Envelope("fresh"));
            Assert.False(store.TryGet("k", out _));
            Assert.True(store.TryGet("fresh", out _));
        }

        [Fact]
        public void Clear_RemovesEverything()
        {
            var store = new SemanticCacheStore(maxEntries: 4, ttl: TimeSpan.FromMinutes(30));
            store.Set("x", Envelope("x"));
            store.Set("y", Envelope("y"));

            store.Clear();

            Assert.False(store.TryGet("x", out _));
            Assert.False(store.TryGet("y", out _));
        }

        [Fact]
        public void TryGet_Miss_ReturnsFalseAndNullValue()
        {
            var store = new SemanticCacheStore(maxEntries: 4, ttl: TimeSpan.FromMinutes(30));

            Assert.False(store.TryGet("missing", out var value));
            Assert.Null(value);
        }
    }
}
