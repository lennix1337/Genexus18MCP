//! A rebuild into a directory that holds a previous run reuses that run's work. Whatever is reused, the
//! directory must hold exactly what a rebuild of the same model into an empty directory writes, and the
//! report must be that run's report. Each test here edits a model, rebuilds into the same directory, and
//! compares every file and the report against a fresh rebuild.
//!
//! The comparison leaves out `obj/`, which holds build state (the record of the run), not generated files.

use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    csharp::webform::WebPanelEnvironment,
    rebuild::{RebuildOptions, RebuildReport, rebuild_all},
    table::SqlDialect,
    udm::reader::DOMAIN_ENUM_PART,
    webpanel_settings::WebPanelSettings,
};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};

const LIVEIMPORT_WEBFORM: &str = include_str!("fixtures/webform/liveimportwebpanel.webform.xml");
const PLAIN_VARIABLES: &str = include_str!("fixtures/webform/plain.variables.xml");
const PAGE_DOMAIN: &str = include_str!("fixtures/sdk_form/page.domain.xml");

/// The generation stamp every run here uses: a web class holds it, so two runs at different times
/// compare byte for byte only under one stamp.
const STAMP: &str = "20260101000000";

fn id(value: &str) -> ObjectId {
    ObjectId::new(value).unwrap()
}

fn part(name: &str, source: &str) -> SourcePart {
    SourcePart {
        name: name.into(),
        source: source.into(),
    }
}

fn procedure(name: &str, source: &str, variables: &str, rules: &str) -> KbObject {
    let mut object = KbObject::new(id(&name.to_ascii_lowercase()), name, ObjectKind::Procedure);
    object.source_parts.push(part("SOURCE", source));
    object.source_parts.push(part("VARIABLES", variables));
    if !rules.is_empty() {
        object.source_parts.push(part("RULES", rules));
    }
    object
}

const NUMBERS: &str = r#"{
    "A": {"kind": "numeric", "precision": 5, "scale": 2},
    "B": {"kind": "numeric", "precision": 5, "scale": 2},
    "C": {"kind": "numeric", "precision": 5, "scale": 2}
}"#;

const CUSTOMER_SDT: &str = r#"{"fields": [
    {"node": "field", "name": "CustomerId", "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
    {"node": "field", "name": "CustomerName", "data_type": {"kind": "varchar", "length": 50}}
]}"#;

const CUSTOMER_STRUCTURE: &str = r#"{"root": {"name": "Customer", "attributes": [
    {"name": "CustomerId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
    {"name": "CustomerName"},
    {"name": "CustomerTotal", "data_type": {"kind": "numeric", "precision": 12, "scale": 2}}
]}}"#;

fn sdt(structure: &str) -> KbObject {
    let mut object = KbObject::new(
        id("customer_sdt"),
        "CustomerSDT",
        ObjectKind::StructuredDataType,
    );
    object.source_parts.push(part("STRUCTURE", structure));
    object
}

fn transaction(structure: &str) -> KbObject {
    let mut object = KbObject::new(id("customer_tx"), "Customer", ObjectKind::Transaction);
    object.source_parts.push(part("STRUCTURE", structure));
    object
}

fn name_domain(length: u32) -> KbObject {
    let mut object = KbObject::new(id("dom_name"), "NameDomain", ObjectKind::Domain);
    object.source_parts.push(part(
        "TYPE",
        &format!(r#"{{"kind":"varchar","length":{length}}}"#),
    ));
    object
}

fn name_attribute(type_part: &str) -> KbObject {
    let mut object = KbObject::new(id("att_name"), "CustomerName", ObjectKind::Attribute);
    object.source_parts.push(part("TYPE", type_part));
    object.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: id("dom_name"),
    });
    object
}

fn page_domain(properties: &str) -> KbObject {
    let mut object = KbObject::new(id("dom_page"), "Page", ObjectKind::Domain);
    object.module_path = vec!["General".into()];
    object.source_parts.push(part(DOMAIN_ENUM_PART, properties));
    object
}

fn settings(entity_id: i32, declared: &str, web_comp: Option<&str>) -> SourcePart {
    WebPanelSettings {
        entity_id,
        declared_name: declared.into(),
        description: None,
        master_page_id: None,
        web_comp: web_comp.map(str::to_string),
    }
    .to_part()
    .unwrap()
}

fn master() -> KbObject {
    let mut object = KbObject::new(
        id("masterunanimosidebar"),
        "MasterUnanimoSidebar",
        ObjectKind::WebPanel,
    );
    object
        .source_parts
        .push(settings(1, "MasterUnanimoSidebar", Some("Master")));
    object
}

fn panel(entity_id: i32) -> KbObject {
    let mut object = KbObject::new(
        id("liveimportwebpanel"),
        "LiveImportWebPanel",
        ObjectKind::WebPanel,
    );
    object
        .source_parts
        .push(part("WEBFORM", LIVEIMPORT_WEBFORM));
    object.source_parts.push(part("VARIABLES", PLAIN_VARIABLES));
    object
        .source_parts
        .push(settings(entity_id, "LiveImportWebPanel", None));
    object.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("masterunanimosidebar"),
    });
    object
}

/// The model the tests edit: Procedures that call each other, use a structured data type and navigate a
/// table, a Transaction whose attribute is typed through an Attribute and a Domain, an enumerated Domain,
/// and a plain WebPanel with its master.
fn model() -> BTreeMap<&'static str, KbObject> {
    let mut use_sdt = procedure(
        "UseSdt",
        "&Cust.CustomerName = 'Ana'\nmsg(&Cust.CustomerName, status)\n",
        r#"{"Cust": {"kind": "record", "schema": "CustomerSDT"}}"#,
        "",
    );
    use_sdt.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("customer_sdt"),
    });
    BTreeMap::from([
        ("hello", procedure("Hello", "msg('hi', status)\n", "{}", "")),
        (
            "callee",
            procedure(
                "CalleeProc",
                "msg('callee', status)\n",
                NUMBERS,
                "Parm(in:&A, out:&B, inout:&C);",
            ),
        ),
        (
            "caller",
            procedure("CallerProc", "CalleeProc(&A, &B, &C)\n", NUMBERS, ""),
        ),
        (
            "list",
            procedure(
                "ListCustomers",
                "For each\n    &T = CustomerTotal\n    msg(trim(str(&T, 12, 2)), status)\nEndFor\n",
                r#"{"T": {"kind": "numeric", "precision": 12, "scale": 2}}"#,
                "",
            ),
        ),
        ("use_sdt", use_sdt),
        ("sdt", sdt(CUSTOMER_SDT)),
        ("transaction", transaction(CUSTOMER_STRUCTURE)),
        ("name_domain", name_domain(50)),
        (
            "name_attribute",
            name_attribute(r#"{"based_on":"NameDomain"}"#),
        ),
        ("page_domain", page_domain(PAGE_DOMAIN.trim())),
        ("master", master()),
        ("panel", panel(3)),
    ])
}

