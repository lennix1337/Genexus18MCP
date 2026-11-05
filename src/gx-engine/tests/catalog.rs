use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

fn id(value: &str) -> ObjectId {
    ObjectId::new(value).expect("valid test identity")
}

fn object(identity: &str, name: &str, kind: ObjectKind) -> KbObject {
    KbObject::new(id(identity), name, kind)
}

fn temp_dir() -> PathBuf {
    let path = std::env::temp_dir().join(format!(
        "gx-engine-catalog-test-{}-{}",
        std::process::id(),
        TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir(&path).expect("create test directory");
    path
}

#[test]
fn rejects_malformed_and_duplicate_identities_or_names() {
    assert!(ObjectId::new("bad/id").is_err());
    assert!(
        Catalog::from_json("{")
            .unwrap_err()
            .contains("invalid catalog JSON")
    );

    let duplicate_ids = r#"{"format_version":1,"revision":0,"objects":[
      {"id":"same","name":"FIRST","kind":"procedure"},
      {"id":"SAME","name":"SECOND","kind":"transaction"}] }"#;
    assert!(
        Catalog::from_json(duplicate_ids)
            .unwrap_err()
            .contains("duplicate object identity")
    );

    let duplicate_names = r#"{"format_version":1,"revision":0,"objects":[
      {"id":"one","name":"Thing","kind":"procedure"},
      {"id":"two","name":"THING","kind":"transaction"}] }"#;
    assert!(
        Catalog::from_json(duplicate_names)
            .unwrap_err()
            .contains("duplicate object name")
    );

    let unknown_field = r#"{"format_version":1,"revision":0,"objects":[],"native_path":"ignored"}"#;
    assert!(
        Catalog::from_json(unknown_field)
            .unwrap_err()
            .contains("unknown field")
    );
}

#[test]
fn rename_keeps_identity_and_references_and_orders_dependencies_first() {
    let mut caller = object("caller", "Caller", ObjectKind::Procedure);
    caller.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("target"),
    });
    let target = object("target", "Target", ObjectKind::Procedure);
    let mut catalog = Catalog::empty();
    assert_eq!(
        catalog.apply(0, &[Edit::Upsert(caller), Edit::Upsert(target)]),
        Ok(1)
    );

    assert_eq!(
        catalog.apply(
            1,
            &[Edit::Rename {
                id: id("TARGET"),
                name: "RenamedTarget".into(),
            }]
        ),
        Ok(2)
    );
    let renamed = catalog.get(&id("target")).expect("same stable identity");
    // The name keeps the case it was given: names are stored as-is, and the emitter uses them in C#.
    assert_eq!(renamed.name, "RenamedTarget");
    assert_eq!(renamed.id, id("target"));
    assert_eq!(
        catalog.get(&id("caller")).unwrap().references[0].target,
        id("target")
    );
    assert_eq!(
        catalog.dependency_order().unwrap(),
        [id("target"), id("caller")]
    );
}

#[test]
fn transaction_rolls_back_on_stale_revision_invalid_names_and_dangling_references() {
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[Edit::Upsert(object("one", "One", ObjectKind::Procedure))],
        )
        .unwrap();
    let original = catalog.to_json().unwrap();

    assert!(
        catalog
            .apply(
                0,
                &[Edit::Rename {
                    id: id("one"),
                    name: "Renamed".into(),
                }]
            )
            .unwrap_err()
            .contains("stale catalog revision")
    );
    assert_eq!(catalog.to_json().unwrap(), original);

    assert!(
        catalog
            .apply(
                1,
                &[Edit::Rename {
                    id: id("one"),
                    name: "not a GeneXus identifier".into(),
                }]
            )
            .is_err()
    );
    assert_eq!(catalog.to_json().unwrap(), original);

    assert!(catalog.apply(1, &[Edit::Remove { id: id("one") }]).is_ok());
}

#[test]
fn dangling_reference_and_inbound_delete_are_rejected_without_partial_commit() {
    let mut catalog = Catalog::empty();
    let mut caller = object("caller", "Caller", ObjectKind::Procedure);
    caller.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("missing"),
    });
    assert!(catalog.apply(0, &[Edit::Upsert(caller)]).is_err());
    assert_eq!(catalog.revision(), 0);
    assert_eq!(catalog.objects().count(), 0);

    let mut caller = object("caller", "Caller", ObjectKind::Procedure);
    caller.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("target"),
    });
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(caller),
                Edit::Upsert(object("target", "Target", ObjectKind::Procedure)),
            ],
        )
        .unwrap();
    let before = catalog.to_json().unwrap();
    assert!(
        catalog
            .apply(1, &[Edit::Remove { id: id("target") }])
            .is_err()
    );
    assert_eq!(catalog.to_json().unwrap(), before);

    catalog
        .apply(
            1,
            &[Edit::SetReferences {
                id: id("caller"),
                references: Vec::new(),
            }],
        )
        .unwrap();
    catalog
        .apply(2, &[Edit::Remove { id: id("target") }])
        .unwrap();
    assert!(catalog.get(&id("target")).is_none());
}

