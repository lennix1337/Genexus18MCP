using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The line diff behind the rollback receipt. A line counts as differing when the common sequence of the two texts leaves it
    /// out, so a removed duplicate and a moved line are reported, and the diff stays bounded for a large change.
    /// </summary>
    public class ChangedLinesDiffTests
    {
        [Fact]
        public void ARemovedDuplicateLineIsReported()
        {
            var lines = ChangedLines.Describe("Events", "a\na\nb", "a\nb");

            Assert.Single(lines);
            Assert.Equal("before", (string)lines[0]["side"]);
            Assert.Equal("a", (string)lines[0]["line"]);
        }

        [Fact]
        public void AMovedLineIsReportedOnBothSides()
        {
            var lines = ChangedLines.Describe("Events", "a\nb", "b\na");

            Assert.Equal(2, lines.Count);
            Assert.Equal("before", (string)lines[0]["side"]);
            Assert.Equal("a", (string)lines[0]["line"]);
            Assert.Equal("after", (string)lines[1]["side"]);
            Assert.Equal("a", (string)lines[1]["line"]);
        }

        [Fact]
        public void ALargeChangeIsBoundedByTheLineLimit()
        {
            string expected = string.Join("\n", Enumerable.Range(1, 600).Select(n => "old " + n));
            string actual = string.Join("\n", Enumerable.Range(1, 600).Select(n => "new " + n));

            var lines = ChangedLines.Describe("Events", expected, actual);

            Assert.Equal(ChangedLines.DefaultMaxLines, lines.Count);
        }
    }
}
