using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class HistoryService
    {
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly SDTService _sdtService;

        public HistoryService(ObjectService objectService, WriteService writeService)
        {
            _objectService = objectService;
            _writeService = writeService;
            _sdtService = new SDTService(objectService);
        }

        internal static string ResolveHistoryRoot(string kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath))
                throw new InvalidOperationException("The active Knowledge Base path is unavailable; history snapshots are disabled.");

            return EditSnapshotStore.ResolveRoot(kbPath);
        }

        internal static string[] FindLegacySnapshotFiles(string legacyRoot, string canonicalName)
        {
            if (string.IsNullOrWhiteSpace(legacyRoot) || string.IsNullOrWhiteSpace(canonicalName)
                || !Directory.Exists(legacyRoot))
                return new string[0];

            string prefix = canonicalName + "_";
            try
            {
                return Directory.EnumerateFiles(legacyRoot, "*", SearchOption.TopDirectoryOnly)
                    .Where(path =>
                    {
                        string fileName = Path.GetFileName(path);
                        return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Logger.Warn("[History] Legacy snapshot scan failed: " + ex.Message);
                return new string[0];
            }
        }

        private string GetActiveKbPath()
        {
            try
            {
                return _objectService.GetKbService().GetKbPath();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("The active Knowledge Base path could not be read; history snapshots are disabled.", ex);
            }
        }

        private string ResolveActiveSnapshotRoot()
        {
            return ResolveHistoryRoot(GetActiveKbPath());
        }

        private static string NormalizePartName(string partName)
        {
            return string.IsNullOrWhiteSpace(partName) ? "Source" : partName.Trim();
        }

        /// <summary>
        /// The single ObjectNotFound envelope for the edit-snapshot paths (restore,
        /// dry-run, list, discard, save). Those sites all resolve the target the same
        /// way, so their recovery contract — re-list by name, then force a reindex when
        /// the object exists but is not indexed — is built once here instead of being
        /// copy-pasted at each call site.
        /// </summary>
        internal static string SnapshotObjectNotFound(string target)
        {
            return Models.McpResponse.Err(
                code: "ObjectNotFound",
                message: "Object not found.",
                hint: "Verify the object name and ensure the KB is open.",
                nextSteps: new JArray(
                    Models.McpResponse.NextStep(
                        tool: "genexus_list_objects",
                        args: new JObject { ["name_contains"] = target },
                        why: "Lists objects whose names match, in case of a typo."),
                    Models.McpResponse.NextStep(
                        tool: "genexus_lifecycle",
                        args: new JObject { ["action"] = "index", ["force"] = true },
                        why: "Rebuilds the SearchIndex if the object exists but isn't indexed.")),
                target: target);
        }

        /// <summary>
        /// The ObjectNotFound envelope for the KB revision paths (get source, list
        /// revisions). Kept distinct from <see cref="SnapshotObjectNotFound"/> because
        /// its hint names the KB revision surface rather than the edit-snapshot one.
        /// </summary>
        internal static string RevisionObjectNotFound(string target)
        {
            return Models.McpResponse.Err(
                code: "ObjectNotFound",
                message: "Object not found.",
                hint: "The requested object is not available in the active Knowledge Base.",
                nextSteps: new JArray(Models.McpResponse.NextStep(
                    tool: "genexus_list_objects",
                    args: new JObject(),
                    why: "Lists available objects in the KB.")),
                target: target);
        }

        private static string LegacyHistoryRoot()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".history");
        }

        private static string CanonicalObjectName(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            string typeName = obj?.TypeDescriptor?.Name ?? obj?.GetType()?.Name ?? "Object";
            string objectName = obj?.Name ?? "Object";
            return (typeName + "_" + objectName).Replace(":", "_").Replace(" ", "_");
        }

        private static JArray LegacySnapshotEntries(string[] paths)
        {
            var entries = new JArray();
            foreach (string path in paths ?? new string[0])
            {
                entries.Add(new JObject
                {
                    ["fileName"] = Path.GetFileName(path),
                    ["legacy"] = true,
                    ["restorable"] = false,
                    ["reason"] = "Legacy snapshots have no Knowledge Base identity."
                });
            }
            return entries;
        }

        private static string LegacySnapshotNotRestorable(
            string target,
            string part,
            string canonicalName,
            string[] paths)
        {
            return Models.McpResponse.Err(
                code: "LegacySnapshotNotRestorable",
                message: "Legacy snapshots were found in the shared .history directory, but they were not restored because their Knowledge Base identity is unknown.",
                hint: "Use history_save in the active Knowledge Base to create a KB-scoped snapshot. Legacy files are reported for inspection and are never selected automatically.",
                nextSteps: new JArray(
                    Models.McpResponse.NextStep(
                        tool: "genexus_versioning",
                        args: new JObject { ["action"] = "history_save", ["name"] = target, ["part"] = part },
                        why: "Creates a new snapshot under the active Knowledge Base's isolated snapshot root."),
                    Models.McpResponse.NextStep(
                        tool: "genexus_versioning",
                        args: new JObject { ["action"] = "history_list", ["name"] = target, ["part"] = part },
                        why: "Shows the isolated snapshots and the non-restorable legacy files.")),
                target: target,
                extra: new JObject
                {
                    ["part"] = part,
                    ["canonicalName"] = canonicalName,
                    ["legacySnapshots"] = LegacySnapshotEntries(paths),
                    ["legacyRestoreAllowed"] = false
                });
        }

        /// <summary>
        /// History dispatch. <paramref name="partName"/> + <paramref name="snapshotToken"/>
        /// drive the edit-snapshot <c>restore</c> action: <c>snapshot=latest</c> or
        /// a timestamp substring resolves to <c>&lt;kbPath&gt;/.gx/snapshots/&lt;guid&gt;-&lt;part&gt;-*.bak</c>
        /// and the prior bytes are routed back through <see cref="WriteService.WriteObject(string, string, string, string, bool, bool, bool, bool)"/>.
        /// When <paramref name="discard"/> is <c>true</c> and no snapshot token is
        /// supplied, the most recent EditSnapshotStore entry is restored — IDE
        /// <i>History | Restore</i> / <i>Discard changes</i> parity. Missing
        /// snapshots return a <c>NoSnapshot</c> envelope rather than an error.
        /// </summary>
        public string Execute(string target, string action, int versionId = 0,
                              string partName = null, string snapshotToken = null,
                              bool discard = false, bool dryRun = false, string expectedVersion = null)
        {
            try
            {
                switch (action?.ToLower())
                {
                    case "list":
                        if (!string.IsNullOrWhiteSpace(snapshotToken) || !string.IsNullOrWhiteSpace(partName))
                            return ListEditSnapshots(target, partName);
                        return ListRevisions(target);
                    case "get_source":
                        return GetVersionSource(target, versionId, partName);
                    case "save":
                        return SaveSnapshot(target, partName);
                    case "restore":
                        // A version ID is an explicit SDK-history selection. It must
                        // never fall through to the local snapshot store (or the old
                        // shared .history directory) when supplied.
                        if (versionId > 0)
                            return RestoreVersion(target, partName, versionId, dryRun, discard, expectedVersion);
                        // Item 21 (friction 2026-05-22): dryRun=true returns the diff
                        // (current vs snapshot) without writing through SDK.
                        if (dryRun)
                            return DryRunRestore(target, partName, snapshotToken, discard);
                        if (!string.IsNullOrWhiteSpace(snapshotToken))
                            return RestoreEditSnapshot(target, partName, snapshotToken);
                        if (discard)
                            return DiscardLatestEditSnapshot(target, partName);
                        return RestoreSnapshot(target, partName);
                    default:
                        return Models.McpResponse.Err(
                        code: "UnknownHistoryAction",
                        message: "Unknown history action '" + action + "'.",
                        hint: "Supported actions are list, get_source, save and restore.",
                        nextSteps: new JArray(Models.McpResponse.NextStep(
                            tool: "genexus_history",
                            args: new JObject { ["target"] = target, ["action"] = "list" },
                            why: "Lists available revisions/snapshots for this object.")),
                        target: target);
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "HistoryExecuteFailed",
                    message: ex.Message,
                    hint: "Check the history action and target.",
                    target: target);
            }
        }

        /// <summary>
        /// Item 21 (friction 2026-05-22) — universal dryRun for genexus_history
        /// action=restore. Resolves the same snapshot the live restore would
        /// pick, reads the current persisted source, and returns a unified
        /// diff envelope — no SDK write.
        /// </summary>
        private string DryRunRestore(string target, string partName, string snapshotToken, bool discard)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return SnapshotObjectNotFound(target);
            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Err(code: "DryRunFailed", message: ex.Message, target: target); }

            string root = ResolveActiveSnapshotRoot();
            string part = NormalizePartName(partName);

            string path;
            if (!string.IsNullOrWhiteSpace(snapshotToken))
            {
                path = EditSnapshotStore.ResolveByTimestamp(root, guid, part, snapshotToken);
            }
            else
            {
                var files = EditSnapshotStore.List(root, guid, part);
                path = files.Count > 0 ? files[0] : null;
            }
            if (string.IsNullOrEmpty(path))
            {
                string[] legacy = FindLegacySnapshotFiles(LegacyHistoryRoot(), CanonicalObjectName(obj));
                if (legacy.Length > 0)
                    return LegacySnapshotNotRestorable(target, part, CanonicalObjectName(obj), legacy);

                return Models.McpResponse.Ok(
                    target: target,
                    code: "NoSnapshot",
                    result: new JObject
                    {
                        ["part"] = part,
                        ["dryRun"] = true,
                        ["hint"] = "No snapshot to dry-run against. Edit this object first to capture a baseline."
                    });
            }

            string snapshotContent = EditSnapshotStore.ReadSnapshot(path);
            if (snapshotContent == null)
            {
                return Models.McpResponse.Err(
                    code: "SnapshotReadFailed",
                    message: "File exists but could not be decoded: " + path,
                    hint: "The snapshot file may be corrupt. List snapshots and use a different token.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_history",
                        args: new JObject { ["target"] = target, ["action"] = "list", ["part"] = part },
                        why: "Lists available snapshots to find a valid token.")),
                    target: target);
            }

            string currentContent = string.Empty;
            try
            {
                string readJson = _objectService.ReadObjectSource(target, part, null, null, "mcp", true, null);
                if (!string.IsNullOrWhiteSpace(readJson))
                {
                    var parsed = JObject.Parse(readJson);
                    currentContent = parsed["source"]?.ToString() ?? parsed["content"]?.ToString() ?? string.Empty;
                }
            }
            catch { /* leave currentContent empty */ }

            string diff = GxMcp.Worker.Helpers.DiffBuilder.UnifiedDiff(currentContent, snapshotContent, 3);
            return Models.McpResponse.Ok(
                target: target,
                code: "DryRun",
                result: new JObject
                {
                    ["part"] = part,
                    ["dryRun"] = true,
                    ["discard"] = discard,
                    ["restoreSource"] = System.IO.Path.GetFileName(path),
                    ["restoreSourcePath"] = path,
                    ["diff"] = diff,
                    ["hint"] = "Re-run without dryRun to write these bytes through WriteService."
                });
        }

        private string ListEditSnapshots(string target, string partName)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return SnapshotObjectNotFound(target);
            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Err(code: "SnapshotListFailed", message: ex.Message, target: target); }

            string root = ResolveActiveSnapshotRoot();
            string part = NormalizePartName(partName);
            var files = EditSnapshotStore.List(root, guid, part);
            string canonicalName = CanonicalObjectName(obj);
            string[] legacy = FindLegacySnapshotFiles(LegacyHistoryRoot(), canonicalName);
            var arr = new JArray();
            foreach (var f in files)
            {
                arr.Add(new JObject
                {
                    ["path"] = f,
                    ["fileName"] = System.IO.Path.GetFileName(f)
                });
            }
            return Models.McpResponse.Ok(
                target: target,
                code: "SnapshotList",
                result: new JObject
                {
                    ["part"] = part,
                    ["count"] = files.Count,
                    ["snapshots"] = arr,
                    ["legacySnapshotCount"] = legacy.Length,
                    ["legacySnapshots"] = LegacySnapshotEntries(legacy),
                    ["legacyRestoreAllowed"] = false
                });
        }

        /// <summary>
        /// v2.6.6 Stream H (FR#28) — IDE "Discard changes" parity. Resolves
        /// the most recent pre-edit snapshot for (target, part) and restores
        /// it through WriteService (the same persistence boundary the IDE
        /// uses). Returns the snapshot token used so the caller has an
        /// audit trail. NoSnapshot is a soft outcome — the agent may ask
        /// for discard before any edit was captured and that should not be
        /// treated as an error.
        /// </summary>
        private string DiscardLatestEditSnapshot(string target, string partName)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return SnapshotObjectNotFound(target);
            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Err(code: "DiscardFailed", message: ex.Message, target: target); }

            string kbPath = GetActiveKbPath();

            return DiscardLatestEditSnapshotCore(
                target, partName, guid, kbPath,
                (t, p, content) => _writeService.WriteObject(t, p, content),
                CanonicalObjectName(obj));
        }

        /// <summary>
        /// v2.6.6 Stream H (FR#28) — pure helper, no SDK reads. Splits out the
        /// snapshot lookup + restoration so it can be unit-tested without a live
        /// KB. <paramref name="writer"/> is the persistence hook (WriteService
        /// in production; a recording delegate in tests).
        /// </summary>
        internal static string DiscardLatestEditSnapshotCore(
            string target,
            string partName,
            string objectGuid,
            string kbPath,
            Func<string, string, string, string> writer,
            string canonicalName = null)
        {
            string root = EditSnapshotStore.ResolveRoot(kbPath);
            string part = NormalizePartName(partName);
            var files = EditSnapshotStore.List(root, objectGuid, part);
            if (files.Count == 0)
            {
                string[] legacy = FindLegacySnapshotFiles(LegacyHistoryRoot(), canonicalName);
                if (legacy.Length > 0)
                    return LegacySnapshotNotRestorable(target, part, canonicalName, legacy);

                return Models.McpResponse.Ok(
                    target: target,
                    code: "NoSnapshot",
                    result: new JObject
                    {
                        ["part"] = part,
                        ["hint"] = "Edit this object first to capture a baseline; discard restores the pre-edit state."
                    });
            }

            string path = files[0]; // newest
            string content = EditSnapshotStore.ReadSnapshot(path);
            if (content == null)
            {
                return Models.McpResponse.Err(
                    code: "SnapshotReadFailed",
                    message: "File exists but could not be decoded: " + path,
                    hint: "The snapshot file may be corrupt. List snapshots and use a different token.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_history",
                        args: new JObject { ["target"] = target, ["action"] = "list", ["part"] = part },
                        why: "Lists available snapshots to find a valid token.")),
                    target: target);
            }

            string writeResult = writer(target, part, content) ?? "{}";
            return AttachSnapshotRestoreMetadata(writeResult, path, part, true);
        }

        private string RestoreEditSnapshot(string target, string partName, string snapshotToken)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return SnapshotObjectNotFound(target);
            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Err(code: "SnapshotRestoreFailed", message: ex.Message, target: target); }

            string root = ResolveActiveSnapshotRoot();
            string part = NormalizePartName(partName);
            string path = EditSnapshotStore.ResolveByTimestamp(root, guid, part, snapshotToken);
            if (string.IsNullOrEmpty(path))
            {
                string[] legacy = FindLegacySnapshotFiles(LegacyHistoryRoot(), CanonicalObjectName(obj));
                if (legacy.Length > 0)
                    return LegacySnapshotNotRestorable(target, part, CanonicalObjectName(obj), legacy);

                return Models.McpResponse.Err(
                    code: "SnapshotNotFound",
                    message: "No snapshot matched token '" + snapshotToken + "' for part '" + part + "'.",
                    hint: "Use action=list with part=" + part + " to enumerate available snapshots.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_history",
                        args: new JObject { ["target"] = target, ["action"] = "list", ["part"] = part },
                        why: "Returns the list of snapshot tokens for this object and part.")),
                    target: target,
                    extra: new JObject { ["part"] = part });
            }

            string content = EditSnapshotStore.ReadSnapshot(path);
            if (content == null)
            {
                return Models.McpResponse.Err(
                    code: "SnapshotReadFailed",
                    message: "File exists but could not be decoded: " + path,
                    hint: "The snapshot file may be corrupt. List snapshots and use a different token.",
                    target: target);
            }

            string writeResult = _writeService.WriteObject(target, part, content);
            return AttachSnapshotRestoreMetadata(writeResult, path, part, false);
        }

        private string GetVersionSource(string target, int versionId, string partName)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return RevisionObjectNotFound(target);

            string requestedPart = NormalizePartName(partName);
            if (string.Equals(requestedPart, "SDTStructure", StringComparison.OrdinalIgnoreCase))
            {
                obj = _objectService.FindObjectFreshByIdentity(obj);
                if (obj == null) return Models.McpResponse.Err(code: "FreshReadUnavailable",
                    message: "A fresh SDT read could not be confirmed.", target: target);
                var revision = obj.GetVersions().Cast<global::Artech.Architecture.Common.Objects.KBObject>()
                    .FirstOrDefault(v => v.VersionId == versionId);
                if (revision == null) return Models.McpResponse.Err(code: "VersionNotFound",
                    message: "The requested revision is unavailable.", target: target);
                try
                {
                    return Models.McpResponse.Ok(target: target, code: "VersionStructureRead", result: new JObject
                    {
                        ["part"] = "SDTStructure", ["versionId"] = versionId,
                        ["structure"] = _sdtService.ReadStructure(revision, false)
                    });
                }
                catch (Exception ex) { return Models.McpResponse.Err(code: "VersionPartUnavailable", message: ex.Message, target: target); }
            }
            if (!TryGetVersionPartContent(obj, versionId, requestedPart, out string content, out string errorCode, out string reason))
            {
                return Models.McpResponse.Err(
                    code: errorCode,
                    message: reason,
                    hint: "Use action=list to see available version IDs and parts for this object.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_versioning",
                        args: new JObject { ["action"] = "history_list", ["name"] = target, ["part"] = requestedPart },
                        why: "Returns available revisions and the requested part.")),
                    target: target,
                    extra: new JObject { ["part"] = requestedPart, ["versionId"] = versionId });
            }

            return Models.McpResponse.Ok(
                target: target,
                code: "VersionSourceRead",
                result: new JObject
                {
                    ["source"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                    ["isBase64"] = true,
                    ["versionId"] = versionId,
                    ["part"] = requestedPart
                });
        }

        private bool TryGetVersionPartContent(
            global::Artech.Architecture.Common.Objects.KBObject obj,
            int versionId,
            string partName,
            out string content,
            out string errorCode,
            out string reason)
        {
            content = null;
            errorCode = "VersionNotFound";
            reason = "Version " + versionId + " not found for this object.";
            try
            {
                var versions = obj.GetVersions()
                    .Cast<global::Artech.Architecture.Common.Objects.KBObject>()
                    .ToList();
                var targetVersion = versions.FirstOrDefault(v => v.VersionId == versionId);
                if (targetVersion == null) return false;

                global::Artech.Architecture.Common.Objects.KBObjectPart part;
                try
                {
                    part = GxMcp.Worker.Structure.PartAccessor.GetPart(targetVersion, partName);
                }
                catch (Exception ex)
                {
                    errorCode = "VersionPartUnavailable";
                    reason = "Version " + versionId + " could not resolve part '" + partName + "': " + ex.Message;
                    return false;
                }

                if (part == null)
                {
                    errorCode = "VersionPartUnavailable";
                    reason = "Version " + versionId + " does not contain part '" + partName + "'.";
                    return false;
                }

                var sourcePart = part as global::Artech.Architecture.Common.Objects.ISource;
                if (sourcePart == null)
                {
                    errorCode = "VersionPartUnsupported";
                    reason = "Version " + versionId + " part '" + partName + "' is not a textual source part and was not restored.";
                    return false;
                }

                content = sourcePart.Source ?? string.Empty;
                errorCode = null;
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                errorCode = "VersionSourceFailed";
                reason = "SDK Version access failed: " + ex.Message;
                return false;
            }
        }

        private string RestoreVersion(string target, string partName, int versionId, bool dryRun, bool discard, string expectedVersion)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null)
            {
                return Models.McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found.",
                    hint: "Verify the object name and ensure the KB is open.",
                    nextSteps: new JArray(
                        Models.McpResponse.NextStep(
                            tool: "genexus_list_objects",
                            args: new JObject { ["name_contains"] = target },
                            why: "Lists objects whose names match, in case of a typo.")),
                    target: target);
            }

            string requestedPart = NormalizePartName(partName);
            if (string.Equals(requestedPart, "SDTStructure", StringComparison.OrdinalIgnoreCase))
            {
                obj = _objectService.FindObjectFreshByIdentity(obj);
                if (obj == null) return Models.McpResponse.Err(code: "FreshReadUnavailable",
                    message: "A fresh SDT read could not be confirmed; no restore was attempted.", target: target);
                var revision = obj.GetVersions().Cast<global::Artech.Architecture.Common.Objects.KBObject>()
                    .FirstOrDefault(v => v.VersionId == versionId);
                if (revision == null) return Models.McpResponse.Err(code: "VersionNotFound",
                    message: "The requested revision is unavailable.", target: target);
                return _sdtService.RestoreRevision(obj, revision, versionId, expectedVersion, dryRun);
            }
            if (!TryGetVersionPartContent(obj, versionId, requestedPart, out string content, out string errorCode, out string reason))
            {
                return Models.McpResponse.Err(
                    code: errorCode,
                    message: reason,
                    hint: "Use action=list to see available version IDs and parts for this object.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_versioning",
                        args: new JObject { ["action"] = "history_list", ["name"] = target, ["part"] = requestedPart },
                        why: "Returns available revisions and the requested part.")),
                    target: target,
                    extra: new JObject { ["part"] = requestedPart, ["versionId"] = versionId });
            }

            if (dryRun)
            {
                string currentContent = ReadCurrentPartContent(target, requestedPart);
                return Models.McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new JObject
                    {
                        ["part"] = requestedPart,
                        ["versionId"] = versionId,
                        ["dryRun"] = true,
                        ["discard"] = discard,
                        ["restoreSource"] = "version:" + versionId,
                        ["diff"] = GxMcp.Worker.Helpers.DiffBuilder.UnifiedDiff(currentContent, content, 3),
                        ["hint"] = "Re-run without dryRun to write this version through WriteService."
                    });
            }

            string writeResult = _writeService.WriteObject(target, requestedPart, content);
            return AttachVersionRestoreMetadata(writeResult, requestedPart, versionId, discard);
        }

        private string ReadCurrentPartContent(string target, string partName)
        {
            try
            {
                string readJson = _objectService.ReadObjectSource(target, partName, 0, 0, "mcp", false, null);
                if (!string.IsNullOrWhiteSpace(readJson))
                {
                    var parsed = JObject.Parse(readJson);
                    return parsed["source"]?.ToString() ?? parsed["content"]?.ToString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        private static string AttachVersionRestoreMetadata(string writeResult, string partName, int versionId, bool discard)
        {
            try
            {
                var json = JObject.Parse(writeResult ?? "{}");
                json["restoredFromVersion"] = versionId;
                json["restoredPart"] = partName;
                json["discarded"] = discard;
                return json.ToString();
            }
            catch
            {
                return writeResult;
            }
        }

        private string ListRevisions(string target)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return RevisionObjectNotFound(target);

            string guid;
            try { guid = obj.Guid.ToString(); }
            catch (Exception ex) { return Models.McpResponse.Err(code: "HistoryAccessFailed", message: ex.Message, target: target); }

            string root = ResolveActiveSnapshotRoot();
            string canonicalName = CanonicalObjectName(obj);
            var snapshotEntries = EditSnapshotStore.ListForGuid(root, guid);
            var snapshots = new JArray();
            foreach (var entry in snapshotEntries)
            {
                snapshots.Add(new JObject
                {
                    ["path"] = entry.Path,
                    ["fileName"] = entry.FileName,
                    ["part"] = entry.Part,
                    ["timestamp"] = entry.Timestamp,
                    ["bytes"] = entry.Bytes,
                    ["legacy"] = false,
                    ["restorable"] = true
                });
            }
            string[] legacy = FindLegacySnapshotFiles(LegacyHistoryRoot(), canonicalName);

            var history = new JArray();
            try
            {
                var versions = obj.GetVersions().Cast<global::Artech.Architecture.Common.Objects.KBObject>();
                foreach (var rev in versions)
                {
                    history.Add(new JObject
                    {
                        ["version"] = rev.VersionId,
                        ["date"] = rev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["user"] = rev.UserName,
                        ["comment"] = rev.Comment
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to read revisions: " + ex.Message);
                return Models.McpResponse.Err(
                    code: "HistoryAccessFailed",
                    message: "SDK History access failed: " + ex.Message,
                    hint: "The SDK history API may not be available for this KB.",
                    target: target,
                    extra: new JObject
                    {
                        ["snapshots"] = snapshots,
                        ["snapshotCount"] = snapshotEntries.Count,
                        ["legacySnapshots"] = LegacySnapshotEntries(legacy),
                        ["legacySnapshotCount"] = legacy.Length,
                        ["legacyRestoreAllowed"] = false
                    });
            }

            return Models.McpResponse.Ok(
                target: target,
                code: "RevisionList",
                result: new JObject
                {
                    ["history"] = history,
                    ["snapshots"] = snapshots,
                    ["snapshotCount"] = snapshotEntries.Count,
                    ["legacySnapshots"] = LegacySnapshotEntries(legacy),
                    ["legacySnapshotCount"] = legacy.Length,
                    ["legacyRestoreAllowed"] = false
                });
        }

        private string SaveSnapshot(string target, string partName)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return SnapshotObjectNotFound(target);

            string part = NormalizePartName(partName);
            string root = ResolveActiveSnapshotRoot();
            string guid = obj.Guid.ToString();
            string sourceJson = _objectService.ReadObjectSource(target, part, 0, 0, "mcp", false, null);
            JObject json;
            try { json = JObject.Parse(sourceJson); }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "SnapshotReadFailed",
                    message: "Could not read part '" + part + "' before saving a snapshot: " + ex.Message,
                    hint: "Use action=list to inspect the available parts for this object.",
                    target: target);
            }

            if (json["error"] != null || string.Equals(json["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
                return sourceJson;

            string code = json["source"]?.ToString() ?? json["content"]?.ToString();
            if (code == null)
            {
                return Models.McpResponse.Err(
                    code: "SnapshotPartUnavailable",
                    message: "Part '" + part + "' does not expose textual content and was not snapshotted.",
                    hint: "Choose a readable textual part from availableParts; history never silently substitutes Source.",
                    target: target,
                    extra: new JObject { ["part"] = part });
            }

            var info = EditSnapshotStore.SaveSnapshot(root, guid, part, code);
            if (info == null)
            {
                return Models.McpResponse.Err(
                    code: "SnapshotSaveFailed",
                    message: "The KB-scoped history snapshot could not be saved.",
                    hint: "Check the active KB path and filesystem permissions.",
                    target: target,
                    extra: new JObject { ["part"] = part, ["snapshotRoot"] = root });
            }

            return Models.McpResponse.Ok(
                target: target,
                code: "SnapshotSaved",
                result: new JObject
                {
                    ["file"] = Path.GetFileName(info.Path),
                    ["path"] = info.Path,
                    ["timestamp"] = info.Timestamp,
                    ["canonicalName"] = CanonicalObjectName(obj),
                    ["guid"] = info.Guid,
                    ["part"] = part,
                    ["compressed"] = info.Compressed,
                    ["bytes"] = info.Bytes,
                    ["legacyDirectoryUsed"] = false
                });
        }

        private string RestoreSnapshot(string target, string partName)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null) return SnapshotObjectNotFound(target);

            string part = NormalizePartName(partName);
            string root = ResolveActiveSnapshotRoot();
            string guid = obj.Guid.ToString();
            var files = EditSnapshotStore.List(root, guid, part);
            string canonicalName = CanonicalObjectName(obj);

            if (files.Count == 0)
            {
                string[] legacy = FindLegacySnapshotFiles(LegacyHistoryRoot(), canonicalName);
                if (legacy.Length > 0)
                    return LegacySnapshotNotRestorable(target, part, canonicalName, legacy);

                return Models.McpResponse.Err(
                    code: "SnapshotNotFound",
                    message: "No KB-scoped snapshots found for '" + canonicalName + "' part '" + part + "'.",
                    hint: "Use action=save first to capture a snapshot before restoring.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_versioning",
                        args: new JObject { ["action"] = "history_save", ["name"] = target, ["part"] = part },
                        why: "Saves the current part under the active Knowledge Base's isolated snapshot root.")),
                    target: target,
                    extra: new JObject { ["part"] = part, ["legacySnapshots"] = new JArray() });
            }

            string path = files[0];
            string code = EditSnapshotStore.ReadSnapshot(path);
            if (code == null)
            {
                return Models.McpResponse.Err(
                    code: "SnapshotReadFailed",
                    message: "File exists but could not be decoded: " + path,
                    hint: "Use action=list with part=" + part + " to enumerate available snapshots and choose another token.",
                    target: target);
            }

            string writeResult = _writeService.WriteObject(target, part, code);
            return AttachSnapshotRestoreMetadata(writeResult, path, part, false);
        }

        private static string AttachSnapshotRestoreMetadata(string writeResult, string path, string part, bool discarded)
        {
            try
            {
                var json = JObject.Parse(writeResult ?? "{}");
                json["restoredFrom"] = path;
                json["restoredSnapshot"] = Path.GetFileName(path);
                json["restoredPart"] = part;
                if (discarded) json["discarded"] = true;
                return json.ToString();
            }
            catch
            {
                return writeResult;
            }
        }
    }
}
