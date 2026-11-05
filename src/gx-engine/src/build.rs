//! Fresh native builds with generation-isolated outputs. Content-addressed executable cache.
use crate::model::Project;
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
    process::{Child, Command, ExitStatus, Stdio},
    sync::{
        OnceLock,
        atomic::{AtomicU64, Ordering},
    },
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

#[derive(Debug)]
pub struct BuildArtifact {
    pub executable: PathBuf,
    pub source: PathBuf,
    pub manifest: PathBuf,
}

#[derive(Serialize)]
struct Manifest<'a> {
    format_version: u32,
    engine_version: &'a str,
    backend: &'a str,
    compiler: &'a str,
    compiler_args: Vec<&'a str>,
    entry: &'a str,
    procedures: usize,
    source: &'a str,
    executable: &'a str,
    project: &'a str,
    cache_hit: bool,
}

fn compute_build_cache_key(
    project_json: &str,
    source: &str,
    compiler: &str,
    args: &[&str],
    engine_version: &str,
) -> String {
    let mut hasher = Sha256::new();
    hasher.update(engine_version.as_bytes());
    hasher.update(b"\0");
    hasher.update(compiler.as_bytes());
    hasher.update(b"\0");
    for arg in args {
        hasher.update(arg.as_bytes());
        hasher.update(b"\0");
    }
    hasher.update(source.as_bytes());
    hasher.update(b"\0");
    hasher.update(project_json.as_bytes());
    let digest = hasher.finalize();
    let mut hex = String::with_capacity(64);
    for byte in digest {
        hex.push(char::from(b"0123456789abcdef"[usize::from(byte >> 4)]));
        hex.push(char::from(b"0123456789abcdef"[usize::from(byte & 0x0f)]));
    }
    hex
}

struct BuildCache {
    dir: PathBuf,
}

impl BuildCache {
    fn new(output: &Path, key: &str) -> Self {
        Self {
            dir: output.join(".cache").join(key),
        }
    }

    fn try_restore(&self, staging: &Path, exe_name: &str) -> bool {
        let cached_exe = self.dir.join(exe_name);
        let cached_source = self.dir.join("program.rs");
        let cached_project = self.dir.join("project.json");

        if cached_exe.is_file()
            && cached_source.is_file()
            && cached_project.is_file()
            && fs::metadata(&cached_exe)
                .map(|m| m.len() > 0)
                .unwrap_or(false)
        {
            fs::copy(&cached_exe, staging.join(exe_name)).is_ok()
                && fs::copy(&cached_source, staging.join("program.rs")).is_ok()
                && fs::copy(&cached_project, staging.join("project.json")).is_ok()
        } else {
            false
        }
    }

    fn store(&self, staging: &Path, exe_name: &str) {
        let parent = match self.dir.parent() {
            Some(p) => p,
            None => return,
        };
        if fs::create_dir_all(parent).is_err() {
            return;
        }
        let tmp_name = format!(
            ".tmp-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        );
        let tmp_dir = parent.join(tmp_name);
        if fs::create_dir_all(&tmp_dir).is_err() {
            return;
        }
        let copy_ok = fs::copy(staging.join(exe_name), tmp_dir.join(exe_name)).is_ok()
            && fs::copy(staging.join("program.rs"), tmp_dir.join("program.rs")).is_ok()
            && fs::copy(staging.join("project.json"), tmp_dir.join("project.json")).is_ok();
        if !copy_ok || self.dir.exists() || fs::rename(&tmp_dir, &self.dir).is_err() {
            let _ = fs::remove_dir_all(&tmp_dir);
        } else {
            Self::prune_if_needed(parent, 128);
        }
    }

    /// Prune cache directory to prevent disk bloating, removing stale `.tmp-*` directories
    /// and oldest cache entries when total entries exceed `max_entries`.
    pub fn prune_if_needed(cache_dir: &Path, max_entries: usize) {
        let entries = match fs::read_dir(cache_dir) {
            Ok(e) => e,
            Err(_) => return,
        };
        let now = SystemTime::now();
        let current_pid_prefix = format!(".tmp-{}-", std::process::id());
        let mut items = Vec::new();
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry.file_name();
            let name_str = name.to_string_lossy();
            if let Some(rest) = name_str.strip_prefix(".tmp-") {
                let modified = entry
                    .metadata()
                    .and_then(|m| m.modified())
                    .unwrap_or(SystemTime::UNIX_EPOCH);
                let is_recent = now
                    .duration_since(modified)
                    .map(|age| age.as_secs() < 300)
                    .unwrap_or(true);
                let is_structured_tmp = rest
                    .split_once('-')
                    .is_some_and(|(pid, seq)| !pid.is_empty() && !seq.is_empty());
                if !(is_structured_tmp && (name_str.starts_with(&current_pid_prefix) || is_recent))
                {
                    let _ = fs::remove_dir_all(&path);
                }
                continue;
            }
            if path.is_dir() {
                let modified = entry
                    .metadata()
                    .and_then(|m| m.modified())
                    .unwrap_or(SystemTime::UNIX_EPOCH);
                items.push((modified, path));
            }
        }
        if items.len() > max_entries {
            items.sort_by_key(|(m, _)| *m);
            let to_remove = items.len() - max_entries;
            for (_, path) in items.into_iter().take(to_remove) {
                let _ = fs::remove_dir_all(path);
            }
        }
    }
}

