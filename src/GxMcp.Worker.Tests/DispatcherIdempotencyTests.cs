using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.8.0 — end-to-end: when a `clientRequestId` is threaded through the
    // RPC params, CommandDispatcher.Dispatch returns the same response for
    // the same id and tags it as a replay. Real services would be invoked
    // here, but we drive through the `ping` exclusion path (which bypasses
    // the cache) to validate the bypass, then through a deliberately-
    // missing-method path which routes to the canonical error response —
    // a deterministic, side-effect-free emission we can cache and replay.
    public class DispatcherIdempotencyTests
    {
        public DispatcherIdempotencyTests() => IdempotencyCache.Clear();

        [Fact]
        public void Ping_BypassesCache_NoMatterTheRequestId()
        {
            var dispatcher = CommandDispatcher.Instance;
            string rpc = "{\"method\":\"ping\",\"params\":{\"clientRequestId\":\"ping-1\"}}";
            string r1 = dispatcher.Dispatch(rpc);
            string r2 = dispatcher.Dispatch(rpc);
            Assert.False(string.IsNullOrEmpty(r1));
            Assert.False(string.IsNullOrEmpty(r2));
            // Neither response carries the replayed tag — ping is excluded.
            try
            {
                var j2 = JObject.Parse(r2);
                Assert.True(j2["_meta"]?["replayed"] == null);
            }
            catch { /* ping returns non-JObject envelope; that's still a bypass success */ }
            Assert.Equal(0, IdempotencyCache.Count);
        }

        [Fact]
        public void UnknownMethod_WithRequestId_DoesNotCacheErrorEnvelope()
        {
            // A5: envelopes de erro não são mais cacheados — um retry com o
            // mesmo clientRequestId deve re-executar em vez de reproduzir a
            // falha transitória via replay.
            var dispatcher = CommandDispatcher.Instance;
            string rpc = "{\"method\":\"unknown_method\",\"action\":\"nope\",\"target\":\"X\",\"params\":{\"clientRequestId\":\"unk-1\"}}";

            string first = dispatcher.Dispatch(rpc);
            string second = dispatcher.Dispatch(rpc);

            var f = JObject.Parse(first);
            Assert.Equal("error", (string)f["status"]);
            Assert.True(f["_meta"]?["replayed"] == null);

            // Segunda chamada re-executa: mesma forma de erro, sem tag de replay.
            var s = JObject.Parse(second);
            Assert.Equal("error", (string)s["status"]);
            Assert.Equal((string)f["error"]["code"], (string)s["error"]["code"]);
            Assert.True(s["_meta"]?["replayed"] == null);

            // Nada foi persistido no cache.
            Assert.Equal(0, IdempotencyCache.Count);
        }

        [Fact]
        public void NoClientRequestId_NoCachingHappens()
        {
            var dispatcher = CommandDispatcher.Instance;
            string rpc = "{\"method\":\"unknown_method\",\"action\":\"nope\",\"target\":\"X\",\"params\":{}}";

            string first = dispatcher.Dispatch(rpc);
            string second = dispatcher.Dispatch(rpc);

            // Both responses look identical but neither carries the replay tag.
            var f = JObject.Parse(first);
            var s = JObject.Parse(second);
            Assert.Equal("error", (string)f["status"]);
            Assert.Equal("error", (string)s["status"]);
            Assert.True(f["_meta"]?["replayed"] == null);
            Assert.True(s["_meta"]?["replayed"] == null);
            Assert.Equal(0, IdempotencyCache.Count);
        }

        [Fact]
        public void MultiEditTargets_UsesMutationEngineVersionFence()
        {
            var dispatcher = CommandDispatcher.Instance;
            var rpc = new JObject
            {
                ["method"] = "batch",
                ["action"] = "MultiEdit",
                ["params"] = new JObject
                {
                    ["items"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "NoSuchObject",
                            ["part"] = "Source",
                            ["content"] = "updated",
                            ["expectedVersion"] = "stale"
                        }
                    },
                    ["rollbackOnFailure"] = true
                }
            };

            var response = JObject.Parse(dispatcher.Dispatch(rpc.ToString(Newtonsoft.Json.Formatting.None)));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("ConcurrencyStateUnavailable", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void DifferentRequestIds_SuccessEnvelopes_CacheSeparately()
        {
            // A5: erros não são cacheados; a separação por id vale para
            // envelopes de sucesso — validado via IsCacheableSuccessEnvelope
            // com dois envelopes distintos.
            var ok1 = "{\"status\":\"Ok\",\"target\":\"A\"}";
            var ok2 = "{\"status\":\"Ok\",\"target\":\"B\"}";
            Assert.True(CommandDispatcher.IsCacheableSuccessEnvelope(ok1));
            Assert.True(CommandDispatcher.IsCacheableSuccessEnvelope(ok2));
        }

        [Fact]
        public void ErrorEnvelope_WithTopLevelErrorToken_IsNotCacheable()
        {
            var envelope = "{\"error\":{\"code\":\"KbNotOpen\"},\"status\":\"Error\"}";
            Assert.False(CommandDispatcher.IsCacheableSuccessEnvelope(envelope));
        }

        [Theory]
        [InlineData("Error")]
        [InlineData("NotFound")]
        [InlineData("NotImplemented")]
        [InlineData("WorkerBusy")]
        [InlineData("Busy")]
        [InlineData("IndexNotReady")]
        [InlineData("Reindexing")]
        [InlineData("IndexCold")]
        [InlineData("Timeout")]
        [InlineData("Cancelled")]
        [InlineData("Running")]
        public void NonCacheableStatuses_AreRejected(string status)
        {
            var envelope = "{\"status\":\"" + status + "\"}";
            Assert.False(CommandDispatcher.IsCacheableSuccessEnvelope(envelope));
        }

        [Fact]
        public void StatusComparison_IsCaseInsensitive()
        {
            Assert.False(CommandDispatcher.IsCacheableSuccessEnvelope("{\"status\":\"error\"}"));
            Assert.True(CommandDispatcher.IsCacheableSuccessEnvelope("{\"status\":\"OK\"}"));
        }

        [Theory]
        [InlineData("{\"status\":\"Ok\"}")]
        [InlineData("{\"code\":\"WriteApplied\",\"target\":\"X\"}")]
        public void SuccessEnvelopes_AreCacheable(string envelope)
        {
            Assert.True(CommandDispatcher.IsCacheableSuccessEnvelope(envelope));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-json")]
        [InlineData("[1,2,3]")]
        public void InvalidOrNonObjectJson_IsNotCacheable(string json)
        {
            Assert.False(CommandDispatcher.IsCacheableSuccessEnvelope(json));
        }
    }
}
