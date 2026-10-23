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

/// Splits `sqlcmd -s <tab>` output into rows of exactly `columns` fields.
///
/// `live.rs` passes `-y 0`, which makes sqlcmd print no header. A header followed
/// by a dashed separator is still skipped, and blank lines are ignored. Any other
/// line with the wrong field count is an error: a row dropped here would be a
/// missing object, part or reference in the answer, which is a false answer.
pub(super) fn parse_live_rows(raw: &str, columns: usize) -> Result<Vec<Vec<&str>>, String> {
    let lines: Vec<&str> = raw
        .lines()
        .map(|line| line.trim_end_matches('\r'))
        .filter(|line| !line.trim().is_empty())
        .collect();
    let mut rows = Vec::with_capacity(lines.len());
    for (index, line) in lines.iter().enumerate() {
        if is_separator_line(line) {
            continue;
        }
        if lines
            .get(index + 1)
            .is_some_and(|next| is_separator_line(next))
        {
            continue;
        }
        let fields: Vec<&str> = line.split('\t').map(str::trim).collect();
        if fields.len() != columns {
            return Err(format!(
                "sqlcmd returned a row with {} tab-separated fields; expected {columns}",
                fields.len()
            ));
        }
        rows.push(fields);
    }
    Ok(rows)
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

    #[test]
    fn entity_version_fields_refuse_a_negative_declared_length() {
        // Mutation check: a negative length read as zero would decode the empty hex as an empty part.
        assert!(entity_version_from_fields(7, 8, 9, "-1", "", "0", "").is_err());
        assert!(entity_version_from_fields(7, 8, 9, "0", "", "-1", "").is_err());
    }
}
