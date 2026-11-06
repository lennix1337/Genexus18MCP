using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #112 — worker-exe resolution must honor GeneXus.WorkerExecutable, fall back
    // through the known locations when it's missing, and report every probed path so a
    // broken npm/npx extraction (empty publish/worker/) is diagnosable instead of a bare
    // "Worker NOT FOUND at <default>".
    public class WorkerExecutableResolutionTests
    {
        private static string BaseDir => AppContext.BaseDirectory;

        [Fact]
        public void ConfiguredAbsolute_ExistingPath_Wins()
        {
            string fake = Path.Combine(Path.GetTempPath(), "gxmcptest-" + Guid.NewGuid().ToString("N"), "GxMcp.Worker.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
            try
            {
                File.WriteAllText(fake, "stub");
                var config = new Configuration
                {
                    GeneXus = new GeneXusConfig { WorkerExecutable = fake }
                };

                var res = WorkerProcess.ResolveWorkerExecutable(config, BaseDir);

                Assert.Equal(fake, res.ResolvedPath);
                Assert.Equal(fake, res.ConfiguredPath);
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(fake)!, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ConfiguredMissing_FallsBackAndRecordsTriedPaths()
        {
            var config = new Configuration
            {
                GeneXus = new GeneXusConfig { WorkerExecutable = @"Z:\does\not\exist\GxMcp.Worker.exe" }
            };

            var res = WorkerProcess.ResolveWorkerExecutable(config, BaseDir);

            // The configured path never wins when it doesn't exist; whatever resolved (if
            // anything — a dev test host may find its own bin\Debug tree) came from fallbacks.
            Assert.NotEqual(@"Z:\does\not\exist\GxMcp.Worker.exe", res.ResolvedPath);
            Assert.Equal(@"Z:\does\not\exist\GxMcp.Worker.exe", res.ConfiguredPath);
            // The configured path (resolved absolute) is always probed first…
            Assert.Contains(res.TriedPaths, p => p.IndexOf(@"Z:\does\not\exist", StringComparison.OrdinalIgnoreCase) >= 0);
            // …and the first dev fallback was probed next (on a dev machine it may even
            // resolve; on a clean install all three fallbacks are recorded before null).
            Assert.Contains(res.TriedPaths, p => p.IndexOf(@"src\GxMcp.Worker\bin\Debug", StringComparison.OrdinalIgnoreCase) >= 0
                                              || p.EndsWith(@"worker\GxMcp.Worker.exe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Evolution3_UsesTheSiblingEvolution3Worker()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-ev3-worker-" + Guid.NewGuid().ToString("N"));
            string main = Path.Combine(root, "worker", "GxMcp.Worker.exe");
            string ev3 = Path.Combine(root, "worker-ev3", "GxMcp.Worker.exe");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(main)!);
                Directory.CreateDirectory(Path.GetDirectoryName(ev3)!);
                File.WriteAllText(main, string.Empty);
                File.WriteAllText(ev3, string.Empty);

                string? selected = WorkerProcess.SelectSdkProfileWorker(main, "10.3", out string? diagnostic);

                Assert.Equal(ev3, selected);
                Assert.Null(diagnostic);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Evolution3_WithoutItsWorker_FailsInsteadOfStartingTheGeneXus18Worker()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-ev3-worker-" + Guid.NewGuid().ToString("N"));
            string main = Path.Combine(root, "worker", "GxMcp.Worker.exe");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(main)!);
                File.WriteAllText(main, string.Empty);

                string? selected = WorkerProcess.SelectSdkProfileWorker(main, "10.3", out string? diagnostic);

                Assert.Null(selected);
                Assert.StartsWith("GXMCP_EV3_WORKER_MISSING", diagnostic);
                Assert.Contains(Path.Combine(root, "worker-ev3", "GxMcp.Worker.exe"), diagnostic);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Theory]
        [InlineData("18")]
        [InlineData("17")]
        [InlineData("10.2")]
        [InlineData("")]
        [InlineData(null)]
        public void OtherMajors_KeepTheConfiguredWorker(string? major)
        {
            string main = @"C:\publish\worker\GxMcp.Worker.exe";

            string? selected = WorkerProcess.SelectSdkProfileWorker(main, major, out string? diagnostic);

            Assert.Equal(main, selected);
            Assert.Null(diagnostic);
        }

        [Fact]
        public void NoConfig_StillProbesFallbacks()
        {
            var res = WorkerProcess.ResolveWorkerExecutable(new Configuration(), BaseDir);

            Assert.Equal(string.Empty, res.ConfiguredPath);
            // At least the first dev fallback was probed (on a dev machine it may resolve
            // immediately; on a clean install all three get recorded before returning null).
            Assert.True(res.TriedPaths.Count >= 1);
            Assert.All(res.TriedPaths, p => Assert.EndsWith("GxMcp.Worker.exe", p, StringComparison.OrdinalIgnoreCase));
        }
    }
}
