using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // https://github.com/lennix1337/Genexus18MCP/issues/1112
    [Collection("Gateway route state")]
    public sealed class ExplicitKbAndOpenPolicyTests : IDisposable
    {
        private const string SdkReady = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}";

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gxmcp-issue1112-" + Guid.NewGuid().ToString("N"));
        private readonly List<IDisposable> _disposables = new();
        private readonly List<(string Alias, string Method, string Action)> _workerCalls = new();
        private readonly string _session = "issue1112-" + Guid.NewGuid().ToString("N");

        public ExplicitKbAndOpenPolicyTests()
        {
            Directory.CreateDirectory(_directory);
        }

        private string CreatePlausibleKb(string name)
        {
            string kbPath = Path.Combine(_directory, name);
            Directory.CreateDirectory(kbPath);
            File.WriteAllText(Path.Combine(kbPath, name + ".gxw"), "");
            return kbPath;
        }

        private Configuration StartGateway(
            bool requireExplicitKbForWrites = false,
            bool openOnlyConfiguredKbs = false,
            bool includeConfiguredKbs = true)
        {
            Program.ResetIndexStateMirrorForTest();
            Program.IndexBootstrapTriggerForTest = () => { };

            string customerKb = CreatePlausibleKb("CustomerKB");
            string ordersKb = CreatePlausibleKb("OrdersKB");
            string configPath = Path.Combine(_directory, "config.json");
            File.WriteAllText(configPath, "{}");

            var config = new Configuration
            {
                Server = new ServerConfig
                {
                    RequireExplicitKbForWrites = requireExplicitKbForWrites,
                    OpenOnlyConfiguredKbs = openOnlyConfiguredKbs
                },
                Environment = new EnvironmentConfig
                {
                    ResolutionPolicy = "strict"
                }
            };

            if (includeConfiguredKbs)
            {
                config.Environment.KBs.Add(new KbEntry { Alias = "customer", Path = customerKb });
                config.Environment.KBs.Add(new KbEntry { Alias = "orders", Path = ordersKb });
            }

            _disposables.Add(Program.ConfigureRouteStateForTest(config, configPath, handle =>
            {
                var worker = new WorkerProcess(config, handle);
                worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
                worker.FakeWorkerForTest = rpc => AnswerWorker(handle.Alias, rpc);
                worker.HandleWorkerRpcResponseForTest(SdkReady);
                return worker;
            }));
            _disposables.Add(Program.AttachWorkerResponseRoutingForTest());
            return config;
        }

        private JObject AnswerWorker(string alias, JObject rpc)
        {
            string method = rpc["method"]?.ToString() ?? string.Empty;
            string action = rpc["action"]?.ToString() ?? string.Empty;
            bool isIndexState = string.Equals(method, "kb", StringComparison.OrdinalIgnoreCase)
                && string.Equals(action, "GetIndexState", StringComparison.OrdinalIgnoreCase);

            if (!isIndexState)
            {
                lock (_workerCalls)
                    _workerCalls.Add((alias, method, action));
            }

            JObject result = isIndexState
                ? new JObject
                {
                    ["indexStatus"] = "Ready",
                    ["freshness"] = "fresh",
                    ["totalObjects"] = 1
                }
                : new JObject
                {
                    ["status"] = "Success",
                    ["kb"] = alias,
                    ["source"] = "// fake source"
                };

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = rpc["id"],
                ["result"] = result
            };
        }

        private async Task<JObject> CallToolAsync(string toolName, JObject arguments)
        {
            var response = await Program.ProcessMcpRequest(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "issue1112-" + Guid.NewGuid().ToString("N"),
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = toolName,
                    ["arguments"] = arguments
                }
            }, _session);
            Assert.NotNull(response);
            return response!;
        }

        private async Task OpenAndSelectAsync(string alias)
        {
            var openResp = await CallToolAsync("genexus_kb", new JObject { ["action"] = "open", ["alias"] = alias });
            Assert.False(openResp["result"]?["isError"]?.Value<bool>() ?? false, openResp.ToString());

            var selectResp = await CallToolAsync("genexus_kb", new JObject { ["action"] = "select", ["alias"] = alias });
            Assert.False(selectResp["result"]?["isError"]?.Value<bool>() ?? false, selectResp.ToString());

            lock (_workerCalls) _workerCalls.Clear();
        }

        private static JObject ParseToolPayload(JObject response)
        {
            string? text = response.SelectToken("result.content[0].text")?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(text), $"Expected tool text content in response: {response}");
            return JObject.Parse(text!);
        }

        private static Configuration ParseConfig(string path)
        {
            var method = typeof(Configuration).GetMethod("ParseConfig", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            try
            {
                return (Configuration)method!.Invoke(null, new object[] { path })!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        [Fact]
        public void ServerConfig_DefaultsKeepBothOptionsFalse()
        {
            var server = new ServerConfig();
            Assert.False(server.RequireExplicitKbForWrites);
            Assert.False(server.OpenOnlyConfiguredKbs);
        }

        [Fact]
        public void ParseConfig_StrictV2_AcceptsBothKbPolicyBoolOptions()
        {
            string configPath = Path.Combine(_directory, "strict-valid.json");
            File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": {
    ""HttpPort"": 0,
    ""McpStdio"": true,
    ""RequireExplicitKbForWrites"": true,
    ""OpenOnlyConfiguredKbs"": true
  },
  ""Environment"": { ""ResolutionPolicy"": ""strict"" }
}");

            var cfg = ParseConfig(configPath);
            Assert.True(cfg.Server!.RequireExplicitKbForWrites);
            Assert.True(cfg.Server.OpenOnlyConfiguredKbs);
        }

        [Theory]
        [InlineData("RequireExplicitKbForWrites", "\"yes\"")]
        [InlineData("OpenOnlyConfiguredKbs", "1")]
        public void ParseConfig_StrictV2_RejectsNonBooleanKbPolicyOptions(string propertyName, string rawJsonValue)
        {
            string configPath = Path.Combine(_directory, $"strict-invalid-{propertyName}.json");
            File.WriteAllText(configPath, $@"{{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": {{ ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" }},
  ""Server"": {{
    ""HttpPort"": 0,
    ""McpStdio"": true,
    ""{propertyName}"": {rawJsonValue}
  }},
  ""Environment"": {{ ""ResolutionPolicy"": ""strict"" }}
}}");

            var ex = Assert.Throws<InvalidDataException>(() => ParseConfig(configPath));
            Assert.Contains(propertyName, ex.Message);
        }

        [Theory]
        [InlineData("genexus_edit", "{\"name\":\"Customer\",\"part\":\"Source\",\"content\":\"msg('hi')\"}")]
        [InlineData("genexus_properties", "{\"action\":\"set\",\"name\":\"Customer\",\"property\":\"Description\",\"value\":\"Cust\"}")]
        [InlineData("genexus_delete_object", "{\"name\":\"Customer\",\"type\":\"Procedure\",\"confirm\":true}")]
        [InlineData("genexus_create", "{\"action\":\"object\",\"type\":\"Procedure\",\"name\":\"PNew\"}")]
        [InlineData("genexus_kb", "{\"action\":\"set_environment\",\"environment\":\"development\"}")]
        [InlineData("genexus_kb", "{\"action\":\"set_startup\",\"name\":\"Home\"}")]
        [InlineData("genexus_structure", "{\"action\":\"set_attribute\",\"name\":\"Customer\",\"attribute\":\"CustomerName\",\"dryRun\":true}")]
        [InlineData("genexus_edit", "{\"name\":\"Customer\",\"part\":\"Source\",\"dryRun\":true,\"changeSet\":{\"action\":\"apply\"}}")]
        public async Task RequireExplicitKbForWrites_On_RefusesWritesWithoutKb_AndListsConfiguredAliases(string tool, string rawArgs)
        {
            StartGateway(requireExplicitKbForWrites: true);
            await OpenAndSelectAsync("customer");

            var response = await CallToolAsync(tool, JObject.Parse(rawArgs));

            Assert.True(response["result"]?["isError"]?.Value<bool>() == true, response.ToString());
            var payload = ParseToolPayload(response);
            Assert.Equal("EXPLICIT_KB_REQUIRED_FOR_WRITE", payload["error"]?["code"]?.ToString());
            Assert.Equal("EXPLICIT_KB_REQUIRED_FOR_WRITE", payload["code"]?.ToString());
            Assert.False(payload["wrote"]?.Value<bool>() ?? true);
            Assert.False(payload["persisted"]?.Value<bool>() ?? true);

            var aliases = payload["configuredAliases"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            Assert.Contains("customer", aliases);
            Assert.Contains("orders", aliases);

            string message = payload["error"]?["message"]?.ToString() ?? string.Empty;
            Assert.Contains("customer", message);
            Assert.Contains("orders", message);

            lock (_workerCalls) Assert.Empty(_workerCalls);
        }

        [Fact]
        public async Task RequireExplicitKbForWrites_On_AllowsSameWriteWhenKbIsExplicit()
        {
            StartGateway(requireExplicitKbForWrites: true);
            await OpenAndSelectAsync("customer");

            var response = await CallToolAsync("genexus_edit", new JObject
            {
                ["kb"] = "customer",
                ["name"] = "Customer",
                ["part"] = "Source",
                ["content"] = "msg('hi')"
            });

            Assert.False(response["result"]?["isError"]?.Value<bool>() ?? false, response.ToString());
            lock (_workerCalls)
                Assert.Contains(_workerCalls, c => c.Alias == "customer");
        }

        [Theory]
        [InlineData("genexus_read", "{\"name\":\"Customer\",\"part\":\"Source\"}")]
        [InlineData("genexus_properties", "{\"action\":\"get\",\"name\":\"Customer\"}")]
        [InlineData("genexus_list_objects", "{}")]
        [InlineData("genexus_edit", "{\"name\":\"Customer\",\"part\":\"Source\",\"content\":\"msg('hi')\",\"dryRun\":true}")]
        public async Task RequireExplicitKbForWrites_On_AllowsReadsAndEffectFreePreviewsWithoutKb(string tool, string rawArgs)
        {
            StartGateway(requireExplicitKbForWrites: true);
            await OpenAndSelectAsync("customer");

            var response = await CallToolAsync(tool, JObject.Parse(rawArgs));

            Assert.False(response["result"]?["isError"]?.Value<bool>() ?? false, response.ToString());
            lock (_workerCalls)
                Assert.Contains(_workerCalls, c => c.Alias == "customer");
        }

        [Fact]
        public async Task RequireExplicitKbForWrites_Off_AllowsWriteWithoutKb_DefaultUnchanged()
        {
            StartGateway(requireExplicitKbForWrites: false);
            await OpenAndSelectAsync("customer");

            var response = await CallToolAsync("genexus_edit", new JObject
            {
                ["name"] = "Customer",
                ["part"] = "Source",
                ["content"] = "msg('hi')"
            });

            Assert.False(response["result"]?["isError"]?.Value<bool>() ?? false, response.ToString());
            lock (_workerCalls)
                Assert.Contains(_workerCalls, c => c.Alias == "customer");
        }

        [Fact]
        public async Task RequireExplicitKbForWrites_On_RefusesWriteEvenWhenNoKbIsOpen_AndListsAliases()
        {
            StartGateway(requireExplicitKbForWrites: true);

            var response = await CallToolAsync("genexus_edit", new JObject
            {
                ["name"] = "Customer",
                ["part"] = "Source",
                ["content"] = "msg('hi')"
            });

            Assert.True(response["result"]?["isError"]?.Value<bool>() == true, response.ToString());
            var payload = ParseToolPayload(response);
            Assert.Equal("EXPLICIT_KB_REQUIRED_FOR_WRITE", payload["error"]?["code"]?.ToString());
            var aliases = payload["configuredAliases"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            Assert.Contains("customer", aliases);
            Assert.Contains("orders", aliases);
        }

        [Fact]
        public async Task RequireExplicitKbForWrites_On_RefusesBeforeDestructiveElicitationPrompt()
        {
            StartGateway(requireExplicitKbForWrites: true);
            await OpenAndSelectAsync("customer");

            int prompts = 0;
            ElicitationBroker.ModeOverrideForTest = ElicitationMode.Auto;
            ElicitationBroker.TimeoutOverrideForTest = TimeSpan.FromSeconds(5);
            using var sender = ElicitationBroker.RegisterSender(_session, req =>
            {
                Interlocked.Increment(ref prompts);
                var reply = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = req["id"]?.DeepClone(),
                    ["result"] = new JObject { ["action"] = "accept", ["content"] = new JObject { ["confirm"] = true } }
                };
                _ = Task.Run(() => Program.ProcessMcpRequest(reply, _session));
                return Task.CompletedTask;
            });
            ElicitationBroker.ObserveInitialize(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JObject { ["elicitation"] = new JObject() }
                }
            }, _session);

            try
            {
                var response = await CallToolAsync("genexus_delete_object", new JObject
                {
                    ["name"] = "Customer",
                    ["type"] = "Procedure",
                    ["confirm"] = true
                });

                Assert.Equal(0, prompts);
                Assert.True(response["result"]?["isError"]?.Value<bool>() == true, response.ToString());
                var payload = ParseToolPayload(response);
                Assert.Equal("EXPLICIT_KB_REQUIRED_FOR_WRITE", payload["error"]?["code"]?.ToString());
            }
            finally
            {
                ElicitationBroker.ForgetSessionForTest(_session);
                ElicitationBroker.ModeOverrideForTest = null;
                ElicitationBroker.TimeoutOverrideForTest = null;
            }
        }

        [Fact]
        public async Task OpenOnlyConfiguredKbs_On_RefusesOpenByPath_AndListsConfiguredAliases()
        {
            StartGateway(openOnlyConfiguredKbs: true);
            string adHocKb = CreatePlausibleKb("AdHocKB");

            var response = await CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "open",
                ["path"] = adHocKb
            });

            Assert.True(response["result"]?["isError"]?.Value<bool>() == true, response.ToString());
            var payload = ParseToolPayload(response);
            Assert.Equal("KB_OPEN_ONLY_CONFIGURED", payload["error"]?["code"]?.ToString());
            Assert.Equal("KB_OPEN_ONLY_CONFIGURED", payload["code"]?.ToString());

            var aliases = payload["configuredAliases"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            Assert.Contains("customer", aliases);
            Assert.Contains("orders", aliases);

            string message = payload["error"]?["message"]?.ToString() ?? string.Empty;
            Assert.Contains("customer", message);
            Assert.Contains("orders", message);
        }

        [Fact]
        public async Task OpenOnlyConfiguredKbs_On_RefusesOpenByPathEvenWhenCombinedWithConfiguredAlias()
        {
            StartGateway(openOnlyConfiguredKbs: true);
            string adHocKb = CreatePlausibleKb("SmuggledKB");

            var response = await CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "open",
                ["alias"] = "customer",
                ["path"] = adHocKb
            });

            Assert.True(response["result"]?["isError"]?.Value<bool>() == true, response.ToString());
            var payload = ParseToolPayload(response);
            Assert.Equal("KB_OPEN_ONLY_CONFIGURED", payload["error"]?["code"]?.ToString());
        }

        [Fact]
        public async Task OpenOnlyConfiguredKbs_On_RefusesFilesystemPathPassedAsAlias_AndUnconfiguredAlias()
        {
            StartGateway(openOnlyConfiguredKbs: true);
            string adHocKb = CreatePlausibleKb("AliasPathKB");

            var pathAsAlias = await CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "open",
                ["alias"] = adHocKb
            });
            Assert.True(pathAsAlias["result"]?["isError"]?.Value<bool>() == true, pathAsAlias.ToString());
            var pathPayload = ParseToolPayload(pathAsAlias);
            Assert.Equal("KB_OPEN_ONLY_CONFIGURED", pathPayload["error"]?["code"]?.ToString());
            Assert.Contains("customer", pathPayload["configuredAliases"]?.Values<string>() ?? Array.Empty<string>());

            var unknownAlias = await CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "open",
                ["alias"] = "not-declared"
            });
            Assert.True(unknownAlias["result"]?["isError"]?.Value<bool>() == true, unknownAlias.ToString());
            var unknownPayload = ParseToolPayload(unknownAlias);
            Assert.Equal("KB_OPEN_ONLY_CONFIGURED", unknownPayload["error"]?["code"]?.ToString());
            Assert.Contains("orders", unknownPayload["configuredAliases"]?.Values<string>() ?? Array.Empty<string>());
        }

        [Fact]
        public async Task OpenOnlyConfiguredKbs_On_AllowsOpenByConfiguredAlias()
        {
            StartGateway(openOnlyConfiguredKbs: true);

            var response = await CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "open",
                ["alias"] = "customer"
            });

            Assert.False(response["result"]?["isError"]?.Value<bool>() ?? false, response.ToString());
            var payload = ParseToolPayload(response);
            Assert.Equal("customer", payload["opened"]?.ToString());
        }

        [Fact]
        public async Task OpenOnlyConfiguredKbs_Off_AllowsOpenByPath_DefaultUnchanged()
        {
            StartGateway(openOnlyConfiguredKbs: false);
            string adHocKb = CreatePlausibleKb("AllowedAdHocKB");

            var response = await CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "open",
                ["path"] = adHocKb
            });

            Assert.False(response["result"]?["isError"]?.Value<bool>() ?? false, response.ToString());
            var payload = ParseToolPayload(response);
            Assert.Equal("allowedadhockb", payload["opened"]?.ToString());
        }

        [Fact]
        public async Task OpenOnlyConfiguredKbs_On_RefusesAdHocPathInSelectAndToolKbArgument()
        {
            StartGateway(openOnlyConfiguredKbs: true);
            string adHocKb = CreatePlausibleKb("BypassKB");

            var selectByPath = await CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "select",
                ["path"] = adHocKb
            });
            Assert.True(selectByPath["result"]?["isError"]?.Value<bool>() == true, selectByPath.ToString());
            Assert.Equal("KB_OPEN_ONLY_CONFIGURED", ParseToolPayload(selectByPath)["error"]?["code"]?.ToString());

            var readByPath = await CallToolAsync("genexus_read", new JObject
            {
                ["kb"] = adHocKb,
                ["name"] = "Customer",
                ["part"] = "Source"
            });
            Assert.True(readByPath["result"]?["isError"]?.Value<bool>() == true, readByPath.ToString());
            var readPayload = ParseToolPayload(readByPath);
            Assert.Equal("KB_OPEN_ONLY_CONFIGURED", readPayload["error"]?["code"]?.ToString());
            Assert.Contains("customer", readPayload["configuredAliases"]?.Values<string>() ?? Array.Empty<string>());
            lock (_workerCalls) Assert.Empty(_workerCalls);
        }

        [Fact]
        public async Task RequireExplicitKbForWrites_On_AllowsPoolActionsWithoutKb()
        {
            StartGateway(requireExplicitKbForWrites: true);

            var list = await CallToolAsync("genexus_kb", new JObject { ["action"] = "list" });
            Assert.False(list["result"]?["isError"]?.Value<bool>() ?? false, list.ToString());

            var open = await CallToolAsync("genexus_kb", new JObject { ["action"] = "open", ["alias"] = "customer" });
            Assert.False(open["result"]?["isError"]?.Value<bool>() ?? false, open.ToString());

            var select = await CallToolAsync("genexus_kb", new JObject { ["action"] = "select", ["alias"] = "customer" });
            Assert.False(select["result"]?["isError"]?.Value<bool>() ?? false, select.ToString());
        }

        [Fact]
        public async Task OpenOnlyConfiguredKbs_On_WithNoConfiguredKbs_RefusesWithEmptyAliasList()
        {
            StartGateway(openOnlyConfiguredKbs: true, includeConfiguredKbs: false);
            string adHocKb = CreatePlausibleKb("EmptyConfigKB");

            var response = await CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "open",
                ["path"] = adHocKb
            });

            Assert.True(response["result"]?["isError"]?.Value<bool>() == true, response.ToString());
            var payload = ParseToolPayload(response);
            Assert.Equal("KB_OPEN_ONLY_CONFIGURED", payload["error"]?["code"]?.ToString());
            Assert.Empty(payload["configuredAliases"]?.Values<string>() ?? Array.Empty<string>());
        }

        [Theory]
        [InlineData("genexus_delete_object", "{\"name\":\"P1\",\"type\":\"Procedure\"}")]
        [InlineData("genexus_data_view", "{\"action\":\"delete\",\"transaction\":\"T1\",\"dataViewName\":\"DV1\"}")]
        [InlineData("genexus_transfer", "{\"action\":\"import\",\"file\":\"x.xpz\",\"dryRun\":false}")]
        [InlineData("genexus_io", "{\"action\":\"delete_kb_objects\",\"names\":[\"P1\"]}")]
        [InlineData("genexus_io", "{\"action\":\"import_part\",\"name\":\"P1\",\"inputPath\":\"x.txt\"}")]
        [InlineData("genexus_io", "{\"action\":\"import_text_to_kb\",\"inputPath\":\"x.txt\",\"dryRun\":false}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"reorg\"}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"build_all\"}")]
        [InlineData("genexus_lifecycle", "{\"action\":\"rebuild\"}")]
        [InlineData("genexus_edit", "{\"name\":\"P1\",\"part\":\"Source\",\"content\":\"x\"}")]
        [InlineData("genexus_edit_and_build", "{\"name\":\"P1\",\"part\":\"Source\",\"content\":\"x\"}")]
        [InlineData("genexus_deploy", "{\"action\":\"deploy\"}")]
        public void EveryDestructiveOperationInElicitation_RequiresExplicitKb(string tool, string rawArgs)
        {
            var args = JObject.Parse(rawArgs);
            Assert.NotNull(Program.DescribeDestructiveOperation(tool, args));
            Assert.True(KbAccessPolicy.RequiresExplicitKbForWrite(tool, args));
        }

        public void Dispose()
        {
            Program.ClearSessionSelectedKb(_session);
            Program.IndexBootstrapTriggerForTest = null;
            for (int i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            try { Directory.Delete(_directory, true); } catch { }
        }
    }
}
