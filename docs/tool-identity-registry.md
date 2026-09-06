# Tool identity registry

> Design doc for Plans 046 and 075. The registry answers the identity/action
> queries below from the canonical schema and legacy rewrite table. The
> cache-invalidation gate now consumes its explicit mutation projection for
> recognized actions and aliases; compatibility branches remain for tools that
> have not yet been migrated.

## 1. The drift surface (inventory)

Two authoritative sources already exist and are not in question:

- `src/GxMcp.Gateway/tool_definitions.json` — canonical tool names + each
  tool's `action` enum (when it has one) in `inputSchema.properties.action.enum`.
- `McpRouter.TryRewriteLegacyTool` (`McpRouter.cs:1120-~1420`) — the
  legacy-name → `(canonical tool, action)` rewrite table, applied before
  routing so old client calls keep working.

Independently-maintained catalogs found that hard-code tool names *outside*
those two sources (`grep -rn '"genexus_' src/GxMcp.Gateway --include=*.cs | grep -v Tests`,
19 non-test files touch a `"genexus_..."` literal; the ones below hold a
**list of tool identities** used for a decision, not just a one-off string):

| Catalog | Keying today | Confirmed stale? |
|---|---|---|
| `NextLegalActionsBuilder.cs:27-39,53-63` | `switch` on tool name for the "read-only, skip suggestions" set and the "build suggestions for this tool" set. | **Yes** — `genexus_create_object`, `genexus_create_popup`, `genexus_save_as` (lines 56-60) are legacy names; post-consolidation calls arrive as `genexus_create` with `action=object/popup/save_as`, so these cases are dead code (plan 041). |
| `ToolHelpCatalog.cs:9-254` (`_helpTexts` dictionary) | Dictionary keyed by tool name. | **Yes** — `genexus_create_object` (line 198) and `genexus_db_optimize` (line 254) are legacy keys; a help lookup for `genexus_create` or `genexus_db` finds nothing (plan 044). |
| `DidYouMean` candidate lists | Not itself a list — `DidYouMean.Suggest`/`FormatSuggestionMessage` take `IEnumerable<string> candidates` from the caller. Today's callers (`ObjectRouter.cs:9,14` — `_validEditModes`, `_validSemanticOps`) pass edit-mode/semantic-op vocab, not tool names. | N/A today — but there is **no candidate list of valid tool names** anywhere, so an unknown-tool-name typo gets no "did you mean" at all. This is a gap, not a stale value. |
| `RemovedToolsRegistry.cs:20-32` (`Map` dictionary) | Dictionary keyed by fully-removed tool name → `{ReplacedBy, ArgHint}`. Consulted in `McpRouter.cs:248` and `Program.RequestLoop.cs:97` to turn a call to a dead tool into an actionable error instead of "unknown tool". | Not stale (its 7 entries — `genexus_batch_read`, `genexus_batch_edit`, `genexus_open_kb`, `genexus_get_sql`, `genexus_get_sql_for_navigation`, `genexus_summarize`, `genexus_explain_code` — still point at live replacements), but it is a **fourth independently-maintained tool-name list** with no link back to `tool_definitions.json` or `TryRewriteLegacyTool`. See §5 open question. |
| `MacroSuggestionService.cs:29-39` (read-only-tools `HashSet`) | Own copy of the "read-only, don't suggest macros for this" tool-name set — near-identical intent to `NextLegalActionsBuilder`'s `_readOnlyTools` but maintained separately. | Not verified stale, but is a **second copy of the same read-only-tool-set concept**, which is itself a drift risk (the two lists can silently diverge). |

