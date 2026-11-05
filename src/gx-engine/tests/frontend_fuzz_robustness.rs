use gx_engine::frontend::{parse_body, parse_expression, parse_expression_with_attributes};

#[test]
fn test_parse_expression_fuzz_malformed_inputs_never_panic() {
    let malformed_inputs = [
        "",
        " ",
        "+",
        "-",
        "*",
        "/",
        "* 10",
        "1 + ",
        "1 + + 2",
        "1 + * 2",
        "(",
        ")",
        "(1 + 2",
        "1 + 2)",
        "((((1)))",
        "(((1))))",
        ">=",
        "<=",
        "<>",
        "=",
        "&",
        "&Var.",
        "&Var..Member",
        "&Var.Method(,",
        "&Var.Method(1, )",
        "&Var[",
        "&Var[]",
        "&Var[1,",
        "\"unterminated string",
        "'unterminated single quote",
        "99999999999999999999999999999999999999999999999999999999999999999999999999999999999999",
        "1.2.3.4",
        "// only comment",
        "/* unterminated block comment",
        "!@#$%^&*",
        "\0\0\0",
    ];

    for input in &malformed_inputs {
        let result = std::panic::catch_unwind(|| parse_expression(input));
        assert!(
            result.is_ok(),
            "parse_expression must not panic on input: {:?}",
            input
        );
        let expr_result = result.unwrap();
        assert!(
            expr_result.is_err(),
            "parse_expression must fail-closed on malformed input: {:?}, got {:?}",
            input,
            expr_result
        );
    }
}

#[test]
fn test_parse_expression_with_attributes_fuzz_never_panic() {
    let inputs = [
        "CustomerId.",
        "CustomerId[",
        "CustomerId.",
        "Customer.Method(,",
        "Table.Column.Extra.Invalid.",
    ];

    for input in &inputs {
        let result = std::panic::catch_unwind(|| parse_expression_with_attributes(input));
        assert!(
            result.is_ok(),
            "parse_expression_with_attributes must not panic on input: {:?}",
            input
        );
        assert!(result.unwrap().is_err());
    }
}

#[test]
fn test_parse_body_fuzz_malformed_statements_never_panic() {
    let malformed_bodies = [
        "If",
        "If 1",
        "If 1 = 1\n",
        "For Each",
        "Do While",
        "Sub",
        "Sub 123",
        "&x =",
        "Print",
        "Print(",
        "Return (",
        "EndIf without If",
        "EndFor without For",
    ];

    for body in &malformed_bodies {
        let result = std::panic::catch_unwind(|| parse_body(body));
        assert!(
            result.is_ok(),
            "parse_body must not panic on malformed body: {:?}",
            body
        );
        assert!(
            result.unwrap().is_err(),
            "parse_body must fail-closed on malformed body: {:?}",
            body
        );
    }
}

#[test]
fn test_deep_expression_nesting_does_not_overflow_stack() {
    // 30 levels of nested parentheses (within 128-node limit)
    let nested = format!("{}1{}", "(".repeat(30), ")".repeat(30));
    let parsed = parse_expression(&nested);
    assert!(parsed.is_ok(), "30 levels of parens must parse safely");

    // 30 levels of addition (within 128-node limit)
    let mut chained = String::from("1");
    for _ in 0..30 {
        chained.push_str(" + 1");
    }
    let parsed_chain = parse_expression(&chained);
    assert!(
        parsed_chain.is_ok(),
        "30 chained additions must parse safely"
    );

    // Exceeding MAX_EXPRESSION_NODES fails closed safely without panic
    let mut massive_chain = String::from("1");
    for _ in 0..150 {
        massive_chain.push_str(" + 1");
    }
    let parsed_massive = parse_expression(&massive_chain);
    assert!(
        parsed_massive.is_err(),
        "Expressions exceeding 128 nodes must fail-closed safely"
    );
}
