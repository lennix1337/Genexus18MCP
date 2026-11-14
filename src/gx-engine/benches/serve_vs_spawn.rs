//! SYNTHETIC benchmark: one engine process per call against one resident `gx-engine serve` process.
//!
//! What it measures: the wall time of each request as a host sees it, for the same argument lists, on
//! this machine. What it does not measure: anything about a real Knowledge Base. No SQL Server, no
//! `sqlcmd`, no GeneXus install and no MCP Gateway take part, so these numbers say how much process
//! start costs here and nothing about read or build times on a Knowledge Base. For that, run
//! `scripts/validate-engine-resident-live.ps1` on a machine with a test Knowledge Base.
//!
//! Workloads:
//! - `refusal`: `kb-read` of a folder that does not exist. The command does nothing but start, parse
//!   its arguments and report the error, so the difference between the two paths is the process start.
//! - `catalog-build`: `rebuild-all --skip-compile` of a generated catalog file of Procedures (specify and
//!   emit C#, no compile), as a request that does real work without a database.
//!
//! Run: `cargo bench --manifest-path src/gx-engine/Cargo.toml --bench serve_vs_spawn`
//! Options after `--`: `--requests <n>` (refusal requests, default 200), `--builds <n>` (default 30),
//! `--procedures <n>` (catalog size, default 50).

use gx_engine::catalog::{Catalog, KbObject, ObjectId, ObjectKind, SourcePart};
use std::collections::BTreeMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, Command, ExitCode, Stdio};
use std::sync::mpsc::{Receiver, channel};
use std::time::{Duration, Instant};

const ENGINE: &str = env!("CARGO_BIN_EXE_gx-engine");

fn engine(no_tools: &Path) -> Command {
    let mut command = Command::new(ENGINE);
    // No SQL tool is reachable: the benchmark can never touch a database.
    command
        .env("PATH", no_tools)
        .env_remove("SQLCMDUSER")
        .env_remove("SQLCMDPASSWORD");
    command
}

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

/// The host side of the `serve` protocol (see `src/serve.rs`).
struct Resident {
    child: Child,
    input: ChildStdin,
    stdout: Receiver<Vec<u8>>,
    stderr: Receiver<Vec<u8>>,
    next_id: u64,
}

impl Resident {
    fn start(no_tools: &Path) -> Result<Self, String> {
        let mut child = engine(no_tools)
            .arg("serve")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|error| format!("cannot start gx-engine serve: {error}"))?;
        let stdout = lines_of(child.stdout.take().expect("piped stdout"));
        let stderr = lines_of(child.stderr.take().expect("piped stderr"));
        let input = child.stdin.take().expect("piped stdin");
        let ready = stdout
            .recv_timeout(Duration::from_secs(60))
            .map_err(|_| "gx-engine serve wrote no ready line".to_string())?;
        if !ready.starts_with(b"{\"gxe\":\"serve-ready\",\"protocol\":1,") {
            return Err("gx-engine serve announced another protocol".into());
        }
        Ok(Self {
            child,
            input,
            stdout,
            stderr,
            next_id: 0,
        })
    }

    fn frame(stream: &Receiver<Vec<u8>>, id: &str) -> Result<(Vec<u8>, String), String> {
        let begin = format!("{{\"gxe\":\"serve-begin\",\"id\":\"{id}\"}}\n");
        let end = format!("{{\"gxe\":\"serve-end\",\"id\":\"{id}\"");
        let first = stream
            .recv_timeout(Duration::from_secs(120))
            .map_err(|_| "no answer from gx-engine serve".to_string())?;
        if first != begin.as_bytes() {
            return Err("an answer did not start with its begin line".into());
        }
        let mut bytes = Vec::new();
        loop {
            let line = stream
                .recv_timeout(Duration::from_secs(120))
                .map_err(|_| "gx-engine serve ended inside an answer".to_string())?;
            if line.starts_with(end.as_bytes()) {
                bytes.pop();
                return Ok((bytes, String::from_utf8_lossy(&line[end.len()..]).into_owned()));
            }
            bytes.extend_from_slice(&line);
        }
    }

    fn request(&mut self, args: &[String]) -> Result<(Vec<u8>, Vec<u8>, i32), String> {
        self.next_id += 1;
        let id = format!("{:016x}", self.next_id);
        let line = format!("{}\n", serde_json::json!({ "id": id, "args": args }));
        self.input
            .write_all(line.as_bytes())
            .and_then(|()| self.input.flush())
            .map_err(|error| format!("cannot send a request: {error}"))?;
        let (stdout, trailer) = Self::frame(&self.stdout, &id)?;
        let (stderr, _) = Self::frame(&self.stderr, &id)?;
        let exit = trailer
            .trim()
            .strip_prefix(",\"exit\":")
            .and_then(|rest| rest.strip_suffix('}'))
            .and_then(|code| code.parse().ok())
            .ok_or("the trailer carries no exit code")?;
        Ok((stdout, stderr, exit))
    }
}

impl Drop for Resident {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn one_shot(no_tools: &Path, args: &[String]) -> Result<(Vec<u8>, Vec<u8>, i32), String> {
    let output = engine(no_tools)
        .args(args)
        .stdin(Stdio::null())
        .output()
        .map_err(|error| format!("cannot run gx-engine: {error}"))?;
    Ok((
        output.stdout,
        output.stderr,
        output.status.code().ok_or("no exit code")?,
    ))
}

struct Samples(Vec<Duration>);

impl Samples {
    fn of(count: usize, mut request: impl FnMut(usize) -> Result<(), String>) -> Result<Self, String> {
        let mut samples = Vec::with_capacity(count);
        for index in 0..count {
            let started = Instant::now();
            request(index)?;
            samples.push(started.elapsed());
        }
        samples.sort();
        Ok(Self(samples))
    }

