#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Common.SharedHost
{
    // One registry v2 record as the Admission_Guard sees it. Liveness comes from SharedHostRecordClassifier; the
    // policy counts only Live records, so a Stale or LivenessUnverified record never takes a Worker slot, a Cold_Start
    // slot, a hold position, or the victim role.
    internal sealed class AdmissionHostView
    {
        public string Key { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int WorkerPid { get; set; }
        public bool Evictable { get; set; }
        public int Attachments { get; set; }
        public DateTime? LastActivityUtc { get; set; }
        public DateTime? HeldSinceUtc { get; set; }
        public RecordLiveness Liveness { get; set; } = RecordLiveness.Live;
    }

    // Everything one admission decision depends on. Hosts includes the deciding host itself (SelfKey). AvailableMb is
    // null when Available_Physical_Memory could not be read; the floor check then passes for this start only
    // (Requirement 6 criterion 10). Range validation of the limits belongs to the settings resolver.
    internal sealed class AdmissionInput
    {
        public string SelfKey { get; set; } = string.Empty;
        public IReadOnlyList<AdmissionHostView> Hosts { get; set; } = Array.Empty<AdmissionHostView>();
        public int MaxWorkers { get; set; }

        // Result of the Gateway WorkerPool check of Server.MaxOpenKbs, taken after the pool's own eviction.
        public bool MaxOpenKbsExceeded { get; set; }

        public int ColdStartConcurrency { get; set; }
        public long MemoryFloorMb { get; set; }
        public long? AvailableMb { get; set; }
    }

    internal enum AdmissionDecisionKind
    {
        Start,
        Hold,
        Evict,
        Refuse
    }

    // Start | Hold(position) | Evict(victimKey) | Refuse(code, reason, details). Fields that do not belong to the
    // kind are null.
    internal sealed class AdmissionDecision
    {
        internal const string PoolFullCode = "KB_POOL_FULL";
        internal const string WorkerLimitReason = "worker-limit";
        internal const string MaxOpenKbsReason = "max-open-kbs";
        internal const string LowMemoryReason = "low-memory";

        private AdmissionDecision(AdmissionDecisionKind kind, int? position, string? victimKey, string? code, string? reason, JObject? details)
        {
            Kind = kind;
            Position = position;
            VictimKey = victimKey;
            Code = code;
            Reason = reason;
            Details = details;
        }

        internal AdmissionDecisionKind Kind { get; }

        // 1-based FIFO position of the deciding host among held starts; set only for Hold.
        internal int? Position { get; }

        internal string? VictimKey { get; }
        internal string? Code { get; }
        internal string? Reason { get; }
        internal JObject? Details { get; }

        internal static AdmissionDecision Start() => new AdmissionDecision(AdmissionDecisionKind.Start, null, null, null, null, null);

        internal static AdmissionDecision Hold(int position)
        {
            if (position < 1) throw new ArgumentOutOfRangeException(nameof(position), position, "Hold position is 1-based.");
            return new AdmissionDecision(AdmissionDecisionKind.Hold, position, null, null, null, null);
        }

        internal static AdmissionDecision Evict(string victimKey)
        {
            if (string.IsNullOrEmpty(victimKey)) throw new ArgumentException("Victim key is required.", nameof(victimKey));
            return new AdmissionDecision(AdmissionDecisionKind.Evict, null, victimKey, null, null, null);
        }

        internal static AdmissionDecision Refuse(string code, string reason, JObject details)
        {
            if (string.IsNullOrEmpty(code)) throw new ArgumentException("Code is required.", nameof(code));
            if (string.IsNullOrEmpty(reason)) throw new ArgumentException("Reason is required.", nameof(reason));
            if (details == null) throw new ArgumentNullException(nameof(details));
            return new AdmissionDecision(AdmissionDecisionKind.Refuse, null, null, code, reason, details);
        }
    }

    // The Admission_Guard decision, pure (Requirements 5 and 6). Check order:
    //   0. Server.MaxOpenKbs result from the Gateway (see below);
    //   1. cold-start concurrency, with a FIFO hold by heldSinceUtc, then ordinal key (Requirement 6 criterion 4);
    //   2. machine-wide Worker count against MaxWorkers, with eviction when the limit is reached;
    //   3. Memory_Floor last.
    // The caller re-runs Decide for a released held start, so every limit is re-applied at release (Requirement 6
    // criterion 5).
    internal static class SharedHostAdmissionPolicy
    {
        private const string StartingState = "starting";
        private const string HeldState = "held";

        internal static AdmissionDecision Decide(AdmissionInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var hosts = input.Hosts ?? Array.Empty<AdmissionHostView>();

            // Server.MaxOpenKbs is a per-Gateway limit. The design leaves its place in the check order open; it is
            // checked first and refused without eviction because the Gateway WorkerPool already ran its own eviction
            // (detach with stopIfLast) before reporting the limit as exceeded, and stopping a machine-wide victim
            // cannot lower this Gateway's count. Refusing here starts and stops nothing (Requirement 5 criterion 3).
            // The WorkerPool adds the configured value and its per-Gateway open KBs to the error it builds.
            if (input.MaxOpenKbsExceeded)
            {
                return AdmissionDecision.Refuse(
                    AdmissionDecision.PoolFullCode,
                    AdmissionDecision.MaxOpenKbsReason,
                    new JObject { ["limit"] = "Server.MaxOpenKbs" });
            }

            var counted = new List<AdmissionHostView>();
            var coldStarts = 0;
            AdmissionHostView? self = null;
            foreach (var host in hosts)
            {
                if (host == null) continue;
                if (self == null && string.Equals(host.Key, input.SelfKey, StringComparison.Ordinal)) self = host;
                if (host.Liveness != RecordLiveness.Live) continue;
                if (host.WorkerPid > 0) counted.Add(host);
                if (string.Equals(host.State, StartingState, StringComparison.Ordinal)) coldStarts++;
            }

            // Requirement 6 criterion 4: a start requested while the Cold_Starts in progress fill the concurrency is
            // held, whatever the Worker count. A Cold_Start owner is never evictable, so deciding the Worker limit
            // first would refuse a start that could pass once that Cold_Start ends; criterion 5 re-applies the Worker
            // limit and the Memory_Floor when the hold is released.
            if (coldStarts >= input.ColdStartConcurrency)
            {
                return AdmissionDecision.Hold(HoldPosition(hosts, input.SelfKey, self));
            }

            if (counted.Count >= input.MaxWorkers)
            {
                var victim = ChooseVictim(counted);
                return victim != null ? AdmissionDecision.Evict(victim.Key) : RefuseWorkerLimit(input.MaxWorkers, counted);
            }

            if (input.AvailableMb.HasValue && input.AvailableMb.Value < input.MemoryFloorMb)
            {
                return AdmissionDecision.Refuse(
                    AdmissionDecision.PoolFullCode,
                    AdmissionDecision.LowMemoryReason,
                    new JObject
                    {
                        ["availableMb"] = input.AvailableMb.Value,
                        ["floorMb"] = input.MemoryFloorMb,
                    });
            }

            return AdmissionDecision.Start();
        }

        // Decision D8: evictable hosts with zero Attachments before any with Attachments; then the oldest
        // lastActivityUtc (a Worker that never completed a request counts as oldest); then the ordinal key.
        private static AdmissionHostView? ChooseVictim(List<AdmissionHostView> counted)
        {
            AdmissionHostView? best = null;
            foreach (var host in counted)
            {
                if (!host.Evictable) continue;
                if (best == null || IsPreferredVictim(host, best)) best = host;
            }
            return best;
        }

        private static bool IsPreferredVictim(AdmissionHostView a, AdmissionHostView b)
        {
            var aZero = a.Attachments == 0;
            var bZero = b.Attachments == 0;
            if (aZero != bZero) return aZero;

            var ta = a.LastActivityUtc ?? DateTime.MinValue;
            var tb = b.LastActivityUtc ?? DateTime.MinValue;
            if (ta != tb) return ta < tb;
            return string.CompareOrdinal(a.Key, b.Key) < 0;
        }

        // 1-based position among live held starts ordered by heldSinceUtc, then ordinal key. A deciding host that is
        // not held yet (or has no heldSinceUtc) joins the tail.
        private static int HoldPosition(IReadOnlyList<AdmissionHostView> hosts, string selfKey, AdmissionHostView? self)
        {
            var selfHeldSince = self != null
                && string.Equals(self.State, HeldState, StringComparison.Ordinal)
                && self.HeldSinceUtc.HasValue
                    ? self.HeldSinceUtc.Value
                    : DateTime.MaxValue;

            var ahead = 0;
            foreach (var host in hosts)
            {
                if (host == null || host.Liveness != RecordLiveness.Live) continue;
                if (string.Equals(host.Key, selfKey, StringComparison.Ordinal)) continue;
                if (!string.Equals(host.State, HeldState, StringComparison.Ordinal) || !host.HeldSinceUtc.HasValue) continue;

                var since = host.HeldSinceUtc.Value;
                if (since < selfHeldSince || (since == selfHeldSince && string.CompareOrdinal(host.Key, selfKey) < 0)) ahead++;
            }
            return ahead + 1;
        }

        private static AdmissionDecision RefuseWorkerLimit(int maxWorkers, List<AdmissionHostView> counted)
        {
            var openKbs = new JArray();
            foreach (var host in counted)
            {
                openKbs.Add(new JObject
                {
                    ["key"] = host.Key,
                    ["sessions"] = host.Attachments,
                    ["lastActivityUtc"] = host.LastActivityUtc.HasValue ? new JValue(host.LastActivityUtc.Value) : JValue.CreateNull(),
                });
            }

            return AdmissionDecision.Refuse(
                AdmissionDecision.PoolFullCode,
                AdmissionDecision.WorkerLimitReason,
                new JObject
                {
                    ["maxWorkers"] = maxWorkers,
                    ["openKbs"] = openKbs,
                });
        }
    }
}
