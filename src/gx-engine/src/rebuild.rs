//! High-performance parallel Rebuild All pipeline.
//!
//! Replaces the legacy 8-minute GeneXus IDE and MSBuild rebuild workflow by:
//! 1. Specifying the entire Knowledge Base concurrently with scoped threads.
//! 2. Emitting idiomatic C# (.NET) source and SDK-compatible project definitions.
//! 3. Driving multi-core Roslyn builds with structured diagnostic capture.

#![forbid(unsafe_code)]

mod artifact_cache;
mod classify;
mod compile;
mod emission;
mod parallel;
mod reuse;
mod specify;

use crate::{
    catalog::{Catalog, ObjectId, ObjectKind},
    csharp::{ProcedureClass, webform::WebPanelEnvironment},
    navigation::NavigationReport,
    table::SqlDialect,
};
use classify::classify_objects;
use compile::compile_phase;
use emission::emission_phase;
use parallel::Findings;
use reuse::Session;
use serde::{Deserialize, Serialize};
use specify::specify_phase;
use std::{
    collections::{BTreeMap, BTreeSet},
    path::{Path, PathBuf},
    time::Instant,
};

/// Configuration options for the Rebuild All pipeline.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RebuildOptions {
    /// Target .NET runtime framework (e.g. "net8.0", "net9.0").
    pub target_framework: String,
    /// Whether to clean the output directory before generating files.
    pub clean: bool,
    /// Concurrency level for parallel phases (0 = automatic based on available cores).
    pub concurrency: usize,
    /// SQL dialect for SQL navigation and table schema derivation.
    pub sql_dialect: SqlDialect,
    /// Whether to abort on the first encountered error.
    pub fail_fast: bool,
    /// Whether to skip invoking `dotnet build` compiler after emission.
    pub skip_compile: bool,
    /// Optional application assembly name.
    pub assembly_name: Option<String>,
    /// NuGet version of `GeneXus.Classes.Web.Core`, the runtime the generated classes
    /// derive from. Written as declared by the KB (`[1.30.0]`), see
    /// [`genexus_runtime_version_from_kb`].
    pub genexus_runtime_version: String,
    /// Explicit configuration for plain WebPanels (theme, generator, master class paths and the
    /// build number). An unset value refuses every plain panel by name; see [`WebPanelEnvironment`].
    pub web_environment: WebPanelEnvironment,
    /// The class whose `Main` starts the application when the main project is an executable. Set only
    /// by a single-object closure build (see [`closure_startup_object`]); the full rebuild stays a library.
    pub startup_object: Option<String>,
    /// The web model's client configuration script `gxcfg.js`, or why it is not written (see
    /// [`crate::csharp::webform::web_client_config_from_kb`]). Written with the web classes; a reason
    /// here fails a run that emits one.
    pub client_config: Option<Result<String, String>>,
    /// Names of GeneXus library structured data types whose classes the GeneXus runtime assemblies
    /// already define (see [`runtime_sdt_names_from_kb`]). Such an SDT is never emitted, even when a
    /// generated object reaches it: the SDK's generated code uses the runtime class (KBTeste `WWTrn353`
    /// uses the runtime `GridState`, and the SDK output holds no `type_SdtGridState.cs`).
    pub runtime_sdts: BTreeSet<String>,
    /// The model files the SDK writes beside the classes that this run reproduces from the KB and the
    /// install (`messages.<lang>.txt/.js`, `log.config`, `CloudServices.config`; see
    /// [`crate::csharp::model_files`]), each group with its label, or why the group is not written. Written
    /// with the support files; a reason here fails a run that writes them.
    pub model_files: Vec<crate::csharp::model_files::ModelFileGroup>,
    /// Directory of the content-addressed artifact cache (`GXMCP_ENGINE_BUILD_CACHE_DIR`), which may be
    /// shared between output folders and machines. Unset, the default, reads and writes no cache.
    #[serde(default)]
    pub artifact_cache_dir: Option<PathBuf>,
    /// The generation stamp written into every web class (`yyyyMMddHHmmss`), instead of the current time
    /// (`GXMCP_ENGINE_GENERATION_STAMP`). A web class regenerated at another time differs only by this
    /// stamp, so a fixed one makes two rebuilds of one model comparable byte for byte.
    #[serde(default)]
    pub generation_stamp: Option<String>,
}

