# UDM physical storage notes

Working reference for the `gx-engine` `udm_storage_access` capability: how a
native GeneXus Knowledge Base stores objects, parts, blobs and references, and
what the engine can and cannot serve from that storage. Everything below was
observed on a real GeneXus 18 Knowledge Base (`C:\KBs\KBTeste`, LocalDB) with
`sqlcmd`; every claim includes the query or code path that produced it. This is
not a GeneXus specification — it is the evidence the engine's admitted space is
built on. Re-verify with the commands in [Reproduce](#reproduce) before relying
on a number.

Related documents: [`engine_backend.md`](engine_backend.md) (the routing gate and
served operations), [`technical_architecture.md`](technical_architecture.md) (the
Gateway/Worker runtime). Engine code:
[`src/gx-engine/src/udm/`](../src/gx-engine/src/udm/).

## How a Knowledge Base resolves its database

`<KB>\knowledgebase.connection` is an XML document:

```xml
<ConnectionInformation>
  <DBName>GX_KB_KBTeste</DBName>
  <IntegratedSecurity>True</IntegratedSecurity>
  <ServerInstance>(LocalDB)\MSSQLLocalDB</ServerInstance>
  <CreateDbInKbFolder>True</CreateDbInKbFolder>
  <DataFile>GX_KB_KBTeste.mdf</DataFile>
  <LogFile>GX_KB_KBTeste_log.ldf</LogFile>
  <DetachOnClose>False</DetachOnClose>
</ConnectionInformation>
```

`udm/connection.rs` parses it into `KbConnectionInfo`; `UdmClient::connect_kb`
builds a live client from it. The `.mdf` lives in the KB folder and the database
is attached to the named SQL Server / LocalDB instance, so reads go through
`sqlcmd` and no KB file is parsed directly.

## Tables the engine reads

| Table | Contents | Engine use |
| :--- | :--- | :--- |
| `ModelEntityVersion` | one row per **entity version in the model** — top-level objects **and** their parts and KB-tree nodes (`ModelId`, `EntityTypeId`, `EntityId`, `EntityVersionId`, `ModelEntityVersionName`). | Object listing and identity. |
| `EntityVersion` | the blob payload for one `(EntityTypeId, EntityId, EntityVersionId)` in `EntityVersionData`. | Part content (the 11-byte envelope below). |
| `EntityVersionComposition` | `Compound*` (object) → `Component*` (part) links. | Which parts an object has. |
| `ModelCrossReference` | `ModelId`, `From*`, `To*`, `ReferenceType`. | `usedby:` query filtering and catalog references. |
| `EntityType` | authoritative `EntityTypeId` → `EntityTypeName`, plus `EntityTypeIsModelable` / `EntityTypeIsInternalNode`. | Object-kind mapping. |

## `ModelEntityVersion` contains parts, not only objects

This is the single most misleading fact about the table. On KBTeste
(`ModelId = 1`) there are **3,701 rows**, and the largest groups are not objects:

| EntityTypeId | EntityTypeName | Rows |
| ---: | :--- | ---: |
| 95 | Documentation | 668 |
| 152 | SDTItemEntity | 361 |
| 98 | Help | 288 |
| 102 | Rules | 224 |
| 105 | Variables | 224 |
| 89 | Conditions | 203 |
| 38 | Procedure (object) | 200 |
| 100 | Procedure (source part) | 188 |
| 118 | Layout | 188 |
| 27 | Attribute (object) | 61 |
| 47 | WebPanel (object) | 15 |
| 43 | Transaction (object) | 8 |

Proof that those rows are parts and not objects: for the procedure
`AddDeviceGroups` (`EntityTypeId = 38`), `EntityVersionComposition` links it to
components `95/228`, `102/2` and `105/2` — Documentation, Rules and Variables —
and **each component has its own `ModelEntityVersion` row**:

```
ComponentEntityTypeId  ComponentEntityId  InMev  TypeName
95                     228                1      Documentation
102                    2                  1      Rules
105                    2                  1      Variables
```

Note also the name collision: entity type **38** is the *Procedure object*;
entity type **100** is its *source part* and is also called `Procedure` in
`EntityType`.

Two consequences:

- Listing every `ModelEntityVersion` row as an object mislabels hundreds of
  parts as `Procedure` (before this was fixed, `kb-query --type Procedure` on
  KBTeste returned attribute names like `access_token` with `total: 3377`).
- `EntityTypeIsModelable` does **not** separate objects from parts: every one of
  the 3,701 rows has `EntityTypeIsModelable = 1` and
  `EntityTypeIsInternalNode = 0`. The engine therefore filters by an explicit
  servable-object type list.

A live-derivable rule — "an entity that is never a `Component*` of another
entity is a top-level object" — yields a sensible set (187 procedures, 61
attributes, 15 web panels, 14 SDTs, 14 domains, 8 tables, 8 transactions, 6
modules, …) but still includes IDE metadata such as `ThemeClass` (129 rows),
`FormClass`, `GlobalWorkflowProperties`, `DebugData`, and KB-tree nodes
(`Folder`, `Category`). Attributes are never components (`SELECT COUNT(*) FROM
EntityVersionComposition WHERE ComponentEntityTypeId = 27` → `0`), so they are
top-level, as the IDE lists them.

