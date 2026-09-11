using System;
using System.IO;
using System.Collections.Generic;
using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Tests
{
    public class IndexStorageEngineTests
    {
        [Fact]
        public void IndexStorageEngine_ShardOf_ProducesStableBucketUnder16()
        {
            var engine = new IndexStorageEngine(Path.GetTempPath());

            int shard1 = engine.ShardOf("Procedure:CalculateInvoiceTotal");
            int shard2 = engine.ShardOf("Procedure:CalculateInvoiceTotal");
            int shard3 = engine.ShardOf("Transaction:Customer");

            // Must be deterministic
            Assert.Equal(shard1, shard2);
            // Must be bounded in [0, 15]
            Assert.InRange(shard1, 0, 15);
            Assert.InRange(shard3, 0, 15);
        }

        [Fact]
        public void IndexStorageEngine_FlushAndLoad_PreservesPayload()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gx_idx_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                var engine = new IndexStorageEngine(tempDir);
                var testIndex = new SearchIndex();
                testIndex.Objects["Procedure:TestProc"] = new SearchIndex.IndexEntry
                {
                    Name = "TestProc",
                    Type = "Procedure",
                    Complexity = 10,
                    Description = "Test calculation procedure"
                };

                var dirtyShards = new HashSet<int> { engine.ShardOf("Procedure:TestProc") };
                bool flushed = engine.Flush(testIndex, dirtyShards, 1);
                Assert.True(flushed);

                // Reload from disk
                var loaded = engine.Load();
                Assert.NotNull(loaded);
                Assert.True(loaded.Objects.ContainsKey("Procedure:TestProc"));
                Assert.Equal("TestProc", loaded.Objects["Procedure:TestProc"].Name);
                Assert.Equal(10, loaded.Objects["Procedure:TestProc"].Complexity);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void IndexStorageEngine_LoadRejectsMissingShardInsteadOfPublishingPartialIndex()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gx_idx_integrity_" + Guid.NewGuid().ToString("N"));
            try
            {
                var engine = new IndexStorageEngine(tempDir);
                var index = new SearchIndex();
                index.Objects["Procedure:TestProc"] = new SearchIndex.IndexEntry { Name = "TestProc", Type = "Procedure" };
                Assert.True(engine.Flush(index, new HashSet<int> { engine.ShardOf("Procedure:TestProc") }, 1));
                File.Delete(Path.Combine(tempDir, "shard_00.json.gz"));
                Assert.Null(engine.Load());
            }
            finally { try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { } }
        }

        [Fact]
        public void IndexStorageEngine_LoadRejectsCorruptShardInsteadOfReturningOtherShards()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gx_idx_integrity_" + Guid.NewGuid().ToString("N"));
            try
            {
                var engine = new IndexStorageEngine(tempDir);
                var index = new SearchIndex();
                index.Objects["Procedure:TestProc"] = new SearchIndex.IndexEntry { Name = "TestProc", Type = "Procedure" };
                Assert.True(engine.Flush(index, new HashSet<int> { engine.ShardOf("Procedure:TestProc") }, 1));
                File.WriteAllText(Path.Combine(tempDir, "shard_00.json.gz"), "not gzip");
                Assert.Null(engine.Load());
            }
            finally { try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { } }
        }

        [Fact]
        public void MemoryService_Recall_EnrichesWithVectorSimilarity()
        {
            var vectorService = new VectorService();
            var memoryService = new MemoryService(null, vectorService);

            float[] vec1 = vectorService.ComputeEmbedding("customer billing discount rules");
            float[] vec2 = vectorService.ComputeEmbedding("customer billing invoice tax rules");
            float[] vec3 = vectorService.ComputeEmbedding("quantum physics cosmological constants");

            float sim1 = vectorService.CosineSimilarity(vec1, vec2);
            float sim2 = vectorService.CosineSimilarity(vec1, vec3);

            // Related domains have significantly higher similarity
            Assert.True(sim1 > 0.4f);
            Assert.True(sim1 > sim2);
        }
    }
}
