//! Correctness guards for the incremental reindex.
//!
//! The performance claim lives in `incremental_reindex_bench.rs`. What must
//! never regress is that reusing cached parse output produces *the same graph*
//! as parsing everything from scratch. Each test drives a mutation through
//! `load_cache` and compares the result against a cold build of a pristine copy
//! of the same directory.

use gxwire::cache::{build_index, get_cache_path, load_cache, save_index};
use gxwire::graph::CsrGraph;
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

fn write_object(dir: &Path, name: &str, body: &str) -> PathBuf {
    fs::create_dir_all(dir).unwrap();
    let path = dir.join(format!("{}.txt", name));
    fs::write(&path, body).unwrap();
    path
}

/// Structural fingerprint of a graph: node identity plus the full edge multiset.
///
/// Compares by node *name* rather than by index because index assignment depends
/// on directory-walk order, and the two directories are populated independently.
fn fingerprint(graph: &CsrGraph) -> (BTreeMap<String, String>, BTreeMap<String, usize>) {
    let mut nodes = BTreeMap::new();
    for node in &graph.nodes {
        nodes.insert(
            node.name.clone(),
            format!(
                "{:?}|{}|{}|{:?}",
                node.kind,
                node.lines,
                node.complexity,
                node.path.as_deref().map(|p| {
                    Path::new(p)
                        .file_name()
                        .map(|n| n.to_string_lossy().to_string())
                        .unwrap_or_default()
                })
            ),
        );
    }

    let name_of = |id: u32| -> String {
        graph
            .get_node(id)
            .map(|n| n.name.clone())
            .unwrap_or_else(|| format!("<missing:{}>", id))
    };

    let mut edges = BTreeMap::new();
    for id in 0..graph.nodes.len() as u32 {
        for (to, kind, line) in graph.get_callers(id) {
            let key = format!(
                "{}->{}|{}|{}",
                name_of(id),
                name_of(to),
                kind.as_str(),
                line
            );
            *edges.entry(key).or_insert(0usize) += 1;
        }
    }

    (nodes, edges)
}

fn assert_same_graph(label: &str, incremental: &CsrGraph, full: &CsrGraph) {
    let (inc_nodes, inc_edges) = fingerprint(incremental);
    let (full_nodes, full_edges) = fingerprint(full);

    assert_eq!(
        inc_nodes.len(),
        full_nodes.len(),
        "{}: node count differs",
        label
    );
    for (name, signature) in &full_nodes {
        assert_eq!(
            inc_nodes.get(name),
            Some(signature),
            "{}: node '{}' differs",
            label,
            name
        );
    }

    assert_eq!(
        inc_edges.len(),
        full_edges.len(),
        "{}: distinct edge count differs",
        label
    );
    for (key, count) in &full_edges {
        assert_eq!(
            inc_edges.get(key),
            Some(count),
            "{}: edge '{}' differs",
            label,
            key
        );
    }
}

/// Copies `src` into a fresh directory so a cold build can be compared against
/// the incrementally-maintained one.
fn clone_dir(src: &Path, tag: &str) -> PathBuf {
    let dst = src.with_file_name(format!(
        "{}_{}",
        src.file_name().unwrap().to_string_lossy(),
        tag
    ));
    let _ = fs::remove_dir_all(&dst);
    fs::create_dir_all(&dst).unwrap();
    for entry in fs::read_dir(src).unwrap() {
        let entry = entry.unwrap();
        if entry.path().is_file() {
            fs::copy(entry.path(), dst.join(entry.file_name())).unwrap();
        }
    }
    dst
}

fn seed(dir: &Path) {
    write_object(dir, "ProcAlpha", "Call(ProcBeta)\nAttr001 = &X\n");
    write_object(dir, "ProcBeta", "Call(ProcGamma)\n&X = Attr002\n");
    write_object(dir, "ProcGamma", "// leaf\n");
    write_object(dir, "ProcDelta", "Call(&Dynamic)\nForEach\nEnd\n");
}

