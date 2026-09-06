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
            "genexus_compare",
            "genexus_format",
            "genexus_logs" // legacy alias
        };

        private static readonly HashSet<string> KnownMutatingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_edit", "genexus_create_object", "genexus_delete_object", "genexus_refactor",
            "genexus_forge", "genexus_import_object", "genexus_test",
            "genexus_connection_recover", "genexus_worker_reload"
        };

        // Published tools without an action property still need a typed policy.
        // These entries are intentionally explicit: a tool name must never be
        // classified by a substring such as "edit" or "create".
        private static readonly HashSet<string> ModeDependentTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_sdk_probe",
            "genexus_run_object",
            "genexus_merge"
        };

        // Legacy tools without an action field still need an explicit policy while
        // their callers migrate to an umbrella contract. Keep this set finite and
        // named; substring checks belong only in the compatibility fallback.
        private static readonly HashSet<string> NameOnlyMutatingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_edit_form",
            "genexus_edit_and_build",
            "genexus_bulk_edit",
            "genexus_create",
            "genexus_variable",
            "genexus_sd_panel_create",
            "genexus_sd_panel_edit",
            "genexus_rename_across_kb",
            "genexus_kb_import",
            "genexus_apply_pattern"
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
                    readOnly: new[] { "reorg_preview", "status", "result", "snapshots-list", "inspect" },
                    mutating: new[] { "build", "cancel", "specify", "validate", "validate-kb", "rebuild", "reorg", "sync", "index", "snapshots-restore", "reconcile" }),
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

        /// <summary>
        /// Classifies both action-bearing and action-less published tools from
        /// the same registry used by cache, retry and invalidation callers.
        /// A mode-dependent operation returns Unknown for an unsupported mode so
        /// retry/cache policy fails closed instead of guessing.
        /// </summary>
        internal static OperationKind ClassifyTool(string? toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return OperationKind.Unknown;

            var effectiveArgs = NormalizeArguments(toolName, args, out var canonical);
            return ClassifyCanonicalTool(canonical, effectiveArgs);
        }

        private static OperationKind ClassifyCanonicalTool(string toolName, JObject args)
        {
            if (ModeDependentTools.Contains(toolName))
            {
                if (string.Equals(toolName, "genexus_sdk_probe", StringComparison.OrdinalIgnoreCase))
                {
                    string? mode = args["mode"]?.ToString();
                    if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "surface", StringComparison.OrdinalIgnoreCase))
                        return OperationKind.Mutating; // surface writes the diagnostic dump
                    if (string.Equals(mode, "capabilities", StringComparison.OrdinalIgnoreCase))
                        return OperationKind.ReadOnly;
                    return OperationKind.Unknown;
                }

                if (string.Equals(toolName, "genexus_run_object", StringComparison.OrdinalIgnoreCase))
                {
                    return args["dryRun"]?.ToObject<bool?>() == true
                        ? OperationKind.ReadOnly
                        : OperationKind.Mutating; // normal mode may perform GAM login/network I/O
                }

                if (string.Equals(toolName, "genexus_merge", StringComparison.OrdinalIgnoreCase))
                {
                    return args["dryRun"]?.ToObject<bool?>() == false
                        ? OperationKind.Mutating
                        : OperationKind.ReadOnly; // the published default is dryRun=true
                }
            }

            string? action = args["action"]?.ToString();
            var actionKind = ClassifyAction(toolName, action);
            if (actionKind != OperationKind.Unknown) return actionKind;

            if (PureReadOnlyTools.Contains(toolName)) return OperationKind.ReadOnly;
            if (KnownMutatingTools.Contains(toolName) || NameOnlyMutatingTools.Contains(toolName))
                return OperationKind.Mutating;

            return OperationKind.Unknown;
        }

        public static bool IsReadOnly(string? toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            var effectiveArgs = NormalizeArguments(toolName, args, out var canonical);
            var toolKind = ClassifyCanonicalTool(canonical, effectiveArgs);
            if (toolKind == OperationKind.ReadOnly
                && !IsActionMutationPreview(canonical, effectiveArgs))
                return !HasKnownSideEffects(canonical, effectiveArgs["action"]?.ToString(), effectiveArgs);

            if (toolKind != OperationKind.Mutating) return false;
            return IsActionMutationPreview(canonical, effectiveArgs);
        }

        /// <summary>
        /// Returns true when the canonical contract already proves that a call
        /// is a mutation. This is deliberately narrower than the legacy cache
        /// gate: callers can keep their compatibility fallback for tools that
        /// have not been moved into the registry yet.
        /// </summary>
        internal static bool IsMutationCandidate(string? toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            var effectiveArgs = NormalizeArguments(toolName, args, out var effectiveTool);

            var kind = ClassifyCanonicalTool(effectiveTool, effectiveArgs);
            if (kind == OperationKind.Unknown && effectiveArgs["action"] != null)
            {
                var normalizedArgs = (JObject)effectiveArgs.DeepClone();
                normalizedArgs["action"] = NormalizeActionToken(
                    effectiveTool, effectiveArgs["action"]?.ToString());
                kind = ClassifyCanonicalTool(effectiveTool, normalizedArgs);
            }
            if (kind == OperationKind.Mutating)
                return !IsActionMutationPreview(effectiveTool, effectiveArgs);

            // A read-labelled browser preview can still build or write a
            // screenshot baseline. Reuse the same side-effect predicate used by
            // IsReadOnly so both consumers agree on that exception.
            return kind == OperationKind.ReadOnly
                && HasKnownSideEffects(effectiveTool, effectiveArgs["action"]?.ToString(), effectiveArgs);
        }

        internal sealed class OperationContract
        {
            public string CanonicalName { get; init; } = string.Empty;
            public OperationKind Kind { get; init; }
            public string Effects { get; init; } = "unknown";
            public string Execution { get; init; } = "unknown";
            public string Retry { get; init; } = "never";
            public string Cache { get; init; } = "never";
            public IReadOnlyList<string> Invalidation { get; init; } = Array.Empty<string>();
            public bool PreviewSupported { get; init; }
        }

        /// <summary>Single policy projection consumed by cache/retry/preview callers.</summary>
        internal static OperationContract Describe(string toolName, JObject? args)
        {
            var effectiveArgs = NormalizeArguments(toolName, args, out var canonical);
            OperationKind kind = ClassifyCanonicalTool(canonical, effectiveArgs);
            if (kind == OperationKind.ReadOnly
                && HasKnownSideEffects(canonical, effectiveArgs["action"]?.ToString(), effectiveArgs))
                kind = OperationKind.Unknown;
            if (kind == OperationKind.Mutating && IsActionMutationPreview(canonical, effectiveArgs))
                kind = OperationKind.ReadOnly;
            return new OperationContract
            {
                CanonicalName = canonical,
                Kind = kind,
                Effects = EffectsFor(canonical, kind),
                Execution = ExecutionFor(canonical, kind),
                Retry = kind == OperationKind.ReadOnly ? "safe" : kind == OperationKind.Mutating ? "operation_key" : "never",
                Cache = kind == OperationKind.ReadOnly ? "semantic" : "never",
                Invalidation = InvalidationFor(canonical, kind),
                PreviewSupported = IsActionPreviewSupported(canonical, effectiveArgs)
            };
        }

        private static bool IsActionMutationPreview(string toolName, JObject args)
        {
            string? action = args["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return false;

            bool dryRun = args["dryRun"]?.ToObject<bool?>() == true
                || (args["dryRun"] == null && IsDefaultDryRunRecordAction(toolName, action));
            return ClassifyAction(toolName, action) == OperationKind.Mutating
                && dryRun
                && DryRunCapableActions.Contains(toolName + ":" + action);
        }

        private static bool IsActionPreviewSupported(string toolName, JObject args)
        {
            string? action = args["action"]?.ToString();
            return !string.IsNullOrWhiteSpace(action)
                && DryRunCapableActions.Contains(toolName + ":" + action);
        }

        private static string EffectsFor(string toolName, OperationKind kind)
        {
            if (kind == OperationKind.Unknown) return "unknown";
            if (kind == OperationKind.ReadOnly) return "kb.read";
            if (string.Equals(toolName, "genexus_connection_recover", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase))
                return "process.write";
            if (string.Equals(toolName, "genexus_test", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "genexus_run_object", StringComparison.OrdinalIgnoreCase))
                return "external.execute";
            if (string.Equals(toolName, "genexus_sdk_probe", StringComparison.OrdinalIgnoreCase))
                return "file.write";
            return "kb.write";
        }

        private static string ExecutionFor(string toolName, OperationKind kind)
        {
            if (kind == OperationKind.Unknown) return "unknown";
            if (string.Equals(toolName, "genexus_connection_recover", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase))
                return "gateway";
            return "worker";
        }

        private static IReadOnlyList<string> InvalidationFor(string toolName, OperationKind kind)
        {
            if (kind == OperationKind.ReadOnly || kind == OperationKind.Unknown)
                return Array.Empty<string>();
            if (string.Equals(toolName, "genexus_connection_recover", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "genexus_worker_reload", StringComparison.OrdinalIgnoreCase))
                return new[] { "process", "sessions" };
            if (string.Equals(toolName, "genexus_sdk_probe", StringComparison.OrdinalIgnoreCase))
                return new[] { "files" };
            if (string.Equals(toolName, "genexus_test", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "genexus_run_object", StringComparison.OrdinalIgnoreCase))
                return new[] { "external" };
            return new[] { "kb", "dependents", "collections" };
        }

        /// <summary>
        /// Applies the published legacy rewrite table to a cloned argument bag
        /// and returns the canonical tool name used for policy/cache identity.
        /// The caller's request is never mutated.
        /// </summary>
        internal static JObject NormalizeArguments(
            string toolName, JObject? args, out string canonicalTool)
        {
            var effectiveArgs = args == null ? new JObject() : (JObject)args.DeepClone();
            string effectiveTool = toolName;
            if (McpRouter.TryRewriteLegacyTool(toolName, effectiveArgs, out var rewrittenTool, out var rewrittenArgs))
            {
                effectiveTool = rewrittenTool;
                effectiveArgs = rewrittenArgs;
            }

            canonicalTool = ToolIdentity.ResolveCanonical(effectiveTool);
            return effectiveArgs;
        }

        private static bool IsDefaultDryRunRecordAction(string toolName, string? action)
            => (string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(action, "records_insert", StringComparison.Ordinal)
                        || string.Equals(action, "records_update", StringComparison.Ordinal)))
                || (string.Equals(toolName, "genexus_api", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(action, "routes_clone", StringComparison.Ordinal)
                        || string.Equals(action, "routes_update", StringComparison.Ordinal)));

        private static string? NormalizeActionToken(string toolName, string? action)
        {
            if (string.IsNullOrWhiteSpace(action) || !ActionContracts.TryGetValue(toolName, out var contract))
                return action;

            return contract.ReadOnly.FirstOrDefault(value =>
                       string.Equals(value, action, StringComparison.OrdinalIgnoreCase))
                ?? contract.Mutating.FirstOrDefault(value =>
                       string.Equals(value, action, StringComparison.OrdinalIgnoreCase))
                ?? action;
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
