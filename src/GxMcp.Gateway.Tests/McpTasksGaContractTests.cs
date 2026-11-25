using Newtonsoft.Json.Linq;
using Xunit;
using static GxMcp.Gateway.Tests.McpTasksContractTests;

namespace GxMcp.Gateway.Tests
{
    public class McpTasksGaContractTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void GaPollReturnsCallToolResultForSuccessAndToolErrors(bool success)
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            var payload = new JObject { ["success"] = success, ["detail"] = "worker payload" };
            registry.Complete(job.Id, success, "Finished", payload);

            var legacy = McpTasksProtocol.Handle(Request("tasks/get", 1, job.Id), "client-a", registry);
            Assert.Equal(success ? "succeeded" : "failed", (string?)legacy?["result"]?["status"]);
            Assert.True(JToken.DeepEquals(payload, legacy?["result"]?["result"]));

            var response = McpTasksProtocol.Handle(
                ModernRequest("tasks/get", 2, job.Id, declaresTasks: true), "client-a", registry);
            var task = Assert.IsType<JObject>(response?["result"]);
            Assert.Equal("completed", (string?)task["status"]);
            var result = Assert.IsType<JObject>(task["result"]);
            Assert.Equal(!success, (bool?)result["isError"]);
            Assert.Equal("text", (string?)result["content"]?[0]?["type"]);
            Assert.Null(task["error"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GaExecutionFailureHasJsonRpcError(bool stalled)
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            if (stalled) registry.Stall(job.Id, "Dispatch stalled");
            else registry.Complete(job.Id, success: false, summary: "Dispatch failed");

            var response = McpTasksProtocol.Handle(
                ModernRequest("tasks/get", 1, job.Id, declaresTasks: true), "client-a", registry);
            var task = Assert.IsType<JObject>(response?["result"]);
            Assert.Equal("failed", (string?)task["status"]);
            Assert.Equal(-32603, (int?)task["error"]?["code"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)task["error"]?["message"]));
            Assert.Null(task["result"]);
        }

        [Fact]
        public void GaMissingCapabilityUsesStandardErrorData()
        {
            var registry = new BackgroundJobRegistry();
            var response = McpTasksProtocol.Handle(
                ModernRequest("tasks/get", 1, "unused", declaresTasks: false), "client-a", registry);
            Assert.Equal(-32021, (int?)response?["error"]?["code"]);
            var required = response?["error"]?["data"]?["requiredCapabilities"];
            Assert.Empty(Assert.IsType<JObject>(required?["extensions"]?["io.modelcontextprotocol/tasks"]).Properties());
        }

        [Fact]
        public void GaQueuedJobIsWorkingAndCanBeCancelledWithoutChangingLegacyContract()
        {
            const string session = "client-a";
            var registry = new BackgroundJobRegistry();
            var ownership = new OwnershipFence(session, "kb", 1);
            registry.AdmitLifecycle(session, "lifecycle/build", 30, ownership, "kb", "build");
            var job = registry.AdmitLifecycle(session, "lifecycle/rebuild", 30, ownership, "kb", "rebuild").Job;

            var legacy = McpTasksProtocol.Handle(Request("tasks/cancel", 1, job.Id), session, registry, ownership: ownership);
            Assert.Equal(-32602, (int?)legacy?["error"]?["code"]);
            Assert.Equal("queued", job.Status);

            var get = ModernRequest("tasks/get", 2, job.Id, declaresTasks: true);
            var task = McpTasksProtocol.Handle(get, session, registry, ownership: ownership)?["result"];
            Assert.Equal("working", (string?)task?["status"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)task?["statusMessage"]));
            Assert.Null(task?["ownerScopeId"]);
            Assert.Null(task?["result"]);

            var cancel = ModernRequest("tasks/cancel", 3, job.Id, declaresTasks: true);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var ack = McpTasksProtocol.Handle(cancel, session, registry, ownership: ownership);
                var result = Assert.IsType<JObject>(ack?["result"]);
                Assert.Single(result.Properties());
                Assert.Equal("complete", (string?)result["resultType"]);
            }
            Assert.Equal("cancelled", (string?)McpTasksProtocol.Handle(get, session, registry, ownership: ownership)?["result"]?["status"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GaRequiresStringTaskIdWhileLegacyKeepsIdAlias(bool numeric)
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("client-a", "edit/genexus_edit", 30);
            var request = ModernRequest("tasks/get", 1, job.Id, declaresTasks: true);
            var parameters = (JObject)request["params"]!;
            parameters.Remove("taskId");
            if (numeric) parameters["taskId"] = 123;
            parameters["id"] = job.Id;
            var modern = McpTasksProtocol.Handle(request, "client-a", registry);
            Assert.Equal(-32602, (int?)modern?["error"]?["code"]);
            Assert.Equal("taskId is required.", (string?)modern?["error"]?["message"]);

            parameters.Remove("taskId");
            parameters.Remove("_meta");
            var legacy = McpTasksProtocol.Handle(request, "client-a", registry);
            Assert.Equal(job.Id, (string?)legacy?["result"]?["taskId"]);
        }
    }
}
