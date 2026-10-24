//! Regression guards for issue #501.
//!
//! A Procedure called from an attribute's `[Formula: ...]` affects every object
//! that reads that attribute. No tool exposed the link: `genexus_wire impact`
//! returned `formulas: []` because the lexer had no rule for a formula at all.

use gxwire::model::EdgeKind;
use gxwire::parser::formula_references;

/// The attribute shape from the report, single line.
const ATTR_ONE_LINE: &str = "LimiteAnualDeAbatePeloCARFazendaId : NUMERIC(6) // \"\", [Formula: procPessoaIdComRegistroNoCAR(LimiteAnualDeAbateEmpresaId, LimiteAnualDeAbateCodigoDeRegistroNoCAR, 0) IF LimiteAnualDeAbateEmpresaId <> 0;]";

/// The same attribute written across several physical lines, which GeneXus
/// allows and which a line-oriented parse cannot see.
const ATTR_MULTILINE: &str = "\
PessoaEnderecosGoogleMapsPeloBairro : VARCHAR(1024) // \"\", [Formula: PessoaEnderecosBairro + \" , \" + PessoaEnderecosMunicipioNome + \" - \" + PessoaEnderecosMunicipioUfSigla +
\t\" , \" + PessoaEnderecosPaisNome
\tIF (not PessoaEnderecosBairro.isempty()) and not PessoaEnderecosMunicipioId.isempty();]
";

fn callees(source: &str) -> Vec<String> {
    formula_references(source)
        .into_iter()
        .map(|r| r.target_name)
        .collect()
}

#[test]
fn formula_call_is_reported() {
    let found = callees(ATTR_ONE_LINE);
    assert!(
        found.iter().any(|n| n == "procPessoaIdComRegistroNoCAR"),
        "a Procedure called from a formula must be reported: {:?}",
        found
    );
}

#[test]
fn multiline_formula_call_is_reported() {
    // The reported attribute was exactly this shape, and a per-line parse stops
    // at the first physical line.
    let found = callees(ATTR_MULTILINE);
    assert!(
        found.is_empty(),
        "this attribute's formula calls no Procedure, only attributes: {:?}",
        found
    );

    let with_call = format!(
        "{}\nTotal : NUMERIC(6) [Formula: procSomaTotal(&a, &b);]",
        ATTR_MULTILINE
    );
    let found = callees(&with_call);
    assert!(
        found.iter().any(|n| n == "procSomaTotal"),
        "a call on a later line of a multi-attribute source must still be found: {:?}",
        found
    );
}

#[test]
fn builtins_inside_a_formula_are_not_calls() {
    // Str(...), Now() and friends inside a formula must not become phantom
    // callees, which would inflate every blast radius they touched.
    let source = "Total : NUMERIC(6) [Formula: Str(procBase(&a)) + Val(&b) + Year(Now());]";
    let found = callees(source);

    assert!(
        found.iter().any(|n| n == "procBase"),
        "the real callee must survive: {:?}",
        found
    );
    for builtin in ["Str", "Val", "Year", "Now"] {
        assert!(
            !found.iter().any(|n| n.eq_ignore_ascii_case(builtin)),
            "built-in `{}` must not be reported as a callee: {:?}",
            builtin,
            found
        );
    }
}

#[test]
fn nested_procedure_calls_are_all_reported() {
    // The second reported attribute nests a call inside a call.
    let source = "DemonstrativoSituacaoCarPeloCARFazendaId : NUMERIC(6) [Formula: procPessoaIdComRegistroNoCAR(procEmpresaPessoa(), DemonstrativoSituacaoCarRegistro, 0) IF &x > 0;]";
    let found = callees(source);

    assert!(found.iter().any(|n| n == "procPessoaIdComRegistroNoCAR"));
    assert!(
        found.iter().any(|n| n == "procEmpresaPessoa"),
        "a call nested in an argument list must also be reported: {:?}",
        found
    );
}

#[test]
fn source_without_a_formula_yields_nothing() {
    assert!(formula_references("CustomerBalance : NUMERIC(12)").is_empty());
    assert!(formula_references("").is_empty());
}

#[test]
fn a_plain_attribute_reference_is_not_a_formula_call() {
    // Attributes referenced by a formula are not Procedure calls. They are
    // already covered by the attribute-read rules; reporting them again here
    // would duplicate every attribute edge in the graph.
    let found = callees("Total : NUMERIC(6) [Formula: AttrBase + AttrOther;]");
    assert!(
        found.is_empty(),
        "attribute references inside a formula are not calls: {:?}",
        found
    );
}

#[test]
fn formula_edges_use_the_defines_formula_kind() {
    // The kind is what `impact` groups into `formulas`, so an edge of the wrong
    // kind would populate the list with something that is not a formula.
    let refs = formula_references("Total : NUMERIC(6) [Formula: procSomaTotal(&a);]");
    assert_eq!(refs.len(), 1);
    assert_eq!(
        refs[0].kind,
        EdgeKind::Calls(gxwire::model::CallMode::Static)
    );
}

#[test]
fn xml_property_formula_call_is_reported() {
    // Regression guard for issue #532:
    // In XML export, an attribute formula is stored in <Property><Name>Formula</Name><Value>...</Value></Property>
    let xml = r#"
<Attribute>
    <Property><Name>Name</Name><Value>LimiteAnualDeAbatePeloCARFazendaId</Value></Property>
    <Property><Name>Formula</Name><Value>procPessoaIdComRegistroNoCAR(LimiteAnualDeAbateEmpresaId, LimiteAnualDeAbateCodigoDeRegistroNoCAR, 0) IF not (LimiteAnualDeAbateCodigoDeRegistroNoCAR.IsEmpty())</Value></Property>
</Attribute>
"#;
    let found = callees(xml);
    assert!(
        found
            .iter()
            .any(|n| n.eq_ignore_ascii_case("procPessoaIdComRegistroNoCAR")),
        "a Procedure called from XML <Property> Formula must be reported: {:?}",
        found
    );
}
