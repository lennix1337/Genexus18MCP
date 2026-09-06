using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal static class McpModernSubscriptionProtocol
    {
        internal const string ListenMethod = "subscriptions/listen";

        internal static bool IsListenRequest(JObject request)
            => string.Equals(request?["method"]?.ToString(), ListenMethod, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 2026-07-28 subscription stream is transport-scoped rather than
    /// session-scoped. Each POST owns one bounded stream and receives only the
    /// notification classes/URIs it explicitly requested.
    /// </summary>
    internal sealed class McpModernSubscriptionRegistry
    {
        internal const int DefaultMaxSubscriptions = 128;
        internal const int DefaultQueueCapacity = 64;

        private readonly int _maxSubscriptions;
        private readonly int _queueCapacity;
        private readonly object _gate = new object();
        private readonly ConcurrentDictionary<string, McpModernSubscription> _active =
            new ConcurrentDictionary<string, McpModernSubscription>(StringComparer.Ordinal);

        internal McpModernSubscriptionRegistry(
            int maxSubscriptions = DefaultMaxSubscriptions,
            int queueCapacity = DefaultQueueCapacity)
        {
            if (maxSubscriptions < 1) throw new ArgumentOutOfRangeException(nameof(maxSubscriptions));
            if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
            _maxSubscriptions = maxSubscriptions;
            _queueCapacity = queueCapacity;
        }

        internal int Count => _active.Count;
        internal int MaxSubscriptions => _maxSubscriptions;
        internal int QueueCapacity => _queueCapacity;

        internal IReadOnlyCollection<McpModernSubscription> Active
            => _active.Values.ToArray();

        internal bool TryOpen(
            JObject request,
            out McpModernSubscription? subscription,
            out JObject? error)
        {
            subscription = null;
            error = null;

            var id = request["id"];
            if (id == null || id.Type == JTokenType.Null)
            {
                error = Error(id, -32602,
                    "subscriptions/listen requires a JSON-RPC request id.",
                    new JObject { ["field"] = "id" });
                return false;
            }

            if (!McpModernSubscriptionFilter.TryParse(
                    request["params"] as JObject,
                    out var filter,
                    out error))
            {
                if (error != null)
                    error["id"] = id.DeepClone();
                return false;
            }

            lock (_gate)
            {
                if (_active.Count >= _maxSubscriptions)
                {
                    error = Error(id, -32025,
                        "The subscriptions/listen capacity is full; retry after an active stream closes.",
                        new JObject
                        {
                            ["capacity"] = _maxSubscriptions,
                            ["retryAfterMs"] = 1000
                        });
                    return false;
                }

                var candidate = new McpModernSubscription(
                    Guid.NewGuid().ToString("N"),
                    filter!,
                    _queueCapacity);
                if (!_active.TryAdd(candidate.Id, candidate))
                {
                    error = Error(id, -32025,
                        "The subscriptions/listen stream could not be allocated; retry the request.",
                        new JObject { ["retryAfterMs"] = 1000 });
                    return false;
                }

                subscription = candidate;
                return true;
            }
        }

        internal bool Remove(string subscriptionId, out McpModernSubscription? removed)
        {
            if (_active.TryRemove(subscriptionId, out removed))
            {
                removed.Complete();
                return true;
            }

            removed = null;
            return false;
        }

        private static JObject Error(JToken? id, int code, string message, JObject data)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["data"] = data
                }
            };
    }

    internal sealed class McpModernSubscriptionFilter
    {
        private const int MaxResourceUris = 128;

        private McpModernSubscriptionFilter(
            bool toolsListChanged,
            bool promptsListChanged,
            bool resourcesListChanged,
            IReadOnlyCollection<string> resourceSubscriptions,
            JObject grantedNotifications)
        {
            ToolsListChanged = toolsListChanged;
            PromptsListChanged = promptsListChanged;
            ResourcesListChanged = resourcesListChanged;
            ResourceSubscriptions = new HashSet<string>(resourceSubscriptions, StringComparer.Ordinal);
            GrantedNotifications = grantedNotifications;
        }

        internal bool ToolsListChanged { get; }
        internal bool PromptsListChanged { get; }
        internal bool ResourcesListChanged { get; }
        internal IReadOnlySet<string> ResourceSubscriptions { get; }
        internal JObject GrantedNotifications { get; }

        internal static bool TryParse(
            JObject? parameters,
            out McpModernSubscriptionFilter? filter,
            out JObject? error)
        {
            filter = null;
            error = null;
            var notifications = parameters?["notifications"] as JObject;
            if (notifications == null)
            {
                error = Error(-32602,
                    "subscriptions/listen requires params.notifications.",
                    new JObject { ["field"] = "params.notifications" });
                return false;
            }

            bool tools = ReadBoolean(notifications, "toolsListChanged", out bool toolsValid);
            ReadBoolean(notifications, "promptsListChanged", out bool promptsValid);
            bool resources = ReadBoolean(notifications, "resourcesListChanged", out bool resourcesValid);
            if (!toolsValid || !promptsValid || !resourcesValid)
            {
                error = Error(-32602,
                    "Subscription list-change filters must be boolean.",
                    new JObject { ["field"] = "params.notifications" });
                return false;
            }

            var resourceUris = new HashSet<string>(StringComparer.Ordinal);
            var requestedUris = notifications["resourceSubscriptions"];
            if (requestedUris != null && requestedUris.Type != JTokenType.Array)
            {
                error = Error(-32602,
                    "resourceSubscriptions must be an array of absolute URIs.",
                    new JObject { ["field"] = "params.notifications.resourceSubscriptions" });
                return false;
            }

            if (requestedUris is JArray uriArray)
            {
                if (uriArray.Count > MaxResourceUris)
                {
                    error = Error(-32602,
                        $"resourceSubscriptions cannot contain more than {MaxResourceUris} URIs.",
                        new JObject
                        {
                            ["field"] = "params.notifications.resourceSubscriptions",
                            ["maxItems"] = MaxResourceUris
                        });
                    return false;
                }

                foreach (var token in uriArray)
                {
                    string uri = token?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out _))
                    {
                        error = Error(-32602,
                            "resourceSubscriptions must contain only absolute URIs.",
                            new JObject { ["field"] = "params.notifications.resourceSubscriptions" });
                        return false;
                    }

                    resourceUris.Add(uri);
                }
            }

            // GeneXus currently publishes tool/resource list changes and resource
            // updates. Prompt list changes are intentionally not acknowledged until
            // the server exposes that capability.
            var granted = new JObject();
            if (tools) granted["toolsListChanged"] = true;
            if (resources) granted["resourcesListChanged"] = true;
            if (resourceUris.Count > 0)
                granted["resourceSubscriptions"] = new JArray(resourceUris.OrderBy(x => x, StringComparer.Ordinal));

            filter = new McpModernSubscriptionFilter(
                tools,
                promptsListChanged: false,
                resources,
                resourceUris,
                granted);
            return true;
        }

        private static bool ReadBoolean(JObject obj, string name, out bool valid)
        {
            if (obj[name] == null)
            {
                valid = true;
                return false;
            }

            valid = obj[name]!.Type == JTokenType.Boolean;
            return valid && obj[name]!.Value<bool>();
        }

        private static JObject Error(int code, string message, JObject data)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JValue.CreateNull(),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["data"] = data
                }
            };
    }

    internal sealed class McpModernSubscription
    {
        private readonly Channel<string> _events;
        private readonly McpModernSubscriptionFilter _filter;

        internal McpModernSubscription(
            string id,
            McpModernSubscriptionFilter filter,
            int queueCapacity)
        {
            Id = id;
            _filter = filter;
            _events = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        internal string Id { get; }
        internal ChannelReader<string> Reader => _events.Reader;
        internal JObject GrantedNotifications => (JObject)_filter.GrantedNotifications.DeepClone();

        internal bool TryQueue(string method, object? payload)
        {
            if (!Matches(method, payload)) return false;

            var parameters = payload is JObject obj
                ? obj.DeepClone()
                : (payload is JToken token ? token.DeepClone() : JToken.FromObject(payload ?? new JObject()));
            if (parameters.Type != JTokenType.Object)
                parameters = new JObject { ["value"] = parameters };

            var parameterObject = (JObject)parameters;
            var metadata = parameterObject["_meta"] as JObject ?? new JObject();
            metadata["io.modelcontextprotocol/subscriptionId"] = Id;
            parameterObject["_meta"] = metadata;

            var notification = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameterObject
            };

            return _events.Writer.TryWrite(notification.ToString(Newtonsoft.Json.Formatting.None));
        }

        internal void Complete() => _events.Writer.TryComplete();

        private bool Matches(string method, object? payload)
        {
            switch (method)
            {
                case "notifications/tools/list_changed":
                    return _filter.ToolsListChanged;
                case "notifications/prompts/list_changed":
                    return _filter.PromptsListChanged;
                case "notifications/resources/list_changed":
                    return _filter.ResourcesListChanged;
                case "notifications/resources/updated":
                    {
                        if (_filter.ResourceSubscriptions.Count == 0) return false;
                        try
                        {
                            var obj = payload as JObject ?? (payload == null ? null : JObject.FromObject(payload));
                            if (obj == null) return false;

                            // Legacy notifications keep `uri` stable for existing
                            // clients. Modern scoped notifications also expose a
                            // KB-qualified `resourceUri`; accepting either lets a
                            // subscriber opt into the exact identity it understands
                            // without broadening a subscription to same-named objects
                            // in another open KB.
                            foreach (var candidate in new[]
                            {
                                obj["resourceUri"]?.ToString()?.Trim(),
                                obj["uri"]?.ToString()?.Trim()
                            })
                            {
                                if (!string.IsNullOrWhiteSpace(candidate)
                                    && _filter.ResourceSubscriptions.Contains(candidate))
                                    return true;
                            }

                            return false;
                        }
                        catch { return false; }
                    }
                default:
                    return false;
            }
        }
    }
}
