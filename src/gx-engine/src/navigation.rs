//! GeneXus Specifier & SQL Navigation Engine.
//!
//! Provides read-only query planning, relational base table inference, extended table
//! join path resolution, order and index selection, and multi-dialect SQL query generation
//! with ZERO external dependencies and ZERO database writes.
//!
//! Adheres strictly to official GeneXus specifier semantics and error diagnostics:
//! - `spc0004`: No relationship between attributes.
//! - `spc0005`: Ambiguous base table for attributes.

use crate::catalog::{Catalog, KbObject, ObjectKind};
use crate::frontend::{BinOp, Expr, ForEachOrder, ForEachStmt, Stmt};
use crate::table::{SqlDialect, Table, TableForeignKey, derive_tables_from_catalog};
use std::collections::{BTreeMap, BTreeSet};

/// Represents an individual filter condition extracted from a navigation block.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationFilter {
    pub attribute: String,
    pub op: String,
    pub expression: String,
}

/// Represents a join relationship from the base table to an extended table.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationJoin {
    pub table: String,
    pub on_condition: String,
}

/// Represents an individual aggregate or horizontal formula in the navigation plan.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationFormula {
    pub formula_type: String, // "Sum", "Count", "Max", "Min", "Find", "Average", "Horizontal"
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub target_attribute: Option<String>,
    pub base_table: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub index: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub conditions: Vec<NavigationFilter>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub correlations: Vec<String>,
    pub sql: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub default_value: Option<String>,
}

/// Represents a single data access navigation level (e.g. `For Each`).
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationLevel {
    pub number: usize,
    pub level_type: String,
    pub base_table: String,
    pub index: String,
    pub order: Vec<String>,
    pub filters: Vec<NavigationFilter>,
    pub joins: Vec<NavigationJoin>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub formulas: Vec<NavigationFormula>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub dml_plans: Vec<crate::dml::NavigationDmlPlan>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub using_data_selector: Option<String>,
    pub sql: String,
    pub has_when_none: bool,
}

/// Complete navigation report for a GeneXus object (Procedure, etc.).
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationReport {
    pub target_name: String,
    pub levels: Vec<NavigationLevel>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub standalone_formulas: Vec<NavigationFormula>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub standalone_dml: Vec<crate::dml::NavigationDmlPlan>,
    pub warnings: Vec<String>,
}

/// Extracts all referenced attribute names (in ASCII uppercase) from a `ForEachStmt`.
pub fn extract_for_each_attributes(for_each: &ForEachStmt) -> BTreeSet<String> {
    let mut attrs = BTreeSet::new();
    for explicit in &for_each.explicit_attributes {
        attrs.insert(explicit.to_ascii_uppercase());
    }
    if let Some(ref using) = for_each.using {
        for arg in &using.arguments {
            collect_expr_attributes(arg, &mut attrs);
        }
    }
    for expr in &for_each.wheres {
        collect_expr_attributes(expr, &mut attrs);
    }
    for order in &for_each.orders {
        attrs.insert(order.attribute.to_ascii_uppercase());
    }
    if let Some(ref defined) = for_each.defined_by {
        attrs.insert(defined.to_ascii_uppercase());
    }
    collect_stmt_attributes(&for_each.body, &mut attrs);
    attrs
}

fn collect_stmt_attributes(stmts: &[Stmt], attrs: &mut BTreeSet<String>) {
    for stmt in stmts {
        match stmt {
            Stmt::AssignAttribute(name, expr) => {
                attrs.insert(name.to_ascii_uppercase());
                collect_expr_attributes(expr, attrs);
            }
            Stmt::Assign(_, expr)
            | Stmt::Print(expr)
            | Stmt::MsgStatus(expr)
            | Stmt::Return(expr)
            | Stmt::Call(expr) => {
                collect_expr_attributes(expr, attrs);
            }
            Stmt::If(cond, yes, no) => {
                collect_expr_attributes(cond, attrs);
                collect_stmt_attributes(yes, attrs);
                collect_stmt_attributes(no, attrs);
            }
            Stmt::While(cond, body) => {
                collect_expr_attributes(cond, attrs);
                collect_stmt_attributes(body, attrs);
            }
            Stmt::For(_, init, limit, _, body) => {
                collect_expr_attributes(init, attrs);
                collect_expr_attributes(limit, attrs);
                collect_stmt_attributes(body, attrs);
            }
            Stmt::ForIn(_, col, body) => {
                collect_expr_attributes(col, attrs);
                collect_stmt_attributes(body, attrs);
            }
            Stmt::Sub(_, body) => {
                collect_stmt_attributes(body, attrs);
            }
            Stmt::ForEach(nested) => {
                // Collect explicit attributes from nested loops
                for explicit in &nested.explicit_attributes {
                    attrs.insert(explicit.to_ascii_uppercase());
                }
            }
            Stmt::New(new_stmt) => {
                collect_stmt_attributes(&new_stmt.body, attrs);
                collect_stmt_attributes(&new_stmt.when_duplicate, attrs);
            }
            _ => {}
        }
    }
}

/// Returns true if the function name matches a GeneXus aggregate formula keyword.
pub fn is_aggregate_call(name: &str) -> bool {
    matches!(
        name.to_ascii_uppercase().as_str(),
        "SUM" | "COUNT" | "MAX" | "MIN" | "FIND" | "AVERAGE" | "AVG"
    )
}

/// Collects all attribute references from an expression unconditionally.
pub fn collect_all_expr_attributes(expr: &Expr, attrs: &mut BTreeSet<String>) {
    match expr {
        Expr::Attribute(name) => {
            attrs.insert(name.to_ascii_uppercase());
        }
        Expr::Binary(left, _, right) => {
            collect_all_expr_attributes(left, attrs);
            collect_all_expr_attributes(right, attrs);
        }
        Expr::Not(inner) | Expr::Negate(inner) | Expr::TextLength(inner) => {
            collect_all_expr_attributes(inner, attrs);
        }
        Expr::Member(inner, _) | Expr::CloneRecord(inner) => {
            collect_all_expr_attributes(inner, attrs);
        }
        Expr::CollectionItem(receiver, idx) => {
            collect_all_expr_attributes(receiver, attrs);
            collect_all_expr_attributes(idx, attrs);
        }
        Expr::DateMethod(_, inner) => {
            collect_all_expr_attributes(inner, attrs);
        }
        Expr::DateShift(_, a, b) => {
            collect_all_expr_attributes(a, attrs);
            collect_all_expr_attributes(b, attrs);
        }
        Expr::Call(_, args) => {
            for arg in args {
                collect_all_expr_attributes(arg, attrs);
            }
        }
        _ => {}
    }
}

