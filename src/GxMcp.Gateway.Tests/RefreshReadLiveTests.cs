using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Sdk;

namespace GxMcp.Gateway.Tests
{
    [Trait("Category", "LiveRefreshRead")]
    [Trait("Category", "ProcessSmoke")]
    public sealed class RefreshReadLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness reader;
        public RefreshReadLiveTests(LiveGatewayHarness reader) => this.reader = reader;
        public Task InitializeAsync() => reader.InitializeAsync();
        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task AutoDeclaredVariableIsReportedAndIndependentlyReloaded()
        {
            AssertSyntheticFixture();
            string name = "Mcp889" + Guid.NewGuid().ToString("N").Substring(0, 10);
            bool created = false;
            bool uncertain = false;
            try
            {
                uncertain = true;
                await Call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = "Procedure" });
                created = true;
                uncertain = false;
                uncertain = true;
                var edit = await Call("genexus_edit", new JObject
                {
                    ["name"] = name, ["type"] = "Procedure", ["part"] = "Source", ["mode"] = "full",
                    ["content"] = "&McpUndeclared = 100", ["autoDeclareVariables"] = true,
                    ["requireObjectSave"] = true
                });
                uncertain = false;
                var declared = (edit["autoCreatedVariables"] ?? edit["result"]?["autoCreatedVariables"]) as JArray;
                Assert.NotNull(declared);
                Assert.Contains(declared!, item => string.Equals(item["name"]?.ToString(), "&McpUndeclared", StringComparison.OrdinalIgnoreCase));
                var variables = await Call("genexus_read", new JObject
                {
                    ["name"] = name, ["type"] = "Procedure", ["part"] = "Variables", ["limit"] = 0, ["refresh"] = true
                });
                Assert.Contains("McpUndeclared", variables.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.Equal("sdk-refresh", (variables["readFreshness"] ?? variables["result"]?["readFreshness"])?["origin"]?.ToString());
            }
            finally
            {
                if (created && !uncertain) await Delete(name);
                if (uncertain) Console.Error.WriteLine("Uncertain mutation; reconcile owned fixture before cleanup: " + name);
            }
        }

        [LiveKbFact]
        public Task LargeSourceSingularAndPluralReadsPreserveEveryPage()
        {
            AssertSyntheticFixture();
            return LargePartReadLiveAssertions.Verify(Call, "Procedure", "Source");
        }

        [LiveKbFact]
        public Task LargeEventsSingularAndPluralReadsPreserveEveryPage()
        {
            AssertSyntheticFixture();
            return LargePartReadLiveAssertions.Verify(Call, "WebPanel", "Events");
        }

        [LiveKbFact]
        public async Task SameSdkObjectCanBeIndependentlyReloadedByNameAndGuid()
        {
            AssertSyntheticFixture();
            string name = "Mcp439" + Guid.NewGuid().ToString("N").Substring(0, 10);
            bool created = false;
            try
            {
                await Call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = "Procedure" });
                created = true;
                var byName = new JObject { ["name"] = name, ["type"] = "Procedure", ["part"] = "Source", ["limit"] = 0 };
                var initial = await Call("genexus_read", byName);
                string source = Field(initial, "source");
                string version = Field(initial, "versionToken");
                var complete = await Call("genexus_read", new JObject { ["name"] = name, ["type"] = "Procedure" });
                string guid = (complete["identity"] ?? complete["result"]?["identity"])?["guid"]?.ToString() ?? "";
                Assert.True(Guid.TryParse(guid, out _));
                var byGuid = new JObject { ["guid"] = guid, ["type"] = "Procedure", ["part"] = "Source", ["limit"] = 0 };
                await Call("genexus_read", byGuid);
                foreach (var args in new[] { byName, byGuid })
                {
                    args["refresh"] = true;
                    var fresh = await Call("genexus_read", args);
                    Assert.Equal(source, Field(fresh, "source"));
                    Assert.Equal(version, Field(fresh, "versionToken"));
                    Assert.Equal("sdk-refresh", (fresh["readFreshness"] ?? fresh["result"]?["readFreshness"])?["origin"]?.ToString());
                }
                // This smoke proves SDK invalidation/eviction and same-identity reload
                // only. It does NOT prove observation of an IDE Save/pattern generation.
            }
            finally
            {
                if (created) await Delete(name);
            }
        }

        [LiveKbFact]
        public async Task SharedWorkerClientSaveRefreshesAnotherGatewaysNameAndGuidReads()
        {
            AssertSyntheticFixture();
            string configPath = Environment.GetEnvironmentVariable("GX_CONFIG_PATH") ?? "";
            var config = File.Exists(configPath) ? JObject.Parse(File.ReadAllText(configPath)) : new JObject();
            if (config["Server"]?["WorkerSharingMode"]?.ToString() != "shared-host")
                throw SkipException.ForSkip("This two-Gateway cache smoke requires shared-host configuration; it does not bypass isolated Worker ownership.");
            string name = "Mcp439" + Guid.NewGuid().ToString("N").Substring(0, 10);
            bool created = false;
            try
            {
                await Call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = "Procedure" });
                created = true;
                var byName = new JObject { ["name"] = name, ["type"] = "Procedure", ["part"] = "Source", ["limit"] = 0 };
                var initial = await Call("genexus_read", byName);
                string original = Field(initial, "source");
                var complete = await Call("genexus_read", new JObject { ["name"] = name, ["type"] = "Procedure" });
                string guid = (complete["identity"] ?? complete["result"]?["identity"])?["guid"]?.ToString() ?? "";
                Assert.True(Guid.TryParse(guid, out _));
                var byGuid = new JObject { ["guid"] = guid, ["type"] = "Procedure", ["part"] = "Source", ["limit"] = 0 };
                await Call("genexus_read", byGuid);
                string? logDir = Environment.GetEnvironmentVariable("GXMCP_LOG_DIR");
                string? summary = Environment.GetEnvironmentVariable("GXMCP_LIVE_SUMMARY_PATH");
                LiveGatewayHarness second;
                try
                {
                    Environment.SetEnvironmentVariable("GXMCP_LOG_DIR", logDir + "-writer");
                    Environment.SetEnvironmentVariable("GXMCP_LIVE_SUMMARY_PATH", summary + ".writer.json");
                    second = new LiveGatewayHarness();
                }
                finally
                {
                    Environment.SetEnvironmentVariable("GXMCP_LOG_DIR", logDir);
                    Environment.SetEnvironmentVariable("GXMCP_LIVE_SUMMARY_PATH", summary);
                }
                using var writer = second;
                await writer.InitializeAsync();
                await CallOn(writer, "genexus_kb", new JObject { ["action"] = "open", ["path"] = Environment.GetEnvironmentVariable("GXMCP_TEST_KB") });
                var readerWho = await Call("genexus_whoami", new JObject());
                var writerWho = await CallOn(writer, "genexus_whoami", new JObject());
                var readerPid = readerWho.SelectTokens("$..workerPid").FirstOrDefault(p => p.Type == JTokenType.Integer);
                var writerPid = writerWho.SelectTokens("$..workerPid").FirstOrDefault(p => p.Type == JTokenType.Integer);
                Assert.NotNull(readerPid);
                Assert.Equal(readerPid?.ToString(), writerPid?.ToString());
                foreach (string source in new[] { original + "\n// external Gateway cache probe\n", original })
                {
                    await CallOn(writer, "genexus_edit", new JObject
                    {
                        ["name"] = name, ["type"] = "Procedure", ["part"] = "Source", ["mode"] = "full",
                        ["content"] = source, ["verifyMode"] = "exact", ["autoDeclareVariables"] = false, ["requireObjectSave"] = true
                    });
                    foreach (var identity in new[] { byName, byGuid })
                    {
                        var args = (JObject)identity.DeepClone();
                        args["refresh"] = true;
                        var fresh = await Call("genexus_read", args);
                        Assert.Equal(source, Field(fresh, "source"));
                        Assert.False(string.IsNullOrWhiteSpace(Field(fresh, "versionToken")));
                    }
                }
                // Client B shares the SDK Worker with A. This proves Gateway cache
                // isolation/refresh, NOT an external IDE or separate-SDK Save.
            }
            finally { if (created) await Delete(name); }
        }

        [LiveKbFact]
        public async Task ReportTextModePersistsWhenTheSdkExposesTheLayoutBagEntry()
        {
            AssertSyntheticFixture();
            string name = "Mcp407" + Guid.NewGuid().ToString("N").Substring(0, 10);
            bool created = false;
            try
            {
                await Call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = "Procedure" });
                created = true;
                var read = new JObject { ["action"] = "get", ["name"] = name, ["propertyName"] = "RPT_TEXT_MODE", ["projection"] = "standard" };
                var properties = await Call("genexus_properties", read);
                if (properties["result"]?["values"]?["RPT_TEXT_MODE"] == null)
                    throw SkipException.ForSkip("This newly created report has no RPT_TEXT_MODE Layout bag entry; a migrated synthetic GX8 report fixture is required.");
                foreach (string value in new[] { "True", "False" })
                {
                    var write = await Call("genexus_properties", new JObject
                    {
                        ["action"] = "set", ["name"] = name, ["propertyName"] = "RPT_TEXT_MODE", ["value"] = value
                    });
                    Assert.True(write["result"]?["persistedVerified"]?.Value<bool>() == true);
                    var after = await Call("genexus_properties", read);
                    Assert.Equal(value, after["result"]?["values"]?["RPT_TEXT_MODE"]?.ToString());
                    var layout = await Call("genexus_read", new JObject { ["name"] = name, ["part"] = "Layout", ["limit"] = 0, ["refresh"] = true });
                    Assert.Contains("RPT_TEXT_MODE=\"" + value + "\"", Field(layout, "source"));
                }
            }
            finally
            {
                if (created) await Delete(name);
            }
        }

        private static void AssertSyntheticFixture()
        {
            string manifest = Environment.GetEnvironmentVariable("GXMCP_TEST_FIXTURE") ?? "";
            Assert.True(File.Exists(manifest), "A verified synthetic/disposable fixture manifest is required.");
            var fixture = JObject.Parse(File.ReadAllText(manifest));
            Assert.True(fixture["synthetic"]?.Value<bool>() == true && fixture["disposable"]?.Value<bool>() == true
                && fixture["isolation"]?["verified"]?.Value<bool>() == true);
            Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("GXMCP_TEST_KB")!).TrimEnd('\\', '/'),
                Path.GetFullPath(fixture["kbPath"]!.Value<string>()!).TrimEnd('\\', '/'), ignoreCase: true);
        }
        private Task<JObject> Delete(string name) => Call("genexus_delete_object", new JObject { ["name"] = name, ["type"] = "Procedure", ["confirm"] = true });
        private static string Field(JObject payload, string name) => (payload[name] ?? payload["result"]?[name])?.ToString() ?? "";
        private Task<JObject> Call(string tool, JObject args) => CallOn(reader, tool, args);
        private static async Task<JObject> CallOn(LiveGatewayHarness harness, string tool, JObject args)
        {
            var response = await harness.CallToolAsync(tool, args, 180_000);
            Assert.True(response["error"] == null, tool + ": " + response["error"]?.ToString(Newtonsoft.Json.Formatting.None));
            var payload = LiveGatewayHarness.ParseToolPayload(response);
            Assert.Null(payload?["operationId"]);
            Assert.Null(payload?["job_id"]);
            Assert.False(LiveGatewayHarness.IsToolError(response), tool + ": " + payload?.ToString(Newtonsoft.Json.Formatting.None));
            Assert.NotNull(payload);
            return payload!;
        }
    }
}
