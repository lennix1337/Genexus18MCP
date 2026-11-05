//! Integration tests for the product-facing KB operations (`kb-read`,
//! `kb-query`, `kb-write`) that the MCP Gateway's engine backend invokes.

#![forbid(unsafe_code)]

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart};
use gx_engine::kb_ops::{
    kind_from_type_name, query_objects, read_object, type_name, write_object_part,
};
use gx_engine::udm::{AuthMode, KbConnectionInfo, UdmClient, UdmWriter};
use std::sync::Arc;

fn object(name: &str, kind: ObjectKind, parts: &[(&str, &str)]) -> KbObject {
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, kind);
    for (part, source) in parts {
        object.source_parts.push(SourcePart {
            name: part.to_string(),
            source: source.to_string(),
        });
    }
    object
}

fn seeded_client() -> UdmClient {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));

    writer
        .create_object(&object(
            "PAlpha",
            ObjectKind::Procedure,
            &[("SOURCE", "// alpha"), ("RULES", "parm(&x);")],
        ))
        .expect("create PAlpha");

    let mut beta = object("PBeta", ObjectKind::Procedure, &[("SOURCE", "// beta")]);
    beta.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("PAlpha").unwrap(),
    });
    writer.create_object(&beta).expect("create PBeta");

    writer
        .create_object(&object(
            "TGamma",
            ObjectKind::Transaction,
            &[("STRUCTURE", "<Structure/>")],
        ))
        .expect("create TGamma");

    client
}

#[test]
fn read_serves_stored_parts_and_reports_missing_ones() {
    let client = seeded_client();

    let all = read_object(&client, "PAlpha", None).expect("read all parts");
    assert_eq!(all.name, "PAlpha");
    assert_eq!(all.object_type, "Procedure");
    let names: Vec<&str> = all.parts.iter().map(|part| part.name.as_str()).collect();
    assert!(names.contains(&"SOURCE"));
    assert!(names.contains(&"RULES"));
    assert!(all.missing_parts.is_empty());

    // Name and part lookup are case-insensitive; an absent part is reported,
    // never invented.
    let filtered = read_object(
        &client,
        "palpha",
        Some(&["source".to_string(), "Missing".to_string()]),
    )
    .expect("filtered read");
    assert_eq!(filtered.parts.len(), 1);
    assert_eq!(filtered.parts[0].name, "SOURCE");
    assert_eq!(filtered.parts[0].source, "// alpha");
    assert_eq!(filtered.missing_parts, vec!["Missing".to_string()]);
    assert!(filtered.available_parts.contains(&"SOURCE".to_string()));
    assert!(filtered.available_parts.contains(&"RULES".to_string()));
}

#[test]
fn read_of_unknown_object_is_an_error() {
    let client = seeded_client();
    let error = read_object(&client, "PDoesNotExist", None).unwrap_err();
    assert!(error.contains("PDoesNotExist"), "{error}");
    assert!(error.contains("not found"), "{error}");
}

#[test]
fn read_returns_plain_source_text_for_tokenized_parts() {
    let client = seeded_client();
    let all = read_object(&client, "PBeta", None).expect("read PBeta");
    assert_eq!(all.parts.len(), 1);
    assert_eq!(all.parts[0].name, "SOURCE");
    assert_eq!(all.parts[0].source, "// beta");
}

#[test]
fn query_filters_by_name_type_and_reference_with_paging() {
    let client = seeded_client();

    let by_name = query_objects(&client, Some("pbeta"), None, None, 0).expect("query by name");
    assert_eq!(by_name.total, 1);
    assert_eq!(by_name.objects[0].name, "PBeta");
    assert_eq!(by_name.objects[0].object_type, "Procedure");

    let procedures =
        query_objects(&client, None, Some("Procedure"), None, 0).expect("query by type");
    assert_eq!(procedures.total, 2);
    assert!(!procedures.truncated);
    let names: Vec<&str> = procedures
        .objects
        .iter()
        .map(|object| object.name.as_str())
        .collect();
    assert_eq!(names, vec!["PAlpha", "PBeta"]); // sorted by name

    let used_by = query_objects(&client, None, None, Some("PAlpha"), 0).expect("query usedby");
    assert_eq!(used_by.total, 1);
    assert_eq!(used_by.objects[0].name, "PBeta");

    let limited =
        query_objects(&client, None, Some("Procedure"), None, 1).expect("query with limit");
    assert_eq!(limited.objects.len(), 1);
    assert_eq!(limited.total, 2);
    assert!(limited.truncated);

    let unknown_type = query_objects(&client, None, Some("Gizmo"), None, 0).unwrap_err();
    assert!(unknown_type.contains("Gizmo"), "{unknown_type}");
}

