using System;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Services.Structure;

namespace GxMcp.Worker.Services
{
    public class IndexCacheService
    {
        private SearchIndex _index;
        private string _indexPath;
        private BuildService _buildService;
        private bool _initialized = false;
        private readonly object _lock = new object();
        private DateTime _lastFlushTime = DateTime.MinValue;
        private bool _savingInProgress = false;
        private readonly VectorService _vectorService = new VectorService();

        public IndexCacheService()
        {
            _indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
        }

        public void SetBuildService(BuildService bs) { _buildService = bs; }
        public KbService KbService => _buildService?.KbService;

         public bool IsIndexMissing
         {
             get
             {
                 try
                 {
                     if (string.IsNullOrEmpty(_indexPath)) return true;
                     if (!File.Exists(_indexPath)) return true;
                     
                     var index = GetIndex();
                     return index == null || index.Objects.Count == 0;
                 }
                 catch { return true; }
             }
         }
 
         public bool IsScanning => KbService?.IsIndexing ?? false;

        private void EnsureInitialized()
        {
            if (_initialized) return;
            try
            {
                string kbPath = _buildService.GetKBPath();
                if (!string.IsNullOrEmpty(kbPath)) Initialize(kbPath);
            }
            catch { }
        }

        public void Initialize(string kbPath)
        {
            if (string.IsNullOrEmpty(kbPath)) return;
            if (kbPath.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase)) kbPath = Path.GetDirectoryName(kbPath);

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string cacheDir = Path.Combine(localAppData, "GxMcp", "Cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string hash = GetHash(kbPath);
                _indexPath = Path.Combine(cacheDir, string.Format("index_{0}.json", hash));
                _initialized = true;
                Logger.Info(string.Format("IndexCache initialized: {0}", _indexPath));

                // PERFORMANCE: Pro-active loading in background
                Task.Run(() => GetIndex());
            }
            catch (Exception ex) { Logger.Error("IndexCache Init Error: " + ex.Message); }
        }

