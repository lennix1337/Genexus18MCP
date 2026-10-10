using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class GxwireGatewayToolTests
    {
        [Fact]
        public void ToolProfileFilter_IncludesGxwireInCoreProfiles()
        {
            Assert.Null(ToolProfileFilter.GetToolNotInProfileError("core", "genexus_wire"));
            Assert.Null(ToolProfileFilter.GetToolNotInProfileError("standard", "genexus_wire"));
            Assert.Null(ToolProfileFilter.GetToolNotInProfileError("authoring", "genexus_wire"));
        }

        [Fact]
        public void ToolHelpCatalog_ContainsGxwireDocumentation()
        {
            string? help = ToolHelpCatalog.Get("genexus_wire");
            Assert.NotNull(help);
            Assert.Contains("pack_task", help);
            Assert.Contains("callers", help);
            Assert.Contains("impact", help);
            Assert.Contains("slice", help);
        }

        [Theory]
        [InlineData("pack_task")]
        [InlineData("for")]
        [InlineData("callers")]
        [InlineData("impact")]
        [InlineData("slice")]
        [InlineData("safe_delete")]
        [InlineData("index")]
        [InlineData("doctor")]
        public void OperationClassifier_AllGxwireActions_AreReadOnly(string action)
        {
            var args = new JObject { ["action"] = action };
            Assert.Equal(OperationClassifier.OperationKind.ReadOnly,
                OperationClassifier.ClassifyTool("genexus_wire", args));
            Assert.True(OperationClassifier.IsReadOnly("genexus_wire", args));
            Assert.False(OperationClassifier.IsMutationCandidate("genexus_wire", args));
        }

        [Fact]
        public async Task Gxwire_Doctor_ExecutesSub10msAndReturnsReady()
        {
            var args = new JObject { ["action"] = "doctor" };
            var result = await Program.ExecuteGxwireToolAsyncForTest(args);

            Assert.NotNull(result);
            Assert.Equal("ok", result["status"]?.ToString());
            Assert.Equal("gxwire", result["engine"]?.ToString());
            string output = result["output"]?.ToString() ?? string.Empty;
            Assert.Contains("<doctor", output);
            Assert.Contains("status=\"ready\"", output);
        }

        [Fact]
        public async Task Gxwire_MissingTarget_ReturnsTargetRequiredError()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            try
            {
                var args = new JObject
                {
                    ["action"] = "callers",
                    ["dir"] = tempDir
                };
                var result = await Program.ExecuteGxwireToolAsyncForTest(args);
                Assert.Equal("error", result["status"]?.ToString());
                Assert.Equal("TargetRequired", result["error"]?.ToString());
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task Gxwire_MissingTask_ReturnsTaskRequiredError()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            try
            {
                var args = new JObject
                {
                    ["action"] = "pack_task",
                    ["dir"] = tempDir
                };
                var result = await Program.ExecuteGxwireToolAsyncForTest(args);
                Assert.Equal("error", result["status"]?.ToString());
                Assert.Equal("TaskRequired", result["error"]?.ToString());
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
