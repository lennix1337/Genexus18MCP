use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::ProcedureClass,
    navigation::NavigationLevel,
};

#[test]
fn test_procedure_for_each_cursor_emission() {
    let mut object = KbObject::new(
        ObjectId::new("plistcustomers").unwrap(),
        "PListCustomers",
        ObjectKind::Procedure,
    );
    let source = "For Each\n    &Total = &Total + 1\nWhen none\n    &Total = 0\nEndFor\n";
    let vars = r#"{ "Total": { "kind": "numeric", "precision": 5, "scale": 0 } }"#;
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: source.into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: vars.into(),
    });

    let nav_level = NavigationLevel {
        number: 1,
        level_type: "For Each".into(),
        base_table: "Customer".into(),
        index: "PK_Customer".into(),
        order: vec!["CustomerId".into()],
        filters: vec![],
        joins: vec![],
        formulas: vec![],
        dml_plans: vec![],
        using_data_selector: None,
        sql: "SELECT CustomerId, CustomerName FROM Customer ORDER BY CustomerId ASC".into(),
        has_when_none: true,
    };

    let class = ProcedureClass::from_kb_object(&object)
        .expect("from_kb_object")
        .with_navigation_levels(vec![nav_level]);

    let emitted = class.emit_checked().expect("emit_checked");

    assert!(emitted.contains("/* Using cursor P00011 */"));
    assert!(emitted.contains("pr_default.execute(0);"));
    assert!(emitted.contains("while ( (pr_default.getStatus(0) != 101) )"));
    assert!(emitted.contains("pr_default.readNext(0);"));
    assert!(emitted.contains("pr_default.close(0);"));
    assert!(emitted.contains("if ( pr_default.getStatus(0) == 101 )"));
    assert!(emitted.contains("public override void cursorDefinitions( )"));
    assert!(emitted.contains("new CursorDef(\"P00011\""));
    assert!(emitted.contains("SELECT CustomerId, CustomerName FROM Customer"));
    assert!(emitted.contains("private IDataStoreProvider pr_default ;"));
    assert!(emitted.contains("private IDataReader P00011_cursor ;"));
}

#[test]
fn test_procedure_for_each_parameterized_cursor_emission() {
    let mut object = KbObject::new(
        ObjectId::new("plistactive").unwrap(),
        "PListActive",
        ObjectKind::Procedure,
    );
    let source =
        "For Each Where CustomerStatus = &CustomerStatus\n    &Total = &Total + 1\nEndFor\n";
    let vars = r#"{ "CustomerStatus": { "kind": "numeric", "precision": 4, "scale": 0 }, "Total": { "kind": "numeric", "precision": 5, "scale": 0 } }"#;
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: source.into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: vars.into(),
    });

    let nav_level = NavigationLevel {
        number: 1,
        level_type: "For Each".into(),
        base_table: "Customer".into(),
        index: "PK_Customer".into(),
        order: vec!["CustomerId".into()],
        filters: vec![gx_engine::navigation::NavigationFilter {
            attribute: "CustomerStatus".into(),
            op: "=".into(),
            expression: "@AV8CustomerStatus".into(),
        }],
        joins: vec![],
        formulas: vec![],
        dml_plans: vec![],
        using_data_selector: None,
        sql: "SELECT CustomerId, CustomerStatus FROM Customer WHERE CustomerStatus = @AV8CustomerStatus".into(),
        has_when_none: false,
    };

    let class = ProcedureClass::from_kb_object(&object)
        .expect("from_kb_object")
        .with_navigation_levels(vec![nav_level]);

    let emitted = class.emit_checked().expect("emit_checked");

    assert!(emitted.contains("new ParDef(\"@AV8CustomerStatus\",GXType.Int16,4,0)"));
    assert!(emitted.contains("new CursorDef(\"P00011\", @\""));
}

#[test]
fn test_cursor_parameter_extraction_ignores_literals_and_comments() {
    use gx_engine::csharp::cursor::extract_parameters;

    let nav_level = NavigationLevel {
        number: 2,
        level_type: "For Each".into(),
        base_table: "User".into(),
        index: "PK_User".into(),
        order: vec!["UserId".into()],
        filters: vec![gx_engine::navigation::NavigationFilter {
            attribute: "TenantId".into(),
            op: "=".into(),
            expression: "TenantId = :TenantId".into(),
        }],
        joins: vec![],
        formulas: vec![],
        dml_plans: vec![],
        using_data_selector: None,
        sql: "SELECT UserId, Email FROM User WHERE Email = 'support@company.com' /* @ignored_comment */ -- @line_comment\nAND Active = @Active".into(),
        has_when_none: false,
    };

    let params = extract_parameters(&nav_level);
    assert_eq!(params.len(), 2);
    assert!(params.contains(&"@TenantId".to_string()));
    assert!(params.contains(&"@Active".to_string()));
    assert!(!params.iter().any(|p| p.contains("company.com")));
    assert!(!params.iter().any(|p| p.contains("ignored_comment")));
    assert!(!params.iter().any(|p| p.contains("line_comment")));
}