    fn at(&self, fraction: f64) -> f64 {
        let index = ((self.0.len() - 1) as f64 * fraction).round() as usize;
        self.0[index].as_secs_f64() * 1000.0
    }

    fn total(&self) -> f64 {
        self.0.iter().sum::<Duration>().as_secs_f64() * 1000.0
    }

    fn report(&self, label: &str) {
        println!(
            "  {label:<22} n={:<4} median {:>8.3} ms   p90 {:>8.3} ms   min {:>8.3} ms   max {:>8.3} ms   total {:>9.1} ms",
            self.0.len(),
            self.at(0.5),
            self.at(0.9),
            self.at(0.0),
            self.at(1.0),
            self.total()
        );
    }
}

fn option(args: &[String], name: &str, default: usize) -> Result<usize, String> {
    match args.iter().position(|argument| argument == name) {
        None => Ok(default),
        Some(index) => args
            .get(index + 1)
            .and_then(|value| value.parse().ok())
            .filter(|value| *value > 0)
            .ok_or_else(|| format!("{name} expects a positive integer")),
    }
}

fn catalog_file(dir: &Path, procedures: usize) -> Result<PathBuf, String> {
    let mut objects = BTreeMap::new();
    for index in 0..procedures {
        let id = ObjectId::new(format!("p{index}"))?;
        let mut object = KbObject::new(id.clone(), format!("PBench{index}"), ObjectKind::Procedure);
        object.source_parts.push(SourcePart {
            name: "SOURCE".into(),
            source: format!("&Total = {index}\n&Total = &Total + 1\n"),
        });
        objects.insert(id, object);
    }
    let path = dir.join("catalog.json");
    std::fs::write(&path, Catalog::from_objects_map(objects).to_json()?)
        .map_err(|error| format!("cannot write the catalog: {error}"))?;
    Ok(path)
}

fn run() -> Result<(), String> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let requests = option(&args, "--requests", 200)?;
    let builds = option(&args, "--builds", 30)?;
    let procedures = option(&args, "--procedures", 50)?;

    let scratch = std::env::temp_dir().join(format!("gx-serve-bench-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&scratch);
    std::fs::create_dir_all(&scratch).map_err(|error| error.to_string())?;
    let text = |path: &Path| path.to_string_lossy().into_owned();

    println!("SYNTHETIC benchmark: one process per call vs one resident `gx-engine serve` process.");
    println!("No Knowledge Base, SQL Server, sqlcmd or Gateway is involved; these are not real-KB numbers.");
    println!("Engine binary: {ENGINE}");
    println!(
        "Build profile of this run: {}",
        if cfg!(debug_assertions) { "debug" } else { "release" }
    );

    let ready_started = Instant::now();
    let mut resident = Resident::start(&scratch)?;
    println!(
        "Resident process start to ready line (paid once): {:.3} ms",
        ready_started.elapsed().as_secs_f64() * 1000.0
    );

    // Workload 1: a refusal before any read.
    let refusal: Vec<String> = [
        "--diagnostics=json",
        "kb-read",
        &text(&scratch.join("no-such-kb")),
        "PAlpha",
    ]
    .map(String::from)
    .to_vec();
    let expected = one_shot(&scratch, &refusal)?;
    if resident.request(&refusal)? != expected || expected.2 != 1 {
        return Err("the refusal differs between the two paths".into());
    }
    println!("\nWorkload `refusal` (process start and argument handling only), {requests} requests each:");
    let spawned = Samples::of(requests, |_| one_shot(&scratch, &refusal).map(|_| ()))?;
    let served = Samples::of(requests, |_| resident.request(&refusal).map(|_| ()))?;
    spawned.report("one process per call");
    served.report("resident process");
    println!(
        "  median saved per request: {:.3} ms",
        spawned.at(0.5) - served.at(0.5)
    );

    // Workload 2: specify and emit a generated catalog, without compiling.
    let catalog = catalog_file(&scratch, procedures)?;
    let build = |output: &Path| -> Vec<String> {
        [
            "--diagnostics=json",
            "rebuild-all",
            "--kb",
            &text(&catalog),
            "--output",
            &text(output),
            "--skip-compile",
        ]
        .map(String::from)
        .to_vec()
    };
    let (spawn_out, serve_out) = (scratch.join("out-spawn"), scratch.join("out-serve"));
    let warm = one_shot(&scratch, &build(&spawn_out))?;
    if warm.2 != 3 || resident.request(&build(&serve_out))?.2 != 3 {
        return Err("the catalog build did not end as a partial (not compiled) run on both paths".into());
    }
    println!(
        "\nWorkload `catalog-build` (rebuild-all --skip-compile, {procedures} generated Procedures), {builds} requests each:"
    );
    let spawned = Samples::of(builds, |_| one_shot(&scratch, &build(&spawn_out)).map(|_| ()))?;
    let served = Samples::of(builds, |_| resident.request(&build(&serve_out)).map(|_| ()))?;
    spawned.report("one process per call");
    served.report("resident process");
    println!(
        "  median saved per request: {:.3} ms",
        spawned.at(0.5) - served.at(0.5)
    );

    drop(resident);
    let _ = std::fs::remove_dir_all(&scratch);
    Ok(())
}

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("serve_vs_spawn: {error}");
            ExitCode::FAILURE
        }
    }
}
