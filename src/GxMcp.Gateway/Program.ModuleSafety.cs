using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        internal static bool IsModuleInstallation(string? toolName, JObject? args)
        {
            if (!string.Equals(toolName, "genexus_module", StringComparison.OrdinalIgnoreCase)) return false;
            string? action = args?["action"]?.ToString().Trim();
            return string.Equals(action, "install", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "install_builtin", StringComparison.OrdinalIgnoreCase);
        }

        internal static JObject? ValidateModulePreview(string? toolName, JObject? args)
        {
            if (!string.Equals(toolName, "genexus_module", StringComparison.OrdinalIgnoreCase)
                || args?["dryRun"]?.ToObject<bool?>() != true
                || IsModuleInstallation(toolName, args)) return null;
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "ModuleDryRunUnsupported",
                    ["message"] = "dryRun is supported only for install and install_builtin. No Worker command was sent.",
                    ["persisted"] = false,
                    ["retryable"] = false,
                    ["implicitLifecycleOperations"] = new JArray()
                }
            };
        }

        internal static void MarkModuleInstallOutcomeUnknown(JObject payload)
        {
            var error = payload["error"] as JObject ?? new JObject();
            error["code"] = "ModuleInstallOutcomeUnknown";
            error["hint"] = "Do not repeat installation. Wait until the Worker is no longer busy, then independently inspect the complete module/dependency/object inventory. No asynchronous result or automatic inventory reconciliation is available.";
            error["retryable"] = false;
            error["reconciliationRequired"] = true;
            error["persisted"] = JValue.CreateNull();
            error["persistedStateKnown"] = false;
            error["verifiedByReadback"] = false;
            payload["error"] = error;
            payload["persisted"] = JValue.CreateNull();
            payload["persistedStateKnown"] = false;
        }

        internal static void NormalizeInterruptedModuleInstallation(JObject response, string? toolName, JObject? args)
        {
            if (!IsModuleInstallation(toolName, args) || args?["dryRun"]?.ToObject<bool?>() == true) return;
            var payload = response["result"] as JObject ?? response;
            var error = payload["error"] as JObject;
            string? code = (error?["code"] ?? payload["code"])?.ToString();
            string? message = (error?["message"] ?? payload["message"])?.ToString();
            bool interrupted = code == "-32800" || code == "Cancelled" || code == "WorkerNativeCrashRecovered"
                || (message?.IndexOf("crashed/exited", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!interrupted) return;
            // Cancellation acknowledges the client's request, not an SDK rollback.
            // Native crashes likewise cannot prove how much the import committed.
            MarkModuleInstallOutcomeUnknown(payload);
        }
    }
}
