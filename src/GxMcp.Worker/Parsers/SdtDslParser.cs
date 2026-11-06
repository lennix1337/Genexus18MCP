using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using System.Reflection;

namespace GxMcp.Worker.Parsers
{
    public class SdtDslParser : IDslParser
    {
        private static readonly Guid SDT_STRUCTURE_PART = Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");

        // Set in Parse() so SyncSDTNodes can resolve SDT-typed members (issue #33). The root
        // SDTLevel exposes no Model property, so we can't reach it from the node itself.
        private KBModel _model;

        public void Serialize(KBObject obj, StringBuilder sb)
        {
            if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
            {
                try { _model = obj.Model; } catch { _model = null; }
                KBObject sdt = obj;
                KBObjectPart structure = null;
                
                // Find structure part: match by descriptor name, class name, or GUID
                foreach (KBObjectPart part in sdt.Parts)
                {
                    try {
                        string descName = part.TypeDescriptor?.Name ?? "";
                        string className = part.GetType().Name;
                        if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                            className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            part.Type == SDT_STRUCTURE_PART)
                        { 
                            structure = part; 
                            break; 
                        }
                    } catch { }
                }
                
                // Fallback: duck typing - any part with Root
                if (structure == null)
                {
                    foreach (KBObjectPart part in sdt.Parts)
                    {
                        try {
                            dynamic dp = part;
                            if (dp.Root != null)
                            { structure = part; break; }
                        } catch { }
                    }
                }

                if (structure != null)
                {
                    dynamic ds = structure;
                    // DEEP DISCOVERY
                    try {
                        var partProps = new List<string>();
                        foreach (var p in structure.GetType().GetProperties()) partProps.Add(p.Name);
                        Logger.Debug($"[SDT PART DISCOVERY] Type: {structure.GetType().FullName} | Props: {string.Join(",", partProps)}");
                    } catch { }

                    // Try to find the root level (Root or StructureRoot)
                    dynamic root = null;
                    try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }

                    if (root != null)
                    {
                        // Try all known collections for SDT levels
                        try { foreach (dynamic child in root.Items) SerializeLevel(child, sb, 0); } 
                        catch {
                            try { foreach (dynamic child in root.Children) SerializeLevel(child, sb, 0); }
                            catch {
                                try { foreach (dynamic child in root.InternalItems) SerializeLevel(child, sb, 0); }
                                catch { }
                            }
                        }
                    }
                }
                else
                {
                    Logger.Error($"SdtDslParser: Could not find structure part for SDT {obj.Name}");
                }
            }
        }

        public void Parse(KBObject obj, string text)
        {
            if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
            {
                KBObjectPart structure = null;
                foreach (KBObjectPart p in obj.Parts)
                {
                    try
                    {
                        string descName = p.TypeDescriptor?.Name ?? "";
                        string className = p.GetType().Name;
                        if (p.Type == SDT_STRUCTURE_PART ||
                            descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                            className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            structure = p;
                            break;
                        }
                    }
                    catch { }
                }

                if (structure == null)
                {
                    foreach (KBObjectPart p in obj.Parts)
                    {
                        try
                        {
                            dynamic dp = p;
                            if (dp.Root != null) { structure = p; break; }
                        }
                        catch { }
                    }
                }

                if (structure == null)
                {
                    Logger.Error("[SDT PARSE] Structure part not found for " + obj.Name);
                    return;
                }

                // Diagnostic: log the first item's full property set so we can see what's missing
                try
                {
                    dynamic dsDbg = structure;
                    dynamic rootDbg = null;
                    try { rootDbg = dsDbg.Root; } catch { try { rootDbg = dsDbg.StructureRoot; } catch { } }
                    if (rootDbg != null)
                    {
                        foreach (dynamic item in rootDbg.Items)
                        {
                            try
                            {
                                var itemType = ((object)item).GetType();
                                var dump = new System.Text.StringBuilder();
                                foreach (var prop in itemType.GetProperties())
                                {
                                    try { object val = prop.GetValue(item); if (val != null) dump.Append(prop.Name + "=" + val + "; "); } catch { }
                                }
                                Logger.Info("[SDT ITEM DUMP] " + obj.Name + "/" + item.Name + ": " + dump.ToString());
                            }
                            catch { }
                            break;
                        }
                    }
                }
                catch { }

                Logger.Info("[SDT PARSE] Begin parse for " + obj.Name + " using part " + structure.GetType().Name);
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToList();
                var parsedNodes = DslParserUtils.ParseLinesIntoNodes(lines);
                try { _model = obj.Model; } catch { _model = null; }
                dynamic ds = structure;
                dynamic root = null;
                try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }
                if (root == null) { Logger.Error("[SDT PARSE] Root not found for " + obj.Name); return; }

                int preItemCount = CountItemsSafe(root);
                SyncSDTNodes(root, parsedNodes);
                int postItemCount = CountItemsSafe(root);
                Logger.Info($"[SDT PARSE] {obj.Name}: items {preItemCount} -> {postItemCount} (requested {parsedNodes.Count})");

                // Friction-report 05-13 #2: even when AddItem/AddLevel succeed on the in-memory
                // Items collection, the SDTStructurePart may not flag itself dirty, so the
                // subsequent obj.Save() persists the OLD serialized XML (with only the seed).
                // External reads — including the validator running when a Procedure consumes the
                // SDT — then resolve fields against the stale persisted version and reject the
                // member access (`src0216: 'AluCod' propriedade inválida.`).
                //
                // Force the part dirty via every avenue the SDK exposes so the next Save round
                // writes the live items. Each branch is defensive — if a property/method doesn't
                // exist we just keep going.
                MarkPartDirty(structure, obj.Name);
            }
        }

        private static int CountItemsSafe(dynamic root)
        {
            int n = 0;
            try { foreach (var _ in root.Items) n++; } catch { }
            return n;
        }

        internal static void MarkPartDirty(object part, string objName)
        {
            if (part == null) return;
            // 1) part.Dirty = true (most SDK parts expose this directly)
            TryWriteBool(part, "Dirty", true, objName);
            TryWriteBool(part, "IsDirty", true, objName);

            // 2) Invoke "Modified()" / "Touch()" / "MarkDirty()" if present
            foreach (var name in new[] { "Touch", "Modified", "MarkDirty", "OnChanged", "NotifyChanged" })
            {
                try
                {
                    var mi = part.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (mi != null) { mi.Invoke(part, null); Logger.Debug($"[SDT PARSE] {objName}: {name}() invoked."); }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[SDT PARSE] {objName}: {name}() threw: {ex.Message}");
                }
            }

            // 3) Some SDK parts track a private DirtyProperties bag — clearing it forces re-serialize
            try
            {
                var dp = part.GetType().GetProperty("DirtyProperties", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (dp != null)
                {
                    var bag = dp.GetValue(part);
                    if (bag is System.Collections.ICollection c && c.Count == 0)
                    {
                        // Touch Description/Mode/... to seed a dirty property if the bag is empty.
                        try { ((dynamic)part).Mode = ((dynamic)part).Mode; } catch { }
                    }
                }
            }
            catch { }
        }

        private static void TryWriteBool(object target, string propName, bool value, string objName)
        {
            try
            {
                var p = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                {
                    p.SetValue(target, value);
                    Logger.Debug($"[SDT PARSE] {objName}: {propName}=true via reflection.");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[SDT PARSE] {objName}: set {propName} threw: {ex.Message}");
            }
        }

        private void SerializeLevel(dynamic level, StringBuilder sb, int indent)
        {
            string indentStr = new string(' ', indent * 4);
            string collectionMarker = "";
            try { collectionMarker = level.IsCollection ? " Collection" : ""; } catch { }
            
            // Default to leaf=false when we can probe the Items collection: an item with
            // children must serialize as compound. Falling back to leaf=true silently flattens
            // nested structures when the SDK doesn't expose IsLeafItem on this build.
            bool isLeaf;
            try { isLeaf = level.IsLeafItem; }
            catch
            {
                bool hasChildren = false;
                try { foreach (var _ in level.Items) { hasChildren = true; break; } } catch { }
                isLeaf = !hasChildren;
            }
            
            if (!isLeaf)
            {
                sb.AppendLine($"{indentStr}{level.Name}{collectionMarker}");
                sb.AppendLine($"{indentStr}{{");
                try { foreach (dynamic child in level.Items) SerializeLevel(child, sb, indent + 1); } catch { }
                sb.AppendLine($"{indentStr}}}");
            }
            else
            {
                string typeStr = "Unknown";
                try { typeStr = level.Type != null ? level.Type.ToString() : "Unknown"; } catch { }

                // issue #33: an SDT-typed member serializes as the referenced SDT name (so the
                // reader round-trips it back to the same SDT reference) instead of the bare
                // "GX_SDT" enum, which would lose the reference on re-write.
                if (typeStr.StartsWith("GX_SDT", StringComparison.Ordinal))
                {
                    string sdtName = ResolveSdtMemberName(level);
                    if (!string.IsNullOrEmpty(sdtName))
                    {
                        sb.AppendLine($"{indentStr}{level.Name} : {sdtName}{collectionMarker}");
                        return;
                    }
                }

                // issue #109: surface Attribute-based member names (parity with IDE Attribute:<Name> picker)
                string attrName = GxMcp.Worker.Helpers.DomainPropertyApplier.GetAttributeBasedOnName((object)level);
                if (!string.IsNullOrEmpty(attrName))
                {
                    sb.AppendLine($"{indentStr}{level.Name} : Attribute:{attrName}{collectionMarker}");
                    return;
                }

                // issue #51: surface Domain-based member names so domain typing survives text reads
                string domName = GxMcp.Worker.Helpers.DomainPropertyApplier.GetDomainBasedOnName((object)level);
                if (!string.IsNullOrEmpty(domName))
                {
                    sb.AppendLine($"{indentStr}{level.Name} : {domName}{collectionMarker}");
                    return;
                }

                // issue #31.1: surface Length/Decimals so the reader can see (and round-trip)
                // the element size, e.g. "Numeric(9)" / "Numeric(9,2)". Without this a numeric
                // element read back as bare "NUMERIC" (default Numeric(4) → xsd:short, silent
                // truncation past 32767). Comma form matches the Transaction structure DSL.
                try
                {
                    int len = 0, dec = 0;
                    try { len = (int)level.Length; } catch { }
                    try { dec = (int)level.Decimals; } catch { }
                    if (len > 0) typeStr += dec > 0 ? $"({len},{dec})" : $"({len})";
                }
                catch { }

                sb.AppendLine($"{indentStr}{level.Name} : {typeStr}{collectionMarker}");
            }
        }

        // Set an int property (Length/Decimals) on an SDT item via reflection, resolving the
        // Artech SDK's shadowed-property ambiguity the same way AttributeTypeApplier does.
        private static void SetIntProperty(object target, string propName, int value)
        {
            if (target == null) return;
            try
            {
                var p = GxMcp.Worker.Helpers.AttributeTypeApplier.GetPropertyUnambiguous(target.GetType(), propName);
                if (p != null && p.CanWrite) p.SetValue(target, value, null);
            }
            catch (Exception ex) { Logger.Debug("[SDT PARSE] SetIntProperty " + propName + " failed: " + ex.Message); }
        }

        // A type token that maps to a GeneXus primitive (or the raw GX_SDT enum a prior read
        // emitted). Anything else may be an SDT reference and warrants a KB lookup (issue #33).
        private static bool LooksLikePrimitive(string typeStr)
        {
            if (string.IsNullOrWhiteSpace(typeStr)) return true;
            string[] prims = { "Numeric", "Char", "VarChar", "Varchar", "LongVarchar", "Date", "DateTime",
                               "Bool", "Boolean", "Blob", "Binary", "Image", "Bitmap", "Audio", "Video",
                               "GUID", "Geography", "GX_SDT", "GX_BUSCOMP", "GX_USRDEFTYP" };
            foreach (var p in prims)
                if (typeStr.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Read-side: recover the referenced SDT's name from a GX_SDT member's ATTCUSTOMTYPE.
        // Persisted form is a StructureTypeReference XML whose <Type> is the target SDT's guid;
        // freshly-written (pre-save) items may instead carry the SDT name directly. Returns null
        // when it can't resolve (caller falls back to the raw enum).
        private string ResolveSdtMemberName(dynamic level)
        {
            if (_model == null) return null;
            try
            {
                object item = (object)level;
                object ie = item.GetType().GetProperty("ItemEntity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(item)
                         ?? item.GetType().GetProperty("SDTItemEntity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(item);
                if (ie == null) return null;
                object ct = ie.GetType().GetProperty("CustomType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ie);
                if (ct == null) return null;
                string guid = ct.GetType().GetField("m_guid", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ct) as string;
                if (string.IsNullOrEmpty(guid)) return null;

                // Persisted StructureTypeReference is an EntityKey pair:
                //   <Type> = the SDT class guid (a constant, not the target), <Id> = the target
                //   SDT object's numeric id. Resolve it via model.Objects.Get(EntityKey).
                // issue #543: the pair is parsed by the shared order-tolerant helper.
                if (GxMcp.Worker.Helpers.CustomTypeReferenceParser.TryParse(guid, out var classGuid, out var objId))
                {
                    try
                    {
                        var ek = new global::Artech.Udm.Framework.EntityKey(classGuid, objId);
                        var o = _model.Objects.Get(ek);
                        if (o != null) return o.Name;
                    }
                    catch { }
                }
                // Pre-save form: the plain SDT name (or an object name we can confirm).
                if (guid.IndexOf('<') < 0)
                {
                    var obj = ResolveSdtType(_model, guid);
                    return obj?.Name ?? guid;
                }
            }
            catch { }
            return null;
        }

        // Resolve a member type token to an SDT KBObject, or null when it isn't one.
        private static KBObject ResolveSdtType(KBModel model, string typeStr)
        {
            try
            {
                var obj = GxMcp.Worker.Helpers.VariableInjector.ResolveTypeObject(model, typeStr.Trim());
                if (obj != null && obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                    return obj;
            }
            catch { }
            return null;
        }

        // The primitive type a Domain-typed member is created with before it is bound to
        // the Domain. A new member needs one: AddItem takes an eDBType, and a Domain name is
        // not an eDBType member, so without this the member was never added.
        internal static object DomainDbType(Type eDBTypeT, object domain)
        {
            if (eDBTypeT == null) return null;
            // Domain.Type hides KBObject.Type (a Guid) with `new`, so look the property up by
            // its eDBType type: GetProperty("Type") is ambiguous on a real Domain.
            foreach (string property in new[] { "DataType", "Type" })
            {
                var match = domain.GetType().GetProperties()
                    .FirstOrDefault(p => p.Name == property && p.PropertyType == eDBTypeT && p.GetIndexParameters().Length == 0);
                if (match == null) continue;
                try
                {
                    object value = match.GetValue(domain);
                    if (value != null) return value;
                }
                catch { }
            }
            return Enum.Parse(eDBTypeT, "VARCHAR");
        }

        private static object ResolveDbType(Type eDBTypeT, string typeStr)
        {
            if (eDBTypeT == null) return null;
            string name;
            if (string.IsNullOrEmpty(typeStr)) name = "VARCHAR";
            else if (typeStr.StartsWith("Numeric", StringComparison.OrdinalIgnoreCase)) name = "NUMERIC";
            else if (typeStr.StartsWith("Char", StringComparison.OrdinalIgnoreCase)) name = "CHARACTER";
            else if (typeStr.StartsWith("Varchar", StringComparison.OrdinalIgnoreCase)) name = "VARCHAR";
            else if (typeStr.StartsWith("Date", StringComparison.OrdinalIgnoreCase)) name = "DATE";
            else if (typeStr.StartsWith("Bool", StringComparison.OrdinalIgnoreCase)) name = "Boolean";
            else if (typeStr.StartsWith("LongVarchar", StringComparison.OrdinalIgnoreCase)) name = "LONGVARCHAR";
            // issue #36.2 — eDBType has no BLOB/IMAGE member: blob-family = BINARY, image = BITMAP.
            // Mirror VariableInjector's variable-path table so an SDT member typed Blob/Binary
            // persists correctly instead of silently coercing to VARCHAR (the old fallback below).
            else if (typeStr.StartsWith("Blob", StringComparison.OrdinalIgnoreCase)
                  || typeStr.StartsWith("Binary", StringComparison.OrdinalIgnoreCase)
                  || typeStr.StartsWith("Audio", StringComparison.OrdinalIgnoreCase)
                  || typeStr.StartsWith("Video", StringComparison.OrdinalIgnoreCase)) name = "BINARY";
            else if (typeStr.StartsWith("Image", StringComparison.OrdinalIgnoreCase)
                  || typeStr.StartsWith("Bitmap", StringComparison.OrdinalIgnoreCase)) name = "BITMAP";
            else name = typeStr.ToUpperInvariant();
            // issue #36.2 — no silent VARCHAR coercion: an unknown type token returns null so the
            // member fails loudly (SyncSDTNodes logs + does not add it) instead of persisting as
            // the wrong type and reporting success.
            try { return Enum.Parse(eDBTypeT, name, true); }
            catch { return null; }
        }

        private void SyncSDTNodes(dynamic node, List<DslParserUtils.ParsedNode> parsedNodes)
        {
            // REFLECTION DISCOVERY (Log once per session)
            try {
                var props = new List<string>();
                foreach (PropertyInfo p in node.GetType().GetProperties()) props.Add(p.Name);
                var methods = new List<string>();
                foreach (MethodInfo m in node.GetType().GetMethods()) methods.Add(m.Name);
                Logger.Debug($"[SDT DISCOVERY] Node: {node.GetType().FullName} | Props: {string.Join(",", props)} | Methods: {string.Join(",", methods)}");
            } catch { }

            // Try to find the collection of items (Items or Children or Levels or Elements)
            dynamic items = null;
            try { items = node.Items; } catch { try { items = node.Children; } catch { try { items = node.Levels; } catch { try { items = node.Elements; } catch { } } } }
            if (items == null) return;

            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic child in items) { existingItems[child.Name] = child; }

            var toRemove = new List<dynamic>();
            foreach (dynamic child in items) {
                if (!parsedNodes.Any(p => p.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase))) toRemove.Add(child);
            }
            foreach (dynamic dead in toRemove) { try { items.Remove(dead); } catch {} }

            foreach (var pNode in parsedNodes)
            {
                // issue #33, #51 & #109: resolve SDT, Domain, or Attribute references
                KBObject sdtMemberObj = null;
                KBObject domainMemberObj = null;
                KBObject attributeMemberObj = null;
                if (!pNode.IsCompound && _model != null)
                {
                    if (pNode.TypeStr != null && pNode.TypeStr.StartsWith("Attribute:", StringComparison.OrdinalIgnoreCase))
                    {
                        string rawAttrName = pNode.TypeStr.Substring("Attribute:".Length).Trim();
                        attributeMemberObj = VariableInjector.FindAttribute(_model, rawAttrName);
                    }
                    else if (!LooksLikePrimitive(pNode.TypeStr))
                    {
                        var resolved = VariableInjector.ResolveTypeObject(_model, pNode.TypeStr);
                        if (resolved != null)
                        {
                            if (resolved.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)) sdtMemberObj = resolved;
                            else if (resolved is Artech.Genexus.Common.Objects.Domain || resolved.TypeDescriptor.Name.Equals("Domain", StringComparison.OrdinalIgnoreCase)) domainMemberObj = resolved;
                        }
                    }
                }

                dynamic targetChild = null;
                if (existingItems.TryGetValue(pNode.Name, out var existing)) targetChild = existing;
                else
                {
                    // Discovery for SDTItem/SDTLevel type
                    Type sdtItemType = null;
                    string[] namespaces = { "Artech.Genexus.Common.Parts", "Artech.Genexus.Common.Objects", "Artech.Genexus.Common" };
                    foreach (var ns in namespaces) {
                        sdtItemType = node.GetType().Assembly.GetType($"{ns}.SDTItem") ?? 
                                      node.GetType().Assembly.GetType($"{ns}.SDTLevel");
                        if (sdtItemType != null) break;
                    }

                    bool added = false;
                    try
                    {
                        Type nodeType = ((object)node).GetType();
                        if (pNode.IsCompound)
                        {
                            MethodInfo addLevel = nodeType.GetMethod("AddLevel", new Type[] { typeof(string) })
                                                ?? nodeType.GetMethods().FirstOrDefault(m => m.Name == "AddLevel" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                            if (addLevel != null)
                            {
                                targetChild = addLevel.Invoke(node, new object[] { pNode.Name });
                                added = (targetChild != null);
                            }
                        }
                        else
                        {
                            Type eDBTypeT = nodeType.Assembly.GetType("Artech.Genexus.Common.eDBType");
                            object dbType;
                            if (sdtMemberObj != null && eDBTypeT != null) dbType = Enum.Parse(eDBTypeT, "GX_SDT");
                            else if (attributeMemberObj != null && eDBTypeT != null)
                            {
                                try { dbType = ((dynamic)attributeMemberObj).Type; }
                                catch { dbType = Enum.Parse(eDBTypeT, "VARCHAR"); }
                            }
                            else if (domainMemberObj != null) dbType = DomainDbType(eDBTypeT, domainMemberObj);
                            else dbType = ResolveDbType(eDBTypeT, pNode.TypeStr);

                            MethodInfo addItem = nodeType.GetMethod("AddItem", new Type[] { typeof(string), eDBTypeT });
                            if (addItem != null && eDBTypeT != null && dbType != null)
                            {
                                targetChild = addItem.Invoke(node, new object[] { pNode.Name, dbType });
                                added = (targetChild != null);
                            }
                            else
                            {
                                var sigs = string.Join("; ", nodeType.GetMethods().Where(m => m.Name == "AddItem").Select(m => "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
                                Logger.Error("[SDT PARSE] AddItem(string, eDBType) not invokable. eDBType=" + (eDBTypeT?.FullName ?? "null") + " resolved=" + (dbType?.ToString() ?? "null") + " Sigs=[" + sigs + "]");
                            }
                        }
                    }
                    catch (Exception ex) { Logger.Error("[SDT PARSE] AddItem/AddLevel('" + pNode.Name + "') failed: " + (ex.InnerException?.Message ?? ex.Message)); }

                    // Note: a previous fallback tried Activator.CreateInstance(SDTItem, ...) + items.Add.
                    // That path is known to silently produce items the SDK rejects on Save (proved by
                    // the original ObjectService.InitializeSDTWithDefaultItem bug). We deliberately
                    // do NOT fall back to it — if AddItem/AddLevel didn't work we fail loudly so the
                    // caller can see the real problem instead of a half-broken SDT.
                    if (!added)
                    {
                        Logger.Error("[SDT PARSE] Could not add item '" + pNode.Name + "' via AddItem/AddLevel (node type=" + ((object)node).GetType().FullName + "). sdtItemType=" + (sdtItemType?.FullName ?? "null"));
                    }
                }

                if (targetChild != null)
                {
                    try { targetChild.IsCollection = pNode.IsCollection; } catch { }
                    if (pNode.IsCompound) SyncSDTNodes(targetChild, pNode.Children);
                    else if (sdtMemberObj != null)
                    {
                        DomainPropertyApplier.ClearAttributeBasedOn((object)targetChild);
                        DomainPropertyApplier.ClearDomainBasedOn((object)targetChild);
                        // issue #33: type the member as a reference to the SDT (GX_SDT +
                        // ATTCUSTOMTYPE). IsCollection above already carries the "Collection" marker.
                        if (!VariableInjector.BindSdtItemToSdt((object)targetChild, sdtMemberObj))
                            Logger.Error($"[SDT PARSE] Failed to bind member '{pNode.Name}' to SDT '{sdtMemberObj.Name}'");
                    }
                    else if (attributeMemberObj != null)
                    {
                        DomainPropertyApplier.ClearDomainBasedOn((object)targetChild);
                        try {
                            var setProp = targetChild.GetType().GetMethod("SetPropertyValue", new[] { typeof(string), typeof(object) });
                            setProp?.Invoke(targetChild, new object[] { "ATTCUSTOMTYPE", null });
                        } catch { }
                        try {
                            Type eDBTypeT = targetChild.GetType().Assembly.GetType("Artech.Genexus.Common.eDBType");
                            object dbType;
                            try { dbType = ((dynamic)attributeMemberObj).Type; }
                            catch { dbType = Enum.Parse(eDBTypeT, "VARCHAR"); }
                            GxMcp.Worker.Helpers.AttributeTypeApplier.SetMemberType((object)targetChild, dbType);
                        } catch { }
                        // issue #109: bind attribute reference to SDT member
                        DomainPropertyApplier.ApplyAttributeBasedOn((object)targetChild, attributeMemberObj);
                    }
                    else if (domainMemberObj != null)
                    {
                        DomainPropertyApplier.ClearAttributeBasedOn((object)targetChild);
                        try {
                            var setProp = targetChild.GetType().GetMethod("SetPropertyValue", new[] { typeof(string), typeof(object) });
                            setProp?.Invoke(targetChild, new object[] { "ATTCUSTOMTYPE", null });
                        } catch { }
                        try {
                            Type eDBTypeT = targetChild.GetType().Assembly.GetType("Artech.Genexus.Common.eDBType");
                            GxMcp.Worker.Helpers.AttributeTypeApplier.SetMemberType((object)targetChild, DomainDbType(eDBTypeT, domainMemberObj));
                        } catch { }
                        // issue #51: bind domain reference to SDT member
                        DomainPropertyApplier.ApplyDomainBasedOn((object)targetChild, domainMemberObj);
                    }
                    else
                    {
                        DomainPropertyApplier.ClearAttributeBasedOn((object)targetChild);
                        DomainPropertyApplier.ClearDomainBasedOn((object)targetChild);
                        try {
                            var setProp = targetChild.GetType().GetMethod("SetPropertyValue", new[] { typeof(string), typeof(object) });
                            setProp?.Invoke(targetChild, new object[] { "ATTCUSTOMTYPE", null });
                        } catch { }
                        try {
                            Type eDBType = targetChild.GetType().Assembly.GetType("Artech.Genexus.Common.eDBType");
                            var resolvedDb = ResolveDbType(eDBType, pNode.TypeStr);
                            GxMcp.Worker.Helpers.AttributeTypeApplier.SetMemberType((object)targetChild, resolvedDb);
                        } catch { }

                        // issue #31.1: apply Length/Decimals when the type carries them
                        // (e.g. "Numeric(9)" / "Numeric(9.0)"). Previously dropped, so a numeric
                        // element stayed at the Numeric(4) default (xsd:short) and truncated
                        // values > 32767. Best-effort: SDT items without these props are skipped.
                        try
                        {
                            var spec = GxMcp.Worker.Helpers.AttributeTypeApplier.Parse(pNode.TypeStr);
                            if (spec.Length.HasValue) SetIntProperty((object)targetChild, "Length", spec.Length.Value);
                            if (spec.Decimals.HasValue) SetIntProperty((object)targetChild, "Decimals", spec.Decimals.Value);
                        }
                        catch { }
                    }
                }
            }
        }
    }
}
