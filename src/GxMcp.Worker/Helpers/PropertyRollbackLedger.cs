using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The objects whose last property rollback was not confirmed. A later write to such an object is refused until a fresh
    /// comparison with the state kept here (the state before the failed write) finds no difference. A second failed attempt
    /// keeps the first state, so its residue never becomes the baseline. The ledger lives in memory: a Worker restart
    /// forgets it.
    /// </summary>
    internal sealed class PropertyRollbackLedger<TSnapshot> where TSnapshot : class
    {
        internal sealed class Entry
        {
            internal Entry(TSnapshot original, IReadOnlyList<string> changedParts)
            {
                Original = original;
                ChangedParts = changedParts;
            }

            internal TSnapshot Original { get; }
            internal IReadOnlyList<string> ChangedParts { get; }
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        /// <summary>How many objects are held. A write checks the ledger only when at least one is held.</summary>
        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        internal bool TryGetEntry(string key, out Entry entry)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(key ?? string.Empty, out entry);
            }
        }

        /// <summary>Records an unconfirmed rollback. An entry already held keeps its original state and its parts.</summary>
        internal void Remember(string key, TSnapshot original, IReadOnlyList<string> changedParts)
        {
            lock (_gate)
            {
                string name = key ?? string.Empty;
                if (!_entries.ContainsKey(name)) _entries[name] = new Entry(original, changedParts ?? new string[0]);
            }
        }

        internal void Forget(string key)
        {
            lock (_gate)
            {
                _entries.Remove(key ?? string.Empty);
            }
        }
    }

    /// <summary>What a write does when its object may still have an unconfirmed rollback.</summary>
    internal enum PropertyWriteGateOutcome
    {
        /// <summary>The object has no unconfirmed rollback: the write goes on.</summary>
        Open,

        /// <summary>The object had one and is back at the state kept for it: the entry is cleared and the write goes on.</summary>
        Reconciled,

        /// <summary>The object differs from the state kept for it: the write is refused.</summary>
        Locked
    }

    internal static class PropertyWriteGate
    {
        /// <summary>
        /// Open when the object has no unconfirmed rollback. Reconciled when it has one and the fresh comparison finds the
        /// object at the state kept for it. Locked when it differs.
        /// </summary>
        internal static PropertyWriteGateOutcome Decide(bool hasEntry, bool matchesOriginal)
        {
            if (!hasEntry) return PropertyWriteGateOutcome.Open;
            return matchesOriginal ? PropertyWriteGateOutcome.Reconciled : PropertyWriteGateOutcome.Locked;
        }
    }
}
