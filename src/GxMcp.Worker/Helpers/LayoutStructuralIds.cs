using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    internal static class LayoutStructuralIds
    {
        private const string StructuralIdMarker = "native-structural-guid";

        internal static void Normalize(XDocument document) => Normalize(document, document.Descendants());

        internal static void NormalizeWebForms(XDocument left, XDocument right)
        {
            // Use one marker absent from both inputs so a literal non-GUID id
            // can never collide with a normalized structural GUID.
            string leftXml = left.ToString(SaveOptions.DisableFormatting);
            string rightXml = right.ToString(SaveOptions.DisableFormatting);
            string marker = StructuralIdMarker;
            while (leftXml.IndexOf(marker, StringComparison.Ordinal) >= 0
                || rightXml.IndexOf(marker, StringComparison.Ordinal) >= 0)
                marker += "_";
            NormalizeWebForm(left, marker);
            NormalizeWebForm(right, marker);
            NormalizePanelTypeDefaults(left, right);
        }

        // The SDK materializes the default PanelType of a WebForm detail when a parent is saved, and a
        // fresh read can omit it. Only that default is tolerated, and only when the other side omits the
        // attribute: any other value, or a value present on both sides, is still compared.
        private const string DefaultPanelType = "Web";

        private static void NormalizePanelTypeDefaults(XDocument left, XDocument right)
        {
            var leftDetails = ProjectedDetails(left);
            var rightDetails = ProjectedDetails(right);
            if (leftDetails.Count != rightDetails.Count) return;
            for (int i = 0; i < leftDetails.Count; i++)
            {
                XAttribute leftPanel = leftDetails[i].Attribute("PanelType");
                XAttribute rightPanel = rightDetails[i].Attribute("PanelType");
                if (leftPanel == null && rightPanel?.Value == DefaultPanelType) rightPanel.Remove();
                else if (rightPanel == null && leftPanel?.Value == DefaultPanelType) leftPanel.Remove();
            }
        }

        private static List<XElement> ProjectedDetails(XDocument document)
        {
            if (!Is(document.Root, "GxMultiForm")) return new List<XElement>();
            return document.Root.Elements().Where(e => Is(e, "Form"))
                .SelectMany(form => form.Elements().Where(detail => Is(detail, "detail")))
                .ToList();
        }

        private static void NormalizeWebForm(XDocument document, string marker)
        {
            if (!Is(document.Root, "GxMultiForm")) return;
            var elements = document.Root.Elements().Where(e => Is(e, "Form")
                    && string.Equals((string)e.Attribute("type"), "layout", StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => e.Elements().Where(detail => Is(detail, "detail")))
                .SelectMany(e => e.Elements().Where(layout => Is(layout, "layout")))
                .SelectMany(e => e.DescendantsAndSelf());
            Normalize(document, elements, preserveReferencedIds: true, marker: marker, includeNamedControls: true);
        }

        private static void Normalize(XDocument document, IEnumerable<XElement> elements,
            bool preserveReferencedIds = false, string marker = StructuralIdMarker, bool includeNamedControls = false)
        {
            // Only projected WebForm readback adds reference protection. The DDL
            // comparison keeps its existing structural-id normalization unchanged.
            // Snapshot values before replacing any id so duplicate ids stay visible.
            var attributes = preserveReferencedIds ? document.Descendants().Attributes()
                .Select(a => new { Attribute = a, Value = a.Value }).ToArray() : null;
            var text = preserveReferencedIds ? document.DescendantNodes().OfType<XText>().Select(t => t.Value).ToArray() : null;
            foreach (var element in elements.Where(e =>
                (Is(e, "layout") || Is(e, "table") || Is(e, "row") || Is(e, "cell")
                    || (includeNamedControls && (IsTabContainer(e) || IsGridControl(e))))
                && e.AncestorsAndSelf().Any(a => Is(a, "layout"))))
            {
                var id = element.Attribute("id");
                if (id == null || !Guid.TryParse(id.Value, out _)) continue;
                bool referenced = preserveReferencedIds && (attributes.Any(a => a.Attribute != id
                        && a.Value.IndexOf(id.Value, StringComparison.OrdinalIgnoreCase) >= 0)
                    || text.Any(value => value.IndexOf(id.Value, StringComparison.OrdinalIgnoreCase) >= 0));
                if (!referenced) id.Value = marker;
            }
        }

        private static bool IsTabContainer(XElement element)
        {
            // SDK LayoutTab/LayoutTabItem have hidden unique-id keys and required
            // control names. Only a tab's direct item is a tab page, not a business item.
            if (Is(element, "tab"))
                return !string.IsNullOrWhiteSpace((string)element.Attribute("controlName"));
            return Is(element, "item") && Is(element.Parent, "tab")
                && !string.IsNullOrWhiteSpace((string)element.Parent.Attribute("controlName"))
                && !string.IsNullOrWhiteSpace((string)element.Attribute("itemControlName"));
        }

        // Issue #948: the WorkWithPlus engine emits new grid and column ids on every
        // projection. A grid is identified by its control name and a column by the
        // attribute or variable it shows, so only those keep the id out of the comparison.
        private static bool IsGridControl(XElement element)
        {
            if (Is(element, "simplegrid") || Is(element, "grid"))
                return !string.IsNullOrWhiteSpace((string)element.Attribute("controlName"));
            return Is(element, "item") && Is(element.Parent, "simplegrid")
                && !string.IsNullOrWhiteSpace((string)element.Parent.Attribute("controlName"))
                && !string.IsNullOrWhiteSpace((string)element.Attribute("attribute"));
        }

        private static bool Is(XElement element, string name)
            => element != null && element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    }
}
