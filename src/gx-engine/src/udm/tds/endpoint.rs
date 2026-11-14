//! Where a `knowledgebase.connection` server name leads: a TCP address, a LocalDB instance or a named pipe.

use std::process::{Command, Stdio};

/// The address a `ServerInstance` value names, in the `sqlcmd -S` syntax GeneXus stores.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Endpoint {
    /// `host`, `host,port` or `host\instance`. A named instance without a port is resolved
    /// through the SQL Server Browser of `host`.
    Tcp {
        host: String,
        port: Option<u16>,
        instance: Option<String>,
    },
    /// `(LocalDB)\name`: a per-user instance reached through a pipe whose name changes at each start.
    LocalDb { instance: String },
    /// `np:\\host\pipe\...`: an explicit named pipe.
    Pipe { path: String },
}

const LOCALDB_PREFIX: &str = "(localdb)\\";
const LOCAL_PIPE_PREFIX: &str = "\\\\.\\pipe\\";

impl Endpoint {
    /// Parses a `ServerInstance` value. A form this transport cannot reach is an error naming it,
    /// so the caller keeps `sqlcmd` for it.
    pub fn parse(server_instance: &str) -> Result<Self, String> {
        let server = server_instance.trim();
        let lower = server.to_ascii_lowercase();
        if let Some(rest) = strip_prefix_ignore_case(server, "np:") {
            if !rest.starts_with("\\\\") {
                return Err("a named-pipe server name must be np:\\\\host\\pipe\\name".into());
            }
            return Ok(Self::Pipe { path: rest.into() });
        }
        for prefix in ["lpc:", "admin:"] {
            if lower.starts_with(prefix) {
                return Err(format!(
                    "the {prefix} protocol is not reachable over TDS (shared memory has no public protocol)"
                ));
            }
        }
        let server = strip_prefix_ignore_case(server, "tcp:").unwrap_or(server);
        if let Some(instance) = strip_prefix_ignore_case(server, LOCALDB_PREFIX) {
            if instance.is_empty() {
                return Err("the LocalDB server name has no instance".into());
            }
            return Ok(Self::LocalDb {
                instance: instance.into(),
            });
        }
        let (server, port) = match server.rsplit_once(',') {
            Some((server, port)) => (
                server,
                Some(
                    port.trim()
                        .parse::<u16>()
                        .map_err(|_| "the server name has a port that is not a number")?,
                ),
            ),
            None => (server, None),
        };
        let (host, instance) = match server.split_once('\\') {
            Some((host, instance)) => (host, Some(instance.trim())),
            None => (server, None),
        };
        let host = match host.trim() {
            "" | "." => "localhost",
            host if host.eq_ignore_ascii_case("(local)") => "localhost",
            host => host,
        };
        if instance.is_some_and(str::is_empty) {
            return Err("the server name has an empty instance".into());
        }
        Ok(Self::Tcp {
            host: host.into(),
            port,
            // An explicit port already identifies the instance, as it does for sqlcmd.
            instance: instance.filter(|_| port.is_none()).map(Into::into),
        })
    }

    /// Whether the endpoint is a pipe of this machine: traffic never leaves it.
    pub fn is_local_pipe(&self) -> bool {
        match self {
            Self::LocalDb { .. } => true,
            Self::Pipe { path } => strip_prefix_ignore_case(path, LOCAL_PIPE_PREFIX).is_some(),
            Self::Tcp { .. } => false,
        }
    }
}

fn strip_prefix_ignore_case<'a>(text: &'a str, prefix: &str) -> Option<&'a str> {
    let head = text.get(..prefix.len())?;
    head.eq_ignore_ascii_case(prefix)
        .then(|| &text[prefix.len()..])
}

/// The server part of `\\server\pipe\name`, or `None` for a pipe of this machine.
pub fn pipe_host(path: &str) -> Option<&str> {
    let host = path.strip_prefix("\\\\")?.split('\\').next()?;
    (!host.is_empty() && host != ".").then_some(host)
}

/// The pipe of a running LocalDB instance, from what `sqllocaldb info <instance>` printed.
/// The labels of that output are localized; the `np:\\.\pipe\...` value is not, so it is found by itself.
pub fn pipe_from_info(output: &str) -> Option<String> {
    let lower = output.to_ascii_lowercase();
    let start = lower.find("np:\\\\.\\pipe\\")?;
    let pipe = output[start + "np:".len()..]
        .split(['\r', '\n'])
        .next()?
        .trim();
    (pipe.len() > LOCAL_PIPE_PREFIX.len()).then(|| pipe.to_string())
}

