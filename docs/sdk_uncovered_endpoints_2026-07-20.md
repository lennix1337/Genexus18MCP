# SDK endpoints not yet exposed by the MCP — validation 2026-07-20

Fresh cross-reference of the GeneXus 18 SDK surface (via `genexus_sdk_probe`) against the
current MCP tool surface (`src/GxMcp.Gateway/tool_definitions.json`, 46 tools, server
v2.27.1). Goal: a concrete backlog of SDK capabilities the MCP could turn into endpoints.

This **refreshes** `docs/sdk_coverage_gap_matrix.md` (last full pass 2026-07-09): many of
that doc's `❌ gap` rows — Comparer, Merge, Module Manager, GAM provisioning, GXserver
write path, KB version/branch — have since **shipped** (`genexus_compare`, `genexus_merge`,
`genexus_module`, `genexus_gam`, `genexus_gxserver` write, `genexus_kb_version`). The rows
below are what is **still** uncovered as of this probe.

## Method

- Probe: 109 assemblies, 14,745 public types, 2,478 generator candidates
  (`docs/sdk-probe/raw.json`, 17 MB; regenerate with `genexus_sdk_probe outputDir=<repo>\docs\sdk-probe`).
- Axis chosen: **resolvable service interfaces** (`I*Service`, 106 total) — the SDK's own
  "capability" unit, each resolved headless via `Services.TryGetService<T>()`
  (see `Helpers/SdkServiceResolver.cs`). This is the cleanest "endpoint" axis; the
  model-mutation axis (`KBObject`/parts/patterns) is already broadly covered and noted at the end.
- Coverage baseline: the Worker consumes **8** SDK services today —
  `IComparerService`, `IMergeService`, `IModuleManagerService`, `IIntegratedSecurityService`,
  `IGXserverService`, `ITeamDevClientService`, `IRunService`, `IGxService` — plus static
  helpers (`KBVersionHelper`, `ReportLayoutHelper`, `WebFormXmlHelper`, …).

## Ranked backlog of uncovered endpoints

Ranking = IDE-parity value (how much it unblocks "use the MCP instead of the IDE"), not effort.
All entry points below verified present in the 2026-07-20 probe; signatures abbreviated.

### P0 — high value, headless-viable, a real workflow depends on it

| # | Capability | Proposed tool | SDK entry point(s) |
|---|---|---|---|
| 1 | **XPZ export / import (real, dependency-aware)** — the promote-to-prod flow (`project_controleobjetos_promotion_flow`). Today `genexus_io`/`genexus_kb_import` only copy part files at the filesystem level and **do not resolve dependencies** — semantically wrong for XPZ. | `genexus_transfer action=export\|import` | `IKnowledgeManagerService.Export(KBModel, IEnumerable<KBObject\|EntityKey\|Entity>, [actions], outputFile, ExportOptions)`; `ExploreExport(file, model, ExploreExportOptions, out objects/actions/idMap, out KMFileInfo/KMSourceInfo, out deps)`; `GetCurrentImport(kb)` / `GetCurrentExport(kb)`; `GetDeleteAction(...)`. `Artech.Architecture.Common.Services` (`Artech.Architecture.Common`). |
| 2 | **Real GeneXus Security Scanner** — the SDK's own scanner, distinct from `genexus_security` (regex/env scan) and `genexus_gam` (provisioning). Produces the IDE "Security Scanner" findings. | `genexus_security action=scan_native` | `ISecurityScannerService.Scan(KBObjectQuery query, SecurityScanPlan plan, IScannerOuput output)`; `GetCommand(int) / RegisterSecurityCommand(...)`. `GeneXus.SecurityScanner.Common`. |

### P1 — needed for full IDE parity; workaround exists or partial today

