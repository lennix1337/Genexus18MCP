//! One blocking TDS connection, built on the `tiberius` client and a current-thread runtime.

use super::endpoint::{self, Endpoint};
use super::{ConnectOptions, Encryption, Value, redact};
use crate::udm::connection::KbConnectionInfo;
use std::io;
use std::pin::Pin;
use std::task::{Context, Poll};
use std::time::Duration;
use tiberius::{AuthMethod, Client, ColumnData, ColumnType, Config, EncryptionLevel, SqlBrowser};
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio::net::TcpStream;
use tokio_util::compat::{Compat, TokioAsyncWriteCompatExt};

/// Bounds the connection, the TLS handshake and the login. Statements have no deadline, as
/// under `sqlcmd`: a catalog read is one long statement.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

/// The byte stream under the protocol: a socket, or a named pipe for LocalDB.
#[derive(Debug)]
enum Transport {
    Tcp(TcpStream),
    #[cfg(windows)]
    Pipe(tokio::net::windows::named_pipe::NamedPipeClient),
}

impl AsyncRead for Transport {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        match self.get_mut() {
            Self::Tcp(stream) => Pin::new(stream).poll_read(cx, buf),
            #[cfg(windows)]
            Self::Pipe(pipe) => Pin::new(pipe).poll_read(cx, buf),
        }
    }
}

impl AsyncWrite for Transport {
    fn poll_write(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        match self.get_mut() {
            Self::Tcp(stream) => Pin::new(stream).poll_write(cx, buf),
            #[cfg(windows)]
            Self::Pipe(pipe) => Pin::new(pipe).poll_write(cx, buf),
        }
    }

    fn poll_flush(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        match self.get_mut() {
            Self::Tcp(stream) => Pin::new(stream).poll_flush(cx),
            #[cfg(windows)]
            Self::Pipe(pipe) => Pin::new(pipe).poll_flush(cx),
        }
    }

    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        match self.get_mut() {
            Self::Tcp(stream) => Pin::new(stream).poll_shutdown(cx),
            #[cfg(windows)]
            Self::Pipe(pipe) => Pin::new(pipe).poll_shutdown(cx),
        }
    }
}

/// A logged-in connection to the Knowledge Base database.
pub struct TdsSession {
    runtime: tokio::runtime::Runtime,
    client: Client<Compat<Transport>>,
    encryption: Encryption,
    /// Set by the first failed statement: the token stream may be cut mid-row, so nothing else is read.
    failed: bool,
}

impl std::fmt::Debug for TdsSession {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TdsSession")
            .field("encryption", &self.encryption)
            .field("failed", &self.failed)
            .finish_non_exhaustive()
    }
}

impl TdsSession {
    /// Connects and logs in. `credentials` is the SQL login and password, or `None` for Windows
    /// integrated authentication. Nothing has been read when this fails, and the error never
    /// carries the login or the password.
    pub fn connect(
        info: &KbConnectionInfo,
        credentials: Option<(&str, &str)>,
        options: &ConnectOptions,
    ) -> Result<Self, String> {
        Self::connect_with(info, credentials, options, &mut |args| {
            endpoint::run_sqllocaldb(args)
        })
    }

    /// [`Self::connect`] with the `sqllocaldb` runner given, so a test names the pipe itself.
    pub(crate) fn connect_with(
        info: &KbConnectionInfo,
        credentials: Option<(&str, &str)>,
        options: &ConnectOptions,
        sqllocaldb: &mut dyn FnMut(&[&str]) -> Result<String, String>,
    ) -> Result<Self, String> {
        let endpoint = match Endpoint::parse(&info.server_instance)? {
            Endpoint::LocalDb { instance } => Endpoint::Pipe {
                path: endpoint::resolve_localdb_pipe(&instance, sqllocaldb)?,
            },
            other => other,
        };
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_io()
            .enable_time()
            .build()
            .map_err(|error| format!("cannot start the TDS runtime: {error}"))?;
        let secrets: Vec<&str> = credentials.iter().flat_map(|(u, p)| [*u, *p]).collect();
        let mut failure = String::new();
        for encryption in
            options.encryption_attempts(endpoint.is_local_pipe(), credentials.is_none())
        {
            match runtime.block_on(open(&endpoint, &info.db_name, credentials, encryption)) {
                Ok(client) => {
                    return Ok(Self {
                        runtime,
                        client,
                        encryption,
                        failed: false,
                    });
                }
                Err(error) => {
                    failure = format!(
                        "TDS connection (encryption {}) failed: {}",
                        encryption.label(),
                        redact(&error, &secrets)
                    );
                }
            }
        }
        Err(failure)
    }

    /// What the successful connection attempt asked of the server.
    pub fn encryption(&self) -> Encryption {
        self.encryption
    }

    /// Runs one batch and returns every row of every result set, in order. A server error, a
    /// column type outside [`Value`] or a connection that ends mid-stream fails the whole read
    /// and the session with it.
    pub fn query_rows(&mut self, sql: &str) -> Result<Vec<Vec<Value>>, String> {
        if self.failed {
            return Err("the TDS session ended with an earlier failure".into());
        }
        let result = self.read(sql);
        self.failed = result.is_err();
        result
    }

    fn read(&mut self, sql: &str) -> Result<Vec<Vec<Value>>, String> {
        let sets = self
            .runtime
            .block_on(async {
                let stream = self.client.simple_query(sql).await?;
                stream.into_results().await
            })
            .map_err(|error| format!("TDS read failed: {error}"))?;
        let mut rows = Vec::with_capacity(sets.iter().map(Vec::len).sum());
        for set in sets {
            if let Some(first) = set.first() {
                check_column_types(first.columns())?;
            }
            for row in set {
                rows.push(row.into_iter().map(value).collect::<Result<Vec<_>, _>>()?);
            }
        }
        Ok(rows)
    }
}

