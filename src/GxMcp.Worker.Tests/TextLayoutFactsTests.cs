using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The export_part receipt says which bytes it wrote so a caller can compare them with the version token's
    /// fingerprint (the SHA-256 of the part text exactly as the SDK holds it). These tests pin how a text's layout is
    /// measured - on the exact string, never normalised - and that the line count agrees with a read's totalLines.
    /// </summary>
    public class TextLayoutFactsTests
    {
        [Theory]
        [InlineData(null, 0, "none", false)]
        [InlineData("", 0, "none", false)]
        [InlineData("a", 1, "none", false)]
        [InlineData("a\n", 1, "LF", true)]
        [InlineData("a\nb", 2, "LF", false)]
        [InlineData("a\r\nb\r\n", 2, "CRLF", true)]
        [InlineData("a\r\nb", 2, "CRLF", false)]
        [InlineData("a\rb", 2, "CR", false)]
        [InlineData("a\rb\r", 2, "CR", true)]
        [InlineData("a\r\nb\n", 2, "mixed", true)]
        [InlineData("a\nb\r\nc", 3, "mixed", false)]
        [InlineData("a\rb\nc", 3, "mixed", false)]
        [InlineData("\n", 1, "LF", true)]
        [InlineData("\r\n\r\n", 2, "CRLF", true)]
        [InlineData("a\n\n", 2, "LF", true)]
        public void Measure_DescribesTheExactText(string text, int lines, string lineEnding, bool endsWithNewline)
        {
            TextLayoutFacts facts = TextLayoutFacts.Measure(text);

            Assert.Equal(lines, facts.Lines);
            Assert.Equal(lineEnding, facts.LineEnding);
            Assert.Equal(endsWithNewline, facts.EndsWithNewline);
        }

        [Fact]
        public void Measure_ACrlfPairIsOneTerminatorNotTwo()
        {
            // Counted as two (CR then LF) it would read as "mixed" with twice the lines.
            TextLayoutFacts facts = TextLayoutFacts.Measure("styles X {\r\n}\r\n");

            Assert.Equal("CRLF", facts.LineEnding);
            Assert.Equal(2, facts.Lines);
        }

        // The receipt's `lines` must mean what `totalLines` means in a read of the same text.
        [Theory]
        [InlineData("")]
        [InlineData("one")]
        [InlineData("one\n")]
        [InlineData("one\ntwo")]
        [InlineData("one\r\ntwo\r\n")]
        [InlineData("one\n\ntwo\n\n")]
        [InlineData("one\rtwo\r\nthree\nfour")]
        [InlineData("\n\n\n")]
        public void Measure_CountsLinesAsAReadDoes(string text)
        {
            var page = ReadPagination.ApplyDefault(text, 0, 0, "mcp");

            Assert.Equal(page.TotalLines, TextLayoutFacts.Measure(text).Lines);
        }

        [Fact]
        public void ExportPartReceipt_ReportsTheHashTheLayoutAndTheVersionToken()
        {
            // export_part needs a live SDK object, so pin the wiring at source level.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("[\"sha256\"] = WriteService.ComputeContentFingerprint(source)", source, StringComparison.Ordinal);
            Assert.Contains("GxMcp.Worker.Helpers.TextLayoutFacts.Measure(source)", source, StringComparison.Ordinal);
            Assert.Contains("[\"lineEnding\"] = layout.LineEnding", source, StringComparison.Ordinal);
            Assert.Contains("[\"endsWithNewline\"] = layout.EndsWithNewline", source, StringComparison.Ordinal);
            Assert.Contains("[\"lines\"] = layout.Lines", source, StringComparison.Ordinal);
            Assert.Contains("exported[\"versionToken\"] = exportedVersionToken", source, StringComparison.Ordinal);
            Assert.Contains("exported[\"tokenFingerprintMatchesFile\"] = exportedVersionToken.EndsWith(\":\" + (string)exported[\"sha256\"], StringComparison.Ordinal)", source, StringComparison.Ordinal);
        }

        [Fact]
        public void TheFingerprintTheReceiptReports_IsTheOneTheVersionTokenEndsWith()
        {
            // Same function, same text: the receipt's sha256 is the token's last segment, so a caller can check a file
            // against the token without knowing anything about how the token is built.
            string text = "styles Sample {\r\n    .A { color: red; }\r\n}\r\n";
            string fingerprint = GxMcp.Worker.Services.WriteService.ComputeContentFingerprint(text);

            Assert.Equal(64, fingerprint.Length);
            Assert.Equal(fingerprint, fingerprint.ToLowerInvariant());
            Assert.NotEqual(fingerprint, GxMcp.Worker.Services.WriteService.ComputeContentFingerprint(text.Replace("\r\n", "\n")));
            Assert.NotEqual(fingerprint, GxMcp.Worker.Services.WriteService.ComputeContentFingerprint(text.TrimEnd()));
        }
    }
}