#[test]
fn query_orders_modules_first_then_by_ordinal_name_like_the_sdk_search() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    for (name, kind) in [
        ("beta", ObjectKind::Procedure),
        ("Zeta", ObjectKind::Procedure),
        ("Alpha", ObjectKind::Procedure),
        ("ZModule", ObjectKind::Module),
    ] {
        writer
            .create_object(&object(name, kind, &[]))
            .unwrap_or_else(|error| panic!("create {name}: {error}"));
    }

    let everything = query_objects(&client, None, None, None, 0).expect("query everything");
    let names: Vec<&str> = everything
        .objects
        .iter()
        .map(|object| object.name.as_str())
        .collect();
    // Module rows first; then ordinal name order, where upper case sorts before lower case.
    assert_eq!(names, vec!["ZModule", "Alpha", "Zeta", "beta"]);

    let first_page = query_objects(&client, None, None, None, 2).expect("query first page");
    let page: Vec<&str> = first_page
        .objects
        .iter()
        .map(|object| object.name.as_str())
        .collect();
    assert_eq!(page, vec!["ZModule", "Alpha"]);
    assert!(first_page.truncated);
}

#[test]
fn write_replaces_one_part_preserving_the_others_and_bumps_the_version() {
    let client = seeded_client();

    let result = write_object_part(&client, "PAlpha", "Source", "// alpha v2", None, None)
        .expect("write source");
    assert_eq!(result.name, "PAlpha");
    assert_eq!(result.object_type, "Procedure");
    assert_eq!(result.part, "Source");
    assert_eq!(result.version_id, 2);
    assert!(!result.created);
    assert_eq!(result.bytes, "// alpha v2".len());

    let after = read_object(&client, "PAlpha", None).expect("read after write");
    let source = after
        .parts
        .iter()
        .find(|part| part.name == "SOURCE")
        .expect("source part");
    assert_eq!(source.source, "// alpha v2");
    let rules = after
        .parts
        .iter()
        .find(|part| part.name == "RULES")
        .expect("rules part");
    assert_eq!(rules.source, "parm(&x);");
}

#[test]
fn write_of_unknown_part_is_rejected() {
    let client = seeded_client();
    let error =
        write_object_part(&client, "PAlpha", "Methods", "anything", None, None).unwrap_err();
    assert!(error.contains("Methods"), "{error}");
}

#[test]
fn write_refuses_a_type_mismatch_before_any_write() {
    let client = seeded_client();

    let error = write_object_part(
        &client,
        "PAlpha",
        "Source",
        "// x",
        None,
        Some(ObjectKind::WebPanel),
    )
    .unwrap_err();
    assert!(error.contains("Procedure"), "{error}");
    assert!(error.contains("WebPanel"), "{error}");

    // The refusal left the stored object untouched.
    let after = read_object(&client, "PAlpha", None).expect("read after refused write");
    let source = after
        .parts
        .iter()
        .find(|part| part.name == "SOURCE")
        .expect("source part");
    assert_eq!(source.source, "// alpha");
}

#[test]
fn write_requires_an_existing_object_unless_create_selects_a_kind() {
    let client = seeded_client();

    let error = write_object_part(&client, "PDelta", "Source", "// new", None, None).unwrap_err();
    assert!(error.contains("PDelta"), "{error}");

    let created = write_object_part(
        &client,
        "PDelta",
        "Source",
        "// new",
        Some(ObjectKind::Procedure),
        None,
    )
    .expect("create via write");
    assert!(created.created);
    assert_eq!(created.version_id, 1);

    let read = read_object(&client, "PDelta", None).expect("read created object");
    assert_eq!(read.parts[0].source, "// new");
}

