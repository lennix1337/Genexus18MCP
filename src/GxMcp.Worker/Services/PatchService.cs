using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Structure;

namespace GxMcp.Worker.Services
{
    public class PatchService
    {
        private sealed class SourceCacheEntry
        {
            public string Source { get; set; }
            public string VersionToken { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }

        private sealed class ObjectMetadataSnapshot
        {
            public string Revision { get; set; }
            public string LastUpdate { get; set; }
            public KBObject Object { get; set; }
        }

        private static readonly ConcurrentDictionary<string, SourceCacheEntry> _sourceCache =
            new ConcurrentDictionary<string, SourceCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan SourceCacheTtl = TimeSpan.FromSeconds(20);

        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly PatternAnalysisService _patternAnalysisService;

        public PatchService(ObjectService objectService, WriteService writeService, PatternAnalysisService patternAnalysisService = null)
        {
            _objectService = objectService;
            _writeService = writeService;
            _patternAnalysisService = patternAnalysisService;
        }

        /// <summary>
        /// v2.3.8 Task 3.3 — Pure-string {find, replace} patch shape that routes through
        /// <see cref="WriteService.TryMatch"/> so multi-line context with CRLF/LF mismatch
        /// or trailing-whitespace differences still resolves to a unique window. Returns a
        /// tuple of (ok, result). On failure result echoes the input source unchanged so
        /// callers can splice the value safely. Used directly by unit tests; the live
        /// editor path goes through ApplyPatch which also handles persistence.
        /// </summary>
        public static (bool ok, string result, string reason) ApplyFindReplace(string source, JObject patch)
        {
            if (source == null) return (false, string.Empty, "source is null");
            if (patch == null) return (false, source, "patch is null");
            string find = patch["find"]?.ToString();
            string replace = patch["replace"]?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(find)) return (false, source, "patch.find is required");

            if (WriteService.TryMatch(source, find, out int start, out int end) && end > start)
            {
                string spliced = source.Substring(0, start) + replace + source.Substring(end);
                return (true, spliced, null);
            }
            return (false, source, "NoMatch");
        }

        // Returns a warning if the agent is editing a visual part (WebForm/Layout) on
        // an object whose WorkWithPlus host has a PatternInstance — hand edits there
        // can be overwritten on next pattern apply/save.
        //
        // Resolution covers three target shapes:
        //   (1) The WWP host itself (`WorkWithPlus<X>`) — direct match.
        //   (2) The Transaction the host is built on — ResolveWWPInstance("WorkWithPlus"+name).
        //   (3) A generated WW WebPanel (`WW<X>`, `View<X>`, `Export*`, etc.) — these
        //       don't follow the `WorkWithPlus<X>` naming rule, so we resolve the host
        //       by checking GetParent() up to 3 levels, looking for a WWP-typed ancestor.
        private JArray BuildPatternShadowWarningsIfAny(string target, string partName, string typeFilter)
        {
            try
            {
                if (_patternAnalysisService == null || _objectService == null) return null;
                if (!GxMcp.Worker.Helpers.WebFormXmlHelper.IsVisualPart(partName)) return null;

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return null;

                var resolved = _patternAnalysisService.ResolveWWPInstance(obj);

                // Fallback for generated WW family (WW<Trn>, View<Trn>, etc.): WWP host
                // for `WWX` is named `WorkWithPlusX`. We try a few common prefixes; the
                // ResolveWWPInstance also walks Children, so a hit here closes the gap
                // for the generated WebPanel case that motivated F6.
                if (resolved == null && !string.IsNullOrEmpty(obj.Name))
                {
                    string[] candidatePrefixes = { "WW", "View", "ViewWW", "Prompt" };
                    foreach (var pre in candidatePrefixes)
                    {
                        if (!obj.Name.StartsWith(pre, StringComparison.Ordinal)) continue;
                        string baseName = obj.Name.Substring(pre.Length);
                        if (string.IsNullOrEmpty(baseName)) continue;
                        var hostName = "WorkWithPlus" + baseName;
                        try
                        {
                            var host = _objectService.FindObject(hostName);
                            if (host != null && string.Equals(host.TypeDescriptor?.Name, "WorkWithPlus", StringComparison.OrdinalIgnoreCase))
                            {
                                resolved = host;
                                break;
                            }
                        }
                        catch { /* lookup best-effort */ }
                    }
                }

                if (resolved == null) return null;

                var part = _patternAnalysisService.FindPatternPart(resolved, "PatternInstance");
                if (part == null) return null;

                return new JArray
                {
                    new JObject
                    {
                        ["code"] = "EditingWebFormUnderPattern",
                        ["severity"] = "warning",
                        ["message"] =
                            "This object is covered by a WorkWithPlus PatternInstance ('" + resolved.Name +
                            "'). Hand edits to " + partName + " can be overwritten on the next pattern apply/save. " +
                            "Consider editing part=PatternInstance on '" + resolved.Name + "' instead. " +
                            "Toggle SDPlus_Editor_Apply_On_Save=False on " + resolved.Name +
                            " if you must keep a hard override on the visual part.",
                        ["patternInstance"] = resolved.Name
                    }
                };
            }
            catch (Exception ex)
            {
                Logger.Debug("[PatchService] PatternShadow warning probe skipped: " + ex.Message);
                return null;
            }
        }

        private static string AttachWarningsToJson(string json, JArray warnings)
        {
            if (warnings == null || warnings.Count == 0 || string.IsNullOrEmpty(json)) return json;
            try
            {
                var obj = JObject.Parse(json);
                obj["warnings"] = warnings;
                return obj.ToString();
            }
            catch
            {
                return json;
            }
        }

