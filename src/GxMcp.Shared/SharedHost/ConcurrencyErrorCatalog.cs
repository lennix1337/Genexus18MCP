#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace GxMcp.Common.SharedHost
{
    /// <summary>
    /// The allowed <c>dispatchState</c> values of a concurrency error (Requirement 12 criterion 2).
    /// </summary>
    internal static class ConcurrencyDispatchState
    {
        /// <summary>The request never reached the Worker process.</summary>
        internal const string NotDelivered = "not-delivered";

        /// <summary>The request reached the Worker, which exited or disconnected before it answered.</summary>
        internal const string DeliveredUnanswered = "delivered-unanswered";

        /// <summary>The Worker received the request and rejected it before any SDK save.</summary>
        internal const string DeliveredRejected = "delivered-rejected";
    }

    /// <summary>
    /// One row of the Concurrency_Error_Table (Requirement 12).
    /// </summary>
    internal sealed class ConcurrencyErrorRow
    {
        internal ConcurrencyErrorRow(
            string code,
            string[] reasons,
            bool? retryable,
            string? dispatchState,
            Func<ConcurrencyErrorContext, string> message,
            Func<ConcurrencyErrorContext, string> hint)
        {
            Code = code;
            Reasons = new ReadOnlyCollection<string>((string[])reasons.Clone());
            Retryable = retryable;
            DispatchState = dispatchState;
            Message = message;
            Hint = hint;
        }

        /// <summary>The fixed, documented error code.</summary>
        internal string Code { get; }

        /// <summary>The allowed reason values in table order; empty when the row lists none.</summary>
        internal IReadOnlyList<string> Reasons { get; }

        /// <summary>The fixed <c>retryable</c> value, or null when it depends on the Operation_Class.</summary>
        internal bool? Retryable { get; }

        /// <summary>The fixed <c>dispatchState</c> value, or null when it depends on the delivery.</summary>
        internal string? DispatchState { get; }

        internal Func<ConcurrencyErrorContext, string> Message { get; }

        internal Func<ConcurrencyErrorContext, string> Hint { get; }
    }

    /// <summary>
    /// The resolved values a row's message and hint templates read. It carries no Details,
    /// so a template cannot copy source content, payloads, Lease tokens or credentials.
    /// </summary>
    internal readonly struct ConcurrencyErrorContext
    {
        internal ConcurrencyErrorContext(string? reason, string dispatchState, bool retryable, int? holderPid)
        {
            Reason = reason;
            DispatchState = dispatchState;
            Retryable = retryable;
            HolderPid = holderPid;
        }

        internal string? Reason { get; }

        internal string DispatchState { get; }

        internal bool Retryable { get; }

        internal int? HolderPid { get; }
    }

    /// <summary>
    /// The values a raiser passes to <see cref="ConcurrencyErrorCatalog.Build"/>.
    /// </summary>
    internal sealed class ConcurrencyErrorFields
    {
        /// <summary>The configured queue wait; the upper bound of <c>retryAfterMs</c> for every retryable code except <c>TargetLockedByOtherAgent</c>.</summary>
        internal int QueueWaitMs { get; set; }

        /// <summary>The delay the raiser wants; clamped into [0, bound]. Null lets Build pick a value inside the bound.</summary>
        internal int? RetryAfterMs { get; set; }

        /// <summary>Milliseconds until the lock expiry, the bound for <c>TargetLockedByOtherAgent</c>; null means 0.</summary>
        internal long? LockRemainingMs { get; set; }

        /// <summary>
        /// Required for <c>SHARED_WORKER_RESTARTED</c> (not-delivered or delivered-unanswered).
        /// For the other rows, null or the row value.
        /// </summary>
        internal string? DispatchState { get; set; }

        /// <summary>The Operation_Class of a delivered-unanswered <c>SHARED_WORKER_RESTARTED</c>; null means no table entry.</summary>
        internal OperationClass? OperationClass { get; set; }

        /// <summary>The holder Gateway PID named in the <c>KB_WORKER_OWNED_ELSEWHERE</c> hint.</summary>
        internal int? HolderPid { get; set; }

        /// <summary>Extra structured fields merged into the error object. Never overrides a core key and never reaches hint or message.</summary>
        internal JObject? Details { get; set; }
    }

    /// <summary>
    /// The single Concurrency_Error_Table catalog (Requirement 12 criteria 1, 2, 3, 5 and 6).
    /// </summary>
    /// <remarks>
    /// The broker, the Worker and the Gateway build every concurrency error through
    /// <see cref="Build"/>, so a code carries the same <c>retryable</c> and
    /// <c>dispatchState</c> values whoever raises it.
    /// </remarks>
    internal static class ConcurrencyErrorCatalog
    {
        internal const string WorkerBusy = "WorkerBusy";
        internal const string KbPoolFull = "KB_POOL_FULL";
        internal const string KbWorkerOwnedElsewhere = "KB_WORKER_OWNED_ELSEWHERE";
        internal const string SharedWorkerIdentityMismatch = "SHARED_WORKER_IDENTITY_MISMATCH";
        internal const string SharedWorkerAttachFailed = "SHARED_WORKER_ATTACH_FAILED";
        internal const string WorkerStartupFailed = "WORKER_STARTUP_FAILED";
        internal const string SharedWorkerBackpressure = "SHARED_WORKER_BACKPRESSURE";
        internal const string SharedWorkerRestarted = "SHARED_WORKER_RESTARTED";
        internal const string TargetLockedByOtherAgent = "TargetLockedByOtherAgent";
        internal const string StaleObject = "StaleObject";
        internal const string LockCheckFailed = "LockCheckFailed";
        internal const string VersionCheckUnavailable = "VersionCheckUnavailable";

        // Default retryAfterMs when the raiser names none: the lower end of the Busy_Envelope
        // range of Requirement 3 criterion 6, capped by the bound.
        private const long DefaultRetryAfterMs = 1000;

        private static readonly string[] CoreKeys =
        {
            "code", "message", "hint", "retryable", "dispatchState", "reason", "retryAfterMs",
        };

        private static readonly ConcurrencyErrorRow[] Table =
        {
            new ConcurrencyErrorRow(
                WorkerBusy,
                new[] { "queue-wait-expired", "queue-full", "session-queue-limit" },
                true,
                ConcurrencyDispatchState.NotDelivered,
                c => c.Reason == "queue-wait-expired"
                    ? "The request waited longer than the configured queue wait for the shared Worker and was not executed."
                    : c.Reason == "queue-full"
                        ? "The shared Worker request queue is full; the request was not queued."
                        : "This session already holds the maximum number of queued requests; the request was not queued.",
                c => "Retry the request after retryAfterMs milliseconds."),

            new ConcurrencyErrorRow(
                KbPoolFull,
                new[] { "worker-limit", "max-open-kbs", "low-memory" },
                false,
                ConcurrencyDispatchState.NotDelivered,
                c => c.Reason == "low-memory"
                    ? "Available machine memory is below the configured minimum for starting another Worker."
                    : c.Reason == "worker-limit"
                        ? "The machine-wide shared Worker limit is reached."
                        : "The maximum number of open KBs is reached.",
                c => c.Reason == "low-memory"
                    ? "Close open KBs, GeneXus IDE instances, or idle MCP sessions to free memory, then open the KB again."
                    : "Close an open KB with genexus_kb action=close, or wait for idle Worker reaping, then open the KB again."),

            new ConcurrencyErrorRow(
                KbWorkerOwnedElsewhere,
                new[] { "owner-does-not-accept-attachments", "liveness-unverified" },
                false,
                ConcurrencyDispatchState.NotDelivered,
                c => c.Reason == "liveness-unverified"
                    ? "The KB Worker is owned by another Gateway whose liveness could not be verified."
                    : "The KB Worker is owned by another Gateway that does not accept attachments.",
                c => c.HolderPid.HasValue
                    ? "Ask the user to release the KB in the holder Gateway with PID "
                        + c.HolderPid.Value.ToString(CultureInfo.InvariantCulture) + ", then open the KB again."
                    : "Ask the user to release the KB in the holder Gateway, then open the KB again."),

            new ConcurrencyErrorRow(
                SharedWorkerIdentityMismatch,
                new string[0],
                false,
                ConcurrencyDispatchState.NotDelivered,
                c => "The live Worker_Host for this KB runs with a different GeneXus installation, driver, target, or Worker executable.",
                c => "Align the GeneXus installation, driver, target, and Worker executable with the live Worker_Host, or close the sessions attached to it, then open the KB again."),

            new ConcurrencyErrorRow(
                SharedWorkerAttachFailed,
                new[] { "readiness-timeout", "pipe-refused" },
                true,
                ConcurrencyDispatchState.NotDelivered,
                c => c.Reason == "readiness-timeout"
                    ? "The shared Worker_Host did not report ready within the attach readiness wait."
                    : "The shared Worker_Host refused the attach pipe connection.",
                c => "Retry the request after retryAfterMs milliseconds."),

            new ConcurrencyErrorRow(
                WorkerStartupFailed,
                new[] { "replacement-failed", "cold-start-wait-expired" },
                true,
                ConcurrencyDispatchState.NotDelivered,
                c => c.Reason == "replacement-failed"
                    ? "The replacement Worker failed to start."
                    : "The request waited longer than the cold-start wait for another Worker to finish starting.",
                c => "Retry the request after retryAfterMs milliseconds."),

            new ConcurrencyErrorRow(
                SharedWorkerBackpressure,
                new string[0],
                true,
                ConcurrencyDispatchState.NotDelivered,
                c => "The shared Worker_Host inbound command queue is full; the request was not delivered.",
                c => "Retry the request after retryAfterMs milliseconds."),

            new ConcurrencyErrorRow(
                SharedWorkerRestarted,
                new string[0],
                null,
                null,
                c => c.DispatchState == ConcurrencyDispatchState.NotDelivered
                    ? "The shared Worker exited before the queued request was delivered; the request was not executed."
                    : "The shared Worker exited or disconnected after the request was delivered and before it answered.",
                c => c.Retryable
                    ? "Retry the request after retryAfterMs milliseconds."
                    : "Read the target first to check whether the write was applied before you resend it."),

            new ConcurrencyErrorRow(
                TargetLockedByOtherAgent,
                new string[0],
                true,
                ConcurrencyDispatchState.DeliveredRejected,
                c => "The target object part is locked by another owner; the write was rejected before any save.",
                c => "Retry after the lock expiry, at the earliest after retryAfterMs milliseconds, or coordinate with the lock holder."),

            new ConcurrencyErrorRow(
                StaleObject,
                new string[0],
                false,
                ConcurrencyDispatchState.DeliveredRejected,
                c => "The base version token differs from the current version token of the object part; the write was rejected before any save.",
                c => "Re-read the object part to get its current version token before writing again."),

            new ConcurrencyErrorRow(
                LockCheckFailed,
                new string[0],
                true,
                ConcurrencyDispatchState.DeliveredRejected,
                c => "The Worker could not check the object part lock; the write was rejected before any save.",
                c => "Retry the write after retryAfterMs milliseconds."),

            new ConcurrencyErrorRow(
                VersionCheckUnavailable,
                new string[0],
                true,
                ConcurrencyDispatchState.DeliveredRejected,
                c => "The Worker could not compute the current version token of the object part; the write was rejected before any save.",
                c => "Retry the write after retryAfterMs milliseconds."),
        };

        private static readonly Dictionary<string, ConcurrencyErrorRow> ByCode = BuildIndex();

        /// <summary>The Concurrency_Error_Table, one row per code.</summary>
        internal static IReadOnlyList<ConcurrencyErrorRow> Rows { get; } = new ReadOnlyCollection<ConcurrencyErrorRow>(Table);

        /// <summary>
        /// Builds the envelope <c>error</c> object for <paramref name="code"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="fields"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The code is not in the table, the reason is outside the row, missing on a row with
        /// reasons or present on a row without reasons, or the dispatch state conflicts with the row.
        /// </exception>
        internal static JObject Build(string code, string? reason, ConcurrencyErrorFields fields)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));

            if (code == null || !ByCode.TryGetValue(code, out var row))
                throw new ArgumentException("Unknown concurrency error code.", nameof(code));

            if (row.Reasons.Count > 0)
            {
                if (string.IsNullOrEmpty(reason))
                    throw new ArgumentException("Code " + row.Code + " requires a reason.", nameof(reason));
                if (!ContainsOrdinal(row.Reasons, reason!))
                    throw new ArgumentException("The reason is not allowed for code " + row.Code + ".", nameof(reason));
            }
            else if (reason != null)
            {
                throw new ArgumentException("Code " + row.Code + " takes no reason.", nameof(reason));
            }

            string dispatchState = ResolveDispatchState(row, fields.DispatchState);
            bool retryable = row.Retryable ?? (
                string.Equals(dispatchState, ConcurrencyDispatchState.NotDelivered, StringComparison.Ordinal)
                || fields.OperationClass == OperationClass.Read);

            var context = new ConcurrencyErrorContext(reason, dispatchState, retryable, fields.HolderPid);
            var error = new JObject
            {
                ["code"] = row.Code,
                ["message"] = row.Message(context),
                ["hint"] = row.Hint(context),
                ["retryable"] = retryable,
                ["dispatchState"] = dispatchState,
            };
            if (reason != null) error["reason"] = reason;
            if (retryable) error["retryAfterMs"] = ResolveRetryAfter(row, fields);

            if (fields.Details != null)
            {
                foreach (var property in fields.Details.Properties())
                {
                    if (ContainsOrdinal(CoreKeys, property.Name)) continue;
                    error[property.Name] = property.Value.DeepClone();
                }
            }

            return error;
        }

        private static string ResolveDispatchState(ConcurrencyErrorRow row, string? requested)
        {
            if (row.DispatchState != null)
            {
                if (requested != null && !string.Equals(requested, row.DispatchState, StringComparison.Ordinal))
                    throw new ArgumentException("Code " + row.Code + " always uses dispatchState " + row.DispatchState + ".", "fields");
                return row.DispatchState;
            }

            if (string.Equals(requested, ConcurrencyDispatchState.NotDelivered, StringComparison.Ordinal)
                || string.Equals(requested, ConcurrencyDispatchState.DeliveredUnanswered, StringComparison.Ordinal))
            {
                return requested!;
            }

            throw new ArgumentException(
                "Code " + row.Code + " requires dispatchState " + ConcurrencyDispatchState.NotDelivered
                    + " or " + ConcurrencyDispatchState.DeliveredUnanswered + ".",
                "fields");
        }

        private static long ResolveRetryAfter(ConcurrencyErrorRow row, ConcurrencyErrorFields fields)
        {
            bool lockBound = string.Equals(row.Code, TargetLockedByOtherAgent, StringComparison.Ordinal);
            long bound = lockBound
                ? Math.Max(0L, fields.LockRemainingMs ?? 0L)
                : Math.Max(0L, fields.QueueWaitMs);

            if (fields.RetryAfterMs.HasValue)
                return Math.Min(Math.Max(0L, fields.RetryAfterMs.Value), bound);

            // A lock conflict is worth retrying only at the expiry; every other code uses the default delay.
            return lockBound ? bound : Math.Min(DefaultRetryAfterMs, bound);
        }

        private static bool ContainsOrdinal(IEnumerable<string> values, string value)
        {
            foreach (var candidate in values)
            {
                if (string.Equals(candidate, value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static Dictionary<string, ConcurrencyErrorRow> BuildIndex()
        {
            var index = new Dictionary<string, ConcurrencyErrorRow>(StringComparer.Ordinal);
            foreach (var row in Table) index.Add(row.Code, row);
            return index;
        }
    }
}
