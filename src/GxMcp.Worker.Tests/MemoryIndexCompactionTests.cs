using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class MemoryIndexCompactionTests : IDisposable
    {
        private readonly string _tempDir;

        public MemoryIndexCompactionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-mem-compact-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void SourceStore_Uses_Compact_Numeric_Postings_And_Packed_Trigrams()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var trigramIndexField = typeof(SourceStoreService).GetField("_trigramIndex", flags);
            var trigramsByRecordField = typeof(SourceStoreService).GetField("_trigramsByRecord", flags);

            Assert.NotNull(trigramIndexField);
            Assert.NotNull(trigramsByRecordField);

            // Pins the compact representation (fails on main where both were ConcurrentDictionary<string, HashSet<string>>).
            Assert.Equal(typeof(ConcurrentDictionary<ulong, SourceStoreService.PostingList>), trigramIndexField.FieldType);
            Assert.Equal(typeof(ConcurrentDictionary<int, ulong[]>), trigramsByRecordField.FieldType);
        }

        [Fact]
        public void SourceStore_Compact_Index_Preserves_Search_And_Absent_Pruning_And_Reload()
        {
            var store = new SourceStoreService();
            store.SetStoreDirectoryForTest(_tempDir);

            var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<SearchIndex.IndexEntry>();

            for (int i = 0; i < 40; i++)
            {
                string guid = Guid.NewGuid().ToString("D");
                string body = i % 2 == 0
                    ? $"// Cálculo Padrão {i}\nFor Each Customer\n  Call('ProcessarFatura_{i}', &Id)\nEndFor\n"
                    : $"// Outro Objeto {i}\n&Msg = 'Operação Concluída'\n";

                Assert.True(store.Put(guid, "Source", body, now, "v1"));
                entries.Add(new SearchIndex.IndexEntry
                {
                    Guid = guid,
                    Name = $"Proc_{i}",
                    Type = "Procedure",
                    LastUpdate = now
                });
            }

            store.FlushCatalogForTest();
            store.ReloadCatalogForTest();

            var criteria = new SourceSearchCriteria { Pattern = "ProcessarFatura_10", MaxResults = 20 };
            var rx = new Regex("ProcessarFatura_10", RegexOptions.IgnoreCase);
            var hits = store.SearchStore(entries, criteria, rx);
            Assert.Single(hits);
            Assert.Equal("Proc_10", hits[0]["objectName"]?.ToString());

            // Unicode literal pruning
            Assert.False(store.ProvesLiteralAbsent(entries[0], "Source", "Padrão"));
            Assert.True(store.ProvesLiteralAbsent(entries[1], "Source", "ProcessarFatura"));
            Assert.False(store.ProvesLiteralAbsent(entries[1], "Source", "Concluída"));
        }

        [Fact]
        public void SourceStore_RemoveObject_Reclaims_Postings_And_Reuses_Numeric_RecordIds()
        {
            var store = new SourceStoreService();
            store.SetStoreDirectoryForTest(_tempDir);
            var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            string g1 = Guid.NewGuid().ToString("D");
            string g2 = Guid.NewGuid().ToString("D");
            string g3 = Guid.NewGuid().ToString("D");

            Assert.True(store.Put(g1, "Source", "UniqueAlphaToken_111", now, "v1"));
            Assert.True(store.Put(g2, "Source", "UniqueBetaToken_222", now, "v1"));
            Assert.Equal(1, store.RemoveObject(g1));
            Assert.True(store.Put(g3, "Source", "UniqueGammaToken_333", now, "v1"));

            Assert.Equal(2, store.StoredRecordCount);
            Assert.Equal(2, store.TrigramIndexedRecordCount);

            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Guid = g2, Name = "Beta", Type = "Procedure", LastUpdate = now },
                new SearchIndex.IndexEntry { Guid = g3, Name = "Gamma", Type = "Procedure", LastUpdate = now }
            };
            var hits = store.SearchStore(entries, new SourceSearchCriteria { Pattern = "UniqueGammaToken_333", MaxResults = 10 },
                new Regex("UniqueGammaToken_333", RegexOptions.IgnoreCase));
            Assert.Single(hits);
            Assert.Equal("Gamma", hits[0]["objectName"]?.ToString());
        }

        [Fact]
        public void TrigramExtractor_PackedTrigrams_Match_StringTrigrams()
        {
            string sample = "For Each Customer // Padrão_123\nEndFor";
            var strSet = TrigramExtractor.ExtractTrigrams(sample);
            var packed = TrigramExtractor.ExtractPackedTrigrams(sample);

            Assert.Equal(strSet.Count, packed.Length);
            foreach (ulong p in packed)
            {
                Assert.Contains(TrigramExtractor.UnpackTrigram(p), strSet);
            }
        }

        [Fact]
        public void SearchIndex_WriteToStream_RoundTrips_Without_String_Materialization()
        {
            var index = new SearchIndex();
            var key = "Procedure:ProcStreamTest";
            index.Objects[key] = new SearchIndex.IndexEntry
            {
                Guid = Guid.NewGuid().ToString("D"),
                Name = "ProcStreamTest",
                Type = "Procedure",
                FullSource = "For Each Customer\nEndFor"
            };

            using (var ms = new MemoryStream())
            {
                index.WriteToStream(ms);
                ms.Position = 0;
                var restored = SearchIndex.FromJsonStream(ms);
                Assert.NotNull(restored);
                Assert.True(restored.Objects.ContainsKey(key));
                Assert.Equal("ProcStreamTest", restored.Objects[key].Name);
                Assert.Equal("For Each Customer\nEndFor", restored.Objects[key].FullSource);
                Assert.False(restored.Objects[key].ShouldSerializeCalls());
                Assert.False(restored.Objects[key].ShouldSerializeTags());
            }
        }
    }
}
