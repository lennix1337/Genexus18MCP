use gxwire::analysis::{analyze_callers, analyze_impact, pack_task, search_for_task};
use gxwire::graph::CsrGraph;
use gxwire::model::{CallMode, EdgeKind, EntityKind, EntityNode, RawEdge};
use gxwire::parser::parse_genexus_source;

#[test]
fn test_edge_case_circular_dependency_cycles() {
    // ProcA -> ProcB -> ProcC -> ProcA (Cycle!)
    let nodes = vec![
        EntityNode { id: 0, name: "ProcA".to_string(), kind: EntityKind::Procedure, module: None, description: None, parm_rule: None, path: None, lines: 10, complexity: 1 },
        EntityNode { id: 1, name: "ProcB".to_string(), kind: EntityKind::Procedure, module: None, description: None, parm_rule: None, path: None, lines: 10, complexity: 1 },
        EntityNode { id: 2, name: "ProcC".to_string(), kind: EntityKind::Procedure, module: None, description: None, parm_rule: None, path: None, lines: 10, complexity: 1 },
    ];

    let edges = vec![
        RawEdge { from: 0, to: 1, kind: EdgeKind::Calls(CallMode::Static), line: 5 },
        RawEdge { from: 1, to: 2, kind: EdgeKind::Calls(CallMode::Static), line: 5 },
        RawEdge { from: 2, to: 0, kind: EdgeKind::Calls(CallMode::Static), line: 5 },
    ];

    let graph = CsrGraph::build(nodes, &edges);

    // Callers of ProcA should not infinite loop!
    let callers_a = analyze_callers(&graph, "ProcA", 10).expect("ProcA should exist");
    assert_eq!(callers_a.count, 1); // ProcC
    // Transitive should find ProcB and ProcC without duplicating ProcA
    assert!(callers_a.transitive.iter().all(|t| t.name != "ProcA"));
}

#[test]
fn test_edge_case_case_insensitivity() {
    let nodes = vec![
        EntityNode { id: 0, name: "PProcessaVenda".to_string(), kind: EntityKind::Procedure, module: None, description: None, parm_rule: None, path: None, lines: 10, complexity: 1 },
    ];
    let graph = CsrGraph::build(nodes, &[]);

    // Search with lower, upper, and mixed case
    assert!(graph.get_node_by_name("pprocessavenda").is_some());
    assert!(graph.get_node_by_name("PPROCESSAVENDA").is_some());
    assert!(graph.get_node_by_name("pPrOcEsSaVeNdA").is_some());
}

#[test]
fn test_edge_case_empty_source_and_comments() {
    let empty_code = "";
    let res_empty = parse_genexus_source(empty_code);
    assert_eq!(res_empty.references.len(), 0);
    assert_eq!(res_empty.lines_count, 0);

    let comment_code = r#"
        // Call(PFalso1)
        /* Call(PFalso2) */
    "#;
    let res_comments = parse_genexus_source(comment_code);
    // Shouldn't pick up comments starting with //
    assert!(!res_comments.references.iter().any(|r| r.target_name == "PFalso1"));
}

#[test]
fn test_edge_case_tight_token_budget_pruning() {
    let mut nodes = Vec::new();
    let mut edges = Vec::new();

    for i in 0..20 {
        nodes.push(EntityNode {
            id: i as u32,
            name: format!("BigProc_{}", i),
            kind: EntityKind::Procedure,
            module: Some("BigModule".to_string()),
            description: Some("Long description explaining this procedure in detail".to_string()),
            parm_rule: Some("in:&Param1, in:&Param2, out:&Result".to_string()),
            path: None,
            lines: 100,
            complexity: 5,
        });
        if i > 0 {
            edges.push(RawEdge {
                from: (i - 1) as u32,
                to: i as u32,
                kind: EdgeKind::Calls(CallMode::Static),
                line: 10,
            });
        }
    }

    let graph = CsrGraph::build(nodes, &edges);
    let packed = pack_task(&graph, "BigProc", 300);

    // Ensure it stayed under budget
    assert!(packed.est_tokens <= 350);
}

#[test]
fn test_edge_case_nonexistent_symbols() {
    let graph = CsrGraph::build(vec![], &[]);
    assert!(analyze_callers(&graph, "Inexistente", 3).is_none());
    assert!(analyze_impact(&graph, "Inexistente").is_none());

    let search = search_for_task(&graph, "nada", 10);
    assert_eq!(search.hits.len(), 0);
}
