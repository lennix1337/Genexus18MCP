using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// What the property rollback compares with the state before the write. The text of Events, Rules, Source and the other
    /// text parts, and the text of Variables, are compared. The other parts are compared by their properties only: their content
    /// is not read by the snapshot. WebForm and Documentation are not compared at all: the properties their snapshot reads carry
    /// volatile entries (layout ids and empty defaults) that a byte comparison cannot tell from a change. Both are restored with
    /// the object and listed as not compared. The move, SDT and variable rollbacks keep their own comparison.
    /// </summary>
    internal static class PropertyVerificationScope
    {
        private static readonly string[] NotCompared = { "WebForm", "Documentation" };

        internal static bool IsCompared(string partName)
            => !NotCompared.Any(name => string.Equals(name, (partName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The distinct part names, in the order given, that the verification leaves out.</summary>
        internal static string[] NotComparedOf(IEnumerable<string> partNames)
            => (partNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name) && !IsCompared(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}
