//! Live SQL Server / LocalDB driver for GeneXus physical storage.
//!
//! Statements run through a `sqlcmd` subprocess. Generated reads can instead use the native TDS
//! transport of `udm::tds` when `GXMCP_ENGINE_SQL_TRANSPORT=tds` selects it; [`SqlTransport`]
//! decides which one a connection's reads use, once, and never mixes the two.

#![forbid(unsafe_code)]

use crate::udm::connection::{AuthMode, KbConnectionInfo};
use crate::udm::tds;
use std::ffi::OsString;
use std::sync::{Arc, Mutex, OnceLock};

pub use crate::udm::tds::Value;

fn sqlcmd_command(info: &KbConnectionInfo) -> Result<std::process::Command, String> {
    sqlcmd_command_with_credentials(
        info,
        std::env::var_os("SQLCMDUSER"),
        std::env::var_os("SQLCMDPASSWORD"),
    )
}

/// Checks credential availability without starting SQL or touching build outputs.
/// The CLI uses this only before rebuild-all's first read to prove safe SDK fallback.
pub fn validate_sqlcmd_auth(info: &KbConnectionInfo) -> Result<(), String> {
    sqlcmd_command(info).map(|_| ())
}

fn sqlcmd_command_with_credentials(
    info: &KbConnectionInfo,
    user_env: Option<std::ffi::OsString>,
    password_env: Option<std::ffi::OsString>,
) -> Result<std::process::Command, String> {
    let mut cmd = std::process::Command::new("sqlcmd");
    cmd.arg("-S").arg(&info.server_instance);
    cmd.arg("-d").arg(&info.db_name);
    cmd.arg("-b"); // on error batch abort
    // `-y 0` removes the 256-character limit sqlcmd applies to variable-length
    // columns. Without it every `CONVERT(VARCHAR(MAX), <varbinary>, 2)` hex
    // column (part blobs) was cut mid-stream and `decode_blob` failed with
    // "incomplete deflate stream". This sqlcmd build rejects `-y 0` together
    // with `-W`/`-h`, so callers trim their own fields (they already do).
    cmd.arg("-y").arg("0");
    cmd.arg("-s").arg("\t"); // tab delimited

    match sql_credentials(info, user_env, password_env)? {
        None => {
            cmd.arg("-E");
            // Inherited SQL-auth variables must not conflict with explicit Windows auth.
            cmd.env_remove("SQLCMDUSER").env_remove("SQLCMDPASSWORD");
        }
        Some((user, password)) => {
            // Never let sqlcmd prompt on stdin (which carries SELECT batches in ReadSession).
            // Credentials travel in the child's environment, never argv or error text.
            cmd.env("SQLCMDUSER", user);
            cmd.env("SQLCMDPASSWORD", password);
        }
    }

    Ok(cmd)
}

/// The login and password of a SQL-authenticated connection, `None` for Windows integrated
/// authentication. The environment values override the connection file; both transports take
/// their credentials from here, so neither accepts what the other refuses.
fn sql_credentials(
    info: &KbConnectionInfo,
    user_env: Option<OsString>,
    password_env: Option<OsString>,
) -> Result<Option<(OsString, OsString)>, String> {
    let AuthMode::SqlServer { user, password } = &info.auth_mode else {
        return Ok(None);
    };
    let user = user_env.unwrap_or_else(|| user.into());
    let password = password_env.or_else(|| password.as_ref().map(Into::into));
    match password {
        Some(password) if !user.to_string_lossy().trim().is_empty() && !password.is_empty() => {
            Ok(Some((user, password)))
        }
        _ => Err("SQL authentication requires a non-empty user and password. Set SQLCMDUSER and SQLCMDPASSWORD in the gx-engine/Gateway process environment; the GeneXus private credential store is not read. sqlcmd was not started.".into()),
    }
}

/// Windows rejects a command line longer than this many UTF-16 units (CreateProcess).
const MAX_COMMAND_LINE_CHARS: usize = 32_767;

/// The statement cannot travel as `sqlcmd -Q <sql>` because the resulting command line
/// would exceed what the OS accepts. Refused before spawning so the caller sees the size
/// instead of an opaque OS error.
#[derive(Debug, PartialEq, Eq)]
pub struct CommandLineTooLong {
    pub chars: usize,
    pub limit: usize,
}

impl std::fmt::Display for CommandLineTooLong {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "SQL statement is too large for sqlcmd -Q: the command line would be about {} characters but the operating system accepts at most {}. sqlcmd was not started; reduce the amount of data written in one statement.",
            self.chars, self.limit
        )
    }
}

