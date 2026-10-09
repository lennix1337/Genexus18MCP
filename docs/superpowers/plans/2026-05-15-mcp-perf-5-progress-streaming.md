# Sub-plan 5 — Progress Streaming via `operationId`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use `- [ ]` syntax.

**Goal:** Stream live `notifications/progress` events bound to each long-running operation so the LLM (or any MCP client) can read progress without polling `genexus_lifecycle action=status`. Today only indexing emits progress, using a fixed demonstration value. The plan binds the progress field to `operationId` at the call site, propagates it through the worker request, and lets `BuildService`, `AnalyzeService.ImpactAnalysis`, and `KbService.BulkIndex` emit progress under that value.

**Architecture:** Three coordinated changes. (1) Gateway: in `SendWorkerCommandAsync`, when an `operationId` is generated, attach it to the worker RPC request body as `_meta.progressToken`. (2) Worker: `CommandDispatcher` extracts that token at the start of every dispatch and stashes it in an `AsyncLocal<string?>` (`ProgressContext.CurrentToken`). The three long-running services read `ProgressContext.CurrentToken` and call a new helper `ProgressEmitter.Emit(progress, total, message)` which serializes a `notifications/progress` JSON-RPC line to stdout. (3) Gateway: the existing forwarding path at `src/GxMcp.Gateway/Program.cs:919-932` already relays `notifications/progress` to stdio + HTTP — no transport change needed. We add a test harness that captures stdout in-process to assert progress arrives.

**Tech Stack:** .NET 8 (Gateway), .NET Framework 4.8 (Worker), MCP JSON-RPC `notifications/progress`, `AsyncLocal<T>`, xUnit.

---

## File Structure

- **Create:** `src/GxMcp.Worker/Helpers/ProgressContext.cs`
- **Create:** `src/GxMcp.Worker/Helpers/ProgressEmitter.cs`
- **Modify:** `src/GxMcp.Worker/Services/CommandDispatcher.cs` (extract `_meta.progressToken`, wrap dispatch in `ProgressContext.Use`)
- **Modify:** `src/GxMcp.Worker/Services/BuildService.cs` (emit progress at each phase transition)
- **Modify:** `src/GxMcp.Worker/Services/AnalyzeService.cs` (emit progress during ImpactAnalysis BFS)
- **Modify:** `src/GxMcp.Worker/Services/KbService.cs` (replace hardcoded `genexus-mcp-bulk-index` token with `ProgressContext.CurrentToken ?? "genexus-mcp-bulk-index"`)
- **Modify:** `src/GxMcp.Gateway/Program.cs` (attach `_meta.progressToken=operationId` in worker request envelope)
- **Create:** `src/GxMcp.Worker.Tests/ProgressEmitterTests.cs`
- **Modify:** `src/GxMcp.Gateway.Tests/OperationTrackerTests.cs` (assert progressToken is bound to operationId)
- **Modify:** `CHANGELOG.md`

---

### Task 1: Add `ProgressContext` and `ProgressEmitter` to the Worker

**Files:**
- Create: `src/GxMcp.Worker/Helpers/ProgressContext.cs`
- Create: `src/GxMcp.Worker/Helpers/ProgressEmitter.cs`

- [ ] **Step 1: Write the failing test**

Create `src/GxMcp.Worker.Tests/ProgressEmitterTests.cs`:

```csharp
using GxMcp.Worker.Helpers;
using System.IO;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ProgressEmitterTests
    {
        [Fact]
        public void Emit_WritesNotificationsProgressLine_ToStdout_WhenTokenIsSet()
        {
            var captured = new StringWriter();
            var originalOut = System.Console.Out;
            System.Console.SetOut(captured);

            try
            {
                using (ProgressContext.Use("op-abc"))
                {
                    ProgressEmitter.Emit(progress: 5, total: 10, message: "halfway");
                }
            }
            finally
            {
                System.Console.SetOut(originalOut);
            }

            string line = captured.ToString().Trim();
            Assert.Contains("\"method\":\"notifications/progress\"", line);
            Assert.Contains("\"progressToken\":\"op-abc\"", line);
            Assert.Contains("\"progress\":5", line);
            Assert.Contains("\"total\":10", line);
            Assert.Contains("halfway", line);
        }

        [Fact]
        public void Emit_IsNoOp_WhenNoTokenInContext()
        {
            var captured = new StringWriter();
            var originalOut = System.Console.Out;
            System.Console.SetOut(captured);

            try
            {
                ProgressEmitter.Emit(progress: 1, total: 2, message: "x");
            }
            finally
            {
                System.Console.SetOut(originalOut);
            }

            Assert.Equal(string.Empty, captured.ToString().Trim());
        }

        [Fact]
        public void Context_IsAsyncLocal_AndDisposesCleanly()
        {
            Assert.Null(ProgressContext.CurrentToken);
            using (ProgressContext.Use("op-1"))
            {
                Assert.Equal("op-1", ProgressContext.CurrentToken);
                using (ProgressContext.Use("op-2"))
                {
                    Assert.Equal("op-2", ProgressContext.CurrentToken);
                }
                Assert.Equal("op-1", ProgressContext.CurrentToken);
            }
            Assert.Null(ProgressContext.CurrentToken);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test src/GxMcp.Worker.Tests --filter "FullyQualifiedName~ProgressEmitterTests" --nologo --verbosity minimal
```

Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement `ProgressContext`**

