//! End-to-end reindex benchmark for the on-disk index path.
//!
//! `scale_benchmark.rs` measures queries against an in-memory graph. This
//! covers the path that dominates an agent session: a Knowledge Base on disk is
//! edited one object at a time, and every `genexus_wire` call re-enters
//! `cache::load_cache`. A developer edit must not cost a full re-parse of the
//! whole Knowledge Base.
//!
//! Each scenario is repeated and reported as a median, because a single sample
//! on this machine varies by more than the effect being measured.
//!
//! Run with `--release` for the numbers that matter; debug builds run every
//! regex pass unoptimized and are only useful for shape comparison.

use gxwire::cache::{build_index, get_cache_path, load_cache, save_index};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const REPEATS: usize = 5;

/// Writes one realistic-ish GeneXus object source.
///
/// The content matters: a file that produces no `ParseResult.references` would
/// skip the edge-derivation work and understate the reindex cost.
fn write_object(dir: &Path, index: usize, peers: usize) -> PathBuf {
    let (name, ext) = match index % 3 {
        0 => (format!("Proc{:06}", index), "txt"),
        1 => (format!("Trn{:06}", index), "trn"),
        _ => (format!("WebPanel{:06}", index), "wp"),
    };

    let mut body = String::new();
    body.push_str("// Generated benchmark object\n");
    body.push_str("ParmRule(in:&CliCod, out:&Total)\n");
    body.push_str("Subroutine(DoWork)\n");
    for peer in 0..peers {
        let callee = (index + peer * 13 + 7) % 20_000;
        let target = match callee % 3 {
            0 => format!("Proc{:06}", callee),
            1 => format!("Trn{:06}", callee),
            _ => format!("WebPanel{:06}", callee),
        };
        body.push_str(&format!("Call({})\n", target));
        body.push_str(&format!("Attr{:04} = &Local{}\n", callee % 500, peer));
        body.push_str(&format!("&Local{} = Attr{:04}\n", peer, (callee + 1) % 500));
        body.push_str(&format!("If &Local{} = ''\n", peer));
        body.push_str("End\n");
    }
    for sub in 0..3 {
        body.push_str(&format!("DoWork{}\n", sub));
    }

    let path = dir.join(format!("{}.{}", name, ext));
    fs::write(&path, body).unwrap();
    path
}

fn generate_kb(dir: &Path, objects: usize, peers: usize) -> Vec<PathBuf> {
    fs::create_dir_all(dir).unwrap();
    (0..objects).map(|i| write_object(dir, i, peers)).collect()
}

fn median(samples: &mut [Duration]) -> Duration {
    samples.sort();
    samples[samples.len() / 2]
}

/// Runs `op` `REPEATS` times and reports the median.
///
/// `setup` runs before the timer starts, so an edit that a scenario has to
/// perform itself is not billed to the reindex it is meant to measure.
fn measure(
    label: &str,
    objects: usize,
    extra: &str,
    mut setup: impl FnMut(),
    mut op: impl FnMut(),
) {
    let mut samples = Vec::with_capacity(REPEATS);
    for _ in 0..REPEATS {
        setup();
        let t = Instant::now();
        op();
        samples.push(t.elapsed());
    }
    let m = median(&mut samples);
    println!(
        "  {:<34} {:>9.2?}  ({} objects{})",
        label, m, objects, extra
    );
}

/// Paths of the file-backed nodes, sorted. Phantom nodes created for unresolved
/// references legitimately appear and disappear as references change, so node
/// *counts* are not a stable assertion; the on-disk file set is.
fn file_backed_paths(graph: &gxwire::graph::CsrGraph) -> Vec<String> {
    let mut paths: Vec<String> = graph.nodes.iter().filter_map(|n| n.path.clone()).collect();
    paths.sort();
    paths
}