Net: the plan's "at least two catalogs already stale" claim is confirmed for
`NextLegalActionsBuilder` and `ToolHelpCatalog`. The inventory also surfaced
`RemovedToolsRegistry` and `MacroSuggestionService`'s read-only set as
additional independently-maintained tool-name lists (five total, counting
`DidYouMean`'s gap). None of these newly-found lists change the registry's
feasibility or core design — `RemovedToolsRegistry` answers a genuinely
different question (which legacy names have **no** forwarding path at all)
and is folded into the API below as `IsRemoved` rather than treated as a
competing source of truth for canonical names.

## 2. Registry API

```csharp
namespace GxMcp.Gateway
{
    public static class ToolIdentity
    {
        // All canonical tool names, as declared by tool_definitions.json's
        // top-level "name" field. Sourced from: tool_definitions.json.
        public static IReadOnlyList<string> CanonicalToolNames { get; }

        // Legacy alias or canonical name -> canonical name. For a canonical
        // name, returns it unchanged. For a legacy alias recognized by
        // TryRewriteLegacyTool, returns the umbrella tool it rewrites to.
        // Unknown input returns the input unchanged (caller checks
        // IsKnownTool first if it needs to distinguish "unknown" from
        // "already canonical").
        // Sourced from: tool_definitions.json (for the canonical case) +
        // McpRouter.TryRewriteLegacyTool (for the alias case, driven with a
        // throwaway JObject since the rewrite only needs the tool name to
        // decide the destination tool).
        public static string ResolveCanonical(string nameOrAlias);

        // The action enum for a canonical tool's `action` property, or
        // empty if the tool has no `action` (some tools, e.g. genexus_query,
        // are not umbrella tools and take no action).
        // Sourced from: tool_definitions.json,
        // inputSchema.properties.action.enum.
        public static IReadOnlyList<string> ActionsFor(string canonicalTool);

        // True for a canonical name (found in tool_definitions.json) OR a
        // legacy alias that TryRewriteLegacyTool recognizes.
        // Sourced from: tool_definitions.json + TryRewriteLegacyTool.
        public static bool IsKnownTool(string name);

        // True if `name` is a fully-removed legacy tool (RemovedToolsRegistry
        // entry) with no forwarding rewrite — i.e. calling it today is a
        // hard error, not a silent alias.
        // Sourced from: RemovedToolsRegistry.Map.
        public static bool IsRemoved(string name);
    }
}
```

Caching strategy mirrors `GatewayArgsValidator` (`GatewayArgsValidator.cs:217-234`):
load `tool_definitions.json` once behind a `lock`, parse to a `JArray`, cache
the derived `CanonicalToolNames`/per-tool action lists in `Dictionary`s keyed
by tool name, and re-use the same walk-up-from-`AppContext.BaseDirectory`
search `GatewayArgsValidator.LocateToolDefinitions` uses (the prototype
duplicates that ~15-line walk rather than taking a dependency from
`ToolIdentity` back into `GatewayArgsValidator`, since the plan's Scope
disallows touching existing files). `ResolveCanonical`/`IsKnownTool` do not
cache `TryRewriteLegacyTool` calls — that method's own switch is already O(1)
and takes no I/O, so no cache is needed there.

The registry deliberately has **no independently-maintained tool list of its
own** — every member above is a projection or cache of `tool_definitions.json`
and/or `TryRewriteLegacyTool`, so it cannot itself drift from the two
authoritative sources the way the four/five catalogs in §1 have.

## 3. Prototype

`src/GxMcp.Gateway/ToolIdentity.cs` implements the API above against the real
`tool_definitions.json` and the real `McpRouter.TryRewriteLegacyTool` (both
are already `internal`, resolvable from within the same assembly).
`src/GxMcp.Gateway.Tests/ToolIdentityTests.cs` asserts the three key queries
from the plan:

- `ResolveCanonical("genexus_create_object") == "genexus_create"`
- `ActionsFor("genexus_create")` contains `object`, `popup`, `save_as`
- `IsKnownTool("genexus_create")` and `IsKnownTool("genexus_create_object")` are both true

All pass against the live data (see verification log at the bottom of this
doc). `OperationClassifier` owns the explicit action/actionless/mode policy and
`Program.IsMutatingTool` delegates to it, so a newly classified write cannot
depend on a substring match to invalidate semantic cache state. The
`NextLegalActionsBuilder` canonicalizes aliases before selecting a follow-up,
and `ToolHelpCatalog.Get` resolves legacy aliases to canonical help entries.

## 4. Remaining migration proposal (follow-up plan sketch)

A follow-up plan ("047: migrate catalogs onto ToolIdentity", not written here)
would:

1. **`NextLegalActionsBuilder`** is migrated: it canonicalizes aliases and
   dispatches only canonical tool/action pairs. The remaining work is to move
   response-shape-specific suggestions into a data file only if that proves
   useful; the current builders intentionally inspect payload fields.
2. **`ToolHelpCatalog`** is already keyed canonically and resolves aliases at
   lookup time. The remaining work is per-action help granularity, not name
   identity.
3. **`DidYouMean`**: give the tool-name-typo path (wherever "unknown tool"
   errors are raised, e.g. `Program.RequestLoop.cs`) a candidate list of
   `ToolIdentity.CanonicalToolNames` so a call to a misspelled tool gets a
   real suggestion instead of silence.
4. **`RemovedToolsRegistry`**: leave as its own table (it answers "is this
   name gone with no forwarding", which `ToolIdentity.IsKnownTool` alone
   can't distinguish from "legacy alias, still forwards") but have
   `ToolIdentity.IsKnownTool` OR `IsRemoved` cover the full "have I ever heard
   of this name" question for any caller that needs it (e.g. a future
   DidYouMean integration that wants to suggest the replacement, not just
   the nearest string).
5. **Guard test** (new, in `GxMcp.Gateway.Tests`): enumerate every key in
   `ToolHelpCatalog._helpTexts` and every tool-name literal in
   `NextLegalActionsBuilder`'s `switch`/`_readOnlyTools`, assert
   `ToolIdentity.IsKnownTool(key)` is true for each. This is the test that
   would have failed the moment plan 041/044's root-cause commit landed
   (the umbrella consolidation), because `genexus_create_object` stops being
   a live tool name in `tool_definitions.json` at that exact commit — instead
   of silently shipping a catalog that resolves to nothing.

### Open questions

1. **Per-action vs per-tool help granularity.** `ToolHelpCatalog` today is
   one blob of text per tool. Umbrella tools like `genexus_create` cover 10+
   actions with very different arg shapes — does the migrated catalog need
   `[tool][action]` help, or is one tool-level blob (with action subsections
   inside the text) good enough? Affects both the catalog's shape and how
   big the guard test's "every key is known" check needs to be (tool-level
   only, or tool+action pairs).
