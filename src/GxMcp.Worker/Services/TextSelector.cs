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

            foreach (string value in token.ToString().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                TextSelector selector = ParseOne(value.Trim());
                if (selector != null) selectors.Add(selector);
            }
            return selectors;
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

        internal static bool Matches(TextSelector selector, SearchIndex.IndexEntry entry)
        {
            if (entry == null) return false;
            if (Matches(selector, entry.Name, entry.Type)) return true;
            // A GUID or EntityKey names one object whatever its type, so it selects that entry too.
            return selector != null
                && !string.IsNullOrWhiteSpace(selector.Name)
                && (string.IsNullOrWhiteSpace(selector.Type) || string.Equals(selector.Type, entry.Type, StringComparison.OrdinalIgnoreCase))
                && (string.Equals(selector.Name, entry.Guid, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(selector.Name, entry.EntityKey, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool Matches(TextSelector selector, string name, string type)
        {
            if (selector == null) return false;
            if (!string.IsNullOrWhiteSpace(selector.Type)
                && !string.Equals(selector.Type, type, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(selector.Name) || selector.Name == "*") return true;
            if (string.Equals(selector.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(type)
                && string.Equals(selector.Name, type + ":" + name, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = selector.Name.LastIndexOf('.');
            return dot >= 0
                && string.Equals(selector.Name.Substring(dot + 1), name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
