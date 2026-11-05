//! Database client interface and query execution for GeneXus UDM physical storage.
//!
//! Provides `UdmClient` coordinating in-memory mock storage and live SQL Server /
//! LocalDB execution.

#![forbid(unsafe_code)]

use crate::udm::connection::KbConnectionInfo;
use crate::udm::entity_types::EntityTypeResolver;
use crate::udm::library::ModelParentRecord;
use crate::udm::live;
pub use crate::udm::mock::{
    EntityTypeRecord, EntityVersionCompositionRecord, EntityVersionRecord, InMemoryDatabase,
    ModelCrossReferenceRecord, ModelEntityVersionRecord, ObjectReadMetadata, StateLockRecord,
};
pub use crate::udm::row::{UdmRow, UdmValue};

use std::collections::BTreeMap;
use std::path::Path;
use std::sync::{Arc, Mutex, OnceLock, RwLock};

mod environment;
mod inspect;
mod kb_facts;
mod listing;
mod live_rows;
mod model_pin;
mod model_reads;
mod prefetch;
mod read_view;
mod versions;
mod writes;

pub use kb_facts::AttributeFacts;

/// GUID GeneXus gives the Knowledge Base root module (`Entity.EntityGuid` of the Module
/// with `EntityId` 1). It is the same in the GeneXus 16, 17 and 18 KBs checked.
pub const SYSTEM_ROOT_MODULE_GUID: &str = "afa47377-41d5-4ae8-9755-6f53150aa361";

/// Keys whose compositions and `EntityVersion` parts were read set-wise for a model.
/// A key listed here is answered from memory: the bulk read returned every database row
/// for it, so a row missing from memory is missing from the database as well.
#[derive(Debug, Default)]
struct BulkCoverage {
    /// Compound keys whose `EntityVersionComposition` rows are all in memory.
    compositions: std::collections::HashSet<(i32, i32, i32)>,
    /// `EntityVersion` keys whose rows are all in memory, present or absent.
    entity_versions: std::collections::HashSet<(i32, i32, i32)>,
    /// The latest version id of the Knowledge Base root entity, once read (`None` inside: no version).
    root_version: Option<Option<i32>>,
}

/// Unified Data Model (UDM) physical storage client.
#[derive(Debug, Clone)]
pub struct UdmClient {
    connection_info: Option<KbConnectionInfo>,
    storage: Arc<RwLock<InMemoryDatabase>>,
    /// The served vocabulary of a live Knowledge Base, read from its own
    /// `EntityType` table once and shared by every clone of this client.
    live_vocabulary: Arc<OnceLock<Arc<EntityTypeResolver>>>,
    /// Keys answered from memory after a set-wise read; any write clears it.
    bulk: Arc<RwLock<BulkCoverage>>,
    read_session: Option<Arc<Mutex<Option<live::ReadSession>>>>,
    /// Operation-local serializable reader; a failed session must never be recreated.
    consistent_read: bool,
    /// The Design model readers and writers of this client target (see `udm::version_scope`).
    design_model_id: i32,
    /// Whether writes are executed against the live SQL Server / LocalDB database.
    live_writes: bool,
}

impl UdmClient {
    /// Creates a purely in-memory UDM client for testing, emulation, and fast verification.
    pub fn new_in_memory() -> Self {
        Self {
            connection_info: None,
            storage: Arc::new(RwLock::new(InMemoryDatabase::new())),
            live_vocabulary: Arc::new(OnceLock::new()),
            bulk: Arc::new(RwLock::new(BulkCoverage::default())),
            read_session: None,
            consistent_read: false,
            design_model_id: crate::udm::reader::DEFAULT_DESIGN_MODEL_ID,
            live_writes: false,
        }
    }

    /// Creates an in-memory client whose `EntityType` table is `entity_types`,
    /// for example the GeneXus 16 numbering of a Knowledge Base.
    pub fn new_in_memory_with_entity_types(entity_types: Vec<EntityTypeRecord>) -> Self {
        let mut database = InMemoryDatabase::without_vocabulary();
        database.entity_types = entity_types;
        Self {
            connection_info: None,
            storage: Arc::new(RwLock::new(database)),
            live_vocabulary: Arc::new(OnceLock::new()),
            bulk: Arc::new(RwLock::new(BulkCoverage::default())),
            read_session: None,
            consistent_read: false,
            design_model_id: crate::udm::reader::DEFAULT_DESIGN_MODEL_ID,
            live_writes: false,
        }
    }

