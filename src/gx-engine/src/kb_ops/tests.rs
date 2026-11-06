use super::*;
use crate::catalog::{KbObject, ObjectId, SourcePart};
use crate::udm::ModelEntityVersionRecord;
use crate::udm::writer::{UdmObjectIdentity, UdmWriter};

const ALPHA_SHA: &str = "8d6e1d4ad45838c31ecd8f897bb9909f6f81f58695b62e20b932165db2ac3e8b";

fn object(name: &str, parts: &[(&str, &str)]) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(name).expect("object id"),
        name,
        ObjectKind::Procedure,
    );
    for (part, source) in parts {
        object.source_parts.push(SourcePart {
            name: part.to_string(),
            source: source.to_string(),
        });
    }
    object
}

fn seed_metadata(
    client: &UdmClient,
    identity: &UdmObjectIdentity,
    parent: Option<(i32, i32)>,
    parent_guid: Option<&str>,
    parent_name: Option<&str>,
    object_saved_ticks: Option<i64>,
    model_ticks: Option<i64>,
) {
    let model_id = client.design_model_id();
    let mut store = client.storage_for_test().write().expect("store");
    store.entity_guids.insert(
        (identity.entity_type_id, identity.entity_id),
        "83a55789-716f-4c13-a03c-215b167050e8".to_string(),
    );
    if let Some(parent) = parent {
        store.model_parents.insert(
            (model_id, identity.entity_type_id, identity.entity_id),
            parent,
        );
        if let Some(guid) = parent_guid {
            store.entity_guids.insert(parent, guid.to_string());
        }
        if let Some(name) = parent_name {
            store.model_entity_versions.push(ModelEntityVersionRecord {
                model_id,
                entity_type_id: parent.0,
                entity_id: parent.1,
                entity_version_id: 1,
                name: name.to_string(),
            });
        }
    }
    if let Some(ticks) = object_saved_ticks {
        store.object_saved_timestamps.insert(
            (
                model_id,
                identity.entity_type_id,
                identity.entity_id,
                identity.version_id,
            ),
            ticks,
        );
    }
    if let Some(ticks) = model_ticks {
        store.model_entity_version_timestamps.insert(
            (
                model_id,
                identity.entity_type_id,
                identity.entity_id,
                identity.version_id,
            ),
            ticks,
        );
    }
}

#[test]
fn an_unverified_kind_cannot_get_a_flat_sdk_page_or_token() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let mut provider = object("Provider", &[("SOURCE", "// provider")]);
    provider.kind = ObjectKind::DataProvider;
    let identity = writer.create_object(&provider).expect("create provider");
    seed_metadata(
        &client,
        &identity,
        None,
        None,
        None,
        Some(SAVED_TICKS),
        None,
    );
    let read =
        read_object(&client, "Provider", Some(&["Source".to_string()])).expect("read stored text");
    assert_eq!(read.version_token, None);
    let error = read_object_page(&client, "Provider", "Source", None, None, ReadClient::Mcp)
        .expect_err("unverified object kind must not claim SDK page parity");
    assert!(
        error.contains("not served by the native SDK-text read route"),
        "{error}"
    );
}

#[test]
fn a_single_part_read_emits_the_sdk_version_token() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha")]))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        Some((12, 17)),
        Some("81eb782b-33a0-4f12-84bc-d6694a9d78f4"),
        Some("ParentModule"),
        Some(636_163_074_130_000_000),
        Some(637_847_356_281_230_000),
    );

    let read = read_object(&client, "PAlpha", Some(&["Source".to_string()])).expect("read");
    assert_eq!(read.parts.len(), 1);
    let expected = format!("636163074130000000:81eb782b33a04f1284bcd6694a9d78f4:{ALPHA_SHA}");
    assert_eq!(read.version_token.as_deref(), Some(expected.as_str()));
}

#[test]
fn a_full_read_of_a_one_part_object_does_not_borrow_the_single_part_token() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha")]))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        Some((12, 17)),
        Some("81eb782b-33a0-4f12-84bc-d6694a9d78f4"),
        None,
        Some(636_163_074_130_000_000),
        None,
    );

    let read = read_object(&client, "PAlpha", None).expect("read");
    assert_eq!(read.parts.len(), 1);
    assert_eq!(read.version_token, None);
}

