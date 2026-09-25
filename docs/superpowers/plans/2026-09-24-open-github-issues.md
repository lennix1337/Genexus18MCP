# Open GitHub Issues Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the 14 issues currently open in `lennix1337/Genexus18MCP` (#268 and #304–#316) without publishing a release or closing issues, while preserving fail-closed KB mutation semantics.

**Architecture:** Keep the existing Gateway → Worker → GeneXus SDK boundary. Treat visual XML as untrusted input: detect legacy WebForm flavour, preserve untouched attributes, verify the SDK re-read, and restore only through a version-conditional path. Use the existing typed operation registry, STA scheduler, source store, warm snapshots, and pattern/layout services rather than adding parallel protocols. Every published action change is synchronized across `tool_definitions.json`, routers/dispatch, help, capability inventory, generated operation inventory, tests, and `CHANGELOG.md`.

**Tech Stack:** C#/.NET 10 Gateway, C#/.NET Framework 4.8 STA Worker, GeneXus 16/17/18 native SDKs, xUnit, Newtonsoft.Json, Node.js 22, PowerShell 7, Python contract validators, GitHub CLI.

---

## Task 1: Freeze the issue and contract baseline

**Files:**
- Read: `AGENTS.md`, `docs/agent_playbook.md`, `CHANGELOG.md`
- Read: issue bodies for #268 and #304–#316 via `gh issue view`
- Inspect: `src/GxMcp.Gateway/tool_definitions.json`, `src/GxMcp.Gateway/ToolHelpCatalog.cs`, `src/GxMcp.Worker/Services/CommandDispatcher.cs`, generated inventories and current tests

- [ ] Record the 14 open issue URLs, the exact acceptance criteria, the current commit (`82d273e5a`), and a clean-worktree/upstream-drift result.
- [ ] Map each issue to its existing route and test fixture before editing; do not infer a fix from the issue's suggested patch alone.
- [ ] Keep the working tree free of unrelated user changes and do not commit, push, release, or close issues.

## Task 2: Make legacy WebForm writes safe (#308, #309, #310)

**Files:**
- Modify: `src/GxMcp.Worker/Helpers/WebFormTypedPropertyWriter.cs`
- Modify: `src/GxMcp.Worker/Helpers/WebFormSchemaHints.cs`
- Modify: `src/GxMcp.Worker/Services/WebFormEditService.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.VisualWrite.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.PatchUtils.cs` if the preflight clone path requires the shared transformation
- Test: `src/GxMcp.Worker.Tests/WebFormTypedPropertyAutoRouteTests.cs`
- Test: `src/GxMcp.Worker.Tests/WebFormEditServiceTests.cs`
- Test: `src/GxMcp.Worker.Tests/WriteVerificationRestoreHintTests.cs`
- Add: focused legacy `<BODY>` caption-preservation and verification/rollback regression tests

- [ ] Add a flavour detector that distinguishes legacy `<BODY>` HTML from `GxMultiForm`/modern visual XML and prevents the descriptor `CaptionExpression` → `Caption` rewrite on the legacy form.
- [ ] Limit descriptor fix-ups to nodes changed by the current write and remove a source attribute only after the canonical replacement is observed; an absent replacement must fail closed and preserve the source.
- [ ] Generate legacy `CaptionExpression` Tokens for `add_button`/`add_textblock`, clone sibling attribute shape when possible, normalize an event supplied as `Event` or `'Event'`, and allow an explicit `controlId` while retaining a deterministic generated fallback.
- [ ] Classify legacy attributes as accepted or explicitly unverified instead of asserting they will be sanitized; update the schema/help text and examples.
- [ ] Make visual dry-run and `validate:\"only\"` execute the same in-memory transformation on a cloned XML document and report bounded attribute deltas.
- [ ] On post-save mismatch, report physical persistence separately, honor `rollbackOnFailure` with a pre-write snapshot restore, and use a valid `genexus_versioning action=history_restore` next step when an atomic restore is unavailable.
- [ ] Run the focused Worker tests before any broader build.

## Task 3: Correct mutation recovery fences (#311)

**Files:**
- Modify: `src/GxMcp.Gateway/Program.ToolPayload.cs`
- Modify: `src/GxMcp.Gateway/Program.ToolDispatch.cs`
- Modify: `src/GxMcp.Gateway/MutationRecoveryRegistry.cs`
- Modify: `src/GxMcp.Gateway/LifecycleResponseShaper.cs` or the lifecycle result shaper used by timed-out operations
- Test: `src/GxMcp.Gateway.Tests/MutationRecoveryRegistryTests.cs`
- Add: timed-out variable recovery and late-operation reconciliation tests

