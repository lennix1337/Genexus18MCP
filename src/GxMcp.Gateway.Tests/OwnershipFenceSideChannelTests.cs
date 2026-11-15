using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class OwnershipFenceSideChannelTests
    {
        [Fact]
        public void Tasks_GetAndCancel_RejectAnotherOwnerWithSameHandle()
        {
            var registry = new BackgroundJobRegistry();
            var ownerA = new OwnershipFence("session-a", "kb-a", 7);
            var ownerB = new OwnershipFence("session-b", "kb-a", 7);
            var job = registry.Start("session-a", "build", 10, ownerA);

            var get = McpTasksProtocol.Handle(TaskRequest("tasks/get", job.Id), "session-b", registry, true, ownerB);
            var cancel = McpTasksProtocol.Handle(TaskRequest("tasks/cancel", job.Id), "session-b", registry, true, ownerB);

            Assert.Equal(-32003, get!["error"]!["code"]!.Value<int>());
            Assert.Equal(-32003, cancel!["error"]!["code"]!.Value<int>());
            Assert.Equal("running", job.Status);
        }

        [Fact]
        public void Tasks_GetOwnTaskAfterKbContextChange_NamesTheContextChange()
        {
            var registry = new BackgroundJobRegistry();
            var started = new OwnershipFence("session-a", "kb-a", 1);
            var now = new OwnershipFence("session-a", "kb-a", 2);
            var job = registry.Start("session-a", "build", 10, started);

            var get = McpTasksProtocol.Handle(TaskRequest("tasks/get", job.Id), "session-a", registry, true, now);

            Assert.Equal(-32003, get!["error"]!["code"]!.Value<int>());
            var data = (JObject)get["error"]!["data"]!;
            Assert.Equal("kb_context_changed", data["reason"]!.ToString());
            Assert.Equal(1, data["taskGeneration"]!.Value<long>());
            Assert.Equal(2, data["currentGeneration"]!.Value<long>());
            Assert.Equal("running", data["taskStatus"]!.ToString());
        }

        [Fact]
        public void Tasks_GetAnotherSessionsTask_DoesNotRevealItsContext()
        {
            var registry = new BackgroundJobRegistry();
            var job = registry.Start("session-a", "build", 10, new OwnershipFence("session-a", "kb-a", 7));

            var get = McpTasksProtocol.Handle(TaskRequest("tasks/get", job.Id), "session-b", registry, true,
                new OwnershipFence("session-b", "kb-a", 7));

            var data = (JObject)get!["error"]!["data"]!;
            Assert.Null(data["reason"]);
            Assert.Null(data["taskKbId"]);
        }

        [Fact]
        public void Subscription_DropsDelayedEventFromDifferentGeneration()
        {
            var registry = new McpModernSubscriptionRegistry();
            var fence = new OwnershipFence("session-a", "kb-a", 3);
            var request = new JObject
            {
                ["id"] = 1,
                ["method"] = McpModernSubscriptionProtocol.ListenMethod,
                ["params"] = new JObject
                {
                    ["notifications"] = new JObject { ["resourcesListChanged"] = true }
                }
            };
            Assert.True(registry.TryOpen(request, out var subscription, out _, fence));

            Assert.False(subscription!.TryQueue("notifications/resources/list_changed",
                new JObject { ["ownerScopeId"] = "session-a", ["kbId"] = "kb-a", ["generation"] = 2, ["epoch"] = 2 }));
            Assert.True(subscription.TryQueue("notifications/resources/list_changed",
                new JObject { ["ownerScopeId"] = "session-a", ["kbId"] = "kb-a", ["generation"] = 3, ["epoch"] = 3 }));
        }

        [Fact]
        public void ResourceSubscription_IsBoundToOwnerFence()
        {
            var session = new HttpSessionState { Id = "session-a" };
            var ownerA = new OwnershipFence("session-a", "kb-a", 1);
            var ownerB = new OwnershipFence("session-a", "kb-a", 2);
            Assert.True(session.SubscribeResource("genexus://objects/Customer", ownerA));
            Assert.True(session.IsSubscribedToResource("genexus://objects/Customer", ownerA));
            Assert.False(session.IsSubscribedToResource("genexus://objects/Customer", ownerB));
        }

        [Fact]
        public void StatelessSubscriptionPayloadWithoutFence_IsNotReboundToKb()
        {
            var registry = new McpModernSubscriptionRegistry();
            var request = new JObject
            {
                ["id"] = 2,
                ["method"] = McpModernSubscriptionProtocol.ListenMethod,
                ["params"] = new JObject
                {
                    ["notifications"] = new JObject { ["toolsListChanged"] = true }
                }
            };
            Assert.True(registry.TryOpen(request, out var subscription, out _, new OwnershipFence("client", "", 0)));
            Assert.True(subscription!.TryQueue("notifications/tools/list_changed", new JObject { ["stateless"] = true }));
        }

        [Fact]
        public void ExpiredSession_EmitsRemovalFenceEvent()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMilliseconds(1));
            string? removed = null;
            registry.SessionRemoved += id => removed = id;
            var session = registry.Create();
            Thread.Sleep(10);
            Assert.False(registry.TryGet(session.Id, out _));
            Assert.Equal(session.Id, removed);
        }

        private static JObject TaskRequest(string method, string taskId)
            => new JObject
            {
                ["id"] = 1,
                ["method"] = method,
                ["params"] = new JObject { ["taskId"] = taskId }
            };
    }
}
