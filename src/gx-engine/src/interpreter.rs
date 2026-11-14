//! Tree-walking interpreter for the Procedures the portable pipeline admits.
//!
//! It runs the program `frontend` parses and `typed_backend` / `backend` would compile, without
//! `rustc`: the same values (`Decimal`, `Text`, `Date`, `DateTime`, `Record`, `Collection`) and the
//! same library operations, called directly instead of through emitted source. The compiled program
//! is the oracle; this module mirrors its observable behavior, including call hoisting, parameter
//! copy-back and the shared execution-step budget, so a program that hits a limit fails in both.
//!
//! Admission is the compiler's own: `crate::compile` (emit only, no `rustc`) must accept the
//! project, so the interpreter never runs a program the typed pipeline refuses. Database access
//! (`For Each`, `New`, `Delete`, attribute reads and writes) and anything else the emitter does not
//! lower is refused by name; nothing is approximated.

mod builtins;
mod v1;

use builtins::{builtin_values, date_part, date_shift, is_empty};

use crate::{
    date::Date,
    datetime::DateTime,
    diagnostics::DiagnosticCategory,
    frontend::{BinOp, Expr, Procedure, Stmt},
    model::{DataType, ParamMode, Project, parse_parameter},
    record_runtime::{Collection, Field, Record},
    text::Text,
    types::{Decimal, NumericType, format_str},
};
use std::{
    collections::{BTreeMap, HashMap},
    fmt,
    io::Write,
    time::{Duration, Instant},
};

/// Steps a program may spend, as in the generated program (`p{entry}(&mut 100000_u32, 0)`).
const MAX_STEPS: u32 = 100_000;
/// Call depth bound of the generated program.
const MAX_CALL_DEPTH: u32 = 128;
/// Subroutine nesting bound of the generated program.
const MAX_SUB_DEPTH: u32 = 128;
/// The wall-clock bound `gx-engine run` applies to the compiled program.
const WALL_DEADLINE: Duration = Duration::from_secs(10);
/// The interpreter recurses once per nested statement and expression; give it room.
const STACK_BYTES: usize = 64 * 1024 * 1024;
const MAX_PROJECT_BYTES: u64 = 1_048_576;

/// Execution limits. `Default` matches the compiled path.
#[derive(Debug, Clone, Copy)]
pub struct Limits {
    pub steps: u32,
    pub wall: Duration,
}

impl Default for Limits {
    fn default() -> Self {
        Self {
            steps: MAX_STEPS,
            wall: WALL_DEADLINE,
        }
    }
}

/// Why a project did not run to completion.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InterpretError {
    /// The interpreter (or the compiler front end it shares) refuses the program before running it.
    Refused(String),
    /// The program was admitted and failed while running (the compiled program exits 70).
    Runtime(String),
}

impl fmt::Display for InterpretError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Refused(reason) => write!(f, "interpreter refused the program: {reason}"),
            Self::Runtime(reason) => write!(f, "interpreter runtime error: {reason}"),
        }
    }
}

impl std::error::Error for InterpretError {}

/// Runs `project`, writing the program's standard output to `out`.
pub fn run(project: &Project, out: &mut dyn Write, limits: Limits) -> Result<(), InterpretError> {
    let canonical = Project::from_json(&project.to_json().map_err(InterpretError::Refused)?)
        .map_err(InterpretError::Refused)?;
    let parsed = canonical.parse().map_err(InterpretError::Refused)?;
    refuse_unsupported(&parsed, &canonical)?;
    // The compiler's admission check, without `rustc`: the interpreter runs exactly what it admits.
    crate::compile(&canonical).map_err(InterpretError::Refused)?;
    refuse_exit_outside_loop(&parsed)?;
    let deadline = Instant::now() + limits.wall;
    if canonical.format_version == 1 {
        let mut machine = v1::Machine {
            parsed: &parsed,
            steps: u64::from(limits.steps),
            deadline,
            ticks: 0,
            out,
        };
        let entry = parsed
            .iter()
            .position(|p| p.name.eq_ignore_ascii_case(&canonical.entry))
            .ok_or_else(|| InterpretError::Refused("entry procedure does not exist".into()))?;
        return machine
            .call(entry, 0, Vec::new())
            .map(|_| ())
            .map_err(InterpretError::Runtime);
    }
    let program = Program::new(&canonical, parsed)?;
    let mut machine = Machine {
        prog: &program,
        steps: limits.steps,
        deadline,
        ticks: 0,
        out,
    };
    machine
        .invoke(program.entry, 0, Vec::new())
        .map(|_| ())
        .map_err(InterpretError::Runtime)
}

/// Runs `project` and returns its standard output.
pub fn run_to_string(project: &Project) -> Result<String, InterpretError> {
    let mut out = Vec::new();
    run(project, &mut out, Limits::default())?;
    String::from_utf8(out).map_err(|e| InterpretError::Runtime(e.to_string()))
}

/// The `gx-engine interpret <project.json>` command: same stdout and success exit as `run`; refusals
/// and runtime errors are returned as `Err` (nonzero exit, message on stderr).
pub fn interpret_command(args: &[String], category: &mut DiagnosticCategory) -> Result<(), String> {
    if args == ["--help"] || args == ["-h"] {
        println!(
            "gx-engine interpret <project.json>\n\nRuns the project's entry Procedure with the tree-walking interpreter (no rustc), \
             printing the same output as `gx-engine run`. Procedures using database access (For Each, New, Delete, attributes) \
             or anything the typed pipeline refuses are refused by name. Execution is bounded by the same 100000-step budget \
             and 10-second deadline as `run`."
        );
        return Ok(());
    }
    let [path] = args else {
        return Err("invalid arguments; use gx-engine interpret <project.json>".into());
    };
    *category = DiagnosticCategory::Input;
    let project = load(path)?;
    let worker = std::thread::Builder::new()
        .name("gx-interpreter".into())
        .stack_size(STACK_BYTES)
        .spawn(move || {
            let mut out = std::io::BufWriter::new(std::io::stdout().lock());
            let result = run(&project, &mut out, Limits::default());
            // Output already produced stays visible, as with the compiled program.
            let flushed = out.flush();
            (result, flushed)
        })
        .map_err(|e| format!("cannot start the interpreter thread: {e}"))?;
    let (result, flushed) = worker
        .join()
        .map_err(|_| "interpreter terminated unexpectedly (internal panic)".to_string())?;
    match result {
        Ok(()) => flushed.map_err(|e| format!("cannot write output: {e}")),
        Err(error) => {
            *category = match error {
                InterpretError::Refused(_) => DiagnosticCategory::Compilation,
                InterpretError::Runtime(_) => DiagnosticCategory::Execution,
            };
            Err(error.to_string())
        }
    }
}

