using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Trait("Category", "ProcessSmoke")]
    public sealed class TransferImportLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness gateway;
        public TransferImportLiveTests(LiveGatewayHarness gateway) => this.gateway = gateway;
        public Task InitializeAsync() => gateway.InitializeAsync();
        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task RefusedImportDoesNotCreateProcedureStub()
        {
            string manifestPath = Environment.GetEnvironmentVariable("GXMCP_TEST_FIXTURE") ?? "";
            Assert.True(File.Exists(manifestPath), "A verified disposable fixture manifest is required.");
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            Assert.True(manifest["synthetic"]?.Value<bool>() == true
                && manifest["disposable"]?.Value<bool>() == true
                && manifest["isolation"]?["verified"]?.Value<bool>() == true);
            Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("GXMCP_TEST_KB")!).TrimEnd('\\', '/'),
                Path.GetFullPath(manifest["kbPath"]!.Value<string>()!).TrimEnd('\\', '/'), ignoreCase: true);

            string name = "Mcp886" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string path = Path.Combine(Path.GetTempPath(), name + ".xpz");
            try
            {
                await AssertMissing(name);
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("export.xml").Open()))
                {
                    // A Procedure cannot contain a WebForm. Fidelity inspection must refuse
                    // before SDK import preparation can construct a destination object.
                    await writer.WriteAsync("<ExportFile><Objects><Object name='" + name
                        + "' type='84a12160-f59b-4ad7-a683-ea4481ac23e9'>"
                        + "<Part type='d24a58ad-57ba-41b7-9e6e-eaca3543c778'>"
                        + "<Source><![CDATA[<GxMultiForm />]]></Source></Part>"
                        + "</Object></Objects></ExportFile>");
                }

                var response = await gateway.CallToolAsync("genexus_transfer", new JObject
                {
                    ["action"] = "import", ["file"] = path, ["dryRun"] = false, ["confirm"] = true
                });
                // Observe destination state even if the import returned an unexpected code.
                await AssertMissing(name);
                Assert.True(LiveGatewayHarness.IsToolError(response));
                var payload = LiveGatewayHarness.ParseToolPayload(response);
                Assert.NotNull(payload);
                Assert.Equal("TransferImportVerificationUnavailable", payload!["code"]?.ToString());
                Assert.Equal("unchanged", payload["persistedState"]?.ToString());
                Assert.Contains("unexpected WebForm part", payload["message"]?.ToString() ?? "");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private async Task AssertMissing(string name)
        {
            var response = await gateway.CallToolAsync("genexus_read", new JObject
            {
                ["name"] = name, ["type"] = "Procedure", ["part"] = "Source", ["refresh"] = true
            });
            Assert.True(LiveGatewayHarness.IsToolError(response));
            var payload = LiveGatewayHarness.ParseToolPayload(response);
            Assert.NotNull(payload);
            Assert.Equal("ObjectNotFound", payload!["code"]?.ToString());
        }
    }
}
