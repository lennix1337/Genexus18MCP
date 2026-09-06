using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace GxMcp.Benchmarks
{
    // Port of the graph query shape used by CallerGraphService. The benchmark is
    // intentionally SDK-free: it measures the cost removed from the hot path while
    // live-KB harnesses separately measure SDK edge extraction.
    [MemoryDiagnoser]
    [Config(typeof(SemanticGraphBenchmarkConfig))]
    public class SemanticGraphBenchmarks
    {
        [Params(1000, 10000, 50000)]
        public int N;

        private List<Node> _nodes = null!;
        private Dictionary<string, List<string>> _callers = null!;
        private string _target = "N0";

        [GlobalSetup]
        public void Setup()
        {
            _nodes = new List<Node>(N);
            for (int i = 0; i < N; i++)
            {
                _nodes.Add(new Node
                {
                    Name = "N" + i,
                    Calls = i == 0 ? new List<string>() : new List<string> { "N0" }
                });
            }
            _callers = BuildAdjacency(_nodes);
        }

        [Benchmark(Baseline = true)]
        public List<string> LegacyFullScan()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in _nodes)
            {
                if (node.Calls.Any(call => string.Equals(call, _target, StringComparison.OrdinalIgnoreCase)))
                    result.Add(node.Name);
            }
            return result.ToList();
        }

        [Benchmark]
        public List<string> RevisionedAdjacencyLookup()
        {
            return _callers.TryGetValue(_target, out var values)
                ? new List<string>(values)
                : new List<string>();
        }

        [Benchmark]
        public Dictionary<string, List<string>> RebuildAdjacency()
        {
            return BuildAdjacency(_nodes);
        }

        private static Dictionary<string, List<string>> BuildAdjacency(IEnumerable<Node> nodes)
        {
            var sets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                foreach (var callee in node.Calls)
                {
                    if (!sets.TryGetValue(callee, out var callers))
                    {
                        callers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        sets[callee] = callers;
                    }
                    callers.Add(node.Name);
                }
            }
            return sets.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        private sealed class Node
        {
            public string Name { get; set; } = string.Empty;
            public List<string> Calls { get; set; } = new List<string>();
        }
    }

    // Worktrees can contain several projects with the same filename. Running this
    // focused benchmark in-process avoids BenchmarkDotNet's project-file discovery
    // ambiguity while preserving the short, deterministic comparison used here.
    public sealed class SemanticGraphBenchmarkConfig : ManualConfig
    {
        public SemanticGraphBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
        }
    }
}
