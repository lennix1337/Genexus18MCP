using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // The SDK-free half of the write-destination guard: the pin variables and the
    // observation list that CommandLaneClassifier reads to pick a lane. It may use only
    // the BCL and Newtonsoft, because the shared-host TestHost compiles it without the
    // GeneXus SDK (issue #854).
    internal static partial class WriteDestinationGuard
    {
        internal const string PathVariable = "GXMCP_EXPECTED_KB_PATH";
        internal const string VersionVariable = "GXMCP_EXPECTED_KB_VERSION";
        internal static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VersionVariable));

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
    }
}
