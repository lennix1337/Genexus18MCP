using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The representation noise that the SDK adds to two parts on every read or restore: the layout ids of a WebForm change
    /// each time it is read, and a Documentation part gains empty default entries after a restore. Only these two parts are
    /// normalized, and only for the property rollback comparison. The move, SDT and variable comparisons keep the bytes.
    /// </summary>
    internal static class PropertyPartNormalizer
    {
        private static readonly Regex GuidValue = new Regex(
            @"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$",
            RegexOptions.CultureInvariant);

        internal static bool IsNormalized(string partName)
            => string.Equals(partName, "WebForm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Documentation", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The text to compare for a part. Parts other than the two normalized ones, and text that is not well-formed XML,
        /// come back unchanged. Otherwise the layout ids (an <c>id</c> attribute or a GUID value) go, and so do the empty
        /// elements left by a default entry.
        /// </summary>
        internal static string Normalize(string partName, string text)
        {
            if (!IsNormalized(partName) || string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            XDocument document;
            try
            {
                document = XDocument.Parse(text);
            }
            catch (Exception)
            {
                return text;
            }

            foreach (XElement element in document.Descendants().ToList())
            {
                foreach (XAttribute attribute in element.Attributes().ToList())
                {
                    if (string.Equals(attribute.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase)
                        || GuidValue.IsMatch(attribute.Value))
                        attribute.Remove();
                }
            }

            bool removed;
            do
            {
                removed = false;
                foreach (XElement element in document.Descendants().ToList())
                {
                    if (element.Parent == null || element.HasElements || element.HasAttributes) continue;
                    if (!string.IsNullOrWhiteSpace(element.Value)) continue;
                    element.Remove();
                    removed = true;
                }
            }
            while (removed);

            return document.ToString(SaveOptions.DisableFormatting);
        }
    }
}
