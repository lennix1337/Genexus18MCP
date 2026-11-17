using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class GatewayMemoryFootprintTests
    {
        [Fact]
        public void GatewayProject_UsesWorkstationGcAndBoundedMemorySettings()
        {
            string csprojText = RepoSource.Read("src", "GxMcp.Gateway", "GxMcp.Gateway.csproj");
            XDocument doc = XDocument.Parse(csprojText);

            string? serverGc = doc.Root?.Element("PropertyGroup")?.Element("ServerGarbageCollection")?.Value;
            string? retainVm = doc.Root?.Element("PropertyGroup")?.Element("RetainVMGarbageCollection")?.Value;
            Assert.Equal("false", serverGc);
            Assert.Equal("false", retainVm);

            string? conserveMemory = null;
            string? heapHardLimitRaw = null;
            foreach (var opt in doc.Descendants("RuntimeHostConfigurationOption"))
            {
                string? include = opt.Attribute("Include")?.Value;
                if (string.Equals(include, "System.GC.ConserveMemory", StringComparison.Ordinal))
                    conserveMemory = opt.Attribute("Value")?.Value;
                else if (string.Equals(include, "System.GC.HeapHardLimit", StringComparison.Ordinal))
                    heapHardLimitRaw = opt.Attribute("Value")?.Value;
            }

            Assert.Equal("9", conserveMemory);
            Assert.Null(heapHardLimitRaw);
        }

        [Fact]
        public void ToolIdentity_CanonicalNames_MatchRawToolDefinitionsAfterCompatibilityApply()
        {
            string? path = ToolDefinitionsLocator.Locate();
            Assert.NotNull(path);

            var rawNames = JArray.Parse(File.ReadAllText(path!))
                .OfType<JObject>()
                .Select(t => t["name"]?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();

            ToolDefinitionsLocator.ClearCache();
            var sharedNames = ToolIdentity.CanonicalToolNames.ToList();

            Assert.Equal(rawNames, sharedNames);
        }

        [Fact]
        public void ToolDefinitionsLocator_SharesSingleParsedArrayAcrossCallers()
        {
            ToolDefinitionsLocator.ClearCache();
            var first = ToolDefinitionsLocator.GetOrLoadDefinitions();
            var second = ToolDefinitionsLocator.GetOrLoadDefinitions();

            Assert.NotNull(first);
            Assert.Same(first, second);

            ToolDefinitionsLocator.ClearCache();
            var afterClear = ToolDefinitionsLocator.GetOrLoadDefinitions();
            Assert.NotNull(afterClear);
            Assert.NotSame(first, afterClear);
        }

        [Fact]
        public void RecipeCatalog_ConfigureUserMacroDirectory_IsLazyUntilQueried()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-recipe-lazy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string macroPath = Path.Combine(tempDir, "lazy_test_macro.json");
                File.WriteAllText(macroPath, "{\"name\":\"lazy_test_macro\",\"description\":\"before\"}");

                RecipeCatalog.ConfigureUserMacroDirectory(tempDir);

                // Overwrite before first query: if ConfigureUserMacroDirectory were eager,
                // the cached description would be "before" instead of "after".
                File.WriteAllText(macroPath, "{\"name\":\"lazy_test_macro\",\"description\":\"after\"}");

                var listed = RecipeCatalog.Dispatch("list", null);
                Assert.Contains("lazy_test_macro", listed.ToString());
                var fetched = RecipeCatalog.Get("lazy_test_macro");
                Assert.Equal("after", fetched["description"]?.ToString());
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
