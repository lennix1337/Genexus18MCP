// Unit tests of the SharedWorkerHostAdmission I/O wrapper alone (RFC #854, task 5.5; Requirements 6.1, 6.2, 6.4,
// 6.10 and 6.12). The runtime integration is covered by SharedWorkerHostAdmissionTests.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class SharedWorkerHostAdmissionWrapperTests
    {
        private const long StartTicks = 638100000000000000L;
        private static readonly DateTime Old = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime New = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Start_Writes_Starting_Under_The_Mutex_With_The_Cold_Start_Fields()
        {
            var w = new Wiring();
            var self = Record("self", 9000, SharedHostRecordState.Admitting);

            var result = w.Admission.Admit(self);

            Assert.Equal(SharedHostAdmissionOutcome.Start, result.Outcome);
            Assert.Equal(4096L, result.AvailableMb);
            Assert.Equal(new[] { "mutex+", "enumerate", "memory", "write:starting", "mutex-" }, w.Log);
            Assert.Equal(SharedHostRecordState.Starting, self.State);
            Assert.NotNull(self.ColdStartStartedUtc);
            Assert.Equal(2, self.ColdStartConcurrency);
            Assert.Null(self.HeldSinceUtc);
        }

        [Fact]
        public void Own_Stale_Record_Is_Dropped_And_Dead_Cold_Starts_Do_Not_Count()
        {
            var w = new Wiring(coldStartConcurrency: 1);
            var self = Record("self", 9000, SharedHostRecordState.Admitting);
            w.Others.Add(Record("self", 9000, SharedHostRecordState.Starting));
            w.Others.Add(Record("dead", 9001, SharedHostRecordState.Starting));
            w.Probe[9001] = ProcessProbeResult.NotRunning;

            Assert.Equal(SharedHostAdmissionOutcome.Start, w.Admission.Admit(self).Outcome);
            Assert.DoesNotContain("probe:9000", w.Log);
        }

        [Fact]
        public void Hold_Keeps_The_First_Held_Since_Time()
        {
            var w = new Wiring(coldStartConcurrency: 1);
            w.Others.Add(Record("other", 9001, SharedHostRecordState.Starting));
            var self = Record("self", 9000, SharedHostRecordState.Admitting);

            Assert.Equal(SharedHostAdmissionOutcome.Hold, w.Admission.Admit(self).Outcome);
            DateTime first = self.HeldSinceUtc.Value;
            Thread.Sleep(5);
            var again = w.Admission.Admit(self);

            Assert.Equal(SharedHostAdmissionOutcome.Hold, again.Outcome);
            Assert.Equal(1, again.Decision.Position);
            Assert.Equal(first, self.HeldSinceUtc.Value);
            Assert.Equal(SharedHostRecordState.Held, self.State);
        }

        [Fact]
        public void Low_Memory_Refuses_With_The_Measured_Value_And_The_Floor()
        {
            var w = new Wiring { MemoryMb = 900 };
            var self = Record("self", 9000, SharedHostRecordState.Admitting);

            var result = w.Admission.Admit(self);

            Assert.Equal(SharedHostAdmissionOutcome.Refuse, result.Outcome);
            Assert.Equal(SharedHostRecordState.Refused, self.State);
            Assert.Equal(ConcurrencyErrorCatalog.KbPoolFull, self.Refusal.Code);
            Assert.Equal("low-memory", self.Refusal.Reason);
            Assert.Equal(900L, self.Refusal.AvailableMb);
            Assert.Equal(1024L, self.Refusal.FloorMb);
        }

        [Fact]
        public void Unreadable_Memory_Warns_Once_And_Passes_The_Floor()
        {
            var w = new Wiring { MemoryFailure = "reader broke" };
            var self = Record("self", 9000, SharedHostRecordState.Admitting);

            var result = w.Admission.Admit(self);

            Assert.Equal(SharedHostAdmissionOutcome.Start, result.Outcome);
            Assert.Null(result.AvailableMb);
            Assert.Contains("reader broke", Assert.Single(w.Warnings), StringComparison.Ordinal);
        }

        [Fact]
        public void Evict_Writes_Admitting_And_Signals_The_Victim_Under_The_Mutex()
        {
            var w = new Wiring(maxWorkers: 1);
            var victim = Record("victim", 9001, SharedHostRecordState.Ready);
            victim.WorkerPid = 7001;
            victim.Evictable = true;
            w.Others.Add(victim);
            var self = Record("self", 9000, SharedHostRecordState.Admitting);

            var result = w.Admission.Admit(self);

            Assert.Equal(SharedHostAdmissionOutcome.Evict, result.Outcome);
            Assert.Equal(SharedHostRecordState.Admitting, self.State);
            int signal = w.Log.IndexOf("signal:" + SharedWorkerHostAdmission.EvictEventName("victim"));
            Assert.InRange(signal, w.Log.IndexOf("write:admitting") + 1, w.Log.IndexOf("mutex-") - 1);
        }

        [Fact]
        public void Contended_Reads_And_Writes_Nothing()
        {
            var w = new Wiring { Contended = true };
            var result = w.Admission.Admit(Record("self", 9000, SharedHostRecordState.Admitting));

            Assert.Equal(SharedHostAdmissionOutcome.Contended, result.Outcome);
            Assert.Null(result.Decision);
            Assert.Empty(w.Log);
        }

        [Fact]
        public void End_Cold_Start_Wakes_The_Oldest_Live_Held_Record_Only()
        {
            var w = new Wiring();
            var newer = Record("b-newer", 9001, SharedHostRecordState.Held);
            newer.HeldSinceUtc = New;
            var older = Record("c-older", 9002, SharedHostRecordState.Held);
            older.HeldSinceUtc = Old;
            var deadOldest = Record("a-dead", 9003, SharedHostRecordState.Held);
            deadOldest.HeldSinceUtc = Old.AddDays(-1);
            w.Probe[9003] = ProcessProbeResult.NotRunning;
            w.Others.AddRange(new[] { newer, older, deadOldest });
            var self = Record("self", 9000, SharedHostRecordState.Ready);

            w.Admission.EndColdStart(self);

            Assert.Equal(new[] { "mutex+", "write:ready", "signal:" + SharedWorkerHostAdmission.WakeEventName("c-older"), "mutex-" },
                w.Log.Where(e => !e.StartsWith("probe:", StringComparison.Ordinal) && e != "enumerate"));
        }

        [Fact]
        public void End_Cold_Start_Without_A_Held_Record_Signals_Nothing()
        {
            var w = new Wiring();
            w.Admission.EndColdStart(Record("self", 9000, SharedHostRecordState.Ready));
            Assert.DoesNotContain(w.Log, e => e.StartsWith("signal:", StringComparison.Ordinal));
        }

        [Fact]
        public void Named_Event_Hub_Delivers_A_Signal_To_A_Subscriber()
        {
            var hub = new SharedHostNamedEventHub();
            string name = @"Local\GxMcp.SharedHost.v2.Wake.test-" + Guid.NewGuid().ToString("N");
            using (var fired = new ManualResetEventSlim(false))
            using (hub.Subscribe(name, () => fired.Set()))
            {
                hub.Signal(name);
                Assert.True(fired.Wait(5000), "the subscriber must observe the signal");
            }
        }

        [Fact]
        public void Machine_Memory_Reader_Returns_A_Positive_Reading()
        {
            Assert.True(new SharedHostMachineMemoryReader().TryReadAvailableMb(out long mb, out string error), error);
            Assert.True(mb > 0);
        }

        [Fact]
        public void Unrestricted_Seams_Always_Start()
        {
            var settings = Settings(maxWorkers: 1, coldStartConcurrency: 1);
            var log = new List<string>();
            var admission = new SharedWorkerHostAdmission(settings, SharedHostSystemClock.Instance, new Mutexes(log, () => false),
                new Writer(log), SharedHostAdmissionSeams.Unrestricted);

            Assert.Equal(SharedHostAdmissionOutcome.Start, admission.Admit(Record("self", 9000, SharedHostRecordState.Admitting)).Outcome);
        }

        private static SharedHostSettingsResult Settings(int maxWorkers = 4, int coldStartConcurrency = 2, int memoryFloorMb = 1024) =>
            new SharedHostSettingsResult(600000, 8, 4, maxWorkers, memoryFloorMb, coldStartConcurrency, 120,
                new Dictionary<string, string>(), new List<string>());

        private static SharedHostRecord Record(string key, int hostPid, string state) => new SharedHostRecord
        {
            KbKey = key,
            HostPid = hostPid,
            HostStartTimeUtcTicks = StartTicks,
            PipeName = "GxMcpShared2_" + key,
            State = state
        };

        private sealed class Wiring : ISharedHostRegistryEnumerator, ISharedHostMemoryReader, IProcessProbe, ISharedHostSignalHub
        {
            internal Wiring(int maxWorkers = 4, int coldStartConcurrency = 2)
            {
                var seams = new SharedHostAdmissionSeams
                {
                    Registry = this,
                    HostProbe = this,
                    Memory = this,
                    Signals = this,
                    Warn = Warnings.Add
                };
                Admission = new SharedWorkerHostAdmission(Settings(maxWorkers, coldStartConcurrency), SharedHostSystemClock.Instance,
                    new Mutexes(Log, () => Contended), new Writer(Log), seams);
            }

            internal SharedWorkerHostAdmission Admission { get; }
            internal List<string> Log { get; } = new List<string>();
            internal List<string> Warnings { get; } = new List<string>();
            internal List<SharedHostRecord> Others { get; } = new List<SharedHostRecord>();
            internal Dictionary<int, ProcessProbeResult> Probe { get; } = new Dictionary<int, ProcessProbeResult>();
            internal long MemoryMb { get; set; } = 4096;
            internal string MemoryFailure { get; set; }
            internal bool Contended { get; set; }

            public IReadOnlyList<RecordReadResult> ReadAll()
            {
                Log.Add("enumerate");
                return Others.Select(r => RecordReadResult.FromJson(r.ToJson())).ToList();
            }

            public bool TryReadAvailableMb(out long availableMb, out string error)
            {
                Log.Add("memory");
                availableMb = MemoryFailure == null ? MemoryMb : 0;
                error = MemoryFailure;
                return MemoryFailure == null;
            }

            ProcessProbeResult IProcessProbe.Probe(int pid)
            {
                Log.Add("probe:" + pid);
                return Probe.TryGetValue(pid, out var result) ? result : ProcessProbeResult.Running(StartTicks);
            }

            public void Signal(string name) => Log.Add("signal:" + name);

            public IDisposable Subscribe(string name, Action onSignal) => throw new NotSupportedException();
        }

        private sealed class Mutexes : ISharedHostMutexFactory
        {
            private readonly List<string> _log;
            private readonly Func<bool> _contended;

            internal Mutexes(List<string> log, Func<bool> contended)
            {
                _log = log;
                _contended = contended;
            }

            public IDisposable TryAcquire(string name)
            {
                Assert.Equal(SharedWorkerHostAdmission.AdmissionMutexName, name);
                if (_contended()) return null;
                _log.Add("mutex+");
                return new Release(_log);
            }

            private sealed class Release : IDisposable
            {
                private readonly List<string> _log;
                internal Release(List<string> log) { _log = log; }
                public void Dispose() => _log.Add("mutex-");
            }
        }

        private sealed class Writer : ISharedHostRecordWriter
        {
            private readonly List<string> _log;
            internal Writer(List<string> log) { _log = log; }
            public void Write(SharedHostRecord record) => _log.Add("write:" + record.State);
            public void Delete() { }
        }
    }
}
