using System;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #352: the pattern diagnostic composed an <c>overrideConflict</c> finding
    /// whose remediation unconditionally told the caller to reapply "to regenerate the
    /// existing pattern instance", in the same response that a
    /// <c>routeUnsupported</c> finding used to declare reapply unsupported. A
    /// diagnostic must not recommend the route it refuses.
    ///
    /// These guards are pure: they assert the composition rule (the remediation is
    /// derived from the same capability the refusal is derived from) rather than
    /// re-testing the individual message strings, so rewording the copy cannot silently
    /// restore the contradiction.
    /// </summary>
    public class PatternRouteCapabilityTests
    {
        // IsWorkWithPlus is derived from the pattern Id, so the WWP case is the
        // registry's real id rather than a flag the caller sets.
        private static PatternManifest NonWwp(string name = "SyntheticPattern") =>
            new PatternManifest
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = name
            };

        private static PatternManifest Wwp() =>
            new PatternManifest
            {
                Id = PatternRegistry.WorkWithPlusPatternId,
                Name = "WorkWithPlus"
            };

        [Fact]
        public void Generic_Reapply_Is_Blocked_And_FirstApply_Is_Allowed()
        {
            var pattern = NonWwp();
            Assert.True(PatternApplyService.PatternRouteCapabilities.IsSupported(
                pattern, PatternApplyService.PatternRoute.FirstApply, out _));
            Assert.False(PatternApplyService.PatternRouteCapabilities.IsSupported(
                pattern, PatternApplyService.PatternRoute.Reapply, out string reason));
            Assert.Contains("not supported", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void WorkWithPlus_Keeps_Its_Own_Supported_Routes()
        {
            // The capability switch governs only the generic pattern engine. Folding
            // WWP into it would break its first-apply and reapply routes.
            Assert.True(PatternApplyService.PatternRouteCapabilities.IsSupported(
                Wwp(), PatternApplyService.PatternRoute.FirstApply, out _));
            Assert.True(PatternApplyService.PatternRouteCapabilities.IsSupported(
                Wwp(), PatternApplyService.PatternRoute.Reapply, out _));
        }

        [Fact]
        public void Generic_Reapply_Is_Not_Claimed_To_Regenerate_Derived_Objects()
        {
            // The recorded GX17 U4 evidence is that generic reapply re-saves the
            // instance while every generated object keeps its previous version. If a
            // future build gains a regenerating route this must flip with the evidence.
            Assert.False(PatternApplyService.PatternRouteCapabilities.ReapplyRegenerates);
            Assert.False(PatternApplyService.PatternRouteCapabilities.IsRegeneratingReapply(NonWwp()));
        }

        [Fact]
        public void WorkWithPlus_Reapply_Is_Not_Claimed_To_Regenerate_Derived_Objects()
        {
            // WWP is excluded from the generic switch because it has its own supported
            // apply route, so it reads its own constant. For an existing instance that route
            // skips the engine and only re-projects the parent, so a derived helper keeps its
            // old contract after a "PatternApplied" reapply. The rebuilding route is
            // genexus_wwp action=regenerate_derived; if reapply ever runs the engine this must
            // flip together with the evidence, and the diagnose remediation flips with it.
            Assert.False(PatternApplyService.PatternRouteCapabilities.WwpReapplyRegenerates);
            Assert.False(PatternApplyService.PatternRouteCapabilities.IsRegeneratingReapply(Wwp()));
        }

        [Fact]
        public void RouteCapabilities_Json_Reports_Both_Routes_And_Only_Reasons_For_Unsupported_Ones()
        {
            var json = PatternApplyService.PatternRouteCapabilities.ToJson(NonWwp());

            Assert.True((bool)json["firstApply"]!["supported"]!);
            Assert.Null(json["firstApply"]!["reason"]);

            Assert.False((bool)json["reapply"]!["supported"]!);
            Assert.False(string.IsNullOrWhiteSpace((string)json["reapply"]!["reason"]));
        }

        /// <summary>
        /// The rule the diagnose composition relies on: a pattern whose reapply is
        /// blocked must not be reported as a regenerating reapply, and one whose reapply
        /// regenerates must not be blocked. <see cref="PatternRouteCapabilityTests"/>
        /// guards the capability values; this guards that the diagnose remediation and
        /// the capability report cannot disagree about the same pattern, which is the
        /// contradiction #352 reported.
        /// </summary>
        [Fact]
        public void Diagnose_Remediate_Route_Must_Agree_With_The_Reported_Reapply_Capability()
        {
            var pattern = NonWwp();
            var caps = typeof(PatternApplyService.PatternRouteCapabilities);

            // The composition reads both from the same source, so by construction they
            // cannot disagree. If a future change re-introduces an independent constant
            // in the diagnose body, this equality stops holding.
            bool reapplySupported = PatternApplyService.PatternRouteCapabilities.IsSupported(
                pattern, PatternApplyService.PatternRoute.Reapply, out _);
            bool remediateClaimsRegeneration = PatternApplyService.PatternRouteCapabilities.IsRegeneratingReapply(pattern);

            Assert.NotNull(caps);
            Assert.Equal(reapplySupported, remediateClaimsRegeneration);
        }

        [Fact]
        public void RouteUnsupported_Hint_Does_Not_Promise_Regeneration_Or_Structural_Authoring()
        {
            var rejection = PatternApplyService.TryBuildRouteUnsupportedRejection(
                "SyntheticObject", NonWwp(), PatternApplyService.PatternRoute.Reapply);
            Assert.NotNull(rejection);

            var payload = JObject.Parse(rejection);
            string hint = (string)(payload["error"]?["hint"] ?? payload["hint"]);
            Assert.False(string.IsNullOrWhiteSpace(hint));

            // What #352 reported was a hint that AFFIRMS regeneration or structural
            // authoring. A hint that denies them is the correction, so the guard is on
            // the affirmative promise, not on the word.
            Assert.DoesNotContain("are regenerated", hint.ToLowerInvariant());
            Assert.DoesNotContain("regenerates the", hint.ToLowerInvariant());
            Assert.DoesNotContain("structural authoring is possible", hint.ToLowerInvariant());
            // Issue #478: the hint must not make the blanket claim that derived objects
            // are unconditionally not regenerated either way across all environments.
            Assert.DoesNotContain("are not regenerated either way", hint.ToLowerInvariant());
            // Reapply refusal must be stated explicitly.
            Assert.Contains("reapply remains unsupported", hint.ToLowerInvariant());
            // The permitted, qualified action must still be discoverable.
            Assert.Contains("genexus_read", hint, StringComparison.Ordinal);
            Assert.Contains("genexus_edit", hint, StringComparison.Ordinal);
        }

        [Fact]
        public void RouteUnsupported_Guidance_Distinguishes_Reapply_Refusal_From_Property_Edits_And_Directs_To_Ide()
        {
            var rejection = PatternApplyService.TryBuildRouteUnsupportedRejection(
                "SyntheticObject", NonWwp(), PatternApplyService.PatternRoute.Reapply);
            Assert.NotNull(rejection);

            var payload = JObject.Parse(rejection);
            string hint = (string)(payload["error"]?["hint"] ?? payload["hint"]);

            // Issue #478 acceptance criteria:
            // 1. Reapply of this route remains unsupported.
            Assert.Contains("reapply remains unsupported", hint, StringComparison.OrdinalIgnoreCase);

            // 2. Existing property edits may persist the instance, but derived-object
            // update is not measured by the edit call and results vary by environment.
            Assert.Contains("genexus_edit part=PatternInstance is possible", hint, StringComparison.Ordinal);
            Assert.Contains("not measured by the edit call", hint, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("vary by environment", hint, StringComparison.OrdinalIgnoreCase);

            // 3. If derived objects do not follow, apply via GeneXus IDE.
            Assert.Contains("apply the pattern in the genexus ide", hint, StringComparison.OrdinalIgnoreCase);

            // Shared guidance constant equality check across rejection and diagnostic.
            Assert.Equal(PatternApplyService.RouteUnsupportedGuidance, hint);
        }

        [Fact]
        public void Supported_Route_Produces_No_Rejection()
        {
            Assert.Null(PatternApplyService.TryBuildRouteUnsupportedRejection(
                "SyntheticObject", Wwp(), PatternApplyService.PatternRoute.Reapply));
            Assert.Null(PatternApplyService.TryBuildRouteUnsupportedRejection(
                "SyntheticObject", NonWwp(), PatternApplyService.PatternRoute.FirstApply));
        }
    }
}
