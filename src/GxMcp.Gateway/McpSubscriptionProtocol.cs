using System;
using System.Collections.Concurrent;
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
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ProcessSubscriptions =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);

        internal static JObject? Handle(JObject request, string sessionId, HttpSessionRegistry registry)
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

            if (string.Equals(sessionId, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                var subscriptions = ProcessSubscriptions.GetOrAdd(sessionId,
                    _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                bool changedStdio = string.Equals(method, "resources/subscribe", StringComparison.Ordinal)
                    ? subscriptions.TryAdd(uri, 0)
                    : subscriptions.TryRemove(uri, out _);
                return BuildSuccess(id, method, uri, changedStdio);
            }

            if (!registry.TryGet(sessionId, out var session) || session == null)
            {
                return Error(id, -32001, "Resource subscriptions require a valid MCP session.",
                    new JObject { ["sessionId"] = sessionId ?? string.Empty });
            }

            bool changed = string.Equals(method, "resources/subscribe", StringComparison.Ordinal)
                ? session.SubscribeResource(uri)
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

        internal static bool IsSubscribed(HttpSessionState session, string uri)
            => session != null && session.IsSubscribedToResource(uri);

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
