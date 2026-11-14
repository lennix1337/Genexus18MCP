//! Whole-Knowledge-Base static analysis in one engine run (`kb-analyze`).
//!
//! The catalog is read once; every Procedure the front end admits (`frontend::parse_body`) is checked by
//! rules that can be decided on the parsed program. A Procedure the pass cannot judge (the reader refused
//! it, the Knowledge Base stores it as not generated, it holds no Source, the parser does not admit its
//! Source, or it belongs to the GeneXus library) is listed under `skipped` with the reason. It is never
//! reported as clean and never receives a finding by guesswork.
//!
//! Rule ids reuse the SDK-side Worker linter (`LinterService.cs`) where a rule means the same thing:
//! `GX008` (Unused variable) and `GX009` (Unused subroutine). The rules only this engine decides have the
//! ids `GXE001`..`GXE004`, so a Worker id is never reused for another meaning. Severity names match the
//! Worker's (`Warning`, `Info`).
//!
//! Every rule prefers a missed problem over a false report. What each rule deliberately does not flag is
//! documented on its `RULES` entry and on the function that decides it.
//!
//! What no rule can see: the catalog serves a Procedure's Source, Rules, Variables and Conditions only.
//! A Layout, Help or Documentation part is not read, so a variable used only there looks unused here.
//! The front end admits no print blocks, which keeps report Procedures (the usual owners of a Layout)
//! among the skipped ones; the residual risk is a Layout on a Procedure whose Source still parses.

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};

use serde::Serialize;

use crate::catalog::{Catalog, KbObject, ObjectKind};
use crate::frontend::{Expr, Stmt};

pub const UNUSED_VARIABLE: &str = "GX008";
pub const UNUSED_SUBROUTINE: &str = "GX009";
pub const ASSIGNED_NEVER_READ: &str = "GXE001";
pub const UNREACHABLE_STATEMENT: &str = "GXE002";
pub const WRONG_ARGUMENT_COUNT: &str = "GXE003";
pub const UNREACHABLE_PROCEDURE: &str = "GXE004";

/// One rule this pass can report.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct RuleInfo {
    pub id: &'static str,
    pub title: &'static str,
    pub severity: &'static str,
}

/// The rules, in id order. The comment on each says what it deliberately does not flag.
pub const RULES: [RuleInfo; 6] = [
    // Not flagged: a variable named in the Rules, Conditions or any other part the catalog holds
    // (parameters are named in `parm`), a variable the Worker treats as framework managed or built in,
    // and any variable of a Procedure whose Variables part cannot be read. A mention in a comment is not a use.
    RuleInfo {
        id: UNUSED_VARIABLE,
        title: "Unused variable",
        severity: "Warning",
    },
    // Not flagged: a subroutine some `Do` reaches, even when that `Do` sits in a subroutine nobody calls.
    RuleInfo {
        id: UNUSED_SUBROUTINE,
        title: "Unused subroutine",
        severity: "Warning",
    },
    // Not flagged: a variable that is read anywhere (a self-read like `&n = &n + 1` counts as a read), passed
    // to a call (it may be an out or inout argument), used as a loop variable, or whose record, collection or
    // member is changed in place; a parameter; a variable named in any non-Source part. The check is not
    // flow-sensitive: a variable read only before its last assignment is not flagged.
    RuleInfo {
        id: ASSIGNED_NEVER_READ,
        title: "Variable assigned but never read",
        severity: "Warning",
    },
    // Not flagged: code after a Return or Exit under a condition (only the same block is judged), and a
    // subroutine definition placed after a Return. Only the first dead statement of a block is reported.
    RuleInfo {
        id: UNREACHABLE_STATEMENT,
        title: "Unreachable statement",
        severity: "Warning",
    },
    // Not flagged: a callee name held by more than one object or by a typed built-in; a callee with no
    // readable Rules or with Rules this engine does not parse; a call through a variable or a method; a
    // module-qualified name; a call whose tokens the parser tree does not confirm one to one. A function
    // call may omit the callee's last out parameter. A name matching no catalog object is NOT reported:
    // GeneXus ships many functions this engine has no list of, so absence cannot be proven.
    RuleInfo {
        id: WRONG_ARGUMENT_COUNT,
        title: "Wrong number of arguments",
        severity: "Warning",
    },
    // Not flagged: a Procedure stored as main or with no stored flag, one the KB stores as not generated, a
    // library one, and any object kind other than Procedure. References from object kinds the reader does not
    // serve, and calls made by name at run time, are invisible to the catalog, hence the `Info` severity.
    RuleInfo {
        id: UNREACHABLE_PROCEDURE,
        title: "Procedure no generated object reaches",
        severity: "Info",
    },
];

