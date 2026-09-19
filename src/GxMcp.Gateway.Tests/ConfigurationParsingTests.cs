using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ConfigurationParsingTests
    {
        [Fact]
        public void ParseConfig_LegacyKbPath_MigratesToKbsAndDefaultKb()
        {
            // issue #28 item 6: legacy KBPath is migrated ONLY when it points at a real KB
            // (a dir containing a .gxw / KnowledgeBase.Connection). Point KBPath at a temp
            // dir carrying a .gxw sentinel so the migration path is exercised.
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string kbDir = Path.Combine(tempDir, "LegacyDemo");
            Directory.CreateDirectory(kbDir);
            File.WriteAllText(Path.Combine(kbDir, "LegacyDemo.gxw"), "");
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                var json = "{ \"Environment\": { \"ResolutionPolicy\": \"legacy\", \"KBPath\": " + System.Text.Json.JsonSerializer.Serialize(kbDir) + " } }";
                File.WriteAllText(configPath, json);

                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Environment);
                Assert.NotNull(cfg.Environment!.KBs);
                var single = Assert.Single(cfg.Environment.KBs);
                Assert.Equal("legacydemo", single.Alias);
                Assert.Equal(kbDir, single.Path);
                Assert.Equal("legacydemo", cfg.Environment.DefaultKb);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ClassicDatKbMarkers_AreRecognizedAsPlausibleLegacyKb()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-classic-kb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                foreach (string marker in new[] { "DATA001", "GXSPC001", "ATTRIBUT.DAT", "ATT.XPW" })
                    File.WriteAllText(Path.Combine(tempDir, marker), "classic");

                Assert.True(Configuration.LooksLikeKb(tempDir));
                Assert.True(Configuration.IsPlausibleKbPath(tempDir));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_KbPath_StrictMode_DoesNotAutoPromoteToDefaultKb()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string kbDir = Path.Combine(tempDir, "StrictDemo");
            Directory.CreateDirectory(kbDir);
            File.WriteAllText(Path.Combine(kbDir, "StrictDemo.gxw"), "");
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                var json = "{ \"Environment\": { \"ResolutionPolicy\": \"strict\", \"KBPath\": " + System.Text.Json.JsonSerializer.Serialize(kbDir) + " } }";
                File.WriteAllText(configPath, json);

                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Environment);
                Assert.NotNull(cfg.Environment!.KBs);
                var single = Assert.Single(cfg.Environment.KBs);
                Assert.Equal("strictdemo", single.Alias);
                Assert.Null(cfg.Environment.DefaultKb);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_StrictModeAcceptsPerKbLegacyDriverRouting()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-strict-legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true },
  ""Environment"": {
    ""ResolutionPolicy"": ""strict"",
    ""KBs"": {
      ""SECT80"": {
        ""Path"": ""D:\\GX80\\SECT"",
        ""Driver"": ""com-gxpublic"",
        ""InstallationPath"": ""C:\\Program Files (x86)\\ARTech\\GeneXus\\gxw80"",
        ""Major"": ""8""
      }
    }
  }
}");

                var cfg = ParseConfig(configPath);
                var entry = Assert.Single(cfg.Environment!.KBs);
                Assert.Equal("SECT80", entry.Alias);
                Assert.Equal("com-gxpublic", entry.Driver);
                Assert.Equal("8", entry.Major);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_AcceptsNonSecretReadOnlyDataStoreAlias()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-alias-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true },
  ""Environment"": {
    ""ResolutionPolicy"": ""strict"",
    ""DataStoreAliases"": {
      ""production"": {
        ""dataStore"": ""Default"",
        ""family"": ""sqlserver"",
        ""server"": ""SERVER\\\\INSTANCE"",
        ""database"": ""APP_PROD"",
        ""schema"": ""dbo"",
        ""integratedSecurity"": true
      }
    }
  }
}");

                var cfg = ParseConfig(configPath);
                var alias = Assert.Single(cfg.Environment!.DataStoreAliases!);
                Assert.Equal("Default", alias.Value.DataStore);
                Assert.Equal("SERVER\\\\INSTANCE", alias.Value.Server);
                Assert.True(alias.Value.IntegratedSecurity == true);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_StrictModeRejectsNonBooleanAliasIntegratedSecurity()
            => AssertStrictAliasRejected(@"{ ""dataStore"": ""Default"", ""integratedSecurity"": ""yes"" }");

        [Fact]
        public void ParseConfig_StrictModeRejectsUnknownAliasMembers()
            => AssertStrictAliasRejected(@"{ ""dataStore"": ""Default"", ""unexpected"": true }");

        [Fact]
        public void ParseConfig_PlaceholderKbPath_IsNotMigrated()
        {
            // issue #28 item 6: the shipped fallback config carries a placeholder KBPath
            // (an empty scaffold with no .gxw / KnowledgeBase.Connection). It must NOT be
            // migrated into a phantom DefaultKb that auto-opens alongside the real KB.
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string placeholderDir = Path.Combine(tempDir, "YourKB"); // exists but no .gxw
            Directory.CreateDirectory(placeholderDir);
            string missingDir = Path.Combine(tempDir, "DoesNotExist");
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                foreach (var kbPath in new[] { placeholderDir, missingDir })
                {
                    var json = "{ \"Environment\": { \"KBPath\": " + System.Text.Json.JsonSerializer.Serialize(kbPath) + " } }";
                    File.WriteAllText(configPath, json);

                    var cfg = ParseConfig(configPath);

                    Assert.NotNull(cfg.Environment);
                    // No KBs synthesised, no phantom DefaultKb.
                    Assert.True(cfg.Environment!.KBs == null || cfg.Environment.KBs.Count == 0,
                        $"Expected no migrated KBs for placeholder '{kbPath}'");
                    Assert.True(string.IsNullOrEmpty(cfg.Environment.DefaultKb),
                        $"Expected no DefaultKb for placeholder '{kbPath}'");
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_AppliesEnvOverrides_AndPromotesActiveKb_UnderLegacyPolicy()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            string? oldPort = Environment.GetEnvironmentVariable("GX_MCP_PORT");
            string? oldStdio = Environment.GetEnvironmentVariable("GX_MCP_STDIO");
            try
            {
                var json = @"{
  ""Server"": {
    ""HttpPort"": 5000,
    ""McpStdio"": true
  },
  ""Environment"": {
    ""ResolutionPolicy"": ""legacy"",
    ""DefaultKb"": """",
    ""ActiveKb"": ""from_cli"",
    ""KBs"": {
      ""from_cli"": ""C:/KBs/FromCli""
    }
  }
}";
                File.WriteAllText(configPath, json);

                Environment.SetEnvironmentVariable("GX_MCP_PORT", "7711");
                Environment.SetEnvironmentVariable("GX_MCP_STDIO", "false");
                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Server);
                Assert.Equal(7711, cfg.Server!.HttpPort);
                Assert.False(cfg.Server.McpStdio);

                Assert.NotNull(cfg.Environment);
                Assert.Equal("from_cli", cfg.Environment!.DefaultKb);
                var single = Assert.Single(cfg.Environment.KBs);
                Assert.Equal("from_cli", single.Alias);
                Assert.Equal("C:/KBs/FromCli", single.Path);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_MCP_PORT", oldPort);
                Environment.SetEnvironmentVariable("GX_MCP_STDIO", oldStdio);
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_SingleDeclaredKb_WithoutDefault_AutoPromotesToDefaultKb_UnderLegacyPolicy()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                var json = @"{
  ""Environment"": {
    ""ResolutionPolicy"": ""legacy"",
    ""KBs"": {
      ""mykb"": ""C:/KBs/MyKb""
    }
  }
}";
                File.WriteAllText(configPath, json);
                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Environment);
                Assert.Equal("mykb", cfg.Environment!.DefaultKb);
                var single = Assert.Single(cfg.Environment.KBs);
                Assert.Equal("mykb", single.Alias);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_SingleDeclaredKb_WithoutDefault_DoesNotAutoPromoteToDefaultKb_UnderStrictMode()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                var json = @"{
  ""Environment"": {
    ""ResolutionPolicy"": ""strict"",
    ""KBs"": {
      ""mykb"": ""C:/KBs/MyKb""
    }
  }
}";
                File.WriteAllText(configPath, json);
                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Environment);
                Assert.Null(cfg.Environment!.DefaultKb);
                var single = Assert.Single(cfg.Environment.KBs);
                Assert.Equal("mykb", single.Alias);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_StrictV2_AcceptsNeutralStdioFixture()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true, ""BindAddress"": ""127.0.0.1"" },
  ""Environment"": { ""ResolutionPolicy"": ""strict"" }
}");

                var cfg = ParseConfig(configPath);

                Assert.Equal(2, cfg.ConfigSchemaVersion);
                Assert.Equal("stdio-isolated", cfg.GatewayMode);
                Assert.Equal("strict", cfg.Environment!.ResolutionPolicy);
                Assert.Empty(cfg.Environment.KBs);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_StrictV2_AcceptsOptInSharedWorkerHost()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true, ""WorkerSharingMode"": ""shared-host"" },
  ""Environment"": { ""ResolutionPolicy"": ""strict"" }
}");

                var cfg = ParseConfig(configPath);

                Assert.Equal("shared-host", cfg.Server!.WorkerSharingMode);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Theory]
        [InlineData("invalid")]
        [InlineData("shared-host", "http-shared")]
        public void ParseConfig_StrictV2_RejectsInvalidSharedWorkerMode(string workerSharingMode, string gatewayMode = "stdio-isolated")
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                File.WriteAllText(configPath, $@"{{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""{gatewayMode}"",
  ""GeneXus"": {{ ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" }},
  ""Server"": {{ ""HttpPort"": {(gatewayMode == "http-shared" ? 5000 : 0)}, ""McpStdio"": {(gatewayMode == "http-shared" ? "false" : "true")}, ""WorkerSharingMode"": ""{workerSharingMode}"" }},
  ""Environment"": {{ ""ResolutionPolicy"": ""strict"" }}
}}");

                var error = Assert.Throws<InvalidDataException>(() => ParseConfig(configPath));

                Assert.Contains("WorkerSharingMode", error.Message);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_StrictV2_AcceptsExplicitKbPathFields()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true },
  ""Environment"": { ""ResolutionPolicy"": ""strict"", ""KBPath"": ""C:\\KBs\\Demo"" }
}");

                var cfg = ParseConfig(configPath);
                Assert.Equal(@"C:\KBs\Demo", cfg.Environment!.KBPath);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_StrictV2_RejectsConflictingTransportEnvironment()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            string? oldPort = Environment.GetEnvironmentVariable("GX_MCP_PORT");
            try
            {
                File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true },
  ""Environment"": { ""ResolutionPolicy"": ""strict"" }
}");
                Environment.SetEnvironmentVariable("GX_MCP_PORT", "5000");

                var error = Assert.Throws<InvalidDataException>(() => ParseConfig(configPath));
                Assert.Contains("GX_MCP_PORT conflicts", error.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_MCP_PORT", oldPort);
                TryDeleteDirectory(tempDir);
            }
        }

        // Environment matrix documented in Configuration.cs:
        // structural overrides (shared gateway/profile/response shape) are rejected
        // by strict config; GX_CONFIG_PATH selects the file, GXMCP_HTTP_TOKEN is a
        // secret, and diagnostics/operational switches do not alter Configuration.
        [Theory]
        [InlineData("GXMCP_SHARED_GATEWAY")]
        [InlineData("GX_MCP_SHARED_GATEWAY")]
        [InlineData("GXMCP_PROFILE")]
        [InlineData("GXMCP_NO_STRUCTURED_CONTENT")]
        [InlineData("GXMCP_EMIT_STRUCTURED_CONTENT")]
        [InlineData("GXMCP_TERSE")]
        public void ParseConfig_StrictV2_RejectsStructuralEnvironmentOverride(string variable)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            string? oldValue = Environment.GetEnvironmentVariable(variable);
            try
            {
                File.WriteAllText(configPath, StrictStdioJson());
                Environment.SetEnvironmentVariable(variable, "1");

                var error = Assert.Throws<InvalidDataException>(() => ParseConfig(configPath));
                Assert.Contains(variable, error.Message);
                Assert.Contains("not permitted", error.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, oldValue);
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_StrictV2_RejectsInvalidTypedMemberWithoutPublishingPartialConfig()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true, ""AllowedOrigins"": 17 },
  ""Environment"": { ""ResolutionPolicy"": ""strict"" }
}");

                var error = Assert.Throws<InvalidDataException>(() => ParseConfig(configPath));
                Assert.Contains("Server.AllowedOrigins", error.Message);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_StrictV2_AcceptsArtifactOutputDirectory()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                string json = StrictStdioJson().Replace(
                    "\"Server\": { \"HttpPort\": 0, \"McpStdio\": true }",
                    "\"Server\": { \"HttpPort\": 0, \"McpStdio\": true, \"ArtifactOutputDirectory\": \"C:\\\\Artifacts\" }");
                File.WriteAllText(configPath, json);

                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Server);
                Assert.Equal(@"C:\Artifacts", cfg.Server!.ArtifactOutputDirectory);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void StdioEof_KeepsOnlyLegacyHttpMasterAlive()
        {
            Assert.False(Program.ShouldKeepStdioAliveForLegacyMaster(new Configuration
            {
                Server = new ServerConfig { McpStdio = true, HttpPort = 5000 }
            }));
            Assert.False(Program.ShouldKeepStdioAliveForLegacyMaster(new Configuration
            {
                GatewayMode = "stdio-isolated",
                Server = new ServerConfig { TransportMode = "stdio-isolated", HttpPort = 0 }
            }));
            Assert.False(Program.ShouldKeepStdioAliveForLegacyMaster(new Configuration
            {
                GatewayMode = "stdio-isolated",
                Server = new ServerConfig { TransportMode = "legacy", HttpPort = 5000 }
            }));
            Assert.True(Program.ShouldKeepStdioAliveForLegacyMaster(new Configuration
            {
                GatewayMode = "legacy",
                Server = new ServerConfig { TransportMode = "legacy", HttpPort = 5000 }
            }));
            Assert.False(Program.ShouldKeepStdioAliveForLegacyMaster(new Configuration
            {
                GatewayMode = "legacy",
                Server = new ServerConfig { TransportMode = "legacy", HttpPort = 0 }
            }));
        }

        private static string StrictStdioJson()
        {
            return @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true },
  ""Environment"": { ""ResolutionPolicy"": ""strict"" }
}";
        }

        private static Configuration ParseConfig(string path)
        {
            var method = typeof(Configuration).GetMethod("ParseConfig", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            try
            {
                var cfg = method!.Invoke(null, new object[] { path }) as Configuration;
                Assert.NotNull(cfg);
                return cfg!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private static void AssertStrictAliasRejected(string aliasDefinition)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-alias-invalid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                File.WriteAllText(configPath, @"{
  ""ConfigSchemaVersion"": 2,
  ""GatewayMode"": ""stdio-isolated"",
  ""GeneXus"": { ""InstallationPath"": ""C:\\GeneXus18"", ""WorkerExecutable"": ""C:\\worker.exe"" },
  ""Server"": { ""HttpPort"": 0, ""McpStdio"": true },
  ""Environment"": {
    ""ResolutionPolicy"": ""strict"",
    ""DataStoreAliases"": { ""production"": " + aliasDefinition + @" }
  }
}");

                var error = Assert.Throws<InvalidDataException>(() => ParseConfig(configPath));
                Assert.Contains("DataStoreAliases", error.Message);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }
    }
}
