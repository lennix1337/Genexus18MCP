use crate::graph::CsrGraph;
use crate::model::{EdgeKind, EntityKind, EntityNode, RawEdge};
use crate::parser::lexer::ParsedSymbolRef;
use crate::parser::{ParseResult, parse_genexus_source};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::error::Error;
use std::fs::{self, File};
use std::io::{BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::time::UNIX_EPOCH;
use walkdir::WalkDir;

const CACHE_FILE_NAME: &str = ".gxwire_cache.bin";

/// Layout marker, read before the payload so a cache written by a different
/// gxwire build is discarded instead of being misread. The Knowledge Base is
/// then reparsed in full, which is slow but never wrong.
const CACHE_FORMAT_VERSION: u32 = 4;

/// Parse output for one source file, together with the stat signature it was
/// produced from. The entry is reused verbatim while both still match, which
/// is what turns a one-object edit into a one-object reparse.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FileParse {
    pub path: String,
    pub stem: String,
    pub kind: EntityKind,
    pub mtime_ticks: u64,
    pub size: u64,
    pub parse: ParseResult,
    /// Symbols this file's attribute formulas reference.
    ///
    /// Kept beside `parse` rather than recomputed at graph-assembly time: the
    /// file is already in memory when it is lexed, and re-reading every file
    /// there would undo the incremental reindex.
    #[serde(default)]
    pub formula_refs: Vec<ParsedSymbolRef>,
}

/// Parse entries keyed by absolute source path.
pub type ParseMap = HashMap<String, FileParse>;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CachedBundle {
    pub version: u32,
    pub graph: CsrGraph,
    pub parses: ParseMap,
}

pub fn get_cache_path(root: &Path) -> PathBuf {
    root.join(CACHE_FILE_NAME)
}

/// Where the index actually lives.
///
/// Defaults to a dotfile inside `root`, which is what gxwire has always done and
/// what its own tests assume. `--cache-path` moves it out, because the contract
/// published for `genexus_wire` declares it read-only (`readOnlyHint: true`,
/// "Mutating actions: none") while a real invocation wrote
/// `.gxwire_cache.bin` into the directory it was asked to analyse. With a native
/// Knowledge Base that directory is the KB itself, so the write lands a dotfile
/// inside a GeneXus KB the tool was supposed to be reading (#499).
pub fn cache_path_for(root: &Path, override_path: Option<&Path>) -> PathBuf {
    match override_path {
        Some(path) => path.to_path_buf(),
        None => get_cache_path(root),
    }
}

/// (mtime in milliseconds, size in bytes). Size is part of the signature so a
/// same-millisecond rewrite that changes length cannot slip through as fresh.
fn stat_signature(path: &Path) -> (u64, u64) {
    match fs::metadata(path) {
        Ok(meta) => {
            // Full resolution, not milliseconds.
            //
            // `as_millis()` threw away resolution the filesystem already had:
            // NTFS records 100-nanosecond ticks, and two writes inside the same
            // millisecond produced an identical signature whenever the length
            // matched. The cache then served the pre-edit graph, which is the one
            // failure mode this signature exists to prevent. Demonstrated: a
            // file whose content had changed from `Call(Alpha)` to `Call(Bravo)`
            // - same length, same millisecond - still answered
            // `--callers Alpha -> count: 1` while `--callers Bravo` reported
            // `SymbolNotFound`.
            //
            // Length alone cannot close this: `Call(A)` -> `Call(B)`, or any
            // one-character edit in a large file, keeps the size identical.
            // Timestamp alone could not either, which is why size is still
            // checked - a coarse-granularity filesystem can stamp two different
            // versions identically.
            //
            // 100ns ticks since the epoch fit in a u64 until the year 2554.
            let mtime = meta
                .modified()
                .ok()
                .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
                .map(|d| d.as_nanos() as u64)
                .unwrap_or(0);
            (mtime, meta.len())
        }
        // An unreadable file reparses on every run: slow, never stale.
        Err(_) => (0, 0),
    }
}

