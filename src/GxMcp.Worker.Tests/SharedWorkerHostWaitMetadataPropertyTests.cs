// Feature: concurrent-multi-session-workers, Property 18: Response wait metadata
// Validates: Requirements 11.4
//
// Broker-side property test for the response wait metadata of the shared-host Worker_Host
// (RFC #854, task 3.8). It is written before the implementation (tasks 3.10 and 3.11) and
// needs no GeneXus SDK, license, or KB.
//
// Seams: exactly the contract pinned in the header of SharedWorkerHostSchedulerWiringTests.cs
// (task 3.5): ISharedHostClock, ISharedHostTimer, ISharedHostChildLauncher, ISharedHostChild,
// ISharedHostRegistryWriter, ISharedHostAttachment, and the SharedWorkerHostRuntime test
// constructor with runBackgroundThreads=false plus Start, Attach, Receive, OnChildLine,
// OnChildExited and PumpChildInbox. This file adds no member to that contract. The fakes in
// the 3.5 file are private, so this file has its own private nested fakes with distinct names.
//
// Broker-side shape pinned here (design.md, "SharedWorkerHost.cs": "The restored response
// carries a top-level _gxmcpShared object { queueWaitMs, workerPid } for requests that were
// queued"; the Gateway side, SharedWorkerConnectionV2Tests, moves it to
// result._meta.sharedHost and removes it):
//
//   { "jsonrpc": "2.0", "id": <client id>, "result": <child result, unchanged>,
//     "_gxmcpShared": { "queueWaitMs": <integer ms>, "workerPid": <integer> } }
//
//   - "_gxmcpShared" is a top-level member of the routed JSON-RPC response, a sibling of
//     "result". It is a JSON object with exactly the two members queueWaitMs and workerPid,
//     both JSON integers. No other member is added to it, and the broker does not write into
//     result or result._meta (the Gateway does that).
//   - queueWaitMs = (monotonic clock when the broker released the request from the
//     Worker_Scheduler to the child inbox) - (monotonic clock when Receive submitted it to the
//     Worker_Scheduler), both read from ISharedHostClock.NowMs. Release is the broker's
//     observable stand-in for "start on the Worker's STA thread" (Requirement 11.4), since the
//     dispatch window is one and the child starts the request as soon as it reads it.
//   - workerPid = ISharedHostChild.Pid of the child the request was written to.
//   - Apart from the added "_gxmcpShared" member, the routed response equals the child's
//     response with only "id" restored to the client id (TryRestoreResponse today).
//
// Rules pinned here:
//   - Every SDK_Operation (any request that Receive hands to SharedHostScheduler.Submit)
//     enters the Request_Queue, so its successful response always carries "_gxmcpShared".
//     A request submitted while the Worker is idle is released at once and carries
//     queueWaitMs = 0. A request that waited carries the exact difference above.
//   - Thread-safe and bypass commands (CommandLaneClassifier.IsThreadSafe and the control
//     commands) never enter the Request_Queue, so their responses carry no "_gxmcpShared"
//     member at all: absent, not 0. Source: design.md Property 18 ("a request that started
//     without queueing carries no queue wait metadata") and the "for requests that were
//     queued" clause above. A 0 on a bypass response would claim a queue wait measurement
//     that never happened.
//   - Not pinned here: child JSON-RPC error responses, broker-generated envelopes (Busy,
//     Cancelled, SHARED_WORKER_RESTARTED) and progress notifications.
//
// Generator: 1 to 3 Attachments; seeded child PIDs and up to two idle child restarts so the
// PID changes during a case; SDK_Operations sent while the Worker is idle (released at once)
// or busy (queued); bypass commands interleaved with them and answered in any order; clock
// advances of 0 to 4000 ms between events. Requests stay within the per-Session cap and the
// queue depth, so no Busy_Envelope is produced. The run is deterministic: a fixed base seed,
// 100 cases, and GXMCP_PBT_SEED replays one reported case.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Common.SharedHost;
using GxMcp.Gateway.Tests.SharedHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Collection("CommandLaneClassifierEnvironment")]
    public sealed class SharedWorkerHostWaitMetadataPropertyTests
    {
        private const string PropertyName = "Feature: concurrent-multi-session-workers, Property 18: Response wait metadata";
        private const int Iterations = 100;
        private const int BaseSeed = 1185418;
        private const int QueueWaitMs = 600000;
        private const int QueueDepth = 8;
        private const int SessionQueueCap = 4;
        private const string SharedKey = "_gxmcpShared";

        // ---- Property ----

        [Fact]
        [Trait("Feature", "concurrent-multi-session-workers")]
        [Trait("Property", "18")]
        public void Property18_ResponseWaitMetadata_HoldsForSeededInterleavings()
        {
            var totals = new Totals();

            SeededInterleavingRunner.Run(
                PropertyName,
                Iterations,
                c => new Simulation(c, totals).Run(),
                SeededInterleavingRunner.ParseSeed(Environment.GetEnvironmentVariable(SeededInterleavingRunner.SeedEnvironmentVariable)),
                BaseSeed);

            if (SeededInterleavingRunner.ParseSeed(Environment.GetEnvironmentVariable(SeededInterleavingRunner.SeedEnvironmentVariable)).HasValue)
                return;

            // Generator sanity: the runs covered every case the property speaks about.
            Assert.True(totals.ImmediateSdk > 0, "no SDK_Operation was released at once");
            Assert.True(totals.WaitedSdk > 0, "no SDK_Operation waited in the queue");
            Assert.True(totals.Bypass > 0, "no bypass response was checked");
            Assert.True(totals.Restarts > 0, "no child restart changed the Worker PID");
        }

        // ---- Examples ----

        [Fact]
        public void An_Sdk_Operation_Released_At_Once_Carries_Zero_Wait_And_The_Worker_Pid()
        {
            using (var h = new WaitHarness(firstPid: 7000))
            {
                var a = h.Attach();
                h.Send(a, Request(1, "object", "Read"));
                var line = h.Pump().Single();
                h.Clock.Advance(2500);

                var payload = new JObject { ["status"] = "ok", ["value"] = 1 };
                h.Respond(line, payload);

                var frame = a.Responses().Single();
                var shared = AssertShared(frame);
                Assert.Equal(0L, (long)shared["queueWaitMs"]);
                Assert.Equal(h.Child.Pid, (int)shared["workerPid"]);
                AssertPayloadUnchanged(frame, 1, payload);
            }
        }

        [Fact]
        public void A_Queued_Sdk_Operation_Carries_Its_Release_Minus_Enqueue_Time()
        {
            using (var h = new WaitHarness(firstPid: 7100))
            {
                var a = h.Attach();
                var b = h.Attach();
                h.Send(a, Request(1, "object", "Read"));
                var first = h.Pump().Single();

                h.Clock.Advance(300);
                h.Send(b, Request(2, "object", "Read"));
                h.Clock.Advance(1700);
                h.Respond(first, new JObject { ["status"] = "ok" });

                var second = h.Pump().Single();
                h.Clock.Advance(900);
                var payload = new JObject { ["status"] = "ok", ["_meta"] = new JObject { ["existing"] = "kept" } };
                h.Respond(second, payload);

                var frame = b.Responses().Single();
                var shared = AssertShared(frame);
                Assert.Equal(1700L, (long)shared["queueWaitMs"]);
                Assert.Equal(h.Child.Pid, (int)shared["workerPid"]);
                AssertPayloadUnchanged(frame, 2, payload);
            }
        }

        [Fact]
        public void A_Bypass_Response_Carries_No_Shared_Metadata_Even_While_The_Sdk_Lane_Is_Busy()
        {
            using (var h = new WaitHarness(firstPid: 7200))
            {
                var a = h.Attach();
                h.Send(a, Request(1, "object", "Read"));
                h.Pump().Single();
                h.Clock.Advance(500);
                h.Send(a, Request(2, "ping", null));
                var ping = h.Pump().Single();
                h.Clock.Advance(800);

                var payload = new JObject { ["status"] = "ok", ["pong"] = true };
                h.Respond(ping, payload);

                var frame = a.Responses().Single();
                Assert.Null(frame[SharedKey]);
                AssertPayloadUnchanged(frame, 2, payload);
            }
        }

        // ---- Simulation ----

        private sealed class Totals
        {
            internal int ImmediateSdk;
            internal int WaitedSdk;
            internal int Bypass;
            internal int Restarts;
        }

        private sealed class Pending
        {
            internal int Id;
            internal bool IsSdk;
            internal string Label;
            internal WaitAttachment Owner;
            internal long EnqueuedAtMs;
            internal long ReleasedAtMs;
            internal int ReleasedToPid;
            internal JObject ChildLine;
        }

        private sealed class Simulation
        {
            private static readonly string[][] BypassCommands =
            {
                new[] { "ping", null },
                new[] { "health", null },
                new[] { "list", "Objects" },
                new[] { "kb", "GetIndexStatus" },
                new[] { "system", "GetSdkBusyStatus" }
            };

            private static readonly string[][] SdkCommands =
            {
                new[] { "object", "Read" },
                new[] { "object", "Create" },
                new[] { "object", "Update" }
            };

            private readonly PbtCase _c;
            private readonly Totals _totals;
            private readonly Dictionary<string, Pending> _byClientId = new Dictionary<string, Pending>();
            private readonly List<Pending> _bypassInFlight = new List<Pending>();
            private readonly List<WaitAttachment> _attachments = new List<WaitAttachment>();
            private WaitHarness _h;
            private Pending _running;
            private int _nextId;
            private int _restarts;

            internal Simulation(PbtCase c, Totals totals)
            {
                _c = c;
                _totals = totals;
            }

            internal void Run()
            {
                int firstPid = _c.Int(1000, 60000);
                using (_h = new WaitHarness(firstPid))
                {
                    int sessions = _c.SmallInt(1, 3);
                    for (int i = 0; i < sessions; i++) _attachments.Add(_h.Attach());
                    _c.Log("start firstPid=" + firstPid + " sessions=" + sessions);

                    int events = _c.Int(8, 40);
                    for (int i = 0; i < events; i++) Step();

                    // Drain: answer everything still outstanding so every request is checked.
                    int guard = 0;
                    while ((_running != null || _bypassInFlight.Count > 0 || QueuedSdkCount() > 0) && guard++ < 200)
                    {
                        Advance(_c.Int(0, 2000));
                        if (_bypassInFlight.Count > 0 && (_running == null || _c.Bool())) RespondBypass();
                        else if (_running != null) RespondSdk();
                        else throw new InvalidOperationException("queued SDK_Operations were not released while the Worker was idle");
                    }
                    Assert.True(_running == null && _bypassInFlight.Count == 0 && QueuedSdkCount() == 0, "drain did not finish");
                }
            }

            private void Step()
            {
                int roll = _c.Int(0, 99);
                if (roll < 35) SendSdk();
                else if (roll < 50) SendBypass();
                else if (roll < 70) Advance(_c.Chance(0.2) ? 0 : _c.Int(1, 4000));
                else if (roll < 88) { if (_running != null) RespondSdk(); else SendSdk(); }
                else if (roll < 97) { if (_bypassInFlight.Count > 0) RespondBypass(); else SendBypass(); }
                else RestartIfIdle();
            }

            private int QueuedSdkCount() => _byClientId.Values.Count(p => p.IsSdk && p.ChildLine == null);

            private int OutstandingSdk(WaitAttachment owner) => _byClientId.Values.Count(p => p.IsSdk && p.Owner == owner);

            private void SendSdk()
            {
                var owner = _c.Pick(_attachments);
                // Stay within both limits however the host counts the running request.
                if (OutstandingSdk(owner) >= SessionQueueCap || _byClientId.Values.Count(p => p.IsSdk) >= QueueDepth)
                {
                    _c.Log("skip sdk send: limits");
                    return;
                }
                var command = _c.Pick(SdkCommands);
                var pending = NewPending(owner, true, command[0] + "/" + command[1]);
                pending.EnqueuedAtMs = _h.Clock.NowMs;
                _c.Log("t=" + _h.Clock.NowMs + " send sdk id=" + pending.Id + " " + pending.Label + " from " + owner.Name + (_running == null ? " (idle)" : " (busy)"));
                _h.Send(owner, Request(pending.Id, command[0], command[1]));
                PumpAndRecord();
            }

            private void SendBypass()
            {
                var owner = _c.Pick(_attachments);
                var command = _c.Pick(BypassCommands);
                var pending = NewPending(owner, false, command[0] + (command[1] == null ? string.Empty : "/" + command[1]));
                pending.EnqueuedAtMs = _h.Clock.NowMs;
                _c.Log("t=" + _h.Clock.NowMs + " send bypass id=" + pending.Id + " " + pending.Label + " from " + owner.Name);
                _h.Send(owner, Request(pending.Id, command[0], command[1]));
                PumpAndRecord();
                Assert.True(pending.ChildLine != null, "bypass command " + pending.Label + " was not written to the child at once");
            }

            private Pending NewPending(WaitAttachment owner, bool isSdk, string label)
            {
                var pending = new Pending { Id = ++_nextId, IsSdk = isSdk, Label = label, Owner = owner };
                _byClientId.Add(pending.Id.ToString(CultureInfo.InvariantCulture), pending);
                return pending;
            }

            private void Advance(int ms)
            {
                _h.Clock.Advance(ms);
                _c.Log("advance " + ms + " -> t=" + _h.Clock.NowMs);
                // A timer may release nothing here (no expiry in range), but record whatever it wrote.
                PumpAndRecord();
            }

            private void PumpAndRecord()
            {
                foreach (var line in _h.Pump())
                {
                    string clientId = (string)line["params"]?["testClientId"];
                    Assert.False(clientId == null, "the child received a line the test did not send: " + line.ToString(Formatting.None));
                    Assert.True(_byClientId.TryGetValue(clientId, out Pending pending), "the child received an unknown request id " + clientId);
                    Assert.True(pending.ChildLine == null, "request " + clientId + " was written to the child twice");
                    pending.ChildLine = line;
                    pending.ReleasedAtMs = _h.Clock.NowMs;
                    pending.ReleasedToPid = _h.Child.Pid;
                    _c.Log("t=" + _h.Clock.NowMs + " released id=" + clientId + " to pid " + pending.ReleasedToPid
                        + (pending.IsSdk ? " wait=" + (pending.ReleasedAtMs - pending.EnqueuedAtMs) : " (bypass)"));
                    if (pending.IsSdk)
                    {
                        Assert.True(_running == null, "a second SDK_Operation was released while one was in flight");
                        _running = pending;
                    }
                    else
                    {
                        _bypassInFlight.Add(pending);
                    }
                }
            }

            private void RespondSdk()
            {
                var pending = _running;
                _running = null;
                Respond(pending);
                PumpAndRecord();
            }

            private void RespondBypass()
            {
                var pending = _c.Pick(_bypassInFlight);
                _bypassInFlight.Remove(pending);
                Respond(pending);
                PumpAndRecord();
            }

            private void Respond(Pending pending)
            {
                var payload = Payload();
                var before = _attachments.ToDictionary(a => a, a => a.Responses().Count);
                _c.Log("t=" + _h.Clock.NowMs + " child answers id=" + pending.Id + " payload=" + payload.ToString(Formatting.None));
                _h.Respond(pending.ChildLine, payload);
                _byClientId.Remove(pending.Id.ToString(CultureInfo.InvariantCulture));

                foreach (var other in _attachments.Where(a => a != pending.Owner))
                    Assert.True(before[other] == other.Responses().Count, "a response reached an Attachment that does not own it");
                var received = pending.Owner.Responses().Skip(before[pending.Owner]).ToList();
                Assert.True(received.Count == 1, "expected one routed response for id " + pending.Id + ", got " + received.Count);
                var frame = received[0];
                _c.Log("routed " + frame.ToString(Formatting.None));

                AssertPayloadUnchanged(frame, pending.Id, payload);
                if (pending.IsSdk)
                {
                    var shared = AssertShared(frame);
                    long expectedWait = pending.ReleasedAtMs - pending.EnqueuedAtMs;
                    Assert.Equal(expectedWait, (long)shared["queueWaitMs"]);
                    Assert.Equal(pending.ReleasedToPid, (int)shared["workerPid"]);
                    Assert.Equal(_h.Child.Pid, (int)shared["workerPid"]);
                    if (expectedWait == 0) _totals.ImmediateSdk++;
                    else _totals.WaitedSdk++;
                }
                else
                {
                    Assert.True(frame[SharedKey] == null, "a bypass response carries " + SharedKey + ": " + frame.ToString(Formatting.None));
                    _totals.Bypass++;
                }
            }

            private void RestartIfIdle()
            {
                if (_restarts >= 2 || _running != null || _bypassInFlight.Count > 0 || _byClientId.Count > 0)
                {
                    _c.Log("skip restart");
                    return;
                }
                _restarts++;
                int oldPid = _h.Child.Pid;
                _h.ExitChild(_h.Child);
                _h.Ready();
                Assert.True(_h.Child.Pid != oldPid, "the replacement child kept the old PID");
                _c.Log("t=" + _h.Clock.NowMs + " restart pid " + oldPid + " -> " + _h.Child.Pid);
                _totals.Restarts++;
                PumpAndRecord();
            }

            private JObject Payload()
            {
                var payload = new JObject
                {
                    ["status"] = "ok",
                    ["value"] = _c.Int(-1000, 1000),
                    ["text"] = "r" + _c.Int(0, 99999).ToString(CultureInfo.InvariantCulture)
                };
                if (_c.Bool())
                {
                    var items = new JArray();
                    int count = _c.SmallInt(0, 3);
                    for (int i = 0; i < count; i++) items.Add(new JObject { ["n"] = i, ["flag"] = _c.Bool() });
                    payload["items"] = items;
                }
                // An existing result._meta must survive untouched: the Gateway, not the broker, merges into it.
                if (_c.Chance(0.3)) payload["_meta"] = new JObject { ["existing"] = "kept", ["busyWaitMs"] = 5 };
                return payload;
            }
        }

        // ---- Assertions ----

        private static JObject AssertShared(JObject frame)
        {
            var shared = frame[SharedKey] as JObject;
            Assert.True(shared != null, "an SDK_Operation response has no " + SharedKey + " object: " + frame.ToString(Formatting.None));
            Assert.Equal(new[] { "queueWaitMs", "workerPid" }, shared.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(JTokenType.Integer, shared["queueWaitMs"].Type);
            Assert.Equal(JTokenType.Integer, shared["workerPid"].Type);
            return shared;
        }

        private static void AssertPayloadUnchanged(JObject frame, int clientId, JObject payload)
        {
            var stripped = (JObject)frame.DeepClone();
            stripped.Remove(SharedKey);
            var expected = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = clientId,
                ["result"] = payload.DeepClone()
            };
            Assert.True(JToken.DeepEquals(expected, stripped),
                "the routed response differs from the child response beyond " + SharedKey
                + ".\nExpected: " + expected.ToString(Formatting.None)
                + "\nActual:   " + frame.ToString(Formatting.None));
        }

        private static JObject Request(int id, string method, string action)
        {
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = new JObject()
            };
            if (action != null) request["action"] = action;
            return request;
        }

        // ---- Harness and fakes ----

        private sealed class WaitHarness : IDisposable
        {
            internal WaitHarness(int firstPid)
            {
                Launcher = new WaitChildLauncher(firstPid);
                Options = SharedWorkerHostOptions.Parse(new[]
                {
                    "--shared-host",
                    "--worker-executable", @"C:\GeneXus\GxMcp.Worker.exe",
                    "--kb", @"C:\KBs\KbA",
                    "--installation", @"C:\GeneXus",
                    "--driver", "native",
                    "--major", "18"
                });
                var settings = new SharedHostSettingsResult(
                    QueueWaitMs, QueueDepth, SessionQueueCap, 4, 1024, 1, 120,
                    new Dictionary<string, string>(), new List<string>());
                Runtime = new SharedWorkerHostRuntime(Options, settings, Clock, Launcher, Registry, runBackgroundThreads: false);
                Runtime.Start();
                Ready();
            }

            internal SharedWorkerHostOptions Options { get; }
            internal WaitClock Clock { get; } = new WaitClock();
            internal WaitChildLauncher Launcher { get; }
            internal WaitRegistryWriter Registry { get; } = new WaitRegistryWriter();
            internal SharedWorkerHostRuntime Runtime { get; }
            internal WaitChild Child => Launcher.Started.Last();

            private int _attachCount;

            internal WaitAttachment Attach()
            {
                var attachment = new WaitAttachment(Runtime, "s" + (++_attachCount).ToString(CultureInfo.InvariantCulture));
                string frame = new JObject
                {
                    ["type"] = "attach",
                    ["gxmcp"] = "attach",
                    ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["version"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["identityKey"] = Options.IdentityKey,
                    ["key"] = Options.IdentityKey,
                    ["gatewayPid"] = 4242,
                    ["gatewayStartTimeUtcTicks"] = 638000000000000000L,
                    ["attachNonce"] = Guid.NewGuid().ToString("N")
                }.ToString(Formatting.None);
                Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame, out SharedWorkerEnvelope envelope, out string parseError), parseError);
                Assert.True(Runtime.Attach(attachment, envelope, out string error), error);
                Assert.False(string.IsNullOrEmpty(attachment.AttachmentId));
                return attachment;
            }

            // Tags each request with its client id in params, because the broker rewrites "id".
            internal void Send(WaitAttachment attachment, JObject frame)
            {
                if (frame["id"] != null && frame["params"] is JObject parameters)
                    parameters["testClientId"] = frame["id"].ToString();
                Runtime.Receive(attachment, frame.ToString(Formatting.None));
            }

            internal List<JObject> Pump()
            {
                var child = Child;
                int before = child.Received.Count;
                Runtime.PumpChildInbox();
                return child.Received.Skip(before).ToList();
            }

            internal void Ready() => Runtime.OnChildLine(Child, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/worker/sdk_ready"
            }.ToString(Formatting.None));

            internal void Respond(JObject childRequest, JObject result) => Runtime.OnChildLine(Child, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = childRequest["id"].DeepClone(),
                ["result"] = result.DeepClone()
            }.ToString(Formatting.None));

            internal void ExitChild(WaitChild child)
            {
                child.Exit(1);
                Runtime.OnChildExited(child);
            }

            public void Dispose() => Runtime.Dispose();
        }

        // Monotonic clock under test control; Advance runs the timers due at or before the new
        // time once each, earliest first, on the test thread.
        private sealed class WaitClock : ISharedHostClock
        {
            private readonly List<WaitTimer> _timers = new List<WaitTimer>();

            public long NowMs { get; private set; } = 1000;

            public ISharedHostTimer CreateTimer(Action callback)
            {
                var timer = new WaitTimer(callback);
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

        private sealed class WaitTimer : ISharedHostTimer
        {
            internal WaitTimer(Action callback) { Callback = callback; }
            internal Action Callback { get; }
            internal long? DueAtMs { get; set; }
            internal bool Disposed { get; private set; }
            public void Change(long? dueAtMs) { DueAtMs = dueAtMs; }
            public void Dispose() { Disposed = true; DueAtMs = null; }
        }

        private sealed class WaitChildLauncher : ISharedHostChildLauncher
        {
            private int _nextPid;

            internal WaitChildLauncher(int firstPid) { _nextPid = firstPid - 1; }

            internal List<WaitChild> Started { get; } = new List<WaitChild>();

            public ISharedHostChild Start(ProcessStartInfo startInfo)
            {
                var child = new WaitChild(++_nextPid);
                Started.Add(child);
                return child;
            }
        }

        private sealed class WaitChild : ISharedHostChild
        {
            private readonly ManualResetEvent _exited = new ManualResetEvent(false);

            internal WaitChild(int pid)
            {
                Pid = pid;
                StartTimeUtcTicks = DateTime.UtcNow.Ticks;
            }

            internal List<JObject> Received { get; } = new List<JObject>();

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
                Received.Add(JObject.Parse(line));
            }

            public void Kill() => Exit(-1);

            internal void Exit(int code)
            {
                HasExited = true;
                ExitCode = code;
                _exited.Set();
            }

            public void Dispose() { }
        }

        private sealed class WaitRegistryWriter : ISharedHostRegistryWriter
        {
            public void Write(SharedWorkerHostRegistryRecord record) { }
            public void Delete() { }
        }

        private sealed class WaitAttachment : ISharedHostAttachment
        {
            private readonly SharedWorkerHostRuntime _runtime;
            private long _sequence;
            private int _stopped;

            internal WaitAttachment(SharedWorkerHostRuntime runtime, string name)
            {
                _runtime = runtime;
                Name = name;
                ConnectionId = Guid.NewGuid().ToString("N");
            }

            internal string Name { get; }
            internal List<JObject> Lines { get; } = new List<JObject>();

            public string ConnectionId { get; }
            public string AttachmentId { get; set; } = string.Empty;

            public long NextSequence() => Interlocked.Increment(ref _sequence);

            public bool Enqueue(string line)
            {
                if (Volatile.Read(ref _stopped) != 0) return false;
                Lines.Add(JObject.Parse(line));
                return true;
            }

            public void Stop()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
                _runtime.Detach(this);
            }

            // JSON-RPC responses (frames with an id and a result or an error); notifications excluded.
            internal List<JObject> Responses() => Lines
                .Where(l => l["jsonrpc"] != null && l["id"] != null && (l["result"] != null || l["error"] != null))
                .ToList();
        }
    }
}
