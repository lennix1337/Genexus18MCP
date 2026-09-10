using System;
using System.IO;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Profile-owned pins survive Worker/Gateway restarts. They never activate a version.
    internal static class WriteDestinationGuard
    {
        internal const string PathVariable = "GXMCP_EXPECTED_KB_PATH";
        internal const string VersionVariable = "GXMCP_EXPECTED_KB_VERSION";
        internal static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VersionVariable));

        internal static string CheckOpen(string actualPath) => Check(
            Environment.GetEnvironmentVariable(PathVariable), Environment.GetEnvironmentVariable(VersionVariable),
            actualPath, null, null, true);

        internal static string CheckCommand(KbService service, string method, string action, JObject args)
        {
            string expectedPath = Environment.GetEnvironmentVariable(PathVariable);
            string expectedVersion = Environment.GetEnvironmentVariable(VersionVariable);
            if (string.IsNullOrWhiteSpace(expectedPath) && string.IsNullOrWhiteSpace(expectedVersion)) return null;
            // Only known observations bypass the version fence. Unknown commands are fenced,
            // including dryRun: a preview on another version must not appear valid.
            if (IsObservation(method, action, args)) return null;
            var kb = service.KbObject as KnowledgeBase;
            string path = kb?.Location;
            string pathError = Check(expectedPath, expectedVersion, path, null, null, true);
            if (pathError != null) return pathError;
            // Explicit recovery is permitted only towards the pinned version, with auto-update off.
            if (string.Equals(method, "kbversion", StringComparison.OrdinalIgnoreCase)
                && string.Equals(args?["action"]?.ToString(), "set_active", StringComparison.OrdinalIgnoreCase))
                return CheckActivation(expectedVersion, args);
            try
            {
                var active = KBVersion.GetActive(kb);
                return Check(expectedPath, expectedVersion, path, active?.Name, active == null ? (bool?)null : active.IsFrozen, false);
            }
            catch { return Check(expectedPath, expectedVersion, path, null, null, false); }
        }

        internal static string CheckActivation(string expectedVersion, JObject args)
        {
            if (string.Equals(args?["targetVersion"]?.ToString(), expectedVersion, StringComparison.OrdinalIgnoreCase)
                && args?["autoUpdate"]?.Type == JTokenType.Boolean && !(bool)args["autoUpdate"]) return null;
            return McpResponse.Err(code: "WriteDestinationActivationRejected", message: "Activation must target the pinned version with explicit autoUpdate=false.",
                hint: "List versions and use the configured targetVersion. No activation was performed.");
        }

        internal static bool IsObservation(string method, string action, JObject args)
        {
            switch ((method ?? "").ToLowerInvariant())
            {
                case "ping": return true;
                case "health": return action == "GetReport";
                case "doctor": return true; // Handler has no actions and only inspects files/loaded assemblies.
                case "build": return action == "Status" || action == "Result";
                case "control": return action == "Cancel";
                case "read": return action == "ExtractFullObject" || action == "ExtractSource" || action == "ExtractParts" || action == "GetVariables" || action == "GetAttribute";
                case "search": return action == "Query" || action == "SearchSource";
                case "list": return action == "Objects";
                case "object": return action == "Read" || action == "ReadLogs";
                case "kbversion": return string.Equals(args?["action"]?.ToString() ?? "list", "list", StringComparison.OrdinalIgnoreCase);
                // Open has its own pre-SDK path fence in KbService.OpenKB.
                case "kb": return action == "Open" || action == "GetIndexStatus" || action == "GetIndexState" || action == "GetNameTypeMap" || action == "ListEnvironments" || action == "GetActiveEnvironment";
                default: return false;
            }
        }

        internal static string Check(string expectedPath, string expectedVersion, string actualPath,
            string actualVersion, bool? frozen, bool opening)
        {
            if (string.IsNullOrWhiteSpace(expectedPath) && string.IsNullOrWhiteSpace(expectedVersion)) return null;
            string code = null;
            if (string.IsNullOrWhiteSpace(expectedPath) || string.IsNullOrWhiteSpace(expectedVersion)) code = "WriteDestinationConfigurationIncomplete";
            else if (!SamePath(expectedPath, actualPath)) code = "WriteDestinationKbMismatch";
            else if (!opening && !string.Equals(expectedVersion, actualVersion, StringComparison.OrdinalIgnoreCase)) code = "WriteDestinationVersionMismatch";
            else if (!opening && frozen != false) code = "WriteDestinationVersionNotWritable";
            if (code == null) return null;
            return McpResponse.Err(code: code, message: "The configured write destination could not be confirmed; no operation was dispatched.",
                hint: "Check the profile pins and list KB versions. Explicit activation requires targetVersion matching the pin and autoUpdate=false.",
                extra: new JObject { ["expectedKbPath"] = expectedPath, ["actualKbPath"] = actualPath,
                    ["expectedKbVersion"] = expectedVersion, ["actualKbVersion"] = actualVersion, ["isFrozen"] = frozen,
                    ["persisted"] = false });
        }

        private static bool SamePath(string expected, string actual)
        {
            try { return string.Equals(NormalizePath(expected), NormalizePath(actual), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Missing path");
            var full = Path.GetFullPath(path);
            if (string.Equals(Path.GetExtension(full), ".gxw", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(full), ".gx", StringComparison.OrdinalIgnoreCase)) full = Path.GetDirectoryName(full);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
