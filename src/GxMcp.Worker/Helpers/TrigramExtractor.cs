using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Extracts trigrams for full-text indexing and query candidate pruning.
    /// Trigrams are 3-character normalized (lowercase) substrings.
    /// Query parsing extracts guaranteed literal runs and alternation branches
    /// to build candidate postings intersections without false negatives.
    /// </summary>
    public static class TrigramExtractor
    {
        internal static ulong PackTrigram(char c0, char c1, char c2)
            => ((ulong)c0 << 32) | ((ulong)c1 << 16) | c2;

        internal static bool TryPackTrigram(string trigram, out ulong packed)
        {
            if (trigram == null || trigram.Length != 3)
            {
                packed = 0;
                return false;
            }
            packed = PackTrigram(
                ToLowerInvariantFast(trigram[0]),
                ToLowerInvariantFast(trigram[1]),
                ToLowerInvariantFast(trigram[2]));
            return true;
        }

        internal static string UnpackTrigram(ulong packed)
            => new string(new[]
            {
                (char)((packed >> 32) & 0xFFFF),
                (char)((packed >> 16) & 0xFFFF),
                (char)(packed & 0xFFFF)
            });

        private static char ToLowerInvariantFast(char c)
            => (c >= 'A' && c <= 'Z') ? (char)(c + 32) : (c < 128 ? c : char.ToLowerInvariant(c));

        /// <summary>
        /// Extracts sorted unique packed trigrams without allocating a lowercased copy
        /// of <paramref name="text"/> or per-trigram substring objects.
        /// </summary>
        internal static ulong[] ExtractPackedTrigrams(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 3) return Array.Empty<ulong>();

            var set = new HashSet<ulong>();
            char c0 = ToLowerInvariantFast(text[0]);
            char c1 = ToLowerInvariantFast(text[1]);
            for (int i = 2; i < text.Length; i++)
            {
                char c2 = ToLowerInvariantFast(text[i]);
                set.Add(PackTrigram(c0, c1, c2));
                c0 = c1;
                c1 = c2;
            }
            if (set.Count == 0) return Array.Empty<ulong>();
            var arr = new ulong[set.Count];
            set.CopyTo(arr);
            Array.Sort(arr);
            return arr;
        }

        internal static List<ulong[]> ExtractRequiredPackedTrigramSets(string pattern, string callee)
        {
            ulong[] calleeTrigrams = null;
            if (!string.IsNullOrWhiteSpace(callee))
            {
                calleeTrigrams = ExtractPackedTrigrams(callee.Trim());
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                if (calleeTrigrams != null && calleeTrigrams.Length > 0)
                {
                    return new List<ulong[]> { calleeTrigrams };
                }
                return null;
            }

            List<string> branches = SplitTopLevelAlternations(pattern.Trim());
            var branchTrigramSets = new List<ulong[]>(branches.Count);

            foreach (var branch in branches)
            {
                var branchSet = new HashSet<ulong>();
                var literalRuns = ExtractLiteralRuns(branch);
                foreach (var run in literalRuns)
                {
                    if (run.Length >= 3)
                    {
                        foreach (var t in ExtractPackedTrigrams(run)) branchSet.Add(t);
                    }
                }

                if (calleeTrigrams != null && calleeTrigrams.Length > 0)
                {
                    foreach (var t in calleeTrigrams) branchSet.Add(t);
                }

                if (branchSet.Count == 0)
                {
                    return null;
                }

                var arr = new ulong[branchSet.Count];
                branchSet.CopyTo(arr);
                Array.Sort(arr);
                branchTrigramSets.Add(arr);
            }

            return branchTrigramSets.Count > 0 ? branchTrigramSets : null;
        }

        public static HashSet<string> ExtractTrigrams(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text) || text.Length < 3) return set;

            string normalized = text.ToLowerInvariant();
            for (int i = 0; i <= normalized.Length - 3; i++)
            {
                set.Add(normalized.Substring(i, 3));
            }
            return set;
        }

        public static List<HashSet<string>> ExtractRequiredTrigramSets(string pattern, string callee)
        {
            HashSet<string> calleeTrigrams = null;
            if (!string.IsNullOrWhiteSpace(callee))
            {
                calleeTrigrams = ExtractTrigrams(callee.Trim());
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                if (calleeTrigrams != null && calleeTrigrams.Count > 0)
                {
                    return new List<HashSet<string>> { calleeTrigrams };
                }
                return null;
            }

            List<string> branches = SplitTopLevelAlternations(pattern.Trim());
            var branchTrigramSets = new List<HashSet<string>>();

            foreach (var branch in branches)
            {
                var branchSet = new HashSet<string>(StringComparer.Ordinal);
                var literalRuns = ExtractLiteralRuns(branch);
                foreach (var run in literalRuns)
                {
                    if (run.Length >= 3)
                    {
                        var runTrigrams = ExtractTrigrams(run);
                        foreach (var t in runTrigrams) branchSet.Add(t);
                    }
                }

                if (calleeTrigrams != null && calleeTrigrams.Count > 0)
                {
                    foreach (var t in calleeTrigrams) branchSet.Add(t);
                }

                // If any branch has NO required trigrams, it can match any document.
                // In a disjunction (A | B), if B can match anything, the query matches anything.
                if (branchSet.Count == 0)
                {
                    return null;
                }

                branchTrigramSets.Add(branchSet);
            }

            return branchTrigramSets.Count > 0 ? branchTrigramSets : null;
        }

        public static HashSet<string> IntersectPostings(
            IDictionary<string, HashSet<string>> trigramIndex,
            List<HashSet<string>> branches,
            ICollection<string> allKeys)
        {
            if (branches == null || branches.Count == 0)
            {
                return new HashSet<string>(allKeys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var branch in branches)
            {
                if (branch == null || branch.Count == 0)
                {
                    return new HashSet<string>(allKeys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                }

                // Find the rarest trigram in this branch
                string rarest = null;
                int minPostings = int.MaxValue;
                foreach (var t in branch)
                {
                    if (trigramIndex == null || !trigramIndex.TryGetValue(t, out var postings) || postings == null || postings.Count == 0)
                    {
                        rarest = null;
                        minPostings = 0;
                        break;
                    }
                    if (postings.Count < minPostings)
                    {
                        minPostings = postings.Count;
                        rarest = t;
                    }
                }

                if (minPostings == 0 || rarest == null)
                {
                    // Trigram does not appear anywhere in index; 0 candidates for this branch
                    continue;
                }

                var branchCandidates = new HashSet<string>(trigramIndex[rarest], StringComparer.OrdinalIgnoreCase);
                foreach (var t in branch)
                {
                    if (t == rarest) continue;
                    if (!trigramIndex.TryGetValue(t, out var postings) || postings == null)
                    {
                        branchCandidates.Clear();
                        break;
                    }
                    branchCandidates.IntersectWith(postings);
                    if (branchCandidates.Count == 0) break;
                }

                foreach (var key in branchCandidates)
                {
                    result.Add(key);
                }
            }

            return result;
        }

        private static List<string> SplitTopLevelAlternations(string pattern)
        {
            var branches = new List<string>();
            int parenDepth = 0;
            int bracketDepth = 0;
            int lastStart = 0;
            bool escaped = false;

            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '[' && parenDepth >= 0) bracketDepth++;
                else if (c == ']' && bracketDepth > 0) bracketDepth--;
                else if (bracketDepth == 0)
                {
                    if (c == '(') parenDepth++;
                    else if (c == ')' && parenDepth > 0) parenDepth--;
                    else if (c == '|' && parenDepth == 0)
                    {
                        branches.Add(pattern.Substring(lastStart, i - lastStart));
                        lastStart = i + 1;
                    }
                }
            }
            branches.Add(pattern.Substring(lastStart));
            return branches;
        }

        internal static List<string> ExtractLiteralRuns(string pattern)
        {
            var runs = new List<string>();
            var currentRun = new StringBuilder();
            int i = 0;
            int len = pattern.Length;

            while (i < len)
            {
                char c = pattern[i];

                if (c == '\\')
                {
                    if (i + 1 < len)
                    {
                        char next = pattern[i + 1];
                        // Check for meta escapes
                        if (next == 'd' || next == 'D' || next == 's' || next == 'S' ||
                            next == 'w' || next == 'W' || next == 'b' || next == 'B')
                        {
                            FlushRun(runs, currentRun);
                            i += 2;
                            continue;
                        }
                        else if (next == 'n' || next == 'r' || next == 't')
                        {
                            FlushRun(runs, currentRun);
                            i += 2;
                            continue;
                        }
                        else
                        {
                            // Escaped punctuation (e.g. \., \-, \*, \+) is a literal character!
                            // But check if followed by quantifier:
                            char literalChar = next;
                            i += 2;
                            if (i < len && (pattern[i] == '*' || pattern[i] == '?' || (pattern[i] == '{' && IsZeroRepetition(pattern, i))))
                            {
                                // Optional, not guaranteed
                                FlushRun(runs, currentRun);
                                SkipQuantifier(pattern, ref i);
                            }
                            else
                            {
                                currentRun.Append(literalChar);
                                if (i < len && (pattern[i] == '+' || pattern[i] == '{'))
                                {
                                    FlushRun(runs, currentRun);
                                    SkipQuantifier(pattern, ref i);
                                }
                            }
                            continue;
                        }
                    }
                    else
                    {
                        FlushRun(runs, currentRun);
                        i++;
                        continue;
                    }
                }

                if (c == '[' || c == '(' || c == ')' || c == '^' || c == '$' || c == '.' || c == '|')
                {
                    FlushRun(runs, currentRun);
                    if (c == '[')
                    {
                        // Skip character class
                        i++;
                        while (i < len && pattern[i] != ']')
                        {
                            if (pattern[i] == '\\') i++;
                            i++;
                        }
                        if (i < len && pattern[i] == ']') i++;
                        SkipQuantifier(pattern, ref i);
                    }
                    else
                    {
                        i++;
                    }
                    continue;
                }

                // Check if current character is followed by a quantifier
                int nextIdx = i + 1;
                if (nextIdx < len && (pattern[nextIdx] == '*' || pattern[nextIdx] == '?' || (pattern[nextIdx] == '{' && IsZeroRepetition(pattern, nextIdx))))
                {
                    // This character is optional; not guaranteed
                    FlushRun(runs, currentRun);
                    i = nextIdx;
                    SkipQuantifier(pattern, ref i);
                    continue;
                }

                currentRun.Append(c);
                i++;
                if (i < len && (pattern[i] == '+' || pattern[i] == '{'))
                {
                    // Character appears at least once, but repeated, so close the run here
                    FlushRun(runs, currentRun);
                    SkipQuantifier(pattern, ref i);
                }
            }

            FlushRun(runs, currentRun);
            return runs;
        }

        private static void FlushRun(List<string> runs, StringBuilder currentRun)
        {
            if (currentRun.Length > 0)
            {
                runs.Add(currentRun.ToString());
                currentRun.Clear();
            }
        }

        private static bool IsZeroRepetition(string pattern, int braceIdx)
        {
            int close = pattern.IndexOf('}', braceIdx);
            if (close < 0) return false;
            string content = pattern.Substring(braceIdx + 1, close - braceIdx - 1).Trim();
            return content.StartsWith("0");
        }

        private static void SkipQuantifier(string pattern, ref int i)
        {
            while (i < pattern.Length)
            {
                char c = pattern[i];
                if (c == '*' || c == '+' || c == '?') i++;
                else if (c == '{')
                {
                    int close = pattern.IndexOf('}', i);
                    if (close >= 0) i = close + 1;
                    else i++;
                }
                else break;
            }
        }
    }
}
