//! Direct compiler invocation: the logged command, the key, the response file, the verdicts, and byte
//! identity with `dotnet build` on a small real project.
//!
//! The log excerpt is SYNTHETIC. It has the shape of MSBuild's `Csc` line at `-v:detailed` (the compiler
//! path with spaces and unquoted, quoted analyzer paths, generated sources last), with paths and the
//! option set shortened.

use gx_engine::csc_direct::{
    Verdict, ambient_import, analyzers_can_be_skipped, capture, classify, compile,
    compiler_and_sdk_identity, declares_import, environment_digest, guarded_key, guarded_key_with,
    key_text, project_sources, render_rsp, sdk_inputs, sdk_root_of, split_command_line,
    template_from_log,
};
use std::ffi::OsString;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};

static SEQUENCE: AtomicU64 = AtomicU64::new(0);

const LOG: &str = r#"Build started preamble
    C:\Program Files\dotnet\sdk\10.0.204\Roslyn\bincore\csc.exe /noconfig /unsafe- /nowarn:1701,1702 /analyzerconfig:"C:\Program Files\dotnet\sdk\10.0.204\Sdks\Microsoft.NET.Sdk\analyzers\x.globalconfig" /analyzer:"C:\Program Files\dotnet\sdk\10.0.204\Sdks\Microsoft.NET.Sdk\analyzers\Microsoft.CodeAnalysis.NetAnalyzers.dll" /reference:C:\Users\u\.nuget\packages\genexus.classes.core\1.30.0\lib\net8.0\GxClasses.dll /out:obj\Release\net8.0\RebuildApp.dll /refout:obj\Release\net8.0\refint\RebuildApp.dll /target:library /debug:portable SdtA.cs SdtB.cs "obj\Release\net8.0\.NETCoreApp,Version=v8.0.AssemblyAttributes.cs" obj\Release\net8.0\RebuildApp.AssemblyInfo.cs /warnaserror+:NU1605,SYSLIB0011
    Compilation request RebuildApp (net8.0)
"#;

fn scratch(label: &str) -> PathBuf {
    scratch_in(&std::env::temp_dir(), label)
}

fn scratch_in(base: &Path, label: &str) -> PathBuf {
    let dir = base.join(format!(
        "gx_csc_direct_{label}_{}_{}",
        std::process::id(),
        SEQUENCE.fetch_add(1, Ordering::SeqCst)
    ));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).unwrap();
    dir
}

/// A precondition this host cannot meet. A local run skips and says why on stderr; CI (`CI` set)
/// fails instead, so the coverage cannot quietly disappear from the pipeline.
fn skip_or_fail(reason: &str) {
    assert!(std::env::var_os("CI").is_none(), "{reason}");
    eprintln!("skipped: {reason}");
}

