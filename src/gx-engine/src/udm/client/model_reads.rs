//! Reads of one model: active objects, parents, compositions, entity versions, entity types and cross references.

use super::live_rows::{entity_version_from_fields, parse_live_int, parse_live_rows};
use super::*;

impl UdmClient {
    /// The stored `GenerateObject` value of each object of `entity_type_id` in `model_id`, keyed by entity
    /// id, as written ("True" or "False"; any other text is returned as stored and judged by the caller).
    /// The property is a model-level row (`ModelEntityProperty`) whose property id is the entity id of the
    /// `Udm.Types.Property` row named `Property: GenerateObject`; the join resolves that id by name. A
    /// Knowledge Base without that row has no value, and an object without a row is absent here.
    pub fn generate_flags(
        &self,
        model_id: i32,
        entity_type_id: i32,
    ) -> Result<BTreeMap<i32, String>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let in_memory: BTreeMap<i32, String> = store
                .generate_flags
                .iter()
                .filter(|((m, t, _), _)| *m == model_id && *t == entity_type_id)
                .map(|((_, _, id), value)| (*id, value.clone()))
                .collect();
            if !in_memory.is_empty() {
                return Ok(in_memory);
            }
        }
        if self.connection_info.is_none() {
            return Ok(BTreeMap::new());
        }
        let sql = format!(
            "SET NOCOUNT ON; SELECT m.EntityId, m.ModelEntityPropertyValue FROM ModelEntityProperty m JOIN EntityVersion p ON p.EntityId = m.ModelEntityPropertyId AND p.EntityVersionName = 'Property: GenerateObject' JOIN EntityType t ON t.EntityTypeId = p.EntityTypeId AND t.EntityTypeName = 'Udm.Types.Property' WHERE m.ModelId = {model_id} AND m.EntityTypeId = {entity_type_id}"
        );
        let raw = self.execute_live_sql(&sql)?;
        let mut flags: BTreeMap<i32, String> = BTreeMap::new();
        for row in parse_live_rows(&raw, 2)? {
            let id = parse_live_int(row[0], "EntityId")?;
            let value = row[1].trim().to_string();
            // The same object under two property rows with different values is refused, never resolved.
            if let Some(previous) = flags.get(&id) {
                if *previous != value {
                    return Err(format!(
                        "GenerateObject of entity {id} is stored twice with values '{previous}' and '{value}'"
                    ));
                }
            }
            flags.insert(id, value);
        }
        Ok(flags)
    }

    /// The stored `IsMain` ("Main program") property of each object of `entity_type_id`, by entity id. The SDK
    /// generates a Procedure that is not main only when a generated object calls it (spc0217, "Object is
    /// unreachable"). A client without a live database answers nothing, and every Procedure stays a root.
    pub fn main_flags(
        &self,
        model_id: i32,
        entity_type_id: i32,
    ) -> Result<BTreeMap<i32, bool>, String> {
        if self.connection_info.is_none() {
            return Ok(BTreeMap::new());
        }
        let sql = format!(
            "SET NOCOUNT ON; SELECT m.EntityId, m.ModelEntityPropertyValue FROM ModelEntityProperty m JOIN EntityVersion p ON p.EntityId = m.ModelEntityPropertyId AND p.EntityVersionName = 'Property: IsMain' JOIN EntityType t ON t.EntityTypeId = p.EntityTypeId AND t.EntityTypeName = 'Udm.Types.Property' WHERE m.ModelId = {model_id} AND m.EntityTypeId = {entity_type_id}"
        );
        let raw = self.execute_live_sql(&sql)?;
        let mut flags = BTreeMap::new();
        for row in parse_live_rows(&raw, 2)? {
            let id = parse_live_int(row[0], "EntityId")?;
            let value = match row[1].trim() {
                "True" => true,
                "False" => false,
                other => return Err(format!("IsMain of entity {id} is stored as '{other}'")),
            };
            if flags
                .insert(id, value)
                .is_some_and(|previous| previous != value)
            {
                return Err(format!(
                    "IsMain of entity {id} is stored twice with different values"
                ));
            }
        }
        Ok(flags)
    }

    pub fn get_active_objects(
        &self,
        model_id: i32,
    ) -> Result<Vec<ModelEntityVersionRecord>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let in_memory: Vec<_> = store
                .model_entity_versions
                .iter()
                .filter(|m| m.model_id == model_id)
                .cloned()
                .collect();
            if !in_memory.is_empty() {
                return Ok(in_memory);
            }
        }

        if self.connection_info.is_some() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT EntityTypeId, EntityId, EntityVersionId, ModelEntityVersionName FROM ModelEntityVersion WHERE ModelId = {model_id}"
            );
            // A live read that fails is an error: returning the (empty) in-memory
            // cache here reported "no objects" for a database the client could
            // not read.
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 4)?;
            let mut fetched = Vec::with_capacity(rows.len());
            for row in rows {
                fetched.push(ModelEntityVersionRecord {
                    model_id,
                    entity_type_id: parse_live_int(row[0], "EntityTypeId")?,
                    entity_id: parse_live_int(row[1], "EntityId")?,
                    entity_version_id: parse_live_int(row[2], "EntityVersionId")?,
                    name: row[3].to_string(),
                });
            }
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            for rec in fetched {
                if !store.model_entity_versions.iter().any(|m| {
                    m.model_id == rec.model_id
                        && m.entity_type_id == rec.entity_type_id
                        && m.entity_id == rec.entity_id
                }) {
                    store.model_entity_versions.push(rec);
                }
            }
            return Ok(store
                .model_entity_versions
                .iter()
                .filter(|m| m.model_id == model_id)
                .cloned()
                .collect());
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .model_entity_versions
            .iter()
            .filter(|m| m.model_id == model_id)
            .cloned()
            .collect())
    }

    /// The holder (module or folder) of every row of the model, read from the live database. A row without a
    /// holder has a zero parent type. An in-memory client holds no parent links and returns none.
    pub fn get_model_parents(&self, model_id: i32) -> Result<Vec<ModelParentRecord>, String> {
        if self.connection_info.is_none() {
            return Ok(Vec::new());
        }
        let sql = format!(
            "SET NOCOUNT ON; SELECT EntityTypeId, EntityId, ModelParentEntityTypeId, ModelParentEntityId FROM ModelEntityVersion WHERE ModelId = {model_id}"
        );
        self.live_records(&sql, 4, |row| {
            Ok(ModelParentRecord {
                entity_type_id: parse_live_int(row[0], "EntityTypeId")?,
                entity_id: parse_live_int(row[1], "EntityId")?,
                parent_entity_type_id: parse_live_int(row[2], "ModelParentEntityTypeId")?,
                parent_entity_id: parse_live_int(row[3], "ModelParentEntityId")?,
            })
        })
    }

    /// The stored identity and last-update inputs of one exact active object version.
    ///
    /// The SDK's `KBObject.Guid`, `KBObject.Parent` and `KBObject.LastUpdate` are the
    /// token inputs of `WriteService.ComputeVersionToken`. Last update is the active
    /// version's `ObjectSaved` output (`OutputTypeId=301`, `OutputTypeVersion=0`);
    /// when that row is absent it is `ModelEntityVersion.ModelEntityVersionTimestamp`.
    /// An `ObjectSaved` row that exists with a `NULL` timestamp is refused, not treated as
    /// absent. A parent row of `(0, 0)` means no parent; its name is read from the parent's
    /// own row in this model only. The live query refuses a duplicated parent or
    /// `ObjectSaved` row, and never converts the stored value between time zones.
    pub fn get_object_read_metadata(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
        entity_version_id: i32,
    ) -> Result<Option<ObjectReadMetadata>, String> {
        if self.connection_info.is_none() {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let parent = store
                .model_parents
                .get(&(model_id, entity_type_id, entity_id))
                .copied();
            let has_metadata = store
                .entity_guids
                .contains_key(&(entity_type_id, entity_id))
                || parent.is_some()
                || store.object_saved_timestamps.contains_key(&(
                    model_id,
                    entity_type_id,
                    entity_id,
                    entity_version_id,
                ))
                || store.model_entity_version_timestamps.contains_key(&(
                    model_id,
                    entity_type_id,
                    entity_id,
                    entity_version_id,
                ));
            if !has_metadata {
                return Ok(None);
            }
            let parent = match parent {
                Some((0, 0)) => None,
                Some((0, id)) | Some((id, 0)) => {
                    return Err(format!(
                        "parent of entity {entity_id} is stored with a zero half: ({id}, 0)"
                    ));
                }
                other => other,
            };
            let parent_guid = parent.and_then(|(parent_type, parent_id)| {
                store.entity_guids.get(&(parent_type, parent_id)).cloned()
            });
            let parent_name = match parent {
                Some((parent_type, parent_id)) => {
                    // Only the active model's own row of the parent is a candidate; any
                    // second row of it (even with the same name) is refused, like the live join.
                    let names = store
                        .model_entity_versions
                        .iter()
                        .filter(|row| {
                            row.model_id == model_id
                                && row.entity_type_id == parent_type
                                && row.entity_id == parent_id
                        })
                        .map(|row| row.name.as_str())
                        .collect::<Vec<_>>();
                    match names.as_slice() {
                        [] => None,
                        [name] => Some((*name).to_string()),
                        _ => {
                            return Err(format!(
                                "parent ({parent_type}, {parent_id}) is stored {} times in model {model_id}; expected one",
                                names.len()
                            ));
                        }
                    }
                }
                None => None,
            };
            return Ok(Some(ObjectReadMetadata {
                guid: store
                    .entity_guids
                    .get(&(entity_type_id, entity_id))
                    .cloned(),
                parent,
                parent_guid,
                parent_name,
                object_saved_ticks: store
                    .object_saved_timestamps
                    .get(&(model_id, entity_type_id, entity_id, entity_version_id))
                    .copied(),
                model_entity_version_ticks: store
                    .model_entity_version_timestamps
                    .get(&(model_id, entity_type_id, entity_id, entity_version_id))
                    .copied(),
            }));
        }

        let sql = object_read_metadata_sql(model_id, entity_type_id, entity_id, entity_version_id);
        let raw = self.execute_live_sql(&sql)?;
        let rows = parse_live_rows(&raw, OBJECT_READ_METADATA_COLUMNS)?;
        object_read_metadata_from_rows(entity_id, &rows)
    }

    /// The last-update ticks of a batch of active object versions in `model_id` of `entity_type_id`,
    /// keyed by `entity_id`.
    ///
    /// Each requested `(entity_id, entity_version_id)` resolves
    /// `object_saved_ticks.or(model_entity_version_ticks)` under the same rules as
    /// [`Self::get_object_read_metadata`]: an `ObjectSaved` row (`OutputTypeId=301`,
    /// `OutputTypeVersion=0`) that exists with a `NULL` or unparseable timestamp, or a duplicated
    /// row for an entity, fails closed with an error. An entity with no stored metadata row is
    /// omitted from the returned map so the caller can refuse missing lifecycle metadata.
    pub fn get_objects_lifecycle_ticks(
        &self,
        model_id: i32,
        entity_type_id: i32,
        versions: &[(i32, i32)],
    ) -> Result<BTreeMap<i32, Option<i64>>, String> {
        if versions.is_empty() {
            return Ok(BTreeMap::new());
        }
        let mut seen_entities = std::collections::BTreeSet::new();
        for &(entity_id, _) in versions {
            if !seen_entities.insert(entity_id) {
                return Err(format!(
                    "lifecycle metadata batch contains duplicate entity {entity_id}"
                ));
            }
        }
        if self.connection_info.is_none() {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let mut ticks_by_entity = BTreeMap::new();
            for &(entity_id, entity_version_id) in versions {
                let has_metadata = store
                    .entity_guids
                    .contains_key(&(entity_type_id, entity_id))
                    || store
                        .model_parents
                        .contains_key(&(model_id, entity_type_id, entity_id))
                    || store.object_saved_timestamps.contains_key(&(
                        model_id,
                        entity_type_id,
                        entity_id,
                        entity_version_id,
                    ))
                    || store.model_entity_version_timestamps.contains_key(&(
                        model_id,
                        entity_type_id,
                        entity_id,
                        entity_version_id,
                    ));
                if !has_metadata {
                    continue;
                }
                let object_saved_ticks = store
                    .object_saved_timestamps
                    .get(&(model_id, entity_type_id, entity_id, entity_version_id))
                    .copied();
                let model_entity_version_ticks = store
                    .model_entity_version_timestamps
                    .get(&(model_id, entity_type_id, entity_id, entity_version_id))
                    .copied();
                ticks_by_entity
                    .insert(entity_id, object_saved_ticks.or(model_entity_version_ticks));
            }
            return Ok(ticks_by_entity);
        }

        let sql = objects_lifecycle_ticks_sql(model_id, entity_type_id, versions);
        let raw = self.execute_live_sql(&sql)?;
        let rows = parse_live_rows(&raw, OBJECTS_LIFECYCLE_TICKS_COLUMNS)?;
        objects_lifecycle_ticks_from_rows(&rows)
    }

    /// Retrieves part compositions for a compound entity version.
    pub fn get_compositions(
        &self,
        compound_type: i32,
        compound_id: i32,
        compound_ver: i32,
    ) -> Result<Vec<EntityVersionCompositionRecord>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let in_memory: Vec<_> = store
                .entity_version_compositions
                .iter()
                .filter(|c| {
                    c.compound_entity_type_id == compound_type
                        && c.compound_entity_id == compound_id
                        && c.compound_entity_version_id == compound_ver
                })
                .cloned()
                .collect();
            if !in_memory.is_empty() {
                return Ok(in_memory);
            }
        }

        if self.connection_info.is_some()
            && !self.composition_is_covered(compound_type, compound_id, compound_ver)
        {
            let sql = format!(
                "SET NOCOUNT ON; SELECT ComponentEntityTypeId, ComponentEntityId, ComponentEntityVersionId FROM EntityVersionComposition WHERE CompoundEntityTypeId = {compound_type} AND CompoundEntityId = {compound_id} AND CompoundEntityVersionId = {compound_ver}"
            );
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 3)?;
            let mut fetched = Vec::with_capacity(rows.len());
            for row in rows {
                fetched.push(EntityVersionCompositionRecord {
                    compound_entity_type_id: compound_type,
                    compound_entity_id: compound_id,
                    compound_entity_version_id: compound_ver,
                    component_entity_type_id: parse_live_int(row[0], "ComponentEntityTypeId")?,
                    component_entity_id: parse_live_int(row[1], "ComponentEntityId")?,
                    component_entity_version_id: parse_live_int(
                        row[2],
                        "ComponentEntityVersionId",
                    )?,
                });
            }
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            for rec in fetched {
                if !store.entity_version_compositions.iter().any(|c| {
                    c.compound_entity_type_id == rec.compound_entity_type_id
                        && c.compound_entity_id == rec.compound_entity_id
                        && c.compound_entity_version_id == rec.compound_entity_version_id
                        && c.component_entity_type_id == rec.component_entity_type_id
                        && c.component_entity_id == rec.component_entity_id
                }) {
                    store.entity_version_compositions.push(rec);
                }
            }
            return Ok(store
                .entity_version_compositions
                .iter()
                .filter(|c| {
                    c.compound_entity_type_id == compound_type
                        && c.compound_entity_id == compound_id
                        && c.compound_entity_version_id == compound_ver
                })
                .cloned()
                .collect());
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .entity_version_compositions
            .iter()
            .filter(|c| {
                c.compound_entity_type_id == compound_type
                    && c.compound_entity_id == compound_id
                    && c.compound_entity_version_id == compound_ver
            })
            .cloned()
            .collect())
    }

    /// Retrieves an entity version payload record.
    pub fn get_entity_version(
        &self,
        entity_type: i32,
        entity_id: i32,
        version_id: i32,
    ) -> Result<Option<EntityVersionRecord>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            if let Some(rec) = store.entity_versions.iter().find(|ev| {
                ev.entity_type_id == entity_type
                    && ev.entity_id == entity_id
                    && ev.entity_version_id == version_id
            }) {
                return Ok(Some(rec.clone()));
            }
        }

        if self.connection_info.is_some()
            && !self.entity_version_is_covered(entity_type, entity_id, version_id)
        {
            // One row when the version exists (an empty payload is still a row),
            // none when it does not: the caller tells a missing part from an empty one.
            // The properties are read as hex too: the column is free text (a value can hold a
            // newline), and hex keeps every row on one tab-separated line with a declared length.
            let sql = format!(
                "SET NOCOUNT ON; SELECT 'PRESENT', ISNULL(DATALENGTH(EntityVersionData), 0), CONVERT(VARCHAR(MAX), EntityVersionData, 2), ISNULL(DATALENGTH(EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(EntityVersionProperties AS VARBINARY(MAX)), 2) FROM EntityVersion WHERE EntityTypeId = {entity_type} AND EntityId = {entity_id} AND EntityVersionId = {version_id}"
            );
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 5)?;
            match rows.as_slice() {
                [] => {}
                [row] => {
                    let rec = entity_version_from_fields(
                        entity_type,
                        entity_id,
                        version_id,
                        row[1],
                        row[2],
                        row[3],
                        row[4],
                    )?;
                    let mut store = self
                        .storage
                        .write()
                        .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                    store.entity_versions.push(rec.clone());
                    return Ok(Some(rec));
                }
                _ => {
                    return Err(format!(
                        "EntityVersion ({entity_type}, {entity_id}, {version_id}) matched more than one row"
                    ));
                }
            }
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .entity_versions
            .iter()
            .find(|ev| {
                ev.entity_type_id == entity_type
                    && ev.entity_id == entity_id
                    && ev.entity_version_id == version_id
            })
            .cloned())
    }

    /// Returns the `EntityType` rows of this Knowledge Base. A live client reads
    /// its own table on every call (no seeds involved); an in-memory client
    /// returns its declared vocabulary.
    pub fn get_entity_types(&self) -> Result<Vec<EntityTypeRecord>, String> {
        if self.connection_info.is_some() {
            let raw = self.execute_live_sql(
                "SET NOCOUNT ON; SELECT EntityTypeId, EntityTypeName, ISNULL(EntityTypeNamespace, '') FROM EntityType",
            )?;
            let rows = parse_live_rows(&raw, 3)?;
            return rows
                .iter()
                .map(|row| {
                    Ok(EntityTypeRecord {
                        entity_type_id: parse_live_int(row[0], "EntityTypeId")?,
                        name: row[1].to_string(),
                        namespace: row[2].to_string(),
                    })
                })
                .collect();
        }
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store.entity_types.clone())
    }

    /// The `(EntityTypeId, EntityId)` of this model's Knowledge Base root module, or
    /// `None` when the model has no root.
    ///
    /// The root is identified by its owner data, never by its display name: a
    /// parent-less Module with `EntityId` 1 whose `Entity.EntityGuid` is
    /// [`SYSTEM_ROOT_MODULE_GUID`]. A user Module named `Root Module` under a parent
    /// is not the root, and neither is an `EntityId` 1 with a parent or another GUID.
    pub fn system_root_module(
        &self,
        model_id: i32,
        module_type_id: i32,
    ) -> Result<Option<(i32, i32)>, String> {
        if self.is_live() {
            let sql = system_root_module_sql(model_id, module_type_id);
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, SYSTEM_ROOT_MODULE_COLUMNS)?;
            return system_root_module_from_rows(model_id, module_type_id, &rows);
        }
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        let present = store.model_entity_versions.iter().any(|m| {
            m.model_id == model_id && m.entity_type_id == module_type_id && m.entity_id == 1
        });
        let parent_less = store.model_parents.get(&(model_id, module_type_id, 1)) == Some(&(0, 0));
        let guid_matches = store
            .entity_guids
            .get(&(module_type_id, 1))
            .is_some_and(|guid| guid.eq_ignore_ascii_case(SYSTEM_ROOT_MODULE_GUID));
        Ok((present && parent_less && guid_matches).then_some((module_type_id, 1)))
    }

    /// The served vocabulary of this Knowledge Base, keyed by its own entity
    /// type ids. A live client reads `EntityType` once and keeps the result for
    /// every clone of the client; an in-memory client resolves its current table
    /// each time, so a test that declares a vocabulary sees it immediately.
    pub fn entity_type_resolver(&self) -> Result<Arc<EntityTypeResolver>, String> {
        if !self.is_live() {
            let records = self.get_entity_types()?;
            return Ok(Arc::new(EntityTypeResolver::from_records(&records)?));
        }
        if let Some(cached) = self.live_vocabulary.get() {
            return Ok(Arc::clone(cached));
        }
        let records = self.get_entity_types()?;
        let resolver = Arc::new(EntityTypeResolver::from_records(&records)?);
        // A concurrent first call may have stored an equal resolver already: both
        // were read from the same table, so either one is correct.
        let _ = self.live_vocabulary.set(Arc::clone(&resolver));
        Ok(resolver)
    }

    /// Retrieves cross-references originating from a specific object in a model.
    pub fn get_cross_references(
        &self,
        model_id: i32,
        from_type_id: i32,
        from_id: i32,
    ) -> Result<Vec<ModelCrossReferenceRecord>, String> {
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .model_cross_references
            .iter()
            .filter(|xr| {
                xr.model_id == model_id
                    && xr.from_entity_type_id == from_type_id
                    && xr.from_entity_id == from_id
            })
            .cloned()
            .collect())
    }

    /// Retrieves every cross-reference of a model. With a live connection the
    /// whole table is read once and merged into the in-memory cache, so
    /// per-object hydration stays in memory instead of issuing one query per
    /// object; in-memory clients just return the cached records.
    pub fn get_cross_references_for_model(
        &self,
        model_id: i32,
    ) -> Result<Vec<ModelCrossReferenceRecord>, String> {
        if self.connection_info.is_some() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT FromEntityTypeId, FromEntityId, ToEntityTypeId, ToEntityId, ReferenceType FROM ModelCrossReference WHERE ModelId = {model_id}"
            );
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 5)?;
            let mut fetched = Vec::with_capacity(rows.len());
            for row in rows {
                fetched.push(ModelCrossReferenceRecord {
                    model_id,
                    from_entity_type_id: parse_live_int(row[0], "FromEntityTypeId")?,
                    from_entity_id: parse_live_int(row[1], "FromEntityId")?,
                    to_entity_type_id: parse_live_int(row[2], "ToEntityTypeId")?,
                    to_entity_id: parse_live_int(row[3], "ToEntityId")?,
                    reference_type: parse_live_int(row[4], "ReferenceType")?,
                });
            }
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            for rec in fetched {
                if !store.model_cross_references.iter().any(|x| {
                    x.model_id == rec.model_id
                        && x.from_entity_type_id == rec.from_entity_type_id
                        && x.from_entity_id == rec.from_entity_id
                        && x.to_entity_type_id == rec.to_entity_type_id
                        && x.to_entity_id == rec.to_entity_id
                        && x.reference_type == rec.reference_type
                }) {
                    store.model_cross_references.push(rec);
                }
            }
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .model_cross_references
            .iter()
            .filter(|xr| xr.model_id == model_id)
            .cloned()
            .collect())
    }
}

