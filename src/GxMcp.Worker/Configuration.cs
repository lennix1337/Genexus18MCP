using System;
using System.Configuration;

namespace GxMcp.Worker
{
    /// <summary>
    /// Centralised typed accessors over App.config &lt;appSettings&gt;. Keep this small —
    /// only host flags that gate worker behaviour at runtime (perf knobs, feature flags).
    /// </summary>
    public static class Configuration
    {
        // Shared accessor: an appSetting bool that defaults to true (on) and is also true on
        // any read/parse failure. All the indexing feature flags share these semantics.
        private static bool BoolSetting(string key, bool defaultValue = true)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings[key];
                if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
                return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return defaultValue;
            }
        }

        // SP6.T6 — gate the new lite-pass + lazy-enrichment indexing pipeline.
        // Defaults to true (fast path on). Set Indexing.UseLitePass=false in App.config
        // to fall back to the legacy monolithic BulkIndex path for one release.
        public static bool UseLitePass => BoolSetting("Indexing.UseLitePass");

        // Fase 1 — gate the persistible delta-on-open path. Defaults to true. Set
        // Indexing.UseDeltaOnOpen=false in App.config to fall back to the previous
        // "load cache + trust forever" warm-start behaviour (no automatic delta).
        public static bool UseDeltaOnOpen => BoolSetting("Indexing.UseDeltaOnOpen");

        // Post-upgrade write-starvation fix — gate the delta-across-worker-DLL path.
        // Defaults to true. Every MCP version upgrade changes the worker DLL hash →
        // DllMatch=False → a full 38k re-walk that sets _isIndexing and BLOCKS ALL WRITES
        // (EnsureNotIndexing) for its whole duration. When this is on and only the DLL
        // differs (schema still matches → index layout unchanged), we run the bounded
        // DELTA refresh instead and let it re-baseline the sidecar to the new DLL hash.
        // Tradeoff: enrichment-LOGIC changes in the new release won't retro-apply to
        // UNCHANGED objects until a forced reindex (genexus_lifecycle action=index
        // force=true). In LazyEnrichment mode (default) those objects are re-enriched
        // on demand anyway, so the staleness window is invisible in practice. Set
        // Indexing.DeltaAcrossWorkerDll=false to restore the full-rebuild-on-DLL-change
        // behaviour (always re-enriches everything, but starves writes on large KBs).
        public static bool DeltaAcrossWorkerDll => BoolSetting("Indexing.DeltaAcrossWorkerDll");

        // Fase 3 — lazy/on-demand enrichment. Defaults to true. When true, the index build
        // stops after the lite catalogue (LiteReady→Ready) and does NOT eagerly drain all
        // objects through enrichment (measured ~20min on a 38.6k KB, ~91% STA-bound SDK reads,
        // and mostly wasted since most objects are never queried). Edges/snippets/embeddings
        // are filled in on demand via EnrichmentQueue.PromoteAsync when a tool needs a target.
        // Set Indexing.LazyEnrichment=false to restore the eager full-KB enrichment drain.
        public static bool LazyEnrichment => BoolSetting("Indexing.LazyEnrichment");

        /// <summary>
        /// Source-store background population mode.  auto enables bounded P2 slices;
        /// off leaves the store entirely lazy.  The environment variable is used by
        /// the Gateway-launched Worker, while App.config supports direct launches.
        /// </summary>
        public static bool SourceStoreBackfillEnabled
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("GXMCP_SOURCE_STORE_BACKFILL");
                if (!string.IsNullOrWhiteSpace(env))
                    return !string.Equals(env.Trim(), "off", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(env.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(env.Trim(), "0", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var raw = ConfigurationManager.AppSettings["Server.SourceStoreBackfill"];
                    if (!string.IsNullOrWhiteSpace(raw))
                        return !string.Equals(raw.Trim(), "off", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(raw.Trim(), "0", StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                return true;
            }
        }

        public static int SourceStoreMaxMB
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("GXMCP_SOURCE_STORE_MAX_MB");
                if (int.TryParse(env, out int envMb) && envMb > 0) return envMb;
                try
                {
                    var raw = ConfigurationManager.AppSettings["Server.SourceStoreMaxMB"];
                    if (int.TryParse(raw, out int cfgMb) && cfgMb > 0) return cfgMb;
                }
                catch { }
                return 512;
            }
        }
    }
}
