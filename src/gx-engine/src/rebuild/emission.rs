//! Phase 2: emits the C# classes, support files and project definitions of the model.

use super::{
    RebuildOptions,
    classify::ObjectManifest,
    parallel::{Findings, map_chunks},
    reuse::{
        EmitRecord, ProcedureRecord, RecordedSignature, SdtRecord, Session, digest_of, key_of,
    },
};
use crate::{
    catalog::KbObject,
    csharp::{
        CsprojConfig, CsprojEmitter, ProcedureClass, SdtClass,
        calls::{CallParam, CallSignatures, CallTarget},
        procedure::CheckedEmit,
        sdt::SdtIndex,
        webform,
    },
    model::ParamMode,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, BTreeSet, HashMap},
    path::{Path, PathBuf},
    sync::{Arc, OnceLock, atomic::Ordering},
    time::Instant,
};

/// The artifact cache kind of a Procedure's emitted C#.
const EMIT_KIND: &str = "emit";
/// The artifact cache kind of the files of a model's structured data types.
const SDTS_KIND: &str = "sdts";

/// The support classes of the model, as the SDK writes them (see the support step of the emitter).
const SUPPORT_FILES: [(&str, &str); 5] = [
    (
        "GxFullTextSearchReindexer.cs",
        include_str!("../csharp/support/GxFullTextSearchReindexer.cs.tmpl"),
    ),
    (
        "GxModelInfoProvider.cs",
        include_str!("../csharp/support/GxModelInfoProvider.cs.tmpl"),
    ),
    (
        "GxObjectCollection.cs",
        include_str!("../csharp/support/GxObjectCollection.cs.tmpl"),
    ),
    (
        "SoapParm.cs",
        include_str!("../csharp/support/SoapParm.cs.tmpl"),
    ),
    ("GxWebServicesConfig.cs", ""),
];

/// The runtime configuration the SDK writes beside each class of a main program (`<class>.exe.config`).
const EXE_CONFIG: &str = include_str!("../csharp/exe_config.tmpl");

/// The files written into the output directory and the errors of writing them. Each thread of a parallel
/// step fills its own tree; the caller merges them in chunk order.
struct OutputTree<'a> {
    dir: &'a Path,
    files: Vec<PathBuf>,
    errors: Vec<String>,
    /// A file could not be written. The output is then not what the model generates, so the run records
    /// nothing a later run could reuse.
    io_failed: bool,
}

impl<'a> OutputTree<'a> {
    fn new(dir: &'a Path) -> Self {
        Self {
            dir,
            files: Vec::new(),
            errors: Vec::new(),
            io_failed: false,
        }
    }

    fn merge(&mut self, other: OutputTree<'_>) {
        self.files.extend(other.files);
        self.errors.extend(other.errors);
        self.io_failed |= other.io_failed;
    }

    /// Lists a file the previous run wrote and this run keeps as it is (see `Session::is_intact`).
    fn keep_file(&mut self, file_name: &str) {
        self.files.push(self.dir.join(file_name));
    }

    /// Writes one model file as the given bytes (no newline or encoding rewriting), creating its folder.
    fn write_model_file(&mut self, relative: &str, bytes: &[u8]) {
        let file_path = self.dir.join(relative);
        let written = file_path
            .parent()
            .map_or(Ok(()), std::fs::create_dir_all)
            .and_then(|()| std::fs::write(&file_path, bytes));
        match written {
            Ok(()) => self.files.push(file_path),
            Err(e) => {
                self.io_failed = true;
                self.errors.push(format!("{relative}: not written: {e}"));
            }
        }
    }

    fn write_generated_file(&mut self, file_name: &str, content: &str) {
        self.write_stamped_file(file_name, content, "");
    }

    /// Writes one generated file unless the file already holds that output: the same bytes, or the same
    /// bytes generated at another time (`stamp`, see `webform::differs_only_in_stamp`). A kept file keeps
    /// its bytes and time, so an unchanged project is recognised as unchanged by the compile step.
    fn write_stamped_file(&mut self, file_name: &str, content: &str, stamp: &str) {
        let file_path = self.dir.join(file_name);
        // A C# file's literal values depend on the whole file's bytes (see `csharp::literals`), so its text
        // is resolved here, once, before it is written.
        // It is written in the SDK's bytes: CRLF and cp1252 (`csharp::literals::sdk_file_bytes`).
        // A file in the SDK's form already holds each stored character raw (`Expr::Text` in the emitter), so
        // its bytes are the SDK's bytes and the compiler reads them as the SDK's file; only an ASCII file
        // with escapes is resolved (artm41852400.cs holds the stored C2 A0 as two bytes).
        let bytes = if file_name.ends_with(".cs") {
            if content.is_ascii() {
                crate::csharp::literals::sdk_file_bytes(
                    &crate::csharp::literals::resolve_sdk_literals(content),
                )
            } else {
                crate::csharp::literals::sdk_file_bytes(content)
            }
        } else {
            content.as_bytes().to_vec()
        };
        let kept = std::fs::read(&file_path)
            .is_ok_and(|existing| webform::differs_only_in_stamp(&existing, &bytes, stamp));
        match if kept {
            Ok(())
        } else {
            std::fs::write(&file_path, bytes)
        } {
            Ok(()) => self.files.push(file_path),
            Err(e) => {
                self.io_failed = true;
                self.errors
                    .push(format!("failed to write '{}': {e}", file_path.display()));
            }
        }
    }
}