#[test]
fn a_missing_object_saved_output_uses_the_model_timestamp_and_root_parent() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha")]))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        None,
        None,
        None,
        None,
        Some(637_847_356_281_230_000),
    );

    let read = read_object(&client, "PAlpha", Some(&["Source".to_string()])).expect("read");
    let expected = format!("637847356281230000:root:{ALPHA_SHA}");
    assert_eq!(read.version_token.as_deref(), Some(expected.as_str()));
}

#[test]
fn a_parent_without_a_guid_uses_the_parent_name() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha")]))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        Some((12, 17)),
        None,
        Some("ParentModule"),
        Some(636_163_074_130_000_000),
        None,
    );

    let read = read_object(&client, "PAlpha", Some(&["Source".to_string()])).expect("read");
    let expected = format!("636163074130000000:ParentModule:{ALPHA_SHA}");
    assert_eq!(read.version_token.as_deref(), Some(expected.as_str()));
}

#[test]
fn a_multi_part_read_has_no_single_part_version_token() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object(
            "PAlpha",
            &[("SOURCE", "// alpha"), ("RULES", "parm(&x);")],
        ))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        Some((12, 17)),
        Some("81eb782b-33a0-4f12-84bc-d6694a9d78f4"),
        None,
        Some(636_163_074_130_000_000),
        None,
    );

    let read = read_object(&client, "PAlpha", None).expect("read");
    assert_eq!(read.parts.len(), 2);
    assert_eq!(read.version_token, None);
}

fn mark_not_generated(client: &UdmClient, identity: &UdmObjectIdentity) {
    client
        .storage_for_test()
        .write()
        .expect("store")
        .generate_flags
        .insert(
            (
                client.design_model_id(),
                identity.entity_type_id,
                identity.entity_id,
            ),
            "False".into(),
        );
}

#[test]
fn a_rules_only_procedure_page_uses_the_sdk_source_alias() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object("RulesOnly", &[("RULES", "parm(&x);\n")]))
        .expect("create procedure");
    seed_metadata(&client, &identity, None, None, None, Some(10), None);
    mark_not_generated(&client, &identity);

    for (offset, limit) in [(None, None), (None, Some(0)), (Some(99999), Some(0))] {
        let rules = read_object_page(
            &client,
            "RulesOnly",
            "Rules",
            offset,
            limit,
            ReadClient::Mcp,
        )
        .expect("Rules page");
        let page = read_object_page(
            &client,
            "RulesOnly",
            "Source",
            offset,
            limit,
            ReadClient::Mcp,
        )
        .expect("SDK Source alias");
        assert_eq!(page.part, "Source");
        assert_eq!(page.source, rules.source);
        assert_eq!(page.version_token, rules.version_token);
        assert_eq!(page.offset, rules.offset);
        assert_eq!(page.limit, rules.limit);
        assert_eq!(page.explicit_full_read, limit == Some(0));
    }
    assert!(read_object_page(&client, "RulesOnly", "Events", None, None, ReadClient::Mcp).is_err());
}

#[test]
fn the_source_alias_does_not_guess_between_stored_text_parts() {
    for parts in [
        vec![("RULES", "parm(&x);"), ("EVENTS", "Event Start\nEndEvent")],
        vec![("RULES", "parm(&x);"), ("CONDITIONS", "1 = 1")],
        vec![("RULES", "parm(&x);"), ("RULES", "parm(&y);")],
        vec![("EVENTS", "Event Start\nEndEvent")],
    ] {
        let client = UdmClient::new_in_memory();
        let writer = UdmWriter::new(Arc::new(client.clone()));
        let identity = writer
            .create_object(&object("NoAlias", &parts))
            .expect("create");
        seed_metadata(&client, &identity, None, None, None, Some(10), None);
        mark_not_generated(&client, &identity);
        assert!(
            read_object_page(&client, "NoAlias", "Source", None, None, ReadClient::Mcp).is_err()
        );
    }
}

