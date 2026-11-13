using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // GXMCP_PROFILE is process-wide, so the end-to-end tests run alone.
    [CollectionDefinition("AdaptiveProfileEnvironment", DisableParallelization = true)]
    public sealed class AdaptiveProfileEnvironmentDefinition { }

    [Collection("AdaptiveProfileEnvironment")]
    public class AdaptiveToolProfileTests
    {
        private static JArray Tools()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            return JArray.Parse(File.Exists(beside)
                ? File.ReadAllText(beside)
                : RepoSource.Read("src", "GxMcp.Gateway", "tool_definitions.json"));
        }

        private static string[] Names(JArray tools) => tools.Select(t => t["name"]!.ToString()).ToArray();

        private static async Task<JObject?> Rpc(string method, string session, JObject? parameters = null, bool sessionContext = true)
        {
            var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method };
            if (parameters != null) request["params"] = parameters;
            return await Program.ProcessMcpRequest(request, session, sessionContext);
        }

        private static async Task<T> WithAdaptiveProfile<T>(Func<Task<T>> body)
        {
            string? previous = Environment.GetEnvironmentVariable("GXMCP_PROFILE");
            Environment.SetEnvironmentVariable("GXMCP_PROFILE", "adaptive");
            try { return await body(); }
            finally { Environment.SetEnvironmentVariable("GXMCP_PROFILE", previous); }
        }

        private static string[] ListedNames(JObject? response) =>
            ((JArray)response!["result"]!["tools"]!).Select(t => t["name"]!.ToString()).ToArray();

        [Fact]
        public void Groups_PartitionEveryPublishedNonCoreTool()
        {
            var all = Names(Tools());
            var core = Names(ToolProfileFilter.Filter(Tools(), "core"));
            var grouped = AdaptiveTools.Groups.SelectMany(g => g.Tools).ToList();

            Assert.Equal(grouped.Count, grouped.Distinct().Count());
            Assert.Empty(grouped.Intersect(core));
            Assert.Empty(grouped.Except(all));
            Assert.Empty(all.Except(core).Except(grouped));
        }

        [Fact]
        public void MetaTool_StaysTiny()
        {
            string json = AdaptiveTools.BuildMetaToolDefinition().ToString(Formatting.None);
            Assert.True(json.Length < 400, $"genexus_tools schema is {json.Length} chars");
        }

        [Fact]
        public void AdaptiveProfile_NeverRefusesAPublishedTool()
        {
            Assert.Null(ToolProfileFilter.GetToolNotInProfileError("adaptive", "genexus_wwp"));
            Assert.NotNull(ToolProfileFilter.GetToolNotInProfileError("core", "genexus_wwp"));
        }

        [Fact]
        public void StaticAdaptiveFilter_IsExactlyCore()
        {
            Assert.Equal(Names(ToolProfileFilter.Filter(Tools(), "core")), Names(ToolProfileFilter.Filter(Tools(), "adaptive")));
        }

        [Fact]
        public async Task ToolsList_DefaultAndOtherProfiles_DoNotContainTheMetaTool()
        {
            var response = await Rpc("tools/list", "adaptive-default-" + Guid.NewGuid().ToString("N"));
            Assert.DoesNotContain(AdaptiveTools.MetaToolName, ListedNames(response));
        }

        [Fact]
        public async Task ToolsList_Adaptive_IsCorePlusMetaTool_ThenGrowsPerSession()
        {
            await WithAdaptiveProfile(async () =>
            {
                string a = "adaptive-a-" + Guid.NewGuid().ToString("N");
                string b = "adaptive-b-" + Guid.NewGuid().ToString("N");
                var core = Names(ToolProfileFilter.Filter(Tools(), "core")).OrderBy(n => n, StringComparer.Ordinal);

                var initial = ListedNames(await Rpc("tools/list", a));
                Assert.Equal(core.Append(AdaptiveTools.MetaToolName), initial);

                var notifications = new List<(string Session, string Json)>();
                Program.ToolsListChangedObserver = (s, j) => { lock (notifications) notifications.Add((s, j)); };
                try
                {
                    var enable = await Rpc("tools/call", a, JObject.Parse(
                        """{"name":"genexus_tools","arguments":{"action":"enable","group":"data"}}"""));
                    string text = enable!["result"]!["content"]![0]!["text"]!.ToString();
                    Assert.Contains("tools/list again", text);
                    Assert.DoesNotContain("\"isError\":true", enable.ToString(Formatting.None));

                    // Only session A changed.
                    var listedA = ListedNames(await Rpc("tools/list", a));
                    Assert.Contains("genexus_db", listedA);
                    Assert.Contains("genexus_navigation", listedA);
                    Assert.DoesNotContain("genexus_wwp", listedA);
                    Assert.Equal(initial, ListedNames(await Rpc("tools/list", b)));

                    lock (notifications)
                    {
                        var only = Assert.Single(notifications);
                        Assert.Equal(a, only.Session);
                        Assert.Contains("notifications/tools/list_changed", only.Json);
                    }

                    // Enabling again is idempotent and silent.
                    await Rpc("tools/call", a, JObject.Parse(
                        """{"name":"genexus_tools","arguments":{"action":"enable","group":"data"}}"""));
                    lock (notifications) Assert.Single(notifications);

                    // A new initialize starts from scratch.
                    await Rpc("initialize", a, JObject.Parse(
                        """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}"""));
                    Assert.Equal(initial, ListedNames(await Rpc("tools/list", a)));
                }
                finally
                {
                    Program.ToolsListChangedObserver = null;
                    AdaptiveToolSessions.Forget(a);
                    AdaptiveToolSessions.Forget(b);
                }
                return 0;
            });
        }

        [Fact]
        public async Task Sessionless_KeepsNoStateAndSaysSo()
        {
            await WithAdaptiveProfile(async () =>
            {
                string id = "adaptive-modern-" + Guid.NewGuid().ToString("N");
                var response = await Rpc("tools/call", id, JObject.Parse(
                    """{"name":"genexus_tools","arguments":{"action":"enable","group":"data"}}"""), sessionContext: false);
                Assert.Contains("SessionRequired", response!.ToString(Formatting.None));
                Assert.Empty(AdaptiveToolSessions.EnabledGroups(id));
                Assert.DoesNotContain("genexus_db", ListedNames(await Rpc("tools/list", id, sessionContext: false)));
                return 0;
            });
        }

        [Fact]
        public async Task MetaTool_ListAndErrors()
        {
            await WithAdaptiveProfile(async () =>
            {
                string id = "adaptive-list-" + Guid.NewGuid().ToString("N");
                var list = await Rpc("tools/call", id, JObject.Parse(
                    """{"name":"genexus_tools","arguments":{"action":"list"}}"""));
                string text = list!["result"]!["content"]![0]!["text"]!.ToString();
                foreach (var g in AdaptiveTools.Groups) Assert.Contains(g.Name, text);

                var unknown = await Rpc("tools/call", id, JObject.Parse(
                    """{"name":"genexus_tools","arguments":{"action":"enable","group":"nope"}}"""));
                Assert.Contains("UnknownGroup", unknown!.ToString(Formatting.None));
                return 0;
            });
        }

        [Fact]
        public void NonAdaptiveProfile_MetaToolIsRefused()
        {
            var payload = Program.HandleAdaptiveMetaTool(JObject.Parse("""{"action":"list"}"""), "s", true, adaptiveProfile: false);
            Assert.Equal("ToolNotInProfile", payload["error"]!["code"]!.ToString());
        }

        [Fact]
        public void AutoEnable_EnablesTheGroupOnceAndOnlyForThatSession()
        {
            string a = "auto-a-" + Guid.NewGuid().ToString("N");
            string b = "auto-b-" + Guid.NewGuid().ToString("N");
            var seen = new List<string>();
            Program.ToolsListChangedObserver = (s, _) => { lock (seen) seen.Add(s); };
            try
            {
                Assert.False(Program.AutoEnableAdaptiveGroup(a, true, "genexus_read"));   // core: nothing to enable
                Assert.False(Program.AutoEnableAdaptiveGroup(a, true, "genexus_unknown"));
                Assert.True(Program.AutoEnableAdaptiveGroup(a, true, "genexus_wwp"));
                Assert.False(Program.AutoEnableAdaptiveGroup(a, true, "genexus_layout")); // same group, already on
                Assert.False(Program.AutoEnableAdaptiveGroup(b, false, "genexus_wwp"));   // sessionless
                Assert.Equal(new[] { "ui" }, AdaptiveToolSessions.EnabledGroups(a));
                Assert.Empty(AdaptiveToolSessions.EnabledGroups(b));
                lock (seen) Assert.Equal(new[] { a }, seen);
            }
            finally
            {
                Program.ToolsListChangedObserver = null;
                AdaptiveToolSessions.Forget(a);
            }
        }
    }
}
