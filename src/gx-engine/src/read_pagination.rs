//! Port of the Worker's `Helpers/ReadPagination.cs` page projection.
//!
//! The SDK projects one part's complete stored text into the `source` field of a
//! read response. `totalLines` counts the text's lines with `StringReader`
//! semantics, `totalBytes` is the UTF-16 code-unit count of the complete text,
//! and each page retains its original line terminators. An explicit `limit=0`
//! read is an evidence path: it returns the original slice, final newline included.
//!
//! This module is a pure projection. It reads no Knowledge Base. `kb_ops` wires it
//! into the single-part `kb-read --page` envelope for the MCP client only; routing
//! and Gateway admission are separate decisions.
//!
//! `ReadClient::Other` is the Worker's plain-text policy for non-MCP clients. It is
//! kept as a tested pure helper; no native route serves that response shape.

/// Client family the Worker's pagination policy distinguishes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReadClient {
    /// MCP defaults to 200 lines or 16 KB, whichever trips first.
    Mcp,
    /// Every other client gets the complete text unless it paginates explicitly.
    Other,
}

/// One projected page of a part's complete text.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReadPage {
    /// The projected page. A complete read uses the original text unchanged.
    pub content: String,
    /// The first line of this page.
    pub offset: usize,
    /// Number of lines in `content` (the Worker's `limit` field).
    pub lines_returned: usize,
    /// Number of lines in the complete text.
    pub total_lines: usize,
    /// UTF-16 code units in the complete text (the Worker's `totalBytes`).
    pub total_bytes: usize,
    /// More lines exist after this page.
    pub truncated: bool,
    pub suggested_next_offset: Option<usize>,
    pub suggested_next_limit: Option<usize>,
    /// The caller explicitly requested the whole part with `limit=0`.
    pub explicit_full_read: bool,
}

pub const DEFAULT_LINE_LIMIT: usize = 200;
pub const DEFAULT_BYTE_BUDGET: usize = 16 * 1024;

/// The Worker runs on Windows, where `Environment.NewLine` is always `\r\n`.
const PAGE_NEWLINE: &str = "\r\n";

/// Projects `content` exactly as the Worker does.
///
/// `offset` is a zero-based line number; `limit` counts lines, and `Some(0)`
/// means "everything from `offset`" without pagination. With neither argument,
/// an MCP caller gets the 200-line / 16 KB default and every other caller gets
/// the complete text.
pub fn apply_default(
    content: &str,
    offset: Option<usize>,
    limit: Option<usize>,
    client: ReadClient,
) -> ReadPage {
    let lines = split_lines(content);
    let total_lines = lines.len();
    let total_bytes = utf16_len(content);
    let start = offset.unwrap_or(0);

    // Explicit opt-out: `limit=0` means "no pagination, return everything from
    // offset". A full read preserves the original EOLs and the final newline.
    if limit == Some(0) {
        let start = start.min(total_lines);
        let position = line_start_byte_offset(content, start);
        return ReadPage {
            content: content[position..].to_string(),
            offset: start,
            lines_returned: total_lines - start,
            total_lines,
            total_bytes,
            truncated: false,
            suggested_next_offset: None,
            suggested_next_limit: None,
            explicit_full_read: true,
        };
    }

    let mcp_default = offset.is_none() && limit.is_none() && client == ReadClient::Mcp;
    if offset.is_none() && limit.is_none() && !mcp_default {
        return ReadPage {
            content: content.to_string(),
            offset: 0,
            lines_returned: total_lines,
            total_lines,
            total_bytes,
            truncated: false,
            suggested_next_offset: None,
            suggested_next_limit: None,
            explicit_full_read: false,
        };
    }

    let line_limit = limit.unwrap_or(DEFAULT_LINE_LIMIT);
    let byte_budget = if mcp_default {
        DEFAULT_BYTE_BUDGET
    } else {
        usize::MAX
    };

    let mut page_lines: Vec<&str> = Vec::new();
    let mut page_bytes = 0usize;
    for line in lines.iter().skip(start).take(line_limit) {
        let line_bytes = utf16_len(line) + utf16_len(PAGE_NEWLINE);
        if page_bytes.saturating_add(line_bytes) > byte_budget && !page_lines.is_empty() {
            break;
        }
        page_lines.push(line);
        page_bytes = page_bytes.saturating_add(line_bytes);
    }

    let lines_returned = page_lines.len();
    let end = start.saturating_add(lines_returned);
    let truncated = end < total_lines;
    ReadPage {
        content: content
            [line_start_byte_offset(content, start)..line_start_byte_offset(content, end)]
            .to_string(),
        offset: start,
        lines_returned,
        total_lines,
        total_bytes,
        truncated,
        suggested_next_offset: truncated.then_some(end),
        suggested_next_limit: truncated.then_some(line_limit),
        explicit_full_read: false,
    }
}

