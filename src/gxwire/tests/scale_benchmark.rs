use gxwire::analysis::{analyze_callers, analyze_impact, pack_task, search_for_task};
use gxwire::graph::CsrGraph;
use gxwire::model::{CallMode, EdgeKind, EntityKind, EntityNode, RawEdge, WriteMode};
use std::time::Instant;

fn generate_large_kb(num_nodes: usize, edges_per_node: usize) -> CsrGraph {
    let mut nodes = Vec::with_capacity(num_nodes);
    let mut edges = Vec::with_capacity(num_nodes * edges_per_node);

    for i in 0..num_nodes {
        let kind = match i % 6 {
            0 => EntityKind::Procedure,
            1 => EntityKind::Transaction,
            2 => EntityKind::WebPanel,
            3 => EntityKind::Attribute,
            4 => EntityKind::Table,
            _ => EntityKind::Sdt,
        };

        nodes.push(EntityNode {
            id: i as u32,
            name: format!("Obj_{:05}", i),
            kind,
            module: Some(format!("Mod_{}", i % 20)),
            description: Some(format!("Description for business object {:05}", i)),
            parm_rule: if kind == EntityKind::Procedure {
                Some("in:&CliCod, out:&Total".to_string())
            } else {
                None
            },
            path: None,
            lines: 100,
            complexity: (i % 10 + 1) as u32,
        });

        // Generate edges to simulate realistic call/impact graph
        for e in 1..=edges_per_node {
            let target = (i + e * 7) % num_nodes;
            let edge_kind = match e % 3 {
                0 => EdgeKind::Calls(CallMode::Static),
                1 => EdgeKind::ReadsAttr,
                _ => EdgeKind::WritesAttr(WriteMode::ForEachUpdate),
            };
            edges.push(RawEdge {
                from: i as u32,
                to: target as u32,
                kind: edge_kind,
                line: (e * 10) as u32,
            });
        }
    }

    CsrGraph::build(nodes, &edges)
}

#[test]
fn benchmark_scale_50k_nodes() {
    let num_nodes = 50_000;
    let edges_per_node = 6; // 300,000 edges!

    println!("\n=== BENCHMARK: GENEXUS KB COM 50.000 OBJETOS (300.000 RELAÇÕES) ===");

    let t0 = Instant::now();
    let graph = generate_large_kb(num_nodes, edges_per_node);
    let build_time = t0.elapsed();
    println!("1. Tempo de Construção do Grafo CSR + PageRank: {:?}", build_time);

    // Benchmark 1: Instant 1-hop Callers
    let t_callers = Instant::now();
    let callers_res = analyze_callers(&graph, "Obj_00100", 3).unwrap();
    let callers_latency = t_callers.elapsed();
    println!(
        "2. Query --callers: {:?} (encontrou {} diretos, {} transitivos)",
        callers_latency, callers_res.count, callers_res.transitive_count
    );
    assert!(callers_latency.as_micros() < 5_000, "Callers query should be < 5ms");

    // Benchmark 2: Impact Analysis & Blast Radius
    let t_impact = Instant::now();
    let impact_res = analyze_impact(&graph, "Obj_00100").unwrap();
    let impact_latency = t_impact.elapsed();
    println!(
        "3. Query --impact (Blast Radius): {:?} (blast radius: {} objetos)",
        impact_latency, impact_res.blast_radius
    );
    assert!(impact_latency.as_micros() < 10_000, "Impact query should be < 10ms");

    // Benchmark 3: Search for task
    let t_search = Instant::now();
    let search_res = search_for_task(&graph, "business object 00100", 10);
    let search_latency = t_search.elapsed();
    println!(
        "4. Query --for (Busca Semantica + PageRank): {:?} ({} resultados)",
        search_latency, search_res.hits.len()
    );
    let max_search_ms = if cfg!(debug_assertions) { 200 } else { 50 };
    assert!(search_latency.as_millis() < max_search_ms, "Search query should be < {}ms", max_search_ms);

    // Benchmark 4: Pack task under token budget
    let t_pack = Instant::now();
    let pack_res = pack_task(&graph, "business object 00100", 1200);
    let pack_latency = t_pack.elapsed();
    println!(
        "5. Query --pack-task (Orientacao One-Shot): {:?} (est_tokens: {})",
        pack_latency, pack_res.est_tokens
    );
    assert!(pack_latency.as_millis() < max_search_ms, "Pack task query should be < {}ms", max_search_ms);

    println!("===================================================================\n");
}