/// One supported source file found on disk, before it is read.
struct SourceFile {
    path: PathBuf,
    stem: String,
}

/// Directories whose contents are tooling or build output, never Knowledge Base
/// sources. They are pruned before the walk descends into them (case-insensitive,
/// exact directory name, never the analysed root itself).
///
/// Without this a text tree that lives next to a repository's working files is
/// walked through `.git` and `node_modules`, and the `.txt`/`.xml` files in there
/// become graph nodes that no GeneXus object owns (#593: the walk of a large
/// folder was also the cost that never finished). Only names that cannot be a
/// GeneXus export folder are listed: an export nests objects under folders named
/// after their type or module, never `bin`, `obj` or a VCS metadata directory.
///
/// `build`, `UserControls`, `Locks` and `OutputLogs` are the generation, control
/// cache, lock and log folders of a native KB environment (#593). Pruning only
/// skips them in the walk; nothing on disk is touched.
const PRUNED_DIR_NAMES: &[&str] = &[
    ".git",
    ".svn",
    ".hg",
    ".vs",
    "node_modules",
    "bin",
    "obj",
    "build",
    "usercontrols",
    "locks",
    "outputlogs",
];

fn is_pruned_dir_name(name: &std::ffi::OsStr) -> bool {
    let name = name.to_string_lossy();
    PRUNED_DIR_NAMES
        .iter()
        .any(|p| p.eq_ignore_ascii_case(&name))
}

fn collect_source_files(dir: &Path) -> Vec<SourceFile> {
    let mut out = Vec::new();
    let walker = WalkDir::new(dir).into_iter().filter_entry(|entry| {
        // The root is analysed whatever it is called; only directories below it
        // are candidates for pruning, and files are never filtered here.
        entry.depth() == 0 || !entry.file_type().is_dir() || !is_pruned_dir_name(entry.file_name())
    });
    for entry in walker.filter_map(|e| e.ok()) {
        // `file_type()` comes from the directory enumeration itself, whereas
        // `path.is_file()` costs a stat per entry. That stat is the single
        // biggest per-file overhead when indexing a large Knowledge Base.
        if !entry.file_type().is_file() {
            continue;
        }
        let path = entry.path();
        let ext = path.extension().and_then(|s| s.to_str()).unwrap_or("");
        if !is_supported_ext(ext) {
            continue;
        }

        // `.xml` has to be indexed - a GeneXus XML export writes its objects
        // that way - but the same extension is what GeneXus and its tooling use
        // for their own metadata. Observed at the root of three live Knowledge
        // Bases, and indistinguishable from an exported object by extension or
        // by file name alone.
        //
        // Restricted to the analysed root, which is the only place this metadata
        // lives: an export that nests its objects is unaffected, and a deeper
        // object that happens to share one of these names is still indexed.
        let stem = path.file_stem().and_then(|s| s.to_str()).unwrap_or("");
        if path.parent() == Some(dir) && is_knowledge_base_metadata(stem) {
            continue;
        }
        out.push(SourceFile {
            path: path.to_path_buf(),
            stem: path
                .file_stem()
                .and_then(|s| s.to_str())
                .unwrap_or("")
                .to_string(),
        });
    }
    out
}

