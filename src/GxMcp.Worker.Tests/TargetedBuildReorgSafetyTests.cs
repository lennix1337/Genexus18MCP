using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TargetedBuildReorgSafetyTests
    {
        [Theory]
        [InlineData("Build", "UnrelatedPanel")]
        [InlineData("Build", null)]
        [InlineData("RebuildAll", "UnrelatedPanel")]
        [InlineData("RebuildAll", null)]
        public void External_build_paths_never_implicitly_apply_reorganization(string action, string target)
        {
            var targets = target == null ? new List<string>() : new List<string> { target };
            var project = XDocument.Parse(BuildService.BuildExternalProjectXml("tasks.targets", "KBTeste", action, targets));
            var build = Assert.Single(project.Descendants(), e => e.Name.LocalName == "IdeWebBuildAndDeploy");
            Assert.Equal("true", (string)build.Attribute("FailIfReorg"));
            Assert.Equal("true", (string)build.Attribute("DoNotExecuteReorg"));
            Assert.Contains(project.Descendants(), e => e.Name.LocalName == "OnError"
                && (string)e.Attribute("ExecuteTargets") == "CloseOnBuildError");
            Assert.DoesNotContain(project.Descendants(), e => e.Name.LocalName == "CheckAndInstallDatabase");
        }

        [Fact]
        public void Explicit_reorg_remains_the_only_database_installation_task()
        {
            string xml = BuildService.BuildExternalProjectXml("tasks.targets", "KBTeste", "Reorg", new List<string>());
            Assert.Contains("<CheckAndInstallDatabase", xml);
            Assert.DoesNotContain("IdeWebBuildAndDeploy", xml);
        }

        [Theory]
        [InlineData("Build")]
        [InlineData("RebuildAll")]
        [InlineData("BuildAll")]
        public void Reorg_refusal_is_a_terminal_failure_not_partial_success(string action)
        {
            var status = new BuildService.BuildTaskStatus { Action = action, Status = "Failed", PartialSuccess = true, ExitCode = 1 };
            BuildService.FinalizeBuildAllStatus(status, "Reorganization required by the KB");
            Assert.Equal("ReorgRequired", status.Status);
            Assert.Equal("reorg_required", status.FailureOrigin);
            Assert.True(status.ReorgRequired);
            Assert.False(status.PartialSuccess);
            Assert.Contains("action=reorg_preview", status.Hint);
            Assert.Contains("action=reorg", status.Hint);
        }

        [Fact]
        public void Targeted_reorg_refusal_is_preserved_when_earlier_output_says_not_required()
        {
            var status = new BuildService.BuildTaskStatus { Action = "Build", Status = "Failed" };
            var sink = typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);
            var service = new BuildService();
            sink.Invoke(service, new object[] { status, "No reorganization is required by the KB", false });
            sink.Invoke(service, new object[] { status, "Reorganization required by the KB", true });
            BuildService.FinalizeBuildAllStatus(status, status.FullOutput.ToString());
            Assert.Equal("ReorgRequired", status.Status);
        }
    }
}
