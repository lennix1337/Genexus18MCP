using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Which parts the property rollback compares with the state before the write. WebForm and Documentation keep their
    /// content as CDATA text, where layout ids and empty default entries are part of the text, so a byte comparison cannot
    /// tell their noise from a real change. Both are still restored with the rest of the object, but they are not compared:
    /// the receipt names them as not compared instead of reporting a false change. Events, Rules, Variables and the other
    /// parts keep the exact comparison. The move, SDT and variable rollbacks do not use this rule.
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
