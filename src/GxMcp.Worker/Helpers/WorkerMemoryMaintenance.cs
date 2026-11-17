using System;
using System.Runtime;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Bounded SDK cache eviction and LOH compaction helpers for the x86 Worker.
    /// Keeps long-lived SDK caches populated by full-KB walks (bulk index, source-store
    /// backfill) from pinning hundreds of MB of managed/LOH memory after the walk finishes.
    /// </summary>
    internal static class WorkerMemoryMaintenance
    {
        /// <summary>
        /// Clears the GeneXus SDK's in-memory <c>DesignModel.Objects</c> cache when it
        /// implements <see cref="IKBModelObjectsCacheConfiguration"/>. Best-effort: an SDK
        /// implementation without cache configuration or a transient failure returns false.
        /// Must be called on the SDK STA thread or when no SDK object mutation is in flight.
        /// </summary>
        internal static bool ResetSdkObjectCaches(object designModelObjects)
        {
            if (designModelObjects is IKBModelObjectsCacheConfiguration cacheConfig)
            {
                try
                {
                    cacheConfig.ResetCaches();
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Debug("[MEM-MAINT] ResetCaches failed: " + ex.Message);
                }
            }
            return false;
        }

        /// <summary>
        /// Performs one LOH-compacting gen-2 GC and returns the managed heap size in MB
        /// after collection. Respects <c>GXMCP_IDLE_GC=0</c> opt-out.
        /// </summary>
        internal static long CompactLohOnce(string reason)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("GXMCP_IDLE_GC"), "0", StringComparison.OrdinalIgnoreCase))
                return GC.GetTotalMemory(false) / (1024 * 1024);

            long beforeMb = GC.GetTotalMemory(false) / (1024 * 1024);
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            long afterMb = GC.GetTotalMemory(true) / (1024 * 1024);
            Logger.Info($"[MEM-MAINT] reason={reason} gcMemMB {beforeMb}->{afterMb}");
            return afterMb;
        }
    }
}