fn load(path: &str) -> Result<Project, String> {
    use std::io::Read;
    let file = std::fs::File::open(path).map_err(|e| format!("cannot open project: {e}"))?;
    let mut text = String::new();
    file.take(MAX_PROJECT_BYTES + 1)
        .read_to_string(&mut text)
        .map_err(|e| format!("cannot read project: {e}"))?;
    if text.len() as u64 > MAX_PROJECT_BYTES {
        return Err("portable project exceeds 1 MiB".into());
    }
    Project::from_json(&text)
}

// ---------------------------------------------------------------------------------------------
// Refusals
// ---------------------------------------------------------------------------------------------

fn refuse(what: &str) -> InterpretError {
    InterpretError::Refused(format!(
        "{what} is not supported by the interpreter (no database or Knowledge Base access)"
    ))
}

fn refuse_unsupported(parsed: &[Procedure], project: &Project) -> Result<(), InterpretError> {
    for procedure in parsed {
        scan_statements(&procedure.body, project)?;
    }
    Ok(())
}

fn scan_statements(body: &[Stmt], project: &Project) -> Result<(), InterpretError> {
    for statement in body {
        match statement {
            Stmt::ForEach(_) => return Err(refuse("`For Each`")),
            Stmt::New(_) => return Err(refuse("`New` (insert)")),
            Stmt::Delete => return Err(refuse("`Delete`")),
            Stmt::AssignAttribute(..) => return Err(refuse("attribute assignment")),
            Stmt::Assign(_, e) | Stmt::Print(e) | Stmt::MsgStatus(e) | Stmt::Return(e) => {
                scan_expression(e, project)?;
            }
            Stmt::AssignField(r, _, e) => {
                scan_expression(r, project)?;
                scan_expression(e, project)?;
            }
            Stmt::CollectionAdd(r, e, i) => {
                scan_expression(r, project)?;
                scan_expression(e, project)?;
                if let Some(i) = i {
                    scan_expression(i, project)?;
                }
            }
            Stmt::CollectionRemove(r, e) | Stmt::FromJson(r, e) => {
                scan_expression(r, project)?;
                scan_expression(e, project)?;
            }
            Stmt::CollectionClear(r) => scan_expression(r, project)?,
            Stmt::FromXml(x) => {
                scan_expression(&x.target, project)?;
                scan_expression(&x.xml, project)?;
                if let Some(root) = &x.root {
                    scan_expression(root, project)?;
                }
            }
            Stmt::Call(e) => scan_expression(e, project)?,
            Stmt::If(c, yes, no) => {
                scan_expression(c, project)?;
                scan_statements(yes, project)?;
                scan_statements(no, project)?;
            }
            Stmt::While(c, b) => {
                scan_expression(c, project)?;
                scan_statements(b, project)?;
            }
            Stmt::For(_, a, b, _, body) => {
                scan_expression(a, project)?;
                scan_expression(b, project)?;
                scan_statements(body, project)?;
            }
            Stmt::ForIn(_, c, body) => {
                scan_expression(c, project)?;
                scan_statements(body, project)?;
            }
            Stmt::Sub(_, body) => scan_statements(body, project)?,
            Stmt::ReturnVoid | Stmt::Exit | Stmt::DoSub(_) | Stmt::Commit | Stmt::Rollback => {}
        }
    }
    Ok(())
}

fn scan_expression(expression: &Expr, project: &Project) -> Result<(), InterpretError> {
    match expression {
        Expr::Attribute(name) => {
            return Err(refuse(&format!("database attribute `{name}`")));
        }
        Expr::Call(name, args) => {
            if matches!(
                name.as_str(),
                "SUM" | "COUNT" | "MAX" | "MIN" | "FIND" | "AVERAGE" | "AVG"
            ) && !project.procedures.iter().any(|p| &p.name == name)
            {
                return Err(refuse(&format!("database aggregate formula `{name}`")));
            }
            for arg in args {
                scan_expression(arg, project)?;
            }
        }
        Expr::Binary(l, _, r) | Expr::DateShift(_, l, r) | Expr::CollectionItem(l, r) => {
            scan_expression(l, project)?;
            scan_expression(r, project)?;
        }
        Expr::TextLength(e)
        | Expr::Member(e, _)
        | Expr::CloneRecord(e)
        | Expr::DateMethod(_, e)
        | Expr::Not(e)
        | Expr::Negate(e)
        | Expr::Paren(e) => scan_expression(e, project)?,
        Expr::Integer(_)
        | Expr::Decimal(_)
        | Expr::Text(_)
        | Expr::Boolean(_)
        | Expr::EmptyDate
        | Expr::Variable(_) => {}
    }
    Ok(())
}

/// The generated program writes `Exit` as `break;`, which `rustc` rejects outside a loop.
fn refuse_exit_outside_loop(parsed: &[Procedure]) -> Result<(), InterpretError> {
    fn walk(body: &[Stmt], in_loop: bool) -> Result<(), InterpretError> {
        for statement in body {
            match statement {
                Stmt::Exit if !in_loop => {
                    return Err(InterpretError::Refused(
                        "`Exit` outside a loop (the compiled program does not build)".into(),
                    ));
                }
                Stmt::If(_, yes, no) => {
                    walk(yes, in_loop)?;
                    walk(no, in_loop)?;
                }
                Stmt::While(_, b) | Stmt::For(_, _, _, _, b) | Stmt::ForIn(_, _, b) => {
                    walk(b, true)?;
                }
                Stmt::Sub(_, b) => walk(b, false)?,
                _ => {}
            }
        }
        Ok(())
    }
    for procedure in parsed {
        walk(&procedure.body, false)?;
    }
    Ok(())
}

