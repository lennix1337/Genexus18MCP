//! Direct Knowledge Base Writer and Transactional Mutation Engine for GeneXus UDM storage.
//!
//! Provides `UdmWriter` implementing atomic object mutations (`create_object`, `save_object`,
//! `delete_object`), updating `EntityVersion`, `ModelEntityVersion`, `EntityVersionComposition`,
//! and `ModelCrossReference`, and managing concurrent multi-user / agent locks in `State`.

#![forbid(unsafe_code)]

use crate::catalog::{KbObject, ObjectId, Reference, ReferenceKind, SourcePart};
use crate::udm::client::{
    EntityVersionCompositionRecord, EntityVersionRecord, ModelCrossReferenceRecord,
    ModelEntityVersionRecord, StateLockRecord, UdmClient,
};
use crate::udm::decoder::{
    UdmPartKind, build_token_data_list, decode_blob, encode_blob, extract_token_data_list,
};
use crate::udm::entity_types::EntityTypeResolver;
use crate::udm::reader::UdmReader;
use std::collections::BTreeSet;
use std::fmt::Write as _;
use std::sync::Arc;

/// The stored part rows of an object, in write order: the Knowledge Base's own
/// entity type id, the engine part kind, and the part. A part name the engine does
/// not serve, or a part type the Knowledge Base does not declare for this object
/// kind, is refused before anything is written. A row stored under an undeclared
/// entity type (the old code stored `0`) is corrupt data.
fn stored_parts<'a>(
    resolver: &EntityTypeResolver,
    object: &'a KbObject,
) -> Result<Vec<(i32, UdmPartKind, &'a SourcePart)>, String> {
    object
        .source_parts
        .iter()
        .map(|part| {
            let kind = match UdmPartKind::from_name(&part.name) {
                UdmPartKind::Unknown(_) => {
                    return Err(format!(
                        "part '{}' of '{}' is not a part this engine stores; refusing to write it under an undeclared entity type",
                        part.name, object.name
                    ));
                }
                kind => kind,
            };
            let type_id = resolver.part_type_id(object.kind, kind)?;
            Ok((type_id, kind, part))
        })
        .collect()
}

/// The `(entity type, entity id)` a reference points to. The object being written
/// (a self-reference) is resolved from `self_entity`, because a new object is not in
/// the `active` snapshot yet. Any other target that is not an object of the model is
/// refused: storing it under an invented id left a dangling row in the model.
fn reference_target(
    active: &[ModelEntityVersionRecord],
    self_entity: (i32, i32),
    object_name: &str,
    target: &str,
) -> Result<(i32, i32), String> {
    let normalized_object = object_name.replace('.', "-");
    let normalized_target = target.replace('.', "-");
    if target.eq_ignore_ascii_case(object_name)
        || normalized_target.eq_ignore_ascii_case(&normalized_object)
    {
        return Ok(self_entity);
    }
    if let Some(rec) = active.iter().find(|rec| {
        rec.name.eq_ignore_ascii_case(target)
            || rec
                .name
                .replace('.', "-")
                .eq_ignore_ascii_case(&normalized_target)
    }) {
        return Ok((rec.entity_type_id, rec.entity_id));
    }
    let is_qualified = target.contains('.') || (target.contains('-') && object_name.contains('.'));
    if !is_qualified {
        let bare_object = object_name.rsplit('.').next().unwrap_or(object_name);
        if target.eq_ignore_ascii_case(bare_object) {
            return Ok(self_entity);
        }
        let bare_matches: Vec<_> = active
            .iter()
            .filter(|rec| {
                let rec_bare = rec.name.rsplit('.').next().unwrap_or(&rec.name);
                rec_bare.eq_ignore_ascii_case(target)
            })
            .collect();
        if bare_matches.len() == 1 {
            return Ok((bare_matches[0].entity_type_id, bare_matches[0].entity_id));
        }
    }
    Err(format!(
        "'{object_name}' references '{target}', which is not an object of this model; refusing to store a dangling reference"
    ))
}

fn validate_object_name_for_sql(name: &str) -> Result<(), String> {
    if name.trim().is_empty() {
        return Err("object name must not be empty".to_string());
    }
    if name.chars().any(|ch| ch.is_control() || ch == '\u{FFFD}') {
        return Err(format!(
            "object name '{name}' contains control or invalid UTF-8 replacement characters; refusing SQL generation"
        ));
    }
    Ok(())
}

pub(crate) fn format_sql_varbinary_hex(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(2 + bytes.len() * 2);
    out.push_str("0x");
    for &byte in bytes {
        let _ = write!(&mut out, "{byte:02X}");
    }
    out
}

pub(crate) fn escape_sql_literal(value: &str) -> String {
    value.replace('\'', "''")
}

/// Identifies an object entity and active version in the UDM database.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct UdmObjectIdentity {
    pub entity_type_id: i32,
    pub entity_id: i32,
    pub version_id: i32,
    pub name: String,
}

/// Universal Data Model writer executing transactional mutations directly against UDM storage.
#[derive(Debug, Clone)]
pub struct UdmWriter {
    client: Arc<UdmClient>,
    model_id: i32,
}

