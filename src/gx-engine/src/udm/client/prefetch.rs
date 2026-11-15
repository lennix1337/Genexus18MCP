//! Set-wise prefetch of a model parts, and the coverage that lets later reads answer from memory.

use super::live_rows::{parse_live_int, parse_live_rows, part_payload_columns};
use super::*;

/// The identity a part row leads with: its entity type, entity and version.
fn part_key(fields: &[&str]) -> Result<(i32, i32, i32), String> {
    Ok((
        parse_live_int(fields[0], "EntityTypeId")?,
        parse_live_int(fields[1], "EntityId")?,
        parse_live_int(fields[2], "EntityVersionId")?,
    ))
}

impl UdmClient {
    /// Reads, with three set-based queries, the compositions of every object of `model_id`
    /// and the `EntityVersion` parts those objects and compositions reach, then marks those
    /// keys covered. Hydrating a Knowledge Base otherwise issued one `sqlcmd` process per
    /// object part. An in-memory client has no database to read, so this does nothing.
    pub fn prefetch_model_parts(&self, model_id: i32) -> Result<(), String> {
        if self.connection_info.is_none() {
            return Ok(());
        }
        let objects: Vec<(i32, i32, i32)> = self
            .get_active_objects(model_id)?
            .iter()
            .map(|m| (m.entity_type_id, m.entity_id, m.entity_version_id))
            .collect();

        let compositions_sql = format!(
            "SET NOCOUNT ON; SELECT c.CompoundEntityTypeId, c.CompoundEntityId, c.CompoundEntityVersionId, c.ComponentEntityTypeId, c.ComponentEntityId, c.ComponentEntityVersionId FROM EntityVersionComposition c JOIN ModelEntityVersion m ON m.EntityTypeId = c.CompoundEntityTypeId AND m.EntityId = c.CompoundEntityId AND m.EntityVersionId = c.CompoundEntityVersionId WHERE m.ModelId = {model_id}"
        );
        let raw = self.execute_live_sql(&compositions_sql)?;
        let mut compositions = Vec::new();
        for row in parse_live_rows(&raw, 6)? {
            compositions.push(EntityVersionCompositionRecord {
                compound_entity_type_id: parse_live_int(row[0], "CompoundEntityTypeId")?,
                compound_entity_id: parse_live_int(row[1], "CompoundEntityId")?,
                compound_entity_version_id: parse_live_int(row[2], "CompoundEntityVersionId")?,
                component_entity_type_id: parse_live_int(row[3], "ComponentEntityTypeId")?,
                component_entity_id: parse_live_int(row[4], "ComponentEntityId")?,
                component_entity_version_id: parse_live_int(row[5], "ComponentEntityVersionId")?,
            });
        }
        let components: Vec<(i32, i32, i32)> = compositions
            .iter()
            .map(|c| {
                (
                    c.component_entity_type_id,
                    c.component_entity_id,
                    c.component_entity_version_id,
                )
            })
            .collect();

        // The parts are the bulk of a catalog read: read in shards on parallel sqlcmd sessions, each
        // decoded on its own thread, then put back in key order.
        // Only the types the engine decodes are read: an unserved row (a File's `WikiBlob`, a library
        // Module's `ModuleContent`, each up to tens of MB) is left out and stays uncovered, so a caller that
        // asks for one still reads it live.
        let served = self.entity_type_resolver()?.served_type_ids();
        // Shard 0 reads on the session this client's reads share; the others on their own.
        let read_shard = |shard: usize| -> Result<Vec<EntityVersionRecord>, String> {
            self.read_part_records(
                shard != 0,
                3,
                |binary| Self::model_parts_sql(model_id, &served, shard, binary),
                part_key,
            )
        };
        let mut parts = std::thread::scope(|scope| -> Result<Vec<EntityVersionRecord>, String> {
            let shards: Vec<_> = (0..PART_READ_SHARDS)
                .map(|shard| scope.spawn(move || read_shard(shard)))
                .collect();
            let mut parts = Vec::new();
            for shard in shards {
                parts.extend(
                    shard
                        .join()
                        .map_err(|_| "a part reader thread panicked".to_string())??,
                );
            }
            Ok(parts)
        })?;
        parts.sort_by_key(|part| (part.entity_type_id, part.entity_id, part.entity_version_id));

        {
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let mut known_compositions: std::collections::HashSet<(i32, i32, i32, i32, i32, i32)> =
                store
                    .entity_version_compositions
                    .iter()
                    .map(|c| {
                        (
                            c.compound_entity_type_id,
                            c.compound_entity_id,
                            c.compound_entity_version_id,
                            c.component_entity_type_id,
                            c.component_entity_id,
                            c.component_entity_version_id,
                        )
                    })
                    .collect();
            for rec in compositions {
                let key = (
                    rec.compound_entity_type_id,
                    rec.compound_entity_id,
                    rec.compound_entity_version_id,
                    rec.component_entity_type_id,
                    rec.component_entity_id,
                    rec.component_entity_version_id,
                );
                if known_compositions.insert(key) {
                    store.entity_version_compositions.push(rec);
                }
            }
            let mut known_parts: std::collections::HashSet<(i32, i32, i32)> = store
                .entity_versions
                .iter()
                .map(|ev| (ev.entity_type_id, ev.entity_id, ev.entity_version_id))
                .collect();
            for rec in parts {
                let key = (rec.entity_type_id, rec.entity_id, rec.entity_version_id);
                if known_parts.insert(key) {
                    store.entity_versions.push(rec);
                }
            }
        }

        let mut bulk = self
            .bulk
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        bulk.compositions.extend(objects.iter().copied());
        bulk.entity_versions
            .extend(Self::served_keys(objects.iter().copied(), &served));
        bulk.entity_versions
            .extend(Self::served_keys(components, &served));
        Ok(())
    }