fn collect_expr_attributes(expr: &Expr, attrs: &mut BTreeSet<String>) {
    match expr {
        Expr::Attribute(name) => {
            attrs.insert(name.to_ascii_uppercase());
        }
        Expr::Binary(left, _, right) => {
            collect_expr_attributes(left, attrs);
            collect_expr_attributes(right, attrs);
        }
        Expr::Not(inner) | Expr::Negate(inner) | Expr::TextLength(inner) => {
            collect_expr_attributes(inner, attrs);
        }
        Expr::Member(inner, _) | Expr::CloneRecord(inner) => {
            collect_expr_attributes(inner, attrs);
        }
        Expr::CollectionItem(receiver, idx) => {
            collect_expr_attributes(receiver, attrs);
            collect_expr_attributes(idx, attrs);
        }
        Expr::DateMethod(_, inner) => {
            collect_expr_attributes(inner, attrs);
        }
        Expr::DateShift(_, a, b) => {
            collect_expr_attributes(a, attrs);
            collect_expr_attributes(b, attrs);
        }
        // Aggregate formula attributes belong to the formula's own query plan;
        // do not mix their attributes into the enclosing query's base table!
        Expr::Call(name, args) if !is_aggregate_call(name) => {
            for arg in args {
                collect_expr_attributes(arg, attrs);
            }
        }
        _ => {}
    }
}

/// Represents a table reachable in the extended table (Tabela Estendida).
#[derive(Debug, Clone)]
pub struct ExtendedTableNode {
    pub table_name: String,
    pub parent_table: String,
    pub foreign_key: TableForeignKey,
    pub attributes: BTreeSet<String>,
}

/// Represents the full extended table view of a given physical table.
#[derive(Debug, Clone)]
pub struct ExtendedTable {
    pub base_table: String,
    pub direct_attributes: BTreeSet<String>,
    pub reachable_tables: BTreeMap<String, ExtendedTableNode>,
}

impl ExtendedTable {
    /// Returns true if this table or any table in its extended table contains the attribute.
    pub fn contains_attribute(&self, attribute: &str) -> bool {
        let upper = attribute.to_ascii_uppercase();
        if self.direct_attributes.contains(&upper) {
            return true;
        }
        self.reachable_tables
            .values()
            .any(|node| node.attributes.contains(&upper))
    }

    /// Returns all attributes available through this extended table.
    pub fn all_attributes(&self) -> BTreeSet<String> {
        let mut set = self.direct_attributes.clone();
        for node in self.reachable_tables.values() {
            set.extend(node.attributes.clone());
        }
        set
    }
}

/// Computes the extended table for each table in the physical schema.
pub fn compute_extended_tables(tables: &[Table]) -> BTreeMap<String, ExtendedTable> {
    let table_map: BTreeMap<String, &Table> = tables
        .iter()
        .map(|t| (t.name.to_ascii_uppercase(), t))
        .collect();

    let mut result = BTreeMap::new();

    for table in tables {
        let upper_name = table.name.to_ascii_uppercase();
        let direct_attributes: BTreeSet<String> = table
            .columns
            .iter()
            .map(|c| c.name.to_ascii_uppercase())
            .collect();

        let mut reachable = BTreeMap::new();
        let mut queue = vec![(table.name.clone(), table)];
        let mut visited = BTreeSet::new();
        visited.insert(upper_name.clone());

        let mut depth = 0;
        while !queue.is_empty() && depth < 16 {
            depth += 1;
            let current_level = queue;
            queue = Vec::new();

            for (from_table_name, current_t) in current_level {
                for fk in &current_t.foreign_keys {
                    let ref_upper = fk.referenced_table.to_ascii_uppercase();
                    if visited.contains(&ref_upper) {
                        continue;
                    }
                    if let Some(&ref_table) = table_map.get(&ref_upper) {
                        visited.insert(ref_upper.clone());
                        let ref_attrs: BTreeSet<String> = ref_table
                            .columns
                            .iter()
                            .map(|c| c.name.to_ascii_uppercase())
                            .collect();

                        reachable.insert(
                            ref_upper.clone(),
                            ExtendedTableNode {
                                table_name: ref_table.name.clone(),
                                parent_table: from_table_name.clone(),
                                foreign_key: fk.clone(),
                                attributes: ref_attrs,
                            },
                        );

                        queue.push((ref_table.name.clone(), ref_table));
                    }
                }
            }
        }

        result.insert(
            upper_name,
            ExtendedTable {
                base_table: table.name.clone(),
                direct_attributes,
                reachable_tables: reachable,
            },
        );
    }

    result
}

/// Infers the unique Base Table from a set of referenced attributes according to GeneXus specifier rules (Matrix D1).
pub fn infer_base_table_from_attributes<'a>(
    referenced_attrs: &BTreeSet<String>,
    defined_by: Option<&str>,
    tables: &'a [Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
) -> Result<&'a Table, String> {
    if referenced_attrs.is_empty() && tables.len() == 1 {
        return Ok(&tables[0]);
    }

    // Filter candidate tables whose extended table contains all referenced attributes
    let mut candidates: Vec<&'a Table> = Vec::new();

    for table in tables {
        let upper_name = table.name.to_ascii_uppercase();
        if let Some(ext) = extended_tables.get(&upper_name) {
            // Check defined_by constraint
            if let Some(def_by) = defined_by
                && !ext.direct_attributes.contains(&def_by.to_ascii_uppercase())
            {
                continue;
            }

            // Check if all referenced attributes are reachable
            let all_reachable = referenced_attrs
                .iter()
                .all(|attr| ext.contains_attribute(attr));
            if all_reachable {
                candidates.push(table);
            }
        }
    }

    if candidates.is_empty() {
        let attr_list = referenced_attrs
            .iter()
            .cloned()
            .collect::<Vec<_>>()
            .join(", ");
        return Err(format!(
            "spc0004: No relationship between attributes: [{attr_list}]"
        ));
    }

    // If any candidate contains ALL referenced attributes directly in direct_attributes:
    let all_direct_matching: Vec<&'a Table> = candidates
        .iter()
        .copied()
        .filter(|t| {
            let upper = t.name.to_ascii_uppercase();
            if let Some(ext) = extended_tables.get(&upper) {
                referenced_attrs
                    .iter()
                    .all(|attr| ext.direct_attributes.contains(attr))
            } else {
                false
            }
        })
        .collect();

    let candidates = if !all_direct_matching.is_empty() {
        all_direct_matching
    } else {
        // Otherwise, filter to candidates that have at least some direct matches
        let direct_matching_candidates: Vec<&'a Table> = candidates
            .iter()
            .copied()
            .filter(|t| {
                let upper = t.name.to_ascii_uppercase();
                if let Some(ext) = extended_tables.get(&upper) {
                    if let Some(def_by) = defined_by
                        && ext.direct_attributes.contains(&def_by.to_ascii_uppercase())
                    {
                        return true;
                    }
                    ext.direct_attributes
                        .iter()
                        .any(|attr| referenced_attrs.contains(attr))
                } else {
                    false
                }
            })
            .collect();

        if !direct_matching_candidates.is_empty() {
            direct_matching_candidates
        } else {
            candidates
        }
    };

    if candidates.len() == 1 {
        return Ok(candidates[0]);
    }

    // Disambiguate when multiple candidates exist:
    // In GeneXus, prefer the least subordinate (ancestor) candidate among those that satisfy all attributes.
    // A candidate table that reaches another candidate table in its extended table is subordinate to it
    // and would unnecessarily multiply rows.
    let mut non_subordinate_candidates = Vec::new();

    for candidate in &candidates {
        let cand_upper = candidate.name.to_ascii_uppercase();
        let mut is_subordinate_to_another = false;

        for other in &candidates {
            if candidate.name == other.name {
                continue;
            }
            let other_upper = other.name.to_ascii_uppercase();
            if let Some(cand_ext) = extended_tables.get(&cand_upper)
                && cand_ext.reachable_tables.contains_key(&other_upper)
            {
                is_subordinate_to_another = true;
                break;
            }
        }

        if !is_subordinate_to_another {
            non_subordinate_candidates.push(*candidate);
        }
    }

    if non_subordinate_candidates.len() == 1 {
        return Ok(non_subordinate_candidates[0]);
    }

    // If still ambiguous without subordination relationship:
    let candidate_names = non_subordinate_candidates
        .iter()
        .map(|t| t.name.as_str())
        .collect::<Vec<_>>()
        .join(", ");
    let attr_list = referenced_attrs
        .iter()
        .cloned()
        .collect::<Vec<_>>()
        .join(", ");
    Err(format!(
        "spc0005: Ambiguous base table for attributes [{attr_list}]; candidate tables: [{candidate_names}]. Disambiguate with 'Defined By' or explicit For Each attribute."
    ))
}

