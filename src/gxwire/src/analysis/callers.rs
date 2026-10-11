use crate::graph::CsrGraph;
use crate::model::EdgeKind;
use serde::Serialize;

#[derive(Debug, Clone, Serialize)]
pub struct CallerItem {
    pub name: String,
    pub kind: String,
    pub call_mode: String,
    pub line: u32,
    pub depth: usize,
}

#[derive(Debug, Clone, Serialize)]
pub struct CallersResult {
    pub schema: String,
    pub target: String,
    pub target_kind: String,
    pub count: usize,
    pub callers: Vec<CallerItem>,
    pub transitive_count: usize,
    pub transitive: Vec<CallerItem>,
    pub empty: bool,
    pub truncated: bool,
    pub omitted: usize,
    pub next: String,
}

#[allow(dead_code)]
pub fn analyze_callers(graph: &CsrGraph, target_name: &str, max_depth: usize) -> Option<CallersResult> {
    analyze_callers_ext(graph, target_name, max_depth, false)
}

pub fn analyze_callers_ext(
    graph: &CsrGraph,
    target_name: &str,
    max_depth: usize,
    full: bool,
) -> Option<CallersResult> {
    let target_node = graph.get_node_by_name(target_name)?;
    let target_id = target_node.id;

    // 1-hop callers
    let callers_raw = graph.get_callers(target_id);
    let mut callers = Vec::new();

    for (src_id, kind, line) in callers_raw {
        if let Some(src_node) = graph.get_node(src_id) {
            let mode = match kind {
                EdgeKind::Calls(m) => m.as_str().to_string(),
                other => other.as_str().to_string(),
            };
            callers.push(CallerItem {
                name: src_node.name.clone(),
                kind: src_node.kind.as_str().to_string(),
                call_mode: mode,
                line,
                depth: 1,
            });
        }
    }

    // Transitive callers
    let trans_raw = graph.transitive_callers(target_id, max_depth, if full { 2000 } else { 200 });
    let mut transitive = Vec::new();
    for (src_id, depth) in trans_raw {
        if let Some(src_node) = graph.get_node(src_id) {
            transitive.push(CallerItem {
                name: src_node.name.clone(),
                kind: src_node.kind.as_str().to_string(),
                call_mode: "transitive".to_string(),
                line: 0,
                depth,
            });
        }
    }

    let transitive_count = transitive.len();
    let count = callers.len();
    let empty = count == 0;

    let (truncated, omitted) = if !full && transitive_count > 30 {
        let omit = transitive_count - 30;
        transitive.truncate(30);
        (true, omit)
    } else {
        (false, 0)
    };

    let next = if empty {
        format!("gxwire --for \"{}\" to find similar symbols", target_node.name)
    } else {
        format!("gxwire --impact {} or gxwire --slice {}:VAR", target_node.name, target_node.name)
    };

    Some(CallersResult {
        schema: "gxwire.callers/v1".to_string(),
        target: target_node.name.clone(),
        target_kind: target_node.kind.as_str().to_string(),
        count,
        callers,
        transitive_count,
        transitive,
        empty,
        truncated,
        omitted,
        next,
    })
}
