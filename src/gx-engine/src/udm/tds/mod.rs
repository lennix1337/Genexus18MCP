//! Native TDS read transport for the physical Knowledge Base database.
//!
//! An alternative to the `sqlcmd` subprocess of `udm::live`, off unless
//! `GXMCP_ENGINE_SQL_TRANSPORT=tds` selects it. It speaks the SQL Server wire protocol through
//! the `tiberius` crate, so `varbinary` part payloads arrive as bytes instead of hex text and no
//! `sqlcmd` is needed for generated reads. `udm::live` decides when it is used and when `sqlcmd`
//! takes over; this module only connects and reads.
//!
//! The session itself needs the `tds` cargo feature. Without it this module still parses server
//! names and renders rows, and `udm::live` reports that the transport is not built in.

#![forbid(unsafe_code)]

pub mod endpoint;
#[cfg(all(test, feature = "tds"))]
mod fake_server;
#[cfg(feature = "tds")]
mod session;
#[cfg(all(test, feature = "tds"))]
mod tests;

#[cfg(feature = "tds")]
pub use session::TdsSession;

/// Accepts the server certificate of a TCP or remote-pipe connection without validating it.
pub const TRUST_SERVER_CERTIFICATE_ENV: &str = "GXMCP_ENGINE_SQL_TRUST_SERVER_CERTIFICATE";

/// One column value of a row, reduced to what the engine's reads select.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Value {
    Null,
    /// `tinyint`, `smallint`, `int`, `bigint` and `bit`.
    Int(i64),
    /// Every character type, already decoded from the column's code page or UTF-16.
    Text(String),
    /// `varbinary`, `binary` and `image`, as stored.
    Bytes(Vec<u8>),
}

/// What one connection attempt asks of the server.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Encryption {
    /// TLS for the whole session; the server certificate must chain to the OS trust store.
    TlsVerified,
    /// TLS for the whole session; the server certificate is accepted as it is.
    TlsUnverified,
    /// No TLS unless the server requires it.
    Unencrypted,
}

impl Encryption {
    /// The label diagnostics carry.
    pub fn label(self) -> &'static str {
        match self {
            Self::TlsVerified => "tls",
            Self::TlsUnverified => "tls-unverified-certificate",
            Self::Unencrypted => "none",
        }
    }
}

/// How a session is opened. `Default` is the strict form: nothing is trusted unverified.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ConnectOptions {
    /// `GXMCP_ENGINE_SQL_TRUST_SERVER_CERTIFICATE=1`.
    pub trust_server_certificate: bool,
    /// The in-process fake endpoint of the tests has no TLS.
    #[cfg(test)]
    pub(crate) unencrypted_for_test: bool,
}

impl ConnectOptions {
    pub fn from_environment() -> Self {
        Self {
            trust_server_certificate: std::env::var(TRUST_SERVER_CERTIFICATE_ENV)
                .is_ok_and(|value| value.trim() == "1"),
            #[cfg(test)]
            unencrypted_for_test: false,
        }
    }

    /// The encryption each connection attempt asks for, in order. A later attempt is made only
    /// when the earlier one failed, and always before any statement is sent.
    ///
    /// Over TCP or a pipe of another machine the session is TLS or it is not opened: a server that
    /// does not offer encryption is refused, never read in clear text, and its certificate is
    /// validated unless the operator set the trust variable. A pipe of this machine (LocalDB) is
    /// asked for TLS without validating its self-signed certificate; when that fails and the login
    /// is Windows-integrated, so that no password travels, the pipe is read without TLS.
    pub fn encryption_attempts(&self, local_pipe: bool, integrated: bool) -> Vec<Encryption> {
        #[cfg(test)]
        if self.unencrypted_for_test {
            return vec![Encryption::Unencrypted];
        }
        match (local_pipe, integrated) {
            (true, true) => vec![Encryption::TlsUnverified, Encryption::Unencrypted],
            (true, false) => vec![Encryption::TlsUnverified],
            (false, _) if self.trust_server_certificate => vec![Encryption::TlsUnverified],
            (false, _) => vec![Encryption::TlsVerified],
        }
    }
}

