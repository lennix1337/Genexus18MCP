//! Decodes the stored structure of a native Knowledge Base Structured Data Type (SDT) into a tree of
//! portable fields and levels.
//!
//! The served `STRUCTURE` part is GeneXus XML: a `<Level Id>` root whose children are `<Item Id>`
//! leaves and nested `<Level Id>` nodes. Each id names one row of the SDT's `EntityVersion`
//! table: a level (EntityType 151, `SDTLevelEntity`) or an item (EntityType 152, `SDTItemEntity`).
//! The type of a node lives in that row's `EntityVersionProperties` (`<Properties>`), not in the
//! XML. This module reads the tree and each node's stored properties with the same strict rules as
//! the VARIABLES decoder (`stored_variables`), so an SDT is decoded by one set of rules.
//!
//! A node whose stored type this module does not prove is refused by name, with the SDT and the
//! item. Nothing is guessed, and an item is never dropped. The mapping and its evidence are in
//! `docs/udm_storage_notes.md` ("SDT structure").

use std::collections::BTreeMap;

use roxmltree::{Document, Node, ParsingOptions};

use crate::model::DataType;
use crate::udm::stored_variables::{
    self, NO_BASED_ON, SDT_TYPE_GUID, attcustomtype, decode_type, structure_ref,
};

/// The most nodes one SDT may carry. The stored SDTs carry at most a few hundred.
pub(crate) const MAX_NODES: usize = 4096;

/// The largest structure part this module reads.
const MAX_XML_BYTES: usize = 8 * 1024 * 1024;

/// One node of a decoded SDT: a field, or a level (a nested record or a collection of records).
#[derive(Debug, Clone, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(tag = "node", rename_all = "snake_case", deny_unknown_fields)]
pub enum SdtNode {
    /// A scalar, or a reference to another SDT. `data_type` is the portable type.
    Field { name: String, data_type: DataType },
    /// A nested level. `collection` marks a collection of `children` records; `item_name` is the
    /// class name of one item of the collection.
    Level {
        name: String,
        collection: bool,
        item_name: Option<String>,
        children: Vec<SdtNode>,
    },
}

/// The portable JSON the SDT emitter reads: the top-level nodes of the SDT.
#[derive(Debug, Clone, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PortableSdt {
    pub fields: Vec<SdtNode>,
    /// The item name of a collection root (see `SdtStructure::root_collection`). Absent for a record.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub root_collection: Option<String>,
    /// The XML namespace the SDK writes on the SDT's classes: its stored `ExternalNamespace`, else the
    /// Knowledge Base's name. Absent when neither was read.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub xml_namespace: Option<String>,
}

impl SdtStructure {
    /// The portable JSON of the structure. The emitter reads this form, never the stored XML.
    pub fn to_portable_json(&self) -> Result<String, String> {
        self.to_portable_json_in(None)
    }

    /// The portable JSON of the structure, with the SDT's XML namespace when it is known.
    pub fn to_portable_json_in(&self, xml_namespace: Option<&str>) -> Result<String, String> {
        serde_json::to_string(&PortableSdt {
            fields: self.nodes.clone(),
            root_collection: self.root_collection.clone(),
            xml_namespace: xml_namespace.map(str::to_string),
        })
        .map_err(|e| format!("cannot write the SDT structure: {e}"))
    }
}

/// The decoded structure of one SDT.
#[derive(Debug, Clone, PartialEq)]
pub struct SdtStructure {
    pub nodes: Vec<SdtNode>,
    /// Entity ids of the SDTs the structure uses (a record field), each an explicit `Uses`.
    pub sdt_entity_ids: Vec<i32>,
    /// Entity ids of the domains the structure is based on, each an explicit `BasedOn`.
    pub domain_entity_ids: Vec<i32>,
    /// Every stored `<Level Id>` of the structure, in document order. A Procedure variable typed by a
    /// nested level names one of these by its entity id.
    pub levels: Vec<StoredLevel>,
    /// The item name of the SDT's root, when the root itself is a collection (`AttCollection True`).
    /// The SDK then has no class for the root: the SDT is its item class `<SDT>_<item>`, holding the
    /// items under the root directly. `None` for a record root.
    pub root_collection: Option<String>,
}

