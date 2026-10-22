# WorkWithPlus derived objects: what regenerates them, and what does not

Records how the objects a WorkWithPlus instance derives (the `LoadDVCombo<Trn>` helper,
the WW/DS/export family) are rebuilt, and why `genexus_wwp action=regenerate_derived`
exists. The reading is of the installed GeneXus 18 SDK (U16 18.0.16) and of this
repository's own apply path; the limits of what was run are stated at the end.

## The symptom

An edit that changes how the instance calls a helper (combo `Parameters`,
`ConditionsDynamicAttsOrVars`) was saved on the PatternInstance and projected onto the
parent Transaction, and the parent's Events then called the helper with the new
arguments. The helper, a separate Procedure with its own `Source`/`Rules`/`Variables`,
kept its old `parm` rule. `genexus_apply_pattern reapply=true` answered `PatternApplied`
and changed nothing in it.

## Why reapply did nothing

`PatternApplyService.ApplyPatternToObject` for WorkWithPlus:

- first apply: calls the engine (`_engine.ApplyPattern`), which generates the family;
- existing instance: logs "existing host detected - skipping engine reapply (NRE-prone)"
  and only runs `TryInvokeBuildProcessUpdateParent`, the `UpdateParentObject` step that
  re-projects the parent WebForm.

So a reapply is a parent re-projection. `PatternRouteCapabilities.WwpReapplyRegenerates`
claimed otherwise (a "declared capability, not a measurement"); it is now `false`, the
reapply response carries `derivedObjectsRegenerated` and the diagnose remediation names
`regenerate_derived`.

## What the engine does

`Artech.Packages.Patterns.PatternEngine.ApplyPattern(PatternInstance, ApplySettings)` is
public and is what the IDE reaches from Apply Pattern. Its body (decompiled from
`Artech.Packages.Patterns.dll`), in order:

1. `instance.PatternPart.UpdateData()`, `ValidateInstance`;
2. `BeforeStartBuild`, `ResourceManager.ImportResources` (+ `AfterImportResources`),
   `instance.SaveUpdates(false)`;
3. `GenerateInstanceObjects`: `BeforeGenerateObjects`, then for every non-global
   `PatternObject` of the definition the generated objects, then `SaveInstanceObjects`
   (`BeforeSaveObjects`, save every generated object, `UpdateParentObject` and save the
   parent, `AfterSaveObjects`);
4. `DeleteOldInstanceObjects`: children of the instance that are no longer generated are
   deleted, or detached when customized;
5. `GenerateInstanceFiles`;
6. `RefreshGlobalObjects` unless `ApplySettings.IsRefresh` is set;
7. `instance.SaveUpdates(true)`, `AfterEndBuild`.

Facts that shape `regenerate_derived`:

- **The derived objects of an instance are its children**: `Model.Objects.GetChildren(instance)`.
  The engine uses the same relation for step 4. The unit is found that way, not by name:
  the family names are a Settings value (`LoadDVCombo<Object>` by default; a KB may use
  another).
- **The helper is declared in `WorkWithPlus.Pattern`** as
  `<Object Type="Procedure" Id="LoadDVCombo" Name="LoadDVCombo{Parent.Name}" Element="instance">`;
  the content comes from the pattern's default-provider templates, which is why a part the
  user customized (not default) is not overwritten.
- **`ApplySettings.IsRefresh` is internal** and is how the engine batches regeneration
  before a build (`RefreshApplyPatterns`): it skips `RefreshGlobalObjects`. Setting it
  confines a run to the instance's own objects instead of also rewriting KB-wide ones.
- **The parent is saved after the generated objects** (step 3), so it is never saved
  against a helper still on the old contract. The previous route (`UpdateParentObject`
  alone, as `set_combo_properties` projects) has no such ordering.
- **Prerequisite**: the WorkWithPlus package refuses to build for a KB without its
  Design System: "Before applying WorkWithPlus for Web Pattern for the first time, a
  Design System must be imported". The same requirement as the IDE's Apply Pattern, and
  the same message `genexus_apply_pattern` already reports for a first apply.
- The `DVelop.Patterns.WorkWithPlus.dll` method bodies are protected (the decompiler
  reports `Invalid MethodBodyBlock`), so the package's own build-process hooks cannot be
  read, only observed.

## What `regenerate_derived` does with that

See the `genexus_wwp` help. In short: capture the unit (host, parent, every child, all
text parts), require a `versionToken` per object, run the engine in refresh mode, re-read
everything freshly and check three things, the PatternInstance unchanged, no object changed
outside the helpers (`scope=helpers`) and no helper disagreeing with a caller (argument
count, order, names, declared types). Otherwise restore the unit and prove it by a fresh
re-read. Objects the engine deletes in step 4 cannot be restored from text; they are
reported, with their pre-run text in the snapshot store.

## What was and was not run

Run, on a disposable KB with the WorkWithPlus package but without its Design System
(GeneXus 18 U16, 18.0.16): the action over the real Gateway and Worker; the `dryRun` plan;
the write up to the engine call; the snapshot manifest; capture of the engine's output
(it returned the Design System message above); and the rollback with a fresh-read
verification (nothing had changed). Unit tests cover the helper detection, the call
contract (count, order, type, name, comments and strings, nested calls), the capture diff
and the token comparison.

A read-only `dryRun` against a real application KB (a production WorkWithPlus Transaction with a generated `LoadDVCombo` helper) also ran, and it caught a defect that the synthetic cases had missed: the generated procedure is called as a function (`&Json = Helper(...)`), so the call passes one argument fewer than the `parm` rule and the last `out` parameter is the result. The first version counted that as an argument-count mismatch; the contract check now models the function-style call, its `out` requirement and the type of the receiving variable. A second real instance then showed that comparing basic types by their exact text was too strict (a `VARCHAR(40)` variable legitimately receives the `LONGVARCHAR` result), so basic types now agree by family while attribute and domain references still have to name the same target.

**Not run**: a regeneration that actually rewrites a helper. It needs a KB with the Design
System imported and a combo whose helper already exists. Until that run, the claim that the
engine rebuilds the `LoadDVCombo` helper from the current instance is read from the SDK, not
observed; the operation is built to refuse and restore, not to assume it.
