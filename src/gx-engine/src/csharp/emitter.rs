//! C# syntax emitter for expressions and statements.

use crate::{
    csharp::{
        calls::{
            CallParam, CallSignatures, CallTarget, is_builtin_name, procedure_calls, temp_cs_type,
            temp_type,
        },
        sdt::{SdtIndex, SdtMemberType, sdt_item, sdt_member, sdt_type},
        types::{to_csharp_attr_name, to_csharp_type, to_csharp_var_name},
    },
    frontend::{BinOp, Expr, Stmt},
    model::{DataType, ParamMode},
};
use std::cell::RefCell;
use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Write;

mod calls;
mod expr;
mod literal_shapes;
mod sdk_expr;
mod stmt;

/// The call-site temporaries of one procedure, numbered as the SDK numbers them. A counter shared by every
/// type gives each new temporary its number. A temporary a statement has finished with goes back to a stack
/// for its type, and the next call of that type takes the most recently released one. Evidence:
/// arusttypedcallprobe.cs lines 70-97 (`GXt_boolean3` follows `GXt_decimal1` and `GXt_decimal2`, and the
/// third call of a statement takes `GXt_decimal2` and then `GXt_decimal1` before the new `GXt_decimal5`),
/// and apoa97435900.cs lines 71-75 (the outer call's temporary is planned before the inner call's).
#[derive(Default)]
struct TempPool {
    counter: u32,
    free: BTreeMap<&'static str, Vec<u32>>,
    declared: BTreeSet<(&'static str, u32)>,
}

impl TempPool {
    /// Takes a temporary of `kind`: the most recently released one, or a new number.
    fn alloc(&mut self, kind: &'static str) -> (String, (&'static str, u32)) {
        let number = match self.free.get_mut(kind).and_then(|stack| stack.pop()) {
            Some(number) => number,
            None => {
                self.counter += 1;
                self.counter
            }
        };
        self.declared.insert((kind, number));
        (format!("GXt_{kind}{number}"), (kind, number))
    }

    /// Returns the temporaries of a finished statement, in the order they were taken.
    fn release(&mut self, allocated: &[(&'static str, u32)]) {
        for (kind, number) in allocated {
            self.free.entry(kind).or_default().push(*number);
        }
    }

    /// Every temporary this procedure uses, as (C# type, name), in a stable order.
    fn declarations(&self) -> Vec<(&'static str, String)> {
        self.declared
            .iter()
            .map(|(kind, number)| (temp_cs_type(kind), format!("GXt_{kind}{number}")))
            .collect()
    }
}

/// The C# family of a declared type. Two types of different families have no implicit conversion in C#
/// and no conversion this build evidences from the SDK, so an assignment across families is refused.
/// `Date` and `DateTime` are one family: both are `DateTime`.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum ValueFamily {
    Numeric,
    Text,
    DateTime,
    Boolean,
}

fn family_of(data_type: &DataType) -> Option<ValueFamily> {
    match data_type {
        DataType::Numeric { .. } => Some(ValueFamily::Numeric),
        DataType::Character { .. } | DataType::Varchar { .. } | DataType::LongVarchar { .. } => {
            Some(ValueFamily::Text)
        }
        DataType::Date {} | DataType::DateTime {} => Some(ValueFamily::DateTime),
        DataType::Boolean {} => Some(ValueFamily::Boolean),
        DataType::Record { .. }
        | DataType::Collection { .. }
        | DataType::BaseCollection { .. }
        | DataType::Guid {}
        | DataType::Image {}
        | DataType::Geospatial {} => None,
    }
}

/// A declared type whose C# form is evidenced for a declaration and an SDT member, but whose
/// expression forms (assignment, comparison, helper calls) are not. A statement or expression that
/// reads or writes such a value is refused by name rather than emitted as an uncompilable C# line.
fn has_no_expression_form(data_type: &DataType) -> bool {
    matches!(
        data_type,
        DataType::Guid {} | DataType::Image {} | DataType::Geospatial {}
    )
}

/// The GeneXus kind of a declared type, lower case, for refusal messages.
fn kind_name(data_type: &DataType) -> &'static str {
    match data_type {
        DataType::Boolean {} => "boolean",
        DataType::Character { .. } => "character",
        DataType::Varchar { .. } => "varchar",
        DataType::Date {} => "date",
        DataType::DateTime {} => "datetime",
        DataType::Numeric { .. } => "numeric",
        DataType::LongVarchar { .. } => "longvarchar",
        DataType::Guid {} => "guid",
        DataType::Image {} => "image",
        DataType::Geospatial {} => "geospatial",
        DataType::Record { .. } => "record",
        DataType::Collection { .. } | DataType::BaseCollection { .. } => "collection",
    }
}