#[test]
fn cycles_are_preserved_but_dependency_order_reports_blocked_objects() {
    let mut left = object("left", "Left", ObjectKind::Procedure);
    left.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("right"),
    });
    let mut right = object("right", "Right", ObjectKind::Procedure);
    right.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("left"),
    });
    let mut downstream = object("downstream", "Downstream", ObjectKind::Procedure);
    downstream.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("left"),
    });

    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(downstream),
                Edit::Upsert(right),
                Edit::Upsert(left),
            ],
        )
        .unwrap();
    assert_eq!(catalog.objects().count(), 3);
    assert_eq!(
        catalog.dependency_order().unwrap_err(),
        [id("downstream"), id("left"), id("right")]
    );
}

#[test]
fn topological_dependency_order_resolves_linear_dag() {
    let proc1 = object("p1", "Proc1", ObjectKind::Procedure);
    let mut proc2 = object("p2", "Proc2", ObjectKind::Procedure);
    proc2.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("p1"),
    });

    let mut catalog = Catalog::empty();
    catalog
        .apply(0, &[Edit::Upsert(proc2), Edit::Upsert(proc1)])
        .unwrap();

    let order = catalog.dependency_order().expect("must order linear DAG");
    assert_eq!(order, [id("p1"), id("p2")]);
}

#[test]
fn source_parts_and_references_serialize_deterministically() {
    let mut a = object("a", "Alpha", ObjectKind::StructuredDataType);
    a.source_parts = vec![
        SourcePart {
            name: "Rules".into(),
            source: "Rule 2".into(),
        },
        SourcePart {
            name: "Source".into(),
            source: "Rule 1".into(),
        },
    ];
    a.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("b"),
    });
    let b = object("b", "Beta", ObjectKind::Domain);

    let mut first = Catalog::empty();
    first
        .apply(0, &[Edit::Upsert(a.clone()), Edit::Upsert(b.clone())])
        .unwrap();
    let mut second = Catalog::empty();
    let mut reversed_a = a;
    reversed_a.source_parts.reverse();
    second
        .apply(0, &[Edit::Upsert(b), Edit::Upsert(reversed_a)])
        .unwrap();

    let json = first.to_json().unwrap();
    assert_eq!(json, second.to_json().unwrap());
    let loaded = Catalog::from_json(&json).unwrap();
    assert_eq!(loaded.to_json().unwrap(), json);
}

#[test]
fn save_and_load_round_trip_and_replace_an_existing_snapshot() {
    let dir = temp_dir();
    let path = dir.join("portable-kb.json");
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[Edit::Upsert(object("one", "One", ObjectKind::Procedure))],
        )
        .unwrap();
    catalog.save_atomic(&path).unwrap();
    let first_snapshot = fs::read(&path).unwrap();
    assert_eq!(
        Catalog::load(&path).unwrap().to_json().unwrap(),
        catalog.to_json().unwrap()
    );

    catalog
        .apply(
            1,
            &[Edit::SetSourcePart {
                id: id("one"),
                name: "source".into(),
                source: Some("Print(1)".into()),
            }],
        )
        .unwrap();
    catalog.save_atomic(&path).unwrap();
    assert_ne!(fs::read(&path).unwrap(), first_snapshot);
    assert_eq!(
        Catalog::load(&path).unwrap().to_json().unwrap(),
        catalog.to_json().unwrap()
    );
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn rejects_duplicate_parts_references_and_oversize_json() {
    let mut duplicate_parts = object("one", "One", ObjectKind::Procedure);
    duplicate_parts.source_parts = vec![
        SourcePart {
            name: "source".into(),
            source: "a".into(),
        },
        SourcePart {
            name: "SOURCE".into(),
            source: "b".into(),
        },
    ];
    let mut catalog = Catalog::empty();
    assert!(catalog.apply(0, &[Edit::Upsert(duplicate_parts)]).is_err());
    assert_eq!(catalog.revision(), 0);

    let huge = format!(
        "{{\"format_version\":1,\"revision\":0,\"objects\":[]}}{}",
        " ".repeat(8 * 1024 * 1024)
    );
    assert!(
        Catalog::from_json(&huge)
            .unwrap_err()
            .contains("exceeds the")
    );
}

