//! Reuse of a previous rebuild's work in the same output directory.
//!
//! A rebuild used to specify and emit every object on every run; only the file writes and the compile
//! were skipped when their bytes were unchanged. This module records, beside the output
//! (`obj/gx-incremental/manifest.json`), what each reusable step read and produced, so a later run skips
//! the steps whose inputs are the same.
//!
//! What is reused, and the fingerprint (SHA-256) that has to match:
//!
//! - **The whole generation**, when the model is unchanged: the engine, every option that reaches the
//!   output, and every object of the catalog. Nothing is classified, specified or emitted; the recorded
//!   outcome is reported again and only the compile step runs (it has its own up-to-date check).
//! - **One Procedure's specification**: the engine, the object, the physical tables derived from the
//!   model, and the SQL dialect.
//! - **One Procedure's emission**: the engine, the object, every structured data type of the build (they
//!   form the SDT index and the imported namespaces), and the call signature of every Procedure of the
//!   build (the call table). A Procedure whose body changes keeps its signature, so only it is emitted
//!   again; a changed signature or SDT emits every Procedure again.
//! - **The SDT files**: the engine and every structured data type of the build.
//!
//! Web classes, business components, enumerated-domain classes, support files, Transaction formula
//! checks and the classification itself read the catalog at large; they are redone whenever anything
//! changed. Only successful results are reused: a refusal or an error is always computed again.
//!
//! A reused step does not rewrite its files, so each of them must still be on disk as the previous run
//! left it (same length and modification time, the test `csc_direct` applies to its outputs). A file that
//! fails the test makes the step run again. Files a previous run wrote that this run no longer produces
//! are removed, so the directory holds what a rebuild into an empty directory would write.
//!
//! The record is replaced as a whole (temporary file, then rename) at the end of a generation. A missing,
//! unreadable or other-format record means nothing is reused; a record written by another engine binary
//! only tells which files that engine wrote.

use super::{Generation, RebuildOptions, ReuseSummary, artifact_cache::ArtifactCache};
use crate::{
    catalog::{Catalog, KbObject, Reference, SourcePart},
    model::DataType,
    navigation::NavigationReport,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, BTreeSet, HashMap},
    fmt::Write as _,
    fs,
    io::Write as _,
    path::{Component, Path, PathBuf},
    sync::{
        OnceLock,
        atomic::{AtomicUsize, Ordering},
    },
};

/// Directory, relative to the output directory, that holds the record. It sits under `obj/` with the
/// kept compiler command: both are build state, not generated model files.
pub(super) const MANIFEST_DIR: &str = "obj/gx-incremental";
const MANIFEST_FILE: &str = "manifest.json";
/// Version of the record's schema and of the fingerprint recipes. Bump it with any change to either.
const MANIFEST_FORMAT: u32 = 1;

pub(super) fn hex(bytes: &[u8]) -> String {
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let _ = write!(text, "{byte:02x}");
    }
    text
}

struct HashWriter(Sha256);

impl std::fmt::Write for HashWriter {
    fn write_str(&mut self, text: &str) -> std::fmt::Result {
        self.0.update(text.as_bytes());
        Ok(())
    }
}

/// SHA-256 of a value's `Debug` text. Every type digested here derives `Debug` over all of its fields and
/// holds ordered collections only, so equal digests mean equal values for one engine binary (the engine's
/// identity is part of every fingerprint, so the text never has to be stable across binaries).
pub(super) fn digest_of(value: &impl std::fmt::Debug) -> String {
    let mut writer = HashWriter(Sha256::new());
    let _ = write!(writer, "{value:?}");
    hex(&writer.0.finalize())
}

/// A SHA-256 over values that are each framed by their length, so no two sequences of values share a
/// byte stream.
struct Framed(Sha256);

impl Framed {
    fn new() -> Self {
        Self(Sha256::new())
    }

    fn bytes(&mut self, value: &[u8]) {
        self.0.update((value.len() as u64).to_le_bytes());
        self.0.update(value);
    }

    fn text(&mut self, value: &str) {
        self.bytes(value.as_bytes());
    }

    /// A small value, by its `Debug` text.
    fn debug(&mut self, value: &impl std::fmt::Debug) {
        self.text(&format!("{value:?}"));
    }