impl UdmWriter {
    /// Creates a writer of the client's Design model (`UdmClient::design_model_id`; ModelId 1 by default).
    pub fn new(client: Arc<UdmClient>) -> Self {
        let model_id = client.design_model_id();
        Self { client, model_id }
    }

    /// Creates a writer targeting a specific model ID.
    pub fn with_model_id(client: Arc<UdmClient>, model_id: i32) -> Self {
        Self { client, model_id }
    }

    /// Creates a writer from a value client.
    pub fn from_client(client: UdmClient) -> Self {
        Self::new(Arc::new(client))
    }

    /// Returns the target model ID.
    pub fn model_id(&self) -> i32 {
        self.model_id
    }

    /// Returns a reference to the underlying database client.
    pub fn client(&self) -> &UdmClient {
        &self.client
    }

    /// Acquires an exclusive lock in the `State` table.
    pub fn acquire_lock(
        &self,
        state_name: &str,
        user_name: &str,
        guid: &str,
        machine_name: &str,
    ) -> Result<bool, String> {
        self.client
            .acquire_lock(state_name, user_name, guid, machine_name)
    }

    /// Releases an exclusive lock in the `State` table.
    pub fn release_lock(&self, state_name: &str, guid: &str) -> Result<bool, String> {
        self.client.release_lock(state_name, guid)
    }

    /// Checks if a state name is currently locked.
    pub fn is_locked(&self, state_name: &str) -> Result<bool, String> {
        self.client.get_lock(state_name).map(|opt| opt.is_some())
    }

    /// Retrieves lock details for a state name, if held.
    pub fn get_lock(&self, state_name: &str) -> Result<Option<StateLockRecord>, String> {
        self.client.get_lock(state_name)
    }

    /// Loads an existing active object with its raw stored UDM composition payloads,
    /// without hydrating synthetic compiler parts (`VARIABLE_IDS`, `PROPERTIES`, etc.)
    /// or transforming `VARIABLES` / `STRUCTURE` XML into compiler JSON/DSL.
    /// Refuses the load if any stored composition has an entity type outside engine coverage.
    pub fn load_raw_object(&self, object_name: &str) -> Result<Option<KbObject>, String> {
        let resolver = self.client.entity_type_resolver()?;
        let active = self.client.get_active_objects(self.model_id)?;
        let matches: Vec<&ModelEntityVersionRecord> = active
            .iter()
            .filter(|r| r.name.eq_ignore_ascii_case(object_name))
            .collect();
        if matches.is_empty() {
            return Ok(None);
        }
        if matches.len() > 1 {
            return Err(format!(
                "object '{object_name}' is ambiguous in Model {} ({} active records)",
                self.model_id,
                matches.len()
            ));
        }
        let record = matches[0];
        let kind = resolver.object_kind(record.entity_type_id).ok_or_else(|| {
            format!(
                "object '{}' has unsupported entity type {} in Model {}",
                record.name, record.entity_type_id, self.model_id
            )
        })?;

        let obj_id = ObjectId::new(record.name.replace('.', "-")).unwrap_or_else(|_| {
            ObjectId::new(format!(
                "obj-{}-{}",
                record.entity_type_id, record.entity_id
            ))
            .expect("valid fallback identity")
        });
        let mut object = KbObject::new(obj_id, record.name.clone(), kind);
        let compositions = self.client.get_compositions(
            record.entity_type_id,
            record.entity_id,
            record.entity_version_id,
        )?;

        for comp in compositions {
            let part_kind = resolver
                .part_kind(comp.component_entity_type_id)
                .ok_or_else(|| {
                    format!(
                        "object '{}' has composition of entity type {} outside engine coverage; refusing a partial save",
                        record.name, comp.component_entity_type_id
                    )
                })?;
            let ev = self
                .client
                .get_entity_version(
                    comp.component_entity_type_id,
                    comp.component_entity_id,
                    comp.component_entity_version_id,
                )?
                .ok_or_else(|| {
                    format!(
                        "part {} ({}, {}) of '{}' has no EntityVersion row; the composition is inconsistent",
                        part_kind.name(),
                        comp.component_entity_type_id,
                        comp.component_entity_id,
                        record.name
                    )
                })?;
            let source = if ev.data.is_empty() {
                String::new()
            } else {
                let decoded = decode_blob(&ev.data)?;
                if part_kind.is_tokenized_source() && decoded.contains("<TokenDataList") {
                    extract_token_data_list(&decoded)?
                } else {
                    decoded
                }
            };
            object.source_parts.push(SourcePart {
                name: part_kind.name().to_string(),
                source,
            });
        }

        let xr_records = self.client.get_cross_references(
            self.model_id,
            record.entity_type_id,
            record.entity_id,
        )?;
        for xr in xr_records {
            if let Some(target_rec) = active.iter().find(|r| {
                r.entity_type_id == xr.to_entity_type_id && r.entity_id == xr.to_entity_id
            }) {
                let target_id =
                    ObjectId::new(target_rec.name.replace('.', "-")).unwrap_or_else(|_| {
                        ObjectId::new(format!(
                            "obj-{}-{}",
                            target_rec.entity_type_id, target_rec.entity_id
                        ))
                        .expect("valid fallback identity")
                    });
                let ref_kind = match xr.reference_type {
                    1 => ReferenceKind::Calls,
                    2 => ReferenceKind::Uses,
                    3 => ReferenceKind::BasedOn,
                    4 => ReferenceKind::Contains,
                    _ => ReferenceKind::Uses,
                };
                let reference = Reference {
                    kind: ref_kind,
                    target: target_id,
                };
                if !object.references.contains(&reference) {
                    object.references.push(reference);
                }
            }
        }

        Ok(Some(object))
    }

