#![forbid(unsafe_code)]

mod kb_command;

use gx_engine::{
    build,
    diagnostics::{Diagnostic, DiagnosticCategory},
    model::Project,
};
use kb_command::parse_id_flag;
use std::{
    env,
    fs::File,
    io::Read,
    path::PathBuf,
    process::{Command, ExitCode},
    time::{Duration, Instant},
};

const HELP: &str = "gx-engine — independent compiler & build engine\n\nUsage:\n  gx-engine check <project.json>\n  gx-engine emit <project.json>\n  gx-engine explain <previous.json> <next.json>\n  gx-engine build <project.json> [output-directory]\n  gx-engine run <project.json> [output-directory]\n  gx-engine kb-check <catalog.json> <entry-id>\n  gx-engine kb-emit <catalog.json> <entry-id>\n  gx-engine kb-build <catalog.json> <entry-id> [output-directory]\n  gx-engine kb-run <catalog.json> <entry-id> [output-directory]\n  gx-engine kb-read <kb-path> <object> [--parts <Name,Name>] [--page --offset <n> --limit <n> --client mcp] [--design-model <id> --kb-version <id>]\n  gx-engine kb-query <kb-path> [--name <name>] [--type <ObjectType>] [--usedby <object>] [--limit <n>] [--design-model <id> --kb-version <id>]\n  gx-engine kb-environment <kb-path> [--design-model <id> --kb-version <id>]\n  gx-engine kb-list-procedures <kb-path> [--offset <n> --limit <1..200>] [--design-model <id> --kb-version <id>]\n  gx-engine kb-versions <kb-path> [--design-model <id> --kb-version <id>]\n  gx-engine kb-inspect <kb-path> <procedure-name> [--design-model <id> --kb-version <id>]\n  gx-engine kb-diagnose <kb-path> <procedure-name> [--design-model <id> --kb-version <id>]\n  gx-engine kb-write <kb-path> <object> --part <Part> --content-file <path> [--type <ObjectType>] [--design-model <id> --kb-version <id>]\n  gx-engine xml-check <authored.xml> <entry-name>\n  gx-engine xml-emit <authored.xml> <entry-name>\n  gx-engine xml-build <authored.xml> <entry-name> [output-directory]\n  gx-engine xml-run <authored.xml> <entry-name> [output-directory]\n  gx-engine rebuild-all --kb <path> --output <dir> [--target net8.0|net9.0] [--clean] [--concurrency <N>] [--skip-compile] [--object <name> [--closure]] [--catalog-cache <dir>] [--web-theme <name>] [--web-generator <banner>] [--web-master <Name=dotted.class.path>]... [--design-model <id> --kb-version <id>]\n  gx-engine --version\n\nkb-diagnose: parses one saved Procedure's Source with the engine front end and prints {name,type,status:ok|errors|unavailable,reason?,errors:[{line,column,message}]}. Syntax only (no specify or compile); any other object type or part is `unavailable`.\n\n--design-model/--kb-version: the Design model and KB version the GeneXus session has active. Without them only a KB with a single Design model (no versions) is served. A read serves the named version's Design model; a build covers only the trunk Design model 1 with every environment under it. Anything else is refused before any read or write, with exit code 4.\n\nrebuild-all --object <name>: builds that one object alone and is refused by name when it references another object. With --closure, the object and every object its references reach are built, as an executable whose entry point is the object's Main (a parameterless Procedure only). A reference the native reader or emitter does not cover is refused by name, with its path.\nrebuild-all exit codes: 0 complete; 3 partial (web UI objects skipped, a code kind with no generator present, or not compiled; the report lists them); 1 failed.\n\nPrefix a command with --diagnostics=json for structured errors on stderr.\nBuild diagnostics cover the whole build operation, including code generation.\nrun executes authored code with inherited console I/O and a 10-second deadline.\nSupports native UDM KB access with partial object coverage (refusals are named), C# (.NET) generator, and parallel Rebuild All.\n";

fn load(path: &str) -> Result<Project, String> {
    let file = File::open(path).map_err(|e| format!("cannot open project: {e}"))?;
    let mut text = String::new();
    file.take(1_048_577)
        .read_to_string(&mut text)
        .map_err(|e| format!("cannot read project: {e}"))?;
    if text.len() > 1_048_576 {
        return Err("portable project exceeds 1 MiB".into());
    }
    Project::from_json(&text)
}

struct CapturedStderr(PathBuf);

impl CapturedStderr {
    fn describe(&self, reason: String) -> String {
        let mut bytes = Vec::new();
        if let Err(error) =
            File::open(&self.0).and_then(|file| file.take(16_385).read_to_end(&mut bytes))
        {
            return format!("{reason}; cannot read generated program stderr: {error}");
        }
        let truncated = bytes.len() > 16_384;
        bytes.truncate(16_384);
        if bytes.is_empty() {
            return reason;
        }
        format!(
            "{reason}\n{}{}",
            String::from_utf8_lossy(&bytes),
            if truncated {
                "\n[generated program stderr truncated to 16384 bytes]"
            } else {
                ""
            }
        )
    }
}

