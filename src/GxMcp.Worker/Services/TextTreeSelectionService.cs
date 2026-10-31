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

            string typeFilter = args?["type"]?.ToString();
            string moduleFilter = args?["module"]?.ToString();
            string pathPrefix = TextTreePath.NormalizePath(args?["pathPrefix"]?.ToString());
            int limit = args?["limit"]?.ToObject<int?>() ?? 0;

            IEnumerable<SearchIndex.IndexEntry> baseEntries = index.Objects.Values;
            IEnumerable<SearchIndex.IndexEntry> query = baseEntries
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Name))
                .Where(entry => !IsContainerEntry(entry));
            if (selectors.Count > 0) query = query.Where(entry => selectors.Any(selector => TextSelectorParser.Matches(selector, entry)));
            if (!string.IsNullOrWhiteSpace(typeFilter)) query = query.Where(entry => string.Equals(entry.Type, typeFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(moduleFilter)) query = query.Where(entry => string.Equals(entry.Module, moduleFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pathPrefix)) query = query.Where(entry => TextTreePath.NormalizePath(entry.Path).StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));

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
                        .Where(entry => containers.Any(container => IsDescendantOf(entry, container)));
                    if (!string.IsNullOrWhiteSpace(moduleFilter))
                        descendants = descendants.Where(entry => string.Equals(entry.Module, moduleFilter, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(pathPrefix))
                        descendants = descendants.Where(entry => TextTreePath.NormalizePath(entry.Path).StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));
                    query = query.Concat(descendants);
                }
            }

            List<TextSelector> ignored = TextSelectorParser.ParseMany(args?["ignore"]);
            if (ignored.Any(TextSelectorParser.IsAll))
            {
                selected = new List<SearchIndex.IndexEntry>();
                return true;
            }
            if (ignored.Count > 0)
                query = query.Where(entry => !ignored.Any(selector => TextSelectorParser.Matches(selector, entry)));

            selected = query
                .GroupBy(entry => (entry.Type ?? string.Empty) + ":" + (entry.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (limit > 0) selected = selected.Take(limit).ToList();
            return true;
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
            return entry != null
                && (string.Equals(entry.Type, "Folder", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Type, "Module", StringComparison.OrdinalIgnoreCase));
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