/// Environment variable naming the artifact cache directory (see [`RebuildOptions::artifact_cache_dir`]).
pub const BUILD_CACHE_DIR_VARIABLE: &str = "GXMCP_ENGINE_BUILD_CACHE_DIR";
/// Environment variable fixing the generation stamp (see [`RebuildOptions::generation_stamp`]).
pub const GENERATION_STAMP_VARIABLE: &str = "GXMCP_ENGINE_GENERATION_STAMP";

/// The GeneXus runtime assemblies a web model links (`NetModel/web/bin`), searched for SDT classes.
const RUNTIME_ASSEMBLIES: [&str; 2] = ["GxClasses.dll", "GxClasses.Web.dll"];

/// Which of `library_sdts` the KB's GeneXus runtime assemblies define: a name whose class `Sdt<Name>` is a
/// metadata string (NUL-terminated) of `NetModel/web/bin/GxClasses.dll` or `GxClasses.Web.dll`. A KB
/// without those assemblies yields an empty set, so every reached library SDT is still emitted.
pub fn runtime_sdt_names_from_kb<'a>(
    kb_dir: &Path,
    library_sdts: impl IntoIterator<Item = &'a str>,
) -> BTreeSet<String> {
    let bin = kb_dir.join("NetModel").join("web").join("bin");
    let assemblies: Vec<Vec<u8>> = RUNTIME_ASSEMBLIES
        .iter()
        .filter_map(|name| std::fs::read(bin.join(name)).ok())
        .collect();
    // One pass over the assemblies: every `Sdt<name>\0` they contain, at any offset. A library SDT
    // is runtime-provided when its `Sdt<name>\0` occurs, as with a search per name.
    let mut defined: std::collections::HashSet<&[u8]> = std::collections::HashSet::new();
    for bytes in &assemblies {
        let mut at = 0;
        while let Some(found) = bytes[at..].windows(3).position(|w| w == b"Sdt") {
            let start = at + found + 3;
            if let Some(end) = bytes[start..].iter().position(|&b| b == 0) {
                defined.insert(&bytes[start..start + end]);
            }
            at = at + found + 1;
        }
    }
    library_sdts
        .into_iter()
        .filter(|name| defined.contains(name.as_bytes()))
        .map(str::to_string)
        .collect()
}

/// The runtime version used when the KB declares none (a KB that was never built has no
/// `NetModel/Directory.build.props`). This is the GeneXus 18 runtime available in the
/// NuGet cache; a GeneXus 16 KB without a declaration is compiled against it and the
/// result is reported as the build outcome, not assumed.
pub const DEFAULT_GENEXUS_RUNTIME_VERSION: &str = "[1.30.0]";

/// Reads `GenStdVersion` from a KB's `NetModel/Directory.build.props`, the file GeneXus
/// uses to pin the runtime for every object project. Returns `None` when the file or the
/// property is absent, and for a value that is not a version range.
pub fn genexus_runtime_version_from_kb(kb_dir: &Path) -> Option<String> {
    let props = kb_dir.join("NetModel").join("Directory.build.props");
    let text = std::fs::read_to_string(props).ok()?;
    let document = roxmltree::Document::parse(&text).ok()?;
    let value = document
        .descendants()
        .find(|node| node.has_tag_name("GenStdVersion"))?
        .text()?
        .trim()
        .to_string();
    let well_formed = value
        .trim_start_matches('[')
        .trim_end_matches(']')
        .chars()
        .all(|c| c.is_ascii_digit() || c == '.')
        && value.chars().any(|c| c.is_ascii_digit());
    well_formed.then_some(value)
}