2. **Should `next_legal_actions` become data-driven per action, or stay
   hand-written per case?** The current `switch` hand-picks which follow-up
   tool call to suggest per legacy tool. Migrating to `ToolIdentity` fixes
   the dead-name problem, but a fully data-driven table (e.g. a JSON map of
   `{tool, action} -> [{suggestedTool, why, priority}]`) would remove the
   `switch` entirely and let the guard test check 100% coverage instead of
   spot-checking known cases — is that worth the schema design, or does the
   bespoke reasoning in `BuildForApplyPattern` etc. (which inspects response
   payload shape, not just the tool name) make a fully declarative table
   infeasible?
3. **Cost of loading tool_definitions.json at gateway start vs on first use.**
   `GatewayArgsValidator` and the prototype both lazy-load on first call
   (small file, sub-millisecond parse) — is there a reason to eagerly load
   `ToolIdentity` at `Program.cs` startup instead (e.g. to fail fast if the
   file is missing/malformed, rather than have every catalog silently
   degrade)? Ties into whether `ToolIdentity`'s own failure mode (empty
   `CanonicalToolNames` if the file can't be found) should be louder than
   `GatewayArgsValidator`'s current silent-no-op.
4. **Should `MacroSuggestionService`'s duplicate read-only-tool set be
   collapsed into `NextLegalActionsBuilder`'s during the same migration?**
   They express the same concept today with two separately-maintained lists;
   fixing one drift source while leaving a near-identical sibling in place
   would be an obvious follow-up gap.

## 5. Verification log

```
$ dotnet build src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --no-restore -v:q
Build succeeded. 0 Warning(s), 0 Error(s)

$ dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --no-build --filter "FullyQualifiedName~ToolIdentity|FullyQualifiedName~OperationContractCoverage|FullyQualifiedName~OperationClassifier|FullyQualifiedName~IdempotencyMiddleware" -v:q
Passed! - Failed: 0, Passed: 57, Skipped: 0, Total: 57

$ dotnet test Genexus18MCP.sln --no-restore -v:q
Worker: Passed 2301, Skipped 4, Total 2305; Gateway: Passed 1395, Skipped 10, Total 1405.
```

The skipped cases are environment-gated live SDK/KB scenarios; the live gate
remains unavailable without the fixture variables described in the harness
documentation.
