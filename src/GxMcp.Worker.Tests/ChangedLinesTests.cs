using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The rollback receipt names the lines that differ in a changed part, at most a fixed number of them and each cut to a
    /// fixed length. The lines are reported on the expected side first, and only the lines that differ are included.
    /// </summary>
    public class ChangedLinesTests
    {
        [Fact]
        public void ReportsTheLinesMissingOnEachSideInOrder()
        {
            var lines = ChangedLines.Describe("Events", "a\nb\nc", "a\nc\nd");

            Assert.Equal(2, lines.Count);
            Assert.Equal("before", (string)lines[0]["side"]);
            Assert.Equal("b", (string)lines[0]["line"]);
            Assert.Equal("after", (string)lines[1]["side"]);
            Assert.Equal("d", (string)lines[1]["line"]);
            Assert.All(lines, line => Assert.Equal("Events", (string)line["part"]));
        }

        [Fact]
        public void IdenticalTextHasNoLines()
        {
            Assert.Empty(ChangedLines.Describe("Events", "a\nb", "a\nb"));
        }

        [Fact]
        public void TheLineCountIsLimited()
        {
            string expected = string.Join("\n", Enumerable.Range(1, 8).Select(n => "old " + n));
            string actual = string.Join("\n", Enumerable.Range(1, 8).Select(n => "new " + n));

            var lines = ChangedLines.Describe("Events", expected, actual, maxLines: 3);

            Assert.Equal(3, lines.Count);
            Assert.All(lines, line => Assert.Equal("before", (string)line["side"]));
        }

        [Fact]
        public void ALongLineIsCutToTheLimit()
        {
            var lines = ChangedLines.Describe("Events", "abcdefghijklmnop", "", maxChars: 10);

            Assert.Equal("abcdefghij...", (string)lines[0]["line"]);
        }

        [Fact]
        public void NoLinesAreReportedWhenTheLimitIsZero()
        {
            Assert.Empty(ChangedLines.Describe("Events", "a", "b", maxLines: 0));
        }

        [Fact]
        public void BlankLinesDoNotCountAsDifferences()
        {
            Assert.Empty(ChangedLines.Describe("Events", "a\n\n\nb", "a\nb\n"));
        }
    }
}
