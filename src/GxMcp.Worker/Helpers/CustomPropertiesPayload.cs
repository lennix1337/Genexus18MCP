using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The <c>PATTERN_ELEMENT_CUSTOM_PROPERTIES</c> attribute of a layout-form element:
    /// <c>&lt;Properties&gt;&lt;Property&gt;&lt;Name&gt;..&lt;/Name&gt;&lt;Value&gt;..&lt;/Value&gt;&lt;/Property&gt;..&lt;/Properties&gt;</c>.
    /// Each value is the SDK's own storage form of the property (for a parameter list, a
    /// token list), so the payload is read and compared, never composed by hand.
    /// </summary>
    internal sealed class CustomPropertiesPayload
    {
        internal const string AttributeName = "PATTERN_ELEMENT_CUSTOM_PROPERTIES";

        public List<KeyValuePair<string, string>> Properties { get; } = new List<KeyValuePair<string, string>>();

        internal static bool TryParse(string attributeValue, out CustomPropertiesPayload payload)
        {
            payload = new CustomPropertiesPayload();
            if (string.IsNullOrWhiteSpace(attributeValue)) return true;
            try
            {
                var root = XDocument.Parse(attributeValue).Root;
                if (root == null || !string.Equals(root.Name.LocalName, "Properties", StringComparison.Ordinal))
                {
                    payload = null;
                    return false;
                }
                foreach (var property in root.Elements("Property"))
                {
                    string name = property.Element("Name")?.Value;
                    if (string.IsNullOrEmpty(name)) continue;
                    payload.Properties.Add(new KeyValuePair<string, string>(name, property.Element("Value")?.Value ?? string.Empty));
                }
                return true;
            }
            catch (XmlException)
            {
                payload = null;
                return false;
            }
        }

        internal bool TryGet(string name, out string value)
        {
            foreach (var property in Properties)
            {
                if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        /// <summary>The attribute value with <paramref name="name"/> set to <paramref name="value"/>,
        /// in place when present and appended otherwise. Every other property is carried as is.</summary>
        internal string ToAttributeValue(string name, string value)
        {
            var root = new XElement("Properties");
            bool replaced = false;
            foreach (var property in Properties)
            {
                string propertyValue = property.Value;
                if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    propertyValue = value;
                    replaced = true;
                }
                root.Add(new XElement("Property", new XElement("Name", property.Key), new XElement("Value", propertyValue)));
            }
            if (!replaced)
                root.Add(new XElement("Property", new XElement("Name", name), new XElement("Value", value)));
            return root.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// True when both payloads carry the same properties with the same values, in any order.
        /// A value that is itself XML (a token list) is compared as XML, so whitespace and
        /// attribute order do not read as a change. Unparseable text compares as text.
        /// </summary>
        internal static bool SemanticallyEqual(string left, string right) => SemanticallyEqual(left, right, variableMayBeResolved: false);

        /// <summary>
        /// As <see cref="SemanticallyEqual(string, string)"/>, and with <paramref name="variableMayBeResolved"/> a value that names a
        /// variable ("&amp;name") in one payload equals the key the SDK resolved it to ("var:5") in the other: an XPZ names the
        /// variables of a custom property and the SDK stores their keys when it imports the form.
        /// </summary>
        internal static bool SemanticallyEqual(string left, string right, bool variableMayBeResolved)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) return true;
            if (!TryParse(left, out var l) || !TryParse(right, out var r) || l == null || r == null) return false;
            if (l.Properties.Count != r.Properties.Count) return false;

            var rightByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in r.Properties) rightByName[property.Key] = property.Value;
            if (rightByName.Count != r.Properties.Count) return false;
            foreach (var property in l.Properties)
            {
                if (!rightByName.TryGetValue(property.Key, out string other)) return false;
                if (!ValuesEqual(property.Value, other, variableMayBeResolved)) return false;
            }
            return true;
        }

        private static bool ValuesEqual(string a, string b, bool variableMayBeResolved)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (string.Equals(CanonicalXmlOrText(a), CanonicalXmlOrText(b), StringComparison.Ordinal)) return true;
            if (variableMayBeResolved && XmlEquivalence.IsVariableNamedVsKeyed(CanonicalXmlOrText(a), CanonicalXmlOrText(b))) return true;
            // Issue #711: an expression property (ControlWhere) reads back as a TokenDataList.
            return ControlExpressionText.SameTokenExpression(a, b);
        }

        /// <summary>The value's XML without formatting, or its trimmed text when it is not XML.</summary>
        internal static string CanonicalXmlOrText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Trim();
            if (!trimmed.StartsWith("<", StringComparison.Ordinal)) return trimmed;
            try { return XDocument.Parse(trimmed).Root.ToString(SaveOptions.DisableFormatting); }
            catch (XmlException) { return trimmed; }
        }
    }
}
