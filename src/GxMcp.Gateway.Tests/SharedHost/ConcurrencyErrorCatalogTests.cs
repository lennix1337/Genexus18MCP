using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Unit tests for the Concurrency_Error_Table catalog (Requirement 12 criteria 1, 2, 3, 5, 6;
    // design.md, "ConcurrencyErrorCatalog.cs"). One test per table row plus the cross-row rules.
    //
    // Contract these tests pin for ConcurrencyErrorCatalog (namespace GxMcp.Common.SharedHost):
    // - static class ConcurrencyDispatchState with string constants NotDelivered = "not-delivered",
    //   DeliveredUnanswered = "delivered-unanswered", DeliveredRejected = "delivered-rejected".
    // - sealed class ConcurrencyErrorRow: Code (string), Reasons (IReadOnlyList<string>, table order,
    //   empty when the row lists none), Retryable (bool?, null means "per Operation_Class"),
    //   DispatchState (string?, null means "per delivery"). Only SHARED_WORKER_RESTARTED has nulls.
    // - sealed class ConcurrencyErrorFields, all settable properties:
    //   QueueWaitMs (int, the configured queue wait, upper bound of retryAfterMs for every retryable
    //   code except TargetLockedByOtherAgent), RetryAfterMs (int?, the delay the raiser wants; Build
    //   clamps it into [0, bound]; null lets Build pick a value inside the bound),
    //   LockRemainingMs (long?, ms until the lock expiry, the bound for TargetLockedByOtherAgent;
    //   null means 0), DispatchState (string?, required for SHARED_WORKER_RESTARTED and limited to
    //   not-delivered or delivered-unanswered; for fixed rows null or the row value, anything else
    //   throws), OperationClass (OperationClass?, from WorkerCommandCatalog; decides retryable of a
    //   delivered-unanswered SHARED_WORKER_RESTARTED; null means "no table entry" and gives false),
    //   HolderPid (int?, the holder Gateway PID named in the KB_WORKER_OWNED_ELSEWHERE hint),
    //   Details (JObject?, extra structured fields merged into the error object; a Details key equal
    //   to a core key never overrides the core value; Details values never appear in hint or message).
    // - static ConcurrencyErrorCatalog.Rows (IReadOnlyList<ConcurrencyErrorRow>, one row per code).
    // - static JObject ConcurrencyErrorCatalog.Build(string code, string? reason, ConcurrencyErrorFields fields):
    //   returns the envelope "error" object with "code", "message" (non-empty), "hint" (non-empty,
    //   names the next caller action), "retryable" (JSON boolean), "dispatchState", "reason" (only
    //   when the row lists reasons), and "retryAfterMs" (JSON integer, only when retryable).
    //   Throws ArgumentException for an unknown code, a reason outside the row, a missing reason on
    //   a row with reasons, a reason on a row without reasons, or a conflicting DispatchState.
    // Hint phrases are matched case-insensitively and are part of this contract.
    public class ConcurrencyErrorCatalogTests
    {
        private const int QueueWait = 30000;

        private static readonly string[] AllCodes =
        {
            "WorkerBusy", "KB_POOL_FULL", "KB_WORKER_OWNED_ELSEWHERE", "SHARED_WORKER_IDENTITY_MISMATCH",
            "SHARED_WORKER_ATTACH_FAILED", "WORKER_STARTUP_FAILED", "SHARED_WORKER_BACKPRESSURE",
            "SHARED_WORKER_RESTARTED", "TargetLockedByOtherAgent", "StaleObject", "LockCheckFailed",
            "VersionCheckUnavailable",
        };

        private static readonly string[] DispatchStates =
        {
            "not-delivered", "delivered-unanswered", "delivered-rejected",
        };

        // ---- Cross-row rules ----------------------------------------------------------------

        [Fact]
        public void Rows_HoldExactlyTheTableCodes_AsDistinctStrings()
        {
            var codes = ConcurrencyErrorCatalog.Rows.Select(r => r.Code).ToList();

            Assert.Equal(AllCodes.Length, codes.Count);
            Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(AllCodes.OrderBy(c => c, StringComparer.Ordinal), codes.OrderBy(c => c, StringComparer.Ordinal));
        }

        [Fact]
        public void DispatchStateConstants_AreTheThreeAllowedValues()
        {
            Assert.Equal("not-delivered", ConcurrencyDispatchState.NotDelivered);
            Assert.Equal("delivered-unanswered", ConcurrencyDispatchState.DeliveredUnanswered);
            Assert.Equal("delivered-rejected", ConcurrencyDispatchState.DeliveredRejected);
        }

        [Fact]
        public void Build_UnknownCode_Throws()
        {
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("NoSuchCode", null, Fields()));
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("workerbusy", "queue-full", Fields()));
        }

        [Fact]
        public void Build_ReasonOutsideRow_Throws()
        {
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("WorkerBusy", "low-memory", Fields()));
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("KB_POOL_FULL", "queue-full", Fields()));
        }

        [Fact]
        public void Build_MissingReasonOnRowWithReasons_Throws()
        {
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("WorkerBusy", null, Fields()));
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("WORKER_STARTUP_FAILED", "", Fields()));
        }

        [Fact]
        public void Build_ReasonOnRowWithoutReasons_Throws()
        {
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("StaleObject", "queue-full", Fields()));
        }

        [Fact]
        public void Build_FixedRowWithConflictingDispatchState_Throws()
        {
            var fields = Fields();
            fields.DispatchState = ConcurrencyDispatchState.DeliveredUnanswered;

            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("WorkerBusy", "queue-full", fields));
        }

        [Fact]
        public void Build_FixedRowWithMatchingDispatchState_IsAccepted()
        {
            var fields = Fields();
            fields.DispatchState = ConcurrencyDispatchState.NotDelivered;

            var error = ConcurrencyErrorCatalog.Build("WorkerBusy", "queue-full", fields);

            Assert.Equal("not-delivered", (string?)error["dispatchState"]);
        }

        [Fact]
        public void Build_EveryRowAndReason_CarriesTypedRetryableAndAllowedDispatchState()
        {
            foreach (var (code, reason, fields) in EveryCombination())
            {
                var error = ConcurrencyErrorCatalog.Build(code, reason, fields);
                string label = code + "/" + (reason ?? "<none>") + "/" + (fields.DispatchState ?? "<row>");

                Assert.Equal(code, (string?)error["code"]);
                Assert.True(error["retryable"]?.Type == JTokenType.Boolean, label + ": retryable is not a JSON boolean");
                Assert.Contains((string?)error["dispatchState"], DispatchStates);
                Assert.False(string.IsNullOrWhiteSpace((string?)error["message"]), label + ": empty message");
                Assert.False(string.IsNullOrWhiteSpace((string?)error["hint"]), label + ": empty hint");

                if (reason == null)
                    Assert.Null(error["reason"]);
                else
                    Assert.Equal(reason, (string?)error["reason"]);

                if ((bool)error["retryable"]!)
                    Assert.True(error["retryAfterMs"]?.Type == JTokenType.Integer, label + ": retryAfterMs missing or not an integer");
                else
                    Assert.Null(error["retryAfterMs"]);
            }
        }

        [Theory]
        [InlineData("WorkerBusy", "queue-full")]
        [InlineData("SHARED_WORKER_BACKPRESSURE", null)]
        [InlineData("SHARED_WORKER_ATTACH_FAILED", "pipe-refused")]
        [InlineData("WORKER_STARTUP_FAILED", "replacement-failed")]
        [InlineData("LockCheckFailed", null)]
        [InlineData("VersionCheckUnavailable", null)]
        public void Build_RetryAfterAboveQueueWait_IsClampedToQueueWait(string code, string? reason)
        {
            var fields = Fields(retryAfterMs: QueueWait + 5000);

            var error = ConcurrencyErrorCatalog.Build(code, reason, fields);

            Assert.Equal(QueueWait, (int)error["retryAfterMs"]!);
        }

        [Theory]
        [InlineData("WorkerBusy", "queue-wait-expired")]
        [InlineData("LockCheckFailed", null)]
        public void Build_NegativeRetryAfter_IsClampedToZero(string code, string? reason)
        {
            var error = ConcurrencyErrorCatalog.Build(code, reason, Fields(retryAfterMs: -1));

            Assert.Equal(0, (int)error["retryAfterMs"]!);
        }

        [Fact]
        public void Build_RetryAfterInsideBound_IsKept()
        {
            var error = ConcurrencyErrorCatalog.Build("WorkerBusy", "session-queue-limit", Fields(retryAfterMs: 2500));

            Assert.Equal(2500, (int)error["retryAfterMs"]!);
        }

        [Fact]
        public void Build_DetailsAreMerged_ButNeverOverrideCoreKeys()
        {
            var fields = Fields();
            fields.Details = new JObject
            {
                ["holderPid"] = 4242,
                ["code"] = "Other",
                ["retryable"] = true,
                ["dispatchState"] = "delivered-rejected",
                ["hint"] = "overridden",
            };

            var error = ConcurrencyErrorCatalog.Build("KB_WORKER_OWNED_ELSEWHERE", "liveness-unverified", fields);

            Assert.Equal(4242, (int)error["holderPid"]!);
            Assert.Equal("KB_WORKER_OWNED_ELSEWHERE", (string?)error["code"]);
            Assert.False((bool)error["retryable"]!);
            Assert.Equal("not-delivered", (string?)error["dispatchState"]);
            Assert.NotEqual("overridden", (string?)error["hint"]);
        }

        [Fact]
        public void Build_HintAndMessage_NeverContainDetailValues()
        {
            const string source = "Event Start\n  &Body = 'payload-text-7f3a'\nEndEvent";
            const string lease = "lease-token-9c1e2b";
            const string credential = "Password=hunter2-x91";

            foreach (var (code, reason, fields) in EveryCombination())
            {
                fields.Details = new JObject
                {
                    ["source"] = source,
                    ["leaseToken"] = lease,
                    ["payload"] = "{\"text\":\"payload-text-7f3a\"}",
                    ["connection"] = credential,
                };

                var error = ConcurrencyErrorCatalog.Build(code, reason, fields);

                foreach (var key in new[] { "hint", "message" })
                {
                    string text = (string?)error[key] ?? "";
                    Assert.DoesNotContain("payload-text-7f3a", text);
                    Assert.DoesNotContain(lease, text);
                    Assert.DoesNotContain("hunter2", text);
                }
            }
        }

        // ---- One test per Concurrency_Error_Table row ---------------------------------------

        [Fact]
        public void Row_WorkerBusy()
        {
            AssertRow("WorkerBusy", new[] { "queue-wait-expired", "queue-full", "session-queue-limit" },
                retryable: true, dispatchState: "not-delivered");

            foreach (var reason in new[] { "queue-wait-expired", "queue-full", "session-queue-limit" })
            {
                var error = AssertBuilt("WorkerBusy", reason, Fields(), retryable: true, dispatchState: "not-delivered");
                AssertRetryAfterWithin(error, 0, QueueWait);
                AssertHintNamesRetryAfter(error);
            }
        }

        [Fact]
        public void Row_KbPoolFull()
        {
            AssertRow("KB_POOL_FULL", new[] { "worker-limit", "max-open-kbs", "low-memory" },
                retryable: false, dispatchState: "not-delivered");

            foreach (var reason in new[] { "worker-limit", "max-open-kbs" })
            {
                var error = AssertBuilt("KB_POOL_FULL", reason, Fields(), retryable: false, dispatchState: "not-delivered");
                AssertHintContains(error, "close", "open KB", "idle");
                AssertHintDoesNotOfferRetryAfter(error);
            }

            var lowMemory = AssertBuilt("KB_POOL_FULL", "low-memory", Fields(), retryable: false, dispatchState: "not-delivered");
            AssertHintContains(lowMemory, "close", "open KBs", "GeneXus IDE", "idle MCP session");
            AssertHintDoesNotOfferRetryAfter(lowMemory);
        }

        [Fact]
        public void Row_KbWorkerOwnedElsewhere()
        {
            AssertRow("KB_WORKER_OWNED_ELSEWHERE", new[] { "owner-does-not-accept-attachments", "liveness-unverified" },
                retryable: false, dispatchState: "not-delivered");

            foreach (var reason in new[] { "owner-does-not-accept-attachments", "liveness-unverified" })
            {
                var fields = Fields();
                fields.HolderPid = 31337;

                var error = AssertBuilt("KB_WORKER_OWNED_ELSEWHERE", reason, fields, retryable: false, dispatchState: "not-delivered");
                AssertHintContains(error, "ask the user", "release", "PID", "31337");
                AssertHintDoesNotOfferRetryAfter(error);
            }
        }

        [Fact]
        public void Row_SharedWorkerIdentityMismatch()
        {
            AssertRow("SHARED_WORKER_IDENTITY_MISMATCH", Array.Empty<string>(), retryable: false, dispatchState: "not-delivered");

            var error = AssertBuilt("SHARED_WORKER_IDENTITY_MISMATCH", null, Fields(), retryable: false, dispatchState: "not-delivered");
            AssertHintContains(error, "installation", "driver", "target", "Worker executable", "close");
            AssertHintDoesNotOfferRetryAfter(error);
        }

        [Fact]
        public void Row_SharedWorkerAttachFailed()
        {
            AssertRow("SHARED_WORKER_ATTACH_FAILED", new[] { "readiness-timeout", "pipe-refused" },
                retryable: true, dispatchState: "not-delivered");

            foreach (var reason in new[] { "readiness-timeout", "pipe-refused" })
            {
                var error = AssertBuilt("SHARED_WORKER_ATTACH_FAILED", reason, Fields(), retryable: true, dispatchState: "not-delivered");
                AssertRetryAfterWithin(error, 0, QueueWait);
                AssertHintNamesRetryAfter(error);
            }
        }

        [Fact]
        public void Row_WorkerStartupFailed()
        {
            AssertRow("WORKER_STARTUP_FAILED", new[] { "replacement-failed", "cold-start-wait-expired" },
                retryable: true, dispatchState: "not-delivered");

            foreach (var reason in new[] { "replacement-failed", "cold-start-wait-expired" })
            {
                var error = AssertBuilt("WORKER_STARTUP_FAILED", reason, Fields(), retryable: true, dispatchState: "not-delivered");
                AssertRetryAfterWithin(error, 0, QueueWait);
                AssertHintNamesRetryAfter(error);
            }
        }

        [Fact]
        public void Row_SharedWorkerBackpressure()
        {
            AssertRow("SHARED_WORKER_BACKPRESSURE", Array.Empty<string>(), retryable: true, dispatchState: "not-delivered");

            var error = AssertBuilt("SHARED_WORKER_BACKPRESSURE", null, Fields(), retryable: true, dispatchState: "not-delivered");
            AssertRetryAfterWithin(error, 0, QueueWait);
            AssertHintNamesRetryAfter(error);
        }

        [Fact]
        public void Row_SharedWorkerRestarted_IsPerOperationClassAndPerDelivery()
        {
            AssertRow("SHARED_WORKER_RESTARTED", Array.Empty<string>(), retryable: null, dispatchState: null);
        }

        [Fact]
        public void Row_SharedWorkerRestarted_NotDelivered_IsRetryableWhateverTheOperationClass()
        {
            foreach (OperationClass? operationClass in new OperationClass?[] { OperationClass.Read, OperationClass.Write, null })
            {
                var fields = Fields();
                fields.DispatchState = ConcurrencyDispatchState.NotDelivered;
                fields.OperationClass = operationClass;

                var error = AssertBuilt("SHARED_WORKER_RESTARTED", null, fields, retryable: true, dispatchState: "not-delivered");
                AssertRetryAfterWithin(error, 0, QueueWait);
                AssertHintNamesRetryAfter(error);
            }
        }

        [Fact]
        public void Row_SharedWorkerRestarted_DeliveredUnansweredRead_IsRetryable()
        {
            var fields = Fields();
            fields.DispatchState = ConcurrencyDispatchState.DeliveredUnanswered;
            fields.OperationClass = OperationClass.Read;

            var error = AssertBuilt("SHARED_WORKER_RESTARTED", null, fields, retryable: true, dispatchState: "delivered-unanswered");
            AssertRetryAfterWithin(error, 0, QueueWait);
            AssertHintNamesRetryAfter(error);
        }

        [Fact]
        public void Row_SharedWorkerRestarted_DeliveredUnansweredWriteOrUnclassified_IsNotRetryable()
        {
            foreach (OperationClass? operationClass in new OperationClass?[] { OperationClass.Write, null })
            {
                var fields = Fields();
                fields.DispatchState = ConcurrencyDispatchState.DeliveredUnanswered;
                fields.OperationClass = operationClass;

                var error = AssertBuilt("SHARED_WORKER_RESTARTED", null, fields, retryable: false, dispatchState: "delivered-unanswered");
                AssertHintContains(error, "read the target", "applied", "resend");
                AssertHintDoesNotOfferRetryAfter(error);
            }
        }

        [Fact]
        public void Row_SharedWorkerRestarted_WithoutOrWithRejectedDispatchState_Throws()
        {
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("SHARED_WORKER_RESTARTED", null, Fields()));

            var rejected = Fields();
            rejected.DispatchState = ConcurrencyDispatchState.DeliveredRejected;
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("SHARED_WORKER_RESTARTED", null, rejected));

            var unknown = Fields();
            unknown.DispatchState = "delivered";
            Assert.Throws<ArgumentException>(() => ConcurrencyErrorCatalog.Build("SHARED_WORKER_RESTARTED", null, unknown));
        }

        [Fact]
        public void Row_TargetLockedByOtherAgent_BoundsRetryAfterByLockRemaining()
        {
            AssertRow("TargetLockedByOtherAgent", Array.Empty<string>(), retryable: true, dispatchState: "delivered-rejected");

            // The lock bound applies, not the queue wait: 45000 exceeds the queue wait on purpose.
            var fields = Fields(retryAfterMs: 60000);
            fields.LockRemainingMs = 45000;
            var error = AssertBuilt("TargetLockedByOtherAgent", null, fields, retryable: true, dispatchState: "delivered-rejected");
            Assert.Equal(45000, (int)error["retryAfterMs"]!);
            AssertHintContains(error, "lock expiry", "lock holder");

            var inside = Fields(retryAfterMs: 1200);
            inside.LockRemainingMs = 8000;
            AssertRetryAfterWithin(ConcurrencyErrorCatalog.Build("TargetLockedByOtherAgent", null, inside), 0, 8000);

            var expired = Fields(retryAfterMs: 5000);
            expired.LockRemainingMs = null;
            Assert.Equal(0, (int)ConcurrencyErrorCatalog.Build("TargetLockedByOtherAgent", null, expired)["retryAfterMs"]!);
        }

        [Fact]
        public void Row_StaleObject()
        {
            AssertRow("StaleObject", Array.Empty<string>(), retryable: false, dispatchState: "delivered-rejected");

            var error = AssertBuilt("StaleObject", null, Fields(), retryable: false, dispatchState: "delivered-rejected");
            AssertHintContains(error, "re-read", "version token");
            AssertHintDoesNotOfferRetryAfter(error);
        }

        [Fact]
        public void Row_LockCheckFailed()
        {
            AssertRow("LockCheckFailed", Array.Empty<string>(), retryable: true, dispatchState: "delivered-rejected");

            var error = AssertBuilt("LockCheckFailed", null, Fields(), retryable: true, dispatchState: "delivered-rejected");
            AssertRetryAfterWithin(error, 0, QueueWait);
            AssertHintNamesRetryAfter(error);
        }

        [Fact]
        public void Row_VersionCheckUnavailable()
        {
            AssertRow("VersionCheckUnavailable", Array.Empty<string>(), retryable: true, dispatchState: "delivered-rejected");

            var error = AssertBuilt("VersionCheckUnavailable", null, Fields(), retryable: true, dispatchState: "delivered-rejected");
            AssertRetryAfterWithin(error, 0, QueueWait);
            AssertHintNamesRetryAfter(error);
        }

        // ---- Helpers ------------------------------------------------------------------------

        private static ConcurrencyErrorFields Fields(int? retryAfterMs = null)
        {
            return new ConcurrencyErrorFields { QueueWaitMs = QueueWait, RetryAfterMs = retryAfterMs };
        }

        private static ConcurrencyErrorRow Row(string code)
        {
            var matches = ConcurrencyErrorCatalog.Rows.Where(r => r.Code == code).ToList();
            Assert.True(matches.Count == 1, code + ": expected one row, found " + matches.Count);
            return matches[0];
        }

        private static void AssertRow(string code, string[] reasons, bool? retryable, string? dispatchState)
        {
            var row = Row(code);
            Assert.Equal(reasons, row.Reasons.ToArray());
            Assert.Equal(retryable, row.Retryable);
            Assert.Equal(dispatchState, row.DispatchState);
        }

        private static JObject AssertBuilt(string code, string? reason, ConcurrencyErrorFields fields, bool retryable, string dispatchState)
        {
            var error = ConcurrencyErrorCatalog.Build(code, reason, fields);

            Assert.Equal(code, (string?)error["code"]);
            Assert.Equal(JTokenType.Boolean, error["retryable"]?.Type);
            Assert.Equal(retryable, (bool)error["retryable"]!);
            Assert.Equal(dispatchState, (string?)error["dispatchState"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)error["message"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)error["hint"]));

            if (reason == null)
                Assert.Null(error["reason"]);
            else
                Assert.Equal(reason, (string?)error["reason"]);

            if (!retryable)
                Assert.Null(error["retryAfterMs"]);

            return error;
        }

        private static void AssertRetryAfterWithin(JObject error, int min, int max)
        {
            var token = error["retryAfterMs"];
            Assert.True(token?.Type == JTokenType.Integer, "retryAfterMs missing or not an integer");
            int value = (int)token!;
            Assert.InRange(value, min, max);
        }

        private static void AssertHintNamesRetryAfter(JObject error)
        {
            AssertHintContains(error, "retry", "retryAfterMs");
        }

        private static void AssertHintDoesNotOfferRetryAfter(JObject error)
        {
            string hint = (string?)error["hint"] ?? "";
            Assert.DoesNotContain("retryAfterMs", hint, StringComparison.OrdinalIgnoreCase);
        }

        private static void AssertHintContains(JObject error, params string[] phrases)
        {
            string hint = (string?)error["hint"] ?? "";
            foreach (var phrase in phrases)
                Assert.True(hint.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0,
                    (string?)error["code"] + ": hint does not mention '" + phrase + "': " + hint);
        }

        // Every (code, reason, fields) the table allows, with fresh fields per combination.
        // SHARED_WORKER_RESTARTED expands over both dispatch states and every Operation_Class value.
        private static IEnumerable<(string Code, string? Reason, ConcurrencyErrorFields Fields)> EveryCombination()
        {
            foreach (var code in AllCodes)
            {
                if (code == "SHARED_WORKER_RESTARTED")
                {
                    foreach (var state in new[] { ConcurrencyDispatchState.NotDelivered, ConcurrencyDispatchState.DeliveredUnanswered })
                    foreach (OperationClass? operationClass in new OperationClass?[] { OperationClass.Read, OperationClass.Write, null })
                    {
                        var fields = Fields(retryAfterMs: 1000);
                        fields.DispatchState = state;
                        fields.OperationClass = operationClass;
                        yield return (code, null, fields);
                    }
                    continue;
                }

                var reasons = ReasonsOf(code);
                foreach (var reason in reasons.Length == 0 ? new string?[] { null } : reasons.Cast<string?>())
                {
                    var fields = Fields(retryAfterMs: 1000);
                    fields.LockRemainingMs = 20000;
                    fields.HolderPid = 31337;
                    yield return (code, reason, fields);
                }
            }
        }

        // Literal Concurrency_Error_Table reasons, independent of the catalog under test.
        private static string[] ReasonsOf(string code) => code switch
        {
            "WorkerBusy" => new[] { "queue-wait-expired", "queue-full", "session-queue-limit" },
            "KB_POOL_FULL" => new[] { "worker-limit", "max-open-kbs", "low-memory" },
            "KB_WORKER_OWNED_ELSEWHERE" => new[] { "owner-does-not-accept-attachments", "liveness-unverified" },
            "SHARED_WORKER_ATTACH_FAILED" => new[] { "readiness-timeout", "pipe-refused" },
            "WORKER_STARTUP_FAILED" => new[] { "replacement-failed", "cold-start-wait-expired" },
            _ => Array.Empty<string>(),
        };
    }
}
