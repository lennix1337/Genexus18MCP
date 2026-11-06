using System;
using System.Collections.Generic;
using System.Text;
using GxMcp.Worker.Structure;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_compare — read-only "Compare Objects" parity over the GeneXus SDK's
    /// <see cref="IComparerService"/>. Resolves two KB objects by name (optionally
    /// type-scoped via <c>type</c>) and reports whether they're equal in content
    /// (mode=content, default) or top-level properties (mode=properties).
    ///
    /// Follows the same SDK-service-resolution idiom as
    /// <see cref="GxServerSyncService"/>: resolve via <c>SdkServices.TryGetService</c>,
    /// guard every null/throw path, never crash the worker. If the Comparer package
    /// isn't loaded in this session, returns a clean <c>ComparerServiceUnavailable</c>
    /// error instead of a partial/garbled result.
    ///
    /// Read-only — no Merge/write path is exercised here (see
    /// docs/sdk_coverage_gap_matrix.md P0 #2 for the Merge follow-up).
    /// </summary>
    public class CompareService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public CompareService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            string objectAName = args?["objectA"]?.ToString();
            string objectBName = args?["objectB"]?.ToString();
            string type = args?["type"]?.ToString();
            string mode = args?["mode"]?.ToString();
            if (string.IsNullOrWhiteSpace(mode)) mode = "content";
            mode = mode.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(objectAName) || string.IsNullOrWhiteSpace(objectBName))
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "objectA and objectB are required.",
                    hint: "Pass both objectA and objectB as existing object names.");
            }

            if (mode != "content" && mode != "properties")
            {
                return McpResponse.Err(
                    code: "BadArgs",
                    message: "Unknown mode '" + mode + "'. Expected 'content' or 'properties'.",
                    hint: "Pass mode=content or mode=properties.");
            }

            // issue #32 item 2 (generalized): resilient resolve — retries a lazy/late SDK
            // registration and force-resolves before hard-failing (self-heals a respawn).
            IComparerService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IComparerService>();

            if (svc == null)
            {
                return McpResponse.Err(
                    code: "ComparerServiceUnavailable",
                    message: "The GeneXus SDK's IComparerService is not registered in this worker session (self-heal retries were exhausted).",
                    hint: "The Comparer package may not be loaded in this worker. Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            KBObject objA;
            KBObject objB;
            try
            {
                objA = _objects?.FindObject(objectAName, type);
                objB = _objects?.FindObject(objectBName, type);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "CompareFailed", message: ex.Message, hint: "Check the worker log for details.");
            }

            if (objA == null)
            {
                return McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object '" + objectAName + "' not found.",
                    hint: "Confirm objectA exists (and matches type, if provided).",
                    nextSteps: new JArray(McpResponse.NextStep("genexus_query",
                        new JObject { ["name"] = objectAName },
                        "Search for objectA by name to confirm it exists and get its exact name/type.")));
            }
            if (objB == null)
            {
                return McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object '" + objectBName + "' not found.",
                    hint: "Confirm objectB exists (and matches type, if provided).",
                    nextSteps: new JArray(McpResponse.NextStep("genexus_query",
                        new JObject { ["name"] = objectBName },
                        "Search for objectB by name to confirm it exists and get its exact name/type.")));
            }

            try
            {
                if (mode == "properties")
                {
                    bool eqProps = Compatibility.SdkCompat.AreEqualInProperties(svc, objA, objB);
                    return McpResponse.Ok(
                        code: "CompareCompleted",
                        result: new JObject
                        {
                            ["equal"] = eqProps,
                            ["objectA"] = objA.Name,
                            ["objectB"] = objB.Name,
                            ["mode"] = "properties",
                            ["source"] = "sdk:IComparerService"
                        });
                }

                bool equal = Compatibility.SdkCompat.AreEqualInContent(svc, objA, objB);
                var result = new JObject
                {
                    ["equal"] = equal,
                    ["objectA"] = objA.Name,
                    ["objectB"] = objB.Name,
                    ["mode"] = "content",
                    ["source"] = "sdk:IComparerService"
                };

                if (!equal)
                {
                    AddPartDifferences(result, svc, objA, objB);
                }

                return McpResponse.Ok(code: "CompareCompleted", result: result);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "CompareFailed", message: ex.Message, hint: "Check the worker log for details.");
            }
        }

        internal const int MaxSourceChars = 1024 * 1024;
        internal const int MaxDiffChars = 16 * 1024;
        internal const int MaxTotalDiffBytes = 128 * 1024;

        private static void AddPartDifferences(JObject result, IComparerService svc, KBObject objA, KBObject objB)
        {
            var differences = new JArray();
            var diffs = new JArray();
            result["differences"] = differences;
            result["diffs"] = diffs;
            try
            {
                var partsB = new Dictionary<Guid, KBObjectPart>();
                foreach (KBObjectPart pb in objB.Parts)
                {
                    if (pb != null && !partsB.ContainsKey(pb.Type)) partsB[pb.Type] = pb;
                }

                foreach (KBObjectPart pa in objA.Parts)
                {
                    string key = pa?.TypeDescriptor?.Name;
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!partsB.TryGetValue(pa.Type, out var pb)) continue;

                    AppendPartDifference(differences, diffs, key,
                        ResolveReadPart(objA.TypeDescriptor?.Name, pa.Type, key),
                        () => Compatibility.SdkCompat.AreEqualInContent(svc, pa, pb, false),
                        SourceReader(pa), SourceReader(pb));
                }
            }
            catch { result["diffsOmittedReason"] = "readFailed"; }
        }

        internal static Func<string> SourceReader(object part)
        {
            if (part is ISource source) return () => source.Source ?? "";
            // Match genexus_read's textual fallback (not SerializeToXml).
            try
            {
                var property = part.GetType().GetProperty("Source");
                return property?.PropertyType == typeof(string) && property.CanRead
                    ? (Func<string>)(() => (string)property.GetValue(part, null) ?? "") : null;
            }
            catch { return () => throw new InvalidOperationException("Source reader unavailable."); }
        }

        internal static string ResolveReadPart(string objectType, Guid partType, string descriptor)
        {
            if (partType == PartAccessor.DesignSystemTokensPartGuid) return "Tokens";
            if (partType == PartAccessor.DesignSystemStylesPartGuid) return "Styles";
            foreach (string alias in new[] { "Events", "Rules", "Conditions", "Source", "Variables", "Structure", "WebForm", "Layout", "Help" })
                if (partType != Guid.Empty && PartAccessor.GetPartGuid(objectType ?? "", alias) == partType)
                    return alias;
            return descriptor;
        }

        internal static void AppendPartDifference(JArray differences, JArray diffs, string descriptor,
            string part, Func<bool> areEqual, Func<string> readA, Func<string> readB)
        {
            var item = new JObject { ["part"] = part, ["partType"] = descriptor };
            try { if (areEqual()) return; }
            catch
            {
                item["omittedReason"] = "compareFailed";
                diffs.Add(item);
                return; // Cannot assert this part differs; preserve the SDK verdict.
            }
            differences.Add(descriptor);
            diffs.Add(item);
            if (readA == null || readB == null) { item["omittedReason"] = "nonTextualPart"; return; }
            try
            {
                var text = BuildTextDiff(readA(), readB());
                item.Merge(text);
                // Budget JSON bytes, including escaping, before crossing the Worker pipe.
                // Reserve room below the Gateway's 220 KB guard for the SDK verdict/metadata.
                if (item["unified"] != null && Encoding.UTF8.GetByteCount(diffs.ToString(Newtonsoft.Json.Formatting.None)) > MaxTotalDiffBytes)
                {
                    item.Remove("unified");
                    item.Merge(TruncatedDiff());
                    item["limit"] = "totalDiffBytes";
                    item.Remove("maxChars");
                    item["maxBytes"] = MaxTotalDiffBytes;
                }
            }
            catch { item["omittedReason"] = "readFailed"; }
        }

        internal static JObject BuildTextDiff(string before, string after)
        {
            if (before == null || after == null) return new JObject { ["omittedReason"] = "readFailed" };
            if (before.Length > MaxSourceChars || after.Length > MaxSourceChars)
                return new JObject { ["omittedReason"] = "truncated", ["truncated"] = true,
                    ["limit"] = "inputChars", ["maxChars"] = MaxSourceChars };
            before = before.Replace("\r\n", "\n").Replace('\r', '\n');
            after = after.Replace("\r\n", "\n").Replace('\r', '\n');
            if (before == after) return new JObject { ["omittedReason"] = "normalizedTextEqual" };
            string[] Lines(string text) => text.Length == 0 ? new string[0]
                : (text.EndsWith("\n", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text).Split('\n');
            var a = Lines(before);
            var b = Lines(after);
            bool Same(int i, int j) => a[i] == b[j]
                && (i < a.Length - 1 || before.EndsWith("\n", StringComparison.Ordinal))
                    == (j < b.Length - 1 || after.EndsWith("\n", StringComparison.Ordinal));
            int prefix = 0, suffix = 0;
            while (prefix < a.Length && prefix < b.Length && Same(prefix, prefix)) prefix++;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix
                && Same(a.Length - suffix - 1, b.Length - suffix - 1)) suffix++;
            int start = Math.Max(0, prefix - 3);
            int endA = Math.Min(a.Length, a.Length - suffix + 3);
            int endB = Math.Min(b.Length, b.Length - suffix + 3);
            // ponytail: one contiguous replacement hunk keeps memory/time linear;
            // use a bounded shortest-edit algorithm only if minimal hunks become required.
            var diff = new StringBuilder("--- a/part\n+++ b/part\n");
            diff.AppendFormat("@@ -{0},{1} +{2},{3} @@\n", endA == start ? start : start + 1,
                endA - start, endB == start ? start : start + 1, endB - start);
            bool Line(char marker, string[] lines, int index, string text)
            {
                diff.Append(marker).Append(lines[index]).Append('\n');
                if (index == lines.Length - 1 && !text.EndsWith("\n", StringComparison.Ordinal))
                    diff.Append("\\ No newline at end of file\n");
                return diff.Length <= MaxDiffChars;
            }
            for (int i = start; i < prefix; i++) if (!Line(' ', a, i, before)) return TruncatedDiff();
            for (int i = prefix; i < a.Length - suffix; i++) if (!Line('-', a, i, before)) return TruncatedDiff();
            for (int i = prefix; i < b.Length - suffix; i++) if (!Line('+', b, i, after)) return TruncatedDiff();
            for (int i = a.Length - suffix; i < endA; i++) if (!Line(' ', a, i, before)) return TruncatedDiff();
            return new JObject { ["unified"] = diff.ToString(), ["truncated"] = false };
        }

        private static JObject TruncatedDiff() => new JObject
        { ["omittedReason"] = "truncated", ["truncated"] = true, ["limit"] = "unifiedChars", ["maxChars"] = MaxDiffChars };
    }
}
