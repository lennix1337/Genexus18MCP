# Pattern Settings SDK save audit

Date: 2026-09-10. Scope: static inspection of the installed GeneXus 18 U16
SDK and WorkWithPlus assembly. No knowledge base was opened, saved, imported,
built or modified for this audit. No installed binary was changed.

## Decision

An isolated settings save is **not verified**. Keep the write operation blocked
with `SettingsIsolationUnverified`; a dry run can operate on a detached snapshot.
Do not implement a real save on the assumption that omitting an explicit
Apply/Update call prevents automatic effects. Do not present a process-local
lock or a read-then-save token comparison as an atomic SDK concurrency check.

## Inspected assemblies

Paths below are relative to the GeneXus installation directory.

| Assembly | File version | SHA-256 |
| --- | --- | --- |
| Packages/Artech.Packages.Patterns.dll | 18.0.16.58478 | 36F99D7D76C3C7B54DDED1249D09A02234834C9D38483C68AFC9D79BD8538ACE |
| Packages/Patterns/WorkWithPlus/DVelop.Patterns.WorkWithPlus.dll | 16.1.4.8316 | 16E15BCB8EAEF779ABD5B8EF9D33C724F58406979496612A253473688B52FB02 |
| Artech.Udm.Framework.dll | 18.0.16.58478 | BA47B86818AFF828A5172B44C0EA83353B4A565FEC076105480A995535BC9251 |

Assembly file versions are not the product installer version. Inspection used
ILSpy command-line 10.1.0.8386. Hashes were calculated from installed files.

## Verified SDK behavior

- `PatternSettingsPart` stores its data through its specialized `SerializeData`
  implementation, which writes a `PatternSettings` document containing the
  pattern tree. The generic property representation is not proof of an empty
  template tree. The live tree is `PatternPart.RootElement`.
- `PatternSettings.OnBeforeSaveKBObject` calls its base hook and
  `ThemeClassReferenceList.EnsureThemeClassReferencesConsistency`, then delays
  to a second boundary. Even this object-specific hook is more than a raw
  persistence call.
- `Artech.Packages.Patterns.Package.OnAfterSave` subscribes to
  `event://KnowledgeBase/AfterSaveKBObject`. For a normal save it consults the
  in-memory object flag `SkipApplyPattern`, then traverses
  `PatternVirtualPartItem.ItemsOf`. An item with `ApplyPattern` causes
  `ApplyPattern(... FromApplyOnSave = true)`; another instance mode can cause
  the instance itself to be saved. For a `PatternSettings`, the same handler
  clears the in-memory settings cache.
- `SkipApplyPattern` is evidence about this handler only. It is not evidence
  that other packages honor that flag, nor a documented global suppression
  contract.
- `Package.OnAfterOpenKB` on a writable design model calls
  `RemoveMissingPatternSettings` and imports pattern resources. Starting a
  fresh writable headless process is therefore not inherently side-effect-free.
- `PatternBasePart.ReadFrom` may invoke the version adapter and calls
  `GetDataUpdateProcess()?.UpdateObject(PatternInstance)` after loading the
  root. Avoid round-tripping through deserialize/import for a dry run. This
  observation alone does not establish writes to the KB during a read.

## WorkWithPlus hooks: concrete unresolved effects

`DVelop.Patterns.WorkWithPlus.Helpers.AutoLinkGeneratorEventSubscriptions`
has direct event subscriptions for `BeforeSaveKBObject` and
`AfterSaveKBObject`. IL inspection shows:

- `AfterSaveKBObject`, method body size `0x45a`, checks `PatternSettings` at
  `IL_008c`, compares the pattern definition to `WorkWithPattern.Id`, and
  enters a settings-specific branch. It calls a `GxEnvironmentHelper` predicate
  with an obfuscated name and several settings helpers with obfuscated names.
  The settings-specific helper calls include `IL_00ca`, `IL_00da`, `IL_0146`
  and `IL_0156`. Their absence of side effects has not been established.
- `BeforeSaveKBObject`, method body size `0x608`, reads and serializes settings,
  loads another settings representation, compares them using an obfuscated
  helper (`IL_039c`), and passes the result to another settings helper
  (`IL_03bc`). The comparison helper has an invalid/protected method header
  (`0x9C`) in static IL inspection.
- C# decompilation of this class fails with `BadImageFormatException` while
  following a dependency in `BeforeSaveKBObject` (invalid method header
  `0x6D`). Treat this as an inspection limitation, not as an SDK runtime error.

These hooks are positive evidence that saving settings enters WorkWithPlus
code. They are not proof that an instance update always happens, and they do
not justify claiming that headless execution suppresses all effects.

## Concurrency limitation

The public `StateManager` surface exposes `AcquireState`, `ReleaseState`,
`LockObject`, `UnlockObject`, `IsObjectLocked`, `Multiuser`, and `Enabled`.
`Entity` exposes `Timestamp` and `Save`. Their core implementations in the
installed assembly are protected; the decompiler displays `NoInlining` stub
bodies. Stub returns must not be interpreted as actual runtime semantics.

`SavePreferences` and `KBObjectSavePreferences` expose save/validation flags,
but no verified expected-version argument or compare-and-swap contract was
found in this inspection. Lock scope, interaction with another IDE/process,
and lock participation during `Save` remain unverified. Token comparison
before `Save` alone leaves a race between the two operations.

## Validacoes executadas

- Static C# inspection of PatternSettings, PatternSettingsPart, PatternBasePart,
  Package, SavePreferences, KBObjectSavePreferences, Entity, and StateManager.
- Static IL inspection of WorkWithPlus save event subscriptions and the
  PatternSettings-specific branches.
- Installed assembly file-version and SHA-256 inspection.
- Sanitized report review; no credentials, connection strings or KB contents
  are included in this report.

## Pendencias ou validacoes nao executadas

- Vendor-supported suppression/isolation contract covering all WorkWithPlus
  and SDK save handlers, including resource import and dependent-object writes.
- Verified cross-process optimistic concurrency or exclusive SDK lock contract
  covering reload, version comparison, mutation, save and read-back.
- Authorized disposable test KB: capture every object/version and prohibited
  operation before and after one targeted save; confirm only the intended
  settings delta and its necessary repository persistence changed.
- Race test using a second independent writer and read-back through a fresh
  SDK load; validate conflict refusal and preservation of the second writer.

Do not remove the write gate solely because unit tests pass. Those tests can
validate planning and rejection behavior; they cannot prove third-party save
event isolation or persistence in a real SDK repository.
