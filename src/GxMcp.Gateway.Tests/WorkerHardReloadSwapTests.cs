using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Regression coverage for `genexus_worker_reload mode=hard`, which failed on 100% of calls.
    //
    // CopyWorkerBinaries required BOTH "GxMcp.Worker.exe" and "GxMcp.Worker.dll". The Worker is
    // a net48 project with OutputType=Exe, so the exe IS the assembly and no
    // GxMcp.Worker.dll is ever produced — not in bin\Debug, not in bin\Release, not in
    // publish\worker, not in the staged runtime. The guard therefore threw on every hard
    // reload, and un-cleanly: it runs inside DrainAndReplaceAsync's post-drain hook, so the
    // old worker had already been stopped and entry.Worker cleared by the time it threw. The
    // KB was left with no live worker and the old binary still in place.
    //
    // The intent of the guard (fail loudly rather than silently skip) was right; it just named
    // a file that cannot exist. These tests pin the required set to what the Worker build can
    // actually emit, and pin the fail-fast behaviour that keeps a bad sourceDir from costing a
    // running worker.
    public class WorkerHardReloadSwapTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "gxmcp-hardswap-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        /// <summary>Mirrors the real Worker build output: exe + exe.config, and no dll.</summary>
        private string CreateRealisticWorkerOutput(bool includePdb = true)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "GxMcp.Worker.exe"), "stub");
            File.WriteAllText(Path.Combine(_root, "GxMcp.Worker.exe.config"), "<configuration/>");
            if (includePdb) File.WriteAllText(Path.Combine(_root, "GxMcp.Worker.pdb"), "stub");
            return _root;
        }

        [Fact]
        public void AcceptsARealWorkerBuildOutput()
        {
            // The exact case that used to fail: an output directory with no GxMcp.Worker.dll.
            var source = CreateRealisticWorkerOutput();

            Program.ValidateWorkerBinarySwapSource(source);
        }

        [Fact]
        public void AcceptsAnOutputWithoutThePdb()
        {
            var source = CreateRealisticWorkerOutput(includePdb: false);

            Program.ValidateWorkerBinarySwapSource(source);
        }

        [Theory]
        [InlineData("events-u16-wwp16.2-audit.json")]
        [InlineData("events-u11-wwp16.1-audit.json")]
        [InlineData("events-u10-audit.json")]
        public void HardReload_CopiesTheImmutableEventsAuditManifestsWhenPresent(string manifest)
        {
            var source = CreateRealisticWorkerOutput();
            var target = Path.Combine(_root, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, manifest), "immutable audit bytes");

            typeof(Program).GetMethod("CopyWorkerBinaries", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { source, target });

            Assert.Equal("immutable audit bytes", File.ReadAllText(Path.Combine(target, manifest)));
        }

        [Fact]
        public void EveryEventsAuditManifestOfTheRepositoryIsCopiedByAHardReload()
        {
            // A manifest added to config/ without a place in the swap list would make the swapped Worker refuse the
            // isolated Events save (distributed copy missing), which only a live hard reload would show.
            string config = Path.Combine(FindRepositoryRoot(), "config");
            var manifests = Directory.GetFiles(config, "events-*-audit.json").Select(path => Path.GetFileName(path)!).ToArray();
            Assert.NotEmpty(manifests);

            var swapList = (string[])typeof(Program)
                .GetField("OptionalWorkerSwapFiles", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(null)!;

            foreach (string manifest in manifests)
                Assert.Contains(manifest, swapList);
        }

        [Fact]
        public void RejectsADirectoryMissingTheAssembly()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "GxMcp.Worker.exe.config"), "<configuration/>");

            var ex = Assert.Throws<InvalidOperationException>(
                () => Program.ValidateWorkerBinarySwapSource(_root));
            Assert.Contains("GxMcp.Worker.exe", ex.Message);
        }

        [Fact]
        public void RejectsADirectoryMissingTheRuntimeConfig()
        {
            // The config carries the binding redirects and SDK probing. A swap that dropped it
            // would install a worker that cannot resolve the GeneXus SDK, so it is required.
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "GxMcp.Worker.exe"), "stub");

            var ex = Assert.Throws<InvalidOperationException>(
                () => Program.ValidateWorkerBinarySwapSource(_root));
            Assert.Contains("GxMcp.Worker.exe.config", ex.Message);
        }

        [Fact]
        public void RejectsAMissingDirectory()
        {
            Assert.Throws<InvalidOperationException>(
                () => Program.ValidateWorkerBinarySwapSource(Path.Combine(_root, "nope")));
        }

        [Fact]
        public void RejectsANullOrBlankSource()
        {
            Assert.Throws<InvalidOperationException>(
                () => Program.ValidateWorkerBinarySwapSource(null));
            Assert.Throws<InvalidOperationException>(
                () => Program.ValidateWorkerBinarySwapSource("   "));
        }

        [Fact]
        public void NeverRequiresAFileTheWorkerProjectCannotProduce()
        {
            // Structural guard, so this cannot silently regress when the Worker changes shape.
            // If OutputType ever becomes Library the dll becomes a real artifact and the
            // required set should be revisited; until then requiring it is always wrong.
            string repoRoot = FindRepositoryRoot();
            string csproj = File.ReadAllText(
                Path.Combine(repoRoot, "src", "GxMcp.Worker", "GxMcp.Worker.csproj"));
            string outputType = System.Text.RegularExpressions.Regex.Match(
                csproj, "<OutputType>([^<]+)</OutputType>").Groups[1].Value.Trim();

            Assert.Equal("Exe", outputType);

            // A published/staged Worker directory carries the exe and its config, nothing else.
            foreach (string candidate in new[]
            {
                Path.Combine(repoRoot, "publish", "worker"),
                Path.Combine(repoRoot, "src", "GxMcp.Worker", "bin", "Debug"),
            })
            {
                if (!Directory.Exists(candidate)) continue;
                Assert.True(File.Exists(Path.Combine(candidate, "GxMcp.Worker.exe")),
                    $"expected a Worker exe in {candidate}");
                Assert.False(File.Exists(Path.Combine(candidate, "GxMcp.Worker.dll")),
                    $"OutputType=Exe must not emit GxMcp.Worker.dll, but {candidate} has one");
                // Whatever a real output directory contains must validate.
                Program.ValidateWorkerBinarySwapSource(candidate);
            }
        }

        // The fail-fast ORDERING (validate before drain, so a bad sourceDir cannot cost a live
        // worker) is deliberately not asserted here: a source-text ordering check is brittle
        // and, in this file, matches a comment rather than the call. It is verified against a
        // real gateway instead — mode=hard with a bogus sourceDir must return
        // WorkerSwapSourceInvalid and leave the running worker serving.

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Genexus18MCP.sln"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the Genexus18MCP repository root.");
        }
    }
}