fn catalog_of(objects: &BTreeMap<&'static str, KbObject>) -> Catalog {
    let edits: Vec<Edit> = objects.values().cloned().map(Edit::Upsert).collect();
    let mut catalog = Catalog::empty();
    catalog.apply(0, &edits).expect("catalog setup");
    catalog
}

fn web_environment() -> WebPanelEnvironment {
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::from([(
            "MasterUnanimoSidebar".to_string(),
            "general.ui.masterunanimosidebar".to_string(),
        )]),
        build_number: Some("239440".into()),
        support_class: Some("namespace GeneXus.Programs { public class GxWebStd { } }".into()),
        design_classes: BTreeMap::new(),
        object_guids: BTreeMap::new(),
        attributes: BTreeMap::new(),
        table_keys: BTreeMap::new(),
        subtype_groups: BTreeMap::new(),
    }
}

/// The options of a run that reuses what it can: the output directory is not cleaned.
fn options() -> RebuildOptions {
    RebuildOptions::new("net8.0")
        .with_clean(false)
        .with_concurrency(3)
        .with_skip_compile(true)
        .with_assembly_name("IncrementalApp")
        .with_web_environment(web_environment())
        .with_client_config(Ok("gx.config = {};".to_string()))
        .with_model_files(vec![(
            "messages".to_string(),
            Ok(vec![(
                "resources/messages.eng.txt".to_string(),
                b"hi".to_vec(),
            )]),
        )])
        .with_generation_stamp(STAMP)
        .unwrap()
}

static SEQUENCE: AtomicU64 = AtomicU64::new(0);

fn temp_dir(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "gx_incremental_{tag}_{}_{}",
        std::process::id(),
        SEQUENCE.fetch_add(1, Ordering::Relaxed)
    ));
    let _ = fs::remove_dir_all(&dir);
    dir
}

/// Every generated file under `dir` by its path relative to it, without the build state in `obj/`.
fn tree(dir: &Path) -> BTreeMap<String, Vec<u8>> {
    fn walk(root: &Path, dir: &Path, files: &mut BTreeMap<String, Vec<u8>>) {
        for entry in fs::read_dir(dir).unwrap() {
            let entry = entry.unwrap();
            let path = entry.path();
            let relative = path
                .strip_prefix(root)
                .unwrap()
                .to_string_lossy()
                .replace('\\', "/");
            if entry.file_type().unwrap().is_dir() {
                if relative != "obj" {
                    walk(root, &path, files);
                    assert!(
                        fs::read_dir(&path).unwrap().next().is_some(),
                        "{relative} is an empty folder"
                    );
                }
            } else {
                files.insert(relative, fs::read(&path).unwrap());
            }
        }
    }
    let mut files = BTreeMap::new();
    walk(dir, dir, &mut files);
    files
}

/// The report without what a run that reuses is allowed to differ in: its timings, where its files are,
/// and what it reused.
fn outcome(report: &RebuildReport, dir: &Path) -> serde_json::Value {
    let mut report = report.clone();
    (report.read_ms, report.spec_ms, report.emit_ms) = (0, 0, 0);
    (report.compile_ms, report.total_ms) = (0, 0);
    report.reuse = Default::default();
    report.emitted_files = report
        .emitted_files
        .iter()
        .map(|path| {
            path.strip_prefix(dir)
                .expect("under the output")
                .to_path_buf()
        })
        .collect();
    serde_json::to_value(&report).unwrap()
}

fn assert_same_tree(
    what: &str,
    actual: &BTreeMap<String, Vec<u8>>,
    expected: &BTreeMap<String, Vec<u8>>,
) {
    let names = |tree: &BTreeMap<String, Vec<u8>>| tree.keys().cloned().collect::<Vec<_>>();
    assert_eq!(
        names(actual),
        names(expected),
        "{what}: the file sets differ"
    );
    for (name, bytes) in expected {
        assert!(
            &actual[name] == bytes,
            "{what}: {name} differs from the fresh rebuild\n--- reused ---\n{}\n--- fresh ---\n{}",
            String::from_utf8_lossy(&actual[name]),
            String::from_utf8_lossy(bytes)
        );
    }
}

/// One output directory rebuilt after each edit, checked every time against a fresh rebuild.
struct Workspace {
    dir: PathBuf,
    tag: String,
}

impl Workspace {
    fn new(tag: &str) -> Self {
        Self {
            dir: temp_dir(tag),
            tag: tag.to_string(),
        }
    }

    /// Rebuilds `objects` into the workspace with `options`, and checks the directory and the report
    /// against a rebuild of the same model, with the same options, into an empty directory.
    fn rebuild_with(
        &self,
        objects: &BTreeMap<&'static str, KbObject>,
        options: &RebuildOptions,
    ) -> RebuildReport {
        let catalog = catalog_of(objects);
        let report = rebuild_all(&catalog, &self.dir, options).expect("the reusing rebuild runs");

        let fresh_dir = temp_dir(&format!("{}_fresh", self.tag));
        let mut fresh_options = options.clone();
        fresh_options.artifact_cache_dir = None;
        let fresh = rebuild_all(&catalog, &fresh_dir, &fresh_options).expect("the fresh rebuild");
        assert_eq!(fresh.reuse.mode, "full");
        assert_same_tree(&self.tag, &tree(&self.dir), &tree(&fresh_dir));
        assert_eq!(
            outcome(&report, &self.dir),
            outcome(&fresh, &fresh_dir),
            "{}: the report differs from the fresh rebuild's",
            self.tag
        );
        let _ = fs::remove_dir_all(&fresh_dir);
        report
    }

    fn rebuild(&self, objects: &BTreeMap<&'static str, KbObject>) -> RebuildReport {
        self.rebuild_with(objects, &options())
    }

