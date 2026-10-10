use crate::analysis::search::search_for_task;
use crate::graph::CsrGraph;
use serde::Serialize;

#[derive(Debug, Clone, Serialize)]
pub struct AnchorObject {
    pub name: String,
    pub kind: String,
    pub rank: usize,
    pub parm_rule: Option<String>,
    pub callers: Vec<String>,
    pub callees: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct PackTaskResult {
    pub task: String,
    pub est_tokens: usize,
    pub budget: usize,
    pub anchors: Vec<AnchorObject>,
    pub related_tests: Vec<String>,
}

pub fn pack_task(graph: &CsrGraph, task: &str, budget: usize) -> PackTaskResult {
    let search_res = search_for_task(graph, task, 5);

    let mut anchors = Vec::new();
    let mut tests = Vec::new();

    for hit in search_res.hits {
        if let Some(node) = graph.get_node_by_name(&hit.name) {
            let callers_raw = graph.get_callers(node.id);
            let mut callers = Vec::new();
            for (src_id, _, _) in callers_raw.iter().take(4) {
                if let Some(src_node) = graph.get_node(*src_id) {
                    callers.push(src_node.name.clone());
                    if src_node.kind == crate::model::EntityKind::Test && !tests.contains(&src_node.name) {
                        tests.push(src_node.name.clone());
                    }
                }
            }

            let callees_raw = graph.get_callees(node.id);
            let mut callees = Vec::new();
            for (dst_id, _, _) in callees_raw.iter().take(4) {
                if let Some(dst_node) = graph.get_node(*dst_id) {
                    callees.push(dst_node.name.clone());
                }
            }

            anchors.push(AnchorObject {
                name: node.name.clone(),
                kind: node.kind.as_str().to_string(),
                rank: hit.rank,
                parm_rule: node.parm_rule.clone(),
                callers,
                callees,
            });
        }
    }

    // Estimate tokens (roughly 1 token per 3.5 chars in structured XML)
    let base_chars = 300 + anchors.iter().map(|a| a.name.len() + 80).sum::<usize>();
    let est_tokens = (base_chars as f32 / 3.5).ceil() as usize;

    PackTaskResult {
        task: task.to_string(),
        est_tokens,
        budget,
        anchors,
        related_tests: tests,
    }
}