/// Infers the unique Base Table for a `ForEachStmt` according to GeneXus specifier rules (Matrix D1).
pub fn infer_base_table<'a>(
    for_each: &ForEachStmt,
    tables: &'a [Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
) -> Result<&'a Table, String> {
    let referenced_attrs = extract_for_each_attributes(for_each);
    infer_base_table_from_attributes(
        &referenced_attrs,
        for_each.defined_by.as_deref(),
        tables,
        extended_tables,
    )
}

/// Selects the best index for the base table matching the `Order` clause (Matrix D3).
pub fn select_index(base_table: &Table, orders: &[ForEachOrder]) -> (String, Vec<String>) {
    if orders.is_empty() {
        // Default order: Primary Key
        let order_cols = base_table.primary_key.clone();
        let index_name = format!("PK_{}", base_table.name);
        return (index_name, order_cols);
    }

    let order_cols: Vec<String> = orders.iter().map(|o| o.attribute.clone()).collect();
    let order_upper: Vec<String> = order_cols.iter().map(|c| c.to_ascii_uppercase()).collect();

    // Check primary key match
    let pk_upper: Vec<String> = base_table
        .primary_key
        .iter()
        .map(|c| c.to_ascii_uppercase())
        .collect();
    if pk_upper == order_upper {
        return (format!("PK_{}", base_table.name), order_cols);
    }

    // Check secondary indexes
    for idx in &base_table.indexes {
        let idx_upper: Vec<String> = idx.columns.iter().map(|c| c.to_ascii_uppercase()).collect();
        if idx_upper == order_upper {
            return (idx.name.clone(), order_cols);
        }
    }

    ("NONE".to_string(), order_cols)
}

/// Selects the best index for the base table matching the given columns.
pub fn select_index_for_columns(base_table: &Table, columns: &[String]) -> Option<String> {
    if columns.is_empty() {
        return None;
    }
    let cols_upper: Vec<String> = columns.iter().map(|c| c.to_ascii_uppercase()).collect();

    let pk_upper: Vec<String> = base_table
        .primary_key
        .iter()
        .map(|c| c.to_ascii_uppercase())
        .collect();
    if pk_upper.starts_with(&cols_upper) || cols_upper.starts_with(&pk_upper) {
        return Some(format!("PK_{}", base_table.name));
    }

    for idx in &base_table.indexes {
        let idx_upper: Vec<String> = idx.columns.iter().map(|c| c.to_ascii_uppercase()).collect();
        if idx_upper.starts_with(&cols_upper) || cols_upper.starts_with(&idx_upper) {
            return Some(idx.name.clone());
        }
    }

    None
}

/// Resolves the minimum set of joins needed to satisfy attributes outside the base table (Matrix D5).
pub fn resolve_joins(
    _base_table: &Table,
    referenced_attrs: &BTreeSet<String>,
    ext_table: &ExtendedTable,
    dialect: SqlDialect,
) -> Vec<NavigationJoin> {
    let direct_attrs = &ext_table.direct_attributes;
    let mut needed_tables = BTreeSet::new();

    for attr in referenced_attrs {
        if !direct_attrs.contains(attr) {
            // Find which reachable table provides this attribute
            for (table_name, node) in &ext_table.reachable_tables {
                if node.attributes.contains(attr) {
                    needed_tables.insert(table_name.clone());
                    break;
                }
            }
        }
    }

    let mut joins = Vec::new();
    for table_name in needed_tables {
        if let Some(node) = ext_table.reachable_tables.get(&table_name) {
            let left_table = dialect.quote_identifier(&node.parent_table);
            let right_table = dialect.quote_identifier(&node.table_name);

            let mut conditions = Vec::new();
            for (fk_col, pk_col) in node
                .foreign_key
                .columns
                .iter()
                .zip(&node.foreign_key.referenced_columns)
            {
                let left_col = dialect.quote_identifier(fk_col);
                let right_col = dialect.quote_identifier(pk_col);
                conditions.push(format!(
                    "{left_table}.{left_col} = {right_table}.{right_col}"
                ));
            }

            joins.push(NavigationJoin {
                table: node.table_name.clone(),
                on_condition: conditions.join(" AND "),
            });
        }
    }

    joins
}

