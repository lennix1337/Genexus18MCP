using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The text the version token of a visual part (<c>WebForm</c>, <c>Layout</c>) fingerprints: the XML as the read
    /// returns it, except that every GUID that is the value of an <c>id</c> attribute, and every attribute that holds
    /// one of those GUIDs as its whole value (a reference to the element), is replaced by <c>gx-guid-N</c>, N being the
    /// order in which that GUID first appears as an <c>id</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A form that was never saved (a WebPanel just created) has no stored layout: the SDK builds the default one every
    /// time the object loads, and gives the <c>layout</c>, <c>table</c>, <c>row</c> and <c>cell</c> elements new GUID ids
    /// each time. Two reads of the same, unchanged object therefore differ in those values and in nothing else. A token
    /// that fingerprinted them would be stale against itself: <c>genexus_read</c> holds one instance of the object and
    /// the check behind <c>baseVersion</c> reads a fresh one, so an edit made with the token just read answered
    /// <c>StaleObject</c>. A form that was saved keeps its ids, so for it the numbering below is only another way to
    /// write the same text.
    /// </para>
    /// <para>
    /// What is kept is which ids are the same, which are different, and what refers to which:
    /// <c>&lt;a id=G1/&gt;&lt;b id=G2/&gt;</c> and <c>&lt;a id=G1/&gt;&lt;b id=G1/&gt;</c> do not canonicalise to one
    /// text, and neither do <c>&lt;a id=G1/&gt;&lt;b id=G2/&gt;&lt;ref target=G1/&gt;</c> and
    /// <c>&lt;a id=G2/&gt;&lt;b id=G1/&gt;&lt;ref target=G1/&gt;</c>, because the reference now points at another
    /// element (the repository keeps a referenced id significant everywhere else). A GUID that no <c>id</c> holds is
    /// not numbered, so a reference to an id that is not there stays as it is. Nothing else is touched: a GUID that is
    /// only part of a value (a theme class is <c>&lt;guid&gt;-1</c>), an <c>id</c> that is not a GUID
    /// (<c>&lt;Form id="1"&gt;</c>), a caption, a property and the layout of the text all stay, so a change an author
    /// makes still changes the token. A text without a GUID id comes back as it came, so the token of every other part
    /// and of every form that has none is the one it always was.
    /// </para>
    /// <para>
    /// It works on the text on purpose. It does not parse the XML: the text can be malformed or carry a DOCTYPE and
    /// the answer must not depend on either, and two passes over a text that is already in memory are all it takes.
    /// The one difference from a parse is that an <c>id="..."</c> written inside a comment, a CDATA section or an
    /// element's text is read as the attribute it looks like. Two further limits, neither of which the SDK reaches: an
    /// <c>id</c> whose text is literally <c>gx-guid-1</c> reads as the GUID numbered 1, and an <c>id</c> of 32
    /// hexadecimal digits (a GUID without hyphens, which <c>Guid.ToString("N")</c> writes) is taken for a GUID.
    /// </para>
    /// </remarks>
    internal static class VisualTokenCanonicalizer
    {
        internal const string MarkerPrefix = "gx-guid-";

        private const string Hex = "[0-9a-f]";
        private const string DashedGuid = Hex + "{8}(?:-" + Hex + "{4}){3}-" + Hex + "{12}";

        // The four spellings Guid.ToString produces: with braces, with parentheses, hyphenated, and 32 digits.
        private const string GuidValue =
            @"\{" + DashedGuid + @"\}"
            + @"|\(" + DashedGuid + @"\)"
            + "|" + DashedGuid
            + "|" + Hex + "{32}";

        // An attribute (any name; the white space before it keeps a tag and text out) whose whole value is a GUID. The value
        // is matched up to its own closing quote, which is left where it is. The name tells an id, where the numbering is
        // born, from another attribute: a reference to an id, or a GUID of its own.
        private static readonly Regex GuidAttribute = new Regex(
            @"(?<head>\s(?<name>[^\s=<>/""']+)\s*=\s*(?<quote>[""']))(?<guid>" + GuidValue + @")(?=\k<quote>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static string Canonicalize(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return xml;

            // First pass: number the GUIDs that are the value of an id attribute, in the order they first appear.
            Dictionary<string, int> numbers = null;
            for (Match match = GuidAttribute.Match(xml); match.Success; match = match.NextMatch())
            {
                if (!string.Equals(match.Groups["name"].Value, "id", StringComparison.OrdinalIgnoreCase)) continue;

                if (numbers == null) numbers = new Dictionary<string, int>(StringComparer.Ordinal);
                string identity = Identity(match.Groups["guid"].Value);
                if (!numbers.ContainsKey(identity)) numbers.Add(identity, numbers.Count + 1);
            }

            // No GUID id: the text is returned as it is.
            if (numbers == null) return xml;

            // Second pass: write the number wherever one of those GUIDs is the whole value of an attribute (the id itself,
            // or a reference to it). A GUID that is not an id anywhere is left alone.
            return GuidAttribute.Replace(xml, match =>
            {
                int number;
                if (!numbers.TryGetValue(Identity(match.Groups["guid"].Value), out number)) return match.Value;
                return match.Groups["head"].Value + MarkerPrefix + number.ToString(CultureInfo.InvariantCulture);
            });
        }

        // The same GUID in two spellings (case, braces, parentheses, hyphens) is one id.
        private static string Identity(string guid)
        {
            var digits = new StringBuilder(32);
            foreach (char c in guid)
            {
                if (c == '-' || c == '{' || c == '}' || c == '(' || c == ')') continue;
                digits.Append(char.ToLowerInvariant(c));
            }
            return digits.ToString();
        }
    }
}
