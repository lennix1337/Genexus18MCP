using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace GxMcp.Worker.Services
{
    // Item 51 (mcp-improvements-2026-05-22, Tier-S) — EXPERIMENTAL.
    //
    // Persist IndexCacheService state to disk before a worker reload and
    // restore it on boot when the worker DLL hash matches. The DLL-hash gate
    // is the safety story: a binary that changed shape (new index field,
    // new analyser, schema migration, …) MUST cold-boot — silently
    // deserialising into a different layout corrupts the index.
    //
    // The file lives at <kbPath>/.gx/index-snapshot.bin and is a tiny custom
    // container:
    //   - 4 bytes magic                "GXIS"
    //   - 4 bytes version              1
    //   - JSON metadata block (length-prefixed UTF-8)
    //   - payload bytes                (caller-defined; today the SearchIndex JSON)
    //
    // The IWarmSnapshotStore seam exists for unit tests so we don't write to
    // real disk — the production code path uses DiskWarmSnapshotStore.
    public sealed class WarmIndexSnapshotMetadata
    {
        /// <summary>SHA-256 of the worker DLL that wrote this snapshot.</summary>
        public string WorkerDllSha256 { get; set; }

        /// <summary>Absolute KB path the snapshot was captured against.</summary>
        public string KbPath { get; set; }

        /// <summary>UTC capture timestamp (ISO-8601).</summary>
        public string CapturedAtUtc { get; set; }

        /// <summary>Number of objects in the captured index. Diagnostic only.</summary>
        public int ObjectCount { get; set; }

        /// <summary>
        /// Index payload schema version. Bumped whenever the IndexEntry shape changes.
        /// A mismatch forces a full cold rebuild (IntelliJ stub-index pattern): silently
        /// deserialising a 38k index into a different layout would corrupt it. 0 = legacy
        /// snapshot written before versioning existed.
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// High-water-mark: the maximum KBObject.LastUpdate observed when the index was
        /// captured (ISO-8601 UTC). On warm start, only objects changed after this are
        /// re-indexed (delta-on-open). Null/empty = unknown → treat as epoch (full delta).
        /// </summary>
        public string HighWaterMarkUtc { get; set; }
    }

    public interface IWarmSnapshotStore
    {
        /// <summary>
        /// Write a snapshot. <paramref name="payload"/> is the serialised
        /// index body (typically JSON UTF-8 bytes). Throws on I/O failure.
        /// </summary>
        void Save(string path, WarmIndexSnapshotMetadata metadata, byte[] payload);

        /// <summary>
        /// Try to load a snapshot. Returns true on success and populates
        /// <paramref name="metadata"/> + <paramref name="payload"/>; returns
        /// false on missing-file / unreadable / format-mismatch (caller falls
        /// through to cold boot).
        /// </summary>
        bool TryLoad(string path, out WarmIndexSnapshotMetadata metadata, out byte[] payload);
    }

    internal sealed class DiskWarmSnapshotStore : IWarmSnapshotStore
    {
        private const uint Magic = 0x53495847; // 'GXIS' (little-endian)
        private const int Version = 1;

        public void Save(string path, WarmIndexSnapshotMetadata metadata, byte[] payload)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            payload = payload ?? new byte[0];

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string metaJson = JsonConvert.SerializeObject(metadata);
            byte[] metaBytes = Encoding.UTF8.GetBytes(metaJson);

            string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(Magic);
                    bw.Write(Version);
                    bw.Write(metaBytes.Length);
                    bw.Write(metaBytes);
                    bw.Write(payload.Length);
                    bw.Write(payload);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tempPath, path, null);
                else File.Move(tempPath, path);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        public bool TryLoad(string path, out WarmIndexSnapshotMetadata metadata, out byte[] payload)
        {
            metadata = null;
            payload = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    if (br.BaseStream.Length < 16) return false;
                    uint magic = br.ReadUInt32();
                    if (magic != Magic) return false;
                    int version = br.ReadInt32();
                    if (version != Version) return false;
                    int metaLen = br.ReadInt32();
                    if (metaLen < 0 || metaLen > 1024 * 1024) return false;
                    byte[] metaBytes = br.ReadBytes(metaLen);
                    if (metaBytes.Length != metaLen) return false;
                    string metaJson = Encoding.UTF8.GetString(metaBytes);
                    metadata = JsonConvert.DeserializeObject<WarmIndexSnapshotMetadata>(metaJson);
                    int payloadLen = br.ReadInt32();
                    if (payloadLen < 0) return false;
                    payload = br.ReadBytes(payloadLen);
                    return payload.Length == payloadLen;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class WarmIndexSnapshotResult
    {
        public bool Loaded { get; set; }
        public bool Fallback { get; set; }
        public string FallbackReason { get; set; }
        public WarmIndexSnapshotMetadata Metadata { get; set; }
        public byte[] Payload { get; set; }
    }

    public static class WarmIndexSnapshot
    {
        public const int DefaultSchemaVersion = 1;

        // Injectable for tests; production uses DiskWarmSnapshotStore.
        private static IWarmSnapshotStore _store = new DiskWarmSnapshotStore();
        public static void SetStoreForTests(IWarmSnapshotStore store)
        {
            _store = store ?? new DiskWarmSnapshotStore();
        }

        /// <summary>Default snapshot path under the KB's .gx folder.</summary>
        public static string DefaultPath(string kbPath)
        {
            string directory = NormalizeKbPath(kbPath);
            return string.IsNullOrEmpty(directory)
                ? null
                : Path.Combine(directory, ".gx", "index-snapshot.bin");
        }

        /// <summary>
        /// Normalizes the KB identity used by the warm snapshot. OpenKB accepts both
        /// a KB directory and a .gxw/.gx file, while the snapshot always lives under
        /// the directory; using one representation avoids false path-mismatch fallbacks.
        /// </summary>
        public static string NormalizeKbPath(string kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath)) return null;
            string value = kbPath.Trim();
            try
            {
                string extension = Path.GetExtension(value);
                if (string.Equals(extension, ".gxw", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".gx", StringComparison.OrdinalIgnoreCase))
                {
                    value = Path.GetDirectoryName(value);
                }
                if (string.IsNullOrWhiteSpace(value)) return null;
                return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        /// <summary>
        /// Compute SHA-256 of the worker DLL. Returns empty string on I/O failure;
        /// callers should NOT treat empty as a match.
        /// </summary>
        // Cache for the default (executing-assembly) hash — the worker DLL can't change during
        // a process lifetime, so the multi-MB read + SHA-256 only needs to run once even though
        // the index hot path (WriteMetaSidecar / ValidateOnDiskCache) asks for it repeatedly.
        // An explicit workerDllPath (tests) bypasses the cache.
        private static volatile string _cachedDefaultDllSha;

        public static string ComputeWorkerDllSha256(string workerDllPath = null)
        {
            bool useDefault = string.IsNullOrEmpty(workerDllPath);
            if (useDefault && _cachedDefaultDllSha != null) return _cachedDefaultDllSha;
            try
            {
                workerDllPath = workerDllPath ?? Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(workerDllPath) || !File.Exists(workerDllPath)) return string.Empty;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(workerDllPath))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    var result = sb.ToString();
                    if (useDefault) _cachedDefaultDllSha = result;
                    return result;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void Save(
            string path,
            byte[] payload,
            string kbPath,
            int objectCount,
            string workerDllPath = null,
            int schemaVersion = DefaultSchemaVersion,
            string highWaterMarkUtc = null)
        {
            var meta = new WarmIndexSnapshotMetadata
            {
                WorkerDllSha256 = ComputeWorkerDllSha256(workerDllPath),
                KbPath = NormalizeKbPath(kbPath) ?? string.Empty,
                CapturedAtUtc = DateTime.UtcNow.ToString("o"),
                ObjectCount = objectCount,
                SchemaVersion = schemaVersion <= 0 ? DefaultSchemaVersion : schemaVersion,
                HighWaterMarkUtc = highWaterMarkUtc
            };
            _store.Save(path, meta, payload);
        }

        public static WarmIndexSnapshotResult TryLoad(string path, string workerDllPath = null)
        {
            var result = new WarmIndexSnapshotResult();
            WarmIndexSnapshotMetadata meta;
            byte[] payload;
            if (!_store.TryLoad(path, out meta, out payload))
            {
                result.Fallback = true;
                result.FallbackReason = "snapshot-missing-or-unreadable";
                return result;
            }

            string currentSha = ComputeWorkerDllSha256(workerDllPath);
            if (string.IsNullOrEmpty(currentSha))
            {
                result.Fallback = true;
                result.FallbackReason = "worker-dll-hash-unavailable";
                result.Metadata = meta;
                return result;
            }

            if (!string.Equals(currentSha, meta?.WorkerDllSha256, StringComparison.OrdinalIgnoreCase))
            {
                result.Fallback = true;
                result.FallbackReason = "worker-dll-hash-mismatch";
                result.Metadata = meta;
                return result;
            }

            result.Loaded = true;
            result.Metadata = meta;
            result.Payload = payload;
            return result;
        }
    }
}