struct Staging(PathBuf);
impl Drop for Staging {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

struct CompilerOutput {
    status: ExitStatus,
    stdout: Vec<u8>,
    stderr: Vec<u8>,
}

fn read_log(path: &Path) -> Result<Vec<u8>, String> {
    let mut bytes = Vec::new();
    fs::File::open(path)
        .and_then(|file| file.take(16_384).read_to_end(&mut bytes))
        .map_err(|e| format!("cannot read compiler log: {e}"))?;
    Ok(bytes)
}

fn wait_bounded(child: &mut Child, timeout: Duration) -> Result<ExitStatus, String> {
    let deadline = Instant::now() + timeout;
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return Ok(status),
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(10)),
            Ok(None) => return Err(format!("exceeded {}ms deadline", timeout.as_millis())),
            Err(error) => return Err(format!("cannot observe process: {error}")),
        }
    }
}

fn stop_compiler(child: &mut Child) -> String {
    // Keep Child's process handle alive: Windows cannot reuse this PID while
    // the handle exists. Never terminate compiler processes by executable name.
    #[cfg(windows)]
    let tree = match Command::new("taskkill")
        .args(["/PID", &child.id().to_string(), "/T", "/F"])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
    {
        Ok(mut taskkill) => match wait_bounded(&mut taskkill, Duration::from_secs(2)) {
            Ok(status) => format!("{status}"),
            Err(error) => {
                let kill = taskkill.kill();
                let wait = wait_bounded(&mut taskkill, Duration::from_secs(2));
                format!("{error}; cleanup kill={kill:?}, wait={wait:?}")
            }
        },
        Err(error) => format!("cannot start tree cleanup: {error}"),
    };
    let kill = child.kill();
    let wait = wait_bounded(child, Duration::from_secs(2));
    #[cfg(windows)]
    return format!("tree={tree}, kill={kill:?}, wait={wait:?}");
    #[cfg(not(windows))]
    format!("kill={kill:?}, wait={wait:?}; descendant cleanup not guaranteed on this platform")
}

fn run_compiler(
    command: &mut Command,
    directory: &Path,
    prefix: &str,
    timeout: Duration,
) -> Result<CompilerOutput, String> {
    let stdout = directory.join(format!("{prefix}.stdout.log"));
    let stderr = directory.join(format!("{prefix}.stderr.log"));
    let out =
        fs::File::create_new(&stdout).map_err(|e| format!("cannot create compiler log: {e}"))?;
    let err =
        fs::File::create_new(&stderr).map_err(|e| format!("cannot create compiler log: {e}"))?;
    let mut child = command
        .current_dir(directory)
        .stdin(Stdio::null())
        .stdout(out)
        .stderr(err)
        .spawn()
        .map_err(|e| format!("cannot start compiler: {e}"))?;
    let status = wait_bounded(&mut child, timeout).map_err(|reason| {
        let cleanup = stop_compiler(&mut child);
        format!("compiler {reason}; {cleanup}")
    })?;
    Ok(CompilerOutput {
        status,
        stdout: read_log(&stdout)?,
        stderr: read_log(&stderr)?,
    })
}

fn cached_rustc_version(staging: &Path) -> Result<String, String> {
    static CACHE: OnceLock<Result<String, String>> = OnceLock::new();
    CACHE
        .get_or_init(|| {
            let compiler = run_compiler(
                Command::new("rustc").arg("-vV"),
                staging,
                "version",
                Duration::from_secs(10),
            )?;
            if !compiler.status.success() {
                return Err(format!(
                    "rustc version probe failed: {}",
                    String::from_utf8_lossy(&compiler.stderr)
                ));
            }
            String::from_utf8(compiler.stdout)
                .map_err(|_| "rustc version output is not UTF-8".to_string())
        })
        .clone()
}

