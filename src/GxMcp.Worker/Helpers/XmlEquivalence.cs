using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    public sealed class XmlEquivalenceDiff
    {
        public string Path { get; set; }
        public string Summary { get; set; }
        public string[] LeftAttributes { get; set; }
        public string[] RightAttributes { get; set; }
        // Rejected by SDK = present in input (right) but absent in persisted (left).
        public string[] RejectedAttributes { get; set; }
        // Added by SDK = present in persisted (left) but absent in input (right).
        public string[] AddedAttributes { get; set; }
        public string ElementName { get; set; }
    }

    public static class XmlEquivalence
    {
        public static bool AreEquivalent(string a, string b, out string diffSummary)
        {
            return AreEquivalent(a, b, out diffSummary, out _);
        }

        public static bool AreEquivalent(string a, string b, out string diffSummary, out XmlEquivalenceDiff structuredDiff)
        {
            diffSummary = null;
            structuredDiff = null;
            if (ReferenceEquals(a, b)) return true;
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                diffSummary = "One side empty.";
                structuredDiff = new XmlEquivalenceDiff { Summary = diffSummary };
                return false;
            }

            XDocument da, db;
            try { da = XDocument.Parse(a, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { diffSummary = "Left parse error: " + ex.Message; structuredDiff = new XmlEquivalenceDiff { Summary = diffSummary }; return false; }
            try { db = XDocument.Parse(b, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { diffSummary = "Right parse error: " + ex.Message; structuredDiff = new XmlEquivalenceDiff { Summary = diffSummary }; return false; }

            var ok = ElementsEqual(da.Root, db.Root, "/", out diffSummary, out structuredDiff);
            // WorkWithPlus materializes an empty Parameters node for a
            // WebComponent control even when the author omitted it. This is a
            // canonical SDK projection, not a rejected authored child. Accept
            // only that exact normalization; substantive children and all
            // other structure remain strict.
            if (!ok && WwpEmptyWebComponentParametersEquivalent(da.Root, db.Root))
            {
                diffSummary = null;
                structuredDiff = null;
                return true;
            }
            // Report-layout projection: the persisted <Report> is a SUPERSET of the request
            // (the projection re-emits SDK defaults like BorderStyle/ForeColor that the
            // caller never sent). Only accept requested⊆persisted — never the reverse, or
            // an empty persisted block would "verify" a request full of new controls.
            if (!ok && ReportRootSubsetEquivalent(db.Root, da.Root))
            {
                diffSummary = null;
                structuredDiff = null;
                return true;
            }
            if (!ok && structuredDiff == null)
                structuredDiff = new XmlEquivalenceDiff { Summary = diffSummary };
            return ok;
        }

        private static bool WwpEmptyWebComponentParametersEquivalent(XElement persisted, XElement requested)
        {
            if (persisted == null || requested == null) return false;
            var persistedCopy = new XElement(persisted);
            var requestedCopy = new XElement(requested);
            bool hasWebComponent = persistedCopy.Descendants()
                .Concat(requestedCopy.Descendants())
                .Any(e => e.Name.LocalName.Equals("webComponent", StringComparison.OrdinalIgnoreCase));
            if (!hasWebComponent) return false;

            bool removed = RemoveImplicitParameters(persistedCopy) | RemoveImplicitParameters(requestedCopy);
            if (!removed) return false;

            return ElementsEqual(persistedCopy, requestedCopy, "/", out _, out _);
        }

        private static bool RemoveImplicitParameters(XElement root)
        {
            bool removed = false;
            foreach (var component in root.Descendants()
                .Where(e => e.Name.LocalName.Equals("webComponent", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var parameters in component.Elements()
                    .Where(e => e.Name.LocalName.Equals("parameters", StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    if (!parameters.HasAttributes && !parameters.HasElements
                        && !parameters.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                    {
                        parameters.Remove();
                        removed = true;
                    }
                }
            }
            return removed;
        }

        /// <summary>
        /// Subset equivalence for &lt;Report&gt; roots: every attribute requested on an
        /// element must exist with the same value in the persisted element (matched by
        /// ControlName for controls, Name for print blocks), but the persisted side may
        /// carry additional SDK-default attributes. Child order may differ.
        /// </summary>
        private static bool ReportRootSubsetEquivalent(XElement requestedRoot, XElement persistedRoot)
        {
            if (requestedRoot == null || persistedRoot == null) return false;
            if (!string.Equals(requestedRoot.Name.LocalName, "Report", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(persistedRoot.Name.LocalName, "Report", StringComparison.OrdinalIgnoreCase))
                return false;

            return ReportElementsSubset(requestedRoot, persistedRoot);
        }

        private static bool ReportElementsSubset(XElement requested, XElement persisted)
        {
            if (requested.Name.LocalName != persisted.Name.LocalName) return false;
            if (requested.Name.LocalName == "Control")
            {
                string reqId = AttrValue(requested, "ControlName") ?? AttrValue(requested, "Name");
                string perId = AttrValue(persisted, "ControlName") ?? AttrValue(persisted, "Name");
                if (!string.Equals(reqId, perId, StringComparison.OrdinalIgnoreCase)) return false;
            }

            foreach (var attr in requested.Attributes())
            {
                if (attr.Name.LocalName == "TypeName") continue; // projection-only hint
                var match = persisted.Attributes().FirstOrDefault(a => a.Name.LocalName == attr.Name.LocalName);
                if (match == null) return false;
                if (!string.Equals(NormalizeNumber(attr.Value), NormalizeNumber(match.Value), StringComparison.Ordinal))
                    return false;
            }

            foreach (var reqChild in requested.Elements())
            {
                if (reqChild.Name.LocalName == "Control")
                {
                    string reqId = AttrValue(reqChild, "ControlName") ?? AttrValue(reqChild, "Name");
                    XElement perChild = null;
                    if (reqId != null)
                    {
                        perChild = persisted.Elements()
                            .FirstOrDefault(c => string.Equals(
                                AttrValue(c, "ControlName") ?? AttrValue(c, "Name"),
                                reqId, StringComparison.OrdinalIgnoreCase));
                    }
                    if (perChild == null) return false;
                    if (!ReportElementsSubset(reqChild, perChild)) return false;
                }
                else
                {
                    var candidates = persisted.Elements().Where(c => c.Name.LocalName == reqChild.Name.LocalName).ToList();
                    if (candidates.Count == 0) return false;
                    if (candidates.Count == 1)
                    {
                        if (!ReportElementsSubset(reqChild, candidates[0])) return false;
                    }
                    else
                    {
                        // Ambiguous: require at least one to match.
                        if (!candidates.Any(c => ReportElementsSubset(reqChild, c))) return false;
                    }
                }
            }
            return true;
        }

        private static string AttrValue(XElement e, string localName)
            => e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

        private static string NormalizeNumber(string v)
            => decimal.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : v;

        private static bool ElementsEqual(XElement x, XElement y, string path, out string diff, out XmlEquivalenceDiff structured)
        {
            diff = null;
            structured = null;
            if (x == null && y == null) return true;
            if (x == null || y == null) { diff = "Missing element at " + path; return false; }
            if (x.Name != y.Name) { diff = "Element name differs at " + path + ": '" + x.Name + "' vs '" + y.Name + "'"; return false; }

            var ax = x.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList();
            var ay = y.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList();
            var lNames = ax.Select(a => a.Name.LocalName).ToArray();
            var rNames = ay.Select(a => a.Name.LocalName).ToArray();
            if (ax.Count != ay.Count)
            {
                var lSet = new HashSet<string>(lNames, StringComparer.Ordinal);
                var rSet = new HashSet<string>(rNames, StringComparer.Ordinal);
                var rejected = rNames.Where(n => !lSet.Contains(n)).ToArray(); // requested but missing after save → SDK rejected
                var added = lNames.Where(n => !rSet.Contains(n)).ToArray();    // SDK injected on persist
                diff = "Attribute count differs at " + path + x.Name + " (" + ax.Count + " vs " + ay.Count + ")"
                       + ": left=[" + string.Join(",", lNames) + "] right=[" + string.Join(",", rNames) + "]"
                       + (rejected.Length > 0 ? "; rejectedByPersist=[" + string.Join(",", rejected) + "]" : "")
                       + (added.Length > 0 ? "; addedByPersist=[" + string.Join(",", added) + "]" : "");
                structured = new XmlEquivalenceDiff
                {
                    Path = path + x.Name,
                    ElementName = x.Name.LocalName,
                    Summary = diff,
                    LeftAttributes = lNames,
                    RightAttributes = rNames,
                    RejectedAttributes = rejected,
                    AddedAttributes = added
                };
                return false;
            }
            for (int i = 0; i < ax.Count; i++)
            {
                if (ax[i].Name != ay[i].Name)
                {
                    diff = "Attribute name differs at " + path + x.Name + ": '" + ax[i].Name + "' vs '" + ay[i].Name + "'";
                    structured = new XmlEquivalenceDiff { Path = path + x.Name, ElementName = x.Name.LocalName, Summary = diff, LeftAttributes = lNames, RightAttributes = rNames };
                    return false;
                }
                if (!string.Equals(ax[i].Value, ay[i].Value, StringComparison.Ordinal))
                {
                    // Friction 2026-05-25 (live test) — the SDK auto-resolves
                    // symbolic theme-class names to their GUID-suffixed form on
                    // persist: a requested class="Attribute" becomes
                    // class="d4876646-…-4", "TableGrid" → "…-131", "ErrorViewer"
                    // → "…-59", etc. This is correct SDK behaviour, not a write
                    // rejection — both names point at the same theme class. The
                    // verifier used to flag every popup write because the
                    // probe's fallback symbolic names always got resolved.
                    // Tolerate the diff when the attribute is in the class
                    // family AND one side is symbolic while the other is a
                    // `<guid>-<int>` resolution.
                    if (IsClassFamilyAttribute(ax[i].Name.LocalName) &&
                        IsSymbolicVsGuidClassPair(ax[i].Value, ay[i].Value))
                    {
                        continue;
                    }
                    // Friction 2026-05-25 (live test) — analogous SDK resolution
                    // on variable references: a requested attribute="&Choice"
                    // is persisted as attribute="var:5" because the SDK looks
                    // up the variable by name and stores its numeric ID. Both
                    // point at the same variable. Same shape: tolerate when
                    // the attribute name is in the variable-ref family AND
                    // sides are "&Name" vs "var:N" / "att:N".
                    if (IsVariableRefAttribute(ax[i].Name.LocalName) &&
                        IsAmpersandVsVarRefPair(ax[i].Value, ay[i].Value))
                    {
                        continue;
                    }
                    // Issue #122: Color attribute equivalence (e.g. BackColor="144; 238; 144|" vs "144, 238, 144" or "#90EE90")
                    if (ColorHelper.IsColorAttributeName(ax[i].Name.LocalName) &&
                        ColorHelper.IsColorEquivalent(ax[i].Value, ay[i].Value))
                    {
                        continue;
                    }
                    diff = "Attribute '" + ax[i].Name + "' differs at " + path + x.Name
                           + ": '" + Truncate(ax[i].Value) + "' vs '" + Truncate(ay[i].Value) + "'";
                    structured = new XmlEquivalenceDiff { Path = path + x.Name, ElementName = x.Name.LocalName, Summary = diff, LeftAttributes = lNames, RightAttributes = rNames };
                    return false;
                }
            }

            var cx = SignificantChildren(x).ToList();
            var cy = SignificantChildren(y).ToList();
            if (cx.Count != cy.Count)
            {
                diff = "Child count differs at " + path + x.Name + " (" + cx.Count + " vs " + cy.Count + ")";
                return false;
            }

            for (int i = 0; i < cx.Count; i++)
            {
                var nx = cx[i];
                var ny = cy[i];
                if (nx.NodeType != ny.NodeType)
                {
                    diff = "Node type differs at " + path + x.Name + "[" + i + "]: " + nx.NodeType + " vs " + ny.NodeType;
                    return false;
                }

                if (nx is XElement ex2 && ny is XElement ey2)
                {
                    if (!ElementsEqual(ex2, ey2, path + x.Name + "/", out diff, out structured)) return false;
                }
                else if (nx is XText tx && ny is XText ty)
                {
                    var vx = (tx.Value ?? string.Empty).Trim();
                    var vy = (ty.Value ?? string.Empty).Trim();
                    if (!string.Equals(vx, vy, StringComparison.Ordinal))
                    {
                        diff = "Text differs at " + path + x.Name + "[" + i + "]: '" + Truncate(vx) + "' vs '" + Truncate(vy) + "'";
                        return false;
                    }
                }
            }
            return true;
        }

        private static IEnumerable<XNode> SignificantChildren(XElement e)
        {
            foreach (var n in e.Nodes())
            {
                if (n is XText t)
                {
                    if (string.IsNullOrWhiteSpace(t.Value)) continue;
                    yield return t;
                }
                else if (n is XComment) continue;
                else yield return n;
            }
        }

        private static string Truncate(string s)
        {
            if (s == null) return string.Empty;
            return s.Length <= 80 ? s : s.Substring(0, 80) + "…";
        }

        // Friction 2026-05-25 — set of attributes whose values are theme-class
        // refs the SDK may auto-resolve from symbolic name → GUID form.
        private static readonly System.Collections.Generic.HashSet<string> ClassFamilyAttrs =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "class", "classref", "themeClass", "cellClass", "groupThemeClass",
                "defaultClass", "defaultThemeClass", "defaultCellThemeClass",
                "defaultGroupThemeClass", "columnClass", "defaultColumnClass",
                "ATTThemeClass", "AttBaseClass"
            };

        private static bool IsClassFamilyAttribute(string localName)
            => ClassFamilyAttrs.Contains(localName);

        // GUID-suffixed class form: <8-4-4-4-12>-<int>, e.g. "d4876646-98dd-419b-8c1c-896f83c48368-131".
        private static readonly System.Text.RegularExpressions.Regex GuidSuffixedClassRx =
            new System.Text.RegularExpressions.Regex(
                @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-\d+$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Symbolic class form: a bare theme-class name (letters/digits, no dash).
        private static readonly System.Text.RegularExpressions.Regex SymbolicClassRx =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z][A-Za-z0-9_]*$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// True when one side of a class-attribute diff is a symbolic theme name
        /// (e.g. "Attribute", "TableGrid", "ErrorViewer") and the other is the
        /// SDK-resolved <c>&lt;guid&gt;-&lt;int&gt;</c> form. Both refer to the
        /// same theme class — the verifier should NOT flag this as a write
        /// rejection.
        /// </summary>
        private static bool IsSymbolicVsGuidClassPair(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            bool leftSym = SymbolicClassRx.IsMatch(left);
            bool rightSym = SymbolicClassRx.IsMatch(right);
            bool leftGuid = GuidSuffixedClassRx.IsMatch(left);
            bool rightGuid = GuidSuffixedClassRx.IsMatch(right);
            return (leftSym && rightGuid) || (leftGuid && rightSym);
        }

        // Friction 2026-05-25 — attributes whose values are variable/attribute
        // references the SDK may auto-resolve from ampersand-name → numeric ID.
        private static readonly System.Collections.Generic.HashSet<string> VariableRefAttrs =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "attribute", "AttID", "domain", "defaultDomain"
            };

        private static bool IsVariableRefAttribute(string localName)
            => VariableRefAttrs.Contains(localName);

        // Ampersand-name form: &VarName (variable) — letters/digits/underscore after &.
        private static readonly System.Text.RegularExpressions.Regex AmpVarRefRx =
            new System.Text.RegularExpressions.Regex(@"^&[A-Za-z_][A-Za-z0-9_]*$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // SDK-resolved variable form: var:N or att:N (decimal integer).
        private static readonly System.Text.RegularExpressions.Regex VarNumericRefRx =
            new System.Text.RegularExpressions.Regex(@"^(?:var|att):\d+$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// True when one side of a variable-ref attribute diff is an ampersand-
        /// name reference (e.g. "&Choice") and the other is the SDK-resolved
        /// numeric form (e.g. "var:5", "att:15664"). Both point at the same
        /// variable/attribute — the verifier should not flag this.
        /// </summary>
        private static bool IsAmpersandVsVarRefPair(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            bool leftAmp = AmpVarRefRx.IsMatch(left);
            bool rightAmp = AmpVarRefRx.IsMatch(right);
            bool leftNum = VarNumericRefRx.IsMatch(left);
            bool rightNum = VarNumericRefRx.IsMatch(right);
            return (leftAmp && rightNum) || (leftNum && rightAmp);
        }

        // v2.3.8 Task 4.6 — line-based diff used by patch verification to decide whether
        // a post-save divergence is INSIDE the edited window (real rollback condition)
        // or OUTSIDE it (SDK normalized an untouched line — e.g. DATETIME(10,5)→(8,5);
        // treat as a side-effect, not a verification failure). Despite living on
        // XmlEquivalence, the helper is text-line based — the plan calls it `HunkDiff`
        // because the larger feature classifies on top of XML/source verification.
        //
        // Walks both line arrays, finds the first and last differing lines, and emits
        // one hunk per maximal contiguous run of differences. Each hunk records the
        // 1-based starting line in `before`, plus the joined `Before` / `After` text.
        public sealed class LineHunk
        {
            public int Line;        // 1-based line in 'before' where the hunk starts
            public string Before;   // joined removed lines (may be empty for pure insertions)
            public string After;    // joined inserted lines (may be empty for pure deletions)
            public int BeforeLineCount;
            public int AfterLineCount;
        }

        public static List<LineHunk> HunkDiff(string before, string after)
        {
            var result = new List<LineHunk>();
            string[] b = SplitLines(before);
            string[] a = SplitLines(after);

            // Trim shared prefix.
            int prefix = 0;
            int minLen = Math.Min(b.Length, a.Length);
            while (prefix < minLen && string.Equals(b[prefix], a[prefix], StringComparison.Ordinal))
                prefix++;

            // Trim shared suffix.
            int suffix = 0;
            while (suffix < (minLen - prefix)
                   && string.Equals(b[b.Length - 1 - suffix], a[a.Length - 1 - suffix], StringComparison.Ordinal))
                suffix++;

            int bEnd = b.Length - suffix;       // exclusive
            int aEnd = a.Length - suffix;       // exclusive

            if (prefix >= bEnd && prefix >= aEnd) return result; // identical

            // Inside the differing slab, walk pairwise; group runs of differing lines.
            int i = prefix, j = prefix;
            while (i < bEnd || j < aEnd)
            {
                // Find a run of differences.
                int hunkStartBefore = i;
                var beforeRun = new List<string>();
                var afterRun = new List<string>();

                while (i < bEnd && j < aEnd && !string.Equals(b[i], a[j], StringComparison.Ordinal))
                {
                    beforeRun.Add(b[i]);
                    afterRun.Add(a[j]);
                    i++; j++;
                }
                if (i >= bEnd || j >= aEnd)
                {
                    // Tail asymmetry: leftover lines on one side.
                    while (i < bEnd) { beforeRun.Add(b[i++]); }
                    while (j < aEnd) { afterRun.Add(a[j++]); }
                }

                if (beforeRun.Count > 0 || afterRun.Count > 0)
                {
                    result.Add(new LineHunk
                    {
                        Line = hunkStartBefore + 1, // 1-based
                        Before = string.Join("\n", beforeRun),
                        After = string.Join("\n", afterRun),
                        BeforeLineCount = beforeRun.Count,
                        AfterLineCount = afterRun.Count
                    });
                }

                // Skip any matching middle (rare for simple diffs since we already trimmed
                // prefix/suffix, but defensive in case of multiple disjoint hunks).
                while (i < bEnd && j < aEnd && string.Equals(b[i], a[j], StringComparison.Ordinal))
                { i++; j++; }
            }

            return result;
        }

        // True when a hunk overlaps the inclusive 1-based edit window [windowStart, windowEnd].
        // Pure insertions (BeforeLineCount==0) are treated as touching `Line` only.
        public static bool HunkOverlapsWindow(LineHunk h, int windowStart, int windowEnd)
        {
            if (h == null) return false;
            if (windowStart <= 0 || windowEnd <= 0 || windowEnd < windowStart) return false;
            int hStart = h.Line;
            int hEnd = h.Line + Math.Max(0, h.BeforeLineCount - 1);
            if (h.BeforeLineCount == 0) hEnd = h.Line; // pure insertion
            return hEnd >= windowStart && hStart <= windowEnd;
        }

        private static string[] SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }
    }
}