#[test]
fn executable_replay_requires_unchanged_runtime_bootstrap() {
    let dir = scratch("executable_bootstrap");
    let project = "<Project><PropertyGroup><AssemblyName>Probe</AssemblyName><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>";
    let key = || {
        guarded_key_with(
            &dir,
            project,
            b"assets",
            "compiler",
            None,
            "environment",
            "identity",
        )
    };
    assert!(
        key().is_err(),
        "an executable without SDK bootstrap must fall back"
    );
    let bin = dir.join("bin/Release/net8.0");
    fs::create_dir_all(&bin).unwrap();
    let host = bin.join(format!("Probe{}", std::env::consts::EXE_SUFFIX));
    fs::write(&host, b"host").unwrap();
    fs::write(bin.join("Probe.deps.json"), b"deps").unwrap();
    fs::write(bin.join("Probe.runtimeconfig.json"), b"runtime").unwrap();
    fs::write(bin.join("RuntimeDependency.dll"), b"dependency").unwrap();
    let baseline = key().unwrap();
    fs::write(bin.join("Probe.dll"), b"old compilation").unwrap();
    assert_eq!(
        baseline,
        key().unwrap(),
        "the compiler output is not bootstrap"
    );
    fs::write(bin.join("RuntimeDependency.dll"), b"changed dependency").unwrap();
    assert_ne!(baseline, key().unwrap());
    fs::remove_file(host).unwrap();
    assert!(key().is_err(), "a missing apphost must fall back");
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_quoted_group_keeps_its_spaces_and_the_quotes() {
    assert_eq!(
        split_command_line(r#"/a:"x y" z"#),
        vec![r#"/a:"x y""#.to_string(), "z".to_string()]
    );
}

#[test]
fn the_logged_command_becomes_a_template_with_its_options_and_generated_sources() {
    let template = template_from_log(LOG).expect("the excerpt holds a compiler command");
    assert_eq!(
        template.compiler,
        r"C:\Program Files\dotnet\sdk\10.0.204\Roslyn\bincore\csc.exe"
    );
    assert_eq!(template.options_before[0], "/noconfig");
    assert!(
        template
            .options_before
            .iter()
            .any(|option| option.starts_with("/analyzer:\"C:\\Program Files"))
    );
    assert!(
        template
            .options_before
            .contains(&"/debug:portable".to_string())
    );
    assert_eq!(
        template.generated_sources,
        vec![
            "\"obj\\Release\\net8.0\\.NETCoreApp,Version=v8.0.AssemblyAttributes.cs\"".to_string(),
            "obj\\Release\\net8.0\\RebuildApp.AssemblyInfo.cs".to_string(),
        ]
    );
    assert_eq!(
        template.options_after,
        vec!["/warnaserror+:NU1605,SYSLIB0011".to_string()]
    );
}

#[test]
fn a_project_source_after_a_generated_one_or_an_unknown_argument_refuses_the_template() {
    let after_generated = LOG.replace(
        "obj\\Release\\net8.0\\RebuildApp.AssemblyInfo.cs",
        "obj\\Release\\net8.0\\RebuildApp.AssemblyInfo.cs SdtZ.cs",
    );
    assert!(template_from_log(&after_generated).is_none());
    let unknown = LOG.replace("SdtA.cs", "@extra.rsp SdtA.cs");
    assert!(template_from_log(&unknown).is_none());
    assert!(template_from_log("no compiler command here").is_none());
}

#[test]
fn the_response_file_lists_options_then_project_then_generated_sources() {
    let template = template_from_log(LOG).unwrap();
    let rsp = render_rsp(&template, &["SdtA.cs".to_string(), "SdtB.cs".to_string()]);
    let lines: Vec<&str> = rsp.lines().collect();
    let n = lines.len();
    assert_eq!(lines[n - 1], "/warnaserror+:NU1605,SYSLIB0011");
    assert_eq!(
        lines[n - 2],
        "obj\\Release\\net8.0\\RebuildApp.AssemblyInfo.cs"
    );
    assert_eq!(
        lines[n - 3],
        "\"obj\\Release\\net8.0\\.NETCoreApp,Version=v8.0.AssemblyAttributes.cs\""
    );
    assert_eq!(lines[n - 4], "SdtB.cs");
    assert_eq!(lines[n - 5], "SdtA.cs");
    assert_eq!(lines[0], "/unsafe-");
    assert!(
        !rsp.contains("/noconfig"),
        "csc reports CS2023 for /noconfig in a response file"
    );
}

#[test]
fn project_sources_are_the_compile_items_in_project_order() {
    let csproj = r#"<Project>
  <ItemGroup>
    <Reference Include="X" />
    <Compile Include="A.cs" />
    <Compile Include="B.cs"></Compile>
  </ItemGroup>
</Project>"#;
    assert_eq!(project_sources(csproj), vec!["A.cs", "B.cs"]);
}

#[test]
fn the_key_ignores_compile_items_but_not_properties_packages_or_the_compiler() {
    let base = "<Project>\n<PropertyGroup><Nullable>disable</Nullable></PropertyGroup>\n<ItemGroup>\n<Compile Include=\"A.cs\" />\n</ItemGroup>\n</Project>\n";
    let with_new_source = base.replace(
        "<Compile Include=\"A.cs\" />",
        "<Compile Include=\"A.cs\" />\n<Compile Include=\"B.cs\" />",
    );
    let with_property = base.replace("disable", "enable");
    let assets = b"{}";
    assert_eq!(
        key_text(base, assets, "csc"),
        key_text(&with_new_source, assets, "csc"),
        "a Compile item must not change the key"
    );
    assert_ne!(
        key_text(base, assets, "csc"),
        key_text(&with_property, assets, "csc")
    );
    assert_ne!(
        key_text(base, assets, "csc"),
        key_text(base, b"{ }", "csc"),
        "a package change must change the key"
    );
    assert_ne!(
        key_text(base, assets, "csc"),
        key_text(base, assets, "other")
    );
}

#[test]
fn a_successful_run_is_built_and_a_failed_run_is_judged_by_its_diagnostics() {
    assert_eq!(
        classify(true, "warning CS8019: unused"),
        Verdict::Built {
            warnings: vec!["warning CS8019: unused".to_string()]
        }
    );
    assert_eq!(
        classify(false, "A.cs(1,1): error CS1002: ; expected"),
        Verdict::Diagnostics {
            errors: vec!["A.cs(1,1): error CS1002: ; expected".to_string()],
            warnings: vec![],
        }
    );
    assert!(
        matches!(
            classify(false, "the compiler server crashed"),
            Verdict::Unavailable(_)
        ),
        "a failure with no diagnostics must fall back, not pass"
    );
}

#[test]
fn a_project_without_a_kept_command_is_unavailable() {
    let dir = scratch("missing");
    let csproj = dir.join("RebuildApp.csproj");
    fs::write(&csproj, "<Project />").unwrap();
    assert!(matches!(compile(&csproj), Verdict::Unavailable(_)));
    let _ = fs::remove_dir_all(&dir);
}

fn dotnet_available() -> bool {
    Command::new("dotnet")
        .arg("--version")
        .output()
        .map(|output| output.status.success())
        .unwrap_or(false)
}

fn write_project(dir: &Path, sources: &[&str], extra_property: &str) {
    let items: String = sources
        .iter()
        .map(|source| format!("    <Compile Include=\"{source}\" />\n"))
        .collect();
    let text = format!(
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <AssemblyName>RebuildApp</AssemblyName>\n    <TargetFramework>net8.0</TargetFramework>\n    <OutputType>Library</OutputType>\n    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n    {extra_property}\n  </PropertyGroup>\n  <ItemGroup>\n{items}  </ItemGroup>\n</Project>\n"
    );
    fs::write(dir.join("RebuildApp.csproj"), text).unwrap();
}

fn dotnet_build_detailed(dir: &Path) -> String {
    let output = Command::new("dotnet")
        .current_dir(dir)
        .args([
            "build",
            "RebuildApp.csproj",
            "-c",
            "Release",
            "--nologo",
            "-v:detailed",
        ])
        .output()
        .expect("run dotnet build");
    assert!(
        output.status.success(),
        "dotnet build failed: {}",
        String::from_utf8_lossy(&output.stdout)
    );
    String::from_utf8_lossy(&output.stdout).into_owned()
}

/// Removes what a build produces and keeps what the direct path reads: the generated MSBuild inputs,
/// the restore output and the kept command.
fn remove_outputs(dir: &Path) {
    let _ = fs::remove_dir_all(dir.join("bin"));
    let _ = fs::remove_file(dir.join("obj/Release/net8.0/RebuildApp.dll"));
    let _ = fs::remove_file(dir.join("obj/Release/net8.0/RebuildApp.pdb"));
    let _ = fs::remove_dir_all(dir.join("obj/Release/net8.0/refint"));
}

fn bin_dll(dir: &Path) -> Vec<u8> {
    fs::read(dir.join("bin/Release/net8.0/RebuildApp.dll")).expect("the published assembly")
}

#[test]
fn a_kept_command_builds_the_assembly_dotnet_build_builds_and_follows_compile_items() {
    if !dotnet_available() {
        eprintln!("skipped: the dotnet CLI is not available");
        return;
    }
    let dir = scratch("identity");
    let csproj = dir.join("RebuildApp.csproj");
    write_project(&dir, &["A.cs", "B.cs"], "");
    fs::write(
        dir.join("A.cs"),
        "namespace N { public class A { public int V() => 1; } }\n",
    )
    .unwrap();
    fs::write(
        dir.join("B.cs"),
        "namespace N { public class B { public int V() => 2; } }\n",
    )
    .unwrap();

    // dotnet build captures the command and produces the baseline assembly.
    let log = dotnet_build_detailed(&dir);
    assert!(
        capture(&csproj, &log).unwrap(),
        "the detailed log must hold the compiler command"
    );
    let baseline = bin_dll(&dir);

    // The direct compile, with the outputs removed first, reproduces the same bytes.
    remove_outputs(&dir);
    assert!(matches!(compile(&csproj), Verdict::Built { .. }));
    assert_eq!(
        bin_dll(&dir),
        baseline,
        "the direct compile must match dotnet build"
    );

    // A new Compile item is compiled: the direct result matches dotnet build of the new project.
    fs::write(
        dir.join("C.cs"),
        "namespace N { public class C { public int V() => 3; } }\n",
    )
    .unwrap();
    write_project(&dir, &["A.cs", "B.cs", "C.cs"], "");
    dotnet_build_detailed(&dir);
    let with_c = bin_dll(&dir);
    assert_ne!(with_c, baseline, "the new source must change the assembly");
    remove_outputs(&dir);
    assert!(matches!(compile(&csproj), Verdict::Built { .. }));
    assert_eq!(bin_dll(&dir), with_c, "a new Compile item must be compiled");

    // A property change invalidates the kept command.
    write_project(
        &dir,
        &["A.cs", "B.cs", "C.cs"],
        "<Nullable>enable</Nullable>",
    );
    assert!(
        matches!(compile(&csproj), Verdict::Unavailable(reason) if reason.contains("changed")),
        "a changed property must fall back"
    );
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn an_unchanged_project_is_not_compiled_again_and_any_input_or_output_change_compiles_it() {
    if !dotnet_available() {
        eprintln!("skipped: the dotnet CLI is not available");
        return;
    }
    let dir = scratch("uptodate");
    let csproj = dir.join("RebuildApp.csproj");
    write_project(&dir, &["A.cs"], "");
    let a = "namespace N { public class A { public int V() => 1; } }\n";
    fs::write(dir.join("A.cs"), a).unwrap();
    assert!(capture(&csproj, &dotnet_build_detailed(&dir)).unwrap());
    let dll = dir.join("bin/Release/net8.0/RebuildApp.dll");

    assert!(matches!(compile(&csproj), Verdict::Built { .. }));
    let built = fs::metadata(&dll).unwrap().modified().unwrap();
    // Nothing changed: the published assembly stands, untouched.
    assert!(matches!(compile(&csproj), Verdict::UpToDate { .. }));
    assert_eq!(fs::metadata(&dll).unwrap().modified().unwrap(), built);
    // The emitter rewrites every source each run: a rewrite with the same text is no change.
    fs::write(dir.join("A.cs"), a).unwrap();
    assert!(matches!(compile(&csproj), Verdict::UpToDate { .. }));

    // A changed source compiles, and its assembly differs.
    let before = bin_dll(&dir);
    fs::write(
        dir.join("A.cs"),
        "namespace N { public class A { public int V() => 7; } }\n",
    )
    .unwrap();
    assert!(matches!(compile(&csproj), Verdict::Built { .. }));
    assert_ne!(bin_dll(&dir), before);
    assert!(matches!(compile(&csproj), Verdict::UpToDate { .. }));

    // A removed or altered output is rebuilt, never trusted.
    fs::remove_file(&dll).unwrap();
    assert!(matches!(compile(&csproj), Verdict::Built { .. }));
    assert!(dll.is_file());
    fs::write(&dll, b"not an assembly").unwrap();
    assert!(matches!(compile(&csproj), Verdict::Built { .. }));
    assert_ne!(fs::read(&dll).unwrap(), b"not an assembly");

    // A compile error leaves nothing to reuse: fixing the source back compiles again.
    fs::write(
        dir.join("A.cs"),
        "namespace N { public class A { broken } }\n",
    )
    .unwrap();
    assert!(matches!(compile(&csproj), Verdict::Diagnostics { .. }));
    fs::write(
        dir.join("A.cs"),
        "namespace N { public class A { public int V() => 7; } }\n",
    )
    .unwrap();
    assert!(matches!(compile(&csproj), Verdict::Built { .. }));
    let _ = fs::remove_dir_all(&dir);
}

/// `path` relative to the current directory: `..` up to the shared root, then the rest of `path`.
/// `None` when the two share no root (another drive on Windows): no relative path exists then.
fn relative_to_current_dir(path: &Path) -> Option<PathBuf> {
    let here = std::env::current_dir().unwrap();
    let (here, there): (Vec<_>, Vec<_>) =
        (here.components().collect(), path.components().collect());
    let shared = here.iter().zip(&there).take_while(|(a, b)| a == b).count();
    if shared == 0 {
        return None;
    }
    let mut relative = PathBuf::new();
    for _ in shared..here.len() {
        relative.push("..");
    }
    for part in &there[shared..] {
        relative.push(part.as_os_str());
    }
    Some(relative)
}

/// A folder under which a project can be named by a relative path from the current directory and still
/// be served by the direct path: it shares a root with the current directory (`temp_dir()` can be on
/// another drive) and has no ambient MSBuild or analyzer file above it (the crate sits under the
/// repository's `Directory.Build.props` and `.editorconfig`, which the direct path refuses). The temp
/// folder first, then the ancestors of the current directory, nearest first.
fn relative_scratch_base() -> Option<PathBuf> {
    let here = std::env::current_dir().unwrap();
    std::iter::once(std::env::temp_dir())
        .chain(here.ancestors().skip(1).map(Path::to_path_buf))
        .find(|base| relative_to_current_dir(base).is_some() && ambient_import(base).is_none())
}

#[test]
fn a_kept_command_builds_a_project_named_by_a_relative_path() {
    // The compiler runs in the project folder; `rebuild-all --output out` names the project relatively.
    if !dotnet_available() {
        eprintln!("skipped: the dotnet CLI is not available");
        return;
    }
    let Some(base) = relative_scratch_base() else {
        skip_or_fail(
            "no folder shares a root with the current directory and is free of ambient MSBuild or analyzer files",
        );
        return;
    };
    let dir = scratch_in(&base, "relative");
    let relative = relative_to_current_dir(&dir.join("RebuildApp.csproj"))
        .expect("the scratch base shares a root with the current directory");
    write_project(&dir, &["A.cs"], "");
    fs::write(
        dir.join("A.cs"),
        "namespace N { public class A { public int V() => 1; } }
",
    )
    .unwrap();
    let log = dotnet_build_detailed(&dir);
    assert!(capture(&dir.join("RebuildApp.csproj"), &log).unwrap());
    let baseline = bin_dll(&dir);
    remove_outputs(&dir);
    assert!(relative.is_relative());
    let verdict = compile(&relative);
    assert!(
        matches!(verdict, Verdict::Built { .. }),
        "a relative project path must compile: {verdict:?}"
    );
    assert_eq!(bin_dll(&dir), baseline);
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn executable_replay_recompiles_and_runs_with_the_sdk_bootstrap() {
    if !dotnet_available() {
        return;
    }
    let dir = scratch("executable_replay");
    let csproj = dir.join("RebuildApp.csproj");
    write_project(&dir, &["Program.cs"], "");
    fs::write(
        &csproj,
        fs::read_to_string(&csproj).unwrap().replace(
            "<OutputType>Library</OutputType>",
            "<OutputType>Exe</OutputType>",
        ),
    )
    .unwrap();
    fs::write(
        dir.join("Program.cs"),
        "class Program { static void Main() { System.Console.WriteLine(1); } }",
    )
    .unwrap();
    let log = dotnet_build_detailed(&dir);
    assert!(capture(&csproj, &log).unwrap());
    let original = bin_dll(&dir);
    fs::write(
        dir.join("Program.cs"),
        "class Program { static void Main() { System.Console.WriteLine(2); } }",
    )
    .unwrap();
    fs::remove_file(dir.join("bin/Release/net8.0/RebuildApp.dll")).unwrap();
    assert!(matches!(compile(&csproj), Verdict::Built { .. }));
    assert_ne!(original, bin_dll(&dir));
    let executable = dir
        .join("bin/Release/net8.0")
        .join(format!("RebuildApp{}", std::env::consts::EXE_SUFFIX));
    let output = Command::new(executable).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "2");
    let runtime = dir.join("bin/Release/net8.0/RebuildApp.runtimeconfig.json");
    fs::write(runtime, "{}").unwrap();
    assert!(matches!(compile(&csproj), Verdict::Unavailable(_)));
    fs::remove_dir_all(dir).unwrap();
}

const PROPS_A: &str = "<Project>\n  <PropertyGroup>\n    <DefineConstants>REVISION_A</DefineConstants>\n  </PropertyGroup>\n</Project>\n";
const PROPS_B: &str = "<Project>\n  <PropertyGroup>\n    <DefineConstants>REVISION_B</DefineConstants>\n  </PropertyGroup>\n</Project>\n";

/// The repro in `C:/tmp/gxe-cache-review/csc_import_repro.rs`: a kept command, then an ambient
/// `Directory.Build.props` changes an evaluated property while the csproj, the sources and the assets stay
/// the same. The direct path must refuse, not build the stale options.
#[test]
fn an_import_added_after_capture_refuses_the_direct_path() {
    if !dotnet_available() {
        eprintln!("skipped: the dotnet CLI is not available");
        return;
    }
    let dir = scratch("import_after");
    let csproj = dir.join("RebuildApp.csproj");
    write_project(&dir, &["A.cs"], "");
    fs::write(
        dir.join("A.cs"),
        "namespace N { public class A { public int V() => 1; } }\n",
    )
    .unwrap();
    let log = dotnet_build_detailed(&dir);
    assert!(capture(&csproj, &log).unwrap());
    remove_outputs(&dir);
    assert!(matches!(compile(&csproj), Verdict::Built { .. }));

    fs::write(dir.join("Directory.Build.props"), PROPS_B).unwrap();
    remove_outputs(&dir);
    let verdict = compile(&csproj);
    assert!(
        matches!(&verdict, Verdict::Unavailable(reason) if reason.contains("Directory.Build.props")),
        "an ambient import must refuse the direct path"
    );
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn an_import_present_at_capture_keeps_no_command() {
    if !dotnet_available() {
        eprintln!("skipped: the dotnet CLI is not available");
        return;
    }
    let dir = scratch("import_before");
    let csproj = dir.join("RebuildApp.csproj");
    fs::write(dir.join("Directory.Build.props"), PROPS_A).unwrap();
    write_project(&dir, &["A.cs"], "");
    fs::write(
        dir.join("A.cs"),
        "namespace N { public class A { public int V() => 1; } }\n",
    )
    .unwrap();
    let log = dotnet_build_detailed(&dir);
    assert!(
        !capture(&csproj, &log).unwrap(),
        "a project with an ambient import keeps no command"
    );
    assert!(matches!(compile(&csproj), Verdict::Unavailable(_)));
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn an_import_in_a_parent_directory_refuses_the_direct_path() {
    let root = scratch("parent_import");
    let project = root.join("generated");
    fs::create_dir_all(&project).unwrap();
    fs::write(root.join("Directory.Build.props"), PROPS_A).unwrap();
    assert_eq!(
        ambient_import(&project),
        Some(root.join("Directory.Build.props"))
    );
    let (_, compiler) = fake_sdk("parent_import_compiler");
    let reason = guarded_key(&project, "<Project />\n", b"{}", &compiler)
        .expect_err("an ambient import in a parent directory refuses");
    assert!(reason.contains("Directory.Build.props"), "{reason}");
    let _ = fs::remove_dir_all(&root);
}

#[test]
fn the_key_names_restore_outputs_and_refuses_an_explicit_import() {
    let dir = scratch("guarded_key");
    let csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n";
    let imported =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <Import Project=\"Extra.props\" />\n</Project>\n";
    fs::create_dir_all(dir.join("obj")).unwrap();
    let restore = dir.join("obj/RebuildApp.csproj.nuget.g.props");
    fs::write(&restore, "<Project>A</Project>").unwrap();
    // The compiler must sit in an SDK's Roslyn/bincore folder: its identity and the SDK inputs are in the key.
    let (_, compiler) = fake_sdk("guarded_key_compiler");
    let first = guarded_key(&dir, csproj, b"{}", &compiler).expect("no ambient import");
    fs::write(&restore, "<Project>B</Project>").unwrap();
    let second = guarded_key(&dir, csproj, b"{}", &compiler).expect("no ambient import");
    assert_ne!(first, second, "a changed restore output changes the key");

    assert!(!declares_import(csproj));
    assert!(declares_import(imported));
    assert!(guarded_key(&dir, imported, b"{}", &compiler).is_err());
    let _ = fs::remove_dir_all(&dir);
}

#[test]
fn a_file_under_the_user_level_msbuild_folder_refuses_the_direct_path() {
    let project = scratch("user_ext_project");
    let user = scratch("user_ext_folder");
    let csproj = "<Project />\n";
    assert!(
        guarded_key_with(
            &project,
            csproj,
            b"{}",
            "csc",
            None,
            "env",
            "compiler-identity
"
        )
        .is_ok(),
        "no user-level folder is no refusal"
    );
    assert!(
        guarded_key_with(
            &project,
            csproj,
            b"{}",
            "csc",
            Some(&user.join("absent")),
            "env",
            "compiler-identity
"
        )
        .is_ok(),
        "an absent folder is no refusal"
    );
    assert!(
        guarded_key_with(
            &project,
            csproj,
            b"{}",
            "csc",
            Some(&user),
            "env",
            "compiler-identity
"
        )
        .is_ok(),
        "an empty folder is no refusal"
    );
    let nested = user.join("Imports");
    fs::create_dir_all(&nested).unwrap();
    fs::write(nested.join("Microsoft.Common.targets"), "<Project />").unwrap();
    let reason = guarded_key_with(
        &project,
        csproj,
        b"{}",
        "csc",
        Some(&user),
        "env",
        "compiler-identity
",
    )
    .expect_err("a file under the user-level folder refuses the direct path");
    assert!(reason.contains("user-level"), "names the folder: {reason}");
}

#[test]
fn analyzer_configuration_under_the_project_or_above_it_refuses_the_direct_path() {
    let project = scratch("editorconfig_project");
    let csproj = "<Project />\n";
    assert!(
        guarded_key_with(
            &project,
            csproj,
            b"{}",
            "csc",
            None,
            "env",
            "compiler-identity
"
        )
        .is_ok()
    );
    let nested = project.join("web").join("general");
    fs::create_dir_all(&nested).unwrap();
    fs::write(nested.join(".editorconfig"), "root = true\n").unwrap();
    let reason = guarded_key_with(
        &project,
        csproj,
        b"{}",
        "csc",
        None,
        "env",
        "compiler-identity
",
    )
    .expect_err("analyzer configuration below the project refuses");
    assert!(reason.contains(".editorconfig"), "names the file: {reason}");

    // Above the project: a scratch root holds the configuration and a build directory below it.
    let root = scratch("editorconfig_ancestor");
    fs::write(root.join(".editorconfig"), "root = true\n").unwrap();
    let build = root.join("build");
    fs::create_dir_all(&build).unwrap();
    assert!(
        guarded_key_with(
            &build,
            csproj,
            b"{}",
            "csc",
            None,
            "env",
            "compiler-identity
"
        )
        .is_err(),
        "analyzer configuration in an ancestor refuses the direct path"
    );
}

#[test]
fn the_environment_is_part_of_the_key_and_keeps_no_values() {
    let pairs = |value: &str| vec![(OsString::from("Configuration"), OsString::from(value))];
    assert_eq!(
        environment_digest(pairs("Release")),
        environment_digest(pairs("Release"))
    );
    assert_ne!(
        environment_digest(pairs("Release")),
        environment_digest(pairs("Debug"))
    );
    let forward = vec![
        (OsString::from("A"), OsString::from("1")),
        (OsString::from("B"), OsString::from("2")),
    ];
    let backward = vec![
        (OsString::from("B"), OsString::from("2")),
        (OsString::from("A"), OsString::from("1")),
    ];
    assert_eq!(environment_digest(forward), environment_digest(backward));

    let project = scratch("env_key");
    let csproj = "<Project />\n";
    let first = guarded_key_with(
        &project,
        csproj,
        b"{}",
        "csc",
        None,
        "aaaaaaaa",
        "compiler-identity
",
    )
    .unwrap();
    let second = guarded_key_with(
        &project,
        csproj,
        b"{}",
        "csc",
        None,
        "bbbbbbbb",
        "compiler-identity
",
    )
    .unwrap();
    assert_ne!(first, second, "a different environment changes the key");
    assert!(
        first.contains("environment=aaaaaaaa\n"),
        "the key carries the digest it was given: {first}"
    );
    let digest = environment_digest(pairs("Release"));
    assert_eq!(digest.len(), 16, "the digest is a fixed-width hex value");
    assert!(digest.chars().all(|c| c.is_ascii_hexdigit()));
}

#[test]
fn a_changed_compiler_file_changes_the_key_even_when_its_size_and_time_are_kept() {
    let dir = scratch("compiler_content");
    let bincore = dir.join("10.0.1").join("Roslyn").join("bincore");
    fs::create_dir_all(&bincore).unwrap();
    let compiler = bincore.join("csc.exe");
    let library = bincore.join("csc.dll");
    fs::write(&compiler, b"entry-aaaa").unwrap();
    fs::write(&library, b"library-aaaa").unwrap();
    let project = dir.join("project");
    fs::create_dir_all(&project).unwrap();
    let csproj = "<Project />\n";
    let compiler_path = compiler.to_string_lossy().into_owned();
    let before = guarded_key(&project, csproj, b"{}", &compiler_path).unwrap();
    for file in [compiler.clone(), library.clone()] {
        let original = fs::read(&file).unwrap();
        let modified = fs::metadata(&file).unwrap().modified().unwrap();
        // Same length, same modification time, different content: only the content digest can see it.
        let mut changed = original.clone();
        changed[0] ^= 0x01;
        fs::write(&file, &changed).unwrap();
        fs::OpenOptions::new()
            .write(true)
            .open(&file)
            .unwrap()
            .set_modified(modified)
            .unwrap();
        let after = guarded_key(&project, csproj, b"{}", &compiler_path).unwrap();
        assert_ne!(
            before,
            after,
            "{} changed, so the key must change",
            file.display()
        );
        fs::write(&file, &original).unwrap();
        fs::OpenOptions::new()
            .write(true)
            .open(&file)
            .unwrap()
            .set_modified(modified)
            .unwrap();
        assert_eq!(
            guarded_key(&project, csproj, b"{}", &compiler_path).unwrap(),
            before,
            "the restored {} restores the key",
            file.display()
        );
    }
}

#[test]
fn a_file_added_beside_the_compiler_changes_the_key() {
    let dir = scratch("compiler_sibling");
    let bincore = dir.join("10.0.1").join("Roslyn").join("bincore");
    fs::create_dir_all(&bincore).unwrap();
    let compiler = bincore.join("csc.exe");
    fs::write(&compiler, b"entry").unwrap();
    let project = dir.join("project");
    fs::create_dir_all(&project).unwrap();
    let compiler_path = compiler.to_string_lossy().into_owned();
    let before = guarded_key(&project, "<Project />\n", b"{}", &compiler_path).unwrap();
    fs::write(bincore.join("Roslyn.Extra.dll"), b"extra").unwrap();
    let after = guarded_key(&project, "<Project />\n", b"{}", &compiler_path).unwrap();
    assert_ne!(
        before, after,
        "a new file in the compiler directory changes the key"
    );
}

#[test]
fn a_compiler_that_cannot_be_read_refuses_the_direct_path() {
    let dir = scratch("compiler_missing");
    let project = dir.join("project");
    fs::create_dir_all(&project).unwrap();
    let missing = dir.join("csc.exe").to_string_lossy().into_owned();
    let reason = guarded_key(&project, "<Project />\n", b"{}", &missing)
        .expect_err("a missing compiler refuses the direct path");
    assert!(reason.contains("cannot read the compiler"), "{reason}");
}

/// A fake .NET SDK folder: `<sdk>/Roslyn/bincore/csc.exe`, two MSBuild inputs (one under `Roslyn`, one in a nested
/// SDK folder) and a file that is not an input. Returns the SDK folder and the compiler path.
fn fake_sdk(label: &str) -> (PathBuf, String) {
    let sdk = scratch(label).join("10.0.1");
    let bincore = sdk.join("Roslyn").join("bincore");
    fs::create_dir_all(&bincore).unwrap();
    let compiler = bincore.join("csc.exe");
    fs::write(&compiler, b"compiler").unwrap();
    fs::write(
        sdk.join("Roslyn").join("Microsoft.Managed.Core.targets"),
        b"<Project>A</Project>",
    )
    .unwrap();
    let nested = sdk.join("Sdks").join("Microsoft.NET.Sdk").join("targets");
    fs::create_dir_all(&nested).unwrap();
    fs::write(
        nested.join("Microsoft.NET.Sdk.props"),
        b"<Project>A</Project>",
    )
    .unwrap();
    fs::write(sdk.join("notes.txt"), b"not an input").unwrap();
    (sdk, compiler.to_string_lossy().into_owned())
}

#[test]
fn the_sdk_inputs_are_the_props_and_targets_under_the_sdk_folder_and_nothing_else() {
    let (sdk, _) = fake_sdk("sdk_inputs_listing");
    let text = sdk_inputs(&sdk).unwrap();
    assert!(text.starts_with("sdk-inputs=2\n"), "{text}");
    assert!(
        text.contains("sdk-input=Roslyn/Microsoft.Managed.Core.targets:"),
        "{text}"
    );
    assert!(
        text.contains("sdk-input=Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.props:"),
        "{text}"
    );
    assert!(
        !text.contains("notes.txt") && !text.contains("csc.exe"),
        "{text}"
    );
}

#[test]
fn a_props_file_that_changes_content_at_the_same_size_and_time_changes_the_identity() {
    // The fingerprint is a content identity: a rewrite that keeps the size and the modification time, which a
    // size-and-time key would keep, still changes it.
    let (sdk, compiler) = fake_sdk("sdk_same_size");
    let props = sdk
        .join("Sdks")
        .join("Microsoft.NET.Sdk")
        .join("targets")
        .join("Microsoft.NET.Sdk.props");
    let before = compiler_and_sdk_identity(&compiler).unwrap();
    let time = fs::metadata(&props).unwrap().modified().unwrap();
    fs::write(&props, b"<Project>B</Project>").unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&props)
        .unwrap()
        .set_modified(time)
        .unwrap();
    assert_eq!(
        fs::metadata(&props).unwrap().len(),
        b"<Project>A</Project>".len() as u64
    );
    let after = compiler_and_sdk_identity(&compiler).unwrap();
    assert_ne!(before, after, "the SDK props changed content");
}

#[test]
fn an_input_added_as_a_targets_file_or_in_a_subfolder_changes_the_identity() {
    let (sdk, compiler) = fake_sdk("sdk_added");
    let before = compiler_and_sdk_identity(&compiler).unwrap();
    fs::write(sdk.join("Roslyn").join("Extra.targets"), b"<Project/>").unwrap();
    let with_targets = compiler_and_sdk_identity(&compiler).unwrap();
    assert_ne!(
        before, with_targets,
        "a new .targets file changes the identity"
    );
    fs::create_dir_all(sdk.join("Microsoft").join("Deep")).unwrap();
    fs::write(
        sdk.join("Microsoft").join("Deep").join("Deep.props"),
        b"<Project/>",
    )
    .unwrap();
    let deep = compiler_and_sdk_identity(&compiler).unwrap();
    assert_ne!(
        with_targets, deep,
        "a .props file in a subfolder changes the identity"
    );
    assert!(
        deep.contains("sdk-input=Microsoft/Deep/Deep.props:"),
        "{deep}"
    );
}

#[cfg(windows)]
#[test]
fn a_link_in_the_sdk_folder_refuses_the_direct_path() {
    let (sdk, compiler) = fake_sdk("sdk_link");
    let target = sdk.join("Roslyn").join("Microsoft.Managed.Core.targets");
    let link = sdk.join("Linked.targets");
    // Windows refuses a symlink without Developer Mode or the symlink privilege
    // (ERROR_PRIVILEGE_NOT_HELD). CI has it, so there the test still runs; any other error fails.
    const ERROR_PRIVILEGE_NOT_HELD: i32 = 1314;
    match std::os::windows::fs::symlink_file(&target, &link) {
        Ok(()) => {}
        Err(error) if error.raw_os_error() == Some(ERROR_PRIVILEGE_NOT_HELD) => {
            skip_or_fail(&format!(
                "creating a symlink needs Developer Mode or the symlink privilege: {error}"
            ));
            return;
        }
        Err(error) => panic!("creating the symlink failed: {error}"),
    }
    let reason = compiler_and_sdk_identity(&compiler).expect_err("a link is not read through");
    assert!(reason.contains("cannot read the SDK inputs"), "{reason}");
    assert!(reason.contains("Linked.targets"), "{reason}");
}

#[cfg(windows)]
#[test]
fn only_a_roslyn_bincore_compiler_names_an_sdk_folder() {
    assert_eq!(
        sdk_root_of(r"C:\Program Files\dotnet\sdk\10.0.204\Roslyn\bincore\csc.exe"),
        Some(PathBuf::from(r"C:\Program Files\dotnet\sdk\10.0.204"))
    );
    assert_eq!(sdk_root_of(r"C:\tools\csc.exe"), None);
    assert_eq!(
        sdk_root_of(r"C:\sdk\10.0.204\Roslyn\csc.exe"),
        None,
        "the compiler must be in bincore"
    );
    assert_eq!(
        sdk_root_of(r"C:\sdk\10.0.204\Other\bincore\csc.exe"),
        None,
        "the folder above bincore must be Roslyn"
    );
}

#[test]
fn a_compiler_outside_an_sdk_roslyn_bincore_folder_refuses_the_direct_path() {
    let dir = scratch("sdk_layout");
    let project = dir.join("project");
    fs::create_dir_all(&project).unwrap();
    let tools = dir.join("tools");
    fs::create_dir_all(&tools).unwrap();
    let compiler = tools.join("csc.exe");
    fs::write(&compiler, b"compiler").unwrap();
    let reason = guarded_key(
        &project,
        "<Project />\n",
        b"{}",
        &compiler.to_string_lossy(),
    )
    .expect_err("a compiler outside an SDK's Roslyn/bincore folder refuses the direct path");
    assert!(reason.contains("Roslyn/bincore"), "{reason}");
}

#[test]
fn the_sdk_inputs_are_part_of_the_key_the_direct_path_compares() {
    let (sdk, compiler) = fake_sdk("sdk_key");
    let project = scratch("sdk_key_project");
    let before = guarded_key(&project, "<Project />\n", b"{}", &compiler).unwrap();
    assert!(before.contains("sdk-inputs=2\n"), "{before}");
    fs::write(
        sdk.join("Roslyn").join("Microsoft.Managed.Core.targets"),
        b"<Project>changed</Project>",
    )
    .unwrap();
    let after = guarded_key(&project, "<Project />\n", b"{}", &compiler).unwrap();
    assert_ne!(before, after, "a changed SDK target changes the key");
}

#[test]
fn compiler_diagnostics_keep_each_message_once_without_node_or_project_decoration() {
    let (errors, warnings) = gx_engine::csc_direct::compiler_diagnostics(
        "1:7>A.cs(1,1): warning CS8981: x [C:/p/RebuildApp.csproj]\n  A.cs(1,1): warning CS8981: x [C:/p/RebuildApp.csproj]\n  A.cs(1,1): warning CS8981: x\n",
    );
    assert!(errors.is_empty());
    assert_eq!(warnings, vec!["A.cs(1,1): warning CS8981: x".to_string()]);
}

#[test]
fn compiler_diagnostics_keep_order_and_collect_errors_from_msbuild_and_csc() {
    let (errors, warnings) = gx_engine::csc_direct::compiler_diagnostics(
        "B.cs(2,2): warning CS0168: y\nP.csproj : error NETSDK1004: Assets file not found.\nB.cs(3,3): error CS1002: ; expected [C:/p/RebuildApp.csproj]\nBuild succeeded.\n    2 Warning(s)\n",
    );
    assert_eq!(warnings, vec!["B.cs(2,2): warning CS0168: y".to_string()]);
    assert_eq!(
        errors,
        vec![
            "P.csproj : error NETSDK1004: Assets file not found.".to_string(),
            "B.cs(3,3): error CS1002: ; expected".to_string(),
        ]
    );
}

#[test]
fn a_line_that_only_looks_like_a_node_prefix_is_kept() {
    let (_, warnings) =
        gx_engine::csc_direct::compiler_diagnostics("x1:y>A.cs(1,1): warning CS8981: x\n");
    assert_eq!(
        warnings,
        vec!["x1:y>A.cs(1,1): warning CS8981: x".to_string()]
    );
}

/// Analyzers only add diagnostics (source generators still run with `/skipanalyzers+`), so the direct
/// path skips them unless an option could turn an analyzer diagnostic into an error.
#[test]
fn analyzers_are_skipped_only_when_none_of_their_diagnostics_can_fail_the_build() {
    let options = |list: &[&str]| list.iter().map(|o| o.to_string()).collect::<Vec<_>>();
    let generated = options(&[
        "/warn:8",
        "/nowarn:1701,1702,8002",
        "/warnaserror+:NU1605,SYSLIB0011",
        r#"/analyzer:"C:/sdk/analyzers/Microsoft.CodeAnalysis.NetAnalyzers.dll""#,
    ]);
    assert!(analyzers_can_be_skipped(&generated));
    assert!(analyzers_can_be_skipped(&options(&["/warnaserror-"])));
    for refused in [
        "/warnaserror",
        "/warnaserror+",
        "/warnaserror+:CA2200",
        "/warnaserror:NU1605,IDE0005",
        "/ruleset:rules.ruleset",
        "/skipanalyzers-",
    ] {
        let mut list = generated.clone();
        list.push(refused.to_string());
        assert!(!analyzers_can_be_skipped(&list), "{refused}");
    }
}