    fn append_parts_and_refs_sql(
        &self,
        sql: &mut String,
        active: &[ModelEntityVersionRecord],
        object: &KbObject,
        identity: &UdmObjectIdentity,
        (allocated_part_ids, clear_existing_xrefs): (&[i32], bool),
    ) -> Result<(), String> {
        let resolver = self.client.entity_type_resolver()?;
        let (entity_type_id, entity_id, version_id) = (
            identity.entity_type_id,
            identity.entity_id,
            identity.version_id,
        );
        let parts = stored_parts(&resolver, object)?;
        if allocated_part_ids.len() != parts.len() {
            return Err(format!(
                "allocated_part_ids length ({}) does not match stored parts count ({}) for '{}'",
                allocated_part_ids.len(),
                parts.len(),
                object.name
            ));
        }

        let mut encoded_parts = Vec::with_capacity(parts.len());
        for (&(component_type_id, part_kind, part), &component_id) in
            parts.iter().zip(allocated_part_ids.iter())
        {
            let payload = if part_kind.is_tokenized_source() {
                build_token_data_list(&part.source)
            } else {
                part.source.clone()
            };
            let blob = encode_blob(&payload, true)?;
            let hex_blob = format_sql_varbinary_hex(&blob);
            encoded_parts.push((component_type_id, component_id, hex_blob));
        }

        let mut resolved_refs = Vec::with_capacity(object.references.len());
        let mut seen_refs = BTreeSet::new();
        for r in &object.references {
            let (to_type_id, to_id) = reference_target(
                active,
                (entity_type_id, entity_id),
                &object.name,
                r.target.as_str(),
            )?;
            let ref_type = match r.kind {
                ReferenceKind::Calls => 1,
                ReferenceKind::Uses => 2,
                ReferenceKind::BasedOn => 3,
                ReferenceKind::Contains => 4,
            };
            if seen_refs.insert((to_type_id, to_id, ref_type)) {
                resolved_refs.push((to_type_id, to_id, ref_type));
            }
        }

        for (component_type_id, component_id, hex_blob) in &encoded_parts {
            let _ = write!(
                sql,
                " INSERT INTO EntityVersion (EntityTypeId, EntityId, EntityVersionId, EntityVersionData) VALUES ({component_type_id}, {component_id}, {version_id}, {hex_blob}); \
INSERT INTO EntityVersionComposition (CompoundEntityTypeId, CompoundEntityId, CompoundEntityVersionId, ComponentEntityTypeId, ComponentEntityId, ComponentEntityVersionId) VALUES ({entity_type_id}, {entity_id}, {version_id}, {component_type_id}, {component_id}, {version_id});"
            );
        }

        let model_id = self.model_id;
        if clear_existing_xrefs {
            let _ = write!(
                sql,
                " DELETE FROM ModelCrossReference WHERE ModelId = {model_id} AND FromEntityTypeId = {entity_type_id} AND FromEntityId = {entity_id};"
            );
        }

        for &(to_type_id, to_id, ref_type) in &resolved_refs {
            let _ = write!(
                sql,
                " INSERT INTO ModelCrossReference (ModelId, FromEntityTypeId, FromEntityId, ToEntityTypeId, ToEntityId, ReferenceType) VALUES ({model_id}, {entity_type_id}, {entity_id}, {to_type_id}, {to_id}, {ref_type});"
            );
        }

        sql.push_str(" COMMIT TRANSACTION;");
        Ok(())
    }

