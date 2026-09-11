using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public sealed class VersionedSnapshotBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public VersionedSnapshotBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_VersionedSnapshot_FullAndSingleShardFlush()
        {
            string kbPath = Path.Combine(Path.GetTempPath(), "gxmcp-slotbench-" + Guid.NewGuid().ToString("N"));
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            cache.SetFlushThrottleForTest(0);
            try
            {
                var entries = new List<SearchIndex.IndexEntry>(2000);
                for (int i = 0; i < 2000; i++)
                {
                    entries.Add(new SearchIndex.IndexEntry
                    {
                        Name = "SlotBench" + i,
                        Type = "Procedure",
                        Guid = "slot-guid-" + i,
                        Description = "deterministic snapshot benchmark entry " + i
                    });
                }

                cache.ReplaceAll(entries);
                var full = Stopwatch.StartNew();
                Assert.True(cache.FlushNow());
                full.Stop();

                cache.AddOrUpdateBatch(new[]
                {
                    new SearchIndex.IndexEntry
                    {
                        Name = "SlotBench0",
                        Type = "Procedure",
                        Guid = "slot-guid-0",
                        Description = "updated incremental entry"
                    }
                });
                var incremental = Stopwatch.StartNew();
                Assert.True(cache.FlushNow());
                incremental.Stop();

                string report = string.Format(
                    "\n=== VERSIONED_SNAPSHOT_BENCHMARK ===\nEntries: {0}\nFull publish: {1:F1} ms\nSingle-shard publish: {2:F1} ms\nCertified slot: {3}\n========================================",
                    entries.Count, full.Elapsed.TotalMilliseconds, incremental.Elapsed.TotalMilliseconds,
                    cache.CertifiedSlotPathForTest);
                _output.WriteLine(report);
                Console.WriteLine(report);
            }
            finally
            {
                cache.DeleteOnDiskSnapshot();
            }
        }
    }
}