fn rule(id: &str) -> &'static RuleInfo {
    RULES
        .iter()
        .find(|rule| rule.id == id)
        .expect("every reported rule id is in RULES")
}

/// One finding. `line` and `column` are one-based into the Source part and absent when the finding is not
/// about a position of the Source (a declared variable lives in the Variables part; a Procedure is whole).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Finding {
    pub rule: &'static str,
    pub severity: &'static str,
    pub object: String,
    pub part: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub line: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub column: Option<usize>,
    pub message: String,
}

/// Why a Procedure was not analyzed.
pub const SKIP_LIBRARY: &str = "library";
pub const SKIP_REFUSED: &str = "refused";
pub const SKIP_NOT_GENERATED: &str = "not_generated";
pub const SKIP_NO_SOURCE: &str = "no_source";
pub const SKIP_UNPARSED: &str = "unparsed";

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Skipped {
    pub object: String,
    pub category: &'static str,
    pub reason: String,
}

/// Rules withheld for one analyzed Procedure because their input could not be read.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Withheld {
    pub object: String,
    pub rules: Vec<&'static str>,
    pub reason: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Summary {
    pub objects: usize,
    pub procedures: usize,
    pub analyzed: usize,
    pub skipped: usize,
    pub withheld: usize,
    pub findings: usize,
    pub findings_by_rule: BTreeMap<&'static str, usize>,
    pub skipped_by_category: BTreeMap<&'static str, usize>,
}

/// The `kb-analyze` answer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Analysis {
    pub analyzer: &'static str,
    pub summary: Summary,
    pub rules: Vec<RuleInfo>,
    pub findings: Vec<Finding>,
    pub skipped: Vec<Skipped>,
    pub withheld: Vec<Withheld>,
}

// ---------------------------------------------------------------------------------------------------
// Lexical positions. The parser tree carries no positions, so the positions of a variable, a subroutine
// or a call are read from a token scan that follows the front end's lexical rules (comments, text
// literals, line breaks and columns). The scan never decides whether something is a problem; it only
// says where it is.
// ---------------------------------------------------------------------------------------------------

#[derive(Debug, Clone, PartialEq, Eq)]
enum Tok {
    Var(String),
    Word(String),
    Text(String),
    LParen,
    RParen,
    Comma,
    Dot,
    Other,
}

#[derive(Debug, Clone)]
struct Positioned {
    tok: Tok,
    line: usize,
    column: usize,
}

struct Scanner {
    chars: Vec<char>,
    at: usize,
    line: usize,
    column: usize,
}

impl Scanner {
    fn peek(&self, ahead: usize) -> Option<char> {
        self.chars.get(self.at + ahead).copied()
    }

    /// Advances one character; a `\r\n` pair is one line break, as the front end's lexer counts it.
    fn bump(&mut self) {
        let Some(ch) = self.peek(0) else { return };
        self.at += 1;
        if ch == '\r' {
            if self.peek(0) == Some('\n') {
                self.at += 1;
            }
            self.line += 1;
            self.column = 1;
        } else if ch == '\n' {
            self.line += 1;
            self.column = 1;
        } else {
            self.column += 1;
        }
    }

    fn take_while(&mut self, accept: impl Fn(char) -> bool) -> String {
        let mut out = String::new();
        while let Some(ch) = self.peek(0).filter(|ch| accept(*ch)) {
            out.push(ch);
            self.bump();
        }
        out
    }
}

