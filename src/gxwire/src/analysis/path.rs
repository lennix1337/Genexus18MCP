use crate::graph::CsrGraph;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PathHop {
    pub from: String,
    pub to: String,
    pub kind: String,
    pub line: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PathResult {
    pub schema: String,
    pub from: String,
    pub to: String,
    pub found: bool,
    pub hops_count: usize,
    pub hops: Vec<PathHop>,
    pub empty: bool,
    pub est_tokens: usize,
    pub next: String,
}

#[allow(dead_code)]
pub fn analyze_path(graph: &CsrGraph, from_name: &str, to_name: &str) -> Option<PathResult> {
    analyze_path_ext(graph, from_name, to_name, false)
}

pub fn analyze_path_ext(
    graph: &CsrGraph,
    from_name: &str,
    to_name: &str,
    _full: bool,
) -> Option<PathResult> {
    let from_node = graph.get_node_by_name(from_name)?;
    let to_node = graph.get_node_by_name(to_name)?;

    let raw_path = graph.shortest_path(from_node.id, to_node.id);

    match raw_path {
        Some(steps) => {
            let mut hops = Vec::with_capacity(steps.len());
            for (from_id, to_id, kind, line) in steps {
                let from_str = graph
                    .get_node(from_id)
                    .map(|n| n.name.clone())
                    .unwrap_or_else(|| from_id.to_string());
                let to_str = graph
                    .get_node(to_id)
                    .map(|n| n.name.clone())
                    .unwrap_or_else(|| to_id.to_string());
                hops.push(PathHop {
                    from: from_str,
                    to: to_str,
                    kind: kind.as_str().to_string(),
                    line,
                });
            }

            let hops_count = hops.len();
            let est_tokens = 40 + hops_count * 20;
            let next = if hops_count > 0 {
                let intermediate = if hops_count > 1 {
                    &hops[0].to
                } else {
                    &to_node.name
                };
                format!(
                    "genexus_wire action=slice symbol={} or genexus_read name={}",
                    intermediate, from_node.name
                )
            } else {
                format!("genexus_read name={}", from_node.name)
            };

            Some(PathResult {
                schema: "gxwire.path/v1".to_string(),
                from: from_node.name.clone(),
                to: to_node.name.clone(),
                found: true,
                hops_count,
                hops,
                empty: false,
                est_tokens,
                next,
            })
        }
        None => Some(PathResult {
            schema: "gxwire.path/v1".to_string(),
            from: from_node.name.clone(),
            to: to_node.name.clone(),
            found: false,
            hops_count: 0,
            hops: Vec::new(),
            empty: true,
            est_tokens: 40,
            next: format!(
                "gxwire --callers {} or gxwire --impact {}",
                to_node.name, from_node.name
            ),
        }),
    }
}
