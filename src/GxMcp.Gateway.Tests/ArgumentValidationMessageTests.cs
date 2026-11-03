using System.Threading.Tasks;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issues #655 and #652: an argument validation failure must name the field, the
    /// value that was received and the accepted values/type in <c>error.message</c>
    /// itself (clients that surface only <c>message</c> used to show boilerplate), and
    /// <c>genexus_search_source</c> must accept <c>objectName</c> as a list of names.
    /// </summary>
    public class ArgumentValidationMessageTests
    {
        private static async Task<JObject> CallError(string tool, string argsJson)
        {
            GatewayArgsValidator.ClearCache();
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "arg-msg",
                ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = tool, ["arguments"] = JObject.Parse(argsJson) }
            };
            var response = await Program.ProcessMcpRequest(request);
            string text = response!["result"]!["content"]![0]!["text"]!.ToString();
            var payload = JObject.Parse(text);
            Assert.Equal("InvalidArgs", payload["error"]!["code"]!.ToString());
            return (JObject)payload["error"]!;
        }

        [Fact]
        public async Task Edit_ValidateNone_MessageNamesFieldReceivedValueAndAllowedValues()
        {
            var error = await CallError("genexus_edit",
                "{\"name\":\"P\",\"type\":\"Procedure\",\"mode\":\"patch\",\"patch\":{\"find\":\"a\",\"replace\":\"\"},\"validate\":\"none\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'validate'", message);
            Assert.Contains("none", message);
            Assert.Contains("strict", message);
            Assert.Contains("best-effort", message);
            Assert.Contains("only", message);
        }

        [Fact]
        public async Task Lifecycle_BuildWithModeBuild_MessageNamesModeReceivedValueAndAllowedValues()
        {
            var error = await CallError("genexus_lifecycle",
                "{\"action\":\"build\",\"name\":\"P\",\"mode\":\"build\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'mode'", message);
            Assert.Contains("build", message);
            Assert.Contains("compile_check", message);
            // Every violation is reported, not just the first one.
            Assert.Contains("'name'", message);
        }

        [Fact]
        public async Task Structure_ActionRead_MessageNamesActionReceivedValueAndAllowedActions()
        {
            var error = await CallError("genexus_structure", "{\"action\":\"read\",\"name\":\"T\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'action'", message);
            Assert.Contains("read", message);
            Assert.Contains("get_visual", message);
        }

        [Fact]
        public async Task Properties_ActionRead_MessageNamesActionReceivedValueAndAllowedActions()
        {
            var error = await CallError("genexus_properties", "{\"action\":\"read\",\"name\":\"T\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'action'", message);
            Assert.Contains("read", message);
            Assert.Contains("get", message);
            Assert.Contains("set", message);
            Assert.Contains("move", message);
        }

        [Fact]
        public async Task MissingRequiredArgument_MessageNamesFieldAndExpectedType()
        {
            var error = await CallError("genexus_structure", "{\"action\":\"get_visual\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'name'", message);
            Assert.Contains("missing", message);
            Assert.Contains("string", message);
        }

        [Fact]
        public async Task UnknownArgument_MessageListsAcceptedArguments()
        {
            var error = await CallError("genexus_search_source", "{\"query\":\"x\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'query'", message);
            Assert.Contains("pattern", message);
            Assert.Contains("objectName", message);
        }

        [Fact]
        public async Task WrongType_MessageNamesFieldReceivedValueAndExpectedType()
        {
            var error = await CallError("genexus_search_source", "{\"pattern\":\"x\",\"maxResults\":\"ten\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'maxResults'", message);
            Assert.Contains("integer", message);
            Assert.Contains("ten", message);
        }

        [Fact]
        public void SearchSource_ObjectNameArray_IsAcceptedAndJoinedAsCommaSeparatedNames()
        {
            GatewayArgsValidator.ClearCache();
            var args = new JObject
            {
                ["pattern"] = "SoloSi",
                ["objectName"] = new JArray("TareaEjecutar", "TareaEstado")
            };

            GatewayArgsValidator.NormalizeStringListArguments("genexus_search_source", args);

            Assert.Equal("TareaEjecutar,TareaEstado", args["objectName"]!.ToString());
            Assert.True(GatewayArgsValidator.Validate("genexus_search_source", args).Ok);
            var routed = JObject.FromObject(new SearchRouter().ConvertToolCall("genexus_search_source", args)!);
            Assert.Equal("TareaEjecutar,TareaEstado", routed["objectName"]!.ToString());
        }

        [Fact]
        public async Task SearchSource_ObjectNameArray_PassesGatewayValidationAndReachesWorkerDispatch()
        {
            GatewayArgsValidator.ClearCache();
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "arg-msg-array",
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_search_source",
                    ["arguments"] = JObject.Parse("{\"pattern\":\"SoloSi\",\"objectName\":[\"TareaEjecutar\"]}")
                }
            };

            // Validation passed: dispatch proceeds to the worker pool, which this unit
            // harness never initialises. An InvalidArgs envelope would be returned instead.
            await Assert.ThrowsAsync<System.InvalidOperationException>(
                () => Program.ProcessMcpRequest(request));
        }

        [Fact]
        public async Task SearchSource_ObjectNameArrayOfNonStrings_IsRejectedWithFieldTypeAndReceivedValue()
        {
            var error = await CallError("genexus_search_source", "{\"pattern\":\"x\",\"objectName\":[1,2]}");

            string message = error["message"]!.ToString();
            Assert.Contains("'objectName'", message);
            Assert.Contains("string", message);
            Assert.Contains("array", message);
            Assert.Contains("[1,2]", message);
        }

        [Fact]
        public async Task Lifecycle_ValidateWithCodeBoolean_MessageSaysWhatCodeIsAndWhatItAccepts()
        {
            // #830 item 6a: code is source text for action=validate; a boolean flag is the
            // common slip. The message must say what the field carries, not only its type.
            var error = await CallError("genexus_lifecycle",
                "{\"action\":\"validate\",\"code\":true,\"target\":\"P\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'code'", message);
            Assert.Contains("true", message);
            Assert.Contains("boolean", message);
            Assert.Contains("expected string", message);
            Assert.Contains("in-memory code", message);
        }

        [Fact]
        public async Task Lifecycle_ValidateWithVerification_IsRefusedAndNamesReconcile()
        {
            // #830 item 6a: verification is read only by action=reconcile. Accepting it on
            // validate would ignore it silently, so the refusal names the action it belongs to.
            var error = await CallError("genexus_lifecycle",
                "{\"action\":\"validate\",\"target\":\"P\",\"verification\":\"strict\"}");

            string message = error["message"]!.ToString();
            Assert.Contains("'verification'", message);
            Assert.Contains("\"strict\"", message);
            Assert.Contains("action=reconcile", message);
        }

        [Fact]
        public void Lifecycle_ReconcileWithVerification_PassesValidation()
        {
            GatewayArgsValidator.ClearCache();
            var args = JObject.Parse("{\"action\":\"reconcile\",\"target\":\"op:1\",\"confirmed\":true,\"verification\":\"checked the KB\"}");

            Assert.True(GatewayArgsValidator.Validate("genexus_lifecycle", args).Ok);
        }

        [Fact]
        public void Lifecycle_ValidateWithCodeString_PassesValidation()
        {
            GatewayArgsValidator.ClearCache();
            var args = JObject.Parse("{\"action\":\"validate\",\"target\":\"P\",\"code\":\"Procedure P\\n\"}");

            Assert.True(GatewayArgsValidator.Validate("genexus_lifecycle", args).Ok);
        }

        [Fact]
        public void ObjectNameArray_OnAnotherTool_IsNotRewritten()
        {
            var args = new JObject { ["objectName"] = new JArray("A", "B") };

            GatewayArgsValidator.NormalizeStringListArguments("genexus_read", args);

            Assert.Equal(JTokenType.Array, args["objectName"]!.Type);
        }
    }
}