#[test]
fn the_source_alias_never_replaces_an_explicit_source_or_other_object_kind() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object(
            "HasSource",
            &[("SOURCE", "// source"), ("RULES", "parm();")],
        ))
        .expect("create");
    seed_metadata(&client, &identity, None, None, None, Some(10), None);
    let page = read_object_page(&client, "HasSource", "Source", None, None, ReadClient::Mcp)
        .expect("source");
    assert_eq!(page.source, "// source");

    for kind in [
        ObjectKind::WebPanel,
        ObjectKind::Transaction,
        ObjectKind::DataProvider,
    ] {
        let client = UdmClient::new_in_memory();
        let writer = UdmWriter::new(Arc::new(client.clone()));
        let mut obj = object("OtherKind", &[("RULES", "parm();")]);
        obj.kind = kind;
        let identity = writer.create_object(&obj).expect("create other kind");
        seed_metadata(&client, &identity, None, None, None, Some(10), None);
        assert!(
            read_object_page(&client, "OtherKind", "Source", None, None, ReadClient::Mcp).is_err()
        );
    }
}

#[test]
fn a_default_mcp_page_uses_the_sdk_defaults_and_keeps_the_full_text_token() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let source = (0..250)
        .map(|index| format!("line{index}"))
        .collect::<Vec<_>>()
        .join("\n");
    let identity = writer
        .create_object(&object("PAlpha", &[("SOURCE", &source)]))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        Some((12, 17)),
        Some("81eb782b-33a0-4f12-84bc-d6694a9d78f4"),
        Some("ParentModule"),
        Some(636_163_074_130_000_000),
        None,
    );

    let full_read =
        read_object(&client, "PAlpha", Some(&["Source".to_string()])).expect("full read");
    let page = read_object_page(&client, "PAlpha", "Source", None, None, ReadClient::Mcp)
        .expect("page read");

    assert_eq!(page.part, "Source");
    assert_eq!(page.offset, 0);
    assert_eq!(page.limit, 200);
    assert_eq!(page.total_lines, 250);
    assert_eq!(page.total_bytes, source.len());
    assert!(page.truncated);
    assert!(page.is_truncated_by_worker);
    assert!(!page.explicit_full_read);
    assert_eq!(page.suggested_next_offset, Some(200));
    assert_eq!(page.suggested_next_limit, Some(200));
    assert!(page.source.starts_with("line0\nline1\n"));
    assert!(page.source.ends_with(TRUNCATION_MARKER));
    assert_eq!(page.version_token, full_read.version_token.expect("token"));
}

#[test]
fn a_limit_zero_read_returns_the_original_slice_and_the_same_token() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha\n")]))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        None,
        None,
        None,
        Some(636_163_074_130_000_000),
        None,
    );

    let full_read =
        read_object(&client, "PAlpha", Some(&["Source".to_string()])).expect("full read");
    let page = read_object_page(&client, "PAlpha", "Source", None, Some(0), ReadClient::Mcp)
        .expect("page read");

    assert_eq!(page.source, "// alpha\n");
    assert_eq!(page.offset, 0);
    assert_eq!(page.limit, 1);
    assert_eq!(page.total_lines, 1);
    assert!(!page.truncated);
    assert!(page.explicit_full_read);
    assert_eq!(page.version_token, full_read.version_token.expect("token"));
}

#[test]
fn an_explicit_window_returns_only_that_window() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object("PAlpha", &[("SOURCE", "a\nb\nc")]))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        None,
        None,
        None,
        Some(636_163_074_130_000_000),
        None,
    );

    let page = read_object_page(
        &client,
        "PAlpha",
        "source",
        Some(1),
        Some(1),
        ReadClient::Mcp,
    )
    .expect("page read");

    assert_eq!(page.part, "Source");
    assert!(page.source.starts_with('b'));
    assert!(page.source.ends_with(TRUNCATION_MARKER));
    assert_eq!(page.offset, 1);
    assert_eq!(page.limit, 1);
    assert_eq!(page.total_lines, 3);
    assert!(page.truncated);
    assert_eq!(page.suggested_next_offset, Some(2));
    assert_eq!(page.suggested_next_limit, Some(1));
}

#[test]
fn a_part_without_an_sdk_text_token_is_refused_by_the_page_route() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object(
            "PAlpha",
            &[("SOURCE", "// alpha"), ("VARIABLES", "<Variables/>")],
        ))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        None,
        None,
        None,
        Some(636_163_074_130_000_000),
        None,
    );

    let error = read_object_page(&client, "PAlpha", "Variables", None, None, ReadClient::Mcp)
        .expect_err("Variables has no SDK-text token");
    assert!(error.contains("not served"), "{error}");
}

