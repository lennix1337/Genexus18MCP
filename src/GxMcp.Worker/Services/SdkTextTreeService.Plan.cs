using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class SdkTextTreeService
    {
        private static readonly Lazy<HashSet<string>> InstalledReferenceModules =
            new Lazy<HashSet<string>>(LoadInstalledReferenceModules);

        private static bool IsReferenceModule(SearchIndex.IndexEntry entry)
        {
            string module = entry?.Module;
            return !string.IsNullOrWhiteSpace(module)
                && InstalledReferenceModules.Value.Contains(module);
        }

        private static HashSet<string> LoadInstalledReferenceModules()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sdkRoot = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR")
                ?? Environment.GetEnvironmentVariable("GX_PATH");
            if (string.IsNullOrWhiteSpace(sdkRoot)) return result;
            try
            {
                string modulesRoot = Path.Combine(sdkRoot, "Modules");
                if (!Directory.Exists(modulesRoot)) return result;
                foreach (string file in Directory.EnumerateFiles(modulesRoot, "*.opc", SearchOption.TopDirectoryOnly))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(stem)) continue;
                    int separator = stem.IndexOf('_');
                    result.Add(separator > 0 ? stem.Substring(0, separator) : stem);
                }
            }
            catch { }
            return result;
        }

        private List<ExportPlan> BuildPlan(List<SearchIndex.IndexEntry> selected, bool includeDependencies, bool includeTableProjections, string part, string root)
        {
            var entries = new List<Tuple<SearchIndex.IndexEntry, string>>();
            var tableProjections = new List<Tuple<SearchIndex.IndexEntry, string, SearchIndex.IndexEntry>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SearchIndex.IndexEntry entry in selected)
                AddPlanEntry(entries, seen, entry, IsReferenceModule(entry) ? "ref" : "src");

            if (includeDependencies && _indexCacheService != null)
            {
                SearchIndex index = _indexCacheService.TryGetLoadedIndex();
                if (index != null)
                {
                    var queue = new Queue<SearchIndex.IndexEntry>(selected);
                    var visited = new HashSet<string>(selected.Select(entry => (entry.Type ?? string.Empty) + ":" + entry.Name), StringComparer.OrdinalIgnoreCase);
                    while (queue.Count > 0)
                    {
                        SearchIndex.IndexEntry parent = queue.Dequeue();
                        foreach (string dependency in (parent.Calls ?? new List<string>()).Concat(parent.Tables ?? new List<string>()))
                        {
                            string name = dependency;
                            int colon = name == null ? -1 : name.IndexOf(':');
                            if (colon >= 0 && colon + 1 < name.Length) name = name.Substring(colon + 1);
                            List<SearchIndex.IndexEntry> matches = index.FindByName(name);
                            if (matches.Count == 1)
                            {
                                SearchIndex.IndexEntry dependencyEntry = matches[0];
                                AddPlanEntry(entries, seen, dependencyEntry, "ref");
                                string key = (dependencyEntry.Type ?? string.Empty) + ":" + dependencyEntry.Name;
                                if (visited.Add(key)) queue.Enqueue(dependencyEntry);
                            }
                        }
                    }
                }
            }

            SearchIndex loadedIndex = _indexCacheService?.TryGetLoadedIndex();
            if (includeTableProjections && loadedIndex != null)
            {
                foreach (SearchIndex.IndexEntry transaction in selected.Where(entry =>
                    string.Equals(entry?.Type, "Transaction", StringComparison.OrdinalIgnoreCase)
                    && entry != null
                    && !string.IsNullOrWhiteSpace(entry.Name)))
                {
                    string tableName = string.IsNullOrWhiteSpace(transaction.RootTable) ? transaction.Name : transaction.RootTable;
                    SearchIndex.IndexEntry table = loadedIndex.FindByName(tableName)
                        .FirstOrDefault(entry => string.Equals(entry.Type, "Table", StringComparison.OrdinalIgnoreCase));
                    table = table ?? new SearchIndex.IndexEntry
                    {
                        Name = tableName,
                        Type = "Table",
                        Module = transaction.Module,
                        Parent = transaction.Parent,
                        ParentPath = transaction.ParentPath,
                        ParentFolderPath = transaction.ParentFolderPath,
                        Path = transaction.Path,
                        Guid = transaction.Guid,
                        EntityKey = transaction.EntityKey,
                        LastUpdate = transaction.LastUpdate
                    };
                    tableProjections.Add(Tuple.Create(table, IsReferenceModule(transaction) ? "ref" : "src", transaction));
                }
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plan = new List<ExportPlan>();
            foreach (Tuple<SearchIndex.IndexEntry, string> pair in entries.OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Item1.Type, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Item1.Name, StringComparer.OrdinalIgnoreCase))
            {
                SearchIndex.IndexEntry entry = pair.Item1;
                string stem = ObjectTextService.SanitizeFilePart(entry.Name);
                string relativeFolder = BuildRelativeFolder(entry);
                string relative = Path.Combine(pair.Item2, relativeFolder ?? string.Empty, stem + ".gx");
                if (!used.Add(relative))
                    relative = Path.Combine(pair.Item2, relativeFolder ?? string.Empty, ObjectTextService.SanitizeFilePart(entry.Type) + "__" + stem + ".gx");
                used.Add(relative);
                plan.Add(new ExportPlan
                {
                    Entry = entry,
                    Role = pair.Item2,
                    Part = part,
                    FilePath = Path.Combine(root, relative),
                    RelativePath = NormalizePath(relative),
                    VersionToken = BuildVersionToken(entry, part)
                });
            }
            foreach (Tuple<SearchIndex.IndexEntry, string, SearchIndex.IndexEntry> pair in tableProjections
                .OrderBy(value => value.Item2, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Item1.Name, StringComparer.OrdinalIgnoreCase))
            {
                SearchIndex.IndexEntry table = pair.Item1;
                SearchIndex.IndexEntry transaction = pair.Item3;
                string relative = BuildTableProjectionRelativePath(pair.Item2, table.Name);
                if (!used.Add(relative)) continue;
                plan.Add(new ExportPlan
                {
                    Entry = table,
                    ReadEntry = transaction,
                    Role = pair.Item2,
                    Part = "Structure",
                    Parts = new List<string> { "Structure" },
                    IsTableProjection = true,
                    FilePath = Path.Combine(root, relative),
                    RelativePath = NormalizePath(relative),
                    VersionToken = BuildVersionToken(transaction, "Structure|table")
                });
            }
            return plan;
        }

        internal static string BuildTableProjectionRelativePath(string role, string tableName)
        {
            return NormalizePath(Path.Combine(role ?? "src", "#tables", ObjectTextService.SanitizeFilePart(tableName) + ".gx"));
        }

        private static void AddPlanEntry(List<Tuple<SearchIndex.IndexEntry, string>> entries, HashSet<string> seen, SearchIndex.IndexEntry entry, string role)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) return;
            if (string.Equals(entry.Type, "Folder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Type, "Module", StringComparison.OrdinalIgnoreCase)) return;
            // These SDK-owned catalog rows have no resolvable text part in the public
            // ObjectService path. Keep the mirror fail-closed instead of emitting 131
            // false failures for a valid KB-wide projection.
            if (string.Equals(entry.Type, "ThemeClass", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Type, "DataStoreCategory", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Type, "GeneratorCategory", StringComparison.OrdinalIgnoreCase)) return;
            string key = (entry.Type ?? string.Empty) + ":" + entry.Name;
            if (seen.Add(key)) entries.Add(Tuple.Create(entry, role));
        }

        private bool TryReadParts(SearchIndex.IndexEntry entry, IEnumerable<string> requestedParts, out Dictionary<string, string> parts, out string error)
        {
            parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            if (entry == null)
            {
                error = "The index entry is empty.";
                return false;
            }
            try
            {
                string typedName = (entry.Type ?? string.Empty) + ":" + entry.Name;
                string raw = _objectService.ReadObjectSourceParts(typedName, requestedParts, entry.Type, entry.Guid, entry.EntityKey, entry.Path);
                JObject response = ParseEnvelope(raw);
                JObject values = response["parts"] as JObject;
                if (values != null)
                {
                    foreach (JProperty property in values.Properties())
                    {
                        string value = property.Value.Type == JTokenType.String
                            ? property.Value.ToString()
                            : property.Value["source"]?.ToString();
                        if (value != null) parts[property.Name] = value;
                    }
                }
                if (parts.Count > 0) return true;
                error = response["error"]?.ToString() ?? response["message"]?.ToString() ?? "The SDK returned no textual parts.";
                return false;
            }
            catch (Exception ex)
            {
                error = "SDK part read failed: " + ex.Message;
                return false;
            }
        }

        // A part read that failed because the object exposes no text part at all. The read lists the available
        // parts when there are some, so a missing or empty list means the type has none.
        internal static bool IsNoTextPartsReply(JObject response)
        {
            if (!string.Equals(response?["code"]?.ToString(), "PartNotFound", StringComparison.Ordinal)) return false;
            return !(response["availableParts"] is JArray available) || available.Count == 0;
        }

        private bool TryReadPart(SearchIndex.IndexEntry entry, string part, out string source, out string error)
        {
            source = null;
            error = null;
            if (entry == null)
            {
                error = "The index entry is empty.";
                return false;
            }
            try
            {
                // The index identity (GUID, EntityKey, path) travels with the name, as it does in the
                // multi-part read and in genexus_read. The EntityKey is no longer retried as a name.
                JObject response = ParseEnvelope(_readPartText(entry.Name, part, entry.Type, entry.Guid, entry.EntityKey, entry.Path));
                JToken sourceToken = response["source"] ?? response["result"]?["source"];
                if (sourceToken != null)
                {
                    source = sourceToken.ToString();
                    return true;
                }
                if (IsNoTextPartsReply(response))
                {
                    error = McpResponse.Err(
                        code: "ObjectHasNoTextParts",
                        message: "'" + entry.Name + "' (" + entry.Type + ") has no text parts to export: its data are SDK properties "
                            + "(WorkWithPlus templates keep them in WWPTemplate_* properties, for example).",
                        hint: "Read the properties with genexus_properties action=get and change them with action=set. The native export does not write them.",
                        target: entry.Name);
                    return false;
                }
                error = response["error"]?.ToString() ?? response["message"]?.ToString() ?? "The SDK part did not return text content.";
                return false;
            }
            catch (Exception ex)
            {
                error = "SDK part read failed: " + ex.Message;
                return false;
            }
        }

    }
}
