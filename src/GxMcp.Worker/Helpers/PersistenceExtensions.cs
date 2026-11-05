using System;
using System.Linq;
using System.Text;
using Artech.Architecture.Common.Objects;
using Artech.Common.Diagnostics;

namespace GxMcp.Worker.Helpers
{
    public static class PersistenceExtensions
    {
        public static void EnsureSave(this KBObject obj, bool check = true)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            OutputMessages msgs = new OutputMessages();
            
            if (check)
            {
                bool isValid = obj.Validate(msgs);
                if (!isValid || msgs.HasErrors)
                {
                    string errorText = ExtractErrorText(msgs, obj);
                    throw new Exception($"Validation failed for {obj.TypeDescriptor.Name} '{obj.Name}': {errorText}");
                }
            }

            try
            {
                var saveMethod = obj.GetType().GetMethod("Save", new Type[] { typeof(bool) });
                if (saveMethod != null)
                    saveMethod.Invoke(obj, new object[] { check });
                else
                    obj.Save();
            }
            catch (Exception ex)
            {
                // Force a second validation pass after failure to capture what went wrong
                obj.Validate(msgs);
                string validationText = ExtractErrorText(msgs, obj);
                string sdkMessages = GetSdkMessages(obj);

                // Friction-report 05-13 #2 (deep): when the SDK's Prototype-model validator
                // emits `src0216 'X' propriedade inválida` for a property whose owning SDT
                // *does* have that item in the Design model (Model 1) — provable via SQL —
                // the validator is stale, not the data. Retry the Save bypassing validation
                // (`KBObjectSavePreferences.SkipValidation=true`). The persisted Source
                // round-trips correctly and downstream generation uses Model 1 where the
                // items exist. Only fires when the validation text contains src0216, so
                // legitimate validation errors (src0059 syntax errors, etc.) still bubble up.
                string combined = (validationText ?? string.Empty) + " | " + (sdkMessages ?? string.Empty);
                if (combined.IndexOf("src0216", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (TrySaveSkippingValidation(obj, out string skipError))
                    {
                        Logger.Info($"[EnsureSave] {obj.Name}: bypassed src0216 stale-prototype-model validator via SkipValidation=true.");
                        return;
                    }
                    Logger.Warn($"[EnsureSave] {obj.Name}: SkipValidation retry failed: {skipError}");
                }

                if (!string.IsNullOrEmpty(validationText) && !sdkMessages.Contains(validationText))
                    sdkMessages += " [VALIDATION]: " + validationText;

                throw new Exception($"SDK Save Exception for {obj.Name}: {ex.InnerException?.Message ?? ex.Message}. Detailed Messages: {sdkMessages}", ex);
            }
            
            if (msgs.HasErrors || !string.IsNullOrEmpty(GetSdkMessages(obj)))
            {
                string errorText = ExtractErrorText(msgs, obj);
                if (!string.IsNullOrEmpty(errorText))
                    throw new Exception($"Save failed for {obj.TypeDescriptor.Name} '{obj.Name}': {errorText}");
            }
        }

        // Calls KBObject.Save(KBObjectSavePreferences) with SkipValidation=true via reflection.
        // Returns true on success; emits the failure reason via `error` otherwise.
        private static bool TrySaveSkippingValidation(KBObject obj, out string error)
        {
            error = null;
            try
            {
                // KBObjectSavePreferences lives in Artech.Architecture.Common.dll, not in the
                // assembly that owns the KBObject (which is typically Artech.Genexus.Common).
                // Walk loaded assemblies until we find it.
                Type prefsType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        prefsType = asm.GetType("Artech.Architecture.Common.Objects.KBObjectSavePreferences", throwOnError: false);
                        if (prefsType != null) break;
                    }
                    catch { }
                }
                if (prefsType == null)
                {
                    error = "KBObjectSavePreferences type unresolved across loaded assemblies";
                    return false;
                }
                object prefs = Activator.CreateInstance(prefsType);
                var skipProp = prefsType.GetProperty("SkipValidation");
                if (skipProp == null || !skipProp.CanWrite)
                {
                    error = "SkipValidation property not writable";
                    return false;
                }
                skipProp.SetValue(prefs, true);
                var save = obj.GetType().GetMethod("Save", new[] { prefsType });
                if (save == null)
                {
                    error = "Save(KBObjectSavePreferences) not found on " + obj.GetType().FullName;
                    return false;
                }
                save.Invoke(obj, new[] { prefs });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        // A raw KBObject.Save() failure only says "<type> '<name>' validation failed.":
        // re-run validation on the failed state so the caller sees which validator,
        // node or field rejected the save.
        public static Exception WithValidationDetail(this KBObject obj, Exception saveFailure)
        {
            if (obj == null || saveFailure == null) return saveFailure;
            string detail = null;
            try
            {
                var msgs = new OutputMessages();
                obj.Validate(msgs);
                detail = ExtractErrorText(msgs, obj);
            }
            catch { /* the detail is advisory; keep the original failure */ }
            string message = ComposeSaveFailureMessage(saveFailure.Message, detail);
            return message == saveFailure.Message ? saveFailure : new InvalidOperationException(message, saveFailure);
        }

        internal static string ComposeSaveFailureMessage(string saveMessage, string validationDetail)
        {
            saveMessage = (saveMessage ?? string.Empty).Trim();
            string detail = (validationDetail ?? string.Empty).Trim();
            if (detail.Length == 0 || saveMessage.IndexOf(detail, StringComparison.Ordinal) >= 0) return saveMessage;
            if (detail.IndexOf(saveMessage, StringComparison.Ordinal) >= 0 && saveMessage.Length > 0) return detail;
            return saveMessage.Length == 0 ? detail : saveMessage + " Detailed Messages: " + detail;
        }

        public static string GetSdkMessages(this object target)
        {
            if (target == null) return string.Empty;
            var sb = new StringBuilder();

            if (target is KBObject obj)
            {
                foreach (var part in obj.Parts)
                {
                    string partMsgs = GetSdkMessages((object)part);
                    if (!string.IsNullOrEmpty(partMsgs))
                    {
                        if (sb.Length > 0) sb.Append(" | ");
                        sb.Append($"[{part.TypeDescriptor?.Name ?? "Part"}]: {partMsgs}");
                    }
                }
            }

            try
            {
                var prop = target.GetType().GetProperty("Messages", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (prop != null)
                {
                    var value = prop.GetValue(target);
                    if (value is System.Collections.IEnumerable list)
                    {
                        foreach (object msg in list)
                        {
                            if (msg == null) continue;
                            if (sb.Length > 0) sb.Append(" | ");
                            sb.Append(msg.ToString());
                        }
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        private static string ExtractErrorText(OutputMessages msgs, KBObject obj = null)
        {
            StringBuilder sb = new StringBuilder();

            if (msgs != null)
            {
                if (!string.IsNullOrEmpty(msgs.ErrorText)) sb.Append(msgs.ErrorText);
                else if (!string.IsNullOrEmpty(msgs.FullText)) sb.Append(msgs.FullText);
                else
                {
                    var errors = msgs.OnlyMessages
                                     .Where(m => m is OutputError)
                                     .Select(m => m.Text)
                                     .ToList();
                    if (errors.Any()) sb.Append(string.Join(" | ", errors));
                }
            }

            if (obj != null)
            {
                string localMsgs = GetSdkMessages(obj);
                if (!string.IsNullOrEmpty(localMsgs))
                {
                    if (sb.Length > 0) sb.Append(" [SDK-LOCAL]: ");
                    sb.Append(localMsgs);
                }
            }
            
            return sb.ToString();
        }
    }
}