    /// Plans and builds a deterministic, fail-closed transactional SQL Server batch for creating
    /// a new object in the active model.
    pub fn plan_create_object_sql(
        &self,
        object: &KbObject,
        allocated_entity_id: i32,
        allocated_part_ids: &[i32],
    ) -> Result<(UdmObjectIdentity, String), String> {
        validate_object_name_for_sql(&object.name)?;
        if allocated_entity_id <= 0 {
            return Err(format!(
                "allocated entity ID {allocated_entity_id} for '{}' must be positive (> 0)",
                object.name
            ));
        }

        let active = self.client.get_active_objects(self.model_id)?;
        if active
            .iter()
            .any(|r| r.name.eq_ignore_ascii_case(&object.name))
        {
            return Err(format!(
                "Object '{}' already exists in Model {}",
                object.name, self.model_id
            ));
        }

        let mut seen_ids = BTreeSet::new();
        seen_ids.insert(allocated_entity_id);
        for &part_id in allocated_part_ids {
            if part_id <= 0 {
                return Err(format!(
                    "allocated part entity ID {part_id} for '{}' must be positive (> 0)",
                    object.name
                ));
            }
            if !seen_ids.insert(part_id) {
                return Err(format!(
                    "duplicate allocated entity ID {part_id} for '{}'; entity IDs must be distinct",
                    object.name
                ));
            }
        }

        let resolver = self.client.entity_type_resolver()?;
        let entity_type_id = resolver.object_type_id(object.kind)?;
        let version_id = 1;
        let identity = UdmObjectIdentity {
            entity_type_id,
            entity_id: allocated_entity_id,
            version_id,
            name: object.name.clone(),
        };
        let model_id = self.model_id;
        let escaped_name = escape_sql_literal(&object.name);
        let escaped_guid = escape_sql_literal(object.id.as_str());
        let mut sql = String::new();
        let _ = write!(
            &mut sql,
            "SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000; SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRANSACTION; \
IF EXISTS (SELECT 1 FROM ModelEntityVersion WITH (UPDLOCK, HOLDLOCK) WHERE ModelId = {model_id} AND ModelEntityVersionName = N'{escaped_name}') \
THROW 50002, 'Object already exists in ModelEntityVersion', 1; \
IF NOT EXISTS (SELECT 1 FROM Entity WITH (UPDLOCK, HOLDLOCK) WHERE EntityTypeId = {entity_type_id} AND EntityId = {allocated_entity_id}) \
INSERT INTO Entity (EntityTypeId, EntityId, EntityGuid) VALUES ({entity_type_id}, {allocated_entity_id}, '{escaped_guid}'); \
INSERT INTO ModelEntityVersion (ModelId, EntityTypeId, EntityId, EntityVersionId, ModelEntityVersionName) VALUES ({model_id}, {entity_type_id}, {allocated_entity_id}, {version_id}, N'{escaped_name}');"
        );
        self.append_parts_and_refs_sql(
            &mut sql,
            &active,
            object,
            &identity,
            (allocated_part_ids, false),
        )?;

        Ok((identity, sql))
    }

    /// Creates a new object in the UDM database. Fails if an object with the same name exists.
    pub fn create_object(&self, object: &KbObject) -> Result<UdmObjectIdentity, String> {
        let resolver = self.client.entity_type_resolver()?;
        let entity_type_id = resolver.object_type_id(object.kind)?;
        let parts = stored_parts(&resolver, object)?;
        let active = self.client.get_active_objects(self.model_id)?;
        if active
            .iter()
            .any(|r| r.name.eq_ignore_ascii_case(&object.name))
        {
            return Err(format!(
                "Object '{}' already exists in Model {}",
                object.name, self.model_id
            ));
        }

        let entity_id = self.client.allocate_entity_id()?;
        let mut allocated_part_ids = Vec::with_capacity(parts.len());
        for _ in 0..parts.len() {
            allocated_part_ids.push(self.client.allocate_entity_id()?);
        }

        if self.client.is_live() && self.client.live_writes_enabled() {
            let (_, sql) = self.plan_create_object_sql(object, entity_id, &allocated_part_ids)?;
            self.client.execute_live_sql(&sql)?;
        }

        self.client.begin_transaction()?;

        let result = (|| {
            let version_id = 1;

            if self.client.is_live() && self.client.live_writes_enabled() {
                let guid = object.id.as_str();
                self.client
                    .insert_entity_guid(entity_type_id, entity_id, guid)?;
            }

            // 1. Insert ModelEntityVersion
            self.client
                .insert_model_entity_version(ModelEntityVersionRecord {
                    model_id: self.model_id,
                    entity_type_id,
                    entity_id,
                    entity_version_id: version_id,
                    name: object.name.clone(),
                })?;

            // 2. Insert component parts
            for (&(component_type_id, part_kind, part), &component_id) in
                parts.iter().zip(allocated_part_ids.iter())
            {
                let payload = if part_kind.is_tokenized_source() {
                    build_token_data_list(&part.source)
                } else {
                    part.source.clone()
                };

                let blob = encode_blob(&payload, true)?;

                self.client.insert_entity_version(EntityVersionRecord {
                    entity_type_id: component_type_id,
                    entity_id: component_id,
                    entity_version_id: version_id,
                    data: blob,
                    properties: None,
                })?;

                self.client
                    .insert_composition(EntityVersionCompositionRecord {
                        compound_entity_type_id: entity_type_id,
                        compound_entity_id: entity_id,
                        compound_entity_version_id: version_id,
                        component_entity_type_id: component_type_id,
                        component_entity_id: component_id,
                        component_entity_version_id: version_id,
                    })?;
            }

            // 3. Insert cross-references
            for r in &object.references {
                let (to_type_id, to_id) = reference_target(
                    &active,
                    (entity_type_id, entity_id),
                    &object.name,
                    r.target.as_str(),
                )?;

                let ref_type = match r.kind {
                    ReferenceKind::Calls => 1,
                    ReferenceKind::Uses => 2,
                    ReferenceKind::BasedOn => 3,
                    ReferenceKind::Contains => 4,
                };

                self.client
                    .insert_cross_reference(ModelCrossReferenceRecord {
                        model_id: self.model_id,
                        from_entity_type_id: entity_type_id,
                        from_entity_id: entity_id,
                        to_entity_type_id: to_type_id,
                        to_entity_id: to_id,
                        reference_type: ref_type,
                    })?;
            }

            Ok(UdmObjectIdentity {
                entity_type_id,
                entity_id,
                version_id,
                name: object.name.clone(),
            })
        })();

        match result {
            Ok(identity) => {
                self.client.commit_transaction()?;
                Ok(identity)
            }
            Err(err) => {
                let _ = self.client.rollback_transaction();
                Err(err)
            }
        }
    }

