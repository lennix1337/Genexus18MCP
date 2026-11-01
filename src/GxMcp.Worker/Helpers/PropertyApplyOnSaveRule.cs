using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Whether a property write may skip the pattern that runs when an object is saved. A pattern applied on save (its
    /// "Apply:&lt;pattern id&gt;" property reads True) can rewrite the parts that the pattern owns. A plain property write
    /// does not change the pattern, so the write suppresses that run for its own save. The decision is kept free of SDK
    /// types in <see cref="ShouldSuppress"/>.
    /// </summary>
    internal static class PropertyApplyOnSaveRule
    {
        internal const string ApplyPropertyPrefix = "Apply:";

        /// <summary>
        /// True only for an object-level write (no control named), on an object whose apply-on-save flag is on, and only
        /// when the batch does not change a flag itself: turning a pattern on or off is the caller's decision.
        /// </summary>
        internal static bool ShouldSuppress(string controlName, bool applyOnSaveOn, IEnumerable<string> requestedNames)
        {
            if (!string.IsNullOrEmpty(controlName) || !applyOnSaveOn) return false;
            return !(requestedNames ?? Enumerable.Empty<string>())
                .Any(name => name != null && name.Trim().StartsWith(ApplyPropertyPrefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The suppression for the object about to be saved, or null when the rule says no. Dispose it after the commit:
        /// disposing gives the flag back the value it had before.
        /// </summary>
        internal static IDisposable Begin(KBObject obj, string controlName, IEnumerable<string> requestedNames)
        {
            if (obj == null) return null;
            return ShouldSuppress(controlName, AnyApplyOnSaveOn(obj), requestedNames)
                ? new PatternSaveIsolationScope(obj)
                : null;
        }

        /// <summary>
        /// The one save of a property write. The apply-on-save step is suppressed for this save only, when the rule says so; the suppression
        /// ends before the call returns, even when the save throws. Without an object there is no flag to suppress, and the save runs as it is.
        /// </summary>
        internal static void Save(KBObject obj, string controlName, IEnumerable<string> requestedNames, Action save)
        {
            if (obj == null)
            {
                save();
                return;
            }
            SaveWith(ObjectSaveFlags.Of(obj), AnyApplyOnSaveOn(obj), controlName, requestedNames, save);
        }

        // The same save with the flags and the apply-on-save state given, so that the order and the give-back can be tested without an SDK object.
        internal static void SaveWith(ISaveFlags flags, bool applyOnSaveOn, string controlName, IEnumerable<string> requestedNames, Action save)
        {
            using (BeginWith(flags, applyOnSaveOn, controlName, requestedNames))
                save();
        }

        internal static IDisposable BeginWith(ISaveFlags flags, bool applyOnSaveOn, string controlName, IEnumerable<string> requestedNames)
            => ShouldSuppress(controlName, applyOnSaveOn, requestedNames) ? new PatternSaveIsolationScope(flags) : null;

        /// <summary>
        /// The suppression for the restoring save of a rollback. The rollback always keeps the pattern from running on save for an
        /// object-level write, even when the batch changed a pattern flag: the save puts back what the snapshot holds, and a pattern
        /// run would rewrite the parts the snapshot has just restored.
        /// </summary>
        internal static IDisposable BeginForRollback(KBObject obj, string controlName)
        {
            if (obj == null) return null;
            return ShouldSuppressRollback(controlName, AnyApplyOnSaveOn(obj))
                ? new PatternSaveIsolationScope(obj)
                : null;
        }

        /// <summary>True for an object-level rollback on an object whose apply-on-save flag is on. The batch's own flags do not count.</summary>
        internal static bool ShouldSuppressRollback(string controlName, bool applyOnSaveOn)
            => string.IsNullOrEmpty(controlName) && applyOnSaveOn;

        // A pattern is applied on save when its "Apply:" property reads True on the object. When the properties cannot be
        // read, the save is left as it is: nothing is suppressed.
        private static bool AnyApplyOnSaveOn(KBObject obj)
        {
            try
            {
                foreach (dynamic prop in ((dynamic)obj).Properties)
                {
                    string name = Convert.ToString(prop.Name);
                    if (name == null || !name.StartsWith(ApplyPropertyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    object value = prop.Value;
                    bool on = value is bool flag
                        ? flag
                        : string.Equals(Convert.ToString(value), "True", StringComparison.OrdinalIgnoreCase);
                    if (on) return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[PATTERN-SAVE] apply-on-save flags could not be read: " + ex.Message);
            }
            return false;
        }
    }
}