/// One Procedure's emission, settled before any file is written.
struct ProcedureEmission {
    /// The object's identity, and the fingerprint its written files are recorded under.
    id: String,
    key: String,
    /// The files the previous run wrote for this emission stand: nothing is written, and `emitted.code`
    /// and `wrapper` hold no text.
    kept: bool,
    name: String,
    /// The class name, upper-cased: the key a call site records for this Procedure.
    class: String,
    /// The class the main file declares, and so its file stem (`a<name>` for a split main program).
    stem: String,
    /// The plain class name in lower case (`<name>`), and the file stem of the wrapper.
    class_name: String,
    /// The wrapper file of a split main program (see `ProcedureClass::emit_wrapper`).
    wrapper: Option<String>,
    emitted: CheckedEmit,
}

/// What every Procedure emission resolves against: the namespaces of the emitted SDTs, the SDT index and
/// the call signatures of every Procedure this build emits.
struct ProcedureContext {
    sdt_usings: Vec<String>,
    sdt_index: Arc<SdtIndex>,
    call_table: Arc<CallSignatures>,
}

/// What the emission wrote, and what the next run may reuse of it.
pub(super) struct Emission {
    /// The written and kept files, sorted.
    pub files: Vec<PathBuf>,
    pub main_project: PathBuf,
    pub emit_ms: u64,
    /// One record per Procedure of the build, in build order, without its specification.
    pub procedures: Vec<ProcedureRecord>,
    pub sdts: Option<SdtRecord>,
    pub call_key: Option<String>,
    pub io_failed: bool,
}

/// One Procedure of the build with what identifies it to the others.
struct ProcedurePlan<'a> {
    proc: &'a KbObject,
    /// Digest of the object.
    content: String,
    /// The call target other Procedures see, or `None` when the class does not build (such a Procedure
    /// is absent from the call table).
    target: Option<CallTarget>,
}

fn record_signature(target: &CallTarget) -> RecordedSignature {
    let CallTarget {
        class_name,
        namespace,
        params,
    } = target;
    RecordedSignature {
        class_name: class_name.clone(),
        namespace: namespace.clone(),
        params: params
            .iter()
            .map(|CallParam { mode, data_type }| {
                let mode = match mode {
                    ParamMode::In => 0,
                    ParamMode::Out => 1,
                    ParamMode::InOut => 2,
                };
                (mode, data_type.clone())
            })
            .collect(),
    }
}

/// The call target a record holds, or `None` when it holds a mode this build does not know.
fn recorded_target(signature: &RecordedSignature) -> Option<CallTarget> {
    let params = signature
        .params
        .iter()
        .map(|(mode, data_type)| {
            let mode = match mode {
                0 => ParamMode::In,
                1 => ParamMode::Out,
                2 => ParamMode::InOut,
                _ => return None,
            };
            Some(CallParam {
                mode,
                data_type: data_type.clone(),
            })
        })
        .collect::<Option<Vec<_>>>()?;
    Some(CallTarget {
        class_name: signature.class_name.clone(),
        namespace: signature.namespace.clone(),
        params,
    })
}

/// Plans each Procedure. A Procedure the previous run recorded with the same content has the call target
/// it had; any other is read once, here.
fn plan_procedures<'a>(
    procedures: &[&'a KbObject],
    concurrency: usize,
    session: &Session<'_>,
) -> Vec<ProcedurePlan<'a>> {
    map_chunks(procedures, concurrency, |chunk| {
        chunk
            .iter()
            .map(|proc| {
                let content = session.content(proc);
                let recorded = session
                    .previous_procedure(proc.id.as_str())
                    .filter(|record| record.content == content)
                    .map(|record| match &record.signature {
                        Some(signature) => recorded_target(signature).map(Some),
                        None => Some(None),
                    });
                let target = match recorded {
                    Some(Some(target)) => target,
                    _ => ProcedureClass::from_kb_object(proc)
                        .ok()
                        .map(|class| class.call_target()),
                };
                ProcedurePlan {
                    proc,
                    content,
                    target,
                }
            })
            .collect::<Vec<_>>()
    })
    .into_iter()
    .flatten()
    .collect()
}

