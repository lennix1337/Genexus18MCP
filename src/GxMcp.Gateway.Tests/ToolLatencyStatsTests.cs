using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Per-tool latency was previously uninstrumented — reads/writes had no timing at all.
    // These pin the aggregate: counts/avg/max per tool, ranked by total time, ignoring noise.
    [Collection("ToolLatencySerial")]
    public class ToolLatencyStatsTests
    {
        public ToolLatencyStatsTests() => ToolLatencyStats.ResetForTest();

        [Fact]
        public void Record_AggregatesCountAvgMaxPerTool()
        {
            ToolLatencyStats.Record("genexus_read", 100);
            ToolLatencyStats.Record("genexus_read", 300);
            ToolLatencyStats.Record("genexus_edit", 2000);

            var s = ToolLatencyStats.Summarize(topN: 10);
            Assert.Equal(3L, s["totalCalls"]!.ToObject<long>());

            var byTool = (Newtonsoft.Json.Linq.JArray)s["byTool"]!;
            // Ranked by total time — edit (2000) outranks read (400 total).
            Assert.Equal("genexus_edit", byTool[0]!["tool"]!.ToString());

            var read = System.Linq.Enumerable.First(byTool, t => t!["tool"]!.ToString() == "genexus_read");
            Assert.Equal(2L, read["count"]!.ToObject<long>());
            Assert.Equal(200L, read["avgMs"]!.ToObject<long>());
            Assert.Equal(300L, read["maxMs"]!.ToObject<long>());
        }

        [Fact]
        public void Record_IgnoresHeartbeatNoise()
        {
            ToolLatencyStats.Record("ping", 5);
            ToolLatencyStats.Record("heartbeat", 5);
            var s = ToolLatencyStats.Summarize();
            Assert.Equal(0L, s["totalCalls"]!.ToObject<long>());
        }

        [Fact]
        public void Summarize_Empty_ReturnsZero()
        {
            var s = ToolLatencyStats.Summarize();
            Assert.Equal(0L, s["totalCalls"]!.ToObject<long>());
        }

        [Fact]
        public void Record_ExposesPercentilesQueueWaitPayloadAndResultClass()
        {
            ToolLatencyStats.Record("genexus_read", 10, "success", queueWaitMs: 3, responseBytes: 120);
            ToolLatencyStats.Record("genexus_read", 30, "error", queueWaitMs: 5, responseBytes: 240);
            ToolLatencyStats.Record("genexus_read", 50, "timeout", queueWaitMs: 7, responseBytes: 0);

            var s = ToolLatencyStats.Summarize();
            Assert.Equal(5L, s["avgQueueWaitMs"]!.ToObject<long>());
            var read = ((Newtonsoft.Json.Linq.JArray)s["byTool"]!).Single(t => t!["tool"]!.ToString() == "genexus_read");
            Assert.Equal(30L, read["p50Ms"]!.ToObject<long>());
            Assert.Equal(50L, read["p95Ms"]!.ToObject<long>());
            Assert.Equal(120L, read["avgResponseBytes"]!.ToObject<long>());
            Assert.Equal(1L, read["resultClasses"]!["timeout"]!.ToObject<long>());
        }

        [Fact]
        public void Record_ExposesPhaseAndCacheBreakdown()
        {
            ToolLatencyStats.Record(
                "genexus_read",
                40,
                "success",
                queueWaitMs: 2,
                responseBytes: 400,
                startupMs: 10,
                sdkMs: 20,
                transformMs: 5,
                serializeMs: 3,
                cacheOutcome: "hit");

            var summary = ToolLatencyStats.Summarize();
            Assert.Equal(10L, summary["avgStartupMs"]!.ToObject<long>());
            Assert.Equal(20L, summary["avgSdkMs"]!.ToObject<long>());
            Assert.Equal(5L, summary["avgTransformMs"]!.ToObject<long>());
            Assert.Equal(3L, summary["avgSerializeMs"]!.ToObject<long>());
            var read = ((Newtonsoft.Json.Linq.JArray)summary["byTool"]!).Single(t => t!["tool"]!.ToString() == "genexus_read");
            Assert.Equal(1L, read["cacheOutcomes"]!["hit"]!.ToObject<long>());
        }
    }

    [CollectionDefinition("ToolLatencySerial", DisableParallelization = true)]
    public sealed class ToolLatencySerialCollection { }
}
