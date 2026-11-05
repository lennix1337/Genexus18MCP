# Reading and writing one property of an SDT member

`genexus_structure action=get_item_property` and `action=set_item_property` read and
change one typed property of one member of a Structured Data Type: a leaf item or a
sub-structure (level). They do not rebuild the structure, so every other member and
every other property stays as it is. `update_visual` cannot do this: it replaces or adds
members by name, type and length, and the textual structure it reads and writes does not
carry properties such as `Json Null Serialization`.

```json
{ "action": "get_item_property", "name": "SampleResponse", "itemPath": ["debug"], "property": "JsonNullSerialization" }
```

```json
{ "status": "ok", "code": "SdtItemProperty", "result": {
  "itemPath": ["debug"], "kind": "item",
  "property": "JsonNullSerialization", "nativeName": "idJsonInclude", "displayName": "Json Null Serialization",
  "value": "NoProperty", "label": "No Property", "rawValue": "idJsonNoProperty", "isDefault": true,
  "defaultValue": "NoProperty", "writable": true, "type": "enum",
  "allowedValues": [ { "value": "Empty", "label": "Empty", "rawValue": "idJsonEmpty" }, "..." ],
  "versionToken": "<token>" } }
```

```json
{ "action": "set_item_property", "name": "SampleResponse", "itemPath": ["debug"],
  "property": "JsonNullSerialization", "value": "Empty", "expectedVersion": "<token>", "dryRun": true }
```

## Addressing

- `itemPath` is the member names from the SDT root down, matched ignoring case:
  `["debug"]`, `["Items","Id"]`. A path through a leaf item is refused (`ItemNotALevel`) and an
  unknown member lists the members that exist there (`ItemNotFound`).
- `property` is the SDK typed name (`JsonNullSerialization`), the IDE name
  (`Json Null Serialization`) or the stored key (`idJsonInclude`); case, spaces and
  underscores do not matter, and an unknown spelling returns the close ones (`PropertyNotFound`).
  `get_item_property` without `property` lists every property of the member with its value,
  whether it is only the default, and whether it is writable.
- An enumerated `value` is the typed member (`Empty`, `JsonNull`, `NoProperty`), the IDE name
  (`JSON null`) or the stored id (`idJsonEmpty`). Booleans and numbers go as text.

## What a read returns

`value` is the effective value. The SDK resolves the default: for `Json Null Serialization` it is
`NoProperty` for collections, SDT-typed members and sub-levels and `Empty` for every other member,
so `isDefault: true` says the value was never set. `rawValue` is what the SDK stores,
`allowedValues` lists an enumeration, and `versionToken` is the same object-level token
`get_visual` returns. The Gateway never serves `get_item_property` or the `set_item_property` preview
from its semantic cache, so the token and the value always come from a fresh SDK read.

## Writing

1. `dryRun: true` returns the single change (`changes`) and writes nothing; it checks
   `expectedVersion` when one is sent.
2. A real write requires `expectedVersion`. A stale token returns `VersionConflict` with the
   current one before any change, and the token is checked again inside the SDK transaction.
3. The value is applied and saved inside one SDK transaction. Before it commits, the object is read
   back and compared with a snapshot taken before the write. The snapshot holds the root level and every
   member of the structure with its kind, its position among its siblings and the properties the SDK would
   store. The requested value must be in effect, the only stored difference must be the requested property
   of that member (`storedChanges`), and no other object part may differ. Anything else rolls the
   transaction back (`SdtItemPropertyNotPersisted`).
4. After the commit the object is read again through a fresh SDK instance. If it does not match, the
   previous value is restored in a second transaction and `rollback` reports `verified` or `unverified`.
   The restore runs only when the persisted version and the stored properties are exactly the ones this
   write saved; the object stamp has one-second resolution, so the version alone cannot tell another
   writer's save in the same second.
5. A value already in effect answers `SdtItemPropertyUnchanged` and saves nothing.

Failures carry `persisted` and `rollback`. `persisted` answers one question: is the requested value in
the KB (`true`, `false`, or `null` when the state could not be read). `rollback` is `not-needed` (nothing
was changed), `transaction-verified` (the transaction undid it and a fresh read proved it), `verified`
(restored and proved) or `unverified` (read the member before another write). When the rollback is
`unverified`, `storedChanges` lists what the KB holds now against what it held before (at most 20, with
`storedChangesTotal`), and `otherPartsChanged` says an object part outside the structure differs, so a
change made by another writer is shown as such instead of being reported as this request's write.

## Refused on purpose

- Properties that define the identity or the type of the member (`Name`, `Data Type`, `Based on`,
  `Collection`, `Maximum length`, `Dimensions`, length, decimals, sign): use `update_visual`
  (`StructuralPropertyNotSupported`). Setting `Maximum length` also rewrites `Length`, which is why
  these are not writable here.
- Read-only properties (`PropertyReadOnly`), properties that do not apply to the member in its current
  configuration (`PropertyNotApplicable`), properties the IDE hides for the member (`PropertyHidden`; they
  can still be read by name) and properties that are not text, boolean, numeric or enumerated (colors,
  fonts, formulas: `UnsupportedPropertyType`).
- A property the SDK did not report completely (its descriptor, stored type or allowed values could not be
  read) is read but never written (`PropertyCatalogIncomplete`): the defaults of the missing facts
  (writable, visible, free text) would let through values the SDK itself refuses or rewrites.

## Not covered

- Neither action runs Specify, Generate, Build, Rebuild, compilation, reorganization, execution or
  tests (`implicitLifecycleActions` is always empty). They change the stored property and nothing
  more, so they cannot show whether a serialization property changes the generated REST output: that
  needs a build in a separate test environment.
- GeneXus 18 U16 keeps a value that equals the default as an explicit value (`isDefault: false`
  afterwards). There is no action that clears the stored value again; the value is restored by writing
  the previous one.
