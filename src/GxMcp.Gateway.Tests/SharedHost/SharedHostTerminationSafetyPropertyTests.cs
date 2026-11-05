using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 10: Termination safety
    // Validates: Requirements 7.4
    //
    // Property-based tests for the stale-ownership kill decision. They use only the contract pinned in the header of
    // src/GxMcp.Gateway.Tests/SharedWorkerRegistryV2Tests.cs (task 4.5):
    //   SharedWorkerRegistry.SelectProcessesToTerminate(candidates, observations, gatewayPids)
    //   SharedWorkerRegistry.StopOrphanExact(pid, startTicks, expectedImagePath, SharedWorkerRecoveryContext)
    //   OrphanCandidate, ProcessObservation, OrphanStopOutcome, OrphanStopResult.
    //
    // The oracles below are independent of the code under test:
    // - Millisecond equality is computed here as ticks - ticks % TicksPerMillisecond.
    // - Image path equality is ground truth from the generator: every path is a spelling variant (case, trailing
    //   separator) of one of a few base paths, and two paths are equal exactly when they share a base index. The
    //   oracle never calls SharedWorkerIdentity.NormalizePath.
    // - Duplicate WorkerPid values: the first candidate that passes every rule is kept, later ones are dropped
    //   ("Output keeps input order and contains each WorkerPid at most once").
    //
    // No real process is probed, read, or terminated: every probe, image reader, and terminator is a fake. The
    // StopOrphanExact waits use a 5 ms StopWaitTimeout with a 1 ms StopPollInterval, and the fake probe reports
    // an exit or a reused PID on the first poll after Terminate.
    public class SharedHostTerminationSafetyPropertyTests
    {
        private const string PropertyName = "Feature: concurrent-multi-session-workers, Property 10: Termination safety";
        private const int Iterations = 300;

        // Fixed base seeds keep the generated cases deterministic. GXMCP_PBT_SEED still replays one reported case.
        private const int SelectorBaseSeed = 100_407;
        private const int StopBaseSeed = 100_409;

        private const long BaseTicks = 638_700_000_000_000_000L;
        private const long Ms = TimeSpan.TicksPerMillisecond;

        // Base image paths. Index 0 is the Worker; index 1 is another directory; index 2 is the same directory with
        // another file name. Two paths are equal after normalization exactly when they share the base index.
        private static readonly string[] BaseImages =
        {
            @"C:\Tools\GxMcp\worker\GxMcp.Worker.exe",
            @"C:\Other\GxMcp\worker\GxMcp.Worker.exe",
            @"C:\Tools\GxMcp\worker\GxMcp.Gateway.exe"
        };

        [Fact]
        public void SelectProcessesToTerminate_PicksExactlyTheOracleSet_AndNeverAGatewayOrALiveOwnersWorker()
        {
            var totals = new SelectorTotals();

            SeededInterleavingRunner.Run(
                PropertyName + " (kill-set selector)",
                Iterations,
                c => CheckSelectorCase(c, totals),
                ReplaySeed(),
                SelectorBaseSeed);

            if (ReplaySeed().HasValue) return;

            // The generator must reach every rule, or the property would pass vacuously.
            Assert.True(totals.Selected > 0, "no case selected a process");
            Assert.True(totals.RejectedGatewayPid > 0, "no case rejected a matching Worker because of a Gateway PID");
            Assert.True(totals.RejectedOwnerPid > 0, "no case rejected a matching Worker because it is an owner PID");
            Assert.True(totals.RejectedLiveOwner > 0, "no case rejected a matching Worker because of a live owner");
            Assert.True(totals.RejectedDuplicate > 0, "no case dropped a duplicate WorkerPid");
            Assert.True(totals.RejectedMismatch > 0, "no case rejected a Worker whose PID, start time or image did not match");
        }

        [Fact]
        public void StopOrphanExact_TerminatesOnlyAnExactNonGatewayMatch_AndOtherwiseReturnsTheExpectedRefusal()
        {
            var outcomes = new Dictionary<OrphanStopOutcome, int>();

            SeededInterleavingRunner.Run(
                PropertyName + " (StopOrphanExact)",
                Iterations,
                c => CheckStopCase(c, outcomes),
                ReplaySeed(),
                StopBaseSeed);

            if (ReplaySeed().HasValue) return;

            foreach (OrphanStopOutcome outcome in Enum.GetValues(typeof(OrphanStopOutcome)))
            {
                Assert.True(outcomes.ContainsKey(outcome), "the generator never produced outcome " + outcome);
            }
        }

        // ---------------------------------------------------------------
        // Kill-set selector
        // ---------------------------------------------------------------

        private sealed class SelectorTotals
        {
            internal int Selected;
            internal int RejectedGatewayPid;
            internal int RejectedOwnerPid;
            internal int RejectedLiveOwner;
            internal int RejectedDuplicate;
            internal int RejectedMismatch;
        }

        private sealed class GeneratedCandidate
        {
            internal OrphanCandidate Candidate = null!;

            // Base index of ExpectedImagePath, or -1 when it is empty.
            internal int ExpectedBase;
        }

        private sealed class GeneratedObservation
        {
            internal ProcessObservation Observation = null!;

            // Base index of ImagePath, or -1 when it is null.
            internal int ImageBase;
        }

        private static void CheckSelectorCase(PbtCase c, SelectorTotals totals)
        {
            var pids = new PidSource();
            var gatewayPids = Enumerable.Range(0, c.SmallInt(0, 3)).Select(_ => pids.Next()).ToList();
            c.Log("gatewayPids=[" + string.Join(",", gatewayPids.Select(Num)) + "]");

            var generated = GenerateCandidates(c, pids, gatewayPids);
            var observations = GenerateObservations(c, generated);

            var expected = SelectorOracle(generated, observations, gatewayPids, totals);
            c.Log("expected=[" + string.Join(",", expected.Select(g => g.Candidate.RecordKey)) + "]");

            var candidates = generated.Select(g => g.Candidate).ToList();
            var observationMap = observations.ToDictionary(o => o.Key, o => o.Value.Observation);

            var selected = SharedWorkerRegistry.SelectProcessesToTerminate(candidates, observationMap, gatewayPids);
            c.Log("actual=[" + string.Join(",", selected.Select(s => s.RecordKey + "/pid " + Num(s.WorkerPid))) + "]");

            // Exactly the oracle's set, in input order.
            Assert.Equal(expected.Select(g => g.Candidate.RecordKey).ToArray(), selected.Select(s => s.RecordKey).ToArray());

            // No duplicates.
            Assert.Equal(selected.Count, selected.Select(s => s.WorkerPid).Distinct().Count());

            // A Gateway PID is never selected: neither a listed Gateway PID nor any recorded owner Gateway PID.
            var ownerPids = new HashSet<int>(candidates.Select(k => k.OwnerGatewayPid).Where(p => p > 0));
            foreach (var s in selected)
            {
                Assert.DoesNotContain(s.WorkerPid, gatewayPids);
                Assert.DoesNotContain(s.WorkerPid, ownerPids);
            }

            // A live owner's Worker is never selected.
            foreach (var s in selected)
            {
                Assert.False(OwnerIsLive(s, observations), "selected " + s.RecordKey + " whose owner is live");
            }
        }

        private static List<GeneratedCandidate> GenerateCandidates(PbtCase c, PidSource pids, List<int> gatewayPids)
        {
            int count = c.SmallInt(1, 8);
            var generated = new List<GeneratedCandidate>(count);

            // Owners first, so a Worker PID can collide with any candidate's owner PID.
            var owners = new List<(int Pid, long Ticks)>(count);
            for (int i = 0; i < count; i++)
            {
                int roll = c.Int(1, 100);
                var earlierOwners = owners.Select(o => o.Pid).Where(p => p > 0).ToList();
                int ownerPid;
                if (roll <= 10) ownerPid = c.Pick(new[] { 0, -1, -4 });
                else if (roll <= 25 && gatewayPids.Count > 0) ownerPid = c.Pick(gatewayPids);
                else if (roll <= 40 && earlierOwners.Count > 0) ownerPid = c.Pick(earlierOwners);
                else ownerPid = pids.Next();
                long ownerTicks = c.Chance(0.15) ? 0L : GenerateTicks(c);
                owners.Add((ownerPid, ownerTicks));
            }

            var positiveOwners = owners.Select(o => o.Pid).Where(p => p > 0).ToList();
            for (int i = 0; i < count; i++)
            {
                int roll = c.Int(1, 100);
                int workerPid;
                long workerTicks = c.Chance(0.08) ? 0L : GenerateTicks(c);
                int expectedBase = c.Chance(0.07) ? -1 : (c.Chance(0.8) ? 0 : c.Int(1, BaseImages.Length - 1));
                string kind;

                if (roll <= 8)
                {
                    kind = "non-positive";
                    workerPid = c.Pick(new[] { 0, -1, -4 });
                }
                else if (roll <= 20 && gatewayPids.Count > 0)
                {
                    kind = "gateway-pid";
                    workerPid = c.Pick(gatewayPids);
                }
                else if (roll <= 32 && positiveOwners.Count > 0)
                {
                    kind = "owner-pid";
                    workerPid = c.Pick(positiveOwners);
                }
                else if (roll <= 46 && generated.Any(g => g.Candidate.WorkerPid > 0))
                {
                    kind = "duplicate";
                    var earlier = c.Pick(generated.Where(g => g.Candidate.WorkerPid > 0).ToList());
                    workerPid = earlier.Candidate.WorkerPid;
                    if (c.Chance(0.6))
                    {
                        workerTicks = earlier.Candidate.WorkerStartTimeUtcTicks;
                        expectedBase = earlier.ExpectedBase;
                    }
                }
                else
                {
                    kind = "fresh";
                    workerPid = pids.Next();
                }

                var candidate = new OrphanCandidate
                {
                    RegistryName = c.Bool() ? StaleRecoveryRegistryNames.WorkerOwnershipRegistry : StaleRecoveryRegistryNames.SharedWorkerRegistry,
                    RecordKey = "rec-" + Num(i),
                    OwnerGatewayPid = owners[i].Pid,
                    OwnerGatewayStartTimeUtcTicks = owners[i].Ticks,
                    WorkerPid = workerPid,
                    WorkerStartTimeUtcTicks = workerTicks,
                    ExpectedImagePath = expectedBase < 0 ? string.Empty : ImageVariant(c, expectedBase)
                };
                generated.Add(new GeneratedCandidate { Candidate = candidate, ExpectedBase = expectedBase });
                c.Log("candidate " + candidate.RecordKey + ": owner=" + Num(candidate.OwnerGatewayPid) +
                      " ownerTicks=" + Num(candidate.OwnerGatewayStartTimeUtcTicks) +
                      " worker=" + Num(workerPid) + " (" + kind + ") workerTicks=" + Num(workerTicks) +
                      " image=\"" + candidate.ExpectedImagePath + "\" base=" + Num(expectedBase));
            }

            return generated;
        }

        // One observation per distinct positive PID that a candidate references; some PIDs get none (missing).
        private static Dictionary<int, GeneratedObservation> GenerateObservations(PbtCase c, List<GeneratedCandidate> generated)
        {
            var roles = new Dictionary<int, List<(bool Worker, long Ticks, int Base)>>();
            foreach (var g in generated)
            {
                AddRole(roles, g.Candidate.OwnerGatewayPid, false, g.Candidate.OwnerGatewayStartTimeUtcTicks, -1);
                AddRole(roles, g.Candidate.WorkerPid, true, g.Candidate.WorkerStartTimeUtcTicks, g.ExpectedBase);
            }

            var observations = new Dictionary<int, GeneratedObservation>();
            foreach (var entry in roles.OrderBy(e => e.Key))
            {
                int pid = entry.Key;
                int roll = c.Int(1, 100);
                if (roll <= 8)
                {
                    c.Log("observation " + Num(pid) + ": missing");
                    continue;
                }

                ProcessProbeResult probe;
                string probeText;
                if (roll <= 20)
                {
                    probe = ProcessProbeResult.NotRunning;
                    probeText = "NotRunning";
                }
                else if (roll <= 30)
                {
                    probe = ProcessProbeResult.RunningStartTimeUnreadable;
                    probeText = "RunningStartTimeUnreadable";
                }
                else
                {
                    var reference = c.Pick(entry.Value);
                    long ticks;
                    string tickKind;
                    if (reference.Ticks > 0)
                    {
                        ticks = ObservedTicks(c, reference.Ticks, out tickKind);
                    }
                    else
                    {
                        ticks = GenerateTicks(c);
                        tickKind = "no recorded start time";
                    }

                    probe = ProcessProbeResult.Running(ticks);
                    probeText = "Running ticks=" + Num(ticks) + " (" + tickKind + ")";
                }

                int imageBase;
                var workerRoles = entry.Value.Where(r => r.Worker).ToList();
                if (workerRoles.Count == 0)
                {
                    imageBase = c.Bool() ? -1 : 0;
                }
                else
                {
                    int wanted = c.Pick(workerRoles).Base;
                    if (wanted < 0) wanted = 0;
                    int imageRoll = c.Int(1, 100);
                    if (imageRoll <= 70) imageBase = wanted;
                    else if (imageRoll <= 85) imageBase = (wanted + c.Int(1, BaseImages.Length - 1)) % BaseImages.Length;
                    else imageBase = -1;
                }

                string? image = imageBase < 0 ? null : ImageVariant(c, imageBase);
                observations[pid] = new GeneratedObservation
                {
                    Observation = new ProcessObservation { Pid = pid, Probe = probe, ImagePath = image },
                    ImageBase = imageBase
                };
                c.Log("observation " + Num(pid) + ": " + probeText + " image=" + (image == null ? "null" : "\"" + image + "\"") +
                      " base=" + Num(imageBase));
            }

            return observations;
        }

        private static void AddRole(Dictionary<int, List<(bool, long, int)>> roles, int pid, bool worker, long ticks, int imageBase)
        {
            if (pid <= 0) return;
            if (!roles.TryGetValue(pid, out var list)) roles[pid] = list = new List<(bool, long, int)>();
            list.Add((worker, ticks, imageBase));
        }

        // Independent oracle for the kill-set selector.
        private static List<GeneratedCandidate> SelectorOracle(
            List<GeneratedCandidate> generated,
            Dictionary<int, GeneratedObservation> observations,
            List<int> gatewayPids,
            SelectorTotals totals)
        {
            var ownerPids = new HashSet<int>(generated.Select(g => g.Candidate.OwnerGatewayPid).Where(p => p > 0));
            var seen = new HashSet<int>();
            var selected = new List<GeneratedCandidate>();

            foreach (var g in generated)
            {
                var k = g.Candidate;
                if (!WorkerMatches(g, observations)) { totals.RejectedMismatch++; continue; }
                if (gatewayPids.Contains(k.WorkerPid)) { totals.RejectedGatewayPid++; continue; }
                if (ownerPids.Contains(k.WorkerPid)) { totals.RejectedOwnerPid++; continue; }
                if (OwnerIsLive(k, observations)) { totals.RejectedLiveOwner++; continue; }
                if (!seen.Add(k.WorkerPid)) { totals.RejectedDuplicate++; continue; }
                selected.Add(g);
            }

            totals.Selected += selected.Count;
            return selected;
        }

        private static bool WorkerMatches(GeneratedCandidate g, Dictionary<int, GeneratedObservation> observations)
        {
            var k = g.Candidate;
            if (k.WorkerPid <= 0 || k.WorkerStartTimeUtcTicks <= 0 || g.ExpectedBase < 0) return false;
            if (!observations.TryGetValue(k.WorkerPid, out var o)) return false;
            if (o.Observation.Probe.Status != ProcessProbeStatus.Running) return false;
            if (FloorMs(o.Observation.Probe.StartTimeUtcTicks) != FloorMs(k.WorkerStartTimeUtcTicks)) return false;
            return o.ImageBase >= 0 && o.ImageBase == g.ExpectedBase;
        }

        // Not live: no positive owner PID, no observation, NotRunning, or Running with a recorded start time in a
        // different millisecond. Unreadable start time and a Running owner without a recorded start time are live.
        private static bool OwnerIsLive(OrphanCandidate k, Dictionary<int, GeneratedObservation> observations)
        {
            if (k.OwnerGatewayPid <= 0) return false;
            if (!observations.TryGetValue(k.OwnerGatewayPid, out var o)) return false;
            switch (o.Observation.Probe.Status)
            {
                case ProcessProbeStatus.NotRunning:
                    return false;
                case ProcessProbeStatus.RunningStartTimeUnreadable:
                    return true;
                default:
                    if (k.OwnerGatewayStartTimeUtcTicks <= 0) return true;
                    return FloorMs(o.Observation.Probe.StartTimeUtcTicks) == FloorMs(k.OwnerGatewayStartTimeUtcTicks);
            }
        }

        // ---------------------------------------------------------------
        // StopOrphanExact
        // ---------------------------------------------------------------

        private enum InitialProbe { NotRunning, Unreadable, Mismatch, Match }

        private enum ImageRead { Unreadable, Different, Equal }

        private enum AfterTerminate { Error, StaysRunning, PidReused, Exits }

        private static void CheckStopCase(PbtCase c, Dictionary<OrphanStopOutcome, int> outcomes)
        {
            var pids = new PidSource();
            int recoveringGatewayPid = pids.Next();
            var gatewayPids = Enumerable.Range(0, c.SmallInt(0, 3)).Select(_ => pids.Next()).ToList();

            int pidRoll = c.Int(1, 100);
            int pid;
            if (pidRoll <= 8) pid = c.Pick(new[] { 0, -1, -4 });
            else if (pidRoll <= 20) pid = recoveringGatewayPid;
            else if (pidRoll <= 32 && gatewayPids.Count > 0) pid = c.Pick(gatewayPids);
            else pid = pids.Next();

            long startTicks = c.Chance(0.06) ? c.Pick(new[] { 0L, -1L }) : GenerateTicks(c);
            int expectedBase = c.Chance(0.06) ? -1 : (c.Chance(0.85) ? 0 : c.Int(1, BaseImages.Length - 1));
            string expectedImage = expectedBase < 0 ? string.Empty : ImageVariant(c, expectedBase);

            var initial = PickWeighted(c, (InitialProbe.NotRunning, 10), (InitialProbe.Unreadable, 10), (InitialProbe.Mismatch, 15), (InitialProbe.Match, 65));
            var imageRead = PickWeighted(c, (ImageRead.Unreadable, 12), (ImageRead.Different, 15), (ImageRead.Equal, 73));
            var after = PickWeighted(c, (AfterTerminate.Error, 12), (AfterTerminate.StaysRunning, 15), (AfterTerminate.PidReused, 13), (AfterTerminate.Exits, 60));

            long reference = startTicks > 0 ? startTicks : GenerateTicks(c);
            var fake = new FakeProcesses();
            string probeText;
            switch (initial)
            {
                case InitialProbe.NotRunning:
                    probeText = "NotRunning";
                    break;
                case InitialProbe.Unreadable:
                    fake.Probes[pid] = ProcessProbeResult.RunningStartTimeUnreadable;
                    probeText = "RunningStartTimeUnreadable";
                    break;
                case InitialProbe.Mismatch:
                    long mismatched = MismatchedTicks(c, reference);
                    fake.Probes[pid] = ProcessProbeResult.Running(mismatched);
                    probeText = "Running ticks=" + Num(mismatched) + " (different millisecond)";
                    break;
                default:
                    long same = FloorMs(reference) + c.Int(0, (int)Ms - 1);
                    fake.Probes[pid] = ProcessProbeResult.Running(same);
                    probeText = "Running ticks=" + Num(same) + " (same millisecond)";
                    break;
            }

            int observedBase = imageRead switch
            {
                ImageRead.Unreadable => -1,
                ImageRead.Different => ((expectedBase < 0 ? 0 : expectedBase) + c.Int(1, BaseImages.Length - 1)) % BaseImages.Length,
                _ => expectedBase < 0 ? 0 : expectedBase
            };
            if (observedBase >= 0) fake.Images[pid] = ImageVariant(c, observedBase);

            fake.After = after;
            fake.TerminateError = "access denied (" + Num(c.Int(1, 999)) + ")";
            fake.ReusedTicks = reference + (long)c.Int(1, 86_400) * TimeSpan.TicksPerSecond;

            c.Log("recoveringGatewayPid=" + Num(recoveringGatewayPid) + " gatewayPids=[" + string.Join(",", gatewayPids.Select(Num)) + "]");
            c.Log("call: pid=" + Num(pid) + " startTicks=" + Num(startTicks) + " expectedImage=\"" + expectedImage + "\" base=" + Num(expectedBase));
            c.Log("probe: " + probeText + " image=" + (fake.Images.TryGetValue(pid, out var img) ? "\"" + img + "\"" : "unreadable") +
                  " base=" + Num(observedBase) + " afterTerminate=" + after);

            var expected = StopOracle(pid, startTicks, expectedBase, recoveringGatewayPid, gatewayPids, initial, observedBase, after);
            c.Log("expected: " + expected.Outcome + " terminate=" + expected.Terminates);

            var context = fake.Context(recoveringGatewayPid, gatewayPids);
            var result = SharedWorkerRegistry.StopOrphanExact(pid, startTicks, expectedImage, context);
            c.Log("actual: " + result.Outcome + " error=" + (result.Error ?? "null") + " events=[" + string.Join(",", fake.Events) + "]");

            outcomes[result.Outcome] = outcomes.TryGetValue(result.Outcome, out var n) ? n + 1 : 1;

            Assert.Equal(expected.Outcome, result.Outcome);
            Assert.Equal(result.Outcome == OrphanStopOutcome.Stopped, result.Stopped);

            if (expected.Terminates)
            {
                Assert.Equal(new[] { pid }, fake.Terminated.ToArray());
                int terminateAt = fake.Events.IndexOf("terminate:" + Num(pid));
                Assert.InRange(fake.Events.IndexOf("probe:" + Num(pid)), 0, terminateAt - 1);
                Assert.InRange(fake.Events.IndexOf("image:" + Num(pid)), 0, terminateAt - 1);
            }
            else
            {
                Assert.Empty(fake.Terminated);
            }

            if (expected.Outcome == OrphanStopOutcome.RefusedInvalidArguments || expected.Outcome == OrphanStopOutcome.RefusedGatewayPid)
            {
                // Refused before any probe or image read.
                Assert.Empty(fake.Events);
            }

            if (expected.Outcome == OrphanStopOutcome.KillFailed)
            {
                Assert.Contains(fake.TerminateError, result.Error ?? string.Empty);
            }
        }

        // Independent oracle for StopOrphanExact, in the contract's order of checks.
        private static (OrphanStopOutcome Outcome, bool Terminates) StopOracle(
            int pid, long startTicks, int expectedBase, int recoveringGatewayPid, List<int> gatewayPids,
            InitialProbe initial, int observedBase, AfterTerminate after)
        {
            if (pid <= 0 || startTicks <= 0 || expectedBase < 0) return (OrphanStopOutcome.RefusedInvalidArguments, false);
            if (pid == recoveringGatewayPid || gatewayPids.Contains(pid)) return (OrphanStopOutcome.RefusedGatewayPid, false);
            switch (initial)
            {
                case InitialProbe.NotRunning: return (OrphanStopOutcome.AlreadyExited, false);
                case InitialProbe.Unreadable: return (OrphanStopOutcome.RefusedStartTimeUnreadable, false);
                case InitialProbe.Mismatch: return (OrphanStopOutcome.RefusedStartTimeMismatch, false);
            }

            if (observedBase < 0) return (OrphanStopOutcome.RefusedImagePathUnreadable, false);
            if (observedBase != expectedBase) return (OrphanStopOutcome.RefusedImagePathMismatch, false);

            switch (after)
            {
                case AfterTerminate.Error: return (OrphanStopOutcome.KillFailed, true);
                case AfterTerminate.StaysRunning: return (OrphanStopOutcome.StillRunning, true);
                default: return (OrphanStopOutcome.Stopped, true);
            }
        }

        private sealed class FakeProcesses : IProcessProbe
        {
            internal Dictionary<int, ProcessProbeResult> Probes { get; } = new Dictionary<int, ProcessProbeResult>();

            internal Dictionary<int, string> Images { get; } = new Dictionary<int, string>();

            internal List<string> Events { get; } = new List<string>();

            internal List<int> Terminated { get; } = new List<int>();

            internal AfterTerminate After { get; set; }

            internal string TerminateError { get; set; } = "access denied";

            internal long ReusedTicks { get; set; }

            public ProcessProbeResult Probe(int pid)
            {
                Events.Add("probe:" + Num(pid));
                return Probes.TryGetValue(pid, out var result) ? result : ProcessProbeResult.NotRunning;
            }

            internal SharedWorkerRecoveryContext Context(int recoveringGatewayPid, IReadOnlyCollection<int> gatewayPids) =>
                new SharedWorkerRecoveryContext
                {
                    Probe = this,
                    ReadImagePath = (int pid, out string? path, out string? error) =>
                    {
                        Events.Add("image:" + Num(pid));
                        if (Images.TryGetValue(pid, out var image))
                        {
                            path = image;
                            error = null;
                            return true;
                        }

                        path = null;
                        error = "access denied for pid " + Num(pid);
                        return false;
                    },
                    Terminate = pid =>
                    {
                        Events.Add("terminate:" + Num(pid));
                        Terminated.Add(pid);
                        switch (After)
                        {
                            case AfterTerminate.Error:
                                return TerminateError;
                            case AfterTerminate.Exits:
                                Probes.Remove(pid);
                                return null;
                            case AfterTerminate.PidReused:
                                Probes[pid] = ProcessProbeResult.Running(ReusedTicks);
                                return null;
                            default:
                                return null;
                        }
                    },
                    Log = _ => { },
                    RecoveringGatewayPid = recoveringGatewayPid,
                    GatewayPids = gatewayPids,
                    StopWaitTimeout = TimeSpan.FromMilliseconds(5),
                    StopPollInterval = TimeSpan.FromMilliseconds(1),
                    RegistryRoot = null
                };
        }

        // ---------------------------------------------------------------
        // Shared generators and helpers
        // ---------------------------------------------------------------

        private sealed class PidSource
        {
            private int _next = 1000;

            // Distinct, Windows-like PIDs (multiples of 4). Collisions are generated on purpose, never by accident.
            internal int Next()
            {
                _next += 4;
                return _next;
            }
        }

        private static int? ReplaySeed() =>
            SeededInterleavingRunner.ParseSeed(Environment.GetEnvironmentVariable(SeededInterleavingRunner.SeedEnvironmentVariable));

        // Start ticks aligned to a millisecond, on its last tick, or anywhere inside it.
        private static long GenerateTicks(PbtCase c)
        {
            long ms = c.Int(0, 1_000_000_000);
            long sub = c.Pick(new[] { 0, (int)Ms - 1, c.Int(0, (int)Ms - 1) });
            return BaseTicks + TimeSpan.TicksPerDay + ms * Ms + sub;
        }

        // Observed start time for a PID with a recorded start time: same millisecond, near the boundary, or far away
        // (a reused PID).
        private static long ObservedTicks(PbtCase c, long recorded, out string kind)
        {
            int roll = c.Int(1, 100);
            if (roll <= 60)
            {
                kind = "same millisecond";
                return FloorMs(recorded) + c.Int(0, (int)Ms - 1);
            }

            if (roll <= 80)
            {
                long delta = c.Pick(new[] { -1L, 1L, -Ms, Ms, -(Ms - 1), Ms - 1, -(Ms + 1), Ms + 1 });
                long near = recorded + delta;
                kind = FloorMs(near) == FloorMs(recorded) ? "near, same millisecond" : "near, different millisecond";
                return near;
            }

            kind = "reused pid";
            return recorded + (long)c.Int(1, 86_400) * TimeSpan.TicksPerSecond;
        }

        // A start time guaranteed to be in a different millisecond from the reference.
        private static long MismatchedTicks(PbtCase c, long reference)
        {
            return c.Bool()
                ? FloorMs(reference) + Ms * c.Pick(new[] { 1L, -1L }) + c.Int(0, (int)Ms - 1)
                : reference + (long)c.Int(1, 86_400) * TimeSpan.TicksPerSecond;
        }

        // A spelling of a base path that is equal to it after normalization.
        private static string ImageVariant(PbtCase c, int baseIndex)
        {
            string path = BaseImages[baseIndex];
            switch (c.Int(0, 5))
            {
                case 0: return path;
                case 1: return path.ToUpperInvariant();
                case 2: return path.ToLowerInvariant();
                case 3: return path + "\\";
                case 4: return path + "/";
                default: return path.ToUpperInvariant() + "\\";
            }
        }

        private static T PickWeighted<T>(PbtCase c, params (T Value, int Weight)[] options)
        {
            int roll = c.Int(1, options.Sum(o => o.Weight));
            foreach (var option in options)
            {
                if (roll <= option.Weight) return option.Value;
                roll -= option.Weight;
            }

            return options[options.Length - 1].Value;
        }

        // Ticks here are positive, so subtracting the remainder truncates to the millisecond.
        private static long FloorMs(long ticks) => ticks - (ticks % Ms);

        private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
