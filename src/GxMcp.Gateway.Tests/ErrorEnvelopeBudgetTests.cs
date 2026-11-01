using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // An error envelope over the 80k budget keeps its verdict (status, error code, message and hint, and the write receipt)
    // and shortens its long lists and text. A success over the budget keeps the budget phrase, and a small envelope is
    // returned as it was. The dispatch cuts the envelope before the terse projection, so both steps run here in that order.
    public class ErrorEnvelopeBudgetTests
    {
        private const int Budget = 80000;

        private static JObject Truncate(JObject payload, string toolName)
        {
            var method = typeof(Program).GetMethod("TruncateResponseIfNeeded", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return Assert.IsType<JObject>(method!.Invoke(null, new object?[] { payload, toolName }));
        }

        private static string? CodeOf(JObject envelope)
            => envelope["error"] is JObject error ? (string?)error["code"] : null;

        private static int SizeOf(JObject payload) => payload.ToString(Formatting.None).Length;

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
        public void ErrorOverBudget_KeepsTheVerdictAndHalvesTheList()
        {
            JObject truncated = Truncate(Refusal(items: 12, itemChars: 7000), "genexus_edit");

            Assert.Equal("error", (string?)truncated["status"]);
            Assert.Equal("SyntheticRefusal", CodeOf(truncated));
            Assert.Equal("Synthetic refusal.", (string?)truncated["error"]?["message"]);
            Assert.Equal(false, (bool?)truncated["persisted"]);
            Assert.Equal(true, (bool?)truncated["rollbackVerified"]);
            var kept = Assert.IsType<JArray>(truncated["unexpectedChangedObjectDetails"]);
            Assert.Equal(6, kept.Count);
            Assert.Equal("SynthObject000", (string?)kept[0]?["name"]);
            Assert.Null(truncated["isTruncated"]);
            Assert.Contains("error.message", (string?)truncated["gatewayTruncationHint"] ?? "");
            Assert.True(SizeOf(truncated) <= Budget);
        }

        [Fact]
        public void ErrorOverBudget_TerseProjectionKeepsTheCodeReceiptAndTheHint()
        {
            JObject truncated = Truncate(Refusal(items: 12, itemChars: 7000), "genexus_edit");

            JObject trimmed = McpRouter.TrimErrorEnvelope(truncated, verbose: false);

            Assert.Equal("SyntheticRefusal", (string?)trimmed["code"]);
            Assert.Equal("Synthetic hint.", (string?)trimmed["hint"]);
            Assert.Equal(false, (bool?)trimmed["persisted"]);
            Assert.Equal(true, (bool?)trimmed["rollbackVerified"]);
            Assert.False(string.IsNullOrEmpty((string?)trimmed["gatewayTruncationHint"]));
            Assert.NotEmpty(Assert.IsType<JArray>(trimmed["unexpectedChangedObjectDetails"]));
        }

        [Fact]
        public void TextOverflow_CutsTheTextAndKeepsTheSmallLists()
        {
            JObject payload = Refusal(items: 0, itemChars: 0);
            payload["blockers"] = new JArray(Enumerable.Range(0, 11).Select(i => (JToken)$"SynthBlocker{i}"));
            payload["propertiesNotRestored"] = new JArray("SynthPropA", "SynthPropB", "SynthPropC");
            payload["saveFailureMessage"] = new string('y', 90000);
            ((JObject)payload["error"]!)["message"] = "Synthetic first line " + new string('m', 4000);

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Equal(11, ((JArray)truncated["blockers"]!).Count);
            Assert.Equal(3, ((JArray)truncated["propertiesNotRestored"]!).Count);
            Assert.Equal(2003, ((string?)truncated["saveFailureMessage"])!.Length);
            Assert.Equal(4021, ((string?)truncated["error"]!["message"])!.Length);
            Assert.True(SizeOf(truncated) <= Budget);
        }

        [Fact]
        public void ReceiptListsAreShortenedLast()
        {
            JObject payload = Refusal(items: 0, itemChars: 0);
            payload["blockers"] = new JArray(new string('b', 5000), new string('c', 5000), new string('d', 5000));
            payload["propertiesNotRestored"] = new JArray(new string('p', 17000), new string('q', 17000), new string('r', 17000), new string('s', 17000));

            JObject truncated = Truncate(payload, "genexus_edit");

            var receipt = Assert.IsType<JArray>(truncated["propertiesNotRestored"]);
            Assert.Equal(4, receipt.Count);
            Assert.All(receipt, item => Assert.Equal(17000, ((string?)item)!.Length));
            Assert.Single((JArray)truncated["blockers"]!);
            Assert.True(SizeOf(truncated) <= Budget);
        }

        [Fact]
        public void ListWithOneHugeItem_IsCutAndTheListIsKept()
        {
            JObject payload = Refusal(items: 0, itemChars: 0);
            payload["sdkMessages"] = new JArray(new string('z', 90000));
            payload["blockers"] = new JArray(Enumerable.Range(0, 11).Select(i => (JToken)$"SynthBlocker{i}"));

            JObject truncated = Truncate(payload, "genexus_edit");

            var messages = Assert.IsType<JArray>(truncated["sdkMessages"]);
            Assert.Single(messages);
            Assert.Equal(2003, ((string?)messages[0])!.Length);
            Assert.Equal(11, ((JArray)truncated["blockers"]!).Count);
            Assert.True(SizeOf(truncated) <= Budget);
        }

        [Fact]
        public void MassInAnObjectMap_FallsBackToTheVerdict()
        {
            JObject payload = Refusal(items: 3, itemChars: 100);
            var map = new JObject();
            for (int i = 0; i < 1000; i++) map[$"SynthObject{i:D4}"] = new string('v', 100);
            payload["expectedVersions"] = map;

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Null(truncated["expectedVersions"]);
            Assert.Null(truncated["unexpectedChangedObjectDetails"]);
            Assert.Equal("SyntheticRefusal", CodeOf(truncated));
            Assert.Equal(false, (bool?)truncated["persisted"]);
            Assert.Contains("reduced this error to its verdict", (string?)truncated["gatewayTruncationHint"] ?? "");
            Assert.True(SizeOf(truncated) <= Budget);
        }

        [Fact]
        public void ManySmallArrays_StayWithinTheBudget()
        {
            JObject payload = Refusal(items: 0, itemChars: 0);
            var holder = new JObject();
            for (int i = 0; i < 600; i++) holder["p" + i] = new JArray(new string('a', 150), new string('b', 150));
            payload["nested"] = new JArray(holder);

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Equal("SyntheticRefusal", CodeOf(truncated));
            Assert.True(SizeOf(truncated) <= Budget);
        }

        [Fact]
        public void MarkerRoom_KeepsTheListHalvingThatLeavesRoomForTheMarker()
        {
            JObject payload = Refusal(items: 0, itemChars: 0);
            payload["L"] = new JArray(Enumerable.Range(0, 198).Select(i => (JToken)new string('x', 800)));

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Equal(49, ((JArray)truncated["L"]!).Count);
            Assert.True(SizeOf(truncated) <= Budget);
        }

        [Fact]
        public void ErrorBetweenTheSoftBudgetAndTheCut_IsReturnedUntouched()
        {
            JObject payload = Refusal(items: 0, itemChars: 0);
            var lists = new JArray();
            for (int i = 0; i < 12; i++) lists.Add(new JObject { ["name"] = $"SynthObject{i:D3}", ["note"] = new string('x', 5500) });
            payload["unexpectedChangedObjectDetails"] = lists;
            string before = payload.ToString(Formatting.None);
            Assert.InRange(before.Length, 60001, Budget);

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Null(truncated["gatewayTruncationHint"]);
            Assert.Null(truncated["isTruncated"]);
            Assert.Equal(before, truncated.ToString(Formatting.None));
        }

        [Fact]
        public void EventsRefusalShape_HalvesTheGuidsAndKeepsTheCappedDetails()
        {
            var payload = new JObject
            {
                ["status"] = "error",
                ["target"] = "SynthPanel",
                ["error"] = new JObject { ["code"] = "ObjectSaveIncomplete", ["message"] = "Synthetic: other objects changed during the save.", ["hint"] = "Synthetic hint." },
                ["writeAttempted"] = true,
                ["objectSaved"] = false,
                ["partPersisted"] = false,
                ["verified"] = false,
                ["rollbackVerified"] = true,
                ["unexpectedChangedObjects"] = new JArray(Enumerable.Range(0, 2500).Select(i => (JToken)Guid.NewGuid().ToString())),
                ["unexpectedChangedObjectDetails"] = new JArray(Enumerable.Range(0, 50).Select(i => (JToken)new JObject { ["name"] = $"SynthObject{i:D3}", ["type"] = "Procedure", ["guid"] = Guid.NewGuid().ToString() })),
                ["unexpectedChangedObjectDetailsTruncated"] = true
            };

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Equal("ObjectSaveIncomplete", CodeOf(truncated));
            Assert.Equal(true, (bool?)truncated["rollbackVerified"]);
            Assert.Equal(false, (bool?)truncated["verified"]);
            Assert.Equal(50, ((JArray)truncated["unexpectedChangedObjectDetails"]!).Count);
            int guids = ((JArray)truncated["unexpectedChangedObjects"]!).Count;
            Assert.True(guids > 0 && guids < 2500);
            Assert.True(SizeOf(truncated) <= Budget);
        }

        [Fact]
        public void ErrorListInsideTheErrorObject_IsShortenedAndTheHintSaysSo()
        {
            JObject payload = Refusal(items: 0, itemChars: 0);
            var fieldErrors = new JArray();
            for (int i = 0; i < 30; i++)
                fieldErrors.Add(new JObject { ["field"] = $"SynthField{i:D2}", ["message"] = new string('e', 3000) });
            ((JObject)payload["error"]!)["fieldErrors"] = fieldErrors;

            JObject truncated = Truncate(payload, "genexus_edit");

            Assert.Equal(15, ((JArray)truncated["error"]!["fieldErrors"]!).Count);
            Assert.Equal("Synthetic refusal.", (string?)truncated["error"]!["message"]);
            Assert.Contains("lists", (string?)truncated["gatewayTruncationHint"] ?? "");
        }

        [Fact]
        public void SuccessOverBudget_KeepsTheBudgetPhrase()
        {
            JObject truncated = Truncate(Refusal(items: 12, itemChars: 7000, error: false), "genexus_edit");

            Assert.StartsWith("Response exceeded 80k token budget", (string?)truncated["error"]);
            Assert.Null(truncated["persisted"]);
        }

        [Fact]
        public void SuccessOverBudget_TerseKeepsTheOldShape()
        {
            JObject truncated = Truncate(Refusal(items: 12, itemChars: 7000, error: false), "genexus_edit");

            JObject trimmed = McpRouter.TrimErrorEnvelope(truncated, verbose: false);

            Assert.StartsWith("Response exceeded 80k token budget", (string?)trimmed["message"]);
            Assert.Null(trimmed["isTruncated"]);
            Assert.Null(trimmed["gatewayTruncationHint"]);
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