/// Formats a filter expression from an AST `Expr`.
pub fn format_filter_expression(expr: &Expr, dialect: SqlDialect) -> Option<NavigationFilter> {
    match expr {
        Expr::Binary(left, op, right) => {
            let op_str = match op {
                BinOp::Equal => "=",
                BinOp::NotEqual => "<>",
                BinOp::Less => "<",
                BinOp::LessEqual => "<=",
                BinOp::Greater => ">",
                BinOp::GreaterEqual => ">=",
                _ => return None,
            };

            let (att_name, val_expr) = match (left.as_ref(), right.as_ref()) {
                (Expr::Attribute(att), other) => (att.clone(), format_expr_value(other, dialect)),
                (other, Expr::Attribute(att)) => {
                    let inverted_op = match op {
                        BinOp::Equal => "=",
                        BinOp::NotEqual => "<>",
                        BinOp::Less => ">",
                        BinOp::LessEqual => ">=",
                        BinOp::Greater => "<",
                        BinOp::GreaterEqual => "<=",
                        _ => return None,
                    };
                    return Some(NavigationFilter {
                        attribute: att.clone(),
                        op: inverted_op.to_string(),
                        expression: format_expr_value(other, dialect),
                    });
                }
                _ => return None,
            };

            Some(NavigationFilter {
                attribute: att_name,
                op: op_str.to_string(),
                expression: val_expr,
            })
        }
        _ => None,
    }
}

fn format_expr_value(expr: &Expr, dialect: SqlDialect) -> String {
    match expr {
        Expr::Integer(val) => val.to_string(),
        Expr::Decimal(val) => val.clone(),
        Expr::Text(val) => format!("'{val}'"),
        Expr::Boolean(val) => match dialect {
            SqlDialect::SqlServer | SqlDialect::Oracle => {
                if *val {
                    "1".into()
                } else {
                    "0".into()
                }
            }
            SqlDialect::PostgreSql | SqlDialect::Ansi => {
                if *val {
                    "TRUE".into()
                } else {
                    "FALSE".into()
                }
            }
        },
        Expr::Variable(var) => format!("@{var}"),
        Expr::Attribute(att) => dialect.quote_identifier(att),
        _ => "/* expr */".to_string(),
    }
}

/// Recursively collects filters from an AST `Expr`, handling multiple AND conditions.
pub fn collect_filter_expressions(
    expr: &Expr,
    dialect: SqlDialect,
    filters: &mut Vec<NavigationFilter>,
) {
    if let Expr::Binary(left, BinOp::And, right) = expr {
        collect_filter_expressions(left, dialect, filters);
        collect_filter_expressions(right, dialect, filters);
    } else if let Some(f) = format_filter_expression(expr, dialect) {
        filters.push(f);
    }
}

/// Helper to resolve column and table names with dialect quoting across base and joined tables.
pub fn resolve_column_sql(
    attr: &str,
    base_table: &Table,
    joins: &[NavigationJoin],
    tables: &[Table],
    dialect: SqlDialect,
) -> (String, String) {
    if let Some(c) = base_table
        .columns
        .iter()
        .find(|c| c.name.eq_ignore_ascii_case(attr))
    {
        return (
            dialect.quote_identifier(&base_table.name),
            dialect.quote_identifier(&c.name),
        );
    }
    for join in joins {
        if let Some(t) = tables
            .iter()
            .find(|t| t.name.eq_ignore_ascii_case(&join.table))
            && let Some(c) = t.columns.iter().find(|c| c.name.eq_ignore_ascii_case(attr))
        {
            return (
                dialect.quote_identifier(&t.name),
                dialect.quote_identifier(&c.name),
            );
        }
    }
    (
        dialect.quote_identifier(&base_table.name),
        dialect.quote_identifier(attr),
    )
}