/// Upper-bound estimate of the Windows command line: program and arguments, each quoted
/// (two quotes plus a separator) with every embedded quote or backslash escaped.
fn check_command_line(cmd: &std::process::Command) -> Result<(), CommandLineTooLong> {
    let weigh = |s: &std::ffi::OsStr| {
        let s = s.to_string_lossy();
        s.chars().map(char::len_utf16).sum::<usize>()
            + s.chars().filter(|c| matches!(c, '"' | '\\')).count()
            + 3
    };
    let chars = weigh(cmd.get_program()) + cmd.get_args().map(weigh).sum::<usize>();
    if chars > MAX_COMMAND_LINE_CHARS {
        return Err(CommandLineTooLong {
            chars,
            limit: MAX_COMMAND_LINE_CHARS,
        });
    }
    Ok(())
}

/// Executes raw SQL against a live SQL Server or LocalDB instance using `sqlcmd`.
pub fn execute_sqlcmd(info: &KbConnectionInfo, sql: &str) -> Result<String, String> {
    let mut command = sqlcmd_command(info)?;
    command.arg("-Q").arg(sql);
    check_command_line(&command).map_err(|error| error.to_string())?;
    let output = command
        .output()
        .map_err(|e| format!("Failed to invoke sqlcmd: {e}"))?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr);
        let stdout = String::from_utf8_lossy(&output.stdout);
        return Err(format!(
            "sqlcmd execution failed with exit code {:?}: {stderr}\n{stdout}",
            output.status.code()
        ));
    }

    let result = String::from_utf8_lossy(&output.stdout).to_string();
    Ok(result)
}

/// Selects the transport of generated reads: `sqlcmd` (the default) or `tds`.
pub const SQL_TRANSPORT_ENV: &str = "GXMCP_ENGINE_SQL_TRANSPORT";

/// A way to reach the database.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TransportKind {
    Sqlcmd,
    Tds,
}

impl TransportKind {
    pub fn label(self) -> &'static str {
        match self {
            Self::Sqlcmd => "sqlcmd",
            Self::Tds => "tds",
        }
    }
}

/// What the generated reads of one connection asked for and what serves them.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct TransportReport {
    pub requested: &'static str,
    /// `None` until the connection's first session is opened.
    pub used: Option<&'static str>,
    /// Why `sqlcmd` serves reads that asked for `tds`.
    pub fallback_reason: Option<String>,
    /// What the TDS sessions asked of the server: `tls`, `tls-unverified-certificate` or `none`.
    pub encryption: Option<&'static str>,
}

#[derive(Debug, Clone)]
enum Decision {
    Tds { encryption: &'static str },
    Sqlcmd { fallback_reason: Option<String> },
}

/// The transport of one connection's generated reads, decided by its first session. When `tds`
/// is requested and that session cannot connect or log in, nothing has been read yet: the
/// connection reads through `sqlcmd` from then on and the reason is kept. Once a TDS session
/// has been opened no later failure changes transport, so the rows of one read never come from
/// two transports and a failed read is an error.
#[derive(Debug, Clone)]
pub struct SqlTransport {
    state: Arc<TransportState>,
}

#[derive(Debug)]
struct TransportState {
    requested: TransportKind,
    options: tds::ConnectOptions,
    decision: Mutex<Option<Decision>>,
}

/// Reads the transport variable. Only `tds` leaves `sqlcmd`; a value that is neither is
/// reported instead of guessed.
fn requested_transport(value: Option<&str>) -> (TransportKind, Option<String>) {
    match value.map(str::trim) {
        None | Some("") => (TransportKind::Sqlcmd, None),
        Some(value) if value.eq_ignore_ascii_case("sqlcmd") => (TransportKind::Sqlcmd, None),
        Some(value) if value.eq_ignore_ascii_case("tds") => (TransportKind::Tds, None),
        Some(value) => (
            TransportKind::Sqlcmd,
            Some(format!(
                "{SQL_TRANSPORT_ENV}='{value}' names no transport (sqlcmd or tds)"
            )),
        ),
    }
}

fn registry() -> &'static Mutex<Vec<(String, SqlTransport)>> {
    static REGISTRY: OnceLock<Mutex<Vec<(String, SqlTransport)>>> = OnceLock::new();
    REGISTRY.get_or_init(Default::default)
}