/// Reads and lexes one file. `signature` is passed in so the caller, which
/// already stat'ed the file to decide whether to reuse a cached entry, does not
/// stat it a second time.
fn parse_source(sf: &SourceFile, signature: (u64, u64)) -> FileParse {
    let (mtime_ticks, size) = signature;
    let ext = sf.path.extension().and_then(|s| s.to_str()).unwrap_or("");
    let kind = detect_entity_kind(&sf.path, &sf.stem, ext);

    // Read once. The incremental reindex exists so an unchanged file is never
    // re-read, and computing the formula references in a second read here would
    // reintroduce exactly that cost on every indexed file.
    //
    // Decoded lossily rather than with `read_to_string`. That call rejects any
    // byte sequence that is not valid UTF-8, and a Windows-1252 file - an
    // accented description in a Brazilian Knowledge Base - took the whole file
    // down to an empty parse. The object still became a node, so nothing looked
    // wrong, and every reference in it silently vanished from the graph. Since
    // call syntax is ASCII, decoding with replacement keeps the edges and only
    // mangles the characters that were already unreadable.
    let content = fs::read(&sf.path)
        .ok()
        .map(|bytes| String::from_utf8_lossy(&bytes).into_owned());

    let parse = match &content {
        Some(content) => parse_genexus_source(content),
        // Mirrors the previous builder, which left the node at its
        // constructor defaults when the file could not be read.
        None => ParseResult {
            complexity: 1,
            ..Default::default()
        },
    };

    // An attribute's formula is a dependency of whoever reads the attribute, so
    // the calls it makes become DefinesFormula edges from the attribute itself.
    // That is what lets `impact` on a Procedure report the attributes whose
    // formula calls it, instead of returning `formulas: []` (#501).
    //
    // Gated on the `[Formula:` marker itself, not on `kind == Attribute`. A
    // `.txt` attribute declaration has no distinctive extension or name prefix,
    // so the kind resolver reports `Unknown` for it and a kind-based gate
    // silently skipped every formula in a text tree export.
    let formula_refs = match &content {
        Some(content) => crate::parser::formula_references(content),
        None => Vec::new(),
    };

    FileParse {
        path: sf.path.to_string_lossy().to_string(),
        stem: sf.stem.clone(),
        kind,
        mtime_ticks,
        size,
        parse,
        formula_refs,
    }
}

/// Assembles the graph from already-parsed files.
///
/// Two passes, so that every file is registered as a node before any reference
/// is resolved: a reference to an object defined later in the walk must resolve
/// to that object's real node rather than to a phantom. Unresolved references
/// get phantom nodes (`path: None`) that live only as long as the references
/// that create them. Node ids follow file order, so the same file set produces
/// the same graph regardless of which entries were reused from the cache.
fn assemble_graph(files: &[FileParse]) -> CsrGraph {
    let mut nodes = Vec::with_capacity(files.len());
    let mut raw_edges = Vec::new();
    let mut name_to_id: HashMap<String, u32> = HashMap::with_capacity(files.len());

    // Pass 1: one node per source file.
    for (idx, fp) in files.iter().enumerate() {
        let id = idx as u32;
        name_to_id.insert(fp.stem.to_ascii_lowercase(), id);
        nodes.push(EntityNode {
            id,
            name: fp.stem.clone(),
            kind: fp.kind,
            module: None,
            description: None,
            parm_rule: fp.parse.parm_rule.clone(),
            path: Some(fp.path.clone()),
            lines: fp.parse.lines_count,
            complexity: fp.parse.complexity,
        });
    }

    // Pass 2: resolve references, creating phantoms for targets with no file.
    // Bounded by the file-backed count so phantoms are not themselves scanned.
    for (idx, fp) in files.iter().enumerate() {
        // References and formula references travel together, each carrying its
        // own edge kind: a symbol reached through an attribute formula is a
        // `DefinesFormula` dependency, not a plain read or write.
        let formula_targets: std::collections::HashSet<String> = fp
            .formula_refs
            .iter()
            .map(|r| r.target_name.to_ascii_lowercase())
            .collect();

        let mut edges: Vec<(&ParsedSymbolRef, EdgeKind)> = fp
            .parse
            .references
            .iter()
            .filter(|r| !formula_targets.contains(&r.target_name.to_ascii_lowercase()))
            .map(|r| (r, r.kind.clone()))
            .collect();
        edges.extend(
            fp.formula_refs
                .iter()
                .map(|r| (r, EdgeKind::DefinesFormula)),
        );

        for (ref_sym, edge_kind) in edges {
            let target_key = ref_sym.target_name.to_ascii_lowercase();
            let target_id = match name_to_id.get(&target_key) {
                Some(&id) => id,
                None => {
                    let new_id = nodes.len() as u32;
                    name_to_id.insert(target_key, new_id);
                    let kind = match edge_kind {
                        EdgeKind::WritesAttr(_) | EdgeKind::ReadsAttr => EntityKind::Attribute,
                        _ => EntityKind::Unknown,
                    };
                    nodes.push(EntityNode {
                        id: new_id,
                        name: ref_sym.target_name.clone(),
                        kind,
                        module: None,
                        description: None,
                        parm_rule: None,
                        path: None,
                        lines: 0,
                        complexity: 1,
                    });
                    new_id
                }
            };

            raw_edges.push(RawEdge {
                from: idx as u32,
                to: target_id,
                kind: edge_kind,
                line: ref_sym.line,
            });
        }
    }

    CsrGraph::build(nodes, &raw_edges)
}

