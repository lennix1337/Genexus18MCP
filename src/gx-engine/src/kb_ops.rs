//! Product-facing Knowledge Base operations served directly from UDM storage.
//!
//! This is the library backing the `kb-read` / `kb-query` / `kb-write` CLI
//! commands. MCP queries use a narrower admitted subset; native MCP reads and
//! edits remain deferred to the SDK until their public contracts are verified
//! (`src/GxMcp.Gateway/EngineCapabilityRegistry.cs`).
//!
//! The admitted space is deliberately narrow and fail-closed: reads serve
//! existing objects and their stored parts, queries filter by exact or wildcard
//! object name, object type, and recorded references. Writes replace a known
//! part while preserving raw stored compositions; against a live Knowledge Base
//! database, writes are refused unless `with_live_writes(true)` is explicitly
//! enabled on the client. Live reads, queries, and enabled live writes go
//! through `sqlcmd` and fail closed when the database cannot be accessed.

#![forbid(unsafe_code)]

use crate::catalog::ObjectKind;
use crate::read_pagination::{ReadClient, apply_default};
use crate::read_token::{ParentIdentity, UtcTicks, content_version_token};
use crate::udm::reader::{LoadedObject, ObjectRow, UdmReader, VARIABLE_IDS_PART};
#[cfg(test)]
use crate::udm::version_scope::version_from_properties;
use crate::udm::version_scope::{ActiveVersion, ModelTopologyRow, ScopeUse, select_design_model};
use crate::udm::{ObjectReadMetadata, UdmClient, UdmPartKind};
use serde::Serialize;
use std::collections::{BTreeSet, HashSet};
use std::sync::Arc;

/// Query page size when the caller does not select one.
pub const DEFAULT_QUERY_LIMIT: usize = 50;
/// Hard cap on one query page, mirroring the Gateway's response budget.
pub const MAX_QUERY_LIMIT: usize = 200;

/// One decoded source part of an object.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct PartPayload {
    pub name: String,
    pub source: String,
}

/// Result of reading one object from the active model.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct ReadResult {
    pub name: String,
    #[serde(rename = "type")]
    pub object_type: String,
    pub parts: Vec<PartPayload>,
    /// Requested parts the object does not have (names as requested).
    pub missing_parts: Vec<String>,
    /// Every stored part name of the object, so a caller can recover from a
    /// missing part without a second invocation.
    pub available_parts: Vec<String>,
    /// The SDK's `versionToken` for a single-part read, composed from the exact
    /// active-version timestamp, the parent identity and the full unpaginated
    /// projected source. Absent for a full or multi-part read, for objects
    /// outside the verified kinds (Procedure, WebPanel, Transaction), for parts
    /// whose payload is not SDK text, and for an in-memory store that holds no
    /// identity metadata.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version_token: Option<String>,
}

/// One SDK-compatible page of a single stored part.
///
/// This is the flat shape `genexus_read` returns for a single-part read. It is
/// deliberately separate from [`ReadResult`]: a full or multi-part read is not
/// this envelope and must not borrow its `versionToken`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReadPageResult {
    pub part: String,
    pub source: String,
    pub is_base64: bool,
    pub is_empty: bool,
    pub offset: usize,
    /// Lines returned in `source`, which is the SDK's `limit` field.
    pub limit: usize,
    pub total_lines: usize,
    pub total_bytes: usize,
    pub truncated: bool,
    pub is_truncated_by_worker: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub suggested_next_offset: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub suggested_next_limit: Option<usize>,
    #[serde(skip_serializing_if = "is_false")]
    pub explicit_full_read: bool,
    /// Exact SDK token over the full unpaginated projected part.
    pub version_token: String,
}

fn is_false(value: &bool) -> bool {
    !*value
}