fn connection_key(info: &KbConnectionInfo) -> String {
    format!("{}\n{}", info.server_instance, info.db_name)
}

impl SqlTransport {
    fn new(
        requested: TransportKind,
        options: tds::ConnectOptions,
        refused: Option<String>,
    ) -> Self {
        let decision = refused.map(|reason| Decision::Sqlcmd {
            fallback_reason: Some(reason),
        });
        Self {
            state: Arc::new(TransportState {
                requested,
                options,
                decision: Mutex::new(decision),
            }),
        }
    }

    /// The transport of `info`'s reads in this process, read from the environment on first use.
    /// Every client of the same database shares it, so they cannot decide differently.
    pub fn for_connection(info: &KbConnectionInfo) -> Self {
        let key = connection_key(info);
        let mut registry = registry()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if let Some((_, transport)) = registry.iter().find(|(known, _)| *known == key) {
            return transport.clone();
        }
        let (requested, refused) =
            requested_transport(std::env::var(SQL_TRANSPORT_ENV).ok().as_deref());
        let transport = Self::new(requested, tds::ConnectOptions::from_environment(), refused);
        registry.push((key, transport.clone()));
        transport
    }

    /// Makes `info`'s reads use the TDS transport with `options`, whatever the environment says.
    #[cfg(all(test, feature = "tds"))]
    pub(crate) fn request_tds_for_test(info: &KbConnectionInfo, options: tds::ConnectOptions) {
        let transport = Self::new(TransportKind::Tds, options, None);
        registry()
            .lock()
            .unwrap()
            .push((connection_key(info), transport));
    }

    pub fn report(&self) -> TransportReport {
        let decision = self
            .state
            .decision
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clone();
        let (used, fallback_reason, encryption) = match decision {
            None => (None, None, None),
            Some(Decision::Tds { encryption }) => {
                (Some(TransportKind::Tds), None, Some(encryption))
            }
            Some(Decision::Sqlcmd { fallback_reason }) => {
                (Some(TransportKind::Sqlcmd), fallback_reason, None)
            }
        };
        TransportReport {
            requested: self.state.requested.label(),
            used: used.map(TransportKind::label),
            fallback_reason,
            encryption,
        }
    }

    /// Opens one session on the decided transport, deciding it when this is the first.
    /// `connect_tds` connects and logs in without reading anything; `start_sqlcmd` is the
    /// `sqlcmd` session of the same kind.
    fn open<S>(
        &self,
        connect_tds: impl FnOnce(&tds::ConnectOptions) -> Result<(S, tds::Encryption), String>,
        start_sqlcmd: impl FnOnce() -> Result<S, String>,
    ) -> Result<S, String> {
        // Held through the first connection: sessions opened in parallel wait for one decision.
        let mut decision = self
            .state
            .decision
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        match decision.clone() {
            Some(Decision::Sqlcmd { .. }) => {
                drop(decision);
                start_sqlcmd()
            }
            Some(Decision::Tds { .. }) => {
                drop(decision);
                connect_tds(&self.state.options)
                    .map(|(session, _)| session)
                    .map_err(|error| {
                        format!(
                            "{error}. This connection already reads over TDS, so sqlcmd does not take over; set {SQL_TRANSPORT_ENV}=sqlcmd to read through sqlcmd"
                        )
                    })
            }
            None if self.state.requested == TransportKind::Sqlcmd => {
                *decision = Some(Decision::Sqlcmd {
                    fallback_reason: None,
                });
                drop(decision);
                start_sqlcmd()
            }
            None => match connect_tds(&self.state.options) {
                Ok((session, encryption)) => {
                    *decision = Some(Decision::Tds {
                        encryption: encryption.label(),
                    });
                    Ok(session)
                }
                Err(reason) => {
                    *decision = Some(Decision::Sqlcmd {
                        fallback_reason: Some(reason.clone()),
                    });
                    drop(decision);
                    // Without sqlcmd on the machine this is the only place the cause is visible.
                    start_sqlcmd().map_err(|error| {
                        format!("{error} (the TDS transport was tried first and was unavailable: {reason})")
                    })
                }
            },
        }
    }
}

/// What `info`'s generated reads asked for and what serves them in this process.
pub fn transport_report(info: &KbConnectionInfo) -> TransportReport {
    SqlTransport::for_connection(info).report()
}

/// Why each connection that could not use the requested transport reads through `sqlcmd`.
pub fn transport_fallbacks() -> Vec<String> {
    registry()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .iter()
        .filter_map(|(_, transport)| transport.report().fallback_reason)
        .collect()
}