/// `StringReader.ReadLine`: `\r\n`, `\r` and `\n` all terminate a line, a
/// trailing terminator does not produce a phantom empty line, and blank lines
/// are preserved.
fn split_lines(content: &str) -> Vec<&str> {
    let bytes = content.as_bytes();
    let mut lines = Vec::new();
    let mut start = 0usize;
    let mut index = 0usize;
    while index < bytes.len() {
        match bytes[index] {
            b'\r' => {
                lines.push(&content[start..index]);
                index += 1;
                if index < bytes.len() && bytes[index] == b'\n' {
                    index += 1;
                }
                start = index;
            }
            b'\n' => {
                lines.push(&content[start..index]);
                index += 1;
                start = index;
            }
            _ => index += 1,
        }
    }
    if start < bytes.len() {
        lines.push(&content[start..]);
    }
    lines
}

/// Byte offset where line `line` starts, or `content.len()` past the end.
fn line_start_byte_offset(content: &str, line: usize) -> usize {
    let bytes = content.as_bytes();
    let mut position = 0usize;
    for _ in 0..line {
        while position < bytes.len() && bytes[position] != b'\r' && bytes[position] != b'\n' {
            position += 1;
        }
        if position < bytes.len() {
            let terminator = bytes[position];
            position += 1;
            if terminator == b'\r' && position < bytes.len() && bytes[position] == b'\n' {
                position += 1;
            }
        }
    }
    position
}

fn utf16_len(text: &str) -> usize {
    text.encode_utf16().count()
}

#[cfg(test)]
mod tests {
    use super::{DEFAULT_BYTE_BUDGET, ReadClient, apply_default};

    #[test]
    fn successive_pages_preserve_exact_original_line_endings() {
        let content = "first\r\nsecond\nthird\rfourth\r\n";
        let mut collected = String::new();
        let mut offset = 0;
        loop {
            let page = apply_default(content, Some(offset), Some(2), ReadClient::Mcp);
            collected.push_str(&page.content);
            if !page.truncated {
                break;
            }
            offset = page.suggested_next_offset.unwrap();
        }
        assert_eq!(
            collected,
            apply_default(content, None, Some(0), ReadClient::Mcp).content
        );
    }

    #[test]
    fn empty_text_has_no_lines() {
        let page = apply_default("", None, None, ReadClient::Mcp);
        assert_eq!(page.content, "");
        assert_eq!(page.total_lines, 0);
        assert_eq!(page.total_bytes, 0);
        assert_eq!(page.lines_returned, 0);
        assert!(!page.truncated);
    }

    #[test]
    fn a_trailing_terminator_does_not_add_a_line() {
        let page = apply_default("a\nb\n", None, None, ReadClient::Mcp);
        assert_eq!(page.content, "a\nb\n");
        assert_eq!(page.total_lines, 2);
        assert_eq!(page.total_bytes, 4);
        assert_eq!(page.lines_returned, 2);
        assert!(!page.truncated);
    }

    #[test]
    fn crlf_and_lone_cr_are_line_terminators() {
        for content in ["a\r\nb", "a\rb", "a\nb"] {
            let page = apply_default(content, None, None, ReadClient::Mcp);
            assert_eq!(page.content, content, "{content:?}");
            assert_eq!(page.total_lines, 2, "{content:?}");
            assert_eq!(
                page.total_bytes,
                content.encode_utf16().count(),
                "{content:?}"
            );
        }
        let page = apply_default("a\r\n\r\nb", None, None, ReadClient::Mcp);
        assert_eq!(page.content, "a\r\n\r\nb");
        assert_eq!(page.total_lines, 3);
    }

    #[test]
    fn total_bytes_counts_utf16_code_units_not_utf8_bytes() {
        let page = apply_default("\u{1f600}\nx", None, None, ReadClient::Mcp);
        assert_eq!(page.total_bytes, 4);
        assert_eq!(page.total_lines, 2);
        assert_eq!(page.content, "\u{1f600}\nx");
    }