/// One incremental pass: the graph and the parse map it was assembled from.
type Reindex = (CsrGraph, ParseMap);

/// Walks `root` once and reparses only the files whose stat signature moved.
///
/// `cached` is consumed so a reused entry is moved rather than cloned: cloning
/// it would reallocate every reference string in the Knowledge Base on every
/// call, which defeats the point of caching them.
///
/// Returns `None` when nothing moved, so an unchanged Knowledge Base answers
/// from the cached graph without touching the parse data at all. A single stat
/// per file replaces the previous two-walk freshness probe.
fn reindex(root: &Path, mut cached: ParseMap) -> Result<Option<Reindex>, Box<dyn Error>> {
    let sources = collect_source_files(root);
    let mut ordered: Vec<FileParse> = Vec::with_capacity(sources.len());
    let mut reparsed = 0usize;

    for sf in &sources {
        // The path is the cache key, and the node name is derived from it, so
        // a rename lands on a new key and is reparsed as a new object. The stem
        // needs no separate check: it cannot differ for the same path.
        let key = sf.path.to_string_lossy().to_string();
        let signature = stat_signature(&sf.path);

        let reusable = match cached.get(&key) {
            Some(previous) => previous.mtime_ticks == signature.0 && previous.size == signature.1,
            None => false,
        };

        if reusable {
            ordered.push(cached.remove(&key).expect("entry was just observed"));
        } else {
            ordered.push(parse_source(sf, signature));
            reparsed += 1;
        }
    }

    // `cached` has been drained entry by entry, so whatever is left is an object
    // the walk no longer found: a deleted file. Nothing moved and nothing was
    // deleted means the cached graph still stands.
    if reparsed == 0 && cached.is_empty() {
        return Ok(None);
    }

    let graph = assemble_graph(&ordered);
    let parses = ordered
        .into_iter()
        .map(|fp| (fp.path.clone(), fp))
        .collect::<ParseMap>();
    Ok(Some((graph, parses)))
}

/// Borrowed serialization view of the index, laid out exactly like
/// `CachedBundle` so both encode to the same bytes. Persisting through a
/// borrowed view avoids deep-copying the graph and every reference string in
/// it, which for a real Knowledge Base costs more than the write itself.
///
/// The two must be kept field-for-field identical: if they drift, every write
/// becomes unreadable and `load_cache` silently falls back to reparsing the
/// whole Knowledge Base. `warm_load_does_not_rewrite_the_index` fails loudly in
/// that case.
#[derive(Serialize)]
struct BundleRef<'a> {
    version: u32,
    graph: &'a CsrGraph,
    parses: &'a ParseMap,
}

/// Persists the graph together with the parse entries it was assembled from.
/// Persisting a graph on its own is deliberately not offered: the next load
/// would have no reuse state and would reparse the whole Knowledge Base.
pub fn save_index(graph: &CsrGraph, parses: &ParseMap, root: &Path) -> Result<(), Box<dyn Error>> {
    save_index_to(graph, parses, &get_cache_path(root))
}

