using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Gateway-side schema pre-validation for MCP tool call arguments.
    /// Validates required fields, basic JSON types, and enum membership
    /// against the inputSchema declared in tool_definitions.json.
    /// No worker round-trip is needed; failures are returned immediately.
    /// </summary>
    internal static class GatewayArgsValidator
    {
        // Cache: toolName → inputSchema JObject (null = tool has no inputSchema or no constraints)
        private static readonly ConcurrentDictionary<string, JObject?> _schemaCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _loadLock = new();
        private static JArray? _toolDefs;

        public sealed class Violation
        {
            public string Path { get; init; } = "";
            public string Expected { get; init; } = "";
            public string Actual { get; init; } = "";
            /// <summary>
            /// Compact JSON of the offending value for a type mismatch (<see cref="Actual"/>
            /// carries only the JSON type name). Null otherwise.
            /// </summary>
            public string? Received { get; init; }
            /// <summary>
            /// The schema description of the offending field, set on a type mismatch so the
            /// message says what the field carries (e.g. lifecycle <c>code</c> is source text,
            /// not a flag). Null otherwise.
            /// </summary>
            public string? Description { get; init; }
            /// <summary>
            /// Declared argument names, set on an unknown-argument violation so the
            /// caller can see what the tool does accept. Null otherwise.
            /// </summary>
            public IReadOnlyList<string>? Accepted { get; init; }
            /// <summary>
            /// Best DidYouMean match for a typo'd enum value or arg key, when one
            /// is within edit distance. Null when there's no close candidate.
            /// </summary>
            public string? Suggestion { get; init; }
        }

        // Cross-cutting arguments the gateway/worker honour on (almost) every tool
        // but that individual tool schemas don't redeclare (projection knobs, KB
        // selectors, mutation guards). Never flagged as unknown even when a tool's
        // `properties` doesn't list them.
        internal static readonly HashSet<string> CrossCuttingArgs = new(StringComparer.OrdinalIgnoreCase)
        {
            "axiCompact", "projection", "fields", "full",
            "kb", "kbAlias", "alias", "workingDir", "correlationId",
            "dryRun", "confirm", "force", "until"
        };

        public sealed class ValidationResult
        {
            public bool Ok { get; init; }
            public List<Violation> Violations { get; init; } = new();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Validate <paramref name="args"/> against the tool's inputSchema.
        /// Returns Ok=true when validation passes or the tool has no schema constraints.
        /// </summary>
        public static ValidationResult Validate(string toolName, JObject? args)
        {
            var schema = GetSchema(toolName);
            if (schema == null)
                return new ValidationResult { Ok = true };

            var violations = new List<Violation>();
            var properties = schema["properties"] as JObject;
            var required = (schema["required"] as JArray)?.Select(t => t.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                           ?? new HashSet<string>();
            bool additionalPropertiesFalse = schema["additionalProperties"]?.ToObject<bool?>() == false;

            // 1. Required fields must be present
            foreach (string req in required)
            {
                if (args == null || args[req] == null)
                {
                    string expectedType = (properties?[req] as JObject)?["type"]?.ToString() ?? "value";
                    violations.Add(new Violation
                    {
                        Path = req,
                        Expected = expectedType,
                        Actual = "missing"
                    });
                }
            }

            if (args != null && properties != null)
            {
                foreach (var prop in args.Properties())
                {
                    // 2. additionalProperties: false — reject unknown keys
                    if (additionalPropertiesFalse && !properties.ContainsKey(prop.Name))
                    {
                        if (CrossCuttingArgs.Contains(prop.Name))
                            continue;

                        string? keySuggestion = DidYouMean.Suggest(
                            prop.Name, properties.Properties().Select(p => p.Name));

                        violations.Add(new Violation
                        {
                            Path = prop.Name,
                            Expected = "none (additionalProperties: false)",
                            Actual = "unknown argument",
                            Suggestion = keySuggestion,
                            Accepted = DeclaredArgumentNames(properties)
                        });
                        continue;
                    }

                    var propSchema = properties[prop.Name] as JObject;
                    if (propSchema == null)
                    {
                        // Unknown key on a tool without additionalProperties:false.
                        // Fail loud ONLY when it's a high-confidence typo of a
                        // declared property (edit distance <= 2) — that can't be a
                        // legitimate undeclared passthrough arg, so it's almost
                        // certainly a slip. Cross-cutting args are never flagged.
                        if (!CrossCuttingArgs.Contains(prop.Name))
                        {
                            string? keySuggestion = DidYouMean.Suggest(
                                prop.Name, properties.Properties().Select(p => p.Name));
                            if (keySuggestion != null)
                            {
                                violations.Add(new Violation
                                {
                                    Path = prop.Name,
                                    Expected = "a known argument",
                                    Actual = "unknown argument",
                                    Suggestion = keySuggestion,
                                    Accepted = DeclaredArgumentNames(properties)
                                });
                            }
                        }
                        continue;
                    }

                    string? declaredType = propSchema["type"]?.ToString();
                    var enumValues = propSchema["enum"] as JArray;

                    // 3. Type check
                    if (declaredType != null && prop.Value.Type != JTokenType.Null)
                    {
                        if (!IsTypeMatch(prop.Value, declaredType))
                        {
                            violations.Add(new Violation
                            {
                                Path = prop.Name,
                                Expected = declaredType,
                                Actual = prop.Value.Type.ToString().ToLowerInvariant(),
                                Received = prop.Value.ToString(Newtonsoft.Json.Formatting.None),
                                Description = propSchema["description"]?.ToString()
                            });
                            continue; // don't also check enum if type is wrong
                        }
                    }

                    // 4. Enum membership
                    if (enumValues != null && prop.Value.Type != JTokenType.Null)
                    {
                        string val = prop.Value.ToString();
                        var allowed = enumValues.Select(e => e.ToString()).ToList();
                        if (!allowed.Contains(val, StringComparer.Ordinal))
                        {
                            violations.Add(new Violation
                            {
                                Path = prop.Name,
                                Expected = "one of [" + string.Join(", ", allowed) + "]",
                                Actual = val,
                                Suggestion = DidYouMean.Suggest(val, allowed)
                            });
                        }
                    }
                }
            }

            if (string.Equals(toolName, "genexus_variable", StringComparison.OrdinalIgnoreCase))
                ValidateVariableDimensions(args, violations);
            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
                ValidateLifecycleActionArguments(args, violations);

            return new ValidationResult
            {
                Ok = violations.Count == 0,
                Violations = violations
            };
        }

        // verification is read only by action=reconcile; any other action would ignore it
        // silently, so the call is refused and the message names the action it belongs to.
        private static void ValidateLifecycleActionArguments(JObject? args, List<Violation> violations)
        {
            if (args?["verification"] is not JToken verification || verification.Type == JTokenType.Null) return;
            string action = args["action"]?.ToString() ?? string.Empty;
            if (string.Equals(action, "reconcile", StringComparison.OrdinalIgnoreCase)) return;

            violations.Add(new Violation
            {
                Path = "verification",
                Expected = "action=reconcile",
                Actual = "not applicable",
                Received = verification.ToString(Newtonsoft.Json.Formatting.None)
            });
        }

        private static void ValidateVariableDimensions(JObject? args, List<Violation> violations)
        {
            if (args == null) return;

            ValidateVariableDimensionFields(
                args,
                string.Empty,
                args["dimensions"],
                args["dimensionSizes"],
                args["collection"],
                violations);

            if (args["variables"] is not JArray batch) return;
            for (int index = 0; index < batch.Count; index++)
            {
                string path = "variables[" + index + "]";
                if (batch[index] is not JObject item)
                {
                    // The generic array check cannot inspect a non-object item;
                    // report it here so a malformed batch cannot reach the Worker.
                    violations.Add(new Violation
                    {
                        Path = path,
                        Expected = "object",
                        Actual = batch[index]?.Type.ToString().ToLowerInvariant() ?? "null"
                    });
                    continue;
                }

                ValidateVariableDimensionFields(
                    item,
                    path + ".",
                    item["dimensions"],
                    item["dimensionSizes"],
                    item["collection"],
                    violations);
            }
        }

        private static void ValidateVariableDimensionFields(
            JObject args,
            string pathPrefix,
            JToken? dimensionsToken,
            JToken? sizesToken,
            JToken? collectionToken,
            List<Violation> violations)
        {
            bool hasDimensions = dimensionsToken != null && dimensionsToken.Type != JTokenType.Null;
            bool hasSizes = sizesToken != null && sizesToken.Type != JTokenType.Null;
            if (!hasDimensions && !hasSizes) return;

            int? dimensions = null;
            if (hasDimensions && dimensionsToken!.Type == JTokenType.Integer)
            {
                if (int.TryParse(dimensionsToken.ToString(), out int parsedDimensions))
                {
                    dimensions = parsedDimensions;
                    if (dimensions < 1 || dimensions > 2)
                    {
                        violations.Add(new Violation
                        {
                            Path = pathPrefix + "dimensions",
                            Expected = "1 or 2",
                            Actual = dimensionsToken.ToString()
                        });
                    }
                }
                else
                {
                    violations.Add(new Violation
                    {
                        Path = pathPrefix + "dimensions",
                        Expected = "1 or 2",
                        Actual = dimensionsToken.ToString()
                    });
                }
            }

            if (!hasDimensions)
            {
                violations.Add(new Violation
                {
                    Path = pathPrefix + "dimensions",
                    Expected = "1 or 2 when dimensionSizes is provided",
                    Actual = "missing"
                });
            }
            else if (dimensionsToken!.Type != JTokenType.Integer)
            {
                if (!string.IsNullOrEmpty(pathPrefix))
                {
                    violations.Add(new Violation
                    {
                        Path = pathPrefix + "dimensions",
                        Expected = "integer 1 or 2",
                        Actual = dimensionsToken.Type.ToString().ToLowerInvariant()
                    });
                }
            }
            else if (!hasSizes)
            {
                violations.Add(new Violation
                {
                    Path = pathPrefix + "dimensionSizes",
                    Expected = dimensions.HasValue
                        ? "positive array with length " + dimensions.Value
                        : "positive array",
                    Actual = "missing"
                });
            }
            else if (sizesToken!.Type != JTokenType.Array)
            {
                if (!string.IsNullOrEmpty(pathPrefix))
                {
                    violations.Add(new Violation
                    {
                        Path = pathPrefix + "dimensionSizes",
                        Expected = "array",
                        Actual = sizesToken.Type.ToString().ToLowerInvariant()
                    });
                }
            }
            else
            {
                var sizes = (JArray)sizesToken;
                if (dimensions.HasValue && sizes.Count != dimensions.Value)
                {
                    violations.Add(new Violation
                    {
                        Path = pathPrefix + "dimensionSizes",
                        Expected = "length " + dimensions.Value,
                        Actual = "length " + sizes.Count
                    });
                }

                for (int index = 0; index < sizes.Count; index++)
                {
                    JToken size = sizes[index];
                    bool positiveInteger = size.Type == JTokenType.Integer
                        && int.TryParse(size.ToString(), out int parsedSize)
                        && parsedSize > 0;
                    if (!positiveInteger)
                    {
                        violations.Add(new Violation
                        {
                            Path = pathPrefix + "dimensionSizes[" + index + "]",
                            Expected = "positive integer",
                            Actual = size.Type == JTokenType.Null ? "null" : size.ToString()
                        });
                    }
                }
            }

            if (collectionToken != null && collectionToken.Type != JTokenType.Null
                && collectionToken.Type != JTokenType.Boolean && !string.IsNullOrEmpty(pathPrefix))
            {
                violations.Add(new Violation
                {
                    Path = pathPrefix + "collection",
                    Expected = "boolean",
                    Actual = collectionToken.Type.ToString().ToLowerInvariant()
                });
            }

            if (dimensions.HasValue && dimensions.Value >= 1 && dimensions.Value <= 2 &&
                collectionToken?.Type == JTokenType.Boolean && collectionToken.Value<bool>())
            {
                violations.Add(new Violation
                {
                    Path = pathPrefix + "collection",
                    Expected = "false when dimensions are set",
                    Actual = "true"
                });
            }
        }

        // ── Messages ──────────────────────────────────────────────────────────

        private const int MaxQuotedValueLength = 80;
        private const int MaxListedAcceptedArguments = 40;

        private static List<string> DeclaredArgumentNames(JObject properties)
            => properties.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        /// <summary>
        /// One sentence per violation that names the field, the value that was
        /// received and what is accepted. This text is also placed in
        /// <c>error.message</c>: several MCP clients surface only that field, so it
        /// cannot be left as boilerplate (issue #655).
        /// </summary>
        public static string Describe(Violation violation)
        {
            string path = violation.Path;
            if (violation.Actual == "missing")
                return $"required argument '{path}' is missing (expected {violation.Expected}).";

            if (violation.Actual == "not applicable")
                return $"'{path}' = {violation.Received} is not used by this action; it applies only with {violation.Expected}.";

            if (violation.Actual == "unknown argument")
            {
                string text = $"'{path}' is not an accepted argument";
                if (!string.IsNullOrEmpty(violation.Suggestion))
                    text += $" (did you mean '{violation.Suggestion}'?)";
                if (violation.Accepted is { Count: > 0 })
                {
                    var listed = violation.Accepted.Take(MaxListedAcceptedArguments).ToList();
                    text += "; accepted arguments: " + string.Join(", ", listed)
                        + (violation.Accepted.Count > listed.Count ? ", ..." : "");
                }
                return text + ".";
            }

            // A type mismatch carries compact JSON (quotes/brackets included); an enum or
            // range violation carries bare text, which is quoted so "none" reads as a value.
            string received = violation.Received ?? "'" + violation.Actual + "'";
            if (received.Length > MaxQuotedValueLength)
                received = received.Substring(0, MaxQuotedValueLength) + "...";
            string described = violation.Received != null
                ? $"'{path}' = {received} is {violation.Actual}, not valid; expected {violation.Expected}."
                : $"'{path}' = {received} is not valid; expected {violation.Expected}.";
            if (!string.IsNullOrWhiteSpace(violation.Description) && violation.Received != null)
                described += $" '{path}' means: {TrimDescription(violation.Description)}";
            if (!string.IsNullOrEmpty(violation.Suggestion))
                described += $" Did you mean '{violation.Suggestion}'?";
            return described;
        }

        private const int MaxFieldDescriptionLength = 160;

        private static string TrimDescription(string description)
        {
            string text = description.Trim();
            return text.Length <= MaxFieldDescriptionLength ? text : text.Substring(0, MaxFieldDescriptionLength) + "...";
        }

        /// <summary>All violations of one call, in order, as a single readable string.</summary>
        public static string Describe(IEnumerable<Violation> violations)
            => string.Join(" ", violations.Select(v => Describe(v)));

        // ── Argument normalisation ────────────────────────────────────────────

        // Declared as a comma-separated string in the schema; the array spelling of
        // the same list is an everyday client guess (issue #652), so it is accepted.
        private static readonly HashSet<(string Tool, string Argument)> StringListAsCommaSeparated =
            new()
            {
                ("genexus_search_source", "objectName"),
            };

        /// <summary>
        /// Joins a non-empty array of strings into the comma-separated string the
        /// schema declares, for the (tool, argument) pairs that document that form.
        /// Anything else is left untouched so <see cref="Validate"/> reports it.
        /// </summary>
        public static void NormalizeStringListArguments(string toolName, JObject? args)
        {
            if (args == null) return;
            foreach (var (tool, argument) in StringListAsCommaSeparated)
            {
                if (!string.Equals(tool, toolName, StringComparison.OrdinalIgnoreCase)) continue;
                if (args[argument] is not JArray list || list.Count == 0) continue;
                if (!list.All(item => item.Type == JTokenType.String
                        && !string.IsNullOrWhiteSpace(item.ToString()))) continue;
                args[argument] = string.Join(",", list.Select(item => item.ToString().Trim()));
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool IsTypeMatch(JToken value, string declaredType) => declaredType switch
        {
            "string" => value.Type == JTokenType.String,
            "integer" => value.Type == JTokenType.Integer,
            "number" => value.Type == JTokenType.Integer || value.Type == JTokenType.Float,
            "boolean" => value.Type == JTokenType.Boolean,
            "array" => value.Type == JTokenType.Array,
            "object" => value.Type == JTokenType.Object,
            _ => true // unknown types pass
        };

        private static JObject? GetSchema(string toolName)
        {
            if (_schemaCache.TryGetValue(toolName, out var cached))
                return cached;

            EnsureToolDefsLoaded();
            if (_toolDefs == null)
            {
                _schemaCache[toolName] = null;
                return null;
            }

            var tool = _toolDefs.OfType<JObject>()
                .FirstOrDefault(t => string.Equals(t["name"]?.ToString(), toolName, StringComparison.OrdinalIgnoreCase));

            var schema = tool?["inputSchema"] as JObject;

            // Only cache if the schema has something to validate
            JObject? result = null;
            if (schema != null && (schema["required"] != null || schema["additionalProperties"] != null))
            {
                result = schema;
            }
            else if (schema != null && schema["properties"] != null)
            {
                // Still need the schema for type/enum checks on declared properties
                result = schema;
            }

            _schemaCache[toolName] = result;
            return result;
        }

        private static void EnsureToolDefsLoaded()
        {
            if (_toolDefs != null) return;
            lock (_loadLock)
            {
                if (_toolDefs != null) return;
                string? path = ToolDefinitionsLocator.Locate();
                if (path == null) return;
                try
                {
                    _toolDefs = JArray.Parse(File.ReadAllText(path));
                    ToolSchemaCompatibility.Apply(_toolDefs);
                }
                catch
                {
                    // Silently fail — validation becomes a no-op if the file can't be read
                }
            }
        }

                /// <summary>
        /// Test-only: prime the cache with a given schema so tests don't need a file on disk.
        /// </summary>
        internal static void PrimeCache(string toolName, JObject? schema)
        {
            _schemaCache[toolName] = schema;
        }

        /// <summary>
        /// Test-only: clear the schema cache.
        /// </summary>
        internal static void ClearCache()
        {
            _schemaCache.Clear();
            lock (_loadLock) { _toolDefs = null; }
        }
    }
}
