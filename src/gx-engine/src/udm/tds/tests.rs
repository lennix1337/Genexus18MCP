//! The transport against the in-process endpoint of `fake_server`. These prove how the client
//! side handles the protocol and the fallback rules; they do not prove interoperability with a
//! real SQL Server, which `scripts/validate-engine-tds-live.ps1` checks on a machine that has one.

use super::fake_server::{
    Cell, Column, DONE_ERROR, DONE_MORE, ENCRYPT_NOT_SUP, ENCRYPT_REQ, FakeServer, Reply, Script,
    columns, done, error, nbc_row, result_set, row, transaction_began, wide,
};
use super::{ConnectOptions, Encryption, TdsSession, Value, render_text};
use crate::udm::client::UdmClient;
use crate::udm::connection::{AuthMode, KbConnectionInfo};
use crate::udm::decoder::{decode_blob, encode_blob};
use crate::udm::live::{ReadSession, SqlTransport, transport_report};

const USER: &str = "gxuser";
const PASSWORD: &str = "s3cret-p\u{e4}ss";

fn info(server_instance: &str, auth_mode: AuthMode) -> KbConnectionInfo {
    KbConnectionInfo {
        server_instance: server_instance.into(),
        db_name: "GX_KB_Fake".into(),
        auth_mode,
        create_db_in_kb_folder: None,
        directory: None,
        data_file: None,
        log_file: None,
        host_name: None,
        detach_on_close: None,
    }
}

fn sql_info(server: &FakeServer) -> KbConnectionInfo {
    info(
        &server.server_instance,
        AuthMode::SqlServer {
            user: USER.into(),
            password: Some(PASSWORD.into()),
        },
    )
}

/// The endpoint has no TLS, so the tests that read from it ask for none.
fn unencrypted() -> ConnectOptions {
    ConnectOptions {
        unencrypted_for_test: true,
        ..ConnectOptions::default()
    }
}

fn connect(server: &FakeServer) -> TdsSession {
    TdsSession::connect(&sql_info(server), Some((USER, PASSWORD)), &unencrypted())
        .expect("connect to the fake endpoint")
}

fn replying(tokens: Vec<u8>) -> FakeServer {
    FakeServer::tcp(Script::replying(move |_| Reply::Tokens(tokens.clone())))
}

fn text(value: &str) -> Value {
    Value::Text(value.into())
}

#[test]
fn prelogin_login_and_a_batch_return_typed_rows_and_the_sqlcmd_text() {
    let server = replying(result_set(
        &[
            Column::Int,
            Column::NVarChar,
            Column::VarChar,
            Column::BigIntN,
        ],
        &[
            row(&[
                Cell::Int(31),
                Cell::Short(Some(&wide("Proc\u{e9}dure \u{4e2d}"))),
                Cell::Short(Some(b"Objects")),
                Cell::BigIntN(Some(5_000_000_000)),
            ]),
            row(&[
                Cell::Int(-81),
                Cell::Short(Some(&wide(""))),
                // 0xE9 in code page 1252 is U+00E9.
                Cell::Short(Some(b"caf\xE9")),
                Cell::BigIntN(None),
            ]),
        ],
    ));
    let mut session = connect(&server);
    assert_eq!(session.encryption(), Encryption::Unencrypted);
    let sql = "SET NOCOUNT ON; SELECT EntityTypeId, EntityTypeName FROM EntityType";
    let rows = session.query_rows(sql).expect("rows");
    assert_eq!(
        rows,
        vec![
            vec![
                Value::Int(31),
                text("Proc\u{e9}dure \u{4e2d}"),
                text("Objects"),
                Value::Int(5_000_000_000)
            ],
            vec![Value::Int(-81), text(""), text("caf\u{e9}"), Value::Null],
        ]
    );
    assert_eq!(
        render_text(&rows).unwrap(),
        "31\tProc\u{e9}dure \u{4e2d}\tObjects\t5000000000\r\n-81\t\tcaf\u{e9}\tNULL\r\n"
    );

    let log = server.log();
    assert_eq!(log.client_encryption, [ENCRYPT_NOT_SUP]);
    assert_eq!(log.batches, [sql]);
    let login = &log.logins[0];
    assert_eq!(
        (
            login.user.as_str(),
            login.password.as_str(),
            login.database.as_str()
        ),
        (USER, PASSWORD, "GX_KB_Fake")
    );
    assert!(!login.integrated);
}

