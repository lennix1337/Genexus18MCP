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

```pwsh
.\scripts\release.ps1 patch    # bug fix:     2.1.2 -> 2.1.3
.\scripts\release.ps1 minor    # new feature: 2.1.2 -> 2.2.0
.\scripts\release.ps1 major    # breaking:    2.1.2 -> 3.0.0
.\scripts\release.ps1 2.1.5    # explicit version (e.g. skip a number)
```

The script will:

1. Verify you're on `main` with a clean tree and `gh` is authenticated.
2. Pull the latest `main`.
3. Bump `package.json` (`npm version`).
4. Run `.\build.ps1` to produce `publish/` with Gateway + Worker + Definitions.
5. Zip `publish/` into `publish.zip`.
6. Commit, tag (`vX.Y.Z`), and push.
7. Create the GitHub Release with `publish.zip` as an asset.

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

## Recovery

**Build failed locally**: the script reverts the `package.json` change before exiting. No tag is created, no Release is published. Fix and re-run.

**Tag pushed but workflow failed**: re-run from the Actions tab, or trigger manually:
```pwsh
gh workflow run release.yml
```
The workflow is idempotent — if the version is already on npm it skips automatically.

**Need to unpublish**: npm only allows unpublish within 72 hours. Prefer publishing a patch with the fix.

## Contributing (non-maintainers)

You don't need any of this to contribute. Open a PR against `main`; `ci.yml` runs the test suite on your branch. Only the maintainer can cut releases. Even if you fork the repo and push tags, npm Trusted Publishing rejects publishes that don't originate from `lennix1337/Genexus18MCP`'s `release.yml`.
