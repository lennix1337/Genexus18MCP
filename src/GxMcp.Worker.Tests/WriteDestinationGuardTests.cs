using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Collections;

namespace GxMcp.Worker.Tests
{
    public class WriteDestinationGuardTests
    {
        private static int handlerCalls;

        [Theory]
        [InlineData("list", true)]
        [InlineData("set_active", false)]
        public void DispatcherUsesNestedVersionActionBeforeChoosingReadBypass(string innerAction, bool allowed)
        {
            using (new PinnedEnvironment())
            {
                handlerCalls = 0;
                var dispatcher = (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
                var kb = (KbService)FormatterServices.GetUninitializedObject(typeof(KbService));
                SetField(kb, "_kbLock", new object());
                SetField(dispatcher, "_kbService", kb);
                var tableField = typeof(CommandDispatcher).GetField("_commandTable", BindingFlags.NonPublic | BindingFlags.Instance);
                var table = (IDictionary)Activator.CreateInstance(tableField.FieldType);
                var handlerType = tableField.FieldType.GetGenericArguments()[1];
                table.Add("kbversion", Delegate.CreateDelegate(handlerType, typeof(WriteDestinationGuardTests).GetMethod(nameof(ObservationHandler), BindingFlags.NonPublic | BindingFlags.Static)));
                tableField.SetValue(dispatcher, table);
                var request = new JObject { ["method"] = "kbversion", ["action"] = "Run",
                    ["params"] = new JObject { ["action"] = "list", ["params"] = new JObject { ["action"] = innerAction } } };
                var response = JObject.Parse(dispatcher.Dispatch(request.ToString()));
                Assert.Equal(allowed ? 1 : 0, handlerCalls);
                if (allowed) Assert.Equal("GuardTestObservation", (string)response["code"]);
                else Assert.Equal("WriteDestinationKbMismatch", (string)response["error"]["code"]);
            }
        }

        private static string ObservationHandler(JObject request, string method, string action, string target, string payload, JObject args)
        {
            handlerCalls++;
            return "{\"status\":\"ok\",\"code\":\"GuardTestObservation\"}";
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void DispatcherRejectsBeforeAnyHandlerWithNoOpenKb(bool nested, bool dryRun)
        {
            using (new PinnedEnvironment())
            {
                // No handler table/services are installed. Reaching a handler would fail,
                // instead of returning the destination error. Auto-open state stays untouched.
                var dispatcher = (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
                var kb = (KbService)FormatterServices.GetUninitializedObject(typeof(KbService));
                SetField(kb, "_kbLock", new object());
                SetField(dispatcher, "_kbService", kb);
                var args = new JObject { ["dryRun"] = dryRun, ["requireObjectSave"] = true,
                    ["expectedVersion"] = "stale-content-token", ["GXMCP_EXPECTED_KB_VERSION"] = "Untrusted" };
                var request = new JObject { ["method"] = "write", ["action"] = "Source", ["target"] = "TestPanel",
                    ["params"] = nested ? new JObject { ["params"] = args } : args };
                Assert.False(dispatcher.IsThreadSafe(request.ToString()));
                var result = JObject.Parse(dispatcher.Dispatch(request.ToString()));
                Assert.Equal("WriteDestinationKbMismatch", (string)result["error"]["code"]);
                Assert.False((bool)result["persisted"]);
                Assert.Equal("Development", (string)result["expectedKbVersion"]);
                Assert.False(kb.IsOpen);
                Assert.False(kb.IsInitializing);
                Assert.Equal(0, GetField(kb, "_autoOpenFailCount"));
            }
        }

        [Fact]
        public void OpenRejectsWrongPathBeforeFileOrSdkInspection()
        {
            using (new PinnedEnvironment())
            {
                // Uninitialized service: any access to its collaborators would fail.
                var kb = (KbService)FormatterServices.GetUninitializedObject(typeof(KbService));
                var response = JObject.Parse(kb.OpenKB(@"C:\NotAnExistingKb\Other"));
                Assert.Equal("WriteDestinationKbMismatch", (string)response["error"]["code"]);
            }
        }

        [Theory]
        [InlineData("write", "Source", false)]
        [InlineData("kbversion", "Run", false)]
        [InlineData("health", "GetReport", true)]
        [InlineData("health", "Unknown", false)]
        [InlineData("doctor", "Run", true)]
        [InlineData("build", "Status", true)]
        [InlineData("kb", "GetIndexStatus", true)]
        [InlineData("search", "Unknown", false)]
        [InlineData("search", "SearchSource", false)]
        [InlineData("search", "Query", true)]
        public void ConfiguredSdkFenceStaysOnStaWhileStatusRemainsAvailable(string method, string action, bool threadSafe)
        {
            using (new PinnedEnvironment())
            {
                var dispatcher = (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
                Assert.Equal(threadSafe, dispatcher.IsThreadSafe(new JObject { ["method"] = method, ["action"] = action }.ToString()));
                if (threadSafe) Assert.Null(WriteDestinationGuard.CheckCommand(null, method, action, null));
            }
        }

        private static void SetField(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static object GetField(object obj, string name) => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private sealed class PinnedEnvironment : IDisposable
        {
            private readonly string path = Environment.GetEnvironmentVariable(WriteDestinationGuard.PathVariable);
            private readonly string version = Environment.GetEnvironmentVariable(WriteDestinationGuard.VersionVariable);
            private readonly string autoOpen = Environment.GetEnvironmentVariable("GX_KB_PATH");
            internal PinnedEnvironment()
            {
                Environment.SetEnvironmentVariable(WriteDestinationGuard.PathVariable, @"C:\KB\Test");
                Environment.SetEnvironmentVariable(WriteDestinationGuard.VersionVariable, "Development");
                Environment.SetEnvironmentVariable("GX_KB_PATH", null);
            }
            public void Dispose()
            {
                Environment.SetEnvironmentVariable(WriteDestinationGuard.PathVariable, path);
                Environment.SetEnvironmentVariable(WriteDestinationGuard.VersionVariable, version);
                Environment.SetEnvironmentVariable("GX_KB_PATH", autoOpen);
            }
        }

        [Theory]
        [InlineData("Development", false, true)]
        [InlineData("Other", false, false)]
        [InlineData("Development", true, false)]
        [InlineData("Development", null, false)]
        public void ExplicitRecoveryCannotChangePinOrAutoUpdate(string target, bool? update, bool allowed)
        {
            var args = new JObject { ["targetVersion"] = target, ["autoUpdate"] = update };
            Assert.Equal(allowed, WriteDestinationGuard.CheckActivation("Development", args) == null);
        }
        [Theory]
        [InlineData(null, null)]
        [InlineData(@"C:\KB\Test", "Development")]
        public void DisabledOrMatchingPinsAllow(string path, string version)
        {
            Assert.Null(WriteDestinationGuard.Check(path, version, @"c:\kb\test\", "development", false, false));
        }

        [Theory]
        [InlineData(null, "Development", "WriteDestinationConfigurationIncomplete")]
        [InlineData(@"C:\KB\Test", null, "WriteDestinationConfigurationIncomplete")]
        [InlineData(@"C:\KB\Other", "Development", "WriteDestinationKbMismatch")]
        [InlineData(@"C:\KB\Test", "Another", "WriteDestinationVersionMismatch")]
        public void RejectsWrongOrPartialDestination(string path, string version, string code)
        {
            var response = JObject.Parse(WriteDestinationGuard.Check(path, version, @"C:\KB\Test", "Development", false, false));
            Assert.Equal(code, (string)response["error"]["code"]);
            Assert.False((bool)response["persisted"]);
        }

        [Fact]
        public void RestartOrChangeAfterPreviewCannotReuseDestination()
        {
            Assert.Null(WriteDestinationGuard.Check(@"C:\KB\Test", "Development", @"C:\KB\Test\kb.gxw", "Development", false, false));
            Assert.Contains("WriteDestinationVersionMismatch", WriteDestinationGuard.Check(@"C:\KB\Test", "Development", @"C:\KB\Test", "Release", true, false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(null)]
        public void FrozenOrUnknownWritabilityFailsClosed(bool? frozen)
        {
            Assert.Contains("WriteDestinationVersionNotWritable", WriteDestinationGuard.Check(@"C:\KB\Test", "Development", @"C:\KB\Test", "Development", frozen, false));
        }

        [Fact]
        public void OpenChecksPathBeforeVersionIsAvailable()
        {
            Assert.Null(WriteDestinationGuard.Check(@"C:\KB\Test", "Development", @"C:\KB\Test", null, null, true));
            Assert.Contains("WriteDestinationKbMismatch", WriteDestinationGuard.Check(@"C:\KB\Test", "Development", @"C:\KB\Other", null, null, true));
        }

        [Theory]
        [InlineData("write", "Read")]
        [InlineData("sdkprobe", "Read")]
        [InlineData("batch", "Read")]
        [InlineData("unknown", "Read")]
        [InlineData("read", "FutureMutation")]
        public void UnknownOrScriptCommandsDoNotBypassFence(string method, string action)
        {
            Assert.False(WriteDestinationGuard.IsObservation(method, action, new JObject { ["dryRun"] = true }));
        }
    }
}