impl Drop for CapturedStderr {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

fn execution_error(reason: String, capture: &Option<CapturedStderr>) -> String {
    match capture {
        Some(capture) => capture.describe(reason),
        None => reason,
    }
}

fn execute(
    args: Vec<String>,
    category: &mut DiagnosticCategory,
    json_errors: bool,
) -> Result<(), String> {
    if args.is_empty() || args == ["--help"] || args == ["-h"] {
        print!("{HELP}");
        return Ok(());
    }
    let raw_command = args[0].as_str();
    if matches!(
        raw_command,
        "kb-read"
            | "kb-query"
            | "kb-write"
            | "kb-environment"
            | "kb-list-procedures"
            | "kb-list-objects"
            | "kb-versions"
            | "kb-inspect"
            | "kb-diagnose"
    ) {
        return execute_kb_ops(raw_command, &args[1..], category);
    }
    let from_xml = raw_command.starts_with("xml-");
    let (command, from_objects) = match raw_command
        .strip_prefix("kb-")
        .or_else(|| raw_command.strip_prefix("xml-"))
    {
        Some(command) if matches!(command, "check" | "emit" | "build" | "run") => (command, true),
        _ => (raw_command, false),
    };
    if !matches!(command, "check" | "emit" | "explain" | "build" | "run") {
        return Err(format!("unknown command {command:?}; use --help"));
    }
    let max = if matches!(command, "build" | "run" | "explain") {
        3
    } else {
        2
    };
    let max = max + usize::from(from_objects);
    if args.len() < 2 + usize::from(from_objects) || args.len() > max {
        return Err("invalid arguments; use --help".into());
    }
    if command == "explain" && args.len() != 3 {
        return Err("explain requires previous and next project paths".into());
    }
    *category = DiagnosticCategory::Input;
    let project = if from_xml {
        let file = File::open(&args[1]).map_err(|e| format!("cannot open XML: {e}"))?;
        let mut input = String::new();
        file.take(gx_engine::exchange::MAX_XML_BYTES as u64 + 1)
            .read_to_string(&mut input)
            .map_err(|e| format!("cannot read XML: {e}"))?;
        gx_engine::exchange::project_from_xml(&input, &args[2], None)?
    } else if from_objects {
        let catalog = gx_engine::catalog::Catalog::load(&args[1])?;
        let entry = gx_engine::catalog::ObjectId::new(&args[2])?;
        gx_engine::kb::project_from_catalog(&catalog, &entry, None)?
    } else {
        load(&args[1])?
    };
    if command == "explain" {
        let next = load(&args[2])?;
        *category = DiagnosticCategory::Compilation;
        let mut session = gx_engine::incremental::IncrementalCompiler::new();
        session.update(&project)?;
        let result = session.update(&next)?;
        println!(
            "{}",
            serde_json::json!({
                "reparsed": result.reparsed, "reused": result.reused,
                "affected": result.affected, "artifact_cache": false
            })
        );
        return Ok(());
    }
    *category = DiagnosticCategory::Compilation;
    if matches!(command, "check" | "emit") {
        let source = gx_engine::compile(&project)?;
        if command == "emit" {
            print!("{source}");
        } else {
            println!(
                "verified: admitted portable subset; {} procedures; SDK parity unverified",
                project.procedures.len()
            );
        }
        return Ok(());
    }
    let output = args
        .get(if from_objects { 3 } else { 2 })
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            PathBuf::from(&args[1])
                .parent()
                .unwrap_or_else(|| std::path::Path::new("."))
                .join(".gx-engine")
        });
    // Build is an operation-level phase: it includes code generation and rustc.
    *category = DiagnosticCategory::Build;
    let artifact = build::build(&project, &output)?;
    if command == "build" {
        println!("{}", artifact.executable.display());
        return Ok(());
    }
    *category = DiagnosticCategory::Execution;
    let mut command = Command::new(&artifact.executable);
    // A file avoids pipe backpressure while keeping machine-readable stderr clean.
    // Each build generation is unique; only guard a file we created successfully.
    let capture = if json_errors {
        let path = artifact.executable.with_file_name(".run-stderr.log");
        let file = File::create_new(&path)
            .map_err(|e| format!("cannot create generated program stderr capture: {e}"))?;
        command.stderr(file);
        Some(CapturedStderr(path))
    } else {
        None
    };
    let mut child = command
        .spawn()
        .map_err(|e| execution_error(format!("cannot run generated program: {e}"), &capture))?;
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        match child.try_wait() {
            Ok(Some(status)) => {
                return if status.success() {
                    Ok(())
                } else {
                    Err(execution_error(
                        format!("generated program failed ({status})"),
                        &capture,
                    ))
                };
            }
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(10)),
            result => {
                let killed = child.kill();
                let reaped = child.wait();
                let reason = match result {
                    Err(e) => format!("cannot observe generated program: {e}"),
                    _ => "generated program exceeded 10-second deadline".into(),
                };
                return Err(execution_error(
                    format!("{reason}; cleanup kill={killed:?}, wait={reaped:?}"),
                    &capture,
                ));
            }
        }
    }
}

