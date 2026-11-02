using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A property write suppresses the pattern that runs on save only for an object-level write on an object whose apply-on-save
    /// flag is on, and never when the batch changes that flag. These tests pin the rule without a Knowledge Base.
    /// </summary>
    public class PropertyApplyOnSaveRuleTests
    {
        [Fact]
        public void ObjectLevelWriteOnAnObjectWithApplyOnSaveOnIsSuppressed()
        {
            Assert.True(PropertyApplyOnSaveRule.ShouldSuppress(null, true, new[] { "MasterPage" }));
        }

        [Fact]
        public void AnEmptyControlNameIsAnObjectLevelWrite()
        {
            Assert.True(PropertyApplyOnSaveRule.ShouldSuppress(string.Empty, true, new[] { "MasterPage" }));
        }

        [Fact]
        public void ControlWriteIsNotSuppressed()
        {
            Assert.False(PropertyApplyOnSaveRule.ShouldSuppress("Grid1", true, new[] { "Caption" }));
        }

        [Fact]
        public void ObjectWithoutApplyOnSaveIsNotSuppressed()
        {
            Assert.False(PropertyApplyOnSaveRule.ShouldSuppress(null, false, new[] { "MasterPage" }));
        }

        [Fact]
        public void BatchThatChangesTheApplyFlagIsNotSuppressed()
        {
            Assert.False(PropertyApplyOnSaveRule.ShouldSuppress(null, true, new[] { "MasterPage", "Apply:07135890-56fc-489b-b408-063722fa9f7d" }));
        }

        [Fact]
        public void TheFlagCheckIgnoresCaseAndSurroundingSpaces()
        {
            Assert.False(PropertyApplyOnSaveRule.ShouldSuppress(null, true, new[] { " apply:07135890-56fc-489b-b408-063722fa9f7d" }));
        }

        [Fact]
        public void ANameThatOnlyContainsTheFlagPrefixInsideIsStillSuppressed()
        {
            Assert.True(PropertyApplyOnSaveRule.ShouldSuppress(null, true, new[] { "MasterPage", "NotApply:x" }));
        }
    }
}