    fn finish(self) -> String {
        hex(&self.0.finalize())
    }
}

/// The fingerprint of a step: its name and its inputs, each framed by its length.
pub(super) fn key_of<'p>(step: &'p str, parts: impl IntoIterator<Item = &'p str>) -> String {
    let mut hasher = Framed::new();
    for part in std::iter::once(step).chain(parts) {
        hasher.text(part);
    }
    hasher.finish()
}

/// The digest of a catalog object: every field, the source of each part as its bytes. The destructuring
/// names every field, so a field added to the catalog does not compile until it is digested here.
fn object_digest(object: &KbObject) -> String {
    let KbObject {
        id,
        name,
        kind,
        source_parts,
        references,
        system_root,
        refusal,
        not_generated,
        library,
        main,
        module_path,
    } = object;
    let mut hasher = Framed::new();
    hasher.text(id.as_str());
    hasher.text(name);
    hasher.debug(&(kind, system_root, refusal, not_generated, library, main));
    hasher.debug(module_path);
    hasher.debug(&source_parts.len());
    for SourcePart { name, source } in source_parts {
        hasher.text(name);
        hasher.text(source);
    }
    hasher.debug(&references.len());
    for Reference { kind, target } in references {
        hasher.debug(kind);
        hasher.text(target.as_str());
    }
    hasher.finish()
}

/// A file as a run left it: length and modification time.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub(super) struct FileStamp {
    len: u64,
    modified_ns: u128,
}

fn stamp_of(metadata: &fs::Metadata) -> Option<FileStamp> {
    Some(FileStamp {
        len: metadata.len(),
        modified_ns: metadata
            .modified()
            .ok()?
            .duration_since(std::time::UNIX_EPOCH)
            .ok()?
            .as_nanos(),
    })
}

/// The engine binary a record was written by: its content digest, with the file stamp it was read under
/// so an unchanged binary is not hashed again.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
struct EngineStamp {
    path: String,
    stamp: FileStamp,
    digest: String,
}

/// The identity of the running engine: the SHA-256 of its executable, with the crate version and the
/// record format. A rebuilt engine has another identity even under the same version number, so a changed
/// emitter never reuses what an earlier build of it wrote. `None` when the executable cannot be read.
fn engine_identity(recorded: Option<&EngineStamp>) -> Option<EngineStamp> {
    static IDENTITY: OnceLock<Option<EngineStamp>> = OnceLock::new();
    IDENTITY
        .get_or_init(|| {
            let path = std::env::current_exe().ok()?;
            let stamp = stamp_of(&fs::metadata(&path).ok()?)?;
            let path_text = path.to_string_lossy().into_owned();
            if let Some(recorded) = recorded {
                if recorded.path == path_text && recorded.stamp == stamp {
                    return Some(recorded.clone());
                }
            }
            let mut hasher = Sha256::new();
            hasher.update(format!("{MANIFEST_FORMAT}\n{}\n", env!("CARGO_PKG_VERSION")).as_bytes());
            hasher.update(fs::read(&path).ok()?);
            Some(EngineStamp {
                path: path_text,
                stamp,
                digest: hex(&hasher.finalize()),
            })
        })
        .clone()
}

/// One Procedure's navigation report, under the fingerprint of what its specification read.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub(super) struct SpecRecord {
    pub key: String,
    pub report: NavigationReport,
}

/// One Procedure's written emission, under the fingerprint of what its emission and its files read.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub(super) struct EmitRecord {
    pub key: String,
    pub stem: String,
    pub class_name: String,
    pub wrapper: bool,
    pub callees: BTreeSet<String>,
}

/// A Procedure's call signature as the record keeps it: the class a call names, its namespace, and each
/// parameter's mode (0 in, 1 out, 2 in-out) and type. It is what the call table of the build holds for
/// the Procedure, so a later run builds that table without reading the Procedure again.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub(super) struct RecordedSignature {
    pub class_name: String,
    pub namespace: String,
    pub params: Vec<(u8, DataType)>,
}

/// What a run settled for one Procedure of the build.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub(super) struct ProcedureRecord {
    pub id: String,
    /// Digest of the object.
    pub content: String,
    /// The call signature other Procedures see, or `None` when the class does not build.
    pub signature: Option<RecordedSignature>,
    pub spec: Option<SpecRecord>,
    pub emit: Option<EmitRecord>,
}