#[cfg(feature = "tds")]
type TdsHandle = Box<tds::TdsSession>;
/// Without the `tds` feature no TDS session exists: the variant that would hold one is empty.
#[cfg(not(feature = "tds"))]
type TdsHandle = std::convert::Infallible;

#[cfg(feature = "tds")]
fn connect_tds(
    info: &KbConnectionInfo,
    options: &tds::ConnectOptions,
) -> Result<(TdsHandle, tds::Encryption), String> {
    let credentials = sql_credentials(
        info,
        std::env::var_os("SQLCMDUSER"),
        std::env::var_os("SQLCMDPASSWORD"),
    )?;
    let credentials = match &credentials {
        None => None,
        Some((user, password)) => Some(
            user.to_str()
                .zip(password.to_str())
                .ok_or("the SQL login or password is not valid Unicode")?,
        ),
    };
    let session = tds::TdsSession::connect(info, credentials, options)?;
    let encryption = session.encryption();
    Ok((Box::new(session), encryption))
}

#[cfg(not(feature = "tds"))]
fn connect_tds(
    _: &KbConnectionInfo,
    _: &tds::ConnectOptions,
) -> Result<(TdsHandle, tds::Encryption), String> {
    Err(
        "this gx-engine build has no TDS transport (it was built without the `tds` cargo feature)"
            .into(),
    )
}

#[cfg(feature = "tds")]
fn tds_rows(session: &mut TdsHandle, sql: &str) -> Result<Vec<Vec<Value>>, String> {
    session.query_rows(sql)
}

#[cfg(not(feature = "tds"))]
fn tds_rows(session: &mut TdsHandle, _: &str) -> Result<Vec<Vec<Value>>, String> {
    match *session {}
}

/// One connection for a reader's generated SELECT batches, never shared across Knowledge Bases.
/// Writes retain their one-shot `sqlcmd` execution.
#[derive(Debug)]
pub struct ReadSession {
    kind: SessionKind,
    /// A session kept across statements refuses anything but one generated SELECT per batch.
    kept: bool,
}

#[derive(Debug)]
enum SessionKind {
    /// A `sqlcmd` process fed batches on its standard input.
    Sqlcmd(SqlcmdSession),
    /// One `sqlcmd -Q` process per statement, for a client that keeps no session.
    SqlcmdPerStatement(KbConnectionInfo),
    Tds(TdsHandle),
}

impl ReadSession {
    pub fn start_consistent(info: &KbConnectionInfo) -> Result<Self, String> {
        let mut session = Self::start(info)?;
        let state = session.run(CONSISTENT_READ_START)?;
        if state.trim() != "1" {
            return Err(format!(
                "{} did not establish the consistent read transaction",
                session.transport().label()
            ));
        }
        Ok(session)
    }

    pub fn start(info: &KbConnectionInfo) -> Result<Self, String> {
        let kept = true;
        SqlTransport::for_connection(info).open(
            |options| {
                let (session, encryption) = connect_tds(info, options)?;
                let kind = SessionKind::Tds(session);
                Ok((Self { kind, kept }, encryption))
            },
            || {
                let kind = SessionKind::Sqlcmd(SqlcmdSession::start(info)?);
                Ok(Self { kind, kept })
            },
        )
    }

    /// The session of one statement, for a client that keeps none: `sqlcmd -Q` as before, or a
    /// TDS connection that ends with the statement.
    pub fn per_statement(info: &KbConnectionInfo) -> Result<Self, String> {
        let kept = false;
        SqlTransport::for_connection(info).open(
            |options| {
                let (session, encryption) = connect_tds(info, options)?;
                let kind = SessionKind::Tds(session);
                Ok((Self { kind, kept }, encryption))
            },
            || {
                let kind = SessionKind::SqlcmdPerStatement(info.clone());
                Ok(Self { kind, kept })
            },
        )
    }

    pub fn transport(&self) -> TransportKind {
        match self.kind {
            SessionKind::Sqlcmd(_) | SessionKind::SqlcmdPerStatement(_) => TransportKind::Sqlcmd,
            SessionKind::Tds(_) => TransportKind::Tds,
        }
    }

    /// Whether part payloads are read as bytes with [`Self::query_rows`] instead of as hex text.
    pub fn binary_payloads(&self) -> bool {
        self.transport() == TransportKind::Tds
    }

