using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class IdempotencyCacheTests
    {
        [Fact]
        public async Task NonCacheableResult_DoesNotDetachWaitingCallerFromGate()
        {
            var cache = new IdempotencyCache(15, 1000);
            var firstRelease = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRelease = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = cache.GetOrCompute("kb1", "t", "k1", "h1", () => firstRelease.Task);
            var second = cache.GetOrCompute("kb1", "t", "k1", "h1", () =>
            {
                secondStarted.SetResult(true);
                return secondRelease.Task;
            });
            Task<JObject>? third = null;
            int thirdCalls = 0;
            try
            {
                firstRelease.SetException(new ErrorNotCacheable(new JObject { ["isError"] = true }));
                await first;
                await secondStarted.Task;
                third = cache.GetOrCompute("kb1", "t", "k1", "h1", () =>
                {
                    thirdCalls++;
                    return Task.FromResult(new JObject());
                });
                Assert.Equal(0, thirdCalls);
                Assert.Equal(1, cache.GateCount);
            }
            finally
            {
                secondRelease.TrySetResult(new JObject { ["answer"] = 42 });
                await second;
                if (third != null) await third;
            }
            Assert.Equal(0, cache.GateCount);
        }

        [Fact]
        public async Task GateTimeout_DoesNotExecuteDuplicate_OriginalRemainsReplayable()
        {
            var cache = new IdempotencyCache(15, 1000, System.TimeSpan.FromMilliseconds(50));
            var release = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var original = cache.GetOrCompute("kb1", "t", "k1", "h1", () => release.Task);
            int duplicateCalls = 0;
            try
            {
                var error = await Assert.ThrowsAsync<UsageException>(() =>
                    cache.GetOrCompute("kb1", "t", "k1", "h1", () =>
                    {
                        duplicateCalls++;
                        return Task.FromResult(new JObject());
                    }));
                Assert.Equal("idempotency_in_progress", error.Code);
                Assert.Contains("still in progress", error.Message);
                Assert.Equal(0, duplicateCalls);
                Assert.False(original.IsCompleted);
            }
            finally
            {
                release.TrySetResult(JObject.Parse("{\"answer\":42}"));
                await original;
            }

            var replay = await cache.GetOrCompute("kb1", "t", "k1", "h1",
                () => throw new System.InvalidOperationException("Duplicate executed"));
            Assert.Equal(42, (int)replay["answer"]!);
            Assert.Equal(0, cache.GateCount);
        }

        [Fact]
        public void Miss_ReturnsNull()
        {
            var cache = new IdempotencyCache(ttlMinutes: 15, capacity: 1000);
            var hit = cache.TryGet("kb1", "genexus_edit", "k1",
                payloadHash: "h1", out var cached);
            Assert.False(hit);
            Assert.Null(cached);
        }

        [Fact]
        public void Hit_SamePayloadHash_ReturnsCachedResult()
        {
            var cache = new IdempotencyCache(15, 1000);
            var result = JObject.Parse("{\"ok\":true}");
            cache.Put("kb1", "genexus_edit", "k1", "h1", result);
            var hit = cache.TryGet("kb1", "genexus_edit", "k1", "h1", out var cached);
            Assert.True(hit);
            Assert.Equal(result.ToString(), cached!.ToString());
        }

        [Fact]
        public void Hit_DifferentPayloadHash_ThrowsConflict()
        {
            var cache = new IdempotencyCache(15, 1000);
            cache.Put("kb1", "genexus_edit", "k1", "h1", JObject.Parse("{}"));
            Assert.Throws<IdempotencyConflictException>(() =>
                cache.TryGet("kb1", "genexus_edit", "k1", "h2", out _));
        }

        [Fact]
        public void DifferentKb_DoesNotCollide()
        {
            var cache = new IdempotencyCache(15, 1000);
            cache.Put("kb1", "genexus_edit", "k1", "h1", JObject.Parse("{\"a\":1}"));
            var hit = cache.TryGet("kb2", "genexus_edit", "k1", "h1", out _);
            Assert.False(hit);
        }

        [Fact]
        public void Eviction_LruDropsAtLeastOneEntryWhenOverCapacity()
        {
            // IdempotencyCache shards its KbBucket across 16 shards; the LRU is enforced
            // per-shard, not globally (documented in KbBucket.cs). With capacity=N the
            // per-shard cap is ceil(N/16). Push more keys than the shard count so the
            // pigeonhole principle guarantees at least one shard holds 2+ entries and
            // must evict, regardless of how the hash distributes keys across shards.
            var cache = new IdempotencyCache(15, capacity: 16);
            const int Inserts = 32; // 16 shards * 1 per shard + 16 extras → guaranteed eviction
            for (int i = 0; i < Inserts; i++)
                cache.Put("kb1", "t", "k" + i, "h" + i, JObject.Parse("{}"));

            int retained = 0;
            for (int i = 0; i < Inserts; i++)
                if (cache.TryGet("kb1", "t", "k" + i, "h" + i, out _)) retained++;

            Assert.True(retained < Inserts,
                $"Expected eviction after {Inserts} inserts against a capacity-16 (sharded) cache; all {retained} survived.");
            Assert.True(retained >= 1, "Expected at least one entry to remain.");
        }

        [Fact]
        public async Task ConcurrentSameKey_SecondCallerWaitsAndGetsSameResult()
        {
            var cache = new IdempotencyCache(15, 1000);
            var firstStarted = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var releaseFirst = new System.Threading.Tasks.TaskCompletionSource<bool>();

            System.Threading.Tasks.Task<JObject> First() =>
                cache.GetOrCompute("kb1", "t", "k1", "h1", async () =>
                {
                    firstStarted.SetResult(true);
                    await releaseFirst.Task;
                    return JObject.Parse("{\"answer\":42}");
                });

            var t1 = First();
            await firstStarted.Task;
            var t2 = First(); // must wait for t1, not run factory again
            releaseFirst.SetResult(true);

            var r1 = await t1;
            var r2 = await t2;
            Assert.Equal(r1.ToString(), r2.ToString());
        }
    }
}
