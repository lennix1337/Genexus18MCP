// Property test for broker Attachment routing (RFC #854, task 3.6, design Property 5).
//
// Written before the implementation (task 3.11). It drives SharedWorkerHostRuntime only
// through the seam contract pinned in the header of SharedWorkerHostSchedulerWiringTests.cs
// (task 3.5): ISharedHostClock, ISharedHostTimer, ISharedHostChildLauncher, ISharedHostChild,
// ISharedHostRegistryWriter, ISharedHostAttachment, the six-argument constructor with
// runBackgroundThreads=false, and Start, Attach, Receive, Detach, OnChildLine and
// PumpChildInbox. The fakes below are private to this file on purpose.
//
// The oracle is independent of the broker's id and token rewriting. Every request carries a
// unique params.testTag; the test keeps its own map from tag to owning Attachment, client id
// and progress token. The scripted child answers with result.echoTag and emits progress with
// params.testTag, so a frame routed to the wrong Attachment, or with the wrong id or token,
// is detected without reading _meta.attachmentId or the child id format.
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
    public sealed class SharedWorkerHostRoutingPropertyTests
    {
        private const string PropertyName = "Feature: concurrent-multi-session-workers, Property 5: Attachment routing";
        private const int Iterations = 120;
        private const int MaxRequestsPerCase = 40;

        // Feature: concurrent-multi-session-workers, Property 5: Attachment routing
        // **Validates: Requirements 8.1, 8.2**
        [Fact]
        [Trait("Feature", "concurrent-multi-session-workers")]
        [Trait("Property", "5: Attachment routing")]
        public void Responses_And_Progress_With_Colliding_Ids_Reach_Only_The_Owning_Attachment()
        {
            SeededInterleavingRunner.Run(PropertyName + " (colliding ids)", Iterations, c => RunCase(c, allowDetach: false));
        }

        // Feature: concurrent-multi-session-workers, Property 5: Attachment routing
        // **Validates: Requirements 8.1, 8.2, 8.8**
        [Fact]
        [Trait("Feature", "concurrent-multi-session-workers")]
        [Trait("Property", "5: Attachment routing")]
        public void Nothing_Reaches_A_Detached_Attachment_And_The_Others_Keep_Their_Own_Responses()
        {
            SeededInterleavingRunner.Run(PropertyName + " (with detaches)", Iterations, c => RunCase(c, allowDetach: true));
        }

        private static void RunCase(PbtCase c, bool allowDetach)
        {
            using (var model = new RoutingModel(c))
            {
                int initial = c.Int(2, 4);
                for (int i = 0; i < initial; i++) model.AttachNew();

                int steps = c.Int(15, 60);
                for (int step = 0; step < steps; step++)
                {
                    int roll = c.Int(1, 100);
                    if (roll <= 35) model.SendRequest();
                    else if (roll <= 55) model.Pump();
                    else if (roll <= 70) model.ChildRespond();
                    else if (roll <= 82) model.ChildProgress();
                    else if (roll <= 90) model.Cancel();
                    else if (roll <= 95)
                    {
                        if (allowDetach) model.DetachOne(); else model.SendRequest();
                    }
                    else model.AttachNew();
                }

                model.DrainAndAssertCompleteness();
            }
        }

        // ---- Oracle ----

        private sealed class TrackedRequest
        {
            internal RoutingAttachment Owner;
            internal JToken ClientId;
            internal string Token;           // client progress token, or null
            internal string Tag;
            internal string Method;
            internal JObject ChildLine;      // the line the child received, once it did
            internal bool AtChild;           // received by the child and not yet answered by it
            internal bool ChildAnswered;
            internal bool CancelSent;
            internal int Delivered;          // response frames delivered to the owner

            public override string ToString() =>
                Owner.Name + "/id=" + ClientId.ToString(Formatting.None) + "/" + Method + "/tag=" + Tag
                + (Token != null ? "/token=" + Token : string.Empty);
        }

        private sealed class Delivery
        {
            internal RoutingAttachment To;
            internal string Kind;            // "response" or "progress"
            internal string Tag;             // the request the frame was about, or null for broker answers
            internal int Emission;           // progress emission number
        }

        private sealed class RoutingModel : IDisposable
        {
            private readonly PbtCase _c;
            private readonly RoutingClock _clock = new RoutingClock();
            private readonly RoutingChildLauncher _launcher = new RoutingChildLauncher();
            private readonly RoutingRegistryWriter _registry = new RoutingRegistryWriter();
            private readonly SharedWorkerHostOptions _options;
            private readonly SharedWorkerHostRuntime _runtime;
            private readonly List<RoutingAttachment> _attachments = new List<RoutingAttachment>();
            private readonly Dictionary<string, TrackedRequest> _byTag = new Dictionary<string, TrackedRequest>(StringComparer.Ordinal);
            private readonly List<TrackedRequest> _requests = new List<TrackedRequest>();
            private readonly List<TrackedRequest> _childPending = new List<TrackedRequest>();
            private int _childLinesSeen;
            private int _nextTag;
            private int _nextEmission;

            internal RoutingModel(PbtCase c)
            {
                _c = c;
                _options = SharedWorkerHostOptions.Parse(new[]
                {
                    "--shared-host",
                    "--worker-executable", @"C:\GeneXus\GxMcp.Worker.exe",
                    "--kb", @"C:\KBs\KbRouting",
                    "--installation", @"C:\GeneXus",
                    "--driver", "native",
                    "--major", "18"
                });
                // Limits far above MaxRequestsPerCase and a clock that never moves: no
                // Busy_Envelope and no queue-wait expiry, so every answer is a routing outcome.
                var settings = new SharedHostSettingsResult(
                    600000, 256, 256, 4, 1024, 1, 120,
                    new Dictionary<string, string>(), new List<string>());
                _runtime = new SharedWorkerHostRuntime(_options, settings, _clock, _launcher, _registry, runBackgroundThreads: false);
                _runtime.Start();
                _runtime.OnChildLine(Child, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/worker/sdk_ready"
                }.ToString(Formatting.None));
                _c.Log("start: child pid=" + Child.Pid + ", ready");
            }

            private RoutingChild Child => _launcher.Started.Last();

            private List<RoutingAttachment> Live => _attachments.Where(a => !a.Detached).ToList();

            // ---- Actions ----

            internal void AttachNew()
            {
                if (Live.Count >= 4) { SendRequest(); return; }
                var attachment = new RoutingAttachment(_runtime, "a" + _attachments.Count, stringIds: _c.Bool());
                string frame = new JObject
                {
                    ["type"] = "attach",
                    ["gxmcp"] = "attach",
                    ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["version"] = SharedWorkerHostProtocol.ProtocolVersion,
                    ["identityKey"] = _options.IdentityKey,
                    ["key"] = _options.IdentityKey,
                    ["gatewayPid"] = 4300 + _attachments.Count,
                    ["gatewayStartTimeUtcTicks"] = 638000000000000000L,
                    ["attachNonce"] = Guid.NewGuid().ToString("N")
                }.ToString(Formatting.None);
                Assert.True(SharedWorkerHostProtocol.TryParseEnvelope(frame, out SharedWorkerEnvelope envelope, out string parseError), parseError);
                Assert.True(_runtime.Attach(attachment, envelope, out string error), error);
                Assert.False(string.IsNullOrEmpty(attachment.AttachmentId), "Attach must assign an attachment id");
                _attachments.Add(attachment);
                _c.Log("attach " + attachment.Name + (attachment.StringIds ? " (string ids)" : " (numeric ids)"));
                Observe();
            }

            internal void SendRequest()
            {
                var live = Live;
                if (live.Count == 0 || _requests.Count >= MaxRequestsPerCase) { Pump(); return; }
                var owner = _c.Pick(live);
                // Per-Attachment counters start at 1, so client ids collide across Attachments.
                int n = ++owner.NextClientId;
                JToken clientId = owner.StringIds ? (JToken)("r" + n.ToString(CultureInfo.InvariantCulture)) : n;
                // Tokens are unique inside an Attachment and collide across Attachments.
                string token = _c.Chance(0.6) ? "progress-" + n.ToString(CultureInfo.InvariantCulture) : null;
                string tag = "tag-" + (++_nextTag).ToString(CultureInfo.InvariantCulture);

                int lane = _c.Int(1, 4);
                string method = lane == 1 ? "ping" : lane == 2 ? "list" : "object";
                string action = lane == 1 ? null : lane == 2 ? "Objects" : "Read";

                var request = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = clientId.DeepClone(),
                    ["method"] = method,
                    ["params"] = new JObject { ["testTag"] = tag }
                };
                if (action != null) request["action"] = action;
                if (token != null) request["_meta"] = new JObject { ["progressToken"] = token };

                var tracked = new TrackedRequest
                {
                    Owner = owner,
                    ClientId = clientId,
                    Token = token,
                    Tag = tag,
                    Method = method + (action != null ? "/" + action : string.Empty)
                };
                _requests.Add(tracked);
                _byTag[tag] = tracked;
                owner.Requests[Key(clientId)] = tracked;

                _c.Log("send " + tracked);
                _runtime.Receive(owner, request.ToString(Formatting.None));
                Observe();
            }

            internal void Pump()
            {
                int written = _runtime.PumpChildInbox();
                _c.Log("pump: " + written + " line(s) to child");
                Observe();
            }

            internal void ChildRespond()
            {
                if (_childPending.Count == 0) { Pump(); return; }
                RespondTo(_c.Pick(_childPending));
            }

            private void RespondTo(TrackedRequest request)
            {
                int expected = request.Owner.Detached || request.Delivered > 0 ? 0 : 1;
                _childPending.Remove(request);
                request.AtChild = false;
                request.ChildAnswered = true;
                _c.Log("child responds to " + request + " (expect " + expected + " copy to " + request.Owner.Name + ")");
                _runtime.OnChildLine(Child, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = request.ChildLine["id"].DeepClone(),
                    ["result"] = new JObject { ["status"] = "ok", ["echoTag"] = request.Tag }
                }.ToString(Formatting.None));

                var deliveries = Observe();
                var copies = deliveries.Where(d => d.Kind == "response" && d.Tag == request.Tag).ToList();
                Assert.True(copies.Count == expected,
                    "child response for " + request + " delivered " + copies.Count + " time(s), expected " + expected
                    + " (to " + string.Join(",", copies.Select(d => d.To.Name)) + ")");
            }

            internal void ChildProgress()
            {
                var candidates = _childPending.Where(r => r.Token != null).ToList();
                if (candidates.Count == 0) { Pump(); return; }
                var request = _c.Pick(candidates);
                int emission = ++_nextEmission;
                // A detached owner gets nothing. A request already answered by the broker
                // (Cancelled) may or may not still route progress, but only to its owner.
                int expected = request.Owner.Detached ? 0 : request.Delivered > 0 ? -1 : 1;
                string childToken = request.ChildLine["_meta"]?["progressToken"]?.ToString();
                Assert.False(string.IsNullOrEmpty(childToken), "the child request for " + request + " lost its progress token");
                _c.Log("child progress #" + emission + " for " + request + " childToken=" + childToken
                    + " (expect " + expected + " copy to " + request.Owner.Name + ")");
                _runtime.OnChildLine(Child, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/progress",
                    ["params"] = new JObject
                    {
                        ["progressToken"] = childToken,
                        ["progress"] = emission,
                        ["testTag"] = request.Tag,
                        ["emission"] = emission
                    }
                }.ToString(Formatting.None));

                var deliveries = Observe();
                var copies = deliveries.Where(d => d.Kind == "progress" && d.Emission == emission).ToList();
                Assert.True(expected < 0 ? copies.Count <= 1 : copies.Count == expected,
                    "progress #" + emission + " for " + request + " delivered " + copies.Count + " time(s), expected " + expected
                    + " (to " + string.Join(",", copies.Select(d => d.To.Name)) + ")");
            }

            internal void Cancel()
            {
                var live = Live;
                if (live.Count == 0) { Pump(); return; }
                var from = _c.Pick(live);
                JToken target;
                var own = from.Requests.Values.ToList();
                if (own.Count > 0 && _c.Chance(0.7))
                {
                    target = _c.Pick(own).ClientId.DeepClone();
                }
                else
                {
                    // An id this Attachment never sent, which another Attachment may own.
                    int n = from.NextClientId + _c.Int(1, 3);
                    target = from.StringIds ? (JToken)("r" + n.ToString(CultureInfo.InvariantCulture)) : n;
                }
                if (from.Requests.TryGetValue(Key(target), out TrackedRequest owned)) owned.CancelSent = true;
                _c.Log("cancel from " + from.Name + " requestId=" + target.ToString(Formatting.None)
                    + (owned != null ? " (owns " + owned + ")" : " (owns nothing)"));
                _runtime.Receive(from, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/cancelled",
                    ["params"] = new JObject { ["requestId"] = target, ["reason"] = "property-test" }
                }.ToString(Formatting.None));
                Observe();
            }

            internal void DetachOne()
            {
                var live = Live;
                if (live.Count < 2) { SendRequest(); return; }
                // Empty the child inbox first: whatever of this Attachment has not reached the
                // child after the detach was still in the broker queue and must be removed.
                Pump();
                var victim = _c.Pick(live);
                bool viaFrame = _c.Bool();
                _c.Log("detach " + victim.Name + (viaFrame ? " via detach frame" : " via pipe EOF")
                    + "; outstanding: " + string.Join(", ", victim.Requests.Values.Where(r => r.Delivered == 0).Select(r => r.ToString())));
                if (viaFrame)
                {
                    _runtime.Receive(victim, new JObject
                    {
                        ["type"] = "detach",
                        ["gxmcp"] = "detach",
                        ["protocolVersion"] = SharedWorkerHostProtocol.ProtocolVersion,
                        ["version"] = SharedWorkerHostProtocol.ProtocolVersion,
                        ["identityKey"] = _options.IdentityKey,
                        ["key"] = _options.IdentityKey,
                        ["attachId"] = victim.AttachmentId,
                        ["attachmentId"] = victim.AttachmentId
                    }.ToString(Formatting.None));
                    Assert.True(victim.Stopped, "a valid detach frame must stop the Attachment transport");
                }
                else
                {
                    victim.MarkStopped();
                    _runtime.Detach(victim);
                }
                // Observe what the broker delivered up to and including the detach, then seal it.
                Observe();
                victim.Detached = true;
                victim.SealedAt = victim.Lines.Count;
                Observe();
            }

            // ---- Final drain ----

            internal void DrainAndAssertCompleteness()
            {
                _c.Log("drain");
                for (int round = 0; round < 4 * MaxRequestsPerCase + 10; round++)
                {
                    int written = _runtime.PumpChildInbox();
                    Observe();
                    if (written == 0 && _childPending.Count == 0) break;
                    foreach (var request in _childPending.ToList()) RespondTo(request);
                }

                Assert.True(_childPending.Count == 0, "drain did not finish: the child still owes " + _childPending.Count + " response(s)");
                foreach (var request in _requests)
                {
                    if (request.Owner.Detached)
                    {
                        Assert.True(request.Delivered <= 1, request + " was answered " + request.Delivered + " times");
                        continue;
                    }
                    Assert.True(request.Delivered == 1,
                        request + " of an attached Attachment was answered " + request.Delivered + " time(s), expected exactly once"
                        + (request.ChildLine == null ? "; it never reached the child" : string.Empty));
                }
            }

            // ---- Observation and invariants ----

            private List<Delivery> Observe()
            {
                var deliveries = new List<Delivery>();
                ObserveChild();
                foreach (var attachment in _attachments)
                {
                    for (; attachment.Seen < attachment.Lines.Count; attachment.Seen++)
                    {
                        var line = attachment.Lines[attachment.Seen];
                        if (line["jsonrpc"] == null) continue; // host frame
                        bool isResponse = line["id"] != null && line["method"] == null;
                        bool isProgress = string.Equals((string)line["method"], "notifications/progress", StringComparison.Ordinal);
                        if (!isResponse && !isProgress) continue; // broadcast notifications are not routed
                        string frame = line.ToString(Formatting.None);

                        Assert.False(attachment.Detached && attachment.Seen >= attachment.SealedAt,
                            "detached Attachment " + attachment.Name + " received " + frame);

                        var delivery = isResponse ? CheckResponse(attachment, line, frame) : CheckProgress(attachment, line, frame);
                        _c.Log("  -> " + attachment.Name + " " + delivery.Kind + " " + frame);
                        deliveries.Add(delivery);
                    }
                }
                return deliveries;
            }

            private void ObserveChild()
            {
                var child = Child;
                for (; _childLinesSeen < child.Received.Count; _childLinesSeen++)
                {
                    var line = child.Received[_childLinesSeen];
                    string method = (string)line["method"];
                    if (string.Equals(method, "notifications/cancelled", StringComparison.Ordinal))
                    {
                        string childId = line["params"]?["requestId"]?.ToString();
                        var target = _requests.FirstOrDefault(r => r.ChildLine != null
                            && string.Equals(r.ChildLine["id"]?.ToString(), childId, StringComparison.Ordinal));
                        Assert.True(target != null, "the child received a cancel for unknown child id " + childId);
                        Assert.True(target.CancelSent, "the child received a cancel for " + target + ", which its owner never cancelled");
                        _c.Log("  child <- cancel for " + target);
                        continue;
                    }
                    if (line["id"] == null || method == null)
                    {
                        _c.Log("  child <- other " + line.ToString(Formatting.None));
                        continue;
                    }

                    string tag = (string)line["params"]?["testTag"];
                    Assert.True(tag != null && _byTag.ContainsKey(tag), "the child received an untracked request " + line.ToString(Formatting.None));
                    var request = _byTag[tag];
                    Assert.True(request.ChildLine == null, request + " reached the child twice");
                    Assert.False(request.Owner.Detached,
                        request + " reached the child after its Attachment detached; queued requests must be removed");
                    // Colliding client ids must become distinct child ids, or the child's
                    // answers could not be told apart.
                    var clash = _requests.FirstOrDefault(r => r.ChildLine != null && JToken.DeepEquals(r.ChildLine["id"], line["id"]));
                    Assert.True(clash == null, request + " reached the child with the same child id as " + clash);
                    request.ChildLine = line;
                    request.AtChild = true;
                    _childPending.Add(request);
                    _c.Log("  child <- " + request + " childId=" + line["id"]);
                }
            }

            private Delivery CheckResponse(RoutingAttachment to, JObject line, string frame)
            {
                JToken id = line["id"];
                Assert.True(to.Requests.TryGetValue(Key(id), out TrackedRequest request),
                    to.Name + " received a response with id " + id.ToString(Formatting.None) + " it never sent: " + frame);
                Assert.True(JToken.DeepEquals(id, request.ClientId),
                    to.Name + " received id " + id.ToString(Formatting.None) + " but sent " + request.ClientId.ToString(Formatting.None));
                Assert.True(request.Delivered == 0, request + " was answered more than once: " + frame);

                string echoTag = (string)line["result"]?["echoTag"];
                if (echoTag != null)
                {
                    Assert.True(string.Equals(echoTag, request.Tag, StringComparison.Ordinal),
                        to.Name + " received, under id " + id.ToString(Formatting.None) + ", the child response for "
                        + (_byTag.TryGetValue(echoTag, out var other) ? other.ToString() : echoTag) + ": " + frame);
                    Assert.True(request.ChildAnswered, request + " was answered before the child answered it: " + frame);
                }
                else
                {
                    string code = (string)line["result"]?["code"];
                    Assert.True(string.Equals(code, "Cancelled", StringComparison.Ordinal),
                        "unexpected broker answer for " + request + ": " + frame);
                    Assert.True(request.CancelSent, request + " was answered Cancelled although its owner never cancelled it: " + frame);
                }
                request.Delivered++;
                return new Delivery { To = to, Kind = "response", Tag = echoTag };
            }

            private Delivery CheckProgress(RoutingAttachment to, JObject line, string frame)
            {
                string tag = (string)line["params"]?["testTag"];
                Assert.True(tag != null && _byTag.ContainsKey(tag), to.Name + " received progress the child never emitted: " + frame);
                var request = _byTag[tag];
                Assert.True(ReferenceEquals(request.Owner, to),
                    to.Name + " received progress that belongs to " + request + ": " + frame);
                Assert.True(string.Equals(line["params"]?["progressToken"]?.ToString(), request.Token, StringComparison.Ordinal),
                    to.Name + " received progress for " + request + " without its client token restored: " + frame);
                return new Delivery { To = to, Kind = "progress", Tag = tag, Emission = (int)line["params"]["emission"] };
            }

            private static string Key(JToken id) => id.Type + ":" + id.ToString(Formatting.None);

            public void Dispose() => _runtime.Dispose();
        }

        // ---- Fakes (private to this file; same seams as task 3.5) ----

        // The clock never moves in this property; timers are recorded and never fire.
        private sealed class RoutingClock : ISharedHostClock
        {
            public long NowMs => 1000;
            public ISharedHostTimer CreateTimer(Action callback) => new RoutingTimer();
        }

        private sealed class RoutingTimer : ISharedHostTimer
        {
            public void Change(long? dueAtMs) { }
            public void Dispose() { }
        }

        private sealed class RoutingChildLauncher : ISharedHostChildLauncher
        {
            private int _nextPid = 61000;

            internal List<RoutingChild> Started { get; } = new List<RoutingChild>();

            public ISharedHostChild Start(ProcessStartInfo startInfo)
            {
                var child = new RoutingChild(++_nextPid);
                Started.Add(child);
                return child;
            }
        }

        private sealed class RoutingChild : ISharedHostChild
        {
            private readonly ManualResetEvent _exited = new ManualResetEvent(false);

            internal RoutingChild(int pid)
            {
                Pid = pid;
                StartTimeUtcTicks = 638000000000000000L + pid;
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

        private sealed class RoutingRegistryWriter : ISharedHostRegistryWriter
        {
            public void Write(SharedWorkerHostRegistryRecord record) { }
            public void Delete() { }
        }

        // Records every line the broker hands it, even after Stop, so a frame sent to a
        // detached Attachment is visible to the oracle instead of being silently dropped.
        private sealed class RoutingAttachment : ISharedHostAttachment
        {
            private readonly SharedWorkerHostRuntime _runtime;
            private long _sequence;
            private int _stopped;

            internal RoutingAttachment(SharedWorkerHostRuntime runtime, string name, bool stringIds)
            {
                _runtime = runtime;
                Name = name;
                StringIds = stringIds;
                ConnectionId = Guid.NewGuid().ToString("N");
            }

            internal string Name { get; }
            internal bool StringIds { get; }
            internal int NextClientId { get; set; }
            internal Dictionary<string, TrackedRequest> Requests { get; } = new Dictionary<string, TrackedRequest>(StringComparer.Ordinal);
            internal List<JObject> Lines { get; } = new List<JObject>();
            internal int Seen { get; set; }
            internal bool Detached { get; set; }
            internal int SealedAt { get; set; }
            internal bool Stopped => Volatile.Read(ref _stopped) != 0;

            public string ConnectionId { get; }
            public string AttachmentId { get; set; } = string.Empty;

            public long NextSequence() => Interlocked.Increment(ref _sequence);

            public bool Enqueue(string line)
            {
                Lines.Add(JObject.Parse(line));
                return !Stopped;
            }

            // Mirrors the pipe transport: closing the pipe ends RunAsync, whose finally detaches.
            public void Stop()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
                _runtime.Detach(this);
            }

            internal void MarkStopped() => Interlocked.Exchange(ref _stopped, 1);
        }
    }
}