/// Renders rows as `sqlcmd -y 0 -s <tab>` prints them, which is the text every caller of
/// `UdmClient::execute_live_sql` parses: one line per row, fields separated by a tab, `NULL` for
/// a null. A binary column has no agreed text form here and is refused.
pub fn render_text(rows: &[Vec<Value>]) -> Result<String, String> {
    use std::fmt::Write;
    let mut text = String::new();
    for row in rows {
        for (index, value) in row.iter().enumerate() {
            if index > 0 {
                text.push('\t');
            }
            match value {
                Value::Null => text.push_str("NULL"),
                Value::Int(number) => {
                    let _ = write!(text, "{number}");
                }
                Value::Text(value) => text.push_str(value),
                Value::Bytes(_) => {
                    return Err(format!(
                        "column {} is binary: a text read cannot carry it over the TDS transport",
                        index + 1
                    ));
                }
            }
        }
        text.push_str("\r\n");
    }
    Ok(text)
}

/// Removes the SQL login and password from an error before it is reported.
pub fn redact(text: &str, secrets: &[&str]) -> String {
    let mut text = text.to_string();
    let mut secrets: Vec<&str> = secrets
        .iter()
        .copied()
        .filter(|secret| !secret.is_empty())
        .collect();
    // Longest first: a login contained in the password must not leave the rest of the password behind.
    secrets.sort_by_key(|secret| std::cmp::Reverse(secret.len()));
    for secret in secrets {
        text = text.replace(secret, "<redacted>");
    }
    text
}

#[cfg(test)]
mod unit_tests {
    use super::*;

    #[test]
    fn rows_render_as_the_tab_separated_text_sqlcmd_prints() {
        let rows = vec![
            vec![Value::Int(31), Value::Text("Procedure".into()), Value::Null],
            vec![Value::Int(-7), Value::Text(String::new()), Value::Text("a b".into())],
        ];
        assert_eq!(
            render_text(&rows).unwrap(),
            "31\tProcedure\tNULL\r\n-7\t\ta b\r\n"
        );
        assert_eq!(render_text(&[]).unwrap(), "");
    }

    #[test]
    fn a_binary_column_is_refused_in_a_text_read() {
        let error = render_text(&[vec![Value::Int(1), Value::Bytes(vec![1])]]).unwrap_err();
        assert!(error.contains("column 2"), "{error}");
    }

    #[test]
    fn a_network_session_is_tls_with_a_validated_certificate_unless_trust_is_set() {
        // Mutation check: adding `Unencrypted` to a TCP plan, or dropping validation by default,
        // fails here.
        let strict = ConnectOptions::default();
        for integrated in [true, false] {
            assert_eq!(
                strict.encryption_attempts(false, integrated),
                [Encryption::TlsVerified]
            );
        }
        let trusting = ConnectOptions {
            trust_server_certificate: true,
            ..ConnectOptions::default()
        };
        assert_eq!(
            trusting.encryption_attempts(false, false),
            [Encryption::TlsUnverified]
        );
    }

    #[test]
    fn only_an_integrated_login_on_a_local_pipe_may_go_without_tls() {
        let options = ConnectOptions::default();
        assert_eq!(
            options.encryption_attempts(true, true),
            [Encryption::TlsUnverified, Encryption::Unencrypted]
        );
        // A SQL login sends a password: it is never offered an unencrypted attempt.
        assert_eq!(
            options.encryption_attempts(true, false),
            [Encryption::TlsUnverified]
        );
    }

    #[test]
    fn credentials_are_removed_from_error_text() {
        assert_eq!(
            redact("Login failed for user 'gxuser' (pw gxuser-secret)", &["gxuser", "gxuser-secret", ""]),
            "Login failed for user '<redacted>' (pw <redacted>)"
        );
    }
}
