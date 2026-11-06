using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Incremental SDK text-tree mirror. Filesystem/timer callbacks only enqueue
    /// metadata; all SDK reads and projections run through the Worker's owning STA.
    /// </summary>
    public sealed partial class TextMirrorService : IDisposable
    {
        private const string WatermarkFileName = ".mirror-sync";
        private readonly object _gate = new object();
        private readonly KbService _kbService;
        private readonly ObjectTextService _objectTextService;
        private readonly IndexCacheService _indexCacheService;
        private readonly Dictionary<string, PendingChange> _pending = new Dictionary<string, PendingChange>(StringComparer.OrdinalIgnoreCase);
        private Timer _timer;
        private bool _running;
        private bool _catchupQueued;
        private bool _fullCatchupRequested;
        private string _root;
        private int _intervalMs = 1000;
        private bool _includeReferences;
        private long _processedChanges;
        private long _processedBatches;
        private long _removedFiles;
        private DateTime? _lastTickUtc;
        private int _lastBatchSize;
        private string _lastError;
        private DateTime? _watermarkUtc;

        public TextMirrorService(
            KbService kbService,
            ObjectTextService objectTextService,
            IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _objectTextService = objectTextService;
            _indexCacheService = indexCacheService;
        }

        public string Run(string action, JObject args)
        {
            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "start": return Start(args ?? new JObject());
                case "stop": return Stop();
                case "status": return Status();
                case "catchup": return Catchup(args ?? new JObject());
                case "set_reference_export_enabled": return SetReferenceExport(args ?? new JObject());
                default: return McpResponse.Err(code: "UnknownTextMirrorAction", message: "Unknown text mirror action: " + action, hint: "Use start, stop, status, catchup or set_reference_export_enabled.");
            }
        }

        public void NotifyObjectChanged(string name, string type, DateTime updatedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (_gate)
            {
                if (!_running) return;
                string key = (type ?? string.Empty) + ":" + name;
                _pending[key] = new PendingChange { Name = name, Type = type, UpdatedAtUtc = updatedAtUtc.ToUniversalTime() };
                ScheduleTimerNoLock(_intervalMs);
            }
        }

        public void NotifyObjectDeleted(string name, string type, DateTime deletedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (_gate)
            {
                if (!_running) return;
                string key = (type ?? string.Empty) + ":" + name;
                _pending[key] = new PendingChange
                {
                    Name = name,
                    Type = type,
                    UpdatedAtUtc = deletedAtUtc.ToUniversalTime(),
                    Deleted = true
                };
                ScheduleTimerNoLock(_intervalMs);
            }
        }

        private string Start(JObject args)
        {
            string outputPath = args["outputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(outputPath))
                return McpResponse.Err(code: "OutputPathRequired", message: "outputPath is required to start the text mirror.", hint: "Choose a directory owned by the mirror for the src/ref tree.");

            string root;
            try { root = Path.GetFullPath(outputPath); }
            catch (Exception ex) { return McpResponse.Err(code: "InvalidOutputPath", message: ex.Message, target: outputPath); }
            try
            {
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            }
            catch (Exception ex) { return McpResponse.Err(code: "MirrorDirectoryCreateFailed", message: ex.Message, target: root); }

            int interval = args["intervalMs"]?.ToObject<int?>() ?? 1000;
            interval = Math.Max(250, Math.Min(600000, interval));
            bool includeReferences = args["includeReferences"]?.ToObject<bool?>()
                ?? args["exportReferences"]?.ToObject<bool?>()
                ?? false;
            bool initialSync = args["initialSync"]?.ToObject<bool?>() ?? false;
            lock (_gate)
            {
                if (_running && string.Equals(_root, root, StringComparison.OrdinalIgnoreCase))
                    return McpResponse.Ok(code: "TextMirrorAlreadyRunning", result: SnapshotNoLock());
                if (_running) StopNoLock();
                _root = root;
                _intervalMs = interval;
                _includeReferences = includeReferences;
                _lastError = null;
                LoadWatermarkNoLock();
                _running = true;
                _fullCatchupRequested = initialSync;
                _timer = new Timer(_ => QueueCatchup(), null, Timeout.Infinite, Timeout.Infinite);
                if (initialSync) ScheduleTimerNoLock(0);
                return McpResponse.Ok(code: "TextMirrorStarted", result: SnapshotNoLock());
            }
        }

        private string Stop()
        {
            lock (_gate)
            {
                bool wasRunning = _running;
                StopNoLock();
                return McpResponse.Ok(code: wasRunning ? "TextMirrorStopped" : "TextMirrorAlreadyStopped", result: SnapshotNoLock());
            }
        }

        private void StopNoLock()
        {
            _running = false;
            _catchupQueued = false;
            _fullCatchupRequested = false;
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            _pending.Clear();
        }

        private string Status()
        {
            lock (_gate) return McpResponse.Ok(code: "TextMirrorStatus", result: SnapshotNoLock());
        }

        private string SetReferenceExport(JObject args)
        {
            JToken value = args["enabled"] ?? args["includeReferences"] ?? args["exportReferences"];
            if (value == null) return McpResponse.Err(code: "EnabledRequired", message: "enabled is required for set_reference_export_enabled.");
            lock (_gate)
            {
                _includeReferences = value.ToObject<bool>();
                return McpResponse.Ok(code: "TextMirrorReferenceExportUpdated", result: SnapshotNoLock());
            }
        }

        private string Catchup(JObject args)
        {
            bool full = args["full"]?.ToObject<bool?>() ?? false;
            lock (_gate)
            {
                if (!_running) return McpResponse.Err(code: "TextMirrorNotRunning", message: "The text mirror is not running.", hint: "Start it with outputPath before requesting catchup.");
            }
            return CatchupOnSdk(full, args);
        }

        private void ScheduleTimerNoLock(int dueMs)
        {
            try { _timer?.Change(Math.Max(0, dueMs), Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private void QueueCatchup()
        {
            lock (_gate)
            {
                if (!_running || _catchupQueued) return;
                _catchupQueued = true;
            }

            bool queued = false;
            try { queued = Program.EnqueueSdkAction(() => RunQueuedCatchup(), "text-mirror-catchup"); }
            catch (Exception ex)
            {
                lock (_gate) { _lastError = ex.Message; _catchupQueued = false; }
            }
            if (!queued)
            {
                lock (_gate)
                {
                    _lastError = "The SDK action queue is full; mirror catchup will retry on the next notification.";
                    _catchupQueued = false;
                }
            }
        }

        private void RunQueuedCatchup()
        {
            bool full;
            lock (_gate)
            {
                full = _fullCatchupRequested;
                _fullCatchupRequested = false;
            }
            try { CatchupOnSdk(full, new JObject()); }
            catch (Exception ex) { lock (_gate) { _lastError = ex.Message; } }
            finally { lock (_gate) { _catchupQueued = false; } }
        }

        private string CatchupOnSdk(bool full, JObject overrides)
            => RunMirrorCatchup(full, overrides ?? new JObject());

        private void LoadWatermarkNoLock()
        {
            _watermarkUtc = null;
            string path = Path.Combine(_root, WatermarkFileName);
            try
            {
                if (!File.Exists(path)) return;
                JObject value = JObject.Parse(File.ReadAllText(path));
                _watermarkUtc = value["lastProcessedUtc"]?.ToObject<DateTime?>();
                _processedChanges = value["processedChanges"]?.ToObject<long?>() ?? _processedChanges;
                _processedBatches = value["processedBatches"]?.ToObject<long?>() ?? _processedBatches;
                _removedFiles = value["removedFiles"]?.ToObject<long?>() ?? _removedFiles;
            }
            catch (Exception ex) { _lastError = "Watermark read failed: " + ex.Message; }
        }

        private void PersistWatermarkNoLock()
        {
            if (string.IsNullOrWhiteSpace(_root)) return;
            try
            {
                string path = Path.Combine(_root, WatermarkFileName);
                string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                var value = new JObject
                {
                    ["schemaVersion"] = 1,
                    ["lastProcessedUtc"] = _watermarkUtc,
                    ["processedChanges"] = _processedChanges,
                    ["processedBatches"] = _processedBatches,
                    ["removedFiles"] = _removedFiles
                };
                File.WriteAllText(temporary, value.ToString(Formatting.Indented));
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch { File.Delete(path); File.Move(temporary, path); }
                }
                else File.Move(temporary, path);
            }
            catch (Exception ex) { _lastError = "Watermark write failed: " + ex.Message; }
        }

        private JObject Snapshot()
        {
            lock (_gate) return SnapshotNoLock();
        }

        private JObject SnapshotNoLock()
        {
            return new JObject
            {
                ["running"] = _running,
                ["root"] = _root,
                ["watermark"] = string.IsNullOrWhiteSpace(_root) ? null : Path.Combine(_root, WatermarkFileName),
                ["includeReferences"] = _includeReferences,
                ["intervalMs"] = _intervalMs,
                ["pendingChanges"] = _pending.Count,
                ["pendingDeletions"] = _pending.Values.Count(change => change.Deleted),
                ["processedChanges"] = _processedChanges,
                ["processedBatches"] = _processedBatches,
                ["removedFiles"] = _removedFiles,
                ["lastBatchSize"] = _lastBatchSize,
                ["lastTickUtc"] = _lastTickUtc,
                ["lastProcessedUtc"] = _watermarkUtc,
                ["lastError"] = _lastError
            };
        }

        private static JObject Parse(string raw)
        {
            try { return JObject.Parse(raw ?? "{}"); }
            catch (Exception ex) { return new JObject { ["status"] = "error", ["message"] = ex.Message }; }
        }

        public void Dispose()
        {
            lock (_gate) StopNoLock();
        }

        private sealed class PendingChange
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
            public bool Deleted { get; set; }
        }
    }
}
