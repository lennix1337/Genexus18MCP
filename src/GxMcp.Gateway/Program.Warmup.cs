using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        // Bootstrap each registered Worker, including lazy replacements after an
        // idle exit. A restored cache still needs its delta scan before reads.
        private static void TriggerIndexBootstrapOnce(string? kbAlias = null)
        {
            string? bootstrapKey = NormalizeKbAlias(kbAlias) ?? NormalizeKbAlias(_currentKb.Value?.NormalizedAlias);
            if (bootstrapKey == null)
            {
                // Startup may run before the default Worker is registered. Its
                // OnWorkerStarted event will bootstrap the concrete alias.
                var open = _workerPool?.ListOpen();
                if (open?.Count == 1) bootstrapKey = open[0].NormalizedAlias;
            }
            if (bootstrapKey == null) return;
            var bootstrapPool = _workerPool;
            var bootstrapWorker = bootstrapPool?.TryGet(bootstrapKey);
            if (bootstrapWorker == null) return;
            if (!_indexBootstrapStartedByKb.TryAdd(bootstrapKey, 0)) return;

            if (IndexBootstrapTriggerForTest != null)
            {
                IndexBootstrapTriggerForTest();
                return;
            }

            Log($"[IndexBootstrap] firing for KB '{bootstrapKey}'");

            _ = Task.Run(async () =>
            {
                KbHandle? previousKb = _currentKb.Value;
                bool previousOwnerRequirement = _currentOperationRequiresOwner.Value;
                try
                {
                    if (_workerPool == null) { Log("[IndexBootstrap] worker pool null"); return; }

                    _currentKb.Value = _workerPool.ListOpen()
                        .FirstOrDefault(h => string.Equals(h.NormalizedAlias, bootstrapKey, StringComparison.OrdinalIgnoreCase));
                    if (_currentKb.Value == null)
                    {
                        Log($"[IndexBootstrap] KB '{bootstrapKey}' is no longer open");
                        return;
                    }

                    // Warmup and index bootstrap both acquire the default KB. Serialize
                    // them so initialize cannot create two Workers for the same KB and
                    // leave the gateway holding the BusyRejecting process.
                    await WorkerWarmupCompleted.Task.ConfigureAwait(false);
                    // A queued bootstrap from a retired Worker must not initialize
                    // its replacement a second time or reopen a closed KB.
                    if (!ReferenceEquals(_workerPool, bootstrapPool)
                        || !ReferenceEquals(bootstrapPool!.TryGet(bootstrapKey), bootstrapWorker)) return;

                    var indexCommand = new JObject
                    {
                        ["module"] = "KB",
                        ["action"] = "BulkIndex",
                        ["client"] = "mcp"
                    };

                    // Bootstrap is an internal gateway operation, not a client mutation;
                    // it must not require the caller's session lease after an explicit open
                    // or reload selected the worker by alias.
                    _currentOperationRequiresOwner.Value = false;
                    var resp = await SendWorkerCommandAsync(
                        indexCommand,
                        30000,
                        "Index bootstrap timeout",
                        wr => wr,
                        (_, correlationId) => new JObject(),
                        toolName: "gateway_index_bootstrap",
                        trackOperation: false);

                    // BulkIndex now returns the canonical envelope ({status:"ok", code, result}).
                    // The fresh-vs-warm signal lives in `code`; fall back to the legacy top-level
                    // `status` for any pre-canonical worker still in the pool.
                    var result = resp?["result"] as JObject;
                    string? status = result?["code"]?.ToString()
                        ?? result?["status"]?.ToString();
                    Log($"[IndexBootstrap] worker reply code={status ?? "<null>"}");

                    // The default lite-index path returns "LiteStarted"; the legacy full path
                    // returns "Started". Either means a fresh cold-start index just kicked off,
                    // so the agent should see the one-time background-indexing notice.
                    // ("AlreadyIndexed" / "AlreadyInProgress" / "DeltaStarted" are warm starts — no notice.)
                    if (string.Equals(status, "Started", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "LiteStarted", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("[IndexBootstrap] emitting cold-start notice");
                        BroadcastNotification("notifications/message", new
                        {
                            level = "info",
                            logger = "indexing",
                            data = "First-time indexing of this KB has started in the background. "
                                + "Index-dependent reads wait until freshness=current. "
                                + "Watch notifications/progress for live progress."
                        });
                    }

                    // Open the read gate as soon as the index is genuinely current, before
                    // anything else on this path waits on the index. See the method for why the
                    // read gate would otherwise hold the agent for up to the mirror's 2s floor.
                    // Published while it runs so an index-dependent call that lands in this
                    // short window waits for it instead of returning a retry envelope.
                    var settle = SettleIndexMirrorAfterBootstrapAsync();
                    IndexMirrorSettleInFlight = settle;
                    try { await settle; }
                    finally
                    {
                        if (ReferenceEquals(IndexMirrorSettleInFlight, settle))
                            IndexMirrorSettleInFlight = null;
                    }

                    // The first-touch warm pass can only resolve its probe object once the
                    // index is listable. On a cold start the warmup's single list attempt runs
                    // before this bootstrap, sees the IndexNotReady envelope (no items) and
                    // would skip the pass — leaving the first-touch JIT/SDK cost on the agent's
                    // read/inspect/edit calls. Now that the index has been kicked (or was
                    // already warm), wait for a listable object here and warm.
                    await RunFirstTouchWarmOnceAsync(
                        ResolveWarmupProbeObjectAsync,
                        () => Log("[Warmup] Probe object not listable yet (index still building); waiting before the first-touch warm pass."));
                }
                catch (OperationCanceledException) when (_gatewayLifetime.IsCancellationRequested)
                {
                    // Gateway shutdown cancels internal warmup work by design.
                }
                catch (Exception ex)
                {
                    Log($"[IndexBootstrap] {ex.Message}");
                }
                finally
                {
                    _currentKb.Value = previousKb;
                    _currentOperationRequiresOwner.Value = previousOwnerRequirement;
                }
            });
        }

        // Cold-path fix: right after a KB open the gateway's index mirror holds the snapshot
        // restored from the warm cache — Ready but stale — because the mirror is only written by
        // whoami / the SDK-bound self-heal path and there is no push from the worker. The worker's
        // delta refresh republishes Freshness=current a few hundred ms later (measured 251-283ms
        // against a 620-object KB), but the read gate only re-probes a mirror older than its 2s
        // floor, so the agent's first list/query/read kept receiving Indexing envelopes for ~1.8s
        // against an index that was already usable (measured: first useful result 6857ms).
        //
        // Settling here (bounded, and anchored to the bootstrap that just kicked the delta) opens
        // the gate as soon as the delta actually lands: about one cheap GetIndexState round-trip
        // instead of a fixed 2s wait. GetIndexState runs off the worker's STA thread, so this stays
        // fast even while a cold-start BulkIndex is in flight. It also lets the first-touch warm
        // pass below start against a listable index instead of burning its first 3s probe retry.
        //
        // Bounded on purpose: a genuinely cold KB (first-ever index) keeps the gate closed, and
        // after the budget the regular gate + retryAfterMs path takes over unchanged.
        internal const int IndexMirrorSettleMaxAttempts = 12;
        internal const int IndexMirrorSettleDelayMs = 250;

        // The settle that is currently running, or null. Read by the index gate: a call that
        // arrives while it runs waits for it (bounded) instead of handing the agent an
        // IndexNotReady envelope it would have to retry — measured, the envelope cost a real
        // agent a retryAfterMs backoff and a whole wasted turn on an index that was already
        // usable ~250ms later. Cleared by the bootstrap that published it.
        // Deliberately not keyed by alias: the settle is short, a worker is single-KB, and the
        // gate re-reads the snapshot for its own alias after the wait, so a caller only ever
        // waits for a settle it would have been racing anyway.
        internal static volatile Task? IndexMirrorSettleInFlight;

        // Ceiling for the gate's wait on that settle. The settle's own budget is
        // IndexMirrorSettleMaxAttempts x IndexMirrorSettleDelayMs plus its refresh round-trips;
        // this guards only against a wedged refresh, so a timeout falls back to the envelope.
        internal const int IndexMirrorSettleGateWaitCeilingMs = 6000;

        // True when the mirror holds a snapshot restored from the warm cache that has not been
        // republished as current yet: every object is already loaded, but the Worker's delta
        // refresh — the step that flips Freshness to current — has not landed. This is the one
        // not-ready state where the mirror is known to be WRONG rather than merely young, so the
        // read gate settles it (see the gate) instead of trusting its staleness floor. A genuinely
        // cold KB has no restored snapshot, reports something other than Ready here, and keeps the
        // fast-fail plus retryAfterMs path untouched.
        internal static bool IsRestoredSnapshotAwaitingDeltaForTest(string? status, string? freshness)
            => IsRestoredSnapshotAwaitingDelta(status, freshness);

        // Restricted to `Ready` on purpose. `Ready` means the full index walk finished (every
        // object is loaded), so the only thing missing is the delta that flips freshness — a
        // bounded, sub-second window. A genuinely cold build announces UltraLiteReady/LiteReady/
        // Enriching while it streams, and those must keep the immediate envelope: a multi-minute
        // build cannot be waited out, and settling there would block every read on it.
        private static bool IsRestoredSnapshotAwaitingDelta(string? status, string? freshness)
        {
            if (!string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase)) return false;
            // Same inference IsIndexUsableForReads uses, so the two never disagree about what an
            // absent freshness means. (A `Ready` mirror with no freshness infers `current`, which
            // is usable — the gate is never reached in that state.)
            string effective = string.IsNullOrWhiteSpace(freshness)
                ? InferIndexFreshness(status!)
                : freshness!;
            return !string.Equals(effective, "current", StringComparison.OrdinalIgnoreCase);
        }

        internal static async Task SettleIndexMirrorAfterBootstrapAsync(
            Func<bool>? isUsable = null,
            Func<int, Task<bool>>? refresh = null,
            int? delayMsOverride = null,
            Action<int, bool>? onAttempt = null)
        {
            Func<bool> usableNow = isUsable
                ?? (() => IsIndexUsableForReads(GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias)));
            if (usableNow()) return;

            int delayMs = delayMsOverride ?? IndexMirrorSettleDelayMs;
            bool refreshed = false;
            for (int attempt = 1; attempt <= IndexMirrorSettleMaxAttempts; attempt++)
            {
                if (delayMs > 0)
                {
                    try { await Task.Delay(delayMs).ConfigureAwait(false); }
                    catch { return; }
                }

                try
                {
                    refreshed = refresh != null
                        ? await refresh(attempt).ConfigureAwait(false)
                        : await TryRefreshIndexStateFromWorkerAsync(
                            timeoutMs: 1200, kbAlias: _currentKb.Value?.NormalizedAlias).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_gatewayLifetime.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    refreshed = false;
                }

                bool usable = usableNow();
                try { onAttempt?.Invoke(attempt, usable); } catch { }
                if (usable)
                {
                    Log($"[IndexBootstrap] index gate settled after {attempt} attempt(s).");
                    return;
                }
            }

            // Budget exhausted — a genuinely cold KB (first-ever index) is the expected case here.
            // The gate stays closed and falls back to its regular refresh + retryAfterMs path; the
            // line exists so "settled late" and "never settled" are distinguishable in a support
            // log. A failed refresh (worker busy/exiting) does not shorten the budget: only the
            // snapshot deciding the gate matters.
            Log($"[IndexBootstrap] index mirror did not settle within {IndexMirrorSettleMaxAttempts} "
                + $"attempt(s) (last refresh ok={refreshed}); the read gate keeps its regular "
                + "refresh + retryAfterMs path.");
        }

        private static void TriggerWorkerWarmupOnce()
        {
            if (Interlocked.CompareExchange(ref _workerWarmupStarted, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_workerPool == null)
                    {
                        Log("[Warmup] WorkerPool not available, skipping warmup.");
                        return;
                    }

                    // Resolve the configured default KB before issuing the warmup
                    // command. Strict resolution does not auto-open declared KBs,
                    // so the command would otherwise fail when initialize starts
                    // with no worker already attached.
                    await PrespawnDefaultKbWorkerAsync();

                    Log("[Warmup] Starting worker warmup sequence...");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup started.",
                        timestamp = DateTime.UtcNow
                    });

                    string? objectName = await ResolveWarmupProbeObjectAsync();
                    if (!string.IsNullOrWhiteSpace(objectName))
                    {
                        // Warm start: the index is already listable, so the fast path runs the
                        // pass right here instead of waiting for the index-bootstrap trigger.
                        await RunFirstTouchWarmOnceAsync(() => Task.FromResult<string?>(objectName), onWaitingForProbe: null);
                    }

                    Log("[Warmup] Worker warmup finished.");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup finished.",
                        timestamp = DateTime.UtcNow
                    });
                }
                catch (OperationCanceledException) when (_gatewayLifetime.IsCancellationRequested)
                {
                    // Gateway shutdown cancels internal warmup work by design.
                }
                catch (Exception ex)
                {
                    Log("[Warmup] Worker warmup failed: " + ex.Message);
                    BroadcastNotification("notifications/message", new
                    {
                        level = "warning",
                        logger = "warmup",
                        data = "Worker warmup failed: " + ex.Message,
                        timestamp = DateTime.UtcNow
                    });
                }
                finally
                {
                    WorkerWarmupCompleted.TrySetResult(true);
                }
            });
        }

        // The first-touch warm pass needs a listable probe object. On a cold start the
        // warmup's single List/Objects attempt runs *before* the index is listable, so it
        // gets the IndexNotReady envelope (no items) — the previous single-shot resolve
        // dropped the entire pass there, and the agent's first read/inspect/edit paid the
        // one-time JIT/SDK first-touch cost instead (measured on a real KB: first read
        // 174ms, inspect 78ms, edit dry-run 29ms, against ~1ms for every following call).
        // The index-bootstrap path now waits — bounded — for a listable object and then
        // warms; the budget exists only so a permanently unavailable index cannot leave a
        // background loop alive. Do not move this wait into the pre-bootstrap warmup: the
        // bootstrap itself awaits WorkerWarmupCompleted.
        private const int WarmupProbeMaxAttempts = 40;
        private const int WarmupProbeResolveRetryDelayMs = 3000;

        // Test seam (same pattern as RespawnDelayForTest): the production retry interval
        // would make the bounded-wait regression test run the full 40 × 3s budget.
        internal static int? WarmupProbeRetryDelayMsForTest;
        internal static int WarmupProbeAttemptsForTest => WarmupProbeMaxAttempts;

        private static async Task<string?> ResolveWarmupProbeObjectAsync()
        {
            var listCommand = new JObject
            {
                ["module"] = "List",
                ["action"] = "Objects",
                ["target"] = string.Empty,
                // Prefer a real code object for the first-touch warm: Folders/Modules
                // (alphabetically first in the index) exercise almost no SDK path.
                // A Transaction or Procedure touches structure/source readers — the
                // paths inspect/analyze/read actually hit on the agent's first call.
                ["typeFilter"] = "Transaction,Procedure",
                ["limit"] = 1,
                ["offset"] = 0,
                ["client"] = "mcp"
            };

            var listResponse = await SendWorkerCommandAsync(
                listCommand,
                30000,
                "Warmup list timeout",
                workerResponse => workerResponse,
                (_, correlationId) => new JObject
                {
                    ["error"] = new JObject
                    {
                        ["message"] = "Warmup list operation timed out.",
                        ["correlationId"] = correlationId
                    }
                },
                toolName: "gateway_warmup_list",
                trackOperation: false);

            return ExtractWarmupProbeObjectName(listResponse?["result"]);
        }

        // Any reply shape that carries no listable object — the IndexNotReady envelope
        // while indexing, an error envelope, empty results — yields null so the caller
        // retries instead of warming nothing.
        internal static string? ExtractWarmupProbeObjectName(JToken? result)
        {
            JArray? items = null;
            if (result is JObject obj)
            {
                items = (obj["results"] ?? obj["objects"]) as JArray;
            }
            else if (result is JArray arr)
            {
                items = arr;
            }

            string? name = items?.FirstOrDefault()?["name"]?.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        // Bounded retry around the probe resolve. Extracted so the retry/fail-open
        // behavior is unit-testable without a live worker.
        internal static async Task<string?> AwaitWarmupProbeObjectAsync(
            Func<Task<string?>> resolve,
            int maxAttempts = WarmupProbeMaxAttempts,
            int retryDelayMs = WarmupProbeResolveRetryDelayMs,
            Action<int>? onRetry = null)
        {
            if (resolve == null) return null;
            int attempts = Math.Max(1, maxAttempts);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                string? name = null;
                try
                {
                    name = await resolve().ConfigureAwait(false);
                }
                catch
                {
                    // A failed probe resolve is a warmup miss, never a request failure.
                }

                if (!string.IsNullOrWhiteSpace(name)) return name;
                if (attempt == attempts) break;

                try { onRetry?.Invoke(attempt); } catch { }
                if (retryDelayMs > 0) await Task.Delay(retryDelayMs).ConfigureAwait(false);
            }

            return null;
        }

        // Runs the first-touch warm pass exactly once per gateway. Two callers race for it:
        // the fast path (index already listable right after pre-spawn) and the index-bootstrap
        // path (cold start, where List/Objects answers the IndexNotReady envelope until
        // BulkIndex finishes). Whoever claims the flag warms; the other returns immediately.
        private static int _firstTouchWarmClaimed;

        internal static async Task RunFirstTouchWarmOnceAsync(Func<Task<string?>> resolveProbe, Action? onWaitingForProbe, Func<string, Task>? warmPass = null)
        {
            if (Interlocked.CompareExchange(ref _firstTouchWarmClaimed, 1, 0) != 0) return;

            try
            {
                string? objectName = await AwaitWarmupProbeObjectAsync(
                    resolveProbe,
                    WarmupProbeMaxAttempts,
                    WarmupProbeRetryDelayMsForTest ?? WarmupProbeResolveRetryDelayMs,
                    attempt =>
                    {
                        if (attempt == 1) onWaitingForProbe?.Invoke();
                    });

                if (string.IsNullOrWhiteSpace(objectName))
                {
                    Log("[Warmup] No listable probe object within the wait budget; first-touch warm pass skipped.");
                    return;
                }

                await (warmPass ?? WarmFirstTouchPathsAsync)(objectName);
                Log("[Warmup] First-touch warm pass finished.");
            }
            catch (Exception ex)
            {
                Log("[Warmup] First-touch warm pass failed: " + ex.Message);
            }
        }

        internal static void ResetFirstTouchWarmForTest()
        {
            Interlocked.Exchange(ref _firstTouchWarmClaimed, 0);
        }

        // Pre-spawn the configured default KB's worker via the same AcquireAsync path the
        // explicit `genexus_kb action=open` uses. Fire-and-forget from initialize; errors
        // are swallowed (the regular resolve path re-tries on demand).
        private static async Task PrespawnDefaultKbWorkerAsync()
        {
            try
            {
                string? defaultAlias = GetConfiguredDefaultKb();
                var entry = (_activeConfig?.Environment?.KBs ?? new List<KbEntry>())
                    .FirstOrDefault(k => string.Equals(k.Alias, defaultAlias, StringComparison.OrdinalIgnoreCase));
                if (entry == null || _workerPool == null)
                {
                    Log("[Warmup] No default KB declared — skipping pre-spawn.");
                    return;
                }

                var handle = KbHandle.FromEntry(entry);
                Log($"[Warmup] Pre-spawning worker for default KB '{entry.Alias}' ({entry.Path})");
                await _workerPool.AcquireAsync(handle, CancellationToken.None);
                Log($"[Warmup] Pre-spawn of '{entry.Alias}' completed.");
            }
            catch (Exception ex)
            {
                // Non-fatal: the first real call falls back to the standard open path.
                Log("[Warmup] Default-KB pre-spawn skipped: " + ex.Message);
            }
        }

        // First-touch penalty warmer. Measured ([TOOL-LATENCY], scratch gateway vs real KB):
        // the FIRST call of each STA-heavy tool after a worker cold start pays a one-time
        // JIT/SDK-deserialization cost (inspect: up to 3.8s; analyze linter/callers: 0.6s+)
        // while every subsequent call returns in single-digit ms. Exercising those paths
        // here — in the background, right after pre-spawn/index bootstrap — moves that cost
        // out of the agent's turn entirely. Every sub-call is best-effort and individually
        // guarded: a warm failure must never break the warmup sequence.
        internal static List<(string toolName, JObject command)> BuildWarmupCommands(string probeObjectName)
        {
            var commands = new List<(string toolName, JObject command)>();
            foreach (var (canonicalToolName, toolArgs) in new[]
            {
                ("genexus_read",    new JObject { ["name"] = probeObjectName, ["part"] = "Structure" }),
                // Agents read Source, not Structure: the Source reader (ReadSource/ISource)
                // is a different SDK path than the Structure part walker, so warming only
                // Structure left the first Source read at ~150ms (measured).
                ("genexus_read",    new JObject { ["name"] = probeObjectName, ["part"] = "Source" }),
                ("genexus_inspect", new JObject { ["name"] = probeObjectName }),
                ("genexus_analyze", new JObject { ["mode"] = "linter", ["target"] = probeObjectName }),
                ("genexus_analyze", new JObject { ["mode"] = "callers", ["target"] = probeObjectName }),
                // The first Source search builds the worker's KB-wide source-scan cache on
                // the STA thread: measured 2.7s cold against ~1ms for every later search,
                // and the cost is pattern-independent (a zero-match pattern paid the same
                // 2.7s, so it is the scan and not the match). Every other SDK-heavy first
                // touch is warmed here for exactly this reason; a search was the one left
                // paying its cost inside the agent's turn. maxResults=1 keeps the warm
                // reply tiny — the scan is what we are paying for, not the result set.
                ("genexus_search_source", new JObject { ["pattern"] = probeObjectName, ["maxResults"] = 1 }),
            })
            {
                var converted = McpRouter.ConvertToolCall(new JObject
                {
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = canonicalToolName,
                        ["arguments"] = toolArgs
                    }
                });
                if (converted == null) continue;

                var workerCommand = JObject.FromObject(converted);
                workerCommand["client"] = "mcp";
                commands.Add((canonicalToolName, workerCommand));
            }
            return commands;
        }

        private static async Task WarmFirstTouchPathsAsync(string probeObjectName)
        {
            foreach (var (toolName, command) in BuildWarmupCommands(probeObjectName))
            {
                try
                {
                    await SendWorkerCommandAsync(
                        command,
                        30000,
                        $"Warmup {toolName} timeout",
                        wr => wr,
                        (_, correlationId) => new JObject(),
                        toolName: $"gateway_warmup_{toolName}",
                        trackOperation: false);
                }
                catch (OperationCanceledException) when (_gatewayLifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"[Warmup] {toolName} warm step skipped: {ex.Message}");
                }
            }
        }
    }
}
