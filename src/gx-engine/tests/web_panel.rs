use gx_engine::webpanel::{ControlKind, WebPanel};

#[test]
fn test_parse_webpanel_dsl_with_controls_and_events() {
    let dsl = r#"
        webpanel CustomerDetail {
            master_page = "AppMaster";
            theme = "Carmine";

            layout {
                textblock lblHeader { caption = "Customer Profile"; class = "Title"; }
                attribute CustomerId { binding = "CustomerId"; readonly = true; }
                attribute CustomerName { binding = "CustomerName"; readonly = false; }
                button btnSave { caption = "Save"; event = "Enter"; class = "BtnPrimary"; }
            }

            events {
                Event 'Start'
                    &CustomerId = 100
                EndEvent

                Event 'Enter'
                    SaveCustomer(&CustomerId, &CustomerName)
                EndEvent
            }
        }
    "#;

    let wp = WebPanel::parse("CustomerDetail", dsl).expect("WebPanel DSL should parse");
    assert_eq!(wp.name, "CustomerDetail");
    assert_eq!(wp.master_page.as_deref(), Some("AppMaster"));
    assert_eq!(wp.theme.as_deref(), Some("Carmine"));
    assert_eq!(wp.controls.len(), 4);
    assert_eq!(wp.events.len(), 2);

    let lbl = wp
        .find_control("lblHeader")
        .expect("lblHeader should exist");
    assert_eq!(lbl.kind, ControlKind::TextBlock);
    assert_eq!(lbl.caption.as_deref(), Some("Customer Profile"));
    assert_eq!(lbl.theme_class.as_deref(), Some("Title"));

    let attr = wp
        .find_control("CustomerId")
        .expect("CustomerId should exist");
    assert_eq!(attr.kind, ControlKind::Attribute);
    assert!(attr.is_read_only);

    let btn = wp.find_control("btnSave").expect("btnSave should exist");
    assert_eq!(btn.kind, ControlKind::Button);
    assert_eq!(btn.event_name.as_deref(), Some("Enter"));

    let start_ev = wp.find_event("Start").expect("Start event should exist");
    assert!(start_ev.body.contains("&CustomerId = 100"));

    let enter_ev = wp.find_event("Enter").expect("Enter event should exist");
    assert!(enter_ev.body.contains("SaveCustomer"));
}

#[test]
fn test_parse_webpanel_json_roundtrip() {
    let json = r#"{
        "name": "OrderList",
        "master_page": "MainMaster",
        "controls": [
            {
                "name": "gridOrders",
                "kind": "grid",
                "binding": "Orders"
            },
            {
                "name": "btnRefresh",
                "kind": "button",
                "caption": "Refresh",
                "event_name": "Refresh"
            }
        ],
        "events": [
            {
                "name": "Refresh",
                "body": "GridOrders.Refresh()"
            }
        ]
    }"#;

    let wp = WebPanel::parse("OrderList", json).expect("JSON should parse");
    assert_eq!(wp.name, "OrderList");
    assert_eq!(wp.controls.len(), 2);
    assert_eq!(wp.events.len(), 1);

    let serialized = serde_json::to_string(&wp).expect("Should serialize to JSON");
    let roundtrip: WebPanel = serde_json::from_str(&serialized).expect("Should deserialize");
    assert_eq!(wp, roundtrip);
}

#[test]
fn test_fail_closed_on_duplicate_control_name() {
    let dsl = r#"
        webpanel DupControl {
            layout {
                textblock title { caption = "A"; }
                textblock title { caption = "B"; }
            }
        }
    "#;
    let err = WebPanel::parse("DupControl", dsl).unwrap_err();
    assert!(
        err.contains("Duplicate control name 'title'"),
        "Actual: {err}"
    );
}

#[test]
fn test_fail_closed_on_duplicate_event_name() {
    let dsl = r#"
        webpanel DupEvent {
            events {
                Event 'Start'
                    &A = 1
                EndEvent
                Event 'Start'
                    &A = 2
                EndEvent
            }
        }
    "#;
    let err = WebPanel::parse("DupEvent", dsl).unwrap_err();
    assert!(
        err.contains("Duplicate event definition 'Start'"),
        "Actual: {err}"
    );
}