    /// Saves an object to the UDM database, creating it if it does not exist,
    /// or incrementing its version and replacing its component parts if it exists.
    pub fn save_object(&self, object: &KbObject) -> Result<UdmObjectIdentity, String> {
        let active = self.client.get_active_objects(self.model_id)?;
        let existing = active
            .iter()
            .find(|r| r.name.eq_ignore_ascii_case(&object.name))
            .cloned();

        let Some(existing_record) = existing else {
            let identity = self.create_object(object)?;
            self.verify_saved_object_readback(&identity, object)?;
            return Ok(identity);
        };

        let resolver = self.client.entity_type_resolver()?;
        if resolver.object_kind(existing_record.entity_type_id) != Some(object.kind) {
            return Err(format!(
                "'{}' exists as a different object kind; refusing to save it as {:?}",
                object.name, object.kind
            ));
        }
        let parts = stored_parts(&resolver, object)?;

        let mut allocated_part_ids = Vec::with_capacity(parts.len());
        for _ in 0..parts.len() {
            allocated_part_ids.push(self.client.allocate_entity_id()?);
        }

        if self.client.is_live() && self.client.live_writes_enabled() {
            let (_, sql) = self.plan_save_existing_object_sql(object, &allocated_part_ids)?;
            self.client.execute_live_sql(&sql)?;
        }

        self.client.begin_transaction()?;

        let result = (|| {
            let entity_type_id = existing_record.entity_type_id;
            let entity_id = existing_record.entity_id;
            let new_version_id = existing_record.entity_version_id + 1;

            // 1. Update ModelEntityVersion
            self.client.update_model_entity_version(
                self.model_id,
                entity_type_id,
                entity_id,
                new_version_id,
                Some(&object.name),
            )?;

            // 2. Insert new version component parts
            for (&(component_type_id, part_kind, part), &component_id) in
                parts.iter().zip(allocated_part_ids.iter())
            {
                let payload = if part_kind.is_tokenized_source() {
                    build_token_data_list(&part.source)
                } else {
                    part.source.clone()
                };

                let blob = encode_blob(&payload, true)?;

                self.client.insert_entity_version(EntityVersionRecord {
                    entity_type_id: component_type_id,
                    entity_id: component_id,
                    entity_version_id: new_version_id,
                    data: blob,
                    properties: None,
                })?;

                self.client
                    .insert_composition(EntityVersionCompositionRecord {
                        compound_entity_type_id: entity_type_id,
                        compound_entity_id: entity_id,
                        compound_entity_version_id: new_version_id,
                        component_entity_type_id: component_type_id,
                        component_entity_id: component_id,
                        component_entity_version_id: new_version_id,
                    })?;
            }

            // 3. Refresh cross-references
            self.client
                .clear_cross_references(self.model_id, entity_type_id, entity_id)?;

            for r in &object.references {
                let (to_type_id, to_id) = reference_target(
                    &active,
                    (entity_type_id, entity_id),
                    &object.name,
                    r.target.as_str(),
                )?;

                let ref_type = match r.kind {
                    ReferenceKind::Calls => 1,
                    ReferenceKind::Uses => 2,
                    ReferenceKind::BasedOn => 3,
                    ReferenceKind::Contains => 4,
                };

                self.client
                    .insert_cross_reference(ModelCrossReferenceRecord {
                        model_id: self.model_id,
                        from_entity_type_id: entity_type_id,
                        from_entity_id: entity_id,
                        to_entity_type_id: to_type_id,
                        to_entity_id: to_id,
                        reference_type: ref_type,
                    })?;
            }

            Ok(UdmObjectIdentity {
                entity_type_id,
                entity_id,
                version_id: new_version_id,
                name: object.name.clone(),
            })
        })();

        match result {
            Ok(identity) => {
                self.client.commit_transaction()?;
                self.verify_saved_object_readback(&identity, object)?;
                Ok(identity)
            }
            Err(err) => {
                let _ = self.client.rollback_transaction();
                Err(err)
            }
        }
    }

    /// Saves an object and verifies read-back consistency through `UdmReader`.
    pub fn save_and_verify(&self, object: &KbObject) -> Result<UdmObjectIdentity, String> {
        let identity = self.save_object(object)?;
        self.verify_saved_object_readback(&identity, object)?;
        Ok(identity)
    }