- [ ] Map each mutating tool to the real affected part (`genexus_variable` → `Variables`) rather than defaulting an absent `part` to `Source`.
- [ ] Accept a complete whole-object read (`FullObjectRead`) or a complete read of the affected part as recovery evidence, using its version token without accepting truncated windows.
- [ ] Reconcile a terminal late worker result for the same operation before requiring another read, and never claim the fence was cleared without a verified readback.
- [ ] Emit the exact affected part and `limit: 0` in the recovery hint; keep cancellation and unknown-result paths fail-closed.
- [ ] Add regression coverage for a 90-second simulated variable timeout, terminal late success, full-object read, truncated read, and a newer concurrent version.

## Task 4: Restore source-search scope correctness (#312)

**Files:**
- Modify: `src/GxMcp.Worker/Services/SourceStoreService.cs`
- Modify: `src/GxMcp.Worker/Services/SourceSearchService.cs`
- Modify: `src/GxMcp.Worker/Services/IndexCacheService.cs` if part-level store keys are required
- Test: `src/GxMcp.Worker.Tests/SourceSearchPrimaryPartTests.cs`
- Test: `src/GxMcp.Worker.Tests/SourceSearchScopeTests.cs`
- Add: store/SDK mixed-scope regression tests

- [ ] Treat scope/fields as part-level eligibility, not object-level eligibility; only skip the SDK read for a part that is stored, fresh, and actually requested.
- [ ] Search stored and missing parts independently for the same object, preserve the requested part label on every hit, and report coverage per requested part.
- [ ] Keep the primary-part optimization for default/source searches without making it override explicit `webForm`, `rules`, `conditions`, or `layout` requests.
- [ ] Add the Events-stored + WebForm-requested regression and Rules/Conditions mixed-scope regressions from the issue.

## Task 5: Add typed variable dimensions (#315)

**Files:**
- Modify: `src/GxMcp.Worker/Services/VariableService.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.Variables.cs`
- Modify: `src/GxMcp.Gateway/Program.cs` or the existing variable router/dispatcher
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Modify: `src/GxMcp.Gateway/ToolHelpCatalog.cs`
- Test: `src/GxMcp.Worker.Tests/VariableDeclarationParserTests.cs`
- Add: variable read/write dimension and collection-conflict tests

- [ ] Add `dimensions` (integer 1 or 2) and `dimensionSizes` (positive integer array with matching length) to single and batch variable schemas.
- [ ] Resolve the SDK dimension properties through the existing major-compatible reflection helpers and persist both dimensions and sizes for add/modify.
- [ ] Read dimension metadata from the SDK and expose it in `genexus_read part=Variables` and `genexus_inspect include=[\"variables\"]`.
- [ ] Reject `collection=true` combined with dimensions with an actionable validation error and reject malformed sizes before any SDK mutation.
- [ ] Verify persisted dimension identity with an independent reread and preserve the existing attribute/domain/SDT binding semantics.

## Task 6: Close the three contract papercuts (#316)

**Files:**
- Modify: `src/GxMcp.Worker/Services/ObjectService.cs` or inspect projection service
- Modify: `src/GxMcp.Worker/Services/ReadService.cs` or the common part-windowing helper
- Modify: `src/GxMcp.Gateway/Program.LifecycleGateway.cs`
- Modify: `src/GxMcp.Gateway/OperationTracker.cs`
- Test: inspect projection tests, read windowing tests, and lifecycle wait tests

- [ ] Resolve `include=[\"variables\"]` for K2B WebPanels through the same Variables part reader used by explicit reads, and return an explicit unsupported reason only when the SDK truly cannot expose the part.
- [ ] Apply the common line-windowing contract to multi-line WebForm/Layout XML, including `totalLines`, `offset`, `limit`, and truncation metadata; preserve `limit=0` as complete output.
- [ ] Make lifecycle status wait on every tracked operation kind, including timed-out `op:<id>` operations, until state change/terminal or the requested cap; do not report `Running` immediately when the operation has not transitioned.
- [ ] Add regressions for K2B variables, WebForm windows, and an operation that changes after the initial status call.

## Task 7: Extend typed WorkWithPlus grid actions (#268)

**Files:**
- Modify: `src/GxMcp.Worker/Services/WwpActionService.Grid.cs`
- Modify: `src/GxMcp.Worker/Services/WwpActionService.cs` and router/dispatcher only if action registration is split
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Modify: `src/GxMcp.Gateway/ToolHelpCatalog.cs`
- Test: `src/GxMcp.Worker.Tests/WwpActionServiceTests.cs`
- Add: move-column and presentation-variable contract tests

