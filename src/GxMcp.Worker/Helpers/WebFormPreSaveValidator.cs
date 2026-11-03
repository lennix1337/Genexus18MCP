using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// W5 (friction-report 2026-05-19 roadmap): pre-save validation via the SDK's
    /// authoritative validator. The GeneXus IDE calls `WebFormHelper.Validate(part,
    /// OutputMessages)` before persistence; we invoke it from the worker after a
    /// layout edit so the agent gets validation errors immediately, not after a
    /// later EnsureSave that may run on a different code path.
    ///
    /// Returns a list of validation messages with severity. Empty list means the
    /// SDK considers the part well-formed. Caller decides whether to refuse the
    /// write (error-level), warn (warning-level), or pass through.
    /// </summary>
    public static class WebFormPreSaveValidator
    {
        public sealed class ValidationMessage
        {
            public string Severity;   // "Error", "Warning", "Information"
            public string Message;
            public string Source;     // typically the control id or layout path
            public string Code;       // the SDK's own error code (spc0233, ...), when the message carries one
            public List<string> Positions; // where the SDK says the problem is (line/char, context), at most MaxPositions
        }

        private const int MaxPositions = 3;

        public sealed class ValidationReport
        {
            public List<ValidationMessage> Messages = new List<ValidationMessage>();
            public bool ValidatorAvailable;
            public string Error;
        }

        /// <summary>
        /// Run the SDK validator against a WebFormPart. Returns all messages. Best-effort:
        /// returns an empty list on reflection failure rather than throwing — validation
        /// is advisory; the actual save path will surface errors authoritatively.
        /// </summary>
        public static List<ValidationMessage> Validate(object webFormPart)
        {
            return ValidateDetailed(webFormPart).Messages;
        }

        /// <summary>
        /// Same validation as <see cref="Validate"/>, but preserves whether the
        /// SDK validator was actually found and whether reflection failed. An empty
        /// message list alone is not enough evidence: older GeneXus builds may not
        /// ship WebFormHelper.Validate at all.
        /// </summary>
        public static ValidationReport ValidateDetailed(object webFormPart)
        {
            var report = new ValidationReport();
            if (webFormPart == null)
            {
                report.Error = "WebForm part is not available.";
                return report;
            }

            try
            {
                Type helperType = FindType("Artech.Genexus.Common.Parts.WebForm.WebFormHelper");
                if (helperType == null)
                {
                    report.Error = "WebFormHelper was not found in the loaded GeneXus SDK.";
                    return report;
                }

                // Locate Validate(part, OutputMessages) — signatures vary across SDK versions:
                //   Validate(WebFormPart, OutputMessages)
                //   Validate(KBObject, OutputMessages)
                MethodInfo validate = helperType.GetMethods(Compatibility.SdkMemberProbe.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Validate") return false;
                        var ps = m.GetParameters();
                        if (ps.Length != 2) return false;
                        // first param must accept the part; second must be OutputMessages-like
                        return ps[0].ParameterType.IsInstanceOfType(webFormPart)
                            && ps[1].ParameterType.Name.IndexOf("OutputMessages", StringComparison.OrdinalIgnoreCase) >= 0;
                    });
                if (validate == null)
                {
                    Logger.Info("[WebFormValidate] WebFormHelper.Validate(part, OutputMessages) overload not found.");
                    report.Error = "WebFormHelper.Validate(part, OutputMessages) overload was not found.";
                    return report;
                }

                report.ValidatorAvailable = true;

                // Construct an OutputMessages instance to collect.
                Type outMsgsType = validate.GetParameters()[1].ParameterType;
                object outMsgs = TryCreateOutputMessages(outMsgsType);
                if (outMsgs == null)
                {
                    Logger.Info("[WebFormValidate] could not instantiate " + outMsgsType.FullName);
                    report.Error = "Could not instantiate " + outMsgsType.FullName + ".";
                    return report;
                }

                object validationResult = null;
                try
                {
                    validationResult = validate.Invoke(null, new[] { webFormPart, outMsgs });
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    Logger.Info("[WebFormValidate] Validate threw: " + inner.GetType().Name + ": " + inner.Message);
                    report.Error = inner.Message;
                    return report;
                }

                // Extract messages — OutputMessages typically implements IEnumerable<OutputMessage>
                // or has a property like Messages. Try both.
                IEnumerable msgs = outMsgs as IEnumerable;
                if (msgs == null)
                {
                    var p = outMsgsType.GetProperty("Messages", BindingFlags.Public | BindingFlags.Instance);
                    msgs = p?.GetValue(outMsgs) as IEnumerable;
                }
                if (msgs == null)
                {
                    if (validationResult is bool && !(bool)validationResult)
                    {
                        report.Messages.Add(new ValidationMessage
                        {
                            Severity = "Error",
                            Message = "The GeneXus SDK WebForm validator returned false without diagnostics.",
                            Source = "WebForm"
                        });
                    }
                    else
                    {
                        report.Error = "WebForm validator returned no enumerable message collection.";
                    }
                    return report;
                }

                foreach (var m in msgs)
                {
                    if (m == null) continue;
                    report.Messages.Add(FromSdkMessage(m));
                }

                // Some SDK builds communicate failure through the bool return and
                // leave OutputMessages empty. Preserve that signal so callers do not
                // mistake an unsuccessful validation for a clean WebForm.
                if (validationResult is bool && !(bool)validationResult && !HasErrors(report.Messages))
                {
                    report.Messages.Add(new ValidationMessage
                    {
                        Severity = "Error",
                        Message = "The GeneXus SDK WebForm validator returned false without diagnostics.",
                        Source = "WebForm"
                    });
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                report.Error = inner.Message;
                Logger.Info("[WebFormValidate] outer fault: " + inner.Message);
            }

            return report;
        }

        /// <summary>
        /// One message of the SDK's <c>OutputMessages</c> as the fields a caller can act on: level, text,
        /// the SDK error code (<c>spc0233</c>) and the positions the SDK attached (issue #830: a refusal
        /// used to carry only level and text, so the caller bisected the XML by hand).
        /// </summary>
        internal static ValidationMessage FromSdkMessage(object message)
        {
            string severity = ReadString(message, "Level") ?? ReadString(message, "Severity") ?? ReadString(message, "Type") ?? "Information";
            string text = ReadString(message, "Text") ?? ReadString(message, "Message") ?? message.ToString();
            string source = ReadString(message, "Source") ?? ReadString(message, "ObjectName") ?? null;
            string code = ReadString(message, "ErrorCode") ?? ReadString(message, "Code");
            return new ValidationMessage
            {
                Severity = severity,
                Message = text,
                Source = source,
                Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
                Positions = ReadPositions(message)
            };
        }

        /// <summary>
        /// True if any message has severity that maps to "error" (case-insensitive, accepts
        /// English / Spanish / Portuguese / numeric MessageLevel.Error == 1 strings).
        /// </summary>
        public static bool HasErrors(IEnumerable<ValidationMessage> msgs)
        {
            if (msgs == null) return false;
            foreach (var m in msgs)
            {
                if (m != null && IsErrorSeverity(m.Severity)) return true;
            }
            return false;
        }

        public static bool IsErrorSeverity(string severity)
        {
            string s = severity?.ToLowerInvariant() ?? "";
            return s.Contains("error") || s == "1" || s == "erro";
        }

        /// <summary>
        /// The first errors of a refused form as one line of text: "[code] message (position)". The
        /// refusal's <c>message</c> carries it so a caller that reads only that field still learns what
        /// the SDK objected to and where; the complete list stays in <c>validationDiagnostics</c>.
        /// Returns an empty string when there is no error message to quote.
        /// </summary>
        public static string SummarizeErrors(IEnumerable<ValidationMessage> messages, int max = 3, int maxChars = 700)
        {
            if (messages == null) return string.Empty;
            var errors = messages.Where(m => m != null && IsErrorSeverity(m.Severity)).ToList();
            if (errors.Count == 0) return string.Empty;

            // The SDK often reports the same problem once per control it affects: quote each distinct message once.
            var parts = new List<string>();
            foreach (var error in errors)
            {
                // One line: the error envelope keeps only the first line of a message.
                string text = System.Text.RegularExpressions.Regex.Replace((error.Message ?? string.Empty).Trim(), @"\s+", " ");
                if (text.Length == 0) continue;
                string part = (string.IsNullOrWhiteSpace(error.Code) ? string.Empty : "[" + error.Code + "] ") + text;
                string where = error.Positions != null && error.Positions.Count > 0
                    ? string.Join(", ", error.Positions)
                    : error.Source;
                if (!string.IsNullOrWhiteSpace(where)) part += " (" + System.Text.RegularExpressions.Regex.Replace(where.Trim(), @"\s+", " ") + ")";
                parts.Add(part);
            }
            var distinct = parts.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count == 0) return string.Empty;

            string summary = (distinct.Count == 1 ? "SDK error: " : "First SDK errors: ") + string.Join("; ", distinct.Take(max));
            if (distinct.Count > max) summary += "; +" + (distinct.Count - max) + " more in validationDiagnostics";
            if (summary.Length > maxChars) summary = summary.Substring(0, maxChars) + "...";
            return summary;
        }

        private static List<string> ReadPositions(object message)
        {
            var result = new List<string>();
            try
            {
                var type = message.GetType();
                var many = type.GetProperty("Positions", BindingFlags.Public | BindingFlags.Instance)?.GetValue(message) as IEnumerable;
                if (many != null)
                    foreach (var position in many) AddPosition(result, position);
                if (result.Count == 0)
                    AddPosition(result, type.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance)?.GetValue(message));
            }
            catch (Exception ex)
            {
                Logger.Debug("[WebFormValidate] message positions could not be read: " + (ex.InnerException ?? ex).Message);
            }
            return result;
        }

        private static void AddPosition(List<string> sink, object position)
        {
            if (position == null || sink.Count >= MaxPositions) return;
            string text = DescribePosition(position);
            if (!string.IsNullOrWhiteSpace(text) && !sink.Contains(text)) sink.Add(text);
        }

        /// <summary>
        /// A position the SDK attached to a message, as text: "line 3, char 12" for a text position,
        /// the context string for a context position, otherwise the position's own text. A position
        /// that says nothing beyond its type name is dropped rather than quoted.
        /// </summary>
        internal static string DescribePosition(object position)
        {
            if (position == null) return null;
            // The SDK answers Position with a help-page position built from the error code when the message has
            // none of its own; that is a link to the error's documentation, not a place in the form.
            if (string.Equals(position.GetType().Name, "ErrorHelpPosition", StringComparison.Ordinal)) return null;
            string line = ReadString(position, "Line");
            if (line != null)
            {
                string chr = ReadString(position, "Char");
                return "line " + line + (chr != null ? ", char " + chr : string.Empty);
            }
            string context = ReadString(position, "Context");
            if (!string.IsNullOrWhiteSpace(context)) return context.Trim();

            string text;
            try { text = position.ToString(); } catch { return null; }
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (string.Equals(text, position.GetType().FullName, StringComparison.Ordinal)
                || string.Equals(text, position.GetType().Name, StringComparison.Ordinal)) return null;
            return text.Length > 160 ? text.Substring(0, 160) + "..." : text;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false, true);
                if (t != null) return t;
            }
            return null;
        }

        private static object TryCreateOutputMessages(Type t)
        {
            // Most common pattern: parameterless constructor.
            try
            {
                var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (ctor != null) return ctor.Invoke(null);
            }
            catch { }
            // Fallback: Activator handles the rest.
            try { return Activator.CreateInstance(t); } catch { }
            return null;
        }

        private static string ReadString(object target, string memberName)
        {
            try
            {
                var p = target.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p.GetValue(target)?.ToString();
            }
            catch { }
            try
            {
                var f = target.GetType().GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return f.GetValue(target)?.ToString();
            }
            catch { }
            return null;
        }
    }
}