/// The classes and tables every Procedure emission resolves against, with the errors of building them.
struct BuiltContext {
    sdt_classes: Vec<SdtClass>,
    procedure: ProcedureContext,
    sdt_errors: Vec<String>,
    call_errors: Vec<String>,
}

/// The context of the build, built on first use: a run that keeps every Procedure and the SDT files of
/// the previous run reads no class at all.
struct LazyContext<'m> {
    manifest: &'m ObjectManifest<'m>,
    plans: &'m [ProcedurePlan<'m>],
    built: OnceLock<BuiltContext>,
}

impl LazyContext<'_> {
    fn get(&self) -> &BuiltContext {
        self.built.get_or_init(|| {
            // The SDT classes are built before any Procedure: a Procedure resolves its member accesses
            // against exactly the SDTs emitted here. An SDT that does not build is absent from the index,
            // so a member of it fails closed in every Procedure that uses it.
            let mut sdt_errors = Vec::new();
            let (sdt_classes, sdt_usings) = build_sdt_classes(self.manifest, &mut sdt_errors);
            let sdt_index = build_sdt_index(&sdt_classes, &mut sdt_errors);
            let mut call_errors = Vec::new();
            let call_table = build_call_table(self.plans, &mut call_errors);
            BuiltContext {
                sdt_classes,
                procedure: ProcedureContext {
                    sdt_usings,
                    sdt_index,
                    call_table,
                },
                sdt_errors,
                call_errors,
            }
        })
    }
}

/// Where the files of the build's structured data types come from in this run.
enum SdtSource {
    /// The previous run's files stand.
    Kept(Vec<String>),
    /// The artifact cache holds them, as (file name, text).
    Restored(Vec<(String, String)>),
    /// They are emitted from the classes.
    Emitted,
}