- [ ] Add `move_grid_column` and `add_grid_variable` with typed attribute/variable identity, optional relative placement, caption, and Character/VarChar length rules.
- [ ] Require an expected version even for previews, resolve the instance and parent by SDK identity, and reject ambiguous/homonymous owners.
- [ ] Preserve baseline metadata, bindings, filters, actions, exports, and protected code; project only the requested structural change.
- [ ] Verify the PatternInstance, projected WebForm order/caption/binding, and variable declarations after save; abort before commit on divergence and never force-write over a newer version.
- [ ] Keep `genexus_wwp` typed and do not add implicit lifecycle/build/test calls.

## Task 8: Add report print-block control operations (#314)

**Files:**
- Modify: `src/GxMcp.Worker/Services/LayoutService.cs`
- Modify: `src/GxMcp.Worker/Services/LayoutService.VisualContext.cs`
- Modify: `src/GxMcp.Worker/Services/LayoutService.SourcePersistence.cs`
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Modify: `src/GxMcp.Gateway/ToolHelpCatalog.cs`
- Test: layout service and report-layout fixture tests

- [ ] Extend the report tree with control geometry and font metadata and add `add_report_control`, `move_report_control`, and `remove_report_control` actions.
- [ ] Validate print-block ownership, control kind/binding/caption, relative placement, and non-overlapping geometry before generating XML.
- [ ] Reuse the visual snapshot/base-version/dry-run/independent-reread/rollback path and reject a projected layout that does not match the requested report structure.
- [ ] Add focused fixtures for add, move, remove, dry-run, stale-version rejection, and rollback.

## Task 9: Recognize and safely model K2BTools WebPanel Designer objects (#313)

**Files:**
- Modify: `src/GxMcp.Worker/Services/PatternApplyService.cs` or pattern metadata resolver
- Modify: `src/GxMcp.Worker/Services/WwpActionService.cs` only where shared typed grid parsing belongs
- Add: `src/GxMcp.Worker/Services/K2bWebPanelDesignerService.cs` if no existing owner exists
- Modify: `src/GxMcp.Gateway/tool_definitions.json` and help only if a new action is published
- Test: pattern metadata, K2B marker, protected editor-block, and safe-edit tests

- [ ] Detect `PATTERN_ELEMENT_CUSTOM_PROPERTIES` and K2B editor markers before returning `WWPInstanceNotFound` or an unknown-pattern error.
- [ ] Parse grid name, base table, key attributes, columns, selection mode, and decoded `ControlWhere`/`ControlOrder` into a typed read model.
- [ ] Refuse edits to `// ---- K2BTools - Do Not Change` blocks unless the SDK exposes and verifies the same regeneration path used by the IDE.
- [ ] Provide a safe typed edit only when the native PatternInstance/editor regeneration contract can be verified; otherwise return a structured unsupported envelope with the exact protected blocks and recovery path.
- [ ] Keep unrelated pattern routes and ownership diagnostics unchanged.

## Task 10: Make runtime GC process-safe and atomic (#304)

**Files:**
- Modify: `cli/lib/runtime-stager.js`
- Modify: `cli/commands/axi.js`
- Test: `cli/lib/runtime-stager.test.js`
- Test: `cli/run.test.js` if doctor output is asserted

- [ ] Resolve running Gateway/broker/Worker executable paths against the runtime root and retain every directory with a live process.
- [ ] Rename a candidate directory to a unique `.deleting-<pid>` name before recursive removal; if rename fails, leave the original directory untouched.
- [ ] Return skipped/in-use directories and PIDs to doctor, while preserving best-effort cleanup for unused runtimes.
- [ ] Add simulated live-process, rename-failure, unused-runtime, and partial-deletion regression tests.

## Task 11: Complete lifecycle operation semantics (#305)

**Files:**
- Modify: `src/GxMcp.Gateway/OperationTracker.cs`
- Modify: `src/GxMcp.Gateway/BackgroundJobRegistry.cs`
- Modify: `src/GxMcp.Gateway/Program.LifecycleGateway.cs`
- Modify: `src/GxMcp.Gateway/Program.ToolDispatch.cs`
- Modify: `src/GxMcp.Worker/Services/BuildService.cs` and remaining long-action services
- Modify: `src/GxMcp.Gateway/LifecycleResponseShaper.cs`
- Modify: schema/help/inventory
- Test: `src/GxMcp.Gateway.Tests/OperationTrackerTests.cs`, lifecycle async/result tests, and focused Worker build tests

