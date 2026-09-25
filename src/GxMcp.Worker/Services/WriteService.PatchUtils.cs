using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    // Patch-matching + persisted-state utilities extracted from WriteService.cs (plan 007).
    // Pure move, no logic changes — see plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
        // ----------------------------------------------------------------------
        // v2.3.8 Task 3.1 — EOL-normalized matching helpers (friction-report #4)
        // ----------------------------------------------------------------------
        // Source bytes are preserved on disk; only the comparison is normalized.
        // CRLF/LF are unified and per-line trailing whitespace is trimmed before
        // matching. TryMatch returns indices into the ORIGINAL (non-normalized)
        // source so callers can splice in replacements without corrupting EOLs.

        internal static string NormalizeForCompare(string s)
        {
            if (s == null) return null;
            var lines = s.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
            return string.Join("\n", lines);
        }

        internal static bool TryMatch(string source, string context, out int startIdx, out int endIdx)
        {
            startIdx = endIdx = -1;
            if (source == null || context == null) return false;
            var normSource = NormalizeForCompare(source);
            var normCtx = NormalizeForCompare(context);
            if (normCtx.Length == 0) return false;
            int normIdx = normSource.IndexOf(normCtx, StringComparison.Ordinal);
            if (normIdx < 0) return false;

            int targetLineStart = CountLinesBefore(normSource, normIdx);
            // Walk to the start of the target line in the original source.
            int origPos = 0;
            for (int line = 0; line < targetLineStart && origPos < source.Length; line++)
            {
                int nl = source.IndexOfAny(new[] { '\r', '\n' }, origPos);
                if (nl < 0) { origPos = source.Length; break; }
                origPos = nl + ((source[nl] == '\r' && nl + 1 < source.Length && source[nl + 1] == '\n') ? 2 : 1);
            }

            // Compute column within the normalized line where match starts.
            int prevNL = normSource.LastIndexOf('\n', Math.Max(0, normIdx - 1));
            int normLineStart = prevNL < 0 ? 0 : prevNL + 1;
            int colOffset = normIdx - normLineStart;
            startIdx = Math.Min(source.Length, origPos + colOffset);

            // Walk forward over (ctxLineCount) lines to find the end position in the original source.
            int ctxLineCount = CountLinesBefore(normCtx, normCtx.Length);
            int walker = startIdx;
            for (int i = 0; i < ctxLineCount && walker < source.Length; i++)
            {
                int nl = source.IndexOfAny(new[] { '\r', '\n' }, walker);
                if (nl < 0) { walker = source.Length; break; }
                walker = nl + ((source[nl] == '\r' && nl + 1 < source.Length && source[nl + 1] == '\n') ? 2 : 1);
            }

            // Add the residual column length on the last context line.
            int lastNL = normCtx.LastIndexOf('\n');
            int lastLineLen = lastNL < 0 ? normCtx.Length : (normCtx.Length - lastNL - 1);
            endIdx = Math.Min(source.Length, walker + lastLineLen);
            if (endIdx < startIdx) endIdx = startIdx;
            return true;
        }

        private static int CountLinesBefore(string s, int idx)
        {
            int c = 0;
            int limit = Math.Min(idx, s.Length);
            for (int i = 0; i < limit; i++) if (s[i] == '\n') c++;
            return c;
        }

        // ----------------------------------------------------------------------
        // v2.3.8 Task 3.4 — persistedHash + persistedSnippet on every response
        // ----------------------------------------------------------------------
        // Every write/edit response is wrapped with the SHA256 of the final
        // on-disk source plus a ~10-line snippet, so callers can confirm
        // post-write state without a follow-up read. Applies uniformly to
        // success, no-change, dry-run, rollback, and error responses.

        // ----------------------------------------------------------------------
        // v2.6.6 FR#10 — patch safety guard.
        // ----------------------------------------------------------------------
        /// <summary>
        /// Reject suspicious writes that would silently nuke an object part. A
        /// patch find-string mismatch (CRLF/LF, encoding drift) used to surface
        /// as an empty result string; the unguarded SDK save then persisted the
        /// empty payload and the sha256 of the lost part was e3b0c44... (empty).
        ///
        /// Returns <c>true</c> when the proposed write looks safe. When it
        /// returns <c>false</c>, <paramref name="reason"/> carries a stable
        /// machine-readable code (<c>patch_no_match</c> / <c>suspicious_shrink</c>)
        /// the gateway promotes to an <c>isError</c> envelope.
        /// </summary>
        public static bool IsPatchWriteSafe(string originalContent, string proposedContent, bool anyOpApplied, out string reason)
        {
            reason = null;
            if (proposedContent == null)
            {
                reason = "patch_no_match";
                return false;
            }

            int origLen = originalContent?.Length ?? 0;
            int newLen = proposedContent.Length;

            // Empty proposal with non-empty original is unsafe only when no patch
            // operation was actually applied. A confirmed Replace of the complete
            // part with content="" is an intentional deletion and must reach the SDK.
            if (origLen > 0 && newLen == 0 && !anyOpApplied)
            {
                reason = "patch_no_match";
                return false;
            }

            // Severe shrink with no recorded op == NoMatch fall-through. The
            // 0.5 ratio matches the brief; tune via tests rather than ad-hoc.
            if (!anyOpApplied && origLen > 0 && newLen < origLen / 2)
            {
                reason = "suspicious_shrink";
                return false;
            }

            return true;
        }

        internal static string ComputeSha256(string content)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content ?? ""));
                return "sha256:" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static string ExtractSnippet(string source, int lineHint, int contextLines = 10)
        {
            if (string.IsNullOrEmpty(source)) return "";
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var start = Math.Max(0, lineHint - contextLines);
            var end = Math.Min(lines.Length, lineHint + contextLines + 1);
            if (end <= start) return "";
            return string.Join("\n", lines.Skip(start).Take(end - start));
        }

        // First line index (0-based) that differs between two texts, or 0 when identical /
        // one is empty. Used to center the persisted snippet on the changed region.
        internal static int FirstDiffLine(string before, string after)
        {
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after)) return 0;
            var a = before.Replace("\r\n", "\n").Split('\n');
            var b = after.Replace("\r\n", "\n").Split('\n');
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return i;
            return a.Length == b.Length ? 0 : n;
        }

        internal static JObject AppendPersistedState(JObject response, string finalSource, int? editLine)
        {
            if (response == null) response = new JObject();
            response["persistedHash"] = ComputeSha256(finalSource ?? "");
            response["persistedSnippet"] = ExtractSnippet(finalSource ?? "", editLine ?? 0, 10);
            return response;
        }

        /// <summary>
        /// Wraps a write-response JSON string with persistedHash + persistedSnippet derived
        /// from the on-disk source after the write attempt (success, partial, or rollback).
        /// Failures to re-read are swallowed — the original envelope is still augmented with
        /// an empty hash/snippet so downstream parsers always find the keys.
        /// </summary>
        private string WrapWithPersistedState(string responseJson, string target, string partName, string sdkPath = null, string priorSource = null, string requestedContent = null, string typeFilter = null, string verifyMode = null, bool requireObjectSave = false)
        {
            JObject parsed = null;
            try { parsed = JObject.Parse(responseJson); }
            catch
            {
                parsed = new JObject
                {
                    ["status"] = "error",
                    ["code"] = "WriteVerificationUnavailable",
                    ["message"] = "The write response was not valid JSON, so its persistence outcome is unknown.",
                    ["saveAttempted"] = true,
                    ["raw"] = responseJson ?? ""
                };
            }

            GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(parsed, sdkPath);

            // Skip if the response is already decorated (e.g. nested call).
            if (parsed["persistedHash"] != null && parsed["persistedSnippet"] != null)
                return parsed.ToString();

            bool isDryRun = string.Equals(parsed["code"]?.ToString(), "WriteDryRun", StringComparison.OrdinalIgnoreCase);
            bool responseMarkedDryRun = parsed["dryRun"]?.Value<bool?>() == true
                || parsed["result"]?["dryRun"]?.Value<bool?>() == true;
            isDryRun = isDryRun || responseMarkedDryRun;
            bool suppressVisualSourceEcho = isDryRun && WebFormXmlHelper.IsVisualPart(partName);

            string finalSource = "";
            string finalVersionToken = null;
            bool verificationReadTruncated = false;
            string verificationReadFailure = string.IsNullOrWhiteSpace(target) || _objectService == null ? "readerUnavailable" : null;
            try
            {
                if (!string.IsNullOrWhiteSpace(target) && _objectService != null)
                {
                    // P4 perf: In dryRun mode, disk state was not mutated.
                    // If priorSource is already known, reuse it directly to avoid any I/O.
                    // Otherwise perform a cached read (ReadObjectSource) instead of
                    // invalidating the read cache and forcing a full database reload via ReadObjectSourceForVerification.
                    if (isDryRun && priorSource != null)
                    {
                        finalSource = priorSource;
                    }
                    else
                    {
                        string readJson = isDryRun
                            ? _objectService.ReadObjectSource(target, partName, offset: 0, limit: 0, client: "mcp", minimize: false, typeFilter: typeFilter)
                            : _objectService.ReadObjectSourceForVerification(target, partName, typeFilter);
                        if (!string.IsNullOrWhiteSpace(readJson))
                        {
                            if (!TryReadCompleteVerificationSource(
                                readJson,
                                partName,
                                out finalSource,
                                out finalVersionToken,
                                out verificationReadTruncated,
                                out verificationReadFailure,
                                allowSerializedPart: true))
                            {
                                finalSource = "";
                            }
                        }
                        else
                        {
                            verificationReadFailure = "emptyReadResponse";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                verificationReadFailure = ex.GetType().Name;
                Logger.Debug("[PERSISTED-STATE] Re-read failed for " + target + " (" + partName + "): " + ex.Message);
            }

            // issue #31.3: center the snippet on the first changed line when we know the
            // prior source, so the edited region is shown even past the first ~10 lines.
            int? editLine = priorSource != null ? (int?)FirstDiffLine(priorSource, finalSource) : null;
            bool verificationReadReliable = !verificationReadTruncated && verificationReadFailure == null;
            if (verificationReadReliable)
            {
                AppendPersistedState(parsed, finalSource, editLine);
                if (suppressVisualSourceEcho)
                {
                    parsed["sourceEchoOmitted"] = true;
                    parsed["sourcePreview"] = Truncate(finalSource, 2000);
                    string snippet = parsed["persistedSnippet"]?.ToString();
                    if (!string.IsNullOrEmpty(snippet) && snippet.Length > 2000)
                        parsed["persistedSnippet"] = snippet.Substring(0, 2000) + "…[truncated]";
                }
                else
                {
                    parsed["source"] = finalSource;
                }
                parsed[isDryRun ? "currentState" : "postSaveVerification"] = new JObject
                {
                    ["reReadConfirmed"] = true,
                    ["versionToken"] = finalVersionToken
                };
                if (!isDryRun && !string.IsNullOrEmpty(finalSource))
                {
                    string targetGuid = parsed["guid"]?.ToString() ?? parsed["result"]?["guid"]?.ToString();
                    if (!string.IsNullOrEmpty(targetGuid))
                    {
                        SourceStoreService.Instance.Put(targetGuid, partName, finalSource, DateTime.UtcNow, finalVersionToken);
                    }
                }
            }
            else
            {
                // Keep the stable keys without advertising a hash of a partial/unknown read
                // as though it represented the complete persisted part.
                parsed["persistedHash"] = null;
                parsed["persistedSnippet"] = null;
                parsed[isDryRun ? "currentState" : "postSaveVerification"] = new JObject
                {
                    ["reReadConfirmed"] = false,
                    ["reason"] = verificationReadFailure ?? (verificationReadTruncated ? "truncation" : "unknown")
                };
            }
            parsed["implicitLifecycleActions"] = new JArray();
            if (isDryRun)
            {
                parsed["persisted"] = false;
                parsed["mutationDetected"] = false;
            }

            if (requestedContent != null)
                parsed = ApplyTextVerificationReceipt(parsed, target, partName, priorSource,
                    requestedContent, finalSource, finalVersionToken, verificationReadTruncated,
                    verificationReadFailure, verifyMode, isDryRun, requireObjectSave);

            // issue #31.2: when the write left the persisted content byte-identical to the
            // prior content, this was a no-op — surface WriteNoChange instead of WriteApplied
            // so callers don't have to diff the hash themselves.
            if (priorSource != null)
            {
                bool changed = !string.Equals(ComputeSha256(priorSource), parsed["persistedHash"]?.ToString(), StringComparison.OrdinalIgnoreCase);
                parsed["changed"] = changed;
                string code = parsed["code"]?.ToString();
                if (!changed && string.Equals(code, "WriteApplied", StringComparison.OrdinalIgnoreCase))
                {
                    parsed["code"] = "WriteNoChange";

                    // issue #36.6 — `changed:false` (persisted == prior) was ambiguous: callers
                    // could not tell "the requested content was already present" (idempotent,
                    // safe) from "the write was dropped" (bug). When we know what was requested,
                    // compare it (whitespace-insensitive) against the persisted state and, ONLY
                    // when they match, assert requestedApplied:true — a positive idempotent
                    // signal. We never assert a "drop" here (normalization differences could
                    // false-alarm); absence of the flag means "verify via persistedSnippet".
                    bool? requestedApplied = null;
                    if (requestedContent != null)
                        requestedApplied = EvaluatePersistedVerification(requestedContent, finalSource, verificationReadTruncated, verificationReadFailure, verifyMode, partName).Matches;

                    if (requestedApplied == true)
                    {
                        parsed["requestedApplied"] = true;
                        parsed["noChangeReason"] = "The requested content is already present — this was an idempotent no-op (persisted state matches your request). Nothing needed to change.";
                    }
                    else
                    {
                        parsed["noChangeReason"] = "Persisted content is byte-identical to what was there before this call. If you expected a change, compare your requested content against persistedSnippet — the edit may have been a no-op or dropped.";
                    }
                }
            }

            return parsed.ToString(Newtonsoft.Json.Formatting.None);
        }

        // Pure receipt construction keeps the physical save result independent of
        // comparison and lets synchronous and asynchronous callers share evidence.
        internal static JObject ApplyTextVerificationReceipt(JObject response, string target,
            string part, string before, string requested, string actual, string versionToken,
            bool truncated, string readFailure, string verifyMode, bool dryRun = false, bool requireObjectSave = false)
        {
            string code = response["code"]?.ToString();
            bool successful = string.Equals(response["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(response["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
            bool writeApplied = string.Equals(code, "WriteApplied", StringComparison.OrdinalIgnoreCase);
            bool noChange = string.Equals(code, "WriteNoChange", StringComparison.OrdinalIgnoreCase);
            bool applied = successful && (writeApplied || noChange);
            bool? saved = dryRun ? false : response["sdkSaveCompleted"]?.Value<bool?>()
                ?? response["result"]?["sdkSaveCompleted"]?.Value<bool?>()
                ?? response["saved"]?.Value<bool?>() ?? (successful && writeApplied ? (bool?)true : successful && noChange ? (bool?)false : null);
            string commitState = response["commitState"]?.ToString()
                ?? response["result"]?["commitState"]?.ToString();
            bool physicalCommit = !dryRun && saved == true
                && string.Equals(commitState, "Committed", StringComparison.OrdinalIgnoreCase);
            if (response["commitState"] == null && !string.IsNullOrWhiteSpace(commitState))
                response["commitState"] = commitState;
            bool? objectSaved = response["objectSaved"]?.Value<bool?>()
                ?? response["result"]?["objectSaved"]?.Value<bool?>()
                ?? (successful && noChange ? (bool?)false : null);
            if (response["metadataStampPersisted"] == null && response["result"]?["metadataStampPersisted"] != null)
                response["metadataStampPersisted"] = response["result"]["metadataStampPersisted"].DeepClone();
            var verification = EvaluatePersistedVerification(requested, actual, truncated, readFailure, verifyMode, part);
            bool known = !verification.IsIndeterminate;
            var diff = BuildPersistenceDiff(requested, actual, verification, known);
            response["sdkSaveCompleted"] = saved.HasValue ? (JToken)saved.Value : JValue.CreateNull();
            response["saved"] = response["sdkSaveCompleted"].DeepClone();
            // Errors can occur after EnsureSave/Commit was attempted. Do not turn
            // absent evidence into an assertion that no SDK save ran.
            if (dryRun || successful && noChange) response["saveAttempted"] = false;
            else if (successful && writeApplied) response["saveAttempted"] = true;
            response["part"] = part;
            if (response["target"] == null) response["target"] = target;
            if (requireObjectSave)
            {
                response["requireObjectSave"] = true;
                response["objectSaved"] = objectSaved.HasValue ? (JToken)objectSaved.Value : JValue.CreateNull();
                response["partPersisted"] = !dryRun && known && verification.Matches;
                response["saveContract"] = "transactional-object-save";
            }
            response["persistedStateKnown"] = known;
            response["verified"] = !dryRun && known && verification.Matches;
            response["persistedVerified"] = response["verified"];
            // A visual write can be physically committed even when the requested
            // XML did not round-trip. Preserve that fact instead of reporting
            // persisted=false and steering callers into a blind retry.
            response["persisted"] = !dryRun && ((known && verification.Matches) || physicalCommit);
            response["versionToken"] = known ? versionToken : null;
            response["implicitLifecycleActions"] = new JArray();
            response[dryRun ? "currentState" : "postSaveVerification"] = new JObject
            {
                ["reReadConfirmed"] = known,
                ["matches"] = known && verification.Matches,
                ["representation"] = "genexus_read",
                ["readOptions"] = new JObject { ["offset"] = 0, ["limit"] = 0 },
                ["reason"] = verification.Reason,
                ["versionToken"] = known ? versionToken : null,
                ["mode"] = verifyMode ?? "legacy",
                ["diff"] = diff.DeepClone()
            };
            response["mutation"] = new JObject
            {
                ["before"] = DescribeContent(before), ["requested"] = DescribeContent(requested),
                ["persisted"] = known ? DescribeContent(actual) : null,
                ["diff"] = diff, ["verification"] = dryRun ? "dryRun" : verification.State,
                ["saved"] = response["sdkSaveCompleted"].DeepClone()
            };
            if (!dryRun && applied && !verification.Matches)
            {
                response["status"] = "error";
                response["code"] = known ? "WriteNotPersisted" : "WriteVerificationUnavailable";
                response["error"] = new JObject
                {
                    ["code"] = response["code"].DeepClone(),
                    ["message"] = known
                        ? "The persisted public-read text differs from the request; inspect the diff. The SDK save result is reported separately."
                        : "The post-save state could not be read reliably. The SDK save result is reported separately.",
                    ["hint"] = "Do not retry automatically. Read the complete current state and its versionToken before deciding on another edit."
                };
                response["partialPersistenceDetected"] = known && before != null
                    && !string.Equals(before, actual, StringComparison.Ordinal)
                    && !string.Equals((requested ?? "").Replace("\r\n", "\n").Replace('\r', '\n'), (actual ?? "").Replace("\r\n", "\n").Replace('\r', '\n'), StringComparison.Ordinal);
            }
            if (!dryRun && successful && writeApplied && requireObjectSave && objectSaved != true)
            {
                response["status"] = "error";
                response["code"] = "ObjectSaveIncomplete";
                response["error"] = new JObject
                {
                    ["code"] = "ObjectSaveIncomplete",
                    ["message"] = "The required transactional object-save evidence was not returned. Inspect the independent read evidence before any recovery edit.",
                    ["hint"] = "Do not retry automatically."
                };
            }
            return response;
        }

        private static JObject BuildPersistenceDiff(string expected, string actual, PersistedVerificationResult verification, bool known)
        {
            int line = 0;
            bool eolOnly = string.Equals(
                (expected ?? "").Replace("\r\n", "\n").Replace('\r', '\n'),
                (actual ?? "").Replace("\r\n", "\n").Replace('\r', '\n'),
                StringComparison.Ordinal);

            if (known && !string.Equals(expected, actual, StringComparison.Ordinal))
            {
                if (!verification.Matches || !eolOnly)
                {
                    string left = expected ?? "", right = actual ?? "";
                    int index = 0;
                    while (index < left.Length && index < right.Length && left[index] == right[index]) index++;
                    line = 1;
                    for (int i = 0; i < index; i++)
                        if (left[i] == '\n' || (left[i] == '\r' && (i + 1 >= left.Length || left[i + 1] != '\n'))) line++;
                }
            }
            string[] expectedLines = (expected ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string[] actualLines = (actual ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            // Bounded previews; hashes and the public read provide complete evidence.
            Func<string[], string> preview = lines => line > 0 && line <= lines.Length
                ? lines[line - 1].Substring(0, Math.Min(lines[line - 1].Length, 240)) : null;
            Func<string, string> ending = text =>
            {
                if (!known || text == null) return null;
                int currentLine = 1;
                int targetLine = line > 0 ? line : 1;
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != '\r' && text[i] != '\n') continue;
                    bool crlf = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n';
                    if (currentLine++ == targetLine) return crlf ? "CRLF" : text[i] == '\r' ? "CR" : "LF";
                    if (crlf) i++;
                }
                return currentLine == targetLine ? "none" : null;
            };
            return new JObject
            {
                ["matches"] = verification.Matches, ["reason"] = verification.Reason,
                ["firstDifferentLine"] = known && line > 0 ? (JToken)line : JValue.CreateNull(),
                ["expectedLine"] = known && line > 0 ? preview(expectedLines) : null,
                ["readLine"] = known && line > 0 ? preview(actualLines) : null,
                ["expectedLineEnding"] = ending(expected),
                ["readLineEnding"] = ending(actual),
                ["linePreviewLimit"] = 240
            };
        }

        private string RollbackFullWriteFailure(
            string responseJson,
            string target,
            string partName,
            string typeFilter,
            string priorSource)
        {
            JObject response;
            try { response = JObject.Parse(responseJson); }
            catch { return responseJson; }

            string status = response["status"]?.ToString();
            if (!string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                return responseJson;

            bool visualPart = WebFormXmlHelper.IsVisualPart(partName);
            if (IsPostSaveVerificationIndeterminate(response.ToString(Newtonsoft.Json.Formatting.None)))
            {
                if (string.Equals(partName, "Source", StringComparison.OrdinalIgnoreCase))
                    MarkSourceRollbackUnavailable(response);
                else if (visualPart)
                    MarkVisualRollbackUnavailable(response, "Rollback was not attempted because the post-save state is unknown.");
                return response.ToString(Newtonsoft.Json.Formatting.None);
            }

            string current = response["source"]?.ToString();
            bool physicalEvidence = string.Equals(response["commitState"]?.ToString(), "Committed", StringComparison.OrdinalIgnoreCase)
                || response["sdkSaveCompleted"]?.Value<bool?>() == true
                || response["physicalCommit"]?.Value<bool?>() == true;
            if (current != null && ContentEqualsForRollback(current, priorSource, partName))
            {
                if (visualPart && !physicalEvidence)
                {
                    response["rollback"] = new JObject
                    {
                        ["requested"] = true,
                        ["attempted"] = false,
                        ["rolledBack"] = false,
                        ["verified"] = false,
                        ["atomic"] = false,
                        ["error"] = "No visual write commit was evidenced; rollback was not attempted."
                    };
                    return response.ToString(Newtonsoft.Json.Formatting.None);
                }
                response["rollback"] = new JObject
                {
                    ["requested"] = true,
                    ["attempted"] = false,
                    ["rolledBack"] = true,
                    ["verified"] = true,
                    ["atomic"] = true,
                    ["saveRequired"] = false,
                    ["reReadConfirmed"] = true
                };
                response["rolledBack"] = true;
                response["stateRestored"] = true;
                response["persisted"] = false;
                if (visualPart)
                {
                    response["physicalCommit"] = false;
                    response["commitState"] = "RolledBack";
                    response["persistenceState"] = "Restored";
                }
                return response.ToString(Newtonsoft.Json.Formatting.None);
            }

            if (string.Equals(partName, "Source", StringComparison.OrdinalIgnoreCase))
            {
                // A preflight token check followed by another SDK transaction is not
                // an atomic conditional restore. Fail closed rather than overwrite an
                // IDE/other-Worker edit that arrives between those two operations.
                MarkSourceRollbackUnavailable(response);
                return response.ToString(Newtonsoft.Json.Formatting.None);
            }

            // A restore is only safe when the failed write's post-save version
            // token is available. The public WriteObject path performs the stale
            // check again under its normal concurrency guard before touching the KB.
            string rollbackBaseVersion = response["postSaveVerification"]?["versionToken"]?.ToString();
            if (string.IsNullOrWhiteSpace(rollbackBaseVersion))
                rollbackBaseVersion = response["versionToken"]?.ToString();
            if (string.IsNullOrWhiteSpace(rollbackBaseVersion))
            {
                if (visualPart)
                    MarkVisualRollbackUnavailable(response, "Rollback was not attempted because the post-save version token was unavailable.");
                else
                    response["rollback"] = new JObject
                    {
                        ["requested"] = true,
                        ["attempted"] = false,
                        ["rolledBack"] = false,
                        ["verified"] = false,
                        ["atomic"] = false,
                        ["error"] = "Rollback was not attempted because the post-save version token was unavailable."
                    };
                return response.ToString(Newtonsoft.Json.Formatting.None);
            }

            string restoreError = null;
            JObject restoreEnvelope = null;
            try
            {
                string restore = WriteObject(
                    target,
                    partName,
                    priorSource,
                    typeFilter,
                    autoValidate: false,
                    preferFastSourceSave: false,
                    autoInjectVariables: false,
                    dryRun: false,
                    explicitBase64: false,
                    strictVerify: true,
                    rollbackOnFailure: false,
                    baseVersion: rollbackBaseVersion);
                try { restoreEnvelope = JObject.Parse(restore ?? "{}"); }
                catch { restoreError = "Rollback writer returned invalid JSON."; }

                // A WriteNotPersisted/verification-unavailable response can still
                // have performed the physical restore. Do not trust that envelope
                // alone; the forced read below is the deciding evidence.
                if (restoreEnvelope != null)
                {
                    string restoreStatus = restoreEnvelope["status"]?.ToString();
                    string restoreCode = restoreEnvelope["code"]?.ToString();
                    bool knownRestoreFailure = !string.Equals(restoreStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(restoreStatus, "success", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(restoreCode, "WriteNotPersisted", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(restoreCode, "WriteVerificationUnavailable", StringComparison.OrdinalIgnoreCase);
                    if (knownRestoreFailure && restoreError == null)
                        restoreError = restoreEnvelope["error"]?["message"]?.ToString() ?? restoreEnvelope["message"]?.ToString() ?? "Rollback write failed.";
                }
            }
            catch (Exception ex)
            {
                restoreError = ex.Message;
            }

            string after = null;
            string afterVersion = null;
            try
            {
                if (!TryReadCompleteVerificationSource(
                    _objectService.ReadObjectSourceForVerification(target, partName, typeFilter),
                    partName,
                    out after,
                    out afterVersion,
                    out bool truncated,
                    out string readFailure,
                    allowSerializedPart: true))
                {
                    if (restoreError == null)
                        restoreError = readFailure ?? (truncated ? "Rollback re-read was truncated." : "Rollback re-read was incomplete.");
                }
            }
            catch (Exception ex)
            {
                if (restoreError == null) restoreError = "Rollback re-read failed: " + ex.Message;
            }

            bool restored = restoreError == null && ContentEqualsForRollback(after, priorSource, partName);
            bool atomicRestore = restoreError == null;
            response["rollback"] = new JObject
            {
                ["requested"] = true,
                ["attempted"] = true,
                ["rolledBack"] = restored,
                ["verified"] = restored,
                ["atomic"] = atomicRestore,
                ["baseVersion"] = rollbackBaseVersion,
                ["saveRequired"] = true,
                ["reReadConfirmed"] = after != null,
                ["error"] = restoreError
            };
            response["rolledBack"] = restored;
            response["stateRestored"] = restored;
            response["postSaveVerification"] = new JObject
            {
                ["reReadConfirmed"] = after != null,
                ["matches"] = restored,
                ["versionToken"] = afterVersion,
                ["reason"] = restored ? "rollbackVerified" : "rollbackUnverified"
            };
            response["source"] = after;
            response["persisted"] = false;
            if (visualPart)
            {
                response["physicalCommit"] = !restored;
                response["commitState"] = restored ? "RolledBack" : "Committed";
                response["persistenceState"] = restored ? "Restored" : "CommittedUnverified";
                response["saved"] = true;
                response["sdkSaveCompleted"] = true;
                response["persisted"] = !restored;
                response["persistedVerified"] = false;
                response["verified"] = false;
            }
            if (!restored)
                response["rollbackFailed"] = true;
            return response.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool ContentEqualsForRollback(string actual, string expected, string partName)
        {
            if (actual == null || expected == null) return false;
            if (WebFormXmlHelper.IsVisualPart(partName))
            {
                try { return XmlEquivalence.AreEquivalent(actual, expected, out _); }
                catch { return false; }
            }
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }

        internal static void MarkVisualRollbackUnavailable(JObject response, string reason)
        {
            if (response == null) return;
            response["rollback"] = new JObject
            {
                ["requested"] = true,
                ["attempted"] = false,
                ["rolledBack"] = false,
                ["verified"] = false,
                ["atomic"] = false,
                ["reason"] = "AtomicRollbackUnavailable",
                ["message"] = reason ?? "The visual restore was not attempted because an atomic version-fenced restore could not be established."
            };
            response["rolledBack"] = false;
            response["stateRestored"] = false;
            // Preserve the distinction between a known committed write and an
            // indeterminate post-save read. Never manufacture a physical commit
            // claim merely because a rollback could not be attempted.
            bool committed = string.Equals(response["commitState"]?.ToString(), "Committed", StringComparison.OrdinalIgnoreCase)
                || response["sdkSaveCompleted"]?.Value<bool?>() == true;
            response["physicalCommit"] = committed ? (JToken)true : JValue.CreateNull();
            response["commitState"] = committed ? "Committed" : "Indeterminate";
            response["persistenceState"] = committed ? "CommittedUnverified" : "Indeterminate";
            response["persisted"] = committed ? (JToken)true : JValue.CreateNull();
        }

        internal static void MarkSourceRollbackUnavailable(JObject response)
        {
            response["rollback"] = new JObject
            {
                ["requested"] = true, ["rolledBack"] = false, ["attempted"] = false,
                ["reason"] = "AtomicRollbackUnavailable",
                ["message"] = "Source restore was not attempted: the SDK path does not establish an atomic version-conditional restore. Re-read the current content and version before an explicit recovery edit."
            };
        }

        internal static bool IsPostSaveVerificationIndeterminate(string responseJson)
        {
            try
            {
                var response = JObject.Parse(responseJson);
                string code = response["code"]?.ToString()
                    ?? response["error"]?["code"]?.ToString();
                if (string.Equals(code, "WriteVerificationUnavailable", StringComparison.OrdinalIgnoreCase))
                    return true;

                var postSave = response["postSaveVerification"] as JObject
                    ?? response["error"]?["postSaveVerification"] as JObject;
                return postSave != null && postSave["reReadConfirmed"]?.Value<bool?>() != true;
            }
            catch
            {
                return true;
            }
        }

        internal sealed class PersistedVerificationResult
        {
            public string State { get; set; }
            public string Reason { get; set; }
            public bool Matches { get; set; }
            public bool IsIndeterminate => string.Equals(State, "indeterminate", StringComparison.Ordinal);
        }

        internal static PersistedVerificationResult EvaluatePersistedVerification(
            string requested,
            string persisted,
            bool readTruncated,
            string readFailure, string verifyMode = null, string partName = "Source")
        {
            if (readTruncated)
            {
                return new PersistedVerificationResult
                {
                    State = "indeterminate",
                    Reason = "truncation",
                    Matches = false
                };
            }
            if (!string.IsNullOrWhiteSpace(readFailure))
            {
                return new PersistedVerificationResult
                {
                    State = "indeterminate",
                    Reason = "readFailure",
                    Matches = false
                };
            }
            if (string.Equals(requested, persisted, StringComparison.Ordinal))
            {
                return new PersistedVerificationResult { State = "verified", Reason = "none", Matches = true };
            }
            // An omitted verifyMode keeps the legacy, SDK-tolerant behavior. Only an
            // explicit exact mode opts into strict byte-level verification; otherwise
            // Variables (and other rendered parts) must tolerate harmless SDK casing,
            // whitespace, and line-ending normalization.
            bool explicitMode = verifyMode != null;
            string mode = explicitMode
                ? TextPersistenceVerifier.ResolveMode(verifyMode, partName)
                : "normalized";
            bool exact = explicitMode && string.Equals(mode, "exact", StringComparison.OrdinalIgnoreCase);

            bool eolOnly = string.Equals(
                (requested ?? "").Replace("\r\n", "\n").Replace('\r', '\n'),
                (persisted ?? "").Replace("\r\n", "\n").Replace('\r', '\n'),
                StringComparison.Ordinal);

            if (eolOnly)
            {
                if (!exact)
                {
                    return new PersistedVerificationResult
                    {
                        State = "verified",
                        Reason = "normalization",
                        Matches = true
                    };
                }
                return new PersistedVerificationResult
                {
                    State = "mismatch",
                    Reason = "lineEndings",
                    Matches = false
                };
            }

            bool matches = !exact && (TextPersistenceVerifier.Evaluate(requested, persisted, mode, partName).Matches
                || WhitespaceInsensitiveEquals(persisted, requested)
                || XmlEquivalentWhenApplicable(persisted, requested));
            string reason = ModuleQualificationEquals(persisted, requested) ? "moduleQualification"
                : matches ? "normalization" : "contentMismatch";
            return new PersistedVerificationResult { State = matches ? "verified" : "mismatch", Reason = reason, Matches = matches };
        }

        private static bool XmlEquivalentWhenApplicable(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (!a.TrimStart().StartsWith("<", StringComparison.Ordinal)
                || !b.TrimStart().StartsWith("<", StringComparison.Ordinal)) return false;
            return XmlEquivalence.AreEquivalent(a, b, out _);
        }

        // issue #36.6 — compare two content blobs ignoring all whitespace differences, so a
        // pure re-formatting by the serializer isn't mistaken for a content divergence when we
        // decide whether the requested content is already present.
        // issue #36.6 / issues #70 & #71 — compare two content blobs ignoring whitespace & casing differences
        // outside string literals, so pure re-formatting or SDK casing/XML normalization by the serializer
        // isn't mistaken for a content divergence.
        // issue #78 — the SDK also module-qualifies object/table references on save ("For Each Foo"
        // persists as "For Each MyModule.Foo"), inserting a token that whitespace/case comparison
        // can't classify as equivalent. ModuleQualificationEquals covers that as a final fallback.
        private static bool WhitespaceInsensitiveEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;

            if (IsXmlString(a) && IsXmlString(b))
            {
                return IsXmlEquivalent(a, b);
            }

            return NormalizedCodeEquals(a, b) || ModuleQualificationEquals(a, b);
        }

        private static bool NormalizedCodeEquals(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            string normA = TextPersistenceVerifier.Normalize(a);
            string normB = TextPersistenceVerifier.Normalize(b);
            if (string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase)) return true;

            var linesA = normA.Split('\n');
            var linesB = normB.Split('\n');

            if (linesA.Length != linesB.Length) return false;

            for (int i = 0; i < linesA.Length; i++)
            {
                string trimA = linesA[i].Trim();
                string trimB = linesB[i].Trim();

                if (!string.Equals(trimA, trimB, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        // ----------------------------------------------------------------------
        // issue #78 — SDK module-qualification normalization.
        // ----------------------------------------------------------------------
        // The SDK rewrites object/table references on save by prefixing the module
        // that owns them ("For Each Foo" persists as "For Each MyModule.Foo"). That
        // inserts a NEW token, so the whitespace/case-insensitive comparison above
        // correctly classifies it as a mismatch — and the write verifier then failed
        // a valid save (WriteNotPersisted / AtomicCreateStepFailed rollback).
        //
        // Safety contract (deliberately conservative — a false "verified" would mask
        // a real content divergence, the exact bug #70/#71 fought):
        //   1. Only whole tokens may differ, and each differing token must be a
        //      qualified variant of the other side's token: "X" ↔ "<Module>.<X>".
        //      A qualifier that changes the tail ("Foo" → "MyModule.Baz") is a
        //      genuine mismatch.
        //   2. The qualifier prefix must be a dotted identifier (module names are
        //      identifiers; numbers/punctuation are rejected).
        //   3. The whitespace-run signature must be identical: qualification inserts
        //      no whitespace, so any whitespace drift — including spacing INSIDE
        //      string literals ("a  b" vs "a b") — means the lines genuinely differ.
        //   4. At least one qualified token must actually be present; a pure
        //      whitespace/case difference is reported as plain normalization.
        internal static bool ModuleQualificationEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (string.Equals(a, b, StringComparison.Ordinal)) return false; // no qualification difference to explain

            var linesA = a.Replace("\r\n", "\n").Split('\n');
            var linesB = b.Replace("\r\n", "\n").Split('\n');
            if (linesA.Length != linesB.Length) return false;

            bool anyQualified = false;
            for (int i = 0; i < linesA.Length; i++)
            {
                if (!ModuleQualificationLineEquals(linesA[i].Trim(), linesB[i].Trim(), ref anyQualified))
                    return false;
            }
            return anyQualified;
        }

        private static bool ModuleQualificationLineEquals(string lineA, string lineB, ref bool anyQualified)
        {
            if (string.Equals(lineA, lineB, StringComparison.OrdinalIgnoreCase)) return true;

            // Hard precondition: identical whitespace-run structure. Qualification adds
            // no whitespace, so any whitespace drift means the lines genuinely differ
            // (this is what keeps spacing inside string literals a mismatch).
            if (WhitespaceRunSignature(lineA) != WhitespaceRunSignature(lineB)) return false;

            var tokensA = lineA.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var tokensB = lineB.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokensA.Length != tokensB.Length) return false;

            for (int i = 0; i < tokensA.Length; i++)
            {
                if (string.Equals(tokensA[i], tokensB[i], StringComparison.OrdinalIgnoreCase)) continue;
                if (IsQualifiedVariant(tokensA[i], tokensB[i])) { anyQualified = true; continue; }
                return false;
            }
            return true;
        }

        // true when one token is the other prefixed with a dotted module qualifier:
        // "Foo" ↔ "MyModule.Foo", "Foo.Bar" ↔ "MyModule.Foo.Bar".
        private static bool IsQualifiedVariant(string tokenA, string tokenB)
        {
            return IsDottedQualificationOf(tokenB, tokenA) || IsDottedQualificationOf(tokenA, tokenB);
        }

        private static bool IsDottedQualificationOf(string qualified, string bare)
        {
            if (bare.Length == 0 || qualified.Length <= bare.Length) return false;
            if (!qualified.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase)) return false;
            string prefix = qualified.Substring(0, qualified.Length - bare.Length - 1);
            return IsDottedIdentifier(prefix);
        }

        private static bool IsDottedIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var part in s.Split('.'))
            {
                if (part.Length == 0) return false;
                if (!char.IsLetter(part[0]) && part[0] != '_') return false;
                foreach (char c in part)
                {
                    if (!char.IsLetterOrDigit(c) && c != '_') return false;
                }
            }
            return true;
        }

        // Concatenated whitespace-run lengths ("For Each Foo" → "1;1;"; "a  b" → "2;").
        private static string WhitespaceRunSignature(string line)
        {
            var sb = new System.Text.StringBuilder();
            int run = 0;
            foreach (char c in line)
            {
                if (char.IsWhiteSpace(c)) { run++; }
                else if (run > 0) { sb.Append(run).Append(';'); run = 0; }
            }
            if (run > 0) sb.Append(run).Append(';');
            return sb.ToString();
        }

        private static bool IsXmlString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string trimmed = s.TrimStart();
            return trimmed.StartsWith("<");
        }

        private static bool IsXmlEquivalent(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                var docA = System.Xml.Linq.XDocument.Parse(a);
                var docB = System.Xml.Linq.XDocument.Parse(b);
                return AreXmlElementsEqual(docA.Root, docB.Root, depth: 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool AreXmlElementsEqual(System.Xml.Linq.XElement e1, System.Xml.Linq.XElement e2, int depth)
        {
            if (e1 == null && e2 == null) return true;
            if (e1 == null || e2 == null) return false;
            if (depth > 64) return false; // Prevent stack overflow on deeply nested XML

            if (e1.Name != e2.Name)
                return false;

            var attrs1 = GetSignificantAttributes(e1);
            var attrs2 = GetSignificantAttributes(e2);

            foreach (var kvp in attrs1)
            {
                if (attrs2.TryGetValue(kvp.Key, out string val2))
                {
                    if (!string.Equals(kvp.Value, val2, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else
                {
                    if (!IsDefaultOrEmptyAttribute(kvp.Key, kvp.Value))
                        return false;
                }
            }

            foreach (var kvp in attrs2)
            {
                if (!attrs1.ContainsKey(kvp.Key))
                {
                    if (!IsDefaultOrEmptyAttribute(kvp.Key, kvp.Value))
                        return false;
                }
            }

            var children1 = e1.Elements().Where(c => !IsEmptyContainer(c)).ToList();
            var children2 = e2.Elements().Where(c => !IsEmptyContainer(c)).ToList();

            if (children1.Count != children2.Count)
                return false;

            for (int i = 0; i < children1.Count; i++)
            {
                if (!AreXmlElementsEqual(children1[i], children2[i], depth + 1))
                    return false;
            }

            if (children1.Count == 0 && children2.Count == 0)
            {
                string t1 = e1.Value?.Trim() ?? "";
                string t2 = e2.Value?.Trim() ?? "";
                if (!string.Equals(t1, t2, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static System.Collections.Generic.Dictionary<string, string> GetSignificantAttributes(System.Xml.Linq.XElement e)
        {
            var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in e.Attributes())
            {
                dict[attr.Name.LocalName] = attr.Value;
            }
            return dict;
        }

        private static bool IsDefaultOrEmptyAttribute(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            if (name.StartsWith("default", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "100", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool IsEmptyContainer(System.Xml.Linq.XElement e)
        {
            return !e.HasElements && string.IsNullOrWhiteSpace(e.Value) && !e.HasAttributes;
        }


        private static JObject DescribeContent(string content)
        {
            if (content == null) return null;
            const int cap = 1200;
            string snippet = content.Length > cap ? content.Substring(0, cap) + "…[truncated]" : content;
            return new JObject
            {
                ["hash"] = ComputeSha256(content),
                ["length"] = content.Length,
                ["snippet"] = snippet
            };
        }

        internal static bool TryReadCompleteVerificationSource(
            string response,
            string partName,
            out string source,
            out string versionToken,
            out bool truncated,
            out string failure,
            bool allowSerializedPart = false)
        {
            source = null;
            versionToken = null;
            truncated = false;
            failure = null;
            try
            {
                var json = JObject.Parse(response);
                string status = json["status"]?.ToString();
                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
                    || json["error"] != null)
                {
                    failure = json["error"]?["message"]?.ToString()
                        ?? json["error"]?.ToString()
                        ?? json["message"]?.ToString()
                        ?? "The source read returned an error.";
                    return false;
                }

                truncated = json["truncated"]?.Value<bool?>() == true
                    || json["isTruncatedByWorker"]?.Value<bool?>() == true;
                if (truncated)
                {
                    failure = "The source read was truncated.";
                    return false;
                }

                if (json["isBase64"]?.Value<bool?>() == true
                    || (!allowSerializedPart && json["serializedPart"]?.Value<bool?>() == true)
                    || json["projected"]?.Value<bool?>() == true)
                {
                    failure = json["isBase64"]?.Value<bool?>() == true
                        ? "The source read returned base64 content instead of complete text."
                        : "The source read returned a serialized or projected part, not an editable text source.";
                    return false;
                }

                JToken sourceToken = json["source"]
                    ?? json["content"]
                    ?? json["parts"]?[partName ?? "Source"];
                if (sourceToken == null || sourceToken.Type != JTokenType.String)
                {
                    failure = "The source read did not return a complete text source.";
                    return false;
                }

                source = sourceToken.ToString();
                versionToken = json["versionToken"]?.ToString();
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                return false;
            }
        }
    }
}
