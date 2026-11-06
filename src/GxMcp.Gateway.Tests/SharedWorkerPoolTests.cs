using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Shared-mode Gateway pool, launcher admission pre-check, and attachment admission handling (task 5.3;
    // Requirements 1.2, 1.4, 1.5, 1.8, 5.2, 5.3; design "WorkerPool.cs (shared mode only)",
    // "SharedWorkerHostLauncher.cs (v2)", "SharedWorkerConnection.cs (v2)", decision A2, protocol v2 frames).
    // These tests are written before the implementation (tasks 5.7, 5.8 and 5.9) and pin the seam contract below;
    // those tasks implement exactly these types, members and names. Isolated_Mode keeps keying by alias.
    //
    // ---- WorkerPool shared-mode keying (task 5.8, src/GxMcp.Gateway/WorkerPool.cs) ----
    //   Shared mode is Configuration.Server.WorkerSharingMode == "shared-host" (case-insensitive).
    //   internal Func<KbHandle, string>? SharedIdentityKeyResolver { get; set; }
    //     The Physical_KB_Identity key of a handle (SharedWorkerIdentity.Key). Null in production: the pool then
    //     builds the key from the same worker path, installation, driver, major and profile config path that
    //     WorkerProcess.StartShared passes to SharedWorkerIdentity.Create. Never called in Isolated_Mode.
    //   Shared-mode entries are keyed by that identity key, with an aliasesByKey map (identity key -> normalized
    //   aliases). AcquireAsync for a second alias of one identity returns the same WorkerProcess (one Attachment,
    //   no second spawn) and adds the alias to the map. Every alias-taking member (TryGet, TryGetWorker,
    //   BeginCommand, IsSpawning, Close, DropLiveEntry, SetInFlightForTest, ...) resolves the alias through the map.
    //   IsAtCapacity and the Server.MaxOpenKbs window count identity entries, never aliases (Requirement 5.2).
    //   A Worker exit or a failed spawn removes only its own identity entry and every alias mapped to it.
    //   internal IReadOnlyDictionary<string, IReadOnlyList<string>> AliasesByKey()
    //     Snapshot of aliasesByKey; aliases are NormalizedAlias values sorted ordinal. Empty in Isolated_Mode.
    //   internal Func<string, int>? SessionCountForKey { get; set; }
    //     Session count of one open identity key, for KB_POOL_FULL details; Program wires it to the session store.
    //     Null -> 0.
    //   Eviction for Server.MaxOpenKbs in shared mode stops the victim with WorkerStopReason.CapacityEviction
    //   (new enum member). Isolated_Mode eviction keeps WorkerStopReason.ExplicitClose.
    //
    // ---- Detach with stopIfLast (task 5.8) ----
    //   WorkerStopReason.CapacityEviction                  new member of the existing enum.
    //   internal static bool WorkerProcess.StopIfLastFor(WorkerStopReason reason)
    //     True only for CapacityEviction. The shared stop path disposes its attachment with
    //     Dispose(stopIfLast: StopIfLastFor(reason)).
    //   internal void SharedWorkerConnection.Dispose(bool stopIfLast)
    //     Writes the detach frame with a boolean "stopIfLast" property, then closes the pipe as Dispose() does.
    //     Dispose() is Dispose(stopIfLast: false) and writes "stopIfLast": false. Idempotent like Dispose().
    //
    // ---- admission frame handling (task 5.8, src/GxMcp.Gateway/SharedWorkerConnection.cs) ----
    //   internal sealed class SharedWorkerAdmissionUpdate (same file; init-only)
    //       string State                 "held" | "starting" | "refused" | "evicting"
    //       int? Position                1-based FIFO position, set for "held"
    //       long? ElapsedMs              the frame's elapsedMs
    //       JObject? Refusal             the frame's refusal object (code, reason, measured values), for "refused"
    //   internal SharedWorkerAdmissionUpdate? SharedWorkerConnection.LastAdmission { get; }
    //   internal event Action<SharedWorkerAdmissionUpdate>? SharedWorkerConnection.AdmissionReceived
    //     Raised by the read loop for each { gxmcp: "admission" } frame after LastAdmission is updated. An admission
    //     frame is host control: it never reaches LineReceived and never disconnects the attachment.
    //
    // ---- Admission pre-check (task 5.7, src/GxMcp.Gateway/SharedWorkerHostLauncher.cs) ----
    //   internal delegate bool AvailableMemoryReader(out long availableMb, out string? error);   (next to
    //     ImagePathReader in SharedWorkerIdentity.cs)
    //   SharedWorkerRecoveryContext.ReadAvailableMemory { get; init; }
    //     AvailableMemoryReader, default MachineMemory.TryGetAvailablePhysicalMb. False means unreadable: the
    //     pre-check then passes the floor (AdmissionInput.AvailableMb = null).
    //   ConnectV2, after stale recovery under the election mutex and before context.StartHost, runs
    //   SharedHostAdmissionPolicy.Decide on: the classified v2 records of context.RegistryRoot (every *.json, each
    //   classified with context.Probe; AdmissionHostView.Key = record KbKey, State, WorkerPid, Evictable,
    //   Attachments, LastActivityUtc, HeldSinceUtc, Liveness), SelfKey = the requested kbKey, MaxWorkers,
    //   ColdStartConcurrency and MemoryFloorMb from request.Config.EffectiveSharedHost, MaxOpenKbsExceeded = false
    //   (the pool owns that limit), AvailableMb read last. Refuse -> SharedWorkerLaunchException with Code
    //   KB_POOL_FULL, the decision Reason, and Error = ConcurrencyErrorCatalog.Build(KB_POOL_FULL, reason,
    //   { Details = decision.Details plus limit/value }); worker-limit adds limit = "Server.SharedHost.MaxWorkers"
    //   and value = MaxWorkers, low-memory adds limit = "Server.SharedHost.MemoryFloorMB" and value = the floor.
    //   No host is started and the election mutex is released. Start, Hold and Evict are advisory and launch the
    //   host, which re-decides authoritatively.
    //   A Live record in state "refused" is not attached: ConnectV2 throws SharedWorkerLaunchException with the
    //   record's refusal code and reason, and Error = ConcurrencyErrorCatalog.Build(refusal.Code, refusal.Reason,
    //   { Details = { availableMb, floorMb } when present }). The record and every process are left unchanged.
    //
    // ---- Shared-mode KB_POOL_FULL (tasks 5.8 and 5.9) ----
    //   WorkerPoolFullException gains (the existing constructor keeps Reason and Error null):
    //       string? Reason              "max-open-kbs" in shared mode
    //       JObject? Error              ConcurrencyErrorCatalog.Build(KB_POOL_FULL, "max-open-kbs", { Details =
    //                                   { limit: "Server.MaxOpenKbs", value: <configured MaxOpenKbs>, openKbs: [
    //                                   { key, aliases: [..], sessions: SessionCountForKey(key), lastActivityUtc } ] } })
    //   A shared-mode refusal starts no Worker and stops no Worker (Requirement 5.3).
    //   internal static JObject Program.BuildKbPoolFullPayload(WorkerPoolFullException ex)
    //     The genexus_kb catch block uses it. Error null: exactly today's { error, code: "KB_POOL_FULL", openKbs:
    //     [aliases] }. Error set: every Error property plus error = ex.Message.
    //
    // No test here starts a process or touches the real %LOCALAPPDATA% registries: the pool uses SpawnFactoryForTest,
    // the launcher uses a temporary registry root and a fake clock, and the attachment tests use an in-process
    // named pipe as the host.
    public sealed class SharedWorkerPoolTests : IDisposable
    {
        private const string WorkerPath = @"C:\Tools\GxMcp\worker\GxMcp.Worker.exe";
        private const string InstallationPath = @"C:\GeneXus18";
        private const string Driver = "native-sdk";
        private const string Major = "18";
        private const string ProfilePath = @"C:\profiles\default\config.json";
        private const string KbAPath = @"C:\KBs\KbA";
        private const string KbBPath = @"C:\KBs\KbB";
        private const long AlignedStartTicks = 638_700_000_000_000_000L;
        private const int RecoveringGatewayPid = 3900;
        private const int OtherHostPid = 4130;
        private const int OtherWorkerPid = 5030;
        private const int NewHostPid = 4124;
        private const int IoTimeoutMs = 5000;

        private readonly string _root;
        private readonly string _v2Root;
        private readonly LauncherHarness _h;

        public SharedWorkerPoolTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxmcp-shared-pool-" + Guid.NewGuid().ToString("N"));
            _v2Root = Path.Combine(_root, "shared-workers", "v2");
            _h = new LauncherHarness(_v2Root, Path.Combine(_root, "worker-ownership"));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---------------------------------------------------------------
        // Requirements 1.5 and 5.2: identity keying and aliasesByKey
        // ---------------------------------------------------------------

        [Fact]
        public async Task SharedMode_TwoAliasesOfOneIdentity_ShareOneWorkerAndOneAttachment()
        {
            var cfg = SharedConfig(maxOpenKbs: 3);
            var pool = SharedPool(cfg, out var spawned);
            var first = new KbHandle("kba", KbAPath);
            var second = new KbHandle("kba-alias", @"c:\kbs\kba\");

            var w1 = await pool.AcquireAsync(first, CancellationToken.None);
            var w2 = await pool.AcquireAsync(second, CancellationToken.None);

            Assert.Same(w1, w2);
            Assert.Single(spawned);
            Assert.Same(w1, pool.TryGet("kba"));
            Assert.Same(w1, pool.TryGet("kba-alias"));
            var map = pool.AliasesByKey();
            var aliases = Assert.Single(map);
            Assert.Equal(IdentityKey(KbAPath), aliases.Key);
            Assert.Equal(new[] { "kba", "kba-alias" }, aliases.Value.ToArray());
        }

        [Fact]
        public async Task SharedMode_AliasesOfOneIdentity_CountOnceTowardMaxOpenKbs()
        {
            var cfg = SharedConfig(maxOpenKbs: 2);
            var pool = SharedPool(cfg, out var spawned);

            var a1 = await pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None);
            var a2 = await pool.AcquireAsync(new KbHandle("kba-alias", KbAPath.ToUpperInvariant()), CancellationToken.None);
            Assert.False(pool.IsAtCapacity());
            var b = await pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None);

            Assert.Same(a1, a2);
            Assert.NotSame(a1, b);
            Assert.Equal(2, spawned.Count);
            Assert.True(pool.IsAtCapacity());
            Assert.False(a1.CancellationRequestedForTest, "the KbA Worker must not be evicted to open KbB");
            Assert.Same(a1, pool.TryGet("kba-alias"));
            Assert.Same(b, pool.TryGet("kbb"));
            Assert.Equal(2, pool.AliasesByKey().Count);
        }

        [Fact]
        public async Task IsolatedMode_StaysKeyedByAlias()
        {
            var cfg = IsolatedConfig(maxOpenKbs: 3);
            var pool = new WorkerPool(cfg);
            var spawned = new List<WorkerProcess>();
            pool.SpawnFactoryForTest = h => Track(spawned, LiveWorker(cfg, h));
            pool.SharedIdentityKeyResolver = _ => throw new InvalidOperationException("Isolated_Mode must not resolve an identity key");

            var a1 = await pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None);
            var a2 = await pool.AcquireAsync(new KbHandle("kba-alias", KbAPath), CancellationToken.None);

            Assert.NotSame(a1, a2);
            Assert.Equal(2, spawned.Count);
            Assert.Empty(pool.AliasesByKey());
        }

        // ---------------------------------------------------------------
        // Requirement 5.3: KB_POOL_FULL max-open-kbs with per-KB details
        // ---------------------------------------------------------------

        [Fact]
        public async Task SharedMode_MaxOpenKbsWithNoEvictableWorker_RefusesWithReasonLimitValueAndPerKbDetails()
        {
            var cfg = SharedConfig(maxOpenKbs: 1);
            var pool = SharedPool(cfg, out var spawned);
            string keyA = IdentityKey(KbAPath);
            pool.SessionCountForKey = key => key == keyA ? 2 : 0;
            DateTime before = DateTime.UtcNow;
            var a = await pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None);
            await pool.AcquireAsync(new KbHandle("kba-alias", KbAPath), CancellationToken.None);
            DateTime after = DateTime.UtcNow;
            var release = pool.BeginCommand("kba-alias");
            Assert.NotNull(release);

            var ex = await Assert.ThrowsAsync<WorkerPoolFullException>(() =>
                pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None));

            Assert.Equal("max-open-kbs", ex.Reason);
            var error = Assert.IsType<JObject>(ex.Error);
            AssertCatalogFields(error, ConcurrencyErrorCatalog.KbPoolFull, "max-open-kbs", retryable: false);
            Assert.Equal("Server.MaxOpenKbs", (string?)error["limit"]);
            Assert.Equal(1, (int)error["value"]!);
            var open = Assert.Single(Assert.IsType<JArray>(error["openKbs"]));
            Assert.Equal(keyA, (string?)open["key"]);
            Assert.Equal(new[] { "kba", "kba-alias" }, open["aliases"]!.Select(t => (string)t!).ToArray());
            Assert.Equal(2, (int)open["sessions"]!);
            DateTime lastActivity = ((DateTime)open["lastActivityUtc"]!).ToUniversalTime();
            Assert.InRange(lastActivity, before.AddSeconds(-1), after.AddSeconds(1));

            // Refusal starts no Worker and stops no Worker.
            Assert.Single(spawned);
            Assert.False(a.CancellationRequestedForTest);
            Assert.Same(a, pool.TryGet("kba"));
            Assert.Null(pool.TryGet("kbb"));
            release!();
        }

        [Fact]
        public async Task SharedMode_PoolFullPayload_CarriesTheCatalogDetails()
        {
            var cfg = SharedConfig(maxOpenKbs: 1);
            var pool = SharedPool(cfg, out _);
            await pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None);
            pool.BeginCommand("kba");
            var ex = await Assert.ThrowsAsync<WorkerPoolFullException>(() =>
                pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None));

            JObject payload = Program.BuildKbPoolFullPayload(ex);

            Assert.Equal("KB_POOL_FULL", (string?)payload["code"]);
            Assert.Equal("max-open-kbs", (string?)payload["reason"]);
            Assert.False((bool)payload["retryable"]!);
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string?)payload["dispatchState"]);
            Assert.Equal("Server.MaxOpenKbs", (string?)payload["limit"]);
            Assert.Equal(1, (int)payload["value"]!);
            Assert.Equal(ex.Message, (string?)payload["error"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)payload["hint"]));
            Assert.Single(Assert.IsType<JArray>(payload["openKbs"]));
        }

        [Fact]
        public async Task SharedMode_PoolFullSessions_CountSessionsSelectingAnyAliasOfTheIdentity()
        {
            var cfg = SharedConfig(maxOpenKbs: 1);
            var pool = SharedPool(cfg, out _);
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(10));
            pool.SessionCountForKey = key => Program.CountSessionsForPoolKey(store, pool, key);
            await pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None);
            await pool.AcquireAsync(new KbHandle("kba-alias", KbAPath), CancellationToken.None);
            pool.BeginCommand("kba");
            store.Set("session-1", "KBA");
            store.Set("session-2", "kba-alias");
            store.Set("session-3", "kbb");
            store.Initialize("session-4", null);

            var ex = await Assert.ThrowsAsync<WorkerPoolFullException>(() =>
                pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None));

            var open = Assert.Single(Assert.IsType<JArray>(Program.BuildKbPoolFullPayload(ex)["openKbs"]));
            Assert.Equal(IdentityKey(KbAPath), (string?)open["key"]);
            Assert.Equal(2, (int)open["sessions"]!);
        }

        [Fact]
        public void IsolatedMode_PoolFullPayload_IsUnchanged()
        {
            var ex = new WorkerPoolFullException(new List<KbHandle> { new KbHandle("kba", KbAPath), new KbHandle("kbb", KbBPath) });

            JObject payload = Program.BuildKbPoolFullPayload(ex);

            Assert.Null(ex.Reason);
            Assert.Null(ex.Error);
            var expected = new JObject
            {
                ["error"] = ex.Message,
                ["code"] = "KB_POOL_FULL",
                ["openKbs"] = new JArray("kba", "kbb")
            };
            Assert.True(JToken.DeepEquals(expected, payload), payload.ToString(Formatting.None));
        }

        // ---------------------------------------------------------------
        // Requirement 5.2 eviction: detach with stopIfLast=true
        // ---------------------------------------------------------------

        [Fact]
        public async Task SharedMode_MaxOpenKbsEviction_StopsTheVictimWithCapacityEviction()
        {
            var cfg = SharedConfig(maxOpenKbs: 1);
            var pool = SharedPool(cfg, out var spawned);
            var a = await pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None);
            var reasons = RecordStops(a);

            var b = await pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None);

            Assert.Equal(new[] { WorkerStopReason.CapacityEviction }, reasons.ToArray());
            Assert.True(WorkerProcess.StopIfLastFor(reasons.Single()));
            Assert.Null(pool.TryGet("kba"));
            Assert.Same(b, pool.TryGet("kbb"));
            Assert.Equal(2, spawned.Count);
        }

        [Fact]
        public async Task IsolatedMode_MaxOpenKbsEviction_KeepsExplicitClose()
        {
            var cfg = IsolatedConfig(maxOpenKbs: 1);
            var pool = new WorkerPool(cfg);
            pool.SpawnFactoryForTest = h => LiveWorker(cfg, h);
            var a = await pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None);
            var reasons = RecordStops(a);

            await pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None);

            Assert.Equal(new[] { WorkerStopReason.ExplicitClose }, reasons.ToArray());
            Assert.False(WorkerProcess.StopIfLastFor(WorkerStopReason.ExplicitClose));
        }

        [Fact]
        public void StopIfLast_IsTrueOnlyForCapacityEviction()
        {
            foreach (WorkerStopReason reason in Enum.GetValues(typeof(WorkerStopReason)))
            {
                Assert.Equal(reason == WorkerStopReason.CapacityEviction, WorkerProcess.StopIfLastFor(reason));
            }
        }

        [Fact]
        public async Task DisposeWithStopIfLast_WritesTheDetachFrameWithStopIfLastTrue()
        {
            await using var host = FakeHost.Start(ConnectionIdentity());
            var connection = host.CreateConnection();
            connection.Connect(IoTimeoutMs);

            connection.Dispose(stopIfLast: true);

            JObject detach = await host.NextFrame("detach").WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            Assert.Equal(JTokenType.Boolean, detach["stopIfLast"]?.Type);
            Assert.True((bool)detach["stopIfLast"]!);
            Assert.False(connection.IsConnected);
        }

        [Fact]
        public async Task PlainDispose_WritesTheDetachFrameWithStopIfLastFalse()
        {
            await using var host = FakeHost.Start(ConnectionIdentity());
            var connection = host.CreateConnection();
            connection.Connect(IoTimeoutMs);

            connection.Dispose();

            JObject detach = await host.NextFrame("detach").WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            Assert.Equal(JTokenType.Boolean, detach["stopIfLast"]?.Type);
            Assert.False((bool)detach["stopIfLast"]!);
        }

        // ---------------------------------------------------------------
        // Requirement 5.3 (via the host Admission_Guard): admission frames on the attachment
        // ---------------------------------------------------------------

        [Fact]
        public async Task AdmissionFrameWithHeld_ReportsThePosition_AndIsNotAWorkerLine()
        {
            var identity = ConnectionIdentity();
            await using var host = FakeHost.Start(identity);
            using var connection = host.CreateConnection();
            var lines = new ConcurrentQueue<string>();
            var sentinel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var admission = new TaskCompletionSource<SharedWorkerAdmissionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.LineReceived += line =>
            {
                lines.Enqueue(line);
                if (line.Contains("\"rpc-after-admission\"", StringComparison.Ordinal)) sentinel.TrySetResult(true);
            };
            connection.AdmissionReceived += update => admission.TrySetResult(update);
            connection.Connect(IoTimeoutMs);

            await host.SendAsync(AdmissionFrame(identity, "held", position: 2, elapsedMs: 1500, refusal: null));
            await host.SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = "rpc-after-admission", ["result"] = new JObject() });

            var update = await admission.Task.WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            await sentinel.Task.WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            Assert.Equal("held", update.State);
            Assert.Equal(2, update.Position);
            Assert.Equal(1500, update.ElapsedMs);
            Assert.Null(update.Refusal);
            Assert.Same(update, connection.LastAdmission);
            Assert.DoesNotContain(lines, l => l.Contains("\"admission\"", StringComparison.Ordinal));
            Assert.True(connection.IsConnected);
        }

        [Fact]
        public async Task AdmissionFrameWithRefused_ExposesTheRefusal_AndKeepsTheAttachment()
        {
            var identity = ConnectionIdentity();
            await using var host = FakeHost.Start(identity);
            using var connection = host.CreateConnection();
            var admission = new TaskCompletionSource<SharedWorkerAdmissionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AdmissionReceived += update => admission.TrySetResult(update);
            connection.Connect(IoTimeoutMs);
            var refusal = new JObject { ["code"] = "KB_POOL_FULL", ["reason"] = "low-memory", ["availableMb"] = 900, ["floorMb"] = 1024 };

            await host.SendAsync(AdmissionFrame(identity, "refused", position: null, elapsedMs: 10, refusal: refusal));

            var update = await admission.Task.WaitAsync(TimeSpan.FromMilliseconds(IoTimeoutMs));
            Assert.Equal("refused", update.State);
            Assert.Null(update.Position);
            Assert.True(JToken.DeepEquals(refusal, update.Refusal), update.Refusal?.ToString(Formatting.None));
            Assert.True(connection.IsConnected);
        }

        // ---------------------------------------------------------------
        // Requirement 5.3 and decision A2: advisory pre-check before launching a host
        // ---------------------------------------------------------------

        [Fact]
        public void PreCheck_AtTheMachineWideWorkerLimitWithNoEvictableHost_RefusesBeforeLaunchingAHost()
        {
            var other = SeedOtherHost(SharedHostRecordState.Ready, evictable: false, attachments: 2);

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(LaunchRequest(maxWorkers: 1), _h.Context()));

            var error = AssertLaunchError(ex, "worker-limit");
            Assert.Equal("Server.SharedHost.MaxWorkers", (string?)error["limit"]);
            Assert.Equal(1, (int)error["value"]!);
            var open = Assert.Single(Assert.IsType<JArray>(error["openKbs"]));
            Assert.Equal(other.KbKey, (string?)open["key"]);
            Assert.Equal(2, (int)open["sessions"]!);
            Assert.Equal(other.LastActivityUtc!.Value, ((DateTime)open["lastActivityUtc"]!).ToUniversalTime());
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Connects);
            Assert.Equal(_h.Election.Requested.Count, _h.Election.Released);
        }

        [Fact]
        public void PreCheck_BelowTheMemoryFloor_RefusesWithLowMemoryBeforeLaunchingAHost()
        {
            _h.AvailableMb = 900;

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(LaunchRequest(), _h.Context()));

            var error = AssertLaunchError(ex, "low-memory");
            Assert.Equal(900, (long)error["availableMb"]!);
            Assert.Equal(1024, (long)error["floorMb"]!);
            Assert.Equal("Server.SharedHost.MemoryFloorMB", (string?)error["limit"]);
            Assert.Equal(1024, (int)error["value"]!);
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Connects);
            Assert.Equal(_h.Election.Requested.Count, _h.Election.Released);
        }

        [Fact]
        public void PreCheck_WhenAnEvictableHostExists_LaunchesTheHost()
        {
            SeedOtherHost(SharedHostRecordState.Ready, evictable: true, attachments: 0);
            _h.OnStart = PublishNewHost;

            var attachment = SharedWorkerHostLauncher.ConnectV2(LaunchRequest(maxWorkers: 1), _h.Context());

            Assert.Single(_h.Starts);
            Assert.Same(Assert.Single(_h.Connects).Attachment, attachment);
        }

        [Fact]
        public void PreCheck_WhenAColdStartHoldsTheConcurrency_LaunchesTheHostWhichHoldsItself()
        {
            SeedOtherHost(SharedHostRecordState.Starting, evictable: false, attachments: 1);
            _h.OnStart = PublishNewHost;

            var attachment = SharedWorkerHostLauncher.ConnectV2(LaunchRequest(), _h.Context());

            Assert.Single(_h.Starts);
            Assert.Same(Assert.Single(_h.Connects).Attachment, attachment);
        }

        [Fact]
        public void PreCheck_UnreadableMemory_PassesTheFloorAndLaunchesTheHost()
        {
            _h.AvailableMb = null;
            _h.OnStart = PublishNewHost;

            SharedWorkerHostLauncher.ConnectV2(LaunchRequest(), _h.Context());

            Assert.True(_h.MemoryReads > 0, "the pre-check must read available memory");
            Assert.Single(_h.Starts);
        }

        [Fact]
        public void RefusedRecord_ReturnsItsRefusal_WithoutAttaching()
        {
            var identity = LaunchIdentity(KbAPath);
            var record = HostRecord(identity, NewHostPid, SharedHostRecordState.Refused);
            record.WorkerPid = 0;
            record.WorkerStartTimeUtcTicks = 0;
            record.Refusal = new SharedHostRefusal { Code = "KB_POOL_FULL", Reason = "low-memory", AvailableMb = 900, FloorMb = 1024 };
            string path = SharedWorkerRegistry.WriteRecordV2(record, _v2Root);
            _h.Probe.Set(NewHostPid, ProcessProbeResult.Running(record.HostStartTimeUtcTicks));
            string before = File.ReadAllText(path);

            var ex = Assert.Throws<SharedWorkerLaunchException>(() =>
                SharedWorkerHostLauncher.ConnectV2(LaunchRequest(), _h.Context()));

            var error = AssertLaunchError(ex, "low-memory");
            Assert.Equal(900, (long)error["availableMb"]!);
            Assert.Equal(1024, (long)error["floorMb"]!);
            Assert.Equal(0, _h.ConnectAttempts);
            Assert.Empty(_h.Starts);
            Assert.Empty(_h.Election.Requested);
            Assert.Empty(_h.Terminated);
            Assert.Equal(before, File.ReadAllText(path));
        }

        // ---------------------------------------------------------------
        // Requirements 1.2, 1.4 and 1.8: one KB's failure stays with that KB
        // ---------------------------------------------------------------

        [Fact]
        public async Task SharedMode_WorkerExitOfOneKb_LeavesTheOtherKbWorkerInPlace()
        {
            var cfg = SharedConfig(maxOpenKbs: 3);
            var pool = SharedPool(cfg, out var spawned);
            var a = await pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None);
            await pool.AcquireAsync(new KbHandle("kba-alias", KbAPath), CancellationToken.None);
            var b = await pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None);
            var releaseB = pool.BeginCommand("kbb", b);
            Assert.NotNull(releaseB);

            a.SimulateUnexpectedExitForTest();

            Assert.Null(pool.TryGet("kba"));
            Assert.Null(pool.TryGet("kba-alias"));
            Assert.Same(b, pool.TryGet("kbb"));
            Assert.False(b.CancellationRequestedForTest);
            Assert.Equal(1, pool.InFlightCommandsForTest("kbb"));
            var remaining = Assert.Single(pool.AliasesByKey());
            Assert.Equal(IdentityKey(KbBPath), remaining.Key);
            Assert.Same(b, await pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None));
            Assert.Equal(2, spawned.Count);
            releaseB!();
        }

        [Fact]
        public async Task SharedMode_SpawnFailureOfOneKb_LeavesTheOtherKbWorkerInPlace()
        {
            var cfg = SharedConfig(maxOpenKbs: 3);
            var pool = new WorkerPool(cfg) { SharedIdentityKeyResolver = h => IdentityKey(h.Path) };
            pool.SpawnFactoryForTest = h =>
            {
                if (string.Equals(h.Alias, "kba", StringComparison.Ordinal))
                    throw new InvalidOperationException("KbA host failed to start");
                return LiveWorker(cfg, h);
            };
            var b = await pool.AcquireAsync(new KbHandle("kbb", KbBPath), CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pool.AcquireAsync(new KbHandle("kba", KbAPath), CancellationToken.None));

            Assert.Null(pool.TryGet("kba"));
            Assert.Same(b, pool.TryGet("kbb"));
            Assert.False(b.CancellationRequestedForTest);
            var remaining = Assert.Single(pool.AliasesByKey());
            Assert.Equal(IdentityKey(KbBPath), remaining.Key);
            Assert.Equal(new[] { "kbb" }, remaining.Value.ToArray());
        }

        // ---------------------------------------------------------------
        // Pool helpers
        // ---------------------------------------------------------------

        private static SharedHostSettingsResult Settings(int? maxWorkers = null)
        {
            var section = maxWorkers.HasValue ? new JObject { [SharedHostSettingsResolver.MaxWorkersKey] = maxWorkers.Value } : null;
            var settings = SharedHostSettingsResolver.Resolve(section, strictDocument: false, "stdio-isolated", _ => null);
            Assert.True(settings.IsValid, string.Join("; ", settings.Errors));
            return settings;
        }

        private static Configuration SharedConfig(int maxOpenKbs) => new Configuration
        {
            Server = new ServerConfig { WorkerSharingMode = "shared-host", MaxOpenKbs = maxOpenKbs },
            EffectiveSharedHost = Settings()
        };

        private static Configuration IsolatedConfig(int maxOpenKbs) => new Configuration
        {
            Server = new ServerConfig { WorkerSharingMode = "isolated", MaxOpenKbs = maxOpenKbs }
        };

        private static string IdentityKey(string kbPath) => LaunchIdentity(kbPath).Key;

        private static WorkerPool SharedPool(Configuration cfg, out List<WorkerProcess> spawned)
        {
            var list = new List<WorkerProcess>();
            var pool = new WorkerPool(cfg) { SharedIdentityKeyResolver = h => IdentityKey(h.Path) };
            pool.SpawnFactoryForTest = h => Track(list, LiveWorker(cfg, h));
            spawned = list;
            return pool;
        }

        private static WorkerProcess Track(List<WorkerProcess> spawned, WorkerProcess worker)
        {
            lock (spawned) spawned.Add(worker);
            return worker;
        }

        // A Worker the pool treats as alive; exitConfirmed=false keeps a stop from firing the exit notification.
        private static WorkerProcess LiveWorker(Configuration cfg, KbHandle handle)
        {
            var worker = new WorkerProcess(cfg, handle);
            worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
            return worker;
        }

        private static List<WorkerStopReason> RecordStops(WorkerProcess worker)
        {
            var reasons = new List<WorkerStopReason>();
            worker.StopFailureForTest = reason =>
            {
                lock (reasons) reasons.Add(reason);
                return null;
            };
            return reasons;
        }

        private static void AssertCatalogFields(JObject error, string code, string reason, bool retryable)
        {
            Assert.Equal(code, (string?)error["code"]);
            Assert.Equal(reason, (string?)error["reason"]);
            Assert.Equal(retryable, (bool)error["retryable"]!);
            Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string?)error["dispatchState"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)error["hint"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)error["message"]));
        }

        // ---------------------------------------------------------------
        // Launcher helpers
        // ---------------------------------------------------------------

        private static SharedWorkerIdentity LaunchIdentity(string kbPath) =>
            SharedWorkerIdentity.Create(WorkerPath, kbPath, InstallationPath, Driver, Major, ProfilePath);

        private static SharedWorkerLaunchRequest LaunchRequest(int? maxWorkers = null) => new SharedWorkerLaunchRequest
        {
            Config = new Configuration
            {
                Server = new ServerConfig { WorkerSharingMode = "shared-host" },
                EffectiveSharedHost = Settings(maxWorkers)
            },
            Kb = new KbHandle("kba", KbAPath),
            WorkerPath = WorkerPath,
            InstallationPath = InstallationPath,
            Driver = Driver,
            Major = Major,
            ProfileConfigPath = ProfilePath,
            LaunchTimeoutMs = 30000
        };

        private static SharedHostRecord HostRecord(SharedWorkerIdentity identity, int hostPid, string state) =>
            new SharedHostRecord
            {
                KbKey = identity.KbKey,
                Identity = identity.ToRecordIdentity(),
                HostPid = hostPid,
                HostStartTimeUtcTicks = AlignedStartTicks + hostPid,
                LaunchedByGatewayPid = 3800,
                WorkerPid = OtherWorkerPid,
                WorkerStartTimeUtcTicks = AlignedStartTicks,
                PipeName = SharedWorkerRegistry.PipeNameV2(identity),
                Generation = 1,
                State = state,
                ColdStartConcurrency = 1,
                LastActivityUtc = new DateTime(2026, 9, 25, 11, 0, 0, DateTimeKind.Utc),
                UpdatedUtc = new DateTime(2026, 9, 25, 11, 0, 0, DateTimeKind.Utc)
            };

        // A live host for KbB that occupies one machine-wide Worker slot.
        private SharedHostRecord SeedOtherHost(string state, bool evictable, int attachments)
        {
            var record = HostRecord(LaunchIdentity(KbBPath), OtherHostPid, state);
            record.Evictable = evictable;
            record.Attachments = attachments;
            if (state == SharedHostRecordState.Starting) record.ColdStartStartedUtc = record.UpdatedUtc;
            SharedWorkerRegistry.WriteRecordV2(record, _v2Root);
            _h.Probe.Set(OtherHostPid, ProcessProbeResult.Running(record.HostStartTimeUtcTicks));
            return record;
        }

        // What the broker does after the fake starter "launched" it: publish a live, ready v2 record.
        private void PublishNewHost(SharedWorkerHostStartRequest request)
        {
            var record = HostRecord(request.Identity, NewHostPid, SharedHostRecordState.Ready);
            SharedWorkerRegistry.WriteRecordV2(record, _v2Root);
            _h.Probe.Set(NewHostPid, ProcessProbeResult.Running(record.HostStartTimeUtcTicks));
        }

        private static JObject AssertLaunchError(SharedWorkerLaunchException ex, string reason)
        {
            Assert.Equal(ConcurrencyErrorCatalog.KbPoolFull, ex.Code);
            Assert.Equal(reason, ex.Reason);
            Assert.NotNull(ex.Error);
            AssertCatalogFields(ex.Error, ConcurrencyErrorCatalog.KbPoolFull, reason, retryable: false);
            return ex.Error;
        }

        // ---------------------------------------------------------------
        // Attachment helpers
        // ---------------------------------------------------------------

        private static SharedWorkerIdentity ConnectionIdentity() =>
            SharedWorkerIdentity.Create(WorkerPath, KbAPath, InstallationPath, Driver, Major);

        private static JObject AdmissionFrame(SharedWorkerIdentity identity, string state, int? position, long elapsedMs, JObject? refusal)
        {
            var frame = new JObject
            {
                ["gxmcp"] = "admission",
                ["type"] = "admission",
                ["version"] = SharedWorkerConnection.ProtocolVersion,
                ["protocolVersion"] = SharedWorkerConnection.ProtocolVersion,
                ["key"] = identity.Key,
                ["identityKey"] = identity.Key,
                ["state"] = state,
                ["elapsedMs"] = elapsedMs
            };
            if (position.HasValue) frame["position"] = position.Value;
            if (refusal != null) frame["refusal"] = refusal;
            return frame;
        }

        // ---------------------------------------------------------------
        // Fakes
        // ---------------------------------------------------------------

        private sealed class FakeClock : ISharedWorkerLauncherClock
        {
            private static readonly DateTime Origin = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

            public DateTime UtcNow { get; private set; } = Origin;

            public void Sleep(TimeSpan duration)
            {
                UtcNow += duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
                // A launcher that never stops waiting fails here instead of hanging the test run.
                if (UtcNow - Origin > TimeSpan.FromHours(1))
                    throw new InvalidOperationException("fake clock passed one hour; the launcher does not stop waiting");
            }
        }

        private sealed class FakeProbe : IProcessProbe
        {
            private readonly Dictionary<int, ProcessProbeResult> _results = new Dictionary<int, ProcessProbeResult>();

            internal void Set(int pid, ProcessProbeResult result) => _results[pid] = result;

            public ProcessProbeResult Probe(int pid) =>
                _results.TryGetValue(pid, out var result) ? result : ProcessProbeResult.NotRunning;
        }

        private sealed class FakeAttachment : ISharedWorkerAttachment
        {
            public SharedWorkerAttachInfo? AttachInfo { get; set; }

            public bool IsSdkReady { get; set; } = true;

            public void Dispose()
            {
            }
        }

        private sealed class FakeElection
        {
            internal List<string> Requested { get; } = new List<string>();

            internal int Released { get; private set; }

            internal IDisposable Acquire(string name)
            {
                Requested.Add(name);
                return new Releaser(this);
            }

            private sealed class Releaser : IDisposable
            {
                private readonly FakeElection _owner;
                private bool _disposed;

                internal Releaser(FakeElection owner) => _owner = owner;

                public void Dispose()
                {
                    if (_disposed) return;
                    _disposed = true;
                    _owner.Released++;
                }
            }
        }

        private sealed class LauncherHarness
        {
            private readonly string _v2Root;
            private readonly string _ownershipRoot;

            internal LauncherHarness(string v2Root, string ownershipRoot)
            {
                _v2Root = v2Root;
                _ownershipRoot = ownershipRoot;
            }

            internal FakeClock Clock { get; } = new FakeClock();
            internal FakeProbe Probe { get; } = new FakeProbe();
            internal FakeElection Election { get; } = new FakeElection();
            internal List<int> Terminated { get; } = new List<int>();
            internal List<SharedWorkerHostStartRequest> Starts { get; } = new List<SharedWorkerHostStartRequest>();
            internal List<(SharedHostRecord Record, FakeAttachment Attachment)> Connects { get; } =
                new List<(SharedHostRecord, FakeAttachment)>();
            internal int ConnectAttempts { get; private set; }
            internal int MemoryReads { get; private set; }

            // Available physical memory the pre-check reads; null means unreadable.
            internal long? AvailableMb { get; set; } = 4096;

            internal Action<SharedWorkerHostStartRequest>? OnStart { get; set; }

            internal SharedWorkerRecoveryContext Context() =>
                new SharedWorkerRecoveryContext
                {
                    Probe = Probe,
                    ReadImagePath = (int pid, out string? path, out string? error) =>
                    {
                        path = null;
                        error = "no image for pid " + pid;
                        return false;
                    },
                    Terminate = pid =>
                    {
                        Terminated.Add(pid);
                        return null;
                    },
                    Log = _ => { },
                    RecoveringGatewayPid = RecoveringGatewayPid,
                    GatewayPids = Array.Empty<int>(),
                    StopWaitTimeout = TimeSpan.FromSeconds(10),
                    StopPollInterval = TimeSpan.FromMilliseconds(100),
                    RegistryRoot = _v2Root,
                    OwnershipRegistryRoot = _ownershipRoot,
                    Clock = Clock,
                    RegistryPollInterval = TimeSpan.FromMilliseconds(100),
                    AcquireElectionMutex = (name, timeout) => Election.Acquire(name),
                    ReadAvailableMemory = (out long availableMb, out string? error) =>
                    {
                        MemoryReads++;
                        if (AvailableMb.HasValue)
                        {
                            availableMb = AvailableMb.Value;
                            error = null;
                            return true;
                        }

                        availableMb = 0;
                        error = "memory status unavailable";
                        return false;
                    },
                    StartHost = request =>
                    {
                        Starts.Add(request);
                        OnStart?.Invoke(request);
                    },
                    ConnectPipe = (identity, record, clientId, timeoutMs) =>
                    {
                        ConnectAttempts++;
                        var attachment = new FakeAttachment
                        {
                            AttachInfo = new SharedWorkerAttachInfo
                            {
                                AttachId = "attach-" + ConnectAttempts,
                                HostPid = record.HostPid,
                                HostStartTimeUtcTicks = record.HostStartTimeUtcTicks,
                                WorkerPid = record.WorkerPid,
                                WorkerStartTimeUtcTicks = record.WorkerStartTimeUtcTicks,
                                Generation = record.Generation,
                                SdkReady = true
                            }
                        };
                        Connects.Add((record, attachment));
                        return attachment;
                    }
                };
        }

        /// <summary>
        /// A scripted Worker_Host on an in-process named pipe: answers the attach handshake, records the first frame
        /// of each type it reads, and sends frames on request.
        /// </summary>
        private sealed class FakeHost : IAsyncDisposable
        {
            private readonly SharedWorkerIdentity _identity;
            private readonly string _pipeName = "GxMcpTestPool_" + Guid.NewGuid().ToString("N");
            private readonly NamedPipeServerStream _server;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _firstFrameByType =
                new ConcurrentDictionary<string, TaskCompletionSource<JObject>>(StringComparer.Ordinal);
            private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
            private StreamWriter? _writer;
            private Task? _loop;

            private FakeHost(SharedWorkerIdentity identity)
            {
                _identity = identity;
                _server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }

            internal static FakeHost Start(SharedWorkerIdentity identity)
            {
                var host = new FakeHost(identity);
                host._loop = Task.Run(host.RunAsync);
                return host;
            }

            internal SharedWorkerConnection CreateConnection() => new SharedWorkerConnection(
                _identity,
                new SharedHostRecord { KbKey = _identity.KbKey, PipeName = _pipeName, HostPid = 10, WorkerPid = 12, Generation = 2 },
                "client-pool");

            internal Task<JObject> NextFrame(string type) =>
                _firstFrameByType.GetOrAdd(type, _ => new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

            internal async Task SendAsync(JObject frame)
            {
                await _writeGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await _writer!.WriteLineAsync(frame.ToString(Formatting.None)).ConfigureAwait(false);
                    await _writer.FlushAsync().ConfigureAwait(false);
                }
                finally { _writeGate.Release(); }
            }

            private async Task RunAsync()
            {
                try
                {
                    await _server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(_server, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
                    _writer = new StreamWriter(_server, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
                    while (!_cts.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                        if (line == null) break;
                        JObject frame;
                        try { frame = JObject.Parse(line); } catch { continue; }
                        string type = frame["gxmcp"]?.ToString() ?? frame["type"]?.ToString() ?? string.Empty;
                        _ = NextFrame(type);
                        _firstFrameByType[type].TrySetResult(frame);
                        if (type == "attach") await SendAsync(AttachAck()).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }

            private JObject AttachAck() => new JObject
            {
                ["gxmcp"] = "attach_ack",
                ["type"] = "attach_ack",
                ["version"] = SharedWorkerConnection.ProtocolVersion,
                ["protocolVersion"] = SharedWorkerConnection.ProtocolVersion,
                ["key"] = _identity.Key,
                ["identityKey"] = _identity.Key,
                ["attachId"] = "attach-pool",
                ["hostPid"] = 10,
                ["hostStartTimeUtcTicks"] = 11,
                ["workerPid"] = 12,
                ["workerStartTimeUtcTicks"] = 13,
                ["generation"] = 2,
                ["ready"] = true
            };

            public async ValueTask DisposeAsync()
            {
                try { _cts.Cancel(); } catch { }
                try { _server.Dispose(); } catch { }
                if (_loop != null)
                {
                    try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                }
                _cts.Dispose();
                _writeGate.Dispose();
            }
        }
    }
}