    /// The `EntityVersion` read of one shard of `model_id`'s objects and their components, restricted to the
    /// `served` entity types. With no served type the read selects nothing. `binary` selects the payloads as
    /// bytes instead of hex text (see `part_payload_columns`).
    pub(super) fn model_parts_sql(
        model_id: i32,
        served: &[i32],
        shard: usize,
        binary: bool,
    ) -> String {
        let types = if served.is_empty() {
            "1 = 0".to_string()
        } else {
            format!(
                "e.EntityTypeId IN ({})",
                served
                    .iter()
                    .map(i32::to_string)
                    .collect::<Vec<_>>()
                    .join(", ")
            )
        };
        let payloads = part_payload_columns("e.", binary);
        format!(
            "SET NOCOUNT ON; SELECT e.EntityTypeId, e.EntityId, e.EntityVersionId, {payloads} FROM EntityVersion e WHERE (EXISTS (SELECT 1 FROM ModelEntityVersion m WHERE m.ModelId = {model_id} AND m.EntityTypeId = e.EntityTypeId AND m.EntityId = e.EntityId AND m.EntityVersionId = e.EntityVersionId) OR EXISTS (SELECT 1 FROM EntityVersionComposition c JOIN ModelEntityVersion m ON m.EntityTypeId = c.CompoundEntityTypeId AND m.EntityId = c.CompoundEntityId AND m.EntityVersionId = c.CompoundEntityVersionId WHERE m.ModelId = {model_id} AND c.ComponentEntityTypeId = e.EntityTypeId AND c.ComponentEntityId = e.EntityId AND c.ComponentEntityVersionId = e.EntityVersionId)) AND {types} AND e.EntityId % {PART_READ_SHARDS} = {shard}"
        )
    }

    /// The keys of `keys` whose entity type is in `served` (sorted), in their order.
    pub(super) fn served_keys(
        keys: impl IntoIterator<Item = (i32, i32, i32)>,
        served: &[i32],
    ) -> Vec<(i32, i32, i32)> {
        keys.into_iter()
            .filter(|(entity_type, _, _)| served.binary_search(entity_type).is_ok())
            .collect()
    }

    /// The distinct keys of `keys`, sorted, as the set-based prefetch takes them.
    fn distinct_keys(keys: &[(i32, i32, i32)]) -> Vec<(i32, i32, i32)> {
        keys.iter()
            .copied()
            .collect::<std::collections::BTreeSet<_>>()
            .into_iter()
            .collect()
    }

    /// The `(t, i, v)` rows of a `VALUES` list for one chunk of keys. Every value is an `i32`, so no text
    /// is injected.
    fn values_list(chunk: &[(i32, i32, i32)]) -> String {
        chunk
            .iter()
            .map(|(t, i, v)| format!("({t}, {i}, {v})"))
            .collect::<Vec<_>>()
            .join(", ")
    }