const SYSTEM_ROOT_MODULE_COLUMNS: usize = 3;

/// The candidate-row query behind [`UdmClient::system_root_module`]: the model's parent-less
/// `EntityId` 1 row of the Module type, with its owner GUID.
///
/// The GUID is converted to `VARCHAR(36)` before `ISNULL`, because `ISNULL` casts its
/// replacement to the type of its first argument: on a `uniqueidentifier` column `''` is a
/// failed conversion, not an absent GUID. A missing `Entity` row or a `NULL` GUID arrives as
/// an empty field and never matches the root GUID.
///
/// Columns: entity type id, entity id, owner GUID.
fn system_root_module_sql(model_id: i32, module_type_id: i32) -> String {
    format!(
        "SET NOCOUNT ON; SELECT m.EntityTypeId, m.EntityId, ISNULL(CONVERT(VARCHAR(36), e.EntityGuid), '') \
         FROM ModelEntityVersion m \
         LEFT JOIN Entity e ON e.EntityTypeId = m.EntityTypeId AND e.EntityId = m.EntityId \
         WHERE m.ModelId = {model_id} AND m.EntityTypeId = {module_type_id} AND m.EntityId = 1 \
           AND m.ModelParentEntityTypeId = 0 AND m.ModelParentEntityId = 0"
    )
}

