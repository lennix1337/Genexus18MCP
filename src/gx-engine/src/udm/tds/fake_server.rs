//! An in-process TDS endpoint for the transport tests.
//!
//! Every byte it sends is built here from the published protocol (MS-TDS): PRELOGIN, LOGIN7,
//! SQL batches and the token stream of a reply. It proves how the transport handles those
//! messages. It was not captured from SQL Server and cannot prove interoperability with one.

use std::io;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

/// PRELOGIN encryption values (MS-TDS 2.2.6.5).
pub const ENCRYPT_NOT_SUP: u8 = 0x02;
pub const ENCRYPT_REQ: u8 = 0x03;

const PACKET_PRELOGIN: u8 = 0x12;
const PACKET_LOGIN7: u8 = 0x10;
const PACKET_BATCH: u8 = 0x01;
const PACKET_REPLY: u8 = 0x04;
const PACKET_BYTES: usize = 4096;

/// `DONE` status bits.
pub const DONE_FINAL: u16 = 0x0000;
pub const DONE_MORE: u16 = 0x0001;
pub const DONE_ERROR: u16 = 0x0002;

/// What the endpoint does with one SQL batch.
pub enum Reply {
    /// The complete token stream of the reply.
    Tokens(Vec<u8>),
    /// These bytes, then the connection ends without the reply being finished.
    Cut(Vec<u8>),
}

pub struct Script {
    /// The PRELOGIN encryption answer. The endpoint has no TLS, so anything but
    /// [`ENCRYPT_NOT_SUP`] leaves a client that then starts a handshake unanswered.
    pub encryption: u8,
    /// A login refused with this server error number and message.
    pub login_error: Option<(i32, String)>,
    /// Set by a test to make the endpoint refuse every later login.
    pub closed: Arc<AtomicBool>,
    pub respond: Box<dyn Fn(&str) -> Reply + Send + Sync>,
}