/// Emits the whole model into `output_dir`: the written files (sorted), the main project path and the
/// elapsed milliseconds, with the records of what a later run may reuse. Write and generation failures
/// are added to `findings`.
pub(super) fn emission_phase(
    manifest: &ObjectManifest<'_>,
    output_dir: &Path,
    concurrency: usize,
    options: &RebuildOptions,
    findings: &mut Findings,
    session: &Session<'_>,
) -> Result<Emission, String> {
    let emit_start = Instant::now();

    prepare_output_dir(output_dir, options.clean || session.needs_clean())?;
    // Before the first file: a run cut off from here on leaves a record that is not a state to build on.
    session.begin_writing();
    let mut out = OutputTree::new(output_dir);

    // What an emission reads besides its own object: every SDT of the build (the index and the imported
    // namespaces) and the signature of every Procedure of the build (the call table).
    let sdt_contents: Vec<String> = manifest
        .sdts
        .iter()
        .map(|sdt| session.content(sdt))
        .collect();
    let sdt_key = key_of(
        "sdts",
        std::iter::once(session.engine_key()).chain(sdt_contents.iter().map(String::as_str)),
    );
    let plans = plan_procedures(&manifest.procedures, concurrency, session);
    let signatures: Vec<(&str, &Option<CallTarget>)> = plans
        .iter()
        .map(|plan| (plan.proc.name.as_str(), &plan.target))
        .collect();
    let call_key = key_of(
        "calls",
        [session.engine_key(), digest_of(&signatures).as_str()],
    );
    let inputs = key_of(
        "emission inputs",
        [session.engine_key(), sdt_key.as_str(), call_key.as_str()],
    );

    let sdt_source = match session.previous_sdts() {
        Some(record)
            if record.key == sdt_key && record.files.iter().all(|name| session.is_intact(name)) =>
        {
            session.sdts_kept.fetch_add(1, Ordering::Relaxed);
            SdtSource::Kept(record.files.clone())
        }
        _ => match session
            .cache()
            .and_then(|cache| cache.get(SDTS_KIND, &sdt_key))
        {
            Some(files) => SdtSource::Restored(files),
            None => SdtSource::Emitted,
        },
    };
    let context = LazyContext {
        manifest,
        plans: &plans,
        built: OnceLock::new(),
    };
    // Only a build that had no error is recorded or cached, so the classes and the call table are built
    // again unless a clean build of exactly these inputs is on record.
    let calls_known = plans.is_empty() || session.previous_call_key() == Some(call_key.as_str());
    if matches!(sdt_source, SdtSource::Emitted) || !calls_known {
        context.get();
    }

    // 1. Emit every Procedure. Nothing is written yet: a caller is generated only if every Procedure it
    // calls is generated (csc would fail on a call to a class that is not written), so the outcomes are
    // settled first.
    let mut emissions: Vec<ProcedureEmission> = map_chunks(&plans, concurrency, |chunk| {
        chunk
            .iter()
            .map(|plan| reuse_or_emit(plan, &inputs, &options.target_framework, session, &context))
            .collect::<Vec<_>>()
    })
    .into_iter()
    .flatten()
    .collect();
    emissions.sort_by(|a, b| a.name.cmp(&b.name));
    refuse_callers_of_ungenerated(&mut emissions);

    // The errors of the classes and the call table come first, as when they were built before any emission.
    // A context that was never built has none: its inputs are those of a build that had no error.
    let built = context.built.get();
    if let Some(built) = built {
        out.errors.extend(built.sdt_errors.iter().cloned());
        out.errors.extend(built.call_errors.iter().cloned());
    }

    // 2. Write the Procedures that are generated; each refusal is reported by name.
    write_procedures(&mut out, &emissions, &options.target_framework);
    write_web_classes(&mut out, manifest, options);
    write_business_components(&mut out, manifest);
    write_model_support(&mut out, options);

    // The values class of each enumerated Domain (`gxdomain<name>.cs`), written with the model's classes.
    for (file_name, text) in &manifest.domain_files {
        out.write_generated_file(file_name, text);
    }

    // A Transaction is emitted with the web classes when its form is a matched shape
    // (`transaction_form`); any other is skipped by name (see SkippedObject).

    // 3. Emit SDTs (the classes built above, the same ones the Procedures resolved against)
    let first_sdt_file = out.files.len();
    match &sdt_source {
        SdtSource::Kept(files) => {
            for file_name in files {
                out.keep_file(file_name);
            }
        }
        SdtSource::Restored(files) => {
            for (file_name, text) in files {
                out.write_generated_file(file_name, text);
            }
        }
        SdtSource::Emitted => {
            let built = context.get();
            let cache = session.cache().filter(|_| built.sdt_errors.is_empty());
            let texts = write_sdts(&mut out, &built.sdt_classes, concurrency, cache.is_some());
            if let Some(cache) = cache {
                cache.put(SDTS_KIND, &sdt_key, &texts);
            }
        }
    }
    let sdt_files: Option<Vec<String>> = out.files[first_sdt_file..]
        .iter()
        .map(|path| {
            path.strip_prefix(output_dir)
                .ok()
                .and_then(Path::to_str)
                .map(str::to_string)
        })
        .collect();

    // 5. Emit Main Unified Project File (.csproj)
    let main_csproj_path = write_main_project(&mut out, options)?;

    let built = context.built.get();
    let by_id: HashMap<&str, &ProcedureEmission> = emissions
        .iter()
        .map(|emission| (emission.id.as_str(), emission))
        .collect();
    let procedures = plans
        .iter()
        .map(|plan| ProcedureRecord {
            id: plan.proc.id.as_str().to_string(),
            content: plan.content.clone(),
            signature: plan.target.as_ref().map(record_signature),
            spec: None,
            // Only a written emission is recorded: a refusal is emitted again on every run.
            emit: by_id
                .get(plan.proc.id.as_str())
                .filter(|emission| emission.emitted.code.is_ok())
                .map(|emission| EmitRecord {
                    key: emission.key.clone(),
                    stem: emission.stem.clone(),
                    class_name: emission.class_name.clone(),
                    wrapper: emission.wrapper.is_some(),
                    callees: emission.emitted.callees.clone(),
                }),
        })
        .collect();

    findings.errors.append(&mut out.errors);
    let mut emitted_files = out.files;
    emitted_files.sort();
    Ok(Emission {
        files: emitted_files,
        main_project: main_csproj_path,
        emit_ms: emit_start.elapsed().as_millis() as u64,
        procedures,
        sdts: sdt_files
            .filter(|_| built.is_none_or(|built| built.sdt_errors.is_empty()))
            .map(|files| SdtRecord {
                key: sdt_key,
                files,
            }),
        call_key: built
            .is_none_or(|built| built.call_errors.is_empty())
            .then_some(call_key),
        io_failed: out.io_failed,
    })
}

/// The files `write_procedures` writes for one generated Procedure.
fn procedure_file_names(stem: &str, class_name: &str, wrapper: bool) -> Vec<String> {
    let mut names = vec![format!("{stem}.cs")];
    if wrapper {
        names.push(format!("{class_name}.cs"));
        names.push(format!("{stem}.exe.config"));
        names.push(format!("{class_name}.exe.config"));
    }
    names.push(format!("{class_name}.csproj"));
    names
}