#[test]
fn test_cursor_parameters_from_joins_formulas_and_dml() {
    use gx_engine::csharp::cursor::{extract_parameters, infer_param_def};
    use gx_engine::dml::{ColumnAssignment, NavigationDmlOp, NavigationDmlPlan};
    use gx_engine::navigation::{
        NavigationFilter, NavigationFormula, NavigationJoin, NavigationLevel,
    };
    use std::collections::BTreeMap;

    let mut dml_sql = BTreeMap::new();
    dml_sql.insert(
        "sqlserver".into(),
        "UPDATE Invoice SET Total = @AV10CalculatedTotal WHERE Id = :AV11InvoiceId".into(),
    );

    let nav_level = NavigationLevel {
        number: 3,
        level_type: "For Each".into(),
        base_table: "Invoice".into(),
        index: "PK_Invoice".into(),
        order: vec!["InvoiceId".into()],
        filters: vec![NavigationFilter {
            attribute: "CustomerId".into(),
            op: "=".into(),
            expression: "@AV9CustomerId".into(),
        }],
        joins: vec![NavigationJoin {
            table: "Customer".into(),
            on_condition:
                "Invoice.CustomerId = Customer.CustomerId AND Customer.TenantId = @TenantId".into(),
        }],
        formulas: vec![NavigationFormula {
            formula_type: "Sum".into(),
            target_attribute: Some("InvoiceLinesTotal".into()),
            base_table: "InvoiceLine".into(),
            index: None,
            conditions: vec![NavigationFilter {
                attribute: "LineDiscount".into(),
                op: "<=".into(),
                expression: "@AV12MaxDiscount".into(),
            }],
            correlations: vec!["InvoiceLine.Date >= @FromDate".into()],
            sql: "SELECT SUM(LineTotal) FROM InvoiceLine WHERE InvoiceId = @InvoiceId".into(),
            default_value: None,
        }],
        dml_plans: vec![NavigationDmlPlan {
            operation: NavigationDmlOp::Update,
            target_table: "Invoice".into(),
            primary_key: vec!["InvoiceId".into()],
            assignments: vec![ColumnAssignment {
                attribute: "InvoiceDiscount".into(),
                column: "InvoiceDiscount".into(),
                expr_sql: "@AV13DiscountPercent".into(),
            }],
            where_predicates: vec!["BranchId = @BranchId".into()],
            has_when_duplicate: false,
            sql: dml_sql,
        }],
        using_data_selector: None,
        sql: "SELECT InvoiceId FROM Invoice WHERE CustomerName LIKE @lV8CustomerName".into(),
        has_when_none: false,
    };

    let params = extract_parameters(&nav_level);
    assert!(params.contains(&"@AV9CustomerId".to_string()));
    assert!(params.contains(&"@TenantId".to_string()));
    assert!(params.contains(&"@AV12MaxDiscount".to_string()));
    assert!(params.contains(&"@FromDate".to_string()));
    assert!(params.contains(&"@InvoiceId".to_string()));
    assert!(params.contains(&"@AV10CalculatedTotal".to_string()));
    assert!(params.contains(&"@AV11InvoiceId".to_string()));
    assert!(params.contains(&"@AV13DiscountPercent".to_string()));
    assert!(params.contains(&"@BranchId".to_string()));
    assert!(params.contains(&"@lV8CustomerName".to_string()));

    // Verify type inferences:
    assert_eq!(infer_param_def("@lV8CustomerName").0, "GXType.NVarChar");
    assert_eq!(infer_param_def("@AV9CustomerId").0, "GXType.Int32");
    assert_eq!(infer_param_def("@AV9InvalidCount").0, "GXType.Int32");
    assert_eq!(infer_param_def("@AV12MaxDiscount").0, "GXType.Decimal");
    assert_eq!(infer_param_def("@FromDate").0, "GXType.Date");
    assert_eq!(infer_param_def("@AV13DiscountPercent").0, "GXType.Decimal");
}
