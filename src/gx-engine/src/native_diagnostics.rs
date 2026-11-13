//! Cheap, database-light source diagnostics for one saved object (`kb-diagnose`).
//!
//! The Gateway asks for this after an SDK save, so an agent gets compiler-style feedback in about a
//! second instead of after a whole build. It parses the object's `Source` with the same front end the
//! C# generator uses (`frontend::parse_body`): lexer and parser errors carry a line and column. It does
//! not specify, type-check or compile, so a clean result is `ok` for syntax only, never a build result.
//!
//! Scope is deliberately narrow and fail-soft: only a Procedure `Source` is checked, and everything
//! else is reported as `unavailable` with a reason, never as an error. The parser admits a subset of
//! GeneXus (see `frontend::Stmt`), so a construct outside that subset is reported like a syntax error.
//! The parser stops at its first error, so `errors` holds at most one entry today.

use crate::diagnostics::Diagnostic;
use crate::kb_ops::{KbReadError, ReadResult, read_object_scoped};
use crate::udm::UdmClient;
use crate::udm::version_scope::ActiveVersion;
use serde::Serialize;

/// Cap on reported errors, so the answer stays small if the parser ever recovers past the first one.
pub const MAX_ERRORS: usize = 10;

/// The outcome of a diagnostics pass.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Status {
    /// The checked source parsed.
    Ok,
    /// The checked source has at least one error.
    Errors,
    /// Nothing was checked; `reason` says why.
    Unavailable,
}

/// One located source error. `line` and `column` are one-based and absent when the parser gave none
/// (for example a source over the size limit).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SourceError {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub line: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub column: Option<usize>,
    pub message: String,
}

/// The `kb-diagnose` answer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct DiagnoseResult {
    pub name: String,
    #[serde(rename = "type")]
    pub object_type: String,
    pub status: Status,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    pub errors: Vec<SourceError>,
}

impl DiagnoseResult {
    fn unavailable(read: &ReadResult, reason: impl Into<String>) -> Self {
        Self {
            name: read.name.clone(),
            object_type: read.object_type.clone(),
            status: Status::Unavailable,
            reason: Some(reason.into()),
            errors: Vec::new(),
        }
    }
}

/// Parses `source` as a Procedure body. Empty for a clean parse.
pub fn procedure_source_errors(source: &str) -> Vec<SourceError> {
    let Err(message) = crate::frontend::parse_body(source) else {
        return Vec::new();
    };
    let diagnostic = Diagnostic::syntax_error(message);
    let error = match diagnostic.location {
        Some(location) => SourceError {
            line: Some(location.line),
            column: Some(location.column),
            // The location is reported as fields; keep only the parser's own text.
            message: diagnostic
                .message
                .split_once(": ")
                .map_or(diagnostic.message.clone(), |(_, detail)| detail.to_string()),
        },
        None => SourceError {
            line: None,
            column: None,
            message: diagnostic.message,
        },
    };
    vec![error]
}

/// Diagnoses an object that was already read (only its `SOURCE` part is used).
pub fn diagnose_read(read: &ReadResult) -> DiagnoseResult {
    if read.object_type != "Procedure" {
        return DiagnoseResult::unavailable(
            read,
            format!(
                "native diagnostics cover Procedure source only; {} is not supported",
                read.object_type
            ),
        );
    }
    let Some(source) = read
        .parts
        .iter()
        .find(|part| part.name.eq_ignore_ascii_case("SOURCE"))
    else {
        return DiagnoseResult::unavailable(read, "the object has no stored Source part");
    };
    let mut errors = procedure_source_errors(&source.source);
    errors.truncate(MAX_ERRORS);
    DiagnoseResult {
        name: read.name.clone(),
        object_type: read.object_type.clone(),
        status: if errors.is_empty() {
            Status::Ok
        } else {
            Status::Errors
        },
        reason: None,
        errors,
    }
}