/// A Procedure's emitted C#, as the artifact cache stores it. Only a successful emission is stored.
#[derive(Serialize, Deserialize)]
struct CachedEmission {
    stem: String,
    class_name: String,
    wrapper: Option<String>,
    code: String,
    callees: BTreeSet<String>,
}

/// One Procedure's emission: the files of the previous run when they were written from the same inputs
/// and are still on disk as written, else the artifact cache's entry for those inputs, else the emitter.
fn reuse_or_emit(
    plan: &ProcedurePlan<'_>,
    inputs: &str,
    target_framework: &str,
    session: &Session<'_>,
    context: &LazyContext<'_>,
) -> ProcedureEmission {
    let proc = plan.proc;
    // The emitted text reads the object and the build's SDTs and signatures; its files also read the
    // target framework (the Procedure's own project).
    let emit_key = key_of("emission", [plan.content.as_str(), inputs]);
    let key = key_of("written emission", [emit_key.as_str(), target_framework]);
    let emission = |kept, stem, class_name, wrapper, emitted| ProcedureEmission {
        id: proc.id.as_str().to_string(),
        key: key.clone(),
        kept,
        name: proc.name.clone(),
        class: proc.name.to_ascii_uppercase(),
        stem,
        class_name,
        wrapper,
        emitted,
    };

    let recorded = session
        .previous_procedure(proc.id.as_str())
        .and_then(|record| record.emit.as_ref())
        .filter(|record| record.key == key)
        .filter(|record| {
            procedure_file_names(&record.stem, &record.class_name, record.wrapper)
                .iter()
                .all(|name| session.is_intact(name))
        });
    if let Some(record) = recorded {
        session.procedures_kept.fetch_add(1, Ordering::Relaxed);
        return emission(
            true,
            record.stem.clone(),
            record.class_name.clone(),
            record.wrapper.then(String::new),
            CheckedEmit {
                code: Ok(String::new()),
                callees: record.callees.clone(),
            },
        );
    }

    session.procedures_emitted.fetch_add(1, Ordering::Relaxed);
    if let Some(cached) = session
        .cache()
        .and_then(|cache| cache.get::<CachedEmission>(EMIT_KIND, &emit_key))
    {
        return emission(
            false,
            cached.stem,
            cached.class_name,
            cached.wrapper,
            CheckedEmit {
                code: Ok(cached.code),
                callees: cached.callees,
            },
        );
    }
    let (stem, class_name, wrapper, emitted) = emit_procedure(proc, &context.get().procedure);
    if let (Some(cache), Ok(code)) = (session.cache(), &emitted.code) {
        cache.put(
            EMIT_KIND,
            &emit_key,
            &CachedEmission {
                stem: stem.clone(),
                class_name: class_name.clone(),
                wrapper: wrapper.clone(),
                code: code.clone(),
                callees: emitted.callees.clone(),
            },
        );
    }
    emission(false, stem, class_name, wrapper, emitted)
}

/// Removes the previous output when `clean` is set and creates the directory. A directory that is already
/// absent is clean; any other failure to remove it stops the rebuild, because a `.cs` file left behind
/// would be compiled into the project and counted as generated.
fn prepare_output_dir(output_dir: &Path, clean: bool) -> Result<(), String> {
    if clean {
        clean_output_dir(output_dir).map_err(|e| {
            format!(
                "cannot clean output directory '{}': {e}",
                output_dir.display()
            )
        })?;
    }
    std::fs::create_dir_all(output_dir).map_err(|e| {
        format!(
            "cannot create output directory '{}': {e}",
            output_dir.display()
        )
    })
}

/// A Rebuild's clean: every generated file and build output is removed, and `obj/` is kept as
/// `dotnet clean` keeps it (the restore outputs), with the kept compiler command, which `csc_direct`
/// checks against its key before any use. The compile record goes, so everything is compiled again.
fn clean_output_dir(output_dir: &Path) -> std::io::Result<()> {
    let entries = match std::fs::read_dir(output_dir) {
        Ok(entries) => entries,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e),
    };
    for entry in entries {
        let entry = entry?;
        let path = entry.path();
        if entry.file_type()?.is_dir() {
            if entry.file_name() == "obj" {
                let record = output_dir
                    .join(crate::csc_direct::CACHE_DIR)
                    .join(crate::csc_direct::BUILT_FILE);
                match std::fs::remove_file(record) {
                    Err(e) if e.kind() != std::io::ErrorKind::NotFound => return Err(e),
                    _ => {}
                }
                // The record of what the removed files were generated from goes with them.
                match std::fs::remove_dir_all(output_dir.join(super::reuse::MANIFEST_DIR)) {
                    Err(e) if e.kind() != std::io::ErrorKind::NotFound => return Err(e),
                    _ => {}
                }
            } else {
                std::fs::remove_dir_all(path)?;
            }
        } else {
            std::fs::remove_file(path)?;
        }
    }
    Ok(())
}