/// The files of the build's structured data types, under the fingerprint of those types.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub(super) struct SdtRecord {
    pub key: String,
    pub files: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct ModelRecord {
    key: String,
    main_project: String,
    generation: Generation,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Manifest {
    format: u32,
    engine: Option<EngineStamp>,
    /// Whether a later run may reuse the steps recorded here. A run that could not write a file, or in
    /// which two objects wrote one file, records only the files it wrote.
    reusable: bool,
    /// Written before the first file of a run and kept by a run that could not write a file: files that
    /// no record lists may be on disk, so the next run starts from an emptied output directory.
    #[serde(default)]
    dirty: bool,
    /// The whole generation, to report again when the model is unchanged.
    model: Option<ModelRecord>,
    sdts: Option<SdtRecord>,
    /// The fingerprint of a call table that was built without an error.
    call_key: Option<String>,
    /// In build order.
    procedures: Vec<ProcedureRecord>,
    /// Every file the run wrote or kept, relative to the output directory.
    files: BTreeMap<String, FileStamp>,
}

/// A recorded file name must stay inside the output directory: it is a name this module deletes.
fn is_contained(name: &str) -> bool {
    !name.is_empty()
        && Path::new(name)
            .components()
            .all(|component| matches!(component, Component::Normal(_)))
}

fn load_manifest(path: &Path) -> Result<Option<Manifest>, String> {
    let bytes = match fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => {
            return Err(format!(
                "the record of the previous run is unreadable: {error}"
            ));
        }
    };
    let manifest: Manifest = serde_json::from_slice(&bytes)
        .map_err(|error| format!("the record of the previous run is not valid: {error}"))?;
    if manifest.format != MANIFEST_FORMAT {
        return Err(format!(
            "the record of the previous run has format {}, this engine reads {MANIFEST_FORMAT}",
            manifest.format
        ));
    }
    if !manifest.files.keys().all(|name| is_contained(name)) {
        return Err("the record of the previous run names a file outside the output".to_string());
    }
    Ok(Some(manifest))
}

/// The regular files directly under `dir`, by name.
fn list_files(dir: &Path) -> HashMap<String, FileStamp> {
    let mut files = HashMap::new();
    let Ok(entries) = fs::read_dir(dir) else {
        return files;
    };
    for entry in entries.flatten() {
        let Ok(metadata) = entry.metadata() else {
            continue;
        };
        if !metadata.is_file() {
            continue;
        }
        if let (Some(name), Some(stamp)) = (entry.file_name().to_str(), stamp_of(&metadata)) {
            files.insert(name.to_string(), stamp);
        }
    }
    files
}

/// The stamp of `name` under `dir`: from the listing for a file of the directory itself, from the file
/// system for a file in a folder under it.
fn stamp_in(dir: &Path, listing: &HashMap<String, FileStamp>, name: &str) -> Option<FileStamp> {
    if Path::new(name).components().count() == 1 {
        listing.get(name).copied()
    } else {
        fs::metadata(dir.join(name))
            .ok()
            .filter(fs::Metadata::is_file)
            .and_then(|metadata| stamp_of(&metadata))
    }
}

/// Every option that reaches the generated files or the reported outcome. The destructuring names every
/// field, so a new option does not compile until it is placed in or out of the fingerprint.
fn options_digest(options: &RebuildOptions) -> String {
    let RebuildOptions {
        target_framework,
        // How the output is produced, not what it is.
        clean: _,
        concurrency: _,
        skip_compile: _,
        artifact_cache_dir: _,
        sql_dialect,
        fail_fast,
        assembly_name,
        genexus_runtime_version,
        web_environment,
        startup_object,
        client_config,
        runtime_sdts,
        model_files,
        generation_stamp,
    } = options;
    let mut hasher = Framed::new();
    hasher.text(&digest_of(&(
        target_framework,
        sql_dialect,
        fail_fast,
        assembly_name,
        genexus_runtime_version,
        web_environment,
        startup_object,
        client_config,
        runtime_sdts,
        generation_stamp,
    )));
    // The model files can be megabytes: their bytes are digested as they are.
    hasher.debug(&model_files.len());
    for (label, group) in model_files {
        hasher.text(label);
        match group {
            Ok(files) => {
                hasher.debug(&files.len());
                for (path, bytes) in files {
                    hasher.text(path);
                    hasher.bytes(bytes);
                }
            }
            Err(reason) => {
                hasher.text("not written");
                hasher.text(reason);
            }
        }
    }
    hasher.finish()
}

