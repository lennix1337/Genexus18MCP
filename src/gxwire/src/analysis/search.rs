use crate::graph::CsrGraph;
use serde::Serialize;

#[derive(Debug, Clone, Serialize)]
pub struct SearchHit {
    pub name: String,
    pub kind: String,
    pub rank: usize,
    pub score: f32,
    pub complexity: u32,
    pub callers_count: usize,
    pub parm_rule: Option<String>,
    pub description: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct SearchResult {
    pub query: String,
    pub hits: Vec<SearchHit>,
    pub total: usize,
}

pub fn search_for_task(graph: &CsrGraph, query: &str, limit: usize) -> SearchResult {
    let query_lower = query.to_ascii_lowercase();
    let terms: Vec<&str> = query_lower.split_whitespace().collect();

    let mut scored_nodes = Vec::new();

    for node in &graph.nodes {
        let name_lower = node.name.to_ascii_lowercase();
        let desc_lower = node.description.as_deref().unwrap_or("").to_ascii_lowercase();
        let module_lower = node.module.as_deref().unwrap_or("").to_ascii_lowercase();

        let mut score = 0.0f32;

        // Exact match bonus
        if name_lower == query_lower {
            score += 100.0;
        } else if name_lower.contains(&query_lower) {
            score += 40.0;
        }

        // Term-based matching
        for term in &terms {
            if name_lower.contains(term) {
                score += 15.0;
            }
            if desc_lower.contains(term) {
                score += 5.0;
            }
            if module_lower.contains(term) {
                score += 3.0;
            }
        }

        if score > 0.0 {
            // Apply PageRank boost
            let pr = graph.pagerank.get(node.id as usize).copied().unwrap_or(1.0);
            let callers_count = graph.get_callers(node.id).len();
            let total_score = score * (1.0 + pr * 5.0) + (callers_count as f32 * 0.5);

            scored_nodes.push((node, total_score, callers_count));
        }
    }

    scored_nodes.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));

    let total = scored_nodes.len();
    let hits: Vec<SearchHit> = scored_nodes
        .into_iter()
        .take(limit)
        .enumerate()
        .map(|(idx, (node, score, callers_count))| SearchHit {
            name: node.name.clone(),
            kind: node.kind.as_str().to_string(),
            rank: idx + 1,
            score,
            complexity: node.complexity,
            callers_count,
            parm_rule: node.parm_rule.clone(),
            description: node.description.clone(),
        })
        .collect();

    SearchResult {
        query: query.to_string(),
        hits,
        total,
    }
}
