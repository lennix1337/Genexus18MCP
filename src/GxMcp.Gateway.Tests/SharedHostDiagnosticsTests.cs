#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The shared-host diagnostics block (task 6.5; Requirements 1.1, 1.2, 1.3, 1.5, 2.9, 3.12, 3.13, 4.2, 5.6, 6.9,
    // 10.1, 10.2, 11.1, 11.2, 11.3, 11.6, 11.7, 11.8, 11.9; design "SharedHostDiagnostics.cs (new)", "Settings
    // precedence" and "Protocol v2 frames"). These tests are written before the implementation (task 6.8) and pin
    // the contract below; task 6.8 implements exactly these types, members and names.
    //
    // ---- Builder input seams (src/GxMcp.Gateway/SharedHostDiagnostics.cs, namespace GxMcp.Gateway) ----
    //   internal interface ISharedHostDiagnosticsClock
    //       DateTime UtcNow { get; }
    //       Task Delay(int milliseconds, CancellationToken cancellationToken)
    //     Every timestamp and every timeout of the builder goes through this clock; no CancelAfter, no Task.Delay,
    //     no Stopwatch. Production: DateTime.UtcNow and Task.Delay.
    //   internal sealed class SharedHostDiagnosticsHost           (init-only; one open identity key of this Gateway)
    //       string IdentityKey
    //       IReadOnlyList<string> Aliases                        WorkerPool.AliasesByKey()[IdentityKey]
    //       int? HostPid                                         attach-ack HostPid
    //       int? WorkerPid                                       attach-ack WorkerPid
    //       Func<int, CancellationToken, Task<JObject?>> RequestStatusAsync
    //                                                            production: SharedWorkerConnection.RequestStatusAsync
    //     RequestStatusAsync is the only delegate member: diagnostics reach a host only through the "status" control
    //     frame, never through SendAsync, the pending-request registry or a Request_Queue (Requirement 11.7).
    //   internal sealed class SharedHostDiagnosticsRecord        (init-only; one classified registry v2 record)
    //       SharedHostRecord Record
    //       RecordLiveness Liveness
    //   internal sealed class SharedHostDiagnosticsInputs        (init-only)
    //       Func<IReadOnlyList<SharedHostDiagnosticsHost>> PoolView        the pool view of this Gateway
    //       ISharedHostDiagnosticsClock Clock
    //       AvailableMemoryReader ReadAvailableMemory                      existing delegate in SharedWorkerIdentity.cs
    //       Func<IReadOnlyList<SharedHostDiagnosticsRecord>> ReadRegistry  machine-wide v2 records, classified
    //       Func<int, long?> ReadWorkingSetMb                              null -> "unavailable"
    //       SharedHostSettingsResult Configured                            Configuration.EffectiveSharedHost
    //       Func<string, string?> Environment                              environment lookup
    //       int GatewayPid
    //
    // ---- Builder (one instance per Gateway; it remembers the last successful contact per identity key) ----
    //   internal sealed class SharedHostDiagnostics
    //       internal const int PerHostTimeoutMs = 1000;
    //       internal const int TotalTimeoutMs = 2000;
    //       internal SharedHostDiagnostics(SharedHostDiagnosticsInputs inputs)
    //       internal Task<JObject> BuildAsync(CancellationToken cancellationToken = default)
    //       internal static bool IsSharedHostMode(Configuration? config)
    //         True only when Server.WorkerSharingMode is "shared-host" (case-insensitive, trimmed).
    //   BuildAsync order: read Clock.UtcNow (start of the 2000 ms budget), PoolView(), then every host's
    //   RequestStatusAsync at once (in parallel, exactly one call per host per BuildAsync) with timeoutMs =
    //   min(PerHostTimeoutMs, TotalTimeoutMs - elapsed since start), each raced against Clock.Delay(that timeoutMs)
    //   so a source that ignores its timeout or cancellation cannot hold the call. BuildAsync returns as soon as every
    //   host answered or timed out. Then ReadRegistry(), and ReadAvailableMemory last (once per BuildAsync, never
    //   reused), then checkedAtUtc = Clock.UtcNow.
    //
    // ---- Output: the "sharedHost" block ----
    //   {
    //     "mode": "shared-host", "gatewayPid": int,
    //     "checkedAtUtc": ISO 8601 "o" string, "elapsedMs": long (checkedAtUtc - start),
    //     "attachReadinessWait": { "seconds": int, "source": <Configured source> },
    //     "settings": { "configured": { <name>: { "value": int, "source": string } } }
    //         names: queueWaitMs, queueDepth, sessionQueueCap, maxWorkers, memoryFloorMb, coldStartConcurrency,
    //         attachReadyTimeoutSeconds; sources exactly as SharedHostSettingsResult.Sources.
    //     "ignored": [ { "name": "GXMCP_BUSY_REJECT_MS", "reason": non-empty } ]   empty array when unset
    //     "warnings": [ string ]   one per KB whose host settings differ from the configured ones; it names the
    //                              identity key and every differing setting name
    //     "workers": { "maxWorkers": Configured.MaxWorkers, "counted": int,
    //                  "processes": [ { "identityKey", "pid", "workingSetMb": long | "unavailable" } ] }
    //         counted and processes: records with Liveness == Live and WorkerPid > 0 (the admission rule), ordered
    //         by identity key. workingSetMb comes from that host's status_ack when this Gateway has it, otherwise
    //         from ReadWorkingSetMb(pid).
    //     "admission": { "availableMemoryMb": long | "unavailable", "memoryReadAtUtc": ISO string,
    //                    "memoryFloorMb", "coldStartConcurrency" (configured), "coldStartsInProgress" (Live records
    //                    in state "starting"),
    //                    "heldStarts": [ { "identityKey", "elapsedMs" (now - HeldSinceUtc), "waiting": int | null } ] }
    //         waiting counts the queue entries with waitReason "worker-starting" in that key's status_ack; null when
    //         this Gateway has no status_ack for the key.
    //     "kbs": [ ordered by identityKey, ordinal ]
    //       reachable:   { "identityKey", "aliases" (sorted ordinal), "reachable": true, "hostPid" (pool view),
    //                      "workerPid" (status_ack), "attachments", "childState", "queueDepth", "idle",
    //                      "running": { "operation", "ownerAttachId", "elapsedMs" } | null,
    //                      "queue": [ { "position", "ownerAttachId", "waitMs", "waitReason", "operation"? } ],
    //                      "lastContactUtc" (this call),
    //                      "settings": { "effective": { "source": "host", "launchedByGatewayPid": int | null,
    //                                                   "values": status_ack effectiveSettings },
    //                                    "mismatches": [ setting names that differ from configured, sorted ordinal ] } }
    //         launchedByGatewayPid comes from the record whose KbKey equals the identity key.
    //       unreachable (no status_ack within the per-host timeout, a null answer, or a fault):
    //                    { "identityKey", "aliases", "reachable": false, "error": "status-timeout" | "status-failed",
    //                      "hostPid", "workerPid" (last known: the last status_ack, else the pool view),
    //                      "lastContactUtc": ISO string of the last successful contact | null }
    //
    // ---- Attachment to the three tools (Program) ----
    //   internal static Func<SharedHostDiagnostics>? Program.SharedHostDiagnosticsFactoryForTest { get; set; }
    //   internal static Task<JObject?> Program.BuildSharedHostBlockAsync(CancellationToken cancellationToken = default)
    //     Null when !SharedHostDiagnostics.IsSharedHostMode(active configuration), without calling the factory.
    //     Otherwise (factory ?? the Gateway's own instance).BuildAsync.
    //   genexus_whoami:   BuildWhoamiPayloadAsync adds the block as top-level "sharedHost".
    //   genexus_doctor:   BuildGatewayDoctorEnvelope(string? sessionId = null, JObject? wwpTemplates = null,
    //                     JObject? sharedHost = null) puts it at result.sharedHost and appends each of its warnings
    //                     to result.warnings; the tool dispatch passes await BuildSharedHostBlockAsync().
    //   genexus_lifecycle action=status: BuildWorkerLifecycleStatusEnvelope(job, workerPayload, args, waitSeconds,
    //                     until, JObject? sharedHost = null) adds top-level "sharedHost"; the status handler passes
    //                     await BuildSharedHostBlockAsync().
    //   Isolated_Mode: no "sharedHost" key anywhere, and the field set is the Previous_Release one. The tool schemas
    //   of the three tools stay equal to the contract-golden tools-list fixture.
    //
    // Every test uses a virtual clock and virtual latencies; the only real waits are the few milliseconds the clock
    // yields so continuations can run.
    [Collection("Gateway route state")]
    public sealed class SharedHostDiagnosticsTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SourceReadFailure_DoesNotReportEmptyDiagnostics(bool registryFailure)
        {
            var inputs = registryFailure
                ? new SharedHostDiagnosticsInputs { ReadRegistry = () => throw new IOException("registry unavailable") }
                : new SharedHostDiagnosticsInputs { PoolView = () => throw new IOException("pool unavailable") };

            await Assert.ThrowsAsync<IOException>(() => new SharedHostDiagnostics(inputs).BuildAsync());
        }

        // Virtual time advances in steps of StepMs, so a measured virtual elapsed time can exceed its exact value by
        // at most a few steps.
        private const int StepMs = 25;
        private const int GatewayPid = 3900;
        private const int LaunchingGatewayPid = 3777;
        private const string KeyA = "kb-key-a";
        private const string KeyB = "kb-key-b";
        private const string KeyC = "kb-key-c";
        private static readonly DateTime Origin = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc);
        private static readonly string[] SettingNames =
        {
            "queueWaitMs", "queueDepth", "sessionQueueCap", "maxWorkers", "memoryFloorMb", "coldStartConcurrency",
            "attachReadyTimeoutSeconds"
        };

        // ---------------------------------------------------------------
        // Requirements 1.5, 4.2, 11.1, 11.2: per-KB content
        // ---------------------------------------------------------------

        [Fact]
        public async Task Reachable_kb_reports_aliases_pids_attachments_queue_and_running_operation()
        {
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba-alias", "kba" }, hostPid: 4101, workerPid: 5101, latencyMs: 100,
                Ack(KeyA, workerPid: 5101, attachments: 2,
                    running: Running("genexus_edit/write", "attach-1", 2500),
                    queue: new[]
                    {
                        Entry(1, "attach-2", 800, "sta-busy", "genexus_edit/write"),
                        Entry(2, "attach-1", 300, "sta-busy", "genexus_edit/write")
                    }));
            var b = new FakeHost(clock, KeyB, new[] { "kbb" }, hostPid: 4202, workerPid: 5202, latencyMs: 100,
                Ack(KeyB, workerPid: 5202, attachments: 1, running: null, queue: Array.Empty<JObject>()));
            var diag = Build(clock, new[] { b, a });

            var block = await RunAsync(clock, diag);

            Assert.Equal("shared-host", (string?)block["mode"]);
            Assert.Equal(GatewayPid, (int)block["gatewayPid"]!);
            var kbs = Assert.IsType<JArray>(block["kbs"]);
            Assert.Equal(new[] { KeyA, KeyB }, kbs.Select(k => (string?)k["identityKey"]).ToArray());

            var ka = (JObject)kbs[0];
            Assert.True((bool)ka["reachable"]!);
            Assert.Equal(new[] { "kba", "kba-alias" }, ka["aliases"]!.Select(t => (string?)t).ToArray());
            Assert.Equal(4101, (int)ka["hostPid"]!);
            Assert.Equal(5101, (int)ka["workerPid"]!);
            Assert.Equal(2, (int)ka["attachments"]!);
            Assert.Equal("ready", (string?)ka["childState"]);
            Assert.Equal(2, (int)ka["queueDepth"]!);
            Assert.False((bool)ka["idle"]!);
            Assert.Equal("genexus_edit/write", (string?)ka["running"]!["operation"]);
            Assert.Equal("attach-1", (string?)ka["running"]!["ownerAttachId"]);
            Assert.Equal(2500, (long)ka["running"]!["elapsedMs"]!);
            Assert.Equal(new[] { 1, 2 }, ka["queue"]!.Select(q => (int)q["position"]!).ToArray());
            Assert.Equal(ReadUtc(block["checkedAtUtc"]!), ReadUtc(ka["lastContactUtc"]!));

            var kb = (JObject)kbs[1];
            Assert.True((bool)kb["reachable"]!);
            Assert.True((bool)kb["idle"]!);
            Assert.Equal(JTokenType.Null, kb["running"]!.Type);
            Assert.Equal(0, (int)kb["queueDepth"]!);
            Assert.Empty((JArray)kb["queue"]!);
        }

        [Fact]
        public async Task Two_kbs_running_at_the_same_time_report_distinct_worker_pids()
        {
            // Requirements 1.1, 1.2, 1.3: both operations are observable as running at once, on two Workers.
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 50,
                Ack(KeyA, 5101, 1, Running("genexus_lifecycle/build", "attach-1", 6000), Array.Empty<JObject>()));
            var b = new FakeHost(clock, KeyB, new[] { "kbb" }, 4202, 5202, 50,
                Ack(KeyB, 5202, 1, Running("genexus_lifecycle/build", "attach-9", 5400), Array.Empty<JObject>()));

            var block = await RunAsync(clock, Build(clock, new[] { a, b }));

            var kbs = (JArray)block["kbs"]!;
            Assert.All(kbs, k => Assert.False((bool)k["idle"]!));
            Assert.All(kbs, k => Assert.Equal(JTokenType.Object, k["running"]!.Type));
            Assert.NotEqual((int)kbs[0]["workerPid"]!, (int)kbs[1]["workerPid"]!);
        }

        [Fact]
        public async Task Two_aliases_of_one_identity_are_listed_under_one_key_with_one_worker_pid()
        {
            // Requirement 1.5: one entry per identity key, never one per alias.
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba", "kba-second" }, 4101, 5101, 10,
                Ack(KeyA, 5101, 1, null, Array.Empty<JObject>()));

            var block = await RunAsync(clock, Build(clock, new[] { a }));

            var entry = Assert.Single((JArray)block["kbs"]!);
            Assert.Equal(KeyA, (string?)entry["identityKey"]);
            Assert.Equal(new[] { "kba", "kba-second" }, entry["aliases"]!.Select(t => (string?)t).ToArray());
            Assert.Equal(5101, (int)entry["workerPid"]!);
        }

        // ---------------------------------------------------------------
        // Requirement 11.6: one wait reason per queued request
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("sta-busy")]
        [InlineData("worker-starting")]
        [InlineData("worker-limit")]
        public async Task Each_queue_entry_carries_exactly_one_wait_reason(string reason)
        {
            var clock = new FakeClock(Origin);
            JObject? running = reason == "sta-busy" ? Running("genexus_read/read", "attach-1", 900) : null;
            string? operation = reason == "sta-busy" ? "genexus_read/read" : null;
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, reason == "sta-busy" ? 5101 : 0, 10,
                Ack(KeyA, reason == "sta-busy" ? 5101 : 0, 2, running, new[]
                {
                    Entry(1, "attach-2", 400, reason, operation),
                    Entry(2, "attach-3", 200, reason, operation)
                }, childState: reason == "sta-busy" ? "ready" : reason == "worker-starting" ? "held" : "admitting"));

            var block = await RunAsync(clock, Build(clock, new[] { a }));

            var queue = (JArray)block["kbs"]![0]!["queue"]!;
            Assert.Equal(2, queue.Count);
            Assert.Equal(new[] { 1, 2 }, queue.Select(q => (int)q["position"]!).ToArray());
            foreach (var q in queue)
            {
                Assert.Equal(JTokenType.String, q["waitReason"]!.Type);
                Assert.Equal(reason, (string?)q["waitReason"]);
                Assert.False(string.IsNullOrEmpty((string?)q["ownerAttachId"]));
                Assert.True((long)q["waitMs"]! >= 0);
                Assert.Equal(reason == "sta-busy", q["operation"] != null);
            }
        }

        // ---------------------------------------------------------------
        // Requirements 5.6, 6.9, 11.3: machine-wide count, maximum and admission fields
        // ---------------------------------------------------------------

        [Fact]
        public async Task Machine_wide_count_maximum_and_admission_fields_come_from_live_records()
        {
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 10,
                Ack(KeyA, 5101, 1, null, Array.Empty<JObject>(), workingSetMb: 700));
            var records = new List<SharedHostDiagnosticsRecord>
            {
                Rec(KeyA, SharedHostRecordState.Ready, workerPid: 5101),
                Rec(KeyB, SharedHostRecordState.Ready, workerPid: 5202),
                Rec(KeyC, SharedHostRecordState.Starting, workerPid: 0),
                Rec("kb-key-d", SharedHostRecordState.Held, workerPid: 0, heldSinceUtc: Origin.AddMilliseconds(-3000)),
                Rec("kb-key-e", SharedHostRecordState.Ready, workerPid: 5505, liveness: RecordLiveness.Stale),
                Rec("kb-key-f", SharedHostRecordState.Ready, workerPid: 5606, liveness: RecordLiveness.LivenessUnverified)
            };
            var diag = Build(clock, new[] { a }, records: records, settings: Settings(new JObject { ["MaxWorkers"] = 6 }),
                workingSet: pid => pid == 5202 ? 450 : (long?)null, memoryMb: 4096);

            var block = await RunAsync(clock, diag);

            var workers = (JObject)block["workers"]!;
            Assert.Equal(6, (int)workers["maxWorkers"]!);
            Assert.Equal(2, (int)workers["counted"]!);
            var processes = (JArray)workers["processes"]!;
            Assert.Equal(new[] { 5101, 5202 }, processes.Select(p => (int)p["pid"]!).ToArray());
            Assert.Equal(new[] { KeyA, KeyB }, processes.Select(p => (string?)p["identityKey"]).ToArray());
            Assert.Equal(700, (long)processes[0]["workingSetMb"]!);
            Assert.Equal(450, (long)processes[1]["workingSetMb"]!);

            var admission = (JObject)block["admission"]!;
            Assert.Equal(4096, (long)admission["availableMemoryMb"]!);
            Assert.Equal(1024, (int)admission["memoryFloorMb"]!);
            Assert.Equal(1, (int)admission["coldStartConcurrency"]!);
            Assert.Equal(1, (int)admission["coldStartsInProgress"]!);
            var held = Assert.Single((JArray)admission["heldStarts"]!);
            Assert.Equal("kb-key-d", (string?)held["identityKey"]);
            long heldElapsed = (long)held["elapsedMs"]!;
            Assert.InRange(heldElapsed, 3000, 3000 + 200);
            Assert.Equal(JTokenType.Null, held["waiting"]!.Type);
        }

        [Fact]
        public async Task Held_start_of_an_open_kb_counts_its_worker_starting_waiters()
        {
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 0, 10,
                Ack(KeyA, 0, 2, null, new[]
                {
                    Entry(1, "attach-1", 1200, "worker-starting", null),
                    Entry(2, "attach-2", 900, "worker-starting", null)
                }, childState: "held"));
            var records = new List<SharedHostDiagnosticsRecord>
            {
                Rec(KeyA, SharedHostRecordState.Held, workerPid: 0, heldSinceUtc: Origin.AddMilliseconds(-1200))
            };

            var block = await RunAsync(clock, Build(clock, new[] { a }, records: records));

            var held = Assert.Single((JArray)block["admission"]!["heldStarts"]!);
            Assert.Equal(KeyA, (string?)held["identityKey"]);
            Assert.Equal(2, (int)held["waiting"]!);
            Assert.Equal(0, (int)block["workers"]!["counted"]!);
        }

        [Fact]
        public async Task Memory_is_read_once_per_call_after_the_host_queries_and_within_1000_ms_of_the_response()
        {
            var clock = new FakeClock(Origin);
            var reads = new List<DateTime>();
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 900,
                Ack(KeyA, 5101, 1, null, Array.Empty<JObject>()));
            var diag = Build(clock, new[] { a }, memory: (out long mb, out string? error) =>
            {
                reads.Add(clock.UtcNow);
                mb = 2048;
                error = null;
                return true;
            });

            var first = await RunAsync(clock, diag);
            var second = await RunAsync(clock, diag);

            Assert.Equal(2, reads.Count);
            Assert.True(reads[0] >= Origin.AddMilliseconds(900), "memory must be read after the host queries");
            foreach (var (block, readAt) in new[] { (first, reads[0]), (second, reads[1]) })
            {
                var admission = (JObject)block["admission"]!;
                Assert.Equal(2048, (long)admission["availableMemoryMb"]!);
                Assert.Equal(JTokenType.String, admission["memoryReadAtUtc"]!.Type);
                Assert.Equal(readAt, ReadUtc(admission["memoryReadAtUtc"]!));
                var checkedAt = ReadUtc(block["checkedAtUtc"]!);
                Assert.InRange((checkedAt - readAt).TotalMilliseconds, 0, 1000);
            }
        }

        [Fact]
        public async Task Unreadable_memory_is_reported_as_unavailable()
        {
            var clock = new FakeClock(Origin);
            var diag = Build(clock, Array.Empty<FakeHost>(), memory: (out long mb, out string? error) =>
            {
                mb = 0;
                error = "counter not available";
                return false;
            });

            var block = await RunAsync(clock, diag);

            Assert.Equal("unavailable", (string?)block["admission"]!["availableMemoryMb"]);
        }

        // ---------------------------------------------------------------
        // Requirements 2.9, 3.12, 10.2: configured and effective settings
        // ---------------------------------------------------------------

        [Fact]
        public async Task Configured_settings_carry_their_sources_and_a_host_mismatch_raises_a_warning()
        {
            var env = new Dictionary<string, string> { ["GXMCP_SHARED_QUEUE_DEPTH"] = "16" };
            var settings = Settings(new JObject { ["AttachReadyTimeoutSeconds"] = 300 }, env);
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 10,
                Ack(KeyA, 5101, 1, null, Array.Empty<JObject>(), effective: HostSettings(queueDepth: 8, attachReadyTimeoutSeconds: 120)));
            var records = new List<SharedHostDiagnosticsRecord> { Rec(KeyA, SharedHostRecordState.Ready, 5101) };

            var block = await RunAsync(clock, Build(clock, new[] { a }, records: records, settings: settings, env: env));

            var configured = (JObject)block["settings"]!["configured"]!;
            Assert.Equal(SettingNames.OrderBy(n => n, StringComparer.Ordinal), configured.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(16, (int)configured["queueDepth"]!["value"]!);
            Assert.Equal("env:GXMCP_SHARED_QUEUE_DEPTH", (string?)configured["queueDepth"]!["source"]);
            Assert.Equal(300, (int)configured["attachReadyTimeoutSeconds"]!["value"]!);
            Assert.Equal("config:Server.SharedHost.AttachReadyTimeoutSeconds", (string?)configured["attachReadyTimeoutSeconds"]!["source"]);
            Assert.Equal("default", (string?)configured["maxWorkers"]!["source"]);

            Assert.Equal(300, (int)block["attachReadinessWait"]!["seconds"]!);
            Assert.Equal("config:Server.SharedHost.AttachReadyTimeoutSeconds", (string?)block["attachReadinessWait"]!["source"]);

            var kbSettings = (JObject)block["kbs"]![0]!["settings"]!;
            Assert.Equal("host", (string?)kbSettings["effective"]!["source"]);
            Assert.Equal(LaunchingGatewayPid, (int)kbSettings["effective"]!["launchedByGatewayPid"]!);
            Assert.Equal(8, (int)kbSettings["effective"]!["values"]!["queueDepth"]!);
            Assert.Equal(new[] { "attachReadyTimeoutSeconds", "queueDepth" },
                kbSettings["mismatches"]!.Select(t => (string?)t).ToArray());

            var warning = Assert.Single((JArray)block["warnings"]!).ToString();
            Assert.Contains(KeyA, warning, StringComparison.Ordinal);
            Assert.Contains("queueDepth", warning, StringComparison.Ordinal);
            Assert.Contains("attachReadyTimeoutSeconds", warning, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Default_settings_report_every_documented_default_and_no_warning()
        {
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 10,
                Ack(KeyA, 5101, 1, null, Array.Empty<JObject>(), effective: HostSettings()));

            var block = await RunAsync(clock, Build(clock, new[] { a }));

            var configured = (JObject)block["settings"]!["configured"]!;
            var expected = new Dictionary<string, int>
            {
                ["queueWaitMs"] = 30000, ["queueDepth"] = 8, ["sessionQueueCap"] = 4, ["maxWorkers"] = 4,
                ["memoryFloorMb"] = 1024, ["coldStartConcurrency"] = 1, ["attachReadyTimeoutSeconds"] = 120
            };
            foreach (var pair in expected)
            {
                Assert.Equal(pair.Value, (int)configured[pair.Key]!["value"]!);
                Assert.Equal("default", (string?)configured[pair.Key]!["source"]);
            }
            Assert.Equal(120, (int)block["attachReadinessWait"]!["seconds"]!);
            Assert.Equal("default", (string?)block["attachReadinessWait"]!["source"]);
            Assert.Empty((JArray)block["kbs"]![0]!["settings"]!["mismatches"]!);
            Assert.Empty((JArray)block["warnings"]!);
        }

        [Fact]
        public async Task Legacy_busy_wait_source_is_reported_for_the_queue_wait()
        {
            var env = new Dictionary<string, string> { ["GXMCP_BUSY_WAIT_MS"] = "45000" };
            var clock = new FakeClock(Origin);

            var block = await RunAsync(clock, Build(clock, Array.Empty<FakeHost>(), settings: Settings(null, env), env: env));

            var queueWait = (JObject)block["settings"]!["configured"]!["queueWaitMs"]!;
            Assert.Equal(45000, (int)queueWait["value"]!);
            Assert.Equal("legacy-env:GXMCP_BUSY_WAIT_MS", (string?)queueWait["source"]);
        }

        // ---------------------------------------------------------------
        // Requirement 3.13: GXMCP_BUSY_REJECT_MS is ignored in Shared_Host_Mode
        // ---------------------------------------------------------------

        [Fact]
        public async Task Busy_reject_ms_is_reported_as_ignored_when_set()
        {
            var env = new Dictionary<string, string> { ["GXMCP_BUSY_REJECT_MS"] = "250" };
            var clock = new FakeClock(Origin);

            var block = await RunAsync(clock, Build(clock, Array.Empty<FakeHost>(), env: env));

            var ignored = Assert.Single((JArray)block["ignored"]!);
            Assert.Equal("GXMCP_BUSY_REJECT_MS", (string?)ignored["name"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)ignored["reason"]));
        }

        [Fact]
        public async Task Busy_reject_ms_unset_leaves_the_ignored_list_empty()
        {
            var clock = new FakeClock(Origin);

            var block = await RunAsync(clock, Build(clock, Array.Empty<FakeHost>()));

            Assert.Empty((JArray)block["ignored"]!);
        }

        // ---------------------------------------------------------------
        // Requirements 11.7, 11.8: timeouts, parallel queries, unreachable hosts
        // ---------------------------------------------------------------

        [Fact]
        public async Task Status_queries_run_in_parallel_with_a_1000_ms_per_host_timeout()
        {
            var clock = new FakeClock(Origin);
            var hosts = new[]
            {
                new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 900, Ack(KeyA, 5101, 1, null, Array.Empty<JObject>())),
                new FakeHost(clock, KeyB, new[] { "kbb" }, 4202, 5202, 900, Ack(KeyB, 5202, 1, null, Array.Empty<JObject>())),
                new FakeHost(clock, KeyC, new[] { "kbc" }, 4303, 5303, 900, Ack(KeyC, 5303, 1, null, Array.Empty<JObject>()))
            };

            var block = await RunAsync(clock, Build(clock, hosts));

            Assert.Equal(1000, SharedHostDiagnostics.PerHostTimeoutMs);
            Assert.Equal(2000, SharedHostDiagnostics.TotalTimeoutMs);
            foreach (var host in hosts)
            {
                var call = Assert.Single(host.Calls);
                Assert.Equal(0, call.AtMs);
                Assert.Equal(1000, call.TimeoutMs);
            }
            Assert.All((JArray)block["kbs"]!, k => Assert.True((bool)k["reachable"]!));
            Assert.InRange((long)block["elapsedMs"]!, 900, 999);
        }

        [Fact]
        public async Task A_silent_host_is_unreachable_after_1000_ms_while_the_other_kb_still_reports()
        {
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 200, Ack(KeyA, 5101, 1, null, Array.Empty<JObject>()));
            var b = new FakeHost(clock, KeyB, new[] { "kbb" }, 4202, 5202, null, Ack(KeyB, 5202, 1, null, Array.Empty<JObject>()));

            var block = await RunAsync(clock, Build(clock, new[] { a, b }));

            var kbs = (JArray)block["kbs"]!;
            Assert.True((bool)kbs[0]["reachable"]!);
            Assert.Equal(5101, (int)kbs[0]["workerPid"]!);
            var silent = (JObject)kbs[1];
            Assert.False((bool)silent["reachable"]!);
            Assert.Equal("status-timeout", (string?)silent["error"]);
            Assert.Equal(4202, (int)silent["hostPid"]!);
            Assert.Equal(5202, (int)silent["workerPid"]!);
            Assert.Equal(JTokenType.Null, silent["lastContactUtc"]!.Type);
            Assert.Null(silent["running"]);
            Assert.InRange((long)block["elapsedMs"]!, 1000, 1100);
        }

        [Fact]
        public async Task An_unreachable_host_keeps_its_last_known_pids_and_last_contact_time()
        {
            var clock = new FakeClock(Origin);
            // The pool view still holds the attach-time Worker PID; the first status_ack reports the replacement.
            var b = new FakeHost(clock, KeyB, new[] { "kbb" }, 4202, 5202, 100, Ack(KeyB, 5299, 1, null, Array.Empty<JObject>()));
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 100, Ack(KeyA, 5101, 1, null, Array.Empty<JObject>()));
            var diag = Build(clock, new[] { a, b });

            var first = await RunAsync(clock, diag);
            var contact = ReadUtc(first["kbs"]![1]!["lastContactUtc"]!);
            clock.Jump(5000);
            b.LatencyMs = null;
            var second = await RunAsync(clock, diag);

            var silent = (JObject)second["kbs"]![1]!;
            Assert.Equal(KeyB, (string?)silent["identityKey"]);
            Assert.False((bool)silent["reachable"]!);
            Assert.Equal(4202, (int)silent["hostPid"]!);
            Assert.Equal(5299, (int)silent["workerPid"]!);
            Assert.Equal(JTokenType.String, silent["lastContactUtc"]!.Type);
            Assert.Equal(contact, ReadUtc(silent["lastContactUtc"]!));
            Assert.True((bool)second["kbs"]![0]!["reachable"]!);
        }

        [Fact]
        public async Task The_total_budget_of_2000_ms_shortens_the_per_host_timeout()
        {
            var clock = new FakeClock(Origin);
            var a = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, null, Ack(KeyA, 5101, 1, null, Array.Empty<JObject>()));
            var b = new FakeHost(clock, KeyB, new[] { "kbb" }, 4202, 5202, null, Ack(KeyB, 5202, 1, null, Array.Empty<JObject>()));
            // A slow pool view consumes 1500 ms of the budget before any host is queried.
            var diag = Build(clock, new[] { a, b }, onPoolView: () => clock.Jump(1500));

            var block = await RunAsync(clock, diag);

            foreach (var host in new[] { a, b })
            {
                var call = Assert.Single(host.Calls);
                Assert.InRange(call.TimeoutMs, 1, 500);
            }
            Assert.All((JArray)block["kbs"]!, k => Assert.False((bool)k["reachable"]!));
            Assert.InRange((long)block["elapsedMs"]!, 1500, 2000 + StepMs);
        }

        [Fact]
        public async Task Diagnostics_reach_hosts_only_through_the_status_frame_and_answer_while_every_worker_is_busy()
        {
            // Requirement 11.7: the only delegate a host view exposes is RequestStatusAsync, so diagnostics cannot be
            // queued behind an SDK_Operation; with every Worker busy and every queue full the block still returns.
            var delegateMembers = typeof(SharedHostDiagnosticsHost)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => typeof(Delegate).IsAssignableFrom(p.PropertyType))
                .Select(p => p.Name)
                .ToArray();
            Assert.Equal(new[] { "RequestStatusAsync" }, delegateMembers);

            var clock = new FakeClock(Origin);
            JObject[] FullQueue() => Enumerable.Range(1, 8)
                .Select(i => Entry(i, "attach-" + i, 100 * i, "sta-busy", "genexus_lifecycle/build")).ToArray();
            var hosts = new[]
            {
                new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 300, Ack(KeyA, 5101, 3, Running("genexus_lifecycle/build", "attach-1", 60000), FullQueue())),
                new FakeHost(clock, KeyB, new[] { "kbb" }, 4202, 5202, 300, Ack(KeyB, 5202, 3, Running("genexus_lifecycle/build", "attach-2", 60000), FullQueue()))
            };
            var diag = Build(clock, hosts);
            var wall = Stopwatch.StartNew();

            var block = await RunAsync(clock, diag);

            wall.Stop();
            Assert.True(wall.ElapsedMilliseconds < 2000, "diagnostics took " + wall.ElapsedMilliseconds + " ms of real time");
            Assert.All(hosts, h => Assert.Single(h.Calls));
            Assert.All((JArray)block["kbs"]!, k => Assert.Equal(8, (int)k["queueDepth"]!));
            Assert.InRange((long)block["elapsedMs"]!, 0, 2000);
        }

        // ---------------------------------------------------------------
        // Requirements 10.1, 11.9: mode detection and the three tools
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(null, false)]
        [InlineData("isolated", false)]
        [InlineData("Isolated", false)]
        [InlineData("shared-host", true)]
        [InlineData(" SHARED-HOST ", true)]
        public void Shared_host_mode_is_detected_from_the_worker_sharing_mode(string? mode, bool expected)
        {
            Assert.False(SharedHostDiagnostics.IsSharedHostMode(null));
            var config = new Configuration { Server = new ServerConfig { WorkerSharingMode = mode! } };

            Assert.Equal(expected, SharedHostDiagnostics.IsSharedHostMode(config));
        }

        [Fact]
        public async Task Isolated_mode_builds_no_block_and_never_calls_the_factory()
        {
            int factoryCalls = 0;
            using var state = Program.ConfigureRouteStateForTest(IsolatedConfig(), "shared-diag-isolated.json");
            Program.SharedHostDiagnosticsFactoryForTest = () => { factoryCalls++; return InstantDiagnostics(); };
            try
            {
                Assert.Null(await Program.BuildSharedHostBlockAsync());
                Assert.Equal(0, factoryCalls);
            }
            finally
            {
                Program.SharedHostDiagnosticsFactoryForTest = null;
            }
        }

        [Fact]
        public async Task Whoami_carries_the_block_in_shared_mode_and_keeps_its_isolated_field_set()
        {
            JObject isolatedBaseline;
            using (Program.ConfigureRouteStateForTest(IsolatedConfig(), "shared-diag-whoami.json"))
            {
                isolatedBaseline = await Program.BuildWhoamiPayloadAsync(false, null).WaitAsync(TimeSpan.FromSeconds(10));
            }
            Program.SharedHostDiagnosticsFactoryForTest = InstantDiagnostics;
            try
            {
                using (Program.ConfigureRouteStateForTest(IsolatedConfig(), "shared-diag-whoami.json"))
                {
                    var isolated = await Program.BuildWhoamiPayloadAsync(false, null).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.False(isolated.ContainsKey("sharedHost"));
                    Assert.Equal(Keys(isolatedBaseline), Keys(isolated));
                }
                using (Program.ConfigureRouteStateForTest(SharedConfig(), "shared-diag-whoami.json"))
                {
                    var shared = await Program.BuildWhoamiPayloadAsync(false, null).WaitAsync(TimeSpan.FromSeconds(10));
                    var block = Assert.IsType<JObject>(shared["sharedHost"]);
                    Assert.Equal("shared-host", (string?)block["mode"]);
                    Assert.Equal(KeyA, (string?)block["kbs"]![0]!["identityKey"]);
                }
            }
            finally
            {
                Program.SharedHostDiagnosticsFactoryForTest = null;
            }
        }

        [Fact]
        public void Doctor_puts_the_block_under_result_and_appends_its_warnings()
        {
            var block = new JObject
            {
                ["mode"] = "shared-host",
                ["warnings"] = new JArray("Host settings for kb-key-a differ from this Gateway: queueDepth.")
            };

            var withBlock = (JObject)Program.BuildGatewayDoctorEnvelope(null, null, block)["result"]!;
            var without = (JObject)Program.BuildGatewayDoctorEnvelope(null, null, null)["result"]!;
            var legacy = (JObject)Program.BuildGatewayDoctorEnvelope(null, null)["result"]!;

            Assert.True(JToken.DeepEquals(block, withBlock["sharedHost"]));
            Assert.Contains(((JArray)withBlock["warnings"]!).Select(w => w.ToString()),
                w => w.Contains("kb-key-a", StringComparison.Ordinal) && w.Contains("queueDepth", StringComparison.Ordinal));
            Assert.False(without.ContainsKey("sharedHost"));
            Assert.Equal(Keys(legacy), Keys(without));
            Assert.DoesNotContain(((JArray)without["warnings"]!).Select(w => w.ToString()),
                w => w.Contains("kb-key-a", StringComparison.Ordinal));
        }

        [Fact]
        public void Lifecycle_status_envelope_carries_the_block_only_when_given()
        {
            var job = new JobEntry { Id = "diagjob1", EstimatedSeconds = 30 };
            var payload = new JObject { ["status"] = "running" };
            var block = new JObject { ["mode"] = "shared-host", ["kbs"] = new JArray() };

            var withBlock = Program.BuildWorkerLifecycleStatusEnvelope(job, payload, new JObject(), 0, "change", block);
            var without = Program.BuildWorkerLifecycleStatusEnvelope(job, payload, new JObject(), 0, "change", null);
            var legacy = Program.BuildWorkerLifecycleStatusEnvelope(job, payload, new JObject(), 0, "change");

            Assert.True(JToken.DeepEquals(block, withBlock["sharedHost"]));
            Assert.False(without.ContainsKey("sharedHost"));
            Assert.Equal(Keys(legacy), Keys(without));
        }

        [Theory]
        [InlineData("genexus_whoami")]
        [InlineData("genexus_doctor")]
        [InlineData("genexus_lifecycle")]
        public void Diagnostics_tool_schemas_stay_equal_to_the_contract_golden_fixture(string toolName)
        {
            // The golden fixture is the published tools/list response (compacted descriptions, no examples), so the
            // live schema is read from the same tools/list call, not from the raw tool_definitions.json.
            var listed = McpRouter.Handle(new JObject { ["jsonrpc"] = "2.0", ["id"] = "1", ["method"] = "tools/list" });
            var live = (JArray)(listed as JToken ?? JToken.FromObject(listed!))["tools"]!;
            var golden = (JArray)JObject.Parse(RepoSource.Read(
                "src", "GxMcp.Gateway.Tests", "Fixtures", "Contract", "Discovery", "tools-list.response.json"))["tools"]!;

            var definition = live.Single(t => (string?)t["name"] == toolName);
            var fixture = golden.Single(t => (string?)t["name"] == toolName);

            Assert.True(JToken.DeepEquals(fixture["inputSchema"], definition["inputSchema"]),
                toolName + " inputSchema differs from the contract-golden tools-list fixture.");
            Assert.DoesNotContain("sharedHost", fixture.ToString(), StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static async Task<JObject> RunAsync(FakeClock clock, SharedHostDiagnostics diag, long maxVirtualMs = 4000)
        {
            var task = diag.BuildAsync();
            // Let the builder issue its status calls at the current virtual time before time moves.
            await Task.WhenAny(task, Task.Delay(5));
            long advanced = 0;
            while (!task.IsCompleted && advanced < maxVirtualMs)
            {
                await clock.StepAsync(StepMs);
                advanced += StepMs;
                await Task.WhenAny(task, Task.Delay(2));
            }
            return await task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static SharedHostDiagnostics Build(
            FakeClock clock,
            IReadOnlyList<FakeHost> hosts,
            IReadOnlyList<SharedHostDiagnosticsRecord>? records = null,
            SharedHostSettingsResult? settings = null,
            IReadOnlyDictionary<string, string>? env = null,
            Func<int, long?>? workingSet = null,
            long memoryMb = 8192,
            AvailableMemoryReader? memory = null,
            Action? onPoolView = null)
        {
            var recordList = records ?? hosts.Select(h => Rec(h.Key, SharedHostRecordState.Ready, h.WorkerPid)).ToList();
            return new SharedHostDiagnostics(new SharedHostDiagnosticsInputs
            {
                PoolView = () =>
                {
                    onPoolView?.Invoke();
                    return hosts.Select(h => h.View()).ToList();
                },
                Clock = clock,
                ReadAvailableMemory = memory ?? ((out long mb, out string? error) =>
                {
                    mb = memoryMb;
                    error = null;
                    return true;
                }),
                ReadRegistry = () => recordList,
                ReadWorkingSetMb = workingSet ?? (_ => 256),
                Configured = settings ?? Settings(null),
                Environment = name => env != null && env.TryGetValue(name, out var value) ? value : null,
                GatewayPid = GatewayPid
            });
        }

        // A builder whose one host answers at once, for the Program wiring tests.
        private static SharedHostDiagnostics InstantDiagnostics()
        {
            var clock = new FakeClock(Origin);
            var host = new FakeHost(clock, KeyA, new[] { "kba" }, 4101, 5101, 0, Ack(KeyA, 5101, 1, null, Array.Empty<JObject>()));
            return Build(clock, new[] { host });
        }

        private static SharedHostSettingsResult Settings(JObject? section, IReadOnlyDictionary<string, string>? env = null)
        {
            var result = SharedHostSettingsResolver.Resolve(section, strictDocument: false, "stdio-isolated",
                name => env != null && env.TryGetValue(name, out var value) ? value : null);
            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            return result;
        }

        private static Configuration IsolatedConfig() => new Configuration
        {
            GatewayMode = "stdio-isolated",
            Server = new ServerConfig { WorkerSharingMode = "isolated" }
        };

        private static Configuration SharedConfig() => new Configuration
        {
            GatewayMode = "stdio-isolated",
            Server = new ServerConfig { WorkerSharingMode = "shared-host" },
            EffectiveSharedHost = Settings(null)
        };

        private static string[] Keys(JObject value) =>
            value.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        private static DateTime ReadUtc(JToken token)
        {
            if (token.Type == JTokenType.Date) return ((DateTime)token).ToUniversalTime();
            // RoundtripKind cannot be combined with AdjustToUniversal; the "o" string carries its own kind.
            return DateTime.Parse((string)token!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToUniversalTime();
        }

        private static SharedHostDiagnosticsRecord Rec(
            string key, string state, int workerPid, DateTime? heldSinceUtc = null,
            RecordLiveness liveness = RecordLiveness.Live) => new SharedHostDiagnosticsRecord
        {
            Record = new SharedHostRecord
            {
                KbKey = key,
                HostPid = 4000 + (workerPid % 1000),
                LaunchedByGatewayPid = LaunchingGatewayPid,
                WorkerPid = workerPid,
                State = state,
                HeldSinceUtc = heldSinceUtc,
                ColdStartStartedUtc = state == SharedHostRecordState.Starting ? Origin.AddMilliseconds(-500) : (DateTime?)null,
                ColdStartConcurrency = 1,
                Attachments = 1,
                UpdatedUtc = Origin
            },
            Liveness = liveness
        };

        private static JObject HostSettings(int queueDepth = 8, int attachReadyTimeoutSeconds = 120) => new JObject
        {
            ["queueWaitMs"] = 30000,
            ["queueDepth"] = queueDepth,
            ["sessionQueueCap"] = 4,
            ["maxWorkers"] = 4,
            ["memoryFloorMb"] = 1024,
            ["coldStartConcurrency"] = 1,
            ["attachReadyTimeoutSeconds"] = attachReadyTimeoutSeconds,
            ["heapRecycleMb"] = 1500
        };

        private static JObject Running(string operation, string owner, long elapsedMs) => new JObject
        {
            ["operation"] = operation,
            ["ownerAttachId"] = owner,
            ["elapsedMs"] = elapsedMs
        };

        private static JObject Entry(int position, string owner, long waitMs, string reason, string? operation)
        {
            var entry = new JObject
            {
                ["position"] = position,
                ["ownerAttachId"] = owner,
                ["waitMs"] = waitMs,
                ["waitReason"] = reason
            };
            if (operation != null) entry["operation"] = operation;
            return entry;
        }

        // The status_ack frame as SharedWorkerHostProtocol.BuildStatusAck writes it.
        private static JObject Ack(
            string key, int workerPid, int attachments, JObject? running, IReadOnlyList<JObject> queue,
            string childState = "ready", long workingSetMb = 512, JObject? effective = null)
        {
            var frame = new JObject
            {
                ["type"] = "status_ack",
                ["gxmcp"] = "status_ack",
                ["protocolVersion"] = 2,
                ["version"] = 2,
                ["identityKey"] = key,
                ["key"] = key,
                ["requestId"] = "status-1",
                ["childState"] = childState,
                ["workerPid"] = workerPid,
                ["workerWorkingSetMb"] = workingSetMb,
                ["idle"] = running == null,
                ["running"] = running ?? (JToken)JValue.CreateNull(),
                ["queue"] = new JArray(queue.Select(q => (object)q.DeepClone()).ToArray()),
                ["queueDepth"] = queue.Count,
                ["attachments"] = attachments,
                ["effectiveSettings"] = effective ?? HostSettings()
            };
            return frame;
        }

        private sealed class FakeHost
        {
            private readonly FakeClock _clock;
            private readonly JObject _ack;
            private readonly string[] _aliases;
            private readonly int _hostPid;

            internal FakeHost(FakeClock clock, string key, string[] aliases, int hostPid, int workerPid, int? latencyMs, JObject ack)
            {
                _clock = clock;
                Key = key;
                _aliases = aliases;
                _hostPid = hostPid;
                WorkerPid = workerPid;
                LatencyMs = latencyMs;
                _ack = ack;
            }

            internal string Key { get; }
            internal int WorkerPid { get; }
            // Null: the host never answers and ignores both its timeout and cancellation.
            internal int? LatencyMs { get; set; }
            internal List<(long AtMs, int TimeoutMs)> Calls { get; } = new List<(long AtMs, int TimeoutMs)>();

            internal SharedHostDiagnosticsHost View() => new SharedHostDiagnosticsHost
            {
                IdentityKey = Key,
                Aliases = _aliases,
                HostPid = _hostPid,
                WorkerPid = WorkerPid,
                RequestStatusAsync = RequestStatusAsync
            };

            private async Task<JObject?> RequestStatusAsync(int timeoutMs, CancellationToken cancellationToken)
            {
                int? latency;
                lock (Calls)
                {
                    Calls.Add((_clock.NowMs, timeoutMs));
                    latency = LatencyMs;
                }
                if (latency == null)
                {
                    await new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
                    return null;
                }
                if (latency.Value > 0) await _clock.Delay(latency.Value, CancellationToken.None);
                return (JObject)_ack.DeepClone();
            }
        }

        // Virtual time. Delay registers a timer; StepAsync moves time forward and fires the due timers.
        private sealed class FakeClock : ISharedHostDiagnosticsClock
        {
            private readonly object _gate = new object();
            private readonly DateTime _origin;
            private readonly List<(long DueMs, TaskCompletionSource<bool> Signal)> _timers = new List<(long, TaskCompletionSource<bool>)>();
            private long _nowMs;

            internal FakeClock(DateTime origin) => _origin = origin;

            internal long NowMs { get { lock (_gate) return _nowMs; } }

            public DateTime UtcNow { get { lock (_gate) return _origin.AddMilliseconds(_nowMs); } }

            public Task Delay(int milliseconds, CancellationToken cancellationToken)
            {
                if (milliseconds <= 0) return Task.CompletedTask;
                var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _timers.Add((_nowMs + milliseconds, signal));
                if (cancellationToken.CanBeCanceled)
                    cancellationToken.Register(() => signal.TrySetCanceled(cancellationToken));
                return signal.Task;
            }

            // Moves time without firing timers; the next StepAsync fires anything now due.
            internal void Jump(long milliseconds)
            {
                lock (_gate) _nowMs += milliseconds;
            }

            internal async Task StepAsync(long milliseconds)
            {
                List<TaskCompletionSource<bool>> due;
                lock (_gate)
                {
                    _nowMs += milliseconds;
                    due = _timers.Where(t => t.DueMs <= _nowMs).OrderBy(t => t.DueMs).Select(t => t.Signal).ToList();
                    _timers.RemoveAll(t => t.DueMs <= _nowMs);
                }
                foreach (var signal in due) signal.TrySetResult(true);
                await Task.Delay(2).ConfigureAwait(false);
            }
        }
    }
}
