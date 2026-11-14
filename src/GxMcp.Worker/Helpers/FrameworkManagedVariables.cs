using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Helpers
{
    // Known framework-managed variable names. Re-injected by the SDK / pattern engines
    // after deletion — flagging as "unused" creates a delete-readd loop.
    public static class FrameworkManagedVariables
    {
        private static readonly Dictionary<string, string> _managed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "IsAuthorized", "GAM" },
            { "SecurityFunctionalityKeys", "GAM" },
            { "Time", "WWP+" },
            { "DiasSemanaFin", "WWP+" },
        };

        // SDK builtins always present in generated code — neither user nor framework managed.
        private static readonly HashSet<string> _sdkBuiltins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Pgmname", "Pgmdesc", "Mode", "Today", "UserId", "Message", "EventName", "CtlName",
            // Reported True by VariableDefinition.IsStandard (GeneXus 18 SDK, read by reflection 14/11).
            "Line", "Output", "Page", "Err", "Errmsg", "GxRemove", "Mr", "Msg", "Op", "Outdev", "RestMethod", "RestCode"
        };

        public static string GetManagedBy(string variableName)
        {
            if (string.IsNullOrEmpty(variableName)) return null;
            var name = variableName.TrimStart('&');
            return _managed.TryGetValue(name, out var owner) ? owner : null;
        }

        public static bool IsManaged(string variableName) => GetManagedBy(variableName) != null;

        public static bool ShouldSkipUnusedCheck(string variableName)
        {
            if (string.IsNullOrEmpty(variableName)) return false;
            var name = variableName.TrimStart('&');
            return _sdkBuiltins.Contains(name) || _managed.ContainsKey(name);
        }
    }
}
