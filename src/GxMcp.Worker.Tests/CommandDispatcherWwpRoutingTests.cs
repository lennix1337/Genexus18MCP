using System.Runtime.Serialization;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class CommandDispatcherWwpRoutingTests
    {
        private static CommandDispatcher Dispatcher()
        {
            return (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
        }

        [Fact]
        public void The_template_action_is_not_thread_safe_and_stays_on_the_STA_thread()
        {
            Assert.False(Dispatcher().IsThreadSafe(new JObject { ["method"] = "doctor", ["action"] = "WwpTemplates" }));
        }

        [Theory]
        [InlineData("Diagnose")]
        [InlineData("")]
        public void The_full_doctor_envelope_keeps_its_thread_safe_path(string action)
        {
            Assert.True(Dispatcher().IsThreadSafe(new JObject { ["method"] = "doctor", ["action"] = action }));
        }
    }
}