    /// Runs one generated read and returns its rows as `sqlcmd -y 0 -s <tab>` prints them.
    pub fn query(&mut self, sql: &str) -> Result<String, String> {
        self.check(sql)?;
        self.run(sql)
    }

    /// Runs one generated read and returns its rows typed, so a `varbinary` column arrives as
    /// bytes. Only a session with [`Self::binary_payloads`] serves this.
    pub fn query_rows(&mut self, sql: &str) -> Result<Vec<Vec<Value>>, String> {
        self.check(sql)?;
        match &mut self.kind {
            SessionKind::Tds(session) => tds_rows(session, sql),
            _ => Err("typed rows are read over the TDS transport only".into()),
        }
    }

    fn check(&self, sql: &str) -> Result<(), String> {
        if self.kept {
            // Validate before sending; only the private startup path accepts transaction control.
            read_batch(sql, "validate")?;
        }
        Ok(())
    }

    fn run(&mut self, sql: &str) -> Result<String, String> {
        match &mut self.kind {
            SessionKind::Sqlcmd(session) => session.exchange(sql),
            SessionKind::SqlcmdPerStatement(info) => execute_sqlcmd(info, sql),
            SessionKind::Tds(session) => tds::render_text(&tds_rows(session, sql)?),
        }
    }
}

/// One sqlcmd connection: a process that reads batches until it is dropped.
#[derive(Debug)]
struct SqlcmdSession {
    child: std::process::Child,
    input: std::process::ChildStdin,
    output: std::io::BufReader<std::process::ChildStdout>,
    stderr: Option<std::thread::JoinHandle<Vec<u8>>>,
}

fn read_batch(sql: &str, marker: &str) -> Result<String, String> {
    if !sql.trim_start().starts_with("SET NOCOUNT ON; SELECT ") || sql.contains(['\r', '\n']) {
        return Err("persistent reads require a generated SELECT without sqlcmd directives".into());
    }
    Ok(format!("{sql}\nGO\nPRINT '{marker}';\nGO\n"))
}

// No database settings are changed. Range locks keep source, identity and timestamps
// coherent even when snapshot isolation is unavailable; lock waits are bounded.
const CONSISTENT_READ_START: &str = "SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000; SET DEADLOCK_PRIORITY LOW; SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRANSACTION; SELECT @@TRANCOUNT;";

impl SqlcmdSession {
    fn start(info: &KbConnectionInfo) -> Result<Self, String> {
        use std::io::Read;
        use std::process::Stdio;
        let mut command = sqlcmd_command(info)?;
        let mut child = command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|error| format!("cannot start sqlcmd reader: {error}"))?;
        let input = child.stdin.take().expect("piped stdin");
        let output =
            std::io::BufReader::with_capacity(1 << 20, child.stdout.take().expect("piped stdout"));
        let mut pipe = child.stderr.take().expect("piped stderr");
        let stderr = std::thread::spawn(move || {
            let mut kept = Vec::new();
            let _ = pipe.by_ref().take(32768).read_to_end(&mut kept);
            let _ = std::io::copy(&mut pipe, &mut std::io::sink());
            kept
        });
        Ok(Self {
            child,
            input,
            output,
            stderr: Some(stderr),
        })
    }

    fn exchange(&mut self, sql: &str) -> Result<String, String> {
        use std::io::{BufRead, Write};
        use std::sync::atomic::{AtomicU64, Ordering};
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let marker = format!(
            "__gxe_read_{}_{}_{}__",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_err(|error| error.to_string())?
                .as_nanos(),
        );
        let batch = format!("{sql}\nGO\nPRINT '{marker}';\nGO\n");
        self.input
            .write_all(batch.as_bytes())
            .and_then(|_| self.input.flush())
            .map_err(|error| format!("cannot send sqlcmd read: {error}"))?;
        let mut bytes = Vec::with_capacity(65536);
        let mut line = Vec::with_capacity(1024);
        loop {
            line.clear();
            if self
                .output
                .read_until(b'\n', &mut line)
                .map_err(|error| format!("cannot read sqlcmd response: {error}"))?
                == 0
            {
                let status = self.child.wait().map_err(|error| error.to_string())?;
                let detail = self
                    .stderr
                    .take()
                    .and_then(|task| task.join().ok())
                    .unwrap_or_default();
                return Err(format!(
                    "sqlcmd read ended before its completion marker ({status}): {}{}",
                    String::from_utf8_lossy(&bytes),
                    String::from_utf8_lossy(&detail)
                ));
            }
            let mut content = line.as_slice();
            while let [rest @ .., b'\r' | b'\n'] = content {
                content = rest;
            }
            if content == marker.as_bytes() {
                return Ok(String::from_utf8(bytes).unwrap_or_else(|error| {
                    String::from_utf8_lossy(error.as_bytes()).into_owned()
                }));
            }
            bytes.extend_from_slice(&line);
        }
    }
}

