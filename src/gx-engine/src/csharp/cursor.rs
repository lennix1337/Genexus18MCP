//! ADO.NET Cursor definitions and emission for GeneXus C# procedures and panels.

use crate::model::DataType;
use crate::navigation::NavigationLevel;
use std::{
    collections::{BTreeMap, BTreeSet},
    fmt::Write,
};

/// Maps a declared `DataType` to its ADO.NET `ParDef` tuple `(GXType, length, decimals)`.
pub fn data_type_to_par_def(data_type: &DataType) -> (&'static str, usize, usize) {
    match data_type {
        DataType::Numeric { precision, scale } => {
            let p = *precision as usize;
            let s = *scale as usize;
            if s > 0 {
                ("GXType.Decimal", p, s)
            } else if p <= 4 {
                ("GXType.Int16", p, 0)
            } else if p <= 9 {
                ("GXType.Int32", p, 0)
            } else {
                ("GXType.Int64", p, 0)
            }
        }
        DataType::Character { length } => ("GXType.Char", *length as usize, 0),
        DataType::Varchar { length } => ("GXType.NVarChar", *length as usize, 0),
        DataType::Boolean {} => ("GXType.Boolean", 1, 0),
        DataType::Date {} => ("GXType.Date", 8, 0),
        DataType::DateTime {} => ("GXType.DateTime", 8, 5),
        _ => ("GXType.NVarChar", 100, 0),
    }
}

fn strip_param_prefix(name: &str) -> &str {
    let raw = name.trim_start_matches(['@', ':']);
    let lower = raw.to_ascii_lowercase();
    if let Some(rest) = raw.strip_prefix("AV_").or_else(|| raw.strip_prefix("av_")) {
        return rest;
    }
    if lower.starts_with("av") || lower.starts_with("lv") || lower.starts_with("lp") {
        let after_prefix = &raw[2..];
        let after_digits = after_prefix.trim_start_matches(|c: char| c.is_ascii_digit());
        if !after_digits.is_empty() {
            return after_digits;
        }
    }
    if let Some(rest) = raw.strip_prefix("A_").or_else(|| raw.strip_prefix("a_")) {
        return rest;
    }
    raw
}

/// Resolves the `ParDef` tuple for a cursor parameter from declared variables first,
/// falling back to `infer_param_def` only when the symbol is not in `variables`.
pub fn resolve_param_def(
    name: &str,
    variables: &BTreeMap<String, DataType>,
) -> (&'static str, usize, usize) {
    let lower = name.to_ascii_lowercase();
    if lower.starts_with("@lv")
        || lower.starts_with("lv")
        || lower.starts_with("@lp")
        || lower.starts_with("lp")
    {
        return ("GXType.NVarChar", 100, 0);
    }
    let bare = strip_param_prefix(name);
    if let Some((_, dt)) = variables
        .iter()
        .find(|(k, _)| k.trim_start_matches('&').eq_ignore_ascii_case(bare))
    {
        return data_type_to_par_def(dt);
    }
    infer_param_def(name)
}

/// Infers the GXType, length and decimals for a query parameter based on GeneXus conventions.
pub fn infer_param_def(name: &str) -> (&'static str, usize, usize) {
    let lower = name.to_ascii_lowercase();
    if lower.starts_with("@lv")
        || lower.starts_with("lv")
        || lower.starts_with("@lp")
        || lower.starts_with("lp")
    {
        ("GXType.NVarChar", 100, 0)
    } else if lower.contains("datetime") {
        ("GXType.DateTime", 8, 5)
    } else if lower.contains("date") {
        ("GXType.Date", 8, 0)
    } else if lower.contains("guid") {
        ("GXType.UniqueIdentifier", 16, 0)
    } else if lower.contains("flag")
        || lower.contains("active")
        || lower.contains("status_bool")
        || lower.contains("enabled")
        || lower.starts_with("is_")
        || lower.starts_with("@is_")
        || lower.starts_with("has_")
        || lower.starts_with("@has_")
    {
        ("GXType.Boolean", 1, 0)
    } else if lower.contains("price")
        || lower.contains("total")
        || lower.contains("amount")
        || lower.contains("rate")
        || lower.contains("salary")
        || lower.contains("cost")
        || (lower.contains("val") && !lower.contains("invalid") && !lower.contains("valid"))
        || lower.contains("qty")
        || lower.contains("quantity")
        || lower.contains("discount")
        || lower.contains("tax")
        || lower.contains("percent")
        || lower.contains("saldo")
        || lower.contains("valor")
    {
        ("GXType.Decimal", 12, 2)
    } else if lower.contains("code")
        || lower.contains("desc")
        || lower.contains("name")
        || lower.contains("text")
        || lower.contains("type")
        || lower.contains("email")
        || lower.contains("mail")
        || lower.contains("phone")
        || lower.contains("address")
        || lower.contains("url")
        || lower.contains("path")
        || lower.contains("comment")
        || lower.contains("obs")
        || lower.contains("title")
        || lower.contains("msg")
        || lower.contains("message")
        || lower.contains("status")
        || lower.contains("prefix")
        || lower.contains("suffix")
        || lower.contains("mode")
        || lower.contains("state")
        || lower.contains("category")
        || lower.contains("uf")
        || lower.contains("cep")
        || lower.contains("cpf")
        || lower.contains("cnpj")
    {
        ("GXType.NVarChar", 100, 0)
    } else {
        ("GXType.Int32", 9, 0)
    }
}

