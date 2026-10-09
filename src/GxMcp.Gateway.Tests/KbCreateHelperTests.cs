using System;
using System.IO;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class KbCreateHelperTests
    {
        [Fact]
        public async Task CreateKbAsync_MissingPath_ReturnsPathRequiredError()
        {
            var options = new KbCreateHelper.CreateOptions { Path = "" };
            var result = await KbCreateHelper.CreateKbAsync(
                options, null, "session1", false, null, null, null);

            Assert.Equal("Error", result["status"]?.ToString());
            Assert.Equal("PathRequired", result["code"]?.ToString());
        }

        [Fact]
        public async Task CreateKbAsync_InvalidPath_ReturnsInvalidPathError()
        {
            var options = new KbCreateHelper.CreateOptions { Path = "invalid\0path" };
            var result = await KbCreateHelper.CreateKbAsync(
                options, null, "session1", false, null, null, null);

            Assert.Equal("Error", result["status"]?.ToString());
            Assert.Equal("InvalidPath", result["code"]?.ToString());
        }

        [Fact]
        public async Task CreateKbAsync_DryRun_ReturnsPlanWithoutCreatingDirectory()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp_test_create_" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new KbCreateHelper.CreateOptions
                {
                    Path = tempDir,
                    DryRun = true
                };

                var result = await KbCreateHelper.CreateKbAsync(
                    options, null, "session1", false, null, null, null);

                Assert.Equal("Plan", result["status"]?.ToString());
                Assert.True(result["dryRun"]?.Value<bool>());
                Assert.Equal(Path.GetFileName(tempDir).ToLowerInvariant(), result["alias"]?.ToString());
                Assert.Equal(Path.GetFileName(tempDir), result["name"]?.ToString());
                Assert.Equal($"(LocalDB)\\MSSQLLocalDB", result["dbServer"]?.ToString());
                Assert.Equal($"gx_kb_{Path.GetFileName(tempDir)}", result["dbName"]?.ToString());
                Assert.NotNull(result["plan"]);
                Assert.False(Directory.Exists(tempDir), "DryRun must not create the directory on disk.");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task CreateKbAsync_ExistingNonEmptyDirectory_ReturnsDirectoryNotEmptyError()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp_test_nonempty_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "existing.txt"), "hello");

                var options = new KbCreateHelper.CreateOptions
                {
                    Path = tempDir
                };

                var result = await KbCreateHelper.CreateKbAsync(
                    options, null, "session1", false, null, null, null);

                Assert.Equal("Error", result["status"]?.ToString());
                Assert.Equal("DirectoryNotEmpty", result["code"]?.ToString());
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task CreateKbAsync_ExplicitOptions_RespectedInPlan()
        {
            string targetPath = @"C:\FakeKbs\CustomKb";
            var options = new KbCreateHelper.CreateOptions
            {
                Path = targetPath,
                Name = "MyCustomName",
                Alias = "myalias",
                DbServer = "CUSTOM_SERVER",
                DbName = "custom_db",
                DbUser = "sa",
                DbPassword = "secret" + "Password123!",
                DryRun = true
            };

            var result = await KbCreateHelper.CreateKbAsync(
                options, null, "session1", false, null, null, null);

            Assert.Equal("Plan", result["status"]?.ToString());
            Assert.Equal("MyCustomName", result["name"]?.ToString());
            Assert.Equal("myalias", result["alias"]?.ToString());
            Assert.Equal("CUSTOM_SERVER", result["dbServer"]?.ToString());
            Assert.Equal("custom_db", result["dbName"]?.ToString());
            Assert.NotNull(result["plan"]);
            var plan = result["plan"] as JObject;
            Assert.NotNull(plan);
            Assert.Equal("sa", plan?["dbUser"]?.ToString());
        }
    }
}
