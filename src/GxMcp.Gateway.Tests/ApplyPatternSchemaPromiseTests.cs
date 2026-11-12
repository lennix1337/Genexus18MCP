using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// <c>genexus_apply_pattern</c>'s schema must not promise a reapply inference the
    /// route refuses.
    ///
    /// <para>
    /// The defect is a disagreement between two surfaces, not a wrong string in one of
    /// them. The Worker accepts <c>reapply</c> and dispatches it, then refuses the
    /// generic pattern-engine route with a measured reason
    /// (<c>GenericReapplySupported</c> stays <c>false</c>); the published schema told
    /// the caller to omit <c>pattern</c> and let reapply infer it, so a caller following
    /// the schema spent a turn discovering the refusal. These guards read the schema and
    /// the Worker's refusal and require them to say the same thing, rather than pinning
    /// either one's English.
    /// </para>
    ///
    /// <para>
    /// The third guard is the one against over-correcting. Hiding a parameter the
    /// dispatcher still honours would be a different wrong answer: the caller could no
    /// longer name the WorkWithPlus reapply route that <em>is</em> supported.
    /// </para>
    /// </summary>
    public class ApplyPatternSchemaPromiseTests
    {
        private static readonly string[] WorkerServicePath =
            { "src", "GxMcp.Worker", "Services", "PatternApplyService.cs" };

        private static readonly string[] DispatcherPath =
            { "src", "GxMcp.Worker", "Services", "CommandDispatcher.cs" };

        /// <summary>
        /// The published <c>genexus_apply_pattern</c> tool, located the way
        /// <c>ToolSchemaSizeTests</c> locates the file: beside the test output when the
        /// Gateway propagates it, otherwise by walking up to the repository's
        /// <c>src/GxMcp.Gateway</c>.
        /// </summary>
        private static JObject ApplyPatternTool()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            if (File.Exists(beside)) return ToolNamed(beside, "genexus_apply_pattern");

            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                foreach (string relative in new[]
                {
                    Path.Combine("GxMcp.Gateway", "tool_definitions.json"),
                    Path.Combine("src", "GxMcp.Gateway", "tool_definitions.json")
                })
                {
                    string candidate = Path.Combine(dir, relative);
                    if (File.Exists(candidate)) return ToolNamed(candidate, "genexus_apply_pattern");
                }

                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }

            throw new FileNotFoundException(
                "Could not locate tool_definitions.json from " + AppContext.BaseDirectory);
        }

        private static JObject ToolNamed(string path, string name)
        {
            JObject? tool = JArray.Parse(File.ReadAllText(path))
                .OfType<JObject>()
                .FirstOrDefault(t => (string?)t["name"] == name);
            Assert.True(tool != null, name + " is not in " + path);
            return tool!;
        }

        private static JObject Properties()
        {
            JToken? schema =ApplyPatternTool()["inputSchema"];
            Assert.True(schema is JObject, "genexus_apply_pattern has no inputSchema object");
            JToken? properties =((JObject)schema!)["properties"];
            Assert.True(properties is JObject, "genexus_apply_pattern has no properties object");
            return (JObject)properties!;
        }

        private static string Description(string name)
        {
            JToken? property =Properties()[name];
            Assert.True(property != null, $"genexus_apply_pattern no longer declares a '{name}' parameter");
            string? description = (string?)property!["description"];
            Assert.False(string.IsNullOrWhiteSpace(description), $"'{name}' has no description");
            return description!;
        }

        private static string WorkerSource(string[] path)
        {
            // Comments blanked so a guard cannot pass on prose and fail on code.
            return RepoSource.WithoutComments(path);
        }

        /// <summary>
        /// <c>pattern</c> must not instruct the caller to omit it on the reapply route.
        /// </summary>
        /// <remarks>
        /// Two assertions, because either alone leaves a hole. Forbidding the word
        /// "omit" catches the instruction that shipped; requiring that any surviving
        /// mention of reapply also carry a refusal catches the same promise rephrased,
        /// which is the only realistic way this regresses once the sentence is gone.
        /// </remarks>
        [Fact]
        public void TheSchemaDoesNotInstructTheCallerToOmitPatternForReapply()
        {
            string description = Description("pattern");

            Assert.False(Regex.IsMatch(description, @"\bomit\b", RegexOptions.IgnoreCase),
                "the pattern description must not tell the caller to omit it; that inference "
                + "belongs to a reapply route the Worker refuses. Description was: " + description);

            if (Regex.IsMatch(description, "reapply", RegexOptions.IgnoreCase))
            {
                Assert.True(StatesRefusal(description),
                    "the pattern description mentions reapply, so it must also say the route is "
                    + "refused. Description was: " + description);
            }
        }

        /// <summary>
        /// <c>reapply</c> must state the refusal, and must state the reason the Worker
        /// gives rather than one invented for the schema.
        /// </summary>
        /// <remarks>
        /// The refusal and the emitted code are read out of the Worker rather than
        /// hardcoded, so this asserts agreement: if the runtime changes what it emits, the
        /// schema has to change with it instead of going stale in the other direction.
        /// </remarks>
        [Fact]
        public void TheReapplyDescriptionStatesTheRouteIsRefused()
        {
            string description = Description("reapply");
            string worker = WorkerSource(WorkerServicePath);

            // The refusal the schema describes has to exist in the runtime at all: the
            // generic reapply route is gated off, and WorkWithPlus returns before that
            // gate, which is what makes the refusal scoped rather than universal.
            string supported = SourceAssert.MethodBody(worker, "internal static bool IsSupported(");
            Assert.Contains("GenericReapplySupported", supported);
            Assert.Contains("pattern.IsWorkWithPlus", supported);

            // The gate's exact condition, read from the file rather than a member body:
            // it is a one-line expression-bodied member, and MethodBody needs braces.
            Assert.Contains("route == PatternRoute.Reapply && !GenericReapplySupported", worker);

            string rejection = SourceAssert.MethodBody(worker, "internal static string TryBuildRouteUnsupportedRejection(");
            Assert.Contains("PatternRouteUnsupported", rejection);

            // Agreement, in both directions: the schema names the code the Worker emits
            // and the reason the Worker gives, and scopes it to the same pattern set.
            // "regenerat" is the shared stem on purpose - the Worker assembles that
            // sentence across a string concatenation, so a longer phrase matches neither
            // half and the guard would read a disagreement where there is none.
            Assert.Contains("PatternRouteUnsupported", description);
            Assert.Contains("regenerat", supported);
            Assert.Contains("regenerat", description);
            Assert.Contains("WorkWithPlus", description);

            Assert.True(StatesRefusal(description),
                "the reapply description must say the route is refused, because that is the "
                + "answer the caller gets. Description was: " + description);
        }

        /// <summary>
        /// The parameter must stay advertised: the dispatcher honours it, so dropping it
        /// from the schema would hide a route that works.
        /// </summary>
        [Fact]
        public void TheSchemaStillAdvertisesReapplyAsAParameter()
        {
            JToken? reapply =Properties()["reapply"];
            Assert.True(reapply != null,
                "reapply must stay in the schema: CommandDispatcher dispatches it, and removing "
                + "it hides the WorkWithPlus route that is supported");
            Assert.Equal("boolean", (string?)reapply!["type"]);
            Assert.Equal(JTokenType.Boolean, reapply["default"]?.Type);
            Assert.False((bool?)reapply["default"]);

            // Still optional: reapply must never become required, or a first apply would
            // demand a parameter that only the reapply route reads.
            JArray required = (JArray)ApplyPatternTool()["inputSchema"]!["required"]!;
            Assert.DoesNotContain("reapply", required.Select(token => (string?)token));

            // And the dispatcher still routes it, so the schema describes live behaviour
            // rather than a parameter nothing reads.
            string dispatcher = WorkerSource(DispatcherPath);
            Assert.Contains(@"args?[""reapply""]?.ToObject<bool?>()", dispatcher);
            Assert.Contains(
                "if (reapply) return _patternApplyService.Value.ReapplyPattern(target, patKey, patSettings);",
                dispatcher);
        }

        /// <summary>
        /// Whether a description carries a refusal in either spelling. Deliberately not
        /// one phrase: the guard is that the refusal is stated, not how it is worded.
        /// </summary>
        private static bool StatesRefusal(string description) =>
            Regex.IsMatch(description, @"\brefus\w*|\bunsupported\b", RegexOptions.IgnoreCase);
    }
}
