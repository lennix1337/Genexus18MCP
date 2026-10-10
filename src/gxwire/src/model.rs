use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum EntityKind {
    Procedure,
    Transaction,
    WebPanel,
    Panel,
    WorkWithPlus,
    Attribute,
    Table,
    Domain,
    Sdt,
    DataSelector,
    Subroutine,
    Variable,
    Test,
    Unknown,
}

impl EntityKind {
    pub fn as_str(&self) -> &'static str {
        match self {
            EntityKind::Procedure => "Proc",
            EntityKind::Transaction => "Trn",
            EntityKind::WebPanel => "WebPanel",
            EntityKind::Panel => "Panel",
            EntityKind::WorkWithPlus => "WWP",
            EntityKind::Attribute => "Att",
            EntityKind::Table => "Tbl",
            EntityKind::Domain => "Dom",
            EntityKind::Sdt => "SDT",
            EntityKind::DataSelector => "DS",
            EntityKind::Subroutine => "Sub",
            EntityKind::Variable => "Var",
            EntityKind::Test => "Test",
            EntityKind::Unknown => "Unknown",
        }
    }

    #[allow(dead_code)]
    pub fn from_str_case_insensitive(s: &str) -> Self {
        match s.to_ascii_lowercase().as_str() {
            "procedure" | "proc" | "p" => EntityKind::Procedure,
            "transaction" | "trn" | "t" => EntityKind::Transaction,
            "webpanel" | "wp" => EntityKind::WebPanel,
            "panel" | "sdpanel" => EntityKind::Panel,
            "workwithplus" | "wwp" => EntityKind::WorkWithPlus,
            "attribute" | "att" | "a" => EntityKind::Attribute,
            "table" | "tbl" => EntityKind::Table,
            "domain" | "dom" => EntityKind::Domain,
            "sdt" => EntityKind::Sdt,
            "dataselector" | "ds" => EntityKind::DataSelector,
            "subroutine" | "sub" => EntityKind::Subroutine,
            "variable" | "var" => EntityKind::Variable,
            "test" | "gxunit" => EntityKind::Test,
            _ => EntityKind::Unknown,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum CallMode {
    Static,
    Dynamic,
    Udp,
    Submit,
    Link,
    BusinessComponent,
}

impl CallMode {
    pub fn as_str(&self) -> &'static str {
        match self {
            CallMode::Static => "static",
            CallMode::Dynamic => "dynamic",
            CallMode::Udp => "udp",
            CallMode::Submit => "submit",
            CallMode::Link => "link",
            CallMode::BusinessComponent => "bc",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum WriteMode {
    ForEachUpdate,
    New,
    Delete,
    BusinessComponent,
}

impl WriteMode {
    pub fn as_str(&self) -> &'static str {
        match self {
            WriteMode::ForEachUpdate => "update",
            WriteMode::New => "new",
            WriteMode::Delete => "delete",
            WriteMode::BusinessComponent => "bc",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum EdgeKind {
    Calls(CallMode),
    ReadsAttr,
    WritesAttr(WriteMode),
    NavigatesTable,
    DefinesFormula,
    SubtypeOf,
    UsesDomain,
    UsesSdt,
    ImplementsBc,
}

impl EdgeKind {
    pub fn as_str(&self) -> &'static str {
        match self {
            EdgeKind::Calls(mode) => mode.as_str(),
            EdgeKind::ReadsAttr => "reads_att",
            EdgeKind::WritesAttr(mode) => mode.as_str(),
            EdgeKind::NavigatesTable => "navigates",
            EdgeKind::DefinesFormula => "formula",
            EdgeKind::SubtypeOf => "subtype_of",
            EdgeKind::UsesDomain => "domain",
            EdgeKind::UsesSdt => "sdt",
            EdgeKind::ImplementsBc => "bc",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EntityNode {
    pub id: u32,
    pub name: String,
    pub kind: EntityKind,
    pub module: Option<String>,
    pub description: Option<String>,
    pub parm_rule: Option<String>,
    pub path: Option<String>,
    pub lines: usize,
    pub complexity: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RawEdge {
    pub from: u32,
    pub to: u32,
    pub kind: EdgeKind,
    pub line: u32,
}
