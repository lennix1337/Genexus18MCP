using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    internal sealed class HttpSessionRegistry
    {
        public event Action<string>? SessionRemoved;
        private readonly ConcurrentDictionary<string, HttpSessionState> _sessions = new ConcurrentDictionary<string, HttpSessionState>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _sessionIdleTimeout;
        private readonly int _maxQueuedMessagesPerSession;

        public HttpSessionRegistry(TimeSpan sessionIdleTimeout, int maxQueuedMessagesPerSession = 128)
        {
            _sessionIdleTimeout = sessionIdleTimeout;
            _maxQueuedMessagesPerSession = maxQueuedMessagesPerSession;
        }

        public HttpSessionState Create()
        {
            CleanupExpired();

            var session = new HttpSessionState
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            };

            _sessions[session.Id] = session;
            return session;
        }

        public bool TryGet(string sessionId, out HttpSessionState? session)
        {
            session = null;
            if (string.IsNullOrWhiteSpace(sessionId)) return false;

            if (!_sessions.TryGetValue(sessionId, out var found)) return false;
            if (IsExpired(found))
            {
                if (_sessions.TryRemove(sessionId, out _)) SessionRemoved?.Invoke(sessionId);
                return false;
            }

            found.LastSeenUtc = DateTime.UtcNow;
            session = found;
            return true;
        }

        public bool Remove(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            bool removed = _sessions.TryRemove(sessionId, out _);
            if (removed) SessionRemoved?.Invoke(sessionId);
            return removed;
        }

        public IReadOnlyCollection<HttpSessionState> ActiveSessions
        {
            get
            {
                CleanupExpired();
                return _sessions.Values.ToArray();
            }
        }

        /// <summary>Cheap "is anyone listening" check; does not sweep expired sessions.</summary>
        internal bool AnySessionHasResourceSubscriptions()
        {
            foreach (var session in _sessions.Values)
            {
                if (session.HasResourceSubscriptions) return true;
            }
            return false;
        }

        public void Enqueue(HttpSessionState session, string payload)
        {
            lock (session.PendingMessages)
            {
                session.PendingMessages.Enqueue(payload);
                while (session.PendingMessages.Count > _maxQueuedMessagesPerSession)
                {
                    session.PendingMessages.Dequeue();
                }
            }
        }

        public int CleanupExpired()
        {
            int removed = 0;
            foreach (var pair in _sessions.ToArray())
            {
                if (IsExpired(pair.Value) && _sessions.TryRemove(pair.Key, out _))
                {
                    SessionRemoved?.Invoke(pair.Key);
                    removed++;
                }
            }

            return removed;
        }

        private bool IsExpired(HttpSessionState session)
        {
            return (DateTime.UtcNow - session.LastSeenUtc) > _sessionIdleTimeout;
        }
    }

    internal sealed class HttpSessionState
    {
        private readonly object _subscriptionLock = new object();

        public string Id { get; set; } = "";
        public string ProtocolVersion { get; set; } = McpRouter.SupportedProtocolVersion;
        public DateTime CreatedUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? ActiveKbAlias { get; set; }
        public Queue<string> PendingMessages { get; } = new Queue<string>();

        /// <summary>
        /// Resource subscriptions are scoped to this HTTP session. The gateway
        /// never keeps a process-wide subscription set because that would allow
        /// one client to observe another client's KB/resource stream.
        /// </summary>
        private readonly Dictionary<string, OwnershipFence> _subscribedResources =
            new Dictionary<string, OwnershipFence>(StringComparer.Ordinal);

        public bool SubscribeResource(string uri)
            => SubscribeResource(uri, new OwnershipFence(Id, string.Empty, 0));

        public bool SubscribeResource(string uri, OwnershipFence ownership)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            lock (_subscriptionLock) return _subscribedResources.TryAdd(uri.Trim(), ownership);
        }

        public bool UnsubscribeResource(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            lock (_subscriptionLock) return _subscribedResources.Remove(uri.Trim());
        }

        public bool IsSubscribedToResource(string uri)
            => IsSubscribedToResource(uri, null);

        public bool IsSubscribedToResource(string uri, OwnershipFence? ownership)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            lock (_subscriptionLock)
                return _subscribedResources.TryGetValue(uri.Trim(), out var fence)
                    && (ownership == null || fence.Matches(ownership));
        }

        public bool HasResourceSubscriptions
        {
            get { lock (_subscriptionLock) return _subscribedResources.Count > 0; }
        }

        public void ClearResourceSubscriptions()
        {
            lock (_subscriptionLock) _subscribedResources.Clear();
        }

        internal ResourceSubscription[] SnapshotResourceSubscriptions()
        {
            lock (_subscriptionLock)
            {
                return _subscribedResources
                    .Select(pair => new ResourceSubscription(pair.Key, pair.Value.KbId))
                    .ToArray();
            }
        }

        public IReadOnlyCollection<string> SubscribedResources
        {
            get
            {
                lock (_subscriptionLock)
                {
                    return _subscribedResources.Keys.ToArray();
                }
            }
        }
    }
}
