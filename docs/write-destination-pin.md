# Pinning the write destination across restarts

A dedicated Gateway profile may set both environment variables:

```json
{
  "GXMCP_EXPECTED_KB_PATH": "C:\\KBs\\Test",
  "GXMCP_EXPECTED_KB_VERSION": "Development"
}
```

The Gateway already forwards `GXMCP_*` variables to its Worker. Configure these
on the process dedicated to that KB, not on a shared multi-KB Gateway. Both pins
must be present; neither configured retains existing behavior. A partially
configured pin refuses opening and potentially mutating commands.

The one command that does not accept "neither configured" is the isolated Events
save (`requireObjectSave`, see [Events save isolation](events-object-save-isolation.md)):
without both pins it refuses with `WriteDestinationPinsMissing` (or
`WriteDestinationConfigurationIncomplete` for one) instead of running unguarded,
and the refusal lists every other independent blocker, such as an SDK bundle that
was never audited, so the pins are not mistaken for the whole requirement.

The values belong to the owner of that Gateway profile. They are confirmed against
the SDK (the KB the Gateway opened, the active version from
`genexus_kb_version action=list`) and written once into that profile; the MCP never
copies the active version of a KB into a pin, never derives a pin from a request,
and one profile's pins are never shared with another profile or set machine-wide.

An edit of the profile reaches a Worker only through a Gateway started after the
edit. The isolation report shows what the Worker has (`effectivePins.variables`
says `absent`, `blank` or `present` for each pin; the KB path appears only as an
identity hash and the version by name), so a pin that was written but is still
`absent` there is pending until the Gateway is restarted with no operation in
flight. `profileRevision` in that report is the revision of the Gateway
configuration file (`native-config`), not of the entry that declared the pins.

Before SDK Open, the Worker checks the requested KB path against the pin. Before
dispatching a potentially mutating command, it checks the opened KB location and
`KBVersion.GetActive(kb).Name/IsFrozen`. Paths accept `.gx`/`.gxw` filenames or
root folders, normalize separators/relative segments and compare without case.
Version names compare without case. Unknown commands are guarded conservatively.
Known reads and status remain available when the selected version differs.
With pins configured, commands needing the fence are scheduled on the SDK STA
queue; the fence cannot introduce a `KBVersion` SDK call on the parallel MTA
status path. Health, doctor, build status/results and index status remain
observations and can run while the KB is opening.

The pin does not select or update a version automatically. After restart, a
different SDK-selected version yields `WriteDestinationVersionMismatch` and no
mutation dispatch. Recovery requires an explicit call:

```json
{"action":"set_active","targetVersion":"Development","autoUpdate":false,"kb":"test"}
```

Use `genexus_kb_version` for that call (it is exposed by the `devops` and `all` tool profiles only). A frozen target is refused. Activation
with another target or with absent/true `autoUpdate` is refused. Activating a
version is itself a separate explicit operation, not a dry-run or read.

Dry-run mutation commands are checked too; a preview on an unintended version
must fail. The destination is rechecked on the subsequent write; the existing
object `versionToken` remains independently required by its API.

Errors include expected/actual KB and version, frozen state, and `persisted:false`.
No Save, Build, Apply, Generate or implicit version activation is performed by
the guard. Its check uses the Worker's current SDK state: it is not a database
lock or a cross-process CAS guarantee. External SDK cache staleness and changes
after the pre-dispatch check require integration validation in a disposable KB;
offline tests cannot certify those SDK behaviors. Never infer IDE selection from
the pin or from a successful read alone.

Offline regression coverage exercises path and case normalization, partial
configuration, wrong destinations, frozen/unknown writability, simulated restart
or version change after a preview, conservative command routing, and explicit
activation restrictions. Dispatcher regressions use an unopened service without
mutation handlers, including nested arguments and dryRun, and verify rejection
before handlers and unchanged auto-open state. Routing tests cover the STA fence
and status bypass. It opens no KB. On upstream builds with an SDK manifest
for another installed update, compiling with `GxMcpSkipSdkValidation=true` proves
only source compilation/tests against the chosen SDK, not manifest compatibility
or readiness to promote that upstream binary.