impl Script {
    pub fn replying(respond: impl Fn(&str) -> Reply + Send + Sync + 'static) -> Self {
        Self {
            encryption: ENCRYPT_NOT_SUP,
            login_error: None,
            closed: Arc::default(),
            respond: Box::new(respond),
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Login {
    pub user: String,
    pub password: String,
    pub database: String,
    /// `fIntSecurity` of LOGIN7: the client authenticates with an SSPI token.
    pub integrated: bool,
    pub sspi_bytes: usize,
}

#[derive(Debug, Default)]
pub struct Log {
    /// The encryption value of each connection's PRELOGIN, in arrival order.
    pub client_encryption: Vec<u8>,
    pub logins: Vec<Login>,
    pub batches: Vec<String>,
    /// The transaction descriptor each batch carried in its headers, in the order of `batches`.
    pub transactions: Vec<u64>,
}

pub struct FakeServer {
    /// The `ServerInstance` value that reaches this endpoint.
    pub server_instance: String,
    pub log: Arc<Mutex<Log>>,
    stop: Arc<AtomicBool>,
    thread: Option<std::thread::JoinHandle<()>>,
}

impl FakeServer {
    /// Listens on a loopback TCP port.
    pub fn tcp(script: Script) -> Self {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").expect("bind loopback");
        listener
            .set_nonblocking(true)
            .expect("nonblocking listener");
        let port = listener.local_addr().expect("local address").port();
        Self::run(
            format!("tcp:127.0.0.1,{port}"),
            script,
            move |context| async move {
                let listener = tokio::net::TcpListener::from_std(listener).expect("tokio listener");
                while !context.stop.load(Ordering::Relaxed) {
                    if let Ok(Ok((stream, _))) =
                        tokio::time::timeout(Duration::from_millis(20), listener.accept()).await
                    {
                        tokio::spawn(context.clone().serve(stream));
                    }
                }
            },
        )
    }

    /// Listens on a named pipe of this machine, as a LocalDB instance does.
    #[cfg(windows)]
    pub fn pipe(script: Script) -> Self {
        use tokio::net::windows::named_pipe::ServerOptions;
        static NEXT: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
        let path = format!(
            "\\\\.\\pipe\\LOCALDB#GXE{}_{}\\tsql\\query",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        );
        let name = path.clone();
        Self::run(format!("np:{path}"), script, move |context| async move {
            let mut server = ServerOptions::new()
                .first_pipe_instance(true)
                .create(&name)
                .expect("create pipe");
            while !context.stop.load(Ordering::Relaxed) {
                if let Ok(Ok(())) =
                    tokio::time::timeout(Duration::from_millis(20), server.connect()).await
                {
                    let connected =
                        std::mem::replace(&mut server, ServerOptions::new().create(&name).unwrap());
                    tokio::spawn(context.clone().serve(connected));
                }
            }
        })
    }

    /// The pipe path of a [`Self::pipe`] endpoint, as `sqllocaldb info` prints it.
    #[cfg(windows)]
    pub fn sqllocaldb_info(&self) -> String {
        format!(
            "Name:               GxeFake\r\nState:              Running\r\nInstance pipe name: {}\r\n",
            self.server_instance
        )
    }

    fn run<F, Fut>(server_instance: String, script: Script, accept: F) -> Self
    where
        F: FnOnce(Context) -> Fut + Send + 'static,
        Fut: std::future::Future<Output = ()>,
    {
        let log = Arc::new(Mutex::new(Log::default()));
        let stop = Arc::new(AtomicBool::new(false));
        let context = Context {
            script: Arc::new(script),
            log: log.clone(),
            stop: stop.clone(),
        };
        let (ready, listening) = std::sync::mpsc::channel();
        let thread = std::thread::spawn(move || {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_io()
                .enable_time()
                .build()
                .expect("fake server runtime");
            runtime.block_on(async move {
                ready.send(()).expect("test is waiting");
                accept(context).await
            });
        });
        listening.recv().expect("fake server started");
        Self {
            server_instance,
            log,
            stop,
            thread: Some(thread),
        }
    }

    pub fn log(&self) -> std::sync::MutexGuard<'_, Log> {
        self.log.lock().unwrap()
    }
}

impl Drop for FakeServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

#[derive(Clone)]
struct Context {
    script: Arc<Script>,
    log: Arc<Mutex<Log>>,
    stop: Arc<AtomicBool>,
}

impl Context {
    /// One connection, from PRELOGIN to the client's disconnect.
    async fn serve<S: AsyncRead + AsyncWrite + Unpin>(self, mut stream: S) {
        // An I/O error here is the client leaving; the test asserts on what the client saw.
        let _ = self.converse(&mut stream).await;
    }

    async fn converse<S: AsyncRead + AsyncWrite + Unpin>(&self, stream: &mut S) -> io::Result<()> {
        let (kind, prelogin) = read_message(stream).await?;
        if kind != PACKET_PRELOGIN {
            return Ok(());
        }
        self.log
            .lock()
            .unwrap()
            .client_encryption
            .push(prelogin_encryption(&prelogin));
        write_message(stream, &prelogin_reply(self.script.encryption), true).await?;

        let (kind, login) = read_message(stream).await?;
        if kind != PACKET_LOGIN7 {
            return Ok(());
        }
        let login = parse_login(&login);
        let refused = match (&self.script.login_error, login.integrated) {
            _ if self.script.closed.load(Ordering::Relaxed) => {
                Some((18456, "Login failed.".to_string()))
            }
            (Some((number, message)), _) => Some((*number, message.clone())),
            // The endpoint cannot answer an SSPI exchange: an integrated login ends here.
            (None, true) => Some((18452, "Login failed. The login is from an untrusted domain and cannot be used with Integrated authentication.".to_string())),
            (None, false) => None,
        };
        self.log.lock().unwrap().logins.push(login);
        if let Some((number, message)) = refused {
            let mut tokens = error(number, &message);
            tokens.extend(done(DONE_ERROR, 0));
            return write_message(stream, &tokens, true).await;
        }
        let mut tokens = login_ack();
        tokens.extend(done(DONE_FINAL, 0));
        write_message(stream, &tokens, true).await?;

        loop {
            let (kind, batch) = read_message(stream).await?;
            if kind != PACKET_BATCH {
                return Ok(());
            }
            let sql = batch_sql(&batch);
            {
                let mut log = self.log.lock().unwrap();
                log.batches.push(sql.clone());
                log.transactions.push(batch_transaction(&batch));
            }
            match (self.script.respond)(&sql) {
                Reply::Tokens(tokens) => write_message(stream, &tokens, true).await?,
                Reply::Cut(tokens) => {
                    write_message(stream, &tokens, false).await?;
                    return stream.shutdown().await;
                }
            }
        }
    }
}

/// Reads packets up to the one marked end-of-message and returns the message type and body.
async fn read_message<S: AsyncRead + Unpin>(stream: &mut S) -> io::Result<(u8, Vec<u8>)> {
    let mut body = Vec::new();
    loop {
        let mut header = [0u8; 8];
        stream.read_exact(&mut header).await?;
        let length = usize::from(u16::from_be_bytes([header[2], header[3]]));
        let mut payload = vec![0u8; length.saturating_sub(8)];
        stream.read_exact(&mut payload).await?;
        body.extend(payload);
        if header[1] & 0x01 != 0 {
            return Ok((header[0], body));
        }
    }
}

/// Sends `body` as reply packets of the default size. `complete` marks the last one
/// end-of-message; without it the client keeps waiting for more.
async fn write_message<S: AsyncWrite + Unpin>(
    stream: &mut S,
    body: &[u8],
    complete: bool,
) -> io::Result<()> {
    let chunks: Vec<&[u8]> = body.chunks(PACKET_BYTES - 8).collect();
    let mut packets = Vec::with_capacity(body.len() + chunks.len() * 8);
    for (index, chunk) in chunks.iter().enumerate() {
        let last = complete && index + 1 == chunks.len();
        packets.extend([PACKET_REPLY, u8::from(last)]);
        packets.extend(((chunk.len() + 8) as u16).to_be_bytes());
        packets.extend([0, 0, (index % 256) as u8, 0]);
        packets.extend(*chunk);
    }
    stream.write_all(&packets).await?;
    stream.flush().await
}

/// The ENCRYPTION option of a PRELOGIN message: its option table is `token, offset, length`
/// entries (big-endian) ended by `0xFF`.
fn prelogin_encryption(message: &[u8]) -> u8 {
    let mut entry = 0;
    while message[entry] != 0xFF {
        if message[entry] == 0x01 {
            let offset = usize::from(u16::from_be_bytes([message[entry + 1], message[entry + 2]]));
            return message[offset];
        }
        entry += 5;
    }
    panic!("the PRELOGIN message has no ENCRYPTION option");
}

/// PRELOGIN reply: VERSION, ENCRYPTION, INSTOPT, THREADID and MARS options.
fn prelogin_reply(encryption: u8) -> Vec<u8> {
    let options: [(u8, &[u8]); 5] = [
        (0x00, &[15, 0, 0x10, 0x04, 0, 0]),
        (0x01, &[encryption]),
        (0x02, &[0]),
        (0x03, &[]),
        (0x04, &[0]),
    ];
    let mut table = Vec::new();
    let mut data: Vec<u8> = Vec::new();
    let table_bytes = options.len() * 5 + 1;
    for (token, value) in options {
        table.push(token);
        table.extend(((table_bytes + data.len()) as u16).to_be_bytes());
        table.extend((value.len() as u16).to_be_bytes());
        data.extend(value);
    }
    table.push(0xFF);
    table.extend(data);
    table
}

/// The fields of a LOGIN7 message this endpoint looks at. Its variable part is addressed by
/// `offset, length` pairs (little-endian, lengths in UTF-16 units) that start at byte 36.
fn parse_login(message: &[u8]) -> Login {
    let pair = |at: usize| {
        (
            usize::from(u16::from_le_bytes([message[at], message[at + 1]])),
            usize::from(u16::from_le_bytes([message[at + 2], message[at + 3]])),
        )
    };
    let text = |at: usize, decode: fn(u8) -> u8| {
        let (offset, units) = pair(at);
        let bytes: Vec<u8> = message[offset..offset + units * 2]
            .iter()
            .map(|byte| decode(*byte))
            .collect();
        let units: Vec<u16> = bytes
            .chunks(2)
            .map(|unit| u16::from_le_bytes([unit[0], unit[1]]))
            .collect();
        String::from_utf16(&units).expect("UTF-16 login field")
    };
    // The password is obfuscated: each byte has its nibbles swapped and is XORed with 0xA5.
    let clear = |byte: u8| (byte ^ 0xA5).rotate_left(4);
    Login {
        user: text(40, |byte| byte),
        password: text(44, clear),
        database: text(68, |byte| byte),
        integrated: message[25] & 0x80 != 0,
        sspi_bytes: pair(78).1,
    }
}

/// The statement of a SQL batch: UTF-16 text after the `ALL_HEADERS` block, whose first four
/// bytes are its own length.
fn batch_sql(message: &[u8]) -> String {
    let headers = u32::from_le_bytes([message[0], message[1], message[2], message[3]]) as usize;
    let units: Vec<u16> = message[headers..]
        .chunks(2)
        .map(|unit| u16::from_le_bytes([unit[0], unit[1]]))
        .collect();
    String::from_utf16(&units).expect("UTF-16 batch")
}

/// The transaction descriptor of a SQL batch: the first header of `ALL_HEADERS` is the
/// transaction descriptor header (total length, header length, type 2, descriptor, request count).
fn batch_transaction(message: &[u8]) -> u64 {
    assert_eq!(
        u16::from_le_bytes([message[8], message[9]]),
        2,
        "transaction descriptor header"
    );
    u64::from_le_bytes(message[10..18].try_into().expect("eight bytes"))
}

/// `ENVCHANGE` type 8: a transaction began, and later requests must carry its descriptor.
pub fn transaction_began(descriptor: u64) -> Vec<u8> {
    let mut body = vec![8, 8];
    body.extend(descriptor.to_le_bytes());
    body.push(0);
    token(0xE3, body)
}

fn utf16(text: &str) -> Vec<u8> {
    text.encode_utf16().flat_map(u16::to_le_bytes).collect()
}

/// `B_VARCHAR`: a one-byte length in UTF-16 units, then the text.
fn b_varchar(text: &str) -> Vec<u8> {
    let mut bytes = vec![text.encode_utf16().count() as u8];
    bytes.extend(utf16(text));
    bytes
}

fn token(kind: u8, body: Vec<u8>) -> Vec<u8> {
    let mut bytes = vec![kind];
    bytes.extend((body.len() as u16).to_le_bytes());
    bytes.extend(body);
    bytes
}

/// `LOGINACK`: TSQL interface, TDS 7.4, the server name and its version.
fn login_ack() -> Vec<u8> {
    let mut body = vec![0x01, 0x74, 0x00, 0x00, 0x04];
    body.extend(b_varchar("Microsoft SQL Server"));
    body.extend([15, 0, 0x10, 0x04]);
    token(0xAD, body)
}

/// `ERROR`: number, state, class, message, server name, procedure name and line.
pub fn error(number: i32, message: &str) -> Vec<u8> {
    let mut body = number.to_le_bytes().to_vec();
    body.extend([1, 16]);
    body.extend((message.encode_utf16().count() as u16).to_le_bytes());
    body.extend(utf16(message));
    body.extend(b_varchar("fake"));
    body.extend(b_varchar(""));
    body.extend(1u32.to_le_bytes());
    token(0xAA, body)
}

/// `DONE`: status, current command and row count.
pub fn done(status: u16, rows: u64) -> Vec<u8> {
    let mut bytes = vec![0xFD];
    bytes.extend(status.to_le_bytes());
    bytes.extend(0xC1u16.to_le_bytes());
    bytes.extend(rows.to_le_bytes());
    bytes
}

/// A column of a `COLMETADATA` token, by its `TYPE_INFO`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Column {
    /// `int NOT NULL`: four bytes in every row.
    Int,
    /// `int NULL`: a length byte (0 for NULL), then the value.
    IntN,
    /// `bigint NULL`.
    BigIntN,
    /// `varchar(8000)`: a two-byte length (0xFFFF for NULL), then code page 1252 bytes.
    VarChar,
    /// `nvarchar(4000)`: a two-byte length, then UTF-16.
    NVarChar,
    /// `varbinary(8000)`.
    VarBinary,
    /// `varchar(max)`: a partially length-prefixed (PLP) value.
    VarCharMax,
    /// `varbinary(max)`: a PLP value.
    VarBinaryMax,
    /// `datetime NOT NULL`: eight bytes.
    DateTime,
}

/// `SQL_Latin1_General_CP1_CI_AS`: LCID 0x0409, sort id 52 (code page 1252).
const COLLATION: [u8; 5] = [0x09, 0x04, 0xD0, 0x00, 0x34];

/// `COLMETADATA`: the column count, then each column's user type, flags, `TYPE_INFO` and name.
pub fn columns(columns: &[Column]) -> Vec<u8> {
    let mut bytes = vec![0x81];
    bytes.extend((columns.len() as u16).to_le_bytes());
    for (index, column) in columns.iter().enumerate() {
        bytes.extend(0u32.to_le_bytes());
        bytes.extend(0x0001u16.to_le_bytes());
        match column {
            Column::Int => bytes.push(0x38),
            Column::IntN => bytes.extend([0x26, 4]),
            Column::BigIntN => bytes.extend([0x26, 8]),
            Column::VarChar => {
                bytes.extend([0xA7, 0x40, 0x1F]);
                bytes.extend(COLLATION);
            }
            Column::NVarChar => {
                bytes.extend([0xE7, 0x40, 0x1F]);
                bytes.extend(COLLATION);
            }
            Column::VarBinary => bytes.extend([0xA5, 0x40, 0x1F]),
            Column::VarCharMax => {
                bytes.extend([0xA7, 0xFF, 0xFF]);
                bytes.extend(COLLATION);
            }
            Column::VarBinaryMax => bytes.extend([0xA5, 0xFF, 0xFF]),
            Column::DateTime => bytes.push(0x3D),
        }
        bytes.extend(b_varchar(&format!("c{index}")));
    }
    bytes
}

/// One value of a row, encoded for its column.
pub enum Cell<'a> {
    Int(i32),
    IntN(Option<i32>),
    BigIntN(Option<i64>),
    /// `varchar(n)`, `nvarchar(n)` (as UTF-16 bytes) or `varbinary(n)`.
    Short(Option<&'a [u8]>),
    /// A `max` value sent in chunks of `chunk` bytes. `sized` declares the total length up
    /// front; otherwise the length is declared unknown and only the chunks delimit the value.
    Plp {
        value: Option<&'a [u8]>,
        chunk: usize,
        sized: bool,
    },
    /// Eight raw bytes of a `datetime`.
    DateTime([u8; 8]),
}

impl Cell<'_> {
    fn is_null(&self) -> bool {
        matches!(
            self,
            Cell::IntN(None)
                | Cell::BigIntN(None)
                | Cell::Short(None)
                | Cell::Plp { value: None, .. }
        )
    }