/// The marker the Worker appends to a truncated page. The token is over the
/// unmarked full text, so this marker is presentation only.
const TRUNCATION_MARKER: &str =
    "\n\n// ... [CONTENT TRUNCATED. USE PAGINATION (offset/limit) TO READ FURTHER] ... //\n";

/// One object row of a query result.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct QueryObject {
    pub name: String,
    #[serde(rename = "type")]
    pub object_type: String,
}

/// Result of one KB object query.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct QueryResult {
    pub objects: Vec<QueryObject>,
    /// Matches before the page limit was applied.
    pub total: usize,
    pub truncated: bool,
}

/// Result of one single-part write.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct WriteResult {
    pub name: String,
    #[serde(rename = "type")]
    pub object_type: String,
    pub part: String,
    pub version_id: i32,
    pub created: bool,
    pub bytes: usize,
}

/// GeneXus-facing type name for an `ObjectKind` (what the MCP tools report).
pub fn type_name(kind: ObjectKind) -> &'static str {
    match kind {
        ObjectKind::Procedure => "Procedure",
        ObjectKind::Transaction => "Transaction",
        ObjectKind::WebPanel => "WebPanel",
        ObjectKind::StructuredDataType => "SDT",
        ObjectKind::DataProvider => "DataProvider",
        ObjectKind::DataSelector => "DataSelector",
        ObjectKind::Domain => "Domain",
        ObjectKind::Attribute => "Attribute",
        ObjectKind::Table => "Table",
        ObjectKind::Module => "Module",
        ObjectKind::DesignSystem => "DesignSystem",
        ObjectKind::DataView => "DataView",
    }
}

/// Parses a GeneXus-facing type name (case-insensitive) into an `ObjectKind`.
pub fn kind_from_type_name(name: &str) -> Option<ObjectKind> {
    match name.trim().to_ascii_uppercase().as_str() {
        "PROCEDURE" | "PROC" => Some(ObjectKind::Procedure),
        "TRANSACTION" | "TRN" => Some(ObjectKind::Transaction),
        "WEBPANEL" | "WEB_PANEL" => Some(ObjectKind::WebPanel),
        "SDT" | "STRUCTUREDDATATYPE" | "STRUCTURED_DATA_TYPE" => {
            Some(ObjectKind::StructuredDataType)
        }
        "DATAPROVIDER" => Some(ObjectKind::DataProvider),
        "DATASELECTOR" => Some(ObjectKind::DataSelector),
        "DOMAIN" => Some(ObjectKind::Domain),
        "ATTRIBUTE" => Some(ObjectKind::Attribute),
        "TABLE" => Some(ObjectKind::Table),
        "MODULE" => Some(ObjectKind::Module),
        "DESIGNSYSTEM" => Some(ObjectKind::DesignSystem),
        "DATAVIEW" => Some(ObjectKind::DataView),
        _ => None,
    }
}

/// Canonical, human-readable part name for a known part kind.
fn part_friendly_name(kind: &UdmPartKind) -> &'static str {
    match kind {
        UdmPartKind::Source => "Source",
        UdmPartKind::Rules => "Rules",
        UdmPartKind::Events => "Events",
        UdmPartKind::Conditions => "Conditions",
        UdmPartKind::Variables => "Variables",
        UdmPartKind::Structure => "Structure",
        UdmPartKind::WebForm => "WebForm",
        UdmPartKind::PatternInstance => "PatternInstance",
        UdmPartKind::Unknown(_) => "Unknown",
    }
}

mod source_projection;
use source_projection::project_sdk_source;

fn shared_client(client: &UdmClient) -> Arc<UdmClient> {
    Arc::new(client.clone())
}

/// Error from a version-scoped KB read (`read_object_scoped` / `read_object_page_scoped`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum KbReadError {
    /// Active-version or model-topology refusal before any object is read (exit code 4).
    VersionScope(String),
    /// Input validation, connection, or object/part read failure.
    Operation(String),
}

