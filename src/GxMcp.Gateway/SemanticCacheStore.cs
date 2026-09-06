using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Bounded, TTL-aware store backing the gateway's semantic cache.
    /// The gateway process is long-lived (stdio EOF keeps it alive via Task.Delay(-1)),
    /// so an unbounded ConcurrentDictionary grows forever across read-only sessions.
    /// Entries expire after <see cref="TtlMinutes"/> without access (lazy sweep on Set)
    /// and the store evicts least-recently-accessed entries beyond <see cref="MaxEntries"/>.
    /// </summary>
    internal sealed class SemanticCacheStore
    {
        // Idle time after which an entry is considered stale. Swept lazily on every Set,
        // so no background timer is needed — a hot entry never expires mid-session.
        internal const int TtlMinutes = 30;
        private const int DefaultMaxEntries = 256;
        private const string MaxEntriesEnvVar = "GXMCP_SEMANTIC_CACHE_MAX";

        private readonly ConcurrentDictionary<string, JObject> _entries = new ConcurrentDictionary<string, JObject>();
        // Last-access timestamp per key, driven by NextStamp(). Kept separate so
        // the cached envelope itself stays a plain JObject.
        private readonly ConcurrentDictionary<string, long> _lastAccess = new ConcurrentDictionary<string, long>();
        // Absolute creation timestamp per key. Recency is deliberately separate:
        // a hot entry may move in the LRU order, but a hit must never extend its
        // freshness window indefinitely.
        private readonly ConcurrentDictionary<string, long> _createdAt = new ConcurrentDictionary<string, long>();
        // A mutation advances only the affected KB generation. Reads that started
        // against an older generation cannot repopulate the cache after the write.
        private readonly ConcurrentDictionary<string, long> _scopeRevisions = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly Func<long> _clock;
        private readonly int _maxEntries;
        private readonly TimeSpan _ttl;
        // Logical clock: TickCount64 alone has 1ms resolution, so several accesses
        // can land on the same tick and leave LRU order ambiguous. Stamps only
        // move forward — a bumped stamp is still fresh enough for TTL checks.
        private long _lastStamp;

        public SemanticCacheStore()
            : this(ResolveMaxEntriesFromEnv(), TimeSpan.FromMinutes(TtlMinutes))
        {
        }

        // Test seam: lets unit tests drive eviction/TTL deterministically.
        internal SemanticCacheStore(int maxEntries, TimeSpan ttl)
            : this(maxEntries, ttl, () => Environment.TickCount64)
        {
        }

        // Test seam: inject a monotonic millisecond clock so absolute-expiry and
        // generation races can be asserted without sleeping.
        internal SemanticCacheStore(int maxEntries, TimeSpan ttl, Func<long> clock)
        {
            _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
            _ttl = ttl;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public int MaxEntries => _maxEntries;

        public bool TryGet(string key, out JObject value)
        {
            value = null!;
            if (!_entries.TryGetValue(key, out var found)) return false;

            if (IsExpired(key))
            {
                RemoveEntry(key);
                return false;
            }

            // Touch on hit: LRU-style recency drives both expiry and cap eviction.
            _lastAccess[key] = NextStamp();
            value = found;
            return true;
        }

        public void Set(string key, JObject value)
        {
            // Opportunistic maintenance: expire stale entries first so they don't
            // consume capacity that would otherwise force live entries out.
            SweepExpired();

            _entries[key] = value;
            long now = _clock();
            _createdAt[key] = now;
            _lastAccess[key] = NextStamp();

            EvictBeyondCap();
        }

        public void Clear()
        {
            _entries.Clear();
            _lastAccess.Clear();
            _createdAt.Clear();
            _scopeRevisions.Clear();
        }

        /// <summary>Returns the current generation for one KB scope.</summary>
        public long GetRevision(string kbScope)
        {
            string scope = NormalizeScope(kbScope);
            return _scopeRevisions.TryGetValue(scope, out var revision) ? revision : 0L;
        }

        /// <summary>
        /// Invalidates all reads for one KB and advances its generation. The
        /// returned revision is the value callers should capture for a new read.
        /// </summary>
        public long InvalidateScope(string kbScope)
            => InvalidateScope(kbScope, out _);

        public long InvalidateScope(string kbScope, out int removed)
        {
            string scope = NormalizeScope(kbScope);
            long revision = _scopeRevisions.AddOrUpdate(scope, 1L, (_, current) => checked(current + 1L));
            if (string.IsNullOrWhiteSpace(scope))
            {
                removed = _entries.Count;
                Clear();
                // Clear() removes the revision map, so restore the incremented
                // empty-scope generation for callers that captured it.
                _scopeRevisions[scope] = revision;
            }
            else
            {
                removed = ClearScopeEntries(scope);
            }
            return revision;
        }

        /// <summary>Clear only entries belonging to one KB scope.</summary>
        public int ClearScope(string kbScope)
        {
            string scope = NormalizeScope(kbScope);
            if (string.IsNullOrWhiteSpace(scope)) return 0;
            _scopeRevisions.AddOrUpdate(scope, 1L, (_, current) => checked(current + 1L));
            return ClearScopeEntries(scope);
        }

        private int ClearScopeEntries(string scope)
        {
            int removed = 0;
            foreach (var key in _entries.Keys.ToArray())
            {
                if (key.StartsWith(scope + "|", StringComparison.Ordinal) && RemoveEntry(key)) removed++;
            }
            return removed;
        }

        /// <summary>
        /// Invalidate derived reads within the affected KB: collections and dependency
        /// analyses can change even when their arguments don't name the mutated object.
        /// Only direct source reads of unrelated objects survive until dependency tags
        /// can prove that broader cached results remain valid.
        /// </summary>
        public int RemoveByTarget(string kbScope, string targetObject)
        {
            if (string.IsNullOrWhiteSpace(targetObject)) return 0;
            string scope = NormalizeScope(kbScope);
            if (string.IsNullOrWhiteSpace(scope)) return 0;

            // Word-boundary-ish match on the quoted JSON value so "Cliente" does not
            // match "ClienteId" / "MeuCliente" inside args JSON.
            string needle = Newtonsoft.Json.JsonConvert.SerializeObject(targetObject);
            int removed = 0;
            foreach (var key in _entries.Keys.ToArray())
            {
                if (!key.StartsWith(scope + "|", StringComparison.Ordinal)) continue;
                int argsStart = key.IndexOf(':', scope.Length + 2);
                if (argsStart < 0)
                {
                    if (RemoveEntry(key)) removed++;
                    continue;
                }
                string tool = key.Substring(scope.Length + 1, argsStart - scope.Length - 1);
                string argsJson = key.Substring(argsStart + 1);
                if ((!string.Equals(tool, "genexus_read", StringComparison.OrdinalIgnoreCase)
                     || argsJson.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    && RemoveEntry(key))
                {
                    removed++;
                }
            }
            return removed;
        }

        private bool IsExpired(string key)
        {
            if (!_createdAt.TryGetValue(key, out var createdAt)) return true;
            return TimeSpan.FromMilliseconds(_clock() - createdAt) >= _ttl;
        }

        /// <summary>
        /// Monotonic access stamp: real TickCount64 when it advanced, otherwise the
        /// previous stamp + 1, so every access gets a strictly greater value even
        /// within the same millisecond (LRU eviction needs a total order).
        /// </summary>
        private long NextStamp()
        {
            long ticks = _clock();
            long prev = Interlocked.Read(ref _lastStamp);
            while (true)
            {
                long next = ticks > prev ? ticks : prev + 1;
                long seen = Interlocked.CompareExchange(ref _lastStamp, next, prev);
                if (seen == prev) return next;
                prev = seen;
            }
        }

        private void SweepExpired()
        {
            foreach (var key in _lastAccess.Keys.ToArray())
            {
                if (_entries.ContainsKey(key) && IsExpired(key))
                {
                    RemoveEntry(key);
                }
            }
        }

        private void EvictBeyondCap()
        {
            while (_entries.Count > _maxEntries)
            {
                // Least-recently-accessed victim. ToArray snapshot: concurrent writers
                // may race, but the loop re-checks Count so we never over-evict.
                string? oldestKey = _lastAccess.ToArray()
                    .OrderBy(pair => pair.Value)
                    .Select(pair => pair.Key)
                    .FirstOrDefault(candidate => _entries.ContainsKey(candidate));

                if (oldestKey == null || !RemoveEntry(oldestKey))
                {
                    break;
                }
            }
        }

        private bool RemoveEntry(string key)
        {
            _lastAccess.TryRemove(key, out _);
            _createdAt.TryRemove(key, out _);
            return _entries.TryRemove(key, out _);
        }

        private static string NormalizeScope(string? scope)
            => (scope ?? string.Empty).Trim().ToLowerInvariant();

        private static int ResolveMaxEntriesFromEnv()
        {
            var raw = Environment.GetEnvironmentVariable(MaxEntriesEnvVar);
            if (!int.TryParse(raw, out int parsed) || parsed <= 0) return DefaultMaxEntries;
            return parsed;
        }
    }
}
