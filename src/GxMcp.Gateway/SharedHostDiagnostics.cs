#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Time source of <see cref="SharedHostDiagnostics"/>. Every timestamp and every timeout of the builder goes
    /// through it, so tests can run the builder on virtual time.
    /// </summary>
    internal interface ISharedHostDiagnosticsClock
    {
        DateTime UtcNow { get; }

        Task Delay(int milliseconds, CancellationToken cancellationToken);
    }

    internal sealed class SystemSharedHostDiagnosticsClock : ISharedHostDiagnosticsClock
    {
        internal static readonly SystemSharedHostDiagnosticsClock Instance = new SystemSharedHostDiagnosticsClock();

        public DateTime UtcNow => DateTime.UtcNow;

        public Task Delay(int milliseconds, CancellationToken cancellationToken) =>
            Task.Delay(Math.Max(0, milliseconds), cancellationToken);
    }

    /// <summary>
    /// One open identity key of this Gateway. <see cref="RequestStatusAsync"/> is the only way the builder reaches
    /// a host: the "status" control frame, never a Worker command or a Request_Queue (Requirement 11 criterion 7).
    /// </summary>
    internal sealed class SharedHostDiagnosticsHost
    {
        public string IdentityKey { get; init; } = string.Empty;

        public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

        public int? HostPid { get; init; }

        public int? WorkerPid { get; init; }

        public Func<int, CancellationToken, Task<JObject?>> RequestStatusAsync { get; init; } =
            (_, _) => Task.FromResult<JObject?>(null);
    }

    /// <summary>One classified registry v2 record.</summary>
    internal sealed class SharedHostDiagnosticsRecord
    {
        public SharedHostRecord Record { get; init; } = new SharedHostRecord();

        public RecordLiveness Liveness { get; init; }
    }

    internal sealed class SharedHostDiagnosticsInputs
    {
        public Func<IReadOnlyList<SharedHostDiagnosticsHost>> PoolView { get; init; } =
            () => Array.Empty<SharedHostDiagnosticsHost>();

        public ISharedHostDiagnosticsClock Clock { get; init; } = SystemSharedHostDiagnosticsClock.Instance;

        public AvailableMemoryReader ReadAvailableMemory { get; init; } = MachineMemory.TryGetAvailablePhysicalMb;

        public Func<IReadOnlyList<SharedHostDiagnosticsRecord>> ReadRegistry { get; init; } =
            () => Array.Empty<SharedHostDiagnosticsRecord>();

        public Func<int, long?> ReadWorkingSetMb { get; init; } = _ => null;

        public SharedHostSettingsResult Configured { get; init; } =
            SharedHostSettingsResolver.Resolve(null, strictDocument: false, string.Empty, _ => null);

        public Func<string, string?> Environment { get; init; } = _ => null;

        public int GatewayPid { get; init; }
    }

    /// <summary>
    /// RFC #854, Requirements 1, 2.9, 3.12, 3.13, 4.2, 5.6, 6.9, 10.2 and 11: the "sharedHost" block of
    /// genexus_whoami, genexus_doctor and genexus_lifecycle action=status. It asks every open host for its
    /// status_ack in parallel (1000 ms per host, 2000 ms in total), then reads the registry v2 records and the
    /// available memory locally. Only documented status_ack fields are copied, never whole host objects, so no
    /// request data can reach the block (Requirement 11 criterion 5); effectiveSettings is copied by contract.
    /// One instance per Gateway: it remembers the last successful contact of each identity key.
    /// </summary>
    internal sealed class SharedHostDiagnostics
    {
        internal const int PerHostTimeoutMs = 1000;
        internal const int TotalTimeoutMs = 2000;

        private const string SharedHostMode = "shared-host";
        private const string BusyRejectVariable = "GXMCP_BUSY_REJECT_MS";
        private const string StatusTimeout = "status-timeout";
        private const string StatusFailed = "status-failed";
        private const string WorkerStartingReason = "worker-starting";

        // Output name -> Server.SharedHost key, in the documented order.
        private static readonly (string Name, string Key)[] SettingNames =
        {
            ("queueWaitMs", SharedHostSettingsResolver.QueueWaitMsKey),
            ("queueDepth", SharedHostSettingsResolver.QueueDepthKey),
            ("sessionQueueCap", SharedHostSettingsResolver.SessionQueueCapKey),
            ("maxWorkers", SharedHostSettingsResolver.MaxWorkersKey),
            ("memoryFloorMb", SharedHostSettingsResolver.MemoryFloorMbKey),
            ("coldStartConcurrency", SharedHostSettingsResolver.ColdStartConcurrencyKey),
            ("attachReadyTimeoutSeconds", SharedHostSettingsResolver.AttachReadyTimeoutSecondsKey),
        };

        private sealed class Contact
        {
            internal DateTime Utc;
            internal int? WorkerPid;
        }

        private readonly SharedHostDiagnosticsInputs _inputs;
        private readonly object _contactLock = new object();
        private readonly Dictionary<string, Contact> _lastContact = new Dictionary<string, Contact>(StringComparer.Ordinal);

        internal SharedHostDiagnostics(SharedHostDiagnosticsInputs inputs)
        {
            _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        }

        /// <summary>True only when Server.WorkerSharingMode is "shared-host" (trimmed, case-insensitive).</summary>
        internal static bool IsSharedHostMode(Configuration? config) =>
            string.Equals(config?.Server?.WorkerSharingMode?.Trim(), SharedHostMode, StringComparison.OrdinalIgnoreCase);

        internal async Task<JObject> BuildAsync(CancellationToken cancellationToken = default)
        {
            var clock = _inputs.Clock;
            DateTime start = clock.UtcNow;
            var hosts = ReadPoolView();

            long elapsedBeforeQueries = Math.Max(0L, (long)(clock.UtcNow - start).TotalMilliseconds);
            int timeoutMs = (int)Math.Min(PerHostTimeoutMs, TotalTimeoutMs - elapsedBeforeQueries);

            var answers = new (JObject? Ack, string? Error)[hosts.Count];
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var queries = new Task<(JObject? Ack, string? Error)>[hosts.Count];
                for (int i = 0; i < hosts.Count; i++) queries[i] = QueryAsync(hosts[i], timeoutMs, linked.Token);
                await Task.WhenAll(queries).ConfigureAwait(false);
                for (int i = 0; i < hosts.Count; i++) answers[i] = queries[i].Result;
                // Releases the losing timers and any host request still pending.
                linked.Cancel();
            }
            cancellationToken.ThrowIfCancellationRequested();

            var records = ReadRecords();
            DateTime registryReadAt = clock.UtcNow;

            // Memory is read last, once per call.
            DateTime memoryReadAt = clock.UtcNow;
            JToken availableMemory;
            try
            {
                availableMemory = _inputs.ReadAvailableMemory(out long availableMb, out _)
                    ? new JValue(availableMb)
                    : new JValue("unavailable");
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                availableMemory = new JValue("unavailable");
            }
            DateTime checkedAt = clock.UtcNow;

            var configured = _inputs.Configured;
            var acksByKey = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var order = Enumerable.Range(0, hosts.Count)
                .OrderBy(i => hosts[i].IdentityKey, StringComparer.Ordinal)
                .ToArray();

            var kbs = new JArray();
            var warnings = new JArray();
            foreach (int i in order)
            {
                var host = hosts[i];
                var (ack, error) = answers[i];
                if (ack != null)
                {
                    acksByKey[host.IdentityKey] = ack;
                    var entry = BuildReachable(host, ack, checkedAt, records, configured, out var mismatches);
                    kbs.Add(entry);
                    if (mismatches.Count > 0)
                        warnings.Add(MismatchWarning(host.IdentityKey, mismatches, LaunchedBy(records, host.IdentityKey)));
                }
                else
                {
                    kbs.Add(BuildUnreachable(host, error ?? StatusTimeout));
                }
            }

            return new JObject
            {
                ["mode"] = SharedHostMode,
                ["gatewayPid"] = _inputs.GatewayPid,
                ["checkedAtUtc"] = Iso(checkedAt),
                ["elapsedMs"] = Math.Max(0L, (long)(checkedAt - start).TotalMilliseconds),
                ["attachReadinessWait"] = new JObject
                {
                    ["seconds"] = configured.AttachReadyTimeoutSeconds,
                    ["source"] = SourceOf(configured, SharedHostSettingsResolver.AttachReadyTimeoutSecondsKey)
                },
                ["settings"] = new JObject { ["configured"] = BuildConfigured(configured) },
                ["ignored"] = BuildIgnored(),
                ["warnings"] = warnings,
                ["workers"] = BuildWorkers(records, acksByKey, configured),
                ["admission"] = BuildAdmission(records, acksByKey, configured, availableMemory, memoryReadAt, registryReadAt),
                ["kbs"] = kbs
            };
        }

        private IReadOnlyList<SharedHostDiagnosticsHost> ReadPoolView()
        {
            return _inputs.PoolView()
                .Where(h => h != null && !string.IsNullOrEmpty(h.IdentityKey))
                .ToList();
        }

        private IReadOnlyList<SharedHostDiagnosticsRecord> ReadRecords()
        {
            return _inputs.ReadRegistry()
                .Where(r => r?.Record != null)
                .ToList();
        }

        // One status query raced against the clock, so a source that ignores its timeout or cancellation cannot
        // hold the call past timeoutMs.
        private async Task<(JObject? Ack, string? Error)> QueryAsync(
            SharedHostDiagnosticsHost host, int timeoutMs, CancellationToken cancellationToken)
        {
            if (timeoutMs <= 0) return (null, StatusTimeout);

            Task<JObject?> request;
            try
            {
                request = host.RequestStatusAsync(timeoutMs, cancellationToken) ?? Task.FromResult<JObject?>(null);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return (null, StatusFailed);
            }

            Task delay = _inputs.Clock.Delay(timeoutMs, cancellationToken);
            Task winner = await Task.WhenAny(request, delay).ConfigureAwait(false);
            if (winner != request)
            {
                // Observe a late fault so it is never reported as unobserved.
                _ = request.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return (null, StatusTimeout);
            }

            try
            {
                JObject? ack = await request.ConfigureAwait(false);
                return ack == null ? (null, StatusTimeout) : (ack, null);
            }
            catch (OperationCanceledException)
            {
                return (null, StatusTimeout);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return (null, StatusFailed);
            }
        }

        private JObject BuildReachable(
            SharedHostDiagnosticsHost host,
            JObject ack,
            DateTime checkedAt,
            IReadOnlyList<SharedHostDiagnosticsRecord> records,
            SharedHostSettingsResult configured,
            out List<string> mismatches)
        {
            int? workerPid = IntValue(ack["workerPid"]);
            lock (_contactLock)
            {
                _lastContact[host.IdentityKey] = new Contact { Utc = checkedAt, WorkerPid = workerPid ?? host.WorkerPid };
            }

            var queue = new JArray();
            if (ack["queue"] is JArray ackQueue)
            {
                foreach (var item in ackQueue.OfType<JObject>())
                {
                    var entry = new JObject
                    {
                        ["position"] = IntValue(item["position"]) ?? 0,
                        ["ownerAttachId"] = StringValue(item["ownerAttachId"]),
                        ["waitMs"] = Math.Max(0L, LongValue(item["waitMs"]) ?? 0L),
                        ["waitReason"] = StringValue(item["waitReason"])
                    };
                    string? operation = StringValue(item["operation"]);
                    if (!string.IsNullOrEmpty(operation)) entry["operation"] = operation;
                    queue.Add(entry);
                }
            }

            JToken running = JValue.CreateNull();
            if (ack["running"] is JObject ackRunning)
            {
                running = new JObject
                {
                    ["operation"] = StringValue(ackRunning["operation"]),
                    ["ownerAttachId"] = StringValue(ackRunning["ownerAttachId"]),
                    ["elapsedMs"] = Math.Max(0L, LongValue(ackRunning["elapsedMs"]) ?? 0L)
                };
            }

            var effective = ack["effectiveSettings"] is JObject settings ? (JObject)settings.DeepClone() : new JObject();
            mismatches = new List<string>();
            foreach (var (name, key) in SettingNames)
            {
                long? hostValue = LongValue(effective[name]);
                if (hostValue.HasValue && hostValue.Value != ConfiguredValue(configured, key)) mismatches.Add(name);
            }
            mismatches.Sort(StringComparer.Ordinal);

            int? launchedBy = LaunchedBy(records, host.IdentityKey);
            return new JObject
            {
                ["identityKey"] = host.IdentityKey,
                ["aliases"] = SortedAliases(host),
                ["reachable"] = true,
                ["hostPid"] = Nullable(host.HostPid),
                ["workerPid"] = Nullable(workerPid),
                ["attachments"] = IntValue(ack["attachments"]) ?? 0,
                ["childState"] = StringValue(ack["childState"]),
                ["queueDepth"] = IntValue(ack["queueDepth"]) ?? queue.Count,
                ["idle"] = BoolValue(ack["idle"]) ?? running.Type == JTokenType.Null,
                ["running"] = running,
                ["queue"] = queue,
                ["lastContactUtc"] = Iso(checkedAt),
                ["settings"] = new JObject
                {
                    ["effective"] = new JObject
                    {
                        ["source"] = "host",
                        ["launchedByGatewayPid"] = Nullable(launchedBy),
                        ["values"] = effective
                    },
                    ["mismatches"] = new JArray(mismatches.Cast<object>().ToArray())
                }
            };
        }

        private JObject BuildUnreachable(SharedHostDiagnosticsHost host, string error)
        {
            Contact? last;
            lock (_contactLock)
            {
                _lastContact.TryGetValue(host.IdentityKey, out last);
            }
            return new JObject
            {
                ["identityKey"] = host.IdentityKey,
                ["aliases"] = SortedAliases(host),
                ["reachable"] = false,
                ["error"] = error,
                ["hostPid"] = Nullable(host.HostPid),
                ["workerPid"] = Nullable(last?.WorkerPid ?? host.WorkerPid),
                ["lastContactUtc"] = last != null ? (JToken)Iso(last.Utc) : JValue.CreateNull()
            };
        }

        private static string MismatchWarning(string identityKey, IReadOnlyList<string> mismatches, int? launchedBy)
        {
            return "Host settings for " + identityKey + " differ from the settings configured for this Gateway: "
                + string.Join(", ", mismatches) + ". The host keeps the settings it was launched with"
                + (launchedBy.HasValue ? " by Gateway PID " + launchedBy.Value.ToString(CultureInfo.InvariantCulture) : string.Empty)
                + " until it restarts.";
        }

        private static JObject BuildConfigured(SharedHostSettingsResult configured)
        {
            var result = new JObject();
            foreach (var (name, key) in SettingNames)
            {
                result[name] = new JObject
                {
                    ["value"] = ConfiguredValue(configured, key),
                    ["source"] = SourceOf(configured, key)
                };
            }
            return result;
        }

        private JArray BuildIgnored()
        {
            var ignored = new JArray();
            string? busyReject = null;
            try { busyReject = _inputs.Environment(BusyRejectVariable); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
            if (!string.IsNullOrWhiteSpace(busyReject))
            {
                ignored.Add(new JObject
                {
                    ["name"] = BusyRejectVariable,
                    ["reason"] = "Shared_Host_Mode queues requests per KB and returns a Busy_Envelope only when the queue is full or the queue wait expires, so this Isolated_Mode setting has no effect."
                });
            }
            return ignored;
        }

        private JObject BuildWorkers(
            IReadOnlyList<SharedHostDiagnosticsRecord> records,
            IReadOnlyDictionary<string, JObject> acksByKey,
            SharedHostSettingsResult configured)
        {
            var counted = records
                .Where(r => r.Liveness == RecordLiveness.Live && r.Record.WorkerPid > 0)
                .OrderBy(r => r.Record.KbKey, StringComparer.Ordinal)
                .ToList();
            var processes = new JArray();
            foreach (var r in counted)
            {
                JToken workingSet;
                long? fromAck = acksByKey.TryGetValue(r.Record.KbKey, out var ack) ? LongValue(ack["workerWorkingSetMb"]) : null;
                if (fromAck.HasValue)
                {
                    workingSet = new JValue(fromAck.Value);
                }
                else
                {
                    long? measured = null;
                    try { measured = _inputs.ReadWorkingSetMb(r.Record.WorkerPid); }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
                    workingSet = measured.HasValue ? new JValue(measured.Value) : new JValue("unavailable");
                }
                processes.Add(new JObject
                {
                    ["identityKey"] = r.Record.KbKey,
                    ["pid"] = r.Record.WorkerPid,
                    ["workingSetMb"] = workingSet
                });
            }
            return new JObject
            {
                ["maxWorkers"] = configured.MaxWorkers,
                ["counted"] = counted.Count,
                ["processes"] = processes
            };
        }

        private static JObject BuildAdmission(
            IReadOnlyList<SharedHostDiagnosticsRecord> records,
            IReadOnlyDictionary<string, JObject> acksByKey,
            SharedHostSettingsResult configured,
            JToken availableMemory,
            DateTime memoryReadAt,
            DateTime now)
        {
            var live = records.Where(r => r.Liveness == RecordLiveness.Live).ToList();
            var heldStarts = new JArray();
            foreach (var r in live
                .Where(r => string.Equals(r.Record.State, SharedHostRecordState.Held, StringComparison.Ordinal))
                .OrderBy(r => r.Record.KbKey, StringComparer.Ordinal))
            {
                long elapsed = r.Record.HeldSinceUtc.HasValue
                    ? Math.Max(0L, (long)(now - r.Record.HeldSinceUtc.Value.ToUniversalTime()).TotalMilliseconds)
                    : 0L;
                JToken waiting = JValue.CreateNull();
                if (acksByKey.TryGetValue(r.Record.KbKey, out var ack))
                {
                    waiting = (ack["queue"] as JArray)?.OfType<JObject>()
                        .Count(q => string.Equals(StringValue(q["waitReason"]), WorkerStartingReason, StringComparison.Ordinal)) ?? 0;
                }
                heldStarts.Add(new JObject
                {
                    ["identityKey"] = r.Record.KbKey,
                    ["elapsedMs"] = elapsed,
                    ["waiting"] = waiting
                });
            }
            return new JObject
            {
                ["availableMemoryMb"] = availableMemory,
                ["memoryReadAtUtc"] = Iso(memoryReadAt),
                ["memoryFloorMb"] = configured.MemoryFloorMb,
                ["coldStartConcurrency"] = configured.ColdStartConcurrency,
                ["coldStartsInProgress"] = live.Count(r => string.Equals(r.Record.State, SharedHostRecordState.Starting, StringComparison.Ordinal)),
                ["heldStarts"] = heldStarts
            };
        }

        private static int? LaunchedBy(IReadOnlyList<SharedHostDiagnosticsRecord> records, string identityKey)
        {
            var record = records.FirstOrDefault(r => string.Equals(r.Record.KbKey, identityKey, StringComparison.Ordinal));
            return record != null && record.Record.LaunchedByGatewayPid > 0 ? record.Record.LaunchedByGatewayPid : (int?)null;
        }

        private static int ConfiguredValue(SharedHostSettingsResult configured, string key)
        {
            switch (key)
            {
                case SharedHostSettingsResolver.QueueWaitMsKey: return configured.QueueWaitMs;
                case SharedHostSettingsResolver.QueueDepthKey: return configured.QueueDepth;
                case SharedHostSettingsResolver.SessionQueueCapKey: return configured.SessionQueueCap;
                case SharedHostSettingsResolver.MaxWorkersKey: return configured.MaxWorkers;
                case SharedHostSettingsResolver.MemoryFloorMbKey: return configured.MemoryFloorMb;
                case SharedHostSettingsResolver.ColdStartConcurrencyKey: return configured.ColdStartConcurrency;
                case SharedHostSettingsResolver.AttachReadyTimeoutSecondsKey: return configured.AttachReadyTimeoutSeconds;
                default: throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown shared-host setting.");
            }
        }

        private static string SourceOf(SharedHostSettingsResult configured, string key) =>
            configured.Sources != null && configured.Sources.TryGetValue(key, out var source) && !string.IsNullOrEmpty(source)
                ? source
                : SharedHostSettingsResolver.DefaultSource;

        private static JArray SortedAliases(SharedHostDiagnosticsHost host) =>
            new JArray((host.Aliases ?? Array.Empty<string>())
                .Where(a => a != null)
                .OrderBy(a => a, StringComparer.Ordinal)
                .Cast<object>()
                .ToArray());

        private static string Iso(DateTime value) =>
            DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

        private static JToken Nullable(int? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();

        private static string? StringValue(JToken? token) =>
            token != null && token.Type == JTokenType.String ? (string?)token : null;

        private static bool? BoolValue(JToken? token) =>
            token != null && token.Type == JTokenType.Boolean ? (bool?)token : null;

        private static long? LongValue(JToken? token) =>
            token != null && token.Type == JTokenType.Integer ? (long?)token : null;

        private static int? IntValue(JToken? token)
        {
            long? value = LongValue(token);
            return value.HasValue && value.Value >= int.MinValue && value.Value <= int.MaxValue ? (int)value.Value : (int?)null;
        }

        /// <summary>
        /// The production builder of one Gateway: the pool view of <paramref name="pool"/>, the machine-wide
        /// registry v2 records classified with <see cref="ProcessIdentity"/>, <see cref="MachineMemory"/>, the
        /// process working set, and <paramref name="config"/>'s effective shared-host settings.
        /// </summary>
        internal static SharedHostDiagnostics CreateDefault(Configuration config, Func<WorkerPool?> pool)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (pool == null) throw new ArgumentNullException(nameof(pool));
            var probe = new ProcessIdentity();
            return new SharedHostDiagnostics(new SharedHostDiagnosticsInputs
            {
                PoolView = () => ProductionPoolView(pool()),
                Clock = SystemSharedHostDiagnosticsClock.Instance,
                ReadAvailableMemory = MachineMemory.TryGetAvailablePhysicalMb,
                ReadRegistry = () => ReadRegistryV2(probe),
                ReadWorkingSetMb = ReadProcessWorkingSetMb,
                Configured = config.EffectiveSharedHost,
                Environment = System.Environment.GetEnvironmentVariable,
                GatewayPid = System.Environment.ProcessId
            });
        }

        private static IReadOnlyList<SharedHostDiagnosticsHost> ProductionPoolView(WorkerPool? pool)
        {
            if (pool == null) return Array.Empty<SharedHostDiagnosticsHost>();
            var aliases = pool.AliasesByKey();
            var hosts = new List<SharedHostDiagnosticsHost>();
            foreach (var (key, worker) in pool.SharedWorkers())
            {
                hosts.Add(new SharedHostDiagnosticsHost
                {
                    IdentityKey = key,
                    Aliases = aliases.TryGetValue(key, out var list) ? list : Array.Empty<string>(),
                    HostPid = worker.HostPid,
                    WorkerPid = worker.Pid,
                    RequestStatusAsync = worker.RequestSharedStatusAsync
                });
            }
            return hosts;
        }

        // A disappearing record is harmless; unreadable or uncertain records cannot support an exact count.
        internal static IReadOnlyList<SharedHostDiagnosticsRecord> ReadRegistryV2(IProcessProbe probe, string? root = null)
        {
            var records = new List<SharedHostDiagnosticsRecord>();
            root ??= SharedWorkerRegistry.GetV2Root();
            string[] files;
            try
            {
                files = Directory.GetFiles(root, "*.json");
            }
            catch (DirectoryNotFoundException)
            {
                return records;
            }
            foreach (string file in files)
            {
                // "*.json" can also match longer extensions on Windows; temp files end in ".tmp-<pid>-<guid>".
                if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase)) continue;
                var read = RecordReadResult.ReadFile(file);
                if (read.Status == RecordReadStatus.Unreadable || read.Status == RecordReadStatus.Unparseable)
                    throw new IOException("Shared-host registry record is unreadable or invalid.");
                var classification = SharedHostRecordClassifier.Classify(read, probe);
                if (classification.Liveness == RecordLiveness.LivenessUnverified)
                    throw new IOException("Shared-host registry liveness could not be verified.");
                if (classification.Record == null) continue;
                records.Add(new SharedHostDiagnosticsRecord { Record = classification.Record, Liveness = classification.Liveness });
            }
            return records;
        }

        private static long? ReadProcessWorkingSetMb(int pid)
        {
            if (pid <= 0) return null;
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.WorkingSet64 / (1024L * 1024L);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
                return null;
            }
        }
    }
}