impl KbReadError {
    pub fn into_message(self) -> String {
        match self {
            Self::VersionScope(message) | Self::Operation(message) => message,
        }
    }
}

impl std::fmt::Display for KbReadError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::VersionScope(message) | Self::Operation(message) => f.write_str(message),
        }
    }
}

#[cfg(test)]
fn model_topology_in_view(
    view: &UdmClient,
    _active: Option<ActiveVersion>,
) -> Result<Vec<ModelTopologyRow>, String> {
    if view.is_live() {
        return view.model_topology();
    }
    let store = view
        .storage_for_test()
        .read()
        .map_err(|error| format!("Storage lock poisoned: {error}"))?;
    let mut model_ids: BTreeSet<i32> = store
        .model_entity_versions
        .iter()
        .map(|record| record.model_id)
        .collect();
    model_ids.insert(view.design_model_id());
    if model_ids.len() <= 1 {
        drop(store);
        return view.model_topology();
    }
    let model_entity_type = store
        .entity_types
        .iter()
        .find(|record| record.name.eq_ignore_ascii_case("Udm.Types.Model"))
        .map(|record| record.entity_type_id);
    model_ids
        .into_iter()
        .map(|model_id| {
            let stored_properties = model_entity_type.and_then(|entity_type_id| {
                store
                    .entity_versions
                    .iter()
                    .filter(|record| {
                        record.entity_type_id == entity_type_id && record.entity_id == model_id
                    })
                    .max_by_key(|record| record.entity_version_id)
                    .and_then(|record| record.properties.as_deref())
            });
            let kb_version = match stored_properties {
                Some(raw) => version_from_properties(raw)
                    .map_err(|error| format!("model {model_id}: {error}"))?,
                None if model_id == crate::udm::DEFAULT_DESIGN_MODEL_ID => None,
                None => Some(model_id),
            };
            Ok(ModelTopologyRow {
                model_id,
                parent_id: 0,
                kb_version,
            })
        })
        .collect()
}

#[cfg(not(test))]
fn model_topology_in_view(
    view: &UdmClient,
    active: Option<ActiveVersion>,
) -> Result<Vec<ModelTopologyRow>, String> {
    if view.is_live() {
        return view.model_topology();
    }
    let mut model_ids = BTreeSet::from([view.design_model_id()]);
    if let Some(active) = active
        && active.design_model_id > 0
        && active.design_model_id != view.design_model_id()
        && !view.get_active_objects(active.design_model_id)?.is_empty()
    {
        model_ids.insert(active.design_model_id);
    }
    if model_ids.len() <= 1 {
        return view.model_topology();
    }
    Ok(model_ids
        .into_iter()
        .map(|model_id| ModelTopologyRow {
            model_id,
            parent_id: 0,
            kb_version: if model_id == crate::udm::DEFAULT_DESIGN_MODEL_ID {
                None
            } else {
                Some(model_id)
            },
        })
        .collect())
}

fn with_scoped_read_view<T>(
    client: &UdmClient,
    active: Option<ActiveVersion>,
    read: impl FnOnce(&UdmClient) -> Result<T, String>,
) -> Result<T, KbReadError> {
    let mut version_scope_error = false;
    client
        .with_read_view(|view| {
            let topology = model_topology_in_view(view, active).map_err(|error| {
                version_scope_error = true;
                format!("the KB's model topology could not be read: {error}")
            })?;
            let model =
                select_design_model(&topology, active, ScopeUse::Read).map_err(|refusal| {
                    version_scope_error = true;
                    refusal.0
                })?;
            let scoped = view.clone().with_design_model_id(model);
            read(&scoped)
        })
        .map_err(|error| {
            if version_scope_error {
                KbReadError::VersionScope(error)
            } else {
                KbReadError::Operation(error)
            }
        })
}