#[test]
fn max_values_arrive_whole_across_chunks_and_packets() {
    // 100 000 bytes: 25 packets, sent as chunks that never align with them.
    let blob: Vec<u8> = (0..100_000u32).map(|n| (n * 7 % 251) as u8).collect();
    let hex: String = blob[..5000]
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect();
    let server = replying(result_set(
        &[Column::IntN, Column::VarBinaryMax, Column::VarCharMax],
        &[
            row(&[
                Cell::IntN(Some(1)),
                Cell::Plp {
                    value: Some(&blob),
                    chunk: 3001,
                    sized: false,
                },
                Cell::Plp {
                    value: Some(hex.as_bytes()),
                    chunk: 777,
                    sized: true,
                },
            ]),
            row(&[
                Cell::IntN(Some(2)),
                Cell::Plp {
                    value: Some(b""),
                    chunk: 1,
                    sized: true,
                },
                Cell::Plp {
                    value: None,
                    chunk: 1,
                    sized: true,
                },
            ]),
            row(&[
                Cell::IntN(Some(3)),
                Cell::Plp {
                    value: None,
                    chunk: 1,
                    sized: false,
                },
                Cell::Plp {
                    value: Some(b"00FF"),
                    chunk: 1,
                    sized: false,
                },
            ]),
        ],
    ));
    let rows = connect(&server)
        .query_rows("SET NOCOUNT ON; SELECT 1")
        .expect("rows");
    assert_eq!(
        rows,
        vec![
            vec![Value::Int(1), Value::Bytes(blob), text(&hex)],
            vec![Value::Int(2), Value::Bytes(Vec::new()), Value::Null],
            vec![Value::Int(3), Value::Null, text("00FF")],
        ]
    );
}

#[test]
fn nulls_are_read_from_rows_and_from_null_bitmap_rows() {
    let metadata = [
        Column::IntN,
        Column::NVarChar,
        Column::VarBinary,
        Column::IntN,
    ];
    let server = replying(result_set(
        &metadata,
        &[
            row(&[
                Cell::IntN(None),
                Cell::Short(None),
                Cell::Short(None),
                Cell::IntN(Some(4)),
            ]),
            nbc_row(&[
                Cell::IntN(None),
                Cell::Short(Some(&wide("x"))),
                Cell::Short(None),
                Cell::IntN(None),
            ]),
            nbc_row(&[
                Cell::IntN(Some(9)),
                Cell::Short(None),
                Cell::Short(Some(b"\x00\x01")),
                Cell::IntN(Some(8)),
            ]),
        ],
    ));
    let rows = connect(&server)
        .query_rows("SET NOCOUNT ON; SELECT 1")
        .expect("rows");
    assert_eq!(
        rows,
        vec![
            vec![Value::Null, Value::Null, Value::Null, Value::Int(4)],
            vec![Value::Null, text("x"), Value::Null, Value::Null],
            vec![
                Value::Int(9),
                Value::Null,
                Value::Bytes(vec![0, 1]),
                Value::Int(8)
            ],
        ]
    );
    // The text form spells a null the way sqlcmd does; a binary column has no text form.
    assert_eq!(
        render_text(&rows[..2]).unwrap(),
        "NULL\tNULL\tNULL\t4\r\nNULL\tx\tNULL\tNULL\r\n"
    );
    assert!(render_text(&rows).is_err());
}

