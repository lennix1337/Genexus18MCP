using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class CacheRevisionTests
    {
        [Fact]
        public void AbsoluteTtl_DoesNotExtendWhenEntryIsHit()
        {
            long now = 0;
            var store = new SemanticCacheStore(8, TimeSpan.FromMilliseconds(10), () => now);
            store.Set("kb1|genexus_read:{}", new JObject { ["ok"] = true });

            now = 9;
            Assert.True(store.TryGet("kb1|genexus_read:{}", out _));

            now = 10;
            Assert.False(store.TryGet("kb1|genexus_read:{}", out _));
        }

        [Fact]
        public void InvalidateScope_AdvancesOnlyAffectedKbGeneration()
        {
            var store = new SemanticCacheStore(8, TimeSpan.FromMinutes(30));
            store.Set("kb1|genexus_query:{}", new JObject { ["kb"] = 1 });
            store.Set("kb2|genexus_query:{}", new JObject { ["kb"] = 2 });

            long before = store.GetRevision("KB1");
            long after = store.InvalidateScope("KB1", out int removed);

            Assert.Equal(before + 1, after);
            Assert.Equal(1, removed);
            Assert.Equal(after, store.GetRevision("kb1"));
            Assert.False(store.TryGet("kb1|genexus_query:{}", out _));
            Assert.True(store.TryGet("kb2|genexus_query:{}", out _));
            Assert.Equal(0, store.GetRevision("kb2"));
        }

        [Fact]
        public void ClearScope_AlsoAdvancesGenerationForDirectCallers()
        {
            var store = new SemanticCacheStore(8, TimeSpan.FromMinutes(30));
            store.Set("kb1|genexus_query:{}", new JObject());

            Assert.Equal(1, store.ClearScope("KB1"));
            Assert.Equal(1, store.GetRevision("kb1"));
            Assert.False(store.TryGet("kb1|genexus_query:{}", out _));
        }

        [Fact]
        public void CanonicalKey_SortsObjectArgumentsAndIncludesIdentityAndRevision()
        {
            var first = new JObject { ["limit"] = 10, ["filter"] = new JObject { ["b"] = 2, ["a"] = 1 } };
            var second = new JObject { ["filter"] = new JObject { ["a"] = 1, ["b"] = 2 }, ["limit"] = 10 };

            string? firstKey = Program.CreateSemanticCacheKey(
                "KB1", "GENEXUS_QUERY", first, false, false, 4, "model-v2", "development");
            string? secondKey = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", second, false, false, 4, "MODEL-V2", "Development");
            string? nextRevisionKey = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", second, false, false, 5, "model-v2", "development");

            Assert.NotNull(firstKey);
            Assert.Equal(firstKey, secondKey);
            Assert.NotEqual(firstKey, nextRevisionKey);
            Assert.Contains("|rev=4|model=model-v2|env=development", firstKey);
        }

        [Fact]
        public void CanonicalKey_PreservesArrayOrder()
        {
            var first = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", new JObject { ["targets"] = new JArray("A", "B") }, false, false,
                1, null, null);
            var reordered = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", new JObject { ["targets"] = new JArray("B", "A") }, false, false,
                1, null, null);

            Assert.NotEqual(first, reordered);
        }

        [Fact]
        public void DispatchKey_CanonicalizesArgumentsAtInitialGeneration()
        {
            var first = Program.CreateSemanticCacheKey(
                "KB1", "GENEXUS_QUERY", new JObject { ["b"] = 2, ["a"] = 1 }, false, false,
                0, null, null);
            var reordered = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", new JObject { ["a"] = 1, ["b"] = 2 }, false, false,
                0, null, null);

            Assert.Equal(first, reordered);
            Assert.Contains("|rev=0|model=|env=", first);
        }

        [Fact]
        public void ExternalWatcherScope_ResolvesConfiguredAliasByCanonicalPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-cache-kb");
            var config = new Configuration
            {
                Environment = new EnvironmentConfig
                {
                    KBs = new System.Collections.Generic.List<KbEntry>
                    {
                        new KbEntry { Alias = "Sales", Path = root + Path.DirectorySeparatorChar }
                    }
                }
            };

            Assert.Equal("sales", Program.ResolveConfiguredKbAlias(config, root));
            Assert.Null(Program.ResolveConfiguredKbAlias(config, Path.Combine(root, "other")));
        }
    }
}
