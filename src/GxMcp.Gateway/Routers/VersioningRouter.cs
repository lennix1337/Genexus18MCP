using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class VersioningRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_versioning": return ConvertVersioningUmbrella(args);
                default: return null;
            }
        }

        private object? ConvertVersioningUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? name = args?["name"]?.ToString();

            switch (action)
            {
                case "history_list":
                    return new { module = "History", action = "list", target = name, part = RouterArgs.Part(args) };
                case "history_get":
                    return new { module = "History", action = "get_source", target = name, versionId = RouterArgs.Int(args, "versionId"), part = RouterArgs.Part(args) };
                case "history_save":
                    return new { module = "History", action = "save", target = name, part = RouterArgs.Part(args) };
                case "history_restore":
                    return new
                    {
                        module = "History",
                        action = "restore",
                        target = name,
                        part = RouterArgs.Part(args),
                        versionId = RouterArgs.Int(args, "versionId"),
                        snapshot = RouterArgs.Str(args, "snapshot"),
                        discard = RouterArgs.Bool(args, "discard"),
                        dryRun = RouterArgs.Bool(args, "dryRun"),
                        expectedVersion = RouterArgs.Str(args, "expectedVersion")
                    };

                case "undo":
                    return new
                    {
                        module = "Undo",
                        action = "Undo",
                        last = args?["last"]?.ToObject<int?>() ?? 1,
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                    };

                case "time_travel":
                    return new
                    {
                        module = "TimeTravel",
                        action = "Recover",
                        target = name,
                        at = args?["at"]?.ToString()
                    };

                case "blame":
                    return new
                    {
                        module = "Blame",
                        action = "Get",
                        target = name,
                        name,
                        part = args?["part"]?.ToString(),
                        line = args?["line"]?.ToObject<int?>(),
                        filePath = args?["filePath"]?.ToString(),
                        context = args?["context"]?.ToObject<int?>()
                    };

                case "diff":
                    return new
                    {
                        module = "Diff",
                        action = args?["mode"]?.ToString() ?? "textVsText",
                        target = name,
                        @params = args
                    };

                case "diff_generated":
                    return new
                    {
                        module = "GeneratedDiff",
                        action = "Diff",
                        target = name,
                        against = args?["against"]?.ToString()
                    };

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_versioning: unknown action '{action}'. Valid: history_list|history_get|history_save|history_restore|undo|time_travel|blame|diff|diff_generated."
                    };
            }
        }

        // Database umbrella dispatcher. action drives the worker envelope; legacy tool aliases
        // (genexus_db_drift, _db_optimize, _sql, _generate_sample_data, _types, _translations)
        // are rewritten to genexus_db by McpRouter.LegacyToolAliases before reaching here.
    }
}
