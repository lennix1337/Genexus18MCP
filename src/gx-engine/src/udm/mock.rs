//! In-memory database simulation and mock repository for UDM storage.

#![forbid(unsafe_code)]

/// ModelEntityVersion row: active object version descriptor in a model.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct ModelEntityVersionRecord {
    pub model_id: i32,
    pub entity_type_id: i32,
    pub entity_id: i32,
    pub entity_version_id: i32,
    pub name: String,
}

/// EntityVersionComposition row: parent-to-child component part linkage.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct EntityVersionCompositionRecord {
    pub compound_entity_type_id: i32,
    pub compound_entity_id: i32,
    pub compound_entity_version_id: i32,
    pub component_entity_type_id: i32,
    pub component_entity_id: i32,
    pub component_entity_version_id: i32,
}

/// EntityVersion row: payload envelope blob and metadata.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct EntityVersionRecord {
    pub entity_type_id: i32,
    pub entity_id: i32,
    pub entity_version_id: i32,
    pub data: Vec<u8>,
    pub properties: Option<String>,
}

/// EntityType row: catalog descriptor of object or part types.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct EntityTypeRecord {
    pub entity_type_id: i32,
    pub name: String,
    pub namespace: String,
}

/// ModelCrossReference row: inter-object dependency graph link.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct ModelCrossReferenceRecord {
    pub model_id: i32,
    pub from_entity_type_id: i32,
    pub from_entity_id: i32,
    pub to_entity_type_id: i32,
    pub to_entity_id: i32,
    pub reference_type: i32,
}

/// State row: multi-user and agent lock coordinator in UDM.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct StateLockRecord {
    pub state_name: String,
    pub state_user_name: String,
    pub state_guid: String,
    pub owner_machine_name: String,
}

/// Stored identity and last-update inputs of one active object version.
///
/// These are the inputs the SDK exposes through `KBObject.Guid`, `KBObject.Parent`
/// and `KBObject.LastUpdate`; they are read without decoding any part.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ObjectReadMetadata {
    /// `Entity.EntityGuid` of the object, as stored (canonical dashed form).
    pub guid: Option<String>,
    /// The holder row `(EntityTypeId, EntityId)`, or `None` when the object has no parent.
    pub parent: Option<(i32, i32)>,
    /// `Entity.EntityGuid` of the holder, when the holder row exists.
    pub parent_guid: Option<String>,
    /// `ModelEntityVersion.ModelEntityVersionName` of the holder, the Worker's fallback
    /// when the parent GUID is unavailable.
    pub parent_name: Option<String>,
    /// `ModelEntityOutput.OutputTimestamp` of the active version's `ObjectSaved`
    /// output (`OutputTypeId=301`, `OutputTypeVersion=0`), raw .NET ticks.
    pub object_saved_ticks: Option<i64>,
    /// `ModelEntityVersion.ModelEntityVersionTimestamp` of the exact active version,
    /// raw .NET ticks. Used only when the `ObjectSaved` output row is absent.
    pub model_entity_version_ticks: Option<i64>,
}

/// In-memory storage backing the UDM database for simulation and fast tests.
#[derive(Debug, Clone, Default)]
pub struct InMemoryDatabase {
    pub model_entity_versions: Vec<ModelEntityVersionRecord>,
    pub entity_version_compositions: Vec<EntityVersionCompositionRecord>,
    pub entity_versions: Vec<EntityVersionRecord>,
    pub entity_types: Vec<EntityTypeRecord>,
    pub model_cross_references: Vec<ModelCrossReferenceRecord>,
    pub state_locks: Vec<StateLockRecord>,
    /// `Entity.EntityGuid` per `(EntityTypeId, EntityId)`.
    pub entity_guids: std::collections::BTreeMap<(i32, i32), String>,
    /// `ModelEntityVersion` parent per `(ModelId, EntityTypeId, EntityId)`, as
    /// `(ModelParentEntityTypeId, ModelParentEntityId)`. `(0, 0)` is parent-less.
    pub model_parents: std::collections::BTreeMap<(i32, i32, i32), (i32, i32)>,
    /// `ModelEntityOutput` timestamp of the active version's `ObjectSaved` output
    /// (`OutputTypeId=301`, `OutputTypeVersion=0`) per
    /// `(ModelId, EntityTypeId, EntityId, EntityVersionId)`, raw .NET ticks.
    pub object_saved_timestamps: std::collections::BTreeMap<(i32, i32, i32, i32), i64>,
    /// `ModelEntityVersion.ModelEntityVersionTimestamp` per
    /// `(ModelId, EntityTypeId, EntityId, EntityVersionId)`, raw .NET ticks.
    pub model_entity_version_timestamps: std::collections::BTreeMap<(i32, i32, i32, i32), i64>,
    /// The stored `GenerateObject` value of a Procedure per `(ModelId, EntityTypeId, EntityId)`, as the
    /// Knowledge Base writes it (`"True"` or `"False"`).
    pub generate_flags: std::collections::BTreeMap<(i32, i32, i32), String>,
    pub next_entity_id: i32,
    savepoint: Option<Box<InMemoryDatabase>>,
}

