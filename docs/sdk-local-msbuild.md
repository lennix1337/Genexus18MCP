# Local SDK build outside the MCP (MSBuild)

Use this route to build objects of a disposable test KB with the installed
GeneXus SDK when the IDE build (`BuildOne`, `BuildAll`,
`IdeWebBuildAndDeploy`) cannot or must not run: as the SDK oracle for gx-engine
parity and timing, to refresh generated code, or when the MCP Worker build stops
at GeneXus Cloud.

```powershell
# Specify + generate + compile + publish one object (or several: 'A,B')
pwsh -NoProfile -File scripts/sdk-local-build.ps1 -KbPath C:\KBs\KBTeste -Object DCM37961700 -ForceRebuild `
     -FixtureManifest scratchpad/kbteste.fixture.json

# Whole KB, specification and generation only
pwsh -NoProfile -File scripts/sdk-local-build.ps1 -KbPath C:\KBs\KBTeste -All -Through Generate

# Show the plan and the generated MSBuild project without running anything
pwsh -NoProfile -File scripts/sdk-local-build.ps1 -KbPath C:\KBs\KBTeste -Object DCM37961700 -PlanOnly
```

## What it runs

1. **SDK phase, 32-bit .NET Framework MSBuild**
   (`%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe`; the GeneXus tasks
   are x86 net4x). The generated project imports
   `<GeneXus>\Genexus.Tasks.targets` and runs only:

   ```xml
   <OpenKnowledgeBase Directory="C:\KBs\KBTeste" />
   <SpecifyOneOnly ObjectNames="A;B" ForceRebuild="true" />   <!-- or SpecifyAllOnly -->
   <GenerateOnly />
   <CloseKnowledgeBase />
   <OnError ExecuteTargets="CloseOnError" />
   ```

2. **Compile phase, `dotnet msbuild`** on each SDK-generated project under
   `<KB>\<environment>\build\<program>\<program>.csproj` whose source the SDK
   phase rewrote (or the names passed with `-Project`), with targets
   `Build;Publish` (`Rebuild;Publish` with `-ForceRebuild`). The KB's own
   `Directory.build.props` supplies `TargetFramework` and `GenStdVersion`, and
   `build\DotNetCoreBaseProject.targets` sets `PublishDir` to
   `<environment>\web\bin`, so the program lands where the IDE puts it.
   `-NoPublish` stops after `Build`.

Procedure `DCM37961700` maps to project `adcm37961700`; the published
`web\bin\adcm37961700.exe` runs directly.

## Why not BuildOne / BuildAll

- The installed GeneXus 18 is a Trial: `DEPLOY_CLOUD` is forced to `Yes` for the
  default generator (`DeployToCloudValidResolver`), so `BuildOne`, `BuildAll` and
  `IdeWebBuildAndDeploy` reach the GeneXus Cloud step and fail with
  `Input parameters are not complete: User`. Cloud must stay off in validation.
- Those tasks can also apply a pending database reorganization (#749).

The script checks the generated project against an allow-list
(`OpenKnowledgeBase`, `SpecifyOneOnly`, `SpecifyAllOnly`, `GenerateOnly`,
`CloseKnowledgeBase`, `Message`, `OnError`) before it runs, and accepts only
`C:\KBs\*teste*` Knowledge Bases. `-FixtureManifest` additionally validates
fixture identity and provenance with `scripts/live-fixture.ps1`.

## Evidence

Each run writes `sdk-local-build.proj`, `sdk-msbuild.log`, one
`compile-<program>.log` per project and `summary.json` (SDK version, fixture,
command lines, exit codes, wall times) to `-EvidenceDir`, by default the ignored
`scratchpad\sdk-local-build\<timestamp>`. The process exits non-zero when any
phase fails.

## Limits

- No reorganization runs, so this route cannot prove or disprove reorganization
  behaviour (the #749 guard); it also does not create or alter the application
  database.
- The compile phase covers .NET environments with SDK-style projects
  (`build\DotNetCoreBaseProject.targets`); .NET Framework or Java environments
  stop after generation.
- The KB must not be open in the IDE or in an MCP Worker; `OpenKnowledgeBase`
  fails on a locked KB.
- Without `-ForceRebuild`, the generator may leave unchanged sources untouched
  and nothing is compiled; use `-Project` to compile specific programs anyway.