Create `src/GxMcp.Worker/Helpers/ProgressContext.cs`:

```csharp
using System;
using System.Threading;

namespace GxMcp.Worker.Helpers
{
    public static class ProgressContext
    {
        private static readonly AsyncLocal<string?> _token = new AsyncLocal<string?>();

        public static string? CurrentToken => _token.Value;

        public static IDisposable Use(string? token)
        {
            string? previous = _token.Value;
            _token.Value = token;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly string? _previous;
            private bool _disposed;
            public Scope(string? previous) { _previous = previous; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _token.Value = _previous;
            }
        }
    }
}
```

- [ ] **Step 4: Implement `ProgressEmitter`**

Create `src/GxMcp.Worker/Helpers/ProgressEmitter.cs`:

```csharp
using System;
using Newtonsoft.Json;

namespace GxMcp.Worker.Helpers
{
    public static class ProgressEmitter
    {
        public static void Emit(int progress, int total, string? message = null)
        {
            string? token = ProgressContext.CurrentToken;
            if (string.IsNullOrWhiteSpace(token)) return;

            var envelope = new
            {
                jsonrpc = "2.0",
                method = "notifications/progress",
                @params = new
                {
                    progressToken = token,
                    progress,
                    total,
                    message = message ?? string.Empty
                }
            };

            try
            {
                Console.Out.WriteLine(JsonConvert.SerializeObject(envelope));
                Console.Out.Flush();
            }
            catch
            {
                // stdout might be closed during shutdown — silently drop.
            }
        }

        public static void Emit(string token, int progress, int total, string? message = null)
        {
            using (ProgressContext.Use(token))
            {
                Emit(progress, total, message);
            }
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```
dotnet test src/GxMcp.Worker.Tests --filter "FullyQualifiedName~ProgressEmitterTests" --nologo --verbosity minimal
```

Expected: all 3 tests PASS.

- [ ] **Step 6: Commit**

```
git add src/GxMcp.Worker/Helpers/ProgressContext.cs src/GxMcp.Worker/Helpers/ProgressEmitter.cs src/GxMcp.Worker.Tests/ProgressEmitterTests.cs
git commit -m "feat(worker): add AsyncLocal ProgressContext + ProgressEmitter helper"
```

---

### Task 2: Wire `_meta.progressToken` extraction in the dispatcher

**Files:**
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`

- [ ] **Step 1: Read the dispatch entry point**

```
grep -n "public.*Dispatch\|JObject request" src/GxMcp.Worker/Services/CommandDispatcher.cs | head -10
```

Identify the public `Dispatch(string line)` (or `Dispatch(JObject request)`) entry. The `request` object carries the full JSON-RPC envelope.

- [ ] **Step 2: Extract and bind the token at the top of `Dispatch`**

At the very top of `Dispatch`, after `request` is parsed and before any `switch`, add:

```csharp
            string? progressToken = request?["_meta"]?["progressToken"]?.ToString();
            using (GxMcp.Worker.Helpers.ProgressContext.Use(progressToken))
            {
                // ... existing dispatch body
                return result;
            }
```

Wrap the **entire** existing dispatch body inside the `using`. Make sure every `return` inside it returns from the wrapped block (no early returns above the `using`).

- [ ] **Step 3: Build**

```
dotnet build src/GxMcp.Worker --configuration Debug --nologo --verbosity minimal
```

Expected: build green.

- [ ] **Step 4: Commit**

```
git add src/GxMcp.Worker/Services/CommandDispatcher.cs
git commit -m "feat(worker): bind _meta.progressToken to ProgressContext for the call duration"
```

---

