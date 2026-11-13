using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The server emits notifications/message, so it must declare the logging capability
    /// and honour logging/setLevel (MCP spec). Until a client sets a level the gateway's
    /// existing filtering is unchanged.
    /// </summary>
    public sealed class LoggingCapabilityTests
    {
        [Fact]
        public void Initialize_DeclaresLoggingCapability()
        {
            var response = JObject.FromObject(McpRouter.Handle(JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""))!);

            Assert.NotNull(response["capabilities"]!["logging"]);
        }

        [Fact]
        public void SetLevel_AcceptsRfc5424LevelsAndRejectsOthers()
        {
            try
            {
                var ok = JObject.FromObject(McpRouter.Handle(JObject.Parse(
                    """{"jsonrpc":"2.0","id":"1","method":"logging/setLevel","params":{"level":"warning"}}"""))!);
                Assert.Equal("complete", ok["resultType"]?.ToString());

                var bad = McpRouter.Handle(JObject.Parse(
                    """{"jsonrpc":"2.0","id":"2","method":"logging/setLevel","params":{"level":"verbose"}}"""));
                var error = Assert.IsType<McpRouterError>(bad);
                Assert.Equal(-32602, error.Code);
            }
            finally { Program.ResetMinimumLogLevel(); }
        }

        [Fact]
        public void MinimumLevel_GatesNotificationsMessageOnly()
        {
            var info = new JObject { ["level"] = "info", ["logger"] = "gateway", ["data"] = "x" };
            var error = new JObject { ["level"] = "error", ["logger"] = "gateway", ["data"] = "x" };
            try
            {
                Program.ResetMinimumLogLevel();
                Assert.True(Program.PassesMinimumLogLevel("notifications/message", info));

                Assert.True(Program.TrySetMinimumLogLevel("warning"));
                Assert.False(Program.PassesMinimumLogLevel("notifications/message", info));
                Assert.True(Program.PassesMinimumLogLevel("notifications/message", error));
                Assert.True(Program.PassesMinimumLogLevel("notifications/progress", info));
            }
            finally { Program.ResetMinimumLogLevel(); }
        }
    }
}