/// Finds the pipe of LocalDB `instance`, starting the instance when it exists but is stopped: connecting
/// to `(LocalDB)\name` starts it as well (sqlcmd and SqlClient both do). `run` runs `sqllocaldb` with
/// the given arguments and returns what it printed.
pub fn resolve_localdb_pipe(
    instance: &str,
    run: &mut dyn FnMut(&[&str]) -> Result<String, String>,
) -> Result<String, String> {
    if let Some(pipe) = pipe_from_info(&run(&["info", instance])?) {
        return Ok(pipe);
    }
    run(&["start", instance])?;
    pipe_from_info(&run(&["info", instance])?)
        .ok_or_else(|| format!("LocalDB instance '{instance}' reports no pipe name after start"))
}

/// Runs the `sqllocaldb` utility every LocalDB install ships. A nonzero exit is an error
/// carrying what the utility printed.
pub fn run_sqllocaldb(args: &[&str]) -> Result<String, String> {
    let output = match sqllocaldb_command(args, "sqllocaldb".as_ref()) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            let installed = installed_sqllocaldb()
                .ok_or("sqllocaldb was not found on PATH or under Program Files")?;
            sqllocaldb_command(args, installed.as_os_str())
        }
        other => other,
    }
    .map_err(|error| format!("cannot run sqllocaldb: {error}"))?;
    let text = decode_tool_output(&output.stdout);
    if !output.status.success() {
        return Err(format!(
            "sqllocaldb {} failed ({}): {}{}",
            args.first().copied().unwrap_or_default(),
            output.status,
            text.trim(),
            decode_tool_output(&output.stderr).trim()
        ));
    }
    Ok(text)
}

fn sqllocaldb_command(
    args: &[&str],
    program: &std::ffi::OsStr,
) -> std::io::Result<std::process::Output> {
    Command::new(program)
        .args(args)
        .stdin(Stdio::null())
        .output()
}

/// `SqlLocalDB.exe` of the newest SQL Server version under Program Files, for a PATH that lacks it.
fn installed_sqllocaldb() -> Option<std::path::PathBuf> {
    let root = std::path::Path::new(&std::env::var_os("ProgramFiles")?).join("Microsoft SQL Server");
    std::fs::read_dir(root)
        .ok()?
        .filter_map(Result::ok)
        .filter_map(|entry| {
            let version = entry.file_name().to_str()?.parse::<u32>().ok()?;
            let exe = entry.path().join("Tools").join("Binn").join("SqlLocalDB.exe");
            exe.is_file().then_some((version, exe))
        })
        .max_by_key(|(version, _)| *version)
        .map(|(_, exe)| exe)
}

