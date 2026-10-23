# gx-engine — independent compiler & build engine

**Goal: fully replace the GeneXus SDK and toolchain with an independent safe Rust implementation.** [Plan 130](../../plans/130-direct-udm-kb-engine.md) covers direct native KB/UDM storage access, full object modeling, native C# (.NET) code generation, and parallel Rebuild All. No capability is permanently assigned to the proprietary SDK or 32-bit STA worker.

**Experimental capability areas (full SDK parity is not established):**
- **Native UDM KB Access:** Reads physical GeneXus UDM SQL databases (`knowledgebase.connection`, SQL Server, LocalDB) without spawning `GxMcp.Worker.exe` or COM STA processes. Decodes 11-byte envelopes, decompresses RFC 1952 GZip blobs and reconstructs source code from `<TokenDataList>`. Live writer persistence is not implemented: native writes are refused, not reported as saved; the SDK remains the supported editing path.
- **Object modeling:** Implements admitted forms of Procedures, Transactions, Business Components, WebPanels, Reports, SDTs, Events, Rules and Structure. Coverage differs by pipeline; unsupported native forms still produce refusals/errors rather than approximate builds.
- **Native C# (.NET) Generator:** Emits standard GeneXus C# classes (`GXProcedure`, `GXWebObjectStub`, `GXDataComponent`, `GxUserType`) and Visual Studio solution files (`.sln`, `Directory.build.props`, `.csproj`) targeting `net8.0`/`net9.0`, fully diffable against IDE output and verified by Roslyn compilation.
- **Parallel rebuild pipeline:** Scoped-thread parallel specification and C# emission followed by compilation. Available via CLI: `gx-engine rebuild-all --kb <path> --output <dir>`. A successful supported closure does not establish full-KB coverage or a general build-time guarantee.

## Build coverage and routing

The goal remains independent full SDK replacement; **full SDK replacement is not established**.
On the disposable `KBTeste` KB, `rebuild-all` generates and compiles every targeted object
(**346/346, 0 skipped, 0 refused**), and its output matches what the GeneXus 18 SDK writes for
the same KB byte for byte: **328/328 C# files**, 24/24 web scripts, `gxcfg.js`,
`messages.eng.txt/.js`, `log.config`, `log.console.config` (and their `bin/` copies) and
`CloudServices.config`. That is one oracle KB, not every KB: forms the engine has no SDK
evidence for are refused by name.
Use the SDK for workflows outside validated engine coverage; preserve unsupported-object
refusals: planning skips yield `Partial`, while checked-emission errors yield `Failed`; neither is a successful whole-KB build. Event-bearing WebPanels are refused by the rebuild planner. The separate library-only `WebPanelClass` emitter is not used by rebuild and has no event-parity evidence.

Gateway routing defaults to `Engine.Backend=gxengine`: admitted blocking builds/rebuilds
and field-limited type-only queries use gx-engine, including directed `build` requests with `mode=compile_check`,
explicit `callers=false`, and transitive callees. Those requests build only the
target closure and report `engine.scope=target-closure`; they do not certify the
whole KB. Reads, edits, async builds and unsupported request shapes stay on SDK.
`auto` narrows routing to admitted builds; explicit `sdk` disables engine routing. Capability and argument gates remain mandatory. See
[routing](../../docs/engine_backend.md).

## SQL-authenticated Knowledge Bases

For `IntegratedSecurity=False`, set `SQLCMDUSER` and `SQLCMDPASSWORD` in the
parent process environment using your client's protected secret mechanism.
These override corresponding connection-file fields; both must be nonempty.
The SDK's private credentials are not read. Missing credentials fail before
sqlcmd starts (no interactive password prompt); passwords travel only in the
child environment, never argv. Integrated-security connections still use `-E`
and ignore SQLCMD credential variables; no silent Windows-auth retry is made.

