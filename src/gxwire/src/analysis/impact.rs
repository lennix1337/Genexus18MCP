use crate::graph::CsrGraph;
use crate::model::{EdgeKind, EntityKind};
use serde::Serialize;
use std::collections::HashSet;

#[derive(Debug, Clone, Serialize)]
pub struct ImpactItem {
    pub name: String,
    pub kind: String,
    pub relationship: String,
    pub line: u32,
    pub depth: usize,
}

#[derive(Debug, Clone, Serialize)]
pub struct ImpactResult {
    pub schema: String,
    pub target: String,
    pub target_kind: String,
    pub blast_radius: usize,
    pub reorg_risk: String,
    pub writers: Vec<ImpactItem>,
    pub readers: Vec<ImpactItem>,
    pub formulas: Vec<ImpactItem>,
    pub callers: Vec<ImpactItem>,
    pub tests: Vec<String>,
    pub can_delete: bool,
    pub empty: bool,
    pub truncated: bool,
    pub omitted: usize,
    pub est_tokens: usize,
    pub next: String,
}

#[allow(dead_code)]
pub fn analyze_impact(graph: &CsrGraph, target_name: &str) -> Option<ImpactResult> {
    analyze_impact_ext(graph, target_name, false)
}

pub fn analyze_impact_ext(graph: &CsrGraph, target_name: &str, full: bool) -> Option<ImpactResult> {
    let target_node = graph.get_node_by_name(target_name)?;
    let target_id = target_node.id;

    let mut writers = Vec::new();
    let mut readers = Vec::new();
    let mut formulas = Vec::new();
    let mut callers = Vec::new();
    let mut tests = Vec::new();
    let mut affected_nodes = HashSet::new();

    affected_nodes.insert(target_id);

    // Direct incoming edges to target
    let incoming = graph.get_callers(target_id);
    for (src_id, kind, line) in incoming {
        if let Some(src_node) = graph.get_node(src_id) {
            affected_nodes.insert(src_id);
            let item = ImpactItem {
                name: src_node.name.clone(),
                kind: src_node.kind.as_str().to_string(),
                relationship: kind.as_str().to_string(),
                line,
                depth: 1,
            };

            match &kind {
                EdgeKind::WritesAttr(_) => writers.push(item),
                EdgeKind::ReadsAttr => readers.push(item),
                EdgeKind::DefinesFormula => formulas.push(item),
                EdgeKind::Calls(_) => {
                    if src_node.kind == EntityKind::Test {
                        tests.push(src_node.name.clone());
                    } else {
                        callers.push(item);
                    }
                }
                _ => callers.push(item),
            }
        }
    }

    // Transitive callers
    let trans = graph.transitive_callers(target_id, 4, 100);
    for (src_id, depth) in trans {
        affected_nodes.insert(src_id);
        if let Some(src_node) = graph.get_node(src_id) {
            if src_node.kind == EntityKind::Test {
                if !tests.contains(&src_node.name) {
                    tests.push(src_node.name.clone());
                }
            } else if !callers.iter().any(|c| c.name == src_node.name) {
                callers.push(ImpactItem {
                    name: src_node.name.clone(),
                    kind: src_node.kind.as_str().to_string(),
                    relationship: "transitive_caller".to_string(),
                    line: 0,
                    depth,
                });
            }
        }
    }

    let blast_radius = affected_nodes.len().saturating_sub(1); // Exclude root

    // Determine reorg risk
    let reorg_risk = if target_node.kind == EntityKind::Attribute {
        if !formulas.is_empty() || writers.len() > 3 {
            "high".to_string()
        } else if !writers.is_empty() {
            "medium".to_string()
        } else {
            "low".to_string()
        }
    } else if target_node.kind == EntityKind::Transaction || target_node.kind == EntityKind::Table {
        "high".to_string()
    } else {
        "none".to_string()
    };

    let can_delete = blast_radius == 0 && callers.is_empty() && writers.is_empty();
    let empty = blast_radius == 0 && writers.is_empty() && readers.is_empty() && formulas.is_empty() && callers.is_empty();
    let est_tokens = 60 + writers.len() * 12 + readers.len() * 10 + formulas.len() * 15 + callers.len() * 10;

    let (truncated, omitted) = if !full && callers.len() > 40 {
        let omit = callers.len() - 40;
        callers.truncate(40);
        (true, omit)
    } else {
        (false, 0)
    };

    let next = if can_delete {
        format!("gxwire --safe-delete {} or genexus_delete {}", target_node.name, target_node.name)
    } else {
        format!("gxwire --slice {}:VAR or gxwire --callers {}", target_node.name, target_node.name)
    };

    Some(ImpactResult {
        schema: "gxwire.impact/v1".to_string(),
        target: target_node.name.clone(),
        target_kind: target_node.kind.as_str().to_string(),
        blast_radius,
        reorg_risk,
        writers,
        readers,
        formulas,
        callers,
        tests,
        can_delete,
        empty,
        truncated,
        omitted,
        est_tokens,
        next,
    })
}
