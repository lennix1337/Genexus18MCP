using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class SdkTextTreeService
    {
        private sealed class NativeExportState
        {
            public string Root;
            public string Mode;
            public string ManifestPath;
            public TextBatchOptions Options;
            public bool IncludeDependencies;
            public bool IncludeVisualParts;
            public bool IncludeModuleMetadata;
            public bool IncludeModulePackages;
            public bool IncludeTableProjections;
            public bool FullSelection;
            public bool HasExplicitTargets;
            public List<ExportPlan> Plan;
            public Dictionary<string, JObject> OldManifest;
            public Dictionary<string, JObject> OldMetadata;
            public Dictionary<string, JObject> OldPackages;
            public HashSet<string> PlanKeys;
            public HashSet<int> HomonymIndexes;
            public HashSet<string> EmittedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> EmittedMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> EmittedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public JArray Results = new JArray();
            public JArray ManifestItems = new JArray();
            public JArray MetadataItems = new JArray();
            public JArray PackageItems = new JArray();
            public int Exported;
            public int Skipped;
            public int NoTextParts;
            public int Failed;
            public int FilesWritten;
            public int CompanionFilesWritten;
            public int FilesDeleted;
            public long BytesWritten;
            public int MetadataFilesWritten;
            public int MetadataFilesSkipped;
            public int PackageFilesWritten;
            public int PackageFilesSkipped;
            public bool StoppedOnError;
            public string ManifestError;
            public string CancelledResponse;
        }

        private string RunNativeExport(string target, JObject args, CancellationToken ct)
        {
            if (!TryPrepareNativeExport(target, args, out NativeExportState state,
                out string errorCode, out string errorMessage, out string errorHint, out string errorTarget, out string errorResponse))
                return errorResponse ?? McpResponse.Err(code: errorCode, message: errorMessage, hint: errorHint, target: errorTarget);

            if (state.Options.ListOnly) return BuildNativeExportPlan(state);
            if (!TryCreateNativeExportRoot(state, out string directoryError))
                return McpResponse.Err(code: "ExportDirectoryCreateFailed", message: directoryError, target: state.Root);

            ExportNativeObjects(state, args, ct);
            if (state.CancelledResponse != null) return state.CancelledResponse;
            WriteNativeExportAuxiliaryFiles(state);
            ReconcileNativeExport(state);
            WriteNativeExportManifest(state);
            return BuildNativeExportResponse(state);
        }

        private bool TryPrepareNativeExport(string target, JObject args, out NativeExportState state,
            out string errorCode, out string errorMessage, out string errorHint, out string errorTarget, out string errorResponse)
        {
            state = null;
            errorCode = null;
            errorMessage = null;
            errorHint = null;
            errorTarget = null;
            errorResponse = null;
            string outputPath = args["outputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                errorCode = "OutputPathRequired";
                errorMessage = "outputPath is required for an SDK text-tree export.";
                errorHint = "Provide an empty or existing directory for the src/ref tree.";
                return false;
            }
            if (_objectService == null || _selector == null)
            {
                errorCode = "SdkTextTreeUnavailable";
                errorMessage = "The SDK text-tree exporter is not connected to ObjectService.";
                return false;
            }

            string root;
            try { root = Path.GetFullPath(outputPath); }
            catch (Exception ex)
            {
                errorCode = "InvalidOutputPath";
                errorMessage = ex.Message;
                errorTarget = outputPath;
                return false;
            }

            string mode = NormalizeMode(args["mode"]?.ToString());
            if (mode == null)
            {
                errorCode = "InvalidTextExportMode";
                errorMessage = "mode must be all, newAndModified or newOnly.";
                errorHint = "Use mode=newAndModified for an incremental export.";
                return false;
            }
            if (!TextBatchOptions.TryParse(args, false, false, false, true, false, false, true,
                out TextBatchOptions options, out string optionError))
            {
                errorCode = "InvalidTextOperationOption";
                errorMessage = optionError;
                return false;
            }

            List<TextSelector> explicitTargets = ExplicitTargets(target, args);
            // The native export is the only path that selects an object by its GUID or EntityKey.
            bool selectionOk = _selector.TrySelectEntries(target, args, allowAll: true, out List<SearchIndex.IndexEntry> selected, out string selectionError,
                options.IncludeChildren, allowIdentity: true);
            // The index is read after the selection, which may itself have loaded it.
            SearchIndex loadedIndex = _indexCacheService?.TryGetLoadedIndex();
            bool indexReady = loadedIndex?.Objects != null && loadedIndex.Objects.Count > 0;
            if (!selectionOk)
            {
                // Without a ready index only the explicit targets can still be answered, by the SDK.
                if (indexReady || explicitTargets.Count == 0)
                {
                    errorCode = "ObjectSelectionFailed";
                    errorMessage = selectionError;
                    errorTarget = target;
                    return false;
                }
                selected = new List<SearchIndex.IndexEntry>();
            }
            // An explicit target the index does not hold is looked up in the SDK, as genexus_read does. What the SDK
            // finds takes the request's filters and limit, as an indexed entry does.
            var unresolved = new List<TextSelector>();
            var lookedUp = new List<SearchIndex.IndexEntry>();
            foreach (TextSelector explicitTarget in explicitTargets)
            {
                if (indexReady && loadedIndex.Objects.Values.Any(entry => TextSelectorParser.Matches(explicitTarget, entry, allowIdentity: true))) continue;
                if (selected.Any(entry => TextSelectorParser.Matches(explicitTarget, entry, allowIdentity: true))) continue;
                SearchIndex.IndexEntry found = _sdkEntryLookup(explicitTarget);
                if (found == null) unresolved.Add(explicitTarget);
                else lookedUp.Add(found);
            }
            selected = TextTreeSelectionService.AddLookedUp(selected, lookedUp, args);
            if (selected.Count == 0)
            {
                if (unresolved.Count > 0)
                {
                    // The read's own not-found reply: the index-warming hint appears only while the index is not ready.
                    errorResponse = HealingService.FormatNotFoundError(unresolved[0].Name, indexReady ? loadedIndex : null);
                    return false;
                }
                errorCode = "NoObjectsMatched";
                errorMessage = "No KB objects matched the export selector.";
                errorHint = "Wait for the index and use targets[], name, type, module or pathPrefix.";
                return false;
            }

            bool includeDependencies = args["includeDependencies"]?.ToObject<bool?>()
                ?? args["includeReferences"]?.ToObject<bool?>()
                ?? false;
            bool includeModuleMetadata = args["includeModuleMetadata"]?.ToObject<bool?>() ?? true;
            bool includeModulePackages = args["includeModulePackages"]?.ToObject<bool?>() ?? includeDependencies;
            bool includeTableProjections = args["includeTableProjections"]?.ToObject<bool?>() ?? false;
            List<string> requestedParts = ResolveRequestedParts(args);
            string part = requestedParts.Count == 1 ? requestedParts[0] : "all";
            // The objects the request chose (named, or picked by a filter), as opposed to those a dependency brought in.
            var chosenKeys = new HashSet<string>(selected.Select(TextTreeSelectionService.ObjectKey), StringComparer.OrdinalIgnoreCase);
            List<ExportPlan> plan = BuildPlan(selected, includeDependencies, includeTableProjections, part, root);
            foreach (ExportPlan item in plan)
            {
                if (item.IsTableProjection) continue;
                item.Parts = requestedParts;
                item.VersionToken = BuildVersionToken(item.Entry, string.Join(",", requestedParts));
            }
            if (plan.Count == 0)
            {
                errorCode = "NoObjectsMatched";
                errorMessage = "The export plan contains no serializable KB objects.";
                return false;
            }

            state = new NativeExportState
            {
                Root = root,
                Mode = mode,
                ManifestPath = Path.Combine(root, ManifestFileName),
                Options = options,
                IncludeDependencies = includeDependencies,
                IncludeVisualParts = options.IncludeVisualParts,
                IncludeModuleMetadata = includeModuleMetadata,
                IncludeModulePackages = includeModulePackages,
                IncludeTableProjections = includeTableProjections,
                FullSelection = IsFullSelection(target, args),
                HasExplicitTargets = explicitTargets.Count > 0,
                Plan = plan,
                OldManifest = LoadManifest(root),
                OldMetadata = LoadManifestMetadata(root),
                OldPackages = LoadManifestArray(root, "packages"),
                PlanKeys = new HashSet<string>(plan.Select(item => ManifestKey(item.Entry)), StringComparer.OrdinalIgnoreCase),
                HomonymIndexes = FindHomonymIndexes(plan, chosenKeys)
            };
            state.Skipped = Math.Min(options.Skip, plan.Count);
            return true;
        }

        // The named targets of the request, without the [all] and wildcard selectors.
        private static List<TextSelector> ExplicitTargets(string target, JObject args)
        {
            return TextTreeSelectionService.ReadSelectors(target, args)
                .Where(selector => !TextSelectorParser.IsAll(selector)
                    && !string.IsNullOrWhiteSpace(selector.Name)
                    && selector.Name.IndexOf('*') < 0)
                .ToList();
        }

        // The SDK answers an explicit target the index does not hold: by identity when the target is a GUID or an
        // EntityKey, by name and type otherwise. The entry it returns is built without writing to the index.
        private SearchIndex.IndexEntry LookupSdkEntry(TextSelector explicitTarget)
        {
            if (_objectService == null || _indexCacheService == null) return null;
            (string name, string guid, string entityKey) = LookupIdentity(explicitTarget);
            var obj = _objectService.FindObject(name, explicitTarget.Type, guid, entityKey);
            return obj == null ? null : _indexCacheService.BuildLookupEntry(obj);
        }

        // The argument of the lookup for one explicit target: a GUID, an EntityKey or a name, and only one of them.
        internal static (string Name, string Guid, string EntityKey) LookupIdentity(TextSelector explicitTarget)
        {
            bool isGuid = Guid.TryParse(explicitTarget.Name, out _);
            bool isEntityKey = !isGuid && ObjectService.TryParseEntityKey(explicitTarget.Name, out _, out _);
            return (
                isGuid || isEntityKey ? null : explicitTarget.Name,
                isGuid ? explicitTarget.Name : null,
                isEntityKey ? explicitTarget.Name : null);
        }

        // The native tree keeps one object per Type:Name, and the manifest and the validator key it that way. Of the objects
        // that share a Type:Name, the one the request chose is written, ahead of one that came only as a dependency; between
        // two of the same kind, the first by path. The others are reported instead. A table projection is not an object of
        // its own: it is written under #tables and never competes for the name.
        private static HashSet<int> FindHomonymIndexes(List<ExportPlan> plan, HashSet<string> chosenKeys)
        {
            var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < plan.Count; index++)
            {
                if (plan[index].IsTableProjection) continue;
                string key = ManifestKey(plan[index].Entry);
                if (key == null) continue;
                if (!groups.TryGetValue(key, out List<int> members)) groups[key] = members = new List<int>();
                members.Add(index);
            }
            var result = new HashSet<int>();
            foreach (List<int> members in groups.Values)
            {
                if (members.Count < 2) continue;
                int winner = members
                    .OrderBy(member => chosenKeys.Contains(TextTreeSelectionService.ObjectKey(plan[member].Entry)) ? 0 : 1)
                    .ThenBy(member => TextTreePath.NormalizePath(plan[member].Entry.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .First();
                foreach (int member in members)
                {
                    if (member != winner) result.Add(member);
                }
            }
            return result;
        }

        private const string HomonymCode = "HomonymInSameTree";
        private const string HomonymHint = "The native tree keeps one object per Type:Name: export the other homonym into a separate root (outputPath).";

        private static ReadFailure HomonymFailure(ExportPlan item)
        {
            return new ReadFailure
            {
                Code = HomonymCode,
                Message = "'" + item.Entry.Name + "' (" + item.Entry.Type + ") is a homonym of an object planned earlier in this export.",
                Hint = HomonymHint
            };
        }

        private static string BuildNativeExportPlan(NativeExportState state)
        {
            var planResults = new JArray();
            int offset = Math.Min(state.Options.Skip, state.Plan.Count);
            foreach (ExportPlan item in state.Plan)
            {
                int index = planResults.Count;
                bool homonym = index >= offset && state.HomonymIndexes.Contains(index);
                var planItem = new JObject
                {
                    ["name"] = item.Entry.Name,
                    ["type"] = item.Entry.Type,
                    ["role"] = item.Role,
                    ["part"] = item.Part,
                    ["file"] = item.RelativePath,
                    ["status"] = index < offset ? "skipped" : homonym ? "error" : "planned",
                    ["reason"] = index < offset ? "skip" : "listOnly"
                };
                // The code and the hint the export gives this object, so a client reads the same fields in either response.
                if (homonym)
                {
                    planItem["code"] = HomonymCode;
                    planItem["hint"] = HomonymHint;
                }
                planResults.Add(planItem);
            }
            return McpResponse.Ok(code: "ObjectTextNativeExportPlan", result: new JObject
            {
                ["format"] = "native",
                ["mode"] = state.Mode,
                ["listOnly"] = true,
                ["root"] = state.Root,
                ["planned"] = state.Plan.Count,
                ["filesAvailable"] = state.Plan.Count,
                ["skipped"] = offset,
                ["remaining"] = Math.Max(0, state.Plan.Count - offset),
                ["objects"] = planResults
            });
        }

        private static bool TryCreateNativeExportRoot(NativeExportState state, out string error)
        {
            error = null;
            try
            {
                if (!Directory.Exists(state.Root)) Directory.CreateDirectory(state.Root);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void ExportNativeObjects(NativeExportState state, JObject args, CancellationToken ct)
        {
            for (int index = 0; index < state.Plan.Count; index++)
            {
                if (ct.IsCancellationRequested)
                {
                    state.CancelledResponse = BuildCancelled(state.Root, state.Results, state.ManifestItems,
                        state.Plan.Count, state.Exported, state.Skipped, state.NoTextParts, state.Failed, state.FilesWritten, state.BytesWritten);
                    return;
                }
                ExportNativeObject(state, args, state.Plan[index], index);
                if (state.StoppedOnError) return;
            }
        }

        private void ExportNativeObject(NativeExportState state, JObject args, ExportPlan item, int index)
        {
            if (index < state.Options.Skip)
            {
                state.Results.Add(new JObject
                {
                    ["name"] = item.Entry.Name,
                    ["type"] = item.Entry.Type,
                    ["role"] = item.Role,
                    ["part"] = item.Part,
                    ["file"] = item.RelativePath,
                    ["status"] = "skipped",
                    ["reason"] = "skip"
                });
                return;
            }

            // A homonym is reported, not written: the tree has one object per Type:Name (see FindHomonymIndexes).
            if (state.HomonymIndexes.Contains(index))
            {
                RegisterNativeExportError(state, item, HomonymFailure(item));
                return;
            }

            JObject oldItem = state.OldManifest.TryGetValue(ManifestKey(item.Entry), out JObject previous) ? previous : null;
            if (ShouldSkip(item, oldItem, state.Mode))
            {
                state.Skipped++;
                state.Results.Add(BuildResult(item, "skipped", new FileInfo(item.FilePath).Length, null));
                state.ManifestItems.Add(BuildManifestItem(item, oldItem, null, CloneArray(oldItem?["companions"] as JArray)));
                state.EmittedKeys.Add(ManifestKey(item.Entry));
                return;
            }

            if (!state.Options.Overwrite && state.Mode == "all" && File.Exists(item.FilePath))
            {
                RegisterNativeExportError(state, item, "Output file already exists; pass overwrite=true or use an incremental mode.");
                return;
            }

            if (!TryBuildNativeDocument(item, args, out string document, out ReadFailure failure))
            {
                RegisterNativeExportError(state, item, failure);
                return;
            }
            try
            {
                WriteTextAtomically(item.FilePath, document, state.Options.Overwrite || state.Mode != "all");
                long bytes = new FileInfo(item.FilePath).Length;
                state.Exported++;
                state.FilesWritten++;
                state.BytesWritten += bytes;
                var companions = new JArray();
                if (state.IncludeVisualParts && IsVisualCandidate(item.Entry.Type))
                    ExportVisualCompanion(item, state.Root, state.Mode, state.Options.Overwrite, companions,
                        ref state.FilesWritten, ref state.CompanionFilesWritten, ref state.BytesWritten);
                state.Results.Add(BuildResult(item, "ok", bytes, null));
                state.ManifestItems.Add(BuildManifestItem(item, null, document, companions));
                state.EmittedKeys.Add(ManifestKey(item.Entry));
            }
            catch (Exception ex)
            {
                RegisterNativeExportError(state, item, ex.Message);
            }
        }

        private bool TryBuildNativeDocument(ExportPlan item, JObject args, out string document, out ReadFailure failure)
        {
            document = null;
            failure = null;
            try
            {
                Dictionary<string, string> parts;
                if (item.Parts != null && item.Parts.Count > 1)
                {
                    if (!TryReadParts(item.ReadEntry ?? item.Entry, item.Parts, out parts, out ReadFailure partsFailure))
                        return TryBuildWwpTemplateDocument(item, partsFailure, out document, out failure);
                }
                else
                {
                    if (!TryReadPart(item.ReadEntry ?? item.Entry, item.Part, out string source, out ReadFailure partFailure))
                        return TryBuildWwpTemplateDocument(item, partFailure, out document, out failure);
                    parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [item.Part] = source
                    };
                }
                item.Parts = parts.Keys.ToList();
                bool simpleSource = parts.Count == 1 && parts.ContainsKey("Source") && item.Parts.Count == 1;
                document = simpleSource
                    ? BuildObjectDocument(item.Entry.Type, item.Entry.Name, parts["Source"], args["indentString"]?.ToString())
                    : BuildObjectDocumentParts(item.Entry.Type, item.Entry.Name, parts, args["indentString"]?.ToString());
                return true;
            }
            catch (Exception ex)
            {
                failure = new ReadFailure { Message = ex.Message };
                return false;
            }
        }

        // An object with no text part may still be a WorkWithPlus template, whose data are SDK properties: the export writes
        // them in the layout of the official text. Any other object keeps the no-text-parts failure it was read with, and an
        // object whose properties cannot be read fails alone, as the other objects of the export do.
        private bool TryBuildWwpTemplateDocument(ExportPlan item, ReadFailure noTextParts, out string document, out ReadFailure failure)
        {
            document = null;
            failure = noTextParts;
            if (noTextParts?.Code != NoTextPartsCode) return false;

            SearchIndex.IndexEntry entry = item.ReadEntry ?? item.Entry;
            string typedName = (entry.Type ?? string.Empty) + ":" + entry.Name;
            JObject response = ParseEnvelope(_readTemplateProperties(typedName, entry.Type, entry.Guid, entry.EntityKey, entry.Path));
            if (ReplyCode(response) == NotWwpTemplateCode) return false;
            if (!IsSameObject(entry, response, out failure)) return false;
            if (!(response["properties"] is JObject values))
            {
                failure = FailureOf(entry, response, "The WorkWithPlus template's properties could not be read.");
                return false;
            }

            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JProperty property in values.Properties())
                named[property.Name] = property.Value.Type == JTokenType.Null ? string.Empty : property.Value.ToString();
            document = WwpTemplateDocument.Build(entry.Name, named);
            item.Parts = new List<string> { WwpTemplateDocument.Section };
            return true;
        }

        private static void RegisterNativeExportError(NativeExportState state, ExportPlan item, string message)
        {
            RegisterNativeExportError(state, item, new ReadFailure { Message = message });
        }

        private static void RegisterNativeExportError(NativeExportState state, ExportPlan item, ReadFailure failure)
        {
            // A KB-wide export cannot give text to an object that has none (a WorkWithPlus template keeps its data in SDK
            // properties). That object is counted and reported, and the rest of the export still completes. An object the
            // request named stays a failure: its text was asked for.
            if (failure.Code == NoTextPartsCode && !state.HasExplicitTargets)
            {
                state.NoTextParts++;
                state.Results.Add(BuildResult(item, "skipped", 0, failure.Message, failure.Code, failure.Hint));
                return;
            }
            state.Failed++;
            state.Results.Add(BuildResult(item, "error", 0, failure.Message, failure.Code, failure.Hint));
            if (state.Options.StopOnError) state.StoppedOnError = true;
        }

        private void WriteNativeExportAuxiliaryFiles(NativeExportState state)
        {
            if (state.IncludeModuleMetadata && !state.StoppedOnError)
            {
                WriteModuleMetadata(
                    state.Plan,
                    state.Root,
                    state.Mode,
                    state.Options.Overwrite,
                    state.OldMetadata,
                    state.MetadataItems,
                    state.EmittedMetadata,
                    ref state.FilesWritten,
                    ref state.BytesWritten,
                    ref state.MetadataFilesWritten,
                    ref state.MetadataFilesSkipped,
                    ref state.Failed,
                    ref state.StoppedOnError,
                    state.Options.StopOnError);
            }
            if (state.IncludeModulePackages && !state.StoppedOnError)
            {
                WriteModulePackages(
                    state.Plan,
                    state.Root,
                    state.Mode,
                    state.Options.Overwrite,
                    state.OldPackages,
                    state.PackageItems,
                    state.EmittedPackages,
                    ref state.FilesWritten,
                    ref state.BytesWritten,
                    ref state.PackageFilesWritten,
                    ref state.PackageFilesSkipped,
                    ref state.Failed,
                    ref state.StoppedOnError,
                    state.Options.StopOnError);
            }
        }

        private void ReconcileNativeExport(NativeExportState state)
        {
            // Files an earlier export wrote are removed only when this export covered the whole KB and finished. A limit,
            // an ignore list, a skip, a failed object or a stop leaves part of the KB unexported, so its files stay.
            // The skip is part of FullSelection, so it is not repeated here. An object without text parts is counted, not failed.
            bool complete = state.Mode == "all" && state.FullSelection && state.Failed == 0 && !state.StoppedOnError;
            foreach (JObject previous in state.OldManifest.Values)
            {
                string key = ManifestKey(previous["type"]?.ToString(), previous["name"]?.ToString());
                bool keep = !state.EmittedKeys.Contains(key)
                    && (!complete || state.PlanKeys.Contains(key));
                if (keep) state.ManifestItems.Add((JObject)previous.DeepClone());
            }
            foreach (JObject previous in state.OldMetadata.Values)
            {
                string key = NormalizePath(previous["file"]?.ToString());
                bool keep = !state.EmittedMetadata.Contains(key)
                    && (!complete || !state.IncludeModuleMetadata);
                if (keep) state.MetadataItems.Add((JObject)previous.DeepClone());
            }
            foreach (JObject previous in state.OldPackages.Values)
            {
                string key = NormalizePath(previous["file"]?.ToString());
                bool keep = !state.EmittedPackages.Contains(key)
                    && (!complete || !state.IncludeModulePackages);
                if (keep) state.PackageItems.Add((JObject)previous.DeepClone());
            }
            if (complete)
            {
                state.FilesDeleted = RemoveStaleFiles(state.Root, state.OldManifest, state.PlanKeys);
                if (state.IncludeModuleMetadata)
                    state.FilesDeleted += RemoveStaleMetadata(state.Root, state.OldMetadata, state.EmittedMetadata);
                if (state.IncludeModulePackages)
                    state.FilesDeleted += RemoveStalePackages(state.Root, state.OldPackages, state.EmittedPackages);
            }

            state.ManifestItems = OrderManifestItems(state.ManifestItems);
            state.MetadataItems = OrderFileItems(state.MetadataItems);
            state.PackageItems = OrderFileItems(state.PackageItems);
        }

        private static JArray OrderManifestItems(JArray items)
        {
            var ordered = new JArray();
            foreach (JObject item in items.OfType<JObject>()
                .OrderBy(item => item["file"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item["type"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item["name"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                ordered.Add(item);
            return ordered;
        }

        private static JArray OrderFileItems(JArray items)
        {
            var ordered = new JArray();
            foreach (JObject item in items.OfType<JObject>()
                .OrderBy(item => item["file"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                ordered.Add(item);
            return ordered;
        }

        private void WriteNativeExportManifest(NativeExportState state)
        {
            try
            {
                var manifest = new JObject
                {
                    ["kind"] = "GeneXusSdkTextTree",
                    ["schemaVersion"] = 1,
                    ["format"] = "native",
                    ["mode"] = state.Mode,
                    ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                    ["objects"] = state.ManifestItems,
                    ["metadata"] = state.MetadataItems,
                    ["packages"] = state.PackageItems
                };
                WriteTextAtomically(state.ManifestPath, manifest.ToString(Formatting.Indented), true);
            }
            catch (Exception ex)
            {
                state.ManifestError = ex.Message;
                state.Failed++;
            }
        }

        private static string BuildNativeExportResponse(NativeExportState state)
        {
            var result = new JObject
            {
                ["format"] = "native",
                ["mode"] = state.Mode,
                ["root"] = state.Root,
                ["planned"] = state.Plan.Count,
                ["exported"] = state.Exported,
                ["skipped"] = state.Skipped,
                ["noTextParts"] = state.NoTextParts,
                ["failed"] = state.Failed,
                ["filesWritten"] = state.FilesWritten,
                ["companionFilesWritten"] = state.CompanionFilesWritten,
                ["filesDeleted"] = state.FilesDeleted,
                ["bytesWritten"] = state.BytesWritten,
                ["listOnly"] = false,
                ["filesAvailable"] = state.Plan.Count,
                ["remaining"] = Math.Max(0, state.Plan.Count - state.Exported - state.Skipped - state.NoTextParts - state.Failed),
                ["stoppedOnError"] = state.StoppedOnError,
                ["metadataFilesWritten"] = state.MetadataFilesWritten,
                ["metadataFilesSkipped"] = state.MetadataFilesSkipped,
                ["metadata"] = state.MetadataItems,
                ["packageFilesWritten"] = state.PackageFilesWritten,
                ["packageFilesSkipped"] = state.PackageFilesSkipped,
                ["packages"] = state.PackageItems,
                ["manifestPath"] = state.ManifestPath,
                ["objects"] = state.Results
            };
            if (state.ManifestError != null) result["manifestError"] = state.ManifestError;
            return state.Failed > 0
                ? McpResponse.Partial(null, "ObjectTextNativeExportPartial", result)
                : McpResponse.Ok(code: "ObjectTextNativeExportCompleted", result: result);
        }
    }
}
