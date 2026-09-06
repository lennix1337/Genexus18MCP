using System.IO;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ModernHttpSessionIsolationTests
    {
        [Fact]
        public async System.Threading.Tasks.Task Sessionless_request_does_not_consume_shared_http_selection()
        {
            const string sharedModernId = "http-modern";
            Program.SetSessionSelectedKb(sharedModernId, "other-client-kb");
            try
            {
                var request = JObject.Parse(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"genexus_whoami\",\"arguments\":{}}}");

                var response = await Program.ProcessMcpRequest(
                    request,
                    sharedModernId,
                    sessionContextEnabled: false);

                var result = Assert.IsType<JObject>(response!["result"]);
                var payload = JObject.Parse(result["content"]![0]!["text"]!.ToString());
                var selected = payload["kb"]?["selected"];
                Assert.True(selected == null || selected.Type == JTokenType.Null);
            }
            finally
            {
                Program.ClearSessionSelectedKb(sharedModernId);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Modern_http_tasks_require_a_stable_client_scope()
        {
            var job = Program.JobRegistry.Start("http-modern:owner-a", "edit/genexus_edit", 30);
            try
            {
                var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                context.Request.ContentType = "application/json";
                context.Request.Headers["Accept"] = "application/json, text/event-stream";
                context.Request.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;
                context.Request.Headers["Mcp-Method"] = "tasks/get";
                context.Request.Headers["Mcp-Name"] = job.Id;
                context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tasks/get\",\"params\":{\"taskId\":\""
                    + job.Id
                    + "\",\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{\"extensions\":{\"io.modelcontextprotocol/tasks\":{}}}}}}"));
                context.Response.Body = new MemoryStream();

                var result = await Program.HandleJsonRpcHttpRequest(context.Request);
                await result.ExecuteAsync(context);
                context.Response.Body.Position = 0;
                var payload = JObject.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());

                Assert.Equal(-32023, payload["error"]!["code"]!.ToObject<int>());

                context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                context.Request.ContentType = "application/json";
                context.Request.Headers["Accept"] = "application/json, text/event-stream";
                context.Request.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;
                context.Request.Headers["Mcp-Method"] = "tasks/get";
                context.Request.Headers["Mcp-Name"] = job.Id;
                context.Request.Headers[McpHttpProtocol.ModernClientIdHeader] = "owner-b";
                context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tasks/get\",\"params\":{\"taskId\":\""
                    + job.Id
                    + "\",\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{\"extensions\":{\"io.modelcontextprotocol/tasks\":{}}}}}}"));
                context.Response.Body = new MemoryStream();

                result = await Program.HandleJsonRpcHttpRequest(context.Request);
                await result.ExecuteAsync(context);
                context.Response.Body.Position = 0;
                payload = JObject.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());

                Assert.Equal(-32602, payload["error"]!["code"]!.ToObject<int>());
                Assert.Equal("Invalid taskId.", payload["error"]!["message"]!.ToString());
            }
            finally
            {
                Program.JobRegistry.Cancel(job.Id, "test cleanup");
            }
        }
    }
}
