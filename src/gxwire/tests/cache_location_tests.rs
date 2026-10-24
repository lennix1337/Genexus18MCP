//! Where the index actually lands.
//!
//! Issue #499: `genexus_wire` publishes itself as read-only (`readOnlyHint:
//! true`, "Mutating actions: none") while gxwire wrote `.gxwire_cache.bin` into
//! the directory it was asked to analyse. With a native Knowledge Base that
//! directory is the Knowledge Base itself.
//!
//! These tests drive the real entry points rather than the path helpers. An
//! earlier version of this change passed the whole suite while still writing to
//! the analysed directory, because the one line that did it was on a cold-start
//! branch of `load_cache_from` that no test reached - `read_bundle` returns
//! `None` for a fresh directory, so only a test that actually calls
//! `load_cache_from` against a directory with no index can catch that.

use gxwire::cache::{build_index, cache_path_for, get_cache_path, load_cache_from, save_index_to};
use std::fs;
use std::path::{Path, PathBuf};

fn scratch(label: &str) -> PathBuf {
    let dir =
        std::env::temp_dir().join(format!("gxwire-cache-loc-{}-{}", label, std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).unwrap();
    dir
}

fn write_source(dir: &Path, name: &str, body: &str) {
    fs::write(dir.join(name), body).unwrap();
}

#[test]
fn cold_start_writes_to_the_requested_path_and_leaves_the_tree_alone() {
    let tree = scratch("cold");
    write_source(&tree, "MyProc.txt", "// sample\nCall(OtherProc);\n");

    let outside = scratch("cold-cache").join("idx.bin");
    let _ = load_cache_from(&outside, &tree).unwrap();

    assert!(
        outside.exists(),
        "the index must land where the caller asked"
    );
    assert!(
        !get_cache_path(&tree).exists(),
        "the analysed directory must stay untouched (#499)"
    );
}

#[test]
fn stale_index_refresh_writes_to_the_requested_path() {
    let tree = scratch("stale");
    write_source(&tree, "MyProc.txt", "// one\nCall(A);\n");

    let outside = scratch("stale-cache").join("idx.bin");
    load_cache_from(&outside, &tree).unwrap();

    // The incremental refresh path is a different branch from the cold start,
    // and it is the one that rewrites an existing index.
    write_source(&tree, "Other.txt", "// two\n");
    load_cache_from(&outside, &tree).unwrap();

    assert!(outside.exists());
    assert!(
        !get_cache_path(&tree).exists(),
        "refreshing a stale index must not write into the analysed directory"
    );
}

#[test]
fn relocated_index_is_reused_on_the_next_run() {
    // The point of moving the cache is not merely to avoid a write: the index has
    // to still work, or the change would trade a contract violation for a
    // performance regression.
    let tree = scratch("reuse");
    write_source(&tree, "MyProc.txt", "// one\nCall(A);\n");

    let outside = scratch("reuse-cache").join("idx.bin");
    let first = load_cache_from(&outside, &tree).unwrap();
    let second = load_cache_from(&outside, &tree).unwrap();

    assert_eq!(first.nodes.len(), second.nodes.len());
    assert!(!second.nodes.is_empty(), "the graph must survive the move");
}

#[test]
fn save_index_to_creates_a_missing_cache_directory() {
    // The cache is relocated outside the analysed tree, so its directory is not
    // there by default the way the analysed root's was.
    let tree = scratch("mkparent");
    write_source(&tree, "MyProc.txt", "// one\n");

    let nested = scratch("mkparent-outer")
        .join("a")
        .join("b")
        .join("idx.bin");
    let (graph, parses) = build_index(&tree).unwrap();

    save_index_to(&graph, &parses, &nested).unwrap();

    assert!(nested.exists());
}

#[test]
fn cache_path_for_defaults_to_the_in_tree_dotfile() {
    // The default is unchanged, so gxwire invoked directly on a text tree keeps
    // behaving as it always has.
    let tree = scratch("default");

    assert_eq!(cache_path_for(&tree, None), get_cache_path(&tree));
    assert!(cache_path_for(&tree, None).ends_with(".gxwire_cache.bin"));
}

#[test]
fn cache_path_for_honours_the_override() {
    let tree = scratch("override");
    let outside = PathBuf::from("Z:/elsewhere/idx.bin");

    assert_eq!(cache_path_for(&tree, Some(&outside)), outside);
}