impl Default for RebuildOptions {
    fn default() -> Self {
        Self {
            target_framework: "net8.0".to_string(),
            clean: true,
            concurrency: 0,
            sql_dialect: SqlDialect::SqlServer,
            fail_fast: false,
            skip_compile: false,
            assembly_name: None,
            genexus_runtime_version: DEFAULT_GENEXUS_RUNTIME_VERSION.to_string(),
            web_environment: WebPanelEnvironment::default(),
            startup_object: None,
            client_config: None,
            runtime_sdts: BTreeSet::new(),
            model_files: Vec::new(),
            artifact_cache_dir: None,
            generation_stamp: None,
        }
    }
}

impl RebuildOptions {
    pub fn new(target_framework: impl Into<String>) -> Self {
        Self {
            target_framework: target_framework.into(),
            ..Default::default()
        }
    }

    pub fn with_model_files(
        mut self,
        model_files: Vec<crate::csharp::model_files::ModelFileGroup>,
    ) -> Self {
        self.model_files = model_files;
        self
    }

    pub fn with_client_config(mut self, client_config: Result<String, String>) -> Self {
        self.client_config = Some(client_config);
        self
    }

    pub fn with_web_environment(mut self, web_environment: WebPanelEnvironment) -> Self {
        self.web_environment = web_environment;
        self
    }

    pub fn with_concurrency(mut self, concurrency: usize) -> Self {
        self.concurrency = concurrency;
        self
    }

    pub fn with_sql_dialect(mut self, dialect: SqlDialect) -> Self {
        self.sql_dialect = dialect;
        self
    }

    pub fn with_clean(mut self, clean: bool) -> Self {
        self.clean = clean;
        self
    }

    pub fn with_fail_fast(mut self, fail_fast: bool) -> Self {
        self.fail_fast = fail_fast;
        self
    }

    /// Builds the main project as an executable that starts at `Main` of `startup_object`.
    pub fn with_startup_object(mut self, startup_object: impl Into<String>) -> Self {
        self.startup_object = Some(startup_object.into());
        self
    }

    pub fn with_skip_compile(mut self, skip: bool) -> Self {
        self.skip_compile = skip;
        self
    }

    pub fn with_assembly_name(mut self, name: impl Into<String>) -> Self {
        self.assembly_name = Some(name.into());
        self
    }

    pub fn with_genexus_runtime_version(mut self, version: impl Into<String>) -> Self {
        self.genexus_runtime_version = version.into();
        self
    }

    pub fn with_artifact_cache_dir(mut self, dir: impl Into<PathBuf>) -> Self {
        self.artifact_cache_dir = Some(dir.into());
        self
    }

    /// Fixes the generation stamp. It is the 14 digits the SDK writes (`yyyyMMddHHmmss`); anything else
    /// would change the shape of a web class, so it is refused.
    pub fn with_generation_stamp(mut self, stamp: impl Into<String>) -> Result<Self, String> {
        let stamp = stamp.into();
        if stamp.len() != 14 || !stamp.bytes().all(|byte| byte.is_ascii_digit()) {
            return Err(format!(
                "the generation stamp must be 14 digits (yyyyMMddHHmmss), got '{stamp}'"
            ));
        }
        self.generation_stamp = Some(stamp);
        Ok(self)
    }

    /// Applies the settings a process reads from its environment: the artifact cache directory and the
    /// fixed generation stamp. An unset or empty variable leaves the option as it is.
    pub fn with_process_environment(self) -> Result<Self, String> {
        self.with_environment(|name| std::env::var(name).ok())
    }