    fn modified(&self, name: &str) -> std::time::SystemTime {
        fs::metadata(self.dir.join(name))
            .unwrap_or_else(|error| panic!("{name}: {error}"))
            .modified()
            .unwrap()
    }

    fn manifest_path(&self) -> PathBuf {
        self.dir
            .join("obj")
            .join("gx-incremental")
            .join("manifest.json")
    }
}

impl Drop for Workspace {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.dir);
    }
}

fn set_source(object: &mut KbObject, source: &str) {
    let part = object
        .source_parts
        .iter_mut()
        .find(|part| part.name == "SOURCE")
        .unwrap();
    part.source = source.into();
}

/// The Procedures of the model: every one is specified, and all but `ListCustomers` are generated. Its
/// `For each` has no generated form in this build, so it is refused by name on every run that generates.
const PROCEDURES: usize = 5;
const GENERATED: usize = PROCEDURES - 1;
/// What a rebuild of the model publishes to the artifact cache: each generated emission, each
/// specification, and the SDT files.
const CACHE_ENTRIES: usize = GENERATED + PROCEDURES + 1;

fn generation_failures(report: &RebuildReport) -> Vec<&String> {
    report
        .errors
        .iter()
        .filter(|error| error.contains("failed to generate C# for Procedure"))
        .collect()
}

#[test]
fn the_first_run_is_full_and_generates_the_whole_model() {
    let workspace = Workspace::new("first");
    let report = workspace.rebuild(&model());

    assert_eq!(report.reuse.mode, "full");
    assert_eq!(report.reuse.procedures_emitted, PROCEDURES);
    assert_eq!(report.errors.len(), 1, "{:?}", report.errors);
    assert!(
        report.errors[0].contains("ListCustomers"),
        "{:?}",
        report.errors
    );
    // Every kind of generated file the model reaches is there, so each comparison below covers it.
    let files = tree(&workspace.dir);
    for name in [
        "ahello.cs",
        "hello.cs",
        "hello.csproj",
        "ahello.exe.config",
        "calleeproc.cs",
        "acallerproc.cs",
        "ausesdt.cs",
        "SdtCustomerSDT.cs",
        "gxdomainpage.cs",
        "liveimportwebpanel.cs",
        "liveimportwebpanel.js",
        "gxcfg.js",
        "GxWebStd.cs",
        "GxModelInfoProvider.cs",
        "resources/messages.eng.txt",
        "IncrementalApp.csproj",
    ] {
        assert!(
            files.contains_key(name),
            "{name} missing: {:?}",
            files.keys()
        );
    }
    assert!(!files.contains_key("alistcustomers.cs"));
    assert_eq!(report.navigation_reports.len(), PROCEDURES);
    assert!(workspace.manifest_path().is_file());
}

#[test]
fn an_unchanged_model_generates_nothing_and_touches_no_file() {
    let workspace = Workspace::new("unchanged");
    let model = model();
    workspace.rebuild(&model);
    let before: BTreeMap<String, std::time::SystemTime> = tree(&workspace.dir)
        .keys()
        .map(|name| (name.clone(), workspace.modified(name)))
        .collect();

    let report = workspace.rebuild(&model);

    assert_eq!(report.reuse.mode, "unchanged");
    assert_eq!(report.reuse.procedures_emitted, 0);
    for (name, modified) in before {
        assert_eq!(workspace.modified(&name), modified, "{name} was rewritten");
    }
}

#[test]
fn a_changed_procedure_body_emits_only_that_procedure() {
    let workspace = Workspace::new("body");
    let mut model = model();
    workspace.rebuild(&model);
    let kept = workspace.modified("acallerproc.cs");

    set_source(model.get_mut("hello").unwrap(), "msg('changed', status)\n");
    let report = workspace.rebuild(&model);

    assert_eq!(report.reuse.mode, "incremental");
    // The changed one, and the refused one: a refusal is never taken from the previous run.
    assert_eq!(report.reuse.procedures_emitted, 2);
    assert_eq!(report.reuse.procedures_kept, GENERATED - 1);
    assert_eq!(report.reuse.specifications_computed, 1);
    assert_eq!(report.reuse.specifications_kept, PROCEDURES - 1);
    assert!(report.reuse.sdts_kept);
    assert_eq!(generation_failures(&report).len(), 1);
    assert_eq!(workspace.modified("acallerproc.cs"), kept);
    let hello = fs::read_to_string(workspace.dir.join("ahello.cs")).unwrap();
    assert!(hello.contains("changed"), "{hello}");
}

#[test]
fn a_changed_signature_emits_its_callers_again() {
    let workspace = Workspace::new("signature");
    let mut model = model();
    workspace.rebuild(&model);

    // `B` was `out`: the caller's call site is written from the callee's rule.
    let callee = model.get_mut("callee").unwrap();
    callee
        .source_parts
        .iter_mut()
        .find(|part| part.name == "RULES")
        .unwrap()
        .source = "Parm(in:&A, inout:&B, inout:&C);".into();
    let report = workspace.rebuild(&model);

    assert_eq!(
        report.reuse.procedures_kept, 0,
        "every call site may read it"
    );
    let caller = fs::read_to_string(workspace.dir.join("acallerproc.cs")).unwrap();
    assert!(caller.contains("ref AV_B"), "{caller}");
}

#[test]
fn a_changed_sdt_emits_the_sdt_and_the_procedures_again() {
    let workspace = Workspace::new("sdt");
    let mut model = model();
    workspace.rebuild(&model);

    model.insert(
        "sdt",
        sdt(&CUSTOMER_SDT.replace(r#""length": 50"#, r#""length": 80"#)),
    );
    let report = workspace.rebuild(&model);

    assert!(!report.reuse.sdts_kept);
    assert_eq!(
        report.reuse.procedures_kept, 0,
        "any member may resolve to it"
    );
    // Their specifications read no SDT.
    assert_eq!(report.reuse.specifications_kept, PROCEDURES);
}

#[test]
fn a_changed_sdt_member_refuses_the_procedure_that_uses_it() {
    let workspace = Workspace::new("sdt_member");
    let mut model = model();
    workspace.rebuild(&model);

    model.insert(
        "sdt",
        sdt(&CUSTOMER_SDT.replace("CustomerName", "CustomerTitle")),
    );
    let report = workspace.rebuild(&model);

    assert!(
        report.errors.iter().any(|error| error.contains("UseSdt")),
        "{:?}",
        report.errors
    );
    assert!(!workspace.dir.join("ausesdt.cs").exists());
}

#[test]
fn a_changed_domain_or_attribute_specifies_the_procedures_again() {
    let workspace = Workspace::new("domain");
    let mut model = model();
    workspace.rebuild(&model);

    // The Transaction's `CustomerName` is typed by the Attribute, which is based on the Domain.
    model.insert("name_domain", name_domain(80));
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.specifications_kept, 0, "the tables changed");
    assert_eq!(report.reuse.procedures_kept, GENERATED);

    model.insert(
        "name_attribute",
        name_attribute(r#"{"kind":"character","length":20}"#),
    );
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.specifications_kept, 0, "the tables changed");

    model.insert(
        "page_domain",
        page_domain(&PAGE_DOMAIN.trim().replace(
            "<Name>Length</Name><Value>4</Value>",
            "<Name>Length</Name><Value>3</Value>",
        )),
    );
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "incremental");
}

