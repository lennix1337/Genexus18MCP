using System;
using System.IO;
using GxMcp.Gateway;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // #965: genexus_doctor kb=<GX9 alias> reported the gateway's GX18 installation in its
    // geneXus block, so the target worker's context could not be told apart from the global one.
    public class DoctorTargetContextTests
    {
        private static JObject GatewayBlock() => new JObject
        {
            ["installationPath"] = @"C:\Program Files (x86)\GeneXus\GeneXus18",
            ["version"] = "18.0.11",
            ["matchedMajor"] = "18",
            ["sdkCompatibility"] = new JObject { ["status"] = "compatible" },
            ["catalog"] = new JObject()
        };

        [Fact]
        public void GeneXusBlock_DescribesTheTargetInstallation_AndKeepsTheGatewayAsDefault()
        {
            var gx9 = new KbHandle("kb-gx9", @"C:\KBs\Legacy", @"C:\GeneXus9", "com-gxpublic", "9");

            JObject block = Program.BuildDoctorGeneXusBlock(GatewayBlock(), gx9);

            Assert.Equal("kb", block["source"]!.ToString());
            Assert.Equal(@"C:\GeneXus9", block["installationPath"]!.ToString());
            Assert.Equal("9", block["major"]!.ToString());
            Assert.Equal("com-gxpublic", block["driver"]!.ToString());
            Assert.Equal("not_applicable", block["sdkCompatibility"]!["status"]!.ToString());
            Assert.True((bool)block["versionMatches"]!);
            Assert.Equal(@"C:\Program Files (x86)\GeneXus\GeneXus18",
                block["gatewayDefault"]!["installationPath"]!.ToString());
        }

        [Fact]
        public void GeneXusBlock_IsTheGatewayBlock_WhenTheTargetSharesItsInstallation()
        {
            var gx18 = new KbHandle("kb18", @"C:\KBs\Main", @"C:\Program Files (x86)\GeneXus\GeneXus18\", null, null);

            JObject block = Program.BuildDoctorGeneXusBlock(GatewayBlock(), gx18);

            Assert.Equal("gateway", block["source"]!.ToString());
            Assert.Null(block["gatewayDefault"]);
            Assert.Equal("compatible", block["sdkCompatibility"]!["status"]!.ToString());
        }

        [Fact]
        public void StartingHealth_IsStalledAfterTheLimit_AndTellsHowToRecover()
        {
            var spawned = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

            JObject early = Program.BuildStartingHealth("kb-gx9", spawned, "Starting", null, spawned.AddSeconds(10));
            JObject stalled = Program.BuildStartingHealth("kb-gx9", spawned, "Starting",
                "Artech.Architecture.Common 11.0.0.0 not found", spawned.AddSeconds(Program.WorkerStartupStallSeconds + 5));

            Assert.False((bool)early["stalled"]!);
            Assert.True((bool)stalled["stalled"]!);
            Assert.Equal(Program.WorkerStartupStallSeconds + 5, (int)stalled["elapsedSeconds"]!);
            Assert.Contains("genexus_worker_reload", stalled["hint"]!.ToString());
            Assert.Contains("Artech.Architecture.Common", stalled["hint"]!.ToString());
        }

        [Fact]
        public void IssueReport_CarriesVersionsAndCauses_WithoutPathsOrAliases()
        {
            var gx9 = new KbHandle("acme-client-kb", @"C:\Users\joao\KBs\AcmeClient", @"C:\GeneXus9", "com-gxpublic", "9");
            var result = new JObject
            {
                ["geneXus"] = Program.BuildDoctorGeneXusBlock(GatewayBlock(), gx9),
                ["workerHealth"] = new JObject
                {
                    ["status"] = "starting",
                    ["alias"] = "acme-client-kb",
                    ["stalled"] = true,
                    ["error"] = @"Could not load C:\Users\joao\KBs\AcmeClient\knowledgebase.connection password=hunter2"
                },
                ["warnings"] = new JArray(@"Worker status: starting. Cause: missing C:\GeneXus9\Artech.Common.dll")
            };

            JObject report = Program.BuildDoctorIssueReport(result, gx9, @"C:\Program Files (x86)\GeneXus\GeneXus18");
            string text = report.ToString();

            Assert.Equal("com-gxpublic", report["target"]!["driver"]!.ToString());
            Assert.False((bool)report["target"]!["sameInstallationAsGateway"]!);
            Assert.True((bool)report["worker"]!["stalled"]!);
            Assert.Contains("<path>", text);
            Assert.DoesNotContain("joao", text);
            Assert.DoesNotContain("AcmeClient", text);
            Assert.DoesNotContain("acme-client-kb", text);
            Assert.DoesNotContain("hunter2", text);
            Assert.DoesNotContain(@"C:\", text);
        }

        [Fact]
        public void CopyPasteBlock_IncludesBitnessComArtechExitHexIdeAliasAndInstallDir()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-doctor-965-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string workerExe = Path.Combine(tempDir, "GxMcp.Worker.exe");
                byte[] pe32 = new byte[128];
                pe32[0] = (byte)'M';
                pe32[1] = (byte)'Z';
                pe32[0x3C] = 0x40;
                pe32[0x40] = (byte)'P';
                pe32[0x41] = (byte)'E';
                pe32[0x44] = 0x4C;
                pe32[0x45] = 0x01;
                File.WriteAllBytes(workerExe, pe32);

                var gx9 = new KbHandle("kb-gx9", @"C:\KBs\Legacy", @"C:\GeneXus9", "com-gxpublic", "9");
                var doctorResult = new JObject
                {
                    ["target"] = new JObject { ["alias"] = "kb-gx9" },
                    ["geneXus"] = new JObject { ["installationPath"] = @"C:\GeneXus9", ["major"] = "9" },
                    ["worker"] = new JObject
                    {
                        ["exePath"] = workerExe,
                        ["diagnostics"] = new JObject
                        {
                            ["lastExitCode"] = unchecked((int)0xC0000005)
                        }
                    },
                    ["workerHealth"] = new JObject { ["status"] = "startup_failed" }
                };

                string block = Program.BuildDoctorCopyPasteBlock(
                    doctorResult,
                    gx9,
                    workerExe,
                    name => string.Equals(name, "gx", StringComparison.OrdinalIgnoreCase),
                    _ => "GXPublic.GXPublic.5",
                    _ => "11.0.0.0");

                Assert.Equal("0xC0000005", WorkerCrashGuard.FormatExitCodeHex(unchecked((int)0xC0000005)));
                Assert.Contains("GX alias: kb-gx9", block);
                Assert.Contains(@"Install dir: C:\GeneXus9", block);
                Assert.Contains("Worker bitness: 32-bit", block);
                Assert.Contains("gxpublic COM: GXPublic.GXPublic.5", block);
                Assert.Contains("Artech.Architecture version: 11.0.0.0", block);
                Assert.Contains("Last worker exit code: 0xC0000005 (ACCESS_VIOLATION)", block);
                Assert.Contains("IDE: open (gx.exe)", block);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void GatewayDoctorEnvelope_IncludesCopyPasteIssueReportBlock()
        {
            JObject envelope = Program.BuildGatewayDoctorEnvelope(null);
            JToken? block = envelope["result"]?["issueReportBlock"];

            Assert.NotNull(block);
            string text = block!.ToString();
            Assert.Contains("Worker bitness:", text);
            Assert.Contains("gxpublic COM:", text);
            Assert.Contains("Artech.Architecture version:", text);
            Assert.Contains("Last worker exit code:", text);
            Assert.Contains("IDE:", text);
            Assert.Contains("GX alias:", text);
            Assert.Contains("Install dir:", text);
        }
    }
}
