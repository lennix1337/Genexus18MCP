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
            var list = candidates.Where(c => c != null).ToList();
            foreach (var candidate in list)
            {
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
            string lowerInput = input.ToLowerInvariant();
            string stem = lowerInput.Length >= 4 && lowerInput.EndsWith("s") ? lowerInput.Substring(0, lowerInput.Length - 1) : lowerInput;

            foreach (var candidate in list)
            {
                string lowerCand = candidate.ToLowerInvariant();
                if (lowerCand.StartsWith(lowerInput) || (stem.Length >= 4 && lowerCand.StartsWith(stem)))
                {
                    return candidate;
                }
            }

            // Common arg synonyms (e.g. "limit" -> "maxResults")
            if (string.Equals(input, "limit", StringComparison.OrdinalIgnoreCase))
            {
                var match = list.FirstOrDefault(c => string.Equals(c, "maxResults", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c, "max", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c, "take", StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // Read verbs: the toolset's actions read with "get" (get, get_visual, ...) or
            // "inspect", not "read" (#655).
            if (ReadVerbs.Contains(input, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string verb in new[] { "get", "inspect", "view", "list" })
                {
                    var match = list.FirstOrDefault(c => string.Equals(c, verb, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match;
                }
                var getVariant = list.FirstOrDefault(c => c.StartsWith("get_", StringComparison.OrdinalIgnoreCase));
                if (getVariant != null) return getVariant;
            }

            return null;
        }

        private static readonly string[] ReadVerbs = { "read", "fetch", "show", "view" };

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
