using System;
using System.IO;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class SharedWorkerRefusalRouteTests
    {
        [Fact]
        public void StdioRefusal_ReturnsCatalogDetailsInJsonRpcErrorData()
        {
            var error = new JObject { ["code"] = "KB_POOL_FULL", ["reason"] = "low-memory", ["retryable"] = false };
            var exception = new SharedWorkerLaunchException("KB_POOL_FULL", "low-memory", error);
            JObject response = Program.BuildSharedWorkerLaunchErrorResponse(new JValue(17), exception);

            Assert.Equal(17, (int)response["id"]!);
            Assert.Equal(-32000, (int)response["error"]!["code"]!);
            Assert.Equal("KB_POOL_FULL", (string?)response["error"]!["data"]!["code"]);
            Assert.Equal("low-memory", (string?)response["error"]!["data"]!["reason"]);
            Assert.False((bool)response["error"]!["data"]!["retryable"]!);
        }

        [Theory]
        [InlineData("KB_WORKER_OWNED_ELSEWHERE", "liveness-unverified", false)]
        [InlineData("KB_POOL_FULL", "low-memory", false)]
        [InlineData("WORKER_STARTUP_FAILED", "cold-start-wait-expired", true)]
        public async Task KbOpen_PreservesTypedSharedWorkerRefusal(string code, string reason, bool retryable)
        {
            string directory = Path.Combine(Path.GetTempPath(), "gxmcp-refusal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "uvs.gxw"), "");
            var config = new Configuration { Environment = new EnvironmentConfig { ResolutionPolicy = "strict" }, Server = new ServerConfig { WorkerSharingMode = "shared-host" } };
            using var state = Program.ConfigureRouteStateForTest(config, Path.Combine(directory, "config.json"), _ =>
                throw new SharedWorkerLaunchException(code, reason, new JObject
                {
                    ["code"] = code, ["reason"] = reason, ["retryable"] = retryable
                }));
            try
            {
                var response = await Program.ProcessMcpRequest(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JObject { ["name"] = "genexus_kb", ["arguments"] = new JObject { ["action"] = "open", ["path"] = directory } }
                });
                Assert.True(response!["result"]!["isError"]!.Value<bool>());
                var payload = JObject.Parse(response["result"]!["content"]![0]!["text"]!.ToString());
                Assert.Equal(code, (string?)payload["code"]);
                Assert.Equal(reason, (string?)payload["reason"]);
                Assert.Equal(retryable, payload["retryable"]!.Value<bool>());
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