/// Persists the index to an explicit file.
///
/// `save_index` derives the file from the analysed root, which is what the
/// in-tree tests use. A caller that must not write into the analysed directory
/// resolves the path with `cache_path_for` and calls this (#499).
pub fn save_index_to(
    graph: &CsrGraph,
    parses: &ParseMap,
    cache_path: &Path,
) -> Result<(), Box<dyn Error>> {
    let view = BundleRef {
        version: CACHE_FORMAT_VERSION,
        graph,
        parses,
    };
    let encoded = bincode::serialize(&view)?;
    if let Some(parent) = cache_path.parent() {
        // The cache may be relocated outside the analysed tree, so its directory
        // is not guaranteed to exist the way the analysed root's is.
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent)?;
        }
    }
    let mut file = File::create(cache_path)?;
    file.write_all(&encoded)?;
    Ok(())
}

/// Reads the index, rejecting any file whose layout marker is not ours.
///
/// The marker is validated before the payload is decoded: a cache written by a
/// different gxwire build has a different field layout, and decoding it as if it
/// were ours would interpret arbitrary bytes as collection lengths. `version` is
/// the bundle's first field, so both reads start at the same offset and the
/// header validation lines up exactly with the payload.
fn read_bundle(cache_path: &Path) -> Option<CachedBundle> {
    let bytes = fs::read(cache_path).ok()?;
    let header_bytes = bytes.get(..std::mem::size_of::<u32>())?;
    let version = bincode::deserialize::<VersionHeader>(header_bytes).ok()?;

    if version.version != CACHE_FORMAT_VERSION {
        return None;
    }

    // A truncated write fails here and falls back to a full reparse.
    bincode::deserialize::<CachedBundle>(&bytes).ok()
}

#[derive(Serialize, Deserialize)]
struct VersionHeader {
    version: u32,
}

/// Ingests directory containing GeneXus exported sources / text tree files,
/// returning the graph and the parse entries it was built from.
pub fn build_index(dir: &Path) -> Result<(CsrGraph, ParseMap), Box<dyn Error>> {
    // An empty cache can never serve a file, so this always parses what it finds.
    Ok(match reindex(dir, ParseMap::new())? {
        Some(result) => result,
        // Nothing on disk and nothing cached. A Knowledge Base with no exported
        // sources yet is a valid one, and its graph is empty - not an error.
        None => (assemble_graph(&[]), ParseMap::new()),
    })
}

pub fn build_graph_from_directory(dir: &Path) -> Result<CsrGraph, Box<dyn Error>> {
    Ok(build_index(dir)?.0)
}

/// Returns the graph for `root`, reindexing incrementally when the index is
/// stale and reparsing from scratch when it is missing or unreadable.
pub fn load_cache(root: &Path) -> Result<CsrGraph, Box<dyn Error>> {
    load_cache_from(&get_cache_path(root), root)
}

/// Returns the graph for `root`, reading and writing the index at `cache_path`.
///
/// Split from `load_cache` so the index location can be decided by the caller.
/// Keeping the analysed root and the index file separate is the whole point: the
/// root decides what is parsed, the file decides where bytes land (#499).
pub fn load_cache_from(cache_path: &Path, root: &Path) -> Result<CsrGraph, Box<dyn Error>> {
    if let Some(bundle) = read_bundle(cache_path) {
        let CachedBundle { graph, parses, .. } = bundle;
        match reindex(root, parses)? {
            // Nothing on disk moved: the cached graph is already the truth.
            None => Ok(graph),
            Some((graph, parses)) => {
                // A failed write costs speed on the next run, not correctness.
                let _ = save_index_to(&graph, &parses, cache_path);
                Ok(graph)
            }
        }
    } else {
        // No usable index: parse everything. Saving is best effort so a
        // read-only Knowledge Base still answers queries.
        let (graph, parses) = build_index(root)?;
        let _ = save_index_to(&graph, &parses, cache_path);
        Ok(graph)
    }
}

