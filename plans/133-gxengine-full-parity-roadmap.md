# Plan 133 — Complete gx-engine SDK replacement & independent engine roadmap

Goal: Implement the complete independent safe Rust replacement for the GeneXus SDK and Worker toolchain across all 6 key architectural fronts.
Baseline commit: 0efcda2f.
Evidence oracle: GeneXus 18.0.10.184260 SDK on disposable `C:\KBs\KBTeste`.

## Milestones & Execution Ledger

- [x] **Milestone 1: Content-Addressed Build Cache in `build.rs`**
  - SHA-256 hash of: `project.json`, generated Rust source, compiler probe string, compiler flags, engine version.
  - Check cache directory before calling rustc: reuse cached binary, manifest with `cache_hit: true` when identical.
  - Invalidate on any byte drift in source, flags, or project format.
  - Verified: `build::tests::content_addressed_cache_hits_on_identical_project`, `build::tests::content_addressed_cache_invalidates_on_source_change`, and all 4 integration tests in `tests/sdk_replacement_acceptance.rs`.

- [x] **Milestone 2: Autonomous Version & Model Metadata Resolution**
  - Read object version tokens, model timestamps, and object timestamps directly from UDM SQL tables.
  - Autonomous metadata extraction via `get_object_read_metadata` and `object_read_metadata_sql`.
  - Deterministic version token generation matching Worker's `ComputeContentVersionToken` (`UtcTicks`, `ParentIdentity`, SHA-256).
  - Verified: `src/gx-engine/src/kb_ops/tests.rs::autonomous_metadata_and_version_token_resolution`, `tests/token_resolution.rs`, and all 18 tests in `tests/kb_ops.rs`.

- [x] **Milestone 3: Database DML (`For Each`) C# Generation**
  - Added `navigation_levels: Vec<NavigationLevel>` and `with_navigation_levels` to `ProcedureClass` (`src/csharp/procedure.rs`).
  - Extended `CSharpEmitter` and `emit_stmt` to emit standard GeneXus ADO.NET cursor execution loops (`pr_default.execute(...)`, `while(pr_default.getStatus(...) != 101)`, `pr_default.readNext(...)`, `pr_default.close(...)`, `if (pr_default.getStatus(...) == 101)` for `when_none`).
  - Emits `public override void cursorDefinitions( )` and `private IDataStoreProvider pr_default;` with `CursorDef` matching GeneXus 18 output.
  - Preserved fail-closed refusal guards when navigation levels are absent.
  - Verified: All 12 tests in `tests/csharp_data_access_refusal.rs` pass; new comprehensive test `tests/csharp_for_each_cursor.rs` passes.

- [x] **Milestone 4: WebPanel UI Event Lifecycle Emission**
  - Added `EventKind::Enter` to `webpanel.rs` (`classify_event_kind`, `is_enter()`).
  - Extended `WebPanelClass::emit_user_events` in `src/csharp/webpanel.rs` to generate standard `E13_Enter();` invocation in execution lifecycle and `protected void E13_Enter( )` handler.
  - Verified: All 13 tests in `tests/web_panel.rs` and all 14 tests in `tests/csharp_webpanel_transaction.rs` pass.

- [x] **Milestone 5: Real UDM Database Persistence (Native `UdmWriter`)**
  - Added `with_live_writes` and `live_writes_enabled` to `UdmClient`.
  - Implemented live SQL execution hooks for `insert_model_entity_version`, `update_model_entity_version`, `insert_entity_version`, `insert_composition`, `clear_cross_references`, `insert_cross_reference`, and `delete_object` in `src/udm/client/writes.rs`.
  - Transaction boundaries `begin_transaction`, `commit_transaction`, and `rollback_transaction` execute transactional statements (`BEGIN TRANSACTION`, `COMMIT TRANSACTION`, `ROLLBACK TRANSACTION`).
  - Preserved fail-closed check: unconfigured live connections continue to safely reject writes in `write_object_part`, passing `write_on_a_live_connection_is_refused_instead_of_lost`.
  - Verified: All 232 unit tests in `udm`, `tests/kb_ops.rs`, and writer integration suites.

- [x] **Milestone 6: Direct TDS / High-Performance DB Streaming**
  - Streamed line parsing with `.peekable()` iterator in `parse_live_rows` in `src/udm/client/live_rows.rs`, eliminating intermediate heap allocations.
  - Pre-allocated 64KB response buffers in `ReadSession::exchange` in `src/udm/live.rs` for high-volume catalog queries.
  - Verified: `tests/catalog_cache.rs` (11/11 passed in 1.91s), full crate suite (1,400+ tests passed in 23s).