/// Specifies an individual aggregate formula, inferring its base table, correlation, index, and multi-dialect SQL.
pub fn specify_aggregate_formula(
    call_name: &str,
    args: &[Expr],
    context_table: Option<&str>,
    tables: &[Table],
    dialect: SqlDialect,
) -> Result<NavigationFormula, String> {
    let formula_type = match call_name.to_ascii_uppercase().as_str() {
        "SUM" => "Sum",
        "COUNT" => "Count",
        "MAX" => "Max",
        "MIN" => "Min",
        "FIND" => "Find",
        "AVERAGE" | "AVG" => "Average",
        other => return Err(format!("unrecognized aggregate formula kind: {other}")),
    };

    // 1. Target attribute or expression
    let (target_attribute, target_expression): (Option<String>, Option<&Expr>) = if formula_type
        == "Count"
    {
        if args.is_empty() {
            (None, None)
        } else {
            match &args[0] {
                Expr::Attribute(att) => {
                    if att == "*" {
                        (None, None)
                    } else {
                        (Some(att.to_ascii_uppercase()), None)
                    }
                }
                _ => {
                    return Err(
                        "Count formula expects an attribute name as first argument or no arguments"
                            .into(),
                    );
                }
            }
        }
    } else {
        if args.is_empty() {
            return Err(format!(
                "formula {formula_type} requires at least 1 target attribute argument"
            ));
        }
        match &args[0] {
            Expr::Attribute(att) => (Some(att.to_ascii_uppercase()), None),
            other_expr if matches!(formula_type, "Sum" | "Average") => {
                let mut expr_attrs = BTreeSet::new();
                collect_all_expr_attributes(other_expr, &mut expr_attrs);
                if expr_attrs.is_empty() {
                    return Err(format!(
                        "formula {formula_type} requires an attribute or expression containing attributes"
                    ));
                }
                (None, Some(other_expr))
            }
            _ => {
                return Err(format!(
                    "formula {formula_type} requires an attribute as its first argument"
                ));
            }
        }
    };

    // 2. Conditions
    let mut conditions = Vec::new();
    let mut cond_attrs = BTreeSet::new();
    if args.len() >= 2 {
        collect_filter_expressions(&args[1], dialect, &mut conditions);
        collect_all_expr_attributes(&args[1], &mut cond_attrs);
    }

    // 3. Default value
    let default_value = if args.len() >= 3 {
        Some(format_expr_value(&args[2], dialect))
    } else {
        None
    };

    // 4. Base table inference for formula
    let mut referenced_attributes = BTreeSet::new();
    if let Some(ref att) = target_attribute {
        referenced_attributes.insert(att.clone());
    }
    if let Some(expr) = target_expression {
        collect_all_expr_attributes(expr, &mut referenced_attributes);
    }
    for att in &cond_attrs {
        referenced_attributes.insert(att.clone());
    }

    if referenced_attributes.is_empty() {
        if let Some(ctx) = context_table {
            let base_table = tables
                .iter()
                .find(|t| t.name.eq_ignore_ascii_case(ctx))
                .ok_or_else(|| format!("context table {ctx} not found"))?;
            let sql = match dialect {
                SqlDialect::SqlServer => format!("SELECT COUNT(*) FROM [{}]", base_table.name),
                SqlDialect::Oracle => format!("SELECT COUNT(*) FROM {}", base_table.name),
                _ => format!("SELECT COUNT(*) FROM \"{}\"", base_table.name),
            };
            return Ok(NavigationFormula {
                formula_type: formula_type.into(),
                target_attribute: None,
                base_table: base_table.name.clone(),
                index: None,
                conditions,
                correlations: Vec::new(),
                sql,
                default_value,
            });
        } else {
            return Err(
                "spc0005: Ambiguous base table for Count(): target attribute is required to identify the table to count".into(),
            );
        }
    }

    let extended_tables = compute_extended_tables(tables);
    let base_table =
        infer_base_table_from_attributes(&referenced_attributes, None, tables, &extended_tables)?;

    let joins = if let Some(ext) = extended_tables.get(&base_table.name.to_ascii_uppercase()) {
        resolve_joins(base_table, &referenced_attributes, ext, dialect)
    } else {
        Vec::new()
    };

    // 5. Automatic correlation with context table
    let mut correlations = Vec::new();
    let mut correlation_cols = Vec::new();
    if let Some(ctx) = context_table
        && !ctx.eq_ignore_ascii_case(&base_table.name)
    {
        // Check direct FK from base_table to context
        for fk in &base_table.foreign_keys {
            if fk.referenced_table.eq_ignore_ascii_case(ctx) {
                for (fk_col, pk_col) in fk.columns.iter().zip(&fk.referenced_columns) {
                    correlations.push(format!(
                        "{}.{} = {}.{}",
                        dialect.quote_identifier(&base_table.name),
                        dialect.quote_identifier(fk_col),
                        dialect.quote_identifier(ctx),
                        dialect.quote_identifier(pk_col)
                    ));
                    correlation_cols.push(fk_col.clone());
                }
            }
        }
        // Check if context has FK to base_table (e.g. 1-to-1 or lookup)
        if correlations.is_empty()
            && let Some(ctx_tbl) = tables.iter().find(|t| t.name.eq_ignore_ascii_case(ctx))
        {
            for fk in &ctx_tbl.foreign_keys {
                if fk.referenced_table.eq_ignore_ascii_case(&base_table.name) {
                    for (fk_col, pk_col) in fk.columns.iter().zip(&fk.referenced_columns) {
                        correlations.push(format!(
                            "{}.{} = {}.{}",
                            dialect.quote_identifier(&base_table.name),
                            dialect.quote_identifier(pk_col),
                            dialect.quote_identifier(ctx),
                            dialect.quote_identifier(fk_col)
                        ));
                        correlation_cols.push(pk_col.clone());
                    }
                }
            }
        }
        // Check if context is in extended table of base_table (depth > 1)
        if correlations.is_empty()
            && let Some(ext) = extended_tables.get(&base_table.name.to_ascii_uppercase())
        {
            for (ext_name, node) in &ext.reachable_tables {
                if ext_name.eq_ignore_ascii_case(ctx) {
                    for (fk_col, pk_col) in node
                        .foreign_key
                        .columns
                        .iter()
                        .zip(&node.foreign_key.referenced_columns)
                    {
                        correlations.push(format!(
                            "{}.{} = {}.{}",
                            dialect.quote_identifier(&base_table.name),
                            dialect.quote_identifier(fk_col),
                            dialect.quote_identifier(ctx),
                            dialect.quote_identifier(pk_col)
                        ));
                        correlation_cols.push(fk_col.clone());
                    }
                }
            }
        }
    }

    // 6. Index selection
    let index_name = if !correlation_cols.is_empty() {
        select_index_for_columns(base_table, &correlation_cols)
    } else {
        None
    };

    // 7. Multi-dialect SQL generation
    let canonical_target = if let Some(ref att) = target_attribute {
        let (tbl_q, col_q) = resolve_column_sql(att, base_table, &joins, tables, dialect);
        Some(format!("{tbl_q}.{col_q}"))
    } else if let Some(expr) = target_expression {
        let formatted = format_horizontal_expr(expr, base_table, &joins, tables, dialect)?;
        Some(formatted)
    } else {
        None
    };

    let select_expr = match formula_type {
        "Sum" => format!("SELECT SUM({})", canonical_target.as_deref().unwrap_or("*")),
        "Count" => format!(
            "SELECT COUNT({})",
            canonical_target.as_deref().unwrap_or("*")
        ),
        "Max" => format!("SELECT MAX({})", canonical_target.as_deref().unwrap_or("*")),
        "Min" => format!("SELECT MIN({})", canonical_target.as_deref().unwrap_or("*")),
        "Average" => format!("SELECT AVG({})", canonical_target.as_deref().unwrap_or("*")),
        "Find" => match dialect {
            SqlDialect::SqlServer => {
                format!(
                    "SELECT TOP 1 {}",
                    canonical_target.as_deref().unwrap_or("*")
                )
            }
            _ => format!("SELECT {}", canonical_target.as_deref().unwrap_or("*")),
        },
        _ => unreachable!(),
    };

    let from_clause = format!("FROM {}", dialect.quote_identifier(&base_table.name));
    let mut joins_clause = String::new();
    for join in &joins {
        joins_clause.push_str(&format!(
            " INNER JOIN {} ON {}",
            dialect.quote_identifier(&join.table),
            join.on_condition
        ));
    }

    let mut all_where = Vec::new();
    for corr in &correlations {
        all_where.push(corr.clone());
    }
    for cond in &conditions {
        let (tbl_q, col_q) =
            resolve_column_sql(&cond.attribute, base_table, &joins, tables, dialect);
        all_where.push(format!("{tbl_q}.{col_q} {} {}", cond.op, cond.expression));
    }

    if formula_type == "Find" && dialect == SqlDialect::Oracle {
        all_where.push("ROWNUM = 1".into());
    }

    let where_clause = if !all_where.is_empty() {
        format!(" WHERE {}", all_where.join(" AND "))
    } else {
        String::new()
    };

    let mut sql = format!("{select_expr} {from_clause}{joins_clause}{where_clause}");
    if formula_type == "Find" && (dialect == SqlDialect::Ansi || dialect == SqlDialect::PostgreSql)
    {
        sql.push_str(" LIMIT 1");
    }

    Ok(NavigationFormula {
        formula_type: formula_type.into(),
        target_attribute,
        base_table: base_table.name.clone(),
        index: index_name,
        conditions,
        correlations,
        sql,
        default_value,
    })
}

