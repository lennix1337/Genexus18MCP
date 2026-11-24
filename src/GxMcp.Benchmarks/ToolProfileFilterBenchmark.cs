using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Newtonsoft.Json.Linq;
using System.IO;
using GxMcp.Gateway;

namespace GxMcp.Benchmarks
{
    [Config(typeof(ToolProfileFilterBenchmarkConfig))]
    [MemoryDiagnoser]
    public class ToolProfileFilterBenchmark
    {
        private JArray _tools = null!;

        [GlobalSetup]
        public void Setup()
        {
            string candidate = Path.Combine(
                Path.GetDirectoryName(typeof(GxMcp.Gateway.Program).Assembly.Location)!,
                "tool_definitions.json");
            if (!File.Exists(candidate))
            {
                candidate = Path.Combine("..", "..", "..", "..", "GxMcp.Gateway", "tool_definitions.json");
            }
            string json = File.ReadAllText(candidate);
            _tools = JArray.Parse(json);
            ToolProfileFilter.InvalidateCache();
            _ = ToolProfileFilter.GetOrCreateFiltered(_tools, "all");
        }

        [Benchmark(Baseline = true)]
        public JArray FilterCore_Uncached()
        {
            return ToolProfileFilter.Filter(_tools, "core");
        }

        [Benchmark]
        public JArray FilterAuthoring_Uncached()
        {
            return ToolProfileFilter.Filter(_tools, "authoring");
        }

        [Benchmark]
        public JArray FilterAll_Uncached()
        {
            return ToolProfileFilter.Filter(_tools, "all");
        }

        [Benchmark]
        public JArray GetOrCreateFiltered_All()
        {
            return ToolProfileFilter.GetOrCreateFiltered(_tools, "all");
        }
    }

    public sealed class ToolProfileFilterBenchmarkConfig : ManualConfig
    {
        public ToolProfileFilterBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
        }
    }
}