/// Configurable C# code generator and indentation manager.
pub struct CSharpEmitter<'a> {
    indent_level: usize,
    indent_unit: String,
    vars: Option<&'a BTreeMap<String, DataType>>,
    /// The stored id of each variable, by name. With an id a variable is written as the SDK names its
    /// field (`AV8Hits`); without one, as `AV_Hits`.
    var_ids: Option<&'a BTreeMap<String, u32>>,
    /// The Procedure's subroutines in source order, for the SDK's `S111` names (listprograms.cs).
    subroutines: Vec<String>,
    /// Whether a subroutine body is being written: a `Return` there leaves through `returnInSub`.
    pub(crate) in_subroutine: std::cell::Cell<bool>,
    sdts: Option<SdtScope<'a>>,
    /// The Procedures this build emits, for call sites. `None` writes calls as they were (unit and
    /// WebPanel paths); a Procedure build always sets it.
    calls: Option<&'a CallSignatures>,
    /// Constructs this emitter could not write in the SDK's form, by name. A build refuses on any.
    refusals: RefCell<Vec<String>>,
    /// The temporary each Procedure call of the statement being emitted writes to, keyed by the address of
    /// the call's name (unique per call site). Set while the statement is planned, then cleared.
    call_temps: RefCell<BTreeMap<usize, String>>,
    /// The call-site temporaries of this class, numbered as the SDK numbers them (see `TempPool`).
    temps: RefCell<TempPool>,
    /// The class names (upper-cased) of the Procedures this class calls. A build refuses a caller whose
    /// callee it does not generate, so it needs to know these.
    callees: RefCell<BTreeSet<String>>,
    navigation_levels: Option<&'a [crate::navigation::NavigationLevel]>,
    cursor_index: std::cell::Cell<usize>,
}

/// The SDTs a Procedure resolves its member accesses against. Without a scope, member access is written
/// as it was (WebPanel code, which is not a compile unit).
struct SdtScope<'a> {
    index: &'a SdtIndex,
}

impl Default for CSharpEmitter<'_> {
    fn default() -> Self {
        Self::new()
    }
}

impl<'a> CSharpEmitter<'a> {
    /// Creates a new emitter using the classic GeneXus 3-space indentation.
    pub fn new() -> Self {
        Self::bare(None, None, "   ".to_string())
    }

    /// Creates an emitter with a variable context for case-insensitive identifier preservation.
    pub fn with_vars(vars: &'a BTreeMap<String, DataType>) -> Self {
        Self::bare(Some(vars), None, "   ".to_string())
    }

    /// Creates an emitter that resolves SDT member accesses of the variables in `vars` against `index`.
    /// An access that does not resolve is refused (see [`CSharpEmitter::refusals`]).
    pub fn with_sdts(vars: &'a BTreeMap<String, DataType>, index: &'a SdtIndex) -> Self {
        Self::bare(Some(vars), Some(SdtScope { index }), "   ".to_string())
    }

    /// Creates an emitter with custom indentation unit.
    pub fn with_indent_unit(unit: impl Into<String>) -> Self {
        Self::bare(None, None, unit.into())
    }

    fn bare(
        vars: Option<&'a BTreeMap<String, DataType>>,
        sdts: Option<SdtScope<'a>>,
        indent_unit: String,
    ) -> Self {
        Self {
            indent_level: 0,
            indent_unit,
            vars,
            var_ids: None,
            subroutines: Vec::new(),
            in_subroutine: std::cell::Cell::new(false),
            sdts,
            calls: None,
            refusals: RefCell::new(Vec::new()),
            call_temps: RefCell::new(BTreeMap::new()),
            temps: RefCell::new(TempPool::default()),
            callees: RefCell::new(BTreeSet::new()),
            navigation_levels: None,
            cursor_index: std::cell::Cell::new(0),
        }
    }

