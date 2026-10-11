use crate::graph::CsrGraph;
use crate::model::{EntityKind, EntityNode, RawEdge};
use crate::parser::parse_genexus_source;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs::{self, File};
use std::io::{BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::time::UNIX_EPOCH;
use walkdir::WalkDir;

const CACHE_FILE_NAME: &str = ".gxwire_cache.bin";

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CachedBundle {
    pub graph: CsrGraph,
    pub file_mtimes: HashMap<String, u64>,
}

pub fn get_cache_path(root: &Path) -> PathBuf {
    root.join(CACHE_FILE_NAME)
}

fn get_file_mtime(path: &Path) -> u64 {
    fs::metadata(path)
        .and_then(|m| m.modified())
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

pub fn save_cache(graph: &CsrGraph, root: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let cache_path = get_cache_path(root);
    let mut file_mtimes = HashMap::new();
    for node in &graph.nodes {
        if let Some(ref path_str) = node.path {
            let p = Path::new(path_str);
            if p.exists() {
                file_mtimes.insert(path_str.clone(), get_file_mtime(p));
            }
        }
    }
    let bundle = CachedBundle {
        graph: graph.clone(),
        file_mtimes,
    };
    let encoded = bincode::serialize(&bundle)?;
    let mut file = File::create(cache_path)?;
    file.write_all(&encoded)?;
    Ok(())
}

fn is_cache_fresh(bundle: &CachedBundle, root: &Path) -> bool {
    // 1. Check if all cached files still have identical mtimes
    for (path_str, &cached_mtime) in &bundle.file_mtimes {
        let p = Path::new(path_str);
        if !p.exists() {
            return false;
        }
        let current_mtime = get_file_mtime(p);
        if current_mtime != cached_mtime {
            return false;
        }
    }

    // 2. Check if any new source file exists that was not in cache
    for entry in WalkDir::new(root).into_iter().filter_map(|e| e.ok()) {
        let path = entry.path();
        if path.is_file() {
            let ext = path.extension().and_then(|s| s.to_str()).unwrap_or("");
            if is_supported_ext(ext) {
                let p_str = path.to_string_lossy().to_string();
                if !bundle.file_mtimes.contains_key(&p_str) {
                    return false;
                }
            }
        }
    }

    true
}

pub fn load_cache(root: &Path) -> Result<CsrGraph, Box<dyn std::error::Error>> {
    let cache_path = get_cache_path(root);
    let file = File::open(&cache_path)?;
    let reader = BufReader::new(file);

    // Try reading CachedBundle
    if let Ok(bundle) = bincode::deserialize_from::<_, CachedBundle>(reader) {
        if is_cache_fresh(&bundle, root) {
            return Ok(bundle.graph);
        }
        // Cache is stale! Rebuild and save updated cache automatically
        let fresh_graph = build_graph_from_directory(root)?;
        let _ = save_cache(&fresh_graph, root);
        return Ok(fresh_graph);
    }

    // Fallback: try reading legacy raw CsrGraph
    let file_legacy = File::open(&cache_path)?;
    let reader_legacy = BufReader::new(file_legacy);
    if let Ok(graph) = bincode::deserialize_from::<_, CsrGraph>(reader_legacy) {
        let _ = save_cache(&graph, root);
        return Ok(graph);
    }

    Err("Invalid cache format".into())
}

#[inline]
fn is_supported_ext(ext: &str) -> bool {
    matches!(
        ext.to_ascii_lowercase().as_str(),
        "txt" | "gxd" | "xml" | "gx" | "prc" | "trn" | "wp" | "sdp"
    )
}

fn detect_entity_kind(path: &Path, file_stem: &str, ext: &str) -> EntityKind {
    match ext.to_ascii_lowercase().as_str() {
        "prc" => EntityKind::Procedure,
        "trn" => EntityKind::Transaction,
        "wp" => EntityKind::WebPanel,
        "sdp" => EntityKind::Panel,
        "xml" => {
            if let Ok(file) = File::open(path) {
                let mut buf = [0u8; 512];
                let mut reader = BufReader::new(file);
                if let Ok(n) = reader.read(&mut buf) {
                    let head = String::from_utf8_lossy(&buf[..n]).to_ascii_lowercase();
                    if head.contains("<procedure") {
                        return EntityKind::Procedure;
                    } else if head.contains("<transaction") {
                        return EntityKind::Transaction;
                    } else if head.contains("<webpanel") {
                        return EntityKind::WebPanel;
                    } else if head.contains("<sdpanel") {
                        return EntityKind::Panel;
                    } else if head.contains("<workwith") {
                        return EntityKind::WorkWithPlus;
                    } else if head.contains("<table") {
                        return EntityKind::Table;
                    } else if head.contains("<attribute") {
                        return EntityKind::Attribute;
                    } else if head.contains("<domain") {
                        return EntityKind::Domain;
                    } else if head.contains("<sdt") {
                        return EntityKind::Sdt;
                    } else if head.contains("<dataselector") {
                        return EntityKind::DataSelector;
                    }
                }
            }
            if file_stem.starts_with('P') {
                EntityKind::Procedure
            } else if file_stem.starts_with("Trn") || file_stem.starts_with('T') {
                EntityKind::Transaction
            } else if file_stem.starts_with("WP") || file_stem.starts_with('W') {
                EntityKind::WebPanel
            } else {
                EntityKind::Procedure
            }
        }
        _ => {
            if file_stem.starts_with('P') {
                EntityKind::Procedure
            } else if file_stem.starts_with("Trn") || file_stem.starts_with('T') {
                EntityKind::Transaction
            } else if file_stem.starts_with("WP") || file_stem.starts_with('W') {
                EntityKind::WebPanel
            } else if file_stem.starts_with("UT_") {
                EntityKind::Test
            } else {
                EntityKind::Procedure
            }
        }
    }
}

/// Ingests directory containing GeneXus exported sources / text tree files
pub fn build_graph_from_directory(dir: &Path) -> Result<CsrGraph, Box<dyn std::error::Error>> {
    let mut nodes = Vec::new();
    let mut raw_edges = Vec::new();
    let mut name_to_id = std::collections::HashMap::new();

    // 1. First pass: Collect all files and create nodes
    for entry in WalkDir::new(dir).into_iter().filter_map(|e| e.ok()) {
        let path = entry.path();
        if path.is_file() {
            let ext = path.extension().and_then(|s| s.to_str()).unwrap_or("");
            if is_supported_ext(ext) {
                let file_stem = path.file_stem().and_then(|s| s.to_str()).unwrap_or("");
                let kind = detect_entity_kind(path, file_stem, ext);

                let id = nodes.len() as u32;
                name_to_id.insert(file_stem.to_ascii_lowercase(), id);

                nodes.push(EntityNode {
                    id,
                    name: file_stem.to_string(),
                    kind,
                    module: None,
                    description: None,
                    parm_rule: None,
                    path: Some(path.to_string_lossy().to_string()),
                    lines: 0,
                    complexity: 1,
                });
            }
        }
    }

    // 2. Second pass: Parse files and create edges
    for i in 0..nodes.len() {
        if let Some(ref path_str) = nodes[i].path.clone() {
            if let Ok(content) = fs::read_to_string(path_str) {
                let parsed = parse_genexus_source(&content);
                nodes[i].lines = parsed.lines_count;
                nodes[i].complexity = parsed.complexity;
                nodes[i].parm_rule = parsed.parm_rule;

                for ref_sym in parsed.references {
                    let target_id = if let Some(&id) = name_to_id.get(&ref_sym.target_name.to_ascii_lowercase()) {
                        id
                    } else {
                        // Create phantom node for external reference
                        let new_id = nodes.len() as u32;
                        name_to_id.insert(ref_sym.target_name.to_ascii_lowercase(), new_id);
                        let kind = match ref_sym.kind {
                            crate::model::EdgeKind::WritesAttr(_) | crate::model::EdgeKind::ReadsAttr => EntityKind::Attribute,
                            _ => EntityKind::Unknown,
                        };
                        nodes.push(EntityNode {
                            id: new_id,
                            name: ref_sym.target_name.clone(),
                            kind,
                            module: None,
                            description: None,
                            parm_rule: None,
                            path: None,
                            lines: 0,
                            complexity: 1,
                        });
                        new_id
                    };

                    raw_edges.push(RawEdge {
                        from: i as u32,
                        to: target_id,
                        kind: ref_sym.kind,
                        line: ref_sym.line,
                    });
                }
            }
        }
    }

    Ok(CsrGraph::build(nodes, &raw_edges))
}
