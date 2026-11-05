//! Query descriptors distinguish the SDK's WebPanel subtypes without changing catalog/codegen kinds.

use super::{ObjectKind, ObjectRow, UdmClient, type_name};

pub(super) fn query_object_type(
    client: &UdmClient,
    row: &ObjectRow,
) -> Result<&'static str, String> {
    if row.kind != ObjectKind::WebPanel {
        return Ok(type_name(row.kind));
    }
    let result = client
        .get_entity_version(row.entity_type_id, row.entity_id, row.entity_version_id)
        .and_then(|version| {
            let version = version.ok_or("the active version record is missing")?;
            webpanel_type(version.properties.as_deref())
        });
    result.map_err(|error| {
        format!(
            "cannot classify WebPanel '{}' ({}/{}/{}): {error}",
            row.name, row.entity_type_id, row.entity_id, row.entity_version_id
        )
    })
}

fn webpanel_type(properties: Option<&str>) -> Result<&'static str, String> {
    let text = properties.ok_or("stored properties are missing")?;
    let document = roxmltree::Document::parse(text)
        .map_err(|_| "stored properties are not valid XML".to_string())?;
    let root = document.root_element();
    if !root.has_tag_name("Properties") {
        return Err("stored properties have no Properties root".into());
    }
    let mut flag = None;
    for property in root.children().filter(|node| node.is_element()) {
        let names: Vec<_> = property
            .children()
            .filter(|node| node.has_tag_name("Name"))
            .collect();
        if !property.has_tag_name("Property")
            || names.len() != 1
            || names[0].children().any(|node| !node.is_text())
            || names[0].text().is_none_or(str::is_empty)
        {
            return Err("stored properties have a malformed Property name".into());
        }
        if names[0].text() != Some("WEB_COMP") {
            continue;
        }
        if flag.is_some() {
            return Err("WEB_COMP is stored more than once".into());
        }
        let values: Vec<_> = property
            .children()
            .filter(|node| node.has_tag_name("Value"))
            .collect();
        if values.len() != 1 || values[0].children().any(|node| !node.is_text()) {
            return Err("WEB_COMP has no single text Value".into());
        }
        flag = Some(values[0].text().unwrap_or_default());
    }
    // GX18 SELECT/SDK differential evidence: absent => WebPanel, Master => MasterPage,
    // Yes => WebComponent. No other spelling/value is established, including blank or No.
    match flag {
        None => Ok("WebPanel"),
        Some("Master") => Ok("MasterPage"),
        Some("Yes") => Ok("WebComponent"),
        Some(_) => Err("unsupported WEB_COMP value".into()),
    }
}

/// Matches an object name against a query pattern.
/// If `pattern` does not contain '*', performs an exact case-insensitive match.
/// If `pattern` contains '*', supports prefix ('P*'), suffix ('*trn'), substring ('*cust*'), or multi-wildcard globs.
pub(super) fn matches_name_pattern(name: &str, pattern: &str) -> bool {
    let pattern = pattern.trim();
    let name = name.trim();
    if !pattern.contains('*') {
        return name.eq_ignore_ascii_case(pattern);
    }

    if let Some((prefix, suffix)) = pattern.split_once('*') {
        if !suffix.contains('*') {
            if name.len() < prefix.len() + suffix.len() {
                return false;
            }
            return starts_with_ignore_ascii_case(name, prefix)
                && ends_with_ignore_ascii_case(name, suffix);
        }
    }

    let mut remainder = name;
    let mut parts = pattern.split('*').peekable();
    let mut is_first = true;
    while let Some(part) = parts.next() {
        let is_last = parts.peek().is_none();
        if part.is_empty() {
            is_first = false;
            continue;
        }
        if is_first {
            if !starts_with_ignore_ascii_case(remainder, part) {
                return false;
            }
            remainder = &remainder[part.len()..];
        } else if is_last && !pattern.ends_with('*') {
            return ends_with_ignore_ascii_case(remainder, part);
        } else if let Some(idx) = find_ignore_ascii_case(remainder, part) {
            remainder = &remainder[idx + part.len()..];
        } else {
            return false;
        }
        is_first = false;
    }
    true
}

fn starts_with_ignore_ascii_case(haystack: &str, needle: &str) -> bool {
    haystack
        .as_bytes()
        .get(..needle.len())
        .is_some_and(|slice| slice.eq_ignore_ascii_case(needle.as_bytes()))
}

fn ends_with_ignore_ascii_case(haystack: &str, needle: &str) -> bool {
    haystack
        .len()
        .checked_sub(needle.len())
        .and_then(|start| haystack.as_bytes().get(start..))
        .is_some_and(|slice| slice.eq_ignore_ascii_case(needle.as_bytes()))
}

fn find_ignore_ascii_case(haystack: &str, needle: &str) -> Option<usize> {
    if needle.is_empty() {
        return Some(0);
    }
    let needle_bytes = needle.as_bytes();
    haystack
        .as_bytes()
        .windows(needle_bytes.len())
        .position(|window| window.eq_ignore_ascii_case(needle_bytes))
}

#[cfg(test)]
mod tests {
    use super::matches_name_pattern;

    #[test]
    fn test_exact_matches() {
        assert!(matches_name_pattern("Customer", "Customer"));
        assert!(matches_name_pattern("customer", "CUSTOMER"));
        assert!(!matches_name_pattern("Customer", "Vendor"));
    }

    #[test]
    fn test_prefix_matches() {
        assert!(matches_name_pattern("CustomerList", "Customer*"));
        assert!(matches_name_pattern("customerlist", "CUSTOMER*"));
        assert!(!matches_name_pattern("VendorList", "Customer*"));
    }

    #[test]
    fn test_suffix_matches() {
        assert!(matches_name_pattern("CustomerTrn", "*Trn"));
        assert!(matches_name_pattern("customertrn", "*TRN"));
        assert!(!matches_name_pattern("CustomerTrn", "*Pr"));
    }

    #[test]
    fn test_substring_matches() {
        assert!(matches_name_pattern("PGetCustomerData", "*Customer*"));
        assert!(matches_name_pattern("pgetcustomerdata", "*CUSTOMER*"));
        assert!(!matches_name_pattern("PVendorData", "*Customer*"));
        assert!(matches_name_pattern("  CustomerList  ", "  Customer*  "));
        assert!(matches_name_pattern("PGetCustomerData", "*Get*Data*"));
    }
}
