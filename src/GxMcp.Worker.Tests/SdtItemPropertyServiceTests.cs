using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using GxMcp.Worker.Services.Structure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdtItemPropertyServiceTests
    {
        private sealed class Call
        {
            public string Target;
            public string Type;
            public IList<string> Path;
            public bool ForWrite;
        }

        private static SdtItemPropertyService Service(FakeSdtStore store, List<Call> calls = null)
        {
            return new SdtItemPropertyService((target, type, path, forWrite) =>
            {
                calls?.Add(new Call { Target = target, Type = type, Path = path, ForWrite = forWrite });
                return store;
            });
        }

        private static JObject Args(string json) => JObject.Parse(json);
        private static string Code(JObject o) => o["status"]?.ToString() == "ok" ? o["code"]?.ToString() : o["error"]?["code"]?.ToString();

        // ---- itemPath

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"itemPath\":null}")]
        [InlineData("{\"itemPath\":\"debug\"}")]
        [InlineData("{\"itemPath\":[]}")]
        [InlineData("{\"itemPath\":[1]}")]
        [InlineData("{\"itemPath\":[\"a\",\"  \"]}")]
        [InlineData("{\"itemPath\":[\"a\",null]}")]
        public void ItemPathMustBeANonEmptyArrayOfMemberNames(string json)
        {
            string error;
            List<string> path;
            Assert.False(SdtItemPropertyService.TryReadItemPath(Args(json), out path, out error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void ItemPathIsBoundedAndTrimmed()
        {
            string error;
            List<string> path;
            Assert.True(SdtItemPropertyService.TryReadItemPath(Args("{\"itemPath\":[\" Items \",\"Id\"]}"), out path, out error));
            Assert.Equal(new[] { "Items", "Id" }, path.ToArray());

            var tooDeep = new JObject { ["itemPath"] = new JArray(Enumerable.Repeat("m", 33)) };
            Assert.False(SdtItemPropertyService.TryReadItemPath(tooDeep, out path, out error));
            Assert.Contains("deeper", error);
        }

        [Fact]
        public void ABadItemPathIsAnInvalidItemPathRefusalForReadAndWrite()
        {
            var store = new FakeSdtStore();
            var service = Service(store);

            var read = JObject.Parse(service.GetItemProperty("S", null, Args("{\"property\":\"JsonNullSerialization\"}")));
            Assert.Equal("InvalidItemPath", Code(read));

            var write = JObject.Parse(service.SetItemProperty("S", null, Args("{\"itemPath\":\"debug\",\"property\":\"P\",\"value\":\"v\"}")));
            Assert.Equal("InvalidItemPath", Code(write));
            Assert.False(write["persisted"].Value<bool>());
            Assert.DoesNotContain(store.Log, entry => entry.StartsWith("apply"));
        }

        // ---- value text

        [Fact]
        public void ABooleanOrNumberValueIsReadAsItsText()
        {
            Assert.Equal("true", SdtItemPropertyService.ReadText(JToken.FromObject(true)));
            Assert.Equal("false", SdtItemPropertyService.ReadText(JToken.FromObject(false)));
            Assert.Equal("42", SdtItemPropertyService.ReadText(JToken.FromObject(42)));
            Assert.Equal("Empty", SdtItemPropertyService.ReadText(JToken.FromObject("Empty")));
            Assert.Null(SdtItemPropertyService.ReadText(JValue.CreateNull()));
            Assert.Null(SdtItemPropertyService.ReadText(null));
        }

        [Fact]
        public void ANumericJsonValueIsReadInTheInvariantCultureWhateverTheMachineUses()
        {
            var original = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
                Assert.Equal("1.5", SdtItemPropertyService.ReadText(JToken.FromObject(1.5)));
                Assert.Equal("1234567", SdtItemPropertyService.ReadText(JToken.FromObject(1234567)));
                Assert.Equal("-0.25", SdtItemPropertyService.ReadText(JToken.FromObject(-0.25m)));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = original; }
        }

        // ---- delegation

        [Fact]
        public void AReadAsksForAStoreWithoutABaselineAndAWriteAsksForOne()
        {
            var calls = new List<Call>();
            var store = new FakeSdtStore();
            var service = Service(store, calls);
            string item = "\"itemPath\":[\"debug\"],\"property\":\"JsonNullSerialization\"";

            service.GetItemProperty("S", "SDT", Args("{" + item + "}"));
            service.SetItemProperty("S", "SDT", Args("{" + item + ",\"value\":\"Empty\",\"dryRun\":true}"));
            service.SetItemProperty("S", "SDT", Args("{" + item + ",\"value\":\"Empty\",\"expectedVersion\":\"100:root\"}"));

            Assert.Equal(new[] { false, false, true }, calls.Select(c => c.ForWrite).ToArray());
            Assert.All(calls, c => Assert.Equal("S", c.Target));
            Assert.All(calls, c => Assert.Equal("SDT", c.Type));
            Assert.All(calls, c => Assert.Equal(new[] { "debug" }, c.Path.ToArray()));
        }

        [Fact]
        public void TheWriteGoesThroughTheEngineWithTheRequestArguments()
        {
            var store = new FakeSdtStore();
            var service = Service(store);

            var json = JObject.Parse(service.SetItemProperty("S", null, Args(
                "{\"itemPath\":[\"debug\"],\"property\":\"Json Null Serialization\",\"value\":\"Empty\",\"expectedVersion\":\"100:root\"}")));

            Assert.Equal("SdtItemPropertyUpdated", Code(json));
            Assert.Equal("idJsonEmpty", store.Target[FakeSdtStore.Native]);
        }

        [Fact]
        public void BaseVersionIsAcceptedAsTheVersionToken()
        {
            var store = new FakeSdtStore();
            var service = Service(store);

            var json = JObject.Parse(service.SetItemProperty("S", null, Args(
                "{\"itemPath\":[\"debug\"],\"property\":\"JsonNullSerialization\",\"value\":\"Empty\",\"baseVersion\":\"100:root\"}")));

            Assert.Equal("SdtItemPropertyUpdated", Code(json));
        }

        [Fact]
        public void ABooleanJsonValueWritesABooleanProperty()
        {
            var store = new FakeSdtStore();
            var service = Service(store);

            var json = JObject.Parse(service.SetItemProperty("S", null, Args(
                "{\"itemPath\":[\"debug\"],\"property\":\"Required\",\"value\":true,\"expectedVersion\":\"100:root\"}")));

            Assert.Equal("SdtItemPropertyUpdated", Code(json));
            Assert.Equal("True", store.Target["Required"]);
        }

        // ---- refusals while opening the object

        [Fact]
        public void ARefusalWhileOpeningTheObjectIsReturnedAsAPersistedFalseEnvelope()
        {
            var service = new SdtItemPropertyService((target, type, path, forWrite) =>
            {
                throw new SdtUserFacingException("NotAnSDT", "Object 'Customer' is not an SDT.", "Use genexus_properties.");
            });

            var write = JObject.Parse(service.SetItemProperty("Customer", null, Args("{\"itemPath\":[\"a\"],\"property\":\"P\",\"value\":\"v\"}")));
            Assert.Equal("NotAnSDT", Code(write));
            Assert.False(write["persisted"].Value<bool>());
            Assert.Equal("Customer", write["target"].ToString());

            var read = JObject.Parse(service.GetItemProperty("Customer", null, Args("{\"itemPath\":[\"a\"]}")));
            Assert.Equal("NotAnSDT", Code(read));
        }

        [Fact]
        public void APrebuiltEnvelopeFromTheObjectLookupIsReturnedUntouched()
        {
            string notFound = McpResponse.Err(code: "NotFound", message: "SDT 'Nope' was not found.", target: "Nope");
            var service = new SdtItemPropertyService((target, type, path, forWrite) => { throw new SdtUserFacingException(notFound); });

            Assert.Equal(notFound, service.GetItemProperty("Nope", null, Args("{\"itemPath\":[\"a\"]}")));
        }

        [Fact]
        public void AnUnexpectedFailureIsAnErrorEnvelopeNeverACrash()
        {
            var service = new SdtItemPropertyService((target, type, path, forWrite) => { throw new InvalidOperationException("boom"); });

            var read = JObject.Parse(service.GetItemProperty("S", null, Args("{\"itemPath\":[\"a\"]}")));
            Assert.Equal("SdtItemPropertyReadFailed", Code(read));
            Assert.Contains("boom", read["error"]["message"].ToString());

            var write = JObject.Parse(service.SetItemProperty("S", null, Args("{\"itemPath\":[\"a\"],\"property\":\"P\",\"value\":\"v\"}")));
            Assert.Equal("SdtItemPropertyUpdateFailed", Code(write));
            Assert.Equal(JTokenType.Null, write["persisted"].Type);
        }

        // ---- the dispatcher reaches the service

        private static CommandDispatcher DispatcherOver(SdtItemPropertyService service)
        {
            var structure = (StructureService)FormatterServices.GetUninitializedObject(typeof(StructureService));
            typeof(StructureService).GetField("_sdtItemPropertyService", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(structure, service);

            var dispatcher = (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
            typeof(CommandDispatcher).GetField("_structureService", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(dispatcher, structure);
            var tableField = typeof(CommandDispatcher).GetField("_commandTable", BindingFlags.NonPublic | BindingFlags.Instance);
            var table = (IDictionary)Activator.CreateInstance(tableField.FieldType, StringComparer.OrdinalIgnoreCase);
            var handlerType = tableField.FieldType.GetGenericArguments()[1];
            table.Add("structure", Delegate.CreateDelegate(handlerType, dispatcher,
                typeof(CommandDispatcher).GetMethod("Handle_Structure", BindingFlags.NonPublic | BindingFlags.Instance)));
            tableField.SetValue(dispatcher, table);
            return dispatcher;
        }

        private static string Dispatch(CommandDispatcher dispatcher, string action, JObject args)
        {
            var request = new JObject { ["method"] = "structure", ["action"] = action, ["target"] = "SampleResponse", ["params"] = args };
            return dispatcher.Dispatch(request.ToString());
        }

        [Fact]
        public void TheWorkerDispatchesGetSdtItemPropertyToTheService()
        {
            var dispatcher = DispatcherOver(Service(new FakeSdtStore()));

            var json = JObject.Parse(Dispatch(dispatcher, "GetSdtItemProperty",
                Args("{\"itemPath\":[\"debug\"],\"property\":\"JsonNullSerialization\"}")));

            Assert.Equal("SdtItemProperty", Code(json));
            Assert.Equal("NoProperty", json["result"]["value"].ToString());
            Assert.Equal("100:root", json["result"]["versionToken"].ToString());
        }

        [Fact]
        public void TheWorkerDispatchesSetSdtItemPropertyAndHonoursDryRunWithoutTheUnsupportedPreviewGuard()
        {
            var store = new FakeSdtStore();
            var dispatcher = DispatcherOver(Service(store));

            var preview = JObject.Parse(Dispatch(dispatcher, "SetSdtItemProperty", Args(
                "{\"itemPath\":[\"debug\"],\"property\":\"JsonNullSerialization\",\"value\":\"Empty\",\"dryRun\":true}")));
            Assert.Equal("SdtItemPropertyPreview", Code(preview));
            Assert.Empty(store.Target);

            var write = JObject.Parse(Dispatch(dispatcher, "SetSdtItemProperty", Args(
                "{\"itemPath\":[\"debug\"],\"property\":\"JsonNullSerialization\",\"value\":\"Empty\",\"expectedVersion\":\"100:root\"}")));
            Assert.Equal("SdtItemPropertyUpdated", Code(write));
            Assert.Equal("idJsonEmpty", store.Target[FakeSdtStore.Native]);
        }

        [Fact]
        public void TheTypeFilterOfTheRequestReachesTheService()
        {
            var calls = new List<Call>();
            var dispatcher = DispatcherOver(Service(new FakeSdtStore(), calls));

            Dispatch(dispatcher, "GetSdtItemProperty", Args("{\"type\":\"SDT\",\"itemPath\":[\"debug\"]}"));

            Assert.Single(calls);
            Assert.Equal("SDT", calls[0].Type);
        }
    }
}
