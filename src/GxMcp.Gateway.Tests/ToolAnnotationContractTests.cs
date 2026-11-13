using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Tool annotations are per tool but tools multiplex actions, so they must be the
    /// conservative union of what OperationClassifier says about every action. A
    /// readOnlyHint on a tool with a mutating action (or destructiveHint=false on it)
    /// lets MCP clients auto-approve a write.
    /// </summary>
    public sealed class ToolAnnotationContractTests
    {
        // Mode/flag-dependent tools: the classifier decides from arguments, not from an action.
        private static readonly JObject[] ModeProbes =
        {
            new JObject(),
            new JObject { ["mode"] = "linter", ["fix"] = true },
            new JObject { ["dryRun"] = false },
        };

        private static bool CanMutate(string tool)
        {
            var mutatingAction = ToolIdentity.ActionsFor(tool).Any(action =>
                OperationClassifier.ClassifyAction(tool, action) == OperationClassifier.OperationKind.Mutating);
            return mutatingAction || ModeProbes.Any(probe =>
                OperationClassifier.ClassifyTool(tool, (JObject)probe.DeepClone())
                    == OperationClassifier.OperationKind.Mutating);
        }

        private static IEnumerable<(string Name, JObject Annotations)> PublishedAnnotations()
        {
            var tools = JArray.Parse(RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
            foreach (var tool in tools.OfType<JObject>())
                yield return (tool.Value<string>("name")!, (JObject?)tool["annotations"] ?? new JObject());
        }

        // Mutating tools whose every mutating action only adds new state or touches files,
        // caches or processes outside the KB, so destructiveHint stays false. When unsure
        // whether an action can overwrite or delete existing data, the tool is NOT listed.
        private static readonly Dictionary<string, string> MutatingButAdditive = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["genexus_recipe"] = "crystallize only appends a new recipe/macro entry.",
            ["genexus_test"] = "runs tests and reports; it does not edit KB objects.",
            ["genexus_sdk_probe"] = "writes a diagnostic dump file, no KB state.",
            ["genexus_connection_recover"] = "worker/connection recovery and journal fences, no KB object writes.",
            ["genexus_run_object"] = "executes an object or resolves its URL; no KB definition changes.",
            ["genexus_doc"] = "wiki/visualize write generated files outside the KB.",
            ["genexus_navigation"] = "view refreshes the per-KB navigation cache only.",
            ["genexus_telemetry"] = "friction_append only appends to the friction log.",
            ["genexus_worker_pool"] = "warm_spares starts spare Worker processes.",
        };

        [Fact]
        public void ToolAnnotationsDoNotContradictOperationClassifier()
        {
            var violations = new List<string>();
            foreach (var (name, annotations) in PublishedAnnotations())
            {
                if (!CanMutate(name)) continue;
                if (annotations.Value<bool?>("readOnlyHint") != false)
                    violations.Add(name + ": readOnlyHint must be false (a classified action mutates)");
                if (!MutatingButAdditive.ContainsKey(name) && annotations.Value<bool?>("destructiveHint") != true)
                    violations.Add(name + ": destructiveHint must be true (mutating, not listed in MutatingButAdditive)");
            }

            Assert.True(violations.Count == 0,
                "Annotations contradict OperationClassifier:\n" + string.Join("\n", violations));
        }

        [Fact]
        public void MutatingButAdditiveAllowlistHasNoStaleEntries()
        {
            var published = PublishedAnnotations().ToDictionary(t => t.Name, t => t.Annotations, StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var (name, reason) in MutatingButAdditive)
            {
                if (string.IsNullOrWhiteSpace(reason)) problems.Add(name + ": missing reason");
                if (!published.TryGetValue(name, out var annotations)) { problems.Add(name + ": not published"); continue; }
                if (!CanMutate(name)) problems.Add(name + ": not mutating per OperationClassifier");
                if (annotations.Value<bool?>("destructiveHint") != false)
                    problems.Add(name + ": allowlisted but destructiveHint is not false");
            }

            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void ReadOnlyHintImpliesNoMutatingAction()
        {
            var violations = PublishedAnnotations()
                .Where(t => t.Annotations.Value<bool?>("readOnlyHint") == true && CanMutate(t.Name))
                .Select(t => t.Name)
                .ToArray();

            Assert.True(violations.Length == 0,
                "readOnlyHint=true on tools with mutating actions: " + string.Join(", ", violations));
        }
    }
}
