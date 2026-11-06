using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 7: Admission decision soundness
    //
    // For any set of registry records with any mix of live, dead, and start-time-mismatched owners, any limits within
    // their allowed ranges, and any Available_Physical_Memory value (including unavailable), the decision of
    // SharedHostAdmissionPolicy.Decide matches an independent reference decision and satisfies the soundness
    // invariants of Property 7. Removing every record whose owner is not verified live never changes the decision.
    //
    // **Validates: Requirements 5.1, 5.3, 5.5, 5.7, 6.2, 6.3, 6.10, 6.12**
    public class SharedHostAdmissionPropertyTests
    {
        private const int Iterations = 500;
        private const string SelfKey = "kb-self";
        private static readonly DateTime T0 = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        // Allowed ranges: Requirement 5 criterion 1, Requirement 6 criteria 2 and 3.
        private const int MaxWorkersMin = 1;
        private const int MaxWorkersMax = 16;
        private const int ColdStartMin = 1;
        private const int ColdStartMax = 4;
        private const long FloorMin = 256;
        private const long FloorMax = 8192;

        private static readonly string[] OtherStates = { "ready", "ready", "ready", "starting", "held", "no-worker", "admitting" };

        [Fact]
        public void Decide_ForAnyRecordsLimitsAndMemory_IsSoundAndMatchesReference()
        {
            SeededInterleavingRunner.Run("Property 7: Admission decision soundness", Iterations, c =>
            {
                var input = GenerateInput(c);
                LogInput(c, input);

                var actual = SharedHostAdmissionPolicy.Decide(input);
                c.Log("decision " + Describe(actual));

                var expected = ReferenceDecide(input);
                c.Log("reference " + expected);

                AssertMatchesReference(expected, actual);
                AssertSoundness(input, actual);

                // Non-live records are never counted: dropping them must not change the decision.
                var liveOnly = CopyWithHosts(input, input.Hosts.Where(h => h.Liveness == RecordLiveness.Live));
                var liveOnlyDecision = SharedHostAdmissionPolicy.Decide(liveOnly);
                c.Log("decision without non-live records " + Describe(liveOnlyDecision));
                Assert.Equal(actual.Kind, liveOnlyDecision.Kind);
                Assert.Equal(actual.Position, liveOnlyDecision.Position);
                Assert.Equal(actual.VictimKey, liveOnlyDecision.VictimKey);
                Assert.Equal(actual.Code, liveOnlyDecision.Code);
                Assert.Equal(actual.Reason, liveOnlyDecision.Reason);
            });
        }

        // ---- Generators -------------------------------------------------------------------------------------------

        private static AdmissionInput GenerateInput(PbtCase c)
        {
            var maxWorkers = c.SmallInt(MaxWorkersMin, MaxWorkersMax);
            var coldStartConcurrency = c.SmallInt(ColdStartMin, ColdStartMax);
            var floorMb = (long)c.Int((int)FloorMin, (int)FloorMax);
            long? availableMb;
            switch (c.Int(0, 5))
            {
                case 0: availableMb = null; break;
                case 1: availableMb = floorMb - 1; break;
                case 2: availableMb = floorMb; break;
                case 3: availableMb = floorMb + 1; break;
                default: availableMb = c.Int(0, 16384); break;
            }

            var hosts = new List<AdmissionHostView> { GenerateSelf(c) };

            // Worker counts near maxWorkers matter most, so the host count scales with the limit.
            var otherCount = c.Int(0, maxWorkers + 3);
            var usedKeys = new HashSet<string>(StringComparer.Ordinal) { SelfKey };
            for (var i = 0; i < otherCount; i++)
            {
                string key;
                do
                {
                    // Random key numbers so input order and key order differ.
                    key = "kb-" + c.Int(0, 99).ToString("D2", CultureInfo.InvariantCulture);
                } while (!usedKeys.Add(key));
                hosts.Add(GenerateOther(c, key));
            }

            // Self is not always first.
            var selfIndex = c.Int(0, hosts.Count - 1);
            var self = hosts[0];
            hosts[0] = hosts[selfIndex];
            hosts[selfIndex] = self;

            return new AdmissionInput
            {
                SelfKey = SelfKey,
                Hosts = hosts,
                MaxWorkers = maxWorkers,
                MaxOpenKbsExceeded = false,
                ColdStartConcurrency = coldStartConcurrency,
                MemoryFloorMb = floorMb,
                AvailableMb = availableMb,
            };
        }

        private static AdmissionHostView GenerateSelf(PbtCase c)
        {
            var held = c.Chance(0.3);
            return new AdmissionHostView
            {
                Key = SelfKey,
                State = held ? "held" : "admitting",
                WorkerPid = 0,
                Evictable = false,
                Attachments = c.Int(1, 3),
                LastActivityUtc = null,
                // Small offset range so ties with other held starts occur.
                HeldSinceUtc = held ? T0.AddSeconds(c.Int(0, 5)) : (DateTime?)null,
                Liveness = RecordLiveness.Live,
            };
        }

        private static AdmissionHostView GenerateOther(PbtCase c, string key)
        {
            // Owner mix: live, dead, start-time-mismatched (both classify as Stale), and unreadable start time.
            RecordLiveness liveness;
            var owner = c.Int(0, 9);
            if (owner < 6) liveness = RecordLiveness.Live;
            else if (owner < 9) liveness = RecordLiveness.Stale;
            else liveness = RecordLiveness.LivenessUnverified;

            var state = c.Pick(OtherStates);
            var view = new AdmissionHostView
            {
                Key = key,
                State = state,
                WorkerPid = 0,
                Evictable = false,
                Attachments = c.SmallInt(0, 3),
                LastActivityUtc = null,
                HeldSinceUtc = null,
                Liveness = liveness,
            };

            switch (state)
            {
                case "ready":
                    view.WorkerPid = c.Int(1000, 60000);
                    view.Evictable = c.Bool();
                    // Small minute range so equal last activity times occur and the key tie-break is exercised.
                    view.LastActivityUtc = T0.AddMinutes(c.Int(0, 4));
                    break;
                case "starting":
                    // A Cold_Start may or may not have created its Worker process yet.
                    view.WorkerPid = c.Chance(0.7) ? c.Int(1000, 60000) : 0;
                    break;
                case "held":
                    view.HeldSinceUtc = T0.AddSeconds(c.Int(0, 5));
                    break;
                case "no-worker":
                    view.LastActivityUtc = c.Bool() ? T0.AddMinutes(c.Int(0, 4)) : (DateTime?)null;
                    break;
            }

            return view;
        }

        private static AdmissionInput CopyWithHosts(AdmissionInput input, IEnumerable<AdmissionHostView> hosts)
        {
            return new AdmissionInput
            {
                SelfKey = input.SelfKey,
                Hosts = hosts.ToList(),
                MaxWorkers = input.MaxWorkers,
                MaxOpenKbsExceeded = input.MaxOpenKbsExceeded,
                ColdStartConcurrency = input.ColdStartConcurrency,
                MemoryFloorMb = input.MemoryFloorMb,
                AvailableMb = input.AvailableMb,
            };
        }

        // ---- Reference decision -----------------------------------------------------------------------------------

        private sealed class ExpectedDecision
        {
            internal AdmissionDecisionKind Kind;
            internal int? Position;
            internal string? VictimKey;
            internal string? Code;
            internal string? Reason;

            public override string ToString() =>
                Kind + " position=" + Fmt(Position) + " victim=" + (VictimKey ?? "-") + " code=" + (Code ?? "-") + " reason=" + (Reason ?? "-");
        }

        private static bool IsCountedWorker(AdmissionHostView h) => h.Liveness == RecordLiveness.Live && h.WorkerPid > 0;

        private static bool IsColdStart(AdmissionHostView h) => h.Liveness == RecordLiveness.Live && h.State == "starting";

        // Independent restatement of the contract: cold-start concurrency with a FIFO hold (Requirement 6 criterion 4:
        // whatever the Worker count), then the machine-wide count with eviction, then the Memory_Floor last.
        private static ExpectedDecision ReferenceDecide(AdmissionInput input)
        {
            var coldStarts = input.Hosts.Count(IsColdStart);
            if (coldStarts >= input.ColdStartConcurrency)
            {
                var self = input.Hosts.Single(h => h.Key == input.SelfKey);
                var selfHeldSince = self.State == "held" && self.HeldSinceUtc.HasValue ? self.HeldSinceUtc.Value : DateTime.MaxValue;
                var ahead = input.Hosts.Count(h =>
                    h.Key != input.SelfKey
                    && h.Liveness == RecordLiveness.Live
                    && h.State == "held"
                    && h.HeldSinceUtc.HasValue
                    && (h.HeldSinceUtc.Value < selfHeldSince
                        || (h.HeldSinceUtc.Value == selfHeldSince && string.CompareOrdinal(h.Key, input.SelfKey) < 0)));
                return new ExpectedDecision { Kind = AdmissionDecisionKind.Hold, Position = ahead + 1 };
            }

            var counted = input.Hosts.Where(IsCountedWorker).ToList();
            if (counted.Count >= input.MaxWorkers)
            {
                var evictable = counted.Where(h => h.Evictable).ToList();
                if (evictable.Count == 0)
                {
                    return new ExpectedDecision { Kind = AdmissionDecisionKind.Refuse, Code = "KB_POOL_FULL", Reason = "worker-limit" };
                }

                var zeroAttachments = evictable.Where(h => h.Attachments == 0).ToList();
                var candidates = zeroAttachments.Count > 0 ? zeroAttachments : evictable;
                AdmissionHostView? victim = null;
                foreach (var h in candidates)
                {
                    if (victim == null || IsOlder(h, victim)) victim = h;
                }
                return new ExpectedDecision { Kind = AdmissionDecisionKind.Evict, VictimKey = victim!.Key };
            }

            if (input.AvailableMb.HasValue && input.AvailableMb.Value < input.MemoryFloorMb)
            {
                return new ExpectedDecision { Kind = AdmissionDecisionKind.Refuse, Code = "KB_POOL_FULL", Reason = "low-memory" };
            }

            return new ExpectedDecision { Kind = AdmissionDecisionKind.Start };
        }

        // True when a is preferred over b as a victim: older last activity, then the lower key.
        private static bool IsOlder(AdmissionHostView a, AdmissionHostView b)
        {
            var ta = a.LastActivityUtc ?? throw new InvalidOperationException("Generator gave an evictable host no last activity time: " + a.Key);
            var tb = b.LastActivityUtc ?? throw new InvalidOperationException("Generator gave an evictable host no last activity time: " + b.Key);
            if (ta != tb) return ta < tb;
            return string.CompareOrdinal(a.Key, b.Key) < 0;
        }

        // ---- Assertions -------------------------------------------------------------------------------------------

        private static void AssertMatchesReference(ExpectedDecision expected, AdmissionDecision actual)
        {
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Position, actual.Position);
            Assert.Equal(expected.VictimKey, actual.VictimKey);
            Assert.Equal(expected.Code, actual.Code);
            Assert.Equal(expected.Reason, actual.Reason);
        }

        // The Property 7 statements, checked directly on the decision so a reference bug cannot hide a policy bug.
        private static void AssertSoundness(AdmissionInput input, AdmissionDecision d)
        {
            var counted = input.Hosts.Where(IsCountedWorker).ToList();
            var coldStarts = input.Hosts.Count(IsColdStart);
            var evictable = counted.Where(h => h.Evictable).ToList();
            var memoryBelowFloor = input.AvailableMb.HasValue && input.AvailableMb.Value < input.MemoryFloorMb;

            switch (d.Kind)
            {
                case AdmissionDecisionKind.Start:
                    Assert.True(counted.Count < input.MaxWorkers, "Start with " + counted.Count + " counted Workers at maximum " + input.MaxWorkers);
                    Assert.True(coldStarts < input.ColdStartConcurrency, "Start with " + coldStarts + " Cold_Starts at concurrency " + input.ColdStartConcurrency);
                    Assert.False(memoryBelowFloor, "Start with memory " + Fmt(input.AvailableMb) + " below floor " + input.MemoryFloorMb);
                    Assert.Null(d.VictimKey);
                    Assert.Null(d.Code);
                    Assert.Null(d.Reason);
                    break;

                case AdmissionDecisionKind.Evict:
                    Assert.True(counted.Count >= input.MaxWorkers, "Evict below the Worker maximum");
                    Assert.True(coldStarts < input.ColdStartConcurrency, "Evict while the Cold_Starts fill the concurrency");
                    var victim = evictable.SingleOrDefault(h => h.Key == d.VictimKey);
                    Assert.True(victim != null, "Victim '" + d.VictimKey + "' is not a live evictable Worker");
                    var zero = evictable.Where(h => h.Attachments == 0).ToList();
                    var candidates = zero.Count > 0 ? zero : evictable;
                    Assert.Contains(victim!, candidates);
                    Assert.DoesNotContain(candidates, h => !ReferenceEquals(h, victim) && IsOlder(h, victim!));
                    Assert.Null(d.Code);
                    Assert.Null(d.Reason);
                    break;

                case AdmissionDecisionKind.Hold:
                    // Requirement 6 criterion 4 holds at the concurrency whatever the Worker count; criterion 5
                    // re-applies the Worker limit when the hold is released.
                    Assert.True(coldStarts >= input.ColdStartConcurrency, "Hold below cold-start concurrency");
                    Assert.True(d.Position.HasValue && d.Position.Value >= 1, "Hold without a 1-based position");
                    Assert.Null(d.VictimKey);
                    break;

                case AdmissionDecisionKind.Refuse:
                    Assert.Equal("KB_POOL_FULL", d.Code);
                    Assert.Null(d.VictimKey);
                    Assert.NotNull(d.Details);
                    if (d.Reason == "worker-limit")
                    {
                        Assert.True(counted.Count >= input.MaxWorkers, "worker-limit below the Worker maximum");
                        Assert.True(coldStarts < input.ColdStartConcurrency, "worker-limit while the Cold_Starts fill the concurrency");
                        Assert.Empty(evictable);
                        Assert.Equal(input.MaxWorkers, (int)d.Details!["maxWorkers"]!);
                        var openKbs = ((JArray)d.Details["openKbs"]!).Cast<JObject>().ToList();
                        Assert.Equal(
                            counted.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                            openKbs.Select(o => (string)o["key"]!).OrderBy(k => k, StringComparer.Ordinal).ToArray());
                        foreach (var o in openKbs)
                        {
                            var host = counted.Single(h => h.Key == (string)o["key"]!);
                            Assert.Equal(host.Attachments, (int)o["sessions"]!);
                        }
                    }
                    else if (d.Reason == "low-memory")
                    {
                        Assert.True(memoryBelowFloor, "low-memory with memory " + Fmt(input.AvailableMb) + " and floor " + input.MemoryFloorMb);
                        Assert.True(counted.Count < input.MaxWorkers, "low-memory reported before the Worker maximum");
                        Assert.True(coldStarts < input.ColdStartConcurrency, "low-memory reported before cold-start concurrency");
                        Assert.Equal(input.AvailableMb!.Value, (long)d.Details!["availableMb"]!);
                        Assert.Equal(input.MemoryFloorMb, (long)d.Details["floorMb"]!);
                    }
                    else
                    {
                        Assert.Fail("Unexpected refusal reason '" + d.Reason + "' with MaxOpenKbsExceeded=false");
                    }
                    break;

                default:
                    Assert.Fail("Unknown decision kind " + d.Kind);
                    break;
            }
        }

        // ---- Logging ----------------------------------------------------------------------------------------------

        private static void LogInput(PbtCase c, AdmissionInput input)
        {
            c.Log("limits maxWorkers=" + input.MaxWorkers
                + " coldStartConcurrency=" + input.ColdStartConcurrency
                + " memoryFloorMb=" + input.MemoryFloorMb
                + " availableMb=" + Fmt(input.AvailableMb)
                + " maxOpenKbsExceeded=" + input.MaxOpenKbsExceeded);
            foreach (var h in input.Hosts)
            {
                c.Log("host key=" + h.Key
                    + " state=" + h.State
                    + " liveness=" + h.Liveness
                    + " workerPid=" + h.WorkerPid
                    + " evictable=" + h.Evictable
                    + " attachments=" + h.Attachments
                    + " lastActivityUtc=" + FmtTime(h.LastActivityUtc)
                    + " heldSinceUtc=" + FmtTime(h.HeldSinceUtc));
            }
        }

        private static string Describe(AdmissionDecision d) =>
            d.Kind + " position=" + Fmt(d.Position) + " victim=" + (d.VictimKey ?? "-") + " code=" + (d.Code ?? "-") + " reason=" + (d.Reason ?? "-");

        private static string Fmt(long? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unavailable";

        private static string Fmt(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";

        private static string FmtTime(DateTime? value) =>
            value.HasValue ? value.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "-";
    }
}