#[test]
fn a_changed_transaction_changes_the_navigation_of_the_procedure_that_reads_it() {
    let workspace = Workspace::new("transaction");
    let mut model = model();
    let first = workspace.rebuild(&model);

    model.insert(
        "transaction",
        transaction(&CUSTOMER_STRUCTURE.replace(r#""name": "Customer","#, r#""name": "Client","#)),
    );
    let report = workspace.rebuild(&model);

    assert_ne!(
        serde_json::to_string(&report.navigation_reports).unwrap(),
        serde_json::to_string(&first.navigation_reports).unwrap(),
        "the edit does not reach a navigation report, so it proves nothing"
    );
}

#[test]
fn an_added_object_is_generated_and_the_rest_is_kept() {
    let workspace = Workspace::new("added");
    let mut model = model();
    workspace.rebuild(&model);

    model.insert(
        "extra",
        procedure("Extra", "Hello()\nmsg('extra', status)\n", "{}", ""),
    );
    let report = workspace.rebuild(&model);

    // A new Procedure is a new entry of the call table every call site resolves against.
    assert_eq!(report.reuse.procedures_kept, 0);
    assert!(report.reuse.sdts_kept);
    assert!(workspace.dir.join("aextra.cs").is_file());

    model.insert("extra_sdt", {
        let mut extra = sdt(CUSTOMER_SDT);
        extra.id = id("extra_sdt");
        extra.name = "ExtraSDT".into();
        extra
    });
    model
        .get_mut("use_sdt")
        .unwrap()
        .references
        .push(Reference {
            kind: ReferenceKind::Uses,
            target: id("extra_sdt"),
        });
    let report = workspace.rebuild(&model);
    assert!(!report.reuse.sdts_kept);
    assert!(workspace.dir.join("SdtExtraSDT.cs").is_file());
}

#[test]
fn a_deleted_object_leaves_no_file_behind() {
    let workspace = Workspace::new("deleted");
    let mut model = model();
    workspace.rebuild(&model);
    assert!(workspace.dir.join("ahello.cs").is_file());

    model.remove("hello");
    let report = workspace.rebuild(&model);
    for name in [
        "ahello.cs",
        "hello.cs",
        "hello.csproj",
        "ahello.exe.config",
        "hello.exe.config",
    ] {
        assert!(!workspace.dir.join(name).exists(), "{name} is stale");
    }
    assert_eq!(report.reuse.stale_files_removed, 5);

    // The last file of a folder takes the folder with it, and a deleted web object its script.
    model.remove("panel");
    let mut without_messages = options();
    without_messages.model_files.clear();
    workspace.rebuild_with(&model, &without_messages);
    assert!(!workspace.dir.join("resources").exists());
    assert!(!workspace.dir.join("liveimportwebpanel.js").exists());

    // The callee goes: its caller is refused by name and its files go too.
    model.remove("callee");
    let report = workspace.rebuild_with(&model, &without_messages);
    assert!(
        report
            .errors
            .iter()
            .any(|error| error.contains("CallerProc")),
        "{:?}",
        report.errors
    );
    assert!(!workspace.dir.join("acallerproc.cs").exists());
}

#[test]
fn a_renamed_object_moves_its_files() {
    let workspace = Workspace::new("renamed");
    let mut model = model();
    workspace.rebuild(&model);

    // The same object under another name, as a rename in the IDE leaves it.
    model.get_mut("hello").unwrap().name = "Greeting".into();
    workspace.rebuild(&model);
    assert!(!workspace.dir.join("ahello.cs").exists());
    assert!(workspace.dir.join("agreeting.cs").is_file());

    // A rename that only changes the case of a file name: on a case-insensitive file system the old and
    // the new name are one file, which must end under the new name, not be removed as stale.
    model.get_mut("sdt").unwrap().name = "CUSTOMERSDT".into();
    let variables = model
        .get_mut("use_sdt")
        .unwrap()
        .source_parts
        .iter_mut()
        .find(|part| part.name == "VARIABLES")
        .unwrap();
    variables.source = variables.source.replace("CustomerSDT", "CUSTOMERSDT");
    workspace.rebuild(&model);
    let names: Vec<String> = fs::read_dir(&workspace.dir)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .filter(|name| name.to_lowercase() == "sdtcustomersdt.cs")
        .collect();
    assert_eq!(names, ["SdtCUSTOMERSDT.cs"]);
}

#[test]
fn a_changed_generator_setting_reaches_every_file_it_is_written_into() {
    let workspace = Workspace::new("settings");
    let model = model();
    workspace.rebuild(&model);

    // The target framework is written into each Procedure's project.
    let mut net9 = options();
    net9.target_framework = "net9.0".into();
    let report = workspace.rebuild_with(&model, &net9);
    assert_eq!(report.reuse.procedures_kept, 0);
    let project = fs::read_to_string(workspace.dir.join("hello.csproj")).unwrap();
    assert!(project.contains("net9.0"), "{project}");

    // The assembly name is the main project's file name.
    let renamed = net9.clone().with_assembly_name("OtherApp");
    workspace.rebuild_with(&model, &renamed);
    assert!(!workspace.dir.join("IncrementalApp.csproj").exists());

    // The theme and the runtime version are written into a web class and the main project.
    let mut themed = renamed.clone().with_genexus_runtime_version("[1.31.0]");
    themed.web_environment.theme = Some("OtherTheme".into());
    let report = workspace.rebuild_with(&model, &themed);
    assert_eq!(report.reuse.mode, "incremental");
    assert_eq!(report.reuse.procedures_kept, GENERATED);

    // The dialect is read by every specification.
    let report =
        workspace.rebuild_with(&model, &themed.clone().with_sql_dialect(SqlDialect::Oracle));
    assert_eq!(report.reuse.specifications_kept, 0);

    // A model file's content and the client configuration.
    let mut files = themed
        .clone()
        .with_client_config(Ok("gx.config = {a:1};".into()));
    files.model_files = vec![(
        "messages".to_string(),
        Ok(vec![(
            "resources/messages.eng.txt".to_string(),
            b"bye".to_vec(),
        )]),
    )];
    workspace.rebuild_with(&model, &files);

    // And back to the first settings: nothing of the runs in between may remain.
    workspace.rebuild(&model);
}

#[test]
fn a_sequence_of_edits_ends_where_a_fresh_rebuild_ends() {
    let workspace = Workspace::new("sequence");
    let mut model = model();
    workspace.rebuild(&model);

    set_source(model.get_mut("hello").unwrap(), "msg('one', status)\n");
    workspace.rebuild(&model);
    model.insert(
        "extra",
        procedure("Extra", "msg('extra', status)\n", "{}", ""),
    );
    workspace.rebuild(&model);
    set_source(
        model.get_mut("caller").unwrap(),
        "CalleeProc(&A, &B, &C)\nExtra()\n",
    );
    workspace.rebuild(&model);
    model.remove("extra");
    let report = workspace.rebuild(&model);
    assert!(
        report
            .errors
            .iter()
            .any(|error| error.contains("CallerProc")),
        "{:?}",
        report.errors
    );
    set_source(model.get_mut("caller").unwrap(), "CalleeProc(&A, &B, &C)\n");
    model.get_mut("hello").unwrap().name = "Hola".into();
    workspace.rebuild(&model);
    // Back to the first model, by another road.
    model.get_mut("hello").unwrap().name = "Hello".into();
    set_source(model.get_mut("hello").unwrap(), "msg('hi', status)\n");
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "incremental");
    assert_eq!(report.reuse.procedures_kept, 0, "the call table changed");
}

/// Sixty edits drawn from a fixed pseudo-random sequence, one rebuild after each, in one directory: every
/// kind of edit above, plus damage to the generated files, in orders no test above spells out.
#[test]
fn a_long_pseudo_random_sequence_of_edits_always_matches_a_fresh_rebuild() {
    let workspace = Workspace::new("random");
    let mut model = model();
    let mut options = options();
    workspace.rebuild_with(&model, &options);

    let mut state: u64 = 0x5eed_2026;
    let mut next = |bound: usize| {
        state = state
            .wrapping_mul(6_364_136_223_846_793_005)
            .wrapping_add(1_442_695_040_888_963_407);
        ((state >> 33) as usize) % bound
    };
    let mut modes = BTreeMap::new();
    for step in 0..60 {
        match next(12) {
            0 => set_source(
                model.get_mut("hello").unwrap(),
                &format!("msg('step {step}', status)\n"),
            ),
            1 => {
                let rules = model
                    .get_mut("callee")
                    .unwrap()
                    .source_parts
                    .iter_mut()
                    .find(|part| part.name == "RULES")
                    .unwrap();
                rules.source = if rules.source.contains("out:&B") {
                    "Parm(in:&A, inout:&B, inout:&C);".into()
                } else {
                    "Parm(in:&A, out:&B, inout:&C);".into()
                };
            }
            2 => {
                let length = if step % 2 == 0 { "80" } else { "50" };
                model.insert(
                    "sdt",
                    sdt(&CUSTOMER_SDT.replace(r#""length": 50"#, &format!(r#""length": {length}"#))),
                );
            }
            3 => {
                if model.remove("extra").is_none() {
                    model.insert(
                        "extra",
                        procedure("Extra", "Hello()\nmsg('extra', status)\n", "{}", ""),
                    );
                }
            }
            4 => {
                let hello = model.get_mut("hello").unwrap();
                hello.name = if hello.name == "Hello" {
                    "Greeting"
                } else {
                    "Hello"
                }
                .into();
            }
            5 => {
                model.insert("name_domain", name_domain(40 + step as u32));
            }
            6 => {
                model.insert("panel", panel(3 + step % 5));
            }
            7 => {
                options.target_framework = if options.target_framework == "net8.0" {
                    "net9.0".into()
                } else {
                    "net8.0".into()
                };
            }
            8 | 9 => {
                // Damage one generated file: overwrite it, or remove it.
                let files: Vec<String> = tree(&workspace.dir).into_keys().collect();
                let victim = workspace.dir.join(&files[next(files.len())]);
                if step % 2 == 0 {
                    fs::write(&victim, format!("damaged at step {step}")).unwrap();
                } else {
                    fs::remove_file(&victim).unwrap();
                }
            }
            10 => {
                let caller = model.get_mut("caller").unwrap();
                let calls_extra = caller.source_parts[0].source.contains("Extra()");
                set_source(
                    caller,
                    if calls_extra {
                        "CalleeProc(&A, &B, &C)\n"
                    } else {
                        "CalleeProc(&A, &B, &C)\nExtra()\n"
                    },
                );
            }
            _ => {
                let table = if step % 2 == 0 { "Client" } else { "Customer" };
                model.insert(
                    "transaction",
                    transaction(
                        &CUSTOMER_STRUCTURE
                            .replace(r#""name": "Customer","#, &format!(r#""name": "{table}","#)),
                    ),
                );
            }
        }
        let report = workspace.rebuild_with(&model, &options);
        *modes.entry(report.reuse.mode).or_insert(0usize) += 1;
    }
    // The sequence exercised reuse, not sixty full rebuilds.
    assert!(
        modes.get("incremental").copied().unwrap_or(0) >= 40,
        "{modes:?}"
    );
    assert_eq!(modes.get("full"), None, "{modes:?}");
}

/// A Procedure that fails to generate is an error on every run, and the caller that needs it is refused
/// by name on every run: neither is taken from the previous run or from the cache.
#[test]
fn a_refusal_is_computed_again_on_every_run() {
    let workspace = Workspace::new("refusal");
    let cache = temp_dir("refusal_cache");
    let options = options().with_artifact_cache_dir(&cache);
    let mut model = model();
    model.remove("list");
    let report = workspace.rebuild_with(&model, &options);
    assert!(report.errors.is_empty(), "{:?}", report.errors);
    assert!(workspace.dir.join("acallerproc.cs").is_file());

    // The callee keeps its signature, so the caller's emission is unchanged, on record and in the cache;
    // but the callee's `For each` has no generated form, so it is not written.
    set_source(
        model.get_mut("callee").unwrap(),
        "For each\n    &A = CustomerTotal\nEndFor\n",
    );
    for _ in 0..2 {
        let report = workspace.rebuild_with(&model, &options);
        let refused = generation_failures(&report);
        assert_eq!(refused.len(), 2, "{:?}", report.errors);
        assert!(
            refused[1].contains("'CallerProc': needs Procedure 'CalleeProc' (not generated"),
            "{refused:?}"
        );
        assert!(!workspace.dir.join("acallerproc.cs").exists());
        assert!(!workspace.dir.join("calleeproc.cs").exists());
        // Touch another object, so the second pass regenerates instead of reporting the first again.
        set_source(model.get_mut("hello").unwrap(), "msg('again', status)\n");
    }

    // A fresh directory fed by the cache refuses the same two.
    let other = Workspace::new("refusal_other");
    let report = other.rebuild_with(&model, &options);
    assert!(report.reuse.cache_entries_restored > 0);
    assert_eq!(generation_failures(&report).len(), 2);
    assert!(!other.dir.join("acallerproc.cs").exists());
    let _ = fs::remove_dir_all(&cache);
}

/// `AFoo` (with a parameter) and the main program `Foo` both write `afoo.cs`; the later write decides
/// the file. A run that kept `Foo` and wrote only `AFoo` would leave the other content.
#[test]
fn two_objects_that_write_one_file_end_as_in_a_fresh_rebuild() {
    let workspace = Workspace::new("collision");
    let mut model = model();
    model.insert("foo", procedure("Foo", "msg('foo', status)\n", "{}", ""));
    workspace.rebuild(&model);

    model.insert(
        "afoo",
        procedure("AFoo", "msg('afoo', status)\n", NUMBERS, "Parm(in:&A);"),
    );
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "full");
    assert_eq!(
        report.reuse.reason.as_deref(),
        Some("two generated objects write the same file")
    );

    // The record of such a run is not built on.
    set_source(model.get_mut("hello").unwrap(), "msg('later', status)\n");
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "full");
    assert_eq!(
        report.reuse.reason.as_deref(),
        Some("the previous run did not leave a state to build on")
    );
}

#[test]
fn a_generated_file_that_changed_on_disk_is_written_again() {
    let workspace = Workspace::new("tampered");
    let mut model = model();
    workspace.rebuild(&model);

    // Unchanged model: the file is restored (with the refused Procedure, emitted on every run).
    fs::write(workspace.dir.join("acallerproc.cs"), "tampered").unwrap();
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "incremental");
    assert_eq!(report.reuse.procedures_emitted, 2);
    assert_eq!(report.reuse.procedures_kept, GENERATED - 1);

    // Changed model: a missing file of a kept Procedure, and a changed SDT file, are restored too.
    fs::remove_file(workspace.dir.join("hello.exe.config")).unwrap();
    fs::write(workspace.dir.join("SdtCustomerSDT.cs"), "tampered").unwrap();
    set_source(
        model.get_mut("caller").unwrap(),
        "CalleeProc(&A, &B, &C)\nmsg('x', status)\n",
    );
    let report = workspace.rebuild(&model);
    assert_eq!(
        report.reuse.procedures_emitted, 3,
        "the changed, the damaged and the refused"
    );
    assert!(!report.reuse.sdts_kept);
}

/// A file that cannot be written keeps its earlier content, on disk and with its earlier length and
/// time. A record saying the changed Procedure was written would make the next run keep that file.
#[cfg(windows)]
#[test]
fn a_run_that_could_not_write_a_file_is_not_built_on() {
    use std::os::windows::fs::OpenOptionsExt;

    let workspace = Workspace::new("locked");
    let mut model = model();
    workspace.rebuild(&model);

    set_source(model.get_mut("hello").unwrap(), "msg('changed', status)\n");
    {
        // Share mode 0 keeps every other handle out, so the file can be neither read nor written.
        let _lock = fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(workspace.dir.join("ahello.cs"))
            .unwrap();
        let report = rebuild_all(&catalog_of(&model), &workspace.dir, &options()).unwrap();
        assert!(
            report
                .errors
                .iter()
                .any(|error| error.contains("failed to write")),
            "{:?}",
            report.errors
        );
    }

    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "full", "{:?}", report.reuse);
    let hello = fs::read_to_string(workspace.dir.join("ahello.cs")).unwrap();
    assert!(hello.contains("changed"), "{hello}");
}

