use super::*;
use crate::catalog::{ObjectId, Reference, ReferenceKind, SourcePart};

fn id(name: &str) -> ObjectId {
    ObjectId::new(name).unwrap()
}

/// A Procedure with a Source, the named variables declared and the given Rules.
fn procedure(name: &str, source: &str, variables: &[&str], rules: &str) -> KbObject {
    let mut object = KbObject::new(id(name), name, ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: source.into(),
    });
    let declared: serde_json::Map<String, serde_json::Value> = variables
        .iter()
        .map(|name| {
            (
                name.to_string(),
                serde_json::json!({"kind": "numeric", "digits": 4}),
            )
        })
        .collect();
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: serde_json::Value::Object(declared).to_string(),
    });
    object.source_parts.push(SourcePart {
        name: "RULES".into(),
        source: rules.into(),
    });
    object.main = Some(true);
    object
}

fn catalog(objects: Vec<KbObject>) -> Catalog {
    Catalog::from_objects_map(objects.into_iter().map(|o| (o.id.clone(), o)).collect())
}

fn analysis(objects: Vec<KbObject>) -> Analysis {
    analyze(&catalog(objects))
}

fn rules_of(analysis: &Analysis, object: &str) -> Vec<&'static str> {
    analysis
        .findings
        .iter()
        .filter(|f| f.object == object)
        .map(|f| f.rule)
        .collect()
}

fn only(analysis: &Analysis, rule: &str) -> Vec<Finding> {
    analysis
        .findings
        .iter()
        .filter(|f| f.rule == rule)
        .cloned()
        .collect()
}

// ----- GX008 unused variable -------------------------------------------------------------------

#[test]
fn a_declared_variable_nothing_mentions_is_unused() {
    let result = analysis(vec![procedure("P", "Return", &["Used", "Dead"], "")]);
    let found = only(&result, UNUSED_VARIABLE);
    assert_eq!(found.len(), 2, "{found:?}");
    let result = analysis(vec![procedure(
        "P",
        "&Used = 1\nMsg(&Used, status)",
        &["Used", "Dead"],
        "",
    )]);
    let found = only(&result, UNUSED_VARIABLE);
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].object, "P");
    assert_eq!(found[0].part, "Variables");
    assert_eq!(found[0].line, None);
    assert_eq!(
        found[0].message,
        "Variable &Dead is declared but never used."
    );
}

