using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// v2.8.0 (#1) — natural-language → tool router for weakly-capable LLMs.
    ///
    /// The LLM describes an intent in plain text ("delete the panel
    /// MyPanel"), and HelpService.RouteGoal returns the most likely tool
    /// call shape: { tool, args, why, confidence }. The LLM either uses it
    /// directly or refines.
    ///
    /// The matcher is intentionally tiny — a hand-curated keyword scorer
    /// over the ~25 most-used intents. Bigger than that and we'd be
    /// shipping a model. Empirically the intent vocabulary maps 1:1 to
    /// a handful of verbs (read, edit, delete, build, apply, rename,
    /// list, search) crossed with object kinds.
    /// </summary>
    public class HelpService
    {
        // Each entry: keyword(s) → {tool, defaultArgs, why-template}.
        private static readonly List<Intent> Intents = new List<Intent>
        {
            new Intent("read inspect show open look view", "genexus_read",
                "Read parts of an object.", new[] { "name" }),
            new Intent("inspect metadata structure signature variables", "genexus_inspect",
                "Snapshot object metadata + structure (no source text).", new[] { "name" }),
            new Intent("edit modify change update replace patch write", "genexus_edit",
                "Edit a part of an object. Use mode=patch for find/replace, mode=full for whole-body.", new[] { "name", "part", "mode" }),
            new Intent("delete remove drop destroy", "genexus_delete_object",
                "Delete an object from the KB. Mutating — pass clientRequestId.", new[] { "name", "type" }),
            new Intent("rename refactor", "genexus_rename_across_kb",
                "Rename an object (or attribute) and update every caller.", new[] { "from", "to" }),
            new Intent("create new add make build-object", "genexus_create",
                "Create a new object.", new[] { "name", "type" }),
            new Intent("popup dialog modal", "genexus_create_popup",
                "Create a WorkWithPlus-aware layout-form popup WebPanel.", new[] { "name" }),
            new Intent("apply pattern wwp workwithplus", "genexus_apply_pattern",
                "Apply a GeneXus pattern (e.g. WorkWithPlus) to an object.", new[] { "name", "pattern" }),
            new Intent("list ls enumerate", "genexus_list_objects",
                "List objects with pagination.", new[] { "limit" }),
            new Intent("search find query", "genexus_query",
                "Search the active KB. Use name: type: usedby: parent: prefixes.", new[] { "query" }),
            new Intent("search source code text body", "genexus_search_source",
                "Search inside source bodies of procs/web-events/transactions.", new[] { "pattern" }),
            new Intent("all buildall entire whole knowledge base", "genexus_lifecycle",
                "Run the explicit incremental Build All for the selected KB.", new string[] { }, ("action", "build_all")),
            new Intent("build compile", "genexus_lifecycle",
                "Trigger a build. Long; returns operationId.", new string[] { }, ("action", "build")),
            new Intent("rebuild recompile", "genexus_lifecycle",
                "Trigger a full rebuild.", new string[] { }, ("action", "rebuild")),
            new Intent("index reindex scan", "genexus_lifecycle",
                "Rebuild the KB search index.", new string[] { }, ("action", "index"), ("force", true)),
            new Intent("validate check lint", "genexus_lifecycle",
                "Run KB validation.", new string[] { }, ("action", "validate")),
            new Intent("status job polling progress", "genexus_lifecycle",
                "Poll an async operation. target = operationId or op:<id>.", new[] { "target" }, ("action", "status")),
            new Intent("cancel abort", "genexus_lifecycle",
                "Cancel an in-flight operation.", new[] { "target" }, ("action", "cancel")),
            new Intent("preview render screenshot browser", "genexus_preview",
                "Render a WebPanel in headless browser.", new[] { "name" }),
            new Intent("impact callers callees uses-by", "genexus_analyze",
                "Find what calls / is called by an object.", new[] { "name" }, ("mode", "impact")),
            new Intent("complexity hotspots", "genexus_analyze",
                "Per-KB complexity hotspots.", new string[] { }, ("mode", "summary")),
            new Intent("variable variables", "genexus_variable",
                "Add / remove / modify a variable.", new[] { "name", "action" }),
            new Intent("history snapshot discard revert undo", "genexus_history",
                "Snapshot / restore part bytes.", new[] { "name", "action" }),
            new Intent("kb open close which select", "genexus_kb",
                "Manage the active KB.", new[] { "action" }),
            new Intent("whoami who status orient session welcome", "genexus_whoami",
                "Session context + KB info + suggestedNext. Call FIRST every session.", new string[] { }),
            new Intent("help route how can-i what-tool", "genexus_orient",
                "When in doubt, call orient for the welcome card.", new string[] { }),
        };

        public string RouteGoal(string goal)
        {
            if (string.IsNullOrWhiteSpace(goal))
            {
                return McpResponse.Err(
                    code: "EmptyGoal",
                    message: "RouteGoal requires a non-empty 'goal' string.",
                    hint: "Pass goal=\"<plain-English intent>\" — e.g. \"edit the Source of MyPanel\".",
                    nextSteps: new JArray(
                        McpResponse.NextStep(
                            tool: "genexus_orient",
                            args: new JObject(),
                            why: "Orient returns the welcome card so the LLM can browse tools by surface.")));
            }

            var words = Tokenize(goal);
            var scored = Intents
                .Select(intent => new { intent, score = Score(intent, words) })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(3)
                .ToList();

            if (scored.Count == 0)
            {
                return McpResponse.Ok(
                    code: "NoMatch",
                    result: new JObject
                    {
                        ["goal"] = goal,
                        ["matches"] = new JArray(),
                        ["fallback"] = new JObject
                        {
                            ["tool"] = "genexus_orient",
                            ["args"] = new JObject(),
                            ["why"] = "No keyword in the goal matched a known intent. Orient gives an overview."
                        }
                    });
            }

            var matchesArr = new JArray();
            foreach (var s in scored)
            {
                var args = new JObject();
                foreach (var k in s.intent.RequiredArgs)
                {
                    args[k] = $"<{k}>";
                }
                foreach (var (k, v) in s.intent.DefaultArgs)
                {
                    args[k] = v is bool b ? JToken.FromObject(b) : JToken.FromObject(v.ToString());
                }
                matchesArr.Add(new JObject
                {
                    ["tool"] = s.intent.Tool,
                    ["args"] = args,
                    ["why"] = s.intent.Why,
                    ["confidence"] = Math.Round((double)s.score / Math.Max(1, words.Count), 2)
                });
            }

            return McpResponse.Ok(
                code: "RouteSuggested",
                result: new JObject
                {
                    ["goal"] = goal,
                    ["matches"] = matchesArr,
                    ["note"] = "Top match is the recommended call; refine args before invoking. confidence is a rough keyword-overlap score, not a probability."
                });
        }

        private static int Score(Intent intent, HashSet<string> words)
        {
            int score = 0;
            foreach (var kw in intent.Keywords)
            {
                if (words.Contains(kw)) score += 2;            // exact-word match
                else if (words.Any(w => w.StartsWith(kw, StringComparison.OrdinalIgnoreCase) && w.Length - kw.Length <= 2)) score += 1; // near match
            }
            return score;
        }

        private static HashSet<string> Tokenize(string s)
        {
            return new HashSet<string>(
                s.ToLowerInvariant()
                 .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '!', '?', ';', ':', '-', '_', '/', '\\', '\'' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
        }

        private sealed class Intent
        {
            public string[] Keywords { get; }
            public string Tool { get; }
            public string Why { get; }
            public string[] RequiredArgs { get; }
            public List<(string Key, object Value)> DefaultArgs { get; }

            public Intent(string keywords, string tool, string why, string[] requiredArgs, params (string, object)[] defaults)
            {
                Keywords = keywords.Split(' ');
                Tool = tool;
                Why = why;
                RequiredArgs = requiredArgs ?? new string[0];
                DefaultArgs = defaults?.ToList() ?? new List<(string, object)>();
            }
        }
    }
}