        public string ApplyPatch(string target, string partName, string operation, string content, string context = null, int expectedCount = 1, string typeFilter = null, bool dryRun = false, bool verifyRollback = false, bool returnPostState = true, bool verbose = false, bool replaceAll = false, string verifyMode = null, string baseVersion = null, bool rollbackOnFailure = false, bool autoInjectVariables = false, bool requireObjectSave = false)
        {
            string guardedPartName = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
            partName = guardedPartName;
            if (TextPayloadGuard.AppliesToPart(guardedPartName))
            {
                string literalLineBreakError = TextPayloadGuard.BuildWriteError(target, guardedPartName, "content", content);
                if (literalLineBreakError != null) return literalLineBreakError;
            }

            // Friction 2026-05-22: capture entry timestamp so a NoMatch we see at
            // the end can be cross-checked against WriteService.WasTargetWrittenSince
            // — if the file changed while this patch was queued/running, the context
            // is stale (concurrent edit) rather than truly absent. Use strict UtcNow
            // (no backdating) so only writes that landed AFTER this patch entered
            // the read trigger the Stale verdict; a write that completed strictly
            // before patch entry is not concurrent with us.
            DateTime patchEnteredAtUtc = DateTime.UtcNow;
            try
            {
                string resolvedVerifyMode;
                try
                {
                    resolvedVerifyMode = TextPersistenceVerifier.ResolveMode(verifyMode, partName);
                }
                catch (ArgumentException ex)
                {
                    return Models.McpResponse.Err(code: "InvalidVerifyMode", message: ex.Message, target: target);
                }

                if (requireObjectSave && !string.Equals(partName, "Events", StringComparison.OrdinalIgnoreCase))
                {
                    return Models.McpResponse.Err(
                        code: "RequireObjectSaveUnsupportedPart",
                        message: "requireObjectSave is supported only for part=Events in patch mode.",
                        target: target);
                }
                if (requireObjectSave && !dryRun && string.IsNullOrWhiteSpace(baseVersion))
                {
                    return Models.McpResponse.Err(
                        code: "BaseVersionRequired",
                        message: "A complete Events object save requires baseVersion so a concurrent IDE or MCP change cannot be overwritten.",
                        hint: "Re-read Events and retry with the returned versionToken as baseVersion.",
                        target: target,
                        extra: new JObject
                        {
                            ["requireObjectSave"] = true,
                            ["partPersisted"] = false,
                            ["objectSaved"] = false,
                            ["metadataUpdated"] = false
                        });
                }

                // Probe pattern-shadow warning ONCE before doing any work. If the agent
                // is patching a WebForm/Layout on an object whose WorkWithPlus host has
                // a populated PatternInstance, attach a warning to the terminal response
                // so they know hand edits to the visual XML can be overwritten by the
                // next pattern apply/save. Mirrors the same probe in WriteService.WriteVisualPart.
                JArray patternShadowWarnings = BuildPatternShadowWarningsIfAny(target, partName, typeFilter);

                string cacheKey = BuildCacheKey(target, partName, typeFilter);
                bool sourceFromCache = false;
                long readMs = 0;
                string originalSource = null;
                string snapshotVersion = null;
                // v2.6.9 perf: reuse a fresh cache entry when no write has landed
                // since we filled it. WriteService._lastWriteAtUtc tracks every
                // write path; if WasTargetWrittenSince(target, entry.UpdatedUtc)
                // is false AND the entry is within TTL, the cache is authoritative
                // and we can skip the SDK round-trip entirely.
                //
                // Stale-cache prevention still kicks in on the false path:
                // writes that bypass PatchService (or external IDE edits) tracked
                // via _lastWriteAtUtc flip WasTargetWrittenSince to true, and
                // we drop the cache + force a fresh read. The 20s TTL is the
                // last-resort safety net for edits the worker didn't observe
                // at all (e.g. straight filesystem touches).
                // A caller that requests optimistic concurrency or rollback needs a
                // fresh snapshot; a cache entry is not sufficient evidence for either.
                bool requireFreshSnapshot = !string.IsNullOrWhiteSpace(baseVersion) || rollbackOnFailure || verifyRollback;
                if (!requireFreshSnapshot && _sourceCache.TryGetValue(cacheKey, out var cacheEntry) && cacheEntry != null)
                {
                    bool ttlOk = (DateTime.UtcNow - cacheEntry.UpdatedUtc) < SourceCacheTtl;
                    bool noConcurrentWrite = !WriteService.WasTargetWrittenSince(target, cacheEntry.UpdatedUtc);
                    if (ttlOk && noConcurrentWrite)
                    {
                        originalSource = cacheEntry.Source;
                        snapshotVersion = cacheEntry.VersionToken;
                        sourceFromCache = true;
                    }
                    else
                    {
                        _sourceCache.TryRemove(cacheKey, out _);
                        _objectService.MarkReadCacheDirty(_objectService.FindObject(target, typeFilter), partName);
                    }
                }
                else
                {
                    _objectService.MarkReadCacheDirty(_objectService.FindObject(target, typeFilter), partName);
                }
                if (originalSource == null)
                {
                    var readStopwatch = Stopwatch.StartNew();
                    string currentResponse = requireFreshSnapshot
                        ? _objectService.ReadObjectSourceForVerification(target, partName, typeFilter)
                        : ReadSourceFast(target, partName, typeFilter);
                    readStopwatch.Stop();
                    readMs = readStopwatch.ElapsedMilliseconds;
                    string readError = TryExtractError(currentResponse);
                    if (!string.IsNullOrWhiteSpace(readError))
                    {
                        return Models.McpResponse.Err(
                            code: "PatchReadFailed",
                            message: "Patch read failed: " + readError,
                            hint: "Ensure the target object and part exist in the active KB.",
                            nextSteps: new JArray(Models.McpResponse.NextStep(
                                tool: "genexus_read",
                                args: new JObject { ["name"] = target, ["part"] = partName },
                                why: "Verify the part is accessible before patching.")),
                            target: target);
                    }

                    var json = JObject.Parse(currentResponse);
                    originalSource = json["source"]?.ToString();
                    snapshotVersion = json["versionToken"]?.ToString();
                    if (originalSource == null)
                    {
                        return Models.McpResponse.Err(
                            code: "PatchReadSourceNull",
                            message: "Could not retrieve source for the requested part.",
                            hint: "The part may not expose a text source. Use genexus_read to inspect available parts.",
                            nextSteps: new JArray(Models.McpResponse.NextStep(
                                tool: "genexus_read",
                                args: new JObject { ["name"] = target },
                                why: "Lists available parts for this object.")),
                            target: target);
                    }
                    UpdateCachedSource(cacheKey, originalSource, snapshotVersion);
                }

                if (!string.IsNullOrWhiteSpace(baseVersion) &&
                    !string.Equals(baseVersion, snapshotVersion, StringComparison.Ordinal))
                {
                    return Models.McpResponse.Err(
                        code: "VersionConflict",
                        message: "The object Source changed after the caller read it.",
                        target: target,
                        extra: new JObject { ["baseVersion"] = baseVersion, ["currentVersion"] = snapshotVersion });
                }

                // Normalize line endings for internal processing
                string workSource = originalSource.Replace("\r\n", "\n").Replace("\r", "\n");
                string workContext = context?.Replace("\r\n", "\n").Replace("\r", "\n");
                string workContent = (content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");

                var sourceLines = workSource.Split('\n');
                // Preserve blank lines, including a terminal newline. Removing empty
                // entries changed the matched byte range: a context ending in CRLF
                // matched only the visible lines, then a replacement that also ended
                // in CRLF left the original terminator behind and produced an extra
                // blank line. That breaks exact verification and empty deletions.
                var contextLines = workContext?.Split(new[] { '\n' }, StringSplitOptions.None);
                string normalizedOperation = NormalizeOperation(operation);

                // 2. Matching Logic
                string updatedSource = null;
                int matchCount = 0;
                string status = null;
                string details = null;

                if (expectedCount <= 0)
                {
                    return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "expectedCount must be >= 1.");
                }

                var patchStopwatch = Stopwatch.StartNew();
                switch (normalizedOperation)
                {
                    case "replace":
                        if (string.IsNullOrEmpty(workContext))
                            return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0,
                                "Replace needs the text to find. Use mode=patch with operation=\"Replace\", context=\"<exact existing lines>\", content=\"<new lines>\" — or the shorthand patch={\"find\":\"<existing>\",\"replace\":\"<new>\"}. A bare patch string / content-only has nothing to match against.");

                        if (!requireObjectSave && NormalizeSourceForComparison(workContext) == NormalizeSourceForComparison(workContent))
                        {
                            return BuildPatchResult("NoChange", partName, normalizedOperation, expectedCount, 1, "Patch content is identical to context. Write skipped.");
                        }

                        updatedSource = TryReplace(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount, replaceAll);
                        break;

                    case "insert_after":
                        if (string.IsNullOrEmpty(workContext))
                            return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "'context' (anchor) is required for Insert_After.");
                        updatedSource = TryInsertAfter(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
                        break;

                    case "append":
                        matchCount = 1;
                        if (string.IsNullOrWhiteSpace(workContent))
                        {
                            status = "NoChange";
                            details = "Append payload is empty. Write skipped.";
                            updatedSource = workSource;
                            break;
                        }
                        updatedSource = workSource.TrimEnd() + "\n" + workContent;
                        status = "Applied";
                        break;

                    default:
                        return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "Unknown operation: " + operation);
                }
                patchStopwatch.Stop();
                long patchMs = patchStopwatch.ElapsedMilliseconds;

