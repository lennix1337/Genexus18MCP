# GeneXus MCP Capabilities Inventory

This document records the MCP-facing surface that is currently exposed by the repository.

`genexus_compare` preserves SDK `equal`/`differences`. For divergent content it
adds per-part `diffs` with a `genexus_read` alias (`part`), SDK descriptor
(`partType`) and A-to-B `unified` evidence. CRLF/CR normalize to LF; final-newline
changes remain explicit. A single contiguous replacement hunk has three context
lines and is not necessarily minimal. Input bodies have a 1,048,576 UTF-16-unit
cap; unified output has 16,384 units per part and a 131,072-byte serialized JSON
evidence budget across parts (metadata is retained). Over-limit hunks are omitted
entirely with `omittedReason=truncated`, `truncated=true`, `limit` and
`maxChars`/`maxBytes`. Other omission reasons
are `nonTextualPart`, `readFailed`, `compareFailed`, `normalizedTextEqual`.
Enumeration failures report `diffsOmittedReason=readFailed`. Only shared parts
are compared, and `mode=properties` is unchanged. This operation never writes.

Agent usage reference:
- [`docs/llm_cli_mcp_playbook.md`](llm_cli_mcp_playbook.md)

Status values:
- `active`: implemented and reachable through the current gateway-worker path
- `partial`: implemented but still limited in scope or ergonomics

## Transport

| Capability | Status | Notes |
| --- | --- | --- |
| stdio MCP loop | active | Main local transport for agent clients |
| `/mcp` HTTP endpoint | active | Supports POST, GET (SSE), and DELETE with MCP session headers |
| local bind default | active | Defaults to `127.0.0.1` through config |
| origin validation | partial | Loopback safe by default, configurable allowlist supported |
| session expiration | active | Idle sessions are removed automatically |
| HTTP shared-secret boundary | active | `GXMCP_HTTP_TOKEN` is required on every `/mcp` request when set; non-loopback binds refuse requests without it |

## KB context and ownership

The default neutral runtime is local-friendly `stdio-isolated` with
`ResolutionPolicy: "strict"`. An explicit valid local path may be opened without
pre-registering a trust root; hardened deployments add OS ACL/root/network
controls outside the MCP client registration. Strict resolution is explicit
`kb` → session `select`/`set_session_default` → strict rules. Persisted defaults
do not seed a session and declared KBs are not auto-opened. Explicit
`ResolutionPolicy: "legacy"` preserves `config-default` → `single-open` →
`declared-first` and the legacy persistent `set_default` operation.

`open` owns a worker reference through an owner-scoped lease; `select` changes
only the current session; `close` releases only the caller's reference. A
stateful KB-bound operation without its own active lease fails with
`KB_NOT_OWNED`; invalid/mismatched and expired leases use
`KB_LEASE_INVALID`/`KB_LEASE_EXPIRED`. Worker duplicate-lock startup is
`KB_LOCKED` (internal marker `WORKER_HANDSHAKE_REJECT_BUSY`). Neutral gateway
operations and explicitly lease-free reads must not be used to infer a KB.

## Tools

Source of truth:
- `src/GxMcp.Gateway/tool_definitions.json`

Query notes:
- `genexus_query` supports both the legacy `parent:"FolderName"` filter and the hierarchical `parentPath:"Module/Folder"` filter.
- Prefer `parentPath` whenever the KB contains duplicate folder names under different modules.

Tool response notes (`tools/call` text payload):
- Gateway now enriches worker payloads with AXI-like metadata under `_meta` (underscore-prefixed per MCP convention for non-standard fields).
- `_meta.schemaVersion` currently uses `mcp-axi/2` (bumped in v2.0.0).
- `_meta.tool` identifies the normalized tool name.
- For collection responses, gateway may add `returned`, `total`, `empty`, `hasMore`, and `nextOffset` when enough context is available.
- For truncated responses, gateway sets `_meta.truncated=true` and appends an actionable `help` hint.
- For idempotent success (`status=Success` + `details=No change`), gateway adds `noChange=true`.
- v2.0.0 added: `_meta.idempotent=true` on idempotency-cache hits, `_meta.batched=true` when `targets[]` plural form is used, `_meta.dryRun=true` on preview responses, `_meta.removedTools` on `initialize` for proactive agent detection of removed tools.
- For worker timeout budget events, gateway returns a structured payload with `result.isError=true`, `status=Running`, `operationId`, `correlationId`, and `help` follow-up guidance.
- These enrichments are additive and keep existing response fields for backward compatibility.

Optional response-shaping arguments for list-heavy tools:
- `genexus_query` and `genexus_list_objects` accept optional `fields` (array or comma-separated string) for a custom field subset.
- `axiCompact` defaults to **`true`** for `genexus_query` and `genexus_list_objects`. The compact projection returns only `name`, `type`, and `path` (plus `parentPath` for `genexus_list_objects`). Pass `axiCompact: false` to receive the full payload (description, parent, metadata, etc.).
- `meta.fields` is returned when field projection is active.
- `meta.totalByType` may be emitted when result rows expose a `type` field.