/// A run that stops after it wrote some files leaves a directory that no record describes. The main
/// project cannot be written here, which ends the run with an error after every class was written.
#[cfg(windows)]
#[test]
fn a_run_that_was_cut_off_is_not_built_on() {
    use std::os::windows::fs::OpenOptionsExt;

    let workspace = Workspace::new("cut_off");
    let mut model = model();
    workspace.rebuild(&model);

    set_source(model.get_mut("hello").unwrap(), "msg('changed', status)\n");
    model.remove("use_sdt");
    {
        let _lock = fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(workspace.dir.join("IncrementalApp.csproj"))
            .unwrap();
        let error = rebuild_all(&catalog_of(&model), &workspace.dir, &options()).unwrap_err();
        assert!(
            error.contains("failed to write main project file"),
            "{error}"
        );
    }

    // The files of the removed Procedure are still listed, so this run removes them.
    assert!(workspace.dir.join("ausesdt.cs").is_file());
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "full", "{:?}", report.reuse);
    assert_eq!(
        report.reuse.reason.as_deref(),
        Some("the previous run did not leave a state to build on")
    );
    assert!(!workspace.dir.join("ausesdt.cs").exists());
}

/// Runs a rebuild of `model` into `dir` while the main project is locked, which fails it after the
/// Procedure files are written: the run is cut off with files on disk that no record lists.
#[cfg(windows)]
fn rebuild_cut_off(dir: &Path, model: &BTreeMap<&'static str, KbObject>) {
    use std::os::windows::fs::OpenOptionsExt;

    let project = dir.join("IncrementalApp.csproj");
    if !project.exists() {
        fs::create_dir_all(dir).unwrap();
        fs::write(&project, "").unwrap();
    }
    let _lock = fs::OpenOptions::new()
        .read(true)
        .share_mode(0)
        .open(&project)
        .unwrap();
    let error = rebuild_all(&catalog_of(model), dir, &options()).unwrap_err();
    assert!(
        error.contains("failed to write main project file"),
        "{error}"
    );
}

