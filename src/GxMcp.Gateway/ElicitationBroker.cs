using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal enum ElicitationMode
    {
        Off,
        Auto,
        Strict
    }

    internal enum ElicitationAction
    {
        Accept,
        Decline,
        Cancel,
        Unavailable,
        Timeout,
        Failed
    }

    internal sealed class ElicitationOutcome
    {
        internal ElicitationOutcome(ElicitationAction action, JObject? content = null, string? detail = null)
        {
            Action = action;
            Content = content ?? new JObject();
            Detail = detail;
        }

        internal ElicitationAction Action { get; }
        internal JObject Content { get; }
        internal string? Detail { get; }
    }

    /// <summary>
    /// Server-to-client <c>elicitation/create</c> requests (MCP 2025-06-18). A session can
    /// be asked only when its client declared <c>capabilities.elicitation</c> during
    /// initialize AND the transport registered a sender for it; otherwise every request
    /// reports <see cref="ElicitationAction.Unavailable"/> and callers keep their
    /// non-interactive behaviour.
    /// </summary>
    internal static class ElicitationBroker
    {
        internal const string ModeVariable = "GXMCP_ELICITATION";
        internal const string TimeoutVariable = "GXMCP_ELICITATION_TIMEOUT_SECONDS";
        private const string RequestIdPrefix = "gxmcp-elicit-";
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

        private static readonly ConcurrentDictionary<string, bool> _capableSessions = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Func<JObject, Task>> _senders = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending = new(StringComparer.Ordinal);
        private static long _nextRequestId;

        internal static ElicitationMode? ModeOverrideForTest { get; set; }
        internal static TimeSpan? TimeoutOverrideForTest { get; set; }

        internal static ElicitationMode Mode
        {
            get
            {
                if (ModeOverrideForTest.HasValue) return ModeOverrideForTest.Value;
                return ParseMode(Environment.GetEnvironmentVariable(ModeVariable));
            }
        }

        internal static ElicitationMode ParseMode(string? raw)
        {
            switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "0":
                case "off":
                case "false":
                case "disabled":
                    return ElicitationMode.Off;
                case "strict":
                case "always":
                    return ElicitationMode.Strict;
                default:
                    return ElicitationMode.Auto;
            }
        }

        internal static TimeSpan Timeout
        {
            get
            {
                if (TimeoutOverrideForTest.HasValue) return TimeoutOverrideForTest.Value;
                string? raw = Environment.GetEnvironmentVariable(TimeoutVariable);
                return int.TryParse(raw, out int seconds) && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : DefaultTimeout;
            }
        }

        /// <summary>Records whether the initializing client can answer elicitation requests.</summary>
        internal static void ObserveInitialize(JObject request, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            var capabilities = (request["params"] as JObject)?["capabilities"] as JObject;
            if (capabilities?["elicitation"] is JObject)
                _capableSessions[sessionId] = true;
            else
                _capableSessions.TryRemove(sessionId, out _);
        }

        internal static IDisposable RegisterSender(string sessionId, Func<JObject, Task> sender)
        {
            _senders[sessionId] = sender;
            return new SenderRegistration(sessionId, sender);
        }

        internal static bool ClientSupportsElicitation(string sessionId) =>
            !string.IsNullOrEmpty(sessionId) && _capableSessions.ContainsKey(sessionId);

        internal static bool IsAvailable(string sessionId) =>
            Mode != ElicitationMode.Off
            && ClientSupportsElicitation(sessionId)
            && _senders.ContainsKey(sessionId);

        internal static void ForgetSessionForTest(string sessionId)
        {
            _capableSessions.TryRemove(sessionId, out _);
            _senders.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// Completes a pending elicitation when <paramref name="message"/> is the client's
        /// JSON-RPC response to it. Returns false for every other message.
        /// </summary>
        internal static bool TryCompleteResponse(JObject message)
        {
            if (message["method"] != null) return false;
            if (message["result"] == null && message["error"] == null) return false;
            string? id = message["id"]?.Type == JTokenType.String ? message["id"]!.ToString() : null;
            if (id == null || !id.StartsWith(RequestIdPrefix, StringComparison.Ordinal)) return false;
            if (!_pending.TryRemove(id, out var completion)) return false;
            completion.TrySetResult(message);
            return true;
        }

        internal static async Task<ElicitationOutcome> RequestAsync(
            string sessionId,
            string message,
            JObject requestedSchema,
            CancellationToken cancellationToken = default)
        {
            if (!IsAvailable(sessionId) || !_senders.TryGetValue(sessionId, out var sender))
                return new ElicitationOutcome(ElicitationAction.Unavailable);

            string id = RequestIdPrefix + Interlocked.Increment(ref _nextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                var outbound = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = "elicitation/create",
                    ["params"] = new JObject
                    {
                        ["message"] = message,
                        ["requestedSchema"] = requestedSchema
                    }
                };
                await sender(outbound).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Timeout);
                var finished = await Task.WhenAny(completion.Task, Task.Delay(System.Threading.Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
                if (finished != completion.Task)
                {
                    return cancellationToken.IsCancellationRequested
                        ? new ElicitationOutcome(ElicitationAction.Cancel, detail: "request cancelled")
                        : new ElicitationOutcome(ElicitationAction.Timeout);
                }

                var response = completion.Task.Result;
                if (response["error"] is JObject error)
                {
                    return new ElicitationOutcome(
                        ElicitationAction.Failed,
                        detail: error["message"]?.ToString() ?? "client returned an error");
                }

                var result = response["result"] as JObject;
                string action = result?["action"]?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
                return action switch
                {
                    "accept" => new ElicitationOutcome(ElicitationAction.Accept, result?["content"] as JObject),
                    "decline" => new ElicitationOutcome(ElicitationAction.Decline),
                    "cancel" => new ElicitationOutcome(ElicitationAction.Cancel),
                    _ => new ElicitationOutcome(ElicitationAction.Failed, detail: "unrecognised elicitation action '" + action + "'")
                };
            }
            catch (Exception ex)
            {
                Program.Log("[Elicitation] request failed: " + ex.Message);
                return new ElicitationOutcome(ElicitationAction.Failed, detail: ex.Message);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private sealed class SenderRegistration : IDisposable
        {
            private readonly string _sessionId;
            private readonly Func<JObject, Task> _sender;

            internal SenderRegistration(string sessionId, Func<JObject, Task> sender)
            {
                _sessionId = sessionId;
                _sender = sender;
            }

            public void Dispose()
            {
                ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, Func<JObject, Task>>>)_senders)
                    .Remove(new System.Collections.Generic.KeyValuePair<string, Func<JObject, Task>>(_sessionId, _sender));
            }
        }
    }
}