    /// Reads, with set-based queries, the compositions of the given compounds and the `EntityVersion` rows of
    /// the given keys and of their components, then marks exactly those keys covered: a key with no row is
    /// covered as absent. The cost follows the keys passed, not the model. A key that is not passed still reads
    /// live, so an incomplete call is slower, never wrong. An in-memory client has no database and does nothing.
    pub fn prefetch_keys(
        &self,
        compounds: &[(i32, i32, i32)],
        parts: &[(i32, i32, i32)],
    ) -> Result<(), String> {
        if self.connection_info.is_none() {
            return Ok(());
        }
        // A `VALUES` list takes at most 1000 rows; a chunk stays well below that.
        const KEYS_PER_QUERY: usize = 500;
        let compounds = Self::distinct_keys(compounds);
        let mut compositions = Vec::new();
        for chunk in compounds.chunks(KEYS_PER_QUERY) {
            let sql = format!(
                "SET NOCOUNT ON; SELECT c.CompoundEntityTypeId, c.CompoundEntityId, c.CompoundEntityVersionId, c.ComponentEntityTypeId, c.ComponentEntityId, c.ComponentEntityVersionId FROM EntityVersionComposition c JOIN (VALUES {}) AS k(t, i, v) ON c.CompoundEntityTypeId = k.t AND c.CompoundEntityId = k.i AND c.CompoundEntityVersionId = k.v",
                Self::values_list(chunk)
            );
            let raw = self.execute_live_sql(&sql)?;
            for row in parse_live_rows(&raw, 6)? {
                compositions.push(EntityVersionCompositionRecord {
                    compound_entity_type_id: parse_live_int(row[0], "CompoundEntityTypeId")?,
                    compound_entity_id: parse_live_int(row[1], "CompoundEntityId")?,
                    compound_entity_version_id: parse_live_int(row[2], "CompoundEntityVersionId")?,
                    component_entity_type_id: parse_live_int(row[3], "ComponentEntityTypeId")?,
                    component_entity_id: parse_live_int(row[4], "ComponentEntityId")?,
                    component_entity_version_id: parse_live_int(
                        row[5],
                        "ComponentEntityVersionId",
                    )?,
                });
            }
        }
        let mut version_keys: Vec<(i32, i32, i32)> = parts.to_vec();
        version_keys.extend(compositions.iter().map(|c| {
            (
                c.component_entity_type_id,
                c.component_entity_id,
                c.component_entity_version_id,
            )
        }));
        let version_keys = Self::distinct_keys(&version_keys);
        let mut versions = Vec::new();
        for chunk in version_keys.chunks(KEYS_PER_QUERY) {
            let keys = Self::values_list(chunk);
            versions.extend(self.read_part_records(
                false,
                3,
                |binary| {
                    format!(
                        "SET NOCOUNT ON; SELECT e.EntityTypeId, e.EntityId, e.EntityVersionId, {} FROM EntityVersion e JOIN (VALUES {keys}) AS k(t, i, v) ON e.EntityTypeId = k.t AND e.EntityId = k.i AND e.EntityVersionId = k.v",
                        part_payload_columns("e.", binary)
                    )
                },
                part_key,
            )?);
        }

        {
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let mut known_compositions: std::collections::HashSet<(i32, i32, i32, i32, i32, i32)> =
                store
                    .entity_version_compositions
                    .iter()
                    .map(|c| {
                        (
                            c.compound_entity_type_id,
                            c.compound_entity_id,
                            c.compound_entity_version_id,
                            c.component_entity_type_id,
                            c.component_entity_id,
                            c.component_entity_version_id,
                        )
                    })
                    .collect();
            for rec in compositions {
                let key = (
                    rec.compound_entity_type_id,
                    rec.compound_entity_id,
                    rec.compound_entity_version_id,
                    rec.component_entity_type_id,
                    rec.component_entity_id,
                    rec.component_entity_version_id,
                );
                if known_compositions.insert(key) {
                    store.entity_version_compositions.push(rec);
                }
            }
            let mut known_parts: std::collections::HashSet<(i32, i32, i32)> = store
                .entity_versions
                .iter()
                .map(|ev| (ev.entity_type_id, ev.entity_id, ev.entity_version_id))
                .collect();
            for rec in versions {
                let key = (rec.entity_type_id, rec.entity_id, rec.entity_version_id);
                if known_parts.insert(key) {
                    store.entity_versions.push(rec);
                }
            }
        }

        let mut bulk = self
            .bulk
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        bulk.compositions.extend(compounds);
        bulk.entity_versions.extend(version_keys);
        Ok(())
    }

    /// Whether the compositions of this compound are answered from memory.
    pub(super) fn composition_is_covered(
        &self,
        compound_type: i32,
        compound_id: i32,
        compound_ver: i32,
    ) -> bool {
        self.bulk.read().is_ok_and(|bulk| {
            bulk.compositions
                .contains(&(compound_type, compound_id, compound_ver))
        })
    }

    /// Whether this `EntityVersion` key is answered from memory, present or absent.
    pub(super) fn entity_version_is_covered(
        &self,
        entity_type: i32,
        entity_id: i32,
        version_id: i32,
    ) -> bool {
        self.bulk.read().is_ok_and(|bulk| {
            bulk.entity_versions
                .contains(&(entity_type, entity_id, version_id))
        })
    }

    /// Drops the set-wise coverage after a write, so later reads go to the database again.
    pub(super) fn clear_bulk_coverage(&self) {
        let mut bulk = self
            .bulk
            .write()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        *bulk = BulkCoverage::default();
    }
}
