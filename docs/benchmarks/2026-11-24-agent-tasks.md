# Agent tasks benchmark: Genexus18MCP vs plain approaches (2026-11-24)

Read-only benchmark comparing **Genexus18MCP** (isolated stdio Gateway + Worker) against two plain non-MCP approaches on the disposable Knowledge Base:
- **`mcp`**: Genexus18MCP tools (`genexus_query`, `genexus_read`, `genexus_impact`, `genexus_search_source`) over one warm MCP stdio session (`EmitStructuredContent: false`, `TerseResponses: true`).
- **`plain-sql`**: Direct read-only `sqlcmd` queries against the KB LocalDB (`GX_KB_KBTeste`, `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED`) plus local GZip/`<TokenDataList>` decoding of `EntityVersionData` blobs.
- **`plain-fs`**: Direct filesystem inspection (`rg` / `fs.readFileSync`) over the KB folder on disk (`NetModel\Web\*.cs` generated artifacts).

## Environment and reproducibility

| Parameter | Value |
|---|---|
| Date | 2026-11-24 |
| Disposable KB | `C:\KBs\KBTeste` (read-only) |
| GeneXus SDK | 18.0.10.184260 |
| Gateway version / commit | 4.7.3 (513e72d5) |
| Tool profile | `standard` |
| Iterations (after warmup) | 5 (warmup: 1) |
| Targets | object `WWTrn353` (Events), refs `Transaction Trn353`, text `Trn353` |

Reproduce:

```powershell
node scripts/bench/agent-tasks-bench.js --kb "C:\KBs\KBTeste" --iterations 5 --warmup 1
```

## Results (median per task, warm session)

Response bytes measure the UTF-8 bytes returned to the agent context (`content[*].text` for MCP; command/decoded output for plain approaches). Tokens are estimated at 4 bytes per token.

| Task | Approach | Calls | Response bytes | Est. tokens | 1st call (ms) | Warm p50 (ms) | Warm p95 (ms) | Fidelity / Scope |
|---|---|---:|---:|---:|---:|---:|---:|---|
| Find object (`WWTrn353`) | `mcp` | 1 | 450 | ~113 | 351.87 | 0.50 | 0.76 | Exact KB object metadata (type, GUID, path) |
| Find object (`WWTrn353`) | `plain-sql` | 1 | 24 | ~6 | 38.17 | 28.76 | 29.13 | UDM table row (requires knowing ModelId + namespace filter) |
| Find object (`WWTrn353`) | `plain-fs` | 1 | 223 | ~56 | 29.01 | 26.88 | 29.16 | Generated disk files only (misses ungenerated KB objects) |
| Read source (`WWTrn353` Events) | `mcp` | 1 | 1,578 | ~395 | 2348.92 | 0.72 | 1.49 | Native GeneXus Events source |
| Read source (`WWTrn353` Events) | `plain-sql` | 2 | 890 | ~223 | 71.95 | 58.97 | 62.11 | Decoded GeneXus Events source (2 queries + gzip/XML decoder) (SQL raw transfer: 1,797 B) |
| Read source (`WWTrn353` Events) | `plain-fs` | 1 | 81,442 | ~20,361 | 0.13 | 0.03 | 0.04 | Generated C# only (not GeneXus source; absent if ungenerated) |
| List references (`Transaction Trn353`) | `mcp` | 1 | 974 | ~244 | 73.53 | 0.54 | 0.63 | Semantic callers + callees graph (depth 1) |
| List references (`Transaction Trn353`) | `plain-sql` | 1 | 497 | ~124 | 50.89 | 29.76 | 30.47 | Raw ModelCrossReference rows (includes internal parts/attributes) |
| List references (`Transaction Trn353`) | `plain-fs` | 1 | 63,947 | ~15,987 | 20.68 | 18.67 | 19.19 | Text grep in generated *.cs only (noisy, misses metadata refs) |
| Search by text (`Trn353`) | `mcp` | 1 | 7,241 | ~1,810 | 74.08 | 1.92 | 2.03 | Indexed GeneXus source/events/rules matches with line context |
| Search by text (`Trn353`) | `plain-sql` | 1 | 1,800 | ~450 | 196.64 | 58.74 | 61.37 | Full-table UDM blob dump + gzip/XML decode + scan (SQL LIKE cannot read blobs) (SQL raw transfer: 471,887 B) |
| Search by text (`Trn353`) | `plain-fs` | 1 | 63,947 | ~15,987 | 21.73 | 16.88 | 17.99 | ripgrep over generated *.cs only (misses GeneXus syntax and ungenerated objects) |

## Session startup overhead (measured once per session, outside per-task medians)

| Setup step | Latency (ms) | Notes |
|---|---:|---|
| MCP Gateway spawn + `initialize` + `genexus_kb open` + index Ready | 3211.94 | Paid once per session before steady-state calls |
| `plain-sql` read `knowledgebase.connection` + initial `sqlcmd` probe | 540.43 | Paid once per session; each subsequent `sqlcmd` spawns a new process |

## What this does not measure

- Fixed session `tools/list` schema overhead (measured separately in the 2026-11-23 tools-list-size benchmark).
- End-to-end LLM reasoning turns, tool-selection accuracy, or provider tokenizers (tokens here are estimated as `bytes / 4`).
- Write/edit, build, navigation, or pattern-instance workflows; every operation in this benchmark is strictly read-only.
- Large production KBs (40,000+ objects); `C:\KBs\KBTeste` is a small disposable GeneXus 18 KB, so full-table blob scans in `plain-sql` scale much worse on real production KBs.
- Objects without generated artifacts on disk: `plain-fs` only sees generated C#/JS under `NetModel\Web` and misses ungenerated objects (e.g. `PCalculaDesconto`) and GeneXus-only constructs such as `For Each` (compiled away into C# cursor code).
