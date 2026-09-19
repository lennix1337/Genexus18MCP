using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace GxMcp.Gateway
{
    /// <summary>Discovers or elects the one broker for a physical KB identity.</summary>
    internal static class SharedWorkerHostLauncher
    {
        internal static SharedWorkerConnection Connect(
            Configuration config,
            KbHandle kb,
            string workerPath,
            string installationPath,
            string driver,
            string major,
            string? legacyProvider = null,
            int timeoutMs = 30000)
        {
            var identity = SharedWorkerIdentity.Create(workerPath, kb.Path, installationPath, driver, major);
            SharedWorkerRecord record = WaitForLiveRecord(identity, timeoutMs, config, kb, workerPath, installationPath, driver, major, legacyProvider);
            var connection = new SharedWorkerConnection(identity, record, "gateway-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
            connection.Connect(Math.Min(timeoutMs, 30000));
            return connection;
        }

        private static SharedWorkerRecord WaitForLiveRecord(
            SharedWorkerIdentity identity,
            int timeoutMs,
            Configuration config,
            KbHandle kb,
            string workerPath,
            string installationPath,
            string driver,
            string major,
            string? legacyProvider)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
            bool launched = false;
            while (DateTime.UtcNow < deadline)
            {
                SharedWorkerRecord? record = SharedWorkerRegistry.ReadRecord(SharedWorkerRegistry.GetRecordPath(identity));
                if (SharedWorkerRegistry.IsRecordLive(record, identity)) return record!;

                if (!launched)
                {
                    using (var reservation = SharedWorkerRegistry.TryReserve(identity, TimeSpan.FromMilliseconds(250), out record))
                    {
                        if (reservation != null)
                        {
                            StartHost(config, kb, identity, workerPath, installationPath, driver, major, legacyProvider);
                            launched = true;
                            // Keep the election mutex until the host has published a live
                            // record. Releasing it immediately lets a cold SDK start race
                            // launch several brokers for the same identity.
                            while (DateTime.UtcNow < deadline)
                            {
                                record = SharedWorkerRegistry.ReadRecord(SharedWorkerRegistry.GetRecordPath(identity));
                                if (SharedWorkerRegistry.IsRecordLive(record, identity)) return record!;
                                Thread.Sleep(100);
                            }
                        }
                    }
                }

                Thread.Sleep(100);
                record = SharedWorkerRegistry.ReadRecord(SharedWorkerRegistry.GetRecordPath(identity));
                if (SharedWorkerRegistry.IsRecordLive(record, identity)) return record!;
                if (!launched && record != null && record.Identity?.Key != identity.Key)
                    throw new InvalidOperationException("Shared Worker registry identity mismatch; refusing to attach.");
            }

            throw new TimeoutException(
                "Timed out waiting for shared Worker host for KB '" + kb.Alias + "' (identity " + identity.Key + ").");
        }

        private static void StartHost(
            Configuration config,
            KbHandle kb,
            SharedWorkerIdentity identity,
            string workerPath,
            string installationPath,
            string driver,
            string major,
            string? legacyProvider)
        {
            int idleMs = ResolveIdleTimeoutMs(config);
            string pipeName = SharedWorkerRegistry.PipeName(identity);
            var info = new ProcessStartInfo
            {
                FileName = workerPath,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                Arguments = "--shared-host"
                    + " --identity-key " + Quote(identity.Key)
                    + " --pipe-name " + Quote(pipeName)
                    + " --worker-executable " + Quote(workerPath)
                    + " --kb " + Quote(kb.Path)
                    + " --installation " + Quote(installationPath)
                    + " --driver " + Quote(driver)
                    + " --major " + Quote(major)
                    + " --idle-ms " + idleMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            info.EnvironmentVariables["GX_PROGRAM_DIR"] = installationPath ?? string.Empty;
            info.EnvironmentVariables["GX_KB_PATH"] = kb.Path ?? string.Empty;
            info.EnvironmentVariables["GXMCP_DRIVER"] = driver ?? string.Empty;
            info.EnvironmentVariables["GXMCP_TARGET_MAJOR"] = major ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(Configuration.CurrentConfigPath))
                info.EnvironmentVariables["GXMCP_PROFILE_CONFIG_PATH"] = Configuration.CurrentConfigPath;
            if (!string.IsNullOrWhiteSpace(legacyProvider))
                info.EnvironmentVariables["GXMCP_GXPUBLIC_PROVIDER"] = legacyProvider;
            info.EnvironmentVariables["GXMCP_SHARED_HOST"] = "1";
            try
            {
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    string key = entry.Key?.ToString() ?? string.Empty;
                    if (key.StartsWith("GXMCP_", StringComparison.OrdinalIgnoreCase) && !info.EnvironmentVariables.ContainsKey(key))
                        info.EnvironmentVariables[key] = entry.Value?.ToString() ?? string.Empty;
                }
            }
            catch { }

            Process? host = Process.Start(info);
            if (host == null) throw new InvalidOperationException("Could not start shared Worker host process.");
            // Keep stdout/stderr drained without making host startup depend on a Gateway
            // reader. Diagnostics remain in the normal gateway log only when startup fails.
            _ = host.StandardOutput.ReadToEndAsync();
            _ = host.StandardError.ReadToEndAsync();
            Program.Log("[Gateway] shared_worker_host_started pid=" + host.Id + " identity=" + identity.Key);
        }

        private static int ResolveIdleTimeoutMs(Configuration config)
        {
            int minutes = config.Server?.WorkerIdleTimeoutMinutes ?? 60;
            if (minutes <= 0) return 60 * 60 * 1000;
            return Math.Min(minutes * 60 * 1000, 24 * 60 * 60 * 1000);
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
