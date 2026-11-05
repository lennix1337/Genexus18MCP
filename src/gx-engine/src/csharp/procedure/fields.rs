//! Field and cleanup/initialize emission for C# Procedures.

use super::{CsharpParam, ProcedureClass, allocation_expression};
use crate::csharp::types::{to_csharp_type, to_csharp_var_name};
use crate::model::{DataType, ParamMode};
use std::fmt::Write;

impl ProcedureClass {
    /// Where `cs_name` first appears in the emitted `ExecutePrivate` body, an assignment's value read
    /// before its target, else in the class so far: the SDK declares and initialises variables in that
    /// order within each type group (fields.py on the KBTeste NetModel: 84 of 84 Procedures).
    pub(super) fn first_use(code: &str, cs_name: &str) -> usize {
        let body = Self::reads_first(code);
        let at = |range: &str| {
            let word = |c: char| c.is_ascii_alphanumeric() || c == '_';
            range.match_indices(cs_name).map(|(at, _)| at).find(|&at| {
                !range[..at].chars().next_back().is_some_and(word)
                    && !range[at + cs_name.len()..].chars().next().is_some_and(word)
            })
        };
        at(&body).unwrap_or_else(|| code.len() + at(code).unwrap_or(code.len()))
    }

    /// Where a call temporary is last copied in (`GXt_decimal2 = AV8Amount;`): the SDK declares its
    /// temporaries in that order, within their type group (arusttypedcallprobe.cs: 2, 1, 5).
    pub(super) fn last_copy_in(code: &str, temp: &str) -> usize {
        Self::reads_first(code)
            .rfind(&format!(" {temp}\n"))
            .unwrap_or(code.len())
    }

