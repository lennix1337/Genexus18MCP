# Troubleshooting

Common issues when installing or running the GeneXus MCP server, and how to fix them.

> **First step for any issue:** run `npx genexus-mcp doctor --mcp-smoke` and read the output. It checks GeneXus path, KB path, worker availability, .NET runtimes, and runs a protocol smoke test. If an stdio client only reports an exit code, also read `%LOCALAPPDATA%\GenexusMCP\logs\last-stdio-error.txt`.

---

## Installer issues

### "GeneXus installation not found"

The installer couldn't locate GeneXus 18 in the default path.

**Fix:** pass `--gx` explicitly. The path is the folder that contains `GeneXus.exe` — usually:

```bash
npx genexus-mcp@latest init --gx "C:\Program Files (x86)\GeneXus\GeneXus18"
```

If GeneXus is installed somewhere else (custom install, network drive), point `--gx` to that folder.

### "Knowledge Base not found" / "KB path invalid"

The folder you passed isn't a GeneXus KB.

**Checks:**
- The path must point to the **KB root folder** (the one that contains the `.gx` file and folders like `Model/`, `WebSpa/`, etc.), not a parent directory.
- The KB must have been **opened in GeneXus IDE at least once** so it's initialized and built.
- Make sure the path doesn't have unescaped quotes or trailing slashes.

```bash
npx genexus-mcp@latest init --kb "C:\KBs\YourKB"
```

### Installer succeeds, but an npm bootstrap launcher is slow on every launch

`npx` resolves the package for clients that use the npm launcher. Antigravity skips
that bootstrap when the packaged gateway executable is available. For every client,
use the fixed-path installer when you need a stable executable path:

```pwsh
iex (irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1)
```

The fixed-path installer registers the gateway directly and avoids the npx cache.

---

## AI client doesn't see the GeneXus tools

You ran the installer, restarted the client, but no `genexus_*` tools show up.

### Step 1 — Confirm the MCP is registered

The installer prints a JSON block. It must appear in your client's MCP config. Where to find that file:

#### Client setup

| Client | Config file |
|---|---|
| **Claude Desktop** | `%APPDATA%\Claude\claude_desktop_config.json` |
| **Claude Code** | `%USERPROFILE%\.claude.json` (or run `claude mcp list`) |
| **Cursor** | Settings → MCP → check the `mcpServers` block |
| **Antigravity** | `%USERPROFILE%\.gemini\config\mcp_config.json` (or `%USERPROFILE%\.gemini\antigravity\mcp_config.json`) |

The relevant block looks like:

```json
{
  "mcpServers": {
    "genexus": {
      "command": "npx.cmd",
      "args": ["genexus-mcp@latest"]
    }
  }
}
```

> ⚠️ On Windows, clients using the npm launcher must use `npx.cmd`, not `npx`. Plain `npx` fails because clients launch processes without a shell. Antigravity normally receives a direct `GxMcp.Gateway.exe` path from `init`.

### Step 2 — Fully restart the client

"Restart" means closing **all** windows of the client and reopening — not just refreshing a chat. For Claude Desktop, also check the tray icon and quit from there.

### Step 3 — Check the client's MCP logs

- **Claude Desktop**: `%APPDATA%\Claude\logs\mcp*.log`
- **Claude Code**: `claude --debug` shows MCP startup
- **Cursor**: Output panel → "MCP" channel

Look for `genexus-mcp` startup messages or errors.

### Antigravity only shows `exit status 1` or `0xffffffff`

The Antigravity Language Server may discard the child process stderr. Read the
launcher breadcrumb from PowerShell:

```powershell
Get-Content "$env:LOCALAPPDATA\GenexusMCP\logs\last-stdio-error.txt"
```

The file is written by the npm wrapper for missing executables, spawn failures, and
non-zero gateway exits. It includes the UTC timestamp, exit code, and the last 64 KiB
of stderr. If `genexus-mcp clients` reports Antigravity's launcher as stale, refresh
the package path with:

```powershell
npx genexus-mcp@latest clients add --clients antigravity
```

### Step 4 — Verify the gateway can start standalone

```bash
npx genexus-mcp status
npx genexus-mcp doctor --mcp-smoke
```

If `doctor` passes but the client still doesn't see tools, the problem is the client's config (back to Step 1).

---

## Worker / .NET issues

### "Worker failed to start" / .NET 4.8 errors

The MCP has two parts:
- **Gateway** (.NET 10) — runs always
- **Worker** (.NET Framework 4.8) — hosts the GeneXus SDK, spins up on first command

