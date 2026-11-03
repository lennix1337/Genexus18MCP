using System.Collections.Generic;
using System.Linq;
using Artech.Common.Diagnostics;
using Artech.Common.Location;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #830: a refused WebForm came back as "WebFormValidationFailed" with no node, line or code, so the caller
    /// bisected the XML by hand. The SDK's messages carry an error code and positions; the refusal now quotes the first
    /// of them in its message and lists all of them in validationDiagnostics.
    /// </summary>
    public sealed class WebFormValidationDiagnosticsTests
    {
        private static WebFormPreSaveValidator.ValidationMessage Message(string severity, string text, string code = null, string source = null, params string[] positions)
            => new WebFormPreSaveValidator.ValidationMessage
            {
                Severity = severity, Message = text, Code = code, Source = source, Positions = new List<string>(positions)
            };

        // ------------------------------------------------------------ the SDK's own message

        [Fact]
        public void SdkMessage_KeepsLevelTextCodeAndTextPosition()
        {
            var sdk = new OutputError("spc0233", "Attribute NomeCliente not found.", MessageLevel.Error, new TextPosition(3, 12));

            var message = WebFormPreSaveValidator.FromSdkMessage(sdk);

            Assert.Equal("spc0233", message.Code);
            Assert.Equal("Attribute NomeCliente not found.", message.Message);
            Assert.True(WebFormPreSaveValidator.IsErrorSeverity(message.Severity), message.Severity);
            Assert.Equal(new[] { "line 3, char 12" }, message.Positions);
        }

        [Fact]
        public void SdkMessage_KeepsAContextPosition()
        {
            var sdk = new OutputError("src0216", "Control not found.", MessageLevel.Error, new ContextPosition("grid GridProva"));

            var message = WebFormPreSaveValidator.FromSdkMessage(sdk);

            Assert.Equal("src0216", message.Code);
            Assert.Equal(new[] { "grid GridProva" }, message.Positions);
        }

        [Fact]
        public void SdkMessage_WithoutAPositionOfItsOwn_HasNoPositions_NotTheHelpPositionTheSdkSynthesises()
        {
            // OutputError.Position answers an ErrorHelpPosition built from the code when none was given: a link to the
            // error's page, not a place in the form.
            var sdk = new OutputError("spc0233", "Attribute not found.", MessageLevel.Error);

            var message = WebFormPreSaveValidator.FromSdkMessage(sdk);

            Assert.Equal("spc0233", message.Code);
            Assert.Empty(message.Positions);
        }

        [Fact]
        public void SdkMessage_WithSeveralPositions_KeepsAtMostThree()
        {
            var sdk = new OutputError("spc0001", "text", MessageLevel.Error,
                new TextPosition(1, 1), new TextPosition(2, 2), new TextPosition(3, 3), new TextPosition(4, 4));

            var message = WebFormPreSaveValidator.FromSdkMessage(sdk);

            Assert.Equal(new[] { "line 1, char 1", "line 2, char 2", "line 3, char 3" }, message.Positions);
        }

        [Fact]
        public void SdkMessage_WithoutACode_HasNone()
        {
            var message = WebFormPreSaveValidator.FromSdkMessage(new OutputError("Something is wrong.", MessageLevel.Error));

            Assert.True(string.IsNullOrEmpty(message.Code));
            Assert.Equal("Something is wrong.", message.Message);
        }

        [Fact]
        public void Position_ThatSaysNothingBeyondItsTypeName_IsDropped()
        {
            Assert.Null(WebFormPreSaveValidator.DescribePosition(new object()));
            Assert.Null(WebFormPreSaveValidator.DescribePosition(null));
            Assert.Equal("line 7", WebFormPreSaveValidator.DescribePosition(new LineOnly { Line = 7 }));
        }

        private sealed class LineOnly { public int Line { get; set; } }

        // ------------------------------------------------------------------- the quoted summary

        [Fact]
        public void Summary_QuotesCodeTextAndPositionOfTheFirstErrors()
        {
            var summary = WebFormPreSaveValidator.SummarizeErrors(new[]
            {
                Message("Error", "Attribute NomeCliente not found.", "spc0233", null, "line 3, char 12"),
                Message("Warning", "Theme class missing."),
                Message("Error", "Control GridTable is duplicated.", null, "GridTable")
            });

            Assert.Equal(
                "First SDK errors: [spc0233] Attribute NomeCliente not found. (line 3, char 12); Control GridTable is duplicated. (GridTable)",
                summary);
        }

        [Fact]
        public void Summary_OfASingleError_SaysSdkError()
        {
            var summary = WebFormPreSaveValidator.SummarizeErrors(new[] { Message("Error", "Bad.", "spc0001") });

            Assert.Equal("SDK error: [spc0001] Bad.", summary);
        }

        [Fact]
        public void Summary_CountsTheErrorsItDoesNotQuote()
        {
            var errors = Enumerable.Range(1, 5).Select(i => Message("Error", "Problem " + i)).ToList();

            var summary = WebFormPreSaveValidator.SummarizeErrors(errors);

            Assert.Contains("Problem 1; Problem 2; Problem 3", summary);
            Assert.DoesNotContain("Problem 4", summary);
            Assert.EndsWith("; +2 more in validationDiagnostics", summary);
        }

        [Fact]
        public void Summary_QuotesAMessageTheSdkRepeatsOnce()
        {
            // The SDK reports a problem once per control it affects: the same line twice is one error to read.
            var repeated = Message("Error", "A value is required for 'Control Name'.", null, null, "Web Panel 'P', [att:9], Control Name");

            Assert.Equal("SDK error: A value is required for 'Control Name'. (Web Panel 'P', [att:9], Control Name)",
                WebFormPreSaveValidator.SummarizeErrors(new[] { repeated, repeated }));
        }

        [Fact]
        public void Summary_DoesNotCountRepeatsAsMoreMessages()
        {
            // Five errors but two distinct messages, under the cap of three: nothing is left out.
            var errors = new[] { "a", "a", "a", "a", "b" }.Select(t => Message("Error", t)).ToList();

            Assert.Equal("First SDK errors: a; b", WebFormPreSaveValidator.SummarizeErrors(errors));
        }

        [Fact]
        public void Summary_CountsDistinctMessagesWhenCappingTheList()
        {
            var errors = new[] { "a", "a", "b", "c", "c", "d", "e" }.Select(t => Message("Error", t)).ToList();

            string summary = WebFormPreSaveValidator.SummarizeErrors(errors);

            Assert.Equal("First SDK errors: a; b; c; +2 more in validationDiagnostics", summary);
        }

        [Fact]
        public void Summary_UsesTheSourceWhenThereIsNoPosition_AndThePositionsWhenThereAreSome()
        {
            Assert.Equal("SDK error: Bad. (Grid1)", WebFormPreSaveValidator.SummarizeErrors(new[] { Message("Error", "Bad.", null, "Grid1") }));
            Assert.Equal("SDK error: Bad. (line 2, char 4)", WebFormPreSaveValidator.SummarizeErrors(new[] { Message("Error", "Bad.", null, "Grid1", "line 2, char 4") }));
        }

        [Theory]
        [InlineData("Error")]
        [InlineData("error")]
        [InlineData("1")]
        [InlineData("Erro")]
        public void Summary_QuotesEveryErrorSpelling(string severity)
        {
            Assert.Contains("Bad.", WebFormPreSaveValidator.SummarizeErrors(new[] { Message(severity, "Bad.") }));
        }

        [Fact]
        public void Summary_IsEmptyWithoutAnErrorMessage()
        {
            Assert.Equal(string.Empty, WebFormPreSaveValidator.SummarizeErrors(null));
            Assert.Equal(string.Empty, WebFormPreSaveValidator.SummarizeErrors(new WebFormPreSaveValidator.ValidationMessage[0]));
            Assert.Equal(string.Empty, WebFormPreSaveValidator.SummarizeErrors(new[] { Message("Warning", "Careful."), Message("Information", "FYI.") }));
            Assert.Equal(string.Empty, WebFormPreSaveValidator.SummarizeErrors(new[] { Message("Error", "   ") }));
        }

        [Fact]
        public void Summary_IsOneLine_BecauseTheErrorEnvelopeKeepsOnlyTheFirstLineOfAMessage()
        {
            var summary = WebFormPreSaveValidator.SummarizeErrors(new[]
            {
                Message("Error", "Line one\r\nline two\n\tline three", "spc0002", null, "multi\nline\r\ncontext")
            });

            Assert.DoesNotContain("\n", summary);
            Assert.DoesNotContain("\r", summary);
            Assert.Equal("SDK error: [spc0002] Line one line two line three (multi line context)", summary);
        }

        [Fact]
        public void Summary_IsBounded()
        {
            var summary = WebFormPreSaveValidator.SummarizeErrors(new[] { Message("Error", new string('x', 5000)) });

            Assert.True(summary.Length <= 703, summary.Length.ToString());
            Assert.EndsWith("...", summary);
        }

        // ------------------------------------------------------- the refusal of a visual write

        private static WebFormPreSaveValidator.ValidationReport Report(params WebFormPreSaveValidator.ValidationMessage[] messages)
            => new WebFormPreSaveValidator.ValidationReport { ValidatorAvailable = true, Messages = messages.ToList() };

        [Fact]
        public void Refusal_QuotesTheFirstSdkErrorsInItsMessage_AndListsAllOfThemInTheDiagnostics()
        {
            var report = Report(
                Message("Error", "Attribute NomeCliente not found.", "spc0233", "GridProva", "line 3, char 12"),
                Message("Warning", "Unused variable."),
                Message("Error", "Control Foo is unknown.", "spc0216"));

            var json = JObject.Parse(WriteService.BuildWebFormValidationRefusal(
                "PainelLista", "WebForm", "The WebForm SDK validator found errors.", report, dryRun: false));

            Assert.Equal("error", (string)json["status"]);
            Assert.Equal("WebFormValidationFailed", (string)json["error"]["code"]);
            string message = (string)json["error"]["message"];
            Assert.StartsWith("The WebForm SDK validator found errors.", message);
            Assert.Contains("spc0233", message);
            Assert.Contains("Attribute NomeCliente not found.", message);
            Assert.Contains("Control Foo is unknown.", message);
            var diagnostics = (JArray)json["validationDiagnostics"];
            Assert.Equal(3, diagnostics.Count);
            Assert.Equal("spc0233", (string)diagnostics[0]["code"]);
            Assert.Equal("line 3, char 12", (string)diagnostics[0]["positions"][0]);
            Assert.Equal("PainelLista", (string)json["target"]);
            Assert.Equal("WebForm", (string)json["part"]);
            Assert.False((bool)json["persisted"]);
            Assert.Contains("forceWrite=true", (string)json["error"]["hint"]);
        }

        [Fact]
        public void Refusal_OfADryRun_SaysSo_AndTheRealOneDoesNot()
        {
            var report = Report(Message("Error", "bad"));

            var dry = JObject.Parse(WriteService.BuildWebFormValidationRefusal("T", "WebForm", "m", report, dryRun: true));
            var real = JObject.Parse(WriteService.BuildWebFormValidationRefusal("T", "WebForm", "m", report, dryRun: false));

            Assert.True((bool)dry["dryRun"]);
            Assert.Null(real["dryRun"]);
        }

        [Fact]
        public void Refusal_CarriesTheValidatorsAvailabilityAndItsOwnFailure()
        {
            var report = Report(Message("Error", "bad"));
            report.ValidatorAvailable = false;
            report.Error = "validator crashed";

            var json = JObject.Parse(WriteService.BuildWebFormValidationRefusal("T", "WebForm", "m", report, dryRun: false));

            Assert.False((bool)json["validationAvailable"]);
            Assert.Equal("validator crashed", (string)json["validationError"]);

            report.Error = null;
            Assert.Null(JObject.Parse(WriteService.BuildWebFormValidationRefusal("T", "WebForm", "m", report, dryRun: false))["validationError"]);
        }

        [Fact]
        public void Refusal_WithNoErrorToQuote_KeepsTheBaseMessage()
        {
            var json = JObject.Parse(WriteService.BuildWebFormValidationRefusal(
                "T", "WebForm", "The validator refused the form.", Report(Message("Warning", "only a warning")), dryRun: false));

            Assert.Equal("The validator refused the form.", (string)json["error"]["message"]);
            Assert.Single((JArray)json["validationDiagnostics"]);
        }

        // -------------------------------------------------------------------------- wiring

        [Fact]
        public void Wiring_EveryWebFormValidationRefusalOfAVisualWriteUsesTheSharedBuilder()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "WriteService.VisualWrite.cs"));

            // The current-XML check, the dry run and the write itself: one builder, the code spelled once.
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(source, @"return BuildWebFormValidationRefusal\(target, partName,").Count);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, "code: \"WebFormValidationFailed\""));
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source, @"dryRun: true\);").Count);
            Assert.Contains("validationReport, dryRun: false);", source);
            Assert.Contains("item[\"code\"] = message.Code", source);
            Assert.Contains("item[\"positions\"] = new JArray(message.Positions)", source);
        }

        [Fact]
        public void Wiring_TheTypedControlWritesQuoteTheSdkErrorCodeToo()
        {
            string conditions = System.IO.File.ReadAllText(System.IO.Path.Combine(
                TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "ControlConditionsService.cs"));
            string parameters = System.IO.File.ReadAllText(System.IO.Path.Combine(
                TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "ControlParametersService.cs"));

            Assert.Contains("\"[\" + d.Code + \"] \"", conditions);
            Assert.Contains("[\"diagnostics\"] = new JArray(diagnostics.Select(d => new JObject { [\"code\"] = d.Code", conditions);
            Assert.Contains("\"[\" + d.Code + \"] \"", parameters);
            Assert.Contains("quoteDiagnostics: true", parameters); // only the layout-validator refusal; others carry their own detail
        }
    }
}
