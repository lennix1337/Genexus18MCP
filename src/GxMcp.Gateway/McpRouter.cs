using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway
{
    internal sealed class McpRouterError
    {
        public McpRouterError(int code, string message, JObject? data = null)
        {
            Code = code;
            Message = message;
            Data = data;
        }

        public int Code { get; }
        public string Message { get; }
        public JObject? Data { get; }
    }

    public class McpRouter
    {
        public static readonly string ServerVersion = ResolveServerVersion();
        // Keep the legacy default for initialize-based clients while also serving
        // the sessionless per-request metadata protocol. A client that explicitly
        // asks for the modern revision is negotiated onto it; old clients continue
        // to receive the 2025-11-25 handshake shape.
        public const string ModernProtocolVersion = "2026-07-28";
        public const string SupportedProtocolVersion = "2025-11-25";

        private static string ResolveServerVersion()
        {
            // Prefer the InformationalVersion (set in the csproj via <InformationalVersion>),
            // fall back to FileVersion, then AssemblyVersion. release.ps1 keeps the csproj
            // in sync with package.json so this surface always matches the published build.
            var asm = Assembly.GetExecutingAssembly();
            try
            {
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info.IndexOf('+');
                    return plus > 0 ? info.Substring(0, plus) : info;
                }
                var file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                if (!string.IsNullOrWhiteSpace(file)) return file;
                var name = asm.GetName().Version;
                if (name != null) return name.ToString(3);
            }
            catch { }
            return "0.0.0";
        }

        private static readonly string[] _objectParts = { "Source", "Rules", "Events", "Variables", "Structure", "Layout", "WebForm", "PatternInstance", "PatternVirtual" };
        private static readonly string[] _analysisIncludes = { "metadata", "variables", "signature", "structure" };
        private static readonly string[] _targetLanguages = { "CSharp", "TypeScript", "Java", "Python" };
        private static readonly string[] _visualSurfaces = { "Layout", "WebForm", "PatternInstance", "PatternVirtual" };
        private static readonly IReadOnlyDictionary<string, PromptDefinition> _promptDefinitions = BuildPromptDefinitions();
        private static readonly string[] _promptNames = _promptDefinitions.Keys.ToArray();
        private static readonly List<IMcpModuleRouter> _routers;
        private static JArray _toolDefinitions = new JArray();
        private static JObject? _cachedToolsListResponse;
        private static readonly object _cachedResourcesListResponse = BuildResourcesListResponse();
        private static readonly object _cachedResourceTemplatesListResponse = BuildResourceTemplatesListResponse();
        private static readonly object _cachedPromptsListResponse = new
        {
            resultType = "complete",
            prompts = BuildPromptCatalog(),
            ttlMs = 3600000,
            cacheScope = "public"
        };
        // PERFORMANCE (G-B3): hot-reload tool_definitions.json without restarting the gateway.
        // The watcher is kept in a static field so it is rooted for the lifetime of the process.
        // Debounced with a System.Threading.Timer because editors (e.g. VS Code) often fire
        // multiple Changed events for a single save.
        private static FileSystemWatcher? _toolDefinitionsWatcher;
        private static System.Threading.Timer? _toolDefinitionsReloadTimer;
        private static readonly object _toolDefinitionsReloadLock = new object();

        private sealed class PromptArgumentDefinition
        {
            public PromptArgumentDefinition(string name, string description, bool required, params string[] allowedValues)
            {
                Name = name;
                Description = description;
                Required = required;
                AllowedValues = allowedValues?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? Array.Empty<string>();
            }

            public string Name { get; }
            public string Description { get; }
            public bool Required { get; }
            public string[] AllowedValues { get; }
        }

        private sealed class PromptDefinition
        {
            public PromptDefinition(string name, string description, Func<JObject, string> buildMessage, params PromptArgumentDefinition[] arguments)
            {
                Name = name;
                Description = description;
                BuildMessage = buildMessage;
                Arguments = arguments ?? Array.Empty<PromptArgumentDefinition>();
            }

            public string Name { get; }
            public string Description { get; }
            public PromptArgumentDefinition[] Arguments { get; }
            public Func<JObject, string> BuildMessage { get; }
        }

        static McpRouter()
        {
            _routers = new List<IMcpModuleRouter>
            {
                new SearchRouter(),
                new ObjectRouter(),
                new AnalyzeRouter(),
                new SystemRouter(),
                new OperationsRouter(),
                // Wave-3 doc-flagged long-term / speculative items. Schema only;
                // every tool dispatches to FutureItemStub.Deferred in the worker.
                new FutureItemRouter()
            };

            LoadToolDefinitions();
            SetupToolDefinitionsWatcher();
            AssertNoDuplicateRouterCoverage();
        }

        // v2.6.6 router-dup guard: each tool must be claimed by AT MOST one router.
        // The router list is iterated in order and the first non-null return wins, so
        // a duplicate doesn't fail at runtime — it silently strips fields that the
        // losing router would have forwarded. The genexus_history live bug (Stream H
        // forwarded `discard`/`part`/`snapshot` in OperationsRouter but SystemRouter
        // had a legacy duplicate that ran first and dropped them) cost us a release
        // pass. Detecting at startup turns the bug class into a fail-fast.
        private static void AssertNoDuplicateRouterCoverage()
        {
            try
            {
                if (_toolDefinitions == null || _toolDefinitions.Count == 0) return;
                var duplicates = new List<string>();
                foreach (var def in _toolDefinitions.OfType<JObject>())
                {
                    string toolName = def["name"]?.ToString();
                    if (string.IsNullOrEmpty(toolName)) continue;
                    int hits = 0;
                    var claimers = new List<string>();
                    foreach (var router in _routers)
                    {
                        try
                        {
                            // empty JObject probe — safer than null; routers that gate on
                            // required args will return null without throwing.
                            object result = router.ConvertToolCall(toolName, new JObject());
                            if (result != null) { hits++; claimers.Add(router.GetType().Name); }
                        }
                        catch { /* throwing router doesn't claim the tool */ }
                    }
                    if (hits > 1)
                    {
                        duplicates.Add(toolName + " → " + string.Join(", ", claimers));
                    }
                }
                if (duplicates.Count > 0)
                {
                    string msg = "[McpRouter] FATAL: duplicate router coverage detected. "
                               + "Each tool must be claimed by exactly one router. Offenders: "
                               + string.Join(" | ", duplicates);
                    Program.Log(msg);
                    throw new InvalidOperationException(msg);
                }
                Program.Log($"[McpRouter] Router-dup guard OK ({_toolDefinitions.Count} tools, {_routers.Count} routers).");
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                Program.Log("[McpRouter] router-dup guard self-check failed: " + ex.Message);
            }
        }

        private static void LoadToolDefinitions()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string defPath = Path.Combine(exeDir, "tool_definitions.json");
                if (File.Exists(defPath))
                {
                    string json = File.ReadAllText(defPath);
                    var parsed = JArray.Parse(json);
                    ToolSchemaCompatibility.Apply(parsed);
                    // MCP clients cache tools/list aggressively; deterministic ordering
                    // keeps discovery diffs stable and avoids model-visible churn when
                    // the source JSON is edited in a different order.
                    _toolDefinitions = new JArray(parsed.OfType<JObject>()
                        .OrderBy(definition => definition["name"]?.ToString() ?? string.Empty, StringComparer.Ordinal));
                    _cachedToolsListResponse = new JObject
                    {
                        ["resultType"] = "complete",
                        ["tools"] = _toolDefinitions,
                        ["ttlMs"] = 3600000,
                        ["cacheScope"] = "public"
                    };
                    Program.Log($"[McpRouter] Loaded {_toolDefinitions.Count} tool definitions from JSON.");
                }
                else
                {
                    Program.Log($"[McpRouter] ERROR: tool_definitions.json not found at {defPath}");
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[McpRouter] ERROR loading tool definitions: {ex.Message}");
            }
        }

        private static void SetupToolDefinitionsWatcher()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return;

                _toolDefinitionsWatcher = new FileSystemWatcher(exeDir, "tool_definitions.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };

                FileSystemEventHandler onChange = (_, __) =>
                {
                    // PERFORMANCE (G-B3): coalesce a burst of events into a single reload 500ms later.
                    lock (_toolDefinitionsReloadLock)
                    {
                        _toolDefinitionsReloadTimer?.Dispose();
                        _toolDefinitionsReloadTimer = new System.Threading.Timer(_ =>
                        {
                            try
                            {
                                LoadToolDefinitions();
                            }
                            catch (Exception ex)
                            {
                                Program.Log($"[McpRouter] tool_definitions reload failed: {ex.Message}");
                            }
                        }, null, 500, System.Threading.Timeout.Infinite);
                    }
                };

                _toolDefinitionsWatcher.Changed += onChange;
                _toolDefinitionsWatcher.Created += onChange;
                _toolDefinitionsWatcher.Renamed += (_, e) => onChange(_, e);
                _toolDefinitionsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Program.Log($"[McpRouter] FileSystemWatcher setup failed: {ex.Message}");
            }
        }

        public const string McpAxiSchemaVersion = "mcp-axi/2";

        // Fix 6d: known MCP protocol versions this server supports (oldest → newest).
        public static readonly string[] KnownProtocolVersions =
        {
            "2024-11-05",
            "2025-03-26",
            "2025-06-18",
            "2025-11-25",
            ModernProtocolVersion
        };

        internal static bool IsModernProtocolVersion(string? version)
        {
            return string.Equals(version, ModernProtocolVersion, StringComparison.Ordinal);
        }

        internal static string? GetRequestProtocolVersion(JObject request)
        {
            var parameters = request["params"] as JObject;
            return parameters?["protocolVersion"]?.ToString()
                ?? (parameters?["_meta"] as JObject)?["io.modelcontextprotocol/protocolVersion"]?.ToString();
        }

        internal static bool IsModernRequest(JObject request)
        {
            return IsModernProtocolVersion(GetRequestProtocolVersion(request));
        }

        internal static string NegotiateProtocolVersion(string? clientRequestedVersion)
        {
            return !string.IsNullOrEmpty(clientRequestedVersion)
                && Array.IndexOf(KnownProtocolVersions, clientRequestedVersion) >= 0
                ? clientRequestedVersion
                : SupportedProtocolVersion;
        }

        private static JObject BuildInitializeResponse(string? clientRequestedVersion = null)
        {
            // Echo the client's requested version if it is one we support; otherwise
            // keep initialize-based clients on the legacy session protocol. The modern
            // HTTP revision is discovered through server/discover instead.
            string negotiatedVersion = NegotiateProtocolVersion(clientRequestedVersion);

            var removed = new JArray();
            foreach (var kvp in RemovedToolsRegistry.Map)
            {
                removed.Add(new JObject
                {
                    ["name"] = kvp.Key,
                    ["replacedBy"] = kvp.Value.ReplacedBy,
                    ["argHint"] = kvp.Value.ArgHint
                });
            }

            return new JObject
            {
                ["protocolVersion"] = negotiatedVersion,
                ["capabilities"] = new JObject
                {
                    ["prompts"] = new JObject { ["listChanged"] = false },
                    ["tools"] = new JObject { ["listChanged"] = true },
                    ["resources"] = new JObject { ["listChanged"] = true, ["subscribe"] = true },
                    ["completion"] = new JObject()
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = "genexus-mcp-server",
                    ["version"] = ServerVersion
                },
                ["_meta"] = new JObject
                {
                    ["schemaVersion"] = McpAxiSchemaVersion,
                    ["removedTools"] = removed
                }
            };
        }

        private static JObject BuildServerDiscoverResponse()
        {
            return new JObject
            {
                ["resultType"] = "complete",
                ["supportedVersions"] = new JArray(KnownProtocolVersions),
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = true },
                    ["resources"] = new JObject { ["listChanged"] = true, ["subscribe"] = true },
                    ["prompts"] = new JObject { ["listChanged"] = false },
                    ["completion"] = new JObject(),
                    ["extensions"] = new JObject
                    {
                        ["io.modelcontextprotocol/tasks"] = new JObject()
                    }
                },
                ["_meta"] = new JObject
                {
                    ["io.modelcontextprotocol/serverInfo"] = new JObject
                    {
                        ["name"] = "genexus-mcp-server",
                        ["version"] = ServerVersion
                    }
                },
                ["instructions"] = "Use genexus_whoami first, then discover and operate on the active GeneXus Knowledge Base with the narrowest read or write tool that fits.",
                ["ttlMs"] = 3600000,
                ["cacheScope"] = "public"
            };
        }

        public static object? Handle(JObject request)
        {
            string? method = request["method"]?.ToString();
            switch (method)
            {
                case "initialize":
                    {
                        // Modern protocol revisions replaced the initialize/session
                        // handshake with per-request metadata and server/discover.
                        // Returning null lets the normal JSON-RPC method-not-found path
                        // produce the deterministic stdio error; HTTP applies its 404
                        // binding-specific status before dispatching here.
                        if (IsModernRequest(request)) return null;
                        string? clientVersion = (request["params"] as JObject)?["protocolVersion"]?.ToString();
                        return BuildInitializeResponse(clientVersion);
                    }
                case "server/discover":
                    return BuildServerDiscoverResponse();
                case "tools/list":
                    {
                        string activeProfile = ToolProfileFilter.ResolveActiveProfile(Program.ActiveConfig?.Server?.ToolProfile);
                        if (string.IsNullOrEmpty(activeProfile) || activeProfile == "all")
                        {
                            return _cachedToolsListResponse ?? new JObject
                            {
                                ["resultType"] = "complete",
                                ["tools"] = _toolDefinitions,
                                ["ttlMs"] = 3600000,
                                ["cacheScope"] = "public"
                            };
                        }
                        return new JObject
                        {
                            ["resultType"] = "complete",
                            ["tools"] = ToolProfileFilter.GetOrCreateFiltered(_toolDefinitions, activeProfile),
                            ["profile"] = activeProfile,
                            ["ttlMs"] = 3600000,
                            ["cacheScope"] = "public"
                        };
                    }
                case "resources/list":
                    return _cachedResourcesListResponse;
                case "resources/read":
                    return BuildStaticResourceResponse(request);
                case "resources/templates/list":
                    return _cachedResourceTemplatesListResponse;
                case "resources/subscribe":
                    {
                        var uri = (request["params"] as JObject)?["uri"]?.ToString();
                        return new
                        {
                            resultType = "complete",
                            subscribed = true,
                            uri = uri
                        };
                    }
                case "resources/unsubscribe":
                    {
                        var uri = (request["params"] as JObject)?["uri"]?.ToString();
                        return new
                        {
                            resultType = "complete",
                            subscribed = false,
                            uri = uri
                        };
                    }
                case "completion/complete":
                    return HandleCompletion(request);
                case "prompts/list":
                    return _cachedPromptsListResponse;
                case "prompts/get":
                    return BuildPromptResponse(request);
                case "ping":
                    return new { resultType = "complete" };
                default:
                    return null;
            }
        }

        private static object HandleCompletion(JObject request)
        {
            var paramsObj = request["params"] as JObject;
            var argument = paramsObj?["argument"] as JObject;
            string argumentName = argument?["name"]?.ToString() ?? "";
            string currentValue = argument?["value"]?.ToString() ?? "";
            string refType = paramsObj?["ref"]?["type"]?.ToString() ?? "";
            string refName = paramsObj?["ref"]?["name"]?.ToString() ?? "";
            string uriTemplate = paramsObj?["ref"]?["uriTemplate"]?.ToString() ?? "";

            IEnumerable<string> values = Enumerable.Empty<string>();

            if (argumentName == "part")
            {
                values = _objectParts;
            }
            // v2.8.0 (S1) — autocomplete object names from the cached index.
            // 'name' / 'target' / 'targets' all carry object references in
            // various tools; offer the same shortlist. Falls back to empty
            // when the index hasn't warmed yet.
            else if (argumentName == "name" || argumentName == "target" || argumentName == "targets")
            {
                // Plan 038: completion/complete runs outside ProcessMcpRequest's per-tool
                // dispatch, but _currentKb is still resolved for this request by the time
                // McpRouter.Handle runs (set earlier in ProcessMcpRequest) — reuse it so
                // suggestions come from the right KB's name→type map.
                string? kbAlias = Program.GetCurrentKb()?.NormalizedAlias;
                values = string.IsNullOrEmpty(kbAlias)
                    ? Enumerable.Empty<string>()
                    : AutoTypeInjector.CompleteName(kbAlias!, currentValue, cap: 25);
            }
            else if (argumentName == "language" || argumentName == "targetLanguage")
            {
                values = _targetLanguages;
            }
            else if (argumentName == "include")
            {
                values = _analysisIncludes;
            }
            else if (argumentName == "prompt")
            {
                values = _promptNames;
            }
            else if (refType == "ref/resource")
            {
                if (uriTemplate.Contains("/part/{part}", StringComparison.OrdinalIgnoreCase))
                    values = _objectParts;
                else if (uriTemplate.Contains("/conversion-context", StringComparison.OrdinalIgnoreCase))
                    values = _analysisIncludes;
            }
            else if (refType == "ref/prompt" && TryGetPromptArgumentDefinition(refName, argumentName, out var promptArgument))
            {
                values = promptArgument.AllowedValues;
            }
            else if (refType == "ref/tool")
            {
                if (refName == "genexus_read")
                    values = _objectParts;
                else if (refName == "genexus_inspect")
                    values = _analysisIncludes;
                else if (refName == "genexus_forge")
                    values = _targetLanguages;
                else if (refName == "genexus_lifecycle")
                    values = new[] { "build", "build_all", "rebuild", "reorg", "validate", "sync", "index", "status", "result" };
                else if (refName == "genexus_properties")
                    values = new[] { "get", "set", "move" };
                else if (refName == "genexus_asset")
                    values = new[] { "find", "read", "write" };
                else if (refName == "genexus_history")
                    values = new[] { "list", "get_source", "save", "restore" };
                else if (refName == "genexus_structure")
                    values = new[] { "get_visual", "update_visual", "get_indexes", "get_logic", "move_attribute" };
                else if (refName == "genexus_refactor")
                    values = new[] { "RenameAttribute", "RenameVariable", "RenameObject", "ExtractProcedure" };
                else if (refName == "prompts/get")
                    values = _promptNames;
            }

            var filteredValues = values
                .Where(value => value.StartsWith(currentValue, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => new { value })
                .ToArray();

            return new
            {
                resultType = "complete",
                completion = new
                {
                    values = filteredValues
                }
            };
        }

        private static object BuildResourcesListResponse()
        {
            var baseResources = new List<object>
            {
                new { uri = "genexus://kb/index-status", name = "KB Index Status", description = "Current indexing status for the active Knowledge Base." },
                new { uri = "genexus://kb/health", name = "Gateway Health Report", description = "Health report for the GeneXus MCP worker and gateway." },
                new { uri = "genexus://kb/capabilities", name = "GeneXus SDK Capabilities", description = "Capability evidence for the active KB and installed GeneXus SDK; availability is queried explicitly and is not hidden in tools/list." },
                new { uri = "genexus://kb/agent-playbook", name = "GeneXus Agent Playbook", description = "Recommended MCP workflow to operate this GeneXus server in an agent-native, Git-friendly way." },
                new { uri = "genexus://kb/llm-playbook", name = "LLM CLI+MCP Playbook", description = "Protocol-first guide for choosing CLI vs MCP, token-efficient calls, and timeout/lifecycle handling." },
                new { uri = "genexus://objects", name = "GeneXus Objects Index", description = "Browsable index of all objects in the KB." },
                new { uri = "genexus://attributes", name = "GeneXus Attributes", description = "Browsable list of all attributes." }
            };
            foreach (var skill in SkillCatalog.All)
            {
                baseResources.Add(new
                {
                    uri = "genexus://kb/skills/" + skill.Key,
                    name = skill.Title,
                    description = skill.Description,
                    mimeType = "text/markdown"
                });
            }
            return new
            {
                resultType = "complete",
                resources = baseResources,
                ttlMs = 3600000,
                cacheScope = "public"
            };
        }

        private static object BuildResourceTemplatesListResponse()
        {
            return new
            {
                resultType = "complete",
                resourceTemplates = new[]
                {
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/part/{part}",
                        name = "GeneXus Object Part",
                        description = "Read a specific part of a GeneXus object such as Source, Rules, Events, Variables, Structure, or Layout."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/variables",
                        name = "GeneXus Object Variables",
                        description = "Read the variable declarations for a GeneXus object."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/navigation",
                        name = "GeneXus Navigation",
                        description = "Read the navigation analysis for a GeneXus object."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/hierarchy",
                        name = "GeneXus Hierarchy",
                        description = "Read the dependency hierarchy for a GeneXus object."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/data-context",
                        name = "GeneXus Data Context",
                        description = "Read attributes, variables, and inferred data context for a GeneXus object."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/ui-context",
                        name = "GeneXus UI Context",
                        description = "Read UI structure and controls for a GeneXus object."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/conversion-context",
                        name = "GeneXus Conversion Context",
                        description = "Read consolidated conversion context for a GeneXus object."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/pattern-metadata",
                        name = "GeneXus Pattern Metadata",
                        description = "Read pattern metadata detected for a GeneXus object."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/summary",
                        name = "GeneXus Object Summary",
                        description = "Read an LLM-oriented summary for a GeneXus object."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/indexes",
                        name = "GeneXus Visual Indexes",
                        description = "Read visual indexes for a Transaction or Table."
                    },
                    new
                    {
                        uriTemplate = "genexus://objects/{name}/logic-structure",
                        name = "GeneXus Logic Structure",
                        description = "Read the logical structure for a Transaction or Table."
                    },
                    new
                    {
                        uriTemplate = "genexus://attributes/{name}",
                        name = "GeneXus Attribute Metadata",
                        description = "Read metadata for a specific GeneXus attribute."
                    },
                    new
                    {
                        uriTemplate = "genexus://kb/tool-help/{name}",
                        name = "GeneXus Tool Help",
                        description = "Long-form help for a single MCP tool: prefixes, modes, examples, defaults."
                    }
                },
                ttlMs = 3600000,
                cacheScope = "public"
            };
        }

        private static object[] BuildPromptCatalog()
        {
            return _promptDefinitions.Values
                .Select(prompt => new
                {
                    name = prompt.Name,
                    description = prompt.Description,
                    arguments = prompt.Arguments.Select(argument => new
                    {
                        name = argument.Name,
                        description = argument.Description,
                        required = argument.Required,
                        allowedValues = argument.AllowedValues.Length > 0 ? argument.AllowedValues : null
                    }).ToArray()
                })
                .Cast<object>()
                .ToArray();
        }

        private static object BuildPromptResponse(JObject request)
        {
            var paramsObj = request["params"] as JObject;
            string promptName = paramsObj?["name"]?.ToString() ?? "";
            var args = paramsObj?["arguments"] as JObject ?? new JObject();
            if (!_promptDefinitions.TryGetValue(promptName, out var prompt))
            {
                return new McpRouterError(
                    -32602,
                    $"Prompt '{promptName}' is not defined by this server.",
                    new JObject { ["prompt"] = promptName });
            }

            string? validationError = ValidatePromptArguments(prompt, args);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return new McpRouterError(
                    -32602,
                    validationError,
                    new JObject { ["prompt"] = prompt.Name });
            }

            return new
            {
                resultType = "complete",
                description = prompt.Description,
                messages = new[]
                {
                    CreatePromptMessage(prompt.BuildMessage(args))
                }
            };
        }

        private static object CreatePromptMessage(string text)
        {
            return new
            {
                role = "user",
                content = new
                {
                    type = "text",
                    text
                }
            };
        }

        private static string BuildExplainObjectPrompt(string name, string part)
        {
            return
                $"Explain the GeneXus object '{name}'. " +
                $"Start from resource 'genexus://objects/{name}/part/{part}', then use 'genexus://objects/{name}/variables', " +
                $"'genexus://objects/{name}/navigation', and 'genexus://objects/{name}/summary'. " +
                "Summarize purpose, data flow, external dependencies, and risky assumptions. " +
                "If important context is missing, say exactly which additional resource should be read next.";
        }

        private static string BuildConvertObjectPrompt(string name, string targetLanguage)
        {
            return
                $"Prepare the GeneXus object '{name}' for conversion to {targetLanguage}. " +
                $"Read 'genexus://objects/{name}/conversion-context', 'genexus://objects/{name}/variables', " +
                $"'genexus://objects/{name}/navigation', and 'genexus://objects/{name}/summary' first. " +
                "Produce: semantic summary, target architecture assumptions, unsupported features, manual review items, and a translation plan. " +
                "Do not invent framework behavior that is not grounded in the retrieved context.";
        }

        private static string BuildReviewTransactionPrompt(string name)
        {
            return
                $"Review the Transaction '{name}'. " +
                $"Read 'genexus://objects/{name}/part/Structure', 'genexus://objects/{name}/part/Rules', " +
                $"'genexus://objects/{name}/data-context', and 'genexus://objects/{name}/summary'. " +
                "Focus on data integrity, inferred business rules, side effects, and migration risks. " +
                "Report findings first, then open questions, then recommended changes.";
        }

        private static string BuildRefactorProcedurePrompt(string name)
        {
            return
                $"Refactor the Procedure '{name}' without changing behavior. " +
                $"Read 'genexus://objects/{name}/part/Source', 'genexus://objects/{name}/variables', " +
                $"'genexus://objects/{name}/navigation', and 'genexus://objects/{name}/summary'. " +
                "Identify duplicated logic, implicit dependencies, and extraction opportunities. " +
                "Return a stepwise refactor plan before proposing code changes.";
        }

        private static string BuildGenerateTestsPrompt(string name)
        {
            return
                $"Generate a test plan for the GeneXus object '{name}'. " +
                $"Ground the analysis in 'genexus://objects/{name}/summary', 'genexus://objects/{name}/variables', " +
                $"'genexus://objects/{name}/navigation', and the primary source part under 'genexus://objects/{name}/part/Source'. " +
                "List normal cases, edge cases, integration dependencies, and regression risks. " +
                "Prefer deterministic assertions over vague behavioral checks.";
        }

        private static string BuildTraceDependenciesPrompt(string name)
        {
            return
                $"Trace dependencies for the GeneXus object '{name}'. " +
                $"Use 'genexus://objects/{name}/hierarchy', 'genexus://objects/{name}/navigation', " +
                $"'genexus://objects/{name}/summary', and if needed 'genexus_query' with 'usedby:{name}'. " +
                "Separate direct dependencies, indirect dependencies, and likely impact zones. " +
                "Call out where the trace is inferred versus explicitly grounded in retrieved data.";
        }

        private static string BuildAgentShipChangePrompt(string goal, string objectName, string part)
        {
            string normalizedPart = string.IsNullOrWhiteSpace(part) ? "Source" : part;
            string objectSpecificGuidance = string.IsNullOrWhiteSpace(objectName)
                ? "Start with `genexus_query` and the KB-level resources to identify the smallest object set involved before editing anything. "
                : $"Treat '{objectName}' as the primary object. Read 'genexus://objects/{objectName}/summary', 'genexus://objects/{objectName}/part/{normalizedPart}', 'genexus://objects/{objectName}/variables', and 'genexus://objects/{objectName}/hierarchy' before proposing edits. ";

            return
                $"Execute a controlled GeneXus change with the goal '{goal}'. " +
                "Start by reading 'genexus://kb/agent-playbook'. " +
                objectSpecificGuidance +
                "Use MCP discovery instead of hardcoded assumptions, keep the blast radius explicit, and prefer the smallest reversible change set. " +
                "If editing is required, re-read the exact target before mutation, persist the change, then verify with a re-read plus the appropriate lifecycle command (`validate`, `build`, or `test`). " +
                "Finish with a Git-ready change summary listing modified objects, verification evidence, and open risks.";
        }

        private static string BuildVisualChangePrompt(string name, string changeGoal, string preferredSurface)
        {
            string normalizedSurface = string.IsNullOrWhiteSpace(preferredSurface) ? "PatternInstance" : preferredSurface;
            return
                $"Plan and validate a GeneXus visual metadata change for '{name}' with the goal '{changeGoal}'. " +
                "Start by reading 'genexus://kb/agent-playbook'. " +
                $"Inspect 'genexus://objects/{name}/ui-context', 'genexus://objects/{name}/pattern-metadata', and 'genexus://objects/{name}/part/{normalizedSurface}' first. " +
                "Determine the authoritative surface before editing: base layout, raw WebForm metadata, or pattern-owned metadata. " +
                "If assets are involved, inspect `genexus_asset` metadata before changing any binary file. " +
                "After the write, re-read the exact same surface and report whether persistence is confirmed or still blocked.";
        }

        private static string BuildBootstrapLlmPrompt(string goal)
        {
            string goalHint = string.IsNullOrWhiteSpace(goal)
                ? "If the user goal is unknown, ask one concise clarifying question before editing."
                : $"User goal: '{goal}'. Prioritize next calls for this goal.";

            return
                "Bootstrap this GeneXus MCP session in protocol-first mode. " +
                "Start with discovery (`tools/list`, `resources/list`, `prompts/list`). " +
                "Read `genexus://kb/llm-playbook` and summarize: when to use AXI CLI vs MCP, pagination/field-shaping defaults, and timeout follow-up via `genexus_lifecycle(op:<operationId>)`. " +
                $"{goalHint} " +
                "Then propose the next 3 deterministic calls with explicit arguments.";
        }

        private static IReadOnlyDictionary<string, PromptDefinition> BuildPromptDefinitions()
        {
            var prompts = new[]
            {
                new PromptDefinition(
                    "gx_bootstrap_llm",
                    "Bootstrap an LLM session with protocol-first CLI+MCP usage guidance.",
                    args => BuildBootstrapLlmPrompt(args["goal"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("goal", "Optional current user objective to prioritize the next MCP calls.", false)),
                new PromptDefinition(
                    "gx_explain_object",
                    "Explain a GeneXus object using source, variables, navigation, and summary context.",
                    args => BuildExplainObjectPrompt(
                        args["name"]?.ToString() ?? string.Empty,
                        args["part"]?.ToString() ?? "Source"),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true),
                    new PromptArgumentDefinition("part", "Primary part to emphasize during the explanation.", false, _objectParts)),
                new PromptDefinition(
                    "gx_convert_object",
                    "Prepare a GeneXus object for conversion to another language using conversion context and target-specific guidance.",
                    args => BuildConvertObjectPrompt(
                        args["name"]?.ToString() ?? string.Empty,
                        args["targetLanguage"]?.ToString() ?? "CSharp"),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true),
                    new PromptArgumentDefinition("targetLanguage", "Target language for conversion.", true, _targetLanguages)),
                new PromptDefinition(
                    "gx_review_transaction",
                    "Review a Transaction object with focus on structure, rules, and generated impact.",
                    args => BuildReviewTransactionPrompt(args["name"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("name", "Transaction object name.", true)),
                new PromptDefinition(
                    "gx_refactor_procedure",
                    "Refactor a Procedure with attention to readability, side effects, and migration safety.",
                    args => BuildRefactorProcedurePrompt(args["name"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("name", "Procedure object name.", true)),
                new PromptDefinition(
                    "gx_generate_tests",
                    "Generate a test plan from source, variables, navigation, and business context.",
                    args => BuildGenerateTestsPrompt(args["name"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true)),
                new PromptDefinition(
                    "gx_trace_dependencies",
                    "Trace upstream and downstream dependencies for a GeneXus object.",
                    args => BuildTraceDependenciesPrompt(args["name"]?.ToString() ?? string.Empty),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true)),
                new PromptDefinition(
                    "gx_agent_ship_change",
                    "Guide an agent through a controlled GeneXus change with MCP discovery, verification, and Git-ready reporting.",
                    args => BuildAgentShipChangePrompt(
                        args["goal"]?.ToString() ?? string.Empty,
                        args["objectName"]?.ToString() ?? string.Empty,
                        args["part"]?.ToString() ?? "Source"),
                    new PromptArgumentDefinition("goal", "User-visible outcome or change objective.", true),
                    new PromptArgumentDefinition("objectName", "Primary GeneXus object when the scope is already known.", false),
                    new PromptArgumentDefinition("part", "Primary part to inspect first when an object is known.", false, _objectParts)),
                new PromptDefinition(
                    "gx_agent_visual_change",
                    "Guide an agent through a visual metadata change while resolving the authoritative GeneXus surface first.",
                    args => BuildVisualChangePrompt(
                        args["name"]?.ToString() ?? string.Empty,
                        args["changeGoal"]?.ToString() ?? string.Empty,
                        args["preferredSurface"]?.ToString() ?? "PatternInstance"),
                    new PromptArgumentDefinition("name", "GeneXus object name.", true),
                    new PromptArgumentDefinition("changeGoal", "Requested UI or metadata change.", true),
                    new PromptArgumentDefinition("preferredSurface", "Best initial guess for the authoritative editable surface.", false, _visualSurfaces))
            };

            return prompts.ToDictionary(prompt => prompt.Name, StringComparer.Ordinal);
        }

        private static string? ValidatePromptArguments(PromptDefinition prompt, JObject args)
        {
            foreach (var argument in prompt.Arguments)
            {
                string value = args[argument.Name]?.ToString() ?? string.Empty;
                if (argument.Required && string.IsNullOrWhiteSpace(value))
                {
                    return $"Missing required argument '{argument.Name}' for prompt '{prompt.Name}'.";
                }

                if (!string.IsNullOrWhiteSpace(value) &&
                    argument.AllowedValues.Length > 0 &&
                    !argument.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    return $"Invalid value '{value}' for argument '{argument.Name}' in prompt '{prompt.Name}'. Allowed values: {string.Join(", ", argument.AllowedValues)}.";
                }
            }

            return null;
        }

        private static bool TryGetPromptArgumentDefinition(string promptName, string argumentName, out PromptArgumentDefinition argument)
        {
            argument = null!;
            if (!_promptDefinitions.TryGetValue(promptName, out var prompt))
            {
                return false;
            }

            var found = prompt.Arguments.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, argumentName, StringComparison.OrdinalIgnoreCase));

            if (found == null)
            {
                return false;
            }

            argument = found;
            return true;
        }

        private static object? BuildStaticResourceResponse(JObject request)
        {
            string requestedUri = request["params"]?["uri"]?.ToString() ?? string.Empty;
            string uri = UnscopeResourceUri(requestedUri, out _);

            if (string.Equals(uri, "genexus://kb/health", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    resultType = "complete",
                    ttlMs = 1000,
                    cacheScope = "private",
                    contents = new[]
                    {
                        new
                        {
                            uri = "genexus://kb/health",
                            mimeType = "text/markdown",
                            text = BuildHealthReport()
                        }
                    }
                };
            }

            if (string.Equals(uri, "genexus://kb/agent-playbook", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    resultType = "complete",
                    ttlMs = 3600000,
                    cacheScope = "public",
                    contents = new[]
                    {
                        new
                        {
                            uri = "genexus://kb/agent-playbook",
                            mimeType = "text/markdown",
                            text = BuildAgentPlaybook()
                        }
                    }
                };
            }

            if (string.Equals(uri, "genexus://kb/llm-playbook", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    resultType = "complete",
                    ttlMs = 3600000,
                    cacheScope = "public",
                    contents = new[]
                    {
                        new
                        {
                            uri = "genexus://kb/llm-playbook",
                            mimeType = "text/markdown",
                            text = BuildLlmCliMcpPlaybook()
                        }
                    }
                };
            }

            // v2.8.0 — curated, source-verified GeneXus development skills.
            // Each entry is hand-authored and fact-checked against
            // docs.genexus.com so an LLM that consults it before invoking a
            // property/method has authoritative reference material instead
            // of hallucinated method names.
            const string skillPrefix = "genexus://kb/skills/";
            if (uri.StartsWith(skillPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string skillKey = uri.Substring(skillPrefix.Length);
                var skill = SkillCatalog.FindByKey(skillKey);
                if (skill == null) return null;
                return new
                {
                    resultType = "complete",
                    ttlMs = 3600000,
                    cacheScope = "public",
                    contents = new[]
                    {
                        new
                        {
                            uri,
                            mimeType = "text/markdown",
                            text = skill.Body
                        }
                    }
                };
            }

            // Friction 2026-05-22 #62: gotcha doc resource. Codes emitted on
            // warnings carry docUrl=genexus://kb/tool-help/gotchas/<code>; the
            // agent fetches the long-form here. Falls back to a generic stub
            // when the code is unknown so callers always get a 200.
            const string gotchaPrefix = "genexus://kb/tool-help/gotchas/";
            if (uri.StartsWith(gotchaPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string code = uri.Substring(gotchaPrefix.Length);
                string text = ToolHelpCatalog.GetGotchaHelp(code);
                return new
                {
                    resultType = "complete",
                    ttlMs = 3600000,
                    cacheScope = "public",
                    contents = new[]
                    {
                        new
                        {
                            uri,
                            mimeType = "text/markdown",
                            text
                        }
                    }
                };
            }

            const string toolHelpPrefix = "genexus://kb/tool-help/";
            if (uri.StartsWith(toolHelpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string toolName = uri.Substring(toolHelpPrefix.Length);
                string? text = ToolHelpCatalog.Get(toolName);
                if (text == null) return null;

                return new
                {
                    resultType = "complete",
                    ttlMs = 3600000,
                    cacheScope = "public",
                    contents = new[]
                    {
                        new
                        {
                            uri,
                            mimeType = "text/markdown",
                            text
                        }
                    }
                };
            }

            return null;
        }

        private static string BuildHealthReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Gateway Health Report");
            sb.AppendLine();
            sb.AppendLine("## Latency");

            var pool = Program.GetWorkerPool();
            var aliases = pool?.GetKnownAliases() ?? System.Array.Empty<string>();

            sb.AppendLine("| KB | spawnMs samples | spawnMs p50 | spawnMs p95 | spawnMs lastMs | sdkInitMs lastMs |");
            sb.AppendLine("|---|---|---|---|---|---|");

            if (aliases.Count == 0)
            {
                sb.AppendLine("| _(no KBs tracked)_ | — | — | — | — | — |");
            }
            else
            {
                foreach (var alias in aliases)
                {
                    var (count, p50, p95) = Program.OperationTracker.GetSpawnStats(alias);
                    var worker = pool?.TryGetWorker(alias);
                    var lastSpawn = worker?.SpawnMs?.ToString() ?? "n/a";
                    var lastSdkInit = worker?.SdkInitMs?.ToString() ?? "n/a";
                    sb.AppendLine($"| {alias} | {count} | {p50:0.#} | {p95:0.#} | {lastSpawn} | {lastSdkInit} |");
                }
            }

            return sb.ToString();
        }

        private static string BuildAgentPlaybook()
        {
            return
                "# GeneXus Agent Playbook\n\n" +
                "Use this server in an agent-native way:\n" +
                "1. Start with MCP discovery (`tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`).\n" +
                "2. Prefer resources for read-only grounding and use tool calls only for mutations or deeper analysis.\n" +
                "3. Keep GeneXus artifacts reviewable and Git-friendly: small diffs, explicit blast radius, and post-write verification.\n" +
                "4. For code or metadata changes, re-read before editing, write once, then confirm persistence with a second read.\n" +
                "5. Close the loop with the relevant lifecycle action (`validate`, `build`, `test`, or `index`) instead of stopping at a successful write.\n" +
                "6. When the authoritative surface is unclear, inspect summary, hierarchy, ui-context, pattern metadata, and visual parts before mutating anything.\n" +
                "7. Treat assets and visual metadata as first-class artifacts: inspect metadata first, then opt into heavy content only when necessary.\n\n" +
                "Current server strengths:\n" +
                "- MCP-first gateway and discovery\n" +
                "- Source, metadata, pattern, and asset operations\n" +
                "- Prompt and completion support\n\n" +
                "Current caution points:\n" +
                "- Some visual metadata flows still require practical persistence confirmation.\n" +
                "- Extension lint warnings are legacy debt; runtime validation is stronger than stylistic cleanliness today.\n" +
                "- Prompt flows are grounded, but the agent must still choose the smallest safe change set.";
        }

        private static string BuildLlmCliMcpPlaybook()
        {
            return
                "# LLM CLI+MCP Playbook\n\n" +
                "Use this server with protocol-first rules:\n" +
                "1. Use AXI CLI for bootstrap and environment checks (`home`, `status`, `doctor --mcp-smoke`, `tools list`, `config show`).\n" +
                "2. Use MCP tools for KB operations (`genexus_query`, `genexus_list_objects`, `genexus_read`, `genexus_edit`, `genexus_lifecycle`).\n" +
                "3. For list/read operations, always set `limit`/`offset`; prefer narrow, paginated requests.\n" +
                "4. For `genexus_query` and `genexus_list_objects`, use `fields` or `axiCompact=true` to reduce tokens.\n" +
                "5. Parse MCP tool payload from `result.content[0].text` as JSON.\n" +
                "6. `schemaVersion=mcp-axi/2` is emitted once at `initialize` (`_meta.schemaVersion`), not per response. Expect additive metadata on responses: collection helpers (`returned`, `total`, `empty`, `hasMore`, `nextOffset`) when inferable, and `meta.{truncated,fields,totalByType}` when relevant.\n" +
                "7. If `result.isError=true` and `operationId` is present, treat as running operation and poll `genexus_lifecycle(action='status'|'result', target='op:<operationId>')`.\n" +
                "8. For safe mutation flows, use patch `dryRun` first, then apply and re-read for persistence confirmation.\n\n" +
                "Recommended bootstrap sequence:\n" +
                "- `tools/list`\n" +
                "- `resources/list`\n" +
                "- `prompts/list`\n" +
                "- `resources/read` for `genexus://kb/llm-playbook`";
        }

        public static object? ConvertResourceCall(JObject request)
        {
            string uri = UnscopeResourceUri(request["params"]?["uri"]?.ToString() ?? "", out _);
            if (string.IsNullOrEmpty(uri)) return null;

            if (uri == "genexus://kb/index-status") return new { module = "KB", action = "GetIndexStatus" };
            if (uri == "genexus://kb/health") return new { module = "Health", action = "GetReport" };
            if (uri == "genexus://kb/capabilities") return new { module = "SdkProbe", action = "Capabilities", target = "_self" };
            if (uri == "genexus://objects") return new { module = "Search", action = "Query", target = "", limit = 200 };
            if (uri == "genexus://attributes") return new { module = "Search", action = "Query", target = "type:Attribute", limit = 200 };

            if (TryReadObjectResource(uri, out var objectResource))
                return objectResource;

            if (uri.StartsWith("genexus://attributes/", StringComparison.OrdinalIgnoreCase))
            {
                string name = uri.Replace("genexus://attributes/", "");
                return new { module = "Read", action = "GetAttribute", target = name };
            }

            return null;
        }

        internal static bool TryGetScopedResourceKb(JObject request, out string? kbAlias)
        {
            string uri = request["params"]?["uri"]?.ToString() ?? string.Empty;
            UnscopeResourceUri(uri, out kbAlias);
            return !string.IsNullOrWhiteSpace(kbAlias);
        }

        private static string UnscopeResourceUri(string uri, out string? kbAlias)
        {
            kbAlias = null;
            string trimmed = (uri ?? string.Empty).Trim();
            const string prefix = "genexus://kb/";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return trimmed;

            string remainder = trimmed.Substring(prefix.Length);
            int separator = remainder.IndexOf('/');
            if (separator <= 0 || separator == remainder.Length - 1) return trimmed;

            string candidateAlias = remainder.Substring(0, separator);
            string scopedResource = remainder.Substring(separator + 1);
            int rootSeparator = scopedResource.IndexOf('/');
            string root = rootSeparator < 0 ? scopedResource : scopedResource.Substring(0, rootSeparator);
            // `genexus://kb/skills/...` and the other existing KB resources are
            // already unscoped legacy URIs. Only recognize the roots emitted by
            // BuildScopedResourceUri so a skill named `foo` cannot be mistaken for
            // a KB alias.
            if (!string.Equals(root, "objects", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(root, "attributes", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(root, "kb", StringComparison.OrdinalIgnoreCase))
                return trimmed;

            try { kbAlias = Uri.UnescapeDataString(candidateAlias); }
            catch { kbAlias = candidateAlias; }
            return "genexus://" + scopedResource;
        }

        private static bool TryReadObjectResource(string uri, out object? resourceCall)
        {
            resourceCall = null;
            const string objectPrefix = "genexus://objects/";
            if (!uri.StartsWith(objectPrefix, StringComparison.OrdinalIgnoreCase)) return false;

            string relativePath = uri.Substring(objectPrefix.Length).Trim('/');
            if (string.IsNullOrWhiteSpace(relativePath)) return false;

            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            string name = segments[0];
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (segments.Length == 1)
            {
                resourceCall = new { module = "Read", action = "ExtractSource", target = name, part = "Source" };
                return true;
            }

            string resourceKind = segments[1];
            switch (resourceKind.ToLowerInvariant())
            {
                case "part":
                    string part = segments.Length >= 3 ? segments[2] : "Source";
                    resourceCall = new { module = "Read", action = "ExtractSource", target = name, part };
                    return true;
                case "source":
                    resourceCall = new { module = "Read", action = "ExtractSource", target = name, part = "Source" };
                    return true;
                case "variables":
                    resourceCall = new { module = "Read", action = "GetVariables", target = name };
                    return true;
                case "navigation":
                    resourceCall = new { module = "Analyze", action = "GetNavigation", target = name };
                    return true;
                case "hierarchy":
                    resourceCall = new { module = "Analyze", action = "GetHierarchy", target = name };
                    return true;
                case "data-context":
                    resourceCall = new { module = "Analyze", action = "GetDataContext", target = name };
                    return true;
                case "ui-context":
                    resourceCall = new { module = "UI", action = "GetUIContext", target = name };
                    return true;
                case "conversion-context":
                    resourceCall = new { module = "Analyze", action = "GetConversionContext", target = name };
                    return true;
                case "pattern-metadata":
                    resourceCall = new { module = "Analyze", action = "GetPatternMetadata", target = name };
                    return true;
                case "summary":
                    resourceCall = new { module = "Analyze", action = "Summarize", target = name };
                    return true;
                case "indexes":
                    resourceCall = new { module = "Structure", action = "GetVisualIndexes", target = name };
                    return true;
                case "logic-structure":
                    resourceCall = new { module = "Structure", action = "GetLogicStructure", target = name };
                    return true;
                default:
                    return false;
            }
        }

        public static object? ConvertToolCall(JObject request)
        {
            string? method = request["method"]?.ToString();
            if (method != "tools/call") return null;

            var paramsObj = request["params"] as JObject;
            string? toolName = paramsObj?["name"]?.ToString();
            var args = paramsObj?["arguments"] as JObject;

            if (string.IsNullOrEmpty(toolName)) return null;

            // Soft-alias rewrite for consolidated tools. Legacy callers (Cursor, Codex, older
            // Claude sessions) still work transparently; the new umbrella tool is the only one
            // advertised in tools/list. Set GXMCP_LEGACY_TOOL_ALIASES=0 to opt out early.
            if (Environment.GetEnvironmentVariable("GXMCP_LEGACY_TOOL_ALIASES") != "0")
            {
                if (TryRewriteLegacyTool(toolName!, args, out var rewrittenName, out var rewrittenArgs))
                {
                    toolName = rewrittenName;
                    args = rewrittenArgs;
                }
            }

            if (RemovedToolsRegistry.Map.ContainsKey(toolName)) return null;

            foreach (var router in _routers)
            {
                var converted = router.ConvertToolCall(toolName, args);
                if (converted != null) return converted;
            }

            // Direct declarative tool dispatch seam (Candidate 1 deepening)
            // Forwards canonical tools declared in tool_definitions.json directly to Worker CommandHandlerRegistry.
            bool isDeclared = _toolDefinitions != null && _toolDefinitions.Any(t => string.Equals(t["name"]?.ToString(), toolName, StringComparison.OrdinalIgnoreCase));
            if (isDeclared)
            {
                return new
                {
                    tool = toolName,
                    method = toolName,
                    action = args?["action"]?.ToString() ?? args?["mode"]?.ToString() ?? args?["step"]?.ToString(),
                    target = args?["target"]?.ToString() ?? args?["name"]?.ToString() ?? args?["object"]?.ToString() ?? args?["kb"]?.ToString(),
                    payload = args?["payload"]?.ToString() ?? args?["content"]?.ToString() ?? args?["source"]?.ToString() ?? args?["code"]?.ToString(),
                    @params = args ?? new JObject()
                };
            }

            return null;
        }

        // Maps a legacy tool name to its umbrella replacement, injecting/overwriting the
        // `action` (and `mode` where needed) field. Returns false for unknown names so the
        // normal router dispatch runs unchanged.
        internal static bool TryRewriteLegacyTool(
            string toolName,
            JObject? args,
            out string newToolName,
            out JObject newArgs)
        {
            switch (toolName)
            {
                // Umbrella: genexus_browser (smoke|a11y|wcag|capture|cross|preview).
                case "genexus_smoke_test":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "smoke";
                    newToolName = "genexus_browser";
                    return true;
                case "genexus_a11y_audit":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "a11y";
                    newToolName = "genexus_browser";
                    return true;
                case "genexus_wcag_check":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "wcag";
                    newToolName = "genexus_browser";
                    return true;
                case "genexus_browser_capture":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "capture";
                    newToolName = "genexus_browser";
                    return true;
                case "genexus_cross_browser":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "cross";
                    newToolName = "genexus_browser";
                    return true;
                case "genexus_preview":
                {
                    newArgs = CloneArgs(args);
                    // Preview's old sub-action (render|run) becomes the umbrella's `mode`.
                    var sub = newArgs["action"]?.ToString();
                    newArgs["mode"] = string.Equals(sub, "run", StringComparison.OrdinalIgnoreCase) ? "run" : "render";
                    newArgs["action"] = "preview";
                    newToolName = "genexus_browser";
                    return true;
                }

                // Umbrella: genexus_db (drift_*|optimize_*|sql_*|sample_data|types_*|translations_import).
                case "genexus_db_drift":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString();
                    newArgs["action"] = string.Equals(sub, "report", StringComparison.OrdinalIgnoreCase) ? "drift_report" : "drift_check";
                    newToolName = "genexus_db";
                    return true;
                }
                case "genexus_db_optimize":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub switch
                    {
                        "suggest_indexes" => "optimize_suggest",
                        "report" => "optimize_report",
                        _ => "optimize_analyze"
                    };
                    newToolName = "genexus_db";
                    return true;
                }
                case "genexus_sql":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub == "navigation" ? "sql_navigation" : "sql_ddl";
                    newToolName = "genexus_db";
                    return true;
                }
                case "genexus_generate_sample_data":
                {
                    newArgs = CloneArgs(args);
                    if (newArgs["trn"] != null && newArgs["target"] == null)
                        newArgs["target"] = newArgs["trn"];
                    newArgs["action"] = "sample_data";
                    newToolName = "genexus_db";
                    return true;
                }
                case "genexus_types":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub switch
                    {
                        "describe" => "types_describe",
                        "validate_value" => "types_validate",
                        _ => "types_list"
                    };
                    newToolName = "genexus_db";
                    return true;
                }
                case "genexus_translations":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "translations_import";
                    newToolName = "genexus_db";
                    return true;

                // Umbrella: genexus_versioning (history_*|undo|time_travel|blame|diff|diff_generated).
                case "genexus_history":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub switch
                    {
                        "get_source" => "history_get",
                        "save" => "history_save",
                        "restore" => "history_restore",
                        _ => "history_list"
                    };
                    newToolName = "genexus_versioning";
                    return true;
                }
                case "genexus_undo":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "undo";
                    newToolName = "genexus_versioning";
                    return true;
                case "genexus_time_travel":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "time_travel";
                    newToolName = "genexus_versioning";
                    return true;
                case "genexus_blame":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "blame";
                    newToolName = "genexus_versioning";
                    return true;
                case "genexus_diff":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "diff";
                    newToolName = "genexus_versioning";
                    return true;
                case "genexus_diff_generated":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "diff_generated";
                    newToolName = "genexus_versioning";
                    return true;

                // Umbrella: genexus_io (asset_*|export_part|import_part|export_unified|screenshot_publish|ocr).
                case "genexus_asset":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub switch
                    {
                        "read" => "asset_read",
                        "write" => "asset_write",
                        _ => "asset_find"
                    };
                    newToolName = "genexus_io";
                    return true;
                }
                case "genexus_export_object":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "export_part";
                    newToolName = "genexus_io";
                    return true;
                case "genexus_import_object":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "import_part";
                    newToolName = "genexus_io";
                    return true;
                case "genexus_export_unified":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "export_unified";
                    newToolName = "genexus_io";
                    return true;
                case "genexus_screenshot_publish":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "screenshot_publish";
                    newToolName = "genexus_io";
                    return true;
                case "genexus_ocr_screenshot":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "ocr";
                    newToolName = "genexus_io";
                    return true;

                // Umbrella: genexus_variable (add|delete|modify).
                case "genexus_add_variable":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "add";
                    newToolName = "genexus_variable";
                    return true;
                case "genexus_delete_variable":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "delete";
                    newToolName = "genexus_variable";
                    return true;
                case "genexus_modify_variable":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "modify";
                    newToolName = "genexus_variable";
                    return true;

                // Umbrella: genexus_telemetry (executions|watch_event|friction_*|learning_report|logs|profile_*).
                case "genexus_execution_history":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "executions";
                    newToolName = "genexus_telemetry";
                    return true;
                case "genexus_watch_event":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "watch_event";
                    newToolName = "genexus_telemetry";
                    return true;
                case "genexus_friction_log":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub == "tail" ? "friction_tail" : "friction_append";
                    newToolName = "genexus_telemetry";
                    return true;
                }
                case "genexus_learning":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "learning_report";
                    newToolName = "genexus_telemetry";
                    return true;
                case "genexus_logs":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "logs";
                    newToolName = "genexus_telemetry";
                    return true;
                case "genexus_profile":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub switch
                    {
                        "hotspots" => "profile_hotspots",
                        "correlate" => "profile_correlate",
                        _ => "profile_analyze"
                    };
                    newToolName = "genexus_telemetry";
                    return true;
                }

                // Umbrella: genexus_create (object|popup|sd_panel_*|save_as|scaffold|translate|sample|template).
                case "genexus_create_object":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "object";
                    newToolName = "genexus_create";
                    return true;
                case "genexus_create_popup":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "popup";
                    newToolName = "genexus_create";
                    return true;
                case "genexus_sd_panel":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub switch
                    {
                        "create" => "sd_panel_create",
                        "edit" => "sd_panel_edit",
                        _ => "sd_panel_inspect"
                    };
                    newToolName = "genexus_create";
                    return true;
                }
                case "genexus_save_as":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "save_as";
                    newToolName = "genexus_create";
                    return true;
                case "genexus_forge":
                {
                    newArgs = CloneArgs(args);
                    var sub = newArgs["action"]?.ToString()?.ToLowerInvariant();
                    newArgs["action"] = sub switch
                    {
                        "translate" => "translate",
                        "sample" => "sample",
                        _ => "scaffold"
                    };
                    newToolName = "genexus_create";
                    return true;
                }
                case "genexus_apply_template":
                    newArgs = CloneArgs(args);
                    newArgs["action"] = "template";
                    newToolName = "genexus_create";
                    return true;
            }

            newToolName = toolName;
            newArgs = args!;
            return false;
        }

        private static JObject CloneArgs(JObject? args) =>
            args is null ? new JObject() : (JObject)args.DeepClone();

        internal static void StripNulls(JObject obj)
        {
            var toRemove = new List<string>();
            foreach (var prop in obj.Properties())
            {
                if (prop.Value is null || prop.Value.Type == JTokenType.Null)
                    toRemove.Add(prop.Name);
                else if (prop.Value is JObject child)
                    StripNulls(child);
                else if (prop.Value is JArray arr)
                    foreach (var item in arr) if (item is JObject o) StripNulls(o);
            }
            foreach (var name in toRemove) obj.Remove(name);
        }

        /// <summary>
        /// Attaches a <c>_meta.background_jobs</c> snapshot to <paramref name="toolResult"/> when the session has
        /// running or unseen-completed jobs in <paramref name="registry"/>. Marks completed jobs as seen so they
        /// surface exactly once. No-ops when the snapshot is empty.
        /// </summary>
        internal static void PiggybackJobs(JObject toolResult, string sessionId, BackgroundJobRegistry registry)
        {
            var snapshot = registry.SnapshotForSession(sessionId);
            if (snapshot.Count == 0) return; // early-out: no job to inject → zero parse/serialize cost

            // PERF note: the JObject.Parse below cannot be avoided by reusing the caller's
            // PendingWorkerRequest.ParsedResponse — that holds the RPC envelope whose `result`
            // IS toolInnerResult (already reused upstream). The inner content[0].text payload
            // only exists serialized here (built inside BuildToolResultContent), so one
            // parse + serialize when a job actually exists is the structural minimum.

            var jobsArr = new JArray(snapshot.Select(j => new JObject
            {
                ["id"] = j.Id,
                ["status"] = j.Status,
                ["summary"] = j.Summary,
                ["completed_at"] = j.CompletedAt?.ToString("o"),
                ["estimated_seconds"] = j.EstimatedSeconds
            }));

            // The LLM reads content[0].text (a serialized JSON string), not the wrapper JObject.
            var content = toolResult["content"] as JArray;
            var first = content?[0] as JObject;
            var textToken = first?["text"];
            if (textToken != null)
            {
                JObject? inner;
                try { inner = JObject.Parse(textToken.ToString()); }
                catch { return; /* non-JSON text payload — leave alone */ }
                var meta = (JObject?)inner["_meta"] ?? new JObject();
                meta["background_jobs"] = jobsArr;
                inner["_meta"] = meta;
                first["text"] = inner.ToString(Newtonsoft.Json.Formatting.None);
            }
            else
            {
                // Error envelopes have no content array — attach _meta to the result root so jobs still surface.
                var meta = (JObject?)toolResult["_meta"] ?? new JObject();
                meta["background_jobs"] = jobsArr;
                toolResult["_meta"] = meta;
            }
            registry.MarkSeen(sessionId, snapshot.Select(j => j.Id));
        }

        /// <summary>
        /// Default token limit reported in <c>_meta.tokens</c>. Configurable; default 25000.
        /// </summary>
        internal const int MetaTokenLimit = 25000;

        /// <summary>
        /// Injects <c>_meta.tokens</c> into the inner JSON payload carried by
        /// <c>toolResult.content[0].text</c>.  Tokens are estimated as
        /// <c>Math.Round(charCount / 4)</c>.  When <c>_meta.tokens</c> already exists it is
        /// merged rather than replaced.  A non-null <c>hint</c> is added when usage exceeds
        /// 50% of <see cref="MetaTokenLimit"/>.
        /// </summary>
        internal static void InjectMetaTokens(JObject toolResult)
        {
            // Terse mode: the _meta.tokens used/limit block is UX sugar for LLM
            // self-pagination; terse deployments opt out of paying ~60-90 bytes
            // per response for it.
            if (Program.TerseResponsesEnabled()) return;

            try
            {
                var content = toolResult["content"] as JArray;
                var first = content?[0] as JObject;
                var textToken = first?["text"];
                if (textToken == null) return;

                string textStr = textToken.ToString();

                JObject? inner;
                try { inner = JObject.Parse(textStr); }
                catch { return; /* non-JSON text payload — leave alone */ }

                // Merge: don't overwrite an existing _meta.tokens block if already set.
                var meta = (JObject?)inner["_meta"] ?? new JObject();
                if (meta["tokens"] == null)
                {
                    // PERF: estimate the emitted size from the PRE-injection text (already in hand)
                    // plus a constant for the injected block, instead of a throwaway full serialize.
                    // The few-byte estimation error is immaterial against the 50% threshold
                    // (~12500 tokens). Single serialize on the common path carries the REAL `used`.
                    bool hadMeta = inner["_meta"] != null;
                    int blockOverhead = (hadMeta ? 0 : "\"_meta\":{}".Length + 1)
                                        + ",\"tokens\":{\"used\":1234567,\"limit\":".Length + MetaTokenLimit.ToString().Length + "}".Length;
                    int used = Math.Max(1, (int)Math.Round((textStr.Length + blockOverhead) / 4.0));
                    var tokenBlock = new JObject
                    {
                        ["used"] = used,
                        ["limit"] = MetaTokenLimit
                    };
                    meta["tokens"] = tokenBlock;
                    inner["_meta"] = meta;

                    string emitted = inner.ToString(Newtonsoft.Json.Formatting.None);
                    if (used > MetaTokenLimit / 2)
                    {
                        tokenBlock["hint"] = used > MetaTokenLimit
                            ? "Response exceeds token limit. Use fields/axiCompact=true, narrower filters, or pagination to reduce size."
                            : "Response is over 50% of the token limit. Consider fields/axiCompact=true or pagination for follow-up calls.";
                        // Block changed after measurement — one re-serialize on this rare path only,
                        // so the emitted text carries the hint; still a single assignment to first["text"].
                        emitted = inner.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    first["text"] = emitted;
                }
            }
            catch { /* token injection must never break the response */ }
        }

        /// <summary>
        /// Resolves a background-job ID from lifecycle tool arguments.
        /// Tries <c>job_id</c>, then <c>jobId</c>, then <c>target</c> (the lifecycle tool's
        /// conventional parameter), returning the first non-empty value found.
        /// </summary>
        internal static string? ResolveJobId(JObject? args)
        {
            var v = args?["job_id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(v)) return StripOpPrefix(v);
            v = args?["jobId"]?.ToString();
            if (!string.IsNullOrWhiteSpace(v)) return StripOpPrefix(v);
            v = args?["target"]?.ToString();
            if (!string.IsNullOrWhiteSpace(v)) return StripOpPrefix(v);
            return null;
        }

        // v2.6.2 (Item B follow-up): callers pass `target=op:<jobId>` to lifecycle cancel,
        // but JobRegistry keys are the raw GUID. Without stripping here, Cancel falls
        // through to the OperationTracker path and returns NotFound even when the job
        // is registered.
        private static string StripOpPrefix(string s)
        {
            if (s == null) return null;
            return s.StartsWith("op:", StringComparison.OrdinalIgnoreCase) ? s.Substring(3) : s;
        }

        /// <summary>
        /// Returns a terse error envelope containing only <c>message</c>, <c>code</c>, and <c>hint</c>.
        /// Stack traces and full SDK diagnostics are dropped by default. Pass <paramref name="verbose"/> = true
        /// (via the <c>verbose_errors</c> tool argument) to get the full original envelope.
        /// </summary>
        // v2.8.0 canonical error envelope nests code/message/hint/nextSteps under an
        // `error` sub-object: { status:"error", error:{ code, message, hint, ... }, ... }.
        // Legacy/flat envelopes put them at the top level. Resolve a field from the
        // canonical sub-object first, then fall back to the top level so both shapes
        // trim correctly. Without this, `error["message"]` is null for a canonical
        // envelope and the old `?? error["error"]?.ToString()` fallback serialized the
        // entire sub-object — whose first line is "{" — producing the {"message":"{"}
        // false error that masked every validation diagnostic (issue #24).
        private static JToken ResolveErrorField(JObject error, string key)
        {
            if (error == null) return null;
            if (error["error"] is JObject inner && inner[key] != null) return inner[key];
            return error[key];
        }

        private static string ResolveErrorMessage(JObject error)
        {
            string msg = ResolveErrorField(error, "message")?.ToString();
            // Last-resort legacy shape: `error` is a bare string, not a sub-object.
            if (string.IsNullOrEmpty(msg) && error?["error"]?.Type == JTokenType.String)
                msg = error["error"].ToString();
            return string.IsNullOrEmpty(msg) ? "Unknown error" : msg;
        }

        internal static JObject TrimErrorEnvelope(JObject error, bool verbose)
        {
            if (verbose) return error; // pass-through
            var trimmed = new JObject();
            // first line of message only
            var msg = ResolveErrorMessage(error);
            var firstLine = msg.Split('\n')[0].Trim();
            trimmed["message"] = firstLine;
            var code = ResolveErrorField(error, "code");
            if (code != null) trimmed["code"] = code;
            var hint = ResolveErrorField(error, "hint");
            if (hint != null) trimmed["hint"] = hint;
            // v2.6.9: preserve a small allowlist of routing/diagnosis fields that
            // an LLM needs to self-correct on the next call. Without them the
            // agent sees "WorkWithPlus cannot be applied to a Procedure." and has
            // to guess what IS valid; `validParentTypes` answers that in one hop.
            // `status` is preserved when it carries a non-Error semantic
            // (NotImplemented, NotApplicable, etc.) so the LLM can branch on it.
            string[] routingKeys = { "parentType", "validParentTypes", "patternKey", "target", "type" };
            foreach (var k in routingKeys)
            {
                if (error[k] != null) trimmed[k] = error[k];
            }
            // Friction 2026-05-25 item #5 — verification/validation failure
            // diagnostics. When the SDK rejects a write because the persisted
            // XML doesn't match the requested (Pattern write verification
            // failed, Visual write failed with sanitisation, etc.), the agent
            // needs the actual diff to fix the next call. The terse default
            // dropped `details` + `verifyDiff` so the agent saw only "Pattern
            // write verification failed" with no clue what was rejected.
            // Allowlist these when present — they're small structured objects.
            string[] diagnosticKeys = {
                "details", "verifyDiff", "suggestion", "persistedSnippet", "requestedSnippet",
                "availableParts", "part", "objectName", "objectType",
                // Patch persistence receipt: these fields must survive terse error
                // projection so WriteNotPersisted still tells the caller what the SDK
                // saved, what the forced re-read proved, and whether rollback landed.
                "saved", "verified", "persisted", "persistedVerified", "requestedHash",
                "persistedHash", "normalizedRequestedHash", "normalizedPersistedHash",
                "persistedMatchCount", "oldContentPresent", "verification", "rollback",
                "rolledBack", "versionToken", "persistedVerifyError", "replacementPresent",
                "reReadConfirmed", "commentOnly", "commentStyle", "before", "after",
                "matchedCount", "implicitOperations"
            };
            foreach (var k in diagnosticKeys)
            {
                if (error[k] != null) trimmed[k] = error[k];
            }
            string status = error["status"]?.ToString();
            if (!string.IsNullOrEmpty(status) &&
                !string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase))
            {
                trimmed["status"] = status;
            }
            // Friction 2026-05-22 #63: surface a structured "what to do next"
            // hint on every error envelope. Pre-existing suggested_next_step
            // (e.g. from the worker's write_not_persisted path) is preserved;
            // otherwise we synthesize one from the error code / message text.
            JToken existing = error["suggested_next_step"] ?? AttachSuggestedNextStep(error);
            if (existing != null) trimmed["suggested_next_step"] = existing;
            return trimmed;
        }

        /// <summary>
        /// Friction 2026-05-22 #63: turn an error envelope into a structured
        /// "next-step" hint. Pure function — code/message pattern matching, no
        /// I/O. Returns null when the error doesn't match any registered
        /// recovery shape (TrimErrorEnvelope then falls back to message+hint).
        /// </summary>
        public static JObject AttachSuggestedNextStep(JObject error)
        {
            if (error == null) return null;
            string code = ResolveErrorField(error, "code")?.ToString() ?? error["status"]?.ToString();
            string msg = ResolveErrorMessage(error);
            if (string.Equals(msg, "Unknown error", StringComparison.Ordinal)) msg = "";

            // Patch NoMatch — point at fuzzy/eolDiff (already in payload as
            // nearMatches/eolDiff/did_you_mean); next-step tells the agent how
            // to consume them.
            if (string.Equals(code, "patch_no_match", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "NoMatch", StringComparison.OrdinalIgnoreCase)
                || (msg.IndexOf("Context not found", StringComparison.OrdinalIgnoreCase) >= 0)
                || (msg.IndexOf("Ambiguous patch", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new JObject
                {
                    ["action"] = "inspect_near_match",
                    ["hint"] = "Patch context did not match. Inspect response.nearMatches / response.eolDiff / response.did_you_mean for the closest source window. Re-issue with the exact tabs/EOLs/whitespace of one of those, or pass replaceAll=true if you intended every occurrence."
                };
            }

            // Visual write failure — point at LayoutGotchaScanner / inspect.
            if (string.Equals(code, "visual_write_failed", StringComparison.OrdinalIgnoreCase)
                || (msg.IndexOf("Invalid visual XML", StringComparison.OrdinalIgnoreCase) >= 0)
                || (msg.IndexOf("Visual part not found", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new JObject
                {
                    ["action"] = "run_layout_gotcha_scanner",
                    ["hint"] = "Visual part write failed. Use genexus_inspect include=structure to fetch the live layout, then check response.layoutGotchas for the structural rule that the SDK rejects (gxButton custom events in html-form, ControlType misspellings, missing AttID/DataField, etc.). Fix the offending element and retry."
                };
            }

            // KB_AMBIGUOUS — point at the kb parameter.
            if (string.Equals(code, "KB_AMBIGUOUS", StringComparison.OrdinalIgnoreCase)
                || (msg.IndexOf("KB_AMBIGUOUS", StringComparison.OrdinalIgnoreCase) >= 0)
                || (msg.IndexOf("multiple KBs", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new JObject
                {
                    ["action"] = "specify_kb",
                    ["hint"] = "More than one KB is open. For a one-off call, re-issue it with kb=<alias>. For a session-wide default, run genexus_kb action=set_default alias=<alias>. genexus_whoami / genexus_kb action=list enumerate the available aliases."
                };
            }

            // spc0150 build failure — point at the extract_to_procedure recipe.
            if (string.Equals(code, "LintSpc0150ForEachAttributeWrite", StringComparison.OrdinalIgnoreCase)
                || (msg.IndexOf("spc0150", StringComparison.OrdinalIgnoreCase) >= 0)
                || (msg.IndexOf("Attribute cannot be assigned in this context", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new JObject
                {
                    ["action"] = "recipe_extract_to_procedure",
                    ["hint"] = "spc0150 fires when a WebPanel Events block writes a transaction attribute inside For each. Call genexus_recipe { name: 'extract_to_procedure' } to get the step-by-step playbook for moving the attribute-write into a Procedure.",
                    ["recipe"] = "extract_to_procedure"
                };
            }

            // PartNotFound — point at genexus_read omitting part (to get full object or availableParts)
            if (string.Equals(code, "PartNotFound", StringComparison.OrdinalIgnoreCase)
                || (msg.IndexOf("Part not found", StringComparison.OrdinalIgnoreCase) >= 0)
                || (msg.IndexOf("part does not exist", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new JObject
                {
                    ["action"] = "read_full_object",
                    ["tool"] = "genexus_read",
                    ["hint"] = "The requested part does not exist on this object type. Call genexus_read omitting 'part' (or part='all') to fetch all valid parts for this object in 1 call."
                };
            }

            // ObjectNotFound — point at genexus_query to find candidate names
            if (string.Equals(code, "ObjectNotFound", StringComparison.OrdinalIgnoreCase)
                || (msg.IndexOf("Object not found", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new JObject
                {
                    ["action"] = "search_objects",
                    ["tool"] = "genexus_query",
                    ["hint"] = "Object not found with this exact name. Call genexus_query to search for similar names or partial matches."
                };
            }

            return null;
        }

        // Friction 2026-05-22: long builds (5-13min for popup compile) at the 90s cap
        // forced ~12 polls per build, each consuming a turn. 600s lets a single
        // long-poll cover the slowest realistic build.
        public const int MaxLongPollSeconds = 600;

        // Bug 2026-05-22: blocking the stdio response for >~60s with no traffic
        // makes Claude Code's MCP client treat the request as dead and close
        // the transport ("MCP error -32000: Connection closed"). When the
        // caller did NOT include a progressToken we have no way to keep the
        // client alive, so cap the effective wait below the observed client
        // timeout and let the caller re-poll. With a progressToken we emit
        // notifications/progress on HeartbeatIntervalSeconds and can safely
        // respect the full MaxLongPollSeconds.
        public const int SafeLongPollSecondsWithoutProgress = 50;
        public const int HeartbeatIntervalSeconds = 15;

        /// <summary>
        /// Awaits <paramref name="completion"/> while keeping a synchronous MCP request alive:
        /// when the client supplied a usable <paramref name="progressToken"/> and a
        /// <paramref name="heartbeat"/> writer, emits an MCP <c>notifications/progress</c> payload
        /// every <paramref name="heartbeatIntervalSeconds"/> until the work completes or
        /// <paramref name="timeoutMs"/> elapses. This is the spec-native keepalive for long-running
        /// synchronous tool calls (e.g. a first <c>apply_pattern</c>) so the client doesn't fire its
        /// own request timeout (<c>-32001</c>) on work that is still progressing on the server.
        /// Returns <c>true</c> when <paramref name="completion"/> finished, <c>false</c> on timeout.
        /// A heartbeat write that throws is swallowed — liveness signalling must never abort the call.
        /// </summary>
        internal static async Task<bool> AwaitWithHeartbeat(
            Task completion,
            int timeoutMs,
            JToken? progressToken,
            Func<JObject, Task>? heartbeat,
            string toolName,
            int heartbeatIntervalSeconds = HeartbeatIntervalSeconds)
        {
            bool canHeartbeat = heartbeat != null
                && progressToken != null
                && progressToken.Type != Newtonsoft.Json.Linq.JTokenType.Null
                && heartbeatIntervalSeconds > 0;

            if (!canHeartbeat)
            {
                var done = await Task.WhenAny(completion, Task.Delay(timeoutMs));
                return done == completion;
            }

            var deadlineTask = Task.Delay(timeoutMs);
            int elapsedSec = 0;
            while (true)
            {
                var beatTask = Task.Delay(TimeSpan.FromSeconds(heartbeatIntervalSeconds));
                var winner = await Task.WhenAny(completion, deadlineTask, beatTask);
                if (winner == completion) return true;
                if (winner == deadlineTask) return false;

                elapsedSec += heartbeatIntervalSeconds;
                var note = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/progress",
                    ["params"] = new JObject
                    {
                        ["progressToken"] = progressToken!.DeepClone(),
                        ["progress"] = elapsedSec,
                        ["message"] = $"{toolName} still running ({elapsedSec}s elapsed)…"
                    }
                };
                try { await heartbeat!(note); } catch { /* liveness signalling is best-effort */ }
            }
        }

        /// <summary>
        /// Long-polls <paramref name="registry"/> for <paramref name="jobId"/> until it reaches a terminal
        /// state or <paramref name="waitSeconds"/> elapses (clamped 0–<see cref="MaxLongPollSeconds"/>).
        /// Returns a status envelope.
        /// <list type="bullet">
        ///   <item><c>wait_seconds=0</c> (or omitted) → immediate single poll, no blocking.</item>
        ///   <item>Unknown job → envelope with <c>error="unknown_job_id"</c>.</item>
        ///   <item>Terminal job → returns immediately regardless of <paramref name="waitSeconds"/>.</item>
        ///   <item>When <paramref name="progressToken"/> is supplied and <paramref name="heartbeat"/> is non-null,
        ///         emits an MCP <c>notifications/progress</c> JSON-RPC payload every <see cref="HeartbeatIntervalSeconds"/>
        ///         so the client doesn't time out the in-flight request.</item>
        ///   <item>When no <paramref name="progressToken"/> is available the effective wait is capped at
        ///         <see cref="SafeLongPollSecondsWithoutProgress"/> regardless of the requested
        ///         <paramref name="waitSeconds"/> — callers re-poll to cover longer waits.</item>
        ///   <item>When <paramref name="cancellationToken"/> is signalled, returns a typed
        ///         request-cancelled envelope instead of waiting until the poll deadline.</item>
        /// </list>
        /// </summary>
        internal static async Task<JObject> LongPollJob(
            BackgroundJobRegistry registry,
            string jobId,
            int waitSeconds,
            JToken? progressToken = null,
            Func<JObject, Task>? heartbeat = null,
            CancellationToken cancellationToken = default)
        {
            // Clamp wait_seconds to [0, MaxLongPollSeconds]
            int requestedWaitSeconds = Math.Min(Math.Max(waitSeconds, 0), MaxLongPollSeconds);

            // A JToken with Type=Null is not C# null but carries no useful progressToken
            // value (client sent `_meta.progressToken: null`). Treat it as absent so the
            // safe-wait cap fires and we don't emit progress notifications with a null token.
            bool hasUsefulProgressToken = progressToken != null && progressToken.Type != Newtonsoft.Json.Linq.JTokenType.Null;
            bool canHeartbeat = hasUsefulProgressToken && heartbeat != null;
            int effectiveWaitSeconds = canHeartbeat
                ? requestedWaitSeconds
                : Math.Min(requestedWaitSeconds, SafeLongPollSecondsWithoutProgress);
            bool capApplied = effectiveWaitSeconds < requestedWaitSeconds;

            var startedAt = DateTime.UtcNow;
            var deadline = startedAt.AddSeconds(effectiveWaitSeconds);
            var nextHeartbeatAt = startedAt.AddSeconds(HeartbeatIntervalSeconds);
            JobEntry? job;

            do
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return BuildRequestCancelledEnvelope(jobId);
                }

                job = registry.Get(jobId);
                if (job == null || job.Status != "running" || effectiveWaitSeconds == 0)
                    break;

                if (canHeartbeat && DateTime.UtcNow >= nextHeartbeatAt)
                {
                    int elapsed = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
                    var note = new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "notifications/progress",
                        ["params"] = new JObject
                        {
                            ["progressToken"] = progressToken!.DeepClone(),
                            ["progress"] = elapsed,
                            ["total"] = effectiveWaitSeconds,
                            ["message"] = $"job {jobId} still running ({elapsed}s elapsed, status={job.Status})"
                        }
                    };
                    try { await heartbeat!(note).ConfigureAwait(false); }
                    catch { /* heartbeat failure must not abort the poll */ }
                    nextHeartbeatAt = DateTime.UtcNow.AddSeconds(HeartbeatIntervalSeconds);
                }

                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return BuildRequestCancelledEnvelope(jobId);
                }
            }
            while (DateTime.UtcNow < deadline);

            if (job == null)
            {
                return new JObject
                {
                    ["error"] = "unknown_job_id",
                    ["job_id"] = jobId
                };
            }

            var envelope = new JObject
            {
                ["job_id"] = job.Id,
                ["status"] = job.Status,
                ["summary"] = job.Summary,
                ["completed_at"] = job.CompletedAt?.ToString("o"),
                ["estimated_seconds"] = job.EstimatedSeconds,
                ["result"] = job.Result
            };

            // Surface the safe-wait cap so callers know to re-poll: we returned early
            // (relative to their requested wait_seconds) because no progressToken was
            // available to keep their connection alive past SafeLongPollSecondsWithoutProgress.
            if (capApplied && string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                envelope["capped"] = true;
                envelope["cappedAtSeconds"] = SafeLongPollSecondsWithoutProgress;
            }

            return envelope;
        }

        private static JObject BuildRequestCancelledEnvelope(string jobId)
        {
            return new JObject
            {
                ["error"] = new JObject
                {
                    ["code"] = -32800,
                    ["message"] = "Request cancelled by client"
                },
                ["cancelled"] = true,
                ["job_id"] = jobId
            };
        }

        // v2.6.4 (#18): lifecycle action=result for op:<id> reads the stored
        // JobEntry result. Extracted from Program.cs so it can be unit-tested
        // and so the result-envelope shape stays in lockstep with status long-poll.
        // isError is set when the job terminated in failed/cancelled — callers
        // that branch on isError get a clear pass/fail signal.
        internal static (JObject envelope, bool isError) BuildJobResultEnvelope(JobEntry job)
        {
            if (job == null)
                throw new ArgumentNullException(nameof(job));

            if (string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                var pending = new JObject
                {
                    ["status"] = "Pending",
                    ["operationId"] = job.Id,
                    ["message"] = "Operation still running. Poll genexus_lifecycle action=status target=op:" + job.Id + " (with wait_seconds>0 to long-poll), then call result once it terminates.",
                    ["startedAt"] = job.StartedAt.ToString("o"),
                    ["estimated_seconds"] = job.EstimatedSeconds
                };
                return (pending, isError: false);
            }

            var terminal = new JObject
            {
                ["status"] = job.Status,
                ["operationId"] = job.Id,
                ["kind"] = job.Kind,
                ["summary"] = job.Summary,
                ["startedAt"] = job.StartedAt.ToString("o"),
                ["completedAt"] = job.CompletedAt?.ToString("o")
            };
            if (job.Result != null) terminal["result"] = job.Result;
            bool isErr = string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                      // issue #79: a watchdog-stalled job is terminal AND an error — the
                      // SDK never answered, so the agent needs the recovery steps, not a
                      // neutral status.
                      || string.Equals(job.Status, "stalled", StringComparison.OrdinalIgnoreCase);
            // Friction 2026-05-22 item 10: when the inner BuildTaskStatus reports
            // 0 errors / 0 warnings / ExitCode=0 (or partial_success=true), respect
            // that over the registry's status flag. Race-safe: if the registry
            // stamped success=false but the build truly was a 0/0/0, the agent
            // would otherwise see an <e>error{}> envelope around "Build succeeded".
            // cancelled/stalled are excluded: their terminal meaning is authoritative and
            // must not be reclassified by the build-outcome heuristic (a stalled job's
            // envelope has no build fields, which the classifier would read as 0/0/0).
            if (isErr && job.Result is JObject inner
                && !string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(job.Status, "stalled", StringComparison.OrdinalIgnoreCase))
            {
                var outcome = LifecycleResponseShaper.ClassifyBuildOutcome(inner);
                if (outcome == LifecycleResponseShaper.BuildOutcome.Success)
                    isErr = false;
                else if (outcome == LifecycleResponseShaper.BuildOutcome.PartialSuccess)
                {
                    isErr = false;
                    terminal["partial_success"] = true;
                    terminal["envelope"] = "warning";
                }
            }
            return (terminal, isErr);
        }

        /// <summary>
        /// Issue #27 item 1: classify a worker Build/Status payload for job reconciliation.
        /// Returns <c>null</c> when the job should stay "running" (worker still building, or a
        /// transient/unparseable status), or a terminal resolution otherwise. Pure and
        /// side-effect-free so the reconcile decision is unit-testable without a live worker.
        ///
        /// <paramref name="workerStatus"/> is the unwrapped BuildTaskStatus JSON (status/Status,
        /// errorCount/ErrorCount, warningCount/WarningCount, message/Message). A worker "Error"
        /// whose message mentions "not found" is treated as tracking-lost (worker recycled and
        /// dropped its in-memory task map), not a build error.
        /// </summary>
        internal static (bool success, string summary, JObject result)? ClassifyWorkerBuildStatus(JObject? workerStatus)
        {
            if (workerStatus == null) return null;
            if (workerStatus["error"] != null) return null; // transient poll error

            string? s = workerStatus["status"]?.ToString() ?? workerStatus["Status"]?.ToString();
            if (string.IsNullOrEmpty(s)) return null;

            bool isError = string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase);
            string? msg = workerStatus["message"]?.ToString() ?? workerStatus["Message"]?.ToString();
            bool trackingLost = isError && msg != null
                                && msg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
            if (trackingLost)
            {
                return (false,
                    "Build tracking lost — the worker was recycled before this job resolved, so its outcome can't be confirmed. Re-run the build to verify.",
                    new JObject { ["status"] = "TrackingLost" });
            }

            bool terminal = string.Equals(s, "Succeeded", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(s, "Cancelled", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(s, "ReorgRequired", StringComparison.OrdinalIgnoreCase);
            if (!terminal) return null; // still Running — genuinely in progress

            var outcome = LifecycleResponseShaper.ClassifyBuildOutcome(workerStatus);
            bool success = outcome == LifecycleResponseShaper.BuildOutcome.Success
                        || outcome == LifecycleResponseShaper.BuildOutcome.PartialSuccess;
            int errs = workerStatus["errorCount"]?.ToObject<int?>() ?? workerStatus["ErrorCount"]?.ToObject<int?>() ?? 0;
            int warns = workerStatus["warningCount"]?.ToObject<int?>() ?? workerStatus["WarningCount"]?.ToObject<int?>() ?? 0;
            string summary = string.Equals(s, "ReorgRequired", StringComparison.OrdinalIgnoreCase)
                ? "Build All stopped because the KB requires reorganization; run action=reorg explicitly and retry."
                : outcome == LifecycleResponseShaper.BuildOutcome.Error
                ? $"Build {s}: {errs} errors, {warns} warnings"
                : success
                ? $"Build succeeded: {warns} warnings, {errs} errors"
                : $"Build {s}: {errs} errors, {warns} warnings";
            return (success, summary, workerStatus);
        }
    }
}
