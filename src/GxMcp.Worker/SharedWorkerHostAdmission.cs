using System;
using System.Collections.Generic;
using System.Threading;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker
{
    // The I/O wrapper around SharedHostAdmissionPolicy (RFC #854, design "SharedWorkerHostAdmission.cs").
    // The contract is pinned in the header of src/GxMcp.Worker.Tests/SharedWorkerHostAdmissionTests.cs.
    // This file is SDK-free and is linked into GxMcp.SharedHost.TestHost.

    internal enum SharedHostAdmissionOutcome
    {
        Start,
        Hold,
        Evict,
        Refuse,

        // The admission mutex is held by another host; nothing was read or written. Retry after AdmissionRetryMs.
        Contended
    }

    internal sealed class SharedHostAdmissionResult
    {
        internal SharedHostAdmissionResult(SharedHostAdmissionOutcome outcome, AdmissionDecision decision, long? availableMb)
        {
            Outcome = outcome;
            Decision = decision;
            AvailableMb = availableMb;
        }

        internal SharedHostAdmissionOutcome Outcome { get; }

        /// <summary>Null for <see cref="SharedHostAdmissionOutcome.Contended"/>.</summary>
        internal AdmissionDecision Decision { get; }

        /// <summary>Null when the memory was not read or could not be read.</summary>
        internal long? AvailableMb { get; }
    }

    /// <summary>
    /// One admission attempt per <see cref="Admit"/> call under the machine-wide admission mutex: enumerate and
    /// classify the registry v2 records, read Available_Physical_Memory last, decide, and write the host's own
    /// record before the mutex is released. Not thread-safe; the runtime calls it under its own lock.
    /// </summary>
    internal sealed class SharedWorkerHostAdmission
    {
        internal const string AdmissionMutexName = @"Local\GxMcp.SharedHost.v2.Admission";
        internal const string WakeEventPrefix = @"Local\GxMcp.SharedHost.v2.Wake.";
        internal const string EvictEventPrefix = @"Local\GxMcp.SharedHost.v2.Evict.";

        /// <summary>A held or evicting host re-runs admission at this interval, which detects a crashed Cold_Start owner.</summary>
        internal const int HeldPollMs = 1000;

        /// <summary>Retry interval after a <see cref="SharedHostAdmissionOutcome.Contended"/> attempt.</summary>
        internal const int AdmissionRetryMs = 50;

        // EndColdStart must not lose the wake signal to a briefly contended mutex: it retries for at most
        // EndColdStartAttempts * AdmissionRetryMs and then writes and signals without the mutex. The woken host
        // re-runs the full admission under the mutex, so the signal itself needs no exclusion.
        private const int EndColdStartAttempts = 20;

        private readonly SharedHostSettingsResult _settings;
        private readonly ISharedHostClock _clock;
        private readonly ISharedHostMutexFactory _mutexFactory;
        private readonly ISharedHostRecordWriter _recordWriter;
        private readonly SharedHostAdmissionSeams _seams;

        internal SharedWorkerHostAdmission(
            SharedHostSettingsResult settings,
            ISharedHostClock clock,
            ISharedHostMutexFactory mutexFactory,
            ISharedHostRecordWriter recordWriter,
            SharedHostAdmissionSeams seams)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _mutexFactory = mutexFactory ?? throw new ArgumentNullException(nameof(mutexFactory));
            _recordWriter = recordWriter ?? throw new ArgumentNullException(nameof(recordWriter));
            _seams = seams ?? throw new ArgumentNullException(nameof(seams));
            if (seams.Registry == null) throw new ArgumentException("The admission seams need a registry enumerator.", nameof(seams));
            if (seams.HostProbe == null) throw new ArgumentException("The admission seams need a host probe.", nameof(seams));
            if (seams.Memory == null) throw new ArgumentException("The admission seams need a memory reader.", nameof(seams));
            if (seams.Signals == null) throw new ArgumentException("The admission seams need a signal hub.", nameof(seams));
        }

        internal static string WakeEventName(string kbKey) => WakeEventPrefix + kbKey;

        internal static string EvictEventName(string kbKey) => EvictEventPrefix + kbKey;

        /// <summary>
        /// Runs one admission attempt for <paramref name="self"/> and updates and writes it in place according to
        /// the decision. Contended when the admission mutex is held elsewhere, with no read and no write.
        /// </summary>
        internal SharedHostAdmissionResult Admit(SharedHostRecord self, bool maxOpenKbsExceeded = false)
        {
            if (self == null) throw new ArgumentNullException(nameof(self));

            IDisposable mutex = _mutexFactory.TryAcquire(AdmissionMutexName);
            if (mutex == null) return new SharedHostAdmissionResult(SharedHostAdmissionOutcome.Contended, null, null);

            try
            {
                var hosts = ReadOtherHosts(self.KbKey);
                hosts.Add(ToView(self, RecordLiveness.Live));

                // Memory is read last: after every enumeration and probe of this attempt, right before the
                // decision. The reading belongs to this attempt only.
                long? availableMb = null;
                if (_seams.Memory.TryReadAvailableMb(out long readMb, out string readError)) availableMb = readMb;
                else Warn("Available physical memory could not be read; the memory floor check passes for this start only: " +
                    (string.IsNullOrEmpty(readError) ? "unknown error" : readError));

                var decision = SharedHostAdmissionPolicy.Decide(new AdmissionInput
                {
                    SelfKey = self.KbKey,
                    Hosts = hosts,
                    MaxWorkers = _settings.MaxWorkers,
                    MaxOpenKbsExceeded = maxOpenKbsExceeded,
                    ColdStartConcurrency = _settings.ColdStartConcurrency,
                    MemoryFloorMb = _settings.MemoryFloorMb,
                    AvailableMb = availableMb
                });

                var outcome = Apply(self, decision);
                _recordWriter.Write(self);
                if (outcome == SharedHostAdmissionOutcome.Evict) _seams.Signals.Signal(EvictEventName(decision.VictimKey));
                return new SharedHostAdmissionResult(outcome, decision, availableMb);
            }
            finally
            {
                mutex.Dispose();
            }
        }

        /// <summary>
        /// Called after this host's Cold_Start ended, with <paramref name="self"/> already in its new state. Under
        /// the admission mutex: writes <paramref name="self"/>, then wakes the FIFO head of the live held records.
        /// </summary>
        internal void EndColdStart(SharedHostRecord self)
        {
            if (self == null) throw new ArgumentNullException(nameof(self));

            IDisposable mutex = AcquireForColdStartEnd();
            try
            {
                self.UpdatedUtc = DateTime.UtcNow;
                _recordWriter.Write(self);
                string head = FindHeldHead(self.KbKey);
                if (head != null) _seams.Signals.Signal(WakeEventName(head));
            }
            finally
            {
                mutex?.Dispose();
            }
        }

        private IDisposable AcquireForColdStartEnd()
        {
            for (int attempt = 0; attempt < EndColdStartAttempts; attempt++)
            {
                IDisposable mutex = _mutexFactory.TryAcquire(AdmissionMutexName);
                if (mutex != null) return mutex;
                Thread.Sleep(AdmissionRetryMs);
            }
            Warn("The admission mutex stayed held while a Cold_Start ended; the wake signal is sent without it.");
            return null;
        }

        private SharedHostAdmissionOutcome Apply(SharedHostRecord self, AdmissionDecision decision)
        {
            DateTime now = DateTime.UtcNow;
            self.UpdatedUtc = now;
            switch (decision.Kind)
            {
                case AdmissionDecisionKind.Start:
                    self.State = SharedHostRecordState.Starting;
                    self.ColdStartStartedUtc = now;
                    self.ColdStartConcurrency = _settings.ColdStartConcurrency;
                    self.HeldSinceUtc = null;
                    self.Refusal = null;
                    return SharedHostAdmissionOutcome.Start;

                case AdmissionDecisionKind.Hold:
                    // The FIFO position comes from the first hold of this start; the runtime clears HeldSinceUtc
                    // when the start ends or is dropped.
                    self.State = SharedHostRecordState.Held;
                    if (!self.HeldSinceUtc.HasValue) self.HeldSinceUtc = now;
                    self.Refusal = null;
                    return SharedHostAdmissionOutcome.Hold;

                case AdmissionDecisionKind.Evict:
                    // HeldSinceUtc is kept so a later hold of the same start keeps its FIFO position.
                    self.State = SharedHostRecordState.Admitting;
                    self.Refusal = null;
                    return SharedHostAdmissionOutcome.Evict;

                default:
                    self.State = SharedHostRecordState.Refused;
                    self.HeldSinceUtc = null;
                    self.Refusal = new SharedHostRefusal
                    {
                        Code = decision.Code,
                        Reason = decision.Reason,
                        AvailableMb = ReadLong(decision.Details, "availableMb"),
                        FloorMb = ReadLong(decision.Details, "floorMb")
                    };
                    return SharedHostAdmissionOutcome.Refuse;
            }
        }

        // Every other host's record as the policy sees it. This host's own record is dropped without a probe;
        // the caller adds it from memory, which is newer than any file.
        private List<AdmissionHostView> ReadOtherHosts(string selfKey)
        {
            var views = new List<AdmissionHostView>();
            foreach (var read in _seams.Registry.ReadAll() ?? Array.Empty<RecordReadResult>())
            {
                if (read == null) continue;
                if (read.Record != null && string.Equals(read.Record.KbKey, selfKey, StringComparison.Ordinal)) continue;

                var classification = SharedHostRecordClassifier.Classify(read, _seams.HostProbe);
                // Absent, unparseable and unreadable files carry no record and so no key; the policy would count
                // none of them, because only Live records take a slot.
                if (classification.Record == null) continue;
                views.Add(ToView(classification.Record, classification.Liveness));
            }
            return views;
        }

        private string FindHeldHead(string selfKey)
        {
            SharedHostRecord head = null;
            foreach (var read in _seams.Registry.ReadAll() ?? Array.Empty<RecordReadResult>())
            {
                var record = read?.Record;
                if (record == null) continue;
                if (string.Equals(record.KbKey, selfKey, StringComparison.Ordinal)) continue;
                if (!string.Equals(record.State, SharedHostRecordState.Held, StringComparison.Ordinal)) continue;
                if (SharedHostRecordClassifier.Classify(read, _seams.HostProbe).Liveness != RecordLiveness.Live) continue;
                if (head == null || IsAhead(record, head)) head = record;
            }
            return head?.KbKey;
        }

        // Oldest heldSinceUtc first (a held record without one queues last), then the ordinal kbKey.
        private static bool IsAhead(SharedHostRecord a, SharedHostRecord b)
        {
            DateTime ta = a.HeldSinceUtc ?? DateTime.MaxValue;
            DateTime tb = b.HeldSinceUtc ?? DateTime.MaxValue;
            if (ta != tb) return ta < tb;
            return string.CompareOrdinal(a.KbKey, b.KbKey) < 0;
        }

        private static AdmissionHostView ToView(SharedHostRecord record, RecordLiveness liveness) => new AdmissionHostView
        {
            Key = record.KbKey ?? string.Empty,
            State = record.State ?? string.Empty,
            WorkerPid = record.WorkerPid,
            Evictable = record.Evictable,
            Attachments = record.Attachments,
            LastActivityUtc = record.LastActivityUtc,
            HeldSinceUtc = record.HeldSinceUtc,
            Liveness = liveness
        };

        private static long? ReadLong(JObject details, string name)
        {
            var token = details?[name];
            if (token == null || token.Type != JTokenType.Integer) return null;
            return token.Value<long>();
        }

        private void Warn(string message)
        {
            try { _seams.Warn?.Invoke(message); } catch { }
        }
    }
}
