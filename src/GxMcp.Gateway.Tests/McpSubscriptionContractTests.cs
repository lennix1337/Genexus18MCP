using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class McpSubscriptionContractTests
    {
        [Fact]
        public void SubscribeAndUnsubscribe_AreIdempotentAndSessionScoped()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var owner = registry.Create();
            var other = registry.Create();
            string uri = "genexus://objects/Customer";

            var first = McpSubscriptionProtocol.Handle(Request("resources/subscribe", 1, uri), owner.Id, registry);
            Assert.True(first!["result"]!["subscribed"]!.Value<bool>());
            Assert.True(first["result"]!["changed"]!.Value<bool>());
            Assert.True(owner.IsSubscribedToResource(uri));
            Assert.False(other.IsSubscribedToResource(uri));

            var duplicate = McpSubscriptionProtocol.Handle(Request("resources/subscribe", 2, uri), owner.Id, registry);
            Assert.False(duplicate!["result"]!["changed"]!.Value<bool>());

            var removed = McpSubscriptionProtocol.Handle(Request("resources/unsubscribe", 3, uri), owner.Id, registry);
            Assert.False(removed!["result"]!["subscribed"]!.Value<bool>());
            Assert.True(removed["result"]!["changed"]!.Value<bool>());
            Assert.False(owner.IsSubscribedToResource(uri));

            var duplicateRemove = McpSubscriptionProtocol.Handle(Request("resources/unsubscribe", 4, uri), owner.Id, registry);
            Assert.False(duplicateRemove!["result"]!["changed"]!.Value<bool>());
        }

        [Fact]
        public void SubscriptionRequiresAbsoluteUriAndKnownSession()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var malformed = McpSubscriptionProtocol.Handle(Request("resources/subscribe", 1, "Customer"), "missing", registry);
            Assert.Equal(-32602, malformed!["error"]!["code"]!.Value<int>());

            var missingSession = McpSubscriptionProtocol.Handle(
                Request("resources/subscribe", 2, "genexus://objects/Customer"), "missing", registry);
            Assert.Equal(-32001, missingSession!["error"]!["code"]!.Value<int>());
        }

        [Fact]
        public void ModernSessionlessTransportRejectsLegacySubscriptionMethods()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var request = Request("resources/subscribe", 1, "genexus://objects/Customer");
            request["params"]!["_meta"] = new JObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = McpRouter.ModernProtocolVersion
            };

            var response = McpSubscriptionProtocol.Handle(request, "http-modern", registry);

            Assert.Equal(-32024, response!["error"]!["code"]!.Value<int>());
            Assert.Empty(registry.ActiveSessions);
        }

        [Fact]
        public void StdioRemainsAProcessScopedSession()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var request = Request("resources/subscribe", 1, "genexus://kb/capabilities");

            var response = McpSubscriptionProtocol.Handle(request, "stdio", registry);

            Assert.True(response!["result"]!["subscribed"]!.Value<bool>());
            Assert.True(response["result"]!["changed"]!.Value<bool>());
            var duplicate = McpSubscriptionProtocol.Handle(Request("resources/subscribe", 2, "genexus://kb/capabilities"), "stdio", registry);
            Assert.False(duplicate!["result"]!["changed"]!.Value<bool>());
        }

        [Fact]
        public void ResourceUpdateDeliveryRequiresExactSubscription()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var session = registry.Create();

            Assert.False(McpSubscriptionProtocol.IsSubscribed(session, "genexus://objects/Customer"));
            session.SubscribeResource("genexus://objects/Customer");
            Assert.True(McpSubscriptionProtocol.IsSubscribed(session, "genexus://objects/Customer"));
            Assert.False(McpSubscriptionProtocol.IsSubscribed(session, "genexus://objects/Order"));
        }

        private static JObject Request(string method, object id, string uri)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JToken.FromObject(id),
                ["method"] = method,
                ["params"] = new JObject { ["uri"] = uri }
            };
    }
}
