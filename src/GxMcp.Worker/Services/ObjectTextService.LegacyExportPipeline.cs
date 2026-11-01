using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class ObjectTextService
    {
        private sealed class LegacyExportState
        {
            public string Root;
            public string ManifestPath;
            public TextBatchOptions Options;
            public List<Tuple<SearchIndex.IndexEntry, string>> Plan;
            public JArray Results = new JArray();
            public JArray ManifestItems = new JArray();
            public int Succeeded;
            public int Failed;
            public bool StoppedOnError;
            public string ManifestError;
            public string CancelledResponse;
        }

        private string RunLegacyExport(string target, JObject args, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return BuildCancelled("export", null, new JArray(), 0, 0, 0);
            if (!TryPrepareLegacyExport(target, args, out LegacyExportState state,
                out string errorCode, out string errorMessage, out string errorHint, out string errorTarget))
                return McpResponse.Err(code: errorCode, message: errorMessage, hint: errorHint, target: errorTarget);
            if (state.Options.ListOnly) return BuildLegacyExportPlan(state);
            if (!TryCreateLegacyExportRoot(state, out string directoryError))
                return McpResponse.Err(code: "ExportDirectoryCreateFailed", message: directoryError, target: state.Root);

            ExportLegacyObjects(state, target, args, ct);
            if (state.CancelledResponse != null) return state.CancelledResponse;
            WriteLegacyManifest(state, args);
            return BuildLegacyExportResponse(state);
        }

        private bool TryPrepareLegacyExport(string target, JObject args, out LegacyExportState state,
            out string errorCode, out string errorMessage, out string errorHint, out string errorTarget)
        {
            state = null;
            errorCode = null;
            errorMessage = null;
            errorHint = null;
            errorTarget = null;
            string outputPath = args["outputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                errorCode = "OutputPathRequired";
                errorMessage = "outputPath is required for a KB Object Text export.";
                errorHint = "Provide a directory where the .gxtext files and manifest will be written.";
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
            if (!TextBatchOptions.TryParse(args, false, false, false, true, false, false, true,
                out TextBatchOptions options, out string optionError))
            {
                errorCode = "InvalidTextOperationOption";
                errorMessage = optionError;
                return false;
            }
            if (!TrySelectEntries(target, args, allowAll: true, out List<SearchIndex.IndexEntry> entries, out string selectionError, options.IncludeChildren))
            {
                errorCode = "ObjectSelectionFailed";
                errorMessage = selectionError;
                errorTarget = target;
                return false;
            }
            if (entries.Count == 0)
            {
                errorCode = "NoObjectsMatched";
                errorMessage = "No KB objects matched the export selector.";
                errorHint = "Use targets[], type, module or pathPrefix that exists in the active index.";
                return false;
            }

            string manifestPath = Path.Combine(root, ManifestFileName);
            var plan = new List<Tuple<SearchIndex.IndexEntry, string>>();
            for (int index = 0; index < entries.Count; index++)
            {
                string fileName = TextTreePath.BuildObjectTextFileName(index, entries[index].Type, entries[index].Name);
                string filePath = Path.Combine(root, fileName);
                if (!options.ListOnly && !options.Overwrite && (File.Exists(filePath) || File.Exists(manifestPath)))
                {
                    errorCode = "FileAlreadyExists";
                    errorMessage = "The Object Text export would overwrite an existing file.";
                    errorHint = "Pass overwrite=true or choose an empty output directory.";
                    errorTarget = filePath;
                    return false;
                }
                plan.Add(Tuple.Create(entries[index], filePath));
            }
            state = new LegacyExportState
            {
                Root = root,
                ManifestPath = manifestPath,
                Options = options,
                Plan = plan
            };
            return true;
        }

        private static string BuildLegacyExportPlan(LegacyExportState state)
        {
            int offset = Math.Min(state.Options.Skip, state.Plan.Count);
            var objects = new JArray();
            for (int index = 0; index < state.Plan.Count; index++)
            {
                var item = state.Plan[index];
                objects.Add(new JObject
                {
                    ["name"] = item.Item1.Name,
                    ["type"] = item.Item1.Type,
                    ["file"] = TextTreePath.NormalizePath(PathSafety.MakeRelative(state.Root, item.Item2)),
                    ["status"] = index < offset ? "skipped" : "planned",
                    ["reason"] = index < offset ? "skip" : "listOnly"
                });
            }
            return McpResponse.Ok(code: "ObjectTextExportPlan", result: new JObject
            {
                ["operation"] = "export",
                ["listOnly"] = true,
                ["root"] = state.Root,
                ["planned"] = state.Plan.Count,
                ["filesAvailable"] = state.Plan.Count,
                ["skipped"] = offset,
                ["remaining"] = Math.Max(0, state.Plan.Count - offset),
                ["objects"] = objects
            });
        }

        private static bool TryCreateLegacyExportRoot(LegacyExportState state, out string error)
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

        private void ExportLegacyObjects(LegacyExportState state, string target, JObject args, CancellationToken ct)
        {
            for (int index = 0; index < state.Plan.Count; index++)
            {
                if (ct.IsCancellationRequested)
                {
                    state.CancelledResponse = BuildCancelled("export", state.Root, state.Results,
                        state.Succeeded, state.Failed, state.Plan.Count);
                    return;
                }
                if (index < state.Options.Skip)
                {
                    var skipped = state.Plan[index];
                    state.Results.Add(new JObject
                    {
                        ["name"] = skipped.Item1.Name,
                        ["type"] = skipped.Item1.Type,
                        ["file"] = TextTreePath.NormalizePath(PathSafety.MakeRelative(state.Root, skipped.Item2)),
                        ["status"] = "skipped",
                        ["reason"] = "skip"
                    });
                    continue;
                }

                var item = state.Plan[index];
                JObject parsed;
                try
                {
                    string raw = _objectService.ExportObjectToText(
                        item.Item1.Type + ":" + TextSelectorParser.QualifiedName(item.Item1),
                        item.Item2,
                        args["part"]?.ToString(),
                        item.Item1.Type,
                        overwrite: true);
                    parsed = ParseResult(raw);
                }
                catch (Exception ex)
                {
                    parsed = new JObject
                    {
                        ["status"] = "error",
                        ["code"] = "ObjectTextExportFailed",
                        ["message"] = ex.Message
                    };
                }
                state.Results.Add(BuildItemResult(item.Item1, item.Item2, parsed));
                if (IsSuccess(parsed))
                {
                    state.Succeeded++;
                    state.ManifestItems.Add(new JObject
                    {
                        ["name"] = item.Item1.Name,
                        ["type"] = item.Item1.Type,
                        ["part"] = args["part"]?.ToString() ?? "Source",
                        ["file"] = Path.GetFileName(item.Item2)
                    });
                }
                else
                {
                    state.Failed++;
                    if (state.Options.StopOnError)
                    {
                        state.StoppedOnError = true;
                        return;
                    }
                }
            }
        }

        private static void WriteLegacyManifest(LegacyExportState state, JObject args)
        {
            try
            {
                var manifest = new JObject
                {
                    ["kind"] = ManifestKind,
                    ["schemaVersion"] = 1,
                    ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                    ["part"] = args["part"]?.ToString() ?? "Source",
                    ["objects"] = state.ManifestItems
                };
                File.WriteAllText(state.ManifestPath, manifest.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                state.ManifestError = ex.Message;
                state.Failed++;
            }
        }

        private static string BuildLegacyExportResponse(LegacyExportState state)
        {
            var result = BuildAggregateResult("export", state.Root, state.Results, state.Plan.Count,
                state.Succeeded, state.Failed);
            result["manifestPath"] = state.ManifestPath;
            result["listOnly"] = false;
            result["filesAvailable"] = state.Plan.Count;
            result["skipped"] = Math.Min(state.Options.Skip, state.Plan.Count);
            result["remaining"] = Math.Max(0, state.Plan.Count - Math.Min(state.Options.Skip, state.Plan.Count) - state.Succeeded - state.Failed);
            result["stoppedOnError"] = state.StoppedOnError;
            if (state.ManifestError != null) result["manifestError"] = state.ManifestError;
            return state.Failed > 0
                ? McpResponse.Partial(null, "ObjectTextExportPartial", result)
                : McpResponse.Ok(code: "ObjectTextExportCompleted", result: result);
        }
    }
}