#[test]
fn the_rows_of_every_result_set_are_returned_in_order() {
    let mut tokens = columns(&[Column::Int]);
    tokens.extend(row(&[Cell::Int(1)]));
    tokens.extend(row(&[Cell::Int(2)]));
    tokens.extend(done(DONE_MORE, 2));
    // An empty result set in the middle.
    tokens.extend(columns(&[Column::NVarChar]));
    tokens.extend(done(DONE_MORE, 0));
    tokens.extend(result_set(
        &[Column::NVarChar, Column::Int],
        &[row(&[Cell::Short(Some(&wide("last"))), Cell::Int(3)])],
    ));
    let server = replying(tokens);
    let mut session = connect(&server);
    let rows = session
        .query_rows("SET NOCOUNT ON; SELECT 1")
        .expect("rows");
    assert_eq!(
        rows,
        vec![
            vec![Value::Int(1)],
            vec![Value::Int(2)],
            vec![text("last"), Value::Int(3)]
        ]
    );
    // The session is still usable: each batch is answered on the same connection.
    assert_eq!(
        session
            .query_rows("SET NOCOUNT ON; SELECT 2")
            .expect("second batch")
            .len(),
        3
    );
    assert_eq!(server.log().logins.len(), 1);
}

#[test]
fn a_server_error_fails_the_read_and_ends_the_session() {
    let server = FakeServer::tcp(Script::replying(|sql| {
        if sql.contains("Absent") {
            // The error arrives after a row of the same batch was already sent.
            let mut tokens = columns(&[Column::Int]);
            tokens.extend(row(&[Cell::Int(1)]));
            tokens.extend(error(208, "Invalid object name 'Absent'."));
            tokens.extend(done(DONE_ERROR, 0));
            Reply::Tokens(tokens)
        } else {
            Reply::Tokens(result_set(&[Column::Int], &[row(&[Cell::Int(7)])]))
        }
    }));
    let mut session = connect(&server);
    assert_eq!(
        session.query_rows("SET NOCOUNT ON; SELECT 7").unwrap(),
        [[Value::Int(7)]]
    );
    let failure = session
        .query_rows("SET NOCOUNT ON; SELECT * FROM Absent")
        .unwrap_err();
    assert!(
        failure.contains("Invalid object name 'Absent'"),
        "{failure}"
    );
    // Mutation check: a session that kept answering after a failed batch would let a caller
    // read on from a stream it cannot trust.
    let after = session.query_rows("SET NOCOUNT ON; SELECT 7").unwrap_err();
    assert!(after.contains("earlier failure"), "{after}");
    assert_eq!(server.log().batches.len(), 2);
}

#[test]
fn a_connection_cut_mid_stream_is_an_error_not_a_short_result() {
    let blob = vec![0xABu8; 20_000];
    let server = FakeServer::tcp(Script::replying(move |_| {
        let mut tokens = columns(&[Column::Int, Column::VarBinaryMax]);
        tokens.extend(row(&[
            Cell::Int(1),
            Cell::Plp {
                value: Some(&blob),
                chunk: 4000,
                sized: true,
            },
        ]));
        let whole_row = tokens.len();
        tokens.extend(row(&[
            Cell::Int(2),
            Cell::Plp {
                value: Some(&blob),
                chunk: 4000,
                sized: true,
            },
        ]));
        // The first row arrives whole; the second ends inside its payload.
        tokens.truncate(whole_row + 9000);
        Reply::Cut(tokens)
    }));
    let mut session = connect(&server);
    let failure = session.query_rows("SET NOCOUNT ON; SELECT 1").unwrap_err();
    assert!(failure.starts_with("TDS read failed"), "{failure}");
    assert!(session.query_rows("SET NOCOUNT ON; SELECT 1").is_err());
}

#[test]
fn a_column_type_without_an_agreed_text_form_is_refused() {
    let server = replying(result_set(
        &[Column::Int, Column::DateTime],
        &[row(&[Cell::Int(1), Cell::DateTime([0; 8])])],
    ));
    let failure = connect(&server)
        .query_rows("SET NOCOUNT ON; SELECT 1")
        .unwrap_err();
    assert!(
        failure.contains("column 2") && failure.contains("Datetime"),
        "{failure}"
    );
    assert!(
        failure.contains("GXMCP_ENGINE_SQL_TRANSPORT=sqlcmd"),
        "{failure}"
    );
}

