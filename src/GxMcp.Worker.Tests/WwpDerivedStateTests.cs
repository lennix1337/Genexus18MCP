using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The comparison of two captures of the derived objects: what changed, what appeared, what
    // vanished, and whether a "helpers only" outcome still holds.
    public class WwpDerivedStateTests
    {
        private static WwpObjectState State(string name, string type, Guid guid, params string[] partsAndTexts)
        {
            var state = new WwpObjectState { Name = name, Type = type, Guid = guid, QualifiedName = name };
            for (int i = 0; i < partsAndTexts.Length; i += 2) state.Parts[partsAndTexts[i]] = partsAndTexts[i + 1];
            return state;
        }

        private static readonly Guid Helper = Guid.NewGuid();
        private static readonly Guid Grid = Guid.NewGuid();

        [Fact]
        public void AnIdenticalCapture_HasNoChange()
        {
            var before = new[] { State("LoadDVComboX", "Procedure", Helper, "Rules", "parm(&A);") };
            var after = new[] { State("LoadDVComboX", "Procedure", Helper, "Rules", "parm(&A);") };

            WwpDerivedDiff diff = WwpDerivedDiff.Compare(before, after);

            Assert.Empty(diff.Changed);
            Assert.Empty(diff.Created);
            Assert.Empty(diff.Removed);
            Assert.Equal(1, diff.Unchanged);
        }

        [Fact]
        public void OnlyThePartsWhoseTextDiffers_AreChanged()
        {
            var before = new[] { State("LoadDVComboX", "Procedure", Helper, "Rules", "parm(&A);", "Source", "x", "Variables", "&A : N") };
            var after = new[] { State("LoadDVComboX", "Procedure", Helper, "Rules", "parm(&A, &B);", "Source", "x", "Variables", "&A : N\r\n&B : N") };

            WwpDerivedDiff diff = WwpDerivedDiff.Compare(before, after);

            Assert.Equal(new[] { "Rules", "Variables" }, diff.Changed.Single().Parts.ToArray());
        }

        [Fact]
        public void ObjectsAreMatchedByGuid_SoARenameIsAChangeNotACreateAndARemove()
        {
            var before = new[] { State("LoadDVComboOld", "Procedure", Helper, "Rules", "a") };
            var after = new[] { State("LoadDVComboNew", "Procedure", Helper, "Rules", "a") };

            WwpDerivedDiff diff = WwpDerivedDiff.Compare(before, after);

            Assert.Empty(diff.Created);
            Assert.Empty(diff.Removed);
            Assert.Equal(1, diff.Unchanged);
        }

        [Fact]
        public void CreatedAndRemovedObjects_AreReported()
        {
            var before = new[] { State("Gone", "Procedure", Guid.NewGuid(), "Source", "x") };
            var after = new[] { State("New", "Procedure", Guid.NewGuid(), "Source", "y") };

            WwpDerivedDiff diff = WwpDerivedDiff.Compare(before, after);

            Assert.Equal("New", diff.Created.Single().Name);
            Assert.Equal("Gone", diff.Removed.Single().Name);
        }

        [Fact]
        public void APartPresentOnOneSideOnly_CountsAsChanged_EvenWhenItsTextIsEmpty()
        {
            var before = new[] { State("P", "Procedure", Helper, "Source", "x") };
            var after = new[] { State("P", "Procedure", Helper, "Source", "x", "Rules", "parm(&A);", "Variables", string.Empty) };

            Assert.Equal(new[] { "Rules", "Variables" }, WwpDerivedDiff.Compare(before, after).Changed.Single().Parts.ToArray());
        }

        [Fact]
        public void HelpersOnlyScope_RefusesAnyOtherChangedCreatedOrRemovedObject()
        {
            var before = new[]
            {
                State("LoadDVComboX", "Procedure", Helper, "Rules", "a"),
                State("WWX", "WebPanel", Grid, "Events", "e"),
                State("Gone", "Procedure", Guid.NewGuid(), "Source", "x")
            };
            var after = new[]
            {
                State("LoadDVComboX", "Procedure", Helper, "Rules", "b"),
                State("WWX", "WebPanel", Grid, "Events", "e2"),
                State("New", "Procedure", Guid.NewGuid(), "Source", "y")
            };
            ISet<string> allowed = new HashSet<string>(new[] { "Procedure:LoadDVComboX" }, StringComparer.OrdinalIgnoreCase);

            List<string> outOfScope = WwpDerivedDiff.Compare(before, after).OutOfScope(allowed);

            Assert.Equal(new[] { "WebPanel:WWX", "Procedure:New", "Procedure:Gone" }, outOfScope.ToArray());
        }

        [Fact]
        public void HelpersOnlyScope_AcceptsAChangeConfinedToTheHelper()
        {
            var before = new[] { State("LoadDVComboX", "Procedure", Helper, "Rules", "a"), State("WWX", "WebPanel", Grid, "Events", "e") };
            var after = new[] { State("LoadDVComboX", "Procedure", Helper, "Rules", "b"), State("WWX", "WebPanel", Grid, "Events", "e") };

            Assert.Empty(WwpDerivedDiff.Compare(before, after)
                .OutOfScope(new HashSet<string>(new[] { "Procedure:LoadDVComboX" }, StringComparer.OrdinalIgnoreCase)));
        }

        [Fact]
        public void TheFingerprint_DependsOnEveryPartAndIsIndependentOfInsertionOrder()
        {
            WwpObjectState a = State("P", "Procedure", Helper, "Source", "x", "Rules", "r");
            WwpObjectState b = State("P", "Procedure", Helper, "Rules", "r", "Source", "x");
            WwpObjectState c = State("P", "Procedure", Helper, "Rules", "r2", "Source", "x");

            Assert.Equal(a.Fingerprint(), b.Fingerprint());
            Assert.NotEqual(a.Fingerprint(), c.Fingerprint());
        }
    }
}