/// Interprets the rows of [`system_root_module_sql`]. No row, or a row whose GUID is empty
/// or another value, is no root. A second candidate row is refused rather than resolved by
/// order, and a row that is not the requested `(module type, 1)` is refused as malformed.
fn system_root_module_from_rows(
    model_id: i32,
    module_type_id: i32,
    rows: &[Vec<&str>],
) -> Result<Option<(i32, i32)>, String> {
    let row = match rows {
        [] => return Ok(None),
        [row] => row,
        _ => {
            return Err(format!(
                "system root module candidates of model {model_id} matched {} rows; expected at most one",
                rows.len()
            ));
        }
    };
    if row.len() != SYSTEM_ROOT_MODULE_COLUMNS {
        return Err(format!(
            "system root module row has {} fields; expected {SYSTEM_ROOT_MODULE_COLUMNS}",
            row.len()
        ));
    }
    let entity_type = parse_live_int(row[0], "EntityTypeId")?;
    let entity_id = parse_live_int(row[1], "EntityId")?;
    if entity_type != module_type_id || entity_id != 1 {
        return Err(format!(
            "system root module candidate is ({entity_type}, {entity_id}); expected ({module_type_id}, 1)"
        ));
    }
    let guid = row[2].trim();
    Ok(guid
        .eq_ignore_ascii_case(SYSTEM_ROOT_MODULE_GUID)
        .then_some((entity_type, entity_id)))
}

