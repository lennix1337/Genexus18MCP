using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ModernHttpTaskCreationTests
    {
        [Theory]
        [InlineData(true, null, -32023)]
        [InlineData(true, "owner-a", null)]
        [InlineData(false, null, null)]
        public async Task ToolCall_WithTasksOptInRequiresClientScope(bool optsIntoTasks, string? clientId, int? expectedError)
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json";
            context.Request.Headers["Accept"] = "application/json, text/event-stream";
            context.Request.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;
            context.Request.Headers["Mcp-Method"] = "tools/call";
            context.Request.Headers["Mcp-Name"] = "genexus_whoami";
            if (clientId != null) context.Request.Headers["Mcp-Client-Id"] = clientId;
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            context.RequestServices = services;
            var call = JObject.Parse("""
                {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"genexus_whoami","arguments":{}}}
                """);
            call["params"]!["_meta"] = new JObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = McpRouter.ModernProtocolVersion,
                ["io.modelcontextprotocol/clientCapabilities"] = optsIntoTasks
                    ? new JObject { ["extensions"] = new JObject { [McpTasksProtocol.ExtensionId] = new JObject() } }
                    : new JObject()
            };
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(call.ToString()));
            context.Response.Body = new MemoryStream();

            var result = await Program.HandleJsonRpcHttpRequest(context.Request);
            await result.ExecuteAsync(context);
            context.Response.Body.Position = 0;
            var payload = JObject.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());

            Assert.Equal(expectedError, payload["error"]?["code"]?.Value<int>());
            if (expectedError.HasValue)
                Assert.Equal("Mcp-Client-Id", payload["error"]?["data"]?["requiredHeader"]?.ToString());
            else
                Assert.False(payload["result"]?["isError"]?.Value<bool>());
        }
    }
}
