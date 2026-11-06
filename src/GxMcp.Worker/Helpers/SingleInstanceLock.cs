using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// FR#19 (v2.6.6 Stream B): coarse-grained single-instance enforcement for the
    /// worker process. Keyed by (kbPath + workerExePath); two workers serving the
    /// same KB must NEVER coexist or they fight over the .gxw lock and end up
    /// pinning publish\worker\GxMcp.Worker.exe (so taskkill becomes the only path
    /// to copy a new binary, which is what we're trying to stop).
    ///
    /// Strategy: try a Global\ named mutex first (cross-session); fall back to a
    /// publish\worker\.lock pid-file when the mutex creation is refused (e.g. UAC
    /// on the Global\ namespace under a non-elevated token). The PID file path
    /// always wins on subsequent reads — readers check the recorded PID is alive
    /// before trusting the lock.
    /// </summary>
    public sealed class SingleInstanceLock : IDisposable
    {
        public bool Acquired { get; private set; }
        public int? ExistingPid { get; private set; }
        public string Key { get; }
        public string LockPath { get; }

        private Mutex _mutex;
        private bool _mutexHeld;
        // issue #38 defect #4: a Mutex has thread affinity — ReleaseMutex MUST run on the
        // thread that acquired it, or it throws SynchronizationLockException. The lock is
        // acquired on the TryAcquire thread but Dispose runs from the ProcessExit handler
        // (and worker idle-shutdown), a different thread, which is why every shutdown logged
        // "[SingleInstanceLock] mutex release: ... called from an unsynchronized block".
        // Record the owning thread so Dispose only releases when it can legally do so; on a
        // foreign thread we skip the release and let process exit reclaim the handle (any
        // waiter already handles AbandonedMutexException on the next acquire).
        private int _mutexOwnerThreadId;
        private FileStream _lockStream;
        private bool _disposed;
        private bool _sharedChild;

        private SingleInstanceLock(string key, string lockPath)
        {
            Key = key;
            LockPath = lockPath;
        }

        public static string BuildKey(string kbPath, string workerExePath)
        {
            string normKb = Normalize(kbPath);
            string normExe = Normalize(workerExePath);
            return "kb=" + normKb + "|exe=" + normExe;
        }

        private static string Normalize(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return string.Empty;
            try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return p.Trim().ToLowerInvariant(); }
        }

        public static string Hash(string key)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? string.Empty));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Try to acquire the lock. On success Acquired=true. On failure
        /// Acquired=false and ExistingPid is populated when discoverable.
        /// </summary>
        public static SingleInstanceLock TryAcquire(string kbPath, string workerExePath, string lockDir)
        {
            string key = BuildKey(kbPath, workerExePath);
            string hash = Hash(key);
            string lockPath = Path.Combine(lockDir ?? Path.GetTempPath(), ".worker-" + hash.Substring(0, 16) + ".lock");
            var inst = new SingleInstanceLock(key, lockPath);
            inst._sharedChild = IsSharedChild();

            // 1) Try Global\ mutex.
            try
            {
                bool createdNew;
                inst._mutex = new Mutex(false, "Global\\GxMcpWorker_" + hash, out createdNew);
                bool got = false;
                try { got = inst._mutex.WaitOne(0); }
                catch (AbandonedMutexException)
                {
                    // Previous owner died without releasing. We now own it.
                    got = true;
                }
                if (got)
                {
                    inst._mutexHeld = true;
                    inst._mutexOwnerThreadId = Thread.CurrentThread.ManagedThreadId;
                }
            }
            catch (Exception ex)
            {
                // UnauthorizedAccessException on the Global\ namespace under non-elevated
                // contexts is common — we fall through to the file-lock path below.
                Logger.Info("[SingleInstanceLock] Global mutex unavailable, falling back to file lock: " + ex.Message);
                try { inst._mutex?.Dispose(); } catch { }
                inst._mutex = null;
            }

            // 2) PID file (authoritative — even when mutex worked, write it so other
            //    workers can discover us; cleaned up on ProcessExit / Dispose).
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? ".");

                // If a lock file exists, check whether the owner is still alive.
                if (File.Exists(lockPath))
                {
                    int? existingPid = ReadPid(lockPath);
                    bool live = inst._sharedChild
                        ? IsSharedChildRecordLive(lockPath, existingPid)
                        : existingPid.HasValue && IsProcessAlive(existingPid.Value);
                    if (live)
                    {
                        // Another live worker owns this KB.
                        if (inst._mutexHeld)
                        {
                            // We won the mutex but the file owner is still running — that
                            // means the mutex was abandoned (process died, was restarted
                            // under same pid via OS recycle). Trust the live process.
                            try { inst._mutex.ReleaseMutex(); } catch { }
                            inst._mutexHeld = false;
                        }
                        inst.ExistingPid = existingPid;
                        inst.Acquired = false;
                        return inst;
                    }

                    // Stale lock — delete and continue.
                    try { File.Delete(lockPath); } catch { }
                }

                // Open with FileShare.Read so other workers can read the pid but cannot truncate it.
                inst._lockStream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    bufferSize: 256, options: FileOptions.DeleteOnClose);
                var payload = Encoding.UTF8.GetBytes(inst._sharedChild ? BuildSharedChildLockPayload() : BuildLockPayload());
                inst._lockStream.Write(payload, 0, payload.Length);
                inst._lockStream.Flush(true);
                inst.Acquired = true;
            }
            catch (IOException ioex)
            {
                // CreateNew failed — another worker has the file open exclusively.
                Logger.Info("[SingleInstanceLock] File lock CreateNew failed: " + ioex.Message);
                inst.ExistingPid = ReadPid(lockPath);
                if (inst._mutexHeld) { try { inst._mutex.ReleaseMutex(); } catch { } inst._mutexHeld = false; }
                inst.Acquired = false;
            }
            catch (Exception ex)
            {
                Logger.Error("[SingleInstanceLock] Unexpected file-lock error: " + ex.Message);
                if (inst._mutexHeld) { try { inst._mutex.ReleaseMutex(); } catch { } inst._mutexHeld = false; }
                inst.Acquired = false;
            }

            // Register process-exit cleanup so a crashed worker doesn't leave a stale lock.
            if (inst.Acquired)
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { inst.Dispose(); } catch { } };
            }
            return inst;
        }

        private static string BuildLockPayload()
        {
            return "{\"pid\":" + Process.GetCurrentProcess().Id +
                ",\"startedUtc\":\"" + DateTime.UtcNow.ToString("o") + "\"" +
                ",\"exe\":\"" + (Process.GetCurrentProcess().MainModule?.FileName ?? "").Replace("\\", "\\\\") + "\"}";
        }

        private const string SharedChildVariable = "GXMCP_SHARED_CHILD";

        // Read at acquisition time: a Worker_Host starts its Worker child with GXMCP_SHARED_CHILD=1.
        private static bool IsSharedChild()
        {
            return string.Equals(Environment.GetEnvironmentVariable(SharedChildVariable), "1", StringComparison.Ordinal);
        }

        // Shared-child payload: the Isolated_Mode fields plus the holder's process start time, so
        // a reused PID is not mistaken for the original holder.
        private static string BuildSharedChildLockPayload()
        {
            using (var self = Process.GetCurrentProcess())
            {
                long startTicks = self.StartTime.ToUniversalTime().Ticks;
                return "{\"pid\":" + self.Id +
                    ",\"startedUtc\":\"" + DateTime.UtcNow.ToString("o") + "\"" +
                    ",\"processStartUtcTicks\":" + startTicks.ToString(CultureInfo.InvariantCulture) +
                    ",\"exe\":\"" + (self.MainModule?.FileName ?? "").Replace("\\", "\\\\") + "\"}";
            }
        }

        /// <summary>
        /// Shared-child liveness: the recorded PID is running AND that process started at the
        /// recorded time (millisecond precision). A record without a start time (written by an
        /// Isolated_Mode worker) falls back to PID-only liveness, and an unreadable start time of
        /// a running PID counts as live, so uncertainty refuses instead of overwriting.
        /// </summary>
        private static bool IsSharedChildRecordLive(string lockPath, int? pid)
        {
            if (!pid.HasValue || pid.Value <= 0) return false;
            long? recordedTicks = ReadLongField(lockPath, "processStartUtcTicks");
            try
            {
                using (var p = Process.GetProcessById(pid.Value))
                {
                    if (p.HasExited) return false;
                    if (!recordedTicks.HasValue) return true;
                    long actualTicks;
                    try { actualTicks = p.StartTime.ToUniversalTime().Ticks; }
                    catch { return true; }
                    return actualTicks / TimeSpan.TicksPerMillisecond == recordedTicks.Value / TimeSpan.TicksPerMillisecond;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch { return true; }
        }

        private static long? ReadLongField(string lockPath, string field)
        {
            try
            {
                using (var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    int idx = text.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
                    if (idx < 0) return null;
                    int colon = text.IndexOf(':', idx);
                    if (colon < 0) return null;
                    var sb = new StringBuilder();
                    for (int i = colon + 1; i < text.Length; i++)
                    {
                        char c = text[i];
                        if (c >= '0' && c <= '9') sb.Append(c);
                        else if (sb.Length > 0 || !char.IsWhiteSpace(c)) break;
                    }
                    long value;
                    if (sb.Length == 0 || !long.TryParse(sb.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return null;
                    return value;
                }
            }
            catch { return null; }
        }

        private static int? ReadPid(string lockPath)
        {
            try
            {
                // ReadAllText cannot open a file held with FileShare.Read for write — open
                // explicitly with permissive sharing.
                using (var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    int idx = text.IndexOf("\"pid\"", StringComparison.Ordinal);
                    if (idx < 0) return null;
                    int colon = text.IndexOf(':', idx);
                    if (colon < 0) return null;
                    var sb = new StringBuilder();
                    for (int i = colon + 1; i < text.Length; i++)
                    {
                        char c = text[i];
                        if (char.IsDigit(c)) sb.Append(c);
                        else if (sb.Length > 0) break;
                    }
                    if (sb.Length == 0) return null;
                    return int.Parse(sb.ToString());
                }
            }
            catch { return null; }
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid)) { return !p.HasExited; }
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_lockStream != null)
                {
                    // DeleteOnClose handles file removal; explicitly Dispose to release the
                    // handle so a respawn can claim the same path immediately.
                    _lockStream.Dispose();
                    _lockStream = null;
                }
            }
            catch (Exception ex) { Logger.Error("[SingleInstanceLock] lock stream dispose: " + ex.Message); }

            try
            {
                if (_mutexHeld)
                {
                    // Only the acquiring thread may ReleaseMutex. When Dispose runs on a
                    // foreign thread (ProcessExit / idle-shutdown) skip the release — the OS
                    // reclaims the mutex on process exit and waiters treat it as abandoned.
                    if (Thread.CurrentThread.ManagedThreadId == _mutexOwnerThreadId)
                    {
                        _mutex.ReleaseMutex();
                    }
                    _mutexHeld = false;
                }
            }
            catch (Exception ex) { Logger.Error("[SingleInstanceLock] mutex release: " + ex.Message); }

            try { _mutex?.Dispose(); } catch { }

            // Final belt-and-suspenders cleanup — DeleteOnClose should already have removed it.
            // Requirement 7.10: a refused shared child never owned the file, so it must leave the
            // holder's record in place. Isolated_Mode keeps its historical cleanup.
            if (_sharedChild && !Acquired) return;
            try { if (File.Exists(LockPath)) File.Delete(LockPath); } catch { }
        }
    }
}
