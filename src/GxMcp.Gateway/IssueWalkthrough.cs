using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>One tool call as copied out of the <see cref="OperationTracker"/> for the issue walkthrough.</summary>
    internal sealed class IssueStep
    {
        public DateTime AtUtc { get; init; }
        public string Tool { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public long? DurationMs { get; init; }
        public JObject? Arguments { get; init; }
        public string? Code { get; init; }
        public string? Message { get; init; }
        public string? ExceptionType { get; init; }
        public string? FailureTrace { get; init; }
    }

    // genexus_doctor's issueReport.walkthrough: the calls that led to a problem, in a form a
    // reporter can paste into a public issue and a maintainer can replay on a test KB. Each step
    // keeps the tool, outcome, error code, root exception type and its first SDK frames; the
    // arguments keep their shape. Words the tool's schema declares (property names, enum values
    // such as actions and parts) stay as they are; any other string becomes <v1>, <v2>... with
    // the same token for the same value in every step and message, and long or multi-line text
    // becomes <text N chars>. Object names, source, KB aliases and paths stay on the machine.
    internal static class IssueWalkthrough
    {
        internal const int MaxSteps = 20;
        private const int MaxInlineLength = 80;
        private const int MaxMessageLength = 400;
        // Shorter values would also rewrite ordinary words inside messages.
        private const int MinReplacedLength = 3;

        internal static JArray Build(IEnumerable<IssueStep> steps, Func<string, ISet<string>> vocabulary)
        {
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            var ordered = steps.OrderBy(s => s.AtUtc).ToList();
            ordered = ordered.Skip(Math.Max(0, ordered.Count - MaxSteps)).ToList();

            var walkthrough = new JArray();
            var messages = new List<(JObject Entry, string Message)>();
            for (int i = 0; i < ordered.Count; i++)
            {
                IssueStep step = ordered[i];
                var entry = new JObject
                {
                    ["step"] = i + 1,
                    ["atSeconds"] = Math.Round((step.AtUtc - ordered[0].AtUtc).TotalSeconds, 1),
                    ["tool"] = step.Tool,
                    ["outcome"] = step.Status,
                    ["durationMs"] = step.DurationMs.HasValue ? new JValue(step.DurationMs.Value) : JValue.CreateNull(),
                    ["args"] = Shape(step.Arguments ?? new JObject(), vocabulary(step.Tool), tokens)
                };
                if (!string.IsNullOrWhiteSpace(step.Code)) entry["code"] = step.Code;
                if (!string.IsNullOrWhiteSpace(step.ExceptionType)) entry["exceptionType"] = step.ExceptionType;
                if (!string.IsNullOrWhiteSpace(step.FailureTrace)) entry["failureTrace"] = step.FailureTrace;
                if (!string.IsNullOrWhiteSpace(step.Message)) messages.Add((entry, step.Message!));
                walkthrough.Add(entry);
            }

            // Messages last, so a name first seen in a later step's arguments is replaced too.
            var replacements = tokens.Where(t => t.Key.Length >= MinReplacedLength)
                .OrderByDescending(t => t.Key.Length).ToList();
            foreach (var (entry, message) in messages)
            {
                string text = Program.RedactForIssue(message);
                foreach (var replacement in replacements)
                    text = text.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
                entry["message"] = text.Length > MaxMessageLength ? text.Substring(0, MaxMessageLength) + "…" : text;
            }
            return walkthrough;
        }

        private static JToken Shape(JToken value, ISet<string> words, Dictionary<string, string> tokens)
        {
            switch (value.Type)
            {
                case JTokenType.Object:
                    var shaped = new JObject();
                    foreach (var property in ((JObject)value).Properties())
                    {
                        string key = words.Contains(property.Name) ? property.Name : Token(property.Name, tokens);
                        shaped[key] = Shape(property.Value, words, tokens);
                    }
                    return shaped;
                case JTokenType.Array:
                    return new JArray(((JArray)value).Select(item => Shape(item, words, tokens)));
                case JTokenType.String:
                    string text = value.ToString();
                    if (text.Length == 0 || words.Contains(text)) return text;
                    if (text.Length > MaxInlineLength || text.IndexOf('\n') >= 0) return $"<text {text.Length} chars>";
                    return Token(text, tokens);
                case JTokenType.Integer:
                case JTokenType.Float:
                case JTokenType.Boolean:
                case JTokenType.Null:
                    return value.DeepClone();
                default:
                    return $"<{value.Type.ToString().ToLowerInvariant()}>";
            }
        }

        private static string Token(string value, Dictionary<string, string> tokens)
        {
            if (!tokens.TryGetValue(value, out string? token))
            {
                token = $"<v{tokens.Count + 1}>";
                tokens[value] = token;
            }
            return token;
        }
    }
}
