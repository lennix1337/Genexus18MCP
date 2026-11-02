using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The one save of a property write, run with fake save flags. The seam saves the object itself, then runs the caller's commit work, and
    /// the apply-on-save flag is on for both: it is given back when they return or throw, and only when the rule says so. The four property
    /// write paths save through this seam, so these tests stand for all four.
    /// </summary>
    public class PropertySaveSeamTests
    {
        private const string Flag = "SkipApplyPattern";
        private const string ApplyFlag = "Apply:WorkWithPlus";

        private sealed class FakeFlags : ISaveFlags
        {
            private readonly Dictionary<string, bool> _values = new Dictionary<string, bool>();

            internal FakeFlags(bool initial)
            {
                _values[Flag] = initial;
            }

            internal bool Current => _values[Flag];

            public bool Get(string name) => _values.TryGetValue(name, out bool value) && value;

            public void Set(string name, bool value) => _values[name] = value;
        }

        [Fact]
        public void TheSuppressionIsOnWhileTheObjectIsSavedAndWhileTheCommitRuns()
        {
            var flags = new FakeFlags(false);
            bool duringSave = false;
            bool duringCommit = false;

            PropertyApplyOnSaveRule.SaveWith(flags, true, null, new[] { "Description" },
                () => duringSave = flags.Current,
                () => duringCommit = flags.Current);

            Assert.True(duringSave);
            Assert.True(duringCommit);
            Assert.False(flags.Current);
        }

        [Fact]
        public void TheObjectIsSavedBeforeTheCommitWork()
        {
            var order = new List<string>();

            PropertyApplyOnSaveRule.SaveWith(new FakeFlags(false), true, null, new[] { "Description" },
                () => order.Add("save"),
                () => order.Add("commit"));

            Assert.Equal(new[] { "save", "commit" }, order);
        }

        [Fact]
        public void TheSuppressionIsGivenBackWhenTheSaveThrows()
        {
            var flags = new FakeFlags(false);

            Assert.Throws<InvalidOperationException>(() => PropertyApplyOnSaveRule.SaveWith(flags, true, null, new[] { "Description" },
                () => throw new InvalidOperationException("the save failed"),
                () => { }));

            Assert.False(flags.Current);
        }

        [Fact]
        public void TheSuppressionIsGivenBackWhenTheCommitThrows()
        {
            var flags = new FakeFlags(false);

            Assert.Throws<InvalidOperationException>(() => PropertyApplyOnSaveRule.SaveWith(flags, true, null, new[] { "Description" },
                () => { },
                () => throw new InvalidOperationException("the commit failed")));

            Assert.False(flags.Current);
        }

        [Fact]
        public void AFlagThatWasAlreadySetStaysSetAfterTheSave()
        {
            var flags = new FakeFlags(true);
            bool duringSave = false;

            PropertyApplyOnSaveRule.SaveWith(flags, true, null, new[] { "Description" }, () => duringSave = flags.Current, () => { });

            Assert.True(duringSave);
            Assert.True(flags.Current);
        }

        [Fact]
        public void NothingIsSuppressedForAnObjectWithoutApplyOnSave()
        {
            var flags = new FakeFlags(false);
            bool duringSave = true;

            PropertyApplyOnSaveRule.SaveWith(flags, false, null, new[] { "Description" }, () => duringSave = flags.Current, () => { });

            Assert.False(duringSave);
        }

        [Fact]
        public void NothingIsSuppressedForAControlWrite()
        {
            var flags = new FakeFlags(false);
            bool duringSave = true;

            PropertyApplyOnSaveRule.SaveWith(flags, true, "Grid1", new[] { "Caption" }, () => duringSave = flags.Current, () => { });

            Assert.False(duringSave);
        }

        [Fact]
        public void NothingIsSuppressedForABatchThatChangesAnApplyFlag()
        {
            var flags = new FakeFlags(false);
            bool duringSave = true;

            PropertyApplyOnSaveRule.SaveWith(flags, true, null, new[] { ApplyFlag, "Description" }, () => duringSave = flags.Current, () => { });

            Assert.False(duringSave);
        }

        [Fact]
        public void ASaveWithoutAnObjectIsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => PropertyApplyOnSaveRule.Save(null, null, new[] { "Description" }, () => { }));
        }
    }
}