/// One stored level of an SDT, as a Procedure variable names it: its entity id, the C# class the SDK
/// emits for it (without the `Sdt` prefix, as `DataType::Record` takes it), its dotted GeneXus name,
/// and whether it is one item of a collection.
///
/// The naming is the one `csharp::sdt` emits: a non-collection level is `<holder>_<name>`; a collection
/// level's item is `<holder>_<item name>`, where the item name is the stored `idCollectionItemName`
/// when there is one, else `<name>Item`. The dotted name joins the same segments with `.`.
#[derive(Debug, Clone, PartialEq)]
pub struct StoredLevel {
    pub id: i32,
    pub schema: String,
    pub dotted: String,
    pub collection_item: bool,
}

/// What the decoder needs to know about the Knowledge Base for one SDT.
pub struct Lookup<'a> {
    /// The `EntityVersionProperties` of a level or item row, or `None` when the row is missing.
    pub node_properties: &'a dyn Fn(i32) -> Option<String>,
    /// The model name of the SDT with the given entity id.
    pub sdt_name: &'a dyn Fn(i32) -> Option<String>,
    /// The stored portable type of the domain with the given entity id, and its entity id.
    pub domain_type: &'a dyn Fn(i32) -> Option<DataType>,
}

/// Decodes one SDT's served `STRUCTURE` part. `sdt` is the SDT's name, for the error messages.
pub fn decode(xml: &str, sdt: &str, lookup: &Lookup<'_>) -> Result<SdtStructure, String> {
    if xml.len() > MAX_XML_BYTES {
        return Err(format!(
            "STRUCTURE of '{sdt}' is {} bytes, above the {MAX_XML_BYTES}-byte limit",
            xml.len()
        ));
    }
    let document = Document::parse_with_options(
        xml,
        ParsingOptions {
            allow_dtd: false,
            ..ParsingOptions::default()
        },
    )
    .map_err(|e| format!("STRUCTURE of '{sdt}' is not well formed XML: {e}"))?;
    let root = document.root_element();
    if root.tag_name().name() != "Level" {
        return Err(format!(
            "STRUCTURE of '{sdt}' root is <{}>, expected <Level>",
            root.tag_name().name()
        ));
    }
    let mut out = SdtStructure {
        nodes: Vec::new(),
        sdt_entity_ids: Vec::new(),
        domain_entity_ids: Vec::new(),
        levels: Vec::new(),
        root_collection: None,
    };
    let mut count = 0usize;
    let (holder, path) = root_holder(root, sdt, lookup, &mut out)?;
    out.nodes = children(root, sdt, lookup, &mut out, &mut count, &holder, &path)?;
    if out.nodes.is_empty() {
        return Err(format!("STRUCTURE of '{sdt}' has no items"));
    }
    Ok(out)
}

/// The nodes under one `<Level>` (or the root), decoded in document order. `holder` is the schema
/// (without `Sdt`) of the class that holds these nodes; `path` is the dotted GeneXus name of that class.
fn children(
    parent: Node<'_, '_>,
    sdt: &str,
    lookup: &Lookup<'_>,
    out: &mut SdtStructure,
    count: &mut usize,
    holder: &str,
    path: &str,
) -> Result<Vec<SdtNode>, String> {
    let mut nodes = Vec::new();
    for child in parent.children().filter(|n| n.is_element()) {
        *count += 1;
        if *count > MAX_NODES {
            return Err(format!("STRUCTURE of '{sdt}' exceeds {MAX_NODES} nodes"));
        }
        let id = node_id(child, sdt)?;
        match child.tag_name().name() {
            "Item" => {
                if child.children().any(|n| n.is_element()) {
                    return Err(format!("item {id} of '{sdt}' has child elements"));
                }
                nodes.push(item(id, sdt, lookup, out)?);
            }
            "Level" => {
                let props = level_properties(id, sdt, lookup)?;
                let name = props
                    .get("Name")
                    .cloned()
                    .ok_or_else(|| format!("level {id} of '{sdt}' has no Name"))?;
                let collection = is_collection(&props, &name, sdt)?;
                let item_name = props.get("idCollectionItemName").cloned();
                // The class this level's own children belong to: the item class of a collection, the
                // level's class otherwise. Same naming as `csharp::sdt::emit_class`.
                let (schema, dotted, collection_item) = if collection {
                    let segment = item_name.clone().unwrap_or_else(|| format!("{name}Item"));
                    (
                        format!("{holder}_{segment}"),
                        format!("{path}.{segment}"),
                        true,
                    )
                } else {
                    (format!("{holder}_{name}"), format!("{path}.{name}"), false)
                };
                out.levels.push(StoredLevel {
                    id,
                    schema: schema.clone(),
                    dotted: dotted.clone(),
                    collection_item,
                });
                let grandchildren = children(child, sdt, lookup, out, count, &schema, &dotted)?;
                if grandchildren.is_empty() {
                    return Err(format!("level '{name}' of '{sdt}' has no items"));
                }
                nodes.push(SdtNode::Level {
                    name,
                    collection,
                    item_name,
                    children: grandchildren,
                });
            }
            other => {
                return Err(format!("unsupported element <{other}> in '{sdt}'"));
            }
        }
    }
    Ok(nodes)
}

