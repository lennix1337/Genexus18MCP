using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;

namespace GxMcp.Benchmarks
{
    [Config(typeof(ToolCallHotPathBenchmarkConfig))]
    [MemoryDiagnoser]
    public class ToolCallHotPathBenchmark
    {
        private JObject _lifecycleArgs = null!;
        private JObject _editArgs = null!;
        private JObject _queryArgs = null!;

        [GlobalSetup]
        public void Setup()
        {
            _lifecycleArgs = new JObject
            {
                ["action"] = "build",
                ["target"] = "CustomerLoad",
                ["wait_until_done"] = true
            };
            _editArgs = new JObject
            {
                ["name"] = "CustomerLoad",
                ["part"] = "Source",
                ["mode"] = "patch",
                ["oldText"] = "For each Customer",
                ["newText"] = "For each Customer order CustomerId"
            };
            _queryArgs = new JObject
            {
                ["query"] = "Customer",
                ["typeFilter"] = "Transaction",
                ["limit"] = 50
            };

            // Warm schema loading before measuring per-call hot path.
            _ = GatewayArgsValidator.Validate("genexus_lifecycle", _lifecycleArgs);
            _ = GatewayArgsValidator.Validate("genexus_edit", _editArgs);
            _ = GatewayArgsValidator.Validate("genexus_query", _queryArgs);
            _ = GxMcp.Gateway.Program.ToolDeclaresOutputSchema("genexus_query");
        }

        [Benchmark]
        public bool Validate_GenexusLifecycle_ValidEnum()
            => GatewayArgsValidator.Validate("genexus_lifecycle", _lifecycleArgs).Ok;

        [Benchmark]
        public bool Validate_GenexusEdit_ValidArgs()
            => GatewayArgsValidator.Validate("genexus_edit", _editArgs).Ok;

        [Benchmark]
        public bool Validate_GenexusQuery_ValidArgs()
            => GatewayArgsValidator.Validate("genexus_query", _queryArgs).Ok;

        [Benchmark]
        public bool ToolDeclaresOutputSchema_Miss()
            => GxMcp.Gateway.Program.ToolDeclaresOutputSchema("genexus_query");

        [Benchmark]
        public bool ToolDeclaresOutputSchema_Hit()
            => GxMcp.Gateway.Program.ToolDeclaresOutputSchema("genexus_lifecycle");
    }

    public sealed class ToolCallHotPathBenchmarkConfig : ManualConfig
    {
        public ToolCallHotPathBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
        }
    }
}
