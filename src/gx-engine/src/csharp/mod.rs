//! Native C# (.NET Core / Framework) code generator for GeneXus.
//!
//! Track D of Plan 130:
//! - Generates `GXProcedure` C# class equivalent to GeneXus IDE output
//! - Maps GeneXus typed variables and expressions to valid C# constructs
//! - Emits .NET Core `net8.0`/`net9.0` `.csproj` project files

#![forbid(unsafe_code)]

pub mod calls;
pub mod cursor;
pub mod data_provider;
pub mod developer_menu;
pub mod domain;
pub mod emitter;
pub mod literals;
pub mod messages;
pub mod model_files;
pub mod not_authorized;
pub mod procedure;
pub mod project;
pub mod prompt;
pub mod prompt_js;
pub mod runtime_config;
pub mod sdt;
pub mod sdt_sdk;
pub mod solution;
pub mod template;
pub mod transaction;
pub mod transaction_form;
pub mod transaction_js;
pub mod types;
pub mod unanimo_master;
pub mod webform;
pub mod webpanel;
pub mod work_with;

pub use emitter::CSharpEmitter;
pub use procedure::{CsharpParam, ProcedureClass, STANDARD_IMPORTS};
pub use project::{CsprojConfig, CsprojEmitter, OutputType};
pub use sdt::SdtClass;
pub use solution::{SolutionConfig, SolutionEmitter, SolutionProject, deterministic_guid};
pub use transaction::{TRANSACTION_IMPORTS, TransactionClass};
pub use types::{
    clean_ident, to_csharp_attr_name, to_csharp_default_value, to_csharp_param_name,
    to_csharp_param_signature, to_csharp_type, to_csharp_var_name,
};
pub use webpanel::{WEBPANEL_IMPORTS, WebPanelClass};
