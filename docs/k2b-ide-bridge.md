# K2B WebPanel Designer bridge (GeneXus 17)

`genexus_k2b_designer` edits an **already-active** K2B WebPanel Designer through
the open GeneXus IDE document. The IDE extension owns the native `FormDesignerPart`
and calls the normal document save. The Worker relays the request over a local
named pipe. No generated WebForm or Events text is written directly.

The first increment does not activate an inactive Designer, apply a K2B pattern,
or convert a legacy HW. Keep an independent KB backup for any live editing.

## Build and install the optional IDE extension

The extension is built locally against the installed GeneXus 17 and K2B SDK. It
is not loaded by the Worker and is not required for other MCP tools.

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus17'
dotnet build src\GxMcp.K2bIdeBridge\GxMcp.K2bIdeBridge.csproj -c Release
Copy-Item src\GxMcp.K2bIdeBridge\bin\Release\net48\GxMcp.K2bIdeBridge.dll "$env:GX_PATH\Packages" -Force
& "$env:GX_PATH\GeneXus.exe" /install
```

Copying into `Program Files` and registering the package normally require an
elevated shell. Restart the IDE after installation. The package is inactive
unless the environment below is set **before starting GeneXus**.

## Start an isolated test session

Use one explicit KB path and one pipe name in the IDE and MCP process. An
optional target restriction prevents the IDE bridge from editing other objects.

```powershell
$env:GXMCP_K2B_IDE_KB = '<absolute-path-to-a-disposable-kb>'
$env:GXMCP_K2B_IDE_PIPE = 'gxmcp_k2b_mytest_01'
$env:GXMCP_K2B_IDE_TARGET = 'MyPanel' # optional
& 'C:\Program Files (x86)\GeneXus\GeneXus17\GeneXus.exe'
```

Configure `GXMCP_K2B_IDE_PIPE` with the same value in the MCP Gateway/Worker
environment and open that KB explicitly through `genexus_kb`. Open the WebPanel
in the IDE and select its active Designer tab. The bridge rejects a mismatched
KB, another restricted target, an inactive Designer, and unsaved IDE changes.
The pipe grants access only to the current Windows user; it has no network
listener. An elevated IDE may connect to a pipe owned by the user's MCP process.

## MCP sequence

1. `inspect` returns the root node, document dirty state, and `version`.
2. `tree` returns node paths, properties, and available root child types.
3. `preview` validates a proposed `operation` (`set_property`, `add_node`,
   `move_node`, or `remove_node`) without saving.
4. Repeat the mutation with the same arguments and `expectedVersion`. Pass
   `confirm=true` to remove a node.
5. Reread the Designer and generated parts after a save. A returned
   `IdeSaveReturned` means the IDE save completed, while an independent reread
   proves persistence. A save failure sets `reconciliationRequired`.

K2B regeneration can assign new internal GUIDs in the generated WebForm even
when an edit is later reversed. A reversible test on GeneXus 17/K2B 13.1
produced a GUID-only WebForm diff while the Designer, Events, Variables, and
Rules returned to their original content. Review generated markup separately
from identifier churn; do not edit the generated WebForm to restore GUIDs.

Node paths use `root` for the WebForm node and zero-based child indexes such as
`0` or `0/1` for descendants. Property
names and node types must match those returned by the installed K2B SDK. A
version mismatch requires a fresh read and preview. The target document must
remain open and clean from preview through save.

The extension currently targets GeneXus 17 with the installed K2B package.
Other major versions require an extension built and tested against that IDE.
