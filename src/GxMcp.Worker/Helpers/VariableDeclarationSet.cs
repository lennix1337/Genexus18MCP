using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    internal static class VariableDeclarationSet
    {
        internal static List<VariableDeclaration> ParseAll(string text)
        {
            var declarations = new List<VariableDeclaration>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (text ?? string.Empty).Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (!VariableDeclarationParser.TryParse(line, out var declaration))
                    throw new ArgumentException("Invalid Variables declaration: " + line.Trim());
                if (!names.Add(declaration.Name))
                    throw new ArgumentException("Duplicate Variables declaration: " + declaration.Name);
                if (declaration.Decimals > declaration.Length)
                    throw new ArgumentException("Variables decimals cannot exceed length: " + declaration.Name);
                if (declaration.Dimensions > 0 && declaration.IsCollection)
                    throw new ArgumentException("A variable cannot be both a collection and a fixed-size array: " + declaration.Name);
                declarations.Add(declaration);
            }
            return declarations;
        }

        internal static bool AreEquivalent(VariableDeclaration a, VariableDeclaration b)
        {
            if (a == null || b == null) return false;
            bool sameType = string.Equals(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase);
            if (VariableInjector.TryParseDbType(a.TypeName, out var aType)
                && VariableInjector.TryParseDbType(b.TypeName, out var bType))
                sameType = aType == bType;
            return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                && sameType && a.Length == b.Length && a.Decimals == b.Decimals
                && a.IsCollection == b.IsCollection && a.Dimensions == b.Dimensions
                && a.DimensionSizes.SequenceEqual(b.DimensionSizes);
        }
    }
}
