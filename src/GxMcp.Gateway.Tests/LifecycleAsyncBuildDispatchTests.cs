using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LifecycleAsyncBuildDispatchTests
    {
        [Fact]
        public void BuildCommandFactory_UsesRebuildAllForRebuild()
        {
            var command = Program.BuildAsyncLifecycleCommand(
                "rebuild",
                new JObject { ["target"] = "Customer" },
                "job-2");

            Assert.Equal("RebuildAll", command["action"]!.ToString());
            Assert.Equal("job-2", command["cancelToken"]!.ToString());
        }

        [Fact]
        public void BuildCommandFactory_UsesBuildAllForBuildAll()
        {
            var command = Program.BuildAsyncLifecycleCommand(
                "build_all",
                new JObject { ["dryRun"] = false },
                "job-3");

            Assert.Equal("BuildAll", command["action"]!.ToString());
            Assert.True(string.IsNullOrEmpty(command["target"]?.ToString()));
            Assert.Equal("job-3", command["cancelToken"]!.ToString());
        }
    }
}