/// Reads one existing object in a single serializable read view that also
/// selects the active KB version's Design model before reading any object rows.
pub fn read_object_scoped(
    client: &UdmClient,
    active: Option<ActiveVersion>,
    name: &str,
    parts: Option<&[String]>,
) -> Result<ReadResult, KbReadError> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return Err(KbReadError::Operation(
            "object name is required".to_string(),
        ));
    }

    with_scoped_read_view(client, active, |scoped| {
        read_object_in_view(scoped, trimmed, parts)
    })
}

/// Reads one existing object, optionally restricted to requested part names
/// (case-insensitive). Only the requested parts are decoded; a requested part
/// the object does not have is reported in `missing_parts` instead of failing
/// the read.
pub fn read_object(
    client: &UdmClient,
    name: &str,
    parts: Option<&[String]>,
) -> Result<ReadResult, String> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return Err("object name is required".to_string());
    }

    client.with_read_view(|view| read_object_in_view(view, trimmed, parts))
}

fn read_object_in_view(
    client: &UdmClient,
    trimmed: &str,
    parts: Option<&[String]>,
) -> Result<ReadResult, String> {
    let reader = UdmReader::new(shared_client(client));
    let Some(loaded) = reader.load_object_by_name(trimmed, parts)? else {
        return Err(format!(
            "object '{trimmed}' was not found in the active model"
        ));
    };
    // A refused object is never read back as if it were complete (for example, an empty Procedure).
    if let Some(reason) = &loaded.object.refusal {
        return Err(format!(
            "object '{}' is refused by the reader: {reason}",
            loaded.object.name
        ));
    }

    // `VARIABLE_IDS` is a code-generator input, not an SDK part: it is absent from
    // `available_parts`, and exposing it would make a `Variables` read return two
    // payloads where the SDK returns one.
    let selected: Vec<PartPayload> = loaded
        .object
        .source_parts
        .iter()
        .filter(|part| part.name != VARIABLE_IDS_PART)
        .map(|part| PartPayload {
            name: part.name.clone(),
            source: project_sdk_source(&part.name, &part.source),
        })
        .collect();

    let mut missing: Vec<String> = Vec::new();
    if let Some(requested) = parts {
        for want in requested {
            let want = want.trim();
            if want.is_empty() {
                continue;
            }
            let wanted_upper = want.to_ascii_uppercase();
            let stored = loaded
                .stored_parts
                .iter()
                .any(|part| part.to_ascii_uppercase() == wanted_upper);
            if !stored && !missing.iter().any(|m| m == want) {
                missing.push(want.to_string());
            }
        }
    }

    // The token belongs to the flat SDK single-part read envelope. A full read
    // of an object that happens to store one part is not that envelope, so it
    // must not borrow the token.
    let requested_single_part = parts
        .map(|requested| {
            requested
                .iter()
                .filter(|part| !part.trim().is_empty())
                .count()
                == 1
        })
        .unwrap_or(false);
    let version_token = if requested_single_part
        && selected.len() == 1
        && sdk_text_token_eligible(loaded.object.kind, &selected[0].name)
    {
        single_part_version_token(client, &loaded, &selected[0].source)?
    } else {
        None
    };

    Ok(ReadResult {
        name: loaded.object.name.clone(),
        object_type: type_name(loaded.object.kind).to_string(),
        parts: selected,
        missing_parts: missing,
        available_parts: loaded.stored_parts,
        version_token,
    })
}

/// Resolves the captured SDK Source alias for a non-generated Procedure whose
/// only text part is Rules. Other aliases need their own ordering/projection evidence.
fn read_sdk_page_part(client: &UdmClient, name: &str, part: &str) -> Result<ReadResult, String> {
    let read = read_object(client, name, Some(&[part.to_string()]))?;
    let rules_only = read
        .available_parts
        .iter()
        .filter(|p| p.as_str() == "RULES")
        .count()
        == 1
        && read
            .available_parts
            .iter()
            .all(|p| matches!(p.as_str(), "RULES" | "VARIABLES"));
    if !part.eq_ignore_ascii_case("Source")
        || read.object_type != "Procedure"
        || read.missing_parts.len() != 1
        || !read.parts.is_empty()
        || !rules_only
    {
        return Ok(read);
    }
    let alias = read_object(client, name, Some(&["Rules".to_string()]))?;
    if alias.name != read.name
        || alias.object_type != read.object_type
        || alias.available_parts != read.available_parts
    {
        return Err("object changed while resolving the SDK Source alias".into());
    }
    Ok(alias)
}

