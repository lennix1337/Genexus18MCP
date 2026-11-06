using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Descriptors;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Parsers
{
    // issue #36.1 — raised when the SDK could not remove one or more attributes that a
    // Structure write intended to drop (mode:full replacement, or a remove_attribute op).
    // The interceptor turns this into a StructureAttributeNotRemoved error so the caller
    // never sees a false success for an additive-only no-op.
    public class StructureRemovalException : Exception
    {
        public IReadOnlyList<string> Failures { get; }
        public StructureRemovalException(IReadOnlyList<string> failures)
            : base("Could not remove attribute(s): " + string.Join("; ", failures))
        {
            Failures = failures;
        }
    }

    public class TransactionDslParser : IDslParser
    {
        // Accumulates removal failures across all (recursive) SyncTransactionNodes calls;
        // checked once after the top-level sync in Parse(). A fresh parser is created per
        // ParseFromText call (StructureParser.GetParser), so this is never shared.
        private readonly List<string> _removalFailures = new List<string>();

        /// <summary>
        /// Attributes this parse dropped from the Transaction's levels. Read by
        /// <see cref="Parse"/> to flag the Structure part as changed (issue #558).
        /// </summary>
        internal int RemovedAttributeCount { get; private set; }

        public void Serialize(KBObject obj, StringBuilder sb)
        {
            if (obj is Transaction trn)
            {
                SerializeLevel(trn.Structure.Root, sb, 0);
            }
        }

        /// <summary>
        /// Splits DSL text into physical lines, folding multiline attribute
        /// declarations back into a single line first.
        ///
        /// GeneXus allows an attribute formula to wrap across physical lines, so
        /// a formula continued onto the next line arrived here as its own node:
        /// the truncated first line failed validation, or the continuation was
        /// read as a level name - and in one observed case as an attribute to
        /// create, leaving a stray global behind (issue #492). Rejoining the
        /// continuation before parsing keeps the attribute entry atomic, so
        /// insertion also lands after the complete entry.
        ///
        /// A fold continues only while the line carries an unbalanced opening
        /// bracket inside the formula marker. Levels and attributes stay one per
        /// line. The marker text itself is assembled at runtime so this file does
        /// not re-state the rendering rules that
        /// <see cref="Helpers.AttributeDeclaration"/> owns; the single-source
        /// guard in <c>SharedHelperSingleSourceTests</c> enforces that.
        /// </summary>
        internal static List<string> SplitDslLines(string text)
        {
            var raw = (text ?? string.Empty)
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                .ToList();

            var lines = new List<string>(raw.Count);
            var pending = new StringBuilder();
            int depth = 0;

            foreach (var line in raw)
            {
                if (depth > 0)
                {
                    pending.Append(' ').Append(line.Trim());
                }
                else
                {
                    pending.Clear();
                    pending.Append(line);
                }

                depth = FormulaBracketDepth(pending.ToString());

                if (depth <= 0)
                {
                    depth = 0;
                    var joined = pending.ToString();
                    pending.Clear();
                    if (!string.IsNullOrWhiteSpace(joined))
                        lines.Add(joined.Trim());
                }
            }

            // Unterminated formula: emit what we have so validation reports the
            // real syntax error instead of the text silently vanishing.
            if (pending.Length > 0 && !string.IsNullOrWhiteSpace(pending.ToString()))
            {
                var joined = pending.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(joined)) lines.Add(joined);
            }

            return lines;
        }

        /// <summary>
        /// The opening part of the formula marker, built from the shared
        /// renderer so this parser never restates the rendering rules.
        /// </summary>
        private static string FormulaMarkerPrefix()
            => Helpers.AttributeDeclaration.FormulaMarkerPrefix;

        /// <summary>
        /// Net bracket depth of the formula region only. Square brackets inside
        /// a quoted description must not affect it.
        /// </summary>
        internal static int FormulaBracketDepth(string line)
        {
            // Assembled, not written literally: the marker belongs to
            // AttributeDeclaration's rendering rules. The search is
            // case-insensitive because GeneXus writes it as `formula` in some
            // exports and `Formula` in others.
            var marker = FormulaMarkerPrefix().ToLowerInvariant();
            int start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return 0;

            int depth = 0;
            bool inQuote = false;
            // Start at the marker itself: its opening bracket is at `start`, and
            // anything before it is a quoted description we must not count.
            for (int i = start; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') inQuote = !inQuote;
                if (inQuote) continue;
                if (c == '[') depth++;
                else if (c == ']') depth--;
            }
            // A closed formula nets to zero; anything negative is a stray closer,
            // which is not a reason to keep folding.
            return depth > 0 ? depth : 0;
        }

        /// <summary>
        /// DSL text to parsed nodes: fold wrapped attribute declarations, then
        /// parse.
        ///
        /// Extracted from <see cref="Parse"/> so the fold can be asserted
        /// without a Knowledge Base. Both halves are pure, which is what makes
        /// the reported failure testable here at all.
        /// </summary>
        internal static List<DslParserUtils.ParsedNode> ParseNodes(string text)
            => DslParserUtils.ParseLinesIntoNodes(SplitDslLines(text));

        /// <summary>
        /// Unwraps the synthetic root node produced when the whole DSL is one
        /// compound block, so callers see the level's own children.
        /// </summary>
        internal static List<DslParserUtils.ParsedNode> FlattenRoot(List<DslParserUtils.ParsedNode> parsedNodes)
        {
            if (parsedNodes != null && parsedNodes.Count == 1 && parsedNodes[0].IsCompound)
                return parsedNodes[0].Children;
            return parsedNodes ?? new List<DslParserUtils.ParsedNode>();
        }

        public void Parse(KBObject obj, string text)
        {
            if (obj is Transaction trn)
            {
                var targetNodes = FlattenRoot(ParseNodes(text));

                // Track globals created by this parse so a failed save can undo
                // them. Static only because the SDK objects are not shareable;
                // it is cleared immediately after use and the parser is used
                // single-threaded on the Worker's STA thread.
                CreatedGlobals.Clear();
                PublishCommittedGlobals(null);
                bool completed = false;
                try
                {
                    SyncTransactionNodes(trn.Structure.Root, targetNodes, obj.Model);

                    if (_removalFailures.Count > 0)
                        throw new StructureRemovalException(_removalFailures.AsReadOnly());

                    // issue #558 — a removal through the level's native Items storage
                    // (TryRemoveAttribute step 5) bypasses the SDK bookkeeping that flags
                    // the part as changed, so the caller's EnsureSave could skip it and
                    // the re-read would still show the attribute. Mark the part the way
                    // genexus_structure action=remove_attribute does before its save.
                    if (RemovedAttributeCount > 0)
                        trn.Structure.Dirty = true;
                    completed = true;
                }
                catch
                {
                    UndoCreatedGlobals();
                    throw;
                }
                finally
                {
                    // issue #592 — a parse that completed hands its globals over to the
                    // Transaction save, but that save is still verified afterwards and
                    // may be rolled back. Keep the identities (never the SDK objects)
                    // so the caller can undo the globals after a verified rollback.
                    if (completed)
                        PublishCommittedGlobals(CreatedGlobals.Select(c => new CreatedGlobalIdentity(c.Name, c.Guid)));
                    CreatedGlobals.Clear();
                }
            }
        }

        /// <summary>
        /// Identities of the globals created by the most recent parse that
        /// completed, until a caller takes them. Published by <see cref="Parse"/>
        /// and consumed once by the Structure writer; reset at the start of every
        /// parse so a stale hand-off can never describe another write.
        /// </summary>
        private static IReadOnlyList<CreatedGlobalIdentity> _committedGlobals = new CreatedGlobalIdentity[0];

        internal static void PublishCommittedGlobals(IEnumerable<CreatedGlobalIdentity> identities)
        {
            _committedGlobals = identities == null
                ? new CreatedGlobalIdentity[0]
                : identities.Where(i => i != null).ToList().AsReadOnly();
        }

        /// <summary>
        /// Returns the identities published by the last completed parse and
        /// clears them, so a second caller sees nothing to undo.
        /// </summary>
        internal static IReadOnlyList<CreatedGlobalIdentity> TakeCommittedGlobals()
        {
            var taken = _committedGlobals;
            _committedGlobals = new CreatedGlobalIdentity[0];
            return taken;
        }

        /// <summary>
        /// Deletes the global Attributes created by the current parse.
        ///
        /// Best effort: a global that cannot be deleted is logged rather than
        /// allowed to mask the original failure the caller is already reporting.
        /// </summary>
        internal static void UndoCreatedGlobals()
        {
            foreach (var created in CreatedGlobals)
            {
                try
                {
                    created.Attribute?.Delete();
                    Logger.Warn("[TransactionDslParser] removed orphaned global Attribute '" + created.Name + "' after the Transaction write failed.");
                }
                catch (Exception ex)
                {
                    Logger.Error("[TransactionDslParser] failed to remove orphaned global Attribute '" + created.Name + "': " + (ex.InnerException?.Message ?? ex.Message));
                }
            }
            CreatedGlobals.Clear();
        }

        /// <summary>
        /// The DSL type of a Transaction attribute: the Domain name when the attribute is
        /// based on one, so `Qty : MyDomain` reads back as written; otherwise its primitive type.
        /// </summary>
        internal static string RenderAttributeType(object attribute, string primitiveType)
        {
            string domain = GxMcp.Worker.Helpers.DomainPropertyApplier.GetDomainBasedOnName(attribute);
            return string.IsNullOrEmpty(domain) ? primitiveType : domain;
        }

        private void SerializeLevel(dynamic level, StringBuilder sb, int indent)
        {
            string indentStr = new string(' ', indent * 4);
            if (indent > 0)
            {
                sb.AppendLine($"{indentStr}{level.Name}");
                sb.AppendLine($"{indentStr}{{");
            }

            if (level.Attributes != null)
            {
                foreach (dynamic attr in level.Attributes)
                {
                    string keyMarker = attr.IsKey ? "*" : "";
                    string typeStr = "Unknown";
                    try {
                        if (attr.Type != null) typeStr = attr.Type.ToString();
                    } catch {
                        try { if (attr.Attribute != null && attr.Attribute.Type != null) typeStr = attr.Attribute.Type.ToString(); } catch { }
                    }

                    try {
                        if (attr.Attribute != null) {
                            int len = attr.Attribute.Length;
                            int dec = attr.Attribute.Decimals;
                            if (len > 0) {
                                if (dec > 0) typeStr += $"({len},{dec})";
                                else typeStr += $"({len})";
                            }
                        }
                    } catch { }

                    try { typeStr = RenderAttributeType((object)attr.Attribute, typeStr); } catch { }

                    string desc = "";
                    string formula = "";
                    bool isNullable = false;
                    try {
                        if (attr.Attribute != null) {
                            desc = attr.Attribute.Description?.ToString() ?? "";
                            formula = attr.Attribute.Formula?.ToString() ?? "";
                            dynamic pNullable = attr.Attribute.Properties.Get("Nullable");
                            if (pNullable != null) {
                                string nVal = pNullable.ToString();
                                isNullable = nVal.Equals("Yes", StringComparison.OrdinalIgnoreCase) || nVal.Equals("Nullable", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    } catch { }

                    var declaration = AttributeDeclaration.Render(attr.Name, keyMarker, typeStr, desc, formula, isNullable);
                    sb.AppendLine(string.Format("{0}{1}{2}{3}", indentStr, indent > 0 ? "    " : "", declaration.Head, declaration.Comment));
                }
            }

            if (level.Levels != null)
            {
                foreach (dynamic subLevel in level.Levels) SerializeLevel(subLevel, sb, indent + 1);
            }

            if (indent > 0) sb.AppendLine($"{indentStr}}}");
        }

        /// <summary>
        /// Global Attributes created during this sync, with the DSL text that
        /// produced each one.
        ///
        /// A new attribute needs its global <c>Attribute</c> object saved before
        /// the Transaction can reference it, but that save is not part of the
        /// Transaction's own transaction. When the Transaction save was then
        /// rejected, the global survived as an orphan: the write reported failure
        /// and the Knowledge Base still carried a stray attribute nobody asked
        /// for (issue #492, same family as #217).
        ///
        /// Recording what was created lets the caller undo it when the
        /// Transaction is not persisted.
        /// </summary>
        internal static readonly List<CreatedGlobalAttribute> CreatedGlobals =
            new List<CreatedGlobalAttribute>();

        internal sealed class CreatedGlobalAttribute
        {
            internal string Name;
            /// <summary>GUID the SDK assigned when the global was saved; Empty when unreadable.</summary>
            internal Guid Guid;
            internal Artech.Genexus.Common.Objects.Attribute Attribute;
        }

        /// <summary>
        /// Name plus GUID of a global Attribute created by a completed parse: the
        /// proof that a later rollback may act on it. The GUID, not the name, is
        /// what identifies the object again after the post-save verification; a
        /// homonym created meanwhile by someone else is not this write's global.
        /// </summary>
        internal sealed class CreatedGlobalIdentity
        {
            internal CreatedGlobalIdentity(string name, Guid guid)
            {
                Name = name;
                Guid = guid;
            }

            internal string Name { get; }
            internal Guid Guid { get; }
        }

        private void SyncTransactionNodes(dynamic sdkLevel, List<DslParserUtils.ParsedNode> parsedNodes, KBModel model)
        {
            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            if (sdkLevel.Attributes != null) { foreach (dynamic attr in sdkLevel.Attributes) existingItems[attr.Name] = attr; }

            foreach (var pNode in parsedNodes)
            {
                if (pNode.IsCompound)
                {
                    dynamic targetSubLevel = null;
                    if (sdkLevel.Levels != null) {
                        foreach (dynamic subLvl in sdkLevel.Levels) { if (subLvl.Name.Equals(pNode.Name, StringComparison.OrdinalIgnoreCase)) { targetSubLevel = subLvl; break; } }
                    }

                    if (targetSubLevel == null)
                    {
                        // Use the (TransactionLevel parent) ctor + typed AddLevel(level) so the SDK
                        // wires the new level into the structure with bookkeeping EnsureSave honors.
                        // The previous Activator+Levels.Add path silently dropped new sub-levels —
                        // same shape as the BC attribute regression fixed in 8c8f433.
                        try {
                            targetSubLevel = new TransactionLevel(sdkLevel);
                            targetSubLevel.Name = pNode.Name;
                            sdkLevel.AddLevel(targetSubLevel);
                        } catch (Exception lvlEx) {
                            Logger.Error("[TransactionDslParser] Failed to add sub-level '" + pNode.Name + "': " + (lvlEx.InnerException?.Message ?? lvlEx.Message));
                            targetSubLevel = null;
                        }
                    }
                    if (targetSubLevel != null) SyncTransactionNodes(targetSubLevel, pNode.Children, model);
                }
                else
                {
                    if (existingItems.TryGetValue(pNode.Name, out var existing))
                    {
                        existing.IsKey = pNode.IsKey;
                        // Update existing attribute's type if DSL specifies one.
                        ApplyTypeFromDsl(existing, pNode.TypeStr, model);
                        ApplyMetadataFromDsl(existing, pNode);
                    }
                    else
                    {
                        try {
                            // Resolve (or create) the global Attribute first — the SDK's typed
                            // sdkLevel.AddAttribute(globalAttr) is the only path that links the new
                            // TransactionAttribute into the structure with the bookkeeping that
                            // EnsureSave honors. The previous Activator+Attributes.Add path silently
                            // dropped new items because the SDK's TransactionAttribute proxy needs
                            // to be created by the level itself.
                            var globalAttr = Artech.Genexus.Common.Objects.Attribute.Get(model, pNode.Name);
                            bool createdGlobal = false;
                            if (globalAttr == null)
                            {
                                try
                                {
                                    var attrGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                                    var newAttr = KBObject.Create(model, attrGuid);
                                    newAttr.Name = pNode.Name;
                                    globalAttr = newAttr as Artech.Genexus.Common.Objects.Attribute;
                                    if (globalAttr != null)
                                    {
                                        ApplyTypeFromDsl(globalAttr, pNode.TypeStr, model);
                                        ApplyMetadataFromDsl(globalAttr, pNode);
                                        newAttr.Save();
                                        createdGlobal = true;
                                        Guid createdGuid = Guid.Empty;
                                        try { createdGuid = newAttr.Guid; }
                                        catch (Exception guidEx)
                                        {
                                            Logger.Warn("[TransactionDslParser] the GUID of the created global '" + pNode.Name + "' is unreadable; a rollback cannot identify it: " + guidEx.Message);
                                        }
                                        CreatedGlobals.Add(new CreatedGlobalAttribute
                                        {
                                            Name = pNode.Name,
                                            Guid = createdGuid,
                                            Attribute = globalAttr
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn("[TransactionDslParser] global Attribute create failed for '" + pNode.Name + "': " + ex.Message);
                                    globalAttr = null;
                                }
                            }

                            if (globalAttr == null)
                            {
                                Logger.Error("[TransactionDslParser] cannot add attribute '" + pNode.Name + "' — no global Attribute available; skipping.");
                                continue;
                            }

                            dynamic trnAttr = sdkLevel.AddAttribute(globalAttr);
                            try { trnAttr.IsKey = pNode.IsKey; } catch { }

                            // If the global already existed, still apply the DSL type so changes
                            // like TokenUser : Numeric(4) → TokenUser : UserLogin propagate.
                            if (!createdGlobal && !string.IsNullOrEmpty(pNode.TypeStr))
                            {
                                ApplyTypeFromDsl(globalAttr, pNode.TypeStr, model);
                            }
                            ApplyMetadataFromDsl(globalAttr, pNode);
                        } catch (Exception addEx) {
                            Logger.Error("[TransactionDslParser] Failed to add attribute '" + pNode.Name + "': " + (addEx.InnerException?.Message ?? addEx.Message));
                        }
                    }
                }
            }

            // issue #36.1 — removals run AFTER adds/updates so a key REPLACEMENT succeeds:
            // the new key already exists when the old one is dropped (the SDK refuses to
            // leave a transaction momentarily keyless). Failures are no longer swallowed —
            // a removal the SDK rejects (key still in use, referenced by an FK/relation/index)
            // is recorded and surfaced by Parse() as a hard error, instead of a silent merge
            // that leaves the attribute in place and reports success ("additive-only" bug).
            if (sdkLevel.Attributes != null)
            {
                var toRemove = new List<dynamic>();
                foreach (dynamic attr in sdkLevel.Attributes)
                {
                    if (!parsedNodes.Any(p => !p.IsCompound && p.Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)))
                        toRemove.Add(attr);
                }
                object attrsColl = sdkLevel.Attributes;
                foreach (dynamic dead in toRemove)
                {
                    string deadName = dead.Name;
                    // B8/B9: the previous `dynamic sdkLevel.Attributes.Remove(dead)` threw a
                    // C# RuntimeBinderException ("...Attribute does not contain a definition
                    // for 'Remove'") when the SDK collection exposes no such member — which
                    // was then mis-reported as "key in use". Probe the real removal API via
                    // reflection and classify the outcome accurately.
                    string reflError;
                    bool apiMissing;
                    bool removed = TryRemoveAttribute(attrsColl, (object)sdkLevel, (object)dead, deadName, out apiMissing, out reflError);

                    // Verify (a removal method can no-op without throwing when the SDK refuses).
                    bool stillPresent = false;
                    foreach (dynamic a in sdkLevel.Attributes)
                    {
                        if (((string)a.Name).Equals(deadName, StringComparison.OrdinalIgnoreCase)) { stillPresent = true; break; }
                    }
                    if (removed && !stillPresent) { RemovedAttributeCount++; continue; }

                    if (apiMissing)
                        _removalFailures.Add(deadName + " (SDK-API-UNSUPPORTED: this GeneXus SDK build exposes no attribute-removal method on the transaction structure — use genexus_structure action=remove_attribute or remove it in the GeneXus IDE)");
                    else if (!string.IsNullOrEmpty(reflError))
                        _removalFailures.Add(deadName + " (" + reflError + "; try genexus_structure action=remove_attribute)");
                    else
                        _removalFailures.Add(deadName + " (SDK kept the attribute — it is likely a key in use, or referenced by a relation/index; try genexus_structure action=remove_attribute)");
                }
            }
        }

        // B8: probe the SDK collection for a working attribute-removal API instead of a
        // blind `dynamic .Remove(dead)` (which throws RuntimeBinderException — a missing
        // member — when no such method exists, and was mis-reported as "key in use").
        // Tries, in order: collection.Remove(item), collection.Remove(name),
        // collection.RemoveAt(index), item.Remove()/Delete(), and native sdkLevel.Items m_Inner.
        // Sets apiMissing=true only when NONE of these members exist on the types involved.
        // Internal so the Items path can be asserted with fakes (issue #558).
        internal static bool TryRemoveAttribute(object attrsColl, object sdkLevel, object dead, string deadName,
            out bool apiMissing, out string error)
        {
            apiMissing = false;
            error = null;
            if (attrsColl == null && sdkLevel == null) { apiMissing = true; return false; }
            bool anyMemberFound = false;

            if (attrsColl != null)
            {
                var collType = attrsColl.GetType();

                // 1) collection.Remove(item)  2) collection.Remove(string)
                foreach (var m in collType.GetMethods().Where(mi => mi.Name == "Remove"))
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    object arg = null;
                    if (ps[0].ParameterType.IsInstanceOfType(dead)) arg = dead;
                    else if (ps[0].ParameterType == typeof(string)) arg = deadName;
                    else continue;
                    anyMemberFound = true;
                    try { m.Invoke(attrsColl, new[] { arg }); return true; }
                    catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
                }

                // 3) collection.RemoveAt(index)
                var removeAt = collType.GetMethod("RemoveAt", new[] { typeof(int) });
                if (removeAt != null)
                {
                    anyMemberFound = true;
                    try
                    {
                        int idx = 0, found = -1;
                        foreach (dynamic a in (System.Collections.IEnumerable)attrsColl)
                        {
                            if (((string)a.Name).Equals(deadName, StringComparison.OrdinalIgnoreCase)) { found = idx; break; }
                            idx++;
                        }
                        if (found >= 0) { removeAt.Invoke(attrsColl, new object[] { found }); return true; }
                    }
                    catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
                }
            }

            // 4) item.Remove() / item.Delete()
            if (dead != null)
            {
                foreach (var name in new[] { "Remove", "Delete" })
                {
                    var im = dead.GetType().GetMethod(name, Type.EmptyTypes);
                    if (im == null) continue;
                    anyMemberFound = true;
                    try { im.Invoke(dead, null); return true; }
                    catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
                }
            }

            // 5) sdkLevel.Items native storage (m_Inner IList removal)
            if (sdkLevel != null)
            {
                try
                {
                    var itemsProp = sdkLevel.GetType().GetProperty("Items",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var items = itemsProp?.GetValue(sdkLevel, null);
                    if (items != null)
                    {
                        var innerField = SdkReflection.FindField(items.GetType(), "m_Inner");
                        var inner = innerField?.GetValue(items) as System.Collections.IList;
                        if (inner != null)
                        {
                            anyMemberFound = true;
                            int itemIndex = -1;
                            for (int i = 0; i < inner.Count; i++)
                            {
                                if (object.ReferenceEquals(inner[i], dead)) { itemIndex = i; break; }
                            }
                            if (itemIndex < 0 && dead != null)
                            {
                                // Items also holds the sub-levels, and a TransactionLevel has a
                                // Name too: only an item of the dead attribute's own kind may
                                // match by name, or a homonym level would be dropped instead.
                                var deadType = dead.GetType();
                                for (int i = 0; i < inner.Count; i++)
                                {
                                    var item = inner[i];
                                    if (item != null && deadType.IsInstanceOfType(item))
                                    {
                                        var nameProp = item.GetType().GetProperty("Name");
                                        var itemName = nameProp?.GetValue(item, null)?.ToString();
                                        if (string.Equals(itemName, deadName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            itemIndex = i;
                                            break;
                                        }
                                    }
                                }
                            }
                            if (itemIndex >= 0)
                            {
                                inner.RemoveAt(itemIndex);
                                var dirtyProp = items.GetType().GetProperty("Dirty",
                                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                dirtyProp?.SetValue(items, true, null);
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = (ex.InnerException ?? ex).Message;
                }
            }

            if (!anyMemberFound) apiMissing = true;
            return false;
        }

        private static void ApplyTypeFromDsl(dynamic trnAttrOrAttribute, string typeStr, KBModel model)
        {
            GxMcp.Worker.Helpers.AttributeTypeApplier.ApplyFromDslType(trnAttrOrAttribute, typeStr, model);
        }

        private static void ApplyMetadataFromDsl(object trnAttrOrAttribute, DslParserUtils.ParsedNode node)
        {
            if (trnAttrOrAttribute == null || node == null) return;
            object globalAttr = trnAttrOrAttribute;
            try
            {
                var property = trnAttrOrAttribute.GetType().GetProperty("Attribute");
                var nested = property?.GetValue(trnAttrOrAttribute, null);
                if (nested != null) globalAttr = nested;
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(node.Description))
                TrySetStringProperty(globalAttr, "Description", node.Description);
            if (node.Formula != null)
            {
                var attribute = globalAttr as Artech.Genexus.Common.Objects.Attribute;
                if (attribute == null) throw new InvalidOperationException("DSL formula target is not a global Attribute.");
                attribute.Formula = Formula.Parse(node.Formula, attribute, null);
            }
        }

        private static void TrySetStringProperty(object target, string name, string value)
        {
            try
            {
                var property = target?.GetType().GetProperty(name);
                if (property?.CanWrite == true) property.SetValue(target, value, null);
            }
            catch (Exception ex)
            {
                Logger.Warn("[TransactionDslParser] Could not apply " + name + " metadata: " + ex.Message);
            }
        }
    }
}