impl Drop for SqlcmdSession {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(task) = self.stderr.take() {
            let _ = task.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sql_password_is_passed_by_environment_not_argv() {
        let info = KbConnectionInfo {
            server_instance: "srv".into(),
            db_name: "db".into(),
            auth_mode: AuthMode::SqlServer {
                user: "sa".into(),
                password: Some("s3cret-pw".into()),
            },
            create_db_in_kb_folder: None,
            directory: None,
            data_file: None,
            log_file: None,
            host_name: None,
            detach_on_close: None,
        };
        let command = sqlcmd_command_with_credentials(&info, None, None).unwrap();
        let args: Vec<_> = command
            .get_args()
            .map(|a| a.to_string_lossy().into_owned())
            .collect();
        assert!(args.iter().all(|a| a != "-P" && !a.contains("s3cret-pw")));
        assert!(!args.contains(&"-U".to_string()));
        let user = command
            .get_envs()
            .find(|(key, _)| *key == "SQLCMDUSER")
            .and_then(|(_, value)| value);
        assert_eq!(user, Some(std::ffi::OsStr::new("sa")));
        let password = command
            .get_envs()
            .find(|(k, _)| *k == "SQLCMDPASSWORD")
            .and_then(|(_, v)| v);
        assert_eq!(password, Some(std::ffi::OsStr::new("s3cret-pw")));
    }

    fn sql_info() -> KbConnectionInfo {
        KbConnectionInfo::parse_xml("<ConnectionInformation><ServerInstance>test</ServerInstance><DBName>test</DBName><IntegratedSecurity>false</IntegratedSecurity></ConnectionInformation>").unwrap()
    }

    #[test]
    fn missing_credentials_are_rejected_without_a_command_or_secret_in_errors() {
        for (user, password) in [
            (None, None),
            (Some("login-only"), None),
            (None, Some("secret-only")),
            (Some(""), Some("secret-only")),
            (Some("login-only"), Some("")),
            (Some("  "), Some("secret-only")),
        ] {
            let error = sqlcmd_command_with_credentials(
                &sql_info(),
                user.map(Into::into),
                password.map(Into::into),
            )
            .unwrap_err();
            assert!(error.contains("SQLCMDUSER") && error.contains("SQLCMDPASSWORD"));
            assert!(!error.contains("login-only") && !error.contains("secret-only"));
        }
    }

    #[test]
    fn sql_credentials_from_environment_override_file_without_argv_exposure() {
        let mut info = sql_info();
        info.auth_mode = AuthMode::SqlServer {
            user: "old-user".into(),
            password: Some("old-password".into()),
        };
        let command = sqlcmd_command_with_credentials(
            &info,
            Some("env-user".into()),
            Some("env-password".into()),
        )
        .unwrap();
        let args: Vec<_> = command.get_args().collect();
        for secret in ["old-user", "old-password", "env-user", "env-password"] {
            assert!(
                !args
                    .iter()
                    .any(|value| value.to_string_lossy().contains(secret))
            );
        }
        let vars: std::collections::BTreeMap<_, _> = command.get_envs().collect();
        assert_eq!(
            vars[std::ffi::OsStr::new("SQLCMDUSER")],
            Some(std::ffi::OsStr::new("env-user"))
        );
        assert_eq!(
            vars[std::ffi::OsStr::new("SQLCMDPASSWORD")],
            Some(std::ffi::OsStr::new("env-password"))
        );
    }

    #[test]
    fn explicit_empty_environment_credentials_do_not_fall_back_to_file_secrets() {
        let mut info = sql_info();
        info.auth_mode = AuthMode::SqlServer {
            user: "file-user".into(),
            password: Some("file-password".into()),
        };
        for (user, password) in [(Some("".into()), None), (None, Some("".into()))] {
            assert!(sqlcmd_command_with_credentials(&info, user, password).is_err());
        }
    }

