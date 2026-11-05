using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 14: Concurrency error catalog conformance
    //
    // For any row of the Concurrency_Error_Table, any reason value of that row, and any field values, the built error
    // has exactly that code and reason, the row's retryable and dispatchState, a non-empty hint for that code and
    // reason, and, when retryable, an integer retryAfterMs within the bound of Requirement 12 criterion 6 (for
    // TargetLockedByOtherAgent, from 0 to the milliseconds remaining until the lock expiry). Any input outside the
    // table throws ArgumentException. Details never override a core key and Details values never reach hint or message.
    //
    // **Validates: Requirements 9.2, 12.1, 12.2, 12.3, 12.5, 12.6**
    //
    // Uses only the contract pinned in the header of ConcurrencyErrorCatalogTests.cs. The oracle below restates the
    // Concurrency_Error_Table literally and never reads ConcurrencyErrorCatalog to compute an expectation.
    public class ConcurrencyErrorCatalogPropertyTests
    {
        private const int Iterations = 300;
        private const string MarkerPrefix = "SECRET-MARKER-";

        private const string NotDelivered = "not-delivered";
        private const string DeliveredUnanswered = "delivered-unanswered";
        private const string DeliveredRejected = "delivered-rejected";
        private const string Restarted = "SHARED_WORKER_RESTARTED";
        private const string TargetLocked = "TargetLockedByOtherAgent";

        private static readonly string[] AllowedDispatchStates = { NotDelivered, DeliveredUnanswered, DeliveredRejected };

        // Keys Build owns. A Details key equal to one of these never overrides or introduces the core value.
        private static readonly string[] CoreKeys = { "code", "message", "hint", "retryable", "dispatchState", "reason", "retryAfterMs" };

        // Non-core Details keys. Kept apart from any name Build could emit on its own.
        private static readonly string[] NonCoreKeys = { "source", "leaseToken", "payload", "connection", "detailA", "detailB", "detailC" };

        // ---- Oracle: the Concurrency_Error_Table, restated literally ---------------------------------------------

        private sealed class OracleRow
        {
            internal OracleRow(string code, string[] reasons, bool? retryable, string? dispatchState)
            {
                Code = code;
                Reasons = reasons;
                Retryable = retryable;
                DispatchState = dispatchState;
            }

            internal string Code { get; }
            internal string[] Reasons { get; }
            internal bool? Retryable { get; }        // null: per Operation_Class (SHARED_WORKER_RESTARTED only)
            internal string? DispatchState { get; }  // null: per delivery (SHARED_WORKER_RESTARTED only)
        }

        private static readonly OracleRow[] Table =
        {
            new OracleRow("WorkerBusy", new[] { "queue-wait-expired", "queue-full", "session-queue-limit" }, true, NotDelivered),
            new OracleRow("KB_POOL_FULL", new[] { "worker-limit", "max-open-kbs", "low-memory" }, false, NotDelivered),
            new OracleRow("KB_WORKER_OWNED_ELSEWHERE", new[] { "owner-does-not-accept-attachments", "liveness-unverified" }, false, NotDelivered),
            new OracleRow("SHARED_WORKER_IDENTITY_MISMATCH", new string[0], false, NotDelivered),
            new OracleRow("SHARED_WORKER_ATTACH_FAILED", new[] { "readiness-timeout", "pipe-refused" }, true, NotDelivered),
            new OracleRow("WORKER_STARTUP_FAILED", new[] { "replacement-failed", "cold-start-wait-expired" }, true, NotDelivered),
            new OracleRow("SHARED_WORKER_BACKPRESSURE", new string[0], true, NotDelivered),
            new OracleRow(Restarted, new string[0], null, null),
            new OracleRow(TargetLocked, new string[0], true, DeliveredRejected),
            new OracleRow("StaleObject", new string[0], false, DeliveredRejected),
            new OracleRow("LockCheckFailed", new string[0], true, DeliveredRejected),
            new OracleRow("VersionCheckUnavailable", new string[0], true, DeliveredRejected),
        };

        private static readonly string[] AllReasons = Table.SelectMany(r => r.Reasons).Distinct(StringComparer.Ordinal).ToArray();

        private static OracleRow? FindRow(string code) => Table.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal));

        // ---- Generated input --------------------------------------------------------------------------------------

        private sealed class Input
        {
            internal string Code = "";
            internal string? Reason;
            internal int QueueWaitMs;
            internal int? RetryAfterMs;
            internal long? LockRemainingMs;
            internal string? DispatchState;
            internal OperationClass? OperationClass;
            internal int? HolderPid;
            internal JObject? Details;
            internal string Mutation = "none";

            internal ConcurrencyErrorFields ToFields() => new ConcurrencyErrorFields
            {
                QueueWaitMs = QueueWaitMs,
                RetryAfterMs = RetryAfterMs,
                LockRemainingMs = LockRemainingMs,
                DispatchState = DispatchState,
                OperationClass = OperationClass,
                HolderPid = HolderPid,
                // A copy, so a Build that consumes or reparents Details cannot change the oracle's view.
                Details = Details == null ? null : (JObject)Details.DeepClone(),
            };
        }

        // ---- Properties -------------------------------------------------------------------------------------------

        [Fact]
        public void Build_ForAnyRowReasonAndFields_ConformsToTheTable()
        {
            SeededInterleavingRunner.Run("Property 14: Concurrency error catalog conformance (valid input)", Iterations, c =>
            {
                var input = GenerateValid(c);
                LogInput(c, input);
                Assert.True(OracleIsValid(input, out var why), "Generator produced invalid input: " + why);

                AssertCatalogRowMatchesOracle(input.Code);

                var error = ConcurrencyErrorCatalog.Build(input.Code, input.Reason, input.ToFields());
                c.Log("built " + error.ToString(Formatting.None));

                AssertConforms(input, error);
            });
        }

        [Fact]
        public void Build_ForAnyInputOutsideTheTable_ThrowsArgumentException()
        {
            SeededInterleavingRunner.Run("Property 14: Concurrency error catalog conformance (invalid input)", Iterations, c =>
            {
                var input = GenerateValid(c);
                Mutate(c, input);
                LogInput(c, input);
                Assert.False(OracleIsValid(input, out _), "Mutation '" + input.Mutation + "' left the input valid");

                var fields = input.ToFields();
                var ex = Record.Exception(() => ConcurrencyErrorCatalog.Build(input.Code, input.Reason, fields));
                c.Log("threw " + (ex == null ? "nothing" : ex.GetType().Name));

                Assert.NotNull(ex);
                Assert.IsType<ArgumentException>(ex);
            });
        }

        // ---- Generators -------------------------------------------------------------------------------------------

        private static Input GenerateValid(PbtCase c)
        {
            var row = c.Pick(Table);
            var input = new Input
            {
                Code = row.Code,
                Reason = row.Reasons.Length == 0 ? null : c.Pick(row.Reasons),
                QueueWaitMs = GenerateQueueWait(c),
                LockRemainingMs = GenerateLockRemaining(c),
                OperationClass = c.Pick(new OperationClass?[] { GxMcp.Common.SharedHost.OperationClass.Read, GxMcp.Common.SharedHost.OperationClass.Write, null }),
                HolderPid = c.Chance(0.7) ? c.Int(1, 99999) : (int?)null,
            };

            if (row.DispatchState == null)
                input.DispatchState = c.Pick(new[] { NotDelivered, DeliveredUnanswered });
            else
                input.DispatchState = c.Bool() ? row.DispatchState : null;

            long bound = string.Equals(row.Code, TargetLocked, StringComparison.Ordinal)
                ? Math.Max(0L, input.LockRemainingMs ?? 0L)
                : input.QueueWaitMs;
            input.RetryAfterMs = GenerateRetryAfter(c, bound);
            input.Details = GenerateDetails(c);
            return input;
        }

        private static int GenerateQueueWait(PbtCase c)
        {
            switch (c.Int(0, 4))
            {
                case 0: return 0;
                case 1: return 30000;
                case 2: return c.Int(1, 100);
                default: return c.Int(0, 120000);
            }
        }

        private static long? GenerateLockRemaining(PbtCase c)
        {
            switch (c.Int(0, 4))
            {
                case 0: return null;
                case 1: return 0;
                case 2: return c.Int(1, 100);
                default: return c.Int(0, 3600000);
            }
        }

        // Negative, inside, at, and above the bound, plus null (Build picks a value inside the bound).
        private static int? GenerateRetryAfter(PbtCase c, long bound)
        {
            int b = (int)Math.Min(bound, int.MaxValue);
            switch (c.Int(0, 9))
            {
                case 0: return null;
                case 1: return int.MinValue;
                case 2: return -1;
                case 3: return c.Int(-100000, -1);
                case 4: return 0;
                case 5: return b;
                case 6: return b == int.MaxValue ? b : b + 1;
                case 7: return b == int.MaxValue ? b : c.Int(b + 1, int.MaxValue - 1);
                case 8: return int.MaxValue;
                default: return c.Int(0, b);
            }
        }

        private static JObject? GenerateDetails(PbtCase c)
        {
            switch (c.Int(0, 5))
            {
                case 0: return null;
                case 1: return new JObject();
            }

            var details = new JObject();
            foreach (var key in CoreKeys)
            {
                if (c.Chance(0.4)) details[key] = MarkerValue(c);
            }
            foreach (var key in NonCoreKeys)
            {
                if (c.Chance(0.4)) details[key] = MarkerValue(c);
            }
            return details;
        }

        // A marker as a plain string, a number-looking string, or nested inside an object or array.
        private static JToken MarkerValue(PbtCase c)
        {
            string marker = MarkerPrefix + c.Int(0, 999999).ToString(CultureInfo.InvariantCulture);
            switch (c.Int(0, 3))
            {
                case 0: return new JValue(marker);
                case 1: return new JObject { ["text"] = marker };
                case 2: return new JArray(marker, MarkerPrefix + "tail");
                default: return new JValue("Password=" + marker);
            }
        }

        // Exactly one violation of the table, chosen among the kinds the contract says throw.
        private static void Mutate(PbtCase c, Input input)
        {
            var row = FindRow(input.Code)!;
            var kinds = new List<string> { "unknown-code" };
            if (row.Reasons.Length > 0)
            {
                kinds.Add("reason-outside-row");
                kinds.Add("missing-reason");
            }
            else
            {
                kinds.Add("reason-on-row-without-reasons");
            }
            kinds.Add(row.DispatchState == null ? "restarted-dispatch-state" : "conflicting-dispatch-state");

            input.Mutation = c.Pick(kinds);
            switch (input.Mutation)
            {
                case "unknown-code":
                    switch (c.Int(0, 4))
                    {
                        case 0: input.Code = "NoSuchCode" + c.Int(0, 999).ToString(CultureInfo.InvariantCulture); break;
                        case 1: input.Code = row.Code.ToLowerInvariant(); break;
                        case 2: input.Code = " " + row.Code; break;
                        case 3: input.Code = row.Code + "_X"; break;
                        default: input.Code = ""; break;
                    }
                    break;

                case "reason-outside-row":
                    var foreign = AllReasons.Where(r => !row.Reasons.Contains(r, StringComparer.Ordinal)).ToArray();
                    input.Reason = c.Bool() ? c.Pick(foreign) : "bogus-" + c.Int(0, 999).ToString(CultureInfo.InvariantCulture);
                    break;

                case "missing-reason":
                    input.Reason = c.Bool() ? null : "";
                    break;

                case "reason-on-row-without-reasons":
                    input.Reason = c.Bool() ? c.Pick(AllReasons) : "bogus-" + c.Int(0, 999).ToString(CultureInfo.InvariantCulture);
                    break;

                case "conflicting-dispatch-state":
                    var others = AllowedDispatchStates.Where(s => s != row.DispatchState).ToList();
                    others.Add("delivered");
                    others.Add(row.DispatchState!.ToUpperInvariant());
                    input.DispatchState = c.Pick(others);
                    break;

                case "restarted-dispatch-state":
                    input.DispatchState = c.Pick(new string?[] { null, DeliveredRejected, "delivered", "NOT-DELIVERED" });
                    break;
            }
        }

        // ---- Oracle -----------------------------------------------------------------------------------------------

        private static bool OracleIsValid(Input input, out string why)
        {
            var row = FindRow(input.Code);
            if (row == null) { why = "unknown code"; return false; }

            if (row.Reasons.Length > 0)
            {
                if (string.IsNullOrEmpty(input.Reason)) { why = "missing reason"; return false; }
                if (!row.Reasons.Contains(input.Reason, StringComparer.Ordinal)) { why = "reason outside row"; return false; }
            }
            else if (input.Reason != null)
            {
                why = "reason on row without reasons";
                return false;
            }

            if (row.DispatchState == null)
            {
                if (input.DispatchState != NotDelivered && input.DispatchState != DeliveredUnanswered)
                {
                    why = "SHARED_WORKER_RESTARTED needs not-delivered or delivered-unanswered";
                    return false;
                }
            }
            else if (input.DispatchState != null && input.DispatchState != row.DispatchState)
            {
                why = "conflicting dispatch state";
                return false;
            }

            why = "valid";
            return true;
        }

        private static bool ExpectedRetryable(OracleRow row, Input input)
        {
            if (row.Retryable.HasValue) return row.Retryable.Value;
            if (input.DispatchState == NotDelivered) return true;
            // delivered-unanswered: read retries, write or no table entry does not.
            return input.OperationClass == GxMcp.Common.SharedHost.OperationClass.Read;
        }

        private static string ExpectedDispatchState(OracleRow row, Input input) => row.DispatchState ?? input.DispatchState!;

        private static long RetryBound(OracleRow row, Input input) =>
            string.Equals(row.Code, TargetLocked, StringComparison.Ordinal)
                ? Math.Max(0L, input.LockRemainingMs ?? 0L)
                : input.QueueWaitMs;

        // Hint phrases of Requirement 12 criterion 5, as pinned in ConcurrencyErrorCatalogTests.
        private static string[] ExpectedHintPhrases(OracleRow row, Input input, bool retryable)
        {
            switch (row.Code)
            {
                case "KB_POOL_FULL":
                    return input.Reason == "low-memory"
                        ? new[] { "close", "open KBs", "GeneXus IDE", "idle MCP session" }
                        : new[] { "close", "open KB", "idle" };
                case "KB_WORKER_OWNED_ELSEWHERE":
                    return input.HolderPid.HasValue
                        ? new[] { "ask the user", "release", "PID", input.HolderPid.Value.ToString(CultureInfo.InvariantCulture) }
                        : new[] { "ask the user", "release" };
                case "SHARED_WORKER_IDENTITY_MISMATCH":
                    return new[] { "installation", "driver", "target", "Worker executable", "close" };
                case TargetLocked:
                    return new[] { "lock expiry", "lock holder" };
                case "StaleObject":
                    return new[] { "re-read", "version token" };
                case Restarted:
                    return retryable ? new[] { "retry", "retryAfterMs" } : new[] { "read the target", "applied", "resend" };
                default:
                    return new[] { "retry", "retryAfterMs" };
            }
        }

        // ---- Assertions -------------------------------------------------------------------------------------------

        private static void AssertCatalogRowMatchesOracle(string code)
        {
            var oracle = FindRow(code)!;
            var rows = ConcurrencyErrorCatalog.Rows.Where(r => string.Equals(r.Code, code, StringComparison.Ordinal)).ToList();
            Assert.True(rows.Count == 1, code + ": expected one catalog row, found " + rows.Count);
            Assert.Equal(oracle.Reasons, rows[0].Reasons.ToArray());
            Assert.Equal(oracle.Retryable, rows[0].Retryable);
            Assert.Equal(oracle.DispatchState, rows[0].DispatchState);
        }

        private static void AssertConforms(Input input, JObject error)
        {
            var row = FindRow(input.Code)!;
            bool retryable = ExpectedRetryable(row, input);
            string dispatchState = ExpectedDispatchState(row, input);
            string label = input.Code + "/" + (input.Reason ?? "<none>") + "/" + dispatchState;

            // Core values, with JSON types.
            AssertString(error, "code", label);
            Assert.Equal(input.Code, (string?)error["code"]);

            Assert.True(error["retryable"]?.Type == JTokenType.Boolean, label + ": retryable is not a JSON boolean");
            Assert.Equal(retryable, (bool)error["retryable"]!);
            if (row.Retryable.HasValue)
                Assert.Equal(row.Retryable.Value, (bool)error["retryable"]!);

            AssertString(error, "dispatchState", label);
            Assert.Equal(dispatchState, (string?)error["dispatchState"]);
            Assert.Contains((string?)error["dispatchState"], AllowedDispatchStates);
            if (row.DispatchState != null)
                Assert.Equal(row.DispatchState, (string?)error["dispatchState"]);

            if (row.Reasons.Length > 0)
            {
                AssertString(error, "reason", label);
                Assert.Equal(input.Reason, (string?)error["reason"]);
            }
            else
            {
                Assert.True(error["reason"] == null, label + ": reason present on a row without reasons");
            }

            AssertString(error, "message", label);
            AssertString(error, "hint", label);
            string message = (string)error["message"]!;
            string hint = (string)error["hint"]!;
            Assert.False(string.IsNullOrWhiteSpace(message), label + ": empty message");
            Assert.False(string.IsNullOrWhiteSpace(hint), label + ": empty hint");

            // retryAfterMs: present, integer, and inside the bound only when retryable.
            if (retryable)
            {
                var token = error["retryAfterMs"];
                Assert.True(token?.Type == JTokenType.Integer, label + ": retryAfterMs missing or not an integer");
                long value = (long)token!;
                long bound = RetryBound(row, input);
                Assert.True(value >= 0 && value <= bound, label + ": retryAfterMs " + value + " outside [0, " + bound + "]");
                if (input.RetryAfterMs.HasValue)
                {
                    long expected = Math.Min(Math.Max(0L, input.RetryAfterMs.Value), bound);
                    Assert.Equal(expected, value);
                }
            }
            else
            {
                Assert.True(error["retryAfterMs"] == null, label + ": retryAfterMs present on a non-retryable error");
            }

            // Hint names the next caller action.
            foreach (var phrase in ExpectedHintPhrases(row, input, retryable))
            {
                Assert.True(hint.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0,
                    label + ": hint does not mention '" + phrase + "': " + hint);
            }
            if (!retryable)
                Assert.True(hint.IndexOf("retryAfterMs", StringComparison.OrdinalIgnoreCase) < 0,
                    label + ": non-retryable hint offers retryAfterMs: " + hint);

            // Details values never reach hint or message.
            Assert.True(hint.IndexOf(MarkerPrefix, StringComparison.Ordinal) < 0, label + ": hint carries a Details value: " + hint);
            Assert.True(message.IndexOf(MarkerPrefix, StringComparison.Ordinal) < 0, label + ": message carries a Details value: " + message);

            // Core values never come from Details; non-core Details are merged unchanged; nothing else is added.
            foreach (var key in CoreKeys)
            {
                var value = error[key];
                if (value != null)
                    Assert.False(ContainsMarker(value), label + ": core key '" + key + "' took its value from Details");
            }

            var expectedKeys = new HashSet<string>(StringComparer.Ordinal) { "code", "message", "hint", "retryable", "dispatchState" };
            if (row.Reasons.Length > 0) expectedKeys.Add("reason");
            if (retryable) expectedKeys.Add("retryAfterMs");
            if (input.Details != null)
            {
                foreach (var property in input.Details.Properties())
                {
                    if (CoreKeys.Contains(property.Name, StringComparer.Ordinal)) continue;
                    expectedKeys.Add(property.Name);
                    Assert.True(JToken.DeepEquals(property.Value, error[property.Name]),
                        label + ": Details key '" + property.Name + "' was not merged unchanged");
                }
            }

            var actualKeys = error.Properties().Select(p => p.Name).ToList();
            Assert.Equal(expectedKeys.OrderBy(k => k, StringComparer.Ordinal), actualKeys.OrderBy(k => k, StringComparer.Ordinal));
        }

        private static void AssertString(JObject error, string key, string label)
        {
            Assert.True(error[key]?.Type == JTokenType.String, label + ": '" + key + "' missing or not a JSON string");
        }

        private static bool ContainsMarker(JToken token)
        {
            if (token.Type == JTokenType.String)
                return ((string)token!).IndexOf(MarkerPrefix, StringComparison.Ordinal) >= 0;
            return token.Children().Any(ContainsMarker);
        }

        // ---- Logging ----------------------------------------------------------------------------------------------

        private static void LogInput(PbtCase c, Input input)
        {
            c.Log("input code=" + Quote(input.Code)
                + " reason=" + Quote(input.Reason)
                + " mutation=" + input.Mutation
                + " queueWaitMs=" + input.QueueWaitMs.ToString(CultureInfo.InvariantCulture)
                + " retryAfterMs=" + Fmt(input.RetryAfterMs)
                + " lockRemainingMs=" + Fmt(input.LockRemainingMs)
                + " dispatchState=" + Quote(input.DispatchState)
                + " operationClass=" + (input.OperationClass.HasValue ? input.OperationClass.Value.ToString() : "null")
                + " holderPid=" + Fmt(input.HolderPid));
            c.Log("details " + (input.Details == null ? "null" : input.Details.ToString(Formatting.None)));
        }

        private static string Quote(string? value) => value == null ? "null" : "'" + value + "'";

        private static string Fmt(long? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null";

        private static string Fmt(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null";
    }
}
