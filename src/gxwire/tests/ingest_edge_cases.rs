//! Three edge cases that let `genexus_wire` report a confident wrong answer.
//!
//! Each test below reproduces a case that was measured against a real
//! installation before the fix, because all three fail in the same direction:
//! the graph looks populated and is wrong. That is the dangerous shape for a
//! tool whose `safe_delete` and `impact` actions reason over it.

use gxwire::cache::{build_graph_from_directory, load_cache};
use std::fs;
use std::path::{Path, PathBuf};

fn scratch(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("gxwire-edge-{}-{}", label, std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).unwrap();
    dir
}

fn write_source(dir: &Path, name: &str, body: &str) {
    fs::write(dir.join(name), body).unwrap();
}

/// Names of the nodes reachable from the graph.
fn node_names(dir: &Path) -> Vec<String> {
    let graph = build_graph_from_directory(dir).unwrap();
    graph.nodes.iter().map(|n| n.name.clone()).collect()
}

// ---------------------------------------------------------------------------
// 1. Same-millisecond, same-length edit served a stale graph
// ---------------------------------------------------------------------------

/// The cache signature must notice an edit that preserves both the length and
/// the millisecond.
///
/// Measured before the fix: `stat_signature` truncated the timestamp with
/// `as_millis()`, so two writes inside the same millisecond produced an
/// identical signature whenever the length matched, and the cache served the
/// pre-edit graph. `Alpha` and `Bravo` are the same byte length, and the edit
/// lands 1 microsecond later - a different tick, the same millisecond.
///
/// Deliberately *not* the case where the timestamp is restored to the exact
/// previous value. No timestamp scheme can see that, and pretending otherwise
/// would make this guard assert something untrue.
#[test]
fn same_length_edit_within_the_same_millisecond_invalidates_the_cache() {
    let dir = scratch("freshness");
    let file = dir.join("Caller.txt");
    write_source(&dir, "Caller.txt", "// obj\nCall(Alpha);\n");

    let before = load_cache(&dir).unwrap();
    assert_eq!(
        before.nodes.len(),
        2,
        "the cold graph must have the call and its target"
    );

    // Move the stamp into the same millisecond the cache recorded, but to a
    // different tick inside it. This is what two writes a microsecond apart
    // actually look like on NTFS.
    let recorded = recorded_mtime_ticks(&dir);
    let same_millisecond = (recorded / 1_000_000) * 1_000_000 + 1_000;
    assert_eq!(
        same_millisecond / 1_000_000,
        recorded / 1_000_000,
        "must stay in one millisecond"
    );
    assert_ne!(same_millisecond, recorded, "must differ at tick resolution");

    write_source(&dir, "Caller.txt", "// obj\nCall(Bravo);\n");
    restore_modified_time(&file, same_millisecond);

    let after = load_cache(&dir).unwrap();
    let names: Vec<&str> = after.nodes.iter().map(|n| n.name.as_str()).collect();

    assert!(
        !names.contains(&"Alpha"),
        "the removed symbol must be gone: a stale graph here tells safe_delete \
         an object is unreachable when it is still referenced"
    );
    assert!(
        names.contains(&"Bravo"),
        "the new symbol must be present: the file on disk calls it"
    );
}

/// A file the filesystem cannot stamp differently still has to be caught by the
/// size check, or a real edit could be missed.
#[test]
fn same_timestamp_different_length_invalidates_the_cache() {
    let dir = scratch("freshness-len");
    let file = dir.join("Caller.txt");
    write_source(&dir, "Caller.txt", "// obj\nCall(Alpha);\n");

    load_cache(&dir).unwrap();

    write_source(
        &dir,
        "Caller.txt",
        "// obj\nCall(AlphaWithAVeryMuchLongerName);\n",
    );
    restore_modified_time(&file, recorded_mtime_ticks(&dir));

    let names = node_names(&dir);
    assert!(names.iter().any(|n| n == "AlphaWithAVeryMuchLongerName"));
}

/// The mtime currently on disk for the directory's source file, at tick
/// resolution.
fn recorded_mtime_ticks(dir: &Path) -> u64 {
    let entry = fs::read_dir(dir)
        .unwrap()
        .map(|e| e.unwrap().path())
        .find(|p| p.extension().and_then(|e| e.to_str()) == Some("txt"))
        .expect("a source file");
    entry
        .metadata()
        .unwrap()
        .modified()
        .unwrap()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_nanos() as u64
}

fn restore_modified_time(path: &Path, ticks: u64) {
    let target = std::time::UNIX_EPOCH + std::time::Duration::from_nanos(ticks);
    let file = fs::File::options().write(true).open(path).unwrap();
    file.set_modified(target).unwrap();
}

// ---------------------------------------------------------------------------
// 2. A file that is not valid UTF-8 lost every reference it had
// ---------------------------------------------------------------------------

