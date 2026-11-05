//! Portable, SDK-independent KB catalog authored for gx-engine.
//!
//! This is not a decoder or writer for GeneXus native KB storage. It models stable
//! object identities, source parts, and explicit references in a bounded JSON file.

mod native;

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

const FORMAT_VERSION: u32 = 1;
pub(crate) const MAX_CATALOG_BYTES: usize = 8 * 1024 * 1024;
const MAX_OBJECTS: usize = 4096;
const MAX_OBJECT_NAME_BYTES: usize = 128;
const MAX_SOURCE_PARTS: usize = 128;
// Raised from 1 MiB: the real 1.3 MB Procedure Plan068Proc in KBTeste is a legitimate
// object that the old cap rejected. The cap still bounds decompression: the UDM decoder
// refuses any blob whose declared size exceeds it before inflating.
pub const MAX_SOURCE_PART_BYTES: usize = 8 * 1024 * 1024;
const MAX_REFERENCES_PER_OBJECT: usize = 1024;
const MAX_REFERENCES: usize = 65_536;

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

/// Stable, opaque identity independent of an object's mutable name.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(transparent)]
pub struct ObjectId(String);

impl ObjectId {
    /// Creates a canonical lowercase identity from ASCII letters, digits, `_`, and `-`.
    pub fn new(value: impl Into<String>) -> Result<Self, String> {
        let mut value = value.into();
        validate_token(&value, "object identity", true)?;
        value.make_ascii_lowercase();
        Ok(Self(value))
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ObjectKind {
    Attribute,
    DataProvider,
    DataSelector,
    DataView,
    DesignSystem,
    Domain,
    Module,
    Procedure,
    StructuredDataType,
    Table,
    Transaction,
    WebPanel,
}

impl ObjectKind {
    /// Why objects of this kind are not compile units of the portable engine, or `None` when
    /// they are one. Web UI (Transaction, WebPanel) and the code generators this engine has no
    /// generator for (DataProvider, DataSelector, DataView) are never built. A rebuild reports
    /// them by name; a build or run that targets or needs one is refused.
    pub fn not_compile_unit_reason(&self) -> Option<&'static str> {
        match self {
            ObjectKind::Transaction | ObjectKind::WebPanel => Some("web UI, not generated"),
            ObjectKind::DataProvider | ObjectKind::DataSelector | ObjectKind::DataView => {
                Some("no generator, not generated")
            }
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReferenceKind {
    BasedOn,
    Calls,
    Contains,
    Uses,
}

/// A named authored source part. Part names are canonicalized to ASCII uppercase.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SourcePart {
    pub name: String,
    pub source: String,
}

/// An explicitly typed dependency on another stable object identity.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Reference {
    pub kind: ReferenceKind,
    pub target: ObjectId,
}

/// One object in the portable catalog. The catalog validates and sorts its collections.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct KbObject {
    pub id: ObjectId,
    pub name: String,
    pub kind: ObjectKind,
    #[serde(default)]
    pub source_parts: Vec<SourcePart>,
    #[serde(default)]
    pub references: Vec<Reference>,
    /// The Knowledge Base root module, identified by the reader from its owner data.
    /// Only this flag exempts a Module from the ASCII name rule, and only under the
    /// name `SYSTEM_ROOT_MODULE_NAME`. Objects it owns carry no module segment.
    #[serde(default, skip_serializing_if = "is_false")]
    pub system_root: bool,
    /// Why the reader could not model this object faithfully, when it could not. A refused object
    /// stays in the catalog, so every caller that depends on it still sees the dependency. Every
    /// build or run that targets or needs it is refused by name; it is never emitted empty.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub refusal: Option<String>,
    /// Why the Knowledge Base says this object is not generated, when it does. Such an object is
    /// neither built nor an error: the run reports it as skipped, and a caller that needs it is
    /// refused by name. Procedures only: `GenerateObject` is False. A Procedure with no SOURCE part is refused, not skipped.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub not_generated: Option<String>,
    /// Whether the object belongs to the GeneXus library: a module the GeneXus runtime ships (`GeneXus`,
    /// `GeneXusUnanimo`, top-level under the root) or an object inside one. The reader sets it from the model's
    /// parent chain. A structured data type of the library that no generated object reaches is not targeted
    /// (see `crate::reachability`). A catalog without the flag targets every object, as before.
    #[serde(default, skip_serializing_if = "is_false")]
    pub library: bool,
    /// A Procedure's stored `IsMain` ("Main program") property, when the reader read it. A Procedure stored
    /// as not main is generated only when a generated object reaches it (see `crate::reachability`); `None`
    /// keeps every Procedure a root, as a catalog read before the reader recorded it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub main: Option<bool>,
    /// The names of the Modules that hold the object, outermost first, without the root module
    /// (`["General", "UI"]` for `General.UI.MasterPrompt`). Empty for an object under the root
    /// module and for a catalog read before the reader recorded it.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub module_path: Vec<String>,
}

/// The display name GeneXus gives the Knowledge Base root module. It contains a space,
/// so it is valid only as the flagged system root (see `KbObject::system_root`).
pub const SYSTEM_ROOT_MODULE_NAME: &str = "Root Module";

fn is_false(value: &bool) -> bool {
    !*value
}

impl KbObject {
    pub fn new(id: ObjectId, name: impl Into<String>, kind: ObjectKind) -> Self {
        Self {
            id,
            name: name.into(),
            kind,
            source_parts: Vec::new(),
            references: Vec::new(),
            system_root: false,
            refusal: None,
            not_generated: None,
            library: false,
            main: None,
            module_path: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Edit {
    /// Adds an object or replaces the object with the same stable identity.
    Upsert(KbObject),
    Rename {
        id: ObjectId,
        name: String,
    },
    /// `source: None` removes the named part; `Some` adds or replaces it.
    SetSourcePart {
        id: ObjectId,
        name: String,
        source: Option<String>,
    },
    SetReferences {
        id: ObjectId,
        references: Vec<Reference>,
    },
    /// Fails if any remaining object references this identity.
    Remove {
        id: ObjectId,
    },
}

/// In-memory authored catalog. Mutations validate a complete candidate before publication.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Catalog {
    revision: u64,
    objects: BTreeMap<ObjectId, KbObject>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct CatalogFile {
    format_version: u32,
    revision: u64,
    /// A vector allows the loader to reject duplicate IDs instead of losing them in a JSON map.
    objects: Vec<KbObject>,
}

/// The module that a single-object closure accepts as namespace membership, never as code. The evidence is
/// one KB: DCM37961700 on KBTeste, a Uses reference to the `Default` module (entity type 52, id 1). The name
/// rule is an inference from that evidence, not a general rule; any other module is refused by name.
pub const CLOSURE_NAMESPACE_MODULE: &str = "Default";

/// What a single-object closure builds from: the entry and the compile units it reaches, and the namespace
/// modules those objects are in. Both sets are sorted by id.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClosurePlan {
    pub members: Vec<ObjectId>,
    pub namespace_modules: Vec<ObjectId>,
}

/// Plans the closure of one entry over a catalog that holds the entry and every object it reaches.
///
/// The walk follows each reference from the entry. The root module is exempt. A Module target is namespace
/// membership: only `CLOSURE_NAMESPACE_MODULE` is accepted, every other module is refused by name. A
/// containment reference is refused. A target the catalog does not hold, and a web UI or not-generated
/// object, are refused with the path that reached them. Nothing is dropped. The
/// entry must be a Procedure or a structured data type.
pub fn plan_single_object_closure(
    catalog: &Catalog,
    entry: &ObjectId,
) -> Result<ClosurePlan, String> {
    let start = catalog
        .get(entry)
        .ok_or_else(|| format!("closure entry '{}' is not in the catalog", entry.as_str()))?;
    if !matches!(
        start.kind,
        ObjectKind::Procedure | ObjectKind::StructuredDataType
    ) {
        return Err(format!(
            "closure entry '{}' is a {:?}: a closure starts at a Procedure or a structured data type",
            start.name, start.kind
        ));
    }
    let mut members = BTreeSet::new();
    let mut namespace_modules = BTreeSet::new();
    members.insert(entry.clone());
    let mut path = vec![start.name.clone()];
    visit_closure(
        catalog,
        entry,
        &mut path,
        &mut members,
        &mut namespace_modules,
    )?;
    Ok(ClosurePlan {
        members: members.into_iter().collect(),
        namespace_modules: namespace_modules.into_iter().collect(),
    })
}

fn visit_closure(
    catalog: &Catalog,
    id: &ObjectId,
    path: &mut Vec<String>,
    members: &mut BTreeSet<ObjectId>,
    namespace_modules: &mut BTreeSet<ObjectId>,
) -> Result<(), String> {
    let Some(object) = catalog.get(id) else {
        return Err(format!(
            "{}: '{}' is not in the closure; refused",
            path.join(" -> "),
            id.as_str()
        ));
    };
    for reference in &object.references {
        let target_id = &reference.target;
        if target_id.as_str() == crate::udm::reader::SYSTEM_ROOT_OBJECT_ID {
            continue;
        }
        let here = path.join(" -> ");
        if reference.kind == ReferenceKind::Contains {
            return Err(format!(
                "{here} -> {:?} '{}': containment not evidenced; refused",
                reference.kind,
                target_id.as_str()
            ));
        }
        let Some(target) = catalog.get(target_id) else {
            return Err(format!(
                "{here} -> {:?} '{}': the target is not in the closure; refused",
                reference.kind,
                target_id.as_str()
            ));
        };
        if target.kind == ObjectKind::Module {
            if target.name == CLOSURE_NAMESPACE_MODULE {
                namespace_modules.insert(target_id.clone());
                continue;
            }
            return Err(format!(
                "{here} -> module '{}': namespace not evidenced; refused",
                target.name
            ));
        }
        let blocked = target
            .refusal
            .as_deref()
            .or(target.not_generated.as_deref())
            .or(target.kind.not_compile_unit_reason());
        if let Some(reason) = blocked {
            return Err(format!(
                "{here} -> {:?} '{}' ({reason}); refused",
                target.kind, target.name
            ));
        }
        if members.insert(target_id.clone()) {
            path.push(target.name.clone());
            visit_closure(catalog, target_id, path, members, namespace_modules)?;
            path.pop();
        }
    }
    Ok(())
}

/// The first reference of `object` that a single-object catalog cannot resolve, if any.
///
/// A single-object rebuild emits the object alone, so every reference that emits code must resolve to
/// the object itself. The root module is exempt: a Module emits nothing, and every object refers to it.
pub fn single_object_gap(object: &KbObject) -> Option<ObjectId> {
    object
        .references
        .iter()
        .map(|reference| &reference.target)
        .find(|target| {
            target.as_str() != crate::udm::reader::SYSTEM_ROOT_OBJECT_ID && **target != object.id
        })
        .cloned()
}

impl Catalog {
    pub fn empty() -> Self {
        Self {
            revision: 0,
            objects: BTreeMap::new(),
        }
    }

    pub fn from_objects_map(objects: BTreeMap<ObjectId, KbObject>) -> Self {
        Self {
            revision: 1,
            objects,
        }
    }

    pub fn revision(&self) -> u64 {
        self.revision
    }

    pub fn get(&self, id: &ObjectId) -> Option<&KbObject> {
        self.objects.get(id)
    }

    pub fn objects(&self) -> impl Iterator<Item = &KbObject> {
        self.objects.values()
    }

    /// Parses one bounded portable catalog, rejecting unknown fields and duplicate identities.
    pub fn from_json(input: &str) -> Result<Self, String> {
        if input.len() > MAX_CATALOG_BYTES {
            return Err(format!(
                "catalog JSON exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        let file: CatalogFile = serde_json::from_str(input)
            .map_err(|error| format!("invalid catalog JSON: {error}"))?;
        if file.format_version != FORMAT_VERSION {
            return Err(format!(
                "unsupported catalog format_version {}; expected {FORMAT_VERSION}",
                file.format_version
            ));
        }
        if file.objects.len() > MAX_OBJECTS {
            return Err(format!("catalog exceeds the {MAX_OBJECTS}-object limit"));
        }

        let mut objects = BTreeMap::new();
        for object in file.objects {
            let id = canonical_id(object.id.clone())?;
            let mut object = object;
            object.id = id.clone();
            if objects.insert(id.clone(), object).is_some() {
                return Err(format!("duplicate object identity '{}'", id.as_str()));
            }
        }
        let mut catalog = Self {
            revision: file.revision,
            objects,
        };
        catalog.normalize_and_validate()?;
        Ok(catalog)
    }

    /// Serializes validated data in stable identity/name/part/reference order.
    pub fn to_json(&self) -> Result<String, String> {
        let mut catalog = self.clone();
        catalog.normalize_and_validate()?;
        let file = CatalogFile {
            format_version: FORMAT_VERSION,
            revision: catalog.revision,
            objects: catalog.objects.into_values().collect(),
        };
        let output = serde_json::to_string(&file)
            .map_err(|error| format!("could not serialize catalog: {error}"))?;
        if output.len() > MAX_CATALOG_BYTES {
            return Err(format!(
                "serialized catalog exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        Ok(output)
    }

    /// Applies all edits as one optimistic transaction. Failure leaves this catalog unchanged.
    pub fn apply(&mut self, expected_revision: u64, edits: &[Edit]) -> Result<u64, String> {
        if expected_revision != self.revision {
            return Err(format!(
                "stale catalog revision: expected {expected_revision}, current revision is {}",
                self.revision
            ));
        }
        if edits.is_empty() {
            return Ok(self.revision);
        }

        let mut candidate = self.clone();
        for edit in edits {
            match edit {
                Edit::Upsert(object) => {
                    let id = canonical_id(object.id.clone())?;
                    let mut object = object.clone();
                    object.id = id.clone();
                    candidate.objects.insert(id, object);
                }
                Edit::Rename { id, name } => {
                    let object = candidate
                        .objects
                        .get_mut(id)
                        .ok_or_else(|| format!("object '{}' does not exist", id.as_str()))?;
                    object.name = name.clone();
                }
                Edit::SetSourcePart { id, name, source } => {
                    let object = candidate
                        .objects
                        .get_mut(id)
                        .ok_or_else(|| format!("object '{}' does not exist", id.as_str()))?;
                    let name = canonical_part_name(name)?;
                    if let Some(source) = source {
                        if let Some(part) = object.source_parts.iter_mut().find(|p| p.name == name)
                        {
                            part.source.clone_from(source);
                        } else {
                            object.source_parts.push(SourcePart {
                                name,
                                source: source.clone(),
                            });
                        }
                    } else {
                        object.source_parts.retain(|part| part.name != name);
                    }
                }
                Edit::SetReferences { id, references } => {
                    let object = candidate
                        .objects
                        .get_mut(id)
                        .ok_or_else(|| format!("object '{}' does not exist", id.as_str()))?;
                    object.references.clone_from(references);
                }
                Edit::Remove { id } => {
                    if candidate.objects.remove(id).is_none() {
                        return Err(format!("object '{}' does not exist", id.as_str()));
                    }
                }
            }
        }
        candidate.normalize_and_validate()?;
        if candidate.objects == self.objects {
            return Ok(self.revision);
        }
        candidate.revision = self
            .revision
            .checked_add(1)
            .ok_or_else(|| "catalog revision overflow".to_owned())?;
        *self = candidate;
        Ok(self.revision)
    }

    /// Returns a deterministic dependency-first order, or IDs blocked by a dependency cycle.
    /// Cyclic references remain valid catalog data; consumers choose how to handle them.
    pub fn dependency_order(&self) -> Result<Vec<ObjectId>, Vec<ObjectId>> {
        let mut remaining_dependencies = BTreeMap::<ObjectId, usize>::new();
        let mut dependents = BTreeMap::<ObjectId, BTreeSet<ObjectId>>::new();
        for (id, object) in &self.objects {
            let unique_dependencies: BTreeSet<_> = object
                .references
                .iter()
                .map(|reference| reference.target.clone())
                .filter(|target| self.objects.contains_key(target))
                .collect();
            remaining_dependencies.insert(id.clone(), unique_dependencies.len());
            for dependency in unique_dependencies {
                dependents.entry(dependency).or_default().insert(id.clone());
            }
        }

        let mut ready: BTreeSet<_> = remaining_dependencies
            .iter()
            .filter_map(|(id, count)| (*count == 0).then_some(id.clone()))
            .collect();
        let mut ordered = Vec::with_capacity(self.objects.len());
        while let Some(id) = ready.pop_first() {
            ordered.push(id.clone());
            if let Some(users) = dependents.get(&id) {
                for user in users {
                    let count = remaining_dependencies
                        .get_mut(user)
                        .expect("every dependent is a catalog object");
                    *count -= 1;
                    if *count == 0 {
                        ready.insert(user.clone());
                    }
                }
            }
        }

        if ordered.len() == self.objects.len() {
            Ok(ordered)
        } else {
            Err(remaining_dependencies
                .into_iter()
                .filter_map(|(id, count)| (count != 0).then_some(id))
                .collect())
        }
    }

    /// Loads a size-bounded portable catalog or native Knowledge Base from disk.
    pub fn load(path: impl AsRef<Path>) -> Result<Self, String> {
        let path = path.as_ref();
        if path.is_dir() {
            let conn_file = path.join("knowledgebase.connection");
            if conn_file.exists() {
                return native::load(path);
            }
        } else if path
            .file_name()
            .is_some_and(|f| f == "knowledgebase.connection")
        {
            return native::load(path.parent().unwrap_or(path));
        }

        Self::from_json(&read_catalog_text(path)?)
    }

    /// Loads the catalog of Design model `model_id` of the native Knowledge Base folder `path`. The caller
    /// has selected the model (see `udm::version_scope::select_design_model`); `load` instead serves only a
    /// KB with a single Design model.
    pub fn load_native_model(path: impl AsRef<Path>, model_id: i32) -> Result<Self, String> {
        native::load_model(path.as_ref(), model_id)
    }

    /// The snapshot JSON for this catalog: the text `save_snapshot_atomic` writes and `from_snapshot_text`
    /// reads. Nothing is re-sorted, so the order the KB reader produced is kept.
    pub fn snapshot_text(&self) -> Result<String, String> {
        let file = CatalogFile {
            format_version: FORMAT_VERSION,
            revision: self.revision,
            objects: self.objects.values().cloned().collect(),
        };
        let text = serde_json::to_string(&file)
            .map_err(|error| format!("cannot encode the catalog snapshot: {error}"))?;
        if text.len() > MAX_CATALOG_BYTES {
            return Err(format!(
                "catalog snapshot exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        Ok(text)
    }

    /// Writes the snapshot the KB cache reuses. Unlike `save_atomic`, nothing is re-sorted: each object's
    /// parts and references keep the order the KB reader produced, so `load_snapshot` returns the same
    /// catalog the live read builds.
    pub fn save_snapshot_atomic(&self, path: impl AsRef<Path>) -> Result<(), String> {
        let path = path.as_ref();
        let bytes = self.snapshot_text()?;
        atomic_write_with(path, bytes.as_bytes(), |file, contents| {
            file.write_all(contents)
        })
        .map_err(|error| {
            format!(
                "could not atomically save catalog snapshot '{}': {error}",
                path.display()
            )
        })
    }

    /// Loads a snapshot written by `save_snapshot_atomic`, keeping every object's part and reference order.
    /// The format, the size, the object limit and identity uniqueness are checked. Nothing is re-sorted or
    /// re-normalized, so the loaded catalog equals the catalog that was saved.
    pub fn load_snapshot(path: impl AsRef<Path>) -> Result<Self, String> {
        let text = read_catalog_text(path.as_ref())?;
        Self::from_snapshot_text(&text)
    }

    /// Decodes snapshot JSON with the checks `load_snapshot` makes.
    pub fn from_snapshot_text(text: &str) -> Result<Self, String> {
        let file: CatalogFile = serde_json::from_str(text)
            .map_err(|error| format!("invalid catalog snapshot: {error}"))?;
        if file.format_version != FORMAT_VERSION {
            return Err(format!(
                "unsupported catalog snapshot format_version {}; expected {FORMAT_VERSION}",
                file.format_version
            ));
        }
        if file.objects.len() > MAX_OBJECTS {
            return Err(format!("catalog exceeds the {MAX_OBJECTS}-object limit"));
        }
        let mut objects = BTreeMap::new();
        for object in file.objects {
            let id = object.id.clone();
            if objects.insert(id.clone(), object).is_some() {
                return Err(format!("duplicate object identity '{}'", id.as_str()));
            }
        }
        Ok(Self {
            revision: file.revision,
            objects,
        })
    }

    /// Writes a complete catalog to a sibling temporary file, then atomically renames it.
    /// A failed write or replacement leaves the previous destination untouched.
    pub fn save_atomic(&self, path: impl AsRef<Path>) -> Result<(), String> {
        let path = path.as_ref();
        let bytes = self.to_json()?;
        atomic_write_with(path, bytes.as_bytes(), |file, contents| {
            file.write_all(contents)
        })
        .map_err(|error| {
            format!(
                "could not atomically save catalog '{}': {error}",
                path.display()
            )
        })
    }

    fn normalize_and_validate(&mut self) -> Result<(), String> {
        if self.objects.len() > MAX_OBJECTS {
            return Err(format!("catalog exceeds the {MAX_OBJECTS}-object limit"));
        }

        let mut normalized = BTreeMap::new();
        let mut names = HashSet::with_capacity(self.objects.len());
        let mut total_bytes = 0usize;
        let mut total_references = 0usize;
        let mut system_roots = 0usize;
        for (_, mut object) in std::mem::take(&mut self.objects) {
            object.id = canonical_id(object.id)?;
            if object.system_root {
                system_roots += 1;
                if system_roots > 1 {
                    return Err("catalog declares more than one system root module".to_string());
                }
            }
            normalize_object_name(&object)?;
            add_bounded_bytes(&mut total_bytes, object.id.0.len())?;
            add_bounded_bytes(&mut total_bytes, object.name.len())?;
            // Only objects that produce a named class or file must have unique names. A Module emits
            // nothing, and GeneXus allows two Modules of one name under different parents (KBTeste16
            // and KBTeste both hold two `Notifications` Modules). A Table is the physical model of a
            // Transaction and takes its name by design (`plan070trn`, `trn353`), and the rebuild never
            // derives a table from a Table object. Neither kind is in the name set.
            // Uniqueness is case-insensitive; the name itself keeps its stored case (see `normalize_name`).
            if !matches!(object.kind, ObjectKind::Module | ObjectKind::Table)
                && !names.insert(object.name.to_ascii_uppercase())
            {
                return Err(format!(
                    "duplicate object name '{}' (names are case-insensitive)",
                    object.name
                ));
            }
            if object.source_parts.len() > MAX_SOURCE_PARTS {
                return Err(format!(
                    "object '{}' exceeds the {MAX_SOURCE_PARTS}-source-part limit",
                    object.id.as_str()
                ));
            }
            let mut parts = BTreeMap::new();
            for mut part in object.source_parts {
                part.name = canonical_part_name(&part.name)?;
                if part.source.len() > MAX_SOURCE_PART_BYTES {
                    return Err(format!(
                        "source part '{}' on '{}' exceeds the {MAX_SOURCE_PART_BYTES}-byte limit",
                        part.name,
                        object.id.as_str()
                    ));
                }
                if part.source.contains('\0') {
                    return Err(format!(
                        "source part '{}' on '{}' contains a NUL byte",
                        part.name,
                        object.id.as_str()
                    ));
                }
                add_bounded_bytes(&mut total_bytes, part.name.len())?;
                add_bounded_bytes(&mut total_bytes, part.source.len())?;
                if parts.insert(part.name.clone(), part).is_some() {
                    return Err(format!(
                        "duplicate source part name on '{}' (names are case-insensitive)",
                        object.id.as_str()
                    ));
                }
            }
            object.source_parts = parts.into_values().collect();

            if object.references.len() > MAX_REFERENCES_PER_OBJECT {
                return Err(format!(
                    "object '{}' exceeds the {MAX_REFERENCES_PER_OBJECT}-reference limit",
                    object.id.as_str()
                ));
            }
            let mut references = BTreeSet::new();
            for mut reference in object.references {
                reference.target = canonical_id(reference.target)?;
                add_bounded_bytes(&mut total_bytes, reference.target.0.len())?;
                if !references.insert(reference) {
                    return Err(format!(
                        "duplicate reference on object '{}'",
                        object.id.as_str()
                    ));
                }
            }
            total_references = total_references
                .checked_add(references.len())
                .ok_or_else(|| "catalog reference count overflow".to_owned())?;
            if total_references > MAX_REFERENCES {
                return Err(format!(
                    "catalog exceeds the {MAX_REFERENCES}-reference limit"
                ));
            }
            object.references = references.into_iter().collect();
            let id = object.id.clone();
            if normalized.insert(id.clone(), object).is_some() {
                return Err(format!("duplicate object identity '{}'", id.as_str()));
            }
        }

        for object in normalized.values() {
            for reference in &object.references {
                if !normalized.contains_key(&reference.target) {
                    return Err(format!(
                        "object '{}' has a dangling {:?} reference to '{}'",
                        object.id.as_str(),
                        reference.kind,
                        reference.target.as_str()
                    ));
                }
            }
        }
        if total_bytes > MAX_CATALOG_BYTES {
            return Err(format!(
                "catalog content exceeds the {MAX_CATALOG_BYTES}-byte limit"
            ));
        }
        self.objects = normalized;
        Ok(())
    }
}

impl Default for Catalog {
    fn default() -> Self {
        Self::empty()
    }
}

fn canonical_id(mut id: ObjectId) -> Result<ObjectId, String> {
    validate_token(&id.0, "object identity", true)?;
    id.0.make_ascii_lowercase();
    Ok(id)
}

/// Object names follow the ASCII rule, except the flagged system root. Its exemption
/// covers exactly one Module under exactly `SYSTEM_ROOT_MODULE_NAME`: a Module with a
/// space in its name is refused unless the reader identified it as the root.
fn normalize_object_name(object: &KbObject) -> Result<(), String> {
    if !object.system_root {
        return normalize_name(&object.name, "object name");
    }
    if object.kind != ObjectKind::Module
        || !object.name.eq_ignore_ascii_case(SYSTEM_ROOT_MODULE_NAME)
    {
        return Err(format!(
            "object '{}' is flagged as the system root module but is not the Module named '{SYSTEM_ROOT_MODULE_NAME}'",
            object.id.as_str()
        ));
    }
    Ok(())
}

/// Validates an object name and leaves its case as stored. The KB path keeps the stored case, and the
/// emitter writes it into C# class names (`public class AddDeviceGroups`), so a snapshot that upper-cased
/// it would emit different classes. Uniqueness is checked case-insensitively by the caller.
fn normalize_name(name: &str, field: &str) -> Result<(), String> {
    validate_token(name, field, false)
}

fn canonical_part_name(name: &str) -> Result<String, String> {
    validate_token(name, "source-part name", false)?;
    Ok(name.to_ascii_uppercase())
}

fn validate_token(value: &str, field: &str, allow_hyphen: bool) -> Result<(), String> {
    let bytes = value.as_bytes();
    if bytes.is_empty() || bytes.len() > MAX_OBJECT_NAME_BYTES {
        return Err(format!(
            "{field} must contain 1 to {MAX_OBJECT_NAME_BYTES} ASCII characters"
        ));
    }
    let first = bytes[0];
    let first_ok = if allow_hyphen {
        first.is_ascii_alphanumeric() || first == b'_' || first == b'-'
    } else {
        first.is_ascii_alphabetic() || first == b'_'
    };
    if !first_ok
        || !bytes.iter().all(|byte| {
            byte.is_ascii_alphanumeric() || *byte == b'_' || (allow_hyphen && *byte == b'-')
        })
    {
        return Err(format!(
            "{field} must use ASCII letters, digits, underscores{}",
            if allow_hyphen { ", or hyphens" } else { "" }
        ));
    }
    Ok(())
}

fn add_bounded_bytes(total: &mut usize, amount: usize) -> Result<(), String> {
    *total = total
        .checked_add(amount)
        .ok_or_else(|| "catalog content size overflow".to_owned())?;
    if *total > MAX_CATALOG_BYTES {
        return Err(format!(
            "catalog content exceeds the {MAX_CATALOG_BYTES}-byte limit"
        ));
    }
    Ok(())
}

/// Reads a catalog file as UTF-8 text, bounded by `MAX_CATALOG_BYTES`.
fn read_catalog_text(path: &Path) -> Result<String, String> {
    let file = File::open(path)
        .map_err(|error| format!("could not open catalog '{}': {error}", path.display()))?;
    let metadata = file
        .metadata()
        .map_err(|error| format!("could not inspect catalog '{}': {error}", path.display()))?;
    if metadata.len() > MAX_CATALOG_BYTES as u64 {
        return Err(format!(
            "catalog file exceeds the {MAX_CATALOG_BYTES}-byte limit"
        ));
    }
    let mut bytes = Vec::with_capacity(metadata.len() as usize);
    file.take((MAX_CATALOG_BYTES + 1) as u64)
        .read_to_end(&mut bytes)
        .map_err(|error| format!("could not read catalog '{}': {error}", path.display()))?;
    if bytes.len() > MAX_CATALOG_BYTES {
        return Err(format!(
            "catalog file exceeds the {MAX_CATALOG_BYTES}-byte limit"
        ));
    }
    String::from_utf8(bytes).map_err(|error| format!("catalog file is not UTF-8: {error}"))
}

fn atomic_write_with(
    path: &Path,
    contents: &[u8],
    write_contents: impl FnOnce(&mut File, &[u8]) -> io::Result<()>,
) -> io::Result<()> {
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let file_name = path.file_name().ok_or_else(|| {
        io::Error::new(io::ErrorKind::InvalidInput, "destination has no file name")
    })?;

    let (temp_path, mut temp_file) = create_temp(parent, file_name)?;
    let result = (|| {
        write_contents(&mut temp_file, contents)?;
        temp_file.sync_all()?;
        drop(temp_file);
        fs::rename(&temp_path, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp_path);
    }
    result
}

fn create_temp(parent: &Path, file_name: &std::ffi::OsStr) -> io::Result<(PathBuf, File)> {
    for _ in 0..32 {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let mut temp_name = file_name.to_os_string();
        temp_name.push(format!(".tmp.{}.{}", std::process::id(), sequence));
        let path = parent.join(temp_name);
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => return Ok((path, file)),
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error),
        }
    }
    Err(io::Error::new(
        io::ErrorKind::AlreadyExists,
        "could not allocate a unique sibling temporary file",
    ))
}

#[cfg(test)]
mod tests {
    use super::atomic_write_with;
    use std::fs;
    use std::io::{self, Write};
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicU64, Ordering};

    static TEST_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    fn temp_dir() -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "gx-engine-catalog-atomic-{}-{}",
            std::process::id(),
            TEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).expect("create test directory");
        path
    }

    #[test]
    fn failed_atomic_write_preserves_the_previous_valid_bytes() {
        let dir = temp_dir();
        let target = dir.join("catalog.json");
        fs::write(&target, b"previous valid catalog").expect("seed destination");

        let error = atomic_write_with(&target, b"new catalog", |file, _| {
            file.write_all(b"partial temporary data")?;
            Err(io::Error::other("injected write failure"))
        })
        .expect_err("injected write must fail");

        assert_eq!(error.kind(), io::ErrorKind::Other);
        assert_eq!(
            fs::read(&target).expect("previous bytes remain"),
            b"previous valid catalog"
        );
        assert_eq!(fs::read_dir(&dir).expect("read test dir").count(), 1);
        fs::remove_dir_all(dir).expect("remove test directory");
    }
}
