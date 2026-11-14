//! Reads a Knowledge Base through whichever SQL transport `GXMCP_ENGINE_SQL_TRANSPORT` selects and
//! prints what it read as digests, with the time each read took. Read-only: it issues the
//! engine's generated SELECT statements and nothing else.
//!
//! `scripts/validate-engine-tds-live.ps1` runs it once per transport and compares the results.
//!
//! ```text
//! udm_transport_probe <kb-path> [--runs <n>] [--parts <n>] [--dump <directory>]
//! ```

use gx_engine::catalog::Catalog;
use gx_engine::udm::version_scope::{ScopeUse, select_design_model};
use gx_engine::udm::{KbConnectionInfo, UdmClient};
use sha2::{Digest, Sha256};
use std::path::Path;
use std::time::Instant;

fn main() -> std::process::ExitCode {
    match run() {
        Ok(report) => {
            println!("{report}");
            std::process::ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("udm_transport_probe: {error}");
            // The same line the engine CLI prints: a failed read still says which transport it used.
            for reason in gx_engine::udm::live::transport_fallbacks() {
                eprintln!("gx-engine: SQL transport: reads used sqlcmd: {reason}");
            }
            std::process::ExitCode::FAILURE
        }
    }
}

fn run() -> Result<String, String> {
    let mut args = std::env::args().skip(1);
    let kb = args.next().ok_or("usage: udm_transport_probe <kb-path> [--runs <n>] [--parts <n>] [--dump <directory>]")?;
    let (mut runs, mut parts, mut dump) = (3usize, 40usize, None);
    while let Some(flag) = args.next() {
        let value = args.next().ok_or_else(|| format!("{flag} needs a value"))?;
        match flag.as_str() {
            "--runs" => runs = value.parse().map_err(|_| "--runs takes a number")?,
            "--parts" => parts = value.parse().map_err(|_| "--parts takes a number")?,
            "--dump" => dump = Some(value),
            other => return Err(format!("unknown argument {other}")),
        }
    }
    if runs == 0 {
        return Err("--runs must be at least 1".into());
    }
    let kb = Path::new(&kb);

    let mut catalog_ms = Vec::new();
    let mut parts_ms = Vec::new();
    let mut read: Option<(Vec<u8>, Vec<u8>, usize)> = None;
    for _ in 0..runs {
        // A new client per run: nothing read by an earlier run is answered from memory.
        let started = Instant::now();
        let catalog = Catalog::load(kb)?.snapshot_text()?.into_bytes();
        catalog_ms.push(started.elapsed().as_secs_f64() * 1000.0);

        let started = Instant::now();
        let (part_bytes, part_count) = read_parts(kb, parts)?;
        parts_ms.push(started.elapsed().as_secs_f64() * 1000.0);

        match &read {
            Some((first_catalog, first_parts, _))
                if *first_catalog != catalog || *first_parts != part_bytes =>
            {
                return Err(
                    "two runs over the same transport read different bytes: the Knowledge Base changed during the probe, or a read is not deterministic".into(),
                );
            }
            Some(_) => {}
            None => read = Some((catalog, part_bytes, part_count)),
        }
    }
    let (catalog, part_bytes, part_count) = read.expect("at least one run");
    if let Some(directory) = dump {
        let directory = Path::new(&directory);
        for (name, bytes) in [("catalog.bin", &catalog), ("parts.bin", &part_bytes)] {
            std::fs::write(directory.join(name), bytes)
                .map_err(|error| format!("cannot write {name}: {error}"))?;
        }
    }
    let sha256 = |bytes: &[u8]| {
        Sha256::digest(bytes)
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<String>()
    };
    let transport = gx_engine::udm::live::transport_report(&KbConnectionInfo::from_kb_dir(kb)?);
    Ok(serde_json::json!({
        "transport": transport,
        "catalog_bytes": catalog.len(),
        "catalog_sha256": sha256(&catalog),
        "catalog_ms": catalog_ms,
        "parts": part_count,
        "part_bytes": part_bytes.len(),
        "parts_sha256": sha256(&part_bytes),
        "parts_ms": parts_ms,
    })
    .to_string())
}

/// Reads the stored payloads of the first `limit` objects of the model and of their parts, one
/// statement per part (the path a single object read takes, not the set-wise catalog read), and
/// returns them as one byte string: each part's key, payload and properties, length-prefixed.
fn read_parts(kb: &Path, limit: usize) -> Result<(Vec<u8>, usize), String> {
    let client = UdmClient::connect_kb(kb)?.with_persistent_reads();
    let model = select_design_model(&client.model_topology()?, None, ScopeUse::Read)
        .map_err(|refusal| refusal.0)?;
    let client = client.with_design_model_id(model);
    let mut objects = client.get_active_objects(model)?;
    objects.sort_by_key(|object| (object.entity_type_id, object.entity_id));

    let mut bytes = Vec::new();
    let mut count = 0;
    let types = client.get_entity_types()?;
    for entity_type in &types {
        bytes.extend(entity_type.entity_type_id.to_le_bytes());
        bytes.extend(entity_type.name.as_bytes());
        bytes.push(0);
    }
    for object in objects.iter().take(limit) {
        let mut keys = vec![(
            object.entity_type_id,
            object.entity_id,
            object.entity_version_id,
        )];
        keys.extend(
            client
                .get_compositions(
                    object.entity_type_id,
                    object.entity_id,
                    object.entity_version_id,
                )?
                .iter()
                .map(|part| {
                    (
                        part.component_entity_type_id,
                        part.component_entity_id,
                        part.component_entity_version_id,
                    )
                }),
        );
        for (entity_type, entity_id, version_id) in keys {
            for id in [entity_type, entity_id, version_id] {
                bytes.extend(id.to_le_bytes());
            }
            match client.get_entity_version(entity_type, entity_id, version_id)? {
                None => bytes.push(0),
                Some(record) => {
                    count += 1;
                    bytes.push(1);
                    bytes.extend((record.data.len() as u64).to_le_bytes());
                    bytes.extend(&record.data);
                    let properties = record.properties.unwrap_or_default();
                    bytes.extend((properties.len() as u64).to_le_bytes());
                    bytes.extend(properties.as_bytes());
                }
            }
        }
    }
    Ok((bytes, count))
}
