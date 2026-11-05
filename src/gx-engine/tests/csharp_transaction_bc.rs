//! Tests for C# Transaction and Business Component (BC) generation.

#![forbid(unsafe_code)]

use gx_engine::csharp::transaction::TransactionClass;
use gx_engine::model::DataType;
use gx_engine::transaction::{TransactionAttributeRef, TransactionLevel, TransactionStructure};

fn sample_structure() -> TransactionStructure {
    let mut structure = TransactionStructure::new(TransactionLevel {
        name: "Customer".to_string(),
        description: Some("Customer Master".to_string()),
        attributes: vec![
            TransactionAttributeRef {
                name: "CustomerId".to_string(),
                is_key: true,
                is_nullable: false,
                is_foreign_key: false,
                data_type: Some(DataType::Numeric {
                    precision: 9,
                    scale: 0,
                }),
                formula: None,
            },
            TransactionAttributeRef {
                name: "CustomerName".to_string(),
                is_key: false,
                is_nullable: false,
                is_foreign_key: false,
                data_type: Some(DataType::Character { length: 50 }),
                formula: None,
            },
            TransactionAttributeRef {
                name: "CustomerBalance".to_string(),
                is_key: false,
                is_nullable: false,
                is_foreign_key: false,
                data_type: Some(DataType::Numeric {
                    precision: 12,
                    scale: 2,
                }),
                formula: None,
            },
        ],
        levels: Vec::new(),
    });
    structure.is_business_component = true;
    structure
}

#[test]
fn transaction_class_emits_complete_business_component_api() {
    let structure = sample_structure();
    let tc = TransactionClass::from_structure("Customer", structure).with_business_component(true);

    let code = tc.emit();

    // Verification: Namespace and class declaration
    assert!(code.contains("namespace GeneXus.Programs"));
    assert!(code.contains("public class Customer : GXDataArea"));

    // Verification: Property accessors matching attributes
    assert!(code.contains("public int CustomerId"));
    assert!(code.contains("public string CustomerName"));
    assert!(code.contains("public decimal CustomerBalance"));

    // Verification: Business Component CRUD lifecycle methods
    assert!(code.contains("public void Load( int aP_CustomerId )"));
    assert!(code.contains("this.A_CustomerId = aP_CustomerId;"));
    assert!(code.contains("getByPrimaryKey();"));

    assert!(code.contains("public void Load( )"));
    assert!(code.contains("public void Save( )"));
    assert!(code.contains("public void Insert( )"));
    assert!(code.contains("public void Update( )"));
    assert!(code.contains("public void Delete( )"));
    assert!(code.contains("public bool Success( )"));
    assert!(code.contains("public bool Fail( )"));
}

#[test]
fn non_business_component_transaction_omits_bc_methods() {
    let mut structure = sample_structure();
    structure.is_business_component = false;
    let tc = TransactionClass::from_structure("Customer", structure).with_business_component(false);

    let code = tc.emit();
    assert!(!code.contains("public void Load( int aP_CustomerId )"));
    assert!(!code.contains("public void Insert( )"));
    assert!(!code.contains("public void Update( )"));
}

#[test]
fn business_component_with_composite_key_emits_multikey_load() {
    let structure = TransactionStructure::new(TransactionLevel {
        name: "BranchOffice".to_string(),
        description: Some("Branch Office".to_string()),
        attributes: vec![
            TransactionAttributeRef {
                name: "CompanyId".to_string(),
                is_key: true,
                is_nullable: false,
                is_foreign_key: false,
                data_type: Some(DataType::Numeric {
                    precision: 4,
                    scale: 0,
                }),
                formula: None,
            },
            TransactionAttributeRef {
                name: "BranchId".to_string(),
                is_key: true,
                is_nullable: false,
                is_foreign_key: false,
                data_type: Some(DataType::Numeric {
                    precision: 4,
                    scale: 0,
                }),
                formula: None,
            },
            TransactionAttributeRef {
                name: "BranchAddress".to_string(),
                is_key: false,
                is_nullable: false,
                is_foreign_key: false,
                data_type: Some(DataType::Character { length: 100 }),
                formula: None,
            },
        ],
        levels: Vec::new(),
    })
    .with_business_component(true);

    let tc = TransactionClass::from_structure("BranchOffice", structure);
    let code = tc.emit();

    assert!(code.contains("public void Load( short aP_CompanyId, short aP_BranchId )"));
    assert!(code.contains("this.A_CompanyId = aP_CompanyId;"));
    assert!(code.contains("this.A_BranchId = aP_BranchId;"));
    assert!(code.contains("getByPrimaryKey();"));
}

#[test]
fn business_component_emits_typed_default_and_condition_rules() {
    use gx_engine::transaction::TransactionRule;

    let mut structure = sample_structure();
    structure.rules.push(TransactionRule::Default {
        attribute: "CustomerName".to_string(),
        value: "'Anonymous'".to_string(),
    });
    structure.rules.push(TransactionRule::Error {
        message: "Name is required".to_string(),
        condition: Some("CustomerName.IsEmpty()".to_string()),
    });

    let tc = TransactionClass::from_structure("Customer", structure);
    let code = tc.emit();

    assert!(
        code.contains("if ( string.IsNullOrEmpty(A_CustomerName) )"),
        "string attribute must not emit A_CustomerName == 0: {code}"
    );
    assert!(code.contains("A_CustomerName = \"Anonymous\";"));
    assert!(
        !code.contains("if ( /*"),
        "condition must be translated rather than commented out into if ( true ): {code}"
    );
}