    /// Creates a UDM client configured with connection info pointing to a SQL Server or LocalDB instance.
    /// Its vocabulary comes from the Knowledge Base itself, never from seeds.
    pub fn from_connection_info(info: KbConnectionInfo) -> Self {
        Self {
            connection_info: Some(info),
            storage: Arc::new(RwLock::new(InMemoryDatabase::without_vocabulary())),
            live_vocabulary: Arc::new(OnceLock::new()),
            bulk: Arc::new(RwLock::new(BulkCoverage::default())),
            read_session: None,
            consistent_read: false,
            design_model_id: crate::udm::reader::DEFAULT_DESIGN_MODEL_ID,
            live_writes: false,
        }
    }

    /// Creates a UDM client connected to a Knowledge Base directory containing `knowledgebase.connection`.
    pub fn connect_kb<P: AsRef<Path>>(kb_dir: P) -> Result<Self, String> {
        let info = KbConnectionInfo::from_kb_dir(kb_dir)?;
        Ok(Self::from_connection_info(info))
    }

    /// Reuses one subprocess connection for this reader's generated SELECT batches.
    /// The connection is shared only with clones of this client; writes stay one-shot.
    pub fn with_persistent_reads(mut self) -> Self {
        self.read_session = Some(Arc::new(Mutex::new(None)));
        self
    }

    /// Targets the Design model `model_id` (the active KB version's, selected by
    /// `udm::version_scope::select_design_model`) for every reader and writer built on this client.
    pub fn with_design_model_id(mut self, model_id: i32) -> Self {
        self.design_model_id = model_id;
        self
    }

    /// The Design model readers and writers built on this client target.
    pub fn design_model_id(&self) -> i32 {
        self.design_model_id
    }

    /// Returns the target `KbConnectionInfo`, if configured.
    pub fn connection_info(&self) -> Option<&KbConnectionInfo> {
        self.connection_info.as_ref()
    }

    /// The in-memory store, for unit tests that seed storage rows directly.
    #[cfg(test)]
    pub(crate) fn storage_for_test(&self) -> &Arc<RwLock<InMemoryDatabase>> {
        &self.storage
    }

    /// Returns `true` when reads go through a live SQL Server / LocalDB
    /// connection instead of the in-memory store alone.
    pub fn is_live(&self) -> bool {
        self.connection_info.is_some()
    }

    /// Enables live SQL Server mutations on this client.
    /// By default, live writes are disabled to prevent unintended database modifications.
    pub fn with_live_writes(mut self, enabled: bool) -> Self {
        self.live_writes = enabled;
        self
    }

    /// Whether live SQL Server mutations are enabled on this client.
    pub fn live_writes_enabled(&self) -> bool {
        self.live_writes
    }

