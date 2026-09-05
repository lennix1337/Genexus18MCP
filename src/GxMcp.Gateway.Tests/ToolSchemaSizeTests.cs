using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolSchemaSizeTests
    {
        private static string FindToolDefinitionsJson()
        {
            // Preferred: alongside the test output (propagated via Gateway's <Content> item).
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            if (File.Exists(beside)) return beside;

            // Fallback: walk up from base dir to repo src (for IDE test runs from src tree).
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("Could not locate tool_definitions.json from test base " + AppContext.BaseDirectory);
        }

        [Fact]
        public void TotalToolSchemaSizeIsUnderBudget()
        {
            var path = FindToolDefinitionsJson();
            Assert.True(File.Exists(path), $"tool_definitions.json not found at {path}");
            var content = File.ReadAllText(path);
            var approxTokens = content.Length / 4;
            // This budget guards the combined size of every tool schema in
            // tool_definitions.json (approximated as content.Length / 4 tokens). MCP
            // clients pay this cost on every session's tool list, so growth here is
            // deliberate — bump the constant only alongside a schema change that needs
            // the extra room, and check headroom before adding a new field to an
            // existing tool. Full bump-by-bump history lives in CHANGELOG.md; only the
            // last few entries are kept here for quick context:
            //   2026-07-09 (genexus_merge spike): 11750 → 12200 for the new
            //   genexus_merge tool (IMergeService object-merge, WRITE +
            //   destructiveHint=true). Measured ~12084 tokens; ~116 headroom.
            //   2026-07-09 (SDK-coverage batch integration): 12200 → 13300 for
            //   genexus_kb_version, genexus_module, genexus_gam, plus genexus_gxserver
            //   write actions landed together. Measured ~13150 tokens; ~150 headroom.
            //   2026-07-10 (issue #28 authoring papercuts): 13300 → 13600 for
            //   genexus_variable length/decimals/collection params + genexus_create
            //   firstItem/firstItemType SDT-seed params. Measured ~13378 tokens;
            //   ~222 headroom.
            //   2026-07-14 (issue #32): 13600 → 14100 for genexus_variable batch
            //   `variables[]` add + typeName/VarChar docs and genexus_gxserver commit
            //   `targets[]` (partial commit). Measured ~13856 tokens; ~244 headroom.
            //   2026-07-15 (per-KB memory): 14100 → 14550 for the new genexus_memory
            //   tool (save/recall/list/forget per-KB fact store). Measured ~14333
            //   tokens; ~217 headroom.
            //   2026-07-15 (per-KB memory, Phase 3): added consolidate/promote actions
            //   + message/dryRun params to genexus_memory. Measured ~14469 tokens;
            //   ~81 headroom — still under the 14550 budget, no bump needed.
            //   2026-07-20 (compile_check): 14550 → 14750 for the genexus_lifecycle
            //   `mode` param (compile_check) + discoverability copy in the tool
            //   description and an example. Measured ~14693 tokens; ~57 headroom.
            //   2026-07-20 (issue #39 create_index): 14750 → 14900 for the
            //   genexus_structure `create_index` action (enum value + payload docs +
            //   example) — the GeneXus-parity way to enforce attribute uniqueness.
            //   Measured ~14823 tokens; ~77 headroom.
            //   2026-07-20 (issue #39 data-model batch): 14900 → 15100 for the
            //   genexus_structure drop_index / set_attribute / set_level / set_domain
            //   actions (enum values + expanded payload docs + examples). Measured
            //   ~15008 tokens; ~92 headroom.
            //   2026-07-20 (issue #39 batch 2 authoring): 15100 → 15400 for the new
            //   genexus_authoring tool (add_external_method / add_external_property /
            //   add_menu_option). Measured ~15300 tokens; ~100 headroom.
            //   2026-07-20 (issue #39 batch 3): 15400 → 15600 for genexus_authoring
            //   add_condition (Data Selector filter). (add_theme_color was prototyped
            //   then dropped — classic Theme colors are a virtual-part projection,
            //   IDE-only, like SDPanel parts.) Measured ~15330 tokens; ~270 headroom.
            //   2026-07-20 (SDK-endpoints P0/P1 batch): 15600 → 16200 for the new
            //   genexus_transfer (XPZ export/import) + genexus_deploy tools, plus
            //   genexus_analyze mode=kb_stats, genexus_db action=reorg_impact,
            //   genexus_security action=scan_native, and genexus_gxserver
            //   pipeline_* actions/params. Measured ~16049 tokens; ~151 headroom.
            //   2026-07-20 (reliability batch): 16200 → 16400 for genexus_lifecycle's
            //   new compile_check `callers`/`callerCap` (target-only scoping) and build
            //   `deploy` (full deploy → runnable output) params. Measured ~16254; ~146 headroom.
            //   2026-07-24 (issue #50): 16400 → 16600 for genexus_create's new
            //   folder/module/parentPath destination args (rejected with
            //   FolderPlacementUnsupported). Measured ~16489; ~111 headroom.
            //   2026-07-24 (issue #50 rework — real move): 16600 → 16700 for
            //   genexus_properties action=move (destination/destKind/dryRun params)
            //   + reworked genexus_create folder/module copy (now creates-then-moves
            //   instead of rejecting). Measured ~16629; ~71 headroom.
            //   2026-07-24 (issue #52): 16700 → 16900 for genexus_structure update_visual
            //   2026-07-31 (issues #58-#62): 16900 → 17700 for WorkWithPlus actions,
            //   atomic authoring, validated edit persistence, and reorg-preview schemas.
            //   SDT support (description + payload docs for isCollection/collectionItemName/
            //   basedOnDomain + an SDT example). Measured ~16760; ~140 headroom.
            //   2026-07-31 (issues #59/#60): 16900 → 17700 for the validationMode=
            //   "specify"/rollbackOnFailure params added to genexus_edit,
            //   genexus_variable, genexus_properties, genexus_structure and
            //   genexus_create. Measured ~17572; ~128 headroom.
            //   2026-07-31 (issue #62): 17700 → 18200 for the genexus_create
            //   action=object_atomic params (mode/variables/rules/parms/source/
            //   properties/expectedVersion/validate) + the new action enum value and
            //   description copy. Measured ~18077; ~123 headroom.
            //   2026-07-31 (issue #58): 18200 → 19000 for the new genexus_wwp tool
            //   (WorkWithPlus action groups: list/add_action/update_action/
            //   move_action/remove_action + group/position/confirm/selection/
            //   enabledWhen/procedure/dryRun params). Measured ~18938; ~62 headroom.
            //   2026-08-01 (search-source continuation): 19000 → 19100 for the
            //   opaque cursor parameter that resumes pages ending inside an object.
            //   Measured ~19002; ~98 headroom.
            //   2026-08-01 (merged atomic-authoring contracts): 19100 → 19500 for
            //   the compatible mode/baseVersion and expectedVersion/updateExisting
            //   aliases plus native Domain-binding guidance. Measured ~19350; ~150
            //   headroom.
            //   2026-08-12 (move_attribute): 19500 → 19800 for native Transaction
            //   attribute reordering with before/after/position, nested level paths,
            //   dry-run, module lookup and baseVersion. Measured ~19637; ~163 headroom.
            //   2026-08-13 (create_index dry-run safety): 19800 → 20000 for the
            //   get_indexes versionToken/baseVersion contract, projected diff,
            //   exact post-save verification and rollback semantics. Measured ~19875.
            //   2026-08-16 (issue #97): 20000 → 20300 for genexus_structure
            //   remove_attribute / check_subtypes actions — native TransactionLevel.Items
            //   attribute removal (lets agents drop + re-add a misclassified subtype
            //   attribute) and the subtype-classification guard-rail. Measured ~20158;
            //   ~142 headroom.
            //   2026-08-16 (Items A, B, D): 20300 → 20500 for genexus_edit autoDeclareVariables,
            //   genexus_refactor ExtractSubroutine, and genexus_transfer includeDependencies.
            //   Measured ~20313; ~187 headroom.
            //   2026-08-18 (native Data View authoring): 20500 → 21100 for
            //   genexus_data_view inspect/dry_run/create/update/delete, typed
            //   mappings, optimistic concurrency and atomic rollback. Measured
            //   ~20932; ~168 headroom.
            //   2026-08-20 (typed generator references): 21100 → 21700 for
            //   genexus_generator_reference list/add/remove, dry-run, optimistic
            //   concurrency and exact rollback. Measured ~21563; ~137 headroom.
            //   2026-08-20 (batch properties & multi-part edit): 21700 → 21900 for
            //   genexus_edit parts array and genexus_properties properties map
            //   enabling single-pass atomic persistence. Measured ~21744; ~156 headroom.
            //   2026-08-26 (literal line-break write guard): 21900 → 22200 for
            //   the explicit real-line-break contract on full/patch/atomic text writes.
            //   Measured ~22059; ~141 headroom.
            //   2026-09-02 (native API routes): 22200 → 22500 for typed
            //   API method inspection, clone/update preview and rollback fields.
            //   Measured ~22380; ~120 headroom.
            //   2026-09-04 (modular object identity): 22500 → 23000 for native
            //   GUID, EntityKey, and qualified path resolution across genexus_read,
            //   genexus_inspect, and genexus_search_source. Measured ~22782; ~218 headroom.
            //   2026-09-04 (Issue #131 multi-action completeness & descriptions): 23000 → 24500
            //   for undeclared lifecycle router parameters (part, page, pageSize, notifyOnFailure,
            //   skipFullDeploy, fastIncremental), structure type parameter, recipe steps array,
            //   and comprehensive property descriptions across layout, edit_form, refactor,
            //   apply_pattern, versioning, create, security, and structure. Measured ~24200; ~300 headroom.
            //   2026-09-04 (Issues #139/#140 action contract parity): 24500 → 25000
            //   for explicit descriptions on all 31 action-bearing tool properties and
            //   their machine-checked action inventory. Measured ~24666; ~334 headroom.
            //   2026-09-04 (PR #133 typed Transaction records): 25000 → 25500 for
            //   records_query/records_insert/records_update schemas and their safety
            //   contract. Measured ~25165; ~335 headroom.
            Assert.True(approxTokens < 25500, $"tool_definitions.json is ~{approxTokens} tokens; budget 25500.");
        }
    }
}