/// Exit code of a `udm::version_scope` refusal: nothing was read or written, so a host may serve the
/// operation from the GeneXus SDK instead.
const EXIT_VERSION_SCOPE: u8 = 4;
const EXIT_SQL_CREDENTIALS_UNAVAILABLE: u8 = 5;

/// The active KB version named by `--design-model` and `--kb-version`, which go together.
fn active_version(
    design_model: Option<i32>,
    kb_version: Option<i32>,
) -> Result<Option<gx_engine::udm::version_scope::ActiveVersion>, String> {
    match (design_model, kb_version) {
        (None, None) => Ok(None),
        (Some(design_model_id), Some(kb_version_id)) => {
            Ok(Some(gx_engine::udm::version_scope::ActiveVersion {
                design_model_id,
                kb_version_id,
            }))
        }
        _ => Err(
            "--design-model and --kb-version name the active KB version together: give both or neither"
                .into(),
        ),
    }
}

/// The Design model `client` serves for `active` (see `udm::version_scope`). A refusal, or a topology
/// that cannot be read, sets the version-scope category: no object has been read yet.
fn select_design_model(
    client: &gx_engine::udm::UdmClient,
    active: Option<gx_engine::udm::version_scope::ActiveVersion>,
    scope_use: gx_engine::udm::version_scope::ScopeUse,
    category: &mut DiagnosticCategory,
) -> Result<i32, String> {
    let selected = client
        .model_topology()
        .map_err(|error| format!("the KB's model topology could not be read: {error}"))
        .and_then(|rows| {
            let model_id =
                gx_engine::udm::version_scope::select_design_model(&rows, active, scope_use)
                    .map_err(|refusal| refusal.0)?;
            // A build reads, from a KB with several versions, facts no Design model holds a version of:
            // an entity with several versions among them cannot be pinned, so the build is refused.
            if scope_use == gx_engine::udm::version_scope::ScopeUse::Build
                && gx_engine::udm::version_scope::has_several_design_models(&rows)
                && let Some(conflict) = client.unpinned_build_fact_conflict().map_err(|error| {
                    format!("the build facts could not be pinned to the KB version: {error}")
                })?
            {
                return Err(conflict);
            }
            Ok(model_id)
        });
    if selected.is_err() {
        *category = DiagnosticCategory::VersionScope;
    }
    selected
}

fn connect_udm_kb(kb_path: &str) -> Result<gx_engine::udm::UdmClient, String> {
    let path = std::path::Path::new(kb_path);
    if !path.is_dir() {
        return Err(format!(
            "Knowledge Base directory '{kb_path}' does not exist"
        ));
    }
    gx_engine::udm::UdmClient::connect_kb(path)
}

/// The client for `kb_path` serving the Design model of the active KB version, selected on the
/// operation's own client before any read.
fn connect_selected_kb(
    kb_path: &str,
    active: Option<gx_engine::udm::version_scope::ActiveVersion>,
    category: &mut DiagnosticCategory,
) -> Result<gx_engine::udm::UdmClient, String> {
    // One SQL reader session serves the topology read and the operation's own reads.
    let client = connect_udm_kb(kb_path)?.with_persistent_reads();
    let model_id = select_design_model(
        &client,
        active,
        gx_engine::udm::version_scope::ScopeUse::Read,
        category,
    )?;
    Ok(client.with_design_model_id(model_id))
}

fn print_json<T: serde::Serialize>(result: &T) -> Result<(), String> {
    println!(
        "{}",
        serde_json::to_string(result).map_err(|e| e.to_string())?
    );
    Ok(())
}

/// One `kb-read` / `kb-query` / `kb-write` product operation against a native
/// Knowledge Base directory. The result is printed as one line of JSON on
/// stdout; failures follow the CLI's diagnostic contract (text by default,
/// JSON on stderr when `--diagnostics=json` is the first argument).
fn execute_kb_ops(
    command: &str,
    args: &[String],
    category: &mut DiagnosticCategory,
) -> Result<(), String> {
    *category = DiagnosticCategory::Input;
    let request = kb_command::parse(command, args)?;
    let active = active_version(request.design_model, request.kb_version)?;
    let operation = request.admit(command)?;
    run_kb_operation(operation, active, category)
}

fn lift_kb_read<T>(
    result: Result<T, gx_engine::kb_ops::KbReadError>,
    category: &mut DiagnosticCategory,
) -> Result<T, String> {
    result.map_err(|error| match error {
        gx_engine::kb_ops::KbReadError::VersionScope(message) => {
            *category = DiagnosticCategory::VersionScope;
            message
        }
        gx_engine::kb_ops::KbReadError::Operation(message) => message,
    })
}