fn node_id(node: Node<'_, '_>, sdt: &str) -> Result<i32, String> {
    let mut id: Option<&str> = None;
    for attr in node.attributes() {
        if attr.name() != "Id" {
            return Err(format!(
                "<{}> of '{sdt}' has unsupported attribute '{}'",
                node.tag_name().name(),
                attr.name()
            ));
        }
        id = Some(attr.value());
    }
    let id = id.ok_or_else(|| format!("<{}> of '{sdt}' has no Id", node.tag_name().name()))?;
    id.parse::<i32>().map_err(|_| {
        format!(
            "<{}> Id '{id}' of '{sdt}' is not a number",
            node.tag_name().name()
        )
    })
}

fn level_properties(
    id: i32,
    sdt: &str,
    lookup: &Lookup<'_>,
) -> Result<BTreeMap<String, String>, String> {
    let raw = (lookup.node_properties)(id)
        .ok_or_else(|| format!("level {id} of '{sdt}' has no stored row"))?;
    stored_variables::property_map(&raw).map_err(|e| format!("level {id} of '{sdt}': {e}"))
}

/// Whether a level is a collection, from its stored `AttCollection`: absent or `False` is a record,
/// `True` a collection, any other value is refused.
fn is_collection(props: &BTreeMap<String, String>, name: &str, sdt: &str) -> Result<bool, String> {
    match props.get("AttCollection").map(String::as_str) {
        None | Some("False") => Ok(false),
        Some("True") => Ok(true),
        Some(other) => Err(format!(
            "level '{name}' of '{sdt}': invalid AttCollection value '{other}'"
        )),
    }
}

/// The item name of a collection level: its stored `idCollectionItemName`, or `<name>Item` when none is
/// stored (the same default `csharp::sdt` writes).
fn item_segment(props: &BTreeMap<String, String>, name: &str) -> String {
    props
        .get("idCollectionItemName")
        .cloned()
        .unwrap_or_else(|| format!("{name}Item"))
}

/// The holder (schema, without `Sdt`) and the dotted path of the root's items. A record root's items
/// belong to the SDT itself. A collection root's items belong to its item class `<SDT>_<item>`, which is
/// recorded as the root's level, so a Procedure variable typed by the root names that class.
///
/// A root whose stored row is absent is a record: the row is read only to tell a collection apart.
fn root_holder(
    root: Node<'_, '_>,
    sdt: &str,
    lookup: &Lookup<'_>,
    out: &mut SdtStructure,
) -> Result<(String, String), String> {
    let id = node_id(root, sdt)?;
    let Some(raw) = (lookup.node_properties)(id) else {
        return Ok((sdt.to_string(), sdt.to_string()));
    };
    let props =
        stored_variables::property_map(&raw).map_err(|e| format!("level {id} of '{sdt}': {e}"))?;
    let name = props
        .get("Name")
        .cloned()
        .unwrap_or_else(|| sdt.to_string());
    if !is_collection(&props, &name, sdt)? {
        return Ok((sdt.to_string(), sdt.to_string()));
    }
    let item = item_segment(&props, &name);
    let schema = format!("{sdt}_{item}");
    let dotted = format!("{sdt}.{item}");
    out.levels.push(StoredLevel {
        id,
        schema: schema.clone(),
        dotted: dotted.clone(),
        collection_item: true,
    });
    out.root_collection = Some(item);
    Ok((schema, dotted))
}