    /// Plans and builds a deterministic, fail-closed transactional SQL Server batch for saving an
    /// existing object in the active model.
    pub fn plan_save_existing_object_sql(
        &self,
        object: &KbObject,
        allocated_part_ids: &[i32],
    ) -> Result<(UdmObjectIdentity, String), String> {
        validate_object_name_for_sql(&object.name)?;

        let active = self.client.get_active_objects(self.model_id)?;
        let existing_record = active
            .iter()
            .find(|r| r.name.eq_ignore_ascii_case(&object.name))
            .ok_or_else(|| {
                format!(
                    "Object '{}' does not exist in Model {}; refusing to plan existing-object SQL save",
                    object.name, self.model_id
                )
            })?;

        let resolver = self.client.entity_type_resolver()?;
        if resolver.object_kind(existing_record.entity_type_id) != Some(object.kind) {
            return Err(format!(
                "'{}' exists as a different object kind; refusing to save it as {:?}",
                object.name, object.kind
            ));
        }

        let mut seen_part_ids = BTreeSet::new();
        for &part_id in allocated_part_ids {
            if part_id <= 0 {
                return Err(format!(
                    "allocated part entity ID {part_id} for '{}' must be positive (> 0)",
                    object.name
                ));
            }
            if !seen_part_ids.insert(part_id) {
                return Err(format!(
                    "duplicate allocated part entity ID {part_id} for '{}'; part entity IDs must be distinct",
                    object.name
                ));
            }
        }

        let entity_type_id = existing_record.entity_type_id;
        let entity_id = existing_record.entity_id;
        let expected_old_version = existing_record.entity_version_id;
        if expected_old_version <= 0 {
            return Err(format!(
                "invalid current EntityVersionId {} for '{}'",
                expected_old_version, object.name
            ));
        }
        let new_version_id = expected_old_version
            .checked_add(1)
            .ok_or_else(|| format!("EntityVersionId overflow for '{}'", object.name))?;

        let identity = UdmObjectIdentity {
            entity_type_id,
            entity_id,
            version_id: new_version_id,
            name: object.name.clone(),
        };
        let model_id = self.model_id;
        let escaped_name = escape_sql_literal(&object.name);
        let mut sql = String::new();
        let _ = write!(
            &mut sql,
            "SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000; SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRANSACTION; \
IF NOT EXISTS (SELECT 1 FROM ModelEntityVersion WITH (UPDLOCK, HOLDLOCK) WHERE ModelId = {model_id} AND EntityTypeId = {entity_type_id} AND EntityId = {entity_id} AND EntityVersionId = {expected_old_version}) \
THROW 50001, 'Optimistic concurrency conflict on ModelEntityVersion', 1; \
UPDATE ModelEntityVersion SET EntityVersionId = {new_version_id}, ModelEntityVersionName = N'{escaped_name}' WHERE ModelId = {model_id} AND EntityTypeId = {entity_type_id} AND EntityId = {entity_id} AND EntityVersionId = {expected_old_version};"
        );
        self.append_parts_and_refs_sql(
            &mut sql,
            &active,
            object,
            &identity,
            (allocated_part_ids, true),
        )?;

        Ok((identity, sql))
    }

