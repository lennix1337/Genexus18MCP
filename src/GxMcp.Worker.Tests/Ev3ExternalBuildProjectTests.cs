using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // GeneXus X Evolution 3 MSBuild tasks have no CompileMains/FailIfReorg/DoNotExecuteReorg
    // parameters (MSB4064), and none of its stock build tasks can keep a build from
    // reorganizing the database. The EV3 project builds through its own task instead.
    public class Ev3ExternalBuildProjectTests
    {
        private static readonly XNamespace Ms = "http://schemas.microsoft.com/developer/msbuild/2003";
        private static readonly string[] Gx18OnlyParameters = { "CompileMains", "FailIfReorg", "DoNotExecuteReorg" };

        private static XDocument Project(string action, params string[] targets) =>
            XDocument.Parse(BuildService.BuildExternalProjectXml(
                @"C:\GX\Genexus.Tasks.targets", @"C:\KB\Win", action, targets.ToList(), null, evolution3: true));

        private static XElement BuildTask(XDocument project) =>
            Assert.Single(project.Descendants(Ms + "Target").First().Elements(), e => e.Name.LocalName == "GxMcpBuildNoReorg");

        [Theory]
        [InlineData("Build")]
        [InlineData("Rebuild")]
        [InlineData("RebuildAll")]
        [InlineData("BuildAll")]
        public void Ev3_project_never_passes_parameters_the_ev3_tasks_lack(string action)
        {
            var project = Project(action, "WorkPanel:WinProbe1");
            var execute = project.Descendants(Ms + "Target").First();
            foreach (var element in execute.Elements())
                foreach (var parameter in Gx18OnlyParameters)
                    Assert.Null(element.Attribute(parameter));
            Assert.DoesNotContain(execute.Elements(), e => e.Name.LocalName is "IdeWebBuildAndDeploy" or "BuildAll" or "SpecifyOneOnly");
        }

        [Fact]
        public void Ev3_targeted_build_names_its_objects_and_never_reorganizes()
        {
            var project = Project("Build", "WorkPanel:WinProbe1", "Customer");
            var task = BuildTask(project);
            Assert.Equal("WorkPanel:WinProbe1;Customer", (string)task.Attribute("ObjectNames"));
            Assert.Equal("false", (string)task.Attribute("ForceRebuild"));

            var usingTask = Assert.Single(project.Root!.Elements(Ms + "UsingTask"), e => (string)e.Attribute("TaskName") == "GxMcpBuildNoReorg");
            string code = usingTask.Descendants(Ms + "Code").Single().Value;
            Assert.Contains("BuildOptions.DoNotExecuteReorg", code);
            Assert.Contains("BuildOptions.DoNotGenerateReorg", code);
            Assert.Contains(usingTask.Descendants(Ms + "Reference"), r => (string)r.Attribute("Include") == @"C:\GX\Artech.Genexus.Common.dll");
            Assert.DoesNotContain(project.Descendants(), e => e.Name.LocalName == "CheckAndInstallDatabase");
        }

        [Theory]
        [InlineData("Rebuild", "true")]
        [InlineData("RebuildAll", "true")]
        [InlineData("Build", "false")]
        public void Ev3_rebuild_maps_to_force_rebuild(string action, string expected)
        {
            Assert.Equal(expected, (string)BuildTask(Project(action, "Customer")).Attribute("ForceRebuild"));
        }

        [Fact]
        public void Ev3_build_all_builds_every_object_without_reorg_and_closes_on_error()
        {
            var project = Project("BuildAll");
            var task = BuildTask(project);
            Assert.Equal("", (string)task.Attribute("ObjectNames"));
            var execute = project.Descendants(Ms + "Target").First();
            Assert.Equal("OnError", execute.Elements().Last().Name.LocalName);
            Assert.Equal("CloseOnBuildAllError", (string)execute.Elements().Last().Attribute("ExecuteTargets"));
            Assert.Contains("[GXMCP-BUILD-ALL] BuildAll started", project.ToString());
        }

        [Fact]
        public void Unlicensed_generator_is_an_itemized_error_naming_the_generator()
        {
            var status = new BuildService.BuildTaskStatus { Action = "Build", Status = "Running" };
            var sink = typeof(BuildService).GetMethod("HandleLine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            sink!.Invoke(new BuildService(), new object[] { status, "  >RGeneXus C# Win Generator:\\n local protection: \\r\\n\tNot authorized", false });

            Assert.Equal(1, status.ErrorCount);
            Assert.Contains(status.Errors, e => e.Contains("GeneXus C# Win Generator") && e.Contains("not licensed"));
        }

        [Fact]
        public void Ev3_explicit_reorg_stays_the_only_database_installation()
        {
            var project = Project("Reorg");
            Assert.Contains(project.Descendants(), e => e.Name.LocalName == "CheckAndInstallDatabase");
            Assert.DoesNotContain(project.Descendants(), e => e.Name.LocalName == "GxMcpBuildNoReorg");
        }
    }
}
