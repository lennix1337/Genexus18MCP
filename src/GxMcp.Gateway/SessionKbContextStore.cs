using System;
using System.Collections.Concurrent;

namespace GxMcp.Gateway
{
    internal sealed class SessionKbContextStore
    {
        private sealed class Entry
        {
            public string? Alias { get; set; }
            public string OwnerScopeId { get; set; } = string.Empty;
            public string? KbId { get; set; }
            public long ContextGeneration { get; set; }
            public KbUseLease? Lease { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }

        private readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        internal sealed class Snapshot
        {
            public Snapshot(string ownerScopeId, string kbId, long contextGeneration, KbUseLease? lease)
            { OwnerScopeId = ownerScopeId; KbId = kbId; ContextGeneration = contextGeneration; Lease = lease; }
            public string OwnerScopeId { get; }
            public string KbId { get; }
            public long ContextGeneration { get; }
            public KbUseLease? Lease { get; }
        }

        private readonly TimeSpan _idleTimeout;

        public SessionKbContextStore(TimeSpan idleTimeout)
        {
            if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
            _idleTimeout = idleTimeout;
        }

        public string? Get(string sessionId)
        {
            return TryGet(sessionId, out var alias) ? alias : null;
        }

        public bool TryGet(string sessionId, out string? alias)
        {
            alias = null;
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            CleanupExpired();
            if (!_entries.TryGetValue(sessionId, out var entry)) return false;

            entry.LastSeenUtc = DateTime.UtcNow;
            alias = entry.Alias;
            return true;
        }

        public bool Initialize(string sessionId, string? alias)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session id is required.", nameof(sessionId));
            CleanupExpired();
            return _entries.TryAdd(sessionId, new Entry
            {
                OwnerScopeId = sessionId,
                Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
                LastSeenUtc = DateTime.UtcNow
            });
        }

        public void Set(string sessionId, string alias)
        {
            Validate(sessionId, alias);
            _entries.TryGetValue(sessionId, out var prior);
            _entries[sessionId] = new Entry
            {
                OwnerScopeId = sessionId,
                Alias = alias.Trim(),
                ContextGeneration = (prior?.ContextGeneration ?? 0) + 1,
                LastSeenUtc = DateTime.UtcNow
            };
        }

        public void Set(string sessionId, string alias, string kbId, KbUseLease lease)
        {
            Validate(sessionId, alias);
            if (string.IsNullOrWhiteSpace(kbId)) throw new ArgumentException("KB id is required.", nameof(kbId));
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            CleanupExpired();
            _entries.TryGetValue(sessionId, out var prior);
            _entries[sessionId] = new Entry
            {
                OwnerScopeId = sessionId, Alias = alias.Trim(), KbId = kbId,
                ContextGeneration = (prior?.ContextGeneration ?? 0) + 1, Lease = lease, LastSeenUtc = DateTime.UtcNow
            };
        }

        public bool TryGetSnapshot(string sessionId, out Snapshot? snapshot)
        {
            snapshot = null;
            if (!TryGetEntry(sessionId, out var entry) || string.IsNullOrWhiteSpace(entry.KbId)) return false;
            snapshot = new Snapshot(entry.OwnerScopeId, entry.KbId!, entry.ContextGeneration, entry.Lease);
            return true;
        }

        public bool RefreshLease(string sessionId, KbUseLease lease)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || lease == null) return false;
            if (!_entries.TryGetValue(sessionId, out var entry)
                || !string.Equals(entry.KbId, lease.KbId, StringComparison.Ordinal)
                || !string.Equals(entry.Lease?.Token, lease.Token, StringComparison.Ordinal)
                || entry.ContextGeneration != lease.ContextGeneration)
                return false;
            entry.Lease = lease;
            entry.LastSeenUtc = DateTime.UtcNow;
            return true;
        }

        public void Clear(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            _entries.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// True when a live session entry still points at <paramref name="token"/>.
        /// This is the reachability question the lease registry asks before
        /// dropping a terminal entry: every read that can observe a terminal lease
        /// state takes its token from one of these snapshots.
        /// </summary>
        /// <remarks>
        /// Deliberately a pure read. It does not call <see cref="CleanupExpired"/>
        /// and does not refresh <c>LastSeenUtc</c>, so asking the question can
        /// neither retire a session nor extend one. Liveness stays exactly what
        /// <see cref="TryGetEntry"/> and <see cref="CleanupExpired"/> decide, and the
        /// stdio exemption documented in <see cref="CleanupExpired"/> applies here
        /// without this method having to restate it.
        /// </remarks>
        internal bool IsLeaseTokenReferenced(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            foreach (var pair in _entries)
            {
                var lease = pair.Value.Lease;
                if (lease != null && string.Equals(lease.Token, token, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Number of live sessions whose selected alias or canonical KB id satisfies
        /// <paramref name="matchesAlias"/>; each session counts once. Used for the shared-mode
        /// KB_POOL_FULL details.
        /// </summary>
        /// <remarks>
        /// A pure read like <see cref="IsLeaseTokenReferenced"/>: it neither retires nor
        /// refreshes a session. Idle entries that <see cref="CleanupExpired"/> would retire
        /// are skipped, with the same stdio exemption.
        /// </remarks>
        internal int CountSessionsMatching(Func<string, bool> matchesAlias)
        {
            if (matchesAlias == null) throw new ArgumentNullException(nameof(matchesAlias));
            DateTime cutoff = DateTime.UtcNow - _idleTimeout;
            int count = 0;
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                bool stdio = string.Equals(pair.Key, "stdio", StringComparison.OrdinalIgnoreCase);
                if (!stdio && entry.LastSeenUtc < cutoff) continue;
                if ((!string.IsNullOrWhiteSpace(entry.Alias) && matchesAlias(entry.Alias!))
                    || (!string.IsNullOrWhiteSpace(entry.KbId) && matchesAlias(entry.KbId!)))
                    count++;
            }
            return count;
        }

        private bool TryGetEntry(string sessionId, out Entry entry)
        {
            entry = null!;
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            CleanupExpired();
            if (!_entries.TryGetValue(sessionId, out entry!)) return false;
            entry.LastSeenUtc = DateTime.UtcNow;
            return true;
        }

        private void CleanupExpired()
        {
            DateTime cutoff = DateTime.UtcNow - _idleTimeout;
            foreach (var pair in _entries)
            {
                // stdio is a process-scoped session and has no HTTP idle timeout.
                if (string.Equals(pair.Key, "stdio", StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value.LastSeenUtc < cutoff)
                    _entries.TryRemove(pair.Key, out _);
            }
        }

        private static void Validate(string sessionId, string alias)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session id is required.", nameof(sessionId));
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("KB alias is required.", nameof(alias));
        }
    }
}
