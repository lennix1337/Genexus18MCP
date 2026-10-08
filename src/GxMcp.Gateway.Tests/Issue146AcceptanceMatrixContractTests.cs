using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Final issue #146 acceptance gates that are executable without a GeneXus SDK,
    /// licensed installation, or live KB. SDK-touching behavior remains in the
    /// separately gated live suite.
    /// </summary>
    public sealed class Issue146AcceptanceMatrixContractTests
    {
        [Theory]
        [InlineData("stdio-isolated", "strict", 0, true)]
        [InlineData("stdio-isolated", "legacy", 0, true)]
        [InlineData("http-shared", "strict", 5517, false)]
        [InlineData("http-shared", "legacy", 5518, false)]
        public void StrictConfigurationMatrix_AcceptsOnlyTheDocumentedTransportPair(
            string gatewayMode, string resolutionPolicy, int port, bool stdio)
        {
            string root = CreateTempDirectory();
            string path = Path.Combine(root, "config.json");
            try
            {
                File.WriteAllText(path, $@"{{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""{gatewayMode}"",
  ""GeneXus"": {{ ""InstallationPath"": ""C:\\\\GeneXus18"", ""WorkerExecutable"": ""C:\\\\worker.exe"" }},
  ""Server"": {{ ""HttpPort"": {port}, ""McpStdio"": {(stdio ? "true" : "false")} }},
  ""Environment"": {{ ""ResolutionPolicy"": ""{resolutionPolicy}"" }}
}}");

                var config = ParseConfig(path);

                Assert.Equal(2, config.ConfigSchemaVersion);
                Assert.Equal(gatewayMode, config.GatewayMode);
                Assert.Equal(resolutionPolicy, config.Environment!.ResolutionPolicy);
                Assert.Equal(port, config.Server!.HttpPort);
                Assert.Equal(stdio, config.Server.McpStdio);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Theory]
        [InlineData("stdio-isolated", 5519, false)]
        [InlineData("http-shared", 0, true)]
        public void StrictConfigurationMatrix_RejectsHybridTransport(string gatewayMode, int port, bool stdio)
        {
            string root = CreateTempDirectory();
            string path = Path.Combine(root, "config.json");
            try
            {
                File.WriteAllText(path, $@"{{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""{gatewayMode}"",
  ""GeneXus"": {{ ""InstallationPath"": ""C:\\\\GeneXus18"", ""WorkerExecutable"": ""C:\\\\worker.exe"" }},
  ""Server"": {{ ""HttpPort"": {port}, ""McpStdio"": {(stdio ? "true" : "false")} }},
  ""Environment"": {{ ""ResolutionPolicy"": ""strict"" }}
}}");

                var error = Assert.Throws<InvalidDataException>(() => ParseConfig(path));
                Assert.Contains("requires", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void LocalFriendlyAndHardenedPostures_AreExplicitlyDocumentedWithoutInventingSdkAuthorization()
        {
            string contract = File.ReadAllText(FindRepositoryFile("docs/kb-isolation-contract.md"));
            string inventory = File.ReadAllText(FindRepositoryFile("docs/mcp_capabilities_inventory.md"));

            Assert.Contains("local-friendly", contract, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hardened", contract, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("explicit absolute path", contract, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("local-friendly", inventory, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hardened", inventory, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("explicit valid local path", inventory, StringComparison.OrdinalIgnoreCase);

            // The contract must keep hardened controls as deployment posture, not as
            // an invented SDK/licensing check in the neutral client configuration.
            Assert.Contains("deployment posture", contract, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not the default", contract, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AcceptanceMatrix_ReferencesSeparateLeaseGenerationSideChannelCacheAndSmokeContracts()
        {
            string tests = FindRepositoryDirectory("src", "GxMcp.Gateway.Tests");
            string[] requiredContracts =
            {
                "KbUseLeaseRegistryTests.cs",
                "OperationalStateIsolationTests.cs",
                "OwnershipFenceSideChannelTests.cs",
                "StateScopedCacheIsolationTests.cs",
                "McpSmokeScriptContractTests.cs"
            };

            Assert.All(requiredContracts, file =>
                Assert.True(File.Exists(Path.Combine(tests, file)), $"Missing contract test: {file}"));
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

        private static string FindRepositoryFile(string relativePath)
        {
            string directory = AppContext.BaseDirectory;
            for (int i = 0; i < 12; i++)
            {
                string candidate = Path.Combine(directory, relativePath);
                if (File.Exists(candidate)) return candidate;
                directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
                if (directory.Length == 0) break;
            }
            throw new FileNotFoundException($"Repository file not found: {relativePath}");
        }

        private static string FindRepositoryDirectory(params string[] parts)
        {
            string directory = AppContext.BaseDirectory;
            for (int i = 0; i < 12; i++)
            {
                string candidate = Path.Combine(new[] { directory }.Concat(parts).ToArray());
                if (Directory.Exists(candidate)) return candidate;
                directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
                if (directory.Length == 0) break;
            }
            throw new DirectoryNotFoundException($"Repository directory not found: {Path.Combine(parts)}");
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-issue146-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }

    /// <summary>Separate process smoke for the isolated stdio transport.</summary>
    [Trait("Category", "ProcessSmoke")]
    public sealed class Issue146StdioSmokeContractTests
    {
        [Fact]
        public async Task StdioIsolated_InitializeAndToolsList_CompleteWithinExplicitTimeouts()
        {
            if (!OperatingSystem.IsWindows()) return;

            string? gateway = FindGatewayExe();
            if (gateway == null) return; // Build/live artifact gate, not an SDK claim.

            string root = Path.Combine(Path.GetTempPath(), "gxmcp-stdio-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true },
  ""Environment"": { ""ResolutionPolicy"": ""strict"" }
}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = gateway,
                    WorkingDirectory = Path.GetDirectoryName(gateway)!,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.Environment["GX_CONFIG_PATH"] = config;
            process.Start();
            try
            {
                await SendAndAwaitAsync(process, 1, "initialize", "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"issue146-contract\",\"version\":\"1\"}}", 30_000);
                await SendAndAwaitAsync(process, 2, "tools/list", "{}", 30_000);
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { }
                if (!process.WaitForExit(5_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    process.WaitForExit(5_000);
                }
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static async Task<JObject> SendAndAwaitAsync(Process process, int id, string method, string parameters, int timeoutMs)
        {
            process.StandardInput.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{parameters}}}");
            await process.StandardInput.FlushAsync();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(deadline - DateTime.UtcNow);
                if (line == null) break;
                if (!line.StartsWith("{")) continue;
                var message = JObject.Parse(line);
                if (message["id"]?.Value<int>() == id)
                {
                    Assert.Null(message["error"]);
                    return message;
                }
            }
            throw new TimeoutException($"stdio {method} did not answer within {timeoutMs}ms");
        }

        private static string? FindGatewayExe()
        {
            string? configured = Environment.GetEnvironmentVariable("GXMCP_LIVE_GATEWAY_EXE");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) || !File.Exists(configured))
                    throw new InvalidOperationException(
                        $"GXMCP_LIVE_GATEWAY_EXE must point to an existing absolute executable: {configured}");
                return Path.GetFullPath(configured);
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                foreach (string relative in new[]
                {
                    Path.Combine("src", "GxMcp.Gateway", "bin", "Debug", "net10.0-windows", "GxMcp.Gateway.exe"),
                    Path.Combine("src", "GxMcp.Gateway", "bin", "Release", "net10.0-windows", "GxMcp.Gateway.exe"),
                    Path.Combine("publish", "GxMcp.Gateway.exe")
                })
                {
                    string candidate = Path.Combine(directory.FullName, relative);
                    if (File.Exists(candidate)) return candidate;
                }
                directory = directory.Parent;
            }
            return null;
        }
    }
}