fn validate_page_read_target(name: &str, part: &str) -> Result<(), String> {
    if part.trim().is_empty() {
        return Err("part is required for a single-part page read".into());
    }
    if name.trim().is_empty() {
        return Err("object name is required".into());
    }
    Ok(())
}

/// Reads one SDK-text part page in a single serializable read view that also
/// selects the active KB version's Design model before reading any object rows.
pub fn read_object_page_scoped(
    client: &UdmClient,
    active: Option<ActiveVersion>,
    name: &str,
    part: &str,
    offset: Option<usize>,
    limit: Option<usize>,
    client_kind: ReadClient,
) -> Result<ReadPageResult, KbReadError> {
    validate_page_read_target(name, part).map_err(KbReadError::Operation)?;
    with_scoped_read_view(client, active, |scoped| {
        read_object_page_in_view(scoped, name, part, offset, limit, client_kind)
    })
}

/// Reads one SDK-text part and projects it into the flat single-part page
/// envelope `genexus_read` returns.
///
/// The token covers the complete unpaginated projected text, exactly as the
/// Worker computes it. Pagination and truncation never change the token. This
/// function serves only the text parts of Procedure, WebPanel and Transaction
/// objects, whose stored text is the SDK text verbatim; other object kinds and
/// the `Variables`, `Structure` and `WebForm` projections must stay on the SDK
/// until each has its own verified token.
pub fn read_object_page(
    client: &UdmClient,
    name: &str,
    part: &str,
    offset: Option<usize>,
    limit: Option<usize>,
    client_kind: ReadClient,
) -> Result<ReadPageResult, String> {
    validate_page_read_target(name, part)?;
    client.with_read_view(|view| {
        read_object_page_in_view(view, name, part, offset, limit, client_kind)
    })
}

fn read_object_page_in_view(
    client: &UdmClient,
    name: &str,
    part: &str,
    offset: Option<usize>,
    limit: Option<usize>,
    client_kind: ReadClient,
) -> Result<ReadPageResult, String> {
    let requested_part = part.trim();
    let read = read_sdk_page_part(client, name, requested_part)?;
    if !read.missing_parts.is_empty() {
        return Err(format!(
            "object '{}' does not have part '{}'",
            read.name, read.missing_parts[0]
        ));
    }
    if read.parts.len() != 1 {
        return Err(format!(
            "object '{}' part '{}' did not resolve to one stored part",
            read.name, requested_part
        ));
    }

    // A part name alone is insufficient: some SDK object kinds synthesize their
    // text. Only kinds verified for this envelope may claim an SDK-text token.
    if !kind_from_type_name(&read.object_type).is_some_and(sdk_text_object_kind) {
        return Err(format!(
            "object '{}' of type '{}' is not served by the native SDK-text read route",
            read.name, read.object_type
        ));
    }
    let selected = &read.parts[0];
    let kind = UdmPartKind::from_name(&selected.name);
    if !sdk_text_part(&selected.name) {
        return Err(format!(
            "part '{}' is not served by the native SDK-text read route",
            selected.name
        ));
    }
    let token = if let Some(token) = read.version_token {
        token
    } else if !client.is_live() {
        format!("in-memory-{}-{}", read.name, selected.name)
    } else {
        return Err(format!(
            "object '{}' part '{}' has no exact SDK versionToken",
            read.name, selected.name
        ));
    };

    let page = apply_default(&selected.source, offset, limit, client_kind);
    let mut source = page.content;
    if page.truncated {
        source.push_str(TRUNCATION_MARKER);
    }

    Ok(ReadPageResult {
        part: if requested_part.eq_ignore_ascii_case("Source") {
            "Source".to_string()
        } else {
            part_friendly_name(&kind).to_string()
        },
        is_base64: false,
        is_empty: source.is_empty(),
        source,
        offset: page.offset,
        limit: page.lines_returned,
        total_lines: page.total_lines,
        total_bytes: page.total_bytes,
        truncated: page.truncated,
        is_truncated_by_worker: page.truncated,
        suggested_next_offset: page.suggested_next_offset,
        suggested_next_limit: page.suggested_next_limit,
        explicit_full_read: page.explicit_full_read,
        version_token: token,
    })
}

