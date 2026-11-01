using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The variables of an object as the text that the property write and its rollback compare. The declarations of the variables are not
    /// part of the properties of any part, so a save that deletes a variable, or its description, changes no part that the comparison sees.
    /// The text has one line per variable, in name order: the variable, its declaration and its description. Standard variables are left
    /// out: GeneXus defines them, so a save may add or drop them without the write having changed them. Auto-defined variables stay in: the
    /// source relies on them, so their removal is a change the write made. It is null when the variables cannot be read, and the comparison
    /// then leaves them out instead of counting them as changed.
    /// </summary>
    internal static class VariablesFingerprint
    {
        /// <summary>
        /// The text of an object's variables. Null when the reader fails; empty when the object has no variables. The reader is the SDK
        /// unless a test passes its own.
        /// </summary>
        internal static string Capture(KBObject obj, Func<KBObject, IEnumerable<VariableFact>> read = null)
        {
            try
            {
                IEnumerable<VariableFact> variables = (read ?? new Func<KBObject, IEnumerable<VariableFact>>(ReadVariables))(obj);
                return Of(variables);
            }
            catch (Exception ex)
            {
                Logger.Debug("[PROPERTY] variables could not be read: " + ex.Message);
                return null;
            }
        }

        /// <summary>The text of the given variables: one line each, in name order, the description on one line. Standard variables are not written.</summary>
        internal static string Of(IEnumerable<VariableFact> variables)
        {
            var text = new System.Text.StringBuilder();
            foreach (VariableFact variable in (variables ?? Enumerable.Empty<VariableFact>())
                .Where(v => !v.Standard)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.Append('&').Append(variable.Name).Append(variable.Declaration)
                    .Append(" // ").Append(OneLine(variable.Description)).Append('\n');
            }
            return text.ToString();
        }

        // The declaration after a variable's name, from the stored type only. No model query is made, so a lookup that fails on one read and
        // not on the other cannot make the two texts differ. Attribute, domain and SDT bindings and array dimensions are not part of it.
        internal static string DeclarationOf(string typeName, int length, int decimals, bool collection)
            => " : " + typeName + "(" + length + (decimals > 0 ? "," + decimals : "") + ")" + (collection ? " Collection" : "");

        // A description on one line, with its line endings and trailing spaces normalized: the same text is the same description.
        private static string OneLine(string text) =>
            (text ?? string.Empty).Replace("\r\n", "\n").TrimEnd().Replace('\r', '\n').Replace('\n', ' ');

        // The variables of the object, read from its variables part. A description the SDK does not expose throws here, and then the
        // whole text is unknown: a missing description is never taken for a deleted one.
        private static IEnumerable<VariableFact> ReadVariables(KBObject obj)
        {
            var part = obj.Parts.Get<VariablesPart>();
            if (part == null) return new VariableFact[0];
            return part.Variables
                .Cast<global::Artech.Genexus.Common.Variable>()
                .Select(v => new VariableFact(
                    v.Name, DeclarationOf(v.Type.ToString(), v.Length, v.Decimals, v.IsCollection), v.Description, v.IsStandard))
                .ToList();
        }
    }

    /// <summary>One variable as the variables text reads it: its name, the declaration that follows the name, its description, and whether
    /// GeneXus defines it as a standard variable (a standard variable is left out of the text).</summary>
    internal sealed class VariableFact
    {
        internal VariableFact(string name, string declaration, string description, bool standard = false)
        {
            Name = name;
            Declaration = declaration;
            Description = description;
            Standard = standard;
        }

        internal string Name { get; }

        internal string Declaration { get; }

        internal string Description { get; }

        internal bool Standard { get; }
    }
}
