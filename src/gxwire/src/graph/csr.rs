use crate::model::{EdgeKind, EntityNode, RawEdge};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CsrGraph {
    pub nodes: Vec<EntityNode>,
    pub symbol_to_id: HashMap<String, u32>,

    // Forward adjacency (From -> To)
    pub forward_offsets: Vec<u32>,
    pub forward_targets: Vec<u32>,
    pub forward_kinds: Vec<EdgeKind>,
    pub forward_lines: Vec<u32>,

    // Backward adjacency (To -> From) - Instant Callers
    pub backward_offsets: Vec<u32>,
    pub backward_sources: Vec<u32>,
    pub backward_kinds: Vec<EdgeKind>,
    pub backward_lines: Vec<u32>,

    // Precomputed metrics
    pub pagerank: Vec<f32>,
}

impl CsrGraph {
    pub fn build(nodes: Vec<EntityNode>, raw_edges: &[RawEdge]) -> Self {
        let node_count = nodes.len();
        let mut symbol_to_id = HashMap::with_capacity(node_count);
        for (idx, node) in nodes.iter().enumerate() {
            symbol_to_id.insert(node.name.to_ascii_lowercase(), idx as u32);
        }

        // 1. Build Forward CSR
        let mut forward_buckets: Vec<Vec<(u32, EdgeKind, u32)>> = vec![Vec::new(); node_count];
        // 2. Build Backward CSR
        let mut backward_buckets: Vec<Vec<(u32, EdgeKind, u32)>> = vec![Vec::new(); node_count];

        for edge in raw_edges {
            if (edge.from as usize) < node_count && (edge.to as usize) < node_count {
                forward_buckets[edge.from as usize].push((edge.to, edge.kind.clone(), edge.line));
                backward_buckets[edge.to as usize].push((edge.from, edge.kind.clone(), edge.line));
            }
        }

        let mut forward_offsets = Vec::with_capacity(node_count + 1);
        let mut forward_targets = Vec::new();
        let mut forward_kinds = Vec::new();
        let mut forward_lines = Vec::new();

        let mut current_offset = 0u32;
        for bucket in forward_buckets {
            forward_offsets.push(current_offset);
            for (to, kind, line) in bucket {
                forward_targets.push(to);
                forward_kinds.push(kind);
                forward_lines.push(line);
                current_offset += 1;
            }
        }
        forward_offsets.push(current_offset);

        let mut backward_offsets = Vec::with_capacity(node_count + 1);
        let mut backward_sources = Vec::new();
        let mut backward_kinds = Vec::new();
        let mut backward_lines = Vec::new();

        current_offset = 0u32;
        for bucket in backward_buckets {
            backward_offsets.push(current_offset);
            for (from, kind, line) in bucket {
                backward_sources.push(from);
                backward_kinds.push(kind);
                backward_lines.push(line);
                current_offset += 1;
            }
        }
        backward_offsets.push(current_offset);

        let mut graph = Self {
            nodes,
            symbol_to_id,
            forward_offsets,
            forward_targets,
            forward_kinds,
            forward_lines,
            backward_offsets,
            backward_sources,
            backward_kinds,
            backward_lines,
            pagerank: vec![1.0; node_count],
        };

        graph.compute_pagerank(20, 0.85);
        graph
    }

    pub fn find_node_id(&self, name: &str) -> Option<u32> {
        self.symbol_to_id.get(&name.to_ascii_lowercase()).copied()
    }

    pub fn get_node(&self, id: u32) -> Option<&EntityNode> {
        self.nodes.get(id as usize)
    }

    pub fn get_node_by_name(&self, name: &str) -> Option<&EntityNode> {
        self.find_node_id(name).and_then(|id| self.get_node(id))
    }

    pub fn get_callers(&self, node_id: u32) -> Vec<(u32, EdgeKind, u32)> {
        let idx = node_id as usize;
        if idx >= self.nodes.len() {
            return Vec::new();
        }
        let start = self.backward_offsets[idx] as usize;
        let end = self.backward_offsets[idx + 1] as usize;
        let mut res = Vec::with_capacity(end - start);
        for i in start..end {
            res.push((
                self.backward_sources[i],
                self.backward_kinds[i].clone(),
                self.backward_lines[i],
            ));
        }
        res
    }

    pub fn get_callees(&self, node_id: u32) -> Vec<(u32, EdgeKind, u32)> {
        let idx = node_id as usize;
        if idx >= self.nodes.len() {
            return Vec::new();
        }
        let start = self.forward_offsets[idx] as usize;
        let end = self.forward_offsets[idx + 1] as usize;
        let mut res = Vec::with_capacity(end - start);
        for i in start..end {
            res.push((
                self.forward_targets[i],
                self.forward_kinds[i].clone(),
                self.forward_lines[i],
            ));
        }
        res
    }

    /// Fast BFS transitive callers (Backward traversal)
    pub fn transitive_callers(&self, root_id: u32, max_depth: usize, max_nodes: usize) -> Vec<(u32, usize)> {
        let mut visited = vec![false; self.nodes.len()];
        let mut queue = std::collections::VecDeque::new();
        let mut result = Vec::new();

        visited[root_id as usize] = true;
        queue.push_back((root_id, 0usize));

        while let Some((curr, depth)) = queue.pop_front() {
            if curr != root_id {
                result.push((curr, depth));
                if result.len() >= max_nodes {
                    break;
                }
            }

            if depth >= max_depth {
                continue;
            }

            for (caller_id, _, _) in self.get_callers(curr) {
                if !visited[caller_id as usize] {
                    visited[caller_id as usize] = true;
                    queue.push_back((caller_id, depth + 1));
                }
            }
        }

        result
    }

    /// Compute PageRank with power iteration
    pub fn compute_pagerank(&mut self, iterations: usize, damping: f32) {
        let n = self.nodes.len();
        if n == 0 {
            return;
        }

        let mut pr = vec![1.0f32 / n as f32; n];
        let mut next_pr = vec![0.0f32; n];
        let base_score = (1.0 - damping) / n as f32;

        for _ in 0..iterations {
            for v in 0..n {
                next_pr[v] = base_score;
            }

            for u in 0..n {
                let start = self.forward_offsets[u] as usize;
                let end = self.forward_offsets[u + 1] as usize;
                let out_degree = end - start;
                if out_degree > 0 {
                    let share = damping * pr[u] / out_degree as f32;
                    for i in start..end {
                        let target = self.forward_targets[i] as usize;
                        next_pr[target] += share;
                    }
                } else {
                    let share = damping * pr[u] / n as f32;
                    for v in 0..n {
                        next_pr[v] += share;
                    }
                }
            }
            pr.copy_from_slice(&next_pr);
        }

        self.pagerank = pr;
    }
}
