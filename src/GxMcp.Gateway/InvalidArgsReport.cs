using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The texts of an <c>InvalidArgs</c> envelope. The violations carry the field, the
    /// expected shape and the accepted names, but a client that renders only
    /// <c>error.message</c> showed the same sentence for every rejected call, and the
    /// hint named just the first violation (#655). The message now names the offending
    /// fields, the hint walks every violation, and an unknown argument lists the names
    /// the tool accepts.
    /// </summary>
    internal static class InvalidArgsReport
    {
        private const int MessageViolations = 3;
        private const int HintExtraViolations = 2;
        private const int HintAcceptedNames = 15;

        private static bool IsMissing(GatewayArgsValidator.Violation v) => v.Actual == "missing";
        private static bool IsUnknown(GatewayArgsValidator.Violation v) => v.Actual == "unknown argument";

        /// <summary>One violation as a clause: the field and what is wrong with it.</summary>
        internal static string Clause(GatewayArgsValidator.Violation v)
        {
            string text = IsMissing(v) ? $"'{v.Path}' is required"
                : IsUnknown(v) ? $"'{v.Path}' is not an argument of this tool"
                : $"'{v.Path}' expected {v.Expected}, got {v.Actual}";
            return string.IsNullOrEmpty(v.Suggestion) ? text : $"{text} (did you mean '{v.Suggestion}'?)";
        }

        /// <summary><c>error.message</c>: the tool and the first violations, by field name.</summary>
        internal static string Message(string toolName, IReadOnlyList<GatewayArgsValidator.Violation> violations)
        {
            string clauses = string.Join("; ", violations.Take(MessageViolations).Select(Clause));
            int more = violations.Count - MessageViolations;
            if (more > 0) clauses += $"; and {more} more";
            return $"Arguments for tool '{toolName}' failed schema validation: {clauses}.";
        }

        /// <summary>
        /// <c>error.hint</c>: the first violation as before, then the next ones, then the
        /// accepted names when the first is an unknown argument.
        /// </summary>
        internal static string Hint(IReadOnlyList<GatewayArgsValidator.Violation> violations)
        {
            var first = violations[0];
            string hint = IsMissing(first)
                ? $"Required field '{first.Path}' is missing — expected {first.Expected}."
                : IsUnknown(first)
                    ? $"Unknown argument '{first.Path}'."
                    : $"Field '{first.Path}': expected {first.Expected}, got {first.Actual}.";
            if (!string.IsNullOrEmpty(first.Suggestion))
            {
                hint += $" Did you mean '{first.Suggestion}'?";
            }

            if (IsUnknown(first) && first.Accepted is { Count: > 0 } accepted)
            {
                string names = string.Join(", ", accepted.Take(HintAcceptedNames));
                int rest = accepted.Count - HintAcceptedNames;
                hint += rest > 0
                    ? $" Accepted arguments: {names}, and {rest} more (see violations[].accepted)."
                    : $" Accepted arguments: {names}.";
            }

            var others = violations.Skip(1).ToList();
            if (others.Count > 0)
            {
                hint += " Also: " + string.Join("; ", others.Take(HintExtraViolations).Select(Clause));
                int hidden = others.Count - HintExtraViolations;
                hint += hidden > 0 ? $"; and {hidden} more (see violations)." : ".";
            }

            return hint;
        }
    }
}
