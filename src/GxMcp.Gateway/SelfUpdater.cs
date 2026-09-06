using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Squirrel-style self-update for the corporate fixed-path install (the one the
    // npx `@latest` launcher can't auto-update). Two halves:
    //
    //   1. MaybeStageAsync — background download of the new publish.zip into a
    //      `.staged/` folder next to the install, verified by SHA-256.
    //   2. ApplyStagedUpdateOnStartup — at the next gateway launch, swap the staged
    //      files over the live ones. Windows won't let a running exe overwrite
    //      itself, so we rename the running gateway exe out of the way (allowed
    //      while running) and move the staged one in; the current session keeps
    //      running the old in-memory code and the NEXT launch is the new binary.
    //
    // Only activates for a MANAGED install (a version.txt sits next to the exe,
    // written by scripts/install.ps1). An npx-cache launch has no version.txt and
    // is skipped — npx already fetches @latest on each start. Disable entirely with
    // GENEXUS_MCP_NO_SELF_UPDATE=1. Everything is best-effort and fail-safe: any
    // error logs and leaves the install untouched (or rolls back) — it must never
    // crash the gateway or leave a half-applied state.
    internal static class SelfUpdater
    {
        private const string Repo = "lennix1337/Genexus18MCP";
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

        private static bool Disabled =>
            Environment.GetEnvironmentVariable("GENEXUS_MCP_NO_SELF_UPDATE") == "1";

        private static string InstallDir
        {
            get
            {
                try
                {
                    string? exe = Environment.ProcessPath
                        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    string? dir = exe != null ? Path.GetDirectoryName(exe) : null;
                    return dir ?? AppContext.BaseDirectory;
                }
                catch { return AppContext.BaseDirectory; }
            }
        }

        private static string GatewayExeName => "GxMcp.Gateway.exe";
        private static string VersionFile => Path.Combine(InstallDir, "version.txt");
        private static string StagedDir => Path.Combine(InstallDir, ".staged");
        private static string StagedTmpDir => Path.Combine(InstallDir, ".staged.tmp");
        private static string StagedMarker => Path.Combine(StagedDir, "staged.json");

        // string.GetHashCode é randomizado por processo no runtime — dois gateways
        // derivariam nomes de mutex diferentes e ambos poderiam aplicar o update
        // simultaneamente. SHA-256 do caminho é estável entre processos.
        private static string MutexName =>
            @"Global\GenexusMCP-SelfUpdate-" + Sha256Hex(System.Text.Encoding.UTF8.GetBytes(InstallDir.ToLowerInvariant())).Substring(0, 24);

        // A managed install is one materialized by scripts/install.ps1 (version.txt
        // present next to the gateway exe). npx-cache launches have neither.
        public static bool IsManagedInstall()
        {
            try { return File.Exists(VersionFile) && File.Exists(Path.Combine(InstallDir, GatewayExeName)); }
            catch { return false; }
        }

        private static string? CurrentInstalledVersion()
        {
            try { return File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim().TrimStart('v', 'V') : null; }
            catch { return null; }
        }

        private static string? StagedVersion()
        {
            try
            {
                if (!File.Exists(StagedMarker)) return null;
                var j = JObject.Parse(File.ReadAllText(StagedMarker));
                string? v = j["version"]?.ToString();
                return string.IsNullOrEmpty(v) ? null : v!.TrimStart('v', 'V');
            }
            catch { return null; }
        }

        /// <summary>
        /// For genexus_whoami.update — reports whether an update is staged and waiting
        /// for the next restart. Returns null when nothing is staged.
        /// </summary>
        public static JObject? GetStagedStatusSync()
        {
            try
            {
                if (!IsManagedInstall()) return null;
                string? staged = StagedVersion();
                if (string.IsNullOrEmpty(staged)) return null;
                return new JObject
                {
                    ["stagedVersion"] = staged,
                    ["appliesOn"] = "next restart",
                    ["managedInstall"] = true
                };
            }
            catch { return null; }
        }

        // ── Apply ────────────────────────────────────────────────────────────────

        public static void ApplyStagedUpdateOnStartup()
        {
            if (Disabled) return;
            try
            {
                if (!IsManagedInstall()) return;

                // Quick pre-check before taking the mutex (cheap early-out).
                string? staged = StagedVersion();
                if (string.IsNullOrEmpty(staged)) { SweepOld(InstallDir); return; }

                bool created;
                using var mutex = new Mutex(false, MutexName, out created);
                bool held = false;
                try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return; // another process is applying — let it
                try
                {
                    // SweepOld is intentionally NOT run before ApplyStagedUpdate: it deletes
                    // the *.old-* backups, which are the only recovery path if the apply
                    // fails mid-swap. Sweep only AFTER a successful apply (below); the
                    // early-out above (nothing staged) keeps its own sweep - safe there.
                    if (ApplyStagedUpdate(InstallDir))
                    {
                        Program.Log($"[SelfUpdate] Applied staged update -> v{StagedVersionWasApplied}. Active session continues on the previous version; the new binary loads on next launch.");
                        SweepOld(InstallDir); // clear *.old-* leftovers now that the apply succeeded
                    }
                }
                finally { try { mutex.ReleaseMutex(); } catch { } }
            }
            catch (Exception ex)
            {
                Program.Log($"[SelfUpdate] apply skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Set by ApplyStagedUpdate for the startup log line; not load-bearing.
        private static string? StagedVersionWasApplied;

        // Testable core: gate on version + payload validity, then swap the files in
        // <installDir>/.staged over <installDir>. installDir is a parameter so this
        // can be exercised against a temp dir without touching the real install or
        // needing a running gateway. Returns true if a swap was applied.
        internal static bool ApplyStagedUpdate(string installDir)
        {
            string stagedRoot = Path.Combine(installDir, ".staged");
            string versionFile = Path.Combine(installDir, "version.txt");
            string marker = Path.Combine(stagedRoot, "staged.json");
            if (!File.Exists(marker)) return false;

            string? staged = ReadMarkerVersion(marker);
            if (string.IsNullOrEmpty(staged)) { TryDeleteDir(stagedRoot); return false; }

            string? current = ReadVersionFile(versionFile);
            if (current != null && UpdateNotifier.CompareSemver(staged!, current) <= 0)
            {
                TryDeleteDir(stagedRoot); // stale staged (already at/again this version)
                return false;
            }
            if (!StagedPayloadValid(stagedRoot)) { TryDeleteDir(stagedRoot); return false; }

            if (!ApplyStagedFiles(stagedRoot, installDir, staged!)) return false;
            try { File.WriteAllText(versionFile, "v" + staged!.TrimStart('v', 'V')); } catch { }
            TryDeleteDir(stagedRoot);
            StagedVersionWasApplied = staged;
            return true;
        }

        // Returns true if the swap completed. Conservative: pre-flights every target
        // EXCEPT the gateway exe (renamed last; rename-self reliably handles the
        // running binary). If any other file is locked (e.g. a worker from another
        // open client), aborts WITHOUT touching anything and retries next launch.
        private static bool ApplyStagedFiles(string stagedRoot, string installDir, string stagedVersion)
        {
            var plan = Directory.GetFiles(stagedRoot, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "staged.json", StringComparison.OrdinalIgnoreCase))
                .Select(src => new { Src = src, Dst = Path.Combine(installDir, GetRelative(stagedRoot, src)) })
                .ToList();

            string gatewayTarget = Path.Combine(installDir, GatewayExeName);

            // Pre-flight: confirm every existing non-gateway target is replaceable.
            foreach (var p in plan)
            {
                if (PathEquals(p.Dst, gatewayTarget)) continue;
                if (File.Exists(p.Dst) && !IsRenamable(p.Dst))
                {
                    Program.Log($"[SelfUpdate] target locked, aborting apply (will retry): {p.Dst}");
                    return false;
                }
            }

            // Apply non-gateway files first, then the gateway exe last.
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            foreach (var p in plan)
            {
                if (PathEquals(p.Dst, gatewayTarget)) continue;
                ReplaceFile(p.Src, p.Dst, stamp);
            }
            var gw = plan.FirstOrDefault(p => PathEquals(p.Dst, gatewayTarget));
            if (gw != null) ReplaceFile(gw.Src, gw.Dst, stamp);
            return true;
        }

        private static string? ReadMarkerVersion(string marker)
        {
            try
            {
                var j = JObject.Parse(File.ReadAllText(marker));
                string? v = j["version"]?.ToString();
                return string.IsNullOrEmpty(v) ? null : v!.TrimStart('v', 'V');
            }
            catch { return null; }
        }

        private static string? ReadVersionFile(string versionFile)
        {
            try { return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim().TrimStart('v', 'V') : null; }
            catch { return null; }
        }

        // Move src over dst, renaming an existing dst out of the way first (works even
        // when dst is the running exe — Windows allows renaming an in-use file).
        internal static void ReplaceFile(string src, string dst, string stamp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            string? backup = null;
            if (File.Exists(dst))
            {
                backup = dst + ".old-" + stamp;
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                File.Move(dst, backup);
            }
            try
            {
                File.Move(src, dst);
            }
            catch
            {
                // Rollback: without dst in place the install is left with a MISSING
                // binary (worse than a stale one). Restore the backup before rethrowing.
                if (backup != null && !File.Exists(dst) && File.Exists(backup))
                {
                    try { File.Move(backup, dst); } catch { }
                }
                throw;
            }
        }

        private static bool IsRenamable(string path)
        {
            string probe = path + ".swap-test";
            try
            {
                if (File.Exists(probe)) { try { File.Delete(probe); } catch { } }
                File.Move(path, probe);
                File.Move(probe, path);
                return true;
            }
            catch
            {
                // Best effort to restore if the first move succeeded but the second didn't.
                try { if (File.Exists(probe) && !File.Exists(path)) File.Move(probe, path); } catch { }
                return false;
            }
        }

        private static void SweepOld(string installDir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(installDir, "*.old-*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { /* still locked — try again later */ }
                }
            }
            catch { }
        }

        // ── Stage ──────────────────────────────────────────────────────────────

        public static async Task MaybeStageAsync(string latestVersion)
        {
            if (Disabled || string.IsNullOrEmpty(latestVersion)) return;
            try
            {
                if (!IsManagedInstall()) return;
                string? current = CurrentInstalledVersion();
                if (current != null && UpdateNotifier.CompareSemver(latestVersion, current) <= 0) return;

                string? alreadyStaged = StagedVersion();
                if (alreadyStaged != null && UpdateNotifier.CompareSemver(alreadyStaged, latestVersion) >= 0) return; // already staged this (or newer)

                string v = latestVersion.TrimStart('v', 'V');
                string zipUrl = $"https://github.com/{Repo}/releases/download/v{v}/publish.zip";
                string shaUrl = zipUrl + ".sha256";

                using var http = new HttpClient { Timeout = DownloadTimeout };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("genexus-mcp-gateway");

                byte[] zipBytes;
                try { zipBytes = await http.GetByteArrayAsync(zipUrl); }
                catch (Exception ex) { Program.Log($"[SelfUpdate] download failed: {ex.Message}"); return; }

                // Verify SHA-256 if the release publishes a checksum; else fall back to
                // structural validation only (with a logged warning).
                string? expected = await TryGetChecksumAsync(http, shaUrl);
                if (expected != null)
                {
                    string actual = Sha256Hex(zipBytes);
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        Program.Log($"[SelfUpdate] checksum MISMATCH for v{v} (expected {expected}, got {actual}) — refusing to stage.");
                        return;
                    }
                }
                else
                {
                    Program.Log($"[SelfUpdate] no publish.zip.sha256 for v{v} — staging after structural validation only.");
                }

                // Extract to .staged.tmp, validate, then atomically swap to .staged.
                TryDeleteDir(StagedTmpDir);
                Directory.CreateDirectory(StagedTmpDir);
                string tmpZip = Path.Combine(StagedTmpDir, "publish.zip");
                await File.WriteAllBytesAsync(tmpZip, zipBytes);
                ZipFile.ExtractToDirectory(tmpZip, StagedTmpDir, overwriteFiles: true);
                try { File.Delete(tmpZip); } catch { }

                if (!StagedPayloadValid(StagedTmpDir))
                {
                    Program.Log($"[SelfUpdate] staged payload for v{v} failed validation — discarding.");
                    TryDeleteDir(StagedTmpDir);
                    return;
                }

                File.WriteAllText(Path.Combine(StagedTmpDir, "staged.json"),
                    new JObject { ["version"] = v, ["stagedAt"] = DateTime.UtcNow.ToString("o") }.ToString());

                TryDeleteDir(StagedDir);
                Directory.Move(StagedTmpDir, StagedDir);
                Program.Log($"[SelfUpdate] staged v{v}; it will be applied on the next gateway launch.");
            }
            catch (Exception ex)
            {
                Program.Log($"[SelfUpdate] staging skipped: {ex.GetType().Name}: {ex.Message}");
                TryDeleteDir(StagedTmpDir);
            }
        }

        private static async Task<string?> TryGetChecksumAsync(HttpClient http, string shaUrl)
        {
            try
            {
                var resp = await http.GetAsync(shaUrl);
                if (!resp.IsSuccessStatusCode) return null;
                string body = (await resp.Content.ReadAsStringAsync()).Trim();
                // Accept either "<hex>" or "<hex>  filename" (sha256sum format).
                string token = body.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : null;
            }
            catch { return null; }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // The staged tree must contain the two binaries the install asserts on.
        internal static bool StagedPayloadValid(string root)
        {
            try
            {
                return File.Exists(Path.Combine(root, GatewayExeName))
                    && File.Exists(Path.Combine(root, "worker", "GxMcp.Worker.exe"));
            }
            catch { return false; }
        }

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        private static string GetRelative(string root, string full)
        {
            string rel = full.Substring(root.Length);
            return rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool PathEquals(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
