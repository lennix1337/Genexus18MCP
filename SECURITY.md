# Security Policy

`genexus-mcp` is a Model Context Protocol server that lets AI agents drive a real Knowledge Base through a supported GeneXus SDK. That means it shells out, touches the filesystem, talks to a local worker over IPC, and runs against your actual KB. Treat it accordingly.

## Reporting a vulnerability

**Do not open a public issue for security reports.** Use one of:

- **GitHub private vulnerability reporting** — preferred: <https://github.com/lennix1337/Genexus18MCP/security/advisories/new>
- **Email** — `lucassouza@univali.br` with subject prefix `[genexus-mcp security]`

Please include:

1. Affected version (`npm view genexus-mcp version` or the tag you're on).
2. A minimal reproduction — config snippet, the exact MCP tool call, and the observed effect.
3. Whether the issue requires a malicious KB, a malicious AI prompt, a hostile local user, or just normal usage.

You should get an acknowledgement within **3 business days**. Fixes ship as patch releases on the latest minor.

## Supported versions

Only the **latest minor** receives security fixes. Older minors are not backported — upgrade with `npx genexus-mcp@latest`.

## Self-update

Managed installs can update themselves. The gateway reads the latest release from GitHub (`lennix1337/Genexus18MCP`), downloads `publish.zip` and `publish.zip.sha256` from that release, and stages the update only if the SHA-256 of the zip matches the published checksum **and** the payload contains the expected gateway and worker binaries. A release without a checksum, or with a mismatching one, is refused (fail closed; there is no opt-out). The staged update is applied on the next start.

To disable self-update entirely, set `GENEXUS_MCP_NO_SELF_UPDATE=1`; see [`docs/environment_variables.md`](docs/environment_variables.md).

## Threat model — what's in scope

In scope:

- Arbitrary code execution triggered by a crafted MCP request that the gateway shouldn't have honored.
- Path traversal / SSRF reaching outside the configured KB or GeneXus install root.
- Prompt-injection vectors in `tool_definitions.json` or in injected context that would manipulate the host agent beyond stated tool semantics.
- npm supply-chain issues (tampered `publish.zip`, missing provenance, etc.).
- Authentication/authorization bypass against the worker IPC channel.

Out of scope (these are **expected capabilities** for an MCP that wraps a desktop SDK):

- The package uses `child_process`, filesystem APIs, and `https` — that's how it spawns the .NET worker, reads/writes the KB, and checks for npm updates. Generic scanner flags on these are not vulnerabilities.
- The worker runs as the same user that launched the MCP and has full access to the configured KB. That is the design.
- A user pointing the MCP at a KB they don't own. Access control to the KB itself is GeneXus's job, not ours.
- Issues that only reproduce with a modified `config.json` you wrote yourself.

## Disclosure

I'll coordinate a fix and credit you in the release notes unless you ask to stay anonymous. Public disclosure happens after the patched version is on npm — usually same-day for the kind of issues this project realistically faces.