    /// Verifies that reading a saved object back from the active model via `UdmReader`
    /// matches `expected_identity` and `expected_object` in kind, version, parts, and references.
    pub fn verify_saved_object_readback(
        &self,
        expected_identity: &UdmObjectIdentity,
        expected_object: &KbObject,
    ) -> Result<(), String> {
        if !expected_identity
            .name
            .eq_ignore_ascii_case(&expected_object.name)
        {
            return Err(format!(
                "read-back verification failed: expected identity name '{}' does not match object name '{}'",
                expected_identity.name, expected_object.name
            ));
        }

        let resolver = self.client.entity_type_resolver()?;
        let expected_parts = stored_parts(&resolver, expected_object)?;

        let active_objects = self.client.get_active_objects(self.model_id)?;
        let active_record = active_objects
            .iter()
            .find(|r| r.name.eq_ignore_ascii_case(&expected_object.name))
            .ok_or_else(|| {
                format!(
                    "read-back verification failed for '{}': object not found in active ModelEntityVersion of Model {}",
                    expected_object.name, self.model_id
                )
            })?;

        if active_record.entity_type_id != expected_identity.entity_type_id
            || active_record.entity_id != expected_identity.entity_id
            || active_record.entity_version_id != expected_identity.version_id
        {
            return Err(format!(
                "read-back verification failed for '{}': active ModelEntityVersion (type={}, id={}, version={}) does not match expected identity (type={}, id={}, version={})",
                expected_object.name,
                active_record.entity_type_id,
                active_record.entity_id,
                active_record.entity_version_id,
                expected_identity.entity_type_id,
                expected_identity.entity_id,
                expected_identity.version_id
            ));
        }

        let compositions = self.client.get_compositions(
            expected_identity.entity_type_id,
            expected_identity.entity_id,
            expected_identity.version_id,
        )?;
        if compositions.len() != expected_parts.len() {
            return Err(format!(
                "read-back verification failed for '{}': stored composition count ({}) does not match expected part count ({})",
                expected_object.name,
                compositions.len(),
                expected_parts.len()
            ));
        }

        let reader = if self.model_id == self.client.design_model_id() {
            UdmReader::new(self.client.clone())
        } else {
            UdmReader::with_model_id(self.client.clone(), self.model_id)
        };
        let read_back = reader
            .read_object_by_name(&expected_object.name)?
            .ok_or_else(|| {
                format!(
                    "read-back verification failed for '{}': UdmReader::read_object_by_name returned None",
                    expected_object.name
                )
            })?;

        if read_back.kind != expected_object.kind {
            return Err(format!(
                "read-back verification failed for '{}': read-back kind {:?} does not match expected {:?}",
                expected_object.name, read_back.kind, expected_object.kind
            ));
        }

        if !read_back.name.eq_ignore_ascii_case(&expected_object.name) {
            return Err(format!(
                "read-back verification failed for '{}': read-back name '{}' does not match expected '{}'",
                expected_object.name, read_back.name, expected_object.name
            ));
        }

        let read_back_stored_parts: Vec<&SourcePart> = read_back
            .source_parts
            .iter()
            .filter(|part| !matches!(UdmPartKind::from_name(&part.name), UdmPartKind::Unknown(_)))
            .collect();

        if read_back_stored_parts.len() != expected_parts.len() {
            return Err(format!(
                "read-back verification failed for '{}': read-back part count ({}) does not match expected part count ({})",
                expected_object.name,
                read_back_stored_parts.len(),
                expected_parts.len()
            ));
        }

        for (idx, ((expected_type_id, expected_kind, expected_part), actual_part)) in expected_parts
            .iter()
            .zip(read_back_stored_parts.iter())
            .enumerate()
        {
            let actual_kind = UdmPartKind::from_name(&actual_part.name);
            if actual_kind != *expected_kind
                || !actual_part.name.eq_ignore_ascii_case(expected_kind.name())
            {
                return Err(format!(
                    "read-back verification failed for '{}' at part #{idx}: read-back part '{}' does not match expected '{}'",
                    expected_object.name,
                    actual_part.name,
                    expected_kind.name()
                ));
            }

            let comp = &compositions[idx];
            if comp.component_entity_type_id != *expected_type_id {
                return Err(format!(
                    "read-back verification failed for '{}' part '{}': composition entity type {} does not match expected {}",
                    expected_object.name,
                    expected_kind.name(),
                    comp.component_entity_type_id,
                    expected_type_id
                ));
            }

            let ev = self
                .client
                .get_entity_version(
                    comp.component_entity_type_id,
                    comp.component_entity_id,
                    comp.component_entity_version_id,
                )?
                .ok_or_else(|| {
                    format!(
                        "read-back verification failed for '{}' part '{}': missing EntityVersion row ({}, {}, {})",
                        expected_object.name,
                        expected_kind.name(),
                        comp.component_entity_type_id,
                        comp.component_entity_id,
                        comp.component_entity_version_id
                    )
                })?;

            let decoded_raw = if ev.data.is_empty() {
                String::new()
            } else {
                let raw = decode_blob(&ev.data)?;
                if expected_kind.is_tokenized_source() && raw.contains("<TokenDataList") {
                    extract_token_data_list(&raw)?
                } else {
                    raw
                }
            };

            if decoded_raw != expected_part.source {
                return Err(format!(
                    "read-back verification failed for '{}' part '{}': decoded stored payload does not match expected content",
                    expected_object.name,
                    expected_kind.name()
                ));
            }

            let reader_transforms_part = ((matches!(
                expected_object.kind,
                crate::catalog::ObjectKind::Procedure | crate::catalog::ObjectKind::DataProvider
            ) && *expected_kind == UdmPartKind::Variables)
                || (matches!(
                    expected_object.kind,
                    crate::catalog::ObjectKind::Transaction
                        | crate::catalog::ObjectKind::StructuredDataType
                ) && *expected_kind == UdmPartKind::Structure))
                && expected_part.source.trim_start().starts_with('<');

            if !reader_transforms_part && actual_part.source != expected_part.source {
                return Err(format!(
                    "read-back verification failed for '{}' part '{}': reader part content does not match expected content",
                    expected_object.name,
                    expected_kind.name()
                ));
            }
        }

        let mut expected_refs = Vec::with_capacity(expected_object.references.len());
        for r in &expected_object.references {
            if !expected_refs.contains(r) {
                expected_refs.push(r.clone());
            }
        }
        if read_back.references != expected_refs {
            return Err(format!(
                "read-back verification failed for '{}': read-back references {:?} do not match expected {:?}",
                expected_object.name, read_back.references, expected_refs
            ));
        }

        Ok(())
    }

    /// Plans a deterministic, fail-closed transactional SQL Server batch for deleting an object
    /// from the active model.
    pub fn plan_delete_object_sql(
        &self,
        record: &ModelEntityVersionRecord,
    ) -> Result<String, String> {
        let model_id = self.model_id;
        let entity_type_id = record.entity_type_id;
        let entity_id = record.entity_id;
        let version_id = record.entity_version_id;
        Ok(format!(
            "SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000; SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRANSACTION; \
DELETE FROM ModelCrossReference WHERE ModelId = {model_id} AND FromEntityTypeId = {entity_type_id} AND FromEntityId = {entity_id}; \
DELETE FROM EntityVersionComposition WHERE CompoundEntityTypeId = {entity_type_id} AND CompoundEntityId = {entity_id} AND CompoundEntityVersionId = {version_id}; \
DELETE FROM ModelEntityVersion WHERE ModelId = {model_id} AND EntityTypeId = {entity_type_id} AND EntityId = {entity_id}; \
COMMIT TRANSACTION;"
        ))
    }

