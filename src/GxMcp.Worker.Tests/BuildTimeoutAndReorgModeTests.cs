using System;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #37 items 1-4: wall-clock build timeout resolution + reorg-mode
    // reporting in the reorg_preview envelope.
    public class BuildTimeoutAndReorgModeTests
    {
        [Fact]
        public void ResolveBuildTimeout_DefaultsAndFullKbBuildsAreLarger()
        {
            Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", null);
            Assert.Equal(900, BuildService.ResolveBuildTimeoutSeconds("Build"));
            Assert.Equal(2400, BuildService.ResolveBuildTimeoutSeconds("BuildAll"));
            Assert.Equal(2400, BuildService.ResolveBuildTimeoutSeconds("RebuildAll"));
        }

        [Fact]
        public void ResolveBuildTimeout_EnvOverrideWins_AndIsClamped()
        {
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", "120");
                Assert.Equal(120, BuildService.ResolveBuildTimeoutSeconds("Build"));

                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", "5");   // below floor
                Assert.Equal(60, BuildService.ResolveBuildTimeoutSeconds("Build"));

                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", "99999"); // above ceiling
                Assert.Equal(7200, BuildService.ResolveBuildTimeoutSeconds("Build"));

                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", "garbage"); // ignored → default
                Assert.Equal(900, BuildService.ResolveBuildTimeoutSeconds("Build"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", null);
            }
        }

        [Fact]
        public void ReorgPreview_RequiresAnOpenKb()
        {
            var svc = new BuildService();
            var jo = JObject.Parse(svc.ReorgPreview("MyTrn"));
            Assert.Equal("error", jo["status"]?.ToString());
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void CheckReorgDisabled_ReturnsNull_WhenNoKb()
        {
            // No KbService set → cannot resolve mode → must not block reorg.
            var svc = new BuildService();
            Assert.Null(svc.CheckReorgDisabled());
        }
    }
}
