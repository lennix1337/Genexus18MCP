using System;

namespace GxMcp.Worker.Models
{
    public class IndexState
    {
        public string Status { get; set; } = "Cold";
        // Availability and freshness are deliberately separate. A restored snapshot
        // can serve reads while it is still stale relative to the open KB.
        public string Freshness { get; set; } = "stale";
        public DateTime? LastIndexedAt { get; set; }
        public DateTime? LastSuccessfulScanAt { get; set; }
        public int TotalObjects { get; set; }
        public double? Progress { get; set; }    // 0..1, only when Reindexing
        public int? EtaMs { get; set; }          // only when Reindexing
        public DateTime? LitePassCompletedUtc { get; set; }
        public DateTime? EnrichmentStartedUtc { get; set; }
        // Build activity is separate from index availability. A stalled worker can
        // keep the cache unavailable without being silently cancelled by a status poll.
        public string OperationId { get; set; }
        public string OperationState { get; set; } = "Idle";
        public bool WorkerAlive { get; set; }
        public bool Recoverable { get; set; }
        public bool Stalled { get; set; }
        public DateTime? LastProgressAtUtc { get; set; }
        public DateTime? StalledAtUtc { get; set; }
    }

    /// <summary>
    /// Issue #209 (policy A): the freshness gate is deliberately fail-closed, so the
    /// awaitable half of that policy has to be a first-class predicate. A Status-only wait
    /// returned immediately on a warm start (Status=Ready with Freshness=stale after
    /// MarkIndexRestored) and could never observe the delta that republishes
    /// Freshness=current. `lifecycle action=status wait=... freshness=current` evaluates this.
    /// </summary>
    internal static class IndexWaitPolicy
    {
        /// <param name="since">Prior status to leave; empty means "block until Ready".</param>
        /// <param name="wantFreshness">Optional freshness target (e.g. "current"); empty means "any".</param>
        internal static bool IsSatisfied(IndexState state, string since, string wantFreshness)
        {
            string status = state?.Status ?? "Cold";
            bool statusSatisfied = string.IsNullOrEmpty(since)
                ? string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase)
                : !string.Equals(status, since, StringComparison.OrdinalIgnoreCase);
            bool freshnessSatisfied = string.IsNullOrEmpty(wantFreshness)
                || string.Equals(state?.Freshness, wantFreshness, StringComparison.OrdinalIgnoreCase);
            return statusSatisfied && freshnessSatisfied;
        }
    }
}
