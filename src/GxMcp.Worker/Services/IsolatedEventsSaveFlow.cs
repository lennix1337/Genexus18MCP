using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// The SDK operations the isolated Events save performs, in the order
    /// <see cref="IsolatedEventsSaveFlow"/> calls them. The production
    /// implementation lives beside <c>WriteService.WriteIsolatedEventsCore</c>;
    /// tests drive the flow with a synthetic implementation, which is the only
    /// way to exercise the failure paths without opening a Knowledge Base.
    /// </summary>
    internal interface IIsolatedEventsSaveSteps : IDisposable
    {
        /// <summary>Version token of the Events part as first read, before any transaction.</summary>
        string OriginalToken { get; }

        /// <summary>True once the SDK event suppression scope is poisoned and the Worker must restart.</summary>
        bool WorkerRestartRequired { get; }

        /// <summary>True when the post-commit verification proved the complete isolated persistence.</summary>
        bool Verified { get; }

        /// <summary>
        /// The one-line reason a save that ran without an exception was not confirmed, when the verification knows
        /// it (for example the target is verified and only other objects could not be cleared); null otherwise.
        /// </summary>
        string UnconfirmedMessage { get; }

        void ReadOriginal();
        void CaptureBaseline();
        void BeginTransaction();

        /// <summary>Fresh read inside the transaction; returns its Events version token.</summary>
        string ReadFreshInTransaction();

        void RecheckTarget();
        void AssignSource();
        void SavePart();
        void SaveObject();

        /// <summary>Always runs after <see cref="SavePart"/>/<see cref="SaveObject"/>, even when one throws.</summary>
        void EndSaveIsolation();

        /// <summary>Throws when the in-transaction object state differs from the requested change.</summary>
        void VerifyPreCommit();

        void Commit();
        void Rollback();
        void EndTransaction();
        void RereadAfterCommit();
        void VerifyAfterCommit(bool committed);

        /// <summary>
        /// The SDK objects (label, instance) whose <c>Messages</c> may explain a failure:
        /// the most recent instance of the object and its parts. Empty before any read.
        /// </summary>
        IEnumerable<KeyValuePair<string, object>> SdkMessageSources();
    }

    internal enum IsolatedEventsSaveOutcomeKind
    {
        VersionConflict,
        Verified,
        NotConfirmed,
        Interrupted
    }

    internal sealed class IsolatedEventsSaveOutcome
    {
        private IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind kind, string message, string currentVersion, string phase)
        {
            Kind = kind;
            Message = message;
            CurrentVersion = currentVersion;
            Phase = phase;
        }

        internal IsolatedEventsSaveOutcomeKind Kind { get; }

        /// <summary>The human message for every kind except <see cref="IsolatedEventsSaveOutcomeKind.Verified"/>.</summary>
        internal string Message { get; }

        internal string CurrentVersion { get; }

        /// <summary>The stage the save stopped at, or <see cref="IsolatedEventsSavePhase.Completed"/>.</summary>
        internal string Phase { get; }

        internal static IsolatedEventsSaveOutcome Conflict(string message, string currentVersion, string phase)
            => new IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind.VersionConflict, message, currentVersion, phase);

        internal static IsolatedEventsSaveOutcome Success()
            => new IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind.Verified, null, null, IsolatedEventsSavePhase.Completed);

        internal static IsolatedEventsSaveOutcome NotConfirmed(string message, string phase)
            => new IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind.NotConfirmed, message, null, phase);

        internal static IsolatedEventsSaveOutcome Interrupted(string message, string phase)
            => new IsolatedEventsSaveOutcome(IsolatedEventsSaveOutcomeKind.Interrupted, message, null, phase);
    }

    /// <summary>
    /// Control flow of the isolated Events save: conflict checks, one
    /// transaction, rollback on any failure, and the mandatory fresh read after
    /// the transaction closes. It holds no SDK type, so the failure handling is
    /// testable without a Knowledge Base. A failed save is never retried and no
    /// compensating write is issued here. Every stage is announced to the trace
    /// before it runs, so a failure always names where it happened.
    /// </summary>
    internal static class IsolatedEventsSaveFlow
    {
        internal const string NotConfirmedMessage =
            "Complete isolated object persistence could not be confirmed. Inspect the returned persistence state; do not retry blindly.";

        internal static IsolatedEventsSaveOutcome Run(
            IIsolatedEventsSaveSteps steps, IsolatedEventsSaveTrace trace, string baseVersion, Func<IDisposable> acquireWriteGate)
        {
            JObject receipt = trace.Receipt;
            bool committed = false;
            string failureMessage = null;
            try
            {
                using (acquireWriteGate())
                {
                    trace.Enter(IsolatedEventsSavePhase.FreshRead);
                    steps.ReadOriginal();
                    trace.Enter(IsolatedEventsSavePhase.VersionConflictCheck);
                    if (string.IsNullOrWhiteSpace(baseVersion) || steps.OriginalToken != baseVersion)
                        return IsolatedEventsSaveOutcome.Conflict(
                            "The Events version changed; no save was attempted.", steps.OriginalToken, trace.Phase);
                    trace.Enter(IsolatedEventsSavePhase.BaselineCapture);
                    steps.CaptureBaseline();
                    steps.BeginTransaction();
                    IsolatedEventsSaveOutcome boundaryConflict = null;
                    try
                    {
                        trace.Enter(IsolatedEventsSavePhase.TransactionFreshRead);
                        string currentToken = steps.ReadFreshInTransaction();
                        trace.Enter(IsolatedEventsSavePhase.VersionConflictCheck);
                        if (currentToken != steps.OriginalToken)
                        {
                            steps.Rollback();
                            boundaryConflict = IsolatedEventsSaveOutcome.Conflict(
                                "The Events version changed at the transaction boundary; no save was attempted.",
                                currentToken, trace.Phase);
                        }
                        else
                        {
                            trace.Enter(IsolatedEventsSavePhase.TargetRecheck);
                            steps.RecheckTarget();
                            trace.Enter(IsolatedEventsSavePhase.SourceAssignment);
                            steps.AssignSource();
                            try
                            {
                                trace.Enter(IsolatedEventsSavePhase.PartSave);
                                steps.SavePart();
                                trace.Enter(IsolatedEventsSavePhase.ObjectSave);
                                steps.SaveObject();
                            }
                            finally
                            {
                                steps.EndSaveIsolation();
                            }
                            trace.Enter(IsolatedEventsSavePhase.PreCommitVerification);
                            steps.VerifyPreCommit();
                            trace.Enter(IsolatedEventsSavePhase.Commit);
                            steps.Commit();
                            committed = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        // The SDK's own messages live on the in-memory instance and
                        // are read before the rollback touches it.
                        IsolatedEventsFailure failure = trace.RecordFailure(ex, steps.SdkMessageSources);
                        failureMessage = "Isolated Events save failed during " + failure.Phase + ": " + failure.Detail;
                        try { steps.Rollback(); receipt["transactionRolledBack"] = true; }
                        catch (Exception rollbackEx)
                        {
                            receipt["rollbackError"] = IsolatedEventsSaveDiagnostics.Sanitize(rollbackEx.Message);
                        }
                    }
                    if (boundaryConflict != null)
                    {
                        // The transaction closes inside the write gate, before the
                        // conflict is reported; a failing dispose is an interruption.
                        steps.EndTransaction();
                        return boundaryConflict;
                    }
                    // Mandatory fresh verification occurs after transaction disposal,
                    // while broker suppression remains in force. No compensating Save.
                    trace.Enter(IsolatedEventsSavePhase.PostCommitReread);
                    steps.EndTransaction();
                    steps.RereadAfterCommit();
                    trace.Enter(IsolatedEventsSavePhase.PostCommitVerification);
                    steps.VerifyAfterCommit(committed);
                    if (steps.Verified)
                    {
                        trace.Enter(IsolatedEventsSavePhase.Completed);
                        return IsolatedEventsSaveOutcome.Success();
                    }
                    return IsolatedEventsSaveOutcome.NotConfirmed(
                        failureMessage ?? steps.UnconfirmedMessage ?? NotConfirmedMessage, trace.FailedPhase ?? trace.Phase);
                }
            }
            catch (Exception ex)
            {
                receipt["objectSaved"] = committed;
                receipt["verificationCompleted"] = false;
                receipt["persistenceUncertain"] = receipt["writeAttempted"]?.Value<bool>() == true;
                receipt["partPersisted"] = JValue.CreateNull();
                receipt["workerRestartRequired"] = steps.WorkerRestartRequired;
                // The interruption's message replaces the save failure's in the envelope;
                // when a save failure came first, keep its message beside its diagnostics.
                if (failureMessage != null) receipt["saveFailureMessage"] = failureMessage;
                IsolatedEventsFailure failure = trace.RecordFailure(ex, steps.SdkMessageSources);
                return IsolatedEventsSaveOutcome.Interrupted(
                    "SDK persistence or verification was interrupted during " + failure.Phase + ": "
                        + failure.Detail.TrimEnd('.') + ". No compensating write was performed.",
                    trace.FailedPhase ?? trace.Phase);
            }
        }
    }
}