                // One guarded retry against stale cache: refresh source once and recompute.
                if (sourceFromCache &&
                    string.IsNullOrEmpty(updatedSource) &&
                    (string.Equals(status, "NoMatch", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "Ambiguous", StringComparison.OrdinalIgnoreCase)))
                {
                    var refreshReadSw = Stopwatch.StartNew();
                    string refreshedResponse = ReadSourceFast(target, partName, typeFilter);
                    refreshReadSw.Stop();
                    readMs += refreshReadSw.ElapsedMilliseconds;
                    string refreshedError = TryExtractError(refreshedResponse);
                    if (string.IsNullOrWhiteSpace(refreshedError))
                    {
                        var refreshedJson = JObject.Parse(refreshedResponse);
                        string refreshedSource = refreshedJson["source"]?.ToString();
                        string refreshedVersion = refreshedJson["versionToken"]?.ToString();
                        if (refreshedSource != null)
                        {
                            UpdateCachedSource(cacheKey, refreshedSource, refreshedVersion);
                            if (!string.IsNullOrWhiteSpace(refreshedVersion)) snapshotVersion = refreshedVersion;
                            originalSource = refreshedSource;
                            workSource = originalSource.Replace("\r\n", "\n").Replace("\r", "\n");
                            sourceLines = workSource.Split('\n');
                            patchStopwatch.Restart();
                            if (normalizedOperation == "replace")
                            {
                                updatedSource = TryReplace(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount, replaceAll);
                            }
                            else if (normalizedOperation == "insert_after")
                            {
                                updatedSource = TryInsertAfter(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
                            }
                            patchStopwatch.Stop();
                            patchMs += patchStopwatch.ElapsedMilliseconds;
                        }
                    }
                }

                // An empty updated source is a valid result when Replace matched the
                // complete part and the requested replacement is empty. Match failures
                // also return an empty string, so status (not payload length) is the
                // discriminator that keeps the no-match safety guard intact.
                if (updatedSource == null ||
                    (!string.Equals(status, "Applied", StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrEmpty(updatedSource)))
                {
                    string dbg = string.Empty;
                    if (sourceLines.Length > 0 && contextLines?.Length > 0)
                    {
                        dbg = $" | Example: Source='{sourceLines[0].Trim()}' vs Context='{contextLines[0].Trim()}'";
                    }
                    Logger.Warn($"[PATCH] Match failed for {target}. Context: '{context}'{dbg}");

                    string failedStatus = string.IsNullOrWhiteSpace(status) ? "NoMatch" : status;
                    string failedDetails = string.IsNullOrWhiteSpace(details)
                        ? $"Context not found. Ensure the context matches a unique block in the source code.{dbg}"
                        : details;

                    // Friction 2026-05-22: distinguish "match truly absent" from
                    // "a sibling write to this same target landed before us".
                    // When the cross-check is positive, surface a distinct hint
                    // so the caller doesn't burn turns retrying with the same context.
                    bool concurrentWrite = WriteService.WasTargetWrittenSince(target, patchEnteredAtUtc);
                    if (concurrentWrite && string.Equals(failedStatus, "NoMatch", StringComparison.OrdinalIgnoreCase))
                    {
                        failedStatus = "Stale";
                        failedDetails = "File modified during patch (concurrent edit landed against the same target). Re-read the object source and retry with refreshed context. Original failure: " + failedDetails;
                    }

                    string failure = BuildPatchResult(failedStatus, partName, normalizedOperation, expectedCount, matchCount, failedDetails);

                    // FR#18 (friction-report 2026-05-14): attach near-match diagnostics so the
                    // agent can adjust context in one iteration instead of re-reading the whole
                    // file. Only runs on NoMatch (Ambiguous already gives the matched indices).
                    if (string.Equals(failedStatus, "NoMatch", StringComparison.OrdinalIgnoreCase) &&
                        contextLines != null && contextLines.Length > 0)
                    {
                        // Issue #27 item 6: the near-match scan is O(source × context) so it's
                        // capped by context size — but 50 lines was too tight and left larger
                        // multi-line contexts (common when a blank line is included) with a bare
                        // "not found" and no diagnostics. Raised to 120.
                        var near = contextLines.Length <= 120
                            ? FindNearMatches(sourceLines, contextLines, topN: 3)
                            : new List<PatchTextEditor.NearMatch>();
                        if (near.Count == 0)
                        {
                            // Issue #27 item 6: previously, when no similar window was found the
                            // response carried NO nearMatches/eolDiff at all — "nothing actionable
                            // to compare against". Always emit a concrete next step instead.
                            try
                            {
                                var fj = JObject.Parse(failure);
                                var en = fj["error"] as JObject ?? fj;
                                en["noNearMatchHint"] = contextLines.Length > 120
                                    ? "Context too large for near-match diagnostics (>120 lines). Patch a smaller, unique block: pick a few contiguous lines, or anchor a single unique line with operation=Insert_After."
                                    : "No sufficiently-similar block found in the source. The context likely spans a non-contiguous region, mixes lines from different places, or differs by more than trailing whitespace/EOL. Re-read the object with genexus_read and copy one exact contiguous block, or anchor on a single unique line with operation=Insert_After.";
                                failure = fj.ToString();
                            }
                            catch { /* keep original failure */ }
                        }
                        if (near.Count > 0)
                        {
                            try
                            {
                                // v2.8.0: diagnostic fields go inside the error sub-object.
                                var failureJson = JObject.Parse(failure);
                                var errNode = failureJson["error"] as JObject ?? failureJson;
                                var arr = new JArray();
                                foreach (var nm in near)
                                {
                                    arr.Add(new JObject
                                    {
                                        ["line"] = nm.StartLine + 1, // 1-based for humans
                                        ["similarity"] = Math.Round(nm.Similarity, 2),
                                        ["snippet"] = nm.Snippet
                                    });
                                }
                                errNode["nearMatches"] = arr;
                                errNode["nearMatchHint"] = "Top similar windows in source. Adjust 'context' to match exact tabs/whitespace of one of these and retry.";

                                // v2.3.8 Task 3.2: byte-level divergence detail for the top window
                                // when similarity is high enough that the agent likely just has
                                // the wrong tabs/EOLs/one wrong char.
                                var top = near[0];
                                if (top.StartLine + contextLines.Length <= sourceLines.Length)
                                {
                                    var sb = new System.Text.StringBuilder();
                                    for (int i = 0; i < contextLines.Length; i++)
                                    {
                                        if (i > 0) sb.Append('\n');
                                        sb.Append(sourceLines[top.StartLine + i]);
                                    }
                                    string bestWindow = sb.ToString();
                                    string ctxJoined = string.Join("\n", contextLines);

                                    if (top.Similarity >= 0.80)
                                    {
                                        errNode["nearMatchHintDetail"] = GxMcp.Worker.Helpers.DiffBuilder.ByteLevelDivergence(bestWindow, ctxJoined);
                                    }

                                    // Item 4 (friction 2026-05-22): EOL mismatch short diff.
                                    int eolDiffLines = Math.Min(3, contextLines.Length);
                                    bool eolMismatchDetected = false;
                                    var eolDiffArr = new JArray();
                                    for (int i = 0; i < eolDiffLines; i++)
                                    {
                                        string agentLine = contextLines[i];
                                        string fileLine = sourceLines[top.StartLine + i];
                                        bool linesMatchNorm = string.Equals(agentLine.TrimEnd(), fileLine.TrimEnd(), StringComparison.Ordinal);
                                        bool linesMatchExact = string.Equals(agentLine, fileLine, StringComparison.Ordinal);
                                        if (linesMatchNorm && !linesMatchExact)
                                        {
                                            eolMismatchDetected = true;
                                        }
                                        eolDiffArr.Add(new JObject
                                        {
                                            ["lineNo"] = top.StartLine + i + 1,
                                            ["agent"] = ShowControlChars(agentLine),
                                            ["file"] = ShowControlChars(fileLine),
                                            ["match"] = linesMatchNorm ? "eol_only" : (linesMatchExact ? "exact" : "differs")
                                        });
                                    }
                                    if (eolMismatchDetected || top.Similarity >= 0.60)
                                    {
                                        errNode["eolDiff"] = eolDiffArr;
                                        if (eolMismatchDetected)
                                        {
                                            errNode["eolDiffHint"] = "Lines differ only in trailing whitespace or EOL. The find context is otherwise correct — the patch pipeline normalizes EOLs automatically, so this mismatch should not cause NoMatch. Check for invisible characters or tab/space mix beyond trailing whitespace.";
                                        }
                                    }

                                    // Item 17 (friction 2026-05-22): Levenshtein-based did_you_mean.
                                    int ctxLen = ctxJoined.Length;
                                    const int LevenshteinMaxLen = 2000;
                                    if (ctxLen <= LevenshteinMaxLen && top.Similarity >= 0.50)
                                    {
                                        int threshold = (int)Math.Ceiling(0.20 * ctxLen);
                                        int levenDist = LevenshteinDistance(ctxJoined, bestWindow, threshold + 1);
                                        if (levenDist <= threshold)
                                        {
                                            errNode["did_you_mean"] = new JObject
                                            {
                                                ["startLine"] = top.StartLine + 1,
                                                ["endLine"] = top.StartLine + contextLines.Length,
                                                ["levenshteinDistance"] = levenDist,
                                                ["snippet"] = bestWindow.Length > 300 ? bestWindow.Substring(0, 297) + "..." : bestWindow
                                            };
                                        }
                                    }
                                }

                                failure = failureJson.ToString();
                            }
                            catch { /* keep original failure on serialization issue */ }
                        }
                    }

                    return AttachTimings(failure, readMs, patchMs, 0, sourceFromCache);
                }

                bool noContentChange = NormalizeForPartCompare(partName, workSource) == NormalizeForPartCompare(partName, updatedSource);
                if (noContentChange && !requireObjectSave)
                {
                    // Friction 2026-05-22: distinguish the two NoChange cases.
                    // case-a: matched + content identical to context (caught earlier
                    //   inside switch).
                    // case-b: matched + replacement normalized back to original via
                    //   the part-normalizer (XML comment preservation, attribute
                    //   ordering, trailing whitespace). The match worked — the
                    //   serializer treated the change as semantically equivalent.
                    bool literalIdentical = string.Equals(workSource, updatedSource, StringComparison.Ordinal);
                    string detailMessage = literalIdentical
                        ? "Patch produced no effective changes. Write skipped."
                        : "Patch matched and applied, but the part-normalizer treated the replacement as equivalent to the original (often XML attribute ordering, comment preservation, or trailing whitespace). The on-disk file is unchanged. Inspect the serialized source diff and adjust semantics, not just text.";
                    string noChange = BuildPatchResult("NoChange", partName, normalizedOperation, expectedCount, matchCount, detailMessage);
                    try
                    {
                        // v2.8.0: noChangeReason goes inside result (envelope is ok/NoChange).
                        var nj = JObject.Parse(noChange);
                        if (nj["result"] is JObject resultNode)
                            resultNode["noChangeReason"] = literalIdentical ? "literal_identical" : "serializer_normalized";
                        else
                            nj["noChangeReason"] = literalIdentical ? "literal_identical" : "serializer_normalized";
                        noChange = nj.ToString();
                    }
                    catch { }
                    return AttachTimings(noChange, readMs, patchMs, 0, sourceFromCache);
                }

                // Events may already have been saved only as a part. Requiring the
                // object save still means ForceSave + verification when the text is
                // unchanged. Preserve its exact bytes instead of normalizing again.
                if (noContentChange) updatedSource = workSource;

                bool commentOnlyChange = CommentOnlyPatch.TryClassify(
                    partName, normalizedOperation, workContext, workContent, out string commentStyle);
                if (commentOnlyChange && !dryRun && string.IsNullOrWhiteSpace(baseVersion))
                {
                    string missingVersion = Models.McpResponse.Err(
                        code: "BaseVersionRequired",
                        message: "Comment-only Source replacements require baseVersion; no write was attempted.",
                        hint: "Re-read the Source, then retry with the returned versionToken as baseVersion.",
                        nextSteps: new JArray(Models.McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = partName },
                            why: "Obtains the current Source and versionToken for optimistic concurrency.")),
                        target: target,
                        extra: new JObject
                        {
                            ["part"] = partName,
                            ["operation"] = normalizedOperation,
                            ["commentOnly"] = true,
                            ["commentStyle"] = commentStyle,
                            ["matchCount"] = matchCount,
                            ["saved"] = false,
                            ["verified"] = false,
                            ["implicitOperations"] = new JArray()
                        });
                    return AttachTimings(missingVersion, readMs, patchMs, 0, sourceFromCache);
                }

