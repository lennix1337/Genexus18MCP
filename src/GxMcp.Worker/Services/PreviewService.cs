using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// W4 — Render preview. Drives <c>chrome-devtools-axi</c> CLI to open a built
    /// WebPanel via a launcher page, fill required parms, capture HTML/a11y/screenshot
    /// and optionally diff against a stored baseline.
    /// All shell-out behaviour is funneled through <see cref="ICliRunner"/> so tests
    /// can mock the CLI without touching a real browser.
    /// </summary>
    public class PreviewService
    {
        /// <summary>Abstracts running an external command. Tests inject a fake.</summary>
        public interface ICliRunner
        {
            CliResult Run(string fileName, string arguments, int timeoutMs);
            /// <summary>Returns CLI absolute path if found (via 'where' or shell PATH), else null.</summary>
            string Which(string command);
        }

        public class CliResult
        {
            public int ExitCode;
            public string StdOut;
            public string StdErr;
            public bool TimedOut;
        }

        internal sealed class AxiCliResolution
        {
            public string ResolvedPath;
            public string Version;
            public string Source;
            public string ConfigFile;
            public string ProfileConfigFile;
            public string AxiCli;
            public string ProfileAxiCli;
            public string PathUsed;
            public readonly List<string> SearchedPaths = new List<string>();

            public bool Resolved => !string.IsNullOrWhiteSpace(ResolvedPath);

            public JObject ToJObject()
            {
                var result = new JObject
                {
                    ["resolved"] = Resolved,
                    ["source"] = Source,
                    ["configFile"] = ConfigFile,
                    ["profileConfigFile"] = ProfileConfigFile,
                    ["axiCli"] = AxiCli,
                    ["profileAxiCli"] = ProfileAxiCli,
                    ["pathUsed"] = PathUsed,
                    ["searchedPaths"] = new JArray(SearchedPaths),
                    ["installHint"] = "Configure axiCli with an absolute path or install chrome-devtools-axi in the MCP runtime (npm install chrome-devtools-axi), then restart the Worker."
                };
                result["path"] = ResolvedPath;
                result["version"] = Version;
                return result;
            }
        }

        public class DefaultCliRunner : ICliRunner
        {
            public CliResult Run(string fileName, string arguments, int timeoutMs)
            {
                ProcessStartInfo psi;
                var ext = Path.GetExtension(fileName);
                bool isNativeExe = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".com", StringComparison.OrdinalIgnoreCase);
                if (!isNativeExe)
                {
                    // Shims are the only interpreter path. Re-tokenize the logical
                    // arguments and escape them for cmd.exe; never append raw request
                    // data to /c.
                    psi = new ProcessStartInfo("cmd.exe", BrowserDriverProcess.BuildShimArguments(
                        Path.GetFullPath(fileName), DefaultBrowserDriverInvoker.ParseLegacyArguments(arguments)));
                }
                else
                {
                    psi = new ProcessStartInfo(Path.GetFullPath(fileName),
                        BrowserDriverProcess.BuildArguments(DefaultBrowserDriverInvoker.ParseLegacyArguments(arguments)));
                }
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                // Skip the ~30s `npx -y chrome-devtools-mcp@latest` cold-start when a
                // globally installed copy of chrome-devtools-mcp is available. The CLI
                // honours CHROME_DEVTOOLS_AXI_MCP_PATH and spawns `node <path>` directly.
                if (string.IsNullOrEmpty(psi.EnvironmentVariables["CHROME_DEVTOOLS_AXI_MCP_PATH"]))
                {
                    var mcpPath = ResolveChromeDevtoolsMcpPath();
                    if (!string.IsNullOrEmpty(mcpPath))
                    {
                        psi.EnvironmentVariables["CHROME_DEVTOOLS_AXI_MCP_PATH"] = mcpPath;
                    }
                }

                using (var p = Process.Start(psi))
                {
                    // Friction 2026-05-22: prior version called ReadToEnd serially
                    // (stdout, then stderr) BEFORE WaitForExit. With both streams
                    // redirected, a child filling either pipe buffer (default ~4 KiB)
                    // while we block on the other can deadlock — the call never
                    // returned and hung Chrome behind the npm shim. Use async
                    // event-driven reads + a Kill-tree fallback so even a wedged
                    // chrome-devtools-mcp subprocess gets reaped.
                    var soBuf = new System.Text.StringBuilder();
                    var seBuf = new System.Text.StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) soBuf.AppendLine(e.Data); };
                    p.ErrorDataReceived  += (s, e) => { if (e.Data != null) seBuf.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { KillProcessTree(p.Id); } catch { }
                        try { p.Kill(); } catch { }
                        // Give the async readers a moment to flush whatever was in flight.
                        try { p.WaitForExit(1000); } catch { }
                        return new CliResult { ExitCode = -1, StdOut = soBuf.ToString(), StdErr = seBuf.ToString(), TimedOut = true };
                    }
                    // After exit, ensure pending async output drains.
                    try { p.WaitForExit(500); } catch { }
                    return new CliResult { ExitCode = p.ExitCode, StdOut = soBuf.ToString(), StdErr = seBuf.ToString() };
                }
            }

            // Recursive taskkill so a Node shim spawning Chrome doesn't leak when
            // the shim itself is killed. Best-effort; failures swallowed because
            // the timeout path needs to return regardless.
            private static void KillProcessTree(int pid)
            {
                try
                {
                    var psi = new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var killer = Process.Start(psi))
                    {
                        killer?.WaitForExit(5000);
                    }
                }
                catch { /* best-effort */ }
            }

            // Cached discovery of `chrome-devtools-mcp`'s entry script — global npm
            // install path. Avoids the ~30s npx bootstrap.
            private static readonly object _mcpPathLock = new object();
            private static string _cachedMcpPath;
            private static bool _mcpPathResolved;
            internal static string ResolveChromeDevtoolsMcpPath()
            {
                if (_mcpPathResolved) return _cachedMcpPath;
                lock (_mcpPathLock)
                {
                    if (_mcpPathResolved) return _cachedMcpPath;
                    string resolved = null;
                    try
                    {
                        var envOverride = Environment.GetEnvironmentVariable("CHROME_DEVTOOLS_AXI_MCP_PATH");
                        if (!string.IsNullOrEmpty(envOverride) && File.Exists(envOverride))
                        {
                            resolved = envOverride;
                        }
                        else
                        {
                            string prefix = null;
                            try
                            {
                                var psi = new ProcessStartInfo("cmd.exe", "/c npm prefix -g")
                                {
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };
                                using (var p = Process.Start(psi))
                                {
                                    string so = p.StandardOutput.ReadToEnd();
                                    p.WaitForExit(5000);
                                    if (p.ExitCode == 0)
                                    {
                                        prefix = so.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                                    }
                                }
                            }
                            catch { }

                            if (!string.IsNullOrEmpty(prefix))
                            {
                                var candidate = Path.Combine(prefix,
                                    "node_modules", "chrome-devtools-mcp", "build", "src", "bin", "chrome-devtools-mcp.js");
                                if (File.Exists(candidate)) resolved = candidate;
                            }
                        }
                    }
                    catch { }
                    _cachedMcpPath = resolved;
                    _mcpPathResolved = true;
                    return _cachedMcpPath;
                }
            }

            public string Which(string command)
            {
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c where " + command)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        string so = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        if (p.ExitCode == 0)
                        {
                            var line = so.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                            return line?.Trim();
                        }
                    }
                }
                catch { }
                return null;
            }
        }

        private readonly ObjectService _objectService;
        private readonly BuildService _buildService;
        private readonly ICliRunner _runner;
        private readonly string _configPath;
        private readonly string _baselineRootOverride;
        private JObject _cachedConfig;
        private string _cachedCliPath;
        private AxiCliResolution _cachedCliResolution;

        // v2.6.6 Stream H (FR#25) — F5 launcher resolver. Defaults to KbService
        // when running in-process; tests inject a deterministic resolver that
        // doesn't need a live KB. Returns the object name or null.
        private Func<string> _launcherResolver;

        internal void SetLauncherResolverForTest(Func<string> resolver) => _launcherResolver = resolver;

        // Cold-start of chrome-devtools-axi's bridge (which spawns chrome-devtools-mcp
        // via npx by default) commonly takes 25-60 s on Windows. 30 s leaves no head
        // room; 90 s safely covers the warm-up plus a few snapshot/eval calls.
        private const int DefaultCliTimeoutMs = 90000;

        // Friction 2026-05-25 #4: a single preview against a GAM-protected
        // panel was observed to wedge the STA worker thread for 10+ minutes
        // while cumulative per-CLI-call timeouts stacked (open → snapshot →
        // fill → click → snapshot → capture). Every other MCP tool queues
        // behind that STA call, so we now enforce a hard wall-clock budget
        // on the whole PreviewSync call. When the budget is exceeded the
        // call returns a structured PreviewTimeout envelope instead of
        // blocking. Override via env GXMCP_PREVIEW_BUDGET_MS.
        private const int DefaultPreviewBudgetMs = 60000;

        internal static int ResolvePreviewBudgetMs()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("GXMCP_PREVIEW_BUDGET_MS");
                if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var v) && v > 1000)
                    return v;
            }
            catch { }
            return DefaultPreviewBudgetMs;
        }

        public PreviewService(ObjectService objectService, BuildService buildService)
            : this(objectService, buildService, new DefaultCliRunner(), null, null) { }

        public PreviewService(ObjectService objectService, BuildService buildService,
            ICliRunner runner, string configPath, string baselineRootOverride)
        {
            _objectService = objectService;
            _buildService = buildService;
            _runner = runner ?? new DefaultCliRunner();
            _configPath = ResolvePathValue(configPath ?? DefaultConfigPath(), AppDomain.CurrentDomain.BaseDirectory);
            _baselineRootOverride = baselineRootOverride;
            // Default resolver — best-effort lookup via KbService. Falls back to
            // null when ObjectService isn't wired (unit tests), in which case
            // the caller is expected to pass an explicit target.
            _launcherResolver = () =>
            {
                try { return _objectService?.GetKbService()?.GetLauncherObjectName(); }
                catch { return null; }
            };
        }

        /// <summary>
        /// v2.6.6 Stream H (FR#25) — F5-equivalent entry point.
        ///
        /// When <paramref name="name"/> is null/empty the launcher object
        /// configured in the KB metadata is resolved (KbService probes
        /// <c>DefaultStartupObject</c> / <c>MainObject</c> SDK properties
        /// first, then falls back to the first IsMain-tagged WebPanel /
        /// SDPanel / Procedure in the index). When no candidate exists
        /// returns a <c>NoLauncher</c> envelope rather than guessing.
        ///
        /// Explicit <c>target</c> still works — passing a name skips
        /// resolution and behaves identically to <see cref="PreviewAsync"/>.
        /// </summary>
        public Task<JObject> RunAsync(
            string name,
            JObject parms = null,
            string launcher = "auto",
            bool buildFirst = false,
            int waitMs = 3000,
            string[] capture = null,
            bool diffBaseline = false,
            bool updateBaseline = false,
            JObject fill = null,
            string click = null,
            JObject auth = null,
            string emulate = null,
            string network = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                string resolved = null;
                try { resolved = _launcherResolver?.Invoke(); } catch { }
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    var noLauncher = JObject.Parse(McpResponse.Err(
                        code: "NoLauncher",
                        message: "No KB launcher object is configured and no IsMain WebPanel/SDPanel was found in the index.",
                        hint: "Pass explicit target=<webpanel> or set a startup object via genexus_kb_startup.",
                        nextSteps: new JArray { McpResponse.NextStep("genexus_kb_startup", new JObject { ["action"] = "set" }, "Set the KB startup object.") }));
                    return Task.FromResult(noLauncher);
                }
                name = resolved;
            }

            var result = PreviewSync(name, parms, launcher, buildFirst, waitMs, capture, diffBaseline, updateBaseline, fill, click, auth, emulate, network);
            result["resolvedLauncher"] = name;
            return Task.FromResult(result);
        }

        private static string DefaultConfigPath()
        {
            // publish/worker/preview.config.json relative to the worker exe; falls back
            // to %CD%/publish/worker/... for dev runs.
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
                return Path.Combine(baseDir, "preview.config.json");
            }
            catch
            {
                return "preview.config.json";
            }
        }

        public static JObject DefaultConfig()
        {
            return new JObject
            {
                ["baseUrl"] = "http://localhost/portal3_desenv",
                ["launcher"] = "dani.aspx",
                ["defaultParms"] = new JObject
                {
                    ["PesCod"] = "5171369",
                    ["ano"] = "27",
                    ["sem"] = "1",
                    ["aluno"] = "27,1,6179"
                },
                ["objectParms"] = new JObject(),
                ["axiCli"] = null,
                ["baselineDir"] = "publish/worker/preview-baselines"
            };
        }

        internal JObject LoadConfig()
        {
            if (_cachedConfig != null) return _cachedConfig;
            try
            {
                if (!File.Exists(_configPath))
                {
                    var def = DefaultConfig();
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? ".");
                        File.WriteAllText(_configPath, def.ToString(Formatting.Indented));
                    }
                    catch { /* best-effort write */ }
                    _cachedConfig = def;
                }
                else
                {
                    _cachedConfig = JObject.Parse(File.ReadAllText(_configPath));
                }
            }
            catch
            {
                _cachedConfig = DefaultConfig();
            }
            return _cachedConfig;
        }

        internal string ResolveCli()
        {
            if (!string.IsNullOrEmpty(_cachedCliPath)) return _cachedCliPath;
            var diagnostic = ResolveCliDiagnostic();
            _cachedCliPath = diagnostic.ResolvedPath;
            return _cachedCliPath;
        }

        internal AxiCliResolution ResolveCliDiagnostic()
        {
            if (_cachedCliResolution != null) return _cachedCliResolution;

            var cfg = LoadConfig();
            string workerDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
            string profilePath = ResolvePathValue(
                Environment.GetEnvironmentVariable("GXMCP_PROFILE_CONFIG_PATH"), workerDir);
            JObject profile = TryLoadJson(profilePath);
            var runtimeDirectories = new List<string>();
            AddDirectory(runtimeDirectories, ResolvePathValue(
                Environment.GetEnvironmentVariable("GXMCP_RUNTIME_DIR"), workerDir));
            AddDirectory(runtimeDirectories, ResolvePathValue(
                Environment.GetEnvironmentVariable("GXMCP_DEPENDENCIES_DIR"), workerDir));
            AddDirectory(runtimeDirectories, ResolvePathValue(
                GetConfigString(profile, "runtimeDirectory", "runtimeDir", "dependenciesDirectory", "dependenciesDir"),
                DirectoryOf(profilePath) ?? workerDir));

            string backendDir = null;
            try { backendDir = Directory.GetParent(workerDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName; }
            catch { }

            _cachedCliResolution = ResolveAxiCli(
                cfg,
                _configPath,
                profile,
                profilePath,
                workerDir,
                backendDir,
                runtimeDirectories,
                Environment.GetEnvironmentVariable("PATH"),
                command => _runner.Which(command));

            if (_cachedCliResolution.Resolved)
            {
                try
                {
                    var version = _runner.Run(_cachedCliResolution.ResolvedPath, "--version", 10000);
                    _cachedCliResolution.Version = FirstOutputLine(version?.StdOut) ?? FirstOutputLine(version?.StdErr);
                }
                catch { }
            }

            return _cachedCliResolution;
        }

        internal static AxiCliResolution ResolveAxiCli(
            JObject previewConfig,
            string previewConfigPath,
            JObject profileConfig,
            string profileConfigPath,
            string workerDir,
            string backendDir,
            IEnumerable<string> runtimeDirectories,
            string pathUsed,
            Func<string, string> pathProbe)
        {
            var resolution = new AxiCliResolution
            {
                ConfigFile = ResolvePathValue(previewConfigPath, workerDir),
                ProfileConfigFile = ResolvePathValue(profileConfigPath, workerDir),
                AxiCli = GetConfigString(previewConfig, "axiCli"),
                ProfileAxiCli = GetConfigString(profileConfig, "axiCli"),
                PathUsed = pathUsed ?? string.Empty
            };

            // 1) Explicit preview configuration. Relative paths are owned by the
            // config file, never by the process current directory.
            if (TryConfigured(resolution, resolution.AxiCli, DirectoryOf(resolution.ConfigFile), "preview.config.json"))
                return resolution;

            // 2) MCP profile configuration. The Gateway forwards its absolute
            // config path to the Worker through GXMCP_PROFILE_CONFIG_PATH.
            if (TryConfigured(resolution, resolution.ProfileAxiCli, DirectoryOf(resolution.ProfileConfigFile), "MCP profile"))
                return resolution;

            // 3) Runtime/dependency directories, including npm's local .bin shims.
            if (runtimeDirectories != null)
            {
                foreach (var directory in runtimeDirectories)
                    if (TryDirectory(resolution, directory, workerDir, "MCP runtime/dependencies")) return resolution;
            }
            foreach (var ancestor in Ancestors(workerDir))
            {
                if (TryDirectory(resolution, Path.Combine(ancestor, "node_modules", ".bin"), workerDir, "MCP runtime/dependencies")) return resolution;
                if (TryDirectory(resolution, Path.Combine(ancestor, "node_modules"), workerDir, "MCP runtime/dependencies")) return resolution;
            }

            // 4) The Worker and its backend directory.
            if (TryDirectory(resolution, workerDir, workerDir, "Worker/backend")) return resolution;
            if (!string.Equals(NormalizeDirectory(workerDir), NormalizeDirectory(backendDir), StringComparison.OrdinalIgnoreCase) &&
                TryDirectory(resolution, backendDir, workerDir, "Worker/backend")) return resolution;

            // 5) Last resort: the effective PATH inherited by the Worker.
            if (pathProbe != null)
            {
                foreach (var name in AxiCliNames)
                {
                    AddSearch(resolution, "PATH:" + name);
                    string probed = null;
                    try { probed = pathProbe(name); } catch { }
                    if (!string.IsNullOrWhiteSpace(probed))
                    {
                        resolution.ResolvedPath = ResolvePathValue(CleanPath(probed), workerDir);
                        resolution.Source = "PATH";
                        return resolution;
                    }
                }
            }

            return resolution;
        }

        private static readonly string[] AxiCliNames =
        {
            "chrome-devtools-axi.exe",
            "chrome-devtools-axi.cmd",
            "chrome-devtools-axi.bat",
            "chrome-devtools-axi.ps1",
            "chrome-devtools-axi"
        };

        private static bool TryConfigured(AxiCliResolution resolution, string rawPath, string baseDir, string source)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return false;
            string candidate = ResolvePathValue(CleanPath(rawPath), baseDir);
            AddSearch(resolution, candidate);
            if (File.Exists(candidate))
            {
                resolution.ResolvedPath = candidate;
                resolution.Source = source;
                return true;
            }
            return Directory.Exists(candidate) && TryDirectory(resolution, candidate, baseDir, source);
        }

        private static bool TryDirectory(AxiCliResolution resolution, string directory, string baseDir, string source)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            string fullDir = ResolvePathValue(CleanPath(directory), baseDir);
            foreach (var name in AxiCliNames)
            {
                string candidate = Path.Combine(fullDir, name);
                AddSearch(resolution, candidate);
                if (File.Exists(candidate))
                {
                    resolution.ResolvedPath = candidate;
                    resolution.Source = source;
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<string> Ancestors(string directory)
        {
            string current = NormalizeDirectory(directory);
            for (int i = 0; !string.IsNullOrEmpty(current) && i < 8; i++)
            {
                yield return current;
                string parent = null;
                try { parent = Directory.GetParent(current)?.FullName; } catch { }
                if (string.Equals(current, parent, StringComparison.OrdinalIgnoreCase)) yield break;
                current = parent;
            }
        }

        private static string GetConfigString(JObject config, params string[] names)
        {
            if (config == null) return null;
            foreach (var property in config.Properties())
            {
                if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return property.Value?.Type == JTokenType.Null ? null : property.Value?.ToString();
            }

            foreach (var sectionName in new[] { "preview", "browser", "browserDriver", "environment" })
            {
                var section = config.Properties().FirstOrDefault(p =>
                    string.Equals(p.Name, sectionName, StringComparison.OrdinalIgnoreCase))?.Value as JObject;
                var nested = GetConfigString(section, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
            return null;
        }

        private static JObject TryLoadJson(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try { return JObject.Parse(File.ReadAllText(path)); }
            catch { return null; }
        }

        private static void AddDirectory(List<string> directories, string directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) &&
                !directories.Any(d => string.Equals(d, directory, StringComparison.OrdinalIgnoreCase)))
                directories.Add(directory);
        }

        private static void AddSearch(AxiCliResolution resolution, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !resolution.SearchedPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                resolution.SearchedPaths.Add(path);
        }

        private static string CleanPath(string path)
        {
            string value = path?.Trim() ?? string.Empty;
            while (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2).Trim();
            try { value = Environment.ExpandEnvironmentVariables(value); } catch { }
            return value;
        }

        private static string ResolvePathValue(string path, string baseDir)
        {
            string value = CleanPath(path);
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                if (!Path.IsPathRooted(value))
                {
                    string root = CleanPath(baseDir);
                    if (string.IsNullOrWhiteSpace(root)) root = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
                    value = Path.Combine(root, value);
                }
                return Path.GetFullPath(value);
            }
            catch { return value; }
        }

        private static string NormalizeDirectory(string directory)
        {
            string value = ResolvePathValue(directory, AppDomain.CurrentDomain.BaseDirectory);
            return value?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string DirectoryOf(string path)
        {
            try { return Path.GetDirectoryName(path); } catch { return null; }
        }

        private static string FirstOutputLine(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
        }

        /// <summary>Merge precedence: defaultParms &lt; objectParms[name] &lt; caller.</summary>
        internal static JObject MergeParms(JObject config, string name, JObject caller)
        {
            var merged = new JObject();
            var def = config?["defaultParms"] as JObject;
            if (def != null) foreach (var p in def.Properties()) merged[p.Name] = p.Value?.DeepClone();
            var perObj = config?["objectParms"]?[name] as JObject;
            if (perObj != null) foreach (var p in perObj.Properties()) merged[p.Name] = p.Value?.DeepClone();
            if (caller != null) foreach (var p in caller.Properties()) merged[p.Name] = p.Value?.DeepClone();
            return merged;
        }

        /// <summary>
        /// Main entry. Returns a JObject with at minimum a 'status' field. Never throws.
        /// </summary>
        public Task<JObject> PreviewAsync(
            string name,
            JObject parms = null,
            string launcher = "auto",
            bool buildFirst = false,
            int waitMs = 3000,
            string[] capture = null,
            bool diffBaseline = false,
            bool updateBaseline = false,
            JObject fill = null,
            string click = null,
            JObject auth = null,
            string emulate = null,
            string network = null)
        {
            return Task.FromResult(PreviewSync(name, parms, launcher, buildFirst, waitMs, capture, diffBaseline, updateBaseline, fill, click, auth, emulate, network));
        }

        // Item 39: device emulation profiles forwarded to chrome-devtools-axi
        // as `--emulate <profile>`. Names mirror the CLI's preset list so
        // typos surface early as cli errors rather than silent misrender.
        internal static readonly HashSet<string> EmulateProfiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "iPhone12", "iPhone15Pro", "iPadPro", "Pixel7", "desktop1920", "desktop1280" };

        // Item 97: network-throttle profiles forwarded as `--throttle <name>`.
        // "fast" is the unthrottled default — we skip the flag in that case
        // so existing baselines (recorded without throttling) stay reproducible.
        internal static readonly HashSet<string> NetworkProfiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "fast", "slow3g", "fast3g", "offline" };

        internal static string BuildEmulateNetworkArgs(string emulate, string network)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(emulate) && EmulateProfiles.Contains(emulate))
            {
                sb.Append(" --emulate ").Append(emulate);
            }
            if (!string.IsNullOrWhiteSpace(network) &&
                NetworkProfiles.Contains(network) &&
                !string.Equals(network, "fast", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" --throttle ").Append(network);
            }
            return sb.ToString();
        }

        internal JObject PreviewSync(
            string name,
            JObject parms,
            string launcher,
            bool buildFirst,
            int waitMs,
            string[] capture,
            bool diffBaseline,
            bool updateBaseline,
            JObject fill = null,
            string click = null,
            JObject auth = null,
            string emulate = null,
            string network = null)
        {
            var result = new JObject { ["name"] = name };
            // Bug #5: the preview URL is served by the IIS vroot, which reflects the last
            // FULL deploy — a fast-path MCP build compiles but does not publish the .aspx
            // there, so a rendered page can lag a recent MCP edit.
            result["deploymentNote"] = "Preview opens the IIS vroot URL, which serves the last FULL deploy — a fast-path build (genexus_lifecycle action=build) compiles the object but does not publish the .aspx to the vroot. If the page looks stale, build with deploy=true (or action=rebuild) or publish from the GeneXus IDE.";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int budgetMs = ResolvePreviewBudgetMs();
            bool BudgetExceeded() => sw.ElapsedMilliseconds > budgetMs;
            JObject Timeout(string stage)
            {
                return JObject.Parse(McpResponse.Err(
                    code: "PreviewTimeout",
                    message: "Preview exceeded the wall-clock budget (default 60s; override GXMCP_PREVIEW_BUDGET_MS). The STA worker has been released.",
                    hint: "Retry with auth credentials pre-injected or buildFirst=false to skip the build step.",
                    nextSteps: new JArray {
                        McpResponse.NextStep("genexus_preview", new JObject { ["target"] = name, ["buildFirst"] = false }, "Skip build to reduce elapsed time."),
                        McpResponse.NextStep("genexus_lifecycle", new JObject { ["action"] = "build", ["target"] = name }, "Pre-build then re-run preview.")
                    },
                    extra: new JObject { ["elapsedMs"] = sw.ElapsedMilliseconds, ["stage"] = stage }));
            }
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    result["status"] = "invalid_request";
                    result["message"] = "name is required";
                    return result;
                }

                if (!IsSafeObjectName(name) || !IsValidPreviewName(name))
                {
                    result["status"] = "invalid_request";
                    result["message"] = "name must be a valid logical preview name";
                    return result;
                }

                // 1) Object type check (best-effort; skipped if ObjectService unavailable in tests)
                if (_objectService != null)
                {
                    try
                    {
                        var obj = _objectService.FindObject(name);
                        if (obj != null)
                        {
                            string typeName = null;
                            try { typeName = obj.TypeDescriptor?.Name; } catch { }
                            if (!string.IsNullOrEmpty(typeName) &&
                                !string.Equals(typeName, "WebPanel", StringComparison.OrdinalIgnoreCase))
                            {
                                result["status"] = "unsupported_object_type";
                                result["type"] = typeName;
                                return result;
                            }
                        }
                    }
                    catch { /* type check best-effort */ }
                }

                var cfg = LoadConfig();
                string baselineDir;
                string screenshotPath;
                string baselinePath;
                if (!TryResolveArtifactPaths(cfg, name, out baselineDir, out screenshotPath, out baselinePath))
                {
                    result["status"] = "invalid_request";
                    result["message"] = "name must be a valid logical preview name";
                    return result;
                }
                var mergedParms = MergeParms(cfg, name, parms);
                if (!AreSafePreviewValues(mergedParms)) return InvalidPreviewRequest(result, "preview parameters contain control characters");
                result["parms"] = mergedParms;

                // 2) Optional buildFirst
                if (buildFirst && _buildService != null)
                {
                    try
                    {
                        var buildJson = _buildService.Build("Build", name);
                        var buildEnv = JObject.Parse(buildJson);
                        string buildStatus = buildEnv["status"]?.ToString();
                        if (string.Equals(buildStatus, "BuildPlanTooLarge", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(buildStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(buildStatus, "Error", StringComparison.OrdinalIgnoreCase))
                        {
                            result["status"] = "build_failed";
                            result["buildError"] = buildEnv;
                            return result;
                        }
                        result["build"] = buildEnv;
                        // Note: build is async in this codebase. Caller can poll separately.
                        // For preview parity we proceed; agent may pass buildFirst=false and
                        // build via lifecycle ahead of time for full correctness.
                    }
                    catch (Exception bex)
                    {
                        result["status"] = "build_failed";
                        string operationId = Guid.NewGuid().ToString("N");
                        Logger.Error($"{{\"event\":\"preview_build_failed\",\"operationId\":\"{operationId}\",\"exceptionType\":\"{bex.GetType().FullName}\",\"exception\":\"{LogValue(bex.ToString())}\"}}");
                        result["buildError"] = "Preview build failed. See server logs for details.";
                        result["operationId"] = operationId;
                        return result;
                    }
                }

                // 3) Resolve CLI and expose the exact preflight evidence.
                var cliDiagnostic = ResolveCliDiagnostic();
                result["axiCli"] = cliDiagnostic.ToJObject();
                var cli = cliDiagnostic.ResolvedPath;
                if (string.IsNullOrEmpty(cli))
                {
                    result["status"] = "cli_missing";
                    result["message"] = "chrome-devtools-axi was not found. See the axiCli preflight fields for searched paths, config files, configured values, effective PATH and version. To fix: configure an absolute axiCli path or install chrome-devtools-axi in the MCP runtime, then restart the Worker.";
                    return result;
                }
                result["cli"] = cli;

                // 4) Build launcher URL
                string baseUrl = (cfg["baseUrl"]?.ToString() ?? "http://localhost/portal3_desenv").TrimEnd('/');
                string launcherPage = (launcher == null || launcher == "auto") ? (cfg["launcher"]?.ToString() ?? "dani.aspx") : launcher;
                if (!IsSafeBaseUrl(baseUrl)) return InvalidPreviewRequest(result, "baseUrl must be an absolute http(s) URL");
                if (!IsSafeLauncherPage(launcherPage)) return InvalidPreviewRequest(result, "launcher must be a relative page path");
                string launcherUrl = baseUrl + "/" + launcherPage.TrimStart('/');
                result["launcherUrl"] = launcherUrl;

                // 5) Open launcher
                //    Items 39/97: forward device-emulation + network-throttle
                //    profiles to chrome-devtools-axi via `--emulate` / `--throttle`.
                //    Unknown names are dropped silently (BuildEmulateNetworkArgs
                //    validates against the schema enum), so a typo never becomes a
                //    garbled command line.
                string emulateNetArgs = BuildEmulateNetworkArgs(emulate, network);
                if (!string.IsNullOrEmpty(emulateNetArgs))
                {
                    var emuRes = new JObject();
                    if (!string.IsNullOrWhiteSpace(emulate) && EmulateProfiles.Contains(emulate)) emuRes["emulate"] = emulate;
                    if (!string.IsNullOrWhiteSpace(network) && NetworkProfiles.Contains(network)) emuRes["network"] = network;
                    if (emuRes.Count > 0) result["emulation"] = emuRes;
                }
                // Per-step CLI timeout is bounded by remaining budget so a
                // single stuck CLI call can't bypass the wall-clock budget.
                int StepTimeout() => (int)Math.Max(2000, Math.Min(DefaultCliTimeoutMs, budgetMs - sw.ElapsedMilliseconds));

                var openRes = _runner.Run(cli, "open " + Quote(launcherUrl) + emulateNetArgs, StepTimeout());
                if (openRes.TimedOut && BudgetExceeded()) return Timeout("open_launcher");
                if (openRes.TimedOut || openRes.ExitCode != 0)
                {
                    result["status"] = "launcher_missing";
                    result["url"] = launcherUrl;
                    result["stderr"] = openRes.StdErr;
                    return result;
                }
                if (BudgetExceeded()) return Timeout("after_open");

                // 6) Snapshot launcher to detect auth/form
                var snap1 = _runner.Run(cli, "snapshot", StepTimeout());
                string snap1Text = snap1.StdOut ?? "";
                if (BudgetExceeded()) return Timeout("snapshot_launcher");

                // 6.0) Surface the final URL so the agent (and our GAM
                //     detector) can distinguish a GAM redirect from a
                //     legitimate launcher load.
                string finalUrl = launcherUrl;
                try
                {
                    var urlRes = _runner.Run(cli, "eval " + Quote("location.href"), Math.Min(StepTimeout(), 10000));
                    var u = (urlRes.StdOut ?? "").Trim().Trim('"');
                    if (!string.IsNullOrEmpty(u)) finalUrl = u;
                }
                catch { }
                result["finalUrl"] = finalUrl;

                // 6a) FR#17 — GAM session injection. When auth.mode == "gam" (or
                //     env vars are set and the page looks like login), fill the
                //     GAM login form and resubmit before bailing out.
                bool authAttempted = false;
                if (LooksLikeAuthScreen(snap1Text) || LooksLikeGamLoginUrl(launcherUrl) || LooksLikeGamLoginUrl(finalUrl))
                {
                    var ai = ResolveAuthInfo(auth);
                    if (ai.Mode == "gam" && !string.IsNullOrEmpty(ai.User) && !string.IsNullOrEmpty(ai.Pass))
                    {
                        authAttempted = true;
                        var driver = GxFormDriver.Parse(snap1Text);
                        var loginVals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["UserName"] = ai.User,
                            ["User"] = ai.User,
                            ["Usuario"] = ai.User,
                            ["UserPassword"] = ai.Pass,
                            ["Password"] = ai.Pass,
                            ["Senha"] = ai.Pass
                        };
                        // Restrict the fill to attrs actually present on the page so we
                        // don't spam noise into the result.errors[].
                        var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in loginVals)
                        {
                            if (driver.ResolveSelector(kv.Key, out _) != null) present[kv.Key] = kv.Value;
                        }
                        string loginJs = driver.BuildFillScript(present, out _);
                        if (!string.IsNullOrEmpty(loginJs))
                            _runner.Run(cli, "eval " + Quote(loginJs), DefaultCliTimeoutMs);

                        // Standard GAM submit button id is "GXSUBMIT" / class gxsubmit /
                        // sometimes a regular form submit. Try a couple of selectors.
                        const string submitJs =
                            "(function(){var b=document.querySelector('input.gxsubmit,button.gxsubmit," +
                            "input[id=GXSUBMIT],input[name=GXSUBMIT],input[type=submit],button[type=submit]');" +
                            "if(b){b.click();return 'clicked';}" +
                            "var f=document.forms[0];if(f){f.submit();return 'form.submit';}" +
                            "return 'no-submit';})()";
                        _runner.Run(cli, "eval " + Quote(submitJs), DefaultCliTimeoutMs);

                        // Allow the redirect to settle, then re-snapshot.
                        try { System.Threading.Thread.Sleep(1500); } catch { }
                        var snapPost = _runner.Run(cli, "snapshot", DefaultCliTimeoutMs);
                        snap1Text = snapPost.StdOut ?? "";
                        result["auth"] = new JObject { ["mode"] = "gam", ["status"] = "injected" };
                    }
                }

                if (LooksLikeAuthScreen(snap1Text))
                {
                    result["status"] = "auth_required";
                    result["url"] = launcherUrl;
                    if (authAttempted) result["message"] = "GAM injection attempted but login screen still detected.";
                    return result;
                }
                if (!LooksLikeLauncherForm(snap1Text, mergedParms))
                {
                    result["status"] = "launcher_missing";
                    result["url"] = launcherUrl;
                    result["message"] = "Launcher page loaded but expected form fields were not detected.";
                    return result;
                }

                // 7) Fill form fields per merged parms (eval document.getElementsByName)
                foreach (var p in mergedParms.Properties())
                {
                    string js = string.Format(
                        "var el=document.getElementsByName('{0}')[0]; if(el){{el.value={1};el.dispatchEvent(new Event('change'));}}",
                        EscapeJs(p.Name),
                        JsonConvert.SerializeObject(p.Value?.ToString() ?? ""));
                    _runner.Run(cli, "eval " + Quote(js), DefaultCliTimeoutMs);
                }

                // 8) Click the link matching name OR href contains <name>.aspx
                string clickJs = string.Format(
                    "var links=document.querySelectorAll('a'); for(var i=0;i<links.length;i++){{var l=links[i]; if((l.textContent||'').trim().toLowerCase()==='{0}'.toLowerCase() || (l.getAttribute('href')||'').toLowerCase().indexOf('{0}.aspx'.toLowerCase())>=0){{l.click(); break;}}}}",
                    EscapeJs(name));
                _runner.Run(cli, "eval " + Quote(clickJs), DefaultCliTimeoutMs);

                // 8a) FR#16 — GX-aware fill / click. After navigating to the
                //     target panel, snapshot once and drive the GX form through
                //     GxFormDriver (logical attr names → selectors → fill JS).
                if ((fill != null && fill.Count > 0) || !string.IsNullOrWhiteSpace(click))
                {
                    if (!AreSafePreviewValues(fill)) return InvalidPreviewRequest(result, "fill contains control characters");
                    if (!IsSafePreviewText(click)) return InvalidPreviewRequest(result, "click contains control characters");
                    // Give the click above a brief moment to navigate before we
                    // snapshot the resulting GX panel.
                    try { System.Threading.Thread.Sleep(Math.Min(waitMs, 5000)); } catch { }
                    var snapPanel = _runner.Run(cli, "snapshot", DefaultCliTimeoutMs);
                    var driver = GxFormDriver.Parse(snapPanel.StdOut ?? "");
                    var fillReport = new JObject();

                    if (fill != null && fill.Count > 0)
                    {
                        var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in fill.Properties())
                            vals[p.Name] = p.Value?.ToString() ?? "";
                        string fillJs = driver.BuildFillScript(vals, out var fillErrors);
                        if (!string.IsNullOrEmpty(fillJs))
                            _runner.Run(cli, "eval " + Quote(fillJs), DefaultCliTimeoutMs);
                        var errArr = new JArray();
                        foreach (var e in fillErrors) errArr.Add(e);
                        fillReport["errors"] = errArr;
                        fillReport["requested"] = fill.Count;
                        fillReport["resolved"] = fill.Count - fillErrors.Count;
                    }

                    if (!string.IsNullOrWhiteSpace(click))
                    {
                        string evJs = driver.BuildClickScript(click);
                        if (!string.IsNullOrEmpty(evJs))
                            _runner.Run(cli, "eval " + Quote(evJs), DefaultCliTimeoutMs);
                        fillReport["click"] = click;
                    }

                    if (fillReport.Count > 0) result["formDriver"] = fillReport;
                }

                // 9) Wait
                if (waitMs > 0)
                {
                    try { System.Threading.Thread.Sleep(Math.Min(waitMs, 120000)); } catch { }
                }

                // 10) Capture
                var captureSet = new HashSet<string>(
                    capture ?? new[] { "html", "a11y" },
                    StringComparer.OrdinalIgnoreCase);
                var captures = new JObject();

                JObject a11yObj = null;
                if (captureSet.Contains("a11y"))
                {
                    var snap2 = _runner.Run(cli, "snapshot", DefaultCliTimeoutMs);
                    captures["a11y_raw"] = snap2.StdOut ?? "";
                    a11yObj = TryParseJson(snap2.StdOut);
                    if (a11yObj != null) captures["a11y"] = a11yObj;
                }
                if (captureSet.Contains("html"))
                {
                    var htmlRes = _runner.Run(cli, "eval " + Quote("document.documentElement.outerHTML"), DefaultCliTimeoutMs);
                    captures["html"] = htmlRes.StdOut ?? "";
                }
                if (captureSet.Contains("console"))
                {
                    var conRes = _runner.Run(cli, "eval " + Quote("JSON.stringify(window.__gxConsoleErrors||[])"), DefaultCliTimeoutMs);
                    captures["console"] = conRes.StdOut ?? "";
                }
                if (captureSet.Contains("exceptions"))
                {
                    var exRes = _runner.Run(cli, "eval " + Quote("JSON.stringify(window.__gxUnhandledErrors||[])"), DefaultCliTimeoutMs);
                    captures["exceptions"] = ParseJsonArrayOrEmpty(exRes.StdOut);
                }
                if (captureSet.Contains("screenshot"))
                {
                    try { Directory.CreateDirectory(baselineDir); } catch { }
                    var shotRes = _runner.Run(cli, "screenshot " + Quote(screenshotPath), DefaultCliTimeoutMs);
                    captures["screenshot"] = screenshotPath;
                    if (shotRes.ExitCode != 0) captures["screenshotError"] = shotRes.StdErr;
                }

                result["captures"] = captures;

                // 11) Baseline diff / update
                if (diffBaseline)
                {
                    if (File.Exists(baselinePath) && a11yObj != null)
                    {
                        try
                        {
                            var baseline = JObject.Parse(File.ReadAllText(baselinePath));
                            result["diff"] = ComputeStructuralDiff(baseline, a11yObj);
                        }
                        catch (Exception dex)
                        {
                            result["diff"] = null;
                            string operationId = Guid.NewGuid().ToString("N");
                            Logger.Error($"{{\"event\":\"preview_diff_failed\",\"operationId\":\"{operationId}\",\"exceptionType\":\"{dex.GetType().FullName}\",\"exception\":\"{LogValue(dex.ToString())}\"}}");
                            result["diffError"] = "Preview baseline diff failed. See server logs for details.";
                            result["operationId"] = operationId;
                        }
                    }
                    else
                    {
                        result["diff"] = null;
                    }
                }
                if (updateBaseline && a11yObj != null)
                {
                    try
                    {
                        Directory.CreateDirectory(baselineDir);
                        File.WriteAllText(baselinePath, a11yObj.ToString(Formatting.Indented));
                        result["baselineUpdated"] = baselinePath;
                    }
                    catch (Exception wex)
                    {
                        string operationId = Guid.NewGuid().ToString("N");
                        Logger.Error($"{{\"event\":\"preview_baseline_update_failed\",\"operationId\":\"{operationId}\",\"exceptionType\":\"{wex.GetType().FullName}\",\"exception\":\"{LogValue(wex.ToString())}\"}}");
                        result["baselineUpdateError"] = "Preview baseline update failed. See server logs for details.";
                        result["operationId"] = operationId;
                    }
                }

                result["status"] = "ok";
                return result;
            }
            catch (Exception ex)
            {
                result["status"] = "error";
                string operationId = Guid.NewGuid().ToString("N");
                Logger.Error($"{{\"event\":\"preview_failed\",\"operationId\":\"{operationId}\",\"exceptionType\":\"{ex.GetType().FullName}\",\"exception\":\"{LogValue(ex.ToString())}\"}}");
                result["message"] = "Preview failed. See server logs for details.";
                result["operationId"] = operationId;
                return result;
            }
        }

        internal static string LogValue(string value)
        {
            string redacted = Regex.Replace(
                value ?? string.Empty,
                @"(?is)(?<key>\b(?:password|passwd|pass|token|secret|api[-_]?key|authorization|credential)\b)\s*[""']?\s*(?<separator>\s*[:=]\s*)(?:"".*?""|'.*?'|(?:Bearer\s+)?[^\s,;}&\]]+)",
                match => match.Groups["key"].Value + match.Groups["separator"].Value + "<redacted>");
            return redacted.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace(((char)13).ToString(), "\r").Replace(((char)10).ToString(), "\n");
        }

        private string ResolveBaselineDir(JObject cfg)
        {
            if (!string.IsNullOrEmpty(_baselineRootOverride)) return Path.GetFullPath(_baselineRootOverride);
            string fromCfg = cfg?["baselineDir"]?.ToString();
            if (string.IsNullOrEmpty(fromCfg)) fromCfg = "publish/worker/preview-baselines";
            if (Path.IsPathRooted(fromCfg)) return Path.GetFullPath(fromCfg);
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
                return Path.GetFullPath(Path.Combine(baseDir, "preview-baselines"));
            }
            catch
            {
                return Path.GetFullPath(fromCfg);
            }
        }

        internal static bool IsValidPreviewName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf("..", StringComparison.Ordinal) >= 0 ||
                name.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0 ||
                !name.All(c => c >= ' '))
                return false;

            return !Path.IsPathRooted(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private bool TryResolveArtifactPaths(JObject cfg, string name, out string root, out string screenshotPath, out string baselinePath)
        {
            root = screenshotPath = baselinePath = null;
            try
            {
                root = ResolveBaselineDir(cfg);
                screenshotPath = Path.GetFullPath(Path.Combine(root, name + ".png"));
                baselinePath = Path.GetFullPath(Path.Combine(root, name + ".a11y.json"));
                return IsUnderRoot(root, screenshotPath) && IsUnderRoot(root, baselinePath);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUnderRoot(string root, string path)
        {
            string canonicalRoot = Path.GetFullPath(root);
            if (!canonicalRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
                !canonicalRoot.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                canonicalRoot += Path.DirectorySeparatorChar;
            return path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool LooksLikeAuthScreen(string snapshot)
        {
            if (string.IsNullOrEmpty(snapshot)) return false;
            // Heuristic mentioned in spec — Usuario textbox is the prod login marker.
            var s = snapshot;
            return (s.IndexOf("Usuario", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    s.IndexOf("textbox", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   s.IndexOf("login.aspx", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool LooksLikeLauncherForm(string snapshot, JObject parms)
        {
            if (string.IsNullOrEmpty(snapshot)) return false;
            // Require at least one configured parm name to appear in snapshot.
            if (parms == null || parms.Count == 0)
            {
                // fall back to the documented defaults
                return ContainsAny(snapshot, "PesCod", "ano", "sem", "aluno");
            }
            foreach (var p in parms.Properties())
            {
                if (snapshot.IndexOf(p.Name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool ContainsAny(string haystack, params string[] needles)
        {
            foreach (var n in needles)
                if (haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static JObject ComputeStructuralDiff(JObject baseline, JObject current)
        {
            var added = new JArray();
            var removed = new JArray();
            var changed = new JArray();
            DiffWalk("", baseline, current, added, removed, changed);
            return new JObject
            {
                ["added"] = added,
                ["removed"] = removed,
                ["changed"] = changed
            };
        }

        private static void DiffWalk(string path, JToken a, JToken b, JArray added, JArray removed, JArray changed)
        {
            if (a == null && b == null) return;
            if (a == null) { added.Add(path); return; }
            if (b == null) { removed.Add(path); return; }
            if (a.Type != b.Type)
            {
                changed.Add(new JObject { ["path"] = path, ["from"] = a.Type.ToString(), ["to"] = b.Type.ToString() });
                return;
            }
            if (a is JObject ao && b is JObject bo)
            {
                var keys = new HashSet<string>(ao.Properties().Select(p => p.Name));
                keys.UnionWith(bo.Properties().Select(p => p.Name));
                foreach (var k in keys)
                {
                    DiffWalk(path + "/" + k, ao[k], bo[k], added, removed, changed);
                }
            }
            else if (a is JArray aa && b is JArray ba)
            {
                int max = Math.Max(aa.Count, ba.Count);
                for (int i = 0; i < max; i++)
                {
                    JToken ax = i < aa.Count ? aa[i] : null;
                    JToken bx = i < ba.Count ? ba[i] : null;
                    DiffWalk(path + "[" + i + "]", ax, bx, added, removed, changed);
                }
            }
            else
            {
                if (!JToken.DeepEquals(a, b))
                    changed.Add(new JObject { ["path"] = path, ["from"] = a.ToString(), ["to"] = b.ToString() });
            }
        }

        private static JObject TryParseJson(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { return JObject.Parse(s); } catch { return null; }
        }

        internal static JArray ParseJsonArrayOrEmpty(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new JArray();
            try
            {
                var token = JToken.Parse(s.Trim());
                return token as JArray ?? new JArray();
            }
            catch { return new JArray(); }
        }

        private static string Quote(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string EscapeJs(string s) =>
            (s ?? "")
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("</", "<\\/");

        private static JObject InvalidPreviewRequest(JObject result, string message)
        {
            result["status"] = "invalid_request";
            result["message"] = message;
            return result;
        }

        internal static bool IsSafePreviewText(string value)
        {
            if (value == null) return true;
            foreach (var c in value)
                if (char.IsControl(c) || c == '\u2028' || c == '\u2029') return false;
            return true;
        }

        internal static bool AreSafePreviewValues(JObject values)
        {
            if (values == null) return true;
            foreach (var p in values.Properties())
                if (string.IsNullOrEmpty(p.Name) || !IsSafePreviewText(p.Name) || !IsSafePreviewText(p.Value?.ToString())) return false;
            return true;
        }

        private static bool IsSafeObjectName(string value)
        {
            if (string.IsNullOrEmpty(value) || !IsSafePreviewText(value)) return false;
            if (!(char.IsLetter(value[0]) || value[0] == '_')) return false;
            for (int i = 1; i < value.Length; i++)
                if (!(char.IsLetterOrDigit(value[i]) || value[i] == '_')) return false;
            return true;
        }

        private static bool IsSafeBaseUrl(string value)
        {
            if (!IsSafePreviewText(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        private static bool IsSafeLauncherPage(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !IsSafePreviewText(value) || value.IndexOf('?') >= 0 || value.IndexOf('#') >= 0 || value.Contains("..")) return false;
            foreach (var c in value)
                if (!(char.IsLetterOrDigit(c) || c == '/' || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }

        // ---- FR#17 GAM session injection helpers ----------------------------

        internal class AuthInfo
        {
            public string Mode = "none";
            public string User;
            public string Pass;
            public string LoginUrl;
        }

        /// <summary>
        /// Resolves the effective auth config. Precedence: caller-passed
        /// <c>auth</c> JObject &gt; env (<c>GXMCP_GAM_USER</c> /
        /// <c>GXMCP_GAM_PASS</c> / <c>GXMCP_GAM_LOGIN_URL</c>) &gt; default
        /// "none". Never logs credentials.
        /// </summary>
        internal static AuthInfo ResolveAuthInfo(JObject auth)
        {
            var ai = new AuthInfo();
            string mode = auth?["mode"]?.ToString();
            string user = auth?["user"]?.ToString();
            string pass = auth?["pass"]?.ToString();

            if (string.IsNullOrEmpty(user))
                user = Environment.GetEnvironmentVariable("GXMCP_GAM_USER");
            if (string.IsNullOrEmpty(pass))
                pass = Environment.GetEnvironmentVariable("GXMCP_GAM_PASS");
            string envLogin = Environment.GetEnvironmentVariable("GXMCP_GAM_LOGIN_URL");

            // If no explicit mode but env creds exist, default to gam.
            if (string.IsNullOrEmpty(mode))
                mode = (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass)) ? "gam" : "none";

            ai.Mode = mode;
            ai.User = user;
            ai.Pass = pass;
            ai.LoginUrl = envLogin;
            return ai;
        }

        /// <summary>Heuristic — URL pattern matches the GAM login page.</summary>
        internal static bool LooksLikeGamLoginUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.IndexOf("glogin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("gamlogin", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
