using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // An error envelope over the 80k budget keeps its verdict (status, error object, scalars and write receipt) and
    // shortens its lists. A success over the budget keeps the budget phrase, and a small envelope is returned as it was.
    // The dispatch cuts the envelope before the terse projection, so both steps run here in that order.
    public class ErrorEnvelopeBudgetTests
    {
        private static JObject Truncate(JObject payload, string toolName)
        {
            var method = typeof(Program).GetMethod("TruncateResponseIfNeeded", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return Assert.IsType<JObject>(method!.Invoke(null, new object?[] { payload, toolName }));
        }

        private static string? CodeOf(JObject envelope)
            => envelope["error"] is JObject error ? (string?)error["code"] : null;

        private static JObject Refusal(int items, int itemChars, bool error = true)
        {
            var lists = new JArray();
            for (int i = 0; i < items; i++)
                lists.Add(new JObject { ["name"] = $"SynthObject{i:D3}", ["details"] = new string('x', itemChars) });

            var payload = new JObject
            {
                ["status"] = error ? "error" : "ok",
                ["target"] = "SynthProc",
                ["persisted"] = false,
                ["rollbackVerified"] = true,
                ["unexpectedChangedObjectDetails"] = lists
            };
            if (error)
                payload["error"] = new JObject { ["code"] = "SyntheticRefusal", ["message"] = "Synthetic refusal.", ["hint"] = "Synthetic hint." };
            return payload;
        }

        [Fact]
        public void ErrorOverBudget_KeepsTheVerdictAndShortensTheLists()
        {
            JObject truncated = Truncate(Refusal(items: 12, itemChars: 7000), "genexus_edit");

            Assert.Equal("error", (string?)truncated["status"]);
            Assert.Equal("SyntheticRefusal", CodeOf(truncated));
            Assert.Equal(false, (bool?)truncated["persisted"]);
            Assert.Equal(true, (bool?)truncated["rollbackVerified"]);
            int count = Assert.IsType<JArray>(truncated["unexpectedChangedObjectDetails"]).Count;
            Assert.InRange(count, 1, 11);
            Assert.Equal(true, (bool?)truncated["isTruncated"]);
            Assert.True(truncated.ToString(Formatting.None).Length <= 80000);
        }

        [Fact]
        public void ErrorOverBudget_TerseProjectionKeepsTheCodeReceiptAndTheCutMarker()
        {
            JObject truncated = Truncate(Refusal(items: 12, itemChars: 7000), "genexus_edit");

            JObject trimmed = McpRouter.TrimErrorEnvelope(truncated, verbose: false);

            Assert.Equal("SyntheticRefusal", (string?)trimmed["code"]);
            Assert.Equal("Synthetic hint.", (string?)trimmed["hint"]);
            Assert.Equal(false, (bool?)trimmed["persisted"]);
            Assert.Equal(true, (bool?)trimmed["rollbackVerified"]);
            Assert.Equal(true, (bool?)trimmed["isTruncated"]);
            Assert.False(string.IsNullOrEmpty((string?)trimmed["gatewayTruncationHint"]));
            Assert.NotEmpty(Assert.IsType<JArray>(trimmed["unexpectedChangedObjectDetails"]));
        }

        [Fact]
        public void ErrorOverBudgetFromText_KeepsTheVerdictAndCutsTheText()
        {
            JObject payload = Refusal(items: 0, itemChars: 0);
            payload["persistedSnippet"] = new string('y', 90000);

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Equal("SyntheticRefusal", CodeOf(truncated));
            Assert.Equal(false, (bool?)truncated["persisted"]);
            Assert.Equal(true, (bool?)truncated["isTruncated"]);
            Assert.True(truncated.ToString(Formatting.None).Length <= 80000);
        }

        [Fact]
        public void SuccessOverBudget_KeepsTheBudgetPhrase()
        {
            JObject truncated = Truncate(Refusal(items: 12, itemChars: 7000, error: false), "genexus_edit");

            Assert.StartsWith("Response exceeded 80k token budget", (string?)truncated["error"]);
            Assert.Null(truncated["persisted"]);
        }

        [Fact]
        public void SmallErrorEnvelope_IsReturnedAsItWas()
        {
            JObject payload = Refusal(items: 3, itemChars: 100);

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Equal(payload.ToString(Formatting.None), truncated.ToString(Formatting.None));
        }
    }
}
