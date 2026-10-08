using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class SDTService
    {
        private readonly ObjectService _objectService;

        public SDTService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetSDTStructure(string sdtName)
        {
            try
            {
                var seed = _objectService.FindObject(sdtName, "SDT");
                if (seed == null) return HealingService.FormatNotFoundError(sdtName, _objectService.GetKbService().GetIndexCache().GetIndex());
                var obj = _objectService.FindObjectFreshByIdentity(seed);
                if (obj == null) return Models.McpResponse.Err(code: "FreshReadUnavailable",
                    message: "A fresh SDT read could not be confirmed.", target: sdtName);
                return ReadStructure(obj).ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("SDTService Error: " + ex.Message);
                return Models.McpResponse.Err(code: "StructureReadFailed", message: ex.Message, target: sdtName);
            }
        }

        internal JObject ReadStructure(KBObject obj, bool includeVersionToken = true)
        {
                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    dynamic sdt = obj;
                    var result = new JObject();
                    result["name"] = sdt.Name;
                    result["type"] = "SDT";

                    var children = new JArray();
                    dynamic structure = FindStructurePart(sdt);
                    dynamic root = null;
                    try { root = structure?.Root; } catch { }
                    if (root == null) throw new InvalidOperationException("The SDT structure root could not be read.");

                    // issue #47: the top-level "Collection" flag lives on the structure ROOT level,
                    // not on the SDT KBObject (sdt.IsCollection reads false there), which made a
                    // collection SDT report isCollection=false + a flat structure. Read the root
                    // flag first and surface the collection item name the IDE shows.
                    bool rootIsCollection = false;
                    try { rootIsCollection = (bool)root.IsCollection; } catch { }
                    if (!rootIsCollection) { try { rootIsCollection = (bool)sdt.IsCollection; } catch { } }
                    result["isCollection"] = rootIsCollection;
                    if (rootIsCollection)
                    {
                        string itemName = null;
                        try { itemName = (string)root.CollectionItemName; } catch { }
                        if (string.IsNullOrEmpty(itemName)) { try { itemName = (string)sdt.CollectionItemName; } catch { } }
                        if (!string.IsNullOrEmpty(itemName)) result["collectionItemName"] = itemName;
                    }

                    Artech.Architecture.Common.Objects.KBModel model = null;
                    try { model = obj.Model; } catch { }

                    foreach (dynamic child in root.Items)
                    {
                        children.Add(MapLevelToResult(child, model));
                    }
                    result["children"] = children;
                    if (includeVersionToken) result["versionToken"] = WriteService.ComputeVersionToken(obj);
                    return result;
                }
                throw new ArgumentException("Object is not an SDT.", nameof(obj));
        }

        // issue #52: author an SDT's structure through genexus_structure action=update_visual.
        // The textual structure DSL cannot express the root Collection flag / item name or a
        // Domain-based member, so update_visual on an SDT routes here with a structured payload:
        //   { isCollection?, collectionItemName?, children:[
        //       { name, type?, length?, decimals?, isCollection? },     // primitive member
        //       { name, basedOnDomain:"<Domain>" },                     // Domain-typed member
        //       { name, type:"<OtherSdt>", isCollection? },             // SDT-reference member
        //       { name, isLevel:true, children:[ ... ] }                // nested level
        //   ] }
        // Replacement removes omitted members; mode=add only inserts new members.
        public string UpdateSDTStructure(string sdtName, string payload, string expectedVersion)
        {
            try
            {
                var seed = _objectService.FindObject(sdtName, "SDT");
                if (seed == null) return HealingService.FormatNotFoundError(sdtName, _objectService.GetKbService().GetIndexCache().GetIndex());
                var obj = _objectService.FindObjectFreshByIdentity(seed);
                if (obj == null) return Models.McpResponse.Err(code: "FreshReadUnavailable",
                    message: "A fresh SDT read could not be confirmed; no write was attempted.", target: sdtName,
                    extra: new JObject { ["persisted"] = false });
                if (!obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                    return Models.McpResponse.Err(code: "NotAnSDT", message: "Object is not an SDT.", target: sdtName);

                JObject json;
                try { json = JObject.Parse(payload); }
                catch (Exception ex) { return Models.McpResponse.Err(code: "InvalidStructurePayload", message: "payload is not valid JSON: " + ex.Message, target: sdtName); }

                var children = json["children"] as JArray;
                if (children == null)
                    return Models.McpResponse.Err(
                        code: "InvalidStructurePayload",
                        message: "payload must contain a 'children' array.",
                        hint: "e.g. {\"isCollection\":true,\"collectionItemName\":\"FooItem\",\"children\":[{\"name\":\"Bar\",\"type\":\"VarChar\",\"length\":100},{\"name\":\"Kind\",\"basedOnDomain\":\"MyDomain\"}]}",
                        target: sdtName);

                if (string.IsNullOrWhiteSpace(expectedVersion))
                    return Models.McpResponse.Err(code: "ExpectedVersionRequired",
                        message: "An SDT structure write requires expectedVersion from get_visual.", target: sdtName,
                        extra: new JObject { ["persisted"] = false });
                string mode = json["mode"]?.ToString() ?? "replace";
                if (mode != "add" && mode != "replace")
                    return Models.McpResponse.Err(code: "InvalidStructureMode", message: "mode must be add or replace.", target: sdtName);
                bool add = mode == "add";
                if (add && (json["isCollection"] != null || json["collectionItemName"] != null))
                    return Models.McpResponse.Err(code: "InvalidStructurePayload",
                        message: "mode=add only accepts children; root metadata must be changed with mode=replace.", target: sdtName);
                JObject before = ReadStructure(obj);
                JArray beforeChildren = (JArray)before["children"];
                JArray projected;
                try { projected = SdtStructurePlan.Project(beforeChildren, children, add); }
                catch (ArgumentException ex) { return Models.McpResponse.Err(code: "InvalidStructurePayload", message: ex.Message, target: sdtName); }
                JArray proposedDiff = SdtStructurePlan.Diff(beforeChildren, projected);
                if (!add && proposedDiff.Any(x => x["change"]?.ToString() == "removed")
                    && json["allowRemoval"]?.ToObject<bool>() != true)
                    return Models.McpResponse.Err(code: "RemovalRequiresConfirmation",
                        message: "Replacement omits existing SDT members. Pass allowRemoval=true only after reviewing the dryRun removals.",
                        target: sdtName, extra: new JObject { ["persisted"] = false, ["diff"] = proposedDiff });

                string versionBefore = WriteService.ComputeVersionToken(obj);
                if (!string.Equals(expectedVersion, versionBefore, StringComparison.Ordinal))
                    return Models.McpResponse.Err(code: "VersionConflict", message: "The SDT changed after expectedVersion was captured.",
                        target: sdtName, extra: new JObject { ["persisted"] = false, ["currentVersion"] = versionBefore });

                ObjectMoveSnapshot snapshot;
                try { snapshot = ObjectMoveSnapshot.Capture(obj); }
                catch (Exception ex) { return Models.McpResponse.Err(code: "SnapshotFailed", message: ex.Message,
                    target: sdtName, extra: new JObject { ["persisted"] = false }); }
                KBObject beforeRevision = FindMatchingRevision(obj, before);
                if (beforeRevision == null) return Models.McpResponse.Err(code: "RollbackRevisionUnavailable",
                    message: "No native GeneXus revision matches the current SDT; the write was refused.",
                    target: sdtName, extra: new JObject { ["persisted"] = false });

                Exception writeFailure = null;
                bool writeStarted = false;
                bool saveStarted = false;
                bool committed = false;
                int applied = 0;
                bool isCollection = false;
                using (var sdkTrans = obj.Model.KB.BeginTransaction())
                {
                    try
                    {
                        var locked = obj.Model.Objects.Get(obj.Guid) ?? obj;
                        if (!string.Equals(WriteService.ComputeVersionToken(locked), versionBefore, StringComparison.Ordinal))
                            throw new InvalidOperationException("VersionConflict: the SDT changed before save.");
                        dynamic structure = FindStructurePart((dynamic)locked);
                        dynamic root = null;
                        try { root = structure?.Root; } catch { }
                        if (structure == null || root == null) throw new InvalidOperationException("SDT structure part/root not found.");
                        Artech.Architecture.Common.Objects.KBModel model = locked.Model;
                        writeStarted = true;
                        if (json["isCollection"] != null) { try { root.IsCollection = json["isCollection"].ToObject<bool>(); } catch { } }
                        string cin = json["collectionItemName"]?.ToString();
                        if (!string.IsNullOrEmpty(cin)) { try { root.CollectionItemName = cin; } catch { } }

                        applied = SyncSdtJsonNodes(root, children, model, !add);

                        GxMcp.Worker.Parsers.SdtDslParser.MarkPartDirty((object)structure, sdtName);
                        saveStarted = true;
                        locked.Save();
                        JObject staged = ReadStructure(locked);
                        if (!MatchesPlan(beforeChildren, children, (JArray)staged["children"], add)
                            || !MatchesMetadata(before, json, staged))
                            throw new InvalidOperationException("The saved SDT structure did not match the requested plan.");
                        if (!snapshot.CompareParts(locked, "SDTStructure").Equal)
                            throw new InvalidOperationException("The SDT save changed another object part.");
                        try { isCollection = (bool)root.IsCollection; } catch { }
                        sdkTrans.Commit();
                        committed = true;
                        WriteService.NotePerTargetWrite(sdtName);
                    }
                    catch (Exception ex)
                    {
                        if (!committed) try { sdkTrans.Rollback(); } catch { }
                        writeFailure = ex;
                    }
                }
                if (writeFailure != null)
                    return ReconcileFailedWrite(obj, beforeRevision, snapshot, before,
                        writeFailure, writeStarted, saveStarted, committed);

                KBObject persistedObj = null;
                JObject persisted = null;
                try
                {
                    persistedObj = _objectService.FindObjectFreshByIdentity(obj);
                    if (persistedObj != null) persisted = ReadStructure(persistedObj);
                }
                catch (Exception ex) { Logger.Warn("SDT post-save read failed: " + ex.Message); }
                bool verified = persisted != null
                    && MatchesPlan(beforeChildren, children, (JArray)persisted["children"], add)
                    && MatchesMetadata(before, json, persisted)
                    && snapshot.CompareParts(persistedObj, "SDTStructure").Equal;
                if (!verified)
                {
                    bool rolledBack = RestoreFromRevision(obj, beforeRevision, snapshot, before);
                    return Models.McpResponse.Err(code: persisted == null ? "FreshReadUnavailable" : "StructureUpdateNotPersisted",
                        message: rolledBack ? "The post-save SDT could not be verified; the prior revision was restored and verified."
                            : "The post-save SDT could not be verified and rollback could not be confirmed. Stop writing this object.",
                        hint: "Read get_visual again through a fresh SDK instance before another write.",
                        target: sdtName, extra: new JObject { ["persisted"] = rolledBack ? (JToken)new JValue(false)
                                : persisted == null ? JValue.CreateNull() : new JValue(true), ["persistedVerified"] = false,
                            ["beforeVersion"] = versionBefore, ["versionToken"] = persisted?["versionToken"],
                            ["persistedStructure"] = persisted, ["rollback"] = rolledBack ? "verified" : "unverified" });
                }
                try { _objectService.GetKbService().GetIndexCache().UpdateEntry(persistedObj); }
                catch (Exception ex) { Logger.Warn("SDT index refresh failed after verified save: " + ex.Message); }
                return Models.McpResponse.Ok(target: sdtName, code: "StructureUpdated",
                    result: new JObject { ["membersApplied"] = applied, ["isCollection"] = isCollection,
                        ["persisted"] = true, ["persistedVerified"] = true,
                        ["beforeVersion"] = versionBefore, ["versionToken"] = persisted["versionToken"],
                        ["diff"] = proposedDiff, ["implicitLifecycleActions"] = new JArray() });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "StructureUpdateFailed", message: ex.Message, target: sdtName);
            }
        }

        private string ReconcileFailedWrite(KBObject seed, KBObject revision, ObjectMoveSnapshot snapshot,
            JObject before, Exception failure, bool writeStarted, bool saveStarted, bool committed,
            string errorCode = "StructureUpdateFailed")
        {
            string target = seed.Name;
            if (!writeStarted)
                return Models.McpResponse.Err(code: failure?.Message?.StartsWith("VersionConflict:", StringComparison.Ordinal) == true
                        ? "VersionConflict" : errorCode,
                    message: failure?.InnerException?.Message ?? failure?.Message ?? "SDT update failed before mutation.",
                    target: target, extra: new JObject { ["persisted"] = false });

            KBObject fresh = null;
            JObject persisted = null;
            try
            {
                fresh = _objectService.FindObjectFreshByIdentity(seed);
                if (fresh != null) persisted = ReadStructure(fresh);
            }
            catch (Exception ex) { Logger.Warn("SDT failure reconciliation read failed: " + ex.Message); }
            bool unchanged = persisted != null && SameStructure(before, persisted)
                && snapshot.CompareParts(fresh, "SDTStructure").Equal;
            bool rolledBack = false;
            if (!unchanged && (saveStarted || committed))
                rolledBack = RestoreFromRevision(seed, revision, snapshot, before);
            return Models.McpResponse.Err(code: errorCode,
                message: failure?.InnerException?.Message ?? failure?.Message ?? "SDT update failed.",
                hint: unchanged || rolledBack ? "The prior SDT structure was verified after the failure."
                    : "The persisted state could not be reconciled. Read get_visual before another write.",
                target: target, extra: new JObject
                {
                    ["persisted"] = unchanged || rolledBack ? (JToken)new JValue(false)
                        : persisted == null ? JValue.CreateNull() : new JValue(true),
                    ["persistedStructure"] = persisted,
                    ["rollback"] = unchanged ? "transaction-verified" : rolledBack ? "verified" : "unverified",
                    ["implicitLifecycleActions"] = new JArray()
                });
        }

        private bool RestoreFromRevision(KBObject seed, KBObject revision, ObjectMoveSnapshot snapshot, JObject expected)
        {
            try
            {
                using (var tx = seed.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        var current = seed.Model.Objects.Get(seed.Guid) ?? seed;
                        CopyStructure(revision, current);
                        snapshot.RestoreParts(current, "SDTStructure");
                        current.Save();
                        if (!SameStructure(expected, ReadStructure(current))
                            || !snapshot.CompareParts(current, "SDTStructure").Equal)
                            throw new InvalidOperationException("The staged SDT rollback did not restore the prior state.");
                        tx.Commit();
                        committed = true;
                    }
                    finally { if (!committed) try { tx.Rollback(); } catch { } }
                }
                var restored = _objectService.FindObjectFreshByIdentity(seed);
                return restored != null && SameStructure(expected, ReadStructure(restored))
                    && snapshot.CompareParts(restored, "SDTStructure").Equal;
            }
            catch (Exception ex)
            {
                Logger.Error("SDT rollback failed: " + ex.Message);
                return false;
            }
        }

        internal KBObject FindMatchingRevision(KBObject obj, JObject expected)
        {
            try
            {
                foreach (KBObject revision in obj.GetVersions())
                    if (!ReferenceEquals(revision, obj) && revision.VersionId == obj.VersionId
                        && SameStructure(expected, ReadStructure(revision, false)))
                        return revision;
            }
            catch (Exception ex) { Logger.Warn("SDT rollback revision lookup failed: " + ex.Message); }
            return null;
        }

        private static void CopyStructure(KBObject source, KBObject destination)
        {
            if (ReferenceEquals(source, destination))
                throw new InvalidOperationException("The source revision is not detached from the current SDT.");
            dynamic srcPart = ObjectService.FindSdtStructurePartOf(source);
            dynamic dstPart = ObjectService.FindSdtStructurePartOf(destination);
            if (srcPart == null || dstPart == null || srcPart.Root == null || dstPart.Root == null)
                throw new InvalidOperationException("SDTStructure is unavailable on source or destination.");
            try { dstPart.Root.IsCollection = srcPart.Root.IsCollection; } catch { }
            try { dstPart.Root.CollectionItemName = srcPart.Root.CollectionItemName; } catch { }
            ObjectService.ClearSdtItems(dstPart.Root);
            ObjectService.CopySdtItems(srcPart.Root, dstPart.Root, destination.Model);
            GxMcp.Worker.Parsers.SdtDslParser.MarkPartDirty((object)dstPart, destination.Name);
        }

        internal static bool MatchesPlan(JArray before, JArray requested, JArray persisted, bool add)
        {
            if (persisted == null || persisted.Count != (add ? (before?.Count ?? 0) + requested.Count : requested.Count))
                return false;
            if (add)
            {
                for (int i = 0; i < before.Count; i++)
                    if (!JToken.DeepEquals(before[i], persisted[i])) return false;
            }
            int offset = add ? before.Count : 0;
            for (int i = 0; i < requested.Count; i++)
            {
                var wanted = requested[i] as JObject;
                var actual = persisted[i + offset] as JObject;
                if (wanted == null || actual == null
                    || !string.Equals(wanted["name"]?.ToString(), actual["name"]?.ToString(), StringComparison.OrdinalIgnoreCase))
                    return false;
                foreach (string key in new[] { "basedOnDomain", "basedOnAttribute", "referencedType", "length", "decimals", "isCollection", "isLevel" })
                    if (wanted[key] != null && !string.Equals(wanted[key].ToString(), actual[key]?.ToString(), StringComparison.OrdinalIgnoreCase))
                        return false;
                string type = wanted["type"]?.ToString();
                if (type?.StartsWith("Attribute:", StringComparison.OrdinalIgnoreCase) == true
                    && !string.Equals(type.Substring("Attribute:".Length).Trim(), actual["basedOnAttribute"]?.ToString(),
                        StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(type) && !type.StartsWith("Attribute", StringComparison.OrdinalIgnoreCase)
                    && !type.Equals("GX_SDT", StringComparison.OrdinalIgnoreCase) && !type.Equals("Compound", StringComparison.OrdinalIgnoreCase))
                {
                    string actualType = LooksLikePrimitiveType(type) ? actual["type"]?.ToString() : actual["referencedType"]?.ToString();
                    string expectedType = type.Split('(')[0].Trim();
                    if (!string.Equals(expectedType, actualType, StringComparison.OrdinalIgnoreCase)) return false;
                }
                if (wanted["children"] is JArray nested && !MatchesPlan(new JArray(), nested, actual["children"] as JArray, false))
                    return false;
            }
            return true;
        }

        private static bool MatchesMetadata(JObject before, JObject requested, JObject actual)
        {
            bool collection = (requested["isCollection"] ?? before["isCollection"])?.ToObject<bool>() ?? false;
            return JToken.DeepEquals(requested["isCollection"] ?? before["isCollection"], actual["isCollection"])
                && string.Equals(collection ? (requested["collectionItemName"] ?? before["collectionItemName"])?.ToString() : null,
                    actual["collectionItemName"]?.ToString(), StringComparison.Ordinal);
        }

        internal string RestoreRevision(KBObject current, KBObject revision, int versionId, string expectedVersion, bool dryRun)
        {
            string target = current.Name;
            if (ReferenceEquals(current, revision))
                return Models.McpResponse.Err(code: "VersionPartUnavailable",
                    message: "The selected revision is not detached from the current SDT.", target: target);
            if (string.IsNullOrWhiteSpace(expectedVersion))
                return Models.McpResponse.Err(code: "ExpectedVersionRequired",
                    message: "SDTStructure restoration requires expectedVersion from get_visual.", target: target,
                    extra: new JObject { ["persisted"] = false });
            string versionBefore = WriteService.ComputeVersionToken(current);
            if (!string.Equals(expectedVersion, versionBefore, StringComparison.Ordinal))
                return Models.McpResponse.Err(code: "VersionConflict", message: "The SDT changed after expectedVersion was captured.",
                    target: target, extra: new JObject { ["persisted"] = false, ["currentVersion"] = versionBefore });
            JObject before, desired;
            try { before = ReadStructure(current); desired = ReadStructure(revision, false); }
            catch (Exception ex) { return Models.McpResponse.Err(code: "VersionPartUnavailable", message: ex.Message, target: target); }
            var desiredChildren = desired["children"] as JArray;
            if (desiredChildren == null || desiredChildren.Count == 0)
                return Models.McpResponse.Err(code: "VersionPartUnavailable",
                    message: "The selected SDT revision has no readable structure; restoration was refused.", target: target);
            var beforeChildren = (JArray)before["children"];
            var diff = SdtStructurePlan.Diff(beforeChildren, desiredChildren);
            foreach (string key in new[] { "isCollection", "collectionItemName" })
                if (!JToken.DeepEquals(before[key], desired[key]))
                    diff.Add(new JObject { ["path"] = key, ["change"] = "changed",
                        ["before"] = before[key]?.DeepClone(), ["after"] = desired[key]?.DeepClone() });
            if (dryRun)
                return Models.McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                {
                    ["part"] = "SDTStructure", ["versionId"] = versionId, ["dryRun"] = true,
                    ["persisted"] = false, ["versionToken"] = versionBefore,
                    ["beforeCount"] = beforeChildren.Count, ["restoredCount"] = desiredChildren.Count,
                    ["diff"] = diff, ["implicitLifecycleActions"] = new JArray()
                });

            ObjectMoveSnapshot snapshot;
            try { snapshot = ObjectMoveSnapshot.Capture(current); }
            catch (Exception ex) { return Models.McpResponse.Err(code: "SnapshotFailed", message: ex.Message,
                target: target, extra: new JObject { ["persisted"] = false }); }
            KBObject beforeRevision = FindMatchingRevision(current, before);
            if (beforeRevision == null) return Models.McpResponse.Err(code: "RollbackRevisionUnavailable",
                message: "No native GeneXus revision matches the current SDT; the restore was refused.",
                target: target, extra: new JObject { ["persisted"] = false });

            bool committed = false;
            bool writeStarted = false;
            bool saveStarted = false;
            try
            {
                using (var tx = current.Model.KB.BeginTransaction())
                {
                    try
                    {
                        var locked = current.Model.Objects.Get(current.Guid) ?? current;
                        if (!string.Equals(WriteService.ComputeVersionToken(locked), versionBefore, StringComparison.Ordinal))
                            throw new InvalidOperationException("VersionConflict: the SDT changed before restoration.");
                        writeStarted = true;
                        CopyStructure(revision, locked);
                        saveStarted = true;
                        locked.Save();
                        if (!SameStructure(desired, ReadStructure(locked)))
                            throw new InvalidOperationException("The staged SDT does not match the selected revision.");
                        if (!snapshot.CompareParts(locked, "SDTStructure").Equal)
                            throw new InvalidOperationException("Restoration changed another object part.");
                        tx.Commit();
                        committed = true;
                    }
                    finally { if (!committed) try { tx.Rollback(); } catch { } }
                }
                var persisted = _objectService.FindObjectFreshByIdentity(current);
                JObject persistedStructure = persisted == null ? null : ReadStructure(persisted);
                if (persistedStructure == null || !SameStructure(desired, persistedStructure)
                    || !snapshot.CompareParts(persisted, "SDTStructure").Equal)
                {
                    bool rolledBack = RestoreFromRevision(current, beforeRevision, snapshot, before);
                    return Models.McpResponse.Err(code: persistedStructure == null ? "FreshReadUnavailable" : "RevisionRestoreNotPersisted",
                        message: rolledBack ? "Restoration could not be verified; the prior state was restored and verified."
                            : "Restoration could not be verified; rollback could not be confirmed. Stop writing this object.",
                        hint: "Read get_visual again through a fresh SDK instance before another write.",
                        target: target, extra: new JObject { ["persisted"] = rolledBack ? (JToken)new JValue(false)
                                : persistedStructure == null ? JValue.CreateNull() : new JValue(true),
                            ["rollbackVerified"] = rolledBack, ["persistedStructure"] = persistedStructure });
                }
                try { _objectService.GetKbService().GetIndexCache().UpdateEntry(persisted); }
                catch (Exception ex) { Logger.Warn("SDT index refresh failed after verified restore: " + ex.Message); }
                WriteService.NotePerTargetWrite(target);
                return Models.McpResponse.Ok(target: target, code: "RevisionStructureRestored", result: new JObject
                {
                    ["part"] = "SDTStructure", ["versionId"] = versionId, ["persisted"] = true,
                    ["persistedVerified"] = true, ["restoredCount"] = desiredChildren.Count,
                    ["versionToken"] = persistedStructure["versionToken"], ["diff"] = diff,
                    ["implicitLifecycleActions"] = new JArray()
                });
            }
            catch (Exception ex)
            {
                return ReconcileFailedWrite(current, beforeRevision, snapshot, before,
                    ex, writeStarted, saveStarted, committed, "RevisionRestoreFailed");
            }
        }

        private static bool SameStructure(JObject expected, JObject actual)
        {
            return JToken.DeepEquals(expected?["children"], actual?["children"])
                && JToken.DeepEquals(expected?["isCollection"], actual?["isCollection"])
                && string.Equals(expected?["collectionItemName"]?.ToString(), actual?["collectionItemName"]?.ToString(), StringComparison.Ordinal);
        }

        // Declaratively sync an SDT structure node's children from a JSON array. Adds/keeps members
        // named in the payload, removes the rest, recurses into nested levels. Handles primitive,
        // Domain-based (basedOnDomain) and SDT-reference (type names an SDT) members.
        private int SyncSdtJsonNodes(dynamic node, JArray children, Artech.Architecture.Common.Objects.KBModel model, bool removeMissing)
        {
            int applied = 0;
            dynamic items;
            try { items = node.Items; } catch (Exception ex) { throw new InvalidOperationException("SDT Items are unavailable.", ex); }

            var wanted = new System.Collections.Generic.HashSet<string>(
                children.Select(c => c["name"]?.ToString() ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            var existing = new System.Collections.Generic.Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            var toRemove = new System.Collections.Generic.List<dynamic>();
            foreach (dynamic c in items)
            {
                string cn = (string)c.Name;
                existing[cn] = c;
                if (removeMissing && !wanted.Contains(cn)) toRemove.Add(c);
            }
            foreach (dynamic d in toRemove) items.Remove(d);

            Type nodeType = ((object)node).GetType();
            Type eDBTypeT = nodeType.Assembly.GetType("Artech.Genexus.Common.eDBType");
            var addItem = eDBTypeT != null ? nodeType.GetMethod("AddItem", new[] { typeof(string), eDBTypeT }) : null;
            var addLevel = nodeType.GetMethod("AddLevel", new[] { typeof(string) });

            foreach (var tok in children)
            {
                var child = tok as JObject;
                if (child == null) throw new ArgumentException("Every SDT child must be an object.");
                string name = child["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Every SDT child needs a name.");

                bool isLevel = child["isLevel"]?.ToObject<bool>() ?? (child["children"] is JArray);
                bool isColl = child["isCollection"]?.ToObject<bool>() ?? false;
                string basedOnDomain = child["basedOnDomain"]?.ToString();
                string basedOnAttribute = child["basedOnAttribute"]?.ToString();
                string typeStr = child["type"]?.ToString();
                string referencedType = child["referencedType"]?.ToString();

                // Support alternative formats for attribute binding:
                // 1. type: "Attribute:<name>"
                if (string.IsNullOrEmpty(basedOnAttribute) && !string.IsNullOrEmpty(typeStr) && typeStr.StartsWith("Attribute:", StringComparison.OrdinalIgnoreCase))
                {
                    basedOnAttribute = typeStr.Substring("Attribute:".Length).Trim();
                }
                // 2. type: "Attribute" and referencedType: "<name>"
                else if (string.IsNullOrEmpty(basedOnAttribute) && !string.IsNullOrEmpty(typeStr) && typeStr.Equals("Attribute", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(referencedType))
                {
                    basedOnAttribute = referencedType;
                }
                // 3. basedOn: "Attribute:<name>" or "<name>"
                string basedOn = child["basedOn"]?.ToString();
                if (string.IsNullOrEmpty(basedOnAttribute) && !string.IsNullOrEmpty(basedOn))
                {
                    if (basedOn.StartsWith("Attribute:", StringComparison.OrdinalIgnoreCase))
                        basedOnAttribute = basedOn.Substring("Attribute:".Length).Trim();
                    else if (string.IsNullOrEmpty(basedOnDomain))
                        basedOnDomain = basedOn;
                }

                dynamic target = existing.TryGetValue(name, out var found) ? found : null;

                if (isLevel)
                {
                    if (target == null)
                    {
                        if (addLevel == null) throw new MissingMethodException("SDT AddLevel is unavailable.");
                        try { target = addLevel.Invoke((object)node, new object[] { name }); }
                        catch (Exception ex) { throw new InvalidOperationException("AddLevel failed for '" + name + "'.", ex); }
                    }
                    if (target == null) throw new InvalidOperationException("AddLevel returned no member for '" + name + "'.");
                    try { target.IsCollection = isColl; } catch { }
                    var grand = child["children"] as JArray ?? new JArray();
                    applied += 1 + SyncSdtJsonNodes(target, grand, model, removeMissing);
                    continue;
                }

                // Leaf: resolve an Attribute (basedOnAttribute), Domain (basedOnDomain) or an SDT reference (type names an SDT).
                KBObject domainObj = null, attributeObj = null, sdtObj = null;
                if (!string.IsNullOrEmpty(basedOnAttribute) && model != null)
                {
                    attributeObj = GxMcp.Worker.Helpers.VariableInjector.FindAttribute(model, basedOnAttribute);
                    if (attributeObj == null)
                        throw new Exception("basedOnAttribute '" + basedOnAttribute + "' did not resolve to an Attribute.");
                }
                else if (!string.IsNullOrEmpty(basedOnDomain) && model != null)
                {
                    domainObj = GxMcp.Worker.Helpers.VariableInjector.ResolveTypeObject(model, basedOnDomain);
                    if (!(domainObj is Artech.Genexus.Common.Objects.Domain)) domainObj = null;
                    if (domainObj == null)
                        throw new Exception("basedOnDomain '" + basedOnDomain + "' did not resolve to a Domain.");
                }
                else if (!string.IsNullOrEmpty(typeStr) && model != null && !LooksLikePrimitiveType(typeStr))
                {
                    var r = GxMcp.Worker.Helpers.VariableInjector.ResolveTypeObject(model, typeStr);
                    if (r != null && r.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)) sdtObj = r;
                }

                if (target == null)
                {
                    if (addItem == null || eDBTypeT == null) throw new MissingMethodException("SDT AddItem is unavailable.");
                    object baseType;
                    if (attributeObj != null) { try { baseType = ((dynamic)attributeObj).Type; } catch { baseType = Enum.Parse(eDBTypeT, "VARCHAR"); } }
                    else if (domainObj != null) { try { baseType = ((dynamic)domainObj).DataType; } catch { baseType = Enum.Parse(eDBTypeT, "VARCHAR"); } }
                    else if (sdtObj != null) baseType = Enum.Parse(eDBTypeT, "GX_SDT");
                    else if (GxMcp.Worker.Helpers.VariableInjector.TryParseDbType(typeStr, out var pt)) baseType = pt;
                    else baseType = Enum.Parse(eDBTypeT, "VARCHAR");
                    try { target = addItem.Invoke((object)node, new object[] { name, baseType }); }
                    catch (Exception ex) { throw new InvalidOperationException("AddItem failed for '" + name + "'.", ex); }
                }
                if (target == null) throw new InvalidOperationException("AddItem returned no member for '" + name + "'.");

                try { target.IsCollection = isColl; } catch { }
                if (attributeObj != null)
                {
                    GxMcp.Worker.Helpers.DomainPropertyApplier.ApplyAttributeBasedOn((object)target, attributeObj);
                }
                else if (domainObj != null)
                {
                    GxMcp.Worker.Helpers.DomainPropertyApplier.ApplyDomainBasedOn((object)target, domainObj);
                }
                else if (sdtObj != null)
                {
                    GxMcp.Worker.Helpers.VariableInjector.BindSdtItemToSdt((object)target, sdtObj);
                }
                else
                {
                    GxMcp.Worker.Helpers.DomainPropertyApplier.ClearAttributeBasedOn((object)target);
                    GxMcp.Worker.Helpers.DomainPropertyApplier.ClearDomainBasedOn((object)target);
                    if (child["length"] != null) { try { SetIntProperty((object)target, "Length", child["length"].ToObject<int>()); } catch { } }
                    if (child["decimals"] != null) { try { SetIntProperty((object)target, "Decimals", child["decimals"].ToObject<int>()); } catch { } }
                }
                applied++;
            }
            return applied;
        }

        private static void SetIntProperty(object target, string propName, int value)
        {
            try
            {
                var p = GxMcp.Worker.Helpers.AttributeTypeApplier.GetPropertyUnambiguous(target.GetType(), propName);
                if (p != null && p.CanWrite) p.SetValue(target, value, null);
            }
            catch (Exception ex) { Logger.Debug("[SDT WRITE] SetIntProperty " + propName + " failed: " + ex.Message); }
        }

        internal static bool LooksLikePrimitiveType(string typeStr)
        {
            if (string.IsNullOrWhiteSpace(typeStr)) return true;
            string[] prims = { "Numeric", "Char", "VarChar", "Varchar", "LongVarchar", "Date", "DateTime",
                               "Bool", "Boolean", "Blob", "Binary", "Image", "Bitmap", "Audio", "Video",
                               "GUID", "Geography" };
            foreach (var p in prims)
                if (typeStr.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Finds the SDT Structure Part using multiple strategies.
        /// In GX18 SDK, the part has TypeDescriptor.Name="SDTStructure" and class SDTStructurePart.
        /// </summary>
        private dynamic FindStructurePart(dynamic sdt)
        {
            // Strategy 1: Iterate parts matching by TypeDescriptor name or class name
            foreach (dynamic part in sdt.Parts)
            {
                try {
                    string descName = part.TypeDescriptor?.Name ?? "";
                    string className = part.GetType().Name;
                    if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                        className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                    { return part; }
                } catch { }
            }
            
            // Strategy 2: Parts.Get with known GUID
            try {
                var part = sdt.Parts.Get(Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3"));
                if (part != null) return part;
            } catch { }
            
            // Strategy 3: Duck typing - any part with Root.Items
            foreach (dynamic part in sdt.Parts)
            {
                try {
                    if (part.Root != null && part.Root.Items != null) return part;
                } catch { }
            }
            
            return null;
        }

        private JObject MapLevelToResult(dynamic level, Artech.Architecture.Common.Objects.KBModel model = null)
        {
            var res = new JObject();
            string memberName = (string)level.Name;
            if (string.IsNullOrWhiteSpace(memberName)) throw new InvalidOperationException("An SDT member has no readable name.");
            res["name"] = memberName;
            bool isLeaf = (bool)level.IsLeafItem;
            res["isCollection"] = (bool)level.IsCollection;

            if (!isLeaf)
            {
                res["isLevel"] = true;
                var children = new JArray();
                try {
                    foreach (dynamic child in level.Items)
                    {
                        children.Add(MapLevelToResult(child, model));
                    }
                } catch (Exception ex) { throw new InvalidOperationException("Nested SDT members could not be read.", ex); }
                res["children"] = children;
                res["type"] = "Compound";
            }
            else
            {
                res["isLevel"] = false;
                string typeStr = level.Type.ToString();
                if (string.IsNullOrWhiteSpace(typeStr)) throw new InvalidOperationException("An SDT member has no readable type.");
                res["type"] = typeStr;
                // issue #109: surface basedOnAttribute if member is based on an Attribute.
                try
                {
                    string attrName = GxMcp.Worker.Helpers.DomainPropertyApplier.GetAttributeBasedOnName((object)level);
                    if (!string.IsNullOrEmpty(attrName)) res["basedOnAttribute"] = attrName;
                }
                catch { }
                // issue #51: a member based on a Domain read back only as its base primitive type,
                // hiding the Domain link. Surface the Domain name so a domain-typed member is
                // visible (and round-trips through update_visual's basedOnDomain).
                try
                {
                    string domName = GxMcp.Worker.Helpers.DomainPropertyApplier.GetDomainBasedOnName((object)level);
                    if (!string.IsNullOrEmpty(domName)) res["basedOnDomain"] = domName;
                }
                catch { }
                // issue #47: surface the referenced SDT/type name for reference-typed members
                // instead of the raw "GX_SDT" enum (parity with the Structure DSL read).
                if (model != null && GxMcp.Worker.Helpers.SdtMemberResolver.IsReferenceType(typeStr))
                {
                    string refName = GxMcp.Worker.Helpers.SdtMemberResolver.ResolveReferencedTypeName((object)level, model);
                    if (!string.IsNullOrEmpty(refName)) res["referencedType"] = refName;
                }
                // issue #47: surface Length/Decimals (parity with genexus_inspect and the
                // Structure DSL). Without them a get_visual read dropped element size, e.g. a
                // Numeric(9,2) came back as bare "NUMERIC".
                try { object len = level.Length; if (len != null) res["length"] = Convert.ToInt32(len); } catch { }
                try { object dec = level.Decimals; if (dec != null) res["decimals"] = Convert.ToInt32(dec); } catch { }
            }
            return res;
        }
    }
}
