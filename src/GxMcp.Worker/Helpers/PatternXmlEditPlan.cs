using System;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    // Pure preflight for raw XML edits. SDK-owned order/defaults are not derivable
    // from document order. Keep the caller's payload intact; never repair metadata.
    public sealed class PatternXmlEditPlan
    {
        public string Xml { get; private set; }
        public string ErrorCode { get; private set; }
        public string Error { get; private set; }
        public JArray Changes { get; } = new JArray();
        public bool IsNoChange => ErrorCode == null && Changes.Count == 0;

        public static PatternXmlEditPlan Create(string currentXml, string requestedXml)
        {
            var plan = new PatternXmlEditPlan { Xml = requestedXml };
            XDocument current, requested;
            try { requested = XDocument.Parse(requestedXml, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { return plan.Reject("PatternInvalidXml", ex.Message); }
            try { current = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { return plan.Reject("PatternReadFailed", "Cannot compare current pattern: " + ex.Message); }
            var oldOutside = current.Nodes().Where(n => !(n is XElement)).ToArray();
            var newOutside = requested.Nodes().Where(n => !(n is XElement)).ToArray();
            if (current.DocumentType != null || requested.DocumentType != null
                || current.Declaration?.ToString() != requested.Declaration?.ToString()
                || oldOutside.Length != newOutside.Length
                || oldOutside.Where((n, i) => !XNode.DeepEquals(n, newOutside[i])).Any())
                return plan.Reject("PatternStructureChangeUnsupported", "Document declarations or nodes outside the pattern root changed (DTDs are unsupported).");
            plan.Compare(current.Root, requested.Root, "/");
            return plan;
        }

        private PatternXmlEditPlan Reject(string code, string detail)
        {
            ErrorCode = code;
            Error = detail;
            Changes.Clear();
            return this;
        }

        private void Compare(XElement before, XElement after, string path)
        {
            if (ErrorCode != null) return;
            path += before.Name + "/";
            if (before.Name != after.Name)
            {
                Reject("PatternStructureChangeUnsupported", "Element identity changed at " + path);
                return;
            }
            foreach (var name in before.Attributes().Select(a => a.Name).Union(after.Attributes().Select(a => a.Name)))
            {
                var left = before.Attribute(name);
                var right = after.Attribute(name);
                if ((string)left == (string)right) continue;
                if (IsProtected(name.LocalName) || left?.IsNamespaceDeclaration == true || right?.IsNamespaceDeclaration == true)
                {
                    Reject("PatternMetadataChangeUnsupported", "SDK-owned metadata or identity changed at " + path + "@" + name);
                    return;
                }
                Changes.Add(new JObject { ["path"] = path + "@" + name, ["before"] = (string)left, ["after"] = (string)right });
            }
            var oldNodes = before.Nodes().Where(Significant).ToArray();
            var newNodes = after.Nodes().Where(Significant).ToArray();
            if (oldNodes.Length != newNodes.Length)
            {
                if (TryAllowWebComponentInsertion(before, after, oldNodes, newNodes, path))
                    return;
                Reject("PatternStructureChangeUnsupported", "Child structure changed at " + path);
                return;
            }
            for (int i = 0; i < oldNodes.Length; i++)
            {
                if (oldNodes[i] is XElement oldElement && newNodes[i] is XElement newElement)
                {
                    if (IsMetadata(oldElement.Name.LocalName) && !XNode.DeepEquals(oldElement, newElement))
                        Reject("PatternMetadataChangeUnsupported", "SDK-owned metadata changed at " + path + oldElement.Name);
                    else
                        Compare(oldElement, newElement, path + "[" + i + "]/");
                }
                else if (!XNode.DeepEquals(oldNodes[i], newNodes[i]))
                    Reject("PatternStructureChangeUnsupported", "Text or node structure changed at " + path);
                if (ErrorCode != null) return;
            }
        }

        // A WebComponent is a named, non-ordered child in WorkWithPlus layout
        // containers. Allow only the narrow structural operation needed to add
        // one such component to an existing container: no removals, moves,
        // replacements, metadata changes, or childrenOrderedList edits. The
        // normal PatternInstance SDK deserializer/save path still performs the
        // actual persistence and the later WWP projection verifies the result.
        private bool TryAllowWebComponentInsertion(XElement before, XElement after,
            XNode[] oldNodes, XNode[] newNodes, string path)
        {
            if (!string.Equals(before.Name.LocalName, "table", StringComparison.OrdinalIgnoreCase)
                || newNodes.Length != oldNodes.Length + 1)
                return false;

            int insertedIndex = -1;
            for (int i = 0; i < newNodes.Length; i++)
            {
                if (!(newNodes[i] is XElement element)
                    || !string.Equals(element.Name.LocalName, "webComponent", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (insertedIndex >= 0) return false;
                insertedIndex = i;
            }
            if (insertedIndex < 0) return false;

            var inserted = (XElement)newNodes[insertedIndex];
            if (inserted.HasElements
                || inserted.Attributes().Any(a => !string.Equals(a.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(a.Name.LocalName, "gxobject", StringComparison.OrdinalIgnoreCase)))
                return false;

            string name = (string)inserted.Attribute("name");
            string gxobject = (string)inserted.Attribute("gxobject");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(gxobject))
                return false;

            int oldIndex = 0;
            for (int newIndex = 0; newIndex < newNodes.Length; newIndex++)
            {
                if (newIndex == insertedIndex) continue;
                if (oldIndex >= oldNodes.Length || !XNode.DeepEquals(oldNodes[oldIndex], newNodes[newIndex]))
                    return false;
                oldIndex++;
            }
            if (oldIndex != oldNodes.Length) return false;

            Changes.Add(new JObject
            {
                ["path"] = path + "webComponent[@name='" + name + "']",
                ["operation"] = "Insert",
                ["after"] = insertedIndex == 0 ? null : "existing child at index " + (insertedIndex - 1),
                ["name"] = name,
                ["gxobject"] = gxobject
            });
            return true;
        }

        private static bool Significant(XNode node) => !(node is XText text)
            || node is XCData || !string.IsNullOrWhiteSpace(text.Value)
            || !node.Parent.Elements().Any()
            || node.Parent.Nodes().OfType<XText>().Any(t => t is XCData || !string.IsNullOrWhiteSpace(t.Value))
            || node.Parent.AncestorsAndSelf().Any(e => (string)e.Attribute(XNamespace.Xml + "space") == "preserve");

        private static bool IsMetadata(string name) => name.StartsWith("default", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("template", StringComparison.OrdinalIgnoreCase)
            || name.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase);

        private static bool IsProtected(string name)
        {
            return IsMetadata(name)
                || name.Equals("name", StringComparison.OrdinalIgnoreCase)
                || name.Equals("controlName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("blockName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("attribute", StringComparison.OrdinalIgnoreCase)
                || name.Equals("id", StringComparison.OrdinalIgnoreCase)
                || name.Equals("type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("identifier", StringComparison.OrdinalIgnoreCase)
                || name.Equals("patternId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("guid", StringComparison.OrdinalIgnoreCase)
                || name.Equals("key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("entityKey", StringComparison.OrdinalIgnoreCase);
        }
    }
}
