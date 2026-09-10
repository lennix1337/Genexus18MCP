# GeneXus SDK compatibility

The Worker is compiled against the GeneXus 18 SDK installed on the build host. The SDK is proprietary and is intentionally **not** a NuGet dependency, checked into this repository, or copied into the npm/package artifacts.

## Supported fixture

`config/sdk-compatibility.json` is a public compatibility lock. It records the supported GeneXus product version and SHA-256 fingerprints for the assemblies the Worker references. It contains no SDK bytes or credentials. The current lock was produced from a self-hosted GeneXus 18 installation whose anchor product version is `18.0.10.184260`.

Provide the SDK through a self-hosted Windows build image or an installed developer workstation:

Additional explicit locks are available for the inspected U11, U12 and U16
installations in `config/sdk-compatibility-u11.json`,
`config/sdk-compatibility-u12.json` and `config/sdk-compatibility-u16.json`.
The original U10 lock remains the default. Select one lock for each build;
the resulting Worker carries only that lock under `sdk-compatibility.json`.
Both build and startup enforce its exact product version and all fingerprints.
These hashes attest SDK identity, not validation of KB writes or save-event
isolation. No proprietary SDK assemblies are added by these manifests.

```powershell
$env:GX_PATH = '<installed-upgrade-directory>'
$env:GxMcpSdkManifest = (Resolve-Path '.\config\sdk-compatibility-u16.json').Path
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj
# build.ps1 also forwards GxMcpSdkManifest to its Release and Debug builds.
```

For a command-scoped choice, use `-p:GxMcpSdkManifest=<absolute-manifest-path>`.
Clear the environment variable to return to the default U10 lock. Do not use
`GxMcpSkipSdkValidation` to build upgrade packages. Switching upgrade builds
replaces the output lock even when the selected source manifest is older.

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj
```

The build target runs `scripts/validate-gx-sdk.ps1` before resolving references. Worker startup repeats the same check from the copied manifest, before SDK initialization. Both checks use stable diagnostics such as:

- `GXMCP_SDK_PATH_MISSING`
- `GXMCP_SDK_VERSION_MISMATCH`
- `GXMCP_SDK_FINGERPRINT_MISMATCH`

A mismatch is a failed build/startup, not a best-effort warning. To inspect the exact path and expected lock, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\validate-gx-sdk.ps1 `
  -GxPath $env:GX_PATH `
  -Manifest .\config\sdk-compatibility.json
```

Do not publish proprietary DLLs, secrets, or a copied SDK fixture in CI. A CI runner without the self-hosted SDK should report the SDK build/live gate as unavailable; it must not claim that the Worker build passed.

When intentionally upgrading the supported SDK, install the candidate on a controlled self-hosted image, run the validator, update the manifest hashes and version together, then run the focused tests and full verification gates. Treat the manifest as a reviewable compatibility decision, not as a license to redistribute the SDK.

Worker test SDK dependencies are refreshed when switching `GX_PATH`. The copy target excludes resolved project/NuGet dependencies (for example Newtonsoft.Json) before copying SDK DLLs, so changing upgrades replaces stale SDK assemblies without replacing application dependencies. For a full SDK matrix, use a separate output directory under the worktree for each upgrade, and compare the output DLL hashes with the selected lock. Keeping outputs inside the worktree also supports tests that locate source files by walking parent directories.