#[test]
fn write_on_a_live_connection_is_refused_instead_of_lost() {
    // A live client reads a Knowledge Base database the in-process writer
    // cannot persist to. The write must be refused, never reported as saved.
    let client = UdmClient::from_connection_info(KbConnectionInfo {
        server_instance: "(LocalDB)\\MSSQLLocalDB".to_string(),
        db_name: "GX_KB_NotWritable".to_string(),
        auth_mode: AuthMode::Integrated,
        create_db_in_kb_folder: None,
        directory: None,
        data_file: None,
        log_file: None,
        host_name: None,
        detach_on_close: None,
    });

    let error = write_object_part(&client, "PAlpha", "Source", "// x", None, None).unwrap_err();
    assert!(error.contains("live"), "{error}");
    assert!(error.contains("not persist"), "{error}");
}

#[test]
fn live_writes_enabled_flag_toggles_safely() {
    let client = UdmClient::new_in_memory();
    assert!(!client.live_writes_enabled());
    let client = client.with_live_writes(true);
    assert!(client.live_writes_enabled());
}

#[test]
fn type_name_mapping_round_trips() {
    assert_eq!(type_name(ObjectKind::Procedure), "Procedure");
    assert_eq!(type_name(ObjectKind::StructuredDataType), "SDT");
    assert_eq!(type_name(ObjectKind::WebPanel), "WebPanel");
    assert_eq!(
        kind_from_type_name("sdt"),
        Some(ObjectKind::StructuredDataType)
    );
    assert_eq!(
        kind_from_type_name("StructuredDataType"),
        Some(ObjectKind::StructuredDataType)
    );
    assert_eq!(kind_from_type_name("webpanel"), Some(ObjectKind::WebPanel));
    assert_eq!(kind_from_type_name("Gizmo"), None);
}

// ---- StyleClass design-class token projection -------------------------------------------------
//
// Native storage names a design-system class `StyleClass:<type guid><Name>`; the SDK shows
// `StyleClass:<Name>`. `kb-read` projects the SDK text, while the reader the generators consume keeps
// the stored token. The oracle is an SDK capture, not a transformation of the native text.

const DESIGN_CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";
const NATIVE_GX0010_EVENTS: &str = include_str!("fixtures/prompt/gx0010.events.txt");
const SDK_GX0010_EVENTS: &str = include_str!("fixtures/kb_ops/gx0010.events.sdk.txt");

fn seeded_part(kind: ObjectKind, part: &str, source: &str) -> UdmClient {
    let client = UdmClient::new_in_memory();
    UdmWriter::new(Arc::new(client.clone()))
        .create_object(&object("Subject", kind, &[(part, source)]))
        .expect("create Subject");
    client
}

fn read_part(client: &UdmClient, part: &str) -> String {
    let read = read_object(client, "Subject", Some(&[part.to_string()])).expect("read Subject");
    assert_eq!(read.parts.len(), 1, "{read:?}");
    read.parts[0].source.clone()
}

#[test]
fn read_projects_a_native_events_part_to_the_sdk_text_but_the_reader_keeps_the_token() {
    assert!(NATIVE_GX0010_EVENTS.contains(&format!("StyleClass:{DESIGN_CLASS_TYPE}Advanced")));
    let client = seeded_part(ObjectKind::WebPanel, "EVENTS", NATIVE_GX0010_EVENTS);

    assert_eq!(read_part(&client, "Events"), SDK_GX0010_EVENTS);

    let stored = gx_engine::udm::UdmReader::new(Arc::new(client.clone()))
        .read_object_by_name("Subject")
        .expect("reader")
        .expect("object");
    let events = stored
        .source_parts
        .iter()
        .find(|part| part.name == "EVENTS")
        .expect("EVENTS part");
    assert_eq!(
        events.source, NATIVE_GX0010_EVENTS,
        "generators need the stored token"
    );
}

