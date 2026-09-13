using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    public class Configuration
    {
        // Configuration schema version is intentionally distinct from the MCP
        // response _meta.schemaVersion (mcp-axi/2).
        [JsonProperty("ConfigSchemaVersion")]
        public int? ConfigSchemaVersion { get; set; }

        [JsonProperty("GatewayMode")]
        public string? GatewayMode { get; set; }

        [JsonProperty("GeneXus")]
        public GeneXusConfig? GeneXus { get; set; }

        [JsonProperty("Server")]
        public ServerConfig? Server { get; set; }

        [JsonProperty("Logging")]
        public LoggingConfig? Logging { get; set; }

        [JsonProperty("Environment")]
        public EnvironmentConfig? Environment { get; set; }

        public static string? CurrentConfigPath { get; private set; }
        public static string ResolvedFrom { get; internal set; } = "launcher";

        internal static void SetCurrentConfigPathForTest(string? path)
        {
            CurrentConfigPath = path;
        }
        private static FileSystemWatcher? _watcher;
        // Debounce state for the config hot-reload, same pattern as tool_definitions.json
        // (McpRouter): editors fire multiple Changed events per save, so coalesce them into
        // one reload ~300ms after the last event. The lock serialises parse+dispatch so
        // handlers never run in parallel or duplicated.
        private static readonly object _reloadLock = new object();
        private static System.Threading.Timer? _reloadDebounceTimer;
        private static Configuration? _lastValidConfiguration;
        public static event Action<Configuration>? OnConfigurationChanged;

        // Gateway environment-variable matrix. Structural values belong in a strict
        // config file; only GX_CONFIG_PATH selects that file. Legacy transport and
        // presentation overrides remain available to non-strict documents. Secrets
        // and diagnostics are process concerns and are never copied into Configuration.
        //
        // Legacy overrides (strict permits only an exact match with the file): GX_MCP_PORT,
        // GX_MCP_STDIO. Structural (strict rejects): GXMCP_SHARED_GATEWAY,
        // GX_MCP_SHARED_GATEWAY, GXMCP_PROFILE,
        // GXMCP_NO_STRUCTURED_CONTENT, GXMCP_EMIT_STRUCTURED_CONTENT, GXMCP_TERSE.
        // Bootstrap selector (allowed): GX_CONFIG_PATH.
        // Secret (allowed): GXMCP_HTTP_TOKEN, GXMCP_AI_COMPLETE_KEY, GXMCP_GAM_PASS.
        // Diagnostic/operational (allowed): GXMCP_VERBOSE_LOGS, GXMCP_LOG_DIR,
        // GENEXUS_MCP_NO_UPDATE_CHECK, GENEXUS_MCP_NO_SELF_UPDATE,
        // GENEXUS_MCP_REAPPLY_TIMEOUT_MS, GXMCP_ASYNC_JOB_WATCHDOG_S,
        // GXMCP_LEGACY_TOOL_ALIASES, MCP_PERF_PROFILE, GXMCP_SERVER_VERSION,
        // GXMCP_ALLOW_CONCURRENT_BUILDS, GXMCP_BUILD_NOPROGRESS_SEC,
        // GXMCP_INPROCESS_BUILD_FASTPATH, GXMCP_REAP_ORPHAN_MSBUILD,
        // GXMCP_SEMANTIC_CACHE_MAX, PATH, PATHEXT, LOCALAPPDATA, and the
        // remaining worker/tool-specific variables forwarded or read outside config.

        public static Configuration Load()
        {
            if (CurrentConfigPath == null)
            {
                string? explicitConfigPath = global::System.Environment.GetEnvironmentVariable("GX_CONFIG_PATH");
                if (!string.IsNullOrWhiteSpace(explicitConfigPath))
                {
                    string fullPath = Path.GetFullPath(explicitConfigPath);
                    if (File.Exists(fullPath))
                    {
                        CurrentConfigPath = fullPath;
                        ResolvedFrom = "env";
                    }
                    else
                    {
                        throw new FileNotFoundException($"GX_CONFIG_PATH points to non-existent config file: {fullPath}", fullPath);
                    }
                }

                if (CurrentConfigPath == null)
                {
                    // Reliable path discovery: look for config.json starting from .exe up to root
                    string? currentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                    while (currentDir != null)
                    {
                        string check = Path.Combine(currentDir, "config.json");
                        if (File.Exists(check)) { CurrentConfigPath = check; ResolvedFrom = "launcher"; break; }
                        currentDir = Path.GetDirectoryName(currentDir);
                    }

                    if (CurrentConfigPath == null)
                    {
                        if (File.Exists("config.json"))
                        {
                            CurrentConfigPath = Path.GetFullPath("config.json");
                            ResolvedFrom = "cwd";
                        }
                        else
                        {
                            string userProfileDir = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile);
                            string userConfig = Path.Combine(userProfileDir, ".genexus-mcp", "config.json");
                            if (File.Exists(userConfig))
                            {
                                CurrentConfigPath = userConfig;
                                ResolvedFrom = "neutral";
                            }
                            else
                            {
                                throw new FileNotFoundException($"Could not find config.json in any parent directory or user profile (explicit GX_CONFIG_PATH '{explicitConfigPath}' was missing).");
                            }
                        }
                    }
                }
            }

            Program.Log($"[Gateway] Loading config from: {CurrentConfigPath}");
            var config = ParseConfig(CurrentConfigPath);
            Volatile.Write(ref _lastValidConfiguration, config);

            SetupWatcher(CurrentConfigPath);

            return config;
        }

        private static Configuration ParseConfig(string path)
        {
            // Retry logic for file locks
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var document = JObject.Parse(json);
                    bool strictDocument = document.Property("ConfigSchemaVersion") != null
                        || document.Property("GatewayMode") != null;
                    if (strictDocument)
                        ValidateStrictDocument(document, path);
                    // Tolerant parse (E6): a single invalid scalar (e.g. "HttpPort": "abc")
                    // must not take the whole gateway down. Log the offending member and
                    // keep every member that did deserialize.
                    Exception? strictDeserializationError = null;
                    var settings = new JsonSerializerSettings
                    {
                        Error = (sender, args) =>
                        {
                            if (strictDocument)
                            {
                                strictDeserializationError ??= new InvalidDataException($"Invalid value at '{args.ErrorContext.Path}' in strict config '{path}': {args.ErrorContext.Error.Message}", args.ErrorContext.Error);
                                args.ErrorContext.Handled = true;
                                return;
                            }
                            Program.Log($"[Gateway] WARNING: invalid config.json value at '{args.ErrorContext.Path}' ({args.ErrorContext.Error.Message}) — ignoring it and keeping the rest.");
                            args.ErrorContext.Handled = true;
                        }
                    };
                    if (strictDocument)
                        settings.MissingMemberHandling = MissingMemberHandling.Error;
                    var config = JsonConvert.DeserializeObject<Configuration>(json, settings);
                    if (strictDeserializationError != null)
                        throw strictDeserializationError;
                    if (config == null)
                    {
                        Program.Log("[Gateway] WARNING: config.json could not be deserialized into any usable state — using defaults.");
                        return new Configuration();
                    }

                    if (config.Environment != null)
                    {
                        config.Environment.RawDefaultKb = config.Environment.DefaultKb;
                        bool isLegacy = string.Equals(config.Environment.ResolutionPolicy, "legacy", StringComparison.OrdinalIgnoreCase);
                        if (isLegacy && string.IsNullOrWhiteSpace(config.Environment.DefaultKb))
                        {
                            if (!string.IsNullOrWhiteSpace(config.Environment.ActiveKb))
                            {
                                config.Environment.DefaultKb = config.Environment.ActiveKb;
                            }
                            else if (config.Environment.KBs != null && config.Environment.KBs.Count == 1)
                            {
                                config.Environment.DefaultKb = config.Environment.KBs[0].Alias;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(config.Environment?.KBPath))
                        Program.Log("[Gateway] WARNING: Environment.KBPath is missing in config.json!");
                    else 
                        Program.Log($"[Gateway] KB Path configured: {config.Environment.KBPath}");

                    string? portOverride = global::System.Environment.GetEnvironmentVariable("GX_MCP_PORT");
                    if (strictDocument)
                        RejectStrictStructuralEnvironment();
                    if (strictDocument && !string.IsNullOrWhiteSpace(portOverride))
                    {
                        if (!int.TryParse(portOverride, out int strictPortOverride))
                            throw new InvalidDataException("GX_MCP_PORT must be an integer when a strict configuration is used.");
                        if (strictPortOverride != config.Server!.HttpPort)
                            throw new InvalidDataException($"GX_MCP_PORT conflicts with strict Server.HttpPort ({config.Server.HttpPort}).");
                    }
                    else if (int.TryParse(portOverride, out int httpPortOverride) && httpPortOverride > 0)
                    {
                        config.Server ??= new ServerConfig();
                        config.Server.HttpPort = httpPortOverride;
                        Program.Log($"[Gateway] HTTP port overridden by GX_MCP_PORT={httpPortOverride}");
                    }

                    string? stdioOverride = global::System.Environment.GetEnvironmentVariable("GX_MCP_STDIO");
                    if (strictDocument && !string.IsNullOrWhiteSpace(stdioOverride))
                    {
                        if (!bool.TryParse(stdioOverride, out bool strictStdioOverride))
                            throw new InvalidDataException("GX_MCP_STDIO must be true or false when a strict configuration is used.");
                        if (strictStdioOverride != config.Server!.McpStdio)
                            throw new InvalidDataException($"GX_MCP_STDIO conflicts with strict Server.McpStdio ({config.Server.McpStdio}).");
                    }
                    else if (bool.TryParse(stdioOverride, out bool mcpStdioOverride))
                    {
                        config.Server ??= new ServerConfig();
                        config.Server.McpStdio = mcpStdioOverride;
                        Program.Log($"[Gateway] MCP stdio overridden by GX_MCP_STDIO={mcpStdioOverride}");
                    }

                    // Legacy migration: Environment.KBPath without KBs[] synthesises one entry.
                    // issue #28 item 6: only migrate when KBPath points at a REAL KB. The
                    // shipped fallback config carries a placeholder KBPath (C:\KBs\YourKB —
                    // an empty scaffold with no .gxw / KnowledgeBase.Connection). Migrating
                    // that synthesised a phantom `yourkb` DefaultKb that auto-opened alongside
                    // the user's real KB, producing "Multiple KBs open (yourkb,…); 'kb'
                    // required" on every call. Skipping the placeholder means the only open KB
                    // is the one the user actually opened, so no `kb` arg is needed.
                    if (config.Environment != null &&
                        (config.Environment.KBs == null || config.Environment.KBs.Count == 0) &&
                        !string.IsNullOrWhiteSpace(config.Environment.KBPath))
                    {
                        string legacyPath = config.Environment.KBPath!;
                        if (!LooksLikeKb(legacyPath))
                        {
                            Program.Log($"[Gateway] Legacy KBPath '{legacyPath}' is not a valid KB (missing / no .gxw / KnowledgeBase.Connection) — skipping auto-migration so no placeholder KB is opened. Open your KB with genexus_kb action=open path=<kbPath>, or declare it in config.Environment.KBs[].");
                        }
                        else
                        {
                            string alias = Path.GetFileName(legacyPath.TrimEnd('\\', '/')).ToLowerInvariant();
                            if (string.IsNullOrEmpty(alias)) alias = "default";
                            config.Environment.KBs = new List<KbEntry>
                            {
                                new KbEntry { Alias = alias, Path = legacyPath }
                            };
                            bool isLegacy = string.Equals(config.Environment.ResolutionPolicy, "legacy", StringComparison.OrdinalIgnoreCase);
                            if (isLegacy && string.IsNullOrWhiteSpace(config.Environment.DefaultKb))
                            {
                                config.Environment.DefaultKb = alias;
                            }
                            Program.Log($"[Gateway] Legacy KBPath migrated to KBs[{alias}], DefaultKb={config.Environment.DefaultKb}");
                        }
                    }

                    return config;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
            }
            throw new Exception("Could not read config.json after multiple attempts.");
        }

        private static void ValidateStrictDocument(JObject document, string path)
        {
            const int currentVersion = 2;
            var allowedRoot = new HashSet<string>(StringComparer.Ordinal)
            {
                "ConfigSchemaVersion", "GatewayMode", "GeneXus", "Server", "Logging", "Environment"
            };
            RejectUnknown(document, allowedRoot, "root", path);

            if (document["ConfigSchemaVersion"]?.Type != JTokenType.Integer
                || document.Value<int?>("ConfigSchemaVersion") != currentVersion)
                throw new InvalidDataException($"Unsupported or missing ConfigSchemaVersion in strict config '{path}'. Expected {currentVersion}.");

            string? mode = document.Value<string>("GatewayMode")?.Trim().ToLowerInvariant();
            if (mode != "stdio-isolated" && mode != "http-shared")
                throw new InvalidDataException("Strict config GatewayMode must be 'stdio-isolated' or 'http-shared'.");

            var geneXus = document["GeneXus"] as JObject
                ?? throw new InvalidDataException("Strict config requires a GeneXus object.");
            RejectUnknown(geneXus, new HashSet<string>(new[] { "InstallationPath", "WorkerExecutable" }, StringComparer.Ordinal), "GeneXus", path);
            RequireString(geneXus, "InstallationPath", "GeneXus");
            RequireString(geneXus, "WorkerExecutable", "GeneXus");

            var server = document["Server"] as JObject
                ?? throw new InvalidDataException("Strict config requires a Server object.");
            RejectUnknown(server, new HashSet<string>(new[]
            {
                "HttpPort", "McpStdio", "BindAddress", "AllowedOrigins", "SessionIdleTimeoutMinutes",
                "WorkerIdleTimeoutMinutes", "WedgedCommandTimeoutMinutes", "WorkerHeapRecycleMB",
                "ArtifactOutputDirectory", "IdempotencyTtlMinutes", "IdempotencyCacheSize", "BuildSyncThresholdSeconds", "MaxOpenKbs",
                "ToolProfile", "EmitStructuredContent", "TerseResponses"
            }, StringComparer.Ordinal), "Server", path);
            if (server["HttpPort"]?.Type != JTokenType.Integer || server["McpStdio"]?.Type != JTokenType.Boolean)
                throw new InvalidDataException("Strict config requires typed Server.HttpPort and Server.McpStdio.");
            int port = server.Value<int>("HttpPort");
            bool stdio = server.Value<bool>("McpStdio");
            if (mode == "stdio-isolated" && (port != 0 || !stdio))
                throw new InvalidDataException("stdio-isolated requires HttpPort=0 and McpStdio=true.");
            if (mode == "http-shared" && (port <= 0 || stdio))
                throw new InvalidDataException("http-shared requires HttpPort>0 and McpStdio=false.");

            if (document["Logging"] is JObject logging)
                RejectUnknown(logging, new HashSet<string>(new[] { "Level", "Path" }, StringComparer.Ordinal), "Logging", path);

            var environment = document["Environment"] as JObject
                ?? throw new InvalidDataException("Strict config requires an Environment object.");
            RejectUnknown(environment, new HashSet<string>(new[] { "ResolutionPolicy" }, StringComparer.Ordinal), "Environment", path);
            string? policy = environment.Value<string>("ResolutionPolicy")?.Trim().ToLowerInvariant();
            if (policy != "strict" && policy != "legacy")
                throw new InvalidDataException("Strict config ResolutionPolicy must be 'strict' or 'legacy'.");
        }

        private static void RejectStrictStructuralEnvironment()
        {
            // GX_MCP_PORT and GX_MCP_STDIO are the sole legacy overrides retained
            // for strict documents, and the caller below still rejects conflicts.
            string[] forbidden =
            {
                "GXMCP_SHARED_GATEWAY", "GX_MCP_SHARED_GATEWAY", "GXMCP_PROFILE",
                "GXMCP_NO_STRUCTURED_CONTENT", "GXMCP_EMIT_STRUCTURED_CONTENT", "GXMCP_TERSE"
            };
            foreach (string name in forbidden)
            {
                if (!string.IsNullOrWhiteSpace(global::System.Environment.GetEnvironmentVariable(name)))
                    throw new InvalidDataException($"{name} is a structural environment override and is not permitted with a strict configuration.");
            }
        }

        private static void RejectUnknown(JObject value, HashSet<string> allowed, string scope, string path)
        {
            var unknown = value.Properties().FirstOrDefault(p => !allowed.Contains(p.Name));
            if (unknown != null)
                throw new InvalidDataException($"Unknown member '{scope}.{unknown.Name}' in strict config '{path}'.");
        }

        private static void RequireString(JObject value, string name, string scope)
        {
            if (value[name]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(value.Value<string>(name)))
                throw new InvalidDataException($"Strict config requires non-empty {scope}.{name}.");
        }

        // issue #28 item 6: a path is real KB only if it exists and carries a .gxw
        // (or the legacy KnowledgeBase.Connection). Used to skip auto-migrating the
        // shipped placeholder KBPath into a phantom DefaultKb.
        internal static bool LooksLikeKb(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
                foreach (var f in Directory.EnumerateFiles(path))
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    if (name.EndsWith(".gxw") || name == "knowledgebase.connection") return true;
                }
            }
            catch { /* unreadable dir → treat as not-a-KB */ }
            return false;
        }

        // issue #38 defect #1: a path is openable as a KB when it is either the KB
        // folder (contains a .gxw or the legacy knowledgebase.connection) or a direct
        // .gxw/.gx file. Broader than LooksLikeKb (which only accepts a directory) so
        // callers that pass the .gxw file path directly aren't falsely rejected. This
        // is a cheap filesystem pre-check; the SDK remains the final authority on open.
        internal static bool IsPlausibleKbPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                if (File.Exists(path))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    return ext == ".gxw" || ext == ".gx";
                }
                return LooksLikeKb(path);
            }
            catch { return false; }
        }

        private static void SetupWatcher(string path)
        {
            if (_watcher != null) return;

            string dir = Path.GetDirectoryName(path)!;
            string file = Path.GetFileName(path);

            _watcher = new FileSystemWatcher(dir, file);
            _watcher.NotifyFilter = NotifyFilters.LastWrite;
            _watcher.Changed += (s, e) => {
                Program.Log($"[Gateway] Configuration file changed: {e.FullPath}");
                // Coalesce the editor's event burst into a single reload 300ms after the
                // last event (same debounce pattern as tool_definitions.json in McpRouter).
                // The timer callback takes _reloadLock so parse+dispatch are serialised:
                // handlers never run in parallel, and a half-written file never reaches them
                // (ParseConfig already retries briefly on IOException for the write lock).
                lock (_reloadLock)
                {
                    _reloadDebounceTimer?.Dispose();
                    _reloadDebounceTimer = new System.Threading.Timer(_ =>
                    {
                        try
                        {
                            Configuration newConfig;
                            lock (_reloadLock)
                            {
                                newConfig = ParseConfig(path);
                                Volatile.Write(ref _lastValidConfiguration, newConfig);
                                OnConfigurationChanged?.Invoke(newConfig);
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.Log($"[Gateway] Failed to reload configuration: {ex.Message}");
                        }
                    }, null, 300, Timeout.Infinite);
                }
            };
            _watcher.EnableRaisingEvents = true;
        }
    }

    public class GeneXusConfig
    {
        public string? InstallationPath { get; set; }
        public string? WorkerExecutable { get; set; }
    }

    public class ServerConfig
    {
        public string TransportMode { get; set; } = "legacy";
        public int HttpPort { get; set; } = 5000;
        public bool McpStdio { get; set; } = true;
        public bool SharedGateway { get; set; } = false;
        public string BindAddress { get; set; } = "127.0.0.1";
        public List<string> AllowedOrigins { get; set; } = new List<string>();
        public int SessionIdleTimeoutMinutes { get; set; } = 10;
        // Idle-reap window for a worker with no in-flight work. Raised from 5 to 60:
        // the worker's cold start is ~90s (95% of it the intrinsic, unshrinkable
        // GxServiceManager activation — see the worker's [COLD-START-BREAKDOWN] log and
        // docs), so reaping the sole warm worker after 5 idle minutes made the very next
        // tool call re-pay the full ~90s tax. 60 minutes keeps the worker warm across a
        // normal working session while still reclaiming a genuinely abandoned worker.
        // Set to 0 (or any value <= 0) to disable idle reaping entirely — the worker then
        // lives for the gateway's lifetime and is bounded only by MaxOpenKbs LRU eviction
        // and process exit on client disconnect.
        public int WorkerIdleTimeoutMinutes { get; set; } = 60;
        /// <summary>
        /// BUG-03: hard ceiling on how long a single in-flight command may sit
        /// unanswered before the worker is force-stopped as wedged. Deliberately
        /// generous — legitimate builds run for minutes — and clearly separate from
        /// the per-tool gateway operation timeout and the 45s "unresponsive" log
        /// warning in WorkerProcess. Only a command that has genuinely exceeded this
        /// ceiling triggers a reap; workers with no in-flight command are unaffected
        /// (idle reaping is governed solely by WorkerIdleTimeoutMinutes).
        /// </summary>
        public int WedgedCommandTimeoutMinutes { get; set; } = 15;
        // Proactive heap recycle: when an IDLE worker's working set exceeds this many MB, the
        // gateway recycles it (and eager-respawns a fresh warm one in the background) so a long
        // heavy session can't drift toward the x86 ~4GB ceiling / a fragmented, unstable heap.
        // Measured baseline is small (~130MB small KB, ~158MB for 38k objects), so 1500 only
        // trips after sustained heavy work — the same threshold as the whoami reload hint.
        // Only fires when the worker is idle (no in-flight/queued work), so it never interrupts
        // an active operation. Set to 0 to disable.
        public int WorkerHeapRecycleMB { get; set; } = 1500;
        /// <summary>
        /// Optional root for generated wiki and visualizer files. The Worker always adds a
        /// stable per-KB scope below this root. When omitted, artifacts go to the durable
        /// %LOCALAPPDATA%\\GxMcp\\Artifacts root instead of the installed Worker directory.
        /// </summary>
        public string? ArtifactOutputDirectory { get; set; }
        public int IdempotencyTtlMinutes { get; set; } = 15;
        public int IdempotencyCacheSize { get; set; } = 1000;
        /// <summary>
        /// Lifecycle build requests whose estimated_seconds is below this threshold are
        /// executed synchronously (fast-path) and return the build result directly.
        /// Requests at or above the threshold are dispatched asynchronously and return a
        /// job_id immediately. Default: 20 seconds.
        /// </summary>
        public int BuildSyncThresholdSeconds { get; set; } = 20;
        /// <summary>
        /// Maximum number of KBs that may be open simultaneously. Each KB runs in a
        /// dedicated Worker process. When exceeded, the oldest idle Worker is evicted
        /// (LRU); if all are busy, the request fails with KB_POOL_FULL.
        /// </summary>
        public int MaxOpenKbs { get; set; } = 3;
        /// <summary>
        /// Active tool profile to gate tool surface (all, core, authoring, devops, ui, db).
        /// Can be overridden via GXMCP_PROFILE environment variable. Default: all.
        /// </summary>
        public string ToolProfile { get; set; } = "all";
        /// <summary>
        /// Emit the MCP-standard `structuredContent` field on tool results. It duplicates
        /// the entire JSON payload already serialized in `content[0].text`, roughly
        /// doubling every tool response's byte size (measured: +54-60% across tools).
        /// LLM clients read `content[0].text`; only structured-output consumers need this.
        /// Default: true (protocol-compliant). Set false for lean responses — the biggest
        /// single per-turn token/latency win available in the gateway. Can be forced via
        /// GXMCP_NO_STRUCTURED_CONTENT=1 without touching config files.
        /// </summary>
        public bool EmitStructuredContent { get; set; } = true;
        /// <summary>
        /// Terse mode: strip the per-response UX sugar the LLM does not strictly need —
        /// `next_legal_actions`, `_meta.tokens` (the used/limit block), and SQL-dialect
        /// nudges — and keep only the payload itself plus error hints. Saves ~200-600
        /// bytes per response on top of EmitStructuredContent=false. Default: false
        /// (full UX). Can be forced via GXMCP_TERSE=1 without touching config files.
        /// </summary>
        public bool TerseResponses { get; set; } = false;
    }

    public class LoggingConfig
    {
        public string? Level { get; set; }
        public string? Path { get; set; }
    }

    public class EnvironmentConfig
    {
        public string? KBPath { get; set; }
        public string? GX_SHADOW_PATH { get; set; }
        public string? DefaultKb { get; set; }
        public string? RawDefaultKb { get; set; }
        public string ResolutionPolicy { get; set; } = "strict";
        // Alias written by the Node CLI (cli/lib/config.js writeKbCatalog) —
        // ParseConfig promotes it to DefaultKb after deserialize if DefaultKb is empty.
        public string? ActiveKb { get; set; }
        [JsonConverter(typeof(KbCatalogConverter))]
        public List<KbEntry> KBs { get; set; } = new List<KbEntry>();
    }

    public class KbEntry
    {
        public string Alias { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    // Accepts both schemas the codebase writes for Environment.KBs:
    //   - List shape (Gateway-native):   [ { "Alias": "x", "Path": "..." }, ... ]
    //   - Dict shape (Node CLI writes):  { "x": "...", "y": "..." }
    // Without this, the CLI's multi-KB output crashes the Gateway on load.
    internal class KbCatalogConverter : JsonConverter<List<KbEntry>>
    {
        public override List<KbEntry> ReadJson(JsonReader reader, Type objectType, List<KbEntry>? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return new List<KbEntry>();

            if (reader.TokenType == JsonToken.StartArray)
            {
                var list = new List<KbEntry>();
                serializer.Populate(reader, list);
                return list;
            }

            if (reader.TokenType == JsonToken.StartObject)
            {
                var dict = serializer.Deserialize<Dictionary<string, string>>(reader) ?? new Dictionary<string, string>();
                var list = new List<KbEntry>(dict.Count);
                foreach (var kv in dict)
                {
                    list.Add(new KbEntry { Alias = kv.Key, Path = kv.Value });
                }
                return list;
            }

            throw new JsonSerializationException($"Environment.KBs must be an array or object, got {reader.TokenType}.");
        }

        public override void WriteJson(JsonWriter writer, List<KbEntry>? value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, value);
        }
    }
}