/// Runs an admitted operation; its Knowledge Base is connected only now.
fn run_kb_operation(
    operation: kb_command::KbOperation,
    active: Option<gx_engine::udm::version_scope::ActiveVersion>,
    category: &mut DiagnosticCategory,
) -> Result<(), String> {
    use kb_command::KbOperation;
    match operation {
        KbOperation::Read {
            kb_path,
            object,
            parts,
        } => {
            let client = connect_udm_kb(&kb_path)?;
            let read = lift_kb_read(
                gx_engine::kb_ops::read_object_scoped(&client, active, &object, parts.as_deref()),
                category,
            )?;
            print_json(&read)
        }
        KbOperation::ReadPage {
            kb_path,
            object,
            part,
            offset,
            limit,
            client: client_kind,
        } => {
            let client = connect_udm_kb(&kb_path)?;
            let page = lift_kb_read(
                gx_engine::kb_ops::read_object_page_scoped(
                    &client,
                    active,
                    &object,
                    &part,
                    offset,
                    limit,
                    client_kind,
                ),
                category,
            )?;
            print_json(&page)
        }
        KbOperation::Query {
            kb_path,
            name,
            object_type,
            used_by,
            limit,
        } => {
            let client = connect_selected_kb(&kb_path, active, category)?;
            print_json(&gx_engine::kb_ops::query_objects(
                &client,
                name.as_deref(),
                object_type.as_deref(),
                used_by.as_deref(),
                limit,
            )?)
        }
        KbOperation::Write {
            kb_path,
            object,
            part,
            content,
            expected,
        } => {
            let client = connect_selected_kb(&kb_path, active, category)?;
            print_json(&gx_engine::kb_ops::write_object_part(
                &client, &object, &part, &content, None, expected,
            )?)
        }
        KbOperation::Diagnose { kb_path, name } => {
            let client = connect_udm_kb(&kb_path)?;
            let result = lift_kb_read(
                gx_engine::native_diagnostics::diagnose_object_scoped(&client, active, &name),
                category,
            )?;
            print_json(&result)
        }
        scoped_read => run_scoped_read(scoped_read, active),
    }
}

/// The native reads that select the active version inside the read itself, on a client opened
/// without `connect_selected_kb`.
fn run_scoped_read(
    operation: kb_command::KbOperation,
    active: Option<gx_engine::udm::version_scope::ActiveVersion>,
) -> Result<(), String> {
    use kb_command::KbOperation;
    match operation {
        KbOperation::ProcedureList {
            kb_path,
            offset,
            limit,
        } => print_json(&connect_udm_kb(&kb_path)?.procedure_list_page(active, offset, limit)?),
        KbOperation::ObjectList {
            kb_path,
            kind,
            offset,
            limit,
        } => print_json(&connect_udm_kb(&kb_path)?.object_list_page(active, kind, offset, limit)?),
        KbOperation::Environment { kb_path } => print_json(&serde_json::json!({
            "environment": connect_udm_kb(&kb_path)?.sole_environment_name(active)?
        })),
        KbOperation::Inspect {
            kb_path,
            name,
            kind,
        } => {
            let client = connect_udm_kb(&kb_path)?;
            let json = if kind == gx_engine::catalog::ObjectKind::Procedure {
                client.procedure_inspect_minimal(active, &name)?
            } else {
                client.object_inspect_minimal(active, kind, &name)?
            };
            print_json(&json)
        }
        KbOperation::VersionList { kb_path } => {
            print_json(&connect_udm_kb(&kb_path)?.kb_version_list(active)?)
        }
        KbOperation::Read { .. }
        | KbOperation::ReadPage { .. }
        | KbOperation::Query { .. }
        | KbOperation::Diagnose { .. }
        | KbOperation::Write { .. } => {
            unreachable!("run_kb_operation serves the category-selecting operations")
        }
    }
}

/// Exit code of a partial rebuild: some objects were skipped or not compiled. Distinct from
/// success (0) and failure (1), so a partial run cannot pass as a complete one.
const EXIT_PARTIAL: u8 = 3;

/// A catalog of one object read by name from a native KB directory, for a single-object build.
/// The object's references must all resolve inside the catalog: a reference to an object that is not
/// read would drop a dependency silently, so such an object is refused and the full catalog is needed.
fn load_one_object_catalog(
    kb: &str,
    name: &str,
    model_id: i32,
) -> Result<gx_engine::catalog::Catalog, String> {
    let path = std::path::Path::new(kb);
    if !path.join("knowledgebase.connection").exists() {
        return Err(format!(
            "--object needs a native KB directory (knowledgebase.connection): {kb}"
        ));
    }
    let client = std::sync::Arc::new(
        gx_engine::udm::client::UdmClient::connect_kb(path)?
            .with_persistent_reads()
            .with_design_model_id(model_id),
    );
    let reader = gx_engine::udm::reader::UdmReader::new(client);
    let object = reader
        .read_object_by_name(name)?
        .ok_or_else(|| format!("object '{name}' is not in the KB"))?;
    // Only a reference to an object that emits code must be in the catalog (see `single_object_gap`).
    if let Some(missing) = gx_engine::catalog::single_object_gap(&object) {
        return Err(format!(
            "object '{name}' references {missing:?} outside the single-object catalog: use the full rebuild"
        ));
    }
    let mut objects = std::collections::BTreeMap::new();
    objects.insert(object.id.clone(), object);
    Ok(gx_engine::catalog::Catalog::from_objects_map(objects))
}

