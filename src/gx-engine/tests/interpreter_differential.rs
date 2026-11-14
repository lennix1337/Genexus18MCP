//! The compiled program (`gx-engine run`: emit Rust, `rustc`, execute) is the oracle for the
//! interpreter (`gx-engine interpret`): same stdout, same success or failure, for every fixture and
//! for targeted programs on the edge semantics the fixtures cover.
//!
//! Each case builds a native executable, which takes seconds; keep `--test-threads` low.

use serde_json::{Value, json};
use std::{
    fs,
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

struct Scratch(PathBuf);

impl Scratch {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "gx-interpreter-diff-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

struct Outcome {
    stdout: Vec<u8>,
    stderr: String,
    success: bool,
}

fn gx(args: &[&Path]) -> Outcome {
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .args(args)
        .output()
        .expect("gx-engine starts");
    Outcome {
        stdout: output.stdout,
        stderr: String::from_utf8_lossy(&output.stderr).into_owned(),
        success: output.status.success(),
    }
}

fn run_compiled(project: &Path, scratch: &Path) -> Outcome {
    let mut args: Vec<&Path> = vec![Path::new("run"), project];
    let build = scratch.join("build");
    args.push(&build);
    gx(&args)
}

fn run_interpreted(project: &Path) -> Outcome {
    gx(&[Path::new("interpret"), project])
}

/// Compares the two paths on one project file. Returns a description of every difference.
fn compare(project: &Path) -> Vec<String> {
    let scratch = Scratch::new();
    let compiled = run_compiled(project, &scratch.0);
    let interpreted = run_interpreted(project);
    let mut differences = Vec::new();
    if compiled.stdout != interpreted.stdout {
        differences.push(format!(
            "stdout differs\n--- compiled\n{}\n--- interpreted\n{}",
            String::from_utf8_lossy(&compiled.stdout),
            String::from_utf8_lossy(&interpreted.stdout)
        ));
    }
    if compiled.success != interpreted.success {
        differences.push(format!(
            "exit differs: compiled success={} interpreted success={}\n--- compiled stderr\n{}\n--- interpreted stderr\n{}",
            compiled.success, interpreted.success, compiled.stderr, interpreted.stderr
        ));
    }
    differences
}

fn write_project(scratch: &Scratch, name: &str, project: &Value) -> PathBuf {
    let path = scratch.0.join(format!("{name}.json"));
    fs::write(&path, project.to_string()).unwrap();
    path
}

/// One project, both paths; the compiled path must `succeed` (or fail) as stated, so a program that
/// does not exercise what it claims cannot pass by failing identically on a typo.
fn case(name: &str, project: Value, compiled_succeeds: bool) {
    let scratch = Scratch::new();
    let path = write_project(&scratch, name, &project);
    let compiled = run_compiled(&path, &scratch.0);
    assert_eq!(
        compiled.success,
        compiled_succeeds,
        "{name}: the compiled path {} (stderr: {})",
        if compiled.success {
            "succeeded"
        } else {
            "failed"
        },
        compiled.stderr
    );
    if !compiled_succeeds {
        // Fail at run time, after the program started, not at emit or `rustc`.
        assert!(
            compiled.stderr.contains("generated program failed"),
            "{name}: expected a runtime failure, got: {}",
            compiled.stderr
        );
        assert!(
            !compiled.stdout.is_empty(),
            "{name}: failed before any output"
        );
    }
    let differences = compare(&path);
    assert!(differences.is_empty(), "{name}: {}", differences.join("\n"));
}

fn lines(source: &[&str]) -> String {
    source.join("\n")
}

fn numeric(precision: u8, scale: u8) -> Value {
    json!({"kind":"numeric","precision":precision,"scale":scale})
}

fn single(variables: Value, source: &[&str]) -> Value {
    json!({
        "format_version": 2,
        "entry": "Main",
        "procedures": [{"name":"Main","params":[],"variables":variables,"source":lines(source)}]
    })
}

// ---------------------------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------------------------

/// Fixtures `gx-engine run` cannot take, with the reason each is not compared.
const EXCLUDED_FIXTURES: &[(&str, &str)] = &[
    (
        "catalog-demo.json",
        "a KB object catalog for `kb-run`, not a portable project",
    ),
    (
        "catalog-parm-demo.json",
        "a KB object catalog for `kb-run`, not a portable project",
    ),
    (
        "str-bounds-native.tsv",
        "native Str() measurements read by the str_format tests, not a project",
    ),
];

#[test]
fn every_runnable_fixture_matches_the_compiled_program() {
    let fixtures = Path::new(env!("CARGO_MANIFEST_DIR")).join("fixtures");
    let mut compared = 0;
    let mut failures = Vec::new();
    let mut entries: Vec<_> = fs::read_dir(&fixtures)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .collect();
    entries.sort();
    for path in entries {
        let name = path.file_name().unwrap().to_string_lossy().into_owned();
        if EXCLUDED_FIXTURES
            .iter()
            .any(|(excluded, _)| *excluded == name)
        {
            continue;
        }
        compared += 1;
        for difference in compare(&path) {
            failures.push(format!("{name}: {difference}"));
        }
    }
    assert!(compared >= 14, "only {compared} fixtures compared");
    assert!(failures.is_empty(), "{}", failures.join("\n\n"));
}

#[test]
fn excluded_fixtures_exist_and_are_refused_by_the_interpreter() {
    let fixtures = Path::new(env!("CARGO_MANIFEST_DIR")).join("fixtures");
    for (name, _) in EXCLUDED_FIXTURES {
        let path = fixtures.join(name);
        assert!(path.exists(), "{name} no longer exists; update the list");
        assert!(
            !run_interpreted(&path).success,
            "{name} must be refused by `interpret`"
        );
    }
}

/// The portable projects the other test suites build and run (captured from `build::build` calls
/// across the suite; every one ran to success under `gx-engine run` when captured).
#[test]
#[ignore = "compiles about 125 programs (roughly ten minutes); run with --ignored"]
fn every_project_the_existing_suites_build_matches_the_compiled_program() {
    let corpus: Vec<Value> = serde_json::from_str(
        &fs::read_to_string(
            Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/interpreter-corpus.json"),
        )
        .unwrap(),
    )
    .unwrap();
    let scratch = Scratch::new();
    let mut failures = Vec::new();
    for (index, project) in corpus.iter().enumerate() {
        let path = write_project(&scratch, &format!("corpus-{index}"), project);
        for difference in compare(&path) {
            failures.push(format!("corpus[{index}]: {difference}"));
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n\n"));
}

// ---------------------------------------------------------------------------------------------
// Edge semantics
// ---------------------------------------------------------------------------------------------

#[test]
fn numeric_rounding_division_and_display_match() {
    case(
        "numeric_rounding",
        single(
            json!({"A": numeric(10, 4), "W": numeric(5, 0), "N": numeric(5, 2), "S": numeric(3, 1)}),
            &[
                "&A = 1 / 3",
                "Print(&A)",
                "Print(1 / 3)",
                "Print(10 / 4)",
                "Print(Round(2.5, 0))",
                "Print(Round(-2.5, 0))",
                "Print(Round(1.005, 2))",
                "Print(Round(1234.5678, -2))",
                "Print(Trunc(-1.99, 1))",
                "Print(Trunc(1234.5678, -2))",
                "Print(Int(-1.5))",
                "Print(Int(2.9))",
                "&W = 99999",
                "Print(&W)",
                "&W = 1.9",
                "Print(&W)",
                "&W = -7.9",
                "Print(&W)",
                "&N = 123.456",
                "Print(&N)",
                "&S = 99.95",
                "Print(&S)",
                "Print(&S + &N)",
                "Print(0.1 + 0.2)",
                "Print(-0.5 * 4)",
                "Print(Str(1234.5, 8, 2))",
                "Print(Str(12345.678, 4, 1))",
                "Print(LTrimStr(7.5, 6, 2))",
                "Print(Val(\"12.5abc\"))",
                "Print(Val(\"1,5\", \",\"))",
                "Print(Val(\"abc\"))",
            ],
        ),
        true,
    );
}

#[test]
fn numeric_overflow_and_division_by_zero_fail_after_the_same_output() {
    case(
        "numeric_overflow_storage",
        single(
            json!({"W": numeric(4, 0)}),
            &["&W = 32767", "Print(&W)", "&W = 32768", "Print(&W)"],
        ),
        false,
    );
    case(
        "numeric_divide_by_zero",
        single(
            json!({"A": numeric(5, 2), "B": numeric(5, 2)}),
            &[
                "&A = 6",
                "&B = 0",
                "Print(&A)",
                "Print(&A / &B)",
                "Print(7)",
            ],
        ),
        false,
    );
    case(
        "numeric_multiply_overflow",
        single(
            json!({"A": numeric(18, 0)}),
            &[
                "&A = 999999999999999999",
                "Print(&A)",
                "Print(&A * &A * &A * &A * &A)",
            ],
        ),
        false,
    );
}

#[test]
fn string_padding_trimming_and_searching_match() {
    case(
        "strings",
        single(
            json!({
                "T": {"kind":"varchar","length":20},
                "C": {"kind":"character","length":5},
                "N": numeric(6, 0)
            }),
            &[
                "&T = \"  ab cd  \"",
                "Print(\"[\" + Trim(&T) + \"]\")",
                "Print(\"[\" + LTrim(&T) + \"]\")",
                "Print(\"[\" + RTrim(&T) + \"]\")",
                "Print(\"[\" + PadL(\"ab\", 5, \"*\") + \"]\")",
                "Print(\"[\" + PadR(\"ab\", 5, \"*\") + \"]\")",
                "Print(\"[\" + PadL(\"abcdef\", 3) + \"]\")",
                "Print(\"[\" + PadR(\"abcdef\", 3) + \"]\")",
                "Print(\"[\" + PadL(\"ab\", 6) + \"]\")",
                "Print(\"[\" + Substr(\"abcdef\", 2, 3) + \"]\")",
                "Print(\"[\" + Substr(\"abcdef\", 0, 3) + \"]\")",
                "Print(\"[\" + Substr(\"abcdef\", 5, 10) + \"]\")",
                "Print(\"[\" + Left(\"abcdef\", 2) + \"]\")",
                "Print(\"[\" + Right(\"abcdef\", 2) + \"]\")",
                "Print(\"[\" + Upper(\"aBc\") + Lower(\"aBc\") + \"]\")",
                "Print(\"[\" + StrReplace(\"a-b-c\", \"-\", \"+\") + \"]\")",
                "Print(\"[\" + Space(3) + \"]\")",
                "Print(\"[\" + Replicate(\"ab\", 3) + \"]\")",
                "Print(\"[\" + Concat(\"a\", \"b\", \"/\") + \"]\")",
                "Print(StrSearch(\"abcabc\", \"c\"))",
                "Print(StrSearch(\"abcabc\", \"c\", 4))",
                "Print(StrSearch(\"abc\", \"z\"))",
                "Print(StrSearchRev(\"abcabc\", \"b\"))",
                "Print(Len(\"ação\"))",
                "Print(Len(\"a😀b\"))",
                "Print(Asc(\"A\"))",
                "Print(Chr(66))",
                "Print(StartsWith(\"abc\", \"ab\"))",
                "Print(EndsWith(\"abc\", \"bc\"))",
                "Print(Contains(\"abc\", \"x\"))",
                "Print(NotNumeric(\"12a\"))",
                "Print(NotNumeric(\"123\"))",
                "&C = \"abcdefgh\"",
                "Print(\"[\" + &C + \"]\")",
                "Print(&C.Length())",
                "Print(IsEmpty(\"   \"))",
                "&N = 42",
                "Print(\"n=\" + Trim(Str(&N)))",
                "Print(Format(\"%1-%2\", \"x\", 5))",
                "Print(CharAt(\"abc\", 2))",
                "Print(IIf(&N > 10, \"big\", \"small\"))",
                "Print(\"A\" = \"a\")",
            ],
        ),
        true,
    );
}

#[test]
fn date_and_datetime_arithmetic_match() {
    case(
        "dates",
        json!({
            "format_version": 2,
            "date_two_digit_year_max": 2039,
            "entry": "Main",
            "procedures": [{
                "name": "Main", "params": [],
                "variables": {
                    "D": {"kind":"date"}, "E": {"kind":"date"},
                    "T": {"kind":"date_time"}, "U": {"kind":"date_time"},
                    "N": numeric(10, 0)
                },
                "source": lines(&[
                    "&D = YMDToD(2024, 1, 31)",
                    "&E = &D.AddMonths(1)",
                    "Print(&E)",
                    "&E = AddMth(&D, 13)",
                    "Print(&E)",
                    "&E = AddYr(YMDToD(2024, 2, 29), 1)",
                    "Print(&E)",
                    "&E = &D + 366",
                    "Print(&E)",
                    "&N = &E - &D",
                    "Print(&N)",
                    "Print(&D < &E)",
                    "Print(&D = &D)",
                    "Print(DOW(&D))",
                    "Print(EndOfMonth(YMDToD(2023, 2, 10)))",
                    "Print(DToC(&D))",
                    "Print(CToD(\"2024-03-05\"))",
                    "Print(CToD(\"garbage\"))",
                    "Print(YMDToD(29, 1, 1))",
                    "Print(YMDToD(40, 1, 1))",
                    "Print(DDiff(YMDToD(2024, 3, 1), YMDToD(2024, 2, 1)))",
                    "&T = YMDHMSToT(2024, 2, 28, 23, 59, 58)",
                    "Print(&T)",
                    "&U = TAdd(&T, 5)",
                    "Print(&U)",
                    "Print(TDiff(&U, &T))",
                    "Print(&U.Hour())",
                    "Print(TToC(&U))",
                    "Print(ResetTime(&U))",
                    "Print(Age(YMDToD(2000, 6, 15), YMDToD(2024, 6, 14)))",
                    "&D.SetEmpty()",
                    "Print(&D.IsEmpty())",
                    "Print(&D)",
                    "&D = YMDToD(9999, 12, 31)",
                    "Print(&D)",
                    "&D = &D + 1",
                    "Print(&D)",
                ])
            }]
        }),
        // The last statement leaves the supported calendar range.
        false,
    );
}

#[test]
fn parameter_modes_in_out_and_inout_match() {
    let numeric52 = numeric(5, 2);
    case(
        "parameter_modes",
        json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [
                {"name":"Main","params":[],
                 "variables":{"A":numeric52,"B":numeric52,"C":numeric52,"W":numeric(3,0),"R":numeric52},
                 "source": lines(&[
                    "&A = 1", "&B = 5", "&C = 3",
                    "Modes(&A, &B, &C)",
                    "Print(&A)", "Print(&B)", "Print(&C)",
                    "&W = 0",
                    "Narrow(&W)",
                    "Print(&W)",
                    "&C = 4",
                    "Modes(1, &C, 2 + 3)",
                    "Print(&C)",
                    "&R = Last(7)",
                    "Print(&R)",
                    "&R = Twice(Last(2), 3)",
                    "Print(&R)",
                    "&A = 2",
                    "Modes(&A, &A, &A)",
                    "Print(&A)",
                 ])},
                {"name":"Modes","params":["in:A","out:B","inout:C"],
                 "variables":{"A":numeric52,"B":numeric52,"C":numeric52},
                 "source": lines(&["Print(&A)", "Print(&B)", "Print(&C)", "&B = &A + 1", "&C = &C * 2", "&A = 100"])},
                {"name":"Narrow","params":["out:W"],
                 "variables":{"W":numeric(3,0)},
                 "source": lines(&["Print(&W)", "&W = 12.9"])},
                {"name":"Last","params":["in:X","out:Y"],
                 "variables":{"X":numeric52,"Y":numeric52},
                 "source":"&Y = &X * 2"},
                {"name":"Twice","params":["in:X","in:Z"],
                 "variables":{"X":numeric52,"Z":numeric52},
                 "return_type":numeric52,
                 "source":"Return &X * &Z"}
            ]
        }),
        true,
    );
}

#[test]
fn nested_records_collections_and_aliasing_match() {
    case(
        "nested_records",
        json!({
            "format_version": 2,
            "entry": "Main",
            "record_types": [
                {"name":"Root","fields":{
                    "Child":{"kind":"record","schema":"Child"},
                    "Rows":{"kind":"collection","schema":"Row"},
                    "Label":{"kind":"varchar","length":10},
                    "On":{"kind":"boolean"},
                    "When":{"kind":"date"}
                }},
                {"name":"Child","fields":{"Amount":numeric(4,0),"Name":{"kind":"character","length":4}}},
                {"name":"Row","fields":{"Amount":numeric(4,0)}}
            ],
            "procedures": [
                {"name":"Main","params":[],
                 "variables":{
                    "First":{"kind":"record","schema":"Root"},
                    "Second":{"kind":"record","schema":"Root"},
                    "Item":{"kind":"record","schema":"Row"},
                    "Other":{"kind":"record","schema":"Row"},
                    "Rows":{"kind":"collection","schema":"Row"},
                    "Copy":{"kind":"collection","schema":"Row"},
                    "Total":numeric(8,0)
                 },
                 "source": lines(&[
                    "&First = new()",
                    "Print(&First.Child.Amount)",
                    "Print(\"[\" + &First.Label + \"]\")",
                    "Print(&First.On)",
                    "&First.Child.Amount = 7",
                    "&First.Child.Name = \"abcdefg\"",
                    "&First.On = True",
                    "&Second = &First",
                    "&Second.Child.Amount = 9",
                    "Print(&First.Child.Amount)",
                    "&Second = &First.Clone()",
                    "&Second.Label = \"copy\"",
                    "&Second.Child.Amount = 11",
                    "Print(&First.Child.Amount)",
                    "Print(&Second.Child.Amount)",
                    "Print(\"[\" + &First.Label + \"]\")",
                    "Print(\"[\" + &Second.Child.Name + \"]\")",
                    "&Rows = new()",
                    "&Item = new()",
                    "&Other = new()",
                    "&Item.Amount = 5",
                    "&Rows.Add(&Item)",
                    "&Other.Amount = 6",
                    "&Rows.Add(&Other)",
                    "&Item.Amount = 8",
                    "Print(&Rows.Count)",
                    "Print(&Rows.Item(1).Amount)",
                    "&Copy = &Rows.Clone()",
                    "&Copy.Remove(1)",
                    "Print(&Rows.Count)",
                    "Print(&Copy.Count)",
                    "&Total = 0",
                    "For &Item in &Rows",
                    "&Total = &Total + &Item.Amount",
                    "EndFor",
                    "Print(&Total)",
                    "&Rows.Clear()",
                    "Print(&Rows.Count)",
                    "Print(IndexOf(&Copy, &Other))",
                    "Print(&First.When.IsEmpty())",
                    "Print(ToJson(&Other))",
                    "Fill(&Other)",
                    "Print(&Other.Amount)",
                 ])},
                {"name":"Fill","params":["inout:Row"],
                 "variables":{"Row":{"kind":"record","schema":"Row"}},
                 "source":"&Row.Amount = 1234"}
            ]
        }),
        true,
    );
}

#[test]
fn control_flow_loops_exit_subroutines_and_returns_match() {
    let n = numeric(8, 0);
    case(
        "control_flow",
        json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [
                {"name":"Main","params":[],
                 "variables":{"I":n,"J":n,"Acc":n,"B":{"kind":"boolean"}},
                 "source": lines(&[
                    "For &I = 10 To 1 Step -3",
                    "Print(&I)",
                    "EndFor",
                    "Print(&I)",
                    "&Acc = 0",
                    "For &I = 1 To 5",
                    "If &I = 4",
                    "Exit",
                    "EndIf",
                    "&Acc = &Acc + &I",
                    "EndFor",
                    "Print(&Acc)",
                    "&J = 0",
                    "Do While &J < 100",
                    "&J = &J + 7",
                    "If &J > 30",
                    "Exit",
                    "EndIf",
                    "EndDo",
                    "Print(&J)",
                    "Do 'Bump'",
                    "Do 'Bump'",
                    "Print(&Acc)",
                    "Print(Fact(5))",
                    "Print(Early(3))",
                    "Print(Early(-3))",
                    "Sub 'Bump'",
                    "&Acc = &Acc + 100",
                    "Do 'Inner'",
                    "EndSub",
                    "Sub 'Inner'",
                    "&Acc = &Acc + 1",
                    "EndSub",
                 ])},
                {"name":"Fact","params":["in:X"],"variables":{"X":n},"return_type":n,
                 "source": lines(&["If &X <= 1", "Return 1", "EndIf", "Return &X * Fact(&X - 1)"])},
                {"name":"Early","params":["in:X"],"variables":{"X":n,"R":n},"return_type":n,
                 "source": lines(&[
                    "Do 'Check'",
                    "Return 0",
                    "Sub 'Check'",
                    "If &X > 0",
                    "Return 77",
                    "EndIf",
                    "EndSub",
                 ])}
            ]
        }),
        true,
    );
}