/// Windows-1252 source text must keep its call edges.
///
/// Measured before the fix: a file whose description contained a Latin-1
/// `ç`/`ã` became a node with `refs: 0` - present in the graph, so nothing
/// looked wrong, with every call in it gone. `read_to_string` had rejected the
/// whole file and the object had fallen back to an empty parse.
#[test]
fn non_utf8_source_keeps_its_references() {
    let dir = scratch("encoding");

    // `// Descrição` in Windows-1252: 0xE7 and 0xE3 are invalid on their own in
    // UTF-8. The `Call(ProcDep)` on line 3 is plain ASCII and must survive.
    let bytes: Vec<u8> = b"// obj\r\n// Descri\xe7\xe3o\r\nCall(ProcDep);\r\n".to_vec();
    fs::write(dir.join("ProcLatin1.txt"), &bytes).unwrap();

    let names = node_names(&dir);

    assert!(
        names.iter().any(|n| n == "ProcDep"),
        "the call in a Windows-1252 file must be indexed: found {names:?}"
    );
    assert!(names.iter().any(|n| n == "ProcLatin1"));
}

#[test]
fn utf8_source_is_unaffected() {
    let dir = scratch("encoding-utf8");
    write_source(&dir, "ProcUtf8.txt", "// Descrição\nCall(ProcDep);\n");

    let names = node_names(&dir);

    assert!(names.iter().any(|n| n == "ProcDep"));
}

// ---------------------------------------------------------------------------
// 3. GeneXus metadata at the Knowledge Base root was indexed as objects
// ---------------------------------------------------------------------------

/// `.xml` is both a GeneXus object export and where the IDE keeps its own
/// bookkeeping, and the two are indistinguishable by extension or file name.
///
/// Measured before the fix: `C:\KBs\KBTeste` indexed to 30 nodes built from
/// `CategoryViewConfig`, `iar_objs` and `nav_objs`, with `CategoryViewConfig`
/// reported as `kind: "Proc"` and `iar_objs` as `kind: "Tbl"`.
#[test]
fn knowledge_base_metadata_at_the_root_is_not_indexed() {
    let dir = scratch("metadata");
    for name in ["CategoryViewConfig.xml", "iar_objs.xml", "nav_objs.xml"] {
        write_source(&dir, name, "<root/>");
    }
    write_source(&dir, "RealProc.txt", "// obj\nCall(Other);\n");

    let names = node_names(&dir);

    for metadata in ["CategoryViewConfig", "iar_objs", "nav_objs"] {
        assert!(
            !names.iter().any(|n| n == metadata),
            "{metadata} is IDE bookkeeping, not an object: found {names:?}"
        );
    }
    assert!(
        names.iter().any(|n| n == "RealProc"),
        "a real object must still index"
    );
    assert!(names.iter().any(|n| n == "Other"));
}

#[test]
fn metadata_exclusion_is_case_insensitive() {
    let dir = scratch("metadata-case");
    write_source(&dir, "categoryviewconfig.xml", "<root/>");

    assert!(node_names(&dir).is_empty());
}

/// The metadata only ever sits at the analysed root, so a nested file that
/// happens to share the name is still an object and must be indexed.
#[test]
fn a_nested_file_with_a_metadata_name_is_still_indexed() {
    let dir = scratch("metadata-nested");
    let nested = dir.join("objects");
    fs::create_dir_all(&nested).unwrap();
    write_source(&nested, "CategoryViewConfig.xml", "// obj\nCall(Other);\n");

    let names = node_names(&dir);

    assert!(
        names.iter().any(|n| n == "CategoryViewConfig"),
        "only the root is excluded; found {names:?}"
    );
}

/// An exported object is XML too, and this is the case the exclusion must not
/// cost.
#[test]
fn exported_object_xml_is_still_indexed() {
    let dir = scratch("metadata-export");
    write_source(&dir, "MyProcedure.xml", "<Procedure/>\nCall(Other);\n");

    let names = node_names(&dir);

    assert!(names.iter().any(|n| n == "MyProcedure"));
    assert!(names.iter().any(|n| n == "Other"));
}

#[test]
fn xml_export_object_type_guid_detection() {
    use gxwire::model::EntityKind;
    let dir = scratch("xml-guid");
    let proc_xml = r#"<Object parentGuid="c88fffcd-b6f8-0000-8fec-00b5497e2117" user="admin" versionDate="2026-10-14" versionId="1" name="procCustomExport" description="" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
    <Part type="c414ed00-8cc4-4f44-8820-4baf93547173"><Source><![CDATA[Call(Other);]]></Source></Part>
</Object>"#;
    let trn_xml = r#"<Object parentGuid="c88fffcd-b6f8-0000-8fec-00b5497e2117" user="admin" name="CustomTrn" type="1db606f2-af09-4cf9-a3b5-b481519d28f6">
</Object>"#;
    let wp_xml = r#"<Object parentGuid="c88fffcd-b6f8-0000-8fec-00b5497e2117" user="admin" name="CustomPanel" type="c9584656-94b6-4ccd-890f-332d11fc2c25">
</Object>"#;

    write_source(&dir, "procCustomExport.xml", proc_xml);
    write_source(&dir, "CustomTrn.xml", trn_xml);
    write_source(&dir, "CustomPanel.xml", wp_xml);

    let graph = build_graph_from_directory(&dir).unwrap();
    let proc_node = graph.get_node_by_name("procCustomExport").unwrap();
    assert_eq!(proc_node.kind, EntityKind::Procedure);

    let trn_node = graph.get_node_by_name("CustomTrn").unwrap();
    assert_eq!(trn_node.kind, EntityKind::Transaction);

    let wp_node = graph.get_node_by_name("CustomPanel").unwrap();
    assert_eq!(wp_node.kind, EntityKind::WebPanel);
}
