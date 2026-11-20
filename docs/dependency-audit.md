# npm dependency audit

The dedicated workflow runs every Monday and on demand. Phase 1 is report-only:
full-tree and `--omit=dev` JSON reports and exit codes are attached to the run,
and severity totals appear in the job summary. Review the weekly summary and
artifact in GitHub Actions; no issue or external notification is sent. Failures
of the packaging checks make the run red. Audit findings alone do not in phase 1.
For phase 2, run the workflow manually with `enforce` enabled: each tree has a
separate high/critical gate. Enable this by default only after reviewing reports.
The `--omit=dev` reports track the runtime invariant separately.

The gate checks the versioned allowlist, rejects expired or absent entries and
unlisted moderates, and rejects all high/critical findings. After dependency
bumps rerun `npm audit --json` in both trees and review the reports and
allowlist; don't copy a fixed vulnerability count. `fixAvailable` may change.
Entries below describe direct advisory nodes, not their indirect parents.

## Accepted development-only moderates

Measured 2026-11-20 from the Nexus lockfile; root had no advisories. npm
reported four moderate package nodes in Nexus, arising from these two advisories:

| Advisory | Package | Tree | Version | fixAvailable | Decision |
| --- | --- | --- | --- | --- | --- |
| [GHSA-3g3m-2fjr-6pw4](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4) | glob | nexus | 13.0.7 | `{name: mocha, version: 11.4.0, isSemVerMajor: true}` | Accept temporarily: dev-only Mocha dependency; npm proposes a major downgrade. |
| [GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249) | minimatch | nexus | 10.2.6 | `{name: mocha, version: 11.4.0, isSemVerMajor: true}` | Accept temporarily: dev-only; npm proposes a major downgrade. |

Owner and review expiry are in `scripts/npm-audit-allowlist.json`. The extension
is packaged with `--no-dependencies`: the committed structural test forbids
runtime dependencies and the VSIX smoke checks the actual archive contents.