    /// `with_process_environment` over the given variable lookup.
    pub fn with_environment(
        mut self,
        variable: impl Fn(&str) -> Option<String>,
    ) -> Result<Self, String> {
        let set = |name: &str| variable(name).filter(|value| !value.trim().is_empty());
        if let Some(dir) = set(BUILD_CACHE_DIR_VARIABLE) {
            self = self.with_artifact_cache_dir(dir);
        }
        if let Some(stamp) = set(GENERATION_STAMP_VARIABLE) {
            self = self
                .with_generation_stamp(stamp)
                .map_err(|error| format!("{GENERATION_STAMP_VARIABLE}: {error}"))?;
        }
        Ok(self)
    }
}

/// A catalog object the rebuild did not turn into a compile unit, by name and kind.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SkippedObject {
    pub name: String,
    pub kind: String,
    pub reason: String,
}

/// Outcome of a rebuild. Only `Complete` is success.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RebuildStatus {
    /// Every generated object was emitted and the project built; nothing was skipped.
    Complete,
    /// The run is not a full build: web UI objects were skipped, a code kind has no
    /// generator, or compilation did not run (for example, dotnet is missing).
    Partial,
    /// At least one error was recorded.
    Failed,
}

/// Structured execution summary and timing report produced by `rebuild_all`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RebuildReport {
    pub read_ms: u64,
    pub spec_ms: u64,
    pub emit_ms: u64,
    pub compile_ms: u64,
    pub total_ms: u64,
    /// Time to load the catalog before the pipeline (a KB read, or a snapshot when the cache is used). It is
    /// not part of `total_ms`. The CLI measures and sets it; a library call leaves it at zero.
    #[serde(default)]
    pub catalog_load_ms: u64,

    pub procedures_count: usize,
    pub transactions_count: usize,
    pub sdts_count: usize,
    pub webpanels_count: usize,
    pub total_objects: usize,

    /// `complete`, `partial` or `failed`; see [`RebuildStatus`].
    pub status: RebuildStatus,
    /// C# compile units emitted (Procedures and SDTs).
    pub generated_count: usize,
    /// Compile units of a project that built; 0 when compilation did not run or failed.
    pub compiled_count: usize,
    /// Web UI objects (Transactions and WebPanels) outside a matched shape, reason
    /// `web UI, not generated: <why>`.
    pub skipped_count: usize,
    pub skipped_objects: Vec<SkippedObject>,
    /// Objects of kinds this engine has no generator for (DataProvider, DataSelector,
    /// DataView), reason `no generator, not generated`.
    pub not_generated_count: usize,
    pub not_generated_objects: Vec<SkippedObject>,
    /// GeneXus library structured data types that no generated object reaches. They are not built and
    /// are not an error: the GeneXus runtime provides them (see `crate::reachability`).
    #[serde(default)]
    pub not_targeted_count: usize,
    #[serde(default)]
    pub not_targeted_objects: Vec<SkippedObject>,
    /// Procedures refused because they need a web UI or not-generated object. Each one is also
    /// an error, so the run is `failed`, never `partial`.
    pub refused_count: usize,
    pub refused_objects: Vec<SkippedObject>,
    /// Objects with no compile unit by nature (Module, Domain, Attribute, ...), by kind name.
    pub definition_kinds: BTreeMap<String, usize>,

    pub errors: Vec<String>,
    pub warnings: Vec<String>,
    pub emitted_files: Vec<PathBuf>,
    pub navigation_reports: Vec<NavigationReport>,
    /// What this run reused from the previous run in the same output directory and from the artifact
    /// cache. It describes how the outcome was reached; every other field is the same for a run that
    /// reused nothing.
    #[serde(default)]
    pub reuse: ReuseSummary,
}

