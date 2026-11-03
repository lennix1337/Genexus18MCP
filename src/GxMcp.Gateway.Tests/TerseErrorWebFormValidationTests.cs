using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Issue #830: WebFormValidationFailed told the caller to "fix the listed controls/attributes" and listed none: the
    // Worker attaches the SDK validator's diagnostics, and the terse projection dropped them.
    public class TerseErrorWebFormValidationTests
    {
        private static JObject Refusal(int diagnostics, string code = "WebFormValidationFailed")
        {
            var list = new JArray(Enumerable.Range(1, diagnostics).Select(i => new JObject
            {
                ["severity"] = "Error",
                ["message"] = "Attribute 'att:99999" + i + "' is unknown.",
                ["code"] = "spc0233",
                ["positions"] = new JArray("line " + i + ", char 4")
            }));
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = code, ["message"] = "The WebForm SDK validator found errors; the visual write was not persisted.", ["hint"] = "Fix the listed controls/attributes and retry." },
                ["part"] = "WebForm",
                ["persisted"] = false,
                ["validationAvailable"] = true,
                ["validationError"] = "validator reported a problem",
                ["validationDiagnostics"] = list,
                ["internalOnly"] = "dropped"
            };
        }

        [Fact]
        public void KeepsTheValidatorsDiagnosticsOfARefusedWrite()
        {
            var output = McpRouter.TrimErrorEnvelope(Refusal(2), verbose: false);

            var diagnostics = (JArray)output["validationDiagnostics"]!;
            Assert.Equal(2, diagnostics.Count);
            Assert.Equal("spc0233", (string?)diagnostics[0]["code"]);
            Assert.Equal("line 1, char 4", (string?)diagnostics[0]["positions"]![0]);
            Assert.True((bool)output["validationAvailable"]!);
            Assert.Equal("validator reported a problem", (string?)output["validationError"]);
            Assert.Null(output["validationDiagnosticsTruncated"]);
            Assert.Null(output["internalOnly"]);
        }

        [Fact]
        public void CapsTheListAndSaysHowManyThereWere()
        {
            var output = McpRouter.TrimErrorEnvelope(Refusal(35), verbose: false);

            Assert.Equal(20, ((JArray)output["validationDiagnostics"]!).Count);
            Assert.Equal(35, (int)output["validationDiagnosticsTotal"]!);
            Assert.True((bool)output["validationDiagnosticsTruncated"]!);
        }

        [Fact]
        public void KeepsTheListInsideTheErrorObjectToo()
        {
            var input = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "WebFormValidationFailed",
                    ["message"] = "m",
                    ["validationDiagnostics"] = new JArray(new JObject { ["severity"] = "Error", ["message"] = "bad" })
                }
            };

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("bad", (string?)output["validationDiagnostics"]![0]!["message"]);
        }

        [Fact]
        public void VerboseEnvelopeKeepsEverythingAsItWas()
        {
            var input = Refusal(35);

            var output = McpRouter.TrimErrorEnvelope(input, verbose: true);

            Assert.Equal(35, ((JArray)output["validationDiagnostics"]!).Count);
            Assert.Equal("dropped", (string?)output["internalOnly"]);
        }

        [Fact]
        public void AnotherCodeDoesNotGainTheseFields()
        {
            var output = McpRouter.TrimErrorEnvelope(Refusal(3, code: "WriteFailed"), verbose: false);

            Assert.Null(output["validationDiagnostics"]);
            Assert.Null(output["validationAvailable"]);
            Assert.Null(output["validationError"]);
        }

        [Fact]
        public void ARefusalWithoutDiagnosticsAddsNothing()
        {
            var input = new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject { ["code"] = "WebFormValidationFailed", ["message"] = "m" }
            };

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Null(output["validationDiagnostics"]);
            Assert.Null(output["validationDiagnosticsTruncated"]);
        }
    }
}
