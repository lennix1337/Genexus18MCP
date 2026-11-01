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
    /// The text has one line per variable, in name order: the variable, its declaration as the variable editor writes it, and its
    /// description. It is null when the variables cannot be read, and the comparison then leaves them out instead of counting them as changed.
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

        /// <summary>The text of the given variables: one line each, in name order, the description on one line.</summary>
        internal static string Of(IEnumerable<VariableFact> variables)
        {
            var text = new System.Text.StringBuilder();
            foreach (VariableFact variable in (variables ?? Enumerable.Empty<VariableFact>())
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.Append('&').Append(variable.Name).Append(variable.Declaration)
                    .Append(" // ").Append(OneLine(variable.Description)).Append('\n');
            }
            return text.ToString();
        }

        private static string OneLine(string text) => (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');

        // The variables of the object, read from its variables part. A description the SDK does not expose throws here, and then the
        // whole text is unknown: a missing description is never taken for a deleted one.
        private static IEnumerable<VariableFact> ReadVariables(KBObject obj)
        {
            var part = obj.Parts.Get<VariablesPart>();
            if (part == null) return new VariableFact[0];
            return part.Variables
                .Cast<global::Artech.Genexus.Common.Variable>()
                .Select(v => new VariableFact(v.Name, VariableInjector.DeclarationOf(part, v), v.Description))
                .ToList();
        }
    }

    /// <summary>One variable as the variables text reads it: its name, the declaration that follows the name, and its description.</summary>
    internal sealed class VariableFact
    {
        internal VariableFact(string name, string declaration, string description)
        {
            Name = name;
            Declaration = declaration;
            Description = description;
        }

        internal string Name { get; }

        internal string Declaration { get; }

        internal string Description { get; }
    }
}
