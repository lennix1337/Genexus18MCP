# Textual Variables edits and verification scope

Textual `genexus_edit` full/patch and `import_part` writes validate the complete Variables DSL and referenced types before any setters, insertion or removal. Unchanged declarations are not rebound. An identical DSL skips Save. Replacement still removes omitted nonstandard variables; patch applies the delta of its resulting DSL. Source/Rules auto-declaration and typed `genexus_variable` operations are unchanged.

A Variables DSL exposes declaration name, type/binding, length/decimals, collection and dimensions. It does not encode every SDK property, Description, internal identity or explicit/default property state. Successful textual verification is therefore labeled `verificationScope=variables-dsl`, with `textVerified`/`persistedTextVerified` evidence and `metadataVerified=false`. A post-save equal-text result is not an absence-of-mutation guarantee: `textChanged=false` does not imply `changed=false`, and its recovery snapshot is retained.

Previews report declaration/type-reference validation and `savePathExercised=false`; they do not establish native Save behavior or metadata fidelity.

## Recovery compatibility change

Automatic recovery of Variables from a text-only snapshot is refused, including full-writer, patch, multipart-target and bulk compensation. Such a restore could overwrite a concurrent native edit and cannot certify metadata restoration. Existing text snapshots remain useful for manual comparison, but they are not complete Variables backups. Receipts no longer advertise complete `Restored`, `RolledBack`, `stateRestored`, `verified` or atomic recovery on text equality alone. If a batch fails after a Variables save, stop writing, obtain an independent persisted read, and use native revision recovery or an operator-reviewed typed edit rather than replaying the old DSL automatically.

Native fixture tests compare only the identity/properties that the selected SDK exposes and fresh reads can confirm. Unit tests and source guards do not prove native metadata preservation. Unavailable SDK dimensions, live gates or plugin dependencies must be reported separately.

Issues: [#447](https://github.com/lennix1337/Genexus18MCP/issues/447), [#448](https://github.com/lennix1337/Genexus18MCP/issues/448), [#449](https://github.com/lennix1337/Genexus18MCP/issues/449).
