//! A Knowledge Base folder backed by the in-memory store, for tests that drive the real binary.
//!
//! Debug builds only: `UdmClient::connect_kb` opens a folder that holds `gxe-mock-udm.json` and no
//! `knowledgebase.connection` as an in-memory client loaded from that file. A release build has no
//! such path, and a real Knowledge Base folder (it has a connection file) is never read this way.
//! The file is read again on every connection, like the database of a live Knowledge Base.

use super::*;

/// The file a mock Knowledge Base folder holds.
pub const MOCK_KB_FILE: &str = "gxe-mock-udm.json";

/// (model, entity type, entity id) of a child mapped to its parent.
type ModelParentKey = (i32, i32, i32);

/// Every table of `InMemoryDatabase`; keyed tables are stored as entry lists.
#[derive(serde::Serialize, serde::Deserialize)]
struct MockKb {
    model_entity_versions: Vec<ModelEntityVersionRecord>,
    entity_version_compositions: Vec<EntityVersionCompositionRecord>,
    entity_versions: Vec<EntityVersionRecord>,
    entity_types: Vec<EntityTypeRecord>,
    model_cross_references: Vec<ModelCrossReferenceRecord>,
    state_locks: Vec<StateLockRecord>,
    entity_guids: Vec<((i32, i32), String)>,
    model_parents: Vec<(ModelParentKey, (i32, i32))>,
    object_saved_timestamps: Vec<((i32, i32, i32, i32), i64)>,
    model_entity_version_timestamps: Vec<((i32, i32, i32, i32), i64)>,
    generate_flags: Vec<((i32, i32, i32), String)>,
    next_entity_id: i32,
}

impl UdmClient {
    /// Writes this in-memory client's store as the mock Knowledge Base of `kb_dir`.
    pub fn save_mock_kb(&self, kb_dir: &Path) -> Result<(), String> {
        let store = self
            .storage
            .read()
            .map_err(|error| format!("Storage lock poisoned: {error}"))?;
        let mock = MockKb {
            model_entity_versions: store.model_entity_versions.clone(),
            entity_version_compositions: store.entity_version_compositions.clone(),
            entity_versions: store.entity_versions.clone(),
            entity_types: store.entity_types.clone(),
            model_cross_references: store.model_cross_references.clone(),
            state_locks: store.state_locks.clone(),
            entity_guids: store.entity_guids.clone().into_iter().collect(),
            model_parents: store.model_parents.clone().into_iter().collect(),
            object_saved_timestamps: store.object_saved_timestamps.clone().into_iter().collect(),
            model_entity_version_timestamps: store
                .model_entity_version_timestamps
                .clone()
                .into_iter()
                .collect(),
            generate_flags: store.generate_flags.clone().into_iter().collect(),
            next_entity_id: store.next_entity_id,
        };
        let text = serde_json::to_string(&mock).map_err(|error| error.to_string())?;
        // Written beside the file and renamed over it, so a reader never sees half a store.
        let staged = kb_dir.join(format!("{MOCK_KB_FILE}.tmp"));
        std::fs::write(&staged, text)
            .and_then(|()| std::fs::rename(&staged, kb_dir.join(MOCK_KB_FILE)))
            .map_err(|error| format!("cannot write the mock Knowledge Base: {error}"))
    }
}

/// The in-memory client of a mock Knowledge Base folder, or `None` when `kb_dir` is not one.
pub(super) fn open(kb_dir: &Path) -> Result<Option<UdmClient>, String> {
    let file = kb_dir.join(MOCK_KB_FILE);
    if kb_dir.join("knowledgebase.connection").exists() || !file.is_file() {
        return Ok(None);
    }
    let text = std::fs::read_to_string(&file)
        .map_err(|error| format!("cannot read the mock Knowledge Base: {error}"))?;
    let mock: MockKb = serde_json::from_str(&text)
        .map_err(|error| format!("the mock Knowledge Base is not valid: {error}"))?;
    let client = UdmClient::new_in_memory_with_entity_types(mock.entity_types);
    {
        let mut store = client
            .storage
            .write()
            .map_err(|error| format!("Storage lock poisoned: {error}"))?;
        store.model_entity_versions = mock.model_entity_versions;
        store.entity_version_compositions = mock.entity_version_compositions;
        store.entity_versions = mock.entity_versions;
        store.model_cross_references = mock.model_cross_references;
        store.state_locks = mock.state_locks;
        store.entity_guids = mock.entity_guids.into_iter().collect();
        store.model_parents = mock.model_parents.into_iter().collect();
        store.object_saved_timestamps = mock.object_saved_timestamps.into_iter().collect();
        store.model_entity_version_timestamps =
            mock.model_entity_version_timestamps.into_iter().collect();
        store.generate_flags = mock.generate_flags.into_iter().collect();
        store.next_entity_id = mock.next_entity_id;
    }
    Ok(Some(client))
}
