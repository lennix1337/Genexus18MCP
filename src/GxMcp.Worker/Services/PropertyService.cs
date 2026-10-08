using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Common.Properties;
using Artech.Genexus.Common.Parts;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class PropertyService
    {
        private readonly ObjectService _objectService;

        // Small TTL cache for GetProperties — cold path on certain object kinds
        // (Domain, External Object) hits 3s the first time because the SDK lazily
        // hydrates property definitions via reflection. Subsequent reads of the
        // same object usually want the same envelope, so we cache by GUID for a
        // few seconds. SetProperty invalidates the entry explicitly.
        private static readonly Dictionary<string, (DateTime expiresAt, JObject propsResult)> _propertyCache
            = new Dictionary<string, (DateTime, JObject)>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _propertyCacheLock = new object();
        private const int PropertyCacheTtlSeconds = 30;

        /// <summary>
        /// The properties a caller always gets, whatever projection it asked for.
        /// </summary>
        internal static readonly HashSet<string> MinimalProjectionPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Name",
            "Description",
            "DescriptionValue",
            "Type",
            "DataType",
            "DataTypeString",
            "Length",
            "AttMaxLen",
            "Decimals",
            "AttDec",
            "Signed",
            "AttSign",
            "Picture",
            "ATT_PICTURE",
            "Domain",
            "BasedOn",
            "DomainBasedOn",
            "DomainDefinition",
            "RPT_TEXT_MODE"
        };

        /// <summary>
        /// The minimal set plus the standard object, domain and control metadata.
        ///
        /// It used to spell the minimal nineteen out a second time under a
        /// "// Minimal set" comment marking where the copy ended. That comment
        /// asserted a containment nothing enforced: adding a name to
        /// <see cref="MinimalProjectionPropertyNames"/> and not to this list would
        /// leave a property that every projection returns and the standard
        /// projection does not, which shows up as a field missing from one shape of
        /// the same object rather than as an error. The set is now built from the
        /// minimal one, so the relationship holds by construction.
        /// </summary>
        internal static readonly HashSet<string> StandardProjectionPropertyNames = BuildStandardProjectionPropertyNames();

        private static HashSet<string> BuildStandardProjectionPropertyNames()
        {
            return new HashSet<string>(MinimalProjectionPropertyNames, StringComparer.OrdinalIgnoreCase)
            {
                "IsNullable",
                "Nullable",
                "ALLOWNULL",
                "Autonumber",
                "Collection",
                "AttCollection",
                "Title",
                "Caption",
                "Module",
                "Parent",
                "Prefix",
                "ControlValues",
                "Values",
                "EnumValues",
                "ValidationFailedText",
                "Help",
                "Theme",
                "MasterPage",
                "Folder",
                "ExternalName",
                "ExternalNamespace",
                "CommitOnExit",
                "Protocol",
                "ExposeAsWebService",
                "SOAP",
                "REST",
                "ConnectivitySupport",
                "WebNotification",
                "WebUserExperience",
                "FormClass",
                "DefaultSelected",
                "Visible",
                "Enabled",
                "Class"
            };
        }

        public PropertyService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        private static string CacheKey(KBObject obj, string controlName)
        {
            string guid;
            try { guid = obj.Guid.ToString(); } catch { guid = obj.Name ?? "?"; }
            return guid + "|" + (controlName ?? "");
        }

        internal static void InvalidatePropertyCache(KBObject obj)
        {
            if (obj == null) return;
            string guid;
            try { guid = obj.Guid.ToString(); } catch { return; }
            lock (_propertyCacheLock)
            {
                var keys = _propertyCache.Keys.Where(k => k.StartsWith(guid + "|", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var k in keys) _propertyCache.Remove(k);
            }
        }

        public string GetProperties(
            string target,
            string controlName = null,
            string typeFilter = null,
            string propertyName = null,
            IEnumerable<string> propertyNames = null,
            string projection = null,
            string query = null,
            bool reconcileTimedOutWrite = false)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Check the target name and that the KB is open.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")), target: target);

                if (reconcileTimedOutWrite)
                {
                    obj = _objectService.FindObjectFreshByIdentity(obj);
                    if (obj == null)
                        return Models.McpResponse.Err(code: "FreshReadUnavailable",
                            message: "The persisted object could not be read independently; the timeout recovery fence remains in place.",
                            target: target);
                }

                string ck = CacheKey(obj, controlName);
                JObject fullPropsResult = null;
                if (!reconcileTimedOutWrite) lock (_propertyCacheLock)
                {
                    if (_propertyCache.TryGetValue(ck, out var hit) && hit.expiresAt > DateTime.UtcNow)
                        fullPropsResult = (JObject)hit.propsResult.DeepClone();
                }

                if (fullPropsResult == null)
                {
                    dynamic container = obj;
                    if (!string.IsNullOrEmpty(controlName))
                    {
                        container = FindControl(obj, controlName);
                        if (container == null) return Models.McpResponse.Err(code: "ControlNotFound", message: $"Control '{controlName}' not found in {obj.Name}.", hint: "Use genexus_inspect to list controls available in this object's layout.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Returns the layout controls for this object.")), target: target);
                    }

                    var selectedRecoveryNames = reconcileTimedOutWrite
                        ? (propertyNames ?? Enumerable.Empty<string>())
                            .Concat((propertyName ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                            .Select(name => name.Trim())
                            .Where(name => name.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                        : Array.Empty<string>();
                    fullPropsResult = selectedRecoveryNames.Length > 0
                        ? SerializeSelectedProperties(container, obj.Model, selectedRecoveryNames,
                            string.IsNullOrEmpty(controlName) && IsWebPanel(obj))
                        : SerializeProperties(container, obj.Model);
                    if (reconcileTimedOutWrite && string.IsNullOrEmpty(controlName)
                        && IsWebPanel(obj)
                        && !((JArray)fullPropsResult["properties"]).OfType<JObject>().Any(p =>
                            string.Equals(p["name"]?.ToString(), "MainProgram", StringComparison.OrdinalIgnoreCase)))
                    {
                        dynamic isMain = ResolvePropertyEntry(obj, "IsMain");
                        if (isMain != null)
                        {
                            ((JArray)fullPropsResult["properties"]).Add(new JObject
                            {
                                ["name"] = "MainProgram", ["nativeName"] = "IsMain",
                                ["value"] = RenderPropertyValue((object)isMain.Value, obj.Model),
                                ["type"] = "System.Boolean", ["readOnly"] = false
                            });
                        }
                        else
                        {
                            var mainProgram = obj.GetType().GetProperty("MainProgram");
                            if (mainProgram != null && mainProgram.CanRead)
                            {
                                try
                                {
                                    ((JArray)fullPropsResult["properties"]).Add(new JObject
                                    {
                                        ["name"] = "MainProgram",
                                        ["value"] = RenderPropertyValue(mainProgram.GetValue(obj, null), obj.Model),
                                        ["type"] = mainProgram.PropertyType.FullName,
                                        ["readOnly"] = !mainProgram.CanWrite
                                    });
                                }
                                catch { }
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(controlName))
                    {
                        var reportContainer = ResolveReportPropertyContainer(obj, ReportLayoutHelper.TextModeProperty, null);
                        var textMode = ReflectionHelper.TryGetPropertyBagValue(reportContainer, ReportLayoutHelper.TextModeProperty);
                        if (textMode != null)
                            ((JArray)fullPropsResult["properties"]).Add(new JObject
                            {
                                ["name"] = ReportLayoutHelper.TextModeProperty, ["value"] = textMode.ToString(),
                                ["type"] = "System.Boolean", ["readOnly"] = false
                            });
                    }
                    if (!reconcileTimedOutWrite) lock (_propertyCacheLock)
                    {
                        _propertyCache[ck] = (DateTime.UtcNow.AddSeconds(PropertyCacheTtlSeconds), (JObject)fullPropsResult.DeepClone());
                    }
                }

                string versionToken = null;
                try { versionToken = WriteService.ComputeVersionToken(obj); } catch { }

                string shaped = ShapeGetPropertiesResult(
                    fullPropsResult,
                    target,
                    controlName,
                    propertyName,
                    propertyNames,
                    projection,
                    versionToken,
                    query);
                if (!reconcileTimedOutWrite) return shaped;
                var authoritative = JObject.Parse(shaped);
                if (authoritative["result"] is JObject result)
                {
                    result["authoritativeRead"] = true;
                    if (result["missingProperties"] is JArray missing && missing.Any(p =>
                        string.Equals(p?.ToString(), "MainProgram", StringComparison.OrdinalIgnoreCase)))
                        result["unsupportedProperties"] = new JObject
                        {
                            ["MainProgram"] = "The GeneXus SDK exposes neither MainProgram nor its WebPanel IsMain property-bag entry nor a readable typed member. Its persisted value cannot be confirmed."
                        };
                }
                else if (authoritative["error"] is JObject error
                    && string.Equals(error["code"]?.ToString(), "PropertyNotFound", StringComparison.OrdinalIgnoreCase)
                    && (propertyNames ?? Enumerable.Empty<string>()).Concat(new[] { propertyName })
                        .Any(name => string.Equals(name?.Trim(), "MainProgram", StringComparison.OrdinalIgnoreCase)))
                    error["hint"] = "The GeneXus SDK exposes neither MainProgram nor its WebPanel IsMain property-bag entry nor a readable typed member; its persisted value cannot be confirmed.";
                return authoritative.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string GetPropertiesBatch(
            JArray targets,
            string controlName = null,
            string typeFilter = null,
            string propertyName = null,
            IEnumerable<string> propertyNames = null,
            string projection = null,
            string query = null)
        {
            return ShapeGetPropertiesBatchResult(
                targets,
                (name, itemType) => GetProperties(
                    name,
                    controlName,
                    string.IsNullOrWhiteSpace(itemType) ? typeFilter : itemType,
                    propertyName,
                    propertyNames,
                    projection,
                    query));
        }

        internal static string ShapeGetPropertiesBatchResult(
            JArray targets,
            Func<string, string, string> getProperties,
            int maxTargets = 100)
        {
            if (targets == null || targets.Count == 0)
                return Models.McpResponse.Err(
                    code: "InvalidBatchTargets",
                    message: "Pass a non-empty targets array.",
                    hint: "Use action=get with targets containing objects of {name, type?}.");
            if (maxTargets < 1)
                return Models.McpResponse.Err(
                    code: "InvalidBatchLimit",
                    message: "The batch target limit must be greater than zero.");

            int processedCount = Math.Min(targets.Count, maxTargets);
            int errorCount = 0;
            var results = new JArray();
            for (int index = 0; index < processedCount; index++)
            {
                JObject item = new JObject { ["index"] = index };
                if (targets[index] is not JObject target || string.IsNullOrWhiteSpace(target["name"]?.ToString()))
                {
                    item["status"] = "error";
                    item["error"] = new JObject
                    {
                        ["code"] = "InvalidTarget",
                        ["message"] = "Each target must be an object with a non-empty name."
                    };
                    results.Add(item);
                    errorCount++;
                    continue;
                }

                string name = target["name"].ToString().Trim();
                string type = target["type"]?.ToString();
                item["name"] = name;
                if (!string.IsNullOrWhiteSpace(type)) item["type"] = type;

                JObject response;
                try
                {
                    response = JObject.Parse(getProperties(name, type));
                }
                catch
                {
                    item["status"] = "error";
                    item["error"] = new JObject
                    {
                        ["code"] = "InvalidWorkerResponse",
                        ["message"] = "The property read returned an invalid response."
                    };
                    results.Add(item);
                    errorCount++;
                    continue;
                }

                if (string.Equals(response["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase) &&
                    response["result"] is JObject result)
                {
                    item["status"] = "ok";
                    if (result["values"] is JObject values) item["values"] = values.DeepClone();
                    if (result["missingProperties"] is JArray missing) item["missingProperties"] = missing.DeepClone();
                    if (result["versionToken"] != null) item["versionToken"] = result["versionToken"].DeepClone();
                    results.Add(item);
                    continue;
                }

                JObject responseError = response["error"] as JObject;
                string errorMessage = responseError?["message"]?.ToString()
                    ?? response["message"]?.ToString()
                    ?? response["error"]?.ToString()
                    ?? "Property read failed.";
                JObject error = responseError != null ? (JObject)responseError.DeepClone() : new JObject();
                if (error["code"] == null)
                    error["code"] = response["code"]?.ToString() ?? "PropertiesReadFailed";
                if (error["message"] == null) error["message"] = errorMessage;
                item["status"] = "error";
                item["error"] = error;
                results.Add(item);
                errorCount++;
            }

            bool truncated = targets.Count > processedCount;
            var resultEnvelope = new JObject
            {
                ["requestedCount"] = targets.Count,
                ["processedCount"] = processedCount,
                ["errorCount"] = errorCount,
                ["truncated"] = truncated,
                ["results"] = results
            };

            if (errorCount == 0 && !truncated)
                return Models.McpResponse.Ok(code: "PropertiesBatchRead", result: resultEnvelope);

            var warnings = new JArray();
            if (errorCount > 0) warnings.Add($"{errorCount} target(s) failed; see results[].error.");
            if (truncated) warnings.Add($"Only the first {processedCount} of {targets.Count} targets were read.");
            return Models.McpResponse.Partial(
                target: null,
                code: "PropertiesBatchRead",
                result: resultEnvelope,
                warnings: warnings);
        }

        internal static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            int lenA = a.Length;
            int lenB = b.Length;
            var dp = new int[lenA + 1, lenB + 1];
            for (int i = 0; i <= lenA; i++) dp[i, 0] = i;
            for (int j = 0; j <= lenB; j++) dp[0, j] = j;
            for (int i = 1; i <= lenA; i++)
            {
                char ca = char.ToLowerInvariant(a[i - 1]);
                for (int j = 1; j <= lenB; j++)
                {
                    char cb = char.ToLowerInvariant(b[j - 1]);
                    int cost = ca == cb ? 0 : 1;
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
                }
            }
            return dp[lenA, lenB];
        }

        internal static List<string> FindPropertySuggestions(string targetProp, IEnumerable<string> allCandidateNames, int maxSuggestions = 3)
        {
            if (string.IsNullOrWhiteSpace(targetProp) || allCandidateNames == null)
                return new List<string>();

            string cleaned = targetProp.Trim();
            var scored = new List<(string name, int score)>();

            foreach (var cand in allCandidateNames)
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                string candTrimmed = cand.Trim();

                if (string.Equals(cleaned, candTrimmed, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Prefix match (candidate starts with input or input starts with candidate)
                if (candTrimmed.StartsWith(cleaned, StringComparison.OrdinalIgnoreCase))
                {
                    scored.Add((candTrimmed, 1));
                    continue;
                }
                if (cleaned.StartsWith(candTrimmed, StringComparison.OrdinalIgnoreCase))
                {
                    scored.Add((candTrimmed, 2));
                    continue;
                }

                // Substring match
                if (candTrimmed.IndexOf(cleaned, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    scored.Add((candTrimmed, 3));
                    continue;
                }

                // Levenshtein distance
                int dist = Levenshtein(cleaned, candTrimmed);
                int maxAllowedDist = Math.Max(2, cleaned.Length / 2);
                if (dist <= maxAllowedDist)
                {
                    scored.Add((candTrimmed, 10 + dist));
                }
            }

            return scored
                .OrderBy(s => s.score)
                .ThenBy(s => s.name.Length)
                .Select(s => s.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxSuggestions)
                .ToList();
        }

        internal static Func<string, bool> BuildPropertyMatcher(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return _ => true;

            if (pattern.Contains("*") || pattern.Contains("?"))
            {
                string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                var rx = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return propName => !string.IsNullOrEmpty(propName) && rx.IsMatch(propName);
            }

            return propName => !string.IsNullOrEmpty(propName) && propName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool MatchesWildcardOrQuery(string propName, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (string.IsNullOrEmpty(propName)) return false;
            return BuildPropertyMatcher(pattern)(propName);
        }

        internal static string ShapeGetPropertiesResult(
            JObject fullPropsResult,
            string target,
            string controlName = null,
            string propertyName = null,
            IEnumerable<string> propertyNames = null,
            string projection = null,
            string versionToken = null,
            string query = null)
        {
            var props = fullPropsResult?["properties"] as JArray ?? new JArray();
            List<string> allPropNames = null;
            List<string> GetAllPropNames() => allPropNames ?? (allPropNames = props
                .Select(p => p["name"]?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList());

            var requested = new List<string>();
            if (!string.IsNullOrWhiteSpace(propertyName))
            {
                var parts = propertyName.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !requested.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        requested.Add(trimmed);
                    }
                }
            }
            if (propertyNames != null)
            {
                foreach (var name in propertyNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var trimmed = name.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !requested.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                        {
                            requested.Add(trimmed);
                        }
                    }
                }
            }

            string entityDesc = string.IsNullOrEmpty(controlName) ? $"'{target}'" : $"control '{controlName}' in '{target}'";

            // Query / Wildcard mode
            bool isWildcardProperty = requested.Count == 1 && (requested[0].Contains("*") || requested[0].Contains("?"));
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            if (hasQuery || isWildcardProperty)
            {
                string searchPattern = hasQuery ? query.Trim() : requested[0].Trim();
                var matchedProps = new JArray();
                var valuesMap = new JObject();
                var matcher = BuildPropertyMatcher(searchPattern);

                foreach (JObject p in props)
                {
                    var n = p["name"]?.ToString();
                    if (matcher(n))
                    {
                        matchedProps.Add((JObject)p.DeepClone());
                        if (!string.IsNullOrEmpty(n) && valuesMap[n] == null)
                        {
                            valuesMap[n] = p["value"]?.ToString() ?? "";
                        }
                    }
                }

                if (matchedProps.Count == 0)
                {
                    string cleanSearch = searchPattern.Trim('*', '?');
                    var suggestions = FindPropertySuggestions(cleanSearch, GetAllPropNames(), 3);
                    var nextSteps = new JArray();
                    foreach (var sug in suggestions.Take(2))
                    {
                        var stepArgs = new JObject { ["action"] = "get", ["name"] = target, ["propertyName"] = sug };
                        if (!string.IsNullOrEmpty(controlName)) stepArgs["control"] = controlName;
                        nextSteps.Add(Models.McpResponse.NextStep("genexus_properties", stepArgs, $"Read closest matching property '{sug}'."));
                    }
                    nextSteps.Add(Models.McpResponse.NextStep("genexus_properties", new JObject { ["action"] = "get", ["name"] = target, ["projection"] = "minimal" }, "View common properties using minimal projection."));

                    string msg = $"No properties matching '{searchPattern}' found on {entityDesc}.";
                    if (suggestions.Count > 0)
                    {
                        msg += $" Did you mean: '{string.Join("', '", suggestions)}'?";
                    }
                    var errorExtra = suggestions.Count > 0 ? new JObject { ["didYouMean"] = new JArray(suggestions) } : null;

                    return Models.McpResponse.Err(
                        code: "PropertyNotFound",
                        message: msg,
                        hint: "Use projection=minimal to view common properties or omit query/wildcard to list all properties.",
                        nextSteps: nextSteps,
                        target: target,
                        errorExtra: errorExtra);
                }

                var queryResult = new JObject
                {
                    ["query"] = searchPattern,
                    ["count"] = matchedProps.Count,
                    ["values"] = valuesMap,
                    ["properties"] = matchedProps
                };
                if (!string.IsNullOrEmpty(versionToken))
                {
                    queryResult["versionToken"] = versionToken;
                }
                return Models.McpResponse.Ok(target: target, code: "PropertiesRead", result: queryResult);
            }

            // Single property mode
            if (requested.Count == 1)
            {
                string targetPropName = requested[0];
                JObject matched = null;
                foreach (JObject p in props)
                {
                    var n = p["name"]?.ToString();
                    if (string.Equals(n, targetPropName, StringComparison.OrdinalIgnoreCase) ||
                        IsDomainReadAlias(targetPropName, n))
                    {
                        matched = p;
                        break;
                    }
                }

                if (matched == null)
                {
                    var suggestions = FindPropertySuggestions(targetPropName, GetAllPropNames(), 3);
                    var nextSteps = new JArray();
                    foreach (var sug in suggestions.Take(2))
                    {
                        var stepArgs = new JObject { ["action"] = "get", ["name"] = target, ["propertyName"] = sug };
                        if (!string.IsNullOrEmpty(controlName)) stepArgs["control"] = controlName;
                        nextSteps.Add(Models.McpResponse.NextStep("genexus_properties", stepArgs, $"Read closest matching property '{sug}'."));
                    }
                    nextSteps.Add(Models.McpResponse.NextStep("genexus_properties", new JObject { ["action"] = "get", ["name"] = target, ["projection"] = "minimal" }, "View common properties using minimal projection."));

                    string msg = $"Property '{targetPropName}' not found on {entityDesc}.";
                    if (suggestions.Count > 0)
                    {
                        msg += $" Did you mean: '{string.Join("', '", suggestions)}'?";
                    }
                    var errorExtra = suggestions.Count > 0 ? new JObject { ["didYouMean"] = new JArray(suggestions) } : null;

                    return Models.McpResponse.Err(
                        code: "PropertyNotFound",
                        message: msg,
                        hint: "Call genexus_properties with action=get and no propertyName (or projection=minimal) to see available properties.",
                        nextSteps: nextSteps,
                        target: target,
                        errorExtra: errorExtra);
                }

                string matchedName = matched["name"]?.ToString() ?? targetPropName;
                string matchedValue = matched["value"]?.ToString() ?? "";
                var singleResult = new JObject
                {
                    ["propertyName"] = matchedName,
                    ["value"] = matchedValue,
                    ["values"] = new JObject { [matchedName] = matchedValue },
                    ["property"] = (JObject)matched.DeepClone(),
                    ["properties"] = new JArray { (JObject)matched.DeepClone() }
                };
                if (!string.IsNullOrEmpty(versionToken))
                {
                    singleResult["versionToken"] = versionToken;
                }
                return Models.McpResponse.Ok(target: target, code: "PropertiesRead", result: singleResult);
            }

            // Multi-property mode
            if (requested.Count > 1)
            {
                var matchedArray = new JArray();
                var valuesMap = new JObject();
                var foundRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var req in requested)
                {
                    foreach (JObject p in props)
                    {
                        var n = p["name"]?.ToString();
                        if (string.Equals(n, req, StringComparison.OrdinalIgnoreCase) ||
                            IsDomainReadAlias(req, n))
                        {
                            if (!foundRequested.Contains(req))
                            {
                                foundRequested.Add(req);
                                matchedArray.Add((JObject)p.DeepClone());
                                if (!string.IsNullOrEmpty(n) && valuesMap[n] == null)
                                {
                                    valuesMap[n] = p["value"]?.ToString() ?? "";
                                }
                            }
                            break;
                        }
                    }
                }

                if (matchedArray.Count == 0)
                {
                    var propNames = GetAllPropNames();
                    var suggestions = new List<string>();
                    foreach (var req in requested)
                    {
                        suggestions.AddRange(FindPropertySuggestions(req, propNames, 2));
                    }
                    suggestions = suggestions.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

                    var nextSteps = new JArray();
                    foreach (var sug in suggestions.Take(2))
                    {
                        var stepArgs = new JObject { ["action"] = "get", ["name"] = target, ["propertyName"] = sug };
                        if (!string.IsNullOrEmpty(controlName)) stepArgs["control"] = controlName;
                        nextSteps.Add(Models.McpResponse.NextStep("genexus_properties", stepArgs, $"Read closest matching property '{sug}'."));
                    }
                    nextSteps.Add(Models.McpResponse.NextStep("genexus_properties", new JObject { ["action"] = "get", ["name"] = target, ["projection"] = "minimal" }, "View common properties using minimal projection."));

                    string msg = $"None of the requested properties ({string.Join(", ", requested)}) were found on {entityDesc}.";
                    if (suggestions.Count > 0)
                    {
                        msg += $" Did you mean: '{string.Join("', '", suggestions)}'?";
                    }
                    var errorExtra = suggestions.Count > 0 ? new JObject { ["didYouMean"] = new JArray(suggestions) } : null;

                    return Models.McpResponse.Err(
                        code: "PropertyNotFound",
                        message: msg,
                        hint: "Call genexus_properties with action=get and no propertyName (or projection=minimal) to see available properties.",
                        nextSteps: nextSteps,
                        target: target,
                        errorExtra: errorExtra);
                }

                var multiResult = new JObject
                {
                    ["values"] = valuesMap,
                    ["properties"] = matchedArray
                };
                var missing = requested.Where(r => !foundRequested.Contains(r)).ToList();
                if (missing.Count > 0)
                {
                    multiResult["missingProperties"] = new JArray(missing);
                }
                if (!string.IsNullOrEmpty(versionToken))
                {
                    multiResult["versionToken"] = versionToken;
                }
                return Models.McpResponse.Ok(target: target, code: "PropertiesRead", result: multiResult);
            }

            // Projection mode: minimal
            if (string.Equals(projection, "minimal", StringComparison.OrdinalIgnoreCase))
            {
                return BuildProjectionResult(target, "minimal", props, MinimalProjectionPropertyNames, versionToken);
            }

            // Projection mode: standard
            if (string.Equals(projection, "standard", StringComparison.OrdinalIgnoreCase))
            {
                return BuildProjectionResult(target, "standard", props, StandardProjectionPropertyNames, versionToken);
            }

            // Full / Default mode
            var allValuesMap = new JObject();
            foreach (JObject p in props)
            {
                var n = p["name"]?.ToString();
                if (!string.IsNullOrEmpty(n) && allValuesMap[n] == null)
                {
                    allValuesMap[n] = p["value"]?.ToString() ?? "";
                }
            }
            var fullResult = (JObject)(fullPropsResult?.DeepClone() ?? new JObject { ["properties"] = new JArray() });
            fullResult["values"] = allValuesMap;
            if (!string.IsNullOrEmpty(versionToken))
            {
                fullResult["versionToken"] = versionToken;
            }
            return Models.McpResponse.Ok(target: target, code: "PropertiesRead", result: fullResult);
        }

        /// <summary>
        /// Builds a filtered <c>projection=minimal</c> or <c>projection=standard</c>
        /// read.
        ///
        /// The two were written out separately and identically, differing only in
        /// the name they echo and the set they filter by. That shape is the client
        /// contract: a caller asking for the standard projection is asking for the
        /// minimal one plus more, so both must emit the same keys -
        /// <c>projection</c>, <c>values</c>, <c>properties</c>, and
        /// <c>versionToken</c> when one was supplied. Two copies meant a key added
        /// to one was silently absent from the other.
        ///
        /// The first value seen for a name wins in <c>values</c>, matching the
        /// unfiltered path: a duplicated property name reports its first value
        /// rather than its last.
        ///
        /// The full/default read is deliberately not routed here. It does not
        /// filter, and it returns the whole cloned result rather than a
        /// reconstructed one.
        /// </summary>
        private static string BuildProjectionResult(
            string target,
            string projection,
            JArray props,
            HashSet<string> allowedNames,
            string versionToken)
        {
            var filteredProps = new JArray();
            var valuesMap = new JObject();
            foreach (JObject p in props)
            {
                var n = p["name"]?.ToString();
                if (n != null && allowedNames.Contains(n))
                {
                    filteredProps.Add((JObject)p.DeepClone());
                    if (valuesMap[n] == null)
                    {
                        valuesMap[n] = p["value"]?.ToString() ?? "";
                    }
                }
            }
            var projResult = new JObject
            {
                ["projection"] = projection,
                ["values"] = valuesMap,
                ["properties"] = filteredProps
            };
            if (!string.IsNullOrEmpty(versionToken))
            {
                projResult["versionToken"] = versionToken;
            }
            return Models.McpResponse.Ok(target: target, code: "PropertiesRead", result: projResult);
        }

        public string SetProperty(string target, string propName, string value, string controlName = null, string typeFilter = null)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Check the target name and that the KB is open.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")), target: target);

                // Folder/module placement is not a scalar property write — it's a parent
                // move. Route it to ObjectService.MoveObject (create-in-Root then reparent
                // via the Udm EntityManager). The old "not writable — Parent/Module setters
                // are no-ops" verdict was a facade-DLL decompilation artefact; see ObjectMover.
                if (string.IsNullOrEmpty(controlName) && IsObjectPlacementProperty(propName))
                {
                    string kind = propName.Trim().StartsWith("Module", StringComparison.OrdinalIgnoreCase) ? "Module"
                        : propName.Trim().StartsWith("Folder", StringComparison.OrdinalIgnoreCase) ? "Folder" : null;
                    return _objectService.MoveObject(target, value, typeFilter, kind);
                }

                // issue #49: renaming an object by setting its "Name" via the generic property
                // setter half-works — obj.Name = newName persists to the KB, but the index cache
                // is never re-keyed, so the object is unreachable under the new name afterwards
                // (get returns ObjectNotFound) and a subsequent rename can orphan the old state.
                // Renaming is a refactor (it must also patch every caller), not a scalar write.
                // Reject it and route to the operation that does it correctly.
                if (string.IsNullOrEmpty(controlName) && string.Equals(propName?.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
                {
                    return Models.McpResponse.Err(
                        code: "RenameNotViaProperties",
                        message: $"Cannot rename '{target}' by setting the 'Name' property: this only re-keys the object in memory, leaving the index stale (the object becomes unreachable under the new name) and does not update references from other objects.",
                        hint: "Use genexus_refactor action=RenameObject, which renames the object, patches its call-sites, and refreshes the index.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_refactor", new JObject { ["action"] = "RenameObject", ["target"] = target, ["newName"] = value, ["type"] = typeFilter }, "Renames the object and patches every reference to it.")),
                        target: target);
                }

                // issue #41: ControlValues is a STRUCTURED property (the static combo's
                // list of value/description pairs), not a scalar. The generic string setter
                // can't represent it, so the SDK writes an empty collection — silently
                // WIPING the existing values while the call still "succeeds". Reject up
                // front instead of destroying data and reporting PropertyApplied.
                if (IsNonScalarProperty(propName))
                {
                    return Models.McpResponse.Err(
                        code: "PropertyNotScalarWritable",
                        message: $"'{propName}' is a structured property (a list of value/description pairs), not a scalar. Setting it through genexus_properties would write an empty collection and WIPE the existing values.",
                        hint: "Edit the static values in the GeneXus IDE (the control/attribute's Control Info > Values editor). This property is not writable as a plain string through the SDK.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_properties", new JObject { ["action"] = "get", ["name"] = target, ["control"] = controlName }, "Read the current ControlValues so you don't lose them.")),
                        target: target);
                }

                // issue #48: a Data Provider's OutputSDT is a READ-ONLY derived string
                // ("OutputSDT:String {get;}") — the real output is a DataProviderOutputReference
                // set via Properties.DPRV.SetOutput. The generic string setter can't write the
                // get-only property, so it silently cleared the output while reporting success.
                // Route to the typed SDK API instead.
                if (string.IsNullOrEmpty(controlName)
                    && string.Equals(propName?.Trim(), "OutputSDT", StringComparison.OrdinalIgnoreCase))
                {
                    return SetDataProviderOutputSdt(obj, target, value);
                }

                // issue #117: Domain assignment on Attribute/Domain/Variable routes through
                // DomainBasedOn. Validate domain existence up front if non-empty.
                if (string.IsNullOrEmpty(controlName) && IsDomainPropertyName(propName) && !string.IsNullOrWhiteSpace(value))
                {
                    var domCheck = _objectService.FindObject(value.Trim(), "Domain");
                    if (domCheck == null)
                    {
                        return Models.McpResponse.Err(
                            code: "DomainNotFound",
                            message: $"Cannot set {propName}: no Domain named '{value}' was found in the Knowledge Base.",
                            hint: "Pass the name of an existing Domain (or an empty value to clear).",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", new JObject { ["type"] = "Domain", ["query"] = value }, "Lists Domains matching the name.")),
                            target: target);
                    }
                }

                dynamic container = obj;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(obj, controlName);
                    if (container == null) return Models.McpResponse.Err(code: "ControlNotFound", message: $"Control '{controlName}' not found in {obj.Name}.", hint: "Use genexus_inspect to list controls available in this object's layout.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Returns the layout controls for this object.")), target: target);
                }

                // Validate before dispatching. ValidatePropertyWrite already exempts a
                // WebPanelReference from the scalar value-shape check (it is set by object
                // name through the typed adapter below), but its read-only check runs first,
                // so returning from the reference branch ahead of this call would make every
                // read-only WebPanelReference property writable.
                object reportContainer = ResolveReportPropertyContainer(obj, propName, controlName);
                container = reportContainer ?? container;
                string propertyValidation = reportContainer != null ? ValidateReportTextMode(value) : ValidatePropertyWrite(container, propName, value);
                if (propertyValidation != null)
                    return Models.McpResponse.Err(
                        code: propertyValidation.StartsWith("PropertyReadOnly", StringComparison.Ordinal)
                            ? "PropertyReadOnly"
                            : propertyValidation.StartsWith("PropertyNotFound", StringComparison.Ordinal)
                                ? "PropertyNotFound"
                                : "InvalidPropertyValue",
                        message: propertyValidation,
                        target: target);

                if (IsWebPanelReferenceType(GetPropertyTargetType(container, propName)))
                    return SetWebPanelReferenceProperty(obj, container, target, propName, value, controlName, typeFilter);

                string beforeVal = null;
                using (var trans = obj.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        // issue #41 (general safety net): capture the prior value so a silent
                        // wipe (non-empty → empty for a non-empty request) is caught even for
                        // properties not on the explicit non-scalar list, and rolled back.
                        beforeVal = TryReadPropertyString(container, propName, obj.Model);

                        ApplyPropertyValue(container, propName, value, controlName, obj);
                        if (ReportLayoutHelper.IsTextModeProperty(propName) && string.IsNullOrEmpty(controlName))
                            ReportLayoutHelper.MarkLayoutDirty(GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Layout"));

                        string afterVal = TryReadPropertyString(container, propName, obj.Model);
                        if (!string.IsNullOrEmpty(value)
                            && !string.IsNullOrEmpty(beforeVal)
                            && string.IsNullOrEmpty(afterVal))
                        {
                            throw new PropertyWipeException(propName, beforeVal);
                        }

                        try { if (container != obj) container.Dirty = true; } catch { }
                        obj.EnsureSave();
                        trans.Commit();
                        committed = true;
                        WriteService.NotePerTargetWrite(target);
                        InvalidatePropertyCache(obj);
                    }
                    finally
                    {
                        if (!committed)
                        {
                            try { trans.Rollback(); } catch (Exception rbEx) { Logger.Warn("[PROPERTY] Rollback failed: " + rbEx.Message); }
                        }
                    }
                }

                // issue #59 — post-save persistence verification. Re-resolve the object and
                // the target container FRESH (not the mutated in-memory instance) and compare
                // the persisted value against what was requested. A mismatch returns the
                // structured PropertyNotPersisted envelope; a confirmed match attaches the
                // before/requested/persisted diff block to the success response.
                string verified = VerifyPropertyPersisted(target, propName, value, controlName, typeFilter, beforeVal, obj);
                if (verified != null) return verified;

                return Models.McpResponse.Ok(target: target, code: "PropertyApplied", result: new JObject { ["property"] = propName, ["value"] = value });
            }
            catch (PropertyWipeException pwe)
            {
                // issue #41: the write was rolled back — the property still has its prior value.
                return Models.McpResponse.Err(
                    code: "PropertyWriteWipedValue",
                    message: $"Setting '{pwe.PropertyName}' emptied a previously non-empty value; the write was rolled back to avoid data loss. This property is likely structured (not a scalar string) and can't be set through genexus_properties.",
                    hint: "The prior value is preserved. Edit this property in the GeneXus IDE. If it should be scalar-writable, report the property name.",
                    target: target);
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string SetProperties(string target, JObject properties, string controlName = null, string typeFilter = null,
            string expectedVersion = null, bool dryRun = false, bool rollbackOnFailure = true)
        {
            try
            {
                if (properties == null || properties.Count == 0)
                    return Models.McpResponse.Ok(target: target, code: "WriteNoChange", result: new JObject { ["details"] = "No properties provided." });
                if (!rollbackOnFailure)
                    return Models.McpResponse.Err(code: "RollbackRequired", target: target,
                        message: "Property writes require rollbackOnFailure=true to protect the complete object.");

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Check the target name and that the KB is open.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists available objects to verify the target name.")), target: target);

                if (!string.IsNullOrWhiteSpace(expectedVersion))
                {
                    obj = _objectService.FindObjectFreshByIdentity(obj);
                    if (obj == null)
                        return Models.McpResponse.Err(code: "FreshReadUnavailable", target: target,
                            message: "The current persisted version could not be read; no property write was attempted.");
                }

                dynamic container = obj;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(obj, controlName);
                    if (container == null) return Models.McpResponse.Err(code: "ControlNotFound", message: $"Control '{controlName}' not found in {obj.Name}.", hint: "Use genexus_inspect to list controls available in this object's layout.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Returns the layout controls for this object.")), target: target);
                }

                var beforeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string beforeVersion = WriteService.ComputeVersionToken(obj);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, beforeVersion, StringComparison.Ordinal))
                    return Models.McpResponse.Err(code: "VersionConflict", target: target,
                        message: "The object changed after the property read; no write was attempted.",
                        extra: new JObject { ["expectedVersion"] = expectedVersion, ["currentVersion"] = beforeVersion });

                var references = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var nativeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in properties.Properties())
                {
                    string propName = p.Name;
                    string nativeName = NativeWebPanelPropertyName(obj, controlName, propName);
                    nativeNames[propName] = nativeName;
                    string val = p.Value?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(controlName) && IsObjectPlacementProperty(propName))
                        return Models.McpResponse.Err(code: "InvalidPropertyBatch", target: target,
                            message: $"'{propName}' is an object move; use action=move.");
                    if (string.IsNullOrEmpty(controlName) && string.Equals(propName, "Name", StringComparison.OrdinalIgnoreCase))
                        return Models.McpResponse.Err(code: "InvalidPropertyBatch", target: target,
                            message: "Name cannot be changed by a property batch.");
                    if (IsNonScalarProperty(propName) || string.Equals(propName, "OutputSDT", StringComparison.OrdinalIgnoreCase))
                        return Models.McpResponse.Err(code: "InvalidPropertyBatch", target: target,
                            message: $"'{propName}' cannot be changed by a scalar property batch.");
                    object propertyContainer = ResolveReportPropertyContainer(obj, nativeName, controlName) ?? container;
                    string validation = ReportLayoutHelper.IsTextModeProperty(nativeName)
                        ? ValidateReportTextMode(val) : ValidatePropertyWrite((dynamic)propertyContainer, nativeName, val);
                    if (validation != null)
                        return Models.McpResponse.Err(code: "InvalidPropertyValue", target: target, message: validation);
                    if (IsWebPanelReferenceType(GetPropertyTargetType((dynamic)propertyContainer, nativeName)))
                    {
                        if (string.IsNullOrWhiteSpace(val))
                            references[propName] = global::Artech.Genexus.Common.CustomTypes.WebPanelReference.NoneRef;
                        else
                        {
                            var referenced = _objectService.FindObject(val.Trim(), "MasterPage")
                                ?? _objectService.FindObject(val.Trim(), "WebPanel");
                            if (referenced == null)
                                return Models.McpResponse.Err(code: "ReferencedObjectNotFound", target: target,
                                    message: $"No MasterPage or WebPanel named '{val}' was found.");
                            references[propName] = new global::Artech.Genexus.Common.CustomTypes.WebPanelReference(referenced.Key);
                        }
                    }
                    beforeValues[propName] = TryReadPropertyString((dynamic)propertyContainer, nativeName, obj.Model) ?? string.Empty;
                }

                ObjectMoveSnapshot snapshot;
                try { snapshot = ObjectMoveSnapshot.Capture(obj); }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(code: "PropertySnapshotFailed", target: target,
                        message: "Could not capture the complete object before editing: " + ex.Message);
                }
                if (dryRun)
                    return Models.McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                    {
                        ["before"] = JObject.FromObject(beforeValues),
                        ["requested"] = properties.DeepClone(),
                        ["versionToken"] = beforeVersion,
                        ["preservedParts"] = snapshot.PreservedParts,
                        ["persisted"] = false,
                        ["implicitOperations"] = new JArray()
                    });
                using (var trans = obj.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        var transactionObject = _objectService.FindObjectFreshByIdentity(obj);
                        if (transactionObject == null)
                            return Models.McpResponse.Err(code: "FreshReadUnavailable", target: target,
                                message: "The object could not be refreshed inside the property transaction; no write was committed.");
                        obj = transactionObject;
                        if (!string.IsNullOrEmpty(controlName))
                        {
                            container = FindControl(obj, controlName);
                            if (container == null)
                                return Models.McpResponse.Err(code: "ControlNotFound", target: target,
                                    message: "The control disappeared before the property transaction; no write was committed.");
                        }
                        else container = obj;
                        string transactionVersion = WriteService.ComputeVersionToken(obj);
                        if (!string.Equals(beforeVersion, transactionVersion, StringComparison.Ordinal))
                            return Models.McpResponse.Err(code: "VersionConflict", target: target,
                                message: "The object changed before the property transaction; no write was committed.",
                                extra: new JObject { ["expectedVersion"] = expectedVersion ?? beforeVersion, ["currentVersion"] = transactionVersion });
                        foreach (var p in properties.Properties())
                        {
                            string propName = p.Name;
                            string nativeName = nativeNames[propName];
                            string val = p.Value?.ToString();

                            if (string.IsNullOrEmpty(controlName) && IsObjectPlacementProperty(propName))
                                throw new InvalidOperationException($"'{propName}' is an object move, not a scalar property; use genexus_properties action=move.");
                            if (string.IsNullOrEmpty(controlName) && string.Equals(propName?.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException($"Cannot rename '{target}' via property batch setter.");
                            if (IsNonScalarProperty(propName))
                                throw new InvalidOperationException($"'{propName}' is a structured property and cannot be set as a scalar string.");
                            if (string.IsNullOrEmpty(controlName)
                                && string.Equals(propName?.Trim(), "OutputSDT", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("'OutputSDT' uses a typed Data Provider API and cannot be combined with a scalar property batch; use action=set with propertyName=OutputSDT.");

                            object reportContainer = ResolveReportPropertyContainer(obj, nativeName, controlName);
                            dynamic propertyContainer = reportContainer ?? container;
                            string propertyValidation = reportContainer != null ? ValidateReportTextMode(val) : ValidatePropertyWrite(propertyContainer, nativeName, val);
                            if (propertyValidation != null)
                                throw new InvalidOperationException(propertyValidation);

                            string before = TryReadPropertyString(propertyContainer, nativeName, obj.Model);
                            if (before != null) beforeValues[propName] = before;

                            if (references.TryGetValue(propName, out object reference))
                                propertyContainer.SetPropertyValue(nativeName, reference);
                            else
                                ApplyPropertyValue(propertyContainer, nativeName, val, controlName, obj);
                            if (ReportLayoutHelper.IsTextModeProperty(nativeName) && string.IsNullOrEmpty(controlName))
                                ReportLayoutHelper.MarkLayoutDirty(GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Layout"));

                            string after = TryReadPropertyString(propertyContainer, nativeName, obj.Model);
                            if (!string.IsNullOrEmpty(val) && !string.IsNullOrEmpty(before) && string.IsNullOrEmpty(after))
                            {
                                throw new PropertyWipeException(propName, before);
                            }
                        }

                        try { if (container != obj) container.Dirty = true; } catch { }
                        obj.EnsureSave();
                        trans.Commit();
                        committed = true;
                        WriteService.NotePerTargetWrite(target);
                        InvalidatePropertyCache(obj);
                    }
                    finally
                    {
                        if (!committed)
                        {
                            try { trans.Rollback(); } catch (Exception rbEx) { Logger.Warn("[PROPERTY] Rollback failed: " + rbEx.Message); }
                        }
                    }
                }

                var verifiedBefore = new JObject();
                var verifiedRequested = new JObject();
                var verifiedPersisted = new JObject();
                foreach (var p in properties.Properties())
                {
                    string propName = p.Name;
                    string requested = p.Value?.ToString() ?? string.Empty;
                    beforeValues.TryGetValue(propName, out string before);
                    string verification = VerifyPropertyPersisted(target, nativeNames[propName], requested,
                        controlName, typeFilter, before, obj);
                    if (verification == null)
                        return RollbackPropertySnapshot(target, obj, snapshot,
                            "The saved property could not be independently read back.");

                    var verificationEnvelope = JObject.Parse(verification);
                    if (string.Equals(verificationEnvelope["status"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase))
                        return RollbackPropertySnapshot(target, obj, snapshot,
                            verificationEnvelope["error"]?["message"]?.ToString() ?? "Property verification failed.");

                    var diff = verificationEnvelope["result"] as JObject;
                    if (diff == null || diff["persistedVerified"]?.ToObject<bool>() != true)
                        return RollbackPropertySnapshot(target, obj, snapshot,
                            "The saved property could not be confirmed.");
                    verifiedBefore[propName] = diff["before"]?.ToString() ?? string.Empty;
                    verifiedRequested[propName] = diff["requested"]?.ToString() ?? requested;
                    verifiedPersisted[propName] = diff["persisted"]?.ToString() ?? string.Empty;
                }

                var fresh = _objectService.FindObjectFreshByIdentity(obj);
                if (fresh == null || !snapshot.CompareParts(fresh).Equal)
                    return RollbackPropertySnapshot(target, obj, snapshot,
                        "The property save changed an unrelated object part or the parts could not be verified.");

                var result = new JObject { ["properties"] = properties };
                if (verifiedPersisted.Count == properties.Count)
                {
                    result["before"] = verifiedBefore;
                    result["requested"] = verifiedRequested;
                    result["persisted"] = verifiedPersisted;
                    result["persistedVerified"] = true;
                }
                result["versionToken"] = WriteService.ComputeVersionToken(fresh);
                result["preservedParts"] = snapshot.PreservedParts;
                result["implicitOperations"] = new JArray();
                return Models.McpResponse.Ok(target: target, code: "PropertiesApplied", result: result);
            }
            catch (PropertyWipeException pwe)
            {
                return Models.McpResponse.Err(
                    code: "PropertyWriteWipedValue",
                    message: $"Setting '{pwe.PropertyName}' emptied a previously non-empty value; the write was rolled back to avoid data loss.",
                    target: target);
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string RollbackPropertySnapshot(string target, KBObject written, ObjectMoveSnapshot snapshot, string reason)
        {
            try
            {
                var current = _objectService.FindObjectFreshByIdentity(written);
                if (current == null || !string.Equals(WriteService.ComputeVersionToken(current),
                    WriteService.ComputeVersionToken(written), StringComparison.Ordinal))
                    return Models.McpResponse.Err(code: "PropertyRollbackUnsafe", target: target,
                        message: reason + " The persisted version could not be tied to this write; automatic rollback was refused.");
                using (var tx = written.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        snapshot.RestoreObject(current);
                        current.Dirty = true;
                        current.Save();
                        snapshot.RestoreParts(current);
                        tx.Commit();
                        committed = true;
                    }
                    finally { if (!committed) try { tx.Rollback(); } catch { } }
                }
                var restored = _objectService.FindObjectFreshByIdentity(current);
                bool verified = restored != null && snapshot.Compare(restored).Equal;
                return Models.McpResponse.Err(code: "PropertyVerificationFailed", target: target,
                    message: reason + (verified ? " The complete object snapshot was restored and verified."
                        : " Snapshot restoration could not be verified; inspect the object before writing again."),
                    extra: new JObject { ["rollbackAttempted"] = true, ["rollbackVerified"] = verified });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "PropertyRollbackFailed", target: target,
                    message: reason + " Snapshot restoration failed: " + ex.Message,
                    extra: new JObject { ["rollbackAttempted"] = true, ["rollbackVerified"] = false });
            }
        }

        internal static void ApplyPropertiesDirect(KBObject obj, JObject properties, dynamic container = null)
        {
            if (obj == null || properties == null || properties.Count == 0) return;
            dynamic targetContainer = container ?? obj;
            foreach (var p in properties.Properties())
            {
                string pName = p.Name;
                string directWriteValidation = ValidateDirectPropertyWrite(pName);
                if (directWriteValidation != null) throw new InvalidOperationException(directWriteValidation);
                string pVal = p.Value?.ToString();
                object reportContainer = ResolveReportPropertyContainer(obj, pName, null);
                dynamic propertyContainer = reportContainer ?? targetContainer;
                string propertyValidation = reportContainer != null ? ValidateReportTextMode(pVal) : ValidatePropertyWrite(propertyContainer, pName, pVal);
                if (propertyValidation != null) throw new InvalidOperationException(propertyValidation);
                ApplyPropertyValue(propertyContainer, pName, pVal, null, obj);
                if (ReportLayoutHelper.IsTextModeProperty(pName))
                    ReportLayoutHelper.MarkLayoutDirty(GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Layout"));
            }
        }

        /// <summary>
        /// Resolve a property descriptor before opening an SDK transaction. This
        /// keeps read-only properties and malformed scalar values at zero saves;
        /// the older setter path only discovered both failures after invoking SDK
        /// conversion and could return an opaque error envelope.
        /// </summary>
        internal static string ValidatePropertyWrite(dynamic container, string propName, string rawValue)
        {
            if (container == null || string.IsNullOrWhiteSpace(propName))
                return "PropertyNotFound: a property name is required.";
            // These names have dedicated typed SDK adapters below and are not
            // scalar descriptor conversions.
            if (IsDomainPropertyName(propName)
                || IsNullablePropertyName(propName)
                || string.Equals(propName.Trim(), "OutputSDT", StringComparison.OrdinalIgnoreCase))
                return null;

            dynamic descriptor = null;
            bool enumerated = false;
            try
            {
                foreach (dynamic property in container.Properties)
                {
                    enumerated = true;
                    string name = null;
                    try { name = property.Name?.ToString(); } catch { }
                    if (string.Equals(name, propName, StringComparison.OrdinalIgnoreCase))
                    {
                        descriptor = property;
                        break;
                    }
                }
            }
            catch
            {
                // Some SDK bags expose only SetPropertyValue and no enumerable
                // descriptor collection. Leave those to the proven setter path.
                return null;
            }

            if (descriptor == null)
                return enumerated ? $"PropertyNotFound: '{propName}' is not exposed by this object or control." : null;

            try
            {
                if (descriptor.Definition?.ReadOnly == true)
                    return $"PropertyReadOnly: '{propName}' is read-only for this object or control.";
            }
            catch { }

            Type targetType = null;
            try
            {
                if (descriptor.Definition?.Type is Type definitionType) targetType = definitionType;
            }
            catch { }
            if (targetType == null)
            {
                try
                {
                    object current = descriptor.Value;
                    if (current != null) targetType = current.GetType();
                }
                catch { }
            }

            // A WebPanelReference (e.g. MasterPage) is set by object name through a typed
            // adapter (SetWebPanelReferenceProperty), not by scalar conversion.
            if (IsWebPanelReferenceType(targetType)) return null;

            if (targetType != null && targetType != typeof(string)
                && !TryConvertToType(rawValue, targetType, out _))
                return $"InvalidPropertyValue: '{rawValue}' cannot be converted to {targetType.Name} for '{propName}'.";

            return null;
        }

        // issue #59 — re-read the property from a freshly-resolved object after the SDK
        // commit and confirm the requested value persisted. Returns either:
        //   - an error envelope (PropertyNotPersisted) when the re-read disagrees,
        //   - the success envelope decorated with before/requested/persisted when confirmed,
        //   - null when the fresh re-read is impossible (unverifiable → keep the original
        //     response; never falsely accuse a write that can't be checked).
        private string VerifyPropertyPersisted(string target, string propName, string requested, string controlName, string typeFilter, string beforeVal, KBObject original)
        {
            try
            {
                bool reportProperty = ReportLayoutHelper.IsTextModeProperty(propName) && string.IsNullOrEmpty(controlName);
                var fresh = _objectService.FindObjectFreshByIdentity(original);
                if (fresh == null) return reportProperty ? ReportPropertyVerificationUnavailable(target) : null;
                if (reportProperty && (original == null || fresh.Guid != original.Guid))
                    return ReportPropertyVerificationUnavailable(target);

                // Circularity guard: if the SDK hands back the SAME in-memory instance we
                // just mutated (rather than a fresh object from the KB store), a re-read
                // trivially matches the requested value and proves nothing. Treat identity
                // as "unverifiable" — never claim a confirmation we didn't obtain.
                if (original != null && object.ReferenceEquals(fresh, original))
                    return reportProperty ? ReportPropertyVerificationUnavailable(target) : null;

                dynamic container = fresh;
                if (!string.IsNullOrEmpty(controlName))
                {
                    container = FindControl(fresh, controlName);
                    if (container == null) return null;
                }

                container = ResolveReportPropertyContainer(fresh, propName, controlName) ?? container;
                string persisted = null;
                // Nullable/ALLOWNULL family (issue #57): the property-bag entry may be named
                // IsNullable even when the caller used Nullable — read the typed getter.
                if (IsNullablePropertyName(propName))
                {
                    try
                    {
                        dynamic inl = container.IsNullable;
                        if (inl != null) persisted = inl.ToString();
                    }
                    catch { /* fall through to the bag read */ }
                }
                // issue #117: Domain assignment on Attribute/Domain/Variable routes through DomainBasedOn
                if (IsDomainPropertyName(propName))
                {
                    requested = StripDomainQualifier(requested);
                    try
                    {
                        string domName = DomainPropertyApplier.GetDomainBasedOnName((object)container);
                        persisted = domName ?? string.Empty;
                    }
                    catch { }
                }
                if (string.Equals(propName?.Trim(), "Type", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        object typedAttribute = container;
                        if (!(typedAttribute is global::Artech.Genexus.Common.Objects.Attribute))
                        {
                            var attributeProperty = AttributeTypeApplier.GetPropertyUnambiguous(typedAttribute?.GetType(), "Attribute");
                            typedAttribute = attributeProperty?.GetValue(typedAttribute, null);
                        }
                        if (typedAttribute is global::Artech.Genexus.Common.Objects.Attribute)
                        {
                            var typeProperty = AttributeTypeApplier.GetPropertyUnambiguous(typedAttribute.GetType(), "Type");
                            persisted = typeProperty?.GetValue(typedAttribute, null)?.ToString();
                        }
                    }
                    catch { }
                }
                if (string.Equals(propName?.Trim(), "OutputSDT", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var outputProperty = fresh.GetType().GetProperty("OutputSDT");
                        if (outputProperty != null)
                            persisted = outputProperty.GetValue(fresh, null)?.ToString() ?? string.Empty;
                    }
                    catch { }
                }
                if (reportProperty) persisted = ReflectionHelper.TryGetPropertyBagValue((object)container, ReportLayoutHelper.TextModeProperty)?.ToString();
                if (persisted == null) persisted = TryReadPropertyString(container, propName, fresh.Model);
                if (persisted == null) return reportProperty ? ReportPropertyVerificationUnavailable(target) : null; // unverifiable

                if (!PersistenceVerifier.ValuesMatch(requested, persisted, IsNullablePropertyName(propName)))
                {
                    bool preservedExistingAttributeType = original is global::Artech.Genexus.Common.Objects.Attribute
                        && string.Equals(propName?.Trim(), "Type", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(beforeVal)
                        && !PersistenceVerifier.ValuesMatch(beforeVal, requested)
                        && PersistenceVerifier.ValuesMatch(beforeVal, persisted);
                    if (preservedExistingAttributeType)
                    {
                        return PersistenceVerifier.BuildNotPersistedError(
                            code: "UnsupportedOperation",
                            target: target,
                            property: propName,
                            requestedValue: requested,
                            previousValue: beforeVal ?? string.Empty,
                            persistedValue: persisted,
                            message: "Changing Type on an existing Attribute is not supported through the GeneXus SDK property surface; the original type was preserved.",
                            hint: "Do not retry this property write. Change the Attribute Type in the GeneXus IDE or import a package that already contains the desired type.");
                    }

                    return PersistenceVerifier.BuildNotPersistedError(
                        code: "PropertyNotPersisted",
                        target: target,
                        property: propName,
                        requestedValue: requested,
                        previousValue: beforeVal ?? string.Empty,
                        persistedValue: persisted);
                }

                string success = Models.McpResponse.Ok(
                    target: target,
                    code: "PropertyApplied",
                    result: new JObject { ["property"] = propName, ["value"] = requested });
                return PersistenceVerifier.AttachPersistedDiff(success, beforeVal ?? string.Empty, requested, persisted);
            }
            catch (Exception ex)
            {
                Logger.Debug("[PROPERTY-VERIFY] " + ex.Message);
                return ReportLayoutHelper.IsTextModeProperty(propName) && string.IsNullOrEmpty(controlName)
                    ? ReportPropertyVerificationUnavailable(target) : null;
            }
        }

        private static string ValidateReportTextMode(string value)
            => bool.TryParse(value, out _) ? null : "InvalidPropertyValue: RPT_TEXT_MODE requires True or False.";

        internal static string ValidateDirectPropertyWrite(string propName)
            => ReportLayoutHelper.IsTextModeProperty(propName)
                ? "RPT_TEXT_MODE must be set with genexus_properties action=set so persisted state can be independently verified."
                : null;

        private static string ReportPropertyVerificationUnavailable(string target)
            => Models.McpResponse.Err(code: "PropertyVerificationUnavailable", target: target,
                message: "RPT_TEXT_MODE save could not be independently read back. Do not retry the write automatically.");

        internal static object ResolveReportPropertyContainer(KBObject obj, string propName, string controlName)
        {
            if (!string.IsNullOrEmpty(controlName) || !ReportLayoutHelper.IsTextModeProperty(propName)) return null;
            var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Layout");
            return ReportLayoutHelper.IsReportPart(part) == null ? null : ReportLayoutHelper.GetTextModeContainer(part);
        }

        // issue #41: structured properties the generic scalar setter can't represent —
        // writing them as a string wipes the underlying collection. Reject up front.
        private static readonly HashSet<string> _nonScalarProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ControlValues"
        };

        private static bool IsNonScalarProperty(string propName)
            => !string.IsNullOrEmpty(propName) && _nonScalarProps.Contains(propName.Trim());

        // Best-effort read of a property's current value as a string (for the wipe check).
        /// <summary>
        /// Resolves a property entry by name from an SDK property bag, or null when
        /// the container has no such property.
        ///
        /// The bag's own lookup is case-sensitive while GeneXus property names are
        /// not, so a miss falls back to a case-insensitive scan. Both callers need
        /// the same resolution: one reads the value, the other reads the declared
        /// type to coerce a new value into. If the two disagreed on which entry
        /// matched, a write would read one property's current value while coercing
        /// against another property's type - a silent corruption that no individual
        /// check would catch.
        ///
        /// Every SDK access here is individually guarded: a property bag on a
        /// partially-loaded object throws on access rather than returning null.
        /// </summary>
        private static object ResolvePropertyEntry(dynamic container, string propName)
        {
            dynamic existing = null;
            try { existing = container.Properties?[propName]; } catch { }
            if (existing != null) return existing;

            try
            {
                foreach (dynamic p in container.Properties)
                {
                    string n = null;
                    try { n = (string)p.Name; } catch { }
                    if (string.Equals(n, propName, StringComparison.OrdinalIgnoreCase)) return p;
                }
            }
            catch { }

            return null;
        }

        private static bool IsWebPanel(KBObject obj)
            => string.Equals(obj?.TypeDescriptor?.Name, "WebPanel", StringComparison.OrdinalIgnoreCase);

        private static string NativeWebPanelPropertyName(KBObject obj, string controlName, string requestedName)
            => string.IsNullOrEmpty(controlName) && IsWebPanel(obj)
                && string.Equals(requestedName, "MainProgram", StringComparison.OrdinalIgnoreCase)
                && ResolvePropertyEntry(obj, "MainProgram") == null
                && ResolvePropertyEntry(obj, "IsMain") != null
                    ? "IsMain" : requestedName;

        private static string TryReadPropertyString(dynamic container, string propName, KBModel model = null)
        {
            try
            {
                if (IsDomainPropertyName(propName))
                {
                    try
                    {
                        string domName = DomainPropertyApplier.GetDomainBasedOnName((object)container);
                        if (domName != null) return domName;
                    }
                    catch { }
                }

                object existing = ResolvePropertyEntry(container, propName);
                object val = null;
                try { val = existing == null ? null : ((dynamic)existing).Value; } catch { }
                if (val == null && ReportLayoutHelper.IsTextModeProperty(propName))
                    val = ReflectionHelper.TryGetPropertyBagValue((object)container, ReportLayoutHelper.TextModeProperty);
                return val == null ? null : RenderPropertyValue(val, model);
            }
            catch { return null; }
        }

        private sealed class PropertyWipeException : Exception
        {
            public string PropertyName { get; }
            public PropertyWipeException(string propName, string priorValue)
                : base($"Property '{propName}' would be wiped (prior value: '{priorValue}').")
            { PropertyName = propName; }
        }

        // Object-level "properties" that actually mean "reparent this object". They are not
        // scalar property writes, so a request naming one is routed to ObjectService.MoveObject
        // (which persists via the Udm EntityManager and re-reads to confirm) rather than being
        // pushed through SetPropertyValue. See ObjectMover for why the direct setters look inert.
        private static readonly HashSet<string> _placementProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Folder", "FolderId", "FolderGuid", "Module", "ModuleId", "Parent", "ParentKey", "ParentId"
        };

        private static bool IsObjectPlacementProperty(string propName)
            => !string.IsNullOrEmpty(propName) && _placementProps.Contains(propName.Trim());

        internal static bool IsWebPanelReferenceType(Type type)
            => type != null && typeof(global::Artech.Genexus.Common.CustomTypes.WebPanelReference).IsAssignableFrom(type);

        private static Type GetPropertyTargetType(dynamic container, string name)
        {
            try
            {
                dynamic entry = ResolvePropertyEntry(container, name);
                if (entry == null) return null;
                try { if (entry.Definition?.Type is Type t) return t; } catch { }
                try { object cur = entry.Value; if (cur != null) return cur.GetType(); } catch { }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Renders a property value for output. Reference values (WebPanelReference, e.g. the
        /// MasterPage property) stringify to their CLR type name, so any value exposing
        /// <c>GetName(KBModel)</c> is rendered as the referenced object's name instead; an
        /// empty/none reference renders as an empty string.
        /// </summary>
        // A value whose GetName(KBModel) is meaningful for rendering: the SDK reference type
        // itself, or another reference-shaped wrapper exposing the same shape. Applying
        // GetName to every property would change how unrelated types are displayed, and a
        // type whose GetName throws must still render its own value rather than silently
        // becoming empty (an empty read can trip the PropertyWipeException safety net).
        internal static bool IsReferenceShapedType(Type type)
        {
            if (type == null) return false;
            if (IsWebPanelReferenceType(type)) return true;
            return type.Name.IndexOf("Reference", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string RenderPropertyValue(object value, KBModel model)
        {
            if (value == null) return string.Empty;
            if (value is string s) return s;
            if (!IsReferenceShapedType(value.GetType())) return value.ToString() ?? string.Empty;
            var getName = value.GetType().GetMethod("GetName", new[] { typeof(KBModel) });
            if (getName != null && getName.ReturnType == typeof(string))
            {
                try
                {
                    // The SDK renders its none reference as "(none)"; that is "no value" here.
                    string name = (string)getName.Invoke(value, new object[] { model });
                    return string.IsNullOrEmpty(name) || name == "(none)" ? string.Empty : name;
                }
                catch (Exception ex)
                {
                    Logger.Debug("[PROPERTY] GetName failed for " + value.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message));
                    // Fall back to the reference's own rendering rather than reporting no value.
                    return value.ToString() ?? string.Empty;
                }
            }
            return value.ToString() ?? string.Empty;
        }

        // A WebPanelReference property (MasterPage) holds an EntityKey, not a string: resolve the
        // requested object name in the same KB, build the reference from its key, set it through
        // the property bag and verify on re-read. An empty value sets NoneRef (clears).
        private string SetWebPanelReferenceProperty(KBObject obj, dynamic container, string target, string propName, string value, string controlName, string typeFilter)
        {
            try
            {
                object newRef;
                if (string.IsNullOrWhiteSpace(value))
                {
                    newRef = global::Artech.Genexus.Common.CustomTypes.WebPanelReference.NoneRef;
                }
                else
                {
                    string name = value.Trim();
                    var referenced = _objectService.FindObject(name, "MasterPage") ?? _objectService.FindObject(name, "WebPanel");
                    if (referenced == null)
                        return Models.McpResponse.Err(
                            code: "ReferencedObjectNotFound",
                            message: $"Cannot set {propName}: no MasterPage or WebPanel named '{value}' was found in the Knowledge Base.",
                            hint: "Pass the name of an existing MasterPage (or an empty value to clear).",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", new JObject { ["type"] = "MasterPage", ["query"] = value }, "Lists MasterPages matching the name.")),
                            target: target);
                    newRef = new global::Artech.Genexus.Common.CustomTypes.WebPanelReference(referenced.Key);
                }

                string beforeVal = TryReadPropertyString(container, propName, obj.Model);
                using (var trans = obj.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        container.SetPropertyValue(propName, newRef);
                        try { if (container != obj) container.Dirty = true; } catch { }
                        obj.EnsureSave();
                        trans.Commit();
                        committed = true;
                        WriteService.NotePerTargetWrite(target);
                        InvalidatePropertyCache(obj);
                    }
                    finally
                    {
                        if (!committed) { try { trans.Rollback(); } catch (Exception rbEx) { Logger.Warn("[PROPERTY] Rollback failed: " + rbEx.Message); } }
                    }
                }

                string requested = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
                string verified = VerifyPropertyPersisted(target, propName, requested, controlName, typeFilter, beforeVal, obj);
                if (verified != null) return verified;

                return Models.McpResponse.Ok(target: target, code: "PropertyApplied", result: new JObject { ["property"] = propName, ["value"] = requested });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "PropertyWriteFailed",
                    message: ex.InnerException?.Message ?? ex.Message,
                    hint: "Check the worker log for the SDK stack trace.",
                    target: target);
            }
        }

        // issue #48: set a Data Provider's output SDT through the typed SDK API. OutputSDT is a
        // read-only derived string; the writable output is a DataProviderOutputReference applied
        // via the static helper Artech.Genexus.Common.Properties+DPRV.SetOutput(IPropertyBag,
        // DataProviderOutputReference). Passing an empty value clears the output.
        private string SetDataProviderOutputSdt(KBObject obj, string target, string value)
        {
            try
            {
                if (!string.Equals(obj.TypeDescriptor?.Name, "DataProvider", StringComparison.OrdinalIgnoreCase))
                    return Models.McpResponse.Err(
                        code: "OutputSdtNotADataProvider",
                        message: $"OutputSDT applies to Data Providers; '{target}' is a {obj.TypeDescriptor?.Name}.",
                        hint: "Set OutputSDT only on a DataProvider object.",
                        target: target);

                KBObject sdt = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    sdt = _objectService.FindObject(value.Trim(), "SDT");
                    if (sdt == null)
                        return Models.McpResponse.Err(
                            code: "OutputSdtNotFound",
                            message: $"Cannot set OutputSDT: no SDT named '{value}' was found in the Knowledge Base.",
                            hint: "Pass the name of an existing SDT (or an empty value to clear the output).",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", new JObject { ["type"] = "SDT", ["query"] = value }, "Lists SDTs matching the name.")),
                            target: target);
                }

                var commonAsm = typeof(global::Artech.Genexus.Common.Objects.Transaction).Assembly;
                var refType = commonAsm.GetType("Artech.Genexus.Common.CustomTypes.DataProviderOutputReference");
                var dprvType = commonAsm.GetType("Artech.Genexus.Common.Properties+DPRV");
                if (refType == null || dprvType == null)
                    return Models.McpResponse.Err(
                        code: "OutputSdtApiUnavailable",
                        message: "The SDK types for setting a Data Provider output (DataProviderOutputReference / Properties.DPRV) were not found.",
                        hint: "This GeneXus build may differ; report the version from genexus_whoami.",
                        target: target);

                string beforeOutput = TryReadPropertyString(obj, "OutputSDT");

                var setOutput = dprvType.GetMethod("SetOutput",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (setOutput == null)
                    return Models.McpResponse.Err(
                        code: "OutputSdtApiUnavailable",
                        message: "Properties.DPRV.SetOutput was not found in this SDK build.",
                        target: target);

                // Build the reference: ctor(KBObject) for a target SDT, or a null reference to clear.
                object outputRef = null;
                if (sdt != null)
                {
                    var ctor = refType.GetConstructor(new[] { typeof(KBObject) })
                               ?? refType.GetConstructor(new[] { typeof(Artech.Architecture.Common.Objects.KBObjectReference) });
                    if (ctor == null)
                        return Models.McpResponse.Err(
                            code: "OutputSdtApiUnavailable",
                            message: "DataProviderOutputReference has no (KBObject) constructor in this SDK build.",
                            target: target);
                    outputRef = ctor.GetParameters()[0].ParameterType == typeof(KBObject)
                        ? ctor.Invoke(new object[] { sdt })
                        : ctor.Invoke(new object[] { new Artech.Architecture.Common.Objects.KBObjectReference(sdt) });
                }

                using (var trans = obj.Model.KB.BeginTransaction())
                {
                    bool committed = false;
                    try
                    {
                        // The IPropertyBag is the KBObject itself (PropertiesObject) — obj.Properties
                        // is only an enumerable projection of descriptors, not the bag.
                        setOutput.Invoke(null, new object[] { obj, outputRef });
                        obj.EnsureSave();
                        trans.Commit();
                        committed = true;
                        WriteService.NotePerTargetWrite(target);
                        InvalidatePropertyCache(obj);
                    }
                    finally
                    {
                        if (!committed) { try { trans.Rollback(); } catch { } }
                    }
                }

                string requestedOutput = sdt?.Name ?? string.Empty;
                string verified = VerifyPropertyPersisted(
                    target,
                    "OutputSDT",
                    requestedOutput,
                    controlName: null,
                    typeFilter: "DataProvider",
                    beforeVal: beforeOutput,
                    original: obj);
                if (verified != null) return verified;

                return Models.McpResponse.Ok(target: target, code: "PropertyApplied",
                    result: new JObject { ["property"] = "OutputSDT", ["value"] = requestedOutput });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "OutputSdtWriteFailed",
                    message: ex.InnerException?.Message ?? ex.Message,
                    hint: "Check the worker log for the SDK stack trace.",
                    target: target);
            }
        }

        // Properties on Transaction (and other KBObjects) have heterogeneous underlying CLR types:
        // bool, int, enum, or string. SetPropertyValue(string, object) does not coerce, so passing
        // the raw "True"/"1" string fails with InvalidCastException on non-string properties
        // (e.g. idISBUSINESSCOMPONENT, idISBCEJB). Coerce by inspecting the existing value's type
        // or the property Definition before delegating, then fall back to the string-overload setter
        // (SetPropertyValueString) which the SDK provides for textual input.
        internal static void ApplyPropertyValue(dynamic container, string propName, string rawValue, string controlName, KBObject obj)
        {
            Exception lastError = null;
            if (ReportLayoutHelper.IsTextModeProperty(propName) && string.IsNullOrEmpty(controlName))
            {
                ReportLayoutHelper.SetTextMode((object)container, rawValue);
                return;
            }

            // issue #179: the generic property bag accepts a Type string on an
            // Attribute but does not change the typed SDK value. Route Attributes
            // (and TransactionAttribute occurrences) through the same typed adapter
            // used by the DSL authoring path.
            if (string.Equals(propName?.Trim(), "Type", StringComparison.OrdinalIgnoreCase))
            {
                object boxedContainer = (object)container;
                bool isAttribute = boxedContainer is global::Artech.Genexus.Common.Objects.Attribute;
                if (!isAttribute)
                {
                    try
                    {
                        var attributeProperty = AttributeTypeApplier.GetPropertyUnambiguous(boxedContainer?.GetType(), "Attribute");
                        isAttribute = attributeProperty?.GetValue(boxedContainer, null)
                            is global::Artech.Genexus.Common.Objects.Attribute;
                    }
                    catch { }
                }

                if (isAttribute)
                {
                    if (!AttributeTypeApplier.TryApplyType(boxedContainer, rawValue, out string typeError))
                        throw new InvalidOperationException(typeError);
                    return;
                }
            }

            // issue #57: Nullable / ALLOWNULL on a Transaction or Table attribute occurrence
            // is the typed TableAttribute.IsNullableValue (False=0, True=1, Compatible=2).
            // string setter can't represent the enum ("Yes" is the converter's display string,
            // not an enum member) and an int assignment to the dynamic property throws a
            // runtime binder error — write the typed value directly. The ALLOWNULL alias is
            // accepted for IDE parity (the KB's own DDL-driven property name).
            if (IsNullablePropertyName(propName)
                && (container is Artech.Genexus.Common.Parts.TableAttribute
                    || container is Artech.Genexus.Common.Parts.TransactionAttribute))
            {
                // TableAttribute.IsNullableValue: False=0, True=1, Compatible=2.
                container.IsNullable = (Artech.Genexus.Common.Parts.TableAttribute.IsNullableValue)ParseIsNullableValue(rawValue);
                return;
            }

            // issue #117: Domain assignment on Attribute/Domain/Variable routes through DomainBasedOn
            if (IsDomainPropertyName(propName))
            {
                if (!string.IsNullOrWhiteSpace(rawValue))
                {
                    object domainObj = null;
                    try
                    {
                        if (obj?.Model != null)
                        {
                            var qName = new Artech.Architecture.Common.Objects.QualifiedName(StripDomainQualifier(rawValue).Trim());
                            domainObj = Artech.Genexus.Common.Objects.Domain.Get(obj.Model, qName);
                        }
                    }
                    catch { }
                    if (domainObj != null)
                    {
                        if (DomainPropertyApplier.ApplyDomainBasedOn((object)container, domainObj)) return;
                        if (AttributeTypeApplier.ApplyDomain((object)container, domainObj)) return;
                    }
                }
                else
                {
                    if (DomainPropertyApplier.ClearDomainBasedOn((object)container)) return;
                }
            }

            // 1) Try a coerced typed value via SetPropertyValue(string, object).
            object coerced;
            if (TryCoercePropertyValue(container, propName, rawValue, out coerced))
            {
                try { container.SetPropertyValue(propName, coerced); return; }
                catch (Exception ex) { lastError = ex; }
            }

            // 2) Try the SDK's string-overload setter, which converts strings internally.
            try
            {
                var t = (Type)container.GetType();
                var mi = t.GetMethod("SetPropertyValueString",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(string), typeof(string) }, null);
                if (mi != null)
                {
                    mi.Invoke((object)container, new object[] { propName, rawValue });
                    return;
                }
            }
            catch (Exception ex) { lastError = ex; }

            // 3) Last resort: untyped passthrough (original behavior).
            try { container.SetPropertyValue(propName, rawValue); return; }
            catch (Exception ex) { lastError = ex; }

            // 4) Last-last resort: reflection on a public CLR property of the same name.
            try
            {
                var pInfo = ((Type)container.GetType()).GetProperty(propName);
                if (pInfo != null && pInfo.CanWrite)
                {
                    object refValue = TryConvertToType(rawValue, pInfo.PropertyType, out var conv) ? conv : (object)rawValue;
                    pInfo.SetValue((object)container, refValue);
                    return;
                }
            }
            catch (Exception ex) { lastError = ex; }

            throw new Exception($"Property '{propName}' not found or not writable on {controlName ?? obj.Name}. Underlying error: {lastError?.Message}");
        }

        internal static bool IsNullablePropertyName(string propName)
        {
            if (string.IsNullOrEmpty(propName)) return false;
            return propName.Equals("ALLOWNULL", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("Nullable", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("IsNullable", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDomainPropertyName(string propName)
        {
            if (string.IsNullOrEmpty(propName)) return false;
            string norm = propName.Trim();
            return norm.Equals("Domain", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("DomainBasedOn", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("BasedOn", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("DomainDefinition", StringComparison.OrdinalIgnoreCase)
                || IsIdBasedOnPropertyName(norm);
        }

        // idBasedOn is the SDK name of an Attribute's "Based on" property. Its value is a
        // BasedOnReference, not a string, so it must take the typed Domain path: the
        // generic scalar setter cannot bind to it and reported PropertyApplied without
        // writing anything.
        private static bool IsIdBasedOnPropertyName(string propName)
            => string.Equals(propName?.Trim(), "idBasedOn", StringComparison.OrdinalIgnoreCase);

        // Read-side aliasing: a Domain/BasedOn request returns the DomainBasedOn entry.
        // idBasedOn is excluded on both sides — the bag holds the opaque BasedOnReference
        // type name there, and letting it alias would make `get Domain` return that
        // instead of the domain name depending on property order.
        internal static bool IsDomainReadAlias(string requested, string candidate)
            => IsDomainPropertyName(requested) && IsDomainPropertyName(candidate)
               && !IsIdBasedOnPropertyName(requested) && !IsIdBasedOnPropertyName(candidate);

        // Agents commonly qualify the value as "Domain:<Name>" (the form the variable tool
        // accepts). QualifiedName cannot resolve the prefixed form, and the persisted
        // value read back is the bare name, so the prefix is dropped on both the write and
        // the verification side. Any other prefix (e.g. "Attribute:") is left intact so it
        // fails as DomainNotFound instead of resolving to the wrong object.
        internal static string StripDomainQualifier(string value)
        {
            if (value == null) return null;
            string trimmed = value.Trim();
            const string prefix = "Domain:";
            return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(prefix.Length).Trim()
                : value;
        }

        // TableAttribute.IsNullableValue: False=0, True=1, Compatible=2. Accepts the
        // canonical strings, the JSON-boolean forms, and numeric values.
        internal static int ParseIsNullableValue(string rawValue)
        {
            string norm = (rawValue ?? "").Trim();
            if (norm.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("True", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("Y", StringComparison.OrdinalIgnoreCase)
                || norm == "1")
                return 1;
            if (norm.Equals("Managed", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("Compatible", StringComparison.OrdinalIgnoreCase)
                || norm == "2")
                return 2;
            return 0;
        }

        private static bool TryCoercePropertyValue(dynamic container, string propName, string rawValue, out object coerced)
        {
            coerced = rawValue;
            Type targetType = null;

            // Prefer the Definition.Type from the existing property entry; fall back to the
            // runtime type of the current Value.
            try
            {
                dynamic existing = ResolvePropertyEntry(container, propName);
                if (existing != null)
                {
                    try
                    {
                        var defType = existing.Definition?.Type;
                        if (defType is Type t1) targetType = t1;
                    }
                    catch { }
                    if (targetType == null)
                    {
                        try
                        {
                            object curVal = existing.Value;
                            if (curVal != null) targetType = curVal.GetType();
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (targetType == null || targetType == typeof(string)) return false;

            return TryConvertToType(rawValue, targetType, out coerced);
        }

        private static bool TryConvertToType(string raw, Type targetType, out object converted)
        {
            converted = raw;
            if (targetType == null) return false;

            try
            {
                if (targetType == typeof(string)) { converted = raw ?? string.Empty; return true; }
                if (targetType == typeof(bool) || targetType == typeof(bool?))
                {
                    if (string.IsNullOrWhiteSpace(raw)) { converted = false; return true; }
                    string t = raw.Trim();
                    if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1" ||
                        t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("y", StringComparison.OrdinalIgnoreCase))
                    { converted = true; return true; }
                    if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0" ||
                        t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("n", StringComparison.OrdinalIgnoreCase))
                    { converted = false; return true; }
                    return false;
                }
                if (targetType.IsEnum)
                {
                    converted = Enum.Parse(targetType, raw, ignoreCase: true);
                    return true;
                }
                Type underlying = Nullable.GetUnderlyingType(targetType);
                if (underlying != null && underlying.IsEnum)
                {
                    if (string.IsNullOrWhiteSpace(raw)) { converted = null; return true; }
                    converted = Enum.Parse(underlying, raw, ignoreCase: true);
                    return true;
                }
                if (targetType == typeof(int) || targetType == typeof(int?))
                {
                    if (int.TryParse(raw, out int iv)) { converted = iv; return true; }
                    return false;
                }
                if (targetType == typeof(long) || targetType == typeof(long?))
                {
                    if (long.TryParse(raw, out long lv)) { converted = lv; return true; }
                    return false;
                }
                if (targetType == typeof(Guid) || targetType == typeof(Guid?))
                {
                    if (Guid.TryParse(raw, out Guid gv)) { converted = gv; return true; }
                    return false;
                }
                converted = Convert.ChangeType(raw, underlying ?? targetType);
                return true;
            }
            catch
            {
                converted = raw;
                return false;
            }
        }

        private dynamic FindControl(KBObject obj, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // FR#4 + FR#5 (friction-report 2026-05-14): accept three scope forms in `control`:
            //   1. Layout control name (e.g. "BtnConfirmar") — existing behavior.
            //   2. Variable reference with & prefix (e.g. "&Alu2RegProf") — new.
            //   3. Plain variable name when starting with '&' is stripped.
            // The variable form returns the SDK Variable instance so its properties
            // (ControlType, ControlValues, Enabled, Visible, …) can be read/set.
            string trimmed = name.Trim();
            if (trimmed.StartsWith("&"))
            {
                return FindVariable(obj, trimmed.Substring(1));
            }

            // Support qualified paths: "Documento.DocCod" or "Documento/DocCod"
            var segments = name.Split(new[] { '.', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string leaf = segments[segments.Length - 1];

            var webFormPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.TypeDescriptor.Name == "WebForm");
            if (webFormPart != null)
            {
                dynamic dPart = webFormPart;

                dynamic root = null;
                try { if (dPart.Form != null) root = dPart.Form; } catch { }
                if (root == null) { try { if (dPart.WebForm != null && dPart.WebForm.Form != null) root = dPart.WebForm.Form; } catch { } }

                if (root != null)
                {
                    if (segments.Length > 1)
                    {
                        var qualified = FindByPath(root, segments);
                        if (qualified != null) return qualified;
                    }
                    var ctrl = FindInControlCollection(root, leaf);
                    if (ctrl != null) return ctrl;
                }
            }

            // FR#4 + FR#5 last-resort: if the user passed a bare name that matches a Variable,
            // accept it. This is mostly to keep error messages sane when the agent forgets the
            // `&` prefix; explicit `&Name` is still the recommended form.
            var fallbackVar = FindVariable(obj, leaf);
            if (fallbackVar != null) return fallbackVar;

            // issue #57: a Transaction structure attribute ("ProcessamentoQtd") is not a
            // layout control or variable — resolve it from the Transaction's structure so
            // its occurrence properties (IsNullable, …) are reachable via genexus_properties.
            var trn = obj as Artech.Genexus.Common.Objects.Transaction;
            if (trn != null)
            {
                var structureAttr = FindStructureAttribute(trn.Structure.Root, leaf);
                if (structureAttr != null) return structureAttr;
            }

            return null;
        }

        // issue #57: recursive name lookup of a TransactionAttribute occurrence in the
        // Transaction structure (all levels).
        private dynamic FindStructureAttribute(Artech.Genexus.Common.Parts.TransactionLevel level, string attrName)
        {
            if (level == null) return null;
            try
            {
                foreach (dynamic attr in level.Attributes)
                {
                    string n = null;
                    try { n = (string)attr.Name; } catch { }
                    if (!string.IsNullOrEmpty(n) && string.Equals(n, attrName, StringComparison.OrdinalIgnoreCase)) return attr;
                }
                foreach (dynamic sub in level.Levels)
                {
                    var hit = FindStructureAttribute(sub, attrName);
                    if (hit != null) return hit;
                }
            }
            catch (Exception ex) { Logger.Debug("FindStructureAttribute: " + ex.Message); }
            return null;
        }

        // FR#4 + FR#5: resolve a Variable from the VariablesPart by name.
        private dynamic FindVariable(KBObject obj, string varName)
        {
            if (string.IsNullOrEmpty(varName)) return null;
            try
            {
                var vPart = obj.Parts.Cast<KBObjectPart>().FirstOrDefault(p => p.GetType().Name.Equals("VariablesPart"));
                if (vPart == null) return null;
                dynamic dPart = vPart;
                foreach (dynamic v in dPart.Variables)
                {
                    string n = null;
                    try { n = (string)v.Name; } catch { }
                    if (!string.IsNullOrEmpty(n) && string.Equals(n, varName, StringComparison.OrdinalIgnoreCase))
                    {
                        return v;
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("FindVariable: " + ex.Message); }
            return null;
        }

        private dynamic FindByPath(dynamic root, string[] segments)
        {
            dynamic current = root;
            foreach (var seg in segments)
            {
                if (current == null) return null;
                current = FindInControlCollection(current, seg);
                if (current == null) return null;
            }
            return current;
        }

        private dynamic FindInControlCollection(dynamic root, string name)
        {
            if (root == null) return null;
            try { if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase)) return root; } catch {}

            try {
                if (root.Controls != null) {
                    foreach (dynamic child in root.Controls) {
                        var found = FindInControlCollection(child, name);
                        if (found != null) return found;
                    }
                }
            } catch {}
            return null;
        }

        internal static JObject SerializeSelectedProperties(dynamic container, KBModel model,
            IEnumerable<string> names, bool webPanel = false)
        {
            var props = new JArray();
            foreach (string name in names)
            {
                dynamic entry = ResolvePropertyEntry(container, name);
                bool alias = entry == null && webPanel
                    && string.Equals(name, "MainProgram", StringComparison.OrdinalIgnoreCase);
                if (alias) entry = ResolvePropertyEntry(container, "IsMain");
                if (entry == null) continue;
                try
                {
                    var item = new JObject
                    {
                        ["name"] = alias ? "MainProgram" : entry.Name.ToString(),
                        ["value"] = RenderPropertyValue((object)entry.Value, model)
                    };
                    if (alias) item["nativeName"] = "IsMain";
                    try
                    {
                        if (entry.Definition != null)
                        {
                            item["type"] = entry.Definition.Type.ToString();
                            item["readOnly"] = entry.Definition.ReadOnly;
                        }
                    }
                    catch { }
                    props.Add(item);
                }
                catch (Exception ex) { Logger.Debug($"Recovery property '{name}' could not be read: {ex.Message}"); }
            }
            return new JObject { ["properties"] = props };
        }

        internal static JObject SerializeProperties(dynamic container, KBModel model = null)
        {
            var result = new JObject();
            var props = new JArray();

            try
            {
                if (container != null && container.Properties != null)
                {
                    foreach (dynamic prop in container.Properties)
                    {
                        try {
                            var pObj = new JObject();
                            pObj["name"] = prop.Name.ToString();
                            pObj["value"] = RenderPropertyValue((object)prop.Value, model);
                            
                            try {
                                if (prop.Definition != null) {
                                    pObj["type"] = prop.Definition.Type.ToString();
                                    pObj["readOnly"] = prop.Definition.ReadOnly;
                                }
                            } catch {}

                            props.Add(pObj);
                        } catch { }
                    }
                }
            }
            catch (Exception ex) { Logger.Debug($"General error in SerializeProperties: {ex.Message}"); }

            try
            {
                string basedOnName = DomainPropertyApplier.GetDomainBasedOnName((object)container);
                if (!string.IsNullOrEmpty(basedOnName))
                {
                    bool alreadyPresent = false;
                    foreach (JObject p in props)
                    {
                        if (string.Equals(p["name"]?.ToString(), "DomainBasedOn", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p["name"]?.ToString(), "BasedOn", StringComparison.OrdinalIgnoreCase))
                        {
                            alreadyPresent = true;
                            break;
                        }
                    }
                    if (!alreadyPresent)
                    {
                        props.Add(new JObject
                        {
                            ["name"] = "DomainBasedOn",
                            ["value"] = basedOnName,
                            ["type"] = "System.String",
                            ["readOnly"] = false
                        });
                    }
                }
            }
            catch { }

            try
            {
                var enumVals = DomainPropertyApplier.ReadEnumValues((object)container);
                if (enumVals != null && enumVals.Count > 0)
                {
                    bool alreadyPresent = false;
                    foreach (JObject p in props)
                    {
                        if (string.Equals(p["name"]?.ToString(), "EnumValues", StringComparison.OrdinalIgnoreCase))
                        {
                            alreadyPresent = true;
                            break;
                        }
                    }
                    if (!alreadyPresent)
                    {
                        props.Add(new JObject
                        {
                            ["name"] = "EnumValues",
                            ["value"] = enumVals.ToString(Newtonsoft.Json.Formatting.None),
                            ["type"] = "Artech.Genexus.Common.CustomTypes.EnumValues",
                            ["readOnly"] = false
                        });
                    }
                }
            }
            catch { }

            result["properties"] = props;
            return result;
        }
    }
}
