//! Web message files the GeneXus 18 SDK writes beside a web model: `messages.<lang>.txt` (`Key=Value`)
//! and `messages.<lang>.js` (`gx.msg["Key"]="Value";`). Both are UTF-8 without BOM, CRLF line ends.
//!
//! Evidence (GeneXus 18 install, KBTeste; fixtures and tests in `tests/csharp_sdk_messages.rs`):
//! - Keys: one `TranslationMessage` entity (EntityTypeId 149) per message; `FullName` is the key and
//!   `MessageType` is `Internal` for all 626 entities (`Artech.Genexus.Common.Entities.TranslationMessage`).
//! - Text: the KB holds no text for these entities and the English Language LNGStructure has no
//!   translations. Each key's text equals the value in `Library/GAM/Platforms/NetCoreSql/messages.eng.resources`
//!   once the txt escaping is applied (626/626; the other resource files miss keys or differ).
//! - Order: ascending `EntityId` (1..626 in KBTeste, identical to the SDK line order).
//! - Writer: GeneXus ARI code embedded in `Artech.Generator.DotNetCore.dll` (`message_list.ari`,
//!   `generators_translations.ari`). ilspycmd does not expose it, so the byte rules were derived from the
//!   SDK output. Values this corpus does not exercise (backslash, `%`) are refused, not guessed.

use std::collections::HashSet;
use std::fmt::{self, Write as _};

const CRLF: &str = "\r\n";
const RESOURCE_MAGIC: u32 = 0xBEEF_CACE;
const RESOURCE_READER_VERSION: u32 = 2;
const STRING_TYPE_CODE: usize = 1;

/// Whether the message ships with GeneXus (`Internal`) or is user-defined (`User`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MessageKind {
    Internal,
    User,
}

/// One `TranslationMessage` of the KB with the English text the SDK reads for it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MessageEntry {
    /// `TranslationMessage` EntityId. The files list messages in ascending id order.
    pub id: i64,
    /// `FullName`, for example `GXM_newrow`.
    pub key: String,
    pub kind: MessageKind,
    /// English text from the install's message resources. `None` means the source has no entry.
    pub text: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MessageFile {
    pub name: String,
    pub bytes: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MessageFiles {
    pub txt: MessageFile,
    pub js: MessageFile,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MessageError {
    Empty,
    InvalidLanguage(String),
    InvalidKey(String),
    DuplicateId(i64),
    DuplicateKey(String),
    MissingText(String),
    UserMessage(String),
    UnsupportedCharacter { key: String, ch: char },
    Resource(&'static str),
}

impl fmt::Display for MessageError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Empty => write!(f, "no messages to write"),
            Self::InvalidLanguage(lang) => write!(f, "invalid language code {lang:?}"),
            Self::InvalidKey(key) => write!(f, "invalid message key {key:?}"),
            Self::DuplicateId(id) => write!(f, "duplicate message id {id}"),
            Self::DuplicateKey(key) => write!(f, "duplicate message key {key}"),
            Self::MissingText(key) => write!(f, "no English text for message {key}"),
            Self::UserMessage(key) => write!(
                f,
                "user message {key} needs LNG translation text, not implemented"
            ),
            Self::UnsupportedCharacter { key, ch } => {
                write!(f, "message {key} contains unverified character {ch:?}")
            }
            Self::Resource(reason) => write!(f, "malformed message resource: {reason}"),
        }
    }
}

impl std::error::Error for MessageError {}

