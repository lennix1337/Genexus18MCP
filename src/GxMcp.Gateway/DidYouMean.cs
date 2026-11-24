using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    public static class DidYouMean
    {
        public static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            if (b.Length <= 128)
            {
                Span<int> prev = stackalloc int[b.Length + 1];
                Span<int> curr = stackalloc int[b.Length + 1];
                for (int j = 0; j <= b.Length; j++) prev[j] = j;
                for (int i = 1; i <= a.Length; i++)
                {
                    curr[0] = i;
                    char ca = char.ToLowerInvariant(a[i - 1]);
                    for (int j = 1; j <= b.Length; j++)
                    {
                        int cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                        curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    }
                    curr.CopyTo(prev);
                }
                return prev[b.Length];
            }
            else
            {
                var prev = new int[b.Length + 1];
                var curr = new int[b.Length + 1];
                for (int j = 0; j <= b.Length; j++) prev[j] = j;
                for (int i = 1; i <= a.Length; i++)
                {
                    curr[0] = i;
                    char ca = char.ToLowerInvariant(a[i - 1]);
                    for (int j = 1; j <= b.Length; j++)
                    {
                        int cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                        curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    }
                    Array.Copy(curr, prev, curr.Length);
                }
                return prev[b.Length];
            }
        }

        public static string? Suggest(string input, IEnumerable<string> candidates, int maxDistance = 2)
        {
            if (string.IsNullOrEmpty(input)) return null;
            string? best = null;
            int bestDist = int.MaxValue;
            IReadOnlyList<string> list = candidates as IReadOnlyList<string>
                ?? candidates.Where(c => c != null).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                string candidate = list[i];
                if (candidate == null) continue;
                if (Math.Abs(input.Length - candidate.Length) <= maxDistance)
                {
                    int d = Levenshtein(input, candidate);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = candidate;
                        if (d == 0) break;
                    }
                }
            }
            if (bestDist <= maxDistance) return best;

            // Prefix / stem matching (e.g. "objects" -> "objectName", "procs" -> "procedures")
            bool useStem = input.Length >= 4 && (input[input.Length - 1] == 's' || input[input.Length - 1] == 'S');
            ReadOnlySpan<char> inputSpan = input.AsSpan();
            ReadOnlySpan<char> stemSpan = useStem ? inputSpan.Slice(0, input.Length - 1) : ReadOnlySpan<char>.Empty;

            for (int i = 0; i < list.Count; i++)
            {
                string candidate = list[i];
                if (candidate == null) continue;
                ReadOnlySpan<char> candSpan = candidate.AsSpan();
                if (candSpan.StartsWith(inputSpan, StringComparison.OrdinalIgnoreCase)
                    || (stemSpan.Length >= 4 && candSpan.StartsWith(stemSpan, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }

            // Common arg synonyms (e.g. "limit" -> "maxResults")
            if (string.Equals(input, "limit", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string c = list[i];
                    if (c != null && (string.Equals(c, "maxResults", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c, "max", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c, "take", StringComparison.OrdinalIgnoreCase)))
                    {
                        return c;
                    }
                }
            }

            // Read verbs: the toolset's actions read with "get" (get, get_visual, ...) or
            // "inspect", not "read" (#655).
            if (ArrayContainsIgnoreCase(ReadVerbs, input))
            {
                for (int v = 0; v < ReadVerbReplacements.Length; v++)
                {
                    string verb = ReadVerbReplacements[v];
                    for (int i = 0; i < list.Count; i++)
                    {
                        string c = list[i];
                        if (c != null && string.Equals(c, verb, StringComparison.OrdinalIgnoreCase))
                            return c;
                    }
                }
                for (int i = 0; i < list.Count; i++)
                {
                    string c = list[i];
                    if (c != null && c.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
                        return c;
                }
            }

            return null;
        }

        private static bool ArrayContainsIgnoreCase(string[] items, string value)
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static readonly string[] ReadVerbs = { "read", "fetch", "show", "view" };
        private static readonly string[] ReadVerbReplacements = { "get", "inspect", "view", "list" };

        public static string FormatSuggestionMessage(string field, string value, IEnumerable<string> candidates, int maxDistance = 2)
        {
            var list = candidates.ToList();
            var suggestion = Suggest(value, list, maxDistance);
            string allowed = string.Join(", ", list);
            if (suggestion != null)
            {
                return $"Invalid {field} '{value}'. Did you mean '{suggestion}'? Allowed: {allowed}.";
            }
            return $"Invalid {field} '{value}'. Allowed: {allowed}.";
        }
    }
}