    #[test]
    fn environment_user_can_use_a_file_password() {
        let mut info = sql_info();
        info.auth_mode = AuthMode::SqlServer {
            user: "file-user".into(),
            password: Some("file-password".into()),
        };
        let command =
            sqlcmd_command_with_credentials(&info, Some("env-user".into()), None).unwrap();
        let vars: std::collections::BTreeMap<_, _> = command.get_envs().collect();
        assert_eq!(
            vars[std::ffi::OsStr::new("SQLCMDUSER")],
            Some(std::ffi::OsStr::new("env-user"))
        );
        assert_eq!(
            vars[std::ffi::OsStr::new("SQLCMDPASSWORD")],
            Some(std::ffi::OsStr::new("file-password"))
        );
    }

    #[test]
    fn integrated_auth_clears_inherited_sql_credentials() {
        let mut info = sql_info();
        info.auth_mode = AuthMode::Integrated;
        let command =
            sqlcmd_command_with_credentials(&info, Some("ignored".into()), Some("ignored".into()))
                .unwrap();
        assert!(command.get_args().any(|arg| arg == "-E"));
        let vars: std::collections::BTreeMap<_, _> = command.get_envs().collect();
        assert_eq!(vars[std::ffi::OsStr::new("SQLCMDUSER")], None);
        assert_eq!(vars[std::ffi::OsStr::new("SQLCMDPASSWORD")], None);
    }

    #[test]
    fn oversized_statement_is_refused_before_spawning_sqlcmd() {
        let command = || {
            sqlcmd_command_with_credentials(&sql_info(), Some("u".into()), Some("p".into()))
                .unwrap()
        };
        let mut small = command();
        small.arg("-Q").arg("SELECT 1");
        assert_eq!(check_command_line(&small), Ok(()));

        let mut large = command();
        large.arg("-Q").arg("A".repeat(32_767));
        let error = check_command_line(&large).unwrap_err();
        assert!(error.chars > MAX_COMMAND_LINE_CHARS);
        let message = error.to_string();
        assert!(message.contains(&error.chars.to_string()) && message.contains("32767"));
    }

    fn transport(requested: TransportKind) -> SqlTransport {
        SqlTransport::new(requested, tds::ConnectOptions::default(), None)
    }