#[test]
fn step_limit_stops_a_non_terminating_procedure_in_both_paths() {
    case(
        "infinite_loop",
        single(
            json!({"I": numeric(8, 0)}),
            &[
                "&I = 0",
                "Do While True",
                "&I = &I + 1",
                "Print(&I)",
                "EndDo",
            ],
        ),
        false,
    );
}

#[test]
fn call_depth_limit_stops_unbounded_recursion_in_both_paths() {
    case(
        "unbounded_recursion",
        json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [
                {"name":"Main","params":[],"variables":{"N":numeric(8,0)},
                 "source":"&N = Down(1)\nPrint(&N)"},
                {"name":"Down","params":["in:X"],"variables":{"X":numeric(8,0)},
                 "return_type":numeric(8,0),
                 "source":"Print(&X)\nReturn Down(&X + 1)"}
            ]
        }),
        false,
    );
}

#[test]
fn short_circuit_hoists_calls_but_not_pure_operands() {
    let n = numeric(8, 0);
    case(
        "short_circuit",
        json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [
                {"name":"Main","params":[],
                 "variables":{"Z":n,"B":{"kind":"boolean"}},
                 "source": lines(&[
                    "&Z = 0",
                    // The call runs although the left operand decides; the division is skipped.
                    "&B = False And Noisy(1) = 1",
                    "Print(&B)",
                    "&B = True Or 1 / &Z = 1",
                    "Print(&B)",
                    "&B = IIf(True, Noisy(2) = 2, Noisy(3) = 3)",
                    "Print(&B)",
                    "&Z = Noisy(4) + Noisy(5) * Noisy(6)",
                    "Print(&Z)",
                 ])},
                {"name":"Noisy","params":["in:V"],"variables":{"V":n},"return_type":n,
                 "source":"Print(&V)\nReturn &V"}
            ]
        }),
        true,
    );
}

