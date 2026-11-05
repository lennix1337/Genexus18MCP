//! The portable printer of statements.

use super::calls::statement_has_procedure_call;
use super::literal_shapes::literal_message_format;
use super::*;
use crate::frontend::ForEachStmt;

impl<'a> CSharpEmitter<'a> {
    /// Emits a C# statement string with indentation and trailing newline.
    pub fn emit_stmt(&mut self, stmt: &Stmt, vars: &BTreeMap<String, DataType>) -> String {
        let indent = self.indent();
        if self.calls.is_some() && statement_has_procedure_call(stmt) {
            self.fail(
                "a Procedure call in a member, attribute, collection or JSON statement has no SDK form in this build"
                    .to_string(),
            );
            return String::new();
        }
        match stmt {
            Stmt::Assign(var, expr) => {
                let lookup_key = var.trim_start_matches('&');
                let matched_var = vars.iter().find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(var)
                });
                let cs_var = if let Some((k, _)) = matched_var {
                    self.var_cs(k)
                } else {
                    self.var_cs(var)
                };
                if let Some((k, dt)) = matched_var {
                    if has_no_expression_form(dt) {
                        self.fail(format!(
                            "assignment to '{k}': a {} variable has no assignment form evidenced in this build",
                            kind_name(dt)
                        ));
                        return String::new();
                    }
                }
                // NEW() uses the same declared record/collection constructor as initialization.
                if let (Expr::Call(name, args), Some((_, data_type))) = (expr, matched_var) {
                    if name.eq_ignore_ascii_case("NEW") && args.is_empty() {
                        if let Some(allocation) =
                            crate::csharp::procedure::allocation_expression(data_type)
                        {
                            return format!("{indent}{cs_var} = {allocation};\n");
                        }
                    }
                }
                if let Some(calls) = self.calls {
                    if !procedure_calls(expr).is_empty() {
                        return match matched_var {
                            Some((_, dt))
                                if !matches!(
                                    dt,
                                    DataType::Record { .. } | DataType::Collection { .. }
                                ) =>
                            {
                                self.emit_assignment_with_call(&cs_var, dt, expr, &indent, calls)
                            }
                            _ => {
                                self.fail(format!(
                                    "assignment to '{var}' with a Procedure call: the target is not a scalar variable"
                                ));
                                String::new()
                            }
                        };
                    }
                }
                // C# has no implicit conversion between a number, a text, a date-time or a boolean, and
                // this build evidences none of these conversions from the SDK. The assignment would not
                // compile, so it is refused by name; only a proven source type is checked.
                if let Some((_, target)) = matched_var {
                    if let (Some(target_family), Some((source, source_family))) =
                        (family_of(target), self.source_kind(expr))
                    {
                        if target_family != source_family {
                            self.fail(format!(
                                "assignment to '{lookup_key}': a {source} value cannot be assigned to a {} variable; no conversion between these types is evidenced in this build",
                                kind_name(target)
                            ));
                            return String::new();
                        }
                    }
                }
                if self.sdk_form() {
                    if let Some((_, dt)) = matched_var {
                        if let Some(text) = self.sdk_assigned(dt, expr) {
                            return format!("{indent}{cs_var} = {text};\n");
                        }
                    }
                }
                let expr_str = self.emit_expr(expr);
                if let Some((_, dt)) = matched_var {
                    if matches!(dt, DataType::Numeric { .. }) {
                        let cs_type = to_csharp_type(dt);
                        return format!("{indent}{cs_var} = ({cs_type})({expr_str});\n");
                    }
                }
                format!("{indent}{cs_var} = {expr_str};\n")
            }
            Stmt::AssignField(target, field, expr) => {
                // The same family rule as a variable: a member's type comes from its SDT, and a value of
                // another family has no evidenced conversion, so it is refused by name.
                let member = Expr::Member(target.clone(), field.clone());
                if let (Some(member_type), Some((source, source_family))) =
                    (self.value_type(&member), self.source_kind(expr))
                {
                    if let Some(member_family) = family_of(&member_type) {
                        if member_family != source_family {
                            self.fail(format!(
                                "assignment to member '{field}': a {source} value cannot be assigned to a {} member; no conversion between these types is evidenced in this build",
                                kind_name(&member_type)
                            ));
                            return String::new();
                        }
                    }
                }
                let t = self.member_target(target, field);
                let e = self.emit_expr(expr);
                format!("{indent}{t} = {e};\n")
            }
            Stmt::CollectionAdd(col, item, opt_pos) => {
                let c = self.collection_target(col, "Add");
                self.check_collection_item(col, item);
                let i = self.emit_expr(item);
                if let Some(pos) = opt_pos {
                    let p = self.emit_expr(pos);
                    format!("{indent}{c}.Add({i}, {p});\n")
                } else {
                    // The SDK appends without a position as `X.gxTpr_Rows.Add(AV9Row, 0);`: 31 member
                    // appends and 1 variable append in the KBTeste NetModel output (`artdx765000a.cs`).
                    format!("{indent}{c}.Add({i}, 0);\n")
                }
            }
            Stmt::CollectionRemove(col, index) => {
                let c = self.collection_target(col, "Remove");
                let idx = self.emit_expr(index);
                format!("{indent}{c}.RemoveItem((int)({idx}));\n")
            }
            Stmt::CollectionClear(col) => {
                let c = self.collection_target(col, "Clear");
                format!("{indent}{c}.Clear();\n")
            }
            Stmt::FromJson(target, expr) => {
                let t = self.emit_expr(target);
                let e = self.emit_expr(expr);
                format!("{indent}{t}.FromJSonString({e}, null);\n")
            }
            Stmt::FromXml(from_xml) => {
                let t = self.emit_expr(&from_xml.target);
                let x = self.emit_expr(&from_xml.xml);
                if let Some(root) = &from_xml.root {
                    let r = self.emit_expr(root);
                    format!("{indent}{t}.FromXml({x}, {r});\n")
                } else {
                    format!("{indent}{t}.FromXml({x});\n")
                }
            }
            Stmt::Print(expr) | Stmt::MsgStatus(expr) => {
                // `StatusMessage` takes a string. A numeric message is written by the SDK as `StringUtil.LTrimStr(...)`
                // with the expression's own precision and scale (arustdisplayprobe.cs line 77), which is not evidenced
                // for these expressions, so it is refused rather than written as an int argument.
                if self.is_numeric_value(expr) {
                    match literal_message_format(expr) {
                        Some((precision, scale)) if self.sdk_form() => {
                            match self.sdk_numeric_message(expr, precision, scale) {
                                Some(text) => format!("{indent}context.StatusMessage( {text} );\n"),
                                None => {
                                    let e = self.emit_expr(expr);
                                    format!(
                                        "{indent}context.StatusMessage( StringUtil.LTrimStr( {e}, {precision}, {scale}) );\n"
                                    )
                                }
                            }
                        }
                        Some((precision, scale)) => {
                            let e = self.emit_expr(expr);
                            format!(
                                "{indent}context.StatusMessage( StringUtil.LTrimStr( {e}, {precision}, {scale}) );\n"
                            )
                        }
                        None => {
                            self.fail("message of a numeric value: the SDK writes StringUtil.LTrimStr with the expression's precision and scale, which is not evidenced for this expression".to_string());
                            String::new()
                        }
                    }
                } else {
                    let e = self.emit_expr(expr);
                    format!("{indent}context.StatusMessage( {e} );\n")
                }
            }
            Stmt::Return(expr) => {
                let e = self.emit_expr(expr);
                format!("{indent}AV_Result = {e};\n{indent}cleanup();\n{indent}return;\n")
            }
            Stmt::ReturnVoid if self.sdk_form() && self.in_subroutine.get() => {
                format!("{indent}returnInSub = true;\n{indent}if (true) return;\n")
            }
            Stmt::ReturnVoid if self.sdk_form() => {
                // The SDK's return from a Procedure body (afrq12224800.cs).
                format!("{indent}cleanup();\n{indent}if (true) return;\n")
            }
            Stmt::ReturnVoid => {
                format!("{indent}cleanup();\n{indent}return;\n")
            }
            Stmt::If(cond, then_branch, else_branch) if self.var_ids.is_some() => {
                // The SDK writes a Do Case, and an Else holding only an If, as `else if` (adcm37961700.cs).
                let mut out = String::new();
                let mut keyword = "if";
                let (mut cond, mut then_branch, mut else_branch) = (cond, then_branch, else_branch);
                loop {
                    let c = self.emit_condition(cond);
                    out.push_str(&format!("{indent}{keyword} ( {c} )\n{indent}{{\n"));
                    self.indented(|this| {
                        for s in then_branch {
                            out.push_str(&this.emit_stmt(s, vars));
                        }
                    });
                    out.push_str(&format!("{indent}}}\n"));
                    match else_branch.as_slice() {
                        [] => break,
                        [Stmt::If(c, t, e)] => {
                            keyword = "else if";
                            (cond, then_branch, else_branch) = (c, t, e);
                        }
                        rest => {
                            out.push_str(&format!("{indent}else\n{indent}{{\n"));
                            self.indented(|this| {
                                for s in rest {
                                    out.push_str(&this.emit_stmt(s, vars));
                                }
                            });
                            out.push_str(&format!("{indent}}}\n"));
                            break;
                        }
                    }
                }
                out
            }
            Stmt::If(cond, then_branch, else_branch) => {
                let c = self.emit_expr(cond);
                let mut out = format!("{indent}if ( {c} )\n{indent}{{\n");
                self.indented(|this| {
                    for s in then_branch {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                if !else_branch.is_empty() {
                    out.push_str(&format!("{indent}else\n{indent}{{\n"));
                    self.indented(|this| {
                        for s in else_branch {
                            out.push_str(&this.emit_stmt(s, vars));
                        }
                    });
                    out.push_str(&format!("{indent}}}\n"));
                }
                out
            }
            Stmt::While(cond, body) => {
                let c = self.emit_condition(cond);
                let mut out = format!("{indent}while ( {c} )\n{indent}{{\n");
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                out
            }
            Stmt::For(var, start, end, stride, body) => {
                let lookup_key = var.trim_start_matches('&');
                let matched_var = vars.iter().find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(var)
                });
                // The loop is `for (x = a; x <= b; x++)`, which needs a numeric iterator. The typed backend
                // refuses a non-numeric iterator for the same reason; the SDK never writes a loop over one.
                if let Some((name, data_type)) = matched_var {
                    if family_of(data_type) != Some(ValueFamily::Numeric) {
                        self.fail(format!(
                            "For '{name}': the iterator is a {} variable; this build writes For only over a numeric variable",
                            kind_name(data_type)
                        ));
                        return String::new();
                    }
                }
                let cs_var = if let Some((k, _)) = matched_var {
                    self.var_cs(k)
                } else {
                    self.var_cs(var)
                };
                // A start value that calls a Procedure is assigned before the loop, as the SDK does
                // (`GXt_int1 = AV_I; execute(...); AV_I = GXt_int1; while (AV_I <= 3)`).
                let hoisted = match self.calls {
                    Some(_) if !procedure_calls(start).is_empty() => {
                        Some(self.emit_stmt(&Stmt::Assign(var.clone(), start.clone()), vars))
                    }
                    _ => None,
                };
                let init = if hoisted.is_some() {
                    String::new()
                } else {
                    format!("{cs_var} = {}", self.emit_expr(start))
                };
                let e = self.emit_expr(end);
                let cmp = if *stride >= 0 { "<=" } else { ">=" };
                if self.var_ids.is_some() {
                    // The SDK's loop: the start assigned, a `while`, and the step added at the end of the
                    // body (`AV8I = (int)(AV8I+1);`, `AV8I+-1` downward; afrc12224800.cs, adcs37961700.cs).
                    let mut out = hoisted.unwrap_or_else(|| {
                        self.emit_stmt(&Stmt::Assign(var.clone(), start.clone()), vars)
                    });
                    out.push_str(&format!(
                        "{indent}while ( {cs_var} {cmp} {e} )\n{indent}{{\n"
                    ));
                    self.indented(|this| {
                        for s in body {
                            out.push_str(&this.emit_stmt(s, vars));
                        }
                    });
                    let cs_type =
                        matched_var.map_or("int".to_string(), |(_, dt)| to_csharp_type(dt));
                    out.push_str(&format!(
                        "{indent}   {cs_var} = ({cs_type})({cs_var}+{stride});\n{indent}}}\n"
                    ));
                    return out;
                }
                let step = if *stride == 1 {
                    format!("{cs_var}++")
                } else if *stride == -1 {
                    format!("{cs_var}--")
                } else if *stride >= 0 {
                    format!("{cs_var} += {stride}")
                } else {
                    format!("{cs_var} -= {}", -stride)
                };
                let mut out = hoisted.unwrap_or_default();
                out.push_str(&format!(
                    "{indent}for ( {init}; {cs_var} {cmp} {e}; {step} )\n{indent}{{\n"
                ));
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                out
            }
            Stmt::ForIn(item, col, body) if self.sdk_form() && self.for_in_counter(1).is_some() => {
                // The SDK walks a collection by index (general/ui/sidebaritemsdp.cs): `AV9GXV1 = 1;`,
                // `while ( AV9GXV1 <= AV5wwProgramNames.Count )`, the item read by `Item`, the step last.
                let counter = self.for_in_counter(1).unwrap_or_default();
                let c = self.emit_expr(col);
                let item_type = vars
                    .iter()
                    .find(|(k, _)| k.eq_ignore_ascii_case(item.trim_start_matches('&')))
                    .map(|(_, dt)| to_csharp_type(dt))
                    .unwrap_or_default();
                let cs_item = self.var_cs(item);
                let mut out = format!(
                    "{indent}{counter} = 1;\n{indent}while ( {counter} <= {c}.Count )\n{indent}{{\n{indent}   {cs_item} = (({item_type}){c}.Item({counter}));\n"
                );
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!(
                    "{indent}   {counter} = (int)({counter}+1);\n{indent}}}\n"
                ));
                out
            }
            Stmt::ForIn(item, col, body) => {
                let lookup_key = item.trim_start_matches('&');
                let matched_var = vars.iter().find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(item)
                });
                let cs_item = if let Some((k, _)) = matched_var {
                    self.var_cs(k)
                } else {
                    self.var_cs(item)
                };
                let c = self.emit_expr(col);
                let mut out = format!("{indent}foreach ( var {cs_item} in {c} )\n{indent}{{\n");
                self.indented(|this| {
                    for s in body {
                        out.push_str(&this.emit_stmt(s, vars));
                    }
                });
                out.push_str(&format!("{indent}}}\n"));
                out
            }
            Stmt::ForEach(for_each_stmt) => {
                if let Some(levels) = self.navigation_levels {
                    let idx = self.cursor_index.get();
                    if let Some(level) = levels.get(idx) {
                        self.cursor_index.set(idx + 1);
                        return self.emit_for_each_cursor(for_each_stmt, level, idx, &indent, vars);
                    }
                }
                self.refuse_database_statement("For Each");
                let mut out = format!("{indent}/* GeneXus For Each block */\n");
                for s in &for_each_stmt.body {
                    out.push_str(&self.emit_stmt(s, vars));
                }
                out
            }
            Stmt::AssignAttribute(attr, expr) => {
                // The parser reads an attribute assignment only inside a For Each or New, where it writes a
                // column of the current row. No Procedure statement has a bare attribute field to assign.
                if self.navigation_levels.is_none() {
                    self.refuse_database_statement(&format!("attribute assignment '{attr}'"));
                }
                let cs_attr = to_csharp_attr_name(attr);
                let e = self.emit_expr(expr);
                format!("{indent}{cs_attr} = {e};\n")
            }
            Stmt::Delete => {
                self.refuse_database_statement("Delete");
                format!("{indent}/* delete current row */\n")
            }
            Stmt::New(new_stmt) => {
                self.refuse_database_statement("New");
                let mut out = format!("{indent}/* new record */\n");
                for s in &new_stmt.body {
                    out.push_str(&self.emit_stmt(s, vars));
                }
                out
            }
            Stmt::Exit => format!("{indent}break;\n"),
            Stmt::Call(Expr::Call(name, args)) => {
                if let Some(calls) = self.calls {
                    return self.emit_statement_call(name, args, &indent, calls);
                }
                let args_str = args
                    .iter()
                    .map(|a| self.emit_expr(a))
                    .collect::<Vec<_>>()
                    .join(", ");
                let call_args = if args_str.is_empty() {
                    String::new()
                } else {
                    args_str
                };
                format!("{indent}new {name}(context).execute({call_args});\n")
            }
            Stmt::Call(other) => {
                let e = self.emit_expr(other);
                format!("{indent}{e};\n")
            }
            Stmt::DoSub(name) if self.sdk_form() => {
                // listprograms.cs: the call, then the leave when the subroutine returned.
                let method = self.subroutine_method(name);
                let leave = if self.in_subroutine.get() {
                    format!("{indent}   returnInSub = true;\n")
                } else {
                    format!("{indent}   cleanup();\n")
                };
                format!(
                    "{indent}/* Execute user subroutine: '{}' */\n{indent}{method} ();\n{indent}if ( returnInSub )\n{indent}{{\n{leave}{indent}   if (true) return;\n{indent}}}\n",
                    name.to_ascii_uppercase()
                )
            }
            Stmt::DoSub(name) => {
                let clean = crate::csharp::types::clean_ident(name);
                format!("{indent}Sub_{clean}();\n")
            }
            Stmt::Sub(_name, _body) => {
                // Handled as separate member method
                String::new()
            }
            Stmt::Commit => format!("{indent}context.CommitDataStores();\n"),
            Stmt::Rollback => format!("{indent}context.RollbackDataStores();\n"),
        }
    }

    fn emit_for_each_cursor(
        &mut self,
        for_each_stmt: &ForEachStmt,
        level: &crate::navigation::NavigationLevel,
        idx: usize,
        indent: &str,
        vars: &BTreeMap<String, DataType>,
    ) -> String {
        let num = level.number;
        let mut out = format!("{indent}/* Using cursor P0001{num} */\n");
        out.push_str(&format!("{indent}pr_default.execute({idx});\n"));
        out.push_str(&format!(
            "{indent}while ( (pr_default.getStatus({idx}) != 101) )\n{indent}{{\n"
        ));
        self.indented(|this| {
            for s in &for_each_stmt.body {
                out.push_str(&this.emit_stmt(s, vars));
            }
            out.push_str(&format!("{indent}   pr_default.readNext({idx});\n"));
        });
        out.push_str(&format!("{indent}}}\n{indent}pr_default.close({idx});\n"));
        if level.has_when_none && !for_each_stmt.when_none.is_empty() {
            out.push_str(&format!(
                "{indent}if ( pr_default.getStatus({idx}) == 101 )\n{indent}{{\n"
            ));
            self.indented(|this| {
                for s in &for_each_stmt.when_none {
                    out.push_str(&this.emit_stmt(s, vars));
                }
            });
            out.push_str(&format!("{indent}}}\n"));
        }
        out
    }
}
