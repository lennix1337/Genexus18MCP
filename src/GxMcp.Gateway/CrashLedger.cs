using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Durable, append-only record of every worker death.
    //
    // Motivation: worker_debug.log is renamed to .prev.log on EVERY worker start
    // (Logger's static ctor), so at most two sessions of crash forensics survive and
    // history is destroyed on the next spawn. The worker also emits a rich
    // [WORKER-CRASH] line, but only for CATCHABLE exceptions — the SDK's scary deaths
    // (AccessViolation / StackOverflow) are uncatchable on net48 and never reach that
    // handler. This ledger is written by the GATEWAY, which observes EVERY worker exit
    // (reason + OS exit code + memory-at-death + the tool that was last running),
    // including the uncatchable ones. It outlives worker restarts so death causes are
    // measured, not guessed. Ring-capped so it can never grow without bound.
    public static class CrashLedger
    {
        private static readonly object _lock = new object();

        // Keep the last N death records. A death is a handful of small fields, so 500
        // lines is a few hundred KB — enough for weeks of real usage, trivially cheap
        // to read whole on each write and on Summarize.
        internal const int MaxRecords = 500;

        private static string? _pathOverride;

        // Test seam: redirect the ledger to a scratch file.
        internal static void SetPathForTest(string? path)
        {
            lock (_lock) { _pathOverride = path; }
        }

        private static string LedgerPath
        {
            get
            {
                if (_pathOverride != null) return _pathOverride;
                string? scoped = Environment.GetEnvironmentVariable("GXMCP_CRASH_LEDGER_PATH");
                if (!string.IsNullOrWhiteSpace(scoped)) return scoped;
                string baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                                 ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(baseDir, "GenexusMCP", "worker-crashes.jsonl");
            }
        }

        internal static string ResolveScopedPath(StateScopeId scopeId, string kbId, long generation)
        {
            var scope = StateScope.Create(id: scopeId);
            return scope.LogsPath(kbId, generation, "crash-ledger.jsonl");
        }

        internal static bool IsKbContextMatch(string expected, string candidate)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(candidate)) return false;
            try
            {
                string left = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string right = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // A worker exit is "unexpected" (i.e. a real death worth investigating) when it
        // was NOT one of the planned lifecycle transitions. Idle reaping, gateway
        // shutdown, explicit close, planned reload and single-instance busy-reject are
        // all deliberate. Reason=None (an unobserved crash) and Wedged (force-reaped
        // hung worker) are the ones that hurt.
        public static bool IsUnexpected(WorkerStopReason reason, int? exitCode)
        {
            switch (reason)
            {
                case WorkerStopReason.IdleTimeout:
                case WorkerStopReason.GatewayShutdown:
                case WorkerStopReason.BusyReject:
                case WorkerStopReason.ExplicitClose:
                case WorkerStopReason.CapacityEviction:
                case WorkerStopReason.PlannedReload:
                case WorkerStopReason.HeapRecycle:
                case WorkerStopReason.SdkCompatibilityRejected:
                    return false;
                default:
                    // None / Wedged. A clean exit code 0 with reason None is a benign
                    // stdin-EOF shutdown (client disconnected); anything else is a crash.
                    if (reason == WorkerStopReason.None && exitCode == 0) return false;
                    return true;
            }
        }

        public static void Record(
            string kbAlias,
            WorkerStopReason reason,
            int? exitCode,
            int? pid,
            double? uptimeSec,
            long? lastWorkingSetBytes,
            string? lastOperation,
            long? spawnMs,
            long? sdkInitMs,
            bool sdkReady,
            string? ledgerPath = null)
        {
            try
            {
                var rec = new JObject
                {
                    ["atUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["kb"] = kbAlias ?? "",
                    ["reason"] = reason.ToString(),
                    ["unexpected"] = IsUnexpected(reason, exitCode),
                    ["exitCode"] = exitCode.HasValue ? new JValue(exitCode.Value) : JValue.CreateNull(),
                    ["pid"] = pid.HasValue ? new JValue(pid.Value) : JValue.CreateNull(),
                    ["uptimeSec"] = uptimeSec.HasValue ? new JValue(Math.Round(uptimeSec.Value, 1)) : JValue.CreateNull(),
                    ["memMB"] = lastWorkingSetBytes.HasValue ? new JValue(lastWorkingSetBytes.Value / (1024 * 1024)) : JValue.CreateNull(),
                    ["lastOp"] = lastOperation ?? "",
                    ["spawnMs"] = spawnMs.HasValue ? new JValue(spawnMs.Value) : JValue.CreateNull(),
                    ["sdkInitMs"] = sdkInitMs.HasValue ? new JValue(sdkInitMs.Value) : JValue.CreateNull(),
                    ["sdkReady"] = sdkReady
                };

                string line = rec.ToString(Formatting.None);

                lock (_lock)
                {
                    string path = ledgerPath ?? LedgerPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    // Single-writer (gateway only) so a plain append is safe. Trim to the
                    // ring cap when the file overshoots, amortised so most writes are a
                    // bare append rather than a full rewrite.
                    File.AppendAllText(path, line + Environment.NewLine);

                    var lines = File.ReadAllLines(path);
                    if (lines.Length > MaxRecords + 100)
                    {
                        File.WriteAllLines(path, lines.Skip(lines.Length - MaxRecords));
                    }
                }
            }
            catch
            {
                // Forensics are best-effort — a full disk / locked file must never
                // interfere with the worker-exit handling that calls this.
            }
        }

        // Aggregated view for whoami / doctor: how many deaths, how many were real
        // (unexpected), a breakdown by reason and exit code, and the most recent few.
        public static JObject Summarize(int recentN = 5)
        {
            var summary = new JObject
            {
                ["total"] = 0,
                ["unexpected"] = 0
            };
            try
            {
                string path = LedgerPath;
                string[] lines;
                lock (_lock)
                {
                    if (!File.Exists(path)) return summary;
                    lines = File.ReadAllLines(path);
                }

                var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
                var byExit = new Dictionary<string, int>(StringComparer.Ordinal);
                int unexpected = 0;
                var recent = new List<JObject>();
                string? lastUnexpectedAt = null;

                foreach (var l in lines)
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    JObject rec;
                    try { rec = JObject.Parse(l); } catch { continue; }

                    string reason = rec["reason"]?.ToString() ?? "Unknown";
                    byReason[reason] = byReason.TryGetValue(reason, out var rc) ? rc + 1 : 1;

                    string exit = rec["exitCode"]?.Type == JTokenType.Null || rec["exitCode"] == null
                        ? "null" : rec["exitCode"]!.ToString();
                    byExit[exit] = byExit.TryGetValue(exit, out var ec) ? ec + 1 : 1;

                    if (rec["unexpected"]?.ToObject<bool?>() == true)
                    {
                        unexpected++;
                        lastUnexpectedAt = rec["atUtc"]?.ToString() ?? lastUnexpectedAt;
                    }
                }

                int take = Math.Max(0, recentN);
                recent = lines
                    .Reverse()
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Take(take)
                    .Select(l => { try { return JObject.Parse(l); } catch { return null; } })
                    .Where(o => o != null)
                    .Cast<JObject>()
                    .ToList();

                summary["total"] = lines.Count(l => !string.IsNullOrWhiteSpace(l));
                summary["unexpected"] = unexpected;
                if (lastUnexpectedAt != null) summary["lastUnexpectedAtUtc"] = lastUnexpectedAt;
                summary["byReason"] = JObject.FromObject(byReason);
                summary["byExitCode"] = JObject.FromObject(byExit);
                summary["recent"] = new JArray(recent);
            }
            catch (Exception ex)
            {
                summary["error"] = ex.Message;
            }
            return summary;
        }
    }
}
