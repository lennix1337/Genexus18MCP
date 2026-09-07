# Release build warning baseline

Captured on 2026-09-05 after the warning cleanup for issue #138.

## Measurement

The machine-readable source of truth is
[`build_warning_baseline.json`](build_warning_baseline.json). The manifest is
produced with the repository's GeneXus SDK path configured:

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
.\scripts\check-build-warning-baseline.ps1 -UpdateBaseline -GxPath $env:GX_PATH
```

The build completed successfully (`exit code 0`). It emitted 444 compiler/analyzer
warning lines. The solution graph repeats some diagnostics, so the actionable
baseline is 218 distinct `(code, file, line)` locations. The checker also
reports line-only moves separately, so source reorganization does not look like
a newly introduced diagnostic:

| Project | CS8600 | CS8602 | CS8603 | CS8604 | CS8605 | CS8618 | CS8620 | CS8625 | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `GxMcp.Gateway` | 61 | 4 | 9 | 15 | 0 | 5 | 1 | 0 | 95 |
| `GxMcp.Gateway.Tests` | 48 | 15 | 3 | 23 | 6 | 0 | 21 | 7 | 123 |
| **Total** | **109** | **19** | **12** | **38** | **6** | **5** | **22** | **7** | **218** |

`MSB3277` is not emitted. The two xUnit1012 diagnostics reported by the issue
are not emitted, and the benchmark initialization warnings in
`SearchRankParallelismBenchmark.cs` are not emitted.

## Policy

- A Release rebuild must exit successfully.
- `MSB3277` must remain at zero; its suppression is scoped to
  `GxMcp.Worker.Tests` and does not hide compiler/analyzer warnings.
- Future warning work should compare distinct `(code, file, line)` locations
  against the JSON manifest and must not add a new `(code, file)` diagnostic
  without an explicit baseline update reviewed with a Release rebuild. Line
  shifts are reported as `moved` and remain visible in the command output.
- The release script runs
  `.\scripts\check-build-warning-baseline.ps1` without `-UpdateBaseline` and
  fails on `MSB3277` or any new warning location.
