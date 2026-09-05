using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Unified read/write action classifier across the gateway (Issues #131 and #139).
    /// Used by MacroSuggestionService (to discard pure read-only sequences) and
    /// NextLegalActionsBuilder (to skip steps that don't need next-step follow-up).
    ///
    /// Action-bearing tools must be explicit here. An omitted or unknown action is
    /// deliberately conservative and is never treated as read-only.
    /// </summary>
    internal static class OperationClassifier
    {
        internal enum OperationKind
        {
            Unknown,
            ReadOnly,
            Mutating
        }

        private sealed class ActionContract
        {
            public ActionContract(IEnumerable<string> readOnly, IEnumerable<string> mutating)
            {
                ReadOnly = new HashSet<string>(readOnly, StringComparer.Ordinal);
                Mutating = new HashSet<string>(mutating, StringComparer.Ordinal);
            }

            public HashSet<string> ReadOnly { get; }
            public HashSet<string> Mutating { get; }
        }

        private static readonly HashSet<string> PureReadOnlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // These tools have no action property in the published schema.
            "genexus_query",
            "genexus_list_objects",
            "genexus_read",
            "genexus_inspect",
            "genexus_analyze",
            "genexus_whoami",
            "genexus_doctor",
            "genexus_search_source",
            "genexus_logs" // legacy alias
        };

        private static readonly Dictionary<string, ActionContract> ActionContracts =
            new Dictionary<string, ActionContract>(StringComparer.OrdinalIgnoreCase)
            {
                ["genexus_data_view"] = Contract(
                    readOnly: new[] { "inspect", "dry_run" },
                    mutating: new[] { "create", "update", "delete" }),
                ["genexus_recipe"] = Contract(
                    readOnly: new[] { "list", "describe", "suggest_macro" },
                    mutating: new[] { "crystallize" }),
                ["genexus_lifecycle"] = Contract(
                    readOnly: new[] { "reorg_preview", "validate", "validate-kb", "status", "result", "snapshots-list" },
                    mutating: new[] { "build", "cancel", "specify", "rebuild", "reorg", "sync", "index", "snapshots-restore" }),
                ["genexus_refactor"] = Contract(
                    readOnly: Array.Empty<string>(),
                    mutating: new[] { "RenameAttribute", "RenameVariable", "RenameObject", "ExtractProcedure", "ExtractSubroutine", "WWPSetCondition" }),
                ["genexus_gam"] = Contract(
                    readOnly: new[] { "status" },
                    mutating: new[] { "define_api", "deploy" }),
                ["genexus_properties"] = Contract(
                    readOnly: new[] { "get" },
                    mutating: new[] { "set", "move" }),
                ["genexus_structure"] = Contract(
                    readOnly: new[] { "get_visual", "get_indexes", "get_logic", "check_subtypes" },
                    mutating: new[] { "update_visual", "create_index", "drop_index", "set_attribute", "set_level", "set_domain", "update_group", "move_attribute", "remove_attribute" }),
                ["genexus_authoring"] = Contract(
                    readOnly: Array.Empty<string>(),
                    mutating: new[] { "add_external_method", "add_external_property", "add_menu_option", "add_condition" }),
                ["genexus_layout"] = Contract(
                    readOnly: new[] { "get_tree", "find_controls", "inspect_surface", "get_preview", "scan_mutators", "list_controls", "design_system" },
                    mutating: new[] { "set_property", "set_properties", "rename_printblock", "add_printblock", "delete_printblock" }),
                ["genexus_doc"] = Contract(
                    readOnly: new[] { "health" },
                    mutating: new[] { "wiki", "visualize" }),
                ["genexus_kb"] = Contract(
                    readOnly: new[] { "list", "list_environments", "get_environment", "get_startup" },
                    mutating: new[] { "open", "close", "set_default", "set_startup", "set_environment" }),
                ["genexus_navigation"] = Contract(
                    readOnly: Array.Empty<string>(),
                    mutating: new[] { "view" }),
                ["genexus_api"] = Contract(
                    readOnly: new[] { "list", "describe", "routes_inspect", "diff_baseline" },
                    mutating: new[] { "routes_clone", "routes_update", "snapshot" }),
                ["genexus_apply_pattern"] = Contract(
                    readOnly: new[] { "list_actions" },
                    mutating: new[] { "add_grid_action", "update_action", "move_action", "remove_action" }),
                ["genexus_security"] = Contract(
                    readOnly: new[] { "audit_gam", "scan_secrets", "scan_native" },
                    mutating: Array.Empty<string>()),
                ["genexus_edit_form"] = Contract(
                    readOnly: Array.Empty<string>(),
                    mutating: new[] { "add_textblock", "add_button", "set_visibility", "remove_control", "wrap_in_fieldset" }),
                ["genexus_module"] = Contract(
                    readOnly: new[] { "list" },
                    mutating: new[] { "install", "install_builtin", "update" }),
                ["genexus_gxserver"] = Contract(
                    readOnly: new[] { "status", "pending", "ignored", "conflicts", "history", "pipeline_list", "pipeline_runs", "pipeline_output" },
                    mutating: new[] { "commit", "update", "lock", "resolve", "pipeline_run", "pipeline_abort" }),
                ["genexus_kb_version"] = Contract(
                    readOnly: new[] { "list" },
                    mutating: new[] { "freeze", "branch", "set_active", "revert" }),
                ["genexus_browser"] = Contract(
                    readOnly: new[] { "smoke", "a11y", "wcag", "capture", "cross", "preview" },
                    mutating: Array.Empty<string>()),
                ["genexus_db"] = Contract(
                    readOnly: new[] { "drift_check", "drift_report", "optimize_analyze", "optimize_suggest", "optimize_report", "sql_ddl", "sql_navigation", "records_query", "types_list", "types_describe", "types_validate", "reorg_impact", "reorg_preview" },
                    mutating: new[] { "sample_data", "records_insert", "records_update", "translations_import" }),
                ["genexus_versioning"] = Contract(
                    readOnly: new[] { "history_list", "history_get", "time_travel", "blame", "diff", "diff_generated" },
                    mutating: new[] { "history_save", "history_restore", "undo" }),
                ["genexus_io"] = Contract(
                    readOnly: new[] { "asset_find", "asset_read", "ocr" },
                    mutating: new[] { "asset_write", "export_part", "import_part", "export_unified", "screenshot_publish" }),
                ["genexus_variable"] = Contract(
                    readOnly: Array.Empty<string>(),
                    mutating: new[] { "add", "delete", "modify" }),
                ["genexus_telemetry"] = Contract(
                    readOnly: new[] { "executions", "watch_event", "friction_tail", "learning_report", "logs", "profile_analyze", "profile_hotspots", "profile_correlate" },
                    mutating: new[] { "friction_append" }),
                ["genexus_create"] = Contract(
                    readOnly: new[] { "sd_panel_inspect" },
                    mutating: new[] { "object", "object_atomic", "popup", "sd_panel_create", "sd_panel_edit", "save_as", "scaffold", "translate", "sample", "template", "curl_procedure" }),
                ["genexus_memory"] = Contract(
                    readOnly: new[] { "recall", "list" },
                    mutating: new[] { "save", "forget", "promote", "consolidate" }),
                ["genexus_transfer"] = Contract(
                    readOnly: new[] { "inspect" },
                    mutating: new[] { "export", "import" }),
                ["genexus_deploy"] = Contract(
                    readOnly: new[] { "list_targets" },
                    mutating: new[] { "deploy" }),
                ["genexus_generator_reference"] = Contract(
                    readOnly: new[] { "list", "dry_run_add", "dry_run_remove" },
                    mutating: new[] { "add", "remove" }),
                ["genexus_wwp"] = Contract(
                    readOnly: new[] { "list" },
                    mutating: new[] { "add_action", "update_action", "move_action", "remove_action" })
            };

        // Only actions with a documented preview mode may become read-only when
        // dryRun=true. An arbitrary dryRun flag must not hide a write.
        private static readonly HashSet<string> DryRunCapableActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "genexus_data_view:create",
            "genexus_data_view:update",
            "genexus_data_view:delete",
            "genexus_properties:move",
            "genexus_lifecycle:build",
            "genexus_lifecycle:rebuild",
            "genexus_lifecycle:index",
            "genexus_structure:update_visual",
            "genexus_structure:create_index",
            "genexus_structure:drop_index",
            "genexus_structure:set_attribute",
            "genexus_structure:set_level",
            "genexus_structure:set_domain",
            "genexus_structure:update_group",
            "genexus_structure:move_attribute",
            "genexus_structure:remove_attribute",
            "genexus_api:routes_clone",
            "genexus_api:routes_update",
            "genexus_refactor:RenameAttribute",
            "genexus_refactor:RenameVariable",
            "genexus_refactor:RenameObject",
            "genexus_refactor:ExtractProcedure",
            "genexus_refactor:ExtractSubroutine",
            "genexus_refactor:WWPSetCondition",
            "genexus_apply_pattern:add_grid_action",
            "genexus_apply_pattern:update_action",
            "genexus_apply_pattern:move_action",
            "genexus_apply_pattern:remove_action",
            "genexus_edit_form:add_textblock",
            "genexus_edit_form:add_button",
            "genexus_edit_form:set_visibility",
            "genexus_edit_form:remove_control",
            "genexus_edit_form:wrap_in_fieldset",
            "genexus_versioning:history_restore",
            "genexus_versioning:undo",
            "genexus_variable:add",
            "genexus_variable:delete",
            "genexus_variable:modify",
            "genexus_create:object",
            "genexus_create:object_atomic",
            "genexus_create:popup",
            "genexus_create:save_as",
            "genexus_create:template",
            "genexus_memory:consolidate",
            "genexus_db:records_insert",
            "genexus_db:records_update",
            "genexus_transfer:import",
            "genexus_wwp:add_action",
            "genexus_wwp:update_action",
            "genexus_wwp:move_action",
            "genexus_wwp:remove_action",
            "genexus_generator_reference:add",
            "genexus_generator_reference:remove"
        };

        internal static IReadOnlyCollection<string> ActionTools => ActionContracts.Keys;

        internal static string BuildHelpContract(string toolName)
        {
            if (!ActionContracts.TryGetValue(toolName, out var contract)) return string.Empty;

            string readOnly = string.Join(", ", contract.ReadOnly.OrderBy(action => action, StringComparer.Ordinal)
                .Select(action => "`" + action + "`"));
            string mutating = string.Join(", ", contract.Mutating.OrderBy(action => action, StringComparer.Ordinal)
                .Select(action => "`" + action + "`"));
            string preview = string.Join(", ", DryRunCapableActions
                .Where(key => key.StartsWith(toolName + ":", StringComparison.OrdinalIgnoreCase))
                .Select(key => key.Substring(toolName.Length + 1))
                .OrderBy(action => action, StringComparer.Ordinal)
                .Select(action => "`" + action + "`"));

            var lines = new List<string>
            {
                "\n## Action contract\n",
                "- Read-only actions: " + (string.IsNullOrEmpty(readOnly) ? "none" : readOnly) + ".\n",
                "- Mutating actions: " + (string.IsNullOrEmpty(mutating) ? "none" : mutating) + ".\n"
            };
            if (!string.IsNullOrEmpty(preview))
            {
                lines.Add("- `dryRun=true` is a read-only preview only for: " + preview + ".\n");
            }

            return string.Concat(lines);
        }

        internal static OperationKind ClassifyAction(string? toolName, string? action)
        {
            if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(action))
                return OperationKind.Unknown;

            if (!ActionContracts.TryGetValue(toolName, out var contract))
                return OperationKind.Unknown;
            if (contract.ReadOnly.Contains(action)) return OperationKind.ReadOnly;
            if (contract.Mutating.Contains(action)) return OperationKind.Mutating;
            return OperationKind.Unknown;
        }

        public static bool IsReadOnly(string? toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            if (PureReadOnlyTools.Contains(toolName)) return true;

            string? action = args?["action"]?.ToString();
            var kind = ClassifyAction(toolName, action);
            if (kind == OperationKind.ReadOnly)
                return !HasKnownSideEffects(toolName, action, args);
            if (kind != OperationKind.Mutating) return false;

            string key = toolName + ":" + action;
            return args?["dryRun"]?.ToObject<bool?>() == true && DryRunCapableActions.Contains(key);
        }

        private static bool HasKnownSideEffects(string toolName, string? action, JObject? args)
        {
            if (!string.Equals(toolName, "genexus_browser", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(action, "preview", StringComparison.Ordinal))
            {
                return false;
            }

            if (args?["buildFirst"]?.ToObject<bool?>() == true
                || args?["updateBaseline"]?.ToObject<bool?>() == true)
            {
                return true;
            }

            JToken? capture = args?["capture"];
            if (capture is JArray captures)
            {
                return captures.Any(item => string.Equals(item?.ToString(), "screenshot", StringComparison.OrdinalIgnoreCase));
            }

            return string.Equals(capture?.ToString(), "screenshot", StringComparison.OrdinalIgnoreCase);
        }

        private static ActionContract Contract(IEnumerable<string> readOnly, IEnumerable<string> mutating)
            => new ActionContract(readOnly, mutating);
    }
}