`--diagnostics=json rebuild-all ...` returns exit **5** and one stderr diagnostic
with `category=sql_credentials_unavailable`, empty stdout and no build output
when this credential preflight fails. The Gateway can safely use SDK fallback
for that exact refusal; generic failures/timeouts after start cannot replay builds.
See [SQL authentication and auto routing](../../docs/engine_backend.md#sql-authentication-issue-635).

## SDK-form output

What a whole-KB run writes, and from what:

| Output | Source | Matched against |
| :--- | :--- | :--- |
| Procedures, DataProviders, Transactions (custom, default, key-only forms), Work With family, prompts `Gx00N0`, NotAuthorized, masters, plain WebPanels | KB parts read directly from UDM | KBTeste SDK output, 328/328 `.cs` identical |
| Web scripts (`*.js`) and `gxcfg.js` | KB parts; `NetModel/Web/GXCFG.WEB` | 24/24 `.js` identical |
| SDT classes (`type_Sdt*.cs`) | SDT structures; only SDTs a generated object reaches, and never a GeneXus library SDT the runtime assemblies (`GxClasses*.dll`) already define | 13 KBTeste SDK SDTs identical |
| Enumerated domain classes (`gxdomain*.cs`) | Domain enum rows | `general/gxdomainpage.cs` identical |
| `messages.eng.txt` / `.js` | KB `TranslationMessage` entities + install `Library/GAM/Platforms/NetCoreSql/messages.eng.resources` (`GX18_INSTALL_DIR`, default `C:/Program Files (x86)/GeneXus/GeneXus18`) | identical; English-only KBs only |
| `log.config`, `log.console.config` (+ `bin/`), `CloudServices.config` | Stored model properties; unstored ones take the .NET generator defaults | identical; other log targets/providers refused |

Reachability follows the SDK: a Procedure stored as not main, or an SDT, is generated only when a
generated object reaches it (SDK `spc0217`). Not written: the Developer Menu (`DeveloperMenu.xml`
emitter exists, but which objects the SDK lists is not established) and the SDK's `.bld`/`GX*PRO41`
bookkeeping files.

## Measured local SDK comparison

**Whole-KB Rebuild All** (`KBTeste`, 346 classes), paired and alternating on one machine,
2026-10-21, with the database held open by an idle connection as an opened KB holds it
(`bench-s42.py`). The SDK side runs specify + generate only (MSBuild `SpecifyAllOnly` +
`GenerateOnly`); its own compile is blocked by GeneXus Cloud here, so the engine side, which
does compile, is measured against less work than it does:

| | Runs | Median | Range | Ratio |
| :--- | :---: | :---: | :---: | :---: |
| GeneXus SDK (specify + generate, no compile) | 3 | 42.2 s | 32.3 – 46.2 s | |
| gx-engine, nothing changed (compile proven up to date) | 9 | 289 ms | 271 – 306 ms | ~146× |
| gx-engine, forced full compile of all 346 classes | 9 | 816 ms | 739 – 989 ms | ~51.7× |

The ratios are medians against medians; the fastest SDK run against the slowest forced-compile run
is ~33×. Output parity of the same run: 328/328 C# files identical to the SDK, 20 extra, 0
missing; the Developer Menu is not written, so full SDK parity is unverified and this is not a
full-replacement claim.

Where the time went: the catalog read is ~150 ms (from ~3.1 s): every KB read of a run shares a
persistent `sqlcmd` session, only the entity types the engine serves are read (2 MB of part hex
instead of 62 MB), on four parallel sessions decoded with a lookup table, while the build
environment is read alongside. A web class that differs from the file on disk only by its
generation stamp is not rewritten, so an unchanged KB leaves every compiler input unchanged and
the compile is skipped. A real compile replays the kept compiler command directly (`csc /shared`,
`/skipanalyzers+` when no analyzer diagnostic can fail the build; same DLL). The first build into a
new output folder runs `dotnet build` (~1.9 s); a cold compiler server was measured once at
~22.8 s. An earlier binary measured generation alone (`--skip-compile`) at 0.61 – 0.68 s against
SDK 33.4 – 39.9 s (~58×).

A five-pair **warm, selected-object** local MSBuild comparison measured SDK median
14.756s versus gx-engine 0.230s (**64.19x**), including engine database setup.
Both paths regenerated/recompiled C# and produced matching direct CLI output and
core APIs. The input is the synthetic `DCM37961700` subroutine fixture; this is
**not** a full-application, cold-build, or full-SDK replacement claim. Both paths
used AUTO_CLOSE=OFF during the benchmark; the original ON setting was restored.
The first engine/MSBuild bootstrap took 1.254s and is reported separately. The
packaged binary was smoke-tested, not benchmarked; its hash differs from the timed
binary. See
[Plan 131](../../plans/131-sdk-performance-benchmark.md) for commands, all
samples, output checks, cache conditions, failures and outstanding gates.

## Local use

Requirements: Cargo/rustc (edition 2024 support), plus the native linker required by your Rust toolchain. No global install or client configuration changes.

```powershell
cargo test --manifest-path src/gx-engine/Cargo.toml
cargo run --manifest-path src/gx-engine/Cargo.toml -- check src/gx-engine/fixtures/demo.json
cargo run --manifest-path src/gx-engine/Cargo.toml -- emit src/gx-engine/fixtures/demo.json
cargo run --manifest-path src/gx-engine/Cargo.toml -- explain previous.json next.json
cargo run --manifest-path src/gx-engine/Cargo.toml -- run src/gx-engine/fixtures/demo.json
cargo run --manifest-path src/gx-engine/Cargo.toml -- --version
```

On Windows two tests of `tests/csc_direct.rs` depend on the host. The symlink test needs Developer Mode or the symlink privilege and skips locally on `ERROR_PRIVILEGE_NOT_HELD`. The relative-project-path test builds under the temp folder, or under the nearest parent of the checkout that has no ambient MSBuild or analyzer file when the temp folder is on another drive (the checkout itself sits under `Directory.Build.props`, which the direct path refuses), and skips locally when none shares a root with the crate folder. A skip is a message on stderr, not an ignored test; when `CI` is set both fail instead of skipping, so the coverage cannot quietly disappear from the pipeline.

`check` validates admitted source and emits nothing executable. `emit` prints generated Rust; `build` prints the native executable path. The compiler version probe has a ten-second deadline; native compilation has a 120-second deadline. `run` then executes **authored code**, inheriting the console and enforcing a separate ten-second execution deadline. Compiler timeout cleanup targets the owned process tree on Windows; descendant cleanup on other platforms is not guaranteed. Timeouts are not memory/disk quotas or a sandbox. Only use trusted projects, especially as the language grows.

## Host integration (experimental)

`--version` (or `-V`) prints `gx-engine <package version>` and exits without
reading a project. That line is the Gateway's engine-backend probe contract: the
MCP Gateway can point at a `gx-engine` executable, verify it, and report it from
`genexus_doctor`. The default `gxengine` policy enables admitted builds,
type-only queries selecting only `name`/`type` fields; `auto` enables only builds
and `sdk` none. All reads, `usedby:` and ranked fuzzy searches remain on SDK
fallback until native response/identity/freshness/token parity is verified. Engine routing requires a registered
consumer and its bounded capability evidence; unsupported requests follow the configured SDK fallback or
fail closed. A started build with an unknown outcome requires reconciliation,
not automatic SDK replay. See
[docs/engine_backend.md](../../docs/engine_backend.md) for the switch, the
current capability table and the rules for adding a capability.

`kb-read`, `kb-query`, `kb-write` and `rebuild-all` accept `--design-model <id>
--kb-version <id>`: the Design model and KB version the GeneXus session has
active. Without them only a Knowledge Base with a single Design model is served.
A read serves the named version's Design model once its stored `KBVersion`
matches; a build requires a single Design model (trunk model 1) with every
environment under it. Supporting build facts are not yet version-pinned, so a KB
with multiple Design models is refused even with the trunk active. Anything else
is refused before any object read or output write with exit code 4 and diagnostic
category `version_scope` (see `src/udm/version_scope.rs`). Model `KBVersion` metadata
is parsed as XML; malformed, repeated and non-scalar values are not trunk defaults.

Legacy catalog commands (`kb-check`, `kb-emit`, `kb-build`, `kb-run`) also check
native directory/connection-file inputs and bind the sole Design model's actual
id. They cannot name an active version, so multi-model KBs are refused through
their existing input-error contract. Portable JSON catalogs are unaffected.

Standalone `kb-read` projects the evidenced design-class `StyleClass:<type-guid>Name`
wrapper to `StyleClass:Name` in tokenized source responses, preserving literals
and comments. Raw reader/codegen inputs are unchanged. This displayed source is
not round-trip storage text: do not feed it into the in-memory writer. Native
live writes remain refused. Missing Source parts stay missing rather than aliasing
Rules; native reads remain outside MCP admission while identity, token and
freshness parity are unresolved.

## Machine-readable CLI diagnostics

The leading `--diagnostics=json` option emits JSON diagnostics on stderr while
preserving stdout; without it, errors remain human-readable. Diagnostics identify
operation-level phases. `Build` intentionally treats code generation and rustc as one
operation/phase. Source locations are exposed only through the direct parser
diagnostic API, not the CLI JSON envelope. These phases and diagnostics are gx-engine
contracts, not GeneXus SDK error-code equivalence.

Builds default to `.gx-engine/` beside the portable project. An optional output directory follows the project argument for `build`/`run`. Every successful build has a separate generation directory containing the portable input, generated Rust, compiler/version manifest and executable. These may contain private authored source: do not upload them unintentionally. Failed compiler generations are cleaned up and never reported as successful builds. Older successful generations are preserved. There is deliberately **no artifact cache yet**: a correct toolchain/linker/environment input model must precede executable reuse. Remove your disposable output directory when no longer needed.

## Independent portable KB catalog

The `catalog` library now provides stable object identities, source parts and typed
references, bounded deterministic JSON, revision-checked transactional edits,
reference-aware deletion, dependency ordering/cycle reporting, and atomic snapshot
save/load. Its format is ours—not a decoder or writer for native GeneXus KB data.
Disk snapshot replacement is not a multi-process compare-and-swap or a durability
guarantee across power loss. Catalog naming is currently a conservative globally
unique namespace, not full native module/type naming compatibility.

The compiler consumes the entry's reachable Procedure/Calls/Uses graph using stable
object IDs. Explicit `Uses` references to `StructuredDataType` objects admit one
portable `Fields` JSON map of admitted scalar, nested Record and Collection declarations;
other SDT parts are unsupported.
A Procedure using Record/Collection variables or return types, and a schema with nested
fields, must declare the corresponding direct `Uses`; typed Record return types are
covered by the same boundary. Unknown or undeclared references and schema cycles fail
closed. Reachable catalog compilation is bounded to 1,024 Procedures, 128 schemas and
1,152 reachable objects; boundary tests accept 1,024 Procedures and reject 1,025. The
latest `kb_pipeline` increment passed 12/12: transitive `Probe` → `Child` → `Grandchild`
plus `Row` emitted `7`, `8`, `9` without changing the catalog, and a missing
`Child` → `Grandchild` `Uses` edge was rejected. Source calls also require their own
direct `Calls` reference even if the target is transitively reachable; the regression
case is `portable_catalog_requires_direct_calls_and_preserves_recursive_edges`.
These are portable pipeline tests, not native KB or SDK parity evidence. Those schemas
feed the bounded record compiler below. This is our authored catalog format,
not a reader for native SDK Structure parts. `Source`, `Variables` (typed declaration JSON), and either `Signature`
(JSON `params`/`return_type`) or `Rules` are the admitted portable parts. `Rules`
accepts only a standalone `Parm(...)` declaration with variable arguments and
`in`/`out`/`inout` modes (default `inout`); mixed Rules/Signature and other rules
are rejected rather than guessed. `catalog-parm-demo.json` demonstrates an
SDK-free call returning its final `out` parameter. Unsupported referenced
object kinds, reference kinds and parts fail explicitly; Events and other native
Rules do not silently disappear. Unused objects are not compiled. The bridge still enforces
the compiler's project limits and does not infer or repair omitted call references.

```powershell
cargo run --manifest-path src/gx-engine/Cargo.toml -- kb-check src/gx-engine/fixtures/catalog-demo.json main-id
cargo run --manifest-path src/gx-engine/Cargo.toml -- kb-run src/gx-engine/fixtures/catalog-demo.json main-id
```

`kb-emit` and `kb-build` are also available. The runnable fixture loads a saved
catalog, validates references/typed source, compiles two Procedures and prints `42`.
CLI catalog compilation has no implicit two-digit-year policy; library callers can
supply the explicitly measured window. This is real portable KB-to-executable
integration, not native KB/SDK interoperability or completed SDK replacement.

## Read-only SDK XML compilation projection

`xml-check`, `xml-emit`, `xml-build` and `xml-run` consume an authored SDK
`ExportFile` XML document and an unqualified entry Procedure name. For example:

```powershell
cargo run --manifest-path src/gx-engine/Cargo.toml -- xml-run C:/private/authored.xml Main
```

This independently implemented reader projects supported Procedure Source/Variables/
standalone Parm Rules and scalar, recursively nested Record and Collection SDT
Structure parts -- bounded by the XML depth limit -- into the portable catalog.
It retains exported object identities and derives Calls/Uses from the parsed source
and declarations. All referenced Procedures/SDTs must be present. SDK metadata is
not a fallback execution path. A fresh disposable-KB SDK export ran unchanged
through the release Rust CLI, matching native `alias / alias / alias / clone` output;
the input hash was unchanged. The exchange suite passed 16/16; projection tests use
synthetic schema IDs/references and cover the `RowsItem` alias and the depth-2
full-path item alias `Deep.Child.NestedCollectionItem`, the only alias beyond the
root level that native readback preserved (deeper collections and non-root composite
names stay fail-closed). Actual native hierarchy exports are available:
`nested-record-export-1792080479819133800/authored.xml` contains `RSN03944700.Child`
and a `Rows` collection, `custom-item-export-1792080825587526300/authored.xml`
records `idCollectionItemName=Entry`, and `deep-sdt-native-export-1792102090401/
xpz-contents/exportfile-sanitized.xml` holds the three-level `NativeDeepSdt85299100`
SDT with its depth-2 collection. The nested-record and deep exports run through the
Rust XML flow with exact output (the deep export with an injected companion
Procedure whose output matches the natively measured values); the custom-item export
has no Procedure entry to run. The raw SDK exports remain outside Git.

XML is capped at 8 MiB, 50,000 nodes and depth 128. Entity resolution/DTD content
are disabled. Duplicate identities/declarations/properties, unsupported object
kinds, qualified/module names and unsupported authored parts fail closed. A native
Rust XML-to-build/run differential validation against those native nested/collection
exports remains unverified. `roxmltree` supplies the XML parser; no handwritten
entity decoder is used.
The reader is a **compilation projection**, not a lossless metadata/Help roundtrip,
XPZ ZIP extractor, native KB database decoder, or general SDK importer. Nonempty
unsupported authored metadata is rejected; ordinary export provenance is omitted.
Calendar formatting/timezone/fractional-second coverage remains limited to the
existing typed runtime. Raw exchanges can contain local usernames and KB paths;
keep them and generated artifacts private.

## Portable project format

```json
{
  "format_version": 1,
  "entry": "Main",
  "procedures": [
    {"name": "Main", "params": [], "source": "Print(21 * 2)\nReturn 0"}
  ]
}
```

The JSON format is ours, not an undocumented GeneXus KB representation. Unknown fields/format versions and malformed identities are rejected. Procedure bodies use a deliberately narrow GeneXus-style syntax; their parameters are specified in JSON, or derived from the portable catalog's bounded standalone `Parm(...)` Rules subset. General native Rules are not compiled.

The admitted path uses signed 64-bit integers, numeric comparisons (0/1), nonzero conditions, `Not`/`And`/`Or` (comparisons bind before `Not`, then `And`, then `Or`; `And`/`Or` short-circuit left to right), assignments, numeric calls, `Print`, `Return`, `If`/`Else`/`EndIf`, and `Do While`/`EndDo`. Keywords and identities are case-insensitive. Arithmetic is checked: overflow/division errors fail rather than wrap. These numeric choices are **not a claim of GeneXus Numeric/Decimal/null compatibility**. Variables must be definitely assigned before reads; assignments inside a possibly skipped loop do not initialize them afterward. Helpers may print and preserve source evaluation order, but calling the entry procedure as a function is currently rejected. Fall-through returns numeric zero; procedure return values are computation results, not OS process exit codes. Generated programs deliberately stop at 100,000 shared ticks (procedure entries and loop iterations) or a call depth of 128; even otherwise valid long-running programs can exceed those prototype limits.

The portable format-2 compiler admits bounded nested Record/Collection schemas and the specific operations described below. The SDK XML projection also maps nested/collection SDT structures in synthetic tests. Native nested/custom-item/deep hierarchy exports are captured; the nested-record and deep exports parse and run through the Rust XML flow with exact output, while the custom-item export has no Procedure entry to run. Other unsupported features include general string libraries/collations, timezone/null semantics, transaction schemas, `For each`, SQL/navigation planning, general Rules/Events beyond standalone `Parm`, forms, native KB persistence, SDK providers, lossless import/export compatibility, runtime libraries, parallel/per-query semantic scheduling and production MCP routing. Unsupported syntax is rejected, never delegated silently or compiled approximately.

## Bounded records and collections (format version 2)

Format 2 accepts `record_types` schemas with typed fields, and variables declared
as `{ "kind": "record", "schema": "Probe" }` or
`{ "kind": "collection", "schema": "Row" }`. Schemas can contain scalar,
nested Record, and Collection fields. Initialize records with `new()`, read/write
members (for example `&First.Child.Amount`), alias values with assignment, and use
`Clone()` for the measured shallow-copy behavior. Collections admit `Count`,
`Add(record-variable)`, `Item(numeric-expression)`, and `Clone()`. `Item` accepts
numeric expressions, variables, and helper results; the evaluated value converts to
an integer by truncating fractional digits toward zero, matching native KBTeste
(1.9 -> item 1, 2.7 -> item 2, 1.5 -> item 1). Indices are one-based and must be in
range after truncation; nonpositive or out-of-range values produce controlled runtime
errors, while native instead terminates with an unhandled `NullReferenceException`
(observed exit 1 and stack trace) -- the controlled diagnostic is a deliberate safety
divergence. The focused `collection_indices` tests passed 3/3; a For-driven
traversal/member update printed `7`, `9`, `11` in the portable pipeline. The
dynamic-index native differential was run against disposable KBTeste: variable,
expression, and For-driven indices match (7/9 and 9/7), truncation matched 1.9/2.7/0.9
and -0.5, and invalid indices reproduced native acceptance (build exit 0) followed by
the runtime failure above. `typed-records.json`, `typed-record-nested.json`, and
`typed-record-collection.json` exercise the portable compiler path; generated
programs use the dependency-free inlined runtime, not `structured.rs`.

Schemas are case-insensitive, unique, bounded to 128 schemas and 1,024 fields
each, and checked at load time; unknown references and recursive schemas are
rejected. Scalar field assignments reuse the typed runtime rules, including
numeric storage bounds and Character/Varchar text behavior. Native comparisons in
the authorized disposable KBTeste measured scalar defaults/aliasing/Clone, nested
record Clone, and collection Clone (evidence details and exact outputs are in the
implementation progress ledger). These are narrow GeneXus 18 observations, not a
claim of general SDK compatibility.

The measured reference behavior is specific: record assignment aliases its field
map; `Record.Clone()` separates the outer map but keeps nested Record/Collection
references shared; `Collection.Clone()` separates list membership while retaining
aliases to the contained records. Thus mutations to nested children/items remain
visible through both references, while later Add operations on a cloned collection
change only that list. The portable runtime and regression fixtures encode these
rules. A fresh integrated Rust-vs-native run for the current nested/collection
fixtures remains a validation gate; do not infer it from the recorded native run
or from the presence of tests.

Record variables must be initialized before member access. Void helpers accept
same-schema record variables with `in`, `out`, and `inout` modes; native probes
also covered zero-filled `out`, alias visibility, rebinding with `new()`, and
copy-back behavior. Record/collection expression results and returns, record
parameters on value-returning helpers, rvalue record arguments, comparisons,
non-literal Item indices, inline `Add(new())`, unsupported members, and other
unimplemented operations remain rejected. Schema changes invalidate incremental
compilation, including affected callers. The separate `structured.rs` owned-value
API does not establish GeneXus compatibility.

## Bounded control flow and console messages

`Do Case` / `Case condition` / optional `Otherwise` / `EndCase` selects the first
matching branch and skips later conditions. Lowered case chains and their nested
bodies share the 128-level nesting limit; excessive chains fail before build
artifacts are created. The native-backed fixture `typed-do-case.json` covers
first-match, no-match, nesting and skipped helper side effects.

Format 2 also admits Numeric `For` / `EndFor` loops. `Step` is restricted to a
nonzero integer literal. The owner-reported `for_loop` suite passed 8/8; its
 definite-assignment fixture initializes `J=0`. The runnable `fixtures/typed-for.json`
demonstrates the ascending subset. Native observations and the bounded implementation
match for inclusive ascending/descending literals, positive `Step 2` (1, 3, 5; then 7),
negative `Step -1` (3, 2, 1; then 0), direction mismatch (zero iterations), one-time
start evaluation, and `To` reevaluation on every condition including the final false
check; see the [native For evidence ledger](../../plans/129-native-for-progress.md).
Native accepts `Step 0` in a bounded immediate-Return probe, but gx-engine deliberately
rejects it as a safety divergence; the probe does not characterize subsequent native
zero-step iterations. Native also rejects variable/helper Step expressions, matching
the engine's literal-only rule. This bounded subset is not broad `For` parity.

Format 2 also accepts bare `Return` (including `Return;`) to exit a void Procedure,
copying current `out`/`inout` values back. A value-returning helper still requires a
value. `Msg(text, status)` writes a text message to the CLI console, as observed in
the native probes. Other message modes, numeric implicit conversion, and interactive
message/UI semantics are not admitted. This is a bounded CLI subset, not full SDK parity.

## Typed Boolean/Numeric programs (format version 2)

Version 1 retains its checked-i64 contract. Version 2 adds explicit, case-insensitive
variable declarations; mixed Boolean/Numeric assignments, arithmetic, conditions,
and undeclared/unassigned reads fail before rustc. Example:

```json
{
  "format_version": 2,
  "entry": "Main",
  "procedures": [{
    "name": "Main", "params": [],
    "variables": {
      "Amount": {"kind": "numeric", "precision": 5, "scale": 2},
      "Flag": {"kind": "boolean"}
    },
    "source": "&Amount = 1.235\n&Flag = &Amount = 1.235\nIf &Flag\nPrint(&Amount)\nEndIf"
  }]
}
```

This compiles to a native executable whose `Print(&Amount)` emits ` 1.24` (the
native picture includes the padding); stored `Amount` remains `1.235`. Decimal
arithmetic uses exact checked coefficients (96-bit magnitude, scale up to 28),
not binary floats. Fractional-to-integer assignment truncates; printing a
declared Numeric variable emits exactly the native
`StringUtil.Str(value, precision, scale)` picture - padding, separator place and
the width 15..18 double-narrowed bound included - measured over 120 KBTeste
cases (`tests/fixtures/numeric_render_native.tsv`), with halves rounded away
from zero. Numeric expression printing uses canonical decimal text; Boolean
printing uses `true`/`false`. Display overflow produces width markers.
`And`/`Or` require Boolean operands and short-circuit pure expressions. Like the
measured GeneXus .NET emitter, typed helper calls are evaluated before their
containing expression, even inside an otherwise skipped logical operand. Loop
condition calls run again on each condition evaluation. This differs from the
older format-1 call behavior.

Typed helpers list parameters in `params` and declare those names in
`variables`. A parameter is written like a GeneXus `Parm` entry: `Name` or
`inout:Name` (the GeneXus default), `in:Name`, or `out:Name`; canonical JSON
drops `inout:`. Native KBTeste probes fixed the semantics: `in` is copied in only;
`out` starts empty in the callee and is copied back; `inout` is copied in and
back when the argument is a variable, and a literal or expression argument is
passed by value. Copy-back runs in parameter order, so one variable passed in
several positions ends with the last write. Storage conversion is checked both
ways. A helper without `return_type` is called as a statement, `Proc(args)` or
`Proc.Call(args)`. When its last parameter is `out`, an expression call omits
that argument and receives its final value (the native GeneXus convention).
An explicit `return_type` remains available for `Return expr` helpers, which must
return on every path. The entry is parameterless with no `return_type`. See
`fixtures/typed-calls.json` and `fixtures/typed-parm-modes.json`.
All procedures share a 100,000-statement/iteration budget and a depth limit of 128.
Format 1 keeps by-value calls and rejects parameter modes and call statements.

**Current admission limits:** an explicit `out` argument must be a variable.
Explicit Numeric `inout:` literal/expression arguments were verified natively:
mutations stay local to the temporary. Nested calls run before earlier direct
operand captures: native `Pair(&X, Bump(&X))` from X=1 receives 2 and 2;
`&X + Bump(&X)` from X=10 yields 22. This is not left-to-right snapshot evaluation.
Native empty defaults for Boolean, VarChar, Date, Character and DateTime `out`
parameters match the tested subset; Character trailing blanks and DateTime year/
nonempty values survive copyback. Native `Len("Q ")` reported 1 while retaining
the trailing blank; this does not characterize the separate `.Length()` method
or admit a general `Len` library implementation.
Numeric precision is 1..18, scale at most precision. Native boundary probes for
every integer precision observed Int16 for 1..4, Int32 for 5..9, and Int64 for
10..18. Rust checks those storage limits instead of reproducing native wrapping.
Decimal arithmetic follows .NET `System.Decimal` (28-29 significant digits), so
`1 / 3` prints `0.3333333333333333333333333333`; this matches 288 .NET oracle cases
(`scripts/validate_decimal.py`), not native GeneXus. All typed statements
and loop iterations consume the execution budget. Unsupported types are rejected.

`validate_typed.py --engine <gx-engine.exe> --native-evidence <native-run-directory>
--report <output.json>` compares a newly compiled Rust executable with verified,
source-matched native evidence. Currently 19 observations match; native integer
wrapping is an explicit safety divergence tested separately.

## Gregorian Date (format version 2)

`{"kind":"date"}` supports `YMDToD(year,month,day)`, `.Year()`, `.Month()`,
`.Day()`, `.IsEmpty()`, variable `.SetEmpty()`, comparisons, integer-day addition/
subtraction and Date differences. `.AddMonths(n)` and `.AddYears(n)` take integral
Numeric offsets, clamp the day to the target month and check the supported range. Dates also pass through typed helper parameters
and returns. Invalid month/day combinations produce empty; empty components are
zero. Empty uses the .NET minimum-day sentinel: adding one day yields 0001-01-02.
Arithmetic outside 0001..9999 and fractional offsets fail with controlled errors.
Date display/conversion: `Print(&date)` renders the measured native
`&date.ToString()` picture (`MM/dd/yy` with zero padding; empty renders
`"  /  /  "`). `CToD(text)` parses the measured native grammar — three numeric
components joined by any mix of `-`, `/` or `.`; `yyyy`-first when the first
component has four digits, otherwise `M/d/[yy]yy`; a trailing time after a
space or `T` is accepted only when the time itself is valid — and yields empty
for every other form; `DToC(date)` renders the ISO exchange picture
`yyyy-MM-dd` with the empty sentinel `"0000-00-00"`. `tests/date_conversions.rs`
runs 39 native `DateTimeUtil.CToD2` rows and 6 `DToC2` rows. Text concatenation
with dates and database dates are not implemented. Unassigned reads remain errors rather than implicit initialization.

Two-digit years have **no implicit window**. Set project-level
`"date_two_digit_year_max":2039` only when matching that measured environment;
otherwise years 0..99 fail explicitly. Valid maxima are 99..9999 (format 2 only).
The measured native window maps 39 to 2039 and 40 to 1940; it is not universal.
Changing the setting invalidates all incremental semantic results, reusing parsed
bodies. See `fixtures/typed-dates.json` and `scripts/validate_dates.py`: 37 native
observations match with the explicit 2039 setting; a separate calendar fixture
matches 18 more observations (`validate_dates.py --calendar`). This is a standalone Date
subset, not full SDK or runtime parity.

## Integral-second DateTime (format version 2)

`{"kind":"date_time"}` admits `YMDHMSToT(y,m,d,h,m,s)`, component access
(Year/Month/Day/Hour/Minute/Second), IsEmpty/SetEmpty, comparisons, AddSeconds
and Difference. Typed helper parameters/returns use the same schema. The shared
explicit year-window setting applies here too. Values are timezone-free and have
integral-second precision; `Print(&dateTime)` renders the measured native
`&dateTime.ToString()` picture (`MM/dd/yy hh:mm AM/PM`, 12-hour with seconds
dropped; empty renders `"  /  /   12:00 AM"`). `TToC(dateTime)` renders the ISO
exchange picture `yyyy-MM-ddTHH:mm:ss` (empty renders `"0000-00-00T00:00:00"`,
5 native `TToC2` rows in the same fixture). Fractional components/offsets,
timezone conversions, `Str`-style time formatting, millisecond storage,
`CToT` (its context-less native helper substitutes the current date) and
other calendar methods are not admitted.

Native construction keeps a valid time when the date is invalid (minimum date,
not an empty DateTime); an invalid time component resets the entire time to
midnight while preserving the date. These measured distinctions are tested.
`typed-datetime.json` and `validate_dates.py --datetime` match 42 source-matched
native outputs. This does not establish general DateTime/runtime parity.

## Character/Varchar text (format version 2)

`{"kind":"character","length":5}` and `{"kind":"varchar","length":5}` declare
text variables/parameters/results. Admitted operations: double-quoted literals,
assignment, `+` concatenation, `=`/`<>`, `.Length()`, `Len(text)`, default
`Str(numeric)`, `Trim(text)`, variable `.Trim()`, `Str(numeric, width[, scale])` with integer
literal width 1..18 and optional integer literal scale 0..width, the measured
text functions `Substr(text, start, len)`, `Upper(text)`, `Lower(text)`,
`StrReplace(text, find, replace)` with their method forms `.Substring(start,
len)`, `.ToUpper()`, `.ToLower()`, `.Replace(find, replace)`, and typed helper
calls. `Str` matches the native picture rule measured over black-box
`StringUtil.Str` scans (36,246 in-domain rows, zero mismatches): the value rounds
half away from zero at the display scale, and the field prints asterisks when
`|v|` exceeds the picture bound `10^places - sign - 5*10^-scale`, where native
builds that bound as a `double` and narrows it through `(decimal)` at 15
significant digits — past 15 digits the narrowed bound no longer equals the exact
rational one, and the engine reproduces that pipeline instead of the ideal
arithmetic. The integral zero is rendered only when a place exists, and a value
that rounds to zero loses its sign. `tests/fixtures/str_matrix.tsv` keeps 6,152
recorded native rows (small-width discriminators, the width 9..18 Numeric(p,s)
grid, bound-boundary families and exact-power samples), and
`fixtures/str-bounds-native.tsv` pins the native double/narrowing pair for every
admitted width/scale/sign; dynamic width/scale are rejected. `Len(text)` uses the same UTF-16-unit count and
trailing-ASCII-space handling
as `.Length()`. `len_builtin` tests passed 2/2, matching `.Length()` for empty text (0),
a backslash path (11), padded Character (2), and an astral character (4); this is not a
separate full native `LEN` differential. The measured text functions
(`Substr`/`Upper`/`Lower`/`StrReplace` and their method forms) follow the native
rules: `Substr` reads the raw stored value (1-based, no padding; `start < 1` or
past the raw length is empty text, a negative length takes the remainder and an
over-long length clamps), `Upper`/`Lower` keep a character whose mapping expands
(`ß`), and `StrReplace` is case-sensitive, non-overlapping and left to right
with an empty find as a no-op; the spellings native rejects (`Substring(...)`,
`Replace(...)`, `&v.Substr(...)`, `&v.Upper()`, `&v.Lower()`) stay unresolved
calls. 63 recorded rows in `tests/fixtures/text_functions_native.tsv` run
through `tests/text_functions.rs`. The measured standalone .NET procedure behavior stores
strings without padding or
truncating to declared width. Equality ignores trailing ASCII spaces and is case-sensitive; concatenation
preserves spaces. Length counts UTF-16 code units. `fixtures/typed-strings.json`
is runnable. Escaped/single-quoted/multiline literals, ordering, other trimming
forms, other string-library members, encodings and general collation are not admitted.
Declaration lengths are bounded to 1..65535; stored values have an explicit 1 MiB
UTF-8 resource ceiling, not a native field-width constraint. Mixed types fail
before rustc. Rust preserves valid Unicode, including non-BMP characters.

Native evidence has 18 observations: 17 agree after normalizing four length labels
to values; the native CP1252-generated C# lost an emoji as `??`. Rust deliberately
preserves it instead of reproducing encoding loss. This is an explicit divergence,
not a matching case. `scripts/validate_text.py` checks authored inputs, complete
native readback/execution, and fresh Rust output; it reports the divergence.
These are local procedure semantics, not database-column or all-generator parity.

## Incremental analysis, not an executable cache

The library's `IncrementalCompiler` supports both formats and maintains a transactional parse session. It compares exact canonical procedure identities, parameter lists and source bodies; unchanged bodies reuse parsed ASTs. Typed declaration/return-signature changes invalidate the procedure and its transitive callers without reparsing unchanged syntax. Format changes invalidate all current procedures. Changes propagate through the union of old/new reverse call graphs so renames/deletions do not lose old dependents. Every candidate revision still undergoes full backend semantic validation. A failed candidate does not replace the last accepted session.

`explain previous.json next.json` reports sorted `reparsed`, `reused`, and conservative transitive `affected` procedure lists without invoking rustc. It is a two-revision demonstration, not a daemon or persistent on-disk cache. Native builds remain fresh, and per-query semantic invalidation/runtime compatibility are unfinished. Microbenchmark results apply only to the measured synthetic frontend workload, not the GeneXus SDK or whole builds.

## Evidence and next gates

[Validation and benchmark protocol](VALIDATION.md) documents the reproducible Python oracle, native pipeline measurements, and executed official-GeneXus differential comparisons for the admitted subset. Run `python src/gx-engine/scripts/validate.py --out scratchpad/gx-engine-validation.json` from the repository root. A separate benchmark (`python src/gx-engine/scripts/benchmark.py`) runs the parity capability through both the real GeneXus runtime helper and the engine, re-checks live parity and reports per-pass timings (see VALIDATION.md, "SDK-vs-engine benchmark").

Artifact trees: `scripts/artifact_diff.py` is a read-only byte differential of an engine
output directory against an SDK output directory (identical / different / engine-only /
sdk-only, with sizes, SHA-256 and a reported denominator). It takes both roots explicitly,
never writes either tree, and does no normalisation. Exit 0 = every compared file identical,
1 = differences, 2 = usage/path error, unreadable/vanished input, any symlink/junction/reparse
entry, or nothing compared. `--out` is written atomically (temp file + `os.replace`), so a
symlink or hardlink at the output leaf is replaced, never written through; an output path whose
parent resolves inside either tree is refused. Scope: only regular files with a selected
extension are compared; empty directories and directory-only differences are not reported.
With `--extension`, exit 0 means the *selected* extensions match: check
`summary.selectionComplete` (true only when no scanned file was excluded on either side; see
`engineExcludedByExtension`/`sdkExcludedByExtension`) before reading it as tree parity. Fresh-session command (point it at
freshly generated disposable `C:\KBs\*teste*` offline outputs; keep the report outside Git):

```powershell
python src/gx-engine/scripts/artifact_diff.py --engine <engine-out> --sdk <sdk-out> --extension .cs --extension .js --out $env:TEMP\artifact-diff.json
# tests: cd src/gx-engine/scripts; python -m unittest test_artifact_diff
```

The tool states only what it compared; it does not by itself establish a whole-KB result.

Latest integrated gate: `cargo test` 481 passed, 0 failed, 2 ignored across 48
harnesses; `cargo clippy --all-targets` and `cargo fmt --check` pass. It includes data view 10, output channel 10, diagnostics catalog 10, text tree roundtrip 10, extended types 10, design system 10, data provider 10, impact analysis 10, reorg impact 9, module resolution 10, dml navigation 10, data selector 9, formula navigation 11, navigation specifier 10, table DDL 9, transaction control 7, rules parity 10, domain attribute 10, transaction structure 10, collection ops 24, calendar ops 83, sdt JSON 32, sdt XML 40, conditional state 57, subroutines 13, numeric math 107, collection indices 3, For 8, LEN 2, typed pipeline 47, `kb_pipeline` 13 (direct `Calls` required for every non-builtin callee), SDK XML exchange 16, the portable CLI check→emit→build→artifact-execution→run acceptance flow 4, diagnostics API 5 and CLI diagnostics 5. Zero SQL writes. This is a portable-subset gate, not SDK replacement validation. Passing compiler tests do
not establish full GeneXus behavioral parity or performance advantage.

The native nested-record ExportFile now passes the Rust XML `check` → `emit` → `build`
→ `run` flow with exit 0 and output matching native stdout after CRLF-only line-ending
normalization; the input XML remains unchanged (`scratchpad/nested-record-export-
1792080479819133800/authored.xml`). The captured deep-SDT export likewise runs through
`xml-run` with an injected companion Procedure: nested writes, the depth-2 collection
count, and a variable bound to the natively evidenced
`NativeDeepSdt85299100.Child.NestedCollectionItem` alias print the expected values, and
the captured input stays unchanged (`scratchpad/deep-sdt-native-export-1792102090401/
xpz-contents/exportfile-sanitized.xml`, driven by `scratchpad/run-deep-projection-check.py`).
The custom-item export has no Procedure entry, so
the compiler correctly refuses to run it without an entry; there is no runtime
equivalence result for that file. The dynamic collection-index differential was run
against disposable KBTeste: in-range variable/expression/For indices matched native
outputs exactly and fractional indices matched native truncation toward zero (see the
collection section), while invalid indices keep the engine's controlled diagnostic
against native's `NullReferenceException`. Real diagnostic phase tests remain
outstanding.
The custom-item XML projection shape itself is recorded at
`scratchpad/custom-item-export-1792080825587526300/authored.xml`.
Navigation/specifier and native-KB interoperability remain substantial gaps. No
proprietary decompiled sources/binaries belong in this crate or fixtures.
