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
        private static readonly ConcurrentDictionary<string, CompiledSchema?> _compiledSchemaCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ISet<string>> _vocabularyCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _loadLock = new();
        private static JArray? _toolDefs;
        private static readonly ValidationResult OkResult = new() { Ok = true };

        private sealed class CompiledProperty
        {
            public string? DeclaredType { get; init; }
            public HashSet<string>? EnumSet { get; init; }
            public string[]? EnumValues { get; init; }
            public string? EnumExpectedText { get; init; }
            public string? Description { get; init; }
        }

        private sealed class CompiledSchema
        {
            public required (string Name, string ExpectedType)[] Required { get; init; }
            public required bool AdditionalPropertiesFalse { get; init; }
            public required Dictionary<string, CompiledProperty>? Properties { get; init; }
            public required string[] DeclaredNames { get; init; }
        }

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
            var compiled = GetCompiledSchema(toolName);
            if (compiled == null)
                return OkResult;

            List<Violation>? violations = null;
            List<Violation> EnsureViolations() => violations ??= new List<Violation>();

            // 1. Required fields must be present
            var required = compiled.Required;
            for (int i = 0; i < required.Length; i++)
            {
                var (req, expectedType) = required[i];
                if (args == null || args[req] == null)
                {
                    EnsureViolations().Add(new Violation
                    {
                        Path = req,
                        Expected = expectedType,
                        Actual = "missing"
                    });
                }
            }

            var properties = compiled.Properties;
            if (args != null && properties != null)
            {
                bool additionalPropertiesFalse = compiled.AdditionalPropertiesFalse;
                for (JToken? child = args.First; child != null; child = child.Next)
                {
                    if (child is not JProperty prop) continue;
                    if (!properties.TryGetValue(prop.Name, out var propSchema))
                    {
                        // 2. additionalProperties: false — reject unknown keys
                        if (additionalPropertiesFalse)
                        {
                            if (CrossCuttingArgs.Contains(prop.Name))
                                continue;

                            string? keySuggestion = DidYouMean.Suggest(prop.Name, compiled.DeclaredNames);

                            EnsureViolations().Add(new Violation
                            {
                                Path = prop.Name,
                                Expected = "none (additionalProperties: false)",
                                Actual = "unknown argument",
                                Suggestion = keySuggestion,
                                Accepted = compiled.DeclaredNames
                            });
                            continue;
                        }

                        // Unknown key on a tool without additionalProperties:false.
                        // Fail loud ONLY when it's a high-confidence typo of a
                        // declared property (edit distance <= 2) — that can't be a
                        // legitimate undeclared passthrough arg, so it's almost
                        // certainly a slip. Cross-cutting args are never flagged.
                        if (!CrossCuttingArgs.Contains(prop.Name))
                        {
                            string? keySuggestion = DidYouMean.Suggest(prop.Name, compiled.DeclaredNames);
                            if (keySuggestion != null)
                            {
                                EnsureViolations().Add(new Violation
                                {
                                    Path = prop.Name,
                                    Expected = "a known argument",
                                    Actual = "unknown argument",
                                    Suggestion = keySuggestion,
                                    Accepted = compiled.DeclaredNames
                                });
                            }
                        }
                        continue;
                    }

                    var propValue = prop.Value;
                    if (propValue.Type == JTokenType.Null)
                        continue;

                    // 3. Type check
                    if (propSchema.DeclaredType != null)
                    {
                        if (!IsTypeMatch(propValue, propSchema.DeclaredType))
                        {
                            EnsureViolations().Add(new Violation
                            {
                                Path = prop.Name,
                                Expected = propSchema.DeclaredType,
                                Actual = propValue.Type.ToString().ToLowerInvariant(),
                                Received = propValue.ToString(Newtonsoft.Json.Formatting.None),
                                Description = propSchema.Description
                            });
                            continue; // don't also check enum if type is wrong
                        }
                    }

                    // 4. Enum membership
                    if (propSchema.EnumSet != null)
                    {
                        string val = propValue is JValue jv && jv.Value is string s ? s : propValue.ToString();
                        if (!propSchema.EnumSet.Contains(val))
                        {
                            EnsureViolations().Add(new Violation
                            {
                                Path = prop.Name,
                                Expected = propSchema.EnumExpectedText!,
                                Actual = val,
                                Suggestion = DidYouMean.Suggest(val, propSchema.EnumValues!)
                            });
                        }
                    }
                }
            }

            if (string.Equals(toolName, "genexus_variable", StringComparison.OrdinalIgnoreCase))
            {
                var varViolations = violations ?? new List<Violation>();
                ValidateVariableDimensions(args, varViolations);
                if (varViolations.Count > 0) violations = varViolations;
            }
            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                if (args?["verification"] is JToken verification && verification.Type != JTokenType.Null)
                {
                    ValidateLifecycleActionArguments(args, EnsureViolations());
                }
            }

            if (violations == null || violations.Count == 0)
                return OkResult;

            return new ValidationResult
            {
                Ok = false,
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

        // Words a tool's schema declares at any depth: property names and enum values. They are the
        // product's vocabulary (actions, parts, modes), never a user's KB, so genexus_doctor's issue
        // walkthrough keeps them while it replaces every other argument value with a token.
        internal static ISet<string> SchemaVocabulary(string toolName) =>
            _vocabularyCache.GetOrAdd(toolName, name =>
            {
                var words = new HashSet<string>(CrossCuttingArgs, StringComparer.OrdinalIgnoreCase);
                if (GetSchema(name) is JObject schema) CollectVocabulary(schema, words);
                return words;
            });

        private static void CollectVocabulary(JToken node, HashSet<string> words)
        {
            if (node is JArray items)
            {
                foreach (var item in items) CollectVocabulary(item, words);
                return;
            }
            if (node is not JObject schema) return;
            if (schema["properties"] is JObject properties)
                foreach (var property in properties.Properties()) words.Add(property.Name);
            if (schema["enum"] is JArray values)
                foreach (var value in values.Where(v => v.Type == JTokenType.String)) words.Add(value.ToString());
            // Examples hold sample object names, not vocabulary.
            foreach (var child in schema.Properties().Where(p => p.Name != "examples"))
                CollectVocabulary(child.Value, words);
        }

        private static CompiledSchema? GetCompiledSchema(string toolName)
        {
            if (_compiledSchemaCache.TryGetValue(toolName, out var cached))
                return cached;

            var schema = GetSchema(toolName);
            var compiled = CompileSchema(schema);
            _compiledSchemaCache[toolName] = compiled;
            return compiled;
        }

        private static CompiledSchema? CompileSchema(JObject? schema)
        {
            if (schema == null) return null;

            var propertiesObj = schema["properties"] as JObject;
            var requiredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requiredList = new List<(string Name, string ExpectedType)>();
            if (schema["required"] is JArray reqArray)
            {
                foreach (var token in reqArray)
                {
                    string req = token.ToString();
                    if (requiredSet.Add(req))
                    {
                        string expectedType = (propertiesObj?[req] as JObject)?["type"]?.ToString() ?? "value";
                        requiredList.Add((req, expectedType));
                    }
                }
            }

            Dictionary<string, CompiledProperty>? compiledProps = null;
            string[] declaredNames = Array.Empty<string>();
            if (propertiesObj != null)
            {
                compiledProps = new Dictionary<string, CompiledProperty>(propertiesObj.Count, StringComparer.Ordinal);
                var names = new List<string>(propertiesObj.Count);
                foreach (var prop in propertiesObj.Properties())
                {
                    names.Add(prop.Name);
                    if (prop.Value is not JObject propSchema) continue;

                    string? declaredType = propSchema["type"]?.ToString();
                    string? description = propSchema["description"]?.ToString();
                    HashSet<string>? enumSet = null;
                    string[]? enumValues = null;
                    string? enumExpectedText = null;
                    if (propSchema["enum"] is JArray enumArr)
                    {
                        enumValues = new string[enumArr.Count];
                        enumSet = new HashSet<string>(enumArr.Count, StringComparer.Ordinal);
                        for (int i = 0; i < enumArr.Count; i++)
                        {
                            string v = enumArr[i].ToString();
                            enumValues[i] = v;
                            enumSet.Add(v);
                        }
                        enumExpectedText = "one of [" + string.Join(", ", enumValues) + "]";
                    }

                    compiledProps[prop.Name] = new CompiledProperty
                    {
                        DeclaredType = declaredType,
                        EnumSet = enumSet,
                        EnumValues = enumValues,
                        EnumExpectedText = enumExpectedText,
                        Description = description
                    };
                }
                declaredNames = names.ToArray();
            }

            return new CompiledSchema
            {
                Required = requiredList.ToArray(),
                AdditionalPropertiesFalse = schema["additionalProperties"]?.ToObject<bool?>() == false,
                Properties = compiledProps,
                DeclaredNames = declaredNames
            };
        }

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
                _toolDefs = ToolDefinitionsLocator.GetOrLoadDefinitions(path);
            }
        }

                /// <summary>
        /// Test-only: prime the cache with a given schema so tests don't need a file on disk.
        /// </summary>
        internal static void PrimeCache(string toolName, JObject? schema)
        {
            _schemaCache[toolName] = schema;
            _compiledSchemaCache[toolName] = CompileSchema(schema);
            _vocabularyCache.TryRemove(toolName, out _);
        }

        /// <summary>
        /// Test-only: clear the schema cache.
        /// </summary>
        internal static void ClearCache()
        {
            _schemaCache.Clear();
            _compiledSchemaCache.Clear();
            _vocabularyCache.Clear();
            lock (_loadLock) { _toolDefs = null; }
            ToolDefinitionsLocator.ClearCache();
        }
    }
}