#[test]
fn a_server_without_encryption_is_refused_before_the_login_is_sent() {
    let server = replying(Vec::new());
    // The options of a real run: TLS with a validated certificate over TCP.
    let failure = TdsSession::connect(
        &sql_info(&server),
        Some((USER, PASSWORD)),
        &ConnectOptions::default(),
    )
    .unwrap_err();
    assert!(failure.contains("encryption tls"), "{failure}");
    let log = server.log();
    assert_eq!(log.client_encryption, [ENCRYPT_REQ]);
    // Mutation check: with an unencrypted attempt in the TCP plan the login, and the password
    // in it, reaches the server in clear text.
    assert!(log.logins.is_empty(), "the login was sent without TLS");
}

#[test]
fn a_refused_login_reports_neither_the_login_nor_the_password() {
    let server = FakeServer::tcp(Script {
        login_error: Some((
            18456,
            format!("Login failed for user '{USER}' (password {PASSWORD})."),
        )),
        ..Script::replying(|_| Reply::Tokens(Vec::new()))
    });
    let failure = TdsSession::connect(&sql_info(&server), Some((USER, PASSWORD)), &unencrypted())
        .unwrap_err();
    assert!(
        failure.contains("Login failed for user '<redacted>'"),
        "{failure}"
    );
    assert!(
        !failure.contains(USER) && !failure.contains(PASSWORD),
        "{failure}"
    );
    assert_eq!(server.log().logins.len(), 1);
}

#[cfg(windows)]
#[test]
fn a_local_pipe_is_reached_by_its_name_and_through_the_localdb_instance() {
    let server = FakeServer::pipe(Script::replying(|_| {
        Reply::Tokens(result_set(&[Column::Int], &[row(&[Cell::Int(42)])]))
    }));
    // The explicit `np:` form.
    let mut session = connect(&server);
    assert_eq!(
        session.query_rows("SET NOCOUNT ON; SELECT 42").unwrap(),
        [[Value::Int(42)]]
    );

    // `(LocalDB)\name`: the pipe comes from what `sqllocaldb info` prints for the instance.
    let localdb = info(
        "(LocalDB)\\GxeFake",
        AuthMode::SqlServer {
            user: USER.into(),
            password: None,
        },
    );
    let mut asked = Vec::new();
    let mut session = TdsSession::connect_with(
        &localdb,
        Some((USER, PASSWORD)),
        &unencrypted(),
        &mut |args| {
            asked.push(args.join(" "));
            Ok(server.sqllocaldb_info())
        },
    )
    .expect("connect through the instance pipe");
    assert_eq!(asked, ["info GxeFake"]);
    assert_eq!(
        session.query_rows("SET NOCOUNT ON; SELECT 42").unwrap(),
        [[Value::Int(42)]]
    );
    assert_eq!(server.log().logins.len(), 2);

    // An instance that cannot be resolved fails before anything is opened.
    let failure = TdsSession::connect_with(&localdb, None, &unencrypted(), &mut |_| {
        Err("sqllocaldb info failed".into())
    })
    .unwrap_err();
    assert!(failure.contains("sqllocaldb info failed"), "{failure}");
    assert_eq!(server.log().client_encryption.len(), 2);
}

