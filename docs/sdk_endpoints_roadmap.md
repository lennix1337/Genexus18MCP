# SDK-endpoint expansion roadmap (P0 → P3)

Execution plan for wiring the uncovered SDK capabilities from
[`sdk_uncovered_endpoints_2026-07-20.md`](sdk_uncovered_endpoints_2026-07-20.md) into MCP
endpoints. Each item lists the SDK entry point, the input-construction gate result, the
tool surface, and the wiring touch-points.

## Live verification (2026-07-20) — all 6 P0/P1 tools working

Smoke-tested over HTTP against a running worker on KB `AcademicoHomolog1`. All six resolve
and execute:

| Tool | Result |
|---|---|
| `genexus_transfer` | ✅ IKnowledgeManagerService resolves; Export reached (dependency-aware) |
| `genexus_gxserver pipeline_*` | ✅ IContinuousIntegrationService resolves + invokes |
| `genexus_security scan_native` | ✅ `SecurityScanCompleted` |
| `genexus_analyze kb_stats` | ✅ real timestamps + `reorgLikelyNeeded` |
| `genexus_db reorg_impact` | ✅ timestamps + heuristic |
| `genexus_deploy list_targets` | ✅ 13 real targets (AWS EB, Tomcat, IIS8, …) |

**The wall we hit and solved.** The static input-construction gate passed for all six, but
at runtime the *service-registration* gate failed for four: `IModelInformationService`,
`ISpecifierService`, `IDeploymentService`/`IDeploymentTargetService`, and
`ISecurityScannerService` are **not registered in the headless worker's service registry**
(they are registered by IDE-only packages the worker never loads). `Services.TryGetService`
(by type or by interface GUID) returns null for them — a wall only a live probe reveals.

**Resolution:** construct the **public concrete impl** directly and cast to the interface —
the same idiom `GamService` already uses. Every impl has a public parameterless ctor:

| Interface | Concrete class (assembly) |
|---|---|
| `ISecurityScannerService` | `GeneXus.SecurityScanner.Common.Services.SecurityScannerService` (+ `Initialize(<gx>\Security\Commands)`) |
| `IModelInformationService` | `Artech.Packages.Genexus.BL.Services.ModelInformationService` (GenexusBL) |
| `ISpecifierService` | `Artech.Packages.Specifier.Services.SpecifierService` (Specifier) |
| `IDeploymentTargetService` | `Artech.Packages.Genexus.BL.Services.DeploymentTargetService` (GenexusBL) |
| `IDeploymentService` | `Artech.Packages.Genexus.BL.Services.DeployService` (GenexusBL) |
| `IStatisticsService` | `Artech.Architecture.Common.Services.StatisticsService` |

`SdkServiceLocator.ConstructOrResolve<T>(factory)` encapsulates "construct-concrete-first,
fall back to registry". New csproj refs: `Artech.Packages.GenexusBL`, `Artech.Packages.Specifier`,
`GeneXus.SecurityScanner.Common`, `GeneXus.TeamDevClient.Architecture.BL`.

## Capability probe contract (v3)

`genexus_sdk_probe { mode: "capabilities" }` is the safe first step for authoring
parity. It returns the installed SDK version, matched type names, and one of
`available_unverified`, `unavailable`, or `deferred` for each curated capability.
Every row carries `persistenceVerified=false`; reflection cannot certify a save.
The Business Component row is deferred because BC rules belong to the generated
application runtime and require a separate disposable endpoint fixture.

## Feasibility gate (done 2026-07-20)

Ran against `docs/sdk-probe/raw.json`. **All P0/P1 input types are headless-constructible — no WWP-style wall.**

