using System;
using System.Reflection;
using GxMcp.Gateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ResponsePipelinePerfRegressionTests
    {
        private static readonly MethodInfo TruncateMethod = typeof(Program).GetMethod(
            "TruncateResponseIfNeeded",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        private static JToken InvokeTruncate(JToken? token, string toolName)
            => (JToken)TruncateMethod.Invoke(null, new object?[] { token, toolName })!;

        [Fact]
        public void MeasureSerializedCharCount_MatchesToStringLength_AcrossTokenShapes()
        {
            JToken[] samples =
            {
                new JObject { ["name"] = "Cliente", ["unicode"] = "São Paulo — ação \"ok\"\n\\path", ["count"] = 42, ["flag"] = true, ["nil"] = JValue.CreateNull() },
                new JArray(1, "two", new JObject { ["nested"] = new JArray("a", "b") }, JValue.CreateNull()),
                new JValue("plain string with \"quotes\" and \t tabs"),
                new JValue(123456),
                JValue.CreateNull()
            };

            foreach (var sample in samples)
            {
                int expected = sample.ToString(Formatting.None).Length;
                int actual = McpRouter.MeasureSerializedCharCount(sample);
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void TruncateResponseIfNeeded_OverBudgetResults_TrimsToBudgetPrefixAndSetsContractFields()
        {
            var rows = new JArray();
            for (int i = 0; i < 400; i++)
            {
                rows.Add(new JObject
                {
                    ["name"] = $"Procedure{i:D4}",
                    ["type"] = "Procedure",
                    ["description"] = new string('d', 240)
                });
            }

            var payload = new JObject
            {
                ["count"] = 400,
                ["total"] = 400,
                ["nextOffset"] = 400,
                ["results"] = rows
            };

            var truncated = Assert.IsType<JObject>(InvokeTruncate(payload, "genexus_query"));

            Assert.True(truncated["isTruncated"]?.Value<bool>());
            Assert.Equal(400, truncated["originalCount"]?.Value<int>());
            int kept = truncated["returnedCount"]!.Value<int>();
            Assert.InRange(kept, 1, 399);
            Assert.Equal(kept, truncated["returned"]?.Value<int>());
            Assert.Equal(kept, ((JArray)truncated["results"]!).Count);
            Assert.True(truncated["hasMore"]?.Value<bool>());
            Assert.Null(truncated["nextOffset"]);
            Assert.True(truncated.ToString(Formatting.None).Length <= 80000);
            Assert.Equal("Procedure0000", truncated["results"]![0]!["name"]?.ToString());
        }

        [Fact]
        public void TruncateResponseIfNeeded_UnderBudgetResults_ReturnsOriginalWithoutTruncation()
        {
            var rows = new JArray();
            for (int i = 0; i < 20; i++)
            {
                rows.Add(new JObject { ["name"] = $"Obj{i}", ["type"] = "Procedure" });
            }

            var payload = new JObject { ["results"] = rows };
            var output = Assert.IsType<JObject>(InvokeTruncate(payload, "genexus_query"));

            Assert.Same(payload, output);
            Assert.Null(output["isTruncated"]);
            Assert.Equal(20, ((JArray)output["results"]!).Count);
        }

        [Fact]
        public void StripNulls_RemovesAllNullsInPlaceAcrossNestedObjectsAndArrays()
        {
            var payload = new JObject
            {
                ["keepString"] = "",
                ["keepZero"] = 0,
                ["keepFalse"] = false,
                ["dropTop"] = JValue.CreateNull(),
                ["nested"] = new JObject
                {
                    ["dropNested"] = JValue.CreateNull(),
                    ["keepNested"] = "ok"
                },
                ["items"] = new JArray
                {
                    new JObject { ["a"] = 1, ["dropItem"] = JValue.CreateNull() },
                    new JObject { ["dropOnly"] = JValue.CreateNull() },
                    new JValue("scalar")
                }
            };

            McpRouter.StripNulls(payload);

            Assert.Null(payload["dropTop"]);
            Assert.Equal("", payload["keepString"]?.ToString());
            Assert.Equal(0, payload["keepZero"]?.Value<int>());
            Assert.False(payload["keepFalse"]?.Value<bool>());
            Assert.Null(payload["nested"]?["dropNested"]);
            Assert.Equal("ok", payload["nested"]?["keepNested"]?.ToString());
            var items = Assert.IsType<JArray>(payload["items"]);
            Assert.Equal(3, items.Count);
            Assert.Null(items[0]!["dropItem"]);
            Assert.Equal(1, items[0]!["a"]?.Value<int>());
            Assert.Empty(((JObject)items[1]).Properties());
            Assert.Equal("scalar", items[2].ToString());
        }

        [Fact]
        public void SemanticCacheStore_SweepExpiredAndLruEviction_RespectClockAndByteCap()
        {
            long now = 1000;
            var store = new SemanticCacheStore(maxEntries: 3, ttl: TimeSpan.FromMilliseconds(500), () => now, maxBytes: 10000);

            store.Set("k1", new JObject { ["v"] = 1 });
            now = 1200;
            store.Set("k2", new JObject { ["v"] = 2 });
            now = 1400;
            store.Set("k3", new JObject { ["v"] = 3 });

            // Touch k1 so k2 is now the least recently accessed.
            Assert.True(store.TryGet("k1", out _));
            store.Set("k4", new JObject { ["v"] = 4 });
            Assert.False(store.TryGet("k2", out _));
            Assert.True(store.TryGet("k1", out _));
            Assert.True(store.TryGet("k3", out _));
            Assert.True(store.TryGet("k4", out _));

            // Advance past k1's expiry (created at 1000, ttl 500 -> expires at 1500), but before k3/k4 (1400 -> 1900).
            now = 1600;
            store.Set("k5", new JObject { ["v"] = 5 });
            Assert.False(store.TryGet("k1", out _));
            Assert.True(store.TryGet("k3", out _));
            Assert.True(store.TryGet("k4", out _));
            Assert.True(store.TryGet("k5", out _));
        }
    }
}
