using System;
using System.Collections.Generic;
using System.IO;

namespace GxMcp.Worker.Helpers
{
    // v2.3.8 (Task 6.2) — friction report 2026-05-15 #10: large parts (Events,
    // Source on big WebPanels) routinely exceed the assistant's tool-result
    // budget. ApplyDefault paginates by lines OR bytes, whichever trips first,
    // and reports a suggestedNextOffset/Limit so the caller can chain pages.
    //
    // Defaults (mcp client only): 200 lines, 16 KB. Other clients (IDE) and
    // explicit limit=0 disable pagination. Explicit offset/limit honour the
    // caller's intent verbatim and only paginate at the explicit boundary.
    public sealed class ReadPage
    {
        public string Content { get; set; } = string.Empty;
        public int Offset { get; set; }
        public int LinesReturned { get; set; }
        public int TotalLines { get; set; }
        public int TotalBytes { get; set; }
        public bool Truncated { get; set; }
        public int? SuggestedNextOffset { get; set; }
        public int? SuggestedNextLimit { get; set; }
        // Issue #27 item 7: the caller explicitly asked for the whole part (limit=0).
        // The gateway honours this by relaxing its source context-budget cut, so
        // "limit=0 to read in full" is truthful instead of being silently re-capped.
        public bool ExplicitFullRead { get; set; }
    }

    public static class ReadPagination
    {
        public const int DefaultLineLimit = 200;
        public const int DefaultByteBudget = 16 * 1024;

        public static ReadPage ApplyDefault(string content, int? offset, int? limit, string client)
        {
            if (content == null) content = string.Empty;

            // Materialise lines once (preserves blanks, strips \r\n vs \n).
            var lines = new List<string>();
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
            }

            int totalLines = lines.Count;
            int totalBytes = content.Length;

            int start = Math.Max(0, offset ?? 0);

            // Explicit opt-out: limit == 0 means "no pagination, return everything from offset".
            if (limit.HasValue && limit.Value == 0)
            {
                start = Math.Min(start, totalLines);
                int position = 0;
                for (int line = 0; line < start && position < content.Length; line++)
                {
                    while (position < content.Length && content[position] != '\r' && content[position] != '\n') position++;
                    if (position < content.Length && content[position++] == '\r'
                        && position < content.Length && content[position] == '\n') position++;
                }
                // A full read is evidence: never synthesize EOLs or drop the final newline.
                var slice = content.Substring(position);
                return new ReadPage
                {
                    Content = slice,
                    Offset = start,
                    LinesReturned = totalLines - start,
                    TotalLines = totalLines,
                    TotalBytes = totalBytes,
                    Truncated = false,
                    ExplicitFullRead = true
                };
            }

            bool mcpDefault = !offset.HasValue && !limit.HasValue
                              && string.Equals(client, "mcp", StringComparison.OrdinalIgnoreCase);

            // Non-MCP clients with no explicit pagination get the full content (legacy IDE behaviour).
            if (!offset.HasValue && !limit.HasValue && !mcpDefault)
            {
                return new ReadPage
                {
                    Content = content,
                    Offset = 0,
                    LinesReturned = totalLines,
                    TotalLines = totalLines,
                    TotalBytes = totalBytes,
                    Truncated = false
                };
            }

            int lineLimit = limit ?? DefaultLineLimit;
            int byteBudget = mcpDefault ? DefaultByteBudget : int.MaxValue;

            var pageLines = new List<string>();
            int pageBytes = 0;
            for (int i = start; i < totalLines && pageLines.Count < lineLimit; i++)
            {
                int lineBytes = lines[i].Length + Environment.NewLine.Length;
                if (pageBytes + lineBytes > byteBudget && pageLines.Count > 0) break;
                pageLines.Add(lines[i]);
                pageBytes += lineBytes;
            }

            int linesReturned = pageLines.Count;
            int endIndex = start + linesReturned;
            bool truncated = endIndex < totalLines;

            int startPosition = 0;
            for (int line = 0; line < start && startPosition < content.Length; line++)
            {
                while (startPosition < content.Length && content[startPosition] != '\r' && content[startPosition] != '\n') startPosition++;
                if (startPosition < content.Length && content[startPosition++] == '\r'
                    && startPosition < content.Length && content[startPosition] == '\n') startPosition++;
            }
            int endPosition = startPosition;
            for (int line = 0; line < linesReturned && endPosition < content.Length; line++)
            {
                while (endPosition < content.Length && content[endPosition] != '\r' && content[endPosition] != '\n') endPosition++;
                if (endPosition < content.Length && content[endPosition++] == '\r'
                    && endPosition < content.Length && content[endPosition] == '\n') endPosition++;
            }
            var pageContent = content.Substring(startPosition, endPosition - startPosition);

            return new ReadPage
            {
                Content = pageContent,
                Offset = start,
                LinesReturned = linesReturned,
                TotalLines = totalLines,
                TotalBytes = totalBytes,
                Truncated = truncated,
                SuggestedNextOffset = truncated ? (int?)endIndex : null,
                SuggestedNextLimit = truncated ? (int?)lineLimit : null
            };
        }
    }
}