### Task 3: Gateway — attach `_meta.progressToken` to worker RPC requests

**Files:**
- Modify: `src/GxMcp.Gateway/Program.cs` (around `BuildWorkerRpcRequest`, line 957-980 area)

- [ ] **Step 1: Locate `SendWorkerCommandAsync` and the operationId assignment**

```
grep -n "SendWorkerCommandAsync\|StartOperation\|BuildWorkerRpcRequest" src/GxMcp.Gateway/Program.cs | head -20
```

Identify where `_operationTracker.StartOperation(...)` is called and where the resulting `operationId` is held. The same scope builds the worker JSON-RPC body via `BuildWorkerRpcRequest`.

- [ ] **Step 2: Write the failing test**

Append to `src/GxMcp.Gateway.Tests/OperationTrackerTests.cs`:

```csharp
[Fact]
public void BuildWorkerRpcRequest_IncludesMetaProgressToken_WhenOperationIdPresent()
{
    var workerCommand = JObject.Parse(@"{
        ""module"": ""Build"",
        ""action"": ""Build"",
        ""target"": ""InvoiceProc""
    }");

    var method = typeof(GxMcp.Gateway.Program).GetMethod(
        "BuildWorkerRpcRequest",
        BindingFlags.NonPublic | BindingFlags.Static);

    Assert.NotNull(method);

    // Most likely signature today: BuildWorkerRpcRequest(JObject workerCommand, string requestId)
    // — sub-plan extends this to: BuildWorkerRpcRequest(JObject workerCommand, string requestId, string? operationId = null)
    var built = (JObject)method!.Invoke(null, new object?[] { workerCommand, "req-1", "op-xyz" })!;

    Assert.Equal("op-xyz", built["_meta"]?["progressToken"]?.ToString());
    Assert.Equal("Build", built["method"]?.ToString());
}
```

- [ ] **Step 3: Run test to verify it fails**

```
dotnet test src/GxMcp.Gateway.Tests --filter "FullyQualifiedName~BuildWorkerRpcRequest_IncludesMetaProgressToken" --nologo --verbosity minimal
```

Expected: FAIL — current signature does not accept an `operationId` parameter.

- [ ] **Step 4: Extend `BuildWorkerRpcRequest`**

In `src/GxMcp.Gateway/Program.cs` at line 957, replace the existing method with:

```csharp
        private static JObject BuildWorkerRpcRequest(JObject workerCommand, string requestId, string? operationId = null)
        {
            var rpc = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = workerCommand["module"]?.ToString() ?? string.Empty,
                ["action"] = workerCommand["action"]?.DeepClone(),
                // ... preserve all existing fields below this point
            };

            if (!string.IsNullOrWhiteSpace(operationId))
            {
                rpc["_meta"] = new JObject
                {
                    ["progressToken"] = operationId
                };
            }

            return rpc;
        }
```

Make sure the `// preserve all existing fields below this point` line is replaced with the actual remaining fields the original method copies (`target`, `args`, etc.). Read lines 957-1000 first to enumerate them.

- [ ] **Step 5: Pass `operationId` from the call site**

Locate `SendWorkerCommandAsync`. Find the line that calls `BuildWorkerRpcRequest(workerCommand, requestId)` and change it to:

```csharp
                JObject rpcRequest = BuildWorkerRpcRequest(workerCommand, requestId, operationId);
```

where `operationId` is the variable already bound by `StartOperation` in the same scope. (If the variable name differs — e.g., `opId`, `tracked.OperationId` — match the actual local.)

- [ ] **Step 6: Run tests**

```
dotnet test src/GxMcp.Gateway.Tests --filter "FullyQualifiedName~BuildWorkerRpcRequest" --nologo --verbosity minimal
dotnet test src/GxMcp.Gateway.Tests --nologo --verbosity minimal
```

Expected: all PASS.

- [ ] **Step 7: Commit**

```
git add src/GxMcp.Gateway/Program.cs src/GxMcp.Gateway.Tests/OperationTrackerTests.cs
git commit -m "feat(gateway): attach _meta.progressToken=operationId on worker RPC requests"
```

---

### Task 4: Emit progress from `BuildService`

**Files:**
- Modify: `src/GxMcp.Worker/Services/BuildService.cs`

- [ ] **Step 1: Locate phase transitions in `RunBuild`**

```
grep -n "Phase\s*=\s*\"" src/GxMcp.Worker/Services/BuildService.cs | head -20
```

Each `status.Phase = "..."` assignment is a natural emission point. Typical phases: `Starting`, `Compiling`, `Linking`, `Completed`.

