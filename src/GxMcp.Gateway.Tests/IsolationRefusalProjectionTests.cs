using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The terse error projection keeps a fixed set of diagnostic keys. A refusal of the isolated Events save is
    // only actionable if every blocker, its remediation and the pin evidence survive that projection.
    public class IsolationRefusalProjectionTests
    {
        private static JObject Refusal() => JObject.Parse(@"{
            ""code"":""ObjectSaveIsolationUnverified"",
            ""message"":""Isolated Events save requires both profile-owned KB and version pins."",
            ""blocker"":""ObjectSaveIsolationUnverified"",
            ""reason"":""Isolated Events save requires both profile-owned KB and version pins."",
            ""reasonCode"":""WriteDestinationPinsMissing"",
            ""blockers"":[
              {""code"":""WriteDestinationPinsMissing"",""layer"":""pins"",""message"":""m1"",""remediation"":""r1"",""evidence"":{""missing"":[""GXMCP_EXPECTED_KB_PATH"",""GXMCP_EXPECTED_KB_VERSION""]}},
              {""code"":""SdkBundleNotAudited"",""layer"":""sdk-bundle"",""message"":""m2"",""remediation"":""r2"",""evidence"":{""auditedBundles"":[""18.0.16.189595""]}}],
            ""requiredPins"":[""GXMCP_EXPECTED_KB_PATH"",""GXMCP_EXPECTED_KB_VERSION""],
            ""effectivePins"":{""kbPathConfigured"":false,""versionConfigured"":false,""source"":""process-environment"",
              ""variables"":{""GXMCP_EXPECTED_KB_PATH"":""absent"",""GXMCP_EXPECTED_KB_VERSION"":""absent""}},
            ""profileRevision"":""0123456789AB"",""profileRevisionSource"":""native-config"",
            ""phase"":""isolation-preflight"",""persisted"":false,""saved"":false,""writeAttempted"":false,
            ""partPersisted"":false,""objectSaved"":false,""savePhase"":""isolation-preflight"",
            ""stack"":""internal""
        }");

        [Fact]
        public void TerseProjection_KeepsEveryBlockerItsRemediationAndThePinEvidence()
        {
            var input = Refusal();

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (var property in input.Properties().Where(p => p.Name != "stack"))
                Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
            Assert.Null(trimmed["stack"]);
            var blockers = (JArray)trimmed["blockers"]!;
            Assert.Equal(2, blockers.Count);
            Assert.Equal("r1", blockers[0]["remediation"]!.ToString());
        }

        [Fact]
        public void TerseProjection_KeepsTheLiteralEscapeDiagnosticOfAPatchThatMatchedNothing()
        {
            var input = JObject.Parse(@"{
                ""code"":""NoMatch"",""message"":""Context block not found."",
                ""literalEscapeSequences"":[""\\n"",""\\t""],
                ""literalEscapeHint"":""The MCP does not convert escape sequences."",
                ""stack"":""internal""}");

            var trimmed = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.True(JToken.DeepEquals(input["literalEscapeSequences"], trimmed["literalEscapeSequences"]));
            Assert.Equal("The MCP does not convert escape sequences.", trimmed["literalEscapeHint"]!.ToString());
        }

        [Fact]
        public void TerseProjection_KeepsLineAndLinesForTextPayloadGuardRefusals()
        {
            var typographic = JObject.Parse(@"{
                ""status"":""error"",
                ""error"":{
                    ""code"":""TypographicCharactersDetected"",
                    ""message"":""Field 'content' contains typographic characters starting on line 2 (lines 2, 4)."",
                    ""hint"":""Use plain ASCII characters.""
                },
                ""part"":""Source"",
                ""field"":""content"",
                ""line"":2,
                ""lines"":[2,4],
                ""typographicCharacters"":[""\u2013 (U+2013 en dash)"",""NBSP (U+00A0 non-breaking space)""],
                ""hasActualLineBreaks"":true,
                ""stack"":""internal""}");

            var trimmedTypo = McpRouter.TrimErrorEnvelope(typographic, verbose: false);

            Assert.Equal("TypographicCharactersDetected", trimmedTypo["code"]?.ToString());
            Assert.Equal(2, trimmedTypo["line"]?.Value<int>());
            Assert.Equal(new[] { 2, 4 }, trimmedTypo["lines"]?.ToObject<int[]>());
            Assert.Equal("content", trimmedTypo["field"]?.ToString());
            Assert.True(JToken.DeepEquals(typographic["typographicCharacters"], trimmedTypo["typographicCharacters"]));
            Assert.True(trimmedTypo["hasActualLineBreaks"]?.Value<bool>() ?? false);
            Assert.Null(trimmedTypo["stack"]);

            var literal = JObject.Parse(@"{
                ""status"":""error"",
                ""error"":{
                    ""code"":""LiteralLineBreaksDetected"",
                    ""message"":""Field 'content' contains literal line break escape sequences (\\n) starting on line 3 (lines 3, 5)."",
                    ""hint"":""Send real line breaks.""
                },
                ""part"":""Source"",
                ""field"":""content"",
                ""line"":3,
                ""lines"":[3,5],
                ""literalSequences"":[""\\n""],
                ""firstLiteralIndex"":18,
                ""hasActualLineBreaks"":true,
                ""stack"":""internal""}");

            var trimmedLiteral = McpRouter.TrimErrorEnvelope(literal, verbose: false);

            Assert.Equal("LiteralLineBreaksDetected", trimmedLiteral["code"]?.ToString());
            Assert.Equal(3, trimmedLiteral["line"]?.Value<int>());
            Assert.Equal(new[] { 3, 5 }, trimmedLiteral["lines"]?.ToObject<int[]>());
            Assert.True(JToken.DeepEquals(literal["literalSequences"], trimmedLiteral["literalSequences"]));
            Assert.Equal(18, trimmedLiteral["firstLiteralIndex"]?.Value<int>());
            Assert.Null(trimmedLiteral["stack"]);
        }
    }
}