const OBJECT_READ_METADATA_COLUMNS: usize = 9;
const ROW_PRESENT: &str = "PRESENT";
const ROW_ABSENT: &str = "ABSENT";

/// The one-row query behind [`UdmClient::get_object_read_metadata`].
///
/// Every join is bound to the requested model, type, entity and exact version. The parent's
/// name comes from the parent's own `ModelEntityVersion` row **in the same model**, joined
/// without any ordering or `TOP`: a second row of that parent in the model, or a second
/// `ObjectSaved` row, multiplies the result and is refused by the caller instead of being
/// resolved by a guess. `PRESENT`/`ABSENT` markers come from join-key columns, so a row that
/// exists with a `NULL` timestamp is distinguishable from a row that does not exist. Entity
/// GUIDs are converted to `VARCHAR(36)` before `ISNULL`, so a `uniqueidentifier` column is
/// never coerced from `''`.
///
/// Columns: object GUID, parent type, parent id, parent GUID, parent-row marker, parent name,
/// `ObjectSaved`-row marker, `ObjectSaved` timestamp, `ModelEntityVersionTimestamp`.
fn object_read_metadata_sql(
    model_id: i32,
    entity_type_id: i32,
    entity_id: i32,
    entity_version_id: i32,
) -> String {
    format!(
        "SET NOCOUNT ON; SELECT \
         ISNULL(CONVERT(VARCHAR(36), e.EntityGuid), ''), \
         m.ModelParentEntityTypeId, m.ModelParentEntityId, \
         ISNULL(CONVERT(VARCHAR(36), p.EntityGuid), ''), \
         CASE WHEN pm.EntityId IS NULL THEN '{ROW_ABSENT}' ELSE '{ROW_PRESENT}' END, \
         ISNULL(pm.ModelEntityVersionName, ''), \
         CASE WHEN o.OutputTypeId IS NULL THEN '{ROW_ABSENT}' ELSE '{ROW_PRESENT}' END, \
         ISNULL(CONVERT(VARCHAR(33), o.OutputTimestamp, 126), ''), \
         ISNULL(CONVERT(VARCHAR(33), m.ModelEntityVersionTimestamp, 126), '') \
         FROM ModelEntityVersion m \
         JOIN Entity e ON e.EntityTypeId = m.EntityTypeId AND e.EntityId = m.EntityId \
         LEFT JOIN Entity p ON p.EntityTypeId = m.ModelParentEntityTypeId AND p.EntityId = m.ModelParentEntityId \
         LEFT JOIN ModelEntityVersion pm ON pm.ModelId = m.ModelId \
            AND pm.EntityTypeId = m.ModelParentEntityTypeId AND pm.EntityId = m.ModelParentEntityId \
         LEFT JOIN ModelEntityOutput o ON o.ModelId = m.ModelId AND o.EntityTypeId = m.EntityTypeId \
            AND o.EntityId = m.EntityId AND o.OutputEntityVersionId = m.EntityVersionId \
            AND o.OutputTypeId = 301 AND o.OutputTypeVersion = 0 \
         WHERE m.ModelId = {model_id} AND m.EntityTypeId = {entity_type_id} \
           AND m.EntityId = {entity_id} AND m.EntityVersionId = {entity_version_id}"
    )
}

