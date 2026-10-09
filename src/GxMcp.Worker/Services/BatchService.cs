using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class BatchService
    {
        private readonly KbService _kbService;
        private readonly WriteService _writeService;
        private readonly PatchService _patchService;
        private readonly ObjectService _objectService;
        private readonly List<BatchItem> _buffer = new List<BatchItem>();

        public BatchService(KbService kbService, WriteService writeService, PatchService patchService, ObjectService objectService)
        {
            _kbService = kbService;
            _writeService = writeService;
            _patchService = patchService;
            _objectService = objectService;
        }

        /// <summary>
        /// The completion envelope for a batch edit, shared by the three places one
        /// is produced: an empty change list, the transactional path, and the
        /// per-change path.
        ///
        /// The count, the per-change results and the elapsed time are the same three
        /// fields every time, so a client reading one of them learns the shape from
        /// any of them. Written out three times they could drift, and the empty-list
        /// case already reports a literal <c>0</c> for the duration where the other
        /// two read the stopwatch - the kind of difference that looks deliberate and
        /// is not, because no stopwatch has run at that point.
        /// </summary>
        private static string BatchEditCompleted(string target, int count, JArray results, System.Diagnostics.Stopwatch sw, bool dryRun = false)
        {
            var result = new JObject
            {
                ["count"] = count,
                ["results"] = results,
                ["duration"] = sw.ElapsedMilliseconds
            };
            if (dryRun)
            {
                result["dryRun"] = true;
                result["savePathExercised"] = false;
            }
            return McpResponse.Ok(target: target, code: "BatchEditCompleted", result: result);
        }

        public string BatchEdit(string target, JArray changes, bool dryRun = false)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int count = 0;
                var results = new JArray();

                if (changes == null || changes.Count == 0)
                    return BatchEditCompleted(target, 0, results, sw, dryRun);

                // Variables must validate as a complete request before another part can be saved.
                foreach (var change in changes)
                {
                    string part = change["part"]?.ToString() ?? "Source";
                    if (!VariablesTextReceipt.Applies(part)) continue;
                    string preview = change["mode"]?.ToString() == "patch"
                        ? _patchService.ApplyPatch(target, part, change["operation"]?.ToString() ?? "Replace",
                            change["content"]?.ToString(), change["context"]?.ToString(),
                            change["expectedCount"]?.ToObject<int?>() ?? 1, dryRun: true,
                            replaceAll: change["replaceAll"]?.ToObject<bool?>() ?? false)
                        : _writeService.WriteObject(target, part, change["content"]?.ToString(), dryRun: true);
                    if (JObject.Parse(preview)["status"]?.ToString() == "error") return preview;
                }

                bool allDirect = !dryRun;
                foreach (var change in changes)
                {
                    string mode = change["mode"]?.ToString();
                    bool itemDryRun = dryRun || (change["dryRun"]?.ToObject<bool?>() ?? false);
                    if (mode == "patch" || itemDryRun || VariablesTextReceipt.Applies(change["part"]?.ToString())) { allDirect = false; break; }
                }

                if (allDirect && changes.Count > 1)
                {
                    foreach (var change in changes)
                    {
                        string partName = change["part"]?.ToString() ?? "Source";
                        string styleError = ThemeStyleEditHelper.IsStylePartName(partName)
                            ? ThemeStyleEditHelper.ValidateInlineDataUris(change["content"]?.ToString()) : null;
                        if (styleError != null)
                            return McpResponse.Err(code: "ThemeStyleValidationFailed", message: styleError, target: target);
                        if (!TextPayloadGuard.AppliesToPart(partName)) continue;

                        string literalLineBreakError = TextPayloadGuard.BuildWriteError(
                            target, partName, "content", change["content"]?.ToString());
                        if (literalLineBreakError != null) return literalLineBreakError;
                    }

                    var obj = _objectService.FindObject(target);
                    if (obj != null)
                    {
                        using (var trans = obj.Model.KB.BeginTransaction())
                        {
                            bool ok = false;
                            try
                            {
                                foreach (var change in changes)
                                {
                                    string partName = change["part"]?.ToString() ?? "Source";
                                    string content = change["content"]?.ToString();
                                    var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, partName);
                                    if (part == null)
                                        throw new Exception($"Part '{partName}' not found on object '{target}'.");

                                    if (part is Artech.Architecture.Common.Objects.ISource srcPart)
                                    {
                                        srcPart.Source = content ?? "";
                                    }
                                    else
                                    {
                                        var prop = part.GetType().GetProperty("Source") ?? part.GetType().GetProperty("Content");
                                        if (prop == null)
                                            throw new Exception($"Part '{partName}' does not expose a writable text Source/Content property.");
                                        prop.SetValue(part, content ?? "");
                                    }
                                    results.Add(new JObject { ["status"] = "ok", ["part"] = partName });
                                    count++;
                                }
                                obj.EnsureSave(check: false);
                                trans.Commit();
                                ok = true;
                                WriteService.NotePerTargetWrite(target);
                            }
                            finally
                            {
                                if (!ok) { try { trans.Rollback(); } catch { } }
                            }
                        }
                        return BatchEditCompleted(target, count, results, sw, dryRun);
                    }
                }

                foreach (var change in changes)
                {
                    string part = change["part"]?.ToString() ?? "Source";
                    string mode = change["mode"]?.ToString() ?? "patch";
                    string content = change["content"]?.ToString();
                    string context = change["context"]?.ToString();
                    string operation = change["operation"]?.ToString() ?? "Replace";
                    int expectedCount = change["expectedCount"]?.ToObject<int?>() ?? 1;
                    bool itemDryRun = dryRun || (change["dryRun"]?.ToObject<bool?>() ?? false);
                    bool replaceAll = change["replaceAll"]?.ToObject<bool?>() ?? false;

                    string result;
                    if (mode == "patch")
                    {
                        result = _patchService.ApplyPatch(target, part, operation, content, context, expectedCount, null, itemDryRun, verifyRollback: false, returnPostState: true, verbose: false, replaceAll: replaceAll);
                    }
                    else
                    {
                        result = _writeService.WriteObject(target, part, content, dryRun: itemDryRun);
                    }
                    
                    try {
                        results.Add(JObject.Parse(result));
                    } catch {
                        results.Add(new JObject { ["error"] = result });
                    }
                    count++;
                }

                return BatchEditCompleted(target, count, results, sw, dryRun);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "BatchEditFailed",
                    message: "BatchEdit failed: " + ex.Message,
                    hint: "Check each result item for per-change errors. Retry individual changes that failed.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = target },
                        why: "Inspect the target object to confirm its parts are available before retrying.")),
                    target: target);
            }
        }

        public string ProcessBatch(string action, string name, string code)
        {
            if (action == "Add")
            {
                _buffer.Add(new BatchItem { Name = name, Code = code });
                return McpResponse.Ok(target: name, code: "BatchItemBuffered", result: new JObject { ["bufferedCount"] = _buffer.Count });
            }
            else if (action == "Commit")
            {
                // Preflight every buffered source before the first write. This keeps a
                // malformed item from being reported as committed after the shared
                // text guard correctly refused its persistence.
                foreach (var item in _buffer)
                {
                    string literalLineBreakError = TextPayloadGuard.BuildWriteError(item.Name, "Source", "content", item.Code);
                    if (literalLineBreakError != null) return literalLineBreakError;
                }

                int count = 0;
                foreach (var item in _buffer)
                {
                    _writeService.WriteObject(item.Name, "Source", item.Code);
                    count++;
                }
                _buffer.Clear();
                return McpResponse.Ok(target: name, code: "BatchCommitted", result: new JObject { ["count"] = count });
            }
            return McpResponse.Err(
                code: "UnknownBatchAction",
                message: $"Unknown batch action '{action}'.",
                hint: "Supported batch actions are Add and Commit.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_batch",
                    args: new JObject { ["action"] = "Add", ["name"] = name },
                    why: "Use action=Add to queue an item, then action=Commit to flush all buffered writes.")),
                target: name);
        }

        private class BatchItem { public string Name; public string Code; }

        public string MultiEdit(JArray items)
        {
            try
            {
                if (items == null || items.Count == 0)
                    return McpResponse.Err(
                        code: "NoItemsProvided",
                        message: "No items provided.",
                        hint: "Pass a non-empty items array where each entry has name and changes.");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var allResults = new JArray();
                int totalChanges = 0;

                var grouped = new System.Collections.Generic.Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (item is JObject jo)
                    {
                        string name = (jo["name"] ?? jo["target"])?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;

                        if (jo["changes"] is JArray chArr)
                        {
                            if (!grouped.TryGetValue(name, out var list))
                            {
                                list = new JArray();
                                grouped[name] = list;
                            }
                            foreach (var ch in chArr) list.Add(ch);
                        }
                        else
                        {
                            if (!grouped.TryGetValue(name, out var list))
                            {
                                list = new JArray();
                                grouped[name] = list;
                            }
                            list.Add(jo);
                        }
                    }
                }

                foreach (var kvp in grouped)
                {
                    string name = kvp.Key;
                    var changes = kvp.Value;

                    string result = BatchEdit(name, changes);
                    try {
                        var parsed = JObject.Parse(result);
                        parsed["object"] = name;
                        allResults.Add(parsed);
                        totalChanges += parsed["result"]?["count"]?.ToObject<int>() ?? 0;
                    } catch {
                        allResults.Add(new JObject { ["object"] = name, ["error"] = result });
                    }
                }

                return McpResponse.Ok(
                    code: "MultiEditCompleted",
                    result: new JObject
                    {
                        ["objectCount"] = grouped.Count,
                        ["totalChanges"] = totalChanges,
                        ["results"] = allResults,
                        ["duration"] = sw.ElapsedMilliseconds
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "MultiEditFailed",
                    message: "MultiEdit failed: " + ex.Message,
                    hint: "Check the results array for per-object errors and retry failed objects individually.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_edit",
                        args: new JObject { ["target"] = "<object-name>", ["part"] = "Source" },
                        why: "Retry a single-object edit to isolate which object caused the failure.")));
            }
        }
        /// <summary>
        /// Clamps a requested page and page size into the range this API accepts.
        /// Compatible with net48 (no Math.Clamp).
        /// </summary>
        private static void ClampPage(ref int page, ref int pageSize)
        {
            page = Math.Max(page, 1);
            pageSize = Math.Min(Math.Max(pageSize, 1), 200);
        }

        /// <summary>
        /// Slices one page out of a list and reports the window, without naming a
        /// collection.
        ///
        /// The result and warning payloads are the same envelope around a different
        /// collection - one keyed <c>items</c>, one keyed <c>warnings</c> - and each
        /// was computing the window itself: the same clamps, the same
        /// <c>skip</c>/<c>hasMore</c> arithmetic and the same bounds-checked loop.
        /// That arithmetic decides which page a client sees and whether
        /// <c>has_more</c> tells it to ask again, so two copies is two chances for
        /// the two payloads to disagree about the page they are showing.
        ///
        /// The caller supplies the already-clamped values, because clamping changes
        /// them and the clamped page and page size are both reported back inside the
        /// envelope - so reading them from the result is not an option.
        /// </summary>
        private static JArray PageOf(IList<string> source, int page, int pageSize, out int total, out bool hasMore)
        {
            total = source == null ? 0 : source.Count;
            int skip = (page - 1) * pageSize;
            hasMore = skip + pageSize < total;

            var sliced = new JArray();
            if (source == null) return sliced;

            int end = Math.Min(skip + pageSize, total);
            for (int i = skip; i < end; i++)
                sliced.Add(source[i]);

            return sliced;
        }

        /// <summary>
        /// The <c>_meta.pagination</c> block both paginated payloads carry.
        /// </summary>
        private static JObject PaginationMeta(int total, int page, int pageSize, bool hasMore)
        {
            return new JObject
            {
                ["total"] = total,
                ["page"] = page,
                ["page_size"] = pageSize,
                ["has_more"] = hasMore
            };
        }

        /// <summary>
        /// Builds a paginated payload for lifecycle result items (errors list).
        /// Compatible with net48 (no Math.Clamp).
        /// </summary>
        public static JObject BuildResultPayload(IList<string> items, int page, int pageSize)
        {
            ClampPage(ref page, ref pageSize);
            var sliced = PageOf(items, page, pageSize, out int total, out bool hasMore);

            return new JObject
            {
                ["items"] = sliced,
                ["_meta"] = new JObject { ["pagination"] = PaginationMeta(total, page, pageSize, hasMore) }
            };
        }

        /// <summary>
        /// Builds a paginated payload for lifecycle status warnings.
        /// Compatible with net48 (no Math.Clamp).
        /// </summary>
        public static JObject BuildStatusPayload(IList<string> warnings, int page, int pageSize)
        {
            ClampPage(ref page, ref pageSize);
            var sliced = PageOf(warnings, page, pageSize, out int total, out bool hasMore);

            return new JObject
            {
                ["warnings"] = sliced,
                ["_meta"] = new JObject { ["pagination"] = PaginationMeta(total, page, pageSize, hasMore) }
            };
        }

        public string BatchRead(JArray items, string defaultPart = "Source", JArray requestedParts = null, bool refresh = false)
        {
            try
            {
                string unsupportedRefresh = ObjectService.FreshReadUnsupportedResponse(
                    null, refresh, GxMcp.Worker.Compatibility.DynamicSdkBridge.IsComDriver);
                if (unsupportedRefresh != null) return unsupportedRefresh;

                if (items == null || items.Count == 0)
                    return McpResponse.Err(
                        code: "NoItemsProvided",
                        message: "No items provided.",
                        hint: "Pass a non-empty items array where each entry is an object name (string) or an object with name and optionally part.");
                // no-nextStep: caller controls the items array; no specific tool call can resolve an empty input

                if (string.IsNullOrEmpty(defaultPart)) defaultPart = "Source";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = new JArray();

                foreach (var item in items)
                {
                    // genexus_read targets is an array of bare object-name strings
                    // (each item is a JValue), while the internal batch form allows
                    // {name, part} objects. Accept both so `targets:["A","B"]` no
                    // longer crashes with "Cannot access child value on JValue".
                    string name;
                    string part;
                    bool hasItemPart = false;
                    if (item is JObject itemObj)
                    {
                        name = itemObj["name"]?.ToString();
                        hasItemPart = !string.IsNullOrWhiteSpace(itemObj["part"]?.ToString());
                        part = itemObj["part"]?.ToString() ?? defaultPart;
                    }
                    else
                    {
                        name = item?.ToString();
                        part = defaultPart;
                    }
                    if (string.IsNullOrEmpty(name)) continue;

                    var selectedParts = requestedParts?
                        .Select(p => p?.ToString())
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToArray();
                    bool useFieldSelection = !hasItemPart && selectedParts != null && selectedParts.Length > 0;
                    string readResult = useFieldSelection
                        ? ObjectService.DescribeReadFreshness(_objectService.ReadObjectSourceParts(name, selectedParts, refresh: refresh), refresh)
                        : _objectService.ReadObjectSource(name, part, null, null, "mcp", refresh: refresh);
                    try {
                        var parsed = JObject.Parse(readResult);
                        parsed["object"] = name;
                        if (useFieldSelection)
                            parsed["requestedParts"] = new JArray(selectedParts);
                        else
                            parsed["part"] = part;
                        results.Add(parsed);
                    } catch {
                        var failed = new JObject { ["object"] = name, ["error"] = readResult };
                        if (useFieldSelection)
                            failed["requestedParts"] = new JArray(selectedParts);
                        else
                            failed["part"] = part;
                        results.Add(failed);
                    }
                }

                return McpResponse.Ok(
                    code: "BatchReadCompleted",
                    result: new JObject
                    {
                        ["count"] = results.Count,
                        ["results"] = results,
                        ["duration"] = sw.ElapsedMilliseconds
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "BatchReadFailed",
                    message: "BatchRead failed: " + ex.Message,
                    hint: "Check each result item for per-object errors and retry the failed reads individually.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = "<object-name>", ["part"] = "Source" },
                        why: "Retry a single-object read to isolate which object caused the failure.")));
            }
        }
    }
}
