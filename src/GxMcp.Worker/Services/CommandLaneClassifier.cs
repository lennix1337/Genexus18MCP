using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Decides whether a Worker command may run on the parallel lane or must wait for the
    /// SDK (STA) lane. The body was moved unchanged from <c>CommandDispatcher.IsThreadSafe</c>
    /// so the shared-host broker can classify a request without loading the GeneXus SDK
    /// (issue #854). It may use only the BCL, Newtonsoft and the SDK-free half of
    /// <see cref="WriteDestinationGuard"/>.
    /// </summary>
    internal static class CommandLaneClassifier
    {
        internal static bool IsThreadSafe(JObject request)
        {
            try
            {
                string method = request["method"]?.ToString();
                string action = request["action"]?.ToString();

                if (string.IsNullOrEmpty(method)) return false;
                // ToLower() é culture-aware: em culturas Turkish-I um método contendo
                // "I" não bate com a whitelist e muda de thread de execução conforme
                // a cultura do SO.
                method = method.ToLowerInvariant();

                // A configured fence may inspect KBVersion via the SDK. Unknown commands
                // must therefore stay on STA even if legacy scheduling accepted the module.
                if (WriteDestinationGuard.IsConfigured
                    && !WriteDestinationGuard.IsObservation(method, action, request["params"] as JObject))
                    return false;

                // Only allow strictly non-SDK or pure read-cache operations to bypass STA thread
                if (method == "ping" || method == "health")
                    return true;

                // The active-model snapshot is published by the SDK thread and read without the SDK.
                if (method == "kbversion" && string.Equals(action, "ActiveModel", StringComparison.OrdinalIgnoreCase))
                    return true;

                // issue #26 P2: 'search' is index-only (in-memory) EXCEPT SearchSource,
                // which reads object Source/WebForm XML via the GeneXus SDK and MUST run
                // on the STA thread. Routing SearchSource to the MTA thread-pool path
                // caused a native COM access-violation that crashed the worker 100% of the time.
                if (method == "search" && !string.Equals(action, "SearchSource", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (method == "list")
                    return true;

                // v2.3.8 (post-Task 7.2 fix): Control:Cancel must run on the parallel
                // dispatch path so it can interleave with an in-flight SDK call and
                // trip the registered CTS. Pure in-memory dictionary lookup, no SDK.
                if (method == "control" && action == "Cancel")
                    return true;

                // GetIndexStatus only reads static volatile fields – no SDK access
                if (method == "kb" && action == "GetIndexStatus")
                    return true;
                // v2.3.8 Task 1.2: GetIndexState reads in-memory IndexCacheService snapshot only – no SDK access
                if (method == "kb" && action == "GetIndexState")
                    return true;
                // Root-cause fix (Table-shadow auto-injection): GetNameTypeMap scans the
                // in-memory index only – no SDK access (see the action body below).
                if (method == "kb" && action == "GetNameTypeMap")
                    return true;

                // D21: health/status polls must never be starved behind an in-flight
                // long SDK op (build/index/edit) on the single STA queue.
                // - doctor: filesystem + loaded-assembly checks only (no KB/COM touch). The
                //   WwpTemplates action reads KB objects through the SDK, so it stays on the STA
                //   thread with the other SDK commands.
                // - build Status/Result: lock-guarded read of the in-memory _tasks
                //   dict (the very "is my build done?" poll that must not queue behind
                //   the build it's asking about).
                if (method == "doctor")
                    return !string.Equals(action, "WwpTemplates", StringComparison.OrdinalIgnoreCase);
                if (method == "build" && (action == "Status" || action == "Result"))
                    return true;

                // issue #342: ReadLogs is the diagnostic route an agent reaches for when
                // something has already gone wrong - a blocked SDK lane being the usual
                // reason. It read the log file and returned, touching no KBObject, no COM
                // object and no SDK type, yet it was dispatched through the very lane that
                // might be blocked, so the one request that could explain the blockage
                // waited behind the blockage. It is pure file I/O plus in-process
                // filtering, so it belongs on the parallel path with the other
                // diagnostic reads above.
                //
                // This is the audit the issue asks for, stated explicitly: ReadLogs
                // reaches only Logger.LogDirectory, the log file itself,
                // CommandDispatcher.EscapeJsonString and SourceSearchService.RegexMatchTimeout.
                // None of those is SDK-owned. Anything that later makes ReadLogs consult
                // the KB must remove this whitelist entry in the same change - a stale
                // whitelist here would be a COM access violation, not a slowdown.
                if (method == "object" && action == "ReadLogs")
                    return true;

                // issue #342: the SDK-lane liveness probe must answer *while* the SDK lane
                // is blocked - that is the only moment it has anything to report. Routing
                // it through the STA would make it wait behind the blockage it exists to
                // detect, and the gateway would read the timeout as "unknown" forever.
                // Reads volatile fields and a lock-guarded counter; touches no SDK object.
                if (method == "system" && action == "GetSdkBusyStatus")
                    return true;

                // Any operation interacting with GeneXus SDK (COM objects) MUST run in the STA thread to prevent corruption
                return false;
            }
            catch { return false; }
        }
    }
}
