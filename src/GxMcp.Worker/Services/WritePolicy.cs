using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public static class WritePolicy
    {
        private static readonly Regex GenericLineErrorRegex = new Regex(
            @"^(erro|error)\s*,\s*line\s*:\s*\d+\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsUnchangedSourceWrite(string existingSource, string incomingSource, bool exact = false) =>
            string.Equals(
                exact ? existingSource ?? string.Empty : NormalizeSourceForComparison(existingSource),
                exact ? incomingSource ?? string.Empty : NormalizeSourceForComparison(incomingSource),
                StringComparison.Ordinal);

        public static string NormalizeSourceForComparison(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }

            return text.Replace("\r\n", "\n")
                       .Replace("\r", "\n")
                       .TrimEnd('\n');
        }

        public static bool IsLogicalSourcePart(string partName)
        {
            if (string.IsNullOrWhiteSpace(partName))
            {
                return false;
            }

            return partName.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                   partName.Equals("Events", StringComparison.OrdinalIgnoreCase) ||
                   partName.Equals("Code", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsCopyModelWriteLock(string message) =>
            !string.IsNullOrWhiteSpace(message)
            && message.IndexOf("CopyModel", StringComparison.OrdinalIgnoreCase) >= 0;

        public static string BuildFailureDetails(string primaryMessage, JArray issues)
        {
            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(primaryMessage))
            {
                details.Add(primaryMessage.Trim());
            }

            if (issues != null)
            {
                foreach (var issue in issues.OfType<JObject>())
                {
                    string description = issue["description"]?.ToString();
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        continue;
                    }

                    if (details.Any(d => string.Equals(d, description, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    details.Add(description.Trim());
                }
            }

            return string.Join(" | ", details);
        }

        // Returns true when a message is the SDK's uninformative "Erro" / "Error" / "Erro, line: 1" fallback.
        // Used to decide whether to upgrade an exception message with the part's GetSdkMessages() or diagnostics.
        public static bool IsBareGenericError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return true;
            string trimmed = message.Trim();
            if (string.Equals(trimmed, "Erro", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(trimmed, "Error", StringComparison.OrdinalIgnoreCase)) return true;
            if (GenericLineErrorRegex.IsMatch(trimmed)) return true;
            return false;
        }

        // Same as IsBareGenericError but tolerant of the "Part save failed: " / "Part save
        // reported errors: " wrappers WriteService adds around the raw SDK exception text.
        // "Part save failed: Erro" is still an uninformative error even though it isn't the
        // bare token "Erro".
        public static bool IsUninformativeSaveError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return true;
            string trimmed = message.Trim();
            foreach (var prefix in new[] { "Part save failed:", "Part save reported errors:" })
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(prefix.Length).Trim();
                    break;
                }
            }
            return IsBareGenericError(trimmed);
        }

        // Picks the most informative message available. When the SDK raises a bare "Erro" but
        // part.GetSdkMessages() or SdkDiagnosticsHelper.GetDiagnostics returned the real text
        // (e.g. "src0059: Esperando 'EndFor'..."), prefer that text. Otherwise return the
        // original exception message unchanged.
        public static string PreferDetailedMessage(string exceptionMessage, string sdkMessages, JArray issues)
        {
            string baseMessage = string.IsNullOrWhiteSpace(exceptionMessage) ? string.Empty : exceptionMessage.Trim();
            if (!IsBareGenericError(baseMessage)) return baseMessage;

            if (!string.IsNullOrWhiteSpace(sdkMessages))
            {
                string trimmed = sdkMessages.Trim();
                if (!IsBareGenericError(trimmed)) return trimmed;
            }

            if (issues != null)
            {
                foreach (var issue in issues.OfType<JObject>())
                {
                    string description = issue["description"]?.ToString();
                    if (string.IsNullOrWhiteSpace(description)) continue;
                    if (IsBareGenericError(description)) continue;
                    return description.Trim();
                }
            }

            return baseMessage;
        }

        // Matches "src0216: 'Foo' propriedade inválida." style diagnostics. The capture group
        // returns the property name (Foo) that the SDK flagged as invalid. This is the SDK's
        // signal for "I don't know this property on the dotted accessor target", which most
        // often means the agent wrote `&Var.Foo` without declaring `&Var` (auto-inject now
        // skips on purpose to avoid the wrong-typed VARCHAR fallback — friction-report 05-13 #3).
        private static readonly Regex Src0216Regex = new Regex(
            @"src0216:\s*'(?<prop>[^']+)'\s+propriedade\s+inv[áa]lida",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // Walks every "&VarName.PropName" reference in `sourceCode` and returns the set of
        // variable names whose corresponding `.PropName` matches one of the props flagged
        // by the SDK in `sdkErrorText` AND whose variable name is NOT in `declaredVars`.
        // This is the precise "src0216 + undeclared variable" pattern: when present, the
        // SDK's "property invalid" message is misleading — the real issue is the missing
        // variable declaration.
        public static IReadOnlyList<string> FindUndeclaredVariablesForSrc0216(
            string sdkErrorText,
            string sourceCode,
            IEnumerable<string> declaredVars)
        {
            if (string.IsNullOrWhiteSpace(sdkErrorText) || string.IsNullOrWhiteSpace(sourceCode))
            {
                return Array.Empty<string>();
            }

            var flaggedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Src0216Regex.Matches(sdkErrorText))
            {
                if (m.Success) flaggedProps.Add(m.Groups["prop"].Value.Trim());
            }
            if (flaggedProps.Count == 0) return Array.Empty<string>();

            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (declaredVars != null)
            {
                foreach (var v in declaredVars)
                {
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    declared.Add(v.TrimStart('&').Trim());
                }
            }

            var hits = new List<string>();
            // Capture &VarName.PropName; case-insensitive variable lookup; only flag once per var name.
            var memberAccess = new Regex(@"&(?<v>[A-Za-z_][A-Za-z0-9_]*)\.(?<p>[A-Za-z_][A-Za-z0-9_]*)",
                                          RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in memberAccess.Matches(sourceCode))
            {
                string v = m.Groups["v"].Value;
                string p = m.Groups["p"].Value;
                if (!flaggedProps.Contains(p)) continue;
                if (declared.Contains(v)) continue;
                if (seen.Add(v)) hits.Add(v);
            }
            return hits;
        }

        public static string BuildUndeclaredVariableHint(IReadOnlyList<string> undeclaredVars)
        {
            if (undeclaredVars == null || undeclaredVars.Count == 0) return null;
            string list = string.Join(", ", undeclaredVars.Select(v => "&" + v));
            return $"src0216 likely caused by undeclared variable(s) {list}. Use genexus_add_variable with typeName=<SDT|domain> to declare and bind before re-saving the Source.";
        }

        // C17: map the SDK diagnostic codes seen on Events-part writes to their known
        // (previously undocumented) causes so the agent gets an actionable next step
        // instead of a bare code. Returns null when no known code is present.
        //   src0208 — "event already defined": a userAction name="Foo" auto-generates an
        //             empty 'DoFoo' event stub, so appending your own Event 'DoFoo' collides.
        //   src0233/src0216 — a control-bound event (&Ctrl.Click / &Ctrl.ControlValueChanged /
        //             &Ctrl.Display=…) references a control that does not yet exist in the
        //             projected form, so Events must be written AFTER the layout/PatternInstance.
        public static string BuildEventDiagnosticHint(string sdkErrorText)
        {
            if (string.IsNullOrWhiteSpace(sdkErrorText)) return null;
            string t = sdkErrorText;
            bool has(string code) => t.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0;

            if (has("src0208") || t.IndexOf("already defined", StringComparison.OrdinalIgnoreCase) >= 0
                                || t.IndexOf("ya está definido", StringComparison.OrdinalIgnoreCase) >= 0
                                || t.IndexOf("já está definido", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "src0208 (event already defined): a userAction name=\"Foo\" auto-generates an empty 'DoFoo' event stub. Don't add a second Event 'DoFoo' — edit/fill the existing stub instead (patch the Events part where 'Event DoFoo … EndEvent' already exists).";
            }
            if (has("src0233") || has("src0216"))
            {
                return "src0233/src0216 on a control-bound event: the referenced control must exist in the projected form BEFORE the event compiles. Write the layout / apply the PatternInstance FIRST, then write the Events part that references &Control.Click / &Control.ControlValueChanged / &Control.Display.";
            }
            return null;
        }

        // issue #39: agents (coming from SQL / other ORMs) commonly write pseudo-rules that
        // GeneXus does not accept in the Rules part. The rules specifier rejects them with a
        // bare "Erro" and no line diagnostic, so the failure is uninformative. Map each such
        // statement-leading keyword to actionable guidance. Consulted ONLY to enrich an
        // already-failed Rules save — never to block a write (a real object could legitimately
        // be named the same, and blocking would be a false negative).
        private static readonly Dictionary<string, string> InvalidRuleKeywords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Unique"] =
                    "'Unique' is not a GeneXus transaction rule (the SDK reports src0295 \"Regra desconhecida\"). " +
                    "Enforce attribute uniqueness with a unique index: call genexus_structure action=create_index " +
                    "name=<Transaction> payload={\"attributes\":[\"<Attr>\"],\"unique\":true}, then " +
                    "genexus_lifecycle action=reorg to apply it to the database. The Unique clause only ever " +
                    "existed for queries (For Each) and was removed after GeneXus 18 Upgrade 9.",
            };

        // Statement-leading identifier immediately followed by '(' — i.e. the rule keyword of a
        // `Keyword(args) ...;` rule. Statements are ';'-delimited in the Rules DSL; the first has
        // no leading ';', hence (?:^|;). A member call like `Obj.Method(` is not matched because
        // the '.' breaks the (?:^|;)\s* anchor.
        private static readonly Regex RuleStatementLeadRegex = new Regex(
            @"(?:^|;)\s*(?<kw>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex BlockCommentsRegex = new Regex(
            @"/\*.*?\*/",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex LineCommentsRegex = new Regex(
            @"//[^\r\n]*",
            RegexOptions.Compiled);

        // Removes /* block */ and // line comments so a keyword mentioned in a comment doesn't
        // produce a spurious hint.
        private static string StripRuleComments(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            source = BlockCommentsRegex.Replace(source, " ");
            source = LineCommentsRegex.Replace(source, " ");
            return source;
        }

        // Returns the distinct statement-leading keywords in a Transaction Rules source that
        // GeneXus rejects as rules (see InvalidRuleKeywords). Order-preserving, de-duplicated.
        public static IReadOnlyList<string> FindInvalidRuleKeywords(string rulesSource)
        {
            if (string.IsNullOrWhiteSpace(rulesSource)) return Array.Empty<string>();
            string stripped = StripRuleComments(rulesSource);
            var hits = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in RuleStatementLeadRegex.Matches(stripped))
            {
                string kw = m.Groups["kw"].Value;
                if (InvalidRuleKeywords.ContainsKey(kw) && seen.Add(kw))
                {
                    hits.Add(kw);
                }
            }
            return hits;
        }

        public static string BuildInvalidRuleHint(IReadOnlyList<string> invalidKeywords)
        {
            if (invalidKeywords == null || invalidKeywords.Count == 0) return null;
            return string.Join(" ", invalidKeywords.Select(k => InvalidRuleKeywords[k]));
        }

        public static bool ShouldRetryWithoutPartSave(string partName, string exceptionMessage, string diagnosticText)
        {
            if (!IsLogicalSourcePart(partName))
            {
                return false;
            }

            string normalizedException = exceptionMessage?.Trim() ?? string.Empty;
            string normalizedDiagnostic = diagnosticText?.Trim() ?? string.Empty;

            bool genericFailure = string.IsNullOrWhiteSpace(exceptionMessage) ||
                                  string.Equals(normalizedException, "Erro", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(normalizedException, "Error", StringComparison.OrdinalIgnoreCase) ||
                                  GenericLineErrorRegex.IsMatch(normalizedException) ||
                                  exceptionMessage.IndexOf("Part save failed: Erro", StringComparison.OrdinalIgnoreCase) >= 0;
            bool genericDiagnostics = string.IsNullOrWhiteSpace(diagnosticText) ||
                                      string.Equals(normalizedDiagnostic, "Erro", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(normalizedDiagnostic, "Error", StringComparison.OrdinalIgnoreCase) ||
                                      GenericLineErrorRegex.IsMatch(normalizedDiagnostic);
            return genericFailure && genericDiagnostics;
        }
    }
}
