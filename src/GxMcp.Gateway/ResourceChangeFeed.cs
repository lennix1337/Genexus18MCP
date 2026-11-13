using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal enum ResourceUriKind
    {
        Invalid,
        /// <summary>A resource this server serves that has no change events (index, health, skills, ...).</summary>
        Other,
        /// <summary><c>genexus://objects/{name}[/...]</c>, optionally KB-scoped.</summary>
        Object,
        /// <summary><c>genexus://kb/changes</c>, optionally KB-scoped.</summary>
        Changes
    }

    /// <summary>
    /// Recognises the resource URIs this server actually serves. Subscriptions are validated
    /// against it, and the change feed uses it to decide which subscription a change belongs to.
    /// </summary>
    internal static class ResourceUris
    {
        internal const string ChangesUri = "genexus://kb/changes";
        private const string SchemePrefix = "genexus://";
        private const int MaxLength = 512;

        internal static bool TryParse(string? uri, out ResourceUriKind kind, out string? alias, out string? name)
        {
            kind = ResourceUriKind.Invalid;
            alias = null;
            name = null;

            string trimmed = (uri ?? string.Empty).Trim();
            if (trimmed.Length <= SchemePrefix.Length || trimmed.Length > MaxLength
                || !trimmed.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (char ch in trimmed)
            {
                if (char.IsControl(ch) || char.IsWhiteSpace(ch)) return false;
            }

            string[] segments = trimmed.Substring(SchemePrefix.Length)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            // Same scoping rule as McpRouter.UnscopeResourceUri: genexus://kb/{alias}/{objects|attributes|kb}/...
            // Anything else under genexus://kb/ (skills, tool-help, health) is an unscoped static resource.
            if (Is(segments[0], "kb") && segments.Length >= 3
                && (Is(segments[2], "objects") || Is(segments[2], "attributes") || Is(segments[2], "kb")))
            {
                alias = Unescape(segments[1]).Trim().ToLowerInvariant();
                segments = segments[2..];
            }

            if (Is(segments[0], "objects") || Is(segments[0], "attributes"))
            {
                if (Is(segments[0], "objects") && segments.Length >= 2)
                {
                    kind = ResourceUriKind.Object;
                    name = Unescape(segments[1]);
                }
                else
                {
                    kind = ResourceUriKind.Other;
                }
                return true;
            }

            if (Is(segments[0], "kb"))
            {
                kind = segments.Length == 2 && Is(segments[1], "changes")
                    ? ResourceUriKind.Changes
                    : ResourceUriKind.Other;
                return true;
            }

            alias = null;
            return false;
        }

        private static bool Is(string segment, string expected)
            => string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);

        private static string Unescape(string value)
        {
            try { return Uri.UnescapeDataString(value); }
            catch (UriFormatException) { return value; }
        }
    }

    internal readonly record struct ResourceSubscription(string Uri, string KbId);

    /// <summary>One session's (or the stdio process's) subscriptions plus the way to reach it.</summary>
    internal sealed class ResourceSubscriber
    {
        internal ResourceSubscriber(IReadOnlyList<ResourceSubscription> subscriptions, Action<string> send)
        {
            Subscriptions = subscriptions;
            Send = send;
        }

        internal IReadOnlyList<ResourceSubscription> Subscriptions { get; }
        internal Action<string> Send { get; }
    }

    internal readonly record struct ResourceChange(
        string? KbAlias, string Name, string? Type, bool Deleted, long? CacheRevision, DateTime AtUtc);

    /// <summary>
    /// Turns the Worker's "object changed outside the MCP" signal into
    /// <c>notifications/resources/updated</c> for the sessions that subscribed to it, and keeps a
    /// small bounded record of recent changes that <c>genexus://kb/changes</c> serves.
    ///
    /// Cost model: with no subscriber an event costs one ring write and one counter; nothing is
    /// serialised and no timer starts. With subscribers, events inside one window are merged per
    /// object, at most <c>maxObjectsPerWindow</c> distinct objects get their own notification per
    /// window (the rest are only counted in the KB-wide changes notification), and a subscription
    /// receives at most one notification per URI per window.
    ///
    /// Nothing here is shared between sessions except the change record, which is read per KB alias;
    /// delivery always goes through the subscriber list the caller supplies.
    /// </summary>
    internal sealed class ResourceChangeFeed
    {
        internal const int RingCapacity = 64;
        private const string Reason = "external_kb_change";

        private sealed class PendingObject
        {
            public string? Alias;
            public string Name = string.Empty;
            public string? Type;
            public bool Deleted;
            public int Count;
            public long? Revision;
            public DateTime AtUtc;
        }

        private sealed class PendingKb
        {
            public int Count;
            public long? Revision;
            public DateTime AtUtc;
        }

        private readonly object _gate = new object();
        private readonly TimeSpan _window;
        private readonly int _maxObjectsPerWindow;
        private readonly Func<bool> _anySubscribers;
        private readonly Func<IReadOnlyList<ResourceSubscriber>> _subscribers;
        private readonly Action<TimeSpan, Action> _schedule;
        private readonly ResourceChange[] _ring = new ResourceChange[RingCapacity];
        private int _ringNext;
        private int _ringCount;
        private Dictionary<string, PendingObject> _pendingObjects = new Dictionary<string, PendingObject>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, PendingKb> _pendingKbs = new Dictionary<string, PendingKb>(StringComparer.OrdinalIgnoreCase);
        private bool _flushScheduled;

        private long _received;
        private long _skippedNoSubscribers;
        private long _merged;
        private long _dropped;
        private long _flushes;
        private long _sent;

        internal ResourceChangeFeed(
            TimeSpan window,
            int maxObjectsPerWindow,
            Func<bool> anySubscribers,
            Func<IReadOnlyList<ResourceSubscriber>> subscribers,
            Action<TimeSpan, Action>? schedule = null)
        {
            _window = window;
            _maxObjectsPerWindow = maxObjectsPerWindow;
            _anySubscribers = anySubscribers;
            _subscribers = subscribers;
            _schedule = schedule ?? ScheduleOnThreadPool;
        }

        internal long Received => Interlocked.Read(ref _received);
        internal long SkippedNoSubscribers => Interlocked.Read(ref _skippedNoSubscribers);
        /// <summary>Events folded into an earlier event for the same object in the same window.</summary>
        internal long Merged => Interlocked.Read(ref _merged);
        /// <summary>Per-object notifications withheld by the window cap; the KB-wide changes URI still counts them.</summary>
        internal long Dropped => Interlocked.Read(ref _dropped);
        internal long NotificationsSent => Interlocked.Read(ref _sent);

        internal void Enqueue(ResourceChange change)
        {
            Interlocked.Increment(ref _received);
            RecordInRing(change);

            if (!_anySubscribers())
            {
                Interlocked.Increment(ref _skippedNoSubscribers);
                return;
            }

            bool schedule = false;
            lock (_gate)
            {
                string alias = change.KbAlias ?? string.Empty;
                string key = alias + "\n" + change.Name;
                if (_pendingObjects.TryGetValue(key, out var existing))
                {
                    existing.Count++;
                    existing.Deleted = change.Deleted;
                    existing.AtUtc = change.AtUtc;
                    existing.Revision = Max(existing.Revision, change.CacheRevision);
                    _merged++;
                }
                else if (_pendingObjects.Count >= _maxObjectsPerWindow)
                {
                    _dropped++;
                }
                else
                {
                    _pendingObjects[key] = new PendingObject
                    {
                        Alias = change.KbAlias,
                        Name = change.Name,
                        Type = change.Type,
                        Deleted = change.Deleted,
                        Count = 1,
                        Revision = change.CacheRevision,
                        AtUtc = change.AtUtc
                    };
                }

                if (!_pendingKbs.TryGetValue(alias, out var kb))
                    _pendingKbs[alias] = kb = new PendingKb();
                kb.Count++;
                kb.AtUtc = change.AtUtc;
                kb.Revision = Max(kb.Revision, change.CacheRevision);

                if (!_flushScheduled)
                {
                    _flushScheduled = true;
                    schedule = true;
                }
            }

            if (schedule) _schedule(_window, Flush);
        }

        /// <summary>Delivers what the current window collected. Called by the scheduler; tests call it directly.</summary>
        internal void Flush()
        {
            Dictionary<string, PendingObject> objects;
            Dictionary<string, PendingKb> kbs;
            lock (_gate)
            {
                objects = _pendingObjects;
                kbs = _pendingKbs;
                _pendingObjects = new Dictionary<string, PendingObject>(StringComparer.OrdinalIgnoreCase);
                _pendingKbs = new Dictionary<string, PendingKb>(StringComparer.OrdinalIgnoreCase);
                _flushScheduled = false;
            }

            Interlocked.Increment(ref _flushes);
            if (kbs.Count == 0) return;

            foreach (var subscriber in _subscribers())
            {
                foreach (var subscription in subscriber.Subscriptions)
                {
                    if (!ResourceUris.TryParse(subscription.Uri, out var kind, out var uriAlias, out var uriName))
                        continue;

                    if (kind == ResourceUriKind.Object)
                    {
                        foreach (var pending in objects.Values)
                        {
                            if (string.Equals(pending.Name, uriName, StringComparison.OrdinalIgnoreCase)
                                && AliasMatches(uriAlias, subscription.KbId, pending.Alias))
                            {
                                Send(subscriber, BuildPayload(subscription.Uri, pending.Alias, pending.Revision,
                                    pending.AtUtc, pending.Count, pending.Deleted, pending.Name));
                            }
                        }
                    }
                    else if (kind == ResourceUriKind.Changes)
                    {
                        foreach (var kb in kbs)
                        {
                            string? alias = kb.Key.Length == 0 ? null : kb.Key;
                            if (AliasMatches(uriAlias, subscription.KbId, alias))
                            {
                                Send(subscriber, BuildPayload(subscription.Uri, alias, kb.Value.Revision,
                                    kb.Value.AtUtc, kb.Value.Count, deleted: null, name: null));
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// <c>resources/read</c> of the changes resource, or null when <paramref name="request"/> is
        /// something else. An unscoped read needs the session's selected KB.
        /// </summary>
        internal JObject? HandleRead(JObject request, string? sessionKbAlias)
        {
            if (!string.Equals(request["method"]?.ToString(), "resources/read", StringComparison.Ordinal))
                return null;
            string requested = (request["params"]?["uri"]?.ToString() ?? string.Empty).Trim();
            if (!ResourceUris.TryParse(requested, out var kind, out var uriAlias, out _)
                || kind != ResourceUriKind.Changes)
                return null;

            var id = request["id"]?.DeepClone();
            string alias = !string.IsNullOrWhiteSpace(uriAlias) ? uriAlias! : (sessionKbAlias ?? string.Empty).Trim().ToLowerInvariant();
            if (alias.Length == 0)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JObject
                    {
                        ["code"] = -32602,
                        ["message"] = "No KB is selected for this session; read the KB-scoped form genexus://kb/<alias>/kb/changes.",
                        ["data"] = new JObject { ["field"] = "uri" }
                    }
                };
            }

            var changes = new JArray();
            foreach (var change in SnapshotRing(alias))
            {
                changes.Add(new JObject
                {
                    ["name"] = change.Name,
                    ["type"] = change.Type,
                    ["deleted"] = change.Deleted,
                    ["at"] = change.AtUtc.ToString("o"),
                    ["cacheRevision"] = change.CacheRevision
                });
            }

            var body = new JObject
            {
                ["kbAlias"] = alias,
                ["detection"] = "polling; latency is about the Worker watcher interval (5 s)",
                ["since"] = "gateway start, last " + RingCapacity + " changes across all KBs, newest first",
                ["changes"] = changes,
                ["counters"] = new JObject
                {
                    ["received"] = Received,
                    ["skippedNoSubscribers"] = SkippedNoSubscribers,
                    ["merged"] = Merged,
                    ["dropped"] = Dropped,
                    ["notificationsSent"] = NotificationsSent
                }
            };

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["resultType"] = "complete",
                    ["ttlMs"] = 0,
                    ["cacheScope"] = "private",
                    ["contents"] = new JArray(new JObject
                    {
                        ["uri"] = requested,
                        ["mimeType"] = "application/json",
                        ["text"] = body.ToString(Newtonsoft.Json.Formatting.Indented)
                    })
                }
            };
        }

        private static bool AliasMatches(string? uriAlias, string subscriptionKbId, string? changeAlias)
        {
            // A change the Worker could not attribute to a KB is delivered: missing an update is worse
            // than a spurious one, and it is still only sent to sessions that subscribed.
            if (string.IsNullOrEmpty(changeAlias)) return true;
            // A KB-scoped URI names its KB explicitly; otherwise the KB the session had selected
            // when it subscribed decides; with neither, any KB matches.
            string? wanted = !string.IsNullOrEmpty(uriAlias) ? uriAlias : subscriptionKbId;
            return string.IsNullOrEmpty(wanted)
                || string.Equals(wanted, changeAlias, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildPayload(
            string uri, string? alias, long? revision, DateTime atUtc, int count, bool? deleted, string? name)
        {
            var payload = new JObject
            {
                ["uri"] = uri,
                ["reason"] = Reason,
                ["external"] = true,
                ["updatedAt"] = atUtc,
                ["changes"] = count
            };
            if (!string.IsNullOrEmpty(alias)) payload["kbAlias"] = alias;
            if (revision.HasValue) payload["cacheRevision"] = revision.Value;
            if (name != null) payload["name"] = name;
            if (deleted == true) payload["deleted"] = true;
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/resources/updated",
                ["params"] = payload
            };
        }

        private void Send(ResourceSubscriber subscriber, JObject notification)
        {
            try
            {
                subscriber.Send(notification.ToString(Newtonsoft.Json.Formatting.None));
                Interlocked.Increment(ref _sent);
            }
            catch
            {
                // One unreachable session must not stop delivery to the others.
            }
        }

        private void RecordInRing(ResourceChange change)
        {
            lock (_gate)
            {
                _ring[_ringNext] = change;
                _ringNext = (_ringNext + 1) % RingCapacity;
                if (_ringCount < RingCapacity) _ringCount++;
            }
        }

        private List<ResourceChange> SnapshotRing(string alias)
        {
            var result = new List<ResourceChange>();
            lock (_gate)
            {
                for (int i = 1; i <= _ringCount; i++)
                {
                    var entry = _ring[(_ringNext - i + RingCapacity) % RingCapacity];
                    if (string.IsNullOrEmpty(entry.KbAlias)
                        || string.Equals(entry.KbAlias, alias, StringComparison.OrdinalIgnoreCase))
                        result.Add(entry);
                }
            }
            return result;
        }

        private static long? Max(long? a, long? b)
            => !a.HasValue ? b : !b.HasValue ? a : Math.Max(a.Value, b.Value);

        private static void ScheduleOnThreadPool(TimeSpan delay, Action action)
            => _ = Task.Delay(delay).ContinueWith(_ =>
            {
                try { action(); } catch { }
            }, TaskScheduler.Default);
    }
}
