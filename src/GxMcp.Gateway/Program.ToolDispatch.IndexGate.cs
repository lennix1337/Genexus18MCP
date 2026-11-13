using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>
        /// Gateway-side fast-fail for SDK-bound tools while the Worker index is not
        /// usable. Returns the refusal result, or null when the call may proceed to the
        /// Worker. Extracted from DispatchToolCallCoreAsync (phase: index gate).
        /// </summary>
        private static async Task<JObject?> TryRefuseWhenIndexNotReadyAsync(
            string tName,
            JObject? tArgs,
            CancellationToken transportCancellation)
        {
            // v2.6.9 perf: gateway-side fast-fail for SDK-bound tools when
            // the worker is still doing its initial BulkIndex on the STA thread.
            // Without this the request queues behind a 30-60s SDK enumeration
            // and the agent eats the full 60s gateway-timeout before learning
            // the index isn't ready. Once _lastKnownIndexState reports a usable
            // status we forward normally; when it doesn't, the block below does a
            // synchronous refresh-and-recheck before fast-failing so a ready index
            // isn't masked by a stale mirror. Worker-side ListService has its own
            // fast-fail for callers that bypass this short-circuit.
            //
            // Allow-list of tools that are blocked behind the STA thread and
            // therefore benefit from the short-circuit. Gateway-served tools
            // (whoami, recipe, kb_diff, sandbox, etc.) bypass naturally because they
            // never hit the worker dispatcher. genexus_doctor is deliberately NOT
            // listed: the Gateway answers it from its own state, and its one Worker
            // read, the WorkWithPlus template block, does not go through the index.
            if (IsIndexDependentTool(tName, tArgs))
            {
                IndexStateSnapshot idxSnap = GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias);
                bool indexUsable = IsIndexUsableForReads(idxSnap);
                if (!indexUsable)
                {
                    // The gateway's index mirror is only written by TryRefreshIndexStateFromWorkerAsync
                    // (whoami), so a "not usable" snapshot may just be STALE — the worker finished
                    // indexing but no whoami has refreshed the mirror since. Rather than fast-fail
                    // on a possibly-stale cache (which left agents stuck on IndexNotReady until they
                    // manually called whoami), do ONE bounded synchronous refresh and re-check.
                    // GetIndexState runs off the worker's STA thread, so this stays fast even while a
                    // cold-start BulkIndex is in flight. Skip the refresh when the mirror was just
                    // refreshed (index genuinely still building) so tight retry loops don't each pay
                    // a round-trip.
                    bool cacheStale = idxSnap == null
                        || idxSnap.RefreshedAtUtc == DateTime.MinValue
                        || (DateTime.UtcNow - idxSnap.RefreshedAtUtc).TotalSeconds > 2;
                    if (cacheStale && await TryRefreshIndexStateFromWorkerAsync(
                        timeoutMs: 1200, kbAlias: _currentKb.Value?.NormalizedAlias))
                    {
                        idxSnap = GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias);
                        indexUsable = IsIndexUsableForReads(idxSnap);
                    }
                }
                if (!indexUsable)
                {
                    // A KB the Worker failed to open never leaves Cold: report why, do not make the
                    // caller wait out the settle window and loop on IndexNotReady (issue #957).
                    JObject? openFailed = BuildKbOpenFailedEnvelope(idxSnap);
                    if (openFailed != null)
                    {
                        Log($"[IndexGate] closed tool={tName} kbOpenFailed={openFailed["message"]}");
                        return BuildToolResultContent(openFailed, true, tName, tArgs);
                    }
                }
                if (!indexUsable)
                {
                    // Cold path: right after a KB open the bootstrap settles the index mirror, and
                    // this call can land inside that short window. Joining that settle is bounded
                    // and turns the agent's first index-dependent call into a real result instead
                    // of an IndexNotReady envelope it must retry — measured against a real KB, the
                    // envelope cost a full extra turn plus the advertised retryAfterMs backoff on
                    // an index that became usable ~250ms later.
                    //
                    // The join alone is not enough: a call that reaches this gate in the same
                    // instant the bootstrap publishes its settle loses the race and fast-fails
                    // (observed intermittently with genexus_search_source as the first call). So
                    // when the mirror reports a restored snapshot still awaiting its delta, this
                    // gate settles the mirror itself — same bounded routine, same budget — making
                    // the outcome independent of that race. Any other not-ready state (a genuine
                    // first-ever build) falls straight through to the envelope below.
                    string? currentKbAlias = _currentKb.Value?.NormalizedAlias;
                    var settleInFlight = GetIndexMirrorSettleInFlight(currentKbAlias);
                    if (settleInFlight != null)
                    {
                        if (!settleInFlight.IsCompleted)
                        {
                            // Link the caller's token with gateway shutdown so either one ends
                            // the wait. A completed settle is still meaningful: do not start a
                            // second budget-expensive refresh after it has already given up.
                            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                                _gatewayLifetime.Token, transportCancellation);
                            try
                            {
                                await Task.WhenAny(settleInFlight,
                                    Task.Delay(IndexMirrorSettleGateWaitCeilingMs, waitCts.Token));
                            }
                            catch { }
                        }
                    }
                    else if (IsRestoredSnapshotAwaitingDeltaForTest(idxSnap?.Status, idxSnap?.Freshness))
                    {
                        await SettleIndexMirrorAfterBootstrapAsync(
                            kbAlias: currentKbAlias,
                            cancellationToken: transportCancellation);
                    }
                    idxSnap = GetLastKnownIndexState(_currentKb.Value?.NormalizedAlias);
                    indexUsable = IsIndexUsableForReads(idxSnap);
                }
                if (!indexUsable)
                {
                    // This envelope is silent otherwise, and "which field closed the gate, and how
                    // old is the mirror it came from" is the first question when the agent's cold
                    // start eats Indexing envelopes against an index that is already usable.
                    long mirrorAgeMs = idxSnap == null || idxSnap.RefreshedAtUtc == DateTime.MinValue
                        ? -1
                        : (long)(DateTime.UtcNow - idxSnap.RefreshedAtUtc).TotalMilliseconds;
                    Log($"[IndexGate] closed tool={tName} status={idxSnap?.Status ?? "<null>"} "
                        + $"freshness={idxSnap?.Freshness ?? "<null>"} "
                        + $"opState={idxSnap?.OperationState ?? "<null>"} mirrorAgeMs={mirrorAgeMs}");
                    return BuildToolResultContent(
                        BuildIndexNotReadyEnvelope(
                            idxSnap?.Status, idxSnap?.Freshness, idxSnap?.TotalObjects ?? 0,
                            idxSnap?.Progress, idxSnap?.EtaMs, idxSnap?.OperationId,
                            idxSnap?.OperationState, idxSnap?.WorkerAlive,
                            idxSnap?.Recoverable, idxSnap?.Stalled),
                        false, tName, tArgs);
                }
            }

            return null;
        }
    }
}
