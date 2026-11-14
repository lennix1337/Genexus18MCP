//! Interpreter behavior that needs no `rustc`: its guards (execution limits, call depth, admission,
//! refusals) and a few expected outputs. Equality with the compiled program is in
//! `interpreter_differential.rs`.

use gx_engine::{
    interpreter::{InterpretError, Limits, run, run_to_string},
    model::Project,
};
use serde_json::{Value, json};
use std::time::Duration;

fn project(variables: Value, source: &str) -> Project {
    Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{"name":"Main","params":[],"variables":variables,"source":source}]
        })
        .to_string(),
    )
    .unwrap()
}

/// Runs on a thread with the CLI's stack so a missing depth guard cannot crash the test process.
fn run_big_stack(project: Project, limits: Limits) -> (Result<(), InterpretError>, String) {
    std::thread::Builder::new()
        .stack_size(64 * 1024 * 1024)
        .spawn(move || {
            let mut out = Vec::new();
            let result = run(&project, &mut out, limits);
            (result, String::from_utf8(out).unwrap())
        })
        .unwrap()
        .join()
        .unwrap()
}

fn numeric() -> Value {
    json!({"kind":"numeric","precision":8,"scale":0})
}

#[test]
fn a_non_terminating_loop_stops_at_the_step_limit() {
    let looping = project(
        json!({"I": numeric()}),
        "&I = 0\nDo While True\n&I = &I + 1\nEndDo",
    );
    let limits = Limits {
        steps: 1_000,
        wall: Duration::from_secs(5),
    };
    let (result, _) = run_big_stack(looping, limits);
    assert_eq!(
        result,
        Err(InterpretError::Runtime(
            "execution step limit exceeded".into()
        ))
    );
}

#[test]
fn the_default_step_budget_matches_the_compiled_program() {
    // One statement per iteration plus the loop's own step: the compiled program fails after the
    // same 100000 steps, so the output is exactly this many lines.
    let looping = project(
        json!({"I": numeric()}),
        "&I = 0\nDo While True\n&I = &I + 1\nPrint(&I)\nEndDo",
    );
    let (result, output) = run_big_stack(looping, Limits::default());
    assert!(matches!(result, Err(InterpretError::Runtime(_))));
    assert_eq!(output.lines().count(), 33_332);
}

#[test]
fn the_wall_clock_deadline_stops_a_program_that_still_has_steps() {
    let looping = project(
        json!({"I": numeric()}),
        "&I = 0\nDo While True\n&I = &I + 1\nEndDo",
    );
    let limits = Limits {
        steps: 2_000_000,
        wall: Duration::ZERO,
    };
    let (result, _) = run_big_stack(looping, limits);
    assert_eq!(
        result,
        Err(InterpretError::Runtime(
            "execution exceeded the wall-clock deadline".into()
        ))
    );
}

#[test]
fn unbounded_recursion_stops_at_the_call_depth_limit() {
    let recursive = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [
                {"name":"Main","params":[],"variables":{"N":numeric()},"source":"&N = Down(1)"},
                {"name":"Down","params":["in:X"],"variables":{"X":numeric()},
                 "return_type":numeric(),"source":"Return Down(&X + 1)"}
            ]
        })
        .to_string(),
    )
    .unwrap();
    let (result, _) = run_big_stack(recursive, Limits::default());
    assert_eq!(
        result,
        Err(InterpretError::Runtime("call depth limit exceeded".into()))
    );
}

#[test]
fn unbounded_subroutine_recursion_stops_at_the_subroutine_depth_limit() {
    let recursive = project(
        json!({"I": numeric()}),
        "Do 'Again'\nSub 'Again'\nDo 'Again'\nEndSub",
    );
    let (result, _) = run_big_stack(recursive, Limits::default());
    assert_eq!(
        result,
        Err(InterpretError::Runtime(
            "subroutine call depth limit exceeded".into()
        ))
    );
}

#[test]
fn format_one_has_its_own_step_budget_and_call_depth_guard() {
    let counting = Project::from_json(
        &json!({
            "format_version": 1,
            "entry": "main",
            "procedures": [{"name":"main","params":[],"source":"&n = 0\nDo While 1\n&n = &n + 1\nEndDo"}]
        })
        .to_string(),
    )
    .unwrap();
    let (result, _) = run_big_stack(counting, Limits::default());
    assert_eq!(
        result,
        Err(InterpretError::Runtime(
            "execution step limit exceeded".into()
        ))
    );
    let recursive = Project::from_json(
        &json!({
            "format_version": 1,
            "entry": "main",
            "procedures": [
                {"name":"main","params":[],"source":"Print(down(1))"},
                {"name":"down","params":["x"],"source":"Return down(&x + 1)"}
            ]
        })
        .to_string(),
    )
    .unwrap();
    let (result, _) = run_big_stack(recursive, Limits::default());
    assert_eq!(
        result,
        Err(InterpretError::Runtime(
            "procedure call depth limit exceeded".into()
        ))
    );
}

