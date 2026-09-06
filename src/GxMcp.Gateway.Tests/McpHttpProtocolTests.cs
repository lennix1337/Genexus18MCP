using System;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class McpHttpProtocolTests
    {
        [Fact]
        public void TryApplyProtocol_ShouldSetSupportedVersionWhenHeaderIsMissing()
        {
            var context = new DefaultHttpContext();

            var error = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);

            Assert.Null(error);
            Assert.Equal(McpRouter.SupportedProtocolVersion, context.Response.Headers["MCP-Protocol-Version"].ToString());
        }

        [Fact]
        public void TryApplyProtocol_ShouldAcceptKnownOlderVersion()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = "2025-03-26";

            var error = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);

            Assert.Null(error);
            Assert.Equal("2025-03-26", context.Response.Headers["MCP-Protocol-Version"].ToString());
        }

        [Fact]
        public void TryApplyProtocol_ShouldRejectUnknownVersion()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = "2099-01-01";

            var error = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status400BadRequest, error.Value.StatusCode);
            Assert.Contains("Unsupported MCP protocol version", error.Value.Message);
        }

        [Fact]
        public void TryApplyProtocol_ShouldRejectVersionDifferentFromSession()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = "2025-03-26";

            var error = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers, "2025-11-25");

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status400BadRequest, error.Value.StatusCode);
            Assert.Contains("does not match the negotiated session version", error.Value.Message);
        }

        [Fact]
        public void TryApplyProtocol_ShouldAcceptSupportedVersion()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = McpRouter.SupportedProtocolVersion;

            var error = McpHttpProtocol.TryApplyProtocol(context.Request, context.Response.Headers);

            Assert.Null(error);
            Assert.Equal(McpRouter.SupportedProtocolVersion, context.Response.Headers["MCP-Protocol-Version"].ToString());
        }

        [Fact]
        public void ValidateModernRequest_ShouldAcceptPerRequestMetadataAndRoutingHeaders()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;
            context.Request.Headers["Mcp-Method"] = "tools/call";
            context.Request.Headers["Mcp-Name"] = "genexus_whoami";
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"genexus_whoami","arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}"""
            );

            var error = McpHttpProtocol.ValidateModernRequest(context.Request, request);

            Assert.Null(error);
        }

        [Fact]
        public void ValidateModernRequest_ShouldRequireClientCapabilitiesForRequests()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;
            context.Request.Headers["Mcp-Method"] = "tools/list";
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}"""
            );

            var error = McpHttpProtocol.ValidateModernRequest(context.Request, request);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status400BadRequest, error.Value.StatusCode);
            Assert.Equal(-32602, error.Value.JsonRpcCode);
            Assert.Equal("params._meta.io.modelcontextprotocol/clientCapabilities", error.Value.Data?["field"]?.ToString());
        }

        [Fact]
        public void ValidateModernRequest_ShouldRequireProtocolVersionInMeta()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;
            context.Request.Headers["Mcp-Method"] = "tools/list";
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"protocolVersion":"2026-07-28"}}"""
            );

            var error = McpHttpProtocol.ValidateModernRequest(context.Request, request);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status400BadRequest, error.Value.StatusCode);
            Assert.Equal(-32020, error.Value.JsonRpcCode);
        }

        [Fact]
        public void ValidateModernRequest_ShouldRejectHeaderBodyMismatch()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;
            context.Request.Headers["Mcp-Method"] = "tools/list";
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2025-11-25"}}}"""
            );

            var error = McpHttpProtocol.ValidateModernRequest(context.Request, request);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status400BadRequest, error.Value.StatusCode);
            Assert.Equal(-32020, error.Value.JsonRpcCode);
        }

        [Fact]
        public void ValidateModernRequest_ShouldDecodeMcpNameSentinel()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Protocol-Version"] = McpRouter.ModernProtocolVersion;
            context.Request.Headers["Mcp-Method"] = "resources/read";
            context.Request.Headers["Mcp-Name"] = McpHttpProtocol.EncodeHeaderValue("genexus://kb/世界");
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"genexus://kb/世界","_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}"""
            );

            Assert.Null(McpHttpProtocol.ValidateModernRequest(context.Request, request));
        }

        [Fact]
        public void GetStandardHeaderName_ShouldUseTaskIdForTaskRequests()
        {
            var request = JObject.Parse(
                "{\"method\":\"tasks/get\",\"params\":{\"taskId\":\"task-123\"}}"
            );

            Assert.Equal("task-123", McpHttpProtocol.GetStandardHeaderName(request));
        }

        [Fact]
        public void ValidatePostHeaders_ShouldAcceptJsonAndSse()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json; charset=utf-8";
            context.Request.Headers["Accept"] = "application/json, text/event-stream";

            Assert.Null(McpHttpProtocol.ValidatePostHeaders(context.Request));
        }

        [Fact]
        public void ValidatePostHeaders_ShouldRejectWrongContentType()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "text/plain";
            context.Request.Headers["Accept"] = "application/json, text/event-stream";

            var error = McpHttpProtocol.ValidatePostHeaders(context.Request);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status415UnsupportedMediaType, error.Value.StatusCode);
        }

        [Fact]
        public void ValidatePostHeaders_ShouldRejectMissingOrUnacceptableMediaType()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json";
            context.Request.Headers["Accept"] = "application/json, text/event-stream; q=0";

            var error = McpHttpProtocol.ValidatePostHeaders(context.Request);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status406NotAcceptable, error.Value.StatusCode);
        }

        [Fact]
        public void ValidatePostHeaders_ShouldRejectMalformedOrOutOfRangeQuality()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json";
            context.Request.Headers["Accept"] = "application/json, text/event-stream; q=2";

            var error = McpHttpProtocol.ValidatePostHeaders(context.Request);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status406NotAcceptable, error.Value.StatusCode);
        }

        [Fact]
        public void ValidateBodyLength_AllowsUnknownLength()
        {
            Assert.Null(McpHttpProtocol.ValidateBodyLength(null));
        }

        [Theory]
        [InlineData(0L, false)]
        [InlineData(2097152L, false)]
        [InlineData(2097153L, true)]
        public void ValidateBodyLength_EnforcesTheGatewayLimit(long contentLength, bool rejected)
        {
            var error = McpHttpProtocol.ValidateBodyLength(contentLength);

            Assert.Equal(rejected, error.HasValue);
            if (rejected)
            {
                Assert.Equal(StatusCodes.Status413PayloadTooLarge, error!.Value.StatusCode);
                Assert.Equal(-32600, error.Value.JsonRpcCode);
                Assert.Equal(2097152, error.Value.Data?["maxBytes"]?.Value<int>());
            }
        }

        [Fact]
        public void ValidateSseHeaders_ShouldRequireEventStream()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["Accept"] = "application/json";

            var error = McpHttpProtocol.ValidateSseHeaders(context.Request);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status406NotAcceptable, error.Value.StatusCode);
        }

        [Fact]
        public void TryGetValidSession_ShouldAllowInitializeWithoutSessionHeader()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var context = new DefaultHttpContext();
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"init","method":"initialize"}""");

            var error = McpHttpProtocol.TryGetValidSession(registry, context.Request, request, out var session);

            Assert.Null(error);
            Assert.Null(session);
        }

        [Fact]
        public void TryGetValidSession_ShouldRequireSessionHeaderForNonInitializeCalls()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var context = new DefaultHttpContext();
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"tools/list"}""");

            var error = McpHttpProtocol.TryGetValidSession(registry, context.Request, request, out var session);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status400BadRequest, error.Value.StatusCode);
            Assert.Equal("Missing MCP-Session-Id header.", error.Value.Message);
            Assert.Null(session);
        }

        [Fact]
        public void TryGetValidSession_ShouldSkipSessionForModernRequests()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var context = new DefaultHttpContext();
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}"""
            );

            var error = McpHttpProtocol.TryGetValidSession(registry, context.Request, request, out var session, modern: true);

            Assert.Null(error);
            Assert.Null(session);
        }

        [Fact]
        public void TryGetValidSession_ShouldRejectExpiredSession()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMilliseconds(1));
            var session = registry.Create();
            var context = new DefaultHttpContext();
            context.Request.Headers["MCP-Session-Id"] = session.Id;
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"tools/list"}""");

            System.Threading.Thread.Sleep(20);

            var error = McpHttpProtocol.TryGetValidSession(registry, context.Request, request, out var resolved);

            Assert.NotNull(error);
            Assert.Equal(StatusCodes.Status404NotFound, error.Value.StatusCode);
            Assert.Equal("Unknown or expired MCP session.", error.Value.Message);
            Assert.Null(resolved);
        }

        [Fact]
        public void IsInitializeRequest_RequiresExactMethodName()
        {
            Assert.True(McpHttpProtocol.IsInitializeRequest(JObject.Parse("""{"method":"initialize"}""")));
            Assert.False(McpHttpProtocol.IsInitializeRequest(JObject.Parse("""{"method":"Initialize"}""")));
            Assert.False(McpHttpProtocol.IsInitializeRequest(JObject.Parse("""{"method":"initialize "}""")));
        }

        [Fact]
        public void CancellationNotificationIsRecognizedWithoutTreatingItAsARequestId()
        {
            Assert.True(McpHttpProtocol.IsCancellationNotification(
                JObject.Parse("""{"method":"notifications/cancelled","params":{"requestId":1}}""")));
            Assert.False(McpHttpProtocol.IsCancellationNotification(
                JObject.Parse("""{"method":"notifications/cancelled "}""")));
        }
    }
}
