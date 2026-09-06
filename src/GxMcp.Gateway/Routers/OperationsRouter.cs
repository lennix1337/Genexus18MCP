using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    public class OperationsRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                // Creation umbrella: object|popup|sd_panel_*|save_as|scaffold|translate|sample|template.
                // Replaces genexus_create_object, _create_popup, _sd_panel, _save_as, _forge, _apply_template.
                case "genexus_create":
                    return ConvertCreateUmbrella(args);

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
                case "genexus_telemetry":
                    return ConvertTelemetryUmbrella(args);

                case "genexus_refactor":
                    return ConvertRefactorToolCall(args);

                // Item 91: genexus_rename_across_kb — thin wrapper that routes to the
                // existing RefactorService.Refactor(action=RenameObject|RenameAttribute)
                // path. The service already iterates the index's CalledBy edges and
                // patches every source-text call-site, so KB-wide rename is just the
                // RenameAttribute/RenameObject flow under a more discoverable name.
                case "genexus_rename_across_kb":
                {
                    string? from = args?["from"]?.ToString() ?? args?["oldName"]?.ToString();
                    string? to = args?["to"]?.ToString() ?? args?["newName"]?.ToString();
                    string? type = args?["type"]?.ToString();
                    bool renameAcrossDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                    // RenameAttribute path is the index-driven one (writes attribute then
                    // updates every CalledBy edge). For non-Attribute types, RenameObject
                    // currently falls into the same code path (line 64 of RefactorService).
                    string refactorAction = string.Equals(type, "Attribute", System.StringComparison.OrdinalIgnoreCase)
                        ? "RenameAttribute"
                        : "RenameObject";
                    return new
                    {
                        module = "Refactor",
                        action = refactorAction,
                        target = from,
                        dryRun = renameAcrossDryRun,
                        payload = new JObject
                        {
                            ["oldName"] = from,
                            ["newName"] = to,
                            ["type"] = type
                        }.ToString()
                    };
                }

                // Variable umbrella: add|delete|modify. Replaces _add_variable, _delete_variable, _modify_variable.
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
                        typeName = args?["typeName"]?.ToString(),
                        basedOn = args?["basedOn"]?.ToString(),
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
                case "genexus_versioning":
                    return ConvertVersioningUmbrella(args);

                // export_unified merged into genexus_io umbrella.

                case "genexus_format":
                    return new
                    {
                        module = "Formatting",
                        action = "Format",
                        payload = args?["code"]?.ToString()
                    };

                case "genexus_properties":
                    return ConvertPropertiesToolCall(args);

                // IO umbrella: asset_*|export_part|import_part|export_unified|screenshot_publish|ocr.
                // Replaces genexus_asset, _export_object, _import_object, _export_unified, _screenshot_publish, _ocr_screenshot.
                case "genexus_io":
                    return ConvertIoUmbrella(args);

                // history / undo merged into genexus_versioning umbrella.

                // Item 50 — genexus_security action=audit_gam
                case "genexus_security":
                    return new
                    {
                        module = "Security",
                        action = args?["action"]?.ToString() ?? "audit_gam"
                    };

                // Database umbrella: drift_check|drift_report|optimize_*|sql_*|sample_data|types_*.
                // Replaces genexus_db_drift, _db_optimize, _sql, _generate_sample_data, _types, _translations.
                case "genexus_db":
                    return ConvertDbUmbrella(args);

                // Item 19 (mcp-improvements-2026-05-22) — semantic WebForm edits.
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
                case "genexus_browser":
                    return ConvertBrowserUmbrella(args);

                // IDE Save-As parity.
                // save_as merged into genexus_create umbrella.

                // Item 65 — genexus_orient welcome card
                case "genexus_orient":
                    return new
                    {
                        module = "Orient",
                        action = "Welcome"
                    };

                case "genexus_structure":
                    return ConvertStructureToolCall(args);
                case "genexus_authoring":
                    return ConvertAuthoringToolCall(args);
                case "genexus_layout":
                    return ConvertLayoutToolCall(args);

                // create_popup merged into genexus_create umbrella.

                // genexus_api — REST endpoint introspection + breaking-change diff.
                // Single dispatcher arm; the worker's ApiIntrospectService.Run switches
                // on args.action (list|describe|diff_baseline|snapshot).
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
                // update_action|move_action|remove_action); dryRun supported.
                case "genexus_wwp":
                    return new
                    {
                        module = "WwpAction",
                        action = "Run",
                        @params = args
                    };

                // genexus_types — Domain/SDT introspection + value validation.
                default:
                    return null;
            }
        }

        // Creation umbrella dispatcher. Replaces _create_object/_create_popup/_sd_panel/_save_as/_forge/_apply_template.
        private static object? ConvertCreateUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? name = args?["name"]?.ToString();
            string? type = args?["type"]?.ToString();

            if (string.IsNullOrWhiteSpace(action))
            {
                if (args?["source"] != null || args?["variables"] != null || args?["rules"] != null || args?["parms"] != null)
                {
                    action = "object_atomic";
                }
                else if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type))
                {
                    action = "object";
                }
            }

            switch (action)
            {
                case "object":
                    return new
                    {
                        module = "Object",
                        action = "Create",
                        target = name,
                        type,
                        dataType = args?["dataType"]?.ToString(),
                        length = args?["length"]?.ToObject<int?>(),
                        decimals = args?["decimals"]?.ToObject<int?>(),
                        signed = args?["signed"]?.ToObject<bool?>(),
                        description = args?["description"]?.ToString(),
                        basedOn = args?["basedOn"]?.ToString(),
                        enumValues = args?["enumValues"],
                        // issue #28 item 7: SDT first-item seed override.
                        firstItem = args?["firstItem"]?.ToString(),
                        firstItemType = args?["firstItemType"]?.ToString(),
                        // issue #50: forward a requested folder/module destination so the worker
                        // can reject it loudly (SDK placement is a no-op) instead of silently
                        // creating in Root Module.
                        folder = args?["folder"]?.ToString(),
                        destModule = args?["module"]?.ToString(),
                        parentPath = args?["parentPath"]?.ToString(),
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                        // issue #60 — validationMode="specify" runs the inline Specify pass after
                        // creation; rollbackOnFailure deletes/reverts on spec errors (best-effort).
                        validationMode = args?["validationMode"]?.ToString(),
                        rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false
                    };

                case "object_atomic":
                    // Issue #62 — atomic create/update: one validated call with variables[],
                    // rules[], parms[], properties{} and source. Forward the raw args; the
                    // worker's AtomicCreateService validates the whole definition BEFORE the
                    // first save, composes the SDK write primitives, and compensates on failure
                    // (delete fresh object / restore snapshots) so nothing partial is left.
                    return new
                    {
                        module = "AtomicCreate",
                        action = "Run",
                        target = name,
                        @params = args
                    };

                case "popup":
                    return new
                    {
                        module = "Popup",
                        action = "Create",
                        target = name,
                        name,
                        spec = args?["spec"],
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                    };

                case "sd_panel_create":
                case "sd_panel_inspect":
                case "sd_panel_edit":
                {
                    var inner = action switch
                    {
                        "sd_panel_create" => "create",
                        "sd_panel_edit" => "edit",
                        _ => "inspect"
                    };
                    return new
                    {
                        module = "SdPanel",
                        action = inner,
                        target = name,
                        @params = args
                    };
                }

                case "save_as":
                    return new
                    {
                        module = "Object",
                        action = "SaveAs",
                        target = name,
                        @params = args
                    };

                case "scaffold":
                    return new
                    {
                        module = "Forge",
                        action = "Scaffold",
                        type,
                        name,
                        code = args?["content"]?.ToString(),
                        description = args?["description"]?.ToString()
                    };
                case "translate":
                    return new
                    {
                        module = "Conversion",
                        action = "TranslateTo",
                        target = name,
                        language = args?["content"]?.ToString()
                    };
                case "sample":
                    return new { module = "Pattern", action = "GetSample", target = type };

                case "template":
                    return new
                    {
                        module = "Write",
                        action = "ApplyTemplate",
                        target = name,
                        @params = args
                    };

                // P2 #9: scaffold a Procedure from a curl command over ICurlGeneratorService.
                case "curl_procedure":
                    return new
                    {
                        module = "CurlProc",
                        action = "Run",
                        @params = args
                    };

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_create: unknown action '{action}'. Valid: object|object_atomic|popup|sd_panel_create|sd_panel_inspect|sd_panel_edit|curl_procedure|save_as|scaffold|translate|sample|template."
                    };
            }
        }

        // Telemetry umbrella dispatcher (worker-routed actions). executions + watch_event
        // are gateway-only and short-circuited in Program.cs before reaching here.
        private static object? ConvertTelemetryUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            switch (action)
            {
                case "logs":
                    return new
                    {
                        module = "Object",
                        action = "ReadLogs",
                        target = "_self",
                        lines = args?["tail"]?.ToObject<int?>() ?? args?["lines"]?.ToObject<int?>() ?? 100,
                        filterCorrelation = args?["filterCorrelation"]?.ToString(),
                        grep = args?["grep"]?.ToString(),
                        since = args?["since"]?.ToString(),
                        objectFilter = args?["target"]?.ToString()
                    };

                case "friction_append":
                    return new
                    {
                        module = "FrictionLog",
                        action = "Append",
                        tool = args?["tool"]?.ToString(),
                        message = args?["message"]?.ToString(),
                        severity = args?["severity"]?.ToString()
                    };

                case "friction_tail":
                    return new
                    {
                        module = "FrictionLog",
                        action = "Tail",
                        n = args?["n"]?.ToObject<int?>() ?? 20
                    };

                case "learning_report":
                    return new
                    {
                        module = "Learning",
                        action = "Report",
                        since = args?["since"]?.ToString(),
                        until = args?["until"]?.ToString()
                    };

                case "profile_analyze":
                case "profile_hotspots":
                case "profile_correlate":
                {
                    var inner = action switch
                    {
                        "profile_hotspots" => "hotspots",
                        "profile_correlate" => "correlate",
                        _ => "analyze"
                    };
                    return new
                    {
                        module = "Profile",
                        action = inner,
                        target = args?["target"]?.ToString(),
                        @params = args
                    };
                }

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_telemetry: unknown or gateway-only action '{action}'. Worker-routed: logs|friction_append|friction_tail|learning_report|profile_analyze|profile_hotspots|profile_correlate."
                    };
            }
        }

        // IO umbrella dispatcher. Replaces _asset/_export_object/_import_object/_export_unified/_screenshot_publish/_ocr_screenshot.
        private static object? ConvertIoUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            switch (action)
            {
                case "asset_find":
                case "asset_read":
                case "asset_write":
                {
                    var inner = action switch
                    {
                        "asset_find" => "Find",
                        "asset_read" => "Read",
                        _ => "Write"
                    };
                    return new
                    {
                        module = "Asset",
                        action = inner,
                        target = args?["path"]?.ToString(),
                        pattern = args?["pattern"]?.ToString(),
                        relativeRoot = args?["relativeRoot"]?.ToString(),
                        limit = args?["limit"]?.ToObject<int?>(),
                        includeContent = args?["includeContent"]?.ToObject<bool?>(),
                        maxBytes = args?["maxBytes"]?.ToObject<int?>(),
                        contentBase64 = args?["contentBase64"]?.ToString()
                    };
                }

                case "export_part":
                    return new
                    {
                        module = "Object",
                        action = "ExportText",
                        target = args?["name"]?.ToString(),
                        outputPath = args?["outputPath"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false
                    };

                case "import_part":
                    return new
                    {
                        module = "Object",
                        action = "ImportText",
                        target = args?["name"]?.ToString(),
                        inputPath = args?["inputPath"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        type = args?["type"]?.ToString()
                    };

                case "export_unified":
                    return new
                    {
                        module = "Export",
                        action = "Unified",
                        target = args?["name"]?.ToString(),
                        @params = new JObject { ["type"] = args?["type"]?.ToString() }
                    };

                case "screenshot_publish":
                    return new { module = "ScreenshotPublish", action = "Publish", path = args?["path"]?.ToString() };

                case "ocr":
                    return new { module = "Ocr", action = "Run", path = args?["path"]?.ToString() };

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_io: unknown action '{action}'. Valid: asset_find|asset_read|asset_write|export_part|import_part|export_unified|screenshot_publish|ocr."
                    };
            }
        }

        // Versioning umbrella dispatcher. Replaces _history/_undo/_time_travel/_blame/_diff/_diff_generated.
        private static object? ConvertVersioningUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? name = args?["name"]?.ToString();

            switch (action)
            {
                case "history_list":
                    return new { module = "History", action = "list", target = name, part = RouterArgs.Str(args, "part") };
                case "history_get":
                    return new { module = "History", action = "get_source", target = name, versionId = RouterArgs.Int(args, "versionId"), part = RouterArgs.Str(args, "part") };
                case "history_save":
                    return new { module = "History", action = "save", target = name, part = RouterArgs.Str(args, "part") };
                case "history_restore":
                    return new
                    {
                        module = "History",
                        action = "restore",
                        target = name,
                        part = RouterArgs.Str(args, "part"),
                        snapshot = RouterArgs.Str(args, "snapshot"),
                        discard = RouterArgs.Bool(args, "discard"),
                        dryRun = RouterArgs.Bool(args, "dryRun")
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
        private static object? ConvertDbUmbrella(JObject? args)
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
                    return new
                    {
                        module = "types",
                        action = inner,
                        target = args?["name"]?.ToString() ?? args?["type"]?.ToString() ?? target,
                        @params = args
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
        private static object? ConvertBrowserUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString()?.ToLowerInvariant();
            string? target = args?["target"]?.ToString() ?? args?["name"]?.ToString();

            switch (action)
            {
                case "smoke":
                    return new { module = "smoke_test", action = "Run", target, name = target };

                case "a11y":
                    return new { module = "a11y_audit", action = "Audit", target, name = target };

                case "wcag":
                    return new { module = "WcagCheck", action = "Check", target };

                case "capture":
                    return new
                    {
                        module = "browser_capture",
                        action = "Capture",
                        target,
                        name = target,
                        capture = args?["capture"]
                    };

                case "cross":
                    return new
                    {
                        module = "CrossBrowser",
                        action = "Run",
                        target,
                        browsers = args?["browsers"],
                        capture = args?["capture"]
                    };

                case "preview":
                {
                    var mode = args?["mode"]?.ToString();
                    var previewAction = string.Equals(mode, "run", StringComparison.OrdinalIgnoreCase) ? "Run" : "Render";
                    return new
                    {
                        module = "Preview",
                        action = previewAction,
                        target,
                        name = target,
                        parms = args?["parms"],
                        launcher = args?["launcher"]?.ToString() ?? "auto",
                        buildFirst = args?["buildFirst"]?.ToObject<bool?>() ?? false,
                        waitMs = args?["waitMs"]?.ToObject<int?>() ?? 3000,
                        capture = args?["capture"],
                        diffBaseline = args?["diffBaseline"]?.ToObject<bool?>() ?? false,
                        updateBaseline = args?["updateBaseline"]?.ToObject<bool?>() ?? false,
                        fill = args?["fill"],
                        click = args?["click"]?.ToString(),
                        auth = args?["auth"],
                        emulate = args?["emulate"]?.ToString(),
                        network = args?["network"]?.ToString()
                    };
                }

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_browser: unknown action '{action}'. Valid: smoke|a11y|wcag|capture|cross|preview."
                    };
            }
        }

        private static object? ConvertRefactorToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;
            bool refactorDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;

            if (action == "ExtractProcedure")
            {
                JObject? payloadObj = null;
                if (args?["payload"] is JObject pObj) payloadObj = pObj;
                else if (args?["payload"] is JValue pVal && pVal.Value is string pStr && pStr.TrimStart().StartsWith("{"))
                {
                    try { payloadObj = JObject.Parse(pStr); } catch { }
                }

                return new
                {
                    module = "Refactor",
                    action,
                    target = args?["target"]?.ToString() ?? args?["objectName"]?.ToString() ?? payloadObj?["target"]?.ToString(),
                    dryRun = refactorDryRun,
                    payload = new JObject
                    {
                        ["code"] = args?["code"]?.ToString() ?? args?["codeToExtract"]?.ToString() ?? payloadObj?["code"]?.ToString() ?? payloadObj?["codeToExtract"]?.ToString(),
                        ["procedureName"] = args?["procedureName"]?.ToString() ?? payloadObj?["procedureName"]?.ToString() ?? payloadObj?["name"]?.ToString()
                    }.ToString()
                };
            }

            if (action == "ExtractSubroutine" || string.Equals(action, "extract_subroutine", StringComparison.OrdinalIgnoreCase))
            {
                JObject? payloadObj = null;
                if (args?["payload"] is JObject pObj) payloadObj = pObj;
                else if (args?["payload"] is JValue pVal && pVal.Value is string pStr && pStr.TrimStart().StartsWith("{"))
                {
                    try { payloadObj = JObject.Parse(pStr); } catch { }
                }

                return new
                {
                    module = "Refactor",
                    action = "ExtractSubroutine",
                    target = args?["target"]?.ToString() ?? args?["objectName"]?.ToString() ?? payloadObj?["target"]?.ToString(),
                    dryRun = refactorDryRun,
                    payload = new JObject
                    {
                        ["code"] = args?["code"]?.ToString() ?? args?["codeToExtract"]?.ToString() ?? payloadObj?["code"]?.ToString() ?? payloadObj?["codeToExtract"]?.ToString(),
                        ["subroutineName"] = args?["subroutineName"]?.ToString() ?? args?["subroutine"]?.ToString() ?? args?["name"]?.ToString() ?? payloadObj?["subroutineName"]?.ToString() ?? payloadObj?["subroutine"]?.ToString() ?? payloadObj?["name"]?.ToString()
                    }.ToString()
                };
            }

            if (action == "WWPSetCondition")
            {
                return new
                {
                    module = "Refactor",
                    action,
                    target = args?["target"]?.ToString() ?? args?["objectName"]?.ToString(),
                    dryRun = refactorDryRun,
                    payload = new JObject
                    {
                        ["controlAttribute"] = args?["controlAttribute"]?.ToString(),
                        ["value"] = args?["value"]?.ToString(),
                        ["typeFilter"] = args?["type"]?.ToString()
                    }.ToString()
                };
            }

            string? target = args?["target"]?.ToString();
            if (action == "RenameVariable")
            {
                target = args?["objectName"]?.ToString();
            }

            return new
            {
                module = "Refactor",
                action,
                target,
                dryRun = refactorDryRun,
                payload = new JObject
                {
                    ["oldName"] = args?["target"]?.ToString(),
                    ["newName"] = args?["newName"]?.ToString(),
                    ["type"] = args?["type"]?.ToString()
                }.ToString()
            };
        }

        private static object? ConvertPropertiesToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            if (action.Equals("set", System.StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    module = "Property",
                    action = "Set",
                    target = args?["name"]?.ToString(),
                    propertyName = args?["propertyName"]?.ToString(),
                    value = args?["value"]?.ToString(),
                    properties = args?["properties"] as JObject,
                    control = args?["control"]?.ToString(),
                    type = args?["type"]?.ToString(),
                    // issue #60 — validationMode="specify" runs the inline Specify pass after
                    // the property write; rollbackOnFailure restores on spec errors.
                    validationMode = args?["validationMode"]?.ToString(),
                    rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false
                };
            }

            if (action.Equals("move", System.StringComparison.OrdinalIgnoreCase))
            {
                string? targetModule = args?["targetModule"]?.ToString();
                string? explicitDestination = args?["destination"]?.ToString();
                return new
                {
                    module = "Property",
                    action = "Move",
                    target = args?["name"]?.ToString(),
                    destination = explicitDestination ?? targetModule,
                    targetModule,
                    folder = args?["folder"]?.ToString(),
                    module_ = args?["module"]?.ToString(),
                    destModule = args?["destModule"]?.ToString(),
                    destKind = args?["destKind"]?.ToString()
                        ?? (string.IsNullOrWhiteSpace(explicitDestination)
                            && !string.IsNullOrWhiteSpace(targetModule) ? "Module" : null),
                    dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                    baseVersion = args?["baseVersion"]?.ToString(),
                    rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true,
                    type = args?["type"]?.ToString()
                };
            }

            return new
            {
                module = "Property",
                action = "Get",
                target = args?["name"]?.ToString(),
                control = args?["control"]?.ToString(),
                type = args?["type"]?.ToString()
            };
        }

        private static object? ConvertAssetToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            return new
            {
                module = "Asset",
                action = char.ToUpperInvariant(action[0]) + action.Substring(1).ToLowerInvariant(),
                target = args?["path"]?.ToString(),
                pattern = args?["pattern"]?.ToString(),
                relativeRoot = args?["relativeRoot"]?.ToString(),
                limit = args?["limit"]?.ToObject<int?>(),
                includeContent = args?["includeContent"]?.ToObject<bool?>(),
                maxBytes = args?["maxBytes"]?.ToObject<int?>(),
                contentBase64 = args?["contentBase64"]?.ToString()
            };
        }

        private static object? ConvertStructureToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? mappedAction = action switch
            {
                "get_visual" => "GetVisualStructure",
                "update_visual" => "UpdateVisualStructure",
                "get_indexes" => "GetVisualIndexes",
                "create_index" => "CreateIndex",
                "drop_index" => "DropIndex",
                "set_attribute" => "SetAttributeProperties",
                "set_level" => "SetLevelProperties",
                "set_domain" => "SetDomainProperties",
                "get_logic" => "GetLogicStructure",
                "update_group" => "UpdateGroupStructure",
                "move_attribute" => "MoveAttribute",
                // Issue #97: native TransactionLevel.Items attribute removal (lets agents
                // drop + re-add a misclassified subtype attribute to force re-derivation)
                // and the subtype-classification guard-rail.
                "remove_attribute" => "RemoveAttribute",
                "check_subtypes" => "CheckSubtypes",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Structure",
                action = mappedAction,
                target = args?["name"]?.ToString(),
                type = args?["type"]?.ToString(),
                payload = args?["payload"]?.ToString(),
                transactionModule = args?["module"]?.ToString(),
                attribute = args?["attribute"]?.ToString(),
                before = args?["before"]?.ToString(),
                after = args?["after"]?.ToString(),
                position = args?["position"]?.ToObject<int?>(),
                level = args?["level"]?.ToString(),
                levelPath = args?["levelPath"],
                dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                baseVersion = args?["baseVersion"]?.ToString(),
                expectedVersion = args?["expectedVersion"]?.ToString(),
                // issue #60 — validationMode="specify" runs the inline Specify pass after a
                // structure write; rollbackOnFailure restores the pre-write state on spec errors.
                validationMode = args?["validationMode"]?.ToString(),
                rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>()
                    ?? (string.Equals(action, "create_index", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(action, "update_visual", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(action, "move_attribute", StringComparison.OrdinalIgnoreCase))
            };
        }

        private static object? ConvertAuthoringToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? mappedAction = action switch
            {
                "add_external_method" => "AddExternalMethod",
                "add_external_property" => "AddExternalProperty",
                "add_menu_option" => "AddMenuOption",
                "add_condition" => "AddDataSelectorCondition",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Authoring",
                action = mappedAction,
                target = args?["name"]?.ToString(),
                payload = args?["payload"]?.ToString()
            };
        }

        private static object? ConvertLayoutToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            // P2 #10: control/theme catalog over IUserControlsManagerService — a distinct
            // service, not a Layout mutator, so route it to its own worker module.
            if (string.Equals(action, "list_controls", System.StringComparison.OrdinalIgnoreCase))
                return new { module = "UserControls", action = "Run", @params = args };

            // P3: Design System Object tokens/classes/images over DesignSystemHelper.
            if (string.Equals(action, "design_system", System.StringComparison.OrdinalIgnoreCase))
                return new { module = "DesignSystem", action = "Run", @params = args };

            string? objectName = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(objectName))
            {
                objectName = args?["target"]?.ToString();
            }

            string? mappedAction = action switch
            {
                "get_tree" => "GetTree",
                "set_property" => "SetProperty",
                "find_controls" => "FindControls",
                "set_properties" => "SetProperties",
                "inspect_surface" => "InspectSurface",
                "get_preview" => "GetVisualPreview",
                "scan_mutators" => "ScanMutators",
                "rename_printblock" => "RenamePrintBlock",
                "add_printblock" => "AddPrintBlock",
                "delete_printblock" => "DeletePrintBlock",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Layout",
                action = mappedAction,
                target = objectName,
                control = args?["control"]?.ToString(),
                propertyName = args?["propertyName"]?.ToString(),
                value = args?["value"]?.ToString(),
                query = args?["query"]?.ToString(),
                changes = args?["changes"],
                limit = args?["limit"]?.ToObject<int?>(),
                currentName = args?["currentName"]?.ToString(),
                newName = args?["newName"]?.ToString(),
                printBlockName = args?["printBlockName"]?.ToString(),
                height = args?["height"]?.ToObject<int?>()
            };
        }
    }
}
