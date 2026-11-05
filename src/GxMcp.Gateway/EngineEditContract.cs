using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The planned parameters for an engine-served edit operation
    /// (<c>genexus_edit</c> -&gt; <c>gx-engine kb-write</c>).
    /// </summary>
    internal sealed class EngineEditPlan
    {
        internal EngineEditPlan(string name, string part, string content, string? expectedKind)
        {
            Name = name;
            Part = part;
            Content = content;
            ExpectedKind = expectedKind;
        }

        internal string Name { get; }
        internal string Part { get; }
        internal string Content { get; }
        internal string? ExpectedKind { get; }
    }

    /// <summary>
    /// The admitted space and argument contract for native edits via gx-engine.
    /// </summary>
    internal static class EngineEditContract
    {
        private static readonly string[] AdmittedParts = { "Source", "Events", "Rules", "Conditions" };
        private static readonly HashSet<string> AdmittedArgumentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "name", "objectName", "part", "content", "xml", "mode", "type", "kb", "dryRun" };

        internal static string? TryPlan(JObject args, out EngineEditPlan? plan)
        {
            plan = null;
            string? refusal = ValidateEnvelope(args);
            if (refusal != null) return refusal;

            string? name = StringPair(args, "name", "objectName", out string? nameErr);
            if (nameErr != null) return nameErr;
            if (string.IsNullOrWhiteSpace(name)) return "a single object `name` is required";

            string? rawPart = StringField(args, "part", out string? partErr);
            if (partErr != null) return partErr;
            string requestedPart = !string.IsNullOrWhiteSpace(rawPart) ? rawPart! : "Source";
            string? part = CanonicalPart(requestedPart);
            if (part == null)
                return $"`part` '{requestedPart}' is outside the native edit space; it serves Source, Events, Rules and Conditions";

            string? content = StringPair(args, "content", "xml", out string? contentErr);
            if (contentErr != null) return contentErr;
            if (content == null) return "a string `content` (or `xml`) is required for full-part replacement";

            string? expectedKind = StringField(args, "type", out string? typeErr);
            if (typeErr != null) return typeErr;
            plan = new EngineEditPlan(name.Trim(), part, content, expectedKind);
            return null;
        }

        private static string? ValidateEnvelope(JObject args)
        {
            if (args == null) return "the edit arguments are missing";
            if (args["targets"] is JArray) return "`targets[]` is outside the native edit space; only single-object edits are served";
            if (args["parts"] is JArray) return "`parts[]` is outside the native edit space; only single-part edits are served";
            if (args["changeSet"] != null) return "`changeSet` is outside the native edit space";
            if (args["changes"] != null) return "`changes` is not supported; use single-part content";
            if (args["dryRun"] is JToken dry && dry.Type != JTokenType.Null && (dry.Type != JTokenType.Boolean || dry.Value<bool>()))
                return "`dryRun` is outside the native edit space; dry-run previews fall back to SDK";
            foreach (JProperty prop in args.Properties())
                if (prop.Value.Type != JTokenType.Null && !AdmittedArgumentNames.Contains(prop.Name))
                    return $"`{prop.Name}` is outside the native edit space";
            string? mode = StringField(args, "mode", out string? modeErr);
            if (modeErr != null) return modeErr;
            if (!string.IsNullOrWhiteSpace(mode) && !string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "xml", StringComparison.OrdinalIgnoreCase))
                return $"`mode` '{mode}' is outside the native edit space; only full replacement is served";
            return null;
        }

        internal static List<string> ToEngineArguments(string kbPath, EngineEditPlan plan, string tempContentFile)
        {
            var engineArgs = new List<string>
                { "--diagnostics=json", "kb-write", kbPath, plan.Name, "--part", plan.Part, "--content-file", tempContentFile };
            if (!string.IsNullOrWhiteSpace(plan.ExpectedKind)) { engineArgs.Add("--type"); engineArgs.Add(plan.ExpectedKind!); }
            return engineArgs;
        }

        internal static bool TryShape(
            EngineEditPlan plan, JObject engineJson, JObject engineInfo, out JObject? payload, out string? refusal)
        {
            refusal = engineJson == null ? "the engine returned an empty answer" : null;
            payload = engineJson == null ? null : new JObject
            {
                ["status"] = "success",
                ["name"] = plan.Name,
                ["part"] = plan.Part,
                ["type"] = engineJson["type"]?.ToString(),
                ["persisted"] = true,
                ["bytes"] = engineJson["bytes"]?.ToObject<int>() ?? plan.Content.Length,
                ["versionId"] = engineJson["versionId"]?.ToObject<int>() ?? 1,
                ["engine"] = engineInfo
            };
            return payload != null;
        }

        private static string? CanonicalPart(string value)
        {
            foreach (string part in AdmittedParts)
            {
                if (string.Equals(part, value, StringComparison.OrdinalIgnoreCase)) return part;
            }
            return null;
        }

        private static string? StringPair(JObject args, string primary, string alias, out string? error)
        {
            string? a = StringField(args, primary, out error);
            if (error != null) return null;
            string? b = StringField(args, alias, out error);
            if (error == null && a != null && b != null && !string.Equals(a, b, StringComparison.Ordinal))
                error = $"`{primary}` and `{alias}` must not differ";
            return error == null ? (a ?? b) : null;
        }

        private static string? StringField(JObject args, string name, out string? error)
        {
            JToken? t = args[name];
            error = t != null && t.Type != JTokenType.Null && t.Type != JTokenType.String ? $"`{name}` must be a string" : null;
            return error == null ? t?.Value<string>() : null;
        }
    }
}
