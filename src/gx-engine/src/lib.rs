//! Independent compiler and build engine replacing the proprietary GeneXus SDK.
//! Provides direct native UDM storage access, C# code generation, and parallel Rebuild All.

#![forbid(unsafe_code)]
#![allow(
    clippy::collapsible_if,
    clippy::manual_range_contains,
    clippy::needless_lifetimes,
    clippy::single_match,
    clippy::unnecessary_cast,
    clippy::manual_div_ceil
)]

pub mod backend;
pub mod build;
pub mod catalog;
pub mod catalog_cache;
pub mod csc_direct;
pub mod csharp;
pub mod dataprovider;
pub mod dataselector;
pub mod datastore;
pub mod dataview;
pub mod date;
pub mod datetime;
pub mod decimal;
mod dependencies;
pub mod design_system;
pub mod diagnostics;
pub mod diagnostics_catalog;
pub mod dml;
pub mod exchange;
pub mod extended_types;
pub mod formatter;
pub mod frontend;
pub mod gam;
pub mod gxtest;
pub mod impact;
pub mod incremental;
pub mod kb;
pub mod kb_ops;
pub mod locks;
pub mod model;
pub mod module;
pub mod native_diagnostics;
pub mod navigation;
pub mod output_channel;
pub mod reachability;
pub mod read_pagination;
pub mod read_token;
pub mod rebuild;
pub mod record_runtime;
pub mod reorg;
pub mod report;
pub mod rules;
pub mod structured;
pub mod table;
pub mod text;
pub mod text_tree;
pub mod transaction;
mod typed_backend;
pub mod types;
pub mod udm;
pub mod versioning;
pub mod webpanel;
pub mod webpanel_settings;
pub mod xpz;

pub use rebuild::{RebuildOptions, RebuildReport, rebuild_all};

pub fn compile(project: &model::Project) -> Result<String, String> {
    if project.format_version == 2 {
        return typed_backend::emit(project);
    }
    let procedures = project.parse()?;
    backend::emit(&procedures, &project.entry.to_ascii_uppercase())
}