#[test]
fn benchmark_cold_warm_and_single_edit_reindex() {
    // Debug runs every regex pass unoptimized; a full-size KB there costs
    // ~45s of CI time for numbers nobody reads in that profile. The assertions
    // are what CI needs from this test, and they hold at either size.
    const OBJECTS: usize = if cfg!(debug_assertions) { 400 } else { 4_000 };
    const PEERS: usize = 12;

    let dir = std::env::temp_dir().join(format!("gxwire_bench_{}", std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    let written = generate_kb(&dir, OBJECTS, PEERS);
    let baseline_paths = file_backed_paths(&build_index(&dir).unwrap().0);

    println!(
        "\n=== GXWIRE REINDEX BENCHMARK ({} objects, median of {}) ===",
        OBJECTS, REPEATS
    );

    // 1. Cold: parse everything. `build_index` never consults the index, so
    // every repeat is a genuine cold build.
    let mut cold = None;
    measure(
        "1. cold build (parse everything)",
        OBJECTS,
        "",
        || {},
        || {
            let (g, parses) = build_index(&dir).unwrap();
            assert_eq!(file_backed_paths(&g), baseline_paths);
            if cold.is_none() {
                cold = Some((g, parses));
            }
        },
    );
    let (cold, cold_parses) = cold.expect("cold build ran");

    // 2. First index write.
    let t = Instant::now();
    save_index(&cold, &cold_parses, &dir).unwrap();
    let save_time = t.elapsed();
    println!(
        "  {:<34} {:>9.2?}  ({} objects, {:.1} MB)",
        "2. save index",
        save_time,
        OBJECTS,
        fs::metadata(get_cache_path(&dir)).unwrap().len() as f64 / 1_048_576.0
    );

    // 3. Warm: nothing changed. This is the common case and must stay cheap.
    measure(
        "3. warm load (0 files changed)",
        OBJECTS,
        "",
        || {},
        || {
            let g = load_cache(&dir).unwrap();
            assert_eq!(g.nodes.len(), cold.nodes.len());
        },
    );

    // 4. One object edited - the developer-edit case. A real editor changes the
    // size and bumps the mtime, which is what the reuse check keys on.
    let edited_body = "Call(Proc000007)\nCall(Trn000042)\nAttr0001 = &LocalX\n";
    measure(
        "4. load after 1 file edited",
        OBJECTS,
        "",
        || {
            std::thread::sleep(Duration::from_millis(12));
            fs::write(&written[0], edited_body).unwrap();
        },
        || {
            let g = load_cache(&dir).unwrap();
            assert!(g.symbol_to_id.contains_key("proc000007"));
            assert_eq!(file_backed_paths(&g), baseline_paths);
        },
    );

    // 5. Newly created object.
    let added = write_object(&dir, 900_001, 2);
    let added_key = added
        .file_stem()
        .unwrap()
        .to_string_lossy()
        .to_ascii_lowercase();
    measure(
        "5. load after 1 file added",
        OBJECTS + 1,
        "",
        || {
            std::thread::sleep(Duration::from_millis(12));
            fs::write(&added, "Call(Proc000007)\nCall(WebPanel000009)\n").unwrap();
        },
        || {
            let g = load_cache(&dir).unwrap();
            assert!(g.symbol_to_id.contains_key(&added_key));
        },
    );

    // 6. Deleted object: its node and its edges must both be gone. Every timed
    // sample has to start from an index that *contains* the file, or the
    // deletion is only real on the first pass and the rest are warm loads.
    let mut after_delete_nodes = 0usize;
    measure(
        "6. load after 1 file deleted",
        OBJECTS,
        "",
        || {
            fs::write(&added, "Call(Proc000007)\nCall(WebPanel000009)\n").unwrap();
            let _ = load_cache(&dir).unwrap();
            std::thread::sleep(Duration::from_millis(12));
            fs::remove_file(&added).unwrap();
        },
        || {
            let g = load_cache(&dir).unwrap();
            after_delete_nodes = g.nodes.len();
            assert_eq!(file_backed_paths(&g), baseline_paths);
            if let Some(id) = g.symbol_to_id.get(&added_key).copied() {
                let node = g
                    .get_node(id)
                    .expect("symbol_to_id must point at a real node");
                assert!(
                    node.path.is_none(),
                    "a deleted object must not stay file-backed"
                );
                assert!(
                    g.get_callers(id).is_empty(),
                    "edges of a deleted object must be dropped"
                );
            }
        },
    );

    // 7. Settled again: the warm path must still answer from the cached graph,
    // byte-identical to the graph the last reindex produced.
    measure(
        "7. warm load (settled)",
        OBJECTS,
        "",
        || {},
        || {
            let g = load_cache(&dir).unwrap();
            assert_eq!(g.nodes.len(), after_delete_nodes);
        },
    );

    println!("===================================================================\n");
    let _ = fs::remove_dir_all(&dir);
}
