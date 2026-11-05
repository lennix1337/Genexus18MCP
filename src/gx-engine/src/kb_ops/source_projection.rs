//! Projection of stored source parts to SDK representations.

use crate::udm::decoder::UdmPartKind;

/// The type GUID native storage writes between `StyleClass:` and a design-system class name.
const DESIGN_CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";
const STYLE_CLASS_PREFIX: &str = "StyleClass:";

/// The text `kb-read` returns for a stored source part: the SDK's projection of it.
/// Native storage names a design-system class `StyleClass:<type guid><Name>`; the
/// SDK shows `StyleClass:<Name>`. Only that token is rewritten; comments, string
/// literals and other tokens are left alone.
pub(super) fn project_sdk_source(part_name: &str, source: &str) -> String {
    if !UdmPartKind::from_name(part_name).is_tokenized_source() {
        return source.to_string();
    }
    let bytes = source.as_bytes();
    let line_end = |from: usize| source[from..].find('\n').map_or(bytes.len(), |n| from + n);
    let mut out = String::with_capacity(source.len());
    let mut copied = 0;
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            quote @ (b'"' | b'\'') => {
                let mut end = i + 1;
                while end < bytes.len() && bytes[end] != b'\n' {
                    // Rules double-quoted literals consume escapes before testing the terminator;
                    // a doubled single quote is an escaped quote inside a single-quoted literal.
                    let escaped_double = quote == b'"'
                        && bytes[end] == b'\\'
                        && bytes.get(end + 1).is_some_and(|&byte| byte != b'\n');
                    let doubled_single =
                        quote == b'\'' && bytes[end] == b'\'' && bytes.get(end + 1) == Some(&b'\'');
                    if escaped_double || doubled_single {
                        end += 2;
                    } else if bytes[end] == quote {
                        end += 1;
                        break;
                    } else {
                        end += 1;
                    }
                }
                i = end;
            }
            b'/' if bytes.get(i + 1) == Some(&b'/') => i = line_end(i),
            b'/' if bytes.get(i + 1) == Some(&b'*') => {
                i = source[i + 2..]
                    .find("*/")
                    .map_or(bytes.len(), |n| i + 2 + n + 2);
            }
            _ if is_design_class_wrapper(source, i) => {
                let guid_start = i + STYLE_CLASS_PREFIX.len();
                out.push_str(&source[copied..guid_start]);
                i = guid_start + DESIGN_CLASS_TYPE.len();
                copied = i;
            }
            _ => i += 1,
        }
    }
    out.push_str(&source[copied..]);
    out
}

/// Whether `StyleClass:<design class type guid>` starts at byte `at` of `source`,
/// is followed by a class name, and is not the tail of a longer identifier.
fn is_design_class_wrapper(source: &str, at: usize) -> bool {
    let is_identifier = |byte: u8| byte.is_ascii_alphanumeric() || byte == b'_';
    let guid_start = at + STYLE_CLASS_PREFIX.len();
    let guid_end = guid_start + DESIGN_CLASS_TYPE.len();
    let (Some(keyword), Some(guid)) =
        (source.get(at..guid_start), source.get(guid_start..guid_end))
    else {
        return false;
    };
    keyword.eq_ignore_ascii_case(STYLE_CLASS_PREFIX)
        && guid.eq_ignore_ascii_case(DESIGN_CLASS_TYPE)
        && !source.as_bytes()[..at]
            .last()
            .is_some_and(|&byte| is_identifier(byte))
        && source
            .as_bytes()
            .get(guid_end)
            .is_some_and(|&byte| is_identifier(byte))
}