// ---------------------------------------------------------------------------------------------
// Typed program (format_version 2)
// ---------------------------------------------------------------------------------------------

type R<T> = Result<T, String>;

struct Schema {
    name: &'static str,
    fields: BTreeMap<&'static str, DataType>,
}

struct Proc {
    params: Vec<(ParamMode, String)>,
    variables: BTreeMap<String, DataType>,
    return_type: Option<DataType>,
    main: Vec<Stmt>,
    subs: BTreeMap<String, Vec<Stmt>>,
}

struct Program {
    procs: Vec<Proc>,
    names: HashMap<String, usize>,
    schemas: Vec<Schema>,
    schema_index: HashMap<String, usize>,
    entry: usize,
    window: Option<u16>,
}

/// Record and collection schemas carry `&'static str` names in the shared runtime; the generated
/// program writes them as literals. A project has at most 128 schemas of 1024 fields, so leaking
/// each name once per run is a bounded, short-lived cost.
fn leak(name: &str) -> &'static str {
    Box::leak(name.to_owned().into_boxed_str())
}

impl Program {
    fn new(project: &Project, parsed: Vec<Procedure>) -> Result<Self, InterpretError> {
        let mut schemas = Vec::new();
        let mut schema_index = HashMap::new();
        for (i, record) in project.record_types.iter().enumerate() {
            schema_index.insert(record.name.clone(), i);
            schemas.push(Schema {
                name: leak(&record.name),
                fields: record
                    .fields
                    .iter()
                    .map(|(n, t)| (leak(n), t.clone()))
                    .collect(),
            });
        }
        let mut procs = Vec::new();
        let mut names = HashMap::new();
        for (i, (source, parsed)) in project.procedures.iter().zip(parsed).enumerate() {
            names.insert(source.name.clone(), i);
            let mut main = Vec::new();
            let mut subs = BTreeMap::new();
            for statement in parsed.body {
                match statement {
                    Stmt::Sub(name, body) => {
                        subs.insert(name, body);
                    }
                    other => main.push(other),
                }
            }
            let params = source
                .params
                .iter()
                .map(|p| {
                    parse_parameter(p)
                        .map(|(mode, name)| (mode, name.to_owned()))
                        .map_err(InterpretError::Refused)
                })
                .collect::<Result<Vec<_>, _>>()?;
            procs.push(Proc {
                params,
                variables: source.variables.clone(),
                return_type: source.return_type.clone(),
                main,
                subs,
            });
        }
        let entry = *names
            .get(&project.entry)
            .ok_or_else(|| InterpretError::Refused("entry procedure does not exist".into()))?;
        Ok(Self {
            procs,
            names,
            schemas,
            schema_index,
            entry,
            window: project.date_two_digit_year_max,
        })
    }

    fn schema(&self, name: &str) -> R<usize> {
        self.schema_index
            .get(name)
            .copied()
            .ok_or_else(|| format!("unknown record schema {name}"))
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Boolean,
    Numeric,
    Text,
    Date,
    DateTime,
    Record,
    Collection,
}

fn kind(data_type: &DataType) -> R<Kind> {
    Ok(match data_type {
        DataType::Boolean {} => Kind::Boolean,
        DataType::Numeric { .. } => Kind::Numeric,
        DataType::Character { .. } | DataType::Varchar { .. } | DataType::LongVarchar { .. } => {
            Kind::Text
        }
        DataType::Date {} => Kind::Date,
        DataType::DateTime {} => Kind::DateTime,
        DataType::Record { .. } => Kind::Record,
        DataType::Collection { .. } => Kind::Collection,
        other => return Err(format!("type {other:?} is not supported")),
    })
}

fn zero() -> R<Decimal> {
    "0".parse::<Decimal>()
}

fn decimal_of(value: usize) -> R<Decimal> {
    value.to_string().parse::<Decimal>()
}

fn decimal_i64(value: i64) -> R<Decimal> {
    value.to_string().parse::<Decimal>()
}

/// The declared-type boundary: a Numeric assignment is checked against its precision/scale.
fn store(data_type: &DataType, value: Field) -> R<Field> {
    match (data_type, value) {
        (DataType::Numeric { precision, scale }, Field::Numeric(number)) => Ok(Field::Numeric(
            NumericType::new(*precision, *scale)?.assign(number)?,
        )),
        (_, value) => Ok(value),
    }
}

fn num(value: Field) -> R<Decimal> {
    match value {
        Field::Numeric(n) => Ok(n),
        _ => Err("typed value mismatch: expected Numeric".into()),
    }
}

fn text(value: Field) -> R<Text> {
    match value {
        Field::Text(t) => Ok(t),
        _ => Err("typed value mismatch: expected Text".into()),
    }
}

fn boolean(value: Field) -> R<bool> {
    match value {
        Field::Boolean(b) => Ok(b),
        _ => Err("typed value mismatch: expected Boolean".into()),
    }
}

fn record(value: Field) -> R<Record> {
    match value {
        Field::Record(r) => Ok(r),
        _ => Err("typed value mismatch: expected Record".into()),
    }
}

fn collection(value: Field) -> R<Collection> {
    match value {
        Field::Collection(c) => Ok(c),
        _ => Err("typed value mismatch: expected Collection".into()),
    }
}

fn txt(value: &str) -> R<Field> {
    Ok(Field::Text(Text::new(value)?))
}

struct Frame<'p> {
    proc: &'p Proc,
    vars: HashMap<&'p str, Field>,
    depth: u32,
    in_sub: bool,
    sub_depth: u32,
}

impl<'p> Frame<'p> {
    fn get(&self, name: &str) -> R<&Field> {
        self.vars
            .get(name)
            .ok_or_else(|| format!("undeclared typed variable {name}"))
    }

