using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// What a later property write reports about an earlier write whose rollback was not confirmed. The state before that
    /// write is kept per object, and it is only reported: no write is refused because of it. A write on an object with
    /// residue reports its preserved parts against that state, and it names the parts that still differ from it.
    /// </summary>
    internal sealed class PropertyResidueLedger<TSnapshot> where TSnapshot : class
    {
        private readonly Dictionary<string, TSnapshot> _original = new Dictionary<string, TSnapshot>(StringComparer.Ordinal);

        internal int Count => _original.Count;

        // The first state that was left unconfirmed is the one reported against: a later unconfirmed write does not replace it.
        internal void Remember(string key, TSnapshot original)
        {
            if (!_original.ContainsKey(key)) _original[key] = original;
        }

        internal bool TryGetOriginal(string key, out TSnapshot original) => _original.TryGetValue(key, out original);

        internal void Forget(string key) => _original.Remove(key);
    }

    internal static class PropertyResidue
    {
        /// <summary>The names of the original parts that the residue does not touch, in the order given.</summary>
        internal static string[] PreservedNames(IEnumerable<string> originalNames, IEnumerable<string> residueNames)
        {
            var residue = new HashSet<string>(residueNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return (originalNames ?? Enumerable.Empty<string>()).Where(name => !residue.Contains(name)).ToArray();
        }

        /// <summary>The warning that names the parts still different from the state before an earlier unconfirmed write.</summary>
        internal static string Warning(IEnumerable<string> residueNames)
            => "The object still differs from the state before an earlier property write whose rollback was not confirmed, in: "
               + string.Join(", ", residueNames) + ". preservedParts lists the parts that still match that state.";
    }
}