/// What a generation hands over to be recorded.
pub(super) struct Commit<'c> {
    pub emitted: &'c [PathBuf],
    pub main_project: &'c Path,
    pub generation: &'c Generation,
    pub procedures: Vec<ProcedureRecord>,
    pub sdts: Option<SdtRecord>,
    pub call_key: Option<String>,
    /// A file could not be written: the run is not a state to build on.
    pub io_failed: bool,
}

/// One rebuild's view of the previous run and of what it may reuse from it.
pub(super) struct Session<'a> {
    output_dir: &'a Path,
    engine: Option<EngineStamp>,
    /// The engine's digest, or a value no record holds when the engine cannot be identified.
    engine_key: String,
    model_key: String,
    /// The previous run's record, when this engine wrote it and the run is not a clean rebuild.
    previous: Option<Manifest>,
    by_id: HashMap<String, usize>,
    /// Files a previous run wrote, whether or not its record is reusable.
    tracked: BTreeMap<String, FileStamp>,
    /// Whether the previous record says a run was cut off or could not write a file.
    previous_dirty: bool,
    /// The output directory's files when the run started.
    disk: HashMap<String, FileStamp>,
    contents: HashMap<&'a str, String>,
    cache: Option<ArtifactCache>,
    /// Why nothing of a previous run is reused, when nothing is.
    full_reason: Option<String>,
    unchanged: bool,
    stale_files_removed: usize,
    pub procedures_kept: AtomicUsize,
    pub procedures_emitted: AtomicUsize,
    pub specifications_kept: AtomicUsize,
    pub specifications_computed: AtomicUsize,
    pub sdts_kept: AtomicUsize,
}

impl<'a> Session<'a> {
    pub fn open(catalog: &'a Catalog, output_dir: &'a Path, options: &RebuildOptions) -> Self {
        let loaded = load_manifest(&output_dir.join(MANIFEST_DIR).join(MANIFEST_FILE));
        let recorded_engine = loaded
            .as_ref()
            .ok()
            .and_then(|manifest| manifest.as_ref())
            .and_then(|manifest| manifest.engine.as_ref());
        let engine = engine_identity(recorded_engine);
        let engine_key = engine.as_ref().map_or_else(
            || {
                format!(
                    "unidentified engine {} {:?}",
                    std::process::id(),
                    std::time::SystemTime::now()
                )
            },
            |engine| engine.digest.clone(),
        );

        let mut tracked = BTreeMap::new();
        let previous_dirty = matches!(&loaded, Ok(Some(manifest)) if manifest.dirty);
        let (previous, full_reason) = match loaded {
            _ if options.clean => (None, Some("a clean rebuild was requested".to_string())),
            Err(reason) => (None, Some(reason)),
            Ok(None) => (
                None,
                Some("the output directory has no record of a previous run".to_string()),
            ),
            Ok(Some(manifest)) => {
                tracked.clone_from(&manifest.files);
                if engine.is_none() {
                    (
                        None,
                        Some("the engine binary cannot be identified".to_string()),
                    )
                } else if manifest.engine.as_ref().map(|recorded| &recorded.digest)
                    != engine.as_ref().map(|engine| &engine.digest)
                {
                    (
                        None,
                        Some("the previous run was written by another engine binary".to_string()),
                    )
                } else if !manifest.reusable {
                    (
                        None,
                        Some("the previous run did not leave a state to build on".to_string()),
                    )
                } else {
                    (Some(manifest), None)
                }
            }
        };

        let mut contents = HashMap::new();
        let mut model = Framed::new();
        model.text(&engine_key);
        model.text(&options_digest(options));
        for object in catalog.objects() {
            let content = object_digest(object);
            model.text(&content);
            contents.insert(object.id.as_str(), content);
        }

        let by_id = previous
            .iter()
            .flat_map(|manifest| manifest.procedures.iter().enumerate())
            .map(|(index, record)| (record.id.clone(), index))
            .collect();
        Self {
            output_dir,
            // A cache shared between machines needs an identity they can agree on.
            cache: options
                .artifact_cache_dir
                .as_deref()
                .filter(|_| engine.is_some())
                .map(ArtifactCache::new),
            engine,
            engine_key,
            model_key: model.finish(),
            previous,
            by_id,
            tracked,
            previous_dirty,
            disk: list_files(output_dir),
            contents,
            full_reason,
            unchanged: false,
            stale_files_removed: 0,
            procedures_kept: AtomicUsize::new(0),
            procedures_emitted: AtomicUsize::new(0),
            specifications_kept: AtomicUsize::new(0),
            specifications_computed: AtomicUsize::new(0),
            sdts_kept: AtomicUsize::new(0),
        }
    }

