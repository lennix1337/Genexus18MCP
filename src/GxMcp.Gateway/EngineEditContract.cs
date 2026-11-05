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

        internal static string? TryPlan(JObject args, out EngineEditPlan? plan)
        {
            plan = null;
            if (args == null) return "the edit arguments are missing";

            if (args["targets"] is JArray)
                return "`targets[]` is outside the native edit space; only single-object edits are served";
            if (args["parts"] is JArray)
                return "`parts[]` is outside the native edit space; only single-part edits are served";
            if (args["changeSet"] != null)
                return "`changeSet` is outside the native edit space";
            if (args["changes"] != null)
                return "`changes` is not supported; use single-part content";
            if (args["dryRun"]?.ToObject<bool?>() == true)
                return "`dryRun` is outside the native edit space; dry-run previews fall back to SDK";

            string? mode = args["mode"]?.ToString();
            if (!string.IsNullOrWhiteSpace(mode)
                && !string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "xml", StringComparison.OrdinalIgnoreCase))
            {
                return $"`mode` '{mode}' is outside the native edit space; only full replacement is served";
            }

            string? name = args["name"]?.ToString() ?? args["objectName"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return "a single object `name` is required";

            string requestedPart = args["part"]?.ToString() ?? "Source";
            string? part = CanonicalPart(requestedPart);
            if (part == null)
                return $"`part` '{requestedPart}' is outside the native edit space; it serves Source, Events, Rules and Conditions";

            string? content = args["content"]?.ToString() ?? args["xml"]?.ToString();
            if (content == null)
                return "a string `content` (or `xml`) is required for full-part replacement";

            string? expectedKind = args["type"]?.ToString();

            plan = new EngineEditPlan(name.Trim(), part, content, expectedKind);
            return null;
        }

        internal static List<string> ToEngineArguments(string kbPath, EngineEditPlan plan, string tempContentFile)
        {
            var engineArgs = new List<string>
            {
                "--diagnostics=json",
                "kb-write",
                kbPath,
                plan.Name,
                "--part",
                plan.Part,
                "--content-file",
                tempContentFile
            };
            if (!string.IsNullOrWhiteSpace(plan.ExpectedKind))
            {
                engineArgs.Add("--type");
                engineArgs.Add(plan.ExpectedKind!);
            }
            return engineArgs;
        }

        internal static bool TryShape(
            EngineEditPlan plan, JObject engineJson, JObject engineInfo,
            out JObject? payload, out string? refusal)
        {
            refusal = null;
            payload = null;

            if (engineJson == null)
            {
                refusal = "the engine returned an empty answer";
                return false;
            }

            var result = new JObject
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

            payload = result;
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
    }
}
