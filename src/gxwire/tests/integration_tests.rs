use gxwire::analysis::{analyze_callers, analyze_impact, analyze_slice, pack_task, search_for_task};
use gxwire::graph::CsrGraph;
use gxwire::model::{CallMode, EdgeKind, EntityKind, EntityNode, RawEdge, WriteMode};

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
            name: "ProcC".to_string(),
            kind: EntityKind::Procedure,
            module: Some("Finance".to_string()),
            description: Some("Emite boleto bancario".to_string()),
            parm_rule: Some("in:&Total, out:&BoletoId".to_string()),
            path: None,
            lines: 40,
            complexity: 2,
        },
        EntityNode {
            id: 3,
            name: "TrnCliente".to_string(),
            kind: EntityKind::Transaction,
            module: Some("Core".to_string()),
            description: Some("Cadastro de clientes".to_string()),
            parm_rule: None,
            path: None,
            lines: 100,
            complexity: 8,
        },
        EntityNode {
            id: 4,
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
            id: 5,
            name: "CliTotDivida".to_string(),
            kind: EntityKind::Attribute,
            module: Some("Core".to_string()),
            description: Some("Total da divida consolidada".to_string()),
            parm_rule: None,
            path: None,
            lines: 0,
            complexity: 1,
        },
        EntityNode {
            id: 6,
            name: "UT_ProcB".to_string(),
            kind: EntityKind::Test,
            module: Some("Tests".to_string()),
            description: Some("Teste unitario de calculo".to_string()),
            parm_rule: None,
            path: None,
            lines: 30,
            complexity: 1,
        },
    ];

    let edges = vec![
        // ProcA calls ProcB
        RawEdge { from: 0, to: 1, kind: EdgeKind::Calls(CallMode::Static), line: 25 },
        // ProcB calls ProcC
        RawEdge { from: 1, to: 2, kind: EdgeKind::Calls(CallMode::Static), line: 40 },
        // TrnCliente calls ProcB via BC
        RawEdge { from: 3, to: 1, kind: EdgeKind::Calls(CallMode::BusinessComponent), line: 15 },
        // UT_ProcB calls ProcB
        RawEdge { from: 6, to: 1, kind: EdgeKind::Calls(CallMode::Static), line: 10 },
        // ProcB writes CliSaldo
        RawEdge { from: 1, to: 4, kind: EdgeKind::WritesAttr(WriteMode::ForEachUpdate), line: 55 },
        // TrnCliente writes CliSaldo
        RawEdge { from: 3, to: 4, kind: EdgeKind::WritesAttr(WriteMode::New), line: 30 },
        // CliTotDivida is a formula depending on CliSaldo
        RawEdge { from: 5, to: 4, kind: EdgeKind::DefinesFormula, line: 0 },
        // ProcA reads CliSaldo
        RawEdge { from: 0, to: 4, kind: EdgeKind::ReadsAttr, line: 12 },
    ];

    CsrGraph::build(nodes, &edges)
}

#[test]
fn test_callers_precision() {
    let graph = create_sample_kb_graph();
    let res = analyze_callers(&graph, "ProcB", 3).expect("ProcB should exist");

    assert_eq!(res.count, 3); // ProcA, TrnCliente, UT_ProcB
    assert!(res.callers.iter().any(|c| c.name == "ProcA" && c.call_mode == "static"));
    assert!(res.callers.iter().any(|c| c.name == "TrnCliente" && c.call_mode == "bc"));
    assert!(res.callers.iter().any(|c| c.name == "UT_ProcB"));

    // Check callers of ProcC (should have ProcB as 1-hop, and ProcA, TrnCliente, UT_ProcB as transitive)
    let res_c = analyze_callers(&graph, "ProcC", 3).expect("ProcC should exist");
    assert_eq!(res_c.count, 1);
    assert_eq!(res_c.callers[0].name, "ProcB");
    assert!(res_c.transitive.iter().any(|t| t.name == "ProcA" && t.depth == 2));
    assert!(res_c.transitive.iter().any(|t| t.name == "TrnCliente" && t.depth == 2));
}

#[test]
fn test_impact_analysis_precision() {
    let graph = create_sample_kb_graph();
    let res = analyze_impact(&graph, "CliSaldo").expect("CliSaldo should exist");

    assert_eq!(res.target, "CliSaldo");
    assert_eq!(res.reorg_risk, "high");
    assert_eq!(res.writers.len(), 2); // ProcB, TrnCliente
    assert_eq!(res.readers.len(), 1); // ProcA
    assert_eq!(res.formulas.len(), 1); // CliTotDivida
    assert!(res.tests.contains(&"UT_ProcB".to_string()));
    assert!(res.blast_radius >= 4);
}

#[test]
fn test_search_and_pack_task() {
    let graph = create_sample_kb_graph();

    // Search for task
    let search = search_for_task(&graph, "pedido faturamento", 5);
    assert!(!search.hits.is_empty());
    assert!(search.hits.iter().any(|h| h.name == "ProcA" || h.name == "ProcB"));

    // Pack task under 1000 tokens
    let packed = pack_task(&graph, "calculo boleto", 1000);
    assert!(packed.est_tokens <= 1000);
    assert!(!packed.anchors.is_empty());
    assert!(packed.anchors.iter().any(|a| a.name == "ProcB" || a.name == "ProcC"));
}

#[test]
fn test_slice_data_flow() {
    let code = r#"
        parm(in:&CliCod, out:&Total);
        &Total = 0
        For Each
            Where CliCod = &CliCod
            &Total = &Total + TitVlr
        Endfor
        If &Total > 1000
            Call(PAvisaGerente, &CliCod, &Total)
        Endif
    "#;

    let res = analyze_slice("PCalculo", "&Total", code);
    assert_eq!(res.definitions_count, 2); // &Total = 0, &Total = &Total + TitVlr
    assert!(res.uses_count >= 1); // If &Total > 1000
}
