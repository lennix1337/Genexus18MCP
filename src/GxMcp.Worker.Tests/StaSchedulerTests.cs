using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class StaSchedulerTests
    {
        [Fact]
        public void ResolvePriority_ClassifiesOperationsAccurately()
        {
            // P0 Interactive
            var readReq = JObject.Parse("{\"method\":\"object\",\"action\":\"ExtractSource\",\"params\":{\"name\":\"Trn\"}}");
            Assert.Equal(CommandPriority.P0_Interactive, StaScheduler.ResolvePriority(readReq));

            var inspectReq = JObject.Parse("{\"method\":\"inspect\",\"params\":{\"target\":\"Obj\"}}");
            Assert.Equal(CommandPriority.P0_Interactive, StaScheduler.ResolvePriority(inspectReq));

            var propsGetReq = JObject.Parse("{\"method\":\"properties\",\"action\":\"get\",\"params\":{\"target\":\"Obj\"}}");
            Assert.Equal(CommandPriority.P0_Interactive, StaScheduler.ResolvePriority(propsGetReq));

            var whoamiReq = JObject.Parse("{\"method\":\"whoami\"}");
            Assert.Equal(CommandPriority.P0_Interactive, StaScheduler.ResolvePriority(whoamiReq));

            // P1 Normal (writes, build, validate)
            var editReq = JObject.Parse("{\"method\":\"edit\",\"params\":{\"name\":\"Trn\"}}");
            Assert.Equal(CommandPriority.P1_Normal, StaScheduler.ResolvePriority(editReq));

            var writeReq = JObject.Parse("{\"method\":\"write\",\"params\":{\"name\":\"Trn\"}}");
            Assert.Equal(CommandPriority.P1_Normal, StaScheduler.ResolvePriority(writeReq));

            var buildReq = JObject.Parse("{\"method\":\"lifecycle\",\"action\":\"build\"}");
            Assert.Equal(CommandPriority.P1_Normal, StaScheduler.ResolvePriority(buildReq));

            // P2 Background (search source, doc)
            var searchSourceReq = JObject.Parse("{\"method\":\"search\",\"action\":\"SearchSource\",\"params\":{\"pattern\":\"foo\"}}");
            Assert.Equal(CommandPriority.P2_Background, StaScheduler.ResolvePriority(searchSourceReq));

            var docReq = JObject.Parse("{\"method\":\"doc\",\"params\":{\"target\":\"Trn\"}}");
            Assert.Equal(CommandPriority.P2_Background, StaScheduler.ResolvePriority(docReq));

            // Explicit overrides
            var p0Override = JObject.Parse("{\"method\":\"search\",\"action\":\"SearchSource\",\"_meta\":{\"priority\":\"p0\"}}");
            Assert.Equal(CommandPriority.P0_Interactive, StaScheduler.ResolvePriority(p0Override));
        }

        [Fact]
        public void PriorityOrdering_DequeuesP0BeforeP1BeforeP2()
        {
            var scheduler = new StaScheduler();
            scheduler.Clear();

            var p2 = new ScheduledCommandItem { Priority = CommandPriority.P2_Background, RawLine = "p2", ClientId = "c1" };
            var p1 = new ScheduledCommandItem { Priority = CommandPriority.P1_Normal, RawLine = "p1", ClientId = "c1" };
            var p0 = new ScheduledCommandItem { Priority = CommandPriority.P0_Interactive, RawLine = "p0", ClientId = "c1" };

            // Enqueue in reverse order
            scheduler.Enqueue(p2);
            scheduler.Enqueue(p1);
            scheduler.Enqueue(p0);

            Assert.True(scheduler.TryTakeNext(out var first));
            Assert.Equal("p0", first.RawLine);

            Assert.True(scheduler.TryTakeNext(out var second));
            Assert.Equal("p1", second.RawLine);

            Assert.True(scheduler.TryTakeNext(out var third));
            Assert.Equal("p2", third.RawLine);

            Assert.False(scheduler.TryTakeNext(out _));
        }

        [Fact]
        public void ResolveClientId_UsesAttachmentAndGatewaySessionMetadata()
        {
            Assert.Equal("attachment-7", StaScheduler.ResolveClientId(JObject.Parse(
                "{\"method\":\"object\",\"_meta\":{\"attachmentId\":\"attachment-7\"}}")));
            Assert.Equal("session-9", StaScheduler.ResolveClientId(JObject.Parse(
                "{\"method\":\"object\",\"_meta\":{\"sessionId\":\"session-9\"}}")));
        }

        [Fact]
        public void FairnessRoundRobin_InterleavesClientsWithinSamePriority()
        {
            var scheduler = new StaScheduler();
            scheduler.Clear();

            scheduler.Enqueue(new ScheduledCommandItem { Priority = CommandPriority.P0_Interactive, RawLine = "clientA-1", ClientId = "A" });
            scheduler.Enqueue(new ScheduledCommandItem { Priority = CommandPriority.P0_Interactive, RawLine = "clientA-2", ClientId = "A" });
            scheduler.Enqueue(new ScheduledCommandItem { Priority = CommandPriority.P0_Interactive, RawLine = "clientB-1", ClientId = "B" });
            scheduler.Enqueue(new ScheduledCommandItem { Priority = CommandPriority.P0_Interactive, RawLine = "clientB-2", ClientId = "B" });

            var dequeued = new List<string>();
            while (scheduler.TryTakeNext(out var item))
            {
                dequeued.Add(item.RawLine);
            }

            Assert.Equal(4, dequeued.Count);
            // Should alternate between client A and client B
            Assert.Equal("clientA-1", dequeued[0]);
            Assert.Equal("clientB-1", dequeued[1]);
            Assert.Equal("clientA-2", dequeued[2]);
            Assert.Equal("clientB-2", dequeued[3]);
        }

        // Issue #321: the Worker resolves the bounded-wait value from the RPC
        // envelope and then the process environment. The environment read happens
        // on every enqueue (not once at startup), which is what docs/environment_variables.md
        // now claims, so the precedence is pinned here.
        [Fact]
        public void ResolveBusyWaitMs_DefaultsWhenNothingIsConfigured()
        {
            using (new ScopedBusyWaitEnvironment(null))
            {
                Assert.Equal(15000, StaScheduler.ResolveBusyWaitMs(JObject.Parse("{\"method\":\"whoami\"}")));
            }
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("not-a-number")]
        [InlineData("")]
        public void ResolveBusyWaitMs_NonPositiveOrInvalidEnvironmentFallsBackToDefault(string configured)
        {
            using (new ScopedBusyWaitEnvironment(configured))
            {
                Assert.Equal(15000, StaScheduler.ResolveBusyWaitMs(JObject.Parse("{\"method\":\"whoami\"}")));
            }
        }

        [Fact]
        public void ResolveBusyWaitMs_EnvironmentIsReconsultedForEveryCommand()
        {
            using (new ScopedBusyWaitEnvironment("1200"))
            {
                Assert.Equal(1200, StaScheduler.ResolveBusyWaitMs(JObject.Parse("{\"method\":\"whoami\"}")));
            }

            // Same process, new value: a second command observes the new environment
            // without a Worker restart. What still requires a restart is the process
            // environment of the *parent* that spawns the Worker.
            using (new ScopedBusyWaitEnvironment("3400"))
            {
                Assert.Equal(3400, StaScheduler.ResolveBusyWaitMs(JObject.Parse("{\"method\":\"whoami\"}")));
            }
        }

        [Fact]
        public void ResolveBusyWaitMs_EnvelopeKnobWinsOverEnvironment()
        {
            using (new ScopedBusyWaitEnvironment("1200"))
            {
                Assert.Equal(900,
                    StaScheduler.ResolveBusyWaitMs(JObject.Parse("{\"method\":\"whoami\",\"params\":{\"busyWaitMs\":900}}")));
                Assert.Equal(700,
                    StaScheduler.ResolveBusyWaitMs(JObject.Parse("{\"method\":\"whoami\",\"_meta\":{\"busyWaitMs\":700}}")));
            }
        }

        // Issue #321: the hint returned with the WAIT expiry error is the only
        // operator-facing description of the knob. It used to advertise a
        // per-command "busyWaitMs" override that no published tool can reach.
        [Fact]
        public void BusyWaitHint_AdvertisesOnlyTheReachableControl()
        {
            string programPath = Path.Combine(
                TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Program.cs");
            Assert.True(File.Exists(programPath), "Program.cs must exist at: " + programPath);

            string source = File.ReadAllText(programPath);
            int envMention = source.IndexOf("GXMCP_BUSY_WAIT_MS", StringComparison.Ordinal);
            Assert.True(envMention >= 0, "The WorkerBusy WAIT hint must name GXMCP_BUSY_WAIT_MS.");

            // Take the whole "hint: \"...\"" literal that mentions it, not just the
            // text after the variable name.
            int literalStart = source.LastIndexOf("hint: \"", envMention, StringComparison.Ordinal);
            Assert.True(literalStart >= 0, "The GXMCP_BUSY_WAIT_MS mention must live in a hint literal.");
            int literalEnd = source.IndexOf('"', literalStart + "hint: \"".Length);
            Assert.True(literalEnd > literalStart, "The hint literal must be terminated.");

            string hint = source.Substring(literalStart + "hint: \"".Length, literalEnd - literalStart - "hint: \"".Length);
            Assert.Contains("GXMCP_BUSY_WAIT_MS", hint);
            Assert.DoesNotContain("busyWaitMs", hint);
            Assert.DoesNotContain("per command", hint, StringComparison.OrdinalIgnoreCase);
        }

        // Issue #321: the doc states there is no published per-command override.
        // That is a claim about the Gateway, so guard it against drift: the day
        // forwarding is implemented, this fails and the doc must be revisited.
        [Fact]
        public void NoPublishedToolCanForwardTheBusyWaitKnob()
        {
            string repoRoot = TestFixtures.FindRepoRoot();
            string[] gatewayFiles =
            {
                Path.Combine(repoRoot, "src", "GxMcp.Gateway", "tool_definitions.json"),
                Path.Combine(repoRoot, "src", "GxMcp.Gateway", "Program.WorkerLifecycle.cs"),
                Path.Combine(repoRoot, "src", "GxMcp.Gateway", "McpRouter.cs")
            };

            foreach (string file in gatewayFiles)
            {
                Assert.True(File.Exists(file), "Expected file must exist at: " + file);
                Assert.DoesNotContain("busyWaitMs", File.ReadAllText(file));
            }

            // The doctor's template probe is the one internal request that sets the knob. It takes the value from its own
            // budget, never from a tool argument, so it is the single occurrence the guard allows.
            string probe = Path.Combine(repoRoot, "src", "GxMcp.Gateway", "Program.DoctorWwpTemplates.cs");
            Assert.True(File.Exists(probe), "Expected file must exist at: " + probe);
            string probeSource = File.ReadAllText(probe);
            Assert.Equal(1, probeSource.Split(new[] { "busyWaitMs" }, StringSplitOptions.None).Length - 1);
            Assert.Contains("[\"busyWaitMs\"] = Math.Max(0, (int)budget.TotalMilliseconds - DoctorWwpTemplatesBusySlackMs)", probeSource);
        }

        // Issue #321: the STA scheduler section must describe the environment
        // provenance instead of claiming the values are re-read per request.
        [Fact]
        public void BusyEnvironmentDocumentation_DescribesWorkerProcessEnvironment()
        {
            string docPath = Path.Combine(
                TestFixtures.FindRepoRoot(), "docs", "environment_variables.md");
            Assert.True(File.Exists(docPath), "environment_variables.md must exist at: " + docPath);

            string doc = File.ReadAllText(docPath);
            Assert.Contains("Both values come from the **Worker process environment**", doc);
            Assert.DoesNotContain("read per request", doc);
            Assert.DoesNotContain("Per-command tuning", doc);
        }

        private sealed class ScopedBusyWaitEnvironment : IDisposable
        {
            private readonly string _previous = Environment.GetEnvironmentVariable("GXMCP_BUSY_WAIT_MS");

            public ScopedBusyWaitEnvironment(string value)
            {
                Environment.SetEnvironmentVariable("GXMCP_BUSY_WAIT_MS", value);
            }

            public void Dispose() => Environment.SetEnvironmentVariable("GXMCP_BUSY_WAIT_MS", _previous);
        }

        [Fact]
        public void DrainPendingInteractive_DrainsOnlyP0Items()
        {
            var scheduler = new StaScheduler();
            scheduler.Clear();

            scheduler.Enqueue(new ScheduledCommandItem { Priority = CommandPriority.P2_Background, RawLine = "scan", ClientId = "c1" });
            scheduler.Enqueue(new ScheduledCommandItem { Priority = CommandPriority.P0_Interactive, RawLine = "read-1", ClientId = "c2" });
            scheduler.Enqueue(new ScheduledCommandItem { Priority = CommandPriority.P0_Interactive, RawLine = "read-2", ClientId = "c3" });
            scheduler.Enqueue(new ScheduledCommandItem { Priority = CommandPriority.P1_Normal, RawLine = "write", ClientId = "c1" });

            Assert.True(scheduler.HasPendingInteractive);

            var drained = new List<string>();
            int count = scheduler.DrainPendingInteractive(item => drained.Add(item.RawLine));

            Assert.Equal(2, count);
            Assert.Equal(new[] { "read-1", "read-2" }, drained.ToArray());
            Assert.False(scheduler.HasPendingInteractive);

            // Remaining items should be P1 and P2
            Assert.True(scheduler.TryTakeNext(out var next1));
            Assert.Equal("write", next1.RawLine);

            Assert.True(scheduler.TryTakeNext(out var next2));
            Assert.Equal("scan", next2.RawLine);
        }

        [Fact]
        public void ExpireTimedOut_IdentifiesExpiredItems()
        {
            var scheduler = new StaScheduler();
            scheduler.Clear();

            var now = DateTime.UtcNow;
            scheduler.Enqueue(new ScheduledCommandItem
            {
                Priority = CommandPriority.P1_Normal,
                RawLine = "fresh",
                ClientId = "c1",
                EnqueuedAtUtc = now,
                BusyWaitMs = 15000
            });
            scheduler.Enqueue(new ScheduledCommandItem
            {
                Priority = CommandPriority.P1_Normal,
                RawLine = "stale",
                ClientId = "c1",
                EnqueuedAtUtc = now.AddSeconds(-20),
                BusyWaitMs = 15000
            });

            var expired = scheduler.ExpireTimedOut(now);
            Assert.Single(expired);
            Assert.Equal("stale", expired[0].RawLine);

            // "fresh" is still in the queue
            Assert.True(scheduler.TryTakeNext(out var remaining));
            Assert.Equal("fresh", remaining.RawLine);
        }
    }
}
