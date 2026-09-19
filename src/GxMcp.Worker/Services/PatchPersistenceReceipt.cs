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
            bool replacementPresent = canonicalReplacement.Length == 0 || replacementMatchCount > 0;
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
            AttachOutcome(payload, saved, verified: true);
        }

        internal static void MarkNotPersisted(JObject payload, bool saved, string verifyError, bool commentOnly = false)
        {
            payload["_internalStatus"] = "Error";
            payload["code"] = commentOnly ? "CommentOnlyWriteNotPersisted" : "WriteNotPersisted";
            payload["message"] = commentOnly
                ? "The SDK save completed, but the forced Source re-read did not contain the requested comment-only change."
                : "The post-save re-read does not contain the requested patched content.";
            if (!string.IsNullOrWhiteSpace(verifyError)) payload["persistedVerifyError"] = verifyError;
            payload["saveAttempted"] = saved;
            AttachOutcome(payload, saved, verified: false);
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
