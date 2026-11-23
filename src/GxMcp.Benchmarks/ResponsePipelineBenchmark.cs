using System;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;

namespace GxMcp.Benchmarks
{
    [Config(typeof(ResponsePipelineBenchmarkConfig))]
    [MemoryDiagnoser]
    public class ResponsePipelineBenchmark
    {
        private JObject _underBudget100Rows = null!;
        private JObject _overBudget400Rows = null!;
        private JObject _stripNullsTemplate = null!;
        private JObject _cacheEnvelope40Kb = null!;
        private SemanticCacheStore _warmCache = null!;
        private MethodInfo _truncateMethod = null!;
        private int _cacheSeq;

        [GlobalSetup]
        public void Setup()
        {
            _underBudget100Rows = BuildRowsPayload(100, includeNulls: true);
            _overBudget400Rows = BuildRowsPayload(400, includeNulls: false);
            _stripNullsTemplate = BuildRowsPayload(100, includeNulls: true);

            _cacheEnvelope40Kb = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["result"] = new JObject
                {
                    ["resultType"] = "complete",
                    ["isError"] = false,
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = _underBudget100Rows.ToString(Newtonsoft.Json.Formatting.None)
                        }
                    }
                }
            };

            long tick = 1000;
            _warmCache = new SemanticCacheStore(256, TimeSpan.FromMinutes(30), () => tick);
            for (int i = 0; i < 256; i++)
            {
                _warmCache.Set($"kb|genexus_read:init-{i}", new JObject { ["ok"] = i });
            }

            _truncateMethod = typeof(GxMcp.Gateway.Program).GetMethod(
                "TruncateResponseIfNeeded",
                BindingFlags.NonPublic | BindingFlags.Static)!;
        }

        private static JObject BuildRowsPayload(int rows, bool includeNulls)
        {
            var arr = new JArray();
            for (int i = 0; i < rows; i++)
            {
                var row = new JObject
                {
                    ["guid"] = $"00000000-0000-0000-0000-{i:D12}",
                    ["name"] = $"CustomerInvoiceProcedure{i}",
                    ["type"] = i % 2 == 0 ? "Procedure" : "Transaction",
                    ["path"] = $"Root Module/Billing/CustomerInvoiceProcedure{i}",
                    ["parentPath"] = "Root Module/Billing",
                    ["description"] = "Processes billing and invoice records for customer account " + i,
                    ["lastUpdate"] = "2026-11-23T12:00:00Z"
                };
                if (includeNulls)
                {
                    row["parm"] = JValue.CreateNull();
                    row["snippet"] = JValue.CreateNull();
                    row["table"] = JValue.CreateNull();
                }
                arr.Add(row);
            }

            return new JObject
            {
                ["count"] = rows,
                ["total"] = rows,
                ["hasMore"] = false,
                ["results"] = arr
            };
        }

        [Benchmark]
        public JToken Truncate_UnderBudget_100Rows()
        {
            return (JToken)_truncateMethod.Invoke(null, new object?[] { _underBudget100Rows, "genexus_query" })!;
        }

        [Benchmark]
        public JToken Truncate_OverBudget_400Rows()
        {
            var copy = (JObject)_overBudget400Rows.DeepClone();
            return (JToken)_truncateMethod.Invoke(null, new object?[] { copy, "genexus_query" })!;
        }

        [Benchmark]
        public JObject StripNulls_100Rows()
        {
            var copy = (JObject)_stripNullsTemplate.DeepClone();
            McpRouter.StripNulls(copy);
            return copy;
        }

        [Benchmark]
        public int SemanticCache_Set_AtCap_40KB()
        {
            int seq = ++_cacheSeq;
            _warmCache.Set($"kb|genexus_query:seq-{seq}", _cacheEnvelope40Kb);
            return seq;
        }
    }

    public sealed class ResponsePipelineBenchmarkConfig : ManualConfig
    {
        public ResponsePipelineBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
        }
    }
}