    fn encode(&self, bytes: &mut Vec<u8>) {
        match self {
            Cell::Int(value) => bytes.extend(value.to_le_bytes()),
            Cell::IntN(None) | Cell::BigIntN(None) => bytes.push(0),
            Cell::IntN(Some(value)) => {
                bytes.push(4);
                bytes.extend(value.to_le_bytes());
            }
            Cell::BigIntN(Some(value)) => {
                bytes.push(8);
                bytes.extend(value.to_le_bytes());
            }
            Cell::Short(None) => bytes.extend(0xFFFFu16.to_le_bytes()),
            Cell::Short(Some(value)) => {
                bytes.extend((value.len() as u16).to_le_bytes());
                bytes.extend(*value);
            }
            Cell::Plp { value: None, .. } => bytes.extend(u64::MAX.to_le_bytes()),
            Cell::Plp {
                value: Some(value),
                chunk,
                sized,
            } => {
                let total = if *sized {
                    value.len() as u64
                } else {
                    u64::MAX - 1
                };
                bytes.extend(total.to_le_bytes());
                for part in value.chunks(*chunk) {
                    bytes.extend((part.len() as u32).to_le_bytes());
                    bytes.extend(part);
                }
                bytes.extend(0u32.to_le_bytes());
            }
            Cell::DateTime(value) => bytes.extend(value),
        }
    }
}