#[cfg(windows)]
#[test]
fn an_integrated_login_on_a_local_pipe_asks_for_tls_first_and_sends_no_password() {
    let server = FakeServer::pipe(Script::replying(|_| Reply::Tokens(Vec::new())));
    let localdb = info("(LocalDB)\\GxeFake", AuthMode::Integrated);
    // The endpoint offers no TLS and cannot answer SSPI, so the login is refused; what
    // matters is what the client sent on the way.
    let failure = TdsSession::connect_with(&localdb, None, &ConnectOptions::default(), &mut |_| {
        Ok(server.sqllocaldb_info())
    })
    .unwrap_err();
    assert!(failure.contains("encryption none"), "{failure}");
    let log = server.log();
    // First TLS; only when that is refused, and only because no password travels, without it.
    assert_eq!(log.client_encryption, [ENCRYPT_REQ, ENCRYPT_NOT_SUP]);
    assert_eq!(log.logins.len(), 1);
    let login = &log.logins[0];
    assert!(login.integrated && login.sspi_bytes > 0, "{login:?}");
    assert!(
        login.user.is_empty() && login.password.is_empty(),
        "{login:?}"
    );
}

#[cfg(windows)]
#[test]
fn a_sql_login_on_a_local_pipe_is_never_sent_without_tls() {
    let server = FakeServer::pipe(Script::replying(|_| Reply::Tokens(Vec::new())));
    let localdb = info(
        "(LocalDB)\\GxeFake",
        AuthMode::SqlServer {
            user: USER.into(),
            password: None,
        },
    );
    let failure = TdsSession::connect_with(
        &localdb,
        Some((USER, PASSWORD)),
        &ConnectOptions::default(),
        &mut |_| Ok(server.sqllocaldb_info()),
    )
    .unwrap_err();
    assert!(
        failure.contains("encryption tls-unverified-certificate"),
        "{failure}"
    );
    let log = server.log();
    assert_eq!(log.client_encryption, [ENCRYPT_REQ]);
    assert!(log.logins.is_empty());
}

/// A stored part: a real envelope the existing decoder reads.
fn stored_part() -> Vec<u8> {
    let source = "For each\n    &Total += InvoiceAmount\nEndfor\n".repeat(200);
    encode_blob(&source, true).expect("encode a part")
}

fn part_reply(sql: &str, data: &[u8], properties: &[u8]) -> Reply {
    assert!(
        sql.contains(", EntityVersionData, ") && !sql.contains("CONVERT(VARCHAR(MAX)"),
        "a part read over TDS selects the payload as bytes: {sql}"
    );
    Reply::Tokens(result_set(
        &[
            Column::VarChar,
            Column::IntN,
            Column::VarBinaryMax,
            Column::IntN,
            Column::VarBinaryMax,
        ],
        &[row(&[
            Cell::Short(Some(b"PRESENT")),
            Cell::IntN(Some(data.len() as i32)),
            Cell::Plp {
                value: Some(data),
                chunk: 1500,
                sized: true,
            },
            Cell::IntN(Some(properties.len() as i32)),
            Cell::Plp {
                value: Some(properties),
                chunk: 1500,
                sized: true,
            },
        ])],
    ))
}

#[test]
fn a_client_reads_a_part_as_bytes_and_the_existing_decoder_reads_it() {
    let part = stored_part();
    let served = part.clone();
    let server = FakeServer::tcp(Script::replying(move |sql| {
        part_reply(
            sql,
            &served,
            b"<Properties><Name>IsDefault</Name></Properties>",
        )
    }));
    let info = sql_info(&server);
    SqlTransport::request_tds_for_test(&info, unencrypted());
    let client = UdmClient::from_connection_info(info.clone()).with_persistent_reads();

    let record = client
        .get_entity_version(38, 7, 2)
        .expect("read")
        .expect("a row");
    assert_eq!(record.data, part);
    assert_eq!(
        record.properties.as_deref(),
        Some("<Properties><Name>IsDefault</Name></Properties>")
    );
    // The envelope and GZip decoder is the one every other read uses.
    let decoded = decode_blob(&record.data).expect("decode the envelope");
    assert!(decoded.contains("&Total += InvoiceAmount"), "{decoded}");

    let report = transport_report(&info);
    assert_eq!((report.requested, report.used), ("tds", Some("tds")));
    assert_eq!(report.fallback_reason, None);
    assert_eq!(report.encryption, Some("none"));
}