    /// Sets navigation levels for database DML generation.
    pub fn with_navigation_levels(
        mut self,
        levels: &'a [crate::navigation::NavigationLevel],
    ) -> Self {
        self.navigation_levels = Some(levels);
        self
    }

    /// Sets navigation levels on a mutable emitter reference.
    pub fn set_navigation_levels(&mut self, levels: &'a [crate::navigation::NavigationLevel]) {
        self.navigation_levels = Some(levels);
    }

    /// Sets the stored id of each variable, so that variables are written with the SDK's field names.
    pub fn with_var_ids(mut self, ids: &'a BTreeMap<String, u32>) -> Self {
        self.var_ids = Some(ids);
        self
    }

    /// Sets the Procedure's subroutines, in source order.
    pub fn with_subroutines(mut self, names: Vec<String>) -> Self {
        self.subroutines = names;
        self
    }

    /// The SDK's method of a subroutine: `S111` for the first (listprograms.cs), `S1<n>1` after it.
    pub fn subroutine_method(&self, name: &str) -> String {
        let index = self
            .subroutines
            .iter()
            .position(|sub| sub.eq_ignore_ascii_case(name))
            .unwrap_or(self.subroutines.len());
        format!("S1{}1", index + 1)
    }

    /// The SDK's counter of the `n`th For-In loop (`AV9GXV1`), when its id is known.
    fn for_in_counter(&self, n: usize) -> Option<String> {
        let name = format!("GXV{n}");
        self.var_ids
            .and_then(|ids| ids.get(&name))
            .map(|id| format!("AV{id}{name}"))
    }

    /// The C# field of a variable: `AV<Id><Name>` when its stored id is known, else `AV_<Name>`.
    pub fn var_cs(&self, name: &str) -> String {
        sdk_var_name(self.var_ids, name)
    }

    /// Sets the Procedures this build emits, so that every call site is written in the SDK's form or
    /// refused by name.
    pub fn with_calls(mut self, calls: &'a CallSignatures) -> Self {
        self.calls = Some(calls);
        self
    }

    /// Everything this emitter refused, by name: unresolved member accesses and call shapes with no SDK
    /// form. Empty when everything was written in a form the SDK output shows.
    pub fn refusals(&self) -> Vec<String> {
        self.refusals.borrow().clone()
    }

    /// The call-site temporaries this class uses, as (C# type, name), in a stable order.
    pub fn temp_declarations(&self) -> Vec<(&'static str, String)> {
        self.temps.borrow().declarations()
    }

    /// The Procedures this class calls, by upper-cased class name.
    pub fn callees(&self) -> BTreeSet<String> {
        self.callees.borrow().clone()
    }

