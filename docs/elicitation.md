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
- `genexus_deploy action=deploy`.

A refusal response tells the agent not to retry the operation unless the user asks
again, so the agent cannot simply re-send it with `confirm=true`. In `strict` mode
the human is asked even when the agent already passed `confirm=true`.

## When it is active

Elicitation is used only when all of these hold:

1. the client declared `capabilities.elicitation` in `initialize`;
2. the session uses the stdio transport. HTTP sessions and the legacy HTTP proxy
   keep the non-interactive behaviour;
3. `GXMCP_ELICITATION` is not `off`.

When the client cannot answer (missing capability, an error reply, or an
unrecognised action), the call goes ahead as if elicitation did not exist, and the
Worker's own `confirm=true` check still applies. No existing contract changes for
clients that do not support elicitation.

## Configuration

| Variable | Values | Default |
|----------|--------|---------|
| `GXMCP_ELICITATION` | `auto`: KB picker, and confirmation only when `confirm` is missing. `strict`: also confirm when the agent passed `confirm=true`. `off`: never elicit. | `auto` |
| `GXMCP_ELICITATION_TIMEOUT_SECONDS` | How long to wait for the human. An unanswered confirmation is treated as a refusal; an unanswered KB picker returns the original error. | `300` |

## Implementation

- `src/GxMcp.Gateway/ElicitationBroker.cs` tracks client capabilities for each
  session, sends `elicitation/create` through the sender the transport registered,
  and correlates the client's response by its `gxmcp-elicit-<n>` id.
- `src/GxMcp.Gateway/Program.Elicitation.cs` holds the destructive-operation policy
  (`DescribeDestructiveOperation`), the confirmation step that runs before dispatch,
  and the KB recovery step that runs after it.
- Tests: `src/GxMcp.Gateway.Tests/ElicitationTests.cs`.