    /// Begins a transactional boundary.
    pub fn begin_transaction(&self) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.begin_transaction();
        Ok(())
    }

    /// Commits the active transaction.
    pub fn commit_transaction(&self) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.commit_transaction()?;
        Ok(())
    }

    /// Rolls back the active transaction.
    pub fn rollback_transaction(&self) -> Result<(), String> {
        {
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            store.rollback_transaction()?;
        }
        // The rollback restores the snapshot taken at begin. Rows a prefetch loaded inside the
        // transaction are gone from memory now, so their coverage must go too (begin and commit
        // leave the rows in place, so they keep the coverage).
        self.clear_bulk_coverage();
        Ok(())
    }

    /// Executes raw SQL against live SQL Server or LocalDB instance using `sqlcmd`.
    pub fn execute_live_sql(&self, sql: &str) -> Result<String, String> {
        if self.consistent_read {
            if self.read_session.is_none() {
                return Err(
                    "consistent read session is unavailable; the operation cannot be replayed"
                        .into(),
                );
            }
            if !sql.trim_start().starts_with("SET NOCOUNT ON; SELECT ") {
                return Err("consistent reads require a generated SELECT batch".into());
            }
        }
        let info = self
            .connection_info
            .as_ref()
            .ok_or_else(|| "No connection info configured for live SQL execution".to_string())?;
        if let Some(shared) = &self.read_session {
            if sql.trim_start().starts_with("SET NOCOUNT ON; SELECT ") {
                let mut slot = shared
                    .lock()
                    .map_err(|_| "SQL reader session lock poisoned")?;
                if slot.is_none() {
                    if self.consistent_read {
                        return Err(
                            "consistent read session ended; the operation cannot be replayed"
                                .into(),
                        );
                    }
                    *slot = Some(live::ReadSession::start(info)?);
                }
                let result = slot
                    .as_mut()
                    .expect("the session slot was filled above under the same lock")
                    .query(sql);
                if result.is_err() {
                    *slot = None; // Reap the failed process; never replay this query.
                }
                return result;
            }
        }
        live::execute_sqlcmd(info, sql)
    }

    /// Tests the connection to the database.
    pub fn test_connection(&self) -> Result<bool, String> {
        if self.connection_info.is_some() {
            let res = self.execute_live_sql("SELECT 1")?;
            Ok(res.contains('1'))
        } else {
            Ok(true)
        }
    }

    /// Executes a SELECT query against in-memory storage, returning structured `UdmRow` records.
    pub fn query(&self, sql: &str) -> Result<Vec<UdmRow>, String> {
        let upper = sql.to_ascii_uppercase();
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;

        if upper.contains("FROM MODELENTITYVERSION") {
            let model_filter = parse_filter_int(sql, "ModelId");
            let columns = vec![
                "ModelId".to_string(),
                "EntityTypeId".to_string(),
                "EntityId".to_string(),
                "EntityVersionId".to_string(),
                "ModelEntityVersionName".to_string(),
            ];
            let rows = store
                .model_entity_versions
                .iter()
                .filter(|m| model_filter.is_none_or(|mf| m.model_id == mf))
                .map(|m| {
                    UdmRow::new(
                        columns.clone(),
                        vec![
                            UdmValue::Int(m.model_id as i64),
                            UdmValue::Int(m.entity_type_id as i64),
                            UdmValue::Int(m.entity_id as i64),
                            UdmValue::Int(m.entity_version_id as i64),
                            UdmValue::String(m.name.clone()),
                        ],
                    )
                })
                .collect();
            return Ok(rows);
        }

        if upper.contains("FROM ENTITYTYPE") {
            let columns = vec![
                "EntityTypeId".to_string(),
                "EntityTypeName".to_string(),
                "EntityTypeNamespace".to_string(),
            ];
            let rows = store
                .entity_types
                .iter()
                .map(|et| {
                    UdmRow::new(
                        columns.clone(),
                        vec![
                            UdmValue::Int(et.entity_type_id as i64),
                            UdmValue::String(et.name.clone()),
                            UdmValue::String(et.namespace.clone()),
                        ],
                    )
                })
                .collect();
            return Ok(rows);
        }

        Err(format!("Unsupported UDM query table in SQL: {sql}"))
    }

    /// Executes an ad-hoc write statement.
    pub fn execute(&self, sql: &str) -> Result<usize, String> {
        self.clear_bulk_coverage();
        let upper = sql.to_ascii_uppercase();
        if upper.starts_with("DELETE FROM MODELENTITYVERSION") {
            let model_id = parse_filter_int(sql, "ModelId").unwrap_or(0);
            let entity_type = parse_filter_int(sql, "EntityTypeId").unwrap_or(0);
            let entity_id = parse_filter_int(sql, "EntityId").unwrap_or(0);
            return Ok(if self.delete_object(model_id, entity_type, entity_id)? {
                1
            } else {
                0
            });
        }
        Ok(1)
    }

    /// Executes multiple statements in a batch.
    pub fn execute_batch(&self, sqls: &[&str]) -> Result<usize, String> {
        self.clear_bulk_coverage();
        let mut count = 0;
        for sql in sqls {
            count += self.execute(sql)?;
        }
        Ok(count)
    }
}

fn parse_filter_int(sql: &str, col: &str) -> Option<i32> {
    let upper = sql.to_ascii_uppercase();
    let col_upper = col.to_ascii_uppercase();
    if let Some(pos) = upper.find(&col_upper) {
        let rest = &sql[pos + col.len()..];
        let trimmed = rest.trim_start();
        if let Some(after_eq) = trimmed.strip_prefix('=') {
            let num_str: String = after_eq
                .trim_start()
                .chars()
                .take_while(|c| c.is_ascii_digit())
                .collect();
            return num_str.parse::<i32>().ok();
        }
    }
    None
}

/// Parallel sqlcmd sessions that read a model's parts (`prefetch_model_parts`).
const PART_READ_SHARDS: usize = 4;

#[cfg(test)]
mod tests;
