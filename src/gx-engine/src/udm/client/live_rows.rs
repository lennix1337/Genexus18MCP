//! Parsing of the tab-separated rows and hex payloads that live `sqlcmd` reads return.

use super::*;

/// Builds one `EntityVersion` record from its declared lengths and hex columns, as the
/// per-object read and the set-wise read both return them. A payload whose hex does not
/// match its declared length is an error, never a silently shortened part.
pub(super) fn entity_version_from_fields(
    entity_type: i32,
    entity_id: i32,
    version_id: i32,
    data_len: &str,
    data_hex: &str,
    props_len: &str,
    props_hex: &str,
) -> Result<EntityVersionRecord, String> {
    let declared = parse_live_int(data_len, "DATALENGTH")?;
    let declared = usize::try_from(declared)
        .map_err(|_| format!("EntityVersion DATALENGTH {declared} is negative"))?;
    let data = decode_hex_value(data_hex, declared)?;
    let declared_props = parse_live_int(props_len, "DATALENGTH(EntityVersionProperties)")?;
    let declared_props = usize::try_from(declared_props)
        .map_err(|_| format!("EntityVersionProperties DATALENGTH {declared_props} is negative"))?;
    let props_bytes = decode_hex_value(props_hex, declared_props)?;
    entity_version_from_payloads(entity_type, entity_id, version_id, data, props_bytes)
}

/// Builds one `EntityVersion` record from its payload bytes, whichever transport carried them.
fn entity_version_from_payloads(
    entity_type: i32,
    entity_id: i32,
    version_id: i32,
    data: Vec<u8>,
    props_bytes: Vec<u8>,
) -> Result<EntityVersionRecord, String> {
    // A NULL column and an empty one both store no properties.
    let properties = if props_bytes.is_empty() {
        None
    } else {
        Some(String::from_utf8(props_bytes).map_err(|e| {
            format!("EntityVersionProperties of ({entity_type}, {entity_id}) is not UTF-8: {e}")
        })?)
    };
    Ok(EntityVersionRecord {
        entity_type_id: entity_type,
        entity_id,
        entity_version_id: version_id,
        data,
        properties,
    })
}

/// The four select-list columns that carry one part: each payload's declared byte length and the
/// payload. `sqlcmd` prints a payload as hex text; a session with binary payloads reads the same
/// bytes as they are stored, so `prefix` is the only thing a caller chooses (`e.` or nothing).
pub(super) fn part_payload_columns(prefix: &str, binary: bool) -> String {
    if binary {
        format!(
            "ISNULL(DATALENGTH({prefix}EntityVersionData), 0), {prefix}EntityVersionData, ISNULL(DATALENGTH({prefix}EntityVersionProperties), 0), CAST({prefix}EntityVersionProperties AS VARBINARY(MAX))"
        )
    } else {
        format!(
            "ISNULL(DATALENGTH({prefix}EntityVersionData), 0), CONVERT(VARCHAR(MAX), {prefix}EntityVersionData, 2), ISNULL(DATALENGTH({prefix}EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST({prefix}EntityVersionProperties AS VARBINARY(MAX)), 2)"
        )
    }
}

/// One payload read as bytes. Its declared length is checked as the hex digits of the text
/// form are: a payload that disagrees with it was cut, and is an error here.
fn binary_payload(
    declared: &live::Value,
    payload: live::Value,
    column: &str,
) -> Result<Vec<u8>, String> {
    let live::Value::Int(declared) = declared else {
        return Err(format!("the database returned a non-integer value for {column}"));
    };
    let declared = usize::try_from(*declared)
        .map_err(|_| format!("{column} {declared} is negative"))?;
    let bytes = match payload {
        live::Value::Null => Vec::new(),
        live::Value::Bytes(bytes) => bytes,
        _ => return Err(format!("the payload of {column} is not binary")),
    };
    if bytes.len() != declared {
        return Err(format!(
            "{column} is {declared} but the payload has {} bytes: it was truncated before it was decoded",
            bytes.len()
        ));
    }
    Ok(bytes)
}

/// A leading column of a typed row, as the text `sqlcmd` would print for it.
fn leading_text(value: &live::Value) -> Result<String, String> {
    match value {
        live::Value::Null => Ok("NULL".into()),
        live::Value::Int(number) => Ok(number.to_string()),
        live::Value::Text(text) => Ok(text.clone()),
        live::Value::Bytes(_) => Err("a part row has a binary column before its payloads".into()),
    }
}

