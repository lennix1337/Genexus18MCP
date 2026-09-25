using System;
using System.Text;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (Task 6.2) — friction report 2026-05-15 #10: large Events/Source parts
    // overflow the assistant's tool-result budget. Read paginates by default at 200
    // lines OR 16 KB, whichever is hit first. limit=0 opts out.
    public class ReadPaginationDefaultsTests
    {
        private static string MakeContent(int lines, int approxBytesPerLine = 40)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines; i++)
                sb.AppendLine($"line{i}: " + new string('x', Math.Max(0, approxBytesPerLine - 8)));
            return sb.ToString();
        }

        [Fact]
        public void LargePart_NoOffsetNoLimit_McpClient_DefaultsTo200Lines()
        {
            var content = MakeContent(1240, approxBytesPerLine: 40);
            var page = ReadPagination.ApplyDefault(content, offset: null, limit: null, client: "mcp");

            Assert.True(page.Truncated);
            Assert.Equal(1240, page.TotalLines);
            Assert.Equal(200, page.LinesReturned);
            Assert.Equal(200, page.SuggestedNextOffset);
            Assert.Equal(200, page.SuggestedNextLimit);
        }

        [Fact]
        public void LimitZero_OptsOutOfPagination()
        {
            var content = MakeContent(1240, approxBytesPerLine: 40);
            var page = ReadPagination.ApplyDefault(content, offset: null, limit: 0, client: "mcp");

            Assert.False(page.Truncated);
            Assert.Equal(1240, page.TotalLines);
            Assert.Equal(1240, page.LinesReturned);
            Assert.Equal(content.TrimEnd('\r', '\n').Length, page.Content.TrimEnd('\r', '\n').Length);
            // Issue #27 item 7: limit=0 flags an explicit full read so the gateway honours
            // it (larger source budget) instead of silently re-capping at ~20 KB.
            Assert.True(page.ExplicitFullRead);
        }

        [Fact]
        public void DefaultPaginatedRead_IsNotFlaggedExplicitFull()
        {
            var content = MakeContent(1240, approxBytesPerLine: 40);
            var page = ReadPagination.ApplyDefault(content, offset: null, limit: null, client: "mcp");
            Assert.False(page.ExplicitFullRead);
        }

        [Fact]
        public void IdeClient_NoLimit_DoesNotPaginate()
        {
            var content = MakeContent(1240, approxBytesPerLine: 40);
            var page = ReadPagination.ApplyDefault(content, offset: null, limit: null, client: "ide");

            Assert.False(page.Truncated);
            Assert.Equal(1240, page.LinesReturned);
        }

        [Fact]
        public void Offset_NextPage_ReturnsCorrectSlice()
        {
            var content = MakeContent(500, approxBytesPerLine: 40);
            var page = ReadPagination.ApplyDefault(content, offset: 200, limit: 200, client: "mcp");

            Assert.True(page.Truncated);
            Assert.Equal(500, page.TotalLines);
            Assert.Equal(200, page.LinesReturned);
            Assert.Equal(400, page.SuggestedNextOffset);
        }

        [Fact]
        public void ExplicitWindowOffsetAndLimit_ReturnsExactLinesWithoutDefaultRecap()
        {
            var content = MakeContent(500, approxBytesPerLine: 40);
            var page = ReadPagination.ApplyDefault(content, offset: 318, limit: 20, client: "mcp");

            Assert.Equal(20, page.LinesReturned);
            Assert.Equal(318, page.Offset);
            Assert.StartsWith("line318:", page.Content.TrimStart());
            Assert.DoesNotContain("line317:", page.Content);
            Assert.DoesNotContain("line338:", page.Content);
        }

        [Fact]
        public void LastPage_NoSuggestedNext()
        {
            var content = MakeContent(500, approxBytesPerLine: 40);
            var page = ReadPagination.ApplyDefault(content, offset: 400, limit: 200, client: "mcp");

            Assert.False(page.Truncated);
            Assert.Equal(500, page.TotalLines);
            Assert.Equal(100, page.LinesReturned);
            Assert.Null(page.SuggestedNextOffset);
        }

        [Fact]
        public void ByteBudget_TripsTruncationEvenWhenUnderLineLimit()
        {
            // 150 lines but each ~200 bytes → 30KB total > 16KB budget, so we truncate even though
            // line count is under the 200-line default.
            var content = MakeContent(150, approxBytesPerLine: 200);
            var page = ReadPagination.ApplyDefault(content, offset: null, limit: null, client: "mcp");

            Assert.True(page.Truncated);
            Assert.True(page.LinesReturned <= 150);
            Assert.True(page.Content.Length <= 16 * 1024 + 256); // tolerate trailing line
        }

        [Fact]
        public void SmallContent_NoPagination()
        {
            var content = MakeContent(40, approxBytesPerLine: 40);
            var page = ReadPagination.ApplyDefault(content, offset: null, limit: null, client: "mcp");

            Assert.False(page.Truncated);
            Assert.Equal(40, page.LinesReturned);
            Assert.Null(page.SuggestedNextOffset);
        }
    }
}
