using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // v2.6.6 Stream F: SystemRouter forwards `wait` and `since` from the
    // genexus_lifecycle tool args onto the worker command envelope so the
    // worker-side GetStatusWait blocks on the per-task signal.
    public class GatewayLifecycleWaitProxyTests
    {
        [Fact]
        public void Status_ForwardsWaitAndSince()
        {
            var router = new SystemRouter();
            var args = new JObject
            {
                ["action"] = "status",
                ["target"] = "abcd1234",
                ["wait"] = 30,
                ["since"] = "Generating|2|0|1|Running"
            };

            var command = JObject.FromObject(router.ConvertToolCall("genexus_lifecycle", args)!);

            Assert.Equal("Build", command["module"]?.ToString());
            Assert.Equal("Status", command["action"]?.ToString());
            Assert.Equal("abcd1234", command["target"]?.ToString());
            Assert.Equal(30, command["wait"]?.ToObject<int>());
            Assert.Equal("Generating|2|0|1|Running", command["since"]?.ToString());
        }

        [Fact]
        public void Status_DefaultsWaitToZeroWhenAbsent()
        {
            var router = new SystemRouter();
            var args = new JObject
            {
                ["action"] = "status",
                ["target"] = "abcd1234"
            };

            var command = JObject.FromObject(router.ConvertToolCall("genexus_lifecycle", args)!);
            Assert.Equal(0, command["wait"]?.ToObject<int>());
        }

        [Fact]
        public void Status_ClampsWaitTo600()
        {
            var router = new SystemRouter();
            var args = new JObject
            {
                ["action"] = "status",
                ["target"] = "abcd1234",
                ["wait"] = 9999
            };

            var command = JObject.FromObject(router.ConvertToolCall("genexus_lifecycle", args)!);
            Assert.Equal(600, command["wait"]?.ToObject<int>());
        }

        [Fact]
        public void Status_MapsWaitSecondsAlias()
        {
            var router = new SystemRouter();
            var command = JObject.FromObject(router.ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "status",
                ["target"] = "abcd1234",
                ["wait_seconds"] = 45
            })!);
            Assert.Equal(45, command["wait"]?.ToObject<int>());
        }

        [Fact]
        public void Status_ClampsNegativeWaitToZero()
        {
            var router = new SystemRouter();
            var args = new JObject
            {
                ["action"] = "status",
                ["target"] = "abcd1234",
                ["wait"] = -5
            };
            var command = JObject.FromObject(router.ConvertToolCall("genexus_lifecycle", args)!);
            Assert.Equal(0, command["wait"]?.ToObject<int>());
        }

        [Fact]
        public void Status_DefaultsUntilToTerminalForWaitUntilDone()
        {
            var router = new SystemRouter();
            var command = JObject.FromObject(router.ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "status",
                ["target"] = "abcd1234",
                ["wait_until_done"] = true
            })!);

            Assert.Equal("terminal", command["until"]?.ToString());
            Assert.Equal(600, command["wait"]?.ToObject<int>());
        }

        [Fact]
        public void Status_UsesJobIdAndStripsOperationPrefixForLegacyWorkerFallback()
        {
            var router = new SystemRouter();
            var command = JObject.FromObject(router.ConvertToolCall("genexus_lifecycle", new JObject
            {
                ["action"] = "status",
                ["job_id"] = "op:abcd1234"
            })!);

            Assert.Equal("Build", command["module"]?.ToString());
            Assert.Equal("Status", command["action"]?.ToString());
            Assert.Equal("abcd1234", command["target"]?.ToString());
        }
    }
}