/// Only the types whose text form is the same under `sqlcmd` are served. A date, a decimal, a
/// float or a GUID would need `sqlcmd`'s own formatting, which no generated read relies on.
fn check_column_types(columns: &[tiberius::Column]) -> Result<(), String> {
    for (index, column) in columns.iter().enumerate() {
        if !matches!(
            column.column_type(),
            ColumnType::Null
                | ColumnType::Bit
                | ColumnType::Bitn
                | ColumnType::Int1
                | ColumnType::Int2
                | ColumnType::Int4
                | ColumnType::Int8
                | ColumnType::Intn
                | ColumnType::BigVarChar
                | ColumnType::BigChar
                | ColumnType::NVarchar
                | ColumnType::NChar
                | ColumnType::Text
                | ColumnType::NText
                | ColumnType::BigVarBin
                | ColumnType::BigBinary
                | ColumnType::Image
        ) {
            return Err(format!(
                "column {} has SQL type {:?}, which the TDS transport does not serve; set {}=sqlcmd for this read",
                index + 1,
                column.column_type(),
                crate::udm::live::SQL_TRANSPORT_ENV
            ));
        }
    }
    Ok(())
}

fn value(data: ColumnData<'static>) -> Result<Value, String> {
    let integer = |value: Option<i64>| value.map_or(Value::Null, Value::Int);
    Ok(match data {
        ColumnData::U8(value) => integer(value.map(Into::into)),
        ColumnData::I16(value) => integer(value.map(Into::into)),
        ColumnData::I32(value) => integer(value.map(Into::into)),
        ColumnData::I64(value) => integer(value),
        ColumnData::Bit(value) => integer(value.map(Into::into)),
        ColumnData::String(value) => {
            value.map_or(Value::Null, |text| Value::Text(text.into_owned()))
        }
        ColumnData::Binary(value) => {
            value.map_or(Value::Null, |bytes| Value::Bytes(bytes.into_owned()))
        }
        _ => return Err("the TDS transport received a value of an unserved SQL type".into()),
    })
}

async fn open(
    endpoint: &Endpoint,
    database: &str,
    credentials: Option<(&str, &str)>,
    encryption: Encryption,
) -> Result<Client<Compat<Transport>>, String> {
    let mut config = Config::new();
    config.database(database);
    config.application_name("gx-engine");
    config.handshake_timeout(Some(CONNECT_TIMEOUT));
    config.command_timeout(None);
    match credentials {
        Some((user, password)) => config.authentication(AuthMethod::sql_server(user, password)),
        None => config.authentication(integrated_authentication()?),
    }
    match encryption {
        Encryption::TlsVerified => config.encryption(EncryptionLevel::Required),
        Encryption::TlsUnverified => {
            config.encryption(EncryptionLevel::Required);
            config.trust_cert();
        }
        Encryption::Unencrypted => {
            config.encryption(EncryptionLevel::NotSupported);
            // A server that requires TLS still gets it; only a pipe of this machine comes here.
            config.trust_cert();
        }
    }
    let transport = match endpoint {
        Endpoint::Tcp {
            host,
            port,
            instance,
        } => {
            config.host(host);
            if let Some(port) = port {
                config.port(*port);
            }
            if let Some(instance) = instance {
                config.instance_name(instance);
            }
            let stream = tokio::time::timeout(CONNECT_TIMEOUT, TcpStream::connect_named(&config))
                .await
                .map_err(|_| "the server did not accept a TCP connection in time".to_string())?
                .map_err(|error| error.to_string())?;
            stream
                .set_nodelay(true)
                .map_err(|error| error.to_string())?;
            Transport::Tcp(stream)
        }
        Endpoint::Pipe { path } => {
            if let Some(host) = endpoint::pipe_host(path) {
                config.host(host);
            }
            open_pipe(path).await?
        }
        Endpoint::LocalDb { .. } => {
            return Err("the LocalDB instance pipe was not resolved".into());
        }
    };
    Client::connect(config, transport.compat_write())
        .await
        .map_err(|error| error.to_string())
}

#[cfg(windows)]
fn integrated_authentication() -> Result<AuthMethod, String> {
    Ok(AuthMethod::Integrated)
}

#[cfg(not(windows))]
fn integrated_authentication() -> Result<AuthMethod, String> {
    Err("Windows integrated authentication needs a Windows host".into())
}

#[cfg(windows)]
async fn open_pipe(path: &str) -> Result<Transport, String> {
    use tokio::net::windows::named_pipe::ClientOptions;
    /// `ERROR_PIPE_BUSY`: every instance of the pipe is serving another client for the moment.
    const PIPE_BUSY: i32 = 231;
    let deadline = tokio::time::Instant::now() + CONNECT_TIMEOUT;
    loop {
        match ClientOptions::new().open(path) {
            Ok(pipe) => return Ok(Transport::Pipe(pipe)),
            Err(error)
                if error.raw_os_error() == Some(PIPE_BUSY)
                    && tokio::time::Instant::now() < deadline =>
            {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(error) => return Err(format!("cannot open the server pipe: {error}")),
        }
    }
}

#[cfg(not(windows))]
async fn open_pipe(_: &str) -> Result<Transport, String> {
    Err("named pipes need a Windows host".into())
}
