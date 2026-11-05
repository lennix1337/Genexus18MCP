//! Phase 2: emits the C# classes, support files and project definitions of the model.

use super::{
    RebuildOptions,
    classify::ObjectManifest,
    parallel::{Findings, map_chunks},
};
use crate::csharp::{
    CsprojConfig, CsprojEmitter, ProcedureClass, SdtClass, calls::CallSignatures,
    procedure::CheckedEmit, sdt::SdtIndex, webform,
};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::{Path, PathBuf},
    sync::Arc,
    time::Instant,
};

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
}

impl<'a> OutputTree<'a> {
    fn new(dir: &'a Path) -> Self {
        Self {
            dir,
            files: Vec::new(),
            errors: Vec::new(),
        }
    }

    fn merge(&mut self, other: OutputTree<'_>) {
        self.files.extend(other.files);
        self.errors.extend(other.errors);
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
            Err(e) => self.errors.push(format!("{relative}: not written: {e}")),
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
            Err(e) => self
                .errors
                .push(format!("failed to write '{}': {e}", file_path.display())),
        }
    }
}

/// One Procedure's emission, settled before any file is written.
struct ProcedureEmission {
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

/// Emits the whole model into `output_dir`. Returns the written files (sorted), the main project path and
/// the elapsed milliseconds; write and generation failures are added to `findings`.
pub(super) fn emission_phase(
    manifest: &ObjectManifest<'_>,
    output_dir: &Path,
    concurrency: usize,
    options: &RebuildOptions,
    findings: &mut Findings,
) -> Result<(Vec<PathBuf>, PathBuf, u64), String> {
    let emit_start = Instant::now();

    prepare_output_dir(output_dir, options.clean)?;
    let mut out = OutputTree::new(output_dir);

    // The SDT classes are built before any Procedure: a Procedure resolves its member accesses against
    // exactly the SDTs emitted here. An SDT that does not build is absent from the index, so a member of
    // it fails closed in every Procedure that uses it.
    let (sdt_classes, sdt_usings) = build_sdt_classes(manifest, &mut out.errors);
    let sdt_index = build_sdt_index(&sdt_classes, &mut out.errors);
    let call_table = build_call_table(manifest, &mut out.errors);
    let context = ProcedureContext {
        sdt_usings,
        sdt_index,
        call_table,
    };

    // 1. Emit every Procedure. Nothing is written yet: a caller is generated only if every Procedure it
    // calls is generated (csc would fail on a call to a class that is not written), so the outcomes are
    // settled first.
    let mut emissions: Vec<ProcedureEmission> =
        map_chunks(&manifest.procedures, concurrency, |chunk| {
            chunk
                .iter()
                .map(|proc| emit_procedure(proc, &context))
                .collect::<Vec<_>>()
        })
        .into_iter()
        .flatten()
        .collect();
    emissions.sort_by(|a, b| a.name.cmp(&b.name));
    refuse_callers_of_ungenerated(&mut emissions);

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
    write_sdts(&mut out, &sdt_classes, concurrency);

    // 5. Emit Main Unified Project File (.csproj)
    let main_csproj_path = write_main_project(&mut out, options)?;

    findings.errors.append(&mut out.errors);
    let mut emitted_files = out.files;
    emitted_files.sort();
    Ok((
        emitted_files,
        main_csproj_path,
        emit_start.elapsed().as_millis() as u64,
    ))
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
fn build_call_table(
    manifest: &ObjectManifest<'_>,
    errors: &mut Vec<String>,
) -> Arc<CallSignatures> {
    let mut call_table = CallSignatures::new();
    for proc in &manifest.procedures {
        if let Ok(class) = ProcedureClass::from_kb_object(proc) {
            if let Err(e) = call_table.insert(class.call_target()) {
                errors.push(format!("Procedure '{}': {e}", proc.name));
            }
        }
    }
    Arc::new(call_table)
}

fn emit_procedure(
    proc: &crate::catalog::KbObject,
    context: &ProcedureContext,
) -> ProcedureEmission {
    let (stem, class_name, wrapper, emitted) =
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
        };
    ProcedureEmission {
        name: proc.name.clone(),
        class: proc.name.to_ascii_uppercase(),
        stem,
        class_name,
        wrapper,
        emitted,
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
    // The stamp is the generation time, the token the SDK writes into a panel's script URLs.
    let stamp = webform::current_stamp();
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

/// Writes each SDT in parallel; a thread fills its own tree and the trees merge in chunk order.
fn write_sdts(out: &mut OutputTree<'_>, sdt_classes: &[SdtClass], concurrency: usize) {
    let dir = out.dir;
    for tree in map_chunks(sdt_classes, concurrency, |chunk| {
        let mut tree = OutputTree::new(dir);
        for sdt_class in chunk {
            // The SDK's form (`type_Sdt<class>.cs`, one file per class) when the SDT is a shape the
            // SDK evidence covers; otherwise the portable class, one file for the SDT and its levels.
            match crate::csharp::sdt_sdk::sdk_files(sdt_class) {
                Ok(files) => {
                    for (file_name, text) in files {
                        tree.write_generated_file(&file_name, &text);
                    }
                }
                Err(_) => tree.write_generated_file(
                    &format!("{}.cs", sdt_class.class_name()),
                    &sdt_class.emit(),
                ),
            }
        }
        tree
    }) {
        out.merge(tree);
    }
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