## Object and part kinds are resolved by name

The engine holds no fixed `EntityTypeId` numbers. `udm/entity_types.rs` resolves
every object and part kind from the Knowledge Base's own `EntityType` table, by
`EntityTypeName` and `EntityTypeNamespace`, once per live client. The ids differ
per GeneXus version, so a fixed table answers wrong on some version. The values
below are the `EntityType` rows of the disposable KBs (GX18 `KBTeste`, GX17
`KBTeste17`, GX16 `KBTeste16`). A kind a KB holds no rows of (for example the GX16
Attribute, since `KBTeste16` has Domains only) has no data to confirm its id, so
that cell is a reference, not a check. The engine never uses this table: it
resolves every id at runtime from the KB's own vocabulary.

| Object kind | Name / namespace | GX18 | GX17 | GX16 |
| :--- | :--- | ---: | ---: | ---: |
| Procedure | Procedure / Objects | 38 | 31 | 31 |
| Transaction | Transaction / Objects | 43 | 36 | 36 |
| WebPanel | WebPanel / Objects | 47 | 40 | 40 |
| SDT | SDT / Objects | 40 | 33 | 33 |
| DataProvider | DataProvider / Objects | 29 | 22 | 22 |
| DataSelector | DataSelector / Objects | 30 | 23 | 23 |
| DataView | DataView / DataViews | 31 | 24 | 24 |
| Attribute | Attribute / Attributes | 27 | 20 | 20 |
| Domain | Domain / Domains | 33 | 26 | 26 |
| Table | Table / PhysicalModel | 41 | 34 | 34 |
| Module | Module / Objects | 12 | 17 | 17 |
| DesignSystem | DesignSystem / Themes | 57 | 50 | 145 |

A name alone is not a key: the namespace decides it. `Menu` is 37 in `Objects` and
102 in the empty namespace on GX16. Each kind's name and namespace must match, so a
part is never read as an object. Every other entity type is not an object the
engine lists or reads.

Part kinds follow the same rule. The engine serves these parts (empty namespace,
GX18 / GX17 / GX16):

| Part | GX18 | GX17 | GX16 |
| :--- | ---: | ---: | ---: |
| Procedure source (`Source`) | 100 | 88 | 81 |
| DataProviderSource (`Source`) | 99 | 87 | 80 |
| Rules | 102 | 90 | 83 |
| Events | 97 | 85 | 78 |
| Conditions | 89 | 77 | 70 |
| Variables | 105 | 93 | 86 |
| Structure | 104 | 92 | 85 |
| WebForm | 107 | 95 | 88 |
| PatternInstance | 78 | 130 | 119 |

A `Procedure` object's source is part 100 (GX18) or 81 (GX16); a DataProvider's
source is `DataProviderSource`. Other part entities (`Documentation`, `Help`,
`WinForm`, `Layout`, `Xml`, design tokens, SDT levels and items, `FormClass`) have
no engine part kind. The engine does not decode them and does not list them in
`availableParts`: a read for one of them reports the part as missing instead of
pretending it was served. A composition that names an unserved part is skipped.
A composition that names a served part with no `EntityVersion` row is an error.

## Stored Transaction key inheritance

In KBTeste, `Plan070TrnId` (Attribute 11, served version 3) stores
`Class=PRIMARY` and `SuperTypeKey=12`, but no `ATTCUSTOMTYPE`.
`Plan071TrnId` (Attribute 12) supplies its stored `NUMERIC(4,0)` type.
The reader follows served rows only; a missing or untyped target never falls
back to an older row or a default type. Missing/non-key targets, cycles, and a
row containing both its own type and a link are refused. Multi-hop traversal
has synthetic tests, not an additional native SDK comparison.

