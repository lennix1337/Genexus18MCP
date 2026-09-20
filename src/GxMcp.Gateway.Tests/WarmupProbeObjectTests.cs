using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Regression coverage for the first-touch warm pass probe resolve.
    //
    // Live evidence (scratch gateway + real KB, GXMCP_VERBOSE_LOGS=1): the warmup logged
    // `gateway_warmup_list` but never `gateway_warmup_read|inspect|analyze`, because the
    // probe came from a List/Objects reply issued while the index was still building —
    // that reply is the IndexNotReady envelope, which carries no items. The previous
    // single-shot resolve dropped the whole pass there, so the agent's first read/inspect/
    // edit paid the one-time first-touch cost (measured 174ms / 78ms / 29ms vs ~1ms after).
    public class WarmupProbeObjectTests
    {
        // Verbatim shape captured from a live cold-start List/Objects reply.
        private const string IndexNotReadyEnvelope = @"{
            ""status"": ""Indexing"",
            ""code"": ""IndexNotReady"",
            ""indexStatus"": ""Cold"",
            ""freshness"": ""stale"",
            ""totalObjects"": 0,
            ""message"": ""Building index from cold start."",
            ""retryAfterMs"": 5000
        }";

        [Fact]
        public void ExtractWarmupProbeObjectName_ReturnsNull_ForIndexNotReadyEnvelope()
        {
            var envelope = JObject.Parse(IndexNotReadyEnvelope);

            Assert.Null(Program.ExtractWarmupProbeObjectName(envelope));
        }

        [Fact]
        public void ExtractWarmupProbeObjectName_ReadsResultsFromObjectReply()
        {
            var result = JObject.Parse(@"{""results"":[{""name"":""AddDeviceGroups""}]}");

            Assert.Equal("AddDeviceGroups", Program.ExtractWarmupProbeObjectName(result));
        }

        [Fact]
        public void ExtractWarmupProbeObjectName_ReadsObjectsFromObjectReply()
        {
            var result = JObject.Parse(@"{""objects"":[{""name"":""Customer""}]}");

            Assert.Equal("Customer", Program.ExtractWarmupProbeObjectName(result));
        }

        [Fact]
        public void ExtractWarmupProbeObjectName_ReadsTopLevelArrayReply()
        {
            var result = JArray.Parse(@"[{""name"":""Invoice""}]");

            Assert.Equal("Invoice", Program.ExtractWarmupProbeObjectName(result));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(@"{""results"":[]}")]
        [InlineData(@"{""results"":[{""type"":""Procedure""}]}")]
        [InlineData(@"{""results"":[{""name"":""""}]}")]
        public void ExtractWarmupProbeObjectName_ReturnsNull_WhenNoListableObject(string? json)
        {
            JToken? result = json == null ? null : JToken.Parse(json);

            Assert.Null(Program.ExtractWarmupProbeObjectName(result));
        }

        [Fact]
        public async Task AwaitWarmupProbeObjectAsync_RetriesUntilAListableObjectAppears()
        {
            int calls = 0;
            var retries = new List<int>();

            string? name = await Program.AwaitWarmupProbeObjectAsync(
                () =>
                {
                    calls++;
                    // Index not ready for the first two probes — the live cold-start case.
                    return Task.FromResult<string?>(calls < 3 ? null : "AddDeviceGroups");
                },
                maxAttempts: 5,
                retryDelayMs: 0,
                onRetry: retries.Add);

            Assert.Equal("AddDeviceGroups", name);
            Assert.Equal(3, calls);
            Assert.Equal(new[] { 1, 2 }, retries);
        }

        [Fact]
        public async Task AwaitWarmupProbeObjectAsync_StopsAtMaxAttempts_WhenNeverListable()
        {
            int calls = 0;

            string? name = await Program.AwaitWarmupProbeObjectAsync(
                () => { calls++; return Task.FromResult<string?>(null); },
                maxAttempts: 4,
                retryDelayMs: 0);

            Assert.Null(name);
            Assert.Equal(4, calls);
        }

        [Fact]
        public async Task AwaitWarmupProbeObjectAsync_TreatsAThrowingResolveAsAMiss()
        {
            int calls = 0;

            string? name = await Program.AwaitWarmupProbeObjectAsync(
                () =>
                {
                    calls++;
                    if (calls == 1) throw new InvalidOperationException("worker busy");
                    return Task.FromResult<string?>("Invoice");
                },
                maxAttempts: 3,
                retryDelayMs: 0);

            Assert.Equal("Invoice", name);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task AwaitWarmupProbeObjectAsync_ReturnsNull_ForMissingResolveDelegate()
        {
            Assert.Null(await Program.AwaitWarmupProbeObjectAsync(null!));
        }

        [Fact]
        public async Task RunFirstTouchWarmOnceAsync_WarmsOnlyOncePerGateway()
        {
            Program.ResetFirstTouchWarmForTest();
            try
            {
                int resolves = 0;
                int warmPasses = 0;
                var warmed = new List<string>();

                Func<Task<string?>> resolve = () => { resolves++; return Task.FromResult<string?>("AddDeviceGroups"); };
                Func<string, Task> warmPass = name => { warmPasses++; warmed.Add(name); return Task.CompletedTask; };

                await Program.RunFirstTouchWarmOnceAsync(resolve, null, warmPass);
                // The index-bootstrap trigger races the fast path for the same pass; the
                // loser must return without resolving or warming again.
                await Program.RunFirstTouchWarmOnceAsync(resolve, null, warmPass);

                Assert.Equal(1, resolves);
                Assert.Equal(1, warmPasses);
                Assert.Equal(new[] { "AddDeviceGroups" }, warmed);
            }
            finally
            {
                Program.ResetFirstTouchWarmForTest();
            }
        }

        [Fact]
        public async Task RunFirstTouchWarmOnceAsync_ClaimsThePass_EvenWhenNoProbeIsEverListable()
        {
            Program.ResetFirstTouchWarmForTest();
            Program.WarmupProbeRetryDelayMsForTest = 0;
            try
            {
                int resolves = 0;
                int warmPasses = 0;
                var waiting = new List<int>();

                await Program.RunFirstTouchWarmOnceAsync(
                    () => { resolves++; return Task.FromResult<string?>(null); },
                    () => waiting.Add(1),
                    _ => { warmPasses++; return Task.CompletedTask; });

                // maxAttempts retries then gives up quietly; a second caller must not start
                // another 40-attempt loop.
                await Program.RunFirstTouchWarmOnceAsync(
                    () => { resolves++; return Task.FromResult<string?>(null); },
                    () => waiting.Add(1),
                    _ => { warmPasses++; return Task.CompletedTask; });

                Assert.Equal(Program.WarmupProbeAttemptsForTest, resolves);
                Assert.Equal(0, warmPasses);
                Assert.Single(waiting);
            }
            finally
            {
                Program.WarmupProbeRetryDelayMsForTest = null;
                Program.ResetFirstTouchWarmForTest();
            }
        }

        [Fact]
        public void BuildWarmupCommands_GeneratesCanonicalWorkerCommandsWithTarget()
        {
            var commands = Program.BuildWarmupCommands("AddDeviceGroups");

            Assert.Equal(6, commands.Count);

            // 1. Structure read
            var (t1, c1) = commands[0];
            Assert.Equal("genexus_read", t1);
            Assert.Equal("Read", c1["module"]?.ToString());
            Assert.Equal("ExtractSource", c1["action"]?.ToString());
            Assert.Equal("AddDeviceGroups", c1["target"]?.ToString());
            Assert.Equal("Structure", c1["part"]?.ToString());
            Assert.Equal("mcp", c1["client"]?.ToString());

            // 2. Source read
            var (t2, c2) = commands[1];
            Assert.Equal("genexus_read", t2);
            Assert.Equal("Read", c2["module"]?.ToString());
            Assert.Equal("ExtractSource", c2["action"]?.ToString());
            Assert.Equal("AddDeviceGroups", c2["target"]?.ToString());
            Assert.Equal("Source", c2["part"]?.ToString());
            Assert.Equal("mcp", c2["client"]?.ToString());

            // 3. Inspect (routes to Analyze/GetConversionContext)
            var (t3, c3) = commands[2];
            Assert.Equal("genexus_inspect", t3);
            Assert.Equal("Analyze", c3["module"]?.ToString());
            Assert.Equal("GetConversionContext", c3["action"]?.ToString());
            Assert.Equal("AddDeviceGroups", c3["target"]?.ToString());
            Assert.Equal("mcp", c3["client"]?.ToString());

            // 4. Linter (routes to Linter/linter)
            var (t4, c4) = commands[3];
            Assert.Equal("genexus_analyze", t4);
            Assert.Equal("Linter", c4["module"]?.ToString());
            Assert.Equal("linter", c4["action"]?.ToString());
            Assert.Equal("AddDeviceGroups", c4["target"]?.ToString());
            Assert.Equal("mcp", c4["client"]?.ToString());

            // 5. Callers (routes to Analyze/FindCallerSites)
            var (t5, c5) = commands[4];
            Assert.Equal("genexus_analyze", t5);
            Assert.Equal("Analyze", c5["module"]?.ToString());
            Assert.Equal("FindCallerSites", c5["action"]?.ToString());
            Assert.Equal("AddDeviceGroups", c5["target"]?.ToString());
            Assert.Equal("mcp", c5["client"]?.ToString());

            // 6. Source search (routes to Search/SearchSource). The first Source search
            // builds the worker's KB-wide source-scan cache (~2.7s cold); warming it keeps
            // that cost out of the agent's first search.
            var (t6, c6) = commands[5];
            Assert.Equal("genexus_search_source", t6);
            Assert.Equal("Search", c6["module"]?.ToString());
            Assert.Equal("SearchSource", c6["action"]?.ToString());
            Assert.Equal("AddDeviceGroups", c6["pattern"]?.ToString());
            Assert.Equal(1, c6["maxResults"]?.ToObject<int>());
            Assert.Equal("mcp", c6["client"]?.ToString());
        }
    }
}