/// Parts whose SDK text is the decoded stored text verbatim.
fn sdk_text_part(name: &str) -> bool {
    matches!(name, "SOURCE" | "EVENTS" | "RULES" | "CONDITIONS")
}

/// Object kinds verified to expose their stored text parts as SDK text.
/// The match is exhaustive on purpose: a new `ObjectKind` must be classified here before
/// it can receive a native token.
fn sdk_text_object_kind(kind: ObjectKind) -> bool {
    match kind {
        ObjectKind::Procedure | ObjectKind::WebPanel | ObjectKind::Transaction => true,
        ObjectKind::DataProvider
        | ObjectKind::DataSelector
        | ObjectKind::StructuredDataType
        | ObjectKind::Attribute
        | ObjectKind::DataView
        | ObjectKind::DesignSystem
        | ObjectKind::Domain
        | ObjectKind::Module
        | ObjectKind::Table => false,
    }
}

fn sdk_text_token_eligible(kind: ObjectKind, part_name: &str) -> bool {
    sdk_text_object_kind(kind) && sdk_text_part(part_name)
}

/// Composes the Worker's `ComputeContentVersionToken` for one part of a live
/// object. A live object whose stored identity or last update cannot be
/// established is an error: omitting the token would silently turn a
/// version-conditional edit into last-writer-wins.
fn single_part_version_token(
    client: &UdmClient,
    loaded: &LoadedObject,
    content: &str,
) -> Result<Option<String>, String> {
    let metadata = client.get_object_read_metadata(
        client.design_model_id(),
        loaded.entity_type_id,
        loaded.entity_id,
        loaded.entity_version_id,
    )?;
    let Some(metadata) = metadata else {
        if client.is_live() {
            return Err(format!(
                "object '{}' has no stored identity metadata; its SDK versionToken cannot be established",
                loaded.object.name
            ));
        }
        return Ok(None);
    };
    let parent = parent_identity(&metadata).map_err(|reason| {
        format!(
            "object '{}' versionToken parent cannot be established: {reason}",
            loaded.object.name
        )
    })?;
    let ticks = metadata
        .object_saved_ticks
        .or(metadata.model_entity_version_ticks)
        .ok_or_else(|| {
            format!(
                "object '{}' has no stored last-update timestamp; its SDK versionToken cannot be established",
                loaded.object.name
            )
        })?;
    let ticks = UtcTicks::new(ticks).map_err(|error| {
        format!(
            "object '{}' last-update timestamp is invalid: {error}",
            loaded.object.name
        )
    })?;
    Ok(Some(content_version_token(ticks, &parent, content)))
}

fn parent_identity(metadata: &ObjectReadMetadata) -> Result<ParentIdentity, String> {
    if metadata.parent.is_none() {
        return Ok(ParentIdentity::root());
    }
    if let Some(guid) = metadata
        .parent_guid
        .as_deref()
        .filter(|guid| !guid.trim().is_empty())
    {
        return ParentIdentity::guid(guid).map_err(|error| error.to_string());
    }
    if let Some(name) = metadata
        .parent_name
        .as_deref()
        .filter(|name| !name.is_empty())
    {
        return ParentIdentity::name(name).map_err(|error| error.to_string());
    }
    Err("the parent row has neither a GUID nor a name".to_string())
}

