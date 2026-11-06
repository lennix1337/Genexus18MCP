using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    [Trait("Category", "ProcessSmoke")]
    [Collection("SharedHostQueueProcessSmokeSerial")]
    public sealed class SharedHostRefusalStdioProcessSmokeTests
    {
        [Fact]
        public async Task LiveHostIdentityMismatch_ReachesStdioClientAsTypedRefusal()
        {
            string exe = SharedHostQueueProcessSmokeTests.LocateTestHost() ?? throw new InvalidOperationException("Build the shared host TestHost first.");
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-refusal-smoke-" + Guid.NewGuid().ToString("N"));
            string registry = Path.Combine(root, "registry");
            string kb = Path.Combine(root, "fake-kb");
            string installation = Path.Combine(root, "install");
            Directory.CreateDirectory(kb);
            Directory.CreateDirectory(installation);
            File.WriteAllText(Path.Combine(kb, "uvs.gxw"), "");
            var identity = SharedWorkerIdentity.Create(exe, kb, installation, "sdk", "18");
            var stderr = new StringBuilder();
            Process? broker = null;
            try
            {
                broker = SharedHostQueueProcessSmokeTests.StartBroker(exe, identity, kb, installation, registry, stderr);
                await SharedHostQueueProcessSmokeTests.WaitForRecordAsync(SharedWorkerRegistry.GetRecordPathV2(identity.KbKey, registry), broker, stderr);
                var config = new Configuration
                {
                    Server = new ServerConfig { WorkerSharingMode = "shared-host" },
                    GeneXus = new GeneXusConfig { WorkerExecutable = exe, InstallationPath = installation },
                    Environment = new EnvironmentConfig { ResolutionPolicy = "strict" }
                };
                using var state = Program.ConfigureRouteStateForTest(config, Path.Combine(root, "config.json"));
                SharedWorkerHostLauncher.RecoveryContextForTest = new SharedWorkerRecoveryContext { RegistryRoot = registry, OwnershipRegistryRoot = Path.Combine(root, "ownership") };
                var request = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 42, ["method"] = "tools/call",
                    ["params"] = new JObject { ["name"] = "genexus_kb", ["arguments"] = new JObject { ["action"] = "open", ["path"] = kb } }
                };
                JObject? response = await Program.ProcessStdioLine(request.ToString(Newtonsoft.Json.Formatting.None));
                Assert.NotNull(response);
                Assert.Equal(42, (int)response!["id"]!);
                Assert.True((bool)response["result"]!["isError"]!);
                var payload = JObject.Parse((string)response["result"]!["content"]![0]!["text"]!);
                Assert.Equal("SHARED_WORKER_IDENTITY_MISMATCH", (string?)payload["code"]);
            }
            finally
            {
                SharedWorkerHostLauncher.RecoveryContextForTest = null;
                if (broker != null)
                {
                    if (!broker.HasExited) broker.Kill(entireProcessTree: true);
                    broker.WaitForExit(5000);
                    broker.Dispose();
                }
                Directory.Delete(root, true);
            }
        }
    }
}