## Action contract

The table below is the machine-checkable action contract for every umbrella tool. An action appears in exactly one column. A mutating action changes KB, gateway, filesystem, database, team-server, or deployment state; `dryRun` is read-only only where the tool explicitly supports that preview mode.

| Tool | Read-only actions | Mutating actions |
| --- | --- | --- |
| `genexus_wire` | `pack_task`, `status`, `for`, `callers`, `impact`, `path`, `slice`, `safe_delete`, `index`, `doctor` | — |
| `genexus_data_view` | `inspect`, `dry_run` | `create`, `update`, `delete` |
| `genexus_connection_recover` | `journal_status` | `recover`, `journal_repair`, `recovery_read_complete` |
| `genexus_recipe` | `list`, `describe`, `suggest_macro` | `crystallize` |
| `genexus_lifecycle` | `inspect`, `reorg_preview`, `status`, `result`, `snapshots-list` | `build`, `build_all`, `cancel`, `reconcile`, `specify`, `validate`, `validate-kb`, `rebuild`, `reorg`, `sync`, `index`, `snapshots-restore` |
| `genexus_refactor` | — | `RenameAttribute`, `RenameVariable`, `RenameObject`, `ExtractProcedure`, `ExtractSubroutine`, `WWPSetCondition` |
| `genexus_gam` | `status` | `define_api`, `deploy` |
| `genexus_properties` | `get` | `set`, `move` |
| `genexus_structure` | `get_visual`, `get_indexes`, `get_logic`, `check_subtypes` | `update_visual`, `create_index`, `drop_index`, `set_attribute`, `set_level`, `set_domain`, `update_group`, `move_attribute`, `remove_attribute` |
| `genexus_authoring` | — | `add_external_method`, `add_external_property`, `add_menu_option`, `add_condition` |
| `genexus_layout` | `get_tree`, `find_controls`, `inspect_surface`, `get_preview`, `scan_mutators`, `list_controls`, `design_system` | `set_property`, `set_properties`, `rename_printblock`, `add_printblock`, `delete_printblock`, `add_report_control`, `move_report_control`, `remove_report_control` |
| `genexus_doc` | `health` | `wiki`, `visualize` |
| `genexus_kb` | `list`, `list_environments`, `get_environment`, `get_startup` | `open`, `close`, `select`, `set_session_default`, `set_default`, `set_persistent_default`, `set_startup`, `set_environment`, `create` |
| `genexus_navigation` | — | `view` |
| `genexus_api` | `list`, `describe`, `routes_inspect`, `diff_baseline`, `export_openapi`, `import_openapi` | `routes_clone`, `routes_update`, `snapshot` |
| `genexus_apply_pattern` | `list_actions` | `add_grid_action`, `add_user_action`, `update_action`, `move_action`, `remove_action` |
| `genexus_security` | `audit_gam`, `scan_secrets`, `scan_native`, `audit_object` | — |
| `genexus_sandbox` | — | `create`, `remove` |
| `genexus_worker_pool` | — | `warm_spares` |
| `genexus_edit_form` | — | `add_textblock`, `add_button`, `set_visibility`, `remove_control`, `wrap_in_fieldset` |
| `genexus_k2b_designer` | `inspect`, `tree`, `preview` | `set_property`, `add_node`, `move_node`, `remove_node` |
| `genexus_module` | `list` (SDK identity/path-aware, deterministic), `list_modules_servers`, `search_modules_in_servers` | `install`, `install_builtin` (dryRun=true plans local dependencies/objects/exact versions; application verifies inventory, with no implicit lifecycle or automatic rollback), `update`, `package`, `publish`, `restore`, `add_modules_server` |
| `genexus_gxserver` | `status`, `pending`, `ignored`, `conflicts`, `history`, `pipeline_list`, `pipeline_runs`, `pipeline_output` | `commit`, `update`, `lock`, `resolve`, `pipeline_run`, `pipeline_abort` |
| `genexus_kb_version` | `list`, `changed_objects` (Design vs frozen, paginated, read-only) | `freeze`, `branch`, `set_active`, `revert` |
| `genexus_browser` | `smoke`, `a11y`, `wcag`, `capture`, `cross`, `preview` | — |
| `genexus_db` | `drift_check`, `drift_report`, `optimize_analyze`, `optimize_suggest`, `optimize_report`, `sql_ddl`, `sql_navigation`, `records_query`, `types_list`, `types_describe`, `types_validate`, `reorg_impact`, `reorg_preview` | `sample_data`, `records_insert`, `records_update`, `translations_import` |
| `genexus_versioning` | `history_list`, `history_get`, `time_travel`, `blame`, `diff`, `diff_generated` | `history_save`, `history_restore`, `undo` |

