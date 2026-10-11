use crate::graph::CsrGraph;
use crate::model::{HubItem, StatusResult};
use std::path::Path;

pub fn compute_status(graph: &CsrGraph, root: &Path, cache_path: &Path) -> StatusResult {
    let nodes_count = graph.nodes.len();
    let edges_count = graph.forward_targets.len();
    let cache_exists = cache_path.exists();
    let empty = nodes_count == 0;

    // Find top architectural hubs: rank by PageRank * (1.0 + caller_count)
    let mut hubs_scored: Vec<(usize, f32, usize)> = (0..nodes_count)
        .map(|idx| {
            let callers_len = graph.get_callers(idx as u32).len();
            let pr = graph.pagerank.get(idx).copied().unwrap_or(1.0);
            let score = pr * (1.0 + callers_len as f32);
            (idx, score, callers_len)
        })
        .collect();

    hubs_scored.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));

    let hubs: Vec<HubItem> = hubs_scored
        .into_iter()
        .take(5)
        .filter_map(|(idx, _, callers_len)| {
            graph.get_node(idx as u32).map(|n| HubItem {
                name: n.name.clone(),
                kind: n.kind.as_str().to_string(),
                refs: callers_len,
                pagerank: graph.pagerank.get(idx).copied().unwrap_or(1.0),
            })
        })
        .collect();

    let next = if empty {
        format!("gxwire --index \"{}\" to scan and index Knowledge Base", root.display())
    } else {
        "gxwire --for \"<task>\" or gxwire --pack-task \"<task>\"".to_string()
    };

    StatusResult {
        schema: "gxwire.status/v1".to_string(),
        root: root.display().to_string(),
        cache_exists,
        cache_path: cache_path.display().to_string(),
        nodes_count,
        edges_count,
        empty,
        hubs,
        next,
    }
}