/// Stems GeneXus and its tooling write into a Knowledge Base root.
///
/// Confirmed present in `KBTeste`, `KBTeste17` and `KBTesteProbe2`. Each was
/// previously indexed as an object and reported as a hub - `CategoryViewConfig`
/// came back as `kind: "Proc"`, `iar_objs` as `kind: "Tbl"` - which is how a
/// Knowledge Base analysis can look like an answer while describing the IDE's
/// own bookkeeping.
///
/// A blocklist rather than a content classifier on purpose: telling an exported
/// object apart from metadata by content needs the real XML export schema, and
/// guessing at one would exclude genuine objects. Adding a name here is
/// verifiable; inventing a rule is not.
fn is_knowledge_base_metadata(stem: &str) -> bool {
    const METADATA_STEMS: &[&str] = &["CategoryViewConfig", "iar_objs", "nav_objs"];
    METADATA_STEMS.iter().any(|m| m.eq_ignore_ascii_case(stem))
}

#[inline]
fn is_supported_ext(ext: &str) -> bool {
    matches!(
        ext.to_ascii_lowercase().as_str(),
        "txt" | "gxd" | "xml" | "gx" | "prc" | "trn" | "wp" | "sdp"
    )
}

/// Best-effort object kind from a file name, used only when the file's own
/// content cannot say.
///
/// The old chain tested a single leading letter, so every Transaction whose
/// name began with `P` - `PessoaDadosFiscaisAdicionais` among them - was
/// reported as `kind: "Proc"`, and `callers` answered on the wrong object type
/// (#500). Two-letter prefixes are unambiguous in GeneXus exports and are
/// therefore checked first; a bare single-letter guess is no longer made,
/// because `P` alone cannot distinguish a Procedure from a Transaction named
/// after a person, and guessing wrong is worse than reporting `Unknown`.
///
/// Content detection remains authoritative for XML exports, where the element
/// name says the real kind.
fn kind_from_type_guid(guid: &str) -> Option<EntityKind> {
    match guid.to_ascii_lowercase().as_str() {
        "84a12160-f59b-4ad7-a683-ea4481ac23e9" | "2a9e9aba-d2de-4801-ae7f-5e3819222daf" => {
            Some(EntityKind::Procedure)
        }
        "1db606f2-af09-4cf9-a3b5-b481519d28f6" => Some(EntityKind::Transaction),
        "c9584656-94b6-4ccd-890f-332d11fc2c25" | "198e8ea4-1d49-4c9c-8a9a-417024baa9d1" => {
            Some(EntityKind::WebPanel)
        }
        "d82625fd-5892-40b0-99c9-5c8559c197fc" => Some(EntityKind::Panel),
        "78cecefe-be7d-4980-86ce-8d6e91fba04b" | "15cf49b5-fc38-4899-91b5-395d02d79889" => {
            Some(EntityKind::WorkWithPlus)
        }
        "857ca50e-7905-0000-0007-c5d9ff2975ec" => Some(EntityKind::Table),
        "adbb33c9-0906-4971-833c-998de27e0676" => Some(EntityKind::Attribute),
        "00972a17-9975-449e-aab1-d26165d51393" => Some(EntityKind::Domain),
        "ffd44be7-3bb4-4d01-9e7e-d1c1a3c095af" => Some(EntityKind::DataSelector),
        "447527b5-9210-4523-898b-5dccb17be60a" => Some(EntityKind::Sdt),
        _ => None,
    }
}

fn extract_type_guid_from_xml(head: &str) -> Option<&str> {
    let pos = head.find("type=")?;
    let rest = &head[pos + 5..];
    let quote = rest.chars().next()?;
    if quote != '"' && quote != '\'' {
        return None;
    }
    let rest = &rest[1..];
    let end = rest.find(quote)?;
    Some(&rest[..end])
}

