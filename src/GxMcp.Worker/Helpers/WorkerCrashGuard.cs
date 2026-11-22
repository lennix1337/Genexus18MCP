#nullable enable
using System;
using System.Runtime.InteropServices;

namespace GxMcp.Worker.Helpers
{
    // Classifies corrupted-state (native/SDK) exceptions so ProcessCommand can turn a would-be
    // silent process death into a structured error + clean respawn. Kept separate from Program so
    // the decision is unit-testable without touching process lifetime (Environment.Exit stays in
    // the caller). Paired with App.config's legacyCorruptedStateExceptionsPolicy (issue #35
    // worker-stability hardening).
    public static class WorkerCrashGuard
    {
        // True when the exception indicates the CLR / native heap may be inconsistent — the
        // GeneXus COM-flavoured SDK raises these (AccessViolation, raw SEH) on some complex edits.
        // The current call is recovered with an error, then the worker MUST exit rather than keep
        // serving a poisoned AppDomain. StackOverflow is uncatchable and never reaches here.
        public static bool IsCorruptedState(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AccessViolationException) return true;
                if (e is SEHException) return true;
            }
            return false;
        }

        // Short, log/telemetry-friendly reason string for a corrupted-state exception.
        public static string CrashReason(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AccessViolationException) return "AccessViolation";
                if (e is SEHException seh) return "SEHException:0x" + seh.ErrorCode.ToString("X8");
            }
            return ex?.GetType().Name ?? "Unknown";
        }

        public static string? FormatExitCodeHex(int? exitCode)
        {
            return exitCode.HasValue ? "0x" + exitCode.Value.ToString("X8") : null;
        }

        public static string? ExitCodeStatusName(int exitCode)
        {
            switch ((uint)exitCode)
            {
                case 0xC0000005: return "ACCESS_VIOLATION";
                case 0xC00000FD: return "STACK_OVERFLOW";
                case 0xC0000374: return "HEAP_CORRUPTION";
                case 0xC0000409: return "STACK_BUFFER_OVERRUN";
                default: return null;
            }
        }
    }
}
