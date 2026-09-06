using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class IdempotencyMiddlewareTests
    {
        [Fact]
        public async Task SameKey_SecondCallReturnsCached_WithoutHittingWorker()
        {
            var calls = 0;
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{\"id\":1}}"));
            }

            var req = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\",\"idempotencyKey\":\"k1\"}}");
            var r1 = await middleware.Invoke(req, Inner);
            var r2 = await middleware.Invoke(req, Inner);

            Assert.Equal(1, calls);
            Assert.Null(r1["meta"]?["idempotent"]);
            Assert.True((bool)r2["meta"]!["idempotent"]!);
        }

        [Fact]
        public async Task DryRun_BypassesCache()
        {
            var calls = 0;
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{}}"));
            }

            var req = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\"," +
                "\"idempotencyKey\":\"k1\",\"dryRun\":true}}");
            await middleware.Invoke(req, Inner);
            await middleware.Invoke(req, Inner);

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task ErrorResult_NotCached()
        {
            var calls = 0;
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":true,\"error\":{\"message\":\"boom\"}}"));
            }

            var req = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\",\"idempotencyKey\":\"k1\"}}");
            await middleware.Invoke(req, Inner);
            await middleware.Invoke(req, Inner);

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task ReorderedArguments_WithSameIdempotencyKey_ReusesCachedResult()
        {
            var calls = 0;
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{\"id\":1}}"));
            }

            var req1 = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"X\",\"content\":\"<x/>\",\"idempotencyKey\":\"k1\"}}");
            var req2 = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"content\":\"<x/>\",\"name\":\"X\",\"idempotencyKey\":\"k1\"}}");

            var first = await middleware.Invoke(req1, Inner);
            var second = await middleware.Invoke(req2, Inner);

            Assert.Equal(1, calls);
            Assert.Equal(first["data"]!.ToString(), second["data"]!.ToString());
            Assert.Null(first["meta"]);
            Assert.True((bool)second["meta"]!["idempotent"]!);
        }

        [Fact]
        public async Task LegacyAlias_UsesCanonicalMutationPolicy()
        {
            var calls = 0;
            var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), kbPath: "kb1");

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{\"id\":1}}"));
            }

            var req = JObject.Parse(
                "{\"name\":\"genexus_import_object\",\"arguments\":{\"name\":\"X\",\"inputPath\":\"x.txt\",\"idempotencyKey\":\"k1\"}}");
            var first = await middleware.Invoke(req, Inner);
            var canonical = JObject.Parse(
                "{\"name\":\"genexus_io\",\"arguments\":{\"action\":\"import_part\",\"name\":\"X\",\"inputPath\":\"x.txt\",\"idempotencyKey\":\"k1\"}}");
            var second = await middleware.Invoke(canonical, Inner);

            Assert.Equal(1, calls);
            Assert.Null(first["meta"]);
            Assert.True((bool)second["meta"]!["idempotent"]!);
        }

        [Fact]
        public async Task RuntimeModelAndEnvironmentBindTheOperationKey()
        {
            var cache = new IdempotencyCache(15, 1000);
            var first = new IdempotencyMiddleware(cache, "kb1", "model-a", "development");
            var second = new IdempotencyMiddleware(cache, "kb1", "model-a", "production");
            int calls = 0;

            Task<JObject> Inner(JObject req)
            {
                calls++;
                return Task.FromResult(JObject.Parse("{\"isError\":false,\"data\":{}}"));
            }

            var request = JObject.Parse(
                "{\"name\":\"genexus_edit\",\"arguments\":{\"name\":\"Customer\",\"content\":\"x\",\"idempotencyKey\":\"context-key\"}}");

            await first.Invoke(request, Inner);
            await Assert.ThrowsAsync<IdempotencyConflictException>(() => second.Invoke(request, Inner));

            Assert.Equal(1, calls);
        }
    }
}
