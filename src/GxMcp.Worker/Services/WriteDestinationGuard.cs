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
        internal const string PinsMissingCode = "WriteDestinationPinsMissing";
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
                case "doctor": return true; // Handler only reads: files, loaded assemblies and WorkWithPlus template objects.
                case "build": return action == "Status" || action == "Result";
                case "control": return action == "Cancel";
                case "read": return action == "ExtractFullObject" || action == "ExtractSource" || action == "ExtractParts" || action == "CertifyRecovery" || action == "GetVariables" || action == "GetAttribute";
                case "search": return action == "Query" || action == "SearchSource";
                case "list": return action == "Objects";
                case "object": return action == "Read" || action == "ReadLogs";
                case "kbversion": return action == "ActiveModel"
                    || string.Equals(args?["action"]?.ToString() ?? "list", "list", StringComparison.OrdinalIgnoreCase);
                // Open has its own pre-SDK path fence in KbService.OpenKB.
                case "kb": return action == "Open" || action == "GetIndexStatus" || action == "GetIndexState" || action == "GetNameTypeMap" || action == "ListEnvironments" || action == "GetActiveEnvironment";
                default: return false;
            }
        }

        // The single decision behind every destination refusal. With neither pin set the ordinary guard is
        // off (documented behavior); a caller whose contract needs a guaranteed destination, such as the
        // isolated Events save, asks for requirePins and gets WriteDestinationPinsMissing instead.
        internal static string Classify(string expectedPath, string expectedVersion, string actualPath,
            string actualVersion, bool? frozen, bool opening, bool requirePins = false)
        {
            bool noPath = string.IsNullOrWhiteSpace(expectedPath), noVersion = string.IsNullOrWhiteSpace(expectedVersion);
            if (noPath && noVersion) return requirePins ? PinsMissingCode : null;
            if (noPath || noVersion) return "WriteDestinationConfigurationIncomplete";
            if (!SamePath(expectedPath, actualPath)) return "WriteDestinationKbMismatch";
            if (!opening && !string.Equals(expectedVersion, actualVersion, StringComparison.OrdinalIgnoreCase)) return "WriteDestinationVersionMismatch";
            if (!opening && frozen != false) return "WriteDestinationVersionNotWritable";
            return null;
        }

        internal static string Check(string expectedPath, string expectedVersion, string actualPath,
            string actualVersion, bool? frozen, bool opening)
        {
            string code = Classify(expectedPath, expectedVersion, actualPath, actualVersion, frozen, opening);
            if (code == null) return null;
            var diagnostic = DescribePins(expectedPath, expectedVersion);
            diagnostic.Merge(new JObject { ["expectedKbPath"] = expectedPath, ["actualKbPath"] = actualPath,
                ["expectedKbVersion"] = expectedVersion, ["actualKbVersion"] = actualVersion, ["isFrozen"] = frozen });
            return McpResponse.Err(code: code, message: "The configured write destination could not be confirmed; no operation was dispatched.",
                hint: "Check the profile pins and list KB versions. Explicit activation requires targetVersion matching the pin and autoUpdate=false.",
                extra: diagnostic);
        }

        internal static JObject DescribePins(string path, string version)
        {
            string identity = KbIdentity(path);
            string profile = Environment.GetEnvironmentVariable("GXMCP_PROFILE_CONFIG_PATH");
            string revision = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(profile))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        revision = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(profile))).Replace("-", "").Substring(0, 12);
            }
            catch { }
            return new JObject
            {
                ["requiredPins"] = new JArray(PathVariable, VersionVariable),
                ["effectivePins"] = new JObject
                {
                    ["kbPathConfigured"] = !string.IsNullOrWhiteSpace(path),
                    ["versionConfigured"] = !string.IsNullOrWhiteSpace(version),
                    ["kbIdentity"] = identity, ["version"] = version,
                    // Where the Worker read them from, and in what state: the only precedence there is.
                    // The Gateway forwards its own GXMCP_* environment to the Worker, so the pins are whatever
                    // the process that launched the Gateway declared (for example the env of the MCP client's
                    // server entry). The KB path is reported only as its identity hash; the version is reported
                    // by name, which is what the caller compares with kb_version action=list.
                    ["source"] = "process-environment",
                    ["variables"] = new JObject { [PathVariable] = PinState(path), [VersionVariable] = PinState(version) }
                },
                // profileRevision is the revision of the Gateway configuration file (native-config), not of the
                // launcher entry that declared the pins; a pin edit there reaches a Worker only after the
                // Gateway is restarted.
                ["profileRevision"] = revision, ["profileRevisionSource"] = "native-config",
                ["phase"] = "write-destination", ["persisted"] = false, ["saved"] = false,
                ["writeAttempted"] = false, ["implicitOperations"] = new JArray()
            };
        }

        private static string PinState(string value)
            => value == null ? "absent" : string.IsNullOrWhiteSpace(value) ? "blank" : "present";

        // Same one-way identity DescribePins reports, so expected and actual destinations can be compared
        // in a refusal without printing either path.
        internal static string KbIdentity(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) ? null : Helpers.SdkReflection.Sha256Hex(NormalizePath(path).ToUpperInvariant()); }
            catch { return null; }
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
