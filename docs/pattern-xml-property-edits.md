# Raw pattern XML property edits

Raw `PatternInstance` XML edits now preserve the current SDK metadata rather
than rebuilding `childrenOrderedList`. XML document order is not a reliable
description of WorkWithPlus's internal rendering metadata: valid serialized
lists may include entries without direct XML children.

An unchanged document returns `WriteNoChange`. A property-only edit with the
same tree and identities returns `WriteDryRun` with exact attribute paths,
`before`/`after` values and `savePathExercised: false`. The preview and write
preflight share one pure plan and the same original request string; no SDK
deserializer or save is invoked by the preview. Failure to read the current
document rejects both paths with `PatternReadFailed`.

Structural edits previously accepted by the raw route now fail explicitly with
`PatternStructureChangeUnsupported`; changes to defaults, templates, child-order
metadata or node identities fail with `PatternMetadataChangeUnsupported`.
This includes adding/removing those attributes, renaming named nodes and moving
identified siblings. Namespaced attributes are compared by their full XML name.
Meaningful text, comments and CDATA must remain unchanged. Inter-element
formatting whitespace can differ; DTDs are unsupported.

Use the appropriate SDK pattern authoring action for structural operations,
reviewing its documented side effects. Anonymous siblings with identical element
names have positional identity: exchanging only their editable attribute values
is indistinguishable from property edits. The raw route does not infer a layout
reordering from those values and never repairs their internal lists.

This preflight is not an SDK save-isolation guarantee. Existing save behavior,
pattern projection and Events/Settings isolation protections are unchanged.
Do not use a successful property preview as authorization for an isolated save.
