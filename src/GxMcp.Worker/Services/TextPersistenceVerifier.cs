using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Compares requested text with a forced post-save read without confusing
    /// GeneXus' harmless text rendering with a failed persistence operation.
    /// </summary>
    internal static class TextPersistenceVerifier
    {
        internal sealed class Result
        {
            public string Mode { get; set; }
            public bool Matches { get; set; }
            public string RequestedHash { get; set; }
            public string PersistedHash { get; set; }
            public string NormalizedRequestedHash { get; set; }
            public string NormalizedPersistedHash { get; set; }
            public JArray NormalizationApplied { get; set; }
            public string DiffNormalized { get; set; }
            public string NormalizedRequested { get; set; }
            public string NormalizedPersisted { get; set; }

            public JObject ToJson(bool reReadConfirmed)
            {
                return new JObject
                {
                    ["mode"] = Mode,
                    ["normalizationApplied"] = NormalizationApplied ?? new JArray(),
                    ["diffNormalized"] = DiffNormalized == null ? JValue.CreateNull() : (JToken)DiffNormalized,
                    ["reReadConfirmed"] = reReadConfirmed
                };
            }
        }

        internal static string ResolveMode(string requestedMode, string partName)
        {
            string mode = (requestedMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode.Length == 0)
            {
                if (string.Equals(partName, "Structure", StringComparison.OrdinalIgnoreCase))
                    return "semantic";
                return IsCodeOrTextPart(partName) ? "normalized" : "exact";
            }
            if (mode != "exact" && mode != "normalized" && mode != "semantic")
                throw new ArgumentException("verifyMode must be exact, normalized, or semantic.", nameof(requestedMode));
            return mode;
        }

        internal static Result Evaluate(string requested, string persisted, string requestedMode, string partName)
        {
            requested = requested ?? string.Empty;
            persisted = persisted ?? string.Empty;
            string mode = ResolveMode(requestedMode, partName);
            string nr = Normalize(requested);
            string np = Normalize(persisted);
            bool matches;
            if (mode == "exact")
                matches = string.Equals(
                    ExactCanonicalize(requested, partName),
                    ExactCanonicalize(persisted, partName),
                    StringComparison.Ordinal);
            else if (mode == "semantic")
                matches = string.Equals(
                    SemanticCanonicalize(nr, partName),
                    SemanticCanonicalize(np, partName),
                    StringComparison.Ordinal);
            else
                matches = string.Equals(nr, np, StringComparison.Ordinal);

            return new Result
            {
                Mode = mode,
                Matches = matches,
                RequestedHash = Sha256(requested),
                PersistedHash = Sha256(persisted),
                NormalizedRequestedHash = Sha256(nr),
                NormalizedPersistedHash = Sha256(np),
                NormalizationApplied = DetectNormalizations(requested, persisted),
                DiffNormalized = BuildDiff(nr, np),
                NormalizedRequested = nr,
                NormalizedPersisted = np
            };
        }

        internal static string Canonicalize(string text, string requestedMode, string partName)
        {
            string mode = ResolveMode(requestedMode, partName);
            if (mode == "exact") return ExactCanonicalize(text, partName);
            string normalized = Normalize(text);
            return mode == "semantic" ? SemanticCanonicalize(normalized, partName) : normalized;
        }

        internal static string Normalize(string text)
        {
            string value = (text ?? string.Empty).TrimStart('\uFEFF').Normalize(NormalizationForm.FormC)
                .Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = value.Split('\n');
            var output = new List<string>(lines.Length);
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd(' ', '\t');
                bool blank = line.Length == 0;
                // Empty lines are formatting-only in Source/Rules and the SDK may
                // insert, collapse, or remove them while rendering a saved part.
                if (blank) continue;
                output.Add(line);
            }
            return string.Join("\n", output);
        }

        private static string ExactCanonicalize(string text, string partName)
        {
            string value = text ?? string.Empty;
            // Line endings are canonicalized for every part, not only the code
            // ones. A Structure is authored and re-read by different layers: the
            // requested payload can carry CRLF while the forced post-save SDK
            // read returns LF. Under `exact` that difference alone failed the
            // comparison and reported `WriteNotPersisted` for a write that had
            // landed (#494). Line endings carry no content in any part, so
            // normalizing them cannot hide a real difference.
            return value.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>
        /// Drops the description GeneXus generates for an attribute from its
        /// name, so a write that landed is not reported as
        /// <c>WriteNotPersisted</c> merely because the SDK filled in a detail the
        /// request never mentioned (#494).
        ///
        /// Scoped narrowly on purpose:
        ///
        /// - Only a description that matches what the SDK derives from the
        ///   attribute's own name is dropped. "Generated" cannot be told from
        ///   "caller-supplied" any other way, and treating every description as
        ///   generated would pass a write that wrote the wrong description.
        /// - Only for a Structure part. On a code part `semantic` must stay a
        ///   pure keyword/spacing canonicalisation.
        /// - A formula or `[Nullable]` after the description is preserved
        ///   verbatim, since that part is what the caller wrote.
        /// - Containment is deliberately not the rule: "persisted contains
        ///   requested" proves an addition but not a removal.
        ///
        /// Known limitation: this closes the reported receipt, but the live
        /// reproduction in #494 also shows the SDK not honouring the requested
        /// insertion point (`after`), which is a separate defect in the
        /// Structure apply path and is tracked in
        /// <c>docs/plans/2026-10-13-open-issues-plan.md</c>.
        /// </summary>
        private static string SemanticCanonicalize(string text, string partName)
        {
            // The generated-description strip only applies to a Structure part.
            // Applying it to a code part would rewrite lines that merely contain a
            // comment, and `semantic` on Source/Rules/Events must stay a pure
            // keyword/spacing canonicalisation.
            if (IsCodeOrTextPart(partName))
                return SemanticCanonicalizeRaw(text);

            // Strip first, on the raw text. The description is part of the DSL's
            // rendering, so it has to be recognised before keyword casing and
            // whitespace normalisation alter the line around it.
            return SemanticCanonicalizeRaw(StripSdkGeneratedAttributeDescriptions(text));
        }

        /// <summary>
        /// Removes the description the SDK generates for an attribute, line by
        /// line.
        ///
        /// "Generated" is decided by the text itself: the description must equal
        /// what GeneXus derives from the attribute's own name. That is the only
        /// rule that can tell a generated description from a caller-supplied one,
        /// and it matters - stripping any description would make two genuinely
        /// different descriptions compare equal, which is a false positive in a
        /// write verdict.
        ///
        /// The same rule is what the DSL renderer already applies: a description
        /// that merely repeats the name is not rendered at all. So a requested
        /// `// "Attr"` on `Attr` and a persisted line with no comment are the same
        /// declaration, and comparing them as different would be its own false
        /// mismatch.
        /// </summary>
        internal static string StripSdkGeneratedAttributeDescriptions(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("//", StringComparison.Ordinal) < 0) return text;

            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new StringBuilder(text.Length);

            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd(' ', '\t');
                if (!TryGetGeneratedDescription(trimmed, out _, out string declaration))
                {
                    sb.Append(line).Append('\n');
                    continue;
                }
                sb.Append(declaration).Append('\n');
            }

            // Preserve the absence of a trailing newline on the final line.
            if (!text.EndsWith("\n", StringComparison.Ordinal) && sb.Length > 0)
                sb.Length -= 1;

            return sb.ToString();
        }

        /// <summary>
        /// True when a Structure part differs from the request only by the attribute
        /// description GeneXus derives from the attribute name: once that description
        /// is dropped from both texts, the normalized lines are identical. Lets a
        /// receipt label a textual mismatch the caller opted into (<c>normalized</c>
        /// or <c>exact</c>) so it is not mistaken for a lost write (#557).
        /// </summary>
        internal static bool OnlySdkGeneratedAttributeDescriptionDiffers(string partName, string requested, string persisted)
        {
            if (!string.Equals(partName, "Structure", StringComparison.OrdinalIgnoreCase)) return false;
            string nr = Normalize(requested);
            string np = Normalize(persisted);
            // GeneXus identifiers and type keywords are case-insensitive, and the SDK
            // renders a requested `Numeric(4)` as `NUMERIC(4)` next to the derived
            // description (GX18 U16 proof) — a rendering `semantic` already accepts. The
            // label is about the description, so the keyword casing must neither hide it
            // nor, on its own, earn it.
            if (string.Equals(nr, np, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(
                StripSdkGeneratedAttributeDescriptions(nr),
                StripSdkGeneratedAttributeDescriptions(np),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Recognises an attribute declaration whose trailing comment is only the
        /// description GeneXus derives from the attribute name. Returns the
        /// declaration with that description removed.
        /// </summary>
        private static bool TryGetGeneratedDescription(string trimmedLine, out string description, out string declaration)
        {
            description = null;
            declaration = null;

            int commentAt = trimmedLine.IndexOf("//", StringComparison.Ordinal);
            if (commentAt < 0) return false;

            declaration = trimmedLine.Substring(0, commentAt).TrimEnd(' ', '\t');

            // An attribute declaration is `Name[+*] : Type`; a level, a rule or a
            // bare comment must be left alone.
            int colon = declaration.IndexOf(':');
            if (colon <= 0) return false;

            string name = declaration.Substring(0, colon).TrimEnd(' ', '\t');
            // Strip the key/nullable markers GeneXus puts in the declaration.
            name = name.TrimEnd('+', '*', ' ').Trim();
            if (name.Length == 0) return false;

            string detail = trimmedLine.Substring(commentAt + 2).Trim();
            if (detail.Length < 2 || detail[0] != '"') return false;

            int close = detail.IndexOf('"', 1);
            if (close < 0) return false;

            string quoted = detail.Substring(1, close - 1).Trim();
            if (!DescriptionMatchesName(quoted, name)) return false;

            // A generated description is *not* the only detail a line can carry:
            // the SDK renders `// "Attr Name", [Formula: ...]` and
            // `// "Attr Name", [Nullable]`. Keep whatever follows the description
            // verbatim, so only the description itself is dropped. Anything that
            // is not a recognised detail marker means the caller put something
            // else there and the line must be compared as written.
            string after = detail.Substring(close + 1).Trim();
            if (after.Length > 0)
            {
                if (after.StartsWith(",", StringComparison.Ordinal))
                    after = after.Substring(1).Trim();

                bool recognised =
                    after.StartsWith("//", StringComparison.Ordinal)
                    || after.StartsWith("[", StringComparison.Ordinal)
                    || after.Length == 0;

                if (!recognised) return false;
            }

            // Reassemble with the remaining detail so the formula or nullable
            // marker survives into the canonical form.
            declaration = declaration + (after.Length > 0 ? " // " + after : string.Empty);
            description = quoted;
            return true;
        }

        /// <summary>
        /// True when <paramref name="description"/> is what GeneXus would derive
        /// from <paramref name="name"/>. Two renderings are accepted:
        ///
        /// - the name split on camel-case boundaries and joined with spaces
        ///   (<c>CustomerId</c> → "Customer Id"), compared case-insensitively and
        ///   ignoring punctuation the derivation also drops;
        /// - the same letters and digits in the same order, wherever the spaces
        ///   fall. The SDK keeps a leading run of capitals as one word
        ///   (<c>ZTstMcpArq</c> → "ZTst Mcp Arq", observed live in #541), where
        ///   the split above yields "Z Tst Mcp Arq". A description rebuilt from
        ///   the name cannot contain other letters, so comparing the alphanumeric
        ///   skeleton tells generated from caller-supplied without guessing the
        ///   SDK's exact word boundaries. A caller-supplied description that merely
        ///   re-spells the name (<c>"CUSTOMERID"</c>) is dropped as well; it carries
        ///   nothing the name does not.
        /// </summary>
        private static bool DescriptionMatchesName(string description, string name)
        {
            if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(name)) return false;

            string descAlphanumeric = new string(description.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            string nameAlphanumeric = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (descAlphanumeric.Length > 0 && descAlphanumeric == nameAlphanumeric)
                return true;

            var derived = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool boundary = i > 0
                    && char.IsUpper(c)
                    && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (boundary) derived.Append(' ');
                derived.Append(c);
            }

            return NormalizeForDescriptionCompare(description)
                == NormalizeForDescriptionCompare(derived.ToString());
        }

        private static string NormalizeForDescriptionCompare(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        private static string SemanticCanonicalizeRaw(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool inString = false;
            char quote = '\0';
            bool whitespacePending = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote)
                        {
                            sb.Append(text[++i]);
                        }
                        else inString = false;
                    }
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    if (whitespacePending && sb.Length > 0 && IsWordish(sb[sb.Length - 1])) sb.Append(' ');
                    whitespacePending = false;
                    inString = true;
                    quote = c;
                    sb.Append(c);
                }
                else if (char.IsWhiteSpace(c))
                {
                    whitespacePending = true;
                }
                else
                {
                    if (whitespacePending && sb.Length > 0 && IsWordish(sb[sb.Length - 1]) && IsWordish(c)) sb.Append(' ');
                    whitespacePending = false;
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString().Trim();
        }

        private static JArray DetectNormalizations(string requested, string persisted)
        {
            var result = new JArray();
            if (HasDifferentEol(requested, persisted)) result.Add("EOL");
            if (requested.StartsWith("\uFEFF", StringComparison.Ordinal) != persisted.StartsWith("\uFEFF", StringComparison.Ordinal)
                || !string.Equals(requested, requested.Normalize(NormalizationForm.FormC), StringComparison.Ordinal)
                || !string.Equals(persisted, persisted.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
                result.Add("encoding");
            if (HasTrailingWhitespaceDifference(requested, persisted)) result.Add("trailing-whitespace");
            if (HasBlankLineDifference(requested, persisted)) result.Add("blank-lines");
            return result;
        }

        private static bool HasDifferentEol(string a, string b)
        {
            return Count(a, "\r\n") != Count(b, "\r\n") || CountLoneLf(a) != CountLoneLf(b) || CountLoneCr(a) != CountLoneCr(b);
        }

        private static bool HasTrailingWhitespaceDifference(string a, string b)
        {
            string Strip(string value) => string.Join("\n", (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Select(x => x.TrimEnd(' ', '\t')));
            bool aHasTrailing = (a ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Any(x => x.Length != x.TrimEnd(' ', '\t').Length);
            bool bHasTrailing = (b ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Any(x => x.Length != x.TrimEnd(' ', '\t').Length);
            return aHasTrailing != bHasTrailing || (!string.Equals(a, b, StringComparison.Ordinal) && string.Equals(Strip(a), Strip(b), StringComparison.Ordinal));
        }

        private static bool HasBlankLineDifference(string a, string b)
        {
            string Prepare(string value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").TrimStart('\uFEFF');
            string Collapse(string value) => string.Join("\n", Prepare(value).Split('\n').Where((line, index) => line.Trim().Length > 0 || index == 0));
            return !string.Equals(Prepare(a), Prepare(b), StringComparison.Ordinal) && string.Equals(Collapse(a), Collapse(b), StringComparison.Ordinal);
        }

        private static string BuildDiff(string requested, string persisted)
        {
            if (string.Equals(requested, persisted, StringComparison.Ordinal)) return null;
            string[] left = requested.Split('\n');
            string[] right = persisted.Split('\n');
            int count = Math.Max(left.Length, right.Length);
            for (int i = 0; i < count; i++)
            {
                string l = i < left.Length ? left[i] : "<missing>";
                string r = i < right.Length ? right[i] : "<missing>";
                if (!string.Equals(l, r, StringComparison.Ordinal))
                    return "line " + (i + 1) + ": requested=" + Clip(l) + "; persisted=" + Clip(r);
            }
            return "normalized content differs";
        }

        private static string Clip(string value) => value.Length <= 160 ? value : value.Substring(0, 160) + "…";
        private static bool IsWordish(char value) => char.IsLetterOrDigit(value) || value == '_' || value == '&' || value == '#';
        internal static string Sha256(string value)
        {
            return SdkReflection.Sha256Hex(value);
        }
        private static int Count(string value, string needle) => (value ?? string.Empty).Split(new[] { needle }, StringSplitOptions.None).Length - 1;
        private static int CountLoneLf(string value) => (value ?? string.Empty).Replace("\r\n", string.Empty).Count(c => c == '\n');
        private static int CountLoneCr(string value) => (value ?? string.Empty).Replace("\r\n", string.Empty).Count(c => c == '\r');
        internal static bool IsCodeOrTextPart(string partName) => string.IsNullOrWhiteSpace(partName)
            || string.Equals(partName, "Source", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Rules", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Events", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Conditions", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Documentation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "DataSelector", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "WSDL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Styles", StringComparison.OrdinalIgnoreCase);
    }
}