    /// The `ExecutePrivate` body with each assignment written value first, target last.
    pub(super) fn reads_first(code: &str) -> String {
        // From `execute` on: a parameter is first used where `execute` copies it (84 of 84 KBTeste
        // Procedures, fields.py).
        let body_start = code
            .find("      public void execute(")
            .or_else(|| code.find("protected override void ExecutePrivate( )"))
            .unwrap_or(0);
        let body_end = code[body_start..]
            .find("public override void cleanup( )")
            .map_or(code.len(), |end| body_start + end);
        code[body_start..body_end]
            .lines()
            .map(|line| match line.trim_start().split_once(" = ") {
                Some((target, value))
                    if !target.is_empty()
                        && target
                            .chars()
                            .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '.') =>
                {
                    format!("{value} {target}\n")
                }
                _ => format!("{line}\n"),
            })
            .collect()
    }

    /// The SDK's initial value of a variable in `initialize( )`: text, dates and SDTs; numbers and
    /// booleans are not initialised (rustdisplayprobe, och42412500, odh42412500, rphout64375900).
    pub(super) fn sdk_initial_value(data_type: &DataType) -> Option<String> {
        match data_type {
            DataType::Character { .. }
            | DataType::Varchar { .. }
            | DataType::LongVarchar { .. } => Some("\"\"".to_string()),
            DataType::Date {} => Some("DateTime.MinValue".to_string()),
            DataType::DateTime {} => Some("(DateTime)(DateTime.MinValue)".to_string()),
            _ => allocation_expression(data_type),
        }
    }

    pub(super) fn emit_cleanup_and_initialize(&self, out: &mut String) {
        let _ = writeln!(out, "      public override void cleanup( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         CloseCursors();");
        let _ = writeln!(out, "         if ( IsMain )");
        let _ = writeln!(out, "         {{");
        let _ = writeln!(out, "            context.CloseConnections();");
        let _ = writeln!(out, "         }}");
        let _ = writeln!(out, "         ExitApp();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public override void initialize( )");
        let _ = writeln!(out, "      {{");
        if self.sdk_names() {
            // Every used variable but an input parameter, in the order the body first uses it.
            let mut initial: Vec<(usize, String, String)> = self
                .variables
                .iter()
                .filter_map(|(name, data_type)| {
                    let cs_name = self.cs_var(name);
                    let input = self
                        .params
                        .iter()
                        .any(|p| p.var_name == cs_name && p.mode != ParamMode::Out);
                    // A DataProvider's output (`Gxm2rootcol`) is allocated in `execute` only.
                    if input || !uses_identifier(out, &cs_name) || cs_name.starts_with("Gxm2") {
                        return None;
                    }
                    Self::sdk_initial_value(data_type)
                        .map(|value| (Self::first_use(out, &cs_name), cs_name, value))
                })
                .collect();
            initial.sort();
            for (_, cs_name, value) in initial {
                let _ = writeln!(out, "         {cs_name} = {value};");
            }
            let _ = writeln!(out, "         /* GeneXus formulas. */");
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
            return;
        }
        // The SDK allocates each SDT-typed local here (`AV8First = new SdtX(context);`, artdx765000a.cs).
        // A parameter is the caller's object: it is not allocated (rphout64375900.cs, `out SdtX aP0_Value`).
        for (var_name, data_type) in &self.variables {
            let Some(allocation) = allocation_expression(data_type) else {
                continue;
            };
            let cs_name = self.cs_var(var_name);
            // Unlike record parameters, the SDK initializes root collection parameters here too, and
            // an output record (`AV8Value = new SdtRustFreshSdt091817(context);`, rphout64375900.cs).
            if matches!(data_type, DataType::Record { .. })
                && self
                    .params
                    .iter()
                    .any(|p| p.var_name == cs_name && p.mode != ParamMode::Out)
            {
                continue;
            }
            let _ = writeln!(out, "         {cs_name} = {allocation};");
        }
        let _ = writeln!(out, "         /* GeneXus formulas. */");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    pub(super) fn emit_member_fields(&self, out: &mut String, temps: &[(&str, String)]) {
        if self.sdk_names() {
            // The SDK declares, in id order, only the variables the class uses (`AV8Outer`,
            // `AV9Inner`; adcm37961700.cs declares none of the standard variables it does not use).
            let mut fields: Vec<(usize, usize, String, String)> = self
                .variables
                .iter()
                .map(|(name, data_type)| {
                    let cs_name = self.cs_var(name);
                    (
                        sdk_type_group(data_type),
                        Self::first_use(out, &cs_name),
                        cs_name,
                        to_csharp_type(data_type),
                    )
                })
                .filter(|(_, _, cs_name, _)| uses_identifier(out, cs_name))
                .collect();
            if !self.subroutines.is_empty() {
                fields.push((
                    6,
                    Self::first_use(out, "returnInSub"),
                    "returnInSub".into(),
                    "bool".into(),
                ));
            }
            // The call temporaries are declared in their type's group (arusttypedcallprobe.cs).
            for (cs_type, name) in temps {
                let group = match *cs_type {
                    "short" => 0,
                    "int" => 1,
                    "long" => 2,
                    "decimal" => 3,
                    "DateTime" => 4,
                    "bool" => 6,
                    "string" => 8,
                    _ => 9,
                };
                fields.push((
                    group,
                    Self::last_copy_in(out, name),
                    name.clone(),
                    cs_type.to_string(),
                ));
            }
            fields.sort();
            for (_, _, cs_name, cs_type) in fields {
                let _ = writeln!(out, "      private {cs_type} {cs_name} ;");
            }
        } else {
            for (var_name, data_type) in &self.variables {
                let cs_type = to_csharp_type(data_type);
                let cs_name = to_csharp_var_name(var_name);
                let _ = writeln!(out, "      private {cs_type} {cs_name} ;");
            }
        }
        if let Some(ref ret_type) = self.return_type {
            if !self.variables.contains_key("Result") {
                let cs_ret = to_csharp_type(ret_type);
                let _ = writeln!(out, "      private {cs_ret} AV_Result ;");
            }
        }
        // The SDK declares a field for each in-out parameter, then each output: an input is read in
        // `execute` and never kept (rtphin69933200.cs, poi97435900.cs).
        let mut kept: Vec<&CsharpParam> = self.params.iter().collect();
        if self.sdk_names() {
            kept.retain(|p| p.mode != ParamMode::In);
            kept.sort_by_key(|p| p.mode == ParamMode::Out);
        }
        for p in kept {
            let cs_type = to_csharp_type(&p.data_type);
            let _ = writeln!(out, "      private {cs_type} {} ;", p.param_name);
        }
        // The call-site temporaries, declared as the SDK declares them: `GXt_<kind><n>`.
        for (cs_type, name) in temps.iter().filter(|_| !self.sdk_names()) {
            let _ = writeln!(out, "      private {cs_type} {name} ;");
        }
        if !self.navigation_levels.is_empty() {
            let _ = writeln!(out, "      private IDataStoreProvider pr_default ;");
            for level in &self.navigation_levels {
                let num = level.number;
                let _ = writeln!(out, "      private IDataReader P0001{num}_cursor ;");
            }
        }
    }
}

/// Whether `code` names `identifier` as a whole word.
pub(super) fn uses_identifier(code: &str, identifier: &str) -> bool {
    let word = |c: char| c.is_ascii_alphanumeric() || c == '_';
    code.match_indices(identifier).any(|(at, _)| {
        !code[..at].chars().next_back().is_some_and(word)
            && !code[at + identifier.len()..]
                .chars()
                .next()
                .is_some_and(word)
    })
}

/// The group the SDK declares a variable's field in: `short`, `int`, `long`, `decimal`, then DateTime,
/// Date, `bool`, Character, VarChar, and SDTs, in that order (fields.py over the KBTeste NetModel Procedures).
pub(super) fn sdk_type_group(data_type: &DataType) -> usize {
    match data_type {
        DataType::Numeric { .. } => match to_csharp_type(data_type).as_str() {
            "short" => 0,
            "int" => 1,
            "long" => 2,
            _ => 3,
        },
        DataType::DateTime {} => 4,
        DataType::Date {} => 5,
        DataType::Boolean {} => 6,
        DataType::Character { .. } => 7,
        DataType::Varchar { .. } | DataType::LongVarchar { .. } => 8,
        _ => 9,
    }
}
