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
    public class MemoryAttributionTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _tempDir;

        public MemoryAttributionTests(ITestOutputHelper output)
        {
            _output = output;
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-mem-attr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        [Trait("Category", "MemoryProfile")]
        public void Measure_Retained_Heap_Per_Worker_Component()
        {
            const int objectCount = 2000;
            long MeasureHeap()
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                return GC.GetTotalMemory(true);
            }

            long h0 = MeasureHeap();

            // 1. SearchIndex Entries (without derived secondary indexes)
            var index = new SearchIndex();
            var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < objectCount; i++)
            {
                string type = (i % 4 == 0) ? "Procedure" : (i % 4 == 1 ? "WebPanel" : (i % 4 == 2 ? "Transaction" : "Attribute"));
                string name = $"Obj_{type}_{i:D4}";
                string key = $"{type}:{name}";
                string src = (i % 2 == 0)
                    ? $"// Calculation {i}\nFor Each Customer\n  Where CustomerId = &CustomerId\n  Call('ProcHelper_{i % 50}', &CustomerId, &Status)\n  &Amount = &Qty * &Price\nEndFor\n"
                    : null;

                var entry = new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString("D"),
                    EntityKey = key,
                    EntityTypeGuid = "84a12160-f59b-4ad7-a683-ea4481ac23e9",
                    Name = name,
                    Type = type,
                    Parent = "Folder_" + (i % 20),
                    ParentPath = "Root/ModuleA/Folder_" + (i % 20),
                    Module = "ModuleA",
                    BusinessDomain = "Sales",
                    LastUpdate = now,
                    FullSource = src,
                    FullSourcePart = src != null ? "Source" : null,
                    Calls = src != null ? new List<string> { $"ProcHelper_{i % 50}" } : new List<string>(),
                    Tables = src != null ? new List<string> { "Customer" } : new List<string>()
                };
                index.Objects[key] = entry;
            }
            SearchIndex.InternSharedStrings(index);
            long hAfterEntries = MeasureHeap();

            // 2. Derived secondary indexes (ChildrenByParent, GuidToKey, TypeIndex, DomainIndex, ByNameIndex, SourceTokenIndex)
            var cache = new IndexCacheService();
            cache.UpdateIndex(index);
            cache.EnsureSourceTokenIndex();
            long hAfterSecondary = MeasureHeap();

            // 3. SourceStoreService (compressed disk store + trigram index)
            var store = new SourceStoreService();
            store.SetStoreDirectoryForTest(_tempDir);
            foreach (var kv in index.Objects)
            {
                if (kv.Value.FullSource != null)
                    store.Put(kv.Value.Guid, "Source", kv.Value.FullSource, now, "v1");
            }
            long hAfterSourceStore = MeasureHeap();

            // 4. CallerGraphService
            var graph = new CallerGraphService(cache);
            var callers = graph.GetCallers("ProcHelper_1");
            long hAfterCallerGraph = MeasureHeap();

            // 5. Serialization peak (WriteToStream -> MemoryStream + FromJsonStream)
            long hAfterSerialize;
            using (var ms = new MemoryStream())
            {
                index.WriteToStream(ms);
                hAfterSerialize = MeasureHeap();
                ms.Position = 0;
                var roundTrip = SearchIndex.FromJsonStream(ms);
                GC.KeepAlive(roundTrip);
            }
            long hAfterDeserialize = MeasureHeap();

            _output.WriteLine($"Baseline heap:           {h0 / 1024.0:N1} KB");
            _output.WriteLine($"+ SearchIndex entries:   +{(hAfterEntries - h0) / 1024.0:N1} KB ({objectCount} objects)");
            _output.WriteLine($"+ Secondary + TokIndex:  +{(hAfterSecondary - hAfterEntries) / 1024.0:N1} KB (SourceTokenIndex keys={index.SourceTokenIndex?.Count ?? 0})");
            _output.WriteLine($"+ SourceStore (trigram): +{(hAfterSourceStore - hAfterSecondary) / 1024.0:N1} KB (records={store.StoredRecordCount})");
            _output.WriteLine($"+ CallerGraphService:    +{(hAfterCallerGraph - hAfterSourceStore) / 1024.0:N1} KB (sample callers={callers.Count})");
            _output.WriteLine($"+ WriteToStream buffer:  +{(hAfterSerialize - hAfterCallerGraph) / 1024.0:N1} KB");
            _output.WriteLine($"After FromJsonStream GC: {(hAfterDeserialize - h0) / 1024.0:N1} KB retained");

            GC.KeepAlive(index);
            GC.KeepAlive(store);
            GC.KeepAlive(graph);
        }
    }
}
