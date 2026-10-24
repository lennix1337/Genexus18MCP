use crate::model::{CallMode, EdgeKind, WriteMode};
use regex::Regex;
use serde::{Deserialize, Serialize};
use std::sync::LazyLock;

static RE_PARM: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)\bparm\s*\(([^)]*)\)").unwrap());

/// An attribute's formula body: everything between `[Formula:` and its closing
/// bracket. GeneXus writes it inline in the attribute declaration and allows it
/// to span several physical lines, so the match is taken over the whole source
/// rather than line by line (#501).
static RE_FORMULA: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?is)\[\s*Formula\s*:(.*?)\]").unwrap());

static RE_CALL_FUNC: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)\bcall\s*\(\s*([&]?[a-zA-Z0-9_#]+)").unwrap());

static RE_CALL_METHOD: LazyLock<Regex> = LazyLock::new(|| {
    // `.Popup(` opens a Transaction in a popup, `.Link(` creates a link URL,
    // and `.Call(` is a standard call form in Events/Rules.
    Regex::new(r"(?i)(?:^|[\s,;(=])([&]?[a-zA-Z0-9_#]+)\s*\.\s*(call|popup|link)\s*\(").unwrap()
});

/// GeneXus lets a Procedure be invoked as a function: `procName(args)`. There
/// was no rule for it, so the most common call shape in a Knowledge Base was
/// invisible to the graph: 36 textual occurrences collapsed to one caller
/// (#500). Deliberately narrow - a bare `identifier(` is only a call when the
/// identifier is not a keyword or a built-in function, because matching
/// everything would invent edges to phantom nodes for every built-in.
///
/// Scans every occurrence rather than the first: a call nested inside a
/// built-in's argument list (`Str(procTotal(&a))`) has no leading boundary
/// character before it, so a single-match scan stopped at the built-in and
/// lost the real callee.
static RE_FUNCTION_CALL: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)\b([a-z_][a-zA-Z0-9_#]*)\s*\(").unwrap());

static RE_UDP: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)\budp\s*\(\s*([&]?[a-zA-Z0-9_#]+)").unwrap());

static RE_SUBMIT: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)\bsubmit\s*\(\s*([&]?[a-zA-Z0-9_#]+)").unwrap());

static RE_LINK: LazyLock<Regex> = LazyLock::new(|| {
    // Standalone `Link(Target, ...)`. Uses a delimiter prefix so `Obj.Link(` is not matched here.
    Regex::new(r"(?i)(?:^|[\s,;(=])link\s*\(\s*([&]?[a-zA-Z0-9_#]+)").unwrap()
});

static RE_XML_PROPERTY_FORMULA: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"(?is)<Property[^>]*>\s*<Name>\s*Formula\s*</Name>\s*<Value>(.*?)</Value>\s*</Property>",
    )
    .unwrap()
});

static RE_BC_METHOD: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"(?i)(?:^|[\s,;(=])([&][a-zA-Z0-9_#]+)\s*\.\s*(save|insert|update|delete|load)\s*\(",
    )
    .unwrap()
});

static RE_SUB: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"(?i)\bsub\s+['"]?([a-zA-Z0-9_# ]+)['"]?"#).unwrap());

static RE_DO_SUB: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"(?i)\bdo\s+['"]?([a-zA-Z0-9_# ]+)['"]?"#).unwrap());

static RE_ASSIGN: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\s*([&]?[a-zA-Z0-9_#]+)\s*=").unwrap());

static RE_ASSIGN_READ: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\s*&[a-zA-Z0-9_#]+\s*=\s*([a-zA-Z0-9_#]+)").unwrap());

/// Same left-hand side, but the right-hand token is immediately called: the
/// assignment's value comes from a Procedure invocation, so the token is a
/// callee, not an attribute being read (#500).
static RE_ASSIGN_CALL_RHS: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\s*&[a-zA-Z0-9_#]+\s*=\s*([a-zA-Z0-9_#]+)\s*\(").unwrap());

static RE_WHERE_ATTR: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)\bwhere\s+([a-zA-Z0-9_#]+)").unwrap());

static RE_FOR_EACH: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)\bfor\s+each\b").unwrap());

static RE_END_FOR: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)\bendfor\b").unwrap());

static RE_NEW: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)\bnew\b").unwrap());

static RE_END_NEW: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)\bendnew\b").unwrap());

static RE_DELETE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)\bdelete\b").unwrap());

fn is_gx_keyword(s: &str) -> bool {
    let lower = s.to_ascii_lowercase();
    matches!(
        lower.as_str(),
        "true"
            | "false"
            | "null"
            | "serverdate"
            | "systime"
            | "nullvalue"
            | "val"
            | "trim"
            | "str"
            | "round"
            | "udp"
            | "call"
            | "new"
            | "not"
            | "empty"
            | "none"
            | "y"
            | "n"
    )
}

/// Control words and built-in functions that can appear immediately before `(`.
/// Without this list the function-call rule would turn `Str(...)`, `Now()` and
/// `If(...)` into edges to phantom nodes, which would inflate every blast
/// radius it touched (#500).
fn is_call_keyword_or_builtin(s: &str) -> bool {
    matches!(
        s.to_ascii_lowercase().as_str(),
        // control words
        "if" | "then" | "else" | "elseif" | "do" | "while" | "for" | "each"
            | "endfor" | "endwhile" | "new" | "endnew" | "delete" | "insert"
            | "update" | "read" | "write" | "return" | "exit" | "break"
            | "continue" | "and" | "or" | "not" | "in" | "where" | "with"
            | "rules" | "events" | "source" | "form" | "sub" | "end" | "begin"
            | "define" | "parm" | "parms" | "for-each" | "error"
            | "message" | "validate" | "withoutput" | "default" | "values"
            // call-shaped keywords already handled by their own rules
            | "call" | "udp" | "submit" | "link" | "popup" | "save" | "load"
            // built-in functions and casts
        | "str" | "trim" | "ltrim" | "rtrim" | "val" | "round" | "abs" | "int"
            | "integer" | "numeric" | "decimal" | "char" | "character" | "varchar"
            | "date" | "datetime" | "time" | "now" | "today" | "year" | "month"
            | "day" | "hour" | "minute" | "second" | "dbdatetime" | "dbtime"
            | "serial" | "att" | "formattime" | "converttodatetime"
            | "converttonumber" | "converttotext" | "length" | "len" | "substring"
            | "substr" | "concat" | "upper" | "uppercase" | "lower" | "lowercase"
            | "nullvalue" | "empty" | "isempty" | "isnull" | "notempty"
            | "nulls" | "count" | "sum" | "avg" | "min" | "max" | "genexus"
            | "error_handler" | "guid" | "uuid" | "random" | "space"
    )
}

/// Serialized into the on-disk index so an unchanged file never has to be
/// lexed again. Keep this a faithful record of `parse_genexus_source`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ParsedSymbolRef {
    pub target_name: String,
    pub kind: EdgeKind,
    pub line: u32,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ParseResult {
    pub parm_rule: Option<String>,
    pub references: Vec<ParsedSymbolRef>,
    pub subroutines_defined: Vec<String>,
    pub subroutines_called: Vec<String>,
    pub complexity: u32,
    pub lines_count: usize,
}

pub fn parse_genexus_source(source: &str) -> ParseResult {
    let mut result = ParseResult::default();
    let lines: Vec<&str> = source.lines().collect();
    result.lines_count = lines.len();

    let mut in_for_each = false;
    let mut in_new = false;
    let mut complexity = 1u32;

    for (idx, line_raw) in lines.iter().enumerate() {
        let line_num = (idx + 1) as u32;
        let line = line_raw.trim();

        // Skip comments
        if line.starts_with("//") || line.starts_with("/*") {
            continue;
        }

        // Complexity indicators
        if line.to_ascii_lowercase().starts_with("if ")
            || line.to_ascii_lowercase().starts_with("do while ")
            || line.to_ascii_lowercase().contains(" where ")
        {
            complexity += 1;
        }

        // Parm rule detection
        if let Some(caps) = RE_PARM.captures(line)
            && let Some(parm_content) = caps.get(1)
        {
            result.parm_rule = Some(parm_content.as_str().trim().to_string());
        }

        // Subroutine definitions & calls
        if let Some(caps) = RE_SUB.captures(line)
            && let Some(sub_name) = caps.get(1)
        {
            result
                .subroutines_defined
                .push(sub_name.as_str().trim().to_string());
        }
        if let Some(caps) = RE_DO_SUB.captures(line)
            && let Some(sub_name) = caps.get(1)
        {
            result
                .subroutines_called
                .push(sub_name.as_str().trim().to_string());
        }

        // For Each block tracking
        if RE_FOR_EACH.is_match(line) {
            in_for_each = true;
            complexity += 1;
        }
        if RE_END_FOR.is_match(line) {
            in_for_each = false;
        }

        // New block tracking
        if RE_NEW.is_match(line) {
            in_new = true;
            complexity += 1;
        }
        if RE_END_NEW.is_match(line) {
            in_new = false;
        }

        // Call invocations
        // 1. Call(Target, ...)
        if let Some(caps) = RE_CALL_FUNC.captures(line)
            && let Some(target) = caps.get(1)
        {
            let name = target.as_str();
            let mode = if name.starts_with('&') {
                CallMode::Dynamic
            } else {
                CallMode::Static
            };
            result.references.push(ParsedSymbolRef {
                target_name: name.trim_start_matches('&').to_string(),
                kind: EdgeKind::Calls(mode),
                line: line_num,
            });
        }

        // 2. Target.Call(...), Target.Popup(...), Target.Link(...)
        if let Some(caps) = RE_CALL_METHOD.captures(line)
            && let Some(target) = caps.get(1)
        {
            let name = target.as_str();
            let method = caps
                .get(2)
                .map(|m| m.as_str().to_ascii_lowercase())
                .unwrap_or_default();
            let mode = if method == "link" {
                CallMode::Link
            } else if name.starts_with('&') {
                CallMode::Dynamic
            } else {
                CallMode::Static
            };
            result.references.push(ParsedSymbolRef {
                target_name: name.trim_start_matches('&').to_string(),
                kind: EdgeKind::Calls(mode),
                line: line_num,
            });
        }

        // 3. UDP(Target, ...)
        if let Some(caps) = RE_UDP.captures(line)
            && let Some(target) = caps.get(1)
        {
            result.references.push(ParsedSymbolRef {
                target_name: target.as_str().trim_start_matches('&').to_string(),
                kind: EdgeKind::Calls(CallMode::Udp),
                line: line_num,
            });
        }

        // 4. Submit(Target, ...)
        if let Some(caps) = RE_SUBMIT.captures(line)
            && let Some(target) = caps.get(1)
        {
            result.references.push(ParsedSymbolRef {
                target_name: target.as_str().trim_start_matches('&').to_string(),
                kind: EdgeKind::Calls(CallMode::Submit),
                line: line_num,
            });
        }

        // 5. Link(Target, ...)
        if let Some(caps) = RE_LINK.captures(line)
            && let Some(target) = caps.get(1)
        {
            result.references.push(ParsedSymbolRef {
                target_name: target.as_str().trim_start_matches('&').to_string(),
                kind: EdgeKind::Calls(CallMode::Link),
                line: line_num,
            });
        }

        // 6. Business Component methods (&bc.Save(), &bc.Insert())
        if let Some(caps) = RE_BC_METHOD.captures(line)
            && let Some(var_target) = caps.get(1)
        {
            result.references.push(ParsedSymbolRef {
                target_name: var_target.as_str().to_string(),
                kind: EdgeKind::Calls(CallMode::BusinessComponent),
                line: line_num,
            });
        }

        // 7. Procedure invoked as a function: `procName(args)`.
        //
        // Placed after the keyword rules so `Call(`, `Udp(` and friends keep
        // their own classification, and skipped when the token is a control
        // word or a built-in, which would otherwise become phantom callees and
        // inflate every blast radius they touched (#500).
        for caps in RE_FUNCTION_CALL.captures_iter(line) {
            if let Some(name) = caps.get(1) {
                let callee = name.as_str();
                if !is_call_keyword_or_builtin(callee) {
                    result.references.push(ParsedSymbolRef {
                        target_name: callee.to_string(),
                        kind: EdgeKind::Calls(CallMode::Static),
                        line: line_num,
                    });
                }
            }
        }

        // Attribute mutations
        if let Some(caps) = RE_ASSIGN.captures(line)
            && let Some(lhs) = caps.get(1)
        {
            let lhs_str = lhs.as_str();
            if !lhs_str.starts_with('&') {
                // It's an Attribute assignment
                // Outside a `New` block the assignment updates the current
                // row, whether or not a `For Each` is open.
                let write_mode = if in_new {
                    WriteMode::New
                } else {
                    WriteMode::ForEachUpdate
                };
                result.references.push(ParsedSymbolRef {
                    target_name: lhs_str.to_string(),
                    kind: EdgeKind::WritesAttr(write_mode),
                    line: line_num,
                });
            }
        }

        // Attribute reads (&Var = Attr)
        //
        // `&x = procName(...)` assigns the result of a Procedure invocation, so
        // the right-hand token is a callee and not an attribute being read. It
        // was recorded as `reads_att`, which reported a Transaction as an
        // attribute consumer and hid the real call edge (#500).
        if let Some(caps) = RE_ASSIGN_READ.captures(line)
            && let Some(rhs) = caps.get(1)
        {
            let rhs_str = rhs.as_str();
            let rhs_is_call = RE_ASSIGN_CALL_RHS.is_match(line);
            if !rhs_is_call && !rhs_str.starts_with('&') && !is_gx_keyword(rhs_str) {
                result.references.push(ParsedSymbolRef {
                    target_name: rhs_str.to_string(),
                    kind: EdgeKind::ReadsAttr,
                    line: line_num,
                });
            }
        }

        // Where clause attribute reads (Where Attr = ...)
        if let Some(caps) = RE_WHERE_ATTR.captures(line)
            && let Some(target) = caps.get(1)
        {
            let attr = target.as_str();
            if !attr.starts_with('&') && !is_gx_keyword(attr) {
                result.references.push(ParsedSymbolRef {
                    target_name: attr.to_string(),
                    kind: EdgeKind::ReadsAttr,
                    line: line_num,
                });
            }
        }

        if in_for_each && RE_DELETE.is_match(line) {
            result.references.push(ParsedSymbolRef {
                target_name: "CURRENT_TABLE".to_string(),
                kind: EdgeKind::WritesAttr(WriteMode::Delete),
                line: line_num,
            });
        }
    }

    result.complexity = complexity;
    result
}

/// Calls made from an attribute's `[Formula: ...]`.
///
/// A formula can span several physical lines and can nest built-in calls, so the
/// body is scanned whole and every non-keyword invocation is reported with the
/// line it starts on. The caller turns these into `DefinesFormula` edges from the
/// attribute, which is what lets `impact` on a Procedure report the attributes
/// whose formula calls it (#501).
pub fn formula_references(source: &str) -> Vec<ParsedSymbolRef> {
    let mut out = Vec::new();

    for caps in RE_FORMULA.captures_iter(source) {
        let Some(body) = caps.get(1) else { continue };
        let formula = body.as_str();

        // Line the formula body starts on, so reported lines point at the
        // declaration instead of always line 1.
        let offset = caps.get(0).map(|m| m.start()).unwrap_or(0);
        let base_line = source[..offset].lines().count() as u32 + 1;

        for caps in RE_FUNCTION_CALL.captures_iter(formula) {
            let Some(name) = caps.get(1) else { continue };
            let callee = name.as_str();
            if is_call_keyword_or_builtin(callee) {
                continue;
            }

            let within = caps.get(0).map(|m| m.start()).unwrap_or(0);
            let consumed = formula[..within].lines().count() as u32;
            let line = base_line + consumed - u32::from(within == 0);

            out.push(ParsedSymbolRef {
                target_name: callee.to_string(),
                kind: EdgeKind::Calls(CallMode::Static),
                line,
            });
        }
    }

    for caps in RE_XML_PROPERTY_FORMULA.captures_iter(source) {
        let Some(body) = caps.get(1) else { continue };
        let raw_formula = body.as_str();
        let formula = raw_formula
            .replace("&gt;", ">")
            .replace("&lt;", "<")
            .replace("&quot;", "\"")
            .replace("&amp;", "&");

        let offset = caps.get(0).map(|m| m.start()).unwrap_or(0);
        let base_line = source[..offset].lines().count() as u32 + 1;

        for f_caps in RE_FUNCTION_CALL.captures_iter(&formula) {
            let Some(name) = f_caps.get(1) else { continue };
            let callee = name.as_str();
            if is_call_keyword_or_builtin(callee) {
                continue;
            }

            let within = f_caps.get(0).map(|m| m.start()).unwrap_or(0);
            let consumed = formula[..within].lines().count() as u32;
            let line = base_line + consumed - u32::from(within == 0);

            out.push(ParsedSymbolRef {
                target_name: callee.to_string(),
                kind: EdgeKind::Calls(CallMode::Static),
                line,
            });
        }
    }

    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_proc_calls_and_parm() {
        let code = r#"
            // Regra parm
            parm(in:&CliCod, out:&CliSaldo, inout:&Status);

            Call(PValidaCliente, &CliCod, &CliSaldo)
            &DynamicProc.Call(&CliCod)
            &Saldo = udp(PCalculaSaldo, &CliCod)

            For Each
                Where CliCod = &CliCod
                CliSaldo = &CliSaldo
                CliUltUpdate = ServerDate()
            Endfor
        "#;

        let res = parse_genexus_source(code);
        assert_eq!(
            res.parm_rule.as_deref(),
            Some("in:&CliCod, out:&CliSaldo, inout:&Status")
        );
        assert!(
            res.references
                .iter()
                .any(|r| r.target_name == "PValidaCliente"
                    && r.kind == EdgeKind::Calls(CallMode::Static))
        );
        assert!(res.references.iter().any(
            |r| r.target_name == "DynamicProc" && r.kind == EdgeKind::Calls(CallMode::Dynamic)
        ));
        assert!(
            res.references
                .iter()
                .any(|r| r.target_name == "PCalculaSaldo"
                    && r.kind == EdgeKind::Calls(CallMode::Udp))
        );
        assert!(res.references.iter().any(|r| r.target_name == "CliSaldo"
            && r.kind == EdgeKind::WritesAttr(WriteMode::ForEachUpdate)));
        assert!(
            res.references
                .iter()
                .any(|r| r.target_name == "CliUltUpdate"
                    && r.kind == EdgeKind::WritesAttr(WriteMode::ForEachUpdate))
        );
        assert!(
            res.references
                .iter()
                .any(|r| r.target_name == "CliCod" && r.kind == EdgeKind::ReadsAttr)
        );
    }
}
