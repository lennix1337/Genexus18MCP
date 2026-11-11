using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Version-neutral extraction from the textual Design System Tokens and Styles parts.
    /// This is deliberately a best-effort fallback: the SDK helper remains authoritative
    /// whenever its corresponding member exists and succeeds.
    /// </summary>
    internal sealed class DesignSystemSourceParseResult
    {
        internal DesignSystemSourceParseResult()
        {
            TokenGroups = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            Classes = new List<string>();
            Images = new List<string>();
            ReferencedDSOs = new List<string>();
            Warnings = new List<string>();
            UnparsedConstructs = new List<string>();
        }

        internal Dictionary<string, JObject> TokenGroups { get; private set; }
        internal List<string> Classes { get; private set; }
        internal List<string> Images { get; private set; }
        internal List<string> ReferencedDSOs { get; private set; }
        internal List<string> Warnings { get; private set; }
        internal List<string> UnparsedConstructs { get; private set; }
        internal string Completeness
        {
            get
            {
                if (Warnings.Count > 0 || UnparsedConstructs.Count > 0) return "partial";
                if (TokenGroups.Count == 0 && Classes.Count == 0 && Images.Count == 0 && ReferencedDSOs.Count == 0)
                    return "empty";
                return "complete";
            }
        }
    }

    internal static class DesignSystemSourceParser
    {
        private static readonly Regex TokenGroupStartRegex = new Regex(
            @"#(?<name>[A-Za-z0-9_-]+)\s*\{",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex ClassStartRegex = new Regex(
            @"\.(?<name>[A-Za-z0-9_:-]+)\s*\{",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex DeclarationRegex = new Regex(
            @"([A-Za-z0-9_-]+)\s*:\s*([^;]+);",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex ImageRegex = new Regex(
            @"\bgx-image\s*\(\s*(?:['""](?<quotedName>[^'""]+)['""]|(?<bareName>[A-Za-z_][A-Za-z0-9_.]*))\s*\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ImportRegex = new Regex(
            @"@import\b(?<body>[^;]*);",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex ImportedNameRegex = new Regex(
            @"(?<!['""])(?<name>[A-Za-z_][A-Za-z0-9_.]*)(?=\s*(?:,|$))",
            RegexOptions.Compiled);

        internal static DesignSystemSourceParseResult Parse(string tokensSource, string stylesSource)
        {
            var result = new DesignSystemSourceParseResult();
            MergeTokens(result.TokenGroups, ParseTokenBlocks(tokensSource, result));
            MergeTokens(result.TokenGroups, ParseTokenBlocks(stylesSource, result));

            AddUnique(result.Classes, ParseClassBlocks(stylesSource, result).Keys);

            ValidateBalance(tokensSource, "Tokens", result);
            ValidateBalance(stylesSource, "Styles", result);

            string cleanStyles = StripComments(stylesSource);
            AddUnique(result.Images, ExtractImageNames(stylesSource));

            foreach (Match import in ImportRegex.Matches(cleanStyles))
            {
                foreach (Match candidate in ImportedNameRegex.Matches(import.Groups["body"].Value))
                {
                    string name = NormalizeImportedDso(candidate.Groups["name"].Value);
                    if (!string.IsNullOrEmpty(name)) AddUnique(result.ReferencedDSOs, name);
                }
            }

            return result;
        }

        /// <summary>
        /// The Image objects a style source refers to through <c>gx-image(Name)</c> or <c>gx-image("Name")</c>, in order of
        /// appearance, each name once (compared without regard to case). A reference inside a comment is ignored.
        /// </summary>
        internal static IReadOnlyList<string> ExtractImageNames(string styleSource)
        {
            var names = new List<string>();
            foreach (Match match in ImageRegex.Matches(StripComments(styleSource)))
            {
                string imageName = match.Groups["quotedName"].Success
                    ? match.Groups["quotedName"].Value
                    : match.Groups["bareName"].Value;
                AddUnique(names, imageName.Trim());
            }
            return names;
        }

        internal static Dictionary<string, JObject> ParseTokens(string source)
        {
            return ParseTokenBlocks(source, null);
        }

        internal static Dictionary<string, JObject> ParseClasses(string source)
        {
            return ParseClassBlocks(source, null);
        }

        private static Dictionary<string, JObject> ParseTokenBlocks(
            string source,
            DesignSystemSourceParseResult diagnostics)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(source)) return result;

            string cleanSource = StripComments(source);
            foreach (Match start in TokenGroupStartRegex.Matches(cleanSource))
            {
                string groupName = start.Groups["name"].Value.Trim();
                int openBrace = cleanSource.IndexOf('{', start.Index, start.Length);
                int closeBrace = FindMatchingBrace(cleanSource, openBrace);
                if (closeBrace < 0)
                {
                    AddDiagnostic(diagnostics, "Unclosed token group: " + groupName, "token-group:" + groupName);
                    continue;
                }

                string body = cleanSource.Substring(openBrace + 1, closeBrace - openBrace - 1);
                var group = new JObject();
                foreach (Match itemMatch in DeclarationRegex.Matches(body))
                {
                    string name = itemMatch.Groups[1].Value.Trim();
                    group[name] = itemMatch.Groups[2].Value.Trim();
                }
                if (group.Count == 0 && !string.IsNullOrWhiteSpace(body))
                    AddDiagnostic(diagnostics, "Token group could not be parsed: " + groupName, "token-group:" + groupName);
                result[groupName] = group;
            }

            return result;
        }

        private static Dictionary<string, JObject> ParseClassBlocks(
            string source,
            DesignSystemSourceParseResult diagnostics)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(source)) return result;

            string cleanSource = StripComments(source);
            foreach (Match start in ClassStartRegex.Matches(cleanSource))
            {
                string className = start.Groups["name"].Value.Trim();
                int openBrace = cleanSource.IndexOf('{', start.Index, start.Length);
                int closeBrace = FindMatchingBrace(cleanSource, openBrace);
                if (closeBrace < 0)
                {
                    AddDiagnostic(diagnostics, "Unclosed style class: " + className, "style-class:" + className);
                    continue;
                }

                string body = cleanSource.Substring(openBrace + 1, closeBrace - openBrace - 1);
                var classBody = new JObject();
                foreach (Match propertyMatch in DeclarationRegex.Matches(body))
                {
                    string name = propertyMatch.Groups[1].Value.Trim();
                    classBody[name] = propertyMatch.Groups[2].Value.Trim();
                }
                if (classBody.Count == 0 && !string.IsNullOrWhiteSpace(body))
                    AddDiagnostic(diagnostics, "Style class could not be parsed: " + className, "style-class:" + className);
                result[className] = classBody;
            }

            return result;
        }

        private static void MergeTokens(
            Dictionary<string, JObject> target,
            Dictionary<string, JObject> source)
        {
            foreach (KeyValuePair<string, JObject> pair in source)
                target[pair.Key] = pair.Value;
        }

        private static void AddUnique(List<string> target, IEnumerable<string> values)
        {
            foreach (string value in values) AddUnique(target, value);
        }

        private static void AddUnique(List<string> target, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            for (int i = 0; i < target.Count; i++)
                if (string.Equals(target[i], value, StringComparison.OrdinalIgnoreCase)) return;
            target.Add(value);
        }

        private static string NormalizeImportedDso(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            string name = candidate.Trim();
            if (name.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.EndsWith(".tokens", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".tokens".Length);
            else if (name.EndsWith(".styles", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".styles".Length);
            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// Blanks out comments while preserving every offset: a comment character
        /// becomes a space, a newline stays a newline. Callers index the returned
        /// string with the positions they used on the input, so the length and the
        /// line structure have to survive.
        /// </summary>
        private static string StripComments(string source)
        {
            if (string.IsNullOrEmpty(source)) return string.Empty;

            var result = new StringBuilder(source.Length);
            bool inSingle = false;
            bool inDouble = false;
            bool escaped = false;
            bool inLineComment = false;
            bool inBlockComment = false;
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';
                if (inLineComment)
                {
                    if (c == '\n')
                    {
                        inLineComment = false;
                        result.Append(c);
                    }
                    else
                    {
                        result.Append(' ');
                    }
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        result.Append("  ");
                        i++;
                    }
                    else
                    {
                        result.Append(c == '\n' ? '\n' : ' ');
                    }
                    continue;
                }
                if (inSingle || inDouble)
                {
                    result.Append(c);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if ((inSingle && c == '\'') || (inDouble && c == '"'))
                    {
                        inSingle = false;
                        inDouble = false;
                    }
                    continue;
                }
                if (c == '/' && next == '/')
                {
                    inLineComment = true;
                    result.Append("  ");
                    i++;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    result.Append("  ");
                    i++;
                    continue;
                }
                result.Append(c);
                if (c == '\'') inSingle = true;
                else if (c == '"') inDouble = true;
            }
            return result.ToString();
        }

        private static void ValidateBalance(
            string source,
            string partName,
            DesignSystemSourceParseResult diagnostics)
        {
            if (diagnostics == null || string.IsNullOrWhiteSpace(source)) return;
            int depth = 0;
            ScanCode(source, 0, source.Length, (c, i) =>
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth < 0)
                    {
                        AddDiagnostic(diagnostics, partName + " contains a closing brace without an opening brace.", partName + ":unbalanced");
                        return false; // stop: already unbalanced
                    }
                }
                return true;
            });

            if (depth != 0)
                AddDiagnostic(diagnostics, partName + " contains an unclosed brace block.", partName + ":unbalanced");
        }

        /// <summary>
        /// Walks <paramref name="source"/> and invokes
        /// <paramref name="onCodeChar"/> for every character that is real code —
        /// outside line comments, block comments, and single/double-quoted
        /// strings — with string escapes handled. Returning false stops the scan.
        ///
        /// This lexer existed twice more in this file, in
        /// <c>ValidateBalance</c> and <c>FindMatchingBrace</c>, both of which only
        /// needed to know which braces were structural. <c>StripComments</c> keeps
        /// its own walk because it must re-emit comment characters as blanks to
        /// preserve offsets rather than skip them.
        ///
        /// Two copies of a lexer is two chances for the string rules to disagree,
        /// and the failure is silent rather than loud: a brace inside a comment or
        /// a quoted selector changes the reported nesting depth, which shows up as
        /// a spurious "unbalanced" diagnostic or, worse, a block boundary found in
        /// the wrong place so one rule body is parsed as two.
        /// </summary>
        /// <param name="start">Index to begin at.</param>
        /// <param name="end">Index to stop before (exclusive).</param>
        private static void ScanCode(
            string source,
            int start,
            int end,
            Func<char, int, bool> onCodeChar)
        {
            bool inSingle = false;
            bool inDouble = false;
            bool escaped = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            for (int i = start; i < end; i++)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';
                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }
                if (!inSingle && !inDouble && c == '/' && next == '/') { inLineComment = true; i++; continue; }
                if (!inSingle && !inDouble && c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (inSingle || inDouble)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if ((inSingle && c == '\'') || (inDouble && c == '"'))
                    {
                        inSingle = false;
                        inDouble = false;
                    }
                    continue;
                }
                if (c == '\'') { inSingle = true; continue; }
                if (c == '"') { inDouble = true; continue; }
                if (!onCodeChar(c, i)) return;
            }
        }

        private static int FindMatchingBrace(string source, int openBrace)
        {
            if (openBrace < 0 || openBrace >= source.Length || source[openBrace] != '{') return -1;
            int depth = 0;
            int match = -1;
            ScanCode(source, openBrace, source.Length, (c, i) =>
            {
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) { match = i; return false; }
                return true;
            });
            return match;
        }

        private static void AddDiagnostic(
            DesignSystemSourceParseResult diagnostics,
            string warning,
            string unparsed)
        {
            if (diagnostics == null) return;
            AddUnique(diagnostics.Warnings, warning);
            AddUnique(diagnostics.UnparsedConstructs, unparsed);
        }
    }
}