    /// The engine's identity, the first input of every fingerprint.
    pub fn engine_key(&self) -> &str {
        &self.engine_key
    }

    /// The digest of a catalog object.
    pub fn content(&self, object: &KbObject) -> String {
        self.contents
            .get(object.id.as_str())
            .cloned()
            .unwrap_or_else(|| object_digest(object))
    }

    pub fn cache(&self) -> Option<&ArtifactCache> {
        self.cache.as_ref()
    }

    pub fn previous_procedure(&self, id: &str) -> Option<&ProcedureRecord> {
        let manifest = self.previous.as_ref()?;
        manifest.procedures.get(*self.by_id.get(id)?)
    }

    pub fn previous_sdts(&self) -> Option<&SdtRecord> {
        self.previous.as_ref()?.sdts.as_ref()
    }

    pub fn previous_call_key(&self) -> Option<&str> {
        self.previous.as_ref()?.call_key.as_deref()
    }

    /// Whether `name` is on disk as the previous run left it.
    pub fn is_intact(&self, name: &str) -> bool {
        let recorded = self
            .previous
            .as_ref()
            .and_then(|manifest| manifest.files.get(name));
        recorded.is_some() && recorded.copied() == stamp_in(self.output_dir, &self.disk, name)
    }

    /// Whether a step of this run was skipped because the previous run's files stand for it.
    pub fn kept_any(&self) -> bool {
        self.procedures_kept.load(Ordering::Relaxed) + self.sdts_kept.load(Ordering::Relaxed) > 0
    }

    /// Stops reusing the previous run's record for the rest of this run. Its file list is still used to
    /// remove what this run no longer produces.
    pub fn forget_previous(&mut self, reason: &str) {
        self.previous = None;
        self.by_id.clear();
        self.full_reason = Some(reason.to_string());
        for counter in [
            &self.procedures_kept,
            &self.procedures_emitted,
            &self.sdts_kept,
        ] {
            counter.store(0, Ordering::Relaxed);
        }
    }

    /// The previous run's generation, when the model is the one it generated and every file it wrote is
    /// still as it left it. Nothing has to be classified, specified or emitted then.
    pub fn unchanged_generation(&mut self) -> Option<Generation> {
        let manifest = self.previous.as_ref()?;
        let model = manifest.model.as_ref()?;
        if model.key != self.model_key
            || !manifest
                .files
                .iter()
                .all(|(name, stamp)| stamp_in(self.output_dir, &self.disk, name) == Some(*stamp))
        {
            return None;
        }
        let mut generation = model.generation.clone();
        generation.emitted_files = manifest
            .files
            .keys()
            .map(|name| self.output_dir.join(name))
            .collect();
        generation.emitted_files.sort();
        generation.main_project = Some(self.output_dir.join(&model.main_project));
        generation.navigation_reports = manifest
            .procedures
            .iter()
            .filter_map(|record| record.spec.as_ref())
            .map(|spec| spec.report.clone())
            .collect();
        self.unchanged = true;
        Some(generation)
    }

    /// Whether the output directory has to be emptied first: the previous run was cut off or could not
    /// write a file, so it may have left files that no record lists.
    pub fn needs_clean(&self) -> bool {
        self.previous_dirty
    }

