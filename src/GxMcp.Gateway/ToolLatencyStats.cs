using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // In-memory, per-tool latency aggregate. The gateway had no per-tool timing at all —
    // only the index build and KB-open logged elapsed, so read/write/edit latency was
    // invisible and "the worker is slow" was unmeasurable. This records the end-to-end
    // gateway→worker→gateway time for each tool call (excluding worker cold-start, which is
    // awaited before the sample starts), logs one [TOOL-LATENCY] line per call, and keeps a
    // rolling aggregate surfaced in whoami so slow paths are identifiable instead of guessed.
    public static class ToolLatencyStats
    {
        private sealed class Agg
        {
            public long Count;
            public long TotalMs;
            public long MaxMs;
            public long LastMs;
            public string? LastAtUtc;
            public long QueueWaitTotalMs;
            public long StartupTotalMs;
            public long SdkTotalMs;
            public long TransformTotalMs;
            public long SerializeTotalMs;
            public long ResponseBytesTotal;
            public readonly List<long> Samples = new List<long>(128);
            public readonly Dictionary<string, long> ResultClasses = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, long> CacheOutcomes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly ConcurrentDictionary<string, Agg> _stats =
            new ConcurrentDictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        // Export is opt-in: without an ActivityListener/MeterListener these produce
        // no spans or external traffic, while hosts that already configure OpenTelemetry
        // can subscribe to the stable source names without changing MCP payloads.
        internal static readonly ActivitySource ActivitySource = new ActivitySource("Genexus.Mcp.Gateway");
        internal static readonly Meter Meter = new Meter("Genexus.Mcp.Gateway", "3.0.0");
        private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("genexus_mcp_tool_calls");
        private static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>("genexus_mcp_tool_duration_ms", "ms");

        public static void Record(
            string? tool,
            double ms,
            string? resultClass = null,
            long queueWaitMs = 0,
            long responseBytes = 0,
            long startupMs = 0,
            long sdkMs = 0,
            long transformMs = 0,
            long serializeMs = 0,
            string? cacheOutcome = null)
        {
            if (string.IsNullOrEmpty(tool)) tool = "unknown";
            // Ignore internal/heartbeat noise so the aggregate reflects real tool calls.
            if (string.Equals(tool, "ping", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool, "heartbeat", StringComparison.OrdinalIgnoreCase))
                return;

            try { tool = OperationClassifier.Describe(tool, null).CanonicalName; }
            catch { /* instrumentation must remain best-effort */ }

            long m = ms <= 0 ? 0 : (long)Math.Round(ms);
            string resultLabel = string.IsNullOrWhiteSpace(resultClass) ? "success" : resultClass.Trim().ToLowerInvariant();
            string cacheLabel = string.IsNullOrWhiteSpace(cacheOutcome) ? "unknown" : cacheOutcome.Trim().ToLowerInvariant();
            try
            {
                using (var activity = ActivitySource.StartActivity("genexus.mcp.tool"))
                {
                    activity?.SetTag("mcp.tool", tool);
                    activity?.SetTag("mcp.result", resultLabel);
                    activity?.SetTag("mcp.cache", cacheLabel);
                }
                var tags = new TagList
                {
                    { "mcp.tool", tool! },
                    { "mcp.result", resultLabel },
                    { "mcp.cache", cacheLabel }
                };
                ToolCalls.Add(1, tags);
                ToolDuration.Record(Math.Max(0, ms), tags);
            }
            catch { /* observability listeners must never break a tool call */ }
            var a = _stats.GetOrAdd(tool!, _ => new Agg());
            lock (a)
            {
                a.Count++;
                a.TotalMs += m;
                if (m > a.MaxMs) a.MaxMs = m;
                a.LastMs = m;
                a.LastAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                a.QueueWaitTotalMs += Math.Max(0, queueWaitMs);
                a.StartupTotalMs += Math.Max(0, startupMs);
                a.SdkTotalMs += Math.Max(0, sdkMs);
                a.TransformTotalMs += Math.Max(0, transformMs);
                a.SerializeTotalMs += Math.Max(0, serializeMs);
                a.ResponseBytesTotal += Math.Max(0, responseBytes);
                if (a.Samples.Count == 128) a.Samples.RemoveAt(0);
                a.Samples.Add(m);
                a.ResultClasses[resultLabel] = a.ResultClasses.TryGetValue(resultLabel, out var count) ? count + 1 : 1;
                a.CacheOutcomes[cacheLabel] = a.CacheOutcomes.TryGetValue(cacheLabel, out var cacheCount) ? cacheCount + 1 : 1;
            }
        }

        // Top tools by total time spent (where the session's time actually went), with the
        // per-call count / avg / max / last so a single slow call and a chatty-but-cheap tool
        // are distinguishable.
        public static JObject Summarize(int topN = 10)
        {
            var result = new JObject();
            var arr = new JArray();
            var snapshot = _stats.ToArray();
            long grandTotal = 0;
            long grandCount = 0;
            long grandQueueWait = 0;
            long grandStartup = 0;
            long grandSdk = 0;
            long grandTransform = 0;
            long grandSerialize = 0;
            long grandResponseBytes = 0;

            var ranked = snapshot
                .Select(kv =>
                {
                    long count, total, max, last, queueWait, startup, sdk, transform, serialize, responseBytes, p50, p95;
                    string? lastAt;
                    Dictionary<string, long> resultClasses;
                    Dictionary<string, long> cacheOutcomes;
                    var a = kv.Value;
                    lock (a)
                    {
                        count = a.Count; total = a.TotalMs; max = a.MaxMs; last = a.LastMs;
                        lastAt = a.LastAtUtc; queueWait = a.QueueWaitTotalMs; startup = a.StartupTotalMs;
                        sdk = a.SdkTotalMs; transform = a.TransformTotalMs; serialize = a.SerializeTotalMs;
                        responseBytes = a.ResponseBytesTotal;
                        p50 = Percentile(a.Samples, 0.50); p95 = Percentile(a.Samples, 0.95);
                        resultClasses = new Dictionary<string, long>(a.ResultClasses, StringComparer.OrdinalIgnoreCase);
                        cacheOutcomes = new Dictionary<string, long>(a.CacheOutcomes, StringComparer.OrdinalIgnoreCase);
                    }
                    return (tool: kv.Key, count, total, max, last, lastAt, queueWait, startup, sdk, transform, serialize, responseBytes, p50, p95, resultClasses, cacheOutcomes);
                })
                .OrderByDescending(x => x.total)
                .ToList();

            foreach (var x in ranked)
            {
                grandTotal += x.total;
                grandCount += x.count;
                grandQueueWait += x.queueWait;
                grandStartup += x.startup;
                grandSdk += x.sdk;
                grandTransform += x.transform;
                grandSerialize += x.serialize;
                grandResponseBytes += x.responseBytes;
            }

            foreach (var x in ranked.Take(Math.Max(0, topN)))
            {
                arr.Add(new JObject
                {
                    ["tool"] = x.tool,
                    ["count"] = x.count,
                    ["avgMs"] = x.count > 0 ? (long)Math.Round((double)x.total / x.count) : 0,
                    ["maxMs"] = x.max,
                    ["lastMs"] = x.last,
                    ["totalMs"] = x.total,
                    ["p50Ms"] = x.p50,
                    ["p95Ms"] = x.p95,
                    ["avgQueueWaitMs"] = x.count > 0 ? (long)Math.Round((double)x.queueWait / x.count) : 0,
                    ["avgStartupMs"] = x.count > 0 ? (long)Math.Round((double)x.startup / x.count) : 0,
                    ["avgSdkMs"] = x.count > 0 ? (long)Math.Round((double)x.sdk / x.count) : 0,
                    ["avgTransformMs"] = x.count > 0 ? (long)Math.Round((double)x.transform / x.count) : 0,
                    ["avgSerializeMs"] = x.count > 0 ? (long)Math.Round((double)x.serialize / x.count) : 0,
                    ["avgResponseBytes"] = x.count > 0 ? (long)Math.Round((double)x.responseBytes / x.count) : 0,
                    ["resultClasses"] = JObject.FromObject(x.resultClasses),
                    ["cacheOutcomes"] = JObject.FromObject(x.cacheOutcomes),
                    ["lastAtUtc"] = x.lastAt
                });
            }

            result["totalCalls"] = grandCount;
            result["totalMs"] = grandTotal;
            result["avgQueueWaitMs"] = grandCount > 0 ? (long)Math.Round((double)grandQueueWait / grandCount) : 0;
            result["avgStartupMs"] = grandCount > 0 ? (long)Math.Round((double)grandStartup / grandCount) : 0;
            result["avgSdkMs"] = grandCount > 0 ? (long)Math.Round((double)grandSdk / grandCount) : 0;
            result["avgTransformMs"] = grandCount > 0 ? (long)Math.Round((double)grandTransform / grandCount) : 0;
            result["avgSerializeMs"] = grandCount > 0 ? (long)Math.Round((double)grandSerialize / grandCount) : 0;
            result["avgResponseBytes"] = grandCount > 0 ? (long)Math.Round((double)grandResponseBytes / grandCount) : 0;
            result["byTool"] = arr;
            return result;
        }

        private static long Percentile(List<long> samples, double quantile)
        {
            if (samples == null || samples.Count == 0) return 0;
            var ordered = samples.OrderBy(v => v).ToArray();
            int index = (int)Math.Ceiling(quantile * ordered.Length) - 1;
            index = Math.Max(0, Math.Min(index, ordered.Length - 1));
            return ordered[index];
        }

        internal static void ResetForTest() => _stats.Clear();
    }
}
