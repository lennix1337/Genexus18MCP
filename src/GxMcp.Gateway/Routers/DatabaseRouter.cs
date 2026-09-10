using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class DatabaseRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_db": return ConvertDbUmbrella(args);
                default: return null;
            }
        }

        private object? ConvertDbUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString()?.ToLowerInvariant();
            string? target = args?["target"]?.ToString() ?? args?["name"]?.ToString() ?? args?["trn"]?.ToString() ?? args?["transaction"]?.ToString();
            string? type = args?["type"]?.ToString();

            switch (action)
            {
                // Bug #2: forward deep so drift_check defaults to the cheap timestamp
                // heuristic; deep=true is the opt-in build-heavy ImpactDatabase path.
                case "drift_check":
                    return new { module = "DbDrift", action = "Check", target, deep = args?["deep"]?.ToObject<bool?>() ?? false };
                case "drift_report":
                    return new { module = "DbDrift", action = "Report", target, deep = args?["deep"]?.ToObject<bool?>() ?? false };

                case "optimize_analyze":
                    return new { module = "DbOptimize", action = "Analyze", target, format = args?["format"]?.ToString() };
                case "optimize_suggest":
                    return new { module = "DbOptimize", action = "SuggestIndexes", target, format = args?["format"]?.ToString() };
                case "optimize_report":
                    return new { module = "DbOptimize", action = "Report", target, format = args?["format"]?.ToString() };

                case "sql_ddl":
                {
                    bool includeSub = args?["includeSubordinated"]?.Value<bool>() ?? false;
                    return new { module = "Analyze", action = "GetSQL", target, includeSubordinated = includeSub, type };
                }
                case "sql_navigation":
                {
                    int? levelNumber = args?["levelNumber"]?.ToObject<int?>();
                    bool includeExecutionPlan = args?["includeExecutionPlan"]?.ToObject<bool?>() ?? false;
                    bool includeIndexAdvisor = args?["includeIndexAdvisor"]?.ToObject<bool?>() ?? false;
                    return new { module = "Analyze", action = "GetSqlForNavigation", target, levelNumber, includeExecutionPlan, includeIndexAdvisor, type };
                }

                case "sample_data":
                {
                    int rows = args?["rows"]?.ToObject<int?>() ?? 5;
                    return new { module = "Analyze", action = "GenerateSampleData", target, rows, type };
                }

                case "records_query":
                    return new
                    {
                        module = "Analyze",
                        action = "QueryRecords",
                        target,
                        type,
                        @params = args
                    };
                case "records_insert":
                    return new
                    {
                        module = "Analyze",
                        action = "InsertRecord",
                        target,
                        type,
                        @params = args
                    };
                case "records_update":
                    return new
                    {
                        module = "Analyze",
                        action = "UpdateRecords",
                        target,
                        type,
                        @params = args
                    };

                case "types_list":
                case "types_describe":
                case "types_validate":
                {
                    var inner = action switch
                    {
                        "types_list" => "list",
                        "types_describe" => "describe",
                        _ => "validate_value"
                    };
                    var workerArgs = args == null ? new JObject() : (JObject)args.DeepClone();
                    workerArgs["action"] = inner;
                    return new
                    {
                        module = "types",
                        action = inner,
                        target = args?["name"]?.ToString() ?? args?["type"]?.ToString() ?? target,
                        @params = workerArgs
                    };
                }

                // P1 #5 / issue #61: reorg / DDL impact preview. Cheap timestamp
                // heuristic by default; deep=true runs ISpecifierService.ImpactDatabase
                // (specification, build-heavy). reorg_preview additionally diffs the
                // model-logical vs physical column structure (nullable per #57), lists
                // indexes, and emits proposed DDL + destructive warnings.
                case "reorg_impact":
                    return new { module = "ReorgImpact", action = "Run", @params = args };
                case "reorg_preview":
                    return new { module = "ReorgImpact", action = "Preview", @params = args };

                // SDK translations import — was genexus_translations action=import.
                case "translations_import":
                    return new
                    {
                        module = "Analyze",
                        action = "TranslationsImport",
                        target,
                        payload = args?["inputPath"]?.ToString(),
                        type
                    };

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_db: unknown action '{action}'. Valid: drift_check|drift_report|optimize_analyze|optimize_suggest|optimize_report|sql_ddl|sql_navigation|sample_data|records_query|records_insert|records_update|types_list|types_describe|types_validate|translations_import|reorg_impact|reorg_preview."
                    };
            }
        }

        // Browser umbrella dispatcher. action drives the worker envelope; legacy tool aliases
        // (genexus_smoke_test, _a11y_audit, _wcag_check, _browser_capture, _cross_browser, _preview)
        // are rewritten to genexus_browser by McpRouter.LegacyToolAliases before reaching here.
    }
}
