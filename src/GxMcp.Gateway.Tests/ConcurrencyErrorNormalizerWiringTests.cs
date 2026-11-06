using System;
using System.Globalization;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Task 6.7 wiring of ConcurrencyErrorNormalizer (RFC #854, Requirements 9.2 and 12): shared-mode Worker responses
    // are normalized before the tool shaping runs, and an isolated Worker response is the same object, untouched.
    // Typed launch refusals use the SharedWorkerLaunchException paths covered by SharedWorkerRefusalRouteTests.
    public sealed class ConcurrencyErrorNormalizerWiringTests
    {
        private const string KbPath = "C:/KB/Orders";

        [Fact]
        public void SharedWorker_ResponseWithAWorkerConflict_IsNormalizedBeforeShaping()
        {
            var worker = new WorkerProcess(ConfigFor("shared-host"), new KbHandle("orders", KbPath));
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "w1",
                ["result"] = new JObject
                {
                    ["status"] = "Error",
                    ["code"] = ConcurrencyErrorCatalog.TargetLockedByOtherAgent,
                    ["message"] = "Object 'Customer' part 'Rules' is locked by agent 'gateway:owner-b'.",
                    ["lockHolder"] = "gateway:owner-b",
                    ["lockExpiresAt"] = DateTime.UtcNow.AddMinutes(2).ToString("o", CultureInfo.InvariantCulture),
                    ["target"] = "Customer",
                },
            };
            var request = new JObject
            {
                ["method"] = "Write",
                ["action"] = "Rules",
                ["target"] = "Customer",
                ["params"] = new JObject { ["part"] = "Rules" },
            };

            var normalized = worker.NormalizeConcurrencyError(response, request);

            var result = (JObject)normalized["result"]!;
            Assert.Equal(ConcurrencyDispatchState.DeliveredRejected, (string?)result["dispatchState"]);
            Assert.True((bool)result["retryable"]!);
            Assert.Equal(JTokenType.Integer, result["retryAfterMs"]!.Type);
            Assert.Equal("Rules", (string?)result["part"]);
        }

        [Fact]
        public void IsolatedWorker_ResponseWithAWorkerConflict_IsTheSameObjectUntouched()
        {
            var worker = new WorkerProcess(ConfigFor("isolated"), new KbHandle("orders", KbPath));
            var response = JObject.Parse(
                "{\"jsonrpc\":\"2.0\",\"id\":\"i1\",\"result\":{\"status\":\"Error\",\"code\":\"TargetLockedByOtherAgent\","
                + "\"message\":\"Object 'Customer' part 'Source' is locked by agent 'gateway:owner-b'.\","
                + "\"lockHolder\":\"gateway:owner-b\",\"lockExpiresAt\":\"2026-03-04T10:05:00.0000000Z\",\"target\":\"Customer\"}}");
            string before = response.ToString(Formatting.None);

            var normalized = worker.NormalizeConcurrencyError(response, new JObject { ["method"] = "Write", ["action"] = "Source" });

            Assert.Same(response, normalized);
            Assert.Equal(before, normalized.ToString(Formatting.None));
        }

        private static Configuration ConfigFor(string sharingMode) =>
            new Configuration { Server = new ServerConfig { WorkerSharingMode = sharingMode } };
    }
}