#[cfg(windows)]
#[test]
fn a_cut_off_run_never_leaves_a_file_that_a_later_run_keeps() {
    // A run that adds an object is cut off after writing its files: the record cannot list them.
    let workspace = Workspace::new("cut_off_new_file");
    let mut model = self::model();
    let added = model.remove("use_sdt").unwrap();
    workspace.rebuild(&model);
    model.insert("use_sdt", added);
    rebuild_cut_off(&workspace.dir, &model);
    assert!(workspace.dir.join("ausesdt.cs").is_file());
    model.remove("use_sdt");
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "full", "{:?}", report.reuse);
    assert!(!workspace.dir.join("ausesdt.cs").exists());

    // The first run into a directory is cut off: no earlier record exists, and the marker still holds.
    let workspace = Workspace::new("cut_off_first_run");
    let mut model = self::model();
    rebuild_cut_off(&workspace.dir, &model);
    assert!(workspace.dir.join("ausesdt.cs").is_file());
    model.remove("use_sdt");
    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "full", "{:?}", report.reuse);
    assert!(!workspace.dir.join("ausesdt.cs").exists());
}

#[test]
fn a_record_that_cannot_be_trusted_means_a_full_rebuild() {
    let workspace = Workspace::new("record");
    let mut model = model();
    workspace.rebuild(&model);
    let record = fs::read_to_string(workspace.manifest_path()).unwrap();
    let rebuild_after = |record_text: Option<&str>, model: &BTreeMap<&'static str, KbObject>| {
        match record_text {
            Some(text) => fs::write(workspace.manifest_path(), text).unwrap(),
            None => fs::remove_file(workspace.manifest_path()).unwrap(),
        }
        let report = workspace.rebuild(model);
        assert_eq!(report.reuse.mode, "full", "{:?}", report.reuse);
        assert_eq!(report.reuse.procedures_kept, 0);
        report.reuse.reason.unwrap()
    };

    assert!(rebuild_after(None, &model).contains("no record"));
    assert!(rebuild_after(Some("{ not json"), &model).contains("not valid"));
    assert!(rebuild_after(Some(&record[..record.len() / 2]), &model).contains("not valid"));
    let other_format = record.replacen("\"format\":1", "\"format\":0", 1);
    assert_ne!(other_format, record);
    assert!(rebuild_after(Some(&other_format), &model).contains("format 0"));

    // A record of another engine binary: nothing is reused, but the files it lists are still this
    // directory's generated files, so the ones the model no longer produces are removed.
    let digest_at = record.find("\"digest\":\"").unwrap() + "\"digest\":\"".len();
    let mut other_engine = record.clone();
    other_engine.replace_range(digest_at..digest_at + 8, "00000000");
    model.remove("hello");
    assert!(rebuild_after(Some(&other_engine), &model).contains("another engine binary"));
    assert!(!workspace.dir.join("ahello.cs").exists());

    // A record naming a file outside the output directory is rejected as a whole, and nothing outside is
    // touched.
    let outside = workspace.dir.with_extension("outside");
    fs::write(&outside, "keep").unwrap();
    let escaping = fs::read_to_string(workspace.manifest_path())
        .unwrap()
        .replacen(
            "\"files\":{",
            &format!(
                "\"files\":{{\"../{}\":{{\"len\":4,\"modified_ns\":0}},",
                outside.file_name().unwrap().to_string_lossy()
            ),
            1,
        );
    assert!(rebuild_after(Some(&escaping), &model).contains("outside the output"));
    assert_eq!(fs::read_to_string(&outside).unwrap(), "keep");
    let _ = fs::remove_file(&outside);
}

