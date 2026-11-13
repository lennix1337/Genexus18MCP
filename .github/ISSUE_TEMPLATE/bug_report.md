---
name: Bug report
about: Worker crash / disconnect / timeout, wrong result, or any defect
title: "[Bug] "
labels: ""
assignees: ""
---

<!--
Please DON'T paste source code, object contents, KB paths, or anything private.
The diagnostics collector below already redacts paths / user / host / KB names
and credential values (tokens, passwords, api keys, authorization headers,
connection strings, user ids) — run it and paste its output; that's usually
enough for us to debug a crash without your KB.
-->

## Environment & Versions

- **GeneXus SDK Major & Version:** (e.g. GeneXus 16 / 17 / 18, Build `16.0.11.144151` or `18.0.10.184260`)
- **Knowledge Base Major & Generator:** (e.g. GX16 .NET Framework, GX18 .NET Core 8, Java)
- **AI Client:** (e.g. Cursor, Claude Desktop, Antigravity, OpenCode, VS Code)
- **Genexus18MCP Package Version:** (from `genexus-mcp --version` or `genexus_whoami`)
- **Transport Mode:** (`stdio-isolated`, `direct stdio`, `HTTP`)

## What happened

<!-- One or two sentences describing what failed. For a crash: "Worker exited / MCP disconnected". -->

## The exact tool call

<!-- The genexus_* tool + arguments that triggered the issue. -->

```json
{ "tool": "genexus_edit", "args": { } }
```

## Error output / MCP response

<!-- Paste the exact JSON envelope returned by the MCP server, especially error code, message, hint and diagnosticContext. -->

```json
{
  "status": "error",
  "error": { }
}
```

## Expected vs actual

- **Expected:**
- **Actual:**

## Doctor report right after the failure (most useful part)

<!--
Reproduce the problem, then, within the hour and without restarting the MCP,
call `genexus_doctor` (pass kb=<alias> if more than one KB is open) and paste
`result.issueReport`. Its `walkthrough` lists the last 20 tool calls with their
error codes, exception types and SDK frames, with every object name, KB alias,
source text and path replaced by tokens such as <v1> or <text 120 chars>, so we
can replay your steps on our own test KB. Read it before posting; it is plain JSON.
-->

<details><summary>genexus_doctor result.issueReport</summary>

```json
paste here
```

</details>

## Diagnostics bundle (redacted — please run and paste)

<!--
Run this from the installed package folder (or clone) and paste the file's
contents. It collects versions (Node, OS, GeneXus SDKs found on machine,
active config, worker ledger, log markers), redacting paths/user/host/KB and
credential values.

  pwsh -File scripts/collect-diagnostics.ps1

Alternatively, run:
  npx genexus-mcp doctor --format json
-->

<details><summary>Diagnostics output (genexus-mcp-diagnostics.txt / doctor output)</summary>

```
paste here
```

</details>

## Steps to reproduce / Additional context

- Steps to reproduce or narrow down the problem.
- Output of `genexus_whoami` (specifically `geneXus.actualVersion`, `geneXus.matchedMajor`, and `worker.deaths`).