The decoded Transaction records a `key_references` link. Physical derivation
requires exactly one derived table whose complete primary key is the target
attribute, with the same type. Missing, ambiguous, cyclic, or duplicate targets
refuse the referencing Transaction; dependent references are checked again.
The root table receives the foreign key, without an extra secondary index.
The DDL library creates referenced tables before their dependents and refuses
missing targets or cross-table cycles. It has no production caller: `rebuild-all`
neither emits nor applies that DDL. Key-existence checks are not generated;
Transactions with key references carry an explicit warning about these limits.
The FK remains in-memory metadata, not observed database enforcement. No DDL is
executed by these tests.

Reader wiring, stale-row refusal, decoding and physical derivation are covered
by `native_transaction_structure`; `table_ddl` tests dependency order across
all four supported dialects. Main's KBTeste engine-only validation removed the
two Plan070 errors, but Plan070 remains skipped as web UI, not compiled. This is
not SDK parity, generated UI/BC existence-check parity, or a physical database
constraint check.

## Blob envelope

`EntityVersionData` is a binary blob. Its layout depends on the flag at offset 6:

| Offset | Bytes | Meaning |
| :--- | :--- | :--- |
| 0..3 | `01 02 03 04` | magic |
| 4..5 | `00 01` | version |
| 6 | `01` = GZip / `02` = uncompressed UTF-8 | compression flag |
| 7..10 | `u32` little-endian, flag `01` only | uncompressed length |
| 11.. | flag `01`: RFC 1952 GZip stream | payload |
| 7.. | flag `02`: UTF-8 text, no length field | payload |

A flag-`02` blob has no length field: the text starts at offset 7. Reading it from offset 11
dropped its first four characters (for example `<?xm` of a GeneXus XML part). This was found on the
stored `SDTStructure` rows (`01 02 03 04 00 01 02 3C 3F 78 6D 6C …` is `<?xml …`) and is verified on
all 242 `SDTStructure` rows of KBTeste, which are 155 gzip and 87 uncompressed, with the magic on every row.

`udm/decoder.rs` validates the header, decompresses, checks the declared length,
and rejects an unknown flag. Tokenized parts (`Source`, `Rules`, `Events`,
`Conditions`) store XML `<TokenDataList>`; `extract_token_data_list` reconstructs
the plain GeneXus source the user sees in the IDE (verified on
`PCalculaDesconto`, whose `Source` decodes to the expected `if/else` block).

## The sqlcmd driver and the 256-character truncation bug

`udm/live.rs` reads through `sqlcmd` (ODBC build 15.0.1300.359 on this machine).
Two behaviors are load-bearing:

- **Default variable-length display width is 256 characters.** Fetching a blob
  as hex (`SELECT CONVERT(VARCHAR(MAX), EntityVersionData, 2)`) with the default
  flags returned **259 characters including headers** for a 201-byte value that
  needs 402 hex characters; with `-h -1` alone the value itself came back cut at
  exactly 256. The engine used to decode that cut stream and failed on real KBs
  with `GZip decompression failed: incomplete deflate stream`.
- **`-y 0` removes the limit, but is mutually exclusive with `-W` and `-h -1`**
  in this build (`Sqlcmd: The -W and the -y/-Y options are mutually
  exclusive.` / `Sqlcmd: The -h and the -y 0 options are mutually exclusive.`).
  The driver now passes `-y 0` alone and trims fields itself. Measured:

  | Flags | 201-byte blob | 5,316-byte blob |
  | :--- | ---: | ---: |
  | default | 259 chars (cut) | — |
  | `-h -1` | 256 chars (cut) | — |
  | `-h -1 -y 8000` | 402 chars (exact) | — |
  | `-y 0` | 402 chars (exact) | 10,632 chars (exact) |

  A hex column with an odd number of digits is rejected by
  `decode_hex_column` rather than decoded short, so a future display-width
  regression fails loudly instead of surfacing later as a corrupt part.

`sqlcmd` remains the default and the only transport measured in these notes. An
opt-in alternative (`udm/tds`, `GXMCP_ENGINE_SQL_TRANSPORT=tds`, cargo feature
`tds`) reads the same statements over TDS and the part payloads as `varbinary`
bytes; it has not been run against a real server yet
(`scripts/validate-engine-tds-live.ps1` is how it gets verified).

## Fail-closed reads

The live read helpers used to swallow a `sqlcmd` failure (`if let Ok(raw) = …`)
and fall through to the empty in-memory cache. A `kb-query` against a database
the process could not read therefore answered `{"objects":[],"total":0}` with
exit code 0 — a false empty result. `get_active_objects`, `get_compositions`,
`get_entity_version` and `get_cross_references_for_model` now propagate the
error; a live read either succeeds or fails, never degrades to "nothing".

