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

        /// <summary>
        /// Element identities added and removed by this plan. Instance state on purpose: a
        /// static pair would leak one plan's move into the next, and these plans are built
        /// concurrently per request.
        /// </summary>
        private readonly Dictionary<string, string> _insertedIdentities =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _removedIdentities =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The installed pattern specification, when the caller could read it. A leaf added by a
        /// structural edit may then carry the attributes the specification declares for its type,
        /// not only the ones in <see cref="StructuralLayoutRules"/>.
        /// </summary>
        private IPatternSpecView _spec;

        public static PatternXmlEditPlan Create(string currentXml, string requestedXml, bool allowGridStructure = false)
        {
            return Create(currentXml, requestedXml, allowGridStructure, null);
        }

        internal static PatternXmlEditPlan Create(string currentXml, string requestedXml, bool allowGridStructure, IPatternSpecView spec)
        {
            var plan = new PatternXmlEditPlan { Xml = requestedXml, _spec = spec };
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
            if (plan.ErrorCode == null) plan.PromoteMatchedInsertRemoveAsMove();
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
                if (IsProtected(before.Name.LocalName, name.LocalName) || left?.IsNamespaceDeclaration == true || right?.IsNamespaceDeclaration == true)
                {
                    Reject("PatternMetadataChangeUnsupported", DescribeMetadataChange(path, name.LocalName));
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
                if (TryAllowAttributePromptEdit(before, after, oldNodes, newNodes, path))
                    return;
                // Issue #551: <parameter> leaves of a selection, prompt or user action list.
                if (TryAllowParameterListEdit(before, oldNodes, newNodes, path))
                    return;
                // Issue #349: structural layout authoring. Insert / remove / move of a
                // single leaf layout element inside an existing container.
                if (TryAllowStructuralLayoutEdit(oldNodes, newNodes, path, allowGridStructure, before))
                    return;
                if (TryAllowMultipleLayoutRemovals(oldNodes, newNodes, path))
                    return;
                Reject("PatternStructureChangeUnsupported", DescribeStructureChange(path, oldNodes, newNodes, allowGridStructure));
                return;
            }
            if (allowGridStructure && IsGrid(before)
                && TryCompareGridReorder(oldNodes, newNodes, path, allowGridStructure))
                return;
            // Issue #560: a <prompt> edited in place (target changed, a <parameter> added
            // or removed) leaves the <attribute> with the same number of children, so it
            // never reached the handler above and the positional recursion rejected the
            // <parameters> size change instead. Same handler, same allow-list, one Update.
            if (TryAllowAttributePromptEdit(before, after, oldNodes, newNodes, path))
                return;
            if (TryCompareContainerReorder(before, oldNodes, newNodes, path, allowGridStructure, out string reorderBlocker))
                return;
            for (int i = 0; i < oldNodes.Length; i++)
            {
                if (oldNodes[i] is XElement oldElement && newNodes[i] is XElement newElement)
                {
                    // Issue #545: identity matching declined because a sibling has no
                    // identity, and this slot now holds a sibling that used to be
                    // elsewhere. Reporting that positionally as "identity changed at
                    // .../@attribute" contradicts the childrenOrderedList message that
                    // told the caller to move the element; name the real blocker instead.
                    if (reorderBlocker != null && IsMovedSibling(oldNodes, newNodes, oldElement, newElement))
                    {
                        Reject("PatternStructureChangeUnsupported", DescribeBlockedReorder(path, reorderBlocker));
                        return;
                    }
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

        /// <summary>
        /// Explains a protected-attribute rejection in terms of what the caller should do
        /// instead.
        ///
        /// <para>
        /// Issue #349. The old message was "SDK-owned metadata or identity changed at
        /// /instance/.../@childrenOrderedList" and then pointed at "the appropriate SDK
        /// pattern authoring action" - which for this attribute does not exist. The
        /// reporter's move was in fact a correct structural edit that the caller had
        /// completed the hard way: they had also updated both <c>childrenOrderedList</c>
        /// values, because that is what the attribute looks like it wants. It does not.
        /// </para>
        ///
        /// <para>
        /// The SDK rebuilds that list from the children on save - which is why
        /// <c>PatternChildOrderReconciler</c> is deliberately kept out of the write path
        /// (see <c>PatternReconcileAttachmentTests</c>). So the fix is for the caller to
        /// leave it alone, and the message now says so.
        /// </para>
        /// </summary>
        private static string DescribeMetadataChange(string path, string attributeName)
        {
            if (string.Equals(attributeName, "childrenOrderedList", StringComparison.OrdinalIgnoreCase))
            {
                return "Do not edit childrenOrderedList at " + path
                    + ". WorkWithPlus rebuilds it from the child elements on save, so a"
                    + " hand-written value is both unnecessary and the reason a correct"
                    + " move or reorder is rejected. Move or reorder the child element"
                    + " itself and leave this attribute out of the edit; the ordering is"
                    + " then derived from document order.";
            }
            return "SDK-owned metadata or identity changed at " + path + "@" + attributeName
                + ". This attribute is maintained by the SDK and cannot be authored through"
                + " a raw XML edit.";
        }

        /// <summary>
        /// Issue #545. A reorder is matched by sibling identity, so one sibling without
        /// an identity makes every move in that container unverifiable. The caller can
        /// act on "which element and why"; they cannot act on "identity changed at
        /// .../attribute/@attribute", which is what the positional fallback said.
        /// </summary>
        private static string DescribeBlockedReorder(string path, string blocker)
        {
            return "Reorder refused at " + path + ": " + blocker
                + ". Siblings are matched by identity when they are reordered, and one that"
                + " cannot be matched makes the move read as an identity change instead."
                + " Give that element a name or controlName in the GeneXus IDE so every"
                + " sibling has a distinct identity, then retry; actions, tabs and grid"
                + " columns can also be moved through genexus_wwp move_action, move_tab"
                + " and move_grid_column.";
        }

        /// <summary>
        /// What a raw PatternInstance edit accepts and refuses, for the rejection hint.
        /// Issue #545: the previous hint said raw XML "is limited to existing property
        /// changes" after #349 had opened single-leaf structural edits, so a caller
        /// refused for one reason was told that the edits the preflight accepts were
        /// impossible. Keep this in step with the handlers in <see cref="Compare"/>.
        /// </summary>
        public const string RawXmlEditScopeHint =
            "raw PatternInstance XML accepts property changes on existing elements, one leaf"
            + " element (textBlock, attribute, variable, userAction, image, userControl,"
            + " actionGroup, errorViewer, data; or a webComponent into a table) added per"
            + " container, several leaf removals, an empty table removed, a leaf moved between containers, siblings"
            + " reordered by identity inside a table, actionGroup, tabs or tab, and a <prompt>"
            + " added to or removed from an <attribute>; it still refuses identity and"
            + " childrenOrderedList edits, container-valued or nested additions except a"
            + " specification-validated Selection table subtree, more than one addition per"
            + " container, and a reorder next to a sibling without an identity"
            + " attribute.";

        /// <summary>
        /// Names what actually changed, because "Child structure changed at /path" was true
        /// and useless: it did not say which child, nor whether the added element was
        /// malformed or simply not on the allow-list.
        /// </summary>
        private static string DescribeStructureChange(
            string path, XNode[] oldNodes, XNode[] newNodes, bool allowGridStructure)
        {
            int delta = newNodes.Length - oldNodes.Length;
            string verb = delta > 0 ? "added" : "removed";
            int count = Math.Abs(delta);

            var added = Names(newNodes.Except(oldNodes, StructuralNodeKeyComparer.Instance));
            var removed = Names(oldNodes.Except(newNodes, StructuralNodeKeyComparer.Instance));

            var sb = new System.Text.StringBuilder();
            sb.Append("Structural layout edit at ").Append(path).Append(": ")
              .Append(count).Append(count == 1 ? " child element " : " child elements ")
              .Append(verb).Append('.');
            if (removed.Count > 0) sb.Append(" Removed: ").Append(string.Join(", ", removed)).Append('.');
            if (added.Count > 0) sb.Append(" Added: ").Append(string.Join(", ", added)).Append('.');

            if (!allowGridStructure
                && added.Any(n => string.Equals(n, "gridVariable", StringComparison.OrdinalIgnoreCase)))
                sb.Append(" gridVariable insertion is only permitted on a grid container.");

            sb.Append(" Structural edits accept one leaf element per container, from the")
              .Append(" permitted kinds, carrying only that kind's documented attributes.")
              .Append(" Nested additions and container-valued additions are rejected because")
              .Append(" their generated layout cannot be predicted from the XML alone.");
            return sb.ToString();
        }

        private static List<string> Names(IEnumerable<XNode> nodes) => nodes
            .OfType<XElement>()
            .Select(e => e.Name.LocalName)
            .ToList();

        /// <summary>
        /// The attribute names the installed specification declares for a <paramref name="childKind"/>
        /// element added under <paramref name="container"/>, or null when there is no specification
        /// or it does not say (the static allow-list then decides alone).
        /// </summary>
        private IReadOnlyCollection<string> DeclaredAttributes(XElement container, string childKind)
        {
            if (_spec == null || container == null) return null;
            try
            {
                string type = _spec.RootType();
                foreach (XElement element in container.AncestorsAndSelf().Reverse().Skip(1))
                {
                    if (string.IsNullOrEmpty(type)) return null;
                    type = _spec.ChildType(type, element.Name.LocalName);
                }
                if (string.IsNullOrEmpty(type)) return null;
                string childType = _spec.ChildType(type, childKind);
                return string.IsNullOrEmpty(childType) ? null : _spec.AttributeNames(childType);
            }
            catch (Exception)
            {
                // A specification that cannot be read is "not declared": the static list stays in force.
                return null;
            }
        }

        private string SelectionTableViolation(XElement container, XElement table)
        {
            const string prefix = "Selection template table insertion refused: ";
            const string shape = " Supported shape: a named table with numberOfColumns and specification-declared controls or nested tables; explicit row/cell elements are not supported.";
            if (_spec == null) return prefix + "the installed pattern specification is unavailable." + shape;
            string type = _spec.RootType();
            foreach (XElement element in container.AncestorsAndSelf().Reverse().Skip(1))
            {
                if (string.IsNullOrEmpty(type)) break;
                type = _spec.ChildType(type, element.Name.LocalName);
            }
            string problem = SelectionSubtreeViolation(table, type);
            return problem == null ? null : prefix + problem + shape;
        }

        private string SelectionSubtreeViolation(XElement element, string parentType)
        {
            string kind = element.Name.LocalName;
            if (element.Name.Namespace != XNamespace.None
                || !(kind == "table" || kind == "textBlock" || kind == "variable" || kind == "attribute" || kind == "userAction"))
                return "child <" + kind + "> is outside the supported table/control shape.";
            if (kind != "table" && element.HasElements)
                return "control <" + kind + "> must be a leaf.";
            string type = string.IsNullOrEmpty(parentType) ? null : _spec.ChildType(parentType, kind);
            if (string.IsNullOrEmpty(type)) return "child <" + element.Name.LocalName + "> is not declared by the pattern specification.";
            var attributes = _spec.AttributeNames(type);
            if (attributes == null) return "attributes of <" + element.Name.LocalName + "> are unavailable.";
            foreach (XAttribute attribute in element.Attributes())
                if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None
                    || StructuralLayoutRules.IsSdkOwnedAttribute(attribute.Name.LocalName)
                    || !attributes.Contains(attribute.Name.LocalName))
                    return "attribute '" + attribute.Name + "' on <" + element.Name.LocalName + "> is not authorable.";
            string identityAttribute = kind == "textBlock" ? "controlName" : kind == "attribute" ? "attribute" : "name";
            if (string.IsNullOrWhiteSpace((string)element.Attribute(identityAttribute)))
                return "<" + kind + "> requires " + identityAttribute + ".";
            if (element.Nodes().Any(n => !(n is XElement) && !(n is XText text && string.IsNullOrWhiteSpace(text.Value))))
                return "non-element content in <" + element.Name.LocalName + "> is not supported.";
            foreach (XElement child in element.Elements())
            {
                string problem = SelectionSubtreeViolation(child, type);
                if (problem != null) return problem;
            }
            return null;
        }

        /// <summary>
        /// Structural layout authoring: one leaf element added to, or removed from, an
        /// existing container.
        ///
        /// <para>
        /// Issue #349. The reporter could set attributes but not add elements: a
        /// <c>&lt;textBlock&gt;</c> added inside an existing table was refused as
        /// <c>PatternStructureChangeUnsupported</c> even though it touched nothing but
        /// itself. The gate already permitted exactly this shape for two kinds
        /// (<c>gridVariable</c> into a grid, <c>webComponent</c> into a table); this
        /// generalises the same rule to the layout kinds the issue names.
        /// </para>
        ///
        /// <para>
        /// Deliberately narrow, because this cannot be verified against a real
        /// PatternInstance without the WorkWithPlus package installed, and the cost of
        /// being too permissive is a corrupt pattern rather than a rejected edit. So:
        /// exactly one node of difference, that node must be a child element of a
        /// permitted kind, it must be a leaf, and every attribute on it must be in that
        /// kind's allow-list. A nested addition, a container arriving with children, or an
        /// unknown attribute is rejected - and the message names the offending attribute,
        /// so the caller can correct it rather than guess.
        /// </para>
        ///
        /// <para>
        /// <b>Cross-container moves fall out of this for free.</b> A move is one parent
        /// losing a child and a sibling gaining one, and the SDK derives both
        /// <c>childrenOrderedList</c> values from the resulting document order on save.
        /// <see cref="PromoteMatchedInsertRemoveAsMove"/> then reports it as the single
        /// <c>Move</c> operation it is, instead of leaving the caller to notice.
        /// </para>
        /// </summary>
        private bool TryAllowStructuralLayoutEdit(
            XNode[] oldNodes, XNode[] newNodes, string path, bool allowGridStructure, XElement container = null)
        {
            // Exactly one node of difference, on one side only.
            if (Math.Abs(newNodes.Length - oldNodes.Length) != 1) return false;
            bool insertion = newNodes.Length > oldNodes.Length;

            // First divergence is the changed node. Scanning it once keeps this O(n)
            // rather than the O(n^2) a "try removing each index" search would cost on a
            // container with hundreds of children.
            int limit = Math.Min(oldNodes.Length, newNodes.Length);
            int firstDiff = limit;
            for (int i = 0; i < limit; i++)
            {
                if (SameLayoutNode(oldNodes[i], newNodes[i])) continue;
                firstDiff = i;
                break;
            }

            if (insertion)
            {
                if (firstDiff >= newNodes.Length) return false;
                if (!(newNodes[firstDiff] is XElement inserted)) return false;
                // Everything but the inserted node must be untouched and in order, or this
                // is a rewrite rather than an insertion. Untouched is semantic (#551): a
                // sibling re-serialised with its attributes in another order, or re-indented,
                // is the same sibling, and XNode.DeepEquals refused it as if the inserted
                // element were the problem.
                for (int i = 0, j = 0; i < newNodes.Length; i++)
                {
                    if (i == firstDiff) continue;
                    if (j >= oldNodes.Length || !SameLayoutNode(oldNodes[j], newNodes[i]))
                    {
                        // A permitted leaf next to a sibling that really did change: name the
                        // sibling, because "1 child element added" blamed the wrong node.
                        var addedKinds = Names(newNodes.Except(oldNodes, StructuralNodeKeyComparer.Instance));
                        if (addedKinds.Count == 1 && StructuralLayoutRules.IsPermittedKind(addedKinds[0])
                            && j < oldNodes.Length && oldNodes[j] is XElement sibling)
                        {
                            Reject("PatternStructureChangeUnsupported",
                                "Structural layout edit at " + path + ": a <" + addedKinds[0] + "> was added,"
                                + " but the existing <" + sibling.Name.LocalName + "> at index " + j
                                + " also changed. A structural edit must leave every other child of the"
                                + " container as it was; add the element alone and edit the sibling in a"
                                + " separate import.");
                            return true;
                        }
                        return false;
                    }
                    j++;
                }

                string kind = inserted.Name.LocalName;
                if (kind == "table" && container != null && container.Name.LocalName == "table"
                    && container.Ancestors().Any(e => e.Name.LocalName == "WPRoot" && (string)e.Attribute("Template") == "Selection"))
                {
                    string violation = SelectionTableViolation(container, inserted);
                    if (violation != null)
                        Reject("PatternStructureChangeUnsupported", violation);
                    else
                        Changes.Add(new JObject { ["path"] = path, ["operation"] = "Insert", ["element"] = "table",
                            ["identity"] = (string)inserted.Attribute("name"), ["index"] = firstDiff, ["ordering"] = OrderingNote });
                    return true;
                }
                if (!StructuralLayoutRules.IsPermittedKind(kind)) return false;
                if (inserted.HasElements) return false;

                var declared = DeclaredAttributes(container, kind);
                string offender = StructuralLayoutRules.FirstDisallowedAttribute(kind, inserted, declared);
                if (offender != null)
                {
                    Reject("PatternStructureChangeUnsupported",
                        "Attribute '" + offender + "' is not permitted on a <" + kind + ">"
                        + " added at " + path + ". Permitted: "
                        + string.Join(", ", StructuralLayoutRules.AllowedAttributes(kind)
                            .Concat(declared ?? Enumerable.Empty<string>())
                            .Where(a => !StructuralLayoutRules.IsSdkOwnedAttribute(a))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                        + ".");
                    return true;
                }
                string identity = StructuralLayoutRules.Identity(kind, inserted);
                if (identity == null)
                {
                    Reject("PatternStructureChangeUnsupported",
                        "A <" + kind + "> added at " + path + " needs "
                        + StructuralLayoutRules.RequiredIdentityAttribute(kind) + ".");
                    return true;
                }

                Changes.Add(new JObject
                {
                    ["path"] = path,
                    ["operation"] = "Insert",
                    ["element"] = kind,
                    ["identity"] = identity,
                    ["index"] = firstDiff,
                    ["after"] = firstDiff == 0 ? null : "existing child at index " + (firstDiff - 1),
                    ["ordering"] = OrderingNote
                });
                _insertedIdentities[identity] = kind;
                return true;
            }

            if (firstDiff >= oldNodes.Length) return false;
            if (!(oldNodes[firstDiff] is XElement removed)) return false;
            // Everything but the removed node must be untouched and in order.
            for (int i = 0, j = 0; i < oldNodes.Length; i++)
            {
                if (i == firstDiff) continue;
                if (j >= newNodes.Length || !SameLayoutNode(newNodes[j], oldNodes[i])) return false;
                j++;
            }

            string removedKind = removed.Name.LocalName;
            if (!StructuralLayoutRules.IsRemovable(removed)) return false;

            string removedIdentity = StructuralLayoutRules.ReportIdentity(removedKind, removed);
            Changes.Add(new JObject
            {
                ["path"] = path,
                ["operation"] = "Remove",
                ["element"] = removedKind,
                ["identity"] = removedIdentity,
                ["index"] = firstDiff,
                ["ordering"] = OrderingNote
            });
            // Issue #545: ReportIdentity is null for a leaf that predates its identity
            // attribute, and a null key throws out of the plan instead of planning the
            // removal.
            if (removedIdentity != null) _removedIdentities[removedIdentity] = removedKind;
            return true;
        }

        private const string OrderingNote =
            "childrenOrderedList is not authored; the SDK rebuilds it from document order";

        /// <summary>
        /// Whether an untouched sibling is the same sibling: same element name, the same
        /// attributes in any order, and the same significant children compared the same
        /// way; other node types fall back to <see cref="XNode.DeepEquals"/>.
        ///
        /// <para>
        /// Issue #551. The reporter added one well-formed textBlock and was told
        /// "1 child element added. Added: textBlock" - the structural handler had declined
        /// silently because <see cref="XNode.DeepEquals"/> is order-sensitive on attributes
        /// and counts insignificant whitespace, so a sibling the client had merely
        /// re-serialised failed the "everything else untouched" check. Attribute order and
        /// indentation mean nothing to the SDK that deserialises the payload, and the
        /// property-change path already compares attributes by name, so this is the same
        /// notion of "unchanged" applied to the structural path.
        /// </para>
        /// </summary>
        private static bool SameLayoutNode(XNode left, XNode right)
        {
            if (!(left is XElement a) || !(right is XElement b)) return XNode.DeepEquals(left, right);
            if (a.Name != b.Name) return false;
            var attributes = a.Attributes().Where(x => !x.IsNamespaceDeclaration).ToList();
            if (attributes.Count != b.Attributes().Count(x => !x.IsNamespaceDeclaration)) return false;
            foreach (var attribute in attributes)
            {
                var other = b.Attribute(attribute.Name);
                if (other == null || !string.Equals(attribute.Value, other.Value, StringComparison.Ordinal)) return false;
            }
            var leftChildren = a.Nodes().Where(Significant).ToArray();
            var rightChildren = b.Nodes().Where(Significant).ToArray();
            if (leftChildren.Length != rightChildren.Length) return false;
            for (int i = 0; i < leftChildren.Length; i++)
                if (!SameLayoutNode(leftChildren[i], rightChildren[i])) return false;
            return true;
        }

        /// <summary>
        /// Collapses a matched Insert + Remove of the same element identity into one
        /// <c>Move</c>, because that is what the caller did and what the issue asked to
        /// see reported.
        ///
        /// <para>
        /// Purely a reporting refinement - each half is already an independently permitted
        /// single-leaf operation, so safety never depended on recognising the pair.
        /// </para>
        /// </summary>
        internal void PromoteMatchedInsertRemoveAsMove()
        {
            if (_removedIdentities.Count == 0 || _insertedIdentities.Count == 0) return;

            foreach (var change in Changes.ToArray())
            {
                if ((string)change["operation"] != "Insert") continue;
                string identity = (string)change["identity"];
                if (identity == null) continue;
                if (!_removedIdentities.ContainsKey(identity)) continue;

                var removal = Changes.FirstOrDefault(c =>
                    (string)c["operation"] == "Remove"
                    && string.Equals((string)c["identity"], identity, StringComparison.OrdinalIgnoreCase));
                if (removal == null) continue;

                change["operation"] = "Move";
                change["fromPath"] = removal["path"]?.ToString();
                change["fromIndex"] = removal["index"];
                change["reason"] = "same element removed from one container and inserted into another";
                removal.Remove();
                _removedIdentities.Remove(identity);
                _insertedIdentities.Remove(identity);
            }
        }

        /// <summary>
        /// Compares two nodes by element kind and identity rather than by content, so a
        /// rewritten-but-same-named child is recognised as the same child when describing
        /// a structural change.
        /// </summary>
        private sealed class StructuralNodeKeyComparer : IEqualityComparer<XNode>
        {
            internal static readonly StructuralNodeKeyComparer Instance = new StructuralNodeKeyComparer();

            public bool Equals(XNode x, XNode y)
            {
                if (x is XElement ex && y is XElement ey)
                {
                    return string.Equals(ex.Name.LocalName, ey.Name.LocalName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            StructuralLayoutRules.Identity(ex.Name.LocalName, ex),
                            StructuralLayoutRules.Identity(ey.Name.LocalName, ey),
                            StringComparison.OrdinalIgnoreCase);
                }
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(XNode obj)
            {
                if (obj is XElement e) return (e.Name.LocalName ?? "").GetHashCode();
                return 0;
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

        private static bool IsProtected(string elementName, string name)
        {
            if (string.Equals(elementName, "parameter", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(name, "name", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "value", StringComparison.OrdinalIgnoreCase)))
                return false;

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

        private struct ElementPosition
        {
            public XElement Element;
            public int Index;
            public ElementPosition(XElement element, int index) { Element = element; Index = index; }
        }

        /// <summary>
        /// Same-size container whose children were reordered: match them by identity
        /// and report a <c>Reorder</c> per displaced child.
        ///
        /// <para>
        /// Issue #545. <paramref name="blocker"/> is set when the container is one this
        /// handles and a child cannot be matched (no identity, or an identity shared
        /// with a sibling). The caller does not reject on it - a property edit next to
        /// an unnamed group table must keep working - but uses it to explain a refused
        /// move instead of reporting a positional identity change.
        /// </para>
        /// </summary>
        private bool TryCompareContainerReorder(
            XElement container, XNode[] oldNodes, XNode[] newNodes, string path, bool allowGridStructure,
            out string blocker)
        {
            blocker = null;
            if (oldNodes.Length != newNodes.Length || oldNodes.Length <= 1) return false;

            string containerName = container.Name.LocalName;
            if (!string.Equals(containerName, "table", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(containerName, "actionGroup", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(containerName, "tabs", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(containerName, "tab", StringComparison.OrdinalIgnoreCase))
                return false;

            var oldElements = oldNodes.OfType<XElement>().ToList();
            var newElements = newNodes.OfType<XElement>().ToList();
            if (oldElements.Count != oldNodes.Length || newElements.Count != newNodes.Length)
                return false;

            var oldMap = new Dictionary<string, ElementPosition>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < oldElements.Count; i++)
            {
                string id = GetElementCompositeKey(oldElements[i]);
                if (id == null)
                {
                    blocker = "child <" + oldElements[i].Name.LocalName + "> at index " + i
                        + " has no identity attribute (name, controlName, attribute or variable;"
                        + " an unnamed <table> may be addressed by a unique title)";
                    return false;
                }
                if (oldMap.TryGetValue(id, out var duplicate))
                {
                    blocker = "children at index " + duplicate.Index + " and index " + i
                        + " share the identity '" + id + "'";
                    return false;
                }
                oldMap[id] = new ElementPosition(oldElements[i], i);
            }

            var newMap = new Dictionary<string, ElementPosition>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < newElements.Count; i++)
            {
                string id = GetElementCompositeKey(newElements[i]);
                if (string.IsNullOrWhiteSpace(id) || newMap.ContainsKey(id)) return false;
                if (!oldMap.ContainsKey(id)) return false;
                newMap[id] = new ElementPosition(newElements[i], i);
            }

            if (oldMap.Count != newMap.Count) return false;

            bool anyPositionChanged = false;
            foreach (var kvp in newMap)
            {
                if (kvp.Value.Index != oldMap[kvp.Key].Index)
                {
                    anyPositionChanged = true;
                    break;
                }
            }

            if (!anyPositionChanged) return false;

            foreach (var kvp in newMap)
            {
                int newIdx = kvp.Value.Index;
                int oldIdx = oldMap[kvp.Key].Index;
                XElement newEl = kvp.Value.Element;
                XElement oldEl = oldMap[kvp.Key].Element;

                if (newIdx != oldIdx)
                {
                    string id = ElementIdentity(newEl);
                    Changes.Add(new JObject
                    {
                        ["path"] = path + newEl.Name.LocalName + "[@" + id + "]",
                        ["operation"] = "Reorder",
                        ["element"] = newEl.Name.LocalName,
                        ["identity"] = id,
                        ["fromIndex"] = oldIdx,
                        ["toIndex"] = newIdx,
                        ["ordering"] = OrderingNote
                    });
                }

                Compare(oldEl, newEl, path + "[" + newIdx + "]/", allowGridStructure);
                if (ErrorCode != null) return true;
            }

            return true;
        }

        private static string GetElementCompositeKey(XElement element)
        {
            string id = ElementIdentity(element);
            return id == null ? null : element.Name.LocalName + ":" + id;
        }

        /// <summary>
        /// The identity a sibling is matched by when reordering, or null.
        ///
        /// <para>
        /// Issue #545. The lookup is case-insensitive because WorkWithPlus writes
        /// <c>ControlName</c> on tab elements and <c>controlName</c> on text blocks, and
        /// an <c>XName</c> lookup on the wrong spelling made every tab reorder fall back
        /// to the positional comparison. An unnamed group table is addressed by its
        /// title, the same weak identifier <c>PatternChildOrderReconciler</c> derives for
        /// it; uniqueness is enforced by the caller's map, not here.
        /// </para>
        /// </summary>
        private static string ElementIdentity(XElement element)
        {
            if (element == null) return null;
            string kind = element.Name.LocalName;
            string id = StructuralLayoutRules.Identity(kind, element)
                ?? AttributeIgnoreCase(element, "attribute")
                ?? AttributeIgnoreCase(element, "controlName")
                ?? AttributeIgnoreCase(element, "name")
                ?? AttributeIgnoreCase(element, "variable");
            if (id == null && string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase))
                id = AttributeIgnoreCase(element, "title") ?? AttributeIgnoreCase(element, "caption");
            return id?.Trim();
        }

        private static string AttributeIgnoreCase(XElement element, string localName)
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                if (!string.Equals(attribute.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase)) continue;
                return string.IsNullOrWhiteSpace(attribute.Value) ? null : attribute.Value;
            }
            return null;
        }

        /// <summary>
        /// True when the element now at this position carries the identity of a sibling
        /// that was elsewhere, or the old occupant's identity turns up elsewhere: a move,
        /// not a rename. A rename to an identity no sibling had stays a protected
        /// attribute change and keeps its own message.
        /// </summary>
        private static bool IsMovedSibling(XNode[] oldNodes, XNode[] newNodes, XElement oldElement, XElement newElement)
        {
            string oldKey = GetElementCompositeKey(oldElement);
            string newKey = GetElementCompositeKey(newElement);
            if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return false;
            return (newKey != null && oldNodes.OfType<XElement>().Any(e =>
                        string.Equals(GetElementCompositeKey(e), newKey, StringComparison.OrdinalIgnoreCase)))
                || (oldKey != null && newNodes.OfType<XElement>().Any(e =>
                        string.Equals(GetElementCompositeKey(e), oldKey, StringComparison.OrdinalIgnoreCase)));
        }

        private bool TryAllowMultipleLayoutRemovals(XNode[] oldNodes, XNode[] newNodes, string path)
        {
            if (oldNodes.Length <= newNodes.Length) return false;

            int oldIdx = 0;
            var removedElements = new List<ElementPosition>();
            for (int newIdx = 0; newIdx < newNodes.Length; newIdx++)
            {
                while (oldIdx < oldNodes.Length && !XNode.DeepEquals(oldNodes[oldIdx], newNodes[newIdx]))
                {
                    if (oldNodes[oldIdx] is XElement removedEl)
                    {
                        if (!StructuralLayoutRules.IsRemovable(removedEl)) return false;
                        removedElements.Add(new ElementPosition(removedEl, oldIdx));
                    }
                    else return false;
                    oldIdx++;
                }
                if (oldIdx >= oldNodes.Length) return false;
                oldIdx++;
            }
            while (oldIdx < oldNodes.Length)
            {
                if (oldNodes[oldIdx] is XElement removedEl)
                {
                    if (!StructuralLayoutRules.IsRemovable(removedEl)) return false;
                    removedElements.Add(new ElementPosition(removedEl, oldIdx));
                }
                else return false;
                oldIdx++;
            }

            if (removedElements.Count == 0) return false;

            foreach (var item in removedElements)
            {
                string removedKind = item.Element.Name.LocalName;
                string removedIdentity = StructuralLayoutRules.ReportIdentity(removedKind, item.Element);
                Changes.Add(new JObject
                {
                    ["path"] = path,
                    ["operation"] = "Remove",
                    ["element"] = removedKind,
                    ["identity"] = removedIdentity,
                    ["index"] = item.Index,
                    ["ordering"] = OrderingNote
                });
                if (removedIdentity != null) _removedIdentities[removedIdentity] = removedKind;
            }

            return true;
        }

        /// <summary>
        /// Issue #560: the DVelop Combo insert prompt, a <c>&lt;prompt&gt;</c> child of an
        /// <c>&lt;attribute&gt;</c> carrying a target and a <c>&lt;parameters&gt;</c> list.
        /// Accepts adding it, removing it, or replacing it in place (target changed, a
        /// <c>&lt;parameter&gt;</c> added or removed) as one <c>Insert</c> / <c>Remove</c> /
        /// <c>Update</c>, provided every other child of the attribute is untouched.
        ///
        /// <para>
        /// A new or changed prompt outside the documented shape is rejected with the
        /// offending item named. An unchanged prompt is left to the positional comparison,
        /// so an instance whose prompt carries an attribute this gate does not know still
        /// takes unrelated property edits.
        /// </para>
        /// </summary>
        private bool TryAllowAttributePromptEdit(XElement before, XElement after, XNode[] oldNodes, XNode[] newNodes, string path)
        {
            if (!string.Equals(before.Name.LocalName, "attribute", StringComparison.OrdinalIgnoreCase))
                return false;

            var oldPrompt = oldNodes.OfType<XElement>().FirstOrDefault(IsPrompt);
            var newPrompt = newNodes.OfType<XElement>().FirstOrDefault(IsPrompt);

            if (oldPrompt == null && newPrompt == null) return false;
            if (oldPrompt != null && newPrompt != null && XNode.DeepEquals(oldPrompt, newPrompt)) return false;

            if (newPrompt != null)
            {
                string violation = PromptViolation(newPrompt, path);
                if (violation != null)
                {
                    Reject("PatternStructureChangeUnsupported", violation);
                    return true;
                }
            }

            var oldOther = oldNodes.Where(n => n != oldPrompt).ToArray();
            var newOther = newNodes.Where(n => n != newPrompt).ToArray();
            if (!SameNodeSequence(oldOther, newOther)) return false;

            var change = new JObject
            {
                ["path"] = path + "prompt",
                ["operation"] = oldPrompt == null ? "Insert" : newPrompt == null ? "Remove" : "Update",
                ["element"] = "prompt"
            };
            if (newPrompt != null) change["prompt"] = PromptTarget(newPrompt);
            Changes.Add(change);
            return true;
        }
        /// <summary>
        /// Issue #551: the parameter list of a WorkWithPlus selection, prompt or user action
        /// is a flat list of <c>&lt;parameter name value/&gt;</c> leaves. A caller may add
        /// or remove such leaves inside an existing <c>&lt;parameters&gt;</c>, and may add
        /// (or remove) the list itself directly under <c>&lt;selection&gt;</c>, which has no
        /// list until one is authored. Everything else about the list - a parameter outside
        /// it, an unknown attribute, a nested child, a list under any other parent - stays
        /// with the structural gate, so the one shape accepted is the one WorkWithPlus
        /// already serialises. Persistence of a selection list is unverified against the
        /// SDK on this host.
        /// </summary>
        private bool TryAllowParameterListEdit(XElement before, XNode[] oldNodes, XNode[] newNodes, string path)
        {
            if (IsNamed(before, "parameters")
                && (IsNamed(before.Parent, "selection") || IsNamed(before.Parent, "prompt") || IsNamed(before.Parent, "userAction")))
                return TryAllowParameterLeafEdit(oldNodes, newNodes, path);

            if (!IsNamed(before, "selection") || Math.Abs(newNodes.Length - oldNodes.Length) != 1) return false;
            bool insertion = newNodes.Length > oldNodes.Length;
            XNode[] longer = insertion ? newNodes : oldNodes;
            XNode[] shorter = insertion ? oldNodes : newNodes;
            int index = 0;
            while (index < shorter.Length && XNode.DeepEquals(longer[index], shorter[index])) index++;
            if (!(longer[index] is XElement list) || !IsNamed(list, "parameters")
                || list.Attributes().Any(a => !a.IsNamespaceDeclaration)) return false;
            for (int i = index + 1; i < longer.Length; i++)
                if (!XNode.DeepEquals(longer[i], shorter[i - 1])) return false;

            var leaves = list.Nodes().Where(Significant).ToArray();
            if (!leaves.All(leaf => leaf is XElement element && IsNamed(element, "parameter"))) return false;
            if (insertion)
            {
                foreach (XElement leaf in leaves)
                {
                    string problem = ParameterLeafProblem(leaf);
                    if (problem == null) continue;
                    Reject("PatternStructureChangeUnsupported", problem + " at " + path + "parameters/.");
                    return true;
                }
            }
            Changes.Add(new JObject
            {
                ["path"] = path + "parameters",
                ["operation"] = insertion ? "Insert" : "Remove",
                ["element"] = "parameters",
                ["index"] = index,
                ["parameters"] = leaves.Length
            });
            return true;
        }

        private bool TryAllowParameterLeafEdit(XNode[] oldNodes, XNode[] newNodes, string path)
        {
            if (!oldNodes.All(n => n is XElement e && IsNamed(e, "parameter"))
                || !newNodes.All(n => n is XElement e && IsNamed(e, "parameter"))) return false;
            bool insertion = newNodes.Length > oldNodes.Length;
            XNode[] longer = insertion ? newNodes : oldNodes;
            XNode[] shorter = insertion ? oldNodes : newNodes;
            // The shorter side must survive in order; whatever is left over is the edit.
            var changed = new List<int>();
            int kept = 0;
            for (int i = 0; i < longer.Length; i++)
            {
                if (kept < shorter.Length && XNode.DeepEquals(longer[i], shorter[kept])) kept++;
                else changed.Add(i);
            }
            if (kept != shorter.Length) return false;
            foreach (int index in changed)
            {
                var leaf = (XElement)longer[index];
                string problem = insertion ? ParameterLeafProblem(leaf) : null;
                if (problem != null)
                {
                    Reject("PatternStructureChangeUnsupported", problem + " at " + path + ".");
                    return true;
                }
                Changes.Add(new JObject
                {
                    ["path"] = path,
                    ["operation"] = insertion ? "Insert" : "Remove",
                    ["element"] = "parameter",
                    ["identity"] = (string)leaf.Attribute("name"),
                    ["index"] = index
                });
            }
            return true;
        }

        private static string ParameterLeafProblem(XElement leaf)
        {
            if (leaf.HasElements) return "A <parameter> may not carry child elements";
            var offender = leaf.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration
                && !a.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase)
                && !a.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase));
            if (offender != null)
                return "Attribute '" + offender.Name.LocalName + "' is not permitted on a <parameter>. Permitted: name, value";
            if (string.IsNullOrWhiteSpace((string)leaf.Attribute("name"))) return "A <parameter> needs a name";
            return null;
        }

        private static bool IsNamed(XElement element, string name)
            => element != null && string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

        private static bool IsPrompt(XElement element)
            => string.Equals(element.Name.LocalName, "prompt", StringComparison.OrdinalIgnoreCase);

        /// <summary>The object the prompt opens (<c>prompt</c>, else <c>gxobject</c>), or null when it has none.</summary>
        private static string PromptTarget(XElement prompt)
        {
            string target = (string)prompt.Attribute("prompt") ?? (string)prompt.Attribute("gxobject");
            return string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        }

        /// <summary>
        /// Why a <c>&lt;prompt&gt;</c> is outside the documented shape, or null when it is
        /// inside it: a non-empty target, no attribute beyond <c>prompt</c> /
        /// <c>gxobject</c> / <c>name</c>, and at most a <c>&lt;parameters&gt;</c> child made
        /// of leaf <c>&lt;parameter name|value&gt;</c> elements.
        /// </summary>
        private static string PromptViolation(XElement prompt, string path)
        {
            string where = " on the <prompt> at " + path + "prompt.";
            foreach (var attr in prompt.Attributes())
            {
                if (attr.IsNamespaceDeclaration) continue;
                string name = attr.Name.LocalName;
                if (!name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("gxobject", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("name", StringComparison.OrdinalIgnoreCase))
                    return "Attribute '" + name + "' is not permitted" + where + " Permitted: gxobject, name, prompt.";
            }
            if (PromptTarget(prompt) == null)
                return "A non-empty prompt (or gxobject) target is required" + where;
            foreach (var child in prompt.Elements())
            {
                if (!child.Name.LocalName.Equals("parameters", StringComparison.OrdinalIgnoreCase))
                    return "Element <" + child.Name.LocalName + "> is not permitted" + where + " Only <parameters> is.";
                foreach (var param in child.Elements())
                {
                    if (!param.Name.LocalName.Equals("parameter", StringComparison.OrdinalIgnoreCase) || param.HasElements)
                        return "Only leaf <parameter> elements are permitted under <parameters>" + where;
                    foreach (var pAttr in param.Attributes())
                    {
                        if (pAttr.IsNamespaceDeclaration) continue;
                        string pName = pAttr.Name.LocalName;
                        if (!pName.Equals("name", StringComparison.OrdinalIgnoreCase)
                            && !pName.Equals("value", StringComparison.OrdinalIgnoreCase))
                            return "Attribute '" + pName + "' is not permitted on a <parameter>" + where + " Permitted: name, value.";
                    }
                }
            }
            return null;
        }
    }
}
