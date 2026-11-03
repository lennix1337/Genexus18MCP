using System.Collections.Generic;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// #830 item 4: <c>genexus_lifecycle action=build force=true target=&lt;object&gt;</c> must
    /// re-specify and regenerate exactly that object, and must refuse the shapes the SDK
    /// Rebuild flag cannot honour instead of building without the force.
    /// </summary>
    public class ForceRebuildTargetTests
    {
        private static JObject ErrorOf(string json) => (JObject)JObject.Parse(json)["error"]!;

        [Fact]
        public void Force_without_target_is_refused_with_a_route_to_rebuild_all()
        {
            string json = BuildService.RejectForceRebuildRequest("Build", null, true, null);

            Assert.NotNull(json);
            var error = ErrorOf(json);
            Assert.Equal("ForceRequiresTarget", error["code"]!.ToString());
            Assert.Contains("action=rebuild", error["hint"]!.ToString());
        }

        [Theory]
        [InlineData("Type:Customer")]
        [InlineData("{7D2F1C0A-1111-4C2B-9A11-0123456789AB}")]
        public void Force_with_type_or_guid_identity_is_refused(string target)
        {
            string json = BuildService.RejectForceRebuildRequest("Build", target, true, "none");

            Assert.NotNull(json);
            Assert.Equal("ForceTargetIdentityUnsupported", ErrorOf(json)["code"]!.ToString());
        }

        [Theory]
        [InlineData("transitive")]
        [InlineData("direct")]
        public void Force_that_would_expand_to_callees_is_refused(string includeCallees)
        {
            string json = BuildService.RejectForceRebuildRequest("Build", "Customer", true, includeCallees);

            Assert.NotNull(json);
            var error = ErrorOf(json);
            Assert.Equal("ForceRequiresIncludeCalleesNone", error["code"]!.ToString());
            Assert.Contains("includeCallees=none", error["hint"]!.ToString());
        }

        [Theory]
        [InlineData("Customer", null)]
        [InlineData("Customer", "none")]
        [InlineData("Customer;Orders", "NONE")]
        public void Force_on_named_objects_without_callees_is_admitted(string target, string includeCallees)
        {
            Assert.Null(BuildService.RejectForceRebuildRequest("Build", target, true, includeCallees));
        }

        [Fact]
        public void Without_force_no_refusal_applies_so_existing_builds_are_unchanged()
        {
            Assert.Null(BuildService.RejectForceRebuildRequest("Build", null, false, "transitive"));
            Assert.Null(BuildService.RejectForceRebuildRequest("Build", "Type:Customer", false, "transitive"));
            Assert.Null(BuildService.RejectForceRebuildRequest("RebuildAll", null, true, "transitive"));
        }

        [Fact]
        public void Forced_object_is_never_skipped_for_specification_even_when_clean_or_listed()
        {
            // A clean object or one on the skip list would otherwise compile the existing .cs.
            Assert.False(InProcessBuildRunner.MaySkipSpecify(true, null, "Customer", dirty: false));
            Assert.False(InProcessBuildRunner.MaySkipSpecify(true, new[] { "Customer" }, "Customer", dirty: false));
        }

        [Fact]
        public void Unforced_clean_object_still_skips_specification()
        {
            Assert.True(InProcessBuildRunner.MaySkipSpecify(false, null, "Customer", dirty: false));
            Assert.False(InProcessBuildRunner.MaySkipSpecify(false, null, "Customer", dirty: true));
            Assert.True(InProcessBuildRunner.MaySkipSpecify(false, new[] { "Customer" }, "Customer", dirty: true));
        }

        [Fact]
        public void External_project_marks_only_the_named_objects_as_forced()
        {
            var status = new BuildService.BuildTaskStatus { ForceRebuildTargets = true };
            string xml = BuildService.BuildExternalProjectXml("tasks.targets", "KBTeste", "Build", new List<string> { "Customer" }, status);

            var spec = Assert.Single(XDocument.Parse(xml).Descendants(), e => e.Name.LocalName == "SpecifyOneOnly");
            Assert.Equal("Customer", (string)spec.Attribute("ObjectNames"));
            Assert.Equal("true", (string)spec.Attribute("ForceRebuild"));
        }

        [Fact]
        public void External_project_without_force_keeps_the_plain_specify_task()
        {
            var status = new BuildService.BuildTaskStatus { ForceRebuildTargets = false };
            string xml = BuildService.BuildExternalProjectXml("tasks.targets", "KBTeste", "Build", new List<string> { "Customer" }, status);

            var spec = Assert.Single(XDocument.Parse(xml).Descendants(), e => e.Name.LocalName == "SpecifyOneOnly");
            Assert.Null(spec.Attribute("ForceRebuild"));
        }
    }
}
