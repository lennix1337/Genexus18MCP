using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Which WorkWithPlus layout elements a raw XML edit may add to or remove from a
    /// container, and which attributes each one may carry.
    ///
    /// <para>
    /// Issue #349. Attribute-level edits were already permitted and auto-projected; only
    /// structural edits were refused. This table is what makes "add a textBlock" a
    /// permitted structural edit rather than an open-ended one.
    /// </para>
    ///
    /// <para>
    /// <b>Why an explicit allow-list rather than a general rule.</b> A rejected edit costs
    /// the caller one retry with a corrected payload. A permitted edit that the SDK then
    /// misreads costs a layout the author has to unpick by hand, and a raw XML write has
    /// no schema validation to catch it. Nothing here is verifiable against a real
    /// PatternInstance without the WorkWithPlus package installed, so the table is drawn
    /// from the element set the issue names and from the attribute names the reconciler
    /// already understands, and everything else is refused with the offending attribute
    /// named in the message.
    /// </para>
    ///
    /// <para>
    /// <b>Identity per kind.</b> WorkWithPlus addresses an element inside a container by
    /// an identifier whose attribute differs per kind - <c>controlName</c> for a
    /// textBlock, <c>name</c> for most others, <c>blockName</c> for a block. These are the
    /// same identifiers <c>PatternChildOrderReconciler</c> reads when it rebuilds
    /// <c>childrenOrderedList</c>, so accepting an element under one of them is accepting
    /// one the ordering can represent.
    /// </para>
    /// </summary>
    internal static class StructuralLayoutRules
    {
        private sealed class Kind
        {
            internal Kind(string identityAttribute, params string[] attributes)
            {
                IdentityAttribute = identityAttribute;
                Allowed = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase)
                {
                    identityAttribute
                };
            }

            internal string IdentityAttribute { get; }
            internal HashSet<string> Allowed { get; }
        }

        private static readonly Dictionary<string, Kind> Kinds =
            new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase)
            {
                // A label on the form. `controlName` is the identifier the reconciler
                // reads for textBlock.
                { "textBlock", new Kind("controlName",
                    "controlName", "caption", "title", "visible", "styleClass", "themeClass", "class", "tag", "position") },

                // A form variable declaration.
                { "variable", new Kind("name",
                    "name", "type", "length", "decimals", "caption", "description",
                    "domain", "initialValue", "labelPosition", "visible", "styleClass", "themeClass", "class", "controlName") },

                // A data-element reference on the form.
                { "attribute", new Kind("attribute",
                    "attribute", "caption", "labelPosition", "visible", "styleClass", "themeClass", "cellThemeClass", "class",
                    "tag", "contextSensitive", "description", "mergeWithPrevious", "readOnly", "isPassword", "inviteMessage", "tooltip", "controlName") },

                // A form button or grid row action.
                { "userAction", new Kind("name",
                    "name", "caption", "description", "visible", "styleClass", "themeClass", "class", "callObject",
                    "popup", "parameters", "gxobject", "disabled", "confirm", "confirmText", "order", "image", "disabledImage", "controlName") },

                // issue #408: a variable (or attribute) shown as a control - edit, combo box, radio.
                // This is the element PopupLayoutBuilder emits for a combo bound to a variable, with the
                // control type and values in PATTERN_ELEMENT_CUSTOM_PROPERTIES; it is addressed by its
                // `attribute` reference, not by a controlName (the SDK strips controlName).
                { "data", new Kind("attribute",
                    "attribute", "labelCaption", "labelPosition", "class", "visible", "styleClass",
                    "tag", "PATTERN_ELEMENT_CUSTOM_PROPERTIES") },

                // NOTE: gridAttribute and gridVariable are deliberately absent.
                //
                // Not an oversight: they are already governed, more narrowly, by
                // TryAllowGridVariableInsertion, which permits a gridVariable insertion
                // only on a <grid> and only behind `allowGridStructure`. Listing them here
                // would have quietly widened that, because the unconditional structural
                // handler is consulted first - so grid column insertion would have started
                // working on any container and without the authoring mode. Their reordering
                // is likewise already handled by TryCompareGridReorder. Adding them back
                // here is the regression this omission prevents.

                // An image / static control on the form.
                { "image", new Kind("name",
                    "name", "gxobject", "caption", "url", "width", "height",
                    "visible", "styleClass") },

                // A user control instance on the form.
                { "userControl", new Kind("name",
                    "name", "gxobject", "caption", "properties", "visible", "styleClass") },

                // An action group, so an existing one can be moved between containers.
                { "actionGroup", new Kind("name",
                    "name", "caption", "visible", "layout", "styleClass") },

                // The pattern's error viewer: a leaf the reconciler already codes (28).
                { "errorViewer", new Kind("name",
                    "name", "visible", "styleClass") },
            };

        internal static bool IsPermittedKind(string elementName)
        {
            return !string.IsNullOrEmpty(elementName) && Kinds.ContainsKey(elementName);
        }

        /// <summary>
        /// Whether a raw PatternInstance edit may remove this element: any permitted leaf,
        /// or a table with no child elements. Issue #869: add_table creates such a table and
        /// move_control can leave one behind, and nothing else could remove it. A table that
        /// still holds children stays refused, because removing it would drop them too.
        /// </summary>
        internal static bool IsRemovable(XElement element)
        {
            if (element == null) return false;
            string kind = element.Name.LocalName;
            if (IsPermittedKind(kind)) return true;
            return string.Equals(kind, "table", System.StringComparison.OrdinalIgnoreCase) && !element.HasElements;
        }

        internal static string RequiredIdentityAttribute(string elementName)
        {
            return Kinds.TryGetValue(elementName ?? "", out var kind)
                ? kind.IdentityAttribute
                : "an identity attribute";
        }

        /// <summary>
        /// The identity a container addresses this element by, or null when it has none -
        /// which is itself a rejection reason, because an element the ordering cannot name
        /// cannot participate in a childrenOrderedList.
        /// </summary>
        internal static string Identity(string elementName, XElement element)
        {
            if (element == null || !Kinds.TryGetValue(elementName ?? "", out var kind)) return null;
            var value = (string)element.Attribute(kind.IdentityAttribute);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>
        /// A best-effort identifier for reporting, falling back to a generic name
        /// attribute.
        ///
        /// <para>
        /// For <em>removal</em> only. An element already in the KB may predate the
        /// canonical identity attribute, and refusing to let it be removed on that basis
        /// would strand it. Insertion still requires the real identity, because an element
        /// the ordering cannot name is one the SDK cannot address.
        /// </para>
        /// </summary>
        internal static string ReportIdentity(string elementName, XElement element)
        {
            var identity = Identity(elementName, element);
            if (identity != null) return identity;

            if (element == null) return null;
            var fallback = (string)element.Attribute("name");
            return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
        }

        /// <summary>The first attribute on the element that its kind does not permit.</summary>
        internal static string FirstDisallowedAttribute(string elementName, XElement element)
        {
            return FirstDisallowedAttribute(elementName, element, null);
        }

        /// <summary>
        /// The first attribute on the element that neither its kind nor the pattern specification
        /// permits. <paramref name="declared"/> is what the installed specification declares for
        /// the element type (null when it cannot be read); it widens the static list because the
        /// specification, not this table, says which properties a button or image of the pattern
        /// carries. SDK-owned metadata (<c>default*</c>, <c>template*</c>, <c>childrenOrderedList</c>)
        /// stays refused whatever the specification declares.
        /// </summary>
        internal static string FirstDisallowedAttribute(string elementName, XElement element, IReadOnlyCollection<string> declared)
        {
            if (element == null || !Kinds.TryGetValue(elementName ?? "", out var kind)) return null;
            foreach (var attribute in element.Attributes())
            {
                // A namespace declaration is structural, not layout, and never rejected.
                if (attribute.IsNamespaceDeclaration) continue;
                string name = attribute.Name.LocalName;
                if (kind.Allowed.Contains(name)) continue;
                if (declared != null && !IsSdkOwnedAttribute(name) && declared.Contains(name)) continue;
                return name;
            }
            return null;
        }

        internal static bool IsSdkOwnedAttribute(string attributeName)
        {
            return attributeName.StartsWith("default", StringComparison.OrdinalIgnoreCase)
                || attributeName.StartsWith("template", StringComparison.OrdinalIgnoreCase)
                || attributeName.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase);
        }

        internal static IEnumerable<string> AllowedAttributes(string elementName)
        {
            if (!Kinds.TryGetValue(elementName ?? "", out var kind)) return Enumerable.Empty<string>();
            return kind.Allowed.OrderBy(a => a, StringComparer.OrdinalIgnoreCase);
        }
    }
}