    type Opened = Result<(&'static str, tds::Encryption), String>;

    #[test]
    fn the_transport_variable_selects_tds_only_by_name() {
        for value in [None, Some(""), Some("  "), Some("sqlcmd"), Some("SQLCMD")] {
            assert_eq!(
                requested_transport(value),
                (TransportKind::Sqlcmd, None),
                "{value:?}"
            );
        }
        for value in ["tds", " TDS "] {
            assert_eq!(requested_transport(Some(value)), (TransportKind::Tds, None));
        }
        // A value that names nothing is not guessed: sqlcmd reads, and the report says why.
        let (kind, refused) = requested_transport(Some("tedious"));
        assert_eq!(kind, TransportKind::Sqlcmd);
        assert!(
            refused
                .unwrap()
                .contains("GXMCP_ENGINE_SQL_TRANSPORT='tedious'")
        );
        let unrecognized =
            SqlTransport::new(kind, tds::ConnectOptions::default(), Some("why".into()));
        let opened = unrecognized.open(
            |_| -> Opened { panic!("TDS was not requested") },
            || Ok("sqlcmd"),
        );
        assert_eq!(opened, Ok("sqlcmd"));
        assert_eq!(
            unrecognized.report().fallback_reason.as_deref(),
            Some("why")
        );
    }

    #[test]
    fn sqlcmd_serves_every_session_unless_tds_is_requested() {
        let transport = transport(TransportKind::Sqlcmd);
        assert_eq!(transport.report().used, None);
        for _ in 0..2 {
            let opened = transport.open(
                |_| -> Opened { panic!("TDS was not requested") },
                || Ok("sqlcmd"),
            );
            assert_eq!(opened, Ok("sqlcmd"));
        }
        assert_eq!(
            transport.report(),
            TransportReport {
                requested: "sqlcmd",
                used: Some("sqlcmd"),
                fallback_reason: None,
                encryption: None,
            }
        );
    }

    #[test]
    fn a_tds_connection_failure_before_any_read_falls_back_to_sqlcmd_and_keeps_the_reason() {
        let transport = transport(TransportKind::Tds);
        let mut tds_attempts = 0;
        for _ in 0..3 {
            let opened = transport.open(
                |_| -> Opened {
                    tds_attempts += 1;
                    Err("login failed".into())
                },
                || Ok("sqlcmd"),
            );
            assert_eq!(opened, Ok("sqlcmd"));
        }
        // The decision is made once: later sessions of the connection do not try TDS again,
        // so one read never has some sessions on each transport.
        assert_eq!(tds_attempts, 1);
        assert_eq!(
            transport.report(),
            TransportReport {
                requested: "tds",
                used: Some("sqlcmd"),
                fallback_reason: Some("login failed".into()),
                encryption: None,
            }
        );
    }

    #[test]
    fn once_tds_served_a_session_a_later_failure_is_an_error_and_sqlcmd_is_not_started() {
        let transport = transport(TransportKind::Tds);
        let opened = transport.open(
            |_| Ok(("tds", tds::Encryption::TlsVerified)),
            || panic!("TDS connected"),
        );
        assert_eq!(opened, Ok("tds"));
        // Mutation check: a fallback here would hand a read rows from both transports.
        let mut sqlcmd_started = false;
        let failure = transport
            .open(
                |_| -> Opened { Err("connection reset".into()) },
                || {
                    sqlcmd_started = true;
                    Ok("sqlcmd")
                },
            )
            .unwrap_err();
        assert!(!sqlcmd_started);
        assert!(
            failure.contains("connection reset") && failure.contains("already reads over TDS"),
            "{failure}"
        );
        let report = transport.report();
        assert_eq!((report.used, report.fallback_reason), (Some("tds"), None));
        assert_eq!(report.encryption, Some("tls"));
    }

    #[test]
    fn a_sqlcmd_that_cannot_start_after_the_fallback_names_why_tds_was_not_used() {
        let transport = transport(TransportKind::Tds);
        let failure = transport
            .open(
                |_| -> Opened { Err("no TLS on the server".into()) },
                || Err("cannot start sqlcmd reader: program not found".into()),
            )
            .unwrap_err();
        assert!(
            failure.starts_with("cannot start sqlcmd reader: program not found")
                && failure.contains("no TLS on the server"),
            "{failure}"
        );
    }

    #[cfg(not(feature = "tds"))]
    #[test]
    fn a_build_without_the_tds_feature_falls_back_and_says_so() {
        let reason = connect_tds(&sql_info(), &tds::ConnectOptions::default()).unwrap_err();
        assert!(reason.contains("`tds` cargo feature"), "{reason}");
    }

    #[test]
    fn persistent_select_batches_reject_sqlcmd_directives() {
        let batch = read_batch("SET NOCOUNT ON; SELECT 42;", "finished").unwrap();
        assert_eq!(
            batch,
            "SET NOCOUNT ON; SELECT 42;\nGO\nPRINT 'finished';\nGO\n"
        );
        for sql in [
            "DELETE FROM EntityVersion",
            "SET NOCOUNT ON; SELECT 1;\n:r file",
            "SET NOCOUNT ON; SELECT 1;\nGO\nSELECT 2",
            "SET NOCOUNT ON; SELECT 1;\n!!command",
        ] {
            assert!(read_batch(sql, "finished").is_err(), "{sql}");
        }
    }

    #[test]
    #[ignore = "explicit local-only SQL integration against disposable KBTeste"]
    fn live_read_session_reuses_connection_and_fails_closed() {
        let info = KbConnectionInfo::from_kb_dir("C:/KBs/KBTeste").unwrap();
        let mut session = ReadSession::start(&info).unwrap();
        fn child(session: &mut ReadSession) -> &mut std::process::Child {
            match &mut session.kind {
                SessionKind::Sqlcmd(sqlcmd) => &mut sqlcmd.child,
                _ => panic!("this test reads through a kept sqlcmd session"),
            }
        }
        let pid = child(&mut session).id();
        for value in [42, 73] {
            let output = session
                .query(&format!("SET NOCOUNT ON; SELECT {value};"))
                .unwrap();
            assert_eq!(output.trim(), value.to_string());
            assert_eq!(child(&mut session).id(), pid);
        }
        for sql in [
            "SET NOCOUNT ON; SELECT REPLICATE('a', 4096), NULL, 73;",
            "SET NOCOUNT ON; SELECT NCHAR(233), 42;",
            "SET NOCOUNT ON; SELECT 1 WHERE 1 = 0;",
        ] {
            assert_eq!(
                session.query(sql).unwrap(),
                execute_sqlcmd(&info, sql).unwrap()
            );
        }
        assert!(
            session
                .query("SET NOCOUNT ON; SELECT * FROM __GXE_Absent_Session_Test_Table;")
                .is_err()
        );
        assert!(
            child(&mut session).try_wait().unwrap().is_some(),
            "failed query process must be reaped"
        );
    }
}