#[test]
fn a_client_reads_text_rows_over_tds_through_the_same_parsers() {
    let server = FakeServer::tcp(Script::replying(|sql| {
        assert!(
            sql.starts_with("SET NOCOUNT ON; SELECT EntityTypeId, EntityTypeName"),
            "{sql}"
        );
        Reply::Tokens(result_set(
            &[Column::Int, Column::NVarChar, Column::NVarChar],
            &[
                row(&[
                    Cell::Int(38),
                    Cell::Short(Some(&wide("Procedure"))),
                    Cell::Short(Some(&wide("Objects"))),
                ]),
                row(&[
                    Cell::Int(81),
                    Cell::Short(Some(&wide("Variables"))),
                    Cell::Short(Some(&wide(""))),
                ]),
            ],
        ))
    }));
    let info = sql_info(&server);
    SqlTransport::request_tds_for_test(&info, unencrypted());
    // One client keeps a session, the other opens one per statement.
    for client in [
        UdmClient::from_connection_info(info.clone()).with_persistent_reads(),
        UdmClient::from_connection_info(info.clone()),
    ] {
        let types = client.get_entity_types().expect("entity types");
        let read: Vec<_> = types
            .iter()
            .map(|t| (t.entity_type_id, t.name.as_str(), t.namespace.as_str()))
            .collect();
        assert_eq!(read, [(38, "Procedure", "Objects"), (81, "Variables", "")]);
    }
    // A kept session logs in once for its reads; a per-statement read logs in each time.
    assert_eq!(server.log().logins.len(), 2);
}

#[test]
fn a_failure_after_the_first_tds_session_is_an_error_and_sqlcmd_is_not_started() {
    let part = stored_part();
    let script = Script::replying(move |sql| {
        if sql.contains("EntityId = 7 ") {
            part_reply(sql, &part, b"")
        } else {
            // The row is cut inside its payload.
            let Reply::Tokens(mut tokens) = part_reply(sql, &part, b"") else {
                unreachable!()
            };
            tokens.truncate(tokens.len() / 2);
            Reply::Cut(tokens)
        }
    });
    let closed = script.closed.clone();
    let server = FakeServer::tcp(script);
    let info = sql_info(&server);
    SqlTransport::request_tds_for_test(&info, unencrypted());
    let client = UdmClient::from_connection_info(info.clone()).with_persistent_reads();
    assert!(
        client
            .get_entity_version(38, 7, 2)
            .expect("first read")
            .is_some()
    );

    // Mid-read: the error is the transport's, and no row of the cut read is kept.
    let failure = client.get_entity_version(38, 8, 2).unwrap_err();
    assert!(failure.starts_with("TDS read failed"), "{failure}");
    assert!(!failure.contains("sqlcmd"), "{failure}");

    // The endpoint refuses logins from now on: a new session cannot be opened. The connection
    // already read over TDS, so this is an error too. Mutation check: falling back here would
    // start sqlcmd, and its start error (or its rows) would be the result.
    closed.store(true, std::sync::atomic::Ordering::Relaxed);
    let failure = client.get_entity_version(38, 9, 2).unwrap_err();
    assert!(failure.contains("already reads over TDS"), "{failure}");
    assert!(
        !failure.contains("sqlcmd reader") && !failure.contains("invoke sqlcmd"),
        "{failure}"
    );
    let report = transport_report(&info);
    assert_eq!((report.used, report.fallback_reason), (Some("tds"), None));
}

