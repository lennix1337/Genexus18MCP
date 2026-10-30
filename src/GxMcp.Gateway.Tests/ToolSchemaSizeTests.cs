using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
            // existing tool. Full bump-by-bump history lives in the changelog; only the
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
            //   2026-09-05 (v3 change sets): 25500 → 25600 for the explicit
            //   genexus_edit changeSet preview/validate/apply contract.
            //   2026-09-05 (durable operation recovery): 25600 → 25850 for
            //   lifecycle inspect/reconcile fields that expose redacted journal
            //   state without replaying writes. Measured ~25763 tokens.
            //   2026-09-06 (lifecycle structured output): 25850 → 26000 for the
            //   additive outputSchema contract; text content remains compatible.
            //   2026-09-07 (v2.43 compatibility): 26000 → 26300 for typed WWP
            //   tab/grid operations and the API versionToken alias. Measured ~26079.
            //   2026-09-09 (issue #146 decouple registration): 26300 → 26500 for
            //   genexus_kb select/set_session_default actions and persist flag. Measured ~26327.
            //   2026-09-10 (issue #146 inventory parity): 26500 → 27500 for the four
            //   gateway-only RequestLoop tools: kb_diff, kb_import, sandbox, worker_pool.
            //   The current schema also includes the independently landed Object Text
            //   batch contracts from main; measured ~27279.
            //   2026-09-10 (Pattern Settings): 27500 → 27750 for three Settings
            //   actions, explicit identity, pagination and single-property preview fields.
            //   Measured ~27549; real Settings saves remain blocked.
            //   2026-09-10 (WWP native structural replacement): 27750 → 28250 for
            //   the explicit WebComponent-to-DropDownComponent UserAction contract,
            //   including target identity, preserved Gxobject, trigger/load metadata,
            //   and the dry-run example. Measured ~27980.
            //   2026-09-14 (native WWP table type): 28250 → 28600 for the typed
            //   set_table_type operation, path identity, preservation guarantees,
            //   and dry-run example. Measured ~28385.
            //   2026-09-15 (issues #205/#206/#209 protections): 28600 → 29400 for the
            //   genexus_edit patch={find,replace} object schema (scope anchors and
            //   indentation validation, including their error codes and line evidence),
            //   the top-level scope/indentation aliases the router consumes, and
            //   genexus_lifecycle's freshness wait target. Measured ~29147.
            //   2026-09-16 (KB creation): 29400 → 29800 for genexus_kb create action,
            //   parameters (dbServer, dbName, dbUser, dbPassword, template, sdkPath,
            //   major, openAfterCreate, dryRun), and examples. Measured ~29502.
            //   2026-09-17 (SDK text/mirror/module tasks): 29800 → 30600 for the
            //   native src/ref exchange, in-memory list/validation, watermark mirror,
            //   Module Manager server/package actions, and OpenAPI task publication.
            //   2026-09-17 (filesystem parity): 30600 → 31000 for recursive selectors,
            //   listOnly/skip/stopOnError, forceSave, module.toml metadata and manifest
            //   hash/file-reference validation. Measured ~30905 tokens.
            //   2026-09-17 (typed WWP form UserAction): 31000 → 31500 for the
            //   add_user_action operation, container/event contract, and rollback option.
            //   Measured ~31229 tokens.
            // 31500 -> 31700 for explicit journal status/repair actions and preview contract.
            //   2026-09-22 (issue #274 module install preview): 31700 → 31800 for
            //   the genexus_module dryRun preview param on install/install_builtin/update
            //   plus discoverability copy in the tool description. Measured ~31735 tokens.
            //   2026-09-24 (issues #268, #306, #308, #309, #310, #313, #314, #315, #316): 32000 → 33500 for typed WWP grid, lifecycle continuation, semantic
            //   WebForm safety, report-control, K2B/structural variable, and pagination
            //   contracts. Measured ~33055 tokens.
            //   2026-09-30 (issues #356/#357): 33500 → 34000 for genexus_read's
            //   conditional-read `ifUnchangedSince` and genexus_query's bounded
            //   multi-KB `kbs`/`cursors`/`maxConcurrency`/`maxTotalResults`/
            //   `perKbTimeoutMs`. Both descriptions were trimmed while being added;
            //   what remains is the honest cost of telling a caller which KBs were
            //   actually searched, instead of letting an unsearched KB read as a KB
            //   with no matches. Measured ~33890 tokens.
            //   2026-10-01 (issue #354): 34000 → 34500 for genexus_kb_diff's
            //   `mode`/`maxObjects` params and the inventory-authority copy. The
            //   alternative was keeping the budget and shipping a comparator that
            //   can report an unqualified Success over an inventory it could not
            //   read, which is the false confidence the issue is about. Measured
            //   ~34135 tokens; ~365 headroom.
            //   2026-10-05 (issues #420-#427): 34500 -> 36000 for three new read-only tools
            //   (genexus_dfd, genexus_impact, genexus_object_context: each needs its own
            //   selector/limit schema), genexus_wwp add_grid / list_tabs / tab_schema and the
            //   add_user_action callObject/popup/parameters inputs, and genexus_security
            //   audit_object. The alternative was leaving those capabilities to free-text
            //   arguments that clients cannot validate. Measured ~35685 tokens; ~315 headroom.
            // PRs #320/#443-#446 add an opt-in K2B tool and typed concurrency/SDT/WWP/EXO contracts.
            // Combined schema measures ~36759 tokens; existing guidance remains available.
            // PR #459 introduces GxWire native architectural discovery tool (genexus_wire). Measured ~37302 tokens.
            // Issue #455 introduces standard WorkWith tool (genexus_workwith). Measured ~38041 tokens.
            // Issues #541, #551, #552, #559: 38500 -> 39000 for genexus_workwith filter authoring,
            // genexus_create attribute typing (dataType/length/decimals/basedOn), and genexus_wwp
            // tabs/userAction layout parameter enhancements. Measured ~38783 tokens; ~217 headroom.
            // Unreleased 4.0 work (#586/#587/#588/#595 workwith/wwp inputs, genexus_wire targets,
            // genexus_wwp set_combo_properties) had pushed this to ~39895 tokens through five budget
            // bumps (39000 -> 40000). The review before 4.0 trimmed redundant description text and
            // restated tool summaries as one purpose sentence (no schema-shape change), reaching
            // ~39621 tokens. The v3.16.0 budget of 39000 needs ~2500 more characters out of
            // descriptions whose remaining text is not duplicated elsewhere in the schema, so the
            // budget stays at 39700 (~79 headroom) instead of cutting guidance clients rely on.
            // Issue #689 + #638: 39700 -> 39800 for genexus_kb's `kb`/`alias` descriptions naming the five pool
            // actions that take `kb` in place of `alias` (close, select, set_session_default, set_persistent_default,
            // set_default) and genexus_wwp's `gridPath` taking the grid's name. The schema is what the caller reads to
            // build the call, so a description that omits the actions is the defect. Measured ~39745 tokens; ~55 headroom.
            // Issue #710: 39800 -> 39850 for genexus_versioning history_diff (read-only comparison of native
            // revisions: the action enum value and the `toVersionId` input; the versionId/async/context
            // descriptions were shortened to offset part of it). Measured ~39813 tokens; ~37 headroom.
            // The typed genexus_wwp add_layout (the action value plus one description sentence) takes the schema from ~39844 to ~39879 tokens, so 39850 -> 39900.
            Assert.True(approxTokens < 39900, $"tool_definitions.json is ~{approxTokens} tokens; budget 39900.");
        }

        [Theory]
        // 82000 -> 82500 for the same issue #356/#357 additions as the token budget
        // above (genexus_read.ifUnchangedSince; genexus_query kbs/cursors/
        // maxConcurrency/maxTotalResults/perKbTimeoutMs). Measured 82032 bytes.
        //
        // 82500 -> 83000 for issue #366 (genexus_analyze.maxCallers/maxSourceBytes plus
        // the mode=callers half of `cursor`). Without the two budget arguments the
        // documented resume hint on `cursor` is unreachable, so this is the schema cost of
        // making caller paging usable rather than new surface. Measured 82572 bytes.
        //
        // 83000 -> 86500 for issues #420-#427 (see the token budget above: three read-only
        // tools, the genexus_wwp add_grid/list_tabs/tab_schema/callObject inputs and
        // genexus_security audit_object). Measured 86107 bytes.
        // 86500 -> 86700 for property timeout reconciliation and optimistic set fields.
        // Combined K2B/SDT/WWP/EXO/property contracts: all=88148, authoring=61238 bytes.
        // all 89000 -> 98500, core 25000 -> 25500, authoring 62500 -> 67500, devops
        // 50000 -> 51000: each published tool description is a bounded purpose summary
        // (ToolProfileFilter.MaxToolSummaryLength) instead of only the tool-help URI,
        // because clients choose a tool from that text before reading any resource.
        // With genexus_wire added to core/standard/authoring/devops/all and purpose summaries from origin/main:
        // Measured all ~99400, core=26678, standard ~60500, authoring ~68000, devops=52066 bytes.
        // Issue #455 adds genexus_workwith: all measured 100907, authoring measured 69808.
        // Issue #588 adds the genexus_workwith `default` filter input: authoring 70500 -> 71000
        // (measured 70527); the other profiles stayed within their budgets.
        // Issue #586 adds genexus_workwith remove_filter_attribute (enum, removeConditions,
        // example): authoring 70500 -> 71000, measured 70509 bytes.
        // Issue #595 adds genexus_wwp add_table/move_control inputs: all 102000 -> 103000,
        // authoring 70500 -> 71500, ui 50000 -> 50500 (ui measured 50001).
        // Measured together (#586/#588/#595): all 102473, authoring 71374 bytes.
        // all 103000 -> 103100 for the `expressions` array on genexus_properties (typed parameter list
        // of a Dynamic Combo Box). Measured 103015 bytes; the global token budget does not move.
        // genexus_wire.targets (#499 follow-up, pack_task on a native KB): ~100 bytes per profile;
        // all 103100 -> 103200 (measured 103121), devops 52500 -> 52700 (measured 52598).
        // The unreleased 4.0 work had raised these through six bumps (all 103300, authoring 72000,
        // ui 50500, devops 52700). The pre-4.0 review trimmed redundant description text and
        // restated tool summaries as one purpose sentence (published summaries are sentence-bounded,
        // ToolProfileFilter.MaxToolSummaryLength) without changing any schema shape. ui (49982) and
        // devops (52379) are back under their v3.16.0 budgets; all (102618) and authoring (71074)
        // stay ~618 / ~574 bytes above the v3.16.0 budgets (102000 / 70500): the remaining excess is
        // the property names, types, enums and 40-character published descriptions of the new
        // genexus_wwp/genexus_workwith/genexus_wire inputs, which cannot shrink without changing
        // the schema. Headroom over the measured size is ~132 / ~126 bytes.
        // genexus_wwp regenerate_derived (the action enum value plus the `scope` and `expectedVersions`
        // inputs): all 102750 -> 103000, authoring 71200 -> 71500, ui 50000 -> 50300. Measured all 102844,
        // authoring 71300, ui 50208 bytes; the inputs are the property names, types and the scope enum, which
        // the 40-character published descriptions do not shrink.
        // genexus_query.fields (#637: clients that did not see the array sent it as text): ui 50000 -> 50200
        // (measured 50098), the only profile that crossed its budget; the token budget above does not move.
        // Both landed together with the #637-#662 fixes (whose new descriptions were trimmed to keep the
        // token budget): ui measured 50316 bytes, so ui 50300 -> 50400.
        // Issue #710 adds genexus_versioning history_diff (the action enum value and the `toVersionId`
        // input; the other descriptions were shortened to compensate): all 103000 -> 103100 (measured
        // 103030), devops 52500 -> 52600 (measured 52577).
        // Issue #729 adds genexus_create `imagePath`/`imageUrl` (an Image cannot be saved without an
        // image). Profiles cut every description to 40 chars, so shortening firstItem/firstItemType only
        // offsets the full-schema token budget: all 103100 -> 103250 (measured 103191), authoring
        // 71500 -> 71650 (measured 71562).
        [InlineData("all", 103250)]
        [InlineData("core", 27000)]
        [InlineData("standard", 61000)]
        [InlineData("authoring", 71650)]
        [InlineData("devops", 52600)]
        [InlineData("ui", 50400)]
        [InlineData("db", 40000)]
        public void PublishedToolProfileStaysWithinItsByteBudget(string profile, int maxBytes)
        {
            var tools = JArray.Parse(File.ReadAllText(FindToolDefinitionsJson()));
            var filtered = ToolProfileFilter.Filter(tools, profile);
            int bytes = Encoding.UTF8.GetByteCount(filtered.ToString(Formatting.None));

            Assert.True(bytes <= maxBytes, $"Tool profile '{profile}' is {bytes} bytes; budget {maxBytes}.");
        }

        [Fact]
        public void NoPublishedToolSchemaExceedsItsByteBudget()
        {
            var tools = JArray.Parse(File.ReadAllText(FindToolDefinitionsJson()));
            var publishedTools = ToolProfileFilter.Filter(tools, "all");

            foreach (JObject tool in publishedTools)
            {
                int bytes = Encoding.UTF8.GetByteCount(tool.ToString(Formatting.None));
                Assert.True(bytes <= 8000, $"Tool '{tool["name"]}' is {bytes} bytes; per-tool budget 8000.");
            }
        }
    }
}
