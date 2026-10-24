using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Issue #60 — parse a BuildService status envelope (the JSON emitted by
    /// GetStatus/GetResult after a SpecifyOneOnly pass) into structured
    /// diagnostics: <c>[{code, object, member, message}]</c>.
    ///
    /// Pure (no GeneXus SDK) so it's unit-testable with canned build output.
    /// Handles the mixed naming the status envelope actually uses — PascalCase
    /// CLR property names (<c>Status</c>, <c>ErrorCount</c>, <c>ErrorsDetailed</c>)
    /// coexist with <c>[JsonProperty]</c>-renamed computed getters
    /// (<c>specErrorCount</c>, <c>codeErrors</c>) — via case-insensitive lookup.
    /// </summary>
    public static class SpecificationDiagnostics
    {
        // "error spc0056: ..." / "error src0294: ..." / "error CS0246: ..." / "error gtm0092: ..."
        private static readonly Regex _rxErrorCode = new Regex(
            @"\berror\s+(?<code>[A-Za-z]{2,4}\d+)\s*:",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Issue #56 style: "Variable Empresaid definition is incorrect or not available."
        // Also matches "&DataLimite" tokens and single-quoted identifiers.
        private static readonly Regex _rxMember = new Regex(
            @"(?i)(?:variable\s+&?(?<m>[A-Za-z_][A-Za-z0-9_.]*)|['""](?<m>[A-Za-z_][A-Za-z0-9_.]*)['""]|&(?<m>[A-Za-z_][A-Za-z0-9_.]*))",
            RegexOptions.Compiled);

        /// <summary>
        /// Case-insensitive JObject field read (the build envelope mixes
        /// PascalCase CLR names with JsonProperty-renamed snake/camel keys).
        /// </summary>
        private static JToken Get(JObject jo, params string[] names)
        {
            if (jo == null) return null;
            foreach (var n in names)
            {
                var hit = jo.Property(n, StringComparison.OrdinalIgnoreCase);
                if (hit != null) return hit.Value;
            }
            return null;
        }

        /// <summary>
        /// Extract the status token ("Succeeded" / "Failed" / "Accepted" / "Error" ...).
        /// </summary>
        public static string GetStatus(string statusJson)
        {
            if (string.IsNullOrWhiteSpace(statusJson)) return string.Empty;
            try
            {
                var jo = JObject.Parse(statusJson);
                return Get(jo, "Status", "status")?.ToString() ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Extract the `_meta.snapshot` baseline token (if present) so callers can chain
        /// GetStatusWait calls — passing a null/empty baseline makes the wait return
        /// immediately, so a chained poll must thread the previous snapshot back in.
        /// Returns string.Empty when the envelope has no baseline (unparseable or absent).
        /// </summary>
        public static string GetSnapshot(string statusJson)
        {
            if (string.IsNullOrWhiteSpace(statusJson)) return string.Empty;
            try
            {
                var jo = JObject.Parse(statusJson);
                return (jo["_meta"] as JObject)?["snapshot"]?.ToString() ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        public static bool IsSucceeded(string statusJson)
        {
            string s = GetStatus(statusJson);
            return string.Equals(s, "Succeeded", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTerminal(string statusJson)
        {
            string s = GetStatus(statusJson);
            return string.Equals(s, "Succeeded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the envelope carries any source/specification error
        /// (spc#### / gen#### / src#### / qry####) —
        /// the diagnostic families issue #60 cares about. Environment (MSB/CS/gtm and
        /// related infrastructure families) and authored-code (CS) errors are surfaced
        /// separately by the parser but do not, by themselves, flip this to true (a KB
        /// environment gap is not the edited object's fault).
        /// </summary>
        public static bool HasSpecErrors(string statusJson)
        {
            if (string.IsNullOrWhiteSpace(statusJson)) return false;
            var diags = Parse(statusJson);
            foreach (var d in diags)
            {
                string code = d["code"]?.ToString() ?? string.Empty;
                if (code.StartsWith("spc", StringComparison.OrdinalIgnoreCase)
                    || code.StartsWith("gen", StringComparison.OrdinalIgnoreCase)
                    || code.StartsWith("src", StringComparison.OrdinalIgnoreCase)
                    || code.StartsWith("qry", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // The specifier's "[SampleProc, line 1]" location suffix.
        private static readonly Regex _rxObjectLine = new Regex(
            @"\[(?<obj>[^\],]+),\s*line\s+(?<line>\d+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const string StaleCopyMarker = "changed since the last build";

        /// <summary>
        /// Warning lines of a build-status envelope (<c>warnings</c> array of strings).
        /// The compact status the orchestrator polls moves them to <c>newWarnings</c>
        /// and leaves <c>warnings</c> empty, so both are read.
        /// </summary>
        public static JArray GetWarnings(string statusJson)
        {
            var result = new JArray();
            if (string.IsNullOrWhiteSpace(statusJson)) return result;
            try
            {
                var jo = JObject.Parse(statusJson);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var source in new[] { Get(jo, "Warnings", "warnings"), Get(jo, "NewWarnings", "newWarnings") })
                {
                    if (!(source is JArray arr)) continue;
                    foreach (var w in arr)
                    {
                        string text = w?.ToString();
                        if (!string.IsNullOrWhiteSpace(text) && seen.Add(text)) result.Add(text);
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// True when a warning says the specifier read the environment's copy of an object
        /// that changed since the last build, so the diagnostics may describe stale source.
        /// </summary>
        public static bool HasStaleEnvironmentCopy(JArray warnings)
        {
            if (warnings == null) return false;
            foreach (var w in warnings)
                if (IsStaleEnvironmentCopyWarning(w?.ToString()))
                    return true;
            return false;
        }

        /// <summary>True for the specifier warning that an object changed since the last build.</summary>
        public static bool IsStaleEnvironmentCopyWarning(string line)
            => (line ?? string.Empty).IndexOf(StaleCopyMarker, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Parse a build-status envelope into structured diagnostics.
        /// Returns an empty array for unparseable input.
        /// </summary>
        public static JArray Parse(string statusJson)
        {
            var result = new JArray();
            if (string.IsNullOrWhiteSpace(statusJson)) return result;

            JObject jo;
            try { jo = JObject.Parse(statusJson); }
            catch { return result; }

            // 1) Structured detail rows (ErrorsDetailed / errorsDetailed) — preferred.
            var detailed = Get(jo, "ErrorsDetailed", "errorsDetailed") as JArray;
            if (detailed != null && detailed.Count > 0)
            {
                foreach (var item in detailed)
                {
                    var d = item as JObject;
                    if (d == null) continue;
                    string raw = d["raw"]?.ToString() ?? string.Empty;
                    string rewritten = d["rewritten"]?.ToString();
                    string line = string.IsNullOrWhiteSpace(rewritten) ? raw : rewritten;
                    var diag = BuildDiagnostic(line, d["gxObject"]?.ToString());
                    if (diag != null) result.Add(diag);
                }
                // Some SDK builds populate ErrorsDetailed with metadata rows that
                // have no raw/rewritten text. In that case retain the flat Errors
                // fallback instead of silently returning an empty diagnostic list.
                if (result.Count > 0) return result;
            }

            // 2) Flat string rows (Errors / errors).
            var flat = Get(jo, "Errors", "errors") as JArray;
            if (flat != null && flat.Count > 0)
            {
                foreach (var item in flat)
                {
                    var diag = BuildDiagnostic(item?.ToString() ?? string.Empty, null);
                    if (diag != null) result.Add(diag);
                }
            }
            return result;
        }

        private static JObject BuildDiagnostic(string line, string gxObject)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            string code = null;
            var m = _rxErrorCode.Match(line);
            if (m.Success) code = m.Groups["code"].Value;

            string message = line.Trim();
            if (code != null)
            {
                // Strip the "error spc0056:" prefix so the message reads cleanly.
                int idx = message.IndexOf("error " + code + ":", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) idx = message.IndexOf("error :", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    message = message.Substring(idx).TrimStart();
                    int colon = message.IndexOf(':');
                    if (colon >= 0) message = message.Substring(colon + 1).Trim();
                }
                // Also strip MSBuild location prefixes (e.g. "C:\...\foo.cs(12,3):").
                var loc = Regex.Match(message, @"^.*\.cs\(\d+,\d+\):\s*");
                if (loc.Success) message = message.Substring(loc.Length);
            }

            string member = null;
            var mm = _rxMember.Match(line);
            if (mm.Success) member = mm.Groups["m"].Value;

            string obj = string.IsNullOrWhiteSpace(gxObject) ? null : gxObject;
            int? srcLine = null;
            var ol = _rxObjectLine.Match(line);
            if (ol.Success)
            {
                if (obj == null) obj = ol.Groups["obj"].Value.Trim();
                srcLine = int.Parse(ol.Groups["line"].Value);
            }

            var diag = new JObject();
            if (!string.IsNullOrEmpty(code)) diag["code"] = code;
            if (!string.IsNullOrEmpty(obj)) diag["object"] = obj;
            if (srcLine.HasValue) diag["line"] = srcLine.Value;
            if (!string.IsNullOrEmpty(member)) diag["member"] = member;
            diag["message"] = message;
            diag["raw"] = line.Trim();
            return diag;
        }
    }
}
