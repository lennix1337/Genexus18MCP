using System;
using System.Text.RegularExpressions;

namespace GxMcp.Gateway
{
    internal enum LifecycleAliasKind
    {
        None,
        Job,
        Tracker,
        Worker
    }

    /// <summary>
    /// One normalized lookup result for every lifecycle operation namespace.  The
    /// public lifecycle surface intentionally accepts op:&lt;id&gt;, a bare Gateway
    /// id, and a legacy Worker taskId; callers must not grow separate prefix checks.
    /// </summary>
    internal sealed class LifecycleOperationAliasResolution
    {
        public string Input { get; init; } = string.Empty;
        public string NormalizedId { get; init; } = string.Empty;
        public LifecycleAliasKind Kind { get; init; }
        public JobEntry? Job { get; init; }
        public string? TrackerOperationId { get; init; }
        public string? WorkerTaskId { get; init; }
        public bool IsMalformed { get; init; }
        public bool IsKnown => Kind != LifecycleAliasKind.None || IsMalformed;
    }

    internal static class LifecycleOperationAliasResolver
    {
        private static readonly Regex GatewayId = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex WorkerTaskId = new("^[0-9a-fA-F]{8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static LifecycleOperationAliasResolution Resolve(
            string? target,
            BackgroundJobRegistry? jobs,
            OperationTracker? tracker)
        {
            string input = target?.Trim() ?? string.Empty;
            if (input.Length == 0)
                return new LifecycleOperationAliasResolution { Input = input, Kind = LifecycleAliasKind.None };

            bool prefixed = input.StartsWith("op:", StringComparison.OrdinalIgnoreCase);
            string normalized = prefixed ? input.Substring(3).Trim() : input;
            if (normalized.Length == 0 || normalized.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
            {
                return new LifecycleOperationAliasResolution
                {
                    Input = input,
                    NormalizedId = normalized,
                    IsMalformed = true
                };
            }

            if (jobs != null && jobs.TryResolveLifecycleAlias(normalized, out var job) && job != null)
            {
                return new LifecycleOperationAliasResolution
                {
                    Input = input,
                    NormalizedId = job.Id,
                    Kind = LifecycleAliasKind.Job,
                    Job = job,
                    WorkerTaskId = job.WorkerTaskId
                };
            }

            if (tracker != null && tracker.TryGetContext(normalized, out _, out _))
            {
                return new LifecycleOperationAliasResolution
                {
                    Input = input,
                    NormalizedId = normalized,
                    Kind = LifecycleAliasKind.Tracker,
                    TrackerOperationId = normalized
                };
            }

            // A well-formed Worker task id is a valid legacy namespace even when
            // this Gateway has not yet observed the acknowledgement.  Let the normal
            // worker status/result path handle it instead of mislabeling it expired.
            if (WorkerTaskId.IsMatch(normalized))
            {
                return new LifecycleOperationAliasResolution
                {
                    Input = input,
                    NormalizedId = normalized,
                    Kind = LifecycleAliasKind.Worker,
                    WorkerTaskId = normalized
                };
            }

            // Lifecycle status/result/cancel never target an object name.  A bare
            // non-8/32-hex value is therefore a format error, not an expired task.
            if (!GatewayId.IsMatch(normalized))
            {
                return new LifecycleOperationAliasResolution
                {
                    Input = input,
                    NormalizedId = normalized,
                    IsMalformed = true
                };
            }

            return new LifecycleOperationAliasResolution
            {
                Input = input,
                NormalizedId = normalized,
                Kind = LifecycleAliasKind.None
            };
        }

        internal static bool IsGatewayId(string? value)
            => !string.IsNullOrWhiteSpace(value) && GatewayId.IsMatch(value);

        internal static bool IsWorkerTaskId(string? value)
            => !string.IsNullOrWhiteSpace(value) && WorkerTaskId.IsMatch(value);
    }
}
