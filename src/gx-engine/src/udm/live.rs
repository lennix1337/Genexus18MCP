//! Live SQL Server / LocalDB subprocess driver for GeneXus physical storage.

#![forbid(unsafe_code)]

use crate::udm::connection::{AuthMode, KbConnectionInfo};

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

    match &info.auth_mode {
        AuthMode::Integrated => {
            cmd.arg("-E");
            // Inherited SQL-auth variables must not conflict with explicit Windows auth.
            cmd.env_remove("SQLCMDUSER").env_remove("SQLCMDPASSWORD");
        }
        AuthMode::SqlServer { user, password } => {
            let user = user_env.unwrap_or_else(|| user.into());
            let password = password_env.or_else(|| password.as_ref().map(Into::into));
            if user.to_string_lossy().trim().is_empty()
                || password.as_ref().is_none_or(|value| value.is_empty())
            {
                return Err("SQL authentication requires a non-empty user and password. Set SQLCMDUSER and SQLCMDPASSWORD in the gx-engine/Gateway process environment; the GeneXus private credential store is not read. sqlcmd was not started.".into());
            }
            // Never let sqlcmd prompt on stdin (which carries SELECT batches in ReadSession).
            // Credentials travel in the child's environment, never argv or error text.
            cmd.env("SQLCMDUSER", user);
            cmd.env("SQLCMDPASSWORD", password.expect("validated password"));
        }
    }

    Ok(cmd)
}

/// Executes raw SQL against a live SQL Server or LocalDB instance using `sqlcmd`.
pub fn execute_sqlcmd(info: &KbConnectionInfo, sql: &str) -> Result<String, String> {
    let output = sqlcmd_command(info)?
        .arg("-Q")
        .arg(sql)
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

/// One sqlcmd connection, scoped to a reader and never shared across Knowledge Bases.
/// Only generated SELECT batches use this path; writes retain their one-shot execution.
#[derive(Debug)]
pub struct ReadSession {
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

impl ReadSession {
    pub fn start_consistent(info: &KbConnectionInfo) -> Result<Self, String> {
        let mut session = Self::start(info)?;
        let state = session.exchange(CONSISTENT_READ_START)?;
        if state.trim() != "1" {
            return Err("sqlcmd did not establish the consistent read transaction".into());
        }
        Ok(session)
    }

    pub fn start(info: &KbConnectionInfo) -> Result<Self, String> {
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

    pub fn query(&mut self, sql: &str) -> Result<String, String> {
        // Validate before sending; only the private startup path accepts transaction control.
        read_batch(sql, "validate")?;
        self.exchange(sql)
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

impl Drop for ReadSession {
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
        let pid = session.child.id();
        for value in [42, 73] {
            let output = session
                .query(&format!("SET NOCOUNT ON; SELECT {value};"))
                .unwrap();
            assert_eq!(output.trim(), value.to_string());
            assert_eq!(session.child.id(), pid);
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
            session.child.try_wait().unwrap().is_some(),
            "failed query process must be reaped"
        );
    }
}