                if (dryRun)
                {
                    string dryRunResult = BuildPatchResult("Applied", partName, normalizedOperation, expectedCount, matchCount, "Dry-run succeeded. Write skipped.");
                    try
                    {
                        var dryRunJson = JObject.Parse(dryRunResult);
                        var dryRunBody = dryRunJson["result"] as JObject ?? dryRunJson;
                        var dryRunEvidence = TextPersistenceVerifier.Evaluate(
                            requested: requireObjectSave && noContentChange ? originalSource : ToSdkLineEndings(updatedSource),
                            persisted: originalSource, requestedMode: resolvedVerifyMode, partName: partName);
                        dryRunBody["persisted"] = false;
                        dryRunBody["saved"] = false;
                        dryRunBody["verified"] = false;
                        dryRunBody["requireObjectSave"] = requireObjectSave;
                        dryRunBody["noContentChange"] = noContentChange;
                        if (requireObjectSave)
                        {
                            var isolation = _writeService.InspectEventsIsolation(target, typeFilter);
                            dryRunBody["objectSaveIsolation"] = isolation;
                            if (isolation["blocker"] != null) dryRunBody["writeBlocker"] = isolation["blocker"];
                        }
                        dryRunBody["requestedHash"] = dryRunEvidence.RequestedHash;
                        dryRunBody["persistedHash"] = dryRunEvidence.PersistedHash;
                        dryRunBody["commentOnly"] = commentOnlyChange;
                        if (commentOnlyChange)
                        {
                            dryRunBody["commentStyle"] = commentStyle;
                            dryRunBody["before"] = context;
                            dryRunBody["after"] = content ?? string.Empty;
                            dryRunBody["matchedCount"] = matchCount;
                        }
                        dryRunBody["implicitOperations"] = new JArray();
                        if (!string.IsNullOrWhiteSpace(snapshotVersion)) dryRunBody["versionToken"] = snapshotVersion;
                        dryRunBody["verification"] = new JObject
                        {
                            ["mode"] = resolvedVerifyMode,
                            ["matchCount"] = matchCount,
                            ["reReadConfirmed"] = false,
                            ["skipped"] = "dryRun"
                        };
                        dryRunResult = dryRunJson.ToString();
                    }
                    catch { }
                    dryRunResult = AttachWarningsToJson(dryRunResult, patternShadowWarnings);
                    return AttachTimings(dryRunResult, readMs, patchMs, 0, sourceFromCache);
                }

                // v2.6.6 FR#10 — invariant: never persist a payload that looks like
                // a NoMatch fall-through. Belt-and-braces against the v2.6.5 Events-part
                // friction where an empty/near-empty result reached the SDK save path
                // and the on-disk source was lost with sha256 = e3b0c44... (empty).
                bool anyOpApplied = string.Equals(status, "Applied", StringComparison.OrdinalIgnoreCase)
                                    || matchCount > 0;
                if (!WriteService.IsPatchWriteSafe(workSource, updatedSource, anyOpApplied, out string safetyReason))
                {
                    Logger.Error($"[PATCH] Refusing unsafe write for {target}/{partName}: reason={safetyReason} origLen={workSource?.Length ?? 0} newLen={updatedSource?.Length ?? 0}");
                    var safety = BuildPatchResult("NoMatch", partName, normalizedOperation, expectedCount, matchCount,
                        "Refused to persist suspected NoMatch payload (" + safetyReason + "). No write was attempted; on-disk source unchanged.");
                    try
                    {
                        // v2.8.0: code lives at top level; extra fields go inside error object.
                        var safetyJson = JObject.Parse(safety);
                        safetyJson["code"] = "PatchNoMatch";
                        if (safetyJson["error"] is JObject errNode) errNode["safetyReason"] = safetyReason;
                        else safetyJson["safetyReason"] = safetyReason;
                        safety = safetyJson.ToString();
                    }
                    catch (Exception jsonEx)
                    {
                        Logger.Debug("[PATCH] safety envelope augmentation failed: " + jsonEx.Message);
                    }
                    return AttachTimings(safety, readMs, patchMs, 0, sourceFromCache);
                }

                // Friction 2026-05-28 — early-reject Form-type transitions on
                // WebForm/Layout parts. The SDK rejects html→layout (and vice
                // versa) sub-tree edits without diagnostics; bail out here with
                // a typed envelope so the agent doesn't burn a turn on a generic
                // visual-write failure.
                if (GxMcp.Worker.Helpers.WebFormXmlHelper.IsVisualPart(partName))
                {
                    string origFormType = WriteService.TryExtractFormType(workSource);
                    string newFormType = WriteService.TryExtractFormType(updatedSource);
                    if (!string.IsNullOrEmpty(origFormType)
                        && !string.IsNullOrEmpty(newFormType)
                        && !string.Equals(origFormType, newFormType, StringComparison.OrdinalIgnoreCase))
                    {
                        string transitionMsg = $"Form type transition not supported via mode=patch "
                            + $"({origFormType} → {newFormType}). "
                            + "Use mode='full' with a COMPLETE target-type body (a single <Form type=\""
                            + newFormType + "\"> root containing all required children). "
                            + "On WorkWithPlus KBs use genexus_create_popup or harvest dual-form XML from an existing layout-form WebPanel.";
                        // v2.8.0: direct McpResponse.Err so the curated nextSteps
                        // for FormTypeTransitionUnsupported land on the wire.
                        var transitionFailure = Models.McpResponse.Err(
                            code: "FormTypeTransitionUnsupported",
                            message: transitionMsg,
                            hint: "mode=patch can only adjust a body within the same Form type; use mode=full with a complete target-type body to switch types.",
                            nextSteps: new JArray(
                                GxMcp.Worker.Helpers.SkillHint.ReadStep(
                                    GxMcp.Worker.Helpers.SkillHint.Navigation,
                                    "Form type / Modal / CallProtocol terminology — this skill spells out what the SDK actually exposes vs common LLM hallucinations."),
                                Models.McpResponse.NextStep(
                                    tool: "genexus_create_popup",
                                    args: new JObject { ["name"] = target },
                                    why: "On WorkWithPlus KBs, creates a fresh layout-form popup with the right schema."),
                                Models.McpResponse.NextStep(
                                    tool: "genexus_edit",
                                    args: new JObject { ["name"] = target, ["mode"] = "full", ["part"] = partName },
                                    why: "Full-body replace is the supported path to change Form type.")),
                            target: target,
                            extra: new JObject
                            {
                                ["part"] = partName,
                                ["operation"] = normalizedOperation,
                                ["fromFormType"] = origFormType,
                                ["toFormType"] = newFormType
                            });
                        return AttachTimings(transitionFailure, readMs, patchMs, 0, sourceFromCache);
                    }
                }

                // Re-check at the write boundary as well as entry, reducing the
                // read/patch/write race window for optimistic concurrency callers.
                if (!string.IsNullOrWhiteSpace(baseVersion))
                {
                    string currentVersion = ReadFreshVersionToken(target, partName, typeFilter);
                    if (!string.Equals(baseVersion, currentVersion, StringComparison.Ordinal))
                        return Models.McpResponse.Err(
                            code: "VersionConflict",
                            message: "The object changed while the patch was being prepared; no write was attempted.",
                            target: target,
                            extra: new JObject { ["baseVersion"] = baseVersion, ["currentVersion"] = currentVersion });
                }

                if (requireObjectSave)
                {
                    var requiredSaveWatch = Stopwatch.StartNew();
                    string requiredSave = _writeService.WriteIsolatedEvents(target,
                        noContentChange ? originalSource : ToSdkLineEndings(updatedSource), typeFilter, baseVersion);
                    requiredSaveWatch.Stop();
                    return AttachTimings(requiredSave, readMs, patchMs, requiredSaveWatch.ElapsedMilliseconds, sourceFromCache);
                }

                // 3. Write Back (re-normalize to CRLF for GeneXus)
                string finalCode = ToSdkLineEndings(updatedSource);
                ObjectMetadataSnapshot metadataBefore = null;
                ObjectMoveSnapshot fullObjectSnapshot = null;
                if (requireObjectSave && !dryRun)
                {
                    try
                    {
                        metadataBefore = ReadFreshObjectMetadata(target, typeFilter);
                        if (metadataBefore?.Object == null)
                            throw new InvalidOperationException("The target object could not be reloaded for a complete pre-write snapshot.");
                        fullObjectSnapshot = ObjectMoveSnapshot.Capture(metadataBefore.Object);
                    }
                    catch (Exception snapshotEx)
                    {
                        return Models.McpResponse.Err(
                            code: "ObjectSnapshotFailed",
                            message: "A complete object save was requested, but the pre-write snapshot could not be captured. No write was attempted.",
                            hint: "Re-read the object and retry only after the SDK can enumerate all persisted parts.",
                            target: target,
                            extra: new JObject
                            {
                                ["requireObjectSave"] = true,
                                ["partPersisted"] = false,
                                ["objectSaved"] = false,
                                ["metadataUpdated"] = false,
                                ["details"] = snapshotEx.Message
                            });
                    }
                }
                var writeStopwatch = Stopwatch.StartNew();
                // Do not use the object-only fast path for textual patches. On GX18 U16,
                // obj.Save() can advance the object's version and leave the changed ISource
                // only in the live SDK instance. The full path saves the part explicitly and
                // commits the object transaction, matching mode=full persistence semantics.
                string writeResult = _writeService.WriteObject(target, partName, finalCode, typeFilter, autoValidate: false, preferFastSourceSave: false, autoInjectVariables: autoInjectVariables, baseVersion: baseVersion);
                writeStopwatch.Stop();
                long writeMs = writeStopwatch.ElapsedMilliseconds;
                JObject writePayload = ParseWriteResult(writeResult);
                writePayload["implicitOperations"] = new JArray();
                PatchPersistenceReceipt.AttachContentEvidence(writePayload, finalCode, finalCode, null);
                if (commentOnlyChange)
                {
                    writePayload["commentOnly"] = true;
                    writePayload["commentStyle"] = commentStyle;
                    writePayload["before"] = context;
                    writePayload["after"] = content ?? string.Empty;
                    writePayload["matchedCount"] = matchCount;
                }