/// Specifies a horizontal formula, verifying that all attributes belong to the table's extended table.
pub fn specify_horizontal_formula(
    formula_expr: &Expr,
    base_table_name: &str,
    tables: &[Table],
    dialect: SqlDialect,
) -> Result<NavigationFormula, String> {
    let mut attrs = BTreeSet::new();
    collect_all_expr_attributes(formula_expr, &mut attrs);

    let extended_tables = compute_extended_tables(tables);
    let ext = extended_tables
        .get(&base_table_name.to_ascii_uppercase())
        .ok_or_else(|| format!("base table {base_table_name} not found in catalog tables"))?;

    for att in &attrs {
        if !ext.contains_attribute(att) {
            return Err(format!(
                "spc0004: No relationship between attributes: attribute '{att}' cannot be reached from base table '{base_table_name}'"
            ));
        }
    }

    let base_table = tables
        .iter()
        .find(|t| t.name.eq_ignore_ascii_case(base_table_name))
        .ok_or_else(|| format!("base table {base_table_name} not found"))?;

    let joins = resolve_joins(base_table, &attrs, ext, dialect);
    let expr_sql = format_horizontal_expr(formula_expr, base_table, &joins, tables, dialect)?;

    Ok(NavigationFormula {
        formula_type: "Horizontal".into(),
        target_attribute: None,
        base_table: base_table.name.clone(),
        index: None,
        conditions: Vec::new(),
        correlations: Vec::new(),
        sql: expr_sql,
        default_value: None,
    })
}

fn format_horizontal_expr(
    expr: &Expr,
    base_table: &Table,
    joins: &[NavigationJoin],
    tables: &[Table],
    dialect: SqlDialect,
) -> Result<String, String> {
    match expr {
        Expr::Integer(val) => Ok(val.to_string()),
        Expr::Decimal(val) => Ok(val.clone()),
        Expr::Text(val) => Ok(format!("'{}'", val.replace('\'', "''"))),
        Expr::Boolean(val) => Ok(match dialect {
            SqlDialect::Ansi | SqlDialect::PostgreSql => {
                if *val {
                    "TRUE".into()
                } else {
                    "FALSE".into()
                }
            }
            SqlDialect::SqlServer | SqlDialect::Oracle => {
                if *val {
                    "1".into()
                } else {
                    "0".into()
                }
            }
        }),
        Expr::Variable(var) => Ok(match dialect {
            SqlDialect::SqlServer => format!("@{var}"),
            _ => format!(":{var}"),
        }),
        Expr::Attribute(att) => {
            let (tbl_q, col_q) = resolve_column_sql(att, base_table, joins, tables, dialect);
            Ok(format!("{tbl_q}.{col_q}"))
        }
        Expr::Binary(left, op, right) => {
            let l = format_horizontal_expr(left, base_table, joins, tables, dialect)?;
            let r = format_horizontal_expr(right, base_table, joins, tables, dialect)?;
            let op_str = match op {
                BinOp::Add => "+",
                BinOp::Subtract => "-",
                BinOp::Multiply => "*",
                BinOp::Divide => "/",
                BinOp::Equal => "=",
                BinOp::NotEqual => "<>",
                BinOp::Less => "<",
                BinOp::LessEqual => "<=",
                BinOp::Greater => ">",
                BinOp::GreaterEqual => ">=",
                BinOp::And => "AND",
                BinOp::Or => "OR",
            };
            Ok(format!("({l} {op_str} {r})"))
        }
        Expr::Negate(inner) => {
            let inner_str = format_horizontal_expr(inner, base_table, joins, tables, dialect)?;
            Ok(format!("(-{inner_str})"))
        }
        Expr::Call(name, args) if is_aggregate_call(name) => {
            let formula =
                specify_aggregate_formula(name, args, Some(&base_table.name), tables, dialect)?;
            Ok(format!("({})", formula.sql))
        }
        _ => Err("unsupported expression in horizontal formula".into()),
    }
}

/// Specifies a Transaction attribute formula (from TransactionAttributeRef.formula).
pub fn specify_transaction_attribute_formula(
    attr_name: &str,
    formula_source: &str,
    transaction_tables: &[Table],
    all_tables: &[Table],
    dialect: SqlDialect,
) -> Result<NavigationFormula, String> {
    let parsed_expr = crate::frontend::parse_expression_with_attributes(formula_source)?;

    let owning_table = transaction_tables
        .iter()
        .find(|t| {
            t.columns
                .iter()
                .any(|c| c.name.eq_ignore_ascii_case(attr_name))
        })
        .or_else(|| transaction_tables.first())
        .ok_or_else(|| {
            format!("cannot determine owning table for formula attribute '{attr_name}'")
        })?;

    match &parsed_expr {
        Expr::Call(name, args) if is_aggregate_call(name) => {
            let mut formula = specify_aggregate_formula(
                name,
                args,
                Some(&owning_table.name),
                all_tables,
                dialect,
            )?;
            formula.target_attribute = Some(attr_name.to_string());
            Ok(formula)
        }
        _ => {
            let mut formula =
                specify_horizontal_formula(&parsed_expr, &owning_table.name, all_tables, dialect)?;
            formula.target_attribute = Some(attr_name.to_string());
            Ok(formula)
        }
    }
}

fn extract_formulas_from_stmts(
    stmts: &[Stmt],
    context_table: Option<&str>,
    tables: &[Table],
    dialect: SqlDialect,
    formulas: &mut Vec<NavigationFormula>,
) -> Result<(), String> {
    for stmt in stmts {
        extract_formulas_from_stmt(stmt, context_table, tables, dialect, formulas)?;
    }
    Ok(())
}

fn extract_formulas_from_stmt(
    stmt: &Stmt,
    context_table: Option<&str>,
    tables: &[Table],
    dialect: SqlDialect,
    formulas: &mut Vec<NavigationFormula>,
) -> Result<(), String> {
    match stmt {
        Stmt::Assign(_, expr)
        | Stmt::AssignAttribute(_, expr)
        | Stmt::Print(expr)
        | Stmt::MsgStatus(expr)
        | Stmt::Return(expr)
        | Stmt::Call(expr) => {
            extract_formulas_from_expr(expr, context_table, tables, dialect, formulas)?;
        }
        Stmt::If(cond, yes, no) => {
            extract_formulas_from_expr(cond, context_table, tables, dialect, formulas)?;
            extract_formulas_from_stmts(yes, context_table, tables, dialect, formulas)?;
            extract_formulas_from_stmts(no, context_table, tables, dialect, formulas)?;
        }
        Stmt::While(cond, body) => {
            extract_formulas_from_expr(cond, context_table, tables, dialect, formulas)?;
            extract_formulas_from_stmts(body, context_table, tables, dialect, formulas)?;
        }
        Stmt::For(_, init, limit, _, body) => {
            extract_formulas_from_expr(init, context_table, tables, dialect, formulas)?;
            extract_formulas_from_expr(limit, context_table, tables, dialect, formulas)?;
            extract_formulas_from_stmts(body, context_table, tables, dialect, formulas)?;
        }
        Stmt::ForIn(_, col, body) => {
            extract_formulas_from_expr(col, context_table, tables, dialect, formulas)?;
            extract_formulas_from_stmts(body, context_table, tables, dialect, formulas)?;
        }
        Stmt::ForEach(fe) => {
            extract_formulas_from_stmts(&fe.body, context_table, tables, dialect, formulas)?;
            extract_formulas_from_stmts(&fe.when_none, context_table, tables, dialect, formulas)?;
        }
        Stmt::Sub(_, body) => {
            extract_formulas_from_stmts(body, context_table, tables, dialect, formulas)?;
        }
        Stmt::New(new_stmt) => {
            extract_formulas_from_stmts(&new_stmt.body, context_table, tables, dialect, formulas)?;
            extract_formulas_from_stmts(
                &new_stmt.when_duplicate,
                context_table,
                tables,
                dialect,
                formulas,
            )?;
        }
        _ => {}
    }
    Ok(())
}