/// Reads the closure of one object: the object and every object its references reach. The planner decides
/// what is built (see `UdmReader::read_closure_by_name`).
fn load_closure_catalog(
    kb: &str,
    name: &str,
    model_id: i32,
) -> Result<(gx_engine::catalog::ObjectId, gx_engine::catalog::Catalog), String> {
    let path = std::path::Path::new(kb);
    if !path.join("knowledgebase.connection").exists() {
        return Err(format!(
            "--closure needs a native KB directory (knowledgebase.connection): {kb}"
        ));
    }
    let client = std::sync::Arc::new(
        gx_engine::udm::client::UdmClient::connect_kb(path)?
            .with_persistent_reads()
            .with_design_model_id(model_id),
    );
    let reader = gx_engine::udm::reader::UdmReader::new(client);
    let (entry, objects) = reader
        .read_closure_by_name(name)?
        .ok_or_else(|| format!("object '{name}' is not in the KB"))?;
    let objects = objects
        .into_iter()
        .map(|object| (object.id.clone(), object))
        .collect();
    Ok((
        entry,
        gx_engine::catalog::Catalog::from_objects_map(objects),
    ))
}

/// The theme the KB's generating environment uses; `None`, with the reason on stderr, when it is not read.
fn kb_web_theme(kb_client: &Result<gx_engine::udm::client::UdmClient, String>) -> Option<String> {
    let client = kb_client.as_ref().ok()?;
    client.default_web_theme().unwrap_or_else(|error| {
        eprintln!("web theme not read from the KB: {error}");
        None
    })
}

