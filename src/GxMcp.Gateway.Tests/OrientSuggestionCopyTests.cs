using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// v2.7.0 folded <c>genexus_orient</c> as a duplicate of <c>genexus_whoami</c>
    /// and withdrew it from advertisement — but the dispatch path stayed live
    /// (OperationsRouter → Worker OrientService, measured in
    /// docs/benchmarks/2026-09-20-live-rounds.md) and several Gateway/Worker
    /// envelopes kept suggesting it with copy that describes something orient
    /// does NOT do ("shows the tool catalog", "lists each tool's input
    /// schema"). OrientService.Welcome returns {kb, recentEdits, gotchas,
    /// topTools} — a welcome card, not a catalog.
    ///
    /// These guards pin the truthful copy and forbid the bogus
    /// <c>topic=update</c> orient suggestion (Welcome ignores args; the update
    /// status already ships in whoami's own payload).
    /// </summary>
    public class OrientSuggestionCopyTests
    {
        internal const string CanonicalOrientWhy =
            "Welcome card: KB info, recent edits, and top gotchas.";

        private static string FindUp(params string[] segments)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(new[] { dir }.Concat(segments).ToArray());
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("Could not locate " + string.Join("/", segments));
        }

        [Fact]
        public async Task InvalidArgsEnvelope_OrientNextStep_UsesCanonicalWhy()
        {
            GatewayArgsValidator.ClearCache();
            GatewayArgsValidator.PrimeCache("genexus_inspect", MakeInspectSchema());

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "orient-copy-test-1",
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_inspect",
                    ["arguments"] = new JObject { ["include"] = new JArray("metadata") }
                }
            };

            var response = await Program.ProcessMcpRequest(request);
            var text = ((response!["result"] as JObject)!["content"] as JArray)![0]!["text"]!.ToString();
            var payload = JObject.Parse(text);
            var nextSteps = payload["error"] as JObject == null
                ? null
                : (payload["error"] as JObject)!["nextSteps"] as JArray;
            Assert.NotNull(nextSteps);
            // genexus_orient is not published; the Gateway points at genexus_whoami.
            Assert.DoesNotContain(nextSteps!.OfType<JObject>(), s => s["tool"]?.ToString() == "genexus_orient");
            Assert.Contains(nextSteps!.OfType<JObject>(), s => s["tool"]?.ToString() == "genexus_whoami");
        }

        [Theory]
        [InlineData("GxMcp.Worker", "Services", "CommandDispatcher.cs")]
        [InlineData("GxMcp.Worker", "Services", "HelpService.cs")]
        public void OrientAssociatedWhy_NeverClaimsCatalogOrSchema(params string[] segments)
        {
            var path = FindUp(new[] { "src" }.Concat(segments).ToArray());
            string source = File.ReadAllText(path);
            var whys = Regex.Matches(
                    source,
                    "genexus_orient[\\s\\S]{0,600}?(\\[\"why\"\\]\\s*=\\s*|why:\\s*)\"(?<why>(?:[^\"\\\\]|\\\\.)*)\"",
                    RegexOptions.Singleline)
                .Cast<Match>()
                .Select(m => m.Groups["why"].Value)
                .ToArray();
            Assert.NotEmpty(whys);
            Assert.All(whys, why =>
            {
                Assert.DoesNotContain("catalog", why, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("schema", why, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(CanonicalOrientWhy, why);
            });
        }

        [Fact]
        public void Whoami_NeverSuggestsUnadvertisedOrient()
        {
            // The update status already ships in whoami's own payload; the
            // former orient suggestion carried a bogus topic arg Welcome
            // ignores. No orient reference may remain here.
            var path = FindUp("src", "GxMcp.Gateway", "Program.Whoami.cs");
            string source = File.ReadAllText(path);
            Assert.DoesNotContain("genexus_orient", source);
        }

        private static JObject MakeInspectSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["name"] = new JObject { ["type"] = "string" }
                },
                ["required"] = new JArray("name")
            };
        }
    }
}
