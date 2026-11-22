using System;
using System.Diagnostics;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    public class InjectMetaTokensTests
    {
        private readonly ITestOutputHelper _output;

        public InjectMetaTokensTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static JObject MakeToolResult(string innerJson)
        {
            return new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = innerJson } }
            };
        }

        [Fact]
        public void Injects_Tokens_Block_On_Json_Payload()
        {
            var tr = MakeToolResult("{\"status\":\"Success\"}");
            McpRouter.InjectMetaTokens(tr);
            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            Assert.NotNull(inner["_meta"]?["tokens"]);
            Assert.Equal(McpRouter.MetaTokenLimit, (int)inner["_meta"]!["tokens"]!["limit"]!);
            Assert.True((int)inner["_meta"]!["tokens"]!["used"]! > 0);
        }

        [Fact]
        public void Hint_Omitted_Below_Half_Limit()
        {
            // Perf cut: `hint` is now omitted (not null) when usage is under 50%
            // of the budget. Saves ~15 bytes per response for the common case.
            // Consumers should treat missing key the same as null.
            var tr = MakeToolResult("{\"status\":\"Success\"}");
            McpRouter.InjectMetaTokens(tr);
            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            Assert.Null(inner["_meta"]!["tokens"]!["hint"]);
        }

        [Fact]
        public void Hint_Set_When_Over_Half_Limit()
        {
            // Build a payload big enough to cross 50% of MetaTokenLimit (25000 → 12500 tokens ≈ 50000 chars).
            string padding = new string('x', 60000);
            var tr = MakeToolResult("{\"status\":\"Success\",\"payload\":\"" + padding + "\"}");
            McpRouter.InjectMetaTokens(tr);
            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            var hint = inner["_meta"]!["tokens"]!["hint"];
            Assert.NotEqual(JTokenType.Null, hint!.Type);
            Assert.Contains("token", hint.ToString().ToLower());
        }

        [Fact]
        public void Non_Json_Text_Left_Untouched()
        {
            var tr = MakeToolResult("plain text not json");
            McpRouter.InjectMetaTokens(tr);
            Assert.Equal("plain text not json", (string)tr["content"]![0]!["text"]!);
        }

        [Fact]
        public void Existing_Tokens_Block_Not_Overwritten()
        {
            var tr = MakeToolResult("{\"_meta\":{\"tokens\":{\"used\":42,\"limit\":99,\"hint\":\"sticky\"}}}");
            McpRouter.InjectMetaTokens(tr);
            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            Assert.Equal(42, (int)inner["_meta"]!["tokens"]!["used"]!);
            Assert.Equal(99, (int)inner["_meta"]!["tokens"]!["limit"]!);
            Assert.Equal("sticky", (string)inner["_meta"]!["tokens"]!["hint"]!);
        }

        [Fact]
        public void BuildToolResultContent_Default_Leaves_Text_Unmodified_And_Scope_Injects_In_Memory()
        {
            var payload = new JObject
            {
                ["status"] = "Success",
                ["source"] = new string('a', 1024)
            };

            // Without the request-loop scope, BuildToolResultContent must leave content[0].text identical to payload.
            var unscoped = Program.BuildToolResultContent((JObject)payload.DeepClone(), isError: false, toolName: "genexus_read", toolArgs: new JObject(), payloadOwned: true);
            var unscopedInner = JObject.Parse((string)unscoped["content"]![0]!["text"]!);
            Assert.Null(unscopedInner["_meta"]?["tokens"]);

            // Inside the request-loop scope, genexus_lifecycle (which declares outputSchema) gets _meta.tokens in both text and structuredContent.
            using var _ = Program.BeginInMemoryMetaTokensScope();
            var tr = Program.BuildToolResultContent(payload, isError: false, toolName: "genexus_lifecycle", toolArgs: new JObject(), payloadOwned: true);

            var inner = JObject.Parse((string)tr["content"]![0]!["text"]!);
            Assert.NotNull(inner["_meta"]?["tokens"]);
            Assert.Equal(McpRouter.MetaTokenLimit, (int)inner["_meta"]!["tokens"]!["limit"]!);
            Assert.True((int)inner["_meta"]!["tokens"]!["used"]! > 0);

            // structuredContent must match content[0].text and also carry _meta.tokens.
            Assert.NotNull(tr["structuredContent"]?["_meta"]?["tokens"]);
            Assert.Equal((int)inner["_meta"]!["tokens"]!["used"]!, (int)tr["structuredContent"]!["_meta"]!["tokens"]!["used"]!);

            // Calling InjectMetaTokens afterwards must be a no-op (must not re-parse content[0].text).
            tr["content"]![0]!["text"] = "{\"sentinel\":true}";
            McpRouter.InjectMetaTokens(tr);
            Assert.Equal("{\"sentinel\":true}", (string)tr["content"]![0]!["text"]!);
        }

        [Fact]
        public void Benchmark_BuildToolResultContent_Plus_InjectMetaTokens()
        {
            var payloadTemplate = new JObject
            {
                ["status"] = "Success",
                ["source"] = new string('x', 48_000)
            };

            using var _ = Program.BeginInMemoryMetaTokensScope();

            // Warmup
            for (int i = 0; i < 5; i++)
            {
                var w = Program.BuildToolResultContent((JObject)payloadTemplate.DeepClone(), false, "genexus_read", new JObject(), payloadOwned: true);
                McpRouter.InjectMetaTokens(w);
            }

            const int iterations = 100;
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var clone = (JObject)payloadTemplate.DeepClone();
                var tr = Program.BuildToolResultContent(clone, false, "genexus_read", new JObject(), payloadOwned: true);
                McpRouter.InjectMetaTokens(tr);
            }
            sw.Stop();
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();

            _output.WriteLine($"PERF_BENCH iterations={iterations} elapsedMs={sw.ElapsedMilliseconds} allocatedBytes={allocAfter - allocBefore}");
        }
    }
}
