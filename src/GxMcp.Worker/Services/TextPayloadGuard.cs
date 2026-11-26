using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Detects payloads where a client sent the characters "\\r\\n" or "\\n"
    /// instead of real line breaks, or typographic punctuation/spaces outside
    /// comments and string literals. Those payloads are accepted by GeneXus and
    /// silently alter behavior (for example, a leading // comment with literal
    /// "\\n" swallows the rest of the part, and smart quotes/dashes/NBSP are not
    /// valid GeneXus delimiters or operators). The guard is deliberately a
    /// rejection, not an automatic rewrite: the same characters are valid inside
    /// source string literals and comments.
    /// </summary>
    internal static class TextPayloadGuard
    {
        internal const string ErrorCode = "LiteralLineBreaksDetected";
        internal const string TypographicErrorCode = "TypographicCharactersDetected";

        private sealed class TypographicSpec
        {
            internal TypographicSpec(string label)
            {
                Label = label;
            }

            internal string Label { get; }
        }

        private static readonly Dictionary<char, TypographicSpec> TypographicCharactersMap = new Dictionary<char, TypographicSpec>
        {
            ['\u2018'] = new TypographicSpec("\u2018 (U+2018 left single quote)"),
            ['\u2019'] = new TypographicSpec("\u2019 (U+2019 right single quote)"),
            ['\u201C'] = new TypographicSpec("\u201C (U+201C left double quote)"),
            ['\u201D'] = new TypographicSpec("\u201D (U+201D right double quote)"),
            ['\u2013'] = new TypographicSpec("\u2013 (U+2013 en dash)"),
            ['\u2014'] = new TypographicSpec("\u2014 (U+2014 em dash)"),
            ['\u2026'] = new TypographicSpec("\u2026 (U+2026 ellipsis)"),
            ['\u00A0'] = new TypographicSpec("NBSP (U+00A0 non-breaking space)")
        };

        internal sealed class LiteralMatch
        {
            internal string Sequence;
            internal int Index;
            internal int Line;
        }

        internal sealed class TypographicMatch
        {
            internal string Label;
            internal int Index;
            internal int Line;
        }

        private sealed class ScanResult
        {
            internal List<LiteralMatch> LiteralMatches { get; } = new List<LiteralMatch>();
            internal List<TypographicMatch> TypographicMatches { get; } = new List<TypographicMatch>();
        }

        internal sealed class Issue
        {
            private readonly List<string> _literalSequences = new List<string>();
            private readonly List<string> _typographicCharacters = new List<string>();
            private readonly List<int> _lines = new List<int>();
            private int _firstLiteralIndex = int.MaxValue;
            private int _firstIndex = int.MaxValue;
            private int _firstLine = int.MaxValue;

            internal Issue(bool hasActualLineBreaks, bool startsWithLineComment)
            {
                HasActualLineBreaks = hasActualLineBreaks;
                StartsWithLineComment = startsWithLineComment;
            }

            public bool HasActualLineBreaks { get; }
            public bool StartsWithLineComment { get; }
            public IList<string> LiteralSequences => _literalSequences;
            public int FirstLiteralIndex => _firstLiteralIndex == int.MaxValue ? -1 : _firstLiteralIndex;
            public IList<string> TypographicCharacters => _typographicCharacters;
            public int FirstIndex => _firstIndex == int.MaxValue ? -1 : _firstIndex;
            public int FirstLine => _firstLine == int.MaxValue ? 1 : _firstLine;
            public IList<int> Lines => _lines;
            public string ErrorCode => _literalSequences.Count > 0 ? TextPayloadGuard.ErrorCode : TypographicErrorCode;
            public bool HasLiteralLineBreaks => _literalSequences.Count > 0;
            public bool HasTypographicCharacters => _typographicCharacters.Count > 0;

            internal void AddLiteralMatches(List<LiteralMatch> matches)
            {
                foreach (var match in matches)
                {
                    if (match.Index < _firstLiteralIndex) _firstLiteralIndex = match.Index;
                    RecordLocation(match.Index, match.Line);
                    if (!_literalSequences.Contains(match.Sequence)) _literalSequences.Add(match.Sequence);
                }
            }

            internal void AddTypographicMatches(List<TypographicMatch> matches)
            {
                foreach (var match in matches)
                {
                    RecordLocation(match.Index, match.Line);
                    if (!_typographicCharacters.Contains(match.Label)) _typographicCharacters.Add(match.Label);
                }
            }

            internal void SortLines()
            {
                _lines.Sort();
            }

            private void RecordLocation(int index, int line)
            {
                if (index < _firstIndex)
                {
                    _firstIndex = index;
                    _firstLine = line;
                }
                if (!_lines.Contains(line)) _lines.Add(line);
            }
        }

        internal static bool AppliesToPart(string partName)
        {
            return string.Equals(partName, "Source", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Rules", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Events", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Code", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Variables", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Conditions", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Parameters", StringComparison.OrdinalIgnoreCase);
        }

        internal static Issue Analyze(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            bool hasActualLineBreaks = text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0;
            bool startsWithLineComment = text.TrimStart(' ', '\t', '\uFEFF').StartsWith("//", StringComparison.Ordinal);
            var codeScan = Scan(text, codeOnly: true);

            // A source that starts with // and has no physical line break is the
            // exact failure mode where all generated code becomes unreachable.
            List<LiteralMatch> allLiteralMatches = null;
            bool commentSwallowsPayload = false;
            if (startsWithLineComment && !hasActualLineBreaks)
            {
                allLiteralMatches = Scan(text, codeOnly: false).LiteralMatches;
                commentSwallowsPayload = allLiteralMatches.Count > 0;
            }

            if (!commentSwallowsPayload
                && codeScan.LiteralMatches.Count == 0
                && codeScan.TypographicMatches.Count == 0)
            {
                return null;
            }

            var issue = new Issue(hasActualLineBreaks, startsWithLineComment);
            if (commentSwallowsPayload)
                issue.AddLiteralMatches(allLiteralMatches);
            issue.AddLiteralMatches(codeScan.LiteralMatches);
            issue.AddTypographicMatches(codeScan.TypographicMatches);
            issue.SortLines();
            return issue;
        }

        internal static JObject BuildFieldError(string field, string text)
        {
            var issue = Analyze(text);
            if (issue == null) return null;

            var error = new JObject
            {
                ["field"] = field,
                ["code"] = issue.ErrorCode,
                ["line"] = issue.FirstLine,
                ["lines"] = ToJArray(issue.Lines),
                ["errors"] = new JArray(BuildMessage(field, issue)),
                ["hasActualLineBreaks"] = issue.HasActualLineBreaks
            };
            if (issue.HasLiteralLineBreaks)
                error["literalSequences"] = ToJArray(issue.LiteralSequences);
            if (issue.HasTypographicCharacters)
                error["typographicCharacters"] = ToJArray(issue.TypographicCharacters);
            return error;
        }

        internal static string BuildWriteError(string target, string partName, string field, string text)
        {
            var issue = Analyze(text);
            if (issue == null) return null;

            var extra = new JObject
            {
                ["part"] = partName,
                ["field"] = field,
                ["inputLength"] = text == null ? 0 : text.Length,
                ["line"] = issue.FirstLine,
                ["lines"] = ToJArray(issue.Lines),
                ["hasActualLineBreaks"] = issue.HasActualLineBreaks
            };
            if (issue.HasLiteralLineBreaks)
            {
                extra["literalSequences"] = ToJArray(issue.LiteralSequences);
                extra["firstLiteralIndex"] = issue.FirstLiteralIndex;
            }
            if (issue.HasTypographicCharacters)
            {
                extra["typographicCharacters"] = ToJArray(issue.TypographicCharacters);
            }

            return McpResponse.Err(
                code: issue.ErrorCode,
                message: BuildMessage(field, issue),
                hint: BuildHint(issue),
                target: target,
                extra: extra);
        }

        internal static string BuildMessage(string field)
        {
            return "Field '" + (field ?? "text")
                + "' contains literal line break escape sequences (\\r\\n, \\n, or \\r); send actual line breaks. Nothing was written.";
        }

        internal static string BuildMessage(string field, Issue issue)
        {
            if (issue == null) return BuildMessage(field);

            string fieldName = field ?? "text";
            string location = FormatLineLocation(issue);

            if (issue.HasLiteralLineBreaks && issue.HasTypographicCharacters)
            {
                return "Field '" + fieldName
                    + "' contains literal line break escape sequences (" + string.Join(", ", issue.LiteralSequences)
                    + ") and typographic characters outside comments or strings (" + string.Join(", ", issue.TypographicCharacters)
                    + ") " + location + "; send actual line breaks and plain ASCII characters. Nothing was written.";
            }

            if (issue.HasTypographicCharacters)
            {
                return "Field '" + fieldName
                    + "' contains typographic characters outside comments or strings (" + string.Join(", ", issue.TypographicCharacters)
                    + ") " + location + "; use plain ASCII characters. Nothing was written.";
            }

            return "Field '" + fieldName
                + "' contains literal line break escape sequences (" + string.Join(", ", issue.LiteralSequences)
                + ") " + location + "; send actual line breaks. Nothing was written.";
        }

        private static string BuildHint(Issue issue)
        {
            if (issue.HasLiteralLineBreaks && issue.HasTypographicCharacters)
            {
                return "Send real line breaks (do not send literal \\r\\n, \\n, or \\r or double-encode JSON) and replace typographic characters outside comments/strings with ASCII equivalents (' \" - ... or regular space).";
            }

            if (issue.HasTypographicCharacters)
            {
                return "Replace typographic quotes, dashes, ellipsis, and non-breaking spaces outside comments or string literals with plain ASCII equivalents (' \" - ... or regular space).";
            }

            return "Send the text with real line breaks. Do not send the literal sequences \\r\\n, \\n, or \\r, and do not double-encode the JSON payload.";
        }

        private static string FormatLineLocation(Issue issue)
        {
            if (issue.Lines.Count <= 1)
                return "on line " + issue.FirstLine;

            const int maxListed = 10;
            var listed = new List<string>();
            int limit = Math.Min(issue.Lines.Count, maxListed);
            for (int i = 0; i < limit; i++)
                listed.Add(issue.Lines[i].ToString());
            string suffix = issue.Lines.Count > maxListed ? ", ..." : string.Empty;
            return "starting on line " + issue.FirstLine + " (lines " + string.Join(", ", listed) + suffix + ")";
        }

        private static ScanResult Scan(string text, bool codeOnly)
        {
            var result = new ScanResult();
            int line = 1;
            char quote = '\0';
            bool lineComment = false;
            bool blockComment = false;

            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (current == '\r')
                {
                    if (next == '\n') i++;
                    line++;
                    lineComment = false;
                    quote = '\0';
                    continue;
                }

                if (current == '\n')
                {
                    line++;
                    lineComment = false;
                    quote = '\0';
                    continue;
                }

                if (codeOnly && lineComment)
                {
                    continue;
                }

                if (codeOnly && blockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        blockComment = false;
                        i++;
                    }
                    continue;
                }

                if (codeOnly && quote != '\0')
                {
                    if (current == quote)
                    {
                        if (next == quote) i++;
                        else quote = '\0';
                    }
                    continue;
                }

                if (codeOnly && (current == '\'' || current == '"'))
                {
                    quote = current;
                    continue;
                }

                if (codeOnly && current == '/' && next == '/')
                {
                    lineComment = true;
                    i++;
                    continue;
                }

                if (codeOnly && current == '/' && next == '*')
                {
                    blockComment = true;
                    i++;
                    continue;
                }

                string sequence;
                int length = MatchLiteral(text, i, out sequence);
                if (length > 0)
                {
                    result.LiteralMatches.Add(new LiteralMatch { Sequence = sequence, Index = i, Line = line });
                    i += length - 1;
                    continue;
                }

                if (codeOnly && TypographicCharactersMap.TryGetValue(current, out var spec))
                {
                    result.TypographicMatches.Add(new TypographicMatch { Label = spec.Label, Index = i, Line = line });
                }
            }

            return result;
        }

        private static int MatchLiteral(string text, int index, out string sequence)
        {
            sequence = null;
            if (index + 1 >= text.Length || text[index] != '\\') return 0;

            if (text[index + 1] == 'r')
            {
                if (index + 3 < text.Length && text[index + 2] == '\\' && text[index + 3] == 'n')
                {
                    sequence = "\\r\\n";
                    return 4;
                }

                sequence = "\\r";
                return 2;
            }

            if (text[index + 1] == 'n')
            {
                sequence = "\\n";
                return 2;
            }

            return 0;
        }

        private static JArray ToJArray(IList<string> values)
        {
            var array = new JArray();
            foreach (var value in values) array.Add(value);
            return array;
        }

        private static JArray ToJArray(IList<int> values)
        {
            var array = new JArray();
            foreach (var value in values) array.Add(value);
            return array;
        }
    }
}
