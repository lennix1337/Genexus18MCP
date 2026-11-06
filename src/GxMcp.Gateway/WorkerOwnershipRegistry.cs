using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Checkout-scoped ownership for a worker. The registry is deliberately keyed by
    /// both the worker executable and KB path: another checkout must never reap this
    /// checkout's worker merely because it serves the same KB.
    /// </summary>
    internal sealed class WorkerOwnershipLease : IDisposable
    {
        private readonly string _path;
        private readonly string _key;
        private bool _disposed;

        internal WorkerOwnershipLease(string path, string key)
        {
            _path = path;
            _key = key;
        }

        internal void SetWorker(Process process)
        {
            WorkerOwnershipRegistry.UpdateWorker(_path, _key, process);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            WorkerOwnershipRegistry.Release(_path, _key);
        }
    }

    internal sealed class WorkerOwnershipRecord
    {
        public string Key { get; set; } = string.Empty;
        public int GatewayPid { get; set; }
        public long GatewayStartTimeUtcTicks { get; set; }
        public int WorkerPid { get; set; }
        public long WorkerStartTimeUtcTicks { get; set; }
        public string WorkerExecutable { get; set; } = string.Empty;
        public string KbPath { get; set; } = string.Empty;
    }

    internal static class WorkerOwnershipRegistry
    {
        private static readonly object Gate = new object();
        private static readonly string RegistryRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GxMcp", "worker-ownership");

        /// <summary>The on-disk ownership registry root, %LOCALAPPDATA%\GxMcp\worker-ownership.</summary>
        internal static string DefaultRoot => RegistryRoot;

        /// <summary>
        /// Read-only scan for the ownership records of one KB, for any Worker executable.
        /// Parses every "*.json" file directly under the root and keeps the records whose
        /// normalized KB path equals the normalized <paramref name="kbPath"/>. Unparseable
        /// files are skipped and a missing root gives an empty list. It never creates,
        /// modifies or deletes a file, and takes no lock or mutex.
        /// </summary>
        internal static IReadOnlyList<WorkerOwnershipRecord> ScanByKbPath(string kbPath, string? registryRoot = null)
        {
            string root = registryRoot ?? RegistryRoot;
            string target = SharedWorkerIdentity.NormalizePath(kbPath);
            var matches = new List<WorkerOwnershipRecord>();
            if (target.Length == 0 || !Directory.Exists(root)) return matches;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).ToList(); }
            catch (IOException) { return matches; }
            catch (UnauthorizedAccessException) { return matches; }

            foreach (string file in files)
            {
                // Guard against pattern quirks: temp files ("*.json.tmp-*") are never records.
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                WorkerOwnershipRecord? record = Read(file);
                if (record == null) continue;
                if (string.Equals(SharedWorkerIdentity.NormalizePath(record.KbPath), target, StringComparison.Ordinal))
                    matches.Add(record);
            }
            return matches;
        }

        internal static WorkerOwnershipLease Acquire(string workerExecutable, string kbPath)
        {
            string key = BuildKey(workerExecutable, kbPath);
            string path = Path.Combine(RegistryRoot, key + ".json");
            lock (Gate)
            using (var mutex = new Mutex(false, MutexName(key)))
            {
                mutex.WaitOne();
                try
                {
                Directory.CreateDirectory(RegistryRoot);
                WorkerOwnershipRecord? existing = Read(path);
                if (existing != null && IsLiveGateway(existing))
                {
                    throw new InvalidOperationException(
                        $"Worker ownership is already held by gateway PID {existing.GatewayPid} for this checkout and KB.");
                }

                if (existing != null)
                {
                    ReapRecordedWorker(existing);
                    TryDelete(path);
                }

                using var gateway = Process.GetCurrentProcess();
                var record = new WorkerOwnershipRecord
                {
                    Key = key,
                    GatewayPid = Environment.ProcessId,
                    GatewayStartTimeUtcTicks = gateway.StartTime.ToUniversalTime().Ticks,
                    WorkerExecutable = Normalize(workerExecutable),
                    KbPath = Normalize(kbPath)
                };
                Write(path, record);
                return new WorkerOwnershipLease(path, key);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static string MutexName(string key) => "Local\\GxMcp.WorkerOwnership." + key;

        internal static string BuildKey(string workerExecutable, string kbPath)
        {
            string material = Normalize(workerExecutable) + "\n" + Normalize(kbPath);
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                var sb = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        internal static bool IsSameProcess(Process process, WorkerOwnershipRecord record)
        {
            try
            {
                return process.Id == record.WorkerPid
                    && !process.HasExited
                    && process.StartTime.ToUniversalTime().Ticks == record.WorkerStartTimeUtcTicks;
            }
            catch { return false; }
        }

        internal static void Reconcile(string workerExecutable, string kbPath)
        {
            string key = BuildKey(workerExecutable, kbPath);
            string path = Path.Combine(RegistryRoot, key + ".json");
            lock (Gate)
            {
                WorkerOwnershipRecord? record = Read(path);
                if (record == null || IsLiveGateway(record)) return;
                ReapRecordedWorker(record);
                TryDelete(path);
            }
        }

        internal static void UpdateWorker(string path, string key, Process process)
        {
            lock (Gate)
            {
                WorkerOwnershipRecord? record = Read(path);
                if (record == null || record.Key != key || record.GatewayPid != Environment.ProcessId)
                    throw new InvalidOperationException("Worker ownership reservation was lost before worker start.");
                record.WorkerPid = process.Id;
                record.WorkerStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                Write(path, record);
            }
        }

        internal static void Release(string path, string key)
        {
            lock (Gate)
            {
                WorkerOwnershipRecord? record = Read(path);
                if (record != null && record.Key == key && record.GatewayPid == Environment.ProcessId)
                    TryDelete(path);
            }
        }

        private static void ReapRecordedWorker(WorkerOwnershipRecord record)
        {
            if (record.WorkerPid <= 0) return;
            try
            {
                using (var process = Process.GetProcessById(record.WorkerPid))
                {
                    if (IsSameProcess(process, record))
                    {
                        Program.Log($"[Gateway] Reaping stale owned worker pid={record.WorkerPid} for KB '{record.KbPath}'.");
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
            }
            catch (ArgumentException) { }
            catch (Exception ex) { Program.Log($"[Gateway] Stale worker cleanup failed: {ex.Message}"); }
        }

        private static bool IsLiveGateway(WorkerOwnershipRecord record)
        {
            if (record.GatewayPid <= 0) return false;
            try
            {
                using var process = Process.GetProcessById(record.GatewayPid);
                if (process.HasExited) return false;
                // Old records lack a start time. Preserve their live-PID reservation;
                // uncertain ownership must not authorize reaping a possibly live worker.
                return record.GatewayStartTimeUtcTicks <= 0
                    || process.StartTime.ToUniversalTime().Ticks == record.GatewayStartTimeUtcTicks;
            }
            catch (ArgumentException) { return false; }
            catch { return true; }
        }

        private static WorkerOwnershipRecord? Read(string path)
        {
            try { return File.Exists(path) ? JsonConvert.DeserializeObject<WorkerOwnershipRecord>(File.ReadAllText(path)) : null; }
            catch { return null; }
        }

        private static void Write(string path, WorkerOwnershipRecord record)
        {
            string temp = path + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(temp, JsonConvert.SerializeObject(record));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string Normalize(string value) =>
            (value ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant();
    }
}