    fn declared(&self, name: &str) -> R<&'p DataType> {
        self.proc
            .variables
            .get(name)
            .ok_or_else(|| format!("undeclared typed variable {name}"))
    }

    fn set(&mut self, name: &str, value: Field) -> R<()> {
        let slot = self
            .vars
            .get_mut(name)
            .ok_or_else(|| format!("undeclared typed variable {name}"))?;
        *slot = value;
        Ok(())
    }
}

enum Flow {
    Next,
    Break,
    Return(Option<Field>),
}

struct Outcome {
    ret: Option<Field>,
    /// Final values of the out/inout parameters, in parameter order.
    outs: Vec<Field>,
}

type Hoisted = HashMap<usize, Field>;

struct Machine<'p, 'o> {
    prog: &'p Program,
    steps: u32,
    deadline: Instant,
    ticks: u32,
    out: &'o mut dyn Write,
}

/// Builtin names the emitter lowers before it looks for a user Procedure of that name.
fn is_builtin(name: &str) -> bool {
    matches!(
        name,
        "STR"
            | "TRIM"
            | "LEN"
            | "SUBSTR"
            | "UPPER"
            | "LOWER"
            | "STRREPLACE"
            | "LTRIM"
            | "RTRIM"
            | "PADL"
            | "PADR"
            | "SPACE"
            | "CONCAT"
            | "STARTSWITH"
            | "ENDSWITH"
            | "CONTAINS"
            | "STRSEARCH"
            | "INDEXOF"
            | "INT"
            | "ROUND"
            | "TRUNC"
            | "VAL"
            | "LEFT"
            | "RIGHT"
            | "ASC"
            | "CHR"
            | "NOTNUMERIC"
            | "LTRIMSTR"
            | "DAY"
            | "MONTH"
            | "YEAR"
            | "HOUR"
            | "MINUTE"
            | "SECOND"
            | "SEC"
            | "DOW"
            | "ADDYR"
            | "ADDMTH"
            | "ADDDAYS"
            | "DADD"
            | "TADD"
            | "DDIFF"
            | "TDIFF"
            | "AGE"
            | "ENDOFMONTH"
            | "RESETTIME"
            | "TOSTRING"
            | "YMDHMSTOT"
            | "YMDTOD"
            | "CTOD"
            | "DTOC"
            | "TTOC"
            | "IIF"
            | "ISEMPTY"
            | "NULLVALUE"
            | "FORMAT"
            | "NEWLINE"
            | "REPLICATE"
            | "CHARAT"
            | "STRSEARCHREV"
            | "BOOLTOSTR"
            | "TOBOOLEAN"
            | "STRTOBOOL"
            | "TOJSON"
            | "TOJSONSTRING"
            | "FROMJSON"
            | "FROMJSONSTRING"
            | "TOXML"
            | "FROMXML"
    )
}

impl<'p, 'o> Machine<'p, 'o> {
    fn charge(&mut self, cost: u32) -> R<()> {
        self.steps = self
            .steps
            .checked_sub(cost)
            .ok_or("execution step limit exceeded")?;
        self.ticks = self.ticks.wrapping_add(1);
        if self.ticks % 256 == 0 && Instant::now() >= self.deadline {
            return Err("execution exceeded the wall-clock deadline".into());
        }
        Ok(())
    }

    // ----- defaults -------------------------------------------------------------------------

    /// The value an unassigned or `out` variable holds. `charge` mirrors the generated program,
    /// which spends one step per record field where it builds a default through `r{n}(steps)`.
    fn default_for(&mut self, data_type: &DataType, charge: bool) -> R<Field> {
        Ok(match data_type {
            DataType::Numeric { .. } => Field::Numeric(zero()?),
            DataType::Character { .. }
            | DataType::Varchar { .. }
            | DataType::LongVarchar { .. } => Field::Text(Text::new("")?),
            DataType::Boolean {} => Field::Boolean(false),
            DataType::Date {} => Field::Date(Date::empty()),
            DataType::DateTime {} => Field::DateTime(DateTime::empty()),
            DataType::Record { schema } => {
                let index = self.prog.schema(schema)?;
                Field::Record(self.new_record(index, charge)?)
            }
            DataType::Collection { schema } => {
                let index = self.prog.schema(schema)?;
                let prototype = self.new_record(index, charge)?;
                Field::Collection(Collection::with_prototype(
                    self.prog.schemas[index].name,
                    prototype,
                ))
            }
            other => return Err(format!("type {other:?} is not supported")),
        })
    }

    fn new_record(&mut self, index: usize, charge: bool) -> R<Record> {
        let prog = self.prog;
        let schema = &prog.schemas[index];
        let mut fields = BTreeMap::new();
        for (name, data_type) in &schema.fields {
            if charge {
                self.charge(1)?;
            }
            fields.insert(*name, self.default_for(data_type, charge)?);
        }
        Record::new(schema.name, fields)
    }

    // ----- static typing of container expressions ---------------------------------------------

    fn member_type(&self, f: &Frame<'p>, receiver: &Expr, field: &str) -> R<DataType> {
        match self.expression_type(f, receiver)? {
            DataType::Record { schema } => {
                let index = self.prog.schema(&schema)?;
                self.prog.schemas[index]
                    .fields
                    .get(field)
                    .cloned()
                    .ok_or_else(|| format!("unknown field {schema}.{field}"))
            }
            DataType::Collection { .. } if field == "COUNT" => Ok(DataType::Numeric {
                precision: 10,
                scale: 0,
            }),
            _ => Err("member receiver must be a record (or collection Count)".into()),
        }
    }

    fn expression_type(&self, f: &Frame<'p>, expression: &Expr) -> R<DataType> {
        match expression {
            Expr::Variable(name) => Ok(f.declared(name)?.clone()),
            Expr::Member(receiver, field) => self.member_type(f, receiver, field),
            Expr::CollectionItem(receiver, _) => match self.expression_type(f, receiver)? {
                DataType::Collection { schema } => Ok(DataType::Record { schema }),
                _ => Err("Item receiver must be a collection".into()),
            },
            Expr::CloneRecord(receiver) => self.expression_type(f, receiver),
            _ => Err("container expression must be a variable, member, Item or Clone".into()),
        }
    }

    // ----- expressions ----------------------------------------------------------------------

