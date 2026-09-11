using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public interface IIndexStorageEngine
    {
        int ShardOf(string storageKey);
        bool Flush(SearchIndex index, ISet<int> dirtyShards, long generation);
        SearchIndex Load();
        void DeleteOnDiskSnapshot();
    }

    public sealed class IndexStorageEngine : IIndexStorageEngine
    {
        public const int ShardCount = 16;
        private const int SchemaVersion = 1;
        private readonly string _storageDirectory;
        private readonly object _ioLock = new object();

        public IndexStorageEngine(string storageDirectory) { _storageDirectory = storageDirectory ?? Path.GetTempPath(); }

        public int ShardOf(string storageKey)
        {
            if (string.IsNullOrEmpty(storageKey)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in storageKey) { hash = (hash ^ char.ToUpperInvariant(c)) * 16777619; }
                return (int)(hash % ShardCount);
            }
        }

        private string GetShardFilePath(int shardId) => Path.Combine(_storageDirectory, $"shard_{shardId:D2}.json.gz");

        public bool Flush(SearchIndex index, ISet<int> dirtyShards, long generation)
        {
            if (index == null) return false;
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(_storageDirectory);
                    var buckets = Enumerable.Range(0, ShardCount).ToDictionary(i => i, _ => new List<SearchIndex.IndexEntry>());
                    foreach (var kvp in index.Objects)
                        if (kvp.Value != null) buckets[ShardOf(kvp.Key)].Add(kvp.Value);

                    // A manifest certifies all shards, so write all of them. This is the
                    // smallest safe behavior for callers that provide only a dirty subset.
                    foreach (int shardId in Enumerable.Range(0, ShardCount))
                    {
                        string path = GetShardFilePath(shardId);
                        string temp = path + $".tmp-{Guid.NewGuid():N}";
                        try
                        {
                            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            using (var gz = new GZipStream(fs, CompressionMode.Compress))
                            using (var sw = new StreamWriter(gz, Encoding.UTF8))
                            using (var jw = new JsonTextWriter(sw))
                                new JsonSerializer().Serialize(jw, buckets[shardId]);
                            AtomicReplace(temp, path);
                        }
                        finally { TryDelete(temp); }
                    }

                    string manifest = Path.Combine(_storageDirectory, "manifest.json");
                    string manifestTemp = manifest + $".tmp-{Guid.NewGuid():N}";
                    try
                    {
                        File.WriteAllText(manifestTemp, JsonConvert.SerializeObject(new
                        {
                            generation, flushedAt = DateTime.UtcNow, objectCount = index.Objects.Count,
                            shardCount = ShardCount, schemaVersion = SchemaVersion
                        }), new UTF8Encoding(false));
                        AtomicReplace(manifestTemp, manifest);
                    }
                    finally { TryDelete(manifestTemp); }
                    return true;
                }
                catch (Exception ex) { Logger.Error($"[INDEX-STORAGE] Flush failed: {ex.Message}"); return false; }
            }
        }

        public SearchIndex Load()
        {
            lock (_ioLock)
            {
                try
                {
                    if (!Directory.Exists(_storageDirectory)) return null;
                    string manifestPath = Path.Combine(_storageDirectory, "manifest.json");
                    if (!File.Exists(manifestPath)) return null;
                    var manifest = JsonConvert.DeserializeObject<StorageManifest>(File.ReadAllText(manifestPath));
                    if (manifest == null || manifest.SchemaVersion != SchemaVersion || manifest.ShardCount != ShardCount || manifest.ObjectCount < 0) return null;

                    var index = new SearchIndex();
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int id = 0; id < ShardCount; id++)
                    {
                        string path = GetShardFilePath(id);
                        if (!File.Exists(path)) return null;
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                        using (var sr = new StreamReader(gz, Encoding.UTF8))
                        {
                            var entries = new JsonSerializer().Deserialize<List<SearchIndex.IndexEntry>>(new JsonTextReader(sr));
                            if (entries == null) return null;
                            foreach (var entry in entries)
                            {
                                if (entry == null || string.IsNullOrEmpty(entry.Name) || string.IsNullOrEmpty(entry.Type)) return null;
                                string key = $"{entry.Type}:{entry.Name}";
                                if (ShardOf(key) != id || !keys.Add(key)) return null;
                                index.Objects[key] = entry;
                            }
                        }
                    }
                    return keys.Count == manifest.ObjectCount ? index : null;
                }
                catch (Exception ex) { Logger.Error($"[INDEX-STORAGE] Load failed: {ex.Message}"); return null; }
            }
        }

        private sealed class StorageManifest
        {
            public int SchemaVersion { get; set; }
            public int ShardCount { get; set; }
            public int ObjectCount { get; set; }
        }

        private static void AtomicReplace(string tempPath, string destinationPath)
        {
            if (File.Exists(destinationPath)) File.Replace(tempPath, destinationPath, null);
            else File.Move(tempPath, destinationPath);
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        public void DeleteOnDiskSnapshot()
        {
            lock (_ioLock)
            {
                try { if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, true); }
                catch (Exception ex) { Logger.Warn($"[INDEX-STORAGE] DeleteOnDiskSnapshot failed: {ex.Message}"); }
            }
        }
    }
}
