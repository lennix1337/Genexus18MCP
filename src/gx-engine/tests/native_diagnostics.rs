//! `kb-diagnose` over an in-memory store: what was saved is what is diagnosed.

#![forbid(unsafe_code)]

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::kb_ops::read_object;
use gx_engine::native_diagnostics::{Status, diagnose_read};
use gx_engine::udm::{UdmClient, UdmWriter};
use std::sync::Arc;

fn save(client: &UdmClient, name: &str, kind: ObjectKind, part: &str, source: &str) {
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, kind);
    object.source_parts.push(SourcePart {
        name: part.into(),
        source: source.into(),
    });
    UdmWriter::new(Arc::new(client.clone()))
        .create_object(&object)
        .expect("create object");
}

fn diagnose(client: &UdmClient, name: &str) -> gx_engine::native_diagnostics::DiagnoseResult {
    let read = read_object(client, name, Some(&["Source".to_string()])).expect("read object");
    diagnose_read(&read)
}

#[test]
fn a_saved_procedure_is_diagnosed_from_storage() {
    let client = UdmClient::new_in_memory();
    save(
        &client,
        "PGood",
        ObjectKind::Procedure,
        "SOURCE",
        "&x = 1\nReturn &x",
    );
    save(
        &client,
        "PBad",
        ObjectKind::Procedure,
        "SOURCE",
        "&x = 1\n&y = (2 +\n",
    );

    assert_eq!(diagnose(&client, "PGood").status, Status::Ok);
    let bad = diagnose(&client, "pbad");
    assert_eq!(bad.status, Status::Errors);
    assert_eq!(bad.object_type, "Procedure");
    assert!(bad.errors[0].line.is_some_and(|line| line >= 2));
}

#[test]
fn a_non_procedure_is_unavailable_not_an_error() {
    let client = UdmClient::new_in_memory();
    save(
        &client,
        "TrnHome",
        ObjectKind::Transaction,
        "RULES",
        "parm(",
    );
    let result = diagnose(&client, "TrnHome");
    assert_eq!(result.status, Status::Unavailable);
    assert!(result.errors.is_empty());
}
