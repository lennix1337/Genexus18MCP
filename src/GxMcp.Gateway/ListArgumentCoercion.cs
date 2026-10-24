using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Arguments documented as comma-separated names that clients also send as a JSON array.
    /// The Worker reads such an argument as one string, so an array that reached it would be
    /// split on its own JSON punctuation, and the schema type is <c>string</c>, so the
    /// validator refused the array without saying a joined string would do (#652). The array
    /// is joined before validation; the advertised schema keeps the single type that every
    /// MCP client accepts.
    /// </summary>
    internal static class ListArgumentCoercion
    {
        private static readonly (string Tool, string Argument)[] CommaSeparated =
        {
            ("genexus_search_source", "objectName"),
        };

        /// <summary>
        /// Replaces a non-empty array of strings by the comma-joined string. Anything else,
        /// including an empty array or a non-string item, is left for the validator to refuse.
        /// </summary>
        internal static bool Apply(string toolName, JObject? args)
        {
            if (args == null || string.IsNullOrEmpty(toolName)) return false;

            bool changed = false;
            foreach (var (tool, argument) in CommaSeparated)
            {
                if (!string.Equals(tool, toolName, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (args[argument] is not JArray names || names.Count == 0) continue;
                if (names.Any(n => n.Type != JTokenType.String || string.IsNullOrWhiteSpace(n.ToString()))) continue;

                args[argument] = string.Join(",", names.Select(n => n.ToString().Trim()));
                changed = true;
            }

            return changed;
        }
    }
}
