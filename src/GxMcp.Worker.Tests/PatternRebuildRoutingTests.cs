using System;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #359: <c>genexus_lifecycle action=rebuild target=&lt;objeto&gt;</c> ran a
    /// Rebuild All of the whole Knowledge Base.
    ///
    /// <para>
    /// Two defects, one visible and one latent. The Gateway mapped <c>rebuild</c> to the
    /// Worker's <c>RebuildAll</c> unconditionally, and the Worker's <c>buildAll</c> branch
    /// emits a KB-wide <c>&lt;BuildAll/&gt;</c> without ever reading <c>targets</c> - so
    /// the target was silently discarded and the response came back
    /// <c>Action: RebuildAll</c>. The Worker's plan builder already emits
    /// <c>SpecifyOneOnly</c> + <c>ForceRebuild=true</c> for a targeted <c>Rebuild</c>; it
    /// was simply never asked to.
    /// </para>
    ///
    /// <para>
    /// Issue #546 superseded the Gateway half of that fix: <c>genexus_lifecycle
    /// action=rebuild</c> now refuses a target with <c>TargetNotSupported</c> before any
    /// dispatch, so the targeted <c>Rebuild</c> mapping was unreachable and was removed.
    /// The Worker guards below are unchanged: <c>RebuildAll</c> with a target is still
    /// refused as defense in depth, and the targeted <c>Rebuild</c> action remains
    /// reachable through the legacy <c>genexus_build</c> route.
    /// </para>
    /// </summary>
    public class RebuildTargetRoutingTests
    {
        private static string LifecycleGateway() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Gateway", "Program.LifecycleGateway.cs");

        private static string BuildService() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Worker", "Services", "BuildService.cs");

        [Fact]
        public void A_Targeted_Rebuild_Is_Refused_By_The_Gateway_Instead_Of_Being_Routed()
        {
            // Issue #546: the intercept refuses the target before dispatch, and the dead
            // targeted mapping that #359 added to the async command factory is gone.
            string source = LifecycleGateway();

            Assert.DoesNotContain("rebuildHasTarget", source, StringComparison.Ordinal);
            Assert.Contains("string.Equals(lifecycleAction, \"rebuild\", StringComparison.OrdinalIgnoreCase)",
                source, StringComparison.Ordinal);
            Assert.Contains("[\"code\"] = \"TargetNotSupported\"", source, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Targetless_Rebuild_Stays_A_Whole_Kb_Rebuild()
        {
            // What the tool description promises, and what the reporter relied on when
            // they did not pass a target.
            string source = LifecycleGateway();
            Assert.Contains("rebuild ? \"RebuildAll\"", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Worker_Refuses_A_RebuildAll_Carrying_A_Target()
        {
            // Exercised as a predicate, not asserted as a string in source. Asserting the
            // error code was present in the file stayed green when the guard's condition
            // was short-circuited to false - present in source, absent in behaviour.
            string rejection = GxMcp.Worker.Services.BuildService.RejectGlobalActionWithTarget(
                "RebuildAll", "TPacClsWW");

            Assert.NotNull(rejection);
            Assert.Contains("RebuildAllTargetNotAllowed", rejection, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Refusal_Explains_That_The_Target_Would_Be_Discarded()
        {
            // The defect's real harm is the surprise: a caller who asked for one object and
            // paid for the whole KB with nothing in the response saying so.
            string rejection = GxMcp.Worker.Services.BuildService.RejectGlobalActionWithTarget(
                "RebuildAll", "TPacClsWW");

            Assert.NotNull(rejection);
            Assert.Contains("silently", rejection, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("requestedWorkerAction", rejection, StringComparison.Ordinal);
            Assert.Contains("TPacClsWW", rejection, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Targetless_RebuildAll_Is_Not_Refused()
        {
            // The documented whole-KB path must keep working.
            Assert.Null(GxMcp.Worker.Services.BuildService.RejectGlobalActionWithTarget(
                "RebuildAll", null));
            Assert.Null(GxMcp.Worker.Services.BuildService.RejectGlobalActionWithTarget(
                "RebuildAll", "   "));
        }

        [Fact]
        public void A_Targeted_Action_Is_Not_Refused()
        {
            // "Rebuild" is the targeted action the legacy genexus_build route still sends
            // (issue #546 closed it only for genexus_lifecycle); refusing it here would
            // close that route as well.
            foreach (var action in new[] { "Rebuild", "Build", "Specify", "CompileCheck" })
            {
                Assert.True(
                    GxMcp.Worker.Services.BuildService.RejectGlobalActionWithTarget(
                        action, "TPacClsWW") == null,
                    action + " was wrongly refused");
            }
        }

        [Fact]
        public void The_Existing_BuildAll_Guard_Is_Still_In_Place_And_Behaves()
        {
            // The new guard is an addition, not a replacement.
            string rejection = GxMcp.Worker.Services.BuildService.RejectGlobalActionWithTarget(
                "BuildAll", "TPacClsWW");

            Assert.NotNull(rejection);
            Assert.Contains("BuildAllTargetNotAllowed", rejection, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Match_Is_Case_Insensitive_Like_The_Rest_Of_The_Action_Names()
        {
            // A caller sending "rebuildall" must not slip past the guard.
            Assert.NotNull(GxMcp.Worker.Services.BuildService.RejectGlobalActionWithTarget(
                "rebuildall", "T1"));
        }

        [Fact]
        public void The_Guard_Is_Actually_Called_On_The_Build_Path()
        {
            // Otherwise the predicate is correct and unreachable.
            string source = BuildService();
            Assert.Contains("RejectGlobalActionWithTarget(action, target)", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Whole_Kb_Branch_Still_Ignores_Targets_Which_Is_Why_The_Guard_Exists()
        {
            // Pins the reason the backstop is necessary rather than defensive boilerplate.
            string source = BuildService();

            // Anchored on the classic whole-KB branch: the Evolution 3 branch above it
            // handles targets through its own task and is pinned by its own tests.
            int buildAll = source.IndexOf("else if (buildAll)", StringComparison.Ordinal);
            Assert.True(buildAll > 0, "the whole-KB branch was not found");
            var window = source.Substring(buildAll, Math.Min(2600, source.Length - buildAll));

            int specifyOnly = window.IndexOf("SpecifyOneOnly", StringComparison.Ordinal);
            int ifTargets = window.IndexOf("else if (targets != null && targets.Count > 0", StringComparison.Ordinal);

            Assert.True(ifTargets > 0, "the targeted branch was not found");
            Assert.True(specifyOnly > ifTargets,
                "SpecifyOneOnly must sit in the targeted branch, after the targets test");
        }
    }

    /// <summary>
    /// Issue #351, discovered while answering it: <c>genexus_sdk_probe mode=surface</c>
    /// failed outright, which is the tool whose entire stated purpose is to make SDK
    /// investigations like that one cheap.
    /// </summary>
    public class SdkSurfaceProbeShortNameTests
    {
        [Fact]
        public void The_Index_Builder_No_Longer_Strips_A_Namespace_It_Did_Not_Find()
        {
            // The defect: `fn.Substring((ns.Key + ".").Length)` threw
            // ArgumentOutOfRangeException ("startIndex cannot be greater than the length of
            // the string") for every global-namespace type, because their namespace key is
            // the literal "(global)" and 8 characters were stripped off a name that may be
            // shorter than that.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "SdkSurfaceProbe.cs");

            Assert.DoesNotContain("fn.Substring((ns.Key + \".\").Length)", source, StringComparison.Ordinal);
            Assert.Contains("ShortName(fn, ns.Key)", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Global_Namespace_Is_Never_Treated_As_A_Prefix_To_Strip()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "SdkSurfaceProbe.cs");

            int shortName = source.IndexOf("private static string ShortName", StringComparison.Ordinal);
            Assert.True(shortName > 0, "ShortName was not found");
            var body = source.Substring(shortName, Math.Min(900, source.Length - shortName));

            Assert.Contains("ns == \"(global)\"", body, StringComparison.Ordinal);
            Assert.Contains("StartsWith(prefix", body, StringComparison.Ordinal);
        }
    }
}
