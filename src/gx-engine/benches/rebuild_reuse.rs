//! Synthetic benchmark of what a rebuild reuses: generation only, on a generated model.
//!
//! The model is made here (Procedures with a fixed body shape, some calling others, some using a
//! structured data type). It is not a Knowledge Base: nothing is read from SQL Server and nothing is
//! compiled (`skip_compile`), so the numbers say how the generation phases scale with what changed, not
//! how long a real build takes. Do not quote them as Knowledge Base timings.
//!
//! ```text
//! cargo bench --manifest-path src/gx-engine/Cargo.toml --bench rebuild_reuse -- [procedures] [iterations]
//! ```
//!
//! Each iteration uses new directories and measures, in order: a rebuild into an empty directory
//! (`full`), the same model again (`unchanged`), one Procedure body changed (`one_procedure_changed`),
//! one structured data type changed (`one_sdt_changed`), then a rebuild into an empty directory with an
//! empty artifact cache (`cache_cold`) and into another empty directory with that cache (`cache_warm`).

use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    rebuild::{RebuildOptions, RebuildReport, rebuild_all},
};
use serde_json::json;
use std::{
    fs,
    path::{Path, PathBuf},
    process::ExitCode,
    time::Instant,
};

const VARIABLES: &str = r#"{
    "I": {"kind": "numeric", "precision": 4, "scale": 0},
    "Total": {"kind": "numeric", "precision": 12, "scale": 2},
    "Name": {"kind": "varchar", "length": 40}
}"#;

fn part(name: &str, source: String) -> SourcePart {
    SourcePart {
        name: name.into(),
        source,
    }
}

/// A body of `blocks` loops and conditions, so emission has real work per Procedure.
fn body(index: usize, revision: usize, blocks: usize) -> String {
    let mut source =
        format!("&Name = 'procedure {index} revision {revision}'\nmsg(&Name, status)\n");
    for block in 0..blocks {
        source.push_str(&format!(
            "&I = 0\n&Total = {block}\nDo While &I < 10\n    &Total = &Total + &I * 2\n    &I = &I + 1\nEndDo\nIf &Total > 50\n    msg('big', status)\nElse\n    msg('small', status)\nEndIf\n"
        ));
    }
    source
}

/// Procedure `index`: every fifth one takes a parameter and is called by the next; every seventh one
/// declares a variable of a structured data type.
fn procedure(index: usize, revision: usize, sdts: usize) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(format!("p{index}")).unwrap(),
        format!("P{index}"),
        ObjectKind::Procedure,
    );
    let mut source = body(index, revision, 6);
    let mut variables = VARIABLES.to_string();
    if index % 5 == 1 {
        source.push_str(&format!("P{}(&I)\n", index - 1));
    }
    if index % 7 == 3 && sdts > 0 {
        let sdt = index % sdts;
        variables = variables.replacen(
            '{',
            &format!(r#"{{"Row": {{"kind": "record", "schema": "S{sdt}"}},"#),
            1,
        );
        source.push_str("&Row.Label = &Name\n");
        object.references.push(Reference {
            kind: ReferenceKind::Uses,
            target: ObjectId::new(format!("s{sdt}")).unwrap(),
        });
    }
    object.source_parts.push(part("SOURCE", source));
    object.source_parts.push(part("VARIABLES", variables));
    if index.is_multiple_of(5) {
        object
            .source_parts
            .push(part("RULES", "Parm(in:&I);".to_string()));
    }
    object
}

fn sdt(index: usize, length: u32) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(format!("s{index}")).unwrap(),
        format!("S{index}"),
        ObjectKind::StructuredDataType,
    );
    object.source_parts.push(part(
        "STRUCTURE",
        format!(
            r#"{{"fields": [
                {{"node": "field", "name": "Id", "data_type": {{"kind": "numeric", "precision": 9, "scale": 0}}}},
                {{"node": "field", "name": "Label", "data_type": {{"kind": "varchar", "length": {length}}}}}
            ]}}"#
        ),
    ));
    object
}

fn model(procedures: usize, sdts: usize) -> Vec<KbObject> {
    (0..procedures)
        .map(|index| procedure(index, 0, sdts))
        .chain((0..sdts).map(|index| sdt(index, 40)))
        .collect()
}

fn catalog(objects: &[KbObject]) -> Catalog {
    let edits: Vec<Edit> = objects.iter().cloned().map(Edit::Upsert).collect();
    let mut catalog = Catalog::empty();
    catalog.apply(0, &edits).expect("synthetic catalog");
    catalog
}

fn options() -> RebuildOptions {
    RebuildOptions::new("net8.0")
        .with_clean(false)
        .with_skip_compile(true)
}

/// One measured rebuild: wall time, and the engine's own split of it (classification, specification and
/// emission, in whole milliseconds), which says where a scenario's time goes.
#[derive(Clone, Copy)]
struct Sample {
    wall_ms: f64,
    read_ms: u64,
    spec_ms: u64,
    emit_ms: u64,
}