fn scan(source: &str) -> Vec<Positioned> {
    let mut scanner = Scanner {
        chars: source.chars().collect(),
        at: 0,
        line: 1,
        column: 1,
    };
    let mut out = Vec::new();
    while let Some(ch) = scanner.peek(0) {
        let (line, column) = (scanner.line, scanner.column);
        let mut push = |tok: Tok| out.push(Positioned { tok, line, column });
        match ch {
            '/' if scanner.peek(1) == Some('/') => {
                while !matches!(scanner.peek(0), None | Some('\r') | Some('\n')) {
                    scanner.bump();
                }
            }
            '/' if scanner.peek(1) == Some('*') => {
                scanner.bump();
                scanner.bump();
                while scanner.peek(0).is_some()
                    && !(scanner.peek(0) == Some('*') && scanner.peek(1) == Some('/'))
                {
                    scanner.bump();
                }
                scanner.bump();
                scanner.bump();
            }
            '"' | '\'' => {
                scanner.bump();
                let text = scanner.take_while(|c| c != ch && c != '\r' && c != '\n');
                if scanner.peek(0) == Some(ch) {
                    scanner.bump();
                }
                push(Tok::Text(text));
            }
            '&' => {
                scanner.bump();
                if scanner
                    .peek(0)
                    .is_some_and(|c| c.is_ascii_alphabetic() || c == '_')
                {
                    let name = scanner.take_while(|c| c.is_ascii_alphanumeric() || c == '_');
                    push(Tok::Var(name.to_ascii_uppercase()));
                } else {
                    push(Tok::Other);
                }
            }
            c if c.is_ascii_alphabetic() || c == '_' => {
                let name = scanner.take_while(|c| c.is_ascii_alphanumeric() || c == '_');
                push(Tok::Word(name.to_ascii_uppercase()));
            }
            c if c.is_ascii_digit() => {
                scanner.take_while(|c| c.is_ascii_digit());
                if scanner.peek(0) == Some('.')
                    && scanner.peek(1).is_some_and(|c| c.is_ascii_digit())
                {
                    scanner.bump();
                    scanner.take_while(|c| c.is_ascii_digit());
                }
                push(Tok::Other);
            }
            '(' => {
                scanner.bump();
                push(Tok::LParen);
            }
            ')' => {
                scanner.bump();
                push(Tok::RParen);
            }
            ',' => {
                scanner.bump();
                push(Tok::Comma);
            }
            '.' => {
                scanner.bump();
                push(Tok::Dot);
            }
            c if c.is_whitespace() => scanner.bump(),
            _ => {
                scanner.bump();
                push(Tok::Other);
            }
        }
    }
    out
}

/// The variable names mentioned (as `&name`) in a piece of text outside the Source, upper-cased.
fn mentioned_variables(text: &str) -> HashSet<String> {
    scan(text)
        .into_iter()
        .filter_map(|positioned| match positioned.tok {
            Tok::Var(name) => Some(name),
            _ => None,
        })
        .collect()
}

#[derive(Debug, Clone)]
struct CallToken {
    name: String,
    argc: usize,
    line: usize,
    column: usize,
}

/// The `Name(...)` and `Name.Call(...)` forms in source order, with the number of top-level arguments.
/// A name preceded by a dot is a method or a qualified name, not a plain object call, and is left out.
fn call_tokens(tokens: &[Positioned]) -> Vec<CallToken> {
    let mut calls = Vec::new();
    for (index, positioned) in tokens.iter().enumerate() {
        let Tok::Word(name) = &positioned.tok else {
            continue;
        };
        if index > 0 && tokens[index - 1].tok == Tok::Dot {
            continue;
        }
        let open = match tokens.get(index + 1).map(|t| &t.tok) {
            Some(Tok::LParen) => index + 1,
            Some(Tok::Dot)
                if matches!(
                    (tokens.get(index + 2), tokens.get(index + 3)),
                    (Some(Positioned { tok: Tok::Word(call), .. }), Some(Positioned { tok: Tok::LParen, .. }))
                        if call == "CALL"
                ) =>
            {
                index + 3
            }
            _ => continue,
        };
        let mut depth = 0usize;
        let mut commas = 0usize;
        let mut empty = true;
        let mut closed = false;
        for token in &tokens[open..] {
            match token.tok {
                Tok::LParen => {
                    depth += 1;
                    if depth > 1 {
                        empty = false;
                    }
                }
                Tok::RParen => {
                    depth -= 1;
                    if depth == 0 {
                        closed = true;
                        break;
                    }
                    empty = false;
                }
                Tok::Comma if depth == 1 => commas += 1,
                _ => empty = false,
            }
        }
        if !closed {
            continue;
        }
        calls.push(CallToken {
            name: name.clone(),
            argc: if empty { 0 } else { commas + 1 },
            line: positioned.line,
            column: positioned.column,
        });
    }
    calls
}

