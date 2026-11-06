using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Task 5.7 (Requirements 5.1, 5.3, 6.2, 6.3): the broker receives every resolved Server.SharedHost setting and
    // Server.WorkerHeapRecycleMB as command-line arguments, so it does not depend on inherited environment variables.
    public sealed class SharedWorkerHostLauncherSettingsArgumentsTests
    {
        [Fact]
        public void The_Host_Receives_The_Effective_Admission_Settings_As_Arguments()
        {
            var settings = SharedHostSettingsResolver.Resolve(
                new JObject
                {
                    [SharedHostSettingsResolver.MaxWorkersKey] = 6,
                    [SharedHostSettingsResolver.MemoryFloorMbKey] = 2048,
                    [SharedHostSettingsResolver.ColdStartConcurrencyKey] = 2,
                    [SharedHostSettingsResolver.AttachReadyTimeoutSecondsKey] = 45
                },
                strictDocument: false,
                "stdio-isolated",
                _ => null);
            Assert.True(settings.IsValid, string.Join("; ", settings.Errors));
            var config = new Configuration
            {
                Server = new ServerConfig { WorkerHeapRecycleMB = 900 },
                EffectiveSharedHost = settings
            };

            var arguments = Parse(Build(config));

            Assert.Equal("6", arguments["max-workers"]);
            Assert.Equal("2048", arguments["memory-floor-mb"]);
            Assert.Equal("2", arguments["cold-start-concurrency"]);
            Assert.Equal("45000", arguments["attach-ready-timeout-ms"]);
            Assert.Equal("900", arguments["heap-recycle-mb"]);
        }

        [Fact]
        public void Without_Explicit_Settings_The_Host_Receives_The_Defaults()
        {
            var arguments = Parse(Build(new Configuration()));

            Assert.Equal(Text(SharedHostSettingsResolver.DefaultMaxWorkers), arguments["max-workers"]);
            Assert.Equal(Text(SharedHostSettingsResolver.DefaultMemoryFloorMb), arguments["memory-floor-mb"]);
            Assert.Equal(Text(SharedHostSettingsResolver.DefaultColdStartConcurrency), arguments["cold-start-concurrency"]);
            Assert.Equal(Text(SharedHostSettingsResolver.DefaultAttachReadyTimeoutSeconds * 1000), arguments["attach-ready-timeout-ms"]);
            Assert.Equal(Text(new ServerConfig().WorkerHeapRecycleMB), arguments["heap-recycle-mb"]);
        }

        [Fact]
        public void A_Zero_Heap_Recycle_Setting_Is_Passed_As_Disabled()
        {
            var arguments = Parse(Build(new Configuration { Server = new ServerConfig { WorkerHeapRecycleMB = 0 } }));

            Assert.Equal("0", arguments["heap-recycle-mb"]);
        }

        private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Build(Configuration config) => SharedWorkerHostLauncher.BuildHostArguments(
            config,
            SharedWorkerIdentity.Create(@"C:\Worker\GxMcp.Worker.exe", @"C:\KBs\KbA", @"C:\GeneXus18", "native-sdk", "18"),
            @"C:\Worker\GxMcp.Worker.exe", @"C:\KBs\KbA", @"C:\GeneXus18", "native-sdk", "18");

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
