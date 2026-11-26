using System;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class TextPayloadGuardTests
    {
        [Fact]
        public void Analyze_LeadingCommentWithLiteralCrLf_IsRejected()
        {
            var issue = TextPayloadGuard.Analyze("// header\\r\\nDoSomething()\\r\\n");

            Assert.NotNull(issue);
            Assert.False(issue.HasActualLineBreaks);
            Assert.True(issue.StartsWithLineComment);
            Assert.Contains("\\r\\n", issue.LiteralSequences);
        }

        [Fact]
        public void Analyze_ActualLineBreaks_AreAccepted()
        {
            var issue = TextPayloadGuard.Analyze("// header\r\nDoSomething()\r\n");

            Assert.Null(issue);
        }

        [Theory]
        [InlineData("msg('\\r\\n')")]
        [InlineData("msg(\"\\n\")")]
        public void Analyze_LiteralSequencesInsideStrings_AreAccepted(string source)
        {
            var issue = TextPayloadGuard.Analyze(source);

            Assert.Null(issue);
        }

        [Fact]
        public void Analyze_LiteralLineBreakOutsideString_IsRejected()
        {
            var issue = TextPayloadGuard.Analyze("DoA()\\nDoB()");

            Assert.NotNull(issue);
            Assert.Contains("\\n", issue.LiteralSequences);
        }

        [Fact]
        public void Analyze_LiteralSequenceInCommentAfterActualLineBreak_IsAccepted()
        {
            var issue = TextPayloadGuard.Analyze("// header\r\n// documentation mentions \\n");

            Assert.Null(issue);
        }

        [Fact]
        public void BuildWriteError_ReturnsStructuredNonPersistingDiagnostic()
        {
            var response = JObject.Parse(TextPayloadGuard.BuildWriteError(
                "P",
                "Source",
                "content",
                "// header\\r\\nDoSomething()"));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("P", response["target"]?.ToString());
            Assert.Equal("Source", response["part"]?.ToString());
            Assert.Equal("content", response["field"]?.ToString());
            Assert.Equal("\\r\\n", response["literalSequences"]?[0]?.ToString());
        }

        [Fact]
        public void ApplyPatch_InvalidPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new PatchService(null, null).ApplyPatch(
                "P",
                "Source",
                "Replace",
                "// header\\r\\nDoSomething()",
                "DoSomething()"));

            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("content", response["field"]?.ToString());
        }

        [Fact]
        public void ApplyPatch_LiteralSequenceInContext_DoesNotTriggerWriteGuard()
        {
            var response = JObject.Parse(new PatchService(null, null).ApplyPatch(
                "P",
                "Source",
                "Replace",
                "new text",
                "// documentation mentions \\n"));

            Assert.NotEqual("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void FullWrite_InvalidPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new WriteService(null).WriteObject(
                "P",
                "Source",
                "// header\\nDoSomething()"));

            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("content", response["field"]?.ToString());
        }

        [Fact]
        public void FullWrite_ExplicitBase64ContainingLiteralLineBreaks_IsRejectedAfterDecode()
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("// header\\r\\nDoSomething()"));
            var response = JObject.Parse(new WriteService(null).WriteObject(
                "P", "Source", encoded, null, true, false, true, false, true));

            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("content", response["field"]?.ToString());
        }

        [Fact]
        public void AtomicAuthoring_InvalidPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new AtomicAuthoringService(null, null, null, null).Run(new JObject
            {
                ["name"] = "P",
                ["type"] = "Procedure",
                ["source"] = "// header\\r\\nDoSomething()"
            }));

            Assert.Equal("AtomicPreflightFailed", response["error"]?["code"]?.ToString());
            Assert.Contains(response["diagnostics"] as JArray, diagnostic =>
                diagnostic["code"]?.ToString() == "LiteralLineBreaksDetected"
                && diagnostic["member"]?.ToString() == "source");
        }

        [Fact]
        public void BatchEdit_InvalidDirectPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new BatchService(null, null, null, null).BatchEdit("P", new JArray
            {
                new JObject { ["part"] = "Source", ["content"] = "// header\\r\\nDoSomething()" },
                new JObject { ["part"] = "Rules", ["content"] = "DoSomething();" }
            }));

            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("Source", response["part"]?.ToString());
        }

        [Fact]
        public void ParseAndValidate_LiteralSourceLineBreaks_AreFieldValidationErrors()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["source"] = "// header\\r\\nDoSomething()"
            });

            Assert.Contains(spec.Errors, error =>
                error["field"]?.ToString() == "source"
                && error["code"]?.ToString() == "LiteralLineBreaksDetected");
        }

        [Fact]
        public void ParseAndValidate_LiteralRuleLineBreaks_AreFieldValidationErrors()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["rules"] = new JArray("// header\\r\\nDoSomething()")
            });

            Assert.Contains(spec.Errors, error =>
                error["field"]?.ToString() == "rules"
                && error["code"]?.ToString() == "LiteralLineBreaksDetected");
        }

        [Theory]
        [InlineData("Msg(\u2018hello\u2019)", "U+2018")]
        [InlineData("Msg(\u201Chello\u201D)", "U+201C")]
        [InlineData("&a = &b \u2013 1", "U+2013")]
        [InlineData("&a = &b \u2014 1", "U+2014")]
        [InlineData("&a = 1 \u2026", "U+2026")]
        [InlineData("&a\u00A0= 1", "U+00A0")]
        public void BuildWriteError_TypographicCharactersOutsideStringsAndComments_AreRejectedWithLine(string source, string expectedCodePoint)
        {
            string raw = TextPayloadGuard.BuildWriteError("P", "Source", "content", "DoFirst()\r\n" + source);

            Assert.NotNull(raw);
            var response = JObject.Parse(raw);
            Assert.Equal("TypographicCharactersDetected", response["error"]?["code"]?.ToString());
            Assert.Equal(2, response["line"]?.Value<int>());
            Assert.Contains("line 2", response["error"]?["message"]?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(response["typographicCharacters"] as JArray, item =>
                item.ToString().IndexOf(expectedCodePoint, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Theory]
        [InlineData("Msg('\u2018quoted\u2019 \u2013 \u2014 \u2026 \u00A0')")]
        [InlineData("Msg(\"\u201Cquoted\u201D \u2013 \u2014 \u2026 \u00A0\")")]
        [InlineData("Msg('It''s \u2014 fine')")]
        [InlineData("Msg(\"Say \"\"hi\"\" \u2014 ok\")")]
        [InlineData("// Note \u2014 use \u2018ascii\u2019 \u2026\u00A0\r\nDoWork()")]
        [InlineData("/* Block \u2013 \u201Cdoc\u201D \u2026\u00A0 */\r\nDoWork()")]
        [InlineData("&Descri\u00E7\u00E3o = 'A\u00E7\u00E3o'")]
        public void Analyze_TypographicCharactersInsideStringsOrCommentsAndAccentedIdentifiers_AreAccepted(string source)
        {
            var issue = TextPayloadGuard.Analyze(source);

            Assert.Null(issue);
        }

        [Theory]
        [InlineData("Msg('It''s') + &a \u2013 1", 1)]
        [InlineData("Msg('C:\\dir\\') + &a \u2013 1", 1)]
        [InlineData("Msg('http://example.com') + &a \u2013 1", 1)]
        [InlineData("Msg('/* not a comment') + &a \u2013 1", 1)]
        [InlineData("// don't open a string\r\n&a = 1 \u2013 2", 2)]
        [InlineData("/* line 1\r\nline 2 \u2014 ok 'still comment'\r\nline 3 */\r\n&a = 1 \u2013 2", 4)]
        [InlineData("Msg('unclosed\u2019)\r\n&a = 1 \u2013 2", 2)]
        [InlineData("DoA()\nDoB()\n&a = 1 \u2014 2", 3)]
        [InlineData("DoA()\rDoB()\r&a = 1 \u2014 2", 3)]
        public void BuildWriteError_LexicalBoundariesAndLineNumbers_AreAccurate(string source, int expectedLine)
        {
            string raw = TextPayloadGuard.BuildWriteError("P", "Source", "content", source);

            Assert.NotNull(raw);
            var response = JObject.Parse(raw);
            Assert.Equal("TypographicCharactersDetected", response["error"]?["code"]?.ToString());
            Assert.Equal(expectedLine, response["line"]?.Value<int>());
            Assert.Contains("line " + expectedLine, response["error"]?["message"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildWriteError_LiteralLineBreakOnLaterLine_NamesExactLine()
        {
            string raw = TextPayloadGuard.BuildWriteError("P", "Source", "content", "DoA()\r\nDoB()\r\nDoC()\\nDoD()");

            Assert.NotNull(raw);
            var response = JObject.Parse(raw);
            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal(3, response["line"]?.Value<int>());
            Assert.Contains("line 3", response["error"]?["message"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ApplyPatch_TypographicCharacterInContentRejected_AndInContextAllowed()
        {
            var rejected = JObject.Parse(new PatchService(null, null).ApplyPatch(
                "P", "Source", "Replace", "&a = &b \u2013 1", "&a = &b - 1"));
            Assert.Equal("TypographicCharactersDetected", rejected["error"]?["code"]?.ToString());
            Assert.Equal(1, rejected["line"]?.Value<int>());

            var allowedContext = JObject.Parse(new PatchService(null, null).ApplyPatch(
                "P", "Source", "Replace", "&a = &b - 1", "&a = &b \u2013 1"));
            Assert.NotEqual("TypographicCharactersDetected", allowedContext["error"]?["code"]?.ToString());
        }

        [Fact]
        public void FullWrite_TypographicCharacter_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new WriteService(null).WriteObject(
                "P", "Source", "DoA()\r\nMsg(\u2018hi\u2019)"));

            Assert.Equal("TypographicCharactersDetected", response["error"]?["code"]?.ToString());
            Assert.Equal(2, response["line"]?.Value<int>());
        }

        [Fact]
        public void MutationEngine_TypographicCharacter_IsRejectedWithCodeAndLine()
        {
            var engine = new MutationEngine();
            var res = engine.Execute(new MutationRequest
            {
                Target = "TestProc",
                Part = "Source",
                Content = "DoA()\r\n&x = &y \u2014 1",
                Mode = MutationMode.Xml
            });

            Assert.False(res.Success);
            Assert.Equal("TypographicCharactersDetected", res.ErrorCode);
            Assert.Contains("line 2", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AtomicAuthoring_TypographicCharacter_IsRejectedWithDiagnosticCodeAndLine()
        {
            var response = JObject.Parse(new AtomicAuthoringService(null, null, null, null).Run(new JObject
            {
                ["name"] = "P",
                ["type"] = "Procedure",
                ["source"] = "DoA()\r\n&x = &y \u2013 1"
            }));

            Assert.Equal("AtomicPreflightFailed", response["error"]?["code"]?.ToString());
            Assert.Contains(response["diagnostics"] as JArray, diagnostic =>
                diagnostic["code"]?.ToString() == "TypographicCharactersDetected"
                && diagnostic["member"]?.ToString() == "source"
                && diagnostic["line"]?.Value<int>() == 2);
        }

        [Fact]
        public void ParseAndValidate_TypographicCharactersInSourceAndRules_AreFieldValidationErrors()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["source"] = "DoA()\r\nMsg(\u201Chi\u201D)",
                ["rules"] = new JArray("Parm(in:&Id);", "Default(&X, 1 \u2013 2);")
            });

            Assert.Contains(spec.Errors, error =>
                error["field"]?.ToString() == "source"
                && error["code"]?.ToString() == "TypographicCharactersDetected"
                && error["line"]?.Value<int>() == 2);
            Assert.Contains(spec.Errors, error =>
                error["field"]?.ToString() == "rules"
                && error["code"]?.ToString() == "TypographicCharactersDetected"
                && error["line"]?.Value<int>() == 2);
        }

        [Fact]
        public void AuthoringAddDataSelectorCondition_TypographicCharacter_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new GxMcp.Worker.Services.Structure.AuthoringService(null).AddDataSelectorCondition(
                "MyDS",
                new JObject { ["source"] = "CategoryId = \u2018A\u2019" }.ToString()));

            Assert.Equal("TypographicCharactersDetected", response["error"]?["code"]?.ToString());
            Assert.Equal(1, response["line"]?.Value<int>());
        }

        [Fact]
        public void SemanticOpsAddRule_TypographicCharacter_IsRejectedWithLine()
        {
            var service = new SemanticOpsService();
            var op = SemanticOp.From(new JObject { ["op"] = "add_rule", ["text"] = "Error(\u2018bad\u2019);" });

            var ex = Assert.Throws<UsageException>(() =>
                service.Apply("<Rules />", "Transaction", new[] { op }));

            Assert.Equal("TypographicCharactersDetected", ex.Code);
            Assert.Contains("line 1", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildWriteError_CombinedLiteralLineBreakAndTypographicCharacter_ReportsBothAndLine()
        {
            string raw = TextPayloadGuard.BuildWriteError(
                "P", "Source", "content", "DoA()\r\n&x = \u2018bad\u2019\\nDoB()");

            Assert.NotNull(raw);
            var response = JObject.Parse(raw);
            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal(2, response["line"]?.Value<int>());
            Assert.Contains("line 2", response["error"]?["message"]?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(response["literalSequences"] as JArray, t => t.ToString() == "\\n");
            Assert.Contains(response["typographicCharacters"] as JArray, t =>
                t.ToString().IndexOf("U+2018", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void MutationEngine_MultiTargetTypographicCharacter_IsRejectedWithCodeAndLine()
        {
            var engine = new MutationEngine();
            var res = engine.Execute(new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject { ["name"] = "Proc1", ["part"] = "Source", ["content"] = "DoA()\r\nDoB()" },
                    new JObject { ["name"] = "Proc2", ["part"] = "Source", ["content"] = "DoA()\r\nDoB()\r\n&x\u00A0= 1" }
                }
            });

            Assert.False(res.Success);
            Assert.Equal("TypographicCharactersDetected", res.ErrorCode);
            Assert.Contains("line 3", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildWriteError_MultipleAffectedLines_ReportsFirstAndAllLines()
        {
            string raw = TextPayloadGuard.BuildWriteError(
                "P", "Source", "content", "DoA()\r\n&x = &y \u2013 1\r\nDoB()\r\nMsg(\u201Chi\u201D)");

            Assert.NotNull(raw);
            var response = JObject.Parse(raw);
            Assert.Equal("TypographicCharactersDetected", response["error"]?["code"]?.ToString());
            Assert.Equal(2, response["line"]?.Value<int>());
            var lines = response["lines"] as JArray;
            Assert.NotNull(lines);
            Assert.Equal(new[] { 2, 4 }, lines.ToObject<int[]>());
            Assert.Contains("line 2", response["error"]?["message"]?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2, 4", response["error"]?["message"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BatchEdit_TypographicCharacterInDirectPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new BatchService(null, null, null, null).BatchEdit("P", new JArray
            {
                new JObject { ["part"] = "Source", ["content"] = "DoA()\r\n&x = &y \u2014 1" },
                new JObject { ["part"] = "Rules", ["content"] = "Parm(in:&x);" }
            }));

            Assert.Equal("TypographicCharactersDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("Source", response["part"]?.ToString());
            Assert.Equal(2, response["line"]?.Value<int>());
        }
    }
}