                bool primaryWriteSuccess = string.Equals(writePayload["_internalStatus"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);
                bool writeReportedVerificationMismatch = string.Equals(writePayload["code"]?.ToString(), "WriteNotPersisted", StringComparison.OrdinalIgnoreCase);
                bool writeReportedVersionConflict = string.Equals(writePayload["code"]?.ToString(), "StaleObject", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(writePayload["code"]?.ToString(), "VersionConflict", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(writePayload["code"]?.ToString(), "VersionCheckUnavailable", StringComparison.OrdinalIgnoreCase);
                bool persistedMatches = false;
                bool saveReported = primaryWriteSuccess || writeReportedVerificationMismatch;
                string confirmedPersistedSource = null;
                bool isPatternPart = Services.PatternAnalysisService.IsPatternPart(partName);

                if (primaryWriteSuccess && isPatternPart)
                {
                    // Pattern XML is reconciled before persistence and is already checked with
                    // XML equivalence inside WriteService. Text verify modes intentionally apply
                    // only to Source/Rules-like parts.
                    persistedMatches = true;
                    writePayload["persistedVerified"] = true;
                    writePayload["persisted"] = true;
                }
                else if (!writeReportedVersionConflict && (primaryWriteSuccess || writeReportedVerificationMismatch || requireObjectSave))
                {
                    string persistedSource;
                    string verifyError;
                    TextPersistenceVerifier.Result verification = ReadAndVerifyPersistedSource(
                        target, partName, typeFilter, finalCode, resolvedVerifyMode, out persistedSource, out verifyError);

                    if (verification != null)
                    {
                        confirmedPersistedSource = persistedSource;
                        persistedMatches = PatchPersistenceReceipt.AttachVerification(
                            writePayload,
                            verification,
                            content,
                            context,
                            finalCode,
                            persistedSource,
                            resolvedVerifyMode,
                            partName,
                            matchCount,
                            commentOnlyChange);
                    }
                    else
                    {
                        writePayload["verification"] = new JObject
                        {
                            ["mode"] = resolvedVerifyMode,
                            ["matchCount"] = matchCount,
                            ["reReadConfirmed"] = false
                        };
                    }

                    if (persistedMatches && (primaryWriteSuccess || writeReportedVerificationMismatch))
                    {
                        // A WriteService false negative is superseded by the mandatory forced
                        // re-read. No second write is performed.
                        PatchPersistenceReceipt.MarkVerified(writePayload, saveReported);
                    }
                    else if (!persistedMatches)
                    {
                        PatchPersistenceReceipt.MarkNotPersisted(writePayload, saveReported, verifyError, commentOnlyChange);

                        // Rollback is never implicit. It is attempted once only when explicitly
                        // requested and the fresh pre-write snapshot is available.
                        if (PatchPersistenceReceipt.ShouldRollback(persistedMatches, rollbackOnFailure) && originalSource != null)
                        {
                            string rollbackResult = _writeService.WriteObject(target, partName, originalSource, typeFilter, autoValidate: false, preferFastSourceSave: false, autoInjectVariables: false);
                            JObject rollbackPayload = ParseWriteResult(rollbackResult);
                            // WriteService's legacy verifier may call a durable rollback
                            // WriteNotPersisted solely because it applies a different text
                            // equivalence rule. In both cases the SDK save completed, so always
                            // perform this operation's selected-mode forced re-read.
                            bool rollbackSaved = string.Equals(rollbackPayload["_internalStatus"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(rollbackPayload["code"]?.ToString(), "WriteNotPersisted", StringComparison.OrdinalIgnoreCase);
                            string rollbackPersisted = null;
                            string rollbackError = null;
                            TextPersistenceVerifier.Result rollbackVerification = null;
                            if (rollbackSaved)
                                rollbackVerification = ReadAndVerifyPersistedSource(target, partName, typeFilter, originalSource, resolvedVerifyMode, out rollbackPersisted, out rollbackError);
                            bool rollbackVerified = rollbackVerification != null && rollbackVerification.Matches;
                            writePayload["rollback"] = PatchPersistenceReceipt.BuildRollback(
                                rollbackSaved,
                                rollbackVerification,
                                rollbackError ?? rollbackPayload["message"]?.ToString());
                            writePayload["rolledBack"] = rollbackVerified;
                            if (rollbackVerified) UpdateCachedSource(cacheKey, originalSource, snapshotVersion);
                        }
                    }
                }

                // Always expose the save/verification distinction, including SDK-save
                // failures where no post-save comparison could run.
                if (writePayload["saved"] == null) writePayload["saved"] = saveReported;
                if (writePayload["verified"] == null) writePayload["verified"] = persistedMatches;
                if (requireObjectSave && writeReportedVersionConflict)
                {
                    writePayload["requireObjectSave"] = true;
                    writePayload["persistencePath"] = "object_save";
                    writePayload["partPersisted"] = false;
                    writePayload["objectSaved"] = false;
                    writePayload["metadataUpdated"] = false;
                }

                if (requireObjectSave && !writeReportedVersionConflict)
                {
                    ObjectMetadataSnapshot metadataAfter = ReadFreshObjectMetadata(target, typeFilter);
                    var comparison = fullObjectSnapshot.CompareParts(metadataAfter?.Object, partName);
                    bool metadataStampPersisted = writePayload["metadataStampPersisted"]?.ToObject<bool?>()
                        ?? (writePayload["result"] as JObject)?["metadataStampPersisted"]?.ToObject<bool?>()
                        ?? false;
                    bool objectSaved = saveReported;
                    PatchPersistenceReceipt.AttachObjectSaveEvidence(
                        writePayload,
                        persistedMatches,
                        objectSaved,
                        metadataBefore?.Revision,
                        metadataAfter?.Revision,
                        metadataBefore?.LastUpdate,
                        metadataAfter?.LastUpdate,
                        comparison.Equal,
                        metadataStampPersisted: metadataStampPersisted,
                        unexpectedChangedParts: comparison.ChangedParts);
                    writePayload["requireObjectSave"] = true;
                    writePayload["persistencePath"] = "object_save";

                    PatchPersistenceReceipt.RequireCompleteObjectSave(writePayload);
                }
                string versionToken = null;
                try
                {
                    versionToken = ReadFreshVersionToken(target, partName, typeFilter);
                    if (!string.IsNullOrWhiteSpace(versionToken)) writePayload["versionToken"] = versionToken;
                }
                catch { }

                if (string.Equals(writePayload["_internalStatus"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase) && persistedMatches)
                {
                    UpdateCachedSource(cacheKey, finalCode, versionToken);
                }

                // v2.8.0: convert the WriteService legacy envelope (status=Success/Error) to canonical shape.
                // WriteService is out-of-scope for this migration; we lift its fields into result/error here.
                bool finalSuccess = string.Equals(writePayload["_internalStatus"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);
                var resultObj = new JObject
                {
                    ["part"] = string.IsNullOrWhiteSpace(partName) ? "Source" : partName,
                    ["operation"] = normalizedOperation,
                    ["expectedCount"] = expectedCount,
                    ["matchCount"] = matchCount,
                    ["timings"] = new JObject
                    {
                        ["readMs"] = readMs,
                        ["patchMs"] = patchMs,
                        ["writeMs"] = writeMs,
                        ["usedSourceCache"] = sourceFromCache
                    }
                };
                // Carry through WriteService diagnostic fields into result.
                foreach (var prop in writePayload.Properties())
                {
                    string pn = prop.Name;
                    if (pn == "status" || pn == "action" || pn == "target") continue;
                    if (resultObj[pn] == null) resultObj[pn] = prop.Value;
                }
                if (returnPostState && finalSuccess && updatedSource != null)
                    resultObj["post_state"] = GxMcp.Worker.Services.JsonPatchService.BuildPostState(
                        originalSource,
                        updatedSource,
                        verbose,
                        persistedAfter: confirmedPersistedSource);

                if (finalSuccess)
                {
                    string finalCode2 = resultObj["code"]?.ToString() ?? "Applied";
                    var canonical = JObject.Parse(Models.McpResponse.Ok(target: target, code: finalCode2, result: resultObj));
                    if (patternShadowWarnings != null && patternShadowWarnings.Count > 0)
                        canonical["warnings"] = patternShadowWarnings;
                    return canonical.ToString();
                }
                else
                {
                    string writeMsg = writePayload["message"]?.ToString() ?? writePayload["error"]?.ToString() ?? "Patch write failed.";
                    string writeCode = writePayload["code"]?.ToString();
                    string errCode = !string.IsNullOrWhiteSpace(writeCode) ? writeCode : "PatchWriteFailed";
                    bool objectSaveIncomplete = string.Equals(errCode, "ObjectSaveIncomplete", StringComparison.OrdinalIgnoreCase);
                    string recoveryHint = objectSaveIncomplete
                        ? writePayload["manualRecovery"]?.ToString()
                        : "Re-read the object source and verify the part is writable, then retry.";
                    var canonical = JObject.Parse(Models.McpResponse.Err(
                        code: errCode,
                        message: writeMsg,
                        hint: recoveryHint,
                        nextSteps: new JArray(Models.McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = partName },
                            why: objectSaveIncomplete
                                ? "Confirm the actual persisted content before deciding whether any further action is safe."
                                : "Confirm the part content and state before retrying the patch.")),
                        target: target,
                        extra: resultObj));
                    if (patternShadowWarnings != null && patternShadowWarnings.Count > 0)
                        canonical["warnings"] = patternShadowWarnings;
                    return canonical.ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[PATCH] Error applying patch: {ex.Message}");
                return BuildPatchResult("Error", partName, NormalizeOperation(operation), expectedCount, 0, ex.Message);
            }
        }

        private ObjectMetadataSnapshot ReadFreshObjectMetadata(string target, string typeFilter)
        {
            try
            {
                KBObject obj = _objectService.FindObjectFresh(target, typeFilter);
                if (obj == null) return null;
                string revision = null;
                string lastUpdate = null;
                try { revision = obj.VersionId.ToString(System.Globalization.CultureInfo.InvariantCulture); } catch { }
                try
                {
                    if (obj.LastUpdate > DateTime.MinValue)
                        lastUpdate = obj.LastUpdate.ToUniversalTime().ToString("o");
                }
                catch { }
                return new ObjectMetadataSnapshot
                {
                    Object = obj,
                    Revision = revision,
                    LastUpdate = lastUpdate
                };
            }
            catch { return null; }
        }

        private string TryReplace(string[] sourceLines, string[] contextLines, string newContent, int expectedCount, out string status, out string details, out int matchCount, bool replaceAll = false)
        {
            return PatchTextEditor.TryReplace(
                sourceLines, contextLines, newContent, expectedCount,
                out status, out details, out matchCount, replaceAll);
        }

        private string TryInsertAfter(string[] sourceLines, string[] contextLines, string newContent, int expectedCount, out string status, out string details, out int matchCount)
        {
            return PatchTextEditor.TryInsertAfter(
                sourceLines, contextLines, newContent, expectedCount,
                out status, out details, out matchCount);
        }

        private static List<PatchTextEditor.NearMatch> FindNearMatches(string[] sourceLines, string[] contextLines, int topN)
        {
            return PatchTextEditor.FindNearMatches(sourceLines, contextLines, topN);
        }

        private static string ShowControlChars(string value)
        {
            return PatchTextEditor.ShowControlChars(value);
        }

        internal static int LevenshteinDistance(string a, string b, int maxDist = -1)
        {
            return PatchTextEditor.LevenshteinDistance(a, b, maxDist);
        }

        private static string NormalizeOperation(string operation)
        {
            string value = operation?.Trim().Replace("-", "_").ToLowerInvariant() ?? "replace";
            switch (value)
            {
                case "insertafter":
                case "insert_after":
                    return "insert_after";
                default:
                    return value;
            }
        }

        private static string NormalizeSourceForComparison(string text)
        {
            if (text == null) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
        }

        private static string ToSdkLineEndings(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n")
                .Replace("\n", Environment.NewLine);
        }

        // Friction-report #5 write-side: VariablesPart's underlying SDK collection inserts new
        // variables at the FRONT of the list, so a patch that produced `<original>...\n&NewVar`
        // round-trips through SetVariablesFromText / GetVariablesAsText as
        // `&NewVar\n<original>...`. Line-by-line equality verification then fails even though
        // the persisted state semantically matches. Use a set-based comparison for Variables
        // so patch verification reflects semantic equality, not the SDK's collection order.
        //
        // Friction-report 05-13 #4: the Variables DSL renderer drops `,0` when Decimals==0
        // (`NUMERIC(4,0)` → `NUMERIC(4)`), so a patch that wrote `&Counter : NUMERIC(4,0)`
        // round-trips as `&Counter : NUMERIC(4)`. Strings then differ even after set sort,
        // and verification falsely fails → auto-rollback wipes the new variable. Canonicalize
        // both sides by stripping trailing `,0)` so the comparison is semantic.
        internal static string NormalizeForPartCompare(string partName, string text)
        {
            string normalized = NormalizeSourceForComparison(text);
            if (!string.Equals(partName, "Variables", StringComparison.OrdinalIgnoreCase)) return normalized;
            var lines = normalized
                .Split('\n')
                .Select(l => CanonicalizeVariablesLine(l))
                .Where(l => l.Length > 0)
                .OrderBy(l => l, StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        private static readonly System.Text.RegularExpressions.Regex VariableTrailingZeroDecimals =
            new System.Text.RegularExpressions.Regex(
                @"\(\s*(\d+)\s*,\s*0\s*\)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string CanonicalizeVariablesLine(string line)
        {
            if (line == null) return string.Empty;
            string trimmed = line.Trim();
            if (trimmed.Length == 0) return string.Empty;
            // Collapse intra-line whitespace runs so "&X  :  NUMERIC ( 4 , 0 )" and
            // "&X : NUMERIC(4,0)" canonicalize identically.
            trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ");
            // Normalize "(N,0)" → "(N)" since the SDK renderer drops trailing zero decimals.
            trimmed = VariableTrailingZeroDecimals.Replace(trimmed, "($1)");
            return trimmed;
        }

        private static JObject ParseWriteResult(string writeResult)
        {
            try
            {
                var jo = JObject.Parse(writeResult);
                // v2.8.0 migration bridge. WriteService now emits the CANONICAL envelope
                // ({status:"ok"|"error"|"partial", code:"WriteApplied"|"WriteNoChange"|…}),
                // but the rest of ApplyPatch keys success off the LEGACY _internalStatus /
                // status:"Success" shape. Without this lift, primaryWriteSuccess is always
                // false for a clean canonical write, so every patch is forced down the
                // fallback re-verify/rollback path — an unnecessary second write on the
                // happy path. Map canonical status -> _internalStatus so the success
                // detection holds against the current WriteService envelope.
                if (jo["_internalStatus"] == null)
                {
                    string st = jo["status"]?.ToString();
                    if (string.Equals(st, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(st, "partial", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(st, "Success", StringComparison.OrdinalIgnoreCase))
                        jo["_internalStatus"] = "Success";
                    else if (string.Equals(st, "error", StringComparison.OrdinalIgnoreCase))
                        jo["_internalStatus"] = "Error";
                    // Lift the canonical error.message up so the legacy
                    // writePayload["message"] reads still find a human sentence
                    // instead of falling back to a raw/partial string.
                    if (jo["message"] == null && jo["error"]?["message"] != null)
                        jo["message"] = jo["error"]["message"];
                    // issue #34: lift the canonical error.code too. ApplyPatch keys the
                    // terminal envelope's code off writePayload["code"] and defaults to
                    // "PatchWriteFailed" when absent — which masked WriteService's real
                    // code (e.g. "AmbiguousObjectName") behind a generic one.
                    if (jo["code"] == null && jo["error"]?["code"] != null)
                        jo["code"] = jo["error"]["code"];
                }
                return jo;
            }
            // Scaffold-only: this JObject is consumed by the ApplyPatch method's local
            // writePayload checks; it is NEVER returned directly as the tool response.
            // The final response is emitted via McpResponse.Ok / .Err at the bottom
            // of ApplyPatch.
            catch { return new JObject { ["_internalStatus"] = "Error", ["message"] = writeResult }; }
        }

        private static string TryExtractError(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return "Empty response.";

            try
            {
                var json = JObject.Parse(response);
                // v2.8.0 canonical error envelope: { "status":"error", "error": { "message":"..." } }
                string status = json["status"]?.ToString();
                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var errObj = json["error"] as JObject;
                    if (errObj != null)
                        return errObj["message"]?.ToString() ?? "error";
                    return json["error"]?.ToString() ?? "error";
                }
                // Legacy error: { "status":"Error", "message":"..." }
                if (string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase))
                    return json["message"]?.ToString() ?? json["error"]?.ToString() ?? "error";
                return null; // ok / partial / accepted — not an error
            }
            catch
            {
                return "Non-JSON response.";
            }
        }

        private string ReadSourceFast(string target, string partName, string typeFilter)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null)
                {
                    // Internal read helper — callers check for error via TryExtractError.
                    // Use legacy Error shape here so TryExtractError() still picks up the error field.
                    // no-nextStep: this is an internal helper; the error is consumed by TryExtractError() and re-surfaced by the calling public method which already carries nextSteps.
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "Object not found.",
                        hint: "Verify the object name and type in the active KB.",
                        target: target);
                }

                string resolvedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;

                // Pattern parts (PatternInstance / PatternVirtual, e.g. WorkWithPlus) do not
                // implement ISource and PartAccessor.GetPart on the source object will miss them
                // (the part lives on the resolved WWP instance). Route through
                // PatternAnalysisService so patch-mode can read & rewrite pattern XML.
                if (_patternAnalysisService != null && PatternAnalysisService.IsPatternPart(resolvedPart))
                {
                    string patternXml = _patternAnalysisService.ReadPatternPartXml(obj, resolvedPart, out _, out var resolvedPatternPartName);
                    if (patternXml == null)
                    {
                        return Models.McpResponse.Err(
                            code: "PatternPartNotFound",
                            message: "The object does not expose the requested pattern part.",
                            hint: "Check the part name; pattern parts are typically 'PatternInstance'.",
                            target: target);
                    }
                    // Internal read response — intentionally not canonical so TryExtractError can probe it.
                    return new JObject
                    {
                        ["status"] = "ok",
                        ["part"] = string.IsNullOrWhiteSpace(resolvedPatternPartName) ? resolvedPart : resolvedPatternPartName,
                        ["source"] = patternXml
                    }.ToString();
                }

                KBObjectPart part = PartAccessor.GetPart(obj, resolvedPart);
                if (part == null)
                {
                    return Models.McpResponse.Err(
                        code: "PartNotFound",
                        message: "The object does not expose the requested part.",
                        hint: "Use genexus_read on the target to list available parts.",
                        nextSteps: new JArray(
                            Models.McpResponse.NextStep(
                                tool: "genexus_read",
                                args: new JObject { ["name"] = target },
                                why: "Returns availableParts so the next write picks a valid one.")),
                        target: target);
                }

                string source = null;
                if (part is global::Artech.Genexus.Common.Parts.VariablesPart varPart)
                {
                    // Variables part is exposed via DSL serialization
                    source = GxMcp.Worker.Helpers.VariableInjector.GetVariablesAsText(varPart);
                }
                else if (resolvedPart.Equals("Structure", StringComparison.OrdinalIgnoreCase) &&
                         (obj is global::Artech.Genexus.Common.Objects.Transaction
                          || obj is global::Artech.Genexus.Common.Objects.Table
                          || obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)))
                {
                    source = GxMcp.Worker.Helpers.StructureParser.SerializeToText(obj);
                }
                else if (GxMcp.Worker.Helpers.WebFormXmlHelper.IsVisualPart(resolvedPart))
                {
                    // WebForm/Layout: expose the GxMultiForm XML as patchable text.
                    source = GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj);
                }
                else if (part is ISource sourcePart)
                {
                    source = sourcePart.Source;
                }
                else
                {
                    var contentProp = part.GetType().GetProperty("Source") ?? part.GetType().GetProperty("Content");
                    if (contentProp != null && contentProp.CanRead && contentProp.PropertyType == typeof(string))
                    {
                        source = contentProp.GetValue(part)?.ToString();
                    }
                }

                if (source == null)
                {
                    return Models.McpResponse.Err(
                        code: "PartNoTextSource",
                        message: "Part does not expose a text source. Patch operations require a textual part.",
                        hint: "Visual or binary parts cannot be patched via this operation.",
                        target: target);
                }

                // Internal read response — intentionally not canonical so TryExtractError can probe it.
                return new JObject
                {
                    ["status"] = "ok",
                    ["part"] = resolvedPart,
                    ["source"] = source
                }.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "PatchReadFailed",
                    message: "Patch read failed: " + ex.Message,
                    hint: "Ensure the target object and part are accessible.",
                    target: target);
            }
        }

