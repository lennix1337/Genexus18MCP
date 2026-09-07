# Release Process

This document describes how `genexus-mcp` is published to npm. **Only the maintainer can run this** — it requires GeneXus 18 installed locally and push access to this repository.

## Why the build runs locally

`src/GxMcp.Worker` references private GeneXus 18 SDK DLLs from `C:\Program Files (x86)\GeneXus\GeneXus18` (see the `<HintPath>` entries in `GxMcp.Worker.csproj`). GitHub-hosted runners don't have GeneXus, so the .NET artifacts must be built on a machine that does. The actual `npm publish` still happens in GitHub Actions, which preserves the **npm provenance** badge via OIDC Trusted Publishing.

## Prerequisites (one-time)

- Windows with **.NET 10 SDK** (the Worker still builds against .NET Framework 4.8)
- **GeneXus 18** installed at `C:\Program Files (x86)\GeneXus\GeneXus18` (or override via `config.json`)
- **GitHub CLI** authenticated: `gh auth status` must succeed
- npm account is a maintainer of `genexus-mcp` (Trusted Publishing already configured for this repo + workflow)
- Clean working tree on `main` branch

## Publish a new version

From the repo root, in PowerShell:

Run the canonical entrypoint from the repository root:

```pwsh
pwsh -NoProfile -File .\release.ps1 -Version 3.0.1
```

The compatibility wrapper at `scripts/release.ps1` still accepts `patch`,
`minor`, `major`, or an explicit semver and delegates to that command. It has
no independent build or publication path.

The script will:

1. Verify the working tree and release notes.
2. Synchronize package, lockfile, Gateway, Worker, and Nexus versions.
3. Commit the release source state before building and record that commit in
   `gxmcp-manifest.json`.
4. Run `scripts/release-preflight.ps1` (the full solution, CLI, Nexus, contract,
   inventory, plan, script-test, and warning gates).
5. Run `.\build.ps1`, package the Nexus VSIX, and zip `publish/` into
   `publish.zip`.
6. Write `publish.zip.sha256` as a release asset, then verify the tree is still
   at the manifest source commit.
7. Create and push the annotated tag and GitHub Release with all assets.

That Release **published** event triggers `.github/workflows/release.yml`, which:

1. Downloads `publish.zip` from the Release.
2. Unpacks it into `publish/`.
3. Runs `npm publish --access public --provenance` via OIDC.

## Verify

```pwsh
gh run watch                                  # watch the publish workflow
npm view genexus-mcp@<version> dist           # confirm fileCount > 150, size > 2 MB
npm view genexus-mcp@<version> --json | jq .  # confirm provenance present
```

The package page at https://www.npmjs.com/package/genexus-mcp should show the **"Provenance"** badge.

The script prints a status-file path (under `%TEMP%` by default). Follow a
detached run with:

```pwsh
pwsh -NoProfile -File .\scripts\release-status.ps1 -Path <status-file> -WaitSeconds 60
```

The JSON status contains `version`, `tag`, `phase`, `state`, `pid`,
`updatedAtUtc`, `releaseUrl`, `workflowRunId`, `exitCode`, and `error`.

## Recovery

**Build failed locally**: the source metadata commit remains untagged and no
Release is published. Fix the build and re-run with the same version; the
script resumes only when the remote tag/release state is safe.

**Tag pushed but workflow failed**: re-run from the Actions tab, or trigger manually:
```pwsh
gh workflow run release.yml
```
The workflow is idempotent — if the version is already on npm it skips automatically.

**Release exists without assets**: rerun the canonical entrypoint with the same
version. It uploads the missing assets to the existing release instead of
trying to create a duplicate.

**Need to unpublish**: npm only allows unpublish within 72 hours. Prefer publishing a patch with the fix.

## Contributing (non-maintainers)

You don't need any of this to contribute. Open a PR against `main`; `ci.yml` runs the test suite on your branch. Only the maintainer can cut releases. Even if you fork the repo and push tags, npm Trusted Publishing rejects publishes that don't originate from `lennix1337/Genexus18MCP`'s `release.yml`.
