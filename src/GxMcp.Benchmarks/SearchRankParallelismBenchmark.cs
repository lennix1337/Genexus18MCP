using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;

namespace GxMcp.Benchmarks
{
    // Calibrates SearchService.Search's ParallelScanThreshold (currently 256): the
    // ranker pipeline is measured both sequentially and with PLINQ (DOP=4, the cap
    // the worker uses) for candidate-set sizes 64..4096, so the crossover point —
    // where PLINQ stops being overhead and starts winning — is visible per row.
    //
    // The per-item work mirrors the real pipeline faithfully: CalculateSemanticScore
    // (name/description/keyword/tag/table scoring over the terms) + the unsafe
    // unrolled 128-dim CosineSimilarity from VectorService + the noise-type
    // short-circuit + the Score > 0 filter + ToList. The Worker project targets
    // net48 (GeneXus SDK) and can't be referenced from this net10.0 project, so the
    // two scoring functions are ported here verbatim (see GxMcp.Worker
    // Services/SearchService.cs and Services/VectorService.cs).
    //
    // Run: dotnet run -c Release --project src/GxMcp.Benchmarks -- --job short --filter '*SearchRankParallelismBenchmark*'
    // Interpret: whichever row has Plinq_Dop4 faster than Sequential (Baseline=1.00)
    // tells you the threshold for YOUR hardware; a PLINQ ratio > 1.00 means the
    // parallel overhead dominates and the set should stay sequential.
    [MemoryDiagnoser]
    public class SearchRankParallelismBenchmark
    {
        [Params(64, 128, 256, 512, 1024, 2048, 4096)]
        public int N;

        private List<IndexEntry> _entries = null!;
        private readonly float[] _queryEmbedding = new float[128];
        private readonly HashSet<string> _terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "customer", "invoice", "total"
        };

        [GlobalSetup]
        public void Setup()
        {
            // Query embedding: normalized random vector (same shape VectorService produces).
            FillNormalized(_queryEmbedding, new Random(7));

            _entries = new List<IndexEntry>(N);
            var rng = new Random(42);
            for (int i = 0; i < N; i++)
            {
                var entry = new IndexEntry
                {
                    Name = "Transaction" + i,
                    Type = "Transaction",
                    Description = "Handles customer invoice total for region " + (i % 7),
                    Keywords = new List<string> { "customer", "invoice" },
                    Tags = new List<string>(),
                    Tables = new List<string> { "Customer" + (i % 3) },
                    Calls = new List<string>(),
                    Embedding = new float[128]
                };
                FillNormalized(entry.Embedding, rng);
                _entries.Add(entry);
            }
        }

        [Benchmark(Baseline = true)]
        public List<Ranked> Sequential()
        {
            return RankPipeline(_entries, parallel: false);
        }

        [Benchmark]
        public List<Ranked> Plinq_Dop4()
        {
            return RankPipeline(_entries, parallel: true);
        }

        // Faithful shape of the SearchService parallel region:
        //   sourceSet → TypeFilter/DomainFilter wheres → ranker Select (semantic score
        //   + cosine) → noise short-circuit → Score > 0 filter → ToList.
        private List<Ranked> RankPipeline(IEnumerable<IndexEntry> sourceSet, bool parallel)
        {
            const int dop = 4;
            IEnumerable<IndexEntry> queryResults = parallel
                ? sourceSet.AsParallel().WithDegreeOfParallelism(dop)
                : sourceSet;

            queryResults = queryResults.Where(e => string.Equals(e.Type, "Transaction", StringComparison.OrdinalIgnoreCase));
            queryResults = queryResults.Where(e => string.Equals(e.BusinessDomain ?? "", "Finance", StringComparison.OrdinalIgnoreCase) || e.BusinessDomain == null);

            return queryResults
                .Select(entry =>
                {
                    int score = SemanticScore(entry, _terms);
                    float vectorScore = 0;
                    if (entry.Embedding != null)
                    {
                        vectorScore = CosineSimilarity(_queryEmbedding, entry.Embedding);
                    }
                    // Noise-type short-circuit + semantic floor, as in the real ranker.
                    if (score <= 0 && vectorScore < 0.45f)
                        return new Ranked { Score = -1 };
                    return new Ranked
                    {
                        Entry = entry,
                        Score = score + (int)(vectorScore * 1000),
                        VectorSimilarity = vectorScore
                    };
                })
                .Where(r => r != null)
                .Where(r => r.Score > 0)
                .ToList();
        }

