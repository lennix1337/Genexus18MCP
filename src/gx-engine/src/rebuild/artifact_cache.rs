//! Content-addressed cache of emitted artifacts, shared between output folders and machines.
//!
//! Off unless a directory is configured (`RebuildOptions::artifact_cache_dir`). An entry is the value of
//! one generation step, stored under the fingerprint of everything that step read (see `super::reuse`),
//! so the same key always names the same value, whichever Knowledge Base or machine produced it.
//!
//! Layout, under the configured directory:
//!
//! ```text
//! v1/<kind>/<first two hex digits of the key>/<key>
//! ```
//!
//! `<kind>` is `emit` (one Procedure's emitted C#), `spec` (one Procedure's navigation report) or `sdts`
//! (the files of a model's structured data types). An entry is one header line and the payload:
//!
//! ```text
//! gx-artifact-1 <key> <payload length> <payload SHA-256>\n<payload JSON>
//! ```
//!
//! Publication: a writer builds the whole entry in a unique temporary file beside it and renames it over
//! the entry, so a reader sees a complete entry or none. A restore checks the header's key, the payload
//! length and its SHA-256 before the payload is decoded. An entry that fails any check is not used: the
//! step runs again and its result replaces the entry. Only successful results are stored; a refusal or an
//! error is never read from the cache.
//!
//! Nothing is evicted. Entries are small text files (the C# of one Procedure, a navigation report); the
//! directory grows with every distinct object version built. Any entry, or the whole directory, can be
//! deleted at any time: a missing entry is only a miss.

use serde::{Serialize, de::DeserializeOwned};
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::{ErrorKind, Read, Write},
    path::{Path, PathBuf},
    sync::{
        Mutex,
        atomic::{AtomicU64, AtomicUsize, Ordering},
    },
};

const LAYOUT: &str = "v1";
const MAGIC: &str = "gx-artifact-1";
/// The largest entry read. A larger file is not an entry this cache wrote for a single object.
const MAX_ENTRY_BYTES: u64 = 256 * 1024 * 1024;

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

/// An open cache directory, with what this run read from it and wrote to it.
pub(super) struct ArtifactCache {
    root: PathBuf,
    restored: AtomicUsize,
    stored: AtomicUsize,
    /// The first failure to store an entry. A failed store never fails the build.
    store_failure: Mutex<Option<String>>,
}

impl ArtifactCache {
    /// Names the directory. Nothing is created until an entry is stored.
    pub fn new(dir: &Path) -> Self {
        Self {
            root: dir.join(LAYOUT),
            restored: AtomicUsize::new(0),
            stored: AtomicUsize::new(0),
            store_failure: Mutex::new(None),
        }
    }

    pub fn restored(&self) -> usize {
        self.restored.load(Ordering::Relaxed)
    }

    pub fn stored(&self) -> usize {
        self.stored.load(Ordering::Relaxed)
    }

    pub fn store_failure(&self) -> Option<String> {
        self.store_failure
            .lock()
            .map(|failure| failure.clone())
            .unwrap_or(None)
    }

    fn entry_path(&self, kind: &str, key: &str) -> PathBuf {
        let shard = key.get(..2).unwrap_or("00");
        self.root.join(kind).join(shard).join(key)
    }

    /// The value stored under `key`, when its entry exists and passes every check.
    pub fn get<T: DeserializeOwned>(&self, kind: &str, key: &str) -> Option<T> {
        let payload = read_verified(&self.entry_path(kind, key), key)?;
        let value = serde_json::from_slice(&payload).ok()?;
        self.restored.fetch_add(1, Ordering::Relaxed);
        Some(value)
    }

    /// Publishes `value` under `key`, unless a valid entry is already there.
    pub fn put<T: Serialize>(&self, kind: &str, key: &str, value: &T) {
        let path = self.entry_path(kind, key);
        if read_verified(&path, key).is_some() {
            return;
        }
        let published = serde_json::to_vec(value)
            .map_err(|error| format!("cannot encode the entry: {error}"))
            .and_then(|payload| publish(&path, &encode(key, &payload)));
        match published {
            Ok(()) => {
                self.stored.fetch_add(1, Ordering::Relaxed);
            }
            Err(error) => {
                if let Ok(mut failure) = self.store_failure.lock() {
                    failure.get_or_insert(format!("{}: {error}", path.display()));
                }
            }
        }
    }
}

