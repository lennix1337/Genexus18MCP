using System;
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
                && !string.IsNullOrWhiteSpace(RecoveryVersionToken(payload))
                && HasNoRecoveryTruncation(payload)
                && (payload["offset"]?.Value<int>() ?? 0) == 0
                && (IsFullObjectMutationRecoveryRead(payload)
                    || payload["versionToken"] != null);

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
            bool persisted = evidence["persisted"]?.Value<bool?>() == true
                || evidence["result"]?["persisted"]?.Value<bool?>() == true;
            bool reread = evidence["rereadConfirmed"]?.Value<bool?>() == true
                || evidence["result"]?["rereadConfirmed"]?.Value<bool?>() == true
                || evidence["postSaveVerification"]?["reReadConfirmed"]?.Value<bool?>() == true;
            return terminal && persisted && reread;
        }

        internal static JObject? HandleMutationJournalAction(MutationRecoveryRegistry registry, JObject? args)
        {
            string action = args?["action"]?.ToString() ?? "recover";
            if (action == "recover") return null;
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