fn extract_formulas_from_expr(
    expr: &Expr,
    context_table: Option<&str>,
    tables: &[Table],
    dialect: SqlDialect,
    formulas: &mut Vec<NavigationFormula>,
) -> Result<(), String> {
    match expr {
        Expr::Call(name, args) if is_aggregate_call(name) => {
            let formula = specify_aggregate_formula(name, args, context_table, tables, dialect)?;
            formulas.push(formula);
        }
        Expr::Binary(left, _, right) => {
            extract_formulas_from_expr(left, context_table, tables, dialect, formulas)?;
            extract_formulas_from_expr(right, context_table, tables, dialect, formulas)?;
        }
        Expr::Not(inner) | Expr::Negate(inner) | Expr::TextLength(inner) => {
            extract_formulas_from_expr(inner, context_table, tables, dialect, formulas)?;
        }
        Expr::Call(_, args) => {
            for arg in args {
                extract_formulas_from_expr(arg, context_table, tables, dialect, formulas)?;
            }
        }
        _ => {}
    }
    Ok(())
}

/// Generates clean, read-only SQL for the navigation level (Matrix D2 & H1).
pub fn generate_navigation_sql(
    base_table: &Table,
    joins: &[NavigationJoin],
    filters: &[NavigationFilter],
    orders: &[ForEachOrder],
    order_cols: &[String],
    tables: &[Table],
    dialect: SqlDialect,
) -> String {
    let mut sql = String::new();
    sql.push_str("SELECT ");

    let resolve_column = |attr: &str| -> (String, String) {
        resolve_column_sql(attr, base_table, joins, tables, dialect)
    };

    let quoted_base = dialect.quote_identifier(&base_table.name);
    let cols: Vec<String> = base_table
        .columns
        .iter()
        .map(|c| format!("{quoted_base}.{}", dialect.quote_identifier(&c.name)))
        .collect();
    sql.push_str(&cols.join(", "));

    sql.push_str(" FROM ");
    sql.push_str(&quoted_base);

    for join in joins {
        let quoted_join = dialect.quote_identifier(&join.table);
        sql.push_str(&format!(
            " INNER JOIN {quoted_join} ON {}",
            join.on_condition
        ));
    }

    if !filters.is_empty() {
        sql.push_str(" WHERE ");
        let where_clauses: Vec<String> = filters
            .iter()
            .map(|f| {
                let (table_prefix, col_name) = resolve_column(&f.attribute);
                format!("{table_prefix}.{col_name} {} {}", f.op, f.expression)
            })
            .collect();
        sql.push_str(&where_clauses.join(" AND "));
    }

    if !orders.is_empty() {
        sql.push_str(" ORDER BY ");
        let order_clauses: Vec<String> = orders
            .iter()
            .map(|o| {
                let (table_prefix, col_name) = resolve_column(&o.attribute);
                let dir = if o.descending { " DESC" } else { " ASC" };
                format!("{table_prefix}.{col_name}{dir}")
            })
            .collect();
        sql.push_str(&order_clauses.join(", "));
    } else if !order_cols.is_empty() {
        sql.push_str(" ORDER BY ");
        let order_clauses: Vec<String> = order_cols
            .iter()
            .map(|col| {
                let (table_prefix, col_name) = resolve_column(col);
                format!("{table_prefix}.{col_name} ASC")
            })
            .collect();
        sql.push_str(&order_clauses.join(", "));
    }

    sql
}

