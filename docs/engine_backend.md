# Engine backend switch (`gx-engine`, experimental)

The Gateway can point at [gx-engine](../src/gx-engine/README.md) — the
independent Rust implementation — as an alternative backend to the GeneXus SDK.
The switch exists so the replacement can be exercised from the real product
before the SDK is removed: flip one setting, see the engine from
`genexus_doctor`, and let capabilities move to the engine as they reach measured
parity.

**Default policy.** `Engine.Backend=gxengine` (also when the section is absent)
uses gx-engine for every call inside the admitted space: blocking
`genexus_lifecycle` `build`/`rebuild` requests (through `parallel_rebuild_all` /
`rebuild-all`), field-limited type-only `genexus_query` requests (through
`kb-query`), bounded `genexus_kb.get_environment` reads (through
`kb-environment`), and nonempty bounded Procedure pages of `genexus_list_objects`
(through `kb-list-procedures`). Object reads stay on SDK fallback until response
parity is verified. A blocking directed `build` is also admitted for
`mode=compile_check`, explicit `callers=false`, and transitive callees, using a
simple or module-qualified object name. Edits, async builds, and unsupported
request shapes fall back to the SDK (`Engine.Fallback=sdk`). This selects the
existing admitted space; it does not expand whole-KB compatibility or guarantee
that arbitrary objects compile. Build errors/partial reports remain errors,
not a trigger to replay a started build through the SDK.

