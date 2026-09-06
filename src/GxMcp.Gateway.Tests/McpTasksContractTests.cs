using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class McpTasksContractTests
    {
        [Fact]
        public void GetAndCancelAreSessionScoped()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            var get = McpTasksProtocol.Handle(Request("tasks/get", "1", job.Id), "client-a", registry);
            Assert.Equal(job.Id, get!["result"]!["taskId"]!.ToString());

            var forbidden = McpTasksProtocol.Handle(Request("tasks/get", 2, job.Id), "client-b", registry);
            Assert.Equal(-32003, forbidden!["error"]!["code"]!.ToObject<int>());
            Assert.Equal("running", registry.Get(job.Id)!.Status);

            var cancel = McpTasksProtocol.Handle(Request("tasks/cancel", 3, job.Id), "client-a", registry);
            Assert.Equal("cancelled", cancel!["result"]!["status"]!.ToString());
        }

        [Fact]
        public void ModernTasksRequireExtensionAndUseTaskStatuses()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            var request = ModernRequest("tasks/get", 1, job.Id, declaresTasks: false);
            var missing = McpTasksProtocol.Handle(request, "client-a", registry);
            Assert.Equal(-32021, missing!["error"]!["code"]!.ToObject<int>());

            request = ModernRequest("tasks/get", 2, job.Id, declaresTasks: true);
            var get = McpTasksProtocol.Handle(request, "client-a", registry);
            Assert.Equal("complete", get!["result"]!["resultType"]!.ToString());
            Assert.Equal("working", get!["result"]!["status"]!.ToString());
            Assert.Equal(1000, get["result"]!["pollIntervalMs"]!.ToObject<int>());
        }

        [Fact]
        public void CreateTaskResultIsDurablyReadableAndUsesExtensionDiscriminator()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            var created = McpTasksProtocol.BuildCreateTaskResult(job, "Edit accepted");

            Assert.Equal("task", created["resultType"]!.ToString());
            Assert.Equal(job.Id, created["taskId"]!.ToString());
            Assert.Equal("working", created["status"]!.ToString());
            Assert.NotNull(created["lastUpdatedAt"]);

            var fetched = McpTasksProtocol.Handle(
                ModernRequest("tasks/get", 2, created["taskId"]!.ToString(), declaresTasks: true),
                "client-a",
                registry);
            Assert.Equal("complete", fetched!["result"]!["resultType"]!.ToString());
            Assert.Equal("working", fetched["result"]!["status"]!.ToString());
        }

        [Fact]
        public void TaskCreationRequiresModernProtocolAndPerRequestCapability()
        {
            var modern = ModernRequest("tools/call", 1, "unused", declaresTasks: true);
            Assert.True(McpTasksProtocol.SupportsTasks(modern));

            var noCapability = ModernRequest("tools/call", 2, "unused", declaresTasks: false);
            Assert.False(McpTasksProtocol.SupportsTasks(noCapability));

            var legacy = Request("tools/call", 3, "unused");
            Assert.False(McpTasksProtocol.SupportsTasks(legacy));
        }

        [Fact]
        public void ModernUpdateRequiresObjectInputResponsesAndReturnsAck()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            var invalid = ModernRequest("tasks/update", 1, job.Id, declaresTasks: true);
            invalid["params"]!["inputResponses"] = new JArray();
            var invalidResponse = McpTasksProtocol.Handle(invalid, "client-a", registry);
            Assert.Equal(-32602, invalidResponse!["error"]!["code"]!.ToObject<int>());

            var valid = ModernRequest("tasks/update", 2, job.Id, declaresTasks: true);
            valid["params"]!["inputResponses"] = new JObject();
            var ack = McpTasksProtocol.Handle(valid, "client-a", registry);
            Assert.Equal("complete", ack!["result"]!["resultType"]!.ToString());
            Assert.Single(((JObject)ack["result"]!).Properties());
        }

        [Fact]
        public void TerminalTaskCannotBeCancelled()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            registry.Complete(job.Id, success: true, summary: "done");
            var result = McpTasksProtocol.Handle(Request("tasks/cancel", 1, job.Id), "client-a", registry);
            Assert.Equal(-32602, result!["error"]!["code"]!.ToObject<int>());
        }

        [Fact]
        public void ModernForeignTaskHandleFailsClosedLikeAnUnknownTask()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            var response = McpTasksProtocol.Handle(
                ModernRequest("tasks/get", 1, job.Id, declaresTasks: true),
                "client-b",
                registry);

            Assert.Equal(-32602, response!["error"]!["code"]!.ToObject<int>());
            Assert.Equal("Invalid taskId.", response["error"]!["message"]!.ToString());
        }

        [Fact]
        public void ModernTasksFailClosedWhenTheTransportHasNoClientScope()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("http-modern:owner-a", "edit/genexus_edit", 30);
            var response = McpTasksProtocol.Handle(
                ModernRequest("tasks/get", 1, job.Id, declaresTasks: true),
                "http-modern-unscoped:request",
                registry,
                taskScopeEnabled: false);

            Assert.Equal(-32023, response!["error"]!["code"]!.ToObject<int>());
            Assert.Equal("Mcp-Client-Id", response["error"]!["data"]!["requiredHeader"]!.ToString());
        }

        [Fact]
        public void UnknownMethodAndUnknownTaskReturnNullAndStructuredError()
        {
            var registry = new BackgroundJobRegistry();
            Assert.Null(McpTasksProtocol.Handle(new JObject { ["method"] = "tools/list" }, "client", registry));
            var missing = McpTasksProtocol.Handle(Request("tasks/get", 1, "missing"), "client", registry);
            Assert.Equal(-32001, missing!["error"]!["code"]!.ToObject<int>());
        }

        private static JObject Request(string method, object id, string taskId)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = JToken.FromObject(id), ["method"] = method,
                ["params"] = new JObject { ["taskId"] = taskId } };

        private static JObject ModernRequest(string method, object id, string taskId, bool declaresTasks)
            => new JObject {
                ["jsonrpc"] = "2.0", ["id"] = JToken.FromObject(id), ["method"] = method,
                ["params"] = new JObject {
                    ["taskId"] = taskId,
                    ["_meta"] = new JObject {
                        ["io.modelcontextprotocol/protocolVersion"] = McpRouter.ModernProtocolVersion,
                        ["io.modelcontextprotocol/clientCapabilities"] = new JObject {
                            ["extensions"] = declaresTasks
                                ? new JObject { ["io.modelcontextprotocol/tasks"] = new JObject() }
                                : new JObject()
                        }
                    }
                }
            };
    }
}
