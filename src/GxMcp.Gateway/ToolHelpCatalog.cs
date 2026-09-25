using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal static class ToolHelpCatalog
    {
        private const string ContextLeaseContract =
            "\n\n## KB context, ownership, and compatibility\n" +
            "- Default local workflow: `GatewayMode=stdio-isolated`, `ResolutionPolicy=strict`. An explicit valid local KB path is sufficient for `genexus_kb action=open`; hardened deployments add OS/root/network controls outside client registration.\n" +
            "- Strict resolution is explicit `kb` → session `select`/`set_session_default` → strict rules. Persisted defaults do not seed a strict session and declared KBs are not auto-opened. `ResolutionPolicy=legacy` preserves `config-default` → `single-open` → `declared-first` and legacy persistent `set_default`.\n" +
            "- `open` creates an owner-scoped lease; `select` changes only in-memory session state; `close` releases only the caller's lease. A `kb` selector identifies a target but does not transfer ownership.\n" +
            "- Stateful KB-bound calls without the caller's active lease fail with `KB_NOT_OWNED`; invalid/mismatched and expired leases use `KB_LEASE_INVALID`/`KB_LEASE_EXPIRED`. Duplicate worker startup is `KB_LOCKED` (internal marker `WORKER_HANDSHAKE_REJECT_BUSY`).\n" +
            "- Neutral gateway operations and explicitly lease-free reads must not be used to infer a KB. Sessionless HTTP cannot use `select` and returns `KB_SESSION_UNAVAILABLE`.\n" +
            "- `GXMCP_HTTP_TOKEN` is an environment secret: when set, send it on every `/mcp` request in an Authorization header using Bearer TOKEN or in X-GXMCP-Token; never put it in config, registration, MCP output, leases, journals, or logs. Loopback without a token remains local-friendly; non-loopback without one is refused.\\n";

        private static readonly Dictionary<string, string> _helpTexts = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["genexus_compare"] =
                "# genexus_compare\n\n" +
                "Read-only comparison of objectA and objectB. The SDK remains authoritative for equal and differences (SDK part descriptor names); mode=properties is unchanged.\n\n" +
                "When content differs, diffs describes shared parts: part is the genexus_read alias, partType is the SDK descriptor, and unified is an A-to-B unified diff with three context lines. Each diff uses a/part and b/part headers; use its enclosing part field to identify the source. One contiguous replacement hunk is emitted, not necessarily the shortest edit script.\n" +
                "CRLF and bare CR normalize to LF. Final-newline differences are preserved with standard no-newline markers. SDK equality is not overridden: normalizedTextEqual means the SDK differs but normalized text does not.\n" +
                "Input bodies are capped at 1,048,576 UTF-16 code units; unified output at 16,384 units per part, with a 131,072-byte serialized JSON evidence budget across parts (metadata is retained). Over-limit evidence is omitted entirely with omittedReason=truncated, truncated=true and limit/maxChars or maxBytes; no incomplete patch is returned.\n" +
                "Other per-part omittedReason values: nonTextualPart, readFailed, compareFailed. Comparison failures do not assert a difference; enumeration failure sets diffsOmittedReason=readFailed. Other parts and the top-level SDK verdict survive. Parts absent from either object are not compared. No writes, merge, build or execution.\n",

            ["genexus_query"] =
                "# genexus_query\n\n" +
                "Search objects in the active Knowledge Base.\n\n" +
                "## Query prefixes\n" +
                "- `usedby:<name>` — objects that reference <name>\n" +
                "- `type:<ObjectType>` — filter by Transaction, Procedure, WebPanel, etc.\n" +
                "- `description:<text>` — search inside object descriptions\n" +
                "- `parent:<folder>` — filter by direct parent folder\n" +
                "- `parentPath:<a/b/c>` — filter by full folder path\n\n" +
                "## Index behaviour\n" +
                "- The first call on a fresh install triggers the KB index build.\n" +
                "- `_meta.partial=true` means more results are still being indexed.\n" +
                "- Literal-name queries (no prefix) skip the index entirely.\n" +
                "- `genexus_read`, `genexus_edit`, `genexus_list_objects`, and `genexus_lifecycle` are index-independent.\n\n" +
                "## Defaults\n" +
                "- `axiCompact: true` — pass `false` to get the full payload.\n" +
                "- `limit: 50`, `offset: 0`.\n" +
                "- `exactMatch: true` restricts results to the exact object name after the query is normalized.\n\n" +
                "## Examples\n" +
                "- `{ query: 'type:Procedure', limit: 20 }`\n" +
                "- `{ query: 'usedby:InvoiceProc' }`\n" +
                "- `{ query: 'OrderTrn', fields: 'name,type,path,description' }`\n",

            ["genexus_lifecycle"] =
                "# genexus_lifecycle\n\n" +
                "Build, validate, index, or poll the active Knowledge Base.\n\n" +
                "## Actions\n" +
                "- `build` — non-blocking when `estimated_seconds >= 20`; returns `{ operationId, job_id, status: 'running', pollTarget: 'op:<id>' }` and surfaces `_meta.background_jobs` on the next call. Pass `wait_until_done: true` to block until terminal (single turn instead of polling).\n" +
                "- `build_all` — incremental Build All for the entire selected KB (`ForceRebuild=false`). It is global and rejects `target`; if the SDK reports a required reorganization it returns `status: 'ReorgRequired'` without applying the reorg.\n" +
                "- `rebuild` — forced Rebuild All (`ForceRebuild=true`) and remains compatible with directed targets.\n" +
                "- `validate` — inline validation/specifier check. Returns the result in the same call; it does not currently use the background-job path.\n" +
                "- `index` — rebuilds the search index. Pass `force=true` to ignore the on-disk cache.\n" +
                "- `status` — accepts either a `taskId` or `job_id` via `target`; pass `wait_seconds > 0` to long-poll up to 600s. A positive lifecycle status wait is not cut to the generic 50s no-progress cap; without `target`, `wait` blocks on the search index and `freshness` sets the target state: `freshness='current'` waits for a warm-start delta refresh to finish (a snapshot restored from the warm cache is `Ready` but `stale`, so a status-only wait returns immediately). The reply carries `waitSatisfied` so a timeout is distinguishable from success.\n" +
                "- `result` — fetch the completion payload of a finished operation.\n" +
                "- `inspect` — read the redacted durable mutation journal for an operation key after a lost response; it never replays the write.\n" +
                "- `reconcile` — close an unknown mutation fence only after an independent read and explicit `confirmed: true` verification; use a fresh key for any later write.\n" +
                "Worker restart is handled by `genexus_worker_reload`; use that dedicated tool instead of a lifecycle action.\n\n" +
                "## target format\n" +
                "- Build/validate: object name(s), comma- or semicolon-separated.\n" +
                "- compile_check: use a unique object name, `Type:Name`, or GUID. Folder paths and textual EntityKey values are unsupported build identifiers.\n" +
                "- Build All: omit `target`; the action always covers the selected KB.\n" +
                "- Status/result on a background op: `op:<operationId>` or just `<job_id>`.\n\n" +
                "## Build-evidence checklist (issue #42)\n" +
                "A GeneXus build can report `Status: Succeeded` (0 errors/0 warnings) without the generated `.cs` actually landing on disk. Do NOT treat `Succeeded` alone as proof your edit was compiled. On every build result:\n" +
                "1. **Check `effective_status`.** `SucceededWithGaps` means the build reported success but the evidence gate found no fresh generated `.cs` for one or more targets — the code you edited may NOT be regenerated. Treat it as a soft failure and investigate before moving on.\n" +
                "2. **Read `generateEvidence`.** `{ ok, objectsChecked, objectsBuilt, filesWritten[], staleOrMissing[], referencedButNotBuilt[]? }`. `staleOrMissing` lists targets whose `.cs` is older than the build start (or absent). `referencedButNotBuilt` appears when `includeCallees: none` dropped objects your target calls — rebuild with `includeCallees: direct|transitive` to regenerate them.\n" +
                "3. **Check `staleGenerated`.** Objects edited via the MCP this session that have not been successfully rebuilt since. Build them (or a full build) before you rely on the generated output.\n" +
                "4. **A second build is refused with `status: BuildAlreadyRunning`** while one is in flight (builds serialize per worker). Poll `activeTaskId` or cancel it first; opt out with env `GXMCP_ALLOW_CONCURRENT_BUILDS=1`.\n" +
                "5. A build that stops making progress (phase/counts frozen) is force-failed after `GXMCP_BUILD_NOPROGRESS_SEC` (default 180s; 0 disables) instead of sitting `Running` for the full timeout.\n\n" +
                "## Examples\n" +
                "- `{ action: 'build', target: 'InvoiceProc' }`\n" +
                "- `{ action: 'status', target: 'op:abc123', wait_seconds: 600 }`\n" +
                "- `{ action: 'build', target: 'InvoiceProc', wait_until_done: true }`\n" +
                "- `{ action: 'status', wait: 30, freshness: 'current' }`  # block until the index is current\n" +
                "- `{ action: 'index', force: true }`\n",

            ["genexus_worker_reload"] =
                "# genexus_worker_reload\n\n" +
                "Restart the gateway-managed Worker for an open Knowledge Base. This is a gateway operation, not a `genexus_lifecycle` action.\n\n" +
                "## Modes\n" +
                "- `mode=soft` — drain the selected worker, replace it, and wait for SDK readiness. This is the normal restart path.\n" +
                "- `mode=hard` — copy Worker binaries from `sourceDir` during the drain window, then replace the worker.\n" +
                "- `force=true` — kill and respawn directly when the worker is wedged and cannot acknowledge a graceful drain. It is only valid with `mode=soft`; `force=true` + `mode=hard` is rejected before any Worker is stopped.\n\n" +
                "## Selection and safety\n" +
                "- With one open KB, `mode=soft` is sufficient. With multiple workers, pass `alias=<alias>` (or its `kb` alias) to select the target explicitly.\n" +
                "- `mode=hard` requires a valid `sourceDir`; use the repository's Worker `bin/Debug` directory when hot-swapping a local build.\n" +
                "- A graceful response means the replacement signalled SDK-ready. A forced reload abandons in-flight Worker jobs; inspect `scope`, `affectedAliases`, and returned worker state before retrying.\n" +
                "- `force=true` with `alias`/`kb` recycles only that selected Worker and returns `scope=alias`; without a selector it is an explicit global reset and returns all affected aliases. The global path abandons in-flight jobs.\n",

            ["genexus_edit"] =
                "# genexus_edit\n\n" +
                "Edit the source or metadata of a GeneXus object.\n\n" +
                "## Required\n" +
                "- Either `name` (single object) **or** `targets` (array) — never both.\n" +
                "- `mode`: `full` (replace whole part) or `patch` (Replace/Insert_After/Append over a context anchor).\n" +
                "- `mode: 'ops'` applies semantic operations; for modular objects pass `module` to select the Transaction module.\n" +
                "- `dryRun: true` first for either mode. A preview is synchronous, never calls Save, and never starts a lifecycle action.\n" +
                "- `patch={find,replace}` is the abbreviated textual replace form; it is also the only form that accepts the two opt-in protections below. Combining either protection with `operation`, `mode=ops`, `targets[]`, `parts[]`, `Insert_After` or `Append` is rejected before anything is normalized (`ScopeUnsupportedPatchForm` / `IndentationUnsupportedPatchForm`) — a protection is never silently ignored.\n\n" +
                "## Opt-in patch protections\n" +
                "- `patch.scope={start,end}` bounds where `find` may match. The editable region is the complete lines between the anchors (after `start`'s last line, before `end`'s first line; to EOF when `end` is omitted) and the anchors themselves are never edited. Each anchor must be a unique complete line of the part — only CRLF/LF are normalized, so a tab/space difference is still a miss. Codes: `ScopeStartRequired`, `ScopeAnchorNotFound`, `ScopeAnchorAmbiguous`, `ScopeAnchorNotComparable`. Use it when a similar block repeats elsewhere in the part (e.g. one branch per database).\n" +
                "- `patch.indentation={mode:'validate'}` compares the replacement's base indentation against the matched line's before persisting (tabs and spaces are distinct; nothing is reformatted, `replace` is inserted literally). A match starting mid-content or mid-indent, or a blank first line in `replace`, is `IndentationNotComparable`; a differing base indent is `IndentationMismatch` and nothing is written. With `replaceAll=true` every match is validated.\n" +
                "- Both protections report line evidence (1-based, exclusive end): `result.scope.editableStartLine`/`editableEndLineExclusive`/`scopeEndsAtEof`/`matchStartLine`, and `result.indentation.sites[].expectedPrefix`/`preservedPrefix`/`receivedPrefix`. A `dryRun` reports the verdict without failing so it can be reviewed first.\n\n" +
                "## Output\n" +
                "- Returns `post_state.diff` (unified diff) by default.\n" +
                "- `verbose: true` adds slices with ±15 lines of context.\n" +
                "- `return_post_state: false` opts out of the post-state block to save tokens.\n" +
                "- `async: true` returns immediately with one `operationId` / `job_id`; the same ID is used by Worker busy telemetry and lifecycle status/result/cancel. Cancellation terminalizes the operation and recycles a blocked non-preemptible Worker.\n" +
                "- Successful writes omit full persisted `source`/`content` by default; `return_post_state` only controls `post_state`. Pass `includePersistedText: true` to restore full text. Otherwise use `genexus_read` when the complete part is needed. Oversized `post_state.diff` is capped at 40 lines with `diffTruncated: true`. Full Source receipts retain `postSaveVerification.versionToken`, `persisted`, and `implicitLifecycleActions`; after timeout/cancellation, a fresh `genexus_read` still gates another write.\n\n" +
                "## Patch persistence verification\n" +
                "Full Source and Rules use the same public-read representation after save. Full Source supports requireObjectSave. sdkSaveCompleted/saved describe physical save independently of persistedStateKnown and postSaveVerification.matches; inspect mutation.diff for bounded expected/read lines and lineEndings, moduleQualification or contentMismatch. Never retry automatically. For `part=Events`, `requireObjectSave: true` requests the complete-save contract. Dry runs remain available; real writes currently return `ObjectSaveIsolationUnverified` before persistence because SDK/pattern save-event isolation is unverified. There is no override. This mode requires `baseVersion` for a non-dry-run write. If only part of that contract is confirmed, the response is `ObjectSaveIncomplete`, includes `partPersisted`, `objectSaved`, `metadataStampPersisted`, `metadataUpdated`, `revisionBefore`, `revisionAfter`, and sibling-part evidence, and warns against a blind retry; rollback is never implicit. `verifyMode: 'normalized'` is the default and tolerates EOL, encoding marker, trailing-whitespace, and repeated-blank-line rendering by the SDK; full `exact` compares text verbatim including EOL; patch `exact` retains logical CRLF/LF equivalence; `semantic` also tolerates harmless SDK casing/spacing changes. Comment-only Replace writes require `baseVersion`, are verified against the requested comment, report active old-statement presence, and return `CommentOnlyWriteNotPersisted` if the SDK re-read diverges. A mismatch is never reported as Applied. Rollback requires `rollbackOnFailure: true` and a valid snapshot. Full Source returns `AtomicRollbackUnavailable` without a restore write until atomic conditional restore is supported; it never overwrites a newer edit through best-effort rollback. Pass the prior read's `versionToken` as `baseVersion` to reject concurrent edits. No Specify, Generate, Build, Rebuild, compilation, reorganization, execution, or tests are invoked by a patch write.\n\n" +
                "## Disambiguation\n" +
                "If `name` matches multiple objects, the error includes `suggestion` and `availableTypes`. Pass `type=<ObjectType>` or use `parentPath` to disambiguate.\n\n" +
                "## Examples (source code)\n" +
                "- `{ name: 'InvoiceProc', part: 'Source', mode: 'patch', operation: 'Replace', context: '<old block>', content: '<new block>', dryRun: true }`\n" +
                "- `{ name: 'OrderTrn', part: 'Rules', mode: 'full', content: '<rules text>' }`\n\n" +
                "## Editing pattern parts (PatternInstance / PatternVirtual)\n" +
                "Any installed pattern's instance can be edited (WorkWithPlus, K2BTools `K2BEntityServices<Trn>`, ...). Name the instance object, or its parent: a parent resolves to its WorkWithPlus instance when it has one (historical behaviour), otherwise to its single pattern instance; a parent with several non-WorkWithPlus instances returns `PatternInstanceAmbiguous` with `candidates`. Editing a non-WorkWithPlus instance saves the instance only (the result carries `generatedObjectsRegenerated: false`); regenerate its objects by applying the pattern in the GeneXus IDE. The WorkWithPlus-specific notes below (apply-on-save, projection, typed actions) apply only to WorkWithPlus.\n\n" +
                "Pattern XML is the IDE's structural model — containers, controls, actions, grids, orders, filters all live there. `PatternVirtual` continues to support structural full/patch edits through the SDK. Raw `PatternInstance` XML edits are limited to existing property changes; identity, defaults, templates, ordering metadata and structure are rejected explicitly. Use the typed WorkWithPlus actions or SDK pattern operations for structural changes.\n\n" +
                "### Element kinds (XML node → IDE control)\n" +
                "- `<textBlock controlName=\"...\" caption=\"...\" themeClass=\"BigTitle|LinkText|...\" format=\"HTML\" />`\n" +
                "- `<errorViewer defaultThemeClass=\"ErrorViewer\" />`\n" +
                "- `<attribute attribute=\"<guid>-<FieldName>\" themeClass=\"Attribute\" isRequired=\"True\" NoAccept=\"True\" />`\n" +
                "- `<gridAttribute>` / `<filterAttribute>` / `<descriptionAttribute>`\n" +
                "- `<standardAction name=\"Trn_Enter|Trn_Cancel|Trn_Delete|Insert|Update|Delete|Export|...\" caption=\"...\" buttonClass=\"btn ButtonGreen\" />` — only these registered names; **the SDK rejects unknown standardAction names**.\n" +
                "- `<userAction name=\"AnyName\" caption=\"...\" buttonClass=\"btn ButtonBlue\" confirm=\"False\" />` — use this for custom buttons like Duplicate, Audit, Export, etc.\n" +
                "- `<table name=\"...\" isGroup=\"True\" title=\"Section title\" groupThemeClass=\"GroupTela|GroupTelaResp|GroupFiltro\">...</table>` — groups (named sections).\n" +
                "- `<order name=\"...\"><attribute attribute=\"<guid>-Field\" /></order>` inside `<orders>` (Selection view).\n" +
                "- `<rule Name=\"...\" Rule=\"<SDK rule text>\" />` inside `<rules>`.\n" +
                "- `<eventBlock BlockName=\"...\" />` inside `<events>`.\n\n" +
                "### Transaction vs Selection views (XPath split)\n" +
                "- Transaction (form view, `/instance/transaction/...`): TableMain → TableContent (attributes) → TableActions (Trn_Enter/Cancel/Delete buttons).\n" +
                "- Selection (list view, `/instance/level/selection/...`): TableSearch (filters) → `<orders>` → TableGridHeader → `<grid>` (gridAttributes).\n" +
                "- Edit one without touching the other.\n\n" +
                "### Theme classes\n" +
                "Run `genexus_list_objects --typeFilter ThemeClass --nameFilter <Button|TextBlock|Title|...>` to discover the actual class names in this KB (they vary per design system). Common patterns: `themeClass=\"BigTitle\"`, `themeClass=\"LinkText\"`, `groupThemeClass=\"GroupTelaResp\"`, `cellThemeClass=\"TableTitleCell\"`. Buttons use `buttonClass=\"btn <ColorClass>\"` (e.g. `btn ButtonGreen`, `btn ButtonRed`).\n\n" +
                "### \"Apply this pattern on save\" override\n" +
                "When that checkbox is on (the default), WorkWithPlus recomputes some attributes after every save — notably `title` on top-level groups. Toggle it via `genexus_properties --action set --name WorkWithPlus<Object> --propertyName SDPlus_Editor_Apply_On_Save --value False` to keep hard overrides.\n\n" +
                "### Pattern examples\n" +
                "- Add a custom button: `{ name: 'WorkWithPlusAcao', part: 'PatternInstance', mode: 'patch', operation: 'Insert_After', context: '<existing Trn_Delete standardAction line>', content: '<userAction caption=\"Auditar\" name=\"Auditar\" buttonClass=\"btn ButtonCinza\" confirm=\"False\" />' }`\n" +
                "- Wrap attributes in a styled group (full rewrite): `{ name: 'WorkWithPlusAcao', part: 'PatternInstance', mode: 'full', content: '<full <instance> XML with <table isGroup=\"True\" title=\"Identificação\" groupThemeClass=\"GroupTelaResp\">...>' }`\n" +
                "- Add a Selection ordering through the typed WorkWithPlus action or an SDK pattern operation; raw PatternInstance edits do not rebuild `childrenOrderedList`.\n",

            ["genexus_analyze"] =
                "# genexus_analyze\n\n" +
                "Semantic analysis across one or more objects.\n\n" +
                "## Modes\n" +
                "- `context` (alias `deep_context`) — 360° task context in one call.\n" +
                "- `impact` — callers, callees, blast radius, risk level, affected entry points.\n" +
                "- `linter` — static analysis; pass `fix: true` only when the automatic fix should be applied to the KB.\n" +
                "- `code_metrics` — KB-wide source metrics; `top` limits the returned results (default 25).\n" +
                "- `dependencies` — typed dependency graph.\n" +
                "- `complexity` — line/cyclomatic counts.\n" +
                "- `naming` — naming-convention audit.\n" +
                "- `summary` — LLM-oriented summary of the object.\n" +
                "- `explain` — legacy compatibility route; returns a typed NotImplemented envelope instead of inventing an explanation.\n\n" +
                "## When to use what\n" +
                "- Raw source: `genexus_read`.\n" +
                "- Single-object metadata: `genexus_inspect`.\n" +
                "- Cross-object reasoning: `genexus_analyze`.\n\n" +
                "## Notes\n" +
                "- `impact` waits up to 30s for the index to be ready unless `waitForIndex: false`; set `waitTimeoutMs` to override that wait bound.\n" +
                "- Returns `callersTruncated: true` and `_meta.partial` when the graph is incomplete.\n\n" +
                "## Examples\n" +
                "- `{ mode: 'impact', target: 'InvoiceProc' }`\n" +
                "- `{ mode: 'summary', target: 'OrderTrn' }`\n",

            ["genexus_variable"] =
                "# genexus_variable\n\n" +
                "Add, delete, or modify variables in an object's Variables part.\n\n" +
                "## Required\n" +
                "- `action` — `add`, `delete`, or `modify`\n" +
                "- `name` — object that owns the variable\n" +
                "- `varName` — variable name, including `&` when that is how the KB stores it\n" +
                "- `typeName` or `newTypeName` — replacement type for `add`/`modify` (`dataType` is also accepted as a legacy alias)\n\n" +
                "## Optional\n" +
                "- `basedOn` — domain name for compatible typed variables (`Attribute:<name>` also binds an attribute)\n" +
                "- `basedOnAttribute` — attribute name (or `Attribute:<name>`) binding the variable by native SDK identity, preserving picture/semantics (e.g. `9999999999` vs `ZZZZZZZZZ9`)\n" +
                "- `typeName: 'Attribute:<name>'` — same attribute binding via the type slot; `variables[]` items accept `basedOn`/`basedOnAttribute` too\n" +
                "- `async: true` returns immediately with `operationId` / `job_id`; poll `genexus_lifecycle action=status|result target=op:<id>` for completion.\n\n" +
                "## Notes\n" +
                "- GAM / WWP+ framework-managed variables are protected and return a refusal instead of mutating them.\n" +
                "- `modify` preserves the variable name and description while changing the type atomically.\n" +
                "- `modify` refuses to silently drop an `Attribute:` binding (`AttributeBindingWouldBeLost`); pass `basedOnAttribute` to preserve or retarget it.\n" +
                "- Untyped `add` inherits a same-named attribute *with its binding*; reads (`genexus_read part=Variables`, `genexus_inspect include=[\"variables\"]`) surface `basedOn`/`basedOnAttribute`.\n\n" +
                "## Examples\n" +
                "- `{ action: 'add', name: 'InvoiceProc', varName: '&Total', typeName: 'Numeric(10.2)' }`\n" +
                "- `{ action: 'modify', name: 'InvoiceProc', varName: '&State', newTypeName: 'Character(20)', async: true }`\n" +
                "- `{ action: 'delete', name: 'InvoiceProc', varName: '&ScratchFlag' }`\n",

            ["genexus_read"] =
                "# genexus_read\n\n" +
                "Read source or metadata parts of one or more GeneXus objects.\n\n" +
                "## Required\n" +
                "- Either `name` (single) **or** `targets` (array). Never both.\n" +
                "- `parts`: array of part names. Common: `Source`, `Variables`, `Rules`, `Events`, `Structure`, `Layout`. Omitting `parts` returns the canonical default set for the object type.\n\n" +
                "## Data Selectors\n" +
                "- Use `type: 'DataSelector'` with `parameters`, `conditions`, `orders`, `definedBy`, `baseTransaction`, `baseTable`, or `structure`. The SDK order and complete expressions are preserved.\n" +
                "- `projection` and resolved `joins` are not exposed by the GeneXus 18 U16 public SDK. Requests return a technical reason in `unsupportedParts`, never a misleading empty value.\n" +
                "- Base table and Transaction are returned only when attribute coverage resolves them unambiguously. Declared indexes may be listed, but this read never runs Specify to claim which index is used.\n\n" +
                "- `structure.expressionKind: 'semanticProjection'` means the complete view was composed from typed SDK elements; U16's raw structure `ToString()` is not returned because it leaks internal collection type names.\n\n" +
                "## Pagination\n" +
                "- `offset` and `limit` apply to the **source** part for large objects.\n" +
                "- `_meta.partial: true` and `_meta.nextOffset` signal more content available.\n\n" +
                "## Examples\n" +
                "- `{ name: 'InvoiceProc', parts: ['Source', 'Variables'] }`\n" +
                "- `{ name: 'OrderTrn', parts: ['Rules'], offset: 0, limit: 200 }`\n" +
                "- `{ name: 'OrderFilter', type: 'DataSelector', parts: ['parameters', 'conditions', 'orders', 'definedBy', 'baseTable'] }`\n" +
                "- `{ targets: [{ name: 'A' }, { name: 'B' }], parts: ['Source'] }`\n",

            ["genexus_apply_pattern"] =
                "# genexus_apply_pattern\n\n" +
                "Apply a GeneXus pattern to a KBObject — equivalent to the IDE's `Right-click → Apply Pattern` menu. " +
                "Registered patterns are discovered from the installation's `Packages\\Patterns\\*\\*.Pattern` manifests (for example `WorkWithPlus`, alias `WWP`, or K2BTools `K2BEntityServices`); an unknown key returns `availablePatterns`. Omit `pattern` only with `reapply=true` to infer it from the existing instance. " +
                "WorkWithPlus keeps its dedicated route below. Other patterns use the generic pattern-engine route: the manifest's `ParentObjects` gate the target type, `reapply=true` takes the pattern from the existing instance (a different `pattern` is `PatternMismatch`, several instances without `pattern` are `PatternInstanceAmbiguous`), and a route that is not supported returns `PatternRouteUnsupported` (`mode=diagnose` reports it as a critical `routeUnsupported` finding). First apply generates the instance and its objects; headless reapply of a non-WorkWithPlus pattern does not regenerate its objects, so it returns `PatternRouteUnsupported` - apply the pattern in the GeneXus IDE to regenerate. " +
                "Existing instances of any pattern are read and edited with `genexus_read` / `genexus_edit part=PatternInstance`.\n\n" +
                "## When to use this — and when NOT to\n" +
                "**Use this** any time the user asks for a WorkWithPlus / Work With Plus / WWP screen on a new or existing object. " +
                "`apply_pattern` is the *only* path that creates a real `PatternInstance` — once that exists, `genexus_edit part=PatternInstance` can shape columns, actions, filters, orders, grids, themes, etc.\n\n" +
                "**Do NOT** try to recreate a WWP screen by editing `WebForm` / `Layout` directly. The HTML generator will compile fine, but the result is a hand-built page that lacks WWP's grid/filter/action infrastructure. " +
                "If a target object already has `PatternInstance`, edit *that* part instead of `WebForm` — see `EditingWebFormUnderPattern` warning surfaced by `genexus_edit`.\n\n" +
                "## Two real target shapes — both work\n\n" +
                "### A) Transaction target — generates the WW family\n" +
                "Classic CRUD-around-an-entity flow. The engine emits:\n\n" +
                "- `WorkWithPlus<Trn>` — pattern host (edit `PatternInstance` here to shape the screen)\n" +
                "- `WW<Trn>` — Selection WebPanel (list view)\n" +
                "- `View<Trn>` — detail WebPanel\n" +
                "- `ExportWW<Trn>` / `ExportReportWW<Trn>` — export procedures\n\n" +
                "```jsonc\n" +
                "{ \"name\": \"Invoice\", \"pattern\": \"WorkWithPlus\" }\n" +
                "// Generates: WorkWithPlusInvoice + WWInvoice + ViewInvoice + ExportWWInvoice + ExportReportWWInvoice\n" +
                "```\n\n" +
                "### B) WebPanel, WebComponent or SDPanel target — in-place attach + auto-project (SOTA path)\n" +
                "For custom WWP screens that aren't pure CRUD (queries, dashboards, hybrid lists and reusable components). The MCP attaches a `WorkWithPlus<ObjectName>` host bound to the original object via the SDK's `PatternInstancePackageInterface.CreatePatternInstanceWithTemplate`, then immediately runs `IPatternBuildProcess.UpdateParentObject` so its WebForm reflects the pattern projection. The original object keeps its type and name.\n\n" +
                "Required: `settings.template` matching a `WorkWithPlus for Web Template` object in your KB. Common names: `MatIsoTemplate`, `TransactionResp2`, `PopoverEmpty`, `TransactionPopUp`. The MCP auto-discovers one if you omit, but explicit is better.\n\n" +
                "```jsonc\n" +
                "{ \"name\": \"InvoiceQueryPanel\",\n" +
                "  \"pattern\": \"WorkWithPlus\",\n" +
                "  \"settings\": { \"template\": \"MatIsoTemplate\" } }\n" +
                "// → status: Success, directAttach: true, template: \"MatIsoTemplate\"\n" +
                "// → patternHost: \"WorkWithPlusInvoiceQueryPanel\" (host with editable PatternInstance)\n" +
                "// → InvoiceQueryPanel.WebForm now contains the template-derived layout\n" +
                "```\n\n" +
                "**Auto-project on edit:** subsequent `genexus_edit name=WorkWithPlus<X> part=PatternInstance` calls automatically run UpdateParentObject too — every PatternInstance edit lands on the parent object's WebForm in the same call. The response's `projection.status` field reports the outcome.\n\n" +
                "## Typed PatternInstance actions\n" +
                "Use `mode: 'actions'` for structural WWP edits. `action: 'add_user_action'` inserts a form-level UserAction directly under `containerName` (normally `TableActions`) and derives the event as `Do<actionName>`. Example: `{ name: 'WorkWithPlusImpressaoConfiguracao', pattern: 'WorkWithPlus', mode: 'actions', action: 'add_user_action', containerName: 'TableActions', actionName: 'BaixarConfiguracao', caption: 'Baixar Configuração', dryRun: true }`. The typed path persists and re-reads the PatternInstance when `dryRun` is false, verifies the projected state, attempts rollback when `rollbackOnFailure` is true, and never runs Specify, Generate, Build, Rebuild, compilation, publication, execution, or tests.\n\n" +
                "## Response\n" +
                "- `{ status: \"Success\", wasFirstApply: true|false, generatedObjects: [...] }` on the happy path.\n" +
                "- `{ status: \"pattern_unavailable\", message: ... }` if `Artech.Packages.Patterns.dll` / license is missing — the call is **non-fatal**, treat as \"feature unavailable on this install\" and surface the message.\n" +
                "- `{ status: \"Error\", error: ... }` on SDK exceptions.\n\n" +
                "## Re-apply\n" +
                "Pass `reapply: true` to regenerate over an existing instance — equivalent to `Right-click → Apply Pattern` on an already-patterned object. If no instance exists, it falls back to first-time apply automatically.\n\n" +
                "## Settings\n" +
                "The `settings` JObject is **best-effort-projected** onto the SDK's `ApplySettings` instance on re-apply (case-insensitive property match, recursive on nested objects). Mismatched keys are silently dropped and logged — they don't fail the call.\n\n" +
                "Caveats: (a) projection only fires on `reapply: true`; first-time apply uses the SDK's void overload which has no settings slot. (b) the canonical names of `ApplySettings` properties are pattern-internal; if your projection doesn't take effect, fall back to shaping the result via `genexus_edit part=PatternInstance` after apply.\n",

            ["genexus_create"] =
                "# genexus_create\n\n" +
                "Create a new empty GeneXus object in the active KB (`action: object`, the default). The action may be omitted when the payload contains `name` + `type`/`objectType`; source/rules/variables/parms infer `object_atomic`. The tool covers every KBObject the IDE can create — both objects with a typed wrapper (Transaction, Procedure, WebPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, ExternalObject, Theme, Image, Menu, Menubar, Stencil, UserControl, WorkPanel, Report, API, URLRewrite, MiniApp, SuperApp, DesignSystem, ColorPalette, OfflineDatabase, DataView, Group, Language) and Guid-only types (SDPanel, Dashboard, Query, QueryDashboard, WorkflowDiagram, ConversationalFlows, TestSuite, ThemeClass, ThemeColor, ThemeTransformation, DesignSystemClass, WorkWithDevices, WorkWithWeb, WikiPageKBObject, TranslationMessage, DataStoreCategory, GeneratorCategory, DeploymentUnitCategory).\n\n" +
                "Aliases accepted: `StructuredDataType`→SDT, `BusinessProcessDiagram`/`BPD`→WorkflowDiagram, `PanelForSD`→SDPanel.\n\n" +
                "## Defaults that get seeded\n" +
                "- `Transaction` — gets a default `<Name>Id : Numeric(4) [Key]` attribute so the SDK accepts the empty save; pass `firstItem`/`firstItemType` to choose the initial key.\n" +
                "- `SDT` — gets a default `Item1 : VARCHAR(40)` item.\n" +
                "- `Procedure` / `DataProvider` — empty source with a `// Procedure: <Name>` header.\n" +
                "- `Domain` — defaults to `Character(20)` when no `dataType` is supplied.\n\n" +
                "When the response carries `_meta.seeded`, the caller knows what's already there and can decide whether to overwrite (`genexus_edit part=Structure mode=full`).\n\n" +
                "## Domain (new)\n" +
                "Pass either a primitive shape (`dataType` + `length`/`decimals`/`signed`) or `basedOn=<existingDomain>`. For an **enumerated domain**, add `enumValues=[{name,value}...]`.\n" +
                "- The `value` for a Character/VarChar domain must be a quoted literal — e.g. `\"\\\"A\\\"\"` (string `\"A\"`). For Numeric, pass the literal number as a string (`\"1\"`).\n" +
                "- Response `_meta` echoes back `dataType`, `length`, `enumValues` (and `enumError`/`typeError` if any step failed best-effort).\n" +
                "- Replace attributes by the new domain via `genexus_edit name=<Attr> part=Structure` setting `DomainBasedOn=<DomainName>`, then `genexus_delete_object` for the now-redundant attributes.\n\n" +
                "Example — exactly the Edgar `UserStatus` case:\n" +
                "```json\n" +
                "{\n  \"type\": \"Domain\",\n  \"name\": \"UserStatus\",\n  \"dataType\": \"Character\",\n  \"length\": 10,\n  \"enumValues\": [\n    {\"name\":\"Active\",   \"value\":\"\\\"A\\\"\", \"description\":\"Cuenta Normal\"},\n    {\"name\":\"Inactive\", \"value\":\"\\\"I\\\"\", \"description\":\"Cuenta inactiva\"},\n    {\"name\":\"Blocked\",  \"value\":\"\\\"B\\\"\", \"description\":\"Bloqueada por exceso de intentos\"}\n  ]\n}\n```\n\n" +
                "## WebPanel / SDPanel hint\n" +
                "An empty WebPanel is just a blank page — it has **no WorkWithPlus pattern** by default. " +
                "If the goal is a WWP-style screen (list with filters, actions, grid), the next call should be `genexus_apply_pattern name=<X> pattern=WorkWithPlus`, then shape via `genexus_edit part=PatternInstance`. " +
                "The response surfaces this in `_meta.patternHint` so the agent doesn't drift into editing `WebForm` by hand.\n\n" +
                "For popup-style WebPanels with structured inputs/buttons, prefer `genexus_create_popup` — it emits a fully-wired popup in one call.\n\n" +
                "## More examples\n" +
                "- `{ type: \"Transaction\", name: \"Invoice\" }`\n" +
                "- `{ type: \"WebPanel\", name: \"InvoiceList\" }` — then call `genexus_apply_pattern` if WWP is wanted.\n" +
                "- `{ type: \"Procedure\", name: \"BillingCalc\" }`\n" +
                "- `{ type: \"Domain\", name: \"Email\", dataType: \"VarChar\", length: 100 }`\n" +
                "- `{ type: \"Domain\", name: \"Age\", basedOn: \"PositiveInt\" }`\n" +
                "- `{ type: \"WorkflowDiagram\", name: \"ApprovalFlow\" }`\n" +
                "- `{ type: \"Dashboard\", name: \"SalesKpis\" }`\n",

            ["genexus_edit_and_build"] =
                "# genexus_edit_and_build\n\n" +
                "Edit an object and rebuild its callers in one call.\n\n" +
                "## Required\n" +
                "- `name` — object to edit\n" +
                "- `part` — which part (e.g., `Source`, `Rules`)\n" +
                "- `content` — full text or unified diff\n\n" +
                "## Optional\n" +
                "- `mode` — `patch` (default) or `full`\n" +
                "- `type` — disambiguates when name matches multiple objects\n" +
                "- `dryRun` — preview without persisting (default `false`)\n" +
                "- `buildIncludeCallees` — `none` | `direct` (default) | `transitive`; the edit and rebuild use the same selected worker/context.\n" +
                "- `buildPlanCap` — max build-plan size (default 200)\n\n" +
                "## Response\n" +
                "Returns a composite envelope with three blocks. The edit is committed before caller rebuild is queued, so a queue failure is partial/uncertain and must not cause the edit to be replayed:\n" +
                "- `edit` — the diff from genexus_edit\n" +
                "- `impact` — output of genexus_analyze mode=impact (callers, risk, etc.)\n" +
                "- `build` — `{ taskId|TaskId, status: 'Accepted'|'Running', pollTarget }` for async caller rebuild, or `{ status: 'Skipped' }` when no callers\n\n" +
                "Poll the worker build via its returned `taskId`/`pollTarget`; do not assume it is a gateway `op:<id>` job.\n\n" +
                "## Errors\n" +
                "If `name` matches multiple objects, the edit phase aborts and the envelope returns `status=Error` with an `alternatives` array — retry with one of the (`name`, `type`) pairs.\n\n" +
                "## Example\n" +
                "`{ name: 'InvoiceProc', part: 'Source', mode: 'patch', content: '<diff>', buildIncludeCallees: 'direct' }`\n",

            ["genexus_db"] =
                "# genexus_db\n\n" +
                "Umbrella tool for datastore/index/DDL/sample-data actions. This entry covers the static index-advisor actions (`optimize_analyze|optimize_suggest|optimize_report`) — walks every Procedure / WebPanel / DataProvider Source + Events part, regex-parses `For each` blocks, derives (Transaction × where-signature × sort) access patterns, then surfaces concrete optimization opportunities. It also exposes typed Transaction-record reads and guarded writes.\n\n" +
                "## Actions\n" +
                "- `optimize_analyze [target=<Tx>]` — KB-wide pattern scan. Returns `{transactions:[{name, accessPatterns:[{whereSignature, callerCount, sortAttributes, samples:[...]}]}]}` sorted by callerCount desc. `target` is an optional filter.\n" +
                "- `optimize_suggest target=<Tx>` — for one Transaction, proposes covering indexes for the top where-signatures that are NOT covered by an existing index. Returns `{existingIndexes:[...], suggestedIndexes:[{columns, rationale, coveredQueries, estimatedBenefit, confidence, ddl}], redundantIndexes:[{name, reason}]}`. DDL is paste-ready (`CREATE INDEX IX_Tx_A_B ON Tx (A, B);`).\n" +
                "- `optimize_report [format=markdown|json]` — top-10 unindexed hot paths across the whole KB ranked by callerCount. `format=markdown` adds a paste-ready table under `report`.\n\n" +
                "- `records_query` — read typed rows from the Transaction's root table using equality-only `where`, optional root-attribute `fields`, a bounded `limit` (1–1000), and `timeoutSeconds` (1–60). `dataStoreAlias` may select a SQL Server connection alias declared in the active MCP profile; the response includes masked connection metadata, `elapsedMs`, `rowCount`, `rows`, and a configuration health confirmation without credentials.\n" +
                "- `records_insert` / `records_update` — validate typed values and return a complete preview by default. Persistence requires `dryRun=false` plus the matching single-use v2 preview token from the same operation; snapshots and verification use a serializable database transaction.\n\n" +
                "## Record safety boundary\n" +
                "These actions execute parameterized SQL against the existing physical table resolved from SDK metadata. They do not run GeneXus business rules, BC events, Specify, Generate, Build, Reorg, or application triggers beyond what the database itself enforces. A commit timeout is indeterminate: do not repeat the write blindly.\n\n" +
                "## Where-signature canonicalisation\n" +
                "Two queries `Where AluCod = &c` and `Where AluCod = 1` collapse to the same signature `AluCod`. Literals and variables (&...) are stripped; only attribute references survive. Order is alphabetical so `{A,B}` and `{B,A}` collide.\n\n" +
                "## Confidence\n" +
                "Each finding carries `confidence: high|medium|low`. `low` means the For each lacked a Transaction name or a Where clause and the parse fell back to heuristic — treat those as leads, not actions.\n\n" +
                "## Index coverage\n" +
                "A multi-column index `(A, B, C)` covers any where-signature that is a strict prefix — `{A}` and `{A, B}` are covered, `{B}` is not. The advisor never suggests indexes that already exist as a prefix.\n\n" +
                "## Examples\n" +
                "- `{ action: 'optimize_analyze' }` — every transaction with at least one For each in the KB.\n" +
                "- `{ action: 'optimize_suggest', target: 'Aluno' }` — covering DDL for the hottest Where signatures on `Aluno`.\n" +
                "- `{ action: 'optimize_report', format: 'markdown' }` — paste into a code review.\n",

            ["genexus_structure"] =
                "# genexus_structure\n\n" +
                "Read or write the structure/data-model of GeneXus objects.\n\n" +
                "## Actions\n" +
                "- `get_visual` — returns logical hierarchy of Transaction levels/attributes or SDT structure. Use `type` (e.g. `Transaction`) to disambiguate name collisions.\n" +
                "- `update_visual` — replace complete logical structure of a Transaction or SDT. Atomic snapshot, verification, and rollback on divergence.\n" +
                "- `move_attribute` — reorder an attribute within a Transaction level using `before`, `after`, or `position`. Nested levels supported via `levelPath`.\n" +
                "- `remove_attribute` — remove an attribute from a Transaction level by name.\n" +
                "- `get_indexes` / `create_index` / `drop_index` — inspect and manage indexes on physical tables or transactions.\n" +
                "- `set_attribute` — modify global attribute metadata (Domain, Formula, Description). Note: does not alter Transaction level composition; use update_visual/move_attribute/remove_attribute for that.\n" +
                "- `set_level` — update Transaction level properties such as descriptionAttribute.\n" +
                "- `set_domain` — define or alter a Domain, including enumValues.\n" +
                "- `get_logic` — extract rules and events summary for a Transaction.\n" +
                "- `update_group` — manage subtype group members and relationships.\n" +
                "- `check_subtypes` — validate subtype consistency and detect circular or misconfigured subtype relations.\n\n" +
                "## Concurrency & Safety\n" +
                "All mutating operations support `dryRun: true`, `baseVersion` / `expectedVersion` optimistic locking, and `rollbackOnFailure: true`.\n",

            ["genexus_layout"] =
                "# genexus_layout\n\n" +
                "Inspect and modify WebForm and layout control trees.\n\n" +
                "## Actions\n" +
                "- `get_tree` — dump hierarchical control tree for a WebPanel or Transaction WebForm.\n" +
                "- `find_controls` — search controls by name, caption, or query string.\n" +
                "- `list_controls` — list all controls with type and key attributes.\n" +
                "- `set_property` — set single property on a layout control (Caption, Visible, Enabled, Class).\n" +
                "- `set_properties` — batch property updates via `changes: [{control, propertyName, value}]`.\n" +
                "- `inspect_surface` — analyze layout structure, grid bindings, and responsive layout rows.\n" +
                "- `get_preview` — get visual representation or HTML mockup preview.\n" +
                "- `scan_mutators` — inspect potential mutations and event-binding risks.\n" +
                "- `add_printblock` / `rename_printblock` / `delete_printblock` — manage Procedure printblocks.\n" +
                "- `design_system` — inspect applied design system tokens and styling.\n",

            ["genexus_versioning"] =
                "# genexus_versioning\n\n" +
                "KB version history, git integration, and rollback umbrella.\n\n" +
                "## Actions\n" +
                "- `history_list` — list SDK versions plus KB-scoped edit snapshots; legacy shared `.history` files are visible but marked non-restorable. Pass `part` (or legacy alias `partName`) to scope a part.\n" +
                "- `history_get` — retrieve source of a specific historic `versionId` and requested part; unsupported parts are rejected explicitly.\n" +
                "- `history_save` — explicitly snapshot the current part under the active KB's isolated snapshot root.\n" +
                "- `history_restore` — restore a prior KB-scoped snapshot or the explicit SDK `versionId`; pass `discard: true` for IDE 'Discard changes' parity. Legacy shared snapshots are reported but never selected automatically.\n" +
                "- `undo` — revert the last N edits performed via MCP.\n" +
                "- `time_travel` — recover object bytes from past git commits (`at: '<sha/ISO>'`).\n" +
                "- `blame` — git blame annotations for object parts or files.\n" +
                "- `diff` — compute textual diff between versions or arbitrary text chunks (`mode: textVsText|currentVsText`).\n" +
                "- `diff_generated` — diff generated code against last build or git HEAD.\n",

            ["genexus_io"] =
                "# genexus_io\n\n" +
                "Asset management, file I/O, and part exchange umbrella.\n\n" +
                "## Actions\n" +
                "- `asset_find` — search files in KB or target directories matching a glob `pattern`.\n" +
                "- `asset_read` — read asset file content (text or binary bytes up to `maxBytes`).\n" +
                "- `asset_write` — write or update asset files using `contentBase64`.\n" +
                "- `read_blob` — read the real bytes of a `WikiFileKBObject`/`WikiBlobPart`; returns Base64 inline or exports atomically to `outputPath` with byte count and SHA-256. `overwrite=true` replaces an existing file; false returns `FileAlreadyExists`. A post-promotion verification failure returns `BlobVerificationFailed` with reconciliation metadata.\n" +
                "- `export_part` — export a single object part (e.g. Source, Rules) to an external file.\n" +
                "- `import_part` — import object part content from a file.\n" +
                "- `export_kb_to_text` — export selected objects, or the full indexed KB, into deterministic `.gxtext` files plus a manifest.\n" +
                "- `import_text_to_kb` — import that manifest; use `dryRun: true` to validate without creating or saving objects.\n" +
                "- `validate_kb_text_files` — validate manifest files and, when the target exists, exercise the SDK import preflight.\n" +
                "- `list_text_files` / `validate_text_in_memory` — inspect an SDK text tree under `src/`/`ref/` without opening a KB; XML is parsed safely and object headers, duplicates, manifest paths and hashes are reported.\n" +
                "- Set `format: native` on export/import/validate to use the MCP's SDK-backed `src/`/`ref/` tree. `part: all` (or `parts: [...]`) serializes the authored textual parts into one sectioned `.gx` document; `mode: newAndModified|newOnly` avoids rewriting unchanged files using the indexed SDK version token; it does not load any GX4A DLL. Native export can emit `module.toml` with `includeModuleMetadata`, copy matching official `.opc` packages with `includeModulePackages`, and emit official SDK Transaction root-table structures under `#tables` with `includeTableProjections`; `indentString` is opt-in so the default preserves source bytes.\n" +
                "- File-system parity options are available on batch actions: `listOnly` returns the plan without SDK/file writes, `skip` resumes a deterministic batch, `stopOnError` leaves an explicit remainder, `includeChildren` expands module/folder trees, `ignore` excludes selectors/files, `forceSave` forwards import persistence to the official writer, and multi-part native imports roll back prior part writes by default.\n" +
                "- `text_mirror_start` — start a watermark-backed incremental mirror.\n" +
                "- `text_mirror_stop` — stop the mirror idempotently.\n" +
                "- `text_mirror_status` — inspect pending changes, batches, watermark, and errors.\n" +
                "- `text_mirror_catchup` — reconcile queued changes or the full indexed KB.\n" +
                "- `text_mirror_set_references` — enable/disable reference export. Watcher callbacks only enqueue identities; SDK reads happen on the Worker's owning STA.\n" +
                "- `delete_kb_objects` — delete selected objects in a batch; requires `confirm: true`, and supports `dryRun: true`.\n" +
                "- `export_unified` — export complete object envelope as a portable JSON file.\n" +
                "- `screenshot_publish` — publish screenshot PNG into `.gx/published-screenshots`.\n" +
                "- `ocr` — optical character recognition on image assets.\n",

            ["genexus_kb_version"] =
                "# genexus_kb_version\n\n" +
                "Manage KB model versions and development branches via the SDK's KBVersionHelper. `changed_objects` is a read-only Design-versus-frozen inventory and never uses SQL/internal tables.\n\n" +
                "## Actions\n" +
                "- `list` — enumerate all versions and branches in the KB version tree.\n" +
                "- `changed_objects` — inventory NEW/CHANGED named objects in the active Design model against a frozen `fromVersion`; omit `fromVersion` to use the latest frozen version. Results are stable, paginated with `offset`/`limit`, include GUID/entity-key identity when available, and select candidates by `KBObject.LastUpdate` before confirming content with `IComparerService.AreEqualInContent` (the SDK Compare/IDE Compare family). The response exposes `baselineSource` and `changeDetection`; it does not reuse the optimistic write token or claim that `genexus_compare` compares Design against frozen. This is not the same as `genexus_list_objects since=`; it is the XPZ/Parte N inventory use case. If the installed SDK cannot expose the frozen model or content comparer, the action returns stable `ChangedObjectsNotSupported` rather than falling back to SQL.\n" +
                "- `freeze` — freeze current version into an immutable baseline (`name`, `description`, `parentVersion`).\n" +
                "- `branch` — create a new parallel branch from a parent version (`name`, `includeEnvironments`).\n" +
                "- `set_active` — switch the active development version/branch (`targetVersion`, `autoUpdate`).\n" +
                "- `revert` — revert working model changes back to a baseline version.\n\n" +
                "## Timestamp semantics\n" +
                "Version results expose `lastUpdate` from `KBVersion.LastUpdate`. `createdAt` is null and `createdAtAvailable=false` when the SDK does not expose a reliable creation timestamp; never interpret `lastUpdate` as creation time.\n",

            ["genexus_doc"] =
                "# genexus_doc\n\n" +
                "Generate structured documentation and visual assets for Knowledge Base objects.\n\n" +
                "## Actions\n" +
                "- `wiki` — generate complete Markdown wiki pages for target objects and modules. Writes documentation files to disk under the durable per-KB artifact directory. The response returns `result.file` and `result.outputDirectory`.\n" +
                "- `visualize` — generate dependency and call graphs (Mermaid / visual format) for target objects. Each response writes a unique HTML file and returns its `result.url` plus `result.outputDirectory`.\n" +
                "- `health` — compile a KB-wide or object-specific health report evaluating code metrics, dead code, and documentation coverage from the active KB's canonical IndexCacheService snapshot.\n\n" +
                "## Operational Notes\n" +
                "Generated files are scoped below `%LOCALAPPDATA%\\GxMcp\\Artifacts\\kb-<identity>` by default, with separate `docs` and `html` directories. Set `Server.ArtifactOutputDirectory` in config.json (or `GXMCP_ARTIFACT_OUTPUT_DIR` for a directly launched Worker) to choose another root; the Worker still adds the per-KB scope. The root is outside the installed Worker directory, so package upgrades do not hide prior artifacts.\n" +
                "`action=wiki` writes documentation files to disk; it is not purely in-memory read-only. Object names are validated as single path components; path separators and traversal are rejected rather than sanitized.\n",

            ["genexus_recipe"] =
                "# genexus_recipe\n\n" +
                "Named playbooks, macro discovery, and repeatable workflow automation.\n\n" +
                "## Actions\n" +
                "- `list` — list all available recipes, built-in playbooks, and crystallized user macros.\n" +
                "- `describe` — display detailed step documentation and prerequisites for a named recipe (`name: 'wwp_on_transaction'`).\n" +
                "- `suggest_macro` — analyze recent session command telemetry to detect repeated multi-step patterns worthy of automation.\n" +
                "- `crystallize` — convert an observed or explicit sequence of tool calls (`steps`) into a named, permanent recipe (`macroName`, `description`).\n",

            ["genexus_refactor"] =
                "# genexus_refactor\n\n" +
                "Automated refactoring operations across Knowledge Base objects.\n\n" +
                "## Actions\n" +
                "- `RenameObject` — rename a KBObject and automatically patch references across all calling objects. Use `type` (e.g. `WebPanel`, `Transaction`) when names are shared.\n" +
                "- `RenameAttribute` — rename an attribute and patch occurrences in structures, rules, and sources.\n" +
                "- `RenameVariable` — rename a variable within a specific object (`objectName`).\n" +
                "- `ExtractProcedure` — extract a highlighted code block (`code`) into a newly created Procedure (`procedureName`), wiring parameters automatically.\n" +
                "- `ExtractSubroutine` — extract code into a local Subroutine (`subroutineName`).\n" +
                "- `WWPSetCondition` — set conditions on WorkWithPlus grid or form controls.\n\n" +
                "Always run with `dryRun: true` first to review affected call sites and projected diffs.\n",

            ["genexus_sdk_probe"] =
                "# genexus_sdk_probe\n\n" +
                "Inspect the installed GeneXus SDK without pretending reflection is authoring support.\n\n" +
                "## Modes\n" +
                "- `surface` (default) writes the diagnostic type/method/property dump to `outputDir` (or the default `docs/sdk-probe/`); this is a file-writing, installation/worker-scoped operation.\n" +
                "- `capabilities` is read-only and returns an in-memory capability matrix with signature-probe status and evidence. A reflected type is not proof that an authoring or persistence path is supported.\n" +
                "This tool does not open/select a KB and does not grant a KB lease. Do not use it to infer KB context or to authorize another KB-bound operation.\n\n" +
                "## Example\n" +
                "- `{ mode: 'capabilities' }`\n",

            ["genexus_connection_recover"] =
                "# genexus_connection_recover\n\n" +
                "Recover unhealthy gateway workers after calls hang, the connection closes, or repeated `WorkerBusy` responses occur.\n\n" +
                "## Contract\n" +
                "- `action: journal_status` inspects the durable mutation journal without touching a Worker.\n" +
                "- `action: journal_repair` defaults to `dryRun: true`; explicitly set false to atomically merge valid journal/candidate fences and verify the committed file. It never discards pending reads or fixes corrupt/foreign entries by deleting them. Do not combine with force.\n" +
                "- Default (`force: false`) probes every open worker and replaces only unhealthy workers.\n" +
                "- `force: true` deliberately recovers all open workers, including responsive ones; use it only as an explicit administrative action.\n" +
                "- This is a gateway/process operation, not an ordinary KB edit. It has no KB selector or per-KB lease input in the published schema, and it clears semantic cache after recovery.\n" +
                "- A worker duplicate-lock condition is `KB_LOCKED`; do not retry blindly or expect proxy fallback.\n\n" +
                "## Example\n" +
                "- `{ force: false }`\n" +
                "- `{ force: true }` — recover all workers\n",

            ["genexus_kb"] =
                "# genexus_kb\n\n" +
                "Manage the gateway's multi-KB pool and the startup fallback selected for future sessions.\n\n" +
                "## Read actions\n" +
                "- `list` — show selected, active, default, open, known, and declared KB aliases.\n" +
                "- `list_environments` / `get_environment` — inspect environment metadata.\n" +
                "- `get_startup` — read the persisted startup selection.\n\n" +
                "## Mutating actions\n" +
                "- `create` — create a brand new GeneXus Knowledge Base from scratch using native MSBuild tasks (supports path, name, alias, major, template, dbServer, dbName, openAfterCreate, persist, dryRun).\n" +
                "- `open` / `close` — register or release a Worker and KB lease. `open` accepts per-KB `driver`, `installationPath`, and `major` overrides; use `driver: com-gxpublic` for classic GX8/GX9 KBs.\n" +
                "- `select` / `set_session_default` — select a KB for the current session only without mutating config.json.\n" +
                "- `set_default` / `set_startup` / `set_environment` — change session or persisted selection (set_default with persist: false acts like select).\n\n" +
                "Use an explicit `kb` alias when a call must target a different open KB; do not rely on shared server-side selection between independent clients. In strict mode, `open`/`close` without the caller's lease fail with `KB_NOT_OWNED`; `select` is session-only and sessionless HTTP returns `KB_SESSION_UNAVAILABLE`.\n",

            ["genexus_data_view"] =
                "# genexus_data_view\n\n" +
                "Author a root-only Business Component Transaction mapped through a native Data View.\n\n" +
                "## Actions\n" +
                "- `inspect` — read the existing mapping, attributes, keys, and version token.\n" +
                "- `dry_run` — validate a proposed mapping without persistence.\n" +
                "- `create` / `update` — validate, save, re-read, and report the committed mapping.\n" +
                "- `delete` — destructive removal; pass the required confirmation and review incoming references first.\n\n" +
                "Use the returned version token as `baseVersion` for optimistic concurrency. A failed verification rolls back the SDK write; a preview never changes the KB.\n",

            ["genexus_gam"] =
                "# genexus_gam\n\n" +
                "Inspect and provision GeneXus Application Management (GAM) integration through the native security service.\n\n" +
                "## Actions\n" +
                "- `status` — read the current GAM/environment security configuration.\n" +
                "- `define_api` — define or update the API security surface.\n" +
                "- `deploy` — apply the GAM deployment operation; confirm the target environment before using it.\n\n" +
                "Only `status` is read-only. Treat `define_api` and `deploy` as state-changing operations and inspect their response before continuing.\n",

            ["genexus_properties"] =
                "# genexus_properties\n\n" +
                "Read or change object-level GeneXus properties without editing the object source.\n\n" +
                "## Actions\n" +
                "- `get` — read current property values and version information. Filter with `propertyName` (name, comma-separated list, or * wildcard), `propertyNames[]`, `query` (search filter), or `projection` (minimal, standard, full; default full). `targets: [{name, type?}]` reads up to 100 objects in input order; each result has its own status/error.\n" +
                "- `set` — assign one or more named properties and verify the saved values.\n" +
                "- `move` — move an object to another module or folder.\n\n" +
                "`get` is read-only. `set` and `move` mutate the KB; use the version token when a concurrent IDE edit must not be overwritten.\n",

            ["genexus_authoring"] =
                "# genexus_authoring\n\n" +
                "Add members that are not covered by the generic object-structure DSL.\n\n" +
                "## Actions\n" +
                "- `add_external_method` — add a method to an External Object.\n" +
                "- `add_external_property` — add a property to an External Object.\n" +
                "- `add_menu_option` — add a menu option and its target.\n" +
                "- `add_condition` — add an authoring condition to the supported object.\n\n" +
                "All actions write object metadata. Resolve the target type first, send a complete typed payload, and use the returned verification details to confirm the SDK persisted the member.\n",

            ["genexus_navigation"] =
                "# genexus_navigation\n\n" +
                "Read the IDE-style navigation report for a GeneXus object.\n\n" +
                "## Action\n" +
                "- `view` — inspect navigation levels, referenced tables, filters, orders, and the report status.\n\n" +
                "The report is read-only with respect to source and generated artifacts, but a fresh result is also written to the per-KB navigation cache. A report with no levels is a valid `NoNavigationBlocks` result; a missing report is an error that should be investigated separately.\n",

            ["genexus_api"] =
                "# genexus_api\n\n" +
                "Inspect HTTP procedure/API endpoints and compare their route shape with a saved baseline.\n\n" +
                "## Actions\n" +
                "- `list`, `describe`, `routes_inspect`, and `diff_baseline` — read endpoint metadata or compare it with a baseline.\n" +
                "- `export_openapi` / `import_openapi` — export the current routes as OpenAPI 3 JSON or parse an OpenAPI document into a typed import blueprint; neither action mutates the KB.\n" +
                "- `snapshot` — persist the current endpoint set as a named baseline.\n" +
                "- `routes_clone` / `routes_update` — change API route metadata.\n\n" +
                "The first group is read-only; `snapshot`, `routes_clone`, and `routes_update` change state. Review the endpoint diff and version token before applying route changes.\n",

            ["genexus_security"] =
                "# genexus_security\n\n" +
                "Run security inspections against the KB, environment properties, and native GeneXus scanner.\n\n" +
                "## Actions\n" +
                "- `audit_gam` — inspect GAM and environment security configuration.\n" +
                "- `scan_secrets` — scan source for credential-like values.\n" +
                "- `scan_native` — invoke the installed native GeneXus Security Scanner when available.\n\n" +
                "All actions are read-only audits. Findings may contain sensitive locations or snippets; keep them in the current response and do not copy secrets into logs or commits.\n",

            ["genexus_edit_form"] =
                "# genexus_edit_form\n\n" +
                "Apply typed semantic edits to a WebForm control tree.\n\n" +
                "## Actions\n" +
                "- `add_textblock` and `add_button` — create controls with their captions and placement.\n" +
                "- `set_visibility` — change a control's visibility expression.\n" +
                "- `remove_control` — remove a named control after checking references.\n" +
                "- `wrap_in_fieldset` — wrap selected controls in a fieldset container.\n\n" +
                "Every action mutates the layout. Prefer a read of the authoritative WebForm/PatternInstance first and verify the persisted tree after the write.\n",

            ["genexus_k2b_designer"] =
                "# genexus_k2b_designer\n\n" +
                "Read and edit an already-active K2B WebPanel Designer in the open GeneXus IDE. " +
                "The IDE extension and MCP Worker must share GXMCP_K2B_IDE_PIPE, and the IDE must be started with GXMCP_K2B_IDE_KB set to the same KB.\n\n" +
                "Use inspect or tree to obtain node paths and a version. Use preview with operation, path, and the fields required by that operation to validate a change. " +
                "Then pass expectedVersion for set_property, add_node, move_node, or remove_node. Removal also requires confirm=true.\n\n" +
                "The target WebPanel must be open with Designer active and free of unsaved changes. " +
                "This tool cannot activate an inactive Designer or convert a legacy HW. " +
                "After a native IDE save, reread the Designer to confirm persistence.\n",

            ["genexus_module"] =
                "# genexus_module\n\n" +
                "Inspect and manage modules through the GeneXus Module Manager.\n\n" +
                "## Actions\n" +
                "- `list` — read installed Module objects from the SDK, deduplicated by GUID/EntityKey and returned with `parent`, `path`, `qualifiedName`, and description so homonymous namespaces remain distinguishable.\n" +
                "- `install` / `install_builtin` — add a module to the KB. Both accept `dryRun=true` for a read-only preview (package identity, dependencies, affected modules) without calling the SDK install; a verified repeat install is a safe no-op at the KB level.\n" +
                "- `update` / `restore` — update or restore an installed module through the SDK. `update` also accepts `dryRun=true` for a read-only preview.\n" +
                "- `list_modules_servers` — list configured module-server metadata without contacting remote catalogs; pass one returned name to `search_modules_in_servers` to bound network work.\n" +
                "- `package` — create an `.opc` package from a Module and its selected environments (`confirm=true`).\n" +
                "- `publish` — publish a package or installed Module to a configured module server (`server`, `confirm=true`).\n" +
                "- `add_modules_server` / `search_modules_in_servers` — manage and query the SDK's configured module servers.\n\n" +
                "`list` and `search_modules_in_servers` are read-only. The remaining actions can change the KB or external module-server state; inspect the returned result before continuing with build/validation.\n",

            ["genexus_gxserver"] =
                "# genexus_gxserver\n\n" +
                "Inspect GXserver/Team Development state and perform explicit synchronization operations.\n\n" +
                "## Read actions\n" +
                "`status`, `pending`, `ignored`, `conflicts`, `history`, `pipeline_list`, `pipeline_runs`, and `pipeline_output` inspect server or pipeline state.\n\n" +
                "## Mutating actions\n" +
                "`commit`, `update`, `lock`, `resolve`, `pipeline_run`, and `pipeline_abort` contact the server or change team-development state. Confirm the target and inspect conflicts before using them.\n",

            ["genexus_browser"] =
                "# genexus_browser\n\n" +
                "Run browser-based verification against a resolved GeneXus application URL.\n\n" +
                "## Actions\n" +
                "- `smoke` — check that the page loads and basic navigation works.\n" +
                "- `a11y` / `wcag` — run accessibility checks.\n" +
                "- `capture` — collect a screenshot or browser artifact.\n" +
                "- `cross` — exercise cross-browser verification.\n" +
                "- `preview` — inspect a preview surface.\n\n" +
                "The inspection actions are read-only with respect to the KB, but `preview` becomes state-changing when `buildFirst=true`, `updateBaseline=true`, or `capture` includes `screenshot`; those options can build or write browser artifacts. Use the response path and cleanup guidance.\n",

            ["genexus_telemetry"] =
                "# genexus_telemetry\n\n" +
                "Inspect MCP execution telemetry and maintain the explicit friction log.\n\n" +
                "## Actions\n" +
                "`executions`, `watch_event`, `friction_tail`, `learning_report`, `logs`, `profile_analyze`, `profile_hotspots`, and `profile_correlate` read telemetry or profiling data.\n\n" +
                "`friction_append` writes a new observation to the per-KB friction log. Keep the append payload concise and free of credentials or personal data.\n",

            ["genexus_memory"] =
                "# genexus_memory\n\n" +
                "Manage durable agent facts scoped to the active Knowledge Base.\n\n" +
                "## Actions\n" +
                "- `recall` / `list` — read relevant or stored facts.\n" +
                "- `save` — persist a new fact.\n" +
                "- `forget` — remove a fact.\n" +
                "- `promote` / `consolidate` — change fact status or merge related facts.\n\n" +
                "Only `recall` and `list` are read-only. Treat all other actions as persistent writes and avoid storing secrets, tokens, or unnecessary personal data.\n",

            ["genexus_transfer"] =
                "# genexus_transfer\n\n" +
                "Exchange complete GeneXus objects through native XPZ/import-export paths.\n\n" +
                "## Actions\n" +
                "- `export` — create an XPZ export, optionally including dependency closure.\n" +
                "- `inspect` — inspect an XPZ manifest without importing it.\n" +
                "- `import` — import the package into the active KB after validating its manifest.\n\n" +
                "`export` writes the requested XPZ artifact, `inspect` is read-only, and `import` mutates the KB. Review the output path and conflicts, and use a disposable or explicitly selected target KB for untrusted packages.\n",

            ["genexus_deploy"] =
                "# genexus_deploy\n\n" +
                "Resolve deployment targets and explicitly deploy an application.\n\n" +
                "## Actions\n" +
                "- `list_targets` — read the configured deployment targets and capabilities.\n" +
                "- `deploy` — execute deployment to the selected target; pass the required confirmation for a destructive operation.\n\n" +
                "Only `list_targets` is read-only. Deployment can change external systems and generated output, so verify the target, environment, and final plan before calling it.\n",

            ["genexus_generator_reference"] =
                "# genexus_generator_reference\n\n" +
                "Manage typed .NET generator references on a GeneXus object.\n\n" +
                "## Actions\n" +
                "- `list` — inspect current references.\n" +
                "- `dry_run_add` / `dry_run_remove` — validate a proposed change without saving.\n" +
                "- `add` / `remove` — persist a reference after managed-assembly validation.\n\n" +
                "The list and dry-run actions are read-only. Add/remove writes object metadata, uses optimistic concurrency when supplied, and verifies the complete post-save snapshot.\n",

            ["genexus_sandbox"] =
                "# genexus_sandbox\n\n" +
                "Manage an explicit filesystem sandbox without SDK dispatch.\n\n" +
                "## Actions\n" +
                "- `create` — clone a validated source KB into a sandbox.\n" +
                "- `remove` — remove the named sandbox after path validation.\n\n" +
                "Both actions mutate filesystem state and require an explicit target; no active KB fallback is used.\n",

            ["genexus_worker_pool"] =
                "# genexus_worker_pool\n\n" +
                "Manage gateway-side warm spare Workers.\n\n" +
                "## Actions\n" +
                "- `warm_spares` — configure the requested spare count without selecting or mutating a KB.\n\n" +
                "This changes gateway process state and is not a read-only operation.\n",

            ["genexus_wwp"] =
                "# genexus_wwp\n\n" +
                "Inspect and edit WorkWithPlus Action Groups, form-level actions, tabs, and grid attributes through the typed PatternInstance contract.\n\n" +
                "## Actions\n" +
                "- `list` — read the current action groups and ordered actions.\n" +
                "- `add_action`, `update_action`, `move_action`, and `remove_action` — change the WWP action model.\n" +
                "- `add_user_action` — add a form-level UserAction directly under a named form container (usually `TableActions`). The event is derived deterministically as `Do<actionName>`; do not pass a Procedure.\n" +
                "- `add_tab`, `move_tab`, and `remove_tab` — edit WebPanel tabs and typed nested controls.\n" +
                "- `set_table_type` — change only an existing WWP table's native `type` (`Regular` or `Responsive`) by path, preserving child identity and metadata with reread/rollback guards.\n" +
                "- `add_grid_attribute` — add one typed Attribute column without changing unrelated children.\n\n" +
                "- `replace_web_component_with_user_action` — use the U16 Patterns SDK to replace one existing form-level WebComponent with a UserAction DropDownComponent at an explicit path. The operation preserves the referenced Gxobject, snapshots the complete PatternInstance and parent projection, saves through the native element commands, re-reads, and rolls back on divergence.\n\n" +
                "- `settings_templates` includes embedded Settings templates and separate WorkWithPlus for Web Template objects linked to Settings/Main. `guid` identifies Settings; `template=wwp:<guid>` selects a separate template. Use returned paths, offset/limit, and the same baseVersion on subsequent pages.\n" +
                "- `settings_read` returns separate templates' stored XML attributes; offset=0, limit=0 also includes the exact XML. WWP default resolvers are not invoked. Embedded templates retain the SDK property projection.\n" +
                "- `settings_edit` with dryRun=true previews one property without mutation. Separate templates support an existing table themeClass only; textEdit preserves every character outside that attribute value. Metadata is protected. Real saves remain refused with SettingsIsolationUnverified.\n" +
                "For instance writes, preview with `dryRun`, pass the returned token as `baseVersion`, `expectedVersion`, or `versionToken`, and persist only after reviewing the typed diff. Instance writes require exact snapshots, re-read the PatternInstance, verify the parent WebForm projection, and roll back on divergence when `rollbackOnFailure` is true. No Specify, Generate, Build, Rebuild, compilation, reorganization, publication, execution, or tests are implicit.\n" +
                "Example: `{ action: 'add_user_action', name: 'WorkWithPlusImpressaoConfiguracao', containerName: 'TableActions', actionName: 'BaixarConfiguracao', caption: 'Baixar Configuração', dryRun: true }`. Run again with `dryRun: false` to persist after reviewing the diff; the response includes the re-read form container and derived event.\n"
        };

        internal static string? Get(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;
            return Get(toolName, McpRouter.FindToolDefinitionForHelp(toolName));
        }

        internal static string? Get(string toolName, JObject? definition)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;

            string canonical = toolName;
            string? curated = null;
            if (_helpTexts.TryGetValue(toolName, out var text))
            {
                curated = text + ContextLeaseContract + OperationClassifier.BuildHelpContract(toolName);
            }
            else if (McpRouter.TryRewriteLegacyTool(toolName, null, out var rewritten, out _)
                && _helpTexts.TryGetValue(rewritten, out var canonicalText))
            {
                canonical = rewritten;
                curated = canonicalText + ContextLeaseContract + OperationClassifier.BuildHelpContract(canonical);
            }

            if (definition == null) return curated;

            string description = definition["description"]?.ToString() ?? string.Empty;
            string schema = definition["inputSchema"]?.ToString(Formatting.Indented) ?? "{}";
            string heading = curated ?? $"# {canonical}\n";
            return heading
                + "\n\n## Complete tool definition\n\n"
                + "### Description\n\n" + description + "\n\n"
                + "### Input schema\n\n```json\n" + schema + "\n```\n";
        }

        internal static System.Collections.Generic.IReadOnlyCollection<string> KnownTools => _helpTexts.Keys;

        // Friction 2026-05-22 #62: gotcha doc resource. Every warning/lint
        // envelope carries docUrl=genexus://kb/tool-help/gotchas/<code>; the
        // agent fetches the long-form here. Returns a per-code body when
        // known, a generic stub otherwise so callers always get a payload.
        private static readonly Dictionary<string, string> _gotchaTexts = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["LintKbCharsetLossy"] =
                "# LintKbCharsetLossy\n\n" +
                "**Severity:** Warning.\n\n" +
                "Content contains characters outside the KB's WIN1252 charset. At runtime GeneXus will render those characters as `?`.\n\n" +
                "## Fix\n" +
                "- Replace with ASCII / latin-1 equivalents (`✓` → `OK`, `⧖` → `[wait]`).\n" +
                "- Or change the KB's `NLS_CHARACTERSET` to a UTF-8 variant if you need full unicode.\n",
            ["LintSpc0150ForEachAttributeWrite"] =
                "# LintSpc0150ForEachAttributeWrite\n\n" +
                "**Severity:** Warning (preflight — write succeeds, build will fail).\n\n" +
                "WebPanel Events source has an attribute assignment (no leading `&`) inside a `For each` / `endfor` block. GeneXus will fail the build with `spc0150 — Attribute cannot be assigned in this context`.\n\n" +
                "## Fix\n" +
                "Move the offending logic into a Procedure. Use the recipe:\n\n" +
                "```\ngenexus_recipe { name: 'extract_to_procedure' }\n```\n",
            ["GotchaGxButtonHtmlFormCustomEvent"] =
                "# GotchaGxButtonHtmlFormCustomEvent\n\n" +
                "`gxButton` with a custom `OnClickEvent` inside `<Form type=\"html\">` compiles but the HTML generator wires `data-gx-evt=5` (Enter) regardless. Custom events do not fire.\n\n" +
                "## Fix\n" +
                "- Use `<gxBitmap eventGX=\"'EventName'\" />` styled as a button, OR\n" +
                "- Move the control to `<Form type=\"layout\">` with `<action onClickEvent=\"'EventName'\" />`.\n",
            ["GotchaGxAttributeHtmlFormDiscreteReadOnly"] =
                "# GotchaGxAttributeHtmlFormDiscreteReadOnly\n\n" +
                "`gxAttribute` with `ControlType=\"Radio Button\"` or `\"Combo Box\"` inside `<Form type=\"html\">` renders disabled (the generator emits `disabled=\"\" class=\"gx-disabled\"`). `ReadOnly=\"False\"` and `Enabled=\"True\"` are ignored on this generator path.\n\n" +
                "## Fix\n" +
                "- Move the control to `<Form type=\"layout\">` (WWP table pattern), OR\n" +
                "- Render via a User Control, OR\n" +
                "- Emit raw `<input type=\"radio\">` inside a `gxTextBlock Format=\"HTML\"` block + JS wiring back to a hidden default-ControlType gxAttribute.\n",
            ["GotchaGxAttributeMissingDataField"] =
                "# GotchaGxAttributeMissingDataField\n\n" +
                "`gxAttribute` has neither `AttID` nor `DataField`. The control renders but binds to nothing; `FixWebFormData` silently keeps it so the missing binding masks the problem.\n\n" +
                "## Fix\n" +
                "Add `AttID=\"var:N\"` or `DataField=\"<attributeName>\"` so the control binds to a value.\n",
            ["GotchaUnknownControlType"] =
                "# GotchaUnknownControlType\n\n" +
                "`gxAttribute ControlType=\"...\"` is not a value the SDK recognizes (often a typo: `RadioButton` for `Radio Button`). The generator silently falls back to `Edit`.\n\n" +
                "## Valid ControlType values\n" +
                "Edit, Text Box, Combo Box, Radio Button, Check Box, Calendar, Image, Picture, Hyperlink, Button, Static, Description, Embedded Page, Dynamic Combo Box, List Box, Multi Selection List Box, Textarea, Password.\n",
            ["GotchaWebComponentMissingObjectCall"] =
                "# GotchaWebComponentMissingObjectCall\n\n" +
                "`gxEmbeddedPage` / `gxWebComponent` has no `ObjectCall` attribute → runtime renders an empty `<div>`.\n\n" +
                "## Fix\n" +
                "Add `ObjectCall=\"<ComponentName>.Create()\"` (or equivalent factory call).\n",
            ["GotchaHtmlFormatScriptStripped"] =
                "# GotchaHtmlFormatScriptStripped\n\n" +
                "`gxTextBlock Format=\"HTML\"` with `<script>`, `<iframe>`, or `<img onerror=...>` inside the CDATA. The GeneXus HTML generator escapes these tags so they render as literal text — your JS will NOT run.\n\n" +
                "## Fix\n" +
                "Use `<body onmousedown=\"...\">` + `addEventListener` for runtime JS injection. Inline event attributes on raw HTML elements inside `Format=\"HTML\"` blocks (e.g. `<input type=\"radio\" onclick=\"...\">`) ARE preserved — only block-level `<script>` / `<iframe>` / `img onerror` patterns are escaped.\n",
            ["GotchaCellOutsideTable"] =
                "# GotchaCellOutsideTable\n\n" +
                "`<cell>` or `<row>` with no `<table>` ancestor — the generator wraps silently or drops the element. Layout structure may be malformed at runtime.\n\n" +
                "## Fix\n" +
                "Wrap the element in a `<table>...<tbody>...</tbody></table>` hierarchy.\n",
            ["GotchaDuplicateControlName"] =
                "# GotchaDuplicateControlName\n\n" +
                "Two elements share the same `id` / `Name`. The SDK auto-renames the duplicates via `GetUniqueName` on save — any caller reference (event handler, JS, parent layout) that pointed at the renamed control breaks silently.\n\n" +
                "## Fix\n" +
                "Make each `id` unique. Suffix logically-related controls (`Btn1`, `Btn2`, ...).\n",
            ["GotchaIdeObjectOpenInEditor"] =
                "# GotchaIdeObjectOpenInEditor\n\n" +
                "**Severity:** Critical Warning / Concurrency Hazard.\n\n" +
                "The object is currently open in an active editor tab in the running GeneXus IDE. " +
                "If the developer presses Ctrl+S (Save) in the GeneXus IDE, their in-memory tab state will silently overwrite the changes made out-of-process by MCP (Last-Write-Wins hazard).\n\n" +
                "## Fix\n" +
                "- In the GeneXus IDE, close the open object tab WITHOUT saving, or choose \"Reload\" when prompted.\n" +
                "- Or use `concurrencyPolicy: 'fail_if_open'` in `genexus_edit` to prevent writes while the object is open in the IDE.\n",
            ["GotchaIdeActiveOnKb"] =
                "# GotchaIdeActiveOnKb\n\n" +
                "**Severity:** Notice / Informational.\n\n" +
                "GeneXus IDE is currently running and has this Knowledge Base open. " +
                "While the specific target object does not appear to be currently open in an active editor tab, keep in mind that concurrent edits in the IDE and MCP can cause conflicts.\n\n" +
                "## Fix\n" +
                "- Ensure any open tabs in the GeneXus IDE are closed or reloaded after external MCP writes.\n" +
                "- Review GeneXus IDE prompts if an external reload prompt appears.\n"
        };

        internal static string GetGotchaHelp(string code)
        {
            if (!string.IsNullOrWhiteSpace(code) && _gotchaTexts.TryGetValue(code, out var text))
                return text;
            // Generic stub so the agent always gets a 200. The code itself is the strongest
            // grep target; the agent can fall back to the message text on the warning.
            return $"# {code}\n\nNo long-form documentation is registered for this code yet. " +
                   "Inspect the `message` / `workaround` fields on the warning envelope — those carry the actionable guidance.\n";
        }

        internal static System.Collections.Generic.IReadOnlyCollection<string> KnownGotchaCodes => _gotchaTexts.Keys;
    }
}
