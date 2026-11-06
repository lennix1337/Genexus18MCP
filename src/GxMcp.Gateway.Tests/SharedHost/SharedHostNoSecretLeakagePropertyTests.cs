using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Common.SharedHost;
using GxMcp.Gateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 16: No secret leakage
    //
    // For any request payloads, source texts, Lease tokens, and credential-like strings seeded into queued requests
    // and Gateway state, none of those values appears as a substring of any Busy_Envelope, concurrency error hint,
    // status_ack frame, or Diagnostics_Surface field.
    //
    // **Validates: Requirements 3.6, 11.5, 12.5**
    //
    // Each case generates one unique random marker per secret category (Lease token, source text, request arguments,
    // credential-like strings, cancel token). Credential-like strings are assembled at runtime from fragments around
    // their marker. The markers are seeded into real SharedHostScheduler requests (Payload and CancelToken) and into
    // the refused request each Gateway-side error answers. The check is an ordinal substring search over the compact
    // JSON text and over every property name and string value of each output.
    //
    // Surfaces covered here:
    //   - SharedHostScheduler.Snapshot diagnostics views (RunningStatus, QueueStatus), read through reflection over
    //     every member, so a new member that copies request data is caught. These views are the only scheduler input
    //     of the Worker-side Busy_Envelope and status_ack builders.
    //   - The Busy_Envelope and the status_ack frame. SharedWorkerHostProtocol (GxMcp.Worker, net48) is not compiled
    //     into the Gateway tests, so both are rebuilt here from the same inputs the Worker builders read: the snapshot
    //     views above and ConcurrencyErrorCatalog.Build. A change inside the Worker builders themselves is covered by
    //     the Worker tests (SharedWorkerHostBusyEnvelopePropertyTests), not by this file.
    //   - ConcurrencyErrorCatalog.Build hints and messages for every row, reason and dispatch state, with the markers
    //     passed in Details; and the whole error when the raiser passes only its structured fields.
    //   - McpRouter.TrimErrorEnvelope (terse and verbose) over every Busy_Envelope and catalog error.
    //   - SharedWorkerLaunchException (Message and Error) for each Gateway-raised concurrency code.
    //   - SharedWorkerRecoveryFailure.Build and SharedWorkerRecoveryFailure.Exception.
    //   - Program.BuildKbPoolFullPayload in shared mode (catalog error with per-KB details) and Isolated_Mode.
    //   - ConcurrencyErrorNormalizer.Normalize (task 6.7) over every Busy_Envelope and Worker conflict error, with the
    //     secret-bearing Worker request as its request argument, and Program.BuildSharedWorkerLaunchErrorResponse
    //     (the stdio launch refusal, issue #914).
    //     API as pinned in ConcurrencyErrorNormalizerTests.cs (task 6.4).
    //   - The SharedHostDiagnostics "sharedHost" block (task 6.8), the Diagnostics_Surface content of genexus_whoami,
    //     genexus_doctor and genexus_lifecycle action=status. API as pinned in SharedHostDiagnosticsTests.cs (task
    //     6.5). Its inputs are the status_ack frames above plus extra secret-bearing keys a host must never send
    //     (top level, running, queue entries), and an environment that holds credential-like variables. The
    //     "effectiveSettings" object is copied into the block by contract, so no secret is seeded there.
    //
    // ConcurrencyErrorNormalizer and SharedHostDiagnostics do not exist until tasks 6.7 and 6.8 land, so this file
    // does not compile before then.
    public class SharedHostNoSecretLeakagePropertyTests
    {
        private const int Iterations = 150;
        private const string PropertyName = "Feature: concurrent-multi-session-workers, Property 16: No secret leakage";

        private const long MinBusyWaitMs = 1000;

        private static readonly string[] Operations = { "Search/SearchSource", "Write/Patch", "Build/BuildOne", "Read/ReadPart", "Inspect/Describe" };
        private static readonly string[] Tools = { "genexus_edit", "genexus_read", "genexus_query", "genexus_build" };
        private static readonly string[] SourcePrefixes = { "Event Start\n  &Var = ", "For each Customer\n  msg(", "Sub 'Load'\n  &Total = " };

        // ---- Secrets -----------------------------------------------------------------------------------------

        private sealed class Secrets
        {
            internal Secrets(PbtCase tc)
            {
                LeaseMarker = NewMarker(tc, "LS");
                SourceMarker = NewMarker(tc, "SC");
                ArgumentMarker = NewMarker(tc, "AR");
                CredentialMarker = NewMarker(tc, "CR");
                CancelMarker = NewMarker(tc, "CT");

                SourceText = tc.Pick(SourcePrefixes) + SourceMarker + "\nEndEvent";
                // Credential-shaped strings are built from fragments so no secret-like literal sits in the source.
                CredentialStrings = new[]
                {
                    string.Concat("pass", "word=", CredentialMarker),
                    string.Concat("Data Source=db01;User ", "ID=svc;Pass", "word=", CredentialMarker, ";"),
                    string.Concat("Bear", "er ", CredentialMarker),
                    string.Concat("api", "Key:", CredentialMarker),
                };
            }

            internal string LeaseMarker { get; }
            internal string SourceMarker { get; }
            internal string ArgumentMarker { get; }
            internal string CredentialMarker { get; }
            internal string CancelMarker { get; }
            internal string SourceText { get; }
            internal string[] CredentialStrings { get; }

            internal IEnumerable<KeyValuePair<string, string>> Markers => new[]
            {
                new KeyValuePair<string, string>("Lease token", LeaseMarker),
                new KeyValuePair<string, string>("source text", SourceMarker),
                new KeyValuePair<string, string>("request argument", ArgumentMarker),
                new KeyValuePair<string, string>("credential-like string", CredentialMarker),
                new KeyValuePair<string, string>("cancel token", CancelMarker),
            };

            // A tools/call request that carries every secret category in its arguments.
            internal JObject Request(PbtCase tc, int id)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = tc.Pick(Tools),
                        ["arguments"] = new JObject
                        {
                            ["name"] = "Obj" + ArgumentMarker,
                            ["part"] = "Source",
                            ["source"] = SourceText,
                            ["kbLease"] = LeaseMarker,
                            ["connection"] = tc.Pick(CredentialStrings),
                            ["authorization"] = tc.Pick(CredentialStrings),
                            ["filter"] = "name:" + ArgumentMarker,
                        }
                    }
                };
            }

            private static string NewMarker(PbtCase tc, string category)
            {
                var sb = new StringBuilder("LEAKMARK").Append(category);
                for (var i = 0; i < 6; i++) sb.Append(tc.Int(0, 0xFFFF).ToString("x4", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ---- Assertions --------------------------------------------------------------------------------------

        private static void AssertNoLeak(Secrets secrets, string surface, JToken? output)
        {
            if (output == null) return;
            var compact = output.ToString(Formatting.None);
            var texts = new List<string> { compact };
            CollectTexts(output, texts);
            foreach (var marker in secrets.Markers)
            {
                foreach (var text in texts)
                {
                    if (text.IndexOf(marker.Value, StringComparison.Ordinal) >= 0)
                    {
                        Assert.Fail("The " + marker.Key + " seeded into the request leaked into " + surface + ": " + compact);
                    }
                }
            }
        }

        private static void AssertNoLeak(Secrets secrets, string surface, string? text)
        {
            if (text == null) return;
            foreach (var marker in secrets.Markers)
            {
                if (text.IndexOf(marker.Value, StringComparison.Ordinal) >= 0)
                {
                    Assert.Fail("The " + marker.Key + " seeded into the request leaked into " + surface + ": " + text);
                }
            }
        }

        private static void CollectTexts(JToken token, List<string> sink)
        {
            switch (token)
            {
                case JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        sink.Add(property.Name);
                        CollectTexts(property.Value, sink);
                    }
                    break;
                case JArray array:
                    foreach (var item in array) CollectTexts(item, sink);
                    break;
                case JValue value when value.Value != null:
                    sink.Add(Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                    break;
            }
        }

        // Every member of a diagnostics view, read through reflection so a member added later is checked too.
        private static JObject ViewMembers(object view)
        {
            var result = new JObject();
            foreach (var property in view.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var value = property.GetValue(view);
                result[property.Name] = value == null ? JValue.CreateNull() : new JValue(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            return result;
        }

        // ---- Mirrors of the Worker-side builders (inputs: snapshot views, limits, catalog) -------------------

        // Same inputs and shape as SharedWorkerHostProtocol.BuildBusyResult; returns the tool-result object.
        private static JObject BusyResult(string reason, SchedulerSnapshot snapshot, SchedulerLimits limits,
            string kbIdentityKey, string kbPath, int? queuePosition, long retryAfterMs)
        {
            var details = new JObject { ["kbIdentityKey"] = kbIdentityKey, ["kbPath"] = kbPath };
            var running = snapshot.RunningStatus;
            if (running != null)
            {
                details["runningOperation"] = running.Operation;
                details["runningElapsedMs"] = Math.Max(0L, running.ElapsedMs);
            }
            if (reason == "queue-wait-expired") details["queuePosition"] = queuePosition ?? 1;
            else if (reason == SubmitResult.QueueFull) details["queueDepth"] = limits.QueueDepth;
            else details["sessionQueueCap"] = limits.SessionQueueCap;

            long requested = Math.Max(MinBusyWaitMs, retryAfterMs);
            var error = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.WorkerBusy, reason, new ConcurrencyErrorFields
            {
                QueueWaitMs = limits.QueueWaitMs,
                RetryAfterMs = (int)Math.Min(requested, int.MaxValue),
                Details = details
            });
            return new JObject { ["status"] = "error", ["code"] = ConcurrencyErrorCatalog.WorkerBusy, ["error"] = error };
        }

        // Same inputs and shape as SharedWorkerHostProtocol.BuildStatusAck with an empty host context.
        private static JObject StatusAck(string identityKey, string requestId, SchedulerSnapshot snapshot, int workerPid, int attachments)
        {
            var frame = new JObject
            {
                ["type"] = "status_ack",
                ["gxmcp"] = "status_ack",
                ["protocolVersion"] = 2,
                ["version"] = 2,
                ["identityKey"] = identityKey,
                ["key"] = identityKey,
                ["requestId"] = requestId,
                ["childState"] = "ready",
                ["workerPid"] = workerPid,
                ["workerWorkingSetMb"] = 150,
            };
            var running = snapshot.IsIdle ? null : snapshot.RunningStatus;
            frame["idle"] = running == null;
            frame["running"] = running == null
                ? (JToken)JValue.CreateNull()
                : new JObject { ["operation"] = running.Operation, ["ownerAttachId"] = running.OwnerAttachId, ["elapsedMs"] = Math.Max(0L, running.ElapsedMs) };
            var queue = new JArray();
            foreach (var entry in snapshot.QueueStatus)
            {
                var item = new JObject
                {
                    ["position"] = entry.Position,
                    ["ownerAttachId"] = entry.OwnerAttachId,
                    ["waitMs"] = Math.Max(0L, entry.WaitMs),
                    ["waitReason"] = entry.WaitReason == WaitReason.WorkerStarting ? "worker-starting"
                        : entry.WaitReason == WaitReason.WorkerLimit ? "worker-limit" : "sta-busy"
                };
                if (entry.WaitReason == WaitReason.StaBusy && !string.IsNullOrEmpty(entry.Operation)) item["operation"] = entry.Operation;
                queue.Add(item);
            }
            frame["queue"] = queue;
            frame["queueDepth"] = snapshot.QueueDepth;
            frame["attachments"] = attachments;
            frame["effectiveSettings"] = new JObject();
            return frame;
        }

        // ---- Property ----------------------------------------------------------------------------------------

        [Fact]
        [Trait("Feature", "concurrent-multi-session-workers")]
        [Trait("Property", "16")]
        public void SeededSecretsNeverReachABusyEnvelopeHintStatusAckOrErrorSurface()
        {
            SeededInterleavingRunner.Run(PropertyName, Iterations, tc =>
            {
                var secrets = new Secrets(tc);
                var busyResults = new List<JObject>();
                var acks = new List<JObject>();
                CheckSchedulerSurfaces(tc, secrets, busyResults, acks);
                CheckCatalogHints(tc, secrets);
                CheckGatewayErrors(tc, secrets);
                CheckNormalizer(tc, secrets, busyResults);
                CheckDiagnostics(tc, secrets, acks);
            });
        }

        private static void CheckSchedulerSurfaces(PbtCase tc, Secrets secrets, List<JObject> busyResults, List<JObject> acks)
        {
            int queueDepth = tc.SmallInt(1, 8);
            var limits = new SchedulerLimits(queueDepth, tc.Int(1, queueDepth), tc.Int(1000, 20000));
            long now = 0;
            var scheduler = new SharedHostScheduler(limits, () => now);
            string identityKey = "kb-identity-" + tc.Int(1, 9).ToString(CultureInfo.InvariantCulture);
            string kbPath = @"C:\KBs\Kb" + tc.Int(1, 9).ToString(CultureInfo.InvariantCulture);
            int sessions = tc.SmallInt(1, 4);
            int steps = tc.SmallInt(1, 24);
            int nextId = 1;
            tc.Log("limits depth=" + limits.QueueDepth + " cap=" + limits.SessionQueueCap + " wait=" + limits.QueueWaitMs + " sessions=" + sessions);

            for (int step = 0; step < steps; step++)
            {
                int action = tc.Int(0, 9);
                if (action <= 5)
                {
                    int id = nextId++;
                    var request = new QueuedRequest
                    {
                        ChildRequestId = "child-" + id.ToString(CultureInfo.InvariantCulture),
                        SessionId = "attach-" + tc.Int(1, sessions).ToString(CultureInfo.InvariantCulture),
                        ClientRequestIdJson = id.ToString(CultureInfo.InvariantCulture),
                        Operation = tc.Pick(Operations),
                        CancelToken = tc.Bool() ? secrets.CancelMarker : null,
                        EnqueuedAtMs = now,
                        Sequence = id,
                        Payload = secrets.Request(tc, id).ToString(Formatting.None),
                    };
                    var submit = scheduler.Submit(request);
                    tc.Log("submit " + request.ChildRequestId + " " + request.SessionId + " " + request.Operation + " -> "
                        + (submit.Started ? "started" : submit.Queued ? "queued" : submit.RejectReason));
                    if (submit.RejectReason != null)
                    {
                        var snapshot = scheduler.Snapshot();
                        var busy = BusyResult(submit.RejectReason, snapshot, limits, identityKey, kbPath, null,
                            tc.Int(1000, limits.QueueWaitMs + 1000));
                        CheckBusy(secrets, busy);
                        busyResults.Add(busy);
                    }
                }
                else if (action <= 7)
                {
                    now += tc.Int(1, limits.QueueWaitMs + 500);
                    tc.Log("advance clock to " + now);
                    foreach (var expired in scheduler.ExpireDue())
                    {
                        tc.Log("expired " + expired.Request.ChildRequestId + " position " + expired.Position);
                        var busy = BusyResult("queue-wait-expired", scheduler.Snapshot(), limits, identityKey, kbPath,
                            expired.Position, tc.Int(1000, limits.QueueWaitMs + 1000));
                        CheckBusy(secrets, busy);
                        busyResults.Add(busy);
                    }
                }
                else
                {
                    var running = scheduler.Snapshot().Running;
                    if (running != null)
                    {
                        tc.Log("complete " + running.ChildRequestId);
                        scheduler.Complete(running.ChildRequestId);
                    }
                }

                var current = scheduler.Snapshot();
                // The markers really are inside scheduler state, so a clean view is not a vacuous pass.
                if (current.Running != null) Assert.Contains(secrets.LeaseMarker, current.Running.Payload, StringComparison.Ordinal);
                if (current.RunningStatus != null) AssertNoLeak(secrets, "the running-operation diagnostics view", ViewMembers(current.RunningStatus));
                foreach (var entry in current.QueueStatus) AssertNoLeak(secrets, "a queue-entry diagnostics view", ViewMembers(entry));
                var ack = StatusAck(identityKey, "status-" + step.ToString(CultureInfo.InvariantCulture), current, tc.Int(100, 60000), sessions);
                AssertNoLeak(secrets, "a status_ack frame", ack);
                acks.Add(ack);
            }
        }

        private static void CheckBusy(Secrets secrets, JObject busy)
        {
            AssertNoLeak(secrets, "a Busy_Envelope", busy);
            AssertNoLeak(secrets, "the Busy_Envelope hint", (string?)busy["error"]?["hint"]);
            AssertNoLeak(secrets, "the terse Busy_Envelope", McpRouter.TrimErrorEnvelope(busy, verbose: false));
            AssertNoLeak(secrets, "the verbose Busy_Envelope", McpRouter.TrimErrorEnvelope(busy, verbose: true));
        }

        private static void CheckCatalogHints(PbtCase tc, Secrets secrets)
        {
            var leakyDetails = new JObject
            {
                ["source"] = secrets.SourceText,
                ["kbLease"] = secrets.LeaseMarker,
                ["arguments"] = secrets.Request(tc, 0)["params"]!["arguments"]!.DeepClone(),
                ["connection"] = tc.Pick(secrets.CredentialStrings),
            };

            foreach (var row in ConcurrencyErrorCatalog.Rows)
            {
                var reasons = row.Reasons.Count == 0 ? new string?[] { null } : row.Reasons.Cast<string?>().ToArray();
                var dispatchStates = row.DispatchState != null
                    ? new string?[] { null }
                    : new string?[] { ConcurrencyDispatchState.NotDelivered, ConcurrencyDispatchState.DeliveredUnanswered };
                foreach (var reason in reasons)
                {
                    foreach (var dispatchState in dispatchStates)
                    {
                        var operationClass = tc.Bool() ? OperationClass.Read : OperationClass.Write;
                        int? holderPid = tc.Bool() ? tc.Int(100, 60000) : (int?)null;
                        int queueWaitMs = tc.Int(1000, 600000);
                        string surface = row.Code + (reason != null ? "/" + reason : string.Empty) + (dispatchState != null ? "/" + dispatchState : string.Empty);

                        // Raiser passes request data in Details by mistake: the hint and message must still not carry it.
                        var withDetails = ConcurrencyErrorCatalog.Build(row.Code, reason, new ConcurrencyErrorFields
                        {
                            QueueWaitMs = queueWaitMs,
                            LockRemainingMs = tc.Int(0, 60000),
                            DispatchState = dispatchState,
                            OperationClass = operationClass,
                            HolderPid = holderPid,
                            Details = (JObject)leakyDetails.DeepClone(),
                        });
                        AssertNoLeak(secrets, "the " + surface + " hint", (string?)withDetails["hint"]);
                        AssertNoLeak(secrets, "the " + surface + " message", (string?)withDetails["message"]);

                        // Raiser passes only its structured fields: the whole error and its terse projection stay clean.
                        var clean = ConcurrencyErrorCatalog.Build(row.Code, reason, new ConcurrencyErrorFields
                        {
                            QueueWaitMs = queueWaitMs,
                            LockRemainingMs = tc.Int(0, 60000),
                            DispatchState = dispatchState,
                            OperationClass = operationClass,
                            HolderPid = holderPid,
                            Details = new JObject { ["kbIdentityKey"] = "kb-identity-1", ["part"] = "Source" },
                        });
                        AssertNoLeak(secrets, "the " + surface + " error", clean);
                        var envelope = new JObject { ["status"] = "error", ["code"] = row.Code, ["error"] = clean };
                        AssertNoLeak(secrets, "the terse " + surface + " envelope", McpRouter.TrimErrorEnvelope(envelope, verbose: false));
                    }
                }
            }
        }

        private static void CheckGatewayErrors(PbtCase tc, Secrets secrets)
        {
            // The refused request carries every secret; the Gateway errors below answer it from Gateway state only.
            var refused = secrets.Request(tc, tc.Int(1, 1000));
            Assert.Contains(secrets.LeaseMarker, refused.ToString(Formatting.None), StringComparison.Ordinal);

            int holderPid = tc.Int(100, 60000);
            var launchErrors = new[]
            {
                (ConcurrencyErrorCatalog.KbWorkerOwnedElsewhere, (string?)tc.Pick(new[] { "owner-does-not-accept-attachments", "liveness-unverified" }), (JObject?)new JObject { ["holderGatewayPid"] = holderPid }),
                (ConcurrencyErrorCatalog.SharedWorkerIdentityMismatch, (string?)null, (JObject?)new JObject { ["mismatchedComponents"] = new JArray("driver", "targetMajor") }),
                (ConcurrencyErrorCatalog.SharedWorkerAttachFailed, (string?)tc.Pick(new[] { "readiness-timeout", "pipe-refused" }), (JObject?)new JObject { ["attachReadyTimeoutSeconds"] = tc.Int(10, 600) }),
                (ConcurrencyErrorCatalog.WorkerStartupFailed, (string?)tc.Pick(new[] { "replacement-failed", "cold-start-wait-expired" }), (JObject?)null),
                (ConcurrencyErrorCatalog.KbPoolFull, (string?)tc.Pick(new[] { "worker-limit", "low-memory" }), (JObject?)new JObject { ["limit"] = "Server.SharedHost.MaxWorkers", ["value"] = tc.Int(1, 16) }),
            };
            foreach (var (code, reason, details) in launchErrors)
            {
                var error = ConcurrencyErrorCatalog.Build(code, reason, new ConcurrencyErrorFields
                {
                    QueueWaitMs = tc.Int(1000, 600000),
                    HolderPid = holderPid,
                    Details = details,
                });
                var ex = new SharedWorkerLaunchException(code, reason, error);
                AssertNoLeak(secrets, "the " + code + " launch exception message", ex.Message);
                AssertNoLeak(secrets, "the " + code + " launch exception error", ex.Error);
            }

            string recordKey = "kb-" + tc.Int(0, int.MaxValue).ToString("x8", CultureInfo.InvariantCulture);
            string registry = tc.Pick(new[] { "shared-workers/v2", "worker-ownership" });
            string failureReason = tc.Pick(new[] { SharedWorkerRecoveryFailure.RemoveFailed, SharedWorkerRecoveryFailure.KillFailed, SharedWorkerRecoveryFailure.StillRunning });
            int? blockingPid = tc.Bool() ? tc.Int(100, 60000) : (int?)null;
            string? detail = tc.Bool() ? "The process cannot access the file because it is being used by another process." : null;
            AssertNoLeak(secrets, "SHARED_WORKER_RECOVERY_FAILED", SharedWorkerRecoveryFailure.Build(recordKey, registry, failureReason, blockingPid, detail));
            var recovery = SharedWorkerRecoveryFailure.Exception(recordKey, registry, failureReason, blockingPid, detail);
            AssertNoLeak(secrets, "the SHARED_WORKER_RECOVERY_FAILED exception message", recovery.Message);
            AssertNoLeak(secrets, "the SHARED_WORKER_RECOVERY_FAILED exception error", recovery.Error);

            int openCount = tc.SmallInt(1, 4);
            var handles = new List<KbHandle>();
            var openKbs = new JArray();
            for (int i = 1; i <= openCount; i++)
            {
                string alias = "kb" + i.ToString(CultureInfo.InvariantCulture);
                handles.Add(new KbHandle(alias, @"C:\KBs\Kb" + i.ToString(CultureInfo.InvariantCulture)));
                openKbs.Add(new JObject
                {
                    ["key"] = "kb-identity-" + i.ToString(CultureInfo.InvariantCulture),
                    ["aliases"] = new JArray(alias),
                    ["sessions"] = tc.Int(0, 4),
                    ["lastActivityUtc"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(tc.Int(0, 86400)),
                });
            }
            var poolError = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.KbPoolFull, "max-open-kbs", new ConcurrencyErrorFields
            {
                Details = new JObject { ["limit"] = "Server.MaxOpenKbs", ["value"] = openCount, ["openKbs"] = openKbs }
            });
            AssertNoLeak(secrets, "the shared-mode KB_POOL_FULL payload",
                Program.BuildKbPoolFullPayload(new WorkerPoolFullException(handles, openCount, "max-open-kbs", poolError)));
            AssertNoLeak(secrets, "the Isolated_Mode KB_POOL_FULL payload",
                Program.BuildKbPoolFullPayload(new WorkerPoolFullException(handles)));
        }

        // ---- ConcurrencyErrorNormalizer (task 6.7) -----------------------------------------------------------

        private static readonly string[] Parts = { "Source", "Events", "Rules", "Conditions" };

        private static Configuration SharedConfig(int queueWaitMs) => new Configuration
        {
            Server = new ServerConfig { WorkerSharingMode = "shared-host" },
            EffectiveSharedHost = SharedHostSettingsResolver.Resolve(
                new JObject { [SharedHostSettingsResolver.QueueWaitMsKey] = queueWaitMs }, strictDocument: false, string.Empty, _ => null),
        };

        // The Worker RPC request (Program.BuildWorkerRpcRequest shape) with every secret category in its params. The
        // target and part are ordinary names that the Worker's own error may legitimately repeat.
        private static JObject WorkerRequest(PbtCase tc, Secrets secrets, string target, string part)
        {
            var arguments = (JObject)secrets.Request(tc, 0)["params"]!["arguments"]!;
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "w" + tc.Int(1, 999).ToString(CultureInfo.InvariantCulture),
                ["method"] = tc.Pick(new[] { "Write", "Read", "Search" }),
                ["action"] = part,
                ["target"] = target,
                ["params"] = new JObject
                {
                    ["module"] = "Write",
                    ["action"] = part,
                    ["part"] = part,
                    ["target"] = target,
                    ["code"] = secrets.SourceText,
                    ["kbLease"] = secrets.LeaseMarker,
                    ["connection"] = tc.Pick(secrets.CredentialStrings),
                    ["arguments"] = arguments,
                },
            };
        }

        private static void CheckNormalizer(PbtCase tc, Secrets secrets, List<JObject> busyResults)
        {
            var config = SharedConfig(tc.Int(1000, 600000));
            DateTime now = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc).AddSeconds(tc.Int(0, 86400));
            Func<DateTime> clock = () => now;
            string target = "Obj" + tc.Int(1, 99).ToString(CultureInfo.InvariantCulture);
            string part = tc.Pick(Parts);
            var request = WorkerRequest(tc, secrets, target, part);
            Assert.Contains(secrets.SourceMarker, request.ToString(Formatting.None), StringComparison.Ordinal);

            var responses = new List<(string Surface, JObject Response)>();
            foreach (var busy in busyResults)
            {
                responses.Add(("the normalized Busy_Envelope", busy));
                responses.Add(("the normalized JSON-RPC Busy_Envelope", new JObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["result"] = busy.DeepClone() }));
            }

            // Worker conflict errors in the flat WritePipeline shape and the canonical McpResponse.Err shape.
            responses.Add(("the normalized TargetLockedByOtherAgent", new JObject
            {
                ["status"] = "Error",
                ["code"] = ConcurrencyErrorCatalog.TargetLockedByOtherAgent,
                ["message"] = "Object '" + target + "' part '" + part + "' is locked by agent 'gateway:owner-b'.",
                ["lockHolder"] = "gateway:owner-b",
                ["lockExpiresAt"] = now.AddMilliseconds(tc.Int(-5000, 60000)).ToString("o", CultureInfo.InvariantCulture),
                ["target"] = target,
            }));
            foreach (var code in new[] { ConcurrencyErrorCatalog.StaleObject, ConcurrencyErrorCatalog.LockCheckFailed, ConcurrencyErrorCatalog.VersionCheckUnavailable })
            {
                responses.Add(("the normalized " + code, new JObject
                {
                    ["status"] = "error",
                    ["target"] = target,
                    ["error"] = new JObject { ["code"] = code, ["message"] = "The write to " + target + " was rejected before any save." },
                }));
            }

            // SHARED_WORKER_RESTARTED as the broker sends it: a JSON-RPC error whose data is the catalog row.
            string dispatchState = tc.Bool() ? ConcurrencyDispatchState.NotDelivered : ConcurrencyDispatchState.DeliveredUnanswered;
            var restarted = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.SharedWorkerRestarted, null, new ConcurrencyErrorFields
            {
                QueueWaitMs = 30000,
                DispatchState = dispatchState,
                OperationClass = OperationClass.Write,
                Details = new JObject { ["generation"] = tc.Int(1, 9) },
            });
            responses.Add(("the normalized SHARED_WORKER_RESTARTED", new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 9,
                ["error"] = new JObject { ["code"] = -32002, ["message"] = (string?)restarted["message"], ["data"] = restarted },
            }));

            foreach (var (surface, response) in responses)
            {
                AssertNoLeak(secrets, surface + " (raw input)", response);
                var normalized = ConcurrencyErrorNormalizer.Normalize(response, request, config, clock);
                AssertNoLeak(secrets, surface, normalized);
                var toolResult = normalized["result"] as JObject ?? normalized;
                AssertNoLeak(secrets, "the terse form of " + surface, McpRouter.TrimErrorEnvelope(toolResult, verbose: false));
            }

            var launchError = ConcurrencyErrorCatalog.Build(ConcurrencyErrorCatalog.SharedWorkerAttachFailed, "readiness-timeout",
                new ConcurrencyErrorFields { QueueWaitMs = 30000 });
            var launch = new SharedWorkerLaunchException(ConcurrencyErrorCatalog.SharedWorkerAttachFailed, "readiness-timeout", launchError);
            AssertNoLeak(secrets, "the stdio JSON-RPC launch refusal",
                Program.BuildSharedWorkerLaunchErrorResponse(new JValue(7), launch));
        }

        // ---- SharedHostDiagnostics (task 6.8) ----------------------------------------------------------------

        private sealed class ImmediateClock : ISharedHostDiagnosticsClock
        {
            private readonly DateTime _now;

            internal ImmediateClock(DateTime now) => _now = now;

            public DateTime UtcNow => _now;

            public Task Delay(int milliseconds, CancellationToken cancellationToken) =>
                Task.Delay(Math.Max(0, milliseconds), cancellationToken);
        }

        private static void CheckDiagnostics(PbtCase tc, Secrets secrets, List<JObject> acks)
        {
            DateTime now = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc).AddSeconds(tc.Int(0, 86400));
            int hostCount = tc.SmallInt(1, 3);
            var hosts = new List<SharedHostDiagnosticsHost>();
            var records = new List<SharedHostDiagnosticsRecord>();
            for (int i = 1; i <= hostCount; i++)
            {
                string key = "kb-key-" + i.ToString(CultureInfo.InvariantCulture);
                int hostPid = 4000 + i;
                int workerPid = 5000 + i;
                var ack = (JObject)tc.Pick(acks).DeepClone();
                ack["identityKey"] = key;
                ack["key"] = key;
                ack["workerPid"] = workerPid;
                // Keys a host must never send; the diagnostics block projects the documented fields only.
                ack["lastRequest"] = secrets.Request(tc, i);
                ack["kbLease"] = secrets.LeaseMarker;
                if (ack["running"] is JObject running) running["arguments"] = secrets.Request(tc, i)["params"]!["arguments"]!.DeepClone();
                foreach (var entry in ((JArray)ack["queue"]!).OfType<JObject>()) entry["payload"] = secrets.SourceText;
                ack["effectiveSettings"] = new JObject
                {
                    ["queueWaitMs"] = 30000, ["queueDepth"] = 8, ["sessionQueueCap"] = 4, ["maxWorkers"] = 4,
                    ["memoryFloorMb"] = 1024, ["coldStartConcurrency"] = 1, ["attachReadyTimeoutSeconds"] = 120,
                };
                bool reachable = tc.Chance(0.8);
                hosts.Add(new SharedHostDiagnosticsHost
                {
                    IdentityKey = key,
                    Aliases = new[] { "kb" + i.ToString(CultureInfo.InvariantCulture) },
                    HostPid = hostPid,
                    WorkerPid = workerPid,
                    RequestStatusAsync = (timeoutMs, ct) => reachable
                        ? Task.FromResult<JObject?>((JObject)ack.DeepClone())
                        : Task.FromResult<JObject?>(null),
                });
                records.Add(new SharedHostDiagnosticsRecord
                {
                    Record = new SharedHostRecord
                    {
                        KbKey = key,
                        HostPid = hostPid,
                        LaunchedByGatewayPid = 3777,
                        WorkerPid = workerPid,
                        State = SharedHostRecordState.Ready,
                        ColdStartConcurrency = 1,
                        Attachments = 1,
                        UpdatedUtc = now,
                    },
                    Liveness = RecordLiveness.Live,
                });
                tc.Log("diagnostics host " + key + " reachable=" + reachable);
            }

            // Credential-like environment values next to the variables the block does read.
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["GXMCP_DB_CONNECTION"] = tc.Pick(secrets.CredentialStrings),
                ["GXMCP_KB_LEASE"] = secrets.LeaseMarker,
                ["GXMCP_LAST_SOURCE"] = secrets.SourceText,
            };
            if (tc.Bool()) environment["GXMCP_BUSY_REJECT_MS"] = "3000";

            var settings = SharedHostSettingsResolver.Resolve(null, strictDocument: false, "stdio-isolated", _ => null);
            var diagnostics = new SharedHostDiagnostics(new SharedHostDiagnosticsInputs
            {
                PoolView = () => hosts,
                Clock = new ImmediateClock(now),
                ReadAvailableMemory = (out long mb, out string? error) =>
                {
                    mb = 8192;
                    error = null;
                    return true;
                },
                ReadRegistry = () => records,
                ReadWorkingSetMb = _ => 256,
                Configured = settings,
                Environment = name => environment.TryGetValue(name, out var value) ? value : null,
                GatewayPid = 3900,
            });

            var block = Task.Run(() => diagnostics.BuildAsync()).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            AssertNoLeak(secrets, "the sharedHost Diagnostics_Surface block", block);
        }
    }
}
