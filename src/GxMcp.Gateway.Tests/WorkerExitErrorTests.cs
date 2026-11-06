using System.Collections.Generic;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Program.BuildWorkerExitError: the JSON-RPC error a pending request gets when its Worker exits.
    //
    // Isolated_Mode (Issue #692) is pinned byte for byte, through the original three-argument
    // overload and through the shared-aware overload with sharedWorker=false.
    //
    // Shared_Host_Mode (RFC #854, Requirements 8.4, 12.3, 12.4): the overload
    //   BuildWorkerExitError(kbAlias, stopReason, delivered, sharedWorker: true, method, action, queueWaitMs)
    // returns { code=-32603, message containing "crashed/exited", data } where data carries every
    // property of ConcurrencyErrorCatalog.Build(SHARED_WORKER_RESTARTED, null, { DispatchState,
    // OperationClass = WorkerCommandCatalog.Classify(method, action), QueueWaitMs }) plus stopReason.
    // DispatchState is not-delivered when no byte reached the Worker and delivered-unanswered otherwise.
    public sealed class WorkerExitErrorTests
    {
        private const string Alias = "kba";
        private const int QueueWaitMs = 5000;

        // ---------------------------------------------------------------- Isolated_Mode pin

        private const string IsolatedNotDeliveredIdle =
            "{\"code\":-32603,\"message\":\"Worker for KB 'kba' crashed/exited (IdleTimeout) before the command was delivered; it was not executed.\","
            + "\"data\":{\"dispatchState\":\"not-delivered\",\"stopReason\":\"IdleTimeout\"}}";

        private const string IsolatedDeliveredNone =
            "{\"code\":-32603,\"message\":\"Worker for KB 'kba' crashed/exited (None) after the command was delivered and before it answered; it may have executed. Read the target back before retrying.\","
            + "\"data\":{\"dispatchState\":\"delivered-unanswered\",\"reconcileRequired\":true,\"stopReason\":\"None\"}}";

        private const string IsolatedDeliveredWedged =
            "{\"code\":-32603,\"message\":\"Worker for KB 'kba' crashed/exited (Wedged) after the command was delivered and before it answered; it may have executed. Read the target back before retrying.\","
            + "\"data\":{\"dispatchState\":\"delivered-unanswered\",\"reconcileRequired\":true,\"stopReason\":\"Wedged\"}}";

        public static IEnumerable<object[]> IsolatedCases()
        {
            yield return new object[] { WorkerStopReason.IdleTimeout, false, IsolatedNotDeliveredIdle };
            yield return new object[] { WorkerStopReason.None, true, IsolatedDeliveredNone };
            yield return new object[] { WorkerStopReason.Wedged, true, IsolatedDeliveredWedged };
        }

        [Theory]
        [MemberData(nameof(IsolatedCases))]
        public void IsolatedExitErrorIsByteIdentical(WorkerStopReason stopReason, bool delivered, string expected)
        {
            Assert.Equal(expected, Program.BuildWorkerExitError(Alias, stopReason, delivered).ToString(Formatting.None));
        }

        [Theory]
        [MemberData(nameof(IsolatedCases))]
        public void SharedAwareOverloadKeepsTheIsolatedOutputForAnIsolatedWorker(WorkerStopReason stopReason, bool delivered, string expected)
        {
            JObject error = Program.BuildWorkerExitError(Alias, stopReason, delivered,
                sharedWorker: false, method: "kb", action: "Open", queueWaitMs: QueueWaitMs);

            Assert.Equal(expected, error.ToString(Formatting.None));
        }

        // ---------------------------------------------------------------- Shared_Host_Mode

        public static IEnumerable<object?[]> Commands()
        {
            var entries = WorkerCommandCatalog.Entries;
            var read = entries.First(e => WorkerCommandCatalog.Classify(e.Method, e.Action) == OperationClass.Read);
            var write = entries.First(e => WorkerCommandCatalog.Classify(e.Method, e.Action) == OperationClass.Write);
            yield return new object?[] { read.Method, read.Action };
            yield return new object?[] { write.Method, write.Action };
            yield return new object?[] { "no_such_method", "NoSuchAction" };
            yield return new object?[] { null, null };
        }

        [Theory]
        [MemberData(nameof(Commands))]
        public void SharedDeliveredUnansweredMatchesTheCatalog(string? method, string? action)
        {
            JObject error = Program.BuildWorkerExitError(Alias, WorkerStopReason.None, delivered: true,
                sharedWorker: true, method, action, QueueWaitMs);

            JObject data = AssertCatalogShape(error, ConcurrencyDispatchState.DeliveredUnanswered, method, action);
            bool isRead = WorkerCommandCatalog.Classify(method, action) == OperationClass.Read;
            Assert.Equal(isRead, data.Value<bool>("retryable"));
            Assert.Equal(!isRead, data.Value<bool>("reconcileRequired"));
            Assert.Equal("None", (string?)data["stopReason"]);
            Assert.False(Program.ShouldRedispatchUndeliveredCommand(Wrap(error), 0));
        }

        [Theory]
        [MemberData(nameof(Commands))]
        public void SharedNotDeliveredMatchesTheCatalogAndIsAlwaysRetryable(string? method, string? action)
        {
            JObject error = Program.BuildWorkerExitError(Alias, WorkerStopReason.IdleTimeout, delivered: false,
                sharedWorker: true, method, action, QueueWaitMs);

            JObject data = AssertCatalogShape(error, ConcurrencyDispatchState.NotDelivered, method, action);
            Assert.True(data.Value<bool>("retryable"));
            Assert.Equal("IdleTimeout", (string?)data["stopReason"]);
            Assert.True(Program.ShouldRedispatchUndeliveredCommand(Wrap(error), 0));
        }

        [Fact]
        public void SharedRetryableFollowsTheOperationClassForEveryCatalogEntry()
        {
            var commands = WorkerCommandCatalog.Entries
                .Select(e => (e.Method, e.Action))
                .Concat(new[] { ("unknown_method", "Whatever"), ("ping", "UnlistedAction") });

            foreach (var (method, action) in commands)
            {
                bool isRead = WorkerCommandCatalog.Classify(method, action) == OperationClass.Read;

                JObject delivered = Program.BuildWorkerExitError(Alias, WorkerStopReason.ExplicitClose, true, true, method, action, QueueWaitMs);
                Assert.Equal(ConcurrencyErrorCatalog.SharedWorkerRestarted, (string?)delivered["data"]?["code"]);
                Assert.True(isRead == delivered["data"]!.Value<bool>("retryable"), method + "/" + action + ": retryable must be " + isRead);

                JObject notDelivered = Program.BuildWorkerExitError(Alias, WorkerStopReason.ExplicitClose, false, true, method, action, QueueWaitMs);
                Assert.Equal(ConcurrencyErrorCatalog.SharedWorkerRestarted, (string?)notDelivered["data"]?["code"]);
                Assert.True(notDelivered["data"]!.Value<bool>("retryable"), method + "/" + action + ": not-delivered must be retryable");
            }
        }

        private static JObject AssertCatalogShape(JObject error, string dispatchState, string? method, string? action)
        {
            JObject expected = ConcurrencyErrorCatalog.Build(
                ConcurrencyErrorCatalog.SharedWorkerRestarted,
                null,
                new ConcurrencyErrorFields
                {
                    DispatchState = dispatchState,
                    OperationClass = WorkerCommandCatalog.Classify(method, action),
                    QueueWaitMs = QueueWaitMs
                });

            Assert.Equal(-32603, error.Value<int>("code"));
            Assert.Contains("crashed/exited", (string?)error["message"]);
            var data = Assert.IsType<JObject>(error["data"]);
            foreach (JProperty property in expected.Properties())
                Assert.True(JToken.DeepEquals(property.Value, data[property.Name]),
                    "data." + property.Name + " expected " + property.Value + " but was " + data[property.Name]);
            return data;
        }

        private static JObject Wrap(JObject error) => new JObject { ["jsonrpc"] = "2.0", ["id"] = "r1", ["error"] = error };
    }
}