- [ ] **Step 2: Add a helper for phase progress**

Inside `BuildService`, add:

```csharp
        private static readonly System.Collections.Generic.Dictionary<string, int> _phaseProgressMap =
            new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Starting"]  = 5,
            ["Specifying"] = 15,
            ["Compiling"]  = 50,
            ["Linking"]    = 85,
            ["Completed"]  = 100
        };

        private static void EmitPhaseProgress(string phase, int total = 100)
        {
            if (_phaseProgressMap.TryGetValue(phase, out int p))
            {
                GxMcp.Worker.Helpers.ProgressEmitter.Emit(p, total, $"Build phase: {phase}");
            }
        }
```

- [ ] **Step 3: Call `EmitPhaseProgress` at each phase transition**

After every `status.Phase = "<phase>";` line in `BuildService.cs`, add immediately below:

```csharp
            EmitPhaseProgress(status.Phase);
```

- [ ] **Step 4: Add an integration test that captures emitted lines**

Append to `src/GxMcp.Worker.Tests/ProgressEmitterTests.cs`:

```csharp
[Fact]
public void Emit_FromMultiplePhases_ProducesMonotonicProgress()
{
    var captured = new StringWriter();
    var originalOut = System.Console.Out;
    System.Console.SetOut(captured);

    try
    {
        using (GxMcp.Worker.Helpers.ProgressContext.Use("op-build"))
        {
            GxMcp.Worker.Helpers.ProgressEmitter.Emit(5, 100, "Build phase: Starting");
            GxMcp.Worker.Helpers.ProgressEmitter.Emit(50, 100, "Build phase: Compiling");
            GxMcp.Worker.Helpers.ProgressEmitter.Emit(100, 100, "Build phase: Completed");
        }
    }
    finally
    {
        System.Console.SetOut(originalOut);
    }

    var lines = captured.ToString().Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(3, lines.Length);
    Assert.Contains("\"progress\":5",   lines[0]);
    Assert.Contains("\"progress\":50",  lines[1]);
    Assert.Contains("\"progress\":100", lines[2]);
}
```

- [ ] **Step 5: Run tests**

```
dotnet test src/GxMcp.Worker.Tests --filter "FullyQualifiedName~ProgressEmitterTests" --nologo --verbosity minimal
dotnet build src/GxMcp.Worker --configuration Debug --nologo --verbosity minimal
```

Expected: PASS + build green.

- [ ] **Step 6: Commit**

```
git add src/GxMcp.Worker/Services/BuildService.cs src/GxMcp.Worker.Tests/ProgressEmitterTests.cs
git commit -m "feat(build): emit notifications/progress at each phase transition"
```

---

### Task 5: Emit progress from `AnalyzeService.ImpactAnalysis`

**Files:**
- Modify: `src/GxMcp.Worker/Services/AnalyzeService.cs`

- [ ] **Step 1: Locate the BFS loop in `ImpactAnalysis`**

```
grep -n "ImpactAnalysis\|queue\.Enqueue\|while\s*\(queue\." src/GxMcp.Worker/Services/AnalyzeService.cs | head -10
```

Identify the breadth-first walk that builds the `callers` set (between `src/GxMcp.Worker/Services/AnalyzeService.cs:117-260` per recon).

- [ ] **Step 2: Emit at progress checkpoints**

Inside the BFS loop, every N nodes (or every depth tick), emit:

```csharp
                if (visited.Count > 0 && visited.Count % 25 == 0)
                {
                    GxMcp.Worker.Helpers.ProgressEmitter.Emit(
                        progress: System.Math.Min(95, visited.Count),
                        total: System.Math.Max(100, visited.Count + queue.Count),
                        message: $"Impact analysis: {visited.Count} nodes visited, {queue.Count} pending");
                }
```

When the walk completes, emit a final `100/100`:

```csharp
            GxMcp.Worker.Helpers.ProgressEmitter.Emit(100, 100, "Impact analysis: complete");
```

Place the final emission immediately before the return statement at the end of `ImpactAnalysis`.

- [ ] **Step 3: Build and run worker tests**

```
dotnet build src/GxMcp.Worker --configuration Debug --nologo --verbosity minimal
dotnet test src/GxMcp.Worker.Tests --nologo --verbosity minimal
```

Expected: build green; tests pass (existing impact-analysis tests should still pass; emission is fire-and-forget).

- [ ] **Step 4: Commit**

