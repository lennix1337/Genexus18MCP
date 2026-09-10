# WorkWithPlus template objects

WorkWithPlus can store templates outside `PatternSettingsPart`. Reading only
`PatternSettings.PatternPart.RootElement` can therefore return a valid Settings
tree while omitting templates displayed by the WorkWithPlus Settings editor.

## Installed SDK evidence

Static inspection of WorkWithPlus 16.1.4.8316 for GeneXus 18 U16 identifies
`DVelop.Extensions.WWPPackagesCommon.Model.WWPTemplate : KBObject`, with type GUID
`083f1b21-5715-45e1-8a8d-ceadef141e02`. Its descriptor is
`WorkWithPlus for Web Template` (`WorkWithPlusTemplate`, `NoCreatable`).

The object stores its XML in the string property `WWPTemplate_TemplateXml`.
`WWPTemplate_Type`, `WWPTemplate_MainTemplate`, category, and instance type are
separate object properties. The installed resource
`Packages/Patterns/WorkWithPlus/Resources/BaseDesignSystem_SkipDesign.xml`
contains a template named `Transaction`, described as `Default data`, with type
`Trn.Transaction` and main template `Main`. Its XML root is `transaction` and
contains the table `TableMain`. Installed resources are evidence of this storage
contract, not a replacement for the KB's current values. No import is involved.

`DVelop.Extensions.WWPPackagesCommon.dll` inspected SHA-256:
`6025d0e585c0edf4fcbe2fe38ec6bb5879c18193cdc4446e4e5fab919df7a689`.

## Read and preview contract

`genexus_wwp` keeps `guid`/`entityKey` scoped to the Pattern Settings object.
Separate-object discovery requires the native Settings definition ID to match
the WorkWithPlus pattern ID; a homonymous Settings from another pattern is not
linked to WorkWithPlus objects.
Separate templates have a stable catalog selector `wwp:<template-object-guid>`,
`storage=wwp-object`, `templateGuid`, `settingsGuid`, `mainTemplate`,
`settingsPath`, and `settingsLinkVerified`. Templates must identify
exactly one embedded `InstanceTemplate` by `WWPTemplate_MainTemplate`; unresolved
or empty main-template links remain visible but cannot be edited. An empty main
template does not prove a link to the Settings root.

The token covers KB/model/active-version identity, the whole Settings snapshot,
and the separately stored template XML and object metadata. A change to another
template conservatively invalidates the token too. SDK object caches are
invalidated before both Settings and separate-template reads. A second fresh
Settings object, its XML/projection, and fresh template objects must match the
first snapshot; the active KB/model/version identity must also remain unchanged.

```json
{"action":"settings_templates","name":"WorkWithPlus","guid":"<settings-guid>","limit":0}
```

Choose the returned `wwp:<guid>` selector. Keep the same token while paging.

```json
{"action":"settings_read","name":"WorkWithPlus","guid":"<settings-guid>","template":"wwp:<template-guid>","offset":0,"limit":0,"baseVersion":"<catalog-token>"}
```

`offset`/`limit` page the node projection. Responses include `totalNodes`,
`truncated`, and `nextOffset`. Only `offset=0, limit=0` also returns the complete,
unmodified `source` string; paged responses report `sourceIncluded=false` and
`sourceLength`. Attributes are labeled `stored-template-xml`.
`effectivePropertiesResolved=false` explicitly means WWP default resolvers and
validators have not run. Embedded Settings templates retain their SDK projection.

For separate objects, preview currently accepts only an existing `themeClass`
attribute on a `table`. Use the node path from the read response:

```json
{"action":"settings_edit","name":"WorkWithPlus","guid":"<settings-guid>","template":"wwp:<template-guid>","nodePath":"wwp:<template-guid>/0","property":"themeClass","value":"TableMainTransaction example-entry","baseVersion":"<read-token>","dryRun":true}
```

The pure planner returns a property `diff` and exact `textEdit` with UTF-16
offset/length and before/after text. It changes only the chosen attribute's
encoded value, preserving quotes, whitespace, comments, XML entity spelling
elsewhere, and every internal identifier/default/order attribute. No-op previews
retain the original entity spelling. DTDs and invalid XML characters are refused.
No SDK property setter, deserializer, resolver, import, or Save is invoked.

## Save isolation remains unverified

The real-save path still returns `SettingsIsolationUnverified`, with
`saved=false` and `saveAvailable=false`. This change does not enable writes.

Saving the separate object is a different SDK route from saving Pattern Settings.
The inspected `WWPTemplate` class declares only its constructor and property
definitions; it does not override Save, SerializeData, or object save hooks.
By contrast, `PatternSettings.OnBeforeSaveKBObject` and
`PatternBasePart.BeforeSaveKBObject` call theme-class consistency routines, which
can directly save ThemeClass objects or create DesignSystemClass entities.
Broker suppression alone cannot suppress those virtual hooks.

Before enabling a separate-template save, verify the exact loaded SDK/WWP
fingerprints, native base save route, actual parts and direct delegates, global
broker suppression/restoration, transaction and concurrency behavior, and fresh
SDK read-back. Compare every other object property/part and dependent object or
entity; do not update Pattern Settings metadata or apply any pattern instance.
Protected WWP method bodies prevent treating static inspection as a complete
isolation proof. No KB was opened or written during these static/offline tests.
