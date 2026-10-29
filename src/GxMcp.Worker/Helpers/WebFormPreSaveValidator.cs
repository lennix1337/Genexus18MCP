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
        }

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
                    string sev = ReadString(m, "Level") ?? ReadString(m, "Severity") ?? ReadString(m, "Type") ?? "Information";
                    string msg = ReadString(m, "Text") ?? ReadString(m, "Message") ?? m.ToString();
                    string src = ReadString(m, "Source") ?? ReadString(m, "ObjectName") ?? null;
                    report.Messages.Add(new ValidationMessage { Severity = sev, Message = msg, Source = src });
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