/// What a rebuild reused instead of generating (see `rebuild/reuse.rs`).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReuseSummary {
    /// `unchanged`: the model is the one the previous run generated, so nothing was generated.
    /// `incremental`: the previous run's record was used, object by object. `full`: nothing was reused.
    pub mode: String,
    /// Why nothing was reused, when `mode` is `full`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    /// Procedures whose files of the previous run were kept, and those emitted or restored in this run.
    pub procedures_kept: usize,
    pub procedures_emitted: usize,
    /// Procedure specifications taken from the previous run, and those computed or restored in this run.
    pub specifications_kept: usize,
    pub specifications_computed: usize,
    /// Whether the structured data type files of the previous run were kept.
    pub sdts_kept: bool,
    /// Files the previous run wrote that this run no longer produces, removed from the output.
    pub stale_files_removed: usize,
    /// Artifact cache entries read into this run, and entries this run published.
    pub cache_entries_restored: usize,
    pub cache_entries_stored: usize,
}

/// Everything a run settles before it compiles: the classification, the findings of specification and
/// emission, and the files of the model. It is what an unchanged model reports again without generating.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct Generation {
    procedures_count: usize,
    transactions_count: usize,
    sdts_count: usize,
    webpanels_count: usize,
    total_objects: usize,
    skipped: Vec<SkippedObject>,
    not_generated: Vec<SkippedObject>,
    not_targeted: Vec<SkippedObject>,
    refused: Vec<SkippedObject>,
    definitions: BTreeMap<String, usize>,
    errors: Vec<String>,
    warnings: Vec<String>,
    // Recorded apart: the reports with each Procedure, the files by their names in the output.
    #[serde(skip)]
    navigation_reports: Vec<NavigationReport>,
    #[serde(skip)]
    emitted_files: Vec<PathBuf>,
    /// The main project, or `None` when nothing was emitted.
    #[serde(skip)]
    main_project: Option<PathBuf>,
    #[serde(skip)]
    read_ms: u64,
    #[serde(skip)]
    spec_ms: u64,
    #[serde(skip)]
    emit_ms: u64,
}

impl RebuildReport {
    /// True only for a complete rebuild. A partial run is never reported as success.
    pub fn is_success(&self) -> bool {
        self.status == RebuildStatus::Complete
    }
}

/// Executes the end-to-end parallel Rebuild All pipeline.
///
/// A run reuses what the previous run into `output_dir` settled for the same inputs (see
/// `rebuild/reuse.rs`): an unchanged model is not generated again, and a changed one regenerates the
/// objects whose inputs changed. The output directory and the report are those of a run that reuses
/// nothing; `RebuildReport::reuse` says what was reused.
pub fn rebuild_all(
    catalog: &Catalog,
    output_dir: &Path,
    options: &RebuildOptions,
) -> Result<RebuildReport, String> {
    let total_start = Instant::now();

    let worker_concurrency = if options.concurrency == 0 {
        std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4)
    } else {
        options.concurrency
    };

    let mut session = Session::open(catalog, output_dir, options);
    let generation = match session.unchanged_generation() {
        Some(mut generation) => {
            generation.read_ms = total_start.elapsed().as_millis() as u64;
            generation
        }
        None => generate(
            catalog,
            output_dir,
            options,
            worker_concurrency,
            &mut session,
        )?,
    };
    let reuse = session.summary();
    eprintln!("generation: {}", describe_reuse(&reuse));

    let Generation {
        procedures_count,
        transactions_count,
        sdts_count,
        webpanels_count,
        total_objects,
        skipped,
        not_generated,
        not_targeted,
        refused,
        definitions,
        errors,
        warnings,
        navigation_reports,
        emitted_files,
        main_project,
        read_ms,
        spec_ms,
        emit_ms,
    } = generation;
    let mut findings = Findings { errors, warnings };

    // Phase 3: Parallel Compilation Driver (`dotnet build`)
    let (compile_ms, built) = match &main_project {
        Some(main_csproj_path) if !options.skip_compile => {
            compile_phase(main_csproj_path, worker_concurrency, &mut findings)
        }
        _ => (0, false),
    };

    let total_ms = total_start.elapsed().as_millis() as u64;
    let Findings {
        errors: final_errors,
        warnings: final_warnings,
    } = findings;

    let generated_count = emitted_files
        .iter()
        .filter(|path| path.extension().and_then(|ext| ext.to_str()) == Some("cs"))
        .count();
    // Partial whenever the run is not a full build: a web UI object was skipped, a code
    // kind has no generator, or the project did not build. A partial run is never success.
    let status = if !final_errors.is_empty() {
        RebuildStatus::Failed
    } else if !built || !skipped.is_empty() || !not_generated.is_empty() {
        RebuildStatus::Partial
    } else {
        RebuildStatus::Complete
    };

    Ok(RebuildReport {
        read_ms,
        spec_ms,
        emit_ms,
        compile_ms,
        total_ms,
        catalog_load_ms: 0,
        procedures_count,
        transactions_count,
        sdts_count,
        webpanels_count,
        total_objects,
        status,
        generated_count,
        compiled_count: if built { generated_count } else { 0 },
        skipped_count: skipped.len(),
        skipped_objects: skipped,
        not_generated_count: not_generated.len(),
        not_generated_objects: not_generated,
        not_targeted_count: not_targeted.len(),
        not_targeted_objects: not_targeted,
        refused_count: refused.len(),
        refused_objects: refused,
        definition_kinds: definitions,
        errors: final_errors,
        warnings: final_warnings,
        emitted_files,
        navigation_reports,
        reuse,
    })
}

