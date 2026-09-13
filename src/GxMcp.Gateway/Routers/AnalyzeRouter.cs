using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class AnalyzeRouter : IMcpModuleRouter
    {
        public string ModuleName => "Analyze";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? target = args?["name"]?.ToString() ?? args?["target"]?.ToString()
                ?? args?["path"]?.ToString() ?? args?["entityKey"]?.ToString() ?? args?["guid"]?.ToString();
            string? type = args?["type"]?.ToString();

            switch (toolName)
            {
                case "genexus_inspect":
                    return new { module = "Analyze", action = "GetConversionContext", target = target, include = args?["include"], type = type, projection = args?["projection"]?.ToString(), verbose = args?["verbose"]?.ToObject<bool?>() ?? false, guid = args?["guid"]?.ToString(), entityKey = args?["entityKey"]?.ToString(), path = args?["path"]?.ToString() };

                case "genexus_inject_context":
                    bool recursive = args?["recursive"]?.Value<bool>() ?? false;
                    return new { module = "Analyze", action = "InjectContext", target = target, recursive = recursive, type = type };

                case "genexus_analyze":
                    string? mode = args?["mode"]?.ToString();
                    switch (mode)
                    {
                        case "context":
                        case "deep_context":
                            return new { module = "Analyze", action = "Get360Context", target = target, type = type };
                        case "linter":
                            bool linterFix = args?["fix"]?.ToObject<bool?>() ?? false;
                            bool linterDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                            return new { module = "Linter", action = "linter", target = target, type = type, @params = new JObject { ["fix"] = linterFix, ["dryRun"] = linterDryRun } };
                        case "navigation":
                            return new { module = "Analyze", action = "GetNavigation", target = target, type = type };
                        case "hierarchy":
                            return new { module = "Analyze", action = "GetHierarchy", target = target, type = type };
                        case "impact":
                            // v2.3.8 (Task 1.4 + post-self-review): delegate to ImpactAnalysis
                            // with index-readiness envelope. Flags must be flattened at the top
                            // level — BuildWorkerRpcRequest carries the entire workerCommand in
                            // request.params, so a nested @params would land at
                            // request.params.params.waitForIndex (two levels deep) and the
                            // worker's `args["waitForIndex"]` lookup would miss it. That's
                            // exactly why the pre-fix flag was always seen as `true`, forcing
                            // a 30s wait loop even with waitForIndex=false.
                            bool waitForIndex = args?["waitForIndex"]?.ToObject<bool?>() ?? true;
                            int? waitTimeoutMs = args?["waitTimeoutMs"]?.ToObject<int?>();
                            string? cancelToken = args?["cancelToken"]?.ToString();
                            return new {
                                module = "Analyze",
                                action = "ImpactAnalysis",
                                target = target,
                                type = type,
                                waitForIndex = waitForIndex,
                                waitTimeoutMs = waitTimeoutMs,
                                cancelToken = cancelToken
                            };
                        case "data_context":
                            return new { module = "Analyze", action = "GetDataContext", target = target, type = type };
                        case "ui_context":
                            return new { module = "UI", action = "GetUIContext", target = target, type = type };
                        case "pattern_metadata":
                            return new { module = "Analyze", action = "GetPatternMetadata", target = target, type = type };
                        case "summary":
                            return new { module = "Analyze", action = "Summarize", target = target, type = type };
                        case "code_metrics":
                            // KB-wide source analytics over the index (no target needed).
                            return new { module = "Analyze", action = "GetCodeMetrics", type = type, top = args?["top"]?.ToObject<int?>() ?? 25 };
                        case "kb_stats":
                            // P1 #6: KB activity & freshness over IModelInformationService
                            // (+ optional IStatisticsService). Read-only; no target needed.
                            return new { module = "KbStats", action = "Run", @params = args };
                        case "table_relations":
                            // P2 #7: table↔transaction relations + redundant-attribute
                            // detection over ITablesService. Read-only. Needs a Transaction name.
                            return new { module = "TableRelations", action = "Run", @params = args };
                        case "explain":
                            return new { module = "Analyze", action = "ExplainCode", target = target, payload = args?["code"]?.ToString(), type = type };
                        case "callers":
                            // Item 24: per-call-site detail with line + context.
                            return new { module = "Analyze", action = "FindCallerSites", target = target, type = type };
                        case "event_flow":
                            // Item 23: ASCII event-flow diagram for WebPanel/SDPanel.
                            return new { module = "Analyze", action = "GetEventFlow", target = target, type = type };
                        case "dependency_heatmap":
                            // Item 87: KB-wide heat ranking — top 50 objects by composite
                            // (edit + ref + caller) score; optional ASCII viz.
                            return new {
                                module = "Analyze",
                                action = "DependencyHeatmap",
                                target = target,
                                type = type,
                                format = args?["format"]?.ToString()
                            };
                        case "cross_platform_impact":
                            // Wave-3 SOTA: bucket callers by Web vs SmartDevices and
                            // surface divergence points before a Trn/SDT/Domain change
                            // ships. Read-only; no SDK writes.
                            return new { module = "Analyze", action = "CrossPlatformImpact", target = target, type = type };
                        case "parent_context":
                            // FR#18 (Stream G, v2.6.6): classifies how a WebPanel / SDPanel is
                            // invoked by its callers (popup vs standalone) so the agent picks
                            // the right form-submit/close pattern.
                            return new { module = "Analyze", action = "ParentContext", target = target, type = type };
                        default:
                            return new { module = "Analyze", action = "Analyze", target = target, type = type };
                    }

                // genexus_sql, genexus_generate_sample_data, genexus_translations consolidated
                // into genexus_db (sql_ddl|sql_navigation|sample_data|translations_import).
                // Legacy names dispatch via McpRouter.LegacyToolAliases.

                case "genexus_get_signature":
                    return new { module = "Analyze", action = "GetParameters", target = target, type = type };
                case "genexus_linter":
                    return new { module = "Linter", action = "linter", target = target, type = type };
                case "genexus_get_navigation":
                    return new { module = "Analyze", action = "GetNavigation", target = target, type = type };

                default:
                    return null;
            }
        }
    }
}
