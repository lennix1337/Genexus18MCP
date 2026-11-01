using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The lines of one part that differ between the expected and the actual text, for the rollback receipt. At most
    /// <paramref name="maxLines"/> lines in all, the expected side first, and each line is cut to <paramref name="maxChars"/>.
    /// The receipt carries them only when the caller asks for verbose errors. A line counts as differing when a line diff
    /// leaves it out of the common sequence, so a removed duplicate or a moved line is reported as well.
    /// </summary>
    internal static class ChangedLines
    {
        internal const int DefaultMaxLines = 20;
        internal const int DefaultMaxChars = 200;

        // The longest common subsequence is computed for the middle of the two texts only, and only while the table stays this
        // small. A larger middle is reported whole, which is the bound the receipt needs.
        internal const long MaxDiffCells = 250_000;

        internal static JArray Describe(string part, string expected, string actual, int maxLines = DefaultMaxLines, int maxChars = DefaultMaxChars)
        {
            var result = new JArray();
            if (maxLines <= 0) return result;
            var missing = new List<string>();
            var added = new List<string>();
            Diff(Lines(expected), Lines(actual), missing, added);

            foreach (string line in missing)
            {
                if (result.Count >= maxLines) return result;
                result.Add(Entry(part, "before", line, maxChars));
            }
            foreach (string line in added)
            {
                if (result.Count >= maxLines) return result;
                result.Add(Entry(part, "after", line, maxChars));
            }
            return result;
        }

        // The lines of before that are not in the common sequence (missing), and the lines of after that are not (added), in order.
        // The common head and tail are skipped first; the middle is compared with a longest common subsequence when it is small.
        internal static void Diff(string[] before, string[] after, List<string> missing, List<string> added)
        {
            int head = 0;
            while (head < before.Length && head < after.Length && before[head] == after[head]) head++;
            int tailBefore = before.Length;
            int tailAfter = after.Length;
            while (tailBefore > head && tailAfter > head && before[tailBefore - 1] == after[tailAfter - 1])
            {
                tailBefore--;
                tailAfter--;
            }

            int m = tailBefore - head;
            int n = tailAfter - head;
            if ((long)m * n > MaxDiffCells)
            {
                for (int i = head; i < tailBefore; i++) missing.Add(before[i]);
                for (int j = head; j < tailAfter; j++) added.Add(after[j]);
                return;
            }

            var length = new int[m + 1, n + 1];
            for (int i = m - 1; i >= 0; i--)
                for (int j = n - 1; j >= 0; j--)
                    length[i, j] = before[head + i] == after[head + j]
                        ? length[i + 1, j + 1] + 1
                        : Math.Max(length[i + 1, j], length[i, j + 1]);

            int x = 0;
            int y = 0;
            while (x < m && y < n)
            {
                if (before[head + x] == after[head + y])
                {
                    x++;
                    y++;
                }
                else if (length[x + 1, y] >= length[x, y + 1])
                {
                    missing.Add(before[head + x]);
                    x++;
                }
                else
                {
                    added.Add(after[head + y]);
                    y++;
                }
            }
            for (; x < m; x++) missing.Add(before[head + x]);
            for (; y < n; y++) added.Add(after[head + y]);
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
