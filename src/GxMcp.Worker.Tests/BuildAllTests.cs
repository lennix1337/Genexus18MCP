using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BuildAllTests
    {
        [Fact]
        public void BuildAll_rejects_a_directed_target_before_dispatch()
        {
            var service = new BuildService();
            var result = JObject.Parse(service.Build(
                "BuildAll", "Customer", "transitive", 200, false, null, false));

            Assert.Equal("error", result["status"]?.ToString());
            Assert.Equal("BuildAllTargetNotAllowed", result["error"]?["code"]?.ToString());
            Assert.Equal("Customer", result["target"]?.ToString());
        }

        [Fact]
        public void BuildAll_dry_run_is_global_and_requires_no_target()
        {
            var service = new BuildService();
            var result = JObject.Parse(service.BuildDryRun("BuildAll", null, "transitive", 200));

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.Equal("BuildAll", result["result"]?["preview"]?["buildMode"]?.ToString());
            Assert.True(result["result"]?["preview"]?["kbWide"]?.Value<bool>());
            Assert.True(result["result"]?["preview"]?["failIfReorg"]?.Value<bool>());
        }

        [Fact]
        public void BuildAll_dry_run_rejects_a_target()
        {
            var service = new BuildService();
            var result = JObject.Parse(service.BuildDryRun("BuildAll", "Customer", "transitive", 200));

            Assert.Equal("error", result["status"]?.ToString());
            Assert.Equal("BuildAllTargetNotAllowed", result["error"]?["code"]?.ToString());
        }

        [Fact]
        public void External_project_uses_incremental_BuildAll_and_closes_on_error()
        {
            string xml = BuildService.BuildExternalProjectXml(
                @"C:\GX\Genexus.Tasks.targets", @"C:\KB\Test", "BuildAll", new List<string>());

            Assert.Contains("<BuildAll ForceRebuild=\"false\"", xml);
            Assert.Contains("FailIfReorg=\"true\"", xml);
            Assert.Contains("DoNotExecuteReorg=\"true\"", xml);
            Assert.Contains("Output=\"IDE\"", xml);
            Assert.Contains("EventsSuspended=\"true\"", xml);
            Assert.Contains("<OnError ExecuteTargets=\"CloseOnBuildAllError\"", xml);
            Assert.Contains("<Target Name=\"CloseOnBuildAllError\"><CloseKnowledgeBase />", xml);
            Assert.DoesNotContain("IdeWebBuildAndDeploy", xml);

            var project = XDocument.Parse(xml);
            XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
            var executeTarget = project.Root!.Element(msbuild + "Target");
            Assert.Equal("OnError", executeTarget!.Elements().Last().Name.LocalName);
            Assert.Equal("CloseKnowledgeBase", executeTarget.Elements().ElementAt(executeTarget.Elements().Count() - 2).Name.LocalName);
        }

        [Fact]
        public void External_BuildAll_uses_single_msbuild_node_and_disables_node_reuse()
        {
            string args = BuildService.BuildMsBuildArguments("BuildAll", @"C:\Temp\build-all.msbuild");

            Assert.Contains("/m:1", args);
            Assert.Contains("/nodeReuse:false", args);
            Assert.DoesNotContain(" /m ", args);
        }

        [Fact]
        public void BuildAll_completion_requires_explicit_evidence_even_with_zero_exit()
        {
            var status = new BuildService.BuildTaskStatus
            {
                Action = "BuildAll",
                ExitCode = 0,
                Status = "Succeeded"
            };

            BuildService.FinalizeBuildAllStatus(status, "[GXMCP-BUILD-ALL] KB opened\n");

            Assert.Equal("Failed", status.Status);
            Assert.False(status.BuildAllDone);
            Assert.Equal(0, status.MsBuildExitCode);
            Assert.Contains("completion evidence", status.Error);
        }

        [Fact]
        public void BuildAll_reorg_is_a_terminal_structured_state()
        {
            var status = new BuildService.BuildTaskStatus
            {
                Action = "BuildAll",
                ExitCode = 1,
                Status = "Failed"
            };

            BuildService.FinalizeBuildAllStatus(status, "Reorganization required by the KB\n");

            Assert.Equal("ReorgRequired", status.Status);
            Assert.True(status.ReorgRequired);
            Assert.Contains("action=reorg", status.Hint);
        }

        [Theory]
        [InlineData("No reorganization is required by the KB")]
        [InlineData("Build completed; reorganization not needed")]
        [InlineData("FailIfReorg=true; the KB does not require reorg")]
        public void BuildAll_does_not_misclassify_non_required_reorg_messages(string output)
        {
            Assert.False(BuildService.DetectBuildAllReorgRequired(output));
        }
    }
}