/// Interprets the rows of [`object_read_metadata_sql`]: none is `None`, one is the metadata,
/// more than one (a duplicated parent or `ObjectSaved` row) is refused.
fn object_read_metadata_from_rows(
    entity_id: i32,
    rows: &[Vec<&str>],
) -> Result<Option<ObjectReadMetadata>, String> {
    let row = match rows {
        [] => return Ok(None),
        [row] => row,
        _ => {
            return Err(format!(
                "read metadata of entity {entity_id} matched {} rows; expected one",
                rows.len()
            ));
        }
    };
    if row.len() != OBJECT_READ_METADATA_COLUMNS {
        return Err(format!(
            "read metadata row has {} fields; expected {OBJECT_READ_METADATA_COLUMNS}",
            row.len()
        ));
    }
    let parent_type = parse_live_int(row[1], "ModelParentEntityTypeId")?;
    let parent_id = parse_live_int(row[2], "ModelParentEntityId")?;
    let parent = match (parent_type, parent_id) {
        (0, 0) => None,
        (0, _) | (_, 0) => {
            return Err(format!(
                "parent of entity {entity_id} is stored with a zero half: ({parent_type}, {parent_id})"
            ));
        }
        other => Some(other),
    };
    let parent_row_present = parse_row_marker(row[4], "parent ModelEntityVersion row")?;
    let parent_name = if parent_row_present {
        non_empty(row[5])
    } else {
        None
    };
    // An absent `ObjectSaved` row falls back to the model timestamp; a row that exists
    // without a timestamp has no last update to fall back from and is refused.
    let object_saved_ticks = if parse_row_marker(row[6], "ObjectSaved ModelEntityOutput row")? {
        let ticks = parse_stored_datetime_ticks(row[7], "ModelEntityOutput.OutputTimestamp")?;
        Some(ticks.ok_or_else(|| {
            format!(
                "ObjectSaved ModelEntityOutput row of entity {entity_id} exists with a NULL OutputTimestamp"
            )
        })?)
    } else {
        if !row[7].trim().is_empty() {
            return Err(format!(
                "ObjectSaved ModelEntityOutput row of entity {entity_id} is absent but reports a timestamp"
            ));
        }
        None
    };
    Ok(Some(ObjectReadMetadata {
        guid: non_empty(row[0]),
        parent,
        parent_guid: non_empty(row[3]),
        parent_name,
        object_saved_ticks,
        model_entity_version_ticks: parse_stored_datetime_ticks(
            row[8],
            "ModelEntityVersion.ModelEntityVersionTimestamp",
        )?,
    }))
}

