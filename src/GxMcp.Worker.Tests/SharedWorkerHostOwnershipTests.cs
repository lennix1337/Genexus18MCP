// Host-side ownership tests for the shared-host Worker_Host (RFC #854, task 4.8; Requirements 2.2,
// 4.1 and 7.5; design decisions A3 and A4, "Startup" and "Detach and pipe EOF").
//
// These tests are written before the implementation (task 4.12). They fix the seam contract below;
// task 4.12 implements exactly these types, members and names. Every seam is internal (GxMcp.Worker
// grants InternalsVisibleTo("GxMcp.Worker.Tests")). The seams of task 3.5 (header of
// SharedWorkerHostSchedulerWiringTests.cs) are reused unchanged; the fakes below are private to this
// file on purpose.
//
// ---- kbKey (decision A3), shared by the Gateway (task 4.9) and the host ----
//
//   New file src/GxMcp.Shared/SharedHost/SharedHostKbKey.cs, namespace GxMcp.Common.SharedHost
//   (already linked into GxMcp.Worker, GxMcp.Gateway and GxMcp.SharedHost.TestHost by the
//   SharedHost\*.cs glob):
//
//   internal static class SharedHostKbKey
//       internal static string NormalizeKbPath(string kbPath)
//       internal static string Compute(string kbPath)      // ArgumentException on null or whitespace
//
//   Normalization rule, identical to the KB component of SharedWorkerIdentity.NormalizePath (Gateway)
//   and SharedWorkerHostProtocol.NormalizePath (Worker):
//     1. normalized = Path.GetFullPath(kbPath)
//     2. remove every trailing '\' and '/' (TrimEnd)
//     3. ToLowerInvariant()
//     If Path.GetFullPath throws, use kbPath.Trim() and apply steps 2 and 3.
//   kbKey = lowercase hexadecimal SHA-256 of the UTF-8 bytes (no BOM, no terminator) of the
//   normalized path: 64 characters [0-9a-f]. Known answer: "c:\kbs\kbteste" ->
//   717f20e76c7a9966c16cbba1019e30fc89e4648fde629b0ae6062e504fe0b504. Paths that differ only by
//   letter case, trailing separators, '/' versus '\', "..\" segments, or relative versus absolute
//   form give one kbKey (Requirement 2 criterion 1).
//
// ---- New seams (namespace GxMcp.Worker, file SharedWorkerHostSeams.cs) ----
//
//   internal interface ISharedHostMutexFactory
//       IDisposable TryAcquire(string name);
//     Takes ownership of the named mutex without waiting. Returns the held mutex, or null when
//     another owner holds it. An abandoned mutex (AbandonedMutexException) counts as acquired.
//     Disposing the returned handle releases the mutex; the handle may be disposed from any
//     thread (the production handle owns the thread affinity of System.Threading.Mutex).
//     Production: internal sealed class SharedHostNamedMutexFactory : ISharedHostMutexFactory,
//     backed by System.Threading.Mutex.
//
//   internal interface ISharedHostRecordWriter
//       void Write(SharedHostRecord record);     // registry v2 record (GxMcp.Common.SharedHost)
//       void Delete();
//     A new interface rather than a change to ISharedHostRegistryWriter: four PR A test files
//     implement ISharedHostRegistryWriter.Write(SharedWorkerHostRegistryRecord), and changing that
//     member would break them. Production: temporary file plus File.Replace at
//     ResolveRecordPathV2(options), as SharedHostFileRegistryWriter does today.
//
//   Gateway liveness uses the existing IProcessProbe / ProcessProbeResult from
//   src/GxMcp.Shared/SharedHost/SharedHostRecordClassifier.cs. Production: ProcessIdentity.
//
// ---- Changed or new members ----
//
//   SharedWorkerHostOptions.KbKey { get; }                  // SharedHostKbKey.Compute(--kb)
//   SharedWorkerHostOptions.PipeName                        // default "GxMcpShared2_" + IdentityKey
//   SharedWorkerHostProtocol.TryBuildPipeName / IsValidPipeName use the prefix "GxMcpShared2_"
//     only (decision A4); a "GxMcpShared_" name is invalid, so Parse rejects a v1 --pipe-name.
//   internal static string SharedWorkerHostRuntime.ResolveRecordPathV2(SharedWorkerHostOptions o)
//     = Path.Combine(dir, o.KbKey + ".json"), where dir is o.RegistryDirectory when set, else
//     %LOCALAPPDATA%\GxMcp\shared-workers\v2 (Environment.SpecialFolder.LocalApplicationData).
//
//   internal SharedWorkerHostRuntime(
//       SharedWorkerHostOptions options,
//       SharedHostSettingsResult settings,
//       ISharedHostClock clock,
//       ISharedHostChildLauncher childLauncher,
//       ISharedHostRecordWriter recordWriter,
//       ISharedHostMutexFactory mutexFactory,
//       IProcessProbe gatewayProbe,
//       bool runBackgroundThreads)
//     The one-argument production constructor delegates to it with SharedHostSystemClock,
//     SharedHostProcessLauncher, the v2 file writer, SharedHostNamedMutexFactory and
//     ProcessIdentity. The task 3.5 six-argument constructor keeps compiling and keeps its
//     observable behavior for the PR A tests: it projects each v2 write onto the given
//     ISharedHostRegistryWriter as a SharedWorkerHostRegistryRecord (same State, PIDs, start
//     times, pipe name, generation), uses a mutex factory that always grants, and a probe that
//     reports every Gateway as running with its recorded start time. No constructor acquires a
//     mutex: SharedWorkerSupervisionTests constructs production runtimes without Start.
//
//   internal bool Start()
//     Order (design "Startup"): (1) mutexFactory.TryAcquire(
//     "Local\GxMcp.SharedWorker.v2.Host." + options.KbKey). On null it returns false and does
//     nothing else: no record write, no record delete, no pipe, no child, no timer. (2) Write the
//     v2 record with state "admitting", workerPid 0 and workerStartTimeUtcTicks 0, schema 2, kbKey,
//     identity (normalized components), hostPid and hostStartTimeUtcTicks of this process, and
//     pipeName. (3) With background threads, start the pipe accept loop. (4) Admission (PR C), then
//     start the child through the launcher; the record then carries the child's PID. (5) Arm the
//     supervisor timer. Returns true. Statement callers (PR A tests) are unaffected.
//   internal const int HostMutexHeldExitCode = 3
//     Run() returns it at once when Start() returns false; it never waits in that case.
//   internal bool IsStopRequested { get; }
//     True once Dispose ran or the supervisor tick found the idle timeout expired. Run's loop ends
//     when it becomes true.
//   Dispose releases the host-lifetime mutex handle (when acquired) and deletes the record only
//     when this runtime wrote it.
//
// ---- Supervisor tick (design "Detach and pipe EOF", "Idle reaping"; Requirement 7.5) ----
//
//   The runtime creates one supervisor timer through ISharedHostClock.CreateTimer. Start arms it
//   at NowMs + 5000; each tick re-arms it at NowMs + 5000. The timer is the only tick source in
//   both modes (with runBackgroundThreads=false the test drives it through the fake clock). A tick:
//     - probes the Gateway PID recorded from each Attachment's attach frame (gatewayPid,
//       gatewayStartTimeUtcTicks) with gatewayProbe. NotRunning, or Running with a start time
//       that differs at millisecond precision (SharedHostRecordClassifier.SameMillisecond), is a
//       dead Gateway: the tick calls Detach(attachment) and then attachment.Stop(). Detach is
//       idempotent; a later transport Detach of the same Attachment does not move the idle start.
//       RunningStartTimeUnreadable keeps the Attachment (fail closed, as Requirement 7.8).
//     - applies the idle rule with ISharedHostClock.NowMs, never DateTime.UtcNow: no Attachment,
//       no in-flight or queued request, no background activity, and at least --idle-ms since the
//       last detachment. The idle start is the moment of the last detachment, not the attach,
//       the construction or the Gateway's exit (Requirement 7.5). Expiry sets IsStopRequested.
//   A Gateway that dies just after a tick is detached by the next one, 5000 ms later, which is
//   within the 10 seconds of Requirement 7.5.
//
// ---- Existing tests this contract conflicts with (not modified here; resolve in 4.11/4.12) ----
//
//   - src/GxMcp.Gateway.Tests/SharedHost/SharedHostQueueProcessSmokeTests.cs passes
//     --pipe-name SharedWorkerRegistry.PipeName(identity) ("GxMcpShared_" prefix) and waits for the
//     v1 record <registryDir>\<identityKey>.json read as SharedWorkerRecord. Under this contract
//     the host rejects that pipe name and writes <registryDir>\<kbKey>.json in the v2 shape.
//   - src/GxMcp.Gateway.Tests/SharedWorkerHostLauncherArgumentsTests.cs asserts pipe-name equals
//     SharedWorkerRegistry.PipeName(identity). It stays green only if the launcher and that
//     function move to "GxMcpShared2_" together.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class SharedWorkerHostOwnershipTests
    {
        private const string HostMutexPrefix = @"Local\GxMcp.SharedWorker.v2.Host.";
        private const string V2PipePrefix = "GxMcpShared2_";
        private const string V1PipePrefix = "GxMcpShared_";
        private const int TickMs = 5000;
        private const long GatewayStartTicks = 638000000000000000L;

        // ---- kbKey (decision A3, Requirement 2 criterion 1) ----

        [Fact]
        public void Kb_Key_Matches_The_Known_Answer_For_A_Normalized_Path()
        {
            Assert.Equal("c:\\kbs\\kbteste", SharedHostKbKey.NormalizeKbPath(@"C:\KBs\KbTeste\"));
            Assert.Equal(
                "717f20e76c7a9966c16cbba1019e30fc89e4648fde629b0ae6062e504fe0b504",
                SharedHostKbKey.Compute(@"c:\kbs\kbteste"));
        }

        [Theory]
        [InlineData(@"C:\KBs\KbTeste")]
        [InlineData(@"c:\kbs\kbteste")]
        [InlineData(@"C:\KBs\KbTeste\")]
        [InlineData(@"C:\KBs\KbTeste\\")]
        [InlineData("C:/KBs/KbTeste/")]
        [InlineData(@"C:\KBs\Other\..\KbTeste")]
        public void Kb_Path_Variants_Resolve_To_One_Kb_Key(string variant)
        {
            Assert.Equal(ExpectedKbKey(@"c:\kbs\kbteste"), SharedHostKbKey.Compute(variant));
        }

        [Fact]
        public void A_Relative_Kb_Path_Resolves_To_The_Kb_Key_Of_Its_Absolute_Form()
        {
            string relative = "KbRelativeTeste";
            string absolute = Path.Combine(Environment.CurrentDirectory, relative);

            Assert.Equal(SharedHostKbKey.Compute(absolute), SharedHostKbKey.Compute(relative));
            Assert.Equal(ExpectedKbKey(absolute), SharedHostKbKey.Compute(relative));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_Missing_Kb_Path_Has_No_Kb_Key(string kbPath)
        {
            Assert.Throws<ArgumentException>(() => SharedHostKbKey.Compute(kbPath));
        }

        // ---- Pipe prefix and record path (decision A4) ----

        [Fact]
        public void Options_Carry_The_Kb_Key_And_A_V2_Pipe_Name()
        {
            string kb = UniqueKbPath();
            var options = ParseOptions(kb);

            Assert.Equal(ExpectedKbKey(kb), options.KbKey);
            Assert.Equal(V2PipePrefix + options.IdentityKey, options.PipeName);
            Assert.True(SharedWorkerHostProtocol.TryBuildPipeName(options.IdentityKey, out string built, out string error), error);
            Assert.Equal(V2PipePrefix + options.IdentityKey, built);
        }

        [Fact]
        public void The_V1_Pipe_Prefix_Is_Not_A_Valid_Host_Pipe_Name()
        {
            var options = ParseOptions(UniqueKbPath());

            Assert.True(SharedWorkerHostProtocol.IsValidPipeName(V2PipePrefix + options.IdentityKey));
            Assert.False(SharedWorkerHostProtocol.IsValidPipeName(V1PipePrefix + options.IdentityKey));
        }

        [Fact]
        public void The_Record_Path_Is_The_Kb_Key_Under_The_V2_Registry_Directory()
        {
            string kb = UniqueKbPath();
            string key = ExpectedKbKey(kb);

            string defaultPath = SharedWorkerHostRuntime.ResolveRecordPathV2(ParseOptions(kb));
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GxMcp", "shared-workers", "v2", key + ".json"),
                defaultPath);

            string overridden = SharedWorkerHostRuntime.ResolveRecordPathV2(ParseOptions(kb, "--registry-dir", @"C:\Temp\gxmcp-registry-teste"));
            Assert.Equal(Path.Combine(@"C:\Temp\gxmcp-registry-teste", key + ".json"), overridden);
        }

        // ---- Host-lifetime mutex (decision A3, Requirement 4 criterion 1) ----

        [Fact]
        public void Start_Takes_The_Host_Lifetime_Mutex_Before_Any_Record_Or_Child()
        {
            using (var h = new Harness())
            {
                Assert.True(h.Runtime.Start());

                var request = Assert.Single(h.Mutexes.Requests);
                Assert.Equal(HostMutexPrefix + ExpectedKbKey(h.KbPath), request.Name);
                Assert.Equal(0, request.RecordsWrittenBefore);
                Assert.Equal(0, request.ChildrenStartedBefore);

                var handle = Assert.Single(h.Mutexes.Granted);
                Assert.False(handle.Disposed, "the host-lifetime mutex must be held while the host runs");

                h.Runtime.Dispose();
                Assert.True(handle.Disposed, "Dispose must release the host-lifetime mutex");
            }
        }

        [Fact]
        public void A_Held_Host_Lifetime_Mutex_Stops_Startup_Without_Side_Effects()
        {
            using (var h = new Harness())
            {
                h.Mutexes.HeldElsewhere.Add(HostMutexPrefix + ExpectedKbKey(h.KbPath));

                Assert.False(h.Runtime.Start());

                Assert.Single(h.Mutexes.Requests);
                Assert.Empty(h.Mutexes.Granted);
                Assert.Empty(h.Launcher.Started);
                Assert.Empty(h.Records.Snapshots);

                // No timer was armed: nothing runs on later ticks.
                h.AdvanceTicks(3);
                Assert.Empty(h.Launcher.Started);
                Assert.Empty(h.Records.Snapshots);

                // The live owner's record is never deleted by the host that lost.
                h.Runtime.Dispose();
                Assert.Equal(0, h.Records.Deletes);
            }
        }

        [Fact]
        public async Task Run_Exits_At_Startup_When_The_Host_Lifetime_Mutex_Is_Held()
        {
            using (var h = new Harness())
            {
                h.Mutexes.HeldElsewhere.Add(HostMutexPrefix + ExpectedKbKey(h.KbPath));

                // Run never waits when Start fails; the bound only keeps a wrong implementation from
                // hanging the suite.
                var run = Task.Run(() => h.Runtime.Run());
                Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(ReferenceEquals(finished, run), "Run must return at once when the host-lifetime mutex is held");

                int exitCode = await run;
                Assert.Equal(SharedWorkerHostRuntime.HostMutexHeldExitCode, exitCode);
                Assert.NotEqual(0, exitCode);
                Assert.Empty(h.Launcher.Started);
                Assert.Empty(h.Records.Snapshots);
            }
        }

        [Fact]
        public void A_Real_Host_Lifetime_Mutex_Held_By_Another_Owner_Blocks_Startup_Until_Released()
        {
            string kb = UniqueKbPath();
            string name = HostMutexPrefix + ExpectedKbKey(kb);

            using (var holder = new MutexHolder(name))
            using (var blocked = new Harness(kb, mutexFactory: new SharedHostNamedMutexFactory()))
            {
                Assert.False(blocked.Runtime.Start());
                Assert.Empty(blocked.Launcher.Started);
                Assert.Empty(blocked.Records.Snapshots);
            }

            using (var owner = new Harness(kb, mutexFactory: new SharedHostNamedMutexFactory()))
            {
                Assert.True(owner.Runtime.Start());
                Assert.False(CanAcquireOnAnotherThread(name), "a running host must hold its host-lifetime mutex");

                owner.Runtime.Dispose();
                Assert.True(CanAcquireOnAnotherThread(name), "Dispose must release the host-lifetime mutex");
            }
        }

        [Fact]
        public void An_Abandoned_Host_Lifetime_Mutex_Is_Acquired()
        {
            string kb = UniqueKbPath();
            string name = HostMutexPrefix + ExpectedKbKey(kb);

            // A host that crashed: its thread ends while it owns the mutex.
            Mutex abandoned = null;
            bool owned = false;
            var crashed = new Thread(() =>
            {
                abandoned = new Mutex(false, name);
                owned = abandoned.WaitOne(0);
            }) { IsBackground = true };
            crashed.Start();
            Assert.True(crashed.Join(TimeSpan.FromSeconds(10)));
            Assert.True(owned, "the crashed owner did not take the mutex");

            try
            {
                using (var h = new Harness(kb, mutexFactory: new SharedHostNamedMutexFactory()))
                {
                    Assert.True(h.Runtime.Start());
                    Assert.Single(h.Launcher.Started);
                }
            }
            finally
            {
                abandoned?.Dispose();
            }
        }

        // ---- Record before the child (design "Startup") ----

        [Fact]
        public void The_Admitting_Record_Is_Written_Before_The_Child_Exists()
        {
            using (var h = new Harness())
            {
                Assert.True(h.Runtime.Start());

                var launch = Assert.Single(h.Launcher.Started);
                Assert.NotEmpty(launch.RecordsAtLaunch);
                Assert.All(launch.RecordsAtLaunch, r => Assert.Equal(0, (int)r["workerPid"]));

                var admitting = launch.RecordsAtLaunch.First();
                Assert.Equal(SharedHostRecordState.Admitting, (string)admitting["state"]);
                Assert.Equal(SharedHostRecord.CurrentSchema, (int)admitting["schema"]);
                Assert.Equal(0L, (long)admitting["workerStartTimeUtcTicks"]);
                Assert.Equal(ExpectedKbKey(h.KbPath), (string)admitting["kbKey"]);
                Assert.Equal(SharedHostKbKey.NormalizeKbPath(h.KbPath), (string)admitting["identity"]["kbPath"]);
                Assert.Equal(h.Options.IdentityKey, (string)admitting["identity"]["key"]);
                Assert.Equal(V2PipePrefix + h.Options.IdentityKey, (string)admitting["pipeName"]);

                using (var self = Process.GetCurrentProcess())
                {
                    Assert.Equal(self.Id, (int)admitting["hostPid"]);
                    Assert.True(SharedHostRecordClassifier.SameMillisecond(
                        self.StartTime.ToUniversalTime().Ticks, (long)admitting["hostStartTimeUtcTicks"]),
                        "the record must carry this host's start time");
                }

                // Once the child exists, the record names it.
                var last = h.Records.Snapshots.Last();
                Assert.Equal(launch.Pid, (int)last["workerPid"]);
                Assert.Equal(SharedHostRecordState.Starting, (string)last["state"]);
            }
        }

        // ---- Gateway liveness on the supervisor tick (Requirement 7 criterion 5) ----

        [Fact]
        public void A_Live_Gateway_Attachment_Survives_Supervisor_Ticks()
        {
            using (var h = new Harness())
            {
                h.StartReady();
                var a = h.Attach(4242);

                h.AdvanceTicks(6);

                Assert.False(a.Stopped);
                Assert.Contains(4242, h.Probe.Calls);
                Assert.False(h.Runtime.IsStopRequested);
            }
        }

        [Theory]
        [InlineData("not-running")]
        [InlineData("start-time-mismatch")]
        public void A_Dead_Gateway_Attachment_Is_Detached_By_The_Next_Supervisor_Tick(string death)
        {
            using (var h = new Harness())
            {
                h.StartReady();
                long startedAtMs = h.Clock.NowMs;
                var a = h.Attach(4242);
                var b = h.Attach(4343);

                h.Clock.Advance(2000);
                long aDiedAtMs = h.Clock.NowMs;
                h.KillGateway(4242, death);

                // The first tick is due 5000 ms after Start, not earlier.
                h.Clock.Advance(startedAtMs + TickMs - 1 - h.Clock.NowMs);
                Assert.False(a.Stopped);

                h.Clock.Advance(1);
                Assert.True(a.Stopped, "the tick must detach an Attachment whose Gateway is gone");
                Assert.InRange(a.StoppedAtMs - aDiedAtMs, 0, 10000);
                Assert.False(b.Stopped, "a live Gateway's Attachment must stay attached");

                // A Gateway that dies just after a tick is detached by the next one.
                long bDiedAtMs = h.Clock.NowMs;
                h.KillGateway(4343, death);
                h.AdvanceTicks(1);
                Assert.True(b.Stopped);
                Assert.InRange(b.StoppedAtMs - bDiedAtMs, 0, 10000);
            }
        }

        [Fact]
        public void A_Gateway_Whose_Start_Time_Cannot_Be_Read_Stays_Attached()
        {
            using (var h = new Harness())
            {
                h.StartReady();
                var a = h.Attach(4242);
                h.Probe.Results[4242] = ProcessProbeResult.RunningStartTimeUnreadable;

                h.AdvanceTicks(3);

                Assert.False(a.Stopped, "an unverifiable Gateway is treated as live (fail closed)");
            }
        }

        [Fact]
        public void The_Idle_Timeout_Starts_At_The_Detachment_Of_A_Dead_Gateway()
        {
            const int idleMs = 20000;
            using (var h = new Harness(idleMs: idleMs))
            {
                h.StartReady();
                var a = h.Attach(4242);

                // Longer than the idle timeout, attached: no reaping.
                h.AdvanceTicks(6);
                Assert.False(h.Runtime.IsStopRequested);

                h.KillGateway(4242, "not-running");
                h.AdvanceTicks(1);
                Assert.True(a.Stopped);
                long detachedAtMs = a.StoppedAtMs;
                // Measured from the attach or the construction, the timeout would already be over.
                Assert.False(h.Runtime.IsStopRequested, "the idle timeout must start at the detachment");

                h.AdvanceTicks(3);
                Assert.Equal(detachedAtMs + 3 * TickMs, h.Clock.NowMs);
                Assert.False(h.Runtime.IsStopRequested, "the idle timeout has not elapsed since the detachment");

                h.AdvanceTicks(2);
                Assert.True(h.Runtime.IsStopRequested, "the idle timeout elapsed since the detachment; the tick must reap the host");
            }
        }

        // ---- Helpers ----

        private static string ExpectedKbKey(string kbPath)
        {
            string normalized = Path.GetFullPath(kbPath).TrimEnd('\\', '/').ToLowerInvariant();
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(new UTF8Encoding(false).GetBytes(normalized));
                var builder = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        // A distinct KB path per test, so real named mutexes never collide across tests or runs.
        // The path is never created on disk.
        private static string UniqueKbPath() => @"C:\KBs\OwnershipTeste_" + Guid.NewGuid().ToString("N");

        private static SharedWorkerHostOptions ParseOptions(string kbPath, params string[] extra)
        {
            var args = new List<string>
            {
                "--shared-host",
                "--worker-executable", @"C:\GeneXus\GxMcp.Worker.exe",
                "--kb", kbPath,
                "--installation", @"C:\GeneXus",
                "--driver", "native",
                "--major", "18"
            };
            args.AddRange(extra);
            return SharedWorkerHostOptions.Parse(args.ToArray());
        }

        private static bool CanAcquireOnAnotherThread(string name)
        {
            bool acquired = false;
            var thread = new Thread(() =>
            {
                using (var mutex = new Mutex(false, name))
                {
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (acquired) mutex.ReleaseMutex();
                }
            }) { IsBackground = true };
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            return acquired;
        }

        // Holds a real named mutex on its own thread until disposed (a second host on this machine).
        private sealed class MutexHolder : IDisposable
        {
            private readonly ManualResetEventSlim _acquired = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            private readonly Thread _thread;

            internal MutexHolder(string name)
            {
                _thread = new Thread(() =>
                {
                    using (var mutex = new Mutex(false, name))
                    {
                        mutex.WaitOne();
                        _acquired.Set();
                        _release.Wait();
                        mutex.ReleaseMutex();
                    }
                }) { IsBackground = true };
                _thread.Start();
                Assert.True(_acquired.Wait(TimeSpan.FromSeconds(10)), "the holder thread did not take the mutex");
            }

            public void Dispose()
            {
                _release.Set();
                _thread.Join(TimeSpan.FromSeconds(10));
                _acquired.Dispose();
                _release.Dispose();
            }
        }

        private sealed class Harness : IDisposable
        {
            internal Harness(string kbPath = null, int idleMs = 120000, ISharedHostMutexFactory mutexFactory = null)
            {
                KbPath = kbPath ?? UniqueKbPath();
                Options = ParseOptions(KbPath, "--idle-ms", idleMs.ToString(CultureInfo.InvariantCulture));
                var settings = new SharedHostSettingsResult(
                    30000, 8, 4, 4, 1024, 1, 120,
                    new Dictionary<string, string>(), new List<string>());
                Launcher = new FakeChildLauncher(Records);
                Mutexes = new FakeMutexFactory(Records, Launcher);
                Runtime = new SharedWorkerHostRuntime(
                    Options, settings, Clock, Launcher, Records, mutexFactory ?? Mutexes, Probe, runBackgroundThreads: false);
            }

            internal string KbPath { get; }
            internal SharedWorkerHostOptions Options { get; }
            internal FakeClock Clock { get; } = new FakeClock();
            internal FakeRecordWriter Records { get; } = new FakeRecordWriter();
            internal FakeChildLauncher Launcher { get; }
            internal FakeMutexFactory Mutexes { get; }
            internal FakeProcessProbe Probe { get; } = new FakeProcessProbe();
            internal SharedWorkerHostRuntime Runtime { get; }

            internal void StartReady()
            {
                Assert.True(Runtime.Start());
                Runtime.OnChildLine(Launcher.Started.Last(), new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/worker/sdk_ready"
                }.ToString(Formatting.None));
            }

            internal FakeAttachment Attach(int gatewayPid)
            {
                Probe.Results[gatewayPid] = ProcessProbeResult.Running(GatewayStartTicks);
                var attachment = new FakeAttachment(Runtime, Clock);
                string frame = new JObject
                {
                    ["type"] = "attach",
                    ["gxmcp"] = "attach",
                    ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["version"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["identityKey"] = Options.IdentityKey,
                    ["key"] = Options.IdentityKey,
                    ["gatewayPid"] = gatewayPid,
                    ["gatewayStartTimeUtcTicks"] = GatewayStartTicks,
                    ["attachNonce"] = Guid.NewGuid().ToString("N")
                }.ToString(Formatting.None);
                Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame, out SharedWorkerEnvelope envelope, out string parseError), parseError);
                Assert.True(Runtime.Attach(attachment, envelope, out string error), error);
                return attachment;
            }

            internal void KillGateway(int gatewayPid, string death)
            {
                Probe.Results[gatewayPid] = death == "not-running"
                    ? ProcessProbeResult.NotRunning
                    // The PID was reused by a process that started a day later.
                    : ProcessProbeResult.Running(GatewayStartTicks + TimeSpan.TicksPerDay);
            }

            internal void AdvanceTicks(int ticks)
            {
                for (int i = 0; i < ticks; i++) Clock.Advance(TickMs);
            }

            public void Dispose() => Runtime.Dispose();
        }

        // Monotonic clock under test control, the same semantics as the task 3.5 fake: Advance moves
        // the time and runs, once each, the callbacks of the timers due at or before the new time,
        // earliest first, on the test thread. A timer re-armed at or before the current time runs on
        // the next Advance.
        private sealed class FakeClock : ISharedHostClock
        {
            private readonly List<FakeTimer> _timers = new List<FakeTimer>();

            public long NowMs { get; private set; } = 1000;

            public ISharedHostTimer CreateTimer(Action callback)
            {
                var timer = new FakeTimer(callback);
                _timers.Add(timer);
                return timer;
            }

            internal void Advance(long ms)
            {
                NowMs += ms;
                var due = _timers
                    .Where(t => !t.Disposed && t.DueAtMs.HasValue && t.DueAtMs.Value <= NowMs)
                    .OrderBy(t => t.DueAtMs.Value)
                    .ToList();
                foreach (var timer in due)
                {
                    if (timer.Disposed || !timer.DueAtMs.HasValue || timer.DueAtMs.Value > NowMs) continue;
                    timer.DueAtMs = null;
                    timer.Callback();
                }
            }
        }

        private sealed class FakeTimer : ISharedHostTimer
        {
            internal FakeTimer(Action callback) { Callback = callback; }
            internal Action Callback { get; }
            internal long? DueAtMs { get; set; }
            internal bool Disposed { get; private set; }
            public void Change(long? dueAtMs) { DueAtMs = dueAtMs; }
            public void Dispose() { Disposed = true; DueAtMs = null; }
        }

        private sealed class MutexRequest
        {
            internal string Name;
            internal int RecordsWrittenBefore;
            internal int ChildrenStartedBefore;
        }

        private sealed class FakeMutexHandle : IDisposable
        {
            internal bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        private sealed class FakeMutexFactory : ISharedHostMutexFactory
        {
            private readonly FakeRecordWriter _records;
            private readonly FakeChildLauncher _launcher;

            internal FakeMutexFactory(FakeRecordWriter records, FakeChildLauncher launcher)
            {
                _records = records;
                _launcher = launcher;
            }

            internal HashSet<string> HeldElsewhere { get; } = new HashSet<string>(StringComparer.Ordinal);
            internal List<MutexRequest> Requests { get; } = new List<MutexRequest>();
            internal List<FakeMutexHandle> Granted { get; } = new List<FakeMutexHandle>();

            public IDisposable TryAcquire(string name)
            {
                Requests.Add(new MutexRequest
                {
                    Name = name,
                    RecordsWrittenBefore = _records.Snapshots.Count,
                    ChildrenStartedBefore = _launcher.Started.Count
                });
                if (HeldElsewhere.Contains(name)) return null;
                var handle = new FakeMutexHandle();
                Granted.Add(handle);
                return handle;
            }
        }

        private sealed class FakeProcessProbe : IProcessProbe
        {
            internal Dictionary<int, ProcessProbeResult> Results { get; } = new Dictionary<int, ProcessProbeResult>();
            internal List<int> Calls { get; } = new List<int>();

            public ProcessProbeResult Probe(int pid)
            {
                Calls.Add(pid);
                return Results.TryGetValue(pid, out ProcessProbeResult result) ? result : ProcessProbeResult.NotRunning;
            }
        }

        // Keeps a parsed copy of every write: the runtime updates one record object in place.
        private sealed class FakeRecordWriter : ISharedHostRecordWriter
        {
            internal List<JObject> Snapshots { get; } = new List<JObject>();
            internal int Deletes { get; private set; }

            public void Write(SharedHostRecord record) => Snapshots.Add(JObject.Parse(record.ToJson()));
            public void Delete() => Deletes++;
        }

        private sealed class FakeChildLauncher : ISharedHostChildLauncher
        {
            private readonly FakeRecordWriter _records;
            private int _nextPid = 60000;

            internal FakeChildLauncher(FakeRecordWriter records) { _records = records; }

            internal List<FakeChild> Started { get; } = new List<FakeChild>();

            public ISharedHostChild Start(ProcessStartInfo startInfo)
            {
                var child = new FakeChild(++_nextPid, _records.Snapshots.ToList());
                Started.Add(child);
                return child;
            }
        }

        private sealed class FakeChild : ISharedHostChild
        {
            private readonly ManualResetEvent _exited = new ManualResetEvent(false);

            internal FakeChild(int pid, List<JObject> recordsAtLaunch)
            {
                Pid = pid;
                StartTimeUtcTicks = DateTime.UtcNow.Ticks;
                RecordsAtLaunch = recordsAtLaunch;
            }

            // The record writes that happened before this child was created.
            internal List<JObject> RecordsAtLaunch { get; }

            public int Pid { get; }
            public long StartTimeUtcTicks { get; }
            public bool HasExited { get; private set; }
            public int ExitCode { get; private set; } = -1;
            public long WorkingSetBytes => HasExited ? 0 : 64L * 1024 * 1024;
            public long PrivateBytes => HasExited ? 0 : 48L * 1024 * 1024;
            public WaitHandle ExitedHandle => _exited;
            public TextReader StandardOutput => TextReader.Null;
            public TextReader StandardError => TextReader.Null;

            public void WriteLine(string line)
            {
                if (HasExited) throw new IOException("The child has exited.");
            }

            public void Kill()
            {
                HasExited = true;
                ExitCode = -1;
                _exited.Set();
            }

            public void Dispose() { }
        }

        // In-memory Attachment. Stop mirrors the pipe transport (closing the pipe ends RunAsync,
        // whose finally block detaches the Attachment) and records when it happened.
        private sealed class FakeAttachment : ISharedHostAttachment
        {
            private readonly SharedWorkerHostRuntime _runtime;
            private readonly FakeClock _clock;
            private long _sequence;
            private int _stopped;

            internal FakeAttachment(SharedWorkerHostRuntime runtime, FakeClock clock)
            {
                _runtime = runtime;
                _clock = clock;
                ConnectionId = Guid.NewGuid().ToString("N");
            }

            internal bool Stopped => Volatile.Read(ref _stopped) != 0;
            internal long StoppedAtMs { get; private set; } = -1;

            public string ConnectionId { get; }
            public string AttachmentId { get; set; } = string.Empty;

            public long NextSequence() => Interlocked.Increment(ref _sequence);

            public bool Enqueue(string line) => !Stopped;

            public void Stop()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
                StoppedAtMs = _clock.NowMs;
                _runtime.Detach(this);
            }
        }
    }
}
