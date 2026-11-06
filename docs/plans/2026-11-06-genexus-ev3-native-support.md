# GeneXus X Evolution 3: native SDK support

Status: in progress. This plan moves GeneXus X Evolution 3 (10.3) from the
"basic legacy compatibility" table to native SDK support, the same contract
GeneXus 16, 17 and 18 have: a Worker compiled against the installed SDK, live
tests on a disposable KB, and tools that either work with verified persistence
or refuse before touching the KB.

## What was measured (2026-11-06)

Installation: `C:\Program Files (x86)\ARTech\GeneXus\GeneXusXEv3`
(the catalog default `C:\Program Files (x86)\GeneXus\GeneXusXEv3` is wrong for
the official installer). `GeneXus.exe` ProductVersion `10.3.16.146128`,
FileVersion `10.3.16.15056`. SDK assembly version `10.1.0.0`
(GeneXus 18: `11.0.0.0`). WorkWithPlus 11.138 is installed under
`Packages\Patterns\WorkWithPlus`.

| Question | Evidence | Result |
|---|---|---|
| Runtime of the SDK | `ImageRuntimeVersion` of the SDK assemblies | CLR 2.0 (`v2.0.50727`); `GeneXus.exe.config` has no `supportedRuntime`, so the IDE runs on CLR 2 |
| Mixed-mode assemblies | PE COR header ILONLY flag over 281 DLLs | 238 IL-only, 24 native, 19 mixed (parsers, specifier, generators, `gxio`, `udmgxio`). A CLR 4 host needs `useLegacyV2RuntimeActivationPolicy="true"` to load them |
| Can a CLR 4 / x86 process host the SDK? | Probe (net48, x86, legacy-V2 activation): `Connector.Initialize/Start`, `KnowledgeBase.Open`, create, save, reread and delete a Procedure | Yes. Open 1.0 s, 434 objects listed, Procedure save 434 ms, reread with marker, delete confirmed |
| Worker bootstrap steps | Same probe | `ContextService`, `CommonServices`, `UIServices.SetDisableUI/Initialize` and `KBModelObjectsInitializer.Initialize` do not exist in Ev3; the Worker already skips missing ones (null-safe reflection). `Connector` + `KBFactory` suffice |
| Disposable KB | Ev3 `CreateKnowledgeBase` MSBuild task run by CLR 4 MSBuild with `(localdb)\MSSQLLocalDB` | `C:\KBs\KBTesteEv3` created from `csharp.kbtemplate`. The CLR 2 MSBuild (3.5) cannot reach LocalDB (old SqlClient), so test KBs are created through CLR 4 |
| Compile gap of the Worker | `dotnet build` of `GxMcp.Worker` with `GX_PATH` = Ev3 | First pass: 29 errors in 5 files (module manager/OPC, Trial cap, `FailIfReorg`). With those behind the `evolution3` profile: 47 errors in 19 files (list below) |
| String-named SDK types the Worker resolves by reflection | 53 type names used by the Worker that exist in GeneXus 18, checked against the Ev3 type list (13,790 public types) | 4 missing: `ObjectNameHelper`, `KBModelVersionObjects`, `DesignSystemHelper`, `ICurlGeneratorService` |

## Design

1. **Build profile, not a reflection driver.** The Worker is compiled against
   the Ev3 SDK with `-p:GxMcpSdkProfile=evolution3` (constant `GXMCP_SDK_EV3`).
   Features Ev3 does not have are compiled out and replaced by a typed refusal
   (`UNSUPPORTED_IN_GENEXUS_VERSION`, `persisted=false`) that is returned before
   any KB access. The `dotnet-reflection` legacy driver for 10.3 is retired once
   this lands.
2. **Safety flags are never emulated.** Where GeneXus 18 relies on an SDK flag
   that Ev3 lacks (`BuildOptions.FailIfReorg`), the Ev3 Worker refuses the
   operation instead of running it without the guard.
3. **Host config.** The Ev3 Worker ships an `App.config` with
   `useLegacyV2RuntimeActivationPolicy="true"`.
4. **Catalog.** `10.3` moves from `legacyMajors` to `supportedMajors` with the
   real default path and an `sdk-compatibility-ev3.json` fingerprint manifest;
   `genexus_sdk_probe mode=capabilities` reports the refused tools.