#[test]
fn object_names_keep_their_stored_case_through_a_snapshot() {
    // Regression: `normalize_name` upper-cased names on the snapshot path (`AddDeviceGroups` became
    // `ADDDEVICEGROUPS`), while the KB path keeps the stored case. The emitter writes names into C# class
    // names, so a snapshot-loaded catalog emitted different classes. The round trip must keep the case.
    let mut objects = BTreeMap::new();
    objects.insert(
        id("procedure-1"),
        object("procedure-1", "AddDeviceGroups", ObjectKind::Procedure),
    );
    let catalog = Catalog::from_objects_map(objects);
    let json = catalog.to_json().unwrap();
    let loaded = Catalog::from_json(&json).unwrap();
    assert_eq!(
        loaded.get(&id("procedure-1")).unwrap().name,
        "AddDeviceGroups"
    );
    assert_eq!(loaded.to_json().unwrap(), json);
}

#[test]
fn object_names_stay_unique_regardless_of_case() {
    // Validation changed to keep case; uniqueness must still be case-insensitive.
    let mut objects = BTreeMap::new();
    objects.insert(
        id("procedure-1"),
        object("procedure-1", "AddDeviceGroups", ObjectKind::Procedure),
    );
    objects.insert(
        id("procedure-2"),
        object("procedure-2", "ADDDEVICEGROUPS", ObjectKind::Procedure),
    );
    let error = Catalog::from_objects_map(objects).to_json().unwrap_err();
    assert!(error.contains("duplicate object name"), "{error}");
}

/// A procedure whose parts and references are out of canonical order, as the KB reader can produce them.
fn out_of_order_catalog() -> Catalog {
    let mut procedure = KbObject::new(id("main-1"), "Main", ObjectKind::Procedure);
    procedure.source_parts = vec![
        SourcePart {
            name: "VARIABLES".into(),
            source: "{}".into(),
        },
        SourcePart {
            name: "SOURCE".into(),
            source: "Print(1)".into(),
        },
        SourcePart {
            name: "RULES".into(),
            source: String::new(),
        },
    ];
    procedure.references = vec![
        Reference {
            kind: ReferenceKind::Calls,
            target: id("zeta-2"),
        },
        Reference {
            kind: ReferenceKind::Uses,
            target: id("alpha-3"),
        },
        Reference {
            kind: ReferenceKind::Calls,
            target: id("alpha-2"),
        },
    ];
    // The JSON path validates references, so every target exists.
    let mut objects = BTreeMap::new();
    for target in [
        KbObject::new(id("zeta-2"), "Zeta2", ObjectKind::Procedure),
        KbObject::new(id("alpha-2"), "Alpha2", ObjectKind::Procedure),
        KbObject::new(id("alpha-3"), "Alpha3", ObjectKind::StructuredDataType),
    ] {
        objects.insert(target.id.clone(), target);
    }
    objects.insert(procedure.id.clone(), procedure);
    Catalog::from_objects_map(objects)
}

#[test]
fn a_snapshot_round_trip_is_the_catalog_that_was_saved_order_included() {
    let dir = temp_dir();
    let path = dir.join("snapshot.catalog.json");
    let catalog = out_of_order_catalog();

    catalog.save_snapshot_atomic(&path).expect("save snapshot");
    let loaded = Catalog::load_snapshot(&path).expect("load snapshot");

    assert_eq!(
        loaded, catalog,
        "a snapshot must reproduce the live catalog exactly"
    );
    let main = loaded.get(&id("main-1")).expect("main procedure");
    let parts: Vec<&str> = main.source_parts.iter().map(|p| p.name.as_str()).collect();
    assert_eq!(parts, ["VARIABLES", "SOURCE", "RULES"]);
    let targets: Vec<&str> = main.references.iter().map(|r| r.target.as_str()).collect();
    assert_eq!(targets, ["zeta-2", "alpha-3", "alpha-2"]);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn the_json_path_still_sorts_while_the_snapshot_path_keeps_order() {
    // Contrast guard: the portable JSON contract is canonical (sorted), the snapshot is the reader's order.
    let catalog = out_of_order_catalog();

    let sorted = Catalog::from_json(&catalog.to_json().unwrap()).unwrap();
    let main = sorted.get(&id("main-1")).unwrap();
    let parts: Vec<&str> = main.source_parts.iter().map(|p| p.name.as_str()).collect();
    assert_eq!(parts, ["RULES", "SOURCE", "VARIABLES"]);

    let dir = temp_dir();
    let path = dir.join("snapshot.catalog.json");
    catalog.save_snapshot_atomic(&path).unwrap();
    assert_eq!(Catalog::load_snapshot(&path).unwrap(), catalog);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn a_snapshot_with_a_repeated_identity_is_rejected() {
    let dir = temp_dir();
    let path = dir.join("snapshot.catalog.json");
    let object = r#"{"id":"same-1","name":"Main","kind":"procedure"}"#;
    fs::write(
        &path,
        format!(r#"{{"format_version":1,"revision":1,"objects":[{object},{object}]}}"#),
    )
    .unwrap();

    let error = Catalog::load_snapshot(&path).unwrap_err();
    assert!(
        error.contains("duplicate object identity 'same-1'"),
        "{error}"
    );
    let _ = fs::remove_dir_all(&dir);
}