fn kind_from_folder_name(path: &Path) -> Option<EntityKind> {
    let parent = path.parent()?.file_name()?.to_str()?;
    match parent.to_ascii_lowercase().as_str() {
        "procedure" | "procedures" => Some(EntityKind::Procedure),
        "transaction" | "transactions" => Some(EntityKind::Transaction),
        "webpanel" | "webpanels" => Some(EntityKind::WebPanel),
        "workwithforweb" | "workwith" => Some(EntityKind::WorkWithPlus),
        "sdpanel" | "panel" => Some(EntityKind::Panel),
        "table" | "tables" => Some(EntityKind::Table),
        "attribute" | "attributes" => Some(EntityKind::Attribute),
        "domain" | "domains" => Some(EntityKind::Domain),
        "sdt" | "sdts" => Some(EntityKind::Sdt),
        "dataselector" | "dataselectors" => Some(EntityKind::DataSelector),
        _ => None,
    }
}

/// Best-effort object kind from a file name, used only when the file's own
/// content cannot say.
///
/// The old chain tested a single leading letter, so every Transaction whose
/// name began with `P` - `PessoaDadosFiscaisAdicionais` among them - was
/// reported as `kind: "Proc"`, and `callers` answered on the wrong object type
/// (#500). Two-letter prefixes are unambiguous in GeneXus exports and are
/// therefore checked first; a bare single-letter guess is no longer made,
/// because `P` alone cannot distinguish a Procedure from a Transaction named
/// after a person, and guessing wrong is worse than reporting `Unknown`.
///
/// Content detection remains authoritative for XML exports, where the element
/// name says the real kind.
fn kind_from_stem(file_stem: &str) -> EntityKind {
    let stem = file_stem.to_ascii_uppercase();
    if stem.starts_with("UT_") {
        return EntityKind::Test;
    }
    if stem.starts_with("TRN") {
        return EntityKind::Transaction;
    }
    if stem.starts_with("WP") {
        return EntityKind::WebPanel;
    }
    if stem.starts_with("SDP") {
        return EntityKind::Panel;
    }
    if stem.starts_with("WW") {
        return EntityKind::WorkWithPlus;
    }
    if stem.starts_with("PROC") {
        return EntityKind::Procedure;
    }
    if stem.starts_with("DS") {
        return EntityKind::DataSelector;
    }
    EntityKind::Unknown
}

fn detect_entity_kind(path: &Path, file_stem: &str, ext: &str) -> EntityKind {
    match ext.to_ascii_lowercase().as_str() {
        "prc" => EntityKind::Procedure,
        "trn" => EntityKind::Transaction,
        "wp" => EntityKind::WebPanel,
        "sdp" => EntityKind::Panel,
        "xml" => {
            if let Ok(file) = File::open(path) {
                let mut buf = [0u8; 1024];
                let mut reader = BufReader::new(file);
                if let Ok(n) = reader.read(&mut buf) {
                    let head = String::from_utf8_lossy(&buf[..n]).to_ascii_lowercase();
                    if head.contains("<procedure") {
                        return EntityKind::Procedure;
                    } else if head.contains("<transaction") {
                        return EntityKind::Transaction;
                    } else if head.contains("<webpanel") {
                        return EntityKind::WebPanel;
                    } else if head.contains("<sdpanel") {
                        return EntityKind::Panel;
                    } else if head.contains("<workwith") {
                        return EntityKind::WorkWithPlus;
                    } else if head.contains("<table") {
                        return EntityKind::Table;
                    } else if head.contains("<attribute") {
                        return EntityKind::Attribute;
                    } else if head.contains("<domain") {
                        return EntityKind::Domain;
                    } else if head.contains("<sdt") {
                        return EntityKind::Sdt;
                    } else if head.contains("<dataselector") {
                        return EntityKind::DataSelector;
                    } else if head.contains("<object")
                        && let Some(guid) = extract_type_guid_from_xml(&head)
                        && let Some(kind) = kind_from_type_guid(guid)
                    {
                        return kind;
                    }
                }
            }
            if let Some(kind) = kind_from_folder_name(path) {
                return kind;
            }
            kind_from_stem(file_stem)
        }
        _ => kind_from_stem(file_stem),
    }
}