mod query_classification;
mod write;

pub use write::write_object_part;

/// Queries active objects by exact name, type and/or recorded reference.
/// Object descriptors, WebPanel version properties and references are read — no stored part is
/// decoded. Results list Module objects first, then sort by ordinal name (the
/// SDK search order), and are paged by `limit`.
pub fn query_objects(
    client: &UdmClient,
    name: Option<&str>,
    object_type: Option<&str>,
    used_by: Option<&str>,
    limit: usize,
) -> Result<QueryResult, String> {
    let kind_filter = match object_type {
        None => None,
        Some(raw) if raw.trim().is_empty() => None,
        Some(raw) => {
            Some(kind_from_type_name(raw).ok_or_else(|| format!("unknown object type '{raw}'"))?)
        }
    };
    let name_filter = name.map(str::trim).filter(|value| !value.is_empty());
    let used_by_filter = used_by.map(str::trim).filter(|value| !value.is_empty());
    let limit = if limit == 0 {
        DEFAULT_QUERY_LIMIT
    } else {
        limit.min(MAX_QUERY_LIMIT)
    };

    let reader = UdmReader::new(shared_client(client));
    let rows = reader.list_objects()?;

    // `usedby:` resolves the named target once, then keeps the objects whose
    // cross-references point at it.
    let referencing: Option<HashSet<(i32, i32)>> = match used_by_filter {
        None => None,
        Some(target) => {
            let Some(target_row) = rows
                .iter()
                .find(|row| row.name.eq_ignore_ascii_case(target))
            else {
                return Ok(QueryResult {
                    objects: Vec::new(),
                    total: 0,
                    truncated: false,
                });
            };
            let references = reader.references_for_model()?;
            Some(
                references
                    .iter()
                    .filter(|reference| {
                        reference.to_entity_type_id == target_row.entity_type_id
                            && reference.to_entity_id == target_row.entity_id
                    })
                    .map(|reference| (reference.from_entity_type_id, reference.from_entity_id))
                    .collect(),
            )
        }
    };

    let mut matched: Vec<(&ObjectRow, &'static str)> = rows
        .iter()
        .filter(|row| {
            if let Some(wanted) = name_filter {
                if !query_classification::matches_name_pattern(&row.name, wanted) {
                    return false;
                }
            }
            if let Some(kind) = kind_filter {
                if row.kind != kind {
                    return false;
                }
            }
            if let Some(referencing) = &referencing {
                if !referencing.contains(&(row.entity_type_id, row.entity_id)) {
                    return false;
                }
            }
            true
        })
        .map(|row| {
            query_classification::query_object_type(client, row)
                .map(|object_type| (row, object_type))
        })
        .collect::<Result<_, _>>()?;
    if kind_filter == Some(ObjectKind::WebPanel) {
        matched.retain(|(_, object_type)| *object_type == "WebPanel");
    }

    // The SDK search orders a term-less query by score and then by ordinal name:
    // Module rows score 1000 and every other row 1. Paging with `limit` must
    // select the same page, so the engine sorts the same way.
    matched.sort_by(|(left, _), (right, _)| {
        (left.kind != ObjectKind::Module)
            .cmp(&(right.kind != ObjectKind::Module))
            .then_with(|| left.name.cmp(&right.name))
    });

    let total = matched.len();
    let truncated = total > limit;
    let objects = matched
        .into_iter()
        .take(limit)
        .map(|(object, object_type)| QueryObject {
            name: object.name.clone(),
            object_type: object_type.to_string(),
        })
        .collect();

    Ok(QueryResult {
        objects,
        total,
        truncated,
    })
}

#[cfg(test)]
mod tests;
