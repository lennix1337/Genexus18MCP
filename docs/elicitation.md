# Human-in-the-loop elicitation

The Gateway can ask the **human** a question through the MCP client, using the
`elicitation/create` request from MCP revision 2025-06-18, instead of returning
an error the agent has to work around. Two situations use it today:

| Situation | Without elicitation | With elicitation |
|-----------|---------------------|------------------|
| A KB-bound tool call fails with `KB_AMBIGUOUS` or `KB_CONTEXT_REQUIRED` and no `kb` argument was passed | The agent gets the error and has to guess an alias or call `genexus_kb` first | The client shows a picker with the open and declared KBs. The choice is applied with `genexus_kb action=select` (session only, config untouched) and the original call is replayed once. The replayed result carries `_meta["gxmcp/humanSelectedKb"]`. |
| An irreversible call is made without `confirm=true` | The Worker rejects it and the agent usually re-issues it with `confirm=true` itself | The client asks the human to approve the described effect. Approval forwards the call with `confirm=true`; a refusal returns `HUMAN_DECLINED`, `HUMAN_CANCELLED` or `HUMAN_CONFIRMATION_TIMEOUT` and nothing reaches the Worker. |

Gated operations:

- `genexus_delete_object` unless `dryRun=true`;
- `genexus_data_view action=delete` unless `dryRun=true`;
- `genexus_transfer action=import` with `dryRun=false`;
- `genexus_io action=delete_kb_objects`, `import_part`, or `import_text_to_kb` when not previewing;
- full/default-mode writes through `genexus_edit` (including flat `targets`), `genexus_edit_and_build`, or legacy `genexus_write_object`;
- `genexus_lifecycle action=reorg`, and non-preview `build_all` and `rebuild` (also legacy `genexus_build` equivalents);
- `genexus_deploy action=deploy`.

Build All and Rebuild prompt regardless of KB size: the Gateway does not guess
whether a build is large enough to warrant approval. For reorganization, use
`reorg_preview`; `reorg` itself does not guarantee a non-mutating `dryRun`.
The human is asked even when the agent passed `confirm=true`. Decline or cancel
returns a tool error and stops the operation before dispatch.

## When it is active

Elicitation is used only when all of these hold:

1. the client declared `capabilities.elicitation` in `initialize`;
2. the session uses the stdio transport. HTTP sessions and the legacy HTTP proxy
   keep the non-interactive behaviour;
3. `GXMCP_ELICITATION` is not `off`.

Without the capability, a registered transport sender, or with elicitation off,
the Gateway uses its existing non-interactive path. Once it asks for approval,
it does not dispatch on a timeout, client error, or unrecognised answer. The KB
picker still returns the original context error if it cannot get a selection.

## Configuration

| Variable | Values | Default |
|----------|--------|---------|
| `GXMCP_ELICITATION` | `auto` or `strict`: KB picker and destructive-write confirmation, even with `confirm=true`. `off`: never elicit. | `auto` |
| `GXMCP_ELICITATION_TIMEOUT_SECONDS` | How long to wait for the human. An unanswered confirmation is treated as a refusal; an unanswered KB picker returns the original error. | `300` |

## Implementation

- `src/GxMcp.Gateway/ElicitationBroker.cs` tracks client capabilities for each
  session, sends `elicitation/create` through the sender the transport registered,
  and correlates the client's response by its `gxmcp-elicit-<n>` id.
- `src/GxMcp.Gateway/Program.Elicitation.cs` holds the destructive-operation policy
  (`DescribeDestructiveOperation`), the confirmation step that runs before dispatch,
  and the KB recovery step that runs after it. The multi-action subset of this policy
  is also published to clients in `tools/list` (profile `all`) under `_meta["gxmcp/actionEffects"]`
  (`src/GxMcp.Gateway/ActionEffectCatalog.cs`).
- Tests: `src/GxMcp.Gateway.Tests/ElicitationTests.cs`.