fn sha256_hex(bytes: &[u8]) -> String {
    super::reuse::hex(&Sha256::digest(bytes))
}

fn encode(key: &str, payload: &[u8]) -> Vec<u8> {
    let mut entry =
        format!("{MAGIC} {key} {} {}\n", payload.len(), sha256_hex(payload)).into_bytes();
    entry.extend_from_slice(payload);
    entry
}

/// The payload of the entry at `path`, when the entry is complete, names `key` and its payload has the
/// recorded length and digest. Any other file is `None`.
fn read_verified(path: &Path, key: &str) -> Option<Vec<u8>> {
    let file = File::open(path).ok()?;
    let mut bytes = Vec::new();
    file.take(MAX_ENTRY_BYTES + 1)
        .read_to_end(&mut bytes)
        .ok()?;
    if bytes.len() as u64 > MAX_ENTRY_BYTES {
        return None;
    }
    let header_end = bytes.iter().position(|&byte| byte == b'\n')?;
    let header = std::str::from_utf8(&bytes[..header_end]).ok()?;
    let mut fields = header.split(' ');
    let (magic, stored_key, length, digest) = (
        fields.next()?,
        fields.next()?,
        fields.next()?,
        fields.next()?,
    );
    if fields.next().is_some() || magic != MAGIC || stored_key != key {
        return None;
    }
    let payload = &bytes[header_end + 1..];
    if length.parse::<usize>().ok()? != payload.len() || digest != sha256_hex(payload) {
        return None;
    }
    Some(payload.to_vec())
}

/// Writes `bytes` to a unique temporary file beside `path`, then renames it over `path`. A failed write or
/// rename removes the temporary file and leaves `path` as it was.
fn publish(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let parent = path.parent().ok_or("the entry has no directory")?;
    fs::create_dir_all(parent).map_err(|error| format!("cannot create the directory: {error}"))?;
    let name = path.file_name().ok_or("the entry has no file name")?;
    let (temp_path, mut temp_file) = create_temp(parent, name)?;
    let staged = temp_file
        .write_all(bytes)
        .and_then(|()| temp_file.sync_all());
    drop(temp_file);
    if let Err(error) = staged {
        let _ = fs::remove_file(&temp_path);
        return Err(format!("cannot write the entry: {error}"));
    }
    if let Err(error) = fs::rename(&temp_path, path) {
        let _ = fs::remove_file(&temp_path);
        // Another writer published the same key first and a reader holds it open: its entry stands.
        if read_verified(path, &name.to_string_lossy()).is_some() {
            return Ok(());
        }
        return Err(format!("cannot publish the entry: {error}"));
    }
    Ok(())
}

