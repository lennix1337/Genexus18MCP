using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Unit tests for the pure Admission_Guard decision (design: SharedHostAdmissionPolicy).
    // Check order under test: machine-wide Worker count with eviction, then cold-start
    // concurrency with FIFO hold, then the Memory_Floor last.
    public class SharedHostAdmissionPolicyTests
    {
        private const string SelfKey = "kb-self";
        private static readonly DateTime T0 = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Decide_UnderAllLimits_ReturnsStart()
        {
            var input = Input(
                hosts: new[] { Self(), Ready("kb-a", attachments: 1, lastActivity: T0) },
                maxWorkers: 4, coldStartConcurrency: 1, memoryFloorMb: 1024, availableMb: 4096);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Start, decision.Kind);
            Assert.Null(decision.Position);
            Assert.Null(decision.VictimKey);
            Assert.Null(decision.Code);
            Assert.Null(decision.Reason);
        }

        [Fact]
        public void Decide_AtWorkerLimit_EvictsZeroAttachmentHostBeforeHostWithAttachments()
        {
            // kb-a has Attachments and the older activity; kb-b has none and must still be chosen first.
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Ready("kb-a", attachments: 2, lastActivity: T0, evictable: true),
                    Ready("kb-b", attachments: 0, lastActivity: T0.AddMinutes(10), evictable: true),
                },
                maxWorkers: 2);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Evict, decision.Kind);
            Assert.Equal("kb-b", decision.VictimKey);
        }

        [Fact]
        public void Decide_AtWorkerLimit_EvictsHostWithAttachmentsWhenNoZeroAttachmentHostIsEvictable()
        {
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Ready("kb-a", attachments: 0, lastActivity: T0, evictable: false),
                    Ready("kb-b", attachments: 3, lastActivity: T0.AddMinutes(5), evictable: true),
                },
                maxWorkers: 2);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Evict, decision.Kind);
            Assert.Equal("kb-b", decision.VictimKey);
        }

        [Fact]
        public void Decide_AtWorkerLimit_AmongEqualAttachmentPreference_EvictsOldestLastActivity()
        {
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Ready("kb-a", attachments: 0, lastActivity: T0.AddMinutes(30), evictable: true),
                    Ready("kb-b", attachments: 0, lastActivity: T0.AddMinutes(1), evictable: true),
                    Ready("kb-c", attachments: 0, lastActivity: T0.AddMinutes(20), evictable: true),
                },
                maxWorkers: 3);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Evict, decision.Kind);
            Assert.Equal("kb-b", decision.VictimKey);
        }

        [Fact]
        public void Decide_AtWorkerLimit_EqualLastActivity_BreaksTieByKey()
        {
            // Listed out of key order so the tie-break cannot come from input order.
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Ready("kb-d", attachments: 1, lastActivity: T0, evictable: true),
                    Ready("kb-b", attachments: 1, lastActivity: T0, evictable: true),
                    Ready("kb-c", attachments: 1, lastActivity: T0, evictable: true),
                },
                maxWorkers: 3);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Evict, decision.Kind);
            Assert.Equal("kb-b", decision.VictimKey);
        }

        [Fact]
        public void Decide_AtWorkerLimit_WithNoEvictableHost_RefusesWorkerLimit()
        {
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Ready("kb-a", attachments: 2, lastActivity: T0),
                    Ready("kb-b", attachments: 0, lastActivity: T0.AddMinutes(3)),
                },
                maxWorkers: 2);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Refuse, decision.Kind);
            Assert.Equal("KB_POOL_FULL", decision.Code);
            Assert.Equal("worker-limit", decision.Reason);
            Assert.Null(decision.VictimKey);
            Assert.NotNull(decision.Details);
            Assert.Equal(2, (int)decision.Details!["maxWorkers"]!);

            var openKbs = (JArray)decision.Details["openKbs"]!;
            var byKey = openKbs.Cast<JObject>().ToDictionary(o => (string)o["key"]!);
            Assert.Equal(new[] { "kb-a", "kb-b" }, byKey.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(2, (int)byKey["kb-a"]["sessions"]!);
            Assert.Equal(0, (int)byKey["kb-b"]["sessions"]!);
            Assert.Equal(T0, ((DateTime)byKey["kb-a"]["lastActivityUtc"]!).ToUniversalTime());
        }

        [Fact]
        public void Decide_AtColdStartConcurrency_HoldsEvenWhenTheWorkerLimitIsReached()
        {
            // Requirement 6 criteria 4 and 5: the only counted Worker is a Cold_Start (never evictable); the start is
            // held, and the Worker limit is applied again when the hold is released.
            var input = Input(
                hosts: new[] { Self(), Starting("kb-a") },
                maxWorkers: 1, coldStartConcurrency: 1);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Hold, decision.Kind);
            Assert.Equal(1, decision.Position);
        }

        [Fact]
        public void Decide_AtColdStartConcurrency_HoldsWithOneBasedFifoPosition()
        {
            // One Cold_Start in progress fills concurrency 1; two starts are already held ahead of self.
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Starting("kb-a"),
                    Held("kb-c", heldSince: T0.AddSeconds(5)),
                    Held("kb-b", heldSince: T0.AddSeconds(5)),
                },
                maxWorkers: 4, coldStartConcurrency: 1);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Hold, decision.Kind);
            Assert.Equal(3, decision.Position);
        }

        [Fact]
        public void Decide_AtColdStartConcurrency_SelfAlreadyHeld_PositionFollowsHeldSinceThenKey()
        {
            var input = Input(
                hosts: new[]
                {
                    Self(state: "held", heldSince: T0.AddSeconds(2)),
                    Starting("kb-x"),
                    Starting("kb-y"),
                    Held("kb-a", heldSince: T0.AddSeconds(2)),
                    Held("kb-z", heldSince: T0.AddSeconds(1)),
                    Held("kb-zz", heldSince: T0.AddSeconds(9)),
                },
                maxWorkers: 8, coldStartConcurrency: 2);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            // Order: kb-z (1 s), kb-a (2 s, key "kb-a" < "kb-self"), kb-self (2 s), kb-zz (9 s).
            Assert.Equal(AdmissionDecisionKind.Hold, decision.Kind);
            Assert.Equal(3, decision.Position);
        }

        [Fact]
        public void Decide_BelowColdStartConcurrency_Starts()
        {
            var input = Input(
                hosts: new[] { Self(), Starting("kb-a") },
                maxWorkers: 4, coldStartConcurrency: 2);

            Assert.Equal(AdmissionDecisionKind.Start, SharedHostAdmissionPolicy.Decide(input).Kind);
        }

        [Fact]
        public void Decide_MemoryBelowFloor_RefusesLowMemoryWithMeasuredAndFloorValues()
        {
            var input = Input(
                hosts: new[] { Self(), Ready("kb-a", attachments: 1, lastActivity: T0) },
                memoryFloorMb: 1024, availableMb: 900);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Refuse, decision.Kind);
            Assert.Equal("KB_POOL_FULL", decision.Code);
            Assert.Equal("low-memory", decision.Reason);
            Assert.NotNull(decision.Details);
            Assert.Equal(900L, (long)decision.Details!["availableMb"]!);
            Assert.Equal(1024L, (long)decision.Details["floorMb"]!);
            Assert.Null(decision.VictimKey);
        }

        [Fact]
        public void Decide_MemoryOneBelowFloor_Refuses()
        {
            var input = Input(hosts: new[] { Self() }, memoryFloorMb: 1024, availableMb: 1023);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Refuse, decision.Kind);
            Assert.Equal("low-memory", decision.Reason);
        }

        [Fact]
        public void Decide_MemoryEqualToFloor_Passes()
        {
            var input = Input(hosts: new[] { Self() }, memoryFloorMb: 1024, availableMb: 1024);

            Assert.Equal(AdmissionDecisionKind.Start, SharedHostAdmissionPolicy.Decide(input).Kind);
        }

        [Fact]
        public void Decide_MemoryUnavailable_PassesTheFloor()
        {
            var input = Input(hosts: new[] { Self() }, memoryFloorMb: 8192, availableMb: null);

            Assert.Equal(AdmissionDecisionKind.Start, SharedHostAdmissionPolicy.Decide(input).Kind);
        }

        [Fact]
        public void Decide_MemoryUnavailable_StillEnforcesWorkerLimit()
        {
            var input = Input(
                hosts: new[] { Self(), Ready("kb-a", attachments: 1, lastActivity: T0) },
                maxWorkers: 1, availableMb: null);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Refuse, decision.Kind);
            Assert.Equal("worker-limit", decision.Reason);
        }

        [Fact]
        public void Decide_MemoryUnavailable_StillEnforcesColdStartConcurrency()
        {
            var input = Input(
                hosts: new[] { Self(), Starting("kb-a") },
                coldStartConcurrency: 1, availableMb: null);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Hold, decision.Kind);
            Assert.Equal(1, decision.Position);
        }

        [Fact]
        public void Decide_WorkerLimitAndLowMemory_ReportsWorkerLimitBecauseMemoryIsCheckedLast()
        {
            var input = Input(
                hosts: new[] { Self(), Ready("kb-a", attachments: 1, lastActivity: T0) },
                maxWorkers: 1, memoryFloorMb: 1024, availableMb: 100);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Refuse, decision.Kind);
            Assert.Equal("worker-limit", decision.Reason);
        }

        [Fact]
        public void Decide_ColdStartConcurrencyAndLowMemory_HoldsBecauseMemoryIsCheckedLast()
        {
            var input = Input(
                hosts: new[] { Self(), Starting("kb-a") },
                coldStartConcurrency: 1, memoryFloorMb: 1024, availableMb: 100);

            Assert.Equal(AdmissionDecisionKind.Hold, SharedHostAdmissionPolicy.Decide(input).Kind);
        }

        [Theory]
        [InlineData("Stale")]
        [InlineData("LivenessUnverified")]
        public void Decide_NonLiveWorkerRecords_AreNotCountedTowardTheWorkerLimit(string livenessName)
        {
            // RecordLiveness is internal; a public theory cannot take it as a parameter (CS0051).
            var liveness = Enum.Parse<RecordLiveness>(livenessName);
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Ready("kb-a", attachments: 1, lastActivity: T0, liveness: liveness),
                    Ready("kb-b", attachments: 0, lastActivity: T0, liveness: liveness),
                },
                maxWorkers: 1);

            Assert.Equal(AdmissionDecisionKind.Start, SharedHostAdmissionPolicy.Decide(input).Kind);
        }

        [Theory]
        [InlineData("Stale")]
        [InlineData("LivenessUnverified")]
        public void Decide_NonLiveStartingRecords_AreNotCountedAsColdStarts(string livenessName)
        {
            var liveness = Enum.Parse<RecordLiveness>(livenessName);
            var input = Input(
                hosts: new[] { Self(), Starting("kb-a", liveness: liveness) },
                coldStartConcurrency: 1);

            Assert.Equal(AdmissionDecisionKind.Start, SharedHostAdmissionPolicy.Decide(input).Kind);
        }

        [Fact]
        public void Decide_NonLiveEvictableRecord_IsNeverChosenAsVictim()
        {
            // The stale record has zero Attachments and the oldest activity, so it would win if it counted.
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Ready("kb-stale", attachments: 0, lastActivity: T0, evictable: true, liveness: RecordLiveness.Stale),
                    Ready("kb-a", attachments: 2, lastActivity: T0.AddMinutes(5), evictable: true),
                },
                maxWorkers: 1);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Evict, decision.Kind);
            Assert.Equal("kb-a", decision.VictimKey);
        }

        [Fact]
        public void Decide_NonLiveHeldRecords_DoNotTakeAHoldPosition()
        {
            var input = Input(
                hosts: new[]
                {
                    Self(),
                    Starting("kb-a"),
                    Held("kb-b", heldSince: T0, liveness: RecordLiveness.Stale),
                    Held("kb-c", heldSince: T0.AddSeconds(1)),
                },
                coldStartConcurrency: 1);

            var decision = SharedHostAdmissionPolicy.Decide(input);

            Assert.Equal(AdmissionDecisionKind.Hold, decision.Kind);
            Assert.Equal(2, decision.Position);
        }

        [Fact]
        public void Decide_RecordsWithoutWorkerPid_AreNotCountedTowardTheWorkerLimit()
        {
            var noWorker = Ready("kb-a", attachments: 1, lastActivity: T0);
            noWorker.State = "no-worker";
            noWorker.WorkerPid = 0;

            var input = Input(hosts: new[] { Self(), noWorker }, maxWorkers: 1);

            Assert.Equal(AdmissionDecisionKind.Start, SharedHostAdmissionPolicy.Decide(input).Kind);
        }

        private static AdmissionInput Input(
            IEnumerable<AdmissionHostView> hosts,
            int maxWorkers = 4,
            int coldStartConcurrency = 1,
            long memoryFloorMb = 1024,
            long? availableMb = 4096)
        {
            return new AdmissionInput
            {
                SelfKey = SelfKey,
                Hosts = hosts.ToList(),
                MaxWorkers = maxWorkers,
                MaxOpenKbsExceeded = false,
                ColdStartConcurrency = coldStartConcurrency,
                MemoryFloorMb = memoryFloorMb,
                AvailableMb = availableMb,
            };
        }

        private static AdmissionHostView Self(string state = "admitting", DateTime? heldSince = null)
        {
            return new AdmissionHostView
            {
                Key = SelfKey,
                State = state,
                WorkerPid = 0,
                Evictable = false,
                Attachments = 1,
                LastActivityUtc = null,
                HeldSinceUtc = heldSince,
                Liveness = RecordLiveness.Live,
            };
        }

        private static AdmissionHostView Ready(
            string key,
            int attachments,
            DateTime lastActivity,
            bool evictable = false,
            RecordLiveness liveness = RecordLiveness.Live)
        {
            return new AdmissionHostView
            {
                Key = key,
                State = "ready",
                WorkerPid = 5000 + Math.Abs(key.GetHashCode() % 1000),
                Evictable = evictable,
                Attachments = attachments,
                LastActivityUtc = lastActivity,
                HeldSinceUtc = null,
                Liveness = liveness,
            };
        }

        private static AdmissionHostView Starting(string key, RecordLiveness liveness = RecordLiveness.Live)
        {
            return new AdmissionHostView
            {
                Key = key,
                State = "starting",
                WorkerPid = 6000 + Math.Abs(key.GetHashCode() % 1000),
                Evictable = false,
                Attachments = 1,
                LastActivityUtc = null,
                HeldSinceUtc = null,
                Liveness = liveness,
            };
        }

        private static AdmissionHostView Held(string key, DateTime heldSince, RecordLiveness liveness = RecordLiveness.Live)
        {
            return new AdmissionHostView
            {
                Key = key,
                State = "held",
                WorkerPid = 0,
                Evictable = false,
                Attachments = 1,
                LastActivityUtc = null,
                HeldSinceUtc = heldSince,
                Liveness = liveness,
            };
        }
    }
}
