using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The lines of one part that differ between the expected and the actual text, for the rollback receipt. At most
    /// <paramref name="maxLines"/> lines in all, the expected side first, and each line is cut to <paramref name="maxChars"/>.
    /// The receipt carries them only when the caller asks for verbose errors.
    /// </summary>
    internal static class ChangedLines
    {
        internal const int DefaultMaxLines = 20;
        internal const int DefaultMaxChars = 200;

        internal static JArray Describe(string part, string expected, string actual, int maxLines = DefaultMaxLines, int maxChars = DefaultMaxChars)
        {
            var result = new JArray();
            if (maxLines <= 0) return result;
            string[] before = Lines(expected);
            string[] after = Lines(actual);
            var beforeSet = new HashSet<string>(before, StringComparer.Ordinal);
            var afterSet = new HashSet<string>(after, StringComparer.Ordinal);

            foreach (string candidate in before.Where(candidate => !afterSet.Contains(candidate)))
            {
                if (result.Count >= maxLines) return result;
                result.Add(Entry(part, "before", candidate, maxChars));
            }
            foreach (string candidate in after.Where(candidate => !beforeSet.Contains(candidate)))
            {
                if (result.Count >= maxLines) return result;
                result.Add(Entry(part, "after", candidate, maxChars));
            }
            return result;
        }

        private static string[] Lines(string text)
            => (text ?? string.Empty).Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Trim().Length > 0)
                .ToArray();

        private static JObject Entry(string part, string side, string line, int maxChars)
            => new JObject
            {
                ["part"] = part,
                ["side"] = side,
                ["line"] = line.Length > maxChars ? line.Substring(0, maxChars) + "..." : line
            };
    }
}
