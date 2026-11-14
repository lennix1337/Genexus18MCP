//! `gx-engine serve`: one long-lived engine process that runs the CLI's own commands on request.
//!
//! The host (the MCP Gateway) writes one JSON line per request on stdin:
//!
//! ```text
//! {"id":"<8..64 lowercase hex digits>","args":["--diagnostics=json","kb-query","C:\\KBs\\X","--type","Procedure"]}
//! ```
//!
//! `args` is the argument list of the one-shot command line. The process runs it through the same
//! function `main` runs, so the command writes its own stdout and stderr. On each stream the command's
//! output sits between a begin line and, after one line break, a trailer line:
//!
//! ```text
//! stdout: {"gxe":"serve-begin","id":"<id>"}\n<command stdout>\n{"gxe":"serve-end","id":"<id>","exit":<exit code>}\n
//! stderr: {"gxe":"serve-begin","id":"<id>"}\n<command stderr>\n{"gxe":"serve-end","id":"<id>"}\n
//! ```
//!
//! What sits between them is, byte for byte, what the one-shot command writes, and `exit` is its exit
//! code. The id is chosen by the host per request, so stored Knowledge Base text cannot forge a
//! trailer, and a stream that does not start with the begin line is out of step: the host stops using
//! the process. The first stdout line of the process is `{"gxe":"serve-ready","protocol":1,...}`.
//!
//! Freshness: nothing read from a Knowledge Base is kept between requests. Every request opens its own
//! SQL session and read transaction and loads what it needs, exactly as the one-shot command does, so
//! an answer cannot be older than the request. A catalog kept in memory is left out on purpose: the
//! change key in `catalog_cache.rs` does not identify catalog content (`CACHE_ENABLED` is off for that
//! reason), and no cheaper proof of freshness is established.
//!
//! A request that is not valid ends the process with exit code 2 before anything runs: the channel is no
//! longer trusted, and the host falls back to one-shot commands. End of input ends the process normally.

use std::io::{BufRead, Read, Write};
use std::process::ExitCode;

/// Version of the request and response framing. The host refuses a process that announces another.
pub const PROTOCOL: u32 = 1;

/// The commands the MCP Gateway issues per call. Nothing else is served: `run` hands the child this
/// process's stdin, and `kb-write` stays a one-shot command.
pub const SERVED_COMMANDS: [&str; 9] = [
    "kb-read",
    "kb-query",
    "kb-environment",
    "kb-list-procedures",
    "kb-list-objects",
    "kb-versions",
    "kb-inspect",
    "kb-diagnose",
    "rebuild-all",
];

/// The largest request line accepted, in bytes.
const MAX_REQUEST_BYTES: usize = 1 << 20;

/// Exit code of a process that stopped on a request it could not accept.
const EXIT_PROTOCOL: u8 = 2;

#[derive(Debug, serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct Request {
    id: String,
    args: Vec<String>,
}

/// Runs the resident loop on the process's own streams. `run_command` is the function `main` runs for
/// a one-shot command line.
pub fn run(args: &[String], run_command: fn(Vec<String>) -> ExitCode) -> ExitCode {
    if !args.is_empty() {
        eprintln!(
            "gx-engine: serve takes no arguments; it reads one JSON request per line on stdin: {{\"id\":\"<hex>\",\"args\":[...]}}"
        );
        return ExitCode::FAILURE;
    }
    let served = serve(
        std::io::stdin().lock(),
        std::io::stdout(),
        std::io::stderr(),
        |args| exit_status(run_command(args)),
    );
    match served {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("gx-engine serve: {error}");
            ExitCode::from(EXIT_PROTOCOL)
        }
    }
}

/// The numeric value of `code`: `ExitCode` is opaque, and the trailer reports the number.
fn exit_status(code: ExitCode) -> u8 {
    (0..=u8::MAX)
        .find(|value| ExitCode::from(*value) == code)
        .unwrap_or(1)
}

/// The resident loop. `run` executes one argument list and writes the command's output to the same
/// streams as `out` and `err`; its return value is the command's exit code.
fn serve<R: BufRead, O: Write, E: Write>(
    mut input: R,
    mut out: O,
    mut err: E,
    mut run: impl FnMut(Vec<String>) -> u8,
) -> Result<(), String> {
    let ready = format!(
        "{{\"gxe\":\"serve-ready\",\"protocol\":{PROTOCOL},\"version\":\"{}\"}}\n",
        env!("CARGO_PKG_VERSION")
    );
    write_frame(&mut out, &ready)?;
    let mut line = String::new();
    loop {
        line.clear();
        let read = (&mut input)
            .take(MAX_REQUEST_BYTES as u64 + 1)
            .read_line(&mut line)
            .map_err(|error| format!("cannot read a request: {error}"))?;
        if read == 0 {
            return Ok(());
        }
        let request = parse_request(&line)?;
        // Anything a stream carries before this line was not written for this request.
        let begin = format!("{{\"gxe\":\"serve-begin\",\"id\":\"{}\"}}\n", request.id);
        write_frame(&mut err, &begin)?;
        write_frame(&mut out, &begin)?;
        let exit = run(request.args);
        // The stderr trailer goes first: when the host sees the stdout trailer, stderr is complete.
        write_frame(
            &mut err,
            &format!("\n{{\"gxe\":\"serve-end\",\"id\":\"{}\"}}\n", request.id),
        )?;
        write_frame(
            &mut out,
            &format!(
                "\n{{\"gxe\":\"serve-end\",\"id\":\"{}\",\"exit\":{exit}}}\n",
                request.id
            ),
        )?;
    }
}