    #[test]
    fn mcp_default_limits_to_two_hundred_lines() {
        let content = (0..250)
            .map(|line| line.to_string())
            .collect::<Vec<_>>()
            .join("\n");
        let page = apply_default(&content, None, None, ReadClient::Mcp);
        assert_eq!(page.lines_returned, 200);
        assert_eq!(page.total_lines, 250);
        assert!(page.truncated);
        assert_eq!(page.suggested_next_offset, Some(200));
        assert_eq!(page.suggested_next_limit, Some(200));
        assert_eq!(page.content.lines().count(), 200);
    }

    #[test]
    fn mcp_default_byte_budget_counts_utf16_units_for_non_bmp_text() {
        // 100 emoji are 200 UTF-16 units (400 UTF-8 bytes, 100 chars); with CRLF a line
        // costs 202. 16384 / 202 = 81, whereas counting chars would admit 160 lines
        // and counting UTF-8 bytes only 40.
        let line = "\u{1f600}".repeat(100);
        let content = std::iter::repeat_n(line.as_str(), 200)
            .collect::<Vec<_>>()
            .join("\n");
        let page = apply_default(&content, None, None, ReadClient::Mcp);
        assert_eq!(page.lines_returned, 81);
        assert!(page.truncated);
        assert_eq!(page.suggested_next_offset, Some(81));
        assert_eq!(page.total_bytes, 200 * 200 + 199);
        assert_eq!(page.content.encode_utf16().count(), 81 * 201);
    }

    #[test]
    fn mcp_default_stops_at_the_byte_budget_before_the_line_limit() {
        let line = "x".repeat(100);
        let content = std::iter::repeat_n(line.as_str(), 200)
            .collect::<Vec<_>>()
            .join("\n");
        let page = apply_default(&content, None, None, ReadClient::Mcp);
        // Each line costs 102 UTF-16 units with its CRLF; 16384 / 102 = 160.
        assert_eq!(page.lines_returned, 160);
        assert!(page.truncated);
        assert_eq!(page.suggested_next_offset, Some(160));
        assert_eq!(page.suggested_next_limit, Some(200));
        assert!(DEFAULT_BYTE_BUDGET >= page.lines_returned * 102);
    }

    #[test]
    fn one_oversized_first_line_is_still_returned() {
        let content = "x".repeat(20_000);
        let page = apply_default(&content, None, None, ReadClient::Mcp);
        assert_eq!(page.lines_returned, 1);
        assert_eq!(page.content.len(), 20_000);
        assert!(!page.truncated);
    }

    #[test]
    fn limit_zero_returns_the_original_slice_with_its_newlines() {
        let page = apply_default("a\nb\nc\n", Some(1), Some(0), ReadClient::Mcp);
        assert_eq!(page.content, "b\nc\n");
        assert_eq!(page.offset, 1);
        assert_eq!(page.lines_returned, 2);
        assert_eq!(page.total_lines, 3);
        assert!(!page.truncated);
        assert!(page.explicit_full_read);
    }

    #[test]
    fn limit_zero_clamps_the_offset_and_returns_an_empty_slice_past_the_end() {
        let page = apply_default("a\nb", Some(9), Some(0), ReadClient::Mcp);
        assert_eq!(page.content, "");
        assert_eq!(page.offset, 2);
        assert_eq!(page.lines_returned, 0);
        assert!(page.explicit_full_read);
    }

    #[test]
    fn a_non_mcp_client_without_pagination_gets_the_complete_text() {
        let page = apply_default("a\nb\n", None, None, ReadClient::Other);
        assert_eq!(page.content, "a\nb\n");
        assert_eq!(page.lines_returned, 2);
        assert!(!page.truncated);
    }

    #[test]
    fn an_explicit_limit_ignores_the_mcp_byte_budget() {
        let line = "x".repeat(20_000);
        let content = format!("{line}\n{line}");
        let page = apply_default(&content, None, Some(1), ReadClient::Mcp);
        assert_eq!(page.lines_returned, 1);
        assert_eq!(page.content.len(), 20_001);
        assert!(page.truncated);
    }

    #[test]
    fn an_offset_past_the_end_is_an_empty_page() {
        let page = apply_default("a\nb", Some(5), Some(2), ReadClient::Mcp);
        assert_eq!(page.content, "");
        assert_eq!(page.offset, 5);
        assert_eq!(page.lines_returned, 0);
        assert!(!page.truncated);
    }
}