#[test]
fn a_missing_part_is_refused_by_the_page_route() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    writer
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha")]))
        .expect("create PAlpha");

    let error = read_object_page(&client, "PAlpha", "Events", None, None, ReadClient::Mcp)
        .expect_err("Events is absent");
    assert!(error.contains("does not have part"), "{error}");
}

#[test]
fn variable_ids_are_not_exposed_and_variables_has_no_sdk_token() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let variables = "<Variables><Variable Name=\"Page\" Id=\"7\"><Properties>\
         <Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property>\
         <Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property>\
         <Property><Name>Length</Name><Value>6</Value></Property>\
         </Properties></Variable></Variables>";
    let identity = writer
        .create_object(&object(
            "PAlpha",
            &[("SOURCE", "&Page = 1\n"), ("VARIABLES", variables)],
        ))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        None,
        None,
        None,
        Some(636_163_074_130_000_000),
        None,
    );

    // The fixture must really carry the code-generator-only part, otherwise the
    // product assertion below would pass without exercising the filter.
    let loaded = UdmReader::new(Arc::new(client.clone()))
        .read_object_by_name("PAlpha")
        .expect("read stored object")
        .expect("PAlpha exists");
    assert!(
        loaded
            .source_parts
            .iter()
            .any(|part| part.name == VARIABLE_IDS_PART),
        "the reader must expose the synthetic part to generators"
    );

    let read = read_object(&client, "PAlpha", Some(&["Variables".to_string()])).expect("read");
    assert_eq!(read.parts.len(), 1);
    assert_eq!(read.parts[0].name, "VARIABLES");
    // The SDK Variables part is declaration text; this payload is the engine's
    // portable JSON, so no SDK-matching token may be emitted for it.
    assert_eq!(read.version_token, None);
}

#[test]
fn an_in_memory_object_without_metadata_keeps_reading_without_a_token() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    writer
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha")]))
        .expect("create PAlpha");

    let read = read_object(&client, "PAlpha", None).expect("read");
    assert_eq!(read.version_token, None);
}

const SAVED_TICKS: i64 = 636_163_074_130_000_000;

/// A one-`SOURCE` Procedure whose stored metadata the caller seeds below.
fn alpha_with_metadata(
    parent: Option<(i32, i32)>,
    parent_guid: Option<&str>,
    parent_name: Option<&str>,
    object_saved_ticks: Option<i64>,
    model_ticks: Option<i64>,
) -> UdmClient {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha")]))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        parent,
        parent_guid,
        parent_name,
        object_saved_ticks,
        model_ticks,
    );
    client
}

fn source_read_error(client: &UdmClient) -> String {
    read_object(client, "PAlpha", Some(&["Source".to_string()]))
        .expect_err("unsound token inputs must refuse the read")
}

#[test]
fn a_parent_stored_with_a_zero_half_is_refused_not_treated_as_root() {
    for parent in [(12, 0), (0, 17)] {
        let client = alpha_with_metadata(Some(parent), None, None, Some(SAVED_TICKS), None);
        let error = source_read_error(&client);
        assert!(error.contains("zero half"), "{parent:?}: {error}");
    }
}

#[test]
fn a_malformed_parent_guid_is_refused_instead_of_falling_back_to_the_name() {
    let client = alpha_with_metadata(
        Some((12, 17)),
        Some("not-a-guid"),
        Some("ParentModule"),
        Some(SAVED_TICKS),
        None,
    );
    let error = source_read_error(&client);
    assert!(error.contains("parent cannot be established"), "{error}");
    assert!(error.contains("GUID"), "{error}");
}

#[test]
fn a_parent_name_with_a_colon_is_refused() {
    let client = alpha_with_metadata(
        Some((12, 17)),
        None,
        Some("Parent:Module"),
        Some(SAVED_TICKS),
        None,
    );
    let error = source_read_error(&client);
    assert!(error.contains("parent cannot be established"), "{error}");
    assert!(error.contains("':'"), "{error}");
}

#[test]
fn a_parent_with_neither_guid_nor_name_is_refused() {
    let client = alpha_with_metadata(Some((12, 17)), None, None, Some(SAVED_TICKS), None);
    let error = source_read_error(&client);
    assert!(error.contains("neither a GUID nor a name"), "{error}");
}