#[test]
fn a_clean_rebuild_reuses_nothing_and_leaves_a_record() {
    let workspace = Workspace::new("clean");
    let model = model();
    workspace.rebuild(&model);

    let report = workspace.rebuild_with(&model, &options().with_clean(true));
    assert_eq!(report.reuse.mode, "full");
    assert_eq!(report.reuse.procedures_emitted, PROCEDURES);

    let report = workspace.rebuild(&model);
    assert_eq!(report.reuse.mode, "unchanged");
}

/// The artifact cache: a second directory, or a second machine, is fed from it.
mod artifact_cache {
    use super::*;

    fn cache_files(cache: &Path) -> Vec<PathBuf> {
        fn walk(dir: &Path, files: &mut Vec<PathBuf>) {
            for entry in fs::read_dir(dir).unwrap() {
                let path = entry.unwrap().path();
                if path.is_dir() {
                    walk(&path, files);
                } else {
                    files.push(path);
                }
            }
        }
        let mut files = Vec::new();
        if cache.is_dir() {
            walk(cache, &mut files);
        }
        files.sort();
        files
    }

    #[test]
    fn a_hit_restores_the_output_of_a_fresh_rebuild() {
        let cache = temp_dir("cache_hit");
        let options = options().with_artifact_cache_dir(&cache);
        let model = model();

        let cold = Workspace::new("cache_cold");
        let report = cold.rebuild_with(&model, &options);
        assert_eq!(report.reuse.cache_entries_restored, 0);
        assert_eq!(report.reuse.cache_entries_stored, CACHE_ENTRIES);

        let warm = Workspace::new("cache_warm");
        let report = warm.rebuild_with(&model, &options);
        assert_eq!(
            report.reuse.mode, "full",
            "an empty directory has no record"
        );
        assert_eq!(report.reuse.cache_entries_restored, CACHE_ENTRIES);
        assert_eq!(report.reuse.cache_entries_stored, 0);
        // The refused Procedure is refused again: no entry was ever stored for it.
        assert_eq!(generation_failures(&report).len(), 1);
        let _ = fs::remove_dir_all(&cache);
    }