#[test]
fn read_projection_leaves_literals_and_comments_alone() {
    let token = format!("StyleClass:{DESIGN_CLASS_TYPE}Box");
    let native = format!(
        "Event Start\n\
         \tMsg(\"{token}\")\n\
         \tMsg(!\"a {token} b\" + {token})\n\
         \t// note {token}\n\
         \t/* block\n\
         \t   {token} */\n\
         \tx.Class = {token} // tail {token}\n\
         \t'{token}'\n\
         EndEvent\n"
    );
    let plain = "StyleClass:Box";
    let expected = format!(
        "Event Start\n\
         \tMsg(\"{token}\")\n\
         \tMsg(!\"a {token} b\" + {plain})\n\
         \t// note {token}\n\
         \t/* block\n\
         \t   {token} */\n\
         \tx.Class = {plain} // tail {token}\n\
         \t'{token}'\n\
         EndEvent\n"
    );
    let client = seeded_part(ObjectKind::WebPanel, "EVENTS", &native);
    assert_eq!(read_part(&client, "Events"), expected);
}

#[test]
fn read_projection_preserves_escaped_double_quotes_and_backslashes() {
    let token = format!("StyleClass:{DESIGN_CLASS_TYPE}Box");
    let cases = [
        (
            format!(r#"Msg("literal \"{token}\""); x.Class = {token}"#),
            format!(r#"Msg("literal \"{token}\""); x.Class = StyleClass:Box"#),
        ),
        (
            format!(r#"Msg("backslash \\"); x.Class = {token}"#),
            r#"Msg("backslash \\"); x.Class = StyleClass:Box"#.to_string(),
        ),
        (
            format!(r#"Msg("ação \"{token}\" 🦷"); x.Class = {token}"#),
            format!(r#"Msg("ação \"{token}\" 🦷"); x.Class = StyleClass:Box"#),
        ),
    ];
    for (native, expected) in cases {
        let client = seeded_part(ObjectKind::WebPanel, "RULES", &native);
        assert_eq!(read_part(&client, "Rules"), expected, "native: {native:?}");
    }
}

#[test]
fn read_projection_rewrites_only_the_design_class_token() {
    let other = "StyleClass:11111111-2222-3333-4444-555555555555Box";
    let upper = DESIGN_CLASS_TYPE.to_ascii_uppercase();
    let cases = [
        // another type's guid is not the design-class wrapper
        (other.to_string(), other.to_string()),
        // no class name after the wrapper
        (
            format!("x = StyleClass:{DESIGN_CLASS_TYPE}\n"),
            format!("x = StyleClass:{DESIGN_CLASS_TYPE}\n"),
        ),
        // a longer identifier that merely ends in the keyword
        (
            format!("x = MyStyleClass:{DESIGN_CLASS_TYPE}Box\n"),
            format!("x = MyStyleClass:{DESIGN_CLASS_TYPE}Box\n"),
        ),
        // a guid cut short
        (
            "x = StyleClass:d52f9833-90c9-4096Box\n".to_string(),
            "x = StyleClass:d52f9833-90c9-4096Box\n".to_string(),
        ),
        // guid case does not matter; the keyword and the name keep their text
        (
            format!("x = StyleClass:{upper}Box\n"),
            "x = StyleClass:Box\n".to_string(),
        ),
        // an unterminated string ends with its line; the next line is code again
        (
            format!(
                "x = \"StyleClass:{DESIGN_CLASS_TYPE}Box\ny = StyleClass:{DESIGN_CLASS_TYPE}Box\n"
            ),
            format!("x = \"StyleClass:{DESIGN_CLASS_TYPE}Box\ny = StyleClass:Box\n"),
        ),
        // an unterminated block comment keeps the rest of the part
        (
            format!(
                "/* open StyleClass:{DESIGN_CLASS_TYPE}Box\nStyleClass:{DESIGN_CLASS_TYPE}Box\n"
            ),
            format!(
                "/* open StyleClass:{DESIGN_CLASS_TYPE}Box\nStyleClass:{DESIGN_CLASS_TYPE}Box\n"
            ),
        ),
        // adjacent tokens
        (
            format!("StyleClass:{DESIGN_CLASS_TYPE}A+StyleClass:{DESIGN_CLASS_TYPE}B"),
            "StyleClass:A+StyleClass:B".to_string(),
        ),
    ];
    for (native, expected) in cases {
        let client = seeded_part(ObjectKind::WebPanel, "EVENTS", &native);
        assert_eq!(read_part(&client, "Events"), expected, "native: {native:?}");
    }
}

#[test]
fn read_projection_only_touches_tokenized_source_parts() {
    let token = format!("StyleClass:{DESIGN_CLASS_TYPE}Box");
    let layout = format!("<Table class=\"{token}\"/>");
    let client = seeded_part(ObjectKind::WebPanel, "WEBFORM", &layout);
    assert_eq!(read_part(&client, "WebForm"), layout);

    // A Procedure is only readable with its SOURCE; the other tokenized parts live on a WebPanel.
    for (kind, part) in [
        (ObjectKind::Procedure, "SOURCE"),
        (ObjectKind::WebPanel, "RULES"),
        (ObjectKind::WebPanel, "CONDITIONS"),
    ] {
        let client = seeded_part(kind, part, &format!("x = {token}\n"));
        assert_eq!(read_part(&client, part), "x = StyleClass:Box\n", "{part}");
    }
}

#[test]
fn operations_serve_the_design_model_the_client_targets() {
    // One store, two Design models: the trunk (1) and a branch (4) with an object of its own.
    let trunk = UdmClient::new_in_memory();
    let branch = trunk.clone().with_design_model_id(4);
    UdmWriter::new(Arc::new(trunk.clone()))
        .create_object(&object(
            "PTrunk",
            ObjectKind::Procedure,
            &[("SOURCE", "// trunk")],
        ))
        .expect("create PTrunk");
    UdmWriter::new(Arc::new(branch.clone()))
        .create_object(&object(
            "PBranch",
            ObjectKind::Procedure,
            &[("SOURCE", "// branch")],
        ))
        .expect("create PBranch");

    let names = |client: &UdmClient| -> Vec<String> {
        query_objects(client, None, Some("Procedure"), None, 0)
            .expect("query procedures")
            .objects
            .into_iter()
            .map(|object| object.name)
            .collect()
    };
    assert_eq!(names(&trunk), vec!["PTrunk"]);
    assert_eq!(names(&branch), vec!["PBranch"]);
    assert!(read_object(&trunk, "PBranch", None).is_err());
    let read = read_object(&branch, "PBranch", None).expect("branch read");
    let source = read.parts.iter().find(|part| part.name == "SOURCE");
    assert_eq!(source.map(|part| part.source.as_str()), Some("// branch"));
}

#[test]
fn test_query_objects_wildcard_patterns() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    writer
        .create_object(&object(
            "CustomerList",
            ObjectKind::Procedure,
            &[("SOURCE", "// 1")],
        ))
        .unwrap();
    writer
        .create_object(&object(
            "CustomerDetail",
            ObjectKind::Procedure,
            &[("SOURCE", "// 2")],
        ))
        .unwrap();
    writer
        .create_object(&object(
            "VendorList",
            ObjectKind::Procedure,
            &[("SOURCE", "// 3")],
        ))
        .unwrap();

    // Prefix wildcard query
    let res_prefix = query_objects(&client, Some("Customer*"), None, None, 0).unwrap();
    assert_eq!(res_prefix.total, 2);
    let names: Vec<_> = res_prefix.objects.into_iter().map(|o| o.name).collect();
    assert!(names.contains(&"CustomerList".to_string()));
    assert!(names.contains(&"CustomerDetail".to_string()));

    // Suffix wildcard query
    let res_suffix = query_objects(&client, Some("*List"), None, None, 0).unwrap();
    assert_eq!(res_suffix.total, 2);
    let names_suffix: Vec<_> = res_suffix.objects.into_iter().map(|o| o.name).collect();
    assert!(names_suffix.contains(&"CustomerList".to_string()));
    assert!(names_suffix.contains(&"VendorList".to_string()));

    // Substring wildcard query
    let res_sub = query_objects(&client, Some("*omer*"), None, None, 0).unwrap();
    assert_eq!(res_sub.total, 2);

    // Exact query remains exact
    let res_exact = query_objects(&client, Some("CustomerList"), None, None, 0).unwrap();
    assert_eq!(res_exact.total, 1);
    assert_eq!(res_exact.objects[0].name, "CustomerList");
}

#[test]
fn read_object_serves_variables_and_structure_parts() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));

    let vars_xml = r#"<Variables>
        <Variable Name="Today">
            <Properties>
                <Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property>
                <Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;6&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property>
                <Property><Name>Length</Name><Value>8</Value></Property>
            </Properties>
        </Variable>
    </Variables>"#;

    writer
        .create_object(&object(
            "ProcWithVars",
            ObjectKind::Procedure,
            &[("SOURCE", "// proc body"), ("VARIABLES", vars_xml)],
        ))
        .unwrap();

    let read_vars = read_object(&client, "ProcWithVars", Some(&["VARIABLES".to_string()])).unwrap();
    assert_eq!(read_vars.parts.len(), 1);
    assert_eq!(read_vars.parts[0].name, "VARIABLES");
    assert!(read_vars.parts[0].source.contains("Today"));
}

#[test]
fn read_object_serves_pattern_instance_and_webform() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));

    let pattern_xml = r#"<PatternInstance Version="1.0" Pattern="WorkWithPlus">
        <Instance Root="True"/>
    </PatternInstance>"#;
    let webform_xml = r#"<WebForm Layout="Responsive">
        <Table Name="MainTable"/>
    </WebForm>"#;

    writer
        .create_object(&object(
            "WpWithPattern",
            ObjectKind::WebPanel,
            &[
                ("EVENTS", "Event Start\nEndEvent"),
                ("PATTERNINSTANCE", pattern_xml),
                ("WEBFORM", webform_xml),
            ],
        ))
        .unwrap();

    let read_pattern = read_object(
        &client,
        "WpWithPattern",
        Some(&["PATTERNINSTANCE".to_string()]),
    )
    .unwrap();
    assert_eq!(read_pattern.parts.len(), 1);
    assert_eq!(read_pattern.parts[0].name, "PATTERNINSTANCE");
    assert!(read_pattern.parts[0].source.contains("WorkWithPlus"));

    let read_form = read_object(&client, "WpWithPattern", Some(&["WEBFORM".to_string()])).unwrap();
    assert_eq!(read_form.parts.len(), 1);
    assert_eq!(read_form.parts[0].name, "WEBFORM");
    assert!(read_form.parts[0].source.contains("MainTable"));
}

