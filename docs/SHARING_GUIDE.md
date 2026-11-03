# Nexus-IDE Sharing Guide

This guide explains how to install and configure Nexus-IDE and how to point external MCP clients to the correct transport.

## Installing the extension

1. Obtain `nexus-ide.vsix`.
2. In VS Code, open Extensions.
3. Choose `Install from VSIX...`.
4. Select the package.

## Initial setup

Nexus-IDE tries to locate:

- the active KB from the workspace
- the GeneXus 18 installation from configuration

If automatic detection is not enough, update the relevant GeneXus settings and reload VS Code.

## Runtime expectation

Nexus-IDE uses MCP directly through `/mcp`.

## Sharing MCP configuration

To connect Claude Desktop, Copilot, Cursor, or another MCP-capable client, point it to the gateway and use the MCP transport, not the legacy gateway command endpoint.

### HTTP MCP

Available only with the shared gateway (`McpStdio: false` or `SharedGateway: true`); with `McpStdio: true` alone nothing listens on `HttpPort`. Use:

- `http://127.0.0.1:5000/mcp`
- `MCP-Protocol-Version: 2025-11-25`

Flow:

1. `initialize`
2. `tools/list`, `resources/list`, `prompts/list`
3. `tools/call`, `resources/read`, `prompts/get`

### stdio MCP

Point the client to the published gateway executable or the startup script that launches it.

## What to avoid

- Do not teach consumers any non-MCP HTTP contract.
- Do not hardcode old tool names from the pre-MCP wrapper phase.
