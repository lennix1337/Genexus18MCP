# SDK coverage-gap matrix — IDE-parity target

> **Status refresh 2026-07-20:** the `❌ gap` rows below for Comparer, Merge, Module Manager,
> GAM provisioning, GXserver write, and KB version/branch have since **shipped**. For a current
> (v2.27.1, fresh-probe) list of what is *still* uncovered, see
> [`sdk_uncovered_endpoints_2026-07-20.md`](sdk_uncovered_endpoints_2026-07-20.md).

Goal: make the MCP cover every **GeneXus IDE user action** via the native SDK, so an
agent can use the MCP *instead of* opening the GeneXus 18 IDE. This document maps each
IDE-capability domain to its concrete SDK entry point(s) and marks current MCP coverage,
then ranks the gaps by how much they block "replace the IDE".

## Scope decision (why this isn't "all 56k methods")

Measured from `docs/sdk-probe/INDEX.md` (2026-05-30 probe): **15,774 public types /
56,555 public methods** across ~130 assemblies. Covering *literally all* is the wrong
goal — most of that surface is internal plumbing the IDE never exposes as an action:

- `Artech.Genexus.Resolvers*` (7,695 types) + JAVA/CSHARP/SWIFT/ANDROID variants — codegen, runs at build.
- `Artech.Common.Language.Parser*` / `*.Grammars` (~2,500 types) — parsers.
- `Artech.Architecture.UI.Framework` (403) + `*.PropertiesResolvers` + `*.resources` — IDE widget shell.

The IDE is a thin shell over a curated subset. This matrix targets the **action packages**:
`Artech.Genexus.Common`, `Artech.Architecture.Common`, `Artech.Common.Properties`,
`Artech.Packages.Patterns`, `Artech.Patterns.WorkWith*`, `DVelop.Patterns.WorkWithPlus`,
`Artech.Packages.TeamDevClient.BL`, `GeneXus.Server.Contracts`, `Artech.Packages.Comparer.BL`,
`Artech.Packages.KnowledgeManager`, `GeneXus.Packages.ModuleManager`, `Artech.Packages.GAM`,
`Artech.Packages.Specifier`, `Artech.LibraryDeployer`, `Artech.Generators`.

Sources: agent inventory of `src/GxMcp.Worker/Services` + `Helpers` (current coverage) and
`docs/sdk-probe/{INDEX,generators}.md` + `wwp-projection-discovery.md` (target surface).

Status legend: **✅ covered** · **🟡 partial** (hand-rolled / reflection / read-only / narrow) · **❌ gap** (no real coverage).

The read-only `genexus_sdk_probe { mode: "capabilities" }` contract now exposes
the current decision surface as `available_unverified`, `unavailable`, or
`deferred`. A reflected type is evidence of a loaded signature only;
`persistenceVerified=false` remains until a disposable fixture certifies the
roundtrip. Business Components are intentionally `deferred` because their
rules execute in a generated application runtime, outside the design-time SDK
worker.

## Build status — 2026-07-09 batch (code done, live-test pending)

