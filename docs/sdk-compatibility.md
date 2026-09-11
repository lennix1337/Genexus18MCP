# GeneXus SDK compatibility

The Worker is compiled against the selected supported GeneXus major installed on the build host. The SDK is proprietary and is intentionally **not** a NuGet dependency, checked into this repository, or copied into the npm/package artifacts.

## Supported fixture

`config/sdk-compatibility.json` records a reference GeneXus product version and SHA-256 fingerprints for selected assemblies the Worker references. The reference version selects the Worker major; minor, patch, build and hash differences within that major are diagnostics, not compatibility failures. The manifest contains no SDK bytes or credentials. The default reference was produced from a self-hosted GeneXus 18 installation whose anchor product version is `18.0.10.184260`.

Both build and startup require the reference GeneXus major and all listed assemblies. The legacy `allowPatchVersionDrift` flag is no longer consulted: compatibility within the supported major does not require an opt-in. Fingerprint drift is reported even when ProductVersion is unchanged. These checks establish SDK compatibility, not validation of KB writes or save-event isolation.

Provide the SDK through a self-hosted Windows build image or an installed developer workstation:

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj
```

The build target runs `scripts/validate-gx-sdk.ps1` before resolving references. Worker startup repeats the same check from the copied manifest, before SDK initialization. Both checks use stable diagnostics such as:

- `GXMCP_SDK_PATH_MISSING`
- `GXMCP_SDK_VERSION_MISMATCH`
- `GXMCP_SDK_ASSEMBLY_MISSING`
- `GXMCP_SDK_FINGERPRINT_DRIFT` (informational; compatibility still succeeds)

A missing required assembly or different/unreadable major fails build/startup. A different major requires a Worker built and validated for that major; changing the manifest on an existing binary is not an upgrade path. To inspect the selected SDK and reference, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\validate-gx-sdk.ps1 `
  -GxPath $env:GX_PATH `
  -Manifest .\config\sdk-compatibility.json
```

Do not publish proprietary DLLs, secrets, or a copied SDK fixture in CI. A CI runner without the self-hosted SDK should report the SDK build/live gate as unavailable; it must not claim that the Worker build passed.

When upgrading within a supported major, run the validator, focused compatibility tests and the authorized live smoke. Refresh reference hashes when useful for diagnosis; exact hashes are not an acceptance gate. Adding a major also requires the explicit version catalog entry and a Worker built and live-tested for it. Package hashes still protect the integrity of our distributed binaries and rollback files; they are separate from SDK compatibility.
