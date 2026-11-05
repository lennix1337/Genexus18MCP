//! Knowledge Base object write operations.

use super::*;
use crate::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use crate::udm::UdmClient;
use crate::udm::decoder::UdmPartKind;
use crate::udm::writer::UdmWriter;

/// Replaces one known part of an existing object (or creates the object
/// when `create` selects a kind) in native UDM storage. An attempt to write an
/// unknown part is refused instead of being silently relocated. When
/// `expected_kind` is set, a mismatch is refused before any write.
pub fn write_object_part(
    client: &UdmClient,
    name: &str,
    part: &str,
    content: &str,
    create: Option<ObjectKind>,
    expected_kind: Option<ObjectKind>,
) -> Result<WriteResult, String> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return Err("object name is required".to_string());
    }

    let trimmed_part = part.trim();
    let part_kind = UdmPartKind::from_name(trimmed_part);
    if matches!(part_kind, UdmPartKind::Unknown(_)) {
        return Err(format!(
            "part '{trimmed_part}' is not a writable engine part (Source, Rules, Events, Conditions, Variables, Structure, WebForm, PatternInstance)"
        ));
    }

    let clean_content = content.strip_prefix('\u{FEFF}').unwrap_or(content);

    // The engine writer mutates the in-process store only unless live writes are
    // explicitly enabled; an unconfigured or test live client would keep its old
    // version. Refusing here is what keeps a reported write from silently not existing.
    if client.is_live() && !client.live_writes_enabled() {
        return Err(format!(
            "object '{trimmed}' resolves to a live Knowledge Base database and engine UDM writes are not implemented for live storage; refusing a write that would not persist (the SDK path must serve this edit)"
        ));
    }

    let writer = UdmWriter::new(shared_client(client));

    let existing = writer.load_raw_object(trimmed)?;
    let created = existing.is_none();
    let mut object = match existing {
        Some(object) => object,
        None => {
            let kind = create
                .ok_or_else(|| format!("object '{trimmed}' was not found in the active model"))?;
            KbObject::new(ObjectId::new(trimmed)?, trimmed.to_string(), kind)
        }
    };

    if let Some(expected) = expected_kind {
        if object.kind != expected {
            return Err(format!(
                "object '{}' is a {}, not a {}",
                object.name,
                type_name(object.kind),
                type_name(expected)
            ));
        }
    }

    if let Some(unknown) = object.source_parts.iter().find(|stored| {
        matches!(
            UdmPartKind::from_name(&stored.name),
            UdmPartKind::Unknown(_)
        )
    }) {
        return Err(format!(
            "object '{}' has part '{}' outside engine coverage; refusing a partial save",
            object.name, unknown.name
        ));
    }

    let canonical = part_kind.name().to_string();
    match object
        .source_parts
        .iter_mut()
        .find(|stored| stored.name.eq_ignore_ascii_case(&canonical))
    {
        Some(stored) => stored.source = clean_content.to_string(),
        None => object.source_parts.push(SourcePart {
            name: canonical,
            source: clean_content.to_string(),
        }),
    }

    let identity = writer.save_object(&object)?;
    Ok(WriteResult {
        name: identity.name,
        object_type: type_name(object.kind).to_string(),
        part: part_friendly_name(&part_kind).to_string(),
        version_id: identity.version_id,
        created,
        bytes: clean_content.len(),
    })
}
