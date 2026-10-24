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
    pub schema: String,
    pub task: String,
    pub est_tokens: usize,
    pub budget: usize,
    pub anchors: Vec<AnchorObject>,
    pub related_tests: Vec<String>,
    pub empty: bool,
    pub truncated: bool,
    pub omitted: usize,
    pub next: String,
}

#[allow(dead_code)]
pub fn pack_task(graph: &CsrGraph, task: &str, budget: usize) -> PackTaskResult {
    pack_task_ext(graph, task, budget, false)
}

pub fn pack_task_ext(graph: &CsrGraph, task: &str, budget: usize, full: bool) -> PackTaskResult {
    let search_limit = if full { 20 } else { 8 };
    let search_res = search_for_task(graph, task, search_limit);

    let mut anchors = Vec::new();
    let mut tests = Vec::new();
    let total_candidates = search_res.hits.len();
    let mut omitted = 0;
    let mut truncated = false;

    for hit in search_res.hits {
        if let Some(node) = graph.get_node_by_name(&hit.name) {
            let callers_raw = graph.get_callers(node.id);
            let mut callers = Vec::new();
            for (src_id, _, _) in callers_raw.iter().take(4) {
                if let Some(src_node) = graph.get_node(*src_id) {
                    callers.push(src_node.name.clone());
                    if src_node.kind == crate::model::EntityKind::Test
                        && !tests.contains(&src_node.name)
                    {
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

            let candidate = AnchorObject {
                name: node.name.clone(),
                kind: node.kind.as_str().to_string(),
                rank: hit.rank,
                parm_rule: node.parm_rule.clone(),
                callers,
                callees,
            };

            // Token check if not full
            if !full && !anchors.is_empty() {
                let current_chars = 300 + (anchors.len() + 1) * 90;
                let projected_tokens = (current_chars as f32 / 3.5).ceil() as usize;
                if projected_tokens > budget {
                    truncated = true;
                    omitted = total_candidates.saturating_sub(anchors.len());
                    break;
                }
            }

            anchors.push(candidate);
        }
    }

    let empty = anchors.is_empty();
    let base_chars = 300 + anchors.iter().map(|a| a.name.len() + 80).sum::<usize>();
    let est_tokens = (base_chars as f32 / 3.5).ceil() as usize;

    let next = if empty {
        "gxwire --doctor or try broader task keywords".to_string()
    } else {
        format!(
            "gxwire --callers {} or genexus_read {}",
            anchors[0].name, anchors[0].name
        )
    };

    PackTaskResult {
        schema: "gxwire.pack/v1".to_string(),
        task: task.to_string(),
        est_tokens,
        budget,
        anchors,
        related_tests: tests,
        empty,
        truncated,
        omitted,
        next,
    }
}