| `genexus_io` | `asset_find`, `asset_read`, `read_blob`, `ocr`, `validate_kb_text_files`, `validate_text_in_memory`, `list_text_files`, `text_mirror_status` | `asset_write`, `export_part`, `import_part`, `export_kb_to_text`, `import_text_to_kb`, `text_mirror_start`, `text_mirror_stop`, `text_mirror_catchup`, `text_mirror_set_references`, `delete_kb_objects`, `export_unified`, `screenshot_publish` |
| `genexus_variable` | — | `add`, `delete`, `modify` (add/modify accept `dimensions=1|2` with positive `dimensionSizes`; reads expose the persisted fixed-size metadata) |
| `genexus_telemetry` | `executions`, `watch_event`, `friction_tail`, `learning_report`, `logs`, `profile_analyze`, `profile_hotspots`, `profile_correlate` | `friction_append` |
| `genexus_create` | `sd_panel_inspect` | `object`, `object_atomic`, `popup`, `sd_panel_create`, `sd_panel_edit`, `save_as`, `scaffold`, `translate`, `sample`, `template`, `curl_procedure` |
| `genexus_memory` | `recall`, `list` | `save`, `forget`, `promote`, `consolidate` |
| `genexus_transfer` | `inspect` | `export`, `import` |
| `genexus_deploy` | `list_targets` | `deploy` |
| `genexus_generator_reference` | `list`, `dry_run_add`, `dry_run_remove` | `add`, `remove` |
| `genexus_wwp` | `list`, `settings_templates`, `settings_read`, `list_tabs`, `tab_schema` | `add_action`, `add_user_action`, `update_action`, `move_action`, `remove_action`, `add_tab`, `move_tab`, `remove_tab`, `set_table_type`, `add_table`, `move_control`, `add_grid_attribute`, `move_grid_column`, `add_grid_variable`, `add_grid`, `set_combo_properties`, `regenerate_derived`, `replace_web_component_with_user_action`, `settings_edit` |
| `genexus_workwith` | `list_actions`, `list_filters` | `add_action`, `remove_action`, `add_filter_attribute`, `remove_filter_attribute` |

For SDTs, `genexus_structure update_visual` accepts `payload.mode=add|replace` and requires `expectedVersion`; replacement previews omitted-member removals and requires `allowRemoval=true` to apply them. `genexus_versioning history_get` reads a revision's `SDTStructure`, and `history_restore` previews or restores it with `versionId`, `expectedVersion`, native rollback, and post-save verification.

`genexus_wwp add_grid` supports both SDT collections and basic-typed variable grids without a collection. Variable columns use `{variable, basicType, length, decimals, description, readOnly}`; `gridName` defaults to `Grid`. `add_user_action` creates a missing `TableActions` under exactly one `TableMain`, including Empty-template WebPanels; `containerName: Grid` targets the unnamed WorkWith grid for a row-level action and `position` is the index among the container's existing standard and user actions. Both operations retain dry-run, optimistic version, native snapshots, independent reread, projection and rollback guards; neither runs KB lifecycle operations.

`genexus_wwp set_combo_properties` configures the DVelop combo of one WorkWithPlus attribute: `Parameters` (array of expressions or a comma-separated string), `LoadDataDynamically` (boolean) and `ConditionsDynamicAttsOrVars` (attribute/variable names of the same form), stored by the SDK as `controlInputDPParameters`, `controlLoadDynamically` and `conditionDynAtts`. Any other key is refused (`UnknownComboProperty`, with the nearest supported name) because the pattern deserializer keeps only the attributes its specification declares; the raw `genexus_edit part=PatternInstance` route now refuses undeclared attributes the same way (`PatternAttributeNotInSdkSpecification`) in the preview and before the save. `dryRun` runs every value through the SDK converters (`ConvertPropertyValueFromString`/`ToString<StringStorage>`, the ones `FromXml`/`ToXml` use) and reports requested/before/after per property without changing the instance; a control that cannot use a property (not a DVelop Combo, Based on, Fixed Values) is refused with its current configuration. A write requires `baseVersion`, changes the properties with the Pattern SDK change command, re-reads the whole instance through a fresh SDK resolution, projects the parent through the WorkWithPlus build process, verifies the projected WebForm structurally, and re-reads the instance again. On any divergence it restores the exact PatternInstance/WebForm snapshots and reports `rollbackVerified` only when a fresh re-read matches their SHA-256. It never sets `controlType`/`controlInputDS` and runs no KB lifecycle operation. Whether the projection materializes the combo control in the parent WebForm depends on the WorkWithPlus Design System of the KB; the response reports the projection steps and the structural check it could prove.