/// Build an admitted project using rustc directly, without loading SDK assemblies.
/// Each success publishes a distinct generation only after rustc has succeeded.
/// Never reuse an executable based solely on timestamps or source-only cache keys.
pub fn build(project: &Project, output: &Path) -> Result<BuildArtifact, String> {
    let source = crate::compile(project)?;
    fs::create_dir_all(output).map_err(|e| format!("cannot create build directory: {e}"))?;
    let output =
        fs::canonicalize(output).map_err(|e| format!("cannot resolve build directory: {e}"))?;
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|e| format!("invalid system clock: {e}"))?
        .as_nanos();
    let name = format!(
        "{}-{stamp}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    );
    let staging = Staging(output.join(format!(".pending-{name}")));
    fs::create_dir(&staging.0).map_err(|e| format!("cannot create staging directory: {e}"))?;
    let compiler = cached_rustc_version(&staging.0)?;
    let executable_name = if cfg!(windows) {
        "program.exe"
    } else {
        "program"
    };
    let args = [
        "--crate-name",
        "gx_program",
        "--edition=2024",
        "-C",
        "opt-level=1",
        "-C",
        "overflow-checks=yes",
    ];

    let project_json = project.to_json()?;
    let cache_key = compute_build_cache_key(
        &project_json,
        &source,
        &compiler,
        &args,
        env!("CARGO_PKG_VERSION"),
    );
    let cache = BuildCache::new(&output, &cache_key);
    let cache_hit = cache.try_restore(&staging.0, executable_name);

    if !cache_hit {
        fs::write(staging.0.join("program.rs"), &source)
            .map_err(|e| format!("cannot write generated source: {e}"))?;
        fs::write(staging.0.join("project.json"), &project_json)
            .map_err(|e| format!("cannot write portable input: {e}"))?;
        let compiled = run_compiler(
            Command::new("rustc")
                .args(args)
                .arg("program.rs")
                .arg("-o")
                .arg(executable_name),
            &staging.0,
            "compile",
            Duration::from_secs(120),
        )?;
        if !compiled.status.success() {
            let len = compiled.stderr.len();
            let detail = if len > 16_384 {
                &compiled.stderr[len - 16_384..]
            } else {
                &compiled.stderr[..]
            };
            return Err(format!(
                "native compilation failed ({}):\n{}",
                compiled.status,
                String::from_utf8_lossy(detail)
            ));
        }
        if !staging.0.join(executable_name).is_file() {
            return Err("compiler succeeded without producing the executable".into());
        }

        cache.store(&staging.0, executable_name);
    }

    let manifest = Manifest {
        format_version: 1,
        engine_version: env!("CARGO_PKG_VERSION"),
        backend: if project.format_version == 2 {
            "independent-rust-typed-subset"
        } else {
            "independent-rust-i64-subset"
        },
        compiler: &compiler,
        compiler_args: args.to_vec(),
        entry: &project.entry,
        procedures: project.procedures.len(),
        source: "program.rs",
        executable: executable_name,
        project: "project.json",
        cache_hit,
    };
    fs::write(
        staging.0.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest).map_err(|e| e.to_string())?,
    )
    .map_err(|e| format!("cannot write build manifest: {e}"))?;
    for log in [
        "version.stdout.log",
        "version.stderr.log",
        "compile.stdout.log",
        "compile.stderr.log",
    ] {
        let p = staging.0.join(log);
        if p.exists() {
            let _ = fs::remove_file(p);
        }
    }
    let published = output.join(format!("build-{name}"));
    fs::rename(&staging.0, &published)
        .map_err(|e| format!("cannot publish complete build: {e}"))?;
    Ok(BuildArtifact {
        executable: published.join(executable_name),
        source: published.join("program.rs"),
        manifest: published.join("manifest.json"),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compiler_deadline_reaps_the_owned_process() {
        let path = std::env::temp_dir().join(format!(
            "gx-compiler-deadline-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        let staging = Staging(path);
        #[cfg(windows)]
        let mut command = {
            let mut command = Command::new("cmd");
            command.args(["/C", "ping -n 3 127.0.0.1 >NUL"]);
            command
        };
        #[cfg(not(windows))]
        let mut command = {
            let mut command = Command::new("sh");
            command.args(["-c", "sleep 2"]);
            command
        };
        let error = run_compiler(&mut command, &staging.0, "test", Duration::from_millis(50))
            .err()
            .expect("long-running compiler must time out");
        assert!(error.contains("exceeded 50ms deadline"), "{error}");
        assert!(error.contains("wait=Ok"), "{error}");
    }

    #[test]
    fn content_addressed_cache_hits_on_identical_project() {
        let scratch = std::env::temp_dir().join(format!(
            "gx-build-cache-hit-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        let _ = fs::remove_dir_all(&scratch);
        fs::create_dir_all(&scratch).unwrap();

        let project_json = r#"{
            "format_version": 1,
            "entry": "MAIN",
            "procedures": [
                {
                    "name": "MAIN",
                    "params": [],
                    "source": "Print(42)\nReturn 0"
                }
            ]
        }"#;
        let project = Project::from_json(project_json).unwrap();

        let first_build = build(&project, &scratch).unwrap();
        assert!(first_build.executable.is_file());
        let first_manifest: serde_json::Value =
            serde_json::from_slice(&fs::read(&first_build.manifest).unwrap()).unwrap();
        assert_eq!(first_manifest["cache_hit"], false);

        let first_run = Command::new(&first_build.executable).output().unwrap();
        assert_eq!(String::from_utf8_lossy(&first_run.stdout).trim(), "42");

        let second_build = build(&project, &scratch).unwrap();
        assert!(second_build.executable.is_file());
        let second_manifest: serde_json::Value =
            serde_json::from_slice(&fs::read(&second_build.manifest).unwrap()).unwrap();
        assert_eq!(second_manifest["cache_hit"], true);

        let second_run = Command::new(&second_build.executable).output().unwrap();
        assert_eq!(String::from_utf8_lossy(&second_run.stdout).trim(), "42");

        let _ = fs::remove_dir_all(&scratch);
    }

    #[test]
    fn content_addressed_cache_invalidates_on_source_change() {
        let scratch = std::env::temp_dir().join(format!(
            "gx-build-cache-inval-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        let _ = fs::remove_dir_all(&scratch);
        fs::create_dir_all(&scratch).unwrap();

        let project1_json = r#"{
            "format_version": 1,
            "entry": "MAIN",
            "procedures": [
                {
                    "name": "MAIN",
                    "params": [],
                    "source": "Print(100)\nReturn 0"
                }
            ]
        }"#;
        let project1 = Project::from_json(project1_json).unwrap();
        let build1 = build(&project1, &scratch).unwrap();
        let m1: serde_json::Value =
            serde_json::from_slice(&fs::read(&build1.manifest).unwrap()).unwrap();
        assert_eq!(m1["cache_hit"], false);

        let project2_json = r#"{
            "format_version": 1,
            "entry": "MAIN",
            "procedures": [
                {
                    "name": "MAIN",
                    "params": [],
                    "source": "Print(200)\nReturn 0"
                }
            ]
        }"#;
        let project2 = Project::from_json(project2_json).unwrap();
        let build2 = build(&project2, &scratch).unwrap();
        let m2: serde_json::Value =
            serde_json::from_slice(&fs::read(&build2.manifest).unwrap()).unwrap();
        assert_eq!(m2["cache_hit"], false);

        let run2 = Command::new(&build2.executable).output().unwrap();
        assert_eq!(String::from_utf8_lossy(&run2.stdout).trim(), "200");

        let _ = fs::remove_dir_all(&scratch);
    }

    #[test]
    fn build_cache_prune_removes_oldest_entries_and_tmp_dirs() {
        let scratch = std::env::temp_dir().join(format!(
            "gx-build-cache-prune-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        let _ = fs::remove_dir_all(&scratch);
        fs::create_dir_all(&scratch).unwrap();

        let dir1 = scratch.join("entry1");
        let dir2 = scratch.join("entry2");
        let dir3 = scratch.join("entry3");
        let tmp = scratch.join(".tmp-1234");
        let active_tmp = scratch.join(format!(".tmp-{}-99", std::process::id()));
        fs::create_dir_all(&dir1).unwrap();
        fs::create_dir_all(&dir2).unwrap();
        fs::create_dir_all(&dir3).unwrap();
        fs::create_dir_all(&tmp).unwrap();
        fs::create_dir_all(&active_tmp).unwrap();

        // Prune to max 2 entries
        BuildCache::prune_if_needed(&scratch, 2);

        // Stale tmp dir must always be purged while active in-flight tmp dir is preserved
        assert!(!tmp.exists());
        assert!(active_tmp.exists());
        let _ = fs::remove_dir_all(&active_tmp);

        // Total remaining dirs must be at most 2
        let remaining: Vec<_> = fs::read_dir(&scratch)
            .unwrap()
            .flatten()
            .filter(|e| e.path().is_dir())
            .collect();
        assert_eq!(remaining.len(), 2);

        let _ = fs::remove_dir_all(&scratch);
    }
}