/// Emits `messages.<lang>.txt` and `messages.<lang>.js` from the KB's messages, in ascending id order.
///
/// Refuses (no partial output) when a message has no text, is a user message, has a duplicate id or key,
/// or contains a character whose SDK escaping this corpus does not establish.
pub fn emit_message_files(
    lang: &str,
    entries: &[MessageEntry],
) -> Result<MessageFiles, MessageError> {
    if lang.is_empty() || !lang.bytes().all(|b| b.is_ascii_alphanumeric()) {
        return Err(MessageError::InvalidLanguage(lang.to_owned()));
    }
    if entries.is_empty() {
        return Err(MessageError::Empty);
    }
    let mut ordered: Vec<&MessageEntry> = entries.iter().collect();
    ordered.sort_by_key(|entry| entry.id);

    let mut keys: HashSet<&str> = HashSet::new();
    let mut txt = String::new();
    let mut js = String::new();
    let mut previous_id = None;
    for entry in ordered {
        if previous_id == Some(entry.id) {
            return Err(MessageError::DuplicateId(entry.id));
        }
        previous_id = Some(entry.id);
        if !valid_key(&entry.key) {
            return Err(MessageError::InvalidKey(entry.key.clone()));
        }
        if !keys.insert(entry.key.as_str()) {
            return Err(MessageError::DuplicateKey(entry.key.clone()));
        }
        if entry.kind != MessageKind::Internal {
            return Err(MessageError::UserMessage(entry.key.clone()));
        }
        let text = entry
            .text
            .as_deref()
            .ok_or_else(|| MessageError::MissingText(entry.key.clone()))?;
        check_characters(&entry.key, text)?;
        let js_text = js_value(&entry.key, text)?;

        txt.push_str(&entry.key);
        txt.push('=');
        txt.push_str(&txt_value(text));
        txt.push_str(CRLF);

        js.push_str("gx.msg[\"");
        js.push_str(&entry.key);
        js.push_str("\"]=\"");
        js.push_str(&js_text);
        js.push_str("\";");
        js.push_str(CRLF);
    }
    Ok(MessageFiles {
        txt: MessageFile {
            name: format!("messages.{lang}.txt"),
            bytes: txt.into_bytes(),
        },
        js: MessageFile {
            name: format!("messages.{lang}.js"),
            bytes: js.into_bytes(),
        },
    })
}

/// Reads the `(name, string value)` pairs of a .NET `.resources` file (ResourceReader version 2).
///
/// Refuses any non-string resource: their values are not decoded here.
pub fn parse_resource_strings(bytes: &[u8]) -> Result<Vec<(String, String)>, MessageError> {
    let mut cursor = Cursor { bytes, pos: 0 };
    if cursor.u32()? != RESOURCE_MAGIC {
        return Err(MessageError::Resource("magic"));
    }
    cursor.u32()?; // ResourceManager header version
    let header_len = cursor.count()?;
    cursor.take(header_len)?; // reader and resource-set type names
    if cursor.u32()? != RESOURCE_READER_VERSION {
        return Err(MessageError::Resource("reader version"));
    }
    let count = cursor.count()?;
    let type_count = cursor.count()?;
    for _ in 0..type_count {
        cursor.string()?;
    }
    cursor.align8()?;
    let hash_bytes = count
        .checked_mul(4)
        .ok_or(MessageError::Resource("count"))?;
    cursor.take(hash_bytes)?; // name hashes, unused
    let mut name_positions = Vec::with_capacity(count);
    for _ in 0..count {
        name_positions.push(cursor.count()?);
    }
    let data_offset = cursor.count()?;
    let names_base = cursor.pos;

    let mut out = Vec::with_capacity(count);
    for position in name_positions {
        let start = names_base
            .checked_add(position)
            .ok_or(MessageError::Resource("name position"))?;
        let mut names = Cursor { bytes, pos: start };
        let byte_len = names.r7()?;
        if byte_len % 2 != 0 {
            return Err(MessageError::Resource("name length"));
        }
        let units: Vec<u16> = names
            .take(byte_len)?
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| u16::from_le_bytes(*pair))
            .collect();
        let name: String = char::decode_utf16(units)
            .collect::<Result<String, _>>()
            .map_err(|_| MessageError::Resource("name utf-16"))?;
        let data_position = names.count()?;
        let start = data_offset
            .checked_add(data_position)
            .ok_or(MessageError::Resource("data position"))?;
        let mut data = Cursor { bytes, pos: start };
        if data.r7()? != STRING_TYPE_CODE {
            return Err(MessageError::Resource("non-string resource"));
        }
        out.push((name, data.string()?.to_owned()));
    }
    Ok(out)
}

