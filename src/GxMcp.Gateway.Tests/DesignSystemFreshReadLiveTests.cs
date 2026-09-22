using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Trait("Category", "LiveDesignSystemRead")]
    // LiveGatewayHarness spawns real processes, so this class belongs to the
    // process-smoke lane like every other fixture-backed live class.
    [Trait("Category", "ProcessSmoke")]
    public sealed class DesignSystemFreshReadLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness harness;
        public DesignSystemFreshReadLiveTests(LiveGatewayHarness harness) => this.harness = harness;
        public Task InitializeAsync() => harness.InitializeAsync();
        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact(requiresDesignSystemFixture: true)]
        public async Task RetainedDesignSystem_FullAndPatchPreviewsUseFreshReadWithoutSaving()
        {
            string name = Environment.GetEnvironmentVariable("GXMCP_DSO_NAME")!;
            foreach (string part in new[] { "Tokens", "Styles" })
            {
                var readArgs = new JObject { ["name"] = name, ["type"] = "DesignSystem", ["part"] = part, ["limit"] = 0 };
                var before = await Call("genexus_read", readArgs);
                string source = Field(before, "source");
                string token = Field(before, "versionToken");
                Assert.False(string.IsNullOrWhiteSpace(source), "The fixture must contain nonempty " + part);
                Assert.False(string.IsNullOrWhiteSpace(token));

                // The public read above materializes the SDK single-instance cache.
                // Both edit routes must then obtain an independent verification read.
                // Keep baseVersion: the full-mode facade checks it even in dryRun,
                // which is what exercises the fresh-read guard without saving.
                foreach (string mode in new[] { "full", "patch" })
                {
                    var edit = new JObject
                    {
                        ["name"] = name, ["type"] = "DesignSystem", ["part"] = part,
                        ["mode"] = mode, ["content"] = source, ["baseVersion"] = token,
                        ["dryRun"] = true, ["autoDeclareVariables"] = false, ["verifyMode"] = "exact"
                    };
                    if (mode == "patch")
                    {
                        edit["operation"] = "Replace";
                        edit["context"] = source;
                        edit["expectedCount"] = 1;
                    }
                    await Call("genexus_edit", edit);
                    var after = await Call("genexus_read", readArgs);
                    Assert.Equal(source, Field(after, "source"));
                    Assert.Equal(token, Field(after, "versionToken"));
                }
            }
        }

        private async Task<JObject> Call(string tool, JObject args)
        {
            var response = await harness.CallToolAsync(tool, args, 120_000);
            var payload = LiveGatewayHarness.ParseToolPayload(response);
            Assert.NotNull(payload);
            Assert.False(LiveGatewayHarness.IsToolError(response), tool + ": " +
                (payload!["error"]?["code"] ?? payload["code"])?.ToString());
            return payload!;
        }

        private static string Field(JObject payload, string name)
            => (payload[name] ?? payload["result"]?[name])?.Value<string>() ?? string.Empty;
    }
}
