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

        // The accepted answer is built from the action alone: the dispatch that starts the job is the one the
        // version-store reads use, and export/inspect never name a versioning read.
        [Theory]
        [InlineData("export")]
        [InlineData("inspect")]
        public void BuildAsyncReadAcceptedPayload_ForATransferSaysTransfer(string action)
        {
            var job = new JobEntry { Id = "0123456789abcdef0123456789abcdef", EstimatedSeconds = 60 };

            JObject payload = Program.BuildAsyncReadAcceptedPayload(job, action);

            Assert.Equal(job.Id, payload["operationId"]?.ToString());
            Assert.Equal("op:" + job.Id, payload["pollTarget"]?.ToString());
            Assert.Equal(action, payload["action"]?.ToString());
            string hint = payload["hint"]?.ToString() ?? string.Empty;
            Assert.StartsWith("Transfer accepted;", hint);
            Assert.Contains("The transfer keeps running if this call's window expires", hint);
            Assert.Contains("genexus_lifecycle(action='result', target='op:" + job.Id + "')", hint);
        }

        [Theory]
        [InlineData("history_get")]
        [InlineData("history_list")]
        [InlineData("history_diff")]
        [InlineData("diff")]
        [InlineData("diff_generated")]
        [InlineData("blame")]
        public void BuildAsyncReadAcceptedPayload_ForAVersionStoreReadKeepsItsOwnWords(string action)
        {
            var job = new JobEntry { Id = "0123456789abcdef0123456789abcdef", EstimatedSeconds = 60 };

            string hint = Program.BuildAsyncReadAcceptedPayload(job, action)["hint"]?.ToString() ?? string.Empty;

            Assert.StartsWith("Read accepted;", hint);
            Assert.Contains("The read keeps running if this call's window expires", hint);
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