#[test]
fn write_replaces_source_on_procedure_with_xml_variables_preserving_variable_ids_and_xml() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let variables_xml = r#"<Variables><Variable Name="CustomerId" Id="8"><Properties><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>10</Value></Property></Properties></Variable></Variables>"#;

    writer
        .create_object(&object(
            "PWithXmlVars",
            ObjectKind::Procedure,
            &[("SOURCE", "// initial"), ("VARIABLES", variables_xml)],
        ))
        .unwrap();

    let result = write_object_part(&client, "PWithXmlVars", "Source", "// updated", None, None)
        .expect("write_object_part must succeed on Procedure with XML variables");
    assert_eq!(result.version_id, 2);

    let raw_after = writer
        .load_raw_object("PWithXmlVars")
        .unwrap()
        .expect("raw object after write");
    let raw_vars = raw_after
        .source_parts
        .iter()
        .find(|p| p.name == "VARIABLES")
        .expect("raw VARIABLES part");
    assert_eq!(
        raw_vars.source, variables_xml,
        "raw XML VARIABLES blob must not be overwritten with compiler JSON"
    );

    let reader = gx_engine::udm::UdmReader::new(Arc::new(client));
    let hydrated = reader
        .read_object_by_name("PWithXmlVars")
        .unwrap()
        .expect("hydrated object after write");
    let var_ids = hydrated
        .source_parts
        .iter()
        .find(|p| p.name == "VARIABLE_IDS")
        .expect("synthetic VARIABLE_IDS part must still be derived from preserved XML");
    assert!(var_ids.source.contains("\"CustomerId\":8"));
}