The worker needs **.NET Framework 4.8** installed on Windows. It's bundled with Windows 10 (1903+) and Windows 11, but on Server SKUs or older installs you may need to install it manually: [.NET Framework 4.8 download](https://dotnet.microsoft.com/download/dotnet-framework/net48).

You can also check:

```bash
npx genexus-mcp doctor
```

It reports the .NET runtimes detected.

### "Worker idle timeout" — first request slow

Expected. The worker is lazy by design and shuts down after `WorkerIdleTimeoutMinutes` (default 5) of inactivity to unlock GeneXus build artifacts. First request after idle takes ~3-8s to spin it back up; subsequent calls are fast.

To keep it warm longer, edit `config.json`:

```json
{ "Server": { "WorkerIdleTimeoutMinutes": 30 } }
```

### Build artifacts locked / "file in use" when building in GeneXus IDE

The worker holds open handles to KB files while running. If you need to do something in the GeneXus IDE that conflicts (rebuild, change DBMS, etc.):

```bash
npx genexus-mcp lifecycle --action stop-worker
```

The worker will respawn on the next MCP call.

---

## Networking

### "Port 5000 already in use"

Another app is using port 5000 (often IIS Express, Skype, or another dev server). Change the port in `config.json`:

```json
{ "Server": { "HttpPort": 5050 } }
```

stdio mode (the default for AI clients) doesn't need the port — this only matters if you use the HTTP `/mcp` endpoint.

### Gateway lease conflicts

If you see "another gateway holds the lease" errors at `%LOCALAPPDATA%\GenexusMCP\gateway-leases`, clean stale leases:

```powershell
Remove-Item "$env:LOCALAPPDATA\GenexusMCP\gateway-leases\*" -Force
```

Then retry. This is safe — leases regenerate.

---

## Permissions

### "Access denied" writing to `%LOCALAPPDATA%\GenexusMCP\`

The gateway and worker keep their cache, index, and snapshot data under
`%LOCALAPPDATA%\GenexusMCP\` (and `%LOCALAPPDATA%\GxMcp\`). There is no
dedicated env var to move just this cache — the location is derived from the
OS "Local Application Data" folder. If a corporate policy locks it down:
- Ask IT to whitelist `%LOCALAPPDATA%\GenexusMCP\` (and `%LOCALAPPDATA%\GxMcp\`), or
- Redirect the whole Local AppData folder for the launching user to a writable
  location (e.g. point the `LOCALAPPDATA` environment variable at `D:\AppData\Local`
  before starting the AI client), which moves this cache along with everything else.

> All runtime environment variables are listed in [`docs/environment_variables.md`](docs/environment_variables.md).

### KB is on a network drive and reads are slow / fail intermittently

Network KBs work but the SDK doesn't love them. Recommended: clone the KB to a local SSD and point `--kb` there. If you must use a network drive, increase `Server.SessionIdleTimeoutMinutes` to 30+.

---

## Tool-specific issues

### `genexus_edit` returns "validation failed"

The XML or ops you sent didn't pass the SDK validator. Tips:
- Run with `dryRun: true` first to see the validation report without mutating.
- Use the `ops` mode for semantic operations (`set_attribute`, `add_rule`, …) instead of raw XML when possible — it's harder to break.
- For `patch` mode, ensure your JSON-Patch ops target the canonical JSON shape (see [`docs/object_json_schema.md`](docs/object_json_schema.md)).

### `genexus_lifecycle` build hangs

GeneXus builds can take minutes on large KBs. The MCP returns an `operationId` and you should poll:

```
genexus_lifecycle({ action: "status", target: "op:<operationId>" })
```

Don't kill the call early — the build is still running in the worker.

### Layout SDK colors look wrong

For `ForeColor`, `BackColor`, `BorderColor`, send values as palette names (`Black`, `Blue`, `Red`, `Transparent`) or RGB token (`R; G; B|`) — not hex strings. The SDK wraps hex incorrectly when nested.

---

## Reporting bugs

If none of the above helps:

1. Run `npx genexus-mcp doctor --mcp-smoke > diagnostic.txt 2>&1` and include `%LOCALAPPDATA%\GenexusMCP\logs\last-stdio-error.txt` when the client only reports an exit code.
2. Reproduce the issue with `claude --debug` (or your client's equivalent) to capture MCP traffic.
3. [Open an issue](https://github.com/lennix1337/Genexus18MCP/issues) and attach `diagnostic.txt` + the client log excerpt. Include:
   - GeneXus 18 version (Help → About in the IDE)
   - Node.js version (`node --version`)
   - Windows version
   - Your `config.json` with paths redacted if sensitive

That gets the issue triaged fast.