const OBJECTS_LIFECYCLE_TICKS_COLUMNS: usize = 4;

/// The batched query behind [`UdmClient::get_objects_lifecycle_ticks`].
///
/// Columns: entity id, `HasObjectSaved` (`1` when the version's `ObjectSaved` output row exists,
/// `0` when absent), `ObjectSaved` timestamp, `ModelEntityVersionTimestamp`.
fn objects_lifecycle_ticks_sql(
    model_id: i32,
    entity_type_id: i32,
    versions: &[(i32, i32)],
) -> String {
    use std::fmt::Write;
    let mut sql = format!(
        "SET NOCOUNT ON; SELECT \
         m.EntityId, \
         CASE WHEN o.OutputTypeId IS NULL THEN 0 ELSE 1 END AS HasObjectSaved, \
         ISNULL(CONVERT(VARCHAR(33), o.OutputTimestamp, 126), ''), \
         ISNULL(CONVERT(VARCHAR(33), m.ModelEntityVersionTimestamp, 126), '') \
         FROM ModelEntityVersion m \
         LEFT JOIN ModelEntityOutput o ON o.ModelId = m.ModelId AND o.EntityTypeId = m.EntityTypeId \
            AND o.EntityId = m.EntityId AND o.OutputTypeId = 301 AND o.OutputTypeVersion = 0 \
            AND o.OutputEntityVersionId = m.EntityVersionId \
         WHERE m.ModelId = {model_id} AND m.EntityTypeId = {entity_type_id} \
           AND ("
    );
    if versions.is_empty() {
        sql.push_str("1 = 0");
    } else {
        for (idx, (entity_id, entity_version_id)) in versions.iter().enumerate() {
            if idx > 0 {
                sql.push_str(" OR ");
            }
            let _ = write!(
                &mut sql,
                "(m.EntityId = {entity_id} AND m.EntityVersionId = {entity_version_id})"
            );
        }
    }
    sql.push(')');
    sql
}