`gxengine` replaced `sdk` as the default in the 4.0.0 line (a breaking default change:
set `Engine.Backend=auto` to route only builds, or `Engine.Backend=sdk` to keep the
previous behavior and never invoke the engine). The evidence behind it is
the disposable `KBTeste` KB: whole-KB `rebuild-all` generates and compiles 346/346
targeted objects with 0 refusals, its 328 C# files match the SDK's output byte for byte,
and whole-KB generation runs in ~0.62 s against ~36 s for the SDK (paired; see the
[engine README](../src/gx-engine/README.md#measured-local-sdk-comparison)). A KB with forms
outside that evidence gets refusals by name, reported as Partial when skipped during planning; checked-emission errors make the build Failed.

`Engine.Backend=auto` narrows routing to admitted builds; reads and queries go
back to the SDK. Explicit `Engine.Backend=sdk` disables all engine routing,
including builds. `genexus_read` and `genexus_edit` are held outside the engine
by the explicit deferral policy (no capability declares them as consumers): the
engine writer has no live persistence yet, so an engine edit would report a save that never reaches the Knowledge Base database
(`Fallback=error` refuses it, `Fallback=sdk` serves it with the SDK). Everything
else — and every call outside the admitted space documented below — stays on the
SDK/Worker path, or is refused when `Engine.Fallback=error`. The remaining
native-oracle capabilities (`str_format`, `numeric_render`, …) still have no
product consumer, and the gate is built so nothing partial can route. Native
`kb-read` now composes the SDK single-part `versionToken` for Source, Events,
Rules and Conditions text and has a tested pagination projection; `genexus_read`
stays deferred until the rest of the response envelope and object resolution are
verified. The physical storage facts behind the read path are recorded in
[`udm_storage_notes.md`](udm_storage_notes.md).

## Coverage accounting

The MCP contract inventory contains **60 tools and 312 operation rows**. Locally
MCP-verified routing admits a restricted subset of **6 tools (10%)**:
`genexus_query`, `genexus_lifecycle` (`build` / `rebuild`), `genexus_kb`
(`get_environment`), `genexus_list_objects` (bounded Procedure, Transaction, WebPanel,
Table and DataProvider pages), `genexus_kb_version` (`list`), and `genexus_inspect`
(minimal Procedure, Transaction and WebPanel inspection). Those are **7 of 312 operation rows (2.2%)**,
each still subject to the argument and capability gates below.
In addition, `genexus_read` routes single-part reads (`Source`, `Events`, `Rules`, `Conditions`)
under `Engine.Backend=gxengine` directly to `kb-read` while retaining SDK fallback for all other shapes.

| Tool | Natively routed request (`Backend=gxengine` only, except builds) | Everything else |
| :--- | :--- | :--- |
| `genexus_query` | field-limited, type-only queries | SDK |
| `genexus_lifecycle` | admitted blocking `build` / `rebuild` | SDK or refusal |
| `genexus_kb` | `get_environment` with exactly one stored environment | SDK |
| `genexus_list_objects` | nonempty pages (`typeFilter` Procedure, Transaction, WebPanel, Table, DataProvider), explicit `limit` 1..100, fields name/type | SDK |
| `genexus_kb_version` | explicit `action=list` | SDK (absent action, `changed_objects`, mutations) |
| `genexus_inspect` | exact-case ASCII name, `type` Procedure/Transaction/WebPanel, `projection=minimal` | SDK (other projections/types/selectors) |
| `genexus_read` | single-part `Source`, `Events`, `Rules` or `Conditions` for Procedure, WebPanel, Transaction | SDK (multi-part, other parts/types/projections) | These figures count routing breadth, not complete implementation,
engineering effort, or independently verified SDK replacement.

**0 of 60 tools are certified fully SDK-independent across their complete
supported contract.** Engine calls still obtain active-version scope from the
Worker; query filters/projections and lifecycle modes outside the admitted space
remain SDK-backed. Native CLI reads have bounded differential evidence, but MCP
reads stay deferred. Native live writes, read-to-edit concurrency, and broad tool
families remain incomplete or unverified. Gateway-only tools are not gx-engine
implementations and are not counted as native engine coverage.

The local `genexus_kb.get_environment` increment passed packaged MCP comparisons
under `sdk`, `auto`, and `gxengine` on the disposable timestamp clone. The engine
policy served two actual native reads with matching visible values and no SDK
deferrals; other policies stayed SDK-only. With Procedure listing this reaches
four tool families; the KB version list and minimal Procedure inspection below
bring it to the six verified families the current goal sought. Each family
counts one bounded request shape on one disposable fixture, not complete tool
parity. Static gates remain nonzero; these unpublished changes are not a
release certification.

Do not derive a completion percentage from unit-test totals, internal capability
labels, or the inventory's `execution` labels: the latter are dispatch metadata,
not a certified per-action SDK-dependency audit.

## Current environment

`genexus_kb action=get_environment` has a bounded native candidate under
`Backend=gxengine`, using `kb-environment`. It requires a ready, matching Worker
active-version snapshot, and the same model/version still observed after the
native read. The engine selects the named Design model and reads its sole child
environment's persisted name within one private serializable read-only
transaction. Zero or multiple environments refuse instead of guessing which one
the SDK session has selected. Missing, duplicate, malformed or wrong-model names
also refuse. Names preserve Unicode and stored spacing.

Only `action=get_environment` and `kb` (plus existing Gateway response/context
arguments) are admitted. `list_environments`, environment mutation, startup
selection, and every other KB action keep their existing SDK/Gateway path.
`Backend=sdk|auto` stays SDK-only; unsupported arguments and native errors obey
`Fallback=sdk|error`. An unknown or changed active scope never returns a native
success. The result is `{environment: <stored name>, engine: <provenance>}` before
normal Gateway enrichment/projection. This creates no authoritative write token
and does not promote `genexus_read`, persistent writes or build scope.

Evidence so far: on the independently isolated disposable timestamp clone, the
SDK current environment and the real native CLI both returned `.NET` for trunk
model 1/version 0; the SDK version inventory remained unchanged. Five pure Rust
metadata tests, strict CLI admission and fallback/policy tests cover negative
cases; the multiple-environment regression has a failing guard-removal mutation.
Raw local receipts stay under `C:/tmp/gxe-goal-10pct/`, outside Git. Packaged MCP
comparisons under all three policies passed; repeated calls matched and the
SDK-reported version inventory plus connection-file bytes remained unchanged.
All three harnesses confirmed clean Gateway/Worker exit and zero logged errors.
This single-fixture evidence is not full SDK parity, live concurrent-version-switch
certification, or a performance measurement.

## Procedure listing

`genexus_list_objects` has a bounded native candidate for nonempty pages with
`typeFilter=Procedure`, an explicit integer `limit` from 1 to 100, an optional
Int32 nonnegative `offset`, and explicit `fields` limited to `name` and/or `type`.
The engine selects the Worker-reported Design model, lists active Procedure rows,
and reads the page slice's stored lifecycle timestamps through one batched SQL
query (`UdmClient::get_objects_lifecycle_ticks`) in one fresh serializable
read-only view. It reproduces the Worker default order (.NET `OrdinalIgnoreCase`
by name within one type bucket) only for nonempty, unique ASCII names: names
that need Unicode case rules, case collisions (whose SDK tie-break is the GUID),
an empty catalog, malformed rows or a short page are refused.

The Gateway emits the Worker page envelope before normal projection: counts,
`nextOffset`, `pagination`, `_meta.aggregates` (`by_type`, `lastUpdate` bounds,
`modified_last_7d`), `suggested_next` and `alternative_views`. Empty pages stay on
the SDK, because their `typesAvailable` hint describes the whole SDK index. Other
types, comma lists, filters, parents/paths, `sort`, `since`, cursors, `verbose`,
`inline_read_top`, implicit/default fields and changed/unknown Worker scope stay
on the SDK or fail closed with `Fallback=error`. Unlike the SDK path, an admitted
native page does not wait for the Worker search index. This is not a concurrency,
freshness-during-index-rebuild or performance claim.

Evidence so far: on the same isolated clone, the native CLI matched the retained
SDK pages at offsets 0 and 3 with `limit=3` (total 201, names/order and stored
`lastUpdate` aggregate bounds), and the beyond-end page's total and empty result.
Unit/routing tests cover ordering, ambiguity, paging bounds, admission, malformed
or short pages and changed scope; the ordering, short-page and empty-page guards
failed under mutation. Packaged MCP comparisons under `sdk`, `auto` and `gxengine`
then passed against the retained SDK pages. Comparison ignored only correlation,
Worker telemetry, backend provenance and the response-size counter
`_meta.tokens.used`. The engine policy served both nonempty pages natively and
cleanly deferred the beyond-end page to the SDK. The version inventory and
connection bytes were unchanged, cleanup passed, and zero errors were logged.
The native response carries no Worker `_meta.telemetry` block. A first packaged
run exposed that the beyond-end page reached the SDK only through an exception
(which `Fallback=error` would report as an invocation failure). It is now an
explicit refusal, and a regression covers it.

## KB version list

`genexus_kb_version action=list` (explicit action only) has a native candidate
using `kb-versions`. In one fresh serializable read view the engine checks the
Worker-reported active scope against the model topology, then reads the latest
stored `Udm.Types.KnowledgeBaseVersions` entities (verifying none is missing a
latest entity version), their users and the Knowledge Base name, which names
the trunk. Like the SDK's `KBVersion.GetAll`, the list excludes the trunk.
Every listed version must be stored as visible, agree on name and description
between its row and its properties, have `IsFrozen` True/False, resolve its
parent and user, and own exactly the Design model its data names; every
versioned Design model must belong to a listed version. Anything else refuses.

The Gateway emits the Worker `KbVersionListRetrieved` envelope: `isBranch` is
`!isFrozen` (every listed version is a frozen version or a development branch),
`isTrunk` false, `isActive` from the Worker's active version id, `lastUpdate`
from the stored UTC `EntityVersionTimestamp`, and no `createdAt` key (the
SDK-visible list carries none). Provenance labels intentionally differ:
`source` is `udm:KnowledgeBaseVersions`, `lastUpdateSource` is
`udm:EntityVersion.EntityVersionTimestamp`, and `createdAtSource`/`createdAtNote`
name the stored entity. Order is stored id order, matched on one fixture only.

Behavioral difference: the SDK `list` also re-captures the Worker's active-model
snapshot as a side effect; the native route only reads that snapshot (before and
after). If a Worker-side path changed the session's active version without
recapturing it, the native list would report the snapshot's active version; that
path is not known to exist but is not ruled out.

Evidence: on the isolated clone (trunk `KBTeste`, frozen
`TimestampAuthorityFreeze`, branch `TimestampAuthorityBranch`), the native CLI
matched the retained SDK list for the trunk and refused wrong or missing scope.
Packaged comparisons under `sdk`, `auto` and `gxengine` passed, ignoring only
correlation, telemetry, backend provenance, `_meta.tokens.used` and the four
provenance labels (whose exact per-backend values are asserted). A first packaged
run failed because the native answer carried `createdAt: null`; that is fixed
with a regression. Guard mutations (active-version id, visibility, `createdAt`)
failed their tests.

## Procedure inspection

`genexus_inspect` with an exact-case ASCII `name`, explicit `type=Procedure` and
`projection=minimal` has a native candidate using `kb-inspect`. The engine
selects the only Procedure with that name and refuses when any other model
entity shares the name (the SDK would disclose such objects from its index).
It returns the GUID, entity id, module path (`Root Module/.../<name>`; folders or
any non-module holder refuse), description, lifecycle timestamp (the same
`ObjectSaved` source the Procedure listing uses), user, and the SDK display
names of the stored parts: `Procedure`, `Rules` and `Conditions` parts display
as `Source`, `Rules` is also listed by name, and `Variables`, `Documentation`,
`Help` and `Layout` keep their names, ordered like `OrdinalIgnoreCase`. Unknown
part types, disagreeing model/entity user or description, and missing GUID,
timestamp or user refuse. The Gateway composes the entity key from the Procedure
type GUID and keeps requests on the SDK when the KB has a `genexus_memory` store
(the Worker attaches its notes to inspections) or when the engine resolves a
differently cased name.

Evidence: six retained SDK minimal inspections on the isolated clone (two
GeneXus library Procedures with three stored parts and four user Procedures with
seven) matched the native CLI field by field, and packaged comparisons under
`sdk`, `auto` and `gxengine` passed (eight native reads under `gxengine`,
including the version lists around them). Mutations removing the `Rules` alias,
the name-collision check, the memory-store check or the case check failed their
tests. Standard/verbose projections, `include`, GUID/entity-key/path selectors and
other types remain SDK-backed.

## Enabling it

`config.json` (all documents). A direct-exe launcher (`GENEXUS_MCP_GATEWAY_EXE`) or
`start_mcp.bat` reads `publish\config.json` of the checkout, **not** the root
`config.json`, unless the client sets `GX_CONFIG_PATH`. `build.ps1` regenerates
`publish\config.json` without an `Engine` section: pass `-PreserveEngineConfig`
(`install.ps1` always does) to carry `Engine.Backend`, `Engine.Fallback` and
`Engine.TimeoutSeconds` over; `Engine.Executable` is machine-specific and is always
dropped with a warning. Without the switch the build warns that the section was
discarded.

| Key | Values | Default | Meaning |
| :--- | :--- | :--- | :--- |
| `Engine.Backend` | `gxengine`, `auto`, `sdk` | `gxengine` | `gxengine`: admitted builds, field-limited type-only queries and sole-environment reads use the engine; object reads/edits and the rest use SDK fallback. `auto`: only admitted builds use the engine. `sdk`: SDK only, the engine is never invoked. |
| `Engine.Fallback` | `sdk`, `error` | `sdk` | What happens when a routed capability is not at parity: keep the GeneXus SDK, or refuse the call. `error` is the verification mode. |
| `Engine.Executable` | path | unset | Explicit `gx-engine.exe`. Authoritative: when the path is missing the probe reports `EngineNotFound` instead of running a different binary. |
| `Engine.TimeoutSeconds` | positive integer | `120` | Wall-clock budget for one engine invocation (matches the engine's native-compilation deadline). |

**Which `config.json`?** A Gateway reads `GX_CONFIG_PATH` first; without it, it
takes the first `config.json` found walking up from its own executable. A Gateway
started from `publish\GxMcp.Gateway.exe` or `publish\start_mcp.bat` without
`GX_CONFIG_PATH` therefore reads `publish\config.json`, not the `config.json` at
the checkout root, and `build.ps1` rebuilds `publish\` (and that file, as a
neutral fallback with no `Engine` section) on every run. To keep an engine choice
for that launcher, pass it to the build: `.\build.ps1 -EngineBackend sdk` (or
`.\install.ps1 -EngineBackend sdk`) writes `Engine.Backend` into
`publish\config.json`. A build that finds an `Engine` section in the previous
`publish\config.json` that it does not carry over prints a warning naming the
keys it dropped; `Fallback`, `Executable` and `TimeoutSeconds` are not written by
the switch and are restored by editing `publish\config.json` afterwards.

Environment overrides (non-strict documents; a **strict** document rejects all
four at load time, because the engine backend is a behavioral/execution switch
and the file must stay authoritative — the same rule as `GXMCP_PROFILE`):

| Variable | Equivalent |
| :--- | :--- |
| `GXMCP_ENGINE_BACKEND` | `Engine.Backend` |
| `GXMCP_ENGINE_FALLBACK` | `Engine.Fallback` |
| `GXMCP_ENGINE_EXE` | `Engine.Executable` |
| `GXMCP_ENGINE_TIMEOUT_SECONDS` | `Engine.TimeoutSeconds` |

Invalid non-strict backend values are logged and resolve to SDK-only; a
strict document fails at parse time. The Gateway hot-reloads `config.json` and
re-resolves these settings on use, so `genexus_doctor` reflects an edit without
a restart.

## SQL authentication (issue #635)

Native KB access uses `sqlcmd`, not the SDK's private credential store. For
`IntegratedSecurity=False`, provide both `SQLCMDUSER` and `SQLCMDPASSWORD` in the
**Gateway process environment** (or the standalone gx-engine process). Explicit
SQLCMD environment values override the corresponding connection-file fields;
missing values may come from that file. Empty users/passwords are rejected
before starting sqlcmd, so it cannot prompt on its batch stdin. Passwords are
passed only in the child environment, never command-line arguments. Do not put
passwords in checked-in configuration, MCP arguments, terminal commands or logs;
use the client's protected environment/secret mechanism and restart the Gateway
after changing inherited credentials. `IntegratedSecurity=True` keeps Windows
authentication (`-E`) and ignores SQLCMD credential variables. The engine does
not silently change authentication identity or retry with Windows authentication.

For `rebuild-all`, missing credentials produce exit **5**, empty stdout, and a
single JSON diagnostic on stderr under `--diagnostics=json`:
`category=sql_credentials_unavailable`, an actionable `message`, and `location=null`.
This dedicated preflight refusal happens before storage reads or build outputs.
`Engine.Fallback=sdk` logs the refusal and runs the original SDK path;
`Fallback=error` returns `EngineSqlCredentialsUnavailable`. No password is copied
from the SDK to the engine. This is not permission to replay generic login errors,
timeouts, malformed output or failed builds. Existing exit-4 version-scope refusals
remain safe before build effects; all other failures after a build starts must not
trigger automatic SDK build replay and may require reconciliation.

### Exactly what `auto` routes

`auto` considers only `genexus_lifecycle` `build`/`rebuild`, with these argument gates:

| Request | Engine candidate under `auto` |
| :--- | :--- |
| Whole-KB `build`/`rebuild`, no `target` or `mode` | Yes, if blocking |
| Targeted `build mode=compile_check callers=false includeCallees=transitive` | Yes, if blocking and target is a simple/module-qualified object name |
| Normal targeted build, another mode, callers enabled, or non-transitive callees | No; SDK (or refusal under `Fallback=error`) |
| Async build | No; SDK (or refusal under `Fallback=error`) |
| Read, query, edit, any other tool/action | SDK; never invokes the engine under `auto` |

Blocking means `wait_until_done=true`, or `estimated_seconds` below the Gateway's
sync threshold (default 20 seconds). Full deployment (`skipFullDeploy=false`),
extra arguments and other admitted-space restrictions below still apply. A
candidate is not a successful build: a build of a frozen version or a branch (or
of a versioned KB whose facts cannot be pinned to the trunk) is refused before
build output and uses the configured SDK fallback. `Backend=sdk` disables
engine invocation entirely. Unsupported/failed SDK builds remain SDK errors.

## What changes today

With default `Backend=gxengine`, admitted builds, field-limited type-only
queries, sole-environment reads, bounded Procedure pages, the KB version list and
minimal Procedure inspections can be served natively. Object reads and edits
are not engine candidates: an
explicit deferral policy (not a capability claim) always hands them to the SDK or
refuses them under `Fallback=error`, pending response parity and persistent writes.
`Backend=auto` keeps only the builds.
Every other tool/action is untouched. `genexus_doctor` result gains an `engine` block:

- always: `backend`, `fallback`, and `capabilities` — the `parity` / `partial` /
  `unsupported` counts plus `routedOperations`, the operations the selected policy
  lets the engine actually serve (argument gates still apply: builds, the
  type-only query and the sole-environment read), and `deferredOperations`, the read/edit operations held
  outside the engine by explicit policy (`genexus_read`, `genexus_edit` under
  `Backend=gxengine`; empty under `auto` and `sdk`). A deferred operation is never
  counted as parity or listed as routed;
- with `Backend=gxengine|auto`: `executable`, `available`, `version` from the
  `gx-engine --version` handshake (ten-second budget) or a structured `error`
  with one of `EngineNotFound`, `EngineStartFailed`, `EngineProbeFailed`,
  `EngineVersionUnrecognized`, `EngineTimeout`.

When the switch is enabled and the binary is unusable, doctor appends a warning
naming the error code and the configured `Engine.Fallback`. Routing is
observable in every answer: an engine-served payload carries a top-level
`engine` block (`{ backend: "gxengine", capability, operation, fallback,
executable? }`). The switch remains observable first and behavioral last, in
that order.

## The capability gate

`EngineCapabilityRegistry` (Gateway) is the single source of truth for what the
engine may serve, and `EngineRouter.Decide(capabilityId, settings)` is the
single entry point a swap must call:

- `Backend=auto` → only `parallel_rebuild_all` is eligible; all other
  capabilities remain SDK-only, even with `Fallback=error`. Builds still require
  the same parity and argument gates as explicit engine mode.
- `Backend=sdk` → the SDK serves everything; the engine is never invoked.
- `Backend=gxengine` (default) + capability at `Parity` → the engine serves it.
- anything else (partial, unsupported, unknown capability) → the SDK serves it,
  or with `Fallback=error` the call is refused with `EngineCapabilityNotParity`
  and the reason. There is no silently-partial engine path.

`genexus_read` and `genexus_edit` are separate from this gate. They are held
outside the engine by an explicit deferral policy
(`EngineRoutedOperations.DeferredOperationCapabilityIds`), which reports the
evidence record `udm_storage_access` (`partial`, no consumer) as their
`engine.capability` and does not call `EngineRouter.Decide`: `Backend=sdk` and
`Backend=auto` leave them on the SDK; `Backend=gxengine` refuses them with
`EngineOperationOutsideAdmittedSpace` under `Fallback=error` and hands them to the
SDK under `Fallback=sdk`. The policy is routing, not evidence, so it never makes a
capability `Parity`.

Status is a claim with evidence: a capability may only move to `Parity` after a
measured native differential, and `EngineBackendTests` fails when an entry cites
a repository path that does not exist. The entries below keep the evidence
write-up for their differential; the current status of every capability is owned
by `EngineCapabilityRegistry` (and the live `genexus_doctor` view). The registry
and this table agree as of this revision, and `EngineBackendTests` pins the
`Parity`, `Unsupported` and non-parity sets by capability id.

**What a status means.** `Parity` is reserved for (a) capabilities whose
evidence is a recorded native-oracle fixture (`*_native.tsv`: the engine value
compared row by row with the native runtime, as recorded in
[`plans/129-implementation-progress.md`](../plans/129-implementation-progress.md);
`EngineBackendTests` checks the fixture is consumed by a Rust test and that its
provenance is recorded either in that ledger or in a captured-from-native comment
in the consuming Rust test, not that the data was re-measured against a live SDK;
`date_display` is the second kind: its provenance is the Rust capture comment and
[`src/gx-engine/VALIDATION.md`](../src/gx-engine/VALIDATION.md) (lines ~679-710),
not the ledger) and (b) the
routed capability records below, each only for its documented admitted space. A capability whose
evidence is the engine's own synthetic unit tests is `Partial`: those tests show
the library behaves as its authors specified, not that it matches the SDK or a
product operation. Doctor's `parity`/`partial`/`unsupported` counts are an
inventory of these labels, not a percentage of SDK replacement, and the number of
entries is not a coverage measure. The routed records are narrow on purpose so a
broad storage label never becomes a routing gate:

- `udm_type_query` — `Parity` only for the field-limited type-only
  `genexus_query` described below; consumer `genexus_query`. Its evidence is a
  recorded, bounded historical SDK-vs-engine differential (see the table row),
  not a claim for every KB.
- There is no read/edit capability record: `genexus_read` and `genexus_edit` are
  covered by the deferral policy described under the capability gate, and the
  storage record `udm_storage_access` stays `partial`.
- `parallel_rebuild_all` — `Parity` only for the admitted blocking build/rebuild
  space and the recorded `KBTeste` differential.

| Capability | Status | Evidence / note |
| :--- | :--- | :--- |
| `udm_type_query` | parity (routed, admitted space only) | Field-limited type-only `genexus_query` through `kb-query`. Recorded historical differential (retained outside Git, not re-run for this revision, one disposable GX18 `KBTeste`, forced and settled SDK index, `Fallback=error`): 19 type tokens × limit 1/10/100 = 57 calls with `fields=[name,type]`; 45 answered natively with `results`, `count`, `total`, `hasMore`, `pagination` and `_meta.match_quality`/`suggested_next` equal to the SDK answer (45 native-served log lines), 6 expected empty-result refusals, 6 `webpanel`/`wp` deferrals, 0 differences. The retained `comparison.json` (`C:/tmp/gx-release4-index-final-parity`, outside Git, not shipped or re-verified by the repository tests) records `gatewaySha256=b1bf724d3f5de9149cd8f768ed786079151390d9667d502c0cb84a9e2b875b86` and `workerSha256=3f725129c7aeb5561c2550c204b579a95d0874590b5941647ce71d114d63e439`. The repository tests (`EngineRoutedOperationTests`, `EngineReadQueryContractTests`, `src/gx-engine/tests/kb_ops.rs`, `src/gx-engine/tests/udm_reader_writer.rs`) are admission/envelope contract tests with a stub engine and storage tests, not that differential. Not a claim for other KBs, name/text search or reads. |
| `udm_storage_access` | partial | Direct native UDM storage access: connection parser + envelope decoder + token reconstructor + reader/writer; `src/gx-engine/src/udm/mod.rs`, `src/gx-engine/tests/udm_decoder.rs` (19/19), `src/gx-engine/tests/udm_reader_writer.rs` (10/10), plus a bounded live KBTeste read. No consumer: the writer persists nothing to a live KB and MCP read parity (SDK identity, freshness, timestamp/parent identity, part-text normalization, `versionToken`) is not established. |
| `parallel_rebuild_all` | parity (routed, admitted space only) | Parallel specification + Roslyn compilation; `src/gx-engine/src/rebuild.rs`, `src/gx-engine/tests/rebuild_pipeline.rs` (5/5: sub-second 24-proc parallel rebuild) and the `KBTeste` differential in the [engine README](../src/gx-engine/README.md) (346/346 compiled, 328/328 C# files identical to SDK output). Serves `genexus_lifecycle` `build`/`rebuild`; other Knowledge Bases and runtime behavior of the generated programs are not established. |
| `str_format` | parity | Numeric `Str` pictures: native `double`→`(decimal)` (15-digit) bound pipeline reproduced; 36,246 in-domain native rows across six scans, 0 mismatches; `src/gx-engine/tests/fixtures/str_matrix.tsv` (6,152 rows) plus the full bound table in `src/gx-engine/fixtures/str-bounds-native.tsv`. No consumer yet. |
| `numeric_render` | parity | Implicit `&var.ToString()`/`Print(&numericVar)`: native measured equal to `Str(value, precision, scale)` (padding included) over 120 KBTeste cases across precision 1..18 boundary families, scale corners and the width 15..18 narrowed-bound regime; `src/gx-engine/tests/fixtures/numeric_render_native.tsv` + `src/gx-engine/tests/numeric_render_native.rs`. Expressions/record members/date components keep the untyped display path. No consumer yet. |
| `text_functions` | parity | `Substr`/`Upper`/`Lower`/`StrReplace` plus `.Substring`/`.ToUpper`/`.ToLower`/`.Replace`: 63 recorded KBTeste rows (raw-value substringing with 1-based out-of-range rules, per-character case mapping that keeps characters whose mapping expands, case-sensitive left-to-right non-overlapping replace); `src/gx-engine/tests/fixtures/text_functions_native.tsv` + `src/gx-engine/tests/text_functions.rs`. Non-BMP positions and database-column semantics outside the claim. No consumer yet. |
| `text_extended_functions` | parity | `LTrim`/`RTrim`/`PadL`/`PadR`/`Space`/`Concat`/`StartsWith`/`EndsWith`/`Contains`/`StrSearch` plus method forms: 95 recorded `GeneXus.Utils.StringUtil` rows (exact ASCII space trimming, left/right padding with truncation, single-sided concat trimming, ordinal comparisons, 1-based index clamping); `src/gx-engine/tests/fixtures/text_extended_native.tsv` + `src/gx-engine/tests/text_extended.rs`. No consumer yet. |
| `numeric_math_functions` | parity | `Int`/`Round`/`Trunc`/`Val`/`Left`/`Right`/`Asc`/`Chr`/`NotNumeric`/`LTrimStr` plus method forms: 107 recorded `GeneXus.Utils.NumberUtil` and `StringUtil` rows (`Math.Floor`, `AwayFromZero` with negative digit support, truncation toward zero, numeric string parsing, ASCII char conversions); `src/gx-engine/tests/fixtures/numeric_math_native.tsv` + `src/gx-engine/tests/numeric_math.rs`. No consumer yet. |
| `collection_operations` | parity | `Add`/`Remove`/`Clear`/`IndexOf`, `For In` iteration and `Exit`: 24 recorded `GeneXus.Utils.GxSimpleCollection` and `GXBaseCollection` rows (1-based index, index insertion, silent bounds removal, element aliasing in loops, loop exit); `src/gx-engine/tests/fixtures/collection_ops_native.tsv` + `src/gx-engine/tests/collection_ops.rs`. No consumer yet. |
| `calendar_operations` | parity | `Day`/`Month`/`Year`/`Hour`/`Minute`/`Sec`/`Dow`/`AddYr`/`AddMth`/`DAdd`/`TAdd`/`DDiff`/`TDiff`/`Age`/`EndOfMonth`/`ResetTime` plus method forms: 83 recorded `GeneXus.Utils.DateTimeUtil` rows (components, leap day clamping, day of week indexing 1=Sunday, forward/backward date and second arithmetic, day/second differences, completed years age calculation, end-of-month and time reset); `src/gx-engine/tests/fixtures/calendar_ops_native.tsv` + `src/gx-engine/tests/calendar_ops.rs`. No consumer yet. |
| `conditional_and_state_operations` | parity | `Iif(cond, trueExpr, falseExpr)` short-circuit conditional, universal `.IsEmpty()`/`IsEmpty()` and `.SetEmpty()` across `Numeric`, `Text`, `Boolean`, `Date`, `DateTime`, `Collection`, and `Record` members, `Nullvalue(&var)`, `.UDP(...)` calls, and `Format`/`NewLine`/`Replicate`/`CharAt`/`StrSearchRev`/`.LastIndexOf`/`BoolToStr`/`ToBoolean`/`StrToBool`: 57 recorded `GeneXus.Utils.StringUtil` and state oracle rows; `src/gx-engine/tests/fixtures/conditional_state_native.tsv` + `src/gx-engine/tests/conditional_state.rs`. No consumer yet. |
| `numeric_core` | partial | Signed 64-bit arithmetic and checked overflow; not a Numeric/Decimal compatibility claim. |
| `text_core` | partial | Len/Trim/comparisons over CP1252 bytes; known encoding divergence on high bytes. |
| `calendar_values` | partial | Date/DateTime component subset; no timezone or fractional-second coverage. |
| `records_collections` | partial | Bounded record/collection runtime; invalid index is a controlled error where native crashes (documented divergence). |
| `procedure_pipeline` | partial | Admitted Procedure compile+run; bounded reachability. |
| `xml_projection` | partial | SDK `ExportFile` XML projection; bounded depth/item-alias rules. |
| `kb_catalog` | partial | The engine's own portable catalog format, not a native KB reader. |
| `native_kb_read` | partial | Text tree/XPZ catalog decoding only; the bounded native storage reader is `udm_storage_access`. Not MCP read parity; `genexus_read` stays on the SDK. |
| `sql_navigation`, `formula_navigation`, `dml_navigation`, `dataselector_composition` | partial | Specifier/navigation/SQL and DML *plans* from synthetic fixtures; no differential against SDK navigation output and no SQL is executed. |
| `csharp_emitter` | partial | Emits and compiles C# for the supported forms; no consumer of its own. The `KBTeste` SDK byte comparison is carried by `parallel_rebuild_all`. |
| `subroutine_control_flow`, `domain_attribute_projection`, `transaction_structure_projection`, `rules_and_parm_parity`, `transaction_control`, `physical_table_ddl` | partial | Frontend/catalog/XML-projection behavior checked by the engine's synthetic tests (subroutines: plus an observation of SDK-generated C# shape); no recorded native execution rows. |
| `database_reorganization`, `module_namespaces`, `dataprovider_specification`, `object_impact_analysis`, `guid_and_extended_math`, `design_system_tokens_styles`, `structured_diagnostics_catalog`, `text_tree_roundtrip`, `dataview_external_table_mapping`, `output_channels_streaming_buffers`, `webpanel_layout_events`, `code_formatting_linting`, `datastore_provider_profiles`, `gam_security_authorization`, `xpz_package_transfer`, `object_versioning_history`, `gxtest_runner`, `multiagent_locking_leases` | partial | Portable-subset libraries with no product consumer, evidenced only by the engine's own synthetic tests; no SDK differential. These were previously labelled parity and are not SDK or product parity. |
| `general_gx_surface` | unsupported | Umbrella claim withdrawn; its components are individually partial. Rules, Events and forms outside the admitted subsets fail closed. |

The parity claim is also measurable: `python src/gx-engine/scripts/benchmark.py`
runs one corpus through the native runtime helper (official
`genexus.classes.core` package) and the engine bench target, re-checking live
output parity (row count, character count, FNV-1a/UTF-16 digest) before
reporting per-pass timings - after two hot-path fixes the measured medians are
~121-130 ns/row engine against ~274-286 ns/row native (see
`src/gx-engine/VALIDATION.md`, "SDK-vs-engine benchmark").

## Served operations and admitted space

Each routed operation admits a deliberately narrow argument space, declared as
one list of supported arguments per operation (`EngineAdmittedSpace` in
`src/GxMcp.Gateway/EngineRoutedOperations.cs`). Any other requested argument
— including one the engine does not know yet — is outside the space; the refusal
message names it and lists the supported arguments. `null`, blank, empty and
`false` values request nothing. A call
outside it is never approximated: with `Fallback=sdk` it continues on the SDK
path unchanged, and with `Fallback=error` it is refused with
`EngineOperationOutsideAdmittedSpace` naming the reason. The same rule applies
when a read/query invocation fails (`EngineNotFound`, `EngineStartFailed`,
`EngineTimeout`, `EngineCommandFailed`). Builds may fall back only on
`EngineNotFound` or `EngineStartFailed`: after process start, missing or
unrecognized reports return an error with `reconciliationRequired: true`, even
with `Fallback=sdk`. A failed or timed-out process may already have changed build
artifacts, so the Gateway must not replay that build on the SDK. A successful
report paired with a nonzero exit is also uncertain, not a zero-error build
failure. Exceptions while observing a started process return
`EngineObservationFailed`; cleanup kills the process tree and waits up to five
seconds for the owned process to exit, reporting whether that exit was observed.
Unknown build outcomes additionally carry `mutationOutcomeUnknown: true`, which
preserves a keyed request's durable retry fence. A generic reconciliation flag
alone does not retain the new request's fence: it may be a pre-dispatch refusal
requiring reconciliation of an earlier write. Reconcile the original operation
before retrying; a new key does not make an uncertain build safe.
Request cancellation is different:
it propagates cancellation without SDK fallback, whether it arrives before
launch or during an engine read/build. Nothing routes through the engine unless
the registry marks the capability `Parity`.

### KB versions: the active Design model

A Knowledge Base with versions stores one Design model per version (trunk,
frozen versions, development branches); the SDK serves the Design model of the
version its session has active. Before every engine query or build the Gateway
asks the session KB's Worker for that version (`kbversion ActiveModel`, answered
off the SDK thread from a snapshot taken at KB open and after `list`, `freeze`,
`branch`, `set_active` and `revert`; an engine call never starts a Worker or waits
for SDK startup)
and passes `--design-model <id> --kb-version <id>`. The engine then:

- serves a query from the named Design model after checking that the model's
  stored `KBVersion` property names that version;
- serves a build only for the trunk Design model (KB version 0, model 1), also
  when the KB has other versions (issue #637). Every fact a build reads is read
  under that model and the environments below it: attributes, tables and keys,
  subtype groups, languages, model properties and the theme come through the
  model's own `ModelEntityVersion` rows (never the latest `EntityVersion`), so a
  newer fact of another version cannot reach the trunk build. `TranslationMessage`
  and `DesignSystemClass` entities are held by no model and are read at their latest
  version; a versioned KB where one of them stores several versions is refused
  (`version_scope`) instead of guessed. Building a frozen version or a branch is
  refused: no SDK build of one has been compared, and the `NetModel` files a build
  reads (`GXCFG.WEB`, `Directory.build.props`, `GxWebStd.cs`) belong to the KB
  folder, not to a version. Those builds, and any build with another version
  active, run on the SDK;
- without a named version, serves only a Knowledge Base with a single Design
  model.

Anything else is a version-scope refusal (exit 4, diagnostic category
`version_scope`) raised before any object is read or output written, so the call
follows `Engine.Fallback` like any call outside the admitted space: the SDK
serves it, or `EngineOperationOutsideAdmittedSpace` refuses it. A build refused
this way needs no reconciliation. v4.0.0 always read Design model 1, so with a
branch active it answered queries and builds from the trunk without an error.

Verified on a disposable copy of `KBTeste` with a frozen version and a branch:
the Worker reported version 0 / model 1 for the trunk and version 2 / model 4
with the branch active (matching the stored `KBVersion` properties), queries
were served from the named model, and a build with the branch active was refused
before the engine read anything. Query results of the two models were identical
on that copy, so the model switch itself is covered by unit tests, not by a
content difference observed live.

Builds sample the active version only after acquiring the engine build gate.
After execution, an observed version change or an unavailable recheck of a known
snapshot makes the outcome uncertain: reconciliation is required and the SDK is
not replayed. This start/end identity check does not detect a version that changes
and returns to the original during the build; full concurrent workflow parity is
not established. Successful `list` refreshes and recaptures publish atomically;
failed observations clear the snapshot. KB open and mutating version operations
still clear it before changing scope, so concurrent readers can see unknown.
Snapshot RPCs reserve the already-ready Worker by identity, never acquire or
respawn one, and bound both send and response to one two-second budget.

#### Open check for trunk builds of a KB with versions (issue #637)

The trunk build reads its facts through the model's `ModelEntityVersion` rows
(`src/gx-engine/src/udm/client/model_pin.rs`). It was compared with the SDK on a
disposable copy of `KBTeste` with three Design models (346/346 trunk objects
generated and compiled, output identical to the unversioned KB). A second
disposable KB with two Design models (read-only `SELECT`s, 2026-10-23) showed
that `ModelEntityVersion` of the trunk model listed 72 of its 237 Attributes and
none of the Groups, `DesignSystemClass`, `TranslationMessage` or
`Udm.Types.Property` entities, while `ModelEntityHistory` kept one history per
model for Attribute, Group, Table, Domain, SDT, Image, DesignSystem and Language.
How the pinned reads behave on a KB like that one, and on a branch that edits an
attribute, a table key or a stored property, has not been compared with the SDK,
and whether an incomplete fact set is refused (`version_scope`) rather than
emitted is not established. Until such a KB is compared, treat trunk builds of a
versioned KB as verified only on the `KBTeste` copy.

Stored model properties are decoded as full XML, including formatted integer
values. Missing `KBVersion` (trunk default) is distinct from duplicated, malformed
or non-scalar metadata, which refuses the operation. Legacy catalog commands
(`kb-check`, `kb-emit`, `kb-build`, `kb-run`) accept a native KB directory or its
connection file only when one Design model exists; they bind that model's actual
id. Their catalog-load refusals use the existing input-error diagnostic, not the
Gateway's structured exit-4 replay contract. Portable JSON catalogs are unchanged.

### `genexus_read` → `kb-read`

Admitted: single-part reads where `part` is one of `Source`, `Events`, `Rules` or
`Conditions`, for object types `Procedure`, `WebPanel`, or `Transaction`. The
engine queries the active model directly via native `kb-read`, calculates the SDK
parity `versionToken` matching `ObjectSaved` / `OutputTypeId=301`, projects the
source content with line/byte pagination, and returns the standard MCP read
envelope (`parts`, `versionToken`, `modified`, `readMode: "single-part"`)
in sub-5ms without waking the 32-bit STA Worker COM process.

Outside: multi-part reads (`parts[]`), non-text parts (`Variables`, `Structure`,
`Layout`, `WebForm`), objects other than Procedure/WebPanel/Transaction, or
requests without an active Worker model snapshot. No read shape is currently admitted
for these non-text or multi-part requests; they fall back transparently to
the SDK (or fail closed under `Engine.Fallback=error`).

The standalone `kb-read` CLI is also available for direct command-line inspection;
see [`udm_storage_notes.md`](udm_storage_notes.md).

### `genexus_query` → `kb-query`

Admitted: one bare `type:X` filter, or `typeFilter` without a `type:` term.
Calls must explicitly select `fields` limited to `name` and `type`, the only row
fields supplied by the engine. `fields` is an array of strings in the tool schema. A
JSON array sent as text (`["name","type"]`) is not admitted to the engine: the call
stays on the SDK, whose compact projection does read it as that array. A supplied `limit` must be 1..200 (default 50).
`exactMatch` has no effect for a query without a name term. A nonempty external
`typeFilter` must have no leading/trailing whitespace: the SDK preserves it
verbatim, so normalization would change its match set. Type tokens use the
alias table in `EngineQueryContract`; unsupported spellings defer. `webpanel` and
`wp` also defer: the native catalog includes objects absent from the SDK's
WebPanel result set. A fresh, settled SDK index is required before comparing
result sets; a ready label during active indexing is not parity evidence.

Outside: reads, empty results, all object-name/text searches (including
`exactMatch:true`), `usedby:`, wildcards/quoted/qualified names, and unsupported
filters/paging/federation arguments. The SDK's exact-name branch uses a different
envelope and limit semantics, so it stays on SDK. Default query projections also
require identity fields (`path`, `lastUpdate`); without explicit supported
`fields`, queries stay on SDK. Missing identity fields are never silently
substituted. `usedby:` stays on SDK because its result unions same-named targets
and SDK-only call/table edges. Empty engine answers are not authoritative for
the SDK's broader catalog and therefore defer as well.

Shape: projected `results[]`, counts, `hasMore`, `pagination` including
`nextOffset` when another page exists, and `_meta.match_quality`. There is no top-level `truncated` or
`suggested_next`. Modules sort first, followed by ordinal name order. Focused
stub-engine tests prove admission and envelope guards, not live SDK result/text
equality or full search parity. Unsupported calls use SDK unchanged or fail
closed with `EngineOperationOutsideAdmittedSpace` under `Engine.Fallback=error`.

### `genexus_edit` → `kb-write` (held outside the served space)

Not served today. `genexus_edit` is on the deferral policy (no capability record
declares it as a consumer), and `UdmWriter::save_object` mutates the engine's
in-process store only: it issues no SQL, so an engine-served edit against a live Knowledge Base would report a save
that is lost when the process exits. Every call is therefore outside the space:
`Fallback=error` refuses it with `EngineOperationOutsideAdmittedSpace` naming the
reason, and `Fallback=sdk` serves it with the SDK unchanged. `gx-engine kb-write`
independently refuses a live client for the same reason.

When live persistence lands (version bump plus part replacement through the same
composition structure, verified by an independent read-back), the intended v1
space is one existing object by `name`, one `part` (default `Source`), the full
replacement text in `content` (an empty string clears the part), an optional
`type` checked against the stored kind before any write, and `mode` absent or
`full`. `patch`/`ops`/`scope`, `parts[]`/`targets[]`, `dryRun`, version
tokens/change sets, validation and verification modes, object creation and
`return_post_state` would remain outside it.

### `genexus_lifecycle` `build`/`rebuild` → `rebuild-all`

Admitted: `action=build|rebuild` with the blocking contract —
`wait_until_done=true`, or an `estimated_seconds` the Gateway itself would run
synchronously. Builds are serialized inside the Gateway process. Cancellation while
waiting for that gate is pre-dispatch; after engine launch it does not imply rollback.
A keyed request cancelled before dispatch leaves its idempotency key reusable.
Cancellation after dispatch retains the unknown-outcome fence: inspect the operation
and generated outputs before deciding whether another build is safe.

A directed compile-only build is admitted with `action=build`, a simple or
module-qualified `target` name, `mode=compile_check`, explicit `callers=false`,
and `includeCallees` omitted or `transitive`. The same blocking intent is required.
It uses `--object <name> --closure`: the engine builds that object and its
transitive dependency closure, not unrelated objects in the KB. Output is isolated
at `<KB>/.gx-engine/build-targets/<SHA-256-of-target>`. The answer identifies
`engine.scope=target-closure` and `engine.target`; whole-KB answers identify
`engine.scope=whole-kb`. A successful target build does not imply whole-KB success.

```json
{"action":"build","target":"MyProcedure","mode":"compile_check","callers":false,"wait_until_done":true}
```

Outside: the bare build/rebuild (the SDK path answers it with an accepted async
job; the engine has no job registry), `wait_seconds`, other directed builds,
targeted `rebuild`, `environment`, other `mode` values, `deploy`,
`skipFullDeploy: false`, `fastIncremental`, `force`, `dryRun`, `callerCap`,
`buildPlanCap`, and `limit`. Directed builds with the default `callers=true`,
non-transitive callees, typed selectors, GUIDs or paths remain on SDK; the engine
must not silently drop caller expansion or approximate a selector.

Shape: `status: "Success"` only for a recognized, error-free complete report
(or the supported legacy pipeline report without a status field), plus `report`
with the engine report projection (`total_ms`, `catalog_load_ms`, phase times, object counts,
`emitted_file_count`, `errors`, `warnings`, and the `status`, `generated_count`,
`compiled_count`, `skipped_count`, `skipped_objects`, `not_generated_count`,
`not_generated_objects`, `refused_count` and `refused_objects` fields).

A run the engine reports as `partial` (exit 3: web UI objects skipped, a code
kind with no generator, or a project that did not build) returns
`status: "Partial"`, `error: "EngineBuildPartial"` and `isError=true`. It is
never `Success`: the report lists what was left out, and the message gives the
counts. A build whose report carries errors returns `status: "Failed"` with
`isError=true` and the report attached — that is the build's answer, not
an engine outage, so it is surfaced even under `Fallback=sdk`. An exit code of zero
does not override a partial report or reported errors. If handling a returned build
report throws, the Gateway returns an engine error, not an SDK replay of the build.
An empty JSON object, unknown status or malformed errors field is not a build
report: it requires reconciliation, not a success response or SDK replay. Such
unknown outcomes carry a reconciliation hint, not advice to change fallback and
retry blindly.
`catalog_load_ms` measures loading before the pipeline and is not included in
`total_ms`; compare both phases and the outer request wall time when profiling.
Whole-KB `build` and `rebuild` both run the full pipeline; `rebuild` adds `--clean`
to the output directory (`<KB>\.gx-engine\build`). `--clean` removes every generated
file and build output and forces a full compile, but keeps `obj/` as `dotnet clean`
does (restore outputs and the kept compiler command, each checked against its key).
A `build` whose generated files and compiler inputs are all unchanged skips the
compile (`compile path: csc-direct (up to date)`); a web class that differs only in
its generation stamp keeps its earlier stamp. Directed `compile_check` builds
run the same pipeline only on the admitted target closure.

### Documented v1 deviations

- No `versionToken`/post-state on reads or edits, and no concurrency tokens.
- Query results are name-sorted, exact-only, and carry no SDK-only identity
  fields (`guid`, `path`, `lastUpdate`).
- An engine-served read does not confirm SDK write-recovery fences; a fence
  left by an uncertain SDK write still clears on an SDK read.
- A semantic-cache hit can answer a routed read with an SDK-shaped payload
  cached before the switch; engine results are not written to that cache.
- Live reads are `sqlcmd`-backed and list only the twelve servable object entity
  types: `ModelEntityVersion` also contains object parts and KB-tree nodes, which
  are filtered out ([notes](udm_storage_notes.md)).
- `rebuild-all` hydrates the full servable catalog. Native catalog snapshots remain
  disabled: row counts and timestamps alone do not safely identify content edits.
  Measure catalog loading separately rather than inferring whole-KB speed from a
  warm compiler cache.
- No engine write is persisted to live storage; `genexus_edit` stays outside the
  served space until an independent read-back verifies a live write.

## Executable discovery

Order: `Engine.Executable`/`GXMCP_ENGINE_EXE` (explicit; missing →
`EngineNotFound`, never a different binary), the Gateway directory, `publish/`,
the checkout `src/gx-engine/target/release` then `.../debug`, the current
directory layouts, then `PATH`. This mirrors the gxwire locator
(`LocateGxwireExecutable`). Build the engine for a checkout with:

```powershell
cargo build --release --manifest-path src/gx-engine/Cargo.toml
```

`publish/gx-engine.exe` only exists when packaging copies it; the experimental
switch is a checkout/operator path today, not a shipped artifact.

## Adding a routed capability

1. Measure the native differential inside the admitted space; record the
   evidence in `plans/129-implementation-progress.md` and the capability matrix.
2. Move/add the entry in `EngineCapabilityRegistry`: status `Parity`, evidence
   path, and the consumer operation key. Routing follows the registry — the
   gateway maps a tool call to its operation key and looks up which capability
   declares that consumer.
3. Route the call site through `EngineRouter.Decide` — never invoke the engine
   from a tool path directly.
4. Add tests for the routing decision, the engine path, and the fallback/error
   behavior; mutation-check each guard (revert it, confirm red, restore).
5. Update this page, the CHANGELOG and the doctor view.

## Safety rules

- Fail closed. The engine may only serve what was measured; nothing is
  approximated or silently delegated mid-operation.
- `Fallback=error` is the experiment mode that proves the engine carried the
  whole call instead of the SDK.
- A write may only route once its persistence is verified end to end (engine
  write, then an independent read-back); until then `genexus_edit` stays outside
  the engine space.
- Native KB object access is served through the engine's own UDM storage
  adapter (`udm_storage_access`, a bounded live read; partial, no consumer; read/edit deferral is separate policy); a generic native
  KB decoder (`native_kb_read`) is not established, and the engine never
  claims an SDK-only surface.
- Raw SDK exports and private KB data never enter Git; engine evidence lives as
  the recorded differentials, fixtures and hashes already committed under
  `src/gx-engine/tests` and the ledger.
- `genexus_wire` (gxwire) is a separate Rust binary for file-tree indexing; this
  switch is about the compiler/runtime replacement, not the indexer.
