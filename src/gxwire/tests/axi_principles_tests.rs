use gxwire::analysis::{
    analyze_callers_ext, analyze_impact_ext, analyze_slice, compute_status, pack_task_ext,
    search_for_task_ext,
};
use gxwire::graph::CsrGraph;
use gxwire::model::{CallMode, EdgeKind, EntityKind, EntityNode, RawEdge, WriteMode};
use gxwire::output::{
    escape_toon_scalar, format_callers_toon, format_search_toon, format_search_xml,
    format_status_toon, format_status_xml,
};
use std::path::Path;

fn create_sample_kb_graph() -> CsrGraph {
    let nodes = vec![
        EntityNode {
            id: 0,
            name: "ProcA".to_string(),
            kind: EntityKind::Procedure,
            module: Some("Sales".to_string()),
            description: Some("Processa pedido de venda".to_string()),
            parm_rule: Some("in:&PedidoId".to_string()),
            path: None,
            lines: 50,
            complexity: 3,
        },
        EntityNode {
            id: 1,
            name: "ProcB".to_string(),
            kind: EntityKind::Procedure,
            module: Some("Billing".to_string()),
            description: Some("Calcula faturamento e juros".to_string()),
            parm_rule: Some("in:&ClienteId, out:&Total".to_string()),
            path: None,
            lines: 80,
            complexity: 5,
        },
        EntityNode {
            id: 2,
            name: "CliSaldo".to_string(),
            kind: EntityKind::Attribute,
            module: Some("Core".to_string()),
            description: Some("Saldo atual do cliente".to_string()),
            parm_rule: None,
            path: None,
            lines: 0,
            complexity: 1,
        },
        EntityNode {
            id: 3,
            name: "UnusedProc".to_string(),
            kind: EntityKind::Procedure,
            module: None,
            description: None,
            parm_rule: None,
            path: None,
            lines: 10,
            complexity: 1,
        },
    ];

    let edges = vec![
        RawEdge { from: 0, to: 1, kind: EdgeKind::Calls(CallMode::Static), line: 25 },
        RawEdge { from: 1, to: 2, kind: EdgeKind::WritesAttr(WriteMode::ForEachUpdate), line: 55 },
    ];

    CsrGraph::build(nodes, &edges)
}

#[test]
fn test_principle_1_toon_format_token_savings() {
    let graph = create_sample_kb_graph();
    let res = analyze_callers_ext(&graph, "ProcB", 3, false).expect("ProcB should exist");

    let toon_output = format_callers_toon(&res);
    let json_output = serde_json::to_string_pretty(&res).unwrap();

    // TOON should be valid, contain table notation, and be significantly shorter than JSON
    assert!(toon_output.contains("callers:\n  [1]{call_mode,depth,kind,line,name}:"));
    assert!(toon_output.contains("static,1,Proc,25,ProcA"));
    assert!(toon_output.len() < json_output.len());
}

#[test]
fn test_principle_3_content_truncation_and_full_escape_hatch() {
    let graph = create_sample_kb_graph();

    // pack_task with small budget vs full
    let packed_budget = pack_task_ext(&graph, "pedido faturamento", 80, false);
    let packed_full = pack_task_ext(&graph, "pedido faturamento", 80, true);

    assert_eq!(packed_full.truncated, false);
    assert_eq!(packed_full.omitted, 0);

    // Callers with full
    let callers_full = analyze_callers_ext(&graph, "ProcB", 3, true).unwrap();
    assert_eq!(callers_full.truncated, false);
    assert_eq!(callers_full.omitted, 0);
}

#[test]
fn test_principle_4_precomputed_aggregates() {
    let graph = create_sample_kb_graph();

    // Impact precomputed aggregates
    let impact_used = analyze_impact_ext(&graph, "ProcB", false).unwrap();
    assert_eq!(impact_used.can_delete, false);
    assert_eq!(impact_used.empty, false);
    assert!(impact_used.blast_radius >= 1);

    // Impact on unused procedure
    let impact_unused = analyze_impact_ext(&graph, "UnusedProc", false).unwrap();
    assert_eq!(impact_unused.can_delete, true);
    assert_eq!(impact_unused.blast_radius, 0);
    assert_eq!(impact_unused.empty, true);
}

#[test]
fn test_principle_5_definitive_empty_states() {
    let graph = create_sample_kb_graph();

    // Non-existent search hits
    let search_empty = search_for_task_ext(&graph, "nonexistent_term_xyz", 10, false);
    assert_eq!(search_empty.empty, true);
    assert_eq!(search_empty.count, 0);
    assert_eq!(search_empty.total, 0);

    let xml = format_search_xml(&search_empty);
    assert!(xml.contains("empty=\"true\""));
    assert!(xml.contains("count=\"0\""));

    let toon = format_search_toon(&search_empty);
    assert!(toon.contains("empty: true"));
    assert!(toon.contains("hits: []"));
}

#[test]
fn test_principle_8_content_first_status() {
    let graph = create_sample_kb_graph();
    let root = Path::new("C:/test_kb");
    let cache_path = Path::new("C:/test_kb/.gxwire.cache");

    let status = compute_status(&graph, root, cache_path);
    assert_eq!(status.nodes_count, 4);
    assert_eq!(status.edges_count, 2);
    assert_eq!(status.empty, false);
    assert!(!status.hubs.is_empty());

    let xml = format_status_xml(&status);
    assert!(xml.contains("<status schema=\"gxwire.status/v1\""));
    assert!(xml.contains("<hubs count="));
    assert!(xml.contains("<next>"));

    let toon = format_status_toon(&status);
    assert!(toon.contains("schema: gxwire.status/v1"));
    assert!(toon.contains("nodes_count: 4"));
    assert!(toon.contains("top_hubs:\n  ["));
}

#[test]
fn test_principle_9_contextual_disclosure_next_steps() {
    let graph = create_sample_kb_graph();

    let search = search_for_task_ext(&graph, "pedido", 5, false);
    assert!(search.next.contains("gxwire --pack-task") || search.next.contains("gxwire --callers"));

    let callers = analyze_callers_ext(&graph, "ProcB", 3, false).unwrap();
    assert!(callers.next.contains("gxwire --impact ProcB"));

    let impact = analyze_impact_ext(&graph, "CliSaldo", false).unwrap();
    assert!(impact.next.contains("gxwire --slice CliSaldo:VAR"));

    let slice = analyze_slice("ProcA", "&PedidoId", "&PedidoId = 1\n msg(&PedidoId)");
    assert_eq!(slice.next, "genexus_read ProcA or genexus_edit ProcA");
}

#[test]
fn test_toon_scalar_escaping() {
    assert_eq!(escape_toon_scalar("ProcA"), "ProcA");
    assert_eq!(escape_toon_scalar("in:&CliCod, out:&Total"), "\"in:&CliCod, out:&Total\"");
    assert_eq!(escape_toon_scalar("Update invoice total"), "\"Update invoice total\"");
    assert_eq!(escape_toon_scalar(""), "\"\"");
}
