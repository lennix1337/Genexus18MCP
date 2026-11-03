using System;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LifecycleRouterTests
    {
        [Fact]
        public void Lifecycle_Rebuild_Without_Target_Routes_To_RebuildAll()
        {
            var router = new SystemRouter();
            var routed = router.ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "rebuild"
            });

            Assert.NotNull(routed);
            var jobj = JObject.FromObject(routed);
            Assert.Equal("Build", jobj["module"]?.ToString());
            Assert.Equal("RebuildAll", jobj["action"]?.ToString());
        }

        /// <summary>
        /// #830 item 4: the directed forced rebuild is <c>build</c> with <c>force</c>. The route
        /// must forward it; dropping it here would build the object without the force.
        /// </summary>
        [Fact]
        public void Lifecycle_Build_Forwards_Force_To_The_Worker()
        {
            var router = new SystemRouter();
            var routed = router.ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "build",
                ["target"] = "Customer",
                ["force"] = true,
                ["includeCallees"] = "none"
            });

            var jobj = JObject.FromObject(routed!);
            Assert.Equal("Build", jobj["module"]?.ToString());
            Assert.Equal("Build", jobj["action"]?.ToString());
            Assert.Equal("Customer", jobj["target"]?.ToString());
            Assert.True(jobj["force"]?.Value<bool>());
        }

        [Fact]
        public void Lifecycle_Build_Without_Force_Routes_Force_False()
        {
            var router = new SystemRouter();
            var routed = router.ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "build",
                ["target"] = "Customer"
            });

            Assert.False(JObject.FromObject(routed!)["force"]?.Value<bool>());
        }

        [Fact]
        public async Task Lifecycle_Rebuild_Intercept_With_Target_Rejects_TargetNotSupported()
        {
            var args = new JObject
            {
                ["action"] = "rebuild",
                ["target"] = "Customer"
            };

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_lifecycle",
                    ["arguments"] = args
                }
            };

            var respObj = await Program.HandleLifecycleGatewayInterceptsAsync(
                1,
                "genexus_lifecycle",
                args,
                request,
                "test-session",
                CancellationToken.None);

            Assert.NotNull(respObj);
            Assert.True(respObj["isError"]?.Value<bool>() == true || respObj["result"]?["isError"]?.Value<bool>() == true);

            string text = respObj["result"]?["content"]?[0]?["text"]?.ToString() ?? respObj.ToString();
            var payload = JObject.Parse(text);

            Assert.Equal("TargetNotSupported", payload["code"]?.ToString());
            Assert.Contains("action=rebuild does not accept a target", payload["message"]?.ToString());
            Assert.Contains("action=build with target=<object> and force=true", payload["hint"]?.ToString());
            Assert.Equal("Customer", payload["target"]?.ToString());
        }

        /// <summary>
        /// Issue #546: a <c>rebuild</c> carrying a target is refused for both the preview
        /// and the real build. The refusal lives in the intercept that
        /// <c>ProcessMcpRequestCore</c> runs before idempotency, async admission and
        /// <c>McpRouter.ConvertToolCall</c>, so nothing is sent to the Worker. No KB is
        /// open in this process: had the intercept fallen through (null), nothing here
        /// could have produced a <c>TargetNotSupported</c> envelope.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Lifecycle_Rebuild_With_Target_Is_Refused_Before_Worker_Dispatch(bool dryRun)
        {
            var args = new JObject
            {
                ["action"] = "rebuild",
                ["target"] = "Customer",
                ["dryRun"] = dryRun
            };
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_lifecycle",
                    ["arguments"] = args
                }
            };

            var respObj = await Program.HandleLifecycleGatewayInterceptsAsync(
                2, "genexus_lifecycle", args, request, "test-session", CancellationToken.None);

            Assert.NotNull(respObj);
            var payload = JObject.Parse(respObj!["result"]?["content"]?[0]?["text"]?.ToString() ?? "{}");
            Assert.Equal("error", payload["status"]?.ToString());
            Assert.Equal("TargetNotSupported", payload["code"]?.ToString());
            Assert.Equal("Customer", payload["target"]?.ToString());
            Assert.Null(payload["preview"]);

            // Defense in depth: even the Worker-bound command for this call is a KB-wide
            // RebuildAll that still carries the target, which the Worker refuses too.
            var routed = JObject.FromObject(new SystemRouter().ConvertToolCall("genexus_lifecycle", args)!);
            Assert.Equal("RebuildAll", routed["action"]?.ToString());
            Assert.Equal("Customer", routed["target"]?.ToString());
            Assert.Equal(dryRun, routed["dryRun"]?.Value<bool>());
        }

        [Fact]
        public void Lifecycle_Help_And_Schema_Say_Rebuild_Rejects_Target()
        {
            // Issue #546: the help used to promise that `rebuild` "remains compatible with
            // directed targets" while the intercept refused them; the discovery golden
            // stores only a truncated purpose summary, so this is the guard for the text.
            string help = ToolHelpCatalog.Get("genexus_lifecycle") ?? string.Empty;
            string rebuildLine = Array.Find(help.Split('\n'), l => l.StartsWith("- `rebuild`", StringComparison.Ordinal)) ?? string.Empty;
            Assert.Contains("rejects `target` with `TargetNotSupported`", rebuildLine, StringComparison.Ordinal);
            Assert.DoesNotContain("remains compatible with directed targets", help, StringComparison.Ordinal);

            var tools = JArray.Parse(GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
            var lifecycle = Assert.Single(tools, t => t["name"]?.ToString() == "genexus_lifecycle");
            Assert.Contains("action=rebuild runs the forced Rebuild All for the entire selected KB", lifecycle["description"]?.ToString(), StringComparison.Ordinal);
            Assert.Contains("rejects target with TargetNotSupported", lifecycle["description"]?.ToString(), StringComparison.Ordinal);
            Assert.Contains("build_all and rebuild reject target.", lifecycle["inputSchema"]?["properties"]?["target"]?["description"]?.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Lifecycle_Intercept_Runs_Before_Tool_Dispatch_In_RequestLoop()
        {
            // The refusal above only keeps the Worker out of it while the intercept stays
            // ahead of the idempotency-wrapped dispatch in ProcessMcpRequestCore.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Gateway", "Program.RequestLoop.cs");

            int intercept = source.IndexOf("await HandleLifecycleGatewayInterceptsAsync(", StringComparison.Ordinal);
            int dispatch = source.IndexOf("DispatchToolCallCoreAsync(tcParams", StringComparison.Ordinal);

            Assert.True(intercept > 0, "the lifecycle intercept call was not found");
            Assert.True(dispatch > intercept, "the tool dispatch must come after the lifecycle intercept");
        }
    }
}
