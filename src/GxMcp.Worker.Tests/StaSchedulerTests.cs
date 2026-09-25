using System;
using System.Collections.Generic;
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
