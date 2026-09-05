# Changelog

## Unreleased

## v2.57.0 - 2026-09-05

### Added

- **Typed Transaction record access (PR #133).** Added `genexus_db` actions
  `records_query`, `records_insert`, and `records_update`. They derive table,
  attributes, key metadata, and scalar conversions from the GeneXus
  Transaction, bound every read, return an optimistic `versionToken`, default
  writes to `dryRun=true`, and require a matching single-use v2 preview receipt
  before persistence. Direct SQL boundaries and the absence of GeneXus business
  rules are reported explicitly. Contributed by David Agostini
  (@davidagostini). [PR #133](https://github.com/lennix1337/Genexus18MCP/pull/133)

### Changed

- **Tool schema budget for action contracts (Issues #139/#140).** Raised the guarded schema-size budget from 24,500 to 25,000 tokens to accommodate the required descriptions for all 31 action-bearing tools; that increment measured ~24,666 tokens with ~334 tokens of headroom before PR #133.
- **Tool schema budget for typed Transaction records (PR #133).** Raised the guarded budget from 25,000 to 25,500 tokens for the `records_query`, `records_insert`, and `records_update` schemas and safety contract; the current measured size is ~25,165 tokens with ~335 tokens of headroom.
- **Typed Transaction integration quality gates (PR #133).** Kept record reads bounded to the requested projection plus one truncation sentinel, preserved the serializable write verification sequence, and made action/help/inventory parity machine-checkable.

### Fixed

- **Release warning baseline and reduction (Issue #138).** Removed the reported xUnit1012 and benchmark CS8618 diagnostics, scoped the Worker.Tests MSB3277 suppression, and documented a reproducible 216-location nullable-warning baseline for future Release builds.
- **Action contract parity and conservative classification (Issue #139).** Added descriptions to all 31 umbrella `action` properties and replaced permissive fallback logic with explicit read-only/mutating sets; omitted and unknown actions are now non-read-only, with every supported `dryRun` preview exception covered by the same contract tests.
- **Tool help and capability inventory parity (Issue #140).** Added help coverage for every action-bearing tool, introduced a schema-checked inventory of all valid actions and mutation semantics, and corrected the README's 50-tool count and dependency-graph wording.
- **OpenCode Desktop setup guidance (Issue #135).** Kept the Desktop target detect-only, exposed structured local-server fields and validation steps, and made `clients add` report a manual setup skip without touching the app-managed configuration file.
- **OpenCode Desktop explicit setup (Issue #135).** `clients add --clients opencode-desktop` now always returns actionable manual setup, including the resolved `GX_CONFIG_PATH`, even when the app is not detected or a stale gateway path is present; writable-client launcher validation remains enforced.
- **Deterministic no-navigation smoke coverage (Issue #137).** The live Procedure navigation test now uses stable name ordering, bounded page/transient-retry caps, progressive backoff, and an explicit `NoNavigationBlocks` status/hint before accepting a hit.
- **Side-effect-aware action classification (Issues #139/#140).** Navigation cache refreshes, transfer exports, and browser preview build/baseline/screenshot options are no longer reported as pure reads; typed record actions now share exact action-token and dry-run contracts across routing, help, inventory, and cache policy.
- **Post-merge cache and smoke hardening (PR #133 follow-up).** Semantic caching now uses the side-effect-aware action classifier for both lookup and storage, including navigation refreshes, XPZ exports, browser preview writes, and omitted `records_*` dry-runs; live navigation smoke now has one bounded deadline, consumes index ETA, detects canonical partial-total markers, and reports pagination limits as inconclusive. Published navigation annotations now match its cache-refresh behavior.
- **Legacy `genexus_analyze` explain envelope (Issue #136).** Restored `mode="explain"` to the published schema and discovery fixture so the existing typed `NotImplemented` response is reachable instead of being rejected by gateway enum validation.
- **Transitive npm security updates (Issue #134).** Refreshed the lockfile to `brace-expansion@1.1.18` and `js-yaml@4.3.2`, removing the two high-severity audit findings without changing the declared dependency surface.
- **Race condition and E409 conflict during npm provenance publish.** Hardened `.github/workflows/release.yml` to gracefully handle npm registry E409 "Cannot publish over previously staged version" during asynchronous Sigstore provenance ingestion, added an active polling loop verifying registry availability before completing, removed duplicate `released` trigger causing simultaneous workflow runs, removed obsolete `always-auth` npmrc configuration, and updated `release.ps1` to detect active in-flight release workflows before triggering redundant fallback runs.

## v2.56.0 - 2026-09-04

### Added

- **Type disambiguator on `genexus_structure` (Issue #131).** Added optional `type` parameter across Gateway router, Worker dispatcher, and `StructureService.GetVisualStructure`, allowing callers to explicitly target `type="Transaction"` or `type="Table"` when homonyms share a name (such as `Empresa` as both a Transaction and a Table). When untyped resolution resolves an unsupported object type, the error message now identifies the resolved type and suggests specifying `type="Transaction"` or `name="Transaction:<target>"`.
- **Missing router-consumed parameters declared in `genexus_lifecycle` schema (Issue #131).** Declared `part`, `page`, `pageSize`, `page_size`, `notifyOnFailure`, `skipFullDeploy`, and `fastIncremental` in `tool_definitions.json` and `tools-list.response.json` for `genexus_lifecycle`, ensuring OpenAPI compliance and full schema transparency for client agents.
- **Recipe crystallization `steps` parameter (Issue #131).** Declared `steps` array in `genexus_recipe` schema for `action="crystallize"`, enabling clients to pass ordered macro steps explicitly.
- **Tool help documentation for multi-action tools (Issue #131).** Added comprehensive markdown guides in `ToolHelpCatalog` accessible via `genexus://kb/tool-help/<tool>` for `genexus_structure`, `genexus_layout`, `genexus_versioning`, `genexus_io`, `genexus_kb_version`, `genexus_doc`, `genexus_recipe`, and `genexus_refactor`.
- **Typed API route persistence and rollback (PR #132).** Added typed route persistence with transactional rollback and logical source equality check for API objects. Contributed by David Agostini (@davidagostini).

### Changed

- **Schema property descriptions across all multi-action tools (Issue #131).** Enriched property descriptions across `genexus_layout` (12 properties), `genexus_db`, `genexus_create`, `genexus_versioning`, `genexus_io`, `genexus_kb_version`, `genexus_edit_form`, `genexus_security`, `genexus_apply_pattern`, `genexus_refactor`, and `genexus_structure`.
- **Tool schema budget bump to 24,500 tokens (Issue #131).** Increased combined schema budget from 23,000 to 24,500 tokens in `ToolSchemaSizeTests` (measured ~24,200 tokens) to accommodate the complete set of property descriptions, undeclared parameters, and typing additions across multi-action tools.
- **Aligned effect metadata and annotations for `genexus_doc` (Issue #131).** Set `readOnlyHint=false` and `idempotentHint=false` on `genexus_doc` because `action="wiki"` writes Markdown files to disk; updated description to reference dependency graph generation instead of sequence diagrams.
- **Removed unsupported `run` action from `genexus_recipe` enum (Issue #131).** Cleaned up `genexus_recipe` action enum and error hints by removing dead `run` action.
- **Documented MCP update and harness synchronization in `AGENTS.md` (Issue #130).** Added preflight checks, decision matrix, operational side-effects/safety boundaries, checkout-scoped process management rules, and portable `<repoRoot>` placeholders to `AGENTS.md`.

### Fixed

- **`dryRun` propagation in `genexus_lifecycle action="index"` (Issue #131).** Fixed `SystemRouter.ConvertToolCall` to forward `dryRun` to the worker for the `index` action so indexing plans can be previewed without executing full rescan.
- **Semantic cache invalidation for `genexus_structure action="remove_attribute"` (Issue #131).** Registered `remove_attribute` in `Program.ToolPayload.cs` under `genexus_structure`, preventing stale cached read responses after attribute removal.
- **Crash retry safety on multi-action tools (Issue #131).** Guarded `ShouldRetryWorkerCrash` in `Program.WorkerLifecycle.cs` with action-level inspection via `IsRetrySafeOperation`, ensuring multi-action tools (`genexus_structure`) only retry on safe read operations (`get_visual`, `get_indexes`, `get_logic`, `check_subtypes`) and never retry mutating writes after a worker crash.
- **Unified read-only classification across internal services (Issue #131).** Extracted `OperationClassifier` to unify read-only detection across `MacroSuggestionService` and `NextLegalActionsBuilder`, eliminating dead tools (`genexus_logs`, `genexus_history`) and correctly classifying multi-action tools (`genexus_doc`, `genexus_recipe`, `genexus_kb`, `genexus_versioning`, `genexus_telemetry`) by action.
- **Ephemeral port race and abort signal cleanup in tests and IDE client (PR #132).** Added retry resilience for port bind races in smoke test harness and improved abort signal listener cleanup in IDE client. Contributed by David Agostini (@davidagostini).

## v2.55.0 - 2026-09-04

### Added

- **Modular object identity resolution across `genexus_read`, `genexus_inspect`, and `genexus_search_source` (PR #129).** Added `guid`, `entityKey`, and `path` parameters across `genexus_read`, `genexus_inspect`, and `genexus_search_source` schemas, allowing LLMs to directly read, inspect, or search objects by their native GUID, SDK EntityKey, or module hierarchy path without requiring ambiguous or duplicated names. `genexus_inspect` now allows omitting `name` when modular identity coordinates are supplied. Contributed by David Agostini (@davidagostini).
- **Uniform identity metadata on object reads.** Expanded all read surfaces (`ReadObject`, `ReadFullObject`, `ReadObjectSourceParts`, `ExtractAllParts`) to proactively return the structured `identity` block (`guid`, `entityKey`, `entityTypeGuid`, `entityId`, `path`), providing downstream tools with deterministic identity tokens for immediate chaining.

### Changed

- **O(1) index lookups for object identity and search scoping.** Replaced full O(N) linear scans across index collections in `BuildObjectIdentity`, `FindIndexEntry`, and `hasIdentityScope` with O(1) lookups via `GuidToKey` and `ByNameIndex`, reducing object resolution time in large KBs from hundreds of milliseconds to microseconds and eliminating per-call allocations. Contributed by David Agostini (@davidagostini) and Antigravity.
- **Pre-normalized path matching in `SourceSearchService`.** Hoisted path normalization outside search candidate iteration loops, preventing repetitive string allocations during source searches.
- **Gateway router identity resolution fallback.** Updated `ObjectRouter` and `AnalyzeRouter` to seamlessly resolve targets from `path`, `entityKey`, or `guid` when `name` is omitted, eliminating missing target errors on modular identity tool calls.

## v2.54.1 - 2026-09-03

### Added

- **IDE concurrency detection and warning in `genexus_edit` and `BulkWrite` (Issue #128).** Added `IdeConcurrencyDetector` to detect when `GeneXus.exe` is running and whether the target KB and/or target object is open in an IDE tab/window using Win32 window and child-window inspection. Surfaces structured warnings `GotchaIdeObjectOpenInEditor` and `GotchaIdeActiveOnKb` with resolvable tool-help documentation URIs (`genexus://kb/tool-help/gotchas/ide-object-open-in-editor` and `genexus://kb/tool-help/gotchas/ide-active-on-kb`). Added optional `concurrencyPolicy` argument (`"warn"` [default] or `"fail_if_open"`) to `genexus_edit` schema and `BulkWrite` facade args to abort write operations with an `IdeObjectOpen` error when the target object is open in the IDE. Reported and architected by Antonio Jose Rodrigues Silva (@antoniojosedev).

### Fixed

- **Synchronous object flushing, entity revision date stamping, and IDE message pump nudging in `WriteService` (Issue #128).** Eliminated race conditions caused by the 2-second debounce timer (`_flushTimer`) and removed non-functional reflection (`model.GetType().GetMethod("Commit")`), replacing with synchronous `FlushSync()`. Stamped entity and model revision dates (`SaveModelEntityDate(301)`, `SaveModelEntityDate(300)`, `SaveVersionIndependentDate(310)`, `KBModel.LastCommitDate`, `KBModel.LastObjectsVersionDate`) upon write/commit so external tooling and IDE detect saved modifications. Dispatched non-intrusive `WM_NULL` to running GeneXus IDE windows after successful writes to trigger `Application.Idle` and prompt the IDE's built-in external modification detection (`Messages.ObjectModified`). Reported and architected by Antonio Jose Rodrigues Silva (@antoniojosedev).



## v2.54.0 - 2026-09-03

### Fixed

- **Primary environment generator selection and Design model exclusion in `genexus_kb(action="list_environments")` (Issue #127).** Fixed generator resolution in `KbService.ResolveModelGenerator` to query `kbModel.GetAs<GxModel>()?.Generator` (with web/default fallbacks) instead of enumerating `GeneratorsPart.Generators` without priority, ensuring the primary/web generator (e.g. `Default (.NET Framework)`) is returned instead of mobile or secondary generators (`Android (Android)`). Fixed `EnumerateEnvironmentModels` and `GetActiveEnvironment` to exclude the conceptual "Design" model and handled `AmbiguousMatchException` in `TryGetMember` when inspecting inheritance-shadowed properties like `KBModel.Type`.

### Internal

- **Centralized `ReflectionHelper` and inheritance-shadowed member resolution.** Extracted `ReflectionHelper` in `GxMcp.Worker.Helpers` to provide safe reflection lookup across GeneXus SDK COM/inheritance hierarchies with declared-only fallback for shadowed properties throwing `AmbiguousMatchException`.

## v2.53.0 - 2026-09-03

### Added

- **Active LLM turn-steering via next_legal_actions, multi-part 360° read aliases, and self-healing error suggestions.** Added active `next_legal_actions` on `genexus_read` (suggesting `genexus_edit`, `genexus_analyze`, and `genexus_navigation` with pre-filled arguments) and `genexus_query` (suggesting 360° `genexus_read` and `genexus_inspect` on the top match), supported 1-roundtrip multi-part aliases (`part="all"|"full"|"summary"|"360"` routing to `ExtractFullObject`), and added actionable self-healing recovery hints for `PartNotFound` and `ObjectNotFound` errors in `McpRouter.AttachSuggestedNextStep`. Eliminates 2 to 3 exploratory roundtrips per task.

### Changed

- **Zero-AST streaming validation in `IsCacheableSuccessEnvelope`.** Replaced full `JObject.Parse(json)` in the worker command dispatcher with a streaming `JsonTextReader` check that verifies top-level `error` and non-cacheable `status` without building in-memory AST syntax trees. Reduces memory allocations by up to 76% and speeds up payload validation by nearly 3X.
- **Zero-copy parameter merge in `CommandDispatcher.DispatchInternal`.** Eliminated redundant recursive `DeepClone` on request parameters during command dispatching, passing property references directly.
- **Tool profile definition caching in `ToolProfileFilter` & `McpRouter`.** Added concurrent cache for profile-filtered tool definitions (`core`, `authoring`, `devops`, `ui`, `db`), eliminating repeated `JArray` allocations on `tools/list` calls (yielding >2,800X throughput improvement with 0 allocations).
- **Extended read cache TTL in `ObjectService`.** Increased default `ReadCacheTtl` from 60s to 300s (5 minutes) and added configurable override via `GXMCP_READ_CACHE_TTL_SEC`. Eliminates redundant COM object reads from disk across multi-turn reasoning sessions while preserving deterministic write invalidation.
- **Candidate loop allocation reduction in `SourceSearchService`.** Hoisted default scope collections out of per-candidate search loops.
- **Zero-allocation pipe writing in `WorkerProcess.ProcessQueueAsync`.** Replaced intermediate `rpc.ToString(Formatting.None)` string allocations with direct streaming via `JsonTextWriter.WriteTo` into the worker pipe writer, speeding up command serialization by 5.4X and eliminating intermediate string allocations in the LOH/Gen2.
- **Asynchronous non-blocking DataStore diagnostic probe in `KbService.OpenKB`.** Offloaded synchronous datastore connection and metadata probing to a background Task so `OpenKB` returns `KbOpened` immediately without gating readiness on remote database latency.
- **Enhanced `GXMCP_EMIT_STRUCTURED_CONTENT` control.** Added explicit `0`/`false` and `1`/`true` toggle for structured content generation to complement `GXMCP_NO_STRUCTURED_CONTENT`, reducing payload sizes by up to 46.8%.
- **Value-type `RankedResult` struct in `SearchService`.** Converted `RankedResult` from a heap-allocated class to a stack/contiguous `readonly struct`, eliminating thousands of individual object allocations during search ranking and improving cache locality.
- **Zero-alloc term matching in `SearchService.CalculateSemanticScore`.** Replaced LINQ `Enumerable.Contains(..., StringComparer.OrdinalIgnoreCase)` calls across `Keywords`, `Tags`, `Tables`, and `Calls` with an allocation-free indexed loop, eliminating up to 60,000 enumerator allocations per search query. Reduces search latency by 45% (0.467ms -> 0.257ms) and Gen0 collections by 75%.
- **Direct indexed pagination in `ListService`.** Replaced LINQ `.Skip().Take()` iterator allocations over ordered index entries with direct indexed iteration.
- **Direct streaming response writing in `Program.TryWriteStdout(JObject)`.** Added streaming JSON response serialization directly to `Console.Out` via `JsonTextWriter.WriteTo`, bypassing intermediate large JSON string allocations in stdio MCP dispatching.
- **Loop unrolling and scalar optimization in `VectorService.ComputeEmbedding`.** Replaced 128-iteration modulo loop with 32-iteration direct dimension updates and scalar multiplication normalization, speeding up embedding generation by 26.8% (4.97µs -> 3.64µs).
- **Pre-compiled regex and allocation-free token extraction in `SourceSearchService`.** Compiled `LiteralTokenRegex` once statically, replaced LINQ `.Distinct().ToList()` with `HashSet<string>`, hoisted separator arrays in `ParseObjectNames`, and replaced enumerator loops in `MatchesAnyLiteral` with indexed iterations, eliminating GC allocations during literal token pre-filtering.
- **Fast-path colon guard and regex-free tokenization in `QueryGrammar.Parse`.** Added early `IndexOf(':')` guard to bypass structured filter regex scans for plain queries, replaced dictionary regex lookups with a struct array, eliminated string allocations in property switching, and bypassed regex tokenization for quote-free queries. Speeds up query parsing by 57.8% (14.44µs -> 6.10µs per pair) and cuts Gen0 GC collections by 50%.
- **Static lookup arrays, string concatenation, and zero-allocation match ordering in `ObjectService`.** Hoisted static arrays for candidate probes, default parts, and colon delimiters, eliminated snapshot allocations in `MarkReadCacheDirty` by iterating `_readCache` directly, optimized `BuildReadCacheKey` using `string.Concat` without redundant lowercasing (27.3% faster key generation), and replaced 4-level LINQ chained sorting in `FindObject` with direct priority comparator and single-match fast paths.
- **Pre-cached static discovery responses in `McpRouter`.** Pre-built and cached responses for `resources/list`, `resources/templates/list`, and `prompts/list`, eliminating repeated anonymous object and string allocations during MCP handshake and capability discovery. Speeds up discovery endpoint handling by 10.2X (1.94µs -> 0.19µs per set) and eliminates 100% of related Gen0 GC collections.
- **ASCII uppercase fast path in `IndexCacheService.ShardOf` and allocation-free method checking in `CommandDispatcher`.** Replaced unicode character database lookups and `foreach` string enumerator allocation in `ShardOf` with direct ASCII branch and indexed loop, speeding up index sharding by 54.6% (0.293µs -> 0.133µs per operation), and eliminated redundant `.ToLowerInvariant()` string allocations on every worker dispatch.
- **Hoisted environment profile lookup and direct indexed pagination in `ListService`.** Hoisted `IsLegacyPerfProfile` resolution outside row projection loops to eliminate hundreds of redundant Win32 `Environment.GetEnvironmentVariable` calls per listing page (28.6% faster page construction, -40.8% GC collections), and replaced LINQ `.Skip().Take().LastOrDefault()` cursor extraction and runtime loops with direct array indexing.
- **Zero-allocation type match in `IndexEntryFilterBuilder.IsTypeMatchAliasAware`.** Replaced per-object `type.ToLower()` and `query.ToLower()` heap allocations with `string.Equals(..., OrdinalIgnoreCase)` and `IndexOf(..., OrdinalIgnoreCase)`, speeding up type match scans across 38,000 KB objects by 53.7% (6.137ms -> 2.839ms per scan) and eliminating 100% of related Gen0 GC collections (54 -> 0).
- **In-place ranking sorting, direct indexed pagination, and string concatenation in `SearchService`.** Replaced LINQ `OrderByDescending().ThenBy().ToList()` and `.Skip().Take().ToList()` with in-place `List<RankedResult>.Sort` and direct indexed paging, eliminating intermediate list allocations (25.5% faster ranking, -81.8% Gen0 GC collections), and replaced 10-parameter `string.Format` cache key construction with `string.Concat`.
- **Single-pass linear token scanner and O(1) variable deduplication in `VariableInjector`.** Replaced two uncompiled Regex passes (`&(\w+)` and `&(\w+)\.`) and LINQ `Cast/Select/Distinct` chains with a single-pass linear scanner, and replaced O(N*M) linear search `variablesPart.Variables.Any(...)` with an O(1) `HashSet<string>` lookup. Speeds up variable extraction during object edits by 10.5X (0.021ms -> 0.002ms per op) and reduces Gen0 GC collections by 84.2%.

## v2.52.0 - 2026-09-02

### Added

- **Native API route inspection and clone/update plans (`genexus_api`, PR #126).** Added typed `routes_inspect`, `routes_clone`, and `routes_update` support for API `ServiceGroupSource` methods, including route bindings, optimistic version tokens, dry-run previews, complete snapshots, post-save rereads, and verified rollback without implicit lifecycle operations. Contributed by David Vinicius Agostini (@davidagostini).
- **Tool definitions schema budget expansion.** Expanded tool definitions approximate token budget from 22,200 to 22,500 tokens in `ToolSchemaSizeTests` to accommodate typed API route inspection, clone, and update parameters.

## v2.51.0 - 2026-09-02

### Added
- **Deep Authoritative `MutationEngine` (Architectural Deepening Candidate 1).** Transformed `MutationEngine` into an authoritative Unit-of-Work module with `IMutationEngine` and `ISdkObjectWriter`. Encapsulates preflight guards against literal line-break sequences (`TextPayloadGuard`), optimistic concurrency checking (`ExpectedVersion`), in-memory dry-run diff generation, multi-object staging, and automatic LIFO rollback compensation across touched objects on intermediate failure.
- **Deep Visual Surface Domain & Adapter Layer (Architectural Deepening Candidate 3).** Retired dead dummy stubs in `IVisualSurfaceAdapter` and implemented complete `WebFormSurfaceAdapter`, `ReportLayoutSurfaceAdapter`, and `VisualSurfaceDomain`. Unifies visual DOM projection, baseline XML preservation of untouched controls and print blocks, and semantic color normalization.
- **Deep `CompilationPipeline` & Scoped Environment Isolation (Architectural Deepening Candidate 4).** Deepened `CompilationPipeline` with structured compiler diagnostic harvesting (MSBuild CS codes and GeneXus SPC/SRC codes) and introduced `EnvironmentScope : IDisposable` ensuring temporary active environment switching during builds automatically restores the original environment on success, failure, or exception.
- **Polymorphic `PartSerializerRegistry` & Unified `QueryGrammar` (Architectural Deepening Candidate 5).** Activated `PartSerializerRegistry` with concrete `SourcePartSerializer`, `WebFormPartSerializer`, and `VariablesPartSerializer` to eliminate sprawling switch ladders. Unified search query parsing into `QueryGrammar.Parse` across `SearchService`.
- **Deep Gateway `McpMiddlewarePipeline` Stages (Architectural Deepening Candidate 2).** Implemented `IMcpMiddleware` across `IdempotencyMiddleware`, `DryRunMiddleware`, and `ResponseCompactionMiddleware`, establishing composability and unit-testability for cross-cutting gateway request transformations.
- **Deep Gateway `WorkerSupervisor` & `IWorkerSupervisor` Contract (Round 2 Deepening Candidate 1).** Extracted a dedicated `WorkerSupervisor` module implementing `IWorkerSupervisor` to decouple worker process orchestration, per-KB concurrency gates, LRU capacity eviction, and backoff crash recovery from gateway routing. Enables deterministic test execution without spawning real OS processes.
- **Deep Worker `IndexStorageEngine` & Vector Recall Integration (Round 2 Deepening Candidate 2).** Extracted `IIndexStorageEngine` and `IndexStorageEngine` to encapsulate 16-shard FNV-1a hashing, GZip stream persistence, dirty generation counters, and manifest sidecar validation from `IndexCacheService`. Wired `VectorService` into `MemoryService` to empower factual memory recall with 128-float cosine similarity ranking.
- **Deep Worker `SchemaMutationEngine` & Relational Domain Wiring (Round 2 Deepening Candidate 4).** Established `ISchemaMutationEngine` and `SchemaMutationEngine` to eliminate duplicate snapshot capture, optimistic concurrency token checking (`ExpectedVersion`), dry-run preview simulation, and rollback compensation across Transactions, Tables, Indexes, and Domains.
- **Deep Worker `VariableService` & Pure `TypeBindingEngine` (Round 3 Deepening Candidate 5).** Extracted `ITypeBindingEngine` and `TypeBindingEngine` encapsulating 6-tier type resolution (primitive DB types with whitespace tolerance, domain references, dotted SDT items, full SDTs, Business Components, and built-in types) and GAM/WWP+ framework variable protection rules. Connected to `VariableService` to provide an authoritative variable domain engine.
- **Deep Worker `ObjectReader` & Virtual DSL Synthesis Engine (Round 3 Deepening Candidate 1).** Extracted `IObjectReader` and `ObjectReader` encapsulating read-through caching with targeted invalidation, virtual part synthesis (Transaction/SDT DSL, DesignSystem Tokens+Styles split, DataSelector parts), uniform line- and byte-budget pagination, and multi-target batch reading. Integrated directly into `ObjectInspectionModule`.
- **Strategy-Based `AnalysisEngine` & Pure `GeneXusSignatureParser` (Round 3 Deepening Candidate 2).** Extracted `GeneXusSignatureParser` as a pure domain parser for parameter rules (`parm(...)`), accessor directions (`in:`, `out:`, `inout:`), and outgoing call statements (`call`, `udp`, `submit`), eliminating regex duplication across `AnalyzeService` and `ObjectService`. Established `IAnalysisEngine` and `AnalysisEngine` with pluggable `IAnalysisModeHandler` strategy registry.
- **Layered `SystemProcessExecutor` & Reorganization Analyzer (Round 3 Deepening Candidate 4).** Created `IProcessExecutor` and `SystemProcessExecutor` providing a clean mockable seam for OS process spawning, streaming, and tree termination. Created `IReorganizationAnalyzer` and `ReorganizationAnalyzer` to classify DDL statements, track affected tables/columns, and flag destructive conversions.
- **Automated End-to-End Live HTTP Smoke Harness.** Created `scripts/test_live_http.ps1` to execute automated live MCP HTTP verification against running local gateways and open Knowledge Bases (`KBTeste`), testing protocol initialization, tools/list, whoami, worker pools, and live object queries with deterministic process cleanup.

## v2.50.0 - 2026-09-02

### Added

- **List Knowledge Base environments (`genexus_kb action=list_environments`, Issue #124).** Added `list_environments` action to `genexus_kb` to enumerate all environments configured in the open Knowledge Base under STA lock. Returns `activeEnvironment` alongside each environment's `name`, `description`, primary `generator` (resolved via `GeneratorsPart`), `isActive` status flag, and resolved `webPath`.
- **Target environment parameter in build lifecycle (`genexus_lifecycle`, Issue #125).** Added optional `environment` parameter to `genexus_lifecycle` for `build`, `specify`, `rebuild`, and `compile_check` actions. Directs the build to target the specified environment without requiring human IDE intervention to switch active environment beforehand.

### Fixed

- **Headless active environment switching (`genexus_kb action=set_environment`, Issue #125).** Fixed `set_environment` failing with `DispatcherException` / `TargetInvocationException` in background workers. The operation now performs direct SDK model activation (`DesignModel.Environment.TargetModel`, `User.SetTargetModel`, and `User.Save`) under `_kbLock`, bypassing the MSBuild task's UI Dispatcher subscriptions (`CommonServices.Output`) with exception-safe fallback.

## v2.49.2 - 2026-08-31

### Fixed

- **Antigravity stdio bootstrap diagnostics (Issue #123).** `init` and `clients add --clients antigravity` now use the packaged gateway executable directly when available, avoiding the repeated npx bootstrap chain; stale package-cache paths are surfaced by `genexus-mcp clients`. The stdio wrapper also tees gateway stderr into `%LOCALAPPDATA%\GenexusMCP\logs\last-stdio-error.txt` on spawn failures and non-zero exits, so clients that hide child stderr leave an actionable timestamp, exit code, and bounded error tail.

### Internal

- **CLI quality gates.** `npm run lint` now rejects warnings, and the unused Codex launcher helper was removed so the baseline is warning-free.
- **Agent workflow references.** Detailed SDK/release guidance now lives in focused documents, and `npm run test:one -- "<pattern>"` provides a supported single-test loop without duplicating npm scripts.

## v2.49.1 - 2026-08-31

### Fixed
- **Report layout rectangle `BackColor`/`ForeColor` persistence and corruption fix (Issue #122).**
  - **Robust Color Token Parsing (`ColorHelper`):** Resolved an issue where custom/unnamed rectangle colors serializing via .NET `Color.ToString()` (e.g. `Color [A=255, R=144, G=238, B=144]`) failed regex parsing in `ReportLayoutHelper.ReadLayout`, causing raw string descriptors to leak into the editable XML and convert to solid black (`#000000` / integer 0) on subsequent writes or dynamic property assignments. Added exhaustive color token parsing supporting .NET format, GeneXus RGB tokens (`R; G; B|`), comma RGB (`R, G, B`), CSS `rgb(...)`/`rgba(...)`, hex (`#RGB`, `#RGBA`, `#RRGGBB`, `#AARRGGBB`), and named colors.
  - **Untouched Control Preservation (`baselineXml`):** Fixed `WebFormXmlHelper.ApplyEditableXml` and `WriteService.VisualWrite` to propagate `baselineXml` to `ReportLayoutHelper.WriteLayout`. Untouched controls across separate print blocks are no longer treated as modified, preventing untouched rectangles in other print blocks from turning black when editing a different print block or text control.
  - **Color Attribute Equivalence in Write Verification:** Updated `XmlEquivalence.ElementsEqual` and `LayoutService.IsPersistedValueMatch` to perform semantic color equivalence on color attributes (`BackColor`, `ForeColor`, `BorderColor`), preventing false-positive write verification mismatches and accidental rollbacks when requested colors normalize to GeneXus canonical tokens.
  - See Issue [#122](https://github.com/lennix1337/Genexus18MCP/issues/122).


## v2.49.0 - 2026-08-31

### Added
- **Default MCP server name changed to `genexus18mcp`.** To eliminate naming collisions with GeneXus's official MCP server (which registers as `genexus`), the default server identifier across all AI clients (Cursor, VS Code, OpenCode, Codex CLI, Claude Desktop, Antigravity) is now `genexus18mcp`.
- **`--server-name <name>` support.** Added `--server-name` flag to `genexus-mcp init`, `clients add`, `clients list`, and `clients remove` to configure any arbitrary server identifier (e.g. `Gx18byLennix`) and enable seamless multi-MCP coexistence in all supported formats (`mcpServers`, `servers`, `mcp.servers`, and TOML `[mcp_servers]`).
- **`--force` override flag.** Added `--force` flag to explicitly overwrite existing foreign or custom MCP server configurations when replacing an entry is desired.

### Fixed
- **Third-party / HTTP MCP collision protection (Issue #121).** Client auto-registration now distinguishes our own local stdio / npx launcher entries from external/official HTTP/SSE/remote MCP server definitions. Attempting to overwrite an existing third-party entry without `--force` is rejected with `MCP_SERVER_COLLISION` and diagnostic guidance instead of silently clobbering the foreign server.
  Thanks to [@antoniojosedev](https://github.com/antoniojosedev) for reporting and investigating the collision with the official GeneXus MCP server — see Issue [#121](https://github.com/lennix1337/Genexus18MCP/issues/121).

## v2.48.0 - 2026-08-28

### Added
- **Unified `NavigationReport` In-Memory Domain Engine (Architectural Candidate 2).** Introduced a strongly-typed `NavigationReport` domain model (`NavigationLevel`, `NavigationFilter`) encapsulating `.nvg.xml` navigation discovery, disk snapshotting, and direct in-memory SQL statement projection. Eliminates legacy intermediate JSON string serialization and re-parsing between `NavigationService`, `NavigationSqlService`, and `NavigationViewService`.
- **Deep `VariableService` & Type Resolution Engine (Architectural Candidate 4).** Consolidated variable CRUD operations, two-tier type resolution (`VariableTypeResolver`), framework-managed protections, and source auto-declaration heuristics into a cohesive `VariableService` interface (`IVariableService`).
- **Synchronized Session & KB Context Registry (Architectural Candidate 5).** Enhanced `HttpSessionRegistry` and `SessionKbContextStore` with case-insensitive session matching, timeout immunity for stdio harnesses, and unified session state tracking.
- **Declarative Gateway Tool Dispatch Seam (Architectural Candidate 1).** Streamlined declarative dispatch in `McpRouter.ConvertToolCall` targeting canonical tool definitions directly.
- **Pattern Engine Adapter & WorkWithPlus Isolation (Architectural Candidate 3).** Decoupled Pattern and WorkWithPlus application workflows behind `IPatternEngineAdapter` and `PatternService` with mockable test harnesses.

## v2.47.0 - 2026-08-27

### Added
- **`McpMiddlewarePipeline` for Gateway Request Processing (Pass 2 Architectural Candidate 1).** Decomposed the monolithic gateway request loop into an extensible, unit-testable pipeline (`IMcpMiddleware`) isolating schema validation, auto-typing, idempotency, semantic caching, and response compaction.
- **Unified `QueryGrammar` & Tokenization (Pass 2 Architectural Candidate 2).** Extracted an authoritative query parser and canonical type alias resolver (`NormalizeType`, `IsTypeMatch`, prefix extraction for `type:`, `parent:`, `usedby:`, `metadata:`) shared across `SearchService`, `ListService`, and `SourceSearchService`.
- **Polymorphic `IVisualSurfaceAdapter` for UI & Layout (Pass 2 Architectural Candidate 3).** Established a unified visual surface abstraction bridging WebForms, Procedure Reports, and Design System Objects (`WebFormSurfaceAdapter`, `ReportLayoutSurfaceAdapter`).
- **`MultiObjectUnitOfWork` for Atomic Refactoring (Pass 2 Architectural Candidate 4).** Added multi-object transaction management in `RefactorService` with in-memory staging, dry-run diffing, and automated rollback across all modified caller objects upon intermediate failure.
- **`DiagnosticAndHealingEngine` (Pass 2 Architectural Candidate 5).** Unified watchdog health probing, crash ledger forensics, mutation fencing analysis, and automated remediation routines for Gateway and Worker self-healing.
- **`CommandHandlerRegistry` for Worker modular command resolution (Architectural Candidate 3).** Replaced monolithic eager command dispatch with a lazy registry pattern supporting direct canonical tool name resolution (`genexus_*`) and typed `CommandContext` execution.
- **Authoritative `MutationEngine` (Architectural Candidate 2).** Consolidated object modifications into a unified pipeline managing preflight checks, optimistic concurrency guards, dry-run simulation, SDK COM transactions, snapshot persistence, and automated rollback compensation.
- **Declarative Gateway Tool Dispatch Seam (Architectural Candidate 1).** Streamlined `McpRouter.ConvertToolCall` to forward canonical tools defined in `tool_definitions.json` directly to the Worker's command registry, preventing schema drift and parameter-dropping bugs while bypassing shallow router boilerplate.
- **Unified `CompilationPipeline` (Architectural Candidate 4).** Encapsulated compilation, in-process specification, MSBuild execution, process tree management, and structured compiler diagnostic parsing behind a cohesive lifecycle module.
- **Consolidated `ObjectInspectionModule` (Architectural Candidate 5).** Unified object reading, multi-part extraction, summaries, and 360-degree context extraction into a tiered inspection engine (`Summary`, `Source`, `Parts`, `Full`, `Context360`).

## v2.46.2 - 2026-08-26

### Fixed
- **Text writes now reject literal line-break escape sequences before persistence.** A shared preflight guard covers full edits, patches, atomic authoring, batch writes, and the legacy scaffold path, returning `LiteralLineBreaksDetected` with the affected field and sequence instead of allowing a whole GeneXus part to become one `//` comment. The discovery schema budget was raised from 21,900 to 22,200 tokens to document the contract.

## v2.46.1 - 2026-08-26

### Fixed
- **`save_as` no longer leaves a partial WebPanel when a part cannot be cloned (issue #118).** Explicitly empty or unsupported parts remain non-fatal skips, but a real part-write failure now stops the clone, removes the incomplete target automatically, and reports whether cleanup succeeded. (PR [#120](https://github.com/lennix1337/Genexus18MCP/pull/120)).
- **`save_as` on Design System objects (DSO) now clones both Tokens and Styles parts (issue #119).** `PartAccessor.GetDisplayPartName` now maps `DesignSystemTokensPartGuid` to `"Tokens"` and `DesignSystemStylesPartGuid` to `"Styles"` so both source parts are discovered and copied instead of colliding on `"Source"`.
- **`save_as` on DataSelector objects preserves structure, conditions, and parameters (issue #116).** DataSelector structure parts cannot be round-tripped as plain source text; `ObjectService.CloneDataSelectorStructurePart` now copies parameters, conditions, orders, and defined-by attributes natively via the SDK object model with XML deserialization fallback.
- **`genexus_properties` persists and verifies Domain assignments on Attributes and Variables (issue #117).** Setting `Domain`, `DomainBasedOn`, or `BasedOn` now validates that the target Domain exists, applies the assignment via `DomainPropertyApplier.ApplyDomainBasedOn`, and confirms persistence by inspecting the object's `DomainBasedOn` link during post-save verification.
- **In-process build resolves targets when a Transaction and Table share the same name (issue #115).** `InProcessBuildRunner.ResolveTargetKBObject` disambiguates homonyms by prioritizing primary logic objects (Transactions, Procedures, WebPanels, etc.) over auto-generated Tables and adds support for `Type:Name` qualifiers (e.g. `Transaction:Customer`) and GUIDs.

## v2.46.0 - 2026-08-24

### Fixed
- **Report Layout writes accepted again via `genexus_edit` / visual write paths.** The input validator rejected the `<Report>` root that `genexus_layout` itself emits for Procedure Report layouts, making every round-trip edit fail with "Visual writes currently require a valid GxMultiForm, BODY, HTML, Layout, or ReportPart XML document". `Report` is now an accepted root.

### Added
- **Creating new controls in Report print blocks.** `ReportLayoutHelper.WriteLayout` only updated attributes of controls that already existed in a band, so adding controls to an empty print block (e.g. border `ReportLine`s) silently did nothing. Controls present in the incoming XML but absent from the band are now created — cloned from an existing control of the same type in the layout when possible — with all incoming geometry/style attributes applied and verified on save.
- **Type-specific report control properties round-trip.** Report layouts are no longer projected lossily: `ReportLine` (Direction, LineWidth, BorderStyle), `ReportRectangle` (per-side BorderStyle*, CornerRadius*), `ReportImage` (ImageReference), and `ReportAttribute` (AttributeReference, Row/Col expressions, FieldSpecifier) properties now appear when reading a Report layout and are applied back on write.
- **`genexus_layout action=delete_printblock`.** Removes a print block from a Procedure's report layout inside a transactional write that also removes the matching `print <block>` command from the Procedure Source and verifies on a cold read-back that the block is gone.
- **Report page-setup read/write.** PaperSize, PaperOrientation, PaperWidth/PaperHeight, RightMargin, and UsePrinterSettings are now projected on the `<Report>` root element and applied back to the layout on write (only attributes actually changed vs. baseline).

## v2.45.2 - 2026-08-24

### Changed
- **Faster object resolution on large KBs.** `genexus_search` and target-name lookups no longer scan the whole in-memory index when the typed probes miss — candidates are resolved through the name→objects multimap that already exists, turning a ~38k-entry linear scan into a direct lookup. Falls back to the old scan transparently when the derived index isn't built yet.
- **Less per-row overhead in `genexus_list_objects`.** The legacy-compatibility profile flag is now read once per page instead of once per result row, removing hundreds of environment lookups from every large listing.
- **Cheaper object deletion on the index.** Removing an object from the search index resolves its storage key through the name multimap instead of filtering every indexed entry while holding the index lock, so watcher-driven deletions no longer block concurrent searches for a full-index pass.
- **Less duplicated enrichment work.** The background enrichment queue now skips entries that are already queued instead of re-running their full SDK reference-graph work per duplicate, and duplicate detection on heavily-referenced hub objects (popular tables/SDTs) uses a hash set instead of a linear scan of thousands of accumulated edges.
- **Leaner per-request response path in the gateway.** Gateway-built tool responses no longer deep-clone the whole payload tree just to attach KB metadata; lifecycle status compaction works directly on the in-memory JSON instead of serializing and re-parsing it (up to three full passes saved per poll); and the per-call feature-flag environment probes (`GXMCP_TERSE`, `GXMCP_NO_STRUCTURED_CONTENT`, `GXMCP_LEGACY_TOOL_ALIASES`) are cached behind a short TTL instead of hitting Win32 environment lookups multiple times per request.

### Fixed
- **npm package missing `scripts/verify-install.js` on publish (issue #114).** The `postinstall` script introduced in v2.44.1 to verify worker/gateway binary presence failed on fresh `npm install -g` / `npx` invocations because `scripts/verify-install.js` was not declared in `package.json`'s `files` array and was omitted from the published npm tarballs. Added `scripts/verify-install.js` to `package.json` `files` and added a contract test ensuring all declared bin and postinstall script files are packaged.

### Added
- **`genexus_connection_recover` — self-healing recovery tool.** When tool calls hang, return `WorkerBusy` repeatedly, or the connection seems dead, the agent can now call this instead of asking the user to restart the AI client. It probes every open worker with a liveness check, kills and respawns only the unhealthy ones (force=true targets all), confirms each replacement is SDK-ready before returning, and clears the semantic cache so stale reads can't survive the recovery. Workers that silently died since their last call are re-opened even though they no longer appear in the open list.

## v2.45.1 - 2026-08-22

### Fixed
- **`genexus-mcp doctor --mcp-smoke` no longer fails on a healthy server.** The smoke check omitted the `Accept: application/json, text/event-stream` header that the gateway requires on every POST, so it always got a 406 back and reported a false failure — masking real connection problems. Users hitting "connection closed" can now trust the smoke check to tell a healthy gateway from a dead one.
- **The smoke check no longer fails when no KB is open.** Tool calls in the smoke run now accept a well-formed structured error (e.g. `KB_AMBIGUOUS` with no default KB) as proof the channel works — only transport-level failures (no reply, non-JSON, HTTP errors) fail the check. A new contract test (`McpSmokeScriptContractTests`) launches a real gateway in-process and runs the unmodified smoke script against it, so any future drift between first-party diagnostic scripts and the gateway's protocol requirements fails CI instead of shipping.

## v2.45.0 - 2026-08-22

### Added
- **Lean-response mode: drop `structuredContent` from tool results.** The MCP-standard `structuredContent` field duplicates the entire JSON payload that is already carried in `content[0].text`, adding 42-46% to every tool response's byte size (measured across `genexus_kb`, `genexus_list_objects`, and `genexus_search` against a real KB). Set `Server.EmitStructuredContent: false` in `config.json`, or the `GXMCP_NO_STRUCTURED_CONTENT=1` environment variable, to omit it — LLM clients read the text content, so nothing is lost for agent-driven sessions. Default remains `true` (protocol-compliant).
- **Terse mode: strip per-response UX sugar.** `Server.TerseResponses: true` (or env `GXMCP_TERSE=1`) omits `next_legal_actions` and the `_meta.tokens` used/limit block from tool responses, keeping only the payload itself plus error hints. Stacks with lean mode for maximum per-turn savings. Also slims `genexus_whoami` (drops worker death history, tool-latency roll-up, metrics summary, install forensics, the reference pointer, `suggestedNext`, index `recentlyChanged`/`flushHealth`, the full update-registry dump when up to date, and redundant KB alias lists — 11KB → ~1.2KB measured), `InvalidArgs` error envelopes (drops the repeated `nextSteps` scaffolding; violations stay), the worker's inner `_meta` UX blocks (`suggested_next`, `alternative_views`, `aggregates`, `enrichmentHint`, `autoInjected*` — list_objects 3.8KB → 1.7KB measured), inspect's `sourceReadHint`, `summary`, name-resolution echoes (`resolvedAs`/`alsoMatches`), `correlationId`, `wwpMetadata.masterPage`, and `lifecycle.lastModifiedBy` (inspect 2.9KB → ~0.9KB measured).
- **Automatic worker pre-spawn on startup.** The gateway now pre-spawns the default KB's worker during MCP initialize, so the measured 3.8-12s cold-start is paid at boot instead of on the agent's first KB-bound call. No configuration needed — it uses `Environment.DefaultKb`/`KBs[]`; a failed pre-spawn logs and falls back to the normal open path.
- **First-touch warmup for STA-heavy tools.** After opening a KB, the background warmup now exercises `inspect`, `analyze` (linter + callers), and read paths — the first real call of each previously paid a one-time 0.6-3.8s JIT/SDK cost while every following call ran in single-digit milliseconds. That cost now lands in the warmup window instead of the agent's turn.
- **Lean + terse responses by default on new installs.** `genexus-mcp init` now writes `EmitStructuredContent: false` and `TerseResponses: true` into the generated `config.json` — every tool response drops the duplicated `structuredContent`, `next_legal_actions`, and `_meta.tokens` blocks, cutting per-turn bytes by roughly half for agent-driven sessions. Existing configs are untouched; flip either flag back to opt in to the full MCP surface.
- **Compact projection for `genexus_search`.** Search result pages (50 items/call) now project each hit to `name`/`type`/`description`/`path`/`lastUpdate`, dropping per-item type metadata (`guid`, `length`, `decimals`) a scanning agent rarely needs. Explicit `fields[]` and `projection=verbose` still win.
- **First-touch warmup now targets real code objects.** The background warmup picks its probe object from Transactions/Procedures instead of the alphabetically-first index entry, so it exercises the structure/source SDK paths that inspect/analyze actually hit.

### Changed
- **Faster post-edit validation and build-status polls.** The `genexus_apply_pattern` validation poll now probes at 250ms for the first 2s before falling back to 1s — single-object validation builds typically land in 1-3s, cutting the common-case wait from ~1-4s to under 1s. The async build-status poller's first probe drops from 2s to 500ms so sync-fast builds surface immediately.
- **Faster visual-edit persistence retry.** The Procedure layout write-back verifier retried at a fixed 350ms per attempt (up to 2.1s of waiting); it now backs off adaptively (100/200/350/350/500/500ms), so edits whose persistence lands quickly confirm in under 600ms while slow cases still get the full retry window.
- **Granular semantic-cache invalidation.** Mutations that target a single object (edit, delete, properties set/move, single-target writes) now drop only cached reads referencing that object; unrelated warm reads survive (~10x faster follow-up reads after writes). KB-wide mutations (rename across KB, import) still clear the whole cache.

## v2.44.1 - 2026-08-21

### Fixed

- **A background build can no longer be killed by the worker's idle watchdog (issue #113).** While a build runs, the worker emits a `build_active` heartbeat every 20 seconds; the Gateway now treats a recent heartbeat as activity and refuses to idle-reap or heap-recycle the worker mid-build, no matter how short `Server.WorkerIdleTimeoutMinutes` is configured.
- **A build whose worker dies mid-run now fails immediately instead of hanging for 30 minutes (issue #113).** The Gateway's background status poller used to loop until its hard cap when the worker process exited, leaving `wait_until_done` callers waiting until the MCP transport itself timed out. Three consecutive failed status polls now complete the job as failed with a "worker exited mid-build" message and a re-run hint.
- **A missing worker binary is now diagnosed at the source (issue #112).** When `GeneXus.WorkerExecutable` in `config.json` doesn't exist, the Gateway logs it instead of silently falling back; the "Worker NOT FOUND" error lists every location checked plus the reinstall command, `genexus-mcp doctor` gained a `worker_binary` check, and the npm package verifies `publish/GxMcp.Gateway.exe` and `publish/worker/GxMcp.Worker.exe` right after install so an incomplete npx extraction fails loudly at install time.

## v2.44.0 - 2026-08-21

### Added

- **Dynamic Tool Gating and Profiles (`GXMCP_PROFILE` / `Server.ToolProfile`).** The MCP Gateway now supports scoped tool surface profiles (`all`, `core`, `authoring`, `devops`, `ui`, `db`), reducing client token overhead by up to 75% on discovery while ensuring full toolset availability on demand.
- **Auto-Fix & Self-Correction Engine (`ErrorDiagnoser`).** Diagnoses GeneXus specifier and compiler error codes (`spc0005`, `spc0011`, `spc0053`, `spc0107`, `spc0130`, etc.) and automatically populates structured `suggestedFixes` arrays on build results and linter reports so AI agents can self-correct with a single click/action.
- **OpenAPI 3.0 Import & Export for GeneXus API Objects (`genexus_api action=export_openapi|import_openapi`).** Serializes GeneXus HTTP procedures and API Objects into canonical OpenAPI 3.0.3 specifications and imports external OpenAPI 3.0 / Swagger definitions directly into GeneXus SDT blueprints and endpoint contracts.
- **Design System Object (DSO) Token Parsing, Class Extraction and Validation (`genexus_layout action=design_system` & `DesignSystemService`).** Extends DSO capabilities with automated extraction of token groups (`#colors`, `#font-sizes`, `#spacing`), CSS class rules (`.ClassName`), and syntax validation for Design System objects.
- **Automated GXtest Unit Test Generation (`GxTestGeneratorService`).** Automatically generates complete, structured GXtest `ProcedureUnitTest` source code, parameters, variables, happy-path assertions (`Assert.IsTrue`, `Assert.AreEqual`), and boundary/edge-case suites for GeneXus procedures.
- **MCP Resource Subscriptions (`resources/subscribe` & `resources/unsubscribe`).** Fully integrates MCP resource subscription protocol and capabilities, enabling clients to receive real-time push events (`notifications/resources/updated`) when KB resources, objects, or health indicators update.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Index cache no longer risks losing the most recent index mutation across a concurrent flush.** MarkDirty/MarkDirtyForKey now mark the affected shard(s) dirty before bumping the flush generation, closing a race where an in-flight flush could record a generation as confirmed on disk without serializing that mutation - leaving it permanently absent from the warm-start snapshot.
- **Self-update no longer leaves the install without a binary if the swap fails mid-apply.** The previous binaries are now only cleaned up after a fully successful update, and a failed file replacement restores the backup instead of leaving the gateway or worker executable missing. The per-install update lock is also derived from a process-stable hash of the install path, so two gateway instances sharing one install can no longer apply the same update simultaneously.
- **A cached read can no longer resurrect a pre-mutation response.** A read already in flight when a mutating tool ran could finish after the cache invalidation and store its stale envelope back, so an identical later read replayed pre-mutation data. In-flight reads are now epoch-stamped and skipped when any invalidation happened meanwhile.
- **Two MCP clients with the same KB open no longer kill each other's worker.** The duplicate-worker reaper matched workers only by KB path and treated another live gateway's worker as an orphan, causing mutual kill/respawn thrashing; it now spares workers whose owning gateway is still running.
- **`genexus_worker_reload` can no longer wedge a KB permanently.** If a reload stalled between marking the pool entry as draining and signalling completion, every subsequent call for that KB waited forever; the wait is now bounded to 60 seconds and fails with a retryable error.
- **Client cancellations (`notifications/cancelled`) now actually cancel pending tool calls.** A cancelled tool call resolves immediately with JSON-RPC error `-32800` instead of occupying the worker until the timeout elapses.
- **Searches and listings no longer risk native crashes while the index is cold.** The few code paths behind thread-safe dispatch that reached into GeneXus SDK COM objects off the dedicated STA thread (exact-name direct lookup, SDK cache warm-up, runtime enumeration fallback) are now guarded and fall back to the in-memory index.
- **Retries with `clientRequestId` no longer replay errors.** The idempotency cache also stored error/busy envelopes, so a quick retry reproduced a transient failure (e.g. "KB not open" during warm-up) instead of re-executing the call.
- **A corrupted or hand-edited `config.json` no longer prevents startup.** Invalid scalar values fall back to defaults with a warning, configuration reloads are debounced and serialized, and the update-check cache file is now written atomically.
- **Cross-reference edges (`CalledBy`) no longer miss targets with different casing or inside Folders/Modules**, and the index meta sidecar is written atomically (a crash mid-write previously forced a full reindex on the next start).
- **`Server.MaxOpenKbs` is now respected exactly** (an off-by-one allowed one extra open KB before eviction kicked in), and worker-pool alias lookups normalize casing consistently.

### Changed

- **Semantic cache is bounded (256 entries, 30-minute TTL).** Long-lived gateways no longer grow memory without limit on read-heavy sessions; tune with `GXMCP_SEMANTIC_CACHE_MAX`.
- **Lower per-call overhead.** Tool commands parse once instead of 3-4 times on the worker; large responses serialize fewer times in gateway post-processing; oversized list truncation no longer reserializes the whole array per removed item; SSE streams deliver messages immediately instead of on a 5-second poll; and the HTTP endpoint rejects request bodies over 2 MB.

### Internal

- New regression coverage for the fixes above (semantic-cache epoch/bounds, send-failure envelopes, self-update rollback, dirty-generation ordering, idempotency envelope filtering, meta-token injection, worker-pool capacity). Suites now total ~1,130 Gateway and ~2,020 Worker tests.

## v2.43.0 - 2026-08-20

### Added

- **Multi-part editing on a single object in `genexus_edit`.** `genexus_edit` now accepts a `parts` array (`[{ part: "Rules", content: "..." }, { part: "Source", content: "..." }]`) alongside `name`, updating all specified parts and saving the object exactly once in a single atomic transaction.
- **Batch property setting in `genexus_properties action=set`.** `genexus_properties` now accepts a `properties: { "Prop1": "Val1", "Prop2": "Val2" }` map to set multiple properties on an object in memory and commit them with a single `EnsureSave()` and SQL Server transaction.

### Performance

- **Eliminated redundant multiple saves and KB history bloat across all authoring and batch tools.** Previously, creating an object with variables, rules, source, and properties, or batch editing multiple parts, would sequentially invoke `EnsureSave()` 5 to 10 times, generating dozens of intermediate `EntityVersion` revisions and slowing down execution.
  - `genexus_create action=object_atomic` (`AtomicCreateService`): Refactored to instantiate the object in memory, apply variables, rules, source, and properties directly in memory, and commit the entire object with **exactly 1 `EnsureSave()`**.
  - `genexus_authoring` (`AtomicAuthoringService`): Coalesces creation, variables, rules, source, and properties in memory before performing a single save.
  - `genexus_edit` / `MultiEdit` (`BatchService`): Groups edits by target object and executes coalesced in-memory part mutations, reducing N saves to 1 single `EnsureSave()` per object.
  - `WriteService`: Logical source parts (`ISource` parts: Source, Rules, Events, Variables) now bypass the redundant intermediate `part.Save()` during transaction save and commit directly via `obj.EnsureSave()`, eliminating duplicate SQL Server `EntityVersion` rows.

## v2.42.0 - 2026-08-20

### Added

- **Single-roundtrip Smart Read and 360° Task Context (`genexus_read` and `genexus_analyze mode=context`).** AI coding agents can now obtain full object context in a single tool call instead of incurring multiple roundtrips across parts, variables, rules, and dependencies:
  - `genexus_read`: Omitting the `part` argument (the new recommended default) returns the complete, tailored object payload for any GeneXus object type in 1 call: rules (with `parm`), source/events, compact variables array, structure DSL, signature, and called procedures' signatures. Single-part reads remain fully supported when `part` is explicitly provided.
  - `genexus_analyze mode=context`: Bundles complete 360° task context in 1 roundtrip: the full target object + inlined parameter signatures of all called procedures + physical schemas and primary keys of referenced tables + structures of referenced SDTs + top callers.
  - Smart Variable Pruning: Prunes 100% of unused GeneXus SDK built-in system noise variables (`&Time`, `&Pgmname`, `&Pgmdesc`, `&Page`, `&Line`, etc.) from variable lists, saving context budget while preserving all user-declared and referenced variables.
  - `inline_read_top` Smart Reads: `genexus_query`, `genexus_list_objects`, and `genexus_search_source` now populate `inline_reads` with full `FullObjectRead` payloads on Turn 1.
  - AXI Guidance: Embedded proactive playbooks and schema annotations in `tool_definitions.json`, `genexus_whoami(verbose=true)`, and Next Legal Actions to guide LLMs toward the 1-roundtrip path, maximizing speed and context economy.
- **Native typed .NET generator references with `genexus_generator_reference`.** Lists, previews, adds, and removes the managed assemblies that GeneXus emits through `GxExternalReference`. Mutations require an optimistic token, save and reread only the native `GeneratorsPart`, avoid duplicates, and restore the complete generator-property snapshot on divergence. The tool never invokes Specify, Generate, Build, Rebuild, compilation, reorganization, publish, execution, or tests.
- Thanks to [@davidagostini](https://github.com/davidagostini) for native typed .NET generator references — see [PR #111](https://github.com/lennix1337/Genexus18MCP/pull/111).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_edit mode=full` now keeps dry-runs non-persistent and recovers deterministically from blocked SDK calls.** Preview requests never enter the asynchronous mutation path or call `Save`; background edits use one operation ID across accepted/status/result/cancel and Worker busy telemetry; cancellation terminalizes the operation and recycles only its blocked Worker. Timed-out or cancelled writes require a successful read-back before another write to the same object. Full writes preserve `baseVersion`/`expectedVersion`, honor `rollbackOnFailure`, and return the independently re-read Source, version token, persistence state, and empty implicit lifecycle list.
- Thanks to [@davidagostini](https://github.com/davidagostini) for stabilizing full edit dry-runs, optimistic concurrency, and async mutation recovery — see [PR #110](https://github.com/lennix1337/Genexus18MCP/pull/110).
- **Robust Native SDK Object Resolution across all modules.** Fixed edge cases where newly created or unindexed objects (and Transactions sharing names with physical Table shadow objects) would fail resolution during single-roundtrip reads; resolution now seamlessly uses native SDK `GetByName` and promotes physical tables to source-bearing Transactions.
- **SDT attribute-based members (`Attribute:<Name>` / `basedOnAttribute`) now persist and round-trip through `genexus_structure`, `genexus_read`, `genexus_inspect`, and `genexus_edit`.** Previously, SDT fields referencing a KB Attribute were ignored or stripped when setting structure, reading visual structure, or serializing to the Structure DSL, causing `basedOnAttribute` to be lost and `dryRun` to report false negatives. The SDT authoring pipeline now binds `AttributeBasedOn` via the native SDK reference provider, parses and emits `Attribute:<Name>` in DSL round-trips, surfaces `basedOnAttribute` in `genexus_structure action=get_visual`, `genexus_inspect`, and `genexus_read part=Structure`, and accurately detects visual structure mutations on attribute changes. Fixes #109.

### Internal

- **Typed generator-reference schema budget.** Raised the intentional combined tool-schema budget from 21,100 to 21,700 tokens for native .NET generator reference list/add/remove, dry-run, optimistic concurrency, and exact rollback (measured ~21,563).

## v2.41.11 - 2026-08-19

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **User Control degradation detection no longer fires on healthy builds.** The post-build
  evidence gate required one `setProp("Gx Control Type", ...)` binding per `gx.uc.getNew(...)`
  instance, but GeneXus emits that property only sporadically: in a reference KB with 32 User
  Control objects, exactly one carried it — and IDE-generated output was no different from
  MCP-generated output. Every other build was reported as `SucceededWithGaps` with a
  `[user-control-degraded]` warning, which buried the real failure in noise. Detection now keys
  on the signature actually observed in degraded output: the control-name argument of
  `gx.uc.getNew(...)` emitted as the literal string `"this"` instead of the control's name,
  and/or a generated `.js` that lost every `setProp(...)` binding. Measured against a real KB,
  the new gate flags 5 of 5 genuinely degraded files and 0 of 28 healthy IDE-generated ones
  (the previous gate flagged 27 of those 28). Fixes #103 item 4.
- Thanks to [@danielkrueger](https://github.com/danielkrueger) for identifying the real User Control degradation signature and updating the evidence gate — see [PR #108](https://github.com/lennix1337/Genexus18MCP/pull/108).

## v2.41.10 - 2026-08-19

### Added

- **Native atomic Data View authoring with `genexus_data_view`.** The new `inspect`, `dry_run`, `create`, `update`, and `delete` actions validate existing table metadata, global attributes, types and complete primary keys before saving a root-only Business Component Transaction and its Data View mapping inside one GeneXus SDK transaction. Responses include optimistic version tokens, persisted reread evidence, physical schema/table mapping, and a no-DDL reorganization preview. The tool never invokes Specify, Generate, Build, Rebuild, Reorg, compilation, publishing, execution, or tests implicitly.
- Thanks to [@davidagostini](https://github.com/davidagostini) for the native atomic Data View authoring — see [PR #104](https://github.com/lennix1337/Genexus18MCP/pull/104).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Data View mutations now fail closed at the safety boundary.** `genexus_data_view action=delete` requires `confirm=true` outside dry-run, create/update report commit and verification independently, updates validate the Transaction/Data View pair, and successful deletes remove both native identities from the search index and semantic cache.
- **Object deletion no longer treats SDK resolution failures as absence.** Native identity verification now distinguishes found, absent, and failed states, limits incoming-reference enumeration, reports verification uncertainty honestly after a committed delete, and removes only the deleted GUID from the index.
- **Business Component variable writes now honor collection intent and protect concurrent edits.** `collection` is applied explicitly for add/modify, and a version conflict detected before mutation skips compensating rollback so a concurrent change cannot be overwritten.
- **XPZ fidelity checks now fail closed when the raw WebForm payload is unavailable.** Namespace-aware, bounded archive parsing is required before import; SDK-projected XML is never used as its own fidelity baseline, and explicit import options fail before mutation when the installed SDK cannot apply them.
- **Transaction object dry-runs no longer create the global seed Attribute.** `genexus_create action=object type=Transaction dryRun=true` now returns before constructing the SDK Transaction, reporting `persisted=false` and `mutationDetected=false` without creating the `<Transaction>Id` Attribute or any table/index metadata.
- **Native modular Business Component variables in `genexus_variable`.** `add` and `modify` now accept `objectType=BusinessComponent`, `objectName`, and `module`, resolve the Transaction through the GeneXus type provider, persist its native custom type, and verify the same BC GUID/module after reread. The atomic path supports `expectedVersion`, mutation-free `dryRun`, typed diffs, and complete rollback without running lifecycle actions.
- **Atomic Transaction visual-structure updates.** `genexus_structure action=update_visual` now snapshots the Structure, authored parts, and referenced global Attributes; checks `expectedVersion`; performs a pure `dryRun`; and restores and verifies the complete snapshot on save or reread failure. Default WinForm/WebForm projections may follow a valid Structure change without producing a false `AuthoredPartsCorrupted` failure.
- Thanks to [@davidagostini](https://github.com/davidagostini) for the modular Business Component and atomic Structure support — see [PR #105](https://github.com/lennix1337/Genexus18MCP/pull/105).
- **Native typed Domain resolution and atomic verification in `genexus_delete_object`.** Domain deletion now resolves through the GeneXus SDK identity API, reports native incoming references in dry-run, supports optimistic concurrency with `expectedVersion`, requires confirmation only for persistence, and performs transactional deletion plus post-save reread without invoking lifecycle actions.
- Thanks to [@davidagostini](https://github.com/davidagostini) for native Domain resolution in object deletion — see [PR #106](https://github.com/lennix1337/Genexus18MCP/pull/106).
- **XPZ imports now use lossless overwrite options and capture fidelity from the raw export payload before SDK import preparation**, preserving WebForm `GxWidth`/`GxHeight` and preventing circular fidelity checks. Fixes #102.
- **Build telemetry now resolves the active Environment through the KB service and audits User Control bindings in up-to-date generated JavaScript**, so stale/incomplete `Gx Control Type` output is reported instead of appearing successful. Fixes #103 items 3 and 4.
- **Build generation evidence is now scoped to the SDK-selected environment output root**, so a newer production artifact cannot be reported for a development build; `genexus_kb` can also read/select the active environment through the official SDK path. Fixes #103 item 3.
- Thanks to [@danielkrueger](https://github.com/danielkrueger) for XPZ fidelity and environment-scoped build evidence — see [PR #107](https://github.com/lennix1337/Genexus18MCP/pull/107).

### Changed

- **Multi-KB target selection is now predictable.** `genexus_kb action=set_default` controls the implicit target for calls that omit `kb`, while explicit aliases remain available for parallel work. Gateway and OpenCode KB catalogs now preserve all registered entries across array/map config formats, and `whoami`/`genexus_kb list` expose the active, open, known, and declared aliases.
- **OpenCode now works across both MCP configuration layouts.** The installer automatically configures detected OpenCode installations while preserving OpenCode 1.x's direct `mcp.genexus` entry and the current nested `mcp.servers.genexus` form; KB-bound responses identify their source with `kbAlias` for text-only clients.
- **KB-bound responses now avoid redundant payload cloning and serialization.** The Gateway transfers ownership of completed Worker payloads before attaching `kbAlias`, reducing allocation and response-finalization cost for large search, read, and edit results while retaining defensive cloning for shared payloads.

### Internal

- **Disposable U16 Data View persistence harness and schema budget.** Added `scripts/Test-DataViewAtomic.ps1` to verify dry-run immutability, atomic create/reread/delete, root-only BC shape, physical mapping, stale-version rejection, and absence of implicit lifecycle actions; raised the intentional combined tool-schema budget from 20,500 to 21,100 tokens (measured ~20,932).

## v2.41.9 - 2026-08-18

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_structure action=move_attribute` now preserves authored Transaction logic across SDK saves and worker restarts.** Rules and Events snapshots select the source-bearing native part when GeneXus exposes duplicate lazy entries, restore through the normal Source writer, and verify the persisted result after commit. Fixes [#99](https://github.com/lennix1337/Genexus18MCP/issues/99).
- **`genexus_transfer action=import` now verifies WebForm fidelity after XPZ import.** The import preserves dimensions, bindings, and theme references through lossless SDK options, repairs a detected mismatch when possible, and reports the verification result instead of claiming success from `ImportFile` alone. Fixes [#102](https://github.com/lennix1337/Genexus18MCP/issues/102).
- **Lifecycle build previews, environment telemetry, and User Control generation evidence are now reliable.** `dryRun=true` stays on the synchronous preview path, compact responses retain the resolved Environment, and generated JavaScript reports incomplete `Gx Control Type` bindings by count. Fixes [#103](https://github.com/lennix1337/Genexus18MCP/issues/103).

## v2.41.8 - 2026-08-17

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Visual layout batch parity and rollback in `genexus_layout action=set_properties`.** Batch layout property writes now synchronize `Caption` and `CaptionExpression` consistently across controls and automatically roll back the layout to the pre-call baseline if readback or post-save verification fails.
- **Snapshot and part preservation in `genexus_structure action=update`.** Updating Transaction visual structures via `SyncVisualStructure` now captures a complete snapshot of non-Structure parts (`Rules`, `Events`, `WebForm`, `Variables`) and restores them if reset during SDK Transaction saving, verifying authored parts preservation before committing.
- **Expanded visual root element support in `NormalizeEditableXmlInput`.** Supported `<Layout>` and `<ReportPart>` roots in addition to `<GxMultiForm>`, `<BODY>`, and `<HTML>` to ensure SDPanels and Procedure/Report layouts read via `genexus_read` can be modified and written back directly.
- **Text persistence verification for Documentation, Help, DataSelector, and WSDL.** `TextPersistenceVerifier` now classifies `Documentation`, `Help`, `DataSelector`, and `WSDL` as code/text parts, preventing false `WriteNotPersisted` errors due to SDK whitespace or line-ending formatting differences.
- **`ExtractProcedure` dry-run preview and line-ending normalization in `genexus_refactor`.** `ExtractProcedure` now normalizes CRLF/LF line endings before matching the code block in the source object, pre-verifies existence before attempting object creation to avoid orphaned procedures, and respects `dryRun=true` by returning planned extraction details without mutating the KB.
- **Standardized error responses in `genexus_analyze mode=linter`.** Replaced unformatted JSON error strings in `LinterService` with canonical `McpResponse.Err` envelopes.

## v2.41.7 - 2026-08-17

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_edit` on `part=Events` and `part=Conditions` now defaults to normalized verification and tolerates harmless formatting/EOL differences.** `TextPersistenceVerifier` now classifies `Events` and `Conditions` alongside `Source` and `Rules` as code/text parts, canonicalizes line endings, and resolves default verification mode to `normalized`. `NormalizedCodeEquals` tolerates blank lines inserted or removed by GeneXus SDK rendering, eliminating false `WriteNotPersisted` errors on successful Event edits. Fixes [#100](https://github.com/lennix1337/Genexus18MCP/issues/100).
- **`genexus_structure move_attribute` and `remove_attribute` preserve non-Structure authored parts and strictly verify snapshot integrity.** Reordering or removing attributes in a Transaction structure now preserves all non-Structure parts (`Rules`, `Events`, `WebForm`, `Variables`), restoring them if reset by SDK transaction saving. Post-save verification (`VerifyMove` / `VerifyRemoval`) now checks that every authored part in the pre-write snapshot remains intact, rather than skipping parts marked default by the SDK. Fixes [#99](https://github.com/lennix1337/Genexus18MCP/issues/99).
- **`genexus_layout set_property` on `gxButton` retains `Caption` and rolls back on verification failure.** Setting `Caption` on visual controls no longer removes the underlying `Caption` attribute when synchronizing with `CaptionExpression`. If post-save readback verification fails, `SetProperty` automatically rolls back the visual XML to the pre-call baseline and returns `rolledBack: true` in the error envelope. Fixes [#101](https://github.com/lennix1337/Genexus18MCP/issues/101).
- **Lossless XPZ import in `genexus_transfer action=import`.** `TransferService.Import` now defaults to `FullOverwrite` with `ClassConflicts=UseFromExport` and `ThemeOptions=Overwrite`, preventing WebForm dimensions (`GxWidth`, `GxHeight`) from being dropped and preserving theme class references when overwriting existing KB objects from an `.xpz`. Fixes [#102](https://github.com/lennix1337/Genexus18MCP/issues/102).
- **WebForm XML root compatibility and build response telemetry.** `WebFormXmlHelper.NormalizeEditableXmlInput` now accepts `<BODY>` and `<HTML>` root elements so Transaction WebForms read by `genexus_read` can be written back without manual wrapping; `genexus_lifecycle action=build` echoes the resolved environment name and surfaces `upToDate: true` in `generateEvidence` when nothing needed to be rebuilt; and `dryRun: true` is respected before dispatching build and specification actions. Fixes [#103](https://github.com/lennix1337/Genexus18MCP/issues/103).

## v2.41.6 - 2026-08-17

### Added

- **Wildcard search support in `genexus_query`.** `genexus_query` now treats `query="*"` as a global search matching all indexed objects, rather than searching for the literal asterisk character.
- **Automatic single-KB default promotion in Gateway.** When `config.json` declares exactly one KB in `Environment.KBs` and `DefaultKb` is not explicitly configured, the Gateway automatically promotes it to `DefaultKb`, eliminating the need for an explicit `genexus_kb action=open` before calling tools.
- **Tolerant action auto-inference in `genexus_create`.** `genexus_create` automatically infers `action="object_atomic"` when `source`, `variables`, `rules`, or `parms` are provided, and `action="object"` when `name` and `type` are supplied without an explicit `action` key.
- **Auto-variable declaration on `genexus_edit` (`autoDeclareVariables=true`).** When modifying `Source`, `Events`, `Rules`, or applying textual patches, setting `autoDeclareVariables=true` scans updated code for newly introduced `&Variable` references and automatically creates them in `VariablesPart`, inferring types from homonymous Attributes/Domains or default types.
- **Local subroutine extraction in `genexus_refactor` (`action="ExtractSubroutine"`).** Extracts a designated code block into a local `Sub 'SubName' ... EndSub` within the object's source/events and replaces the callsite with `Do 'SubName'`, supporting `dryRun=true` preview.
- **Transitive dependency closure export in `genexus_transfer` (`includeDependencies=true`).** When exporting an object to `.xpz`, setting `includeDependencies=true` navigates the `Calls` and `Tables` dependency graph in `SearchIndex` to gather and bundle all referenced objects and tables into a self-contained archive.

### Internal

- **Token budget bump in `ToolSchemaSizeTests`.** Bumped schema budget from 20,300 to 20,500 tokens to accommodate `autoDeclareVariables`, `ExtractSubroutine`, and `includeDependencies` schema additions.

## v2.41.5 - 2026-08-16

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_edit mode=patch` no longer reverts a successfully verified Source when `verifyRollback=true`.** Rollback is now restricted to post-save divergence with `rollbackOnFailure=true`; persistence verification performs a fresh, complete SDK read and reports requested, saved, and independently re-read content separately. Source version tokens include the persisted content fingerprint, so rapid sequential saves cannot reuse a stale timestamp token. Thanks to [@davidagostini](https://github.com/davidagostini) — see PR [#98](https://github.com/lennix1337/Genexus18MCP/pull/98).

## v2.41.4 - 2026-08-16

## v2.41.3 - 2026-08-15

### Changed

- **99.97% latency cut & 0 B allocation in Gateway legacy tool resolution (`McpRouter.TryRewriteLegacyTool`).** Converted `TryRewriteLegacyTool` to evaluate matching cases lazily in [`McpRouter.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Gateway/McpRouter.cs). On normal non-legacy tool calls (`genexus_query`, `genexus_read`, `genexus_edit`), latency dropped from 244.8 ns to 4.1 ns (-98.3%), and on large edit payloads from 14.18 μs to 3.89 ns (-99.97%, 3,646x speedup), completely eliminating all heap allocations (0 B allocated).
- **Zero-clone canonicalization in `IdempotencyMiddleware`.** Replaced the full `args.DeepClone()` in [`IdempotencyMiddleware.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Gateway/IdempotencyMiddleware.cs) with root-level property skipping inside `JsonCanonicalize`, eliminating object tree duplication during SHA256 mutation payload hashing.
- **Zero-reflection JSON envelope construction in Worker `SearchService`.** Replaced `JObject.FromObject(new { ... })` anonymous type serialization with direct `JArray`/`JObject` construction across exact-match and ranked search paths in [`SearchService.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Worker/Services/SearchService.cs), eliminating runtime reflection overhead and intermediate anonymous objects.
- **Zero-allocation dispatch scopes in Worker command lifecycle.** Reused static `NoopDisposable` instances for null tokens in [`WorkerCancellationRegistry.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Worker/Helpers/WorkerCancellationRegistry.cs) and [`ProgressContext.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Worker/Helpers/ProgressContext.cs), and eliminated redundant `.ToLower()` string allocations before case-insensitive lookup in [`CommandDispatcher.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Worker/Services/CommandDispatcher.cs).
- **54% latency cut & 55% memory reduction in Gateway envelope projection.** Streamlined [`NormalizeToolPayloadForAxi`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Gateway/Program.ToolPayload.cs) and [`ProjectArrayItems`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Gateway/Program.ToolPayload.cs) to project array items directly without full intermediate object tree cloning, eliminated redundant `DeepClone()` on `result["structuredContent"]`, and optimized `BuildTotalsByType` with single-lookup dictionary updates. `CompactProjection_500Rows` latency dropped from 500 μs to 230 μs (-54%), heap allocations decreased from 1.7 MB to 775 KB (-55%), and Gen0/Gen1 GC collections were reduced by >53%.
- **Zero-allocation fast structural pre-check in `TruncateResponseIfNeeded`.** Added structural pre-filtering in [`Program.ToolPayload.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Gateway/Program.ToolPayload.cs) before computing full-tree JSON string serializations, bypassing unconditional string serialization on sub-budget tool responses (whoami, status, inspect, short reads, mutations).
- **Allocation-free `DidYouMean.Levenshtein` & candidate length pruning.** Replaced heap array allocations in [`DidYouMean.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Gateway/DidYouMean.cs) with stackalloc `Span<int>` buffers and added candidate length-delta filtering in `DidYouMean.Suggest`. Levenshtein distance calculations now allocate 0 bytes on the heap (215 ns), and non-matching suggestion lookups execute in 17.7 ns.
- **Optimized type lookups and aggregations in Worker `ListService`.** Replaced inline type array instantiations in `ListService.IsLikelyType` with a static frozen `HashSet<string>` and streamlined `ComputeAggregates` to use single-pass `TryGetValue` updates.

## v2.41.2 - 2026-08-14

### Added

- **Multi-target batched build via `BuildWithTheseOnly` on `includeCallees=none`.** When `genexus_lifecycle action=build` receives multiple comma-separated targets and `includeCallees=none`, the worker routes all targets to `IBuildServiceBL.BuildWithTheseOnly` in a single shared specification and MSBuild compilation pipeline, avoiding N sequential `BuildOne` cycles. Fixes [#96](https://github.com/lennix1337/Genexus18MCP/issues/96).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Lifecycle router forwards `dryRun` and `deploy` parameters.** `SystemRouter` and `CommandDispatcher` now forward `dryRun` and `deploy` across `build`, `rebuild`, `specify`, and `compile_check` actions, ensuring preview validation and deploy options are respected by the worker. Fixes [#96](https://github.com/lennix1337/Genexus18MCP/issues/96).
- **`SdkSurfaceProbe` surfaces warning when GeneXus path does not resolve.** Instead of silently returning when the GeneXus installation directory is missing, `SdkSurfaceProbe.TryPreloadSdkAssemblies` now appends an explicit warning to `result.Warnings` indicating the skipped path and noting that only pre-loaded AppDomain assemblies are scanned. Fixes [#94](https://github.com/lennix1337/Genexus18MCP/issues/94).
- **Preserved content and snapshot verification on object moves in `genexus_properties action=move`.** Move operations capture all GeneXus parts and properties before moving, prioritize non-destructive `EntityManager.UpdateParent`, validate the snapshot within the SDK transaction, and perform independent post-commit verification. Thanks to [@davidagostini](https://github.com/davidagostini) — see PR [#95](https://github.com/lennix1337/Genexus18MCP/pull/95).

### Internal

- **Worker local test isolation and coverage strengthening.** Worker tests isolate local SDK assembly resolution from NuGet-provided dependencies and maintain explicit coverage thresholds across Gateway and Worker. Thanks to [@williamgarciadev](https://github.com/williamgarciadev), [@danielkrueger](https://github.com/danielkrueger), and [@davidagostini](https://github.com/davidagostini) — see PR [#93](https://github.com/lennix1337/Genexus18MCP/pull/93).

## v2.41.1 - 2026-08-13

### Changed

- **O(1) dictionary key resolution in `SearchService` exact-match.** Replaced full O(N) `Objects.Values` iteration with direct dictionary probe on `typeFilter:name` in [`SearchService.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Worker/Services/SearchService.cs#L102), making exact name queries instantaneous on large Knowledge Bases.
- **Pre-computed static caching for `tools/list` discovery.** Eliminated expensive per-call `DeepClone()` of the full tool definitions array in [`McpRouter.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Gateway/McpRouter.cs). Added `Cache-Control: public, max-age=3600` response headers on discovery endpoints in [`Program.Http.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Gateway/Program.Http.cs) adhering to the 2026-07-28 MCP specification.
- **Event-driven STA message pump in Worker dispatching.** Replaced timer-only polling with immediate `BeginInvoke` event-driven queue draining in [`Program.cs`](file:///C:/Projetos/Genexus18MCP/src/GxMcp.Worker/Program.cs), reducing internal dispatch latency from 15ms to 0ms.
- **Deepened mutation and patch subsystem inside `WriteService`.** Encapsulates the `PatchService` lifecycle and eliminates redundant per-call instantiations during `genexus_edit` patch mode.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_properties action=move` now preserves and verifies the complete object.** The move captures every GeneXus part and authored property before mutation, prefers the non-destructive `EntityManager.UpdateParent` path, validates the snapshot inside the SDK transaction, and performs an independent post-commit re-read. `dryRun` is non-persistent, `baseVersion` rejects concurrent changes, and any divergence with `rollbackOnFailure=true` restores the original parent and content. Responses expose saved/persisted/verified state, requested and persisted hashes, rollback evidence, and confirm that no lifecycle operation ran.

### Internal

- **Testes locais do Worker agora carregam o SDK GeneXus configurado sem preparação manual.** O projeto de testes copia para sua saída apenas as dependências ausentes do `GX_PATH`, preservando as versões fornecidas por NuGet; a coleta completa de cobertura U16 volta a executar os 1.912 cenários. Os pisos são explícitos por componente (Gateway 60%, Worker 45%), sem exclusões ou testes desativados.
- **Higiene incremental de CI e análise estática.** O teste de lease agora caracteriza concorrência com temporários de outra instância, avisos de nulabilidade e variáveis sem uso foram eliminados nos pontos publicados pela CI, e o upload de cobertura usa `actions/upload-artifact` v6 fixado por SHA.
- **Zero-warning test hygiene and async task execution.** Converted blocking `.Wait()` / `.Result` test calls to `async Task` with `await` in `LauncherResolutionTests`, `StatusWaitTests`, `IdempotencyInflightTests`, and `EdgeCaseRegressionTests` (`xUnit1031`). Resolved unassigned field warnings (`CS0649`), nullable annotations (`CS8632`), and collection assertion idioms (`xUnit2013`).
- **Codebase architectural deepening plan executed.** Implemented and verified deepening refactor plan (`docs/plans/2026-08-13-architecture-deepening.md`), ensuring high module depth, tight locality, and robust test suite verification across Gateway and Worker.

## v2.41.0 - 2026-08-13

### Added

- `genexus_read` now exposes persisted Data Selector definitions through the
  GeneXus 18 U16 public SDK: ordered parameters, complete conditions, orders,
  `Defined By`, referenced attributes, and unambiguous base Table/Transaction
  resolution with declared indexes. The path is strictly read-only, returns a
  `versionToken`, and never runs Specify, Generate, Build, Rebuild, compilation,
  reorganization, execution, or tests. SDK capabilities that do not exist for
  Data Selectors (`projection` and resolved `joins`) are reported explicitly in
  `unsupportedParts` instead of appearing as empty data. The combined
  `structure` is marked as a semantic projection of those typed SDK elements,
  avoiding U16's internal collection type names.

### Changed

- **The text-patch pipeline now separates matching from orchestration and persistence evidence.** `PatchTextEditor` owns pure Replace/InsertAfter matching, while `PatchPersistenceReceipt` owns the stable saved/verified/hash/rollback response fields. The public `genexus_edit` contract and GeneXus SDK save path are unchanged.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_edit mode=patch operation=Replace` now reports success only after a durable Source/Rules save.** Text patches use the same explicit part-save and transaction path as full edits, so GeneXus 18 U16 can no longer advance the object version and leave the replacement only in the live SDK instance. Empty replacements are supported, and the response separates `saved` from `verified`, includes requested/re-read hashes and old-context evidence, and reports rollback verification when requested.
- **`genexus_structure action=create_index` no longer persists during `dryRun=true`.** The Worker now keeps validation/projection separate from SDK mutation, verifies the persisted index snapshot and composite `versionToken` after every preview, and returns `DryRunMutationDetected` with rollback details if any state changes. Effective writes support `baseVersion` optimistic concurrency, preserve the requested attribute order, re-read and verify the exact index, and restore the prior snapshot on save/verification failure when `rollbackOnFailure=true`. The action does not implicitly Specify, Generate, Build, Rebuild, compile, reorganize, execute, or test.
- **`genexus_lifecycle action=specify` surfaces structured evidence and `effective_status=SucceededWithGaps` for unreachable or not found objects.** When GeneXus skips specification because an object is unreachable (`spc0217`) or not found in the Knowledge Base, the worker now captures `generateEvidence` (`ok=false`, `unreachable`/`notFound` lists, note) and emits a `[specify-gap]` warning, allowing the gateway to surface `effective_status="SucceededWithGaps"` instead of a false clean success. Fixes [#86](https://github.com/lennix1337/Genexus18MCP/issues/86).
- **`UIServices.SetDisableUI(true)` invoked during worker bootstrap.** Explicitly disables interactive modal dialogs prior to `UIServices.Initialize`, preventing blocked STA threads during headless execution. Fixes [#88](https://github.com/lennix1337/Genexus18MCP/issues/88).
- **`genexus_sdk_probe` pre-loads and scans unreferenced GeneXus/WWP SDK assemblies from disk.** Discovers and loads assemblies from the GeneXus installation, `Packages`, and `Patterns` directories into the AppDomain prior to probing so that unreferenced tools and generators are discovered. Also cleaned up duplicate merge header artifacts in `CHANGELOG.md`. Fixes [#87](https://github.com/lennix1337/Genexus18MCP/issues/87).

### Internal

- Thanks to [@davidagostini](https://github.com/davidagostini) for Data Selector reading, patch replace durability, create_index dry-run safety, and test coverage improvements — see PRs [#85](https://github.com/lennix1337/Genexus18MCP/pull/85), [#89](https://github.com/lennix1337/Genexus18MCP/pull/89), [#90](https://github.com/lennix1337/Genexus18MCP/pull/90), [#91](https://github.com/lennix1337/Genexus18MCP/pull/91), and [#92](https://github.com/lennix1337/Genexus18MCP/pull/92).

## v2.40.2 - 2026-08-12

### Added

- **`genexus_edit` can persistently remove an Attribute reference from a Transaction Structure.** A single `remove_attribute` semantic operation now detaches the native `TransactionAttribute`, saves and re-reads the Transaction, and returns a before/after diff. The KB-global Attribute and its SubType Group memberships are hash/membership verified as preserved; `dryRun`, `baseVersion`, and automatic snapshot rollback are supported.
- **`genexus_structure action=move_attribute` reorders an existing Transaction attribute without recreating it.** Place an attribute `before` or `after` another attribute in the same level, or at a zero-based `position`; root, named, and nested `levelPath` levels are supported. Dry runs show only the affected positions, `baseVersion` rejects stale edits, and effective writes snapshot every Transaction part, re-read after save, verify native identities/properties and relative order, and restore the complete snapshot if GeneXus normalizes the move or changes anything else. The operation never specifies, generates, builds, reorganizes, or reapplies a Pattern.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_structure action=create_index` no longer persists during `dryRun=true`.** The Worker now keeps validation/projection separate from SDK mutation, verifies the persisted index snapshot and composite `versionToken` after every preview, and returns `DryRunMutationDetected` with rollback details if any state changes. Effective writes support `baseVersion` optimistic concurrency, preserve the requested attribute order, re-read and verify the exact index, and restore the prior snapshot on save/verification failure when `rollbackOnFailure=true`. The action does not implicitly Specify, Generate, Build, Rebuild, compile, reorganize, execute, or test.

- Build diagnostics now classify GeneXus source/query errors (`src####`, `qry####`)
  as specification failures and build-infrastructure errors (`gtm####`, `mtd####`,
  `pmm####`, `rgz####`, `rgo####`) as environment failures, so recovery guidance
  points to the correct cause.
- WorkWithPlus `userAction` bindings now persist the `gxobject` target through the
  SDK PatternInstance change command when the XML deserializer drops it.
- `genexus_edit mode=patch` now always re-reads Source/Rules after the SDK save
  and supports `verifyMode=normalized|semantic|exact` (`normalized` by default).
  Harmless SDK normalization of EOLs, encoding markers, trailing whitespace, or
  repeated blank lines no longer produces a false `WriteNotPersisted`. Responses
  include raw and normalized SHA-256 hashes plus re-read, match, replacement, and
  normalization evidence. A real mismatch performs no implicit second write;
  rollback occurs only when explicitly requested with a valid snapshot.
- Patch-mode edits now enforce `baseVersion` optimistic concurrency at entry and
  again immediately before the single write. `dryRun` remains non-persistent and
  does not claim a post-save re-read.
- Patch-mode optimistic-concurrency misses now include a concrete object-list
  recovery step instead of returning an uncured `ObjectNotFound` error.

### Internal

- Raised the tool-schema budget from 19,500 to 19,800 tokens for the `move_attribute` action and its module, attribute, before/after/position, level/levelPath, dry-run, and optimistic-concurrency fields (measured about 19,637 tokens).
- Thanks to [@davidagostini](https://github.com/davidagostini) for transaction structure editing and normalized patch verification improvements — see PRs [#83](https://github.com/lennix1337/Genexus18MCP/pull/83) and [#84](https://github.com/lennix1337/Genexus18MCP/pull/84).

## v2.40.1 - 2026-08-11

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- `genexus_lifecycle action=rebuild` now scopes comma-separated target lists through
  `SpecifyOneOnly` in the in-process runner instead of falling through to a full KB
  `RebuildAll`.

## v2.40.0 - 2026-08-10

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- `genexus_search_source` and the `genexus_read` log-grep path no longer hang the
  whole Knowledge Base on a pathological search pattern. A regex with
  catastrophic backtracking (e.g. `(a|aa)+$` against a long single line) used to
  run with no match timeout on the worker's single STA thread, blocking every
  call to that KB for ~15 minutes until the wedged-worker killer fired. Every
  LLM-supplied pattern now carries a 2-second per-match timeout; a pattern that
  exceeds it returns `PatternTimeout` with an explanation, and the worker keeps
  serving other calls.
- When an async job's SDK call silently blocks past its watchdog bound, the
  wedged worker process is now **recycled** instead of leaving the KB unusable.
  Previously the job was marked `stalled` but the worker's STA thread stayed
  stuck on the SDK call, so the recovery steps (re-run synchronously, cancel)
  queued behind the same blockage. Now the watchdog force-kills the wedged
  worker the moment the stall is detected and a replacement respawns for the KB;
  the stalled envelope reports `recycledWorker: true` and reads keep working.
- `genexus_structure action=update_group` now verifies its write like every
  other write path. A membership change could report `GroupUpdated` even when
  the SDK dropped part of the update; the service now re-reads the group's
  members after saving and returns `GroupUpdateNotPersisted` (with the
  expected vs. actual sets) when the write didn't survive, and only claims
  `persistedVerified: true` when a genuinely fresh re-read confirmed it.
- Moving an object into a Folder/Module can no longer bind an unrelated
  `EntityManager` type. `ObjectMover`'s reflection fallback now constrains the
  bare simple-name lookup to the `Artech.*` namespace (and logs which binding
  it resolved), so a coincidentally-named type in another assembly can't be
  picked over the GeneXus SDK's own class.

### Changed

- `genexus_db action=reorg_impact deep=true` (and `reorg_preview`) now gets a
  10-minute sync ceiling instead of the generic 60-second default, and the
  response carries a runtime note explaining that a deep impact analysis runs
  the specification engine and can legitimately take minutes. A long-running
  deep analysis no longer times out spuriously while the specifier is still
  working.

### Internal

- `plans/README.md` marks plans 068–072 DONE with the live-KB validation
  evidence; regression coverage landed in `SourceSearchPerfGuardTests`,
  `LogFilteringTests`, `AsyncJobWatchdogTests`, `WorkerPoolTests`,
  `GroupStructureVerificationTests`, `ObjectMoverHardeningTests`, and
  `GatewayBudgetTests`.

## v2.39.4 - 2026-08-10

### Added

- Added `scripts/mcp_recover.ps1`, an out-of-band Streamable HTTP client for
  continuing diagnostics when a client-owned STDIO transport has closed. It
  initializes a fresh session, discovers the current catalog, and blocks tools
  not marked read-only unless `-AllowWrite` is passed explicitly.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- `genexus_apply_pattern` now selects the WorkWithPlus
  `CreatePatternInstanceWithTemplate` overload by its complete compatible
  signature. GeneXus 18 Upgrade 16 exposes both four- and five-parameter
  overloads, which made name-only reflection fail with
  `AmbiguousMatchException` when attaching WorkWithPlus to a new WebComponent.
  WebPanel and WebComponent targets now use the five-parameter
  `SettingsView.Web` overload; SDPanel keeps the native-mobile overload.
  Success is now confirmed by re-reading the PatternInstance association;
  failures include the selected/found signatures and full inner exception.
- WorkWithPlus first-attach diagnostics now resolve the effective
  `Environment.config` from the active GeneXus installation's configured
  `UserAppDataPath` and verify write access before invoking the package.
  Missing permission returns `PatternEnvironmentAccessDenied` with the exact
  path, configuration source, process identity, and complete access exception
  instead of passing diagnose and failing later as a generic `PatternNoOp`.

- Post-write verification no longer rejects writes the SDK module-qualified. When
  GeneXus saves a Procedure/WebPanel source it can rewrite object and table
  references with the owning module's prefix (`For Each Foo` persists as
  `For Each MyModule.Foo`); the verifier treated the inserted token as a content
  mismatch, firing `WriteNotPersisted` and rolling `object_atomic` creates back.
  A reference is now considered normalization when every difference is a
  `<Module>.<Name>` qualification, and `mutation.diff.reason` reports
  `moduleQualification` so the agent can see exactly what the SDK rewrote.
  Spacing inside string literals is still treated as a real difference.
- Async `genexus_edit` / variable / GXserver jobs can no longer stay `running`
  forever when the SDK call silently blocks. Each job now has a watchdog bound
  (10 minutes minimum, `max(10 min, 8×estimated_seconds)` up to 60 minutes;
  tune with `GXMCP_ASYNC_JOB_WATCHDOG_S`, `0` disables). A job that exceeds the
  bound is marked `stalled` — a terminal error carrying recovery steps (re-run
  the edit synchronously to get the immediate validation error, cancel the stuck
  op, check the IDE for a waiting modal dialog) instead of reporting progress
  that isn't happening. The accepted envelope advertises `stallBoundSeconds` up
  front, and cancelling a job that already finished is a no-op.
- `genexus_create type=SDT` with `firstItem`/`firstItemType` now actually
  persists the seeded member. The SDK's `AddItem` mutates the in-memory SDT
  structure but does not always flag the `SDTStructurePart` dirty, so the
  object `Save()` wrote the old (empty) serialized XML while the response
  claimed `seeded` — a follow-up `genexus_structure` read showed no children.
  The create path now forces the structure part dirty before saving (the same
  fix the SDT write path already applied), so the first member survives and
  round-trip reads agree with the creation response.
- `genexus_read` (and other read tools) could keep returning stale content after
  a delete or write until the gateway restarted. The gateway replays the first
  successful response for an identical read to avoid re-hitting the SDK, but
  that semantic cache was only cleared by a subset of write tools — a
  `genexus_delete_object`, `genexus_variable` edits, `genexus_apply_pattern`,
  `genexus_rename_across_kb`, structure/index mutations, `genexus_transfer
  action=import`, `genexus_db` data mutations (translations/sample data) and
  gxserver commit/update/lock/resolve were missed, so a read of a deleted
  object could return its pre-delete content. Every KB-mutating tool/action
  now invalidates the cache, and cache entries are scoped per KB so identical
  reads against different open Knowledge Bases never share envelopes.

Thanks to [@davidagostini](https://github.com/davidagostini) for the WorkWithPlus
overload-resolution fix and the out-of-band MCP recovery client — see PRs
[#76](https://github.com/lennix1337/Genexus18MCP/pull/76) and
[#77](https://github.com/lennix1337/Genexus18MCP/pull/77).

### Internal

- `AGENTS.md` gained an "Engineering workflow rules" section locking in the
  2026-08-10 session lessons: mandatory PR-author credit in the CHANGELOG,
  multi-PR merge flow (Unreleased conflicts + fork-PR worktree merge), the
  semantic-cache invalidation duty for new mutating tools (guarded by
  `SemanticCacheInvalidationTests`), the real-KB HTTP validation harness, and
  the bash-on-Windows gotchas that previously burned turns.
- `OperationTracker.CleanupExpired` no longer sweeps in-flight operations by age.
  A running operation past the retention window (tiny test retention plus thread
  descheduling under CI load) used to have its request→operation mapping dropped
  mid-flight, making every later completion/status poll return `NotFound` — the
  cause of the flaky `CleanupExpired_DoesNotDropMappingForReusedRequestId` that
  blocked PR #76's merge once. Only completed operations age out now.
- Release tooling now survives Windows PowerShell 5.1 and long-running commands:
  `release.ps1` and `build.ps1` are saved as UTF-8 **with BOM** (5.1 mis-parses
  BOM-less `.ps1` containing em-dashes/arrows), `release.ps1 -Detach` relaunches
  the whole release in a background pwsh writing to `%TEMP%\gxmcp-release*.log`
  so a 30 s shell timeout can no longer kill a multi-minute run, 5.1 invocations
  auto-re-exec under pwsh, the CHANGELOG promotion step fails loudly instead of
  silently shipping a release whose notes fall back to generic text, and
  `.editorconfig` pins `utf-8-bom` for `*.ps1`.
## v2.39.3 — 2026-08-09

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- Post-write verification now performs a fresh, explicit full-part read instead of
  comparing an MCP-defaulted/minimized page with the complete requested source. A
  truncated or failed verification read is reported as `indeterminate` and does not
  become `WriteNotPersisted`; mutation diagnostics distinguish `normalization`,
  `truncation`, `readFailure`, and a real `contentMismatch`.
- Lifecycle status now merges the worker's SDK single-flight state, so long-running
  Undo operations report `isBusy: true` with the active operation and elapsed time.
- Batch `genexus_read targets=[...]` now honors `parts=[...]`; variable persistence
  checks use an uncached full read and reconcile SDK errors that occur after commit.
- Best-effort patches no longer force a full-object validation pass, cancellation of
  non-preemptible SDK calls reports `CancellationRequested`, `WorkerBusy` identifies
  the blocking operation, and forced reloads verify replacement workers are SDK-ready.
- **`genexus_apply_pattern` now accepts WorkWithPlus on WebComponents.** WebComponents that expose WorkWithPlus in the GeneXus IDE were incorrectly rejected by the MCP's parent-type gate. They now use the same template-based direct-attach lifecycle as WebPanels, preserving the original object type while creating and projecting the linked `WorkWithPlus<Object>` instance.

## v2.39.2 — 2026-08-07

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- Fixed Report layout writes so `genexus_layout action=set_property` preserves untouched controls, RGB colors, alignment, and geometry ([#72](https://github.com/lennix1337/Genexus18MCP/issues/72)).

## v2.39.1 — 2026-08-07

This release fixes `object_atomic` rollback on Procedure source casing normalization, async `genexus_edit` failures on XML `PatternInstance` default attributes, and `genexus_layout set_property` degradation on Report layouts.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_create action=object_atomic` no longer rolls back valid objects on SDK Source casing/indentation normalization.** (Issue #70) `WhitespaceInsensitiveEquals` now performs case-insensitive comparison (`OrdinalIgnoreCase`), preventing Procedure `Source` keyword case-normalization (`for each` -> `For Each`, `parm` -> `Parm`, `if` -> `If`) from triggering false-positive `WriteNotPersisted` errors that previously rolled back and deleted freshly-created objects.
- **Async `genexus_edit` no longer reports `failed` status for persisted `PatternInstance` parts.** (Issue #71) `WhitespaceInsensitiveEquals` now evaluates structural XML equivalence for XML parts (`PatternInstance`, `Layout`, `WebForm`, etc.), ignoring SDK-dropped default/empty attributes (`default*`, empty strings, default boolean/numeric values) and empty element self-closing differences so background edit jobs report `succeeded`.
- **`genexus_layout action=set_property` on Reports no longer degrades untouched controls or RGB colors.** (Issue #72) `ReportLayoutHelper.TryParseColor` now parses comma-separated RGB color strings (`192, 0, 0`) in addition to semicolons, preventing RGB colors from falling back to `Black`. `ReportLayoutHelper.WriteLayout` now verifies whether the current SDK property value is already equivalent (`IsPropertyEquivalent`) before calling `TrySetProperty`, preventing untouched controls, geometry, alignment, and colors from being overwritten with lossy defaults.

## v2.39.0 — 2026-08-03

This release fixes `dryRun` precheck error handling for pattern/visual parts, clarifies `dryRun` verification scope, updates agent instructions regarding SDK folder/module placement capabilities, and enforces strict release-linked issue closure.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`dryRun` precheck failures no longer return `ok` status when reading current pattern/visual parts fail, and `dryRun` responses now detail verification scope.** (Issue #67) When `ReadPatternPartXml` or `ReadVisualPartXml` threw an exception during precheck, the `dryRun` catch block previously returned `code: "WriteDryRun"` inside an `ok` envelope, masking the read failure. Catch blocks now return `PatternReadFailed` / `VisualReadFailed` error envelopes. Successful `dryRun` responses now explicitly include `verified` scope (`["xmlParse", "childrenOrderedList", "diffVsCurrent"]`), `savePathExercised: false`, and a warning on WorkWithPlus `PatternInstance` parts noting that pattern saves can still be rejected by the WWP validator on save.
- **Agent instructions no longer claim that folder/module placement is impossible.** (Issue #65) `AGENTS.md` — loaded as context in every agent session, and the shared convention file for Claude Code, Cursor, Codex and Aider — still described object placement as an SDK wall and told agents to move objects from the GeneXus IDE by hand. Placement has worked since v2.35.0, so agents reading that section were skipping `genexus_properties action=move` and `genexus_create folder=`/`module=` entirely. The section now documents the move, the `MoveNotPersisted` write-back check, and the fact that `action=set propertyName=Folder` is routed to the move rather than rejected. Server behavior is unchanged — only the instructions were wrong. Also enforces strict rule that issues are only closed after a release link is attached.

### Internal

- The rewritten `AGENTS.md` section keeps a short *why this was believed* note: the `Parent`/`ParentKey`/`Module` setters genuinely read as empty stubs in the facade/reference assembly, which is what produced the original verdict; the runtime persist goes through `EntityManager.SaveWithParent` (`ObjectMover`). Without that note the wrong conclusion is re-derivable from a decompile.
- Struck the matching "confirmed WALL" line in `docs/sdk_uncovered_endpoints_2026-07-20.md` with a dated pointer to v2.35.0, leaving the rest of that dated snapshot intact.
- `PropertyService._placementProps` and the `McpRouterTests` issue-#50 comment described the pre-v2.35.0 rejection paths; both now describe the routing that replaced them.
- Corrected the test counts in `AGENTS.md` (they understated both suites by roughly 2.4x) and the tool count in `README.md` (46 → 47, adding the missing `genexus_wwp` bullet to Tool Surface).

## v2.38.0 — 2026-08-01

The release improves safe GeneXus authoring, persistence verification, inline specification feedback, source-search performance, and modern MCP interoperability.

### Added

- **`genexus_create action=object_atomic` — create (or update) an object with variables, rules, parameters, properties and Source in a single validated call.** Instead of orchestrating several independent calls (create → add variable → edit rules → edit source) — where a mid-way failure leaves a half-configured object — the whole definition arrives at once: `{ type, name, variables:[{varName, typeName?, length?, decimals?, collection?}], rules:["Parm(in:&Id);"], parms:["out:&Msg"], source, properties, validate, mode, expectedVersion }`. Every field is validated BEFORE the first save — variable type names are checked against the KB (so a typo'd Domain name fails with the exact `variables[N]` index instead of persisting a spec-broken object), rules get a parenthesis sanity check, and `parms` is rendered into the `Parm(...)` rule automatically.
- **All-or-nothing by default.** If any step fails after the object was created, the operation compensates: a freshly-created object is deleted, an updated object is restored to its pre-write snapshots — reported inside the error envelope under `compensation` — so a failed call never leaves a partially-configured object behind. `dryRun=true` previews the full plan with the same validation and zero writes.
- **`validate=true` runs the inline Specify pass before confirming success** (reusing the issue #60 machinery): a spec-invalid object is caught in the same call, and with `rollbackOnFailure=true` a fresh object is deleted rather than left spec-broken.
- **Optimistic version control for updates.** Every successful atomic create/update returns a `version` token (a hash of the object's Source/Rules/Variables). Pass it back as `expectedVersion` on the next update and a concurrent change fails with `ConcurrentModification` instead of being silently overwritten — the same intent as `genexus_multi_agent_lock`, built into the operation.

- **`genexus_db action=reorg_preview` — see the physical impact of structural changes before running the reorganization.** Point it at a Transaction (or Table) and it diffs the model's logical structure against the physical Table structure the model records, returning per-column `before`/`after` definitions (`"NUMERIC(18) NOT NULL"` → `"NUMERIC(18) NULL"`) for every change a reorg would apply: type family changes, length and decimal-precision changes, columns added/dropped, and — straight from the issue #57 scenario — a logical `Nullable` that the physical column still stores as `NOT NULL`. The envelope also lists the table's indexes, renders a proposed `CREATE TABLE` for the desired schema (labeled heuristic, like `sql_ddl`), and reports `requiresReorganization` from the SDK's timestamp signal.
- **Destructive changes are highlighted before anything runs.** Cross-family type conversions, length/precision shrinks, `NULL` → `NOT NULL` transitions and dropped columns each carry a structured `warning` (`{column, severity: "destructive", message}`) explaining the risk (data loss, truncation, reorg failure on NULL rows) — so an agent can stop and ask before a reorganization destroys data it didn't expect to touch. Sub-ordinated Transaction levels are walked and diffed individually.
- **`deep=true` swaps the heuristic for the SDK's native impact analysis.** Instead of the timestamp guess, `requiresReorganization` comes from `ISpecifierService.ImpactDatabase` (the same build-heavy specification pass the IDE's Impact Analysis runs) and the `AnalysisResult` verdict is surfaced under `deep`. The operation is read-only in both modes — no KB writes, no database changes; the exact statement-level delta still requires running the reorg on a non-production environment.

- **`genexus_wwp` — list, add, update, move and remove WorkWithPlus grid/bar actions without touching generated code.** Custom buttons on a WorkWithPlus screen live as `<userAction>` elements in the pattern host's `PatternInstance` XML (alongside the registered `Trn_Enter`/`Trn_Cancel`/`Trn_Delete` standard actions). `action=list` reads those containers back as named groups with their actions and attributes; `add_action` inserts a new `<userAction>` into an existing group or creates a new one, with `caption`, `buttonClass`, `icon`, `description`, `confirm`, `selection` (single/multiple → `selectionMode`) and `enabledWhen` availability condition — the issue #58 scenario (`MonitorIntegracaoWW` + a `Reativar` action bound to a Procedure) is one call. `update_action` changes those attributes, `move_action` relocates an action between groups or reorders it, `remove_action` deletes one (`confirm=true`).
- **The associated Procedure is validated against the KB before anything is written.** `procedure` must resolve to an existing object or the call fails with `ProcedureNotFound` and a list hint — no dangling action targets. The response notes that a WWP `userAction` auto-generates an empty `'Do<Action>'` event stub to fill with the actual call.
- **`dryRun=true` returns the exact XML diff without persisting.** Every write action first reports `diff` (`[{op, action, group, before, after}]`) against the current PatternInstance; persisting runs through the same verified pattern-write path as `genexus_edit part=PatternInstance`, which reconciles `childrenOrderedList` and confirms what actually landed — and because the configuration lives in the PatternInstance, it survives reapplying WorkWithPlus.
- **No Security permissions are created implicitly.** Every response carries a `securityNote` stating that no GAM permissions were created or modified — provisioning access for a new action is an explicit, separate step.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`validationMode="specify"` — save and spec-check in a single call.** `genexus_edit`, `genexus_variable`, `genexus_properties` (set), `genexus_structure` (structure writes) and `genexus_create` (object) accept `validationMode="specify"`: right after the write persists, the worker runs the fast Specify+Generate pass and returns the result in the same response — a `_meta.specification` block with structured diagnostics (`[{code, object, member, message}]`) when clean, or a `SpecificationFailed` error listing exactly which `spc*`/`gen*` diagnostics the edited object would trip on build. No more write-then-poll-specify round trips to learn an edit is spec-invalid.
- **`rollbackOnFailure` — auto-restore when the spec check fails.** Combined with `validationMode="specify"`, restores the pre-write state from the edit snapshot when the specify pass reports errors, so a bad edit never leaves the object in a spec-broken state. Works on the part-write paths (`genexus_edit` full/patch/ops); property/variable/structure/create writes have no pre-write snapshot and report `rolledBack=false` with a note instead of silently pretending.
- **Post-write persistence verification on all write paths.** `genexus_properties` (set), `genexus_structure` (update_visual / set_domain), and `genexus_variable` (add, single and batch) now re-read the object after saving and compare what was requested against what persisted. Confirmed writes expose a `before`/`requested`/`persisted` diff block on the success envelope; writes the SDK silently dropped are no longer reported as applied.
- **Empty Knowledge Bases no longer report `IndexNotReady` forever.** A KB whose model contains no objects (e.g. a missing LocalDB model) previously made every `list_objects` / `query` / `read` call return `IndexNotReady` no matter how many times you ran `lifecycle action=index force=true` — and `whoami` kept recommending that same reindex. A built index with zero objects is now recognized as a genuinely empty KB: `list_objects` and `query` return an honest empty result tagged `empty_reason: "kb_has_no_objects"` with a hint, and `whoami` suggests creating an object (or opening a different KB) instead of looping the force-reindex.

- **`PropertyApplied` no longer fires when the property didn't persist.** A property set that the SDK accepted but silently dropped (e.g. Nullable on some builds) now fails with `PropertyNotPersisted`, carrying the property name and the before/requested/persisted values, instead of reporting success over a no-op.
- **`DomainUpdated` no longer fires when enum values were dropped.** A Domain update whose enum values did not survive the save now fails with `DomainUpdateNotPersisted`, naming the dropped values, instead of reporting the update applied with an empty combobox.
- **`StructureUpdated` no longer fires when structure items were dropped.** A Transaction structure update whose requested top-level items are missing after the re-read now fails with `StructureUpdateNotPersisted`.
- **`VariableAdded` no longer fires when the variable didn't land.** Every added variable (single and batch) is re-read from the persisted part; missing variables fail with `VariableAddNotPersisted` instead of a success that spec can never accept.

- **A `genexus_edit` / `genexus_read` / `genexus_analyze` call that omitted `type` on a Transaction now resolves to the Transaction — reliably.** Every Transaction generates a Table model object under the same name, and the gateway's auto-type inference used to guess the type from the few most recently changed objects — when the table shadow surfaced there instead of the transaction, it injected `type="Table"` and the worker resolved the *table* object, which has no Source/Rules/Events part, returning `PatchReadFailed` ("The object does not expose the requested part"). The inference is now fed from the complete index name→types map rather than a small recent-changes window, so a name backed by both a Transaction and its generated Table resolves to the Transaction deterministically; calls that genuinely target a Table pass `type="Table"` explicitly (an explicit type is never overridden).

- **WWP action updates.** Updating an existing `genexus_wwp` action now persists its associated `procedure` instead of accepting and discarding the field.
- **Atomic optimistic concurrency.** `expectedVersion` now fails closed with `VersionUnavailable` when the fingerprint cannot be read, rather than allowing a stale update through.
- **Structure persistence verification.** Full visual-structure writes now detect unexpected persisted items, including the empty-payload case, instead of reporting a false success.
- **Prompt validation.** Unknown prompts and missing or invalid prompt arguments now return JSON-RPC `-32602` errors.
- **Reorganization previews.** Shared physical tables across transaction levels no longer look missing, and generated SQL safely escapes `]` in identifiers.

### Changed

- **`genexus_search_source` repeat searches are much faster.** Source text is now cached per object (invalidated on every write), the scan probes that cache before touching the SDK, unanchored regex patterns run in a single pass instead of per-line, and compiled regexes are reused across calls instead of being re-JITed on every construction (~10–30ms of IL compilation on .NET Framework each). A repeat whole-KB search that previously re-read every candidate's source through the SDK now runs against cached text — the live probe shows a cold first call (~950ms of SDK reads) dropping to ~1ms on the very next identical call.
- **`genexus_whoami` is faster.** The four per-call disk reads (GeneXus version probe, KB-validity directory walk, crash ledger, update-check JSON) are now memoized with short TTLs — a stale-by-seconds answer is fine for a health probe. whoami p50 dropped from ~5.8ms to ~1.2ms in the live benchmark against the same harness.

- **Modern MCP Streamable HTTP.** The gateway now supports the sessionless `2026-07-28` transport alongside legacy sessions, including server discovery, per-request header validation, correct notification/status semantics, cache metadata, and structured tool results.
- **MCP bootstrap.** Discovery and static resources no longer require an open KB, and stdio notifications no longer receive spurious response lines.
- **`genexus_search_source` continuation.** Pages that end inside an object now return an opaque continuation cursor, so resuming does not duplicate or skip hits.


### Internal

- New pure helpers (unit-tested without a live KB): `Helpers/PersistenceVerifier.cs` (normalization-aware value comparison + the NotPersisted envelope + before/requested/persisted diff attach) and `Helpers/SpecificationDiagnostics.cs` (parses the BuildService status envelope — which mixes PascalCase CLR names with renamed computed getters — into `[{code, object, member, message}]`). `Services/SaveSpecifyOrchestrator.cs` runs the inline specify pass with a chained baseline wait (no busy-polling the STA worker) and best-effort snapshot restore. Golden `tools-list` fixture regenerated for the five schema additions; new tests: `PersistenceVerifierTests`, `SpecificationDiagnosticsTests`, `VariableAddPersistenceTests`, `SpecifyValidationRouterTests` (+44). Tool-schema token budget 16900 → 17700 (measured ~17572) for the `validationMode`/`rollbackOnFailure` params on `genexus_edit`, `genexus_variable`, `genexus_properties`, `genexus_structure`, `genexus_create`.
- issue #58: new `Services/WwpActionService.cs` (module `WwpAction`) resolves the WWP host via `PatternAnalysisService.ResolveWWPInstance`, reads the `PatternInstance` XML, applies pure XML patches (`ParseActionGroups`, `BuildUserActionElement`, `BuildAddAction`, `BuildUpdateAction`, `BuildMoveAction`, `BuildRemoveAction` — unit-tested without an SDK) and persists via the verified `WriteService.WriteObject(host, "PatternInstance", xml, dryRun)` path with `childrenOrderedList` reconciliation. `update_action` treats `position` as a reorder directive (never writes a literal attribute); `Persist()` keys off the canonical envelope `status` rather than substring matching, with a `WwpActionNoChange` code for no-ops. Procedure existence validated via `ObjectService.FindObject`. Golden `tools-list` fixture regenerated for the new tool; new `WwpActionServiceTests` (17); tool-schema token budget 18200 → 19000 (measured ~18938) for the new tool's schema.
- issue #61: `ReorgImpactService.Preview` walks `Transaction.Structure.Root` + sub-levels (root-level Table disambiguation mirrors `sql_ddl`), reads logical nullable per the issue #57 DSL convention (`Attribute.Properties.Get("Nullable")` Yes/Nullable) and physical nullable via `TableAttribute.IsNullableValue.True`, and delegates the diff to pure statics (`DiffColumns`, `TypeFamily`, `RenderColumnDef`, `RenderCreateTable`, `Warning`) — unit-tested with synthetic column objects. `genexus_db action=reorg_preview` routes `ReorgImpact/Preview`; `ToolSchemaSizeTests` budget unchanged (18200, measured re-checked). New `ReorgImpactPreviewTests` (24: family normalization incl. the `LONGVARCHAR`→character regression, issue #61 example before/after strings, destructive semantics, DDL rendering, no-KB guard).
- issue #62: new `Services/AtomicCreateService.cs` composes the existing SDK write primitives (`ObjectService.CreateObject` → `WriteService.AddVariables`/`WriteObject` → `PropertyService.SetProperty`) with pre-save field validation (`ValidateVariables` syntax gate + `ValidateKbReferences` KB-existence pre-flight with an injected resolver — the issue #56 failure mode), all-or-nothing compensation (delete fresh object / restore all touched parts from pre-write `EditSnapshotStore` snapshots, which the orchestrator captures for the Variables part that `AddVariables` doesn't snapshot itself), optimistic `version` fingerprinting (`ComputeVersion` = SHA-256 over Source/Rules/Variables), and issue #60 `SaveSpecifyOrchestrator` reuse for `validate=true`. Wired as `genexus_create action=object_atomic` (module `AtomicCreate`). Golden `tools-list` fixture regenerated for the `object_atomic` action + params; new `AtomicCreateServiceTests` (19, pure helpers with fake resolvers); tool-schema token budget 17700 → 18200 (measured ~18077) for the `object_atomic` params.
- Perf pass (worker + gateway): `SourceSearchService` reads cached raw part sources via `ObjectService.ReadPartSourceRaw`/`TryGetPartSourceRaw` (256KB size guard so huge WebForm XML isn't cached; guid-normalized probe key), the scan is cache-first per-part with a `resolutionFailed` bail, unanchored regexes take a single-pass `Matches` path gated by new conservative `HasLineAnchors`/`MayMatchAcrossLines` guards (anchored patterns and newline-capable atoms — `\s \D \W \cJ \p..`, negated classes, `(?is)`-style inline options — keep the exact per-line loop), and compiled regexes go through a bounded 16-entry cache. `Program.Whoami` memoizes `GetCachedGxVersion` (60s), `IsKbPathValid` (15s), `CrashLedger.Summarize` (10s); `UpdateNotifier.GetCachedStatusSync` gains a 10s in-memory layer over the disk read. New `SourceSearchPerfGuardTests` (19) pin the routing guards and the regex cache via reflection; full suites green (Worker 1802, Gateway 750 passed; 4 and 7 live/integration tests skipped). `scripts/bench-live-http.py` fixed: it passed `query` to `genexus_search_source`, which only accepts `pattern`/`callee` — the benchmark was measuring the `MissingCriteria` error path (~15ms flat) rather than the real search; the harness now passes `pattern` and measures the actual scan.
- Modern MCP transport, result-envelope, prompt, static-resource, notification, cursor, and structure-persistence regression coverage was added; the schema budget is now 19100 tokens for the opaque source-search continuation cursor.
- The MCP tool-schema budget is now 19500 tokens to cover the merged atomic-authoring compatibility aliases and native Domain-binding guidance; the resulting catalog measures about 19350 tokens with headroom for small additive fixes.
- Benchmark harness (`scripts/bench-live-http.py`) extended with three ops + a comparison mode: `edit_dryrun` (genexus_edit mode=full + `// gxbench-dryrun` marker on a small Transaction, dryRun-verified per candidate), `analyze` (mode=summary) and `lifecycle_status`, plus `--compare <baseline.json>` printing per-op p50/p95 deltas with a >+25% p50 regression warning (live demo: mean p50 −12.4% baseline→current, no regressions flagged). Findings wired into the harness: tracked ops without a client progress token are capped at the gateway's 50s sync wait (`McpRouter.SafeLongPollSecondsWithoutProgress`) and keep running in the STA worker, so heavy shapes (analyze mode=impact, patches on big WebForm sources) are not latency-measurable and poison every later call — the docstring documents restarting the gateway on uniform ~50s timeouts; omitting `type` on genexus_edit auto-injects `Table` (resolving to the table object, which exposes no Source part → `PatchReadFailed`), and identity find==replace patches short-circuit to `NoChange` — the op now passes the type explicitly and appends a marker line via mode=full. New `scripts/probe-bench-ops.py` diagnoses op shapes live (per-op status + elapsed, full error-envelope dump); a `WriteDryRun` envelope (`status: ok`) is a success, not an error.
- `AutoTypeInjector` root-cause fix: the name→type map is now fed from the FULL index via a new worker action `kb/GetNameTypeMap` (O(n) in-memory scan, STA-exempt alongside `GetIndexState`), fetched once per KB by `Program.Whoami` when the index reaches LiteReady/Enriching/Ready (fire-and-forget, `TryAdd` gate; UltraLiteReady deliberately excluded — the lite pass streams partial snapshots and would pin an incomplete map behind the gate). `ApplyFullNameTypeMap` rebuilds the per-KB map wholesale and collapses {Transaction, Table} → Transaction; `_shadowTypesNoInject` ("Table", "Attribute") refuses injection of shadow-only names, and `RefreshFromRecentlyChanged` now skips shadow entries so the recent-window feed can't flip a collapsed Transaction back to ambiguous when the table shadow surfaces (the poisoning path). Explicit caller-supplied types still short-circuit first. `Attribute` joined the set after a live 16-typeFilter probe found exactly two collision classes: Table+Transaction ×3 (already collapsed) and Domain+ExternalObject ×1 (`Geolocation` — genuine ambiguity between two real objects, already handled by the ambiguous→null rule; Domain is deliberately NOT shadow-listed), while the 8 `Attribute` objects (`GpBaseId`…) are physical artifacts whose Source read falls back to empty Documentation — a type-less `genexus_edit` on one of them auto-injected `type=Attribute` and resolved to the part-less artifact (same class as Table; `IndexCacheService` routes Attribute/Table targets identically). Live-verified: `genexus_edit` without `type` now injects `Transaction` (was `Table` → `PatchReadFailed`) and leaves `Attribute`/`Geolocation` type-less; gateway log confirms `applied full name→type map for 'live' (510 names)`. New `scripts/probe-rootmap-live.py` + `scripts/probe-type-shadows.py`; +6 `AutoTypeInjectorTests` (single-type inject, Transaction+Table → Transaction, Table-only no-inject, Attribute-only no-inject, ambiguous no-inject, wholesale rebuild clears stale) and +7 `FullNameTypeMapFetchTests` (canonical-envelope descent into `{status,code,result:{nameTypeMap}}`, flat payload, missing/null map, once-per-KB gate arm, release re-allows retry, stays armed after success) — 23/23 + 7/7 green; builds clean (worker + gateway).

## v2.37.0 — 2026-07-31

### Added

- **`genexus_structure action=update_group` — populate SubType Group members through the MCP.** A Group created with `genexus_create type=Group` used to come out as an empty shell: `set_attribute subtypeOf` correctly set each attribute's SuperType, but nothing attached those subtype attributes to the Group itself, so the Group's member list stayed empty and `genexus_analyze` / FK inference saw none of the subtype relationships. `update_group` now accepts `{ members: [{ name, subtypeOf }], remove?: [names] }` — each member registers the subtype attribute in the Group and asserts its SuperType link in one call, exactly like the IDE's Group editor — and `genexus_structure action=get_visual` on a Group reads the members back as `children: [{ name, subtypeOf }]` for a write-verify-read round trip.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`Nullable=Yes` on a Transaction attribute now actually persists.** `genexus_structure action=update_visual` with `nullable:"Yes"` crashed with a runtime binder error (`Cannot implicitly convert type 'int' to 'Artech.Genexus.Common.Parts.TableAttribute.IsNullableValue'`), and the JSON-boolean form (`nullable:true`) silently no-oped — either way the DDL kept generating `NOT NULL`. The value is now written as the SDK's typed `IsNullableValue`, the boolean forms are accepted, and the generated table DDL follows the value (verified end-to-end: the column loses `NOT NULL` with `Yes`, regains it with `No`).
- **`genexus_properties` on a Transaction attribute now applies `ALLOWNULL` / `Nullable` / `IsNullable`.** These names previously fell into the generic string setter, which cannot represent the enum — the call reported `PropertyApplied` while nothing changed. `control=<attribute>` now resolves the attribute occurrence from the Transaction's structure (previously only layout controls and variables were reachable), and those property names write the typed nullable value.
- **Domain-based procedure variables now verify their type reference after saving.** On some GeneXus 18 builds the SDK accepts a Domain-typed variable but drops the Domain reference when persisting — the variable saves with an empty `BasedOnReference` and fails specification with `spc0056` ("Variable definition is incorrect"). `genexus_add_variable` (single and batch) now re-reads the persisted variable list after saving and, when a Domain reference did not survive, fails with `VariableDomainReferenceNotPersisted` naming the affected variables instead of reporting a success that spec can never accept.
- **Domains created or updated with enum values no longer silently lose the value list.** `genexus_create type=Domain` and `genexus_structure action=set_domain` could persist a Domain whose enum values were never written: when two values shared a description (an empty one included), the SDK's `EnumValuesValidResolver` rejects the set and the property write silently no-ops — leaving the Domain with no combobox options in the IDE. Enum values now pass through verbatim (raw literals, the canonical stored form for every data family — the template's own `HttpMethod` enum stores `<Value>GET</Value>` unquoted), and a value without a description inherits its name, matching the IDE convention, so the write always survives. The old auto-quoting of character-family values has been removed — it never reached the XML and produced the same silent drop. Verified against a real KB: enums now appear in the stored version XML for both the create and update paths.

## v2.36.1 — 2026-07-27

Patch release: fixed router action dispatch for `genexus_analyze mode=linter` and ensured `FindObject` falls back to SDK lookup when search index misses newly created objects.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_analyze mode=linter` router action.** Previously routed to `action = "Analyze"` instead of `action = "linter"`, which fell through `AnalyzeService` without reaching `LinterService.Lint`. `AnalyzeRouter.cs` now emits `action = "linter"`.
- **`FindObject` SDK fallback when search index misses newly created objects.** `ObjectService.FindObject` previously skipped the SDK `Objects.GetByName` fallback whenever a search index was loaded in memory. If an object was created after index load, tools like `genexus_analyze`, `genexus_inspect`, and `genexus_read` returned `ObjectNotFound` until a full index rebuild ran. `FindObject` now falls through to the SDK fallback when the index lookup misses.

## v2.36.0 — 2026-07-27

Full-fidelity SDT structure & lifecycle rebuild target scoping: cloning and authoring a collection SDT now preserve the collection flag, the item level, Domain-based members and SDT references (#51, #52). `genexus_lifecycle action=rebuild` with a target now scopes execution to the requested object instead of triggering a full KB rebuild (#53).

### Added

- **Author an SDT's structure with `genexus_structure action=update_visual`.** Previously `update_visual` only accepted Transactions (an SDT returned `NotATransaction`) and `genexus_create type=SDT` could seed just one flat member, so a collection SDT with an item level and Domain-typed members could only be built in the GeneXus IDE. `update_visual` on an SDT now takes a structured payload — `{ isCollection, collectionItemName, children:[…] }` — where each child is a primitive member (`type` + `length`/`decimals`), a Domain-based member (`basedOnDomain:"<Domain>"`), an SDT reference (`type:"<OtherSdt>"`, optionally `isCollection:true`), or a nested level (`isLevel:true` with its own `children`). Members absent from the payload are removed, matching the Transaction path (#52).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Cloning a collection SDT via `genexus_create action=save_as` no longer flattens it.** The clone was rebuilt from the SDT's flat textual structure, which encodes neither the root collection flag, the collection item name, nor Domain/SDT-typed members — so a collection SDT was cloned as a flat, non-collection SDT with every Domain member collapsed to its base type. The SDT structure is now copied at the model level, so the clone preserves the collection flag and item name, each member's type/length/decimals, per-member collection flags, nested levels, Domain links (`basedOnDomain`), and SDT references (#51).
- **A Domain-based SDT member now reads back with its Domain.** `genexus_structure action=get_visual`, `genexus_inspect`, and `genexus_read` (part `SDTStructure`) reported a member based on a Domain only by its underlying base type, hiding the Domain link. Reads now include `basedOnDomain` with the Domain's name (#51).
- **`genexus_lifecycle action=rebuild` now honors `target` parameter.** Scopes execution to `<SpecifyOneOnly>` when `targets` are supplied instead of rebuilding the entire KB (#53).
- **User Control generation environment & post-build guardrail.** MSBuild child process now inherits `GX_PATH` and `GX_PROGRAM_DIR` so User Control catalog resolves properly. Post-build evidence check scans generated `.js` files for `gx.uc.getNew` without property bindings (`setProp`) and flags `[user-control-degraded]` warnings with `SucceededWithGaps` status (#53).

## v2.35.0 — 2026-07-24

### Added

- **Objects can now be placed into a Folder or Module.** `genexus_properties action=move name=<obj> destination=<Folder or Module>` moves an object into a KB Explorer container — the same operation as drag-and-drop / right-click → Move in the IDE (add `destKind=Folder|Module` only to disambiguate a shared name). The move is re-read afterwards to confirm it stuck, so a write that doesn't persist returns `MoveNotPersisted` instead of a false success. `dryRun=true` previews `from`/`to` without writing.

### Changed

- **`genexus_create` now honors a `folder` / `module` destination instead of rejecting it.** Passing `folder=<name>` or `module=<name>` creates the object in Root Module and then moves it into the target container (verified), reporting the outcome under `placement`. This replaces the previous `FolderPlacementUnsupported` rejection — the earlier "the SDK cannot place objects" conclusion was wrong (it came from decompiling a metadata-only reference assembly whose members are all empty stubs; the real move persists at runtime via the SDK). `list`/`inspect` reflect the new location immediately.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Typing a variable as an SDT *item* now works — `&Message : Messages.Message` no longer collapses to the collection.** Declaring a variable as a single element of a collection SDT (the dotted `SDT.Item` form, e.g. GeneXusCommon's `Messages.Message`) persisted as the whole `Messages` collection instead, so `&Messages.Add(&Message)` was impossible and callers fell back to ad-hoc `VarChar` collections. `genexus_variable action=add` and `action=modify` now resolve the dotted item form through the SDK's own type-picker resolver — the same path the GeneXus IDE uses — so the variable is typed as the item. Verified end-to-end: `&Message.Id` / `.Type` / `.Description` member access and `&Messages.Add(&Message)` compile. Plain SDT, Business Component, and Domain types are unaffected.

### Internal

- `VariableInjector.TryBindSdtItemType` (thin wrapper over `TryBindGenexusDataType` → `DataTypeProvider.GetTypeByName`) is attempted before the strip-to-parent `ResolveTypeObject` bind for dotted names, in both `WriteService.BuildResolvedVariableInto` (add/batch) and `ModifyVariableInternal`. The DSL path (`SetVariablesFromText`) already tried the type-picker resolver first, so it was unaffected. Added a `Messages.Message` resolver case; live-verified over HTTP against a real KB (AcademicoHomolog1) with a hot-swapped worker.
- Object placement (move): new `ObjectMover` helper persists the parent via reflection on `Artech.Udm.Framework.EntityManager.SaveWithParent(entity, parentEntity, prefs)` (fallbacks: `UpdateParent`, `KBObject.Save`); `ObjectService.MoveObject` resolves the Folder/Module container, moves, re-reads `Parent` from a fresh `Objects.Get`, and reports `MoveNotPersisted` on mismatch. `PropertyService` placement-property writes and `genexus_create` folder/module now route here. `IndexCacheService.InvalidateHierarchy(guid)` drops the per-Guid hierarchy cache + old-parent `ChildrenByParent` slot before `UpdateEntry` so `list`/`inspect` re-file the moved object. Tool-schema token budget 16600 → 16700. Golden `tools-list` fixture regenerated. Live-verified over HTTP against a real KB (AcademicoHomolog1): folder move (Folder id-265 written to the object's `Folder` property, survives a KB reopen) and module move, both via `EntityManager.SaveWithParent`.

## v2.34.0 — 2026-07-24

Correctness fixes for reading and writing SDT / Data Provider objects, plus an explicit failure for folder/module placement.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Reading a collection SDT no longer flattens it.** `genexus_inspect` and `genexus_structure action=get_visual` reported a top-level collection SDT as `isCollection: false` with a flat field list, because the collection flag lives on the structure's root level, not on the SDT object. Both now report `isCollection: true` and the collection item name (e.g. `"DASDTCursosAlunoItem"`), and `get_visual` now also carries each field's length/decimals — matching what the IDE and `genexus_read part=Structure` show.
- **SDT members typed as another SDT now read as that SDT's name.** A member referencing another SDT came back as the opaque `"GX_SDT"` token; it now includes `referencedType` with the referenced object's name (e.g. `"CobrancaEndpointServiceconvenioDto"`).
- **Setting a Data Provider's `OutputSDT` now persists.** `genexus_properties action=set propertyName=OutputSDT value=<SDT name>` reported success but wrote an empty value, because `OutputSDT` is a read-only derived string and the writable output is a typed reference. The MCP now resolves the SDT name and applies it through the SDK's typed Data Provider output API. Passing an empty value clears the output; a non-existent SDT name is rejected with `OutputSdtNotFound` instead of silently emptying the property.

### Changed

- **Renaming an object via `genexus_properties` is rejected instead of half-applied.** Setting the `Name` property re-keyed the object in memory but left the index stale (the object became unreachable under its new name) and never updated references. It now returns `RenameNotViaProperties` pointing to `genexus_refactor action=RenameObject`, which renames the object, patches its call-sites, and refreshes the index.
- **`genexus_create` rejects a folder/module destination instead of ignoring it.** `genexus_create action=object` accepts optional `folder` / `module` / `parentPath` and, because the GeneXus 18 SDK exposes no API to place an object into a folder/module, returns `FolderPlacementUnsupported` up front rather than silently creating the object in Root Module. Create the object without a destination, then move it in the GeneXus IDE.

### Internal

- New `SdtMemberResolver` helper (shared SDT-reference name resolution); `PropertyService.SetDataProviderOutputSdt` routes through `Artech.Genexus.Common.Properties+DPRV.SetOutput(IPropertyBag, DataProviderOutputReference)`. Tool-schema token budget 16400 → 16600 for the new `genexus_create` folder/module/parentPath args. Added `Issue47To50SdtAndPlacementTests` and a router-forwarding test; golden `tools-list` fixture regenerated. All fixes live-verified over HTTP against a real KB (AcademicoHomolog1).

## v2.33.1 — 2026-07-23

Hardening and correctness fixes for the **Nexus IDE VS Code extension**. (The `genexus-mcp` server is unchanged from v2.33.0 — this is a lockstep version bump whose substance is the extension.)

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Webviews no longer execute markup smuggled through Knowledge Base content.** The Structure, Index, and History views built their HTML by concatenating KB-derived values (object / attribute / index names, descriptions, formulas, revision authors and comments) without escaping, under a policy that allowed inline handlers — so a crafted name or comment could run script inside the view and reach the extension host. Every KB-derived value is now HTML-escaped before display (matching the Properties view, which already constructed its DOM safely).
- **Virtual-file paths can no longer escape the KB mirror folder.** The object type/name segments taken from `gxkb18:` URIs are now validated, and every mirror file path is confined to the shadow root — closing a path-traversal gap on both the read and the write paths (the on-disk `file:` path already enforced this).
- **`&variable.` member completion now reflects edits made during the session.** An object's variable list was cached on first use and never refreshed, so a newly added, renamed, or retyped variable didn't appear (or showed members for the old type) until the window was reloaded. The cache now expires after 30 seconds, matching the hover cache.
- **Renaming with unsaved edits open no longer silently misses them.** A rename runs against the *saved* Knowledge Base; if the document has unsaved changes the extension now prompts to save first (or cancel) instead of reporting success while unsaved occurrences are left un-renamed.
- **AI inline completion now aborts its network request** when you keep typing or it times out, instead of leaving the request running server-side and the "N ops" status-bar indicator stuck.
- **Smaller correctness fixes:** a completion path that could throw on a malformed variable payload is now guarded, and "Find All References" honors a caller's request to exclude the declaration.

### Internal

- Nexus IDE cold-audit plans 062–067 (`plans/`) — the first independent audit of the extension code added in 051–061. Each executed in an isolated worktree, advisor-reviewed (scope + diff + tests), and cherry-picked to `main`. New `utils/htmlEscape.ts` + `GxUriParser.resolveWithinRoot`/segment validation; `getObjectVariables` TTL via a per-cache `WeakMap`; `AbortSignal` threaded through `GxGatewayClient.callMcpTool → callMcp → initializeMcpSession → postRawJsonRpc`. Extension test suite grew 76 → 100 (`@vscode/test-electron`, runs locally/self-hosted).

## v2.33.0 — 2026-07-23

The **Nexus IDE VS Code extension** is brought up to the MCP server's quality bar and now ships with every release. (The `genexus-mcp` server itself is unchanged from v2.32.0 — this is a lockstep version bump whose substance is the extension.)

### Added

- **The Nexus IDE extension now ships as a `.vsix` attached to each GitHub Release.** `release.ps1` versions the extension in lockstep with the server, builds it, and attaches `nexus-ide-<version>.vsix` next to `publish.zip`. (Marketplace `vsce publish` stays a manual step — it needs a token this repo doesn't store.)

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Rename now actually updates the editor.** Renaming a variable/attribute ran server-side but the editor showed nothing (it returned an empty edit); it now refreshes the affected open documents (skipping ones with unsaved changes). Also fixed variable renames being mis-routed to the attribute-rename operation.
- **Find References / Go to Definition return real locations** instead of collapsing every hit to the top of the object; variable references within a document now resolve.
- **Code actions come from real linter diagnostics** (e.g. "Remove unused variable" on an unused-variable warning) instead of a blanket "Create Variable" offered on any `&word`.
- **Inline completion is context-aware** — real member suggestions for `&var.` sourced from the KB, plus optional AI completion (opt-in via `genexus.inlineCompletion.ai`) — replacing the previous hardcoded ghost text.
- **Live KB→editor sync is active** (the sync listener was built but never wired in), and the mis-labeled "Explain Code with AI" action now matches what it actually does.
- **Webviews are hardened.** The diagram view bundles mermaid locally (no CDN) under a strict Content-Security-Policy; the layout preview is honestly labeled read-only and renders the SDK's HTML inside a sandboxed iframe.
- **Silently-swallowed failures now surface** in a structured, level-gated log (`genexus.logLevel`) that replaces scattered console output.
- **Packaged-install backend resolution is deterministic** — the extension resolves its backend from the packaged location instead of guessing dev-tree paths.

### Internal

- Nexus IDE elevation plans 051–061 (`plans/`, design in `docs/nexus-ide-roadmap.md`, gap map in `docs/nexus-ide-recon.md`). Each executed in an isolated worktree, reviewed, and merged. Extension test suite grew 9 → 76 (`@vscode/test-electron`, runs locally/self-hosted — VS Code + GeneXus SDK can't run on GitHub-hosted CI). One transient extension-host test flake observed (1 failure in 5 runs, not reproducible across 4 subsequent runs) — known minor, not a logic error. New shared `gxMemberResolver` + `Logger` + `GxVariableToken` helpers; `SyncManager` wired; `release.ps1` bumps `src/nexus-ide/package.json` and builds/attaches the VSIX. Deferred follow-ups tracked in the roadmap doc.

## v2.32.0 — 2026-07-23

Agent-ergonomics round: louder argument validation, richer list metadata, content-first defaults, and a batch of correctness fixes.

### Added

- **List responses now carry `empty`, `returned`, and `totalByType` for far more tools.** These signals — which let an agent tell "0 results" from a silent failure and paginate without guessing — previously only appeared on `genexus_query` / `genexus_list_objects`. They now also attach to tools whose collection uses another key or is nested one level down (e.g. `genexus_api` endpoints, `genexus_versioning` history, gxserver pending/ignored/conflicts).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **A second build started in the split-second after the first no longer slips past the "build already running" guard.** The guard read an in-flight set that a build only joined on a background thread, so two builds fired back-to-back could both be admitted and race the generated output. Builds now register synchronously before the background work is scheduled.
- **`next_legal_actions` follow-up suggestions work again for object/popup/save-as creation.** After the tool consolidation the suggestion builder was still keyed on pre-consolidation tool names, so a `genexus_create` call produced no follow-up hints; it now dispatches on the canonical tool + `action`, and every suggestion points at a current tool name (so it holds even with legacy aliases disabled).
- **`genexus_variable action=modify` rollback now restores a non-primitive type.** When a retype failed and the variable was rolled back, an original SDT / Business Component / built-in GeneXus data-type binding (e.g. `HttpClient`, `WebSession`) was silently downgraded to a bare scalar while the tool reported "the original variable was restored." The rollback now re-establishes the original binding, and the message flags when it couldn't.
- **Per-tool help is reachable by the current tool names.** `resources/read genexus://kb/tool-help/<tool>` returned nothing for `genexus_create` / `genexus_db` because those entries were still keyed by their pre-consolidation names; help now resolves by canonical name, and a legacy name still resolves via alias fallback.

### Changed

- **Mistyped arguments and enum values now fail loud with a "did you mean" suggestion.** Passing an unknown argument that's a keystroke or two from a real one (e.g. `nam` for `name`), or an out-of-range enum value (e.g. `mode: "patche"` when the choices are `full` / `patch` / `ops`), is now rejected up front — the closest valid name is returned in the error message and in a structured `suggestion` field — instead of the value being silently ignored. Genuinely unrecognized pass-through arguments and cross-cutting options (`axiCompact`, `projection`, `fields`, `kb`, …) are never flagged, so this catches slips without breaking working calls.
- **`genexus_api` with no `action` now lists the KB's exposed APIs** instead of returning an "action is required" error, so a bare call shows live data — matching the other umbrella tools.

### Internal

- `GatewayArgsValidator.Violation` gains a `Suggestion`: DidYouMean over enum values, and — for tools without `additionalProperties: false` — over declared property names (edit distance ≤ 2, gated by a `CrossCuttingArgs` allowlist). `Program.RequestLoop`'s `InvalidArgs` envelope surfaces `suggestion` per violation. `ApiIntrospectService` defaults a missing `action` to the read-only `list`.
- `NextLegalActionsBuilder` gains a canonical `genexus_create` case dispatching by `action` (object/popup/save_as); emitted suggestions repointed to canonical names (`genexus_versioning action=undo`, `genexus_telemetry action=logs`), the nonexistent `genexus_playbook` suggestion dropped.
- `Program.ToolPayload.NormalizeToolPayloadForAxi` resolves the collection via a `matchedKey`/`collectionHost` lookup that broadens the recognized key set and descends one level into the canonical `result` object; aggregates are written at top level; field projection stays scoped to search tools.
- `BuildService` registers `_inFlightBuilds` synchronously in `Build()`; the `referencedButNotBuilt` evidence check uses a precomputed `HashSet` instead of a nested `checkList.Any` scan.
- `WriteService.Variables` captures the original type name before removal (via the public `GetVariablesAsText` + a new `ExtractOriginalTypeNameFromDump` helper) and replays the SDT/BC/built-in bind in the rollback branch.
- `ToolHelpCatalog.Get` resolves legacy names through `McpRouter.TryRewriteLegacyTool`; two entries re-keyed to canonical umbrella names.
- New non-wired `ToolIdentity` prototype + design doc (`docs/tool-identity-registry.md`) for a single canonical tool-name source of truth (advisor plan 046 — spike; not yet wired into the catalogs).
- Tests: full suites green — Gateway 697 passed / 7 skipped, Worker 1578 passed / 4 skipped. New tests across `GatewayArgsValidatorTests`, `NextLegalActionsBuilderTests`, `GatewayBudgetTests`, `BuildServiceTests`, `Issue33WebSessionAndSdtCollectionTests`, `McpRouterTests`, `ToolIdentityTests`. Advisor plans + this work: `plans/040`–`046`.

## v2.31.1 — 2026-07-23

Variable-retype reporting honesty (issue #46 follow-up).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_variable action=modify` now reports the type it actually persisted.** Retyping a variable to a built-in GeneXus data type (e.g. `Properties`), an SDT, a Business Component, or a Domain reported `persistedType: "DomainReference"` — an internal placeholder, not a real type — even though the variable was correctly persisted. It now reports the real type name (`"Properties"`, the SDT/BC/Domain name). Declaring `Properties` (and every other built-in data type) already worked as of v2.31.0; this only corrects the confusing success message.
- **`genexus_variable action=modify` no longer silently falls back to `NUMERIC(4)` when a type can't be resolved.** If a requested type matched no Domain, SDT, Business Component, or built-in GeneXus data type, modify used to leave the variable at its default `NUMERIC(4)` and still report success. It now fails loudly, rolls the variable back to its original type, and leaves it unchanged.

### Internal

- issue #46: `ModifyVariableInternal` tracks the resolved bind name (`boundTypeName`) across the SDT/BC/Domain/`TryBindGenexusDataType`/`TryBindBuiltinUserDefinedType` branches, uses it for `requestedType`/`persistedType`/`details` on a non-primitive retype, and throws (triggering the existing best-effort rollback) when every bind path misses instead of persisting a default-typed variable. New resolver case `Properties` in `Issue33WebSessionAndSdtCollectionTests`; functional round-trip (DSL + modify persist `Properties`, spec 0 errors) verified live on AcademicoHomolog1 (GX 18.0.7).

## v2.31.0 — 2026-07-23

GeneXus Server "Ignored Objects" visibility, plus full variable-type authoring (issue #45).

### Added

- **`genexus_gxserver action=ignored` — list the objects Team Development leaves out of a commit.** Surfaces the two "Ignored Objects" sets the IDE shows: `commitIgnored` (locally-changed objects you excluded from commit — each with name, type, operation, last change, user) and `updateIgnored` (objects excluded when receiving server updates). Read-only; returns `{connected:false}` on a KB not linked to a GeneXus Server.

### Changed

- **`genexus_gxserver action=pending` now flags which objects will actually commit.** The pending changelist mixed committable objects with ignored ones and labelled them all "pending". Each object now carries `ignoredForCommit` (true = the object sits in the IDE's "Ignored Objects" tab and a full commit skips it), and the response adds `committableCount` / `ignoredCount`.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **You can now declare a variable of a built-in GeneXus data type — `HttpClient`, `HttpRequest`, `HttpResponse`, `WebSession`, `MailMessage`, `ExcelDocument`, and the rest — through the MCP.** Previously `genexus_variable add typeName=HttpClient` failed with `UnknownType`, `modify` silently persisted a dangling reference, and the `genexus_edit part=Variables` DSL silently coerced the variable to `NUMERIC(4)` — so any object that calls out over HTTP (`&http.Host`, `&http.Execute(…)`) could not be authored without opening the GeneXus IDE by hand. All three paths (`add`, `modify`, and the `mode=full` Variables DSL) now resolve the type through GeneXus's own type registry, exactly as the IDE's variable Type picker does, and the variable round-trips (reads back by name) and passes specification with member access resolved. Only `WebSession` was previously special-cased; every built-in GeneXus data type — and user-defined KB External Objects — is now recognized generically.
- **A variable declared as a collection through the `genexus_edit part=Variables` DSL is now a real collection.** Writing `&items : Numeric(4) Collection` used to persist the `Collection` keyword but leave the variable a scalar, so a later `&items.Count` failed at specification as an unknown function — only `genexus_variable add collection=true` produced a working collection. Setting the type was clearing the collection flag; the DSL now applies the flag after the type, so `.Count` and other collection semantics work.
- **Auto-declared variables no longer include ampersands that live inside string literals or comments.** Saving a Source/Events part with, say, a URL query (`"…&status=paid"`), an HTML entity (`"&nbsp;"`), or a commented-out line used to auto-declare spurious `VARCHAR(100)` variables (`&status`, `&nbsp`, …) from those ampersands. The auto-declare scanner now ignores ampersands inside quoted strings (honoring GeneXus's doubled-quote escaping) and comments, so only real variable references are declared.
- **`genexus_create action=save_as` no longer stops before cloning the important parts.** A part that doesn't apply to the object being cloned (e.g. a `Layout` part reported for a Procedure) used to fail mid-clone and abort the whole operation — so a source object's Variables (carrying, say, a working `HttpClient` variable) were never copied. Save-as now skips a part it can't clone, keeps going, and reports the skipped parts under `created.partsSkipped`; it only fails outright when nothing could be cloned.

### Internal

- Commit-ignore state is read from the object's `ModelEntityOutput` of type **505** in the design model — the marker the IDE writes for "Add to 'Ignored Objects'" (reverse-engineered on GeneXus 18.0.7: mitmproxy capture proved the toggle makes no server call, then a metadata-DB before/after diff isolated the 505 row; the 505 set was verified to equal the IDE's Ignored-Objects tab exactly). Read via the inherited `Artech.Udm.Framework.Model.LoadLastEntityOutput(key, 505, …)` (the high-level `UI.Framework ITeamDevClientService.GetIgnoredForCommit()` does not resolve in the headless worker). `GxServerSyncService` gains the `ignored` action + `IgnoredEnvelope` fallback + `IsCommitIgnored`/`LocalChangeType`/`EnumIgnoredForUpdate` helpers. tool_definitions.json + discovery golden fixture regenerated. New worker tests: `Ignored_NoMetadata_ReturnsConnectedFalse`, `Ignored_WithDotGxState_ReturnsEmptyIgnoredArrays`, `Run_IgnoredAction_IsAcceptedNotBadAction`. `ignoredForCommit` verified live over HTTP against AcademicoHomolog1's real changelist.
- issue #45: `VariableInjector.TryBindGenexusDataType` resolves a type name via `DataTypeProvider.GetProvider(model).GetTypeByName(name, model)` and applies the returned `AttCustomType` (setting `Variable.Type` from its category, `ATTCUSTOMTYPE`, and `DataTypeString`), replacing the hardcoded `WebSession=31` subtype map that covered only 1 of ~137 built-ins. Wired into `BuildResolvedVariableInto` (add), `ModifyVariableInternal` (modify), and `SetVariablesFromText` (DSL) ahead of the legacy `TryBindBuiltinUserDefinedType` fallback; read-side `ResolveTypeRepresentation` now honors `DataTypeString` for `GX_EXTERNAL_OBJECT` too.
- issue #45 follow-ups: `SetVariablesFromText` now assigns `IsCollection` after the type-application block (the SDK resets the flag when `Variable.Type` is set). `VariableInjector.StripLiteralsAndComments` blanks string-literal and comment content (length/newline preserving, doubled-quote aware) before the `&`-token scan in `InjectVariables`. `SaveAsService.SaveAs` continues past a per-part clone failure, accumulating `created.partsSkipped`, and only returns `PartialFailure` when zero parts cloned; `SaveAsServiceTests` updated (non-fatal skip + total-failure cases).
- Live-verified on AcademicoHomolog1 (GX 18.0.7): full type matrix (primitives, `Url` domain, `Parametros` SDT, `HttpClient`/`Location`/`Geolocation`/`WebSession`/`HttpRequest`/`MailMessage` built-ins, `Arquivos` KB External Object, numeric collection) round-trips; Source using `&http.Host`/`.Execute` and `&numColl.Count` specifies with 0 errors; ampersands in string literals no longer auto-declare vars. 9 new resolver/masker tests in `Issue33WebSessionAndSdtCollectionTests` plus updated `SaveAsServiceTests`. (SDT-collection member access at the source level, e.g. `&sdtColl.Count`, is a separate SDK semantic unrelated to these fixes.)

## v2.30.1 — 2026-07-23

Data-loss and reliability fixes (issues #43 and #44).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **The MCP connection no longer drops during/after a build.** A background build kept emitting progress (`Build phase: OpeningKB`, …) tagged with a token whose operation had already finished — so the client (Cursor especially) saw a "progress notification for an unknown token", flagged the transport as errored, and closed the connection (looking like "the server crashed" when it was still running). The gateway now relays a progress notification only while its operation is still active and silently drops stale/unknown ones, so async builds and background indexing can't tear down the session. Live build progress is unaffected — follow it with `genexus_lifecycle action=status target=op:<id>`.
- **`genexus_edit` with `operation: Append` or `Insert_After` no longer overwrites the whole part.** Passing one of these operations without `mode: patch` used to fall through to a full-part replace that silently discarded the operation — so an append/insert against an ~888-line Source destroyed everything but the payload, while still reporting `WriteApplied`. An explicit `operation` now always routes through the patch pipeline (which genuinely appends/inserts and preserves the rest of the part); combining `operation` with `mode: full`/`ops` is now a clear usage error instead of a destructive write.
- **The pre-write `.bak` snapshot now captures the full original part.** The safety-net backup taken before every write was read through the paginated MCP path (~200 lines / 16 KB cap), so for a large part it saved only the head and could not restore the object. The snapshot now reads the complete part, so the backup is usable for recovery.
- **`genexus_lifecycle action=snapshots-list` now surfaces the pre-write edit backups.** It previously listed only WorkWithPlus pattern snapshots, so the `.bak` that a destructive edit had just created showed `count: 0` and there was no tool-driven way to find or restore it. Edit snapshots for the object (all parts) now appear alongside pattern snapshots, each tagged `kind` (`edit`/`pattern`) with its part and timestamp.
- **A failed or slow `genexus_worker_reload` no longer bricks the whole server with `Master error: BadRequest`.** When a reload made the internal stdio→gateway proxy time out and lose its session, every subsequent call — `whoami` included — returned HTTP 400 until a full client restart. The proxy now re-establishes the session (the same recovery it already did for an expired session / 404) and retries, so the server recovers on its own.
- **`genexus_variable action=add` accepts a nested SDT item type.** A `typeName` like `SdtCandUNIEDU.SdtCandUNIEDUItem` — the exact dotted form the reader emits for a variable bound to a collection SDT's item — failed with `UnknownType`, even though existing variables in the object used it. The type resolver now splits the dotted form and binds to the parent SDT.

### Internal

- issue #44: `OperationTracker.IsProgressTokenActive(operationId)` added; `Program.HandleWorkerResponse` gates `notifications/progress` relay (stdio + HTTP/SSE sessions, the single fan-out point) on it, dropping frames for terminal/unknown tokens. This also neutralizes the shared bulk-index literal token and any post-`Accepted` async-build emission at the client boundary without touching the worker emitters (worker-side token re-scoping / cancel-token threading remain as internal follow-ups — the client is protected regardless). 2 new `OperationTrackerTests`.
- issue #43 #1: routing guard added in `ObjectRouter` for `genexus_edit` (`operation` ⇒ patch semantics; conflict with `mode=full`/`ops` throws `UsageException`). issue #43 #2: `WriteService.TryCapturePreWriteSnapshot` now reads with `limit=0` (explicit full-read opt-out). issue #43 #3: `EditSnapshotStore.ListForGuid` added (parses part/timestamp from the filename, orders by timestamp), merged into `KbValidationService.ListPatternSnapshots`. issue #43 #6: `Program.RunMcpProxyAsync` recovers from a session-missing 400 and persists the initialize line across proxy re-entry (`_proxyCachedInitializeLine`). issue #43 #7: `VariableInjector.ResolveTypeObject` gained a dotted-name fallback. New tests: `Issue43EditOperationRoutingTests` (8) and `EditSnapshotStoreTests.ListForGuid*` (2). Worker 1564 + Gateway 673 tests green. Not live-smoked against a KB — the SDT-item bind (item-vs-collection nuance) and the proxy 400-recovery were reasoned/unit-covered, not verified end-to-end.
- issue #43 #4/#5 and the credential half of #6 were investigated and left unshipped as documented walls (see the issue thread): a GXserver `update` is an in-process, COM/STA-bound SDK call that cannot be aborted mid-flight, remote server-side revision history is not reachable from the headless worker (per-object server round-trips hang), and a running gateway's environment (hence Team Dev credentials) is frozen for its lifetime under the single-instance-per-port lease. The reclaim path for a wedged update (`genexus_worker_reload mode=hard force=true`) is now reliable because of the 400-recovery fix above.

## v2.30.0 — 2026-07-22

Build-reliability pass (issue #42). A GeneXus build could report `Succeeded` with 0 errors while the generated `.cs` never reached the environment's `web\` output — so an agent moved on believing its edit was compiled when it wasn't. Builds now carry evidence of what was actually generated, refuse to run two at once, and can't sit wedged as `Running` forever.

### Added

- **Build results now prove the generated code was emitted.** After a successful build of a code-emitting action, the result carries a `generateEvidence` block — `{ ok, objectsChecked, objectsBuilt, filesWritten[], staleOrMissing[], upToDate[], unreachable[] }` — verifying each target actually has a *fresh* generated source file on disk. When the build reports `Succeeded` but a target you edited has no fresh `.cs`, the result is stamped `effective_status: "SucceededWithGaps"` with a `hint` naming the missing targets, so `Status: "Succeeded"` alone can no longer be mistaken for "my edit compiled". The output-directory search now also finds environment layouts like `NETCoreMySQL\web\` (not just the classic `CSharpModel\Web`).
- **The evidence gate no longer cries wolf on unchanged objects.** GeneXus generation is incremental — building an object you did *not* edit leaves its `.cs` untouched (old timestamp). That is correct, not a gap: such objects are now reported under `upToDate[]` and the build stays a clean success. Only an object you edited this session that has no fresh `.cs`, or a target with no generated `.cs` at all, counts as a gap. Freshness is judged by whether the file changed across the build (a pre-build timestamp snapshot), which is immune to clock/filesystem-timestamp skew.
- **An unreachable object is reported as such, not as a false gap.** A Procedure with no callers that isn't a main/entry object is not code-generated by GeneXus by design (the specifier emits `spc0217`). Such a target now appears under `unreachable[]` with a note explaining why — instead of being flagged as a missing-`.cs` gap with a "rebuild with a full deploy" hint that would never emit the file. The build stays a clean success (`ok: true`), and the gap gate is reserved for objects that genuinely should have regenerated.
- **`staleGenerated` in the lifecycle status.** `genexus_lifecycle action=status` lists objects you edited via the MCP this session that have not been successfully rebuilt since, so you can see stale generated code without a separate call.
- **`referencedButNotBuilt` when callees were skipped.** A build with `includeCallees: none` whose target calls other objects now reports which referenced objects were not (re)generated, pointing you at `includeCallees: direct|transitive` to include them.

### Changed

- **A second build on the same KB is refused while one is running.** Builds serialize per worker; firing another now returns `status: "BuildAlreadyRunning"` naming the in-flight task (poll or cancel it) instead of silently queuing behind it. Opt out with `GXMCP_ALLOW_CONCURRENT_BUILDS=1`.
- **A wedged build no longer sits `Running` for the full timeout.** A build that stops making observable progress (phase and error/warning counts frozen) is force-failed after `GXMCP_BUILD_NOPROGRESS_SEC` (default 180s; `0` disables), giving you a terminal result you can act on well before the wall-clock cap.
- **Long background builds keep the worker alive.** The worker emits a periodic build-active heartbeat so the gateway's idle-reap / heap-recycle timer can't kill the worker mid-build.

### Internal

- Active builds are now tracked by an explicit in-flight set maintained across `RunBuild`, not by scanning task-status labels — an orphaned/crashed `Running` label can no longer wedge every future build. The in-flight set and the concurrent-build reject are keyed by KB path, so a build on one KB never rejects a build on another under a future shared-worker / warm-spares mode (a null filter still matches all for the lifecycle-status view). `GeneratedDiffService` gained `BuildCandidateRoots` / `DiscoverEnvironmentWebDirs` / `ProbeGeneratedFreshness` (now taking an optional pre-build mtime snapshot) and a two-arg `FindGeneratedFiles(kbPath, target, allRoots)`; the recursive scan skips VCS/backup/build dirs (`.git`, `.gx`, `GXcvt`, `obj`, …) and omits the full-KB fallback when an environment web dir exists. `EditDirtyTracker.GetDirty` snapshots the explicit dirty set, which the evidence gate cross-references to separate `upToDate` from real gaps. `AttachGenerateEvidence` parses the build log for `spc0217` (via `ParseUnreachableFromLog`, best-effort, capped read) to route unreachable targets to `unreachable[]` instead of the gap gate. New worker tests cover env-web-dir discovery, freshness fresh/stale/missing, pre-build-snapshot freshness, scan pruning, full-tree-fallback omission, dirty snapshot, per-KB reject scoping, concurrent-build reject, and the up-to-date/dirty-gap/missing-gap/unreachable evidence classifications; gateway tests cover `generateEvidence`/`effective_status`/`staleGenerated` passthrough. Discovery golden fixture regenerated for the `genexus_lifecycle` description. Worker 1562 + Gateway 663 tests green. All four evidence classifications plus the reporter's exact edit-Source→build→diff-`.cs` scenario were verified end-to-end against a real GeneXus 18 build (live-smoked over the HTTP endpoint), not only unit-tested.

## v2.29.4 — 2026-07-21

Bug-fix pass — five agent-friction fixes across dry-run, DB drift, targeted build, worker concurrency, and preview. No new features.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_edit` dry-run no longer promises a Transaction attribute removal the SDK will reject.** Removing a key attribute from a transaction always fails at save, but a `dryRun` / `validate=only` run reported the edit applied (`opsApplied:1`) because it only projected the change against the Structure text in memory. Dry-run now flags removals the SDK will refuse up front (`capabilityRisks`, `willLikelyFail`), and every dry-run carries a `dryRunCaveat` making clear the preview is a projection, not a guarantee the persist will succeed.
- **`genexus_db action=drift_check` is fast again.** It unconditionally ran the build-heavy database-impact specification (minutes on a real Knowledge Base), holding the worker's single SDK thread for the whole run. Drift check now uses the cheap timestamp heuristic by default; the specification pass is opt-in with `deep=true`.
- **A targeted build that matches no object fails loud instead of reporting success.** `genexus_lifecycle action=build` for a name that didn't resolve to a KB object built nothing, left the `.dll` untouched, and still reported "succeeded" — so a pattern-generated panel whose object name differs from the name passed (WorkWith panels, etc.) looked built when nothing happened. The build now returns a clear error naming the unresolved target(s), and warns when only some targets of a multi-target build are skipped.
- **A clear "worker busy" reply instead of a misleading build timeout.** The worker runs SDK operations one at a time, so firing a second operation while a long one was still running made the gateway wait and then report "Gateway timeout starting build" after 60s. The worker now answers immediately with a `WorkerBusy` message naming the in-flight operation and how long it has been running, so you can wait, poll status, or cancel it. Cancel / reload / health commands are never blocked. Tunable via `GXMCP_BUSY_REJECT_MS` (milliseconds; `0` disables).
- **`genexus_preview` and `genexus_run_object` explain when a page may be stale.** Both responses now include a `deploymentNote`: the URL is served by the IIS virtual directory, which reflects the last full deploy — a fast-path build compiles the object but does not publish the `.aspx` there. If the page looks out of date, build with `deploy=true` (or `action=rebuild`), or publish from the GeneXus IDE.

### Internal

- New unit test `SemanticOpsServiceTests.IsKeyAttributeInDsl` covers the dry-run key-attribute capability check. Worker 1529 + Gateway 660 tests green (+6 new); solution builds clean. End-to-end live smoke against the running KB was deferred due to dev-environment instability (reload/stdio reconnect cycling and slow large-KB warmup), not a code issue.

## v2.29.3 — 2026-07-21

Two more performance + bug-fix passes (no new features). Fixes span the analysis/edit hot paths, destructive-action safety, background-job and cache memory hygiene, and several latent concurrency and parsing bugs.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_gam action=define_api|deploy` now require `confirm=true`.** These call the GeneXus GAM Define API / security-table deploy and can create or alter tables in the KB's datastore; they previously executed on the first call with no confirmation. They now fail fast asking for `confirm=true`, matching every other destructive SDK action.
- **`genexus_gxserver action=pipeline_run|pipeline_abort` report real failures instead of "not connected".** A network or auth error while triggering or cancelling a CI build was reported as a benign `connected:false` success, so an agent might retry and double-trigger the build. A genuine trigger/cancel failure now returns a clear error; only an actually not-linked KB reports `connected:false`.
- **KB-wide rename is now atomic.** `genexus_refactor` rename patched every caller's source and saved them one by one, then renamed the target last — with no rollback. If that final rename failed, callers were left referencing a name that no longer existed. The whole rename now runs in a single transaction that rolls back every caller edit if any step fails.
- **A cancelled build/edit job stays cancelled.** Buffered worker output draining a moment after `genexus_lifecycle action=cancel` could flip the job's status back to succeeded/failed. Status transitions are now guarded so a cancel can't be clobbered.
- **`genexus_worker_reload mode=hard` can no longer return a stale pre-swap binary.** A tool call arriving on the same KB during the binary swap could spin up a worker on the *old* binary and have it reported as the reloaded one. The reload now holds the slot for the whole swap so concurrent calls wait for the new binary.
- **Call-graph and source search handle GeneXus string literals correctly.** The source tokenizer treated backslash as a C-style escape, so a string ending in a Windows path (e.g. `"C:\Temp\"`) desynced parsing and produced wrong call-graph and search results. It now follows GeneXus grammar (doubled-quote escaping), where backslash is an ordinary character.
- **Intermittent generic "Erro" from background KB watching eliminated.** The change-watcher polled the GeneXus SDK from its own thread while tool calls used the SDK on the main worker thread — an unsafe concurrent access the code only partly guarded. Watcher polling now runs on the same serialized worker thread as every other SDK call.
- **With multiple Knowledge Bases open, auto-filled object types no longer come from the wrong KB.** When a tool call omitted `type`, the type could be auto-injected from a different open KB's index (e.g. `Customer` resolved as a Transaction from one KB while editing a same-named Business Component in another). The name→type cache is now scoped per KB.
- **`genexus_multi_agent_lock` writes its lock file atomically,** so a crash mid-write can't leave a corrupt lock that silently reads as expired and breaks mutual exclusion.
- **Browser-driver resolution can't hang.** The one-off driver-path probe read stdout without draining stderr — a latent pipe deadlock. Both streams are now drained.

### Changed

- **Impact analysis and validation are faster on large KBs.** `genexus_analyze` caller/callee expansion no longer re-scans the whole index (or compiles a regex per object) on each hop; `genexus_analyze mode=cross_platform_impact` resolves names through an index map instead of a linear scan; and the pattern-condition validator resolves each object by its known type in one step instead of an untyped rescan.
- **WebForm/Layout edits are faster.** Every visual-part edit resolved its WorkWithPlus host by walking and COM-reading the entire KB; it now looks the host up by name.
- **Long-running gateways use less memory.** Completed background jobs are now swept on the existing maintenance loop (they previously accumulated for the life of the process, holding their full result payloads), and per-key idempotency gates are evicted once idle instead of growing without bound.
- **Generated-file diffs do less disk work** — `genexus_diff_generated` walks each output root once and filters extensions in memory, instead of a separate recursive walk per extension.

### Internal

- Fifth and sixth `improve` audit passes (performance + bug-fixing only) against v2.29.2; eighteen findings across the two passes, each implemented by a dedicated executor in an isolated worktree with advisor review, then merged to `main`. See `plans/022`–`039` for design context. Worker 1529 + Gateway 660 tests green; solution builds clean. Two findings were considered and rejected/deferred (a non-reachable substring check; a low-frequency CLI config read-modify-write race whose robust fix conflicts with the package's zero-runtime-dependency policy) — see `plans/README.md`.

## v2.29.2 — 2026-07-21

Performance and bug-fix pass (audit 2.29.x). No new features — targeted fixes in the recently added SDK-endpoint tools and in long-running gateway internals.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Long-running gateways no longer accumulate stray background tasks.** Each time a worker was retired for being idle, recycled for memory, or killed for hanging, its writer and health-check loops kept running for the life of the gateway against a token that was never cancelled — a slow, unbounded task/timer leak. Every worker-teardown path now cancels cleanly.
- **`genexus_search_source` metadata-field searches are fast again and respect `objectName`.** Searching `fields=[caption|description|parmNames|webForm]` rescanned the entire Knowledge Base and resolved each candidate the slow (untyped) way — quadratic on large KBs — and ignored any `objectName=` scope you supplied. It now stays within the requested scope and resolves each object by its type in one step.
- **A cancelled build's reported state stays cancelled.** After `genexus_lifecycle action=cancel`, buffered build output still draining in the background could overwrite the task a moment later and flip its phase back off `"Done"`. The cancel now freezes the task's state atomically.
- **`genexus_deploy` / `genexus_gxserver` report an unknown `action` as such.** A typo'd action combined with no open KB returned "open a KB first" instead of naming the bad action, costing a wasted round trip. The action is now validated before Knowledge Base state is consulted.
- **`genexus_gxserver action=pipeline_output` requires `buildId`.** A missing `buildId` silently queried build `0`; it now returns a clear error asking for the id.
- **`genexus_transfer action=export` separates a real lookup error from "not found".** An SDK error while resolving an object to export was reported identically to a genuinely missing object, sending you to re-check a name that was fine. Genuine errors now surface in their own `lookupErrors` list, distinct from the not-found list.
- **`genexus_layout action=list_controls limit=0` returns an empty list** instead of one control.

### Changed

- **`genexus_layout action=design_system` (no `name`) resolves the first Design System Object via the type index** instead of walking and COM-reading every object in the KB — a cheaper lookup on large KBs. Falls back to the full scan when the index isn't ready.

### Internal

- Fourth `improve` audit pass (performance + bug-fixing only) against v2.29.1; five findings, each implemented by a dedicated executor in an isolated worktree with advisor review. See `plans/017`–`021` for design context.

## v2.29.1 — 2026-07-21

Security and reliability hardening for the recently added SDK-endpoint tools.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Lingering MSBuild processes are now actually reaped on every build exit.** The v2.29.0 "guaranteed reap" cleanup inspected the build's process handle *after* it had already been released, so the safety net silently did nothing (and logged a spurious cleanup warning on affected builds). Cleanup now tracks the MSBuild process by PID — with a start-time guard against PID reuse — so a hung child process can't linger after the build finishes, fails, or throws.
- **`genexus_screenshot_publish` now only accepts image files from an expected location.** The tool copied any readable file path it was handed into the Knowledge Base's `.gx` tree with no type, size, or location check. It now requires an image extension, confines the source to the OS temp directory, the open KB, or `GXMCP_SCREENSHOT_DIR`, and caps the file at 25 MB — rejecting anything else before it copies.
- **The gateway's HTTP request log no longer records raw request bodies.** The debug log wrote the first 100 characters of every inbound JSON-RPC body, which could capture a credential passed as a tool argument. It now logs the method, id, and argument key names with sensitive values — and any nested object or array — masked.

### Changed

- **`genexus_deploy action=deploy` and `genexus_gxserver action=pipeline_run|pipeline_abort` check `confirm=true` before anything else.** The confirmation gate on these destructive actions now runs before Knowledge Base resolution, so a missing `confirm` is reported the same way whether or not a KB is open.

### Internal

- Added guard-test coverage for the ten SDK-endpoint services introduced across v2.27–2.29 (confirm gates, no-KB handling), and extracted their duplicated design-model resolution into a shared `KbModelGuard` helper (behavior-preserving). Worker 1507 + Gateway 643 green. See `plans/012`–`016` for design context.
- Fixed a parallel-test race: `CompletionNameTests` and `AutoTypeInjectorTests` share `AutoTypeInjector`'s static index and now run in a single non-parallel xUnit collection, so one class's `ClearAll`/`PrimeIndex` can no longer wipe the other's mid-assertion.

## v2.29.0 — 2026-07-20

Reliability & authoring batch — build/deploy status honesty, long-op resilience, and schema/layout fixes. Also closes issues #40 and #41.

### Added

- **`genexus_lifecycle action=build deploy=true` — produce runnable output.** The default fast build compiles the object but skips the Theme/Image/Style/Module copy to `web/bin`, so the compiled `.dll`/`.aspx` isn't deployed and the screen can't be run from the MCP. Pass `deploy=true` to run the full deploy (module/theme copy + WebAppConfig) when you need to actually run or preview the object. Slower; the default stays compile-only.
- **`genexus_lifecycle mode=compile_check callers=false` / `callerCap=N` — scope the check.** A base transaction (a business component called everywhere) has a huge transitive caller closure; expanding it pulled in fan-in orchestrators like the KB-wide DeveloperMenu and could drag dozens of DLLs over 20–30 min. `callers=false` runs a target-only check; `callerCap` (default 40) bounds the closure and flags truncation.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`compile_check` no longer re-expands the whole KB.** It now builds exactly the target plus its (capped) callers with `includeCallees=none`, instead of also walking every caller's dependency graph — which re-dragged the DeveloperMenu the check is meant to skip and left `CompileCheck:false` on the run.
- **A build that compiled cleanly but hit a late deploy step no longer reports a bare `Failed`.** When Generation and Compilation both succeeded and there are zero code errors, an in-process build whose only failure is a downstream step (WebAppConfig, a standalone module like GAMUser) is now flagged `partial_success` — the gateway renders `effective_status=PartialSuccess` (not an error), matching the external-build path. No more contradictory "Failed with 0 errors".
- **Long specify/build calls no longer get dropped to the background at ~120s.** With no client progress token the gateway now returns an interim "still running — poll `op:<id>`" within its safe window instead of blocking the connection until the client gives up. Progress frames also keep the operation's `updatedAtUtc` live so a status poll shows real movement instead of a frozen timestamp.
- **The gateway no longer wedges after a background op is cancelled.** Cancelling an `op:<id>` now signals the worker (freeing its single SDK queue) instead of only marking it locally; a late worker reply can't resurrect a cancelled op; and a write retried under the same idempotency key with different arguments returns a clean error instead of an uncaught exception that rejected every later call for the rest of the session.
- **Aborted builds no longer leak MSBuild processes.** Cancelling a running build now kills its MSBuild process tree (the timeout path already did), so orphaned `MSBuild.exe /m` nodes don't accumulate across sessions and contend for node reuse. As a guaranteed sweep, the MCP now also reaps its spawned MSBuild tree on **every** build completion — success, failure, or exception — so a hung child node can never linger on the user's machine.
- **Health/status calls answer even while a long build or index is running.** `genexus_doctor` and build `status`/`result` polls no longer queue behind the in-flight SDK operation on the worker's single thread, so "is my build done yet?" doesn't block on the very build it's asking about.
- **`genexus_variable` — removing an attribute from a Transaction now fails honestly.** When the SDK build exposes no attribute-removal API, the write is rejected with a clear "IDE-only" message instead of blaming a phantom foreign key, and `mode:full` no longer silently leaves a composite key behind. Removal is attempted through every removal shape the SDK exposes before giving up.
- **`genexus_structure` attribute ops give an actionable error instead of `<Structure> not found`.** A Transaction Structure patch that mixed in a non-attribute op used to fall through to the XML path and fail cryptically; it now reports exactly which op is unsupported and how to phrase attribute ops.
- **`genexus_edit` no longer reports `changed:false` on a write that actually landed.** The post-write re-read was matching the cache by the raw part name while the cache is keyed by the resolved name (e.g. `SDTStructure` → `Structure`), so a real change could read back stale and be mislabeled `WriteNoChange`. The cache is now invalidated by the resolved part name.
- **Structure writes on a duplicate-name Transaction are fast again.** The advisory ambiguity pre-check no longer triggers a blocking 30 s–3 min synchronous index load on the write path; it uses the already-loaded index and is skipped when the index isn't warm (the object still resolves).
- **Editing a control inside an unnamed group table now renders.** When a `<table isGroup="True" title="…">` has no name and isn't already in the WorkWithPlus ordering list, the reconciler derives its slot from the title (when unambiguous) so the container's edited siblings render, instead of bailing on the whole parent.
- **A malformed `<gxButton>` in a WebForm layout no longer crashes the worker.** Structurally invalid layouts (a `gxButton` nested inside another, or pathologically deep nesting) are rejected before they reach the SDK's recursive parser, which could take down the whole worker process.
- **`genexus_sdk_probe` no longer surfaces a spurious error** when called with an empty `outputDir` (it now falls back to the default directory).
- **`genexus_properties action=set` on `ControlValues` no longer wipes the value (issue #41).** `ControlValues` is a structured list of value/description pairs, not a scalar; setting it through the generic property setter wrote an empty collection and silently destroyed the existing values while reporting success. It's now rejected up front with guidance, and — as a general safety net — any property set that would empty a previously non-empty value is rolled back and reported as an error instead of a false `PropertyApplied`.
- **The debug log no longer breaks `npx genexus-mcp@latest` on Windows (issue #40).** `gateway_debug.log` / `worker_debug.log` were written inside `node_modules`, and the open file handle made `npx @latest` fail with `EBUSY` when it refreshed the package. When the server runs from an npm install the logs now go to `%LOCALAPPDATA%\GenexusMCP\logs` (overridable with `GXMCP_LOG_DIR`); source/dev builds keep them next to the executable.
- **`genexus_whoami` update block is trustworthy when the check is stale.** On a long-lived gateway it now re-checks the registry when the cached result has aged past its TTL and marks the result `stale` instead of confidently reporting "the update feed is lagging" off outdated data.

### Changed

- **`genexus_db action=drift_check` returns a real signal.** Instead of an empty stub it now reports the authoritative "does the model diverge from the last reorg?" verdict (via the specifier) and points at `action=sql_ddl` for the schema DDL. The exact table-level ALTER delta still requires running a reorg — the worker doesn't open a DB connection — and the response says so.

### Internal

- Control-bound event failures (`src0208` auto-stub collision, `src0233`/`src0216` control-not-yet-projected) now attach an actionable hint on write errors.
- Schema budget 16200 → 16400 for the new `callers`/`callerCap`/`deploy` params (measured ~16254). Golden discovery fixture updated. New tests cover the unnamed-group-table invent/ambiguous paths. Worker 1485 + Gateway 637 green.

## v2.28.0 — 2026-07-20

See `docs/sdk_uncovered_endpoints_2026-07-20.md` + `docs/sdk_endpoints_roadmap.md` for the coverage analysis behind this batch.

### Added

- **`genexus_transfer` — real XPZ export / import.** Export objects to a `.xpz` (`action=export targets=["Customer"] outputFile=…`), explore an `.xpz` without importing (`action=inspect file=…`), or import one into the KB (`action=import`, `dryRun` defaults true = preview, `dryRun=false` needs `confirm=true`). Unlike the filesystem part-copies of `genexus_io`/`genexus_kb_import`, this is the IDE Export/Import code path — dependency-aware.
- **`genexus_deploy` — deployment targets.** `action=list_targets` (default, read-only) lists the KB's configured deployment target types (e.g. AWS Elastic Beanstalk, Tomcat, IIS); `action=deploy` (requires `confirm=true`) runs a deploy.
- **`genexus_security action=scan_native` — the native GeneXus Security Scanner.** Runs the SDK's own scanner (the engine behind the IDE's Security Scanner), returning `errorCount`/`warningCount`/`findings`. Complements `action=scan_secrets` (regex over Source) and `action=audit_gam` (env-prop scan).
- **`genexus_analyze mode=kb_stats` — KB activity & freshness.** Reports last object change, last table change, last reorg, and a derived `reorgLikelyNeeded`. Optional per-object-type operation history when a `typeGuid` is given.
- **`genexus_db action=reorg_impact` — reorg impact preview.** A cheap timestamp heuristic by default; `deep=true` runs the specifier for the authoritative impact (build-heavy — off by default).
- **`genexus_gxserver action=pipeline_*` — CI pipelines.** `pipeline_list` / `pipeline_runs` / `pipeline_output` (read) and `pipeline_run` / `pipeline_abort` (require `confirm=true`) over Team Development's continuous-integration service, on a GXserver-linked KB.
- **`genexus_analyze mode=table_relations` — table ↔ transaction relations.** For a Transaction, reports its associated table, the other transactions mapped to that table, and the SDK's redundant / possibly-redundant attribute detection.
- **`genexus_layout action=list_controls` — control & theme-class catalog.** Lists the KB's available control definitions (user controls + built-ins) so a layout-authoring agent can pick valid control types / theme classes.
- **`genexus_create action=curl_procedure` — scaffold a Procedure from a curl command.** `name=<ProcName> curl="curl -X POST https://…"` — the IDE "Import from cURL" flow, creating a REST-consumer Procedure.
- **`genexus_layout action=design_system` — Design System Object catalog.** For a DSO (or the first one in the KB), lists its token groups (e.g. `colors`), theme classes, images and referenced DSOs — the styling vocabulary an agent needs to author WWP/DSO layouts. Read-only.

### Internal

- `design_system` constructs `DesignSystemHelper(dso)` on a resolved `Artech.Genexus.Common.Objects.DesignSystem` KB object (Artech.Genexus.Common, already referenced) — instance-based, no service registry; reads via `GetTokensNames`/`GetClassesNames`/`GetAllImagesNames`/`GetAllDSOsNames`. The helper also exposes DSO write ops (Insert/Update/Delete token/class) — not wired yet.
- The three P2 tools reuse the same `SdkServiceLocator.ConstructOrResolve` concrete-impl idiom (all in `Artech.Packages.GenexusBL`, already referenced): `TablesService`, `UserControlsManagerService`, `CurlGeneratorService`. `table_relations` reads the associated table via `transaction.Structure.Root.AssociatedTable`. Two P2 candidates were dropped after the feasibility gate: **translations** (`ILanguageService` is the source-code parser/type manager, not human-language i18n — the CSV `genexus_db action=translations_import` already covers that) and **types_catalog** (`IDataTypesService` needs a CLR-`Type` arg and duplicates `genexus_db action=types_list`).
- All P0/P1 tools wrap SDK services whose registration differs between the IDE and the headless worker. Four (`IModelInformationService`, `ISpecifierService`, `IDeploymentService`/`IDeploymentTargetService`, `ISecurityScannerService`) are **not** in the headless worker's service registry — `Services.TryGetService` (by type or interface GUID) returns null. New `SdkServiceLocator.ConstructOrResolve<T>(factory)` constructs the public concrete impl directly and casts to the interface (the `GamService` idiom), falling back to the registry. Concrete impls: `SecurityScannerService` (+`Initialize(<gx>\Security\Commands)`), `Artech.Packages.Genexus.BL.Services.{ModelInformationService,DeploymentTargetService,DeployService}`, `Artech.Packages.Specifier.Services.SpecifierService`, `Artech.Architecture.Common.Services.StatisticsService`. New csproj refs: `Artech.Packages.GenexusBL`, `Artech.Packages.Specifier`, `GeneXus.SecurityScanner.Common`, `GeneXus.TeamDevClient.Architecture.BL`. Schema budget 15600 → 16200 (measured ~16049). Golden discovery fixture regenerated (46 tools). Live-verified over HTTP on a real KB; Gateway 637 + Worker 1484 tests green.

## v2.27.1 — 2026-07-20

### Added

- **`genexus_authoring action=add_condition` — add a filter condition to a Data Selector.** A condition is a GeneXus source expression: `genexus_authoring action=add_condition name="ActiveCustomers" payload={"source":"CustomerActive = True"}`. Invalid expressions are rejected with the SDK's exact diagnostic (e.g. `src0265: invalid attribute`).

### Internal

- `AuthoringService.AddDataSelectorCondition` routes through `DataSelectorStructurePart.Root.AddCondition(source)` (creates the root level if absent) + `obj.EnsureSave`; verified to persist across a worker reload via a condition-count differential. Adds the `add_condition` case to the `Authoring` dispatcher module + `ConvertAuthoringToolCall`. Schema-budget note bumped to 15600 (measured ~15349). A `add_theme_color` action was prototyped and dropped: a classic `Theme`'s colors are exposed only as a `ThemeColorsVirtualPart` projection (IDE-only, same class as the SDPanel virtual parts in issue #29), so writes through a concrete `ThemeColorsPart` don't attach.

## v2.27.0 — 2026-07-20

### Added

- **`genexus_structure action=create_index` — author a unique index on a Transaction/Table (issue #39).** GeneXus has no `Unique(...)` rule; uniqueness is enforced with an index. This new action creates one over one or more attributes: `genexus_structure action=create_index name="Country" payload={"attributes":["CountryName"],"unique":true}`. Pass `unique:false` for a non-unique index, `name` to set the index name, `order:"Descending"` to flip sort order. Run `genexus_lifecycle action=reorg` afterward to apply the constraint to the physical database. This closes the MCP-only workflow for uniqueness constraints — no IDE round-trip.
- **`genexus_structure action=drop_index` — remove a user-defined index.** Pairs with `create_index`: `payload={"indexName":"IX..."}`. Only indexes with `source:User` can be dropped; SDK-generated (`Automatic`) indexes are refused. `get_indexes` now reports each index's `source` so you can tell them apart.
- **`genexus_structure action=set_attribute` — write attribute-level properties the structure DSL can't express.** On a KB-global attribute: `formula` (define a computed attribute, e.g. `payload={"formula":"sum(InvoiceAmount)"}`), `subtypeOf` (make it a subtype of another attribute), `title`, `columnTitle`, `contextualTitle`, `isCollection`, `basedOnDomain`. Previously these required the IDE.
- **`genexus_structure action=set_level` — set a Transaction level's Description / Image attribute.** `payload={"descriptionAttribute":"CustomerName"}` (optionally `imageAttribute`, and `level` to target a sub-level). The attribute must belong to that level.
- **`genexus_structure action=set_domain` — edit an existing Domain's enum values and base type.** `payload={"enumValues":[{"name":"Active","value":"A"},…]}` replaces the domain's enum set (character-family values are auto-quoted); optional `dataType`/`length`/`decimals`/`signed`. Domain *creation* already accepted enum values; this closes the edit-after gap.
- **`genexus_authoring` — a new tool for authoring members of object types the structure DSL doesn't cover.** `add_external_method` / `add_external_property` add a method (with parameters) or a property to an **External Object** (`payload={"name":"apiKey","type":"Character"}`); `add_menu_option` adds an option to a **Menu** that calls a KB object (`payload={"description":"Customers","target":"CustomerWW"}` — a target object is required). Auto-assigns the next menu option code when you don't pass one.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Editing a Transaction's `Rules` with an invalid rule now tells you what's wrong instead of a bare "Erro" (issue #39).** Writing `Rules` that contained `Unique(Attribute);` failed with `Part save failed: Erro` and no detail, which looked like the whole `Rules` part was broken. It wasn't — valid `Rules` writes (`Default`, `Error`, `NoAccept`, assignments, conditional rules, proc calls) always worked and still do. The one bad rule was `Unique`, which GeneXus does not recognize (the SDK reports `src0295: unknown rule 'Unique'`). `genexus_edit part=Rules` now returns an actionable `hint` for this: enforce uniqueness with `genexus_structure action=create_index` instead. The `Unique` clause only ever existed for queries and was removed after GeneXus 18 Upgrade 9.

### Internal

- **create_index:** `IndexService.CreateIndex` resolves the transaction's associated table, then `Index.Create(model)` + `IndexType.Unique`/`Duplicate` + `IndexSource.User`, populates `IndexStructure.Members` (`IndexMember` = `Attribute` + `IndexOrder`), attaches via `TableIndexesPart.AddIndex` (the `Index.Table` setter is read-only, so association is through the part), then `index.EnsureSave()` + `tbl.EnsureSave()` inside a `BeginTransaction`. Verified to persist across a full worker kill+respawn (disk re-read). Routed through `genexus_structure` (`OperationsRouter.ConvertStructureToolCall` → dispatcher `CreateIndex`).
- **Rules hint:** the rules specifier throws `ValidationException` with a bare "Erro" from `part.Save()` and empty `GetSdkMessages`/`GetDiagnostics`. `WritePolicy.FindInvalidRuleKeywords` scans the statement-leading keyword of each `;`-delimited rule against a curated denylist (`InvalidRuleKeywords`, currently `Unique`) after stripping comments; `BuildInvalidRuleHint` maps hits to guidance. `WritePolicy.IsUninformativeSaveError` strips the `Part save failed:` wrapper before the bareness check. `CreateTransactionErrorResponse` attaches the hint only when the part is `Rules`, the error is uninformative, and a keyword matches — it never blocks a write. Note: routing Rules through the `IsLogicalSourcePart` retry path (as proposed in a community patch) was evaluated and rejected — it exposes Rules to the pre-existing `EnsureSave(false)` validation-bypass branch, which can persist a rule that fails `obj.Validate()`. New tests: `WritePolicyErrorEnrichmentTests` (+20).

## v2.26.1 — 2026-07-20

### Changed

- **`mode=compile_check` is now discoverable from the tool description.** The fast "did my edit break the build?" check added in v2.26.0 was only visible if you read the `mode` parameter; the `genexus_lifecycle` description now calls it out next to `action=specify`, and there's a worked example (`{"action":"build","mode":"compile_check","target":"MyObject"}`). No behavior change.

## v2.26.0 — 2026-07-20

### Added

- **`genexus_lifecycle action=build mode=compile_check` — a fast "did my edit break the build?" check.** A full build-all spends most of its time regenerating the KB-wide Developer Menu (measured ~200s of a ~260s run), which has nothing to do with whether your code compiles. `compile_check` builds the object(s) you name **plus everything that calls them** (transitive callers, so a changed signature surfaces errors in every caller) and skips the Developer Menu regeneration entirely — spec + generate + compile only. It requires a `target` (that's the point: it scopes the check to what you changed). When the caller graph isn't available yet (index not built), it checks the named objects alone and says so in the response. For a full from-scratch KB build, use `action=build` with no target.

## v2.25.2 — 2026-07-17

Fixes the build-hang reported against v2.25.1, where `genexus_lifecycle action=build` — most visibly a build with no target ("build all") — would generate the KB, then sit at `Running` for many minutes with no phase progress and never reach a terminal state until cancelled by hand.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **A build that fails no longer secretly re-runs the whole thing.** When the in-process GeneXus build ran end-to-end and reported failure (for "build all", on real compile errors), the MCP was discarding that result and silently restarting the entire build as an external MSBuild process — re-opening the KB and recompiling from scratch. That second full pass is what looked like an indefinite hang. The MCP now surfaces the failure from the first pass and terminalizes immediately (`Failed`). The external build is used only when the in-process build could not start at all (SDK unavailable, unsupported action such as reorg), never to retry a build that already ran.
- **In-process builds now report phase progress.** Progress was stuck at the starting phase for the whole build because the phase parser only understood the external MSBuild text format, not the section-marker stream the in-process build emits. Builds now advance through Specifying → Generating → Compiling → Finishing as they run.
- **A build that fails without a per-line error is now actionable.** When the build fails at the section level with no itemized `error CS####:` line (typical of the deploy/config stage), the response now names the failing section under `phaseFailure` and points you at `genexus_lifecycle action=specify target=<object>` for itemized spc/gen diagnostics, instead of reporting a bare `Failed`.

### Internal

- `InProcessBuildRunner.Run` returns a tri-state `InProcessBuildOutcome` (`Succeeded` / `FailedWithDiagnostics` / `CouldNotRun`) instead of `bool`; `BuildService.RunBuild` only falls through to the MSBuild.exe spawn on `CouldNotRun`, and terminalizes in-process (setting `StateChangeSignal`) on the other two. `HandleLine` parses `>S`/`>E0` section markers via `MapSectionToPhase`; `>E0` on a non-`Build` section sets `PhaseFailure`. New tests: `InProcessMarkerParsingTests` (+9); `InProcessBuildRunnerTests` / `EdgeCaseRegressionTests` updated for the enum return.

## v2.25.1 — 2026-07-17

Fixes the gateway lock-up reported in issue #38, where opening a path that isn't a Knowledge Base root (a GeneXus environment/model subfolder, with no `.gxw` / `knowledgebase.connection`) put the worker into an endless background auto-open loop and eventually left every tool call returning `Master error: NotFound` (404) until the server was restarted by hand.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Opening a non-KB path now fails fast with a clear error instead of wedging the gateway.** `genexus_kb action=open` validates that the path is a real KB root (a folder with a `.gxw` / `knowledgebase.connection`, or the `.gxw`/`.gx` file itself) before a worker is spawned for it, and returns `KbInvalidPath` when it isn't. Previously the bad path was handed to a fresh worker whose open failed but kept retrying, so the whole gateway drifted into an unrecoverable state.
- **Background KB auto-open no longer retries forever.** A structurally unopenable path used to be re-attempted on every operation (the debug log filled with the same failed open every few seconds). Auto-open now gives up after 3 consecutive failures and says so; an explicit `genexus_kb action=open` still works and a successful open resets the counter.
- **The gateway recovers on its own when its session to the running server expires.** When a second AI client shares the already-running server and that server restarts (or its session ages out), the client used to get `Master error: NotFound` on every call with no way back except a full restart. The connection now re-establishes the session transparently and retries the call.
- **No more spurious `SynchronizationLockException` on worker shutdown.** The worker's single-instance lock was being released from a different thread than the one that acquired it, logging an error on every shutdown. The release now happens on the owning thread (or is safely left to process exit).

### Internal

- Gateway `Configuration.IsPlausibleKbPath` gates `genexus_kb action=open` in `Program.RequestLoop` before `WorkerPool.AcquireAsync`; worker `KbService.OpenKB` mirrors the check and short-circuits to a `KbInvalidPath` envelope before the SDK `KnowledgeBase.Open`. `KbService` bounds background auto-open via `MaxAutoOpenFailures` (3) + `_autoOpenAbandoned`. Proxy path in `Program.RunMcpProxyAsync` caches the client `initialize` line and, on an HTTP 404 from a live master, replays it via `ProxyRehandshakeAsync` to mint a fresh `MCP-Session-Id` and resend the request (distinct from the connection-failure promotion path). `SingleInstanceLock` records the acquiring thread id and only calls `ReleaseMutex` from it. New tests: `KbPathValidationTests` (+5), `KbOpenValidationTests` (+2).

## v2.25.0 — 2026-07-17

Addresses the deploy/database-apply gaps reported in issue #37: reorg couldn't run, builds and F5 previews could hang forever, and a DBA-managed "no reorg" database was invisible to an agent driving GeneXus headlessly.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Builds and previews can no longer hang indefinitely.** A build (or `buildFirst` preview) that wedges in a late deploy/reorg step used to sit at `Running` with no terminal state, forcing you to cancel by hand. Each build task now has a wall-clock cap: on expiry it is force-failed with a clear reason and any spawned MSBuild process tree is killed. Default 900s (2400s for a full rebuild); override with `GXMCP_BUILD_TIMEOUT_SEC`.
- **`genexus_lifecycle action=reorg` no longer fails with `MSB4036` (task `CheckAndInstallDatabase` not found).** The generated MSBuild project was resolving under the CLR-2.0 toolset, where the .NET 4.x GeneXus task assemblies can't load. It now pins `ToolsVersion="4.0"` so the reorg task resolves.

### Added

- **`genexus_lifecycle action=reorg_preview` now reports the target datastore.** The response carries a `datastore` block (type, dialect, DBMS, provider/driver, access technology) so an agent driving a deploy headlessly can see what it's about to reorganize against. When a KB *does* expose a "Reorganize server tables" toggle, `reorg_preview` surfaces `reorgEnabled` and `action=reorg` fast-fails with `ReorgDisabled` instead of queueing a build that can never apply the schema.
- **KB-wide source analytics: `genexus_analyze mode=code_metrics`.** Answers questions like "how many `for each` loops are in the KB" and "which procedures should I optimize" instantly, with no per-object SDK reads. Returns totals (for-each, nested-for-each, where, new, commit, lines) plus `optimizationCandidates` — procedures with a `for each` nested inside another (the classic smell that often collapses to a single navigation or a data selector) — and a top-by-for-each ranking. Metrics are captured while the index enriches each object; run `genexus_lifecycle action=index force=true` once so already-indexed objects pick them up.
- **`genexus_inspect projection` levels are now live.** `projection=minimal` returns just name/type/lastUpdate/availableParts for a cheap orient; `standard` (default) is the new lean shape; `verbose` restores full detail. Previously the parameter was advertised but ignored.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Enumerated ("combobox") Domains now render their options.** `genexus_create` for a Character/VarChar Domain stored `enumValues` raw (`A`), but GeneXus needs quoted literals (`"A"`) — a raw value produced an empty combobox in the IDE. Character-family enum values are now auto-quoted (pass the bare value; already-quoted input is left alone); numeric/date domains are unchanged.
- **`genexus_layout action=add_printblock` works on any report Procedure.** It previously failed with "no compatible AddBand/collection mutator found" unless the layout already had a `footer` band. It now uses the report layout's own `AddBand` method, so a print block can be added to a freshly-created Procedure.
- **Datastore `provider` / `accessTechnology` are no longer blank.** These were read under friendly names (`ServerName`, `Provider`, …) that GeneXus doesn't use; the introspection now reads the real internal descriptors (`CS_SERVER`, `ADONET_DRIVER`/`JDBC_DRIVER`, `ACCESS_TECHNO`, …). This also unblocks the `whoami` database block, which shared the same latent dynamic-dispatch bug and was stuck at `Pending`.

### Changed

- **`genexus_inspect` default response is ~73% smaller.** The default now caps each inlined source part (Rules/Conditions/Events) to a 1200-char head and returns variables as name+type (capped at 40) — a WebPanel snapshot dropped from ~4150 to ~1100 tokens in testing. Full source is always available via `genexus_read`; pass `projection=verbose` for the previous full shape.

### Internal

- `BuildService.ResolveBuildTimeoutSeconds` + a `System.Threading.Timer` watchdog in `RunBuild` (terminalizes + `KillProcessTree` on cap); the MSBuild.exe path now uses a bounded `WaitForExit`. `DatabaseInfoService.GetDefaultDataStoreInfo(kb)` reused by `BuildService.ReorgPreview`/`CheckReorgDisabled`; `BuildEntry`/`GetInfo` cast the dynamic `BuildEntry(ds)` result to `JObject` so `entry["isDefault"]?.Value<bool>()` binds statically (a dynamic generic call threw "no overload for 'Value' takes 0 arguments"). **Known limitation, verified live against an Oracle KB:** GeneXus 18's SDK does not expose "Reorganize server tables" as a discrete datastore/environment/target-model property (the reorg-named properties are all generator selectors: `REORG_GEN`, `ReorgEnvironment`), so `reorgEnabled` auto-detection is dormant on stock GeneXus 18 — `reorg_preview` says so and points at the IDE. New tests: `BuildTimeoutAndReorgModeTests` (+4).
- `ObjectService.InitializeDomain` auto-quotes char-family enum values via `QuoteCharEnumValue` (`IsStringDataType`/`IsStringDomainByName` gate); new `DomainEnumQuotingTests` (+7). `ReportLayoutHelper.TryAddBandToCollection` now calls `ReportLayout.AddBand(ReportBand)` before falling back to the (read-only) `ReportBands` iterator. `AnalyzeService.GetConversionContext` takes a `projection` arg (minimal short-circuits before the SDK task fan-out; standard caps source heads at `InspectSourceCapLean=1200` + variables ≤40 name/type; verbose keeps 8000 + full detail); threaded via `CommandDispatcher` (honors legacy `verbose:true`) and `AnalyzeRouter`. Golden fixture + `genexus_inspect` schema description updated (schema budget unchanged).
- `SearchIndex.IndexEntry.Metrics` (`CodeMetrics`: forEach/nestedForEach/where/new/commit/lines), populated by `CodeMetricsExtractor.Extract` inside the Procedure/DataProvider enrichment branch (reuses the source already read for Complexity). `AnalyzeService.GetCodeMetrics` aggregates over the index; `code_metrics` mode wired via `AnalyzeRouter`/`CommandDispatcher`; `genexus_analyze` `required` relaxed to `["mode"]` (code_metrics is KB-wide). New `CodeMetricsExtractorTests` (+4).

## v2.24.0 — 2026-07-17

Closes the gaps reported in issue #36 from an end-to-end WorkWithPlus feature build: schema edits that silently no-op'd, misleading success signals, and an unnamed-container layout skip. The theme is honesty — a write that didn't take effect now fails or warns instead of reporting success.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Structure edits are authoritative — no more silent additive-only merges.** `genexus_edit part=Structure` with `mode:full` now replaces the whole attribute list, including keys: sending a different key line no longer leaves you with a composite double key. Removals run after additions, so replacing a key works (the new key exists before the old one is dropped). When the SDK genuinely refuses to drop an attribute (e.g. a key still referenced by a foreign key, relation, or index), the write is aborted with a `StructureAttributeNotRemoved` error explaining why — instead of quietly keeping the attribute and reporting success.
- **`remove_attribute` persists or errors — never `ok:true` on a no-op.** A `mode:ops remove_attribute` (or a textual patch that deletes an attribute) that the SDK does not actually persist now surfaces the failure on the envelope, rather than returning a green `opResults` list while the attribute remains.
- **`genexus_variable action=modify` reports the type it actually persisted.** The success message showed the requested type name even when the SDK stored a different one; it now reports the persisted type (and, when they differ, both) plus `requestedType`/`persistedType` fields.
- **SDT structure members typed `Blob`/`Binary` (and `Image`/`Bitmap`, `Audio`/`Video`) persist with the right type** instead of silently degrading to `VARCHAR`. An unknown member type token is now rejected loudly rather than coerced.
- **WorkWithPlus layout edits inside an unnamed group table now reconcile correctly.** A group table with a title but no name (`<table isGroup="True" title="…">`) is matched by its title against the existing render order, so retargeting a control inside it projects to the Web Form. When the container still can't be addressed, the response carries a top-level `warning` that the affected controls will NOT render — instead of a footnote that was easy to miss.
- **`changed:false` writes now say whether your change is already in place.** A write whose persisted content equals the prior content is still reported as `WriteNoChange`, but now includes `requestedApplied:true` when the persisted state matches what you asked for (an idempotent no-op, safe) — so callers can tell "already applied" from "possibly dropped, verify via persistedSnippet."
- **`genexus_search_source objectName` matches module-qualified and bare names alike**, and when the filter resolves to zero objects it returns an explicit `ObjectNameNoMatch` (with the names you passed) instead of an empty result that looked like the filter was ignored and the whole KB scanned.
- **A timed-out long write now carries an actionable next step.** Large Transaction/Structure writes that exceed the gateway wait budget report a `hint` on `genexus_lifecycle action=status`/`result` explaining the change may already have persisted and to re-read the target to confirm — rather than a bare, frozen `Running`.

### Added

- **Web Panel events skill: control-bound events + WorkWithPlus `userAction` stub.** `genexus://kb/skills/webpanel-events` now documents that control-bound events/properties (`&Var.ControlValueChanged`, `&Var.Click`, `&Var.Display`) must be written after the control exists in the form, and that a WWP `userAction` auto-generates an empty `'DoFoo'` event stub you fill rather than redefine.

### Changed

- `genexus_edit part=Structure` `mode:full` is now replace-semantics (authoritative), not merge-semantics. Writes that relied on the old additive merge to keep unlisted attributes must now include those attributes in the DSL.

### Internal

- `TransactionDslParser.SyncTransactionNodes` moves removals after adds/updates and no longer swallows removal failures; it accumulates them and `Parse` throws `StructureRemovalException`, caught by the Structure interceptor in `WriteService` (`StructureAttributeNotRemoved`). `ApplyTransactionStructureOpsViaDsl` now propagates a failed persist as the envelope. `WrapWithPersistedState` takes an optional `requestedContent` and sets `requestedApplied` via a whitespace-insensitive compare. `PatternChildOrderReconciler` gains a guarded title/caption fallback (`GetWeakTableIdentifier`) that only reuses an identifier already present in the existing list; `WriteService.PatternWrite` escalates the skip to a top-level warning. `SourceSearchService.ObjectNameMatches` handles qualified/bare names and emits `ObjectNameNoMatch`. `SdtDslParser.ResolveDbType` maps blob/image family types and returns null (no VARCHAR fallback) on unknown. `OperationTracker.AttachTimedOutHint` adds the read-back hint on timed-out-but-Running ops. `reorg_preview` remains a stub: the net48 SDK exposes no non-mutating reorg-plan API (`CheckAndInstallDatabase` always touches the live DB); the response says so and points at `action=reorg` on a non-prod environment / `action=validate-kb`. New tests: `PatternChildOrderReconcilerTests` (+2, unnamed-table title fallback and hard-skip).

## v2.23.0 — 2026-07-17

Fixes issue #33 — SDT-typed collections and `WebSession` variables can now be authored entirely through the MCP, without dropping to the IDE or an XPZ import — and hardens the worker against native SDK faults that were taking it down mid-edit (issue #35).

### Added

- **`genexus_variable typeName=WebSession`.** Declaring a variable as `WebSession` now produces a real `WebSession`-typed variable in one call, so `&Sessao.Get('…')` / `&Sessao.Set('…', …)` validate in Source and Events instead of failing with `src0294: unknown function 'Get'`. Previously `WebSession` was rejected as an unknown type, and the workarounds (`GX_USRDEFTYP`, setting `DataTypeString` alone, or forcing the raw custom-type id) left the variable half-typed. It round-trips through `genexus_read` / `genexus_edit` as `WebSession`, and `genexus_variable action=modify` retypes to it too.
- **SDT structure members can reference another SDT (typed collections).** Writing a Structure member such as `Items : SDT_Foo Collection` now persists as a reference to `SDT_Foo` (a `GX_SDT` member carrying the SDT type), matching what the IDE produces — instead of silently degrading to `VARCHAR(40) Collection` and losing the type link. Reading the structure back shows the SDT name (`Items : SDT_Foo Collection`), so it round-trips unchanged. This unblocks List / wrapper SDTs whose items are themselves SDTs.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **A native GeneXus SDK fault no longer silently kills the worker mid-edit.** Some complex edits (large WebComponents, certain Structure writes) made the SDK raise a corrupted-state fault (`AccessViolation`) that the runtime turned into an immediate process exit — the client saw the MCP disconnect with no answer, and the in-flight call was lost (issue #35, and the homonym Transaction/Table Structure crash). The worker now catches that fault, returns a structured `WorkerNativeCrashRecovered` error for the call, and restarts cleanly so the gateway brings up a fresh worker — so a bad call fails with a message and a retry works, instead of dropping the connection. (A `StackOverflow` remains unrecoverable by design; it stays a hard restart.)

### Internal

- Bug-report tooling: `scripts/collect-diagnostics.ps1` produces a single **redacted** bundle (versions + worker crash ledger + `[WORKER-CRASH]`/`[COLD-START]`/`[TOOL-LATENCY]` log markers; paths/user/host/KB names replaced with placeholders) for pasting into an issue without leaking PII, and a `.github/ISSUE_TEMPLATE/bug_report.md` points reporters at it and asks the questions that pinpoint a crash (exact tool call, whether `type` was passed, `whoami.deaths`).
- Worker stability: `App.config` enables `legacyCorruptedStateExceptionsPolicy` and `Program.ProcessCommand` is marked `[HandleProcessCorruptedStateExceptions]`, so an `AccessViolation`/`SEHException` from the COM-flavoured SDK is caught at the per-command boundary instead of terminating the process. On a corrupted-state exception it sends the recovery envelope, logs `[WORKER-CRASH] recovered corrupted-state`, and `SchedulePoisonedExit` exits with a distinct code (`ExitCodePoisoned=70`, surfaced in `whoami.deaths.byExitCode`) after flushing stdout — the AppDomain is never reused after a corrupted-state fault. Ordinary exceptions keep the existing "answer with error, keep serving" path (guarded by `WorkerCrashGuard.IsCorruptedState`). The mechanism was proven on this runtime with a standalone net48 harness raising a real AV (`Marshal.ReadInt32`) and catching it; residual preventable death causes (proxy false-promotion, idle-reap, self-heal respawn) were already fixed in v2.20.0. New tests: `WorkerCrashGuardTests` (worker, 9).
- Problem B: `VariableInjector` gains a small registry of built-in user-defined effective types (`WebSession` → subtype 31, category 255) and `BindVariableToExternalObject` / `TryBindBuiltinUserDefinedType`, which set `eDBType.GX_USRDEFTYP` + `DataTypeString` + an `AttCustomType(guid=<subtype>, dataType=255, description=<name>)`. The custom-type reflection previously inlined in `BindVariableToSdt` was extracted into a shared `BuildAttCustomType`. Wired into `WriteService.BuildResolvedVariableInto`, `ModifyVariableInternal`, and `SetVariablesFromText`; `ResolveTypeRepresentation` now renders `GX_USRDEFTYP` via `DataTypeString`. Encodings were reverse-engineered against a live KB (`AttCustomType` for `WebSession` = `255:31`; SDT structure member = category `254` + a `StructureTypeReference` `<Type>{sdt class guid}</Type><Id>{sdt object id}</Id>`).
- Problem A: `SdtDslParser.SyncSDTNodes` resolves a non-primitive member token to an SDT via `VariableInjector.ResolveTypeObject`, adds the item as `GX_SDT`, and binds it with `BindSdtItemToSdt` (item `SetPropertyValue("ATTCUSTOMTYPE", …)`) instead of falling through `ResolveDbType` to `VARCHAR`. The read path (`SerializeLevel`) recovers the referenced SDT name from the persisted `StructureTypeReference` by resolving `EntityKey(<class guid>, <id>)`; the owning-SDT `SDTItem.SDT` getter is not the target and can't be used. The KB model is threaded via a `_model` field set in `Parse`/`Serialize` (the root `SDTLevel` exposes no `Model`). Both fixes verified live (write + read-back + a spec-check that `&Sessao.Get/.Set` compile clean). New tests: `Issue33WebSessionAndSdtCollectionTests` (worker, 11).

## v2.22.0 — 2026-07-16

Fixes issue #34 — the blocker plus the three secondary problems reported alongside it.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_edit` can now add and modify attributes on a base Transaction.** Every base Transaction shares its name with an auto-generated Table, and while `type` was honored on read and on `dryRun`, the actual write ignored it and re-resolved by name — hitting both objects and failing with `Ambiguous object name`. `type` is now carried all the way into the write, so `genexus_edit part=Structure` (mode `patch` and `ops`) persists against the Transaction you named. This also unblocks JSON-Patch writes and any other same-named object pair (e.g. a WebPanel behind a Transaction).
- **`genexus_edit mode=ops add_attribute` works and accepts the documented argument shape.** Attribute ops (`add_attribute`, `set_attribute`, `remove_attribute`) on a Transaction failed with `<Structure> not found`; they now apply through the same Structure path the `patch` mode uses, so they actually persist. Both the documented `{ op, args: { name, type } }` shape and the flat `{ op, name, type }` shape are accepted.
- **`genexus_variable` no longer stores a Blob or Image as `NUMERIC(4)` and reports success.** `typeName: "Blob"` / `"Binary"` (and `"Image"`) were recognized but mapped to a database type that doesn't exist, so the variable silently fell back to `NUMERIC(4)` while the response claimed the requested type. Blob/Binary now persist as `BINARY` and Image as `BITMAP`; a recognized type that genuinely can't be applied now returns an error instead of a wrong-but-successful write.
- **`genexus_search_source` honors `objectName`.** The `objectName` filter (and the `startIndex` / `timeoutMs` resume knobs the timeout hint tells you to use) was dropped before reaching the search, so a scan scoped to a handful of objects still swept the whole KB and timed out. Scoping to named objects is now the advertised O(objects) scan.

### Internal

- Root cause across three of the four bugs was the gateway dropping an argument before it reached the worker: `ObjectRouter` didn't forward `type` on `mode=patch`/`mode=ops` (only the full-write branch did), and `SearchRouter` didn't forward `objectName`/`startIndex`/`timeoutMs`. `type` is now threaded through `PatchService.ApplyPatch`, `WriteService.ApplyJsonPatch`/`ApplySemanticOps` (→ `FindObject`/`WriteObject`), and `PatchService.ParseWriteResult` now lifts the canonical `error.code` (it only lifted `error.message`, so `AmbiguousObjectName` surfaced as the generic `PatchWriteFailed`).
- Transaction Structure attribute ops route through a new `SemanticOpsService.ApplyTransactionStructureDsl` that mutates the Structure DSL text and persists via the DSL parser, instead of the XML-descendants handlers that assumed a `<Structure>`-rooted document (the real Structure part does not serialize that way — the old unit tests used fabricated XML). `SemanticOp.From` hoists a nested `args` object to the top level (flat fields win on clash). `VariableInjector.TryParseDbType` and `AttributeTypeApplier.CanonicalToEdb` map Blob/Binary→`BINARY`, Image/Bitmap→`BITMAP` (verified against the live `eDBType` enum, which has no `BLOB`/`IMAGE` members); `BuildResolvedVariableInto`/`ModifyVariableInternal` return/raise a `TypeNotApplied`/`PrimitiveNotApplied` error rather than persisting a default-typed variable. Bug 1 reproduced live (`Ambiguous object name` on a real homonym Transaction) before the fix. New tests: `Issue34RouterForwardingTests` (gateway, 6), `Issue34EditTypeAndVariableTests` (worker, 13).

## v2.21.0 — 2026-07-15

### Added

- **`genexus_memory` — a per-KB fact store you write to explicitly.** Save short facts about the KB — a validation rule, a naming convention, a gotcha about a specific object — and recall them later, scoped to the Knowledge Base you're working in. `action=save` takes a `fact` (optionally tagged with an object `target`, `type`, and free-form `tags`); saving the same fact about the same object again just bumps a hit count and merges tags instead of duplicating. `action=recall` returns facts matching any of a `target` / `type` / `tags` filter (no filter returns everything), ranked by how often they've been reinforced; `action=list` shows them all newest-first; `action=forget` drops one by id. Relevant memories are also surfaced automatically alongside `genexus_inspect`/`genexus_read` results for the same object, and `genexus_whoami` nudges you to recall them once per KB. Facts live under the KB's `.gx/memory/` folder, so they travel with the KB and stay separate across different KBs.
- **`genexus_memory action=promote`** lifts a friction-log observation into a durable memory — pass the message text from `genexus_friction_log action=tail` and it's saved tagged `friction`, sourced as `promoted-from-friction`.
- **`genexus_memory action=consolidate`** — "dreaming": merges redundant or overlapping facts within a scope (same object, matching or near-duplicate wording) and compacts the memory file down to the survivors. `dryRun=true` (default recommended first call) previews the proposed merges without writing; `dryRun=false` applies them. `genexus_whoami` suggests this once a KB accumulates 30+ memories, instead of a plain recall.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_refactor action=RenameObject` can now rename any object, and tells same-named objects apart.** Renaming a WebPanel, Transaction, or Procedure previously failed with "Attribute not found" — the action only ever renamed attributes — and when two objects shared a name (for example a WebPanel and the same-named `Table` generated behind a Transaction) there was no way to indicate which one you meant. RenameObject now resolves the object by name, disambiguated by `type` (and honoring a GUID or `Type:Name` target), renames it, and patches every call-site that referenced it. Pass `type=WebPanel` (or `Transaction`, `Procedure`, …) when a name is shared. `genexus_rename_across_kb` gets the same type-aware resolution; renaming attributes is unchanged.
- **`dryRun` previews no longer execute for real.** `dryRun=true` on `genexus_refactor` (rename/extract) — and on the other tools that read it, including index/build/run previews — was silently dropped on the way to the worker, so the "preview" actually performed the operation. Previews now stay previews; nothing is persisted until you run without `dryRun`.

### Internal

- New `MemoryService` (worker) — append-only JSON-lines at `<kbPath>/.gx/memory/memory.jsonl`; edits/tombstones are new lines sharing the original `id`, folded to the latest non-tombstoned record per id by `LoadLive`. Mirrors the `FrictionLogService` static-Core IO idiom. Routed via `OperationsRouter` (`genexus_memory` → module `Memory`) and `CommandDispatcher.Handle_Memory`. Tool-schema budget bumped 14100 → 14550 (measured ~14333 tokens; ~217 headroom) for the new schema. New tests: `MemoryServiceTests`.
- Phase 3: `ConsolidateCore` groups live records by `(objectName, objectType)` (case-insensitive) and merges exact-normalized-text duplicates and substring/superset facts, summing `hits`, unioning `tags`, and recording absorbed ids in `supersedes[]`. Non-dryRun rewrites `memory.jsonl` via a temp-file-then-copy (crash-safe compaction). `Promote` reuses `SaveCore` (now takes an optional `source` parameter, defaulting to `"explicit"` to preserve existing callers) with `source="promoted-from-friction"` and an auto-added `friction` tag. AI-assisted synthesis of merged facts (`useAi`) was scoped but not wired: `AiCompleteService` lives in the Worker and could be reached in principle, but doing so from the static, network-free `ConsolidateCore` core would trade a deterministic/testable merge for a live HTTP call with no DI seam — deferred, noted inline in code. `genexus_whoami`'s memory nudge now recommends `action=consolidate dryRun=true` once a KB has 30+ live memories instead of a plain recall (same once-per-alias gate). Schema-token measurement stayed under budget (~14469 of 14550) after adding `consolidate`/`promote` to the action enum plus `message`/`dryRun` params — no bump needed. New tests: 5 added to `MemoryServiceTests` (20 total in the class).
- `RenameObject` was split from `RenameAttribute` in `RefactorService` (it had been aliased to it and gated on `TypeDescriptor.Name == "Attribute"`, which is why non-attribute renames returned `AttributeNotFound`). The new path resolves through `ObjectService.FindObject(name, typeFilter)`, keys the index `CalledBy` edges on the object's real `<Type>:<Name>` instead of `Attribute:`, and mirrors the existing caller-patch-then-rename flow (`ObjectRenamed` / `ObjectRenamedPartial` envelopes). The gateway's `ConvertRefactorToolCall` and the `genexus_rename_across_kb` case now forward `type` into the worker payload (it was dropped before, so the disambiguator never reached the worker). Router/payload plumbing covered by `RefactorRenameObjectRouterTests` + extended `RenameAcrossKbRouterTests`; the SDK-touching rename itself was validated live (renamed a WebPanel round-trip in a real KB — the `.Name` setter is not a no-op for non-Attribute types).
- `BuildWorkerRpcRequest` (gateway) hoists `dryRun` to the top level of the worker RPC alongside `action`/`target`/`payload`. It had only ever been placed under `params`, but several worker handlers (`Handle_Refactor`, index/build/run/github) read `request["dryRun"]` from the top level, so `dryRun` resolved to `false` and previews executed. Found via live smoke of RenameObject `dryRun=true` (it renamed for real); confirmed fixed live (now returns the `DryRun` preview and writes nothing).

## v2.20.0 — 2026-07-14

Worker-stability pass: the worker stops dying for reasons that have nothing to do with your KB, and when it does die you can finally see why.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **A second editor/agent no longer kills your live worker.** When more than one client connected at once, a second gateway ran as a proxy to the first. A routine, id-less MCP notification — which the main gateway correctly answers with an empty acknowledgement — was misread as "the main gateway is dead," triggering a takeover whose port-recovery step then force-killed the real gateway *and its GeneXus worker*, mid-edit or mid-build. The proxy now treats an empty acknowledgement to a notification as success, re-verifies the main gateway is actually gone before taking over, and never force-kills a process holding the port unless it is itself one of ours. The one request that did trigger a (now genuinely warranted) takeover is replayed by the new master instead of being dropped. This removes a whole class of "the worker just died / I had to reconnect" interruptions that were never about your KB.
- **Two gateways starting at the same instant no longer both become master.** The coordination lease was written non-atomically, so a gateway starting concurrently could read a half-written lease, see "no master," and register a second one (a startup split-brain that ended the same way — a killed worker). The lease is now published via an atomic rename, so a starting gateway always reads either the previous complete lease or the new one, never a partial.
- **A worker that fails to respawn now keeps trying instead of getting stuck.** If the automatic respawn after a crash exhausted its quick retries (host under load, the KB briefly locked by the IDE), the KB was left in `respawn_failed` until you manually reloaded. It now retries quietly on a long interval for ~30 minutes, so a transient cause self-heals without intervention.
- **A long-running-but-progressing operation is no longer killed as "wedged".** The health check reaped any worker whose in-flight command exceeded the wedged ceiling (15 min), even when it was actively working — a big first `genexus_gxserver update` (applying hundreds of objects from the server) was killed at 15 min mid-apply. Wedged detection is now progress-aware: a worker still emitting output is treated as slow, not hung, and is only reaped once it also goes silent. Long updates and builds run to completion.

### Changed

- **The worker stays warm far longer.** An idle worker was reaped after 5 minutes, and the very next tool call then re-paid the full ~90-second cold start (almost all of it the GeneXus Service Manager warmup, which is intrinsic and can't be shortened). The idle window is now 60 minutes and is genuinely disableable: set `Server.WorkerIdleTimeoutMinutes` to `0` to keep the worker up for the whole session. A value of `0` previously did nothing — it was silently forced up to 1 minute. Memory stays bounded by the open-KB limit and by the worker exiting when you disconnect.
- **The worker recycles itself before a long session bloats it.** Baseline memory is small (~130 MB, ~160 MB even on a 38k-object KB), but over a long heavy session the heap can drift up. When the worker has been idle a moment and its memory is over `Server.WorkerHeapRecycleMB` (default 1500, `0` disables), the gateway now recycles it and brings up a fresh warm replacement in the background — so the next thing you do starts on a clean heap instead of one heading toward the 32-bit ceiling. It only ever triggers while idle, so it never interrupts a running operation. The worker also compacts its large-object heap once whenever it goes idle, so fragmentation can't accumulate across a long session.

### Added

- **`genexus_gxserver update` now applies changes, and `resolve` can pick a side.** `update` used to only download the pending-changes package and tell you to finish in the IDE; it now receives the changes into your local KB objects (pass `apply=false` for the old download-only behavior) and, when the server's changes collide with yours, leaves the conflicts flagged and lists them. `resolve` gained a `strategy`: **`mine`** keeps your local version (default, no credentials needed), **`theirs`** takes the server's version, and **`automerge`** does a 3-way merge of base + yours + theirs. Applying server changes talks to the GeneXus Server, so `update` and `theirs`/`automerge` need credentials — the server URL resolves from the linked KB automatically; supply the user/password via `GXMCP_TEAMDEV_USER` / `GXMCP_TEAMDEV_PASSWORD` (never pass secrets as plain tool arguments).
- **`genexus_gxserver commit` now reports what it committed.** A whole-changelist commit previously returned only `{committed: true}`; it now lists the objects that went in and the resulting remote version, so you can confirm the changelist actually reached the server.
- **`genexus_whoami` reports worker deaths and per-tool latency.** The worker health block gains a `deaths` summary — how many times the worker has exited, how many were unexpected (a real crash vs. a planned idle/recycle/shutdown), a breakdown by reason and exit code, and the most recent few with memory-at-death and the tool that was running — plus a `toolLatency` summary (per-tool call count, average, max, ranked by total time). The death history survives worker restarts (the worker's own debug log is wiped on every start), so a recurring crash is finally measurable, and latency shows where a session's time actually goes instead of guessing which tool is slow.

### Internal

- New `CrashLedger` (gateway) appends every worker exit to a ring-capped `%LOCALAPPDATA%\GenexusMCP\worker-crashes.jsonl`; `WorkerProcess` snapshots exit code + working set + uptime + last-op while the process is alive and records from `FireWorkerExitedOnce`. Idle-timeout resolution now honors `<= 0` as disabled (removed the `Math.Max(1,…)` floor); default `WorkerIdleTimeoutMinutes` 5 → 60. New `WorkerStopReason.HeapRecycle` + `Server.WorkerHeapRecycleMB` (default 1500); `WorkerProcess.ShouldRecycleForHeap` fires from the health check on an idle over-ceiling worker and eager-respawns. Worker-side `IdleMemoryMaintenance` thread runs one `GCLargeObjectHeapCompactionMode.CompactOnce` + collect per idle period (`GXMCP_IDLE_GC=0` opts out); `App.config` sets `gcConcurrent`. New `ToolLatencyStats` records end-to-end tool time in `SendWorkerCommandAsync` and emits `[TOOL-LATENCY]` lines. Proxy empty-body decision extracted to `Program.ProxyEmptyBodyIsSuccess`; forced promotion gated on a new `IsPortListeningAsync` liveness probe; `TryKillProcessOnPort` restricted to `GxMcp.Gateway` / `dotnet` processes. `GatewayProcessLease.WriteLeaseFile` now writes-temp-then-`File.Replace`/`Move` (atomic); the promotion-trigger request is buffered in `_promotionReplayLine` and replayed by the new master; the eager-respawn give-up became a bounded ~30-min slow-retry loop; the session-cleanup / lease-heartbeat loops start once (guarded, on a gateway-lifetime token) instead of once per HTTP bind-retry attempt. New test: `GatewayProcessLeaseTests.LeaseWrite_IsAtomic_*`. The ~88s Service-Manager warmup was investigated and confirmed intrinsic/unshrinkable (single-shot per process, unshareable) — no code change, it only reinforces keeping the worker warm. Measured baseline footprint: ~130 MB (small KB) / ~158 MB (38,655 objects, lazy enrichment), both flat at idle. New tests: `CrashLedgerTests`, `WorkerIdleTimeoutTests`, `ProxyPromotionTests`, `WorkerHeapRecycleTests`, `ToolLatencyStatsTests`.
- **gxserver write (headless Team Development).** `GxServerWriteService` routes commit through `ITeamDevClientService.SendChanges` (explicit `ObjectList` for partial), update through `JustReceiveChanges(ReceiveChangesData).Update()`, resolve through `GetConflictEntities`/`MarkAsResolved` + `IMergeService.MergeObjects` — all off `IGXserverService`, which does NOT resolve in a headless worker. Auth: `TokenAuthorizationManager.GetToken(new CommunicationData(TeamDevelopmentData{…}))` gets the GAM OAuth token → `SetDefaultAuthenticationToken` + the data objects' `AuthenticationToken`. **The OAuth username must be `AuthType\user` (e.g. `Local\2635801`)** — `TokenRequestBodyFields` splits on `\` and indexes `[1]`, so a bare username throws `IndexOutOfRangeException`; `AcquireAuthToken` prefixes `GXMCP_TEAMDEV_AUTHTYPE` (default `Local`) when the user has no domain. Conflict entity names resolved via `KBObject.Get(model, key).Name` (their `ToString` is the type). Gateway `GetToolTimeoutMs` gives `genexus_gxserver` 600s (long server ops); wedged detection made progress-aware (`WedgedSilenceSeconds`) so a multi-minute update isn't false-reaped. Verified live end-to-end on a GXserver-linked KB (commit / update ~850 objects / resolve). - **gxserver update/commit async.** `genexus_gxserver` with `async=true` on `update`/`commit` returns an `operationId` immediately (verified: 130 ms) and runs in the background; poll `genexus_lifecycle(action=status|result, target=op:<id>)` — same job path as async edits. Needed because a first update on a stale KB applies hundreds of objects over many minutes, well past any sync timeout. Reads (status/pending/conflicts/history) and lock stay synchronous.
- **Backlog hardening.** `genexus_worker_reload mode=hard` now actually swaps the worker binary: `DrainAndReplaceAsync` gained a post-drain hook that copies `sourceDir`'s `GxMcp.Worker.*` into the worker dir in the drain window (old worker exited, eager respawn suppressed) — previously `sourceDir` was ignored and the plain drain+respawn ran the old binary. `genexus_gxserver` is now a live tool (bypasses the semantic response cache) so `conflicts`/`pending` never return a stale snapshot after a commit/update/resolve. `AnalyzeService`'s inspect/impact caches sweep expired entries on write (past a 256 floor) instead of only evicting a key when it's re-read. `GxServerSyncService` resolves `ITeamDevClientService` through the self-healing `SdkServiceResolver` (the last raw `TryGetService` outside it). `genexus_search_source` Timeout envelope now reports `coveragePercent` and points at scoping (`objectName`/`typeFilter`/`pathPrefix` = O(scope) fast path) before resume — measured cost is ~58 ms/object (intrinsic SDK source read; the deep fix is source-text in the index, i.e. the index-optimization program); its `FindObject` lookup now passes the entry's type for the O(1) typed fast path.

## v2.19.0 — 2026-07-14

Agentic-DX fixes from a real session authoring a SOAP-exposed Procedure (issue #32).

### Added

- **`genexus_variable` batch add.** `action=add` now accepts a `variables` array —
  `variables:[{varName,typeName,length,decimals,collection}, …]` — adding every variable
  in one call with a single save instead of one round-trip per variable. The response
  reports a per-item outcome (`Added` / `Exists` / `Failed`) and aggregate counts, so a
  proc that needs eighteen variables is one tool call, not eighteen. The single-variable
  `varName` form is unchanged.
- **`genexus_gxserver` partial commit.** `action=commit` accepts an optional `targets`
  array to commit only the named pending objects, leaving everyone else's pending changes
  uncommitted — the same selective commit the GeneXus IDE allows. Object names must appear
  in `action=pending`; an unknown name refuses the whole commit rather than committing
  everything. Omitting `targets` keeps the previous whole-changelist behavior.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`VarChar` now persists as `VARCHAR`, not `CHARACTER`.** A variable requested as
  `VarChar(80)` was silently stored as `CHARACTER(80)`, which forced callers to `Trim()`
  padding when writing to a `VARCHAR2` column. `VarChar` is now its own type and round-trips
  to the SDK's `VARCHAR`. The same fix applies to attribute typing, which shared the type
  resolver and had the identical `VarChar → Character` flattening.
- **Spurious "object not found in the Knowledge Base" warning on a successful spec-check.**
  Spec-checking a freshly created object finished `Succeeded / 0 errors` but still emitted a
  warning claiming the object wasn't found — a misleading signal, since the object being
  specified plainly exists. That warning is now dropped when it names one of the objects
  being built.
- **`genexus_gxserver commit` after a worker restart no longer needs a manual reload.** When
  the worker restarted (e.g. the developer touched the GeneXus IDE), the write-side commit
  service could lag the read-side in the SDK's lazy service registration, so `commit` failed
  with `GxServerServiceUnavailable` while `pending` still worked — clearing only after a
  manual `genexus_worker_reload`. Commit and the other write actions now retry service
  resolution (and fall back to the forcing resolver) so a late registration self-heals.
  The same self-heal was applied to every tool that resolved an SDK service the same way
  and hit the same wall — `genexus_compare`, `genexus_gam`, `genexus_merge`, and
  `genexus_module` — so none of them require a manual worker reload after a respawn either.

### Changed

- **`init` registers detected AI clients by default.** Non-interactive `init` used to write
  only `config.json` and report `clientsPatchedCount: 0` unless `--write-clients` was passed,
  so the client still had to be wired up by hand. It now patches already-installed clients
  automatically; pass `--no-write-clients` to skip, `--all-clients` to write every known
  client, or `--clients <csv>` to pick. When nothing is patched, the output points at
  `GX_CONFIG_PATH` for a directory-independent global registration (now documented in
  `docs/environment_variables.md`).

### Internal

- Tool-schema token budget raised 13600 → 14100 for the new `genexus_variable` `variables[]`
  and `genexus_gxserver` `targets[]` fields (measured ~13856; ~244 headroom). Discovery
  golden fixture regenerated.
- `AddVariable`'s SDK construction extracted into shared `BuildResolvedVariableInto` /
  `AddInferredVariableInto` helpers, reused by the new batch path.
- New `SdkServiceResolver.Resolve<T>()` helper (bounded retry + forcing `GetService<T>`
  fallback) centralizes the lazy-SDK-service resolution that GxServer, Compare, GAM, Merge,
  and Module previously each open-coded as a single `TryGetService<T>()`.

## v2.18.0 — 2026-07-10

Second-pass codebase audit plus a large internal-hardening pass. Correctness, data-safety,
security, and performance fixes; a big round of behavior-preserving refactors and test/
tooling cleanup. No tool renames. The only behavior a normal caller notices is faster
search/list on large KBs, the new `Server.WedgedCommandTimeoutMinutes` knob, and
`warm_spares` now reporting its real outcome.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Incremental indexing of large sibling groups is no longer quadratic.** Adding an
  object to the parent-children index scanned the whole sibling list to dedup on every
  insert, so bulk/streaming indexing of a folder or table with thousands of children ran
  in O(n²). Dedup is now O(1) via a companion key-set maintained alongside the list, cutting
  the cost of warming or incrementally updating large KBs. No change to results or ordering.
- **Searches no longer re-scan the whole index to check enrichment state.** On a large KB,
  every filtered/`usedby` search walked the entire object index to decide whether to attach
  the `enrichmentPending` hint — worst case a full walk on each search once enrichment had
  already drained. The result is now cached against the index's mutation generation, so a
  stable index answers in O(1) and only genuine index changes trigger a rescan (which
  early-exits at the first un-enriched entry).
- **Type- and domain-filtered search/list are faster on large KBs.** These filters used to
  scan every object; the index now maintains secondary type/domain lookups so a filtered
  query starts from just the matching set. Results and ordering are unchanged (the previous
  full-scan filter is retained as a verified safety net).
- **Background writes are no longer silently lost when a commit fails.** The background
  flush caught commit exceptions at debug level and then cleared its "pending write"
  flag unconditionally — so a failed commit was never retried, even though the client had
  already been told the write succeeded, and a later worker recycle lost the change
  permanently. Commit failures are now logged as errors and leave the write pending so
  the next flush retries it.
- **Async operation status no longer gets stuck at "Running" after a transient worker
  crash.** When a tool call hit a worker crash mid-flight and was transparently retried,
  the retry's completion arrived under a fresh internal request id that was never linked
  back to the operation, so `genexus_operations status` (and `whoami`'s last-error
  surface) reported the call as perpetually running even though it had finished. The
  retry is now linked to its operation, and per-tool metrics count each call exactly once
  (the crash-then-retry no longer double-counts).
- **The CLI writes its own `config.json` atomically.** The KB catalog / active-KB
  pointer was written in place with a plain overwrite, so a crash or interruption
  mid-write could truncate it and lose the entire registered-KB list — while every
  third-party client config in the same module already used the atomic temp-file+rename
  helper. The tool's own state file now uses it too.
- **`genexus_worker_pool action=warm_spares` is stable when pre-warming more than one
  KB.** The pre-spawn result was collected into a non-thread-safe list from concurrent
  background callbacks, which could throw or drop entries once two or more KBs were
  configured as warm spares. Collection is now concurrency-safe.
- **Corrected a stale troubleshooting entry.** `TROUBLESHOOTING.md` documented a
  `GENEXUS_MCP_CACHE_DIR` environment variable that does not exist; following it
  silently did nothing. The entry now explains the real options for locked-down
  `%LOCALAPPDATA%` machines.
- **A wedged worker is now recycled instead of holding its slot forever.** If a worker
  process stayed alive but never answered an in-flight command (e.g. stuck deep in an SDK
  call), the gateway timed out the client's request but never reaped the worker — its slot
  stayed occupied until a manual close/reload. The health check now force-stops a worker
  whose oldest in-flight command has gone unanswered past a generous hard ceiling
  (`Server.WedgedCommandTimeoutMinutes`, default 15 min — well above any legitimate build).
  Idle workers with no in-flight work are unaffected.
- **`genexus_worker_pool action=warm_spares` reports the real pre-spawn outcome.** The call
  returned its `prespawned` / `skipped` lists before the background spawns had run, so it
  almost always reported nothing pre-spawned even as workers were coming up. It now waits
  for the spawns (bounded by a 10s cap) before reporting; a spawn still running past the cap
  is listed under `skipped` for that call but keeps coming up in the background.

### Security

- **`genexus_worker_reload` no longer builds its PowerShell helper command by
  interpolating the `sourceDir` argument.** The reload path spawned `powershell.exe` with
  the source/destination paths concatenated into the `-Command` string; a crafted
  `sourceDir` could break out of the quoting. The paths are now passed to the helper as
  process environment variables (never shell-parsed), and the script reads them via
  `$env:`.

### Added

- **`docs/environment_variables.md`** — a single reference for every runtime
  environment variable (HTTP token, GAM credentials, AI-completion proxy, build-path and
  diagnostic knobs), with purpose and default for each. Linked from `AGENTS.md` and
  `TROUBLESHOOTING.md`.

### Internal

- New `HttpTokenAuthTests` covering the `/mcp` auth primitives (loopback classification,
  constant-time compare, Bearer / `X-GXMCP-Token` parsing, wrong/empty/missing token) —
  the auth boundary previously had zero test coverage. The three helpers were widened
  `private`→`internal` (the test assembly already has `InternalsVisibleTo`), and the
  Gateway test project gained a `Microsoft.AspNetCore.App` framework reference for
  `HttpContext`.
- New `OperationTracker` regression test (`CrashThenRetrySuccess_UpdatesStatus_ButCountsMetricOnce`)
  pinning the crash-then-retry contract: status transitions to Completed while the tool
  metric is counted exactly once. Backed by a `MetricRegistered` guard on the operation
  record; retry-linked request ids are now also dropped by `CleanupExpired`.
- **God-object decomposition (behavior-preserving `partial class` splits).** `WriteService`
  6982→1804 lines across 7 partials; gateway `Program.cs` 5657→716 across 7 partials;
  `LayoutService` and `PatternApplyService` partially split (remaining cores tracked in
  `plans/`). Whole members moved verbatim; full suites green at every step.
- **`CommandDispatcher` switch → dispatch table.** The ~83-case `switch(method)` is now a
  case-insensitive handler dictionary; each case became a `Handle_<Name>` method, routing and
  unknown-method fallthrough preserved exactly.
- **Shared filter-predicate builder.** Extracted the genuinely-duplicated Search/List filter
  predicates into `IndexEntryFilterBuilder`, deliberately preserving the intentional
  Search-vs-List type-match divergence (alias-aware vs exact); characterization tests pin both.
- **Shared `PathSafety` helper.** Consolidated the several drifted "is this path inside the KB
  root?" / make-relative implementations; the by-design arbitrary-path sites (`genexus_io`
  export/import) were confirmed and left ungated.
- **Error-envelope normalization.** Several worker services that hand-built `{error,…}` /
  `{status:"Success"}` shapes now use canonical `McpResponse.Ok`/`Err` where the shape is
  backward-compatible; observable-shape sites were deliberately left alone. Dead legacy
  dual-shape parsing fallbacks (a v2.8.0 migration leftover) were removed after tracing each
  producer; the one still-live fallback is now documented rather than a bare TODO.
- **Index flush-count regression test** (`IndexFlushBoundTests`) pins the flush-write count
  under a burst, guarding future index-flush work against a re-serialize-per-tick regression.
- **BuildService characterization suite** (46 tests) and one brittle source-text guard replaced
  with a real behavioral test (`NormalizeFacadeArgs` dry-run mapping).
- **Repo tooling**: ESLint 9 flat config + `.editorconfig`, `nexus-ide` migrated to flat
  config, test package versions centralized via `src/Directory.Build.props`. Trimmed the
  accreting comment history in `ToolSchemaSizeTests` to a short rationale + pointer.
- **Deferred (tracked in `plans/`)**: the L-effort index-persistence re-architecture
  (incremental/sharded flush, batched COM reads) and the remaining god-object cores.

## v2.17.0 — 2026-07-10

Security and stability hardening from a codebase audit. No tool renames; the only
behavior change a normal caller sees is the new optional HTTP token and the `to`
argument on `genexus_kb_import`.

### Added

- **`genexus_kb_import` accepts an explicit `to` target KB.** Previously the import
  silently went to whichever KB happened to be first in the open-worker list. Pass
  `to=<alias-or-path>` to name the destination explicitly; omitting it keeps the old
  first-open/DefaultKb fallback for back-compat.
- **Optional shared-secret auth for the HTTP endpoint.** Set `GXMCP_HTTP_TOKEN` and
  every `/mcp` request must present it (`Authorization: Bearer …` or `X-GXMCP-Token`).
  Binding to a non-loopback address now *requires* a token — without one, `/mcp`
  requests are refused rather than silently exposing the full tool surface to the
  network. The default loopback (`127.0.0.1`) bind with no token is unchanged.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_kb_import` rejects path-traversal in `name`/`type`.** These arguments flow
  into filesystem delete/copy; values like `..\..\x` could escape the KB's `Objects/`
  tree and overwrite an unrelated directory. They are now validated against
  `[A-Za-z0-9._-]` with a path-containment check before any file operation.
- **Worker stays consistent under concurrent start/stop.** A worker restart raced with
  idle-timeout/health-check shutdown because the process and stdio handles were
  published outside the lock guarding them, which could surface as spurious "worker
  crashed" errors or dropped commands. The handle swap is now serialized.
- **Post-save index update no longer races the GeneXus SDK.** After a write, the
  background index refresh read live SDK object state on a thread-pool thread outside
  the SDK serialization gate; it now runs on the gated background queue, closing a
  crash/corrupt-read window under concurrent write load.
- **Worker reaping matches the whole KB path, not a substring.** Two KBs where one
  path is a prefix of the other (e.g. `…\Foo` and `…\FooBar`) could cause starting one
  worker to kill the other's live session. The match is now by the exact `--kb`
  argument.
- **A worker error now always returns a response.** An exception escaping command
  dispatch was logged but left the request unanswered, so the client waited out the
  full timeout; the worker now replies with an error envelope immediately.
- **Expired-operation cleanup can't drop a live operation's status.** On JSON-RPC id
  reuse within the retention window, cleaning up the old operation could delete the
  status mapping now pointing at a newer, running one; cleanup is now a
  compare-and-remove.
- **Per-target write serialization covers blank targets.** A write with an empty
  target string received its own unshared lock, silently disabling the serialization
  that prevents concurrent-write races on the same object; blank targets now share one
  lock.

### Changed

- **The AI-completion proxy no longer echoes raw upstream error bodies by default.** A
  failed `genexus_ai_complete` used to return the provider's raw error text (which can
  carry account/billing/request-id detail) into the transcript. It now returns a
  length-only breadcrumb; set `GXMCP_AI_COMPLETE_DEBUG=1` to include the raw body for
  local troubleshooting.

### Internal

- CI coverage gate (`scripts/coverage/assert-threshold.ps1`) honors the
  `worker.skipped.txt` / `worker.failed.txt` markers `collect.ps1` emits, so a
  GeneXus-less hosted runner enforces the Gateway floor instead of dying with a
  misleading "Coverage file not found"; failed collection now throws an actionable
  message.
- Added `ToolDefinitionsFixtureParityTests` — fails loudly when `tool_definitions.json`
  and the golden `tools-list` fixture disagree on the tool-name set or the fixture's
  sort order, instead of surfacing later as a confusing contract diff.
- CI now runs `npm run lint` for the Nexus IDE extension (it was configured but never
  invoked). `CONTRIBUTING.md` documents the coverage/contract/lint steps CI runs beyond
  the dev loop.
- New regression tests: KB-import traversal rejection, worker KB-path boundary match,
  reused-request-id operation-cleanup, and blank-target per-target lock sharing.
- Deferred audit findings (index-flush re-architecture, secondary search indexes,
  god-object decomposition of `WriteService`/`Program.cs`, dispatch-table refactor,
  BuildService test suite, repo lint/dep hygiene) are captured as self-contained
  handoff plans under `plans/`.

## v2.16.1 — 2026-07-10

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Reading a Smart Device Panel (`SDPanel`) no longer reports real content as empty.** An SDPanel's parts are WorkWithDevices projections, and the tool was looking them up with the Web panel's part identifiers, which never matched — so `part=Source` landed on the panel's (usually empty) rules part, and the layout/variables/conditions came back as a blank `<Properties />` that read like an empty object. Now: `part=Source` (and `Events`) returns the panel's **event code**; `SDEvents` and `SDRules` are listed in `availableParts` and readable by name; and reading `SDLayout` / `SDVariables` / `SDConditions` returns a clear note (`projected: true`) explaining the content is projected from the pattern and authored in the GeneXus IDE — a blank there does not mean the panel is empty.

### Internal

- SDPanel virtual-part GUIDs (`Artech.Patterns.WorkWithDevices.Parts.Virtual*Part`) mapped in `PartAccessor.GetPartGuid`; `GetDisplayPartName` no longer collapses the SD `ISource` parts to a single `Source`; `PartAccessor.IsWorkWithDevicesProjectionPart` gates the honest-read note in `ObjectService`. Added `GetPartGuid_SDPanel_*` unit tests.

## v2.16.0 — 2026-07-10

Follow-up on two v2.15 authoring sessions (issues #30 and #31): SDT element sizing, per-object validation, batch reads, no-op detection, and folder moves now behave.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **SDT element Length/Decimals are now settable.** Writing an SDT structure element as `Codigo : Numeric(9)` used to drop the size — the element stayed at the `Numeric(4)` default, which serializes as `xsd:short` and silently truncates any value over 32767. Two causes: the structure write only fired for `part=Structure` while `genexus_read` reports the part as `SDTStructure` (so the write was a silent no-op), and the parser never applied the length even when it ran. Both are fixed — `part=SDTStructure` now writes, and `Numeric(9)` / `Numeric(9.0)` / `Numeric(9,0)` all set length and decimals. Reads round-trip the size (`Codigo : NUMERIC(9)`).
- **Batch `genexus_read` no longer crashes.** `genexus_read targets=["A","B","C"]` failed with `BatchRead failed: Cannot access child value on Newtonsoft.Json.Linq.JValue`. The batch path expected each entry to be an object but the tool passes bare object-name strings; it now accepts both forms, so reading several objects in one call works. Individual reads were unaffected.
- **`genexus_lifecycle action=validate` now validates Procedure Source.** It always returned `ValidationSkipped: "Validation not applicable for this part type."` because the dispatch passed the action verb ("Check") where the part name belonged, so the lookup never matched a part. Validation now targets the object's `Source` (pass `part` for another part, e.g. `Rules`); with no `code` argument it validates the object's current Source in place, giving a lightweight per-object syntax check independent of a full build.
- **No-op edits report `WriteNoChange`.** When the content you write normalizes to exactly what's already persisted, the response now returns `code: WriteNoChange` with `changed: false` instead of a misleading `WriteApplied`, and the pre-write snapshot `.bak` is discarded rather than kept.
- **`persistedSnippet` shows the edited region.** The write-response snippet was always the first ~10 lines, so an edit lower in the part gave no signal. It's now centered on the first changed line, so the region you touched is visible.
- **`genexus_read` on an SDT with no `part` no longer errors.** It defaulted to `Source` (which SDTs don't have) and returned `Part 'Source' not found`. Reads now fall back to the object's primary part — `SDTStructure` for an SDT — when no part is given.
- **`patch` `{find,replace}` shorthand works when the client sends it as a JSON string.** Some clients serialize the nested `patch` object as a string (common when the find/replace text spans lines with CRLF); the shorthand then fell through to the bare-string path and failed with `Replace needs the text to find`. A string `patch` that contains JSON is now reparsed into the object form.
- **Moving an object to a folder no longer silently no-ops.** `genexus_properties action=set propertyName=Folder` returned `PropertyApplied` while the object never moved. Object folder/module placement is not writable through the GeneXus 18 SDK (the `Parent`/`Module` setters do nothing), so the call now fails loudly with `FolderMoveNotSupported` and points you to the IDE, instead of reporting a success that did nothing.
- **Created objects no longer land with `Integrated Security Level = (Unknown)`.** A raw SDK create left the property at an unresolved value the IDE rendered as "(Unknown)", instead of one of the real options (None / Authentication / Authorization). New objects are now normalized to `None` (the default when integrated security isn't enabled) on create, so the property panel shows a valid level. Objects that don't have the property (SDT, Domain, Theme, …) are unaffected.
- **The update check no longer reports an older version as "latest".** When the installed build is newer than the registry's published `latest` (a release live on GitHub but not yet on npm), `genexus_whoami` showed a confusing older `latestVersion`. It now reports the installed version as latest with a note that the feed is lagging; `updateAvailable` was already correctly `false`.

### Added

- **`genexus_create type=Folder` / `type=Module` are documented.** Both were creatable but only `Folder` worked and neither was listed; the `genexus_create` schema now names them and notes that objects cannot be moved into them via the tools (SDK placement is read-only).
- **Build/spec output flags likely-spurious spec errors.** When a build or spec-check reports `spc####` / `gen####` diagnostics, the envelope now carries a `specErrorsHint`: in an ungenerated or broken build environment the specifier can emit a spec error that is invariant to the Source (fixed line number, fires even on known-good objects). The hint says to regenerate the environment before treating it as an authored-code bug, and points at `action=validate` for build-independent Source checking. The error itself is never suppressed. When environment errors are present too, the hint flags the spec errors as likely environment-induced.

### Documentation

- **API-object routing grammar** is now written down in `AGENTS.md`: `Verb { <route> => <Object>; }`, one HTTP-verb block per API object (mixing verbs / `@`-decorators fails at spec — a GeneXus grammar limit, not the MCP), and use per-procedure REST to expose multiple verbs.

### Internal

- SDT length: `VariableTypeResolver` accepts `[.,]` as the length/decimals separator; `SdtDslParser` applies Length/Decimals via `AttributeTypeApplier` and serializes them; the DSL write interceptor accepts the `SDTStructure` part alias. No-op/snippet: `WrapWithPersistedState` takes a prior-source arg, computes `FirstDiffLine`, flips `WriteApplied`→`WriteNoChange`, and drops the snapshot; `EditSnapshotStore.SnapshotInfo` carries `PriorContent`. New tests: `VariableTypeResolverTests` (dot form), `PersistedSnippetTests`, `BuildErrorCategoryTests` (spec hint). Golden `tools-list` fixture regenerated.

## v2.15.0 — 2026-07-10

Second pass on the long-session report (issue #28): the remaining authoring and stability gaps. A spec-check that skips the full build, an API object type, no more phantom placeholder KB, and error text that keeps your casing.

### Added

- **`genexus_lifecycle action=specify` — spec-check without a full build.** Runs the Specify + Generate pass for a target and stops before Compile and deploy, so you see `spc*` / `gen*` diagnostics fast instead of waiting out (and reading through) a full build. Diagnostics come back under `codeErrors` (see v2.14.0's env/code split). If the in-process spec pass isn't available it reports that rather than silently falling back to a full compile+deploy.
- **`genexus_create` can create API objects.** `action=object type=API` scaffolds a GeneXus API object, so grouped-route REST services can be created through the MCP instead of only in the IDE.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **No more phantom placeholder KB.** The shipped fallback config carries a placeholder `KBPath` (`C:\KBs\YourKB` — an empty scaffold). It was being auto-migrated into a `yourkb` default that opened alongside your real KB, so every call failed with `Multiple KBs open (yourkb,…); 'kb' parameter is required`. A `KBPath` that isn't a real KB (missing, or no `.gxw` / `KnowledgeBase.Connection`) is no longer migrated — the only open KB is the one you actually open, so no `kb` argument is needed.
- **Error messages keep the authored identifier casing.** GeneXus lowercases identifiers in its diagnostics (`&Objcod` for a variable authored `&ObjCod`). Build errors now restore the casing the KB actually uses for `&`-prefixed identifiers, so the error matches what you wrote. Unknown identifiers and literal text are left exactly as emitted.

### Internal

- Long-session stability items #1/#2/#3 from the report (Service Manager warmup, mid-session disconnects on long blocking calls) are already covered by existing mechanisms and left unchanged: builds/index run as async jobs with `operationId`, `action=status wait=<sec>` bounds the blocking poll, a spec-compliant `notifications/progress` heartbeat keeps long synchronous calls from tripping the client timeout, and v2.14.0 made index status honest during warmup. No non-spec keepalive was added.
- `Configuration.LooksLikeKb` gates the legacy `KBPath`→`KBs[]` migration (new `ConfigurationParsingTests` for migrate-real + skip-placeholder). `BuildService.NormalizeErrorIdentifierCase` rewrites `&ident` tokens via the index's canonical name. `specifyOnly` threads `BuildService.Build` → `BuildTaskStatus.SpecifyOnly` → `InProcessBuildRunner.Run`, forcing the `ExecuteSpecifyOneOnly` path and refusing the MSBuild.exe fallback. `API` was already in `ResolveObjectTypeGuid`; only the schema advertised it. Golden `tools-list` fixture regenerated.

## v2.14.0 — 2026-07-10

Stability and authoring fixes from a long real-world session on a ~1200-object KB (issue #28): edits no longer stall behind a "not ready" index after a reconnect, declaring variables and SDTs takes fewer round-trips, and a failed build finally tells you whether it's your code or the environment.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Edits no longer blocked by `IndexNotReady` when the index is actually loaded.** After a reconnect the worker's index loads from its warm cache (log shows `Index loaded. Objects: 1191`), yet the first `genexus_edit` could still be rejected with `IndexNotReady` / `indexStatus: Cold` — and the only way to warm it risked a long blocking call. The index state is now hydrated from the loaded cache the moment it's queried, so the first status/edit after a reconnect reflects the objects already in memory instead of reporting `Cold`.

### Added

- **`genexus_variable` accepts `length` and `decimals`.** New variables no longer default to `Character(20)` — too short for API keys or message strings. Pass `length` (and `decimals`) to set the size directly; it overrides the length parsed from the type name. Applies to both `add` and `modify`.
- **`genexus_variable` accepts `collection: true`.** Declare a collection variable in one call instead of adding a scalar and then setting an undocumented property.
- **`genexus_variable add` with no type inherits a matching attribute's type.** Adding `&ObjCod` when an attribute `ObjCod` exists now bases the variable on that attribute (type, length, decimals) instead of falling back to a generic default.
- **`genexus_create` (SDT) can seed a real first field.** An SDT still needs at least one item to save, but instead of a throwaway `Item1 : VarChar(40)` you can pass `firstItem` and `firstItemType` so the seeded item is the field you actually want. Omit them for the previous default.
- **Build output separates environment errors from your object's errors.** A failed build now carries `envErrors` (missing generated sources, unresolved DLL references, locked outputs, NuGet restore — the KB can't compile in this environment) apart from `codeErrors` (the authored object's spec/`spc*`/C# errors), with counts for each. When a build fails on environment errors only, an `envErrorsHint` says so — no more mistaking `CS2001` / `MSB3245` infrastructure noise for a bug in the object you just edited.

### Internal

- `IndexCacheService.GetIndexState` triggers the lazy on-disk hydrate (which promotes `Cold` → `Ready`) before reading the state snapshot. `BuildService.ClassifyErrorCategory` buckets each raw error line (`environment` / `spec` / `code`); `BuildTaskStatus` exposes `EnvErrors` / `CodeErrors` / `EnvErrorCount` / `CodeErrorCount` / `EnvErrorsHint` as computed properties that serialize into every status/result envelope. `WriteService.AddVariable` / `ModifyVariable` and `ObjectService.InitializeSDTWithDefaultItem` gained the length/decimals/collection and first-item parameters, threaded through `OperationsRouter` and `CommandDispatcher`. Tool-schema budget 13300 → 13600. New `BuildErrorCategoryTests`; golden `tools-list` fixture regenerated.

## v2.13.3 — 2026-07-09

Index-status honesty + a "wait until ready" convenience, from a measured pass over the index lifecycle (issue #27 item 3). The re-walk/flapping that item reported is already handled by the persistent warm cache (v2.12/2.13) — reopening a large KB loads it instantly and a build no longer drops the index; these are the remaining rough edges around it.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Index status no longer reports 0 objects when it's actually ready.** When the index loads from the warm/delta cache (the normal path on reopen), `genexus_lifecycle action=status` reported `total: 0`, `processed: 0`, `objectsWalked: 0` and a blank status even though the index was fully `Ready` with thousands of objects — the "processed: 0 the whole session, impossible to tell progress" confusion. Status now reports the real object count and state in that case.
- **A read while the index is still warming gives an honest hint.** Reading an object by name before the index has populated returned "No similar names found in the index" — which implied the index had been consulted and the name truly didn't exist. It now says the index is still warming (and a direct lookup also missed), so you retry instead of concluding the object is absent. Reading by exact name never required a full index and still doesn't.

### Added

- **`genexus_lifecycle action=status wait=<sec>` blocks until the index is Ready.** With no `since` baseline, a status call with `wait` now returns the moment the index reaches `Ready` (or the timeout), so you can wait for a usable index in one call instead of hand-rolling a poll loop. Passing `since` keeps the existing change-driven behaviour for progress polling.

## v2.13.2 — 2026-07-09

Reliability + search-ergonomics pass from a long large-KB session (issue #27): a background build now always resolves to a real result, source search can be scoped to a single object and resumed, and a failed patch tells you enough to fix it in one retry.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **A background build always resolves to a terminal result.** After `genexus_lifecycle action=build`, polling `action=status` / `action=result` could report `running` / `Pending` forever even though the build had already finished — the background progress tracker could wedge (a recycled worker, a stalled pipe) and nothing ever flipped the job to its final state. Every status/result poll now re-checks the worker's real build state and settles the job to `succeeded` / `failed` on the spot. If the worker was recycled and its build outcome is genuinely unrecoverable, the job resolves with a clear "tracking lost — re-run to confirm" instead of hanging.

### Added

- **`genexus_search_source` can be scoped to specific objects.** Pass `objectName="MyProc"` (or a comma-separated list) to search inside just those objects instead of scanning the whole KB — a search inside one known Procedure is now proportional to that object, not to a 9,000-object catalogue, and it works for any object type, not only the default code types.
- **`genexus_search_source` is resumable and its budget is tunable.** When a scan hits its time budget it returns a `nextCursor`; pass it back as `startIndex` to continue where it stopped instead of rescanning from the top. `timeoutMs` lets you raise the per-call budget (default 30000) to cover more objects at once.
- **The last build result is one plain status call away.** `genexus_lifecycle action=status` (no target) now carries a `lastBuild` block — the outcome, error/warning counts and duration of the most recent build — so you can answer "did my last build pass?" without tracking the job id.

### Changed

- **A failed `genexus_edit` patch is always actionable.** When a patch's `context` doesn't match, the response now always carries something to correct with: the closest source windows (`nearMatches`) with a byte- and EOL-level diff when there's a near hit, or — when nothing is close — a concrete next step (re-read and copy one exact block, or anchor a single unique line with `Insert_After`). The near-match diagnostics now cover larger multi-line contexts too. The "context is required for Replace" error now spells out the exact shape to use, including the `patch={find,replace}` shorthand.
- **`genexus_lifecycle` build reports a realistic `estimated_seconds`.** Instead of a flat 60 (rebuild 120), the estimate is now the median of recent build times for that action, so the number tracks your KB instead of misleading you on a large one. The first build of a session still uses the default until there's history.
- **`genexus_read limit=0` truly reads in full.** An explicit full read (`limit=0`) is now honoured through the gateway instead of being silently re-capped at ~20 KB; it's still a clean line-aligned page with a safe continuation offset for a genuinely enormous part, so nothing is ever dropped from the middle.

### Internal

- Gateway `JobEntry` carries the worker build-task id; `McpRouter.ClassifyWorkerBuildStatus` is the pure reconcile decision (unit-tested in `JobReconcileTests`), invoked from the lifecycle status/result intercepts via `ReconcileJobWithWorkerAsync`. A worker "Task ID not found" is classified as tracking-lost, not a build error.
- `SourceSearchCriteria` gains `ObjectName` / `StartIndex`; `TimeoutMs` is now settable from the tool call. `objectName` scoping bypasses both the type whitelist and the literal pre-filter. Timeout/Cancel envelopes carry `nextCursor`; the success envelope's pagination block now reports the scoped `total` and `nextOffset`. Covered by `SourceSearchScopeTests`.
- `PatchService` near-match diagnostics: 50→120-line context cap and a `noNearMatchHint` fallback when no similar window is found.
- `BuildService.GetLatestBuildSummary()` (static, over the `_tasks` map) feeds the `lastBuild` block in `CommandDispatcher` GetIndexStatus. `BackgroundJobRegistry` records successful build wall-clocks per kind and exposes `EstimateBuildSeconds` (median, clamped 5–1800s); build-path routing still keys only on an explicit caller `estimated_seconds`, so the sync/async split is unchanged. `ReadPagination` sets `ExplicitFullRead` on `limit=0`, plumbed through `ObjectService` to a larger gateway source budget. Covered by `BuildEstimateTests` and `ReadPaginationDefaultsTests`.
- Tool-schema token budget 11400 → 11550 for the `genexus_search_source` scope params; golden `tools-list` fixture regenerated.

## v2.13.1 — 2026-07-08

Follow-up to the v2.13.0 Design System work: editing a Design System now actually saves, and a worker that shut down for inactivity comes back on the next call instead of erroring.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Editing a Design System's styles no longer silently no-ops.** Writing a Design System's `Source` with only a `styles { … }` block — or a combined `tokens { … } styles { … }` source in which only the styles changed — returned `WriteNoChange` and never persisted, so the object looked untouched in the IDE. The styles now save correctly. A write where neither the tokens nor the styles block changed still returns `WriteNoChange`, as expected.
- **A worker that shut down for inactivity is replaced on the next call.** After the worker idled out, the following tool call failed with `Worker for KB '…' crashed/exited` and no replacement was started, leaving the session stuck until a manual reconnect. The idle worker is now dropped cleanly the moment it stops, so the next call transparently spawns a fresh one.

### Internal

- `WriteService` DSO routing now compares each block against the persisted part and targets a block that actually changed, instead of always redirecting the combined-source write to Tokens (which let an unchanged-Tokens comparison short-circuit the save and drop a changed-Styles side-effect).
- `WorkerProcess.StopProcess` disposes the OS `Process` right after `Kill`, which suppressed the async `Process.Exited` event that dropped the pool entry. Exit is now signaled deterministically via `FireWorkerExitedOnce` (idempotent with the `Exited` handler). Adds `WorkerProcessExitNotificationTests`.

## v2.13.0 — 2026-07-08

Worker-reliability, KB-lifecycle, and DX pass on large KBs (issue #26): the worker comes back on its own, an opened KB stays put, `genexus_search_source` can no longer take the worker down, and Design System objects write their tokens and styles to the right place.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_search_source` no longer crashes the worker.** Source search was running on a background thread while reaching into the GeneXus SDK, which is single-thread-bound — every call killed the worker and cost a recovery cycle. It now runs on the SDK thread, so searching source is safe and repeatable, even on a large KB and while the index is still building.
- **The worker recovers on its own; no more phantom "respawning".** After a crash the gateway now retries the respawn and, if a health check finds no live worker, starts one — so you no longer get stranded watching `respawning` while nothing is actually coming up. Worker health reports the truth: `starting` when a process really is booting, `respawn_failed` (with the underlying error and a recovery step) when it isn't, and `no_worker` when no KB is open.
- **An opened KB stays open across a worker recycle.** A KB opened by alias or path used to become `Unknown KB '…'` after a build or worker restart, forcing you to reopen it before every call. The gateway now remembers KBs you've opened for the whole session and transparently re-attaches (respawning the worker on demand) instead of failing.
- **`genexus_edit` preserves your indentation.** A Replace whose anchor sat at a deep indent sometimes prepended that indent to every line of your content, stacking spurious tabs. Content is now written exactly as supplied.
- **`genexus_read` trims cleanly instead of dropping the middle of a file.** When a read is too large for the context budget, the gateway now keeps whole lines from the front and tells you the exact line offset to continue from (`gatewaySafeNextOffset`), so you can page through predictably — no more silent middle gap with an offset that pointed past it.
- **Design System objects write tokens and styles to their own sections.** Generating a Design System with a combined `tokens { … } styles { … }` source used to put the whole blob in the Tokens section and leave Styles empty. The MCP now routes the `tokens` block to Tokens and the `styles` block to Styles automatically; reading the object's source returns both, and `Tokens` / `Styles` are now addressable as individual parts.

### Changed

- **`genexus_kb action=open` makes the opened KB the active one.** `genexus_whoami` now reports the KB you're actually working against — the alias, its path, and how many workers are live — instead of the empty config scaffold.
- **`genexus_kb action=set_default` accepts any open KB.** You can promote a KB you just opened (including an ad-hoc one opened by path) to the default; it's added to the config so it survives a restart. Previously this failed unless the alias was already hand-declared in the config.
- **`genexus_doctor` takes an optional `kb`.** When more than one KB is open, pass `kb=<alias>` to choose which one to diagnose, instead of hitting an unresolvable "which KB?" error.
- **Partial index results always announce themselves.** While the catalogue is still being walked, `genexus_list_objects` marks the result `partial: true` and nulls out the misleading total; a filter that matches nothing during the walk says the type or folder may simply not have been reached yet, rather than implying it doesn't exist.

## v2.12.0 — 2026-07-08

Stability + agent-ergonomics pass on large KBs (issue #25): stop silent wrong answers, make index progress observable, keep reads whole, and survive worker crashes without a manual reconnect.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_search_source` no longer returns an empty "not found" for tokens that exist.** A search for text that lived in an object's body — but not in its name — was silently dropped for every Procedure, Data Provider, Web Panel, and Transaction, because a pre-filter treated the (never-populated) indexed snippet as proof of absence. The pre-filter now only skips an object when the index genuinely holds its body text; otherwise the full source is read. A zero result is now trustworthy.
- **Search works while the index is still building.** Instead of hard-failing with `IndexCold` until the entire catalogue is walked, `genexus_search_source` now scans the objects walked so far and marks the result `partial: true`. A zero result on a partial index comes back as `PartialIndexNoMatch` (never a plain empty success), so an in-progress index can't be mistaken for "the token doesn't exist."
- **`genexus_list_objects` no longer presents a partial catalogue as complete.** While the index is still walking, the page is flagged `partial: true` / `totalIsPartial: true` with `hasMore: true`, and a `typeFilter` / folder miss says the type or folder may simply not have been reached yet — instead of implying it doesn't exist. The misleading authoritative `total` / `hasMore: false` over the walked subset is gone.
- **Index build progress is observable.** `genexus_lifecycle action=status` reported `processed: 0` for the entire build on the default indexing path; it now advances with the objects walked and flags `totalKnown: false` while the grand total is still unknown, so a running percentage isn't computed against a moving target.
- **`genexus_lifecycle action=status wait=N` returns the moment the index changes.** Polling the index build with `wait` now blocks and returns as soon as the state transitions (e.g. still-walking → ready) or a progress tick lands, instead of ignoring `wait` and forcing a poll loop. Pass the returned `indexStatus` back as `since` to chain.
- **`genexus_read` no longer punches a hole in the middle of a file.** A source read that the worker had already paginated to ~200 lines / 16 KB could be char-sliced a second time by the gateway, dropping the middle and leaving `[... TRUNCATED BY GATEWAY TOKEN BUDGET ...]` at an unpredictable spot with pagination hints that pointed past the gap. The gateway now leaves an already-paginated page intact; when it must trim an opted-out full read (`limit=0`), it flags `truncatedByGateway: true` with a hint and no longer discards the file's tail.
- **A worker crash mid-read no longer forces a manual reconnect.** Read-only tools (`genexus_read`, `genexus_list_objects`, `genexus_inspect`, `genexus_query`, `genexus_search_source`, and similar) now retry once against the automatically respawned worker instead of surfacing `Worker … crashed/exited. Reconnect or try again.` Write and build tools are deliberately not auto-retried.
- **A respawned worker reuses the index instead of re-walking the whole KB.** After a crash, the replacement worker now reuses the persisted on-disk index (delta refresh of only what changed) rather than starting cold and re-walking every object — previously each reconnect cost another full walk on a large KB.

### Changed

- **`genexus_whoami` is lean by default.** It returns the live health blocks (KB, GeneXus, worker, index, database, update, next-step hints) without the ~3k tokens of static playbooks + skills catalog that used to ship on every call. Pass `verbose=true` once when you want the inline reference material; `genexus_doctor` remains the minimal connection + index health check.
- **`genexus_inspect` is token-bounded by default.** A default inspect (no `include` filter) no longer dumps the full, unpaginated Rules/Conditions/Events source; each part is capped with a `*Truncated` flag and a pointer to read the full text via `genexus_read` (paginated). `genexus_analyze mode=hierarchy` likewise caps its `calls`/`calledBy` lists so a heavily-referenced base object can't return hundreds of entries in one payload.
- **Oversize-response retry hints now match the tool.** When a response exceeds the context budget, the follow-up suggestion for `genexus_inspect`/`genexus_analyze`/`genexus_navigation` points at the levers those tools actually accept (`include=[...]`, a narrower target, or `genexus_read`) instead of `page`/`page_size` params they ignore.

### Fixed (agent-safety follow-up)

- **Edits no longer silently overwrite a change you made in the IDE.** `genexus_read` now returns a `versionToken`; pass it back as `baseVersion` on `genexus_edit` and the write is refused with a `StaleObject` error (with the current vs expected version and a re-read hint) if the object changed in between — for example, because you edited and saved it in the GeneXus IDE after the agent last read it. Previously such an edit could apply on top of the agent's now-stale copy and clobber your change. The check is opt-in per call (omit `baseVersion` to skip it) and never blocks a dry run.
- **"No callers" / "nothing uses X" answers are now verified, not assumed.** With lazy enrichment the index reports `Ready` while cross-reference edges are still filling in, so `genexus_analyze mode=callers`, `genexus_query usedby:X`, and `genexus_what_if` could return an authoritative-looking zero ("safe to delete / change") that merely meant "not enriched yet." These now cross-check the live SDK reference graph (callers) or flag `indexEdgesMissing` / `enrichmentPending` / `impactUnconfirmed` with a hint, so an unconfirmed zero can't be mistaken for a guarantee. Semantic `genexus_query` similarly flags when ranking ran before embeddings were ready.
- **Clearer, actionable errors on common failures.** `genexus_delete_object` (missing `confirm`), a busy/opening KB, and unimplemented analyze/scaffold modes now return a typed `code` + `hint` (+ a `nextSteps` follow-up where applicable) instead of a bare free-text string, so an agent can react without parsing prose.

### Added

- **Index enrichment progress.** The `Enriching` phase now reports a `progress` fraction and `etaMs` (previously only the earlier `Reindexing` phase did), so `genexus_lifecycle action=status` shows how far along enrichment is instead of a fixed "Enriching".

## v2.11.0 — 2026-06-19

### Added

- **Search inside WebForm layouts.** `genexus_search_source` now accepts `scope=["webForm"]` (or `["layout"]`), scanning the WebPanel/Transaction visual XML with the same line-numbered context as a source scan — find a control name, caption, theme class, or binding across the KB. Previously the only way to match WebForm content was `fields=["webForm"]`, which returned the whole XML blob with no line context, and a layout-only term was filtered out before its part was ever read.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Edit and save errors now show the real diagnostic instead of `{"message":"{"}`.** When the GeneXus SDK rejected an edit — invalid source syntax, a save that didn't persist, and similar — the error reaching the client collapsed to a literal `{"message":"{"}`, with the actual `src####` line/column diagnostic, error code, and fix hint all dropped. `genexus_edit mode=patch` and `genexus_io action=export_part` returned the same opaque string. The error now carries the SDK's real message, code, and hint, so a failed write is actionable in one read instead of a dead end. (Fixes the `{"message":"{"}` reports in issue #24.)
- **Editing is no longer blocked for minutes after an upgrade on large KBs.** Every MCP version bump changes the worker binary, which forced a full re-index of the whole KB on the next start; on a 38k-object KB that held all writes for the duration of the rebuild. When only the binary changed (the on-disk index format is unchanged), the worker now runs a bounded delta — re-indexing just the objects that changed since the last run, typically under a second — and re-baselines its cache to the new binary. Reads were always available during this window; now writes are too. `genexus_lifecycle action=index force=true` still runs the full rescan when you want enrichment-logic improvements applied to every object.
- **`genexus_edit` no longer reports `WriteApplied` when a source write persisted as empty.** As a safety net, a non-empty source edit that re-reads as an empty part now returns a `WriteNotPersisted` error with a recovery path (restore via `genexus_history`, or retry once the KB is idle) instead of a false success, and a follow-up edit of the same object is no longer stuck on a phantom `WriteNoChange`. (Addresses the silent empty-write + `WriteNoChange` loop in issue #24.)
- **`genexus_edit validate=best-effort` no longer times out on large WebForm/PatternInstance writes.** A full visual or pattern write used to re-read and diff the persisted XML on every save to verify it landed; on large WorkWithPlus PatternInstance bodies that re-read dominated the call and tripped client timeouts. `validate=best-effort` now skips the post-write XML diff (a genuine SDK save error is still surfaced) — `validate=strict` (the default) keeps the full verification. Build afterward to confirm generation.
- **`genexus_apply_pattern` reapply gets the time it needs before the client gives up.** Reapply runs the WorkWithPlus projection step, which on a large host or an object the IDE is holding open takes minutes; the gateway was cutting the request off at 60 seconds while the worker was still legitimately working. The gateway ceiling now matches the worker's reapply window (`GENEXUS_MCP_REAPPLY_TIMEOUT_MS`, default 5 minutes) plus a cushion, so a slow-but-progressing reapply returns its real result — including the `slowReapply` / `recoveryRequired` hints — instead of a bare transport timeout.

### Internal

- Gateway `McpRouter.TrimErrorEnvelope` / `AttachSuggestedNextStep` resolve `error.{code,message,hint}` from the canonical v2.8.0 `error` sub-object before the legacy top-level fallback. The old `error["message"] ?? error["error"]?.ToString()` returned null then serialized the entire sub-object — whose first line is `{` — producing the `{"message":"{"}` that masked every SDK diagnostic. New `ResolveErrorField`/`ResolveErrorMessage` helpers; regression tests in `TerseErrorTests` (canonical envelope + legacy bare-string shapes).
- Post-upgrade warm start: `Configuration.DeltaAcrossWorkerDll` (default on) + `OnDiskCacheValidation.CanDeltaAcrossDll` gate a delta refresh when only the worker-DLL hash changed (`SchemaMatch` still forces a full rebuild on an index-layout change). `KbService.BulkIndex` fast-path takes the delta and `StartDeltaRefreshThread`'s `WriteMetaSidecar` re-baselines the DLL hash. New tests in `IncrementalIndexValidationTests`.
- BulkIndex now returns the canonical `McpResponse` envelope (`{status:"ok", code, result}`) on every path (`LiteStarted` / `Started` / `AlreadyIndexed` / `AlreadyInProgress` / `DeltaStarted`), replacing the four ad-hoc raw `{"status":...}` strings; the gateway index-bootstrap reads the fresh-vs-warm signal from `code` with a legacy `status` fallback. Resolves the v2.10.0 "BulkIndex status strings left as-is pending gateway contract-test alignment" note.
- `validate` is threaded into the WriteService write pipeline as a `strictVerify` flag (`WriteObject`/`WriteObjectInternal`/`WriteVisualPart`/`WritePatternPart`). New tests: `WriteServiceFacadeArgsTests` validate→strictVerify mapping, `GatewayBudgetTests` apply_pattern timeout window. Golden tools-list fixture regenerated for the `genexus_search_source` scope/fields and `genexus_edit` validate descriptions.
- `PatchService.ParseWriteResult` now bridges the canonical write envelope (`status:"ok"`/`code:"WriteApplied"`) to the legacy `_internalStatus`/`message` fields the patch flow reads, so a clean canonical write is recognized as success instead of being forced down the fallback re-verify/rollback path on every patch (latent since the v2.8.0 envelope migration). `WriteService.ApplyEmptyPersistGuard` / `ShouldRejectEmptyPersist` add the empty-persist safety net (`WriteNotPersisted`) and clear a per-target flag that previously let an empty in-memory part lock the caller into `WriteNoChange`. `Logger` gains an opt-in `GXMCP_SYNC_LOG=1` mode (synchronous file append) for capturing the last step before a hard worker crash. New tests: `PatchParseWriteResultTests`, `EmptyPersistGuardTests`.

## v2.10.0 — 2026-06-11

### Added

- **Multi-agent lock enforcement on writes.** When another agent holds an advisory lock on the target (via `genexus_multi_agent_lock`), write operations now return a typed `TargetLockedByOtherAgent` error with the holder id and remaining TTL instead of silently overwriting. Pass `force=true` to override. Previously the lock tool existed but no write path consulted it.
- **Explicit base64 writes.** `genexus_edit` accepts `encoding:"base64"` for binary-safe payloads. The legacy auto-detection now only fires when the decoded bytes round-trip as valid UTF-8, and every auto-decode is flagged with `decodedBase64: true` in the response — content that merely *looked* like base64 (hashes, tokens) can no longer be silently corrupted.
- **Restore hint on verification failures.** When a visual or pattern write commits but post-write verification finds a mismatch, the error now includes the pre-write snapshot reference and a ready-made `genexus_history action=restore discard=true` next step, so the agent can undo the write in one call.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_worker_reload` no longer leaves the session with a dead pipe.** Reload is now orchestrated by the gateway: tool calls that arrive during the swap wait in a queue instead of being routed to the exiting worker, and the reload response returns only after the replacement worker is SDK-ready (`swappedAndReady: true`). The old "reconnect the MCP client after reload" workaround is no longer needed.
- **Worker respawn loops eliminated.** A worker that exited on purpose — idle timeout, explicit `genexus_kb action=close`, gateway shutdown, or a "KB already open in another instance" rejection — was treated as a crash and respawned, in the busy-KB case in an infinite loop that could kill the legitimate sibling worker. Exit intent is now threaded through the lifecycle and deliberate exits stay down.
- **The on-disk index can no longer go permanently stale.** The index metadata sidecar was written even when the index body flush had failed or was still in flight, so the next warm start trusted a high-water-mark the body didn't contain and skipped those objects forever (only a `force=true` rebuild recovered). The sidecar is now written only after a durably confirmed flush.
- **Index flush throttling no longer drops trailing writes.** A change landing inside the 30-second throttle window was held in memory with nothing re-arming the flush — if the process exited, the change was lost. The throttle is now a proper trailing-edge debounce.
- **Object replacements are reconciled on warm start.** The deletion sweep only ran when the object count shrank, so deleting one object and creating another between sessions left a ghost entry in the index indefinitely. The sweep now compares the actual object sets.
- **Background indexing no longer downgrades enriched entries.** The streaming publish during the initial catalogue walk rebuilt the whole index from stubs, silently demoting objects that had already been enriched on demand (and doing O(N²) work on large KBs). Publishing is now incremental and never overwrites an enriched entry with a stub.
- **Failed enrichment is retried instead of being marked done.** A transient SDK error during on-demand enrichment (object locked, KB busy) permanently flagged the object as enriched for the session, so impact analysis ran against an entry with no call-graph edges. The enriched flag is now set only on success.
- **Intermittent index-save failures under load fixed.** Call-graph edges were mutated in place while a background flush serialized the same lists, producing "Collection was modified" save failures or torn snapshots. Edge lists are now replaced copy-on-write, making concurrent flushes safe.
- **SDK access is serialized across background work.** The catalogue walk, on-demand enrichment, delta refresh, file watcher, and tool commands each ran on their own thread against the thread-unsafe GeneXus SDK — the likely source of sporadic unexplained errors during indexing. All SDK-touching paths now go through a single gate.
- **Opening a KB no longer blocks every status probe.** The multi-minute `KnowledgeBase.Open` held the service lock, so `doctor`/`whoami`-style calls hung instead of answering; a second concurrent open now gets an immediate `OpenInProgress` response.
- **JSON-RPC conformance.** Unknown methods now return `-32601` (previously: no response at all, leaving the client waiting), malformed input returns `-32700`, internal failures return `-32603` with the request id, and `notifications/cancelled` is honored. `initialize` negotiates the protocol version with the client instead of always returning a fixed one, and no longer advertises the unimplemented `resources.subscribe` capability.
- **Port-conflict recovery can no longer kill unrelated processes.** Freeing the HTTP port used a substring match over netstat output (`:5000` also matched `:50001` and remote addresses) and killed whatever it found. It now resolves the exact local listener and only terminates the MCP's own gateway/worker processes.
- **`genexus_refactor action=RenameAttribute` is restartable and honest about partial failure.** Call sites are patched before the attribute itself is renamed, every touched object is snapshotted and recorded, and a mid-run failure returns a `partial` envelope listing `patched[]`/`failed[]` sites instead of a generic error over a half-renamed KB.
- **`genexus_refactor action=ExtractProcedure` now actually creates the procedure.** It wrote to a procedure object that didn't exist yet, so extraction always failed; it now creates the object first, writes the extracted code as its source, and replaces the block in the caller.
- **Layout edits get the same guard-rails as source edits.** `genexus_layout` mutations now take the per-target lock, snapshot before writing (so `genexus_history` restore covers them), and surface save errors that were previously swallowed.
- **Validation bypass is no longer reported as a clean write.** When a save only succeeded after retrying with validation disabled, the response said plain success; it now carries the retry strategy and a warning to build-verify.
- **Fast-path saves check compiler messages.** The fast source-save path reported success on a bare save without consulting SDK messages; errors now surface instead of first appearing at build time.
- **Concurrent-edit clobbering detected in WebForm edits.** A read-modify-write that raced another edit silently overwrote it; it now returns a typed stale-write error so the agent can re-read and retry.
- **Diagnostic raw-entity saves disabled by default.** Three undocumented reflection-based writes into non-public SDK persistence ran on every visual edit (left over from a past investigation); they are now opt-in via `GXMCP_WEBFORM_SAVE_DIAGNOSTICS=1`.
- **`start_mcp.bat` works from any install location.** The launcher hardcoded the original build machine's directory and overrode `GX_CONFIG_PATH` unconditionally; it now resolves paths relative to its own location and respects a pre-set `GX_CONFIG_PATH`.
- **CLI no longer risks corrupting the MCP stream on a late error.** An unhandled-rejection envelope was written to stdout even in server mode, where stdout is the JSON-RPC channel; it now goes to stderr.

### Changed

- **Large-KB index saves are streamed.** The index snapshot was serialized into a single in-memory string (~45 MB on a 38k-object KB) before compressing; it is now streamed straight into the gzip writer, removing the allocation spike on every flush.
- **Gateway log is rotated.** The debug log is written through a persistent writer and capped at 10 MB with one rotation file, instead of growing without bound via per-line file appends.
- **Release integrity chain.** The `publish.zip` SHA-256 is committed in the tagged release commit, the publish workflow verifies the uploaded asset against it before npm-publishing, the installer verifies the download before extracting, binary versions inside the zip are asserted to match the release version, and CI actions are pinned to commit SHAs.
- **Smaller, cleaner npm package.** The package no longer ships the GeneXus `Definitions/` tree (~20 MB of proprietary XML — the worker resolves it from your local GeneXus install, which was already required), and declares `"os": ["win32"]` so non-Windows installs fail early with a clear message instead of at runtime.
- **Config backups are pruned.** MCP-client config backups (`.bak`) are now capped at the 5 most recent per file instead of accumulating forever.
- **Leaner tool catalogue.** Five niche tools (`genexus_ai_complete`, `genexus_github`, `genexus_multi_agent_lock`, `genexus_rename_across_kb`, `genexus_worker_pool`) are no longer advertised in `tools/list` — they still work when called by name, but no longer cost schema tokens in every session. `genexus_rename_across_kb`'s KB-wide call-site patching is documented on `genexus_refactor`, which performs the same operation.
- **Clearer tool schemas.** `genexus_query`, `genexus_search_source` and `genexus_analyze` now state the index-readiness precondition in their descriptions (with a pointer to `genexus_lifecycle action=status`); `genexus_structure`/`genexus_properties` cross-reference `genexus_layout` for layout-control work; redundant `target` alias parameters and experimental flags were removed from the advertised schemas; terse parameter descriptions in `genexus_versioning`/`genexus_io`/`genexus_telemetry` were rewritten; `genexus_edit` is now correctly annotated as destructive.

### Internal

- Resumable `release.ps1`: re-running after a mid-release failure resumes from the failed step (existing tag without a release, version-bump-only dirty tree) instead of aborting.
- Worker envelope contract: dispatcher-level wrapping of non-canonical `status` values into the standard envelope for `ping`/cancel/probe paths; `BulkIndex` status strings intentionally left as-is pending gateway contract-test alignment.
- New `WritePipeline` helper centralizes snapshot + per-target lock + dirty-tracking for WriteService, LayoutService and RefactorService; pattern-debug instrumentation moved to a `WritePatternDiagnostics` partial.

## v2.9.1 — 2026-06-09

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **The MCP server no longer shows "parou de responder" / "stopped responding" while idle.** The host's periodic keepalive `ping` was processed in the same single-file queue as tool calls, so a long-running request (a cold start, an index build, an edit reapply, or a background index refresh) blocked the gateway from answering the ping until it finished — and the IDE declared the server unresponsive even when you weren't actively using it. Pings and other lightweight protocol messages are now answered immediately regardless of what heavier work is in flight.

## v2.9.0 — 2026-06-03

### Added

- **Incremental warm-start indexing.** Opening a Knowledge Base now validates the index already on disk and refreshes it incrementally — only objects changed since the last index are re-read — instead of rebuilding the whole index from scratch on every start. On a large KB this turns a multi-minute re-walk on each open into a sub-second update. The cached index is validated against a schema version, the worker build, and a last-change high-water-mark; a mismatch (or a missing/partial cache) triggers a clean rebuild. Toggle with `Indexing.UseDeltaOnOpen` in the worker config (on by default).
- **Lazy, on-demand enrichment.** The full object catalogue is usable as soon as the fast indexing pass finishes. The heavier per-object analysis (call-graph edges, source snippets, semantic vectors) is now computed on demand the first time a tool needs a given object, instead of an eager pass over the entire KB that delayed readiness for minutes on large KBs. Toggle with `Indexing.LazyEnrichment` (on by default); set it to `false` to restore the eager full-KB pass.
- **Live index updates within a session.** External edits to KB objects detected while the server is running now update the in-memory index immediately. Renames collapse to a single entry (tracked by the object's stable id, not its name), and objects deleted outside the session are reconciled on the next open.
- **Index-build timing diagnostics** in the worker log: a single-line cold-start breakdown (service-manager warmup vs SDK init vs KB open), a time-to-usable marker, a catalogue-pass split of property-read vs snapshot-flush time with per-object-type counts, an enrichment sub-step split, and per-flush serialize/compress/write durations — so a slow start can be attributed from one log read.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Index builds no longer thrash the disk.** While enriching a Knowledge Base the server was re-serializing and rewriting the entire index after nearly every object — hundreds of full rewrites on a large KB, each one slower as the index grew, competing with the build for CPU. These writes are now throttled, with a single final write when the build completes, removing the bulk of the redundant work.
- **`genexus_analyze mode=impact` no longer reports "Low" risk when it has no signal.** When the search index held an object but carried no call-graph edges for it (not yet enriched, or a stale snapshot), impact analysis returned `blastRadiusScore: 0, riskLevel: "Low"` — indistinguishable from a genuinely safe change, and the reason it could claim "0 affected" for an object that clearly had callers. It now cross-checks the live SDK reference graph (the same source `genexus_inspect` uses): edges the index missed are surfaced under `sdkCrossCheck` with `indexEdgesMissing: true`; a genuinely empty graph is reported as `riskLevel: "None", verifiedZero: true`; and when nothing can confirm the result, it returns `riskLevel: "Unknown"` instead of a misleading "Low".
- **`genexus_analyze` and `genexus_inspect` now resolve an ambiguous name to the same object.** A bare name that matches both a Transaction and its generated Table (e.g. `"Acao"`) was resolved nondeterministically — `inspect` could land on the Table while `impact` preferred the Transaction, so the two tools appeared to contradict each other. Resolution is now deterministic: editable logic objects (Transaction/Procedure/WebPanel/…) rank above the generated Table/View, with a stable tiebreak. `genexus_inspect` also returns `resolvedAs` and `alsoMatches` whenever a name spans multiple types, and `genexus_analyze mode=impact` echoes the `resolvedType` it analyzed.
- **`genexus_doctor` no longer falsely reports "GeneXus SDK install not found / CRITICAL".** Doctor only checked the `GX_PATH` environment variable, which the gateway never sets (it launches the worker with `GX_PROGRAM_DIR`), so the triage tool screamed CRITICAL while the worker was happily serving the KB. It now resolves the SDK from `GX_PATH`, then `GX_PROGRAM_DIR`, then a loaded `Artech.*` assembly, and reports which `source` it used. Doctor's reported version now matches `genexus_whoami` (it reads the server version the gateway stamps into the worker, instead of the worker assembly's own — sometimes stale — version).
- **`genexus_db action=optimize_suggest` no longer grinds through the whole Knowledge Base.** For a single target it used to read the Source + Events of every Procedure/WebPanel/DataProvider in the KB on one thread — thousands of round-trips that could hang the worker on a large KB. It now scopes the scan to the objects that actually reference the transaction (via the index call-graph) and caps any fallback full scan, reporting `scan.scoped`, `scan.scannedObjects`, and `scan.truncated` so a capped result is never mistaken for a complete one.

### Changed

- **The on-disk index cache path is now resolved deterministically.** A build and a later warm start could previously compute different cache locations (one under the application folder, one under the user profile), so a freshly built index wasn't found on the next start and was rebuilt from scratch. Both paths now agree, so the persisted index is actually reused across restarts.
- **`genexus_db action=sql_ddl` now labels how trustworthy its output is.** Structure-derived DDL (the common case, when no native reorg SQL is available) is tagged `accuracy: "heuristic"` with a note that column types/lengths and the primary key are reliable but composite indexes, foreign keys, check constraints and storage clauses may differ — plus a `verifyVia` pointer to `action=reorg` for the authoritative statements. Native reorg SQL is tagged `accuracy: "exact"`.

## v2.8.4 — 2026-06-02

### Changed

- **Leaner install payload.** The published bundle no longer ships debug symbols (`.pdb`), trimming the download. The fallback `config.json` shipped in the package is now a sanitized placeholder — earlier builds could embed the developer's real Knowledge Base path; the released artifact never contains a real KB path.
- **Update checks now use the npm registry as the source of truth.** Both the CLI and the in-session gateway notification previously asked GitHub for the "latest" release, but you install from npm — so right after a release (GitHub tag created, npm publish still running) the check would advertise a version `npm install` couldn't yet fetch, and on networks that block `api.github.com` it never worked at all. The check now reads the npm `dist-tags` (with GitHub as a fallback), so "update available" means a version you can actually install, and it works behind proxies that allow npm. The release-notes link is derived from the version.
- **`genexus-mcp update` is now install-method-aware.** It detects how your AI clients launch the gateway and reports the right upgrade path: clients launched via `npx genexus-mcp@latest` **auto-update on restart** (just restart — no command), a global npm install gets `npm install -g …@latest`, and a fixed-path/corporate install gets the installer one-liner. Drift detection (a client pointing at a gateway that isn't this package) now covers any launcher, not just `.exe`.

### Added

- **`genexus-mcp update --apply` performs the upgrade** for your install method (with a confirmation prompt; `--yes` for unattended/CI). `--channel <tag>` checks a specific npm dist-tag (e.g. `--channel next`).
- **Corporate fixed-path installs now self-update in the background.** This is the install type the `npx @latest` launcher can't keep current. The gateway downloads the new `publish.zip` (verified against a published SHA-256), stages it next to the install, and applies it on the next launch — the running session finishes on the current version and the new binary loads when the AI client next starts it. It only activates for installs materialized by `scripts/install.ps1`; it's fail-safe (a locked file or any error leaves the install untouched and retries next launch) and can be turned off with `GENEXUS_MCP_NO_SELF_UPDATE=1`. `genexus_whoami.update.staged` reports a build waiting for restart.

### Internal

- CLI and gateway now share the update-check cache (`%LOCALAPPDATA%\GenexusMCP\update-check.json`), so a check by either side serves the other. Pure-function coverage added for install-method detection and the per-method upgrade plan.

## v2.8.3 — 2026-06-02

### Added

- **`genexus-mcp clients` — see every AI agent at a glance.** A read-only report of each supported agent: whether it's installed, whether `genexus` is registered, the config path, and the launcher command it points at. It flags a **stale** registration whose command points at a launcher (`.exe`, `.bat`, `.cmd`, …) that no longer exists on disk — the classic "Failed to connect / still on old version" cause — with a one-line fix. `genexus-mcp clients add --clients antigravity,vscode` registers specific agents and `genexus-mcp clients remove --clients cursor` unregisters them, without re-running the whole `init`.
- **`genexus-mcp doctor` now reports client registration.** A new `clients_registered` check summarizes how many agents are installed vs registered and warns (with the exact `clients add` command) when an installed agent is unregistered or points at a missing exe.
- **VS Code and VS Code Insiders are first-class registration targets** (Windows, macOS, and Linux). `init` now writes the native MCP entry to `Code/User/mcp.json` (and the Insiders variant) using the `servers` schema VS Code expects. Previously only the standalone build-from-source installer touched VS Code, so corporate/npm installs never wired it up.
- **OpenCode Desktop is detected and surfaced.** It's reported as installed with a one-line note on how to add the server from the app (its config schema differs from the OpenCode CLI, so it's never written blindly). The OpenCode CLI target is now labeled "OpenCode (CLI)" and an existing `opencode.jsonc` is honored.
- **Antigravity's unified config location is supported.** When `~/.gemini/config/mcp_config.json` already exists (the newer shared Antigravity location), the entry is written there; otherwise the IDE-specific `~/.gemini/antigravity/mcp_config.json` path is used.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **The init wizard now detects installed AI agents that haven't created an MCP config yet.** Agents were marked "not detected" whenever their MCP config file was absent — but Antigravity doesn't create `mcp_config.json` until you add a server, so a freshly installed Antigravity always showed as not detected and was skipped. Detection now keys off the agent's own install footprint (e.g. `…\Programs\Antigravity`, `~\.antigravity`), so the wizard offers to register it and creates the config for you. When an agent really isn't found, the prompt now shows where it looked.
- **Client configs are backed up and written atomically.** Before modifying any AI client config the installer now writes a timestamped `.bak`, and the new content is staged to a temp file and renamed into place — so a crash mid-write can no longer leave a client's config truncated. After writing, the entry is read back to confirm it landed; a silently-corrupted write is now reported as a failure instead of a success.
- **Commented (JSONC) client configs are no longer treated as corrupt.** VS Code's `mcp.json`/`settings.json` and OpenCode's `opencode.jsonc` allow `//` and `/* */` comments; registration now parses these instead of failing. (Comments are not preserved when the file is rewritten.)

### Changed

- **One installer flow, one source of truth.** Both PowerShell installers now delegate all AI-client registration and removal to the `genexus-mcp` CLI, so the agent list, paths, config shapes, and detection live in one place. This removes long-standing drift where the build-from-source installer wrote a different server key, pointed Codex at a dead HTTP endpoint, and registered Cursor through the wrong extension — none of which the uninstall could later clean up.
- **Registering a client now replaces any legacy `genexus18` entry instead of leaving a duplicate.** Upgrading from an older build-from-source install previously left both `genexus` and `genexus18` servers wired up, causing duplicate/colliding tools. Both the write and `genexus-mcp uninstall` now clean up the legacy key across `mcpServers`-, `servers`-, and OpenCode-style configs.
- **GeneXus auto-detection in the build-from-source installer probes both registry layouts** (`Artech\GeneXus 18` + `InstallationDirectory` and the legacy `Artech\GeneXus\18.0` + `InstallPath`) and only accepts a folder that actually contains `genexus.exe`, matching the CLI's discovery logic.

### Removed

- **The build-from-source installer no longer packages/installs the VS Code extension.** That extension was unmaintained; the installer now focuses on building the gateway/worker and registering AI clients. (VS Code is still wired up as a native MCP client via the step above.)

## v2.8.2 — 2026-05-30

### Added

- **Worker startup diagnostics in `worker_debug.log`.** On open, the worker now logs the active environment's data store (`[KB-OPEN-DATASTORE]` — type / server / schema, read from metadata only, no connection) plus a single `[COLD-START] totalMs=…` line covering Service-Manager warmup + SDK init + KB open. A slow or hung startup — e.g. one blocked reaching an unreachable database server during open — can now be diagnosed from the log alone instead of by guesswork.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`read`, `query`, `list_objects`, and object creation no longer get stuck on `IndexNotReady` / `totalObjects: 0` after a KB finishes indexing.** The v2.8.0 canonical-envelope migration wrapped the worker's index-state reply one level deeper (`result.result`), but the gateway's internal refresh still read the old top level — so it saw `status: "ok"` and `totalObjects: 0` and fast-failed every SDK-bound tool, even while `genexus_lifecycle action=status` correctly reported the index as ready with all objects. The gateway now reads the nested payload. Backward-compatible with the pre-2.8.0 reply shape.
- **The active data store (DBMS dialect) now resolves for database-aware tools.** Datastore enumeration relied on SDK accessors (`Parts.Get("DataStores")`, `Environment.DataStores`, `TargetModel.DataStore`) that come back empty on many KBs, so the DBMS family silently fell back to a hardcoded default and the new `[KB-OPEN-DATASTORE]` diagnostic showed `<unresolved>`. It now reads the data store through the correct `DataStoresPart` model part — searching every environment model — and reads the DBMS off `GxDataStore.Dbms` directly, so the real dialect (e.g. Oracle) is resolved instead of guessed.
- **The one-time "background indexing started" notice fires on first open again.** The cold-start banner only matched the legacy full-index reply (`Started`), not the default lite-index path (`LiteStarted`), so on most KBs it silently never appeared. It now fires for either path (and stays quiet on warm starts).

### Changed

- **SDK-bound tools self-heal instead of waiting for a manual `whoami`.** When the gateway's index mirror reports "not ready", a blocked tool now does one bounded synchronous refresh against the worker and re-checks before returning `IndexNotReady` — so a ready index that the mirror simply hadn't caught up to no longer leaves the agent stuck until it manually re-runs `whoami`. The refresh reads the worker's off-thread index state, so it stays fast even mid-indexing, and is skipped when the mirror was just refreshed.
- **`genexus_doctor` always runs, even while the index is building.** It previously fast-failed with the generic `IndexNotReady` envelope during indexing. It now reaches its health report, which reads the on-disk snapshot and returns a precise `SearchIndexMissing` / `SearchIndexEmpty` (with retry hints) when appropriate — making it a reliable diagnostic and an escape hatch when the index state looks wrong.

### Internal

- Extracted index-state and database-info parsing into testable `ApplyIndexStateFromWorkerResult` / `ExtractDatabaseInfoFromWorkerResult` seams, with regression coverage for the canonical-envelope, flat-legacy, and string-`data` payload shapes. Index-readiness gating consolidated into `IsIndexUsableForReads`.
- Audited every gateway-internal worker round-trip for the same envelope-nesting class: index-state and database-info were the only two affected (Build start/status and List/Objects stay flat; async-edit completion detection already descends correctly).
- Serialized the tests that touch the process-wide `_lastKnownIndexState` mirror into a non-parallel collection, fixing a latent cross-class flake exposed by the new coverage.
- Database-info refresh hardening: unwrap the v2.8.0 canonical envelope (`ExtractDatabaseInfoFromWorkerResult`), and resolve a KB alias from the single open KB when `_currentKb` is unset (whoami is a meta-tool, so the per-request KB isn't bound and the refresh previously never dispatched). `GetDatabaseInfo` now dispatches for whoami; fully populating the `whoami.database` block is being finished separately.

## v2.8.1 — 2026-05-28

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`mcp.serverVersion` in `whoami` no longer reports a stale 2.7.4 stamp.** The v2.8.0 publish landed with the Gateway csproj `InformationalVersion=2.7.4` because `release.ps1` only bumped version files when `-Version` was passed AND it differed from `package.json`. When `package.json` was edited by hand before invoking the script (as happened for v2.8.0), `$Version -eq $currentVersion` and the whole bump block was skipped — including the csproj sync. The published binary then carried the old version stamp even though the runtime code was the new v2.8.0 source. The script now also reads the csproj's current `InformationalVersion` and forces the bump pass when it's out of sync with `package.json`, regardless of whether `-Version` was passed.
- **csproj version stamp realigned to 2.8.1.** The Gateway DLL emitted by this release stamps `Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion` to 2.8.1 — so `genexus_whoami.mcp.serverVersion` matches the package version, and the in-band update check no longer marks the running binary as "update available" against its own release.

## v2.8.0 — 2026-05-28 (BREAKING)

This release replaces the legacy MCP response shape with a single canonical envelope, so a weakly-capable LLM can read any tool's reply with the same parser. **Every client that parses tool results needs to migrate.**

### Breaking — canonical response envelope

Every worker tool now emits this shape (full spec in `docs/envelope.md`):

```json
{
  "status": "ok" | "error" | "partial" | "accepted",
  "code":   "MachineReadableId",
  "target": "<object name, optional>",
  "result": { "...payload...": "" },
  "error": {
    "code":      "StableErrorCode",
    "message":   "Short human sentence.",
    "hint":      "One-line plain-English fix.",
    "nextSteps": [{ "tool": "...", "args": {}, "why": "..." }]
  },
  "operationId": "...",
  "pollTarget":  "..."
}
```

- **`status`** is lower-case: `ok` / `error` / `partial` / `accepted`. The legacy `Success` / `Ok` / `DryRun` / `NoChange` / `Skipped` / `Error` / `Ready` / `Running` / `Cold` values are gone. Where they conveyed extra meaning, that meaning now rides on `code` (e.g. `code:"DryRun"`, `code:"NoChange"`, `code:"ProjectionTimedOut"`).
- **Tool-specific payload moved under `result`.** Previously fields like `part`, `details`, `source`, `parts`, `availableParts`, `wasFirstApply`, `markdown` lived at the top level alongside `status`. They now nest under `result` for success and under `error` for failures.
- **Errors carry structured next-steps.** Every error path now produces `error.code` (stable PascalCase), `error.message`, `error.hint` (one-line fix), and a curated `error.nextSteps[]` array of `{tool, args, why}` triples so a weak LLM can recover without prose-parsing. ~30+ recurring error paths (PartNotFound, ObjectNotFound, KbNotOpened, FormTypeTransitionUnsupported, PatternVerificationMismatch, IdeHoldsLock, ProjectionTimedOut, GhCliNotInstalled, SearchIndexMissing, …) come with curated next-step suggestions.
- **Async tools always return `accepted`.** The handle is `operationId` plus `pollTarget` for the lifecycle target string clients pass to `genexus_lifecycle action=result`.
- **No legacy aliases on the wire.** Old top-level field names (`action`, `noChange`, top-level `details`/`part`/`message` on errors, `status:"Success"`, etc.) are not emitted in parallel. Migrate the parser, then ship.

### Added

- **`whoami.suggestedNext[]`** — every `genexus_whoami` response now carries a short ordered list of `{tool, args, why}` triples derived from observable state (worker boot, KB not open, index cold, update available, healthy KB). A weakly-capable LLM can read the first entry and pick the right next call without exploring. Same shape as `error.nextSteps[]` so a client reuses one parser.
- **`clientRequestId` idempotency on mutating tools.** Any mutating tool now accepts an optional `clientRequestId` string. The worker caches the full response keyed by that id for 5 minutes; a retry with the same id returns the cached envelope tagged with `_meta.replayed:true` and `_meta.replayedFromUtc:<ISO>`. Lets the client safely retry after a socket drop, gateway timeout, or LLM-side cancellation without double-applying the underlying write — e.g. a `genexus_delete_object` that the gateway timed out on can be re-issued with the same id and the client gets the original `ObjectDeleted` response back, not a `not found` for the already-deleted object. `ping` and `control` (cancel side-channel) are excluded.
- **Gateway pre-validates tool arguments against each tool's `inputSchema`.** Calls with missing required fields, wrong JSON types, or out-of-enum values are rejected immediately with `code:"InvalidArgs"`, `error.violations:[{path, expected, actual}]`, and a hint that names the bad field — no worker round-trip, no STA thread time burned. E.g. `genexus_inspect` without `name` returns `"Required field 'name' is missing — expected string."` synchronously.
- **`status:"accepted"` envelopes inline `cancelTool` and `pollTool` shortcuts.** A weakly-capable LLM no longer has to memorise the `genexus_lifecycle action=cancel target=op:<id>` / `action=status target=op:<id>` shapes — the accept envelope hands back ready-to-call `{tool, args}` objects pointing at exactly those calls. Callers can override either shortcut when the routing isn't the standard lifecycle pair (e.g. tools that expose their own poll handle).
- **Transient error codes carry `error.retryAfterMs`.** Codes that mean "try again soon" (`KbNotOpened` 2 s, `OpenInProgress` 1.5 s, `SearchIndexMissing`/`SearchIndexEmpty` 10 s, `Reindexing`/`IndexCold`/`IndexBuilding` 8 s, `InProgress` 2 s, `ProjectionTimedOut` 60 s, `WorkerBooting`/`Booting` 5 s) now include the recommended wait. Stops LLM clients from hammering the gateway in tight loops or sleeping much longer than needed. A new guard test (`NextStepsCurationGuardTests.TransientErrorCode_CarriesRetryAfterMs`) prevents future drift — any emission of a transient code without `retryAfterMs:` trips CI.
- **`genexus_read` success now carries `result.availableParts`.** Previously the available-parts list only showed up on error envelopes (e.g. `PartNotFound`), forcing the LLM to fail once before learning the object's shape. Reads now expose the list on success, same field name across success and error so a dumb LLM uses one accessor.
- **`AmbiguousName` lookup error replaces the silent "Object not found" when a name matches multiple types.** Previously calling `genexus_read name="Customer"` against a KB with both a `Transaction:Customer` and a `WebPanel:Customer` arbitrarily picked one. The healer now detects ≥ 2 exact-name matches at the index probe and emits `code:"AmbiguousName"` with `error.candidates:[{name,type,parent,module}]` and one pre-mounted `nextSteps[]` entry per candidate (`{tool:"genexus_read", args:{name, type:"Transaction"}}`, …). The LLM literally copies one of the next-step calls.
- **Canonical pagination block on every list/search tool.** `list_objects`, `search`, `query`, `source_search`, and other paged tools now return `result.pagination:{offset, limit, returned, total, hasMore, nextOffset}` with the same shape and field names. `total` is `null` when unbounded (source scans); `nextOffset` is `null` when `hasMore` is false. One pagination formula across the whole surface.
- **Gateway auto-injects `type` when a name resolves uniquely in the index.** Tool calls with `arguments.name` but no `arguments.type` get the missing `type` filled in by the gateway when the cached name→type map has exactly one match. Ambiguous (≥ 2 matches) or unknown names skip the inject so the worker's resolution / `AmbiguousName` flow stays authoritative. The response includes `_meta.autoInjected:["type"]` and `_meta.autoInjectedType:"<X>"` so the LLM can self-correct if the inference was wrong.
- **Every tool advertises `examples` inline in its `inputSchema`.** 40+ tools now carry 1–2 canonical example call shapes in their schema. A weakly-capable LLM reads `tools/list`, copies one example, and has a high-likelihood-working call without guessing. Token budget bumped 9500 → 10500 to fit the additions; the discovery golden fixture is regenerated to match. Examples use generic-but-concrete names (`"Customer"`, `"MyPanel"`) — no KB-specific leaks.
- **`dryRun:true` is universal across every mutating tool.** Previously some tools (`edit`, `versioning`, `create`) supported `dryRun` while others (`delete_object`, `apply_pattern`, `refactor`, `rename_across_kb`, `variable`, `lifecycle build/index`, `github create_pr`, `multi_agent_lock`, `run_object`) silently ignored it or didn't expose it. Every mutating tool now declares `dryRun` in its schema AND short-circuits before persistence when set, returning the canonical envelope with `code:"DryRun"` and `result.preview` describing what would have changed (e.g. `wouldDelete:{name,type,guid}` for delete, expanded build plan for lifecycle, resolved `gh` args for github, resolved URL without GAM login for run_object). A new guard test (`DryRunUniversalGuardTests`, 28 cases) prevents any mutating tool from regressing the contract.
- **Duplicate `clientRequestId` waits on the in-flight call instead of executing twice.** v2.8.0 added idempotency cached responses; this release closes the remaining race: a duplicate that arrives WHILE the original is still executing now blocks on an in-flight signal and returns the original's result when it completes, instead of re-executing the mutation. The duplicate's response carries `_meta.replayed:true`. Eliminates the brief window where a fast LLM retry inside the original's execution time could double-apply.
- **`genexus_help` natural-language → tool router.** A new helper service maps plain-English intents to the right tool call shape. `RouteGoal("delete the WebPanel MyPanel")` returns `result.matches:[{tool:"genexus_delete_object", args:{name:"<name>", type:"<type>"}, why, confidence}]` with up to 3 ranked suggestions; unknown intents fall back to `genexus_orient`. Tiny hand-curated keyword scorer over ~25 intents; cheap, deterministic, no model dependency. Lets a weakly-capable LLM skip "which tool do I use?" guessing.
- **Streaming progress via canonical `notifications/progress`.** Long-running tools push progress as MCP-spec JSON-RPC notifications enriched with `stage` (short label like `indexing`/`compiling`/`projecting`) and `elapsedMs` (computed from a recorded operation start) on top of the spec's `progressToken`/`progress`/`total`/`message`. Clients that render only the spec fields ignore the extras safely; clients that render `stage`/`elapsedMs` get a multi-stage progress bar without parsing the message string. `ProgressEmitter.MarkOperationStart()` lets callers record an anchor so subsequent `EmitStage` calls compute elapsed time automatically. The plumbing already pushed through stdout in prior releases; this release canonicalises the shape and documents the contract.
- **Every tool advertises MCP-spec `annotations`.** Each entry in `tools/list` now carries the standard `annotations:{readOnlyHint, destructiveHint, idempotentHint, openWorldHint}` quartet defined by the MCP specification. Worst-case picked for multi-action tools (e.g. `genexus_kb` exposes both reads and a mutating `close`, so `readOnlyHint:false`). 17 tools are read-only, 2 destructive (`delete_object`, `versioning`), 19 idempotent, 7 open-world (`github`, `ai_complete`, `browser`, `worker_reload`, `worker_pool`, `run_object`, `test`). Spec-compliant — MCP-aware clients (Claude Desktop, etc.) can render automatic safety hints without parsing tool descriptions. A new guard test (`ToolAnnotationsGuardTests`) pins the curated truths so future contributions can't quietly mis-annotate. Token budget bumped 10500 → 12000.
- **Curated, source-verified GeneXus development skills via MCP `resources/`.** Four reference resources fact-checked against `docs.genexus.com`:
  - `genexus://kb/skills/navigation` — `Call` method, `CallOptions.Target` enum (`"Left"` / `"Content"` / `"Blank"`), and the killer correction: **`CallProtocol` does NOT apply to Web Panel or SD Panel, and `"Modal"` is not a valid value.** Real values listed verbatim (`Internal`, `Command Line`, `HTTP`, `SOAP`, `Enterprise Java Bean`).
  - `genexus://kb/skills/gam-integrated-security` — canonical property name `Integrated Security Level`, accepted values (`Authorization` / `Authentication` / `None`, with `Authentication` as the Version-level default), object types that honour it.
  - `genexus://kb/skills/sd-panel-mobile` — the IDE-facing property name is **`Main program`** (LLMs commonly hallucinate `IsMain`); applies to Menu / Panel / Work With; lists what additional properties unlock when Main=True.
  - `genexus://kb/skills/webpanel-events` — canonical `Start → Refresh → Load` order, what's accessible in each, why `Refresh` is the place to reset accumulators.

  Each body cites the docs.genexus.com page it was verified against. The `whoami.suggestedNext` block now always nudges the LLM to read the navigation skill before claiming a navigation property/method exists.
- **MCP-spec `completion/complete` for object-name autocomplete.** Calls with `argumentName ∈ {name, target, targets}` now return suggestions from the cached KB index — partial prefix → up to 25 object names that start with it (case-insensitive). Reuses the `AutoTypeInjector` name lookup, so it warms with the same index refresh whoami uses. Lets a weakly-capable LLM type `name=Cust` and get back `["Customer", "CustomerOrder", ...]` instead of guessing.
- **Skills discoverability — three accumulative paths.** Reading the skill resources used to require the LLM to know that MCP servers can expose resources and to actively probe `resources/list`. Now:
  1. `genexus_whoami.result.skills[]` carries the full catalog as a first-class block (uri + title + summary + `whenToRead` guidance + a pre-mounted `readVia:{tool:"resources/read", args:{uri}}`). Visible on the recommended first call of every session — no extra hop.
  2. The description of `genexus_whoami`, `genexus_edit`, `genexus_properties`, and `genexus_apply_pattern` inlines a `→ resources/read uri=genexus://kb/skills/<topic>` hint, so the cue is visible on every `tools/list` even before the LLM calls anything.
  3. Error envelopes for codes likely caused by hallucinated properties/methods (starting with `FormTypeTransitionUnsupported`) now carry the relevant skill as the first `error.nextSteps[]` entry — the LLM hits the wall exactly once and is one tool call away from the verified reference.

### Internal

- Worker `McpResponse` helpers replaced with `Ok / Err / Partial / Accepted / NextStep`; legacy `Success` / `Error` methods deleted.
- 30+ worker services and the CommandDispatcher rewritten to construct responses through the canonical helpers only.
- Gateway updated to recognise the new envelope (`Program.cs`, `McpRouter.cs`, etc.). The Gateway is pass-through to clients; tool responses reach the wire exactly as the worker emits them.
- `IndexState` payload renames `status` → `indexStatus` to avoid colliding with the envelope-level `status`. Gateway whoami composition updated accordingly.
- `EnvelopeConformance` validator and a source-level guard test (`EnvelopeContractGuardTests`) added so reintroducing legacy emissions trips CI: scans every `src/GxMcp.Worker/Services/**.cs` for `McpResponse.Success(`, `McpResponse.Error(`, `["status"] = "Success"`/etc., or hand-rolled `"{\"status\":\"Error\"…}"` strings, and verifies `McpResponse.cs` only exposes the canonical surface.

## v2.7.4 — 2026-05-28

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_delete_object` retry after a client timeout is no longer reported as "Object not found".** When the worker's `obj.Delete()` finished after the MCP client gave up on the call (large objects can take longer than the gateway's pipe budget), the next `genexus_delete_object` for the same name reached an empty KB and surfaced the generic not-found envelope — leaving the agent unsure whether the deletion actually succeeded. The worker now records every successful delete for 5 minutes and matches retries against that record: a retry whose object is genuinely gone returns `status:"Success", confirmedAfterTimeout:true, deletedAtUtc:<iso>` with a note explaining the earlier call completed server-side. A typo or never-existed name still gets the not-found envelope.
- **`genexus_apply_pattern` with `reapply=true` regenerates the full family when the generated host was previously deleted.** The pattern engine's `GetPatternInstance` returns the metadata stored on the parent even after the `WorkWithPlus<Name>` host has been removed from the KB, so reapply was taking the "existing instance" path and producing a minimalist `PatternInstance` (often an empty `<table/>`) instead of regenerating. The apply path now probes for the host before trusting the metadata: a missing host promotes the call back to first-apply so the engine rebuilds the family. The response carries `staleInstanceRecovered:true` and a hint when this happens.
- **`genexus_edit mode=ops` schema now matches the real ops dispatcher.** The `ops` field was advertised as "RFC 6902 JSON-Patch" with `op ∈ {add, remove, replace, test}`, but the worker actually implements a GeneXus-semantic DSL — `set_attribute`, `add_attribute`, `remove_attribute` (Transaction), `add_rule`, `remove_rule` (Transaction/Procedure/WebPanel), `set_property` (any kind). Sending `{op:"add", path:"…"}` was accepted by the schema and then rejected by the worker with a `did-you-mean: set_attribute, …` error. The schema now declares the actual op enum and a free-form `args` object; the description spells out which op applies to which object kind and points callers at `mode=patch` for textual find/replace.
- **`genexus_query name:"X"` (and bare quoted `"X"`) now returns the exact name instead of 50 substring/vector look-alikes.** Passing a unique identifier like `name:"WorkWithPlusComissaoParecerCadastro"` used to leak the term into vector similarity and surface dozens of attributes whose embeddings were semantically close — wasting the agent's response budget on noise. `name:` is now a first-class filter (alongside `type:`, `usedby:`, `parent:`, `parentPath:`, `description:`) that applies a hard exact-name match before the ranker runs, and a bare-quoted whole query is interpreted the same way. Multi-word semantic queries still vector-rank normally.
- **`genexus_edit_and_build` now survives the real gateway envelope and full-write status codes.** The composite tool is sent to the worker as an orchestration command whose real tool arguments sit under an inner `args` object, but the orchestrator was only reading the outer envelope — so valid calls could fail immediately with `name is required`. After that was fixed, full `Source` writes still stopped at the edit phase because the write service reports `status:"Success"` while the orchestrator only treated `status:"Ok"` as editable success. The tool now unwraps the gateway envelope before validating, uses the real `part` when routing through the write facade, translates patch-shaped `{find,replace}` edits into the patch service correctly, and continues to impact/build for successful full writes. No-op writes return a composite response with `build.status:"Skipped"` instead of looking like a failed orchestration.
- **`genexus_edit mode=patch` on a WebForm now rejects html ↔ layout transitions up front with a typed envelope.** Patching only the `Form type` attribute (or the surrounding fragment) used to reach the SDK and bounce back as a generic "Visual write failed", costing the caller several iterations to diagnose. The patch service now compares the persisted `Form type=...` against the post-patch source and, when they differ on a visual part, returns `status:"Error", code:"FormTypeTransitionUnsupported"` with `fromFormType` / `toFormType` and a hint that points at `mode=full` (plus `genexus_create_popup` on WorkWithPlus KBs). The same surfacing also fires on the full-write path: when a `Form type` transition fails inside the SDK, the human message is now `"Form type transition not supported via this write path (html → layout). Use mode='full' with a complete target-type body."` instead of the bare `"Visual write failed"`.
- **`genexus_edit part=PatternInstance` validate=only now reports `childOrderReconcile` so callers fix structural drift before paying for a write.** The pattern XML pre-processor auto-reconciles the WorkWithPlus `childrenOrderedList` attribute on every container, but its findings used to live only in the worker log. The dry-run envelope now carries `childOrderReconcile:{parentsUpdated, changes[], skips[], skipsHint}` — and the same block rides along on `code:"PatternVerificationMismatch"` envelopes — so an agent that sent XML with missing/unknown child identifiers sees exactly which parents the reconciler refused to rebuild and what to fix. The existing rich verify-failed diagnostics (`verifyDiff`, `persistedSnippet`, `requestedSnippet`, `sdkSaveError`) are unchanged.
- **`genexus_apply_pattern reapply=true` surfaces a hard-timeout signal instead of looking indefinitely successful.** Reapply projection (the SDK `UpdateParentObject` step) was already timed with a 30 s soft-warn hint, but a projection that ran 5+ minutes still returned a normal-looking envelope and let the agent keep polling a worker that was effectively wedged. Projections past `GENEXUS_MCP_REAPPLY_TIMEOUT_MS` (default 300000 ms) now mark the response with `code:"ProjectionTimedOut", recoveryRequired:true, recoveryHint:"…/mcp reconnect or genexus_worker_reload mode=hard"` so the agent stops polling and triggers worker recovery. The IDE-tab-hold guard (`code:"IdeHoldsLock"`) and the soft `slowReapply` hint at 30 s remain unchanged.

### Changed

- **`genexus_edit async=true` is now an official MCP-facing contract for long writes.** Large `WebForm`, `Source`, `Events`, and `PatternInstance` edits can exceed a client or gateway timeout even when the worker is still progressing normally. You can now opt into a standards-compatible async flow by passing `async:true`: the initial `tools/call` returns immediately with `operationId` plus the legacy `job_id`, and follow-up status/result reads go through the existing `genexus_lifecycle target=op:<id>` path. This keeps the wire protocol pure MCP — ordinary tool results plus normal progress notifications when available — while giving clients a reliable handle for long-running edits.
- **Async edit jobs now wait for the real worker completion instead of reusing the synchronous timeout budget.** Previously the gateway's background edit path still applied the normal per-tool timeout internally; when that budget expired, the job could be completed from a placeholder `status:"Running"` envelope and look successful even though the worker was still busy. Background edit jobs now stay attached until the worker returns a terminal result, and the success/failure classification rejects non-terminal inner payloads such as `Running`, `Error`, or `Cancelled`.
- **`genexus_variable async=true` now follows the same public async contract as `genexus_edit`.** Variable adds, deletes, and type changes already went through the same background-job path internally, but the canonical umbrella tool was not advertising that surface and the gateway only recognized the legacy split aliases in its async intercept. The schema/help now document `async` plus `estimated_seconds`, the canonical `genexus_variable` name takes the same path, and the accepted payload returns `operationId`, `job_id`, and `pollTarget`.
- **Async `genexus_lifecycle build` / `rebuild` responses now expose the operation handle inline.** The async build path already created a background job and could be polled through `genexus_lifecycle target=op:<id>`, but the initial accepted payload only returned `job_id`, forcing clients to infer the stronger handle from conventions. It now returns `operationId` and `pollTarget` alongside `job_id`, aligning long builds with the same MCP-facing contract used by async edits while keeping the wire format a normal tool result.
- **`genexus_lifecycle validate` and `genexus_edit_and_build` now advertise the handles they really support.** `genexus_lifecycle action=validate` was documented as if it used the same background-job path as `build`, but it actually runs inline through the validation/specifier route and returns in the same call. `genexus_edit_and_build`, on the other hand, orchestrates its rebuild entirely on the worker side, so the follow-up handle is the worker `taskId`, not a gateway `op:<id>` job; the help/description now say that explicitly, and the build block is enriched with `pollTarget` to point callers at the correct `genexus_lifecycle target=<taskId>` follow-up.

## v2.7.3 — 2026-05-27

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Worker cold-start is ~40% faster, so the first tool call after a worker (re)starts stops timing out.** Booting a worker re-activated the GeneXus Service Manager twice: once via the build-task warm-up and again via the connector init, with the second attempt burning ~35 s before throwing "Service Manager já foi ativado" (already activated). Cold-start dropped from ~92 s to ~53 s on a large KB. On top of that, the gateway now waits for the worker's "SDK ready" signal **before** starting a tool's timeout clock, so worker start-up time is no longer billed against the operation's budget — a `genexus_delete_object`, `genexus_apply_pattern`, or `genexus_read` issued right after a (re)start completes inline instead of returning a spurious "still running" timeout, regardless of how long boot takes. Worker boot is also now instrumented: each init step's duration is logged, and an init failure logs the full inner-exception chain instead of a generic message.
- **Worker processes no longer pile up — strictly one worker per Knowledge Base.** A single worker exit (crash, soft reload, or a `genexus_worker_reload`) could spawn more than one replacement: the worker restarted itself *and* the gateway spawned a fresh one for the same KB, leaving the previous process alive but untracked. Under a reload loop this compounded into hundreds of orphaned `GxMcp.Worker` processes eating memory. The gateway is now the single authority for respawning, and a reaper kills any duplicate worker bound to a KB before starting a new one. The pool still caps the number of open KBs (default 3), so total workers can't exceed that.
- **Long-running tool calls no longer trip the client's "Request timed out" (`-32001`) error.** A heavy operation — typically a first `genexus_apply_pattern` of WorkWithPlus on a real transaction, where GeneXus generates the whole object family — could run past the MCP client's request deadline; the client gave up and showed a timeout even though the work completed on the server. The gateway now emits standard MCP `notifications/progress` messages while the worker is busy (every 15 s) whenever the client supplies a `progressToken`, which keeps the connection alive so the call can finish and return its real result inline. The call stays synchronous — this is the spec's native mechanism for long operations, not a background job — and is a no-op for clients that don't request progress.
- **Tools no longer stall for 30–60 s the first time you touch an object on a freshly opened KB.** Resolving an object by name (which nearly every tool does — `genexus_delete_object`, `genexus_apply_pattern`, `genexus_inspect`, `genexus_edit`, …) used to force a synchronous load of the full search index on the single thread that runs every SDK call. On a cold or large KB that load could take half a minute or more, and because that thread is shared, *every other queued tool call appeared to hang at the same time* — surfacing in clients as intermittent timeouts. Object lookups now use the index only if it's already in memory and otherwise resolve straight through the GeneXus name lookup (fast, and it also sees just-created objects), kicking off the index load in the background. The "object not found" suggestion path got the same treatment, so a miss on a cold KB returns immediately instead of stalling.
- **`genexus_apply_pattern` now fails fast when the GeneXus IDE holds the object open** instead of deadlocking for 10+ minutes on the SDK apply call. It returns the same structured `IdeHoldsLock` error that `reapply` already returned, naming the object(s) to close in the IDE before retrying. The check is best-effort and never blocks a valid apply.

## v2.7.2 — 2026-05-26

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Intermittent `Transport closed` / dropped connection when more than one gateway was running.** Each MCP client session starts a gateway; the first one binds the local port and becomes the "master", the rest attach to it as proxies. The master kept its instance lease alive by refreshing it every 60 seconds, but a lease was treated as stale after only 45 seconds — so for roughly 15 seconds of every minute a newly-launched gateway saw the live master as dead, tried to take over the port, failed to bind it, and killed the running master during port recovery. Clients (Codex, Cursor, …) experienced this as the connection dropping just as it started working, and restarting the client on every prompt was the only workaround. The active gateway now refreshes its lease every 15 seconds — well inside the staleness window — so a second gateway correctly attaches as a proxy instead of evicting the live one.
- **`genexus_gxserver` now detects GeneXus Server links that the IDE sees.** The tool reported `connected:false` on Knowledge Bases that were in fact linked to a GeneXus Server, because it looked for marker files on disk — but the server link is stored in the KB metadata, not in files. It now reads the link through the GeneXus SDK (the same source as the IDE's Team Development tab): `status` returns the real `serverUrl`, `host`, and `remoteKbName`; `pending` lists the objects with uncommitted local changes (`name`, `operation`, `lastChange`, `user`); and `conflicts` reports actual update conflicts. Still read-only — no commit or update is performed. Falls back to the previous file-based detection when the Team Development service isn't loaded.

### Internal

- Gateway lease heartbeat moved to a dedicated loop paced at `GatewayProcessLease.LeaseHeartbeatInterval` (1/3 of `LeaseStaleAfter`), decoupled from the 1-minute session-cleanup loop. A regression test asserts the heartbeat stays at most half the stale window so the two constants can't drift apart again.
- `GxServerSyncService` resolves the model-level `ITeamDevClientService` via `Services.TryGetService<…>()` and projects `GetLocalChanges` / `GetConflictEntities`; the legacy filesystem-probe envelopes remain as the fallback path and still back the existing unit tests.

## v2.7.1 — 2026-05-26

### Added

- **`genexus_edit validate="only"` now works for `PatternInstance` and `WebForm` full-XML writes.** Previously the in-memory dry-run mode was honoured only for `mode=patch` and `mode=ops`; a full-XML write to a pattern or visual part ignored it and went straight to persistence. You can now dry-run a pattern or layout edit to confirm it parses and round-trips before committing — the response comes back `status:"DryRun"` with nothing written to the KB.
- **`genexus_whoami` now reports the KB's database configuration.** SQL-generating tools were defaulting to MySQL dialect because nothing surfaced which DBMS the KB was actually configured against. The whoami envelope now carries a `database` block listing every datastore declared in the active environment (e.g. `Default`, `Docente`, `GAM`) with `name`, `type` ("Oracle" / "SqlServer" / "MySQL" / "PostgreSQL" / "Db2" / …), `dialect` (lowercase family token reused across MCP tools), `provider` ("Oracle Data Provider"), `serverName`, `schema`, and an `isDefault` flag. A top-level `database.default` shortcut + `database.dialect` token are pre-extracted so agents can read one field instead of scanning the array. Populated once per session on the first whoami after KB open and cached gateway-side until restart.
- **`genexus_db` SQL-generating actions inherit the dialect at point-of-use.** When `action` is `sql_ddl`, `sql_navigation`, `optimize_analyze`, `optimize_suggest`, or `optimize_report`, the response now carries `dialect` (e.g. `"oracle"`) and `dialectType` (e.g. `"Oracle"`) drawn from the same gateway cache that powers whoami. Agents that didn't read whoami first still get the correct dialect alongside the generated SQL — no more Oracle KBs receiving MySQL-flavoured queries by default.
- **`genexus_apply_pattern reapply=true` now surfaces a `slowReapply` signal** when the SDK projection phase exceeds 30 s. The response carries `slowReapply: true`, the measured `projectionMs`, and a `slowReapplyHint` pointing at the most common cause (the GeneXus IDE holding the parent or `WorkWithPlus<Name>` open in a tab — close it and retry; if no IDE is running, restart the worker via `genexus_worker_reload mode=hard`). Previously the slow-projection signal only hit the worker log; agents had no structured way to react. The STA constraint still prevents a hard wall-clock abort of the SDK call itself — combine this signal with the existing `IdeHoldsLock` pre-check for the full safety net.

### Changed

- **`genexus_edit` visual-write now emits `code:"FormTypeTransitionUnsupported"` when the request changes `<Form type>`.** Previously a Form-type transition (typically `html → layout`) failed with the generic `"Visual write failed"` envelope and no useful diagnostic — agents iterated several times trying to figure out what the SDK rejected. The worker now extracts the Form `type` attribute from both the persisted XML and the incoming body; if they differ, the save-failure envelope is tagged with the specific code and a hint explaining that Form-type transitions only succeed when the body is a COMPLETE target-type document (mode='full' with the new `<Form type="…">` root and all children), and that WorkWithPlus KBs additionally need the dual-form `<detail><layout><table>` wrapping. Detection is structural (XML attribute comparison), not string-matching the SDK exception, so it fires regardless of which root cause the SDK reports.
- **`Indexing` envelope now reports real progress and ETA.** The cold-start `{status:"Indexing", code:"IndexNotReady"}` envelope (returned by `genexus_list_objects` and the gateway's pre-worker guard when the index isn't ready yet) previously hardcoded `"Index still building; retry in 2-5 seconds."` regardless of KB size. The message is now templated from the index phase (`"Building index from cold start"` / `"Walking KB (ultra-lite pass)"` / `"Rebuilding index"`) with `N% complete` and `~Ns remaining` appended when the worker has populated them. `etaMs` is also surfaced on the envelope so an agent can pace its retry instead of polling blindly. Agents on large KBs (10k+ objects) get a realistic wait estimate; small-KB callers see the same sub-second behavior as before.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **The IDE's "Apply this pattern on save" checkbox now stays checked after the MCP edits a WorkWithPlus pattern.** Editing a host's `PatternInstance` through `genexus_edit` used to silently clear the flag the GeneXus IDE renders as that checkbox, so the next time you opened the object the box was unchecked and the layout no longer regenerated on save. The MCP now re-asserts the flag after every successful pattern write; the response carries `applyOnSaveReenabled: true` so you can confirm it took.
- **GeneXus no longer pops the "different installation than last time" dialog after the MCP opens a Knowledge Base.** On installs where the GeneXus executable's file-version build differs from its product-version build, the MCP was stamping the KB with the file-version build (e.g. `18.0.48055 U7`) while the IDE identifies itself by the product-version build (e.g. `18.0.179127 U7`). Every MCP open rewrote the stamp to the wrong value, so the next IDE open warned about a version mismatch. The MCP now reads the product-version string and writes each `.gxw` version field in the exact format the IDE uses, so opening the same KB in the IDE after using the MCP no longer triggers the prompt.
- **`genexus_edit part=PatternInstance` verification failures now carry the actual SDK error and a stable code.** A failed pattern write previously returned a generic `"Pattern write verification failed"` with nothing to act on. The error envelope now includes a machine-readable `code` (`PatternInvalidXml`, `PatternPartNotFound`, `PatternVerificationMismatch`, or `PatternSaveFailed`) and, when the SDK throws while saving, an `sdkSaveError` block with the exception type, message, and inner-exception chain — so you can see why the SDK rewrote or rejected the bytes instead of guessing.
- **Union-typed tool parameters no longer use a JSON-Schema `anyOf`.** The `patch` parameter (on `genexus_edit` / `genexus_edit_and_build`) and `gamSession` (on `genexus_run_object`) declared their string-or-object shape with `anyOf`, which some MCP clients reject when relaying the tool list to their model API (HTTP 400, "input_schema does not support oneOf, allOf, or anyOf"). They accept exactly the same values as before; only the schema shape changed. A new schema check fails the build if a combinator reappears.
- **`genexus_list_objects` compact shape now returns `parentPath`.** The gateway's default (`axiCompact=true`) projection promised `{name, type, path, parentPath, lastUpdate}`, but the worker only emitted `parentPath` when `verbose=true` — so default callers got the field projected to nothing. Compact responses now carry `parentPath` whenever the index knows it (e.g. `"Root Module/ClickSign"`); verbose callers are unchanged.

### Changed

- **Faster hierarchy lookups on a warm KB.** `genexus_list_objects`, `genexus_inspect`, and other tools that resolve an object's parent chain no longer re-walk `obj.Parent` per sibling on the first hot call after a KB open. The hierarchy cache is now primed from the on-disk index at hydration time, so lookups are O(1) from the first call. Most visible on large KBs where the prior cold-list spent measurable time re-resolving identical parent paths across hundreds of siblings.

## v2.7.0 — 2026-05-26

### Changed

- **Consolidated tool surface.** 92 tools collapsed to 42 (≈54% reduction) by introducing 8 umbrella tools that absorb 38 legacy tools via `action=` dispatch, and removing 14 niche tools from advertisement (still callable by legacy name during this release window):
  - **`genexus_browser`** — `action=smoke|a11y|wcag|capture|cross|preview` (was `genexus_smoke_test`, `_a11y_audit`, `_wcag_check`, `_browser_capture`, `_cross_browser`, `_preview`). `preview` keeps the `mode=render|run` sub-discriminator.
  - **`genexus_db`** — `action=drift_check|drift_report|optimize_analyze|optimize_suggest|optimize_report|sql_ddl|sql_navigation|sample_data|types_list|types_describe|types_validate|translations_import` (was `genexus_db_drift`, `_db_optimize`, `_sql`, `_generate_sample_data`, `_types`, `_translations`).
  - **`genexus_versioning`** — `action=history_list|history_get|history_save|history_restore|undo|time_travel|blame|diff|diff_generated` (was `genexus_history`, `_undo`, `_time_travel`, `_blame`, `_diff`, `_diff_generated`).
  - **`genexus_io`** — `action=asset_find|asset_read|asset_write|export_part|import_part|export_unified|screenshot_publish|ocr` (was `genexus_asset`, `_export_object`, `_import_object`, `_export_unified`, `_screenshot_publish`, `_ocr_screenshot`).
  - **`genexus_variable`** — `action=add|delete|modify` (was `genexus_add_variable`, `_delete_variable`, `_modify_variable`).
  - **`genexus_telemetry`** — `action=executions|watch_event|friction_append|friction_tail|learning_report|logs|profile_analyze|profile_hotspots|profile_correlate` (was `genexus_execution_history`, `_watch_event`, `_friction_log`, `_learning`, `_logs`, `_profile`).
  - **`genexus_create`** — `action=object|popup|sd_panel_create|sd_panel_inspect|sd_panel_edit|save_as|scaffold|translate|sample|template` (was `genexus_create_object`, `_create_popup`, `_sd_panel`, `_save_as`, `_forge`, `_apply_template`).
  - Withdrawn from advertisement (still dispatch by legacy name): `genexus_inject_context`, `_kb_explorer`, `_pr_description`, `_explain`, `_kb_readme`, `_build_plan`, `_sandbox`, `_kb_diff`, `_kb_import`, `_tutorial`, `_voice`, `_what_if`, `_auto_test`, `_reverse_pattern`. Reachable via `genexus_recipe` / `genexus_playbook` references and the `LegacyToolAliases` fallback.
  - Folded duplicates removed: `genexus_orient` (use `genexus_whoami`), `genexus_validate_payload` (use `genexus_edit validate="only"`), `genexus_bulk_edit` (use `genexus_edit targets[]`).
- **Soft-alias compatibility.** Legacy tool names still dispatch transparently to the new umbrellas during this release. Set environment variable `GXMCP_LEGACY_TOOL_ALIASES=0` to opt out early (the old names then return `MethodNotFound`).
- `tool_definitions.json` schema budget lowered from ~13.2k → ~8.8k tokens (~33% reduction on every model turn).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Gemini / Vertex AI HTTP 400 on `tools/list`** caused by `genexus_run_object.args` declaring `type: "array"` with no `items` field — strict OpenAPI consumers (Vertex, some OpenAI Function-Calling configurations) reject the request before the tool is ever called. The schema now declares `items: {type: "string"}`. A new `ToolSchemaShapeTests` suite walks every umbrella + nested schema and asserts `array → items`, non-empty `enum`, `required[]` entries match `properties`, and unique tool names — so this class of bug fails CI instead of a chat session.

### Internal

- Tool-definitions budget lowered 13300 → 9500. `ToolSchemaSizeTests` comment trail updated with the v2.7.0 rationale.
- `McpRouter.TryRewriteLegacyTool` is the single rewrite table; called once early in `Program.cs` (so gateway-only handlers see the new name + `action`) and once again from `McpRouter.ConvertToolCall` (defence-in-depth for callers that bypass the early hook).
- `OperationsRouter` gains `ConvertBrowserUmbrella`, `ConvertDbUmbrella`, `ConvertVersioningUmbrella`, `ConvertIoUmbrella`, `ConvertCreateUmbrella`, `ConvertTelemetryUmbrella` helpers — each is a thin switch on the new `action=` that maps to the worker module/action the legacy tool used. No worker-side service changes; everything reaches the same `CommandDispatcher` cases as before.
- Gateway-only handlers in `Program.cs` for `genexus_execution_history` and `genexus_watch_event` collapsed into a single `genexus_telemetry` short-circuit that gates on `action=executions|watch_event`.
- `NextLegalActionsBuilder` suggestions retargeted to the new names (`genexus_versioning action=history_restore`, `genexus_browser action=preview mode=run`).
- Playbook examples in `wwp_dual_form.md` and `pattern_reapply.md` updated to the new `genexus_versioning action=history_restore discard=true` form.
- `ToolDefinitionsRedirectsTests.GenexusCreateObject_DescriptionRedirectsWwpToApplyPattern` renamed to `GenexusCreate_*` and pointed at the new umbrella description.
- 9 new `LegacyToolAliasTests` cover the browser umbrella's rewrite map (one per legacy name + null-args + non-consolidated tool); the same pattern can be extended for the other 32 absorbed names as needed.

## v2.6.12 — 2026-05-26

### Added

- **`genexus_playbook topic=<topic>`** — deferred-load skill packs. Returns the full markdown body of an embedded playbook for a named topic. Initial topics: `popup_layout` (polished WWP popup `PatternInstance` idiom: `Template="EmptyWithTitle"`, `themeClass="GroupFiltro"`, `descriptionPosition="Left"`, `controlPropertiesString="Direction=Vertical"` for stacked radios, `TableActions` with `class="PrimaryAction"`, reserved-userAction names, declarative `visibleCondition`), `wwp_dual_form` (the `<Form type="layout">` `<detail><layout><table>` schema, allowed control-element attributes, theme class GUID convention, "edit `PatternInstance`, never the parent `WebForm`" rule), `pattern_reapply` (apply vs reapply call shapes, post-v2.6.11 `PartialFailure` envelope, `src0265`/`src0216` fix map, template-choice guidance for WebPanel hosts). Markdown bodies live as embedded resources in the Worker assembly; the tool schema costs ~110 tokens and the bodies only enter the LLM context when the LLM calls the tool. Discoverable via `topic=` + `list=true`.
- **`playbookHint` planted in `next_legal_actions`** for `genexus_create_popup` (suggests `topic=popup_layout` so a freshly-scaffolded popup gets the polished-form idiom before the agent starts customizing) and `genexus_apply_pattern` success (suggests `topic=pattern_reapply` so reapply diagnostics + template-choice guidance are one tool call away).

### Internal

- Tool-definitions budget bumped 13150 → 13300 (measured impact ~110 tokens for the new tool's schema entry; embedded markdown bodies aren't in the schema). `ToolSchemaSizeTests` comment trail updated.
- `PlaybookService` reads `Playbooks\*.md` via `Assembly.GetManifestResourceStream`; new resources auto-register at build time via `<EmbeddedResource Include="Playbooks\*.md" />` in `GxMcp.Worker.csproj`. Drop a new `.md` under `src/GxMcp.Worker/Playbooks/` to add a topic — no service changes needed.
- 4 unit tests in `PlaybookServiceTests` cover list/read/unknown-topic/empty-topic paths.

## v2.6.11 — 2026-05-26

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`apply_pattern reapply=true` no longer returns silent `status:"Success"` when the pattern's Events-by-WorkWithPlus generation will fail at the next IDE save.** Live repro: a fresh PatternInstance (created when `wasFirstApply` lands on a host that had been rebuilt) doesn't carry forward the previous host's controlName map, so any reference in the parent's Events code to a control the new instance doesn't expose (typically `GrpX.Visible = …` after a popup conversion) fails with `src0265: Invalid attribute 'GrpX'` + `src0216: 'Visible' invalid property` — but only visible to the user when they try `Ctrl+S` in the IDE, well after the MCP has already declared the reapply a success. The reapply now runs `SdkDiagnosticsHelper.GetDiagnostics(parent)` after the projection phase and surfaces `Error`-severity diagnostics (plus the WWP-projection-specific src0265 / src0216 codes) in the response. When issues are found the envelope flips to `status:"PartialFailure"` with `patternValidationIssues:[…]` and a hint telling the agent which Events references to fix before the user's next save.

## v2.6.10 — 2026-05-25

Six fixes to surfaces that surfaced friction during the v2.6.9 popup-conversion session — every gap that turned a 10-min task into a 90-min one is now closed.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_create_popup` now works on WorkWithPlus KBs.** The flat `<Form type="layout"><table>` body emitted by prior versions was rejected by `WebLayoutHandler.LoadPanelElement` with `"Elemento não pode ser desserializado do nó XML porque sua marca (table) não corresponde ao nome do elemento (detail)"` on any KB with the WorkWithPlus dual-form convention — i.e. most GeneXus 18 KBs in the field. A new `WwpConventionProbe` samples existing layout-form WebPanels to detect the convention and harvest the theme class GUID prefix (e.g. `d4876646-98dd-419b-8c1c-896f83c48368`), and `PopupLayoutBuilder.BuildWwpLayoutXml` emits the proper `<Form type="layout"><detail><layout id="GUID"><table controlName tableType="Responsive" class="<prefix>-N">…</table></layout></detail></Form>` structure with class suffixes `-4` (data attribute), `-24` (textblock), `-46` (action), `-59` (errorviewer). Non-WWP KBs keep the flat-schema path.
- **`genexus_search_source` gained `fields=["webForm"]` scope.** WebForm XML wasn't indexed by any search; the agent was blind to layout-form examples (e.g. "how does this KB express a Radio Button in WWP?") even when one existed in the same KB. Opt-in via the new `webForm` value so the default code-search path stays fast; reuses `WebFormXmlHelper.ReadEditableXml` for the read.
- **`genexus_preview` wall-clock budget + GAM-redirect detection.** A single preview against a GAM-protected panel used to wedge the STA worker thread for 10+ minutes — every other MCP tool queued behind it until /mcp reconnect. Now bounded by `GXMCP_PREVIEW_BUDGET_MS` (default 60 s); per-step CLI timeouts shrink as the budget burns down. Final URL is also captured after the launcher loads so the GAM-login detector catches the redirect even when the requested URL itself isn't a login URL. Returns `{status:"Error", code:"PreviewTimeout", elapsedMs, stage}` instead of blocking.
- **Visual write failures translate known SDK-error shapes into actionable hints.** "Visual write failed" used to ship as a bare message; the exception chain is now reachable via the diff-allowlist (see v2.6.9's TrimErrorEnvelope expansion), and on top of that recognised patterns get a structured `hint` — e.g. `marca (table) não corresponde` / `WebLayoutHandler` maps to "use the WWP dual-form schema", `variable not declared` maps to "add the variable first via genexus_add_variable".
- **`apply_pattern reapply=true` projection-step stopwatch.** Reapply on a host whose parent / WorkWithPlus host is open in a GeneXus IDE tab takes 10+ minutes (SDK deadlock); on a free object it's 1–3 s. Reapply now times the `UpdateParentObject` projection phase and logs `[APPLY-PATTERN] projection took NN ms — likely IDE-tab-hold contention` at WARN above 30 s, with the elapsed time also surfaced in the `phases` envelope. The `.lock` per-object pre-check stays as defence in depth where it triggers (true positives only — see the limitation note below).
- **`childrenOrderedList` reconciliation no longer skips parents that contain variables / web components / images.** The WorkWithPlus convention omits these kinds from `childrenOrderedList` by design — IDE addresses them by `name`/`controlName`. Prior versions saw an unknown kind and bailed on the whole parent's list reconciliation with a misleading `"may not render in the IDE until corrected manually"` skip note. Now those kinds are treated as `NonOrderedKinds` — skipped from the list but the parent's other orderable children still get an updated `childrenOrderedList`.

### Internal

- `release.ps1` `Invoke-Cmd` parameter renamed from `$Args` (collides with PowerShell's automatic variable — `@Args` then splats the empty automatic instead of the caller's array; killed the v2.6.9 release at "Tagging $tag" with `git` printing its top-level help) to `$Arguments`.
- `PopupTemplateService.IPopupBackend` adds `ProbeWwpConvention()`. Test fakes return null so the flat-schema emit path stays exercised.
- Known limitation, tracked: the `.lock` per-object IDE-detection signal is a false-negative for currently-open tabs (GeneXus IDE doesn't write per-object lock files for tab opens; `<KB>/2635801/AcademicoHomolog.workspace` is only flushed on session close). Time-based safety net documented above is the practical fallback.

## v2.6.9 — 2026-05-25

Adds the REST/DB/GxServer/type/profiler/cross-platform tool surfaces, a self-extending recipe catalog, IDE-parity tools the previous releases left stubbed, and a `next_legal_actions` hint block that turns every state-changing response into a guided next call. Tool-list payload also drops ~6.6 % (~860 tokens) and per-response payload drops ~29 % (~74 B) from a metadata trim — both spec-clean MCP, no client opt-in.

### Added

- **`genexus_api action=list|describe|diff_baseline|snapshot`.** REST endpoint introspection over Procedures with Call Protocol: HTTP. `list` enumerates `{name, httpMethod, url, parms:[{name,direction,type,isCollection}], protocol, callMode, lastUpdate}`; `describe` adds requestSchema + responseSchema with 1-deep SDT inlining; `snapshot` writes a baseline under `<kbPath>/.gx/api-baselines/<name>.json`; `diff_baseline` compares current surface against a baseline and emits `{added, removed, changed:[{name, breaking:[...], compat:[...]}]}`. Breaking detectors: paramRemoved, httpMethodChanged, type-narrowed (Numeric M→M' with M'<M), direction-flipped. Compat: paramAdded, type-widened.
- **`genexus_db_optimize action=analyze|suggest_indexes|report`.** Static index advisor for DB-first GeneXus apps. `analyze` walks every Procedure/WebPanel For each block (regex parser handles nested blocks, line comments, multi-Where, Order clauses), canonicalises where-signatures (literals + variables stripped, attributes sorted), and ranks Transactions by access-pattern caller count. `suggest_indexes` proposes covering indexes per Transaction with DDL ready to paste; `report` emits markdown digest of top-N unindexed hot paths across the KB. Redundant-index detection: any non-unique non-primary index whose columns are a strict prefix of another is flagged.
- **`genexus_gxserver action=status|pending|conflicts|history`.** GeneXus Server (cloud) sync state surface. Read-only v1; detects connection via `Repository.gxs` / `.gx/gxserver-state.xml` / `.gxserver/state.xml` and emits `{connected:false}` graceful when the KB isn't linked. Multi-developer workflows now have a tool surface they can build on; full push/pull semantics land in a later release once the metadata layer is fully mapped.
- **`genexus_types action=list|describe|validate_value`.** Domain + SDT type-system bridge. `list` enumerates Domains/SDTs with one-line shape; `describe` returns full constraints including computed `rangeMin/rangeMax` for Numeric (e.g. Numeric(8,2) → ±999999.99) and `allowedValues` for enumerated Domains; `validate_value type=<X> value=<expr>` is a pure-function dry-check the LLM can call before assignment to catch overflow / domain-violation / length errors at edit-time instead of build-time.
- **`genexus_profile action=analyze|hotspots|correlate path=<xml>`.** Bridges to the GeneXus runtime profiler. Defensive XML parser walks any element with name+timing attributes (handles known shapes plus an unknown-schema fallback that surfaces `parserWarnings` instead of failing). `analyze` returns `{totalSampleMs, sampleCount, byObject:[{name,callCount,totalMs,percent}]}` sorted desc; `hotspots top=N` returns the top-N (N capped at 50); `correlate target=<x>` filters to entries matching the target substring.
- **`genexus_analyze mode=cross_platform_impact`.** Web vs. SmartDevices divergence analysis. Splits impact callers into Web / SD buckets by type-discriminator + caller-walk heuristic, then surfaces `{kind, field, Web, SmartDevices, severity, remediation}` divergence signals. v1 detectors: `required_field_mismatch` and `validation_rule_only_on_one_side`. Envelope includes `_meta.confidence` (low/medium/high) and `detectorsPending` so callers know what else can land in the next pass.
- **`genexus_recipe action=suggest_macro|crystallize`.** Self-extending recipe catalog. `suggest_macro [windowMinutes=30] [minRepetitions=3]` scans the gateway's OperationTracker ring buffer for repeated tool-call shapes (same tool sequence, same arg keys but different values), parameterises the varying args (`"<arg:NAME>"`), and proposes a name + step list. `crystallize macroName=<x>` writes the proposed macro as a real recipe under `<configRoot>/recipes/user-macros/<name>.json`; subsequent `genexus_recipe name=<recipeKey>` resolves it normally. RecipeCatalog discovers user-macros directory on lookup; no server restart needed.
- **`next_legal_actions` block on every state-changing response.** State-changing tools (apply_pattern, create_object, create_popup, edit, lifecycle, save_as, history, undo) now carry a 1-3-entry array of `{tool, args, why, priority}` suggestions for the next call. Cap of 3 keeps payload tight; read-only tools (whoami/query/list/read/inspect/analyze) emit no suggestions. Special-cased: `apply_pattern` error responses with `validParentTypes` route the LLM to `genexus_inspect` + `genexus_create_object` with a valid type pre-filled, eliminating the "which parent type is this?" round-trip.

### Performance

- **-29% per-response payload** (-1250 bytes measured over 17 representative envelopes; ~74 B/response). Three reducers all spec-clean MCP, no client opt-in:
  - `meta.tool` dropped from every response — the client already knows what it called.
  - `meta.schemaVersion` moved from every response to the `initialize` handshake's `_meta.schemaVersion`. Schema version is per-server-build, not per-call.
  - `meta` block emitted only when it carries real signal (`truncated`, `fields`, `totalByType`, etc.); empty `meta:{}` is suppressed.
  - `_meta.tokens.hint` omitted when null (~95% of responses).
  - For a 100-call session: **~7.4 KB saved.**
- **`genexus_edit mode=patch` overhead halved on the happy path.** Two compounding optimizations against `AcademicoHomolog1`, 100 samples across 5 targets:
   - Patch entry was invalidating the source cache unconditionally and re-reading from the SDK every call. Now reuses a fresh cache entry when no out-of-band write has been observed (`WriteService` tracks every write path). Stale-cache prevention still kicks in on external IDE edits + the 20 s TTL safety net.
   - Post-write verification was issuing a second SDK read (~85 ms) to confirm the persisted bytes matched. Now skipped on the clean-success path (no warnings, no `partialFlush`, no explicit `persistedVerified=false`); the safety-net SDK re-read still fires when WriteService's own envelope hints at a partial flush.
   - Combined: patch p99 1824 ms → 271 ms; p95 312 ms → 230 ms; p50 204 ms → 174 ms.
- **`genexus_whoami` first-call latency p50 406 ms → 1.4 ms (-99.6 %).** Whoami previously did a 400 ms timeout RPC to the worker on every call where the index-state cache was empty. The RPC almost always timed out (the worker's STA thread was busy with BulkIndex) and the snapshot was never cached — so every whoami paid the full timeout. Now on timeout we stamp a placeholder snapshot so the next call inside the 15 s cacheFresh window returns from cache in microseconds. The worker's own telemetry push overwrites the placeholder as soon as real state arrives.
- **`genexus_analyze mode=impact` repeat-call cache.** Same 30 s TTL + write-since invalidation pattern as inspect/explain. BFS over CalledBy is 25-60 ms on the STA thread per cold target; repeat queries within the window return sub-ms. Per-target invalidation survives a write to a different target — useful for alternating read/edit sessions where the gateway's broader semantic cache would otherwise wipe on every mutation. Cache is only consulted once the lite index pass has finished so the Reindexing-envelope contract remains intact.
- **`genexus_explain` repeat-call cache.** Same 30 s TTL pattern as inspect — `Explain` chains N sequential SDK reads (parm rule, variables, called procs, called transactions) on the STA thread. Repeat calls within the window return the previously-built summary in sub-ms when no write has landed against the target. Bench against `AcademicoHomolog1`: explain repeat-call p50 0.4 ms, sub-ms p95 on cached rounds (was 88 ms p95).
- **`genexus_inspect` repeat-call cache.** First inspect of a target still pays the SDK reads for signature / variables / structure / parts / controls / events / callers (~50-700 ms depending on object complexity, since the parallel `Task.Run` work serialises on the STA thread); subsequent inspects of the same target within 30 s return from in-memory cache in sub-ms. Cache key includes the `include` set and `type` filter; entries are invalidated automatically when `WriteService` records a write against the target. Measured against `AcademicoHomolog1`, 15 iterations round-robin over 5 targets: inspect repeat-call p50 0.9 ms → 0.5 ms, repeat-call p95 299 ms → sub-ms (the residual high p95/p99 reflects the unavoidable first-call SDK cost; structural skip of those reads needs an opt-in trim of the default `include` set).
- **New-user time-to-productive — gate accepts `LiteReady` and `Enriching`.** BulkIndex has a 2-stage pipeline (lite walk → enrichment). The lite pass already populates name/type/path/description/lifecycle for every object — enough for list_objects, query, inspect, read, edit. Previously the gateway fast-fail required `Status=Ready` (post-enrichment), so a new user with no on-disk snapshot waited the full enrichment cycle. Now `LiteReady` and `Enriching` are accepted; `mode=impact` still triggers on-demand per-target enrichment when its call-graph isn't populated yet. Measured against a real KB with the snapshot wiped (~5400 objects): list_objects became usable ~12–30 s earlier (at LiteReady) instead of after full enrichment.
- **Cold-start fast-fail for all SDK-bound tools.** First call to any worker-bound tool on a freshly-opened KB used to queue behind the initial BulkIndex on the single STA thread and eat the full 60 s gateway timeout before returning an opaque "Gateway timeout" error. The gateway now short-circuits to a structured `{status:"Indexing", code:"IndexNotReady", indexStatus, totalObjects, progress, hint}` envelope in <2 ms when the cached index state isn't yet "Ready". Covers `list_objects`, `query`, `read`, `inspect`, `analyze`, `explain`, `apply_pattern`, `search_source`, `inject_context`, `db_optimize`, `api`, `types`, `doctor`, `edit`, `edit_form`, `edit_and_build`, `save_as`, `create_object`, `create_popup`, `bulk_edit`, `navigation`, `kb_explorer`, `run_object`, `diff_generated`, `what_if`, `db_drift`, `orient`, `security`. Gateway-served tools (`whoami`, `recipe`, `lifecycle status`, `kb_diff`, `kb_import`, `sandbox`, `worker_pool`, `gxserver`, `profile`, `auto_test`, `learning`, `watch_event`, `execution_history`) bypass naturally and stay responsive. Worker-side ListService keeps a matching fast-fail for callers that skip the gateway short-circuit. Measured against `AcademicoHomolog1`: every SDK-bound tool returns `{status:"Indexing"}` in <2 ms during the ~60 s cold-start window instead of timing out; once the worker reaches "Ready", calls flow through normally (steady-state: list_objects p99 31 ms, query p99 77 ms, inspect p99 716 ms, others sub-130 ms).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Error envelope dual-key consolidation.** Hand-built error envelopes across 36 worker services historically emitted `{status:"Error", error:"..."}`; the 18 newly-promoted tools used `{status:"Error", message:"..."}`. The codebase carried both conventions in roughly equal split, and the in-flight `McpResponse.Error()` helper had been emitting BOTH keys defensively (doubling bytes on every error envelope). Now canonical key is `["message"]` (REST / JSON-Schema convention, what new tools already used). McpResponse helper migrated; the 95+ hand-built envelopes across worker services swept to match; 11 test assertions migrated; gateway-side `TrimErrorEnvelope` still reads `error["message"] ?? error["error"]` for back-compat with any unmigrated path. Net: one canonical key, less bandwidth, no LLM ambiguity.
- **Live-KB Gateway E2E now 7/7** (was 4/7 before this release; 11 `[LiveKbFact]`-gated tests had never actually executed in CI). Root causes resolved:
  - `LiveGatewayHarness` is now an `IClassFixture<>` shared across all 7 tests in a class. Previously each test spawned + killed its own gateway+worker in 500 ms; the kill left shared SDK + KB-lock state that crashed the next worker's boot mid-cycle. The fixture also drops the total E2E suite runtime from ~3 min+ to ~1 m11s and is more representative of real MCP usage (one long-lived gateway, many calls). Dispose grace 500 ms → 2 s.
  - Disposable WebPanel name slicing bug in `ApplyPattern_Validate_HappyPath_OnWebPanel`: `Ticks.ToString("X").Substring(0, 6)` was slicing the high-order hex digits (change ~hourly), causing two test runs in the same window to collide on the same name. Switched to last-6-hex (~100 ns granularity).
  - `LiveKbFactAttribute` gained `requiresParityFixture: true` so `Integration_ParityProbe_GeneratesReportToTempPath` skips gracefully when its `GXMCP_PARITY_MCP_NAME`/`_IDE_NAME` env vars aren't set, matching how `requiresWWP` works.
- **`McpRouter.TrimErrorEnvelope` was over-aggressive.** Default trim kept only `{message, code, hint, suggested_next_step}` and dropped structured routing fields the LLM needs to self-correct: `validParentTypes`, `parentType`, `patternKey`, `target`, `type`. Worst case: "WorkWithPlus cannot be applied to a Procedure." reached the agent with NO information about which parent types ARE valid even though the worker emitted them. Now preserves a small allowlist of routing fields, plus the worker's `status` field when it's not literally "Error" (so "NotImplemented", "NotApplicable", etc. survive the trim).
- **Worker unit-suite `PatternApplyServiceTests.ApplyPattern_*` intermittent NRE** on cold runs. The per-collection `DisableParallelization` flag only serialises within a collection; classes in different collections still ran in parallel and could race on `Console.Error` redirection or static SDK probes. Switched to assembly-wide `[assembly: CollectionBehavior(DisableTestParallelization = true)]`. Runtime cost: 7s → 18s. Stability gain: 5/5 consecutive runs at 909/913 (was 1-in-3 flaking).
- **Pattern write verification failures now surface the diff.** Writing a `PatternInstance` part to a WorkWithPlus host produced a bare `{message:"Pattern write verification failed"}` envelope when the SDK silently normalised the input — no clue what was sanitised. Now the error carries `details` (the textual diff), `verifyDiff` (structured per-element rejected/added attribute list), `persistedSnippet` (first 800 chars of what the SDK kept), and `requestedSnippet` (first 800 chars of what you sent) so a side-by-side compare pinpoints the rejected attribute or child. `TrimErrorEnvelope` also expanded its allowlist (`details, verifyDiff, suggestion, persistedSnippet, requestedSnippet, availableParts, part, objectName, objectType`) so these survive the terse-default trim.
- **`genexus_apply_pattern reapply=true` no longer hangs the worker when the IDE has the object open.** The SDK's `UpdateParentObject` projection step deadlocks for 10+ minutes when the GeneXus IDE holds an open handle on the parent WebPanel or its WWP host — a single tab open in the IDE was enough to wedge the worker thread and block every subsequent MCP call. Reapply now pre-checks `<KB>/Locks/<guid>.lock` for both the parent and the `WorkWithPlus<Name>` host and fails fast with `{status:"Error", code:"IdeHoldsLock", lockedObjects:[{role, object, guid, lockFile, lockedAtUtc}], hint:"Close … in the GeneXus IDE before calling reapply."}` instead of blocking. The IDE and the MCP worker can't hold the same KBObject handle simultaneously.
- **`genexus_apply_pattern reapply=true` now re-asserts "Apply this pattern on save"** on the host so the IDE's pattern checkbox stays on across reapplies. First-apply already invoked `PatternInstancePackageInterface.SetPatternApplyOnSave` so the checkbox lit up on initial attach; reapply previously skipped that step, so a host whose checkbox was manually unchecked stayed unchecked even after a successful reapply and the next PatternInstance edit wouldn't regenerate the parent's WebForm. Reapply now re-invokes the SDK helper and force-saves the host so the flag survives the IDE's next refresh.

### Security

- **PreviewService.EscapeJs widened** (security audit LOW #1 follow-up). Existing impl only escaped backslash + single-quote, fine for current single-quoted-JS-literal call sites but brittle if reused in a double-quoted or HTML-attribute context. Now also handles `"`, CR/LF, and the `</` → `<\/` sequence so embedded scripts can't break out of a `</script>` boundary in any future call site.

### Added — IDE-parity tools

- **`genexus_tutorial step=<1..6>`.** Deterministic 6-step onboarding walkthrough. Each step returns `{stepNumber, totalSteps, title, narrative, suggestedCall, next}` so a fresh agent can self-orient without reading source.
- **`genexus_voice transcript=<text>`.** Maps a natural-language phrase (e.g. `"add button called Confirmar"`) to a concrete dispatched tool call (`{matched, dispatchedTool, dispatchedArgs}`). Returns `{matched:false, unrecognised:true}` for phrases outside the recipe table.
- **`genexus_time_travel name=<obj> at=<ISO-or-sha>`.** Recovers an object's part bytes from git history. ISO timestamps resolve through `git log --before=<at> -1`; commit SHAs (7-40 hex chars) bypass the log. Returns `{recoveredFromCommit, parts:[{path, bytes, content}]}` — read-only, no KB write. Surfaces `KbNotInGit` when `.git` is missing.
- **`genexus_ai_complete context=<text>`.** Optional bridge to a customer-hosted completion endpoint (env vars `GXMCP_AI_COMPLETE_URL` / `GXMCP_AI_COMPLETE_KEY`). Returns `{code:"AiEndpointNotConfigured"}` when unset so the LLM can fall back gracefully.
- **`genexus_cross_browser target=<obj> browsers=[chrome,firefox,webkit]`.** Parallel render of the resolved object URL across multiple browser engines. Chrome → `chrome-devtools-axi`, Firefox/WebKit → `npx playwright`. Per-browser graceful skip when the driver isn't installed.
- **`genexus_auto_test action=generate_from_prod_log path=<jsonl>`.** Reads a JSONL log of `{atUtc, tool, target, params}` records and emits GXtest stubs unique by `(tool × target)`. Skips malformed lines.
- **`genexus_reverse_pattern action=infer source=[X,Y,...]`.** Walks ≥2 similar objects, extracts variables (regex on Variables part), event names (Events source scrape), parm signatures (`parm(...)` in Rules) and reports `{commonVariables, commonEvents, commonParmSignature, parmSignatureMatchesAll, divergencePoints}`. Diagnostic only — does not generate a real WWP pattern.
- **`genexus_github action=create_pr title=<…> body=<…>`.** Shells out to `gh pr create`. Returns the PR URL on success, `{code:"GhCliNotInstalled"}` when `gh` is absent, `{code:"GhExitNonZero", exitCode, stderr}` on failure.
- **`genexus_kb_import from=<path> name=<X> type=<Procedure|...>`.** Best-effort import of an external part-bytes file as a new KB object. Validates source path exists; returns typed `BadRequest` for invalid declarations.
- **`genexus_kb_diff kbA=<path> kbB=<path>`.** Cross-KB structural diff. Returns `BadRequest` when paths are identical or one is unreachable.
- **`genexus_rename_across_kb from=<old> to=<new>`.** Routes through `RefactorService` for attribute/object renames across all referencing objects in one shot.
- **`genexus_sandbox action=create|remove|status name=<x>`.** Lightweight named scratch space under `.gx/sandboxes/`. Idempotent: `remove` on nonexistent → `NotFound`, not error.
- **`genexus_worker_pool action=warm_spares spareCount=<n>`.** Pre-warms `n` spare worker processes in the pool so first calls avoid cold-start. `spareCount=0` returns `Disabled`.
- **`genexus_sd_panel action=inspect name=<x>`.** Smart Device Panel layout inspection; returns the parts inventory + control tree. Graceful `NotFound` on bad name.
- **`genexus_multi_agent_lock action=status|acquire|release target=<obj> part=<X>`.** File-system advisory lock under `.gx/locks/<obj>__<part>.lock` so multiple AI agents editing the same KB don't clobber each other. Status returns `{held, holder, since, path}`.
- **`genexus_what_if change={kind,attribute,newType,...}`.** Read-only impact analysis: enumerates the callers, tables, indexes that `change` would touch. Validates required arguments and surfaces `MissingTarget` clearly.
- **`genexus_watch_event target=<obj> event=<Name>`.** Pulls every recent execution of `<obj>.<Event>` from the OperationTracker ring buffer with timestamps, args, and outcomes — for diagnosing flaky events without scraping logs.
- **`genexus_learning action=report`.** Aggregates the per-session friction log (`.gx/friction.jsonl`) into a structured summary: `{totalEntries, topPainPoints, byTool, byErrorCode, suggestedRecipes}`. Lets the LLM notice patterns ("the user has hit `Spc0150` 5 times today; recommend `extract_to_procedure` recipe").

### Added — broader tool surface

- **`genexus_save_as`.** IDE Save-As parity for any creatable object type — Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, Domain, Dashboard, etc. Clones every part under a new name in the same module. `includePatternInstance=true` also clones a linked `WorkWithPlus<X>` pattern instance.
- **`genexus_explain`.** Deterministic, stakeholder-readable summary of an object: purpose (derived from description + type + name), input/output parm rules, variables, top-5 called procedures, top-5 called transactions, last-modified. `depth=deep` recurses one level into called objects. NOT raw source.
- **`genexus_diff_generated`.** Unified diff of an object's generated artifacts (`.cs` / `.aspx` / `.js` / `.html`) vs a baseline. `against=last-build` reads `.gx/build-baselines/<obj>/<UTC>/<file>.txt`; `against=git-head` shells out to `git show HEAD:<path>` (surfaces `KbNotInGit` when the KB isn't a git repo). Returns per-file diff + `addedLines` / `removedLines` plus an overall `totalChangedLines`.
- **`genexus_kb_readme action=generate`.** Walks the KB and produces a Markdown README: name + path, primary entities (Transactions sorted by inbound reference count), entry points (Startup / DefaultObject), modules, top-10 most-edited objects from `.gx/snapshots/`. `outputPath` writes to disk; otherwise the markdown is returned inline.
- **`genexus_kb_explorer action=locate`.** "Locate in KB Explorer" parity. Returns `{ name, type, modulePath, fullPath, siblings, truncated }` where `modulePath` is the dotted folder path and `siblings` lists up to 20 other objects in the same module.
- **`genexus_kb action=set_startup` / `action=get_startup`.** "Set As Startup Object" / inspect parity. Sets the active Environment's `StartupObject` env property; get returns the current value plus the `DefaultObject` fallback (same resolution `KbService.GetLauncherObjectName` uses).
- **`genexus_navigation action=view`.** "View Navigation" / "View Last Navigation" right-click parity. Wraps the existing `genexus_sql action=navigation` so the IDE semantic is discoverable. `latest=true` returns the last cached navigation; otherwise runs a fresh navigation.
- **`genexus_blame name=<obj> part=<X> line?=<N>`.** Per-line git-blame attribution against the parts the SDK writes to disk. Returns `{commitHash, author, date, summary, snippet, line}`. `code: "KbNotInGit"` when the KB isn't a git working tree.
- **`genexus_lifecycle fastIncremental=true`** (EXPERIMENTAL). Reads the `EditDirtyTracker` set of dirty `(kbPath, target)` tuples; the build pipeline surfaces `{canSkipDeploy, canSkipSpecify, fallbackReason}` in the response. Default behaviour unchanged. SDK-deep skip wiring lands next release.
- **`genexus_worker_reload mode=warm`** (EXPERIMENTAL). Persists `IndexCacheService` state to `<kbPath>/.gx/index-snapshot.bin` with a SHA-256 of `GxMcp.Worker.dll` in the header before the reload. The boot-side restore lands when `IndexCacheService.TryLoadFromSnapshot` is added; until then the snapshot is captured but not replayed (`warmReloadFallback: true` surfaces this).
- **`genexus_run_object`.** Resolves the runtime URL for a KB object (active Environment webRoot + lowercase `.aspx` + URL-encoded positional args) and optionally captures GAM session cookies via an HTTP-level login (no browser launch). Caller pipes the returned URL into `chrome-devtools-axi open` or `curl` directly — replaces the `dani.aspx` glue every dev keeps locally.
- **`genexus_bulk_edit transactional=true`.** Pre-snapshots every target via `EditSnapshotStore` before applying any edit; on first Error replays each successful write in reverse using the snapshot bytes. Default behaviour (best-effort, `stopOnError`) is unchanged.
- **`genexus_edit_form`.** Semantic WebForm edits with `action` enum: `add_textblock`, `add_button`, `set_visibility`, `remove_control`, `wrap_in_fieldset`. Mutates the in-memory XML tree, then routes through the existing typed-write path so descriptor-name auto-routes (`OnClickEvent` → `Event` etc.) still apply. Eliminates a whole class of "Invalid visual XML" errors compared to raw-XML edits.
- **`genexus_db_drift`.** Transaction ↔ database schema drift detection. `action=check` returns structured findings (`missing_table` error, `missing_column`/`type_mismatch`/`missing_index` warning, `orphan_column`/`orphan_index` info); `action=report` adds a markdown summary. Adopts `reorg_preview`'s DDL plan as the authoritative drift signal (`source: "reorg_plan"`) when direct DB introspection isn't reachable.
- **`genexus_recipe name=feature_scaffold`.** Orchestrates the full scaffold of a feature from a structured spec (`{entity, ui, procedures, tests?}`): Transaction → optional WorkWithPlus pattern → Procedures with stubbed parm rules → optional test stubs. `dryRun` returns the plan without executing; partial failures short-circuit and surface `{completedSteps, failedStep, hint}` so the caller can `genexus_undo` if needed.
- **`genexus_sdk_probe`.** Reflective dump of the GeneXus SDK assembly surface (types, methods, properties) to `docs/sdk-probe/`. Use when hunting for SDK entry points instead of guessing names like `Form.GoTo`.
- **`genexus_lifecycle action=reorg_preview`.** Returns the list of `ALTER TABLE` / `CREATE TABLE` / `DROP COLUMN` statements the next reorg will execute, plus a summary (tables_added, columns_added, columns_dropped). Run before the destructive `action=reorg`.
- **`genexus_lifecycle notifyOnFailure=<webhook-url>`.** POSTs `{kb, target, errors, errorsDetailedHead, jobId, durationSec}` to a Slack/Discord webhook when the build terminal state is `Failed` (not `PartialSuccess`). One-shot, no retries, no auth.
- **`projection=minimal|standard|verbose`** on `genexus_inspect` and `genexus_list_objects`. `minimal` returns name+type+lastUpdate; `standard` is the existing compact projection; `verbose` includes every verbose-gated field. Overrides `axiCompact` when set.
- **`genexus_security action=scan_secrets`.** Searches Procedure/WebPanel/SDT Source for credential-shaped literals. Extended detectors: three-segment JWTs, any PEM `KEY` or `CERTIFICATE` block, connection strings carrying both `User Id`/`UID` *and* `Password`/`Pwd`. Each finding carries `{severity, code, message, remediation}`.
- **`genexus_recipe name=list` with concrete examples** and **`action=describe name=<recipe>`** for full prose docs. The list view now ships a copy-pasteable args block for each recipe so the agent can apply without a second round-trip.
- **Auto-format on Events writes.** Normalizes indentation, aligns `=` columns in assignment blocks, collapses 3+ blank lines to 1. Runs after patch context-match, before SDK save. Opt out via `autoFormat=false`.
- **Universal `dryRun=true`.** Every edit-shaped tool (`genexus_edit`, `genexus_apply_pattern`, `genexus_create_object`, `genexus_create_popup`, `genexus_edit_and_build`, `genexus_history action=restore`, `genexus_undo`) returns a unified diff of pre/post part bytes without persisting.
- **`code` + `docUrl` on every gotcha and lint.** Each warning entry carries a stable PascalCase `code` and a `docUrl: genexus://kb/tool-help/gotchas/<code>` the agent can fetch for context. Affected emit sites: `HtmlFormatGotcha`, `Spc0150Preflight`, `LayoutGotchaScanner`, `LintKbCharsetLossy`, and the new lints below. Renames: `kb_charset_lossy` → `LintKbCharsetLossy`, `PreflightSpc0150` → `LintSpc0150ForEachAttributeWrite`.
- **`suggested_next_step` on errors.** McpError envelopes now carry a pure pattern-matched hint for patch NoMatch/Ambiguous, Visual write failure, `KB_AMBIGUOUS`, and spc0150 — so the agent's next call has a clear target instead of a guess.
- **`LintSqlInjection`.** Scans `Events` and Procedure `Source` for `For each Where attr = &var.Concat(…)`, string-concat-built Where clauses, and `&dyn = "where …" + &v` dynamic SQL builds. Non-blocking warning with line number and a suggested parametrised alternative.
- **`LintMasterPageIncompat`.** Scans WebForm writes for controls that depend on a different master page (`gxMessages`, `gxAttribute ControlType=ProgressIndicator`, `gxMenu`, `gxBreadcrumb`, `gxNotification` — conservative list). Surfaced before build.
- **`GotchaWebFormTypedPropertyAutoRouted` warning.** When the SDK silently renames a descriptor property at write time, the response now names the rename explicitly (`{from, to, element, controlId}`). New auto-routes: `OnEnterEvent` and `CaptionExpression` join the existing `OnClickEvent` → `Event` for gxButton and `eventGX` for gxAttribute / gxImage.
- **Browser-driver fallback.** `chrome-devtools-axi` on PATH is preferred; `npx playwright` is the automatic fallback; if neither is available the response carries `BrowserDriverUnavailable` with both install hints. Surfaced in `whoami.browserDriver`.
- **`genexus_inspect include=["runtimeIds"]`** — maps design-time control IDs (`BtnConfirmar`, `GrpNumRegProf`) to runtime HTML element IDs (`BTT58`, `GRPNUMREGPROF`) by parsing the generated `.cs` from `GXSPC*/GEN*/web/`. Returns `kind` (gxButton/fieldset/gxAttribute) and `hidden` per entry. Requires a prior build; emits `runtimeIdsNote` when no generated file is found. Eliminates the `Grep -P '_Internalname'` round-trip agents previously needed before targeting controls with `chrome-devtools-axi`.
- **`genexus_undo last=N`** — reverts the last N edit snapshots from `.gx/snapshots/` in reverse-chronological order. Default 1, hard cap 20; when `last>20` the response surfaces `capped/requestedLast/effectiveLast/hint` instead of silently clamping.
- **`genexus_security action=audit_gam`** — scans `<kbPath>/Environments/*.xml` for `IntegratedSecurityLevel=None`, `USE_ENCRYPTION=NONE`, `GAM_DEFAULT_TOKEN_EXPIRES > 24h`, JWT-shaped tokens, and PEM private keys in env property dumps. Each finding carries `{severity, code, message, remediation}`; envelope rolls up `worstSeverity`. File-scan only (no SDK GAM API dependency) so it works without a fully initialized KB.
- **`genexus_orient`** — welcome card for a new session: KB name/path, last 5 unique edited objects from `.gx/snapshots/`, top 3 baseline gotcha hints, static top-tools default. Cheaper than `whoami` for mid-session context refresh. Live per-session stats remain in `whoami.stats.tools`.
- **`genexus_apply_pattern mode=diagnose`** — read-only preflight that returns structured findings (`parentTypeMismatch`, `overrideConflict`, `templateInvalid`, `missingRequiredAttribute`, `ok`) with per-finding `remediation`, without mutating the KB. Stop guessing why WorkWithPlus silently no-ops.
- **`genexus_search_source fields=["source","caption","description","parmNames"]`** — opt into a wider search surface than the default `[source]`. Catches the cases where the term lives in a `CaptionExpression`, an object description, or a parm-rules signature.
- **`genexus_analyze mode=callers target=<X>`** — per-call-site detail (`object`, `part`, `line`, `context` of ±3 lines) instead of just the flat caller list `mode=impact` returns. The ReadObjectSource error-envelope filter is now case-insensitive and recognizes both `{"status":"Error",…}` and bare `{"error":"…"}` shapes.
- **`genexus_logs` filtering** — `tail=N`, `since=<ISO>`, `objectFilter=<name>`, `grep=<pattern>`. Response carries `logPath` and back-compat `path` so the agent can read adjacent logs (`gateway_debug.log`, `probe.log`) via `genexus_asset`.
- **`whoami` worker memory + stats blocks** — `worker.memoryMb` / `worker.uptimeMin` with `reloadHint` when heap > 1.5 GB or uptime > 2 h. New `stats.tools` block emits `p50Ms` / `p95Ms` / `count` per tool from an in-memory ring buffer (resets on gateway restart; documented in `stats.tools.note`).
- **3 new `whoami.playbooks` entries** — `html_form_inline_js` (which raw HTML inline event-attrs survive GeneXus's sanitizer and which don't), `popup_call_async` (`.Popup()` is non-blocking, out-params arrive in a subsequent Refresh that AUTO_REFRESH=VARS_CHANGE may not fire), `verify_in_browser` (the `chrome-devtools-axi` CLI usage cheat-sheet).
- **3 new `genexus_recipe` macros.** `popup_blocking_with_reload` — synchronous popup gate with `<body onmousedown>` reload hook that works around the AUTO_REFRESH unreliability. `radio_group_show_hide` — raw HTML radios inside `Format="HTML"` gxTextBlock + hidden gxAttribute bridge for the selected value. `extract_to_procedure` — lift attribute writes from WebPanel Events into a Procedure to satisfy spc0150.
- **`_meta.tokens` on every tool response** — `{ used, limit, hint }` injected post-serialization so the count reflects the bytes the client actually receives. `hint` is set when used crosses 50 % of `MetaTokenLimit` (25 000 by default). Pre-existing `_meta.tokens` blocks are respected, not overwritten.
- **`genexus_edit replaceAll=true`** — apply a patch to every occurrence of the find context across exact, fuzzy (whitespace-tolerant), and whitespace-normalized fallback paths. Previously the flag was advertised but only honored on the exact path. When ambiguous matches remain on the whitespace-normalized fallback, the error message now mentions `replaceAll=true` explicitly.
- **`eolDiff` + `did_you_mean` on patch NoMatch.** When `genexus_edit mode=patch` fails to match, the response now emits a short per-line diff comparing the first 3 lines the agent passed against the corresponding source lines (each tagged `exact|eol_only|differs`). When edit distance to the best near-match window is below `ceil(0.20·len)` (gated on context ≤ 2000 chars and similarity ≥ 0.50 to keep the STA thread responsive), a `did_you_mean` block points at the candidate line range with a snippet.
- **`GotchaHtmlFormatScriptStripped` warning on WebForm writes.** When a `genexus_edit part=WebForm` payload contains `<gxTextBlock Format="HTML">` with `<script>`, `<iframe>`, or `<img onerror=…>` inside the CDATA, the response now carries a `warnings[]` entry explaining the GeneXus HTML generator escapes those tags so the JS will not execute. Write still succeeds.
- **`PreflightSpc0150` lint on WebPanel Events.** When `genexus_edit part=Events` on a WebPanel writes attribute assignments inside a `For each / endfor` block, the response surfaces a warning pointing at the new `extract_to_procedure` recipe. Catches the spc0150 build error 60 s earlier.
- **`genexus_lifecycle action=cancel`** — cancel an in-progress build via its `job_id`. Previously the only way out of a wedged build was `worker_reload force=true` (which kills every tool surface in the session).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Build success was wrapped in `<e>error{…}</e>`.** `genexus_lifecycle build wait_until_done=true` returned `"Build succeeded: 0 warnings, 0 errors"` inside the MCP error envelope because the wait path compared `JobEntry.Status` against `"completed"` when the registry actually stamps `"succeeded"`. Clean success now classifies as `isError=false`; `partial_success=true` uses the `warning` envelope.
- **Patch write-fallback false negatives.** "Patch write fallback failed after persistence mismatch" fired even when the write had been applied. Now distinguishes `write_not_persisted` (retry-safe error) from `persisted_with_concurrent_change` (write OK, hash drifted post-write — returns `Success` with a `postWriteHashDrift` warning and a `RequiresReread` flag).
- **`genexus_logs since=<ISO>` was off by the worker's timezone offset.** `since` was parsed with `RoundtripKind` (preserving `Z` when present) and compared directly against log-line timestamps parsed with `AssumeLocal`. A client passing `2026-05-22T14:00:00Z` to a worker running in UTC-3 was seeing a 3-hour window of unrelated lines. Both sides now normalize to UTC before comparing.
- **`UndoService` was sorting snapshot files by full path, not by timestamp.** Snapshot filenames are `<guid>-<part>-<yyyyMMddTHHmmssfffZ>.bak`; an ordinal sort of the full path is dominated by the leading GUID, so the "most recent N" selection was silently arbitrary. New `ExtractSnapshotTimestamp` helper isolates the ISO-8601 segment and the sort uses that. Agents calling `genexus_undo last=1` now actually get the newest edit reverted.
- **`AnalyzeService.FindCallerSites` was dropping callers whose source contained the literal word "error".** Previous heuristic `src.Contains("\"error\"")` matched legitimate source code that contained `"error"` mid-line. Now requires the source to start with `{`, then matches `{"status":"Error",…}` (case-insensitive) OR `{"error":…}` near the front of the JSON. Sources that legitimately mention "error" mid-line are no longer dropped.
- **`replaceAll` was silently dropped on `genexus_edit {targets:[…]}` batch patches.** The schema advertised the flag for `mode=patch`, but the multi-target path through `BatchService.BatchEdit` did not forward it to `ApplyPatch`, so each per-target change fell back to `expectedCount=1` semantics and returned `Ambiguous` on N>1 matches. Now forwarded per change item.
- **Heartbeat lambda fired for JSON-null `_meta.progressToken`.** Clients sending `{"_meta":{"progressToken":null}}` were passing the C# `!= null` check (JToken.Null is non-null in .NET) and the gateway was wiring a heartbeat lambda anyway; `LongPollJob` then rejected it via its own `JTokenType.Null` check. The gateway now explicitly checks `Type != JTokenType.Null` at both call sites so the safe-cap path is taken consistently when no useful token was supplied.
- **`_meta.tokens.used` under-counted the emitted payload.** The original implementation computed `used` from the inner JSON text before stamping the `_meta.tokens` block, so responses sitting near the 50 % threshold could cross the threshold after injection without ever getting the pagination hint. The block is now stamped first with `used=0`, the JSON serialized to measure final size, then `used`+`hint` updated in place.

### Internal

- Tool-definitions token budget raised to 13150 to fit the new tools; the actual measured size is ~13081 after this release's description trim sweep.

## v2.6.8 — 2026-05-22

Two streams in one release: lifecycle metadata on `genexus_list_objects` / `genexus_query` (the agent finally has a "what changed?" view without round-tripping the filesystem) and a crash-resilience pass for the gateway↔worker pipe after a user's VS Code Codex session lost its MCP transport on a single bad `lifecycle` call. A post-implementation code review found 8 correctness bugs — all fixed in this same release.

### Lifecycle metadata on discovery tools

- **`lastUpdate`, `createdAt`, `lastModifiedBy` per object** — populated from `KBObject.LastUpdate` / `VersionDate` / `UserName` during both the lite-pass and incremental `UpdateEntry`. `lastUpdate` ships in the default compact projection (ISO-8601 UTC, ~30 bytes); `createdAt` + `lastModifiedBy` are verbose-only to keep the default shape tight.
- **`sort=name|lastUpdate` on `genexus_list_objects` and `genexus_query`.** `lastUpdate` returns newest first; on `query` it also bypasses the relevance scorer so callers asking for recency aren't fighting the score ranking. Default stays `name` (list) / `relevance` (query).
- **`since` / `modifiedBefore` filters.** ISO-8601 UTC bounds; `since` inclusive, `modifiedBefore` exclusive. Items with no recorded lifecycle stamp are excluded once any bound is set — "modified before X" is meaningless for items with `LastUpdate=MinValue`.
- **Stable `cursor` pagination.** Opaque base64url token `(ts, name, guid)` matches the full sort tuple — the resume predicate replays the same `LastUpdate desc, Name asc, Guid asc` order the OrderBy used, so paging across a mutating KB no longer skips or duplicates items. `nextCursor` is emitted alongside `nextOffset` so callers can opt in. Legacy 2-part `(ts, guid)` tokens still decode for back-compat. Cursor also handles the "Untouched" (MinValue) tail without truncating.
- **`_meta.aggregates.lastUpdate.min/max`** and **`modified_last_7d`** — per-page lifecycle window so the agent can decide whether to drill deeper or page on.
- **`_meta.aggregates.by_author`** — per-page lastModifiedBy counts (highest first). Surfaces "who's been touching this area" for free when items carry author data.
- **`_meta.alternative_views.recently_changed`** — emitted whenever the page carries any lifecycle data, pointing the agent at `{ sort:"lastUpdate", limit:20 }` as a one-call switch to the temporal view.
- **`genexus_inspect` lifecycle block.** Same `lastUpdate` / `createdAt` / `lastModifiedBy` triplet attached to the `metadata` projection so a single inspect tells you when the object was touched and by whom — no extra round-trip.
- **`whoami.index.recentlyChanged`** — top-5 most-recently-modified IndexEntry objects projected by the worker on every `GetIndexState` push. First-turn "what's hot in this KB" hint for the agent.

### Crash resilience (gateway↔worker)

- **Eager worker respawn on `OnWorkerExited`.** Gateway fires a background `AcquireAsync` immediately when a worker dies instead of lazy-spawning on the next call. Short-timeout MCP clients (VS Code Codex closes the transport after a few seconds of silence) no longer see the worker boot's ~10–15 s cold-start as a transport hang. Eager respawn first calls `WorkerPool.Close(alias)` so the AcquireAsync fast-path can't return the just-exited handle if `WorkerPool`'s own entry-removal subscriber hasn't fired yet.
- **`SuppressEagerRespawn()` scope.** Refcounted IDisposable wired around `RestartWorker.StopAll()` and the `worker_reload force=true` kill path so planned restarts don't race the eager respawn that orchestrates its own fresh spawn.
- **`whoami` degraded mode.** When the active worker is dead or booting, `whoami` returns instantly with `workerHealth: { status:"respawning", hint:"…" }` (always, regardless of cache freshness) and stamps `index.status="Booting"` when the cached snapshot is also stale. Multi-KB / no-default-KB setups now probe every open worker via `ListOpen()` instead of routing through `KbResolver.Resolve(null,…)` (which throws `KB_AMBIGUOUS` on 2+ open KBs and was being swallowed as "not healthy"). The 400 ms RPC refresh is skipped entirely when no worker is alive, so the call stays sub-100 ms in the degraded case.
- **`genexus_logs since=crash`.** Slices the worker log from the most recent `[ERROR]` / `[CRITICAL]` / `CRITICAL Init|Error|Failure|Exception` / `Unhandled exception` marker (precompiled regex with anchored bracket/word boundaries — bare "critical section" in a debug line no longer trips it) + 5 lines of leading context. Includes `crashLineIndex` and a `hint` block when no markers exist. Users reporting a crash get a focused, paste-ready snippet.

### Installer

- **VS Code (stable) + VS Code Insiders native MCP registration.** `install.ps1` writes to `%APPDATA%\Code\User\mcp.json` and `Code - Insiders\User\mcp.json` in addition to the existing Claude / Codex / Cursor (Cline) / Antigravity hooks. Each variant is independent — silently skipped when not installed. New `-SkipVsCodeMcp` switch for parity with `-SkipClaudeConfig` etc. Extension VSIX push via `code` / `code-insiders` CLI was already wired; this closes the loop so VS Code agents can discover the MCP server without manual `mcp.json` editing.

### Post-implementation review caught 8 correctness bugs (all fixed in this release):

- **Cursor predicate missed the Name tiebreak.** Sort was `LastUpdate desc, Name asc, Guid asc` but the resume predicate only checked `(LastUpdate, Guid)`. Two items sharing a LastUpdate but with Names that out-sort the cursor's Name AND Guids that under-sort the cursor's Guid (e.g., A=`(T,'Alpha','g1')`, B=`(T,'Bravo','g0')`) silently dropped B. Cursor now carries `(ts, name, guid)` and the predicate replays the full tuple. Legacy 2-part decoder retained for back-compat.
- **Eager-respawn handler stamped `Booting` AFTER `AcquireAsync` returned.** Fresh telemetry pushed by the new worker (status=Indexing, totalObjects=5k, progress=0.4) was getting clobbered by the explicit `UpdateLastKnownIndexState("Booting", 0, …)` call. Stamp removed — the new worker pushes its own state.
- **Eager respawn fired during planned `worker_reload`.** `RestartWorker.StopAll()` raised `OnWorkerExited`, which scheduled an eager respawn that raced with the reload's own spawn (double-spawn or pool-disposed exceptions). New `SuppressEagerRespawn()` refcounted scope wraps both `RestartWorker` and the force-reload path.
- **`workerHealth` signal silenced for 15 s after every cache refresh.** Original gate `!workerHealthy && !cacheFresh` suppressed the degraded signal during the exact window VS Code Codex's short transport timeout most needed it. Gate split: `workerHealth` block is always emitted when the worker is unhealthy (purely additive); only the `index.status="Booting"` rewrite stays gated on `!cacheFresh`.
- **`IsActiveWorkerHealthy` threw on multi-KB / no-default-KB setups.** Called `_kbResolver.Resolve(null, _workerPool.ListOpen())`, which throws `KbResolutionException` for both "ambiguous (2+ open)" and "no default + none open". The outer catch returned false → every multi-KB whoami falsely reported `respawning`. Now probes every open worker directly; healthy if any one is alive.
- **`nextCursor=null` with `hasMore=true` truncated the MinValue tail.** When sort=lastUpdate and a page boundary landed inside the "Untouched" (no-timestamp) tail, `EncodeCursor` returned null but `hasMore=true`. Caller had no token to continue. Encoder now allows MinValue ts when name/guid are present.
- **Eager-respawn race with `WorkerPool`'s entry-removal subscriber.** Both `Program` and `WorkerPool` subscribed to the same `OnWorkerExited` event; if Program's `Task.Run` reached `AcquireAsync` before `WorkerPool`'s `TryRemove` fired, AcquireAsync's fast path returned the dead worker. Eager respawn now calls `WorkerPool.Close(alias)` first.
- **`since=crash` matcher matched benign "critical" mentions.** Bare `IndexOf("CRITICAL", OrdinalIgnoreCase)` caught `entering critical section`, `no critical errors`, etc. Replaced with a precompiled regex requiring bracket markers (`[ERROR]`, `[CRITICAL]`, `[FATAL]`), the `CRITICAL Init|Error|Failure|Exception` pattern used by `Logger.Error`, or `Unhandled exception` — all word-anchored.

### Tests

- New `TemporalListTests.cs` (13 tests): cursor encode/decode round-trip incl. legacy 2-part, sort=lastUpdate ordering, since/modifiedBefore inclusivity/exclusivity, cursor resume, cursor-with-sort=name noop, aggregates min/max + 7-day count, by_author ordering, lastUpdate projection (default + verbose + skipped on MinValue), empty-since-window empty_reason.
- New `TestFixtures.IndexWithLifecycle` — 6-entry fixture spanning 30 days with 4/2 author split + one MinValue "Untouched" entry to exercise the skip-on-emit path.
- Schema-size budget bumped 6500 → 6700 to accommodate `sort` / `since` / `modifiedBefore` / `cursor` on `genexus_list_objects` and `genexus_query` (~55 tokens net). 140 tokens of headroom for the next small batch.
- Discovery golden fixture (`tools-list.response.json`) regenerated for the new schema fields on `genexus_list_objects`, `genexus_query`, and `genexus_logs`.

## v2.6.7 — 2026-05-22

A 22-point friction list against an `AcademicoHomolog1` working session on 2026-05-22 named the highest-impact loss-makers: builds queueing through 12 polls each (5–13 min real time), "Build Failed: 0 errors, 0 warnings" with no actionable signal, HTML-form gotchas surfacing only after the browser smoke-test, parallel `genexus_edit` silently shedding all but the first patch, WIN1252 charset losses, opaque "Visual write failed" errors, and a `genexus_preview` path that pinned Chrome and wedged the worker for the rest of the session. This release closes every implementable item end-to-end and ships unit tests for the parser / charset / concurrency helpers; the build-pipeline shortcut (`skipFullDeploy`) is gated behind an `EXPERIMENTAL` flag pending live validation against a runtime that picks up generated sources directly.

**Post-implementation review caught 8 correctness bugs (all fixed in this release):**
- Per-target serialization for `genexus_edit` lived in the JObject facade only; PatchService's writes went through a different overload and still raced. Lock moved into the canonical `WriteObject(target, partName, ...)` overload so every write path shares it; `NotePerTargetWrite` now fires on success regardless of entry point.
- `lifecycle build wait_until_done=true` returned `isError:false` for failed/cancelled builds (dead-code ternary `... ? false : false`). Fixed: terminal status is classified properly so the MCP envelope's `isError` matches build outcome.
- `genexus_worker_reload force=true` skipped `StartWorker` when `_activeConfig` was null but still claimed success in the response. Now refuses with `-32603` when no config is loaded.
- `PatchService.ApplyPatch`'s entry timestamp was backdated 50 ms, producing false-positive `Stale` verdicts when an unrelated write completed strictly before patch entry. Removed the backdate.
- `CollectNonWin1252Glyphs` walked the entire args tree and flagged lossy glyphs inside `patch.find` / `context` (glyphs the caller was REMOVING). Now skips read-only patch keys.
- Charset post-process clobbered any pre-existing `warnings` token that wasn't a JArray (silently dropping JObject-shaped warnings). Now wraps non-array shapes into an array.
- Contract fixture `tools-list.response.json` was stale (missing `wait_until_done`/`skipFullDeploy`, old `wait_seconds` description). Regenerated via the `GXMCP_UPDATE_GOLDEN` harness mode.
- `genexus_lifecycle.wait_seconds` description shifted from "0-25" (old reality) to "0-600" (new reality) but the description-budget test wasn't updated — was masked by the apphost.exe build lock until a clean rebuild surfaced it.

### CLI fixes

- **`genexus-mcp doctor` no longer false-flags `tool_definitions.json is missing` on installed copies.** `getToolDefinitionsPath()` was hardcoded to the dev-tree path `<repo>/src/GxMcp.Gateway/tool_definitions.json`, which doesn't exist in npm/install.ps1 installations — the file ships next to `publish/GxMcp.Gateway.exe`. Resolver now checks the gateway-exe sibling (matching how the gateway itself loads it), the dev-tree fallback, and a `GENEXUS_MCP_TOOL_DEFINITIONS` env override. Doctor's miss-message now names the expected path and the override env var. Regression tests added.

### Tool-definitions bloat sweep (-1150 tokens, -15%)

- Schema-budget test bumped from 7200 → 6500 (NOT upward, despite adding `wait_until_done` + `skipFullDeploy` + `edit_and_build.patch` + `worker_reload.force`). Prior versions raised the budget every friction sweep; this one trims aggressively.
- Boilerplate `kb` parameter description deduped across 32 tools (`"Target KB. Required when 2+ open."` → `"KB alias (multi-KB only)."`).
- Long prose moved out of schema descriptions (the `tools/list` payload sits in every LLM context) into `genexus://kb/tool-help/...` resources that callers fetch on demand. Affected: `genexus_edit.validate`, `genexus_apply_pattern.validate`, `genexus_history`, `genexus_lifecycle.target/compact/force/includeCallees`, `genexus_preview`, `genexus_create_object`, `genexus_create_popup`, `genexus_edit_and_build`.
- Versioned changelog references (`v2.6.6 (FR#28)`, `v2.6.6 Stream H (FR#25)`) removed from schema descriptions — that history belongs in the changelog, not in every tool listing.

### Lifecycle / build telemetry

- **`wait_seconds` cap raised 90 → 600.** `genexus_lifecycle status` + `build` long-poll can now block a full 10 minutes per turn. The 90 s ceiling was tuned for short compiles; a 12-minute popup build at 90 s burned ~8 turns each on noise.
- **`wait_until_done` on `lifecycle build`.** When true, the async dispatch path long-polls inline up to `wait_seconds` (default 600) and returns the terminal envelope directly instead of `{ job_id, running }`. Single turn versus 12.
- **`phase_failure` parsing for "0 errors / exit 1" builds.** When `ErrorCount == 0` but `ExitCode != 0`, `BuildService.ExtractPhaseFailure` scans the raw output for the last `>E0 <name>: <msg>` (or, as a fallback, the last `>RO <step>` marker) and surfaces a structured `phase_failure: { name, message }` block. `LifecycleResponseShaper` also passes it through the compact-mode envelope.
- **`partial_success` flag.** When the build is `Failed` but Generation + Compilation are both observed as succeeded in the raw output, `BuildService.DidGenerationAndCompilationSucceed` sets `PartialSuccess=true` and the shaper surfaces `partial_success: true` plus `effective_status: PartialSuccess`. WebAppConfig-style late failures no longer hide a successful DLL update.
- **`suggested_retry` for WebAppConfig fail.** When `phase_failure.name` contains "WebAppConfig", the retry hint now points the agent at "run the object once before rebuilding, or full IDE build to regenerate the config" rather than asking them to chase missing object names. Other late-phase failures get a generic hint that names the failing step.

### Edit pipeline

- **Per-target serialization for parallel edits.** `WriteService` acquires a per-target `lock` at the `WriteObject(target, args)` facade boundary so 5 parallel `genexus_edit` calls on the same target run sequentially instead of racing on the file hash. The `_lastWriteAtUtc` map is updated under the same lock so the patch path can cross-check.
- **`Stale` patch status vs. `NoMatch`.** `PatchService.ApplyPatch` captures the entry timestamp and, on `NoMatch`, calls `WriteService.WasTargetWrittenSince(target, entered)`. A sibling write that landed during the patch flips the failure to `Stale` with a "File modified during patch (concurrent edit landed)" message instead of the generic "Context not found".
- **`NoChange` disambiguation.** When the patch matched + applied but the part-normalizer canonicalised the change back to the original, the response now carries `noChangeReason: "serializer_normalized"` with a message pointing at XML attribute ordering, comment preservation, or trailing whitespace. The literal-identical case keeps the existing `"literal_identical"` reason. No more guessing whether the edit persisted.
- **WIN1252 charset warning.** `WriteService.CollectNonWin1252Glyphs` walks the patch payload and surfaces a `kb_charset_lossy` warning when any character can't round-trip through codepage 1252. Glyphs like ✓ ⧖ Σ ◷ that the SDK accepts but render as `?` at runtime are flagged before the build.
- **`Visual write failed` exception chain.** `WriteService.FormatExceptionChain` walks `InnerException` so the root SDK diagnostic (e.g. "variable not declared") makes it into `details` instead of being swallowed by a generic wrapper.
- **`genexus_edit_and_build` accepts `patch:{find,replace}`.** Schema relaxed: `content` is no longer required; the orchestrator auto-normalises `patch` to `mode=patch` + `content` shape, matching `genexus_edit`. Wrong-type `content: {...}` for `mode=full` returns a typed error with a workaround hint instead of the opaque "name is required".
- **Layout gotcha preview at write time.** `WriteVisualPart` now runs `LayoutGotchaScanner.Scan` against the normalized prospective XML and attaches `layoutGotchas` to both the `DryRun` and `NoChange` responses, so the four HTML-form limitations (gxButton custom-event silent-drop, gxAttribute discrete control read-only, missing AttID/DataField, unknown ControlType) surface in the same turn as the edit instead of after a build.

### Worker / preview reliability

- **`genexus_preview` no-deadlock spawn.** `CliRunner.Run` switched from serial `ReadToEnd()` calls to async event-driven stream readers, eliminating the stdout/stderr pipe-buffer deadlock that wedged the worker behind a stuck chrome-devtools-mcp child. Timeout path now does `taskkill /PID … /T /F` (recursive tree kill) so the Node shim's Chrome subprocess gets reaped along with the shim.
- **`genexus_worker_reload force=true`.** Gateway-side intercept that bypasses the JSON-RPC pipe entirely — `WorkerPool.StopAll()` + cache clear + `StartWorker()` happen in-process. Required for the wedged-worker case where the soft drain path can't get an ACK because the worker is hung.
- **`lastError` in `whoami.metricsSummary`.** `OperationTracker.BuildMetricsSummary` now scans `_operations` for the most-recent record with a `LastError` and surfaces `{ tool, message, atUtc, operationId }`. Counters alone don't tell the agent _what_ failed; this does, on the first turn after the failure.

### Playbooks (whoami)

- `unbreak_html_form` — the 4 HTML-form limitations + workarounds (custom events silently routed to Enter; gxTextBlock CaptionExpression Type=Variable literal-renders; gxAttribute against uncommitted variables; controls not addressable from Events).
- `bulk_edit` — promote `genexus_bulk_edit` over N parallel `genexus_edit` for same-object patches.
- `wait_long_builds` — point at `wait_until_done` + 600 s `wait_seconds` cap.
- `xml_comments_in_form` — XML comments inside HTML form Source emit as visible text (strip before edit, or use `mode=patch`).
- `partial_success` — when build status=Failed but `partial_success=true`, try running the object before rebuilding.

### Build pipeline (experimental)

- **`skipFullDeploy=true` on single-target Build.** When the build action is `Build` + exactly one target + `includeCallees=none`, this stops the in-process runner after `SpecifyOneOnly` and skips `IdeWebBuildAndDeploy`. Skips Build All / WebAppConfig / module copies — turning a 5–13 min single-object build into ~30 s. **EXPERIMENTAL**: the DLL output is not redeployed; validate live against your runtime before adopting.

### Tests

- `PhaseFailureExtractionTests`: E0 marker wins, RO fallback, both-locales `DidGenerationAndCompilationSucceed`.
- `Win1252CharsetWarnTests`: ASCII + Latin accents pass through; ✓ ⧖ get flagged + deduped.
- `ConcurrentWriteTrackerTests`: per-target lock identity, `WasTargetWrittenSince` semantics.
- `ExceptionChainFormatterTests`: null-safe; walks inner; dedupes repeated messages.

All 558 worker tests pass; all 307 gateway tests pass.

## v2.6.6 — 2026-05-21

A 28-point friction sweep against `AcademicoHomolog1` on 2026-05-21 surfaced the gap between MCP-driven and IDE-driven editing: builds spawned a fresh `MSBuild.exe` per invocation (cold AppDomain, 20-40s overhead before any spec work), `genexus_lifecycle action=status` was a polling treadmill with no `wait`/`since` semantics so agents burned tokens on busy-waits, patches occasionally fell through to a NoMatch and returned `success` while leaving the part unchanged, `return_post_state` echoed the request payload rather than re-reading the persisted bytes, two workers racing against the same KB silently corrupted the snapshot index, the headless preview path had no `genexus_preview action=run` equivalent of the IDE's F5 launcher, popup-vs-standalone classification of generated WebPanels was guesswork, and CS2001 compile errors from orphan `<obj>_bc.cs` files masked real issues during family-generation cleanup. This release closes all 28 points end-to-end, with a build daemon that loads `Genexus.MsBuild.Tasks` once and reuses the open KB handle, event-driven status long-poll, a pre-write snapshot store on every edit, and an IDE-parity Discard-changes path via the new history snapshot ring.

**Live-validation pass (post-fan-out, same day):** end-to-end break-flow against a real KB uncovered four integration bugs the unit suite did not catch — `genexus_edit validate=only` was silently stripped by `ObjectRouter` before reaching the worker AND ignored by the `patch.Apply` dispatcher; `genexus_history discard=true` was intercepted by a legacy duplicate handler in `SystemRouter` that dropped the v2.6.6 fields; and the in-process build daemon (Stream D) crashed inside `Artech.MsBuild.Common.ArtechTask`'s static constructor with `GxException: O Service Manager já foi ativado` because `KbService.OpenKB` activated the process-singleton `GxServiceManager` before the cctor ran. All four are fixed in this same release — the gateway now forwards `validate` from `genexus_edit` end-to-end, `patch.Apply` maps `validate=only` to `dryRun=true`, the legacy `SystemRouter` handler for `genexus_history` is gone, and the worker warms `ArtechTask`'s static ctor BEFORE `InitializeSdk` so the IDE's activation order is mirrored. A `McpRouter.AssertNoDuplicateRouterCoverage` startup guard now fails loudly when any two routers claim the same tool, preventing the duplicate-handler bug class from recurring.

### Performance

- **In-process build daemon.** `genexus_lifecycle action=build` no longer spawns `MSBuild.exe` for `Build` / `RebuildAll` targets — `InProcessBuildRunner` invokes `Genexus.MsBuild.Tasks.SpecifyOneOnly` + `IdeWebBuildAndDeploy` directly against the live `KbService._kb`, routed through an `InProcessBuildEngine` adapter that exposes the worker's logger as the MSBuild `IBuildEngine`. Cold-start cost on a 38k-object KB drops from MSBuild.exe's ~30s AppDomain boot to a single reflective `Assembly.Load` of `Genexus.MsBuild.Tasks.dll` (one-shot per worker process). Non-`Build` / non-`RebuildAll` actions (Sync, Clean, Specify, Generate-only) still fall back to `MSBuild.exe` — set `GXMCP_INPROCESS_BUILD=0` to force the legacy spawn path for the supported actions too.
- **Event-driven status long-poll.** `genexus_lifecycle action=status` now accepts `wait=<0-300>` (seconds) plus `since=<baseline>`; the worker blocks on a per-`BuildTaskStatus` `ManualResetEventSlim` and returns the moment the task transitions out of the baseline state (or `wait` elapses). Agents that previously polled every 1-2s on a long build now make one call per state transition.
- **`KbHandle.ActiveEnvironment` TTL cache.** The active environment lookup hit the SDK on every `whoami` / `lifecycle` call. Now cached for 60s per KB, invalidated explicitly by `KbWatcherService.OnEnvironmentChanged`. Repeat reads return in microseconds.

### Build infra

- **IDE-parity action routing.** `BuildService` distinguishes `Build` / `RebuildAll` (in-process daemon) from `Sync` / `Clean` / `Specify` (still routed through MSBuild.exe with the IDE's task template — `<SpecifyOneOnly>` / `<SpecifyAll>` + `<GenerateOnly>`, never the deprecated `<BuildOne>`). Action mismatch on the wire surfaces as a clear `validActions` envelope instead of a stuck spec phase.
- **Business-component variant auto-chain.** `ExpandTargets` now resolves each `Transaction` target's BC variant (`<name>_bc`) automatically when present in the index, so building a transaction also re-specs its BC the way the IDE's Build action does. No more "looks built but BC is stale" half-builds.
- **Orphan-file demotion.** CS2001 compile errors for `<obj>_bc.cs` files that no longer have a parent object are now classified `IsBcOrphanError` and demoted to warnings — they don't fail the build, but they're still surfaced under `ErrorsDetailed` so the cleanup is visible. Full orphan-sweep helper deferred (see below).
- **MSBuild line → GeneXus-object mapping.** `BuildOutputShaper` rewrites `GxBuild_*.msbuild(N,M):` error locations to the underlying GeneXus object name + part, so error envelopes carry the object the agent actually edited rather than the auto-generated msbuild row. Raw form is preserved under `ErrorsDetailed[i].raw`; rewritten form under `.location`.

### Edit safety

- **Patch safety guard.** Pattern-style patches that fell through to a NoMatch were silently reported as `status: ok` because the abort-on-first-failure check ran before the post-write verify. The guard now requires (a) a non-empty match list and (b) a post-write byte hash that differs from the pre-write hash, otherwise the response is `status: NoMatch` with the original part text unchanged. Reproduces the v2.6.5 Events-part CRLF mismatch from the friction report.
- **Pre-write snapshot store under `.gx/snapshots/`.** Every edit now writes the prior bytes of the affected part to `.gx/snapshots/<obj>/<part>/<UTC-iso>.bin` before the SDK save, capped at the last 20 snapshots per part. `EditSnapshotStore` is the same store `genexus_history action=restore discard=true` reads from to provide IDE-parity Discard.
- **`validate=strict|best-effort|only` modes.** `strict` (default) — refuses to save if any structural validation fails. `best-effort` — saves and surfaces validation warnings in the envelope. `only` — runs validation against the candidate content without persisting (the legacy `dryRun: true` semantics, kept for back-compat).
- **`return_post_state` re-reads persisted bytes.** Previously echoed the request payload, which masked SDK normalizations (whitespace, attribute reordering, CRLF→LF on certain parts). Now reads the part back from the SDK after save and returns the canonical persisted bytes — the regression mode in the v2.6.5 friction report.

### Worker lifecycle

- **`SingleInstanceLock` per KB + worker exe.** A `Global\GxMcpWorker_<sha256>` mutex plus a `DeleteOnClose` PID file under TempPath blocks two workers from opening the same KB. Stale lock files (PID gone but file present) are cleaned automatically on the next acquire attempt; live conflicts surface `ExistingPid` so the new doctor check can list it.
- **Soft hot-reload with persisted job registry.** `genexus_worker_reload mode=soft|hard` cycles the worker process: `soft` waits for in-flight jobs, `hard` cancels them. `BackgroundJobRegistry` persists running / completed jobs to disk across the restart, so `genexus_lifecycle action=status target=op:<id>` and `action=result` keep working through the cycle.

### Preview / Run

- **`GxFormDriver` — parse + fill + click for GeneXus-generated forms.** `PreviewService` now drives generated `<form gx-form>` markup directly: parses the input/select/button tree, fills inputs by gx-name, clicks buttons by id/caption. Replaces the brittle generic-CSS-selector scripts the headless bridge previously synthesized.
- **GAM session injection.** When `GXMCP_GAM_USER` / `GXMCP_GAM_PWD` / `GXMCP_GAM_REPOSITORY` are set, `PreviewService` walks the bridge through `gxgamsignin.aspx` before requesting the target URL — preview of GAM-gated panels no longer dead-ends at the login screen.
- **`analyze mode=parent_context`.** Returns `{ openedAs: "popup"|"standalone", hint }` for a WebPanel based on referrer + the `popupHint` baked into the generated HTML. `genexus_create_popup` inlines the same hint so the IDE and the MCP agree on the classification on the very first call.
- **`genexus_preview action=run` (F5 launcher).** Resolves the KB's launcher object via `KbService.GetLauncherObjectName` (`StartupObject` env property → `DefaultObject` fallback) and opens it in the headless bridge — the MCP equivalent of pressing F5 in the IDE.

### Diagnostics

- **Logger ISO-8601 timestamps + `[phase]` tag.** Worker log lines now lead with `2026-05-21T13:42:11.034Z [build]` (UTC, millisecond precision) instead of locale-dependent timestamps. Phase is stamped per logical operation (`build`, `kb-open`, `edit`, `preview`) so a 38k-object build is greppable as a single trace.
- **`BuildOutputShaper` head/tail/full-log envelope.** Build responses carry `Output.head` (first 50 lines), `Output.tail` (last 50), and `Output.full` (gzip-base64 envelope), so an agent diagnosing a 2k-line MSBuild log doesn't have to round-trip the whole thing — head/tail is enough for 90% of failure modes.
- **Warning aggregation under `compact=true`.** Repeated warnings (e.g. SPC0084 across 40 For Each blocks) collapse to `{ code, count, first }` entries so the envelope stays small. Set `compact=false` (or omit) to keep the full list.
- **Doctor checks for the new infra.** `genexus-mcp doctor` now reports `worker_single_instance_lock` (lists live workers + flags stale .lock files in TempPath) and `in_process_build_assembly_load` (confirms `Genexus.MsBuild.Tasks.dll` is reachable under `GX_PROGRAM_DIR` / the configured GeneXus path; warns when the build will fall back to the MSBuild.exe slow path).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`.gxw` version metadata now matches the format the GeneXus IDE writes.** `KbService.DetectGeneXusVersion` was reading `FileVersionInfo.ProductVersion` from `GeneXus.exe`, which on modern .NET includes the `InformationalVersion` suffix (`18.0.14.187794+<git-sha>`). When the IDE later reopened the KB it re-detected its own canonical string (`18.0.187794 U14`) and showed the "different GeneXus installation than last time" dialog every time, even though the install path was identical. The version is now built from the numeric `FileVersionInfo` parts as `{Major}.{Minor}.{Private} U{Build}`, matching the IDE byte-for-byte. The string-based `ProductVersion`/`FileVersion` path is kept as a fallback for installs where the numeric parts come back zeroed.
- **`genexus_history action=restore discard=true target=<obj>`.** IDE-parity Discard — restores the part bytes from the most recent `EditSnapshotStore` entry, no commit / rollback / VCS round-trip required. Surfaces `restoredFrom` (timestamp + snapshot path) in the envelope so the operation is auditable.
- **Installer no longer silently writes a broken config when `--gx` points at a path without `genexus.exe`.** A field install hit this when GeneXus was at `C:\Program Files (x86)\GeneXus\GeneXus18u7` (the update-pack folder) instead of the canonical `GeneXus18`: `genexus-mcp init --gx "...\GeneXus18"` wrote the config with the wrong path, the doctor only emitted a `warn`, and the worker crashed on first MCP call with the opaque `Worker for KB '<name>' crashed/exited.` envelope. Fix is four-part: (1) `handleInit` validates `--gx` / `--kb` before touching disk and, when the supplied `--gx` is missing, runs `discoverGeneXusInstallation()` to suggest the real path in the error help (catches the `GeneXus18u7` sibling automatically); (2) `handleDoctor` promotes `gx_installation` and `kb_path_exists` from `warn` to `fail` when a path is configured but absent — silently warning about something that guarantees a worker crash was the root cause; (3) `runPostInitVerification` exports `GX_CONFIG_PATH` before invoking doctor so it actually finds the freshly-written config instead of looking at `C:\windows\system32` (the CWD when operators run `npx genexus-mcp init` from a fresh shell); (4) `probeWorkerStartup` spawns the gateway with the resolved config for ~2.5s and detects an early crash with exit code and captured stderr — so init reports the worker failure inline rather than deferring it to the first MCP call. Init now returns a non-zero exit when any check fails, so `scripts/install.ps1` / CI / AI clients see the problem at install time.

### Installer & CLI quality of life

A field install against `NovaKbAcademico` on 2026-05-21 (operator working from `C:\windows\system32`, GeneXus at `C:\Program Files (x86)\GeneXus\GeneXus18u7`) failed silently: `init` accepted the wrong `-Gx`, wrote a broken config, doctor emitted warnings, the install reported `OK`, then the worker crashed on the first MCP call with the opaque envelope `Worker for KB '<name>' crashed/exited.`. The fixes above (path validation, fail-promoted doctor checks, worker startup smoke probe, non-zero exit on verification fail, post-init doctor pointed at the right config) close that path; the items below address the surrounding installer experience so this class of failure surfaces earlier and is recoverable without manual cleanup.

- **`install.ps1` now scans for GeneXus installs before invoking `npx`.** `Find-GeneXusInstallations` enumerates every `<root>\GeneXus\GeneXus*` folder that contains `genexus.exe`, reads the file's `ProductVersion`, and offers an interactive pick when multiple are found. If `-Gx` is supplied but doesn't contain `genexus.exe`, the script falls back to the scanned list instead of forwarding the bad path to `init`. Catches the `GeneXus18u7`-vs-`GeneXus18` mismatch at the script level, before any config is written.
- **Hard prompt when not running as Administrator.** The per-user default (`%LOCALAPPDATA%\Programs\GenexusMCP`) is exactly where AppLocker default rules deny execution. The installer now prints a multi-line warning explaining the consequence (`"Failed to connect" / "Access denied"` from the AI client) and asks for confirmation before proceeding; `-Force` bypasses the prompt. Previously this was a one-line `Write-Warn` buried between download log lines.
- **Download retry with exponential backoff + system-proxy detection.** Both the GitHub API release lookup and the `publish.zip` download go through `Invoke-WithRetry` (default 3 attempts, 2/4/8 s wait). When `$env:HTTPS_PROXY` is unset but `System.Net.WebRequest.GetSystemWebProxy()` returns a non-direct proxy, the installer warns the operator that PowerShell may not honor the system proxy and tells them which env var to export. Configurable via `-DownloadRetries`.
- **`-Repair` and `-Uninstall` flags.** `-Repair` wipes and reinstalls the currently-installed version (or `-Version` if passed) without changing the install dir — the recovery path for a corrupt extract or a half-applied upgrade. `-Uninstall` removes `mcpServers.genexus` from every detected AI client config, then deletes the install dir (with a confirmation prompt unless `-Force` is also passed). Replaces the manual `rm -rf C:\Tools\GenexusMCP` + per-client editing that operators used to do by hand.
- **`npx` invocation pinned to the install's version.** Was `genexus-mcp@latest`; is now `genexus-mcp@<sameVersion>`. The two channels (GitHub Releases and npm) can drift by hours after a publish — re-using the just-extracted exe with an older or newer CLI sometimes produced flag-mismatch errors that operators couldn't diagnose without reading both changelogs. Same-version pinning kills the drift class entirely.
- **`init --format json` parsed by the installer; only the relevant fields surface.** Previously the operator saw a wall of YAML from the CLI default `--format toon` (`[2]:`, `meta:`, `verification:`) and couldn't tell pass from fail. The PowerShell wrapper now `ConvertFrom-Json`s the output and prints either a one-line success summary (config path + patched client ids) or, on failure, just `error.message`, the `help[]` lines, and any check whose `status == "fail"`. The full envelope is still written to stdout when JSON parse fails, so nothing is hidden.
- **Post-install AI client restart prompt.** Gets the live PIDs of Claude Desktop / Cursor / Antigravity / VS Code via `Get-Process`, lists them with their main-module path, and offers to stop + relaunch them in one go. Skips the prompt with `-NoRestartPrompt` for unattended installs. Most operators didn't know mcp config is read once at client startup, so they'd patch the config and then wonder why "nothing works."
- **Gateway `--self-test` flag replaces the no-op `--axi-spawn-probe`.** The old probe only verified the exe could launch. `--self-test` loads `Configuration` from `GX_CONFIG_PATH`, validates `genexus.exe` exists at the configured path, checks for `Genexus.MsBuild.Tasks.dll` (in-process build daemon), validates the KB folder shape, and emits a single JSON line (`schemaVersion: gateway-selftest/1`) to stdout before exiting with code `0` (all pass) or `1` (any fail). Gives the installer + `genexus-mcp doctor` something authoritative to call instead of duplicating the checks in both languages.
- **`genexus-mcp init` auto-discovers KBs when `--kb` is missing.** New `discoverKnowledgeBases(cwd)` walks the cwd ancestry, then scans `C:\KBs`, `D:\KBs`, `C:\GeneXus`, `%USERPROFILE%\Documents\GeneXus`, `%USERPROFILE%\source\repos` (depth 2). One hit → used silently; multiple → listed in the usage error with copy-pastable `--kb "..."` lines so the operator can pick. Removes the "run init from a KB folder OR pass --kb" Catch-22 for operators running `npx genexus-mcp init` from an open PowerShell that doesn't happen to be in a KB.
- **`genexus-mcp doctor --dump` builds a support bundle.** Emits `<TEMP>\genexus-mcp-dump-<UTC>.zip` containing `doctor.json`, `config.redacted.json` (all string values that look like filesystem paths are replaced with `<redacted:hash8>` so the structure survives but usernames / KB names don't), `environment.json` (node version, OS release, env-flag presence booleans, GeneXus version), and the last 64 KB of up to 5 worker logs from `%LOCALAPPDATA%\GenexusMCP\logs`. Path hash is stable across the bundle so a support engineer can still correlate which redacted KB matches which log line. Replaces the "paste me 5 separate outputs" routine.

### Deferred to follow-up

- **Orphan-sweep helper.** The CS2001 demotion lands in v2.6.6, but the active sweep (delete the orphan `<obj>_bc.cs` files from disk + project file) is flag-gated and not enabled by default. Tracked for v2.6.7 once the rollback path is exercised against a live multi-object KB.
- **`KbHandle` env-fetcher gateway wiring.** The 60s `ActiveEnvironment` cache lives in the worker; the gateway-side fetcher that would surface it via `whoami.environment` still hits the SDK directly. Wiring is straightforward (`KbHandle.GetEnvironmentAsync` → `KbService.ActiveEnvironment`) but not in scope for this release.

### Internal

- **Test counts.** Worker 536 passed / 0 failed / 4 skipped (was ~485). Gateway 307 passed / 0 failed / 7 skipped (was ~280). Net ~+50 over the v2.6.5 baseline.
- **Edge-case sweep file.** `src/GxMcp.Worker.Tests/EdgeCaseRegressionTests.cs` covers cross-stream interaction surfaces (concurrent edit + build, soft-reload during long-poll status, snapshot store under disk-full, preview F5 with no launcher resolved). One test file rather than scattering edge-cases across stream-specific files so the regression contract is greppable in one place.
- **New regression coverage** — one file per stream plus the cross-cutting sweep:
  - `PatchSafetyGuardTests`, `SemanticOpsValidateModeTests`, `EditSnapshotStoreTests`, `PostStatePersistedTests` (Stream A)
  - `SingleInstanceLockTests`, `BackgroundJobRegistryPersistenceTests` (Stream B)
  - `LoggerPhaseTagTests`, `BuildOutputShaperTests`, `WarningAggregationTests`, `GxObjectMappingTests` (Stream C)
  - `InProcessBuildRunnerTests` (Stream D)
  - `KbWatcherInvalidationTests`, `EnvCacheTtlTests`, `HistoryDiscardTests`, `LauncherResolutionTests` (Stream H)
  - `StatusWaitTests`, `GatewayLifecycleWaitProxyTests` (Stream F)
  - `GxFormDriverTests`, `GamSessionInjectionTests`, `ParentContextAnalyzeTests` (Stream G)
- **`ToolSchemaSizeTests` budget 6700 → 7200** to fit the new `validate` enum on `genexus_edit`, `wait`/`since` on `genexus_lifecycle action=status`, `genexus_preview action=run`, and `genexus_history discard=true`/`snapshot`/`part` schema. Net ~+315 tokens.
- **Contract-discovery goldens refreshed** under `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`.

## v2.6.5 — 2026-05-21

Two real-session bug hunts. First: `genexus_lifecycle action=build` failed on a 38k-object KB with an opaque `O sistema não pode encontrar o arquivo especificado` at `GxBuild_*.msbuild(5,5)` — same line every time, no further detail even with `/v:diag` or fusion log. Root cause: the worker emitted `<BuildOne>`, a monolithic GeneXus task that bundles spec + gen + IIS deploy and explodes on the deploy step when run from a standalone `MSBuild.exe` (the AppDomain doesn't have the SDK's Artech.* + IIS COM probing the IDE relies on). The IDE itself does NOT use `<BuildOne>` — `C:\Program Files (x86)\GeneXus\GeneXus18\Genexus.msbuild` composes `<SpecifyOneOnly>` + `<GenerateOnly>` instead. Worker now mirrors the IDE template. Validated end-to-end via MCP on `AcademicoHomolog1`: `target=RegProfAlunoUGPopup` finished 0 errors / 0 warnings in 59s and the regenerated `regprofalunougpopup.cs` carries the new eligibility-gate locals exactly as edited.

Second: `genexus_preview` failed with `O executável especificado não é um aplicativo válido para esta plataforma de SO` (`ERROR_BAD_EXE_FORMAT`) before chrome-devtools-axi was ever invoked. The npm shim installs as `chrome-devtools-axi.cmd` / `.ps1` / extensionless (Windows resolves the bash shim first via PATHEXT), and `Process.Start` with `UseShellExecute=false` only accepts true PE images. Plus once the CLI did launch, the cold-start of `chrome-devtools-axi`'s internal bridge (which `npx`-bootstraps `chrome-devtools-mcp@latest`) routinely hit the 30s per-call timeout. The headless preview path now works fully unattended on a stock Windows box.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_lifecycle action=build` rewritten to use the IDE's task pattern.** `BuildService.cs` no longer emits `<BuildOne ObjectName="…" ForceRebuild="true" />` — that task includes an IIS configuration-update sub-step that fails opaquely outside the GeneXus IDE process. Worker now emits `<SpecifyOneOnly ObjectNames="A;B;C" /><GenerateOnly />` for `action=Build` (with targets) and `<SpecifyAll /><GenerateOnly />` for `action=Sync`. `<OpenKnowledgeBase>` is also opened with `Output="IDE"` to match the IDE's load flags. Net effect: build runs 0 errors against a 38k-object KB where the old path produced 6 errors / 10 warnings every time.
- **`PreviewService` CLI launch handles `.cmd` / `.bat` / `.ps1` / extensionless shims.** `DefaultCliRunner.Run` previously called `Process.Start(filename, args)` with `UseShellExecute=false`, which CreateProcess refuses for anything that is not a native PE image. The runner now classifies the resolved path and routes non-`.exe` / non-`.com` candidates through `cmd.exe /c "<file>" <args>` (the same pattern `Which()` already used). Eliminates the `ERROR_BAD_EXE_FORMAT` failure mode that swallowed the real CLI command before it ran.
- **`PreviewService` auto-discovers a globally installed `chrome-devtools-mcp` and injects `CHROME_DEVTOOLS_AXI_MCP_PATH`.** Without that env var the axi bridge `npx`-bootstraps `chrome-devtools-mcp@latest` on first launch (~25-30s on Windows), which routinely tripped the per-command timeout. Worker now caches the resolution of `npm prefix -g` once per process, then sets `CHROME_DEVTOOLS_AXI_MCP_PATH` on every spawned `ProcessStartInfo` when the local file exists. Setup is one-shot: `npm install -g chrome-devtools-mcp` and the headless preview path stays warm afterwards.
- **`PreviewService` per-command timeout 30s → 90s.** The first `chrome-devtools-axi open` call into a cold bridge can legitimately take 25-60s on Windows even with the `MCP_PATH` shortcut. 30s left no headroom; 90s comfortably covers warm-up plus a handful of snapshot/eval calls. Subsequent warm calls return in well under a second.

### Observability (previously Unreleased)

- **`genexus_whoami` flush-failure telemetry (W-M2).** `IndexCacheService` now tracks consecutive snapshot-flush failures, last success timestamp, and last error message; `kb.GetIndexState` surfaces them and the gateway emits a `flushHealth` block under the index section. A silently failing on-disk index snapshot (disk full / locked / permission) is visible from whoami without grepping logs.
- **`genexus_whoami` tool-metrics summary.** `OperationTracker.BuildMetricsSummary()` rolls up total calls / errors / timeouts across tools plus the slowest tool by p95, surfaced as `metricsSummary`. Keeps the first-turn whoami response tiny; full per-tool breakdown stays at `genexus_lifecycle status target=gateway:metrics`.
- **`BoundedStringCache` hit/miss/eviction counters.** SearchService's query cache now exposes Hits / Misses / Evictions / Count / Capacity (Interlocked, no contention with the per-call lock) so a degraded hit ratio from undersized capacity is visible without an external profiler.
- **Slow-log instrumentation on `WriteObject` (>250ms).** Unusually-slow SDK save paths now surface in `worker_debug.log` as `[OBJ-SAVE-SLOW]` lines with target / part / codeLen / dryRun. Complements existing `[KB-OPEN]` and `[SEARCH-SLOW]` markers.

## v2.6.4 — 2026-05-20

Three passes: a usability sweep against KB `AcademicoHomolog1` that caught nine concrete friction points the LLM was hitting on first use; a UX pass focused on the "agent burns 3-8k tokens exploring before doing real work" failure mode on apply_pattern; and a corporate-Windows install hardening pass triggered by a real `2.3.4 -> 2.6.3` upgrade report where the user's MCP config kept silently pointing at an old gateway exe outside `node_modules`, the npm-installed copy was blocked by domain AppLocker from `%APPDATA%`, and every diagnostic surface ("Failed to connect", `npm update` ghost operation, generic launcher errors) compounded the dead end. Validated with happy-path apply on a disposable Transaction + WebPanel (11/11 assertions), focused UX probe (20/20), and the full CLI test suite (37/38, single pre-existing assertion unrelated).

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`analyze mode=explain` was a stub returning hardcoded `"Code analysis simulation"`** regardless of input — agents treated the fake response as real. Mode removed from the public schema (`tool_definitions.json`); legacy callers receive an explicit `NotImplemented` envelope pointing to valid modes.
- **`genexus_query` ranking pulled `Index` objects with no name/path match into the top-20** via vector similarity. Fast literal query for "Country" returned 15 unrelated `IBls*` indexes. Index/Folder/Module are now filtered out of default results unless explicitly requested via `typeFilter`. New `_meta.match_quality` field (exact|prefix|substring|vector|none) lets the caller branch reliably; `suggested_next` is only emitted for `exact`/`prefix` to stop misdirecting agents.
- **`genexus_read` error envelope for invalid parts didn't list valid parts.** Agents were guessing part names through trial-and-error. Now includes `availableParts` (same list `genexus_inspect include=['parts']` returns) plus a `hint` line: `Valid parts for Procedure: Documentation, Help, Layout, Source, Variables.`
- **`analyze pattern_metadata` took 12.3s to error on non-WWP-eligible objects** because `ResolveWWPInstance` walked `model.Objects.GetAll()` on the full KB before falling through. Upfront type guard now rejects in ~30ms (~430× faster) when the parent isn't `WorkWithPlus` / `Transaction` / `WebPanel`.
- **`analyze mode=navigation` returned `{levels:[], warnings:[]}` silently** when an object had no `For Each` blocks — indistinguishable from analysis failure. Empty envelopes now carry `status: "NoNavigationBlocks"` + `hint` pointing to alternative modes.
- **`genexus_whoami` cold-path latency was 1.7s** because `BuildWhoamiPayloadAsync` always blocked on a worker round-trip for fresh index state. Now skips the round-trip when the cached snapshot is < 15s old, and tightens the remaining timeout from 1500ms → 400ms. 1676ms → 7ms baseline (≈240× faster).
- **`genexus_properties action=get` cold-call on a Domain was 3s** due to lazy SDK property-definition reflection. Added a per-GUID TTL cache (30s, invalidated on `set`) so repeat reads are sub-millisecond. The first hit still pays the SDK warm-up but subsequent agent introspection on the same object is free.
- **`genexus_query` `_meta.partial` flag was inconsistent.** Direct-lookup hits during cold-start didn't surface partial-state info, so agents didn't know to re-query once indexing finished. `match_quality` + `partial` now appear uniformly across direct-lookup and index-search paths.
- **Inner-payload errors didn't set `result.isError: true`.** `genexus_read` returning `{error: "Part 'X' not found..."}` was sent with `isError=false`, breaking MCP clients that branch on the flag. The gateway now mirrors inner-payload error/status (Error/NotFound/NotImplemented) into the outer envelope. Affects `read`, `analyze`, and any tool that returns errors via the result-body shape.

### Added

- **`apply_pattern` parent-type gate.** Reported by user: applying WorkWithPlus to a WebPanel created the host but bound it as a Transaction, producing IDE compile errors. The fix is two-fold:
  1. **Upfront type-routing.** Object type is checked before any SDK churn. `Transaction` → family-generation path (no template required). `WebPanel`/`SDPanel` → direct-attach path (template required or auto-discovered). Anything else (`Procedure`, `SDT`, `Domain`, …) is rejected in <500ms with `validParentTypes` + a routing hint, instead of churning through a no-op and returning a misleading "WorkWithPlus instance not found".
  2. **`settings.template` validated against the live catalog.** Bad template returns `availableTemplates` synchronously. Previously the validation walked `model.Objects.GetAll()` on every call (~10s on a 50k-object KB); now consults the search index with a 60s TTL cache, so the check is subsecond after the first hit.
  3. **Response envelope surfaces `parentType` + `bindingMode`** (`transaction-family` | `webpanel-direct-attach` | `sdpanel-direct-attach`) so the agent can verify which lifecycle ran without inspecting the IDE.

- **`genexus_whoami` returns inline playbooks.** First-turn whoami response now carries a `playbooks` block routing the LLM to the right tool for the most common flows (WWP on transaction, WWP on webpanel, edit pattern instance, create popup, read object structure). Eliminates the "agent explores for 3-8k tokens before acting" pattern observed in real sessions. The playbooks are 1-line redirects; full step-by-step recipes are in the new `genexus_recipe` tool.

- **`genexus_recipe { name }` — new gateway-served tool for named playbooks.** Returns `{goal, prereq, steps, pitfalls}` for `wwp_on_transaction`, `wwp_on_webpanel`, `create_popup`, `edit_pattern_instance`, `add_custom_button`. `name='list'` enumerates available recipes. Catalog lives in `RecipeCatalog.cs` for easy extension. Tool descriptions across `genexus_apply_pattern` / `genexus_create_object` / `genexus_edit` now point at the relevant recipes so the LLM discovers the routing layer from `tools/list` without exploration.

### Performance

- **`SdkSurfaceProbe.Run` was firing on EVERY `apply_pattern`** — full reflection sweep over loaded SDK assemblies plus a multi-MB `raw.json` write to disk. 5-15s of pure debugging-tool overhead in production calls. Gated behind `GX_MCP_SDK_PROBE=1`; the same diagnostic is available on demand via the `genexus_sdk_probe` tool when investigating SDK surface. NoOp diagnostic path (the `sdkProbePath` / `sdkSurfaceProbe` envelope fields) gated identically.
- **Per-phase Stopwatch instrumentation logged as `[ApplyPattern-PERF]`** captures `sdkProbeGated / engineApply / lookupFamily / indexUpdate / tailEnvelope` per call. Remaining time on a real apply (~50s on Transaction, ~30s on WebPanel) is the SDK doing actual generation/persist work and cannot be shortcut without losing the WWP lifecycle guarantees.
- **`ListWwpWebTemplates()` walks the search index instead of `model.Objects.GetAll()`** with a 60s TTL cache keyed by KB location. 10s → ~5ms on cached hits.

### Internal

- Probes used for the audit live under `scratch/`: `usability_probe.js` + `usability_probe2.js` (initial audit), `validation_probe.js` (25/25 assertions on the 9 fixes), `ux_probe.js` (20/20 on the new UX features), `apply_happy_path.js` (11/11 on real apply with disposable objects).
- `RecipeCatalog.cs` is `internal static` with a `Dictionary<string, Func<JObject>>` registry — adding a recipe is one entry. Routes through the same `gateway-served meta-tool` path as `genexus_whoami` (no worker involvement, no JSON-RPC round-trip).

### Added (follow-up)

- **`apply_pattern { validate: true }` — post-apply build of the generated host in a single tool call.** The original "vinculou como se fosse transação" bug surfaced as the LLM declaring success on a broken WWP binding that only failed when the user opened the IDE. With `validate: true` the gateway fires `Build/Build` against the host returned by apply, polls `Build/Status` with the worker's taskId until terminal, and folds a `validation` block into the apply response: `{ status: ok|failed|timeout, errorCount, warningCount, errors[], warnings[], durationMs, taskId }`. Failed builds promote `result.isError=true` so MCP clients that branch on the flag get a clear pass/fail signal. Wall time adds 60-180s but the LLM never has to open the IDE to discover a compile failure. Validated live: 11/11 assertions including the bug-mode where the worker's `BuildService.Build` returns a `Running` envelope in milliseconds — earlier draft parsed that as `status: "ok"` (26ms / 0 errors), now correctly polls until the real terminal state (55s / 6 errors / errors[] populated).

- **`genexus_lifecycle action=result target=op:<jobId>` works for completed background jobs.** v2.6.3 fixed `cancel`/`status` for `op:<id>` via JobRegistry but `result` still forwarded to the worker's taskId tracker, which returned `"Task ID not found"` for jobs visible in `_meta.background_jobs`. Symmetric handler now consults JobRegistry first: running → `Pending` envelope with poll hint; completed (`succeeded`/`failed`/`cancelled`) → stored `JobEntry.Result` plus status/operationId/kind/summary/startedAt/completedAt. Failed/cancelled propagate `isError=true`.

### Internal (follow-up)

- **Helper extraction for unit testability.** Two inline payload builders refactored into pure static methods so they can be covered without spinning up the gateway:
  - `McpRouter.BuildJobResultEnvelope(JobEntry job)` → `(envelope, isError)` — the lifecycle result shape, called from the `op:<id>` route.
  - `PatternApplyService.TryBuildTypeGateRejection(objName, patternKey, parentType, callerTemplate, availableTemplates)` → rejection JSON or null — the WWP parent-type gate, called from `ApplyPattern`.
- **Regression suite — ~48 new assertions across 6 files:**
  - `RecipeCatalogTests` (11) — list / known recipe / unknown / empty name / case-insensitivity / wwp_on_webpanel emphasises inspect-first.
  - `WhoamiPlaybooksTests` (7) — playbooks block presence, 6 canonical routes, parent-type-check emphasis, index-state cache reflects updates.
  - `ToolDefinitionsRedirectsTests` (7) — apply_pattern mentions inspect+parentType, create_object redirects WWP, edit warns about PatternInstance vs WebForm, whoami points at playbooks, `genexus_recipe` registered, `analyze.mode` drops `explain`, apply_pattern declares `validate` boolean.
  - `LifecycleResultTests` (6) — running returns Pending without isError, succeeded surfaces stored result, failed/cancelled mark isError, null-result terminal envelope, null-job guard.
  - `PatternApplyTypeGateTests` (17) — Transaction case-insensitive eligibility, WebPanel/SDPanel no-template path, non-eligible types rejected with `validParentTypes` + hint, bad template surfaces availableTemplates, case-insensitive template match, non-WWP keys pass through, null parent type rejected, empty available list skips check.
  - `E2ELiveSmokeTests` — 7 LiveKbFact-gated end-to-end tests against the published Gateway over stdio (whoami latency / explain NotImplemented / query Index pollution / read availableParts / navigation status / apply_pattern type-gate rejection / apply_pattern validate happy path requiring WWP). New `LiveGatewayHarness` spawns the process and runs the JSON-RPC handshake — mirrors the scripts under `scratch/` so the regression contract matches the live audit.
- **`ToolSchemaSizeTests` budget bumped 6300 → 6700** to absorb genexus_recipe (~80 tokens), apply_pattern `validate` + parent-type hint (~80), description front-loading on create_object/edit/whoami (~100). Net actual ~6624 tokens.
- **Contract-discovery goldens refreshed** to include `genexus_recipe` and the updated descriptions.

### Install / DX hardening (corporate Windows + AppLocker)

Triggered by a live `2.3.4 -> 2.6.3` upgrade report on a UNIVALI-domain machine. Symptoms the user hit: `npm update genexus-mcp -g` succeeded but `whoami` kept returning the old version (config pointed at a stale exe copy outside `node_modules`); after repointing at the npm-bundled exe under `%APPDATA%\Roaming\npm\node_modules\genexus-mcp\publish\`, `claude mcp get` reported "Failed to connect" and direct execution gave `Acesso negado` — domain AppLocker / SRP blocks exec from `%APPDATA%`. None of the diagnostic surfaces (`whoami`, launcher stderr, `Failed to connect`, `npm install -g`) pointed at the policy. Six fixes close the gaps:

- **Launcher (`cli/index.js`) emits an actionable AppLocker hint on `EACCES`/`EPERM`.** Detects "Access is denied" / "Acesso negado" / `err.code === 'EACCES' || 'EPERM'` from the gateway spawn, identifies whether the exe lives under `%APPDATA%` / `%LOCALAPPDATA%` / `%TEMP%`, and prints: cause (AppLocker/SRP), the restricted zone tag, and the one-liner `iex (irm .../scripts/install.ps1)` remediation. Generic `Failed to start gateway process: ...` is no longer the user's only signal.

- **`doctor` gains two checks and probes by default.**
  - `gateway_exe_path_safety` — warn when the bundled exe is under `%APPDATA%` / `%LOCALAPPDATA%` / `%TEMP%`, with the install.ps1 remediation.
  - `client_config_sync` — reads each client config (Claude Desktop/Code/Cursor/Antigravity/OpenCode/Codex; mcpServers JSON, OpenCode JSON, Codex TOML) and compares the configured `command` against the npm package's bundled exe. If a client points at a divergent `.exe`, the check warns explicitly that **`npm install -g genexus-mcp@latest` will NOT update that instance**, citing the mismatch path. Directly addresses the "npm update was a ghost operation" complaint from the report.
  - `gateway_spawn_probe` now runs by default (was `--full`-only), so cold-install failures surface immediately. Probe failures that look like access-denial are tagged with the AppLocker hint inline in `detail`.

- **`status` exposes `pathSafetyWarn: boolean` in both modes** plus, when true, prepends the AppLocker remediation as the first `help` line. `ready: true` no longer hides the runtime risk.

- **`init --write-clients` advisory + GENEXUS_MCP_GATEWAY_EXE guard.**
  - When patching clients on Windows without `GENEXUS_MCP_GATEWAY_EXE` set, both interactive and non-interactive init add a help line explaining that the npx launcher resolves the gateway from `%LOCALAPPDATA%\npm-cache`, commonly blocked by corporate AppLocker, and pointing at `scripts/install.ps1`.
  - `patchClientConfig` refuses to write a broken path: if `GENEXUS_MCP_GATEWAY_EXE` is set but the file does not exist, throws `code: 'GATEWAY_EXE_MISSING'`. Both init flows catch that code specifically and return a dedicated non-truncated error envelope with the path checked and the two remediation options. Previously, init would silently write the dead path into six client configs.
  - The two `catch {}` blocks that swallowed real errors (`Failed to write configuration.` / `Interactive init failed.`) now surface the underlying `err.message` (sanitized).

- **`genexus-mcp update` detects client drift.** Beyond fetching the latest release tag, runs the same client-config scan as `doctor` and, when any client points at a divergent `.exe`, emits a WARNING help line: "N AI client(s) point at a gateway exe that is NOT this npm package — `npm install -g` will NOT update them. Mismatches: ... Re-run scripts/install.ps1 (or genexus-mcp init --write-clients) to resync." Same payload exposes `clientDrift[]` for tools that consume the structured envelope. Stops `update` from giving false reassurance after `npm install -g` while the actual exe in use stays stale.

- **`scripts/install.ps1` probes the installed exe before declaring success.** After extraction, runs `GxMcp.Gateway.exe --axi-spawn-probe` via `Start-Process -PassThru -WindowStyle Hidden`, waits 600ms, kills the probe. On `Access is denied` / `Acesso negado` / `0x80070005` / `UnauthorizedAccess` it aborts (rolling back any temp artifacts) with: the blocked path, an explanation that AppLocker default rules deny exec from `%APPDATA%` / `%LOCALAPPDATA%` / `%TEMP%`, remediation suggestions (admin install → `C:\Tools\GenexusMCP`; non-admin → explicit `-InstallDir C:\Apps\GenexusMCP`), `Get-AppLockerPolicy -Effective -Xml` for diagnosis, and event-log pointers (`Microsoft-Windows-AppLocker/EXE and DLL`, IDs 8003/8004). Other launch failures surface the real error instead of "extraction succeeded → done". The next user with this exact policy will be blocked at the installer with the right answer, not at "Failed to connect" hours later.

Internal: new shared helpers in `cli/lib/config.js` — `isPathLikelyAppLockerBlocked(exePath)` (returns the restricted zone name or null), `normalizeExePath(p)` (case-fold + slash-normalize for Windows path comparison), `readClientCommandEntry(client)` (extracts the `genexus` entry's `command` from `mcpServers` JSON, OpenCode JSON, or Codex TOML). Consumed by the launcher, doctor, update, and the patchClientConfig guard.

## v2.6.3 — 2026-05-20

Bug-fix pass uncovered by live-testing v2.6.2. Two gateway-side gaps prevented `lifecycle cancel` / `lifecycle status` from resolving when callers used the canonical `target=op:<jobId>` shape — exactly the call pattern documented in the tool help. Both close now.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`McpRouter.ResolveJobId` strips the `op:` prefix.** Callers pass `target=op:<jobId>` to lifecycle cancel/status; `ResolveJobId` returned the string verbatim, so `JobRegistry.Get("op:<id>")` always returned null, and cancel fell through to the OperationTracker path which doesn't track build/edit jobs — surface error: `"NotFound"` even when the job was registered and running. Now strips the prefix (case-insensitive, idempotent for non-prefixed inputs). 2 new unit tests in `LongPollTests`.

- **`lifecycle status target=op:<jobId>` consults JobRegistry before falling through to OperationTracker.** The previous order routed every `op:<id>` shape to `_operationTracker.BuildOperationStatus`, which is a different lifecycle (gateway-internal request handles, not async jobs) and reported `NotFound`. The status path now checks `JobRegistry.Get(operationId)` first and only falls back to OperationTracker when the id isn't a registered job. Cancel was already covered by the ResolveJobId fix; this closes the symmetric status/result gap.

### Internal

- Live test of v2.6.2 confirmed both fixes end-to-end against KB `AcademicoHomolog1` (build started → `lifecycle cancel target=op:<jobId>` returned `{status:"Cancelled"}` + Control:Cancel fanout → `lifecycle status target=<jobId>` returned `{status:"cancelled", summary:"Cancelled by client...", completed_at:"..."}`). Worker 408/408, gateway 254/254 (+2 ResolveJobId prefix-stripping tests).

## v2.6.2 — 2026-05-20

Observability + cancel reliability + pattern-parity harness. The three together close the "is the agent allowed to be assertive?" loop: writes now self-report which SDK path they took (so we know where parity regresses), `lifecycle cancel target=op:<id>` actually stops the worker (was previously a no-op for async builds/edits), and we ship the test harness that lets a contributor with a WWP-licensed KB verify byte-equivalence against the IDE.

### Added

- **`_meta.sdkPath` tag on every write response.** New `Helpers/WriteResultMeta.cs` attaches a coarse, idempotent label describing which write strategy the handler picked: `typed-sdk` (IDE-native setter), `typed-writer` (our typed helpers), `raw-xml` (XElement.SetAttributeValue / source replace), `sdk-pattern-engine` (IPatternEngine.ApplyPattern), `ops` (semantic-ops / json-patch), or `hybrid` (bulk batch with mixed paths). The tag is idempotent: a deep writer's specific value (e.g. `raw-xml` from `LayoutService.SetProperty`) is preserved when a wrapper later defaults to `typed-sdk`. The KPI we get from this is the first objective measure of how often each path is used — needed to track parity-with-IDE regressions over time.

- **`PatternParityHarness` + `PatternApplyParityTests`.** Five-dimension diff (generated family, PatternInstance XML, WebForm XML, Variables, Rules) between MCP-driven `apply_pattern` output and IDE "Right-click → Apply Pattern" output. Each dimension reports PASS/FAIL independently with a focused detail message (first-divergence index for XML, set-diff for collections). XML normalization sorts attributes alphabetically before comparison so serializer nondeterminism doesn't false-fail the test. `ParityReport.ToMarkdown()` emits a human-readable report. Integration test gated by `[LiveKbFact(requiresWWP: true)]` plus `GXMCP_PARITY_MCP_NAME` / `GXMCP_PARITY_IDE_NAME` env vars; 9 unit tests cover the diff dimensions on JObject fixtures so the harness itself stays regression-protected even when the live KB run is skipped.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_lifecycle action=cancel target=op:<id>` actually cancels async builds/edits.** Previously the worker-side `WorkerCancellationRegistry.Cancel(jobId)` returned `NotFound` because the original async command was dispatched without a `cancelToken` — only search/impact/analyze opted-in per-handler. Now: (a) the gateway injects `cancelToken=jobId` into every async command it starts (`Build/Build`, `Build/RebuildAll`, async edit commands); (b) the worker's `CommandDispatcher.Dispatch` blanket-registers the token once at entry so every handler running under it inherits a single shared CTS; (c) `WorkerCancellationRegistry.Register` is now refcounted so inner handlers that also register the same token (search/impact still do) share the registration without their `Dispose` stripping the outer scope's registration first. Net effect: a single `lifecycle cancel target=op:<id>` resolves the right CTS regardless of which handler is currently in flight.

### Internal

- `WriteResultMeta.TagSdkPath` is the single chokepoint. Instrumented at: `WriteService.WriteObject` / `ApplySemanticOps` / `ApplyJsonPatch` / `AddVariable` / `DeleteVariable` / `DeleteVariables` / `ModifyVariable` / `BulkWrite` (chokepoint: `WrapWithPersistedState`), `LayoutService.SetProperty` / `SetProperties`, `PatternApplyService.ApplyPattern` (tagged `sdk-pattern-engine`). Bulk results inherit the per-item path or report `hybrid` when items disagree.
- `Helpers/WorkerCancellationRegistry.cs` rewritten around a `RefCount`-bearing `Entry` so `Register` / `Scope.Dispose` are nestable; the dictionary key remains the token string for `O(1)` `Cancel`. Test seam (`Reset`) unchanged; existing per-handler `using` blocks in `CommandDispatcher` still work and now share state with the new outer scope.
- `Program.cs` async build dispatch (line ~1654) and async edit dispatch (line ~1853) both inject `cancelToken = job.Id` into the worker command's params. The control fan-out path that already lived at `Program.cs:1474` continues to fire — now it actually finds the registration.
- Tests: worker 399 → 408 (6 `WriteResultMetaTests` + 5 `WorkerCancellationRegistryNestableTests` + 9 `PatternParityHarnessTests` − 1 LiveKbFact skipped on CI; net +9 enabled). Gateway 252/252 unchanged. All three additions are pure-data unit-testable so the suite stays fast and CI-green.

## v2.6.1 — 2026-05-20

`genexus_create_object` now creates **any** object the GeneXus IDE can create, and it grew a real Domain path. Reported by Edgar: trying to create a `UserStatus` enumerated domain via the MCP failed with "MCP doesn't support domain creation"; this release closes that gap and the underlying gap that produced it — the tool only knew about a hardcoded list of types.

### Added

- **`genexus_create_object type=Domain` — full domain creation, including enumerated.** New optional fields: `dataType` (`Character` default, `VarChar`, `Numeric`, `Date`, `DateTime`, `Time`, `Boolean`, `LongVarChar`, `Blob`, `Image`, `GUID`), `length`, `decimals`, `signed`, `description`, `basedOn` (inherit from an existing domain), and `enumValues` (array of `{name, value, description?}` for enumerated domains). For Character/VarChar domains the `value` must be a quoted literal (e.g. `"\"A\""`). Response `_meta` echoes back what was applied plus an `enumHint` so the agent can verify via `genexus_analyze`. Tested live against the Edgar case: `UserStatus` with three enums (`Active="A"`, `Inactive="I"`, `Blocked="B"`) — round-trips through `genexus_analyze` / `genexus_inspect`.

- **Generic type resolution covers every IDE-creatable object.** New `ResolveObjectTypeGuid` walks two paths: a typed-descriptor table (Transaction, Procedure, WebPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, ExternalObject, Theme, Image, Menu, Menubar, Stencil, UserControl, WorkPanel, Report, API, URLRewrite, MiniApp, SuperApp, DesignSystem, ColorPalette, OfflineDatabase, DataView, Group, Language) and a reflective fallback over `Artech.Genexus.Common.ObjClass` static Guid fields (Dashboard, SDPanel, Query, QueryDashboard, WorkflowDiagram, ConversationalFlows, TestSuite, ThemeClass, ThemeColor, ThemeTransformation, DesignSystemClass, WorkWithDevices, WorkWithWeb, WikiPageKBObject, TranslationMessage, DataStoreCategory, GeneratorCategory, DeploymentUnitCategory). Aliases recognised: `StructuredDataType` → SDT, `BusinessProcessDiagram` / `BPD` → WorkflowDiagram, `PanelForSD` → SDPanel. The previous hardcoded if/else chain covered eight types; the new resolver covers everything `ObjClass` exposes.

- **`Helpers/DomainPropertyApplier.cs` — reflective Domain plumbing.** Applies `Type` / `Length` / `Decimals` / `Signed` (eDBType enum on real SDK, string on test fakes — both handled), `DomainBasedOn`, and `EnumValues` (built via `Artech.Genexus.Common.CustomTypes.EnumValue` / `EnumValues` + persisted via `Artech.Genexus.Common.Properties+ATT.SetEnumValues` on the IPropertyBag). The resolved `Type` and `MethodInfo` are cached statically so batch domain creation doesn't rescan loaded assemblies. Falls back to a direct `EnumValues` property setter if the SDK helper isn't resolvable.

### Internal

- Shared the canonical-name → `eDBType` table: `AttributeTypeApplier.CanonicalToEdb` is now `internal` (was private) and `DomainPropertyApplier.ApplyPrimitive` consumes it — one synonym table, two callers.
- `ResolveType` and `ResolveFromObjClassField` prefer assemblies whose name starts with `Artech.Genexus.Common` before falling back to a full `AppDomain` scan; on a GeneXus host with 100+ loaded assemblies, that drops resolution from N-way to one. `_typeGuidCache` (object-class Guids), `_typeCache` (CustomTypes), `_setEnumValuesMethod`, and `_objClassType` cache the resolutions for batch calls.
- `TrySetProperty` uses `Convert.ChangeType` against `Nullable.GetUnderlyingType(prop) ?? prop` instead of branching on boxed int / bool — naturally covers long, short, double if the SDK adds them.
- `OperationsRouter.ConvertToolCall` forwards the new Domain options verbatim (`dataType`, `length`, `decimals`, `signed`, `description`, `basedOn`, `enumValues`); `CommandDispatcher` passes the full `args` JObject to `ObjectService.CreateObject(type, name, options)` so future option bags don't need to be threaded through the gateway-router schema.
- Help catalog and `tool_definitions.json` reworked: Domain section with the exact Edgar `UserStatus` example, full type enumeration in the description, schema fields for the new options. Discovery golden fixture regenerated.
- Tests: `DomainPropertyApplierTests.cs` covers the fake-SDK path (Type/Length/Decimals/Signed string fakes, ApplyDomainBasedOn, ApplyEnumValues hard-fail when SDK types aren't loadable, empty-list early return). Worker 388/388, gateway 252/252.

## v2.6.0 — 2026-05-20

WorkWithPlus on a bare WebPanel now works end-to-end. Apply the pattern, get a host plus a real layout projected onto the WebPanel's WebForm. Edit the host's PatternInstance and the projection updates automatically. Plus a new SDK probe tool, honest no-op detection on unsupported target shapes, and a pile of fixes uncovered during the investigation.

### Added

- **`genexus_apply_pattern` on a WebPanel target — full direct-attach + projection.** Apply WorkWithPlus to an empty WebPanel and the MCP attaches a `WorkWithPlus<X>` host with a real PatternInstance derived from a registered KB template, then runs the SDK's `IPatternBuildProcess.UpdateParentObject` so the WebPanel's WebForm reflects the projected layout immediately. The original WebPanel stays in place — no rename, no destruction. Pass `settings.template` matching a `WorkWithPlus for Web Template` object in your KB (common names: `MatIsoTemplate`, `TransactionResp2`, `PopoverEmpty`). When omitted, an available template is auto-discovered. The response includes `availableTemplates` so the agent can switch templates on a second call without guessing.

- **Auto-project on `genexus_edit` of a host's PatternInstance.** Every successful pattern-part edit on a `WorkWithPlus<X>` host automatically re-runs the projection step against its parent WebPanel — agents shape the screen via PatternInstance XML and the WebPanel updates in the same call. The response carries a `projection` block (`status`, `parent`, `parentType`, `note`) so callers can confirm the layout reflects the edit. The index cache for the affected parent is refreshed in the same code path, so a follow-up `list_objects` / `inspect` / `query` sees the new state without waiting for reindex.

- **`genexus_sdk_probe` — first-class scanner of loaded GeneXus SDK assemblies.** Dumps every public type, method, property, constructor, and field across `Artech.*`, `Genexus.*`, `DVelop.*`, and `GeneXus.*` assemblies to `docs/sdk-probe/`: `raw.json` (full structured tree), `INDEX.md` (per-namespace navigation), `generators.md` (filtered to types whose name suggests they participate in code generation — `Generator`, `Builder`, `Apply`, `Refresh`, `Update`, `Project`, `Engine`, `Helper`, `Service`, `Resolver`, …). Built for SDK exploration: investigators can grep the JSON or read the markdown without writing one-off reflection code. Picks output via `GX_MCP_SDK_PROBE_DIR`, the repo's `docs/sdk-probe/` if found, or `%TEMP%/gxmcp_sdk_probe/`.

- **`genexus_apply_pattern` returns `generatedObjects` honestly.** Previously empty on Transaction targets even when the engine had created the full WW family. Now resolves the canonical family (`WorkWithPlus<X>`, `WW<X>`, `View<X>`, `ExportWW<X>`, `ExportReportWW<X>`, `Prompt<X>`) via name lookup and surfaces what's actually present. The host is also exposed as a top-level `patternHost` field for quick navigation to the editable PatternInstance.

- **`apply_pattern` projects `settings` JObject onto SDK `ApplySettings` on re-apply.** Best-effort property mapping: case-insensitive name match, recursive on nested objects, type coercion for primitives and enums (string or numeric). Unmapped keys are logged, not thrown. Lets agents pass partial settings without knowing the full SDK schema.

- **`genexus_create_object type=WebPanel` includes a structured `_meta.patternHint` and `nextStep`.** Tells the agent both real paths to a WorkWithPlus screen — direct WebPanel attach with a template, or Transaction-driven family generation — with ready-to-issue tool call shapes inline. The hint is generated for `WebPanel` and `SDPanel` types; other types continue to receive only `_meta.seeded`.

- **`genexus_edit` surfaces `EditingWebFormUnderPattern` warning.** When the agent edits the `WebForm` / `Layout` of an object covered by a WorkWithPlus PatternInstance, the response includes a warning identifying the pattern host. The edit still completes — this is advisory so the agent realises the next pattern apply may overwrite the visual edit and can choose to edit `PatternInstance` instead.

- **`genexus_apply_pattern` returns `status: "NoOp"` with an actionable recommendation when the SDK engine no-ops on a target.** Used to silently report `Success` while doing nothing on Procedure/SDPanel targets (and on WebPanel pre-fix). Now carries `noOpReason` explaining the SDK's behaviour and `recommendation` pointing at the Transaction path, plus an optional `sdkProbePath` when `GX_MCP_PATTERN_PROBE=1` is set.

- **`genexus_whoami` reports update availability as structured data.** The response includes an `update` block with `currentVersion`, `latestVersion`, `updateAvailable`, `checkedAt`, `releaseUrl`, `command`, and `restartRequired`. AI agents can detect a pending upgrade in the same call where they read the KB context, then proactively offer the upgrade command — no longer have to rely on the stderr-style `notifications/message` the user might miss. The data comes from a 24h-cached GitHub release check the gateway runs in the background on `initialize`; reading it is zero-latency. Set `GENEXUS_MCP_NO_UPDATE_CHECK=1` to disable the background check (corporate networks that block the GitHub API). Documented as the "Self-update protocol (LLM-facing)" section in `AGENTS.md`.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_apply_pattern` no longer drops `pattern` and `settings`.** The gateway's `OperationsRouter` wrapped the original arguments under `@params` for `apply_pattern`, `apply_template`, `bulk_edit`, and `diff`, but the worker dispatcher read fields at the top level — so `args["pattern"]` was always null and the tool returned `"Pattern key is required."` even when the caller had passed one. The dispatcher now unwraps the nested params object once, preserving any outer routing fields as a fallback.

- **`genexus_apply_pattern reapply=true` works on installs that lack the `ApplyPattern(PatternInstance, ApplySettings)` overload.** Previous logic threw `InvalidOperationException` because the reflection probe disambiguated overloads using `IsAssignableFrom(KBObject)` — but `PatternInstance` inherits from `KBObject`, so both overloads bound to the same field and the reapply slot stayed null. Disambiguation now uses exact-type matching, and `TryReapplyWithFallback` replays the void overload (which the SDK treats as a re-apply when an instance already exists) when the typed overload is missing.

- **`genexus_delete_object` removes the entry from the search index.** Previously the deleted object stayed visible to `list_objects` / `query` for several minutes until a full reindex caught up. The index cache's `RemoveEntry(type, name)` is now called inline after a successful SDK delete; results from index-backed tools reflect the deletion immediately.

- **`genexus_apply_pattern` updates the index cache for every generated family object.** Same gap as delete — after a Transaction-driven apply, the freshly generated `WorkWithPlus<X>`, `WW<X>`, `View<X>`, and export procedures were invisible to `list_objects` until a reindex. Each generated object is now `UpdateEntry`'d via the index cache before the apply response returns.

- **`genexus_worker_reload` reliably copies new binaries.** Previous helper used a single `Copy-Item -ErrorAction SilentlyContinue` that masked a real race — the gateway respawned the worker faster than the helper could copy, re-locking the .exe, and the silent failure went unnoticed. The PowerShell helper now retries up to 20 times at 500 ms intervals, kills any worker that respawned mid-copy so the gateway brings a clean one up with the new bits, and writes `worker_reload.last_result.json` next to the published binaries so callers can diagnose silent failures. The response is now `"Accepted"` (was misleading `"Success"`) and points at that status file.

### Changed

- **`apply_pattern` on existing-instance targets skips the engine reapply call.** The void `ApplyPattern(PatternInstance, ApplySettings)` overload throws `NullReferenceException` on the GeneXus 18.0.7 SDK whenever the IDE service container isn't around. The MCP now goes directly to `IPatternBuildProcess.UpdateParentObject` (the projection step), which works headlessly. The behavioural surface is identical from the caller's perspective: the host's PatternInstance is re-applied onto the bound parent. Engine `ReapplyCalls` are no longer made on this path.

- **`genexus_apply_pattern` tool description and `genexus_create_object` patternHint rewritten.** Both now document the two real target shapes (Transaction-driven family generation and direct WebPanel attach with template), with concrete `settings.template` examples. Previously the documentation either over-promised (`Pattern attaches in-place to any WebPanel`) or under-promised (`apply WWP only on Transaction`). The new copy matches the actual SDK behaviour after the fix.

### Internal

- New helpers, no public-API impact: `Services/WwpProjectionHelper.cs` (shared `TryProjectHostOntoParent` + parent resolution), `Services/SdkSurfaceProbe.cs` (reusable SDK scanner), `Tests/LiveKbFactAttribute.cs` (xunit `[Fact]` subclass that env-gates on `GXMCP_TEST_KB` / `GXMCP_REQUIRE_WWP` for integration smokes).
- `docs/sdk-probe/` directory carries the SDK map plus a `wwp-projection-discovery.md` narrative of dead ends and the working path. `raw.json` is gitignored (~17 MB, regenerated each apply); `INDEX.md`, `generators.md`, and `README.md` are tracked.
- Pattern-write path now exposes `WriteService.ForcePatternPartDirty` and `WriteService.ApplyPatternDataFromXml` publicly so `WwpProjectionHelper` can reuse the same Dirty/Mode bookkeeping the regular pattern write uses.
- New `Microsoft.Build.Framework` reference in the worker csproj so the MSBuild-style `WWP_ApplyTemplate` task's `IBuildEngine` contract resolves (the task's ctor still fails headlessly; we keep the route as a fallback in case future SDK versions relax the requirement).
- Tests: worker 379 → 382 (3 new `ApplySettings` projection tests, integration smokes env-gated via `LiveKbFact`), gateway 252 → 252 (golden discovery fixture regenerated for the new `genexus_sdk_probe` tool). All green; 2 worker tests skipped by design when `GXMCP_TEST_KB` is unset.

## v2.5.3 — 2026-05-19

### Added

- **`genexus_create_popup`** — author a popup WebPanel from a domain-level
  spec in a single tool call. Pass `title`, `description`, an array of
  `inputs` (radio / combo / text), `buttons`, plus `inParms` / `outParms` —
  the MCP emits the matching WebPanel with rules, variables, layout, and
  events parts wired together. Radio and combo inputs are emitted inside
  `Form type="layout"` so they render editable in the browser. Inputs can
  declare a `showWhen` predicate (e.g. `"answer == 'Y'"`) to bind their
  group's visibility to another input's value via a generated `Event
  Refresh`. Existing webpanels are updated in place; the generated layout
  is self-validated against the layout-quality scanner before persisting.

### Internal

- `Helpers/PopupLayoutBuilder.cs` is a pure XML/source builder with no SDK
  dependency, fully unit-testable. `Services/PopupTemplateService.cs`
  orchestrates `ObjectService.CreateObject` + `WriteService.AddVariable` /
  `WriteObject` against an `IPopupBackend` seam.
- Tool schema budget raised 6000 → 6300 tokens for the popup spec
  sub-schema. Discovery golden fixtures regenerated.
- Test surface: worker 365 → 379 (14 new in `PopupTemplateServiceTests`).

## v2.5.2 — 2026-05-19

This release brings the MCP closer to feature parity with the GeneXus IDE.
Three new tools, one major routing fix, four new layout-quality warnings, and
theme introspection. See `docs/mcp-roadmap-ide-parity.md` for the design
context.

### Added

- **`genexus_preview`** — render a WebPanel via headless Chrome (uses
  `chrome-devtools-axi` CLI). Auto-fills the launcher form, navigates to the
  target, and captures HTML / accessibility tree / screenshot / console
  errors. Optional baseline diff against
  `publish/worker/preview-baselines/<name>.a11y.json`. Config at
  `publish/worker/preview.config.json` (auto-created on first call).
  Structured errors for build failure, auth required, launcher missing, CLI
  missing, unsupported object type.

- **`genexus_apply_pattern`** — apply a GeneXus pattern (e.g. WorkWithPlus)
  to a parent object, equivalent to the IDE's "Right-click → Apply Pattern"
  menu. Invokes `Artech.Packages.Patterns.PatternEngine.ApplyPattern`
  directly (first-time apply or re-apply via `reapply: true`). Returns
  `{status: "pattern_unavailable"}` when the package or license is missing,
  rather than throwing.

- **Theme introspection via `genexus_inspect`.** Calling inspect on a
  `ThemeForWeb` or `ThemeForSmartDevices` object now returns the theme's
  class catalog: `{name, parent, isPredefined, category, controlTypes}` per
  class. Default 100-class window (catalogs can exceed 600 classes); pass
  `include=["classesFull"]` to get the full CSS rule and serialized
  property bag per class. Lets callers write `Class="AttributeBlue"` by
  name instead of resolving theme GUIDs by hand.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`gxButton OnClickEvent` for custom events.** Raw-XML writes that emitted
  `OnClickEvent="'MyEvent'"` were silently ignored by the HTML generator,
  which only reads the per-element XML attribute the SDK assigns (`Event`
  for `gxButton`, `eventGX` for `gxAttribute` / `gxImage`). The MCP now
  routes descriptor-named properties through the SDK's
  `PropertiesObject.SetPropertyValue` so the canonical XML attribute is
  emitted. Applies on every layout save; idempotent.

### Added — layout-quality warnings (`genexus_inspect.layoutGotchas`)

Four new static checks for patterns that compile clean but render wrong:

- `GotchaGxAttributeMissingDataField` — `<gxAttribute>` with neither
  `AttID` nor `DataField`. The SDK keeps a phantom control that binds to
  nothing.
- `GotchaUnknownControlType` — `gxAttribute ControlType="…"` value not in
  the SDK whitelist (catches typos like `RadioButton` without the space).
  Generator silently falls back to `Edit`.
- `GotchaWebComponentMissingObjectCall` — `<gxEmbeddedPage>` /
  `<gxWebComponent>` without `ObjectCall`. Renders an empty `<div>` at
  runtime.
- `GotchaCellOutsideTable` — `<cell>` or `<row>` not nested under a
  `<table>`. Generator wraps or drops silently.
- `GotchaDuplicateControlName` — two elements share an `id`; SDK
  auto-renames via `GetUniqueName` on save, so caller references to the
  original id break silently.

### Internal

- New `WebFormPreSaveValidator` wraps the SDK's
  `WebFormHelper.Validate(part, OutputMessages)` validator. Standalone for
  now; a follow-up release will surface validation errors directly in the
  edit response with a force-write escape hatch.
- `ContractGoldenHarness` gained a `GXMCP_UPDATE_GOLDEN=1` environment
  switch to regenerate discovery fixtures after intentional tool schema
  changes — saves a round-trip when adding tools.
- Discovery golden fixtures regenerated for the new tools.
- Tool schema budget: 5300 → 6000 tokens (raised again to 6300 in v2.5.3
  for `genexus_create_popup`).
- Test surface: worker 365 → 379 (+1 skipped live integration test for
  WorkWithPlus license), gateway 250 → 252.

## v2.5.1 — 2026-05-19

### Added

- **`genexus_inspect include=["variables"]` now returns `layoutAttIdsInUse`** (FR#3
  2026-05-19): array of `var:N` / `att:N` references already used in the WebForm
  layout. Lets the agent pick the next free slot when authoring new
  `<gxAttribute />` bindings instead of guessing var:N by position+offset (which
  doesn't hold once the WWP pattern adds system vars). Source:
  `AnalyzeService.cs` scans `WebFormPart.Document.OuterXml`.

- **`genexus_add_variable typeName` accepts SDT / BC / Domain bare names** (FR#4
  2026-05-19): previously rejected `SdtAluUniGraInfo` with `UnknownType` listing
  only primitives. Now bare identifiers (non-primitive, no parens) route through
  `VariableInjector.ResolveTypeObject` and bind via `BindVariableToSdt` /
  `BindVariableToBC` / `DomainBasedOn`. If the KB doesn't have a match, returns
  a clear `UnknownType` with the bad name in the message (no silent NUMERIC
  fallback). Existing `&Foo` (explicit domain prefix) and primitive paths
  unchanged.

- **`genexus_inspect include=["controls"]` fills `name` for `gxAttribute` /
  `gxButton` controls that omit `id` / `ControlName`** (FR#5 2026-05-19):
  synthetic `name = "{type}@{dataBinding}"` (e.g. `gxAttribute@var:8`). Gives
  the agent a stable handle to pass to `genexus_layout set_property`. Previously
  these entries had `name: null`.

- **`VariableInjector.GetVariableInternalId` now resolves the real layout id**
  (FR#3 fully fixed 2026-05-19): `Variable.Id` is the C# instance property the
  SDK uses to back `AttID="var:N"` references in layout XML — confirmed via
  live probe against ListaAtiCPAlunoUniGra (`TotalHorasCredito.Id=22` matches
  `AttID="var:22"`, `SaldoHoras.Id=33` matches `var:33`, etc). The previous
  implementation tried `GetPropertyValue("Id")` which queries the Properties
  metadata bag and returns null — only C# reflection on the instance surfaces
  the value. Helper now reflects `Id` first, falls back to bag keys, and only
  drops to enumeration-position when both fail. System vars Today/Time/
  Pgmname/Pgmdesc resolve to 1/2/3/4 (WWP creates them first); deleted
  variables leave gaps in the sequence. Knock-on: every consumer of this
  helper — `genexus_inspect.variables[].internalId`, `WebFormSchemaHints.
  LookupVarNameById`, `LayoutGotchaScanner` shadow-detection — now returns
  truthful values instead of position-based guesses.

- **`genexus_inspect include=["variables"]` now returns `layoutGotchas`** (FR#1 +
  FR#2 2026-05-19): static analysis array warning about layout patterns that
  compile clean but break at runtime. Currently detects two gotchas — see
  `LayoutGotchaScanner.cs`:
  - `GotchaGxButtonHtmlFormCustomEvent`: `gxButton OnClickEvent="'Custom'"` in
    `<Form type="html">` will be ignored by the HTML generator (always wires
    Enter regardless). Workaround suggestion points to `<gxBitmap eventGX="...">`
    or moving to `<Form type="layout">` with `<action onClickEvent="...">`.
  - `GotchaGxAttributeHtmlFormDiscreteReadOnly`: `gxAttribute ControlType="Radio
    Button" | "Combo Box"` inside `<Form type="html">` always renders disabled.
    The html-form generator does not emit editable radio/combo widgets — the
    original hypothesis that this was caused by variable-name shadowing of a
    transaction attribute was DISPROVED by a live probe (renaming the bound
    variable did not change the render). Workaround suggestion: move the
    control to `<Form type="layout">` with the WWP table pattern, use a User
    Control, or render raw HTML `<input type="radio">` via `gxTextBlock
    Format="HTML"` + JS wiring to a hidden gxAttribute (default ControlType is
    editable in html forms).

  Both gotchas are not "MCP bugs" per se — they're GeneXus HTML generator
  behaviors the agent can't change. But surfacing them at inspect time skips
  the build+browser smoke cycle that previously revealed them. Tests:
  `LayoutGotchaScannerTests` (7 new cases).

## v2.5.0 — 2026-05-18

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`PatchService` reported `Failed` when the auto-reconciler legitimately rewrote
  `childrenOrderedList` during a pattern write**: PatchService's
  `VerifyPersistedSource` ran a byte-level comparison of `finalCode` (the
  pre-reconciler input) vs the persisted XML (post-reconciler). When the
  reconciler added/removed/reordered list entries — its whole purpose — the
  verify flagged the difference as a divergence, triggered a fallback write,
  and returned `error: "Patch write fallback failed after persistence
  mismatch"` even though the save was correct. Pattern parts now skip the
  redundant PatchService-level verify and trust WriteService's internal
  `XmlEquivalence` check (which runs inside `WritePatternPart` after the SDK
  save). Same fix would surface SDK attribute-reordering as a false negative.
  Response now carries `persistedVerifyNote` explaining the routing.

- **`<userAction>` was unknown to `PatternChildOrderReconciler`**: caused
  every `TableActions` row that mixed standard and user actions to land in
  `skipped` (no typeCode known), leaving the list out of sync. `<userAction>`
  is a peer of `<standardAction>` — same row, same context-sensitive typeCode
  (17 selection / 18 transaction). Reconciler now treats them identically.
  Custom buttons like "Duplicate"/"Audit"/"Export" MUST be `<userAction>` (only
  `Trn_Enter`/`Trn_Cancel`/`Trn_Delete` are registered standard actions on a
  WorkWithPlus transaction; the SDK rejects unknown `<standardAction name>`
  during validated operations like `genexus_properties set`).

- **Singleton kinds (`<orders>`, `<grid>`) were incorrectly treated as
  "missing identifier" by `PatternChildOrderReconciler`**: `GetIdentifier`
  returned `string.Empty` for them (correct — their entry shape is
  `{level};{typeCode};` with an empty id slot), but the caller's guard was
  `string.IsNullOrWhiteSpace(identifier)` which lumped empty-string and null
  together. Result: every `TableFiltrosFundo` and `TableGrid` landed in
  `skipped` and never got a list, so the IDE could hide their children.
  Changed the guard to `identifier == null` so the intentional empty slot
  survives.

### Added

- **Auto-reconcile `childrenOrderedList` on pattern writes** (`PatternChildOrderReconciler`):
  WorkWithPlus stores per-parent rendering order in a `childrenOrderedList`
  attribute that the IDE follows blindly — children missing from the list are
  hidden, stale entries leave ghost slots. Callers (LLMs in particular) that
  add/remove/move pattern children would need to keep that attribute in sync
  by hand. Now `WritePatternPart` walks the parsed XML, rebuilds every
  `childrenOrderedList` from the actual child order, **invents the list when
  the parent has none** (so new containers added by callers automatically
  render in the IDE), and surfaces the diff in the response under
  `childrenOrderedListReconciliation` with `(created)` / before-after entries
  plus a skip list naming any parents where type-code or identifier could not
  be inferred — actionable signal that those subtrees may not render until
  the caller corrects the XML. Element-kind → typeCode table covers the
  common WWP nodes (table 01/02 context-aware, textBlock 27, errorViewer 28,
  attribute 22, gridAttribute 23, descriptionAttribute 25, standardAction
  17/18 context-aware, filterAttribute 12, order 13, orders 30, rule 56, grid
  31, eventBlock 75). Identifier extraction handles the composite shape
  inside `<order>` children and the empty-identifier convention for singleton
  kinds (`<orders>`, `<grid>`). Inherited area-code (level 2 vs 4) traverses
  the ancestor chain so a newly-added container picks up the right value
  even when its closest siblings don't have a list yet. Blocklist excludes
  structural elements that don't participate in the ordering scheme
  (`<instance>`, `<transaction>`, `<level>`, `<selection>`, `<WPRoot>`,
  `<rules>`, `<events>`, `<steps>`, `<parameters>`, `<filterAttribute>`).
  Covered by 11 unit tests in `PatternChildOrderReconcilerTests`.

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **Pattern (`PatternInstance` / `PatternVirtual`) writes silently no-op'd —
  `WritePatternPart` reported `Success` but the KB never changed**:
  Three root causes stacked:
  1. `ApplyPatternEnvelope` called `KBObjectPart.DeserializeFromXml(string)`,
     which on `Artech.Packages.Patterns.Objects.PatternInstancePart` only
     round-trips the `<Properties>` bag (`IsDefault` etc) — *not* the pattern
     data. Live SDK reflection showed the actual mutation entrypoint is
     `DeserializeDataFrom(XmlElement)` (inverse of `SerializeDataTo(XmlElement)`).
     The XmlElement must be the **parent** that *contains* the `<instance>`
     child; passing `<instance>` directly persists an empty `<instance/>`.
  2. After deserialize, the part still had `Mode == Unchanged` and
     `Dirty == false`, so `KBObjectManager.PrepareSave` short-circuited even
     under `KBObjectSavePreferences.ForceSave`. Mirrored the
     `WriteVisualPart` fix (lines ~1921–1933): explicitly set `part.Dirty =
     true` and `part.Mode = Modified` via reflection before save.
  3. `resolvedObject.EnsureSave(true)` was a no-op for the same reason —
     replaced with `resolvedObject.Save(KBObjectSavePreferences { ForceSave =
     true, ForceSaveDefaultParts = true, SkipValidation = true })`. Also
     promoted the post-save flush to synchronous (`ScheduleFlush(force: true)`)
     so the verification read sees the bytes on disk.
  Verified live against `WorkWithPlusAcao.PatternInstance` in
  AcademicoHomolog1: `mode: full` and `mode: patch` both persist with
  `persistedVerified: true` and round-trip cleanly via `genexus_read`.

- **`genexus_edit` `mode: patch` rejected pattern parts (`PatternInstance` /
  `PatternVirtual`)**:
  `PatchService.ReadSourceFast` only handled `VariablesPart`, the virtual
  `Structure` part, `WebFormXmlHelper.IsVisualPart`, `ISource`, and a reflective
  `Source`/`Content` fallback. Pattern parts (WorkWithPlus and other patterns)
  expose their editable XML through `PatternAnalysisService.ReadPatternPartXml`
  on a resolved WWP instance, so `GetPart` on the source object returned `null`
  or a non-source part and patch-mode failed with
  `"Part does not expose text source"`. `mode: full` already worked because
  `WriteService.WriteObject` routes pattern parts through `WritePatternPart`.
  Wired `PatternAnalysisService` into `PatchService` and, when
  `IsPatternPart(partName)` matches, route the read through
  `ReadPatternPartXml`; the write side already dispatches correctly, so the
  existing `WriteObject` call in `ApplyPatch` now persists pattern patches
  end-to-end (verification reads reuse `ReadSourceFast`).

- **`Documentation` / `Help` parts silently failed to persist via `genexus_edit`**:
  `DocumentationPart` and `HelpPart` do not implement `ISource`, and their `Content` /
  `EditableContent` properties are read-only on the part wrapper. `WriteService`'s
  generic fallback only probed `Source` / `Content`, so writes hit the
  `"No suitable method found to update part content"` warning, returned a misleading
  `status: "Success"` with the SHA-256 of an empty string as `persistedHash`, and
  did not mutate the KB. Added `TrySetDocumentationContent` which writes through
  `HelpPart.HtmlContent` (HelpPart route) or `part.Page.EditableContent` →
  `Content` → `StorableContent` → `InvariantContent` (WikiPage route),
  instantiating a `WikiPage` from the part's `Module` when `Page` was null.
  Also replaced the bogus `documentation` GUID in `PartAccessor` (the placeholder
  `26323631-…` that decodes to ASCII junk) with the real
  `BABF62C5-0111-49e9-A1C3-CC004D90900A` read from the `[Guid]` attribute on
  `DocumentationPart`.

## v2.4.3 — 2026-05-18

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **KB reopen warning after MCP edits (`11.0.0.0` vs GeneXus 18)**:
  worker now normalizes `.gxw` metadata right after `KnowledgeBase.Open(...)` using
  active installation from `GX_PROGRAM_DIR`. It updates `InstallationPath`,
  `ProductVersion`, `FriendlyVersion`, and `VersionNumber` so IDE reopen no longer
  warns about mismatched GeneXus installation after MCP writes.

## v2.4.2 — Unreleased

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
Systematic bug hunt following the v2.4.1 BC patches surfaced ten latent bugs sharing
the same fault patterns. All ten are fixed in this release; full worker test suite
(314/314) and gateway suite (241/241) green.

- **SDK bookkeeping bypass — `VisualStructureService` dropped attributes/levels on save**:
  `Services/Structure/VisualStructureService.cs` constructed `TransactionLevel` and
  `TransactionAttribute` via `new ...()` + `parent.Levels.Add(...)` / `parent.Attributes.Add(...)`.
  This is the exact pattern fixed in v2.4.1 for `TransactionDslParser` — items bypass SDK
  bookkeeping and are silently lost on `EnsureSave`. Now uses the typed `sdkLevel.AddLevel(...)`
  and `sdkLevel.AddAttribute(...)` methods.
- **SDK bookkeeping bypass — `RefactorService` dropped copied variables**:
  `Services/RefactorService.cs` used `Activator.CreateInstance(sourceVar.GetType(), ...)` plus
  `targetVarPart.Variables.Add(...)`. Replaced with the typed `VariablesPart.Add(string)`
  overload that registers the variable with the SDK and returns the linked instance.
- **SDK bookkeeping bypass — new sub-levels in `TransactionDslParser`**:
  The sub-level creation path (mirror of the attribute path already fixed in v2.4.1) used
  reflection + `Levels.Add`. Now uses `new TransactionLevel(parent)` + `parent.AddLevel(...)`.
- **`SdtDslParser` silently lost item types when SDK proxy didn't expose `eDBType`**:
  Two sites called `Assembly.GetType("Artech.Genexus.Common.eDBType")` and used the result
  without a null-check; subsequent `GetMethod(..., eDBTypeT)` returned null and the `Invoke`
  NRE was swallowed by an outer `catch`. SDT items round-tripped with the default type instead
  of the requested one. Added `ResolveEDbType()` helper that probes the preferred assembly,
  falls back to the statically-linked type, then scans `AppDomain` — and logs a structured
  warning when none resolves (same template as v2.4.1's `TransactionAttribute` fix).
- **Reflection AmbiguousMatchException risk in Report layout and SDT propagation**:
  `Helpers/ReportLayoutHelper.cs` (Band `Name`, items `Name`/`ControlName`,
  `Items`/`Elements`/`Controls`/`Components` collection probe) and `Helpers/SdtModelPropagation.cs`
  (`EntityKey.Id`) used `Type.GetProperty(...)` without `BindingFlags`, which can throw
  `AmbiguousMatchException` or pick the wrong shadowed member on the Artech SDK class hierarchy.
  This is the same fault that v2.4.1's `AttributeTypeApplier` fix addressed. Extracted
  `AttributeTypeApplier.GetPropertyUnambiguous(Type, name)` as a shared helper and routed all
  unsafe call sites through it.
- **`Split(':')` array-bounds crash on malformed `Type:Name` inputs**:
  `Services/ObjectService.cs:FindObject` and `Services/VisualizerService.cs` (two sites) blindly
  indexed `parts[1]` after `Split(':')`; inputs like `"Type:"` or `":Name"` from agents threw
  `IndexOutOfRangeException`. Guarded all sites; `FindObject` logs and returns `null` on
  malformed input. `Services/IndexCacheService.cs` (reference-graph enrichment) had the same
  pattern; now uses `IndexOf` + `Substring` with explicit bounds.
- **DSL parser missed `*` key-marker when it appeared on the type side**:
  `Helpers/DslParserUtils.cs` only stripped the trailing `*` from `node.Name`. Inputs like
  `TrnId : Numeric(4)*` left `*` in `node.TypeStr`, which `AttributeTypeApplier.Parse` rejected
  — the type spec was silently dropped. Now strips `*` from both sides and still marks `IsKey`.
- **DSL parser preserved `&` prefix on attribute names**:
  Inputs like `&UserLogin : Numeric` left `node.Name == "&UserLogin"`, causing case-insensitive
  attribute lookups to miss and duplicate-create the attribute. Verified that Transaction/Table/SDT
  structure DSLs treat `&Name` as an attribute name (not a variable reference), so stripping is
  safe. Three new regression tests in `DslParserUtilsTests`.

### Verified-but-unchanged

- **`Parsers/TableDslParser.cs` attribute creation path**: probed the runtime `TableStructurePart`
  via reflection; no typed `AddAttribute(...)` method exists on this SDK type (unlike
  `TransactionLevel`). Kept the legacy `ctor + Attributes.Add` pattern and added an explicit
  comment documenting the verification gap so a future SDK upgrade can revisit.

## v2.4.1 — 2026-05-16

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **`genexus_properties` set could not toggle Business Component (and other typed bool/enum properties)**:
  `PropertyService.SetProperty` passed the raw string value straight to the SDK's
  `SetPropertyValue(string, object)` overload. For properties whose underlying CLR type is `bool`
  or an enum (e.g. `idISBUSINESSCOMPONENT`, `idISBCEJB`), the SDK threw
  `InvalidCastException: Conversão especificada não é válida` regardless of the value form
  (`"True"`, `"true"`, `"1"`). The setter now coerces the string to the property's declared type
  (probed via `Definition.Type` / current value), falls back to `SetPropertyValueString` for
  textual properties, and only then to the untyped overload.
- **Structure DSL silently dropped newly-added Transaction attributes**: writing a Transaction's
  `Structure` part with new attributes returned `status:Success persistedVerified:false` and the
  attributes never landed. Four bugs stacked on this path:
  1. `DslParserUtils.ParseLinesIntoNodes` only stripped the `*` key marker when it ended the
     trimmed line, so DSL like `TrnId* : Numeric(4)` left the asterisk on `node.Name`. The
     lookup in `existingItems` then missed and forced the create-new branch.
  2. `TransactionDslParser.SyncTransactionNodes` looked up `TransactionAttribute` via
     `sdkLevel.GetType().Assembly`, but the runtime proxy's assembly doesn't expose that
     type — `attrType` came back null and the create-new branch was a no-op.
  3. The same path created the wrapper via `Activator.CreateInstance` + `Attributes.Add`,
     which doesn't run the SDK's bookkeeping; the next `EnsureSave` discarded the addition.
     Replaced with `sdkLevel.AddAttribute(globalAttr)` (the typed SDK method already used by
     `ObjectService.InitializeTransactionWithDefaultKey`).
  4. `AttributeTypeApplier.ApplyPrimitive` called `Type.GetProperty("Type"/"Length"/"Decimals")`
     directly; the SDK Attribute hierarchy shadows those properties, throwing
     `AmbiguousMatchException`. Now resolves the most-derived declaration explicitly.
- **`InjectionService` masked `IsBusinessComponent`**: line 135 read `trn.BusinessComponent`
  (no `Is` prefix) via `dynamic`, throwing `RuntimeBinderException` swallowed by an empty catch.
  BC structures never injected into context. Typed cast against `Transaction.IsBusinessComponent`.

### Added

- **Inspect surfaces Business Component flag**: `genexus_inspect` now returns
  `transactionMetadata.isBusinessComponent` for Transaction objects, so agents can verify BC
  state without paging through the ~150-entry property bag from `genexus_properties get`.

## v2.4.0 — Unreleased

### Fixed

- **Tool calls no longer hang when the worker pipe is unavailable.** When a worker's named pipe never became ready (30s wait timeout) or the command write to the worker failed, the MCP request was silently discarded and hung until the client-side timeout. The Gateway now returns an immediate JSON-RPC error (`code -32000`) naming the KB alias and failure reason, mirroring the existing crashed-worker error.
- **DSL parsers dropped attribute types**: `TransactionDslParser` and `TableDslParser` previously
  parsed `pNode.TypeStr` from the DSL but never applied it — new attributes silently defaulted to
  `Numeric(4)` and type changes to existing attributes were ignored. Both parsers now resolve the
  declared type via the new `AttributeTypeApplier` helper and set `Type`/`Length`/`Decimals` for
  primitives or `DomainBasedOn` for domain references (`UserLogin`, `AutoNum18`, etc.). The bug
  existed since `dfdd526` (v1.2.0). Workaround until now was `semanticops add_attribute type=…`.

### Changed

- **BREAKING (envelope)**: `axiCompact` now defaults to `true` for `genexus_query` and
  `genexus_list_objects`. Callers that relied on full payloads must now pass
  `axiCompact: false` explicitly. The flag is declared in `inputSchema` for discoverability.
- **Token reduction**: `tool_definitions.json` shrunk from ~5200 to ~4956 tokens by trimming
  the descriptions of `genexus_query`, `genexus_lifecycle`, `genexus_edit`, `genexus_analyze`,
  and `genexus_read`. Long-form help is now served on demand at
  `genexus://kb/tool-help/{name}` via the MCP resources protocol.

### Added

- **Observability**: worker spawn time and SDK init time are now measured per KB and exposed via
  `genexus://kb/health` (`spawnMs` samples + p50/p95, `sdkInitMs.lastMs`). New
  `src/GxMcp.Benchmarks` project provides a BenchmarkDotNet baseline for envelope projection,
  tool-definition loading, and spawn-tracker hot paths.
- **New tool**: `genexus_edit_and_build` collapses the edit → analyze impact → build callers
  workflow from 3-5 turns into a single call. Returns a composite envelope with `edit`, `impact`,
  and `build` blocks. The build runs asynchronously and is polled via
  `genexus_lifecycle action=status target=op:<taskId>`.
- **Error UX**: `genexus_edit` now embeds alternative matches inline (`alternatives` array) when
  an object name is ambiguous, so callers no longer need a separate `genexus_list_objects` turn
  to disambiguate.
- **Streaming**: long-running operations now emit `notifications/progress` bound to their
  `operationId`. Build phases, impact-analysis BFS, and KB index report incremental progress
  so the LLM can read status without polling `genexus_lifecycle action=status`. The gateway
  already forwards `notifications/progress` to both stdio and HTTP transports.
- **Fast index**: `BulkIndex` is now split into a lite pass (metadata only, ~30-45s on a
  38k-object KB) followed by background enrichment. `genexus_list_objects`, `genexus_read`,
  and `genexus_inspect` are usable immediately after the lite pass. `genexus_analyze
  mode=impact` enriches only the target's reachable graph on demand, returning in seconds
  even before full enrichment finishes. The legacy monolithic path is preserved behind
  the `Indexing.UseLitePass=false` flag in App.config for rollback safety.

## v2.3.8 — 2026-05-15

Two waves into a single release. Wave 1 (morning) shipped the six new tools and
the deferred items from v2.3.7. Wave 2 (afternoon, this commit run) closed the
remaining friction-report items (Tasks 1.1 → 7.2) plus the warm-start IndexState
fix, worker-side cancel for search, broader ErrorMessages coverage, compact
through the long-poll path, and an end-to-end smoke test composing the workflow.

**Final test suite: 494/494 green** (267 Worker net48 + 227 Gateway net8). The
previously-flaky `IdempotencyCacheTests.Eviction_LruDropsOldestWhenAtCapacity`
is now deterministic against the sharded LRU contract.

### Wave 1 — new tools + deferred items

- **`genexus_validate_payload`** (`Services/ValidatePayloadService.cs`) — pre-flight
  check: parses the XML, runs `WebFormSchemaHints.ScanForRejectedAttributes`, and when
  the current state is readable, computes the would-be structural diff against the
  persisted XML. Returns `status: Valid|Warnings|Error`, `preflightWarnings[]`, and
  `diff` without touching disk.
- **`genexus_bulk_edit`** (`Services/WriteService.BulkWrite`) — apply N independent
  edits in one call. Each item supports `{name, part?, content, type?, dryRun?}`.
  `stopOnError=true` halts at the first failure; remaining items return
  `status: Skipped`. Response carries `counts: {success, failure, skipped}` and a
  per-item `results[]` array.
- **`genexus_apply_template`** (`Services/ApplyTemplateService.cs`) — three predefined
  visual templates: `kpi_header` (title + 3 KPI attributes), `empty_state` (bitmap +
  caption), `confirm_dialog` (confirm/cancel button pair with event wiring). Goes
  through the existing `WriteService.WriteObject` path so dryRun, validation, and
  rollback behaviour are inherited.
- **`genexus_diff`** (`Services/DiffService.cs`) — unified text diff via the existing
  `Helpers/DiffBuilder.UnifiedDiff`. Modes: `textVsText` (two caller-provided strings)
  and `currentVsText` (current persisted part vs. a caller string). Useful for PR
  review and pre-save comparison.
- **`genexus_export_unified`** (`Services/ExportObjectService.cs`) — full state of an
  object as a single JSON envelope: every available part read in one shot. Drives
  cross-snapshot diffs and PR-review artifacts.
- **`genexus_delete_variable`** (carried over from v2.3.7) — already shipped.

### New flags on existing tools

- **`genexus_analyze mode=linter fix=true`** (`Services/LinterService.LintAndFix`) —
  walks the lint report and auto-fixes GX008 unused vars via `DeleteVariable`. Skips
  framework-managed vars (GAM/WWP+) automatically. Other rules surface in `skipped[]`
  with a reason; the fixed set returns in `fixed[]`.
- **`genexus_edit async=true`** (Gateway `Program.cs` async-edit intercept) — writes
  longer than ~30 s now follow the same pattern as `genexus_lifecycle build`: register
  a `JobRegistry` entry, fire-and-forget the worker call, return
  `{job_id, status:"running", estimated_seconds, hint}` immediately. Completion piggybacks
  on the next response via `_meta.background_jobs`. Same flag honoured on
  `genexus_add_variable` and `genexus_delete_variable`.

### Worker

- **Indexed source-search pre-filter** (`Services/SourceSearchService.cs`) —
  extracts alphanumeric literal tokens (≥3 chars) from the regex/callee, then drops
  index entries whose `SourceSnippet`, `Name`, or `Keywords` contain none of them
  before paying for `FindObject`. On the friction-report's
  `pattern: "Alu2RegProf|Alu2NumRegProf"` example this skips 90%+ of the entries
  without changing the final result (regex.IsMatch still gates output).
- **PatternVirtual raw-serialize fallback** (`Services/ObjectService.cs`) — when
  `PatternAnalysisService.ReadPatternPartXml` returns empty, fall back to locating
  the matching part on `obj.Parts` by type-descriptor or CLR-type name and serialise
  via `KBObjectPart.SerializeToXml`. Surfaces the part as raw XML when WWP+'s
  analyser bails out, instead of the previous hard "Pattern XML not available".

### Documentation

- The four items previously marked "Deferred — needs deeper work" in v2.3.7 are now
  shipped:
  - True async writes ✓ (gateway `async=true` intercept)
  - Theme/StyleSheet read ✓ (already worked through the existing generic
    `SerializeToXml` fallback — discovery fixed by the `typesAvailable` hint in v2.3.7)
  - Index-backed `search_source` ✓ (literal-token pre-filter)
  - PatternVirtual read ✓ (raw-serialize fallback)

### Schema budget

- `ToolSchemaSizeTests` budget bumped 4000 → 4600 tokens to fit the six new tools
  (current `tool_definitions.json` ~4498 tokens).
- Bumped again 4600 → 4800 for `nameFilter`/`descriptionFilter`/`pathPrefix` on
  `genexus_list_objects` (Task 2.2), and 4800 → 5000 for `includeCallees` /
  `buildPlanCap` / `compact` on `genexus_lifecycle` (Tasks 5.2, 6.1).
  Current size ~4890 tokens.

### Wave 2 — friction-report 2026-05-15 sweep (Tasks 1.1 → 7.2 + 8)

Closes the remaining items in the friction report; all features below have
matching tests under `GxMcp.Worker.Tests/` and `GxMcp.Gateway.Tests/`.

- **Index state on `whoami`** (Task 1.2): live Cold/Reindexing/Ready surface;
  `IndexCold`/`Timeout` envelopes on `search` (Task 2.1) so callers can wait or
  fall back instead of silently getting empty hits.
- **Unified call-graph service** (Task 1.3): single `CallerGraphService` replaces
  duplicate BFS in `AnalyzeService.ImpactAnalysis`; new `waitForIndex` flag on
  `analyze impact` (Task 1.4).
- **Discovery filters** (Task 2.2): `list_objects` gains `nameFilter`,
  `descriptionFilter`, `pathPrefix`.
- **Edit reliability**: EOL-normalised matching (3.1), byte-level `nearMatchHint`
  (3.2), multi-line `{find,replace}` patch shape (3.3), `persistedHash` +
  `persistedSnippet` on every response (3.4), patch-window rollback verification
  (4.6 — only the diverging window forces rollback, SDK normalisations elsewhere
  are reported).
- **Variables**: `VariableTypeResolver` synonym map (4.1); `add_variable` validates
  `typeName` instead of falling back to NUMERIC (4.2); new
  `genexus_modify_variable` atomic type change (4.3); symmetric `delete_variable`
  across WebPanel/Transaction/etc. (4.4); ghost-binding diagnostics + `[var:N]`
  resolver on rejection (4.5).
- **Segmented build** (5.1/5.2): `lifecycle build` accepts
  `includeCallees={none,direct,transitive}` (default `transitive`) and expands
  the target list reverse-topologically via `CallerGraphService` so callees
  compile before callers. `_meta.buildPlan` reports
  `{requested, expanded, includeCallees, cap}`; `BuildPlanTooLarge` envelope when
  expansion exceeds the cap (default 200).
- **Output size** (6.1/6.2/6.3): `lifecycle status compact=true` default returns
  counts + top-10 errors + warning dedup (opt out with `compact=false`); `read`
  paginates by default at 200 lines / 16 KB (`limit=0` opts out;
  `suggestedNextOffset`/`Limit` surface the next page);
  `_meta.background_jobs` dedups per session so completed jobs appear exactly once.
- **i18n** (7.1): `ErrorMessages.Translate` maps known PT-BR SDK diagnostics to
  canonical EN; original preserved in `_meta.sourceMessage` /
  `_meta.sourceDetails`.
- **Cancel** (7.2): `lifecycle action=cancel` with a `job_id` now signals a
  registered CTS in `BackgroundJobRegistry`, terminates the async build poller
  within one tick, and fans out a fire-and-forget `Build/Cancel` to the worker.
  Worker-side `CancellationToken` plumbing through long-running services
  (search, analyze) is deferred.

### Wave 2 follow-ups (post-self-review)

The first Wave 2 push had a few rough edges flagged during the post-shipping
review. These commits closed them:

- **Warm-start `IndexState`**: `whoami.index.status` reported `Cold` after a
  warm start even though list/search were hitting a fully-hydrated index.
  `IndexCacheService.GetIndex` and `KbService.BulkIndex` now publish Ready
  when they detect the in-memory index was loaded from the disk cache.
- **`compact` through long-poll**: the `LifecycleResponseShaper` was only
  wired into the legacy taskId status path. The `job_id` long-poll branch
  (`McpRouter.LongPollJob`) now also runs the shaper, so callers using
  `wait_seconds>0 + job_id` get the compact envelope.
- **Worker-side cancel for search**: `SourceSearchService.SearchAsJson` now
  accepts a `CancellationToken` and emits a `Cancelled` envelope mid-scan.
  Gateway-side `BackgroundJobRegistry.RegisterCancellation` is already in
  place; the remaining IPC plumbing (a cancel side-channel from gateway to
  worker over stdin) is still future work — see Known gaps below.
- **`ErrorMessages` table**: expanded from 9 to 20 patterns seeded by
  greping the actual friction-report transcripts. Covers Transaction /
  Procedure / SDT validation envelopes, "Não foi possível", "Erro ao",
  target-environment reorganization messages, and the inline-property
  diagnostics (`X é propriedade inválida`).
- **End-to-end smoke test**: `IdealWorkflowSmokeTest` (Worker) +
  `IdealWorkflowGatewaySmokeTests` (Gateway) compose the friction-report
  workflow — Cold→Ready transition, search envelopes, filter narrowing,
  pagination, segmented-build expansion, compact shaper, JobRegistry
  cancel/dedup, ErrorMessages round-trip. Catches the kind of integration
  break (warm-start IndexState) that escaped the original push.

### Gateway ↔ worker cancel side-channel (post-self-review)

The first cancel pass left the gateway poller terminating cleanly but the
worker running its SDK call to completion. Closed:

- **`WorkerCancellationRegistry`** (worker helper): static, thread-safe
  dictionary of `(cancelToken → CTS)`. The dispatcher registers a scoped
  CTS for thread-safe long-running commands (search, impact) and disposes
  on completion.
- **`method=control, action=Cancel`**: new dispatcher command marked
  thread-safe so it interleaves with an in-flight SDK call. Looks up the
  CTS by `cancelToken` and signals it. The worker returns
  `{status: "Cancelled" | "NotFound", cancelToken}` immediately.
- **Gateway fan-out**: when `lifecycle action=cancel` resolves a `job_id`,
  in addition to flipping the registry status and tripping the gateway
  CTS, it now sends a fire-and-forget `Control:Cancel` to the worker
  carrying the same token. Handlers honouring `CancellationToken`
  (currently `SourceSearchService.SearchAsJson`,
  `AnalyzeService.ImpactAnalysis`, and both `CallerGraphService` BFS
  walks) terminate within one iteration.
- **CT plumbed through `CallerGraphService.GetCallersTransitive` and
  `GetCalleesTransitive`** with backwards-compatible overloads, so
  `AnalyzeService.ImpactAnalysis` honours the registered token end-to-end.

### Breaking notes

- `lifecycle status` default is now `compact=true`. Callers that parsed
  `Errors[]` / `Warnings[]` / `Output` directly must pass `compact=false`.
- `lifecycle build` default is `includeCallees=transitive`. Pass
  `includeCallees=none` for the pre-v2.3.8 single-target behaviour.
- `genexus_read` paginates by default when an MCP-client read exceeds 200 lines
  or 16 KB. Pass `limit=0` to opt out.
- Error messages are translated to EN by default; the original SDK string lives
  under `_meta.sourceMessage` / `_meta.sourceDetails` whenever the translator
  rewrote anything.

## v2.3.7 — 2026-05-15

Friction-report sweep #3 (`docs/mcp-friction-report-2026-05-15.md`, since deleted).
13 actionable agent-facing rough edges from the WWP+ UI/UX session, all addressed.
No public API breaking changes.

365/365 unit tests passing (211 Gateway + 154 Worker). Build clean (0 errors).

### Worker (.NET 4.8)

- **#1 — Structured `verifyDiff` on Visual/Pattern write rejection**
  (`Helpers/XmlEquivalence.cs`, `Services/WriteService.cs`). The error envelope
  now carries `verifyDiff: { element, path, rejectedAttributes[], addedAttributes[],
  persistedAttributes[], requestedAttributes[] }` whenever the persisted XML's
  attribute set differs from the requested set. The agent no longer has to compare
  `left=[…] right=[…]` strings to figure out which attribute the SDK sanitised.
- **#2 — `PatternVirtual` filtered from `availableParts`**
  (`Structure/PartAccessor.cs`). The SDK exposes a `PatternVirtual` part in
  `obj.Parts` but has no working read/write path for it through the MCP — listing
  it sent the agent in circles. Hidden until a real read path exists.
- **#3 — `typesAvailable` hint on empty `list_objects` typeFilter**
  (`Services/ListService.cs`). When `typeFilter` matches zero entries but the index
  isn't empty, the response now includes `_meta.typesAvailable: [...]` with the
  distinct type names present so the agent discovers the canonical string instead
  of guessing (e.g. Themes may be indexed as `DKTheme`, not `Theme`).
- **#4 — `managedBy` flag on framework-injected variables**
  (`Helpers/FrameworkManagedVariables.cs`, `Services/AnalyzeService.GetVariables`).
  `IsAuthorized`, `SecurityFunctionalityKeys`, `Time`, `DiasSemanaFin` are tagged
  with their owner (GAM / WWP+). `LinterService` GX008 silences these to break
  the delete-readd-delete loop.
- **#5 — `genexus_delete_variable` tool**
  (`Services/WriteService.DeleteVariable`, `OperationsRouter`, `tool_definitions.json`).
  Symmetric to `genexus_add_variable`, idempotent. Refuses framework-managed vars
  with a `Refused` status instead of letting the SDK re-inject them silently.
- **#6 — `Source` deduped from `availableParts` when `Events` is present**
  (`Structure/PartAccessor.cs`). On WebPanels/Transactions the two labels resolved
  to the same `ISource` part; dropping `Source` from the list leaves a single
  canonical name (the `Source` alias still works via `PartAccessor.FindPart`).
- **#9 — Worker crash diagnostics** (`Program.cs`). `[WORKER-CRASH]` log line now
  carries memory (working set + private + GC), uptime, thread count, exception
  type/message, and the full stack when `AppDomain.UnhandledException` fires.
  Lets the gateway correlate disconnects with the actual cause.
- **#10 — Pre-flight schema scan on dry-run** (`Helpers/WebFormSchemaHints.cs`,
  `Services/WriteService.WriteVisualPart`). Dry-run now walks the input XML and
  emits `preflightWarnings: [{element, attribute, reason}]` for any attribute that
  isn't in the SDK's known accept-list for that element (e.g. `style` on `<table>`
  / `<gxTextBlock>`). Catches the sanitisation issue before the agent tries the
  real save and hits `Visual write verification failed`.
- **#11 — `acceptedAttributes` on controls repertoire**
  (`Services/UIService.cs`). `genexus_inspect controls` now surfaces
  `acceptedAttributes: [...]` per control entry, sourced from the same
  `WebFormSchemaHints` accept-list, so the agent sees the schema before editing.
- **#12 — Linter is now pattern-aware** (`Services/LinterService.cs`). When a
  `PatternInstance` part is detected on the object, `GX012 Direct Table Access in
  UI` is suppressed (the WWP+ pattern *prescribes* direct `For Each` in Event
  Start to hydrate SDTs — flagging that as a warning is noise).
- **#8 — `search_source` time budget** (`Services/SourceSearchService.cs`). Hard
  25 s budget on the source scan loop; partial results return with
  `budgetExceeded=true, budgetMs, budgetHint` instead of an open-ended >2 min
  wait. Index-backed search remains on the v2.4 roadmap.

### Gateway (.NET 8)

- **#5 wiring** — `genexus_delete_variable` registered in
  `OperationsRouter.ConvertToolCall` and `tool_definitions.json`.
- **#7 — Long-write timeout hint** (`Program.cs`). When a write times out at the
  gateway, the help array now spells out that the write has usually already
  persisted by the time the agent sees the timeout — poll `action='result'` once,
  then read back, instead of retrying the edit (which no-ops or conflicts).
- **#13 — `_meta.background_jobs` resilient injection** (`McpRouter.PiggybackJobs`).
  Previously, when `content[0].text` wasn't valid JSON or was missing entirely,
  the piggyback silently dropped the background-jobs snapshot — producing the
  intermittent "_meta às vezes aparece, às vezes não" the agent observed. Now
  wraps non-JSON text and falls back to attaching `_meta` to the result root for
  error envelopes, so background-job status is delivered on every response while
  a build is running.

### Deferred — needs deeper work

- True async writes (`#7` upgrade) — full `{job_id, status:"running"}` envelope
  on edits and SemanticOps. Mitigation (better timeout hint) shipped; full
  refactor needs idempotency/state-machine work.
- Theme/StyleSheet `edit` (`#3` upgrade) — read/edit of Theme objects programmatically.
  Mitigation (`typesAvailable` hint on empty typeFilter) shipped.
- Index-backed `search_source` (`#8` upgrade) — Lucene/ripgrep-style token store.
  Mitigation (25 s budget cap) shipped.
- `PatternVirtual` read/edit (`#2` upgrade) — implementing the SDK path for the
  virtual pattern part. Filtered for now.

## v2.3.6 — 2026-05-15

Less-turns pass: cut round-trips between the agent and the MCP by enriching
return payloads. Same code quality, same correctness guarantees, fewer
tool calls per task. No public API breaking changes.

### Worker (.NET 4.8) — less-turns

- **`inspect` now surfaces `callers[]`** (`AnalyzeService.GetConversionContext`) —
  top-20 incoming references resolved via `obj.GetReferencesTo()`, runs in
  parallel with the existing metadata tasks. Mata o `analyze(mode=impact)` /
  `query usedby:*` follow-up que o agente fazia depois de quase todo inspect.
  Opt-in via `include=["callers"]` ou default quando `include` é omitido.
  Adds `callersTruncated` flag when the 20-cap is hit.
- **`persistedSnippet` em falhas de edit** (`PatchService.AttachPersistedSnippet`) —
  quando `persistedVerified=false` (inicial ou pós-fallback), o payload agora
  inclui `{startLine, divergeLine, content, totalLines}` com ±10 linhas do
  estado real em disco em volta da primeira divergência vs. o que foi enviado.
  Antes a mensagem dizia "re-read source"; agora o agente confirma o estado
  visualmente sem chamar `genexus_read`.
- **`search_source` context bumped to ±3 lines** (`SourceSearchService.BuildHit`)
  — `contextBefore` / `contextAfter` agora são arrays (até 3 linhas cada) em
  vez de strings de 1 linha. Para a maioria dos hits o agente entende o
  callsite sem precisar de um `genexus_read` subsequente.
- **`inline_read_top` em `search_source`** (`CommandDispatcher.AppendInlineReadsForSourceSearch`)
  — espelha o pattern existente de `query` / `list_objects`. Dedup por
  `objectName` para que `N=3` retorne até 3 *objetos distintos* (não 3 hits
  no mesmo arquivo). `AppendInlineReadsCore` foi generalizado para aceitar
  `arrayKey` / `nameField` / `dedupe` opcionais; os call sites antigos
  mantêm comportamento idêntico via defaults.

### Schema budget

- **`tool_definitions.json` trimmed: 4150 → 3974 tokens** (orçamento 4000).
  Boilerplate `"Target KB (alias or path). Required when 2+ KBs are open."`
  (24 ocorrências) → `"Target KB. Required when 2+ open."`; descrição longa do
  `inline_read_top` em 3 tools → forma compacta. Sem perda de informação útil
  ao modelo. Pre-existing `ToolSchemaSizeTests` agora verde.

### Tests

365/365 unit tests passing (211 Gateway + 154 Worker). Build clean (0 errors).

## v2.3.5 — 2026-05-14

Two-pass performance + friction sweep. No public API breaking changes.
- **Phase 1 — preventive perf audit:** 21 changes across Worker (.NET 4.8) and
  Gateway (.NET 8) targeting allocations, lock contention, telemetry, and disk
  I/O on hot paths.
- **Phase 2 — friction-report 2026-05-14:** 10 changes closing the actionable
  agent-facing rough edges from the live debugging session that produced
  `docs/mcp-friction-report-2026-05-14.md`.

365/365 unit tests passing (211 Gateway + 154 Worker). Build clean (0 errors).

### Worker (.NET 4.8) — performance

- **`Logger` rewritten as async writer** (`Helpers/Logger.cs`) — `BlockingCollection`
  fed by ~194 call sites, drained by a dedicated background thread that issues
  one batched `File.AppendAllText` per drain. Previous global lock + sync I/O
  per call was the biggest hot-path tax in bulk index and search. Stderr fallback
  preserved so the Gateway capture path is unchanged.
- **`SearchService.Search` parallelism capped** — `AsParallel().WithDegreeOfParallelism(min(4, ProcessorCount))`
  prevents PLINQ from spawning one task per core on large KBs (50k+ objects).
- **`SearchService` instrumented** — `Stopwatch` + `[SEARCH-SLOW]` log when
  > 50 ms via `try/finally`. Search was the busiest hot path with no telemetry.
- **`IndexCacheService`** — search-index snapshot now flushed gzipped (`*.json.gz`)
  via temp + atomic move; flush throttle 10 s → 30 s; reader stays backward
  compatible with legacy plain JSON; legacy file cleaned up on first flush.
  `ResolveHierarchy` now cached per object Guid (invalidated on remove/clear).
- **`IndexCacheService.GetEntryStorageKey`** caches its `Type:Name` result on
  the `IndexEntry` (new `[JsonIgnore] StorageKey` field) to skip
  `string.Format` in every `AddOrUpdateEntryInParentIndex` lookup.
- **`VectorService.ComputeEmbedding`** — separator array hoisted to a
  `static readonly`; per-token lower-case avoids the full-string `ToLower()`
  copy in every bulk-index call (~30k/cold-start).
- **`ObjectService.ReadCacheTtl`** bumped 20 s → 60 s — read-after-read patterns
  from LLM agents in a single tool sequence now hit cache.
- **`Program.QueueWriter`** — `Write(string)` and `WriteLine(string)` acquire
  the lock once per call; old impl locked per character on every IPC write.
- **`Program.BackgroundQueue`** signalled via `AutoResetEvent` + new
  `EnqueueBackground` helper; loop wakes on signal instead of `Thread.Sleep(100)`.
- **`Helpers/CodeParser`** — 13 inline regex calls replaced with pre-compiled
  static fields (validator was rebuilding interpreted regex per line).
- **`Services/AnalyzeService`** — `Analyze` and `GetHierarchy` now de-duplicate
  references before issuing SDK `Objects.Get` calls (safe portion of the audited
  N+1; same-target edges no longer cost N round-trips). Audited refactor of the
  full SDK fetch pattern remains deferred until a regression suite exists.
- **Cold-start instrumentation** — `KbService.OpenKB` and the bulk-index thread
  now log `[KB-OPEN] elapsedMs=…` / `[BULK-INDEX] elapsedMs=…` so future
  regressions are visible.

### Gateway (.NET 8) — performance

- **`WorkerPool` per-KB spawn gate** — global `_spawnLock` replaced with a
  per-`Entry` `SemaphoreSlim`. Two clients opening different KBs no longer
  serialise behind each other. A narrow `_capacityLock` still protects the
  capacity-window/eviction.
- **`IdempotencyCache`** — `KbBucket` shards across 16 independent LRU slots,
  cutting hot-key contention by ~1/N. `GetOrCompute.WaitAsync` now bounded at
  30 s with a best-effort fallback (run factory bypassing the cache) so a
  stuck worker can no longer starve callers until the 65-min TTL.
- **`WorkerProcess`** — spawn retry uses exponential backoff (100/200/400/800/1000
  ms) + ≤50 % jitter instead of flat 1 s × 10. First retry fires 10× sooner.
- **`WorkerProcess.ProcessQueueAsync`** — `JsonConvert.DeserializeObject<JObject>`
  on the hot IPC path replaced with `JObject.Parse` (direct, no reflection-style
  dispatch).
- **`WorkerPool.SelectVictim`** — linear scan replaces `OrderBy`, dropping the
  full-sequence materialisation for eviction selection.
- **`ResponseSizeGuard`** — `StreamWriter` buffer 1 KB → 32 KB; new
  `ByteSize(string)` overload uses `Encoding.UTF8.GetByteCount` for callers that
  already have the serialised JSON in hand.
- **`McpRouter`** — `tool_definitions.json` hot-reload via `FileSystemWatcher`
  with 500 ms debounce. Subsequent `tools/list` calls observe the new payload
  without restarting the gateway.
- **Build flags** — `<PublishReadyToRun>true</PublishReadyToRun>`,
  `TieredCompilation`, `TieredPGO` enabled for Release publish;
  `ServerGarbageCollection` + `ConcurrentGarbageCollection` on the main
  `PropertyGroup`. Cold-start JIT cost drops significantly in published builds.

### Friction-report 2026-05-14 fixes (second pass)

- **#2 — `persistedVerified` false-negative mitigated** — `PatchService.VerifyPersistedSource`
  now retries the post-write read once after a 120 ms pause (the SDK sometimes
  flushes to disk slightly after `Save()` returns), and on persistent mismatch
  attaches a compact `Verify diff at char N: expected='…' actual='…'` hint so
  the agent can decide whether the rollback is warranted instead of looping
  re-tries.
- **#3 — Reverse-dep index now catches Event Start call sites** —
  `IndexCacheService.EnrichCallsFromTextualScan` (new) augments
  `obj.GetReferences()` with a textual scan over every `ISource` part on the
  object. Any `Identifier(` token that already exists in the index as a
  callable object type (Procedure / DataProvider / WebPanel / Transaction /
  Menubar / WorkPanel / BPD / SDT / Domain / ExternalObject) is added to
  `Calls` + the target's `CalledBy`. Hard "must exist in index" filter
  eliminates false positives from keywords.
- **#4 + #5 — `genexus_properties` accepts variable & control scope** —
  `PropertyService.FindControl` now resolves `&Name` to the SDK Variable and
  also takes a bare name as a Variable when the layout-control lookup misses.
  ControlType / ControlValues / Enabled / Visible / etc. now settable
  per-variable.
- **#11 — `genexus_edit mode=ops` schema enum** —
  `tool_definitions.json` now lists the supported RFC 6902 ops
  (`add | remove | replace | test`) and surfaces `path` as required.
- **#14 — Description as title-bar documented** — `genexus_properties`
  description in the schema explicitly notes the Description property doubles
  as title-bar text when a WebPanel/Popup is opened via `.Popup()`.
- **#15 — Linter `GX022`** — non-prefixed Layout elements (`<Button>`,
  `<Bitmap>`, `<TextBlock>`, `<Attribute>`, `<Grid>`, `<EmbeddedPage>`,
  `<Tab>`, `<Card>`, `<Group>`, `<Image>`, …) flagged as Warning with
  "did you mean `<gx{name}>`?". Previously these silently rendered as
  literal HTML and burned 2-3 build cycles to diagnose.
- **#16 — Patch `{find, replace}` JSON form now actually works** —
  `ObjectRouter` maps `patch={find,replace}` to the existing patch pipeline
  (find → context, replace → payload). The schema advertised this form but
  only the legacy `(operation, context, content)` triple worked before.
  Schema updated to also document the `{find, replace}` shape.
- **#17 — Whitespace-tolerant patch context** —
  `PatchService.TryWhitespaceNormalizedReplace` (new) added as a last-resort
  pass before reporting `NoMatch`. Tab-vs-space context differences now
  resolve: the matcher locates the unique window using collapsed-whitespace
  comparison and splices using the source's original indentation.
  `Ambiguous` is returned if the normalized match is non-unique.

### Friction-report 2026-05-14 fixes (first pass)

- **#1 + #13 — Variable `internalId` exposed** (`AnalyzeService.GetVariables`,
  `GetConversionContext`, new `VariableInjector.GetVariableInternalId`). Layout
  XML uses `AttID="var:N"`; agents can now resolve that mapping from
  `genexus_inspect`/`get_variables` instead of grepping the generated `.cs`.
- **#7 — `lifecycle action=cancel target=op:<id>` actually does something.**
  New Gateway intercept marks the operation `Cancelled` in `OperationTracker`,
  abandons the matching pending request with a structured error, and returns
  `{status:Cancelled, abandonedRequestId, message}`. The worker thread may
  still finish its SDK call but no further response is delivered. Unknown-op
  case now returns a specific "Unknown build taskId" message + hint instead of
  bare "Task ID not found".
- **#8 — `genexus_inspect controls`** — when the SDK web-tag tree walker
  returns empty (mixed HTML + gx-prefixed layouts), `UIService` now falls back
  to a direct XPath scan over `<gx*>` elements and surfaces
  `name/type/controlType/dataBinding/event` per control with `_fallback:true`.
- **#10 — `wait_seconds` cap 25 s → 90 s** (`McpRouter.MaxLongPollSeconds`).
  Builds of 50–70 s now converge in a single long-poll instead of 3.
- **#12 — Build noise filtered from `TailLines`** (`BuildService.HandleLine` +
  new `_rxModuleCopyNoise`). "Copiando módulo …" / "Restoring NuGet" /
  "Touching …" / "Wrote …" lines stay in `FullOutput` (terminal payload) but
  get dropped from the live tail so the agent sees real signal during a build.
- **#18 — Patch failure near-match diagnostic** (`PatchService.FindNearMatches`).
  On `NoMatch`, the patch response now includes a `nearMatches: [{line,
  similarity, snippet}]` array (top-3) + `nearMatchHint`. Agent adjusts the
  context block in one iteration instead of re-reading the whole file.
- **#19 — `lifecycle status` no longer returns full `Output` while Running**
  (`BuildService.GetStatus`/`GetResult`). The 200+ line build log was repeated
  on every poll; now only `TailLines` rides during Running and `Output` is
  attached at terminal state.
- **#20 — Linter `GX021`** — `parm(... out: &X ...)` without a matching
  `&X.Enabled = 1` in Event Start surfaces an Info issue. Catches the
  silent-disabled-control trap from the friction report.
- **#21 — Linter `GX020`** — `<gxButton onClickEvent="X"/>` in a WebForm
  without `Event Enter` defined surfaces a Warning. gxButton in HTML layouts
  only fires `Event Enter`; `onClickEvent` is silently ignored otherwise.

### Internal / docs

- New audit document: `docs/perf_audit_2026-05-14.md` (the Phase-1 baseline).
- Two false positives from the audit closed without code change because the
  code already addressed them: `IndexCacheService.FlushToDisk` (try/catch + log
  present) and Gateway `_pendingRequests` sweeper (`RunSessionCleanupLoop`
  already running on a 1-minute `PeriodicTimer`).
- Items deliberately deferred (require dedicated regression suite or new
  project scaffolding): full Newtonsoft → System.Text.Json migration in the
  IPC hot path, BenchmarkDotNet baseline project, OperationTracker exported
  as an MCP diagnostic endpoint, and the deeper SDK batched-fetch refactor for
  `AnalyzeService`.
- Friction-report items deferred for a dedicated session:
  - **#6** (`genexus_search_source` timeouts) — needs Lucene/ripgrep index.
  - **#9** (worker-disconnect orphan operationId) — needs durable op-state
    persistence (SQLite or similar) with TTL.

## v2.3.0 — 2026-05-14

Multi-KB parallel support + tool surface consolidation + official skill bundles.
One Gateway can now drive up to `Server.MaxOpenKbs` (default 3) concurrent KBs,
each in its own Worker process. Cross-KB tool calls run in parallel — no
serialization between KBs. Intra-KB calls remain serialized by the SDK's STA
constraint, as before.

### Consolidations (5 tools removed → registered in RemovedToolsRegistry for LLM auto-redirect)
- `genexus_open_kb` → `genexus_kb action=open`
- `genexus_get_sql` → `genexus_sql action=ddl`
- `genexus_get_sql_for_navigation` → `genexus_sql action=navigation`
- `genexus_summarize` → `genexus_analyze mode=summary`
- `genexus_explain_code` → `genexus_analyze mode=explain` (takes `code` arg)

Total tools: 33 → 29. Schema size: ~3141 → ~3714 tokens (multi-KB `kb` param
adds tokens; consolidations partly offset). Test budget bumped 3500 → 4000.

### Crash isolation (follow-up to initial v2.3.0 design)
- Pending requests now track their `WorkerAlias`. When a Worker crashes, only
  the requests bound to that KB are aborted with `-32603` — sibling KBs keep
  working. Previously stale pending requests waited for the 65-min sweep.

### `genexus_kb` enrichment
- `action=list` now returns `pid`, `workingSetBytes`, `workingSetMB`, and
  `idleSeconds` per open KB, so the LLM can self-throttle / pick a candidate
  to close before opening another.
- New `action=set_default` — persists `DefaultKb` to `config.json`
  (preserves any unmodelled fields).

### GitHub release notes
- `scripts/release.ps1` now extracts the CHANGELOG section for the released
  version and uses it as the release body (`gh release create --notes-file`).
  Falls back to `--generate-notes` if the section is missing.

### Bundled skills (imported from genexuslabs/genexus-skills, Apache 2.0)
- `nexa/` — full reference set: every GeneXus 18 object type, command,
  formula, type, property (was a stub before).
- `frontend/{chameleon-controls-library, mercury-design-system,
  design-system-builder, ui-creator}/` — Chameleon UI specs, Mercury DS
  tokens/bundles, design-system authoring, panel templates.
- `.gemini/skills/NOTICE.md` documents attribution + upstream refresh steps.

### Added
- **`WorkerPool`** (Gateway) — keyed by KB alias, LRU eviction when pool full,
  idle timeout reuses existing `WorkerIdleTimeoutMinutes`.
- **`KbResolver`** — maps `kb` tool arg (alias OR absolute path) to a
  `KbHandle`. Default-KB fallback: 1 KB open → uses it; 0 open + `DefaultKb`
  configured → opens it; 2+ open without `kb` → `KB_AMBIGUOUS` error.
- **`kb` parameter** on every non-meta tool (28 tools). Optional; required
  when more than one KB is open.
- **`genexus_kb` meta-tool** — `action: list | open | close`. List shows
  open KBs, configured `DefaultKb`, declared aliases, and `MaxOpenKbs`.
- **Config schema:** `Environment.KBs[]` (alias+path) and
  `Environment.DefaultKb`; `Server.MaxOpenKbs` (default 3).
- **Backward-compat:** legacy `Environment.KBPath` auto-migrates to a single
  `KBs[]` entry + `DefaultKb` at load time. Existing configs work unchanged.

### Changed
- `WorkerProcess` constructor now takes `(Configuration, KbHandle)`.
- `KbService` static fields (`_kb`, `_kbLock`, `_isOpenInProgress`) become
  instance fields — each Worker process holds one isolated KbService.
- Idempotency cache is now scoped by the resolved KB path (was previously
  the single `Environment.KBPath`).

### Internal
- `AsyncLocal<KbHandle?>` resolves the active KB at the top of
  `ProcessMcpRequest` and propagates to `SendWorkerCommandAsync` without
  threading new parameters through 7 call sites.

Spec: `docs/superpowers/specs/2026-05-14-multi-kb-parallel-design.md`.
Plan: `docs/superpowers/plans/2026-05-14-multi-kb-parallel.md`.

## v2.2.0 — 2026-05-13

Coordinated perf & stability release closing the tools-disappear-mid-session
bug and reducing roundtrips/payload across the MCP surface. All 13 changes
gated behind a single feature flag `MCP_PERF_PROFILE=v1` (default on).
Env-flip to `legacy` restores pre-v2.2.0 behavior. Total test count grew
from 135 → 199, all green.

### Polish (post-smoke-verification)
- **Piggyback injection layer fix.** `_meta.background_jobs` now injects
  into the inner `content[0].text` payload (which the LLM actually
  reads), not the JSON-RPC wrapper. Async build completions surface on
  the next tool response as designed.
- **Long-poll status accepts `target` as `job_id` fallback.** The
  `lifecycle status` tool conventionally takes `target`; LLMs and users
  pass the job ID there. Registry is probed first; legacy taskId-based
  status falls through unchanged when the value isn't a registered job.
- **`type` alias for `typeFilter` in list/query/search.** The
  `genexus_list_objects` / `genexus_query` / `genexus_search_source`
  routers now accept both names. Aligns with the rest of the tool
  surface where `type` is the conventional parameter name.

Spec: `docs/superpowers/specs/2026-05-13-mcp-perf-and-tool-stability-design.md`.
Plan: `docs/superpowers/plans/2026-05-13-mcp-perf-and-tool-stability-v2.2.0.md`.

### Fixed
- **Tools-disappear-mid-session bug** (`docs/issues/tools-disappear-mid-session.md`)
  — gateway-side `ResponseSizeGuard` caps per-tool payloads at ~220KB
  (≈55k tokens) before the harness-side truncation path can drop the
  tool registry. Payloads over the cap are replaced with a sentinel
  `_meta.truncated: {reason, original_size, cap_bytes, follow_up: {tool, args}}`
  pointing at a paginated continuation. Telemetry log line
  `[Gateway] OVERSIZE tool=X size=N` for one-release calibration.
- **`SystemRouter` "result" routed to "Status" instead of "Result"** —
  pre-existing routing bug surfaced and fixed during pagination work.

### Added (perf profile v1, default on)
- `genexus_lifecycle action=status` / `action=result` accept `page` /
  `page_size` (default 50, max 200); responses carry
  `_meta.pagination: {total, page, page_size, has_more}`.
- `genexus_edit` returns `post_state.diff` (LCS-based unified diff with
  `±3` context) by default — eliminates the re-read-to-verify turn.
  `verbose=true` adds wider slices; `return_post_state=false` opts out.
  Wired across ops, JSON-patch, and text-patch edit modes.
- `genexus_lifecycle action=build` / `rebuild` is non-blocking when
  `estimated_seconds ≥ BuildSyncThresholdSeconds` (default 20) — returns
  `{job_id, status: "running", estimated_seconds, hint}` immediately.
  Short builds use a synchronous fast-path returning the result in one turn.
- `_meta.background_jobs: [...]` piggybacks on every tools/call response
  when a session's `BackgroundJobRegistry` has running jobs or unseen
  completions. LLM can do other work while a build runs and discovers
  completion on the next tool call.
- `genexus_lifecycle action=status` with `wait_seconds=N` (clamped to
  [0, 25]) long-polls server-side until the job reaches terminal state
  or the timeout. One call instead of polling loop.
- Discovery tools (`list_objects`, `query`, `structure`, `search_source`)
  include `_meta.suggested_next: {tool, args}` pointing at the natural
  next call.
- List responses include `_meta.aggregates: {total, by_type}` computed
  during the same scan — eliminates "how many of X" follow-up calls.
- Empty results carry `_meta.empty_reason`: `no_matches` | `filtered_out`
  | `kb_not_loaded`.
- `genexus_read` accepts `parts: [...]` — surgical reads of named
  sections (Source, Variables, Rules, etc.). Backward compatible.
- `genexus_list_objects` and `genexus_query` accept `inline_read_top: 0-3`
  (default 0) — combined list-and-read returns `inline_reads: [{name, content}]`
  for the top N matches in one turn.
- Compact JSON output on tools/call responses: `Formatting.None` plus a
  recursive `StripNulls` pass that drops null properties while preserving
  empty arrays, zeros, false, and empty strings.

### Changed
- List items default to a minimal 4-field shape (`name`, `type`, plus
  two context fields like `path`/`parent`). Pass `verbose=true` to get
  the full per-item shape.
- Errors default to terse `{code, message, hint}` — stack traces and
  full SDK diagnostics dropped from the wire by default. Pass
  `verbose_errors=true` per-call, or fetch from `genexus_logs`, for
  full diagnostics.
- `tool_definitions.json` trimmed from ~9,600 tokens to ~2,800 tokens
  (71% reduction) — every conversation pays less for the fixed tool
  schema in the system prompt. All 32 tools preserved.

### Deferred
- TOON serialization (see spec open question). Revisit after one
  release of telemetry on what tokens are actually spent on.
- Real MCP `notifications/progress` for builds — same broadcast path
  is the leading suspect for the disappear-bug. Revisit once
  `ResponseSizeGuard` calibration data confirms or rules out that
  hypothesis.

### Rollout / Compatibility
- All changes additive on `_meta` or opt-in parameters. No changes to
  `tools/list` or `notifications/tools/list_changed` semantics.
- Existing callers that don't read the new `_meta` fields continue to
  work unchanged.
- Set `MCP_PERF_PROFILE=legacy` to restore pre-v2.2.0 behavior at the
  process level (single env-flip kill switch).

## v2.39.4 - 2026-08-10

Closes every item from the second-cycle friction report
`docs/mcp-friction-report-2026-05-13.md`, produced by a fresh real-KB session
against `AcademicoHomolog1`. Pending live smoke verification before the next
release tag.

### Fixed
- **`whoami.mcp.serverVersion` reads from the assembly version, not a hardcoded
  const.** `McpRouter.ServerVersion` now resolves at runtime via
  `AssemblyInformationalVersionAttribute` (set from the csproj `<Version>`).
  `scripts/release.ps1` mirrors the bumped npm version into the Gateway csproj
  so the version surface always matches the published build. Friction-report
  05-13 #1.
- **SDT Structure write now persists fully: parser dirty-flags every signal
  the SDK exposes, sync-commits Model + KB to disk, propagates the SDT to
  the Prototype model in SQL, and the validator no longer rejects multi-
  write sequences.** Four layers together close the bug:
  1. `SdtDslParser.Parse` reflects `Dirty/IsDirty` + `Touch/Modified/
     MarkDirty/OnChanged/NotifyChanged` onto `SDTStructurePart` and logs
     items-count pre/post-parse so the persisted state is unambiguous.
  2. `WriteService` Structure interceptor forces a synchronous
     `Model.Commit + KB.Commit` immediately after `EnsureSave` (instead of
     the debounced 2-second timer), so a follow-up save sees the new items
     on disk.
  3. `SdtModelPropagation.TryPropagateToPrototypeModel` mirrors Model 1 →
     Model 2 rows for the SDT, SDTStructure, SDTLevelEntity, and
     SDTItemEntity via direct SQL (decompresses the structure blob to
     discover the item EntityIds). Same surgical pattern as
     `WebFormCompositionRepair` (`9242c1d`); needed because
     `KBObject.Create(kb.DesignModel, ...)` never registers the item names
     in the Prototype model the validator queries.
  4. `PersistenceExtensions.EnsureSave` now reflects on
     `Artech.Architecture.Common.Objects.KBObjectSavePreferences`
     (walking loaded assemblies, since the type lives in
     `Artech.Architecture.Common`, not the KBObject's home assembly),
     sets `SkipValidation=true`, and retries `KBObject.Save(prefs)` only
     when the failure text contains `src0216`. This bypasses the SDK's
     stale in-process Prototype-model cache for the legitimate case
     (variable declared, SDT item present in Model 1) while leaving
     genuine validation errors (`src0059` syntax, undeclared variables —
     covered by the new hint in fix #3) untouched.

  Verified end-to-end by `scripts/smoke_2026_05_13.ps1`: a Procedure that
  binds `&Aluno : SdtFrictionProbe`, writes Source `&Aluno.AluCod = 42`,
  then patches Variables with `&Counter : NUMERIC(4,0)` — the original
  report's exact failure mode — now persists clean
  (`persistedVerified=true, patchStatus=Applied`). Worker log records
  `[EnsureSave] bypassed src0216 stale-prototype-model validator via
  SkipValidation=true`. Friction-report 05-13 #2.
- **`src0216 'X' propriedade inválida` is enriched with an "undeclared
  variable" hint when the SDK message points at `&Var.X` and `&Var` isn't in
  the part's Variables collection.** `WritePolicy.FindUndeclaredVariablesForSrc0216`
  cross-references the SDK error against the source text and the declared
  variables; the error response now carries `hint` + `undeclaredVariables[]`
  so the agent reaches for `genexus_add_variable` instead of "fix the field
  name on the SDT". Friction-report 05-13 #3.
- **Variables patch verify no longer false-fails on `NUMERIC(N,0)` round-trip
  drift.** `PatchService.NormalizeForPartCompare` now canonicalizes each
  Variables line: collapses internal whitespace and strips trailing `,0)`
  decimals so `&Counter : NUMERIC(4,0)` (agent-written) and `&Counter :
  NUMERIC(4)` (SDK-rendered after persist) compare equal. Without this, the
  v2.1.6 `&Counter` smoke triggered auto-rollback even though persistence had
  succeeded. Friction-report 05-13 #4.
- **`genexus_lifecycle action=build` echoes the parsed `targets` array even
  for single-object builds.** Previously `targets` was null when `Count == 1`,
  contradicting the doc contract. Single and batch builds now both surface
  the resolved list. Friction-report 05-13 #5.
- **MSBuild output streams use the console's actual encoding instead of UTF-8.**
  `BuildService` now sets `StandardOutputEncoding`/`StandardErrorEncoding` to
  `Console.OutputEncoding` (CP850/CP1252 on PT-BR Windows, UTF-8 if `chcp
  65001` is active), so `TailLines` no longer surfaces `Compila��o` /
  `n�`-style mojibake to the agent. Friction-report 05-13 #6.
- **`genexus_inspect include=["structure"]` surfaces SDT items as
  `sdtStructure`.** The block walks `SDT.Root.Items` via reflection and
  produces `{itemCount, levelCount, items:[{name, type, length, decimals,
  isCollection, isLevel, children?}]}`. Agents inspecting an SDT no longer
  see an empty `uiStructure: {}` and have to fall back to `genexus_read
  part=Structure` for basic metadata. Friction-report 05-13 #7.
- **`genexus_create_object` for SDT/Transaction announces auto-seeded
  payload via `_meta.seeded`.** Response now carries
  `{_meta:{seeded:["Item1 : VARCHAR(40)"], seededHint:"…overwrite via
  genexus_edit part=Structure…"}}` for SDT (and the equivalent Numeric key
  hint for Transaction). Agents that immediately populate the structure no
  longer get surprised by the seed item showing up in round-trip reads.
  Friction-report 05-13 #8.

## v2.1.6 — 2026-05-13

Closes the remaining open items in `docs/mcp-friction-report-2026-05-08.md`
(#2, #3, #4, #5, #6, #9a, #9b). v2.1.4 and v2.1.5 shipped the WebForm-write
composition-pointer fix; this release wraps up the rest of the friction tail.

### Fixed
- **Bare `"Erro"` write failures now surface the real SDK diagnostic.** When
  `obj.Save()` threw `"Erro"` without populating `OutputMessages`,
  `genexus_edit mode=full` returned `{"error":"Erro","line":1}` while
  `mode=patch` surfaced the actual `src0059: Esperando 'EndFor'...`. Both
  write paths now consult `SdkDiagnosticsHelper.GetDiagnostics(obj)` and
  `part.GetSdkMessages()` before falling back; the bare exception text is
  preserved under `originalError` when enrichment fires. Friction-report #2.
  (commit `a2a70cc`)
- **SDT auto-inject no longer creates wrong-typed VARCHAR(100) fallbacks.**
  When the source used `&Var.Field` and no SDT/BC name resolved, the
  injector previously fell through to the VARCHAR(100) default, poisoning
  later validation. It now skips injection so the agent gets a clean
  "undeclared variable" signal and can call `genexus_add_variable
  typeName=<SDT>` explicitly. Friction-report #3. (commit `3dadeb2`)
- **Variables DSL emits the bound SDT name instead of `GX_SDT(4)`.** The
  read-side resolver now probes `ATTCUSTOMTYPE` (where `BindVariableToSdt`
  actually persists the structural reference) when the `DataTypeString`
  fast-path is unavailable, so `&Foo : SdtFoo` surfaces correctly.
  Friction-report #4. (commit `3dadeb2`)
- **Patch post-write verification reads from a forced cache miss.**
  `VerifyPersistedSource` now drops both `_sourceCache` and
  `ObjectService._readCache` before its verify read, eliminating false
  `persistedVerified=true` reports when the verification read hit a stale
  cache entry. Friction-report #6. (commit `9d0394e`)
- **`read part=TableStructure` returns the column DSL.** The structure-alias
  dispatch in `ObjectService.ReadObjectSourceInternal` used a literal
  `GetType().Name == "Table"` string check; subclassed/proxied Table
  instances fell through to the generic `part.SerializeToXml()` path and
  returned `<Properties />`. Now tests via `obj is Table` plus a
  `TypeDescriptor.Name` check, so the existing `TableDslParser` runs.
  Friction-report #9b. (commit `482bf48`)

### Changed
- **`genexus_query` auto-index nudge** surfaces under `_meta.autoIndexed` +
  `_meta.indexStatus` (`starting` | `scanning` | `empty`), mirroring the rest
  of the tool surface. The empty-index case now also kicks off the bulk
  index instead of erroring out with `"Index empty."`. Friction-report #9a.
  (commit `085b9e0`)

- **Variables-part patch mode now persists and verifies correctly.** Live
  smoke against AcademicoHomolog1 caught two write-side bugs that the
  earlier "read side works since e10d382" assessment missed:
  (a) `SetVariablesFromText` aliased `Character → VARCHAR`, so a Variables
  patch round-tripped `&Time : CHARACTER(8)` as VARCHAR(8) and the auto-
  rollback compounded the data loss; (b) the SDK's VariablesPart collection
  inserts new vars at the FRONT, so the patch's line-by-line verify rejected
  semantically-equivalent persisted state. Removes the lossy alias and
  introduces `NormalizeForPartCompare` (set-based equality on Variables,
  strict ordering elsewhere). Friction-report #5 write side. (commit on
  top of `085b9e0`)

## v2.1.3 — 2026-05-12

Hardening release for MCP protocol compatibility, release verification, and cache/idempotency correctness.

### Changed
- Gateway, smoke scripts, docs, and Nexus IDE now use `MCP-Protocol-Version: 2025-11-25`.
- `genexus_query` result caching now uses a bounded LRU cache instead of an unbounded dictionary.
- CI now runs Gateway tests with isolated output, Worker tests when the GeneXus SDK is present, and Nexus IDE compile/tests.
- `scripts/test_all.ps1` now runs .NET tests with isolated output before the live MCP smoke.

### Fixed
- First successful write with `idempotencyKey` no longer reports `meta.idempotent=true`; only cache hits do.
- `genexus_edit(dryRun=true)` now warns when impact analysis is unavailable so `brokenRefs` is not mistaken for complete.

## v2.1.2 — 2026-05-12

Friction-fix release. Closes all 10 items from a real debug session report (`docs/issues/melhorias.md`), plus pulls in the build pipeline work that was on `main` but never tagged.

### Added
- **`genexus_search_source`** — semantic call-search across Procedure / DataProvider / WebPanel / Transaction source. Match by `callee` (qualified `DPParametros.Udp` or unqualified `Udp`) and optional positional `argMatches` (e.g. `{"0":"373"}`), or by regex `pattern`. Both can combine. Returns hits with line numbers, surrounding context, and resolved call args. Implemented via a new in-process `SourceParser` (no SDK dependency; tested directly). (#7)
- **`genexus_get_sql_for_navigation`** — emits SQL from a procedure/DP's resolved For Each navigation. One `SELECT` per Level with `:VarName` bind placeholders where the source uses `&Vars`. Warnings field reports levels where the OptimizedWhere couldn't be translated. Useful for cross-environment comparison. (#10)
- **`genexus_inspect` `include=["navigation"]`** — opt-in surfacing of resolved navigation (base table, indexes, filters) on inspect, alongside existing parts. (#5)
- **`genexus_inspect` on Attribute** — response now includes `tables: [...]` listing the physical tables that host the attribute. (#2)
- **`genexus_inspect` on DataProvider** — response now includes `returnsSDT` and `readsFromTables`. (#8)
- **`genexus_get_sql`** — always returns `subordinatedTables: [...]` for Transactions with Levels. New optional flag `includeSubordinated: true` adds `subordinatedDDL: { name: ddl }` for each subordinated table in one call. (#1)
- **Build pipeline streaming + batch builds + `ForceRebuild`** (from previously-untagged work on `main`): `genexus_lifecycle` streams MSBuild output line-by-line and exposes `Phase` / `CurrentObject` / `ErrorCount` / `WarningCount` / `LineCount` / `LastLine` / `TailLines` / `Errors[]` / `Warnings[]` / `ElapsedSeconds` via `action='status'`. `action='build'` accepts a comma- or semicolon-separated `target` list and runs all `BuildOne` tasks inside a single MSBuild + OpenKB cycle. `ForceRebuild=true` is now emitted on every `BuildOne` (mirrors the IDE's "Build With These Only"). `action='cancel'` kills a runaway build. Single-target builds surface `callersToAlsoBuild` for the next batch.
- **GeneXus version detection fallback** — when `version.txt` is absent, the gateway reads the major version from `GeneXus.exe`'s `FileVersionInfo`.
- **WebForm read** — `genexus_read part="webform"` reads the active WebForm tree.

### Fixed
- `isTruncatedByWorker` and the "MCP defaulted to 200 lines" message now appear only when the read was actually truncated. Small files come back with `isTruncatedByWorker: false` explicitly. (#9)
- Procedure / Transaction / WebPanel / DataProvider parameter types are resolved from the object's Variables part instead of returning `"Unknown"`. SDT-typed parameters surface their SDT name. (#6)
- `usedby:Attribute` resolves consumers via the inverted `CalledBy` index instead of the lexical paths that never matched attributes. Legacy lexical paths preserved for `usedby:Table` / `usedby:Procedure`. (#3)
- `genexus_query` with `typeFilter=Table` and attribute-name terms now boosts the table that contains those attributes (`+5000` instead of `+400`), instead of letting lexical similarity in unrelated table names win. (#4)
- Gateway no longer caches `genexus_lifecycle action='status'|'result'|'cancel'` or `genexus_logs` — these always reflect live worker state. Fixes the "status frozen" symptom.

## v2.1.0 — 2026-05-11

### Added
- **`genexus_whoami` MCP tool** — gateway-served (no worker boot needed) tool returning the active KB (name, path, exists, validity), GeneXus installation (path, detected version, target major match), MCP server/protocol versions, and config source. Use this as the AI's first call to confirm context.
- **Edit validation with did-you-mean** — `genexus_edit` now validates `mode` against `{xml, ops, patch, full}` and `ops[i].op` against the SemanticOpsService canon at the gateway, returning `UsageException` with Levenshtein-based suggestions (e.g., `patche` → `patch`, `set_atribute` → `set_attribute`) before the call ever reaches the worker.
- **GeneXus version check on boot** — gateway reads `version.txt`/`Version.txt`/`GeneXus.version` from `InstallationPath` and logs a warning if the detected major differs from the supported `18`.
- **`genexus-mcp whoami`** CLI command — same shape as the MCP tool, queryable from the shell.
- **`genexus-mcp uninstall`** — reverts AI client configs, deletes `%LOCALAPPDATA%\GenexusMCP\`, and removes local `config.json`. Interactive confirmation by default; `--yes` for scripts.
- **`genexus-mcp kb` multi-KB catalog** — `kb list`, `kb add --name --kb`, `kb remove --name`, `kb switch --name|--kb`. Stored in `Environment.KBs` + `Environment.ActiveKb`; legacy `Environment.KBPath` is kept in sync so the worker requires no changes.
- **`genexus-mcp init` zero-config + post-init verification** — auto-discovers GeneXus from the Windows registry (HKLM/HKCU under `Artech\GeneXus 18/17/16`) and Program Files, and the KB from the current directory; runs `doctor --mcp-smoke` at the end of `init` and reports a verification summary (use `--no-smoke` to skip in CI).
- **`genexus-mcp init --warm`** — pre-spawns the gateway after install so the first AI prompt skips the 3–8s worker cold-start.
- **Docs** — README rewritten around the new-user flow (prerequisites → 3-step quickstart → first prompts); new `TROUBLESHOOTING.md` covering the 7 most common install issues; new `docs/GETTING_STARTED.es.md` for Spanish-speaking users.

### Changed
- **`tool_definitions.json`** — clearer "use when / DON'T use when" guidance on the 4 most-ambiguous tools (`genexus_inspect`, `genexus_analyze`, `genexus_summarize`, `genexus_doc`) with cross-references to disambiguate against `genexus_read` / `genexus_explain_code`.

## v2.0.4 — 2026-05-09

### Added
- `package.json` now declares `mcpName: "io.github.lennix1337/genexus"` (verification marker for the official MCP Registry).
- `server.json` at repo root — metadata for submission to https://registry.modelcontextprotocol.io.

## v2.0.3 — 2026-05-09

### Fixed
- CI: `GxMcp.Gateway.csproj` now copies `config.sample.json` (linked as `config.json`) instead of the gitignored `config.json`. v2.0.1 and v2.0.2 release workflows failed at the build step for this reason and never reached the npm publish stage; this release ships the SEO content (keywords, README) and the v2.0.1 worker hardening together.

## v2.0.2 — 2026-05-09

### Changed
- Discoverability / SEO: `package.json` now ships a `keywords[]` array (mcp, model-context-protocol, genexus, genexus-18, claude, cursor, ai-agent, low-code, …) and an expanded description for npm search.
- README: SEO-tuned H1, added npm version/downloads badges, added explicit search-keyword list, and an opening paragraph that names the supported clients (Claude Desktop, Claude Code, Cursor) and the object kinds the agent can manipulate.

## v2.0.1 — 2026-05-08

### Fixed
- `WriteService` SDK transactions are now finalized in a `finally` block (Commit/Rollback/Dispose), preventing leaked transactions when commit-stage failures cascade into rollback-throws.
- `KbWatcherService` no longer polls `DesignModel.Objects` mid-write. Writers acquire a shared gate (`AcquireWriteGate`) and the watcher skips its tick while a save is in flight — eliminates intermittent generic "Erro" messages caused by SDK collection races.
- `PatchService` auto-rollback: when a fallback write reports success but verification mismatches, the original source is restored instead of leaving the file with the matched context deleted and the replacement missing (data loss).
- `PropertyService` now wraps `SetPropertyValue` + `EnsureSave` + `Commit` in try/finally with explicit `Rollback` on failure, and surfaces the underlying setter exception in error messages.
- `SdkDiagnosticsHelper.CreateIssueFromSdkMessage` switched from `dynamic` (RuntimeBinderException-per-miss, slow + lossy) to reflection with a per-`(Type, name)` accessor cache. Codes like `src0216` now reach the agent intact.
- SDT field access now compiles: `WriteService` binds variables to SDTs via `ATTCUSTOMTYPE`.
- `KBObject.Delete()` replaces `Objects.Remove()` (latter does not delete from the design model).

### Added
- `genexus_inspect` accepts `include=["controls"]` / `include=["events_repertoire"]` to enumerate WebForm controls and the events each control type accepts (cuts trial-and-error on event-name mistakes).
- `InferSuggestion` heuristics for `src0216`-style "invalid property" errors on unbound variables, and "not a valid event" errors on controls.

### Changed
- `config.json` is now gitignored. Use `config.sample.json` as a template and copy it locally.
- Scratch/debug artifacts under `scripts/_*` are gitignored.

## v2.0.0 — 2026-04-29

### Breaking changes
- Removed `genexus_batch_read`. Use `genexus_read` with `targets[]`.
- Removed `genexus_batch_edit`. Use `genexus_edit` with `targets[]`.
- Removed `genexus_edit` `changes` argument. Use `targets[]`.
- `meta.schemaVersion` bumped from `mcp-axi/1` → `mcp-axi/2`.
- Calls to removed tools return JSON-RPC `-32601` with `error.data.replacedBy` and `error.data.argHint` for agent self-correction. `initialize` advertises `_meta.removedTools` for proactive detection.

### Added
- `genexus_read` and `genexus_edit` accept `targets[]` plural form (mutually exclusive with singular `name`).
- `genexus_edit` `mode: ops` with semantic op catalog (`set_attribute`, `add_attribute`, `remove_attribute`, `add_rule`, `remove_rule`, `set_property`).
- `genexus_edit` `mode: patch` accepts a JSON-Patch (RFC 6902) array over canonical JSON object representation. Existing string-form `patch` (text/heuristic patch) still routes to `PatchService` for backward compatibility.
- `dryRun: true` on `genexus_edit` returns a standardized envelope `{meta:{dryRun, schemaVersion}, plan:{touchedObjects, xmlDiff, brokenRefs, warnings}}` without mutating the KB. (`brokenRefs` is currently always `[]`; the analyzer seam exists for a future enhancement.)
- `idempotencyKey` argument on write tools (`genexus_edit`, `genexus_create_object`, `genexus_refactor`, `genexus_forge`, `genexus_import_object`). Per-KB LRU cache with sliding TTL. Defaults: 15 min TTL, 1000-entry capacity. Configurable via `Server.IdempotencyTtlMinutes` and `Server.IdempotencyCacheSize`. Successful results cached; errors not cached. `dryRun` bypasses cache. Concurrent calls with the same key are coalesced.
- `_meta.idempotent: true` on cache-hit responses; `_meta.batched: true` on `targets[]` responses; `_meta.dryRun: true` on dry-run responses.
- `docs/object_json_schema.md` documents the canonical XML↔JSON mapping used by JSON-Patch mode.

## 1.1.7 - 2026-04-10

- Added protocol-first LLM bootstrap surfaces:
  - MCP resource `genexus://kb/llm-playbook`
  - MCP prompt `gx_bootstrap_llm` (now supports optional `goal`)
  - AXI CLI command `genexus-mcp llm help`
- Hardened MCP/AXI contract behavior for agent usage:
  - Stable list normalization for array payloads
  - Timeout responses with actionable `operationId` follow-up
  - Additional contract tests for resources/prompts/operation tracking
- Improved tool discovery descriptions for key tools (`query`, `list_objects`, `read`, `edit`, `lifecycle`) with more actionable guidance.
- Added automated LLM contract smoke:
  - `scripts/mcp_llm_contract_smoke.ps1`
  - CI workflow `.github/workflows/ci.yml` running CLI tests, gateway tests, and LLM smoke.
- Packaging hygiene:
  - Added `.npmignore` to exclude runtime logs/transient cache
  - Build now removes transient logs/cache from `publish` output