/// `ROW`: every value in column order.
pub fn row(cells: &[Cell<'_>]) -> Vec<u8> {
    let mut bytes = vec![0xD1];
    for cell in cells {
        cell.encode(&mut bytes);
    }
    bytes
}

/// `NBCROW`: a bitmap of the NULL columns, then only the values that are not NULL.
pub fn nbc_row(cells: &[Cell<'_>]) -> Vec<u8> {
    let mut bitmap = vec![0u8; cells.len().div_ceil(8)];
    for (index, cell) in cells.iter().enumerate() {
        if cell.is_null() {
            bitmap[index / 8] |= 1 << (index % 8);
        }
    }
    let mut bytes = vec![0xD2];
    bytes.extend(bitmap);
    for cell in cells.iter().filter(|cell| !cell.is_null()) {
        cell.encode(&mut bytes);
    }
    bytes
}

/// A whole reply of one result set: metadata, rows and the final `DONE`.
pub fn result_set(metadata: &[Column], rows: &[Vec<u8>]) -> Vec<u8> {
    let mut tokens = columns(metadata);
    for row in rows {
        tokens.extend(row);
    }
    tokens.extend(done(DONE_FINAL, rows.len() as u64));
    tokens
}

/// UTF-16 bytes of `text`, for an `nvarchar` cell.
pub fn wide(text: &str) -> Vec<u8> {
    utf16(text)
}