#[test]
fn format_one_integer_procedures_match() {
    case(
        "format_one",
        json!({
            "format_version": 1,
            "entry": "main",
            "procedures": [
                {"name":"main","params":[],"source": lines(&[
                    "&n = 10", "&acc = 0",
                    "Do While &n > 0",
                    "&acc = &acc + sq(&n) - 7 / 2",
                    "&n = &n - 1",
                    "EndDo",
                    "Print(&acc)", "Print(-&acc)", "Print(Not 0)", "Print(1 And 0 Or 1)",
                    "Print(9223372036854775807)", "Print(7 / 0)",
                ])},
                {"name":"sq","params":["x"],"source":"Return &x * &x"}
            ]
        }),
        false,
    );
}

// ---------------------------------------------------------------------------------------------
// Refusals
// ---------------------------------------------------------------------------------------------

fn interpret_source(project: &Value) -> Outcome {
    let scratch = Scratch::new();
    let path = write_project(&scratch, "refusal", project);
    run_interpreted(&path)
}

#[test]
fn database_statements_are_refused_by_name_before_any_output() {
    for (source, name) in [
        ("Print(1)\nFor Each\nPrint(2)\nEndFor", "For Each"),
        ("Print(1)\nNew\nEndNew", "New"),
        ("Print(1)\nDelete", "Delete"),
    ] {
        let outcome = interpret_source(&single(json!({}), &[source]));
        assert!(!outcome.success, "{name} must be refused");
        assert!(
            outcome.stdout.is_empty(),
            "{name}: no output before a refusal"
        );
        assert!(
            outcome.stderr.contains("refused") || outcome.stderr.contains("not supported"),
            "{name}: {}",
            outcome.stderr
        );
    }
}

#[test]
fn unknown_calls_and_exit_outside_a_loop_are_refused() {
    let outcome = interpret_source(&single(json!({}), &["Print(Missing(1))"]));
    assert!(
        !outcome.success && outcome.stdout.is_empty(),
        "{}",
        outcome.stderr
    );
    let outcome = interpret_source(&single(json!({}), &["Print(1)", "Exit"]));
    assert!(
        !outcome.success && outcome.stdout.is_empty(),
        "{}",
        outcome.stderr
    );
    assert!(outcome.stderr.contains("Exit"), "{}", outcome.stderr);
}