impl InMemoryDatabase {
    /// An in-memory database that declares the GeneXus 18 vocabulary. Tests and
    /// the catalog tooling use it; a live client never reads these seeds.
    pub fn new() -> Self {
        let mut db = Self::without_vocabulary();
        db.seed_default_entity_types();
        db
    }

    /// An empty database with no `EntityType` rows: the vocabulary of a live
    /// Knowledge Base is read from its own table, never from seeds.
    pub fn without_vocabulary() -> Self {
        Self {
            next_entity_id: 1000,
            ..Default::default()
        }
    }

    /// Seeds the GeneXus 18 ids with the real `EntityType` names and namespaces
    /// (`docs/udm_storage_notes.md`). Unserved parts such as `Documentation`,
    /// `Help` and `Layout` are declared too, so tests can cover parts the engine
    /// must skip.
    pub fn seed_default_entity_types(&mut self) {
        if !self.entity_types.is_empty() {
            return;
        }

        let defaults: [(i32, &str, &str); 35] = [
            (38, "Procedure", "Objects"),
            (43, "Transaction", "Objects"),
            (47, "WebPanel", "Objects"),
            (40, "SDT", "Objects"),
            (29, "DataProvider", "Objects"),
            (30, "DataSelector", "Objects"),
            (12, "Module", "Objects"),
            (27, "Attribute", "Attributes"),
            (33, "Domain", "Domains"),
            (31, "DataView", "DataViews"),
            (41, "Table", "PhysicalModel"),
            (57, "DesignSystem", "Themes"),
            (100, "Procedure", ""),
            (99, "DataProviderSource", ""),
            (102, "Rules", ""),
            (97, "Events", ""),
            (89, "Conditions", ""),
            (105, "Variables", ""),
            (104, "Structure", ""),
            (103, "SDTStructure", ""),
            (151, "SDTLevelEntity", ""),
            (152, "SDTItemEntity", ""),
            (107, "WebForm", ""),
            (78, "PatternInstance", ""),
            (95, "Documentation", ""),
            (98, "Help", ""),
            (118, "Layout", ""),
            (8, "Udm.Types.Folder", "Folders"),
            (2, "Udm.Types.Model", ""),
            (1, "Root", ""),
            (4, "Udm.Types.Reference", ""),
            (5, "Udm.Types.Output", ""),
            (7, "Udm.Types.User", ""),
            (60, "ThemeClass", "ThemeClasses"),
            (139, "GlobalWorkflowProperties", ""),
        ];

        for (id, name, namespace) in defaults {
            self.entity_types.push(EntityTypeRecord {
                entity_type_id: id,
                name: name.to_string(),
                namespace: namespace.to_string(),
            });
        }
    }

    pub fn allocate_entity_id(&mut self) -> i32 {
        self.next_entity_id += 1;
        self.next_entity_id
    }

    pub fn in_transaction(&self) -> bool {
        self.savepoint.is_some()
    }

    pub fn begin_transaction(&mut self) {
        let mut snapshot = self.clone();
        snapshot.savepoint = None;
        self.savepoint = Some(Box::new(snapshot));
    }

    pub fn commit_transaction(&mut self) -> Result<(), String> {
        if self.savepoint.take().is_some() {
            Ok(())
        } else {
            Err("No active transaction to commit".to_string())
        }
    }

    pub fn rollback_transaction(&mut self) -> Result<(), String> {
        if let Some(snapshot) = self.savepoint.take() {
            *self = *snapshot;
            Ok(())
        } else {
            Err("No active transaction to rollback".to_string())
        }
    }
}
