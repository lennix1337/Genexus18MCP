using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    internal sealed class TextTreeSelectionService
    {
        private readonly IndexCacheService _indexCacheService;

        internal TextTreeSelectionService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService;
        }

        internal bool TrySelectEntries(string target, JObject args, bool allowAll,
            out List<SearchIndex.IndexEntry> selected, out string error, bool includeChildren = false)
        {
            selected = new List<SearchIndex.IndexEntry>();
            error = null;
            SearchIndex index;
            try { index = _indexCacheService?.TryGetLoadedIndex() ?? _indexCacheService?.GetIndex(); }
            catch (Exception ex) { error = "The object index could not be read: " + ex.Message; return false; }
            if (index?.Objects == null || index.Objects.Count == 0)
            {
                error = "The active object index is empty. Run genexus_lifecycle action=index first.";
                return false;
            }

            List<TextSelector> selectors = ReadSelectors(target, args);
            bool allSelector = selectors.Any(TextSelectorParser.IsAll);
            if (allSelector) selectors.Clear();
            bool explicitSelection = HasExplicitSelector(args, target);
            if (selectors.Count == 0 && (!allowAll || (explicitSelection && !allSelector)))
            {
                error = allowAll
                    ? "The Object Text selector was empty or invalid. Omit targets/name to export the full indexed KB."
                    : "A target is required for this Object Text action. Supply name or targets[].";
                return false;
            }

            TextTreeFilters filters = TextTreeFilters.Read(args);
            IEnumerable<SearchIndex.IndexEntry> baseEntries = index.Objects.Values;
            IEnumerable<SearchIndex.IndexEntry> query = baseEntries
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Name))
                .Where(entry => !IsContainerEntry(entry));
            if (selectors.Count > 0) query = query.Where(entry => selectors.Any(selector => TextSelectorParser.Matches(selector, entry)));
            query = query.Where(filters.Accepts);

            if (includeChildren && selectors.Count > 0)
            {
                List<SearchIndex.IndexEntry> containers = baseEntries
                    .Where(IsContainerEntry)
                    .Where(entry => selectors.Any(selector => TextSelectorParser.Matches(selector, entry)))
                    .ToList();
                if (containers.Count > 0)
                {
                    IEnumerable<SearchIndex.IndexEntry> descendants = baseEntries
                        .Where(entry => entry != null && !IsContainerEntry(entry))
                        .Where(entry => containers.Any(container => IsDescendantOf(entry, container)))
                        .Where(filters.Accepts);
                    query = query.Concat(descendants);
                }
            }

            if (filters.IgnoresAll)
            {
                selected = new List<SearchIndex.IndexEntry>();
                return true;
            }
            selected = Finish(query, filters.Limit);
            return true;
        }

        // The entries the SDK found for explicit targets that the index does not hold. They take the request's filters
        // and limit as indexed entries do, so the union is ordered and limited once more.
        internal static List<SearchIndex.IndexEntry> AddLookedUp(
            IEnumerable<SearchIndex.IndexEntry> selected,
            IEnumerable<SearchIndex.IndexEntry> lookedUp,
            JObject args)
        {
            TextTreeFilters filters = TextTreeFilters.Read(args);
            if (filters.IgnoresAll) return new List<SearchIndex.IndexEntry>();
            IEnumerable<SearchIndex.IndexEntry> found = lookedUp.Where(entry => entry != null
                && !string.IsNullOrWhiteSpace(entry.Name)
                && !IsContainerEntry(entry)
                && filters.Accepts(entry));
            return Finish(selected.Concat(found), filters.Limit);
        }

        private static List<SearchIndex.IndexEntry> Finish(IEnumerable<SearchIndex.IndexEntry> query, int limit)
        {
            List<SearchIndex.IndexEntry> selected = query
                .GroupBy(entry => (entry.Type ?? string.Empty) + ":" + (entry.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return limit > 0 ? selected.Take(limit).ToList() : selected;
        }

        // The filters one request puts on the entries it selects. An entry the SDK finds for an explicit target passes
        // the same filters as an indexed one, so the two paths select alike.
        internal sealed class TextTreeFilters
        {
            private readonly string _type;
            private readonly string _module;
            private readonly string _pathPrefix;
            private readonly List<TextSelector> _ignored;

            private TextTreeFilters(JObject args)
            {
                _type = args?["type"]?.ToString();
                _module = args?["module"]?.ToString();
                _pathPrefix = TextTreePath.NormalizePath(args?["pathPrefix"]?.ToString());
                _ignored = TextSelectorParser.ParseMany(args?["ignore"]);
                Limit = args?["limit"]?.ToObject<int?>() ?? 0;
            }

            internal int Limit { get; }

            internal bool IgnoresAll => _ignored.Any(TextSelectorParser.IsAll);

            internal static TextTreeFilters Read(JObject args) => new TextTreeFilters(args);

            internal bool Accepts(SearchIndex.IndexEntry entry)
            {
                if (entry == null || IgnoresAll) return false;
                if (!string.IsNullOrWhiteSpace(_type) && !string.Equals(entry.Type, _type, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(_module) && !string.Equals(entry.Module, _module, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(_pathPrefix) && !TextTreePath.NormalizePath(entry.Path).StartsWith(_pathPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                return !_ignored.Any(selector => TextSelectorParser.Matches(selector, entry));
            }
        }

        internal static List<TextSelector> ReadSelectors(string target, JObject args)
        {
            JToken token = args?["targets"] ?? args?["names"] ?? args?["objects"];
            return token != null
                ? TextSelectorParser.ParseMany(token)
                : TextSelectorParser.ParseMany((JToken)(args?["name"]?.ToString() ?? target));
        }

        private static bool HasExplicitSelector(JObject args, string target)
        {
            if (!string.IsNullOrWhiteSpace(target)) return true;
            if (args == null) return false;
            if (args["targets"] != null || args["names"] != null || args["objects"] != null) return true;
            return !string.IsNullOrWhiteSpace(args["name"]?.ToString());
        }

        private static bool IsContainerEntry(SearchIndex.IndexEntry entry)
        {
            return TextSelectorParser.IsContainer(entry);
        }

        private static bool IsDescendantOf(SearchIndex.IndexEntry entry, SearchIndex.IndexEntry container)
        {
            if (entry == null || container == null || string.IsNullOrWhiteSpace(container.Name)) return false;
            if (string.Equals(entry.Module, container.Name, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string value in new[] { entry.Parent, entry.ParentPath, entry.ParentFolderPath, entry.Path })
            {
                if (PathContainsSegment(value, container.Name)) return true;
            }
            return false;
        }

        private static bool PathContainsSegment(string path, string segment)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(segment)) return false;
            return path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(value => string.Equals(value, segment, StringComparison.OrdinalIgnoreCase));
        }
    }
}