    /// The Procedure a call names, when this build generates it. A call to any other Procedure is refused
    /// by name, not written: its class would not exist, and csc would fail on the caller.
    fn callee<'c>(&self, calls: &'c CallSignatures, name: &str) -> Option<&'c CallTarget> {
        match calls.get(name) {
            Some(target) => {
                self.callees
                    .borrow_mut()
                    .insert(target.class_name.to_ascii_uppercase());
                Some(target)
            }
            None => {
                self.fail(format!(
                    "needs Procedure '{name}': this build does not generate it, so a call to it cannot be written"
                ));
                None
            }
        }
    }

    /// Refuses a database statement by name. The SDK writes a cursor class and commands for it; this build
    /// writes neither, so a class that kept only the statement's comment or body would run without ever
    /// reading or writing a row.
    pub(super) fn refuse_database_statement(&self, construct: &str) {
        self.fail(format!(
            "{construct}: a database statement has no SDK form in this build, so the Procedure cannot be written"
        ));
    }

    fn fail(&self, message: String) {
        let mut refusals = self.refusals.borrow_mut();
        if !refusals.contains(&message) {
            refusals.push(message);
        }
    }

    /// The declared type of a variable of this emitter, matched case-insensitively.
    fn declared(&self, name: &str) -> Option<&'a DataType> {
        let vars = self.vars?;
        let lookup = name.trim_start_matches('&');
        vars.iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(lookup) || k.eq_ignore_ascii_case(name))
            .map(|(_, dt)| dt)
    }

    /// What an SDT-valued expression is: a declared SDT variable, a member of one, or the item of a
    /// collection member. Anything else is an error naming what it is not.
    fn resolve(&self, expr: &Expr) -> Result<SdtMemberType<'a>, String> {
        let scope = self
            .sdts
            .as_ref()
            .ok_or_else(|| "no SDT scope is set".to_string())?;
        match expr {
            Expr::Variable(name) => match self.declared(name) {
                Some(dt) if matches!(dt, DataType::Record { .. }) => {
                    sdt_type(scope.index, &to_csharp_type(dt))
                }
                Some(DataType::BaseCollection { schema, .. }) => {
                    let item_type = to_csharp_type(&DataType::Record {
                        schema: schema.clone(),
                    });
                    let class = scope.index.class(&item_type).ok_or_else(|| {
                        format!("collection item SDT '{item_type}' is not emitted by this build")
                    })?;
                    Ok(SdtMemberType::Collection {
                        item: class.class_name(),
                        children: &class.nodes,
                    })
                }
                Some(_) => Err(format!("'{name}' is not an SDT variable")),
                None => Err(format!("'{name}' is not a declared variable")),
            },
            Expr::Member(target, field) => {
                let parent = self.resolve(target)?;
                sdt_member(&parent, field).map(|(_, member)| member)
            }
            Expr::CollectionItem(col, _) => {
                let parent = self.resolve(col)?;
                sdt_item(&parent)
            }
            _ => Err("the receiver is not an SDT variable or a member of one".to_string()),
        }
    }

    /// Records a refusal when a variable of a type with no expression form is read or written.
    fn refuse_if_no_expression_form(&self, name: &str, data_type: &DataType) {
        if has_no_expression_form(data_type) {
            self.fail(format!(
                "'{name}' is a {}: no expression form for this type is evidenced in this build",
                kind_name(data_type)
            ));
        }
    }

    /// `receiver.member` for a member access. With an SDT scope the member is the SDK's `gxTpr_` form
    /// of the stored member; an access that does not resolve is recorded and written as it was.
    fn member_target(&self, target: &Expr, field: &str) -> String {
        let receiver = self.emit_expr(target);
        if self.sdts.is_none() {
            return format!("{receiver}.{field}");
        }
        match self
            .resolve(target)
            .and_then(|parent| sdt_member(&parent, field))
        {
            Ok((member, SdtMemberType::Scalar(data_type))) => {
                self.refuse_if_no_expression_form(field, &data_type);
                format!("{receiver}.{member}")
            }
            Ok((member, _)) => format!("{receiver}.{member}"),
            Err(reason) => {
                self.fail(format!("member '{field}': {reason}"));
                format!("{receiver}.{field}")
            }
        }
    }

    /// The declared type of a variable, or of an SDT scalar member, when it resolves to one.
    fn value_type(&self, operand: &Expr) -> Option<DataType> {
        match operand {
            Expr::Paren(inner) => self.value_type(inner),
            Expr::Variable(name) => self.declared(name).cloned(),
            Expr::Member(..) => match self.resolve(operand) {
                Ok(SdtMemberType::Scalar(data_type)) => Some(data_type),
                _ => None,
            },
            // `Length()` is written as `StringUtil.Len`, and the SDK writes its ToString as
            // `StringUtil.Str( (decimal)(StringUtil.Len( AV8C4)), 10, 0)` (arusttextfuncsemantics.cs).
            Expr::TextLength(_) => Some(DataType::Numeric {
                precision: 10,
                scale: 0,
            }),
            // `Year()` as the SDK writes its ToString: `StringUtil.Trim( StringUtil.Str( (decimal)(DateTimeUtil.Year(
            // AV14D)), 10, 0))` (arustparmmainprobe.cs line 83).
            Expr::DateMethod(name, _) if name == "YEAR" => Some(DataType::Numeric {
                precision: 10,
                scale: 0,
            }),
            _ => None,
        }
    }

    /// Whether an expression is text by its shape: a text literal, a text-typed value, or a `+` with a text side.
    fn is_text_expr(&self, expr: &Expr) -> bool {
        match expr {
            Expr::Paren(inner) => self.is_text_expr(inner),
            Expr::Text(_) => true,
            Expr::Binary(left, BinOp::Add, right) => {
                self.is_text_expr(left) || self.is_text_expr(right)
            }
            Expr::Variable(_) | Expr::Member(..) => matches!(
                self.value_type(expr),
                Some(DataType::Character { .. } | DataType::Varchar { .. })
            ),
            _ => false,
        }
    }

    /// Whether an expression is numeric by its shape. A numeric value cannot be a `StatusMessage` argument.
    fn is_numeric_value(&self, expr: &Expr) -> bool {
        match expr {
            Expr::Paren(inner) => self.is_numeric_value(inner),
            Expr::Integer(_) | Expr::Decimal(_) | Expr::TextLength(_) => true,
            Expr::DateMethod(name, _) => matches!(
                name.to_ascii_uppercase().as_str(),
                "YEAR" | "MONTH" | "DAY" | "HOUR" | "MINUTE" | "SEC" | "SECOND" | "DOW"
            ),
            Expr::Variable(_) | Expr::Member(..) => {
                matches!(self.value_type(expr), Some(DataType::Numeric { .. }))
            }
            Expr::Negate(inner) => self.is_numeric_value(inner),
            Expr::Binary(left, op, right) => match op {
                BinOp::Subtract | BinOp::Multiply | BinOp::Divide => true,
                BinOp::Add => self.is_numeric_value(left) && self.is_numeric_value(right),
                _ => false,
            },
            _ => false,
        }
    }

    /// The kind and family of a value whose type this build can prove: a declared variable, an SDT member
    /// or a literal. Any other expression has no proven type and is not checked here.
    fn source_kind(&self, expr: &Expr) -> Option<(String, ValueFamily)> {
        match expr {
            Expr::Paren(inner) => self.source_kind(inner),
            Expr::Integer(_) | Expr::Decimal(_) => {
                Some(("numeric literal".to_string(), ValueFamily::Numeric))
            }
            Expr::Text(_) => Some(("text literal".to_string(), ValueFamily::Text)),
            Expr::Boolean(_) => Some(("boolean literal".to_string(), ValueFamily::Boolean)),
            Expr::Variable(_) | Expr::Member(..) => {
                let data_type = self.value_type(expr)?;
                let family = family_of(&data_type)?;
                Some((kind_name(&data_type).to_string(), family))
            }
            _ => None,
        }
    }

    /// `ToString()` of a numeric operand is written as `Str` with the operand's own precision and
    /// decimals. The SDK output for N(5,0) is `StringUtil.Str( (decimal)(AV8I), 5, 0)` and for N(5,2)
    /// `StringUtil.Str( AV8Hits, 5, 2)`; this writes the same call with the cast the `Str` form uses.
    /// Any other operand has no evidenced text form, so it is refused by name.
    fn to_string_of(&self, operand: &Expr) -> String {
        let text = self.emit_expr(operand);
        // `IsEmpty()` is a boolean; its text is `StringUtil.BoolToStr( (DateTime.MinValue==AV10D))` (rustouttypesprobe.cs
        // line 87).
        if matches!(operand, Expr::DateMethod(name, _) if name.eq_ignore_ascii_case("ISEMPTY")) {
            return format!("StringUtil.BoolToStr({text})");
        }
        match self.value_type(operand) {
            // Date and DateTime use the SDK's localUtil formatters with the arguments the SDK writes for this
            // KB: `DToC( AV12D, 1, "/")` (arustdisplayprobe.cs line 101) and `TToC( AV13DT, 8, 5, 1, 2, "/", ":", " ")`
            // (line 119). These are the KB's own date and time format settings as the SDK received them. They
            // are not read from the KB yet, so they hold for the KBs whose SDK output was checked (KBTeste).
            Some(DataType::Date {}) => format!("context.localUtil.DToC({text}, 1, \"/\")"),
            Some(DataType::DateTime {}) => {
                format!("context.localUtil.TToC({text}, 8, 5, 1, 2, \"/\", \":\", \" \")")
            }
            Some(DataType::Numeric { precision, scale }) => {
                format!("StringUtil.Str((decimal)({text}), {precision}, {scale})")
            }
            Some(_) => {
                self.fail(format!(
                    "ToString() of '{text}' is mapped for numeric values only"
                ));
                format!("{text}.ToString()")
            }
            None => {
                self.fail(format!(
                    "ToString() of '{text}': its type is not one this build resolves"
                ));
                format!("{text}.ToString()")
            }
        }
    }

    /// The receiver of a collection method (`Add`, `RemoveItem`, `Clear`). With an SDT scope it must
    /// resolve to a collection member.
    fn collection_target(&self, col: &Expr, method: &str) -> String {
        let receiver = self.emit_expr(col);
        if self.sdts.is_some() {
            match self.resolve(col) {
                Ok(SdtMemberType::Collection { .. }) => {}
                Ok(_) => self.fail(format!("{method}(): the receiver is not a collection")),
                Err(reason) => self.fail(format!("{method}(): {reason}")),
            }
        }
        receiver
    }

    /// SDT collections require their declared item class, not their containing SDT.
    fn check_collection_item(&self, collection: &Expr, value: &Expr) {
        let Ok(SdtMemberType::Collection { item, .. }) = self.resolve(collection) else {
            return;
        };
        if let Expr::CloneRecord(inner) = value {
            self.check_collection_item(collection, inner);
            return;
        }
        let actual = match value {
            Expr::Variable(name) => match self.declared(name) {
                Some(data_type @ DataType::Record { .. }) => Some(to_csharp_type(data_type)),
                _ => None,
            },
            _ => match self.resolve(value) {
                Ok(SdtMemberType::Class { class, .. }) => Some(class),
                _ => None,
            },
        };
        match actual {
            Some(class) if class.eq_ignore_ascii_case(&item) => {}
            Some(class) => {
                self.fail(format!("Add(): expected item type '{item}', got '{class}'"));
            }
            None => self.fail(format!(
                "Add(): expected item type '{item}'; the argument does not resolve to that SDT class"
            )),
        }
    }

    /// Current indentation string.
    pub fn indent(&self) -> String {
        self.indent_unit.repeat(self.indent_level)
    }

    /// Executes closure with one deeper indentation level.
    pub fn indented<F, R>(&mut self, f: F) -> R
    where
        F: FnOnce(&mut Self) -> R,
    {
        self.indent_level += 1;
        let res = f(self);
        self.indent_level -= 1;
        res
    }
}