    /// `expression()` in the emitter: calls run first, in lowering order, then the rest.
    fn eval(&mut self, f: &mut Frame<'p>, expression: &Expr) -> R<Field> {
        let mut hoisted = Hoisted::new();
        self.hoist(f, expression, &mut hoisted)?;
        self.pure(f, expression, &hoisted)
    }

    /// `expect()`: an empty literal takes the type the context asks for.
    fn eval_expect(&mut self, f: &mut Frame<'p>, expression: &Expr, expected: Kind) -> R<Field> {
        if matches!(expression, Expr::EmptyDate) {
            return match expected {
                Kind::Date => Ok(Field::Date(Date::empty())),
                Kind::DateTime => Ok(Field::DateTime(DateTime::empty())),
                Kind::Numeric => Ok(Field::Numeric(zero()?)),
                Kind::Text => txt(""),
                Kind::Boolean => Ok(Field::Boolean(false)),
                Kind::Record | Kind::Collection => Err("typed value mismatch".into()),
            };
        }
        self.eval(f, expression)
    }

    fn container_assignment(
        &mut self,
        f: &mut Frame<'p>,
        expected: &DataType,
        expression: &Expr,
    ) -> R<Field> {
        if matches!(expression, Expr::EmptyDate)
            || matches!(expression, Expr::Call(name, args) if name == "NEW" && args.is_empty())
        {
            return self.default_for(expected, true);
        }
        self.eval(f, expression)
    }

    /// Evaluates every user-Procedure call of `expression` before the rest of it, in the order the
    /// emitter lowers them. A branch of `Iif` is its own scope and runs only when taken.
    fn hoist(&mut self, f: &mut Frame<'p>, expression: &Expr, h: &mut Hoisted) -> R<()> {
        match expression {
            Expr::Call(name, args) => {
                if is_builtin(name) {
                    match name.as_str() {
                        "IIF" => {
                            if let Some(condition) = args.first() {
                                self.hoist(f, condition, h)?;
                            }
                        }
                        "NULLVALUE" => {}
                        _ => {
                            for arg in args {
                                self.hoist(f, arg, h)?;
                            }
                        }
                    }
                } else {
                    let value = self.call_user(f, name, args, false, h)?;
                    if let Some(value) = value {
                        h.insert(expression as *const Expr as usize, value);
                    }
                }
            }
            Expr::Binary(l, _, r) | Expr::DateShift(_, l, r) | Expr::CollectionItem(l, r) => {
                self.hoist(f, l, h)?;
                self.hoist(f, r, h)?;
            }
            Expr::TextLength(e)
            | Expr::Member(e, _)
            | Expr::CloneRecord(e)
            | Expr::DateMethod(_, e)
            | Expr::Not(e)
            | Expr::Negate(e)
            | Expr::Paren(e) => self.hoist(f, e, h)?,
            Expr::Integer(_)
            | Expr::Decimal(_)
            | Expr::Text(_)
            | Expr::Boolean(_)
            | Expr::EmptyDate
            | Expr::Variable(_)
            | Expr::Attribute(_) => {}
        }
        Ok(())
    }

    /// Calls a user Procedure (`lower_call`): arguments are captured after every nested call ran,
    /// out/inout values are copied back into variable arguments in parameter order.
    fn call_user(
        &mut self,
        f: &mut Frame<'p>,
        name: &str,
        args: &[Expr],
        statement: bool,
        h: &mut Hoisted,
    ) -> R<Option<Field>> {
        let prog = self.prog;
        let index = *prog
            .names
            .get(name)
            .ok_or_else(|| format!("unknown typed procedure {name}"))?;
        let callee = &prog.procs[index];
        let omits_last_output = !statement
            && callee.return_type.is_none()
            && callee
                .params
                .last()
                .is_some_and(|(mode, _)| *mode == ParamMode::Out);
        // Nested calls first (an out argument is never evaluated)...
        for (arg, (mode, _)) in args.iter().zip(&callee.params) {
            if *mode != ParamMode::Out {
                self.hoist(f, arg, h)?;
            }
        }
        // ...then the argument values, as one block, then the call.
        let mut values: Vec<Option<Field>> = Vec::with_capacity(callee.params.len());
        for (arg, (mode, _)) in args.iter().zip(&callee.params) {
            values.push(if *mode == ParamMode::Out {
                None
            } else {
                Some(self.pure(f, arg, h)?)
            });
        }
        values.resize_with(callee.params.len(), || None);
        let outcome = self.invoke(index, f.depth + 1, values)?;
        let mut copied = 0;
        for (arg, (mode, _)) in args.iter().zip(&callee.params) {
            if *mode == ParamMode::In {
                continue;
            }
            copied += 1;
            if let Expr::Variable(target) = arg {
                let value = outcome
                    .outs
                    .get(copied - 1)
                    .ok_or("missing copied-back parameter")?
                    .clone();
                let stored = store(f.declared(target)?, value)?;
                f.set(target, stored)?;
            }
        }
        if omits_last_output {
            return Ok(outcome.outs.last().cloned());
        }
        Ok(outcome.ret)
    }

