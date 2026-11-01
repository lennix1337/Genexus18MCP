using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A genexus_properties set can run a WorkWithPlus apply-on-save inside the call, which took 50 to 54 s on a real host. Its
    /// synchronous wait therefore has a longer ceiling and a longer no-progress window than the default. Reads and other
    /// property actions keep the windows they had.
    /// </summary>
    public class PropertySetWindowTests
    {
        private static JObject SetArgs() => new JObject { ["action"] = "set" };

        [Fact]
        public void PropertySetGetsTheNinetySecondCeilingInsteadOfTheDefault()
        {
            Assert.Equal(90000, Program.GetToolTimeoutMs("genexus_properties", SetArgs()));
        }

        [Fact]
        public void PropertyGetKeepsTheDefaultCeiling()
        {
            Assert.Equal(60000, Program.GetToolTimeoutMs("genexus_properties", new JObject { ["action"] = "get" }));
        }

        [Fact]
        public void TheNoProgressCapGivesAPropertySetItsWindowAndEveryOtherCallTheSafeWindow()
        {
            Assert.Equal(90000, Program.ResolveNoProgressTimeoutMs("genexus_properties", SetArgs(), 90000));
            Assert.Equal(50000, Program.ResolveNoProgressTimeoutMs("genexus_properties", new JObject { ["action"] = "get" }, 60000));
            Assert.Equal(50000, Program.ResolveNoProgressTimeoutMs("genexus_query", null, 60000));
        }

        [Fact]
        public void TheWindowOfAPropertySetOutlastsARealWorkWithPlusSaveAndStaysUnderTheClientGiveUp()
        {
            // The save took 50 to 54 s on a real host; the client gives up around 120 s. The window has to sit between the two.
            int windowSeconds = McpRouter.PropertySetSafeLongPollSecondsWithoutProgress;

            Assert.True(windowSeconds > 54, "The window must outlast the 54 s save.");
            Assert.True(windowSeconds < 120, "The window must end before the client gives up.");
        }

        [Fact]
        public void OnlyTheSetActionOfPropertiesIsAPropertySetCall()
        {
            Assert.True(Program.IsPropertySetCall("genexus_properties", SetArgs()));
            Assert.False(Program.IsPropertySetCall("genexus_properties", new JObject { ["action"] = "get" }));
            Assert.False(Program.IsPropertySetCall("genexus_edit", SetArgs()));
            Assert.False(Program.IsPropertySetCall("genexus_properties", null));
        }
    }
}
