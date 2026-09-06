using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class SystemRouter : IMcpModuleRouter
    {
        public string ModuleName => "System";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? target = args?["target"]?.ToString();

            switch (toolName)
            {
                case "genexus_lifecycle":
                    switch (action)
                    {
                        case "build":
                            // mode=compile_check: spec+gen+compile the target(s) plus their
                            // transitive callers, skipping the ~200s DeveloperMenu regen a
                            // full build-all pays. Routed to the CompileCheck worker action.
                            if (string.Equals(args?["mode"]?.ToString(), "compile_check", StringComparison.OrdinalIgnoreCase))
                            {
                                return new {
                                    module = "Build",
                                    action = "CompileCheck",
                                    target = target,
                                    environment = args?["environment"]?.ToString(),
                                    buildPlanCap = args?["buildPlanCap"]?.ToObject<int?>(),
                                    dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                                    deploy = args?["deploy"]?.ToObject<bool?>() ?? false
                                };
                            }
                            return new {
                                module = "Build",
                                action = "Build",
                                target = target,
                                environment = args?["environment"]?.ToString(),
                                includeCallees = args?["includeCallees"]?.ToString(),
                                buildPlanCap = args?["buildPlanCap"]?.ToObject<int?>(),
                                // Item 72 (friction 2026-05-22) — Slack/Discord webhook on terminal Failed state.
                                notifyOnFailure = args?["notifyOnFailure"]?.ToString(),
                                skipFullDeploy = args?["skipFullDeploy"]?.ToObject<bool?>() ?? false,
                                // Item 28 (Tier-S, EXPERIMENTAL) — fastIncremental opt-in.
                                fastIncremental = args?["fastIncremental"]?.ToObject<bool?>() ?? false,
                                dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                                deploy = args?["deploy"]?.ToObject<bool?>() ?? false
                            };
                        case "cancel": return new { module = "Build", action = "Cancel", target = target };
                        // issue #28 item 12: spec-check only — Spec+Gen, no Compile/deploy.
                        case "specify": return new {
                            module = "Build",
                            action = "Specify",
                            target = target,
                            environment = args?["environment"]?.ToString(),
                            buildPlanCap = args?["buildPlanCap"]?.ToObject<int?>(),
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                        };
                        case "rebuild": return new {
                            module = "Build",
                            action = "RebuildAll",
                            target = target,
                            environment = args?["environment"]?.ToString(),
                            includeCallees = args?["includeCallees"]?.ToString(),
                            buildPlanCap = args?["buildPlanCap"]?.ToObject<int?>(),
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                            deploy = args?["deploy"]?.ToObject<bool?>() ?? false
                        };
                        case "build_all": return new {
                            module = "Build",
                            action = "BuildAll",
                            // Build All is deliberately KB-global. Preserve a supplied
                            // target so the worker can reject it with a structured,
                            // actionable validation error instead of silently ignoring it.
                            target = target,
                            environment = args?["environment"]?.ToString(),
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                            deploy = args?["deploy"]?.ToObject<bool?>() ?? false
                        };
                        case "reorg": return new { module = "Build", action = "Reorg", target = target };
                        // Item 43 (friction 2026-05-22) — DDL diff/preview pre-reorg.
                        case "reorg_preview": return new { module = "Build", action = "ReorgPreview", target = target };
                        case "validate": return new { module = "Validation", action = "Check", target = target, payload = args?["code"]?.ToString(), part = args?["part"]?.ToString() };
                        case "validate-kb": return new { module = "KB", action = "ValidateConditions", limit = args?["limit"]?.ToObject<int?>() };
                        case "snapshots-list": return new { module = "KB", action = "ListPatternSnapshots", target = target };
                        case "snapshots-restore": return new { module = "KB", action = "RestorePatternSnapshot", target = target, snapshotPath = args?["snapshotPath"]?.ToString() };
                        case "sync": return new { module = "Build", action = "Sync", target = target };
                        case "index": return new {
                            module = "KB",
                            action = "BulkIndex",
                            force = args?["force"]?.ToObject<bool?>() ?? false,
                            dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                        };
                        case "status":
                            if (!string.IsNullOrEmpty(target))
                            {
                                int? page = args?["page"]?.ToObject<int?>();
                                int? pageSize = args?["pageSize"]?.ToObject<int?>() ?? args?["page_size"]?.ToObject<int?>();
                                // v2.6.6 Stream F: event-driven long-poll on worker taskId.
                                // `wait` blocks the worker until the baseline changes (Phase /
                                // counts / TargetsDone / terminal Status) or the timeout fires.
                                // `since` is the snapshot string returned under _meta.snapshot
                                // by the previous status response — pass it back for chaining.
                                int wait = args?["wait"]?.ToObject<int?>() ?? 0;
                                if (wait < 0) wait = 0;
                                if (wait > 300) wait = 300;
                                return new {
                                    module = "Build",
                                    action = "Status",
                                    target = target,
                                    page = page ?? 1,
                                    pageSize = pageSize ?? 50,
                                    wait = wait,
                                    since = args?["since"]?.ToString()
                                };
                            }
                            // issue #25 #1: no-target status polls the INDEX build. Forward
                            // `wait`/`since` so the worker can block and return the moment the
                            // index state transitions (e.g. UltraLiteReady→LiteReady→Ready) or
                            // a walk progress tick lands, instead of the agent polling in a loop.
                            {
                                int idxWait = args?["wait"]?.ToObject<int?>() ?? 0;
                                if (idxWait < 0) idxWait = 0;
                                if (idxWait > 300) idxWait = 300;
                                return new {
                                    module = "KB",
                                    action = "GetIndexStatus",
                                    wait = idxWait,
                                    since = args?["since"]?.ToString()
                                };
                            }
                        case "result":
                            if (!string.IsNullOrEmpty(target))
                            {
                                int? page = args?["page"]?.ToObject<int?>();
                                int? pageSize = args?["pageSize"]?.ToObject<int?>() ?? args?["page_size"]?.ToObject<int?>();
                                return new { module = "Build", action = "Result", target = target, page = page ?? 1, pageSize = pageSize ?? 50 };
                            }
                            return null;
                        default: return null;
                    }

                // forge (scaffold|translate|sample) merged into genexus_create umbrella (OperationsRouter).


                case "genexus_doc":
                    switch (action)
                    {
                        case "wiki": return new { module = "Wiki", action = "Generate", target = target };
                        case "visualize": return new { module = "Visualizer", action = "Generate", target = target };
                        case "health": return new { module = "Health", action = "GetReport" };
                        default: return null;
                    }

                case "genexus_test":
                    return new { module = "Test", action = "Run", target = args?["name"]?.ToString() };

                // genexus_kb set_startup / get_startup and environment selection —
                // SDK-bound operations. The other actions (list/open/close/
                // set_default) are handled directly in Program.cs.
                case "genexus_kb":
                {
                    string kbAction = args?["action"]?.ToString();
                    if (string.Equals(kbAction, "set_startup", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new
                        {
                            module = "KB",
                            action = "SetStartupObject",
                            target = args?["name"]?.ToString(),
                            name = args?["name"]?.ToString()
                        };
                    }
                    if (string.Equals(kbAction, "get_startup", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new
                        {
                            module = "KB",
                            action = "GetStartupObject"
                        };
                    }
                    if (string.Equals(kbAction, "list_environments", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new
                        {
                            module = "KB",
                            action = "ListEnvironments"
                        };
                    }
                    if (string.Equals(kbAction, "get_environment", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new
                        {
                            module = "KB",
                            action = "GetActiveEnvironment"
                        };
                    }
                    if (string.Equals(kbAction, "set_environment", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new
                        {
                            module = "KB",
                            action = "SetActiveEnvironment",
                            target = args?["environment"]?.ToString()
                        };
                    }
                    return null;
                }

                // genexus_kb_explorer action=locate — "Locate in KB Explorer" parity.
                case "genexus_kb_explorer":
                {
                    string kxAction = args?["action"]?.ToString() ?? "locate";
                    return new
                    {
                        module = "KbExplorer",
                        action = string.Equals(kxAction, "locate", System.StringComparison.OrdinalIgnoreCase)
                            ? "Locate"
                            : kxAction,
                        target = args?["name"]?.ToString(),
                        name = args?["name"]?.ToString()
                    };
                }

                // genexus_navigation action=view — "View Navigation / View Last Navigation" parity.
                case "genexus_navigation":
                {
                    bool latest = args?["latest"]?.ToObject<bool?>() ?? false;
                    return new
                    {
                        module = "Navigation",
                        action = "View",
                        target = args?["name"]?.ToString(),
                        latest = latest
                    };
                }

                // genexus_build_plan — Wave-3 item 30. Walks the callee graph for a target
                // and returns nodes/edges/totalEstimatedSeconds. Gateway pre-injects per-tool
                // p95 stats (in-memory tracker) so the worker estimator can use real timing.
                case "genexus_build_plan":
                {
                    JObject p95Map = Program.GetToolP95MapForBuildPlan();
                    return new
                    {
                        module = "BuildPlan",
                        action = "Generate",
                        target = args?["target"]?.ToString() ?? args?["name"]?.ToString(),
                        format = args?["format"]?.ToString(),
                        maxNodes = args?["maxNodes"]?.ToObject<int?>() ?? 100,
                        toolStatsP95 = p95Map
                    };
                }

                // blame merged into genexus_versioning umbrella (OperationsRouter).

                // Legados
                case "genexus_validate":
                    return new { module = "Validation", action = "Check", target = target, payload = args?["code"]?.ToString(), part = args?["part"]?.ToString() };
                case "genexus_build":
                    return new { module = "Build", action = args?["action"]?.ToString(), target = target };
                // genexus_history routing intentionally NOT handled here — the
                // canonical handler lives in OperationsRouter.cs:177 which forwards
                // the v2.6.6 Stream H fields (discard / part / snapshot). The old
                // duplicate handler dropped those flags silently because router
                // pipeline order made SystemRouter win before OperationsRouter.
                //
                default:
                    return null;
            }
        }
    }
}
