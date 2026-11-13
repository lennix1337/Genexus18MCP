using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// External-change delivery: a fake Worker signal (ResourceChange) goes in, and what reaches which
    /// session comes out. The real session registry and subscribe handler are used; only the clock
    /// (the window scheduler) and the sinks are fakes.
    /// </summary>
    [Collection("stdio-subscriptions")]
    public class ResourceChangeFeedTests
    {
        private sealed class Harness
        {
            public readonly HttpSessionRegistry Registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            public readonly Dictionary<string, List<JObject>> Received = new Dictionary<string, List<JObject>>();
            public readonly List<Action> Scheduled = new List<Action>();
            public int SnapshotCalls;
            public ResourceChangeFeed Feed = null!;

            public Harness(int cap = 50)
            {
                McpSubscriptionProtocol.ResetStdioForTest();
                Feed = new ResourceChangeFeed(
                    TimeSpan.FromMilliseconds(500), cap,
                    anySubscribers: () => McpSubscriptionProtocol.HasSubscribers(Registry),
                    subscribers: () =>
                    {
                        SnapshotCalls++;
                        return McpSubscriptionProtocol.SnapshotSubscribers(
                            Registry,
                            json => Record("stdio", json),
                            (session, json) => Record(session.Id, json));
                    },
                    schedule: (_, action) => Scheduled.Add(action));
            }

            public HttpSessionState Session() => Registry.Create();

            public JObject Subscribe(HttpSessionState session, string uri, string kbId = "")
                => McpSubscriptionProtocol.Handle(Request("resources/subscribe", uri), session.Id, Registry,
                    new OwnershipFence(session.Id, kbId, 1))!;

            public void Unsubscribe(HttpSessionState session, string uri)
                => McpSubscriptionProtocol.Handle(Request("resources/unsubscribe", uri), session.Id, Registry);

            public void Change(string alias, string name, bool deleted = false, long revision = 1)
                => Feed.Enqueue(new ResourceChange(alias, name, "Procedure", deleted, revision, DateTime.UtcNow));

            public void FlushWindow()
            {
                var pending = Scheduled.ToArray();
                Scheduled.Clear();
                foreach (var action in pending) action();
            }

            public List<JObject> For(string target)
                => Received.TryGetValue(target, out var list) ? list : new List<JObject>();

            private void Record(string target, string json)
            {
                if (!Received.TryGetValue(target, out var list)) Received[target] = list = new List<JObject>();
                list.Add(JObject.Parse(json));
            }
        }

        private static JObject Request(string method, string uri) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = new JObject { ["uri"] = uri }
        };

        [Fact]
        public void Update_Reaches_Only_The_Subscribing_Session()
        {
            var h = new Harness();
            var subscribed = h.Session();
            var bystander = h.Session();
            h.Subscribe(subscribed, "genexus://objects/Customer", "sales");
            h.Subscribe(bystander, "genexus://objects/Order", "sales");

            h.Change("sales", "Customer");
            h.FlushWindow();

            var delivered = Assert.Single(h.For(subscribed.Id));
            Assert.Equal("notifications/resources/updated", delivered["method"]!.ToString());
            Assert.Equal("genexus://objects/Customer", delivered["params"]!["uri"]!.ToString());
            Assert.Equal("sales", delivered["params"]!["kbAlias"]!.ToString());
            Assert.True(delivered["params"]!["external"]!.Value<bool>());
            Assert.Empty(h.For(bystander.Id));
            Assert.Empty(h.For("stdio"));
        }

        [Fact]
        public void A_Subscription_To_An_Object_Part_Is_Notified_When_The_Object_Changes()
        {
            var h = new Harness();
            var session = h.Session();
            h.Subscribe(session, "genexus://objects/Customer/part/Source", "sales");

            h.Change("sales", "customer");
            h.FlushWindow();

            Assert.Equal("genexus://objects/Customer/part/Source", Assert.Single(h.For(session.Id))["params"]!["uri"]!.ToString());
        }

        [Fact]
        public void A_Change_In_Another_Kb_Is_Not_Delivered()
        {
            var h = new Harness();
            var unscoped = h.Session();
            var scoped = h.Session();
            h.Subscribe(unscoped, "genexus://objects/Customer", "sales");
            h.Subscribe(scoped, "genexus://kb/sales/objects/Customer", "other");

            h.Change("other", "Customer");
            h.FlushWindow();

            Assert.Empty(h.For(unscoped.Id));
            Assert.Empty(h.For(scoped.Id));

            h.Change("sales", "Customer");
            h.FlushWindow();

            Assert.Single(h.For(unscoped.Id));
            Assert.Single(h.For(scoped.Id));
        }

        [Fact]
        public void Deletion_Is_Reported()
        {
            var h = new Harness();
            var session = h.Session();
            h.Subscribe(session, "genexus://objects/Gone", "sales");

            h.Change("sales", "Gone", deleted: true);
            h.FlushWindow();

            Assert.True(Assert.Single(h.For(session.Id))["params"]!["deleted"]!.Value<bool>());
        }

        [Fact]
        public void Without_Subscribers_An_Event_Does_No_Work()
        {
            var h = new Harness();
            h.Session();

            for (int i = 0; i < 100; i++) h.Change("sales", "Customer" + i);

            Assert.Empty(h.Scheduled);          // no window timer was started
            Assert.Equal(0, h.SnapshotCalls);   // no subscriber list was built
            Assert.Empty(h.Received);
            Assert.Equal(100, h.Feed.Received);
            Assert.Equal(100, h.Feed.SkippedNoSubscribers);
            Assert.Equal(0, h.Feed.NotificationsSent);
        }

        [Theory]
        [InlineData("Customer")]
        [InlineData("http://localhost/objects/Customer")]
        [InlineData("file:///c:/kb/Customer")]
        [InlineData("genexus://")]
        [InlineData("genexus://unknown/Customer")]
        [InlineData("genexus://objects/Cust omer")]
        public void An_Invalid_Uri_Is_Rejected_And_Nothing_Is_Recorded(string uri)
        {
            var h = new Harness();
            var session = h.Session();

            var response = h.Subscribe(session, uri);

            Assert.Equal(-32602, response["error"]!["code"]!.Value<int>());
            Assert.False(session.HasResourceSubscriptions);
        }

        [Theory]
        [InlineData("genexus://objects/Customer")]
        [InlineData("genexus://objects/Customer/part/Source")]
        [InlineData("genexus://kb/sales/objects/Customer")]
        [InlineData("genexus://kb/changes")]
        [InlineData("genexus://kb/sales/kb/changes")]
        public void The_Served_Uri_Shapes_Are_Accepted(string uri)
        {
            var h = new Harness();
            var session = h.Session();

            Assert.True(h.Subscribe(session, uri)["result"]!["subscribed"]!.Value<bool>());
            Assert.True(session.HasResourceSubscriptions);
        }

        [Fact]
        public void Unsubscribe_Session_End_And_Reinitialize_Stop_Delivery()
        {
            var h = new Harness();
            var unsubscribed = h.Session();
            var ended = h.Session();
            var reinitialized = h.Session();
            var stays = h.Session();
            foreach (var s in new[] { unsubscribed, ended, reinitialized, stays })
                h.Subscribe(s, "genexus://objects/Customer", "sales");

            h.Unsubscribe(unsubscribed, "genexus://objects/Customer");
            Assert.True(h.Registry.Remove(ended.Id));
            McpSubscriptionProtocol.ClearSession(reinitialized.Id, h.Registry);

            h.Change("sales", "Customer");
            h.FlushWindow();

            Assert.Empty(h.For(unsubscribed.Id));
            Assert.Empty(h.For(ended.Id));
            Assert.Empty(h.For(reinitialized.Id));
            Assert.Single(h.For(stays.Id));
        }

        [Fact]
        public void Stdio_Subscriptions_Are_Honoured_And_Cleared_By_Initialize()
        {
            var h = new Harness();
            McpSubscriptionProtocol.Handle(Request("resources/subscribe", "genexus://objects/Customer"), "stdio", h.Registry,
                new OwnershipFence("stdio", "sales", 1));

            h.Change("sales", "Customer");
            h.Change("sales", "Order");     // not subscribed
            h.FlushWindow();
            Assert.Single(h.For("stdio"));

            McpSubscriptionProtocol.ClearSession("stdio", h.Registry);
            Assert.False(McpSubscriptionProtocol.HasSubscribers(h.Registry));
        }

        [Fact]
        public void A_Burst_Is_Coalesced_Capped_And_Counted()
        {
            var h = new Harness(cap: 10);
            var objectWatcher = h.Session();
            var kbWatcher = h.Session();
            h.Subscribe(objectWatcher, "genexus://objects/Hot", "sales");
            h.Subscribe(kbWatcher, "genexus://kb/changes", "sales");

            for (int i = 0; i < 5; i++) h.Change("sales", "Hot", revision: i);     // same object x5
            for (int i = 0; i < 30; i++) h.Change("sales", "Other" + i);           // 30 more distinct

            Assert.Single(h.Scheduled);                                           // one window for the whole burst
            Assert.Empty(h.Received);                                             // nothing sent before the window closes

            h.FlushWindow();

            var hot = Assert.Single(h.For(objectWatcher.Id));
            Assert.Equal(5, hot["params"]!["changes"]!.Value<int>());
            Assert.Equal(4, hot["params"]!["cacheRevision"]!.Value<int>());

            var summary = Assert.Single(h.For(kbWatcher.Id));                     // one KB-wide notification, not 35
            Assert.Equal(35, summary["params"]!["changes"]!.Value<int>());

            Assert.Equal(35, h.Feed.Received);
            Assert.Equal(4, h.Feed.Merged);                                       // 4 repeats of Hot
            Assert.Equal(21, h.Feed.Dropped);                                     // 30 + Hot = 31 distinct, 10 admitted
            Assert.Equal(2, h.Feed.NotificationsSent);

            // The next change opens a new window.
            h.Change("sales", "Hot");
            Assert.Single(h.Scheduled);
        }

        [Fact]
        public void The_Changes_Resource_Returns_Recent_Changes_For_The_Kb_And_Is_Bounded()
        {
            var h = new Harness();
            for (int i = 0; i < ResourceChangeFeed.RingCapacity + 20; i++) h.Change("sales", "Obj" + i);
            h.Change("other", "Elsewhere");

            var response = h.Feed.HandleRead(Request("resources/read", "genexus://kb/sales/kb/changes"), null)!;
            var body = JObject.Parse(response["result"]!["contents"]![0]!["text"]!.ToString());
            var names = body["changes"]!.Select(c => c["name"]!.ToString()).ToList();

            Assert.Equal("sales", body["kbAlias"]!.ToString());
            Assert.True(names.Count <= ResourceChangeFeed.RingCapacity);
            Assert.Equal("Obj" + (ResourceChangeFeed.RingCapacity + 19), names[0]);   // newest first
            Assert.DoesNotContain("Elsewhere", names);
            Assert.DoesNotContain("Obj0", names);

            // Unscoped read uses the session's KB, and fails clearly without one.
            var unscoped = h.Feed.HandleRead(Request("resources/read", "genexus://kb/changes"), "OTHER")!;
            Assert.Contains("Elsewhere", unscoped["result"]!["contents"]![0]!["text"]!.ToString());
            var noKb = h.Feed.HandleRead(Request("resources/read", "genexus://kb/changes"), null)!;
            Assert.Equal(-32602, noKb["error"]!["code"]!.Value<int>());

            Assert.Null(h.Feed.HandleRead(Request("resources/read", "genexus://kb/health"), "sales"));
        }
    }
}