#[test]
fn test_fail_closed_on_button_missing_event_name() {
    let dsl = r#"
        webpanel BadButton {
            layout {
                button btnAction { caption = "Do"; }
            }
        }
    "#;
    let err = WebPanel::parse("BadButton", dsl).unwrap_err();
    assert!(err.contains("must define an event_name"), "Actual: {err}");
}

#[test]
fn test_fail_closed_on_empty_source() {
    let err = WebPanel::parse("Empty", "   \n\t  ").unwrap_err();
    assert!(err.contains("source cannot be empty"), "Actual: {err}");
}

#[test]
fn test_webpanel_case_insensitive_lookup() {
    let dsl = r#"
        webpanel LookupTest {
            layout {
                attribute MyAttr { binding = "MyAttr"; }
            }
            events {
                Event 'MyEvent'
                    &X = 1
                EndEvent
            }
        }
    "#;
    let wp = WebPanel::parse("LookupTest", dsl).unwrap();
    assert!(wp.find_control("myattr").is_some());
    assert!(wp.find_control("MYATTR").is_some());
    assert!(wp.find_event("myevent").is_some());
    assert!(wp.find_event("MYEVENT").is_some());
}

#[test]
fn test_webpanel_default_attribute_binding() {
    let dsl = r#"
        webpanel BindingTest {
            layout {
                attribute CustomerAddress { readonly = false; }
            }
        }
    "#;
    let wp = WebPanel::parse("BindingTest", dsl).unwrap();
    let c = wp.find_control("CustomerAddress").unwrap();
    assert_eq!(c.binding.as_deref(), Some("CustomerAddress"));
}

#[test]
fn test_webpanel_nested_controls_validation() {
    let mut root = WebPanel {
        name: "Nested".to_string(),
        ..Default::default()
    };
    root.controls.push(gx_engine::webpanel::LayoutControl {
        name: "Container1".to_string(),
        kind: ControlKind::ResponsiveTable,
        caption: None,
        binding: None,
        event_name: None,
        theme_class: None,
        is_read_only: false,
        children: vec![
            gx_engine::webpanel::LayoutControl {
                name: "ChildAttr".to_string(),
                kind: ControlKind::Attribute,
                caption: None,
                binding: Some("ChildAttr".to_string()),
                event_name: None,
                theme_class: None,
                is_read_only: false,
                children: vec![],
            },
            gx_engine::webpanel::LayoutControl {
                name: "ChildAttr".to_string(), // Duplicate
                kind: ControlKind::Attribute,
                caption: None,
                binding: Some("ChildAttr".to_string()),
                event_name: None,
                theme_class: None,
                is_read_only: false,
                children: vec![],
            },
        ],
    });

    let err = root.validate().unwrap_err();
    assert!(
        err.contains("Duplicate control name 'ChildAttr'"),
        "Actual: {err}"
    );
}

#[test]
fn test_webpanel_empty_events_and_controls_valid() {
    let wp = WebPanel {
        name: "Minimal".to_string(),
        ..Default::default()
    };
    assert!(wp.validate().is_ok());
}

#[test]
fn test_webpanel_controls_table_and_edit() {
    let dsl = r#"
        webpanel FormWithTableAndEdit {
            layout {
                table tblMain { class = "MainContainer"; }
                edit edtCustomerCode { binding = "CustomerCode"; }
                textblock lblHeader { caption = "Customer Code"; }
                button btnSubmit { caption = "Submit"; event = "Confirm"; }
            }
        }
    "#;

    let wp = WebPanel::parse("FormWithTableAndEdit", dsl).expect("DSL should parse");
    assert_eq!(wp.controls.len(), 4);

    let tbl = wp.find_control("tblMain").expect("tblMain exists");
    assert_eq!(tbl.kind, ControlKind::Table);
    assert_eq!(tbl.theme_class.as_deref(), Some("MainContainer"));

    let edt = wp
        .find_control("edtCustomerCode")
        .expect("edtCustomerCode exists");
    assert_eq!(edt.kind, ControlKind::Edit);
    assert_eq!(edt.binding.as_deref(), Some("CustomerCode"));

    let btn = wp.find_control("btnSubmit").expect("btnSubmit exists");
    assert_eq!(btn.kind, ControlKind::Button);
    assert_eq!(btn.event_name.as_deref(), Some("Confirm"));
}