fn create_temp(parent: &Path, name: &std::ffi::OsStr) -> Result<(PathBuf, File), String> {
    for _ in 0..32 {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let mut temp_name = name.to_os_string();
        temp_name.push(format!(".tmp.{}.{}", std::process::id(), sequence));
        let path = parent.join(temp_name);
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => return Ok((path, file)),
            Err(error) if error.kind() == ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(format!("cannot create a temporary entry: {error}")),
        }
    }
    Err("cannot find a free temporary entry name".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_cache(label: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "gx_artifact_cache_{label}_{}_{}",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    const KEY: &str = "ab54d286f3c1";

    #[test]
    fn a_stored_value_is_restored_and_counted() {
        let dir = temp_cache("roundtrip");
        let cache = ArtifactCache::new(&dir);
        assert_eq!(
            cache.get::<String>("emit", KEY),
            None,
            "an empty cache hits"
        );
        assert!(!dir.exists(), "a read created the cache directory");

        cache.put("emit", KEY, &"class A {}".to_string());
        assert_eq!(
            cache.get::<String>("emit", KEY).as_deref(),
            Some("class A {}")
        );
        assert_eq!((cache.stored(), cache.restored()), (1, 1));
        assert!(dir.join("v1").join("emit").join("ab").join(KEY).is_file());
        let _ = fs::remove_dir_all(&dir);
    }

    /// Each of these files is at the entry's path and is not the entry that was published. None of them
    /// may be restored; a later store replaces it with a valid entry.
    #[test]
    fn an_entry_that_fails_a_check_is_ignored_and_replaced() {
        let dir = temp_cache("corrupt");
        let cache = ArtifactCache::new(&dir);
        cache.put("emit", KEY, &"class A {}".to_string());
        let path = cache.entry_path("emit", KEY);
        let valid = fs::read(&path).unwrap();
        let header_end = valid.iter().position(|&byte| byte == b'\n').unwrap();

        let mut flipped = valid.clone();
        *flipped.last_mut().unwrap() ^= 1;
        let truncated = valid[..valid.len() - 3].to_vec();
        let other_key = encode("ab00000000000", &valid[header_end + 1..]);
        let cases: [(&str, Vec<u8>); 5] = [
            ("a changed payload byte", flipped),
            ("a truncated payload", truncated),
            ("an entry published under another key", other_key),
            ("an empty file", Vec::new()),
            ("a file without a header", b"class A {}".to_vec()),
        ];
        for (what, bytes) in cases {
            fs::write(&path, &bytes).unwrap();
            assert_eq!(
                cache.get::<String>("emit", KEY),
                None,
                "{what} was restored"
            );
            cache.put("emit", KEY, &"class A {}".to_string());
            assert_eq!(
                cache.get::<String>("emit", KEY).as_deref(),
                Some("class A {}"),
                "{what} was not replaced by a valid entry"
            );
        }
        let _ = fs::remove_dir_all(&dir);
    }

    /// A payload that passes the integrity checks but is not the stored type is a miss, not a panic.
    #[test]
    fn a_valid_entry_of_another_type_is_a_miss() {
        let dir = temp_cache("type");
        let cache = ArtifactCache::new(&dir);
        cache.put("emit", KEY, &vec![1u32, 2, 3]);
        assert_eq!(cache.get::<String>("emit", KEY), None);
        let _ = fs::remove_dir_all(&dir);
    }

    /// Writers of one key publish whole entries: a reader running beside them sees no entry or the value,
    /// never a partial file, and the entry left behind is valid with no temporary file beside it.
    #[test]
    fn concurrent_writers_of_one_key_leave_one_valid_entry() {
        let dir = temp_cache("concurrent");
        let value = "x".repeat(256 * 1024);
        let done = std::sync::atomic::AtomicBool::new(false);
        std::thread::scope(|scope| {
            let reader = scope.spawn(|| {
                let cache = ArtifactCache::new(&dir);
                let mut seen = 0usize;
                while !done.load(Ordering::Relaxed) {
                    if let Some(read) = cache.get::<String>("emit", KEY) {
                        assert_eq!(read, value, "a reader saw another value");
                        seen += 1;
                    }
                    // A file at the entry's path is a whole entry at every instant.
                    let path = cache.entry_path("emit", KEY);
                    if let Ok(bytes) = fs::read(&path) {
                        assert_eq!(bytes, encode(KEY, &serde_json::to_vec(&value).unwrap()));
                    }
                }
                seen
            });
            let writers: Vec<_> = (0..8)
                .map(|_| {
                    scope.spawn(|| {
                        let cache = ArtifactCache::new(&dir);
                        for _ in 0..20 {
                            // Publish unconditionally, so every writer really races on the rename. A
                            // writer that loses the race may fail; it must not damage the entry.
                            let path = cache.entry_path("emit", KEY);
                            let entry = encode(KEY, &serde_json::to_vec(&value).unwrap());
                            let _ = publish(&path, &entry);
                        }
                    })
                })
                .collect();
            for writer in writers {
                writer.join().expect("writer");
            }
            done.store(true, Ordering::Relaxed);
            reader.join().expect("reader");
        });
        let cache = ArtifactCache::new(&dir);
        assert_eq!(cache.get::<String>("emit", KEY), Some(value));
        let shard = cache.entry_path("emit", KEY);
        let names: Vec<String> = fs::read_dir(shard.parent().unwrap())
            .unwrap()
            .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert_eq!(names, [KEY], "a temporary file was left beside the entry");
        let _ = fs::remove_dir_all(&dir);
    }
}
