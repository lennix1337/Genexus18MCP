using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Builds the stable persistence evidence returned by mode=patch. It does
    /// not read, save, cache, or roll back GeneXus objects.
    /// </summary>
    internal static class PatchPersistenceReceipt
    {
        internal static string ObjectSaveIsolationGuard(string target, bool requireObjectSave, bool dryRun)
        {
            if (!requireObjectSave || dryRun) return null;
            return Models.McpResponse.Err(
                code: "ObjectSaveIsolationUnverified",
                message: "No write was attempted: complete object save can run SDK/pattern event handlers whose isolation has not been verified.",
                target: target,
                extra: new JObject
                {
                    ["requireObjectSave"] = true, ["persisted"] = false, ["saved"] = false,
                    ["partPersisted"] = false, ["objectSaved"] = false,
                    ["metadataUpdated"] = false, ["implicitOperations"] = new JArray(),
                    ["writeAttempted"] = false
                });
        }

        internal const string ObjectSavePreviewScope =
            "Preview only: it checked the textual patch and the isolation pins. It performed no SDK validation of the Events source "
            + "and no save, so it does not prove the save will succeed. A source the SDK rejects fails at part-save; the failure "
            + "reports savePhase, exceptionType and diagnostics[] (code, message, object, part, line, column).";

        internal const string SemanticValidationNotPerformedReason =
            "The SDK reports the validation messages of the Events source only from the part save (eventsPart.Save()), "
            + "which a preview must not run.";

        /// <summary>
        /// Says what a <c>requireObjectSave</c> preview established and what it did not.
        /// The isolation audit proves the bundle, pins and callbacks are eligible; it is
        /// not a save, so <c>persistabilityVerified</c> stays false for any preview.
        /// <paramref name="baseVersionProvided"/> defaults to false on purpose: a caller that forgets to say so
        /// gets "cannot apply" rather than a verdict that lets an unversioned save through.
        /// <paramref name="indentationBlocker"/> is the refusal the real write would raise for
        /// <c>patch.indentation</c> (see <see cref="IndentationBlocker"/>), so the preview does not say "can be
        /// applied" for a request the write itself would stop.
        /// </summary>
        internal static void AttachObjectSavePreviewScope(
            JObject dryRunBody, JObject isolation, int matchCount, bool baseVersionProvided = false,
            JObject indentationBlocker = null)
        {
            if (dryRunBody == null) throw new ArgumentNullException(nameof(dryRunBody));
            bool patchMatch = matchCount >= 1;
            bool isolationEligible = isolation?["verified"]?.Value<bool>() == true;
            dryRunBody["objectSaveIsolation"] = isolation;
            dryRunBody["patchMatch"] = patchMatch;
            dryRunBody["isolationEligible"] = isolationEligible;
            dryRunBody["validationExecuted"] = false;
            dryRunBody["semanticValidation"] = new JObject
            {
                ["performed"] = false,
                ["reason"] = SemanticValidationNotPerformedReason
            };
            dryRunBody["saveExecuted"] = false;
            dryRunBody["persistabilityVerified"] = false;
            dryRunBody["previewScope"] = ObjectSavePreviewScope;
            if (isolation?["blocker"] != null) dryRunBody["writeBlocker"] = isolation["blocker"];

            // The verdict first: status/code stay "ok"/"Applied" because the textual simulation did succeed,
            // so these fields say whether the same request could be applied and, if not, every reason.
            bool unchanged = dryRunBody["noContentChange"]?.Value<bool>() == true;
            JArray blockers = PreviewApplyBlockers(isolation, patchMatch, unchanged, baseVersionProvided, indentationBlocker);
            bool canApply = blockers.Count == 0;
            dryRunBody["canApply"] = canApply;
            dryRunBody["writeAttempted"] = false;
            if (!canApply) dryRunBody["applyBlockers"] = blockers;
            dryRunBody["previewSummary"] = BuildPreviewSummary(patchMatch, unchanged, matchCount, blockers);
        }

        private static JArray PreviewApplyBlockers(
            JObject isolation, bool patchMatch, bool unchanged, bool baseVersionProvided, JObject indentationBlocker)
        {
            var blockers = new JArray();
            if (!patchMatch && !unchanged)
                blockers.Add(Blocker("PatchNoMatch", "patch",
                    "The text patch matched nothing in the current Events source.",
                    "Re-read Events and anchor the patch on text present exactly expectedCount times."));
            if (indentationBlocker != null) blockers.Add(indentationBlocker);
            if (!baseVersionProvided)
                blockers.Add(Blocker("BaseVersionRequired", "concurrency",
                    "A complete Events object save requires baseVersion; this preview was sent without one.",
                    "Apply with the versionToken returned by this preview (or a fresh read) as baseVersion; expectedVersion is its alias."));
            if (isolation?["verified"]?.Value<bool>() == true) return blockers;
            if (isolation?["blockers"] is JArray reported && reported.Count > 0)
            {
                foreach (JToken blocker in reported) blockers.Add(blocker.DeepClone());
                return blockers;
            }
            string reason = isolation?["reason"]?.ToString();
            blockers.Add(Blocker(
                isolation?["blocker"]?.ToString() ?? "IsolationReportUnavailable", "preflight",
                string.IsNullOrWhiteSpace(reason) ? "The isolation preflight did not report that the save is eligible." : reason,
                "Fix the reported blocker; nothing was written and the preview cannot establish that the save would succeed."));
            return blockers;
        }

        private static JObject Blocker(string code, string layer, string message, string remediation)
            => new JObject { ["code"] = code, ["layer"] = layer, ["message"] = message, ["remediation"] = remediation };

        /// <summary>
        /// The refusal <c>patch.indentation.mode=validate</c> raises on a real write (IndentationMismatch or
        /// IndentationNotComparable), in the shape of an <c>applyBlockers[]</c> entry. A dry run reports the
        /// evidence without failing; the verdict has to count it, because the write stops at the same check.
        /// </summary>
        internal static JObject IndentationBlocker(string code, string message)
            => Blocker(code, "patch", message,
                "Send `replace` with the same leading tabs/spaces as the matched line, or omit patch.indentation to insert the text literally.");

        internal static string BuildPreviewSummary(bool patchMatch, bool unchanged, int matchCount, JArray blockers)
        {
            string patch = patchMatch ? "The text patch matched (" + matchCount + ")."
                : unchanged ? "The patch leaves the Events source unchanged."
                : "The text patch matched nothing.";
            if (blockers == null || blockers.Count == 0)
                return patch + " The isolation audit accepted the destination pins, SDK bundle and callbacks, so this request can be applied. "
                    + "Nothing was written, and the SDK has not validated the Events source: only the save does.";
            var first = (JObject)blockers[0];
            string more = blockers.Count > 1 ? " (+" + (blockers.Count - 1) + " more in applyBlockers)" : string.Empty;
            return patch + " Applying it is BLOCKED by " + first["code"] + ": " + first["message"] + more
                + " Nothing was written and the SDK validated nothing.";
        }

        internal static bool AttachVerification(
            JObject payload,
            TextPersistenceVerifier.Result verification,
            string requestedReplacement,
            string originalContext,
            string savedSource,
            string persistedSource,
            string verifyMode,
            string partName,
            int matchCount,
            bool commentOnly = false)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (verification == null) throw new ArgumentNullException(nameof(verification));

            payload["requestedHash"] = verification.RequestedHash;
            payload["persistedHash"] = verification.PersistedHash;
            payload["normalizedRequestedHash"] = verification.NormalizedRequestedHash;
            payload["normalizedPersistedHash"] = verification.NormalizedPersistedHash;

            string canonicalReplacement = TextPersistenceVerifier.Canonicalize(requestedReplacement, verifyMode, partName);
            string canonicalPersisted = TextPersistenceVerifier.Canonicalize(persistedSource, verifyMode, partName);
            string canonicalOldContext = TextPersistenceVerifier.Canonicalize(originalContext, verifyMode, partName);
            int replacementMatchCount = canonicalReplacement.Length == 0
                ? 0
                : PatchTextEditor.CountOccurrences(canonicalPersisted, canonicalReplacement);
            int persistedMatchCount = canonicalOldContext.Length == 0
                ? 0
                : commentOnly
                    ? CommentOnlyPatch.CountActiveOccurrences(canonicalPersisted, canonicalOldContext)
                    : PatchTextEditor.CountOccurrences(canonicalPersisted, canonicalOldContext);
            // A visual part matched by XML layout equivalence persisted the whole requested
            // document; the SDK's canonical form of the replacement (a TokenDataList for a
            // ControlWhere expression, issue #711) need not contain it verbatim.
            bool replacementPresent = canonicalReplacement.Length == 0
                || replacementMatchCount > 0
                || (verification.Matches && verification.XmlLayoutEquivalent);
            bool verified = verification.Matches && replacementPresent;

            JObject verificationJson = verification.ToJson(reReadConfirmed: verified);
            verificationJson["readCompleted"] = true;
            verificationJson["matchCount"] = matchCount;
            verificationJson["replacementMatchCount"] = replacementMatchCount;
            verificationJson["replacementPresent"] = replacementPresent;
            verificationJson["persistedMatchCount"] = persistedMatchCount;
            verificationJson["oldContentPresent"] = persistedMatchCount > 0;
            payload["persistedMatchCount"] = persistedMatchCount;
            payload["oldContentPresent"] = persistedMatchCount > 0;
            payload["replacementPresent"] = replacementPresent;
            payload["reReadConfirmed"] = verified;
            verificationJson["source"] = "fresh-sdk-read";
            payload["verification"] = verificationJson;
            AttachContentEvidence(payload, savedSource, savedSource, persistedSource);
            payload["source"] = persistedSource;
            payload.Remove("partialPersistenceDetected");
            payload.Remove("verificationWarning");
            return verified;
        }

        internal static void AttachContentEvidence(
            JObject payload,
            string requestedSource,
            string savedSource,
            string reReadSource)
        {
            payload["content"] = new JObject
            {
                ["requested"] = Describe(requestedSource),
                ["saved"] = Describe(savedSource),
                ["reRead"] = Describe(reReadSource)
            };
        }

        internal static bool ShouldRollback(bool persistedMatches, bool rollbackOnFailure)
            => !persistedMatches && rollbackOnFailure;

        internal static bool CanAttemptRollback(
            bool persistedMatches,
            bool rollbackOnFailure,
            string observedPersistedVersion)
            => ShouldRollback(persistedMatches, rollbackOnFailure)
                && !string.IsNullOrWhiteSpace(observedPersistedVersion);

        internal static void MarkVerified(JObject payload, bool saved)
        {
            payload.Remove("error");
            payload.Remove("mutation");
            payload.Remove("verificationWarning");
            payload["_internalStatus"] = "Success";
            payload["code"] = "Applied";
            payload["message"] = "Patch persisted and was confirmed by post-save re-read.";
            payload["persistedState"] = "confirmed";
            AttachOutcome(payload, saved, verified: true);
        }

        internal const string MismatchKindNormalization = "normalization";
        internal const string MismatchKindContent = "content";

        /// <summary>
        /// Marks a write whose fresh post-save read completed but did not match
        /// the request. <paramref name="normalizedMatches"/> classifies the
        /// divergence: <c>true</c> when the requested and persisted texts are the
        /// same content under the verifier's normalized or semantic
        /// canonicalization (only EOL, BOM, trailing whitespace, blank lines,
        /// keyword casing or spacing differ), <c>false</c> when the content
        /// itself differs, <c>null</c> when no such evidence is available. The
        /// "formatting or normalization" wording is reserved for the first case,
        /// so lost content is never reported as a rendering difference (#547).
        /// </summary>
        internal static void MarkNotPersisted(
            JObject payload,
            bool saved,
            string verifyError,
            bool commentOnly = false,
            bool? normalizedMatches = null)
        {
            string mismatchKind = ClassifyMismatch(normalizedMatches);
            payload["_internalStatus"] = "Error";
            payload["code"] = commentOnly ? "CommentOnlyWriteNotPersisted" : "WriteNotPersisted";
            payload["message"] = commentOnly
                ? "The SDK save completed, but the forced Source re-read did not contain the requested comment-only change."
                : NotPersistedMessage(saved, mismatchKind);
            if (mismatchKind != null) payload["mismatchKind"] = mismatchKind;
            if (!string.IsNullOrWhiteSpace(verifyError)) payload["persistedVerifyError"] = verifyError;
            payload["saveAttempted"] = saved;
            // The physical save result stays at the top of the receipt next to the
            // fail-closed `saved` claim, so a consumer never has to infer it from
            // nested WriteService evidence. WriteService's own value wins when it
            // is present; it may be null for an unknown save completion (#547).
            if (payload["sdkSaveCompleted"] == null || payload["sdkSaveCompleted"].Type == JTokenType.Null)
                payload["sdkSaveCompleted"] = saved;
            payload["persistedState"] = "not_persisted";
            AttachOutcome(payload, saved, verified: false);
        }

        internal static string ClassifyMismatch(bool? normalizedMatches)
            => normalizedMatches == null
                ? null
                : normalizedMatches.Value ? MismatchKindNormalization : MismatchKindContent;

        /// <summary>
        /// True when the requested and persisted texts carry the same content and
        /// differ only by a rendering the SDK is known to apply. The normalized
        /// hashes cover EOL, BOM, trailing whitespace and blank lines; they keep
        /// keyword casing, so `Numeric(4)` persisted as `NUMERIC(4)` hashes
        /// differently although nothing was lost. The semantic canonicalization is
        /// the verifier's own definition of a harmless casing/spacing rendering and
        /// is reused here to classify the divergence without changing the verdict
        /// of the selected verifyMode (#547).
        /// </summary>
        internal static bool NormalizedMatches(
            TextPersistenceVerifier.Result verification,
            string requested,
            string persisted,
            string partName)
        {
            if (verification != null
                && !string.IsNullOrEmpty(verification.NormalizedRequestedHash)
                && string.Equals(verification.NormalizedRequestedHash, verification.NormalizedPersistedHash, StringComparison.Ordinal))
                return true;
            return string.Equals(
                TextPersistenceVerifier.Canonicalize(requested, "semantic", partName),
                TextPersistenceVerifier.Canonicalize(persisted, "semantic", partName),
                StringComparison.Ordinal);
        }

        private static string NotPersistedMessage(bool saved, string mismatchKind)
        {
            if (!saved) return "The post-save re-read does not contain the requested patched content.";
            if (mismatchKind == MismatchKindNormalization)
                return "The SDK save completed and the persisted text differs from the request only by formatting or normalization (casing, spacing, line endings or blank lines) that the selected verifyMode does not tolerate. Re-read the part to confirm it; do not retry the patch.";
            if (mismatchKind == MismatchKindContent)
                return "The SDK save completed, but the persisted text differs from the request in content: the post-save re-read does not contain the requested patched content. Re-read the part and compare it with the request before deciding on another edit; do not retry blindly.";
            return "The SDK save completed, but the post-save re-read does not contain the requested patched content.";
        }

        internal static void MarkVerificationUnavailable(JObject payload, bool saveAttempted, string reason)
        {
            var verification = payload["verification"] as JObject ?? new JObject();
            payload.Remove("error");
            payload.Remove("mutation");
            payload.Remove("source");
            payload.Remove("persistedHash");
            payload.Remove("persistedSnippet");
            payload.Remove("changed");
            payload.Remove("partialPersistenceDetected");
            payload.Remove("verificationWarning");
            payload["_internalStatus"] = "Error";
            payload["code"] = "WriteVerificationUnavailable";
            payload["message"] = saveAttempted
                ? "The SDK save completed, but the complete post-save read could not confirm persistence."
                : "The operation did not report a new save, and the complete post-save read could not confirm persistence.";
            payload["hint"] = "Do not retry blindly. Re-read the complete part or recover from the pre-write snapshot before attempting another edit.";
            payload["verificationUnavailable"] = true;
            verification["readCompleted"] = false;
            verification["reReadConfirmed"] = false;
            verification["reason"] = reason ?? "unknown";
            payload["verification"] = verification;
            payload["postSaveVerification"] = new JObject
            {
                ["reReadConfirmed"] = false,
                ["reason"] = reason ?? "unknown"
            };
            if (!string.IsNullOrWhiteSpace(reason)) payload["persistedVerifyError"] = reason;
            var content = payload["content"] as JObject;
            if (content != null)
            {
                content["saved"] = JValue.CreateNull();
                content["reRead"] = JValue.CreateNull();
            }
            AttachOutcome(payload, saved: false, verified: false);
            payload["persistedState"] = "unknown";
            payload["saveAttempted"] = saveAttempted;
        }

        internal static void MarkRollbackNotAttempted(
            JObject payload,
            string reason,
            bool verificationUnavailable = true)
        {
            if (payload == null) return;
            payload["rollback"] = new JObject
            {
                ["requested"] = true,
                ["snapshotValid"] = true,
                ["attempted"] = false,
                ["saveAttempted"] = false,
                ["verified"] = false,
                ["rolledBack"] = false,
                ["verificationUnavailable"] = verificationUnavailable,
                ["error"] = reason ?? "Rollback was not attempted."
            };
            payload["rolledBack"] = false;
        }

        /// <summary>
        /// Issue #542: WriteService already attempted a structural rollback before it
        /// returned WriteNotPersisted. The receipt says what was reverted (or why the
        /// revert failed) instead of labelling the mismatch a normalization difference,
        /// and carries the rollback as evidence so no textual rollback is stacked on it.
        /// </summary>
        internal static void MarkWriterRollback(JObject payload, bool rolledBack, string rollbackError, JArray unappliedMembers)
        {
            if (payload == null) return;
            string unapplied = string.Empty;
            if (unappliedMembers != null && unappliedMembers.Count > 0)
            {
                var lines = new string[unappliedMembers.Count];
                for (int i = 0; i < lines.Length; i++) lines[i] = unappliedMembers[i]?.ToString();
                unapplied = " Not applied: " + string.Join("; ", lines) + ".";
            }
            const string head = "The SDK save completed, but the post-save re-read did not contain the requested content.";
            string cause = (string.IsNullOrWhiteSpace(rollbackError) ? "unknown cause" : rollbackError.Trim().TrimEnd('.')) + ".";
            payload["message"] = rolledBack
                ? head + unapplied + " The write was reverted to the pre-write structure and the revert was verified by re-read."
                : head + unapplied + " The revert to the pre-write structure could not be verified: " + cause
                    + " Stop writing this object until its persisted state is confirmed.";
            // The writer's RollbackFullWriteFailure may already have attached its own
            // `rollback` object (re-read matched the pre-write snapshot: atomic, no further
            // save). Merge into it so that evidence (atomic, saveRequired, reReadConfirmed,
            // baseVersion) survives next to the attempt this receipt is about.
            var rollback = payload["rollback"] as JObject ?? new JObject();
            rollback["attempted"] = true;
            rollback["saveAttempted"] = true;
            rollback["verified"] = rolledBack;
            rollback["rolledBack"] = rolledBack;
            rollback["source"] = "write-service";
            rollback["error"] = rolledBack ? JValue.CreateNull() : (JToken)(rollbackError ?? "Rollback could not be verified.");
            payload["rollback"] = rollback;
            payload["rolledBack"] = rolledBack;
            MarkRollbackTextOutcomeUnknown(payload);
            if (unappliedMembers != null) payload["unappliedMembers"] = unappliedMembers;
            if (!rolledBack) return;
            payload["stateRestored"] = true;
            payload["commitState"] = "RolledBack";
            payload["persistenceState"] = "Restored";
        }

        internal static void MarkRollbackTextOutcomeUnknown(JObject payload)
        {
            payload["persistedState"] = "unknown";
        }

        internal static void AttachOutcome(JObject payload, bool saved, bool verified)
        {
            payload["persistedVerified"] = verified;
            payload["persisted"] = verified;
            // `saved` is a persistence claim, not an SDK call-return signal. A
            // write whose post-save read did not confirm the requested content
            // must never be exposed as saved=true.
            payload["saved"] = saved && verified;
            payload["verified"] = verified;
        }

        internal static bool AttachObjectSaveEvidence(
            JObject payload,
            bool partPersisted,
            bool objectSaved,
            string revisionBefore,
            string revisionAfter,
            string lastUpdateBefore,
            string lastUpdateAfter,
            bool? otherPartsIntact,
            bool metadataStampPersisted = false,
            JArray unexpectedChangedParts = null)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            bool metadataUpdated = MetadataChanged(
                revisionBefore, revisionAfter, lastUpdateBefore, lastUpdateAfter, metadataStampPersisted);
            payload["partPersisted"] = partPersisted;
            payload["objectSaved"] = objectSaved;
            payload["revisionBefore"] = revisionBefore == null ? JValue.CreateNull() : (JToken)revisionBefore;
            payload["revisionAfter"] = revisionAfter == null ? JValue.CreateNull() : (JToken)revisionAfter;
            payload["lastUpdateBefore"] = lastUpdateBefore == null ? JValue.CreateNull() : (JToken)lastUpdateBefore;
            payload["lastUpdateAfter"] = lastUpdateAfter == null ? JValue.CreateNull() : (JToken)lastUpdateAfter;
            payload["metadataStampPersisted"] = metadataStampPersisted;
            payload["metadataUpdated"] = metadataUpdated;
            if (otherPartsIntact.HasValue) payload["otherPartsIntact"] = otherPartsIntact.Value;
            if (unexpectedChangedParts != null && unexpectedChangedParts.Count > 0)
                payload["unexpectedChangedParts"] = unexpectedChangedParts;
            return metadataUpdated;
        }

        internal static bool MetadataChanged(
            string revisionBefore,
            string revisionAfter,
            string lastUpdateBefore,
            string lastUpdateAfter,
            bool metadataStampPersisted = false)
        {
            if (!metadataStampPersisted)
                return false;

            long beforeRevision;
            long afterRevision;
            if (long.TryParse(revisionBefore, out beforeRevision)
                && long.TryParse(revisionAfter, out afterRevision)
                && afterRevision > beforeRevision)
                return true;

            DateTime before;
            DateTime after;
            return DateTime.TryParse(lastUpdateBefore, null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out before)
                   && DateTime.TryParse(lastUpdateAfter, null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out after)
                   && after.ToUniversalTime() > before.ToUniversalTime();
        }

        internal static void RequireCompleteObjectSave(JObject payload)
        {
            if (payload["requireObjectSave"]?.Value<bool>() != true) return;
            if (payload["objectSaved"]?.Value<bool>() == true
                && payload["partPersisted"]?.Value<bool>() == true
                && payload["metadataUpdated"]?.Value<bool>() == true
                && payload["otherPartsIntact"]?.Value<bool?>() == true) return;

            payload["_internalStatus"] = "Error";
            payload["code"] = "ObjectSaveIncomplete";
            payload["message"] = payload["partPersisted"]?.Value<bool>() == true
                ? "The Events content is persisted, but the complete object-save contract was not confirmed. Do not repeat the edit blindly."
                : "The complete object-save contract was not confirmed and the requested Events content was not found by the fresh re-read.";
            payload["manualRecovery"] = "Compare the fresh Events content with the object open in the GeneXus IDE. If the IDE tab is older, reopen it before saving the object manually so the persisted content is not overwritten.";
            payload["retrySafe"] = false;
        }

        internal static JObject BuildRollback(
            bool saved,
            TextPersistenceVerifier.Result verification,
            string error)
        {
            bool verified = verification != null && verification.Matches;
            return new JObject
            {
                ["requested"] = true,
                ["snapshotValid"] = true,
                ["attempted"] = true,
                ["saved"] = saved,
                ["verified"] = verified,
                ["requestedHash"] = verification?.RequestedHash,
                ["persistedHash"] = verification?.PersistedHash,
                ["error"] = verified ? JValue.CreateNull() : (JToken)(error ?? "Rollback could not be verified.")
            };
        }

        private static JToken Describe(string value)
        {
            if (value == null) return JValue.CreateNull();
            const int cap = 240;
            return new JObject
            {
                ["hash"] = TextPersistenceVerifier.Sha256(value),
                ["length"] = value.Length,
                ["snippet"] = value.Length <= cap ? value : value.Substring(0, cap) + "…[truncated]"
            };
        }
    }
}
