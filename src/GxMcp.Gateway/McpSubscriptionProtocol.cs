using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Session-scoped adapter for the legacy MCP resource subscription methods.
    /// The 2026 sessionless transport has no server-side session to own a
    /// subscription, so it must negotiate a different stream/capability first;
    /// silently accepting the request would leak updates across clients.
    /// </summary>
    internal static class McpSubscriptionProtocol
    {
        // The stdio transport is one client per process, so its subscriptions live for the process
        // (cleared again by `initialize`). The value is the fence taken at subscribe time.
        private static readonly ConcurrentDictionary<string, OwnershipFence> StdioSubscriptions =
            new ConcurrentDictionary<string, OwnershipFence>(StringComparer.Ordinal);

        internal static void ClearSession(string sessionId, HttpSessionRegistry registry)
        {
            if (string.Equals(sessionId, "stdio", StringComparison.OrdinalIgnoreCase))
                StdioSubscriptions.Clear();
            else if (registry.TryGet(sessionId, out var session) && session != null)
                session.ClearResourceSubscriptions();
        }

        internal static bool HasSubscribers(HttpSessionRegistry registry)
            => !StdioSubscriptions.IsEmpty || registry.AnySessionHasResourceSubscriptions();

        /// <summary>
        /// Every subscriber with at least one subscription. <paramref name="stdioSend"/> reaches the stdio
        /// client; <paramref name="httpSend"/> reaches one HTTP session only.
        /// </summary>
        internal static IReadOnlyList<ResourceSubscriber> SnapshotSubscribers(
            HttpSessionRegistry registry, Action<string> stdioSend, Action<HttpSessionState, string> httpSend)
        {
            var subscribers = new List<ResourceSubscriber>();
            if (!StdioSubscriptions.IsEmpty)
            {
                subscribers.Add(new ResourceSubscriber(
                    StdioSubscriptions.Select(pair => new ResourceSubscription(pair.Key, pair.Value.KbId)).ToArray(),
                    stdioSend));
            }

            foreach (var session in registry.ActiveSessions)
            {
                var subscriptions = session.SnapshotResourceSubscriptions();
                if (subscriptions.Length == 0) continue;
                var target = session;
                subscribers.Add(new ResourceSubscriber(subscriptions, json => httpSend(target, json)));
            }
            return subscribers;
        }

        internal static void ResetStdioForTest() => StdioSubscriptions.Clear();

        internal static JObject? Handle(JObject request, string sessionId, HttpSessionRegistry registry, OwnershipFence? ownership = null)
        {
            string method = request["method"]?.ToString() ?? string.Empty;
            if (!string.Equals(method, "resources/subscribe", StringComparison.Ordinal)
                && !string.Equals(method, "resources/unsubscribe", StringComparison.Ordinal))
            {
                return null;
            }

            var id = request["id"]?.DeepClone();
            if (McpRouter.IsModernRequest(request))
            {
                return Error(id, -32024,
                    "Resource subscriptions require a session-bound MCP transport; the 2026 sessionless stream has no negotiated subscription capability.",
                    new JObject
                    {
                        ["method"] = method,
                        ["requiredTransport"] = "legacy-session-sse"
                    });
            }

            var parameters = request["params"] as JObject;
            string uri = parameters?["uri"]?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out _))
            {
                return Error(id, -32602, "Resource subscription requires an absolute uri.",
                    new JObject { ["field"] = "uri" });
            }

            if (!ResourceUris.TryParse(uri, out _, out _, out _))
            {
                return Error(id, -32602,
                    "Resource subscription requires a genexus:// resource URI this server serves (genexus://objects/{name}, genexus://kb/changes, or their KB-scoped genexus://kb/{alias}/... forms).",
                    new JObject { ["field"] = "uri", ["uri"] = uri });
            }

            if (string.Equals(sessionId, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                bool changedStdio = string.Equals(method, "resources/subscribe", StringComparison.Ordinal)
                    ? StdioSubscriptions.TryAdd(uri, ownership ?? new OwnershipFence(sessionId, string.Empty, 0))
                    : StdioSubscriptions.TryRemove(uri, out _);
                return BuildSuccess(id, method, uri, changedStdio);
            }

            if (!registry.TryGet(sessionId, out var session) || session == null)
            {
                return Error(id, -32001, "Resource subscriptions require a valid MCP session.",
                    new JObject { ["sessionId"] = sessionId ?? string.Empty });
            }

            bool changed = string.Equals(method, "resources/subscribe", StringComparison.Ordinal)
                ? session.SubscribeResource(uri, ownership ?? new OwnershipFence(sessionId, string.Empty, 0))
                : session.UnsubscribeResource(uri);

            return BuildSuccess(id, method, uri, changed);
        }

        private static JObject BuildSuccess(JToken? id, string method, string uri, bool changed)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["resultType"] = "complete",
                    ["subscribed"] = method == "resources/subscribe",
                    ["changed"] = changed,
                    ["uri"] = uri,
                    ["cacheScope"] = "private",
                    ["ttlMs"] = 0
                }
            };
        }

        internal static bool IsSubscribed(HttpSessionState session, string uri, OwnershipFence? ownership = null)
            => session != null && session.IsSubscribedToResource(uri, ownership);

        private static JObject Error(JToken? id, int code, string message, JObject data)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["data"] = data
                }
            };
    }
}