/// One rebuild: its timings and its report. A model that does not generate cleanly would time error
/// paths, so any error stops the benchmark.
fn timed(catalog: &Catalog, dir: &Path, options: &RebuildOptions) -> (Sample, RebuildReport) {
    let start = Instant::now();
    let report = rebuild_all(catalog, dir, options).expect("the rebuild runs");
    let wall_ms = start.elapsed().as_secs_f64() * 1000.0;
    assert!(report.errors.is_empty(), "{:?}", report.errors);
    let sample = Sample {
        wall_ms,
        read_ms: report.read_ms,
        spec_ms: report.spec_ms,
        emit_ms: report.emit_ms,
    };
    (sample, report)
}

fn summary(samples: &mut [Sample]) -> serde_json::Value {
    samples.sort_by(|a, b| a.wall_ms.total_cmp(&b.wall_ms));
    let round = |ms: f64| (ms * 100.0).round() / 100.0;
    let median = samples[samples.len() / 2];
    json!({
        "median_ms": round(median.wall_ms),
        "min_ms": round(samples[0].wall_ms),
        "max_ms": round(samples[samples.len() - 1].wall_ms),
        "median_run_phases_ms": {
            "classify": median.read_ms,
            "specify": median.spec_ms,
            "emit": median.emit_ms,
        },
    })
}

fn main() -> ExitCode {
    // `cargo bench` appends `--bench`; the positional arguments are the sizes.
    let sizes: Vec<usize> = std::env::args()
        .skip(1)
        .filter_map(|arg| arg.parse().ok())
        .collect();
    let procedures = sizes.first().copied().unwrap_or(2000);
    let iterations = sizes.get(1).copied().unwrap_or(7).max(1);
    let sdts = (procedures / 20).max(1);

    let root: PathBuf =
        std::env::temp_dir().join(format!("gx_rebuild_reuse_bench_{}", std::process::id()));
    let _ = fs::remove_dir_all(&root);

    let objects = model(procedures, sdts);
    let first = catalog(&objects);
    let mut edited = objects.clone();
    // Procedure 2 takes no parameter and nobody calls it: only its body changes.
    edited[2] = procedure(2, 1, sdts);
    let one_procedure = catalog(&edited);
    edited[procedures] = sdt(0, 80);
    let one_sdt = catalog(&edited);

    let names = [
        "full",
        "unchanged",
        "one_procedure_changed",
        "one_sdt_changed",
        "cache_cold",
        "cache_warm",
    ];
    let mut samples: Vec<Vec<Sample>> = vec![Vec::new(); names.len()];
    let mut generated = 0;
    // One pass that is not measured: thread start, file-system and allocator warm-up.
    for iteration in 0..=iterations {
        let dir = root.join(format!("out{iteration}"));
        let cache = root.join(format!("cache{iteration}"));
        let plain = options();
        let cached = options().with_artifact_cache_dir(&cache);

        let (full, report) = timed(&first, &dir, &plain);
        assert_eq!(report.reuse.mode, "full");
        generated = report.generated_count;
        let (unchanged, report) = timed(&first, &dir, &plain);
        assert_eq!(report.reuse.mode, "unchanged");
        let (one, report) = timed(&one_procedure, &dir, &plain);
        assert_eq!(
            (report.reuse.procedures_emitted, report.reuse.sdts_kept),
            (1, true)
        );
        let (one_sdt_ms, report) = timed(&one_sdt, &dir, &plain);
        assert_eq!(report.reuse.procedures_kept, 0);

        let (cold, report) = timed(&first, &root.join(format!("cold{iteration}")), &cached);
        assert_eq!(report.reuse.cache_entries_restored, 0);
        let (warm, report) = timed(&first, &root.join(format!("warm{iteration}")), &cached);
        assert_eq!(report.reuse.cache_entries_stored, 0);
        assert_eq!(report.reuse.cache_entries_restored, 2 * procedures + 1);

        if iteration > 0 {
            for (index, sample) in [full, unchanged, one, one_sdt_ms, cold, warm]
                .into_iter()
                .enumerate()
            {
                samples[index].push(sample);
            }
        }
        let _ = fs::remove_dir_all(&root);
    }

    let mut scenarios = serde_json::Map::new();
    for (name, samples) in names.iter().zip(&mut samples) {
        scenarios.insert((*name).to_string(), summary(samples));
    }
    println!(
        "{}",
        json!({
            "benchmark": "rebuild_reuse",
            "synthetic": true,
            "note": "generated model, generation only (no Knowledge Base read, no compile)",
            "version": env!("CARGO_PKG_VERSION"),
            "procedures": procedures,
            "sdts": sdts,
            "generated_cs_files": generated,
            "iterations": iterations,
            "warmup": 1,
            "scenarios": scenarios,
        })
    );
    ExitCode::SUCCESS
}
