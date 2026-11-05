using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The admitted space of the native single-part read route
    /// (<c>genexus_read</c> -&gt; <c>kb-read --page</c>).
    ///
    /// It deliberately serves less than the SDK read tool: one existing object
    /// resolved by name, one stored part whose SDK text is the stored text
    /// verbatim (Source, Events, Rules, Conditions), the MCP pagination policy
    /// and a page window. Everything else - the full-object read, <c>parts</c>,
    /// <c>type</c>/<c>guid</c>/<c>entityKey</c>/<c>path</c> selectors,
    /// <c>refresh</c>, conditional reads, non-MCP formats and parts with a
    /// different SDK projection - stays on the SDK.
    /// </summary>
    internal sealed class EngineReadPlan
    {
        internal EngineReadPlan(string name, string part, int? offset, int? limit)
        {
            Name = name;
            Part = part;
            Offset = offset;
            Limit = limit;
        }

        internal string Name { get; }
        internal string Part { get; }
        internal int? Offset { get; }
        internal int? Limit { get; }

        /// <summary>No explicit window: the MCP 200-line/16 KB default applies.</summary>
        internal bool DefaultPage => !Offset.HasValue && !Limit.HasValue;
    }

    internal static class EngineReadContract
    {
        private static readonly string[] AdmittedParts = { "Source", "Events", "Rules", "Conditions" };

        internal static string? TryPlan(JObject args, out EngineReadPlan? plan)
        {
            plan = null;
            if (args == null) return "the read arguments are missing";

            if (!TryOptionalString(args, "name", out string? name, out bool hasName, out string? nameError))
                return nameError!;
            if (!TryOptionalString(args, "objectName", out string? objectName, out bool hasObjectName, out string? objectNameError))
                return objectNameError!;
            if (hasName && hasObjectName && !string.Equals(name, objectName, StringComparison.Ordinal))
                return "`name` and `objectName` name different objects";

            string? target = hasName ? name : objectName;
            if (string.IsNullOrWhiteSpace(target))
                return "a single object `name` is required";

            if (!TryOptionalString(args, "part", out string? requestedPart, out bool hasPart, out string? partError))
                return partError!;
            if (!hasPart || string.IsNullOrWhiteSpace(requestedPart))
                return "a single `part` is required";
            string? part = CanonicalPart(requestedPart!);
            if (part == null)
                return $"`part` '{requestedPart}' is outside the native read space; it serves Source, Events, Rules and Conditions";

            if (!TryOptionalString(args, "format", out string? format, out bool hasFormat, out string? formatError))
                return formatError!;
            if (hasFormat && !string.Equals(format, "mcp", StringComparison.OrdinalIgnoreCase))
                return $"`format` '{format}' is outside the native read space; only the MCP format is served";

            // `fields` is shared by the engine contract and the SDK ignores it on a
            // read, so it cannot make the smaller native answer non-equivalent.
            // These shape the read instead and have no admitted native meaning.
            if (IsRequested(args, "refresh")) return "`refresh` is outside the native read space";
            if (IsRequested(args, "raw")) return "`raw` is outside the native read space";
            if (IsRequested(args, "full")) return "`full` is outside the native read space";
            if (HasNonEmptyValue(args, "projection")) return "`projection` is outside the native read space";
            if (IsRequested(args, "ifUnchangedSince")) return "`ifUnchangedSince` is outside the native read space";
            if (IsRequested(args, "certifyRecovery")) return "`certifyRecovery` is outside the native read space";

            if (!TryOptionalNonNegativeInt(args, "offset", out int? offset, out string? offsetError))
                return offsetError!;
            if (!TryOptionalNonNegativeInt(args, "limit", out int? limit, out string? limitError))
                return limitError!;

            plan = new EngineReadPlan(target!, part, offset, limit);
            return null;
        }

        internal static List<string> ToEngineArguments(string kbPath, EngineReadPlan plan)
        {
            var args = new List<string>
            {
                "--diagnostics=json",
                "kb-read",
                kbPath,
                plan.Name,
                "--page",
                "--parts",
                plan.Part,
                "--client",
                "mcp",
            };
            if (plan.Offset.HasValue)
            {
                args.Add("--offset");
                args.Add(plan.Offset.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (plan.Limit.HasValue)
            {
                args.Add("--limit");
                args.Add(plan.Limit.Value.ToString(CultureInfo.InvariantCulture));
            }
            return args;
        }

        /// <summary>
        /// Projects the engine's flat page JSON into the SDK single-part read
        /// envelope. The engine's answer is authoritative for the content,
        /// pagination and token; the Gateway adds only the truthful native
        /// freshness origin and its engine block.
        /// </summary>
        internal static bool TryShape(
            EngineReadPlan plan,
            JObject json,
            JObject engineInfo,
            out JObject? payload,
            out string? error)
        {
            payload = null;
            error = null;
            if (json == null)
            {
                error = "the engine returned no read object";
                return false;
            }
            if (json["error"] != null)
            {
                error = $"the engine returned an error object: {json["error"]}";
                return false;
            }

            if (!TryGetString(json, "part", out string? part) || string.IsNullOrWhiteSpace(part))
            {
                error = "the engine read has no `part` string";
                return false;
            }
            if (!string.Equals(part, plan.Part, StringComparison.OrdinalIgnoreCase))
            {
                error = $"the engine returned part '{part}' for a request of '{plan.Part}'";
                return false;
            }
            if (!TryGetString(json, "source", out string? source))
            {
                error = "the engine read has no `source` string";
                return false;
            }
            if (!TryGetString(json, "versionToken", out string? versionToken) || string.IsNullOrWhiteSpace(versionToken))
            {
                error = "the engine read has no `versionToken`";
                return false;
            }
            if (!TryGetBool(json, "isBase64", out bool isBase64) || isBase64)
            {
                error = "the engine read is not plain text (`isBase64` must be false)";
                return false;
            }
            if (!TryGetBool(json, "isEmpty", out bool isEmpty) || isEmpty != string.IsNullOrEmpty(source))
            {
                error = "the engine read has an inconsistent `isEmpty` boolean";
                return false;
            }
            if (!TryGetBool(json, "truncated", out bool truncated)
                || !TryGetBool(json, "isTruncatedByWorker", out bool isTruncatedByWorker)
                || truncated != isTruncatedByWorker)
            {
                error = "the engine read has inconsistent truncation fields";
                return false;
            }
            if (!TryGetNonNegativeInt(json, "offset", out int offset)
                || !TryGetNonNegativeInt(json, "limit", out int limit)
                || !TryGetNonNegativeInt(json, "totalLines", out int totalLines)
                || !TryGetNonNegativeInt(json, "totalBytes", out int totalBytes))
            {
                error = "the engine read has invalid pagination counts";
                return false;
            }
            if (limit > totalLines)
            {
                error = "the engine read returns more lines than the complete text holds";
                return false;
            }

            bool explicitFullRead = false;
            JToken? explicitToken = json["explicitFullRead"];
            if (explicitToken != null && explicitToken.Type != JTokenType.Null)
            {
                if (explicitToken.Type != JTokenType.Boolean)
                {
                    error = "the engine read has a non-boolean `explicitFullRead`";
                    return false;
                }
                explicitFullRead = explicitToken.Value<bool>();
            }
            if (!ValidateWindow(plan, offset, limit, totalLines, truncated, explicitFullRead, out error))
                return false;

            int? suggestedNextOffset = null;
            int? suggestedNextLimit = null;
            if (truncated)
            {
                if (!TryGetNonNegativeInt(json, "suggestedNextOffset", out int nextOffset)
                    || !TryGetNonNegativeInt(json, "suggestedNextLimit", out int nextLimit)
                    || (long)nextOffset != (long)offset + limit
                    || nextLimit != (plan.Limit ?? 200))
                {
                    error = "the engine read is truncated without a valid next page";
                    return false;
                }
                suggestedNextOffset = nextOffset;
                suggestedNextLimit = nextLimit;
            }

            var result = new JObject
            {
                ["part"] = plan.Part,
                ["isTruncatedByWorker"] = truncated,
                ["truncated"] = truncated,
                ["isEmpty"] = isEmpty,
                ["source"] = source,
                ["isBase64"] = false,
                ["offset"] = offset,
                ["limit"] = limit,
                ["totalLines"] = totalLines,
                ["totalBytes"] = totalBytes,
                ["versionToken"] = versionToken,
                ["readFreshness"] = new JObject
                {
                    // The SDK's origin names SDK object state. The native reader is
                    // a different source, so it reports its own origin rather than
                    // claiming SDK cache freshness it never observed.
                    ["origin"] = "engine-object-state",
                    ["observedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["ageMs"] = 0,
                    ["sdkCacheRefreshConfirmed"] = false,
                },
                ["engine"] = engineInfo,
            };
            if (explicitFullRead) result["explicitFullRead"] = true;
            if (truncated)
            {
                result["suggestedNextOffset"] = suggestedNextOffset!.Value;
                result["suggestedNextLimit"] = suggestedNextLimit!.Value;
                if (plan.DefaultPage)
                {
                    result["message"] = "MCP read defaulted to ~200 lines / 16 KB to control context size. "
                        + "Use offset/limit to paginate, or limit=0 to read in full.";
                }
            }

            payload = result;
            return true;
        }

        private static bool ValidateWindow(
            EngineReadPlan plan, int offset, int limit, int totalLines,
            bool truncated, bool explicitFullRead, out string? error)
        {
            error = "the engine read pagination does not match the requested window";
            bool fullReadRequested = plan.Limit == 0;
            int requestedOffset = plan.Offset ?? 0;
            int expectedOffset = fullReadRequested ? Math.Min(requestedOffset, totalLines) : requestedOffset;
            if (explicitFullRead != fullReadRequested || offset != expectedOffset) return false;

            int remaining = Math.Max(0, totalLines - offset);
            int maximum = fullReadRequested ? remaining : Math.Min(remaining, plan.Limit ?? 200);
            if (limit > maximum || (remaining > 0 && limit == 0)) return false;
            // Only the MCP default can stop early at its byte budget. Explicit
            // windows must return every line up to the requested boundary.
            if ((!plan.DefaultPage || fullReadRequested) && limit != maximum) return false;
            if (truncated != ((long)offset + limit < totalLines)) return false;
            if (fullReadRequested && truncated) return false;
            error = null;
            return true;
        }

        private static string? CanonicalPart(string value)
        {
            foreach (string part in AdmittedParts)
            {
                if (string.Equals(part, value, StringComparison.OrdinalIgnoreCase)) return part;
            }
            return null;
        }

        private static bool TryOptionalString(
            JObject args, string name, out string? value, out bool present, out string? error)
        {
            value = null;
            error = null;
            JToken? token = args[name];
            present = token != null && token.Type != JTokenType.Null;
            if (!present) return true;
            if (token!.Type != JTokenType.String)
            {
                error = $"`{name}` must be a string";
                return false;
            }
            value = token.ToString();
            return true;
        }

        private static bool TryOptionalNonNegativeInt(
            JObject args, string name, out int? value, out string? error)
        {
            value = null;
            error = null;
            JToken? token = args[name];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (!TryGetNonNegativeInt(args, name, out int raw))
            {
                error = $"`{name}` must be a non-negative integer no greater than {int.MaxValue}";
                return false;
            }
            value = raw;
            return true;
        }

        private static bool TryGetString(JObject json, string name, out string? value)
        {
            value = null;
            JToken? token = json[name];
            if (token == null || token.Type != JTokenType.String) return false;
            value = token.ToString();
            return true;
        }

        private static bool TryGetBool(JObject json, string name, out bool value)
        {
            value = false;
            JToken? token = json[name];
            if (token == null || token.Type != JTokenType.Boolean) return false;
            value = token.Value<bool>();
            return true;
        }

        private static bool TryGetNonNegativeInt(JObject json, string name, out int value)
        {
            value = 0;
            JToken? token = json[name];
            if (token == null || token.Type != JTokenType.Integer) return false;
            return int.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsRequested(JObject args, string name)
        {
            JToken? token = args[name];
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (token.Type == JTokenType.String) return !string.IsNullOrWhiteSpace(token.ToString());
            return true;
        }

        private static bool HasNonEmptyValue(JObject args, string name)
        {
            JToken? token = args[name];
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.String) return !string.IsNullOrWhiteSpace(token.ToString());
            if (token.Type == JTokenType.Array) return ((JArray)token).Count > 0;
            return true;
        }
    }
}
