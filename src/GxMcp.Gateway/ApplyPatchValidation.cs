using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Verdict of <c>genexus_apply_pattern validate=true</c>. The pattern is valid only when its validation build
    /// ends <c>Succeeded</c> with no itemized error. Any other end state (<c>Failed</c>, <c>Cancelled</c>,
    /// <c>Error</c>, <c>ReorgRequired</c>) and a build that reports no end state are failures.
    /// </summary>
    internal static class ApplyPatchValidation
    {
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