    fn pure(&mut self, f: &mut Frame<'p>, expression: &Expr, h: &Hoisted) -> R<Field> {
        match expression {
            Expr::Paren(inner) => self.pure(f, inner, h),
            Expr::EmptyDate => Ok(Field::Date(Date::empty())),
            Expr::Integer(value) => Ok(Field::Numeric(decimal_i64(*value)?)),
            Expr::Decimal(literal) => Ok(Field::Numeric(literal.parse::<Decimal>()?)),
            Expr::Boolean(value) => Ok(Field::Boolean(*value)),
            Expr::Text(value) => txt(value),
            Expr::Attribute(_) => Err("attribute expressions are not supported".into()),
            Expr::Variable(name) => Ok(f.get(name)?.clone()),
            Expr::Negate(value) => {
                let value = num(self.pure(f, value, h)?)?;
                Ok(Field::Numeric(value.checked_neg()?))
            }
            Expr::Not(value) => Ok(Field::Boolean(!boolean(self.pure(f, value, h)?)?)),
            Expr::Binary(left, operator, right) => self.binary(f, left, *operator, right, h),
            Expr::Member(receiver, field) => match self.pure(f, receiver, h)? {
                Field::Collection(c) if field == "COUNT" => {
                    Ok(Field::Numeric(decimal_of(c.len())?))
                }
                Field::Record(r) => Ok(r.get(field)?),
                _ => Err("member receiver must be a record (or collection Count)".into()),
            },
            Expr::CloneRecord(receiver) => {
                let value = self.pure(f, receiver, h)?;
                let cost = match &value {
                    Field::Record(r) => {
                        let index = self.prog.schema(r.schema())?;
                        u32::try_from(self.prog.schemas[index].fields.len())
                            .map_err(|_| "clone size overflow")?
                    }
                    Field::Collection(c) => {
                        u32::try_from(c.len().max(1)).map_err(|_| "clone size overflow")?
                    }
                    _ => return Err("Clone receiver must be a container".into()),
                };
                self.charge(cost)?;
                Ok(match value {
                    Field::Record(r) => Field::Record(r.shallow_clone()),
                    Field::Collection(c) => Field::Collection(c.shallow_clone()),
                    other => other,
                })
            }
            Expr::CollectionItem(receiver, index) => {
                let collection = collection(self.pure(f, receiver, h)?)?;
                let index = num(self.pure(f, index, h)?)?.truncate_to_i64()?;
                Ok(Field::Record(collection.item(index)?))
            }
            Expr::TextLength(value) => {
                let value = text(self.pure(f, value, h)?)?;
                Ok(Field::Numeric(decimal_of(value.utf16_length())?))
            }
            Expr::DateShift(method, receiver, offset) => {
                let receiver = self.pure(f, receiver, h)?;
                let offset = self.pure(f, offset, h)?;
                date_shift(method, receiver, offset)
            }
            Expr::DateMethod(method, receiver) => {
                let receiver = self.pure(f, receiver, h)?;
                if method == "ISEMPTY" {
                    return is_empty(receiver);
                }
                date_part(method, receiver)
            }
            Expr::Call(name, args) => {
                if is_builtin(name) {
                    self.builtin(f, name, args, h)
                } else if let Some(value) = h.get(&(expression as *const Expr as usize)) {
                    Ok(value.clone())
                } else {
                    Err(format!("call to {name} produced no value"))
                }
            }
        }
    }

    fn binary(
        &mut self,
        f: &mut Frame<'p>,
        left: &Expr,
        operator: BinOp,
        right: &Expr,
        h: &Hoisted,
    ) -> R<Field> {
        if matches!(operator, BinOp::And | BinOp::Or) {
            // Calls were hoisted already; the operands themselves short-circuit.
            let left = boolean(self.pure(f, left, h)?)?;
            return Ok(Field::Boolean(match operator {
                BinOp::And => left && boolean(self.pure(f, right, h)?)?,
                _ => left || boolean(self.pure(f, right, h)?)?,
            }));
        }
        let left = self.pure(f, left, h)?;
        let right = self.pure(f, right, h)?;
        match (left, right) {
            (Field::Date(date), Field::Numeric(days))
                if matches!(operator, BinOp::Add | BinOp::Subtract) =>
            {
                let days = days.to_i64()?;
                let days = if operator == BinOp::Subtract {
                    days.checked_neg().ok_or("Date offset overflow")?
                } else {
                    days
                };
                Ok(Field::Date(date.add_days(days)?))
            }
            (Field::Date(a), Field::Date(b)) if operator == BinOp::Subtract => Ok(Field::Numeric(
                a.difference(b).to_string().parse::<Decimal>()?,
            )),
            (Field::Text(a), Field::Text(b)) if operator == BinOp::Add => {
                Ok(Field::Text(a.checked_concat(b)?))
            }
            (Field::Numeric(a), Field::Numeric(b)) => match operator {
                BinOp::Add => Ok(Field::Numeric(a.checked_add(b)?)),
                BinOp::Subtract => Ok(Field::Numeric(a.checked_sub(b)?)),
                BinOp::Multiply => Ok(Field::Numeric(a.checked_mul(b)?)),
                BinOp::Divide => Ok(Field::Numeric(a.checked_div(b)?)),
                BinOp::Equal => Ok(Field::Boolean(a == b)),
                BinOp::NotEqual => Ok(Field::Boolean(a != b)),
                BinOp::Less => Ok(Field::Boolean(a.checked_cmp(b)?.is_lt())),
                BinOp::LessEqual => Ok(Field::Boolean(a.checked_cmp(b)?.is_le())),
                BinOp::Greater => Ok(Field::Boolean(a.checked_cmp(b)?.is_gt())),
                BinOp::GreaterEqual => Ok(Field::Boolean(a.checked_cmp(b)?.is_ge())),
                BinOp::And | BinOp::Or => unreachable!("handled above"),
            },
            (Field::Boolean(a), Field::Boolean(b)) => equality(operator, a == b),
            (Field::Text(a), Field::Text(b)) => equality(operator, a == b),
            (Field::Date(a), Field::Date(b)) => ordering(operator, a == b, a.cmp(&b)),
            (Field::DateTime(a), Field::DateTime(b)) => ordering(operator, a == b, a.cmp(&b)),
            _ => Err("binary operand type mismatch".into()),
        }
    }