        private string GetHash(string input)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input.ToLower().Trim()));
                return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 16);
            }
        }

        private void BuildParentIndex(SearchIndex index)
        {
            var byParent = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var entry in index.Objects.Values)
            {
                string parent = entry.ParentPath ?? entry.Parent ?? "";
                if (!byParent.TryGetValue(parent, out var list))
                {
                    list = new System.Collections.Generic.List<SearchIndex.IndexEntry>();
                    byParent[parent] = list;
                }
                
                // PERFORMANCE: Since we are iterating dictionary values, there are NO duplicates. 
                // Using .Add() directly changes this from an O(N^2) operation to O(N).
                list.Add(entry);
            }
            index.ChildrenByParent = byParent;
        }

        private string GetEntryStorageKey(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;
            if (string.Equals(entry.Type, "Folder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Type, "Module", StringComparison.OrdinalIgnoreCase))
            {
                string scopedPath = entry.Path ?? entry.Name ?? string.Empty;
                return string.Format("{0}:{1}", entry.Type ?? string.Empty, scopedPath);
            }

            return string.Format("{0}:{1}", entry.Type ?? string.Empty, entry.Name ?? string.Empty);
        }

        private void NormalizeLegacyHierarchy(SearchIndex index)
        {
            if (index?.Objects == null) return;

            foreach (var entry in index.Objects.Values)
            {
                if (string.IsNullOrWhiteSpace(entry.ParentPath))
                {
                    entry.ParentPath = InferLegacyParentPath(entry);
                }

                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    entry.Path = InferLegacyPath(entry);
                }
            }
        }

        private string InferLegacyParentPath(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;

            if (string.IsNullOrWhiteSpace(entry.Parent) ||
                string.Equals(entry.Parent, "Root Module", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(entry.Module) &&
                string.Equals(entry.Parent, entry.Module, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Module;
            }

            // When an entry lives inside a Folder under a Module, qualify the
            // folder name with its parent module to preserve hierarchy. Without
            // this, folders sharing names across modules (e.g. "Procs") collapse
            // into a single bucket in the parent index.
            if (!string.IsNullOrWhiteSpace(entry.Module))
            {
                return string.Format("{0}/{1}", entry.Module, entry.Parent);
            }

            return entry.Parent;
        }

        private string InferLegacyPath(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return string.Empty;

            var parentPath = entry.ParentPath ?? InferLegacyParentPath(entry);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return entry.Name ?? string.Empty;
            }

            return string.Format("{0}/{1}", parentPath, entry.Name ?? string.Empty);
        }

        private void AddOrUpdateEntryInParentIndex(System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SearchIndex.IndexEntry>> byParent, SearchIndex.IndexEntry entry)
        {
            string parent = entry.ParentPath ?? entry.Parent ?? "";
            var list = byParent.GetOrAdd(parent, _ => new System.Collections.Generic.List<SearchIndex.IndexEntry>());
            
            lock (list)
            {
                string entryKey = GetEntryStorageKey(entry);
                if (!list.Any(e => string.Equals(GetEntryStorageKey(e), entryKey, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(entry);
                }
            }
        }

        private (string ParentName, string ParentPath, string Path, string ModuleName) ResolveHierarchy(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            string parentName = string.Empty;
            string moduleName = null;
            var parentSegments = new System.Collections.Generic.List<string>();

            try
            {
                dynamic currentParent = obj?.Parent;
                bool isImmediateParent = true;

                while (currentParent != null)
                {
                    try
                    {
                        if (currentParent.Guid == obj.Guid)
                        {
                            break;
                        }
                    }
                    catch
                    {
                    }

                    string parentTypeName = null;
                    try { parentTypeName = currentParent.TypeDescriptor?.Name; } catch { }

                    if (string.Equals(parentTypeName, "DesignModel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isImmediateParent)
                        {
                            parentName = "Root Module";
                        }
                        break;
                    }

                    bool isContainer =
                        currentParent is global::Artech.Architecture.Common.Objects.Module ||
                        currentParent is global::Artech.Architecture.Common.Objects.Folder;

                    if (!isContainer)
                    {
                        currentParent = currentParent.Parent;
                        isImmediateParent = false;
                        continue;
                    }

                    string currentName = null;
                    try { currentName = currentParent.Name; } catch { }

                    if (!string.IsNullOrWhiteSpace(currentName))
                    {
                        parentSegments.Insert(0, currentName);
                        if (isImmediateParent)
                        {
                            parentName = currentName;
                        }

                        if (moduleName == null &&
                            currentParent is global::Artech.Architecture.Common.Objects.Module)
                        {
                            moduleName = currentName;
                        }
                    }

                    currentParent = currentParent.Parent;
                    isImmediateParent = false;
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(moduleName))
            {
                try
                {
                    if (obj?.Module != null && obj.Module.Guid != obj.Guid)
                    {
                        moduleName = obj.Module.Name;
                    }
                }
                catch
                {
                }
            }

            string parentPath = string.Join("/", parentSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
            string path = parentPath;
            if (!string.IsNullOrWhiteSpace(obj?.Name))
            {
                path = string.IsNullOrEmpty(parentPath) ? obj.Name : parentPath + "/" + obj.Name;
            }

            return (parentName, parentPath, path, moduleName);
        }

        public SearchIndex GetIndex()
        {
            if (_index != null) return _index;
            EnsureInitialized();

            lock (_lock)
            {
                if (_index != null) return _index;
                try
                {
                    if (File.Exists(_indexPath))
                    {
                        Logger.Debug(string.Format("Loading index from disk: {0}", _indexPath));
                        string json = File.ReadAllText(_indexPath);
                        _index = SearchIndex.FromJson(json);
                        NormalizeLegacyHierarchy(_index);
                        BuildParentIndex(_index);
                        Logger.Info(string.Format("Index loaded. Objects: {0}", _index.Objects.Count));
                    }
                }
                catch (Exception ex) { Logger.Error("Load Index Error: " + ex.Message); }

                if (_index == null) _index = new SearchIndex();
                return _index;
            }
        }

        public void UpdateIndex(SearchIndex index)
        {
            lock (_lock)
            {
                BuildParentIndex(index);
                _index = index;
            }
            // Fire and forget save to disk with throttling
            if (!_savingInProgress && (DateTime.Now - _lastFlushTime).TotalSeconds > 10)
            {
                Task.Run(() => FlushToDisk());
            }
        }

        private void FlushToDisk()
        {
            if (_savingInProgress) return;
            
            SearchIndex snapshot = null;
            lock (_lock)
            {
                if (_savingInProgress) return;
                if (_index == null) return;
                
                _savingInProgress = true;
                // ELITE: Snapshot the index to serialize it OUTSIDE the lock.
                // ConcurrentDictionary iteration is thread-safe and provides a consistent snapshot view.
                snapshot = _index; 
            }

            try
            {
                string dir = Path.GetDirectoryName(_indexPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                
                var settings = new Newtonsoft.Json.JsonSerializerSettings { 
                    NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                    DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore,
                    Formatting = Newtonsoft.Json.Formatting.None
                };

                // Perform heavy serialization OUTSIDE the lock
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(snapshot, settings);
                
                // Perform I/O OUTSIDE the lock
                File.WriteAllText(_indexPath, json);
                
                Logger.Info($"[INDEX-SAVE] Index flushed: {json.Length / 1024} KB saved.");
            }
            catch (Exception ex) { Logger.Error("Flush Error: " + ex.Message); }
            finally { 
                _savingInProgress = false; 
                _lastFlushTime = DateTime.Now;
            }
        }

        private string GetJsonHash(string json)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
                return BitConverter.ToString(bytes).Replace("-", "");
            }
        }

        public void UpdateEntry(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            var index = GetIndex();
            var hierarchy = ResolveHierarchy(obj);

            var entry = new SearchIndex.IndexEntry
            {
                Guid = obj.Guid.ToString(),
                Name = obj.Name,
                Type = obj.TypeDescriptor.Name,
                Description = obj.Description,
                Parent = hierarchy.ParentName,
                ParentPath = hierarchy.ParentPath,
                Path = hierarchy.Path,
                Module = hierarchy.ModuleName
            };

            if (obj is global::Artech.Genexus.Common.Objects.Attribute attr)
            {
                entry.DataType = attr.Type.ToString();
                entry.Length = attr.Length;
                entry.Decimals = attr.Decimals;
                entry.IsFormula = attr.Formula != null;
            }
            else if (obj is global::Artech.Genexus.Common.Objects.Table tbl)
            {
                entry.RootTable = tbl.Name;
                try {
                    var children = new Newtonsoft.Json.Linq.JArray();
                    dynamic dStructure = ((dynamic)tbl).TableStructure;
                    if (dStructure != null && dStructure.Attributes != null) {
                        foreach (dynamic tableAttr in dStructure.Attributes) 
                            children.Add(VisualStructureMapper.MapAttribute(tableAttr));
                    }
                    entry.SourceSnippet = children.ToString(Newtonsoft.Json.Formatting.None);
                } catch { }
            }
            else if (obj is global::Artech.Genexus.Common.Objects.Transaction trn)
            {
                entry.RootTable = trn.Structure.Root.Name;
                try { entry.ParmRule = trn.Rules.Source.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("parm(", StringComparison.OrdinalIgnoreCase)); } catch { }
            }
            else if (obj is global::Artech.Genexus.Common.Objects.SDT sdt)
            {
                try {
                    entry.SourceSnippet = StructureParser.SerializeToText(obj);
                    entry.Complexity = entry.SourceSnippet?.Split('\n').Length ?? 0;
                } catch (Exception ex) {
                    Logger.Error($"SDT index snippet failed for {obj.Name}: {ex.Message}");
                }
            }

            // Calculate Complexity for Procedures/DataProviders
            if (obj is global::Artech.Genexus.Common.Objects.Procedure || obj is global::Artech.Genexus.Common.Objects.DataProvider)
            {
                try {
                    dynamic sourcePart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p is ISource);
                    if (sourcePart != null) {
                        string src = sourcePart.Source ?? "";
                        entry.Complexity = src.Split('\n').Length;
                    }
                } catch { }
            }

            string key = GetEntryStorageKey(entry);
            
            // Compute Embedding
            string semanticText = $"{entry.Name} {entry.Type} {entry.Description} {entry.RootTable} {entry.ParmRule}";
            entry.Embedding = _vectorService.ComputeEmbedding(semanticText);

            // Atomic update using ConcurrentDictionary
            index.Objects.AddOrUpdate(key, entry, (k, existing) => entry);
            if (index.ChildrenByParent != null)
            {
                AddOrUpdateEntryInParentIndex(index.ChildrenByParent, entry);
            }

            // ENRICHMENT: Dependencies (Calls and Tables) - ASYNCHRONOUS BACKGROUND
            Guid objGuid = obj.Guid;
            Program.BackgroundQueue.Enqueue(() => {
                try {
                    var kb = _buildService.KbService?.GetKB();
                    if (kb == null) return;
                    var bgObj = kb.DesignModel.Objects.Get(objGuid);
                    if (bgObj == null) return;

                    var references = bgObj.GetReferences();
                    if (references != null)
                    {
                        bool changed = false;
                        foreach (dynamic reference in references)
                        {
                            try
                            {
                                dynamic targetKey = reference.To;
                                if (targetKey == null) continue;
                                string targetName = targetKey.Name;
                                if (string.IsNullOrEmpty(targetName)) {
                                    string keyStr = targetKey.ToString();
                                    targetName = keyStr.Contains(":") ? keyStr.Split(':')[1] : keyStr;
                                }
                                if (string.IsNullOrEmpty(targetName)) continue;

                                string targetType = targetKey.TypeDescriptor.Name;
                                if (targetType == "Attribute" || targetType == "Table") {
                                    if (!entry.Tables.Contains(targetName)) { entry.Tables.Add(targetName); changed = true; }
                                } else {
                                    if (!entry.Calls.Contains(targetName)) { entry.Calls.Add(targetName); changed = true; }
                                }

                                // Phase 1: Inverted Index (CalledBy)
                                string targetIndexKey = $"{targetType}:{targetName}";
                                if (index.Objects.TryGetValue(targetIndexKey, out var targetEntry)) {
                                    lock (targetEntry.CalledBy) {
                                        if (!targetEntry.CalledBy.Contains(entry.Name)) {
                                            targetEntry.CalledBy.Add(entry.Name);
                                            changed = true;
                                        }
                                    }
                                }
                            } catch { }
                        }
                        if (changed) Task.Run(() => FlushToDisk());
                    }
                } catch (Exception ex) { Logger.Error("Background Indexing Error: " + ex.Message); }
            });
            
            // Fire and forget save to disk
            Task.Run(() => FlushToDisk());
        }

        public void RemoveEntry(string type, string name)
        {
            var index = GetIndex();
            lock (_lock)
            {
                var keysToRemove = index.Objects
                    .Where(pair =>
                        string.Equals(pair.Value.Type, type, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .ToList();

                var removed = false;
                foreach (var key in keysToRemove)
                {
                    removed |= index.Objects.TryRemove(key, out _);
                }

                if (removed) UpdateIndex(index);
            }
        }

        public void Clear() { lock (_lock) { _index = null; } }
    }
}