    /// Called before the first file of a generation is written, and after any clean. From here until
    /// `commit` the files on disk are not the ones the record describes, so a run that is cut off must
    /// not leave that record: it is replaced by a dirty one that only lists the files, and the next run
    /// empties the directory (this run may write files no record names). This run keeps reusing the
    /// record it read.
    pub fn begin_writing(&self) {
        let listing = Manifest {
            format: MANIFEST_FORMAT,
            engine: self.engine.clone(),
            reusable: false,
            dirty: true,
            model: None,
            sdts: None,
            call_key: None,
            procedures: Vec::new(),
            files: self.tracked.clone(),
        };
        if self.write_manifest(&listing).is_err() {
            self.discard_record();
        }
    }

    /// Records the generation and removes the files of the previous run that it did not produce.
    pub fn commit(&mut self, commit: Commit<'_>) {
        let names: Option<Vec<String>> = commit
            .emitted
            .iter()
            .map(|path| {
                path.strip_prefix(self.output_dir)
                    .ok()
                    .and_then(Path::to_str)
                    .filter(|name| is_contained(name))
                    .map(str::to_string)
            })
            .collect();
        let main_project = commit
            .main_project
            .strip_prefix(self.output_dir)
            .ok()
            .and_then(Path::to_str);
        let (Some(names), Some(main_project)) = (names, main_project) else {
            // A written path that cannot be named relative to the output is not a state to build on.
            self.discard_record();
            return;
        };
        let produced: BTreeSet<&str> = names.iter().map(String::as_str).collect();
        let unique = produced.len() == names.len() && !has_case_twins(&produced);

        if !commit.io_failed {
            self.remove_stale(&produced);
        }
        let listing = list_files(self.output_dir);
        let mut files = BTreeMap::new();
        let mut complete = true;
        for name in &produced {
            match stamp_in(self.output_dir, &listing, name) {
                Some(stamp) => {
                    files.insert((*name).to_string(), stamp);
                }
                None => complete = false,
            }
        }
        if commit.io_failed {
            // Whatever the previous run wrote may still be there; keep it listed so a later run removes it.
            for (name, stamp) in &self.tracked {
                files.entry(name.clone()).or_insert(*stamp);
            }
        }
        let reusable = !commit.io_failed && unique && complete && self.engine.is_some();
        let manifest = Manifest {
            format: MANIFEST_FORMAT,
            engine: self.engine.clone(),
            reusable,
            dirty: commit.io_failed,
            model: reusable.then(|| ModelRecord {
                key: self.model_key.clone(),
                main_project: main_project.to_string(),
                generation: commit.generation.clone(),
            }),
            sdts: commit.sdts.filter(|_| reusable),
            call_key: commit.call_key.filter(|_| reusable),
            procedures: if reusable {
                commit.procedures
            } else {
                Vec::new()
            },
            files,
        };
        if let Err(error) = self.write_manifest(&manifest) {
            eprintln!("incremental: the record of this run was not written: {error}");
            self.discard_record();
        }
    }

    /// Removes each file a previous run wrote that this run did not produce, and the folders that leaves
    /// empty. A name that differs from a produced one only by letter case is the same file on a
    /// case-insensitive file system: it is renamed to the produced name, never removed.
    fn remove_stale(&mut self, produced: &BTreeSet<&str>) {
        let listing = list_files(self.output_dir);
        let by_folded: HashMap<String, &str> = produced
            .iter()
            .map(|name| (name.to_lowercase(), *name))
            .collect();
        let mut removed = 0;
        for old in self.tracked.keys() {
            if produced.contains(old.as_str()) {
                continue;
            }
            let old_path = self.output_dir.join(old);
            if let Some(twin) = by_folded.get(&old.to_lowercase()) {
                let top_level = Path::new(old).components().count() == 1;
                if top_level && listing.contains_key(old) && !listing.contains_key(*twin) {
                    // One file, still under its earlier name: give it the name this run wrote.
                    let _ = fs::rename(&old_path, self.output_dir.join(twin));
                } else if top_level && listing.contains_key(old) && listing.contains_key(*twin) {
                    // Two files on a case-sensitive file system: the earlier one is stale.
                    if fs::remove_file(&old_path).is_ok() {
                        removed += 1;
                    }
                }
                continue;
            }
            if fs::remove_file(&old_path).is_err() {
                continue;
            }
            removed += 1;
            let mut folder = old_path.parent();
            while let Some(dir) = folder.filter(|dir| *dir != self.output_dir) {
                if fs::remove_dir(dir).is_err() {
                    break;
                }
                folder = dir.parent();
            }
        }
        self.stale_files_removed += removed;
    }

