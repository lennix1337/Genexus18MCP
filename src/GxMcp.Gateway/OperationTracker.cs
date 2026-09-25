using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal sealed class OperationTracker
    {
        private readonly TimeSpan _retention;
        private readonly ConcurrentDictionary<string, OperationRecord> _operations = new ConcurrentDictionary<string, OperationRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _requestToOperation = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ToolMetricState> _toolMetrics = new ConcurrentDictionary<string, ToolMetricState>(StringComparer.OrdinalIgnoreCase);

        public OperationTracker(TimeSpan retention)
        {
            _retention = retention;
        }

        public string StartOperation(string requestId, string toolName, JObject? toolArguments, string correlationId)
        {
            string operationId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var record = new OperationRecord
            {
                OperationId = operationId,
                RequestId = requestId,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName,
                CorrelationId = correlationId,
                Status = "Running",
                StartedAtUtc = now,
                UpdatedAtUtc = now,
                ToolArguments = toolArguments != null ? (JObject)toolArguments.DeepClone() : null
            };

            _operations[operationId] = record;
            _requestToOperation[requestId] = operationId;
            return operationId;
        }

        // When a worker-crash retry re-sends the same operation under a fresh JSON-RPC
        // request id, the worker's completion arrives keyed by that new id. Without this
        // mapping, CompleteFromWorker's _requestToOperation lookup misses and the operation
        // record is stranded at "Running" forever even though the retry succeeded.
        public void LinkRequest(string requestId, string operationId)
        {
            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(operationId)) return;
            _requestToOperation[requestId] = operationId;
            // Remember the extra id on the record so CleanupExpired can drop it later;
            // skip the no-op self-link StartOperation already made (requestId == RequestId).
            if (_operations.TryGetValue(operationId, out var record))
            {
                lock (record.SyncRoot)
                {
                    if (!string.Equals(requestId, record.RequestId, StringComparison.OrdinalIgnoreCase))
                    {
                        (record.LinkedRequestIds ??= new List<string>()).Add(requestId);
                    }
                }
            }
        }

        public void MarkTimeout(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return;
            if (!_operations.TryGetValue(operationId, out var record)) return;

            lock (record.SyncRoot)
            {
                record.TimeoutCount++;
                record.TimedOut = true;
                record.UpdatedAtUtc = DateTime.UtcNow;
                if (record.Status == "Running")
                {
                    record.LastError = "Gateway timeout waiting for worker response.";
                }
            }

            if (_toolMetrics.TryGetValue(record.ToolName, out var metric))
            {
                metric.RegisterTimeout();
            }
        }

        internal bool TryGetOperationIdForRequest(string requestId, out string operationId)
        {
            operationId = null;
            return !string.IsNullOrWhiteSpace(requestId)
                && _requestToOperation.TryGetValue(requestId, out operationId);
        }

        public void CompleteFromWorker(string requestId, JObject workerPayload)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            if (!_requestToOperation.TryGetValue(requestId, out var operationId)) return;
            if (!_operations.TryGetValue(operationId, out var record)) return;

            DateTime now = DateTime.UtcNow;
            bool registerMetric;
            lock (record.SyncRoot)
            {
                bool isErrorEnvelope = workerPayload["error"] != null;
                var resultObj = workerPayload["result"] as JObject;
                bool isErrorStatus = string.Equals(resultObj?["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                // A5: a late worker reply must NOT resurrect a client-observed Cancelled
                // op back to Completed/Failed. The client already saw "Cancelled"; flipping
                // it here is a state-consistency wedge. Keep the terminal Cancelled status,
                // but still capture the payload/metric for diagnostics.
                if (!string.Equals(record.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                    record.Status = (isErrorEnvelope || isErrorStatus) ? "Failed" : "Completed";
                record.CompletedAtUtc = now;
                record.UpdatedAtUtc = now;
                record.WorkerPayload = workerPayload.DeepClone();
                record.LastError = isErrorEnvelope
                    ? workerPayload["error"]?.ToString()
                    : (isErrorStatus ? resultObj?["error"]?.ToString() ?? resultObj?["details"]?.ToString() : record.LastError);
                // Status still updates on a retried completion (Failed -> Completed), but the
                // metric is registered only on the first terminal transition (see MetricRegistered).
                registerMetric = !record.MetricRegistered;
                record.MetricRegistered = true;
            }

            if (!registerMetric) return;

            var metric = _toolMetrics.GetOrAdd(record.ToolName, _ => new ToolMetricState(record.ToolName));
            long elapsedMs = record.CompletedAtUtc.HasValue
                ? Math.Max(0L, (long)(record.CompletedAtUtc.Value - record.StartedAtUtc).TotalMilliseconds)
                : 0L;
            // Item 75: cheap proxy for "tokens in/out" — JSON byte length of the
            // tool arguments and the worker's full response payload. Whole-token
            // accuracy isn't worth a tokeniser dep; bytes/4 in the percentile
            // surface gives the agent a workable bound for "is my tool reply huge?".
            long reqBytes = SafeJsonLength(record.ToolArguments);
            long respBytes = SafeJsonLength(workerPayload);
            metric.RegisterCompletion(elapsedMs, string.Equals(record.Status, "Failed", StringComparison.OrdinalIgnoreCase), record.WorkerPayload, reqBytes, respBytes);
        }

        private static long SafeJsonLength(JToken? token)
        {
            if (token == null) return 0;
            try { return token.ToString(Newtonsoft.Json.Formatting.None).Length; }
            catch { return 0; }
        }

        // FR#7 (friction-report 2026-05-14): support best-effort cancellation surface on the
        // Gateway side. The worker thread is still busy on its SDK call (we can't preempt the
        // STA), but marking the op as Cancelled lets the agent stop polling and the next
        // status call will return a non-Running envelope. Returns true when the op existed.
        public bool MarkCancelled(string operationId, string reason)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return false;
            if (!_operations.TryGetValue(operationId, out var record)) return false;

            lock (record.SyncRoot)
            {
                if (string.Equals(record.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return true; // already terminal — idempotent
                }

                record.Status = "Cancelled";
                record.CompletedAtUtc = DateTime.UtcNow;
                record.UpdatedAtUtc = record.CompletedAtUtc.Value;
                record.LastError = reason ?? "Cancelled by client";
                record.MetricRegistered = true; // a later CompleteFromWorker must not re-count this op
            }

            var metric = _toolMetrics.GetOrAdd(record.ToolName, _ => new ToolMetricState(record.ToolName));
            long elapsedMs = record.CompletedAtUtc.HasValue
                ? Math.Max(0L, (long)(record.CompletedAtUtc.Value - record.StartedAtUtc).TotalMilliseconds)
                : 0L;
            metric.RegisterCompletion(elapsedMs, isError: true, workerPayload: null, reqBytes: 0, respBytes: 0);
            return true;
        }

        // SDK calls execute on a non-preemptible STA thread. For those calls, receiving a
        // cancel request is not the same as cancellation completing. Keep the operation live
        // and let CompleteFromWorker publish the truthful terminal state when the SDK returns.
        public bool MarkCancellationRequested(string operationId, string reason)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return false;
            if (!_operations.TryGetValue(operationId, out var record)) return false;

            lock (record.SyncRoot)
            {
                if (string.Equals(record.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                    return true;

                record.Status = "CancellationRequested";
                record.UpdatedAtUtc = DateTime.UtcNow;
                record.LastError = reason ?? "Cancellation requested by client; waiting for the SDK call to return.";
            }
            return true;
        }

        internal bool TryGetContext(string operationId, out string? toolName, out JObject? toolArguments)
        {
            toolName = null;
            toolArguments = null;
            if (string.IsNullOrWhiteSpace(operationId)
                || !_operations.TryGetValue(operationId, out var record))
                return false;
            lock (record.SyncRoot)
            {
                toolName = record.ToolName;
                toolArguments = record.ToolArguments != null
                    ? (JObject)record.ToolArguments.DeepClone()
                    : null;
                return true;
            }
        }

        // A4: a worker progress frame carries progressToken == operationId (see
        // SendWorkerCommandAsync). Bump the record's UpdatedAtUtc so a status poll
        // (genexus_lifecycle action=status target=op:<id>) shows real liveness
        // instead of a frozen timestamp for the whole run. Optional phase/message
        // are surfaced for the poller. No-op once terminal.
        public void TouchProgress(string operationId, string? phase = null, string? message = null)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return;
            if (!_operations.TryGetValue(operationId, out var record)) return;
            lock (record.SyncRoot)
            {
                if (!string.Equals(record.Status, "Running", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(record.Status, "Accepted", StringComparison.OrdinalIgnoreCase))
                    return;
                record.UpdatedAtUtc = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(phase)) record.Phase = phase;
                if (!string.IsNullOrEmpty(message)) record.LastProgressMessage = message;
            }
        }

        // issue #44 — a worker progress frame carries progressToken == operationId. It must only
        // be relayed to the client while that operation is still live (Running/Accepted). Async
        // builds keep emitting "Build phase: OpeningKB" long after their wrapping RPC returned
        // Accepted (which retired the client's progressToken); relaying those, an unknown
        // literal token (e.g. bulk-index), or a token for an expired/terminal op makes the client
        // (Cursor) error the transport with "progress notification for an unknown token" and drop
        // the connection. Returns true only when the op is tracked AND not yet terminal.
        public bool IsProgressTokenActive(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return false;
            if (!_operations.TryGetValue(operationId, out var record)) return false;
            lock (record.SyncRoot)
            {
                return string.Equals(record.Status, "Running", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(record.Status, "Accepted", StringComparison.OrdinalIgnoreCase);
            }
        }

        public void MarkFailedByRequest(string requestId, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            if (!_requestToOperation.TryGetValue(requestId, out var operationId)) return;
            if (!_operations.TryGetValue(operationId, out var record)) return;

            bool registerMetric;
            lock (record.SyncRoot)
            {
                record.Status = "Failed";
                record.CompletedAtUtc = DateTime.UtcNow;
                record.UpdatedAtUtc = record.CompletedAtUtc.Value;
                record.LastError = errorMessage;
                registerMetric = !record.MetricRegistered;
                record.MetricRegistered = true;
            }

            if (!registerMetric) return;

            var metric = _toolMetrics.GetOrAdd(record.ToolName, _ => new ToolMetricState(record.ToolName));
            long elapsedMs = record.CompletedAtUtc.HasValue
                ? Math.Max(0L, (long)(record.CompletedAtUtc.Value - record.StartedAtUtc).TotalMilliseconds)
                : 0L;
            metric.RegisterCompletion(elapsedMs, isError: true, workerPayload: null, reqBytes: 0, respBytes: 0);
        }

        public JObject BuildOperationStatus(string operationId)
        {
            if (!_operations.TryGetValue(operationId, out var record))
            {
                return new JObject
                {
                    ["status"] = "NotFound",
                    ["operationId"] = operationId,
                    ["message"] = "Operation not found or expired."
                };
            }

            lock (record.SyncRoot)
            {
                var status = new JObject
                {
                    ["status"] = record.Status,
                    ["operationId"] = record.OperationId,
                    ["phase"] = record.Phase,
                    ["progressMessage"] = record.LastProgressMessage,
                    ["toolName"] = record.ToolName,
                    ["correlationId"] = record.CorrelationId,
                    ["timedOut"] = record.TimedOut,
                    ["timeoutCount"] = record.TimeoutCount,
                    ["startedAtUtc"] = record.StartedAtUtc,
                    ["updatedAtUtc"] = record.UpdatedAtUtc,
                    ["completedAtUtc"] = record.CompletedAtUtc
                };
                if (!string.IsNullOrWhiteSpace(record.LastError))
                    status["error"] = record.LastError;
                AttachTimedOutHint(status, record);
                return status;
            }
        }

        // issue #36.5 — a gateway wall-clock timeout leaves the op Status="Running" while the
        // worker keeps going (large Transaction/Structure writes routinely exceed the wait
        // budget yet persist). `timedOut:true` alone was easy to miss; spell out the ambiguous
        // state and the read-back contract so callers have a definitive next step instead of
        // polling a frozen "Running" record.
        private static void AttachTimedOutHint(JObject payload, OperationRecord record)
        {
            if (record.TimedOut && string.Equals(record.Status, "Running", StringComparison.OrdinalIgnoreCase))
            {
                payload["hint"] = "Exceeded the gateway wait budget but the worker may still be finishing — or may already have persisted (common for large Transaction/Structure writes). Re-read the target with genexus_read to confirm whether the change landed. The op terminalizes on its own if the worker's reply still arrives.";
            }
        }

        public JObject BuildOperationResult(string operationId)
        {
            if (!_operations.TryGetValue(operationId, out var record))
            {
                return new JObject
                {
                    ["status"] = "NotFound",
                    ["operationId"] = operationId,
                    ["message"] = "Operation not found or expired."
                };
            }

            lock (record.SyncRoot)
            {
                var payload = new JObject
                {
                    ["status"] = record.Status,
                    ["operationId"] = record.OperationId,
                    ["phase"] = record.Phase,
                    ["progressMessage"] = record.LastProgressMessage,
                    ["toolName"] = record.ToolName,
                    ["correlationId"] = record.CorrelationId,
                    ["timedOut"] = record.TimedOut,
                    ["timeoutCount"] = record.TimeoutCount,
                    ["startedAtUtc"] = record.StartedAtUtc,
                    ["updatedAtUtc"] = record.UpdatedAtUtc,
                    ["completedAtUtc"] = record.CompletedAtUtc
                };

                if (!string.IsNullOrWhiteSpace(record.LastError))
                {
                    payload["error"] = record.LastError;
                }

                if (record.WorkerPayload != null)
                {
                    payload["workerPayload"] = record.WorkerPayload.DeepClone();
                }
                else if (string.Equals(record.Status, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    payload["message"] = record.TimedOut
                        ? "Operation exceeded the gateway wait budget; the worker may still be finishing or may already have persisted."
                        : "Operation is still running. Query status again later.";
                }

                AttachTimedOutHint(payload, record);
                return payload;
            }
        }

        /// <summary>
        /// Waits for a tracked Gateway operation to make observable progress or reach a
        /// terminal state.  Lifecycle status used to return the initial Running record as
        /// soon as it was found, even when <c>wait</c> was explicitly supplied.  Keep this
        /// wait in the Gateway (rather than forwarding it to the Worker) because timed-out
        /// edits and other non-Worker operations are represented only by this tracker.
        /// </summary>
        internal async Task<JObject> WaitForOperationAsync(
            string operationId,
            int waitSeconds,
            string until = "change",
            CancellationToken cancellationToken = default)
        {
            int boundedWait = Math.Min(Math.Max(waitSeconds, 0), 600);
            bool waitForTerminal = string.Equals(until, "terminal", StringComparison.OrdinalIgnoreCase);
            JObject current = BuildOperationStatus(operationId);
            string baselineStatus = current["status"]?.ToString() ?? string.Empty;
            string baselineUpdated = current["updatedAtUtc"]?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty;

            bool terminal = IsTerminalOperationStatus(baselineStatus);
            if (boundedWait <= 0 || terminal || string.Equals(baselineStatus, "NotFound", StringComparison.OrdinalIgnoreCase))
            {
                AnnotateWaitResult(current, boundedWait, waitForTerminal, terminal,
                    waited: false, timedOut: false, cancelled: false);
                return current;
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(boundedWait);
            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    AnnotateWaitResult(current, boundedWait, waitForTerminal, terminal,
                        waited: false, timedOut: false, cancelled: true);
                    return current;
                }

                int remainingMs = (int)Math.Max(1, Math.Min(250, (deadline - DateTime.UtcNow).TotalMilliseconds));
                try
                {
                    await Task.Delay(remainingMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AnnotateWaitResult(current, boundedWait, waitForTerminal, terminal,
                        waited: true, timedOut: false, cancelled: true);
                    return current;
                }

                current = BuildOperationStatus(operationId);
                string status = current["status"]?.ToString() ?? string.Empty;
                string updated = current["updatedAtUtc"]?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty;
                bool nowTerminal = IsTerminalOperationStatus(status);
                bool changed = !string.Equals(status, baselineStatus, StringComparison.Ordinal)
                    || !string.Equals(updated, baselineUpdated, StringComparison.Ordinal);
                bool satisfied = waitForTerminal
                    ? nowTerminal
                    : changed || nowTerminal;
                if (satisfied || string.Equals(status, "NotFound", StringComparison.OrdinalIgnoreCase))
                {
                    AnnotateWaitResult(current, boundedWait, waitForTerminal, nowTerminal,
                        waited: true, timedOut: false, cancelled: false);
                    return current;
                }
            }

            current = BuildOperationStatus(operationId);
            string finalStatus = current["status"]?.ToString() ?? string.Empty;
            bool finalTerminal = IsTerminalOperationStatus(finalStatus);
            AnnotateWaitResult(current, boundedWait, waitForTerminal, finalTerminal,
                waited: true, timedOut: !finalTerminal, cancelled: false);
            return current;
        }

        private static bool IsTerminalOperationStatus(string status)
        {
            return string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Stalled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "NotFound", StringComparison.OrdinalIgnoreCase);
        }

        private static void AnnotateWaitResult(
            JObject payload,
            int requestedSeconds,
            bool untilTerminal,
            bool terminal,
            bool waited,
            bool timedOut,
            bool cancelled)
        {
            if (payload == null) return;
            payload["waitSatisfied"] = !timedOut && !cancelled;
            payload["waitRequestedSeconds"] = requestedSeconds;
            payload["waitUntil"] = untilTerminal ? "terminal" : "change";
            payload["waited"] = waited;
            if (cancelled)
                payload["cancelled"] = true;
            if (timedOut)
            {
                payload["waitTimedOut"] = true;
                payload["waitMessage"] = "The operation made no observable change before the requested wait expired.";
            }
            if (terminal)
                payload["terminal"] = true;
        }

        public JObject BuildMetricsPayload()
        {
            var items = new JArray(
                _toolMetrics.Values
                    .OrderBy(metric => metric.ToolName, StringComparer.OrdinalIgnoreCase)
                    .Select(metric => metric.ToJObject()));

            return new JObject
            {
                ["status"] = "Success",
                ["generatedAtUtc"] = DateTime.UtcNow,
                ["tools"] = items
            };
        }

        // Compact roll-up for whoami: total calls/errors/timeouts across all tools and the
        // single slowest tool by p95. Keeps the whoami payload tiny (one short object) so the
        // first-turn cost stays low while still giving the agent / operator a health snapshot.
        public JObject BuildMetricsSummary()
        {
            long totalCalls = 0, totalErrors = 0, totalTimeouts = 0;
            string? slowestTool = null;
            long slowestP95 = 0;
            foreach (var metric in _toolMetrics.Values)
            {
                var j = metric.ToJObject();
                totalCalls += (long)(j["count"] ?? 0L);
                totalErrors += (long)(j["errors"] ?? 0L);
                totalTimeouts += (long)(j["timeouts"] ?? 0L);
                long p95 = (long)(j["p95Ms"] ?? 0L);
                if (p95 > slowestP95)
                {
                    slowestP95 = p95;
                    slowestTool = metric.ToolName;
                }
            }

            // Friction 2026-05-22: counters alone don't tell the agent WHAT failed.
            // Pull the most recent OperationRecord with a LastError so a zombie
            // worker / repeated SDK fault surfaces in one read.
            JObject? lastError = null;
            OperationRecord? mostRecent = null;
            foreach (var rec in _operations.Values)
            {
                if (string.IsNullOrEmpty(rec.LastError)) continue;
                if (mostRecent == null || rec.UpdatedAtUtc > mostRecent.UpdatedAtUtc)
                    mostRecent = rec;
            }
            if (mostRecent != null)
            {
                lastError = new JObject
                {
                    ["tool"] = mostRecent.ToolName,
                    ["message"] = mostRecent.LastError,
                    ["atUtc"] = mostRecent.UpdatedAtUtc,
                    ["operationId"] = mostRecent.OperationId
                };
            }

            return new JObject
            {
                ["totalCalls"] = totalCalls,
                ["totalErrors"] = totalErrors,
                ["totalTimeouts"] = totalTimeouts,
                ["distinctTools"] = _toolMetrics.Count,
                ["slowestToolByP95"] = slowestTool != null
                    ? (JToken)new JObject { ["name"] = slowestTool, ["p95Ms"] = slowestP95 }
                    : JValue.CreateNull(),
                ["lastError"] = lastError != null ? (JToken)lastError : JValue.CreateNull()
            };
        }

        // Item 94: per-tool heat (totalMs / percentOfSession / lastUsedAt) for whoami.stats.heatmap.
        // Purely additive: stats.tools keeps its current shape; heatmap is a separate array.
        // Reads the same ToolMetricState ring buffer so the cost is constant.
        public JArray BuildHeatmapBlock()
        {
            long sessionTotalMs = 0;
            var entries = new List<(string tool, long totalMs, DateTime? lastUsed)>();
            foreach (var kvp in _toolMetrics)
            {
                var snapshot = kvp.Value.SnapshotHeat();
                if (snapshot.totalMs <= 0 && !snapshot.lastUsedAt.HasValue) continue;
                entries.Add((kvp.Key, snapshot.totalMs, snapshot.lastUsedAt));
                sessionTotalMs += snapshot.totalMs;
            }
            var arr = new JArray();
            foreach (var e in entries.OrderByDescending(t => t.totalMs))
            {
                double pct = sessionTotalMs > 0 ? Math.Round((double)e.totalMs * 100.0 / sessionTotalMs, 2) : 0.0;
                arr.Add(new JObject
                {
                    ["tool"] = e.tool,
                    ["totalMs"] = e.totalMs,
                    ["percentOfSession"] = pct,
                    ["lastUsedAt"] = e.lastUsed.HasValue ? (JToken)new JValue(e.lastUsed.Value) : JValue.CreateNull()
                });
            }
            return arr;
        }

        // Item 36: ring-buffer view of recent invocations filtered by target object name.
        // Reads ToolArguments.name/target from each OperationRecord; clamps `last` to 50.
        // Returns an envelope: { runs: [{atUtc, tool, durationMs, params, outcome}] }.
        public JObject BuildExecutionHistory(string targetName, int last)
        {
            if (last <= 0) last = 10;
            if (last > 50) last = 50;
            var matches = new List<OperationRecord>();
            foreach (var rec in _operations.Values)
            {
                if (!string.IsNullOrWhiteSpace(targetName))
                {
                    string? recTarget = rec.ToolArguments?["target"]?.ToString()
                                   ?? rec.ToolArguments?["name"]?.ToString();
                    if (string.IsNullOrEmpty(recTarget)) continue;
                    if (!string.Equals(recTarget, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                }
                matches.Add(rec);
            }
            var runs = new JArray();
            foreach (var rec in matches.OrderByDescending(r => r.StartedAtUtc).Take(last))
            {
                long durMs = rec.CompletedAtUtc.HasValue
                    ? Math.Max(0L, (long)(rec.CompletedAtUtc.Value - rec.StartedAtUtc).TotalMilliseconds)
                    : 0L;
                JObject pruned = rec.ToolArguments != null ? (JObject)rec.ToolArguments.DeepClone() : new JObject();
                runs.Add(new JObject
                {
                    ["atUtc"] = rec.StartedAtUtc,
                    ["tool"] = rec.ToolName,
                    ["durationMs"] = durMs,
                    ["params"] = pruned,
                    ["outcome"] = rec.Status,
                    ["error"] = string.IsNullOrEmpty(rec.LastError) ? (JToken)JValue.CreateNull() : new JValue(rec.LastError)
                });
            }
            return new JObject
            {
                ["status"] = "Success",
                ["target"] = targetName ?? string.Empty,
                ["runs"] = runs,
                ["totalMatches"] = matches.Count,
                ["note"] = "In-memory ring buffer; resets on gateway restart. Filtered by ToolArguments.name/target."
            };
        }

        // Item 35: genexus_watch_event proxy. Filters OperationRecord history for
        // invocations against <target> where the tool is event-relevant (genexus_edit /
        // genexus_run_object / genexus_lifecycle) AND the payload (args + response)
        // mentions <eventName>. Returns the practical proxy promised by tool docs —
        // NOT a real source-level breakpoint (that needs generator changes).
        public JObject BuildWatchEvent(string targetName, string eventName, int last)
        {
            if (last <= 0) last = 10;
            if (last > 50) last = 50;
            var watchTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "genexus_edit",
                "genexus_run_object",
                "genexus_lifecycle"
            };
            var matches = new List<OperationRecord>();
            foreach (var rec in _operations.Values)
            {
                if (!watchTools.Contains(rec.ToolName)) continue;
                if (!string.IsNullOrWhiteSpace(targetName))
                {
                    string? recTarget = rec.ToolArguments?["target"]?.ToString()
                                   ?? rec.ToolArguments?["name"]?.ToString();
                    if (string.IsNullOrEmpty(recTarget)) continue;
                    if (!string.Equals(recTarget, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (!string.IsNullOrWhiteSpace(eventName))
                {
                    string argsBlob = rec.ToolArguments?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty;
                    string respBlob = rec.WorkerPayload?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty;
                    if (argsBlob.IndexOf(eventName, StringComparison.OrdinalIgnoreCase) < 0 &&
                        respBlob.IndexOf(eventName, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }
                matches.Add(rec);
            }
            var runs = new JArray();
            foreach (var rec in matches.OrderByDescending(r => r.StartedAtUtc).Take(last))
            {
                long durMs = rec.CompletedAtUtc.HasValue
                    ? Math.Max(0L, (long)(rec.CompletedAtUtc.Value - rec.StartedAtUtc).TotalMilliseconds)
                    : 0L;
                runs.Add(new JObject
                {
                    ["atUtc"] = rec.StartedAtUtc,
                    ["tool"] = rec.ToolName,
                    ["eventName"] = eventName ?? string.Empty,
                    ["durationMs"] = durMs,
                    ["result"] = rec.Status,
                    ["error"] = string.IsNullOrEmpty(rec.LastError) ? (JToken)JValue.CreateNull() : new JValue(rec.LastError)
                });
            }
            return new JObject
            {
                ["status"] = "Success",
                ["target"] = targetName ?? string.Empty,
                ["event"] = eventName ?? string.Empty,
                ["runs"] = runs,
                ["totalMatches"] = matches.Count,
                ["note"] = "Proxy view: filtered from in-memory OperationTracker (edits + runs + lifecycle ops on target whose payload references the event name). Resets on gateway restart. Not a source-level breakpoint."
            };
        }

        // Item 30: surface per-tool p95 (ms) so BuildPlanService can estimate per-node duration.
        // Returns { toolName -> p95Ms } drained from the live metric ring buffer.
        public JObject BuildToolP95Map()
        {
            var obj = new JObject();
            foreach (var kvp in _toolMetrics)
            {
                var j = kvp.Value.ToJObject();
                obj[kvp.Key] = j["p95Ms"] ?? 0L;
            }
            return obj;
        }

        // Public snapshot type for callers that scan the history (e.g. MacroSuggestionService).
        // We expose immutable copies of ToolArguments so the caller can't mutate the live record.
        internal sealed class OperationSnapshot
        {
            public DateTime AtUtc { get; init; }
            public string ToolName { get; init; } = string.Empty;
            public string Status { get; init; } = string.Empty;
            public JObject? ToolArguments { get; init; }
        }

        // Item: snapshot recent operations for pattern-mining (MacroSuggestionService).
        // Returns ops with StartedAtUtc >= sinceUtc, ordered ascending by start time.
        // Args are deep-cloned so the caller can't mutate the live record.
        internal IReadOnlyList<OperationSnapshot> SnapshotRecentOperations(DateTime sinceUtc)
        {
            var list = new List<OperationSnapshot>();
            foreach (var rec in _operations.Values)
            {
                if (rec.StartedAtUtc < sinceUtc) continue;
                lock (rec.SyncRoot)
                {
                    list.Add(new OperationSnapshot
                    {
                        AtUtc = rec.StartedAtUtc,
                        ToolName = rec.ToolName,
                        Status = rec.Status,
                        ToolArguments = rec.ToolArguments != null ? (JObject)rec.ToolArguments.DeepClone() : null
                    });
                }
            }
            list.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
            return list;
        }

        // Test seam: record a tool invocation synthetically (no worker round-trip required).
        // Used by HeatmapBlockTests and ExecutionHistoryTests to keep them hermetic.
        internal void RecordSyntheticCompletion(string toolName, long elapsedMs, bool isError, JObject? toolArguments = null)
        {
            string requestId = Guid.NewGuid().ToString("N");
            string opId = StartOperation(requestId, toolName, toolArguments, Guid.NewGuid().ToString("N"));
            // Move started timestamp back by elapsedMs so duration math sees the gap.
            if (_operations.TryGetValue(opId, out var rec))
            {
                lock (rec.SyncRoot) { rec.StartedAtUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(elapsedMs); }
            }
            var payload = new JObject { ["id"] = requestId };
            if (isError) payload["error"] = new JObject { ["message"] = "synthetic" };
            else payload["result"] = new JObject { ["status"] = "Success" };
            CompleteFromWorker(requestId, payload);
        }

        internal void RecordCacheHit(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return;
            var metric = _toolMetrics.GetOrAdd(toolName, _ => new ToolMetricState(toolName));
            metric.RegisterCacheHit();
        }

        // Item 73: per-tool latency stats for whoami.stats.tools.
        // In-memory ring buffer (lost on gateway restart); count/p50/p95 per tool.
        // Item 75: also surface request/response size percentiles ("tokensIn /
        // tokensOut" — JSON byte length, divided by 4 as a coarse token proxy)
        // so the agent can spot tools producing oversized responses.
        // Limitation: stats reset on every gateway restart — document this in the block.
        public JObject BuildToolStatsBlock()
        {
            var toolsObj = new JObject();
            // Item 74 — track top-N most-failed tools so the agent can spot
            // "tried X 5 times, still failing" without scanning every entry.
            var failureRanking = new List<(string name, long errors, long count)>();
            foreach (var kvp in _toolMetrics)
            {
                var j = kvp.Value.ToJObject();
                long count = j["count"]?.ToObject<long>() ?? 0;
                if (count == 0) continue;
                long errors = j["errors"]?.ToObject<long>() ?? 0;
                var entry = new JObject
                {
                    ["p50Ms"] = j["p50Ms"],
                    ["p95Ms"] = j["p95Ms"],
                    ["count"] = count,
                    ["errorCount"] = errors,
                    ["cacheHits"] = j["cacheHits"]
                };
                // Item 75: tokensIn / tokensOut percentiles, omitted when no
                // payload was ever observed (cancellation-only history).
                JToken? tIn = j["tokensIn"];
                JToken? tOut = j["tokensOut"];
                if (tIn is JObject) entry["tokensIn"] = tIn;
                if (tOut is JObject) entry["tokensOut"] = tOut;
                if (j["errorsByCode"] is JObject errorsByCode)
                    entry["errorsByCode"] = errorsByCode.DeepClone();
                toolsObj[kvp.Key] = entry;
                if (errors > 0) failureRanking.Add((kvp.Key, errors, count));
            }
            var mostFailed = new JArray();
            foreach (var f in failureRanking.OrderByDescending(t => t.errors).ThenByDescending(t => (double)t.errors / Math.Max(1, t.count)).Take(5))
            {
                mostFailed.Add(new JObject
                {
                    ["tool"] = f.name,
                    ["errors"] = f.errors,
                    ["count"] = f.count,
                    ["errorRate"] = Math.Round((double)f.errors / Math.Max(1, f.count), 3)
                });
            }
            return new JObject
            {
                ["tools"] = toolsObj,
                ["mostFailed"] = mostFailed,
                ["note"] = "In-memory only; resets on gateway restart. tokensIn/Out are bytes/4 estimates."
            };
        }

        public void CleanupExpired()
        {
            DateTime cutoff = DateTime.UtcNow - _retention;
            foreach (var kvp in _operations)
            {
                var record = kvp.Value;
                bool remove;
                lock (record.SyncRoot)
                {
                    // Only COMPLETED operations expire by age. A running operation must
                    // never be swept by a time-based cleanup: under a tiny retention (or
                    // a descheduled thread) an in-flight record can age past the cutoff
                    // between StartOperation and the sweep, dropping the request->operation
                    // mapping and turning every later CompleteFromWorker/status poll into
                    // NotFound (CI-flaky CleanupExpired_DoesNotDropMappingForReusedRequestId;
                    // also the exact 'stuck running disappears' trap for long SDK calls).
                    // Completed records have UpdatedAtUtc pinned to CompletedAtUtc, so the
                    // age check below is exactly the retention window for terminal ops.
                    remove = record.CompletedAtUtc.HasValue && record.UpdatedAtUtc < cutoff;
                }

                if (!remove) continue;

                _operations.TryRemove(kvp.Key, out _);
                // Only drop the request->operation mapping if it still points at THIS
                // (expiring) operation. StartOperation overwrites the mapping on id
                // reuse, so an unconditional TryRemove(record.RequestId) could delete a
                // newer, live operation's mapping. Compare-and-remove via the
                // ICollection<KeyValuePair<>> overload.
                var mapView = (ICollection<KeyValuePair<string, string>>)_requestToOperation;
                mapView.Remove(new KeyValuePair<string, string>(record.RequestId, kvp.Key));
                // Also drop any retry ids linked to this operation (LinkRequest), or they
                // would leak — CleanupExpired never sees them via record.RequestId.
                if (record.LinkedRequestIds != null)
                {
                    foreach (var linkedId in record.LinkedRequestIds)
                        mapView.Remove(new KeyValuePair<string, string>(linkedId, kvp.Key));
                }
            }
        }

        private readonly ConcurrentDictionary<string, SpawnSampleRing> _spawnSamples =
            new ConcurrentDictionary<string, SpawnSampleRing>(StringComparer.OrdinalIgnoreCase);

        public void RegisterSpawnSample(string kbAlias, double ms)
        {
            if (string.IsNullOrWhiteSpace(kbAlias)) return;
            var ring = _spawnSamples.GetOrAdd(kbAlias, _ => new SpawnSampleRing(capacity: 256));
            ring.Add(ms);
        }

        public (int Count, double P50, double P95) GetSpawnStats(string kbAlias)
        {
            if (!_spawnSamples.TryGetValue(kbAlias, out var ring)) return (0, 0, 0);
            return ring.Snapshot();
        }

        private sealed class SpawnSampleRing
        {
            private readonly int _capacity;
            private readonly double[] _buffer;
            private int _count;
            private int _next;
            private readonly object _lock = new object();

            public SpawnSampleRing(int capacity)
            {
                _capacity = capacity;
                _buffer = new double[capacity];
            }

            public void Add(double sample)
            {
                lock (_lock)
                {
                    _buffer[_next] = sample;
                    _next = (_next + 1) % _capacity;
                    if (_count < _capacity) _count++;
                }
            }

            public (int Count, double P50, double P95) Snapshot()
            {
                double[] snapshot;
                int count;
                lock (_lock)
                {
                    count = _count;
                    snapshot = new double[count];
                    System.Array.Copy(_buffer, snapshot, count);
                }
                if (count == 0) return (0, 0, 0);
                System.Array.Sort(snapshot);
                double p50 = snapshot[(int)(count * 0.50)];
                double p95 = snapshot[System.Math.Min(count - 1, (int)(count * 0.95))];
                return (count, p50, p95);
            }
        }

        private sealed class OperationRecord
        {
            public readonly object SyncRoot = new object();
            public string OperationId { get; set; } = string.Empty;
            public string RequestId { get; set; } = string.Empty;
            public string ToolName { get; set; } = string.Empty;
            public string CorrelationId { get; set; } = string.Empty;
            public string Status { get; set; } = "Running";
            public bool TimedOut { get; set; }
            public int TimeoutCount { get; set; }
            public DateTime StartedAtUtc { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
            public string? LastError { get; set; }
            // A4: last progress phase/message from a worker notifications/progress frame,
            // surfaced on status polls so a long op shows live progress, not a frozen state.
            public string? Phase { get; set; }
            public string? LastProgressMessage { get; set; }
            public JObject? ToolArguments { get; set; }
            public JToken? WorkerPayload { get; set; }
            // Guard against registering more than one metric completion per logical
            // operation: a worker-crash retry drives both MarkFailedByRequest (the crash)
            // and CompleteFromWorker (the successful retry) against the same record, and
            // without this the tool metrics would double-count that single user call.
            public bool MetricRegistered { get; set; }
            // Extra request ids linked to this operation by worker-crash retries (via
            // LinkRequest). CleanupExpired must drop these too or _requestToOperation grows
            // without bound — the retention window only knows about the original RequestId.
            public List<string>? LinkedRequestIds { get; set; }
        }

        private sealed class ToolMetricState
        {
            private readonly object _lock = new object();
            private readonly List<long> _latencies = new List<long>();
            // Item 75: parallel ring buffers for JSON byte sizes of the request
            // and the response. Same 256-sample cap as latency so the memory
            // footprint stays bounded.
            private readonly List<long> _reqBytes = new List<long>();
            private readonly List<long> _respBytes = new List<long>();
            private readonly Dictionary<string, long> _errorsByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            private const int MaxLatencySamples = 256;

            public ToolMetricState(string toolName)
            {
                ToolName = toolName;
            }

            public string ToolName { get; }
            public long Count { get; private set; }
            public long ErrorCount { get; private set; }
            public long TimeoutCount { get; private set; }
            public long NoChangeCount { get; private set; }
            public long PatchFailCount { get; private set; }
            public long FallbackSaveCount { get; private set; }
            public long CacheHitCount { get; private set; }
            // Item 94: cumulative elapsed ms across all observed completions and the
            // most recent timestamp any call landed.
            private long _totalMs;
            private DateTime? _lastUsedAt;

            public (long totalMs, DateTime? lastUsedAt) SnapshotHeat()
            {
                lock (_lock) { return (_totalMs, _lastUsedAt); }
            }

            public void RegisterTimeout()
            {
                lock (_lock)
                {
                    TimeoutCount++;
                }
            }

            public void RegisterCacheHit()
            {
                lock (_lock) CacheHitCount++;
            }

            public void RegisterCompletion(long elapsedMs, bool isError, JToken? workerPayload, long reqBytes, long respBytes)
            {
                lock (_lock)
                {
                    Count++;
                    if (isError) ErrorCount++;
                    _lastUsedAt = DateTime.UtcNow;
                    if (elapsedMs > 0) _totalMs += elapsedMs;
                    if (elapsedMs > 0)
                    {
                        _latencies.Add(elapsedMs);
                        if (_latencies.Count > MaxLatencySamples)
                        {
                            _latencies.RemoveAt(0);
                        }
                    }

                    // Only record payload sizes when at least one side is non-zero;
                    // cancellation paths feed (0,0) and should not skew the
                    // percentile toward "tools have empty bodies".
                    if (reqBytes > 0)
                    {
                        _reqBytes.Add(reqBytes);
                        if (_reqBytes.Count > MaxLatencySamples) _reqBytes.RemoveAt(0);
                    }
                    if (respBytes > 0)
                    {
                        _respBytes.Add(respBytes);
                        if (_respBytes.Count > MaxLatencySamples) _respBytes.RemoveAt(0);
                    }

                    ApplySemanticCounters(workerPayload);
                }
            }

            public JObject ToJObject()
            {
                lock (_lock)
                {
                    var ordered = _latencies.OrderBy(v => v).ToArray();
                    long p50 = Percentile(ordered, 0.50);
                    long p95 = Percentile(ordered, 0.95);
                    var payload = new JObject
                    {
                        ["toolName"] = ToolName,
                        ["count"] = Count,
                        ["errors"] = ErrorCount,
                        ["timeouts"] = TimeoutCount,
                        ["noChange"] = NoChangeCount,
                        ["patchFail"] = PatchFailCount,
                        ["fallbackSave"] = FallbackSaveCount,
                        ["cacheHits"] = CacheHitCount,
                        ["p50Ms"] = p50,
                        ["p95Ms"] = p95
                    };
                    payload["tokensIn"] = BuildSizeBlock(_reqBytes);
                    payload["tokensOut"] = BuildSizeBlock(_respBytes);
                    if (_errorsByCode.Count > 0)
                        payload["errorsByCode"] = JObject.FromObject(_errorsByCode);
                    return payload;
                }
            }

            // Bytes / 4 ≈ tokens for the typical JSON character mix; cheap
            // enough that we can compute on every whoami call. Returns null
            // when the buffer is empty so callers can omit the block.
            private static JToken BuildSizeBlock(List<long> samples)
            {
                if (samples == null || samples.Count == 0) return JValue.CreateNull();
                var ordered = samples.OrderBy(v => v).ToArray();
                long p50 = Percentile(ordered, 0.50) / 4;
                long p95 = Percentile(ordered, 0.95) / 4;
                long max = ordered[ordered.Length - 1] / 4;
                return new JObject
                {
                    ["p50"] = p50,
                    ["p95"] = p95,
                    ["max"] = max,
                    ["samples"] = samples.Count
                };
            }

            private void ApplySemanticCounters(JToken? workerPayload)
            {
                if (workerPayload == null) return;

                foreach (var obj in EnumerateObjects(workerPayload))
                {
                    string? status = obj["status"]?.ToString();
                    string? details = obj["details"]?.ToString();
                    string? patchStatus = obj["patchStatus"]?.ToString();
                    string? retryStrategy = obj["retryStrategy"]?.ToString();
                    string? errorCode = (obj["error"] as JObject)?["code"]?.ToString()
                        ?? obj["errorCode"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(errorCode))
                    {
                        _errorsByCode.TryGetValue(errorCode, out long count);
                        _errorsByCode[errorCode] = count + 1;
                    }

                    if (string.Equals(status, "NoChange", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(patchStatus, "NoChange", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(details) && details.IndexOf("No change", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        NoChangeCount++;
                    }

                    if (string.Equals(patchStatus, "NoMatch", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(patchStatus, "Ambiguous", StringComparison.OrdinalIgnoreCase))
                    {
                        PatchFailCount++;
                    }

                    if (!string.IsNullOrWhiteSpace(retryStrategy) &&
                        retryStrategy.IndexOf("object_save_only", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        FallbackSaveCount++;
                    }
                }
            }

            private static IEnumerable<JObject> EnumerateObjects(JToken token)
            {
                if (token is JObject obj)
                {
                    yield return obj;
                    foreach (var property in obj.Properties())
                    {
                        foreach (var nested in EnumerateObjects(property.Value))
                        {
                            yield return nested;
                        }
                    }
                    yield break;
                }

                if (token is JArray arr)
                {
                    foreach (var item in arr)
                    {
                        foreach (var nested in EnumerateObjects(item))
                        {
                            yield return nested;
                        }
                    }
                }
            }

            private static long Percentile(long[] ordered, double percentile)
            {
                if (ordered.Length == 0) return 0;
                int idx = (int)Math.Ceiling(percentile * ordered.Length) - 1;
                if (idx < 0) idx = 0;
                if (idx >= ordered.Length) idx = ordered.Length - 1;
                return ordered[idx];
            }
        }
    }
}
