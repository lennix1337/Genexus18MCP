#nullable enable

using System;

namespace GxMcp.Common.SharedHost
{
    internal enum RecordLiveness
    {
        Live,
        Stale,

        // The owner PID is running but its start time cannot be read. Treated as live (fail closed): the record
        // and every process are left unchanged (Requirement 7 criterion 8).
        LivenessUnverified
    }

    internal enum StaleReason
    {
        None,
        OwnerNotRunning,
        StartTimeMismatch,
        UnparseableRecord,
        OrphanedWorker
    }

    internal enum ProcessProbeStatus
    {
        NotRunning,
        Running,
        RunningStartTimeUnreadable
    }

    // Result of probing one PID. StartTimeUtcTicks is meaningful only when Status is Running.
    internal readonly struct ProcessProbeResult
    {
        private ProcessProbeResult(ProcessProbeStatus status, long startTimeUtcTicks)
        {
            Status = status;
            StartTimeUtcTicks = startTimeUtcTicks;
        }

        internal static ProcessProbeResult NotRunning => new ProcessProbeResult(ProcessProbeStatus.NotRunning, 0);

        internal static ProcessProbeResult RunningStartTimeUnreadable =>
            new ProcessProbeResult(ProcessProbeStatus.RunningStartTimeUnreadable, 0);

        internal static ProcessProbeResult Running(long startTimeUtcTicks) =>
            new ProcessProbeResult(ProcessProbeStatus.Running, startTimeUtcTicks);

        internal ProcessProbeStatus Status { get; }

        internal long StartTimeUtcTicks { get; }
    }

    internal interface IProcessProbe
    {
        ProcessProbeResult Probe(int pid);
    }

    // Record is the parsed record when one exists (also for a stale parsed record, so callers can log the removed
    // PID and start time), and null for absent, unparseable, or unreadable files.
    internal sealed class RecordClassification
    {
        internal RecordClassification(RecordLiveness liveness, StaleReason reason, SharedHostRecord? record)
        {
            Liveness = liveness;
            Reason = reason;
            Record = record;
        }

        internal RecordLiveness Liveness { get; }

        internal StaleReason Reason { get; }

        internal SharedHostRecord? Record { get; }
    }

    // Pure registry v2 liveness classification (Requirement 7 criteria 1 and 8). Liveness depends only on the
    // host owner's PID and start time; Worker fields are never consulted.
    internal static class SharedHostRecordClassifier
    {
        internal static RecordClassification Classify(RecordReadResult read, IProcessProbe probe)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            if (probe == null) throw new ArgumentNullException(nameof(probe));

            switch (read.Status)
            {
                case RecordReadStatus.Absent:
                    return new RecordClassification(RecordLiveness.Stale, StaleReason.OwnerNotRunning, null);
                case RecordReadStatus.Unparseable:
                    return new RecordClassification(RecordLiveness.Stale, StaleReason.UnparseableRecord, null);
                case RecordReadStatus.Parsed:
                    break;
                default:
                    // Unreadable (or any future status): fail closed, never stale.
                    return new RecordClassification(RecordLiveness.LivenessUnverified, StaleReason.None, null);
            }

            var record = read.Record;
            if (record == null || record.HostPid <= 0 || record.HostStartTimeUtcTicks <= 0)
            {
                return new RecordClassification(RecordLiveness.Stale, StaleReason.UnparseableRecord, record);
            }

            var result = probe.Probe(record.HostPid);
            switch (result.Status)
            {
                case ProcessProbeStatus.NotRunning:
                    return new RecordClassification(RecordLiveness.Stale, StaleReason.OwnerNotRunning, record);
                case ProcessProbeStatus.Running:
                    return SameMillisecond(record.HostStartTimeUtcTicks, result.StartTimeUtcTicks)
                        ? new RecordClassification(RecordLiveness.Live, StaleReason.None, record)
                        : new RecordClassification(RecordLiveness.Stale, StaleReason.StartTimeMismatch, record);
                default:
                    return new RecordClassification(RecordLiveness.LivenessUnverified, StaleReason.None, record);
            }
        }

        // Millisecond precision with floor division, so negative probe ticks cannot round toward zero into a match.
        internal static bool SameMillisecond(long recordedTicks, long probedTicks) =>
            FloorMilliseconds(recordedTicks) == FloorMilliseconds(probedTicks);

        private static long FloorMilliseconds(long ticks)
        {
            var ms = ticks / TimeSpan.TicksPerMillisecond;
            return ticks % TimeSpan.TicksPerMillisecond < 0 ? ms - 1 : ms;
        }
    }
}
