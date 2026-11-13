# KB change history

The GeneXus IDE has no `git log`, `git diff` or `git blame` for object sources. With the
history feature on, the Gateway keeps a **local shadow git repository** of what agents changed
through the MCP server, so you read it with plain git. It is off by default, adds no MCP tool
and never writes inside your KB folder.

## Enable

Set in the environment that launches the MCP client (see
[environment variables](environment_variables.md#kb-change-history-git-shadow-repository)):

```text
GXMCP_KB_HISTORY=1
GXMCP_KB_HISTORY_DIR=D:\kb-history      # optional; default %LOCALAPPDATA%\GxMcp\history
```

`git` must be on `PATH`. If it is not, or the repository cannot be created, the feature turns
itself off for that process with one `[KbHistory] disabled for this process` log line; tool
calls are never affected.

## What is recorded

One commit per change, after the Worker confirms the write was persisted by a fresh re-read:

- `genexus_edit` on a single object part (`mode` full, patch or ops): the part text as the
  Worker re-read it, at `<Type>/<Name>/<Part>.gx`. The type comes from the `type` argument;
  when it is not given the folder is `Object`.
- `genexus_delete_object` with `confirm=true`: the object folder is removed.

Nothing is recorded for dry runs, previews, failures, unchanged text, uncertain outcomes
(timeouts, `persistedState` not `confirmed`), multi-target edits (`parts`, `targets`,
`changeSet`), `async=true` edits, or other tools. Those are not in this version.

Commit subject: `genexus_edit patch Procedure MyProc (Source)`. The body carries `KB`,
`Session`, `Operation` (the call's correlation id) and `Content-SHA256`; free-form values
pass through the log redaction. The commit author is the fixed identity `GeneXus MCP`; your
git configuration is neither read nor changed.

## Where the repository is

One repository per KB, named `<alias>-<12 hex of the KB path hash>` under the history
folder. The first use of each KB logs its exact path (`[KbHistory] repository for KB ...`).

## Read it

```powershell
$repo = "$env:LOCALAPPDATA\GxMcp\history\MyKb-0123456789ab"
git -C $repo log --oneline -- Procedure/MyProc          # history of one object
git -C $repo log -p -- Procedure/MyProc/Source.gx       # every change, with diffs
git -C $repo diff HEAD~3 -- Procedure/MyProc            # what changed in the last 3 commits
git -C $repo blame Procedure/MyProc/Source.gx           # which call wrote each line
git -C $repo log --grep "Session: abc"                  # everything one session changed
```

## Cost and limits

- With the feature off there is no added work. With it on, the request path only checks the
  Worker's receipt and enqueues a job; no git process runs before the response is returned.
- A single background worker commits, with a bounded queue of 256 jobs. If a burst
  outpaces git, the oldest pending entries are dropped (each job carries the whole part, so
  the newest state is never lost, only intermediate commits) and a counter is logged.
- On shutdown pending jobs are drained for up to 3 seconds and the rest are abandoned; the
  stop line logs the counters (`enqueued`, `committed`, `unchanged`, `dropped`, `failed`,
  `skipped`, `abandoned`).
- Commit time is dominated by starting two git processes (`add`, `commit`), so it varies
  with the machine and antivirus.
- The repository records what went through the MCP server. Edits made in the GeneXus IDE are
  not seen.
