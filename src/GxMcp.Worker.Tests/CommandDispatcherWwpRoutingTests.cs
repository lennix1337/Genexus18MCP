using System.Reflection;
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

        [Fact]
        public void The_template_action_reaches_the_block_through_the_command_table()
        {
            // The request has the shape the Gateway sends: the module is the method, and the action sits at the top level.
            var dispatcher = Dispatcher();
            SetField(dispatcher, "_doctorService", new DoctorService(null, null));
            SetField(dispatcher, "_commandTable", typeof(CommandDispatcher)
                .GetMethod("BuildCommandTable", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(dispatcher, null));
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "synth-template-1",
                ["method"] = "Doctor",
                ["action"] = "WwpTemplates",
                ["target"] = "_self",
                ["params"] = new JObject
                {
                    ["module"] = "Doctor",
                    ["action"] = "WwpTemplates",
                    ["target"] = "_self",
                    ["busyWaitMs"] = 3500
                }
            };

            var response = JObject.Parse(dispatcher.Dispatch(request.ToString()));

            Assert.Equal("DoctorWwpTemplatesOk", response["code"]?.ToString());
            Assert.Equal(JTokenType.Boolean, response["result"]?["available"]?.Type);
        }

        private static void SetField(object obj, string name, object value) =>
            obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
    }
}
