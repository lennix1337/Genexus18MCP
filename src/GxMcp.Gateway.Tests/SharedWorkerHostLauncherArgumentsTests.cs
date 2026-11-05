using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // RFC #854: the broker receives the resolved queue settings as command-line arguments,
    // not through inherited environment variables. The broker side parses them in
    // SharedWorkerHostOptions.Parse (GxMcp.Worker).
    public sealed class SharedWorkerHostLauncherArgumentsTests
    {
        private static SharedWorkerIdentity Identity() => SharedWorkerIdentity.Create(
            @"C:\Worker\GxMcp.Worker.exe",
            @"C:\KBs\KbA",
            @"C:\GeneXus18",
            "native-sdk",
            "18");

        [Fact]
        public void The_Host_Receives_The_Effective_Queue_Settings_As_Arguments()
        {
            var settings = SharedHostSettingsResolver.Resolve(
                new JObject { ["QueueWaitMs"] = 45000, ["QueueDepth"] = 12, ["SessionQueueCap"] = 3 },
                strictDocument: false,
                "stdio-isolated",
                _ => null);
            Assert.True(settings.IsValid, string.Join("; ", settings.Errors));
            var config = new Configuration { EffectiveSharedHost = settings };

            var arguments = Parse(Build(config));

            Assert.Equal("45000", arguments["queue-wait-ms"]);
            Assert.Equal("12", arguments["queue-depth"]);
            Assert.Equal("3", arguments["session-queue-cap"]);
        }

        [Fact]
        public void Without_A_SharedHost_Section_The_Host_Receives_The_Defaults()
        {
            var arguments = Parse(Build(new Configuration()));

            Assert.Equal(SharedHostSettingsResolver.DefaultQueueWaitMs.ToString(), arguments["queue-wait-ms"]);
            Assert.Equal(SharedHostSettingsResolver.DefaultQueueDepth.ToString(), arguments["queue-depth"]);
            Assert.Equal(SharedHostSettingsResolver.DefaultSessionQueueCapCeiling.ToString(), arguments["session-queue-cap"]);
        }

        [Fact]
        public void The_Existing_Host_Arguments_Are_Unchanged()
        {
            var identity = Identity();
            var arguments = Parse(Build(new Configuration()));

            Assert.True(arguments.ContainsKey("shared-host"));
            Assert.Equal(identity.Key, arguments["identity-key"]);
            // Registry v2 (decision A4, task 4.4): the host accepts only the "GxMcpShared2_" pipe prefix
            // (SharedWorkerHostOwnershipTests, task 4.8), so the launcher passes PipeNameV2.
            Assert.Equal(SharedWorkerRegistry.PipeNameV2(identity), arguments["pipe-name"]);
            Assert.Equal(@"C:\Worker\GxMcp.Worker.exe", arguments["worker-executable"]);
            Assert.Equal(@"C:\KBs\KbA", arguments["kb"]);
            Assert.Equal(@"C:\GeneXus18", arguments["installation"]);
            Assert.Equal("native-sdk", arguments["driver"]);
            Assert.Equal("18", arguments["major"]);
            Assert.Equal(SharedWorkerHostLauncher.ResolveIdleTimeoutMs(new Configuration()).ToString(), arguments["idle-ms"]);
        }

        private static string Build(Configuration config) => SharedWorkerHostLauncher.BuildHostArguments(
            config, Identity(), @"C:\Worker\GxMcp.Worker.exe", @"C:\KBs\KbA", @"C:\GeneXus18", "native-sdk", "18");

        // "--name value" pairs, values optionally quoted; a flag without a value maps to "".
        private static Dictionary<string, string> Parse(string commandLine)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var tokens = new List<string>();
            foreach (Match match in Regex.Matches(commandLine, "\"([^\"]*)\"|(\\S+)"))
                tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!tokens[i].StartsWith("--", StringComparison.Ordinal)) continue;
                string name = tokens[i].Substring(2);
                bool hasValue = i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal);
                Assert.False(result.ContainsKey(name), "duplicate argument --" + name);
                result[name] = hasValue ? tokens[++i] : string.Empty;
            }
            return result;
        }
    }
}