/// The item name of an SDT whose root is a collection, or `None` for a record root. `node_properties`
/// is the stored row of each node of the structure, as `decode` reads it. Only the root is read, so
/// this is cheap enough to run over every SDT of a model.
pub fn root_collection_item(
    xml: &str,
    sdt: &str,
    node_properties: &dyn Fn(i32) -> Option<String>,
) -> Result<Option<String>, String> {
    if xml.len() > MAX_XML_BYTES {
        return Err(format!(
            "STRUCTURE of '{sdt}' is {} bytes, above the {MAX_XML_BYTES}-byte limit",
            xml.len()
        ));
    }
    let document = Document::parse_with_options(
        xml,
        ParsingOptions {
            allow_dtd: false,
            ..ParsingOptions::default()
        },
    )
    .map_err(|e| format!("STRUCTURE of '{sdt}' is not well formed XML: {e}"))?;
    let root = document.root_element();
    if root.tag_name().name() != "Level" {
        return Err(format!(
            "STRUCTURE of '{sdt}' root is <{}>, expected <Level>",
            root.tag_name().name()
        ));
    }
    let id = node_id(root, sdt)?;
    let Some(raw) = node_properties(id) else {
        return Ok(None);
    };
    let props =
        stored_variables::property_map(&raw).map_err(|e| format!("level {id} of '{sdt}': {e}"))?;
    let name = props
        .get("Name")
        .cloned()
        .unwrap_or_else(|| sdt.to_string());
    if is_collection(&props, &name, sdt)? {
        Ok(Some(item_segment(&props, &name)))
    } else {
        Ok(None)
    }
}

/// One item: its stored type, decoded to a portable field.
fn item(
    id: i32,
    sdt: &str,
    lookup: &Lookup<'_>,
    out: &mut SdtStructure,
) -> Result<SdtNode, String> {
    let raw = (lookup.node_properties)(id)
        .ok_or_else(|| format!("item {id} of '{sdt}' has no stored row"))?;
    let props =
        stored_variables::property_map(&raw).map_err(|e| format!("item {id} of '{sdt}': {e}"))?;
    let name = props
        .get("Name")
        .cloned()
        .ok_or_else(|| format!("item {id} of '{sdt}' has no Name"))?;
    let refuse = |reason: String| format!("item '{name}' of '{sdt}': {reason}");

    if props.get("AttCollection").map(String::as_str) == Some("True") {
        // The SDK reports a collection item as a collection (`Route.advisoryNotices`,
        // `FacebookAccessToken.Permissions`), but no captured SDK C# shows its emitted form, so the
        // item is refused by name rather than guessed (docs/udm_storage_notes.md).
        return Err(refuse(
            "a collection item is not mapped by the item decoder: no captured SDK C# form for an item-level collection (docs/udm_storage_notes.md)".into(),
        ));
    }

    let based_on = props
        .get("idBasedOn")
        .map(String::as_str)
        .filter(|value| *value != NO_BASED_ON);
    let custom = props
        .get("ATTCUSTOMTYPE")
        .map(String::as_str)
        .filter(|value| !value.trim().is_empty());

    let data_type = match (based_on, custom) {
        (Some(target), None) => {
            let domain_id = domain_id_of(target).map_err(refuse)?;
            let Some(data_type) = (lookup.domain_type)(domain_id) else {
                return Err(refuse(format!(
                    "based on '{target}': the domain has no stored type this engine maps"
                )));
            };
            out.domain_entity_ids.push(domain_id);
            data_type
        }
        (Some(target), Some(_)) => {
            return Err(refuse(format!(
                "based on '{target}' together with a declared type is not mapped"
            )));
        }
        (None, None) => untyped_item(&props, lookup.sdt_name).map_err(refuse)?,
        (None, Some(fragment)) => {
            let (code, reference) = attcustomtype(fragment).map_err(refuse)?;
            let row = evidenced_length(&code, &props);
            let data_type =
                decode_type(&code, reference.as_deref(), &row, lookup.sdt_name).map_err(refuse)?;
            if let DataType::Record { .. } = data_type {
                let reference = reference
                    .ok_or_else(|| refuse("SDT item has no StructureTypeReference".into()))?;
                let (type_guid, sdt_id) = structure_ref(&reference).map_err(refuse)?;
                if !type_guid.eq_ignore_ascii_case(SDT_TYPE_GUID) {
                    return Err(refuse(format!(
                        "structure type {type_guid} is not an SDT type this engine reads (nested SDT levels are not mapped)"
                    )));
                }
                out.sdt_entity_ids.push(sdt_id);
            }
            data_type
        }
    };
    Ok(SdtNode::Field { name, data_type })
}

