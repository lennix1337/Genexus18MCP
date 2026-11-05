//! Writes to the Knowledge Base: ids, flags, entities, versions, compositions, cross references and locks.

use super::*;
use std::sync::RwLockWriteGuard;

impl UdmClient {
    fn storage_mut(&self) -> Result<RwLockWriteGuard<'_, InMemoryDatabase>, String> {
        self.storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))
    }

    fn execute_live_write_sql(&self, sql: &str) -> Result<(), String> {
        if self.is_live() && self.live_writes {
            let in_tx = self
                .storage
                .read()
                .map(|s| s.in_transaction())
                .unwrap_or(false);
            if !in_tx {
                self.execute_live_sql(sql)?;
            }
        }
        Ok(())
    }

    fn fetch_live_max_entity_id(&self) -> Option<i32> {
        let query = "SET NOCOUNT ON; SELECT ISNULL(MAX(EntityId), 0) + 1 FROM (SELECT EntityId FROM EntityVersion UNION ALL SELECT EntityId FROM ModelEntityVersion UNION ALL SELECT EntityId FROM Entity) AS AllIds;";
        let raw = self.execute_live_sql(query).ok()?;
        let rows = live_rows::parse_live_rows(&raw, 1).ok()?;
        let id_str = rows.first()?.first()?.trim();
        id_str.parse::<i32>().ok()
    }

    /// Allocates a new unique entity ID.
    pub fn allocate_entity_id(&self) -> Result<i32, String> {
        let live_id = if self.is_live() && self.live_writes {
            self.fetch_live_max_entity_id()
        } else {
            None
        };
        let mut store = self.storage_mut()?;
        if let Some(id) = live_id {
            let min_prev = id.saturating_sub(1);
            if store.next_entity_id < min_prev {
                store.next_entity_id = min_prev;
            }
        }
        Ok(store.allocate_entity_id())
    }

    /// Retrieves active object descriptors for a model. If live connection is configured,
    /// populates the internal cache on demand.
    /// Stores the `GenerateObject` value of one Procedure of a model (in-memory client only; tests).
    pub fn set_generate_flag(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
        value: &str,
    ) -> Result<(), String> {
        let mut store = self.storage_mut()?;
        store
            .generate_flags
            .insert((model_id, entity_type_id, entity_id), value.to_string());
        Ok(())
    }

    /// Records the `Entity.EntityGuid` of an entity (test, emulation, and live storage).
    pub fn insert_entity_guid(
        &self,
        entity_type_id: i32,
        entity_id: i32,
        guid: &str,
    ) -> Result<(), String> {
        let escaped_guid = crate::udm::writer::escape_sql_literal(guid);
        let sql = format!(
            "IF NOT EXISTS (SELECT 1 FROM Entity WHERE EntityTypeId = {entity_type_id} AND EntityId = {entity_id}) \
             INSERT INTO Entity (EntityTypeId, EntityId, EntityGuid) VALUES ({entity_type_id}, {entity_id}, '{escaped_guid}');"
        );
        self.execute_live_write_sql(&sql)?;
        let mut store = self.storage_mut()?;
        store
            .entity_guids
            .insert((entity_type_id, entity_id), guid.to_string());
        Ok(())
    }

    /// Records the `ModelEntityVersion` parent of an object (test and emulation storage).
    pub fn insert_model_parent(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
        parent_type_id: i32,
        parent_id: i32,
    ) -> Result<(), String> {
        let mut store = self.storage_mut()?;
        store.model_parents.insert(
            (model_id, entity_type_id, entity_id),
            (parent_type_id, parent_id),
        );
        Ok(())
    }

    /// Inserts a new active model entity version record.
    pub fn insert_model_entity_version(&self, rec: ModelEntityVersionRecord) -> Result<(), String> {
        self.clear_bulk_coverage();
        let escaped_name = crate::udm::writer::escape_sql_literal(&rec.name);
        self.execute_live_write_sql(&format!(
            "SET NOCOUNT ON; INSERT INTO ModelEntityVersion (ModelId, EntityTypeId, EntityId, EntityVersionId, ModelEntityVersionName) VALUES ({}, {}, {}, {}, N'{}');",
            rec.model_id,
            rec.entity_type_id,
            rec.entity_id,
            rec.entity_version_id,
            escaped_name
        ))?;
        let mut store = self.storage_mut()?;
        store.model_entity_versions.retain(|m| {
            !(m.model_id == rec.model_id
                && m.entity_type_id == rec.entity_type_id
                && m.entity_id == rec.entity_id)
        });
        store.model_entity_versions.push(rec);
        Ok(())
    }

    /// Updates the version ID or name of an existing active model entity.
    pub fn update_model_entity_version(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
        new_version_id: i32,
        new_name: Option<&str>,
    ) -> Result<(), String> {
        self.clear_bulk_coverage();
        let name_clause = match new_name {
            Some(name) => format!(
                ", ModelEntityVersionName = N'{}'",
                crate::udm::writer::escape_sql_literal(name)
            ),
            None => String::new(),
        };
        self.execute_live_write_sql(&format!(
            "SET NOCOUNT ON; UPDATE ModelEntityVersion SET EntityVersionId = {}{name_clause} WHERE ModelId = {} AND EntityTypeId = {} AND EntityId = {};",
            new_version_id, model_id, entity_type_id, entity_id
        ))?;
        let mut store = self.storage_mut()?;
        if let Some(record) = store.model_entity_versions.iter_mut().find(|m| {
            m.model_id == model_id && m.entity_type_id == entity_type_id && m.entity_id == entity_id
        }) {
            record.entity_version_id = new_version_id;
            if let Some(name) = new_name {
                record.name = name.to_string();
            }
            Ok(())
        } else {
            Err(format!(
                "ModelEntityVersion not found for ModelId {model_id}, Type {entity_type_id}, Entity {entity_id}"
            ))
        }
    }

    /// Inserts a version payload record into `EntityVersion`.
    pub fn insert_entity_version(&self, rec: EntityVersionRecord) -> Result<(), String> {
        self.clear_bulk_coverage();
        let hex_blob = crate::udm::writer::format_sql_varbinary_hex(&rec.data);
        self.execute_live_write_sql(&format!(
            "SET NOCOUNT ON; INSERT INTO EntityVersion (EntityTypeId, EntityId, EntityVersionId, EntityVersionData) VALUES ({}, {}, {}, {});",
            rec.entity_type_id, rec.entity_id, rec.entity_version_id, hex_blob
        ))?;
        let mut store = self.storage_mut()?;
        store.entity_versions.retain(|ev| {
            !(ev.entity_type_id == rec.entity_type_id
                && ev.entity_id == rec.entity_id
                && ev.entity_version_id == rec.entity_version_id)
        });
        store.entity_versions.push(rec);
        Ok(())
    }

    /// Inserts a part composition link into `EntityVersionComposition`.
    pub fn insert_composition(&self, rec: EntityVersionCompositionRecord) -> Result<(), String> {
        self.clear_bulk_coverage();
        self.execute_live_write_sql(&format!(
            "SET NOCOUNT ON; INSERT INTO EntityVersionComposition (CompoundEntityTypeId, CompoundEntityId, CompoundEntityVersionId, ComponentEntityTypeId, ComponentEntityId, ComponentEntityVersionId) VALUES ({}, {}, {}, {}, {}, {});",
            rec.compound_entity_type_id,
            rec.compound_entity_id,
            rec.compound_entity_version_id,
            rec.component_entity_type_id,
            rec.component_entity_id,
            rec.component_entity_version_id
        ))?;
        let mut store = self.storage_mut()?;
        store.entity_version_compositions.push(rec);
        Ok(())
    }

    /// Clears part composition links for a specific compound version.
    pub fn clear_compositions(
        &self,
        compound_type: i32,
        compound_id: i32,
        compound_ver: i32,
    ) -> Result<(), String> {
        self.clear_bulk_coverage();
        self.execute_live_write_sql(&format!(
            "SET NOCOUNT ON; DELETE FROM EntityVersionComposition WHERE CompoundEntityTypeId = {compound_type} AND CompoundEntityId = {compound_id} AND CompoundEntityVersionId = {compound_ver};"
        ))?;
        let mut store = self.storage_mut()?;
        store.entity_version_compositions.retain(|c| {
            !(c.compound_entity_type_id == compound_type
                && c.compound_entity_id == compound_id
                && c.compound_entity_version_id == compound_ver)
        });
        Ok(())
    }

    /// Inserts a cross-reference link into `ModelCrossReference`.
    pub fn insert_cross_reference(&self, rec: ModelCrossReferenceRecord) -> Result<(), String> {
        self.execute_live_write_sql(&format!(
            "SET NOCOUNT ON; INSERT INTO ModelCrossReference (ModelId, FromEntityTypeId, FromEntityId, ToEntityTypeId, ToEntityId, ReferenceType) VALUES ({}, {}, {}, {}, {}, {});",
            rec.model_id,
            rec.from_entity_type_id,
            rec.from_entity_id,
            rec.to_entity_type_id,
            rec.to_entity_id,
            rec.reference_type
        ))?;
        let mut store = self.storage_mut()?;
        store.model_cross_references.push(rec);
        Ok(())
    }

    /// Clears cross-references originating from a specific object in a model.
    pub fn clear_cross_references(
        &self,
        model_id: i32,
        from_type: i32,
        from_id: i32,
    ) -> Result<(), String> {
        self.execute_live_write_sql(&format!(
            "SET NOCOUNT ON; DELETE FROM ModelCrossReference WHERE ModelId = {} AND FromEntityTypeId = {} AND FromEntityId = {};",
            model_id, from_type, from_id
        ))?;
        let mut store = self.storage_mut()?;
        store.model_cross_references.retain(|xr| {
            !(xr.model_id == model_id
                && xr.from_entity_type_id == from_type
                && xr.from_entity_id == from_id)
        });
        Ok(())
    }

    /// Deletes an object from `ModelEntityVersion` and related links.
    pub fn delete_object(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
    ) -> Result<bool, String> {
        self.clear_bulk_coverage();
        self.execute_live_write_sql(&format!(
            "SET NOCOUNT ON; DELETE FROM ModelCrossReference WHERE ModelId = {} AND FromEntityTypeId = {} AND FromEntityId = {}; DELETE FROM ModelEntityVersion WHERE ModelId = {} AND EntityTypeId = {} AND EntityId = {};",
            model_id, entity_type_id, entity_id, model_id, entity_type_id, entity_id
        ))?;
        let mut store = self.storage_mut()?;
        let len_before = store.model_entity_versions.len();
        store.model_entity_versions.retain(|m| {
            !(m.model_id == model_id
                && m.entity_type_id == entity_type_id
                && m.entity_id == entity_id)
        });

        let found = store.model_entity_versions.len() < len_before;
        if found {
            store.model_cross_references.retain(|xr| {
                !(xr.model_id == model_id
                    && xr.from_entity_type_id == entity_type_id
                    && xr.from_entity_id == entity_id)
            });
        }
        Ok(found)
    }

    /// Attempts to acquire a lock in the `State` table.
    pub fn acquire_lock(
        &self,
        state_name: &str,
        user_name: &str,
        guid: &str,
        machine_name: &str,
    ) -> Result<bool, String> {
        let mut store = self.storage_mut()?;

        if let Some(existing) = store
            .state_locks
            .iter()
            .find(|l| l.state_name.eq_ignore_ascii_case(state_name))
        {
            if existing.state_guid == guid {
                return Ok(true);
            }
            return Ok(false);
        }

        store.state_locks.push(StateLockRecord {
            state_name: state_name.to_string(),
            state_user_name: user_name.to_string(),
            state_guid: guid.to_string(),
            owner_machine_name: machine_name.to_string(),
        });
        Ok(true)
    }

    /// Releases a previously acquired lock from the `State` table.
    pub fn release_lock(&self, state_name: &str, guid: &str) -> Result<bool, String> {
        let mut store = self.storage_mut()?;
        let len_before = store.state_locks.len();
        store
            .state_locks
            .retain(|l| !(l.state_name.eq_ignore_ascii_case(state_name) && l.state_guid == guid));
        Ok(store.state_locks.len() < len_before)
    }

    /// Inspects the status of a specific lock by name.
    pub fn get_lock(&self, state_name: &str) -> Result<Option<StateLockRecord>, String> {
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .state_locks
            .iter()
            .find(|l| l.state_name.eq_ignore_ascii_case(state_name))
            .cloned())
    }

    /// Lists all currently active state locks.
    pub fn list_locks(&self) -> Result<Vec<StateLockRecord>, String> {
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store.state_locks.clone())
    }
}
