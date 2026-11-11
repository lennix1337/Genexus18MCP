using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// What a text says about its own layout, measured on the exact string and never normalised: how many lines it has,
    /// which line terminator it uses and whether it ends with one. A caller that hashes or compares the text itself
    /// (the export_part receipt, against the version token's fingerprint) needs these to know which bytes were hashed.
    /// </summary>
    internal readonly struct TextLayoutFacts
    {
        internal TextLayoutFacts(int lines, string lineEnding, bool endsWithNewline)
        {
            Lines = lines;
            LineEnding = lineEnding;
            EndsWithNewline = endsWithNewline;
        }

        /// <summary>Line count with the same meaning as <c>totalLines</c> in a read: a final terminator does not start a new line.</summary>
        internal int Lines { get; }

        /// <summary><c>CRLF</c>, <c>LF</c>, <c>CR</c> when one kind of terminator is used, <c>mixed</c> when more than one is, <c>none</c> when there is no terminator at all.</summary>
        internal string LineEnding { get; }

        internal bool EndsWithNewline { get; }

        internal static TextLayoutFacts Measure(string text)
        {
            if (string.IsNullOrEmpty(text)) return new TextLayoutFacts(0, "none", false);

            int crlf = 0, lf = 0, cr = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                    else cr++;
                }
                else if (c == '\n') lf++;
            }

            int terminators = crlf + lf + cr;
            char last = text[text.Length - 1];
            bool endsWithNewline = last == '\n' || last == '\r';
            int lines = endsWithNewline ? terminators : terminators + 1;

            string ending;
            int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
            if (kinds == 0) ending = "none";
            else if (kinds > 1) ending = "mixed";
            else ending = crlf > 0 ? "CRLF" : lf > 0 ? "LF" : "CR";

            return new TextLayoutFacts(lines, ending, endsWithNewline);
        }
    }
}