/// The C# field of a variable: `AV<Id><Name>`, the SDK's name (`AV8Hits`, dch37961700.cs), when the
/// stored id of the variable is known, with the name as declared; else `AV_<Name>`.
pub(crate) fn sdk_var_name(ids: Option<&BTreeMap<String, u32>>, name: &str) -> String {
    let bare = name.trim_start_matches('&');
    // The SDK's own variables keep their names (`Gxm2rootcol`, `GXt_objcol_...1`; sidebaritemsdp.cs).
    if ids.is_some() && (bare.starts_with("Gxm") || bare.starts_with("GXt_")) {
        return bare.to_string();
    }
    if let Some((declared, id)) =
        ids.and_then(|ids| ids.iter().find(|(k, _)| k.eq_ignore_ascii_case(bare)))
    {
        // The standard &Page and &Line are the runtime's fields (`Gx_page`, afrc12224800.cs); a variable
        // named `Gx...` keeps its name (`GxObject`, general/security/isauthorized.cs).
        if declared.starts_with("Gx") || declared.starts_with("GXt_") {
            return declared.clone();
        }
        return match declared.to_ascii_lowercase().as_str() {
            "page" if *id <= 7 => "Gx_page".to_string(),
            "line" if *id <= 7 => "Gx_line".to_string(),
            _ => format!("AV{id}{declared}"),
        };
    }
    to_csharp_var_name(name)
}