#[test]
fn a_consistent_read_session_keeps_its_transaction_over_tds() {
    let server = FakeServer::tcp(Script::replying(|sql| {
        Reply::Tokens(if sql.contains("BEGIN TRANSACTION") {
            let mut tokens = transaction_began(0x1122_3344_5566_7788);
            tokens.extend(result_set(&[Column::Int], &[row(&[Cell::Int(1)])]));
            tokens
        } else {
            result_set(&[Column::Int], &[row(&[Cell::Int(42)])])
        })
    }));
    let info = sql_info(&server);
    SqlTransport::request_tds_for_test(&info, unencrypted());
    let mut session = ReadSession::start_consistent(&info).expect("consistent session");
    assert!(session.binary_payloads());
    assert_eq!(
        session.query("SET NOCOUNT ON; SELECT 42;").unwrap(),
        "42\r\n"
    );
    // A kept session refuses what a kept sqlcmd session refuses, on either transport.
    assert!(session.query("DELETE FROM EntityVersion").is_err());
    assert!(session.query_rows("SET NOCOUNT ON; SELECT 1;\nGO").is_err());

    let log = server.log();
    assert_eq!(log.batches.len(), 2);
    assert!(
        log.batches[0].contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRANSACTION;")
    );
    // The read runs inside the transaction the first batch began, on the same login.
    assert_eq!(log.transactions, [0, 0x1122_3344_5566_7788]);
    assert_eq!(log.logins.len(), 1);
}

#[test]
fn a_session_that_does_not_report_its_transaction_is_not_a_consistent_one() {
    let server = replying(result_set(&[Column::Int], &[row(&[Cell::Int(0)])]));
    let info = sql_info(&server);
    SqlTransport::request_tds_for_test(&info, unencrypted());
    let failure = ReadSession::start_consistent(&info).unwrap_err();
    assert_eq!(
        failure,
        "tds did not establish the consistent read transaction"
    );
}

/// Synthetic benchmark: part rows read as bytes from the in-process endpoint over loopback TCP,
/// without TLS. The time covers the endpoint copying and sending its prebuilt reply, the
/// socket, and the driver parsing the rows; it says nothing about SQL Server, LocalDB or the
/// `sqlcmd` path. Run it by name on a release build:
/// `cargo test --release --manifest-path src/gx-engine/Cargo.toml --features tds --lib synthetic_benchmark_tds_rows_from_the_fake_endpoint -- --ignored --nocapture`
#[test]
#[ignore = "synthetic benchmark; run it by name with --ignored --nocapture on a release build"]
fn synthetic_benchmark_tds_rows_from_the_fake_endpoint() {
    const ROW_PAYLOAD: usize = 2048;
    for (hex_megabytes, iterations) in [(2usize, 21usize), (62, 7)] {
        let rows = hex_megabytes * 1024 * 1024 / 2 / ROW_PAYLOAD;
        let encoded: Vec<Vec<u8>> = (0..rows)
            .map(|id| {
                let payload: Vec<u8> = (0..ROW_PAYLOAD).map(|n| (n * 31 + id) as u8).collect();
                row(&[
                    Cell::Int(38),
                    Cell::Int(id as i32),
                    Cell::Int(1),
                    Cell::IntN(Some(ROW_PAYLOAD as i32)),
                    Cell::Plp {
                        value: Some(&payload),
                        chunk: ROW_PAYLOAD,
                        sized: true,
                    },
                    Cell::IntN(Some(0)),
                    Cell::Plp {
                        value: None,
                        chunk: 1,
                        sized: true,
                    },
                ])
            })
            .collect();
        let tokens = result_set(
            &[
                Column::Int,
                Column::Int,
                Column::Int,
                Column::IntN,
                Column::VarBinaryMax,
                Column::IntN,
                Column::VarBinaryMax,
            ],
            &encoded,
        );
        let server = replying(tokens);
        let mut session = connect(&server);
        let mut samples: Vec<f64> = (0..iterations)
            .map(|_| {
                let started = std::time::Instant::now();
                let read = session
                    .query_rows("SET NOCOUNT ON; SELECT 1")
                    .expect("rows");
                let elapsed = started.elapsed().as_secs_f64() * 1000.0;
                assert_eq!(read.len(), rows);
                elapsed
            })
            .collect();
        samples.sort_by(f64::total_cmp);
        println!(
            "synthetic TDS rows from the fake endpoint (loopback, no TLS): {} MB of payload in {rows} rows, {iterations} iterations: median {:.3} ms (min {:.3})",
            hex_megabytes / 2,
            samples[samples.len() / 2],
            samples[0]
        );
    }
}

