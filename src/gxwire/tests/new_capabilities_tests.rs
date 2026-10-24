use gxwire::analysis::{
    analyze_callers_ext, analyze_impact_ext, analyze_path_ext, search_for_task_ext,
};
use gxwire::cache::{get_cache_path, load_cache, save_index};
use gxwire::graph::CsrGraph;
use gxwire::model::{CallMode, EdgeKind, EntityKind, EntityNode, RawEdge, WriteMode};
use gxwire::output::{
    format_callers_mermaid, format_impact_mermaid, format_path_mermaid, format_path_toon,
};
use gxwire::parser::parse_genexus_source;
use std::fs;

fn create_sample_chain_graph() -> CsrGraph {
    let nodes = vec![
        EntityNode {
            id: 0,
            name: "PInvoice".to_string(),
            kind: EntityKind::Procedure,
            module: None,
            description: Some("Process invoice calculation".to_string()),
            parm_rule: Some("in:&InvId".to_string()),
            path: None,
            lines: 100,
            complexity: 3,
        },
        EntityNode {
            id: 1,
            name: "PProcessTax".to_string(),
            kind: EntityKind::Procedure,
            module: None,
            description: Some("Calculate tax breakdown".to_string()),
            parm_rule: Some("in:&InvId, out:&Tax".to_string()),
            path: None,
            lines: 50,
            complexity: 2,
        },
        EntityNode {
            id: 2,
            name: "CustomerBalance".to_string(),
            kind: EntityKind::Attribute,
            module: None,
            description: Some("Current customer balance".to_string()),
            parm_rule: None,
            path: None,
            lines: 0,
            complexity: 1,
        },
        EntityNode {
            id: 3,
            name: "PFAT01".to_string(),
            kind: EntityKind::Procedure,
            module: None,
            description: Some("Rotina de faturamento mensal".to_string()),
            parm_rule: None,
            path: None,
            lines: 80,
            complexity: 4,
        },
    ];

    let raw_edges = vec![
        RawEdge {
            from: 0,
            to: 1,
            kind: EdgeKind::Calls(CallMode::Static),
            line: 42,
        },
        RawEdge {
            from: 1,
            to: 2,
            kind: EdgeKind::WritesAttr(WriteMode::ForEachUpdate),
            line: 88,
        },
    ];

    CsrGraph::build(nodes, &raw_edges)
}

#[test]
fn test_capability_1_shortest_path_chain() {
    let graph = create_sample_chain_graph();

    // PInvoice -> PProcessTax -> CustomerBalance (2 hops)
    let path_opt = analyze_path_ext(&graph, "PInvoice", "CustomerBalance", false);
    assert!(path_opt.is_some(), "Path should be found");
    let path = path_opt.unwrap();

    assert!(path.found);
    assert_eq!(path.hops_count, 2);
    assert_eq!(path.hops[0].from, "PInvoice");
    assert_eq!(path.hops[0].to, "PProcessTax");
    assert_eq!(path.hops[0].line, 42);
    assert_eq!(path.hops[1].from, "PProcessTax");
    assert_eq!(path.hops[1].to, "CustomerBalance");
    assert_eq!(path.hops[1].line, 88);

    // Contextual disclosure check
    assert!(path.next.contains("genexus_wire action=slice") || path.next.contains("genexus_read"));

    // TOON format check
    let toon = format_path_toon(&path);
    assert!(toon.contains("found: true"));
    assert!(toon.contains("hops_count: 2"));
    assert!(toon.contains("PInvoice,static,42,PProcessTax"));

    // Non-existent path
    let no_path = analyze_path_ext(&graph, "PFAT01", "CustomerBalance", false).unwrap();
    assert!(!no_path.found);
    assert_eq!(no_path.hops_count, 0);
    assert!(no_path.empty);
}

#[test]
fn test_capability_2_domain_synonym_expansion() {
    let graph = create_sample_chain_graph();

    // Query "faturamento" should match "PFAT01" via domain synonym expansion (fat -> faturamento)
    let res_fat = search_for_task_ext(&graph, "faturamento", 10, false);
    assert!(!res_fat.hits.is_empty(), "Should find PFAT01 via synonym");
    assert_eq!(res_fat.hits[0].name, "PFAT01");

    // Query "billing" or "invoice" should also rank PFAT01 and PInvoice
    let res_inv = search_for_task_ext(&graph, "invoice", 10, false);
    assert!(res_inv.hits.iter().any(|h| h.name == "PInvoice"));
}