fn execute_rebuild_all(
    args: &[String],
    category: &mut DiagnosticCategory,
    json_errors: bool,
) -> Result<gx_engine::rebuild::RebuildStatus, String> {
    *category = DiagnosticCategory::Input;
    let mut kb_path: Option<String> = None;
    let mut output_dir: Option<String> = None;
    let mut target = "net8.0".to_string();
    let mut clean = false;
    let mut concurrency = 0;
    let mut skip_compile = false;
    let mut only_object: Option<String> = None;
    let mut closure = false;
    let mut web_theme: Option<String> = None;
    let mut web_generator: Option<String> = None;
    let mut web_masters: std::collections::BTreeMap<String, String> = Default::default();
    let mut catalog_cache: Option<String> = None;
    let mut design_model: Option<i32> = None;
    let mut kb_version: Option<i32> = None;

    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--design-model" => {
                i += 1;
                design_model = Some(parse_id_flag(args, i, "--design-model")?);
            }
            "--kb-version" => {
                i += 1;
                kb_version = Some(parse_id_flag(args, i, "--kb-version")?);
            }
            "--kb" => {
                i += 1;
                if i >= args.len() {
                    return Err("missing argument for --kb".into());
                }
                kb_path = Some(args[i].clone());
            }
            "--object" => {
                i += 1;
                if i >= args.len() {
                    return Err("missing argument for --object".into());
                }
                only_object = Some(args[i].clone());
            }
            "--output" => {
                i += 1;
                if i >= args.len() {
                    return Err("missing argument for --output".into());
                }
                output_dir = Some(args[i].clone());
            }
            "--target" => {
                i += 1;
                if i >= args.len() {
                    return Err("missing argument for --target".into());
                }
                target = args[i].clone();
            }
            "--clean" => {
                clean = true;
            }
            "--concurrency" => {
                i += 1;
                if i >= args.len() {
                    return Err("missing argument for --concurrency".into());
                }
                concurrency = args[i]
                    .parse::<usize>()
                    .map_err(|e| format!("invalid concurrency: {e}"))?;
            }
            "--skip-compile" => {
                skip_compile = true;
            }
            "--closure" => {
                closure = true;
            }
            "--catalog-cache" => {
                i += 1;
                if i >= args.len() {
                    return Err("missing argument for --catalog-cache".into());
                }
                catalog_cache = Some(args[i].clone());
            }
            "--web-theme" => {
                i += 1;
                web_theme = Some(
                    args.get(i)
                        .ok_or("missing argument for --web-theme")?
                        .clone(),
                );
            }
            "--web-generator" => {
                i += 1;
                web_generator = Some(
                    args.get(i)
                        .ok_or("missing argument for --web-generator")?
                        .clone(),
                );
            }
            "--web-master" => {
                i += 1;
                let value = args.get(i).ok_or("missing argument for --web-master")?;
                let (name, class) = value.split_once('=').ok_or_else(|| {
                    format!("--web-master expects Name=dotted.class.path, got '{value}'")
                })?;
                web_masters.insert(name.to_string(), class.to_string());
            }
            other => {
                return Err(format!("unexpected argument for rebuild-all: {other}"));
            }
        }
        i += 1;
    }

    let kb = kb_path.ok_or_else(|| "missing required --kb <path>".to_string())?;
    let out = output_dir.ok_or_else(|| "missing required --output <dir>".to_string())?;
    if !matches!(target.as_str(), "net8.0" | "net9.0") {
        return Err(format!(
            "unsupported --target {target}: rebuild-all builds net8.0 or net9.0"
        ));
    }
    // Checked before any read: every KB read would otherwise report the same missing file on its own.
    let kb_dir = std::path::Path::new(&kb);
    if !kb_dir.is_file() && !kb_dir.join("knowledgebase.connection").is_file() {
        return Err(format!(
            "--kb '{kb}' is neither a Knowledge Base folder (it has no knowledgebase.connection) nor a catalog file"
        ));
    }

    if closure && only_object.is_none() {
        return Err("--closure needs --object: the entry object of the closure".into());
    }
    let active = active_version(design_model, kb_version)?;
    // Before any read or output: a build reads the trunk Design model and its environments only. The model
    // the preflight selects is the one every read below targets (a catalog file has none).
    let mut build_model_id: Option<i32> = None;
    if kb_dir.is_dir() {
        let connection = gx_engine::udm::connection::KbConnectionInfo::from_kb_dir(kb_dir)?;
        if let Err(error) = gx_engine::udm::live::validate_sqlcmd_auth(&connection) {
            // Dedicated refusal contract: nothing has queried storage or created output yet.
            *category = DiagnosticCategory::SqlCredentialsUnavailable;
            return Err(error);
        }
        let client = gx_engine::udm::client::UdmClient::connect_kb(kb_dir)?;
        build_model_id = Some(select_design_model(
            &client,
            active,
            gx_engine::udm::version_scope::ScopeUse::Build,
            category,
        )?);
    } else if active.is_some() {
        return Err(
            "--design-model and --kb-version apply to a Knowledge Base folder, not a catalog file"
                .into(),
        );
    }
    // The catalog load happens before the pipeline, so `total_ms` does not include it; it is reported apart.
    let catalog_started = Instant::now();
    let mut closure_entry: Option<gx_engine::catalog::ObjectId> = None;
    // The build environment (runtime version, web configuration, messages, design classes, GUIDs,
    // attributes) is read on its own SQL session while the catalog loads: neither reads the other.
    let (catalog, (runtime_version, client_config, model_files, web_environment)) =
        std::thread::scope(|scope| -> Result<_, String> {
            let kb = &kb;
            let environment = scope.spawn(move || {
                let runtime_version =
                    gx_engine::rebuild::genexus_runtime_version_from_kb(std::path::Path::new(&kb));
                // Plain WebPanels: the build number comes from the KB; theme, generator and master class
                // paths are explicit. An unset option refuses each plain panel by name.
                let client_config = gx_engine::csharp::webform::web_client_config_from_kb(
                    std::path::Path::new(&kb),
                );
                // Messages and runtime configuration: from the KB's entities and stored model properties, and
                // the install's message text (GX18_INSTALL_DIR). A group that cannot be read fails a run that writes it.
                // One client, one persistent reader session, for every KB read that configures the build.
                let kb_client = gx_engine::udm::client::UdmClient::connect_kb(kb).map(|client| {
                    client.with_persistent_reads().with_design_model_id(
                        build_model_id.unwrap_or(gx_engine::udm::DEFAULT_DESIGN_MODEL_ID),
                    )
                });
                let model_files = gx_engine::csharp::model_files::model_files_from_kb(
                    kb_client.as_ref().map_err(String::clone),
                    &gx_engine::csharp::model_files::genexus_install_dir(),
                );
                // Theme and generator text: the explicit options, else the KB's stored theme and the
                // install's version, as the SDK reads them. Neither found leaves each web object refused by name.
                let theme = web_theme.or_else(|| kb_web_theme(&kb_client));
                let generator = web_generator.or_else(|| {
                    gx_engine::csharp::webform::web_generator_from_install(
                        &gx_engine::csharp::model_files::genexus_install_dir(),
                    )
                });
                let web_environment = gx_engine::csharp::webform::WebPanelEnvironment {
                    theme,
                    generator,
                    master_classes: web_masters,
                    build_number: gx_engine::csharp::webform::web_build_number_from_kb(
                        std::path::Path::new(&kb),
                    ),
                    support_class: gx_engine::csharp::webform::web_support_from_kb(
                        std::path::Path::new(&kb),
                    ),
                    // Layout class references resolve against the KB's design system; a KB without one
                    // resolves nothing, and each panel naming a class is refused by name.
                    design_classes: kb_client
                        .clone()
                        .and_then(|client| client.design_system_classes())
                        .unwrap_or_else(|error| {
                            eprintln!("design-system classes not read: {error}");
                            std::collections::BTreeMap::new()
                        }),
                    // Image, SDT and Domain references resolve to their entity GUIDs; a KB whose objects are not
                    // read resolves none, and each panel that is matched by them is refused by name.
                    object_guids: kb_client
                        .clone()
                        .and_then(|client| client.object_guids())
                        .unwrap_or_else(|error| {
                            eprintln!("object GUIDs not read: {error}");
                            std::collections::BTreeMap::new()
                        }),
                    // Attribute ids and titles and table keys name a Transaction's members; a KB whose
                    // attributes are not read resolves none, and each Transaction is refused by name.
                    attributes: kb_client
                        .clone()
                        .and_then(|client| client.attribute_facts())
                        .unwrap_or_else(|error| {
                            eprintln!("attributes not read: {error}");
                            std::collections::BTreeMap::new()
                        }),
                    table_keys: kb_client
                        .clone()
                        .and_then(|client| client.table_keys())
                        .unwrap_or_else(|error| {
                            eprintln!("table keys not read: {error}");
                            std::collections::BTreeMap::new()
                        }),
                    subtype_groups: kb_client
                        .clone()
                        .and_then(|client| client.subtype_groups())
                        .unwrap_or_else(|error| {
                            eprintln!("subtype groups not read: {error}");
                            std::collections::BTreeMap::new()
                        }),
                };
                (runtime_version, client_config, model_files, web_environment)
            });
            let build_model = build_model_id.unwrap_or(gx_engine::udm::DEFAULT_DESIGN_MODEL_ID);
            let catalog = match (only_object, catalog_cache) {
                (Some(name), _) if closure => {
                    let (entry, catalog) = load_closure_catalog(kb, &name, build_model)?;
                    closure_entry = Some(entry);
                    catalog
                }
                (None, Some(cache_dir))
                    if std::path::Path::new(&kb)
                        .join("knowledgebase.connection")
                        .exists() =>
                {
                    let (catalog, status) = gx_engine::catalog_cache::load_native_kb(
                        std::path::Path::new(&kb),
                        std::path::Path::new(&cache_dir),
                        build_model,
                    )?;
                    eprintln!("catalog cache: {status:?}");
                    catalog
                }
                (None, _) => match build_model_id {
                    Some(model_id) => gx_engine::catalog::Catalog::load_native_model(kb, model_id)?,
                    None => gx_engine::catalog::Catalog::load(kb)?,
                },
                (Some(name), _) => load_one_object_catalog(kb, &name, build_model)?,
            };

            let environment = environment
                .join()
                .map_err(|_| "the build environment reader panicked".to_string())?;
            Ok((catalog, environment))
        })?;

    *category = DiagnosticCategory::Build;
    let mut options = gx_engine::rebuild::RebuildOptions::new(target)
        .with_clean(clean)
        .with_concurrency(concurrency)
        .with_skip_compile(skip_compile);
    // Compile against the runtime the KB itself declares; a KB without a declaration keeps
    // the default (see DEFAULT_GENEXUS_RUNTIME_VERSION).
    if let Some(version) = runtime_version {
        options = options.with_genexus_runtime_version(version);
    }
    options = options
        .with_client_config(client_config)
        .with_model_files(model_files)
        .with_web_environment(web_environment)
        // The artifact cache directory and the fixed generation stamp, both opt-in.
        .with_process_environment()?;

    let catalog_load_ms = catalog_started.elapsed().as_millis() as u64;
    // A library SDT the KB's runtime assemblies define is never emitted (the SDK uses the runtime class).
    options.runtime_sdts = gx_engine::rebuild::runtime_sdt_names_from_kb(
        std::path::Path::new(&kb),
        catalog
            .objects()
            .filter(|object| {
                object.kind == gx_engine::catalog::ObjectKind::StructuredDataType && object.library
            })
            .map(|object| object.name.as_str()),
    );
    if let Some(entry) = &closure_entry {
        // The planner refuses by name before anything is emitted; the entry point comes from the emitter.
        let plan = gx_engine::catalog::plan_single_object_closure(&catalog, entry)?;
        options = options
            .with_startup_object(gx_engine::rebuild::closure_startup_object(&catalog, entry)?);
        eprintln!(
            "closure: {} compile units, {} namespace modules",
            plan.members.len(),
            plan.namespace_modules.len()
        );
    }
    let report = gx_engine::rebuild::rebuild_all(&catalog, &PathBuf::from(out), &options)?;
    let report = gx_engine::rebuild::RebuildReport {
        catalog_load_ms,
        ..report
    };

    if json_errors || env::var("GXMCP_JSON_REPORT").is_ok() {
        println!(
            "{}",
            serde_json::to_string_pretty(&report).map_err(|e| e.to_string())?
        );
    } else {
        println!(
            "Rebuild All completed in {}ms (read: {}ms, spec: {}ms, emit: {}ms, compile: {}ms), catalog load {}ms before the pipeline",
            report.total_ms,
            report.read_ms,
            report.spec_ms,
            report.emit_ms,
            report.compile_ms,
            report.catalog_load_ms
        );
        println!(
            "Objects: {} (Procedures in catalog: {}, Transactions: {}, SDTs emitted: {}, WebPanels: {})",
            report.total_objects,
            report.procedures_count,
            report.transactions_count,
            report.sdts_count,
            report.webpanels_count
        );
        println!("Emitted files: {}", report.emitted_files.len());
        println!(
            "Status: {} (generated: {}, compiled: {}, skipped: {}, not generated: {}, refused: {})",
            status_label(report.status),
            report.generated_count,
            report.compiled_count,
            report.skipped_count,
            report.not_generated_count,
            report.refused_count
        );
        for skipped in &report.skipped_objects {
            println!(
                "  [SKIPPED] {} '{}': {}",
                skipped.kind, skipped.name, skipped.reason
            );
        }
        for object in &report.not_generated_objects {
            println!(
                "  [NOT GENERATED] {} '{}': {}",
                object.kind, object.name, object.reason
            );
        }
        for object in &report.not_targeted_objects {
            println!(
                "  [NOT TARGETED] {} '{}': {}",
                object.kind, object.name, object.reason
            );
        }
        for refused in &report.refused_objects {
            println!(
                "  [REFUSED] {} '{}': {}",
                refused.kind, refused.name, refused.reason
            );
        }
        if !report.warnings.is_empty() {
            println!("Warnings ({}):", report.warnings.len());
            for w in &report.warnings {
                println!("  [WARN] {w}");
            }
        }
        if !report.errors.is_empty() {
            println!("Errors ({}):", report.errors.len());
            for e in &report.errors {
                println!("  [ERROR] {e}");
            }
        }
    }

    match report.status {
        gx_engine::rebuild::RebuildStatus::Failed => Err(format!(
            "rebuild failed with {} error(s)",
            report.errors.len()
        )),
        status => Ok(status),
    }
}