| # | Capability | Proposed tool | SDK entry point(s) |
|---|---|---|---|
| 3 | **Deploy application** — no deploy action at all. Covers IDE "Deploy Application" / library deploy / cloud. | `genexus_deploy` | `IDeploymentService.Deploy(KBModel)`; `IDeployService.{SetModel, Deploy(GxModel,GxModel), AddHostConfiguration}`; `IDeploymentTargetService.{GetTarget, GetTargetTypes}`; `ILibraryService.DeployLibrary(model, libraryId, generator, dbms, …)` + `Install(...)`; niche: `IAzureDeploymentService.DeployToAzureWebRole`, `ICloudPrototypingService.{GetServers, GetConnectionString}`, `IDockerExecutorService.GetExecutor`. |
| 4 | **CI pipelines** (Team Dev client) — run/edit/status of server pipelines; natural extension of `genexus_gxserver`. | `genexus_gxserver action=pipeline_*` | `IContinuousIntegrationService.{GetPipelines, GetPipelineRuns, GetPipelineRunInfo, GetPipelineRunOutput, RunPipeline(data, project, isRebuild, runTests), AbortRunPipeline, EditPipeline, RemovePipeline}`. `GeneXus.TeamDevClient.Architecture.BL`. |
| 5 | **Reorg / DDL impact preview** — the deferred "DDL preview" item (`project_issue37_deploy_reorg`). Preview DB impact before a reorg without running it. | `genexus_db action=reorg_impact` (or `genexus_lifecycle`) | `ISpecifierService.{ImpactDatabase(fromModel, toModel, BuildOptions), CreateDatabase(fromModel, toModel), SpecifyObjects(model, keys, options)}` → `AnalysisResult`; pair with `IModelInformationService.NeedReorg(fromModel, toModel)`. `Artech.Genexus.Common`. |
| 6 | **KB activity / statistics & freshness** — operation history + last-modified/last-reorg timestamps for cheap "what changed / do I need a reorg" answers. | `genexus_analyze mode=kb_stats` | `IStatisticsService.{GetEntitiesByDate, GetOperationsByDate}`; `IModelInformationService.{GetLastModifiedObjectTimestamp, GetLastModifiedTableTimestamp, GetLastReorgTimestamp, NeedReorg}`. `Artech.Architecture.Common` / `Artech.Genexus.Common`. |

### P2 — completeness / useful authoring helpers

| # | Capability | Proposed tool | SDK entry point(s) |
|---|---|---|---|
| 7 | **Table / Transaction relation graph** — FK graph, superordinate/subordinate tables, associated transactions, redundant-attribute detection. | `genexus_analyze mode=table_relations` | `ITablesService.{GetAssociatedTable, GetAssociatedTransactions, GetRedundantAttributes, GetPossibleRedundantAttributes, GetTablesWithKeyAttribute}`; `ITableRelationsService.{GetRelations, GetSuperordinated, GetSubordinated}`; `ITransactionRelationsService.{GetRelations, GetLevelRelations}`. |
| 8 | **Multi-language / translations** — beyond the `genexus_db action=translations_import` CSV stub. | `genexus_db action=translations_*` | `ILanguageService.{CreateManager(model), CreateEngine()}` → `ILanguageManager` / `IParserEngine`. `Artech.Architecture.Language`. |
| 9 | **curl → Procedure generator** — scaffold a REST-consumer Procedure from a curl command (IDE "Import from cURL"). | `genexus_create action=object type=Procedure fromCurl=...` | `ICurlGeneratorService.Generate(KBModel, procName, procDescription, KBObject parent, curlCommand)`. `Artech.Genexus.Common`. |
| 10 | **User-control / theme-class introspection** — resolve control definitions and theme-class extensions (helps layout authoring pick valid classes). | `genexus_layout action=list_controls` | `IUserControlsManagerService.{GetControlDefinitionCollection, GetDefinition(name), GetControlDefinitionForThemeClassName, GetWebThemeExtensions, GetSDThemesExtensions, IsUserControlType}`. |
| 11 | **Data-type catalog** — the type picker the IDE grid shows for a given object type (Domains + primitives), for constrained variable/attribute typing. | `genexus_db action=types_catalog` | `IDataTypesService.{GetSortedTypes(model, objType, includeDomains), GetTypeNames, GetTypes}`. |

### P3 — niche / low-frequency