/// One line for the build log: what this run reused.
fn describe_reuse(reuse: &ReuseSummary) -> String {
    match reuse.mode.as_str() {
        "unchanged" => "skipped, the model is unchanged since the previous run".to_string(),
        "full" => format!(
            "full ({}), {} artifact cache entries restored",
            reuse.reason.as_deref().unwrap_or("nothing to reuse"),
            reuse.cache_entries_restored
        ),
        _ => format!(
            "incremental, {} Procedures kept and {} emitted, {} specifications kept and {} computed, SDT files {}, {} stale files removed, {} artifact cache entries restored",
            reuse.procedures_kept,
            reuse.procedures_emitted,
            reuse.specifications_kept,
            reuse.specifications_computed,
            if reuse.sdts_kept { "kept" } else { "written" },
            reuse.stale_files_removed,
            reuse.cache_entries_restored
        ),
    }
}

/// Phases 0 to 2: classifies, specifies and emits the model, reusing what `session` allows, and records
/// the outcome for the next run.
fn generate(
    catalog: &Catalog,
    output_dir: &Path,
    options: &RebuildOptions,
    worker_concurrency: usize,
    session: &mut Session<'_>,
) -> Result<Generation, String> {
    // Phase 0: Object Classification
    let (manifest, read_ms) =
        classify_objects(catalog, &options.web_environment, &options.runtime_sdts);

    let mut findings = Findings::default();

    // A refused object was requested and cannot be built from this catalog: it is an error. The message names
    // the object's own kind: a refused Transaction is not a Procedure.
    for refused in &manifest.refused {
        findings.errors.push(format!(
            "refused to build {} '{}': {}",
            refused.kind, refused.name, refused.reason
        ));
    }

    // Phase 1: Parallel Specification
    let (nav_reports, spec_ms, mut specifications) = specify_phase(
        &manifest,
        catalog,
        worker_concurrency,
        options,
        &mut findings,
        session,
    )?;

    if options.fail_fast && !findings.errors.is_empty() {
        return Err(findings.errors.join("; "));
    }

    // A closure build starts at `Main` of its entry. When classification left nothing to generate (the
    // entry is stored as not main and nothing reaches it, so the SDK does not generate it), that class does
    // not exist: emitting a project that starts there only fails `dotnet build` with CS1555 (#675).
    let nothing_to_generate = manifest.procedures.is_empty()
        && manifest.transactions.is_empty()
        && manifest.sdts.is_empty()
        && manifest.webpanels.is_empty();
    let emission = if options.startup_object.is_some() && nothing_to_generate {
        findings.warnings.push(
            "nothing to generate: the closure entry is not targeted (see not_targeted_objects), so no project was emitted or compiled"
                .to_string(),
        );
        None
    } else {
        // Phase 2: Parallel C# & Project Code Emission
        let found = (findings.errors.len(), findings.warnings.len());
        let mut emission = emission_phase(
            &manifest,
            output_dir,
            worker_concurrency,
            options,
            &mut findings,
            session,
        )?;
        // Two objects wrote one file, and the later write decides it. A step that kept its files of the
        // previous run did not write, so the order of this run is not the order of a run that reuses
        // nothing: the model is emitted again without the previous run.
        if session.kept_any() && reuse::writes_one_file_twice(&emission.files) {
            findings.errors.truncate(found.0);
            findings.warnings.truncate(found.1);
            session.forget_previous("two generated objects write the same file");
            emission = emission_phase(
                &manifest,
                output_dir,
                worker_concurrency,
                options,
                &mut findings,
                session,
            )?;
        }
        Some(emission)
    };

    let webpanels_count = manifest
        .skipped
        .iter()
        .filter(|skipped| skipped.kind == "WebPanel")
        .count();
    let mut generation = Generation {
        // Every Procedure in the catalog: buildable, refused and not-generated alike. It counts Procedures
        // only; refused SDTs are reported under `refused_count`, and `sdts_count` is the SDTs emitted.
        procedures_count: catalog
            .objects()
            .filter(|object| object.kind == ObjectKind::Procedure)
            .count(),
        transactions_count: manifest.transactions.len(),
        sdts_count: manifest.sdts.len(),
        webpanels_count,
        total_objects: manifest.total_objects,
        skipped: manifest.skipped,
        not_generated: manifest.not_generated,
        not_targeted: manifest.not_targeted,
        refused: manifest.refused,
        definitions: manifest.definitions,
        errors: findings.errors,
        warnings: findings.warnings,
        navigation_reports: nav_reports,
        emitted_files: Vec::new(),
        main_project: None,
        read_ms,
        spec_ms,
        emit_ms: 0,
    };
    if let Some(emission) = emission {
        let mut procedures = emission.procedures;
        for record in &mut procedures {
            record.spec = specifications.remove(&record.id);
        }
        session.commit(reuse::Commit {
            emitted: &emission.files,
            main_project: &emission.main_project,
            generation: &generation,
            procedures,
            sdts: emission.sdts,
            call_key: emission.call_key,
            io_failed: emission.io_failed,
        });
        generation.emitted_files = emission.files;
        generation.main_project = Some(emission.main_project);
        generation.emit_ms = emission.emit_ms;
    }
    Ok(generation)
}

/// The entry point of a single-object closure build: the `Main` of the Procedure's core class, the class
/// the SDK starts. Only a parameterless Procedure has a `Main` (`ProcedureClass::is_split`); the class is
/// taken from the emitter, never guessed from a file name. Other kinds and parameterised Procedures are
/// refused by name, before anything is emitted.
pub fn closure_startup_object(catalog: &Catalog, entry: &ObjectId) -> Result<String, String> {
    let object = catalog
        .get(entry)
        .ok_or_else(|| format!("closure entry '{}' is not in the catalog", entry.as_str()))?;
    if object.kind != ObjectKind::Procedure {
        return Err(format!(
            "closure entry '{}' is a {:?}: only a Procedure has a Main to start",
            object.name, object.kind
        ));
    }
    let class = ProcedureClass::from_kb_object(object)?;
    if !class.is_split() {
        return Err(format!(
            "closure entry '{}' takes parameters, so it has no Main: a parameterised closure artifact is not implemented",
            object.name
        ));
    }
    Ok(format!(
        "{}.{}",
        class.namespace,
        class.emitted_class_name()
    ))
}