fn valid_key(key: &str) -> bool {
    !key.is_empty() && key.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_')
}

/// Refuses characters whose SDK escaping this corpus does not establish: control characters other than
/// LF, `%` (the JS placeholder marker), and backslash (no escaping evidence in either file).
fn check_characters(key: &str, text: &str) -> Result<(), MessageError> {
    for ch in text.chars() {
        let known = ch == '\n' || !(ch.is_control() || ch == '%' || ch == '\\');
        if !known {
            return Err(MessageError::UnsupportedCharacter {
                key: key.to_owned(),
                ch,
            });
        }
    }
    Ok(())
}

/// `txt` value: LF becomes `\n`, `=` becomes `\u003D`; placeholders stay `{n}`.
fn txt_value(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for ch in text.chars() {
        match ch {
            '\n' => out.push_str("\\n"),
            '=' => out.push_str("\\u003D"),
            other => out.push(other),
        }
    }
    out
}

/// `js` value: `"` becomes `\"`, LF becomes `\n`, and placeholders `{n}` become `%(n+1)`.
fn js_value(key: &str, text: &str) -> Result<String, MessageError> {
    let unsupported = |ch: char| MessageError::UnsupportedCharacter {
        key: key.to_owned(),
        ch,
    };
    let mut out = String::with_capacity(text.len() + 8);
    let mut chars = text.chars().peekable();
    while let Some(ch) = chars.next() {
        match ch {
            '"' => out.push_str("\\\""),
            '\n' => out.push_str("\\n"),
            '{' => {
                let mut digits = String::new();
                while let Some(&digit) = chars.peek() {
                    if !digit.is_ascii_digit() {
                        break;
                    }
                    digits.push(digit);
                    chars.next();
                }
                if digits.is_empty() || chars.next() != Some('}') {
                    return Err(unsupported('{'));
                }
                let index: usize = digits.parse().map_err(|_| unsupported('{'))?;
                let one_based = index.checked_add(1).ok_or_else(|| unsupported('{'))?;
                write!(out, "%{one_based}").map_err(|_| unsupported('{'))?;
            }
            '}' => return Err(unsupported('}')),
            other => out.push(other),
        }
    }
    Ok(out)
}

struct Cursor<'a> {
    bytes: &'a [u8],
    pos: usize,
}

impl<'a> Cursor<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], MessageError> {
        let end = self
            .pos
            .checked_add(n)
            .ok_or(MessageError::Resource("truncated"))?;
        let slice = self
            .bytes
            .get(self.pos..end)
            .ok_or(MessageError::Resource("truncated"))?;
        self.pos = end;
        Ok(slice)
    }

    fn u32(&mut self) -> Result<u32, MessageError> {
        let raw = self.take(4)?;
        let array: [u8; 4] = raw
            .try_into()
            .map_err(|_| MessageError::Resource("truncated"))?;
        Ok(u32::from_le_bytes(array))
    }

    /// A non-negative 32-bit count, offset or position.
    fn count(&mut self) -> Result<usize, MessageError> {
        let value = self.u32()?;
        if value > i32::MAX as u32 {
            return Err(MessageError::Resource("negative count"));
        }
        usize::try_from(value).map_err(|_| MessageError::Resource("count"))
    }

    fn r7(&mut self) -> Result<usize, MessageError> {
        let mut value: usize = 0;
        let mut shift: u32 = 0;
        loop {
            let byte = self.take(1)?[0];
            if shift > 28 {
                return Err(MessageError::Resource("7-bit length"));
            }
            value |= usize::from(byte & 0x7f) << shift;
            shift += 7;
            if byte & 0x80 == 0 {
                return Ok(value);
            }
        }
    }

    fn string(&mut self) -> Result<&'a str, MessageError> {
        let len = self.r7()?;
        let raw = self.take(len)?;
        std::str::from_utf8(raw).map_err(|_| MessageError::Resource("utf-8"))
    }

    fn align8(&mut self) -> Result<(), MessageError> {
        while !self.pos.is_multiple_of(8) {
            self.take(1)?;
        }
        Ok(())
    }
}