/// Reads one object's `Source` from the active version and diagnoses it.
pub fn diagnose_object_scoped(
    client: &UdmClient,
    active: Option<ActiveVersion>,
    name: &str,
) -> Result<DiagnoseResult, KbReadError> {
    let read = read_object_scoped(client, active, name, Some(&["Source".to_string()]))?;
    Ok(diagnose_read(&read))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::kb_ops::PartPayload;

    fn read(object_type: &str, parts: &[(&str, &str)]) -> ReadResult {
        ReadResult {
            name: "P".into(),
            object_type: object_type.into(),
            parts: parts
                .iter()
                .map(|(name, source)| PartPayload {
                    name: name.to_string(),
                    source: source.to_string(),
                })
                .collect(),
            missing_parts: Vec::new(),
            available_parts: Vec::new(),
            version_token: None,
        }
    }

    #[test]
    fn a_clean_procedure_source_is_ok() {
        let result = diagnose_read(&read("Procedure", &[("SOURCE", "&x = 1\nReturn &x")]));
        assert_eq!(result.status, Status::Ok);
        assert!(result.errors.is_empty() && result.reason.is_none());
        assert_eq!(
            diagnose_read(&read("Procedure", &[("SOURCE", "")])).status,
            Status::Ok
        );
    }

    #[test]
    fn a_syntax_error_reports_its_line_and_column_without_the_prefix() {
        let result = diagnose_read(&read(
            "Procedure",
            &[("SOURCE", "&x = 1\n&y = (2 +\n&z = 3")],
        ));
        assert_eq!(result.status, Status::Errors);
        let [error] = result.errors.as_slice() else {
            panic!("{:?}", result.errors);
        };
        // The parser reports where the unfinished expression ends: line 2.
        assert_eq!(error.line, Some(2));
        assert!(error.column.is_some_and(|column| column >= 1));
        assert!(!error.message.starts_with("line "), "{}", error.message);
        assert!(!error.message.is_empty());
    }

    #[test]
    fn an_unclosed_comment_is_located_at_its_opening_line() {
        let result = diagnose_read(&read("Procedure", &[("SOURCE", "&x = 1\n/* open\n&y = 2")]));
        assert_eq!(result.status, Status::Errors);
        assert_eq!(result.errors[0].line, Some(2));
        assert_eq!(result.errors[0].column, Some(1));
        assert_eq!(result.errors[0].message, "unclosed block comment");
    }

    #[test]
    fn an_oversize_source_is_an_error_not_a_panic() {
        let huge = " ".repeat(crate::catalog::MAX_SOURCE_PART_BYTES + 1);
        let result = diagnose_read(&read("Procedure", &[("SOURCE", &huge)]));
        assert_eq!(result.status, Status::Errors);
        assert!(!result.errors[0].message.is_empty());
    }

    #[test]
    fn coordinates_are_omitted_from_json_when_the_parser_gave_none() {
        let error = SourceError {
            line: None,
            column: None,
            message: "m".into(),
        };
        assert_eq!(
            serde_json::to_value(&error).unwrap(),
            serde_json::json!({"message":"m"})
        );
    }

    #[test]
    fn other_types_and_missing_source_are_unavailable_never_errors() {
        for (object_type, parts) in [
            ("WebPanel", vec![("SOURCE", "&x = (")]),
            ("Transaction", vec![("RULES", "parm(")]),
            ("Procedure", vec![("RULES", "parm(&x);")]),
        ] {
            let result = diagnose_read(&read(object_type, &parts));
            assert_eq!(result.status, Status::Unavailable, "{object_type}");
            assert!(result.reason.as_deref().is_some_and(|r| !r.is_empty()));
            assert!(result.errors.is_empty());
        }
    }

    #[test]
    fn the_answer_serializes_to_the_documented_shape() {
        let ok = serde_json::to_value(diagnose_read(&read("Procedure", &[("SOURCE", "Return")])))
            .unwrap();
        assert_eq!(
            ok,
            serde_json::json!({"name":"P","type":"Procedure","status":"ok","errors":[]})
        );
        let unavailable = serde_json::to_value(diagnose_read(&read("SDT", &[]))).unwrap();
        assert_eq!(unavailable["status"], "unavailable");
        assert!(unavailable["reason"].is_string());
    }
}
