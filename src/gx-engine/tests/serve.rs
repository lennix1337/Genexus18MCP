//! `gx-engine serve` against the one-shot command line, through the real binary.
//!
//! Every comparison runs one argument list twice: as a one-shot process and as a request to a resident
//! process. Stdout, stderr and the exit code must be the same bytes and the same number.
//!
//! The Knowledge Base of the read tests is the in-memory store (`udm/mock.rs`), saved as a mock
//! Knowledge Base folder that debug builds of the binary open (`udm/client/mock_kb.rs`); no SQL Server
//! is involved, so these tests say nothing about `sqlcmd` or a live database.

#![forbid(unsafe_code)]

use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{Receiver, channel};
use std::time::Duration;

static NEXT: AtomicU64 = AtomicU64::new(0);

/// A scratch folder removed when the test ends.
struct Scratch(PathBuf);

impl Scratch {
    fn new(label: &str) -> Self {
        let path = std::env::temp_dir().join(format!(
            "gx-serve-{label}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        let _ = std::fs::remove_dir_all(&path);
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }

    fn path(&self) -> String {
        self.0.to_string_lossy().into_owned()
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// What one command produced.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Outcome {
    stdout: String,
    stderr: String,
    exit: i32,
}

/// The engine with no SQL tool reachable and no inherited SQL credentials: a test never starts sqlcmd.
fn engine(no_tools: &Path) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_gx-engine"));
    command
        .env("PATH", no_tools)
        .env_remove("SQLCMDUSER")
        .env_remove("SQLCMDPASSWORD")
        .env_remove("GXMCP_JSON_REPORT");
    command
}

fn one_shot(no_tools: &Path, args: &[String]) -> Outcome {
    let output = engine(no_tools)
        .args(args)
        .stdin(Stdio::null())
        .output()
        .expect("run gx-engine");
    Outcome {
        stdout: String::from_utf8(output.stdout).expect("stdout is UTF-8"),
        stderr: String::from_utf8(output.stderr).expect("stderr is UTF-8"),
        exit: output.status.code().expect("exit code"),
    }
}

/// Lines of one stream, read on a thread so a full pipe never blocks the process.
fn lines_of(stream: impl Read + Send + 'static) -> Receiver<Vec<u8>> {
    let (sender, receiver) = channel();
    std::thread::spawn(move || {
        let mut reader = BufReader::new(stream);
        loop {
            let mut line = Vec::new();
            match reader.read_until(b'\n', &mut line) {
                Ok(0) | Err(_) => return,
                Ok(_) => {
                    if sender.send(line).is_err() {
                        return;
                    }
                }
            }
        }
    });
    receiver
}

/// A resident `gx-engine serve` process and the host side of its protocol.
struct Resident {
    child: Child,
    input: Option<ChildStdin>,
    stdout: Receiver<Vec<u8>>,
    stderr: Receiver<Vec<u8>>,
    next_id: u64,
}

impl Resident {
    fn start(no_tools: &Path) -> Self {
        let mut child = engine(no_tools)
            .arg("serve")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .expect("start gx-engine serve");
        let stdout = lines_of(child.stdout.take().unwrap());
        let stderr = lines_of(child.stderr.take().unwrap());
        let input = child.stdin.take();
        let ready = stdout
            .recv_timeout(Duration::from_secs(60))
            .expect("the ready line");
        assert_eq!(
            String::from_utf8(ready).unwrap(),
            format!(
                "{{\"gxe\":\"serve-ready\",\"protocol\":1,\"version\":\"{}\"}}\n",
                env!("CARGO_PKG_VERSION")
            )
        );
        Self {
            child,
            input,
            stdout,
            stderr,
            next_id: 0,
        }
    }

    fn send(&mut self, line: &str) {
        let input = self.input.as_mut().expect("stdin is open");
        input.write_all(line.as_bytes()).unwrap();
        input.flush().unwrap();
    }

    /// One answer on one stream: the begin line of request `id`, then everything up to the trailer line
    /// that starts with `trailer_start`. Returns the command's bytes and the rest of the trailer line.
    fn frame(stream: &Receiver<Vec<u8>>, id: &str, trailer_start: &str) -> (String, String) {
        let begin = stream
            .recv_timeout(Duration::from_secs(120))
            .expect("a response begin line");
        assert_eq!(
            String::from_utf8(begin).unwrap(),
            format!("{{\"gxe\":\"serve-begin\",\"id\":\"{id}\"}}\n"),
            "an answer starts with its begin line"
        );
        let mut bytes = Vec::new();
        loop {
            let line = stream
                .recv_timeout(Duration::from_secs(120))
                .expect("a response trailer");
            if let Some(rest) = std::str::from_utf8(&line)
                .ok()
                .and_then(|text| text.strip_prefix(trailer_start))
            {
                assert_eq!(bytes.pop(), Some(b'\n'), "the trailer follows a line break");
                return (
                    String::from_utf8(bytes).expect("the stream is UTF-8"),
                    rest.to_string(),
                );
            }
            bytes.extend_from_slice(&line);
        }
    }

    fn request(&mut self, args: &[String]) -> Outcome {
        self.next_id += 1;
        let id = format!("{:016x}", 0x5eed_0000_0000_0000_u64 + self.next_id);
        self.send(&format!(
            "{}\n",
            serde_json::json!({ "id": id, "args": args })
        ));
        let (stdout, exit) = Self::frame(
            &self.stdout,
            &id,
            &format!("{{\"gxe\":\"serve-end\",\"id\":\"{id}\",\"exit\":"),
        );
        let (stderr, rest) = Self::frame(
            &self.stderr,
            &id,
            &format!("{{\"gxe\":\"serve-end\",\"id\":\"{id}\""),
        );
        assert_eq!(rest, "}\n");
        Outcome {
            stdout,
            stderr,
            exit: exit
                .strip_suffix("}\n")
                .and_then(|code| code.parse().ok())
                .expect("the trailer carries the exit code"),
        }
    }

    /// Closes stdin and returns the exit code and what was left on the two streams.
    fn finish(mut self) -> (i32, String, String) {
        drop(self.input.take());
        let status = self.child.wait().expect("wait for gx-engine serve");
        let rest = |stream: &Receiver<Vec<u8>>| {
            let mut bytes = Vec::new();
            while let Ok(line) = stream.recv_timeout(Duration::from_secs(30)) {
                bytes.extend_from_slice(&line);
            }
            String::from_utf8(bytes).unwrap()
        };
        (
            status.code().expect("exit code"),
            rest(&self.stdout),
            rest(&self.stderr),
        )
    }
}

impl Drop for Resident {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn args(values: &[&str]) -> Vec<String> {
    values.iter().map(|value| value.to_string()).collect()
}

/// Reads against the mock Knowledge Base. Debug builds only: a release binary has no mock folder.
#[cfg(debug_assertions)]
mod mock_kb {
    use super::*;
    use gx_engine::catalog::{
        KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    };
    use gx_engine::kb_ops::write_object_part;
    use gx_engine::udm::{UdmClient, UdmWriter};
    use std::sync::Arc;

    fn object(name: &str, kind: ObjectKind, parts: &[(&str, &str)]) -> KbObject {
        let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, kind);
        for (part, source) in parts {
            object.source_parts.push(SourcePart {
                name: part.to_string(),
                source: source.to_string(),
            });
        }
        object
    }

    /// The store the tests in `tests/kb_ops.rs` use, plus one Procedure with a syntax error.
    fn seeded_client() -> UdmClient {
        let client = UdmClient::new_in_memory();
        let writer = UdmWriter::new(Arc::new(client.clone()));
        writer
            .create_object(&object(
                "PAlpha",
                ObjectKind::Procedure,
                &[("SOURCE", "&x = 1\nReturn &x"), ("RULES", "parm(&x);")],
            ))
            .expect("create PAlpha");
        let mut beta = object("PBeta", ObjectKind::Procedure, &[("SOURCE", "// beta")]);
        beta.references.push(Reference {
            kind: ReferenceKind::Calls,
            target: ObjectId::new("PAlpha").unwrap(),
        });
        writer.create_object(&beta).expect("create PBeta");
        writer
            .create_object(&object(
                "PBroken",
                ObjectKind::Procedure,
                &[("SOURCE", "&x = 1\n&y = (2 +\n")],
            ))
            .expect("create PBroken");
        writer
            .create_object(&object(
                "TGamma",
                ObjectKind::Transaction,
                &[("STRUCTURE", "<Structure/>")],
            ))
            .expect("create TGamma");
        client
    }

    /// Saves `client` as the mock Knowledge Base of `kb_dir`, with an `ObjectSaved` timestamp for every
    /// active object: the writer stores none, and the listing refuses an object without one.
    fn save(client: &UdmClient, kb_dir: &Path) {
        client.save_mock_kb(kb_dir).unwrap();
        let file = kb_dir.join(gx_engine::udm::client::mock_kb::MOCK_KB_FILE);
        let mut store: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(&file).unwrap()).unwrap();
        let timestamps: Vec<serde_json::Value> = store["model_entity_versions"]
            .as_array()
            .unwrap()
            .iter()
            .map(|row| {
                let id = |field: &str| row[field].as_i64().unwrap();
                serde_json::json!([
                    [
                        id("model_id"),
                        id("entity_type_id"),
                        id("entity_id"),
                        id("entity_version_id")
                    ],
                    638_000_000_000_000_000_i64 + id("entity_id") * 10 + id("entity_version_id")
                ])
            })
            .collect();
        store["object_saved_timestamps"] = serde_json::Value::Array(timestamps);
        let staged = kb_dir.join("store.tmp");
        std::fs::write(&staged, store.to_string()).unwrap();
        std::fs::rename(&staged, &file).unwrap();
    }

    /// Every command the Gateway issues, in the shapes it issues them, with refusals among them.
    fn requests(kb: &str) -> Vec<(Vec<String>, i32)> {
        let json = "--diagnostics=json";
        vec![
            (args(&[json, "kb-read", kb, "PAlpha"]), 0),
            (
                args(&[json, "kb-read", kb, "PAlpha", "--parts", "Source"]),
                0,
            ),
            (
                args(&[json, "kb-read", kb, "TGamma", "--parts", "Rules"]),
                0,
            ),
            (args(&[json, "kb-read", kb, "PMissing"]), 1),
            (
                args(&[
                    json, "kb-read", kb, "PAlpha", "--parts", "Source", "--page", "--offset", "0",
                    "--limit", "1", "--client", "ide",
                ]),
                1,
            ),
            (args(&[json, "kb-query", kb]), 0),
            (
                args(&[json, "kb-query", kb, "--type", "Procedure", "--limit", "2"]),
                0,
            ),
            (args(&[json, "kb-query", kb, "--name", "PAlpha"]), 0),
            (args(&[json, "kb-query", kb, "--usedby", "PAlpha"]), 0),
            (args(&[json, "kb-query", kb, "--type", "NoSuchType"]), 1),
            (
                args(&[
                    json,
                    "kb-list-procedures",
                    kb,
                    "--offset",
                    "0",
                    "--limit",
                    "2",
                ]),
                0,
            ),
            (
                args(&[
                    json,
                    "kb-list-procedures",
                    kb,
                    "--offset",
                    "1",
                    "--limit",
                    "200",
                ]),
                0,
            ),
            (args(&[json, "kb-list-procedures", kb, "--limit", "201"]), 1),
            (
                args(&[
                    json,
                    "kb-list-objects",
                    kb,
                    "--type",
                    "Transaction",
                    "--offset",
                    "0",
                    "--limit",
                    "5",
                ]),
                0,
            ),
            (args(&[json, "kb-diagnose", kb, "PAlpha"]), 0),
            (args(&[json, "kb-diagnose", kb, "PBroken"]), 0),
            (args(&[json, "kb-diagnose", kb, "TGamma"]), 0),
            // The in-memory store has no environment names: a refusal both paths must word alike.
            (args(&[json, "kb-environment", kb]), 1),
            // A version the store does not have: the version-scope refusal, exit code 4.
            (
                args(&[
                    json,
                    "kb-read",
                    kb,
                    "PAlpha",
                    "--design-model",
                    "9",
                    "--kb-version",
                    "9",
                ]),
                4,
            ),
            (
                args(&[
                    json,
                    "kb-query",
                    kb,
                    "--design-model",
                    "9",
                    "--kb-version",
                    "9",
                ]),
                4,
            ),
            (args(&[json, "kb-query", kb, "--design-model", "9"]), 1),
            // Text diagnostics: the same commands without the JSON diagnostics prefix.
            (args(&["kb-read", kb, "PAlpha", "--parts", "Rules"]), 0),
            (args(&["kb-read", kb, "PMissing"]), 1),
            (args(&["kb-query", kb, "--limit", "1"]), 0),
        ]
    }

    #[test]
    fn every_routed_read_answers_the_same_bytes_one_shot_and_resident() {
        let scratch = Scratch::new("parity");
        save(&seeded_client(), &scratch.0);
        let kb = scratch.path();

        let mut resident = Resident::start(&scratch.0);
        for (request, expected_exit) in requests(&kb) {
            let direct = one_shot(&scratch.0, &request);
            assert_eq!(
                direct.exit, expected_exit,
                "{request:?} one-shot: {direct:?}"
            );
            // A success answers on stdout and a failure on stderr, so the comparison is not of two
            // empty answers.
            if expected_exit == 0 {
                assert!(direct.stdout.starts_with('{'), "{request:?}: {direct:?}");
                assert!(direct.stderr.is_empty(), "{request:?}: {direct:?}");
            } else {
                assert!(direct.stdout.is_empty(), "{request:?}: {direct:?}");
                assert!(!direct.stderr.is_empty(), "{request:?}: {direct:?}");
            }
            assert_eq!(resident.request(&request), direct, "{request:?}");
        }
        assert_eq!(resident.finish(), (0, String::new(), String::new()));
    }

    #[test]
    fn a_knowledge_base_changed_between_two_requests_is_answered_as_changed() {
        let scratch = Scratch::new("stale");
        let client = seeded_client();
        save(&client, &scratch.0);
        let kb = scratch.path();
        let query = args(&["--diagnostics=json", "kb-query", &kb, "--type", "Procedure"]);
        let read = args(&[
            "--diagnostics=json",
            "kb-read",
            &kb,
            "PAlpha",
            "--parts",
            "Source",
        ]);
        let list = args(&[
            "--diagnostics=json",
            "kb-list-procedures",
            &kb,
            "--offset",
            "0",
            "--limit",
            "50",
        ]);
        let diagnose = args(&["--diagnostics=json", "kb-diagnose", &kb, "PAlpha"]);

        let mut resident = Resident::start(&scratch.0);
        let before: Vec<Outcome> = [&query, &read, &list, &diagnose]
            .into_iter()
            .map(|request| resident.request(request))
            .collect();
        assert!(!before[0].stdout.contains("PAdded"), "{:?}", before[0]);
        assert!(before[1].stdout.contains("Return &x"), "{:?}", before[1]);
        assert!(
            before[3].stdout.contains("\"status\":\"ok\""),
            "{:?}",
            before[3]
        );

        // Another process changes the Knowledge Base: a new object, and new text in an existing one.
        UdmWriter::new(Arc::new(client.clone()))
            .create_object(&object(
                "PAdded",
                ObjectKind::Procedure,
                &[("SOURCE", "// added")],
            ))
            .expect("create PAdded");
        write_object_part(&client, "PAlpha", "Source", "&y = (2 +\n", None, None)
            .expect("rewrite PAlpha");
        save(&client, &scratch.0);

        let after: Vec<Outcome> = [&query, &read, &list, &diagnose]
            .into_iter()
            .map(|request| resident.request(request))
            .collect();
        assert!(after[0].stdout.contains("PAdded"), "{:?}", after[0]);
        assert!(after[1].stdout.contains("(2 +"), "{:?}", after[1]);
        assert!(!after[1].stdout.contains("Return &x"), "{:?}", after[1]);
        assert!(after[2].stdout.contains("PAdded"), "{:?}", after[2]);
        assert!(
            after[3].stdout.contains("\"status\":\"errors\""),
            "{:?}",
            after[3]
        );
        // The same resident process answers what a fresh process answers now.
        for (request, answer) in [&query, &read, &list, &diagnose].into_iter().zip(&after) {
            assert_eq!(answer, &one_shot(&scratch.0, request), "{request:?}");
        }
        for (old, new) in before.iter().zip(&after) {
            assert_ne!(old.stdout, new.stdout);
        }
    }
}

/// Refusals that need no Knowledge Base at all: these run in release builds too.
#[test]
fn refusals_before_any_read_are_the_same_bytes_one_shot_and_resident() {
    let scratch = Scratch::new("refusals");
    let missing = scratch.0.join("no-such-kb").to_string_lossy().into_owned();
    // A Knowledge Base folder with SQL authentication and no credentials: sqlcmd is never started.
    let sql_auth = scratch.0.join("sql-auth");
    std::fs::create_dir_all(&sql_auth).unwrap();
    std::fs::write(
        sql_auth.join("knowledgebase.connection"),
        "<ConnectionInformation><ServerInstance>invalid-gxe-serve</ServerInstance><DBName>GX_KB_Serve</DBName><IntegratedSecurity>False</IntegratedSecurity></ConnectionInformation>",
    )
    .unwrap();
    let sql_auth = sql_auth.to_string_lossy().into_owned();
    let out = scratch.0.join("out").to_string_lossy().into_owned();
    let json = "--diagnostics=json";

    let mut cases: Vec<(Vec<String>, i32)> = Vec::new();
    for command in [
        "kb-read",
        "kb-inspect",
        "kb-diagnose",
        "kb-query",
        "kb-environment",
        "kb-list-procedures",
        "kb-versions",
    ] {
        let object: &[&str] = match command {
            "kb-read" | "kb-inspect" | "kb-diagnose" => &["PAlpha"],
            "kb-list-procedures" => &["--limit", "10"],
            _ => &[],
        };
        for kb in [&missing, &sql_auth] {
            let mut request = args(&[json, command, kb]);
            request.extend(args(object));
            // A query selects its Design model first: without credentials that is a version-scope
            // refusal (exit code 4), not an input error.
            let exit = if command == "kb-query" && kb == &sql_auth {
                4
            } else {
                1
            };
            cases.push((request, exit));
        }
    }
    cases.push((
        args(&[
            json,
            "kb-list-objects",
            &sql_auth,
            "--type",
            "Transaction",
            "--limit",
            "10",
        ]),
        1,
    ));
    cases.push((args(&["kb-read", &missing, "PAlpha"]), 1));
    cases.push((args(&[json, "kb-read", &missing]), 1));
    cases.push((args(&[json, "kb-query", &missing, "--kb-version", "2"]), 1));
    cases.push((
        args(&[json, "rebuild-all", "--kb", &missing, "--output", &out]),
        1,
    ));
    cases.push((args(&[json, "rebuild-all", "--output", &out]), 1));
    // The credentials preflight of a build: exit code 5, before any read or output.
    cases.push((
        args(&[json, "rebuild-all", "--kb", &sql_auth, "--output", &out]),
        5,
    ));

    let mut resident = Resident::start(&scratch.0);
    for (request, expected_exit) in cases {
        let direct = one_shot(&scratch.0, &request);
        assert_eq!(direct.exit, expected_exit, "{request:?}: {direct:?}");
        assert!(direct.stdout.is_empty(), "{request:?}: {direct:?}");
        assert!(!direct.stderr.is_empty(), "{request:?}: {direct:?}");
        assert_eq!(resident.request(&request), direct, "{request:?}");
    }
    assert!(!Path::new(&out).exists(), "a refused build writes nothing");
    assert_eq!(resident.finish(), (0, String::new(), String::new()));
}

/// A build from a portable catalog file, with its report: partial and failed runs.
#[test]
fn a_catalog_build_reports_and_emits_the_same_one_shot_and_resident() {
    use gx_engine::catalog::{Catalog, KbObject, ObjectId, ObjectKind, SourcePart};
    use std::collections::BTreeMap;

    let scratch = Scratch::new("build");
    let catalog_of = |file: &str, objects: &[(&str, ObjectKind, &str, &str)]| {
        let mut map = BTreeMap::new();
        for (name, kind, part, source) in objects {
            let id = ObjectId::new(name.to_ascii_lowercase()).unwrap();
            let mut object = KbObject::new(id.clone(), *name, *kind);
            object.source_parts.push(SourcePart {
                name: part.to_string(),
                source: source.to_string(),
            });
            map.insert(id, object);
        }
        let path = scratch.0.join(file);
        std::fs::write(&path, Catalog::from_objects_map(map).to_json().unwrap()).unwrap();
        path.to_string_lossy().into_owned()
    };
    let complete = catalog_of(
        "complete.json",
        &[("POne", ObjectKind::Procedure, "SOURCE", "&Page = 1\n")],
    );
    let partial = catalog_of(
        "partial.json",
        &[
            ("POne", ObjectKind::Procedure, "SOURCE", "&Page = 1\n"),
            (
                "WPanel",
                ObjectKind::WebPanel,
                "EVENTS",
                "Event Start\nEndEvent\n",
            ),
        ],
    );

    // Timings differ between any two runs, and each run has its own output folder.
    let normalized = |outcome: &Outcome, output: &str| {
        let scrub = |text: &str| {
            let text = text
                .replace(&output.replace('\\', "\\\\"), "<out>")
                .replace(output, "<out>");
            let mut lines = Vec::new();
            for line in text.lines() {
                let timing = line
                    .split_once("_ms\": ")
                    .is_some_and(|(_, value)| value.trim_end_matches(',').parse::<u64>().is_ok());
                lines.push(if timing {
                    format!("{}_ms\": <ms>", line.split_once("_ms\": ").unwrap().0)
                } else {
                    line.to_string()
                });
            }
            lines.join("\n")
        };
        (scrub(&outcome.stdout), scrub(&outcome.stderr), outcome.exit)
    };
    // The incremental build state records file modification times, which differ between two runs.
    let tree = |root: &Path| {
        let mut files = BTreeMap::new();
        let mut pending = vec![root.to_path_buf()];
        while let Some(dir) = pending.pop() {
            for entry in std::fs::read_dir(&dir).unwrap() {
                let path = entry.unwrap().path();
                if path.is_dir() {
                    if path.ends_with("obj/gx-incremental") {
                        continue;
                    }
                    pending.push(path);
                } else {
                    let name = path
                        .strip_prefix(root)
                        .unwrap()
                        .to_string_lossy()
                        .into_owned();
                    files.insert(name, std::fs::read(&path).unwrap());
                }
            }
        }
        files
    };

    let broken = catalog_of(
        "broken.json",
        &[("PBroken", ObjectKind::Procedure, "SOURCE", "&x = (2 +\n")],
    );

    let mut resident = Resident::start(&scratch.0);
    // `--skip-compile` leaves every run partial (exit code 3); a source that does not parse fails it.
    for (label, catalog, json_report, expected_exit) in [
        ("procedure", &complete, true, 3),
        ("webpanel", &partial, true, 3),
        ("broken", &broken, true, 1),
        ("text", &complete, false, 3),
    ] {
        let direct_out = scratch.0.join(format!("{label}-direct"));
        let resident_out = scratch.0.join(format!("{label}-resident"));
        let request = |output: &Path| {
            let mut request = if json_report {
                args(&["--diagnostics=json"])
            } else {
                Vec::new()
            };
            request.extend(args(&[
                "rebuild-all",
                "--kb",
                catalog,
                "--output",
                &output.to_string_lossy(),
                "--skip-compile",
            ]));
            request
        };
        let direct = one_shot(&scratch.0, &request(&direct_out));
        let served = resident.request(&request(&resident_out));
        if json_report {
            let report: serde_json::Value =
                serde_json::from_str(&direct.stdout).expect("the JSON report");
            assert!(report["total_ms"].is_u64(), "{label}: {report}");
        } else {
            assert!(
                direct.stdout.contains("Rebuild All completed in"),
                "{label}"
            );
        }
        if !json_report {
            // The text summary carries its timings inside one line; only the rest is compared.
            let tail = |text: &str| text.split_once('\n').map(|(_, rest)| rest.to_string());
            assert_eq!(
                tail(
                    &served
                        .stdout
                        .replace(&resident_out.to_string_lossy().into_owned(), "<out>")
                ),
                tail(
                    &direct
                        .stdout
                        .replace(&direct_out.to_string_lossy().into_owned(), "<out>")
                ),
                "{label}"
            );
            assert_eq!(
                (served.stderr.as_str(), served.exit),
                (direct.stderr.as_str(), direct.exit),
                "{label}"
            );
        } else {
            assert_eq!(
                normalized(&served, &resident_out.to_string_lossy()),
                normalized(&direct, &direct_out.to_string_lossy()),
                "{label}"
            );
        }
        assert_eq!(tree(&resident_out), tree(&direct_out), "{label}");
        assert!(
            !tree(&direct_out).is_empty(),
            "{label}: the build wrote files"
        );
        assert_eq!(direct.exit, expected_exit, "{label}: {direct:?}");
    }
    assert_eq!(resident.finish(), (0, String::new(), String::new()));
}

#[test]
fn a_request_serve_does_not_accept_ends_the_process_without_running_it() {
    let scratch = Scratch::new("protocol");
    let content = scratch.0.join("content.txt");
    std::fs::write(&content, "// never written").unwrap();
    for line in [
        "not a request\n".to_string(),
        format!(
            "{}\n",
            serde_json::json!({ "id": "00000000aaaaaaaa", "args": ["kb-write", scratch.path(), "PAlpha", "--part", "Source", "--content-file", content.to_string_lossy()] })
        ),
        format!(
            "{}\n",
            serde_json::json!({ "id": "00000000aaaaaaaa", "args": ["run", "project.json"] })
        ),
    ] {
        let mut resident = Resident::start(&scratch.0);
        resident.send(&line);
        let (exit, stdout, stderr) = resident.finish();
        assert_eq!(exit, 2, "{line}");
        assert!(
            stdout.is_empty(),
            "no trailer for a refused request: {stdout}"
        );
        assert!(stderr.starts_with("gx-engine serve: "), "{stderr}");
        assert!(!stderr.contains("serve-end"), "{stderr}");
    }
}

#[test]
fn serve_takes_no_arguments() {
    let scratch = Scratch::new("usage");
    let outcome = one_shot(&scratch.0, &args(&["serve", "--kb", "x"]));
    assert_eq!(outcome.exit, 1);
    assert!(outcome.stdout.is_empty());
    assert!(
        outcome.stderr.contains("serve takes no arguments"),
        "{outcome:?}"
    );
}
