using System;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// #653: a read that starts after the pre-dispatch invalidation of a write and finishes
    /// before the write lands is cached at the new revision with the pre-write source, and
    /// replayed for the cache lifetime. The generation fence cannot tell it from a fresh
    /// entry, so the scope is closed again when the write settles.
    /// </summary>
    public class MutationSettledCacheTests
    {
        private static SemanticCacheStore NewCache() =>
            new SemanticCacheStore(16, TimeSpan.FromMinutes(30), () => 0);

        [Fact]
        public void AReadCachedWhileTheWriteWasInFlightDoesNotSurviveTheWrite()
        {
            var cache = NewCache();
            var edit = new JObject { ["name"] = "SyntheticProc", ["mode"] = "patch" };

            // What the dispatcher does before it sends the write.
            cache.InvalidateScope("kb");
            long generation = cache.GetRevision("kb");

            // A read of the same object starts in that generation and ends before the write lands.
            string key = Program.CreateSemanticCacheKey(
                "kb", "genexus_read", new JObject { ["name"] = "SyntheticProc" }, false, false, generation, null, null)!;
            cache.Set(key, new JObject { ["source"] = "pre-edit" });
            Assert.True(cache.TryGet(key, out _));

            Assert.True(Program.InvalidateSettledMutationScope(cache, "kb", edit, isMutating: true));

            Assert.False(cache.TryGet(key, out _));
            Assert.Equal(generation + 1, cache.GetRevision("kb"));
        }

        [Fact]
        public void AReadStartedBeforeTheWriteSettledCannotFillTheCacheAfterIt()
        {
            var cache = NewCache();
            cache.InvalidateScope("kb");
            long generationAtDispatch = cache.GetRevision("kb");

            Program.InvalidateSettledMutationScope(cache, "kb", new JObject { ["name"] = "SyntheticProc" }, isMutating: true);

            // The dispatcher stores a read only while the revision is the one it started in.
            Assert.NotEqual(generationAtDispatch, cache.GetRevision("kb"));
        }

        [Fact]
        public void OtherKnowledgeBasesKeepTheirWarmEntries()
        {
            var cache = NewCache();
            cache.Set("other-kb|genexus_read:alias", new JObject { ["source"] = "keep" });

            Program.InvalidateSettledMutationScope(cache, "kb", new JObject { ["name"] = "SyntheticProc" }, isMutating: true);

            Assert.True(cache.TryGet("other-kb|genexus_read:alias", out _));
            Assert.Equal(0, cache.GetRevision("other-kb"));
        }

        [Theory]
        [InlineData(false, "{\"name\":\"P\"}")]
        [InlineData(true, "{\"name\":\"P\",\"dryRun\":true}")]
        [InlineData(true, "{\"name\":\"P\",\"validate\":\"only\"}")]
        public void AReadOnlyCallOrAPreviewLeavesTheCacheAlone(bool isMutating, string argsJson)
        {
            var cache = NewCache();
            cache.Set("kb|genexus_read:alias", new JObject { ["source"] = "keep" });

            Assert.False(Program.InvalidateSettledMutationScope(cache, "kb", JObject.Parse(argsJson), isMutating));

            Assert.True(cache.TryGet("kb|genexus_read:alias", out _));
            Assert.Equal(0, cache.GetRevision("kb"));
        }

        [Fact]
        public void AnUnresolvedScopeAdvancesTheProcessWideEpoch()
        {
            var cache = NewCache();
            int before = Program.SemanticCacheEpoch;

            Assert.True(Program.InvalidateSettledMutationScope(cache, "", new JObject { ["name"] = "P" }, isMutating: true));

            Assert.True(Program.SemanticCacheEpoch > before);
        }

        [Fact]
        public void TheDispatcherClosesTheScopeAfterTheWorkerReturns()
        {
            string dispatch = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Gateway", "Program.ToolDispatch.cs"));
            const string settle = "InvalidateSettledMutationScope(_semanticCache, kbScope, tArgs, isMutating);";

            int invalidateBefore = dispatch.IndexOf("_semanticCache.InvalidateScope(kbScope, out int removed)", StringComparison.Ordinal);
            int workerCall = invalidateBefore < 0 ? -1
                : dispatch.IndexOf("innerResult = await SendWorkerCommandAsync", invalidateBefore, StringComparison.Ordinal);
            int settled = workerCall < 0 ? -1 : dispatch.IndexOf(settle, workerCall, StringComparison.Ordinal);

            Assert.True(invalidateBefore >= 0 && workerCall > invalidateBefore && settled > workerCall,
                "A mutation must invalidate before dispatch and close the scope again after the Worker returns.");
            Assert.Equal(1, SourceAssert.Count(dispatch, settle));
        }
    }
}