    fn builtin(&mut self, f: &mut Frame<'p>, name: &str, args: &[Expr], h: &Hoisted) -> R<Field> {
        // The two builtins that do not evaluate all of their arguments.
        match name {
            "IIF" => {
                let [condition, yes, no] = args else {
                    return Err("Iif expects three arguments".into());
                };
                let condition = boolean(self.pure(f, condition, h)?)?;
                return self.eval(f, if condition { yes } else { no });
            }
            "NULLVALUE" => {
                let [target] = args else {
                    return Err("Nullvalue expects exactly one argument".into());
                };
                let data_type = match target {
                    Expr::Variable(variable) => f.declared(variable)?.clone(),
                    Expr::Member(receiver, field) => self.member_type(f, receiver, field)?,
                    _ => return Err("Nullvalue argument must be a variable or record field".into()),
                };
                return self.default_for(&data_type, false);
            }
            "STR" => {
                let value = num(self.pure(f, &args[0], h)?)?;
                let literal = |index: usize, default: u8| -> R<u8> {
                    match args.get(index) {
                        None => Ok(default),
                        Some(Expr::Integer(value)) => u8::try_from(*value)
                            .map_err(|_| "Str width/scale must be nonnegative".to_string()),
                        _ => Err("Str width/scale require integer literals".into()),
                    }
                };
                return txt(&format_str(value, literal(1, 10)?, literal(2, 0)?)?);
            }
            _ => {}
        }
        let mut v = Vec::with_capacity(args.len());
        for arg in args {
            v.push(self.pure(f, arg, h)?);
        }
        let window = self.prog.window;
        builtin_values(name, v, window)
    }

    // ----- procedures -----------------------------------------------------------------------

    /// `p{index}(steps, depth, args...)`.
    fn invoke(&mut self, index: usize, depth: u32, mut args: Vec<Option<Field>>) -> R<Outcome> {
        if depth >= MAX_CALL_DEPTH {
            return Err("call depth limit exceeded".into());
        }
        let prog = self.prog;
        let proc = &prog.procs[index];
        let mut frame = Frame {
            proc,
            vars: HashMap::with_capacity(proc.variables.len()),
            depth,
            in_sub: false,
            sub_depth: 0,
        };
        for (name, data_type) in &proc.variables {
            let value = if let Some(position) = proc.params.iter().position(|(_, p)| p == name) {
                if proc.params[position].0 == ParamMode::Out {
                    self.default_for(data_type, true)?
                } else {
                    let given = args
                        .get_mut(position)
                        .and_then(Option::take)
                        .ok_or("missing argument")?;
                    store(data_type, given)?
                }
            } else {
                self.default_for(data_type, !proc.subs.is_empty())?
            };
            frame.vars.insert(name.as_str(), value);
        }
        let ret = match self.block(&mut frame, &proc.main)? {
            Flow::Return(value) => value,
            Flow::Next => {
                if proc.return_type.is_some() {
                    return Err("procedure ended without returning a value".into());
                }
                None
            }
            Flow::Break => return Err("Exit outside a loop".into()),
        };
        let mut outs = Vec::new();
        for (mode, name) in &proc.params {
            if *mode != ParamMode::In {
                outs.push(frame.get(name)?.clone());
            }
        }
        Ok(Outcome { ret, outs })
    }

    fn block(&mut self, f: &mut Frame<'p>, body: &[Stmt]) -> R<Flow> {
        for statement in body {
            self.charge(1)?;
            match self.statement(f, statement)? {
                Flow::Next => {}
                other => return Ok(other),
            }
        }
        Ok(Flow::Next)
    }

