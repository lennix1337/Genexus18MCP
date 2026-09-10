using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class LegacyHubProfileCompatibilityTests
    {
        [Theory]
        [InlineData("legacy", true)]
        [InlineData("strict", false)]
        public async Task DedicatedHttpProfileWithoutSelection_UsesOnlyExplicitLegacyFallback(string policy, bool reachesHandler)
        {
            string directory = Path.Combine(Path.GetTempPath(), "gxmcp-hub-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            // This is a parser sentinel, not a KB. No Worker executable is available.
            File.WriteAllText(Path.Combine(directory, "Example.gxw"), "");
            string configPath = Path.Combine(directory, "config.json");
            var document = new JObject
            {
                ["GeneXus"] = new JObject { ["InstallationPath"] = "not-installed", ["WorkerExecutable"] = "not-a-worker.exe" },
                ["Server"] = new JObject { ["HttpPort"] = 54321, ["McpStdio"] = false, ["MaxOpenKbs"] = 1 },
                ["Environment"] = new JObject { ["KBPath"] = directory, ["ResolutionPolicy"] = policy }
            };
            File.WriteAllText(configPath, document.ToString());
            var parse = typeof(Configuration).GetMethod("ParseConfig", BindingFlags.NonPublic | BindingFlags.Static)!;
            var config = (Configuration)parse.Invoke(null, new object[] { configPath })!;
            try
            {
                using var scope = Program.ConfigureRouteStateForTest(config, configPath);
                Assert.Null(config.ConfigSchemaVersion);
                Assert.Null(config.GatewayMode);
                Assert.Equal(54321, config.Server!.HttpPort);
                Assert.False(config.Server.McpStdio);
                Assert.Single(config.Environment!.KBs);
                // Match Hub: classic MCP HTTP session, JSON + SSE Accept, no explicit
                // KB argument or select/lease. An invalid macro name stops before I/O.
                var request = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "genexus_recipe",
                        ["arguments"] = new JObject { ["action"] = "crystallize", ["macroName"] = "invalid/name", ["steps"] = new JArray(new JObject()) }
                    }
                };
                string session = Program.CreateHttpSessionForTest();
                var context = new DefaultHttpContext();
                context.Request.ContentType = "application/json";
                context.Request.Headers["Accept"] = "application/json, text/event-stream";
                context.Request.Headers["MCP-Protocol-Version"] = "2025-11-25";
                context.Request.Headers["MCP-Session-Id"] = session;
                context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request.ToString()));
                context.Response.Body = new MemoryStream();
                var result = await Program.HandleJsonRpcHttpRequest(context.Request);
                await result.ExecuteAsync(context);
                context.Response.Body.Position = 0;
                var response = JObject.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
                var payload = JObject.Parse(response["result"]!["content"]![0]!["text"]!.ToString());
                if (reachesHandler)
                    Assert.Contains("macroName must match", payload.ToString());
                else
                    Assert.Contains("KB_CONTEXT_REQUIRED", payload.ToString());
                Assert.Empty(Program.GetWorkerPool()!.ListOpen());
                Assert.Equal(document.ToString(), File.ReadAllText(configPath));
            }
            finally
            {
                File.Delete(configPath);
                File.Delete(Path.Combine(directory, "Example.gxw"));
                Directory.Delete(directory);
            }
        }
    }
}