/// Console tools print in the OEM code page, or in UTF-16 when their output is redirected.
/// Only ASCII is read from the result, so anything else is decoded lossily.
pub fn decode_tool_output(bytes: &[u8]) -> String {
    let (pairs, rest) = bytes.as_chunks::<2>();
    let utf16 = rest.is_empty()
        && !pairs.is_empty()
        && pairs.iter().filter(|pair| pair[1] == 0).count() * 2 > pairs.len();
    if utf16 {
        let units: Vec<u16> = pairs.iter().map(|&pair| u16::from_le_bytes(pair)).collect();
        String::from_utf16_lossy(&units)
    } else {
        String::from_utf8_lossy(bytes).into_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tcp(host: &str, port: Option<u16>, instance: Option<&str>) -> Endpoint {
        Endpoint::Tcp {
            host: host.into(),
            port,
            instance: instance.map(Into::into),
        }
    }

    #[test]
    fn server_names_parse_as_sqlcmd_reads_them() {
        for (server, expected) in [
            ("srv", tcp("srv", None, None)),
            ("tcp:srv,1444", tcp("srv", Some(1444), None)),
            ("192.168.1.100\\SQLEXPRESS", tcp("192.168.1.100", None, Some("SQLEXPRESS"))),
            // A port already identifies the instance: the Browser is not asked.
            ("srv\\INST, 1500", tcp("srv", Some(1500), None)),
            (".", tcp("localhost", None, None)),
            ("(local)\\SQLEXPRESS", tcp("localhost", None, Some("SQLEXPRESS"))),
            (
                "(LocalDB)\\MSSQLLocalDB",
                Endpoint::LocalDb {
                    instance: "MSSQLLocalDB".into(),
                },
            ),
            (
                "(localdb)\\.\\shared",
                Endpoint::LocalDb {
                    instance: ".\\shared".into(),
                },
            ),
            (
                "np:\\\\.\\pipe\\sql\\query",
                Endpoint::Pipe {
                    path: "\\\\.\\pipe\\sql\\query".into(),
                },
            ),
        ] {
            assert_eq!(Endpoint::parse(server), Ok(expected), "{server}");
        }
    }

    #[test]
    fn unreachable_server_names_are_refused_by_name() {
        for server in ["lpc:srv", "ADMIN:srv", "srv,port", "srv\\", "(localdb)\\", "np:srv"] {
            assert!(Endpoint::parse(server).is_err(), "{server}");
        }
    }

    #[test]
    fn only_a_pipe_of_this_machine_is_local() {
        // Mutation check: treating every pipe as local would let a remote pipe skip certificate validation.
        let local = |server: &str| Endpoint::parse(server).unwrap().is_local_pipe();
        assert!(local("(LocalDB)\\MSSQLLocalDB"));
        assert!(local("np:\\\\.\\PIPE\\sql\\query"));
        assert!(!local("np:\\\\dbhost\\pipe\\sql\\query"));
        assert!(!local("localhost"));
        assert_eq!(pipe_host("\\\\dbhost\\pipe\\sql\\query"), Some("dbhost"));
        assert_eq!(pipe_host("\\\\.\\pipe\\sql\\query"), None);
    }

    const RUNNING: &str = "Name:               MSSQLLocalDB\r\nVersion:            15.0.4153.1\r\nShared name:\r\nOwner:              HOST\\user\r\nAuto-create:        Yes\r\nState:              Running\r\nLast start time:    11/13/2026 10:00:00 AM\r\nInstance pipe name: np:\\\\.\\pipe\\LOCALDB#2D07B6E0\\tsql\\query\r\n";
    const STOPPED: &str = "Name:               MSSQLLocalDB\r\nState:              Stopped\r\nInstance pipe name:\r\n";

    #[test]
    fn the_pipe_is_read_from_info_output_whatever_its_labels_say() {
        assert_eq!(
            pipe_from_info(RUNNING).as_deref(),
            Some("\\\\.\\pipe\\LOCALDB#2D07B6E0\\tsql\\query")
        );
        // A localized label, and the value as the last line without a line break.
        assert_eq!(
            pipe_from_info("Nome do pipe da inst\u{e2}ncia: NP:\\\\.\\pipe\\LOCALDB#AB12\\tsql\\query")
                .as_deref(),
            Some("\\\\.\\pipe\\LOCALDB#AB12\\tsql\\query")
        );
        assert_eq!(pipe_from_info(STOPPED), None);
        assert_eq!(pipe_from_info("Instance pipe name: np:\\\\.\\pipe\\"), None);
    }

    #[test]
    fn a_stopped_instance_is_started_once_and_an_absent_one_is_an_error() {
        let mut calls = Vec::new();
        let mut started = false;
        let pipe = resolve_localdb_pipe("MSSQLLocalDB", &mut |args| {
            calls.push(args.join(" "));
            if args[0] == "start" {
                started = true;
            }
            Ok(if started { RUNNING } else { STOPPED }.to_string())
        })
        .unwrap();
        assert_eq!(pipe, "\\\\.\\pipe\\LOCALDB#2D07B6E0\\tsql\\query");
        assert_eq!(
            calls,
            ["info MSSQLLocalDB", "start MSSQLLocalDB", "info MSSQLLocalDB"]
        );

        // A running instance is neither started nor asked twice.
        let mut calls = 0;
        resolve_localdb_pipe("MSSQLLocalDB", &mut |_| {
            calls += 1;
            Ok(RUNNING.to_string())
        })
        .unwrap();
        assert_eq!(calls, 1);

        // An instance that does not exist: `info` fails, and nothing is started.
        let mut calls = Vec::new();
        let error = resolve_localdb_pipe("absent", &mut |args| {
            calls.push(args[0].to_string());
            Err("sqllocaldb info failed".to_string())
        })
        .unwrap_err();
        assert_eq!(calls, ["info"]);
        assert!(error.contains("info failed"), "{error}");

        // An instance that stays stopped is an error, never an empty pipe name.
        let error = resolve_localdb_pipe("stuck", &mut |_| Ok(STOPPED.to_string())).unwrap_err();
        assert!(error.contains("no pipe name"), "{error}");
    }

    #[test]
    fn tool_output_is_decoded_from_either_console_encoding() {
        let utf16: Vec<u8> = "np:\\\\.\\pipe\\X\r\n"
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect();
        assert_eq!(decode_tool_output(&utf16), "np:\\\\.\\pipe\\X\r\n");
        assert_eq!(decode_tool_output(b"np:\\\\.\\pipe\\X"), "np:\\\\.\\pipe\\X");
        assert_eq!(decode_tool_output(b""), "");
    }
}