#[test]
fn missing_last_update_ticks_are_refused() {
    let client = alpha_with_metadata(None, None, None, None, None);
    let error = source_read_error(&client);
    assert!(error.contains("no stored last-update timestamp"), "{error}");
}

#[test]
fn out_of_range_ticks_are_refused_and_not_rescued_by_the_fallback_timestamp() {
    for ticks in [-1, 3_155_378_976_000_000_000] {
        let client =
            alpha_with_metadata(None, None, None, Some(ticks), Some(637_847_356_281_230_000));
        let error = source_read_error(&client);
        assert!(
            error.contains("last-update timestamp is invalid"),
            "{ticks}: {error}"
        );
    }
}

#[test]
fn a_duplicate_stored_source_part_has_no_token_and_is_refused_by_the_page_route() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let identity = writer
        .create_object(&object(
            "PAlpha",
            &[("SOURCE", "// first"), ("SOURCE", "// second")],
        ))
        .expect("create PAlpha");
    seed_metadata(
        &client,
        &identity,
        None,
        None,
        None,
        Some(SAVED_TICKS),
        None,
    );

    let read = read_object(&client, "PAlpha", Some(&["Source".to_string()])).expect("read");
    assert_eq!(read.parts.len(), 2);
    assert_eq!(read.version_token, None);
    let error = read_object_page(&client, "PAlpha", "Source", None, None, ReadClient::Mcp)
        .expect_err("two stored SOURCE parts are ambiguous");
    assert!(
        error.contains("did not resolve to one stored part"),
        "{error}"
    );
}

#[test]
fn only_the_verified_object_kinds_receive_an_sdk_text_token() {
    let kinds = [
        ObjectKind::Attribute,
        ObjectKind::DataProvider,
        ObjectKind::DataSelector,
        ObjectKind::DataView,
        ObjectKind::DesignSystem,
        ObjectKind::Domain,
        ObjectKind::Module,
        ObjectKind::Procedure,
        ObjectKind::StructuredDataType,
        ObjectKind::Table,
        ObjectKind::Transaction,
        ObjectKind::WebPanel,
    ];
    for kind in kinds {
        let verified = matches!(
            kind,
            ObjectKind::Procedure | ObjectKind::WebPanel | ObjectKind::Transaction
        );
        for part in ["SOURCE", "EVENTS", "RULES", "CONDITIONS"] {
            assert_eq!(
                sdk_text_token_eligible(kind, part),
                verified,
                "{kind:?} {part}"
            );
        }
        for part in ["VARIABLES", "STRUCTURE", "WEBFORM", "PATTERNINSTANCE"] {
            assert!(!sdk_text_token_eligible(kind, part), "{kind:?} {part}");
        }
        // The page route resolves the kind from the type name `read_object` reports.
        assert_eq!(
            kind_from_type_name(type_name(kind)).is_some_and(sdk_text_object_kind),
            verified,
            "{kind:?}"
        );
    }
}