// ---------------------------------------------------------------------------------------------------
// Facts read from the parser tree.
// ---------------------------------------------------------------------------------------------------

#[derive(Default)]
struct Facts {
    /// Variables read, or touched in a way that is not a plain assignment (conservatively a read).
    reads: HashSet<String>,
    /// Variables that are the target of a plain assignment.
    writes: HashSet<String>,
    /// Every call expression: callee name and argument count.
    calls: Vec<(String, usize)>,
    subs: Vec<String>,
    do_subs: HashSet<String>,
}

impl Facts {
    fn block(&mut self, statements: &[Stmt]) {
        for statement in statements {
            self.statement(statement);
        }
    }

    // No wildcard arm: a new `Stmt` or `Expr` variant must be classified here before the crate compiles,
    // so a variable use can never hide in a construct this walk does not know.
    fn statement(&mut self, statement: &Stmt) {
        match statement {
            Stmt::Assign(name, value) => {
                self.writes.insert(name.clone());
                self.expr(value);
            }
            Stmt::AssignField(receiver, _, value) => {
                self.expr(receiver);
                self.expr(value);
            }
            Stmt::CollectionAdd(receiver, value, index) => {
                self.expr(receiver);
                self.expr(value);
                if let Some(index) = index {
                    self.expr(index);
                }
            }
            Stmt::CollectionRemove(receiver, index) => {
                self.expr(receiver);
                self.expr(index);
            }
            Stmt::CollectionClear(receiver) => self.expr(receiver),
            Stmt::FromJson(receiver, value) => {
                self.expr(receiver);
                self.expr(value);
            }
            Stmt::FromXml(from_xml) => {
                self.expr(&from_xml.target);
                self.expr(&from_xml.xml);
                if let Some(root) = &from_xml.root {
                    self.expr(root);
                }
            }
            Stmt::Print(value)
            | Stmt::MsgStatus(value)
            | Stmt::Return(value)
            | Stmt::Call(value) => self.expr(value),
            Stmt::ReturnVoid | Stmt::Exit | Stmt::Delete | Stmt::Commit | Stmt::Rollback => {}
            Stmt::If(condition, then_body, else_body) => {
                self.expr(condition);
                self.block(then_body);
                self.block(else_body);
            }
            Stmt::While(condition, body) => {
                self.expr(condition);
                self.block(body);
            }
            Stmt::For(variable, from, to, _, body) => {
                self.reads.insert(variable.clone());
                self.expr(from);
                self.expr(to);
                self.block(body);
            }
            Stmt::ForIn(variable, collection, body) => {
                self.reads.insert(variable.clone());
                self.expr(collection);
                self.block(body);
            }
            Stmt::ForEach(for_each) => {
                if let Some(using) = &for_each.using {
                    for argument in &using.arguments {
                        self.expr(argument);
                    }
                }
                for condition in &for_each.wheres {
                    self.expr(condition);
                }
                self.block(&for_each.body);
                self.block(&for_each.when_none);
            }
            Stmt::AssignAttribute(_, value) => self.expr(value),
            Stmt::New(new) => {
                self.block(&new.body);
                self.block(&new.when_duplicate);
            }
            Stmt::DoSub(name) => {
                self.do_subs.insert(name.clone());
            }
            Stmt::Sub(name, body) => {
                self.subs.push(name.clone());
                self.block(body);
            }
        }
    }

