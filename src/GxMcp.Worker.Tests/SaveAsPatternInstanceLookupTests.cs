using System;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #800: save_as looked the WorkWithPlus instance of its source up among the parts of the host, where it is not
    // (it is an object of its own). The lookup is now the resolution genexus_wwp uses; these tests pin what a resolution
    // means for "the instance linked to the host" and the wiring of the production chain, which no fake cloner reaches.
    public sealed class SaveAsPatternInstanceLookupTests
    {
        private static PatternAnalysisService.LinkedInstanceOutcome Classify(bool resolved, bool resolvedIsHost, string code) =>
            PatternAnalysisService.ClassifyLinkedInstance(resolved, resolvedIsHost, code);

        [Fact]
        public void An_instance_other_than_the_host_is_the_linked_one()
        {
            Assert.Equal(PatternAnalysisService.LinkedInstanceOutcome.Linked, Classify(true, false, null));
        }

        [Fact]
        public void A_host_that_is_itself_the_instance_has_none_linked_to_it()
        {
            Assert.Equal(PatternAnalysisService.LinkedInstanceOutcome.None, Classify(true, true, null));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("PatternInstanceAbsent")]
        [InlineData("PatternMismatch")]
        [InlineData("PatternInstanceAmbiguous")]
        public void No_instance_is_none(string diagnosticCode)
        {
            Assert.Equal(PatternAnalysisService.LinkedInstanceOutcome.None, Classify(false, false, diagnosticCode));
        }

        // The resolution can fail without finding "no instance": no model for the host, a failed child walk, a name match
        // whose owner the SDK did not expose. Reading those as "none" is the silence #800 is about.
        [Theory]
        [InlineData("PatternInstanceResolutionFailed")]
        [InlineData("PatternInstanceOwnershipUnverified")]
        public void A_resolution_that_could_not_tell_is_a_failure_never_none(string diagnosticCode)
        {
            Assert.Equal(PatternAnalysisService.LinkedInstanceOutcome.Failed, Classify(false, false, diagnosticCode));
        }

        [Fact]
        public void The_production_cloner_looks_the_instance_up_through_the_linked_instance_resolution()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "SaveAsService.cs");
            string find = SourceAssert.MethodBody(source, "public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName, string typeFilter)");

            Assert.Contains("_objects.FindObject(sourceName, typeFilter)", find, StringComparison.Ordinal);
            Assert.Contains("_patterns.FindLinkedWorkWithPlusInstance(obj)", find, StringComparison.Ordinal);
            Assert.Contains("InstanceName = instance.Name", find, StringComparison.Ordinal);
            Assert.Contains("throw new InvalidOperationException", find, StringComparison.Ordinal);
        }

        [Fact]
        public void The_linked_instance_resolution_checks_the_host_part_resolves_like_genexus_wwp_and_throws_when_it_cannot_tell()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "PatternAnalysisService.cs");
            string body = SourceAssert.MethodBody(source, "internal KBObject FindLinkedWorkWithPlusInstance(KBObject host)");

            Assert.Contains("FindPatternPart(host, \"PatternInstance\") != null", body, StringComparison.Ordinal);
            Assert.Contains("ResolvePatternInstance(host, PatternRegistry.WorkWithPlusPatternId, fresh: false, out diagnostic)", body, StringComparison.Ordinal);
            Assert.Contains("ClassifyLinkedInstance(resolved != null, resolved != null && resolved.Guid == host.Guid,", body, StringComparison.Ordinal);
            Assert.Contains("case LinkedInstanceOutcome.Failed:", body, StringComparison.Ordinal);
            Assert.Contains("throw new InvalidOperationException(", body, StringComparison.Ordinal);
        }

        [Fact]
        public void The_apply_service_hands_the_host_to_the_analysis_service()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "PatternApplyService.cs");
            string body = SourceAssert.MethodBody(source, "internal KBObject FindLinkedWorkWithPlusInstance(KBObject host)");

            Assert.Contains("Analysis.FindLinkedWorkWithPlusInstance(host)", body, StringComparison.Ordinal);
        }
    }
}
