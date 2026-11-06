using System;
using System.Collections.Generic;
using System.Globalization;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// RFC #854, Requirement 9 criterion 2 and Requirement 12: rewrites the core fields of a
    /// Concurrency_Error_Table error that a shared Worker or the broker returned, so every code
    /// leaves the Gateway with the <c>retryable</c>, <c>dispatchState</c>, <c>hint</c> and
    /// <c>retryAfterMs</c> values of its catalog row. Runs only in Shared_Host_Mode; every other
    /// mode gets the response argument back, so Isolated_Mode output is byte-identical.
    /// </summary>
    internal static class ConcurrencyErrorNormalizer
    {
        private const string SharedHostMode = "shared-host";

        // WriteService checks this part when a write names none.
        private const string DefaultPart = "Source";

        private static readonly HashSet<string> TableCodes = BuildTableCodes();

        /// <summary>True when <paramref name="code"/> is a Concurrency_Error_Table code (ordinal match).</summary>
        internal static bool IsTableCode(string? code) => code != null && TableCodes.Contains(code);

        /// <summary>
        /// Returns <paramref name="response"/> with its concurrency error normalized. The inputs are never mutated:
        /// a change is written to a deep clone. Non-shared modes return the same reference, untouched.
        /// </summary>
        internal static JObject Normalize(JObject response, JObject? request, Configuration config, Func<DateTime> utcNow)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (!string.Equals(config?.Server?.WorkerSharingMode, SharedHostMode, StringComparison.OrdinalIgnoreCase))
                return response;

            var clone = (JObject)response.DeepClone();
            JObject? error = LocateError(clone);
            if (error == null) return response;
            if (!Apply(error, request, config!, utcNow)) return response;
            return clone;
        }

        private static JObject? LocateError(JObject response)
        {
            JObject? x;
            if (response["jsonrpc"] != null)
            {
                if (response["result"] is JObject result) x = result;
                else if (response["error"] is JObject rpcError && rpcError["data"] is JObject data) x = data;
                else return null;
            }
            else
            {
                x = response;
            }

            if (x["error"] is JObject inner && IsTableCode(StringValue(inner["code"]))) return inner;
            if (IsTableCode(StringValue(x["code"]))) return x;
            return null;
        }

        private static bool Apply(JObject error, JObject? request, Configuration config, Func<DateTime> utcNow)
        {
            string code = StringValue(error["code"])!;
            ConcurrencyErrorRow? row = null;
            foreach (var candidate in ConcurrencyErrorCatalog.Rows)
            {
                if (string.Equals(candidate.Code, code, StringComparison.Ordinal)) { row = candidate; break; }
            }
            if (row == null) return false;

            string? reason = null;
            if (row.Reasons.Count > 0)
            {
                reason = StringValue(error["reason"]);
                // Never invent a reason: a raiser that sent none, or one outside the row, stays visible as is.
                if (string.IsNullOrEmpty(reason) || !Contains(row.Reasons, reason!)) return false;
            }

            bool lockConflict = string.Equals(code, ConcurrencyErrorCatalog.TargetLockedByOtherAgent, StringComparison.Ordinal);
            bool restarted = string.Equals(code, ConcurrencyErrorCatalog.SharedWorkerRestarted, StringComparison.Ordinal);

            var fields = new ConcurrencyErrorFields
            {
                QueueWaitMs = config.EffectiveSharedHost?.QueueWaitMs ?? SharedHostSettingsResolver.DefaultQueueWaitMs,
                RetryAfterMs = IntValue(error["retryAfterMs"]),
                HolderPid = IntValue(error["holderPid"]),
            };
            if (lockConflict)
                fields.LockRemainingMs = LockRemainingMs(error["lockExpiresAt"], utcNow());
            if (restarted)
            {
                string? raised = StringValue(error["dispatchState"]);
                fields.DispatchState = string.Equals(raised, ConcurrencyDispatchState.NotDelivered, StringComparison.Ordinal)
                    || string.Equals(raised, ConcurrencyDispatchState.DeliveredUnanswered, StringComparison.Ordinal)
                    ? raised
                    : ConcurrencyDispatchState.DeliveredUnanswered;
                fields.OperationClass = WorkerCommandCatalog.Classify(
                    StringValue(request?["method"]), StringValue(request?["action"]));
            }

            JObject built = ConcurrencyErrorCatalog.Build(code, reason, fields);

            error["retryable"] = built["retryable"]!.DeepClone();
            error["dispatchState"] = built["dispatchState"]!.DeepClone();
            error["hint"] = built["hint"]!.DeepClone();
            if ((bool)built["retryable"]!) error["retryAfterMs"] = built["retryAfterMs"]!.DeepClone();
            else error.Remove("retryAfterMs");
            if (reason != null) error["reason"] = reason;
            if (string.IsNullOrEmpty(StringValue(error["message"])))
                error["message"] = built["message"]!.DeepClone();

            if (lockConflict && string.IsNullOrEmpty(StringValue(error["part"])))
            {
                string? part = StringValue(request?["params"]?["part"]);
                if (string.IsNullOrEmpty(part)) part = StringValue(request?["part"]);
                error["part"] = string.IsNullOrEmpty(part) ? DefaultPart : part;
            }

            return true;
        }

        private static long LockRemainingMs(JToken? lockExpiresAt, DateTime now)
        {
            if (lockExpiresAt == null || lockExpiresAt.Type == JTokenType.Null) return 0;
            DateTime expiresAt;
            if (lockExpiresAt.Type == JTokenType.Date)
            {
                // JObject.Parse turns an ISO-8601 string into a Date token; an unspecified kind is taken as UTC.
                expiresAt = (DateTime)lockExpiresAt;
                expiresAt = expiresAt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)
                    : expiresAt.ToUniversalTime();
            }
            else if (lockExpiresAt.Type != JTokenType.String
                || !DateTime.TryParse((string)lockExpiresAt!, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out expiresAt))
            {
                return 0;
            }

            DateTime nowUtc = now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : now;
            double remaining = Math.Floor((expiresAt - nowUtc).TotalMilliseconds);
            if (remaining <= 0) return 0;
            return remaining >= long.MaxValue ? long.MaxValue : (long)remaining;
        }

        private static string? StringValue(JToken? token) =>
            token != null && token.Type == JTokenType.String ? (string?)token : null;

        private static int? IntValue(JToken? token)
        {
            if (token == null || token.Type != JTokenType.Integer) return null;
            long value;
            try { value = (long)token; }
            catch (OverflowException) { return int.MaxValue; }
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        private static bool Contains(IReadOnlyList<string> values, string value)
        {
            foreach (var candidate in values)
                if (string.Equals(candidate, value, StringComparison.Ordinal)) return true;
            return false;
        }

        private static HashSet<string> BuildTableCodes()
        {
            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in ConcurrencyErrorCatalog.Rows) codes.Add(row.Code);
            return codes;
        }
    }
}
