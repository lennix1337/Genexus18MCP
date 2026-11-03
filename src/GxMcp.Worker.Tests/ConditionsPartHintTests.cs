using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #830: the Conditions part of a web panel is plain text that the SDK accepts, and its only visible failure was
    /// <c>Part save failed: Erro</c>. The commonest cause, proven on a real KB, is a condition without the closing ';'.
    /// </summary>
    public sealed class ConditionsPartHintTests
    {
        // ------------------------------------------------------------------ the hint of a failed save

        [Fact]
        public void AMissingSemicolonIsNamedByLine()
        {
            string hint = WritePolicy.BuildConditionsPartHint("NomeCliente like '%' + &Filtro.Trim() + '%'");

            Assert.Contains("only as 'Erro'", hint);
            Assert.Contains("Each condition ends with ';'", hint);
            Assert.Contains("the likely cause is that these lines do not end with ';': line 1 (", hint);
        }

        [Fact]
        public void OnlyTheLinesWithoutTheTerminatorAreNamed_CommentsAndBlankLinesAreSkipped()
        {
            string text = "// filter by name\r\n"
                        + "NomeCliente like &Filtro;\r\n"
                        + "\r\n"
                        + "EmailCliente like &Filtro\r\n"
                        + "  \r\n"
                        + "Ativo = true";

            string hint = WritePolicy.BuildConditionsPartHint(text);

            Assert.Contains("line 4, line 6 (", hint);
            Assert.DoesNotContain("line 2", hint);
            Assert.DoesNotContain("line 1", hint);
        }

        [Theory]
        [InlineData("Ativo = true; // only active customers")]
        [InlineData("Ativo = true; /* only active customers */")]
        [InlineData("/* only active customers */ Ativo = true;")]
        [InlineData("Ativo = true;\n// closing note")]
        public void ACommentAroundATerminatedConditionIsNotBlamedOnTheTerminator(string text)
        {
            Assert.DoesNotContain("do not end with", WritePolicy.BuildConditionsPartHint(text));
        }

        [Fact]
        public void ABlockCommentThatSpansLinesKeepsTheLineNumbers()
        {
            string hint = WritePolicy.BuildConditionsPartHint("/* first\nsecond */\nAtivo = true;\nNomeCliente like &Filtro");

            Assert.Contains("the likely cause is that these lines do not end with ';': line 4 (", hint);
        }

        [Fact]
        public void SlashesInsideAStringAreNotAComment()
        {
            Assert.DoesNotContain("do not end with", WritePolicy.BuildConditionsPartHint("Site = \"http://host/path\";"));
            Assert.Contains("line 1 (", WritePolicy.BuildConditionsPartHint("Site = \"http://host/path\""));
        }

        [Theory]
        [InlineData("a\nb\nc")]
        [InlineData("a\rb\rc")]
        [InlineData("a\r\nb\r\nc")]
        public void LineNumbersFollowAnyLineEnding(string text)
        {
            Assert.Contains("line 1, line 2, line 3 (", WritePolicy.BuildConditionsPartHint(text));
        }

        [Fact]
        public void TheListIsCappedAndCountsTheRest()
        {
            string hint = WritePolicy.BuildConditionsPartHint("a\nb\nc\nd\ne");

            Assert.Contains("line 1, line 2, line 3 and 2 more (", hint);
        }

        [Fact]
        public void TheListIsPresentedAsTheLikelyCause_AndAllowsAConditionThatSpansLines()
        {
            string hint = WritePolicy.BuildConditionsPartHint("NomeCliente like &Filtro\nwhen not &Filtro.IsEmpty();");

            Assert.Contains("the likely cause", hint);
            Assert.Contains("a condition that spans lines ends with ';' on its last line", hint);
        }

        [Fact]
        public void WhenEveryLineEndsWithTheTerminator_TheHintStillStatesTheFormat()
        {
            string hint = WritePolicy.BuildConditionsPartHint("Ativo = true;\nNomeCliente like &Filtro;");

            Assert.Contains("Each condition ends with ';'", hint);
            Assert.Contains("check the expression", hint);
            Assert.DoesNotContain("do not end with", hint);
        }

        [Theory]
        [InlineData("// only a comment")]
        [InlineData("// one\n// two")]
        [InlineData("/* only a comment */")]
        public void ACommentOnlyTextIsNotBlamedOnTheTerminator(string text)
        {
            Assert.DoesNotContain("do not end with", WritePolicy.BuildConditionsPartHint(text));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \r\n  ")]
        public void NothingToQuoteGivesNoHint(string text)
        {
            Assert.Null(WritePolicy.BuildConditionsPartHint(text));
        }

        // -------------------------------------------------------------------------- the comment stripper

        [Theory]
        [InlineData("a; // note", "a; ")]
        [InlineData("a; /* note */ b;", "a;  b;")]
        [InlineData("a;\n// whole line\nb;", "a;\n\nb;")]
        [InlineData("/* x\ny\nz */a;", "\n\na;")]
        [InlineData("s = \"//x\"; // y", "s = \"//x\"; ")]
        [InlineData("s = '/*x*/'; /* y */", "s = '/*x*/'; ")]
        [InlineData("/* never closed", "")]
        public void CommentsAreRemovedAndTheLineStructureIsKept(string text, string expected)
        {
            Assert.Equal(expected, WritePolicy.StripConditionComments(text));
        }

        [Fact]
        public void AQuoteNeverClosedEndsWithItsLine()
        {
            Assert.Equal("a = 'x\nb; ", WritePolicy.StripConditionComments("a = 'x\nb; // c"));
        }

        // ----------------------------------------------------------- when the hint applies to a failed save

        [Theory]
        [InlineData("Conditions", "Erro")]
        [InlineData("conditions", "Part save failed: Erro")]
        [InlineData("Conditions", "Part save reported errors: Erro")]
        [InlineData("Conditions", "")]
        public void AnUninformativeFailureOfTheConditionsPartGetsTheHint(string part, string error)
        {
            string hint = WritePolicy.BuildConditionsPartFailureHint(part, "NomeCliente like &Filtro", error);

            Assert.NotNull(hint);
            Assert.Contains("do not end with ';': line 1", hint);
        }

        [Theory]
        [InlineData("Rules", "Erro")]
        [InlineData("Source", "Erro")]
        [InlineData(null, "Erro")]
        [InlineData("Conditions", "src0059: Esperando 'EndFor'")]
        public void AnotherPartOrAnInformativeErrorGetsNoConditionsHint(string part, string error)
        {
            Assert.Null(WritePolicy.BuildConditionsPartFailureHint(part, "NomeCliente like &Filtro", error));
        }

        [Fact]
        public void NoSourceGivesNoConditionsHint()
        {
            Assert.Null(WritePolicy.BuildConditionsPartFailureHint("Conditions", null, "Erro"));
            Assert.Null(WritePolicy.BuildConditionsPartFailureHint("Conditions", string.Empty, "Erro"));
        }

        // ---------------------------------------------------------------- which hint a failed save carries

        [Fact]
        public void TheMostSpecificCauseWins_TheSdkDetailsComeLast()
        {
            Assert.Equal("undeclared", WriteService.ChooseSaveFailureHint("undeclared", "rule", "conditions", "details"));
            Assert.Equal("rule", WriteService.ChooseSaveFailureHint(null, "rule", "conditions", "details"));
            Assert.Equal("conditions", WriteService.ChooseSaveFailureHint(null, null, "conditions", "details"));
            Assert.Equal("details", WriteService.ChooseSaveFailureHint(null, null, null, "details"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void WithNoHintAndNoDetailsThereIsNoHint(string details)
        {
            Assert.Null(WriteService.ChooseSaveFailureHint(null, null, null, details));
        }

        // The failed save of a part as the caller receives it (no object needed: the hint needs the text and the error only).
        private static Newtonsoft.Json.Linq.JObject FailedSave(string part, string text, string message)
            => WriteService.CreateTransactionErrorResponse("PainelLista", part, "PartSave", new System.Exception(message), null, null, null, null, text);

        [Fact]
        public void TheFailedSaveOfTheConditionsPart_CarriesTheHint()
        {
            var json = FailedSave("Conditions", "NomeCliente like &Filtro", "Part save failed: Erro");

            string hint = (string)json["error"]["hint"];
            Assert.Contains("Each condition ends with ';'", hint);
            Assert.Contains("these lines do not end with ';': line 1", hint);
            Assert.Equal("TransactionFailed", (string)json["error"]["code"]);
        }

        [Fact]
        public void TheFailedSaveOfAnotherPart_OrWithAnInformativeError_DoesNotCarryIt()
        {
            Assert.DoesNotContain("Each condition ends with", (string)FailedSave("Source", "NomeCliente like &Filtro", "Part save failed: Erro")["error"]["hint"] ?? string.Empty);
            Assert.DoesNotContain("Each condition ends with", (string)FailedSave("Conditions", "NomeCliente like &Filtro", "src0059: unexpected token")["error"]["hint"] ?? string.Empty);
        }

        [Fact]
        public void Wiring_TheFailedSaveUsesTheGateAndThePrecedence()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "WriteService.cs"));

            Assert.Contains("conditionsHint = WritePolicy.BuildConditionsPartFailureHint(partName, decodedCode, enrichedError);", source);
            Assert.Contains("string hint = ChooseSaveFailureHint(undeclaredHint, invalidRuleHint, conditionsHint, detailText);", source);
        }
    }
}
