using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 8: Held starts are released in FIFO order
    //
    // For any set of held starts with distinct or equal heldSinceUtc values, when the policy releases a held start it
    // releases the one with the earliest heldSinceUtc (ties broken by key), and it re-applies every limit to that
    // start at release time.
    //
    // The test simulates the held-start lifecycle over the pure SharedHostAdmissionPolicy.Decide. While every
    // Cold_Start slot is taken, each held host re-runs Decide (its periodic re-check) and reports its Hold position.
    // When a Cold_Start ends (success, failure, or a crashed owner), the simulated releaser wakes held hosts in the
    // order of the positions the policy reported, one at a time, until a slot is taken again or no held start is left.
    // Each woken host re-runs Decide against the state at that moment, with a fresh memory reading and, sometimes,
    // extra Workers that appeared after the hold, so the Worker limit, eviction, and the Memory_Floor are all
    // re-applied at release.
    //
    // **Validates: Requirements 6.4, 6.5**
    public class SharedHostHeldStartPropertyTests
    {
        private const int Iterations = 200;
        private static readonly DateTime T0 = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void HeldStarts_AreReleasedInFifoOrder_WithEveryLimitReappliedAtRelease()
        {
            SeededInterleavingRunner.Run("Property 8: Held starts are released in FIFO order", Iterations, c => new Simulation(c).Run());
        }

        [Fact]
        public void HeldStarts_WithEqualHeldSince_AreReleasedByOrdinalKeyAfterEarlierHolds()
        {
            // kb-d was held first; kb-b and kb-c were held at the same instant, so the ordinal key decides.
            var hosts = new List<AdmissionHostView>
            {
                View("kb-a", "starting", workerPid: 7001),
                View("kb-c", "held", heldSince: T0.AddSeconds(2)),
                View("kb-b", "held", heldSince: T0.AddSeconds(2)),
                View("kb-d", "held", heldSince: T0.AddSeconds(1)),
            };

            Assert.Equal(1, Decide(hosts, "kb-d").Position);
            Assert.Equal(2, Decide(hosts, "kb-b").Position);
            Assert.Equal(3, Decide(hosts, "kb-c").Position);

            // The Cold_Start of kb-a ends; the head kb-d is released and starts.
            hosts[0].State = "ready";
            Assert.Equal(AdmissionDecisionKind.Start, Decide(hosts, "kb-d").Kind);
            hosts[3].State = "starting";
            hosts[3].HeldSinceUtc = null;
            hosts[3].WorkerPid = 7002;

            // The slot is taken again; the remaining held starts move up by one.
            Assert.Equal(AdmissionDecisionKind.Hold, Decide(hosts, "kb-b").Kind);
            Assert.Equal(1, Decide(hosts, "kb-b").Position);
            Assert.Equal(2, Decide(hosts, "kb-c").Position);
        }

        private static AdmissionDecision Decide(List<AdmissionHostView> hosts, string selfKey)
        {
            return SharedHostAdmissionPolicy.Decide(new AdmissionInput
            {
                SelfKey = selfKey,
                Hosts = hosts.ToList(),
                MaxWorkers = 8,
                MaxOpenKbsExceeded = false,
                ColdStartConcurrency = 1,
                MemoryFloorMb = 1024,
                AvailableMb = 4096,
            });
        }

        private static AdmissionHostView View(string key, string state, int workerPid = 0, DateTime? heldSince = null)
        {
            return new AdmissionHostView
            {
                Key = key,
                State = state,
                WorkerPid = workerPid,
                Evictable = false,
                Attachments = 1,
                LastActivityUtc = null,
                HeldSinceUtc = heldSince,
                Liveness = RecordLiveness.Live,
            };
        }

        // ---- Simulation -------------------------------------------------------------------------------------------

        private sealed class SimHost
        {
            internal string Key = "";
            internal string State = "";
            internal int WorkerPid;
            internal bool Evictable;
            internal int Attachments;
            internal DateTime? LastActivityUtc;
            internal DateTime? HeldSinceUtc;
            internal RecordLiveness Liveness = RecordLiveness.Live;
            // Workers of unrelated KBs that appear after the holds and close at the end of the release round.
            internal bool Transient;

            internal AdmissionHostView ToView() => new AdmissionHostView
            {
                Key = Key,
                State = State,
                WorkerPid = WorkerPid,
                Evictable = Evictable,
                Attachments = Attachments,
                LastActivityUtc = LastActivityUtc,
                HeldSinceUtc = HeldSinceUtc,
                Liveness = Liveness,
            };

            public override string ToString() =>
                Key + " state=" + State + " liveness=" + Liveness + " workerPid=" + WorkerPid.ToString(CultureInfo.InvariantCulture)
                + " evictable=" + Evictable + " attachments=" + Attachments.ToString(CultureInfo.InvariantCulture)
                + " heldSinceUtc=" + FmtTime(HeldSinceUtc) + (Transient ? " transient" : "");
        }

        private sealed class Simulation
        {
            private const int MaxRounds = 64;
            private const int MaxDecisionsPerRelease = 32;

            private readonly PbtCase _c;
            private readonly List<SimHost> _hosts = new List<SimHost>();
            private readonly HashSet<string> _usedKeys = new HashSet<string>(StringComparer.Ordinal);
            // Every live start that was ever held, with its heldSinceUtc; the reference for the final release order.
            private readonly List<KeyValuePair<string, DateTime>> _everHeld = new List<KeyValuePair<string, DateTime>>();
            private readonly List<string> _released = new List<string>();
            // Hold positions reported by the policy in the latest probe, valid until the held set changes.
            private Dictionary<string, int> _positions = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly int _maxWorkers;
            private readonly int _coldStartConcurrency;
            private readonly long _memoryFloorMb;
            private int _arrivalsLeft;
            private int _nextPid = 10000;
            private int _transientSeq;
            private DateTime _clock;

            internal Simulation(PbtCase c)
            {
                _c = c;
                _coldStartConcurrency = c.SmallInt(1, 3);
                _memoryFloorMb = c.Int(256, 2048);
                _arrivalsLeft = c.Int(0, 2);

                // Cold_Starts in progress fill every slot, so the generated starts are held.
                for (var i = 0; i < _coldStartConcurrency; i++)
                {
                    var pid = c.Chance(0.7) ? NextPid() : 0;
                    _hosts.Add(new SimHost { Key = NewKey(), State = "starting", WorkerPid = pid, Attachments = c.Int(1, 2) });
                }

                // Small offset range so equal heldSinceUtc values occur and the key tie-break is exercised.
                var heldCount = c.Int(1, 4);
                for (var i = 0; i < heldCount; i++)
                {
                    var heldSince = T0.AddSeconds(c.Int(0, 3));
                    var held = new SimHost { Key = NewKey(), State = "held", HeldSinceUtc = heldSince, Attachments = c.Int(1, 3) };
                    _hosts.Add(held);
                    _everHeld.Add(new KeyValuePair<string, DateTime>(held.Key, heldSince));
                }

                // Held and starting records whose owner is not verified live: never counted, never released. Their
                // heldSinceUtc is earlier, so they would take the head position if the policy counted them.
                var deadHeld = c.Int(0, 2);
                for (var i = 0; i < deadHeld; i++)
                {
                    _hosts.Add(new SimHost
                    {
                        Key = NewKey(),
                        State = "held",
                        HeldSinceUtc = T0.AddSeconds(-c.Int(0, 3)),
                        Attachments = 1,
                        Liveness = c.Bool() ? RecordLiveness.Stale : RecordLiveness.LivenessUnverified,
                    });
                }
                if (c.Chance(0.3))
                {
                    _hosts.Add(new SimHost { Key = NewKey(), State = "starting", WorkerPid = NextPid(), Attachments = 1, Liveness = RecordLiveness.Stale });
                }

                var baselineReady = c.Int(0, 3);
                for (var i = 0; i < baselineReady; i++)
                {
                    _hosts.Add(new SimHost
                    {
                        Key = NewKey(),
                        State = "ready",
                        WorkerPid = NextPid(),
                        Evictable = c.Bool(),
                        Attachments = c.SmallInt(0, 2),
                        LastActivityUtc = T0.AddMinutes(-c.Int(0, 4)),
                    });
                }

                // Room for every Worker this run can create, so only transient Workers can reach the maximum, and
                // they appear only between the last probe and a release. At most 3 + 3 + 4 + 2 + 3 = 15, inside the
                // allowed range of 1 to 16.
                _maxWorkers = baselineReady + _coldStartConcurrency + heldCount + _arrivalsLeft + c.Int(1, 3);
                _clock = T0.AddSeconds(10);

                c.Log("limits maxWorkers=" + _maxWorkers + " coldStartConcurrency=" + _coldStartConcurrency + " memoryFloorMb=" + _memoryFloorMb
                    + " arrivals=" + _arrivalsLeft);
                foreach (var h in _hosts) c.Log("host " + h);
            }

            internal void Run()
            {
                var round = 0;
                while (LiveHeld().Count > 0)
                {
                    round++;
                    if (round > MaxRounds) Assert.Fail("No progress after " + MaxRounds + " rounds; held starts remain: " + string.Join(", ", LiveHeld().Select(h => h.Key)));
                    _c.Log("round " + round);

                    // Every held start waits only while every Cold_Start slot is taken.
                    Assert.True(ColdStarts() >= _coldStartConcurrency,
                        "A held start waits while a Cold_Start slot is free: " + ColdStarts() + " of " + _coldStartConcurrency);

                    Probe();
                    if (_arrivalsLeft > 0 && _c.Chance(0.4)) Arrive();
                    EndColdStart();
                    if (_c.Chance(0.4)) AddTransientWorkers();
                    ReleaseRound();
                    RemoveTransientWorkers();
                    _clock = _clock.AddSeconds(1);
                }

                var expectedOrder = _everHeld.OrderBy(h => h.Value).ThenBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key).ToList();
                _c.Log("released " + string.Join(", ", _released));
                _c.Log("expected " + string.Join(", ", expectedOrder));
                Assert.Equal(expectedOrder, _released);
            }

            // Each held host re-runs Decide in an arbitrary order while every slot is taken. No host starts, and the
            // reported positions are the FIFO ranks by heldSinceUtc, then key.
            private void Probe()
            {
                var held = LiveHeld();
                var rank = ReferenceOrder();
                var positions = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var h in Shuffle(held))
                {
                    var d = Decide(h.Key, SampleMemory());
                    _c.Log("probe " + h.Key + " -> " + Describe(d));
                    Assert.True(d.Kind == AdmissionDecisionKind.Hold,
                        "Held start " + h.Key + " got " + d.Kind + " while every Cold_Start slot is taken and Workers are below the maximum");
                    Assert.Equal(rank.IndexOf(h.Key) + 1, d.Position);
                    positions[h.Key] = d.Position!.Value;
                }
                _positions = positions;
            }

            // A new start request while every slot is taken is held behind every live held start.
            private void Arrive()
            {
                _arrivalsLeft--;
                _clock = _clock.AddSeconds(1);
                var host = new SimHost { Key = NewKey(), State = "admitting", Attachments = 1 };
                _hosts.Add(host);
                var d = Decide(host.Key, SampleMemory());
                _c.Log("arrive " + host.Key + " -> " + Describe(d));
                Assert.Equal(AdmissionDecisionKind.Hold, d.Kind);
                Assert.Equal(LiveHeld().Count + 1, d.Position);

                // Held strictly later than every earlier hold, so it joins the tail.
                host.State = "held";
                host.HeldSinceUtc = _clock;
                _everHeld.Add(new KeyValuePair<string, DateTime>(host.Key, _clock));
                Probe();
            }

            // One Cold_Start ends: its Worker becomes ready, the start fails and its record goes, or its owner crashes
            // and the record is no longer live (Requirement 6 criterion 12).
            private void EndColdStart()
            {
                var starting = _hosts.Where(h => h.Liveness == RecordLiveness.Live && h.State == "starting").ToList();
                var ended = _c.Pick(starting);
                switch (_c.Int(0, 2))
                {
                    case 0:
                        ended.State = "ready";
                        if (ended.WorkerPid == 0) ended.WorkerPid = NextPid();
                        ended.Evictable = _c.Bool();
                        ended.LastActivityUtc = _clock;
                        _c.Log("cold-start ended " + ended.Key + " ready workerPid=" + ended.WorkerPid);
                        break;
                    case 1:
                        _hosts.Remove(ended);
                        _c.Log("cold-start ended " + ended.Key + " failed; record removed");
                        break;
                    default:
                        ended.Liveness = RecordLiveness.Stale;
                        _c.Log("cold-start ended " + ended.Key + " owner crashed; record stale");
                        break;
                }
            }

            // Workers of unrelated KBs start after the holds, bringing the count to or near the maximum at release.
            private void AddTransientWorkers()
            {
                var count = Math.Max(0, _maxWorkers - CountedWorkers().Count + _c.Int(-1, 0));
                for (var i = 0; i < count; i++)
                {
                    var h = new SimHost
                    {
                        Key = "kb-t" + (_transientSeq++).ToString("D2", CultureInfo.InvariantCulture),
                        State = "ready",
                        WorkerPid = NextPid(),
                        Evictable = _c.Chance(0.6),
                        Attachments = _c.SmallInt(0, 2),
                        LastActivityUtc = _clock.AddMinutes(-_c.Int(0, 3)),
                        Transient = true,
                    };
                    _hosts.Add(h);
                    _c.Log("transient worker " + h);
                }
            }

            private void RemoveTransientWorkers()
            {
                var removed = _hosts.RemoveAll(h => h.Transient);
                if (removed > 0) _c.Log("transient workers closed: " + removed);
            }

            // The releaser wakes held starts one at a time in the order of the positions the policy reported, while a
            // slot is free. Each woken start re-runs Decide with every limit applied to the state at that moment.
            private void ReleaseRound()
            {
                foreach (var key in _positions.OrderBy(p => p.Value).Select(p => p.Key).ToList())
                {
                    if (ColdStarts() >= _coldStartConcurrency) break;

                    var host = _hosts.Single(h => h.Key == key);
                    var head = ReferenceOrder().First();
                    Assert.True(head == key, "Woke " + key + " while " + head + " is ahead of it");
                    Release(host);
                }

                Assert.True(LiveHeld().Count == 0 || ColdStarts() >= _coldStartConcurrency,
                    "A Cold_Start slot is free after the release round while starts are still held: "
                    + string.Join(", ", LiveHeld().Select(h => h.Key)));
            }

            private void Release(SimHost host)
            {
                for (var attempt = 0; attempt < MaxDecisionsPerRelease; attempt++)
                {
                    // A fresh memory reading for every decision; an earlier reading is never reused.
                    var memory = SampleMemory();
                    var counted = CountedWorkers();
                    var evictable = counted.Where(h => h.Evictable).ToList();
                    var coldStarts = ColdStarts();
                    var d = Decide(host.Key, memory);
                    _c.Log("release " + host.Key + " counted=" + counted.Count + " coldStarts=" + coldStarts + " availableMb=" + Fmt(memory)
                        + " -> " + Describe(d));

                    if (counted.Count >= _maxWorkers)
                    {
                        if (evictable.Count > 0)
                        {
                            Assert.Equal(AdmissionDecisionKind.Evict, d.Kind);
                            var victim = evictable.SingleOrDefault(h => h.Key == d.VictimKey);
                            Assert.True(victim != null, "Victim '" + d.VictimKey + "' is not a live evictable Worker");
                            _hosts.Remove(victim!);
                            _c.Log("evicted " + victim!.Key);
                            continue;
                        }

                        AssertRefused(d, "worker-limit");
                        Drop(host, "worker-limit");
                        return;
                    }

                    if (memory.HasValue && memory.Value < _memoryFloorMb)
                    {
                        AssertRefused(d, "low-memory");
                        Drop(host, "low-memory");
                        return;
                    }

                    Assert.Equal(AdmissionDecisionKind.Start, d.Kind);
                    Assert.Null(d.Code);
                    Assert.Null(d.Reason);
                    var ahead = ReferenceOrder().TakeWhile(k => k != host.Key).ToList();
                    Assert.True(ahead.Count == 0, "Held start " + host.Key + " started behind " + string.Join(", ", ahead));

                    host.State = "starting";
                    host.HeldSinceUtc = null;
                    host.WorkerPid = _c.Chance(0.7) ? NextPid() : 0;
                    _released.Add(host.Key);
                    _positions.Remove(host.Key);
                    Assert.True(ColdStarts() <= _coldStartConcurrency,
                        "Cold_Starts " + ColdStarts() + " exceed the concurrency " + _coldStartConcurrency);
                    return;
                }

                Assert.Fail("Release of " + host.Key + " did not settle after " + MaxDecisionsPerRelease + " decisions");
            }

            private void AssertRefused(AdmissionDecision d, string reason)
            {
                Assert.Equal(AdmissionDecisionKind.Refuse, d.Kind);
                Assert.Equal("KB_POOL_FULL", d.Code);
                Assert.Equal(reason, d.Reason);
            }

            // A refused held start creates no Worker and leaves no held record behind.
            private void Drop(SimHost host, string reason)
            {
                host.State = "refused";
                host.HeldSinceUtc = null;
                host.WorkerPid = 0;
                _released.Add(host.Key);
                _positions.Remove(host.Key);
                _c.Log("refused " + host.Key + " reason=" + reason);
            }

            private AdmissionDecision Decide(string selfKey, long? availableMb)
            {
                return SharedHostAdmissionPolicy.Decide(new AdmissionInput
                {
                    SelfKey = selfKey,
                    Hosts = _hosts.Select(h => h.ToView()).ToList(),
                    MaxWorkers = _maxWorkers,
                    MaxOpenKbsExceeded = false,
                    ColdStartConcurrency = _coldStartConcurrency,
                    MemoryFloorMb = _memoryFloorMb,
                    AvailableMb = availableMb,
                });
            }

            private List<SimHost> LiveHeld() =>
                _hosts.Where(h => h.Liveness == RecordLiveness.Live && h.State == "held").ToList();

            // Independent FIFO order: live held starts by heldSinceUtc, then ordinal key.
            private List<string> ReferenceOrder() =>
                LiveHeld().OrderBy(h => h.HeldSinceUtc!.Value).ThenBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key).ToList();

            private int ColdStarts() => _hosts.Count(h => h.Liveness == RecordLiveness.Live && h.State == "starting");

            private List<SimHost> CountedWorkers() => _hosts.Where(h => h.Liveness == RecordLiveness.Live && h.WorkerPid > 0).ToList();

            private long? SampleMemory()
            {
                switch (_c.Int(0, 7))
                {
                    case 0: return null;
                    case 1: return _memoryFloorMb - 1;
                    case 2: return _memoryFloorMb;
                    case 3: return _memoryFloorMb + 1;
                    default: return _memoryFloorMb + _c.Int(0, 4096);
                }
            }

            private List<SimHost> Shuffle(List<SimHost> items)
            {
                var copy = items.ToList();
                for (var i = copy.Count - 1; i > 0; i--)
                {
                    var j = _c.Int(0, i);
                    var t = copy[i];
                    copy[i] = copy[j];
                    copy[j] = t;
                }
                return copy;
            }

            private string NewKey()
            {
                string key;
                do
                {
                    // Random key numbers so creation order and key order differ.
                    key = "kb-" + _c.Int(0, 99).ToString("D2", CultureInfo.InvariantCulture);
                } while (!_usedKeys.Add(key));
                return key;
            }

            private int NextPid() => _nextPid++;
        }

        // ---- Formatting -------------------------------------------------------------------------------------------

        private static string Describe(AdmissionDecision d) =>
            d.Kind + " position=" + (d.Position.HasValue ? d.Position.Value.ToString(CultureInfo.InvariantCulture) : "-")
            + " victim=" + (d.VictimKey ?? "-") + " code=" + (d.Code ?? "-") + " reason=" + (d.Reason ?? "-");

        private static string Fmt(long? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unavailable";

        private static string FmtTime(DateTime? value) =>
            value.HasValue ? value.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "-";
    }
}
