using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    public class OperationsRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        private static readonly IReadOnlyList<IMcpModuleRouter> DomainRouters = new IMcpModuleRouter[]
        {
            new CreateRouter(), new TelemetryRouter(), new IoRouter(), new VersioningRouter(),
            new DatabaseRouter(), new BrowserRouter(), new RefactorRouter(), new PropertiesRouter(),
            new StructureRouter(), new AuthoringRouter(), new LayoutRouter()
        };

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            foreach (var router in DomainRouters)
            {
                var result = router.ConvertToolCall(toolName, args);
                if (result != null) return result;
            }

            switch (toolName)
            {
                // Creation umbrella: object|popup|sd_panel_*|save_as|scaffold|translate|sample|template.
                // Replaces genexus_create_object, _create_popup, _sd_panel, _save_as, _forge, _apply_template.
                case "genexus_data_view":
                    return new
                    {
                        module = "DataView",
                        action = "Run",
                        target = args?["transaction"]?.ToString(),
                        @params = args
                    };

                case "genexus_generator_reference":
                    return new
                    {
                        module = "GeneratorReference",
                        action = "Run",
                        @params = args
                    };

                case "genexus_delete_object":
                    return new
                    {
                        module = "Object",
                        action = "Delete",
                        target = args?["name"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        confirm = args?["confirm"]?.ToObject<bool?>() ?? false,
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                        expectedVersion = args?["expectedVersion"]?.ToString()
                    };

                case "genexus_worker_reload":
                    // FR#20 (v2.6.6 Stream B): mode=soft|hard, default soft. Forwarded
                    // verbatim — the worker's CommandDispatcher negotiates default when null.
                    return new
                    {
                        module = "Object",
                        action = "WorkerReload",
                        target = "_self",
                        sourceDir = args?["sourceDir"]?.ToString(),
                        mode = args?["mode"]?.ToString(),
                        drainTimeoutMs = args?["drainTimeoutMs"]?.ToObject<int?>()
                    };

                // Telemetry umbrella: friction_*|learning_report|logs|profile_*.
                // (executions / watch_event are gateway-only — handled in Program.cs before routing.)
                // Replaces genexus_logs, _friction_log, _learning, _profile.
                case "genexus_variable":
                {
                    string? vAction = args?["action"]?.ToString()?.ToLowerInvariant();
                    string mapped = vAction switch
                    {
                        "delete" => "DeleteVariable",
                        "modify" => "ModifyVariable",
                        _ => "AddVariable"
                    };
                    return new
                    {
                        module = "Write",
                        action = mapped,
                        target = args?["name"]?.ToString(),
                        varName = args?["varName"]?.ToString(),
                        typeName = args?["typeName"]?.Type == JTokenType.Null ? null : args?["typeName"]?.ToString(),
                        newTypeName = args?["newTypeName"]?.Type == JTokenType.Null ? null : args?["newTypeName"]?.ToString(),
                        dataType = args?["dataType"]?.Type == JTokenType.Null ? null : args?["dataType"]?.ToString(),
                        basedOn = args?["basedOn"]?.Type == JTokenType.Null ? null : args?["basedOn"]?.ToString(),
                        basedOnAttribute = args?["basedOnAttribute"]?.ToString(),
                        objectType = args?["objectType"]?.ToString(),
                        objectName = args?["objectName"]?.ToString(),
                        objectModule = args?["module"]?.ToString(),
                        expectedVersion = args?["expectedVersion"]?.ToString() ?? args?["baseVersion"]?.ToString(),
                        // issue #28 items 8/9: explicit length/decimals + collection flag.
                        length = args?["length"]?.ToObject<int?>(),
                        decimals = args?["decimals"]?.ToObject<int?>(),
                        collection = args?["collection"]?.ToObject<bool?>(),
                        // issue #32 item 1: batch add — array of {varName,typeName,length,decimals,collection}.
                        variables = args?["variables"],
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                        // issue #60 — validationMode="specify" runs the inline Specify pass after
                        // the write; rollbackOnFailure restores the pre-write state on spec errors.
                        validationMode = args?["validationMode"]?.ToString(),
                        rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>()
                            ?? !string.IsNullOrWhiteSpace(args?["objectType"]?.ToString())
                    };
                }

                case "genexus_validate_payload":
                    return new
                    {
                        module = "Write",
                        action = "ValidatePayload",
                        target = args?["name"]?.ToString(),
                        payload = args?["content"]?.ToString(),
                        @params = new JObject { ["part"] = args?["part"]?.ToString() }
                    };

                case "genexus_bulk_edit":
                    return new
                    {
                        module = "Write",
                        action = "Bulk",
                        @params = args
                    };

                // apply_template merged into genexus_create umbrella.

                case "genexus_apply_pattern":
                {
                    // Item 45: mode=diagnose routes to read-only Diagnose action; default → Apply.
                    // Item 21 (friction 2026-05-22): dryRun=true is an alias for mode=diagnose
                    // — both return the same read-only findings without mutating the KB.
                    string apPatMode = args?["mode"]?.ToString();
                    bool isDiagnose = string.Equals(apPatMode, "diagnose", System.StringComparison.OrdinalIgnoreCase);
                    bool isActions = string.Equals(apPatMode, "actions", System.StringComparison.OrdinalIgnoreCase);
                    bool isDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                    return new
                    {
                        module = "Pattern",
                        action = isActions ? "ManageActions" : (isDiagnose || isDryRun) ? "Diagnose" : "Apply",
                        target = args?["name"]?.ToString(),
                        @params = args
                    };
                }

                case "genexus_sdk_probe":
                    string sdkProbeMode = args?["mode"]?.ToString();
                    return new
                    {
                        module = "SdkProbe",
                        action = string.Equals(sdkProbeMode, "capabilities", StringComparison.OrdinalIgnoreCase) ? "Capabilities" : "Run",
                        target = "_self",
                        outputDir = args?["outputDir"]?.ToString(),
                        mode = sdkProbeMode
                    };

                // Versioning umbrella: history_*|undo|time_travel|blame|diff|diff_generated.
                // Replaces genexus_history, _undo, _time_travel, _blame, _diff, _diff_generated.
                case "genexus_format":
                    return new
                    {
                        module = "Formatting",
                        action = "Format",
                        payload = args?["code"]?.ToString()
                    };

                case "genexus_security":
                    return new
                    {
                        module = "Security",
                        action = args?["action"]?.ToString() ?? "audit_gam"
                    };

                // Database umbrella: drift_check|drift_report|optimize_*|sql_*|sample_data|types_*.
                // Replaces genexus_db_drift, _db_optimize, _sql, _generate_sample_data, _types, _translations.
                case "genexus_edit_form":
                {
                    string editAction = args?["action"]?.ToString();
                    string normalised = string.IsNullOrEmpty(editAction)
                        ? string.Empty
                        : editAction.Trim();
                    return new
                    {
                        module = "WebFormEdit",
                        action = normalised,
                        target = args?["name"]?.ToString(),
                        @params = args
                    };
                }

                case "genexus_k2b_designer":
                    return new
                    {
                        module = "K2bDesigner",
                        action = args?["action"]?.ToString() ?? "inspect",
                        target = args?["name"]?.ToString(),
                        @params = args
                    };

                // Item 11 — resolve runtime URL + optional GAM cookies. No browser launch.
                case "genexus_run_object":
                    return new
                    {
                        module = "RunObject",
                        action = "Resolve",
                        target = args?["name"]?.ToString(),
                        name = args?["name"]?.ToString(),
                        args = args?["args"],
                        gamSession = args?["gamSession"],
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                    };

                // Item 68 — deterministic PM-readable summary.
                case "genexus_explain":
                    return new
                    {
                        module = "Explain",
                        action = "Explain",
                        target = args?["name"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        depth = args?["depth"]?.ToString()
                    };

                // diff_generated merged into genexus_versioning umbrella.

                // Item 90 — Markdown README generation.
                case "genexus_kb_readme":
                    return new
                    {
                        module = "KbReadme",
                        action = "Generate",
                        target = "_self",
                        outputPath = args?["outputPath"]?.ToString()
                    };

                // ocr_screenshot merged into genexus_io umbrella.

                case "genexus_pr_description":
                    return new
                    {
                        module = "PrDescription",
                        action = "Generate",
                        last = args?["last"]?.ToObject<int?>() ?? 10,
                        workingDir = args?["workingDir"]?.ToString()
                    };

                // screenshot_publish merged into genexus_io umbrella.

                // friction_log + learning merged into genexus_telemetry umbrella.

                // Item 78 — SDPanel parity proxy.
                // sd_panel merged into genexus_create umbrella.

                // Item 84 — multi-agent file lock.
                case "genexus_multi_agent_lock":
                    return new
                    {
                        module = "MultiAgentLock",
                        action = args?["action"]?.ToString() ?? "status",
                        target = args?["target"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        ownerId = args?["ownerId"]?.ToString(),
                        ttlSec = args?["ttlSec"]?.ToObject<int?>() ?? 300
                    };

                // Phase 1/3 — per-KB memory (genexus_memory).
                case "genexus_memory":
                    return new
                    {
                        module = "Memory",
                        action = args?["action"]?.ToString() ?? "recall",
                        target = args?["target"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        tags = args?["tags"],
                        fact = args?["fact"]?.ToString(),
                        id = args?["id"]?.ToString(),
                        dryRun = args?["dryRun"],
                        message = args?["message"]?.ToString()
                    };

                // Item 86 — typed-change impact simulator (no mutation).
                case "genexus_what_if":
                    return new
                    {
                        module = "WhatIf",
                        action = "Simulate",
                        change = args?["change"]
                    };

                // genexus_doctor — health-check triage envelope. No args.
                case "genexus_doctor":
                    return new
                    {
                        module = "Doctor",
                        action = "Diagnose",
                        target = "_self"
                    };

                // Item 66 — static step-by-step onboarding walkthrough.
                case "genexus_tutorial":
                    return new { module = "Tutorial", action = "Step", step = args?["step"]?.ToObject<int?>() ?? 1 };

                // genexus_playbook — deferred-load skill packs. Returns embedded
                // markdown for a named topic. NO KB state.
                case "genexus_playbook":
                    return new
                    {
                        module = "Playbook",
                        action = "Read",
                        topic = args?["topic"]?.ToString(),
                        list = args?["list"]?.ToObject<bool?>() ?? false
                    };

                // Read-only surface for GxServer sync state. No SDK calls; worker probes
                // <kbPath>/Repository/Repository.gxs and similar metadata files.
                case "genexus_gxserver":
                    return new
                    {
                        module = "GxServer",
                        action = "Run",
                        @params = args
                    };

                // genexus_compare — read-only IDE "Compare Objects" parity over the
                // SDK's IComparerService. No SDK calls happen in the gateway; worker
                // resolves both objects and asks IComparerService.AreEqualInContent/
                // AreEqualInProperties. See docs/sdk_coverage_gap_matrix.md P0 #2.
                case "genexus_compare":
                    return new
                    {
                        module = "Compare",
                        action = "Run",
                        @params = args
                    };

                // genexus_module — GeneXus Module Manager (install/update modules) over
                // the SDK's IModuleManagerService. action=list is read-only.
                case "genexus_module":
                    return new
                    {
                        module = "Module",
                        action = "Run",
                        @params = args
                    };

                // genexus_gam — GAM / integrated-security provisioning over the SDK's
                // IIntegratedSecurityService. No SDK calls happen in the gateway; the
                // worker resolves the service and dispatches on args.action. Destructive
                // (define_api/deploy) — see GamService for guards.
                case "genexus_gam":
                    return new
                    {
                        module = "Gam",
                        action = "Run",
                        @params = args
                    };

                // genexus_merge — WRITE surface over the SDK's IMergeService (2-way/3-way
                // object merge). No SDK calls happen in the gateway; worker resolves both/
                // three objects and calls IMergeService.MergeObjects. dryRun defaults true.
                case "genexus_merge":
                    return new
                    {
                        module = "Merge",
                        action = "Run",
                        @params = args
                    };

                // genexus_kb_version — KB model-version management (Create Version /
                // Branch / Activate / Revert) over the SDK's static KBVersionHelper.
                // action=list is read-only; freeze/branch/set_active/revert mutate.
                case "genexus_kb_version":
                    return new
                    {
                        module = "KbVersion",
                        action = "Run",
                        @params = args
                    };

                // genexus_transfer — real XPZ export/import over IKnowledgeManagerService
                // (dependency-aware, IDE Export/Import parity). action=export|inspect|import.
                // import is destructive (dryRun defaults true; confirm=true to apply).
                case "genexus_transfer":
                    return new
                    {
                        module = "Transfer",
                        action = "Run",
                        @params = args
                    };

                // genexus_deploy — deploy application over IDeploymentService /
                // IDeploymentTargetService. action=list_targets (read-only) | deploy
                // (destructive, confirm=true).
                case "genexus_deploy":
                    return new
                    {
                        module = "Deploy",
                        action = "Run",
                        @params = args
                    };

                // Item 71 — gh CLI passthrough.
                case "genexus_github":
                    return new
                    {
                        module = "Github",
                        action = "CreatePr",
                        title = args?["title"]?.ToString(),
                        body = args?["body"]?.ToString(),
                        @base = args?["base"]?.ToString(),
                        workingDir = args?["workingDir"]?.ToString(),
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                    };

                // Item 81 — OpenAI-compatible LLM endpoint forward.
                case "genexus_ai_complete":
                    return new
                    {
                        module = "AiComplete",
                        action = "Complete",
                        name = args?["name"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        context = args?["context"]?.ToString(),
                        maxTokens = args?["maxTokens"]?.ToObject<int?>() ?? 200
                    };

                // time_travel merged into genexus_versioning umbrella.

                // Item 83 — voice transcript → intent mapping.
                case "genexus_voice":
                    return new { module = "Voice", action = "Intent", transcript = args?["transcript"]?.ToString() };

                // Item 95 — generate GXtest stubs from production JSONL log.
                case "genexus_auto_test":
                    return new { module = "AutoTest", action = "Generate", path = args?["path"]?.ToString() };

                // Item 96 — surface structural commonalities across N objects.
                case "genexus_reverse_pattern":
                    return new { module = "ReversePattern", action = "Infer", source = args?["source"] };

                // Browser umbrella: action=smoke|a11y|wcag|capture|cross|preview.
                // Replaces genexus_smoke_test/_a11y_audit/_wcag_check/_browser_capture/_cross_browser/_preview.
                // Legacy names still dispatch silently via LegacyToolAliases (McpRouter) until removed.
                case "genexus_orient":
                    return new
                    {
                        module = "Orient",
                        action = "Welcome"
                    };

                case "genexus_api":
                    return new
                    {
                        module = "Api",
                        action = args?["action"]?.ToString() ?? "list",
                        target = args?["target"]?.ToString(),
                        @params = args
                    };

                // genexus_profile — runtime profiler XML bridge (file-only ingest v1).
                // Worker's ProfileService.Run switches on args.action (analyze|hotspots|correlate).
                // profile merged into genexus_telemetry umbrella.

                // issue #58 — genexus_wwp: WorkWithPlus Action Group / grid-action
                // editing over the host's PatternInstance XML. Worker's
                // WwpActionService.Run switches on args.action (list|add_action|
                // add_user_action|update_action|move_action|remove_action); dryRun supported.
                case "genexus_wwp":
                    return new
                    {
                        module = "WwpAction",
                        action = "Run",
                        target = args?["name"]?.ToString(),
                        @params = args
                    };

                // genexus_types — Domain/SDT introspection + value validation.
                default:
                    return null;
            }
        }

    }
}