#[test]
fn scoped_reads_select_the_active_design_model_inside_the_read_view() {
    let trunk = UdmClient::new_in_memory();
    let branch = trunk.clone().with_design_model_id(4);
    let trunk_id = UdmWriter::new(Arc::new(trunk.clone()))
        .create_object(&object("PAlpha", &[("SOURCE", "// alpha")]))
        .expect("create trunk PAlpha");
    seed_metadata(&trunk, &trunk_id, None, None, None, Some(SAVED_TICKS), None);

    let branch_id = UdmWriter::new(Arc::new(branch.clone()))
        .create_object(&object("PAlpha", &[("SOURCE", "// branch")]))
        .expect("create branch PAlpha");
    seed_metadata(
        &branch,
        &branch_id,
        None,
        None,
        None,
        Some(637_847_356_281_230_000),
        None,
    );

    let unnamed_read =
        read_object_scoped(&trunk, None, "PAlpha", Some(&["Source".to_string()])).unwrap_err();
    assert!(
        matches!(&unnamed_read, KbReadError::VersionScope(msg) if msg.contains("active version must be named")),
        "{unnamed_read:?}"
    );
    let unnamed_page = read_object_page_scoped(
        &trunk,
        None,
        "PAlpha",
        "Source",
        None,
        None,
        ReadClient::Mcp,
    )
    .unwrap_err();
    assert!(
        matches!(&unnamed_page, KbReadError::VersionScope(msg) if msg.contains("active version must be named")),
        "{unnamed_page:?}"
    );

    let active_branch = Some(ActiveVersion {
        design_model_id: 4,
        kb_version_id: 4,
    });
    let expected_branch =
        read_object(&branch, "PAlpha", Some(&["Source".to_string()])).expect("direct branch read");
    let scoped_read = read_object_scoped(
        &trunk,
        active_branch,
        "PAlpha",
        Some(&["Source".to_string()]),
    )
    .expect("scoped branch read from trunk client");
    assert_eq!(scoped_read.parts[0].source, "// branch");
    assert_eq!(scoped_read.version_token, expected_branch.version_token);

    let scoped_page = read_object_page_scoped(
        &trunk,
        active_branch,
        "PAlpha",
        "Source",
        None,
        None,
        ReadClient::Mcp,
    )
    .expect("scoped branch page read from trunk client");
    assert_eq!(scoped_page.source, "// branch");
    assert_eq!(
        Some(scoped_page.version_token.as_str()),
        expected_branch.version_token.as_deref()
    );

    let wrong_version = read_object_page_scoped(
        &trunk,
        Some(ActiveVersion {
            design_model_id: 4,
            kb_version_id: 99,
        }),
        "PAlpha",
        "Source",
        None,
        None,
        ReadClient::Mcp,
    )
    .unwrap_err();
    assert!(
        matches!(&wrong_version, KbReadError::VersionScope(msg) if msg.contains("not to the active version 99")),
        "{wrong_version:?}"
    );
}

#[test]
fn scoped_reads_validate_inputs_before_selecting_version_scope() {
    let trunk = UdmClient::new_in_memory();
    let branch = trunk.clone().with_design_model_id(4);
    UdmWriter::new(Arc::new(trunk.clone()))
        .create_object(&object("PAlpha", &[("SOURCE", "// trunk")]))
        .expect("create trunk PAlpha");
    UdmWriter::new(Arc::new(branch))
        .create_object(&object("PAlpha", &[("SOURCE", "// branch")]))
        .expect("create branch PAlpha");

    assert_eq!(
        read_object_scoped(&trunk, None, "   ", None),
        Err(KbReadError::Operation("object name is required".into()))
    );
    assert_eq!(
        read_object_page_scoped(&trunk, None, "PAlpha", " ", None, None, ReadClient::Mcp),
        Err(KbReadError::Operation(
            "part is required for a single-part page read".into()
        ))
    );
    assert_eq!(
        read_object_page_scoped(&trunk, None, " ", "Source", None, None, ReadClient::Mcp),
        Err(KbReadError::Operation("object name is required".into()))
    );
}

#[test]
fn autonomous_metadata_and_version_token_resolution() {
    assert!(sdk_text_part("SOURCE"));
    assert!(sdk_text_part("EVENTS"));
    assert!(sdk_text_part("RULES"));
    assert!(sdk_text_part("CONDITIONS"));
    assert!(sdk_text_token_eligible(ObjectKind::Procedure, "SOURCE"));

    let client = UdmClient::new_in_memory();
    let source_text = "// autonomous procedure logic";
    let identity = UdmWriter::new(Arc::new(client.clone()))
        .create_object(&object("PAutoTokenProc", &[("SOURCE", source_text)]))
        .expect("create PAutoTokenProc");
    seed_metadata(
        &client,
        &identity,
        None,
        None,
        None,
        Some(SAVED_TICKS),
        None,
    );

    let read_all = read_object(&client, "PAutoTokenProc", None).expect("read PAutoTokenProc");
    assert_eq!(read_all.parts.len(), 1);
    let part = &read_all.parts[0];
    assert_eq!(part.name, "SOURCE");
    assert_eq!(part.source, source_text);

    let token_page = read_object_page(
        &client,
        "PAutoTokenProc",
        "SOURCE",
        None,
        None,
        ReadClient::Mcp,
    )
    .expect("read page with token");
    assert_eq!(token_page.source, source_text);
    assert!(!token_page.version_token.is_empty());
}