Six IDE-parity tools built + integrated on local `main` (not pushed). Full build green; Gateway 548 + Worker 1234 tests pass; golden fixture regenerated (42 tools); schema budget 13300. Each uses the confirmed `Services.TryGetService<T>()` / static-Helper pattern with graceful `*Unavailable` fallback (never crashes the worker). **Input-construction feasibility gate passed for all — no WWP-style wall** (the SDK param objects are headless-constructible POCOs / static accessors, unlike WWP's obfuscated settings).

| Tool | SDK entry | Status | Live-check recipe |
|---|---|---|---|
| `genexus_compare` | `IComparerService.AreEqualInContent/AreEqualInProperties` | ✅ built | `objectA=__x__ objectB=__y__` → `ObjectNotFound`=service registers ✓ / `ComparerServiceUnavailable`=not |
| `genexus_merge` | `IMergeService.MergeObjects` (2-way, no ancestor wall) | ✅ built | `dryRun=true` on two real objects; `mode=models` returns honest `MergeModelsUnsupported` |
| `genexus_kb_version` | static `KBVersionHelper` (gate passed via live reflection) | ✅ built | `action=list` (read-only) → enumerates versions + active |
| `genexus_module` | `IModuleManagerService.Install/InstallByName/InstallBuiltIn/Update` | ✅ built | `action=list` (read-only) |
| `genexus_gam` | concrete `Artech.Packages.GAM.IntegratedSecurityService` (interface lacks `IGxService` → CS0311; cast workaround) | ✅ built | `action=status` (read-only) → IsEnabledIntegratedSecurity |
| `genexus_gxserver` +write | `IGXserverService.Commit/GetUpdateFile/Lock` + `ITeamDevClientService.MarkAsResolved` | ✅ built | needs GXserver-linked KB; `resolve` reachable, commit/update/lock POCOs constructible; live commit needs authed session |

Known honest caveats (in-code): `genexus_gxserver` `lock` FilePath semantics + `update` only downloads (no apply); `genexus_merge` models-mode unsupported (one KBModel/session). Live functional test of all six deferred to a running-binary round (user request: test at end).

## Coverage summary by domain

| Domain | Status | MCP tool(s) today | What's missing (SDK entry point to wire) |
|---|---|---|---|
| Objects & Parts CRUD | ✅ | `genexus_create`, `read`, `edit`, `delete_object`, `inspect` | — (core is solid) |
| Structure / Transaction / SDT | ✅ | `genexus_structure`, `variable` | Transaction default-form helpers (`DefaultFormHelper`) not exposed as an action |
| WebForm / Layout / UI | 🟡 | `genexus_layout`, `edit_form`, `properties` | HTML↔attr mapping is reflection-only; no typed `HtmlTagHelper`/`GxControlHelper` path |
| Patterns core | 🟡 | `genexus_apply_pattern` | Only `UpdateParentObject` of 9 `IPatternBuildProcess` hooks; no full build sequence |
| WorkWithPlus settings/components | ⛔ BLOCKED | — | Build works; **persist blocked** — typed `AddComponent` never flushes to the element tree; WWP persistence obfuscated. See `project_wwp_settings_components_persist_blocked`. |
| WWP instance apply/update (MSBuild) | 🟡 | `genexus_apply_pattern` | `WWP_UpdateInstance/UpdateAllInstances/ApplyAllInstances/GenerateSecurityPrograms` tasks |
| Refactor / rename / impact | 🟡 | `genexus_refactor`, `analyze`, `genexus_properties action=move` | Refactor/impact remains hand-rolled over index edges; move is SDK-backed with full snapshot verification |
| Properties (resolver-driven) | 🟡 | `genexus_properties` | Valid-values / visibility / readonly via `IResolverFactory` not surfaced |
| Build / Specify / Generate | 🟡 | `genexus_lifecycle`, `edit_and_build` | Reflection into MsBuild tasks; native `SpecifierService`/`BuildDaemon` not used |
| Deploy (Library / Azure / Cloud) | ❌ | — | `IDeploymentService.Deploy`, `LibraryDeployer.*`, `IAzureDeploymentService` |
| KB version / branch management | ❌ | `genexus_versioning` (history/undo only) | `KBVersionHelper.Freeze/Branch/SetAsActive/Revert` |
| Team Development (Commit/Update) | 🟡 | `genexus_gxserver` (read-only) | `IGXserverService.Commit/GetUpdateFile/Lock`; `ITeamDevClientService.MarkAsResolved` |
| CI pipelines (Team Dev client) | ❌ | — | `IContinuousIntegrationService.RunPipeline/GetPipelineRuns/EditPipeline` |
| Comparer / Diff / Merge | ❌ | `genexus_kb_diff` (filesystem only) | `ComparerService.AreEqualInContent`, `MergeService.Merge{Objects,Parts,Versions,Models}` |
| Knowledge Manager (XPZ) | ❌ | `genexus_io`/`kb_import` (fs copy, no deps) | `IKnowledgeManagerService.{ExploreExport,PrepareImport,ImportFile}` |
| Module Manager | ❌ | — | `IModuleManagerService.{Install,InstallByName,Update,InstallBuiltIn}` |
| GAM / Security | ❌ | `genexus_security` (env-scan + regex) | `IIntegratedSecurityService.{DefineAPI,Deploy,CreateTables}`; `DbmsHelper.*` |
| Index / Search / Navigation | ✅ | `genexus_query`, `search_source`, `navigation` | — |
| Validation / Linter | ✅ | `genexus_analyze`, `lifecycle` | — |

## Ranked gap list (by IDE-parity value)

Ranking = how much each blocks a real team from dropping the IDE. Not effort.

### P0 — hard blockers for "use MCP instead of IDE"

1. **Knowledge Manager — real XPZ export/import.** `genexus_io`/`kb_import` copy part files
   at the filesystem level and **do not resolve dependencies** — semantically wrong for the
   promote-to-prod flow (see `project_controleobjetos_promotion_flow`). Wire the SDK:
   - `IKnowledgeManagerService.ExploreExport(file, model, ExploreExportOptions, out objects/actions/idMap, …)`
   - `IKnowledgeManagerService.PrepareImport(...)` + `ImportFile(kbxFile, model, ImportOptions)`
   - `ExportOptions` (23 props) / `ImportOptions` (26 props) for identity/table/theme handling.

2. **Comparer / Merge.** Zero SDK coverage; `kb_diff` is a filesystem walk. No "Compare Objects",
   no version merge, no conflict resolution — a team on Team Dev can't work without it.
   - `ComparerService.AreEqualInContent(KBObject|KBObjectPart, …, CompareObjectOptions)`
   - `MergeService.MergeObjects/MergeParts/MergeVersions/MergeModels(...)`
   - UI-parity entry: `IComparerService.CompareObjects/CompareVersions/CompareWithActiveVersion`.

3. **Team Development write path.** `genexus_gxserver` is read-only (status/pending/conflicts).
   Commit/Update/Lock/resolve are the daily loop for a hosted KB.
   - `IGXserverService.Commit(ServerCommitData,…)`, `GetUpdateFile/GetPartialUpdateFile`, `Lock(ServerLockData)`
   - `ITeamDevClientService.MarkAsResolved / GetConflictEntities / GetObjectBeforeSynchronization`
   - client proxy: `TeamWorkService2Client.{Update,Commit,PartialUpdate,GetRevisionChanges}`.

### P1 — needed for full IDE parity, workarounds exist

4. **WorkWithPlus settings & components** *(current task)* — ⛔ **BLOCKED** (live spike 2026-07-09).
   - Build works: `WorkWithPlusSettings.Get(KBModel)` → `AddComponent(name)` → `SettingsComponentElement.{Table, AddEventBlock, AddDefinedVariable}`; shows in typed Components collection.
   - **Persist fails via every safe path:** `AddComponent` mutates a throwaway typed view; the mutation never reaches the persisted element tree (XML byte-identical before/after), `Save()` writes the object without the component, re-read returns `[]`. 6 settings-scoped statics + 10 zero-arg instance methods — none flush. Decompiling WWP's real save routine → `BadImageFormatException` (method-body obfuscation, needs de4dot).
   - Only path left: raw-`<Component>`-XML injection via `DeserializeFromXml` — high blast radius (KB-global settings), needs the Component XML schema reverse-engineered from WWP package defaults. Deliberate sandbox spike, not a bolt-on. Parked. Full verdict: `project_wwp_settings_components_persist_blocked`.

5. **Full pattern build sequence.** Only `IPatternBuildProcess.UpdateParentObject` is invoked; the
   other 8 hooks (`BeforeStartBuild`, `BeforeGenerateObjects`, `BeforeSaveObjects`, `AfterSaveObjects`, …)
   plus `PatternImplementation.{InitializeBatch,CleanupBatch}` are what the IDE runs — likely the fix
   for the stale-reapply gap (`wwp-projection-discovery.md` §Known gap).

6. **WWP instance maintenance.** `genexus_apply_pattern` applies; it can't update/refresh across a KB.
   - `WWP_UpdateInstance / WWP_UpdateAllInstances / WWP_ApplyAllInstances / WWP_MarkAllInstancesAsUpdated / WWP_GenerateSecurityProgramsAction` (`Execute() -> bool` MSBuild tasks).

7. **KB version / branch management.** `genexus_versioning` covers object history/undo/time-travel but
   not model versions. `KBVersionHelper.{FreezeModel, BranchModel, ModelToBranch, SetAsActive, Revert}`
   — "Create Version" / "Branch" / "Activate" / "Revert".

8. **Deploy.** No deploy action at all.
   - `IDeploymentService.Deploy(KBModel)`; `LibraryDeployer.*ConnectionHelper.BuildJdbcUrl` + `ConnectionHelperFactory.GetHelper(dbms)` + `ExecHelper.ExecuteCommand`; `IAzureDeploymentService`, `ICloudPrototypingService`.

9. **Module Manager.** GX18 modularity is unusable headless.
   - `IModuleManagerService.{Install(model, ModulePackage|opcFile), InstallByName(model,name,version), Update, InstallBuiltIn}`; `ObjectModuleHelper.{GetModuleAssociation, UsesModule}`.

10. **GAM / Security provisioning.** `genexus_security` only scans; can't deploy security.
    - `IIntegratedSecurityService.{DefineAPI(env,force), Deploy(env,…), CreateTables(env)}`; `DbmsHelper.{ExecuteRepositoryCreation, ExecuteMetadataInitialization, ExecuteApplicationRegistration}`.

### P2 — polish / completeness

11. **Native build daemon.** Replace reflection-into-MsBuild-tasks with `SpecifierService.{SpecifyAll,SpecifyObjects,RebuildArtifacts,CreateDatabase}` + `BuildDaemonClient*` for cleaner, cancelable builds.
12. **Resolver-driven properties.** Surface valid-values / visibility / readonly via `IResolverFactory.{GetValuesResolver,GetVisibleResolver,GetReadOnlyResolver}` so agents get the same constrained property choices the IDE grid shows — not blind set.
13. ~~**Move object** to folder/module~~ — shipped in v2.35.0 and hardened after U16 exposed destructive `SaveWithParent` behavior. It now prefers `EntityManager.UpdateParent`, snapshots and verifies every part, supports dry-run/concurrency, and rolls back divergence.
14. **CI pipelines** from the Team Dev client (`IContinuousIntegrationService.RunPipeline/…`) — nice-to-have once Commit/Update land.
15. **New-object templates catalog** (`ObjectDefinitionHelper.LoadDefinitionsFor`) to mirror the IDE "New Object" template picker.

## Follow-up digs (surface not fully resolved in this pass)

- `Artech.Genexus.Common.Objects.*` concrete classes (Transaction/Procedure/WebForm/SDT/WebComponent) were
  not walked method-by-method — `generators.md` only carries generator-shaped types. Re-run
  `genexus_sdk_probe` and grep `raw.json` for these to complete Objects/Structure detail.
- No standalone SDK `RefactorService` type exists — rename is genuinely hand-rolled here; confirm there's
  no `IRenameService` in a non-action assembly before treating refactor as "SDK-native possible".
- WWP writable-settings load/save (P1 #4) is the one make-or-break unknown — resolve before building the
  Component-generation tool.