/// Interprets the rows of [`objects_lifecycle_ticks_sql`].
///
/// Refuses duplicated entity rows and any `ObjectSaved` row whose timestamp is `NULL` or
/// unparseable, matching [`object_read_metadata_from_rows`].
fn objects_lifecycle_ticks_from_rows(
    rows: &[Vec<&str>],
) -> Result<BTreeMap<i32, Option<i64>>, String> {
    let mut ticks_by_entity = BTreeMap::new();
    for row in rows {
        if row.len() != OBJECTS_LIFECYCLE_TICKS_COLUMNS {
            return Err(format!(
                "lifecycle metadata row has {} fields; expected {OBJECTS_LIFECYCLE_TICKS_COLUMNS}",
                row.len()
            ));
        }
        let entity_id = parse_live_int(row[0], "EntityId")?;
        let has_object_saved = match row[1].trim() {
            "1" | ROW_PRESENT => true,
            "0" | ROW_ABSENT => false,
            other => {
                return Err(format!(
                    "ObjectSaved presence marker for entity {entity_id} is '{other}'; expected 0 or 1"
                ));
            }
        };
        let object_saved_ticks = if has_object_saved {
            let ticks = parse_stored_datetime_ticks(row[2], "ModelEntityOutput.OutputTimestamp")
                .map_err(|err| {
                    format!(
                        "ObjectSaved row for entity {entity_id} has NULL or unparseable OutputTimestamp: {err}"
                    )
                })?
                .ok_or_else(|| {
                    format!(
                        "ObjectSaved row for entity {entity_id} has NULL or unparseable OutputTimestamp"
                    )
                })?;
            Some(ticks)
        } else {
            if !row[2].trim().is_empty() {
                return Err(format!(
                    "ObjectSaved row for entity {entity_id} is absent but reports a timestamp"
                ));
            }
            None
        };
        let model_entity_version_ticks =
            parse_stored_datetime_ticks(row[3], "ModelEntityVersion.ModelEntityVersionTimestamp")?;
        if ticks_by_entity
            .insert(entity_id, object_saved_ticks.or(model_entity_version_ticks))
            .is_some()
        {
            return Err(format!(
                "lifecycle metadata of entity {entity_id} matched multiple rows; expected one"
            ));
        }
    }
    Ok(ticks_by_entity)
}

mod ticks;
#[cfg(test)]
pub(super) use ticks::parse_datetime_ticks;
use ticks::{non_empty, parse_row_marker, parse_stored_datetime_ticks};

#[cfg(test)]
mod tests;
