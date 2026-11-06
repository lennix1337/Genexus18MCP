using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public sealed class ClientCancellationIdempotencyTests
{
    [Fact]
    public async Task CancelAfterDispatchKeepsMutationFenceThroughLateWorkerReply()
    {
        string root = Path.Combine(Path.GetTempPath(), "gxmcp-cancel-idem-" + Guid.NewGuid().ToString("N"));
        string workerId = Guid.NewGuid().ToString("N");
        string session = "client-" + workerId;
        const string kb = @"C:\Synthetic\Orders";
        var args = new JObject { ["name"] = "Example", ["part"] = "Source", ["content"] = "return", ["idempotencyKey"] = "same-key" };
        string operation = Program.OperationTracker.StartOperation(workerId, "genexus_edit", args, workerId);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerReply = AddPending(workerId, session, operation);
        var cache = new IdempotencyCache(15, 32, TimeSpan.FromMilliseconds(20), Path.Combine(root, "operations.json"));
        var middleware = new IdempotencyMiddleware(cache, kb);
        var call = new JObject { ["name"] = "genexus_edit", ["arguments"] = args };
        int executions = 0;
        try
        {
            var first = middleware.Invoke(call, async _ =>
            {
                executions++;
                dispatched.SetResult();
                var reply = JObject.Parse(await workerReply);
                // Exercise the same response projection used by tool dispatch before the idempotency middleware.
                var shaped = Program.ShapeMutationResponse(reply["error"] ?? reply["result"], "genexus_edit", args, new JArray());
                if (reply["error"] != null) shaped = McpRouter.TrimErrorEnvelope((JObject)shaped, verbose: false);
                return new JObject
                {
                    ["isError"] = reply["error"] != null,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = shaped.ToString() })
                };
            });
            await dispatched.Task;
            // A different client with the same request id cannot cancel this write.
            await Cancel(session + "-other");
            Assert.False(workerReply.IsCompleted);
            await Cancel(session);
            var cancelled = await first.WaitAsync(TimeSpan.FromSeconds(5));
            var payload = JObject.Parse(cancelled["content"]![0]!["text"]!.ToString());
            Assert.True(payload["mutationOutcomeUnknown"]?.Value<bool>());
            Assert.Equal(operation, payload["operationId"]?.ToString());
            Assert.False(payload["retryable"]!.Value<bool>());
            Assert.True(payload["reconciliationRequired"]!.Value<bool>());
            Assert.Equal("Indeterminate", payload["commitState"]?.ToString());
            Assert.False(payload["persistenceStateKnown"]!.Value<bool>());
            Assert.Equal(JTokenType.Null, payload["persisted"]!.Type);
            Assert.Equal("started", cache.InspectOperation(kb, "genexus_edit", "same-key")["status"]?.ToString());
            var refused = await Assert.ThrowsAsync<UsageException>(() => middleware.Invoke(call, _ =>
            {
                executions++;
                return Task.FromResult(new JObject());
            }));
            Assert.Equal("operation_unknown", refused.Code);
            Assert.Equal(1, executions);
            Assert.Equal("CancellationRequested", Program.OperationTracker.BuildOperationStatus(operation)["status"]?.ToString());
            Program.RouteWorkerReplyForTest(new JObject { ["id"] = workerId, ["result"] = new JObject { ["status"] = "ok" } }.ToString());
            Assert.Equal("Completed", Program.OperationTracker.BuildOperationStatus(operation)["status"]?.ToString());
            // A late successful SDK reply is evidence, not permission to replay the same key.
            await Assert.ThrowsAsync<UsageException>(() => middleware.Invoke(call, _ =>
            {
                executions++;
                return Task.FromResult(new JObject());
            }));
            Assert.Equal(1, executions);
        }
        finally
        {
            Pending.Remove(workerId);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("genexus_read", false)]
    [InlineData("genexus_edit", true)]
    public async Task ReadAndPreviewCancellationDoNotClaimAnUnknownMutation(string tool, bool dryRun)
    {
        string id = Guid.NewGuid().ToString("N");
        string operation = Program.OperationTracker.StartOperation(id, tool, new JObject { ["dryRun"] = dryRun }, id);
        var reply = AddPending(id, id, operation);
        try
        {
            await Cancel(id);
            var error = JObject.Parse(await reply.WaitAsync(TimeSpan.FromSeconds(5)))["error"]!;
            Assert.Equal(-32800, error["code"]!.Value<int>());
            Assert.Null(error["mutationOutcomeUnknown"]);
        }
        finally { Pending.Remove(id); }
    }

    [Theory]
    [InlineData("genexus_edit", "{dryRun:true,changeSet:{action:'apply'}}")]
    [InlineData("genexus_unknown", "{dryRun:true}")]
    public async Task UnprovenPreviewStillReportsUnknownMutation(string tool, string json)
    {
        string id = Guid.NewGuid().ToString("N");
        string operation = Program.OperationTracker.StartOperation(id, tool, JObject.Parse(json), id);
        var reply = AddPending(id, id, operation);
        try
        {
            await Cancel(id);
            var error = JObject.Parse(await reply.WaitAsync(TimeSpan.FromSeconds(5)))["error"]!;
            Assert.True(error["mutationOutcomeUnknown"]?.Value<bool>());
            Assert.True(error["reconciliationRequired"]?.Value<bool>());
        }
        finally { Pending.Remove(id); }
    }

    [Theory]
    [InlineData("genexus_read", false)]
    [InlineData("genexus_edit", true)]
    [InlineData("", true)]
    public async Task MissingTrackerContextUsesConservativePendingToolClassification(string tool, bool mutationUnknown)
    {
        string id = Guid.NewGuid().ToString("N");
        var reply = AddPending(id, id, null, tool);
        try
        {
            await Cancel(id);
            var error = JObject.Parse(await reply.WaitAsync(TimeSpan.FromSeconds(5)))["error"]!;
            Assert.Equal(mutationUnknown, error["mutationOutcomeUnknown"]?.Value<bool>() ?? false);
            Assert.Null(error["operationId"]);
        }
        finally { Pending.Remove(id); }
    }

    private static Task Cancel(string session) => Program.ProcessMcpRequest(new JObject
    {
        ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JObject { ["requestId"] = 7 }
    }, session);

    private static IDictionary Pending => (IDictionary)typeof(Program)
        .GetField("_pendingRequests", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static Task<string> AddPending(string id, string session, string? operation, string tool = "genexus_edit")
    {
        Type type = typeof(Program).GetNestedType("PendingWorkerRequest", BindingFlags.NonPublic)!;
        object pending = Activator.CreateInstance(type, nonPublic: true)!;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        type.GetProperty("CompletionSource")!.SetValue(pending, completion);
        type.GetProperty("OperationId")!.SetValue(pending, operation);
        type.GetProperty("ToolName")!.SetValue(pending, tool);
        type.GetProperty("McpSessionId")!.SetValue(pending, session);
        type.GetProperty("McpRequestIdToken")!.SetValue(pending, new JValue(7));
        Pending.Add(id, pending);
        return completion.Task;
    }
}