/// What one part read returned: the text `sqlcmd` prints, or typed rows whose payloads are bytes.
enum PartRows {
    Hex(String),
    Binary(Vec<Vec<live::Value>>),
}

impl UdmClient {
    /// Reads `EntityVersion` part rows: `leading` columns of the caller, then the four columns of
    /// [`part_payload_columns`]. `sql` builds the statement for the payload form the session
    /// carries, and `key` reads a part's identity from a row's leading fields. Both forms end in
    /// the same records. `own_session` reads on a session of its own instead of the one this
    /// client's reads share, for a shard that is read in parallel.
    pub(super) fn read_part_records(
        &self,
        own_session: bool,
        leading: usize,
        sql: impl Fn(bool) -> String,
        key: impl Fn(&[&str]) -> Result<(i32, i32, i32), String>,
    ) -> Result<Vec<EntityVersionRecord>, String> {
        let info = self
            .connection_info
            .as_ref()
            .ok_or_else(|| "No connection info configured for live SQL execution".to_string())?;
        let fetch = |session: &mut live::ReadSession| {
            if session.binary_payloads() {
                session.query_rows(&sql(true)).map(PartRows::Binary)
            } else {
                session.query(&sql(false)).map(PartRows::Hex)
            }
        };
        // A read view cannot fan out into independently committed SQL sessions.
        let rows = if own_session && !self.consistent_read && self.read_session.is_some() {
            fetch(&mut live::ReadSession::start(info)?)?
        } else {
            self.on_read_session(info, fetch)?
        };
        rows.decode(leading, key)
    }
}

impl PartRows {
    /// The records of the rows, the same for both forms of the same stored rows.
    fn decode(
        self,
        leading: usize,
        key: impl Fn(&[&str]) -> Result<(i32, i32, i32), String>,
    ) -> Result<Vec<EntityVersionRecord>, String> {
        match self {
            PartRows::Hex(raw) => parse_live_rows(&raw, leading + 4)?
                .into_iter()
                .map(|row| {
                    let (entity_type, entity_id, version_id) = key(&row[..leading])?;
                    entity_version_from_fields(
                        entity_type,
                        entity_id,
                        version_id,
                        row[leading],
                        row[leading + 1],
                        row[leading + 2],
                        row[leading + 3],
                    )
                })
                .collect(),
            PartRows::Binary(rows) => rows
                .into_iter()
                .map(|mut row| {
                    if row.len() != leading + 4 {
                        return Err(format!(
                            "the database returned a row with {} columns; expected {}",
                            row.len(),
                            leading + 4
                        ));
                    }
                    let props = row.pop().expect("length checked above");
                    let props_len = row.pop().expect("length checked above");
                    let data = row.pop().expect("length checked above");
                    let data_len = row.pop().expect("length checked above");
                    let fields = row.iter().map(leading_text).collect::<Result<Vec<_>, _>>()?;
                    let fields: Vec<&str> = fields.iter().map(|field| field.trim()).collect();
                    let (entity_type, entity_id, version_id) = key(&fields)?;
                    let data = binary_payload(&data_len, data, "EntityVersion DATALENGTH")?;
                    let props_bytes = binary_payload(
                        &props_len,
                        props,
                        "EntityVersionProperties DATALENGTH",
                    )?;
                    entity_version_from_payloads(
                        entity_type,
                        entity_id,
                        version_id,
                        data,
                        props_bytes,
                    )
                })
                .collect(),
        }
    }
}

