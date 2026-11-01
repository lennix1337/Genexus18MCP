using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class SdkTextTreeService
    {
        private sealed class NativeImportState
        {
            public string Root;
            public TextBatchOptions Options;
            public bool IncludeReferences;
            public List<string> Files;
            public JArray Results = new JArray();
            public Dictionary<string, NativeImportItem> ImportedByStem = new Dictionary<string, NativeImportItem>(StringComparer.OrdinalIgnoreCase);
            public int Succeeded;
            public int Failed;
            public int Processed;
            public int TemplatesNotImported;
            public bool StoppedOnError;
            public string CancelledResponse;
        }

        private string RunNativeImport(string target, JObject args, CancellationToken ct)
        {
            if (!TryPrepareNativeImport(target, args, out NativeImportState state,
                out string errorCode, out string errorMessage, out string errorTarget))
                return McpResponse.Err(code: errorCode, message: errorMessage, target: errorTarget);

            ImportNativeFiles(state, args, target, ct);
            if (state.CancelledResponse != null) return state.CancelledResponse;
            if (state.Options.IncludeVisualParts && !state.Options.ListOnly && !state.StoppedOnError)
                ImportVisualCompanions(state.Root, args, state.Options.DryRun, state.Options.ForceSave,
                    state.Options.StopOnError, state.ImportedByStem, state.Results, ref state.Succeeded, ref state.Failed, ct);
            return BuildNativeImportResponse(state);
        }

        private bool TryPrepareNativeImport(string target, JObject args, out NativeImportState state,
            out string errorCode, out string errorMessage, out string errorTarget)
        {
            state = null;
            errorCode = null;
            errorMessage = null;
            errorTarget = null;
            string inputPath = args["inputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                errorCode = "InputPathRequired";
                errorMessage = "inputPath is required for an SDK text-tree import.";
                return false;
            }
            if (_objectService == null)
            {
                errorCode = "SdkTextTreeUnavailable";
                errorMessage = "The SDK text-tree importer is not connected to ObjectService.";
                return false;
            }

            string full;
            try { full = Path.GetFullPath(inputPath); }
            catch (Exception ex)
            {
                errorCode = "InvalidInputPath";
                errorMessage = ex.Message;
                errorTarget = inputPath;
                return false;
            }
            string root = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            if (!Directory.Exists(root))
            {
                errorCode = "InputDirectoryNotFound";
                errorMessage = "The SDK text-tree input directory does not exist.";
                errorTarget = root;
                return false;
            }
            if (!TextBatchOptions.TryParse(args, true, false, false, true, true, false, true,
                out TextBatchOptions options, out string optionError))
            {
                errorCode = "InvalidTextOperationOption";
                errorMessage = optionError;
                return false;
            }
            if (!options.DryRun && !options.Confirm)
            {
                errorCode = "ConfirmRequired";
                errorMessage = "A native text-tree import with dryRun=false requires confirm=true.";
                return false;
            }

            bool includeReferences = args["includeReferences"]?.ToObject<bool?>()
                ?? args["includeDependencies"]?.ToObject<bool?>()
                ?? args["importMissingReferences"]?.ToObject<bool?>()
                ?? false;
            List<string> files = CollectNativeObjectFiles(full, root, includeReferences, options.IncludeChildren);
            if (files.Count == 0)
            {
                errorCode = "NoNativeObjectFiles";
                errorMessage = "No .gx object files were found under the SDK text tree.";
                return false;
            }

            state = new NativeImportState
            {
                Root = root,
                Options = options,
                IncludeReferences = includeReferences,
                Files = files
            };
            return true;
        }

        private void ImportNativeFiles(NativeImportState state, JObject args, string target, CancellationToken ct)
        {
            for (int index = 0; index < state.Files.Count; index++)
            {
                if (ct.IsCancellationRequested)
                {
                    state.CancelledResponse = BuildImportCancelled(state.Root, state.Results,
                        state.Files.Count, state.Succeeded, state.Failed, state.Options.DryRun);
                    return;
                }
                if (index < state.Options.Skip)
                {
                    state.Results.Add(new JObject
                    {
                        ["file"] = MakeRelative(state.Root, state.Files[index]),
                        ["status"] = "skipped",
                        ["reason"] = "skip"
                    });
                    continue;
                }
                ImportNativeFile(state, args, target, state.Files[index]);
                if (state.StoppedOnError) return;
            }
        }

        private void ImportNativeFile(NativeImportState state, JObject args, string target, string file)
        {
            string document;
            try { document = File.ReadAllText(file); }
            catch (Exception ex)
            {
                state.Failed++;
                state.Processed++;
                AddNativeImportError(state, file, ex.Message);
                return;
            }

            if (!TryParseObjectDocumentParts(document, out string type, out string name,
                out Dictionary<string, string> parts, out string parseError))
            {
                state.Failed++;
                state.Processed++;
                AddNativeImportError(state, file, parseError);
                return;
            }
            if (!MatchesImportSelector(type, name, target, args)) return;
            state.Processed++;
            if (ObjectCreateTypes.IsWorkWithPlusTemplate(type))
            {
                AddWwpTemplateRefusal(state, file, name, type, NamesImportObject(type, name, target, args));
                return;
            }

            string selectedPart = string.IsNullOrWhiteSpace(args["part"]?.ToString()) ? null : args["part"].ToString();
            if (!SelectNativeImportPart(parts, selectedPart, name, type, file, state)) return;
            if (state.Options.ListOnly)
            {
                state.Results.Add(new JObject
                {
                    ["name"] = name,
                    ["type"] = type,
                    ["parts"] = BuildPartArray(parts.Keys),
                    ["file"] = MakeRelative(state.Root, file),
                    ["status"] = "planned",
                    ["reason"] = "listOnly"
                });
                return;
            }

            var partResults = new JArray();
            bool objectOk = true;
            string firstPartError = null;
            Dictionary<string, string> priorParts = !state.Options.DryRun && state.Options.RollbackOnFailure && parts.Count > 1
                ? TryReadPartsForRollback(type, name, parts.Keys)
                : null;
            bool snapshotComplete = priorParts != null && parts.Keys.All(part => priorParts.ContainsKey(part));
            string rollbackPrecondition = ValidateRollbackPrecondition(
                state.Options.DryRun, state.Options.RollbackOnFailure, parts.Count, snapshotComplete);
            if (rollbackPrecondition != null)
            {
                state.Failed++;
                state.Results.Add(new JObject
                {
                    ["name"] = name,
                    ["type"] = type,
                    ["parts"] = BuildPartArray(parts.Keys),
                    ["file"] = MakeRelative(state.Root, file),
                    ["status"] = "error",
                    ["code"] = rollbackPrecondition,
                    ["message"] = "A complete rollback snapshot could not be read; no part was written."
                });
                if (state.Options.StopOnError) state.StoppedOnError = true;
                return;
            }

            foreach (KeyValuePair<string, string> part in parts)
            {
                JObject response = ParseEnvelope(_objectService.ImportObjectPartText(
                    name, part.Key, part.Value, type, state.Options.DryRun, state.Options.ForceSave));
                bool partOk = IsSuccess(response);
                partResults.Add(new JObject
                {
                    ["part"] = part.Key,
                    ["status"] = partOk ? "ok" : "error",
                    ["response"] = response
                });
                if (!partOk)
                {
                    objectOk = false;
                    firstPartError = firstPartError ?? response["error"]?.ToString()
                        ?? response["message"]?.ToString() ?? "SDK import failed.";
                    if (state.Options.RollbackOnFailure) break;
                }
            }

            JObject rollback = null;
            if (!objectOk && !state.Options.DryRun && state.Options.RollbackOnFailure && priorParts != null)
                rollback = RestorePartsAfterFailure(type, name, priorParts, partResults);
            if (objectOk)
            {
                state.Succeeded++;
                state.ImportedByStem[Path.GetFileNameWithoutExtension(file)] = new NativeImportItem
                {
                    Name = name,
                    Type = type,
                    File = file
                };
                state.Results.Add(new JObject
                {
                    ["name"] = name,
                    ["type"] = type,
                    ["parts"] = BuildPartArray(parts.Keys),
                    ["file"] = MakeRelative(state.Root, file),
                    ["status"] = "ok",
                    ["partResults"] = partResults
                });
            }
            else
            {
                state.Failed++;
                var failed = new JObject
                {
                    ["name"] = name,
                    ["type"] = type,
                    ["file"] = MakeRelative(state.Root, file),
                    ["status"] = "error",
                    ["message"] = firstPartError,
                    ["partResults"] = partResults
                };
                if (rollback != null) failed["rollback"] = rollback;
                state.Results.Add(failed);
                if (state.Options.StopOnError) state.StoppedOnError = true;
            }
        }

        private static bool SelectNativeImportPart(Dictionary<string, string> parts, string selectedPart,
            string name, string type, string file, NativeImportState state)
        {
            if (string.IsNullOrWhiteSpace(selectedPart) || string.Equals(selectedPart, "all", StringComparison.OrdinalIgnoreCase))
                return true;
            string selectedSource = parts.FirstOrDefault(pair =>
                string.Equals(pair.Key, selectedPart, StringComparison.OrdinalIgnoreCase)).Value;
            if (selectedSource != null)
            {
                parts.Clear();
                parts[selectedPart] = selectedSource;
                return true;
            }
            state.Failed++;
            state.Results.Add(new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["part"] = selectedPart,
                ["file"] = MakeRelative(state.Root, file),
                ["status"] = "error",
                ["message"] = "The requested part was not present in the native document."
            });
            if (state.Options.StopOnError) state.StoppedOnError = true;
            return false;
        }

        private const string WwpTemplateImportRefusedCode = "WwpTemplateImportRefused";
        private const string WwpTemplateNotImportedCode = "WwpTemplateNotImported";

        // The native import does not write templates: their data are SDK properties, changed with genexus_properties. A tree the
        // export wrote holds templates, so a template file of a tree is skipped with its own code. It is counted apart, it is not
        // a failure, and it does not stop the batch. A template the request names as a target is an error, and stops with stopOnError.
        private static void AddWwpTemplateRefusal(NativeImportState state, string file, string name, string type, bool namedTarget)
        {
            var result = new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["file"] = MakeRelative(state.Root, file)
            };
            if (namedTarget)
            {
                state.Failed++;
                result["status"] = "error";
                result["code"] = WwpTemplateImportRefusedCode;
                result["message"] = "'" + name + "' is a WorkWithPlus template: the native import does not write templates, so nothing was written to the KB.";
                result["hint"] = "Change the template's properties with genexus_properties action=set, TemplateXml included.";
                state.Results.Add(result);
                if (state.Options.StopOnError) state.StoppedOnError = true;
                return;
            }
            state.TemplatesNotImported++;
            result["status"] = "skipped";
            result["code"] = WwpTemplateNotImportedCode;
            result["message"] = "'" + name + "' is a WorkWithPlus template: the native import does not write templates, so the file is skipped and nothing was written to the KB.";
            result["hint"] = "Change the template's properties with genexus_properties action=set. Naming the template as a target makes this an error.";
            state.Results.Add(result);
        }

        private static void AddNativeImportError(NativeImportState state, string file, string message)
        {
            state.Results.Add(new JObject
            {
                ["file"] = MakeRelative(state.Root, file),
                ["status"] = "error",
                ["message"] = message
            });
            if (state.Options.StopOnError) state.StoppedOnError = true;
        }

        private static string BuildNativeImportResponse(NativeImportState state)
        {
            var result = new JObject
            {
                ["format"] = "native",
                ["root"] = state.Root,
                ["dryRun"] = state.Options.DryRun,
                ["listOnly"] = state.Options.ListOnly,
                ["forceSave"] = state.Options.ForceSave,
                ["rollbackOnFailure"] = state.Options.RollbackOnFailure,
                ["includeReferences"] = state.IncludeReferences,
                ["includeChildren"] = state.Options.IncludeChildren,
                ["planned"] = state.Files.Count,
                ["succeeded"] = state.Succeeded,
                ["failed"] = state.Failed,
                ["templatesNotImported"] = state.TemplatesNotImported,
                ["skipped"] = Math.Min(state.Options.Skip, state.Files.Count),
                ["remaining"] = Math.Max(0, state.Files.Count - Math.Min(state.Options.Skip, state.Files.Count) - state.Processed),
                ["stoppedOnError"] = state.StoppedOnError,
                ["objects"] = state.Results
            };
            return state.Failed > 0
                ? McpResponse.Partial(null, "ObjectTextNativeImportPartial", result)
                : McpResponse.Ok(code: state.Options.DryRun ? "ObjectTextNativeImportDryRun" : "ObjectTextNativeImportCompleted", result: result);
        }
    }
}