## Cross-references

`ModelCrossReference` is read once per model
(`get_cross_references_for_model`) and cached in the client, so `usedby:`
filtering costs one query and per-object hydration stays in memory. On KBTeste
`--usedby PCalculaDesconto` returns `Test_PCalculaDesconto`, matching the
`ModelCrossReference` rows.

`udm/reader.rs` maps `ReferenceType` → `ReferenceKind` as `1 = Calls`,
`2 = Uses`, `3 = BasedOn`, `4 = Contains`, anything else → `Uses`. That mapping
comes from the engine's own catalog vocabulary; it has not been verified against
IDE semantics.

## Write path status (important)

`UdmWriter::save_object` mutates the **in-process store only** — it issues no SQL.
Against a live Knowledge Base an engine write would be reported as saved and be
lost when the process exits. Two guards make that impossible to miss:

- `gx-engine kb-write` refuses when the client is live:
  `object '<name>' resolves to a live Knowledge Base database and engine UDM
  writes are not implemented for live storage; refusing a write that would not
  persist`.
- The Gateway holds `genexus_edit` outside the engine space
  (`EngineRoutedOperations.cs`): `Fallback=error` refuses the call, `Fallback=sdk`
  serves it with the SDK. The capability stays registered, but no engine edit is
  ever reported as a save.

In-memory mode (tests, catalog tooling) still writes normally.

## Measured read performance

| Operation | Work | Time on KBTeste |
| :--- | :--- | ---: |
| `kb-query --type Procedure --limit 5` | one `sqlcmd` for all `ModelEntityVersion` rows + Rust filtering | 85–151 ms |
| `kb-read PCalculaDesconto --parts Source` | MEV + compositions + one blob | ~117 ms |

These operation timings are end-to-end observations, not measurements of
`sqlcmd` startup alone. The former per-part process path is historical:
`UdmReader::read_all_objects` now bulk-prefetches compositions and part payloads,
then reads model cross-references and the system root. Procedure generation
flags are read at most once. A catalog with Procedures therefore takes roughly
seven fixed live queries, not one invocation per object part.

A recorded GX18 scratch run reports about 704 ms `catalog_load_ms`; this shows
catalog loading matters, not that process startup dominates. Before changing
transport, measure query count and cumulative `execute_sqlcmd` time against
`catalog_load_ms` on an unchanged disposable KB. A single-batch SELECT comparison
must preserve authentication/output flags and match row counts and payload
digests; any savings combine process and connection costs. No direct-client or
batched-connection performance gain has been verified.

A later bounded attribution run on candidate `6fe426f3` measured three alternating
pairs on KBTeste with `--skip-compile`. A disposable numeric-only sqlcmd proxy
observed exactly seven calls per wrapped run. Median cumulative delegated
process/connection/query time was 2,208.765 ms, versus 2,525 ms catalog load;
median wall time was 2,653.42 ms direct and 2,666.41 ms wrapped. Call four alone
had a 1,571.755 ms median; its SQL stage has not been independently identified.
This does **not** isolate startup, connection or query/transfer costs. All six
runs produced the same 502-file tree digest; non-timing reports matched after
normalizing only parallel warning/navigation-report ordering (duplicates kept).
The KB still has refused objects and no compilation occurred: this is transport
attribution, not a successful full build, a speedup, or SDK parity. Local evidence:
`scratchpad/sqlcmd-timing-wrapper-v1/measurement-kbteste-6fe-v3/result.json`.
Two failed harness setups are preserved separately and excluded.

## Reproduce

```powershell
$S = '(LocalDB)\MSSQLLocalDB'; $D = 'GX_KB_KBTeste'; $T = "`t"

# entity types and their names
sqlcmd -S $S -d $D -E -h -1 -y 8000 -s $T -Q "SET NOCOUNT ON; SELECT EntityTypeId, EntityTypeName FROM EntityType ORDER BY EntityTypeId"

# what ModelEntityVersion actually contains
sqlcmd -S $S -d $D -E -h -1 -y 8000 -s $T -Q "SET NOCOUNT ON; SELECT EntityTypeId, COUNT(*) FROM ModelEntityVersion WHERE ModelId = 1 GROUP BY EntityTypeId ORDER BY COUNT(*) DESC"