fn status_label(status: gx_engine::rebuild::RebuildStatus) -> &'static str {
    match status {
        gx_engine::rebuild::RebuildStatus::Complete => "complete",
        gx_engine::rebuild::RebuildStatus::Partial => "partial",
        gx_engine::rebuild::RebuildStatus::Failed => "failed",
    }
}

fn main() -> ExitCode {
    let mut args: Vec<String> = env::args().skip(1).collect();
    let json_errors = args.first().is_some_and(|arg| arg == "--diagnostics=json");
    if json_errors {
        args.remove(0);
    }
    // Version probe for host integrations (the MCP Gateway's engine backend gate
    // reads this line); kept dependency-free so it cannot fail on a broken project.
    if args
        .first()
        .is_some_and(|arg| arg == "--version" || arg == "-V")
    {
        println!("gx-engine {}", env!("CARGO_PKG_VERSION"));
        return ExitCode::SUCCESS;
    }
    let mut category = DiagnosticCategory::Usage;
    let outcome = if args.first().is_some_and(|arg| arg == "rebuild-all") {
        execute_rebuild_all(&args[1..], &mut category, json_errors)
    } else {
        execute(args, &mut category, json_errors)
            .map(|()| gx_engine::rebuild::RebuildStatus::Complete)
    };
    match outcome {
        Ok(gx_engine::rebuild::RebuildStatus::Complete) => ExitCode::SUCCESS,
        Ok(gx_engine::rebuild::RebuildStatus::Partial) => ExitCode::from(EXIT_PARTIAL),
        Ok(gx_engine::rebuild::RebuildStatus::Failed) => ExitCode::FAILURE,
        Err(error) => {
            let exit = match category {
                DiagnosticCategory::VersionScope => ExitCode::from(EXIT_VERSION_SCOPE),
                DiagnosticCategory::SqlCredentialsUnavailable => {
                    ExitCode::from(EXIT_SQL_CREDENTIALS_UNAVAILABLE)
                }
                _ => ExitCode::FAILURE,
            };
            if json_errors {
                let diagnostic = Diagnostic::new(category, error);
                eprintln!(
                    "{}",
                    serde_json::to_string(&diagnostic).expect("diagnostic serialization")
                );
            } else {
                eprintln!("gx-engine: {error}");
            }
            exit
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use gx_engine::udm::version_scope::ActiveVersion;

    #[test]
    fn active_version_requires_design_model_and_kb_version_together() {
        assert_eq!(active_version(None, None), Ok(None));
        assert_eq!(
            active_version(Some(3), Some(7)),
            Ok(Some(ActiveVersion {
                design_model_id: 3,
                kb_version_id: 7
            }))
        );
        for pair in [(Some(3), None), (None, Some(7))] {
            assert_eq!(
                active_version(pair.0, pair.1).unwrap_err(),
                "--design-model and --kb-version name the active KB version together: give both or neither"
            );
        }
    }

    #[test]
    fn unpaired_version_flags_are_refused_before_operation_admission() {
        let mut category = DiagnosticCategory::Usage;
        let args = ["kb", "--design-model", "3"].map(String::from);
        let error = execute_kb_ops("kb-query", &args, &mut category).unwrap_err();
        assert!(error.starts_with("--design-model and --kb-version name"));
        assert_eq!(category, DiagnosticCategory::Input);
    }

    #[test]
    fn lift_kb_read_maps_version_scope_errors_to_version_scope_category() {
        let mut category = DiagnosticCategory::Input;
        let error = lift_kb_read::<()>(
            Err(gx_engine::kb_ops::KbReadError::VersionScope(
                "model 4 is not a Design model".into(),
            )),
            &mut category,
        )
        .unwrap_err();
        assert_eq!(error, "model 4 is not a Design model");
        assert_eq!(category, DiagnosticCategory::VersionScope);

        let mut category = DiagnosticCategory::Input;
        let error = lift_kb_read::<()>(
            Err(gx_engine::kb_ops::KbReadError::Operation(
                "object 'PAlpha' was not found in the active model".into(),
            )),
            &mut category,
        )
        .unwrap_err();
        assert_eq!(error, "object 'PAlpha' was not found in the active model");
        assert_eq!(category, DiagnosticCategory::Input);
    }
}