#[test]
fn a_model_part_prefetch_reads_every_shard_over_tds() {
    // The vocabulary of a real Knowledge Base, so the client serves the part types it asks for.
    let types = UdmClient::new_in_memory()
        .get_entity_types()
        .expect("seed vocabulary");
    let procedure = types
        .iter()
        .find(|t| t.name == "Procedure")
        .expect("Procedure type")
        .entity_type_id;
    let part = stored_part();
    let (served_types, served_part) = (types.clone(), part.clone());
    let server = FakeServer::tcp(Script::replying(move |sql| {
        Reply::Tokens(if sql.contains("FROM EntityType") {
            let rows: Vec<Vec<u8>> = served_types
                .iter()
                .map(|t| {
                    row(&[
                        Cell::Int(t.entity_type_id),
                        Cell::Short(Some(&wide(&t.name))),
                        Cell::Short(Some(&wide(&t.namespace))),
                    ])
                })
                .collect();
            result_set(&[Column::Int, Column::NVarChar, Column::NVarChar], &rows)
        } else if sql.contains("ModelEntityVersionName FROM ModelEntityVersion") {
            // Eight objects, two per shard.
            let rows: Vec<Vec<u8>> = (1..=8)
                .map(|id| {
                    row(&[
                        Cell::Int(procedure),
                        Cell::Int(id),
                        Cell::Int(1),
                        Cell::Short(Some(&wide(&format!("Proc{id}")))),
                    ])
                })
                .collect();
            result_set(
                &[Column::Int, Column::Int, Column::Int, Column::NVarChar],
                &rows,
            )
        } else if sql.contains("SELECT c.CompoundEntityTypeId") {
            result_set(&[Column::Int; 6], &[])
        } else {
            assert!(
                sql.contains(", e.EntityVersionData, ") && !sql.contains("CONVERT(VARCHAR(MAX)"),
                "{sql}"
            );
            let shard: i32 = sql.rsplit(" = ").next().unwrap().parse().expect("shard");
            let rows: Vec<Vec<u8>> = (1..=8)
                .filter(|id| id % 4 == shard)
                .map(|id| {
                    row(&[
                        Cell::Int(procedure),
                        Cell::Int(id),
                        Cell::Int(1),
                        Cell::IntN(Some(served_part.len() as i32)),
                        Cell::Plp {
                            value: Some(&served_part),
                            chunk: 900,
                            sized: false,
                        },
                        Cell::IntN(Some(0)),
                        Cell::Plp {
                            value: None,
                            chunk: 1,
                            sized: true,
                        },
                    ])
                })
                .collect();
            result_set(
                &[
                    Column::Int,
                    Column::Int,
                    Column::Int,
                    Column::IntN,
                    Column::VarBinaryMax,
                    Column::IntN,
                    Column::VarBinaryMax,
                ],
                &rows,
            )
        })
    }));
    let info = sql_info(&server);
    SqlTransport::request_tds_for_test(&info, unencrypted());
    let client = UdmClient::from_connection_info(info).with_persistent_reads();
    client.prefetch_model_parts(1).expect("prefetch");

    // Every part is in memory now: no statement is sent for them.
    let statements = server.log().batches.len();
    for id in 1..=8 {
        let record = client
            .get_entity_version(procedure, id, 1)
            .expect("read")
            .expect("part");
        assert_eq!(record.data, part, "part {id}");
        assert_eq!(record.properties, None);
    }
    let log = server.log();
    assert_eq!(log.batches.len(), statements);
    assert_eq!(
        log.batches
            .iter()
            .filter(|sql| sql.contains("e.EntityVersionData"))
            .count(),
        4
    );
    // The shared session and one session per other shard, all of them TDS logins.
    assert_eq!(log.logins.len(), 4);
}