```
git add src/GxMcp.Worker/Services/AnalyzeService.cs
git commit -m "feat(analyze): emit notifications/progress during ImpactAnalysis BFS"
```

---

### Task 6: Switch `KbService.BulkIndex` from hardcoded token to context token

**Files:**
- Modify: `src/GxMcp.Worker/Services/KbService.cs`

- [ ] **Step 1: Locate the existing emission**

```
grep -n "genexus-mcp-bulk-index\|progressToken" src/GxMcp.Worker/Services/KbService.cs
```

The current emission uses a hardcoded token string.

- [ ] **Step 2: Replace with context-aware emission**

Replace the existing progress emission block with:

```csharp
                string tokenForProgress = GxMcp.Worker.Helpers.ProgressContext.CurrentToken ?? "genexus-mcp-bulk-index";
                GxMcp.Worker.Helpers.ProgressEmitter.Emit(
                    token: tokenForProgress,
                    progress: processedCount,
                    total: totalCount,
                    message: $"Indexing KB: {processedCount}/{totalCount} objects");
```

The fallback to `"genexus-mcp-bulk-index"` preserves backwards compatibility for the case where `BulkIndex` is triggered outside of a tool call (e.g., from a background indexer with no operation context).

- [ ] **Step 3: Build**

```
dotnet build src/GxMcp.Worker --configuration Debug --nologo --verbosity minimal
```

Expected: build green.

- [ ] **Step 4: Commit**

```
git add src/GxMcp.Worker/Services/KbService.cs
git commit -m "refactor(index): use ProgressContext token in BulkIndex, fallback to legacy id"
```

---

### Task 7: End-to-end stdio capture test + CHANGELOG

**Files:**
- Modify: `CHANGELOG.md`
- Create: `tests/manual/progress-capture.md` (documentation only)

- [ ] **Step 1: Document the manual capture procedure**

Create `tests/manual/progress-capture.md`:

```markdown
# Manual: capturing notifications/progress

This walkthrough proves end-to-end that `genexus_edit_and_build` and
`genexus_lifecycle action=build` emit `notifications/progress` bound to an
`operationId` and visible in the stdio MCP stream.

## Steps

1. Start the MCP launcher in stdio mode against a real KB:
   `genexus-mcp` (with `GX_CONFIG_PATH` pointing at a config that has `Server.McpStdio=true`)
2. Tee its stdout to a capture file: redirect through `pwsh` or `tee`.
3. Issue a `tools/call` for `genexus_lifecycle action=build target=<RealObject>`.
4. Within the next ~5s, observe stdout lines like:
   `{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"<operationId>","progress":5,"total":100,"message":"Build phase: Starting"}}`
5. The `progressToken` value must match the `operationId` returned from the original tool call.

Save the captured stream as `tests/manual/progress-capture.log` for the audit
trail of this sub-plan.
```

- [ ] **Step 2: Run all tests**

```
dotnet test src/GxMcp.Gateway.Tests --nologo --verbosity minimal
dotnet test src/GxMcp.Worker.Tests --nologo --verbosity minimal
```

Expected: all PASS.

- [ ] **Step 3: MCP smoke probe**

```
pwsh -NoProfile -File scripts/mcp_smoke.ps1 -BaseUrl http://127.0.0.1:5000/mcp
```

Expected: exit code 0. (Smoke only checks the protocol shape; progress will not appear here unless a real long op is triggered.)

- [ ] **Step 4: Live capture (manual)**

Follow `tests/manual/progress-capture.md` against a real KB. Save the captured output to `tests/manual/progress-capture.log`. Confirm at least 3 progress lines with the same `progressToken` matching the original `operationId`.

- [ ] **Step 5: Add CHANGELOG entry**

```markdown
- **Streaming**: long-running operations now emit `notifications/progress` bound to their
  `operationId`. Build, impact analysis, and KB index report incremental progress so the LLM
  can read status without polling `genexus_lifecycle action=status`. The gateway already
  forwards `notifications/progress` to both stdio and HTTP transports.
```

- [ ] **Step 6: Commit**

```
git add CHANGELOG.md tests/manual/progress-capture.md tests/manual/progress-capture.log
git commit -m "docs: notifications/progress streaming + manual capture artifact"
```

---

## Done criteria

- [ ] All tasks above completed
- [ ] Gateway and Worker test suites green
- [ ] `tests/manual/progress-capture.log` exists and contains ≥3 progress lines with matching `progressToken=<operationId>`
- [ ] `BuildWorkerRpcRequest_IncludesMetaProgressToken` test green
- [ ] Sub-plan checkpoint signed off
