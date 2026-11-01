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

        /// <summary>What the read-back proved about the requested values, once the batch write has committed.</summary>
        internal enum PersistenceProof
        {
            /// <summary>Every requested value was read back as requested: the values are in the object.</summary>
            Verified,

            /// <summary>The object was read back and the requested values are not in it, or a rollback restored every property.</summary>
            NotPersisted,

            /// <summary>No read settled it.</summary>
            Unknown
        }

        /// <summary>
        /// Marks an error receipt whose batch write has committed: <c>committed</c> is true, <c>persisted</c> is a boolean that is false
        /// only when a read showed the requested values are not in the object, and <c>persistenceStateKnown</c> says whether a read settled
        /// it. An unknown state carries <c>persisted:true</c> and <c>persistenceStateKnown:false</c>, as the receipts of the Conditions and
        /// DataProviderParameters routes do: the value may be in the object, so a reader decides on <c>persistenceStateKnown</c> first.
        /// </summary>
        internal static void MarkCommitted(JObject receipt, PersistenceProof proof)
        {
            receipt["committed"] = true;
            receipt["persisted"] = proof != PersistenceProof.NotPersisted;
            receipt["persistenceStateKnown"] = proof != PersistenceProof.Unknown;
        }

        /// <summary>
        /// The proof after a verification failure whose rollback ran. A rollback that was verified as a whole, or that restored every
        /// property and confirmed each one, proves the requested values are gone. Anything less leaves the state unknown.
        /// </summary>
        internal static PersistenceProof ProofAfterRollback(bool rollbackVerified, Report report)
            => rollbackVerified || (report != null && report.NotRestored.Count == 0 && report.Restored.Count > 0)
                ? PersistenceProof.NotPersisted
                : PersistenceProof.Unknown;

        /// <summary>
        /// The proof after a rollback that failed. Until the restoring transaction commits, every property is still as the batch wrote it,
        /// so the earlier proof stands. Once it commits, nothing confirms the state.
        /// </summary>
        internal static PersistenceProof ProofAfterFailedRollback(bool restoreCommitted, PersistenceProof earlier)
            => restoreCommitted ? PersistenceProof.Unknown : earlier;

        /// <summary>
        /// The proof of a verification refusal, by its error code. A read-back that found another value (<c>PropertyNotPersisted</c>) shows
        /// the requested value is not in the object. Any other refusal leaves the state unknown.
        /// </summary>
        internal static PersistenceProof ProofOfVerificationRefusal(string errorCode)
            => string.Equals(errorCode, "PropertyNotPersisted", StringComparison.Ordinal) ? PersistenceProof.NotPersisted : PersistenceProof.Unknown;

        /// <summary>
        /// The proof of a read-back that did not confirm the property. Only a read that says the value is not there proves it is not in the
        /// object; a read that confirmed it, or no answer at all, leaves the state unknown.
        /// </summary>
        internal static PersistenceProof ProofOfUnconfirmedRead(bool? persistedVerified)
            => persistedVerified == false ? PersistenceProof.NotPersisted : PersistenceProof.Unknown;

        /// <summary>
        /// The proof when every requested value was read back as requested and only an unrelated part then changed: the values are in the object.
        /// </summary>
        internal static PersistenceProof ProofOfChangedParts() => PersistenceProof.Verified;

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
