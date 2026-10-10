use crate::model::{CallMode, EdgeKind, WriteMode};
use regex::Regex;
use std::sync::LazyLock;

static RE_PARM: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bparm\s*\(([^)]*)\)").unwrap()
});

static RE_CALL_FUNC: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bcall\s*\(\s*([&]?[a-zA-Z0-9_#]+)").unwrap()
});

static RE_CALL_METHOD: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)(?:^|[\s,;(=])([&]?[a-zA-Z0-9_#]+)\s*\.\s*call\s*\(").unwrap()
});

static RE_UDP: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\budp\s*\(\s*([&]?[a-zA-Z0-9_#]+)").unwrap()
});

static RE_SUBMIT: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bsubmit\s*\(\s*([&]?[a-zA-Z0-9_#]+)").unwrap()
});

static RE_LINK: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\blink\s*\(\s*([&]?[a-zA-Z0-9_#]+)").unwrap()
});

static RE_BC_METHOD: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)(?:^|[\s,;(=])([&][a-zA-Z0-9_#]+)\s*\.\s*(save|insert|update|delete|load)\s*\(").unwrap()
});

static RE_SUB: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?i)\bsub\s+['"]?([a-zA-Z0-9_# ]+)['"]?"#).unwrap()
});

static RE_DO_SUB: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?i)\bdo\s+['"]?([a-zA-Z0-9_# ]+)['"]?"#).unwrap()
});

static RE_ASSIGN: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^\s*([&]?[a-zA-Z0-9_#]+)\s*=").unwrap()
});

static RE_FOR_EACH: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bfor\s+each\b").unwrap()
});

static RE_END_FOR: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bendfor\b").unwrap()
});

static RE_NEW: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bnew\b").unwrap()
});

static RE_END_NEW: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bendnew\b").unwrap()
});

static RE_DELETE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bdelete\b").unwrap()
});

#[derive(Debug, Clone)]
pub struct ParsedSymbolRef {
    pub target_name: String,
    pub kind: EdgeKind,
    pub line: u32,
}

#[derive(Debug, Clone, Default)]
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
        if let Some(caps) = RE_PARM.captures(line) {
            if let Some(parm_content) = caps.get(1) {
                result.parm_rule = Some(parm_content.as_str().trim().to_string());
            }
        }

        // Subroutine definitions & calls
        if let Some(caps) = RE_SUB.captures(line) {
            if let Some(sub_name) = caps.get(1) {
                result.subroutines_defined.push(sub_name.as_str().trim().to_string());
            }
        }
        if let Some(caps) = RE_DO_SUB.captures(line) {
            if let Some(sub_name) = caps.get(1) {
                result.subroutines_called.push(sub_name.as_str().trim().to_string());
            }
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
        if let Some(caps) = RE_CALL_FUNC.captures(line) {
            if let Some(target) = caps.get(1) {
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
        }

        // 2. Target.Call(...)
        if let Some(caps) = RE_CALL_METHOD.captures(line) {
            if let Some(target) = caps.get(1) {
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
        }

        // 3. UDP(Target, ...)
        if let Some(caps) = RE_UDP.captures(line) {
            if let Some(target) = caps.get(1) {
                result.references.push(ParsedSymbolRef {
                    target_name: target.as_str().trim_start_matches('&').to_string(),
                    kind: EdgeKind::Calls(CallMode::Udp),
                    line: line_num,
                });
            }
        }

        // 4. Submit(Target, ...)
        if let Some(caps) = RE_SUBMIT.captures(line) {
            if let Some(target) = caps.get(1) {
                result.references.push(ParsedSymbolRef {
                    target_name: target.as_str().trim_start_matches('&').to_string(),
                    kind: EdgeKind::Calls(CallMode::Submit),
                    line: line_num,
                });
            }
        }

        // 5. Link(Target, ...)
        if let Some(caps) = RE_LINK.captures(line) {
            if let Some(target) = caps.get(1) {
                result.references.push(ParsedSymbolRef {
                    target_name: target.as_str().trim_start_matches('&').to_string(),
                    kind: EdgeKind::Calls(CallMode::Link),
                    line: line_num,
                });
            }
        }

        // 6. Business Component methods (&bc.Save(), &bc.Insert())
        if let Some(caps) = RE_BC_METHOD.captures(line) {
            if let Some(var_target) = caps.get(1) {
                result.references.push(ParsedSymbolRef {
                    target_name: var_target.as_str().to_string(),
                    kind: EdgeKind::Calls(CallMode::BusinessComponent),
                    line: line_num,
                });
            }
        }

        // Attribute mutations
        if let Some(caps) = RE_ASSIGN.captures(line) {
            if let Some(lhs) = caps.get(1) {
                let lhs_str = lhs.as_str();
                if !lhs_str.starts_with('&') {
                    // It's an Attribute assignment
                    let write_mode = if in_new {
                        WriteMode::New
                    } else if in_for_each {
                        WriteMode::ForEachUpdate
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
        assert_eq!(res.parm_rule.as_deref(), Some("in:&CliCod, out:&CliSaldo, inout:&Status"));
        assert!(res.references.iter().any(|r| r.target_name == "PValidaCliente" && r.kind == EdgeKind::Calls(CallMode::Static)));
        assert!(res.references.iter().any(|r| r.target_name == "DynamicProc" && r.kind == EdgeKind::Calls(CallMode::Dynamic)));
        assert!(res.references.iter().any(|r| r.target_name == "PCalculaSaldo" && r.kind == EdgeKind::Calls(CallMode::Udp)));
        assert!(res.references.iter().any(|r| r.target_name == "CliSaldo" && r.kind == EdgeKind::WritesAttr(WriteMode::ForEachUpdate)));
        assert!(res.references.iter().any(|r| r.target_name == "CliUltUpdate" && r.kind == EdgeKind::WritesAttr(WriteMode::ForEachUpdate)));
    }
}
