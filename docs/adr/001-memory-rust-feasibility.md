# ADR-001: Memory Profile and Rust Feasibility for Gateway, Indexes, and Source Store

- **Status:** Accepted
- **Date:** 2026-11-17
- **Refs:** #1038, #1039, #1040, #1045

## Context

Issues #1038, #1039, and #1040 ask whether moving SDK-independent components of Genexus18MCP (trigram postings/query, source store, or the Gateway / shared-host broker) to Rust (`gx-engine`, `gxwire`, or a new native component) would meaningfully reduce total process-tree memory on RAM-constrained Windows workstations, without regressing STA affinity, session isolation, freshness, or lifecycle guarantees (#1007).

This ADR records repeatable measurements taken on `origin/main` (`b4aba3f8`, which already includes #1045 compact trigram/index/source-store structures) and compares them against the tuned .NET baseline and estimated Rust alternatives.

## Measured Baseline (`origin/main` `b4aba3f8`)

Workload (`scripts/bench/mem-baseline.ps1`): `GxMcp.Gateway.exe` (stdio-isolated) opening `C:\KBs\KBTeste955` and `C:\KBs\KBTeste`, running `genexus_lifecycle action=index force=true`, `genexus_list_objects`, 10 `genexus_query` + 10 `genexus_search_source`, and 10 `genexus_read` per KB.

### Process Tree Footprint

| Mode | Process / Role | Steady Private (MB) | Peak Private (MB) | Steady WS (MB) | Peak Virtual (MB) | CPU (s) |
|---|---|---:|---:|---:|---:|---:|
| `isolated` | `sqlservr.exe` (LocalDB child) | 551.52–637.96 | 551.79–637.96 | 411.78–494.38 | 45,656.87 | 2.31–3.59 |
| `isolated` | `GxMcp.Worker.exe` (`KBTeste955`, net48/x86) | 103.36–120.20 | 103.36–120.57 | 165.00–172.41 | 473.63 | 5.94–9.11 |
| `isolated` | `GxMcp.Worker.exe` (`KBTeste`, net48/x86) | 107.46–254.85 | 107.46–254.85 | 169.15–313.04 | 717.09 | 5.03–11.61 |
| `isolated` | `GxMcp.Gateway.exe` (net10/x64) | 62.52–76.53 | 73.16–76.53 | 98.91–112.60 | 268,940.52 | 2.38–2.55 |
| `isolated` | `conhost.exe` (x3) + transient `csc.exe` | 5.8–13.6 | 5.8–13.6 | 27.7–47.8 | — | 0.14 |
| `isolated` | **Total Tree** | **857.36–1,072.30** | **857.61–1,082.94** | **885.31–1,119.14** | — | — |
| `shared-host` | `sqlservr.exe` (LocalDB child) | 588.58 | 588.58 | 444.75 | 45,287.80 | 3.80 |
| `shared-host` | `Worker` brokers (`--shared-host`, x2) | 21.77 + 22.17 | 21.77 + 22.17 | 33.21 + 33.86 | 216.49 | 0.61 |
| `shared-host` | `Worker` SDK children (x2) | 257.99 + 302.39 | 257.99 + 302.39 | 310.61 + 329.82 | 718.57 | 17.53 |
| `shared-host` | `GxMcp.Gateway.exe` (net10/x64) | 92.29 | 92.29 | 129.06 | 268,942.94 | 2.69 |
| `shared-host` | **Total Tree** | **1,296.83** | **1,296.83** | **1,336.39** | — | — |

### Retained Memory Attribution

- **Gateway (`dotnet-gcdump` on live `GxMcp.Gateway.exe`):** 18.38 MB managed GC heap (277,637 objects) out of 75.93 MB Private Bytes (~57.5 MB is .NET 10 runtime, ASP.NET Core/Kestrel, JIT, thread stacks, and pipe/I/O buffers). Note: `agy/mem-gateway` separately targets Gateway GC/cache/pipe footprint.
- **Worker control plane (`--shared-host` broker before SDK load):** ~21.8–22.2 MB Private Bytes (net48/x86 CLR + named-pipe/JSON-RPC control plane).
- **SDK-loaded state + LocalDB:** ~85–95 MB Private per Worker (`Artech.*` assemblies, `KnowledgeBase`, `DesignModel`, COM/ADO.NET) + **551–638 MB Private** in `sqlservr.exe` (LocalDB). Together these account for **~85–90%** of the entire process tree.
- **MCP-owned indexes/caches (`MemoryAttributionTests`, 2,000 objects, 500 with source):**
  - `SearchIndex` entries: `1,462.4 KB` baseline → `1,228.1 KB` (-16.0%) after lazy `IndexEntry` lists.
  - Derived secondary indexes + `SourceTokenIndex`: `818.3 KB`.
  - `SourceStoreService` (compact `ulong` trigrams + `PostingList` from #1045): `1,434.2 KB`.
  - `CallerGraphService`: `5.6 KB`.
  - Serialization peak (`ToJson()` string → `WriteToStream()`): `4,868.7 KB` intermediate string → `0 KB` intermediate string.

## Rust Feasibility Assessment

Threshold for prototyping: a candidate must yield **>20% savings of the whole process tree** (~171–214 MB on the 857–1,072 MB tree) after accounting for IPC/FFI copies, UTF-16 ↔ UTF-8 conversions, extra process overhead, startup, and Windows distribution, and must fit existing `gx-engine` / `gxwire` seams.

| Component | MB Now | Movable? | Rust Estimate (incl. copies & overhead) | Maintenance Cost | Decision |
|---|---|---|---|---|---|
| Trigram postings / query (`SourceStoreService._trigramIndex`) | ~1.4–15 MB managed (already compacted to `ulong` + `int[]` in #1045) | Yes (derived index) | Out-of-proc (`gxwire.exe`): +5–10 MB sidecar + pipe/JSON copies → **0 to -5 MB net** (<1% tree). In-proc FFI (`i686` DLL): saves ~0.5–8 MB (<1% tree) minus UTF-16→UTF-8 marshaling. | High (`i686` + `x86_64` Windows build, P/Invoke or IPC sync, `#344` reclamation split) | **Keep .NET** |
| Source store (`SourceStoreService` bodies + `FullSource` cache) | ~1.5–25 MB steady (disk-backed `.gz`; `FullSource` bounded) | Partial (SDK `ReadPartSourceRaw` still in net48 Worker) | SDK still materializes UTF-16 `string` in .NET; copying UTF-16→UTF-8 across IPC/FFI adds 2–3 transient buffers. Net tree saving **<1–3%**. | High (freshness, `#338`/`#363` eviction, and `IndexCacheService` invalidation split) | **Keep .NET** |
| Gateway & shared-host broker (`GxMcp.Gateway` + `SharedWorkerHost`) | Gateway: 62.5–77.8 MB (18.4 MB heap); Broker: 21.8–22.2 MB/KB | Yes (SDK-independent control plane) | Rust Gateway (~10–18 MB) saves ~58–66 MB (**~6.8–7.7%** of 1-client tree; ~12% for 2 clients). Rust broker saves ~12–15 MB/KB (<2% tree). Still keeps UTF-8 JSON framing and full Worker+LocalDB tree. | Very High (60+ MCP tool routers, `EngineCapabilityRegistry`, `#854` sharing, `#1007` lifecycle, Windows signing) | **Keep .NET** |

## Decision

1. **No Rust prototype is justified for memory reduction:** None of the three candidates approaches the 20% whole-tree threshold. Roughly 85–90% of process-tree memory lives in `sqlservr.exe` (LocalDB, 551–638 MB) and the GeneXus SDK inside `GxMcp.Worker.exe` (~85–95 MB per open KB), neither of which can be replaced by moving MCP-owned caches or the Gateway to Rust.
2. **Keep .NET for trigram postings, source store, and Gateway/shared-host broker,** continuing targeted .NET optimizations (`#1045`, `WriteToStream`, lazy `IndexEntry` lists, and `agy/mem-gateway`).
3. **Continue expanding `gx-engine` only along its existing validated boundary** (native UDM reads and blocking builds that bypass opening the SDK/LocalDB altogether for supported operations, which is the only architectural lever that avoids the ~650 MB SDK+LocalDB floor).
