using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The one save of a property write, run with fake save flags. The apply-on-save flag is on while the save runs, and it is given back
    /// when the save returns or throws, and only when the rule says so. The four property write paths save through this seam, so these
    /// tests stand for all four.
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
        public void TheSuppressionIsOnWhileTheSaveRunsAndOffAfterIt()
        {
            var flags = new FakeFlags(false);
            bool duringSave = false;

            PropertyApplyOnSaveRule.SaveWith(flags, true, null, new[] { "Description" }, () => duringSave = flags.Current);

            Assert.True(duringSave);
            Assert.False(flags.Current);
        }

        [Fact]
        public void TheSuppressionIsGivenBackWhenTheSaveThrows()
        {
            var flags = new FakeFlags(false);

            Assert.Throws<InvalidOperationException>(() => PropertyApplyOnSaveRule.SaveWith(
                flags, true, null, new[] { "Description" }, () => throw new InvalidOperationException("the save failed")));

            Assert.False(flags.Current);
        }

        [Fact]
        public void AFlagThatWasAlreadySetStaysSetAfterTheSave()
        {
            var flags = new FakeFlags(true);
            bool duringSave = false;

            PropertyApplyOnSaveRule.SaveWith(flags, true, null, new[] { "Description" }, () => duringSave = flags.Current);

            Assert.True(duringSave);
            Assert.True(flags.Current);
        }

        [Fact]
        public void NothingIsSuppressedForAnObjectWithoutApplyOnSave()
        {
            var flags = new FakeFlags(false);
            bool duringSave = true;

            PropertyApplyOnSaveRule.SaveWith(flags, false, null, new[] { "Description" }, () => duringSave = flags.Current);

            Assert.False(duringSave);
        }

        [Fact]
        public void NothingIsSuppressedForAControlWrite()
        {
            var flags = new FakeFlags(false);
            bool duringSave = true;

            PropertyApplyOnSaveRule.SaveWith(flags, true, "Grid1", new[] { "Caption" }, () => duringSave = flags.Current);

            Assert.False(duringSave);
        }

        [Fact]
        public void NothingIsSuppressedForABatchThatChangesAnApplyFlag()
        {
            var flags = new FakeFlags(false);
            bool duringSave = true;

            PropertyApplyOnSaveRule.SaveWith(flags, true, null, new[] { ApplyFlag, "Description" }, () => duringSave = flags.Current);

            Assert.False(duringSave);
        }

        [Fact]
        public void ASaveWithoutAnObjectRunsAsItWas()
        {
            bool ran = false;

            PropertyApplyOnSaveRule.Save(null, null, new[] { "Description" }, () => ran = true);

            Assert.True(ran);
        }
    }
}
