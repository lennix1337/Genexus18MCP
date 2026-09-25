using System;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    internal sealed class VariableDeclaration
    {
        internal VariableDeclaration(string name, string typeName, int length, int decimals,
            bool isCollection, int dimensions, System.Collections.Generic.List<int> dimensionSizes)
        {
            Name = name;
            TypeName = typeName;
            Length = length;
            Decimals = decimals;
            IsCollection = isCollection;
            Dimensions = dimensions;
            DimensionSizes = dimensionSizes ?? new System.Collections.Generic.List<int>();
        }

        internal string Name { get; }
        internal string TypeName { get; }
        internal int Length { get; }
        internal int Decimals { get; }
        internal bool IsCollection { get; }
        internal int Dimensions { get; }
        internal System.Collections.Generic.List<int> DimensionSizes { get; }
    }

    internal static class VariableDeclarationParser
    {
        private static readonly Regex DeclarationPattern = new Regex(
            @"^\s*&?(\w+)\s*(?:\(\s*(\d+)(?:\s*,\s*(\d+))?\s*\))?\s*:\s*([\w\.\-:]+(?:\s*,\s*[\w\.\-]+)?)(?:\s*\(\s*(\d+)(?:\s*[,.]\s*(\d+))?\s*\))?(?:\s+(Collection))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool TryParse(string line, out VariableDeclaration declaration)
        {
            declaration = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            Match match = DeclarationPattern.Match(line);
            if (!match.Success) return false;

            var sizes = new System.Collections.Generic.List<int>();
            if (match.Groups[2].Success)
            {
                int firstSize;
                if (!int.TryParse(match.Groups[2].Value, out firstSize) || firstSize <= 0)
                    return false;
                sizes.Add(firstSize);
                if (match.Groups[3].Success)
                {
                    int secondSize;
                    if (!int.TryParse(match.Groups[3].Value, out secondSize) || secondSize <= 0)
                        return false;
                    sizes.Add(secondSize);
                }
            }

            int length = 0;
            if (match.Groups[5].Success && (!int.TryParse(match.Groups[5].Value, out length) || length < 0))
                return false;
            int decimals = 0;
            if (match.Groups[6].Success && (!int.TryParse(match.Groups[6].Value, out decimals) || decimals < 0))
                return false;

            int dimensions = sizes.Count == 0 ? 0 : sizes.Count;
            declaration = new VariableDeclaration(
                match.Groups[1].Value,
                match.Groups[4].Value.Trim(),
                length,
                decimals,
                match.Groups[7].Success,
                dimensions,
                sizes);
            return true;
        }

        internal static bool TrySplitModuleQualifiedTypeName(string typeName, out string objectName, out string moduleName)
        {
            objectName = null;
            moduleName = null;
            if (string.IsNullOrWhiteSpace(typeName)) return false;

            int comma = typeName.IndexOf(',');
            if (comma <= 0 || comma != typeName.LastIndexOf(',')) return false;

            objectName = typeName.Substring(0, comma).Trim();
            moduleName = typeName.Substring(comma + 1).Trim();
            return objectName.Length > 0 && moduleName.Length > 0;
        }
    }
}
