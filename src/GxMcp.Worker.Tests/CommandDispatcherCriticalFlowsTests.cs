using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Runtime.Serialization;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class CommandDispatcherCriticalFlowsTests
    {
        [Fact]
        public void IsThreadSafe_ControlCancel_True_ButOtherControlFalse()
        {
            var dispatcher = CreateDispatcherWithoutCtor();

            string cancel = @"{""method"":""control"",""action"":""Cancel""}";
            string notCancel = @"{""method"":""control"",""action"":""Pause""}";

            Assert.True(dispatcher.IsThreadSafe(cancel));
            Assert.False(dispatcher.IsThreadSafe(notCancel));
        }

        [Fact]
        public void IsThreadSafe_KbIndexReads_True_AndMalformedOrUnknown_False()
        {
            var dispatcher = CreateDispatcherWithoutCtor();

            string getStatus = @"{""method"":""kb"",""action"":""GetIndexStatus""}";
            string getState = @"{""method"":""kb"",""action"":""GetIndexState""}";
            string unknown = @"{""method"":""kb"",""action"":""Open""}";
            string malformed = @"{""method"":""kb"",";

            Assert.True(dispatcher.IsThreadSafe(getStatus));
            Assert.True(dispatcher.IsThreadSafe(getState));
            Assert.False(dispatcher.IsThreadSafe(unknown));
            Assert.False(dispatcher.IsThreadSafe(malformed));
        }

        [Fact]
        public void IsThreadSafe_SearchSource_False_ButPlainSearchQuery_True()
        {
            // issue #26 P2: 'search' method covers both index-only Query (module=Search,
            // action=Query) and SDK-touching SearchSource (module=Search, action=SearchSource).
            // Only SearchSource must be forced onto the STA thread.
            var dispatcher = CreateDispatcherWithoutCtor();

            string searchSource = @"{""method"":""search"",""action"":""SearchSource""}";
            string plainQuery = @"{""method"":""search"",""action"":""Query""}";
            string ping = @"{""method"":""ping""}";

            Assert.False(dispatcher.IsThreadSafe(searchSource));
            Assert.True(dispatcher.IsThreadSafe(plainQuery));
            Assert.True(dispatcher.IsThreadSafe(ping));
        }

        [Fact]
        public void AppendInlineReadsCore_SearchSource_DedupesByObjectName()
        {
            string response = new JObject
            {
                ["hits"] = new JArray
                {
                    new JObject { ["objectName"] = "ProcA", ["type"] = "Procedure" },
                    new JObject { ["objectName"] = "ProcA", ["type"] = "Procedure" },
                    new JObject { ["objectName"] = "ProcB", ["type"] = "Procedure" }
                }
            }.ToString();

            string merged = CommandDispatcher.AppendInlineReadsCore(
                response,
                n: 3,
                reader: (name, type) => new JObject { ["name"] = name, ["kind"] = type }.ToString(),
                arrayKey: "hits",
                nameField: "objectName",
                dedupe: true);

            var json = JObject.Parse(merged);
            var inlineReads = Assert.IsType<JArray>(json["inline_reads"]);
            Assert.Equal(2, inlineReads.Count);

            var names = inlineReads
                .OfType<JObject>()
                .Select(x => x["name"]?.ToString())
                .ToArray();

            Assert.Equal(new[] { "ProcA", "ProcB" }, names);
        }

        private static CommandDispatcher CreateDispatcherWithoutCtor()
            => (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));

        // issue #957: the index state is what the gateway mirrors into whoami and its
        // IndexNotReady gate, so it must carry the recorded KB open failure.
        [Fact]
        public void HandleKbIndexState_ReportsTheRecordedKbOpenFailure()
        {
            var cache = new IndexCacheService();
            var kb = new KbService(cache);
            var dispatcher = CreateDispatcherWithoutCtor();
            SetField(dispatcher, "_indexCacheService", cache);
            SetField(dispatcher, "_kbService", kb);

            var before = JObject.Parse(dispatcher.HandleKbIndexState(new JObject(), "kb", "GetIndexState", null, null, new JObject()));
            Assert.Equal(JTokenType.Null, before["result"]?["kbOpenFailure"]?.Type);

            kb.RecordOpenFailure("C:\\KBs\\x", "KbOpenFailed", "InvalidDirectory");
            var after = JObject.Parse(dispatcher.HandleKbIndexState(new JObject(), "kb", "GetIndexState", null, null, new JObject()));

            JObject failure = after["result"]?["kbOpenFailure"] as JObject;
            Assert.NotNull(failure);
            Assert.Equal("KbOpenFailed", failure["code"]?.ToString());
            Assert.Equal("InvalidDirectory", failure["message"]?.ToString());
        }

        private static void SetField(object target, string name, object value)
            => typeof(CommandDispatcher).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(target, value);

        [Fact]
        public void HandleKbNameTypeMap_WithoutKb_ReturnsEmptyMap()
        {
            // Extracted verbatim from Handle_Kb: with no index service both
            // best-effort scans yield an empty map instead of throwing.
            var dispatcher = CreateDispatcherWithoutCtor();

            string json = dispatcher.HandleKbNameTypeMap(
                new JObject(), "kb", "GetNameTypeMap", null, null, new JObject());

            var parsed = JObject.Parse(json);
            Assert.Equal("NameTypeMap", parsed["code"]?.ToString());
            Assert.Equal(0, parsed["result"]?["totalNames"]?.ToObject<int?>() ?? -1);
            Assert.Equal(0, ((JObject)parsed["result"]?["nameTypeMap"])?.Count ?? -1);
        }
    }
}
