using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The restoring save of a rollback keeps the pattern from running on save for an object-level write, even when the batch
    /// changed a pattern flag. A control write is not suppressed. The rule is free of SDK types, so it is tested here.
    /// </summary>
    public class PropertyApplyOnSaveRuleRollbackTests
    {
        [Fact]
        public void AnObjectLevelRollbackIsSuppressedOnAnObjectWithApplyOnSaveOn()
        {
            Assert.True(PropertyApplyOnSaveRule.ShouldSuppressRollback(null, true));
            Assert.True(PropertyApplyOnSaveRule.ShouldSuppressRollback(string.Empty, true));
        }

        [Fact]
        public void AControlRollbackIsNotSuppressed()
        {
            Assert.False(PropertyApplyOnSaveRule.ShouldSuppressRollback("Layout", true));
        }

        [Fact]
        public void AnObjectWithoutApplyOnSaveIsNotSuppressed()
        {
            Assert.False(PropertyApplyOnSaveRule.ShouldSuppressRollback(null, false));
        }
    }
}