/// The SDT classes that build, with the namespaces they are imported through.
///
/// An SDT held by a module is declared in that module's namespace; every emitted class imports
/// those namespaces, so a type of an SDT another module holds resolves by its class name. Root-only
/// builds import nothing extra.
fn build_sdt_classes(
    manifest: &ObjectManifest<'_>,
    errors: &mut Vec<String>,
) -> (Vec<SdtClass>, Vec<String>) {
    let mut sdt_classes: Vec<SdtClass> = Vec::with_capacity(manifest.sdts.len());
    for sdt in &manifest.sdts {
        match SdtClass::from_kb_object(sdt) {
            Ok(class) => sdt_classes.push(class),
            Err(e) => errors.push(format!("failed to generate C# for SDT '{}': {e}", sdt.name)),
        }
    }
    let sdt_usings: Vec<String> = sdt_classes
        .iter()
        .map(|class| class.namespace.clone())
        .filter(|namespace| namespace != crate::csharp::types::ROOT_NAMESPACE)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    let sdt_classes = sdt_classes
        .into_iter()
        .map(|class| class.with_usings(sdt_usings.clone()))
        .collect();
    (sdt_classes, sdt_usings)
}

/// A Procedure variable typed by a nested SDT level resolves its members through that level's
/// class, so each level class an SDT emits is indexed beside the SDT. A clash fails the index.
fn build_sdt_index(sdt_classes: &[SdtClass], errors: &mut Vec<String>) -> Arc<SdtIndex> {
    let indexed: Vec<SdtClass> = sdt_classes
        .iter()
        .flat_map(|class| std::iter::once(class.clone()).chain(class.level_classes()))
        .collect();
    Arc::new(match SdtIndex::from_classes(indexed) {
        Ok(index) => index,
        Err(e) => {
            errors.push(format!(
                "the SDT index is not built, so no Procedure member resolves: {e}"
            ));
            SdtIndex::default()
        }
    })
}