/// Drops the file-level index so the next `load_cache` is forced to reuse
/// nothing, isolating the graph-shape comparison from cache bookkeeping.
fn cold_build(dir: &Path) -> CsrGraph {
    let _ = fs::remove_file(get_cache_path(dir));
    build_index(dir).unwrap().0
}

fn scenario_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("gxwire_incr_{}_{}", std::process::id(), name));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).unwrap();
    dir
}

#[test]
fn incremental_edit_matches_full_rebuild() {
    let dir = scenario_dir("edit");
    seed(&dir);

    let (initial, parses) = build_index(&dir).unwrap();
    save_index(&initial, &parses, &dir).unwrap();

    // Warm load must be a no-op rebuild.
    assert_same_graph(
        "warm",
        &load_cache(&dir).unwrap(),
        &cold_build(&clone_dir(&dir, "edit_ref")),
    );

    // Edit one object: new call target, a dropped reference, a size change.
    std::thread::sleep(std::time::Duration::from_millis(20));
    write_object(&dir, "ProcBeta", "Call(ProcAlpha)\n&X = Attr777\n");
    assert_same_graph(
        "after edit",
        &load_cache(&dir).unwrap(),
        &cold_build(&clone_dir(&dir, "edit_ref")),
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn incremental_added_and_deleted_files_match_full_rebuild() {
    let dir = scenario_dir("adddel");
    seed(&dir);

    let (initial, parses) = build_index(&dir).unwrap();
    save_index(&initial, &parses, &dir).unwrap();
    let _ = load_cache(&dir).unwrap();

    // Add an object that is also called by an existing one.
    write_object(&dir, "ProcEpsilon", "Call(ProcAlpha)\nAttr900 = &Y\n");
    std::thread::sleep(std::time::Duration::from_millis(20));
    write_object(&dir, "ProcDelta", "Call(ProcEpsilon)\n");
    assert_same_graph(
        "after add",
        &load_cache(&dir).unwrap(),
        &cold_build(&clone_dir(&dir, "adddel_ref")),
    );

    // Delete the object again; its node and its edges must disappear together.
    fs::remove_file(dir.join("ProcEpsilon.txt")).unwrap();
    assert_same_graph(
        "after delete",
        &load_cache(&dir).unwrap(),
        &cold_build(&clone_dir(&dir, "adddel_ref")),
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn size_change_alone_invalidates_a_reused_entry() {
    let dir = scenario_dir("resize");
    seed(&dir);
    let (initial, parses) = build_index(&dir).unwrap();
    save_index(&initial, &parses, &dir).unwrap();

    // Two same-length sources that parse differently. Reuse keys on mtime and
    // size, so only the content decides whether the new reference shows up.
    let target = dir.join("ProcGamma.txt");
    write_object(&dir, "ProcGamma", "// Call(ZZZUnknown)\n");
    let commented = fs::read_to_string(&target).unwrap();

    std::thread::sleep(std::time::Duration::from_millis(20));
    write_object(&dir, "ProcGamma", "Call(ZZZUnknown)\n//\n");
    let live = fs::read_to_string(&target).unwrap();
    assert_eq!(
        live.len(),
        commented.len(),
        "the rewrite must preserve length for this test to mean anything"
    );
    assert_ne!(live, commented);

    let g = load_cache(&dir).unwrap();
    assert!(
        g.symbol_to_id.contains_key("zzzunknown"),
        "a same-size content change must still be observed"
    );
    assert_same_graph(
        "after resize",
        &g,
        &cold_build(&clone_dir(&dir, "resize_ref")),
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn renamed_object_renames_its_node() {
    let dir = scenario_dir("rename");
    seed(&dir);
    let (initial, parses) = build_index(&dir).unwrap();
    save_index(&initial, &parses, &dir).unwrap();

    fs::rename(dir.join("ProcGamma.txt"), dir.join("ProcRenamed.txt")).unwrap();
    let g = load_cache(&dir).unwrap();

    let renamed = g
        .symbol_to_id
        .get("procrenamed")
        .and_then(|id| g.get_node(*id))
        .expect("a renamed object must adopt its new name");
    assert!(
        renamed.path.is_some(),
        "the renamed object must stay file-backed"
    );

    // ProcBeta still calls `ProcGamma`, so that name legitimately survives as a
    // phantom node for the now-unresolved reference - but no longer as a file.
    let dangling = g
        .symbol_to_id
        .get("procgamma")
        .and_then(|id| g.get_node(*id));
    assert!(
        dangling.map(|n| n.path.is_none()).unwrap_or(true),
        "the old name must no longer resolve to a file"
    );

    assert_same_graph(
        "after rename",
        &g,
        &cold_build(&clone_dir(&dir, "rename_ref")),
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn unreadable_index_is_rebuilt_instead_of_trusted() {
    let dir = scenario_dir("corrupt");
    seed(&dir);
    let reference = || cold_build(&clone_dir(&dir, "corrupt_ref"));

    // A write interrupted midway leaves a short file. It must be discarded, not
    // decoded into a half-populated graph.
    let (initial, parses) = build_index(&dir).unwrap();
    save_index(&initial, &parses, &dir).unwrap();
    let full = fs::read(get_cache_path(&dir)).unwrap();
    fs::write(get_cache_path(&dir), &full[..full.len() / 2]).unwrap();
    assert_same_graph("truncated", &load_cache(&dir).unwrap(), &reference());

    // Too short to even hold a layout marker. Slicing the marker out of a short
    // buffer would panic here, so this also guards the length check itself.
    fs::write(get_cache_path(&dir), b"nope").unwrap();
    assert_same_graph("too short", &load_cache(&dir).unwrap(), &reference());

    fs::write(get_cache_path(&dir), b"").unwrap();
    assert_same_graph("empty", &load_cache(&dir).unwrap(), &reference());

    // Leading bytes of another layout, whose first field is a collection
    // length. Decoding must not reserve gigabytes before failing.
    fs::write(get_cache_path(&dir), [9u8, 0, 0, 0, 1, 2, 3, 4]).unwrap();
    assert_same_graph("foreign marker", &load_cache(&dir).unwrap(), &reference());

    fs::write(get_cache_path(&dir), vec![0xABu8; 4096]).unwrap();
    assert_same_graph("garbage", &load_cache(&dir).unwrap(), &reference());

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn warm_load_does_not_rewrite_the_index() {
    let dir = scenario_dir("warm");
    seed(&dir);

    let (graph, parses) = build_index(&dir).unwrap();
    save_index(&graph, &parses, &dir).unwrap();

    let cache_path = get_cache_path(&dir);
    let written = fs::metadata(&cache_path).unwrap().modified().unwrap();

    // Nothing moved, so a correct load answers from the cached graph and leaves
    // the index alone. If the write and read layouts ever drift apart, the
    // decode fails, load_cache silently reparses, and this file gets rewritten -
    // which is the only externally visible symptom, and slow rather than wrong.
    std::thread::sleep(std::time::Duration::from_millis(40));
    let g = load_cache(&dir).unwrap();
    assert_same_graph("warm", &g, &cold_build(&clone_dir(&dir, "warm_ref")));

    assert_eq!(
        fs::metadata(&cache_path).unwrap().modified().unwrap(),
        written,
        "a warm load rewrote the index: the cached graph was not reusable"
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn empty_knowledge_base_yields_an_empty_graph() {
    let dir = scenario_dir("empty");
    fs::create_dir_all(&dir).unwrap();

    let g = load_cache(&dir).unwrap();
    assert!(
        g.nodes.is_empty(),
        "an empty KB must produce an empty graph"
    );

    // And it must stay answerable once an object shows up.
    write_object(&dir, "ProcFirst", "// first\n");
    let g = load_cache(&dir).unwrap();
    assert_eq!(g.nodes.len(), 1);

    let _ = fs::remove_dir_all(&dir);
}
