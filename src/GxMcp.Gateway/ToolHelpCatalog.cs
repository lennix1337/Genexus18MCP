using System.Collections.Generic;

namespace GxMcp.Gateway
{
    internal static class ToolHelpCatalog
    {
        private static readonly Dictionary<string, string> _helpTexts = new(System.StringComparer.OrdinalIgnoreCase)
        {
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
                "- `limit: 50`, `offset: 0`.\n\n" +
                "## Examples\n" +
                "- `{ query: 'type:Procedure', limit: 20 }`\n" +
                "- `{ query: 'usedby:InvoiceProc' }`\n" +
                "- `{ query: 'OrderTrn', fields: 'name,type,path,description' }`\n",

            ["genexus_lifecycle"] =
                "# genexus_lifecycle\n\n" +
                "Build, validate, index, or poll the active Knowledge Base.\n\n" +
                "## Actions\n" +
                "- `build` — non-blocking when `estimated_seconds >= 20`; returns `{ operationId, job_id, status: 'running', pollTarget: 'op:<id>' }` and surfaces `_meta.background_jobs` on the next call. Pass `wait_until_done: true` to block until terminal (single turn instead of polling).\n" +
                "- `validate` — inline validation/specifier check. Returns the result in the same call; it does not currently use the background-job path.\n" +
                "- `index` — rebuilds the search index. Pass `force=true` to ignore the on-disk cache.\n" +
                "- `status` — accepts either a `taskId` or `job_id` via `target`; pass `wait_seconds > 0` to long-poll up to 600s.\n" +
                "- `result` — fetch the completion payload of a finished operation.\n" +
                "- `inspect` — read the redacted durable mutation journal for an operation key after a lost response; it never replays the write.\n" +
                "- `reconcile` — close an unknown mutation fence only after an independent read and explicit `confirmed: true` verification; use a fresh key for any later write.\n" +
                "- `stop-worker` — gracefully recycle the worker process for the active KB.\n\n" +
                "## target format\n" +
                "- Build/validate: object name(s), comma- or semicolon-separated.\n" +
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
                "- `{ action: 'index', force: true }`\n",

            ["genexus_edit"] =
                "# genexus_edit\n\n" +
                "Edit the source or metadata of a GeneXus object.\n\n" +
                "## Required\n" +
                "- Either `name` (single object) **or** `targets` (array) — never both.\n" +
                "- `mode`: `full` (replace whole part) or `patch` (Replace/Insert_After/Append over a context anchor).\n" +
                "- `dryRun: true` first for either mode. A preview is synchronous, never calls Save, and never starts a lifecycle action.\n\n" +
                "## Output\n" +
                "- Returns `post_state.diff` (unified diff) by default.\n" +
                "- `verbose: true` adds slices with ±15 lines of context.\n" +
                "- `return_post_state: false` opts out of the post-state block to save tokens.\n" +
                "- `async: true` returns immediately with one `operationId` / `job_id`; the same ID is used by Worker busy telemetry and lifecycle status/result/cancel. Cancellation terminalizes the operation and recycles a blocked non-preemptible Worker.\n" +
                "- Full Source writes return the independently re-read `source`, `postSaveVerification.versionToken`, `persisted`, and `implicitLifecycleActions`. After a timeout or cancellation, another write to that object is blocked until `genexus_read` confirms its actual state.\n\n" +
                "## Patch persistence verification\n" +
                "Source and Rules are always re-read after the single SDK save. `verifyMode: 'normalized'` is the default and tolerates EOL, encoding marker, trailing-whitespace, and repeated-blank-line rendering by the SDK; `exact` preserves comments, whitespace, and blank lines but treats CRLF/LF as the same logical Source representation; `semantic` also tolerates harmless SDK casing/spacing changes. Comment-only Replace writes require `baseVersion`, are verified against the requested comment, report active old-statement presence, and return `CommentOnlyWriteNotPersisted` if the SDK re-read diverges. The response separates `saved` from `verified` and includes raw/normalized hashes, `normalizationApplied`, `diffNormalized`, `matchCount`, `persistedMatchCount`, `oldContentPresent`, `replacementPresent`, `reReadConfirmed`, and `implicitOperations` (always empty for this path). A mismatch is never reported as Applied. Rollback occurs only with `rollbackOnFailure: true` and a valid snapshot, and reports its own save/verification hashes. Pass the prior read's `versionToken` as `baseVersion` to reject concurrent edits. No Specify, Generate, Build, Rebuild, compilation, reorganization, execution, or tests are invoked by a patch write.\n\n" +
                "## Disambiguation\n" +
                "If `name` matches multiple objects, the error includes `suggestion` and `availableTypes`. Pass `type=<ObjectType>` or use `parentPath` to disambiguate.\n\n" +
                "## Examples (source code)\n" +
                "- `{ name: 'InvoiceProc', part: 'Source', mode: 'patch', operation: 'Replace', context: '<old block>', content: '<new block>', dryRun: true }`\n" +
                "- `{ name: 'OrderTrn', part: 'Rules', mode: 'full', content: '<rules text>' }`\n\n" +
                "## Editing WorkWithPlus pattern parts (PatternInstance / PatternVirtual)\n" +
                "Pattern XML is the IDE's structural model — containers, controls, actions, grids, orders, filters all live there. **Both `mode: full` and `mode: patch` work**; the MCP handles the SDK quirks transparently.\n\n" +
                "### Auto-reconcile `childrenOrderedList`\n" +
                "WorkWithPlus stores IDE rendering order in a per-parent `childrenOrderedList` attribute. **You don't need to manage it.** On every pattern write the MCP rebuilds (and creates if missing) every list from the actual child order in your XML, dropping orphans and adding new entries. The response includes a `childrenOrderedListReconciliation` block listing what changed and why — read it back to confirm your changes will render.\n\n" +
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
                "- Add a Selection ordering: insert `<order name=\"Por código\"><attribute attribute=\"<guid>-FieldName\" /></order>` inside `<orders>`; childrenOrderedList is auto-updated.\n",

            ["genexus_analyze"] =
                "# genexus_analyze\n\n" +
                "Semantic analysis across one or more objects.\n\n" +
                "## Modes\n" +
                "- `impact` — callers, callees, blast radius, risk level, affected entry points.\n" +
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
                "- `impact` waits up to 30s for the index to be ready unless `waitForIndex: false`.\n" +
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
                "- `typeName` — required for `add` and `modify`\n\n" +
                "## Optional\n" +
                "- `basedOn` — domain name for compatible typed variables\n" +
                "- `async: true` returns immediately with `operationId` / `job_id`; poll `genexus_lifecycle action=status|result target=op:<id>` for completion.\n\n" +
                "## Notes\n" +
                "- GAM / WWP+ framework-managed variables are protected and return a refusal instead of mutating them.\n" +
                "- `modify` preserves the variable name and description while changing the type atomically.\n\n" +
                "## Examples\n" +
                "- `{ action: 'add', name: 'InvoiceProc', varName: '&Total', typeName: 'Numeric(10.2)' }`\n" +
                "- `{ action: 'modify', name: 'InvoiceProc', varName: '&State', typeName: 'Character(20)', async: true }`\n" +
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
                "Currently registered: `WorkWithPlus` (alias `WWP`).\n\n" +
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
                "Create a new empty GeneXus object in the active KB (`action: object`, the default). The tool covers every KBObject the IDE can create — both objects with a typed wrapper (Transaction, Procedure, WebPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, ExternalObject, Theme, Image, Menu, Menubar, Stencil, UserControl, WorkPanel, Report, API, URLRewrite, MiniApp, SuperApp, DesignSystem, ColorPalette, OfflineDatabase, DataView, Group, Language) and Guid-only types (SDPanel, Dashboard, Query, QueryDashboard, WorkflowDiagram, ConversationalFlows, TestSuite, ThemeClass, ThemeColor, ThemeTransformation, DesignSystemClass, WorkWithDevices, WorkWithWeb, WikiPageKBObject, TranslationMessage, DataStoreCategory, GeneratorCategory, DeploymentUnitCategory).\n\n" +
                "Aliases accepted: `StructuredDataType`→SDT, `BusinessProcessDiagram`/`BPD`→WorkflowDiagram, `PanelForSD`→SDPanel.\n\n" +
                "## Defaults that get seeded\n" +
                "- `Transaction` — gets a default `<Name>Id : Numeric(8,0) [Key]` attribute so the SDK accepts the empty save.\n" +
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
                "- `buildIncludeCallees` — `none` | `direct` (default) | `transitive`\n" +
                "- `buildPlanCap` — max build-plan size (default 200)\n\n" +
                "## Response\n" +
                "Returns a composite envelope with three blocks:\n" +
                "- `edit` — the diff from genexus_edit\n" +
                "- `impact` — output of genexus_analyze mode=impact (callers, risk, etc.)\n" +
                "- `build` — `{ taskId|TaskId, status: 'Accepted'|'Running', pollTarget }` for async caller rebuild, or `{ status: 'Skipped' }` when no callers\n\n" +
                "Poll the build via `genexus_lifecycle action=status target=<pollTarget>`.\n\n" +
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
                "- `records_query` — read typed rows from the Transaction's root table using equality-only `where`, optional root-attribute `fields`, and a bounded `limit` (1–1000). It returns the resolved metadata and an optimistic `versionToken`.\n" +
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
                "- `history_list` — list saved versions and timestamps for an object.\n" +
                "- `history_get` — retrieve source or XML of a specific historic versionId.\n" +
                "- `history_save` — explicitly snapshot current object state into local history.\n" +
                "- `history_restore` — restore an object to a prior snapshot or version; pass `discard: true` for IDE 'Discard changes' parity.\n" +
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
                "- `export_part` — export a single object part (e.g. Source, Rules) to an external file.\n" +
                "- `import_part` — import object part content from a file.\n" +
                "- `export_unified` — export complete object envelope as a portable JSON file.\n" +
                "- `screenshot_publish` — publish screenshot PNG into `.gx/published-screenshots`.\n" +
                "- `ocr` — optical character recognition on image assets.\n",

            ["genexus_kb_version"] =
                "# genexus_kb_version\n\n" +
                "Manage KB model versions and development branches via the SDK's KBVersionHelper.\n\n" +
                "## Actions\n" +
                "- `list` — enumerate all versions and branches in the KB version tree.\n" +
                "- `freeze` — freeze current version into an immutable baseline (`name`, `description`, `parentVersion`).\n" +
                "- `branch` — create a new parallel branch from a parent version (`name`, `includeEnvironments`).\n" +
                "- `set_active` — switch the active development version/branch (`targetVersion`, `autoUpdate`).\n" +
                "- `revert` — revert working model changes back to a baseline version.\n",

            ["genexus_doc"] =
                "# genexus_doc\n\n" +
                "Generate structured documentation and visual assets for Knowledge Base objects.\n\n" +
                "## Actions\n" +
                "- `wiki` — generate complete Markdown wiki pages for target objects and modules. Writes documentation files to disk under the documentation target directory.\n" +
                "- `visualize` — generate dependency and call graphs (Mermaid / visual format) for target objects.\n" +
                "- `health` — compile a KB-wide or object-specific health report evaluating code metrics, dead code, and documentation coverage.\n\n" +
                "## Operational Notes\n" +
                "`action=wiki` writes documentation files to disk; it is not purely in-memory read-only. Use `genexus_analyze` for structured programmatic inspections.\n",

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

            ["genexus_kb"] =
                "# genexus_kb\n\n" +
                "Manage the gateway's multi-KB pool and the startup fallback selected for future sessions.\n\n" +
                "## Read actions\n" +
                "- `list` — show selected, active, default, open, known, and declared KB aliases.\n" +
                "- `list_environments` / `get_environment` — inspect environment metadata.\n" +
                "- `get_startup` — read the persisted startup selection.\n\n" +
                "## Mutating actions\n" +
                "- `open` / `close` — register or release a Worker and KB lease.\n" +
                "- `set_default` / `set_startup` / `set_environment` — change session or persisted selection.\n\n" +
                "Use an explicit `kb` alias when a call must target a different open KB; do not rely on shared server-side selection between independent clients.\n",

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
                "- `get` — read the current property values and version information.\n" +
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

            ["genexus_module"] =
                "# genexus_module\n\n" +
                "Inspect and manage modules through the GeneXus Module Manager.\n\n" +
                "## Actions\n" +
                "- `list` — read installed and available module metadata.\n" +
                "- `install` / `install_builtin` — add a module to the KB.\n" +
                "- `update` — update an installed module.\n\n" +
                "Only `list` is read-only. Installation and updates can change many KB objects; review the returned plan and run an appropriate build or validation afterward.\n",

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

            ["genexus_wwp"] =
                "# genexus_wwp\n\n" +
                "Inspect and edit WorkWithPlus Action Groups and grid actions in PatternInstance XML.\n\n" +
                "## Actions\n" +
                "- `list` — read the current action groups and ordered actions.\n" +
                "- `add_action`, `update_action`, `move_action`, and `remove_action` — change the WWP action model.\n\n" +
                "Only `list` is read-only. Read the authoritative PatternInstance first, use `dryRun` when supported, and verify the saved XML because WorkWithPlus may reconcile IDE ordering on save.\n"
        };

        internal static string? Get(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;
            if (_helpTexts.TryGetValue(toolName, out var text))
                return text + OperationClassifier.BuildHelpContract(toolName);
            // Legacy alias → canonical: resolve and retry so old tool names still find help.
            if (McpRouter.TryRewriteLegacyTool(toolName, null, out var canonical, out _)
                && _helpTexts.TryGetValue(canonical, out var canonText))
                return canonText + OperationClassifier.BuildHelpContract(canonical);
            return null;
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
