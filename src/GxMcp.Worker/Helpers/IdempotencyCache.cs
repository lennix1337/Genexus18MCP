using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// v2.8.0 — per-worker cache of tool responses keyed by `clientRequestId`.
    /// When a mutating tool call carries a clientRequestId and the worker has
    /// already served that id, the cached response is returned instead of
    /// re-executing. This makes retries safe across socket drops, gateway
    /// hangs, or LLM-side timeouts that re-issue the same call.
    ///
    /// Scope: in-memory, per-worker process. Restarting the worker drops the
    /// cache. That's acceptable — cross-restart retry is rare; the silent
    /// double-apply this prevents is the common case (delete-timeout + retry,
    /// edit-timeout + retry, build-trigger + retry).
    /// </summary>
    public static class IdempotencyCache
    {
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DefaultInflightWaitBudget = TimeSpan.FromMinutes(5);
        private static TimeSpan _inflightWaitBudget = DefaultInflightWaitBudget;
        private const int MaxEntries = 500;

        private static readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        // v2.8.0 (#37) — in-flight tracking. When a second call with the same
        // clientRequestId arrives BEFORE the first has stored its response,
        // the second blocks on this signal and then re-probes the cache,
        // ensuring it returns the same response as the first instead of
        // re-executing. Eliminates the brief race window where a fast LLM
        // retry could double-apply during the original call's still-in-flight
        // window.
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _inflight =
            new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);

        private sealed class Entry
        {
            public string Response { get; set; }
            public DateTime StoredAtUtc { get; set; }
        }

        /// <summary>
        /// Returns the cached response if `clientRequestId` was previously
        /// served within the TTL, wrapped with `_meta.replayed=true` so the
        /// caller knows it's a replay. Returns null on cache miss / expiry.
        ///
        /// If another caller is currently executing the same id (in-flight),
        /// this BLOCKS up to <see cref="InflightWaitBudget"/> waiting for that
        /// caller to complete, then re-probes the cache. This prevents the
        /// "fast retry within the original call's window" double-apply race.
        /// </summary>
        public static string TryServe(string clientRequestId)
        {
            if (string.IsNullOrWhiteSpace(clientRequestId)) return null;
            if (TryHit(clientRequestId, out var hit)) return hit;

            // In-flight wait: another caller holds the signal. Block until
            // they release it (via Store) or the budget elapses.
            if (_inflight.TryGetValue(clientRequestId, out var signal))
            {
                if (!signal.Task.Wait(_inflightWaitBudget))
                    return "{\"status\":\"error\",\"code\":\"idempotency_in_progress\",\"message\":\"An operation with this clientRequestId is still in progress; retry with the same payload later.\"}";
                if (TryHit(clientRequestId, out hit)) return hit;
            }
            return null;
        }

        private static bool TryHit(string clientRequestId, out string replay)
        {
            replay = null;
            if (!_entries.TryGetValue(clientRequestId, out var entry)) return false;
            if (DateTime.UtcNow - entry.StoredAtUtc > DefaultTtl)
            {
                _entries.TryRemove(clientRequestId, out _);
                return false;
            }
            replay = WrapAsReplay(entry.Response, entry.StoredAtUtc);
            return true;
        }

        /// <summary>
        /// Marks an id as in-flight. Subsequent <see cref="TryServe"/> calls
        /// with the same id will block until <see cref="Store"/> is called.
        /// Called by the dispatcher after a cache miss, before executing.
        /// </summary>
        public static void BeginInflight(string clientRequestId)
        {
            if (string.IsNullOrWhiteSpace(clientRequestId)) return;
            _inflight.GetOrAdd(clientRequestId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        /// <summary>
        /// Releases the in-flight signal for an id WITHOUT storing a response.
        /// Used when the dispatcher decides not to cache (e.g. excluded method
        /// detected after BeginInflight was already called, or a thrown
        /// exception). Safe to call on a never-marked id.
        /// </summary>
        public static void AbortInflight(string clientRequestId)
        {
            if (string.IsNullOrWhiteSpace(clientRequestId)) return;
            if (_inflight.TryRemove(clientRequestId, out var signal))
            {
                signal.TrySetResult(false);
            }
        }

        /// <summary>
        /// Store the response keyed by id. No-op if id is empty.
        /// </summary>
        public static void Store(string clientRequestId, string response)
        {
            if (string.IsNullOrWhiteSpace(clientRequestId)) return;
            if (string.IsNullOrEmpty(response)) return;

            // Bound the cache size. LRU-ish: evict the oldest 10% when full.
            // Avoids unbounded growth on long-running workers without paying
            // strict-LRU bookkeeping cost on every Store.
            if (_entries.Count >= MaxEntries)
            {
                EvictOldest();
            }

            _entries[clientRequestId] = new Entry
            {
                Response = response,
                StoredAtUtc = DateTime.UtcNow
            };

            // Release any in-flight waiters now that the result is cached.
            if (_inflight.TryRemove(clientRequestId, out var signal))
            {
                signal.TrySetResult(true);
            }
        }

        /// <summary>For tests: empty the cache + clear in-flight signals.</summary>
        public static void Clear()
        {
            _entries.Clear();
            foreach (var kv in _inflight)
            {
                if (_inflight.TryRemove(kv.Key, out var sig))
                {
                    sig.TrySetResult(false);
                }
            }
        }

        internal static void SetInflightWaitBudgetForTests(TimeSpan budget)
            => _inflightWaitBudget = budget;

        internal static void ResetInflightWaitBudgetForTests()
            => _inflightWaitBudget = DefaultInflightWaitBudget;

        /// <summary>For tests: current entry count.</summary>
        public static int Count => _entries.Count;

        private static void EvictOldest()
        {
            var victims = _entries
                .OrderBy(kv => kv.Value.StoredAtUtc)
                .Take(System.Math.Max(1, MaxEntries / 10))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in victims) _entries.TryRemove(k, out _);
        }

        private static string WrapAsReplay(string original, DateTime storedAtUtc)
        {
            // Tag the response so the caller can detect a replay without
            // changing the canonical envelope status/code semantics.
            try
            {
                var obj = JObject.Parse(original);
                var meta = obj["_meta"] as JObject ?? new JObject();
                meta["replayed"] = true;
                meta["replayedFromUtc"] = storedAtUtc.ToString("o");
                obj["_meta"] = meta;
                return obj.ToString();
            }
            catch
            {
                // Non-JSON payload — return as-is. Best-effort.
                return original;
            }
        }
    }
}
