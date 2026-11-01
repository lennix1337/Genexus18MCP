using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    internal sealed class TextSelector
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    internal static class TextSelectorParser
    {
        private const string RootModule = "Root Module";

        internal static List<TextSelector> ParseMany(JToken token)
        {
            var selectors = new List<TextSelector>();
            if (token == null || token.Type == JTokenType.Null) return selectors;
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    TextSelector selector = ParseOne(item);
                    if (selector != null) selectors.Add(selector);
                }
                return selectors;
            }

            foreach (string value in SplitSelectors(token.ToString()))
            {
                TextSelector selector = ParseOne(value.Trim());
                if (selector != null) selectors.Add(selector);
            }
            return selectors;
        }

        // The separators of a selector list, except inside parentheses: EntityKey(guid, id) holds a comma.
        private static IEnumerable<string> SplitSelectors(string text)
        {
            int depth = 0;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (depth == 0 && (c == ',' || c == ';'))
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }
            yield return text.Substring(start);
        }

        internal static TextSelector ParseOne(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Object)
            {
                string name = token["name"]?.ToString() ?? token["target"]?.ToString();
                string type = token["type"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(type)) return null;
                return new TextSelector { Name = name, Type = type };
            }

            string raw = token.ToString().Trim();
            if (raw.Length == 0) return null;
            // guid:id is an EntityKey, not a Type:Name pair.
            if (ObjectService.TryParseEntityKey(raw, out _, out _)) return new TextSelector { Name = raw };
            int colon = raw.IndexOf(':');
            if (colon > 0 && colon < raw.Length - 1)
                return new TextSelector { Type = raw.Substring(0, colon), Name = raw.Substring(colon + 1) };
            return new TextSelector { Name = raw };
        }

        internal static bool IsAll(TextSelector selector)
        {
            return selector != null
                && string.IsNullOrWhiteSpace(selector.Type)
                && (string.Equals(selector.Name, "*", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(selector.Name, "[all]", StringComparison.OrdinalIgnoreCase));
        }

        // An entry is selected by its name and type. Its GUID or EntityKey selects it only when the caller allows
        // identity: the native export does; the delete and the legacy export select by name and type alone.
        internal static bool Matches(TextSelector selector, SearchIndex.IndexEntry entry, bool allowIdentity = false)
        {
            if (entry == null) return false;
            if (Matches(selector, entry.Name, entry.Type, QualifiersOf(entry))) return true;
            // A GUID or EntityKey names one object whatever its type. A folder or module is a container: selecting
            // it by identity would also select its same-named children elsewhere.
            return allowIdentity
                && selector != null
                && !IsContainer(entry)
                && !string.IsNullOrWhiteSpace(selector.Name)
                && (string.IsNullOrWhiteSpace(selector.Type) || string.Equals(selector.Type, entry.Type, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(selector.Name, entry.Guid, StringComparison.OrdinalIgnoreCase)
                    || SameEntityKey(selector.Name, entry.EntityKey));
        }

        // Two EntityKeys name the same object when their parsed identities match, whatever form each one is written in.
        internal static bool SameEntityKey(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return ObjectService.TryParseEntityKey(left, out Guid leftType, out int leftId)
                && ObjectService.TryParseEntityKey(right, out Guid rightType, out int rightId)
                && leftType == rightType
                && leftId == rightId;
        }

        internal static bool IsContainer(SearchIndex.IndexEntry entry)
        {
            return entry != null
                && (string.Equals(entry.Type, "Folder", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Type, "Module", StringComparison.OrdinalIgnoreCase));
        }

        // A name written Qualifier.Name selects the object only when the qualifier is one of the names that hold it. Null
        // qualifiers mean the caller cannot tell the module (a manifest of the legacy export has none): the name then matches
        // on its last segment alone, as before.
        internal static bool Matches(TextSelector selector, string name, string type, IEnumerable<string> qualifiers = null)
        {
            if (selector == null) return false;
            if (!string.IsNullOrWhiteSpace(selector.Type)
                && !string.Equals(selector.Type, type, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(selector.Name) || selector.Name == "*") return true;
            if (string.Equals(selector.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(type)
                && string.Equals(selector.Name, type + ":" + name, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = selector.Name.LastIndexOf('.');
            if (dot < 0 || !string.Equals(selector.Name.Substring(dot + 1), name, StringComparison.OrdinalIgnoreCase)) return false;
            if (qualifiers == null) return true;
            string qualifier = selector.Name.Substring(0, dot);
            return qualifiers.Any(known => string.Equals(known, qualifier, StringComparison.OrdinalIgnoreCase));
        }

        // The module that holds an object, or null when it sits in the root module or in none.
        internal static string ModuleOf(SearchIndex.IndexEntry entry)
        {
            string module = entry?.Module;
            return string.IsNullOrWhiteSpace(module) || string.Equals(module, RootModule, StringComparison.OrdinalIgnoreCase)
                ? null
                : module;
        }

        // The name a delete sends for one selected object: qualified by its module, so the resolution reaches that object
        // and not a homonym in another module.
        internal static string QualifiedName(SearchIndex.IndexEntry entry)
        {
            string module = ModuleOf(entry);
            return module == null ? entry?.Name : module + "." + entry.Name;
        }

        // The names a Qualifier.Name selector may use for an object: its module, the chain of modules that holds it
        // (nested modules are written A.B), and its full folder path.
        internal static IEnumerable<string> QualifiersOf(SearchIndex.IndexEntry entry)
        {
            var qualifiers = new List<string>();
            string module = ModuleOf(entry);
            if (module != null) qualifiers.Add(module);
            string[] container = ContainerSegments(entry);
            if (container.Length == 0) return qualifiers;
            qualifiers.Add(string.Join(".", container));
            int moduleAt = module == null
                ? -1
                : Array.FindLastIndex(container, segment => string.Equals(segment, module, StringComparison.OrdinalIgnoreCase));
            if (moduleAt >= 0) qualifiers.Add(string.Join(".", container.Take(moduleAt + 1)));
            return qualifiers;
        }

        // The folders and modules that hold an object, from the root down, without the root module and the object's name.
        private static string[] ContainerSegments(SearchIndex.IndexEntry entry)
        {
            var segments = TextTreePath.NormalizePath(entry?.Path)
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            if (segments.Count > 0 && string.Equals(segments[0], RootModule, StringComparison.OrdinalIgnoreCase)) segments.RemoveAt(0);
            if (segments.Count > 0 && string.Equals(segments[segments.Count - 1], entry?.Name, StringComparison.OrdinalIgnoreCase))
                segments.RemoveAt(segments.Count - 1);
            return segments.ToArray();
        }
    }
}