/// Splits `sqlcmd -s <tab>` output into rows of exactly `columns` fields.
///
/// `live.rs` passes `-y 0`, which makes sqlcmd print no header. A header followed
/// by a dashed separator is still skipped, and blank lines are ignored. Any other
/// line with the wrong field count is an error: a row dropped here would be a
/// missing object, part or reference in the answer, which is a false answer.
/// Splits `sqlcmd -s <tab>` output and streams each row of exactly `columns` fields into `callback`.
/// Uses a single reusable buffer to avoid heap allocations per row.
pub(super) fn for_each_live_row<'a>(
    raw: &'a str,
    columns: usize,
    mut callback: impl FnMut(&[&'a str]) -> Result<(), String>,
) -> Result<(), String> {
    let mut fields = Vec::with_capacity(columns);
    let mut iter = raw
        .lines()
        .map(|line| line.trim_end_matches('\r'))
        .filter(|line| !line.trim().is_empty())
        .peekable();

    while let Some(line) = iter.next() {
        if is_separator_line(line) {
            continue;
        }
        if iter.peek().is_some_and(|next| is_separator_line(next)) {
            continue;
        }
        fields.clear();
        for field in line.split('\t') {
            fields.push(field.trim());
        }
        if fields.len() != columns {
            return Err(format!(
                "sqlcmd returned a row with {} tab-separated fields; expected {columns}",
                fields.len()
            ));
        }
        callback(&fields)?;
    }
    Ok(())
}

/// Splits `sqlcmd -s <tab>` output into rows of exactly `columns` fields. `live.rs` passes `-y 0`,
/// which makes sqlcmd print no header. A header followed by a dashed separator is still skipped, and blank
/// lines are ignored. Any other line with the wrong field count is an error: a row dropped here would be a
/// missing object, part or reference in the answer, which is a false answer.
pub(super) fn parse_live_rows(raw: &str, columns: usize) -> Result<Vec<Vec<&str>>, String> {
    let mut rows = Vec::new();
    for_each_live_row(raw, columns, |fields| {
        rows.push(fields.to_vec());
        Ok(())
    })?;
    Ok(rows)
}

impl UdmClient {
    /// Runs one live `SELECT` and decodes each `columns`-field row with `decode`. A row that fails to
    /// decode fails the whole read: a dropped row would be a false answer.
    pub(super) fn live_records<T>(
        &self,
        sql: &str,
        columns: usize,
        decode: impl Fn(&[&str]) -> Result<T, String>,
    ) -> Result<Vec<T>, String> {
        let raw = self.execute_live_sql(sql)?;
        let mut records = Vec::new();
        for_each_live_row(&raw, columns, |row| {
            records.push(decode(row)?);
            Ok(())
        })?;
        Ok(records)
    }
}

fn is_separator_line(line: &str) -> bool {
    line.contains('-') && line.chars().all(|c| matches!(c, '-' | ' ' | '\t'))
}

/// Parses one integer column. The value is not echoed into the error.
pub(super) fn parse_live_int(text: &str, column: &str) -> Result<i32, String> {
    text.parse::<i32>()
        .map_err(|_| format!("sqlcmd returned a non-integer value for {column}"))
}

/// Decodes one `CONVERT(VARCHAR(MAX), <varbinary>, 2)` value. `declared_len` is
/// the byte length the server reports (`DATALENGTH`). A value whose hex length is
/// not exactly twice that length is refused: a truncated blob must fail here, not
/// later as "incomplete deflate stream".
pub(super) fn decode_hex_value(hex: &str, declared_len: usize) -> Result<Vec<u8>, String> {
    if declared_len == 0 {
        return if hex.is_empty() || hex == "NULL" {
            Ok(Vec::new())
        } else {
            Err("EntityVersionData reports zero bytes but returned a value".to_string())
        };
    }
    let digits = hex.as_bytes();
    if digits.len() != declared_len * 2 {
        return Err(format!(
            "EntityVersionData hex value has {} digits for {declared_len} bytes: the payload was truncated before it was decoded",
            digits.len()
        ));
    }
    // Table lookup: this decodes every part payload of a catalog load (tens of MB).
    const INVALID: u8 = 0xFF;
    const TABLE: [u8; 256] = {
        let mut table = [INVALID; 256];
        let mut index = 0;
        while index < 256 {
            if let Some(value) = hex_val(index as u8) {
                table[index] = value;
            }
            index += 1;
        }
        table
    };
    let mut invalid = 0u8;
    let data: Vec<u8> = digits
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| {
            let (high, low) = (TABLE[pair[0] as usize], TABLE[pair[1] as usize]);
            invalid |= (high | low) & 0xF0;
            (high << 4) | (low & 0x0F)
        })
        .collect();
    if invalid != 0 {
        return Err("EntityVersionData hex value contains a non-hex digit".to_string());
    }
    Ok(data)
}

