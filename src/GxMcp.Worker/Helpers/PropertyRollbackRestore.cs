using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The decisions behind the property rollback of a batch write, kept free of SDK types so they can be tested without a
    /// Knowledge Base. A property goes back in two steps: it is written back to its previous value inside the rollback
    /// transaction, and it counts as restored only when reading it again on the fresh copy gives that previous value. A
    /// property whose previous value was not read is never written back, so the rollback never invents a value, and a
    /// property that fails does not stop the others. The reports name properties and never carry a value.
    /// </summary>
    internal static class PropertyRollbackRestore
    {
        /// <summary>The requested names, split by whether the rollback put their previous value back and confirmed it.</summary>
        internal sealed class Report
        {
            internal Report(IReadOnlyList<string> restored, IReadOnlyList<string> notRestored)
            {
                Restored = restored;
                NotRestored = notRestored;
            }

            /// <summary>Properties written back to their previous value and read back with that value.</summary>
            internal IReadOnlyList<string> Restored { get; }

            /// <summary>Properties left as written: not read before, not written, or not confirmed by the read-back.</summary>
            internal IReadOnlyList<string> NotRestored { get; }

            /// <summary>The two lists as the receipt carries them: names only, never a value.</summary>
            internal void AddTo(JObject receipt)
            {
                receipt["propertiesRestored"] = new JArray(Restored.ToArray());
                receipt["propertiesNotRestored"] = new JArray(NotRestored.ToArray());
            }
        }

        /// <summary>
        /// The report for a rollback that restored nothing: it was refused, or its transaction never committed, so every
        /// requested property is still as the batch wrote it.
        /// </summary>
        internal static Report NothingRestored(IEnumerable<string> requestedNames)
            => new Report(new string[0], (requestedNames ?? Enumerable.Empty<string>()).ToList());

        /// <summary>
        /// The parenthesis the "unrelated part changed" reason adds: the names of the parts that differ, never their content.
        /// Empty when no name is known, so the reason reads as it did before.
        /// </summary>
        internal static string DescribeChangedParts(IEnumerable<string> changedParts)
        {
            string[] names = (changedParts ?? Enumerable.Empty<string>()).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            return names.Length == 0 ? string.Empty : " (" + string.Join(", ", names) + ")";
        }

        /// <summary>
        /// First step, inside the rollback transaction: writes each property back to its previous value, in request order.
        /// Returns the names written. A property with no previous value is not written, and one whose write throws is left
        /// out while the rest still go back.
        /// </summary>
        internal static IReadOnlyList<string> WriteBack(IEnumerable<string> requestedNames, IDictionary<string, string> previousValues, Action<string, string> restoreOne)
        {
            var written = new List<string>();
            foreach (string name in requestedNames)
            {
                if (!previousValues.TryGetValue(name, out string previous) || previous == null) continue;
                try
                {
                    restoreOne(name, previous);
                    written.Add(name);
                }
                catch (Exception ex)
                {
                    ExceptionRoot.Log("Rollback could not write property '" + name + "' back", ex);
                }
            }
            return written;
        }

        /// <summary>
        /// Second step, after the commit and on the fresh copy: a property is restored only when it was written back and
        /// <paramref name="readBack"/> gives its previous value again. A written property that reads back differently, or
        /// cannot be read, is not restored. A read-back that throws is logged and counts as not confirmed; the others still go on.
        /// </summary>
        internal static Report Confirm(IEnumerable<string> requestedNames, IDictionary<string, string> previousValues, IEnumerable<string> written, Func<string, string> readBack)
        {
            var writtenNames = new HashSet<string>(written);
            var restored = new List<string>();
            var notRestored = new List<string>();
            foreach (string name in requestedNames)
            {
                previousValues.TryGetValue(name, out string previous);
                bool confirmed = writtenNames.Contains(name) && previous != null
                    && ReadsBackAs(name, previous, readBack);
                if (confirmed) restored.Add(name);
                else notRestored.Add(name);
            }
            return new Report(restored, notRestored);
        }

        /// <summary>
        /// The reset variant of the first step. A property that had no explicit value before the batch is reset to its
        /// default, not written back with its effective value, which would make it explicit on the object. A name that the
        /// SDK will not reset is left out, so it is reported as not restored. Other names keep the rule of
        /// <see cref="WriteBack"/>.
        /// </summary>
        internal static IReadOnlyList<string> ResetOrWriteBack(IEnumerable<string> requestedNames, IDictionary<string, string> previousValues,
            ISet<string> wereDefault, Func<string, bool> canReset, Action<string> resetOne, Action<string, string> restoreOne)
        {
            var written = new List<string>();
            foreach (string name in requestedNames)
            {
                try
                {
                    if (wereDefault != null && wereDefault.Contains(name))
                    {
                        if (canReset == null || !canReset(name)) continue;
                        resetOne(name);
                        written.Add(name);
                        continue;
                    }
                    if (!previousValues.TryGetValue(name, out string previous) || previous == null) continue;
                    restoreOne(name, previous);
                    written.Add(name);
                }
                catch (Exception ex)
                {
                    ExceptionRoot.Log("Rollback could not write property '" + name + "' back", ex);
                }
            }
            return written;
        }

        /// <summary>
        /// The confirmation after the reset variant. A property that was default before the batch counts as restored when it
        /// was written, when it reads back its previous value (if one was read), and when it is default again. The others
        /// follow the rule of <see cref="Confirm"/>.
        /// </summary>
        internal static Report ConfirmWithDefaults(IEnumerable<string> requestedNames, IDictionary<string, string> previousValues,
            ISet<string> wereDefault, IEnumerable<string> written, Func<string, string> readBack, Func<string, bool> isDefaultNow)
        {
            var writtenNames = new HashSet<string>(written);
            var restored = new List<string>();
            var notRestored = new List<string>();
            foreach (string name in requestedNames)
            {
                previousValues.TryGetValue(name, out string previous);
                bool wasDefault = wereDefault != null && wereDefault.Contains(name);
                bool confirmed = writtenNames.Contains(name) && (wasDefault
                    ? (previous == null || ReadsBackAs(name, previous, readBack)) && IsDefaultNow(name, isDefaultNow)
                    : previous != null && ReadsBackAs(name, previous, readBack));
                if (confirmed) restored.Add(name);
                else notRestored.Add(name);
            }
            return new Report(restored, notRestored);
        }

        private static bool IsDefaultNow(string name, Func<string, bool> isDefaultNow)
        {
            try
            {
                return isDefaultNow != null && isDefaultNow(name);
            }
            catch (Exception ex)
            {
                ExceptionRoot.Log("Rollback could not read whether property '" + name + "' is default", ex);
                return false;
            }
        }

        /// <summary>
        /// True when reading the property back gives its previous value. A read that throws is logged and counts as not
        /// confirmed, so one property's failed read cannot take the whole rollback report down.
        /// </summary>
        private static bool ReadsBackAs(string name, string previous, Func<string, string> readBack)
        {
            try
            {
                return string.Equals(readBack(name), previous, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                ExceptionRoot.Log("Rollback could not read property '" + name + "' back", ex);
                return false;
            }
        }

        /// <summary>
        /// Writes the captured text back into each source part, in the order given. A part whose write throws is recorded and
        /// the others still get their text. Returns the keys of the parts that could not be written.
        /// </summary>
        internal static IReadOnlyList<string> RestoreSourceTexts(IEnumerable<KeyValuePair<string, string>> sourceTexts, Action<string, string> setText)
        {
            var failed = new List<string>();
            foreach (KeyValuePair<string, string> part in sourceTexts)
            {
                try
                {
                    setText(part.Key, part.Value);
                }
                catch (Exception ex)
                {
                    ExceptionRoot.Log("Rollback could not restore the source text of part '" + part.Key + "'", ex);
                    failed.Add(part.Key);
                }
            }
            return failed;
        }

        /// <summary>
        /// The sentence the rollback message adds when the source text of some parts could not be written back; it names the
        /// parts. Empty when every part got its text back, so a clean rollback reads as before.
        /// </summary>
        internal static string DescribeSourceFailures(IReadOnlyList<string> failed)
        {
            if (failed == null || failed.Count == 0) return string.Empty;
            return " The source text of " + string.Join(", ", failed) + " could not be written back; check the code of the object.";
        }
    }
}
