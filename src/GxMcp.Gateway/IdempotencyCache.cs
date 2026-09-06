using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class IdempotencyCache
    {
        private readonly TimeSpan _gateAcquisitionTimeout;

        private readonly TimeSpan _ttl;
        private readonly int _capacity;
        private readonly MutationOperationJournal? _journal;
        private readonly ConcurrentDictionary<string, KbBucket> _buckets = new ConcurrentDictionary<string, KbBucket>();
        private readonly object _gateLock = new object();
        private readonly Dictionary<(string, string, string), GateEntry> _gates =
            new Dictionary<(string, string, string), GateEntry>();

        public IdempotencyCache(int ttlMinutes, int capacity)
            : this(ttlMinutes, capacity, TimeSpan.FromSeconds(30), null) { }

        internal IdempotencyCache(int ttlMinutes, int capacity, TimeSpan gateAcquisitionTimeout)
            : this(ttlMinutes, capacity, gateAcquisitionTimeout, null) { }

        internal IdempotencyCache(int ttlMinutes, int capacity, TimeSpan gateAcquisitionTimeout, string? journalPath)
        {
            _gateAcquisitionTimeout = gateAcquisitionTimeout;
            _ttl = TimeSpan.FromMinutes(ttlMinutes);
            _capacity = capacity;
            _journal = string.IsNullOrWhiteSpace(journalPath) ? null : new MutationOperationJournal(journalPath);
        }

        // Plan 028: test-only visibility into gate accumulation (InternalsVisibleTo
        // GxMcp.Gateway.Tests is already configured in the csproj).
        internal int GateCount { get { lock (_gateLock) return _gates.Count; } }

        internal JObject InspectOperation(string kbPath, string tool, string key)
        {
            if (_journal == null)
            {
                return new JObject
                {
                    ["journalHealthy"] = false,
                    ["code"] = "operation_journal_disabled",
                    ["message"] = "This Gateway instance has no durable operation journal configured."
                };
            }
            return _journal.Inspect(kbPath, tool, key);
        }

        internal JObject ReconcileOperation(
            string kbPath,
            string tool,
            string key,
            string verification,
            MutationOperationEvidence? observedEvidence = null)
        {
            if (_journal == null)
            {
                return new JObject
                {
                    ["status"] = "Blocked",
                    ["code"] = "operation_journal_disabled",
                    ["message"] = "This Gateway instance has no durable operation journal configured; no state changed."
                };
            }
            return _journal.Reconcile(kbPath, tool, key, verification, observedEvidence);
        }

        public bool TryGet(string kbPath, string tool, string key,
                           string payloadHash, out JObject? cached)
        {
            cached = null;
            var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
            return bucket.TryGet(tool, key, payloadHash, out cached);
        }

        public void Put(string kbPath, string tool, string key,
                        string payloadHash, JObject result)
        {
            var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
            bucket.Put(tool, key, payloadHash, result);
        }

        public async Task<JObject> GetOrCompute(
            string kbPath, string tool, string key, string payloadHash,
            Func<Task<JObject>> factory,
            MutationOperationEvidence? evidence = null)
        {
            if (TryGet(kbPath, tool, key, payloadHash, out var cached))
                return cached!;

            var gateKey = (kbPath, tool, key);
            GateEntry gate;
            lock (_gateLock)
            {
                if (!_gates.TryGetValue(gateKey, out gate!))
                    _gates.Add(gateKey, gate = new GateEntry());
                gate.Users++;
            }

            bool gateAcquired = false;
            try
            {
                gateAcquired = await gate.Semaphore.WaitAsync(_gateAcquisitionTimeout).ConfigureAwait(false);
                if (!gateAcquired)
                    throw new UsageException("idempotency_in_progress",
                        "An operation with this idempotency key is still in progress. " +
                        "This request was not executed; retry with the same key and payload to retrieve its result.");

                if (TryGet(kbPath, tool, key, payloadHash, out cached))
                    return cached!;
                if (_journal != null)
                {
                    switch (_journal.Begin(kbPath, tool, key, payloadHash, evidence))
                    {
                        case MutationOperationJournal.BeginResult.Conflict:
                            throw new IdempotencyConflictException(
                                $"idempotency key '{key}' reused with different payload");
                        case MutationOperationJournal.BeginResult.Completed:
                        case MutationOperationJournal.BeginResult.UnknownAfterRestart:
                            throw new UsageException(
                                "operation_unknown",
                                "A previous process may have committed this mutation, but its response is unavailable. " +
                                "Inspect the affected target before retrying with a new authorization.");
                        case MutationOperationJournal.BeginResult.JournalUnavailable:
                            throw new UsageException(
                                "operation_journal_unavailable",
                                "The durable mutation journal is unavailable or corrupt; this write was not executed. " +
                                "Repair the Gateway state journal and inspect the target before retrying.");
                    }
                }
                try
                {
                    var result = await factory().ConfigureAwait(false);
                    Put(kbPath, tool, key, payloadHash, result);
                    _journal?.Complete(kbPath, tool, key, payloadHash);
                    return result;
                }
                catch (ErrorNotCacheable ex)
                {
                    _journal?.Fail(kbPath, tool, key, payloadHash);
                    return ex.Result;
                }
            }
            finally
            {
                if (gateAcquired) gate.Semaphore.Release();
                // Retain one shared gate while any owner or waiter can still use it.
                // Registration and last-user eviction must be atomic with each other.
                lock (_gateLock)
                {
                    if (--gate.Users == 0)
                    {
                        _gates.Remove(gateKey);
                        gate.Semaphore.Dispose();
                    }
                }
            }
        }

        private sealed class GateEntry
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            public int Users;
        }

        // PERFORMANCE (G-M4): KbBucket now shards its state across N independent LRU slots.
        // Each shard has its own lock, dictionary, linked-list, and capacity slice. Strict
        // global LRU becomes per-shard LRU, which is acceptable for an idempotency cache
        // (semantics: "don't re-run the same key twice in the TTL window"). Hot-key contention
        // drops by 1/N because two threads hitting different shards never block each other.
        private sealed class KbBucket
        {
            private const int ShardCount = 16; // power of two for cheap hash masking
            private readonly Shard[] _shards;
            private readonly TimeSpan _ttl;

            public KbBucket(int capacity, TimeSpan ttl)
            {
                _ttl = ttl;
                _shards = new Shard[ShardCount];
                int perShard = Math.Max(1, (capacity + ShardCount - 1) / ShardCount);
                for (int i = 0; i < ShardCount; i++) _shards[i] = new Shard(perShard, ttl);
            }

            private Shard PickShard(string tool, string key)
            {
                // Stable, low-overhead hash; deterministic across processes is not required.
                int h = unchecked((tool?.GetHashCode() ?? 0) * 397) ^ (key?.GetHashCode() ?? 0);
                return _shards[(h & int.MaxValue) % ShardCount];
            }

            public bool TryGet(string tool, string key, string payloadHash, out JObject? cached)
                => PickShard(tool, key).TryGet(tool, key, payloadHash, out cached);

            public void Put(string tool, string key, string payloadHash, JObject result)
                => PickShard(tool, key).Put(tool, key, payloadHash, result);

            private sealed class Shard
            {
                private readonly int _capacity;
                private readonly TimeSpan _ttl;
                private readonly LinkedList<(string Tool, string Key)> _lru = new LinkedList<(string Tool, string Key)>();
                private readonly Dictionary<(string, string), Entry> _map = new Dictionary<(string, string), Entry>();
                private readonly object _lock = new object();

                public Shard(int capacity, TimeSpan ttl) { _capacity = capacity; _ttl = ttl; }

                public bool TryGet(string tool, string key, string payloadHash, out JObject? cached)
                {
                    cached = null;
                    lock (_lock)
                    {
                        if (!_map.TryGetValue((tool, key), out var entry)) return false;
                        if (DateTime.UtcNow - entry.LastAccessedAt > _ttl)
                        {
                            _map.Remove((tool, key));
                            _lru.Remove(entry.Node);
                            return false;
                        }
                        if (entry.PayloadHash != payloadHash)
                            throw new IdempotencyConflictException(
                                $"idempotency key '{key}' reused with different payload");
                        entry.LastAccessedAt = DateTime.UtcNow;
                        _lru.Remove(entry.Node);
                        _lru.AddFirst(entry.Node);
                        cached = entry.Result;
                        return true;
                    }
                }

                public void Put(string tool, string key, string payloadHash, JObject result)
                {
                    lock (_lock)
                    {
                        if (_map.TryGetValue((tool, key), out var existing))
                        {
                            _lru.Remove(existing.Node);
                            _map.Remove((tool, key));
                        }
                        while (_map.Count >= _capacity)
                        {
                            var oldest = _lru.Last!;
                            _lru.RemoveLast();
                            _map.Remove(oldest.Value);
                        }
                        var node = new LinkedListNode<(string, string)>((tool, key));
                        _lru.AddFirst(node);
                        _map[(tool, key)] = new Entry
                        {
                            PayloadHash = payloadHash,
                            Result = result,
                            LastAccessedAt = DateTime.UtcNow,
                            Node = node
                        };
                    }
                }
            }

            private sealed class Entry
            {
                public string PayloadHash = "";
                public JObject Result = new JObject();
                public DateTime LastAccessedAt;
                public LinkedListNode<(string, string)> Node = null!;
            }
        }
    }
}