/// Two select-list columns carrying `expr` as its UTF-16 byte length and hex digits. NVARCHAR
/// -> hex preserves Unicode, tabs and newlines through sqlcmd; pair with [`decode_nvarchar`].
pub(super) fn nvarchar_hex_columns(expr: &str) -> String {
    format!(
        "DATALENGTH(CONVERT(NVARCHAR(MAX), {expr})), CONVERT(VARCHAR(MAX), CONVERT(VARBINARY(MAX), CONVERT(NVARCHAR(MAX), {expr})), 2)"
    )
}

/// Decodes the two columns [`nvarchar_hex_columns`] selected. SQL NULL is refused, never empty.
pub(super) fn decode_nvarchar(length: &str, hex: &str, column: &str) -> Result<String, String> {
    let length = usize::try_from(parse_live_int(length.trim(), column)?)
        .map_err(|_| format!("{column} length is negative"))?;
    decode_utf16_le(&decode_hex_value(hex.trim(), length)?, column)
}

/// Strict UTF-16LE: an odd length or unpaired surrogate is refused, never replaced or truncated.
pub(super) fn decode_utf16_le(bytes: &[u8], column: &str) -> Result<String, String> {
    if !bytes.len().is_multiple_of(2) {
        return Err(format!("{column} has an odd UTF-16 byte length"));
    }
    let words: Vec<_> = bytes
        .as_chunks::<2>()
        .0
        .iter()
        .map(|&pair| u16::from_le_bytes(pair))
        .collect();
    String::from_utf16(&words).map_err(|_| format!("{column} is not valid UTF-16"))
}

const fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn live_rows_keep_an_empty_trailing_field_and_skip_no_data() {
        // sqlcmd with -y 0 prints no header; an empty last column keeps its tab.
        let raw = "31\tProcedure\tObjects\r\n81\tProcedure\t\r\n";
        let rows = parse_live_rows(raw, 3).expect("rows");
        assert_eq!(rows[1], vec!["81", "Procedure", ""]);
        assert_eq!(rows.len(), 2);
    }

    #[test]
    fn live_rows_skip_a_header_followed_by_a_separator() {
        let raw = "EntityTypeId\tEntityTypeName\t\r\n------------\t-----------\t\r\n           1\tRoot\t\r\n";
        let rows = parse_live_rows(raw, 3).expect("rows");
        assert_eq!(
            rows,
            vec![vec!["1".to_string(), "Root".to_string(), String::new()]]
        );
    }

    #[test]
    fn live_rows_refuse_a_row_with_the_wrong_field_count() {
        // A name containing a tab would split into an extra field: refused, not dropped.
        let error = parse_live_rows("1\tA\tB\tC\r\n", 3).unwrap_err();
        assert!(error.contains("expected 3"), "{error}");
    }

    #[test]
    fn live_integers_refuse_non_numeric_values() {
        assert_eq!(parse_live_int("38", "EntityTypeId"), Ok(38));
        let error = parse_live_int("Procedure", "EntityTypeId").unwrap_err();
        assert!(error.contains("EntityTypeId"), "{error}");
    }

    #[test]
    fn hex_value_decodes_the_declared_length() {
        assert_eq!(
            decode_hex_value("0102030405060708090A", 10).expect("decode"),
            vec![1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
        );
        assert!(decode_hex_value("", 0).expect("empty payload").is_empty());
    }

    #[test]
    fn hex_value_refuses_a_truncated_payload() {
        // The old default sqlcmd width cut values at 256 characters: 201 bytes
        // declared but a 256-digit value must fail here, not as a corrupt blob.
        let cut = "01".repeat(128);
        let error = decode_hex_value(&cut, 201).unwrap_err();
        assert!(error.contains("truncated"), "{error}");
    }

    #[test]
    fn hex_value_decodes_both_cases_and_refuses_every_non_hex_digit() {
        assert_eq!(
            decode_hex_value("00ffFFa0", 4).expect("decode"),
            vec![0x00, 0xFF, 0xFF, 0xA0]
        );
        // A non-hex digit in either nibble, including bytes next to the hex ranges, is refused.
        for bad in ["0g", "g0", "0/", ":0", "@0", "0G", "`0", "\u{e9}"] {
            assert!(decode_hex_value(bad, 1).is_err(), "{bad} was decoded");
        }
    }

    #[test]
    fn hex_value_refuses_a_value_for_an_empty_payload() {
        assert!(decode_hex_value("00", 0).is_err());
    }

    #[test]
    fn entity_version_fields_decode_the_declared_payload() {
        let record = entity_version_from_fields(7, 8, 9, "3", "414243", "0", "").expect("record");
        assert_eq!(record.data, b"ABC".to_vec());
        assert_eq!(record.properties, None);
        assert_eq!(
            (
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id
            ),
            (7, 8, 9)
        );
    }

    #[test]
    fn entity_version_fields_refuse_a_payload_that_disagrees_with_its_length() {
        assert!(entity_version_from_fields(7, 8, 9, "2", "414243", "0", "").is_err());
    }

    fn key(fields: &[&str]) -> Result<(i32, i32, i32), String> {
        Ok((
            parse_live_int(fields[0], "EntityTypeId")?,
            parse_live_int(fields[1], "EntityId")?,
            parse_live_int(fields[2], "EntityVersionId")?,
        ))
    }

    /// One stored part row in the form each transport returns it.
    fn both_forms(
        id: i64,
        data: Option<&[u8]>,
        properties: Option<&[u8]>,
    ) -> (String, Vec<live::Value>) {
        let hex = |payload: Option<&[u8]>| match payload {
            None => "NULL".to_string(),
            Some(bytes) => bytes.iter().map(|byte| format!("{byte:02X}")).collect(),
        };
        let length = |payload: Option<&[u8]>| payload.map_or(0, <[u8]>::len) as i64;
        let bytes = |payload: Option<&[u8]>| {
            payload.map_or(live::Value::Null, |bytes| live::Value::Bytes(bytes.to_vec()))
        };
        (
            format!(
                "38\t{id}\t1\t{}\t{}\t{}\t{}\r\n",
                length(data),
                hex(data),
                length(properties),
                hex(properties)
            ),
            vec![
                live::Value::Int(38),
                live::Value::Int(id),
                live::Value::Int(1),
                live::Value::Int(length(data)),
                bytes(data),
                live::Value::Int(length(properties)),
                bytes(properties),
            ],
        )
    }

    #[test]
    fn hex_text_and_binary_rows_of_the_same_parts_decode_to_the_same_records() {
        let blob: Vec<u8> = (0..=255u8).cycle().take(1000).collect();
        let stored: [(Option<&[u8]>, Option<&[u8]>); 4] = [
            (Some(&blob), Some(b"<Properties/>")),
            (Some(b""), None),
            (None, Some(b"")),
            (Some(b"\x00\xFF"), Some(b"a\tb\r\nc")),
        ];
        let (mut text, mut rows) = (String::new(), Vec::new());
        for (id, (data, properties)) in stored.into_iter().enumerate() {
            let (hex, binary) = both_forms(id as i64, data, properties);
            text.push_str(&hex);
            rows.push(binary);
        }
        let from_hex = PartRows::Hex(text).decode(3, key).expect("hex rows");
        let from_binary = PartRows::Binary(rows).decode(3, key).expect("binary rows");
        assert_eq!(from_hex, from_binary);
        assert_eq!(from_binary.len(), 4);
        assert_eq!(from_binary[0].data, blob);
        assert_eq!(from_binary[0].properties.as_deref(), Some("<Properties/>"));
        // A NULL payload and an empty one are both no bytes, in either form.
        assert_eq!((from_binary[1].data.len(), &from_binary[1].properties), (0, &None));
        assert_eq!((from_binary[2].data.len(), &from_binary[2].properties), (0, &None));
    }

    #[test]
    fn a_binary_payload_that_disagrees_with_its_declared_length_is_refused() {
        // Mutation check: without the length comparison a payload cut in transit decodes as a
        // shorter part, where the hex form already fails.
        let row = |declared: i64, data: live::Value, props_len: i64, props: live::Value| {
            PartRows::Binary(vec![vec![
                live::Value::Int(38),
                live::Value::Int(7),
                live::Value::Int(1),
                live::Value::Int(declared),
                data,
                live::Value::Int(props_len),
                props,
            ]])
            .decode(3, key)
        };
        let bytes = |payload: &[u8]| live::Value::Bytes(payload.to_vec());
        assert!(row(3, bytes(b"ABC"), 0, live::Value::Null).is_ok());
        for (declared, data, props_len, props) in [
            (4, bytes(b"ABC"), 0, live::Value::Null),
            (3, live::Value::Null, 0, live::Value::Null),
            (3, bytes(b"ABC"), 2, bytes(b"x")),
            (-1, live::Value::Null, 0, live::Value::Null),
            (3, live::Value::Text("414243".into()), 0, live::Value::Null),
        ] {
            assert!(row(declared, data, props_len, props).is_err());
        }
        // A row with a column missing is refused as the text form's field count is.
        assert!(
            PartRows::Binary(vec![vec![live::Value::Int(38); 6]])
                .decode(3, key)
                .is_err()
        );
    }

    /// Synthetic benchmark of the client-side decode stage of a part read, on generated payloads:
    /// the hex text `sqlcmd` prints, parsed and decoded into records, against the typed rows of
    /// the TDS transport turned into the same records. It leaves out everything a server is
    /// needed for: the server-side hex conversion, the transfer, the `sqlcmd` process and its
    /// pipe, and the TDS driver's own parsing. Run it by name on a release build:
    /// `cargo test --release --manifest-path src/gx-engine/Cargo.toml --lib synthetic_benchmark_part_payload_decode -- --ignored --nocapture`
    #[test]
    #[ignore = "synthetic benchmark; run it by name with --ignored --nocapture on a release build"]
    fn synthetic_benchmark_part_payload_decode() {
        const ROW_PAYLOAD: usize = 2048;
        // The hex sizes the README reports for a catalog read: served types only, and every type.
        for (hex_megabytes, iterations) in [(2usize, 21usize), (62, 7)] {
            let rows = hex_megabytes * 1024 * 1024 / 2 / ROW_PAYLOAD;
            let (mut text, mut binary) = (String::new(), Vec::new());
            for id in 0..rows {
                let payload: Vec<u8> = (0..ROW_PAYLOAD).map(|n| (n * 31 + id) as u8).collect();
                let (hex, row) = both_forms(id as i64, Some(&payload), None);
                text.push_str(&hex);
                binary.push(row);
            }
            let median = |mut samples: Vec<f64>| {
                samples.sort_by(f64::total_cmp);
                (samples[samples.len() / 2], samples[0])
            };
            let time = |rows: &dyn Fn() -> PartRows| {
                median(
                    (0..iterations)
                        .map(|_| {
                            // The input is copied outside the timed section.
                            let input = rows();
                            let started = std::time::Instant::now();
                            let records = input.decode(3, key).expect("records");
                            let elapsed = started.elapsed().as_secs_f64() * 1000.0;
                            assert_eq!(records.len(), binary.len());
                            elapsed
                        })
                        .collect(),
                )
            };
            let (hex_median, hex_min) = time(&|| PartRows::Hex(text.clone()));
            let (binary_median, binary_min) = time(&|| PartRows::Binary(binary.clone()));
            println!(
                "synthetic part payload decode: {hex_megabytes} MB of hex text = {} MB of payload in {rows} rows, {iterations} iterations: hex text -> records median {hex_median:.3} ms (min {hex_min:.3}); binary rows -> records median {binary_median:.3} ms (min {binary_min:.3})",
                hex_megabytes / 2
            );
        }
    }

    #[test]
    fn the_payload_columns_of_a_part_read_keep_the_statement_sqlcmd_receives() {
        assert_eq!(
            part_payload_columns("", false),
            "ISNULL(DATALENGTH(EntityVersionData), 0), CONVERT(VARCHAR(MAX), EntityVersionData, 2), ISNULL(DATALENGTH(EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(EntityVersionProperties AS VARBINARY(MAX)), 2)"
        );
        assert_eq!(
            part_payload_columns("e.", true),
            "ISNULL(DATALENGTH(e.EntityVersionData), 0), e.EntityVersionData, ISNULL(DATALENGTH(e.EntityVersionProperties), 0), CAST(e.EntityVersionProperties AS VARBINARY(MAX))"
        );
    }

    #[test]
    fn entity_version_fields_refuse_a_negative_declared_length() {
        // Mutation check: a negative length read as zero would decode the empty hex as an empty part.
        assert!(entity_version_from_fields(7, 8, 9, "-1", "", "0", "").is_err());
        assert!(entity_version_from_fields(7, 8, 9, "0", "", "-1", "").is_err());
    }
}
