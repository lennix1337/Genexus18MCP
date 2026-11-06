using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Shared-mode concurrency error normalization (task 6.4; Requirements 9.2, 12.1-12.7; design
    // "ConcurrencyErrorNormalizer.cs (new)" and "WorkerProcess.cs (shared branch)"). These tests are written before
    // the implementation (task 6.7) and pin the seam contract below; that task implements exactly these types,
    // members and names.
    //
    // ---- Entry point (task 6.7, src/GxMcp.Gateway/ConcurrencyErrorNormalizer.cs) ----
    //   internal static class ConcurrencyErrorNormalizer   (namespace GxMcp.Gateway)
    //   internal static JObject Normalize(JObject response, JObject? request, Configuration config, Func<DateTime> utcNow)
    //     response  One of: a JSON-RPC response with "result" (the Worker or broker tool result), a JSON-RPC
    //               response with "error" (its "data" object is the error), or a bare tool result. A JSON-RPC
    //               response is any object with a "jsonrpc" property.
    //     request   The Worker RPC request (Program.BuildWorkerRpcRequest shape: method, action, target, params).
    //               Read for the part (TargetLockedByOtherAgent) and the Operation_Class (SHARED_WORKER_RESTARTED).
    //               Null is allowed: part then defaults to "Source" and the Operation_Class to Write.
    //     config    The shared-mode-only switch and the queue wait.
    //     utcNow    The clock for retryAfterMs of TargetLockedByOtherAgent.
    //   Never mutates response or request.
    //
    // ---- Shared-mode-only switch ----
    //   Normalization runs only when config.Server.WorkerSharingMode equals "shared-host" (OrdinalIgnoreCase). Any
    //   other value, including null, empty, "isolated" and a null Server, returns the response argument itself (same
    //   reference), untouched, so Isolated_Mode output is byte-identical to the Previous_Release. Task 6.7 calls
    //   Normalize on the shared-mode response path only (SendWorkerCommandAsync, before onSuccess, where both the
    //   response and the Worker request are in hand); the switch is a second guard, not the only one.
    //
    // ---- Locating the concurrency error object ----
    //   X = response["result"] (JSON-RPC result), response["error"]["data"] (JSON-RPC error), or response (bare).
    //   The error object is X["error"] when it is an object whose string "code" is a Concurrency_Error_Table code
    //   (canonical McpResponse.Err shape and the broker Busy_Envelope), otherwise X itself when X["code"] is such a
    //   code (the flat WritePipeline.AdvisoryLockCheck shape). Codes match ordinal. Anything else - success
    //   results, non-table codes, SHARED_WORKER_RECOVERY_FAILED - is returned unchanged (equal JSON).
    //
    // ---- Exact fields written into the error object (shared mode, table code found) ----
    //   The normalizer calls ConcurrencyErrorCatalog.Build(code, reason, fields) with
    //     QueueWaitMs     = config.EffectiveSharedHost.QueueWaitMs
    //     RetryAfterMs    = the raiser's integer retryAfterMs when present (Build clamps it), else null
    //     LockRemainingMs = TargetLockedByOtherAgent only: lockExpiresAt (ISO-8601 UTC) - utcNow() in whole
    //                       milliseconds, at least 0; null, missing or unparseable lockExpiresAt -> 0
    //     DispatchState   = SHARED_WORKER_RESTARTED only: the raiser's value when "not-delivered" or
    //                       "delivered-unanswered", else "delivered-unanswered" (every request written to the pipe
    //                       counts as delivered); null for every other row, so the row value wins
    //     OperationClass  = SHARED_WORKER_RESTARTED only: WorkerCommandCatalog.Classify(request.method, request.action)
    //     HolderPid       = the raiser's integer holderPid, else null
    //   and copies from the built object into the error object, overwriting the raiser's values:
    //     "retryable", "dispatchState", "hint", "retryAfterMs" (only when retryable; removed otherwise), "reason"
    //     (only for rows with reasons). "code" is unchanged. "message" keeps the raiser's non-empty message, else
    //     the catalog message. Every other existing key (target, lockHolder, lockExpiresAt, nextSteps, holderPid,
    //     queue details, ...) and every key outside the error object (status, expectedVersion, currentVersion, ...)
    //     is kept unchanged.
    //   TargetLockedByOtherAgent also gets "part": the error's own non-empty part, else request.params.part, else
    //   request.part, else "Source" (the part WriteService checks when none is named).
    //   A row with reasons whose error carries no reason, or a reason outside the row, is left unchanged: the
    //   normalizer never invents a reason (the raiser bug stays visible).
    //   Normalize is idempotent.
    //   Worker conflict codes (TargetLockedByOtherAgent, StaleObject, LockCheckFailed, VersionCheckUnavailable)
    //   therefore always leave with dispatchState "delivered-rejected" (Requirement 12.7).
    //
    // ---- Terse projection (task 6.7, McpRouter.TrimErrorEnvelope) ----
    //   The keep-list that today applies to WorkerBusy with a dispatchState applies to every Concurrency_Error_Table
    //   code with a dispatchState, and additionally keeps "lockHolder" and "lockExpiresAt", so the default terse
    //   output still carries retryable, dispatchState and retryAfterMs (Requirement 12.2, 12.6). Errors without a
    //   dispatchState (Isolated_Mode) keep their existing terse shape.
    //
    // ---- SHARED_WORKER_RECOVERY_FAILED (decision) ----
    //   It has no Concurrency_Error_Table row (Requirement 7 criterion 9) and SharedWorkerRecoveryFailure.Build
    //   already writes retryable=false, dispatchState="not-delivered" and its own hint. Normalize passes it through
    //   unchanged; it is not added to the catalog. A typed launch refusal reaches the caller through the
    //   SharedWorkerLaunchException paths in Program.GatewayTools.cs and Program.ProcessStdioLine (issue #914).
    public sealed class ConcurrencyErrorNormalizerTests
    {
        private const int QueueWait = 30000;
        private static readonly DateTime Now = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);
        private static readonly Func<DateTime> Clock = () => Now;

        // ---------------------------------------------------------------
        // Requirement 12.1, 12.2, 12.3, 12.5, 12.6: every table code
        // ---------------------------------------------------------------

        [Fact]
        public void SharedMode_EveryTableCode_CarriesTheCatalogCoreFields()
        {
            var config = SharedConfig();
            int checkedCases = 0;
            foreach (var c in EveryTableCase())
            {
                var raw = new JObject { ["code"] = c.Code, ["message"] = "raw message" };
                if (c.Reason != null) raw["reason"] = c.Reason;
                if (c.DispatchState != null) raw["dispatchState"] = c.DispatchState;
                if (c.HolderPid.HasValue) raw["holderPid"] = c.HolderPid.Value;
                if (c.Code == ConcurrencyErrorCatalog.TargetLockedByOtherAgent)
                    raw["lockExpiresAt"] = Now.AddMilliseconds(5000).ToString("o", CultureInfo.InvariantCulture);
                var response = RpcResult(new JObject { ["status"] = "error", ["error"] = raw });

                var normalized = ConcurrencyErrorNormalizer.Normalize(response, c.Request, config, Clock);

                string label = c.Code + "/" + (c.Reason ?? "<none>") + "/" + (c.DispatchState ?? "<row>")
                    + "/" + (c.Request?["method"]?.ToString() ?? "<no request>");
                var error = Assert.IsType<JObject>(normalized["result"]?["error"]);
                var expected = ConcurrencyErrorCatalog.Build(c.Code, c.Reason, new ConcurrencyErrorFields
                {
                    QueueWaitMs = QueueWait,
                    LockRemainingMs = c.Code == ConcurrencyErrorCatalog.TargetLockedByOtherAgent ? 5000 : (long?)null,
                    DispatchState = c.DispatchState,
                    OperationClass = c.OperationClass,
                    HolderPid = c.HolderPid,
                });

                Assert.True(error["retryable"]?.Type == JTokenType.Boolean, label + ": retryable must be a JSON boolean");
                Assert.Equal((bool)expected["retryable"]!, (bool)error["retryable"]!);
                Assert.Equal((string?)expected["dispatchState"], (string?)error["dispatchState"]);
                Assert.Contains((string?)error["dispatchState"], new[]
                {
                    ConcurrencyDispatchState.NotDelivered,
                    ConcurrencyDispatchState.DeliveredUnanswered,
                    ConcurrencyDispatchState.DeliveredRejected,
                });
                Assert.Equal((string?)expected["hint"], (string?)error["hint"]);
                Assert.False(string.IsNullOrWhiteSpace((string?)error["hint"]), label + ": hint must be non-empty");
                Assert.Equal(c.Reason, (string?)error["reason"]);
                Assert.Equal(c.Code, (string?)error["code"]);
                Assert.Equal("raw message", (string?)error["message"]);

                if ((bool)error["retryable"]!)
                {
                    Assert.True(error["retryAfterMs"]?.Type == JTokenType.Integer, label + ": retryAfterMs must be an integer");
                    long bound = c.Code == ConcurrencyErrorCatalog.TargetLockedByOtherAgent ? 5000 : QueueWait;
                    Assert.InRange((long)error["retryAfterMs"]!, 0, bound);
                }
                else
                {
                    Assert.Null(error["retryAfterMs"]);
                }
                checkedCases++;
            }
            Assert.True(checkedCases >= 20, "every row, reason and dispatch state must be covered");
        }

        [Fact]
        public void SharedMode_RetryAfterZero_IsKeptAndPresent()
        {
            var raw = new JObject
            {
                ["code"] = ConcurrencyErrorCatalog.WorkerBusy,
                ["reason"] = "queue-full",
                ["message"] = "raw message",
                ["retryAfterMs"] = 0,
            };

            var normalized = ConcurrencyErrorNormalizer.Normalize(
                ToolError(raw), WriteRequest("Customer", "Source"), SharedConfig(), Clock);

            var error = (JObject)normalized["error"]!;
            Assert.Equal(JTokenType.Integer, error["retryAfterMs"]!.Type);
            Assert.Equal(0, (int)error["retryAfterMs"]!);
        }

        [Fact]
        public void SharedMode_RetryAfterAboveQueueWait_IsClampedToQueueWait()
        {
            var raw = new JObject
            {
                ["code"] = ConcurrencyErrorCatalog.SharedWorkerBackpressure,
                ["message"] = "raw message",
                ["retryAfterMs"] = QueueWait + 9000,
            };

            var normalized = ConcurrencyErrorNormalizer.Normalize(ToolError(raw), null, SharedConfig(), Clock);

            Assert.Equal(QueueWait, (int)normalized["error"]!["retryAfterMs"]!);
        }

        [Fact]
        public void SharedMode_NonRetryableCode_DropsARaiserRetryAfter()
        {
            var raw = new JObject
            {
                ["code"] = ConcurrencyErrorCatalog.StaleObject,
                ["message"] = "raw message",
                ["retryAfterMs"] = 500,
                ["retryable"] = true,
            };

            var normalized = ConcurrencyErrorNormalizer.Normalize(ToolError(raw), WriteRequest("Customer", "Source"), SharedConfig(), Clock);

            var error = (JObject)normalized["error"]!;
            Assert.False((bool)error["retryable"]!);
            Assert.Null(error["retryAfterMs"]);
        }

        // ---------------------------------------------------------------
        // Requirement 9.2, 12.6, 12.7: TargetLockedByOtherAgent from the Worker
        // ---------------------------------------------------------------

        [Fact]
        public void SharedMode_WorkerTargetLocked_GainsPartRetryAfterAndDeliveredRejected()
        {
            var flat = WorkerTargetLocked("Customer", "Source", Now.AddMilliseconds(42000));
            var response = RpcResult(flat);

            var normalized = ConcurrencyErrorNormalizer.Normalize(response, WriteRequest("Customer", "Rules"), SharedConfig(), Clock);

            var result = Assert.IsType<JObject>(normalized["result"]);
            Assert.Equal("Error", (string?)result["status"]);
            Assert.Equal(ConcurrencyErrorCatalog.TargetLockedByOtherAgent, (string?)result["code"]);
            Assert.Equal("Rules", (string?)result["part"]);
            Assert.Equal(42000, (long)result["retryAfterMs"]!);
            Assert.True((bool)result["retryable"]!);
            Assert.Equal(ConcurrencyDispatchState.DeliveredRejected, (string?)result["dispatchState"]);
            Assert.Equal(CatalogHint(ConcurrencyErrorCatalog.TargetLockedByOtherAgent), (string?)result["hint"]);
            Assert.Null(result["reason"]);
            // The Worker's holder, expiry, target and message survive.
            Assert.Equal(flat["lockHolder"]!.ToString(), (string?)result["lockHolder"]);
            Assert.Equal(flat["lockExpiresAt"]!.ToString(), result["lockExpiresAt"]!.ToString());
            Assert.Equal("Customer", (string?)result["target"]);
            Assert.Equal(flat["message"]!.ToString(), (string?)result["message"]);
        }

        [Fact]
        public void SharedMode_TargetLockedPart_ComesFromTheErrorThenParamsThenTopLevelThenSource()
        {
            var config = SharedConfig();
            DateTime expiry = Now.AddSeconds(10);

            var ownPart = WorkerTargetLocked("Customer", "Source", expiry);
            ownPart["part"] = "Events";
            Assert.Equal("Events", (string?)ConcurrencyErrorNormalizer.Normalize(ownPart, WriteRequest("Customer", "Rules"), config, Clock)["part"]);

            Assert.Equal("Rules", (string?)ConcurrencyErrorNormalizer.Normalize(
                WorkerTargetLocked("Customer", "Source", expiry), WriteRequest("Customer", "Rules"), config, Clock)["part"]);

            var topLevelOnly = new JObject { ["method"] = "Write", ["action"] = "Patch", ["target"] = "Customer", ["part"] = "Conditions", ["params"] = new JObject() };
            Assert.Equal("Conditions", (string?)ConcurrencyErrorNormalizer.Normalize(
                WorkerTargetLocked("Customer", "Source", expiry), topLevelOnly, config, Clock)["part"]);

            Assert.Equal("Source", (string?)ConcurrencyErrorNormalizer.Normalize(
                WorkerTargetLocked("Customer", "Source", expiry), null, config, Clock)["part"]);
        }

        [Fact]
        public void SharedMode_TargetLockedWithExpiredMissingOrUnparseableExpiry_RetriesAfterZero()
        {
            var config = SharedConfig();
            var request = WriteRequest("Customer", "Source");

            var expired = WorkerTargetLocked("Customer", "Source", Now.AddSeconds(-3));
            var nullExpiry = WorkerTargetLocked("Customer", "Source", Now);
            nullExpiry["lockExpiresAt"] = JValue.CreateNull();
            var missing = WorkerTargetLocked("Customer", "Source", Now);
            missing.Remove("lockExpiresAt");
            var garbage = WorkerTargetLocked("Customer", "Source", Now);
            garbage["lockExpiresAt"] = "not-a-timestamp";

            foreach (var input in new[] { expired, nullExpiry, missing, garbage })
            {
                var normalized = ConcurrencyErrorNormalizer.Normalize(input, request, config, Clock);
                Assert.Equal(JTokenType.Integer, normalized["retryAfterMs"]!.Type);
                Assert.Equal(0, (long)normalized["retryAfterMs"]!);
                Assert.True((bool)normalized["retryable"]!);
            }
        }

        [Fact]
        public void SharedMode_TargetLockedRetryAfter_FollowsTheInjectedClock()
        {
            DateTime expiry = Now.AddMinutes(5);
            var input = WorkerTargetLocked("Customer", "Source", expiry);

            var early = ConcurrencyErrorNormalizer.Normalize(input, null, SharedConfig(), () => Now);
            var later = ConcurrencyErrorNormalizer.Normalize(input, null, SharedConfig(), () => Now.AddMinutes(4));

            Assert.Equal(300000, (long)early["retryAfterMs"]!);
            Assert.Equal(60000, (long)later["retryAfterMs"]!);
        }

        // ---------------------------------------------------------------
        // Requirement 12.7: Worker conflict codes are delivered-rejected
        // ---------------------------------------------------------------

        [Fact]
        public void SharedMode_WorkerStaleObject_IsDeliveredRejectedNotRetryableAndKeepsBothTokens()
        {
            var worker = WorkerCanonicalError(ConcurrencyErrorCatalog.StaleObject, "Customer",
                "The object changed since you last read it.",
                "Re-read with genexus_read to get the current content and versionToken.",
                new JObject { ["expectedVersion"] = "v-base-1", ["currentVersion"] = "v-current-2" });

            var normalized = ConcurrencyErrorNormalizer.Normalize(RpcResult(worker), WriteRequest("Customer", "Source"), SharedConfig(), Clock);

            var result = (JObject)normalized["result"]!;
            var error = (JObject)result["error"]!;
            Assert.Equal(ConcurrencyDispatchState.DeliveredRejected, (string?)error["dispatchState"]);
            Assert.False((bool)error["retryable"]!);
            Assert.Null(error["retryAfterMs"]);
            Assert.Equal(CatalogHint(ConcurrencyErrorCatalog.StaleObject), (string?)error["hint"]);
            Assert.Equal("v-base-1", (string?)result["expectedVersion"]);
            Assert.Equal("v-current-2", (string?)result["currentVersion"]);
            Assert.Equal("error", (string?)result["status"]);
            Assert.Equal("Customer", (string?)result["target"]);
            Assert.NotNull(error["nextSteps"]);
        }

        [Theory]
        [InlineData("LockCheckFailed")]
        [InlineData("VersionCheckUnavailable")]
        public void SharedMode_WorkerCheckFailures_AreDeliveredRejectedRetryableWithRetryAfter(string code)
        {
            var worker = WorkerCanonicalError(code, "Customer", "The check could not be completed.", "Retry later.",
                new JObject { ["expectedVersion"] = "v-base-1", ["currentVersion"] = JValue.CreateNull() });

            var normalized = ConcurrencyErrorNormalizer.Normalize(worker, WriteRequest("Customer", "Source"), SharedConfig(), Clock);

            var error = (JObject)normalized["error"]!;
            Assert.Equal(ConcurrencyDispatchState.DeliveredRejected, (string?)error["dispatchState"]);
            Assert.True((bool)error["retryable"]!);
            Assert.Equal(JTokenType.Integer, error["retryAfterMs"]!.Type);
            Assert.InRange((long)error["retryAfterMs"]!, 0, QueueWait);
            Assert.Equal(CatalogHint(code), (string?)error["hint"]);
        }

        [Fact]
        public void SharedMode_FlatLockCheckFailedFromAdvisoryLockCheck_IsNormalizedInPlace()
        {
            var flat = new JObject
            {
                ["status"] = "Error",
                ["code"] = ConcurrencyErrorCatalog.LockCheckFailed,
                ["message"] = "The existing object lock is unreadable; the write was not attempted.",
                ["hint"] = "Repair or remove the lock only after confirming no writer is active.",
                ["retryable"] = true,
                ["target"] = "Customer",
            };

            var normalized = ConcurrencyErrorNormalizer.Normalize(flat, WriteRequest("Customer", "Source"), SharedConfig(), Clock);

            Assert.Equal(ConcurrencyDispatchState.DeliveredRejected, (string?)normalized["dispatchState"]);
            Assert.True((bool)normalized["retryable"]!);
            Assert.NotNull(normalized["retryAfterMs"]);
            Assert.Equal(CatalogHint(ConcurrencyErrorCatalog.LockCheckFailed), (string?)normalized["hint"]);
            Assert.Null(normalized["error"]);
        }

        [Fact]
        public void SharedMode_WorkerConflictCodeWithAnotherDispatchState_IsForcedToDeliveredRejected()
        {
            var raw = new JObject
            {
                ["code"] = ConcurrencyErrorCatalog.VersionCheckUnavailable,
                ["message"] = "raw message",
                ["dispatchState"] = ConcurrencyDispatchState.NotDelivered,
            };

            var normalized = ConcurrencyErrorNormalizer.Normalize(ToolError(raw), WriteRequest("Customer", "Source"), SharedConfig(), Clock);

            Assert.Equal(ConcurrencyDispatchState.DeliveredRejected, (string?)normalized["error"]!["dispatchState"]);
        }

        // ---------------------------------------------------------------
        // Requirement 12.3, 12.4: SHARED_WORKER_RESTARTED in a JSON-RPC error
        // ---------------------------------------------------------------

        [Fact]
        public void SharedMode_RestartedWithoutDispatchState_IsDeliveredUnansweredWithTheOperationClassRetryable()
        {
            var config = SharedConfig();
            var read = WorkerCommandCatalog.Entries.First(e => e.Class == OperationClass.Read);
            var write = WorkerCommandCatalog.Entries.First(e => e.Class == OperationClass.Write);

            foreach (var (entry, expectedRetryable) in new[] { (read, true), (write, false) })
            {
                var response = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "r1",
                    ["error"] = new JObject
                    {
                        ["code"] = -32603,
                        ["message"] = "Shared Worker disconnected.",
                        ["data"] = new JObject { ["code"] = ConcurrencyErrorCatalog.SharedWorkerRestarted, ["message"] = "raw message" },
                    },
                };
                var request = new JObject { ["method"] = entry.Method, ["action"] = entry.Action, ["params"] = new JObject() };

                var normalized = ConcurrencyErrorNormalizer.Normalize(response, request, config, Clock);

                var data = (JObject)normalized["error"]!["data"]!;
                Assert.Equal(ConcurrencyDispatchState.DeliveredUnanswered, (string?)data["dispatchState"]);
                Assert.Equal(expectedRetryable, (bool)data["retryable"]!);
                Assert.Equal(expectedRetryable, data["retryAfterMs"] != null);
                Assert.Equal(-32603, (int)normalized["error"]!["code"]!);
            }
        }

        [Fact]
        public void SharedMode_RestartedNotDelivered_StaysNotDeliveredAndRetryable()
        {
            var raw = new JObject
            {
                ["code"] = ConcurrencyErrorCatalog.SharedWorkerRestarted,
                ["message"] = "raw message",
                ["dispatchState"] = ConcurrencyDispatchState.NotDelivered,
            };
            var write = WorkerCommandCatalog.Entries.First(e => e.Class == OperationClass.Write);
            var request = new JObject { ["method"] = write.Method, ["action"] = write.Action, ["params"] = new JObject() };

            var normalized = ConcurrencyErrorNormalizer.Normalize(ToolError(raw), request, SharedConfig(), Clock);

            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string?)normalized["error"]!["dispatchState"]);
            Assert.True((bool)normalized["error"]!["retryable"]!);
            Assert.NotNull(normalized["error"]!["retryAfterMs"]);
        }

        [Fact]
        public void SharedMode_CatalogBuiltHostDisconnectError_IsUnchanged()
        {
            var write = WorkerCommandCatalog.Entries.First(e => e.Class == OperationClass.Write);
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "r2",
                ["error"] = WorkerProcess.BuildSharedHostDisconnectError("kba", write.Method, write.Action, QueueWait),
            };
            var request = new JObject { ["method"] = write.Method, ["action"] = write.Action, ["params"] = new JObject() };
            string before = Wire(response);

            var normalized = ConcurrencyErrorNormalizer.Normalize(response, request, SharedConfig(), Clock);

            Assert.Equal(before, Wire(normalized));
        }

        // ---------------------------------------------------------------
        // Pass-through, idempotence, no mutation
        // ---------------------------------------------------------------

        public static IEnumerable<object[]> IsolatedResponses()
        {
            // Worker TargetLockedByOtherAgent exactly as WritePipeline.AdvisoryLockCheck writes it today.
            yield return new object[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":\"i1\",\"result\":{\"status\":\"Error\",\"code\":\"TargetLockedByOtherAgent\","
                + "\"message\":\"Object 'Customer' part 'Source' is locked by agent 'gateway:owner-b'.\","
                + "\"hint\":\"Wait for the lock to expire or use force=true to override. Acquire the lock with genexus_multi_agent_lock action=acquire.\","
                + "\"lockHolder\":\"gateway:owner-b\",\"lockExpiresAt\":\"2026-03-04T10:05:00.0000000Z\",\"target\":\"Customer\"}}"
            };
            // Worker StaleObject in the canonical McpResponse.Err shape.
            yield return new object[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":\"i2\",\"result\":{\"status\":\"error\",\"target\":\"Customer\",\"error\":{\"code\":\"StaleObject\","
                + "\"message\":\"The object changed since you last read it.\",\"hint\":\"Re-read with genexus_read.\"},"
                + "\"expectedVersion\":\"v-base-1\",\"currentVersion\":\"v-current-2\"}}"
            };
            // Isolated Worker WorkerBusy, which carries no dispatchState.
            yield return new object[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":\"i3\",\"result\":{\"status\":\"error\",\"error\":{\"code\":\"WorkerBusy\","
                + "\"message\":\"The SDK command queue is full; the request was not started.\",\"retryAfterMs\":250,\"queue\":\"SDK command queue\",\"capacity\":64}}}"
            };
            // Isolated Worker exit (BuildWorkerExitError shape).
            yield return new object[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":\"i4\",\"error\":{\"code\":-32603,\"message\":\"Worker for KB 'kba' exited.\","
                + "\"data\":{\"dispatchState\":\"delivered-unanswered\",\"reconcileRequired\":true,\"stopReason\":\"None\"}}}"
            };
            // A success result.
            yield return new object[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":\"i5\",\"result\":{\"status\":\"Success\",\"name\":\"Customer\",\"part\":\"Source\",\"versionToken\":\"v-1\"}}"
            };
        }

        [Theory]
        [MemberData(nameof(IsolatedResponses))]
        public void IsolatedMode_Responses_PassThroughByteIdentical(string wire)
        {
            foreach (var config in new[] { IsolatedConfig("isolated"), IsolatedConfig(string.Empty), new Configuration { Server = null } })
            {
                var response = JObject.Parse(wire);
                string before = Wire(response);

                var normalized = ConcurrencyErrorNormalizer.Normalize(response, WriteRequest("Customer", "Rules"), config, Clock);

                Assert.Same(response, normalized);
                Assert.Equal(before, Wire(normalized));
                Assert.Equal(Wire(JObject.Parse(wire)), Wire(normalized));
            }
        }

        public static IEnumerable<object[]> SharedPassThroughResponses()
        {
            yield return new object[] { "{\"jsonrpc\":\"2.0\",\"id\":\"s1\",\"result\":{\"status\":\"Success\",\"name\":\"Customer\"}}" };
            yield return new object[] { "{\"jsonrpc\":\"2.0\",\"id\":\"s2\",\"result\":{\"status\":\"error\",\"error\":{\"code\":\"ObjectNotFound\",\"message\":\"No object.\"}}}" };
            // Case matters: a code that only differs in case is not a table code.
            yield return new object[] { "{\"status\":\"error\",\"error\":{\"code\":\"workerbusy\",\"reason\":\"queue-full\",\"message\":\"x\"}}" };
            // A row with reasons but no reason, and with a reason outside the row: never invented.
            yield return new object[] { "{\"status\":\"error\",\"error\":{\"code\":\"KB_POOL_FULL\",\"message\":\"x\"}}" };
            yield return new object[] { "{\"status\":\"error\",\"error\":{\"code\":\"WorkerBusy\",\"reason\":\"low-memory\",\"message\":\"x\"}}" };
            yield return new object[] { "{\"jsonrpc\":\"2.0\",\"id\":\"s3\",\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}" };
        }

        [Theory]
        [MemberData(nameof(SharedPassThroughResponses))]
        public void SharedMode_NonTableOrUnresolvableErrors_AreUnchanged(string wire)
        {
            var response = JObject.Parse(wire);

            var normalized = ConcurrencyErrorNormalizer.Normalize(response, WriteRequest("Customer", "Source"), SharedConfig(), Clock);

            Assert.Equal(Wire(JObject.Parse(wire)), Wire(normalized));
        }

        [Fact]
        public void SharedMode_RecoveryFailed_PassesThroughUnchanged()
        {
            var error = SharedWorkerRecoveryFailure.Build("kb-key-a", "shared-workers-v2", SharedWorkerRecoveryFailure.KillFailed, 4130, null);
            var response = RpcResult(new JObject { ["status"] = "error", ["code"] = SharedWorkerRecoveryFailure.Code, ["error"] = error });
            string before = Wire(response);

            var normalized = ConcurrencyErrorNormalizer.Normalize(response, null, SharedConfig(), Clock);

            Assert.Equal(before, Wire(normalized));
        }

        [Fact]
        public void SharedMode_Normalize_IsIdempotentAndDoesNotMutateItsInputs()
        {
            var config = SharedConfig();
            var request = WriteRequest("Customer", "Rules");
            var response = RpcResult(WorkerTargetLocked("Customer", "Source", Now.AddSeconds(30)));
            string responseBefore = Wire(response);
            string requestBefore = Wire(request);

            var once = ConcurrencyErrorNormalizer.Normalize(response, request, config, Clock);
            var twice = ConcurrencyErrorNormalizer.Normalize(once, request, config, Clock);

            Assert.Equal(responseBefore, Wire(response));
            Assert.Equal(requestBefore, Wire(request));
            Assert.NotEqual(responseBefore, Wire(once));
            Assert.Equal(Wire(once), Wire(twice));
        }

        // ---------------------------------------------------------------
        // Requirement 12.2, 12.6: the terse projection keeps the normalized fields
        // ---------------------------------------------------------------

        [Fact]
        public void TerseProjection_KeepsTheNormalizedFieldsOfAWorkerConflict()
        {
            var normalized = ConcurrencyErrorNormalizer.Normalize(
                WorkerTargetLocked("Customer", "Source", Now.AddMilliseconds(42000)), WriteRequest("Customer", "Rules"), SharedConfig(), Clock);

            var trimmed = McpRouter.TrimErrorEnvelope(normalized, verbose: false);

            Assert.Equal(ConcurrencyErrorCatalog.TargetLockedByOtherAgent, (string?)trimmed["code"]);
            Assert.True((bool)trimmed["retryable"]!);
            Assert.Equal(ConcurrencyDispatchState.DeliveredRejected, (string?)trimmed["dispatchState"]);
            Assert.Equal(42000, (long)trimmed["retryAfterMs"]!);
            Assert.Equal("Rules", (string?)trimmed["part"]);
            Assert.Equal("Customer", (string?)trimmed["target"]);
            Assert.Equal("gateway:owner-b", (string?)trimmed["lockHolder"]);
            Assert.NotNull(trimmed["lockExpiresAt"]);
        }

        [Fact]
        public void TerseProjection_KeepsDispatchStateAndRetryAfterOfACanonicalWorkerConflict()
        {
            var worker = WorkerCanonicalError(ConcurrencyErrorCatalog.VersionCheckUnavailable, "Customer", "raw message", "raw hint", null);
            var normalized = ConcurrencyErrorNormalizer.Normalize(worker, WriteRequest("Customer", "Source"), SharedConfig(), Clock);

            var trimmed = McpRouter.TrimErrorEnvelope(normalized, verbose: false);

            Assert.Equal(ConcurrencyDispatchState.DeliveredRejected, (string?)trimmed["dispatchState"]);
            Assert.NotNull(trimmed["retryAfterMs"]);
            Assert.True((bool)trimmed["retryable"]!);
        }

        // ---------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------

        private sealed class TableCase
        {
            public string Code = string.Empty;
            public string? Reason;
            public string? DispatchState;
            public OperationClass? OperationClass;
            public int? HolderPid;
            public JObject? Request;
        }

        private static IEnumerable<TableCase> EveryTableCase()
        {
            var read = WorkerCommandCatalog.Entries.First(e => e.Class == OperationClass.Read);
            var write = WorkerCommandCatalog.Entries.First(e => e.Class == OperationClass.Write);
            foreach (var row in ConcurrencyErrorCatalog.Rows)
            {
                var reasons = row.Reasons.Count == 0 ? new string?[] { null } : row.Reasons.Select(r => (string?)r).ToArray();
                foreach (var reason in reasons)
                {
                    int? holderPid = row.Code == ConcurrencyErrorCatalog.KbWorkerOwnedElsewhere ? 4242 : (int?)null;
                    if (row.DispatchState != null)
                    {
                        yield return new TableCase { Code = row.Code, Reason = reason, HolderPid = holderPid, Request = WriteRequest("Customer", "Source") };
                        continue;
                    }

                    yield return new TableCase
                    {
                        Code = row.Code, Reason = reason, DispatchState = ConcurrencyDispatchState.NotDelivered,
                        OperationClass = WorkerCommandCatalog.Classify(write.Method, write.Action),
                        Request = new JObject { ["method"] = write.Method, ["action"] = write.Action, ["params"] = new JObject() },
                    };
                    foreach (var entry in new[] { read, write })
                    {
                        yield return new TableCase
                        {
                            Code = row.Code, Reason = reason, DispatchState = ConcurrencyDispatchState.DeliveredUnanswered,
                            OperationClass = WorkerCommandCatalog.Classify(entry.Method, entry.Action),
                            Request = new JObject { ["method"] = entry.Method, ["action"] = entry.Action, ["params"] = new JObject() },
                        };
                    }
                }
            }
        }

        private static string CatalogHint(string code) =>
            (string)ConcurrencyErrorCatalog.Build(code, null, new ConcurrencyErrorFields { QueueWaitMs = QueueWait })["hint"]!;

        private static JObject WorkerTargetLocked(string target, string part, DateTime expiresAtUtc) => new JObject
        {
            ["status"] = "Error",
            ["code"] = ConcurrencyErrorCatalog.TargetLockedByOtherAgent,
            ["message"] = "Object '" + target + "' part '" + part + "' is locked by agent 'gateway:owner-b'.",
            ["hint"] = "Wait for the lock to expire or use force=true to override. Acquire the lock with genexus_multi_agent_lock action=acquire.",
            ["lockHolder"] = "gateway:owner-b",
            ["lockExpiresAt"] = expiresAtUtc.ToString("o", CultureInfo.InvariantCulture),
            ["target"] = target,
        };

        private static JObject WorkerCanonicalError(string code, string target, string message, string hint, JObject? extra)
        {
            var result = new JObject
            {
                ["status"] = "error",
                ["target"] = target,
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["hint"] = hint,
                    ["nextSteps"] = new JArray(new JObject { ["tool"] = "genexus_read", ["args"] = new JObject { ["name"] = target } }),
                },
            };
            if (extra != null)
                foreach (var property in extra.Properties()) result[property.Name] = property.Value.DeepClone();
            return result;
        }

        private static JObject WriteRequest(string target, string part) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "w1",
            ["method"] = "Write",
            ["action"] = part,
            ["target"] = target,
            ["params"] = new JObject { ["module"] = "Write", ["action"] = part, ["part"] = part, ["target"] = target },
        };

        private static JObject RpcResult(JObject result) => new JObject { ["jsonrpc"] = "2.0", ["id"] = "r0", ["result"] = result };

        private static JObject ToolError(JObject error) => new JObject { ["status"] = "error", ["error"] = error };

        private static string Wire(JToken token) => token.ToString(Formatting.None);

        private static Configuration SharedConfig() => ConfigFor("shared-host");

        private static Configuration IsolatedConfig(string mode) => ConfigFor(mode);

        private static Configuration ConfigFor(string sharingMode)
        {
            var section = new JObject { [SharedHostSettingsResolver.QueueWaitMsKey] = QueueWait };
            return new Configuration
            {
                Server = new ServerConfig { WorkerSharingMode = sharingMode },
                EffectiveSharedHost = SharedHostSettingsResolver.Resolve(section, strictDocument: false, string.Empty, _ => null),
            };
        }
    }
}