        // Port of SearchService.CalculateSemanticScore (terms = HashSet, ordinal-ignore-case).
        private static int SemanticScore(IndexEntry entry, HashSet<string> terms)
        {
            int score = 0;
            string name = entry.Name ?? "";
            string desc = entry.Description ?? "";

            foreach (var term in terms)
            {
                if (name.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 10000;
                else if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 1000;
                else if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 500;

                if (desc.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 300;

                if (entry.Keywords != null && entry.Keywords.Contains(term, StringComparer.OrdinalIgnoreCase)) score += 800;
                if (entry.Tags != null && entry.Tags.Contains(term, StringComparer.OrdinalIgnoreCase)) score += 800;
                if (entry.Tables != null && entry.Tables.Contains(term, StringComparer.OrdinalIgnoreCase)) score += 400;
                if (entry.Calls != null && entry.Calls.Contains(term, StringComparer.OrdinalIgnoreCase)) score += 400;
            }
            return score;
        }

        // Port of VectorService.CosineSimilarity: unrolled dot product over 128-dim
        // normalized vectors (vectorService.ComputeEmbedding always emits 128 floats).
        private static unsafe float CosineSimilarity(float[] v1, float[] v2)
        {
            if (v1 == null || v2 == null || v1.Length != v2.Length) return 0f;

            float dotProduct = 0f;
            int length = v1.Length;

            fixed (float* p1 = v1)
            fixed (float* p2 = v2)
            {
                float* p1a = p1;
                float* p2a = p2;
                float* end = p1 + length;
                float* endUnrolled = p1 + (length - (length % 8));

                while (p1a < endUnrolled)
                {
                    dotProduct += (p1a[0] * p2a[0]) + (p1a[1] * p2a[1]) +
                                  (p1a[2] * p2a[2]) + (p1a[3] * p2a[3]) +
                                  (p1a[4] * p2a[4]) + (p1a[5] * p2a[5]) +
                                  (p1a[6] * p2a[6]) + (p1a[7] * p2a[7]);
                    p1a += 8;
                    p2a += 8;
                }
                while (p1a < end)
                {
                    dotProduct += (*p1a) * (*p2a);
                    p1a++;
                    p2a++;
                }
            }
            return dotProduct;
        }

        private static void FillNormalized(float[] vector, Random rng)
        {
            double magnitude = 0;
            for (int i = 0; i < vector.Length; i++)
            {
                float v = (float)(rng.NextDouble() * 2 - 1);
                vector[i] = v;
                magnitude += v * v;
            }
            magnitude = Math.Sqrt(magnitude);
            if (magnitude > 0)
            {
                for (int i = 0; i < vector.Length; i++) vector[i] /= (float)magnitude;
            }
        }

        // Mirrors SearchService.RankedResult (a class, so the per-item allocation
        // shape — one object per candidate — is preserved for MemoryDiagnoser).
        public class Ranked
        {
            public IndexEntry? Entry { get; set; }
            public int Score { get; set; }
            public float VectorSimilarity { get; set; }
        }

        // Minimal stand-in for SearchIndex.IndexEntry — only the fields the ranker reads.
        public class IndexEntry
        {
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string? BusinessDomain { get; set; }
            public List<string> Keywords { get; set; } = new List<string>();
            public List<string> Tags { get; set; } = new List<string>();
            public List<string> Tables { get; set; } = new List<string>();
            public List<string> Calls { get; set; } = new List<string>();
            public float[] Embedding { get; set; } = Array.Empty<float>();
        }
    }
}
