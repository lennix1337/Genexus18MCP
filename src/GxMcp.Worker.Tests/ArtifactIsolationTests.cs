using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using GxMcp.Worker.Utils;

namespace GxMcp.Worker.Tests
{
    public class ArtifactIsolationTests
    {
        [Fact]
        public void ArtifactOutput_UsesDistinctPerKbScopes()
        {
            string root = NewTempDirectory();
            try
            {
                var first = new ArtifactPathResolver(() => @"C:\KBs\one", root);
                var second = new ArtifactPathResolver(() => @"C:\KBs\two", root);
                var sameKbViaGxw = new ArtifactPathResolver(() => @"C:\KBs\one\KnowledgeBase.gxw", root);

                var firstPaths = first.Resolve();
                var secondPaths = second.Resolve();
                var sameKbPaths = sameKbViaGxw.Resolve();

                Assert.NotEqual(firstPaths.KbScopeDirectory, secondPaths.KbScopeDirectory);
                Assert.Equal(firstPaths.KbScopeDirectory, sameKbPaths.KbScopeDirectory);
                Assert.NotEqual(firstPaths.DocumentationDirectory, secondPaths.DocumentationDirectory);
                Assert.NotEqual(firstPaths.HtmlDirectory, secondPaths.HtmlDirectory);
                Assert.True(IsUnder(root, firstPaths.KbScopeDirectory));
                Assert.True(IsUnder(root, secondPaths.KbScopeDirectory));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void ArtifactOutput_DefaultRootSurvivesWorkerInstallUpgrade()
        {
            string root = NewTempDirectory();
            string localAppData = Path.Combine(root, "local-app-data");
            string oldInstall = Path.Combine(root, "old-install");
            string newInstall = Path.Combine(root, "new-install");
            Directory.CreateDirectory(oldInstall);
            Directory.CreateDirectory(newInstall);
            try
            {
                var beforeUpgrade = new ArtifactPathResolver(
                    () => @"C:\KBs\upgrade-demo",
                    configuredRoot: null,
                    baseDirectory: oldInstall,
                    localAppData: localAppData);
                string artifact = beforeUpgrade.ResolveFile(ArtifactKind.Documentation, "Transaction_Customer.md");
                Directory.CreateDirectory(Path.GetDirectoryName(artifact));
                File.WriteAllText(artifact, "generated before upgrade");

                var afterUpgrade = new ArtifactPathResolver(
                    () => @"C:\KBs\upgrade-demo",
                    configuredRoot: null,
                    baseDirectory: newInstall,
                    localAppData: localAppData);
                string resolvedAfterUpgrade = afterUpgrade.ResolveFile(ArtifactKind.Documentation, "Transaction_Customer.md");

                Assert.Equal(artifact, resolvedAfterUpgrade);
                Assert.Equal("generated before upgrade", File.ReadAllText(resolvedAfterUpgrade));
                Assert.DoesNotContain(oldInstall, resolvedAfterUpgrade, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(newInstall, resolvedAfterUpgrade, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void ArtifactOutput_RejectsPathTraversalFileNames()
        {
            string root = NewTempDirectory();
            try
            {
                var resolver = new ArtifactPathResolver(() => @"C:\KBs\safe", root);

                Assert.Throws<ArtifactPathException>(() =>
                    resolver.ResolveFile(ArtifactKind.Documentation, "..\\..\\escape.md"));
                Assert.Throws<ArtifactPathException>(() =>
                    resolver.ResolveFile(ArtifactKind.Documentation, "nested/escape.md"));
                Assert.False(File.Exists(Path.Combine(root, "escape.md")));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void Visualizer_GeneratesUniqueGraphFilesUnderTheKbScope()
        {
            string root = NewTempDirectory();
            try
            {
                var index = new IndexCacheService();
                index.LoadFromEntries(new[]
                {
                    new SearchIndex.IndexEntry
                    {
                        Name = "Customer",
                        Type = "Transaction",
                        Calls = new List<string>(),
                        CalledBy = new List<string>()
                    }
                });
                var artifacts = new ArtifactPathResolver(() => @"C:\KBs\visualizer", root);
                var service = new VisualizerService(index, artifacts);

                var first = JObject.Parse(service.GenerateGraph("{\"domain\":\"All\"}"));
                var second = JObject.Parse(service.GenerateGraph("{\"domain\":\"All\"}"));
                string firstPath = (string)first["result"]["url"];
                string secondPath = (string)second["result"]["url"];

                Assert.Equal("ok", (string)first["status"]);
                Assert.Equal("ok", (string)second["status"]);
                Assert.NotEqual(firstPath, secondPath);
                Assert.Equal(artifacts.Resolve().HtmlDirectory, (string)first["result"]["outputDirectory"]);
                Assert.Equal(artifacts.Resolve().KbScopeDirectory, (string)first["result"]["kbArtifactScope"]);
                Assert.True(File.Exists(firstPath.Replace("/", "\\")) || File.Exists(firstPath));
                Assert.True(File.Exists(secondPath.Replace("/", "\\")) || File.Exists(secondPath));
                Assert.True(IsUnder(artifacts.Resolve().HtmlDirectory, firstPath));
                Assert.True(IsUnder(artifacts.Resolve().HtmlDirectory, secondPath));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void Health_UsesTheInjectedIndexCacheSnapshot()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry
                {
                    Name = "Customer",
                    Type = "Transaction",
                    Complexity = 3,
                    Calls = new List<string>(),
                    CalledBy = new List<string>()
                }
            });

            var response = JObject.Parse(new HealthService(index).GetHealthReport());

            Assert.Equal("ok", (string)response["status"]);
            Assert.Equal("HealthReport", (string)response["code"]);
            Assert.Equal(1, (int)response["result"]["totalObjects"]);
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-artifacts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static bool IsUnder(string root, string candidate)
        {
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidateFull = Path.GetFullPath(candidate.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
            }
        }
    }
}
