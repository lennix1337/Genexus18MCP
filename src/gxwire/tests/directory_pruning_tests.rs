//! Tooling and build-output directories are not part of a Knowledge Base text
//! tree and must not contribute graph nodes (#593 follow-up).
//!
//! `collect_source_files` used to walk everything under the analysed directory,
//! so the `.txt`/`.xml` files inside `.git` or `node_modules` became nodes no
//! GeneXus object owns, and a large tree paid for walking them.

use gxwire::cache::build_graph_from_directory;
use std::fs;
use std::path::{Path, PathBuf};

fn scratch(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("gxwire-prune-{}-{}", label, std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).unwrap();
    dir
}

fn write_source(dir: &Path, relative: &str, body: &str) {
    let path = dir.join(relative);
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    fs::write(path, body).unwrap();
}

fn node_names(dir: &Path) -> Vec<String> {
    let graph = build_graph_from_directory(dir).unwrap();
    graph.nodes.iter().map(|n| n.name.clone()).collect()
}

#[test]
fn tooling_and_build_directories_are_not_walked() {
    let dir = scratch("tooling");
    write_source(
        &dir,
        "Objects/Procedures/Caller.prc",
        "// obj\nCall(Callee);\n",
    );
    for pruned in [".git", ".svn", ".hg", ".vs", "node_modules", "bin", "obj"] {
        write_source(
            &dir,
            &format!("{pruned}/Stray{}.txt", pruned.trim_start_matches('.')),
            "// obj\n",
        );
    }

    let names = node_names(&dir);

    assert!(
        names.iter().any(|n| n == "Caller"),
        "the real object must still be indexed: {names:?}"
    );
    assert!(
        names.iter().all(|n| !n.starts_with("Stray")),
        "files under tooling/build directories must not become nodes: {names:?}"
    );
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn pruning_is_case_insensitive_and_applies_at_any_depth() {
    let dir = scratch("depth");
    write_source(
        &dir,
        "Objects/Procedures/Caller.prc",
        "// obj\nCall(Callee);\n",
    );
    write_source(&dir, "Module/Node_Modules/DeepStray.txt", "// obj\n");
    write_source(&dir, "Module/Sub/BIN/UpperStray.txt", "// obj\n");

    let names = node_names(&dir);

    assert!(names.iter().any(|n| n == "Caller"));
    assert!(
        names.iter().all(|n| n != "DeepStray" && n != "UpperStray"),
        "{names:?}"
    );
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn only_exact_directory_names_are_pruned() {
    let dir = scratch("exact");
    // Folder names that merely contain or resemble a pruned name are legitimate
    // export folders and must keep contributing nodes.
    write_source(&dir, "Binaries/Kept.prc", "// obj\n");
    write_source(&dir, "objects/Kept2.prc", "// obj\n");
    write_source(&dir, "my.git.backup/Kept3.prc", "// obj\n");

    let names = node_names(&dir);

    for kept in ["Kept", "Kept2", "Kept3"] {
        assert!(
            names.iter().any(|n| n == kept),
            "{kept} must be indexed: {names:?}"
        );
    }
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn the_analysed_root_is_never_pruned_even_when_it_has_a_pruned_name() {
    let base = scratch("root");
    let root = base.join("bin");
    write_source(&root, "Objects/Procedures/Inside.prc", "// obj\n");

    let names = node_names(&root);

    assert!(
        names.iter().any(|n| n == "Inside"),
        "a root named like a pruned directory is still analysed: {names:?}"
    );
    let _ = fs::remove_dir_all(&base);
}

#[test]
fn a_pruned_name_used_for_a_file_does_not_hide_it() {
    let dir = scratch("file");
    // `bin` as a FILE name (no extension => unsupported, but `bin.prc` is a
    // legitimate object): pruning is for directories only.
    write_source(&dir, "bin.prc", "// obj\n");

    let names = node_names(&dir);

    assert!(names.iter().any(|n| n == "bin"), "{names:?}");
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn generated_environment_directories_are_not_walked() {
    let dir = scratch("generated");
    write_source(
        &dir,
        "Objects/Procedures/Caller.prc",
        "// obj
Call(Callee);
",
    );
    for pruned in ["build", "UserControls", "Locks", "OutputLogs"] {
        write_source(
            &dir,
            &format!("{pruned}/Stray{pruned}.txt"),
            "// obj
",
        );
        write_source(
            &dir,
            &format!("Env/{pruned}/Deep{pruned}.txt"),
            "// obj
",
        );
    }

    let names = node_names(&dir);

    assert!(names.iter().any(|n| n == "Caller"), "{names:?}");
    assert!(
        names
            .iter()
            .all(|n| !n.starts_with("Stray") && !n.starts_with("Deep")),
        "generated/runtime folders must not become nodes: {names:?}"
    );
    let _ = fs::remove_dir_all(&dir);
}
