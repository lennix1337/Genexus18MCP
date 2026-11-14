use super::*;

#[test]
fn a_write_drops_the_entity_version_coverage() {
    let client = UdmClient::new_in_memory();
    client
        .bulk
        .write()
        .expect("bulk")
        .entity_versions
        .insert((7, 8, 9));
    assert!(client.entity_version_is_covered(7, 8, 9));
    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 1,
            entity_id: 1,
            entity_version_id: 1,
            data: Vec::new(),
            properties: None,
        })
        .expect("insert");
    assert!(!client.entity_version_is_covered(7, 8, 9));
}

#[test]
fn a_write_drops_the_composition_coverage() {
    let client = UdmClient::new_in_memory();
    client
        .bulk
        .write()
        .expect("bulk")
        .compositions
        .insert((7, 8, 9));
    assert!(client.composition_is_covered(7, 8, 9));
    client
        .insert_composition(EntityVersionCompositionRecord {
            compound_entity_type_id: 1,
            compound_entity_id: 1,
            compound_entity_version_id: 1,
            component_entity_type_id: 2,
            component_entity_id: 2,
            component_entity_version_id: 2,
        })
        .expect("insert");
    assert!(!client.composition_is_covered(7, 8, 9));
}

/// A connection that cannot answer: any query fails, so an `Ok` answer proves the lookup stayed
/// in memory.
fn unreachable_live_client() -> UdmClient {
    use crate::udm::connection::AuthMode;
    UdmClient::from_connection_info(KbConnectionInfo {
        server_instance: "(LocalDB)\\GxEngineNoSuchInstance".to_string(),
        db_name: "gx_engine_no_such_database".to_string(),
        auth_mode: AuthMode::Integrated,
        create_db_in_kb_folder: None,
        directory: None,
        data_file: None,
        log_file: None,
        host_name: None,
        detach_on_close: None,
    })
}

#[test]
fn a_covered_key_answers_a_missing_part_with_no_row_and_no_query() {
    let client = unreachable_live_client();
    client
        .bulk
        .write()
        .expect("bulk")
        .entity_versions
        .insert((47, 3, 9));
    assert_eq!(client.get_entity_version(47, 3, 9).expect("no query"), None);
}

#[test]
fn an_uncovered_key_still_queries_on_a_miss() {
    // Mutation check for the coverage test: an uncovered miss must reach the database, which
    // fails on this connection. A broken shortcut would answer Ok(None) here.
    let client = unreachable_live_client();
    assert!(client.get_entity_version(47, 3, 9).is_err());
}

#[test]
fn a_rollback_drops_the_entity_version_coverage_of_a_prefetch_inside_the_transaction() {
    // Rollback restores the whole snapshot taken at begin, so a row a prefetch loaded inside the
    // transaction is gone from memory afterwards. Its key must not stay covered, or a lookup
    // would answer "absent" for a row that exists in the database.
    let client = unreachable_live_client();
    client.begin_transaction().expect("begin");
    client
        .storage
        .write()
        .expect("storage")
        .entity_versions
        .push(EntityVersionRecord {
            entity_type_id: 47,
            entity_id: 3,
            entity_version_id: 1,
            data: vec![1],
            properties: None,
        });
    client
        .bulk
        .write()
        .expect("bulk")
        .entity_versions
        .insert((47, 3, 1));
    client.rollback_transaction().expect("rollback");

    assert!(client.get_entity_version(47, 3, 1).is_err());
}

#[test]
fn a_rollback_drops_the_composition_coverage_of_a_prefetch_inside_the_transaction() {
    let client = unreachable_live_client();
    client.begin_transaction().expect("begin");
    client
        .storage
        .write()
        .expect("storage")
        .entity_version_compositions
        .push(EntityVersionCompositionRecord {
            compound_entity_type_id: 47,
            compound_entity_id: 3,
            compound_entity_version_id: 1,
            component_entity_type_id: 2,
            component_entity_id: 2,
            component_entity_version_id: 2,
        });
    client
        .bulk
        .write()
        .expect("bulk")
        .compositions
        .insert((47, 3, 1));
    client.rollback_transaction().expect("rollback");

    assert!(client.get_compositions(47, 3, 1).is_err());
}

#[test]
fn the_model_part_read_takes_served_types_only() {
    let sql = UdmClient::model_parts_sql(2, &[38, 81, 201], 1, false);
    assert!(sql.contains("m.ModelId = 2"), "{sql}");
    assert!(sql.contains("AND e.EntityTypeId IN (38, 81, 201)"), "{sql}");
    assert!(
        sql.ends_with(&format!("e.EntityId % {PART_READ_SHARDS} = 1")),
        "{sql}"
    );
    // A Knowledge Base serving no type reads no part.
    assert!(UdmClient::model_parts_sql(2, &[], 0, false).contains("AND 1 = 0"));
}

#[test]
fn the_model_part_read_differs_between_transports_only_in_its_payload_columns() {
    // The statement sqlcmd receives is the one it received before the TDS transport existed.
    let hex = UdmClient::model_parts_sql(2, &[38], 1, false);
    assert!(
        hex.starts_with("SET NOCOUNT ON; SELECT e.EntityTypeId, e.EntityId, e.EntityVersionId, ISNULL(DATALENGTH(e.EntityVersionData), 0), CONVERT(VARCHAR(MAX), e.EntityVersionData, 2), ISNULL(DATALENGTH(e.EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(e.EntityVersionProperties AS VARBINARY(MAX)), 2) FROM EntityVersion e WHERE (EXISTS "),
        "{hex}"
    );
    // The binary form selects the same rows; only the two payloads lose their hex conversion.
    let binary = UdmClient::model_parts_sql(2, &[38], 1, true);
    assert!(
        binary.starts_with("SET NOCOUNT ON; SELECT e.EntityTypeId, e.EntityId, e.EntityVersionId, ISNULL(DATALENGTH(e.EntityVersionData), 0), e.EntityVersionData, ISNULL(DATALENGTH(e.EntityVersionProperties), 0), CAST(e.EntityVersionProperties AS VARBINARY(MAX)) FROM EntityVersion e WHERE (EXISTS "),
        "{binary}"
    );
    let from = |sql: &str| sql[sql.find(" FROM EntityVersion e ").unwrap()..].to_string();
    assert_eq!(from(&hex), from(&binary));
}

#[test]
fn only_served_part_keys_are_covered_by_the_model_read() {
    let keys = [(38, 1, 1), (130, 5, 1), (81, 2, 3)];
    assert_eq!(
        UdmClient::served_keys(keys.iter().copied(), &[38, 81]),
        vec![(38, 1, 1), (81, 2, 3)]
    );
}