# parts of one object (replace the name)
sqlcmd -S $S -d $D -E -h -1 -y 8000 -s $T -Q "SET NOCOUNT ON; SELECT c.ComponentEntityTypeId, c.ComponentEntityId, (SELECT COUNT(*) FROM ModelEntityVersion m WHERE m.ModelId = 1 AND m.EntityTypeId = c.ComponentEntityTypeId AND m.EntityId = c.ComponentEntityId) AS InMev FROM EntityVersionComposition c WHERE c.CompoundEntityTypeId = 38 AND c.CompoundEntityId = (SELECT TOP 1 EntityId FROM ModelEntityVersion WHERE ModelId = 1 AND ModelEntityVersionName = 'AddDeviceGroups') AND c.CompoundEntityVersionId = (SELECT TOP 1 EntityVersionId FROM ModelEntityVersion WHERE ModelId = 1 AND ModelEntityVersionName = 'AddDeviceGroups')"

# blob as exact hex (compare with 2 × DATALENGTH)
sqlcmd -S $S -d $D -E -y 0 -s $T -Q "SET NOCOUNT ON; SELECT CONVERT(VARCHAR(MAX), EntityVersionData, 2) FROM EntityVersion WHERE EntityTypeId = 89 AND EntityId = 655 AND EntityVersionId = 1"