| Input type | Verdict |
|---|---|
| `ExportOptions` (23 props) / `ImportOptions` (26) / `ExploreExportOptions` | public parameterless ctor ✓ |
| `KBObjectQuery` (Model/WhiteList/AuthorizationProcedure) | public ctor; WhiteList+AuthProc from `SecurityScanPlan.GetForModel(model)` ✓ |
| `SecurityScanPlan` | public ctor + static `GetForModel(KBModel)` ✓ |
| `IScannerOuput` | interface → Worker implements a collecting adapter ✓ |
| `BuildOptions` | **`[Flags]` enum** (probe showed 0 ctors — it's an enum, `ImpactAnalysis`/`CreateAnalysis`/…) ✓ |
| `AnalysisResult` | **enum** — `{NoReorgNeeded, ReorganizationNeeded, NoReorgButShowImpact, ErrorInSomeTable, Exception}` = the impact signal ✓ |
| `DeploymentTarget` (19 props) | public ctor ✓ |
| `TeamDevelopmentData` | public ctor + `ctor(KBModel)` ✓ |
| `OperationEvent` | return type (read-only, not constructed by us) ✓ |

## Wiring pattern (every SDK tool follows this)

Established by `CompareService`/`ModuleService`/`GamService`:

1. **Schema** → `src/GxMcp.Gateway/tool_definitions.json` (keep `ToolSchemaSizeTests` budget; bump w/ CHANGELOG note).
2. **Gateway route** → `Routers/OperationsRouter.cs`: `case "genexus_X": return new { module = "X", action = "Run", @params = args };`
3. **Worker dispatch** → `Services/CommandDispatcher.cs`: field + ctor construct + `["x"] = Handle_X` in the action map + `Handle_X` method delegating to the service.
4. **Service** → `src/GxMcp.Worker/Services/XService.cs`: resolve via `SdkServiceResolver.Resolve<T>()`, guard every null/throw, never crash the worker, return `*Unavailable` on missing service.
5. **Golden fixture** → `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json` (alphabetically sorted) + tests.

Discipline: read-only default; destructive actions must be explicit (mirror `genexus_gam` `status` vs `deploy`). Never let a slow SDK call (specification/deploy) hide behind a "preview".

---

## P0 — this task

### #1 `genexus_transfer` — XPZ export / import (real, dependency-aware)
- **SDK:** `IKnowledgeManagerService.Export(model, IEnumerable<KBObject>, outputFile, ExportOptions)`; `ExploreExport(file, model, ExploreExportOptions, out objects/actions/idMap, …)`; `GetCurrentImport(kb)`.
- **Surface:** `action=export` (targets[]+outputFile) · `action=inspect` (explore an .xpz without importing) · `action=import` (file, dryRun default true — destructive on false).
- **Why:** the promote-to-prod flow (`project_controleobjetos_promotion_flow`); today's `genexus_io`/`kb_import` are filesystem copies that don't resolve deps.

### #2 `genexus_security action=scan_native` — real Security Scanner
- **SDK:** `plan = SecurityScanPlan.GetForModel(model)` → `query = new KBObjectQuery{Model, WhiteList=plan.GetWhiteList(), AuthorizationProcedure=plan.GetAuthorizationProcedure()}` → `ISecurityScannerService.Scan(query, plan, output)` with a Worker-side `IScannerOuput` collector.
- **Surface:** adds `scan_native` to `genexus_security` (alongside `audit_gam`/`scan_secrets`). Read-only.

---

## P1 — this task

### #5 `genexus_db action=reorg_impact` — reorg / DDL impact
- **SDK (cheap, primary):** `IModelInformationService.{GetLastReorgTimestamp, GetLastModifiedTableTimestamp, GetLastModifiedObjectTimestamp, NeedReorg}` → derive `reorgLikelyNeeded`.
- **SDK (deep, opt-in `deep=true`):** `ISpecifierService.ImpactDatabase(model, BuildOptions.ImpactAnalysis|CreateAnalysis)` → `AnalysisResult` enum. **Runs specification (build-heavy) — gated behind `deep=true` with an explicit warning; never the default.**
- **Surface:** `genexus_db action=reorg_impact [deep=true]`. Read-only.

### #6 `genexus_analyze mode=kb_stats` — KB activity & freshness
- **SDK:** `IStatisticsService.{GetOperationsByDate, GetEntitiesByDate}` + `IModelInformationService` timestamps.
- **Surface:** new `mode=kb_stats` on `genexus_analyze`. Read-only.

### #4 `genexus_gxserver action=pipeline_*` — CI pipelines
- **SDK:** `IContinuousIntegrationService.{GetPipelines, GetPipelineRuns, GetPipelineRunInfo, GetPipelineRunOutput, RunPipeline(data, project, isRebuild, runTests), AbortRunPipeline}` with `TeamDevelopmentData(model)`.
- **Surface:** `genexus_gxserver` actions `pipeline_list` / `pipeline_runs` / `pipeline_run` (destructive: triggers a build) / `pipeline_abort`. Read actions default; `pipeline_run`/`pipeline_abort` explicit. Returns `{connected:false}` off a linked KB (mirror existing gxserver).

### #3 `genexus_deploy` — deploy application
- **SDK:** `IDeploymentTargetService.{GetTargetTypes, GetTarget}` (read); `IDeploymentService.Deploy(model)` / `ILibraryService.DeployLibrary(...)` (destructive).
- **Surface:** `action=list_targets` (read-only default) · `action=deploy` (explicit, destructive, `confirm=true`).
- **Note:** deploy needs a configured target; ship `list_targets` solid first, `deploy` guarded like `genexus_gam deploy`.

---

## P2 — DONE (2026-07-20), except two dropped after the gate

Shipped + live-verified (all via `ConstructOrResolve` concrete-impl, `Artech.Packages.GenexusBL`):

| Item | Tool | Result |
|---|---|---|
| #7 Table/Transaction relations | `genexus_analyze mode=table_relations` | ✅ associated table + transactions + redundant attrs (over `ITablesService`; the FK-graph `ITableRelationsService`/`ITransactionRelationsService` have no public concrete — deferred) |
| #9 curl → Procedure | `genexus_create action=curl_procedure` | ✅ `ICurlGeneratorService.Generate` (resolves + validates input) |
| #10 User-control / theme catalog | `genexus_layout action=list_controls` | ✅ `IUserControlsManagerService.GetControlDefinitionCollection` |

**Dropped after the feasibility gate (parede = informação):**
- #8 translations — `ILanguageService` is the **source-code parser / type manager** (GetClass, ResolveMethod, GetNamespaces), NOT human-language i18n. Wrong entry point; `genexus_db action=translations_import` (CSV) already covers i18n.
- #11 data-type catalog — `IDataTypesService.GetSortedTypes` needs a CLR-`Type` arg and duplicates the existing `genexus_db action=types_list` (Domains/SDTs). Low marginal value.

## P3 — gated 2026-07-20; one built, rest dropped

Built + live-verified:
- **`genexus_layout action=design_system`** — DSO token groups / theme classes / images / referenced DSOs via `DesignSystemHelper`. The one P3 helper with real value + clean input (complements `list_controls`).

Dropped after the gate (niche / duplicate / wrong-surface / build-time):
- **Full-text search** (`ISearchService`) — duplicates `genexus_query` + `genexus_search_source`.
- **Service-DL generation** (`IRestServiceDLGeneratorService`/OData/gRPC) — build-time codegen, not a discrete agent action.
- **Chatbot** (`IBotGeneratorService`) — needs a pre-existing Chatbot object + Dialogflow config; niche.
- **App help** (`IHelpGeneratorService`) — complex `ApplicationHelpGeneratorOptions`; niche output.
- **KB conversion** (`IKBConversionService`) — pointless for an already-open KB (it converts on open).
- **GXplorer SQL** (`IGXplorerSpecifierService`) — `Initialize`+`StartDaemon` (uncertain runtime); value overlaps `genexus_analyze mode=impact/dependencies`.
- **BPM** (`IGxpm*`) — large niche process-modeling surface.

Not built = deliberate (every tool costs schema-budget tokens each session); the valuable
agent-facing SDK surface is now covered. Remaining gaps are the model-axis walls
(folder move, WWP persist, full pattern build sequence) tracked in `sdk_coverage_gap_matrix.md`.

## Build order (this task)

Read-only wins first (fast, low risk), destructive last:
**#6 kb_stats → #5 reorg_impact → #2 native security scan → #1 XPZ transfer → #4 CI pipelines → #3 deploy.**
Each: service + wiring + fixture + a unit test asserting the graceful `*Unavailable` path
(the live SDK-success path is smoke-tested over HTTP against the running gateway, since the
test KBs don't guarantee a registered service).