    fn statement(&mut self, f: &mut Frame<'p>, statement: &Stmt) -> R<Flow> {
        match statement {
            Stmt::Assign(name, expression) => {
                let data_type = f.declared(name)?;
                let value = if matches!(
                    data_type,
                    DataType::Record { .. } | DataType::Collection { .. }
                ) {
                    self.container_assignment(f, data_type, expression)?
                } else {
                    let value = self.eval_expect(f, expression, kind(data_type)?)?;
                    store(data_type, value)?
                };
                f.set(name, value)?;
            }
            Stmt::AssignField(receiver, field, expression) => {
                let field_type = self.member_type(f, receiver, field)?;
                // The generated block binds the value first, then evaluates the receiver.
                let value = if matches!(
                    field_type,
                    DataType::Record { .. } | DataType::Collection { .. }
                ) {
                    self.container_assignment(f, &field_type, expression)?
                } else {
                    let value = self.eval_expect(f, expression, kind(&field_type)?)?;
                    store(&field_type, value)?
                };
                let receiver = record(self.eval_expect(f, receiver, Kind::Record)?)?;
                receiver.set(field, value)?;
            }
            Stmt::CollectionAdd(receiver, expression, index) => {
                let item = record(self.eval_expect(f, expression, Kind::Record)?)?;
                let receiver = collection(self.eval_expect(f, receiver, Kind::Collection)?)?;
                match index {
                    Some(index) => {
                        let index = num(self.eval_expect(f, index, Kind::Numeric)?)?.to_i64()?;
                        receiver.insert(index, item)?;
                    }
                    None => receiver.add(item)?,
                }
            }
            Stmt::CollectionRemove(receiver, index) => {
                let receiver = collection(self.eval_expect(f, receiver, Kind::Collection)?)?;
                let index = num(self.eval_expect(f, index, Kind::Numeric)?)?.to_i64()?;
                receiver.remove(index)?;
            }
            Stmt::CollectionClear(receiver) => {
                collection(self.eval_expect(f, receiver, Kind::Collection)?)?.clear();
            }
            Stmt::Print(expression) | Stmt::MsgStatus(expression) => {
                let value = self.eval(f, expression)?;
                let line = match value {
                    Field::Record(_) | Field::Collection(_) => {
                        return Err("record display is not admitted".into());
                    }
                    Field::Date(d) => d.to_text(),
                    Field::DateTime(d) => d.to_text(),
                    Field::Numeric(n) => match expression {
                        Expr::Variable(name) => match f.declared(name)? {
                            DataType::Numeric { precision, scale } => {
                                NumericType::new(*precision, *scale)?.render(n)?
                            }
                            _ => n.to_string(),
                        },
                        _ => n.to_string(),
                    },
                    Field::Text(t) => t.to_string(),
                    Field::Boolean(b) => b.to_string(),
                };
                writeln!(self.out, "{line}").map_err(|e| format!("cannot write output: {e}"))?;
            }
            Stmt::ReturnVoid => return Ok(Flow::Return(None)),
            Stmt::Return(expression) => {
                return Ok(Flow::Return(match &f.proc.return_type {
                    Some(data_type) => {
                        let value = self.eval_expect(f, expression, kind(data_type)?)?;
                        Some(store(data_type, value)?)
                    }
                    None => {
                        self.eval_expect(f, expression, Kind::Numeric)?;
                        None
                    }
                }));
            }
            Stmt::Call(call) => {
                let Expr::Call(name, args) = call else {
                    return Err("call statement must hold a call".into());
                };
                let mut hoisted = Hoisted::new();
                self.call_user(f, name, args, true, &mut hoisted)?;
            }
            Stmt::If(condition, yes, no) => {
                let condition = boolean(self.eval_expect(f, condition, Kind::Boolean)?)?;
                return self.block(f, if condition { yes } else { no });
            }
            Stmt::While(condition, body) => loop {
                if !boolean(self.eval_expect(f, condition, Kind::Boolean)?)? {
                    break;
                }
                self.charge(1)?;
                match self.block(f, body)? {
                    Flow::Next => {}
                    Flow::Break => break,
                    ret @ Flow::Return(_) => return Ok(ret),
                }
            },
            Stmt::For(name, initial, limit, step, body) => {
                let data_type = f.declared(name)?;
                if kind(data_type)? != Kind::Numeric {
                    return Err("For iterator must be Numeric".into());
                }
                let start = self.eval_expect(f, initial, Kind::Numeric)?;
                f.set(name, store(data_type, start)?)?;
                let step = decimal_i64(*step)?;
                loop {
                    let limit = num(self.eval_expect(f, limit, Kind::Numeric)?)?;
                    let order = num(f.get(name)?.clone())?.checked_cmp(limit)?;
                    if !(if step.checked_cmp(zero()?)?.is_gt() {
                        order.is_le()
                    } else {
                        order.is_ge()
                    }) {
                        break;
                    }
                    self.charge(1)?;
                    match self.block(f, body)? {
                        Flow::Next => {}
                        Flow::Break => break,
                        ret @ Flow::Return(_) => return Ok(ret),
                    }
                    let next = num(f.get(name)?.clone())?.checked_add(step)?;
                    f.set(name, store(data_type, Field::Numeric(next))?)?;
                }
            }
            Stmt::ForIn(variable, source, body) => {
                let count = collection(self.eval_expect(f, source, Kind::Collection)?)?.len();
                for position in 1..=count {
                    self.charge(1)?;
                    // The generated loop re-evaluates the collection expression per iteration.
                    let items = collection(self.eval_expect(f, source, Kind::Collection)?)?;
                    let item = items.item(i64::try_from(position).map_err(|e| e.to_string())?)?;
                    f.set(variable, Field::Record(item))?;
                    match self.block(f, body)? {
                        Flow::Next => {}
                        Flow::Break => break,
                        ret @ Flow::Return(_) => return Ok(ret),
                    }
                }
            }
            Stmt::DoSub(name) => {
                if !f.in_sub {
                    f.sub_depth = 0;
                }
                f.sub_depth += 1;
                if f.sub_depth > MAX_SUB_DEPTH {
                    return Err("subroutine call depth limit exceeded".into());
                }
                self.charge(1)?;
                let body = f
                    .proc
                    .subs
                    .get(name)
                    .ok_or_else(|| format!("unknown subroutine {name}"))?;
                let previous = std::mem::replace(&mut f.in_sub, true);
                let flow = self.block(f, body)?;
                f.in_sub = previous;
                match flow {
                    Flow::Return(value) => return Ok(Flow::Return(value)),
                    Flow::Break => return Err("Exit outside a loop".into()),
                    Flow::Next => f.sub_depth -= 1,
                }
            }
            Stmt::Sub(..) => {
                return Err("subroutines cannot be nested or placed inside blocks".into());
            }
            Stmt::Exit => return Ok(Flow::Break),
            Stmt::Commit | Stmt::Rollback => {}
            Stmt::FromJson(receiver, json) => {
                let target = self.eval(f, receiver)?;
                let json = text(self.eval_expect(f, json, Kind::Text)?)?;
                let _ = match target {
                    Field::Record(r) => r.from_json(json.as_str()),
                    Field::Collection(c) => c.from_json(json.as_str()),
                    _ => return Err("FromJson receiver must be a record or collection".into()),
                };
            }
            Stmt::FromXml(from_xml) => {
                let target = self.eval(f, &from_xml.target)?;
                let xml = text(self.eval_expect(f, &from_xml.xml, Kind::Text)?)?;
                let root = match &from_xml.root {
                    Some(root) => Some(text(self.eval_expect(f, root, Kind::Text)?)?),
                    None => None,
                };
                let root = root.as_ref().map(Text::as_str);
                let _ = match target {
                    Field::Record(r) => r.from_xml(xml.as_str(), root),
                    Field::Collection(c) => c.from_xml(xml.as_str(), root),
                    _ => return Err("FromXml receiver must be a record or collection".into()),
                };
            }
            Stmt::ForEach(_) => return Err(refuse_text("`For Each`")),
            Stmt::New(_) => return Err(refuse_text("`New` (insert)")),
            Stmt::Delete => return Err(refuse_text("`Delete`")),
            Stmt::AssignAttribute(..) => return Err(refuse_text("attribute assignment")),
        }
        Ok(Flow::Next)
    }
}

fn refuse_text(what: &str) -> String {
    format!("{what} is not supported by the interpreter")
}

fn equality(operator: BinOp, equal: bool) -> R<Field> {
    match operator {
        BinOp::Equal => Ok(Field::Boolean(equal)),
        BinOp::NotEqual => Ok(Field::Boolean(!equal)),
        _ => Err("ordering operands must be Numeric or Date".into()),
    }
}

fn ordering(operator: BinOp, equal: bool, order: std::cmp::Ordering) -> R<Field> {
    match operator {
        BinOp::Equal => Ok(Field::Boolean(equal)),
        BinOp::NotEqual => Ok(Field::Boolean(!equal)),
        BinOp::Less => Ok(Field::Boolean(order.is_lt())),
        BinOp::LessEqual => Ok(Field::Boolean(order.is_le())),
        BinOp::Greater => Ok(Field::Boolean(order.is_gt())),
        BinOp::GreaterEqual => Ok(Field::Boolean(order.is_ge())),
        _ => Err("unsupported date operator".into()),
    }
}