/// The length the SDK gives a typed SDT item whose stored row carries no length, no `AttMaxLen` and no
/// decimals. Character and Varchar: the SDK text of the same items reads `CHARACTER(20)` (8 items,
/// `AnalyticsPurchase.CurrencyCode`) and `VARCHAR(40)` (23 items, `AnalyticsKeyValue.Key`). Numeric: the SDK
/// read of `RustRecordProbe1015.Amount` (`ATTCUSTOMTYPE` 4, stored row 292 bytes with only Name, OBJ_TYPE and
/// ATTCUSTOMTYPE) is `NUMERIC(4)` with Decimals 0 (`Length = 4`, `Decimals = 0` on the SDK read, GX18). A row
/// with any of those stored values keeps them, so a Decimals with no Length stays refused; other codes
/// are returned unchanged.
pub(crate) fn evidenced_length(
    code: &str,
    props: &BTreeMap<String, String>,
) -> BTreeMap<String, String> {
    let sdk_length = match code {
        "4" => "4",
        "5" => "20",
        "13" => "40",
        _ => return props.clone(),
    };
    let carries_length = ["Length", "AttMaxLen", "Decimals"]
        .iter()
        .any(|key| props.contains_key(*key));
    let mut row = props.clone();
    if !carries_length {
        row.insert("Length".to_string(), sdk_length.to_string());
    }
    row
}

/// An item with neither an `ATTCUSTOMTYPE` nor an `idBasedOn`. The SDK text gives `NUMERIC(Length)`
/// for such items, and `NUMERIC(4)` when the row has no length: 6 items with a length (`NUMERIC(8)`,
/// `NUMERIC(18)`, `NUMERIC(2)`) and 6 without (`NUMERIC(4)`). Decimals on such a row have no SDK
/// evidence, so the item is refused by name. Collections are refused before this point.
fn untyped_item(
    props: &BTreeMap<String, String>,
    sdt_name: &dyn Fn(i32) -> Option<String>,
) -> Result<DataType, String> {
    if props.contains_key("Decimals") {
        return Err(
            "the item has no stored type (no ATTCUSTOMTYPE and no idBasedOn) and carries decimals; no SDK form is evidenced for that"
                .into(),
        );
    }
    let mut row = props.clone();
    row.entry("Length".to_string())
        .or_insert_with(|| "4".to_string());
    decode_type("4", None, &row, sdt_name)
}

/// The domain type GUID of an `idBasedOn` value (`<domain type GUID>-<entity id>`). Every based-on
/// item on the evidence KBs points at this type, the Domain root (EntityType 33).
const DOMAIN_TYPE_GUID: &str = "00972a17-9975-449e-aab1-d26165d51393";

/// The domain entity id of an `idBasedOn` value. The GUID must be the Domain type GUID: any other
/// based-on type is refused, not read as a domain.
/// The Domain entity id of an `idBasedOn` reference (`<Domain GUID>-<id>`). Shared by the SDT and
/// the VARIABLES decoders, so both read a based-on reference by one rule.
pub(crate) fn domain_id_of(value: &str) -> Result<i32, String> {
    let (guid, id) = value
        .rsplit_once('-')
        .ok_or_else(|| format!("based on '{value}': not a domain reference"))?;
    // A GUID has four internal dashes, so the split must leave exactly the 36-character GUID.
    if guid.len() != 36 || !guid.eq_ignore_ascii_case(DOMAIN_TYPE_GUID) {
        return Err(format!("based on '{value}': not a Domain reference"));
    }
    id.parse::<i32>()
        .map_err(|_| format!("based on '{value}': the domain id is not a number"))
}
