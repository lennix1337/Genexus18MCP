//! Regression guards for issue #500.
//!
//! Three of the four reported shapes were invisible to the graph, and one was
//! actively misclassified. On a real 15,170-file GeneXus 18 XML export the
//! report measured 36 textual occurrences of a function-form call collapsing to
//! 1 caller, `.Popup(` returning nothing at all, and `&x = procName(...)`
//! recorded as an attribute read.

use gxwire::model::{CallMode, EdgeKind};
use gxwire::parser::parse_genexus_source;

fn calls_named(source: &str, name: &str) -> Vec<EdgeKind> {
    parse_genexus_source(source)
        .references
        .into_iter()
        .filter(|r| r.target_name.eq_ignore_ascii_case(name))
        .map(|r| r.kind)
        .collect()
}

fn callees(source: &str) -> Vec<String> {
    parse_genexus_source(source)
        .references
        .into_iter()
        .filter(|r| matches!(r.kind, EdgeKind::Calls(_)))
        .map(|r| r.target_name)
        .collect()
}

#[test]
fn function_form_call_is_reported_as_a_call() {
    // The reported case: `&x = procPessoaIdComRegistroNoCAR(...) if ...;`
    let source = "\
// Rules
&x = procPessoaIdComRegistroNoCAR(&EmpresaId, &RegistroNoCAR, 0) if &PessoaId > 0;
";

    let kinds = calls_named(source, "procPessoaIdComRegistroNoCAR");
    assert_eq!(
        kinds,
        vec![EdgeKind::Calls(CallMode::Static)],
        "a Procedure invoked as a function must be a call edge, not an attribute read"
    );
}

#[test]
fn function_form_call_is_not_also_reported_as_an_attribute_read() {
    // The old behaviour recorded the callee as `reads_att`, which made a
    // Transaction look like an attribute consumer and hid the call edge.
    let source = "&x = procPessoaIdComRegistroNoCAR(&EmpresaId, 0)\n";
    let parsed = parse_genexus_source(source);

    let misclassified = parsed.references.iter().any(|r| {
        r.target_name
            .eq_ignore_ascii_case("procPessoaIdComRegistroNoCAR")
            && r.kind == EdgeKind::ReadsAttr
    });
    assert!(
        !misclassified,
        "the callee must not also appear as an attribute read"
    );
}

#[test]
fn genuine_attribute_read_is_still_an_attribute_read() {
    // The fix must not swallow real attribute reads on the same line shape.
    let source = "&Total = CustomerBalance\n";
    assert_eq!(
        calls_named(source, "CustomerBalance"),
        vec![EdgeKind::ReadsAttr],
        "a plain `&Var = Attr` assignment is still an attribute read"
    );
}

#[test]
fn popup_invocation_is_a_call() {
    // `.Popup(` opens a Transaction; it was absent, so the Transaction answered
    // `empty: true` - presented as a definitive empty rather than a miss.
    let source = "\
// Events
PessoaDadosFiscaisAdicionais.Popup(&CliCod)
";

    let kinds = calls_named(source, "PessoaDadosFiscaisAdicionais");
    assert_eq!(
        kinds,
        vec![EdgeKind::Calls(CallMode::Static)],
        ".Popup( must be recorded as a call"
    );
}

#[test]
fn dot_call_invocation_still_works() {
    let source = "procCapturaDadosDaTagHtml.Call(&sdtConsultaGTA)\n";
    assert_eq!(
        calls_named(source, "procCapturaDadosDaTagHtml"),
        vec![EdgeKind::Calls(CallMode::Static)]
    );
}

#[test]
fn keywords_and_builtins_are_not_calls() {
    // Every one of these would otherwise become a phantom callee and inflate
    // the blast radius of whatever it was attached to.
    for line in [
        "If &x > 0 Then",
        "x = Str(123)",
        "y = Now()",
        "z = Val(&s)",
        "Call(procAlpha)",
        "Udp(procBeta, &a)",
        "Submit(procGamma, &a)",
        "Link(procDelta)",
        "For Each Att In Collection",
        "New()",
        "d = Date(SystemDate.ToDate())",
        "w = Trim(&s)",
    ] {
        let builtins = callees(line);
        for name in ["str", "now", "val", "trim", "date", "new"] {
            assert!(
                !builtins.iter().any(|c| c.eq_ignore_ascii_case(name)),
                "`{}` must not produce a call edge to the built-in `{}`",
                line,
                name
            );
        }
    }
}

#[test]
fn a_real_procedure_call_alongside_a_builtin_is_still_found() {
    let source = "&x = Str(procCalculaTotal(&a, &b))\n";
    let names = callees(source);
    assert!(
        names
            .iter()
            .any(|n| n.eq_ignore_ascii_case("procCalculaTotal")),
        "a genuine call nested in an expression must survive: {:?}",
        names
    );
}

#[test]
fn reported_counts_are_recovered() {
    // The report's shape: 36 textual occurrences of a function-form call in 15
    // files collapsing to a single caller. One call per line must now be
    // counted, not deduplicated away.
    let mut source = String::new();
    for i in 0..5 {
        source.push_str(&format!(
            "&x{} = procDadosFiscaisAdicionaisPrincipalDaPessoa(&a) if cond;\n",
            i
        ));
    }
    let parsed = parse_genexus_source(&source);
    let count = parsed
        .references
        .iter()
        .filter(|r| {
            r.target_name
                .eq_ignore_ascii_case("procDadosFiscaisAdicionaisPrincipalDaPessoa")
        })
        .count();
    assert_eq!(count, 5, "every invocation must be counted");
}

#[test]
fn object_link_method_invocation_targets_object_not_first_argument() {
    // Regression guard for issue #530:
    // &IconeUpdate.link = PessoaDadosFiscaisAdicionais.Link(trnmode.Update, PessoaEmpresaId, PessoaId, PessoaDadosFiscaisAdicionaisId)
    // Must generate an edge to PessoaDadosFiscaisAdicionais with CallMode::Link, and NOT to trnmode.
    let source = "&IconeUpdate.link = PessoaDadosFiscaisAdicionais.Link(trnmode.Update, PessoaEmpresaId, PessoaId, PessoaDadosFiscaisAdicionaisId)\n";
    let parsed = parse_genexus_source(source);

    let link_refs: Vec<_> = parsed
        .references
        .iter()
        .filter(|r| {
            r.target_name
                .eq_ignore_ascii_case("PessoaDadosFiscaisAdicionais")
        })
        .collect();
    assert_eq!(
        link_refs.len(),
        1,
        "Obj.Link(...) must create an edge targeting Obj: {:?}",
        parsed.references
    );
    assert_eq!(link_refs[0].kind, EdgeKind::Calls(CallMode::Link));

    let trnmode_refs: Vec<_> = parsed
        .references
        .iter()
        .filter(|r| r.target_name.eq_ignore_ascii_case("trnmode"))
        .collect();
    assert!(
        trnmode_refs.is_empty(),
        "Obj.Link(...) must NOT record first argument 'trnmode' as a call target: {:?}",
        trnmode_refs
    );
}

#[test]
fn standalone_link_invocation_still_works() {
    let source = "Link(PessoaDadosFiscaisAdicionais, &id)\n";
    let kinds = calls_named(source, "PessoaDadosFiscaisAdicionais");
    assert_eq!(
        kinds,
        vec![EdgeKind::Calls(CallMode::Link)],
        "standalone Link(Target, ...) must remain supported"
    );
}
