//! Graph-level guard for issue #501.
//!
//! The lexer guards cover parsing a formula; this one covers the edge, because
//! the two failed for different reasons. An early attempt gated formula parsing
//! on `kind == Attribute`, which silently skipped every formula in a text tree
//! export: a `.txt` attribute declaration has no distinctive extension or name
//! prefix, so the kind resolver reports `Unknown` for it. The unit guards passed
//! while `impact` still returned `formulas: []`.

use gxwire::cache::build_graph_from_directory;
use std::fs;
use std::path::{Path, PathBuf};

fn kb_dir(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "gxwire_f501_graph_{}_{}",
        std::process::id(),
        label
    ));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).unwrap();
    dir
}

/// The reported pair: a Procedure, and an attribute whose formula calls it.
fn write_reported_pair(dir: &Path) {
    fs::write(
        dir.join("procPessoaIdComRegistroNoCAR.txt"),
        "// target procedure\n",
    )
    .unwrap();
    fs::write(
        dir.join("LimiteAnualDeAbatePeloCARFazendaId.txt"),
        "LimiteAnualDeAbatePeloCARFazendaId : NUMERIC(6) // \"\", [Formula: procPessoaIdComRegistroNoCAR(LimiteAnualDeAbateEmpresaId, LimiteAnualDeAbateCodigoDeRegistroNoCAR, 0) IF LimiteAnualDeAbateEmpresaId <> 0;]",
    )
    .unwrap();
}

#[test]
fn formula_call_becomes_a_defines_formula_edge() {
    let dir = kb_dir("edge");
    write_reported_pair(&dir);

    let graph = build_graph_from_directory(&dir).unwrap();

    let proc_id = graph
        .find_node_id("procPessoaIdComRegistroNoCAR")
        .expect("the Procedure must be in the graph");
    let attr_id = graph
        .find_node_id("LimiteAnualDeAbatePeloCARFazendaId")
        .expect("the attribute must be in the graph");

    let formulas = graph
        .get_callers(proc_id)
        .into_iter()
        .filter(|(from, kind, _)| {
            *from == attr_id && matches!(kind, gxwire::model::EdgeKind::DefinesFormula)
        })
        .count();

    assert_eq!(
        formulas,
        1,
        "the attribute must reach the Procedure through a DefinesFormula edge; callers of the Procedure were {:?}",
        graph
            .get_callers(proc_id)
            .iter()
            .map(|(from, kind, _)| (graph.get_node(*from).map(|n| n.name.clone()), kind.as_str()))
            .collect::<Vec<_>>()
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn impact_reports_the_attribute_under_formulas() {
    let dir = kb_dir("impact");
    write_reported_pair(&dir);

    let graph = build_graph_from_directory(&dir).unwrap();
    let impact = gxwire::analysis::analyze_impact(&graph, "procPessoaIdComRegistroNoCAR")
        .expect("impact must resolve the Procedure");

    assert!(
        !impact.formulas.is_empty(),
        "`impact` must list the attribute under formulas; it returned {} formulas, {} readers, {} writers",
        impact.formulas.len(),
        impact.readers.len(),
        impact.writers.len()
    );
    assert!(
        impact
            .formulas
            .iter()
            .any(|f| f.name == "LimiteAnualDeAbatePeloCARFazendaId"),
        "the reported attribute must appear: {:?}",
        impact.formulas
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn a_procedure_without_a_formula_reports_no_formulas() {
    // The guard must not manufacture a relationship that does not exist, which
    // would overstate the blast radius of every Procedure in the KB.
    let dir = kb_dir("negative");
    fs::write(dir.join("procAlone.txt"), "// nothing here\n").unwrap();
    fs::write(dir.join("procOther.txt"), "Call(procAlone)\n").unwrap();

    let graph = build_graph_from_directory(&dir).unwrap();
    let impact = gxwire::analysis::analyze_impact(&graph, "procAlone").unwrap();

    assert!(
        impact.formulas.is_empty(),
        "no attribute has a formula here: {:?}",
        impact.formulas
    );
    assert_eq!(
        impact.callers.len(),
        1,
        "the direct caller must still be reported"
    );

    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn the_attribute_is_still_readable_as_an_attribute() {
    // A formula must add the Procedure relationship without erasing the
    // attribute's own identity in the graph.
    let dir = kb_dir("identity");
    write_reported_pair(&dir);

    let graph = build_graph_from_directory(&dir).unwrap();
    let attr_id = graph
        .find_node_id("LimiteAnualDeAbatePeloCARFazendaId")
        .unwrap();
    let node = graph.get_node(attr_id).unwrap();

    assert!(node.path.is_some(), "the attribute must remain file-backed");

    let _ = fs::remove_dir_all(&dir);
}