| # | Capability | SDK entry point(s) |
|---|---|---|
| 12 | **Service DL generation** (REST / OData / gRPC data layer) | `IRestServiceDLGeneratorService.Generate`, `IODataServiceDLGeneratorService.Generate`, `IProtocolBufferServiceDLGeneratorService`. |
| 13 | **Chatbot generation** | `IBotGeneratorService.{CreateInstance, Generate, GenerateAndSynchronize, SetPropertyValue}`. |
| 14 | **Application help generation / import** | `IHelpGeneratorService.{Generate(ApplicationHelpGeneratorOptions), Import(gxlFile, options)}`. |
| 15 | **Native full-text search** (SDK's own, vs our `SearchIndex`) | `ISearchService.Search(model, query, tags, [maxCount], [onlyTitles])`. |
| 16 | **KB version conversion / upgrade** | `IKBConversionService.{NeedConversion(location, out fromVersion), Convert(fromVersion, connectionInfo)}`. |
| 17 | **GXplorer SQL introspection** — related attributes + generated SQL for prolog queries | `IGXplorerSpecifierService.{GetRelatedAttributes, GetSQLSentences}`. |
| 18 | **BPM / process modeling** (Gxpm) | `IGxpm*Service` family (`Artech.Gxpm.Common`) — platforms, dynamic forms, validation. |

## Explicitly out of scope (do not wire)

- **IDE GUI-shell services** (`Artech.Architecture.UI.Framework.Services.*`, `Microsoft.Practices.*`):
  `IStatusBarService`, `IToolWindowsService`, `IToolboxService`, `IClipboardService`,
  `IDragAndDropService`, `IDocumentManagerService`, `IEditorManagerService`, `IMenuService`,
  `IListViewService`, `IOutlinerService`, `IModelTreeService`, `IStartPageService`,
  `IToolTipService`, `IReportViewService`, `ITrackSelectionService`, `ITasksService`,
  `IToolsOptionsService`, `IRecentKBsService`, `IProductInfoService`, `IEventsService`, and the
  dialog services (`ISelectObjectDialogService`, `INewObjectDialogService`, `ICreateKBDialogService`,
  `ISelectAttributeVariableService`, `ISelectImageService`). These drive Windows widgets and have
  no meaning in a headless worker; several also have a model-layer twin we already use
  (e.g. `IComparerService`/`IKnowledgeManagerService`/`ITeamDevClientService` exist in both the
  UI and Common namespaces — the Common one is the headless-safe target).
- **INormalizationService** — `Delete{Attribute,Table,Transaction,Object}` / `Save{Attribute,Domain}`
  are the internal reorg engine, invoked as part of a build; exposing them raw bypasses validation.
  Not an endpoint; drives P1 #5 internally.
- **IParallelProcessingService / IPrologService / IGeneratorsService / ISpecifierService daemons** —
  build-internal plumbing; only `ISpecifierService.ImpactDatabase/CreateDatabase` (P1 #5) is an
  agent-facing action.

## Not-a-service axis (already broadly covered — noted for completeness)

The other capability axis is direct `KBObject` / part / pattern mutation, not a service:
objects & parts CRUD (`genexus_create/read/edit/delete_object`), structure/variables
(`genexus_structure`, `genexus_variable`), layout/WebForm (`genexus_layout`, `genexus_edit_form`),
patterns (`genexus_apply_pattern`), refactor (`genexus_refactor`). Known residual gaps on this axis
are already tracked in `docs/sdk_coverage_gap_matrix.md`:
~~**Move object to folder/module** (SDK setters are no-op stubs — confirmed WALL, see AGENTS.md)~~
— **superseded 2026-07-24 (v2.35.0):** shipped as `genexus_properties action=move`, persisted via
`EntityManager.SaveWithParent`. The "no-op stubs" reading came from a facade/reference assembly;
see CHANGELOG v2.35.0 and `src/GxMcp.Worker/Helpers/ObjectMover.cs`.
**WWP settings/components persist** (⛔ blocked, `project_wwp_settings_components_persist_blocked`),
and the **full 9-hook pattern build sequence** (only `UpdateParentObject` wired).

## Suggested build order

1. **#1 XPZ transfer** — unblocks the promotion flow; highest concrete demand. Gate first:
   confirm `ExportOptions`/`ImportOptions`/`ExploreExportOptions` are headless-constructible POCOs
   (per the coverage-matrix discipline: spike input-construction before committing to a tool).
2. **#5 reorg/DDL impact** — small surface, closes a long-deferred item, low risk (read-only preview).
3. **#6 KB stats + #7 relations** — read-only analytics, cheap, fold into `genexus_analyze`.
4. **#2 native security scan** — one method, high signal.
5. **#3 deploy + #4 CI pipelines** — larger; sequence after the read-only wins land.