    fn expr(&mut self, expr: &Expr) {
        match expr {
            Expr::Integer(_)
            | Expr::Decimal(_)
            | Expr::Text(_)
            | Expr::Boolean(_)
            | Expr::EmptyDate
            | Expr::Attribute(_) => {}
            Expr::Variable(name) => {
                self.reads.insert(name.clone());
            }
            Expr::TextLength(inner)
            | Expr::Not(inner)
            | Expr::Negate(inner)
            | Expr::Paren(inner)
            | Expr::CloneRecord(inner)
            | Expr::DateMethod(_, inner)
            | Expr::Member(inner, _) => self.expr(inner),
            Expr::CollectionItem(left, right)
            | Expr::DateShift(_, left, right)
            | Expr::Binary(left, _, right) => {
                self.expr(left);
                self.expr(right);
            }
            Expr::Call(name, arguments) => {
                self.calls.push((name.clone(), arguments.len()));
                for argument in arguments {
                    self.expr(argument);
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------------
// The per-object pass.
// ---------------------------------------------------------------------------------------------------

/// Names never reported as unused: the Worker linter's `FrameworkManagedVariables` (re-injected by the
/// SDK or pattern engines, or built in), plus the SDK's standard variables (`VariableDefinition.IsStandard`:
/// GeneXus creates them in every object and the user cannot remove them).
const FRAMEWORK_VARIABLES: [&str; 24] = [
    "ISAUTHORIZED",
    "SECURITYFUNCTIONALITYKEYS",
    "TIME",
    "DIASSEMANAFIN",
    "PGMNAME",
    "PGMDESC",
    "MODE",
    "TODAY",
    "USERID",
    "MESSAGE",
    "EVENTNAME",
    "CTLNAME",
    "LINE",
    "OUTPUT",
    "PAGE",
    "ERR",
    "ERRMSG",
    "GXREMOVE",
    "MR",
    "MSG",
    "OP",
    "OUTDEV",
    "RESTMETHOD",
    "RESTCODE",
];

fn part<'a>(object: &'a KbObject, name: &str) -> Option<&'a str> {
    object
        .source_parts
        .iter()
        .find(|part| part.name.eq_ignore_ascii_case(name))
        .map(|part| part.source.as_str())
}

/// The declared variable names of the portable Variables part (a JSON map keyed by name), or `None` when it
/// is absent (no variables) or cannot be read.
fn declared_variables(object: &KbObject) -> Result<Vec<String>, String> {
    let Some(text) = part(object, "VARIABLES") else {
        return Ok(Vec::new());
    };
    if text.trim().is_empty() {
        return Ok(Vec::new());
    }
    serde_json::from_str::<serde_json::Map<String, serde_json::Value>>(text)
        .map(|map| map.keys().cloned().collect())
        .map_err(|error| format!("the Variables part is not a readable declaration map: {error}"))
}

/// Parameter count and whether the last parameter is `in`, from the callee's Rules; `None` when the
/// callee cannot give an unambiguous signature.
fn signature(callee: &KbObject) -> Option<(usize, bool)> {
    if callee.kind != ObjectKind::Procedure
        || callee.refusal.is_some()
        || callee.not_generated.is_some()
    {
        return None;
    }
    let rules = part(callee, "RULES")?;
    let parameters = crate::rules::extract_procedure_parameters(rules).ok()?;
    let last_is_in = match parameters.last() {
        None => true,
        Some(parameter) => match parameter.split_once(':') {
            Some((mode, _)) => mode.eq_ignore_ascii_case("in"),
            // A bare name is inout (see `model::parse_parameter`).
            None => false,
        },
    };
    Some((parameters.len(), last_is_in))
}

struct Context<'a> {
    /// Every object by upper-cased name; a name held by two objects is ambiguous.
    by_name: HashMap<String, Vec<&'a KbObject>>,
}

fn finding(
    id: &'static str,
    object: &KbObject,
    part: &'static str,
    position: Option<(usize, usize)>,
    message: String,
) -> Finding {
    let info = rule(id);
    Finding {
        rule: info.id,
        severity: info.severity,
        object: object.name.clone(),
        part,
        line: position.map(|(line, _)| line),
        column: position.map(|(_, column)| column),
        message,
    }
}

enum Outcome {
    Skipped(&'static str, String),
    Analyzed {
        findings: Vec<Finding>,
        withheld: Option<Withheld>,
    },
}

fn analyze_procedure(object: &KbObject, context: &Context<'_>) -> Outcome {
    if object.library {
        return Outcome::Skipped(
            SKIP_LIBRARY,
            "GeneXus library object, provided by the GeneXus runtime".into(),
        );
    }
    if let Some(refusal) = &object.refusal {
        return Outcome::Skipped(
            SKIP_REFUSED,
            format!("the reader refused the object: {refusal}"),
        );
    }
    if let Some(reason) = &object.not_generated {
        return Outcome::Skipped(
            SKIP_NOT_GENERATED,
            format!("{reason}; its parts are not read"),
        );
    }
    let Some(source) = part(object, "SOURCE") else {
        return Outcome::Skipped(SKIP_NO_SOURCE, "the object holds no Source part".into());
    };
    let (body, unreachable) = match crate::frontend::parse_body_with_unreachable(source) {
        Ok(parsed) => parsed,
        Err(message) => return Outcome::Skipped(SKIP_UNPARSED, message),
    };
    let mut facts = Facts::default();
    facts.block(&body);
    let tokens = scan(source);
    let mut findings = Vec::new();

    // Variables (GX008, GXE001).
    let mut withheld = None;
    match declared_variables(object) {
        Err(reason) => {
            withheld = Some(Withheld {
                object: object.name.clone(),
                rules: vec![UNUSED_VARIABLE, ASSIGNED_NEVER_READ],
                reason,
            });
        }
        Ok(declared) => {
            // Anything the catalog holds outside Source and the declarations counts as a use.
            let mut elsewhere = HashSet::new();
            for other in &object.source_parts {
                let name = other.name.to_ascii_uppercase();
                if name != "SOURCE"
                    && name != "VARIABLES"
                    && name != crate::udm::reader::VARIABLE_IDS_PART
                {
                    elsewhere.extend(mentioned_variables(&other.source));
                }
            }
            let mut seen = BTreeSet::new();
            for declared in declared {
                let key = declared.to_ascii_uppercase();
                if FRAMEWORK_VARIABLES.contains(&key.as_str()) || !seen.insert(key.clone()) {
                    continue;
                }
                if elsewhere.contains(&key) {
                    continue;
                }
                let read = facts.reads.contains(&key);
                let written = facts.writes.contains(&key);
                if !read && !written {
                    findings.push(finding(
                        UNUSED_VARIABLE,
                        object,
                        "Variables",
                        None,
                        format!("Variable &{declared} is declared but never used."),
                    ));
                } else if written && !read {
                    let position = tokens
                        .iter()
                        .find(|t| matches!(&t.tok, Tok::Var(name) if *name == key))
                        .map(|t| (t.line, t.column));
                    findings.push(finding(
                        ASSIGNED_NEVER_READ,
                        object,
                        "Source",
                        position,
                        format!("Variable &{declared} is assigned but its value is never read."),
                    ));
                }
            }
        }
    }

    // Subroutines (GX009).
    for (index, name) in facts.subs.iter().enumerate() {
        if facts.subs[..index].contains(name) || facts.do_subs.contains(name) {
            continue;
        }
        let position = tokens.windows(2).find_map(|pair| {
            let named = match &pair[1].tok {
                Tok::Text(text) | Tok::Word(text) => text.eq_ignore_ascii_case(name),
                _ => false,
            };
            (pair[0].tok == Tok::Word("SUB".into()) && named)
                .then_some((pair[0].line, pair[0].column))
        });
        findings.push(finding(
            UNUSED_SUBROUTINE,
            object,
            "Source",
            position,
            format!("Subroutine '{name}' is never called."),
        ));
    }

    // Unreachable statements (GXE002).
    for (line, column) in unreachable {
        findings.push(finding(
            UNREACHABLE_STATEMENT,
            object,
            "Source",
            Some((line, column)),
            "This statement can never execute: it follows a Return or Exit in the same block."
                .into(),
        ));
    }

    // Wrong argument count (GXE003).
    let sites = call_tokens(&tokens);
    let mut checked = BTreeSet::new();
    for site in &sites {
        if !checked.insert(site.name.clone()) {
            continue;
        }
        let Some(candidates) = context.by_name.get(&site.name) else {
            continue;
        };
        let [callee] = candidates.as_slice() else {
            continue;
        };
        if crate::frontend::is_typed_builtin(&site.name) {
            continue;
        }
        let Some((count, last_is_in)) = signature(callee) else {
            continue;
        };
        // The token scan is trusted only when the parser tree holds exactly the same calls of this name.
        let mut from_tree: Vec<usize> = facts
            .calls
            .iter()
            .filter(|(name, _)| *name == site.name)
            .map(|(_, argc)| *argc)
            .collect();
        let mut from_tokens: Vec<usize> = sites
            .iter()
            .filter(|other| other.name == site.name)
            .map(|other| other.argc)
            .collect();
        from_tree.sort_unstable();
        from_tokens.sort_unstable();
        if from_tree != from_tokens {
            continue;
        }
        for call in sites.iter().filter(|other| other.name == site.name) {
            let accepted =
                call.argc == count || (count > 0 && !last_is_in && call.argc == count - 1);
            if !accepted {
                findings.push(finding(
                    WRONG_ARGUMENT_COUNT,
                    object,
                    "Source",
                    Some((call.line, call.column)),
                    format!(
                        "{} is called with {} argument(s) but its parm rule declares {}.",
                        callee.name, call.argc, count
                    ),
                ));
            }
        }
    }

    Outcome::Analyzed { findings, withheld }
}

/// Analyzes every Procedure of `catalog`. Deterministic: the same catalog yields the same answer.
pub fn analyze(catalog: &Catalog) -> Analysis {
    let mut context = Context {
        by_name: HashMap::new(),
    };
    for object in catalog.objects() {
        context
            .by_name
            .entry(object.name.to_ascii_uppercase())
            .or_default()
            .push(object);
    }

    let mut findings = Vec::new();
    let mut skipped = Vec::new();
    let mut withheld = Vec::new();
    let mut procedures = 0;
    let mut analyzed = 0;
    let targeted = crate::reachability::targeted_objects(catalog);
    for object in catalog.objects() {
        if object.kind != ObjectKind::Procedure {
            continue;
        }
        procedures += 1;
        match analyze_procedure(object, &context) {
            Outcome::Skipped(category, reason) => skipped.push(Skipped {
                object: object.name.clone(),
                category,
                reason,
            }),
            Outcome::Analyzed {
                findings: mut found,
                withheld: held,
            } => {
                analyzed += 1;
                findings.append(&mut found);
                withheld.extend(held);
            }
        }
        // GXE004 needs no Source: it is decided on the reference graph alone. Every generated Procedure that
        // is not stored as not main is a root of `targeted_objects`, so what is left untargeted here is
        // exactly a Procedure stored as not main that no root reaches.
        if !object.library && object.not_generated.is_none() && !targeted.contains(&object.id) {
            findings.push(finding(
                UNREACHABLE_PROCEDURE,
                object,
                "Catalog",
                None,
                "No generated object reaches this Procedure and it is not a main program, so the SDK does not generate it."
                    .into(),
            ));
        }
    }

    findings.sort_by(|a, b| {
        (&a.object, a.line, a.column, a.rule, &a.message)
            .cmp(&(&b.object, b.line, b.column, b.rule, &b.message))
    });
    skipped.sort_by(|a, b| {
        (&a.object, a.category, &a.reason).cmp(&(&b.object, b.category, &b.reason))
    });
    withheld.sort_by(|a: &Withheld, b: &Withheld| a.object.cmp(&b.object));

    let mut findings_by_rule = BTreeMap::new();
    for found in &findings {
        *findings_by_rule.entry(found.rule).or_insert(0) += 1;
    }
    let mut skipped_by_category = BTreeMap::new();
    for entry in &skipped {
        *skipped_by_category.entry(entry.category).or_insert(0) += 1;
    }
    Analysis {
        analyzer: "gx-engine kb-analyze",
        summary: Summary {
            objects: catalog.objects().count(),
            procedures,
            analyzed,
            skipped: skipped.len(),
            withheld: withheld.len(),
            findings: findings.len(),
            findings_by_rule,
            skipped_by_category,
        },
        rules: RULES.to_vec(),
        findings,
        skipped,
        withheld,
    }
}

#[cfg(test)]
mod tests;
