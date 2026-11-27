using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Authoritative per-action effect metadata for action-bearing MCP tools (issue #1113).
    /// Published additively on each multi-action tool in <c>tools/list</c> under
    /// <c>_meta["gxmcp/actionEffects"]</c> so MCP clients can derive call gating from the
    /// server instead of keeping a manual copy.
    ///
    /// Base read/write classification and dry-run preview eligibility are derived from
    /// <see cref="OperationClassifier"/>; higher-severity dimensions (<c>expensive</c>,
    /// <c>destructive</c>, <c>external</c>) and parameter-driven effect modifiers are declared
    /// in one table and cross-checked against <see cref="Program.DescribeDestructiveOperation"/>.
    /// </summary>
    internal static class ActionEffectCatalog
    {
        internal const string MetaExtensionKey = "gxmcp/actionEffects";
        internal const int SchemaVersion = 1;

        internal const string Read = "read";
        internal const string Write = "write";
        internal const string Expensive = "expensive";
        internal const string Destructive = "destructive";
        internal const string External = "external";

        // Canonical tag order emitted in published metadata.
        private static readonly string[] CanonicalOrder = { Read, Write, Expensive, Destructive, External };

        // Actions that are long-running or resource-intensive.
        private static readonly HashSet<string> ExpensiveActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "genexus_browser:smoke",
            "genexus_browser:cross",
            "genexus_db:reorg_impact",
            "genexus_db:reorg_preview",
            "genexus_db:sample_data",
            "genexus_deploy:deploy",
            "genexus_doc:wiki",
            "genexus_doc:visualize",
            "genexus_gxserver:update",
            "genexus_io:ocr",
            "genexus_io:export_kb_to_text",
            "genexus_io:import_text_to_kb",
            "genexus_io:text_mirror_catchup",
            "genexus_io:export_unified",
            "genexus_kb:create",
            "genexus_kb_version:freeze",
            "genexus_kb_version:branch",
            "genexus_lifecycle:build",
            "genexus_lifecycle:build_all",
            "genexus_lifecycle:specify",
            "genexus_lifecycle:rebuild",
            "genexus_lifecycle:reorg",
            "genexus_lifecycle:reorg_preview",
            "genexus_lifecycle:validate-kb",
            "genexus_module:install",
            "genexus_module:install_builtin",
            "genexus_module:update",
            "genexus_module:publish",
            "genexus_module:restore",
            "genexus_refactor:RenameAttribute",
            "genexus_refactor:RenameVariable",
            "genexus_refactor:RenameObject",
            "genexus_sandbox:create",
            "genexus_security:scan_secrets",
            "genexus_security:scan_native",
            "genexus_transfer:export",
            "genexus_transfer:import",
            "genexus_wire:index",
            "genexus_worker_pool:warm_spares",
            "genexus_wwp:regenerate_derived"
        };

        // Irreversible deletions, overwrites, schema changes, resets, or elicitation-gated calls.
        private static readonly HashSet<string> DestructiveActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "genexus_apply_pattern:remove_action",
            "genexus_data_view:delete",
            "genexus_db:records_update",
            "genexus_deploy:deploy",
            "genexus_edit_form:remove_control",
            "genexus_gam:deploy",
            "genexus_generator_reference:remove",
            "genexus_gxserver:update",
            "genexus_gxserver:resolve",
            "genexus_gxserver:pipeline_abort",
            "genexus_io:import_part",
            "genexus_io:import_text_to_kb",
            "genexus_io:delete_kb_objects",
            "genexus_k2b_designer:remove_node",
            "genexus_kb_version:revert",
            "genexus_layout:delete_printblock",
            "genexus_layout:remove_report_control",
            "genexus_lifecycle:build_all",
            "genexus_lifecycle:rebuild",
            "genexus_lifecycle:reorg",
            "genexus_lifecycle:snapshots-restore",
            "genexus_memory:forget",
            "genexus_module:publish",
            "genexus_refactor:RenameAttribute",
            "genexus_refactor:RenameVariable",
            "genexus_refactor:RenameObject",
            "genexus_sandbox:remove",
            "genexus_structure:drop_index",
            "genexus_structure:remove_attribute",
            "genexus_transfer:import",
            "genexus_variable:delete",
            "genexus_versioning:history_restore",
            "genexus_versioning:undo",
            "genexus_workwith:remove_action",
            "genexus_workwith:remove_filter_attribute",
            "genexus_wwp:remove_action",
            "genexus_wwp:remove_tab",
            "genexus_wwp:replace_web_component_with_user_action"
        };

        // Actions that leave the local machine (remote servers, deploy, browser HTTP, SDK build hooks).
        private static readonly HashSet<string> ExternalActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "genexus_browser:smoke",
            "genexus_browser:a11y",
            "genexus_browser:wcag",
            "genexus_browser:capture",
            "genexus_browser:cross",
            "genexus_browser:preview",
            "genexus_deploy:deploy",
            "genexus_gxserver:status",
            "genexus_gxserver:conflicts",
            "genexus_gxserver:history",
            "genexus_gxserver:commit",
            "genexus_gxserver:update",
            "genexus_gxserver:lock",
            "genexus_gxserver:resolve",
            "genexus_gxserver:pipeline_list",
            "genexus_gxserver:pipeline_runs",
            "genexus_gxserver:pipeline_output",
            "genexus_gxserver:pipeline_run",
            "genexus_gxserver:pipeline_abort",
            "genexus_lifecycle:build_all",
            "genexus_lifecycle:rebuild",
            "genexus_module:install",
            "genexus_module:update",
            "genexus_module:publish",
            "genexus_module:restore",
            "genexus_module:search_modules_in_servers"
        };

        // Mutating actions whose published dryRun defaults to true (only explicit dryRun=false writes).
        private static readonly HashSet<string> DefaultDryRunTrueActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "genexus_api:routes_clone",
            "genexus_api:routes_update",
            "genexus_connection_recover:journal_repair",
            "genexus_connection_recover:recovery_read_complete",
            "genexus_db:records_insert",
            "genexus_db:records_update",
            "genexus_transfer:import"
        };

        // Additional explicit dryRun=true preview actions published in tool_definitions.json
        // beyond OperationClassifier.PreviewCapableActions.
        private static readonly HashSet<string> ExtraDryRunPreviewActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "genexus_io:delete_kb_objects",
            "genexus_io:import_text_to_kb",
            "genexus_kb:create"
        };

        private sealed class EffectModifier
        {
            public required string Param { get; init; }
            public required JToken When { get; init; }
            public string[]? Actions { get; init; }
            public string[]? Set { get; init; }
            public string[]? Add { get; init; }
        }

        // Parameter-driven effect transitions beyond standard dryRun previews.
        private static readonly Dictionary<string, EffectModifier[]> CustomModifiers =
            new Dictionary<string, EffectModifier[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["genexus_browser"] = new[]
                {
                    new EffectModifier
                    {
                        Param = "buildFirst",
                        When = new JValue(true),
                        Actions = new[] { "preview" },
                        Add = new[] { Write, Expensive }
                    },
                    new EffectModifier
                    {
                        Param = "updateBaseline",
                        When = new JValue(true),
                        Actions = new[] { "preview" },
                        Add = new[] { Write }
                    },
                    new EffectModifier
                    {
                        Param = "capture",
                        When = new JObject { ["contains"] = "screenshot" },
                        Actions = new[] { "preview" },
                        Add = new[] { Write }
                    }
                },
                ["genexus_create"] = new[]
                {
                    new EffectModifier
                    {
                        Param = "overwrite",
                        When = new JValue(true),
                        Actions = new[] { "save_as" },
                        Add = new[] { Destructive }
                    },
                    new EffectModifier
                    {
                        Param = "updateExisting",
                        When = new JValue(true),
                        Actions = new[] { "object_atomic" },
                        Add = new[] { Destructive }
                    }
                },
                ["genexus_gam"] = new[]
                {
                    new EffectModifier
                    {
                        Param = "rebuild",
                        When = new JValue(true),
                        Actions = new[] { "deploy" },
                        Add = new[] { Expensive }
                    }
                },
                ["genexus_gxserver"] = new[]
                {
                    new EffectModifier
                    {
                        Param = "rebuild",
                        When = new JValue(true),
                        Actions = new[] { "pipeline_run" },
                        Add = new[] { Expensive }
                    }
                },
                ["genexus_io"] = new[]
                {
                    new EffectModifier
                    {
                        Param = "listOnly",
                        When = new JValue(true),
                        Actions = new[] { "export_kb_to_text", "import_text_to_kb", "delete_kb_objects" },
                        Set = new[] { Read }
                    }
                },
                ["genexus_lifecycle"] = new[]
                {
                    new EffectModifier
                    {
                        Param = "deploy",
                        When = new JValue(true),
                        Actions = new[] { "build" },
                        Add = new[] { External }
                    },
                    new EffectModifier
                    {
                        Param = "notifyOnFailure",
                        When = new JValue("present"),
                        Actions = new[] { "build" },
                        Add = new[] { External }
                    },
                    new EffectModifier
                    {
                        Param = "force",
                        When = new JValue(true),
                        Actions = new[] { "index" },
                        Add = new[] { Expensive }
                    }
                },
                ["genexus_module"] = new[]
                {
                    new EffectModifier
                    {
                        Param = "rebuild",
                        When = new JValue(true),
                        Actions = new[] { "package" },
                        Add = new[] { Expensive }
                    }
                },
                ["genexus_navigation"] = new[]
                {
                    new EffectModifier
                    {
                        Param = "latest",
                        When = new JValue(true),
                        Actions = new[] { "view" },
                        Set = new[] { Read }
                    }
                }
            };

        /// <summary>
        /// Builds the additive <c>_meta["gxmcp/actionEffects"]</c> payload for a published
        /// action-bearing tool definition, or returns <c>null</c> for actionless tools.
        /// </summary>
        internal static JObject? BuildForTool(JObject toolDefinition)
        {
            string? toolName = toolDefinition["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(toolName)) return null;

            var actionEnum = toolDefinition["inputSchema"]?["properties"]?["action"]?["enum"] as JArray;
            if (actionEnum == null || actionEnum.Count == 0) return null;

            var enumActions = actionEnum.Select(token => token.ToString()).ToList();
            var actionsObj = new JObject();
            var mutatingActions = new List<string>();
            bool anyReadHasExtraEffect = false;

            foreach (string action in enumActions)
            {
                var effects = BaseEffectsFor(toolName!, action);
                actionsObj[action] = new JArray(effects);
                if (effects.Contains(Write))
                    mutatingActions.Add(action);
                else if (effects.Count > 1)
                    anyReadHasExtraEffect = true;
            }

            var modifiers = BuildModifiers(toolName!, enumActions, mutatingActions, anyReadHasExtraEffect);
            var payload = new JObject
            {
                ["version"] = SchemaVersion,
                ["actions"] = actionsObj
            };
            if (modifiers.Count > 0)
            {
                payload["modifiers"] = modifiers;
            }

            return payload;
        }

        private static List<string> BaseEffectsFor(string toolName, string action)
        {
            string key = toolName + ":" + action;
            var kind = OperationClassifier.ClassifyAction(toolName, action);
            var tags = new HashSet<string>(StringComparer.Ordinal)
            {
                kind == OperationClassifier.OperationKind.ReadOnly ? Read : Write
            };

            if (ExpensiveActions.Contains(key)) tags.Add(Expensive);
            if (DestructiveActions.Contains(key))
            {
                tags.Remove(Read);
                tags.Add(Write);
                tags.Add(Destructive);
            }
            if (ExternalActions.Contains(key)) tags.Add(External);

            var ordered = new List<string>(tags.Count);
            foreach (string candidate in CanonicalOrder)
            {
                if (tags.Contains(candidate)) ordered.Add(candidate);
            }
            return ordered;
        }

        private static JArray BuildModifiers(
            string toolName,
            List<string> enumActions,
            List<string> mutatingActions,
            bool anyReadHasExtraEffect)
        {
            var result = new JArray();

            var defaultTrueDryRun = new List<string>();
            var explicitTrueDryRun = new List<string>();

            foreach (string action in mutatingActions)
            {
                string key = toolName + ":" + action;
                if (DefaultDryRunTrueActions.Contains(key))
                {
                    defaultTrueDryRun.Add(action);
                }
                else if (OperationClassifier.PreviewCapableActions.Contains(key)
                    || ExtraDryRunPreviewActions.Contains(key))
                {
                    explicitTrueDryRun.Add(action);
                }
            }

            if (defaultTrueDryRun.Count > 0)
            {
                result.Add(SerializeModifier(new EffectModifier
                {
                    Param = "dryRun",
                    When = new JValue("notFalse"),
                    Actions = defaultTrueDryRun.ToArray(),
                    Set = new[] { Read }
                }, enumActions));
            }

            if (explicitTrueDryRun.Count > 0)
            {
                bool appliesToAllWithoutLoss = !anyReadHasExtraEffect
                    && defaultTrueDryRun.Count == 0
                    && explicitTrueDryRun.Count == mutatingActions.Count;
                result.Add(SerializeModifier(new EffectModifier
                {
                    Param = "dryRun",
                    When = new JValue(true),
                    Actions = appliesToAllWithoutLoss ? null : explicitTrueDryRun.ToArray(),
                    Set = new[] { Read }
                }, enumActions));
            }

            if (CustomModifiers.TryGetValue(toolName, out var custom))
            {
                foreach (var modifier in custom)
                {
                    result.Add(SerializeModifier(modifier, enumActions));
                }
            }

            return result;
        }

        private static JObject SerializeModifier(EffectModifier modifier, List<string> enumActions)
        {
            var obj = new JObject
            {
                ["param"] = modifier.Param,
                ["when"] = modifier.When.DeepClone()
            };
            if (modifier.Actions != null && modifier.Actions.Length > 0 && modifier.Actions.Length < enumActions.Count)
            {
                obj["actions"] = new JArray(modifier.Actions);
            }
            if (modifier.Set != null)
            {
                obj["set"] = new JArray(modifier.Set);
            }
            else if (modifier.Add != null)
            {
                obj["add"] = new JArray(modifier.Add);
            }
            return obj;
        }
    }
}
