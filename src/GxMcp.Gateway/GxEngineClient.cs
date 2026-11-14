using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GxMcp.Gateway
{
    /// <summary>Outcome of one engine version probe.</summary>
    internal sealed class GxEngineProbe
    {
        private GxEngineProbe(bool available, string? executable, string? version, string? errorCode, string? errorMessage)
        {
            Available = available;
            Executable = executable;
            Version = version;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        internal bool Available { get; }
        internal string? Executable { get; }
        internal string? Version { get; }
        internal string? ErrorCode { get; }
        internal string? ErrorMessage { get; }

        internal static GxEngineProbe Ok(string executable, string version) =>
            new GxEngineProbe(true, executable, version, null, null);

        internal static GxEngineProbe Unavailable(string? executable, string errorCode, string errorMessage) =>
            new GxEngineProbe(false, executable, null, errorCode, errorMessage);
    }

    /// <summary>
    /// Outcome of one gx-engine capability invocation (kb-read, kb-query,
    /// kb-write, rebuild-all). Failures carry a stable code so routing can
    /// decide between falling back to the SDK and refusing the call.
    /// </summary>
    internal sealed class GxEngineInvocation
    {
        private GxEngineInvocation(
            bool ok, string? executable, string standardOutput, string standardError,
            int? exitCode, bool timedOut, string? errorCode, string? errorMessage)
        {
            Ok = ok;
            Executable = executable;
            StandardOutput = standardOutput;
            StandardError = standardError;
            ExitCode = exitCode;
            TimedOut = timedOut;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        internal bool Ok { get; }
        internal string? Executable { get; }
        internal string StandardOutput { get; }
        internal string StandardError { get; }
        internal int? ExitCode { get; }
        internal bool TimedOut { get; }
        internal string? ErrorCode { get; }
        internal string? ErrorMessage { get; }

        internal static GxEngineInvocation Success(string executable, string stdout) =>
            new GxEngineInvocation(true, executable, stdout, string.Empty, 0, false, null, null);

        internal static GxEngineInvocation Failure(
            string? executable, string errorCode, string errorMessage,
            string stdout = "", string stderr = "", int? exitCode = null, bool timedOut = false) =>
            new GxEngineInvocation(false, executable, stdout, stderr, exitCode, timedOut, errorCode, errorMessage);
    }

    /// <summary>
    /// The Gateway-side client for the experimental gx-engine backend. It
    /// resolves the executable, runs the <c>--version</c> handshake, and executes
    /// the capability invocations the routing gate selects (kb-read, kb-query,
    /// kb-write, rebuild-all) under the configured Engine.TimeoutSeconds budget.
    /// </summary>
    internal static class GxEngineClient
    {
        internal const string VersionLinePrefix = "gx-engine ";

        /// <summary>
        /// Version probes answer a diagnostic call and must never stall it; the
        /// build/invocation budget (Engine.TimeoutSeconds) is deliberately not
        /// used here.
        /// </summary>
        internal const int ProbeTimeoutSeconds = 10;

        /// <summary>
        /// Parses the <c>--version</c> line: exactly <c>gx-engine &lt;version&gt;</c>
        /// on the first non-empty line, so a stray compiler warning cannot be
        /// mistaken for the handshake.
        /// </summary>
        internal static bool TryParseVersionOutput(string? stdout, out string version)
        {
            version = string.Empty;
            if (string.IsNullOrWhiteSpace(stdout)) return false;

            foreach (string rawLine in stdout.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (!line.StartsWith(VersionLinePrefix, StringComparison.Ordinal)) return false;
                string candidate = line.Substring(VersionLinePrefix.Length).Trim();
                if (candidate.Length == 0 || candidate.Contains(' ')) return false;
                version = candidate;
                return true;
            }

            return false;
        }

        internal static async Task<GxEngineProbe> ProbeAsync(EngineBackendSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            string? executable = GxEngineLocator.Locate(settings);
            if (executable == null)
            {
                string detail = string.IsNullOrWhiteSpace(settings.Executable)
                    ? "gx-engine.exe was not found next to the Gateway, in publish/ or src/gx-engine/target/{release,debug}, or on PATH."
                    : $"Engine.Executable/GXMCP_ENGINE_EXE points at '{settings.Executable}', which does not exist.";
                return GxEngineProbe.Unavailable(null, "EngineNotFound", detail);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");

            using var proc = new Process { StartInfo = startInfo };
            try
            {
                if (!proc.Start())
                    return GxEngineProbe.Unavailable(executable, "EngineStartFailed", "the process did not start.");
            }
            catch (Exception ex)
            {
                return GxEngineProbe.Unavailable(executable, "EngineStartFailed", ex.Message);
            }

            GxEngineProcessRun run = await GxEngineProcessRunner.RunAsync(proc, ProbeTimeoutSeconds).ConfigureAwait(false);
            if (run.TimedOut)
                return GxEngineProbe.Unavailable(executable, "EngineTimeout", $"no version line within {ProbeTimeoutSeconds}s.");
            if (run.ExitCode != 0)
                return GxEngineProbe.Unavailable(executable, "EngineProbeFailed", $"--version exited with {run.ExitCode}: {Excerpt(run.StandardError)}");
            if (!TryParseVersionOutput(run.StandardOutput, out string version))
                return GxEngineProbe.Unavailable(executable, "EngineVersionUnrecognized", $"--version output was not recognized: {Excerpt(run.StandardOutput)}");

            return GxEngineProbe.Ok(executable, version);
        }

        private static string Excerpt(string text)
        {
            string single = EngineFailureText.Redact(text).Replace("\r", " ").Replace("\n", " ").Trim();
            return EngineFailureText.Truncate(single, 240);
        }

        /// <summary>
        /// Runs one capability invocation to completion under the resolved
        /// Engine.TimeoutSeconds budget. A cancellation request kills the child so
        /// an abandoned MCP request cannot leave a compiler running; the caller
        /// sees the resulting failure, never a half-finished success.
        /// </summary>
        internal static async Task<GxEngineInvocation> InvokeAsync(
            EngineBackendSettings settings,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));

            string? executable = GxEngineLocator.Locate(settings);
            if (executable == null)
            {
                string detail = string.IsNullOrWhiteSpace(settings.Executable)
                    ? "gx-engine.exe was not found next to the Gateway, in publish/ or src/gx-engine/target/{release,debug}, or on PATH."
                    : $"Engine.Executable/GXMCP_ENGINE_EXE points at '{settings.Executable}', which does not exist.";
                return GxEngineInvocation.Failure(null, "EngineNotFound", detail);
            }

            // Opt-in resident process (GXMCP_ENGINE_RESIDENT); null while it is off, which is the default.
            GxEngineResidentPool? residentPool = GxEngineResidentPool.Current;
            if (residentPool != null)
            {
                GxEngineResidentOutcome resident = await residentPool.TryRunAsync(
                    executable, arguments, settings.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
                // A build whose end was not observed: an unknown outcome, like a failed observation
                // of a one-shot process, so it is never run again below.
                if (resident.UnobservedMessage != null)
                    return GxEngineInvocation.Failure(executable, "EngineObservationFailed", resident.UnobservedMessage);
                if (resident.Run is GxEngineProcessRun served)
                    return ToInvocation(executable, served, cancellationToken);
                // Not served and nothing started: the one-shot process below runs the call.
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var proc = new Process { StartInfo = startInfo };
            try
            {
                if (!proc.Start())
                    return GxEngineInvocation.Failure(executable, "EngineStartFailed", "the process did not start.");
            }
            catch (Exception ex)
            {
                return GxEngineInvocation.Failure(executable, "EngineStartFailed", ex.Message);
            }

            GxEngineProcessRun run;
            try
            {
                using var registration = cancellationToken.CanBeCanceled
                    ? cancellationToken.Register(() => GxEngineProcessRunner.TryKillProcessTree(proc))
                    : default(CancellationTokenRegistration);
                run = await GxEngineProcessRunner.RunAsync(proc, settings.TimeoutSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The child already started: a pipe/wait failure is an unknown
                // execution outcome, never proof that SDK replay is safe.
                bool stopped = await GxEngineProcessRunner.StopAndWaitAsync(proc).ConfigureAwait(false);
                return GxEngineInvocation.Failure(
                    executable, "EngineObservationFailed",
                    $"gx-engine started, but observing its completion failed: {ex.Message} "
                    + (stopped ? "Process exit observed after cleanup." : "Process termination could not be confirmed; reconcile before another build."));
            }
            return ToInvocation(executable, run, cancellationToken);
        }

        /// <summary>
        /// The invocation outcome of one finished run. One-shot and resident runs both pass
        /// through here, so the same output and exit code always give the same outcome.
        /// </summary>
        private static GxEngineInvocation ToInvocation(string executable, GxEngineProcessRun run, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return GxEngineInvocation.Failure(
                    executable, "EngineCancelled",
                    "the request was cancelled before gx-engine finished.",
                    stdout: run.StandardOutput, stderr: run.StandardError, timedOut: run.TimedOut);
            }
            if (run.TimedOut)
            {
                return GxEngineInvocation.Failure(
                    executable, "EngineTimeout",
                    $"gx-engine exceeded the {run.TimeoutSeconds}s budget (Engine.TimeoutSeconds).",
                    stdout: run.StandardOutput, stderr: run.StandardError, timedOut: true);
            }
            if (run.ExitCode != 0)
            {
                return GxEngineInvocation.Failure(
                    executable, "EngineCommandFailed",
                    $"gx-engine exited with {run.ExitCode}: {Excerpt(run.StandardError)}",
                    stdout: run.StandardOutput, stderr: run.StandardError, exitCode: run.ExitCode);
            }

            return GxEngineInvocation.Success(executable, run.StandardOutput);
        }
    }
}
