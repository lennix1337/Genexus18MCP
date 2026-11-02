using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A genexus_properties set can run a WorkWithPlus apply-on-save inside the call, which took 50 to 54 s on a real host. With a
    /// client progress token the set may wait up to 90 s for its answer. Without one, the no-progress cap stays at 50 s for every
    /// tool, because the client closes the transport after about 60 s of silence. Reads keep their windows.
    /// </summary>
    public class PropertySetWindowTests
    {
        private static JObject SetArgs() => new JObject { ["action"] = "set" };

        [Fact]
        public void PropertySetWithProgressGetsTheNinetySecondCeilingInsteadOfTheDefault()
        {
            Assert.Equal(90000, Program.GetToolTimeoutMs("genexus_properties", SetArgs()));
        }

        [Fact]
        public void PropertyGetKeepsTheDefaultCeiling()
        {
            Assert.Equal(60000, Program.GetToolTimeoutMs("genexus_properties", new JObject { ["action"] = "get" }));
        }

        [Fact]
        public void WithoutAProgressTokenEveryCallIsCappedAtTheFiftySecondWindowIncludingAPropertySet()
        {
            Assert.Equal(50000, Program.ResolveNoProgressTimeoutMs(Program.GetToolTimeoutMs("genexus_properties", SetArgs())));
            Assert.Equal(50000, Program.ResolveNoProgressTimeoutMs(60000));
            Assert.Equal(30000, Program.ResolveNoProgressTimeoutMs(30000));
        }

        [Fact]
        public void TheProgressCeilingOutlastsARealWorkWithPlusSaveAndTheNoProgressWindowIsFiftySeconds()
        {
            // The save took 50 to 54 s on a real host: the ceiling used with a progress token must outlast it. The window used
            // without the token is the existing one, not a new value.
            Assert.True(McpRouter.PropertySetWaitSecondsWithProgress > 54, "The progress ceiling must outlast the 54 s save.");
            Assert.Equal(50, McpRouter.SafeLongPollSecondsWithoutProgress);
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
