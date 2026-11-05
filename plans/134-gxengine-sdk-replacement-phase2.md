# Plan 134 — gx-engine SDK Replacement & Expansion (Phase 2)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Expand native `gx-engine` coverage and MCP Gateway routing to replace the GeneXus SDK across native edits, expanded reads (Variables, SDT Structure, DataProvider), pattern instance UDM inspection, and Business Component C# generation.

**Architecture:**
1. Gateway MCP: Implement `EngineEditContract.cs` to validate single-part textual edits (`genexus_edit` -> `gx-engine kb-write`), while keeping Gateway `DispatchEngineEdit` deferred to the SDK until IDE pattern synchronization and cross-reference integrity are natively maintained.
2. Engine UDM & Reads: Expand `kb-read` and `source_projection.rs` to decode `Variables` XML, `Structure` XML, and `PatternInstance` parts directly from UDM storage, while keeping `EngineReadContract.cs` restricted to verbatim text parts (`Source`, `Events`, `Rules`, `Conditions`).
3. Pattern Metadata: Add native extraction of `PatternInstance` parts directly from UDM `EntityPart` tables in `gx-engine`.
4. C# Generator: Integrate `TransactionClass` Business Component (`[Transaction]BC.cs` with class `<name>_bc`) emission into `rebuild-all`.

**Tech Stack:** Rust (edition 2021, `gx-engine`, `tiberius`, `tokio`), C# (.NET 10 `GxMcp.Gateway`), T-SQL (UDM SQL Server/LocalDB), JSON-RPC MCP.

---

### Task 1: Expand `EngineReadContract` & `kb-read` for SDTs, DataProviders, DataSelectors and `Variables`

**Files:**
- Modify: `src/GxMcp.Gateway/EngineReadContract.cs`
- Modify: `src/gx-engine/src/kb_ops/source_projection.rs`
- Modify: `src/gx-engine/src/kb_command.rs`
- Test: `src/GxMcp.Gateway.Tests/EngineReadContractTests.cs`
- Test: `src/gx-engine/tests/kb_ops.rs`

**Step 1: Write the failing tests**
- In `src/GxMcp.Gateway.Tests/EngineReadContractTests.cs`, assert that `EngineReadContract.TryPlan` accepts `part: "Variables"` and `part: "Structure"`.
- In `src/gx-engine/tests/kb_ops.rs`, assert that `kb-read` successfully projects `Variables` and `Structure` for SDTs.

**Step 2: Run test to verify it fails**
- Run: `dotnet test src/GxMcp.Gateway.Tests --filter EngineReadContractTests`

**Step 3: Implement minimal code**
- Extend `AdmittedParts` in `EngineReadContract.cs` to include `"Variables"` and `"Structure"`.
- Extend `source_projection.rs` in `gx-engine` to decode Variables XML and Structure XML from `EntityPart`.

**Step 4: Run test to verify it passes**
- Run: `dotnet test src/GxMcp.Gateway.Tests --filter EngineReadContractTests`
- Run: `cargo test --manifest-path src/gx-engine/Cargo.toml --test kb_ops`

---

### Task 2: Implement `EngineEditContract` & Native `genexus_edit` Routing in Gateway

**Files:**
- Create: `src/GxMcp.Gateway/EngineEditContract.cs`
- Modify: `src/GxMcp.Gateway/EngineRoutedOperations.cs`
- Test: `src/GxMcp.Gateway.Tests/EngineEditContractTests.cs`

**Step 1: Write the failing tests**
- Test `EngineEditContract.TryPlan`: validates single object `name`, single `part` in `{"Source", "Events", "Rules", "Conditions"}`, text `content`, and ensures `dryRun` or batch `targets[]` are rejected or deferred to SDK.
- Test `DispatchEngineEditAsync`: when admitted, converts arguments to `kb-write` CLI args and dispatches to `gx-engine`.

**Step 2: Run test to verify it fails**
- Run: `dotnet test src/GxMcp.Gateway.Tests --filter EngineEditContractTests`

**Step 3: Implement minimal code**
- Implement `EngineEditContract` with `TryPlan`, argument sanitization, temp file creation for `--content-file`, and response shaping.
- Update `DispatchEngineEdit` in `EngineRoutedOperations.cs` to become `async Task<JObject?> DispatchEngineEditAsync(EngineRouteContext ctx)` calling `gx-engine kb-write` when admitted, and falling back to SDK otherwise.

**Step 4: Run test to verify it passes**
- Run: `dotnet test src/GxMcp.Gateway.Tests --filter EngineEditContractTests`

---

### Task 3: Native UDM `PatternInstance` (WorkWithPlus) Part Reader in `gx-engine`

**Files:**
- Modify: `src/gx-engine/src/udm/client/model_reads.rs`
- Modify: `src/gx-engine/src/kb_ops/source_projection.rs`
- Test: `src/gx-engine/tests/udm_pattern_instance.rs`

**Step 1: Write the failing tests**
- Add test verifying that `UdmClient::get_object_part` can fetch the `PatternInstance` part (PartType GUID or ID) and return its XML content directly from SQL Server without COM STA.

**Step 2: Run test to verify it fails**
- Run: `cargo test --manifest-path src/gx-engine/Cargo.toml --test udm_pattern_instance`

**Step 3: Implement minimal code**
- Add support for resolving `PatternInstance` part type in `model_reads.rs`.
- Enable reading raw PatternInstance XML in `source_projection.rs`.

**Step 4: Run test to verify it passes**
- Run: `cargo test --manifest-path src/gx-engine/Cargo.toml --test udm_pattern_instance`

---

### Task 4: Business Component C# Generation in `rebuild-all`

**Files:**
- Modify: `src/gx-engine/src/rebuild.rs`
- Modify: `src/gx-engine/src/csharp/transaction.rs`
- Test: `src/gx-engine/tests/rebuild_business_component.rs`

**Step 1: Write the failing test**
- Test that `rebuild-all` on a catalog containing a Transaction with `is_business_component: true` generates both `[Transaction].cs` and `[Transaction]BC.cs` using `TransactionClass::emit_bc_methods`.

**Step 2: Run test to verify it fails**
- Run: `cargo test --manifest-path src/gx-engine/Cargo.toml --test rebuild_business_component`

**Step 3: Implement minimal code**
- In `rebuild.rs`, when emitting Transaction classes, inspect `is_business_component`. If true, emit the companion `BC.cs` file.

**Step 4: Run test to verify it passes**
- Run: `cargo test --manifest-path src/gx-engine/Cargo.toml --test rebuild_business_component`

---

### Task 5: Live Smoke & Preflight Validation on `C:\KBs\KBTeste`

**Files:**
- Verification: `scripts/test-live-matrix.ps1`
- Build: `build.ps1`

**Step 1: Execute full crate & gateway test suites**
- Run: `cargo test --manifest-path src/gx-engine/Cargo.toml`
- Run: `dotnet test src/GxMcp.Gateway.Tests`

**Step 2: Package and live smoke**
- Run: `pwsh -NoProfile -File .\build.ps1`
- Validate live `genexus_read` and `genexus_edit` against test KB `C:\KBs\KBTeste` with `Backend=gxengine`.