    #[test]
    fn a_changed_object_is_a_miss_and_the_rest_are_hits() {
        let cache = temp_dir("cache_miss");
        let options = options().with_artifact_cache_dir(&cache);
        let mut model = model();
        Workspace::new("cache_miss_first").rebuild_with(&model, &options);

        set_source(model.get_mut("hello").unwrap(), "msg('changed', status)\n");
        let report = Workspace::new("cache_miss_second").rebuild_with(&model, &options);
        assert_eq!(report.reuse.cache_entries_restored, CACHE_ENTRIES - 2);
        assert_eq!(
            report.reuse.cache_entries_stored, 2,
            "its emission and its specification"
        );
        let _ = fs::remove_dir_all(&cache);
    }

    #[test]
    fn a_damaged_entry_is_ignored_and_published_again() {
        let cache = temp_dir("cache_damaged");
        let options = options().with_artifact_cache_dir(&cache);
        let model = model();
        Workspace::new("cache_damaged_first").rebuild_with(&model, &options);
        let entries = cache_files(&cache);
        assert_eq!(entries.len(), CACHE_ENTRIES);
        let valid: Vec<Vec<u8>> = entries.iter().map(|path| fs::read(path).unwrap()).collect();

        // Each entry keeps its header and loses the end of its payload: a partial copy.
        for (path, bytes) in entries.iter().zip(&valid) {
            fs::write(path, &bytes[..bytes.len() - 7]).unwrap();
        }
        let report = Workspace::new("cache_damaged_second").rebuild_with(&model, &options);
        assert_eq!(report.reuse.cache_entries_restored, 0);
        assert_eq!(report.reuse.cache_entries_stored, entries.len());
        for (path, bytes) in entries.iter().zip(&valid) {
            assert!(
                &fs::read(path).unwrap() == bytes,
                "{} not republished",
                path.display()
            );
        }

        // Each entry holds another model's text of the same length: the digest no longer matches.
        for (path, bytes) in entries.iter().zip(&valid) {
            let mut changed = bytes.clone();
            let last = changed.len() - 4;
            changed[last] = if changed[last] == b'x' { b'y' } else { b'x' };
            fs::write(path, changed).unwrap();
        }
        let report = Workspace::new("cache_damaged_third").rebuild_with(&model, &options);
        assert_eq!(report.reuse.cache_entries_restored, 0);
        let _ = fs::remove_dir_all(&cache);
    }

    #[test]
    fn concurrent_rebuilds_share_one_cache_directory() {
        let cache = temp_dir("cache_shared");
        let options = options().with_artifact_cache_dir(&cache);
        let model = model();
        std::thread::scope(|scope| {
            for writer in 0..4 {
                let (options, model) = (&options, &model);
                scope.spawn(move || {
                    for round in 0..3 {
                        // Each run publishes and restores the same keys as the others.
                        Workspace::new(&format!("cache_shared_{writer}_{round}"))
                            .rebuild_with(model, options);
                    }
                });
            }
        });
        let entries = cache_files(&cache);
        assert_eq!(
            entries.len(),
            CACHE_ENTRIES,
            "an entry is missing or a temporary file was left: {entries:?}"
        );
        let report = Workspace::new("cache_shared_after").rebuild_with(&model, &options);
        assert_eq!(report.reuse.cache_entries_restored, entries.len());
        let _ = fs::remove_dir_all(&cache);
    }

    #[test]
    fn without_a_cache_directory_nothing_is_written_outside_the_output() {
        let parent = temp_dir("cache_off");
        fs::create_dir_all(&parent).unwrap();
        let out = parent.join("out");
        let catalog = catalog_of(&model());
        let options = options();
        assert_eq!(
            options.artifact_cache_dir, None,
            "the cache is off by default"
        );

        let report = rebuild_all(&catalog, &out, &options).unwrap();
        assert_eq!(
            (
                report.reuse.cache_entries_stored,
                report.reuse.cache_entries_restored
            ),
            (0, 0)
        );
        let beside: Vec<String> = fs::read_dir(&parent)
            .unwrap()
            .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert_eq!(beside, ["out"], "something was created beside the output");
        // Inside the output, build state is the run's record and nothing else.
        let state: Vec<String> = fs::read_dir(out.join("obj"))
            .unwrap()
            .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert_eq!(state, ["gx-incremental"]);

        // With the directory set, the same run does write there.
        let cache = parent.join("cache");
        let cached = options.with_artifact_cache_dir(&cache);
        rebuild_all(&catalog, &parent.join("out2"), &cached).unwrap();
        assert!(!cache_files(&cache).is_empty());
        let _ = fs::remove_dir_all(&parent);
    }
}

mod environment {
    use super::*;
    use gx_engine::rebuild::{BUILD_CACHE_DIR_VARIABLE, GENERATION_STAMP_VARIABLE};

    fn with(pairs: &[(&str, &str)]) -> Result<RebuildOptions, String> {
        RebuildOptions::new("net8.0").with_environment(|name| {
            pairs
                .iter()
                .find(|(variable, _)| *variable == name)
                .map(|(_, value)| value.to_string())
        })
    }

    #[test]
    fn the_cache_and_the_stamp_are_off_unless_their_variables_are_set() {
        let unset = with(&[]).unwrap();
        assert_eq!(
            (unset.artifact_cache_dir, unset.generation_stamp),
            (None, None)
        );
        let blank = with(&[
            (BUILD_CACHE_DIR_VARIABLE, "  "),
            (GENERATION_STAMP_VARIABLE, ""),
        ])
        .unwrap();
        assert_eq!(
            (blank.artifact_cache_dir, blank.generation_stamp),
            (None, None)
        );

        let set = with(&[
            (BUILD_CACHE_DIR_VARIABLE, r"\\server\share\gx-cache"),
            (GENERATION_STAMP_VARIABLE, STAMP),
        ])
        .unwrap();
        assert_eq!(
            set.artifact_cache_dir,
            Some(PathBuf::from(r"\\server\share\gx-cache"))
        );
        assert_eq!(set.generation_stamp.as_deref(), Some(STAMP));
    }

    #[test]
    fn a_stamp_that_is_not_fourteen_digits_is_refused_by_name() {
        for stamp in ["2026", "2026010100000a", "202601010000000"] {
            let error = with(&[(GENERATION_STAMP_VARIABLE, stamp)]).unwrap_err();
            assert!(error.contains(GENERATION_STAMP_VARIABLE), "{error}");
        }
    }
}