`genexus_wwp regenerate_derived` rebuilds, as one unit, the objects a WorkWithPlus instance derives (the `LoadDVCombo<Trn>` helper and the WW/DS/export family) together with the parent Transaction/WebPanel. An edit that changes how the instance calls a helper (combo `Parameters`, `ConditionsDynamicAttsOrVars`) is saved on the instance and projected on the parent, but the helper is a separate KB object that keeps its old `parm` rule and Variables until the engine rebuilds it; `genexus_apply_pattern reapply=true` on an existing WorkWithPlus instance re-projects the parent only and does not rebuild it. `dryRun` lists the host, the parent and every derived object (found as the objects the PatternInstance owns, not by name) with a `versionToken` each, marks the `helpers` (derived procedures that something calls), evaluates the call contract of each helper against every caller (argument count, order, variable names and declared types, position by position; a call written as a function, `&x = Helper(...)`, passes one argument fewer because the last `out` parameter is its result; basic types agree by family, so a `VARCHAR(40)` receives a `LONGVARCHAR`, and `Attribute:`/`Domain:` references must name the same target) and returns a paste-ready `expectedVersions` map keyed `Type:Name`. A write requires that map and refuses with `StaleObject` (listing `staleObjects`) when any object moved. It writes every part of the unit to the snapshot store, rebuilds through `PatternEngine.ApplyPattern` in refresh mode (the instance's own objects only, never the KB-wide global objects), re-reads everything freshly and requires the PatternInstance unchanged, no object changed outside the helpers when `scope` is `helpers` (the default; `all` accepts any derived object), and no helper left disagreeing with a caller (`contractConsistency`). Any failure restores the objects from the captured text and reports `rollbackVerified` only when a fresh re-read matches; objects the engine removed cannot be restored and are reported. The engine has the same prerequisite as the IDE's Apply Pattern: the KB must have the WorkWithPlus Design System imported, otherwise the engine message is returned and nothing changes. It never runs Specify, Generate, Build or Reorg.

`genexus_wwp add_tab` on a form without `<tabs>` creates the container under the unique `TableContent` (then `TableMain`, or an explicit `containerName` path) and refuses any other layout (`TabsContainerNotFound` with `availablePaths`, `TabsContainerAmbiguous`). `children[].type` accepts `attribute`: an attribute already in a form table is moved into the tab, element and XML attributes intact, and reported in `movedAttributes`; one present in several tables is `AttributeAmbiguousOnForm`; the post-save re-read fails closed and rolls back if the attribute is still in its source table. Verified against a real WorkWithPlus package (16.2, Transaction host): a tab always holds one `table`, which the engine creates together with the tab, so `children` is either one `table` child (it configures that table) or a flat list of controls (placed in a Responsive table), and more than one `table` child is refused as `InvalidWwpChild`; `add_tab` (plain, with an attribute child that moves the form attribute, with a list of controls and with a table), `move_tab` and `remove_tab` persist, project and verify, the container being found by its element name (`tabs`) whatever its type (`TabsTransaction`) and the projected tab item by its `itemControlName`.

`genexus_wwp add_table` creates a loose `<table>` (`tableName`, `numberOfColumns`, `tableType`, optional `includeInPanel`/`panelTitle`) in a WebPanel or Transaction form under the same default container rule as `add_tab` (`TableContainerNotFound`/`TableContainerAmbiguous` otherwise), places it with `position`/`before`/`after`, and takes the `add_tab` child vocabulary plus `{type:'textBlock', controlName, caption}`; a name already on the form is `TableAlreadyExists`/`ControlNameAlreadyExists`. `move_control` moves an existing `textBlock`, `variable` or `attribute` (`controlName`, optional `controlKind`) into `tableName`, reported in `movedControls`. Both maintain the affected `childrenOrderedList` entry by entry, refuse a list they cannot maintain (`WwpOrderedContainerUnsupported`), preview with `dryRun`, honor `baseVersion`, re-read and verify the PatternInstance and the parent WebForm projection, and roll back on divergence. Verified against a real WorkWithPlus package (16.2, Transaction host, target = the Transaction): new tables, `includeInPanel`/`panelTitle`, text-block children and text-block moves between tables, with and without `before`/`after`. Moving a `variable` or an `attribute` (and `attribute` children of add_table/add_tab) is verified too: the attribute reference is read from the KB object the property bag holds (its text form is the CLR type name) and written as `<type GUID>-<name>`, and `variable` children are written with numeric sizes (`basicCLength`/`basicLength`/`basicLVCLength` by type, `basicDecimals`), Numeric being the pattern's default type. A WebPanel host without a Design System persists the instance but its WebForm projection does not change, so the operation is rolled back as `WwpProjectionNotConfirmed`. `add_table` lists the new table in the container's `childrenOrderedList` with the type code the instance already uses for tables (the container's own entry, then its ancestors'; a WebPanel is `7;66;`, a transaction `2;01;`); a table the SDK drops on save is `WwpTableNotPersisted`, which reports what the re-read container holds.

`genexus_wwp add_grid_variable` adds one presentation-variable column to an existing grid (`gridPath`: the grid's name or an absolute type path). WorkWithPlus keys a grid variable by its `name` and types it with `dataType`/`basicType`/length (WorkWithPlusInstance.xml, `Variable`), and the SDK's `Variable` has no GUID (an integer `Id` and a `Name`), so there is no reference to read: the column takes its type from the variable the owning object declares (Character, VarChar, Numeric with length and decimals, Date, ..., or `dataType="Based on"` with the attribute/domain reference), a `basicType`/`length` that disagrees is `GridVariableTypeMismatch`, a collection or structured variable is `GridVariableTypeUnsupported`, and an undeclared variable needs an explicit `basicType` (and `length`) or is `GridVariableTypeRequired`. `list` returns `variables[]` with each declared variable's `type`, `length`/`decimals`, `basicType`, `basedOn` and `gridColumn`. `variableReference` is accepted and ignored (it must be the name or end with `-<name>`). A `dryRun` needs no `baseVersion` and returns the PatternInstance token; a write needs it. A write is reported as `WwpGridVariableAdded` only when the PatternInstance re-read holds the column where it was asked with the other columns untouched, and the projected parent WebForm holds exactly one new column bound to the variable (`attribute="var:N"` with the parent's variable id, or `&Name`) inside the grid control named after the requested grid; otherwise it is `WwpGridVariableNotPersisted` or `WwpProjectionNotConfirmed` (with `webFormProjection` listing the grid's columns as the form holds them) and the PatternInstance and WebForm snapshots are restored. A WebForm the projection leaves unchanged (observed without a Design System) is not a confirmation.

Real-KB validation gate: `genexus_structure action=get_visual` with a homonymous
target must be exercised against a KB that contains the relevant Transaction/Table
or WebPanel collision, using `type=Transaction` (or the other intended type). The
automated tests verify schema, routing, and contract parity; they do not claim to
exercise GeneXus SDK object resolution in CI. Follow the controlled SDK procedure
in [`docs/agent_playbook.md`](agent_playbook.md) for that manual check.

Parameter-dependent side effects are classified conservatively in the gateway:
`genexus_browser action=preview` remains read-only only when `buildFirst=false`,
`updateBaseline=false`, and the capture list excludes `screenshot`. The navigation
`view` action refreshes the per-KB navigation cache, and transfer `export` writes
the requested XPZ file.

Acceptance-path side effects do not change an action's classification. The
version-store reads `history_get`, `history_list`, `diff`, `diff_generated` and
`blame` accept an optional `async` flag: it returns an accepted envelope carrying
`operationId` and `pollTarget` before the read starts and runs it detached, so the
result is still fetchable through `genexus_lifecycle action=status/result` when the
caller's window expires. The flag changes when the handle arrives, never what the
read returns, so the actions stay read-only. It is refused on the KB-mutating
`genexus_versioning` actions, which keep the mutation path and its recovery fences.

This follow-up preserves the multi-action contract delivered in #131, the placement
semantics documented in #65, and the homonym-routing behavior tracked in #34.

## Tool inventory

| Tool | Status | Worker path |
| --- | --- | --- |
| `genexus_query` | active | `Search -> Query`; `kbs=[...]` runs a bounded read-only federation across an explicit set of already-open KBs in one call (issue #356) — per-KB `status`/`complete`, a `coverage` summary, `cursors` continuation, and `maxConcurrency`/`maxTotalResults`/`perKbTimeoutMs` bounds; never opens a KB implicitly |
| `genexus_list_objects` | active | `List -> Objects` |
| `genexus_read` | active | `Read -> ExtractSource`; `refresh=true` bypasses response caches and requires independent same-identity SDK reload (otherwise `FreshReadUnavailable`); ordinary reads may be cached after external IDE saves, with `readFreshness.origin`, `observedAtUtc` and `ageMs` (Worker observation age, not persisted KB revision); Gateway replays expose `_meta.cacheSource`/`cacheAgeMs` and combined `readFreshnessAgeMs`; `targets[]` plural form routes to `Batch -> BatchRead`; a single-part read also accepts `ifUnchangedSince` (issue #357), returning an opaque `contentToken`; a provably unchanged representation answers `notModified: true` with no `source`, and every other outcome returns the full body with `conditional.reason`. `ifUnchangedSince` is rejected with `ConditionalReadUnsupportedForm` on `targets[]`, `parts[]` and full-object forms. `objectName` is accepted as an alias of `name` (`name` wins when both are present); a call with no object identity at all is rejected as a `usage_error` and performs no lookup |
| `genexus_edit` | active | Full Source forwards `verifyMode`/`requireObjectSave`, verifies public-read text, and separates physical save from comparison evidence; `Write`, `SemanticOps -> Apply` (mode=ops), `JsonPatch -> Apply` (mode=patch + array), or `Patch -> Apply` (mode=patch + string, and the abbreviated `patch={find,replace}` form that additionally accepts `patch.scope` / `patch.indentation`); `targets[]` plural form routes to `Batch -> MultiEdit`, `parts[]` to `Batch -> BatchEdit`, `changeSet` to `Mutation -> ChangeSet`; full persisted `source`/`content` is omitted by default across successful writes; `return_post_state` controls only the `post_state` object; `includePersistedText=true` restores full text for `genexus_edit`, `genexus_edit_and_build`, `genexus_io`, and `genexus_variable`, with omitted paths in MCP `_meta.omittedFields`; oversized `post_state.diff` is capped at 40 lines with `diffTruncated`; variable add/modify returns changed declarations and persisted count |
| `genexus_inspect` | active | `Analyze -> GetConversionContext` |
| `genexus_analyze` | active | `Analyze`, `Linter`, or `UI` depending on mode |
| `genexus_dfd` | active (read-only) | `Dfd` - ER diagram (Mermaid) of a Transaction/Table from the SDK table relations only |
| `genexus_impact` | active (read-only) | `Impact` - affected set, counts by type, dependencies-first rebuild order and cycles over the caller graph; no risk score |
| `genexus_object_context` | active (read-only) | `ObjectContext` - identity + callers/callees with coverage + data model in one call |
| `genexus_lifecycle` | active | `Build`, `KB`, or `Validation` depending on action (specify, compile_check, build, build_all, rebuild, index, status, result, reorg, validate); index `status` waits accept `wait`/`since`/`freshness` and report `waitSatisfied` |
| `genexus_create` | active | Object creation umbrella: Transaction, Procedure, WebPanel, SDT, API, Domain, Popup, SDPanel, SaveAs, Template, `object_atomic` |
| `genexus_structure` | active | `Structure -> GetVisualStructure | UpdateVisualStructure | GetVisualIndexes | GetLogicStructure | CheckSubtypes`; supports `type` disambiguation, `remove_attribute`, `move_attribute` |
| `genexus_refactor` | active | `Refactor -> RenameObject | RenameAttribute | RenameVariable | ExtractProcedure | ExtractSubroutine | WWPSetCondition` |
| `genexus_format` | active | `Formatting -> Format` |
| `genexus_properties` | active | `Property -> Get | Set | Move`; `Get` accepts `targets: [{name, type?}]` for up to 100 ordered reads with per-target errors, and `propertyNames` plus `reconcileTimedOutWrite` for an authoritative typed read with a version token after a timed-out set; `Set` accepts `dryRun` and `expectedVersion`, verifies persisted values after save, and rolls back partial writes; `Get`/`Set` with `control` + `propertyName=DataProviderParameters` read and replace the ordered parameter list of a layout-form Dynamic Combo/List Box (`expressions[]`; the dry run validates every expression with the SDK's parameter-list language and the resulting layout, and a real write requires `expectedVersion` and is read back from a fresh instance); move accepts `destination`/`folder`/`module`/`targetModule` and tolerates Folder-destination placement property echoes (issue #238) |
| `genexus_versioning` | active | Versioning umbrella: `History -> List | Get_Source | Save | Restore`, `Undo`, `TimeTravel`, `Blame`, `Diff` |
| `genexus_io` | active | IO umbrella: `Asset -> Find | Read | Write`, Object Text batch `ExportKbToText | ImportTextToKb | ValidateKbTextFiles | ValidateTextInMemory | ListTextInMemory | DeleteKbObjects`, native SDK tree `src/`/`ref/` with incremental modes and sectioned `part=all`/`parts[]` documents, installed reference modules/packages routed to `ref/`, module metadata (`module.toml`) and optional official packages (`.opc`) and Transaction table projections (`#tables`), filesystem controls (`listOnly | skip | stopOnError | includeChildren | ignore | forceSave | rollbackOnFailure`), manifest/hash validation, watermark mirror (`Start | Stop | Status | Catchup | SetReferences`), `Object -> ExportText | ImportText` (`import_part` accepts `includePersistedText=true` for full response text), `Export -> Unified`, `ScreenshotPublish` |
| `genexus_db` | active | Database umbrella: `DbDrift`, `DbOptimize`, `Analyze -> GetSQL / GetSqlForNavigation / GenerateSampleData`, typed Transaction records (`QueryRecords / InsertRecord / UpdateRecord`), `Types`, `ReorgImpact` |
| `genexus_layout` | active | WebForm control tree, layout properties, printblock and report-control management; report fonts validate installed families and preserve unspecified components. Printblock add/rename/delete are confirmed by a cache-invalidated read-back and refused with `AddPrintBlockVerificationFailed`, `RenamePrintBlockVerificationFailed` or `DeletePrintBlockVerificationFailed` when it does not show the change (add/rename retry and may repair Source first; the refusal carries no `saved`/`persistedVerified`). Report-control verification failures are a separate family: they return `recoveryEvidence` and `rollbackUnavailableReason` without a second write or unsafe automatic restore |
| `genexus_edit_form` | active | Semantic WebForm element manipulation |
| `genexus_k2b_designer` | requires IDE bridge | Native editing of already-active K2B WebPanel Designers in the open GeneXus IDE; requires matching KB and pipe, an open clean document, and an expected version for writes |
| `genexus_apply_pattern` | active | Pattern application for any installed pattern (registry discovered from `Packages\Patterns`), diagnose, and reapply on WorkWithPlus instances only (with no implicit WorkWithPlus fallback when `pattern` is omitted - headless reapply of any other pattern is refused as `PatternRouteUnsupported`), and WorkWithPlus action-group/form-action configuration |
| `genexus_wwp` | active | Typed WorkWithPlus grid/form actions, tabs, nested controls, native table-type changes, grid attributes/variables, and native WebComponent-to-DropDownComponent replacement with preview, physical KB identity, parent/PatternInstance projection checks, concurrency checks, verification, and conditional rollback; an existing object without a WorkWithPlus instance returns `WWPInstanceNotFound` with `detectedPatterns` |
| `genexus_workwith` | active | Typed authoring and removal of actions, action parameters and Selection filter attributes (`add_filter_attribute` / `remove_filter_attribute`, native SDK only, no XML write fallback) for standard WorkWith (WorkWith for Web) pattern instances; preserves strict boundary with WorkWithPlus (genexus_wwp) |
| `genexus_security` | active | `Security -> audit_gam | scan_secrets | scan_native | audit_object` (native SDK scanner; in-memory single-object audit) |

| `genexus_kb` | active | Multi-KB pool management, startup object, and environment switching |
| `genexus_kb_version` | active | SDK `KBVersionHelper` model version tree and branch management; `changed_objects` provides a read-only Design-vs-frozen NEW/CHANGED inventory or stable `ChangedObjectsNotSupported` when the SDK surface is unavailable |
| `genexus_gam` | active | SDK `IIntegratedSecurityService` GAM provisioning and deploy |
| `genexus_transfer` | active | Native XPZ export and import |
| `genexus_deploy` | active | Application deployment targets and execution |
| `genexus_doc` | active | `Wiki`, `Visualizer`, or `Health` depending on action |
| `genexus_recipe` | active | Named playbooks, macro suggestion, and crystallization |
| `genexus_generator_reference` | active | Native typed .NET generator references |
| `genexus_data_view` | active | Native typed Transaction + Data View authoring |
| `genexus_whoami` | active | KB context, version, health, and playbook/skills discovery |
| `genexus_wire` | active (native-rust) | Architectural discovery, call graph, impact, and orientation under token budget: `pack_task`, `status`, `for`, `callers`, `impact`, `path`, `slice`, `safe_delete`, `index`, `doctor`. Reads sources from **files**: a native GeneXus 18 KB keeps object sources in the KB database, so every action except `pack_task` refuses it with `GXWIRE_REQUIRES_TEXT_SOURCE_TREE` before any process starts, naming `genexus_io action=export_kb_to_text format=native` as the way to obtain a text tree. Requires `dir=` to be a text source tree (GeneXus XML/text export or the native `src/` export). `status` answers from the index and reports `GxwireCacheMissing` when there is none; every failure (`GxwireTimeout`, `GxwireExecutionFailed`, `GxwireOutputUnparsable`, `GxwireNotFound`, `GxwireException`) is a coded envelope with `diagnosticContext`, never a bare `Internal error`. `pack_task` on the open native KB is the exception: it falls through to the Worker, which resolves `targets[]` (and whole identifiers in `task`) through the SDK-backed index and returns the resolved objects with their direct callers/callees, the sources consulted and the limitations (`unresolved`, `ambiguous`, `skipped`, `omittedTargets`, partial-index trust); read-only, no Specify/Generate/Build/Rebuild/reorg/execution and no text export, no part body read. Writes nothing into the analysed directory: the index lives under `%LOCALAPPDATA%\GxMcp\Cache\gxwire` |
| `genexus_kb_diff` | active (gateway-only) | Filesystem comparison of two explicit KB aliases/paths; not KB-bound and does not dispatch to a Worker. Change detection is SHA-256 content per object part (default `mode=content`), not timestamp/part-count metadata; `mode=metadata` restores the cheaper heuristic and is labelled as such. Identity is the on-disk `Objects/<Type>/<Name>` naming and is not module-qualified. An absent, unreadable or truncated `Objects/` tree reports `status=Incomplete` with `complete=false`; only a proven empty `Objects/` reports an empty model. `maxObjects` bounds the enumeration per side. |
| `genexus_kb_import` | active (gateway-only) | Filesystem object copy between explicit source/target KBs; target may fall back to the active KB and must be indexed afterwards |
| `genexus_sandbox` | active (gateway-only) | Filesystem sandbox create/remove; no SDK dispatch |
| `genexus_worker_pool` | active (gateway-only) | Worker-pool warm-spare configuration; no KB selection or Worker tool dispatch |

## Resources

| Resource or template | Status | Notes |
| --- | --- | --- |
| `genexus://kb/index-status` | active | KB indexing status |
| `genexus://kb/health` | active | Gateway and worker health report |
| `genexus://kb/agent-playbook` | active | Agent-native operating playbook for MCP, verification, and Git-friendly change control |
| `genexus://kb/llm-playbook` | active | Protocol-first guide for LLM usage across CLI AXI and MCP tool flows |
| `genexus://kb/skills/nexa` | active | Official Nexa skill for GeneXus modeling, properties, commands, and KB workflows |
| `genexus://kb/skills/nexa/references/{name}` | active | On-demand Markdown references from the official Nexa skill pack |
| `genexus://objects` | active | Browsable index of objects |
| `genexus://attributes` | active | Browsable attribute listing |
| `genexus://objects/{name}/part/{part}` | active | Part-specific object reading |
| `genexus://objects/{name}/variables` | active | Object variable declarations |
| `genexus://objects/{name}/navigation` | active | Navigation analysis |
| `genexus://objects/{name}/hierarchy` | active | Dependency hierarchy |
| `genexus://objects/{name}/data-context` | active | Data context bundle |
| `genexus://objects/{name}/ui-context` | active | UI context bundle |
| `genexus://objects/{name}/conversion-context` | active | Conversion-oriented context |
| `genexus://objects/{name}/pattern-metadata` | active | Pattern metadata |
| `genexus://objects/{name}/summary` | active | LLM-oriented summary |
| `genexus://objects/{name}/indexes` | active | Visual indexes for Transaction/Table objects |
| `genexus://objects/{name}/logic-structure` | active | Logical structure for Transaction/Table objects |
| `genexus://attributes/{name}` | active | Attribute metadata |
| resource subscriptions | partial | Legacy `resources/subscribe` remains session-scoped over GET/SSE; modern 2026 clients can use bounded POST `subscriptions/listen` with opt-in list/resource filters and per-stream subscription ids. Resource notifications now carry `kbAlias`, `cacheRevision`, and a KB-qualified `resourceUri`; full wire/reconnect coverage remains pending. |

## Prompts

| Prompt | Status | Notes |
| --- | --- | --- |
| `gx_explain_object` | active | Grounded explanation workflow using source, variables, navigation, and summary |
| `gx_bootstrap_llm` | active | Session bootstrap workflow for protocol-first usage (`tools/list`, `resources/list`, `prompts/list`, `genexus://kb/llm-playbook`) with optional `goal` argument |
| `gx_convert_object` | active | Conversion workflow with review gates and target-language argument |
| `gx_review_transaction` | active | Transaction review workflow focused on structure, rules, and risks |
| `gx_refactor_procedure` | active | Procedure refactor workflow focused on preserving behavior |
| `gx_generate_tests` | active | Test-plan generation workflow |
| `gx_trace_dependencies` | active | Dependency tracing workflow with impact analysis |
| `gx_agent_ship_change` | active | Controlled-change workflow for agents with explicit verification and reporting |
| `gx_agent_visual_change` | active | Visual metadata workflow that forces authoritative-surface resolution before editing |

## Completion

| Capability | Status | Notes |
| --- | --- | --- |
| `completion/complete` | active | Supports structured completions for object parts, include fields, and target languages |

## Notifications

| Capability | Status | Notes |
| --- | --- | --- |
| `notifications/initialized` | active | Handled as a no-op |
| operation progress notification | active | Emitted through SSE as `notifications/message` with `operationId` and `correlationId` for long-running tools |
| tools list changed notification | active | Emitted through the HTTP SSE session stream |
| resources list changed notification | active | Emitted through the HTTP SSE session stream |
| resource updated notification | active | Emitted through the HTTP SSE session stream |
| modern subscriptions/listen | partial | Acknowledgement and filtered SSE delivery are implemented; stream capacity/queue limits, disconnect cleanup, and KB/revision-qualified resource metadata are enforced. Full wire/reconnect coverage remains pending. |

Operational notes:
- `genexus_lifecycle(action='status'|'result', target='op:<operationId>')` resolves gateway-tracked MCP operations.
- `genexus_lifecycle(action='status', target='gateway:metrics')` returns per-tool p50/p95 and error/timeout/no-change counters.

## Extension integration

| Capability | Status | Notes |
| --- | --- | --- |
| local discovery file `.mcp_config.json` | active | Points to `/mcp` |
| default extension HTTP client | active | Extension runtime speaks MCP directly for discovery, VFS, providers, shadow sync, commands, and webviews |
| dynamic tool discovery in extension | active | Runtime discovery now loads tools, resources, and prompts from `/mcp` and caches the snapshot locally |
| MCP discovery commands in extension | active | Command Palette can inspect discovery, open resources, and run prompts from the cached snapshot |
| global Claude registration | active | Uses HTTP wrapper against `/mcp` |

## Known gaps

- Resource surface is still too small for rich object exploration.
- Prompt catalog is still minimal.
- Completions are currently static and schema-oriented; object-name completion is still pending.
- Prompt workflows now validate required and enumerated arguments in the gateway, but object-name-aware completion is still pending.
- Extension flows already migrated to MCP include discovery, prompts, resources, SQL, tests, build/rebuild, indexing, object creation, attribute rename, procedure extraction, properties, history, and structure/indexes views.
- `genexus_forge` is reachable now, but code generation quality is still early-stage.