- [ ] Decide queue admission before STA wait, maintain a per-Worker FIFO, coalesce identical normalized requests, and return `queued`, `queuePosition`, and the shared `operationId` without changing concurrent-build opt-in behavior.
- [ ] Add `until=change|terminal`, make terminal the default for `wait_until_done`, and return warning deltas after the `since` cursor while retaining the full list on `result`.
- [ ] Resolve `op:<id>`, bare Gateway operation IDs, and Worker task IDs through one aliasing function and distinguish malformed IDs from expired IDs.
- [ ] Route `validate-kb`, `reorg`, and forced index through the same operation registry and wait/cancel contract.
- [ ] Add queue, coalescing, delta, alias, timeout, and multi-target lifecycle tests.

## Task 12: Make STA fairness and scans effective (#306)

**Files:**
- Modify: `src/GxMcp.Worker/SharedWorkerHostProtocol.cs` or the request-rewrite implementation
- Modify: `src/GxMcp.Gateway/SharedWorkerConnection.cs`
- Modify: `src/GxMcp.Worker/Services/StaScheduler.cs`
- Modify: `src/GxMcp.Worker/Services/SourceSearchService.cs`
- Modify: cache/read routing in the existing Worker command path
- Test: protocol, scheduler, source-search slice, and cached-read tests

- [ ] Inject `_meta.attachmentId` in shared-host child requests and `_meta.sessionId` in isolated requests so round-robin buckets identify clients.
- [ ] Slice server-owned scans at a bounded object/time budget, persist continuation state, and re-enqueue at P2 while preserving the public cursor contract.
- [ ] Serve exact cached reads off-STA during an unsliceable specify/build and include `servedFrom: cache` plus a cached version token.
- [ ] Add two-attachment fairness, bounded slice, cache-read, and protocol identity regressions.

## Task 13: Backfill and warm-refresh the source store (#307)

**Files:**
- Modify: `src/GxMcp.Worker/Services/SourceStoreService.cs`
- Modify: `src/GxMcp.Worker/Services/StaScheduler.cs`
- Modify: `src/GxMcp.Worker/Services/IndexCacheService.cs` and warm snapshot metadata
- Modify: `src/GxMcp.Worker/Configuration.cs`, Gateway configuration/diagnostics, and schema/docs for `Server.SourceStoreBackfill`
- Test: `src/GxMcp.Worker.Tests/SourceStoreServiceTests.cs`, `SourceStoreBenchmarkTests.cs`, warm-start tests, and configuration tests

- [ ] Add `auto|off` backfill configuration with a bounded slice (250 ms or 50 objects), recent-update-first ordering, and a persisted cursor/progress record.
- [ ] Schedule one backfill per open KB at P2, release the STA after every slice, and report stored/stale/total counts, state, and ETA in coverage/doctor/whoami.
- [ ] On warm start, compare stored object last-update metadata and refresh only stale entries; never discard a valid newer snapshot.
- [ ] Remove the obsolete 8 MiB full-source cap only after store completeness is certified; retain the SDK fallback for missing/corrupt records.
- [ ] Add restart-resume, stale-delta, off-mode, slice-budget, and synthetic coverage tests.

## Task 14: Synchronize contracts, changelog, and validation

**Files:**
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Modify: `src/GxMcp.Gateway/ToolHelpCatalog.cs`
- Modify: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`
- Modify: `docs/mcp_capabilities_inventory.md`
- Regenerate: `docs/operation-contract-inventory.json`
- Modify: `CHANGELOG.md` under `## Unreleased`

- [ ] Update schemas, help, routers, dispatchers, mutation classification, and invalidation tests for every published action/field.
- [ ] Add one Unreleased changelog bullet per fixed issue, including each canonical `https://github.com/lennix1337/Genexus18MCP/issues/<N>` URL.
- [ ] Run the focused test for each changed subsystem, then `python scripts/validate-tool-contracts.py`, `python scripts/generate-operation-contract-inventory.py --check`, `npm test`, `npm run lint`, solution build/tests, and Worker builds for installed GeneXus majors.
- [ ] Run the smallest available live smoke with an explicit KB and `GXMCP_LOG_DIR`; record `workerPid`, selection state, error count, and log path. Mark SDK/license/KB-dependent gates `unavailable`, never pass.
- [ ] Review `git diff --check`, final status, generated drift, secrets, and scope. Do not commit, push, release, or close issues.

---

## Self-review checklist

- [ ] All 14 issue URLs map to at least one implementation task and regression test.
- [ ] Visual writes cannot silently drop legacy captions or report an unverified rollback as persisted-safe.
- [ ] Recovery, search scope, and lifecycle IDs fail closed on incomplete or ambiguous evidence.
- [ ] Shared-host fairness and source backfill never hold the STA for an unbounded slice.
- [ ] Tool schema, help, discovery golden, capabilities inventory, and generated operation inventory remain synchronized.
- [ ] Every live-only claim has explicit evidence or is reported as unavailable.
