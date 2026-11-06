// Feature: concurrent-multi-session-workers, Property 15: Busy_Envelope content
// Validates: Requirements 1.6, 3.6
//
// Property test for the Busy_Envelope the shared-host broker returns (RFC #854, task 3.7). It is
// written before the implementation (tasks 3.10 and 3.11) and uses only the seam contract pinned in
// the header of SharedWorkerHostSchedulerWiringTests.cs (task 3.5): ISharedHostClock,
// ISharedHostTimer, ISharedHostChildLauncher, ISharedHostChild, ISharedHostRegistryWriter,
// ISharedHostAttachment, the six-argument SharedWorkerHostRuntime constructor, Start, Attach,
// Receive, Detach, OnChildLine, OnChildExited and PumpChildInbox. The fakes below are private
// copies of that file's fakes under distinct names; nothing here adds a seam member.
//
// Oracle: a shadow SharedHostScheduler (the pure core, task 1.9) is fed the same submits,
// completions and expiries on the same fake clock. Its Snapshot() is "the snapshot" of Property 15:
// the envelope must report that snapshot's running operation and elapsed time, and the expiry
// position that the shadow's ExpireDue reports.
//
// Content pinned for every Busy_Envelope (design.md "Busy_Envelope (tool-result shape)" and
// requirements.md Requirement 3 criterion 6):
// - A JSON-RPC result, never a JSON-RPC error: frame.id is the client id exactly as sent, there is
//   no "error" member at the frame level, result.status="error" and result.code="WorkerBusy".
// - result.error is ConcurrencyErrorCatalog.Build(WorkerBusy, reason, ...): code, reason, the
//   catalog message and hint, retryable=true, dispatchState="not-delivered", and an integer
//   retryAfterMs from 1000 to the configured queue wait.
// - kbIdentityKey is the identity key of the KB the request was sent to (design.md shape; this is
//   how Property 15 "names the snapshot's own Physical_KB_Identity"). kbPath, when present, is that
//   KB's path.
// - runningOperation ("method/action", compared case-insensitively) and runningElapsedMs come from
//   that KB's scheduler snapshot at the rejection instant.
// - Exactly one of queuePosition (queue-wait-expired), queueDepth (queue-full) and sessionQueueCap
//   (session-queue-limit) is present, with the 1-based position at removal, the configured depth,
//   or the effective Session_Queue_Cap.
// - Requirement 1 criterion 6: a second KB (KB A) is busy at the same time with a different
//   operation and its own queue; the KB B envelope carries nothing of KB A (identity key, path,
//   operation name), and no other Attachment of either KB receives a frame.
// - Requirement 3 criterion 6 exclusions: every request (running, queued and rejected, on both KBs)
//   carries "SECRET-MARKER-<n>" values in params (source text, Lease token, connection string,
//   cancel token, target). No marker and no argument name appears anywhere in an envelope.
//
// Runs are deterministic: each test passes a fixed base seed to SeededInterleavingRunner;
// GXMCP_PBT_SEED replays one failing case.
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
    [Trait("Feature", "concurrent-multi-session-workers")]
    [Trait("Property", "15")]
    public sealed class SharedWorkerHostBusyEnvelopePropertyTests
    {
        private const string PropertyTag = "Feature: concurrent-multi-session-workers, Property 15: Busy_Envelope content";
        private const int Iterations = 100;
        private const string QueueWaitExpired = "queue-wait-expired";
        private const string SecretPrefix = "SECRET-MARKER-";

        private const string KbPathA = @"C:\KBs\BusyEnvelopeAlpha";
        private const string KbPathB = @"C:\KBs\BusyEnvelopeBravo";

        // SDK_Operations (CommandLaneClassifier.IsThreadSafe is false for each). The two lists are
        // disjoint, so a KB A operation name in a KB B envelope is a cross-KB leak.
        private static readonly string[][] OperationsA =
        {
            new[] { "build", "BuildAll" },
            new[] { "analyze", "Explain" },
            new[] { "object", "Write" },
        };

        private static readonly string[][] OperationsB =
        {
            new[] { "object", "Read" },
            new[] { "search", "SearchSource" },
            new[] { "refactor", "Rename" },
            new[] { "build", "Build" },
        };

        // Argument names seeded into every request; none may appear in an envelope.
        private static readonly string[] ArgumentNames =
        {
            "testClientId", "sourceText", "leaseToken", "connectionString", "cancelToken", "target",
        };

        [Fact]
        public void Property15_Queue_Full_Envelope_Carries_Its_Own_Kb_Snapshot_And_No_Arguments()
        {
            var totals = new Totals();
            RunProperty(SubmitResult.QueueFull, 15001, totals);
            Assert.True(totals.Envelopes >= Iterations, "every case must check at least one queue-full envelope");
        }

        [Fact]
        public void Property15_Session_Queue_Limit_Envelope_Carries_Its_Own_Kb_Snapshot_And_No_Arguments()
        {
            var totals = new Totals();
            RunProperty(SubmitResult.SessionQueueLimit, 15002, totals);
            Assert.True(totals.Envelopes >= Iterations, "every case must check at least one session-queue-limit envelope");
        }

        [Fact]
        public void Property15_Queue_Wait_Expired_Envelope_Carries_Its_Own_Kb_Snapshot_And_No_Arguments()
        {
            var totals = new Totals();
            RunProperty(QueueWaitExpired, 15003, totals);
            Assert.True(totals.Envelopes >= Iterations, "every case must check at least one queue-wait-expired envelope");
            Assert.True(totals.MultiExpiries > 0, "no expiry tick expired more than one request");
            Assert.True(totals.RunningReplaced > 0, "no case changed the running operation before the expiry");
            Assert.True(totals.PositionsAboveOne > 0, "no expired request reported a position above 1");
        }

        private static void RunProperty(string reason, int baseSeed, Totals totals)
        {
            int? replay = SeededInterleavingRunner.ParseSeed(
                Environment.GetEnvironmentVariable(SeededInterleavingRunner.SeedEnvironmentVariable));
            SeededInterleavingRunner.Run(
                PropertyTag + " (" + reason + ")",
                Iterations,
                c => new Scenario(c, reason, totals).Run(),
                replay,
                baseSeed);
        }

        private sealed class Totals
        {
            internal int Envelopes;
            internal int MultiExpiries;
            internal int RunningReplaced;
            internal int PositionsAboveOne;
        }

        // ---- One generated case ----

        private sealed class Sent
        {
            internal string Tag;
            internal JToken ClientId;
            internal string Operation;
        }

        private sealed class Scenario
        {
            private readonly PbtCase _c;
            private readonly string _reason;
            private readonly Totals _totals;
            private int _next;
            private long _sequence;

            private BusyHarness _kbA;
            private BusyHarness _kbB;
            private SharedHostScheduler _shadow;
            private List<BusyAttachment> _sessions;
            private BusyAttachment _attachA;
            private string _operationA;
            private int _depth;
            private int _cap;
            private int _wait;

            internal Scenario(PbtCase c, string reason, Totals totals)
            {
                _c = c;
                _reason = reason;
                _totals = totals;
            }

            internal void Run()
            {
                ChooseLimits();
                _c.Log("limits: depth=" + _depth + " cap=" + _cap + " wait=" + _wait + " reason=" + _reason);

                using (_kbA = new BusyHarness(KbPathA, 8, 4, 30000))
                using (_kbB = new BusyHarness(KbPathB, _depth, _cap, _wait))
                {
                    Assert.NotEqual(_kbA.Options.IdentityKey, _kbB.Options.IdentityKey);
                    _shadow = new SharedHostScheduler(new SchedulerLimits(_depth, _cap, _wait), () => _kbB.Clock.NowMs);

                    MakeKbABusy();

                    int sessionCount = SessionCount();
                    _sessions = Enumerable.Range(0, sessionCount).Select(_ => _kbB.Attach()).ToList();
                    _c.Log("kbB sessions=" + sessionCount);

                    var first = SubmitB(_c.Pick(_sessions), "started");
                    Assert.Equal(first.Tag, ClientTagOf(_kbB.Pump().Single()));
                    MarkAll();

                    if (_reason == QueueWaitExpired) RunExpiry();
                    else RunRejection();
                }
            }

            private void ChooseLimits()
            {
                if (_reason == SubmitResult.SessionQueueLimit)
                {
                    // A session can reach its cap only while the queue still has room.
                    _depth = _c.Int(2, 8);
                    _cap = _c.Int(1, _depth - 1);
                }
                else
                {
                    _depth = _c.Int(1, 8);
                    _cap = _c.Int(1, _depth);
                }
                _wait = _reason == QueueWaitExpired ? _c.Int(1000, 20000) : _c.Int(1000, 600000);
            }

            private int SessionCount()
            {
                int needed = (_depth + _cap - 1) / _cap;
                if (_reason == SubmitResult.QueueFull) return Math.Min(8, needed + _c.Int(0, 1));
                return _c.Int(1, 4);
            }

            // KB A: one running SDK_Operation, an older start than KB B, and its own queue.
            private void MakeKbABusy()
            {
                _attachA = _kbA.Attach();
                var op = _c.Pick(OperationsA);
                _operationA = op[0] + "/" + op[1];
                var running = Frame(op, out string tag, out _);
                _kbA.Send(_attachA, running);
                Assert.Equal(tag, ClientTagOf(_kbA.Pump().Single()));
                int queued = _c.Int(0, 3);
                for (int i = 0; i < queued; i++) _kbA.Send(_attachA, Frame(_c.Pick(OperationsA), out _, out _));
                Assert.Empty(_kbA.Pump());
                _kbA.Clock.Advance(_c.Int(1, 50000));
                _c.Log("kbA running " + _operationA + " queued=" + queued);
            }

            // ---- queue-full and session-queue-limit ----

            private void RunRejection()
            {
                _kbB.Clock.Advance(_c.Int(0, _wait / 2));
                var counts = _sessions.ToDictionary(s => s, s => 0);
                BusyAttachment limited = _c.Pick(_sessions);

                if (_reason == SubmitResult.QueueFull)
                {
                    int total = 0;
                    while (total < _depth)
                    {
                        var open = _sessions.Where(s => counts[s] < _cap).ToList();
                        var s = _c.Pick(open);
                        SubmitB(s, "queued");
                        counts[s]++;
                        total++;
                        _kbB.Clock.Advance(_c.Int(0, 50));
                    }
                }
                else
                {
                    for (int i = 0; i < _cap; i++)
                    {
                        SubmitB(limited, "queued");
                        counts[limited]++;
                        _kbB.Clock.Advance(_c.Int(0, 50));
                    }
                    var others = _sessions.Where(s => s != limited).ToList();
                    int room = _depth - 1 - _cap;
                    int extra = others.Count == 0 ? 0 : _c.Int(0, room);
                    for (int i = 0; i < extra; i++)
                    {
                        var open = others.Where(s => counts[s] < _cap).ToList();
                        if (open.Count == 0) break;
                        var s = _c.Pick(open);
                        SubmitB(s, "queued");
                        counts[s]++;
                        _kbB.Clock.Advance(_c.Int(0, 50));
                    }
                }
                Assert.Empty(_kbB.Pump());
                MarkAll();

                int rejections = _c.Int(1, 3);
                for (int i = 0; i < rejections; i++)
                {
                    var caller = _reason == SubmitResult.QueueFull ? _c.Pick(_sessions) : limited;
                    var snapshot = _shadow.Snapshot();
                    var rejected = SubmitB(caller, _reason);

                    var frame = SingleNewFrameFor(caller);
                    AssertEnvelope(frame, rejected, snapshot, expectedPosition: null);
                    Assert.Empty(_kbB.Pump());
                    Assert.DoesNotContain(_kbB.Child.Received, line => ClientTagOf(line) == rejected.Tag);
                    _kbB.Clock.Advance(_c.Int(0, 50));
                }
            }

            // ---- queue-wait-expired ----

            private void RunExpiry()
            {
                _kbB.Clock.Advance(_c.Int(0, 3000));
                var counts = _sessions.ToDictionary(s => s, s => 0);
                int k = _c.Int(1, Math.Min(_depth, _sessions.Count * _cap));
                var queued = new Dictionary<string, Sent>(StringComparer.Ordinal);
                // Keep the whole fill within half the queue wait, so nothing expires while filling.
                int stagger = Math.Min(300, _wait / 2 / k);
                for (int i = 0; i < k; i++)
                {
                    var s = _c.Pick(_sessions.Where(x => counts[x] < _cap).ToList());
                    var sent = SubmitB(s, "queued");
                    queued[sent.Tag] = sent;
                    counts[s]++;
                    _kbB.Clock.Advance(_c.Int(0, stagger));
                }
                Assert.Empty(_kbB.Pump());
                MarkAll();

                // Optionally replace the running operation before any deadline, so the envelope must
                // report the operation running at expiry, not the one running at enqueue.
                if (k >= 2 && _c.Bool())
                {
                    long deadline = _shadow.NextDeadlineMs.Value;
                    long room = deadline - _kbB.Clock.NowMs;
                    if (room > 0) _kbB.Clock.Advance(_c.Int(0, (int)Math.Min(room, int.MaxValue)));
                    var running = _shadow.Snapshot().Running;
                    var childLine = _kbB.Child.Received.Last(l => ClientTagOf(l) == running.ChildRequestId);
                    var nextShadow = _shadow.Complete(running.ChildRequestId);
                    Assert.NotNull(nextShadow);
                    _kbB.Respond(childLine);
                    var released = _kbB.Pump().Single();
                    Assert.Equal(nextShadow.ChildRequestId, ClientTagOf(released));
                    queued.Remove(nextShadow.ChildRequestId);
                    MarkAll();
                    _totals.RunningReplaced++;
                    _c.Log("running replaced by " + nextShadow.ChildRequestId + " at " + _kbB.Clock.NowMs);
                }

                while (_shadow.NextDeadlineMs.HasValue)
                {
                    long due = _shadow.NextDeadlineMs.Value + 1;
                    long delta = due - _kbB.Clock.NowMs;
                    Assert.True(delta >= 1, "the generator moved the clock past an unexpired deadline");
                    _kbB.Clock.Advance(delta);

                    var snapshot = _shadow.Snapshot();
                    var expired = _shadow.ExpireDue();
                    Assert.NotEmpty(expired);
                    _c.Log("tick " + _kbB.Clock.NowMs + " expired=" + string.Join(",", expired.Select(e => e.Request.ChildRequestId + "@" + e.Position)));
                    if (expired.Count > 1) _totals.MultiExpiries++;

                    var byOwner = expired.GroupBy(e => e.Request.SessionId).ToDictionary(g => g.Key, g => g.ToList());
                    foreach (var session in _sessions)
                    {
                        var frames = NewFrames(session);
                        List<ExpiredRequest> mine;
                        if (!byOwner.TryGetValue(session.AttachmentId, out mine)) mine = new List<ExpiredRequest>();
                        Assert.Equal(mine.Count, frames.Count);
                        foreach (var e in mine)
                        {
                            var sent = queued[e.Request.ChildRequestId];
                            var frame = frames.Single(f => JToken.DeepEquals(f["id"], sent.ClientId));
                            AssertEnvelope(frame, sent, snapshot, e.Position);
                            if (e.Position > 1) _totals.PositionsAboveOne++;
                            queued.Remove(sent.Tag);
                        }
                    }
                    Assert.Empty(NewFrames(_attachA));
                    Assert.Empty(_kbB.Pump());
                }

                Assert.Empty(queued);
            }

            // ---- Submissions ----

            private Sent SubmitB(BusyAttachment session, string expected)
            {
                var op = _c.Pick(OperationsB);
                var frame = Frame(op, out string tag, out JToken clientId);
                var sent = new Sent { Tag = tag, ClientId = clientId, Operation = op[0] + "/" + op[1] };

                var result = _shadow.Submit(new QueuedRequest
                {
                    ChildRequestId = tag,
                    SessionId = session.AttachmentId,
                    ClientRequestIdJson = clientId.ToString(Formatting.None),
                    Operation = sent.Operation,
                    CancelToken = (string)frame["params"]["cancelToken"],
                    EnqueuedAtMs = _kbB.Clock.NowMs,
                    Sequence = ++_sequence,
                });
                string actual = result.Started ? "started" : result.Queued ? "queued" : result.RejectReason;
                Assert.Equal(expected, actual);

                _c.Log("kbB submit " + tag + " " + sent.Operation + " by " + session.AttachmentId + " at " + _kbB.Clock.NowMs + " -> " + actual);
                _kbB.Send(session, frame);
                return sent;
            }

            // A request whose params carry secret markers and argument names that must never reach an envelope.
            private JObject Frame(string[] op, out string tag, out JToken clientId)
            {
                int n = ++_next;
                tag = "t" + n.ToString(CultureInfo.InvariantCulture);
                clientId = _c.Bool() ? (JToken)new JValue(1000 + n) : new JValue("req-" + n.ToString(CultureInfo.InvariantCulture));
                string marker = SecretPrefix + n.ToString(CultureInfo.InvariantCulture);
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = clientId.DeepClone(),
                    ["method"] = op[0],
                    ["action"] = op[1],
                    ["params"] = new JObject
                    {
                        ["testClientId"] = tag,
                        ["sourceText"] = "Procedure Secret\n  &x = '" + marker + "-source'\nEndProcedure",
                        ["leaseToken"] = marker + "-lease",
                        ["connectionString"] = "Server=gx;User Id=sa;Password=" + marker + "-password",
                        ["cancelToken"] = marker + "-cancel",
                        ["target"] = marker + "-target",
                    },
                };
            }

            // ---- Frame bookkeeping ----

            private void MarkAll()
            {
                foreach (var s in _sessions ?? new List<BusyAttachment>()) NewFrames(s);
                NewFrames(_attachA);
            }

            private static List<JObject> NewFrames(BusyAttachment attachment)
            {
                var frames = attachment.JsonRpc().Skip(attachment.Seen).ToList();
                attachment.Seen += frames.Count;
                return frames;
            }

            private JObject SingleNewFrameFor(BusyAttachment caller)
            {
                JObject frame = null;
                foreach (var s in _sessions)
                {
                    var frames = NewFrames(s);
                    if (s == caller) frame = Assert.Single(frames);
                    else Assert.Empty(frames);
                }
                Assert.Empty(NewFrames(_attachA));
                return frame;
            }

            // ---- The property ----

            private void AssertEnvelope(JObject frame, Sent request, SchedulerSnapshot snapshot, int? expectedPosition)
            {
                string reason = expectedPosition.HasValue ? QueueWaitExpired : _reason;
                _totals.Envelopes++;

                // A JSON-RPC result for the caller's own id, never a JSON-RPC error.
                Assert.Equal("2.0", (string)frame["jsonrpc"]);
                Assert.True(JToken.DeepEquals(request.ClientId, frame["id"]),
                    "envelope id " + frame["id"] + " is not the client id " + request.ClientId);
                Assert.Null(frame.Property("error"));
                var result = Assert.IsType<JObject>(frame["result"]);
                Assert.Equal("error", (string)result["status"]);
                Assert.Equal(ConcurrencyErrorCatalog.WorkerBusy, (string)result["code"]);

                // Catalog fields.
                var error = Assert.IsType<JObject>(result["error"]);
                var catalog = ConcurrencyErrorCatalog.Build(
                    ConcurrencyErrorCatalog.WorkerBusy, reason, new ConcurrencyErrorFields { QueueWaitMs = _wait });
                Assert.Equal(ConcurrencyErrorCatalog.WorkerBusy, (string)error["code"]);
                Assert.Equal(reason, (string)error["reason"]);
                Assert.True((bool?)error["retryable"] == true, "retryable must be true");
                Assert.Equal(ConcurrencyDispatchState.NotDelivered, (string)error["dispatchState"]);
                Assert.Equal((string)catalog["message"], (string)error["message"]);
                Assert.False(string.IsNullOrWhiteSpace((string)error["hint"]));
                Assert.Equal((string)catalog["hint"], (string)error["hint"]);
                var retryAfter = error["retryAfterMs"];
                Assert.NotNull(retryAfter);
                Assert.Equal(JTokenType.Integer, retryAfter.Type);
                Assert.InRange((long)retryAfter, 1000L, _wait);

                // This KB's identity and this KB's snapshot (Requirement 1 criterion 6).
                Assert.Equal(_kbB.Options.IdentityKey, (string)error["kbIdentityKey"]);
                if (error["kbPath"] != null)
                    Assert.Equal(KbPathB, (string)error["kbPath"], ignoreCase: true);
                var running = snapshot.RunningStatus;
                Assert.NotNull(running);
                Assert.Equal(running.Operation, (string)error["runningOperation"], ignoreCase: true);
                Assert.NotEqual(_operationA, (string)error["runningOperation"], StringComparer.OrdinalIgnoreCase);
                Assert.NotNull(error["runningElapsedMs"]);
                Assert.Equal(running.ElapsedMs, (long)error["runningElapsedMs"]);

                // Exactly the one queue field that matches the reason.
                var present = new[] { "queuePosition", "queueDepth", "sessionQueueCap" }.Where(f => error[f] != null).ToList();
                Assert.Single(present);
                if (reason == QueueWaitExpired)
                    Assert.Equal(expectedPosition.Value, (int)error["queuePosition"]);
                else if (reason == SubmitResult.QueueFull)
                    Assert.Equal(_depth, (int)error["queueDepth"]);
                else
                    Assert.Equal(_cap, (int)error["sessionQueueCap"]);

                // No argument, marker, or KB A detail anywhere in the frame.
                string text = frame.ToString(Formatting.None);
                Assert.DoesNotContain(SecretPrefix, text, StringComparison.Ordinal);
                foreach (var name in ArgumentNames)
                    Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(_kbA.Options.IdentityKey, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("BusyEnvelopeAlpha", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(_operationA, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The client tag of a line the child received: the broker rewrites the id, so each request
        // carries its tag in params.
        private static string ClientTagOf(JObject childLine) => (string)childLine["params"]?["testClientId"];

        // ---- Harness and fakes (private copies of the task 3.5 fakes) ----

        private sealed class BusyHarness : IDisposable
        {
            internal BusyHarness(string kbPath, int queueDepth, int sessionQueueCap, int queueWaitMs)
            {
                Options = SharedWorkerHostOptions.Parse(new[]
                {
                    "--shared-host",
                    "--worker-executable", @"C:\GeneXus\GxMcp.Worker.exe",
                    "--kb", kbPath,
                    "--installation", @"C:\GeneXus",
                    "--driver", "native",
                    "--major", "18"
                });
                var settings = new SharedHostSettingsResult(
                    queueWaitMs, queueDepth, sessionQueueCap, 4, 1024, 1, 120,
                    new Dictionary<string, string>(), new List<string>());
                Runtime = new SharedWorkerHostRuntime(Options, settings, Clock, Launcher, Registry, runBackgroundThreads: false);
                Runtime.Start();
                Ready();
            }

            internal SharedWorkerHostOptions Options { get; }
            internal BusyClock Clock { get; } = new BusyClock();
            internal BusyChildLauncher Launcher { get; } = new BusyChildLauncher();
            internal BusyRegistryWriter Registry { get; } = new BusyRegistryWriter();
            internal SharedWorkerHostRuntime Runtime { get; }
            internal BusyChild Child => Launcher.Started.Last();

            internal BusyAttachment Attach()
            {
                var attachment = new BusyAttachment(Runtime);
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

            internal void Send(BusyAttachment attachment, JObject frame) =>
                Runtime.Receive(attachment, frame.ToString(Formatting.None));

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

            internal void Respond(JObject childRequest) => Runtime.OnChildLine(Child, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = childRequest["id"].DeepClone(),
                ["result"] = new JObject { ["status"] = "ok" }
            }.ToString(Formatting.None));

            public void Dispose() => Runtime.Dispose();
        }

        // Advance moves the time first, then runs once each the callbacks of the timers due at or
        // before the new time, earliest first, on the test thread.
        private sealed class BusyClock : ISharedHostClock
        {
            private readonly List<BusyTimer> _timers = new List<BusyTimer>();

            public long NowMs { get; private set; } = 1000;

            public ISharedHostTimer CreateTimer(Action callback)
            {
                var timer = new BusyTimer(callback);
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

        private sealed class BusyTimer : ISharedHostTimer
        {
            internal BusyTimer(Action callback) { Callback = callback; }
            internal Action Callback { get; }
            internal long? DueAtMs { get; set; }
            internal bool Disposed { get; private set; }
            public void Change(long? dueAtMs) { DueAtMs = dueAtMs; }
            public void Dispose() { Disposed = true; DueAtMs = null; }
        }

        private sealed class BusyChildLauncher : ISharedHostChildLauncher
        {
            private int _nextPid = 60000;

            internal List<BusyChild> Started { get; } = new List<BusyChild>();

            public ISharedHostChild Start(ProcessStartInfo startInfo)
            {
                var child = new BusyChild(++_nextPid);
                Started.Add(child);
                return child;
            }
        }

        private sealed class BusyChild : ISharedHostChild
        {
            private readonly ManualResetEvent _exited = new ManualResetEvent(false);

            internal BusyChild(int pid)
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

            public void Kill()
            {
                HasExited = true;
                ExitCode = -1;
                _exited.Set();
            }

            public void Dispose() { }
        }

        private sealed class BusyRegistryWriter : ISharedHostRegistryWriter
        {
            public void Write(SharedWorkerHostRegistryRecord record) { }
            public void Delete() { }
        }

        private sealed class BusyAttachment : ISharedHostAttachment
        {
            private readonly SharedWorkerHostRuntime _runtime;
            private long _sequence;
            private int _stopped;

            internal BusyAttachment(SharedWorkerHostRuntime runtime)
            {
                _runtime = runtime;
                ConnectionId = Guid.NewGuid().ToString("N");
            }

            internal List<JObject> Lines { get; } = new List<JObject>();

            // JSON-RPC frames the scenario has already inspected.
            internal int Seen { get; set; }

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

            internal List<JObject> JsonRpc() => Lines.Where(l => l["jsonrpc"] != null).ToList();
        }
    }
}