fn write_frame(stream: &mut impl Write, frame: &str) -> Result<(), String> {
    stream
        .write_all(frame.as_bytes())
        .and_then(|()| stream.flush())
        .map_err(|error| format!("cannot write to the host: {error}"))
}

/// One complete, valid request line. A line cut by end of input is refused: a request the host did not
/// finish writing must never run.
fn parse_request(line: &str) -> Result<Request, String> {
    if line.len() > MAX_REQUEST_BYTES {
        return Err(format!("a request exceeds {MAX_REQUEST_BYTES} bytes"));
    }
    let Some(text) = line.strip_suffix('\n') else {
        return Err("the input ended inside a request line".into());
    };
    let request: Request = serde_json::from_str(text.trim_end_matches('\r'))
        .map_err(|error| format!("a request is not {{\"id\",\"args\"}} JSON: {error}"))?;
    let id_ok = (8..=64).contains(&request.id.len())
        && request
            .id
            .bytes()
            .all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f'));
    if !id_ok {
        return Err("a request id must be 8 to 64 lowercase hexadecimal digits".into());
    }
    let command = request
        .args
        .iter()
        .find(|argument| *argument != "--diagnostics=json");
    match command {
        Some(command) if SERVED_COMMANDS.contains(&command.as_str()) => Ok(request),
        Some(command) => Err(format!(
            "serve does not run {command:?}; it serves {}",
            SERVED_COMMANDS.join(", ")
        )),
        None => Err("a request names no command".into()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    use std::rc::Rc;

    /// A stream the loop and the fake command share, as the process's stdout or stderr is shared.
    #[derive(Clone, Default)]
    struct Shared(Rc<RefCell<Vec<u8>>>);

    impl Write for Shared {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            self.0.borrow_mut().extend_from_slice(bytes);
            Ok(bytes.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    impl Shared {
        fn text(&self) -> String {
            String::from_utf8(self.0.borrow().clone()).unwrap()
        }
    }

    const READY: &str = concat!(
        "{\"gxe\":\"serve-ready\",\"protocol\":1,\"version\":\"",
        env!("CARGO_PKG_VERSION"),
        "\"}\n"
    );

    fn request(id: &str, args: &[&str]) -> String {
        format!("{}\n", serde_json::json!({ "id": id, "args": args }))
    }

    /// Runs the loop over `input` with a command that writes `stdout`, `stderr` and returns `exit` for
    /// each call; returns what the host would read and the argument lists that ran.
    fn drive(
        input: &str,
        mut command: impl FnMut(&[String]) -> (String, String, u8),
    ) -> (Result<(), String>, String, String, Vec<Vec<String>>) {
        let out = Shared::default();
        let err = Shared::default();
        let mut ran = Vec::new();
        let (mut command_out, mut command_err) = (out.clone(), err.clone());
        let result = serve(input.as_bytes(), out.clone(), err.clone(), |args| {
            let (stdout, stderr, exit) = command(&args);
            command_out.write_all(stdout.as_bytes()).unwrap();
            command_err.write_all(stderr.as_bytes()).unwrap();
            ran.push(args);
            exit
        });
        (result, out.text(), err.text(), ran)
    }

    #[test]
    fn each_request_is_framed_by_a_begin_line_and_a_trailer_that_carry_the_id_and_exit_code() {
        let input = request("aaaaaaaa", &["kb-query", "kb"])
            + &request("bbbbbbbb", &["--diagnostics=json", "kb-read", "kb", "P"]);
        let mut calls = 0;
        let (result, out, err, ran) = drive(&input, |_| {
            calls += 1;
            if calls == 1 {
                ("{\"objects\":[]}\n".into(), String::new(), 0)
            } else {
                (String::new(), "{\"category\":\"input\"}\n".into(), 4)
            }
        });
        assert_eq!(result, Ok(()));
        assert_eq!(
            out,
            format!(
                "{READY}{{\"gxe\":\"serve-begin\",\"id\":\"aaaaaaaa\"}}\n{{\"objects\":[]}}\n\n{{\"gxe\":\"serve-end\",\"id\":\"aaaaaaaa\",\"exit\":0}}\n{{\"gxe\":\"serve-begin\",\"id\":\"bbbbbbbb\"}}\n\n{{\"gxe\":\"serve-end\",\"id\":\"bbbbbbbb\",\"exit\":4}}\n"
            )
        );
        assert_eq!(
            err,
            "{\"gxe\":\"serve-begin\",\"id\":\"aaaaaaaa\"}\n\n{\"gxe\":\"serve-end\",\"id\":\"aaaaaaaa\"}\n{\"gxe\":\"serve-begin\",\"id\":\"bbbbbbbb\"}\n{\"category\":\"input\"}\n\n{\"gxe\":\"serve-end\",\"id\":\"bbbbbbbb\"}\n"
        );
        assert_eq!(
            ran,
            vec![
                vec!["kb-query".to_string(), "kb".to_string()],
                vec![
                    "--diagnostics=json".to_string(),
                    "kb-read".to_string(),
                    "kb".to_string(),
                    "P".to_string()
                ],
            ]
        );
    }

    #[test]
    fn output_without_a_final_line_break_is_recovered_exactly() {
        let (_, out, _, _) = drive(&request("aaaaaaaa", &["kb-query", "kb"]), |_| {
            ("no line break".into(), String::new(), 0)
        });
        let framed = out.strip_prefix(READY).unwrap();
        let payload = framed
            .strip_prefix("{\"gxe\":\"serve-begin\",\"id\":\"aaaaaaaa\"}\n")
            .unwrap()
            .strip_suffix("\n{\"gxe\":\"serve-end\",\"id\":\"aaaaaaaa\",\"exit\":0}\n")
            .unwrap();
        assert_eq!(payload, "no line break");
    }

    #[test]
    fn end_of_input_ends_the_loop_and_a_cut_request_never_runs() {
        let (result, out, _, ran) = drive("", |_| unreachable!());
        assert_eq!(result, Ok(()));
        assert_eq!(out, READY);
        assert!(ran.is_empty());

        let cut = request("aaaaaaaa", &["rebuild-all", "--kb", "kb"]);
        let cut = cut.trim_end_matches('\n');
        let (result, out, _, ran) = drive(cut, |_| unreachable!());
        assert_eq!(result.unwrap_err(), "the input ended inside a request line");
        assert_eq!(out, READY);
        assert!(ran.is_empty());
    }

    #[test]
    fn a_request_outside_the_contract_stops_the_loop_before_anything_runs() {
        let oversized = format!(
            "{{\"id\":\"aaaaaaaa\",\"args\":[\"kb-query\",\"{}\"]}}\n",
            "x".repeat(MAX_REQUEST_BYTES)
        );
        for (input, expected) in [
            ("not json\n".to_string(), "is not {\"id\",\"args\"} JSON"),
            (
                "{\"id\":\"aaaaaaaa\",\"args\":[\"kb-query\"],\"extra\":1}\n".to_string(),
                "is not {\"id\",\"args\"} JSON",
            ),
            (request("short", &["kb-query"]), "request id must be"),
            (request("AAAAAAAA", &["kb-query"]), "request id must be"),
            (request("aaaaaaa\"", &["kb-query"]), "request id must be"),
            (request("aaaaaaaa", &[]), "names no command"),
            (
                request("aaaaaaaa", &["--diagnostics=json"]),
                "names no command",
            ),
            (request("aaaaaaaa", &["kb-write", "kb"]), "does not run"),
            (request("aaaaaaaa", &["run", "p.json"]), "does not run"),
            (request("aaaaaaaa", &["serve"]), "does not run"),
            (oversized, "exceeds"),
        ] {
            let later = request("bbbbbbbb", &["kb-query", "kb"]);
            let (result, out, err, ran) = drive(&(input.clone() + &later), |_| unreachable!());
            let error = result.unwrap_err();
            assert!(error.contains(expected), "{input:.80}: {error}");
            assert_eq!(out, READY, "{input:.80}");
            assert!(err.is_empty(), "{input:.80}");
            assert!(ran.is_empty(), "{input:.80}");
        }
    }

    #[test]
    fn every_command_the_gateway_issues_is_served() {
        for command in SERVED_COMMANDS {
            let (result, _, _, ran) = drive(&request("aaaaaaaa", &[command]), |_| {
                (String::new(), String::new(), 0)
            });
            assert_eq!(result, Ok(()), "{command}");
            assert_eq!(ran.len(), 1, "{command}");
        }
    }

    #[test]
    fn exit_status_reports_the_number_main_would_exit_with() {
        assert_eq!(exit_status(ExitCode::SUCCESS), 0);
        assert_eq!(exit_status(ExitCode::FAILURE), 1);
        for value in [3, 4, 5, 255] {
            assert_eq!(exit_status(ExitCode::from(value)), value);
        }
    }
}
