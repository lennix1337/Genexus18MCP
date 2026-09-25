using System;
using System.Collections.Generic;
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

        public static PatternXmlEditPlan Create(string currentXml, string requestedXml, bool allowGridStructure = false)
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
            plan.Compare(current.Root, requested.Root, "/", allowGridStructure);
            return plan;
        }

        private PatternXmlEditPlan Reject(string code, string detail)
        {
            ErrorCode = code;
            Error = detail;
            Changes.Clear();
            return this;
        }

        private void Compare(XElement before, XElement after, string path, bool allowGridStructure)
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
                if (allowGridStructure
                    && IsGrid(before)
                    && string.Equals(name.LocalName, "childrenOrderedList", StringComparison.OrdinalIgnoreCase))
                {
                    Changes.Add(new JObject
                    {
                        ["path"] = path + "@" + name.LocalName,
                        ["operation"] = "GridOrder",
                        ["before"] = (string)left,
                        ["after"] = (string)right
                    });
                    continue;
                }
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
                if (allowGridStructure && TryAllowGridVariableInsertion(before, oldNodes, newNodes, path))
                    return;
                if (TryAllowWebComponentInsertion(before, after, oldNodes, newNodes, path))
                    return;
                Reject("PatternStructureChangeUnsupported", "Child structure changed at " + path);
                return;
            }
            if (allowGridStructure && IsGrid(before)
                && TryCompareGridReorder(oldNodes, newNodes, path, allowGridStructure))
                return;
            for (int i = 0; i < oldNodes.Length; i++)
            {
                if (oldNodes[i] is XElement oldElement && newNodes[i] is XElement newElement)
                {
                    if (IsMetadata(oldElement.Name.LocalName) && !XNode.DeepEquals(oldElement, newElement))
                        Reject("PatternMetadataChangeUnsupported", "SDK-owned metadata changed at " + path + oldElement.Name);
                    else
                        Compare(oldElement, newElement, path + "[" + i + "]/", allowGridStructure);
                }
                else if (!XNode.DeepEquals(oldNodes[i], newNodes[i]))
                    Reject("PatternStructureChangeUnsupported", "Text or node structure changed at " + path);
                if (ErrorCode != null) return;
            }
        }

        private static bool IsGrid(XElement element)
            => element != null && string.Equals(element.Name.LocalName, "grid", StringComparison.OrdinalIgnoreCase);

        private static bool IsGridColumn(XElement element)
            => element != null && (string.Equals(element.Name.LocalName, "gridAttribute", StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.Name.LocalName, "gridVariable", StringComparison.OrdinalIgnoreCase));

        private static string GridColumnIdentity(XElement element)
        {
            if (!IsGridColumn(element)) return null;
            string identity = (string)element.Attribute("variable")
                ?? (string)element.Attribute("attribute")
                ?? (string)element.Attribute("name");
            return string.IsNullOrWhiteSpace(identity) ? null : identity.Trim();
        }

        private bool TryAllowGridVariableInsertion(XElement parent, XNode[] oldNodes, XNode[] newNodes, string path)
        {
            if (!IsGrid(parent) || newNodes.Length != oldNodes.Length + 1) return false;
            int insertedIndex = -1;
            for (int i = 0; i < newNodes.Length; i++)
            {
                if (newNodes[i] is XElement element
                    && string.Equals(element.Name.LocalName, "gridVariable", StringComparison.OrdinalIgnoreCase))
                {
                    if (insertedIndex >= 0) return false;
                    insertedIndex = i;
                }
            }
            if (insertedIndex < 0 || !(newNodes[insertedIndex] is XElement inserted)
                || inserted.HasElements) return false;
            if (string.IsNullOrWhiteSpace((string)inserted.Attribute("variable"))
                || string.IsNullOrWhiteSpace((string)inserted.Attribute("name"))) return false;
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "variable", "name", "description", "caption", "basicType", "basicCLength", "type", "length"
            };
            if (inserted.Attributes().Any(a => !allowed.Contains(a.Name.LocalName))) return false;
            int oldIndex = 0;
            for (int newIndex = 0; newIndex < newNodes.Length; newIndex++)
            {
                if (newIndex == insertedIndex) continue;
                if (oldIndex >= oldNodes.Length || !XNode.DeepEquals(oldNodes[oldIndex], newNodes[newIndex])) return false;
                oldIndex++;
            }
            if (oldIndex != oldNodes.Length) return false;
            Changes.Add(new JObject
            {
                ["path"] = path + "gridVariable[@name='" + (string)inserted.Attribute("name") + "']",
                ["operation"] = "Insert",
                ["name"] = (string)inserted.Attribute("name")
            });
            return true;
        }

        private bool TryCompareGridReorder(XNode[] oldNodes, XNode[] newNodes, string path, bool allowGridStructure)
        {
            if (!allowGridStructure || oldNodes.Length != newNodes.Length) return false;

            // A grid can contain action groups, filters, sort metadata, and other
            // authored children alongside its columns.  A valid move is one column
            // moving through that sequence; removing that column must leave every
            // other direct child in exactly the same order and with the same data.
            var oldColumns = oldNodes.OfType<XElement>().Where(IsGridColumn).ToList();
            var newColumns = newNodes.OfType<XElement>().Where(IsGridColumn).ToList();
            if (oldColumns.Count == 0 || oldColumns.Count != newColumns.Count) return false;

            var oldMap = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            var oldIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < oldNodes.Length; i++)
            {
                if (!(oldNodes[i] is XElement column) || !IsGridColumn(column)) continue;
                string identity = GridColumnIdentity(column);
                if (string.IsNullOrWhiteSpace(identity) || oldMap.ContainsKey(identity)) return false;
                oldMap[identity] = column;
                oldIndexes[identity] = i;
            }

            var newMap = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            var newIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < newNodes.Length; i++)
            {
                if (!(newNodes[i] is XElement column) || !IsGridColumn(column)) continue;
                string identity = GridColumnIdentity(column);
                if (string.IsNullOrWhiteSpace(identity) || newMap.ContainsKey(identity)
                    || !oldMap.ContainsKey(identity)) return false;
                newMap[identity] = column;
                newIndexes[identity] = i;
            }
            if (oldMap.Count != newMap.Count) return false;

            bool columnPositionChanged = oldMap.Keys.Any(identity =>
                oldIndexes[identity] != newIndexes[identity]);
            if (!columnPositionChanged) return false;

            int moveCandidates = 0;
            string movedIdentity = null;
            foreach (string identity in oldMap.Keys)
            {
                int oldIndex = oldIndexes[identity];
                int newIndex = newIndexes[identity];
                if (oldIndex == newIndex) continue;

                var oldRemaining = oldNodes.Where((node, index) => index != oldIndex).ToArray();
                var newRemaining = newNodes.Where((node, index) => index != newIndex).ToArray();
                if (!SameNodeSequence(oldRemaining, newRemaining)) continue;
                moveCandidates++;
                movedIdentity = movedIdentity ?? identity;
            }
            if (moveCandidates == 0)
            {
                Reject("PatternStructureChangeUnsupported",
                    "Grid column movement changed another direct child at " + path);
                return true;
            }

            Changes.Add(new JObject { ["path"] = path, ["operation"] = "GridOrder" });
            Compare(oldMap[movedIdentity], newMap[movedIdentity],
                path + "[@" + movedIdentity + "]/", allowGridStructure);
            return true;
        }

        private static bool SameNodeSequence(XNode[] left, XNode[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (!XNode.DeepEquals(left[i], right[i])) return false;
            }
            return true;
        }

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