        private static string BuildCacheKey(string target, string partName, string typeFilter)
        {
            string normalizedTarget = target?.Trim() ?? string.Empty;
            string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName.Trim();
            string normalizedType = typeFilter?.Trim() ?? string.Empty;
            return normalizedType + "|" + normalizedTarget + "|" + normalizedPart;
        }

        private static string TryGetCachedSource(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey)) return null;
            if (!_sourceCache.TryGetValue(cacheKey, out var entry) || entry == null) return null;
            if (DateTime.UtcNow - entry.UpdatedUtc > SourceCacheTtl)
            {
                _sourceCache.TryRemove(cacheKey, out _);
                return null;
            }

            return entry.Source;
        }

        private static void UpdateCachedSource(string cacheKey, string source, string versionToken = null)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || source == null) return;
            _sourceCache[cacheKey] = new SourceCacheEntry
            {
                Source = source,
                VersionToken = versionToken,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Friction 2026-05-22 #8: classify a post-fallback "SDK reported failure" outcome.
        /// Inputs are the cheap-to-compute "what does disk look like now?" booleans plus
        /// the SDK error text. Output is the envelope shape the caller should apply to
        /// writePayload. Pure function — unit-tested without standing up the SDK.
        /// </summary>
        public sealed class FallbackFailureClassification
        {
            /// <summary>"Success" or "Error" — the value to put on writePayload.status.</summary>
            public string Status { get; set; }
            /// <summary>Stable code: "write_not_persisted" or "post_write_hash_drift".</summary>
            public string Code { get; set; }
            /// <summary>"write_not_persisted" or "persisted_with_concurrent_change".</summary>
            public string Mode { get; set; }
            /// <summary>Human-readable error/note text.</summary>
            public string Message { get; set; }
            /// <summary>True when the agent's safe move is to re-read before retrying.</summary>
            public bool RequiresReread { get; set; }
            /// <summary>True when the patched content actually landed despite the SDK signal.</summary>
            public bool PatchLanded { get; set; }
        }

        public static FallbackFailureClassification ClassifyFallbackFailure(
            bool persistedMatchesOriginal,
            bool persistedMatchesFinal,
            string fallbackError)
        {
            if (persistedMatchesFinal)
            {
                return new FallbackFailureClassification
                {
                    Status = "Success",
                    Code = "post_write_hash_drift",
                    Mode = "persisted_with_concurrent_change",
                    Message = "Primary write landed; SDK reported a fallback-write failure but the persisted bytes match the patched content. Treating as Success.",
                    RequiresReread = false,
                    PatchLanded = true
                };
            }
            if (!persistedMatchesOriginal)
            {
                return new FallbackFailureClassification
                {
                    Status = "Success",
                    Code = "post_write_hash_drift",
                    Mode = "persisted_with_concurrent_change",
                    Message = "A concurrent write modified this target between our write and our verify. The on-disk source no longer matches either our input or the original — re-read before any further patch.",
                    RequiresReread = true,
                    PatchLanded = false
                };
            }
            return new FallbackFailureClassification
            {
                Status = "Error",
                Code = "write_not_persisted",
                Mode = "write_not_persisted",
                Message = "Write did not reach disk (SDK fallback failed and the on-disk source is unchanged). Retry is safe.",
                RequiresReread = false,
                PatchLanded = false
            };
        }

        internal static void InvalidateAllSourceCaches() => _sourceCache.Clear();

        public static void InvalidateCachedSource(string target, string partName, string typeFilter)
        {
            try
            {
                string cacheKey = BuildCacheKey(target, partName, typeFilter);
                _sourceCache.TryRemove(cacheKey, out _);
            }
            catch { }
        }

        public static void InvalidateAllForTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            string normalizedTarget = target.Trim();
            foreach (var key in _sourceCache.Keys)
            {
                if (key.IndexOf("|" + normalizedTarget + "|", StringComparison.OrdinalIgnoreCase) >= 0)
                    _sourceCache.TryRemove(key, out _);
            }
        }

        private string ReadFreshVersionToken(string target, string partName, string typeFilter)
        {
            try
            {
                string response = _objectService.ReadObjectSourceForVerification(target, partName, typeFilter);
                if (!string.IsNullOrWhiteSpace(TryExtractError(response))) return null;
                return JObject.Parse(response)["versionToken"]?.ToString();
            }
            catch { return null; }
        }

        private TextPersistenceVerifier.Result ReadAndVerifyPersistedSource(
            string target,
            string partName,
            string typeFilter,
            string expectedSource,
            string verifyMode,
            out string persistedSource,
            out string error)
        {
            persistedSource = null;
            error = null;
            try
            {
                string verifyKey = BuildCacheKey(target, partName, typeFilter);
                _sourceCache.TryRemove(verifyKey, out _);
                string readResponse = _objectService.ReadObjectSourceForVerification(target, partName, typeFilter);
                error = TryExtractError(readResponse);
                if (!string.IsNullOrWhiteSpace(error)) return null;

                var readJson = JObject.Parse(readResponse);
                persistedSource = readJson["source"]?.ToString() ?? string.Empty;
                return TextPersistenceVerifier.Evaluate(expectedSource, persistedSource, verifyMode, partName);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private bool VerifyPersistedSource(string target, string partName, string typeFilter, string expectedSource, out string error)
        {
            error = null;
            try
            {
                // Friction-report #6: post-write verification must read from a forced cache miss.
                // Without this, the ObjectService _readCache or the patch-local _sourceCache can
                // hold pre-write content and falsely report persistedVerified=true even though the
                // next user-facing read shows stale source. Drop both caches before the verify read.
                //
                // FR#2 (friction-report 2026-05-14): the SDK sometimes flushes to disk slightly
                // after Save() returns. Verify once immediately; if it disagrees, retry after a
                // short pause before reporting Error. This collapses the false-negative
                // "persistedVerified=false but next genexus_read shows the write applied" case
                // that produced spurious fallback writes / auto-rollbacks.
                string verifyKey = BuildCacheKey(target, partName, typeFilter);
                string expectedNormalized = NormalizeForPartCompare(partName, expectedSource);
                int attempts = 2;
                for (int i = 0; i < attempts; i++)
                {
                    _sourceCache.TryRemove(verifyKey, out _);
                    try
                    {
                        var verifyObj = _objectService.FindObject(target, typeFilter);
                        if (verifyObj != null) _objectService.MarkReadCacheDirty(verifyObj, partName);
                    }
                    catch { }

                    string verifyReadResponse = ReadSourceFast(target, partName, typeFilter);
                    string verifyReadError = TryExtractError(verifyReadResponse);
                    if (!string.IsNullOrWhiteSpace(verifyReadError))
                    {
                        error = verifyReadError;
                        return false;
                    }

                    var verifyJson = JObject.Parse(verifyReadResponse);
                    string persistedSource = verifyJson["source"]?.ToString() ?? string.Empty;
                    if (NormalizeForPartCompare(partName, persistedSource) == expectedNormalized)
                    {
                        return true;
                    }

                    if (i < attempts - 1)
                    {
                        // SDK persistence can lag the Save() return; brief pause before retry.
                        System.Threading.Thread.Sleep(120);
                    }
                    else
                    {
                        // Surface a small diff sample so callers can decide instead of guessing.
                        error = BuildVerifyDiffHint(expectedSource, persistedSource);
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // When verify mismatches, surface a line-based slice of the actual on-disk content
        // so the agent can confirm the state without a follow-up genexus_read call.
        private void AttachPersistedSnippet(JObject writePayload, string target, string partName, string typeFilter, string expectedSource)
        {
            try
            {
                string readResp = ReadSourceFast(target, partName, typeFilter);
                if (!string.IsNullOrWhiteSpace(TryExtractError(readResp))) return;
                var json = JObject.Parse(readResp);
                string persisted = json["source"]?.ToString() ?? string.Empty;

                string[] persistedLines = persisted.Replace("\r\n", "\n").Split('\n');
                string[] expectedLines = (expectedSource ?? string.Empty).Replace("\r\n", "\n").Split('\n');

                int divergeLine = 0;
                int max = Math.Min(persistedLines.Length, expectedLines.Length);
                while (divergeLine < max && persistedLines[divergeLine] == expectedLines[divergeLine]) divergeLine++;

                const int contextLines = 10;
                int start = Math.Max(0, divergeLine - contextLines);
                int end = Math.Min(persistedLines.Length, divergeLine + contextLines + 1);
                int len = Math.Max(0, end - start);

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < len; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(persistedLines[start + i]);
                }

                writePayload["persistedSnippet"] = new JObject
                {
                    ["startLine"] = start + 1,
                    ["divergeLine"] = divergeLine + 1,
                    ["content"] = sb.ToString(),
                    ["totalLines"] = persistedLines.Length
                };
            }
            catch { /* keep payload valid even if snippet capture fails */ }
        }

        // v2.3.8 Task 4.6 — patch-window-only rollback verification.
        //
        // Inputs:
        //   workSource    = LF-normalized original on-disk source BEFORE the patch.
        //   updatedSource = LF-normalized source we computed from the patch (what we wanted).
        //   finalCode     = CRLF version that was actually handed to WriteService.WriteObject().
        // Output:
        //   sideEffects   = JArray of {line, before, after} describing every out-of-window
        //                   normalization the SDK applied on save. Only set when the method
        //                   returns true (i.e. all hunks are out-of-window).
        //
        // Strategy:
        //   1. Edit window = union of line ranges of HunkDiff(workSource, updatedSource).
        //   2. Read persisted source from disk (forced cache miss inside ReadSourceFast).
        //   3. divergenceHunks = HunkDiff(updatedSource, persistedSource).
        //   4. If any divergence hunk overlaps the edit window → return false (real divergence).
        //   5. Else → return true; serialize the out-of-window hunks.
        internal bool TryClassifyOutOfWindowOnly(
            string target,
            string partName,
            string typeFilter,
            string workSource,
            string updatedSource,
            string finalCode,
            out JArray sideEffects)
        {
            sideEffects = null;
            try
            {
                if (string.IsNullOrEmpty(updatedSource) || string.IsNullOrEmpty(workSource)) return false;

                // 1. Compute edit window (1-based inclusive line range).
                var editHunks = GxMcp.Worker.Helpers.XmlEquivalence.HunkDiff(workSource, updatedSource);
                if (editHunks.Count == 0) return false; // nothing changed — nothing to classify
                int windowStart = int.MaxValue, windowEnd = 0;
                foreach (var h in editHunks)
                {
                    int hStart = h.Line;
                    int hEnd = h.Line + Math.Max(0, h.BeforeLineCount - 1);
                    if (h.BeforeLineCount == 0) hEnd = h.Line;
                    if (hStart < windowStart) windowStart = hStart;
                    if (hEnd > windowEnd) windowEnd = hEnd;
                }
                if (windowEnd < windowStart) return false;

                // 2. Read persisted source.
                string readResp = ReadSourceFast(target, partName, typeFilter);
                if (!string.IsNullOrWhiteSpace(TryExtractError(readResp))) return false;
                var json = JObject.Parse(readResp);
                string persisted = json["source"]?.ToString();
                if (persisted == null) return false;

                string persistedLf = persisted.Replace("\r\n", "\n").Replace("\r", "\n");
                string requestedLf = (finalCode ?? updatedSource).Replace("\r\n", "\n").Replace("\r", "\n");

                // Fast equality check (raw, not part-normalized).
                if (string.Equals(requestedLf, persistedLf, StringComparison.Ordinal))
                {
                    sideEffects = new JArray();
                    return true;
                }

                // 3. Classify post-save divergence hunks.
                var divergeHunks = GxMcp.Worker.Helpers.XmlEquivalence.HunkDiff(requestedLf, persistedLf);
                if (divergeHunks.Count == 0) { sideEffects = new JArray(); return true; }

                var outOfWindow = new JArray();
                foreach (var h in divergeHunks)
                {
                    if (GxMcp.Worker.Helpers.XmlEquivalence.HunkOverlapsWindow(h, windowStart, windowEnd))
                    {
                        // A hunk INSIDE the edited window means what we asked for is not what
                        // landed on disk. That's a real verification failure → caller rolls back.
                        return false;
                    }
                    outOfWindow.Add(new JObject
                    {
                        ["line"] = h.Line,
                        ["before"] = h.Before,
                        ["after"] = h.After
                    });
                }

                sideEffects = outOfWindow;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug("[PATCH] window-classification skipped: " + ex.Message);
                return false;
            }
        }

        // FR#2: emit a compact diff hint so the caller sees WHY verify disagreed instead of
        // a bare false. We deliberately cap the dump to keep responses small.
        private static string BuildVerifyDiffHint(string expected, string actual)
        {
            try
            {
                string e = NormalizeSourceForComparison(expected ?? string.Empty);
                string a = NormalizeSourceForComparison(actual ?? string.Empty);
                if (e == a) return "Mismatch under part-specific normalization (likely Variables ordering / NUMERIC(N,0) folding).";
                int firstDiff = 0;
                int max = Math.Min(e.Length, a.Length);
                while (firstDiff < max && e[firstDiff] == a[firstDiff]) firstDiff++;
                int snippetStart = Math.Max(0, firstDiff - 40);
                int snippetLen = Math.Min(80, Math.Max(e.Length, a.Length) - snippetStart);
                string expectedSnippet = snippetStart < e.Length ? e.Substring(snippetStart, Math.Min(snippetLen, e.Length - snippetStart)) : "";
                string actualSnippet   = snippetStart < a.Length ? a.Substring(snippetStart, Math.Min(snippetLen, a.Length - snippetStart)) : "";
                return $"Verify diff at char {firstDiff}: expected='{expectedSnippet}' actual='{actualSnippet}'";
            }
            catch
            {
                return "Verification mismatch (diff unavailable).";
            }
        }

        private static string AttachTimings(string patchResultJson, long readMs, long patchMs, long writeMs, bool usedSourceCache)
        {
            try
            {
                var json = JObject.Parse(patchResultJson);
                json["timings"] = new JObject
                {
                    ["readMs"] = readMs,
                    ["patchMs"] = patchMs,
                    ["writeMs"] = writeMs,
                    ["usedSourceCache"] = usedSourceCache
                };
                return json.ToString();
            }
            catch
            {
                return patchResultJson;
            }
        }

        /// <summary>
        /// v2.8.0: emits canonical envelope shape.
        /// Success-family (Applied, NoChange, DryRun) → McpResponse.Ok with result payload.
        /// Error-family (NoMatch, Ambiguous, Stale, Error) → McpResponse.Err with hint+nextSteps.
        /// The signature is unchanged so all call-sites continue working without modification.
        /// </summary>
        private static string BuildPatchResult(string patchStatus, string partName, string operation, int expectedCount, int matchCount, string details)
        {
            string part = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
            var resultPayload = new JObject
            {
                ["part"] = part,
                ["operation"] = operation,
                ["expectedCount"] = expectedCount,
                ["matchCount"] = matchCount
            };
            if (!string.IsNullOrWhiteSpace(details))
                resultPayload["details"] = details;

            bool isOk = string.Equals(patchStatus, "Applied", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(patchStatus, "NoChange", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(patchStatus, "DryRun", StringComparison.OrdinalIgnoreCase);

            if (isOk)
            {
                return Models.McpResponse.Ok(code: patchStatus, result: resultPayload);
            }

            // Error-family: derive a concrete hint + nextStep per status code.
            string hint;
            JArray nextSteps = null;
            switch (patchStatus.ToLowerInvariant())
            {
                case "nomatch":
                    hint = "The context block was not found in the part. Re-read the object source and copy the exact lines (tabs, whitespace, EOL) as the context.";
                    nextSteps = new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = "(target)", ["part"] = part },
                        why: "Returns the current source so you can copy a verbatim context block and retry the patch."));
                    break;
                case "ambiguous":
                    hint = $"The context matched {matchCount} location(s) but expected {expectedCount}. Add more surrounding lines to make it unique, or pass replaceAll=true to apply to every match.";
                    // no-nextStep: the right action depends on caller intent (unique vs replaceAll); too divergent to prescribe a single tool call.
                    break;
                case "stale":
                    hint = "A concurrent write landed against this target while the patch was queued. Re-read the object source and rebuild the context from fresh content.";
                    nextSteps = new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = "(target)", ["part"] = part },
                        why: "Fetches the current (post-concurrent-write) source so the patch context can be refreshed."));
                    break;
                default: // "Error" and anything unexpected
                    hint = "Check the operation, context, and part name; then retry.";
                    nextSteps = new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = "(target)", ["part"] = part },
                        why: "Verify the part exists and read its current source before retrying."));
                    break;
            }

            // Embed the result payload inside the error envelope as extra fields so
            // diagnostics (part, operation, expectedCount, matchCount, details) are not lost.
            var extra = new JObject
            {
                ["part"] = part,
                ["operation"] = operation,
                ["expectedCount"] = expectedCount,
                ["matchCount"] = matchCount
            };

            return Models.McpResponse.Err(
                code: patchStatus,
                message: string.IsNullOrWhiteSpace(details) ? $"Patch {patchStatus}." : details,
                hint: hint,
                nextSteps: nextSteps,
                extra: extra);
        }
    }
}
