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
            // Two homonyms are two plan entries: the key is the object, not its Type:Name.
            if (seen.Add(TextTreeSelectionService.ObjectKey(entry))) entries.Add(Tuple.Create(entry, role));
        }

        // What the export reports for one object that could not be read: a code, a plain message and a hint. The
        // export writes them as fields of the object's result, so a client branches on the code without parsing text.
        private sealed class ReadFailure
        {
            public string Code { get; set; }
            public string Message { get; set; }
            public string Hint { get; set; }
        }

        internal const string NoTextPartsCode = "ObjectHasNoTextParts";
        // The properties read answers this for an object of another type: its missing text parts stay a no-text-parts case.
        internal const string NotWwpTemplateCode = WwpTemplateReply.NotTemplateCode;
        private const string StaleIndexHint = "The index is out of date: run genexus_lifecycle action=index, then export again.";

        private bool TryReadParts(SearchIndex.IndexEntry entry, IEnumerable<string> requestedParts, out Dictionary<string, string> parts, out ReadFailure failure)
        {
            parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            failure = null;
            if (entry == null)
            {
                failure = new ReadFailure { Message = "The index entry is empty." };
                return false;
            }
            try
            {
                string typedName = (entry.Type ?? string.Empty) + ":" + entry.Name;
                JObject response = ParseEnvelope(_readPartsText(typedName, requestedParts, entry.Type, entry.Guid, entry.EntityKey, entry.Path));
                if (!IsSameObject(entry, response, out failure)) return false;
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
                failure = values != null && IsNoTextPartsEnvelope(response)
                    ? NoTextPartsFailure(entry)
                    : FailureOf(entry, response, "The SDK returned no textual parts.");
                return false;
            }
            catch (Exception ex)
            {
                failure = new ReadFailure { Message = "SDK part read failed: " + ex.Message };
                return false;
            }
        }

        // A part read that failed because the object exposes no text part at all. The read always lists the object's
        // parts, so an empty list means the type has none; a reply without the list says nothing of the kind.
        internal static bool IsNoTextPartsReply(JObject response)
        {
            if (!string.Equals(response?["code"]?.ToString(), "PartNotFound", StringComparison.Ordinal)) return false;
            return response["availableParts"] is JArray available && available.Count == 0;
        }

        private bool TryReadPart(SearchIndex.IndexEntry entry, string part, out string source, out ReadFailure failure)
        {
            source = null;
            failure = null;
            if (entry == null)
            {
                failure = new ReadFailure { Message = "The index entry is empty." };
                return false;
            }
            try
            {
                // The index identity (GUID, EntityKey, path) travels with the Type:Name, as it does in the multi-part
                // read: a bare name would be ambiguous between homonyms of other types (Transaction and its Table).
                string typedName = (entry.Type ?? string.Empty) + ":" + entry.Name;
                JObject response = ParseEnvelope(_readPartText(typedName, part, entry.Type, entry.Guid, entry.EntityKey, entry.Path));
                if (!IsSameObject(entry, response, out failure)) return false;
                JToken sourceToken = response["source"] ?? response["result"]?["source"];
                if (sourceToken != null)
                {
                    source = sourceToken.ToString();
                    return true;
                }
                failure = IsNoTextPartsReply(response)
                    ? NoTextPartsFailure(entry)
                    : FailureOf(entry, response, "The SDK part did not return text content.");
                return false;
            }
            catch (Exception ex)
            {
                failure = new ReadFailure { Message = "SDK part read failed: " + ex.Message };
                return false;
            }
        }

        // A read resolves its object by GUID or EntityKey before the name. After a rename, or after the object was
        // deleted and created again, the object it returns can differ from the index entry, and the export writes the
        // entry's name. A reply that names another object is therefore a stale index, not a part to export.
        private static bool IsSameObject(SearchIndex.IndexEntry entry, JObject response, out ReadFailure failure)
        {
            failure = null;
            string name = response["objectName"]?.ToString() ?? response["name"]?.ToString();
            string type = response["objectType"]?.ToString() ?? response["type"]?.ToString();
            bool sameName = name == null || string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase);
            bool sameType = type == null || string.Equals(type, entry.Type, StringComparison.OrdinalIgnoreCase);
            if (sameName && sameType) return true;
            failure = new ReadFailure
            {
                Code = "IndexEntryStale",
                Message = "The index names '" + entry.Name + "' (" + entry.Type + "), but its GUID or EntityKey resolves to '"
                    + name + "' (" + type + ").",
                Hint = StaleIndexHint
            };
            return false;
        }

        // A multi-part read that returned no part and no error. The object has no text part when it lists none.
        internal static bool IsNoTextPartsEnvelope(JObject response)
        {
            if (response == null || response["code"] != null || response["error"] != null) return false;
            return response["availableParts"] is JArray available && available.Count == 0;
        }

        private static ReadFailure NoTextPartsFailure(SearchIndex.IndexEntry entry)
        {
            return new ReadFailure
            {
                Code = NoTextPartsCode,
                Message = "'" + entry.Name + "' (" + entry.Type + ") has no text parts to export: its data are SDK properties.",
                Hint = "Read the properties with genexus_properties action=get and change them with action=set. The native export does not write them."
            };
        }

        // The code of an error reply, whether the reply nests it under "error" or carries it at the top.
        private static string ReplyCode(JObject response)
        {
            return response["error"] is JObject error ? error["code"]?.ToString() : response["code"]?.ToString();
        }

        // The read's own error. An object the index lists and the read cannot find means the index is out of date.
        private static ReadFailure FailureOf(SearchIndex.IndexEntry entry, JObject response, string fallbackMessage)
        {
            // McpResponse.Err puts code, message and hint under "error"; a missing part replies with them at the top.
            ReadFailure failure = response["error"] is JObject error
                ? new ReadFailure { Code = error["code"]?.ToString(), Message = error["message"]?.ToString(), Hint = error["hint"]?.ToString() }
                : new ReadFailure { Code = response["code"]?.ToString(), Message = response["error"]?.ToString() ?? response["message"]?.ToString(), Hint = response["hint"]?.ToString() };
            failure.Message = failure.Message ?? fallbackMessage;
            if (!string.Equals(failure.Code, "ObjectNotFound", StringComparison.Ordinal)) return failure;
            return new ReadFailure
            {
                Code = "IndexEntryStale",
                Message = "The index lists '" + entry.Name + "' (" + entry.Type + "), but the KB does not hold that object.",
                Hint = StaleIndexHint
            };
        }

    }
}