/// Specifies one `ForEachStmt` within physical table schema and correlation context.
pub fn specify_for_each(
    for_each: &ForEachStmt,
    level_num: usize,
    tables: &[Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
    context_tables: &[&Table],
    dialect: SqlDialect,
) -> Result<NavigationLevel, String> {
    let base_table = infer_base_table(for_each, tables, extended_tables)?;
    let base_upper = base_table.name.to_ascii_uppercase();
    let ext_table = extended_tables
        .get(&base_upper)
        .ok_or("missing extended table")?;

    let referenced_attrs = extract_for_each_attributes(for_each);
    let joins = resolve_joins(base_table, &referenced_attrs, ext_table, dialect);
    let (index_name, default_order_cols) = select_index(base_table, &for_each.orders);

    let mut filters = Vec::new();

    // Context / correlation filters from outer nested For Each loops
    for parent in context_tables {
        for fk in &base_table.foreign_keys {
            if fk.referenced_table.eq_ignore_ascii_case(&parent.name) {
                for (fk_col, pk_col) in fk.columns.iter().zip(&fk.referenced_columns) {
                    filters.push(NavigationFilter {
                        attribute: fk_col.clone(),
                        op: "=".to_string(),
                        expression: format!(
                            "{}.{}",
                            dialect.quote_identifier(&parent.name),
                            dialect.quote_identifier(pk_col)
                        ),
                    });
                }
            }
        }
    }

    // Where filters
    for where_expr in &for_each.wheres {
        collect_filter_expressions(where_expr, dialect, &mut filters);
    }

    let order_names: Vec<String> = if !for_each.orders.is_empty() {
        for_each
            .orders
            .iter()
            .map(|o| o.attribute.clone())
            .collect()
    } else {
        default_order_cols.clone()
    };

    let sql = generate_navigation_sql(
        base_table,
        &joins,
        &filters,
        &for_each.orders,
        &default_order_cols,
        tables,
        dialect,
    );

    let mut formulas = Vec::new();
    extract_formulas_from_stmts(
        &for_each.body,
        Some(&base_table.name),
        tables,
        dialect,
        &mut formulas,
    )?;

    let mut dml_plans = Vec::new();
    if let Some(update_plan) =
        crate::dml::plan_updates_for_for_each(base_table, &for_each.body, dialect)?
    {
        dml_plans.push(update_plan);
    }
    let has_delete = for_each.body.iter().any(|s| matches!(s, Stmt::Delete));
    if has_delete {
        dml_plans.push(crate::dml::plan_delete_for_for_each(base_table, dialect));
    }
    let using_data_selector = for_each.using.as_ref().map(|u| u.name.clone());

    Ok(NavigationLevel {
        number: level_num,
        level_type: "For Each".to_string(),
        base_table: base_table.name.clone(),
        index: index_name,
        order: order_names,
        filters,
        joins,
        formulas,
        dml_plans,
        using_data_selector,
        sql,
        has_when_none: !for_each.when_none.is_empty(),
    })
}

/// Recursively traverses a Procedure's statements to extract and specify all navigation levels.
pub fn specify_statements<'a>(
    stmts: &[Stmt],
    tables: &'a [Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
    context_tables: &mut Vec<&'a Table>,
    levels: &mut Vec<NavigationLevel>,
    dialect: SqlDialect,
) -> Result<(), String> {
    for stmt in stmts {
        match stmt {
            Stmt::ForEach(for_each) => {
                let level_num = levels.len() + 1;
                let level = specify_for_each(
                    for_each,
                    level_num,
                    tables,
                    extended_tables,
                    context_tables,
                    dialect,
                )?;
                let base_table = tables
                    .iter()
                    .find(|t| t.name.eq_ignore_ascii_case(&level.base_table))
                    .ok_or("base table lookup failed")?;

                levels.push(level);

                context_tables.push(base_table);
                specify_statements(
                    &for_each.body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
                context_tables.pop();

                if !for_each.when_none.is_empty() {
                    specify_statements(
                        &for_each.when_none,
                        tables,
                        extended_tables,
                        context_tables,
                        levels,
                        dialect,
                    )?;
                }
            }
            Stmt::If(_, yes, no) => {
                specify_statements(
                    yes,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
                specify_statements(no, tables, extended_tables, context_tables, levels, dialect)?;
            }
            Stmt::While(_, body) => {
                specify_statements(
                    body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
            }
            Stmt::For(_, _, _, _, body) => {
                specify_statements(
                    body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
            }
            Stmt::ForIn(_, _, body) => {
                specify_statements(
                    body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
            }
            Stmt::Sub(_, body) => {
                specify_statements(
                    body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
            }
            _ => {}
        }
    }
    Ok(())
}

/// Generates a comprehensive navigation report for an authored procedure in a catalog (Matrix D2).
/// Generates navigation report for a procedure given precomputed tables and extended tables.
pub fn specify_procedure_with_tables(
    proc_obj: &KbObject,
    tables: &[Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
    dialect: SqlDialect,
) -> Result<NavigationReport, String> {
    let source = proc_obj
        .source_parts
        .iter()
        .find(|p| p.name.eq_ignore_ascii_case("Source"))
        .map(|p| p.source.as_str())
        .unwrap_or_default();

    let stmts = crate::frontend::parse_body(source)?;

    let mut levels = Vec::new();
    let mut context_tables = Vec::new();
    specify_statements(
        &stmts,
        tables,
        extended_tables,
        &mut context_tables,
        &mut levels,
        dialect,
    )?;

    let mut standalone_formulas = Vec::new();
    let mut standalone_dml = Vec::new();
    for stmt in &stmts {
        if !matches!(stmt, Stmt::ForEach(_)) {
            extract_formulas_from_stmt(stmt, None, tables, dialect, &mut standalone_formulas)?;
        }
        if let Stmt::New(new_stmt) = stmt {
            let plan = crate::dml::plan_insert_for_new(new_stmt, tables, dialect)?;
            standalone_dml.push(plan);
        }
    }

    Ok(NavigationReport {
        target_name: proc_obj.name.clone(),
        levels,
        standalone_formulas,
        standalone_dml,
        warnings: Vec::new(),
    })
}

/// Generates a comprehensive navigation report for an authored procedure in a catalog (Matrix D2).
pub fn specify_procedure(
    catalog: &Catalog,
    procedure_name: &str,
    dialect: SqlDialect,
) -> Result<NavigationReport, String> {
    let proc_obj = catalog
        .objects()
        .find(|obj| {
            obj.name.eq_ignore_ascii_case(procedure_name) && obj.kind == ObjectKind::Procedure
        })
        .ok_or_else(|| format!("procedure '{procedure_name}' not found in catalog"))?;

    let tables = derive_tables_from_catalog(catalog)?;
    let extended_tables = compute_extended_tables(&tables);
    specify_procedure_with_tables(proc_obj, &tables, &extended_tables, dialect)
}

/// Generates navigation reports for all procedures in a catalog.
pub fn specify_catalog(
    catalog: &Catalog,
    dialect: SqlDialect,
) -> Result<Vec<NavigationReport>, String> {
    specify_catalog_parallel(catalog, dialect, 1)
}

/// Generates navigation reports for all procedures in a catalog concurrently using `std::thread::scope`.
pub fn specify_catalog_parallel(
    catalog: &Catalog,
    dialect: SqlDialect,
    concurrency: usize,
) -> Result<Vec<NavigationReport>, String> {
    let tables = derive_tables_from_catalog(catalog)?;
    let extended_tables = compute_extended_tables(&tables);

    let procs: Vec<&KbObject> = catalog
        .objects()
        .filter(|obj| obj.kind == ObjectKind::Procedure)
        .collect();

    if procs.is_empty() {
        return Ok(Vec::new());
    }

    let thread_count = if concurrency == 0 {
        std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4)
    } else {
        concurrency
    }
    .min(procs.len())
    .max(1);

    if thread_count == 1 {
        let mut reports = Vec::with_capacity(procs.len());
        for proc in procs {
            reports.push(specify_procedure_with_tables(
                proc,
                &tables,
                &extended_tables,
                dialect,
            )?);
        }
        return Ok(reports);
    }

    let chunk_size = procs.len().div_ceil(thread_count);
    std::thread::scope(|s| {
        let mut handles = Vec::with_capacity(thread_count);
        for chunk in procs.chunks(chunk_size) {
            let tables_ref = &tables;
            let ext_ref = &extended_tables;
            handles.push(s.spawn(move || {
                let mut chunk_reports: Vec<NavigationReport> = Vec::with_capacity(chunk.len());
                for proc in chunk {
                    let r = specify_procedure_with_tables(proc, tables_ref, ext_ref, dialect)?;
                    chunk_reports.push(r);
                }
                Ok(chunk_reports)
            }));
        }

        let mut reports = Vec::with_capacity(procs.len());
        for handle in handles {
            match handle.join() {
                Ok(Ok(chunk_reports)) => reports.extend(chunk_reports),
                Ok(Err(err)) => return Err(err),
                Err(_) => {
                    return Err("parallel specification thread panicked".to_string());
                }
            }
        }
        Ok(reports)
    })
}
