using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static bool HasNoRecoveryTruncation(JObject payload)
        {
            if (payload["truncated"]?.Value<bool>() == true
                || payload["isTruncatedByWorker"]?.Value<bool>() == true
                || payload["truncatedByGateway"]?.Value<bool>() == true
                || payload["_meta"]?["truncated"] != null)
                return false;

            if (payload["offset"]?.Value<int>() is int offset && offset != 0)
                return false;

            return payload["result"] is not JObject nested || HasNoRecoveryTruncation(nested);
        }

        private static string? RecoveryVersionToken(JObject payload)
        {
            string? token = payload["versionToken"]?.ToString();
            if (!string.IsNullOrWhiteSpace(token)) return token;
            return payload["result"] is JObject nested
                ? nested["versionToken"]?.ToString()
                : null;
        }

        internal static bool IsFullObjectMutationRecoveryRead(JToken? result)
        {
            if (result is not JObject payload
                || payload["error"] != null
                || !string.Equals(payload["code"]?.ToString(), "FullObjectRead", StringComparison.OrdinalIgnoreCase))
                return false;

            return !string.IsNullOrWhiteSpace(RecoveryVersionToken(payload))
                && HasNoRecoveryTruncation(payload);
        }

        internal static bool IsCompleteMutationRecoveryRead(JToken? result)
            => result is JObject payload
                && payload["error"] == null
                // A conditional read that omitted the body proves the caller holds the
                // part, not that this read saw it: it can never reconcile a write.
                && payload["notModified"]?.Value<bool?>() != true
                && !string.IsNullOrWhiteSpace(RecoveryVersionToken(payload))
                && HasNoRecoveryTruncation(payload)
                && (payload["offset"]?.Value<int>() ?? 0) == 0
                && (IsFullObjectMutationRecoveryRead(payload)
                    || payload["versionToken"] != null);

        /// <summary>
        /// Selects the pending fences a <c>genexus_read</c> addresses: same KB scope, object
        /// identity as <see cref="MutationRecoveryRegistry.FindForRead"/> decides, and a part the
        /// request actually covers (an absent part selector is a complete-object read).
        /// </summary>
        internal static IReadOnlyList<(string Target, string Part, RecoveryRequirement? Observed)> FindCoveredRecoveryReads(
            MutationRecoveryRegistry registry, string? kbScope, JObject? readArgs)
            => FindCoveredRecoveryReads(registry,
                string.IsNullOrWhiteSpace(kbScope) ? Array.Empty<string>() : new[] { kbScope! }, readArgs);

        /// <summary>As above, over every scope the KB's fences may have been recorded under.</summary>
        internal static IReadOnlyList<(string Target, string Part, RecoveryRequirement? Observed)> FindCoveredRecoveryReads(
            MutationRecoveryRegistry registry, IReadOnlyCollection<string> kbScopes, JObject? readArgs)
        {
            bool ReadCoversPart(string part)
            {
                if (readArgs?["part"] is { } partToken && partToken.Type != JTokenType.Null)
                    return string.Equals(partToken.ToString(), part, StringComparison.OrdinalIgnoreCase);
                if (readArgs?["parts"] is JArray requestedParts)
                    return requestedParts.OfType<JToken>().Any(item =>
                        string.Equals(item.ToString(), part, StringComparison.OrdinalIgnoreCase));
                // No part selector is a complete-object read.
                return true;
            }

            var covered = new List<(string Target, string Part, RecoveryRequirement? Observed)>();
            foreach (var scope in kbScopes.Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var observed in registry.FindForRead(scope, readArgs))
                {
                    if (ReadCoversPart(observed.Part))
                        covered.Add((observed.Target, observed.Part, observed));
                }
            }
            return covered;
        }

        /// <summary>
        /// Clears the fences a read observed, but only from content that survived the wire
        /// guards: a read the Gateway had to cut, that the Worker paginated, or that the size
        /// guard replaced by a sentinel is not evidence. Returns how many fences were cleared.
        /// </summary>
        internal static int ConfirmMutationRecoveryRead(
            MutationRecoveryRegistry registry,
            string? kbScope,
            JToken? finalResult,
            IReadOnlyList<(string Target, string Part, RecoveryRequirement? Observed)> recoveryReads)
        {
            if (!IsCompleteMutationRecoveryRead(finalResult)) return 0;

            int cleared = 0;
            if (IsFullObjectMutationRecoveryRead(finalResult))
            {
                // A versioned FullObjectRead is complete evidence for
                // every part of the object, not just the default Source
                // key used to find the request.
                var fullParts = (finalResult as JObject)?["result"]?["parts"] as JObject;
                var availableParts = (finalResult as JObject)?["result"]?["availableParts"] as JArray;
                foreach (var recoveryTarget in recoveryReads)
                {
                    var pending = recoveryTarget.Observed;
                    if (pending == null) continue;
                    bool partWasRead = fullParts != null
                        && fullParts.Properties().Any(property =>
                            string.Equals(property.Name, pending.Part, StringComparison.OrdinalIgnoreCase));
                    bool emptyPartWasEnumerated = availableParts != null
                        && availableParts.Any(part => string.Equals(part?.ToString(), pending.Part, StringComparison.OrdinalIgnoreCase));
                    if (!partWasRead && !emptyPartWasEnumerated) continue;
                    if (registry.ConfirmRead(kbScope, pending.Target, pending.Part, pending)) cleared++;
                }
            }
            else
            {
                foreach (var recoveryTarget in recoveryReads)
                {
                    if (registry.ConfirmRead(kbScope, recoveryTarget.Target, recoveryTarget.Part, recoveryTarget.Observed))
                        cleared++;
                }
            }
            return cleared;
        }

        internal static bool IsCompletePropertiesRecoveryRead(
            JObject payload, RecoveryRequirement? requirement, JObject? args)
        {
            if (requirement == null || !string.Equals(requirement.Part, "Properties", StringComparison.OrdinalIgnoreCase)
                || args?["control"] != null
                || !string.Equals(payload["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(payload["code"]?.ToString(), "PropertiesRead", StringComparison.OrdinalIgnoreCase)
                || !HasNoRecoveryTruncation(payload)
                || payload["result"] is not JObject result
                || result["authoritativeRead"]?.Value<bool>() != true
                || string.IsNullOrWhiteSpace(result["versionToken"]?.ToString())
                || result["values"] is not JObject values
                || result["missingProperties"] is JArray missing && missing.Count > 0)
                return false;

            string[] required = requirement.PropertyNames ?? Array.Empty<string>();
            if (required.Length == 0)
                return args?["propertyName"] == null
                    && (args?["propertyNames"] == null || args["propertyNames"] is JArray names && names.Count == 0)
                    && args?["query"] == null && args?["projection"] == null;
            return required.All(name => values.Properties().Any(value =>
                string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        internal static bool IsTerminalPersistedReread(JToken? result)
        {
            if (result is not JObject payload || payload["error"] != null)
                return false;

            JObject evidence = payload["workerPayload"] as JObject
                ?? payload["result"] as JObject
                ?? payload;
            string status = (payload["status"]?.ToString() ?? evidence["status"]?.ToString() ?? string.Empty).Trim();
            bool terminal = status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Success", StringComparison.OrdinalIgnoreCase)
                || status.Equals("CompletedWithWarnings", StringComparison.OrdinalIgnoreCase);
            // persisted is a boolean on an error receipt, the per-property map on a success, and "unknown" when the commit is not
            // confirmed. Only a JSON true confirms it; the other shapes read as not persisted instead of throwing a conversion error.
            bool persisted = IsJsonTrue(evidence["persisted"]) || IsJsonTrue(evidence["result"]?["persisted"]);
            bool reread = evidence["rereadConfirmed"]?.Value<bool?>() == true
                || evidence["result"]?["rereadConfirmed"]?.Value<bool?>() == true
                || evidence["postSaveVerification"]?["reReadConfirmed"]?.Value<bool?>() == true;
            return terminal && persisted && reread;
        }

        private static bool IsJsonTrue(JToken? value) => value?.Type == JTokenType.Boolean && value.Value<bool>();

        internal static JObject? HandleMutationJournalAction(MutationRecoveryRegistry registry, JObject? args)
        {
            string action = args?["action"]?.ToString() ?? "recover";
            if (action == "recover") return null;
            // Needs a resolved KB and a Worker read, so it is not a journal-only action.
            if (action == RecoveryReadCompletion.ActionName) return null;
            if ((action == "journal_status" || action == "journal_repair")
                && args?["force"]?.Value<bool>() != true)
            {
                return action == "journal_status"
                    ? registry.GetJournalStatus()
                    : registry.RepairJournal(args?["dryRun"]?.Value<bool>() ?? true);
            }
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "InvalidJournalRecoveryRequest",
                    ["message"] = "Use journal_status or journal_repair without force. Journal repair never restarts Workers or clears pending read requirements."
                }
            };
        }
    }
}
