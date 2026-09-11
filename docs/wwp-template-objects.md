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

## Read contract and blocked edits

`genexus_wwp` keeps `guid`/`entityKey` scoped to the Pattern Settings object.
Separate-object discovery requires the native Settings definition ID to match
the WorkWithPlus pattern ID; a homonymous Settings from another pattern is not
used as the discovery context. This definition check does not establish ownership
of the separately stored objects.
Separate templates have a stable catalog selector `wwp:<template-object-guid>`,
`storage=wwp-object`, `templateGuid`, `settingsGuid`, `mainTemplate`,
`settingsPath`, and `settingsLinkVerified`. These are model-wide library records:
`settingsGuid` identifies the read context, not a verified owner.
`BindTemplate` always returns `settingsScope="model-wide"`, `settingsPath=null`
and `settingsLinkVerified=false`. `settingsLinkEvidence` reports any
`WWPTemplate_MainTemplate` name matches for display only. Even one matching
embedded `InstanceTemplate` does not prove ownership; neither does an empty main
template identify the Settings root.

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

For separate objects, `settings_edit` is refused with
`TemplateSettingsLinkUnverified`, including `dryRun:true`. Once the snapshot and
selector checks pass, the service stops at the ownership guard before invoking
the planner. The following call documents that refusal, not an available preview:

```json
{"action":"settings_edit","name":"WorkWithPlus","guid":"<settings-guid>","template":"wwp:<template-guid>","nodePath":"wwp:<template-guid>/0","property":"themeClass","value":"TableMainTransaction example-entry","baseVersion":"<read-token>","dryRun":true}
```

The internal pure planner can plan an existing `themeClass` attribute on a
`table`, returning a property `diff` and exact `textEdit` with UTF-16
offset/length and before/after text. It changes only the chosen attribute's
encoded value, preserving quotes, whitespace, comments, XML entity spelling
elsewhere, and every internal identifier/default/order attribute. No-op previews
retain the original entity spelling. DTDs and invalid XML characters are refused.
This helper is tested offline but is not reachable through the standalone
template edit route while ownership remains unverified. The refused MCP call
returns no successful diff and invokes no property setter or Save.

Embedded Settings templates follow a different route: their `dryRun:true` can
return a pure projection diff. This does not enable previews for `wwp:<guid>`
records or establish SDK save isolation.

## Save isolation remains unverified

Standalone template edits, including real-save attempts, stop earlier with
`TemplateSettingsLinkUnverified`. For embedded Settings templates, a valid
real-save request with a version token returns `SettingsIsolationUnverified`,
with `saved=false` and `saveAvailable=false`. Missing or stale tokens and invalid
selectors can fail their own checks first. Neither route enables writes.

Saving the separate object is a different SDK route from saving Pattern Settings.
The inspected `WWPTemplate` class declares only its constructor and property
definitions; it does not override Save, SerializeData, or object save hooks.
By contrast, `PatternSettings.OnBeforeSaveKBObject` and
`PatternBasePart.BeforeSaveKBObject` call theme-class consistency routines, which
can directly save ThemeClass objects or create DesignSystemClass entities.
Broker suppression alone cannot suppress those virtual hooks.

Before enabling a separate-template save, establish its actual Settings ownership,
record loaded SDK/WWP versions and fingerprints as diagnostics, and verify the
native base save route, actual parts and direct delegates, global
broker suppression/restoration, transaction and concurrency behavior, and fresh
SDK read-back. Compare every other object property/part and dependent object or
entity; do not update Pattern Settings metadata or apply any pattern instance.
Protected WWP method bodies prevent treating static inspection as a complete
isolation proof. No KB was opened or written during these static/offline tests.

The offline tests cover XML and binding/planning helpers, not the full
`PatternSettingsService.Run` path. They do not certify live SDK enumeration,
fresh-read/token conflicts through that service, or persistence. Those remain
explicit validation gaps; passing the pure planner tests does not remove the
ownership or save guards.
