using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A property that had no explicit value before a batch is reset to its default by the rollback, not written back with its
    /// effective value, which would make it explicit. A reset the SDK refuses is reported as not restored. These tests pin the
    /// choice of step and the confirmation without a Knowledge Base.
    /// </summary>
    public class PropertyRollbackResetTests
    {
        [Fact]
        public void ADefaultPropertyIsResetAndAnExplicitOneIsWrittenBack()
        {
            var resets = new List<string>();
            var writes = new List<string>();
            var previous = new Dictionary<string, string> { ["MasterPage"] = "Default", ["Description"] = "Before" };

            var written = PropertyRollbackRestore.ResetOrWriteBack(
                new[] { "MasterPage", "Description" }, previous, new HashSet<string> { "MasterPage" },
                name => true, resets.Add, (name, value) => writes.Add(name + "=" + value));

            Assert.Equal(new[] { "MasterPage" }, resets);
            Assert.Equal(new[] { "Description=Before" }, writes);
            Assert.Equal(new[] { "MasterPage", "Description" }, written);
        }

        [Fact]
        public void ADefaultPropertyTheSdkWillNotResetIsWrittenBackInsteadOfLeftBehind()
        {
            var resets = new List<string>();
            var writes = new List<string>();
            var previous = new Dictionary<string, string> { ["MasterPage"] = "Default" };

            var written = PropertyRollbackRestore.ResetOrWriteBack(
                new[] { "MasterPage" }, previous, new HashSet<string> { "MasterPage" },
                name => false, resets.Add, (name, value) => writes.Add(name + "=" + value));

            Assert.Empty(resets);
            Assert.Equal(new[] { "MasterPage=Default" }, writes);
            Assert.Equal(new[] { "MasterPage" }, written);
        }

        [Fact]
        public void ADefaultPropertyWithoutAPreviousValueIsNotWrittenWhenTheSdkWillNotReset()
        {
            var resets = new List<string>();
            var writes = new List<string>();
            var previous = new Dictionary<string, string> { ["MasterPage"] = null };

            var written = PropertyRollbackRestore.ResetOrWriteBack(
                new[] { "MasterPage" }, previous, new HashSet<string> { "MasterPage" },
                name => false, resets.Add, (name, value) => writes.Add(name + "=" + value));

            Assert.Empty(resets);
            Assert.Empty(writes);
            Assert.Empty(written);
        }

        [Fact]
        public void AResetThatThrowsIsLoggedAndTheOthersStillGo()
        {
            var writes = new List<string>();
            var previous = new Dictionary<string, string> { ["A"] = "a", ["B"] = "b" };

            var written = PropertyRollbackRestore.ResetOrWriteBack(
                new[] { "A", "B" }, previous, new HashSet<string> { "A" },
                name => true, name => throw new System.InvalidOperationException("refused"), (name, value) => writes.Add(name));

            Assert.Equal(new[] { "B" }, written);
            Assert.Equal(new[] { "B" }, writes);
        }

        [Fact]
        public void AResetPropertyThatIsDefaultAgainCountsAsRestored()
        {
            var previous = new Dictionary<string, string> { ["MasterPage"] = "Default" };

            var report = PropertyRollbackRestore.ConfirmWithDefaults(
                new[] { "MasterPage" }, previous, new HashSet<string> { "MasterPage" }, new[] { "MasterPage" },
                name => "Default", name => true);

            Assert.Equal(new[] { "MasterPage" }, report.Restored);
            Assert.Empty(report.NotRestored);
        }

        [Fact]
        public void AResetPropertyThatBecameExplicitAgainIsNotRestored()
        {
            var previous = new Dictionary<string, string> { ["MasterPage"] = "Default" };

            var report = PropertyRollbackRestore.ConfirmWithDefaults(
                new[] { "MasterPage" }, previous, new HashSet<string> { "MasterPage" }, new[] { "MasterPage" },
                name => "Default", name => false);

            Assert.Empty(report.Restored);
            Assert.Equal(new[] { "MasterPage" }, report.NotRestored);
        }

        [Fact]
        public void AnExplicitPropertyKeepsTheRuleOfTheOriginalConfirmation()
        {
            var previous = new Dictionary<string, string> { ["Description"] = "Before" };

            var report = PropertyRollbackRestore.ConfirmWithDefaults(
                new[] { "Description" }, previous, new HashSet<string>(), new[] { "Description" },
                name => "Changed", name => true);

            Assert.Empty(report.Restored);
            Assert.Equal(new[] { "Description" }, report.NotRestored);
        }
    }
}