#[test]
fn a_variable_used_in_a_nested_block_or_a_subroutine_is_used() {
    let source = "If 1 = 1\n  For &I = 1 to 3\n    &Nested = &I\n  EndFor\nEndIf\nDo 'S'\nReturn\nSub 'S'\n  &InSub = 5\n  &Other = &InSub\nEndSub";
    let result = analysis(vec![procedure(
        "P",
        source,
        &["I", "Nested", "InSub", "Other"],
        "",
    )]);
    assert!(
        only(&result, UNUSED_VARIABLE).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_variable_passed_to_a_call_or_named_in_the_rules_is_used() {
    let callee = procedure("Callee", "Return", &["A"], "parm(out:&A);");
    let caller = procedure(
        "Caller",
        "Callee(&Out)",
        &["Out", "Param"],
        "parm(in:&Param);",
    );
    let result = analysis(vec![callee, caller]);
    assert!(
        only(&result, UNUSED_VARIABLE)
            .iter()
            .all(|f| f.object != "Caller"),
        "{:?}",
        result.findings
    );
}

#[test]
fn variables_are_matched_case_insensitively_and_framework_variables_are_skipped() {
    let result = analysis(vec![procedure(
        "P",
        "&ABC = 1\nMsg(&abc, status)",
        &["Abc", "Pgmname", "IsAuthorized"],
        "",
    )]);
    assert!(
        only(&result, UNUSED_VARIABLE).is_empty(),
        "{:?}",
        result.findings
    );
}

/// GeneXus creates its standard variables (`VariableDefinition.IsStandard` in the SDK: &Line, &Output,
/// &Page...) in every Procedure and the user cannot remove them, so an unused one is not a problem.
/// On KBTeste they were 550 of the 640 unused-variable findings.
#[test]
fn sdk_standard_variables_are_never_reported_unused() {
    let result = analysis(vec![procedure(
        "P",
        "&Abc = 1\nMsg(&Abc, status)",
        &[
            "Abc",
            "Line",
            "Output",
            "Page",
            "Err",
            "Errmsg",
            "GxRemove",
            "Mr",
            "Msg",
            "Op",
            "Outdev",
            "RestMethod",
            "RestCode",
        ],
        "",
    )]);
    assert!(
        only(&result, UNUSED_VARIABLE).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_mention_in_a_comment_or_a_text_literal_is_not_a_use() {
    let source = "// &Ghost\n/* &Ghost */\nMsg('&Ghost', status)";
    let result = analysis(vec![procedure("P", source, &["Ghost"], "")]);
    assert_eq!(only(&result, UNUSED_VARIABLE).len(), 1);
}

#[test]
fn an_unreadable_variables_part_withholds_the_variable_rules_instead_of_guessing() {
    let mut object = procedure("P", "&X = 1", &[], "");
    object
        .source_parts
        .iter_mut()
        .find(|p| p.name == "VARIABLES")
        .unwrap()
        .source = "not json".into();
    let result = analysis(vec![object]);
    assert!(result.findings.is_empty(), "{:?}", result.findings);
    assert_eq!(result.withheld.len(), 1);
    assert_eq!(
        result.withheld[0].rules,
        [UNUSED_VARIABLE, ASSIGNED_NEVER_READ]
    );
    assert_eq!(result.summary.analyzed, 1);
}

// ----- GXE001 assigned but never read ----------------------------------------------------------

#[test]
fn a_variable_only_assigned_is_flagged_at_its_first_assignment() {
    let result = analysis(vec![procedure(
        "P",
        "&Keep = 1\n  &Dead = 2\nMsg(&Keep, status)\n&Dead = 3",
        &["Keep", "Dead"],
        "",
    )]);
    let found = only(&result, ASSIGNED_NEVER_READ);
    assert_eq!(found.len(), 1, "{:?}", result.findings);
    assert_eq!((found[0].line, found[0].column), (Some(2), Some(3)));
    assert_eq!(
        found[0].message,
        "Variable &Dead is assigned but its value is never read."
    );
    assert!(only(&result, UNUSED_VARIABLE).is_empty());
}

#[test]
fn a_variable_read_anywhere_is_not_assigned_never_read() {
    let cases = [
        // read in a condition
        "&A = 1\nIf &A = 1\n  Return\nEndIf",
        // read only inside a subroutine
        "&A = 1\nDo 'S'\nReturn\nSub 'S'\n  Msg(&A, status)\nEndSub",
        // a self read
        "&A = 0\n&A = &A + 1",
        // read inside a nested loop body
        "&A = 1\nFor &I = 1 to 2\n  Msg(&A, status)\nEndFor",
    ];
    for source in cases {
        let result = analysis(vec![procedure("P", source, &["A", "I"], "")]);
        assert!(
            only(&result, ASSIGNED_NEVER_READ).is_empty(),
            "{source}: {:?}",
            result.findings
        );
    }
}

#[test]
fn a_variable_passed_to_a_call_may_be_an_out_argument_and_is_not_flagged() {
    let callee = procedure("Callee", "Return", &["A"], "parm(out:&A);");
    let caller = procedure("Caller", "&Out = 0\nCallee(&Out)", &["Out"], "");
    let result = analysis(vec![callee, caller]);
    assert!(
        only(&result, ASSIGNED_NEVER_READ).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_parameter_that_is_assigned_is_not_flagged() {
    let result = analysis(vec![procedure("P", "&R = 5", &["R"], "parm(out:&R);")]);
    assert!(result.findings.is_empty(), "{:?}", result.findings);
}

#[test]
fn a_record_or_collection_changed_in_place_is_not_flagged() {
    for source in [
        "&Items = &Other\n&Items.Clear()",
        "&Items = &Other\n&Items.Add(1)",
        "&Items = &Other\n&Items.Remove(1)",
    ] {
        let result = analysis(vec![procedure("P", source, &["Items", "Other"], "")]);
        assert_eq!(result.summary.analyzed, 1, "{source}: {:?}", result.skipped);
        assert!(
            only(&result, ASSIGNED_NEVER_READ).is_empty(),
            "{source}: {:?}",
            result.findings
        );
    }
}

#[test]
fn a_loop_variable_that_is_also_assigned_is_not_flagged() {
    let source = "&I = 0\nFor &I = 1 to 3\n  Msg('x', status)\nEndFor";
    let result = analysis(vec![procedure("P", source, &["I"], "")]);
    assert_eq!(result.summary.analyzed, 1, "{:?}", result.skipped);
    assert!(
        only(&result, ASSIGNED_NEVER_READ).is_empty(),
        "{:?}",
        result.findings
    );
}

// ----- GX009 unused subroutine -----------------------------------------------------------------

#[test]
fn a_subroutine_nothing_calls_is_flagged_where_it_is_defined() {
    let result = analysis(vec![procedure(
        "P",
        "Msg('x', status)\nReturn\nSub 'Dead'\n  Msg('y', status)\nEndSub",
        &[],
        "",
    )]);
    let found = only(&result, UNUSED_SUBROUTINE);
    assert_eq!(found.len(), 1, "{:?}", result.findings);
    assert_eq!((found[0].line, found[0].column), (Some(3), Some(1)));
    assert_eq!(found[0].message, "Subroutine 'DEAD' is never called.");
}

#[test]
fn a_subroutine_called_from_main_code_or_from_another_subroutine_is_used() {
    let source = "Do 'A'\nReturn\nSub 'A'\n  Do B\nEndSub\nSub 'B'\n  Msg('b', status)\nEndSub";
    let result = analysis(vec![procedure("P", source, &[], "")]);
    assert!(
        only(&result, UNUSED_SUBROUTINE).is_empty(),
        "{:?}",
        result.findings
    );
}

// ----- GXE002 unreachable statement ------------------------------------------------------------

#[test]
fn a_statement_after_an_unconditional_return_is_flagged_once_per_block() {
    let result = analysis(vec![procedure(
        "P",
        "Msg('a', status)\nReturn\nMsg('b', status)\nMsg('c', status)",
        &[],
        "",
    )]);
    let found = only(&result, UNREACHABLE_STATEMENT);
    assert_eq!(found.len(), 1, "{:?}", result.findings);
    assert_eq!((found[0].line, found[0].column), (Some(3), Some(1)));
}

#[test]
fn dead_code_after_a_return_inside_a_nested_block_is_flagged_at_its_own_position() {
    let source = "If 1 = 1\n  Return\n  Msg('dead', status)\nEndIf\nMsg('live', status)";
    let result = analysis(vec![procedure("P", source, &[], "")]);
    let found = only(&result, UNREACHABLE_STATEMENT);
    assert_eq!(found.len(), 1, "{:?}", result.findings);
    assert_eq!((found[0].line, found[0].column), (Some(3), Some(3)));
}

#[test]
fn code_after_a_conditional_return_is_live() {
    let source = "If &A = 1\n  Return\nEndIf\nMsg('live', status)\nDo Case\n  Case &A = 2\n    Return\n  Otherwise\n    Return\nEndCase\nMsg('also live', status)";
    let result = analysis(vec![procedure("P", source, &["A"], "")]);
    assert!(
        only(&result, UNREACHABLE_STATEMENT).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_subroutine_defined_after_the_return_is_not_dead_code() {
    let source = "Do 'S'\nReturn\nSub 'S'\n  Msg('s', status)\nEndSub";
    let result = analysis(vec![procedure("P", source, &[], "")]);
    assert!(
        only(&result, UNREACHABLE_STATEMENT).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn code_after_exit_in_a_loop_is_dead_but_code_after_the_loop_is_live() {
    let source =
        "Do While &A < 3\n  &A = &A + 1\n  Exit\n  Msg('dead', status)\nEndDo\nMsg('live', status)";
    let result = analysis(vec![procedure("P", source, &["A"], "")]);
    let found = only(&result, UNREACHABLE_STATEMENT);
    assert_eq!(found.len(), 1, "{:?}", result.findings);
    assert_eq!(found[0].line, Some(4));
}

#[test]
fn the_parser_hook_does_not_leak_into_a_plain_parse() {
    crate::frontend::parse_body("Return\nMsg('x', status)").unwrap();
    let (_, positions) = crate::frontend::parse_body_with_unreachable("Return").unwrap();
    assert!(positions.is_empty());
}

// ----- GXE003 wrong argument count -------------------------------------------------------------

fn callee_two() -> KbObject {
    procedure("Callee", "Return", &["A", "B"], "parm(in:&A, out:&B);")
}

#[test]
fn a_call_with_the_wrong_number_of_arguments_is_flagged_at_the_call() {
    let caller = procedure(
        "Caller",
        "Msg('x', status)\n  Callee(&X, &Y, &Z)",
        &["X", "Y", "Z"],
        "",
    );
    let result = analysis(vec![callee_two(), caller]);
    let found = only(&result, WRONG_ARGUMENT_COUNT);
    assert_eq!(found.len(), 1, "{:?}", result.findings);
    assert_eq!((found[0].line, found[0].column), (Some(2), Some(3)));
    assert_eq!(found[0].object, "Caller");
    assert_eq!(
        found[0].message,
        "Callee is called with 3 argument(s) but its parm rule declares 2."
    );
}

#[test]
fn the_explicit_call_form_and_a_missing_argument_are_checked_too() {
    let caller = procedure(
        "Caller",
        "Callee.Call()\nCallee(&X, &Y, &Y, &Y)",
        &["X", "Y"],
        "",
    );
    let result = analysis(vec![callee_two(), caller]);
    assert_eq!(
        only(&result, WRONG_ARGUMENT_COUNT).len(),
        2,
        "{:?}",
        result.findings
    );
}

#[test]
fn a_matching_call_and_a_function_call_that_omits_the_last_out_parameter_are_accepted() {
    let caller = procedure("Caller", "Callee(&X, &Y)\n&Y = Callee(&X)", &["X", "Y"], "");
    let result = analysis(vec![callee_two(), caller]);
    assert!(
        only(&result, WRONG_ARGUMENT_COUNT).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_callee_name_held_by_two_objects_is_ambiguous_and_never_checked() {
    let mut web = KbObject::new(id("callee-web"), "Callee", ObjectKind::WebPanel);
    web.main = None;
    let caller = procedure("Caller", "Callee(&X, &Y, &Z)", &["X", "Y", "Z"], "");
    let result = analysis(vec![callee_two(), web, caller]);
    assert!(
        only(&result, WRONG_ARGUMENT_COUNT).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_callee_without_a_trustworthy_signature_is_never_checked() {
    let mut refused = callee_two();
    refused.refusal = Some("cannot decode a variable".into());
    let odd_rules = procedure("Odd", "Return", &["A"], "default(&A, 1);");
    let no_rules = {
        let mut object = procedure("NoRules", "Return", &[], "");
        object.source_parts.retain(|p| p.name != "RULES");
        object
    };
    let caller = procedure("Caller", "Callee(&X)\nOdd(&X, &X)\nNoRules(&X)", &["X"], "");
    let result = analysis(vec![refused, odd_rules, no_rules, caller]);
    assert!(
        only(&result, WRONG_ARGUMENT_COUNT).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_call_to_a_name_the_catalog_does_not_hold_is_not_reported() {
    // GeneXus ships functions this engine has no list of, so a missing callee is never proven.
    let caller = procedure(
        "Caller",
        "&X = SomeGeneXusFunction(&X, 1)\nMissing(&X)",
        &["X"],
        "",
    );
    let result = analysis(vec![caller]);
    assert!(
        only(&result, WRONG_ARGUMENT_COUNT).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_typed_builtin_with_a_procedure_name_is_not_a_call_to_that_procedure() {
    let shadow = procedure("Trim", "Return", &[], "parm(in:&A, in:&B);");
    let caller = procedure("Caller", "&T = Trim(&T)", &["T"], "");
    let result = analysis(vec![shadow, caller]);
    // The caller must be analyzed, otherwise the absence of a finding proves nothing.
    assert_eq!(result.summary.analyzed, 2, "{:?}", result.skipped);
    assert!(
        only(&result, WRONG_ARGUMENT_COUNT).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_statement_keyword_that_is_also_a_procedure_name_is_not_a_call_to_it() {
    // `Msg('x', status)` and `Print(...)` are statements the parser does not turn into calls, so the token
    // scan and the parser tree disagree and the site is dropped instead of being read as a call.
    let msg = procedure("Msg", "Return", &[], "parm(in:&A);");
    let print = procedure("Print", "Return", &[], "parm(in:&A, in:&B);");
    let caller = procedure("Caller", "Msg('hello', status)\nPrint('x')", &[], "");
    let result = analysis(vec![msg, print, caller]);
    assert_eq!(result.summary.analyzed, 3, "{:?}", result.skipped);
    assert!(
        only(&result, WRONG_ARGUMENT_COUNT).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn recursion_is_checked_like_any_other_call() {
    let rec = procedure("Rec", "Rec(&A, &A)", &["A"], "parm(in:&A);");
    let result = analysis(vec![rec]);
    assert_eq!(
        only(&result, WRONG_ARGUMENT_COUNT).len(),
        1,
        "{:?}",
        result.findings
    );
}

// ----- GXE004 unreachable procedure ------------------------------------------------------------

fn not_main(name: &str) -> KbObject {
    let mut object = procedure(name, "Return", &[], "");
    object.main = Some(false);
    object
}

fn calls(mut object: KbObject, target: &str) -> KbObject {
    object.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id(target),
    });
    object
}

#[test]
fn a_not_main_procedure_nothing_reaches_is_flagged() {
    let result = analysis(vec![
        not_main("Orphan"),
        procedure("Main", "Return", &[], ""),
    ]);
    let found = only(&result, UNREACHABLE_PROCEDURE);
    assert_eq!(found.len(), 1, "{:?}", result.findings);
    assert_eq!(found[0].object, "Orphan");
    assert_eq!(found[0].severity, "Info");
}

#[test]
fn a_procedure_reached_from_main_or_from_a_non_procedure_object_is_reachable() {
    let from_main = calls(procedure("Main", "Return", &[], ""), "viamain");
    let mut panel = KbObject::new(id("panel"), "Panel", ObjectKind::WebPanel);
    panel.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("viapanel"),
    });
    let result = analysis(vec![
        from_main,
        not_main("ViaMain"),
        panel,
        not_main("ViaPanel"),
    ]);
    assert!(
        only(&result, UNREACHABLE_PROCEDURE).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn recursion_alone_and_a_dead_cycle_do_not_make_a_procedure_reachable() {
    let result = analysis(vec![
        calls(not_main("SelfCall"), "selfcall"),
        calls(not_main("CycleA"), "cycleb"),
        calls(not_main("CycleB"), "cyclea"),
    ]);
    assert_eq!(
        only(&result, UNREACHABLE_PROCEDURE).len(),
        3,
        "{:?}",
        result.findings
    );
}

#[test]
fn entry_points_are_never_unreachable() {
    let mut unflagged = procedure("NoFlag", "Return", &[], "");
    unflagged.main = None;
    let mut not_generated = not_main("NotGenerated");
    not_generated.not_generated = Some("GenerateObject is False".into());
    let mut library = not_main("Library");
    library.library = true;
    let result = analysis(vec![
        procedure("Main", "Return", &[], ""),
        unflagged,
        not_generated,
        library,
    ]);
    assert!(
        only(&result, UNREACHABLE_PROCEDURE).is_empty(),
        "{:?}",
        result.findings
    );
}

#[test]
fn a_refused_unreachable_procedure_is_still_reported_unreachable_and_also_skipped() {
    let mut refused = not_main("Refused");
    refused.refusal = Some("bad variable".into());
    let result = analysis(vec![refused]);
    assert_eq!(rules_of(&result, "Refused"), [UNREACHABLE_PROCEDURE]);
    assert_eq!(result.skipped.len(), 1);
}

// ----- skipped objects -------------------------------------------------------------------------

#[test]
fn a_procedure_the_parser_refuses_is_skipped_with_the_reason_and_never_has_findings() {
    let bad = procedure("Bad", "&X = (\n&Y = 1", &["Dead"], "");
    let result = analysis(vec![bad]);
    assert!(result.findings.is_empty(), "{:?}", result.findings);
    assert_eq!(result.skipped.len(), 1);
    assert_eq!(result.skipped[0].category, SKIP_UNPARSED);
    assert!(
        result.skipped[0].reason.starts_with("line "),
        "{}",
        result.skipped[0].reason
    );
    assert_eq!(result.summary.analyzed, 0);
    assert_eq!(result.summary.procedures, 1);
}

#[test]
fn refused_not_generated_sourceless_and_library_procedures_are_skipped_by_category() {
    let mut refused = procedure("Refused", "Return", &["Dead"], "");
    refused.refusal = Some("cannot decode".into());
    let mut not_generated = procedure("NotGen", "Return", &["Dead"], "");
    not_generated.not_generated = Some("GenerateObject is False".into());
    let mut sourceless = procedure("NoSource", "Return", &["Dead"], "");
    sourceless.source_parts.retain(|p| p.name != "SOURCE");
    let mut library = procedure("Lib", "Return", &["Dead"], "");
    library.library = true;
    let result = analysis(vec![refused, not_generated, sourceless, library]);
    assert!(result.findings.is_empty(), "{:?}", result.findings);
    assert_eq!(result.summary.skipped_by_category[SKIP_REFUSED], 1);
    assert_eq!(result.summary.skipped_by_category[SKIP_NOT_GENERATED], 1);
    assert_eq!(result.summary.skipped_by_category[SKIP_NO_SOURCE], 1);
    assert_eq!(result.summary.skipped_by_category[SKIP_LIBRARY], 1);
    assert_eq!(result.summary.skipped, 4);
}

#[test]
fn objects_that_are_not_procedures_are_out_of_scope_not_skipped() {
    let sdt = KbObject::new(id("sdt"), "Sdt", ObjectKind::StructuredDataType);
    let mut panel = KbObject::new(id("panel"), "Panel", ObjectKind::WebPanel);
    panel.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&x = (".into(),
    });
    let result = analysis(vec![sdt, panel, procedure("P", "Return", &[], "")]);
    assert_eq!(result.summary.objects, 3);
    assert_eq!(result.summary.procedures, 1);
    assert!(result.skipped.is_empty());
    assert!(result.findings.is_empty());
}

// ----- shape and determinism -------------------------------------------------------------------

fn sample_objects() -> Vec<KbObject> {
    vec![
        procedure(
            "Beta",
            "&A = 1\nReturn\nMsg('dead', status)",
            &["A", "Dead"],
            "",
        ),
        not_main("Alpha"),
        procedure("Gamma", "&X = (", &[], ""),
        calls(procedure("Delta", "Gamma(&A)", &["A"], ""), "gamma"),
    ]
}

#[test]
fn the_same_catalog_gives_byte_identical_json_whatever_the_insertion_order() {
    let forward = serde_json::to_string(&analysis(sample_objects())).unwrap();
    let mut reversed = sample_objects();
    reversed.reverse();
    let backward = serde_json::to_string(&analysis(reversed)).unwrap();
    let again = serde_json::to_string(&analysis(sample_objects())).unwrap();
    assert_eq!(forward, backward);
    assert_eq!(forward, again);
    assert!(forward.contains("GX008") && forward.contains("GXE004"));
}

#[test]
fn findings_are_ordered_by_object_then_position() {
    let result = analysis(sample_objects());
    let keys: Vec<(String, Option<usize>)> = result
        .findings
        .iter()
        .map(|f| (f.object.clone(), f.line))
        .collect();
    let mut sorted = keys.clone();
    sorted.sort();
    assert_eq!(keys, sorted);
    assert_eq!(
        result.summary.findings,
        result.summary.findings_by_rule.values().sum::<usize>()
    );
}

#[test]
fn the_answer_documents_every_rule_it_can_report() {
    let result = analysis(sample_objects());
    let ids: Vec<&str> = result.rules.iter().map(|r| r.id).collect();
    assert_eq!(
        ids,
        ["GX008", "GX009", "GXE001", "GXE002", "GXE003", "GXE004"]
    );
    for finding in &result.findings {
        assert!(ids.contains(&finding.rule));
    }
}

#[test]
fn a_catalog_read_through_the_in_memory_store_is_analyzed_end_to_end() {
    use crate::udm::{UdmClient, UdmReader, UdmWriter};
    use std::sync::Arc;
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    let mut object = KbObject::new(id("Stored"), "Stored", ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "Return\nMsg('dead', status)".into(),
    });
    writer.create_object(&object).unwrap();
    let catalog = UdmReader::new(Arc::new(client)).read_catalog().unwrap();
    let result = analyze(&catalog);
    assert_eq!(result.summary.procedures, 1);
    assert_eq!(result.summary.analyzed, 1, "{:?}", result.skipped);
    assert_eq!(
        only(&result, UNREACHABLE_STATEMENT).len(),
        1,
        "{:?}",
        result.findings
    );
}

// ----- the token scan --------------------------------------------------------------------------

#[test]
fn the_scan_counts_lines_and_columns_like_the_front_end() {
    let tokens = scan("/* a\nb */ &X = 'q&Y' // &Z\r\n  &W");
    let vars: Vec<(String, usize, usize)> = tokens
        .iter()
        .filter_map(|t| match &t.tok {
            Tok::Var(name) => Some((name.clone(), t.line, t.column)),
            _ => None,
        })
        .collect();
    assert_eq!(vars, [("X".into(), 2, 6), ("W".into(), 3, 3)]);
}

// ----- synthetic scale measurement (not a real-KB number) ---------------------------------------

/// A generated catalog: every 20th Procedure has a Source the parser refuses, the others hold a loop,
/// a branch, two subroutines (one never called), a call to the previous Procedure and a few variables.
fn synthetic_catalog(procedures: usize) -> Catalog {
    let mut objects = Vec::with_capacity(procedures);
    for index in 0..procedures {
        let name = format!("Synth{index:05}");
        let source = if index % 20 == 19 {
            "&A = (\n".to_string()
        } else {
            let call = if index > 0 {
                format!("Synth{:05}(&A, &B)\n", index - 1)
            } else {
                String::new()
            };
            format!(
                "&A = {index}\n&B = &A + 1\nIf &B > 3\n  Msg(&B, status)\nEndIf\nFor &I = 1 to 3\n  &A = &A + &I\nEndFor\nDo 'Helper'\n{call}Return\nSub 'Helper'\n  &C = 1\nEndSub\nSub 'Unused'\n  Msg('x', status)\nEndSub\n"
            )
        };
        let mut object = procedure(
            &name,
            &source,
            &["A", "B", "C", "D", "I"],
            "parm(in:&A, out:&B);",
        );
        object.main = Some(index % 7 != 3);
        objects.push(object);
    }
    catalog(objects)
}

#[test]
fn the_synthetic_catalog_is_analyzed_as_designed() {
    let run = analyze(&synthetic_catalog(40));
    assert_eq!(run.summary.procedures, 40);
    assert_eq!(run.summary.skipped, 2);
    assert_eq!(run.summary.analyzed, 38);
    // &D is never used, &C is assigned and never read, 'Unused' is never called, in every analyzed one.
    assert_eq!(run.summary.findings_by_rule[UNUSED_VARIABLE], 38);
    assert_eq!(run.summary.findings_by_rule[ASSIGNED_NEVER_READ], 38);
    assert_eq!(run.summary.findings_by_rule[UNUSED_SUBROUTINE], 38);
    assert!(
        !run.summary
            .findings_by_rule
            .contains_key(WRONG_ARGUMENT_COUNT)
    );
}

#[test]
#[ignore = "measurement, run with --ignored --nocapture"]
fn measure_synthetic_catalogs() {
    for size in [300, 3000] {
        let catalog = synthetic_catalog(size);
        let mut best = f64::MAX;
        let mut result = None;
        for _ in 0..5 {
            let started = std::time::Instant::now();
            let run = analyze(&catalog);
            let json = serde_json::to_string(&run).unwrap();
            best = best.min(started.elapsed().as_secs_f64());
            result = Some((run, json.len()));
        }
        let (run, bytes) = result.unwrap();
        println!(
            "SYNTHETIC catalog, {size} Procedures: analyze + JSON {:.1} ms (best of 5), analyzed {}, skipped {}, findings {}, JSON {} bytes",
            best * 1000.0,
            run.summary.analyzed,
            run.summary.skipped,
            run.summary.findings,
            bytes
        );
    }
}