5. **Evidence.** Live tests run on `C:\KBs\KBTesteEv3` (and other `*teste*`
   KBs only): open, list, read every part type present, create/edit/save/reread
   /delete for Procedure, Transaction, WebPanel, SDT, Domain, DataProvider,
   and WorkWithPlus 11.138 apply on a test Transaction. A tool counts as
   supported only when its live test passes; the rest are refused.

## Compile gap with the `evolution3` profile (47 errors, 19 files)

| Area | Files | Ev3 gap | Plan |
|---|---|---|---|
| GXserver / TeamDev | `GxServerWriteService` (8) | token auth, `GetServerInfo`, `ExportAll` | refuse |
| Object model | `ObjectService` (5), `ObjectService.Move` (1), `WriteService` (1) | `Stencil`, `UserControl`, `ColorPalette`, `ObjectNameHelper`, `IsInterface`, `LastUpdate` read-only | map the type list; name helper via `KBObject` API; skip `LastUpdate` |
| Variables | `VariableInjector` (4) | `ResolveName` signature | Ev3 overload |
| WorkWithPlus / patterns | `WwpSdkComboElement` (3), `PatternPropertyPreflight`, `PatternSettingsService`, `SdkPatternSpecView` | `GetValueString`, `TryFindChildElement`, `AttributeNameToPropertyBagName` | port to the Ev3 pattern API, verify live with WWP 11.138 |
| Transfer (XPZ) | `TransferService` (3) | `RollBackOnError`, `SilentDefault`, `ExportCurrentVersion` | Ev3 options; import without rollback is refused |
| Compare / merge / versions | `CompareService` (3), `MergeToolService` (3), `KbVersionService` (2) | compare options, `SetAsActive` | port or refuse |
| Build | `InProcessBuildRunner` (2), `NoReorgBuildCommandFactory` (1) | `FailIfReorg`, `QualifiedName` overload | refuse builds that need the reorg guard |
| Other | `DataSelectorReadService` (3), `DeployService` (2), `UserControlsListService` (2), `PropertyService` (1) | newer table/deploy/user-control APIs | port or refuse |

## State after the first pass (2026-11-06)

- The Worker compiles with both profiles: GeneXus 18 (default, unchanged; full
  test suite green) and `evolution3` (0 errors). `build.ps1` builds the Ev3
  Worker into `publish\worker-ev3` when an Ev3 installation is found.
- The Gateway starts `worker-ev3` for KBs whose installation is 10.3
  (`WorkerProcess.SelectSdkProfileWorker`), or reports `GXMCP_EV3_WORKER_MISSING`.
- Live, through the published Gateway on `C:\KBs\KBTesteEv3`: list, query,
  read; create / edit / read-back / delete of a Procedure; create of Domain,
  Transaction, SDT, WebPanel (Events + auto-declared variable) and DataProvider
  (Source edit and read-back), each with `persistedVerified`. `genexus_module`
  answers with a typed refusal.
- Fixed on both SDKs: a Transaction attribute based on a Domain read back as its
  primitive type, so a persisted `Attr : Domain` write failed its own
  round-trip check.

Second pass, both fixed on both SDKs and verified live on `KBTesteEv3`
(`persistedVerified`, re-read and a fresh SDK session agree):

1. Transaction Structure reported `WriteNotPersisted` for a persisted
   Domain-typed attribute: the round-trip comparison kept the rendered
   `// "Description"` comment as part of the type, and a Domain type has no
   `(len)` that cut it off. The comparison now drops the comment.
2. SDT Structure never added a new member typed by a Domain: `AddItem` needs an
   `eDBType` and a Domain name is not one. The member is now created with the
   Domain's primitive type and then bound to the Domain (`Domain.Type` hides
   `KBObject.Type`, so it is looked up by its type, not by name).

Open gap:

3. WorkWithPlus 11.138: not exercised yet.

## Order of work

1. Compile the Worker for Ev3: done.
2. Start the Ev3 Worker against `KBTesteEv3`, list and read: done.
3. Live write tests per object type: Procedure, Domain, Transaction, SDT,
   WebPanel, DataProvider: done.
4. WorkWithPlus 11.138.
5. Move `10.3` to `supportedMajors` with a fingerprint manifest, docs and
   release notes, once 3 and 4 pass.
