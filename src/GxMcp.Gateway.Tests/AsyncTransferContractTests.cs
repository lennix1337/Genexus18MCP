using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A <c>genexus_transfer</c> export of a large Design System (with <c>includeDependencies</c>) can run for minutes on
    /// the Worker's one SDK thread. Called synchronously, it left the caller with no handle once its own window had
    /// expired, while every other call answered <c>WorkerBusy</c>. <c>async=true</c> gives export and inspect the same
    /// accept-then-poll contract the version-store reads have; import, a real KB mutation, keeps the synchronous path.
    /// </summary>
    public class AsyncTransferContractTests
    {
        [Theory]
        [InlineData("export")]
        [InlineData("inspect")]
        [InlineData("Export")]
        [InlineData("  inspect  ")]
        public void IsAsyncLongReadAction_AcceptsTransferExportAndInspect(string action)
        {
            Assert.True(Program.IsAsyncLongReadAction("genexus_transfer", new JObject { ["action"] = action, ["async"] = true }));
            Assert.True(Program.IsAsyncLongReadAction("Genexus_Transfer", new JObject { ["action"] = action, ["async"] = true }));
        }

        [Theory]
        [InlineData("import")]
        [InlineData("")]
        [InlineData("deploy")]
        public void IsAsyncLongReadAction_RefusesTransferImportAndUnknownActions(string action)
        {
            Assert.False(Program.IsAsyncLongReadAction("genexus_transfer", new JObject { ["action"] = action, ["async"] = true }));
        }

        [Fact]
        public void IsAsyncLongReadAction_TransferRequiresTheExplicitOptIn()
        {
            Assert.False(Program.IsAsyncLongReadAction("genexus_transfer", new JObject { ["action"] = "export" }));
            Assert.False(Program.IsAsyncLongReadAction("genexus_transfer", new JObject { ["action"] = "export", ["async"] = false }));
            Assert.False(Program.IsAsyncLongReadAction("genexus_transfer", null));
        }

        [Fact]
        public void IsAsyncLongReadAction_TheTransferActionsDoNotLeakToOtherTools()
        {
            foreach (string tool in new[] { "genexus_versioning", "genexus_io", "genexus_deploy", "genexus_query" })
                Assert.False(Program.IsAsyncLongReadAction(tool, new JObject { ["action"] = "export", ["async"] = true }), tool);
        }

        [Fact]
        public void IsAsyncTransferAction_IsTheTransferSubset()
        {
            Assert.True(Program.IsAsyncTransferAction("genexus_transfer", new JObject { ["action"] = "export" }));
            Assert.False(Program.IsAsyncTransferAction("genexus_transfer", new JObject { ["action"] = "import" }));
            Assert.False(Program.IsAsyncTransferAction("genexus_versioning", new JObject { ["action"] = "history_get" }));
            Assert.False(Program.IsAsyncTransferAction("genexus_transfer", null));
        }

        [Fact]
        public void TheJobIsLabelledAndWordedAsATransfer_NotAsAVersionStoreRead()
        {
            Assert.Equal("transfer/export", Program.AsyncReadJobLabel("genexus_transfer", "export"));
            Assert.Equal("versioning/history_get", Program.AsyncReadJobLabel("genexus_versioning", "history_get"));
            Assert.Equal("transfer", Program.AsyncReadKind("genexus_transfer"));
            Assert.Equal("read", Program.AsyncReadKind("genexus_versioning"));
            Assert.Equal("Transfer", Program.AsyncReadKindTitle("transfer"));
            Assert.Equal("Read", Program.AsyncReadKindTitle("read"));
        }

        [Fact]
        public void BuildAsyncReadAcceptedPayload_ForATransferSaysTransfer()
        {
            var job = new JobEntry { Id = "0123456789abcdef0123456789abcdef", EstimatedSeconds = 60 };

            JObject payload = Program.BuildAsyncReadAcceptedPayload(job, "export", "transfer");

            Assert.Equal(job.Id, payload["operationId"]?.ToString());
            Assert.Equal("op:" + job.Id, payload["pollTarget"]?.ToString());
            Assert.Equal("export", payload["action"]?.ToString());
            string hint = payload["hint"]?.ToString() ?? string.Empty;
            Assert.StartsWith("Transfer accepted;", hint);
            Assert.Contains("The transfer keeps running if this call's window expires", hint);
            Assert.Contains("genexus_lifecycle(action='result', target='op:" + job.Id + "')", hint);
        }

        [Fact]
        public void BuildAsyncReadAcceptedPayload_ForAVersionStoreReadKeepsItsOwnWords()
        {
            var job = new JobEntry { Id = "0123456789abcdef0123456789abcdef", EstimatedSeconds = 60 };

            string hint = Program.BuildAsyncReadAcceptedPayload(job, "history_get")["hint"]?.ToString() ?? string.Empty;

            Assert.StartsWith("Read accepted;", hint);
            Assert.Contains("The read keeps running if this call's window expires", hint);
        }

        [Fact]
        public void TheAsyncReadDispatch_LabelsAndWordsTheJobByItsTool()
        {
            // The dispatch needs a live Worker, so pin the wiring at source level.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Gateway", "Program.ToolDispatch.cs");

            Assert.Contains("JobRegistry.Start(sessionId, AsyncReadJobLabel(tName, action), estimate", source, StringComparison.Ordinal);
            Assert.Contains("string asyncKindTitle = AsyncReadKindTitle(asyncKind);", source, StringComparison.Ordinal);
            Assert.Contains("BuildAsyncReadAcceptedPayload(readJob, action, asyncKind)", source, StringComparison.Ordinal);
            Assert.Contains("(ok ? asyncKindTitle + \" succeeded: \" : asyncKindTitle + \" failed: \") + action", source, StringComparison.Ordinal);
        }

        [Fact]
        public void TransferSchema_DeclaresAsyncAsABoolean()
        {
            var tools = JArray.Parse(GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
            JObject transfer = tools.OfType<JObject>().Single(t => (string?)t["name"] == "genexus_transfer");

            JToken? property = transfer["inputSchema"]?["properties"]?["async"];

            Assert.NotNull(property);
            Assert.Equal("boolean", (string?)property!["type"]);
        }

        // The gateway's argument guard refuses an undeclared key that looks like a typo of a declared one, so an
        // `async` the schema does not declare would be a latent rejection.
        [Fact]
        public void TransferSchema_DeclaresTheAsyncArgument()
        {
            var result = GatewayArgsValidator.Validate("genexus_transfer", new JObject
            {
                ["action"] = "export",
                ["targets"] = new JArray("SampleDesignSystem"),
                ["outputFile"] = "C:\\tmp\\sample.xpz",
                ["includeDependencies"] = true,
                ["async"] = true
            });

            Assert.True(
                result.Ok,
                "async on export must pass schema pre-validation: "
                + string.Join("; ", result.Violations.Select(v => $"{v.Path} ({v.Actual})")));
        }
    }
}