fn strip_sql_literals_and_comments(sql: &str) -> String {
    let mut result = String::with_capacity(sql.len());
    let mut chars = sql.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\'' {
            while let Some(next_c) = chars.next() {
                if next_c == '\'' {
                    if chars.peek() == Some(&'\'') {
                        chars.next();
                    } else {
                        break;
                    }
                }
            }
            result.push(' ');
        } else if c == '/' && chars.peek() == Some(&'*') {
            chars.next();
            while let Some(next_c) = chars.next() {
                if next_c == '*' && chars.peek() == Some(&'/') {
                    chars.next();
                    break;
                }
            }
            result.push(' ');
        } else if c == '-' && chars.peek() == Some(&'-') {
            chars.next();
            for next_c in chars.by_ref() {
                if next_c == '\n' {
                    break;
                }
            }
            result.push('\n');
        } else {
            result.push(c);
        }
    }
    result
}

/// Extracts parameter names from navigation level filters, formulas, joins, and SQL.
pub fn extract_parameters(level: &NavigationLevel) -> Vec<String> {
    let mut params = Vec::new();
    let mut seen = BTreeSet::new();

    let mut scan_words = |text: &str| {
        let clean = strip_sql_literals_and_comments(text);
        for word in clean.split(|c: char| !c.is_alphanumeric() && c != '@' && c != ':' && c != '_')
        {
            if (word.starts_with('@') || word.starts_with(':')) && word.len() > 1 {
                let p = format!("@{}", word.trim_start_matches(['@', ':']));
                if seen.insert(p.clone()) {
                    params.push(p);
                }
            }
        }
    };

    scan_words(&level.sql);

    for f in &level.filters {
        scan_words(&f.expression);
    }

    for join in &level.joins {
        scan_words(&join.on_condition);
    }

    for formula in &level.formulas {
        scan_words(&formula.sql);
        for cond in &formula.conditions {
            scan_words(&cond.expression);
        }
        for corr in &formula.correlations {
            scan_words(corr);
        }
    }

    for dml in &level.dml_plans {
        for pred in &dml.where_predicates {
            scan_words(pred);
        }
        for sql in dml.sql.values() {
            scan_words(sql);
        }
        for assign in &dml.assignments {
            scan_words(&assign.expr_sql);
        }
    }

    params
}

/// Emits the C# `public override void cursorDefinitions( )` method for a set of navigation levels.
pub fn emit_cursor_definitions(levels: &[NavigationLevel], out: &mut String) {
    emit_cursor_definitions_with_vars(levels, &BTreeMap::new(), out);
}

/// Emits the C# `public override void cursorDefinitions( )` method resolving parameter types
/// against declared `variables` first.
pub fn emit_cursor_definitions_with_vars(
    levels: &[NavigationLevel],
    variables: &BTreeMap<String, DataType>,
    out: &mut String,
) {
    let _ = writeln!(out, "      public override void cursorDefinitions( )");
    let _ = writeln!(out, "      {{");
    let _ = writeln!(out, "         if ( def == null )");
    let _ = writeln!(out, "         {{");
    for level in levels {
        let num = level.number;
        let params = extract_parameters(level);
        let _ = writeln!(out, "            Object[] prmP0001{num};");
        let _ = writeln!(out, "            prmP0001{num} = new Object[] {{");
        for (idx, p) in params.iter().enumerate() {
            let (gx_type, len, dec) = resolve_param_def(p, variables);
            let comma = if idx + 1 < params.len() { " ," } else { "" };
            let _ = writeln!(
                out,
                "            new ParDef(\"{p}\",{gx_type},{len},{dec}){comma}"
            );
        }
        let _ = writeln!(out, "            }};");
    }
    let _ = writeln!(out, "            def = new CursorDef[] {{");
    for (i, level) in levels.iter().enumerate() {
        let num = level.number;
        let escaped_sql = level.sql.replace('"', "\"\"");
        let comma = if i + 1 < levels.len() { "," } else { "" };
        let _ = writeln!(
            out,
            "                new CursorDef(\"P0001{num}\", @\"{escaped_sql}\", false, GxErrorMask.GX_NOMASK | GxErrorMask.GX_MASKLOOPLOCK, false, this, prmP0001{num}, 100, GxCacheFrequency.OFF, true, false){comma}"
        );
    }
    let _ = writeln!(out, "            }};");
    let _ = writeln!(out, "         }}");
    let _ = writeln!(out, "      }}");
    let _ = writeln!(out);
}