    /// Deletes an object by name from the active model in the UDM database.
    pub fn delete_object(&self, object_name: &str) -> Result<bool, String> {
        let active = self.client.get_active_objects(self.model_id)?;
        let existing = active
            .iter()
            .find(|r| r.name.eq_ignore_ascii_case(object_name))
            .cloned();

        let Some(record) = existing else {
            return Ok(false);
        };

        if self.client.is_live() && self.client.live_writes_enabled() {
            let sql = self.plan_delete_object_sql(&record)?;
            self.client.execute_live_sql(&sql)?;
        }

        self.client.begin_transaction()?;

        let result = (|| {
            self.client
                .delete_object(self.model_id, record.entity_type_id, record.entity_id)?;
            self.client.clear_compositions(
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id,
            )?;
            self.client.clear_cross_references(
                self.model_id,
                record.entity_type_id,
                record.entity_id,
            )?;
            let _ = self.client.release_lock(object_name, "");
            Ok(true)
        })();

        match result {
            Ok(deleted) => {
                self.client.commit_transaction()?;
                Ok(deleted)
            }
            Err(err) => {
                let _ = self.client.rollback_transaction();
                Err(err)
            }
        }
    }

    /// Saves an object with an exclusive lock lease, automatically releasing the lock upon completion.
    pub fn save_with_lock(
        &self,
        object: &KbObject,
        user_name: &str,
        guid: &str,
        machine_name: &str,
    ) -> Result<UdmObjectIdentity, String> {
        let acquired = self.acquire_lock(&object.name, user_name, guid, machine_name)?;
        if !acquired {
            return Err(format!(
                "Lock acquisition failed for object '{}': already locked by another agent",
                object.name
            ));
        }

        let save_res = self.save_object(object);
        let _ = self.release_lock(&object.name, guid);
        save_res
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::catalog::{ObjectId, ObjectKind, SourcePart};
    use crate::udm::reader::UdmReader;

    #[test]
    fn writer_create_and_save_roundtrip() {
        let client = Arc::new(UdmClient::new_in_memory());
        let writer = UdmWriter::new(client.clone());
        let reader = UdmReader::new(client);

        // 1. Create a Procedure
        let mut proc = KbObject::new(
            ObjectId::new("pcreateinvoice").unwrap(),
            "PCreateInvoice",
            ObjectKind::Procedure,
        );
        proc.source_parts.push(SourcePart {
            name: "SOURCE".to_string(),
            source: "For Each\n    Msg('Invoice Created')\nEndFor".to_string(),
        });
        proc.source_parts.push(SourcePart {
            name: "RULES".to_string(),
            source: "Parm(in:&CustomerId);".to_string(),
        });

        let id1 = writer.create_object(&proc).expect("create object");
        assert_eq!(id1.version_id, 1);
        assert_eq!(id1.name, "PCreateInvoice");

        // Verify with reader
        let read1 = reader
            .read_object_by_name("PCreateInvoice")
            .unwrap()
            .expect("read object");
        assert_eq!(read1.source_parts.len(), 2);
        assert_eq!(
            read1
                .source_parts
                .iter()
                .find(|p| p.name == "SOURCE")
                .unwrap()
                .source,
            "For Each\n    Msg('Invoice Created')\nEndFor"
        );

        // 2. Save an updated version
        proc.source_parts[0].source = "For Each\n    Msg('Invoice Created V2')\nEndFor".to_string();

        let id2 = writer.save_object(&proc).expect("save object");
        assert_eq!(id2.version_id, 2);

        // Verify version 2 read
        let read2 = reader
            .read_object_by_name("PCreateInvoice")
            .unwrap()
            .expect("read object v2");
        assert_eq!(
            read2
                .source_parts
                .iter()
                .find(|p| p.name == "SOURCE")
                .unwrap()
                .source,
            "For Each\n    Msg('Invoice Created V2')\nEndFor"
        );
    }

    #[test]
    fn writer_lock_management_and_save_with_lock() {
        let client = Arc::new(UdmClient::new_in_memory());
        let writer = UdmWriter::new(client);

        let proc = KbObject::new(
            ObjectId::new("plockedproc").unwrap(),
            "PLockedProc",
            ObjectKind::Procedure,
        );

        // Save with lock
        let identity = writer
            .save_with_lock(&proc, "agent_alice", "guid-999", "DEV-BOX")
            .expect("save with lock");
        assert_eq!(identity.name, "PLockedProc");

        // Verify lock was released automatically
        assert!(!writer.is_locked("PLockedProc").unwrap());
    }

    #[test]
    fn writer_delete_object() {
        let client = Arc::new(UdmClient::new_in_memory());
        let writer = UdmWriter::new(client.clone());
        let reader = UdmReader::new(client);

        let proc = KbObject::new(
            ObjectId::new("ptodelete").unwrap(),
            "PToDelete",
            ObjectKind::Procedure,
        );
        writer.create_object(&proc).expect("create object");
        assert!(reader.read_object_by_name("PToDelete").unwrap().is_some());

        let deleted = writer.delete_object("PToDelete").expect("delete object");
        assert!(deleted);
        assert!(reader.read_object_by_name("PToDelete").unwrap().is_none());
    }
}
