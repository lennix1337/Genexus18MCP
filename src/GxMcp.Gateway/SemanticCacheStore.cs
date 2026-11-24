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
    /// Entries expire after <see cref="TtlMinutes"/> without access (lazy sweep on Set);
    /// the store evicts least-recently-accessed entries beyond <see cref="MaxEntries"/>
    /// entries or <see cref="MaxBytes"/> total bytes (a single giant read envelope
    /// must not crowd out hundreds of small ones).
    /// </summary>
    internal sealed class SemanticCacheStore
    {
        // Idle time after which an entry is considered stale. Swept lazily on every Set,
        // so no background timer is needed — a hot entry never expires mid-session.
        internal const int TtlMinutes = 30;
        private const int DefaultMaxEntries = 256;
        private const string MaxEntriesEnvVar = "GXMCP_SEMANTIC_CACHE_MAX";
        // Total serialized payload ceiling (UTF-16 bytes). 256 compact envelopes sit
        // far below this; only giant read envelopes (MBs) ever trip it.
        internal const long DefaultMaxBytes = 64L * 1024 * 1024;
        private const string MaxBytesEnvVar = "GXMCP_SEMANTIC_CACHE_MAX_BYTES";

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
        // Approximate serialized bytes per key (UTF-16 chars × 2), summed into
        // _totalBytes. Recomputed on Set (one extra serialization per distinct
        // read — Gets stay allocation-free); RemoveEntry/Clear keep it exact.
        private readonly ConcurrentDictionary<string, long> _entryBytes = new ConcurrentDictionary<string, long>();
        private long _totalBytes;
        private readonly long _maxBytes;
        private readonly Func<long> _clock;
        private readonly int _maxEntries;
        private readonly TimeSpan _ttl;
        private readonly long _ttlFloorMs;
        // Earliest tick at which any entry currently in the cache may expire.
        // Lets Set() skip scanning all entries when no entry can be expired yet.
        private long _nextExpiryCheckAt = long.MaxValue;
        internal Action<string>? OnSweepCandidateForTest { get; set; }
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
            : this(maxEntries, ttl, clock, ResolveMaxBytesFromEnv())
        {
        }

        // Test seam: byte ceiling override for eviction tests.
        internal SemanticCacheStore(int maxEntries, TimeSpan ttl, Func<long> clock, long maxBytes)
        {
            _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
            _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
            _ttl = ttl;
            _ttlFloorMs = ttl <= TimeSpan.Zero ? 0L : (long)Math.Floor(ttl.TotalMilliseconds);
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public int MaxEntries => _maxEntries;
        public long MaxBytes => _maxBytes;

        public bool TryGet(string key, out JObject value)
            => TryGet(key, out value, out _);

        public bool TryGet(string key, out JObject value, out long ageMilliseconds)
        {
            value = null!;
            ageMilliseconds = 0;
            if (!_entries.TryGetValue(key, out var found)) return false;

            if (IsExpired(key))
            {
                RemoveEntry(key);
                return false;
            }

            if (_createdAt.TryGetValue(key, out long createdAt))
                ageMilliseconds = Math.Max(0L, _clock() - createdAt);

            // Touch on hit: LRU-style recency drives both expiry and cap eviction.
            _lastAccess[key] = NextStamp();
            value = found;
            return true;
        }

        internal bool TryGet(StateScopedCacheKey key, out JObject value)
            => TryGet(key.ToString(), out value);

        public void Set(string key, JObject value)
        {
            long now = _clock();
            // Opportunistic maintenance: expire stale entries first so they don't
            // consume capacity that would otherwise force live entries out.
            SweepExpired(now);

            _entries[key] = value;
            _createdAt[key] = now;
            _lastAccess[key] = NextStamp();
            RecordExpiryCheckAt(AddSaturated(now, _ttlFloorMs));
            TrackEntryBytes(key, value);

            EvictBeyondCap();
        }

        internal void Set(StateScopedCacheKey key, JObject value)
            => Set(key.ToString(), value);

        public void Clear()
        {
            _entries.Clear();
            _lastAccess.Clear();
            _createdAt.Clear();
            _entryBytes.Clear();
            Interlocked.Exchange(ref _totalBytes, 0);
            Interlocked.Exchange(ref _nextExpiryCheckAt, long.MaxValue);
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

        /// <summary>
        /// Advances every scope the store currently holds and clears them all, without
        /// the single empty-scope generation that <see cref="Clear"/> would leave behind.
        ///
        /// <para>
        /// Issue #379. This exists because a change that cannot be attributed to one KB is
        /// still not safe to attribute to a narrower one, but the previous fallback -
        /// the empty scope - also threw away KBs that had nothing to do with the change,
        /// resetting their revisions and keeping their caches cold. Per-scope invalidation
        /// removes every entry (each key belongs to exactly one scope) while keeping every
        /// revision monotonically advanced, so a client comparing revisions still learns
        /// that something moved.
        /// </para>
        /// </summary>
        public long InvalidateEveryKnownScope(out int removed, out int scopes)
        {
            // A scope can hold entries without ever having been invalidated - the legacy
            // string-key Set writes a "scope|tool:args" key with no generation behind it -
            // so the scopes have to come from the entries as well as from the revisions.
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in _scopeRevisions.Keys) live.Add(scope);
            foreach (var key in _entries.Keys)
            {
                if (StateScopedCacheKey.TryParse(key, out var scopedKey))
                {
                    if (!string.IsNullOrWhiteSpace(scopedKey.KbId)) live.Add(scopedKey.KbId);
                    continue;
                }
                int separator = key.IndexOf('|');
                // A key with no scope prefix at all belongs to the empty scope.
                live.Add(separator >= 0 ? key.Substring(0, separator) : string.Empty);
            }

            removed = 0;
            scopes = 0;
            long revision = 0;

            foreach (var scope in live)
            {
                if (string.IsNullOrEmpty(scope)) continue;
                scopes++;
                revision = Math.Max(revision, AdvanceScopeGeneration(scope));
                removed += ClearScopeEntries(scope);
            }

            // The empty scope owns an entry that carries no scope prefix at all. Nothing
            // writes that shape today, but it is advanced unconditionally so its
            // generation stays monotonic: an empty-scope revision captured before this
            // call must not still match a later entry.
            revision = Math.Max(revision, AdvanceScopeGeneration(string.Empty));
            removed += ClearScopeEntries(string.Empty);
            scopes++;

            return revision;
        }

        /// <summary>
        /// Advances one scope's generation without clearing anything. Paired with
        /// <see cref="ClearScopeEntries"/> so the empty scope stops being a synonym for
        /// <see cref="Clear"/>: routing it through <see cref="InvalidateScope"/> wiped the
        /// revision map - including every other scope's generation - which is exactly the
        /// "unattributable change resets everyone" behaviour issue #379 removed.
        /// </summary>
        private long AdvanceScopeGeneration(string kbScope)
        {
            string scope = NormalizeScope(kbScope);
            return _scopeRevisions.AddOrUpdate(scope, 1L, (_, current) => checked(current + 1L));
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
                if ((key.StartsWith(scope + "|", StringComparison.Ordinal)
                    || (StateScopedCacheKey.TryParse(key, out var scopedKey)
                        && string.Equals(scopedKey.KbId, scope, StringComparison.Ordinal)))
                    && RemoveEntry(key)) removed++;
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
                StateScopedCacheKey.TryParse(key, out var scopedKey);
                if (!key.StartsWith(scope + "|", StringComparison.Ordinal)
                    && !string.Equals(scopedKey.KbId, scope, StringComparison.Ordinal)) continue;
                string itemKey = key.StartsWith(scope + "|", StringComparison.Ordinal)
                    ? key.Substring(scope.Length + 1)
                    : scopedKey.ItemKey;
                int argsStart = itemKey.IndexOf(':');
                if (argsStart < 0)
                {
                    if (RemoveEntry(key)) removed++;
                    continue;
                }
                string tool = itemKey.Substring(0, argsStart);
                string argsJson = itemKey.Substring(argsStart + 1);
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

        private static long AddSaturated(long a, long b)
            => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;

        private void RecordExpiryCheckAt(long candidate)
        {
            long current = Volatile.Read(ref _nextExpiryCheckAt);
            while (candidate < current)
            {
                long observed = Interlocked.CompareExchange(ref _nextExpiryCheckAt, candidate, current);
                if (observed == current) return;
                current = observed;
            }
        }

        private void SweepExpired(long now)
        {
            if (_entries.IsEmpty)
            {
                Interlocked.Exchange(ref _nextExpiryCheckAt, long.MaxValue);
                return;
            }
            if (_ttlFloorMs > 0 && now < Volatile.Read(ref _nextExpiryCheckAt))
            {
                return;
            }

            // Reset before scanning so any concurrent Set() after this point lowers
            // _nextExpiryCheckAt via RecordExpiryCheckAt without being clobbered.
            Interlocked.Exchange(ref _nextExpiryCheckAt, long.MaxValue);

            long earliestRemaining = long.MaxValue;
            foreach (var pair in _createdAt)
            {
                OnSweepCandidateForTest?.Invoke(pair.Key);
                if (TimeSpan.FromMilliseconds(now - pair.Value) >= _ttl)
                {
                    if (!RemoveExpiredEntry(pair.Key, pair.Value)
                        && _createdAt.TryGetValue(pair.Key, out long currentCreatedAt)
                        && currentCreatedAt < earliestRemaining)
                    {
                        earliestRemaining = currentCreatedAt;
                    }
                }
                else if (pair.Value < earliestRemaining)
                {
                    earliestRemaining = pair.Value;
                }
            }

            long nextCheck = earliestRemaining == long.MaxValue
                ? long.MaxValue
                : AddSaturated(earliestRemaining, _ttlFloorMs);
            RecordExpiryCheckAt(nextCheck);
        }

        private void EvictBeyondCap()
        {
            while (_entries.Count > _maxEntries
                || Interlocked.Read(ref _totalBytes) > _maxBytes)
            {
                // Least-recently-accessed victim via single O(N) pass with zero allocations.
                // Concurrent writers may race, so the loop re-checks both bounds.
                // A single entry bigger than the whole byte budget evicts everything
                // else first, then itself — the next Get re-fetches (miss, not poison).
                string? oldestKey = null;
                long oldestStamp = long.MaxValue;
                foreach (var pair in _lastAccess)
                {
                    if (pair.Value < oldestStamp && _entries.ContainsKey(pair.Key))
                    {
                        oldestStamp = pair.Value;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey == null || !RemoveEntry(oldestKey))
                {
                    break;
                }
            }
        }

        private static long MeasureEntryBytes(JObject value)
        {
            try
            {
                return (long)McpRouter.MeasureSerializedCharCount(value) * 2L;
            }
            catch { return 0; }
        }

        private void TrackEntryBytes(string key, JObject value)
        {
            long next = MeasureEntryBytes(value);
            long prev = 0;
            if (_entryBytes.TryGetValue(key, out var old)) prev = old;
            _entryBytes[key] = next;
            Interlocked.Add(ref _totalBytes, next - prev);
        }

        private bool RemoveExpiredEntry(string key, long expectedCreatedAt)
        {
            if (!_createdAt.TryRemove(new KeyValuePair<string, long>(key, expectedCreatedAt)))
                return false;

            _lastAccess.TryRemove(key, out _);
            if (_entryBytes.TryRemove(key, out var bytes))
                Interlocked.Add(ref _totalBytes, -bytes);
            _entries.TryRemove(key, out _);
            return true;
        }

        private bool RemoveEntry(string key)
        {
            _lastAccess.TryRemove(key, out _);
            _createdAt.TryRemove(key, out _);
            if (_entryBytes.TryRemove(key, out var bytes))
                Interlocked.Add(ref _totalBytes, -bytes);
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

        internal static long ResolveMaxBytesFromEnv()
        {
            var raw = Environment.GetEnvironmentVariable(MaxBytesEnvVar);
            if (!long.TryParse(raw, out long parsed) || parsed <= 0) return DefaultMaxBytes;
            return parsed;
        }
    }
}