    fn write_manifest(&self, manifest: &Manifest) -> Result<(), String> {
        let dir = self.output_dir.join(MANIFEST_DIR);
        fs::create_dir_all(&dir).map_err(|error| error.to_string())?;
        let bytes = serde_json::to_vec(manifest).map_err(|error| error.to_string())?;
        let path = dir.join(MANIFEST_FILE);
        let temp = dir.join(format!("{MANIFEST_FILE}.tmp.{}", std::process::id()));
        let staged = fs::File::create(&temp).and_then(|mut file| {
            file.write_all(&bytes)?;
            file.sync_all()
        });
        if let Err(error) = staged.and_then(|()| fs::rename(&temp, &path)) {
            let _ = fs::remove_file(&temp);
            return Err(error.to_string());
        }
        Ok(())
    }

    /// Leaves no record: the next run reuses nothing.
    fn discard_record(&self) {
        let _ = fs::remove_file(self.output_dir.join(MANIFEST_DIR).join(MANIFEST_FILE));
    }

    /// What this run reused, for the report.
    pub fn summary(&self) -> ReuseSummary {
        let count = |counter: &AtomicUsize| counter.load(Ordering::Relaxed);
        let mode = if self.unchanged {
            "unchanged"
        } else if self.previous.is_some() {
            "incremental"
        } else {
            "full"
        };
        if let Some(failure) = self.cache.as_ref().and_then(ArtifactCache::store_failure) {
            eprintln!("artifact cache: an entry was not stored: {failure}");
        }
        ReuseSummary {
            mode: mode.to_string(),
            reason: self.full_reason.clone().filter(|_| mode == "full"),
            procedures_kept: count(&self.procedures_kept),
            procedures_emitted: count(&self.procedures_emitted),
            specifications_kept: count(&self.specifications_kept),
            specifications_computed: count(&self.specifications_computed),
            sdts_kept: count(&self.sdts_kept) > 0,
            stale_files_removed: self.stale_files_removed,
            cache_entries_restored: self.cache.as_ref().map_or(0, ArtifactCache::restored),
            cache_entries_stored: self.cache.as_ref().map_or(0, ArtifactCache::stored),
        }
    }
}

/// Whether two of `names` differ only by letter case: one file on a case-insensitive file system.
fn has_case_twins(names: &BTreeSet<&str>) -> bool {
    let folded: BTreeSet<String> = names.iter().map(|name| name.to_lowercase()).collect();
    folded.len() != names.len()
}

/// Whether two of the written paths are one file: the same name, or names that differ only by letter
/// case. The later write then decides the file, so a step that kept its earlier write cannot be trusted.
pub(super) fn writes_one_file_twice(paths: &[PathBuf]) -> bool {
    let mut seen = BTreeSet::new();
    paths
        .iter()
        .any(|path| !seen.insert(path.to_string_lossy().to_lowercase()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_key_frames_its_parts() {
        assert_ne!(key_of("s", ["ab", "c"]), key_of("s", ["a", "bc"]));
        assert_ne!(key_of("s", ["ab"]), key_of("sa", ["b"]));
        assert_eq!(key_of("s", ["ab", "c"]), key_of("s", ["ab", "c"]));
    }

    #[test]
    fn only_names_inside_the_output_are_accepted() {
        for name in ["a.cs", "web/a.js", "a b.cs"] {
            assert!(is_contained(name), "{name}");
        }
        for name in ["", "../a.cs", "web/../../a.cs", "/a.cs", "./a.cs"] {
            assert!(!is_contained(name), "{name}");
        }
    }

    #[test]
    fn a_path_written_twice_is_found_whatever_its_case() {
        let paths = |names: &[&str]| names.iter().map(PathBuf::from).collect::<Vec<_>>();
        assert!(writes_one_file_twice(&paths(&["out/a.cs", "out/a.cs"])));
        assert!(writes_one_file_twice(&paths(&["out/A.cs", "out/a.cs"])));
        assert!(!writes_one_file_twice(&paths(&["out/a.cs", "out/b.cs"])));
    }
}
