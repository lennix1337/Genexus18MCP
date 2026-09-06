using System;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IdempotencyTimeoutTests
    {
        [Fact]
        public async Task DuplicateTimeoutReturnsInProgressWithoutExecuting()
        {
            const string id = "stuck-1";
            IdempotencyCache.Clear();
            IdempotencyCache.SetInflightWaitBudgetForTests(TimeSpan.FromMilliseconds(30));
            try
            {
                IdempotencyCache.BeginInflight(id);
                var started = DateTime.UtcNow;
                var result = await Task.Run(() => IdempotencyCache.TryServe(id));
                Assert.True((DateTime.UtcNow - started).TotalSeconds < 2);
                var payload = JObject.Parse(result);
                Assert.Equal("idempotency_in_progress", payload["code"]!.ToString());
                Assert.Null(IdempotencyCache.TryServe("other-id"));
            }
            finally
            {
                IdempotencyCache.AbortInflight(id);
                IdempotencyCache.ResetInflightWaitBudgetForTests();
                IdempotencyCache.Clear();
            }
        }
    }
}
