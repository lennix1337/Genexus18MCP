using System;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #494, reproduced live against a GeneXus 18.0.10 KB: `add_attribute`
    // persisted and the SDK returned the attribute with the description it
    // generated itself, so the receipt reported `WriteNotPersisted` with
    // `partialPersistenceDetected: true` while an independent re-read showed the
    // attribute present. The diff pointed at line 1 and at LF vs CRLF, neither of
    // which was the cause.
    public class SdkGeneratedDescriptionTests
    {
        // Exactly what the SDK returned after the write.
        private const string Persisted =
            "McpTst492Id* : NUMERIC(4) // \"Mcp Tst492 Id\"\n" +
            "McpTst492Valor : NUMERIC(4) // \"Mcp Tst492 Valor\"\n" +
            "McpTst492Simples : NUMERIC(6) // \"Mcp Tst492 Simples\", [Formula: McpTst492Valor * 2;]";

        // What was requested: no description on the new attribute.
        private const string Requested =
            "McpTst492Id* : NUMERIC(4) // \"Mcp Tst492 Id\"\n" +
            "McpTst492Valor : NUMERIC(4) // \"Mcp Tst492 Valor\"\n" +
            "McpTst492Simples : NUMERIC(6) // [Formula: McpTst492Valor * 2;]";

        [Fact]
        public void SdkGeneratedDescription_DoesNotCauseAFalseMismatch()
        {
            var result = TextPersistenceVerifier.Evaluate(Requested, Persisted, "semantic", "Structure");

            Assert.True(
                result.Matches,
                "the write persisted; the generated description must not fail the comparison");
        }

        [Fact]
        public void ARealDescriptionDifference_StillFails()
        {
            // The strip must not become a blanket "ignore the comment". A caller
            // who asked for one description and got a different one has a real
            // mismatch to hear about.
            var result = TextPersistenceVerifier.Evaluate(
                "Attr : NUMERIC(6) // \"Something else entirely\"",
                "Attr : NUMERIC(6) // \"Attr\"",
                "semantic",
                "Structure");

            Assert.False(result.Matches, "a differing description is a real mismatch");
        }

        [Fact]
        public void DescriptionEqualToTheName_IsNotRenderedSoItIsNotAMismatch()
        {
            // `// "Attr"` on an attribute called `Attr` is dropped by the DSL
            // renderer - that is the same declaration as no comment at all.
            // Treating them as different would be its own false mismatch.
            var result = TextPersistenceVerifier.Evaluate(
                "Attr : NUMERIC(6) // \"Attr\"",
                "Attr : NUMERIC(6)",
                "semantic",
                "Structure");

            Assert.True(result.Matches, "a name-equal description is the same declaration");
        }

        [Fact]
        public void ACustomDescription_IsNeverTreatedAsGenerated()
        {
            // The whole risk of the strip is mistaking a caller's description for
            // a generated one and so passing a wrong write.
            var stripped = TextPersistenceVerifier.StripSdkGeneratedAttributeDescriptions(
                "McpTst492Valor : NUMERIC(4) // \"Descricao escolhida a mao\"");

            Assert.Contains("Descricao escolhida a mao", stripped);
        }

        [Fact]
        public void GeneratedDescription_IsDroppedButTheFormulaSurvives()
        {
            // The SDK renders `// "Attr Name", [Formula: ...]`. Dropping the whole
            // comment would hide the formula from the comparison, which is the
            // part the caller actually wrote.
            var stripped = TextPersistenceVerifier.StripSdkGeneratedAttributeDescriptions(
                "McpTst492Simples : NUMERIC(6) // \"Mcp Tst492 Simples\", [Formula: McpTst492Valor * 2;]");

            Assert.DoesNotContain("Mcp Tst492 Simples", stripped);
            Assert.Contains("[Formula: McpTst492Valor * 2;]", stripped);
        }

        [Fact]
        public void AFormulaThatDidNotLand_StillFails()
        {
            var result = TextPersistenceVerifier.Evaluate(
                "A : NUMERIC(6) // [Formula: B;]",
                "A : NUMERIC(6) // \"A\", [Formula: C;]",
                "semantic",
                "Structure");

            Assert.False(result.Matches, "a different formula is a real mismatch");
        }

        [Fact]
        public void NonAttributeLines_AreNeverTouched()
        {
            foreach (var line in new[]
            {
                "Level1",
                "For Each Att In Collection",
                "// just a comment",
                "Call(procAlpha)",
                "CustomerBalance = 0;"
            })
            {
                Assert.Equal(line, TextPersistenceVerifier.StripSdkGeneratedAttributeDescriptions(line).TrimEnd('\n'));
            }
        }

        [Fact]
        public void LinesWithUnclosedQuotes_AreNotStripped()
        {
            var stripped = TextPersistenceVerifier.StripSdkGeneratedAttributeDescriptions(
                "A : NUMERIC(6) // \"unclosed");

            Assert.Contains("unclosed", stripped);
        }

        [Fact]
        public void TrailingNewlineIsPreserved()
        {
            var withNewline = "A : NUMERIC(6) // \"A\"\n";
            var without = "A : NUMERIC(6) // \"A\"";

            Assert.EndsWith("\n", TextPersistenceVerifier.StripSdkGeneratedAttributeDescriptions(withNewline));
            Assert.False(
                TextPersistenceVerifier.StripSdkGeneratedAttributeDescriptions(without).EndsWith("\n", StringComparison.Ordinal),
                "an input with no trailing newline must not gain one");
        }

        [Fact]
        public void CRLFAndLF_ProduceTheSameCanonicalForm()
        {
            // The live diff blamed the line ending; it must not matter once the
            // real cause is removed.
            var lf = Requested;
            var crlf = Requested.Replace("\n", "\r\n");

            Assert.True(TextPersistenceVerifier.Evaluate(lf, Persisted, "semantic", "Structure").Matches);
            Assert.True(TextPersistenceVerifier.Evaluate(crlf, Persisted, "semantic", "Structure").Matches);
        }

        [Fact]
        public void AnAttributeThatWasActuallyNotAdded_StillFails()
        {
            // The guard must not turn a genuine failure into success: the whole
            // risk of this change is a false positive in a write verdict.
            var result = TextPersistenceVerifier.Evaluate(
                Requested,
                "McpTst492Id* : NUMERIC(4) // \"Mcp Tst492 Id\"\n" +
                "McpTst492Valor : NUMERIC(4) // \"Mcp Tst492 Valor\"",
                "semantic",
                "Structure");

            Assert.False(
                result.Matches,
                "a missing attribute must still be reported as not persisted");
        }

        [Fact]
        public void ARemovedLine_StillFails()
        {
            // Containment would pass this; equality must not.
            var result = TextPersistenceVerifier.Evaluate(
                "A : NUMERIC(6) // \"A\"\nB : NUMERIC(6)",
                "A : NUMERIC(6) // \"A\"\nB : NUMERIC(6) // \"B\"\nC : NUMERIC(6)",
                "semantic",
                "Structure");

            Assert.False(result.Matches, "an extra persisted line is a real mismatch");
        }

        [Fact]
        public void McpTstR03A_WithoutDescription_MatchesSdkGeneratedDescriptionWhenVerifyModeOmitted()
        {
            const string requested = "McpTstR03A : Character(20)";
            const string persisted = "McpTstR03A : Character(20) // \"Mcp Tst R03 A\"";

            // With verifyMode omitted (null), Structure resolves to semantic verification
            // and strips the SDK-generated description for McpTstR03A
            var result = TextPersistenceVerifier.Evaluate(requested, persisted, null, "Structure");
            Assert.True(result.Matches, "SDK-generated description 'Mcp Tst R03 A' for 'McpTstR03A' should match when verifyMode is omitted");
        }

        [Fact]
        public void LeadingCapitalRun_SdkKeepsItAsOneWord_AndTheDescriptionIsStillRecognised()
        {
            // #541 recorded the SDK deriving "ZTst Mcp Var" from ZTstMcpVar: a leading run
            // of capitals stays one word, where a camel-case split gives "Z Tst Mcp Var".
            // Recognising the generated description must not depend on guessing that rule.
            var result = TextPersistenceVerifier.Evaluate(
                "ZTstMcpArq : NUMERIC(4)",
                "ZTstMcpArq : NUMERIC(4) // \"ZTst Mcp Arq\"",
                "semantic",
                "Structure");

            Assert.True(result.Matches, "the SDK's own word boundaries for a leading capital run must be accepted");
        }

        [Fact]
        public void LeadingCapitalRun_ADescriptionWithOtherLetters_IsStillACallerDescription()
        {
            var result = TextPersistenceVerifier.Evaluate(
                "ZTstMcpArq : NUMERIC(4)",
                "ZTstMcpArq : NUMERIC(4) // \"ZTst Mcp Arquivo\"",
                "semantic",
                "Structure");

            Assert.False(result.Matches, "letters the name does not contain mean a real description");
        }

        [Fact]
        public void OnlyGeneratedDescriptionDiffers_IsRecognisedOnStructureOnly()
        {
            const string requested = "McpTstR03A : NUMERIC(4)";
            const string persisted = "McpTstR03A : NUMERIC(4) // \"Mcp Tst R03 A\"";

            Assert.True(TextPersistenceVerifier.OnlySdkGeneratedAttributeDescriptionDiffers("Structure", requested, persisted));
            Assert.False(TextPersistenceVerifier.OnlySdkGeneratedAttributeDescriptionDiffers("Structure", persisted, persisted));
            Assert.False(TextPersistenceVerifier.OnlySdkGeneratedAttributeDescriptionDiffers(
                "Structure", requested, "McpTstR03A : NUMERIC(6) // \"Mcp Tst R03 A\""));
            Assert.False(TextPersistenceVerifier.OnlySdkGeneratedAttributeDescriptionDiffers(
                "Structure", "McpTstR03A : NUMERIC(4) // \"Valor\"", persisted));
            Assert.False(TextPersistenceVerifier.OnlySdkGeneratedAttributeDescriptionDiffers("Source", requested, persisted));
        }

        [Fact]
        public void OnlyGeneratedDescriptionDiffers_IgnoresTheSdkTypeKeywordCasing()
        {
            // GX18 U16 proof: `add_attribute {type:"Numeric(4)"}` is persisted as
            // `NUMERIC(4) // "Mcp Prova557 B"`. The keyword casing is the SDK's own
            // rendering (identifiers and types are case-insensitive in GeneXus), so the
            // description label must still be earned...
            Assert.True(TextPersistenceVerifier.OnlySdkGeneratedAttributeDescriptionDiffers(
                "Structure", "McpTstR03A : Numeric(4)", "McpTstR03A : NUMERIC(4) // \"Mcp Tst R03 A\""));
            // ...while a casing-only rendering without the description is not about the
            // description at all.
            Assert.False(TextPersistenceVerifier.OnlySdkGeneratedAttributeDescriptionDiffers(
                "Structure", "McpTstR03A : Numeric(4)", "McpTstR03A : NUMERIC(4)"));
        }
    }
}