#[test]
fn test_webpanel_screen_variables_and_rules() {
    use gx_engine::model::DataType;
    use gx_engine::webpanel::ScreenVariable;

    let mut wp = WebPanel {
        name: "CustomerFilter".to_string(),
        ..Default::default()
    };

    wp.variables.push(ScreenVariable::new(
        "&CustomerId",
        DataType::Numeric {
            precision: 6,
            scale: 0,
        },
    ));
    wp.variables.push(ScreenVariable::new(
        "&CustomerName",
        DataType::Character { length: 50 },
    ));
    wp.rules
        .push("Parm(in:&CustomerId, out:&CustomerName)".to_string());
    wp.rules
        .push("Default(&CustomerName, 'Unknown')".to_string());

    assert!(wp.validate().is_ok());

    let var1 = wp
        .find_variable("CustomerId")
        .expect("CustomerId variable exists");
    assert_eq!(
        var1.data_type,
        DataType::Numeric {
            precision: 6,
            scale: 0,
        }
    );

    let parsed_rules = wp.parse_rules().expect("Rules should parse");
    assert_eq!(parsed_rules.len(), 2);
    match &parsed_rules[0] {
        gx_engine::rules::Rule::Parm(args) => {
            assert_eq!(args.len(), 2);
            assert_eq!(args[0].mode, "IN");
            assert_eq!(args[0].name, "CUSTOMERID");
            assert!(args[0].is_variable);
            assert_eq!(args[1].mode, "OUT");
            assert_eq!(args[1].name, "CUSTOMERNAME");
            assert!(args[1].is_variable);
        }
        _ => panic!("Expected Parm rule"),
    }
}

#[test]
fn test_webpanel_events_lifecycle_and_grid_load() {
    use gx_engine::webpanel::{EventKind, LayoutControl, WebPanelEvent};

    let mut wp = WebPanel {
        name: "GridPanel".to_string(),
        ..Default::default()
    };

    wp.controls.push(LayoutControl {
        name: "Grid1".to_string(),
        kind: ControlKind::Grid,
        caption: None,
        binding: Some("Customer".to_string()),
        event_name: None,
        theme_class: None,
        is_read_only: false,
        children: vec![],
    });

    let ev_start = WebPanelEvent::new("Start", "&Count = 0");
    assert!(ev_start.is_start() && !ev_start.is_refresh());
    let ev_refresh = WebPanelEvent::new("Refresh", "Do 'CheckAuth'");
    assert!(ev_refresh.is_refresh());
    let ev_grid_load = WebPanelEvent::new("Grid1.Load", "Load");
    assert!(ev_grid_load.is_load());
    match ev_grid_load.event_kind() {
        EventKind::Load { grid } => assert_eq!(grid.as_deref(), Some("Grid1")),
        _ => panic!("Expected Grid1 Load event"),
    }
    let ev_user = WebPanelEvent::new("'DoExport'", "Call(ExportPdf)");
    assert!(ev_user.is_user());
    let ev_enter = WebPanelEvent::new("Enter", "&Count = &Count + 1");
    assert!(ev_enter.is_enter());
    wp.events
        .extend([ev_start, ev_refresh, ev_grid_load, ev_user, ev_enter]);
    assert!(wp.validate().is_ok());

    let class = gx_engine::csharp::WebPanelClass::from_webpanel(&wp);
    let code = class.emit();
    assert!(code.contains("E13_Enter();") && code.contains("protected void E13_Enter( )"));
    assert!(code.contains("protected void E13_Grid1_Load( )"));

    let mut wp_dup_enter = wp.clone();
    wp_dup_enter
        .events
        .push(WebPanelEvent::new("'Enter'", "&Count = 2"));
    assert!(wp_dup_enter.validate().is_err());

    let mut wp_dup_start = wp.clone();
    wp_dup_start
        .events
        .push(WebPanelEvent::new("start", "&Count = 1"));
    assert!(wp_dup_start.validate().is_err());

    // Fails closed if Grid.Load points to non-existent grid
    let mut wp_bad_grid = WebPanel {
        name: "BadGridPanel".to_string(),
        ..Default::default()
    };
    wp_bad_grid
        .events
        .push(WebPanelEvent::new("NonExistentGrid.Load", "Load"));
    let err = wp_bad_grid.validate().unwrap_err();
    assert!(err.contains("does not exist in WebPanel layout"));
}
