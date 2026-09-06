using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class McpModernSubscriptionContractTests
    {
        [Fact]
        public void TryOpen_AcknowledgesSupportedFiltersAndCanonicalUris()
        {
            var registry = new McpModernSubscriptionRegistry(maxSubscriptions: 4, queueCapacity: 2);
            var request = Request(7, new JObject
            {
                ["notifications"] = new JObject
                {
                    ["toolsListChanged"] = true,
                    ["promptsListChanged"] = true,
                    ["resourcesListChanged"] = true,
                    ["resourceSubscriptions"] = new JArray(
                        "genexus://objects/Order",
                        "genexus://objects/Customer",
                        "genexus://objects/Order")
                }
            });

            Assert.True(registry.TryOpen(request, out var subscription, out var error));
            Assert.Null(error);
            Assert.NotNull(subscription);
            Assert.True(subscription!.GrantedNotifications["toolsListChanged"]!.Value<bool>());
            Assert.True(subscription.GrantedNotifications["resourcesListChanged"]!.Value<bool>());
            Assert.Null(subscription.GrantedNotifications["promptsListChanged"]);
            Assert.Equal(
                "genexus://objects/Customer",
                subscription.GrantedNotifications["resourceSubscriptions"]![0]!.ToString());
            Assert.Equal(1, registry.Count);

            Assert.True(registry.Remove(subscription.Id, out var removed));
            Assert.Same(subscription, removed);
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void TryOpen_RejectsMissingOrMalformedFilters()
        {
            var registry = new McpModernSubscriptionRegistry();

            Assert.False(registry.TryOpen(Request(1, new JObject()), out _, out var missing));
            Assert.Equal(-32602, missing!["error"]!["code"]!.Value<int>());

            Assert.False(registry.TryOpen(Request(2, new JObject
            {
                ["notifications"] = new JObject
                {
                    ["resourceSubscriptions"] = new JArray("Customer")
                }
            }), out _, out var malformed));
            Assert.Equal(-32602, malformed!["error"]!["code"]!.Value<int>());
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void TryOpen_EnforcesCapacityAndRemoveCompletesStream()
        {
            var registry = new McpModernSubscriptionRegistry(maxSubscriptions: 1, queueCapacity: 1);
            var request = Request("first", new JObject
            {
                ["notifications"] = new JObject { ["toolsListChanged"] = true }
            });

            Assert.True(registry.TryOpen(request, out var first, out _));
            Assert.False(registry.TryOpen(Request("second", new JObject
            {
                ["notifications"] = new JObject { ["toolsListChanged"] = true }
            }), out _, out var full));
            Assert.Equal(-32025, full!["error"]!["code"]!.Value<int>());
            Assert.Equal(1, registry.Count);

            Assert.True(registry.Remove(first!.Id, out _));
            Assert.True(first.Reader.Completion.IsCompleted);
        }

        [Fact]
        public void TryQueue_FiltersByMethodAndAddsSubscriptionMetadata()
        {
            var registry = new McpModernSubscriptionRegistry();
            Assert.True(registry.TryOpen(Request(1, new JObject
            {
                ["notifications"] = new JObject
                {
                    ["toolsListChanged"] = true,
                    ["resourceSubscriptions"] = new JArray("genexus://objects/Customer")
                }
            }), out var subscription, out _));

            Assert.False(subscription!.TryQueue(
                "notifications/resources/updated",
                new JObject { ["uri"] = "genexus://objects/Order" }));
            Assert.True(subscription.TryQueue(
                "notifications/resources/updated",
                new JObject { ["uri"] = "genexus://objects/Customer", ["reason"] = "changed" }));

            Assert.True(subscription.Reader.TryRead(out var json));
            var notification = JObject.Parse(json!);
            Assert.Equal("notifications/resources/updated", notification["method"]!.ToString());
            Assert.Equal(
                subscription.Id,
                notification["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.ToString());
            Assert.False(subscription.Reader.TryRead(out _));

            Assert.True(subscription.TryQueue("notifications/tools/list_changed", new JObject()));
            Assert.True(subscription.Reader.TryRead(out _));
        }

        [Fact]
        public void TryQueue_MatchesKbQualifiedResourceIdentity()
        {
            var registry = new McpModernSubscriptionRegistry();
            Assert.True(registry.TryOpen(Request("scoped", new JObject
            {
                ["notifications"] = new JObject
                {
                    ["resourceSubscriptions"] = new JArray(
                        "genexus://kb/sales/objects/Customer")
                }
            }), out var subscription, out _));

            Assert.False(subscription!.TryQueue(
                "notifications/resources/updated",
                new JObject
                {
                    ["uri"] = "genexus://objects/Customer",
                    ["resourceUri"] = "genexus://kb/other/objects/Customer",
                    ["kbAlias"] = "other"
                }));
            Assert.True(subscription.TryQueue(
                "notifications/resources/updated",
                new JObject
                {
                    ["uri"] = "genexus://objects/Customer",
                    ["resourceUri"] = "genexus://kb/sales/objects/Customer",
                    ["kbAlias"] = "sales",
                    ["cacheRevision"] = 4
                }));

            Assert.True(subscription.Reader.TryRead(out var json));
            var notification = JObject.Parse(json!);
            Assert.Equal("sales", notification["params"]!["kbAlias"]!.ToString());
            Assert.Equal(4, notification["params"]!["cacheRevision"]!.Value<int>());
            Assert.Equal(
                subscription.Id,
                notification["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.ToString());
        }

        [Theory]
        [InlineData("sales", "genexus://objects/Customer", "genexus://kb/sales/objects/Customer")]
        [InlineData("sales-team", "genexus://kb/health", "genexus://kb/sales-team/kb/health")]
        [InlineData("skills", "genexus://kb/skills/navigation", "genexus://kb/skills/kb/skills/navigation")]
        public void BuildScopedResourceUri_QualifiesLegacyUri(string kbAlias, string uri, string expected)
        {
            Assert.Equal(expected, Program.BuildScopedResourceUri(kbAlias, uri));
        }

        [Fact]
        public void TryQueue_UsesDropOldestBoundedPolicy()
        {
            var registry = new McpModernSubscriptionRegistry(maxSubscriptions: 1, queueCapacity: 2);
            Assert.True(registry.TryOpen(Request(1, new JObject
            {
                ["notifications"] = new JObject { ["toolsListChanged"] = true }
            }), out var subscription, out _));

            Assert.True(subscription!.TryQueue("notifications/tools/list_changed", new JObject { ["n"] = 1 }));
            Assert.True(subscription.TryQueue("notifications/tools/list_changed", new JObject { ["n"] = 2 }));
            Assert.True(subscription.TryQueue("notifications/tools/list_changed", new JObject { ["n"] = 3 }));

            Assert.True(subscription.Reader.TryRead(out var first));
            Assert.True(subscription.Reader.TryRead(out var second));
            Assert.Equal(2, JObject.Parse(first!)["params"]!["n"]!.Value<int>());
            Assert.Equal(3, JObject.Parse(second!)["params"]!["n"]!.Value<int>());
        }

        private static JObject Request(object id, JObject parameters)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JToken.FromObject(id),
                ["method"] = McpModernSubscriptionProtocol.ListenMethod,
                ["params"] = parameters
            };
    }
}
