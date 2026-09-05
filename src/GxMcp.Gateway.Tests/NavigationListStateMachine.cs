using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    internal enum NavigationListDecisionKind
    {
        Process,
        Retry,
        Exhausted,
        Fail
    }

    internal sealed class NavigationListPageDecision
    {
        internal NavigationListDecisionKind Kind { get; }
        internal int? NextOffset { get; }
        internal string Reason { get; }

        private NavigationListPageDecision(NavigationListDecisionKind kind, int? nextOffset, string reason)
        {
            Kind = kind;
            NextOffset = nextOffset;
            Reason = reason;
        }

        internal static NavigationListPageDecision Process(int? nextOffset, string reason)
            => new NavigationListPageDecision(NavigationListDecisionKind.Process, nextOffset, reason);

        internal static NavigationListPageDecision Retry(string reason)
            => new NavigationListPageDecision(NavigationListDecisionKind.Retry, null, reason);

        internal static NavigationListPageDecision Exhausted(string reason)
            => new NavigationListPageDecision(NavigationListDecisionKind.Exhausted, null, reason);

        internal static NavigationListPageDecision Fail(string reason)
            => new NavigationListPageDecision(NavigationListDecisionKind.Fail, null, reason);
    }

    internal static class NavigationListStateMachine
    {
        internal static NavigationListPageDecision Evaluate(JObject? payload, bool isError, int offset)
        {
            if (payload == null) return NavigationListPageDecision.Fail("listing returned no payload");
            if (IsRetryable(payload)) return NavigationListPageDecision.Retry("listing is not complete yet");
            if (isError) return NavigationListPageDecision.Fail("listing returned a non-retryable error");

            var items = payload["results"] as JArray ?? payload["items"] as JArray;
            if (items == null) return NavigationListPageDecision.Retry("listing page did not contain results or items");

            var hasMore = payload["hasMore"];
            if (hasMore == null || hasMore.Type != JTokenType.Boolean)
                return NavigationListPageDecision.Retry("listing page did not contain a boolean hasMore value");

            if (hasMore.Value<bool>())
            {
                int? nextOffset = ReadNextOffset(payload);
                if (!nextOffset.HasValue || nextOffset.Value <= offset)
                    return NavigationListPageDecision.Retry("listing page did not provide a forward nextOffset");
                return NavigationListPageDecision.Process(nextOffset, "listing page has a forward continuation");
            }

            if (items.Count == 0)
                return NavigationListPageDecision.Exhausted("listing explicitly reported an empty terminal page");
            return NavigationListPageDecision.Process(null, "listing explicitly reported its terminal page");
        }

        private static bool IsRetryable(JObject payload)
        {
            string? code = payload["code"]?.ToString();
            string? status = payload["status"]?.ToString();
            string? indexStatus = payload["indexStatus"]?.ToString();
            return string.Equals(code, "IndexNotReady", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Indexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase)
                || string.Equals(indexStatus, "UltraLiteReady", StringComparison.OrdinalIgnoreCase)
                || payload["partial"]?.Value<bool?>() == true
                || payload["_meta"]?["partial"]?.Value<bool?>() == true
                || payload["retriable"]?.Value<bool?>() == true;
        }

        private static int? ReadNextOffset(JObject payload)
        {
            var next = payload["nextOffset"] ?? payload["pagination"]?["nextOffset"];
            return next?.Type == JTokenType.Integer ? next.Value<int?>() : null;
        }
    }
}
