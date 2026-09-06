using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        // Keeps the master's gateway lease fresh. Paced by LeaseHeartbeatInterval,
        // which is deliberately well under GatewayProcessLease.LeaseStaleAfter — see
        // that constant for why the two MUST NOT drift apart. (Previously the lease
        // was only refreshed by the 1-minute session-cleanup loop, leaving a ~15s
        // window each minute where a live master looked stale and got killed by a
        // newly-spawned gateway → intermittent "Transport closed".)
        private static async Task RunLeaseHeartbeatLoop(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(GatewayProcessLease.LeaseHeartbeatInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    if (_activeConfig != null)
                    {
                        try { GatewayProcessLease.RefreshCurrentProcess(_activeConfig); }
                        catch (Exception ex) { Log($"[Gateway] Lease heartbeat failed: {ex.Message}"); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task RunSessionCleanupLoop(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    int removed = _httpSessions.CleanupExpired();
                    if (removed > 0)
                    {
                        Log($"[HTTP] Removed {removed} expired MCP session(s).");
                    }

                    _operationTracker.CleanupExpired();
                    int stalePending = CleanupStalePendingRequests();
                    if (stalePending > 0)
                    {
                        Log($"[Gateway] Removed {stalePending} stale pending worker request(s).");
                    }

                    int staleLongPolls = CleanupStalePendingLongPolls(DateTime.UtcNow - _pendingRequestRetention);
                    if (staleLongPolls > 0)
                    {
                        Log($"[Gateway] Cancelled {staleLongPolls} stale lifecycle long-poll(s).");
                    }

                    // Plan 036: sweep _jobs first, then prune each session's seen-set
                    // against the now-reduced _jobs so retained ids can't reference
                    // already-swept jobs.
                    JobRegistry.SweepExpired();
                    JobRegistry.PruneSeenBySession();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static int CleanupStalePendingRequests()
        {
            int removed = 0;
            DateTime cutoff = DateTime.UtcNow - _pendingRequestRetention;
            foreach (var kvp in _pendingRequests.ToArray())
            {
                if (kvp.Value.CreatedAtUtc > cutoff)
                {
                    continue;
                }

                if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                {
                    _operationTracker.MarkFailedByRequest(kvp.Key, "Pending worker request expired before completion.");
                    pending.CompletionSource.TrySetResult(JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = kvp.Key,
                        error = new
                        {
                            code = -32603,
                            message = "Pending worker request expired before completion."
                        }
                    }));
                    removed++;
                }
            }

            return removed;
        }

        // Logger names whose notifications/message events are safe to surface to
        // stdio AI clients (Antigravity / Claude Desktop / Cursor surface these in
        // chat or system messages). Internal operational telemetry — "Operation X
        // started/finished", "Worker warmup started" — stays out because agents
        // interpret it as KB state changes and get confused.
        private static readonly HashSet<string> _stdioLoggerAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "indexing",
            "update-check"
        };

        private static void BroadcastNotification(string method, object payload)
        {
            _ = Task.Run(() => {
                try {
                    string json = JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        method,
                        @params = payload
                    });

                    if (ShouldForwardNotificationToStdio(method, payload))
                    {
                        EmitStdioNotification(json);
                    }

                    foreach (var session in _httpSessions.ActiveSessions)
                    {
                        if (!ShouldDeliverNotificationToSession(method, payload, session))
                            continue;
                        QueueSessionMessage(session, json);
                    }

                    // Modern 2026 subscriptions/listen streams are independent
                    // transport handles. Each stream applies its own notification
                    // filter and adds its subscription id in-band; never enqueue
                    // the shared legacy envelope directly into a modern stream.
                    foreach (var subscription in _modernSubscriptions.Active)
                    {
                        subscription.TryQueue(method, payload);
                    }
                } catch (Exception ex) {
                    Log($"[Broadcast] Error: {ex.Message}");
                }
            });
        }

        // Forward a pre-serialized JSON-RPC notification envelope to the stdio
        // client (Claude Desktop / Cursor / Antigravity). Required because
        // BroadcastNotification only reaches HTTP sessions otherwise.
        private static void EmitStdioNotification(string json)
        {
            if (!_stdioActive) return;
            _ = TryWriteStdout(json);
        }

        private static bool ShouldForwardNotificationToStdio(string method, object? payload)
        {
            if (method == "notifications/progress" ||
                method == "notifications/resources/updated" ||
                method == "notifications/resources/list_changed" ||
                method == "notifications/tools/list_changed")
            {
                return true;
            }
            if (method != "notifications/message") return false;

            // notifications/message is the loudest channel — be strict. Only surface
            // warnings/errors (user-actionable) or explicit allowlisted loggers
            // (indexing cold-start, update-check). Routine operation start/finish
            // events stay HTTP-only.
            string? level = null, logger = null;
            try
            {
                JObject? jp = payload as JObject;
                if (jp == null && payload is JToken token) jp = token as JObject;
                if (jp == null && payload != null) jp = JObject.FromObject(payload);
                if (jp == null) return false;
                level = jp["level"]?.ToString();
                logger = jp["logger"]?.ToString();
            }
            catch { return false; }

            if (!string.IsNullOrEmpty(logger) && _stdioLoggerAllowlist.Contains(logger)) return true;
            if (string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ShouldDeliverNotificationToSession(string method, object? payload, HttpSessionState session)
        {
            if (!string.Equals(method, "notifications/resources/updated", StringComparison.Ordinal))
                return true;

            try
            {
                var obj = payload as JObject ?? (payload == null ? null : JObject.FromObject(payload));
                foreach (var candidate in new[]
                {
                    obj?["resourceUri"]?.ToString()?.Trim(),
                    obj?["uri"]?.ToString()?.Trim()
                })
                {
                    if (!string.IsNullOrWhiteSpace(candidate)
                        && McpSubscriptionProtocol.IsSubscribed(session, candidate))
                        return true;
                }

                return false;
            }
            catch
            {
                // A malformed resource event must never become a broadcast.
                return false;
            }
        }

        private static void BroadcastToolsListChanged(
            string reason,
            string? kbAlias = null,
            long? cacheRevision = null)
        {
            kbAlias ??= GetCurrentKb()?.NormalizedAlias;
            BroadcastNotification(
                "notifications/tools/list_changed",
                BuildScopedNotificationPayload(reason, kbAlias, cacheRevision, uri: null));
        }

        private static void BroadcastResourcesListChanged(
            string reason,
            string? kbAlias = null,
            long? cacheRevision = null)
        {
            kbAlias ??= GetCurrentKb()?.NormalizedAlias;
            BroadcastNotification(
                "notifications/resources/list_changed",
                BuildScopedNotificationPayload(reason, kbAlias, cacheRevision, uri: null));
        }

        private static void BroadcastResourceUpdated(
            string uri,
            string reason,
            string? kbAlias = null,
            long? cacheRevision = null)
        {
            kbAlias ??= GetCurrentKb()?.NormalizedAlias;
            BroadcastNotification(
                "notifications/resources/updated",
                BuildScopedNotificationPayload(reason, kbAlias, cacheRevision, uri));
        }

        /// <summary>
        /// Adds a stable KB identity and the cache generation observed by the
        /// notification. The legacy <c>uri</c> remains unchanged for existing
        /// subscribers; <c>resourceUri</c> is the scoped form that prevents an
        /// update from one open KB being mistaken for the same-named resource in
        /// another KB. Revisions are metadata rather than a URI query parameter so
        /// a subscription survives a later mutation while still letting a client
        /// reject an event based on an older snapshot.
        /// </summary>
        private static JObject BuildScopedNotificationPayload(
            string reason,
            string? kbAlias,
            long? cacheRevision,
            string? uri)
        {
            var payload = new JObject
            {
                ["reason"] = reason ?? string.Empty,
                ["timestamp"] = DateTime.UtcNow
            };

            string? normalizedAlias = string.IsNullOrWhiteSpace(kbAlias)
                ? null
                : kbAlias.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalizedAlias))
            {
                payload["kbAlias"] = normalizedAlias;
                payload["cacheRevision"] = cacheRevision ?? _semanticCache.GetRevision(normalizedAlias!);
            }

            if (!string.IsNullOrWhiteSpace(uri))
            {
                payload["uri"] = uri.Trim();
                if (!string.IsNullOrWhiteSpace(normalizedAlias))
                    payload["resourceUri"] = BuildScopedResourceUri(normalizedAlias!, uri.Trim());
            }

            return payload;
        }

        internal static string BuildScopedResourceUri(string kbAlias, string uri)
        {
            if (string.IsNullOrWhiteSpace(kbAlias) || string.IsNullOrWhiteSpace(uri)) return uri ?? string.Empty;

            string alias = Uri.EscapeDataString(kbAlias.Trim().ToLowerInvariant());
            string trimmedUri = uri.Trim();
            string scopedPrefix = "genexus://kb/" + alias + "/";
            if (trimmedUri.StartsWith(scopedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string remainder = trimmedUri.Substring(scopedPrefix.Length);
                if (remainder.StartsWith("objects/", StringComparison.OrdinalIgnoreCase)
                    || remainder.StartsWith("attributes/", StringComparison.OrdinalIgnoreCase)
                    || remainder.StartsWith("kb/", StringComparison.OrdinalIgnoreCase))
                    return trimmedUri;
            }

            const string schemePrefix = "genexus://";
            if (trimmedUri.StartsWith(schemePrefix, StringComparison.OrdinalIgnoreCase))
                return schemePrefix + "kb/" + alias + "/" + trimmedUri.Substring(schemePrefix.Length).TrimStart('/');

            return schemePrefix + "kb/" + alias + "/" + trimmedUri.TrimStart('/');
        }
    }
}