/// The parameters of every Procedure this build emits. A call site is written from its callee's
/// parameters (`ref`/`out` keywords and temporaries), so the table is complete before any emission.
fn build_call_table(plans: &[ProcedurePlan<'_>], errors: &mut Vec<String>) -> Arc<CallSignatures> {
    let mut call_table = CallSignatures::new();
    for plan in plans {
        if let Some(target) = &plan.target {
            if let Err(e) = call_table.insert(target.clone()) {
                errors.push(format!("Procedure '{}': {e}", plan.proc.name));
            }
        }
    }
    Arc::new(call_table)
}

/// Emits one Procedure: the stem of its main file, its plain class name, the wrapper of a split main
/// program, and the checked emission.
fn emit_procedure(
    proc: &KbObject,
    context: &ProcedureContext,
) -> (String, String, Option<String>, CheckedEmit) {
    match ProcedureClass::from_kb_object(proc).and_then(|class| {
        if proc.kind == crate::catalog::ObjectKind::DataProvider {
            crate::csharp::data_provider::resolve_collection_calls(class, &context.call_table)
        } else {
            Ok(class)
        }
    }) {
        Ok(class) => {
            let stem = class.emitted_class_name();
            let class_name = class.class_name();
            let wrapper = class.emit_wrapper();
            let emitted = class
                .with_usings(context.sdt_usings.clone())
                .with_sdts(context.sdt_index.clone())
                .with_calls(context.call_table.clone())
                .emit_detailed();
            (stem, class_name, wrapper, emitted)
        }
        Err(e) => (
            String::new(),
            String::new(),
            None,
            CheckedEmit {
                code: Err(e),
                callees: BTreeSet::new(),
            },
        ),
    }
}

/// A caller is generated only when every Procedure it calls is generated. A Procedure whose callee is
/// not generated (refused, or refused at emission) is refused by name, and so is each caller of it, until
/// nothing changes. The reason names the callee and says why it is not generated.
fn refuse_callers_of_ungenerated(emissions: &mut [ProcedureEmission]) {
    // class key -> (name as declared, reason it is not generated)
    let mut ungenerated: BTreeMap<String, (String, String)> = BTreeMap::new();
    for emission in emissions.iter() {
        if let Err(reason) = &emission.emitted.code {
            ungenerated.insert(
                emission.class.clone(),
                (emission.name.clone(), reason.clone()),
            );
        }
    }
    loop {
        let mut changed = false;
        for emission in emissions.iter_mut() {
            if emission.emitted.code.is_err() {
                continue;
            }
            let Some(callee) = emission
                .emitted
                .callees
                .iter()
                .find(|key| ungenerated.contains_key(*key))
                .cloned()
            else {
                continue;
            };
            let (callee_name, callee_reason) = ungenerated[&callee].clone();
            let reason =
                format!("needs Procedure '{callee_name}' (not generated: {callee_reason})");
            emission.emitted.code = Err(reason.clone());
            ungenerated.insert(emission.class.clone(), (emission.name.clone(), reason));
            changed = true;
        }
        if !changed {
            break;
        }
    }
}

/// Writes each generated Procedure with its project; a refused one is an error naming it.
fn write_procedures(
    out: &mut OutputTree<'_>,
    emissions: &[ProcedureEmission],
    target_framework: &str,
) {
    for emission in emissions {
        let code = match &emission.emitted.code {
            Ok(code) => code,
            Err(e) => {
                out.errors.push(format!(
                    "failed to generate C# for Procedure '{}': {e}",
                    emission.name
                ));
                continue;
            }
        };
        if emission.kept {
            for file_name in procedure_file_names(
                &emission.stem,
                &emission.class_name,
                emission.wrapper.is_some(),
            ) {
                out.keep_file(&file_name);
            }
            continue;
        }
        out.write_generated_file(&format!("{}.cs", emission.stem), code);
        let mut includes = vec![format!("{}.cs", emission.stem)];
        if let Some(wrapper) = &emission.wrapper {
            out.write_generated_file(&format!("{}.cs", emission.class_name), wrapper);
            includes.push(format!("{}.cs", emission.class_name));
            // The SDK writes one runtime configuration per class of a main program, the same
            // text for every one (all 262 `*.exe.config` of the KBTeste NetModel are equal).
            for stem in [&emission.stem, &emission.class_name] {
                out.write_generated_file(
                    &format!("{stem}.exe.config"),
                    &EXE_CONFIG.replace('\n', "\r\n"),
                );
            }
        }

        let mut cfg = new_csproj_config(target_framework, &emission.class_name);
        cfg.compile_includes = includes;
        out.write_generated_file(
            &format!("{}.csproj", emission.class_name),
            &CsprojEmitter::emit(&cfg),
        );
    }
}

fn new_csproj_config(target_framework: &str, assembly_name: &str) -> CsprojConfig {
    if target_framework.starts_with("net9") {
        CsprojConfig::net9(assembly_name, false)
    } else {
        CsprojConfig::net8(assembly_name, false)
    }
}

/// Plain WebPanels: one class each, in the SDK shape (see csharp::webform), with the web model's client
/// configuration and the KB's `GxWebStd` helper.
fn write_web_classes(
    out: &mut OutputTree<'_>,
    manifest: &ObjectManifest<'_>,
    options: &RebuildOptions,
) {
    // The stamp is the generation time, the token the SDK writes into a panel's script URLs, unless the
    // run fixes it.
    let stamp = options
        .generation_stamp
        .clone()
        .unwrap_or_else(webform::current_stamp);
    for panel in &manifest.webpanels {
        out.write_stamped_file(&panel.file_name(), &panel.emit(&stamp), &stamp);
        match panel.script() {
            Ok((file_name, text)) => out.write_generated_file(&file_name, &text),
            Err(error) => out
                .errors
                .push(format!("client script of '{}': {error}", panel.file_name())),
        }
    }
    if manifest.webpanels.is_empty() {
        return;
    }
    // The web model's client configuration, which every emitted class's script loads.
    match &options.client_config {
        Some(Ok(text)) => out.write_generated_file("gxcfg.js", text),
        Some(Err(error)) => out.errors.push(format!("gxcfg.js: {error}")),
        None => {}
    }
    // The KB's own GxWebStd helper, which every emitted panel calls; written only with a panel.
    if let Some(support) = options.web_environment.support_class.as_deref() {
        out.write_generated_file("GxWebStd.cs", support);
    }
}

/// Companion Business Component classes emitted for Transactions flagged as business components.
fn write_business_components(out: &mut OutputTree<'_>, manifest: &ObjectManifest<'_>) {
    for txn in &manifest.transactions {
        match crate::csharp::transaction::TransactionClass::from_kb_object(txn) {
            Ok(mut tc) if tc.is_business_component => {
                let bc_name = format!("{}_bc", txn.name.to_lowercase());
                let file_name = format!("{bc_name}.cs");
                tc.name = bc_name;
                out.write_generated_file(&file_name, &tc.emit());
            }
            Ok(_) => {}
            Err(error) => out
                .errors
                .push(format!("business component '{}': {error}", txn.name)),
        }
    }
}

/// The model's support classes, which the SDK writes on every generation whatever the KB holds
/// (KBTeste NetModel/Web after SpecifyAllOnly + GenerateOnly). Their text is fixed for the
/// `GeneXus.Programs` namespace; GxWebServicesConfig.cs is written empty, as the SDK writes it.
/// They belong to a KB's model, so they are written for a run read from a KB (its GxWebStd is
/// configured) that generates anything at all.
fn write_model_support(out: &mut OutputTree<'_>, options: &RebuildOptions) {
    if options.web_environment.support_class.is_none() || out.files.is_empty() {
        return;
    }
    for (file_name, text) in SUPPORT_FILES {
        out.write_generated_file(file_name, text);
    }
    for (label, group) in &options.model_files {
        match group {
            Ok(files) => {
                for (path, bytes) in files {
                    out.write_model_file(path, bytes);
                }
            }
            Err(error) => out.errors.push(format!("{label}: {error}")),
        }
    }
}

/// Writes each SDT in parallel; a thread fills its own tree and the trees merge in chunk order. With
/// `collect`, returns every file as (file name, text) in write order, for the artifact cache.
fn write_sdts(
    out: &mut OutputTree<'_>,
    sdt_classes: &[SdtClass],
    concurrency: usize,
    collect: bool,
) -> Vec<(String, String)> {
    let dir = out.dir;
    let mut texts = Vec::new();
    for (tree, chunk_texts) in map_chunks(sdt_classes, concurrency, |chunk| {
        let mut tree = OutputTree::new(dir);
        let mut texts = Vec::new();
        for sdt_class in chunk {
            // The SDK's form (`type_Sdt<class>.cs`, one file per class) when the SDT is a shape the
            // SDK evidence covers; otherwise the portable class, one file for the SDT and its levels.
            let files = match crate::csharp::sdt_sdk::sdk_files(sdt_class) {
                Ok(files) => files,
                Err(_) => vec![(format!("{}.cs", sdt_class.class_name()), sdt_class.emit())],
            };
            for (file_name, text) in files {
                tree.write_generated_file(&file_name, &text);
                if collect {
                    texts.push((file_name, text));
                }
            }
        }
        (tree, texts)
    }) {
        out.merge(tree);
        texts.extend(chunk_texts);
    }
    texts
}

/// Writes the main unified project file (.csproj) that compiles every emitted `.cs` file.
fn write_main_project(
    out: &mut OutputTree<'_>,
    options: &RebuildOptions,
) -> Result<PathBuf, String> {
    let main_asm_name = options
        .assembly_name
        .clone()
        .unwrap_or_else(|| "RebuildApp".to_string());
    let mut main_cfg = new_csproj_config(&options.target_framework, &main_asm_name);

    let mut cs_includes: Vec<String> = out
        .files
        .iter()
        .filter(|f| f.extension().and_then(|ext| ext.to_str()) == Some("cs"))
        .filter_map(|f| f.file_name().and_then(|n| n.to_str()))
        // The SDK writes GxObjectCollection.cs but compiles it into no project: its
        // genexus.programs.common.bld lists the other support files only.
        .filter(|file_name| *file_name != "GxObjectCollection.cs")
        .map(str::to_string)
        .collect();
    cs_includes.sort();
    main_cfg.compile_includes = cs_includes;
    // The generated classes derive from the GeneXus runtime. GeneXus adds this reference
    // to every object project through DotNetCoreBaseProject.targets; without it the
    // emitted transactions and procedures cannot resolve their base types.
    main_cfg = main_cfg.add_package_reference(
        "GeneXus.Classes.Web.Core",
        options.genexus_runtime_version.clone(),
    );

    // A closure build is an executable that starts at its entry's core class, as the SDK's own build of
    // that object does. The full rebuild keeps the library contract.
    if let Some(startup) = &options.startup_object {
        main_cfg = main_cfg.as_executable(startup.clone());
    }
    let main_csproj_path = out.dir.join(format!("{main_asm_name}.csproj"));
    std::fs::write(&main_csproj_path, CsprojEmitter::emit(&main_cfg)).map_err(|e| {
        format!(
            "failed to write main project file '{}': {e}",
            main_csproj_path.display()
        )
    })?;
    out.files.push(main_csproj_path.clone());
    Ok(main_csproj_path)
}
