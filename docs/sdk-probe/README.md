# GeneXus SDK surface — investigation artifacts

This directory holds a structured map of the GeneXus 18 SDK as loaded by the MCP
worker. It exists to make future investigations cheap: instead of dumping ad-hoc
reflection probes from inside the apply code path, anyone can grep the JSON or
read the markdown indices to find the right entry point.

## Files

- **`raw.json`** — full structured dump of every public type across all loaded
  `Artech.*`, `Genexus.*`, `DVelop.*`, `GeneXus.*` assemblies. Per type: methods,
  properties, constructors, fields, base type, interfaces. ~17 MB; not git-friendly
  but useful for `jq` queries.
- **`INDEX.md`** — human-navigable per-assembly / per-namespace index. Quickly
  see which types exist where without opening the JSON.
- **`generators.md`** — focused list of generator/builder/applier/projector
  candidates. Filters `raw.json` by name keywords likely to participate in code
  generation (`Generator`, `Builder`, `Apply`, `Generate`, `Refresh`, `Update`,
  `Project`, `Process`, `Execute`, `Render`, `Compose`, `Materialize`, `Wire`,
  `Bind`, `Attach`, `Engine`, `Helper`, `Service`, `Resolver`).
- **`wwp-projection-discovery.md`** — narrative of how we found the pattern
  projection lifecycle (`IPatternBuildProcess.UpdateParentObject`) and what
  actually wires PatternInstance → WebForm.
- **`wwp-derived-regeneration.md`** — why `reapply` leaves the derived objects (the `LoadDVCombo<Trn>`
  helper) on their old contract, what `PatternEngine.ApplyPattern` rebuilds, and the limits of what was run.

## How to refresh

The probe runs on every `genexus_apply_pattern` call. It writes to:

1. `$GX_MCP_SDK_PROBE_DIR` if set, OR
2. `<repo>/docs/sdk-probe/` if the heuristic walk-up from the worker exe finds
   a `docs/` directory, OR
3. `%TEMP%/gxmcp_sdk_probe/` as fallback.

Set `GX_MCP_SDK_PROBE_DIR=C:\Projetos\Genexus18MCP\docs\sdk-probe` before
launching the worker if you want fresh artifacts in this repo on every run.

## Useful one-liners

```bash
# Find all types implementing a specific interface
jq '.assemblies[] | .types[] | select(.interfaces[]? == "<IFullName>") | .fullName' raw.json

# Find subclasses of a type
jq '.assemblies[] | .types[] | select(.baseType == "<Full.Type.Name>") | {fullName, assembly}' raw.json

# Find methods matching a pattern across all WWP types
jq '.assemblies[] | select(.name | test("WorkWithPlus")) | .types[] |
    select(.methods[]? | test("Project|UpdateParent"; "i")) | .fullName' raw.json

# All public constructors of a type
jq '.assemblies[] | .types[] | select(.fullName == "<Full.Type.Name>") | .constructors' raw.json
```

## Probe stats (current run)

- **Assemblies scanned:** 103
- **Public types:** 14,609
- **Generator candidates:** 2,456

See the per-assembly breakdown in `INDEX.md`.
