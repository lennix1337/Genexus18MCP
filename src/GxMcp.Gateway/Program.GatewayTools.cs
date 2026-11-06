using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace GxMcp.Gateway
{
    partial class Program
    {

        private static readonly HashSet<string> KbDriverProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "native-sdk",
            "dotnet-reflection",
            "com-gxpublic"
        };

        private static void ValidateKbOpenOverrides(string? driver, string? installationPath, string? major)
        {
            if (!string.IsNullOrWhiteSpace(driver) && !KbDriverProfiles.Contains(driver.Trim()))
                throw new ArgumentException($"Unsupported KB driver '{driver}'. Use native-sdk, dotnet-reflection, or com-gxpublic.");

            if (!string.IsNullOrWhiteSpace(installationPath) && !Directory.Exists(installationPath.Trim()))
                throw new ArgumentException($"KB installationPath does not exist: {installationPath}");

            if (!string.IsNullOrWhiteSpace(major) && !GeneXusVersionCatalog.IsSupportedOrLegacy(major.Trim()))
                throw new ArgumentException($"KB major '{major}' is outside the compatibility catalog.");

            string? expectedDriver = GeneXusVersionCatalog.GetDriverProfile(major);
            if (!string.IsNullOrWhiteSpace(driver)
                && !string.IsNullOrWhiteSpace(expectedDriver)
                && !string.Equals(driver.Trim(), expectedDriver, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"KB driver '{driver}' does not match catalog major '{major}' (expected '{expectedDriver}').");
            }
        }

        private static KbHandle ApplyKbOpenOverrides(KbHandle handle, JObject? args)
        {
            string? driver = args?["driver"]?.ToString();
            string? installationPath = args?["installationPath"]?.ToString();
            string? major = args?["major"]?.ToString();
            ValidateKbOpenOverrides(driver, installationPath, major);
            if (!string.IsNullOrWhiteSpace(driver)
                && !string.Equals(driver, "native-sdk", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(installationPath)
                && string.IsNullOrWhiteSpace(handle.InstallationPath))
            {
                throw new ArgumentException($"KB driver '{driver}' requires installationPath so this KB does not inherit the global GeneXus installation.");
            }
            if (string.IsNullOrWhiteSpace(driver) && string.IsNullOrWhiteSpace(installationPath) && string.IsNullOrWhiteSpace(major))
                return handle;

            return new KbHandle(
                handle.Alias,
                handle.Path,
                installationPath ?? handle.InstallationPath,
                driver ?? handle.Driver,
                major ?? handle.Major);
        }

        /// <summary>
        /// The KB_POOL_FULL tool payload. Isolated_Mode (no catalog error) keeps the original
        /// { error, code, openKbs: [aliases] } shape; shared mode returns the catalog error
        /// (reason, limit, value, per-KB openKbs details) with error set to the exception message.
        /// </summary>
        internal static JObject BuildKbPoolFullPayload(WorkerPoolFullException ex)
        {
            if (ex == null) throw new ArgumentNullException(nameof(ex));
            if (ex.Error == null)
            {
                return new JObject
                {
                    ["error"] = ex.Message,
                    ["code"] = "KB_POOL_FULL",
                    ["openKbs"] = JArray.FromObject(ex.OpenKbs.Select(k => k.Alias))
                };
            }

            var payload = (JObject)ex.Error.DeepClone();
            payload["error"] = ex.Message;
            return payload;
        }

        internal static bool UpsertKbCatalogEntry(JObject environment, string alias, string path)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("KB alias is required.", nameof(alias));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("KB path is required.", nameof(path));

            if (environment["KBs"] is JArray array)
            {
                bool exists = array.OfType<JObject>().Any(entry =>
                {
                    var aliasProperty = entry.Properties().FirstOrDefault(p =>
                        string.Equals(p.Name, "Alias", StringComparison.OrdinalIgnoreCase));
                    return string.Equals(aliasProperty?.Value?.ToString(), alias, StringComparison.OrdinalIgnoreCase);
                });
                if (exists) return false;

                array.Add(new JObject { ["Alias"] = alias, ["Path"] = path });
                return true;
            }

            if (environment["KBs"] is JObject map)
            {
                var existing = map.Properties().FirstOrDefault(p =>
                    string.Equals(p.Name, alias, StringComparison.OrdinalIgnoreCase));
                if (existing != null) return false;

                map[alias] = path;
                return true;
            }

            environment["KBs"] = new JArray
            {
                new JObject { ["Alias"] = alias, ["Path"] = path }
            };
            return true;
        }

        /// <summary>
        /// Gateway-served tools: telemetry ring-buffer views, the <c>genexus_kb</c>
        /// pool meta-tool, warm spares, connection recovery, and the sandbox/
        /// kb_diff/kb_import filesystem operations. Returns null when the tool is
        /// not one of these so the caller falls through to the worker dispatch.
        /// </summary>
        private static async Task<JObject?> HandleGatewayToolAsync(
            JToken? idToken, string toolName, JObject? args, string sessionId, bool sessionContextEnabled)
        {
            // genexus_telemetry: gateway-only short-circuit for ring-buffer views (executions, watch_event).
            // Other actions (logs/friction_*/learning_report/profile_*) fall through to the router.
            if (string.Equals(toolName, "genexus_telemetry", StringComparison.OrdinalIgnoreCase))
            {
                string? telAction = args?["action"]?.ToString()?.ToLowerInvariant();
                if (telAction == "executions")
                {
                    string? targetName = args?["target"]?.ToString() ?? args?["name"]?.ToString();
                    int lastN = args?["last"]?.ToObject<int?>() ?? 10;
                    JObject historyPayload = _operationTracker?.BuildExecutionHistory(targetName, lastN)
                        ?? new JObject { ["status"] = "Unwired", ["code"] = "TrackerUnavailable", ["runs"] = new JArray() };
                    return BuildToolTextResponse(idToken, historyPayload, isError: false, toolName: "genexus_telemetry", toolArgs: args, payloadOwned: true);
                }
                if (telAction == "watch_event")
                {
                    string? watchTarget = args?["target"]?.ToString() ?? args?["name"]?.ToString();
                    string? watchEvent =args?["event"]?.ToString();
                    int watchLast = args?["last"]?.ToObject<int?>() ?? 10;
                    JObject watchPayload = _operationTracker?.BuildWatchEvent(watchTarget, watchEvent, watchLast)
                        ?? new JObject { ["status"] = "Unwired", ["code"] = "TrackerUnavailable", ["runs"] = new JArray() };
                    return BuildToolTextResponse(idToken, watchPayload, isError: false, toolName: "genexus_telemetry", toolArgs: args, payloadOwned: true);
                }
                // Any other action falls through to OperationsRouter ConvertTelemetryUmbrella.
            }

            // genexus_kb — meta-tool for managing the WorkerPool (list/open/close).
            // Handled entirely in the Gateway; never reaches a Worker. The
            // SDK-bound actions fall through to the router pipeline
            // (SystemRouter forwards them to the worker's KB module).
            if (string.Equals(toolName, OperationClassifier.KbTool, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(args?["action"]?.ToString(), "set_startup", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(args?["action"]?.ToString(), "get_startup", StringComparison.OrdinalIgnoreCase)
                && !OperationClassifier.IsKbEnvironmentAction(toolName, args?["action"]?.ToString()))
            {
                JObject payload;
                bool isError = false;
                try
                {
                    if (_workerPool == null)
                    {
                        throw new InvalidOperationException("WorkerPool not initialised.");
                    }

                    string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "list";
                    switch (action)
                    {
                        case "list":
                            var openKbs = _workerPool.ListOpen();
                            var knownKbs = _workerPool.ListKnown();
                            string? configuredAlias = GetConfiguredDefaultKb();
                            string? selectedAlias = sessionContextEnabled
                                ? GetSessionSelectedKb(sessionId)
                                : null;
                            string selectedLeaseState = !string.IsNullOrWhiteSpace(selectedAlias) && sessionContextEnabled
                                ? GetSessionLeaseState(sessionId)
                                : "none";
                            bool selectedLeaseActive = string.Equals(selectedLeaseState, "active", StringComparison.Ordinal);
                            var activeHandle = !string.IsNullOrWhiteSpace(selectedAlias)
                                ? openKbs.FirstOrDefault(k =>
                                    string.Equals(k.Alias, selectedAlias, StringComparison.OrdinalIgnoreCase))
                                : openKbs.FirstOrDefault(k =>
                                    string.Equals(k.Alias, configuredAlias, StringComparison.OrdinalIgnoreCase))
                                    ?? (openKbs.Count == 1 ? openKbs[0] : null);
                            payload = new JObject
                            {
                                ["openKbs"] = JArray.FromObject(_workerPool.Snapshot()
                                    .Select(s => new
                                    {
                                        alias = s.Handle.Alias,
                                        path = s.Handle.Path,
                                        pid = s.Pid,
                                        workingSetBytes = s.WorkingSetBytes,
                                        workingSetMB = s.WorkingSetBytes.HasValue
                                            ? Math.Round(s.WorkingSetBytes.Value / (1024.0 * 1024.0), 1)
                                            : (double?)null,
                                        lastActivityUtc = s.LastActivityUtc,
                                        idleSeconds = (int)Math.Max(0, (DateTime.UtcNow - s.LastActivityUtc).TotalSeconds)
                                    })),
                                ["knownKbs"] = JArray.FromObject(knownKbs
                                    .Select(k => new
                                    {
                                        alias = k.Alias,
                                        path = k.Path,
                                        open = _workerPool.TryGetWorker(k.NormalizedAlias) != null
                                    })),
                                ["activeKb"] = selectedAlias ?? activeHandle?.Alias ?? configuredAlias,
                                ["selectedKb"] = selectedAlias,
                                ["leaseState"] = selectedLeaseState,
                                ["leaseActive"] = selectedLeaseActive,
                                ["contextRequired"] = !string.IsNullOrWhiteSpace(selectedAlias) && !selectedLeaseActive,
                                ["maxOpenKbs"] = _activeConfig?.Server?.MaxOpenKbs ?? 3,
                                ["defaultKb"] = configuredAlias,
                                ["declaredKbs"] = JArray.FromObject(
                                    (_activeConfig?.Environment?.KBs ?? new List<KbEntry>())
                                        .Select(k => new { alias = k.Alias, path = k.Path, driver = k.Driver, installationPath = k.InstallationPath, major = k.Major }))
                            };
                            // Terse mode: the list action is a health snapshot; strip
                            // process telemetry (workingSet/pid/idle) and redundant
                            // alias lists when every catalog says the same thing.
                            if (TerseResponsesEnabledCached() && payload["openKbs"] is JArray openArr)
                            {
                                foreach (var item in openArr.OfType<JObject>())
                                {
                                    item.Remove("workingSetBytes");
                                    item.Remove("workingSetMB");
                                    item.Remove("pid");
                                    item.Remove("lastActivityUtc");
                                    item.Remove("idleSeconds");
                                }
                                var known = payload["knownKbs"] as JArray;
                                var declared = payload["declaredKbs"] as JArray;
                                var declaredAliases = declared?.Select(d => d["alias"]?.ToString()).ToList();
                                bool knownMatches = known != null && declaredAliases != null
                                    && known.Count == declaredAliases.Count
                                    && known.Select(k => k["alias"]?.ToString()).OrderBy(x => x)
                                        .SequenceEqual(declaredAliases.OrderBy(x => x));
                                if (knownMatches) payload.Remove("knownKbs");
                                if (openArr.Count == 0) payload.Remove("openKbs");
                            }
                            break;
                        case "select":
                        case "set_session_default":
                        {
                            if (!sessionContextEnabled)
                            {
                                throw new KbResolutionException("KB_SESSION_UNAVAILABLE",
                                    "Session selection is not available for sessionless HTTP transport without a client identifier. Pass 'kb' explicitly or configure a client identifier header.");
                            }

                            string? alias = args?["alias"]?.ToString() ?? args?["kb"]?.ToString();
                            string? path = args?["path"]?.ToString();
                            if (string.IsNullOrWhiteSpace(alias) && string.IsNullOrWhiteSpace(path))
                                throw new ArgumentException($"The 'alias' (or 'kb') or 'path' parameter is required for '{action}'.");

                            string resolvedAlias;
                            string? resolvedPath = null;
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                if (!Configuration.IsPlausibleKbPath(path!))
                                    throw new ArgumentException($"Path '{path}' does not look like a GeneXus Knowledge Base.");
                                resolvedAlias = string.IsNullOrWhiteSpace(alias)
                                    ? System.IO.Path.GetFileName(path!.TrimEnd('\\', '/')).ToLowerInvariant()
                                    : alias!;
                                resolvedPath = path;
                                _workerPool.RegisterKnown(new KbHandle(resolvedAlias, resolvedPath));
                            }
                            else
                            {
                                var declared = _activeConfig?.Environment?.KBs?.FirstOrDefault(
                                    k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                if (declared != null)
                                {
                                    resolvedAlias = declared.Alias;
                                    resolvedPath = declared.Path;
                                }
                                else
                                {
                                    var known = _workerPool.ListKnown().FirstOrDefault(
                                        k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                    if (known != null)
                                    {
                                        resolvedAlias = known.Alias;
                                        resolvedPath = known.Path;
                                    }
                                    else if (Configuration.IsPlausibleKbPath(alias!))
                                    {
                                        resolvedAlias = System.IO.Path.GetFileName(alias!.TrimEnd('\\', '/')).ToLowerInvariant();
                                        resolvedPath = alias;
                                        _workerPool.RegisterKnown(new KbHandle(resolvedAlias, resolvedPath));
                                    }
                                    else
                                    {
                                        throw new KbResolutionException("KB_NOT_FOUND",
                                            $"Alias '{alias}' is neither declared in config.Environment.KBs[] nor currently open/known.");
                                    }
                                }
                            }

                            SetSessionSelectedKb(sessionId, resolvedAlias, resolvedPath ?? resolvedAlias);
                            string sessionLeaseState = GetSessionLeaseState(sessionId);
                            bool sessionLeaseActive = string.Equals(sessionLeaseState, "active", StringComparison.Ordinal);

                            payload = new JObject
                            {
                                ["selectedKb"] = resolvedAlias,
                                ["path"] = resolvedPath,
                                ["scope"] = "session",
                                ["persisted"] = false,
                                ["leaseState"] = sessionLeaseState,
                                ["leaseActive"] = sessionLeaseActive
                            };
                            break;
                        }
                        case "set_persistent_default":
                        case "set_default":
                        {
                            string? alias = args?["alias"]?.ToString() ?? args?["kb"]?.ToString();
                            if (string.IsNullOrWhiteSpace(alias))
                                throw new ArgumentException($"Missing 'alias' (or 'kb') for action={action}.");

                            bool isLegacySetDefault = string.Equals(action, "set_default", StringComparison.OrdinalIgnoreCase);
                            bool persist = isLegacySetDefault ? (args?["persist"]?.ToObject<bool?>() ?? true) : true;
                            if (!persist)
                            {
                                if (!sessionContextEnabled)
                                {
                                    throw new KbResolutionException("KB_SESSION_UNAVAILABLE",
                                        "Session selection is not available for sessionless HTTP transport without a client identifier. Pass 'kb' explicitly or configure a client identifier header.");
                                }
                                string sessionAlias;
                                string? sessionPath = null;
                                var declaredSession = _activeConfig?.Environment?.KBs?.FirstOrDefault(
                                    k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                if (declaredSession != null)
                                {
                                    sessionAlias = declaredSession.Alias;
                                    sessionPath = declaredSession.Path;
                                }
                                else
                                {
                                    var knownSession = _workerPool.ListKnown().FirstOrDefault(
                                        k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                    if (knownSession != null)
                                    {
                                        sessionAlias = knownSession.Alias;
                                        sessionPath = knownSession.Path;
                                    }
                                    else if (Configuration.IsPlausibleKbPath(alias!))
                                    {
                                        sessionAlias = System.IO.Path.GetFileName(alias!.TrimEnd('\\', '/')).ToLowerInvariant();
                                        sessionPath = alias;
                                        _workerPool.RegisterKnown(new KbHandle(sessionAlias, sessionPath));
                                    }
                                    else
                                    {
                                        throw new KbResolutionException("KB_NOT_FOUND",
                                            $"Alias '{alias}' is neither declared in config.Environment.KBs[] nor currently open/known.");
                                    }
                                }

                                SetSessionSelectedKb(sessionId, sessionAlias, sessionPath ?? sessionAlias);

                                payload = new JObject
                                {
                                    ["selectedKb"] = sessionAlias,
                                    ["path"] = sessionPath,
                                    ["scope"] = "session",
                                    ["persisted"] = false
                                };
                                break;
                            }
                            if (_activeConfig == null || string.IsNullOrWhiteSpace(Configuration.CurrentConfigPath))
                                throw new InvalidOperationException("No active config to persist.");
                            var declared = _activeConfig.Environment?.KBs?.FirstOrDefault(
                                k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                            string resolvedAlias;
                            string? resolvedPath = null;
                            if (declared != null)
                            {
                                resolvedAlias = declared.Alias;
                                resolvedPath = declared.Path;
                            }
                            else
                            {
                                var known = _workerPool.ListKnown().FirstOrDefault(
                                    k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                                if (known == null)
                                    throw new KbResolutionException("KB_NOT_FOUND",
                                        $"Alias '{alias}' is neither declared in config.Environment.KBs[] nor currently open. Use 'open' with a path first, or add it to config.");
                                resolvedAlias = known.Alias;
                                resolvedPath = known.Path;
                            }
                            // Patch the JSON on disk to preserve any fields we don't model.
                            string configPath = Configuration.CurrentConfigPath!;
                            JObject root;
                            try { root = JObject.Parse(System.IO.File.ReadAllText(configPath)); }
                            catch (Exception ex) { throw new InvalidOperationException($"Failed to read config.json: {ex.Message}"); }
                            if (root["Environment"] is not JObject envObj)
                            {
                                envObj = new JObject();
                                root["Environment"] = envObj;
                            }
                            bool promoted = false;
                            if (declared == null && !string.IsNullOrWhiteSpace(resolvedPath))
                            {
                                promoted = UpsertKbCatalogEntry(envObj, resolvedAlias, resolvedPath);
                            }
                            envObj["DefaultKb"] = resolvedAlias;
                            envObj["ActiveKb"] = resolvedAlias;
                            AtomicJsonFileWriter.Write(configPath, root.ToString(Formatting.Indented));

                            // Do not publish the in-memory selection until the exact aliases
                            // written above have been read back from disk. This keeps a failed
                            // or externally replaced config from making the response lie.
                            JObject persistedRoot;
                            try { persistedRoot = JObject.Parse(System.IO.File.ReadAllText(configPath)); }
                            catch (Exception ex) { throw new InvalidOperationException($"Failed to verify persisted config.json: {ex.Message}"); }
                            var persistedEnvironment = persistedRoot["Environment"] as JObject;
                            if (!string.Equals(persistedEnvironment?["DefaultKb"]?.ToString(), resolvedAlias, StringComparison.Ordinal) ||
                                !string.Equals(persistedEnvironment?["ActiveKb"]?.ToString(), resolvedAlias, StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException($"Persisted config.json did not retain DefaultKb and ActiveKb='{resolvedAlias}'.");
                            }
                            if (promoted)
                            {
                                _activeConfig.Environment!.KBs.Add(new KbEntry { Alias = resolvedAlias, Path = resolvedPath });
                            }
                            _activeConfig.Environment!.DefaultKb = resolvedAlias;
                            _activeConfig.Environment!.ActiveKb = resolvedAlias;
                            _activeConfig.Environment!.RawDefaultKb = resolvedAlias;
                            if (sessionContextEnabled)
                                SetSessionSelectedKb(sessionId, resolvedAlias);
                            payload = new JObject
                            {
                                ["defaultKb"] = resolvedAlias,
                                ["selectedKb"] = resolvedAlias,
                                ["persistedTo"] = configPath,
                                ["promotedToDeclared"] = promoted
                            };
                            break;
                        }
                        case "open":
                        {
                            string? alias = args?["alias"]?.ToString();
                            string? path = args?["path"]?.ToString();
                            KbHandle handleToOpen;
                            // Path wins: register ad-hoc (with optional caller-supplied alias).
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                string finalAlias = string.IsNullOrWhiteSpace(alias)
                                    ? System.IO.Path.GetFileName(path!.TrimEnd('\\', '/')).ToLowerInvariant()
                                    : alias!;
                                if (string.IsNullOrEmpty(finalAlias)) finalAlias = "adhoc";

                                // issue #38 defect #1: reject a path that is not a real KB root
                                // BEFORE spawning a worker. A GeneXus environment/model subfolder
                                // (no .gxw / no knowledgebase.connection) would otherwise be handed
                                // to a freshly-spawned worker as GX_KB_PATH, whose open fails but
                                // whose auto-open keeps retrying forever, eventually wedging the
                                // gateway (every later call → "Master error: NotFound"). Fail fast
                                // here so no worker is ever spawned for an unopenable path.
                                if (!Configuration.IsPlausibleKbPath(path!))
                                {
                                    isError = true;
                                    payload = new JObject
                                    {
                                        ["error"] = $"'{path}' is not a GeneXus Knowledge Base root: no modern .gxw/knowledgebase.connection marker and no classic DAT markers (DATA001, GXSPC001, kbdata, ATTRIBUT.DAT, or ATT.XPW). "
                                            + "Point at the KB folder, not an environment/model subfolder.",
                                        ["code"] = "KbInvalidPath",
                                        ["path"] = path
                                    };
                                    return BuildToolTextResponse(idToken, payload, isError, "genexus_kb", args, payloadOwned: true);
                                }

                                var declaredForPath = _activeConfig?.Environment?.KBs?.FirstOrDefault(entry =>
                                {
                                    if (!string.Equals(entry.Alias, finalAlias, StringComparison.OrdinalIgnoreCase)) return false;
                                    try
                                    {
                                        return string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(path!), StringComparison.OrdinalIgnoreCase);
                                    }
                                    catch { return string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase); }
                                });
                                handleToOpen = declaredForPath != null
                                    ? KbHandle.FromEntry(declaredForPath)
                                    : new KbHandle(finalAlias, path!);
                            }
                            // No path → resolve the alias against config-declared KBs.
                            else if (!string.IsNullOrWhiteSpace(alias) && _activeConfig != null)
                            {
                                handleToOpen = new KbResolver(_activeConfig).Resolve(alias, _workerPool.ListOpen(), _workerPool.ListKnown());
                            }
                            else
                            {
                                throw new ArgumentException("Provide 'path' (ad-hoc) or 'alias' of a KB declared in config.Environment.KBs[].");
                            }

                            handleToOpen = ApplyKbOpenOverrides(handleToOpen, args);
                            var w = await _workerPool.AcquireAsync(handleToOpen, CancellationToken.None);
                            TriggerIndexBootstrapOnce(handleToOpen.NormalizedAlias);
                            // Opening a worker must not mutate the persisted default or
                            // another MCP session's selection. Use set_default to select
                            // this KB for the current session, or pass kb explicitly.
                            string? selectedAfterOpen = sessionContextEnabled
                                ? GetSessionSelectedKb(sessionId)
                                : null;
                            bool selected = string.Equals(
                                selectedAfterOpen,
                                handleToOpen.Alias,
                                StringComparison.OrdinalIgnoreCase);
                            string openedLeaseState = selected && sessionContextEnabled
                                ? GetSessionLeaseState(sessionId)
                                : "none";
                            bool openedLeaseActive = string.Equals(openedLeaseState, "active", StringComparison.Ordinal);
                            payload = new JObject
                            {
                                ["opened"] = handleToOpen.Alias,
                                ["path"] = handleToOpen.Path,
                                ["driver"] = handleToOpen.Driver,
                                ["installationPath"] = handleToOpen.InstallationPath,
                                ["major"] = handleToOpen.Major,
                                ["workerPid"] = w?.Pid,
                                ["selected"] = selected,
                                ["active"] = selected,
                                ["leaseState"] = openedLeaseState,
                                ["leaseActive"] = openedLeaseActive,
                                ["contextRequired"] = selected && !openedLeaseActive
                            };
                            if (!selected)
                            {
                                payload["hint"] = $"KB '{handleToOpen.Alias}' is open but not selected for this MCP session. Use genexus_kb action=set_default alias={handleToOpen.Alias} or pass kb explicitly.";
                            }
                            break;
                        }
                        case "close":
                        {
                            string? alias = args?["alias"]?.ToString() ?? args?["kb"]?.ToString();
                            if (string.IsNullOrWhiteSpace(alias))
                            {
                                throw new ArgumentException("Missing 'alias' (or 'kb') for action=close.");
                            }
                            bool closed = _workerPool.Close(alias!);
                            InvalidateIndexStateForKb(alias!);
                            ResetIndexBootstrapForAlias(alias!);
                            // The alias may be reopened against a different KB later;
                            // never let cached name/type resolutions cross that boundary.
                            InvalidateFullNameTypeMap(alias!);
                            bool selectionCleared = sessionContextEnabled
                                && closed
                                && string.Equals(GetSessionSelectedKb(sessionId), alias, StringComparison.OrdinalIgnoreCase);
                            if (selectionCleared)
                                ClearSessionSelectedKb(sessionId);
                            payload = new JObject
                            {
                                ["closed"] = closed,
                                ["alias"] = alias,
                                ["selectionCleared"] = selectionCleared,
                                ["selectedKb"] = sessionContextEnabled
                                    ? GetSessionSelectedKb(sessionId)
                                    : null
                            };
                            break;
                        }
                        case "create":
                        {
                            var options = new KbCreateHelper.CreateOptions
                            {
                                Path = args?["path"]?.ToString() ?? string.Empty,
                                Name = args?["name"]?.ToString(),
                                Alias = args?["alias"]?.ToString(),
                                DbServer = args?["dbServer"]?.ToString(),
                                DbName = args?["dbName"]?.ToString(),
                                DbUser = args?["dbUser"]?.ToString(),
                                DbPassword = args?["dbPassword"]?.ToString(),
                                Template = args?["template"]?.ToString(),
                                SdkPath = args?["sdkPath"]?.ToString(),
                                Major = args?["major"]?.ToString(),
                                OpenAfterCreate = args?["openAfterCreate"]?.Value<bool?>() ?? true,
                                Persist = args?["persist"]?.Value<bool?>() ?? false,
                                DryRun = args?["dryRun"]?.Value<bool?>() ?? false
                            };

                            payload = await KbCreateHelper.CreateKbAsync(
                                options,
                                _activeConfig,
                                sessionId,
                                sessionContextEnabled,
                                _workerPool,
                                SetSessionSelectedKb,
                                TriggerIndexBootstrapOnce);

                            if (string.Equals(payload["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
                            {
                                isError = true;
                            }
                            break;
                        }
                        default:
                            throw new ArgumentException($"Unknown action '{action}'. Use list|open|close|create.");
                    }
                }
                catch (KbResolutionException ex)
                {
                    isError = true;
                    payload = new JObject { ["error"] = ex.Message, ["code"] = ex.Code };
                }
                catch (WorkerPoolFullException ex)
                {
                    isError = true;
                    payload = BuildKbPoolFullPayload(ex);
                }
                catch (SharedWorkerLaunchException ex)
                {
                    isError = true;
                    payload = (JObject)ex.Error.DeepClone();
                    payload["error"] = ex.Message;
                }
                catch (WorkerCommandQueueFullException ex)
                {
                    // issue #369: the Gateway-side command channel stayed full. The command
                    // was never enqueued, so this is retryable - say so, with the byte and
                    // command context the caller needs to decide whether to retry.
                    isError = true;
                    payload = new JObject
                    {
                        ["error"] = ex.Message,
                        ["code"] = "WORKER_COMMAND_QUEUE_FULL",
                        ["retryable"] = true,
                        ["sentToWorker"] = false,
                        ["retryAfterMs"] = 250,
                        ["capacity"] = ex.Capacity,
                        ["waitedMs"] = ex.WaitedMs,
                        ["kbAlias"] = ex.KbAlias,
                        ["method"] = ex.Method,
                        ["requestId"] = ex.CommandId,
                        ["hint"] = "Retry once the Worker drains. A refused command was never sent to the Worker, so a retry cannot duplicate its effect."
                    };
                }
                catch (Exception ex)
                {
                    isError = true;
                    payload = new JObject { ["error"] = ex.Message };
                }

                return BuildToolTextResponse(idToken, payload, isError, "genexus_kb", args);
            }

            // Item 53: genexus_worker_pool action=warm_spares — gateway-side
            // meta-tool. Configures N pre-spawned workers bound to declared KBs
            // so the first KB-bound call doesn't pay cold-start. Capped at
            // WorkerPool.MaxWarmSpareCount; spareCount<0 disables. Never reaches
            // a worker — purely a gateway lifecycle knob.
            if (string.Equals(toolName, "genexus_worker_pool", StringComparison.OrdinalIgnoreCase))
            {
                JObject payload;
                bool isError = false;
                try
                {
                    if (_workerPool == null) throw new InvalidOperationException("WorkerPool not initialised.");
                    string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "warm_spares";
                    if (action != "warm_spares")
                        throw new ArgumentException($"Unknown action '{action}'. Use warm_spares.");
                    int spareCount = args?["spareCount"]?.ToObject<int?>() ?? args?["count"]?.ToObject<int?>() ?? 0;
                    var declared = (_activeConfig?.Environment?.KBs ?? new List<KbEntry>())
                        .Select(KbHandle.FromEntry)
                        .ToList();
                    var result = await _workerPool.ConfigureWarmSpares(spareCount, declared);
                    payload = new JObject
                    {
                        ["status"] = result.Configured == 0 ? "Disabled" : "Configured",
                        ["requested"] = result.Requested,
                        ["configured"] = result.Configured,
                        ["capped"] = result.Capped,
                        ["maxAllowed"] = WorkerPool.MaxWarmSpareCount,
                        ["prespawned"] = JArray.FromObject(result.Prespawned),
                        ["skipped"] = JArray.FromObject(result.Skipped),
                        ["declaredKbCount"] = declared.Count
                    };
                    if (result.Capped)
                        payload["warning"] = $"spareCount={result.Requested} exceeded cap {WorkerPool.MaxWarmSpareCount}; clamped to {result.Configured}.";
                    if (result.Configured > declared.Count)
                        payload["note"] = $"requested {result.Configured} spares but only {declared.Count} KBs declared in config.Environment.KBs[]; declare more aliases to use the full budget.";
                }
                catch (Exception ex)
                {
                    isError = true;
                    payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                }
                return BuildToolTextResponse(idToken, payload, isError, "genexus_worker_pool", args, payloadOwned: true);
            }

            // genexus_connection_recover — gateway-side self-healing meta-tool.
            // Diagnoses the connection/worker state and automatically applies the
            // least-invasive recovery: healthy → no-op report; worker dead/wedged →
            // kill + respawn with SDK-ready confirmation; stale cache → clear.
            // Replaces the manual scripts/mcp_recover.ps1 flow for agent-driven use.
            if (string.Equals(toolName, "genexus_connection_recover", StringComparison.OrdinalIgnoreCase))
            {
                // Journal reconciliation is gateway-only and must remain available
                // when writes are fenced or there is no live Worker/session lease.
                var journalResult = HandleMutationJournalAction(_mutationRecovery, args);
                if (journalResult != null)
                    return BuildToolTextResponse(idToken, journalResult,
                        journalResult["error"] != null, toolName, args, payloadOwned: true);
                // Certifies a complete SDK read and clears one fence; never restarts a
                // Worker, so it must not fall into the worker-recovery logic below.
                if (string.Equals(args?["action"]?.ToString(), RecoveryReadCompletion.ActionName, StringComparison.Ordinal))
                    return await HandleRecoveryReadCompleteAsync(idToken, toolName, args).ConfigureAwait(false);
                JObject payload;
                bool isError = false;
                try
                {
                    if (_workerPool == null) throw new InvalidOperationException("WorkerPool not initialised.");
                    if (_activeConfig == null) throw new InvalidOperationException("No active configuration loaded.");

                    bool force = args?["force"]?.ToObject<bool?>() == true;
                    var openKbs = _workerPool.ListOpen();
                    // Workers that died since their last call are dropped from
                    // ListOpen (entry requires Worker != null) but remain in
                    // ListKnown — include them so recover also re-opens KBs whose
                    // worker silently disappeared, not just wedged live ones.
                    var knownNotOpen = _workerPool.ListKnown()
                        .Where(k => !openKbs.Any(o => string.Equals(o.NormalizedAlias, k.NormalizedAlias, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    payload = new JObject();

                    // 1. Diagnose current state.
                    //
                    // Issue #342. This used to probe Ping, call the result
                    // "responsive", and report an unqualified "Healthy" when every
                    // Worker replied. But Ping answers on the transport/MTA path: a
                    // Worker whose SDK lane is deadlocked still replies, because the
                    // ping never reaches the thread that is stuck. A wedged SDK
                    // therefore produced exactly the same "Healthy" as a genuinely
                    // fine one, and the dimension an operator most needs was the one
                    // nothing measured.
                    //
                    // Two independent probes now feed WorkerLivenessClassifier, and the
                    // verdict is qualified with both. A busy-but-progressing SDK lane is
                    // reported as such and deliberately NOT treated as unhealthy:
                    // recycling a Worker for a legitimate long build costs the in-flight
                    // work, which is a worse failure than waiting.
                    var diagnoses = new JArray();
                    foreach (var kb in openKbs)
                    {
                        bool transportAlive;
                        try
                        {
                            var whoamiProbe = await SendWorkerCommandAsync(
                                new JObject { ["module"] = "Ping", ["action"] = "Ping", ["client"] = "mcp" },
                                5000, "recover-ping timeout",
                                wr => wr,
                                (_, cid) => new JObject { ["__timeout"] = true },
                                toolName: "connection_recover_probe",
                                trackOperation: false);
                            transportAlive = whoamiProbe?["__timeout"]?.ToObject<bool>() != true;
                        }
                        catch { transportAlive = false; }

                        JObject? sdkProbe = null;
                        try
                        {
                            sdkProbe = await SendWorkerCommandAsync(
                                new JObject { ["module"] = "System", ["action"] = "GetSdkBusyStatus" },
                                5000, "recover-sdk-lane timeout",
                                wr => wr,
                                (_, cid) => new JObject { ["__timeout"] = true },
                                toolName: "connection_recover_sdk_probe",
                                trackOperation: false);
                        }
                        catch { sdkProbe = null; }

                        diagnoses.Add(WorkerLivenessClassifier.Classify(kb.Alias, transportAlive, sdkProbe));
                    }
                    payload["diagnosis"] = diagnoses;

                    // Only a dead transport, or an SDK lane that has stopped making
                    // progress, justifies recycling a Worker.
                    var unresponsive = diagnoses.Where(d => WorkerLivenessClassifier.Recovers(d)).ToList();
                    bool anyUnhealthy = force || unresponsive.Count > 0 || knownNotOpen.Count > 0;

                    // 2. Recover only what's broken (or everything when force=true).
                    if (!anyUnhealthy)
                    {
                        // Issue #342: qualify the verdict. The unqualified "Healthy" was
                        // reachable from a responsive ping alone, which is a claim about
                        // the SDK that nothing had checked.
                        bool allIdle = diagnoses.All(d =>
                            string.Equals(d["sdk"]?.ToString(), "idle", StringComparison.OrdinalIgnoreCase));
                        payload["status"] = allIdle ? "Healthy" : "HealthySdkBusy";
                        payload["action"] = "none";
                        payload["transport"] = "alive";
                        payload["sdk"] = allIdle ? "idle" : "busy";
                        payload["detail"] = allIdle
                            ? "All workers responsive on both the transport and the SDK lane; no recovery needed."
                            : "Transport alive on every worker, but at least one SDK lane is busy. A busy lane is not a fault and was not recycled.";
                    }
                    else
                    {
                        var targetsToRestore = openKbs
                            .Where(k => force || unresponsive.Any(d =>
                                string.Equals(d["alias"]?.ToString(), k.Alias, StringComparison.OrdinalIgnoreCase)))
                            .Concat(force ? knownNotOpen : knownNotOpen.Where(k => openKbs.Count == 0))
                            .ToList();

                        using (SuppressEagerRespawn())
                        {
                            _workerPool.StopAll(WorkerStopReason.Wedged);
                        }
                        _semanticCache.InvalidateScope(string.Empty);
                        System.Threading.Interlocked.Increment(ref SemanticCacheEpoch);                            (JArray restored, JArray failed) = await RestoreWorkersAsync(
                                _workerPool, targetsToRestore, "genexus_connection_recover").ConfigureAwait(false);

                        payload["status"] = failed.Count == 0 ? "Recovered" : "PartialRecovery";
                        payload["action"] = $"killed and respawned {(force ? "all" : "unresponsive")} workers; semantic cache cleared";
                        payload["restoredWorkers"] = restored;
                        payload["failedWorkers"] = failed;
                        isError = failed.Count > 0;
                    }

                    BroadcastToolsListChanged("connection_recover");
                }
                catch (Exception ex)
                {
                    isError = true;
                    payload = new JObject { ["error"] = ex.Message, ["code"] = "RecoveryFailed" };
                }
                return BuildToolTextResponse(idToken, payload, isError, "genexus_connection_recover", args, payloadOwned: true);
            }

            // Item 54: genexus_sandbox — gateway-side filesystem clone of a KB.
            // No SDK touch; pure file copy under <configRoot>/sandboxes/<name>/.
            // remove is idempotent. create on an existing target returns
            // {status:"AlreadyExists"} unless overwrite=true.
            if (string.Equals(toolName, "genexus_sandbox", StringComparison.OrdinalIgnoreCase))
            {
                JObject payload;
                bool isError = false;
                try
                {
                    string action = args?["action"]?.ToString()?.ToLowerInvariant() ?? "create";
                    string? name = args?["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Missing 'name'.");
                    // Sanitize: alphanumeric + dash/underscore only.
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
                        throw new ArgumentException("name must match [A-Za-z0-9_-]+ (no spaces/path-separators).");

                    string configDir = !string.IsNullOrEmpty(Configuration.CurrentConfigPath)
                        ? System.IO.Path.GetDirectoryName(Configuration.CurrentConfigPath!)!
                        : AppContext.BaseDirectory;
                    string sandboxRoot = System.IO.Path.Combine(configDir, "sandboxes");
                    string targetPath = System.IO.Path.Combine(sandboxRoot, name);

                    if (action == "remove")
                    {
                        bool existed = System.IO.Directory.Exists(targetPath);
                        if (existed)
                        {
                            try { System.IO.Directory.Delete(targetPath, true); }
                            catch (Exception ex) { throw new InvalidOperationException($"Failed to remove sandbox: {ex.Message}"); }
                        }
                        payload = new JObject
                        {
                            ["status"] = existed ? "Removed" : "NotFound",
                            ["name"] = name,
                            ["path"] = targetPath
                        };
                    }
                    else if (action == "create")
                    {
                        string? from = args?["from"]?.ToString();
                        if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("Missing 'from' (source KB alias or path).");
                        bool overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false;

                        // Resolve `from` as alias against config, else treat as path.
                        string? sourcePath = null;
                        var fromKb = _activeConfig?.Environment?.KBs?.FirstOrDefault(
                            k => string.Equals(k.Alias, from, StringComparison.OrdinalIgnoreCase));
                        if (fromKb != null) sourcePath = fromKb.Path;
                        else if (System.IO.Directory.Exists(from)) sourcePath = from;
                        else throw new ArgumentException($"'from'='{from}' is not a declared KB alias and not an existing directory.");

                        if (System.IO.Directory.Exists(targetPath))
                        {
                            if (!overwrite)
                            {
                                payload = new JObject
                                {
                                    ["status"] = "AlreadyExists",
                                    ["name"] = name,
                                    ["path"] = targetPath,
                                    ["hint"] = "Pass overwrite=true to replace, or action=remove first."
                                };
                                return BuildToolTextResponse(idToken, payload, false, "genexus_sandbox", args, payloadOwned: true);
                            }
                            try { System.IO.Directory.Delete(targetPath, true); }
                            catch (Exception ex) { throw new InvalidOperationException($"Failed to remove existing sandbox before overwrite: {ex.Message}"); }
                        }

                        System.IO.Directory.CreateDirectory(sandboxRoot);
                        var copy = SandboxCopyHelper.CopyDirectory(sourcePath, targetPath);
                        payload = new JObject
                        {
                            ["status"] = "Created",
                            ["name"] = name,
                            ["path"] = targetPath,
                            ["from"] = sourcePath,
                            ["filesCopied"] = copy.Files,
                            ["bytesCopied"] = copy.Bytes,
                            ["durationMs"] = copy.DurationMs,
                            ["alias"] = "sandbox-" + name,
                            ["hint"] = $"Open with: genexus_kb action=open path=\"{targetPath}\" alias=sandbox-{name}"
                        };
                    }
                    else
                    {
                        throw new ArgumentException($"Unknown action '{action}'. Use create|remove.");
                    }
                }
                catch (Exception ex)
                {
                    isError = true;
                    payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                }
                return BuildToolTextResponse(idToken, payload, isError, "genexus_sandbox", args, payloadOwned: true);
            }

            // Item 55: genexus_kb_diff — gateway-side object-index diff between two
            // KB directories. No SDK touch. Walks each KB's filesystem Objects/<Type>/<Name>/
            // tree (and .gx/index-snapshot.bin if present) and returns
            // {onlyInA, onlyInB, modified[]}.
            if (string.Equals(toolName, "genexus_kb_diff", StringComparison.OrdinalIgnoreCase))
            {
                JObject payload;
                bool isError = false;
                try
                {
                    string? kbA = args?["kbA"]?.ToString();
                    string? kbB =args?["kbB"]?.ToString();
                    if (string.IsNullOrWhiteSpace(kbA) || string.IsNullOrWhiteSpace(kbB))
                        throw new ArgumentException("Both 'kbA' and 'kbB' are required (alias or path).");
                    string pathA = ResolveKbPath(kbA) ?? throw new ArgumentException($"'kbA'='{kbA}' not a declared alias and not an existing directory.");
                    string pathB = ResolveKbPath(kbB) ?? throw new ArgumentException($"'kbB'='{kbB}' not a declared alias and not an existing directory.");
                    if (string.Equals(System.IO.Path.GetFullPath(pathA), System.IO.Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("kbA and kbB resolve to the same path.");
                    string mode = (args?["mode"]?.ToString() ?? KbDiffHelper.ContentMode).Trim();
                    if (!string.Equals(mode, KbDiffHelper.ContentMode, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(mode, KbDiffHelper.MetadataMode, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"Unknown 'mode'='{mode}'. Use content|metadata.");
                    int maxObjects = args?["maxObjects"]?.ToObject<int?>() ?? KbDiffHelper.DefaultMaxObjects;
                    if (maxObjects <= 0)
                        throw new ArgumentException("'maxObjects' must be greater than 0 when supplied.");
                    payload = KbDiffHelper.Diff(pathA, pathB, mode, maxObjects);
                }
                catch (Exception ex)
                {
                    isError = true;
                    payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                }
                return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_diff", args, payloadOwned: true);
            }

            // Item 56: genexus_kb_import — limited filesystem-level copy of an
            // object's files between two KBs. Full SDK-level import would require
            // opening a second KB inside the worker (one SDK can only host one
            // KB at a time), so this ships as a directory-copy + index-rescan
            // recommendation. Callers should run genexus_lifecycle action=index
            // afterwards.
            if (string.Equals(toolName, "genexus_kb_import", StringComparison.OrdinalIgnoreCase))
            {
                JObject payload;
                bool isError = false;
                try
                {
                    string? from = args?["from"]?.ToString();
                    string? name = args?["name"]?.ToString();
                    string? type = args?["type"]?.ToString();
                    if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("Missing 'from' (source KB alias or path).");
                    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Missing 'name' (object name to import).");
                    if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Missing 'type' (object type, e.g. WebPanel, Procedure).");
                    string sourcePath = ResolveKbPath(from) ?? throw new ArgumentException($"'from'='{from}' not a declared alias and not an existing directory.");
                    // Target KB resolution. Prefer an explicit 'to' (alias or path) so the
                    // import isn't silently pointed at whichever KB happens to be first in
                    // the open-worker list; fall back to first-open / DefaultKb for
                    // back-compat when 'to' is omitted.
                    string? to = args?["to"]?.ToString();
                    string? targetPath = null;
                    if (!string.IsNullOrWhiteSpace(to))
                    {
                        targetPath = ResolveKbPath(to) ?? throw new ArgumentException($"'to'='{to}' not a declared alias and not an existing directory.");
                    }
                    else
                    {
                        var openKbs = _workerPool?.ListOpen();
                        if (openKbs != null && openKbs.Count > 0) targetPath = openKbs[0].Path;
                        else if (!string.IsNullOrEmpty(_activeConfig?.Environment?.DefaultKb))
                        {
                            var d = _activeConfig.Environment.KBs?.FirstOrDefault(k =>
                                string.Equals(k.Alias, _activeConfig.Environment.DefaultKb, StringComparison.OrdinalIgnoreCase));
                            targetPath = d?.Path;
                        }
                    }
                    if (string.IsNullOrEmpty(targetPath))
                    {
                        payload = new JObject
                        {
                            ["status"] = "MissingFeature",
                            ["code"] = "NoActiveKb",
                            ["error"] = "kb_import requires an open active KB. Open one via genexus_kb action=open."
                        };
                        isError = true;
                        return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_import", args, payloadOwned: true);
                    }
                    if (string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("source and target KB resolve to the same path.");
                    payload = KbImportHelper.ImportObject(sourcePath, targetPath, name, type);
                }
                catch (Exception ex)
                {
                    isError = true;
                    payload = new JObject { ["error"] = ex.Message, ["code"] = "BadRequest" };
                }
                return BuildToolTextResponse(idToken, payload, isError, "genexus_kb_import", args, payloadOwned: true);
                }
            return null;
        }
    }
}
