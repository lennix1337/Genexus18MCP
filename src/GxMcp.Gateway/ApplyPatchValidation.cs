using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Verdict of <c>genexus_apply_pattern validate=true</c>. The pattern is valid only when its validation build
    /// ends <c>Succeeded</c> with no itemized error. Any other end state (<c>Failed</c>, <c>Cancelled</c>,
    /// <c>Error</c>, <c>ReorgRequired</c>) is a failure, and so is a missing one.
    /// </summary>
    internal static class ApplyPatchValidation
    {
        /// <summary>
        /// True for a build status that ends the wait: the worker's terminal statuses, compared without case.
        /// <c>Queued</c>, <c>Running</c> and a missing status keep the poll going.
        /// </summary>
        internal static bool IsEndState(string? status)
            => string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "ReorgRequired", StringComparison.OrdinalIgnoreCase);

        /// <summary>Writes the verdict into the validation block: its status, the build's end state and a reorganization that stopped it.</summary>
        internal static void WriteVerdict(JObject block, JObject terminal, int errorCount)
        {
            string status = BuildStatus(terminal);
            bool passed = errorCount == 0
                && string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)
                && terminal["error"] == null;
            block["status"] = passed ? "ok" : "failed";
            if (status.Length > 0) block["buildStatus"] = status;
            if (string.Equals(status, "ReorgRequired", StringComparison.OrdinalIgnoreCase)
                || terminal["reorgRequired"]?.ToObject<bool?>() == true)
            {
                block["reorgRequired"] = true;
            }
        }

        /// <summary>
        /// Writes the validation block of a build that reached an end state: the verdict, the counts, the task and the
        /// first itemized errors and warnings, read from the worker's status reply.
        /// </summary>
        internal static void WriteBlock(JObject block, JObject terminal, string? taskId)
        {
            int errorCount = terminal["ErrorCount"]?.ToObject<int?>() ?? terminal["errorCount"]?.ToObject<int?>() ?? (terminal["Errors"] as JArray)?.Count ?? 0;
            int warningCount = terminal["WarningCount"]?.ToObject<int?>() ?? terminal["warningCount"]?.ToObject<int?>() ?? (terminal["Warnings"] as JArray)?.Count ?? 0;
            WriteVerdict(block, terminal, errorCount);
            block["errorCount"] = errorCount;
            block["warningCount"] = warningCount;
            block["taskId"] = taskId;
            var errors = terminal["Errors"] as JArray ?? terminal["errors"] as JArray;
            if (errors != null && errors.Count > 0)
                block["errors"] = new JArray(errors.Take(10));
            var warnings = terminal["Warnings"] as JArray ?? terminal["warnings"] as JArray;
            if (warnings != null && warnings.Count > 0)
                block["warnings"] = new JArray(warnings.Take(5));
        }

        /// <summary>True when a validation block that is neither ok nor skipped must mark the apply response as an error.</summary>
        internal static bool PromotesToError(JObject block)
        {
            string status = block["status"]?.ToString() ?? "";
            return !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase);
        }

        // The worker serializes the build status as Status; the poll path also accepts a lowercase status.
        private static string BuildStatus(JObject terminal)
            => (terminal["Status"] ?? terminal["status"])?.ToString() ?? "";
    }
}