fn refused(source: &str) -> String {
    let (result, output) = run_big_stack(project(json!({}), source), Limits::default());
    assert!(output.is_empty(), "nothing runs before a refusal: {output}");
    match result {
        Err(InterpretError::Refused(reason)) => reason,
        other => panic!("expected a refusal, got {other:?}"),
    }
}

#[test]
fn database_access_is_refused_by_name_before_anything_runs() {
    assert!(
        refused("Print(1)\nFor Each\nPrint(2)\nEndFor")
            .contains("`For Each` is not supported by the interpreter")
    );
    assert!(
        refused("Print(1)\nNew\nEndNew")
            .contains("`New` (insert) is not supported by the interpreter")
    );
    assert!(refused("Print(1)\nDelete").contains("`Delete` is not supported by the interpreter"));
}

#[test]
fn database_aggregates_are_refused_by_name() {
    assert!(refused("Print(Sum(Amount))").contains("database aggregate formula `SUM`"));
    assert!(refused("Print(1)\n&A = Sum(Amount)").contains("database aggregate formula `SUM`"));
}

#[test]
fn programs_the_compiler_refuses_are_refused_before_running() {
    let reason = refused("Print(1)\nPrint(Missing(2))");
    assert!(reason.contains("unknown typed procedure"), "{reason}");
}

#[test]
fn exit_outside_a_loop_is_refused_as_the_compiled_program_does_not_build() {
    let reason = refused("Print(1)\nExit");
    assert!(reason.contains("`Exit` outside a loop"), "{reason}");
}

#[test]
fn runtime_failures_keep_the_output_printed_before_them() {
    let failing = project(
        json!({"W": {"kind":"numeric","precision":4,"scale":0}}),
        "&W = 32767\nPrint(&W)\n&W = 32768\nPrint(&W)",
    );
    let (result, output) = run_big_stack(failing, Limits::default());
    assert!(matches!(result, Err(InterpretError::Runtime(_))));
    // A Numeric(4,0) variable prints at its declared width: 32767 does not fit, so it shows stars.
    assert_eq!(output, "****\n");
}

#[test]
fn a_typed_program_prints_declared_numeric_width_and_scale() {
    let program = project(
        json!({"Amount": {"kind":"numeric","precision":5,"scale":2}}),
        "&Amount = 1.235\nPrint(&Amount)\nPrint(1 / 8)",
    );
    assert_eq!(run_to_string(&program).unwrap(), " 1.24\n0.125\n");
}

#[test]
fn every_corpus_project_that_ran_compiled_also_interprets() {
    let corpus: Vec<Value> = serde_json::from_str(
        &std::fs::read_to_string(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("tests/fixtures/interpreter-corpus.json"),
        )
        .unwrap(),
    )
    .unwrap();
    assert!(corpus.len() >= 100, "{}", corpus.len());
    for (index, value) in corpus.iter().enumerate() {
        let project = Project::from_json(&value.to_string()).unwrap();
        let (result, _) = run_big_stack(project, Limits::default());
        assert!(result.is_ok(), "corpus[{index}]: {result:?}");
    }
}

#[test]
fn every_runnable_fixture_interprets_without_a_toolchain() {
    let fixtures = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("fixtures");
    let mut ran = 0;
    for entry in std::fs::read_dir(fixtures).unwrap() {
        let path = entry.unwrap().path();
        if path.extension().is_none_or(|e| e != "json") {
            continue;
        }
        let name = path.file_name().unwrap().to_string_lossy().into_owned();
        if name.starts_with("catalog-") {
            continue;
        }
        let text = std::fs::read_to_string(&path).unwrap();
        let project = Project::from_json(&text).unwrap();
        let (result, output) = run_big_stack(project, Limits::default());
        assert!(result.is_ok(), "{name}: {result:?}");
        assert!(!output.is_empty(), "{name} printed nothing");
        ran += 1;
    }
    assert!(ran >= 14, "{ran}");
}