#[test]
fn test_capability_3_reads_attr_dependency_extraction() {
    let code = r#"
        // Procedure Source
        For Each
            Where CustomerId = &CustomerId
            &CurrentBalance = CustomerBalance
            CustomerBalance = &CurrentBalance - &Amount
        Endfor
    "#;

    let parsed = parse_genexus_source(code);

    // Check ReadsAttr on Where
    let reads_where = parsed
        .references
        .iter()
        .any(|r| r.target_name == "CustomerId" && r.kind == EdgeKind::ReadsAttr);
    assert!(reads_where, "CustomerId in Where clause must be ReadsAttr");

    // Check ReadsAttr on &Var = Attr
    let reads_assign = parsed
        .references
        .iter()
        .any(|r| r.target_name == "CustomerBalance" && r.kind == EdgeKind::ReadsAttr);
    assert!(
        reads_assign,
        "CustomerBalance in assignment RHS must be ReadsAttr"
    );

    // Check WritesAttr on Attr = &Var
    let writes_assign = parsed
        .references
        .iter()
        .any(|r| r.target_name == "CustomerBalance" && matches!(r.kind, EdgeKind::WritesAttr(_)));
    assert!(
        writes_assign,
        "CustomerBalance in assignment LHS must be WritesAttr"
    );
}

#[test]
fn test_capability_4_incremental_cache_freshness() {
    let tmp_dir =
        std::env::temp_dir().join(format!("gxwire_freshness_test_{}", std::process::id()));
    let _ = fs::remove_dir_all(&tmp_dir);
    fs::create_dir_all(&tmp_dir).unwrap();

    let file_p1 = tmp_dir.join("POrder.txt");
    fs::write(&file_p1, "Call(PProcessOrder)\n").unwrap();

    let file_p2 = tmp_dir.join("PProcessOrder.txt");
    fs::write(&file_p2, "// empty\n").unwrap();

    // 1. Initial build and save
    let (g1, parses1) = gxwire::cache::build_index(&tmp_dir).unwrap();
    save_index(&g1, &parses1, &tmp_dir).unwrap();
    assert!(get_cache_path(&tmp_dir).exists());

    // 2. Load fresh cache
    let g_loaded = load_cache(&tmp_dir).unwrap();
    assert_eq!(g_loaded.nodes.len(), 2);

    // 3. Modify a file (simulate developer edit)
    std::thread::sleep(std::time::Duration::from_millis(50));
    fs::write(&file_p2, "Call(PValidateCustomer)\n").unwrap();

    // 4. Load cache again: should detect mtime freshness drift and auto-rebuild!
    let g_reloaded = load_cache(&tmp_dir).unwrap();
    assert!(
        g_reloaded.symbol_to_id.contains_key("pvalidatecustomer"),
        "Auto-rebuilt cache must include newly added reference"
    );

    let _ = fs::remove_dir_all(&tmp_dir);
}

#[test]
fn test_capability_5_mermaid_visualizers() {
    let graph = create_sample_chain_graph();

    // 1. Path Mermaid
    let path = analyze_path_ext(&graph, "PInvoice", "CustomerBalance", false).unwrap();
    let mermaid_path = format_path_mermaid(&path);
    assert!(mermaid_path.starts_with("```mermaid\ngraph LR\n"));
    assert!(mermaid_path.contains("PInvoice"));
    assert!(mermaid_path.contains("PProcessTax"));
    assert!(mermaid_path.contains("CustomerBalance"));
    assert!(mermaid_path.ends_with("```\n"));

    // 2. Callers Mermaid
    let callers = analyze_callers_ext(&graph, "PProcessTax", 2, false).unwrap();
    let mermaid_callers = format_callers_mermaid(&callers);
    assert!(mermaid_callers.contains("graph TD"));
    assert!(mermaid_callers.contains("PInvoice"));
    assert!(mermaid_callers.contains("PProcessTax"));

    // 3. Impact Mermaid
    let impact = analyze_impact_ext(&graph, "PProcessTax", false).unwrap();
    let mermaid_impact = format_impact_mermaid(&impact);
    assert!(mermaid_impact.contains("graph LR"));
    assert!(mermaid_impact.contains("PProcessTax"));
}
