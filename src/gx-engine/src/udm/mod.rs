//! Unified Data Model (UDM) physical storage, envelope decoding, and token extraction.
//! Implements direct reading of GeneXus physical knowledge base metadata and source parts
//! without relying on the COM STA SDK worker.

#![forbid(unsafe_code)]

pub mod client;
pub mod connection;
pub mod decoder;
pub mod entity_types;
pub mod library;
pub mod live;
pub mod mock;
pub mod reader;
pub mod row;
pub mod stored_sdt;
pub mod stored_transaction;
pub mod stored_variables;
pub mod tds;
pub mod type_evidence;
pub mod version_scope;
pub mod writer;

#[cfg(test)]
mod enum_domain_tests;

pub use client::{
    EntityTypeRecord, EntityVersionCompositionRecord, EntityVersionRecord, InMemoryDatabase,
    ModelCrossReferenceRecord, ModelEntityVersionRecord, ObjectReadMetadata,
    SYSTEM_ROOT_MODULE_GUID, StateLockRecord, UdmClient, UdmRow, UdmValue,
};
pub use connection::{AuthMode, KbConnectionInfo};
pub use decoder::{
    FLAG_GZIP, FLAG_UNCOMPRESSED_UTF8, HEADER_LEN, MAGIC, UNCOMPRESSED_PREFIX_LEN, UdmPartKind,
    VERSION_V1, build_token_data_list, decode_blob, encode_blob, escape_xml,
    extract_token_data_list, unescape_xml,
};
pub use entity_types::EntityTypeResolver;
pub use reader::{DEFAULT_DESIGN_MODEL_ID, LoadedObject, ObjectRow, UdmReader};
pub use writer::{UdmObjectIdentity, UdmWriter};