# engine product surface
.\publish\gx-engine.exe kb-query C:\KBs\KBTeste --type Procedure --limit 5
.\publish\gx-engine.exe kb-read C:\KBs\KBTeste PCalculaDesconto --parts Source
.\publish\gx-engine.exe kb-query C:\KBs\KBTeste --usedby PCalculaDesconto --limit 10
```

## Generated SDT JSON runtime checks

Generated SDTs register field values with the GeneXus runtime JSON writer through
`ToJSON` overrides. Without these overrides, a compiled collection could return
items with no fields in `ToJson()` despite holding the correct in-memory values.
Uninitialized child levels stay absent; serialization does not force their lazy
getters. Date and DateTime values use the field-level GeneXus wire shape, including
empty dates.

`tests/csharp_collection_runtime.rs` compiles a synthetic emitted Procedure and SDT
against GeneXus .NET runtime 1.30.0, then executes an output collection and a source
`ToJson()` expression. It checks item Clone isolation, fresh outputs on repeated
calls, numeric/text/Boolean/Guid/date values, initialized nested levels and
collections, and JSON deserialization. It opens no KB or datastore and runs no SDK
generator. This is a real-runtime regression, **not a fresh SDK parity oracle**.
REST/XML wrappers, image/media wire semantics and Geospatial JSON remain unverified.

## SDT item mapping (stored type to C# type)

An SDT's items are read from its `SDTStructure` rows (entity type 103 on the GX18 KB, 84 on
the GX16 KB; the node is the `SDTItemEntity`, 152 on GX18 and 141 on GX16). Each item's C# type comes
from its stored `ATTCUSTOMTYPE` (a `DataType` code), or from the domain it is based on
(`idBasedOn`, a GUID of the form `00972a17-9975-449e-aab1-d26165d51393-<domain id>`).

Each row names its evidence. The domain rows were checked in the latest round against the SDK's
structured form of the SDTs and Procedure variables that use them, read-only (`genexus_inspect
include=["structure"]` for SDTs, `genexus_read part=Variables` for variables). The other rows rest on the
earlier segments' checks of the SDK text (`genexus_read part=Structure`). Not re-checked in the latest
round: type codes 6, 12, 15, 254, 7, 8, 45, 102, 103, 104, and `ATTCUSTOMTYPE` 4 with a `Length`.
`NUMERIC(n)` in the SDK text is the stored `Length`; `CHARACTER(n)` and `VARCHAR(n)` are the stored length.

| Stored form | SDK text | C# type | Evidence |
| :--- | :--- | :--- | :--- |
| `ATTCUSTOMTYPE` 4, `Length` n | `NUMERIC(n)` | `int` / `short` / `decimal` by digits and scale | the stored-type map; the SDK text shows NUMERIC(n) for these rows |
| no `ATTCUSTOMTYPE`, no `idBasedOn`, `Length` n | `NUMERIC(n)`, e.g. `Delivery.Expiration : NUMERIC(18)` | `NUMERIC(n)` | SDK text: `Delivery.Expiration`, `TrackingParameters.ChangesInterval : NUMERIC(8)` |
| no `ATTCUSTOMTYPE`, no `idBasedOn`, no `Length` | `NUMERIC(4)`, e.g. `GeolocationProximityAlert.Radius : NUMERIC(4)` | `NUMERIC(4)` | SDK text: `GeolocationProximityAlert.Radius : NUMERIC(4)`; applied to 3 stored SDT items (lcG18p: 3 SDTs no longer refused) |
| `ATTCUSTOMTYPE` 5, no `Length` | `CHARACTER(20)`, e.g. `AnalyticsPurchase.CurrencyCode : CHARACTER(20)` | `CHARACTER(20)` | SDK text: `AnalyticsPurchase.CurrencyCode : CHARACTER(20)`; applied to SDT items in lcG18p |
| `ATTCUSTOMTYPE` 13, no `Length` and no `AttMaxLen` | `VARCHAR(40)`, e.g. `AnalyticsKeyValue.Key : VARCHAR(40)` | `VARCHAR(40)` | SDK text: `AnalyticsKeyValue.Key : VARCHAR(40)`; applied to SDT items in lcG18p |
| `ATTCUSTOMTYPE` 13, `AttMaxLen` only (no `Length`) | not evidenced | refused by name | no SDK row with this shape |
| `ATTCUSTOMTYPE` 6 | `DATE` | `Date` | stored type map |
| `ATTCUSTOMTYPE` 12 | `DATETIME(n,d)` | `DateTime` | stored type map |
| `ATTCUSTOMTYPE` 15 | `Boolean` | `Boolean`, stored width 0 only | `RSC79223900.Flag` |
| `ATTCUSTOMTYPE` 254 | an SDT | the SDT's class | stored SDT reference |
| `ATTCUSTOMTYPE` 7, 8, 45, 102, 103, 104 | not mapped | refused by name | see the refusal list |
| `idBasedOn` a typed domain (`ATTCUSTOMTYPE` on its Domain row; a Length where the type is variable-length) | the SDK item's resolved type: `Email` VARCHAR(100), `Url` VARCHAR(1000), `Geolocation` CHARACTER(50), `Phone` CHARACTER(20), `ObjectName` VARCHAR(256), `BeaconProximity` NUMERIC(1), `MediaDuration` NUMERIC(12), `EventExecution` NUMERIC(1); `Data` DATE(8) and `IDManual` NUMERIC(18) from variables | the domain's stored type (`stored_variables::domain_data_type`) | each stored Domain row read in full; the SDK's resolved type of an item based on it (`sdtStructure`) agrees on length and kind. Checked on both KBs: `Email`, `Url`, `Geolocation`, `BeaconProximity`. GX18 only: `ObjectName`. GX16 only: `Phone`, `MediaDuration`, `EventExecution`. The variable domains `IDManual` and `Data` are GX18 only |
| `idBasedOn` an enumeration domain (no `ATTCUSTOMTYPE`, no Length) | `PlaybackState` and `StoreProductType`: `NUMERIC(4)` | refused by name | the SDK length is not a stored value: these Domain rows store no Length (inferred; the SDK rule for it is not known) |
| `idBasedOn` an enumeration with a `Length` but no `ATTCUSTOMTYPE` (`MessageTypes`, Length 2) | `Messages.Type : NUMERIC(2)` (GX16) | refused by name | one SDK row agrees, but `SmartDeviceType` stores `Length 1`, `AttMaxLen 1` and no `Decimals` (the stored row, as read in the enumeration census), and the SDK shows NUMERIC(1); the enumeration has no stored type, so it stays refused (see [`udm_enumerated_domains.md`](udm_enumerated_domains.md)): not applied |
| `Decimals` present on an untyped item | not evidenced | refused by name | `Amount` synthetic row; no stored row of this shape |
| collection item (`AttCollection=True`) | a collection | refused by name | collections are not mapped |
| Procedure variable based on a typed domain, with no `ATTCUSTOMTYPE` and no Length, AttMaxLen or Decimals of its own | the domain's type: `&GxObject : ObjectName` VARCHAR(256), `&name : ObjectName` VARCHAR(256), `&link : Url` VARCHAR(1000), `&EmpresaID : IDManual` NUMERIC(18), `&DataLimite : Data` DATE(8) | the domain's type; a `BasedOn` reference to the domain | `genexus_read part=Variables` (structured `variables`) for IsAuthorized, ProcVarDomain and ListPrograms (KBTeste) |
| Procedure variable based on a domain that declares its own Length, AttMaxLen or Decimals | not evidenced | refused by name | which length wins is not evidenced |

A row marked "refused by name" stays refused until a stored row and its SDK text show the form.
Every such refusal names the item, its SDT and the reason, so the Procedure that needs it fails
with the same reason and is never written with a guessed type.

### Refusals, GX18 `KBTeste` and GX16 `KBTeste16` (final bytes, `lcG18q` and `lcG16q`)

| SDT refusal line (one per refused SDT) | GX18 | GX16 |
| :--- | ---: | ---: |
| based on a domain with no stored type (enumerations) | 11 | 10 |
| type code not mapped (7, 8, 45, 102, 103) | 16 | 9 |
| collection item | 7 | 6 |
| no stored length | 1 | 0 |
| duplicate served node id (`Notification`, `Route`) | 0 | 2 |
| needs a refused SDT (dependency) | 3 | 3 |
| **SDT refusal lines** | **38** | **30** |

| Procedure refusal line | GX18 | GX16 |
| :--- | ---: | ---: |
| nested SDT level variable (structure type `a76e9340-bdb9-445d-8f81-cfd4ddd0b0f3`) | 17 | 0 |
| needs a refused SDT (dependency) | 6 | 5 |
| collection variable | 3 | 3 |
| no stored length | 2 | 2 |
| type code not mapped | 2 | 1 |
| no parts in the active version | 1 | 0 |
| **Procedure refusal lines** | **31** | **11** |

The counts come from the `[REFUSED]` reason lines by script (`C:/tmp/t8_strict_shapes.py`); a dependency
line names the SDT it needs. An earlier note gave 60 for GX18 SDT refusals as a header count. That is
wrong: `grep -c` finds 60 `[REFUSED] StructuredDataType` lines in the older runs lcG18j, lcG18k and
lcG18n, which had more refusals. The 60 is not compared with the counts above.

## Stored types and the final live counts (`lcG18t`, `lcG16t`)

The two tables above (`lcG18q`, `lcG16q`) count refusal lines. The table below counts distinct objects,
each under one reason: the first matching reason text in the `[REFUSED]` line (script
`C:/tmp/t9c_reasons.py`). The per-line tables are kept as history.

**Nested SDT levels.** A Procedure variable typed by a nested SDT level (structure type
`a76e9340-bdb9-445d-8f81-cfd4ddd0b0f3`) takes the class the SDK generates for that collection item. The name
is `<SDT>_<item>`, where `<item>` is the stored `idCollectionItemName`, or `<level>Item` when none is
stored. A collection inside a nested level is named under its parent: `<SDT>_<parent>_<item>`. Evidence (SDK
class names and the variable declarations that use them): `RSN03944700_RowsItem` (`RTDX765000A.Row`),
`RCE06829400_Entry` (stored `idCollectionItemName` `Entry`; `RTM49305800.Row`), and
`NativeDeepSdt85299100_Child_NestedCollectionItem` (`RTDA386800E.V`). The SDT that holds a level comes from
the stored STRUCTURE of each SDT, not from names; a level held by two structures refuses the variable. Only
collection items are mapped. On `KBTeste` 16 of 17 nested-level Procedures now generate; `ListPrograms`
(level 181) stays refused because its level is not in the level map.

**Type codes.** A CHARACTER Procedure variable with no stored `Length`, `AttMaxLen` or `Decimals` takes
`CHARACTER(20)`. The SDK reports that for `FilterName` and `FilterValue` on both KBs (four declarations); the
rule for other variables is an inference. Stored codes 7, 8, 45, 102, 103 and 255 stay refused by name, with the
SDK type they print (details in [`udm_type_codes.md`](udm_type_codes.md)).

**Enumerations** (Domain rows with `IDEnumDefinedValues`) without a stored type stay refused; see
[`udm_enumerated_domains.md`](udm_enumerated_domains.md).

| Live `rebuild-all` (release binary of this round) | GX18 `KBTeste` | GX16 `KBTeste16` |
| :--- | ---: | ---: |
| objects | 507 | 152 |
| generated / compiled | 188 / 188 | 16 / 16 |
| skipped (web UI, not generated) | 23 | 6 |
| not generated | 1 (`SidebarItemsDP`, a DataProvider) | 0 |
| refused | 53 | 41 |
| `error CS` | 0 | 0 |

Refused objects by reason:

- GX18: type code not mapped 22; collection 12; based on a domain with no stored type 11; needs a refused SDT
  5; nested SDT level not mapped 1 (`ListPrograms`); no stored length 1 (`RustRecordProbe1015`); other 1
  (`Plan072Proc`: the reader reported no parts at that time; see the current SOURCE rule below).
- GX16: type code not mapped 14; based on a domain with no stored type 10; collection 9; needs a refused SDT 6;
  duplicate served node id 2 (`Notification`, `Route`).

These are historical probe counts, not the current generation policy. A Procedure is skipped only
when its stored `GenerateObject` is explicitly `False`; a missing served SOURCE part is a named
refusal, never evidence that the SDK would skip it. Plan072Proc's active key is compound type 38,
entity 15, version 2: it has zero composition rows. Version 1 has seven, including Source/type 100.
The earlier 14-part claim omitted the compound type and counted unrelated objects with the same
numeric id; it is not a reader defect. Why the SDK reports `nogenspc` remains unverified.

The script (`C:/tmp/t9c_reasons.py`) gives each object the first pattern it matches, in a fixed order (nested level,
collection, needs a refused SDT, and so on). A dependent object whose refusal text also names a collection is
therefore counted under collection. For example, `AddDeviceTargetFilter` (GX18) is refused through a collection item
of the SDT `Configuration`, so it counts under collection, not under the dependency. `AddDeviceTargetFilter` and `RemoveDeviceTargetFilter` on GX16 stop at the SDT `Messages`, whose item
`Type` is based on domain 41, an enumeration without a stored type.

## References

- `src/gx-engine/src/udm/connection.rs` — `knowledgebase.connection` parser.
- `src/gx-engine/src/udm/client.rs` — live SQL reads, hex column decoding, cross-reference cache.
- `src/gx-engine/src/udm/live.rs` — the `sqlcmd` driver and its flags.
- `src/gx-engine/src/udm/decoder.rs` — envelope, GZip, `<TokenDataList>`, part ids.
- `src/gx-engine/src/udm/reader.rs` — object kinds, part listing, object hydration.
- `src/gx-engine/tests/udm_reader_writer.rs`, `src/gx-engine/tests/kb_ops.rs` — regression coverage.
- `docs/engine_backend.md` — what the Gateway routes to the engine today.

### Collections: what the SDK reports, and what is captured

SDK structured reads (`genexus_inspect`) of the GX18 KB, 2026-10-18. The SDK answers the
structure of a collection, not its emitted C#. No emitted C# for any collection form was captured,
so no collection refusal is lifted.

| Object | Member | SDK structured read | Stored form | Status |
| :--- | :--- | :--- | :--- | :--- |
| `FacebookAccessToken` (SDT, EntityId 23) | `Permissions`, `DeclinedPermissions` | `CHARACTER(20)`, `isCollection` true | item with `AttCollection=True` | refused by name: no captured C# |
| `Route` (SDT, EntityId 14) | `advisoryNotices` | `VARCHAR(200)`, `isCollection` true | item with `AttCollection=True` | refused by name: no captured C# |
| `Configuration` (SDT, EntityId 2) | `Properties` | `GX_SDT` `ConfigurationProperty`, `isCollection` true | item with `AttCollection=True` | refused by name: no captured C# |
| `AddDeviceGroups` (Procedure, EntityId 1) | `Groups` (parm `in`) | `CHARACTER(20)`, `isCollection` true | variable with `AttCollection=True`, no stored Length | refused by name: no captured C# |
| `GoogleDirectionsServiceRequest` (Procedure, EntityId 10) | `Routes` (parm `out`) | `GX_SDT` `Route`, `isCollection` true | variable with `AttCollection=True`, SDT type 254 | refused by name: no captured C# |

- The SDK gives `CHARACTER(20)` for `Groups` although its stored variable row has no Length. The
  decoder does not use that value: the variable stays refused.
- A single-object `specifyOnly` build of `FacebookAccessToken` is not a read: it changed the KB (the engine
  handoff records `nav_objs.xml` truncated to 0 bytes). The helper reported that it wrote no generated C#
  under `NetModel/Web` (manifest unchanged), and the build log says Compile and deploy were skipped, so the
  emitted form of this SDT is still not captured.
- Local emitted C# for collections, all found by search:
  - level collections: `GXBaseCollection<level item>` (`NetModel/Web/type_SdtRCE06829400.cs`);
  - a procedure parameter for a level item: `GXBaseCollection<SdtProgramNames_ProgramName>`
    (`general/ui/listprograms.cs`);
  - a domain helper: `GxSimpleCollection<short>` (`general/gxdomainpage.cs`), not a variable.
  None of these is a scalar collection variable or parameter, an item-level SDT collection, or a
  top-level SDT collection variable.

The reader refuses every collection variable (`stored_variables.rs`, checked before the type is
decoded, so any type takes the same branch) and every collection item (`stored_sdt.rs`) by name,
with the reason above. The `GxSimpleCollection<item>` mapping in `csharp/types.rs` is unchanged: it
serves the XPZ exchange path (`exchange.rs`), not the stored-row path.
