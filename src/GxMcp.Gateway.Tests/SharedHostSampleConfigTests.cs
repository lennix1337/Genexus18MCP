using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Common.SharedHost;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Sample and documentation contract for the RFC #854 New_Settings (Requirement 10 criteria 4 and 10,
    /// decision D10).
    ///
    /// <para><b>Canonical rows.</b> <see cref="CanonicalRows"/> is the single source for what each New_Setting
    /// documents: config key, environment variable, default, allowed range with unit, and the
    /// WorkerSharingMode values where it takes effect. Both documents below must carry exactly these rows,
    /// cell for cell (backticks and surrounding spaces are ignored).</para>
    ///
    /// <para><b>docs/environment_variables.md format.</b> Exactly one Markdown table whose header row is
    /// <c>| Config key | Environment variable | Default | Allowed range | Takes effect in |</c> (the column
    /// names of the design's New_Settings table), followed by its separator row and then one row per
    /// New_Setting, in any order. The table ends at the first line that does not start with '|'. Cells may
    /// wrap values in backticks.</para>
    ///
    /// <para><b>config.shared-host.sample.json format.</b> JSON holds no comments, and the strict v2 parser
    /// rejects unknown members at every level (<c>RejectUnknown</c>), so a loose comment field anywhere in
    /// today's allow-lists would break the load. The chosen form is one reserved member,
    /// <c>Server.SharedHost.$comment</c>: a JSON array of strings, one per New_Setting, each string being
    /// the five canonical cells joined by <c>" | "</c> with no leading or trailing pipe, for example
    /// <c>"Server.SharedHost.QueueDepth | GXMCP_SHARED_QUEUE_DEPTH | 8 | 1-64 requests | shared-host"</c>.
    /// Next to it the section lists each of the seven keys as a JSON integer at its default value.
    /// Reasons: <c>Server.SharedHost</c> is a new object whose allow-list task 2.5 creates, so admitting
    /// <c>$comment</c> there widens no existing allow-list and no Previous_Release document; the resolver
    /// reads only the seven named keys, so <c>$comment</c> never becomes a setting; and the alternative
    /// (units and modes only in the docs) would not meet criterion 4, which requires them in the sample too.
    /// Task 2.5 must therefore allow exactly <c>$comment</c> plus the seven keys inside <c>Server.SharedHost</c>.</para>
    ///
    /// <para><b>Members.</b> The names are the ones SharedHostConfigurationTests.cs (task 2.3) pins:
    /// <c>ServerConfig.SharedHost</c> (<c>SharedHostConfig?</c>) with one <c>int?</c> property per key holding
    /// the file value, and <c>Configuration.EffectiveSharedHost</c> (<see cref="SharedHostSettingsResult"/>).
    /// They are read by reflection so this file compiles on its own before task 2.5 and each test here
    /// fails or passes independently.</para>
    /// </summary>
    [Collection("SharedHostBackCompatEnvironment")]
    public sealed class SharedHostSampleConfigTests
    {
        private const string SampleFile = "config.shared-host.sample.json";
        private const string CommentMember = "$comment";
        private const string SharedHostMode = "shared-host";

        private static readonly string[] DocsHeader =
        {
            "Config key", "Environment variable", "Default", "Allowed range", "Takes effect in"
        };

        private sealed record Row(string Key, string Variable, string Default, string Range, string Mode, int DefaultValue, int Min, int? Max)
        {
            public string ShortKey => Key.Substring(SharedHostSettingsResolver.SectionPath.Length + 1);
            public string[] Cells => new[] { Key, Variable, Default, Range, Mode };
        }

        // Max null: the allowed maximum is the effective queue depth (SessionQueueCap).
        private static readonly Row[] CanonicalRows =
        {
            new Row("Server.SharedHost.QueueWaitMs", "GXMCP_SHARED_QUEUE_WAIT_MS",
                "30000, or GXMCP_BUSY_WAIT_MS when it is an integer from 1000 to 600000", "1000-600000 ms", SharedHostMode,
                SharedHostSettingsResolver.DefaultQueueWaitMs, 1000, 600000),
            new Row("Server.SharedHost.QueueDepth", "GXMCP_SHARED_QUEUE_DEPTH",
                "8", "1-64 requests", SharedHostMode,
                SharedHostSettingsResolver.DefaultQueueDepth, 1, 64),
            new Row("Server.SharedHost.SessionQueueCap", "GXMCP_SHARED_SESSION_QUEUE_CAP",
                "lower of 4 and the effective queue depth", "1 to the effective queue depth, in requests", SharedHostMode,
                Math.Min(SharedHostSettingsResolver.DefaultSessionQueueCapCeiling, SharedHostSettingsResolver.DefaultQueueDepth), 1, null),
            new Row("Server.SharedHost.MaxWorkers", "GXMCP_SHARED_MAX_WORKERS",
                "4", "1-16 Workers", SharedHostMode,
                SharedHostSettingsResolver.DefaultMaxWorkers, 1, 16),
            new Row("Server.SharedHost.MemoryFloorMB", "GXMCP_SHARED_MEMORY_FLOOR_MB",
                "1024", "256-8192 MB", SharedHostMode,
                SharedHostSettingsResolver.DefaultMemoryFloorMb, 256, 8192),
            new Row("Server.SharedHost.ColdStartConcurrency", "GXMCP_SHARED_COLD_START_CONCURRENCY",
                "1", "1-4 concurrent cold starts", SharedHostMode,
                SharedHostSettingsResolver.DefaultColdStartConcurrency, 1, 4),
            new Row("Server.SharedHost.AttachReadyTimeoutSeconds", "GXMCP_SHARED_ATTACH_READY_TIMEOUT_S",
                "120", "10-600 s", SharedHostMode,
                SharedHostSettingsResolver.DefaultAttachReadyTimeoutSeconds, 10, 600),
        };

        // The warnings that mean ParseConfig rejected or dropped a value. The missing-KBPath warning is not
        // one: the sample, like config.neutral.sample.json, is neutral and names no KB.
        private static readonly string[] ValidationWarnings =
        {
            "WARNING: invalid config.json value", "WARNING: config.json could not be deserialized"
        };

        // Cleared for every load so the sample is resolved as the file alone defines it, then restored.
        private static readonly string[] ControlledVariables = new[]
        {
            "GXMCP_BUSY_WAIT_MS", "GXMCP_BUSY_REJECT_MS", "GX_MCP_PORT", "GX_MCP_STDIO",
            "GXMCP_SHARED_GATEWAY", "GX_MCP_SHARED_GATEWAY", "GXMCP_PROFILE",
            "GXMCP_NO_STRUCTURED_CONTENT", "GXMCP_EMIT_STRUCTURED_CONTENT", "GXMCP_TERSE",
            "GXMCP_ENGINE_BACKEND", "GXMCP_ENGINE_FALLBACK", "GXMCP_ENGINE_EXE", "GXMCP_ENGINE_TIMEOUT_SECONDS"
        }.Concat(CanonicalRows.Select(r => r.Variable)).ToArray();

        public static IEnumerable<object[]> RowKeys() => CanonicalRows.Select(r => new object[] { r.Key });

        [Fact]
        public void SharedHostSample_LoadsWithoutValidationErrorInACleanEnvironment()
        {
            var (config, warnings) = LoadSampleInCleanEnvironment();

            Assert.Empty(warnings);
            Assert.Equal(SharedHostMode, (config.Server ?? new ServerConfig()).WorkerSharingMode);
        }

        [Fact]
        public void SharedHostSample_IsAStrictDocumentListingEveryNewSettingAtItsDefault()
        {
            JObject document = ReadSample();
            Assert.Equal(2, document.Value<int?>("ConfigSchemaVersion"));
            Assert.Equal(SharedHostMode, document["Server"]?.Value<string>("WorkerSharingMode"));

            var section = document["Server"]?["SharedHost"] as JObject;
            Assert.True(section != null, $"{SampleFile} must define a Server.SharedHost object.");

            var expectedMembers = CanonicalRows.Select(r => r.ShortKey).Append(CommentMember).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(expectedMembers, section!.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var row in CanonicalRows)
            {
                JToken value = section[row.ShortKey]!;
                Assert.True(value.Type == JTokenType.Integer, $"{row.Key} must be a JSON integer in {SampleFile}.");
                Assert.Equal(row.DefaultValue, value.Value<int>());
            }
        }

        [Fact]
        public void SharedHostSample_SectionResolvesToTheDocumentedDefaults()
        {
            var section = ReadSample()["Server"]?["SharedHost"] as JObject;
            Assert.True(section != null, $"{SampleFile} must define a Server.SharedHost object.");

            SharedHostSettingsResult result = SharedHostSettingsResolver.Resolve(section, strictDocument: true, "stdio-isolated", _ => null);

            Assert.Empty(result.Errors);
            AssertDefaults(result);
            foreach (var row in CanonicalRows)
                Assert.Equal("config:" + row.Key, result.Sources[row.ShortKey]);
        }

        [Fact]
        public void SharedHostSample_BindsServerSharedHostAndTheEffectiveSettingsToTheDocumentedDefaults()
        {
            var (config, _) = LoadSampleInCleanEnvironment();
            var server = config.Server ?? new ServerConfig();

            PropertyInfo? sharedHostProperty = typeof(ServerConfig).GetProperty("SharedHost", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(sharedHostProperty != null, "ServerConfig.SharedHost (SharedHostConfig?) is missing.");
            object? sharedHost = sharedHostProperty!.GetValue(server);
            Assert.True(sharedHost != null, $"ServerConfig.SharedHost must be bound from {SampleFile}.");
            foreach (var row in CanonicalRows)
            {
                PropertyInfo? member = sharedHost!.GetType().GetProperty(row.ShortKey, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.True(member != null, $"SharedHostConfig.{row.ShortKey} is missing.");
                Assert.Equal((object)row.DefaultValue, UnwrapInt(member!.GetValue(sharedHost)));
            }

            PropertyInfo? effectiveProperty = typeof(Configuration).GetProperty("EffectiveSharedHost", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(effectiveProperty != null && effectiveProperty.PropertyType == typeof(SharedHostSettingsResult),
                "Configuration.EffectiveSharedHost (SharedHostSettingsResult) is missing.");
            var effective = effectiveProperty!.GetValue(config) as SharedHostSettingsResult;
            Assert.True(effective != null, $"Configuration.EffectiveSharedHost must be set after loading {SampleFile}.");
            Assert.Empty(effective!.Errors);
            AssertDefaults(effective);
        }

        [Fact]
        public void SharedHostSample_CommentDocumentsEveryNewSetting()
        {
            var section = ReadSample()["Server"]?["SharedHost"] as JObject;
            Assert.True(section != null, $"{SampleFile} must define a Server.SharedHost object.");
            var comment = section![CommentMember] as JArray;
            Assert.True(comment != null, $"Server.SharedHost.{CommentMember} must be a JSON array of strings in {SampleFile}.");

            var rows = comment!.Select(item =>
            {
                Assert.True(item.Type == JTokenType.String, $"Every Server.SharedHost.{CommentMember} entry must be a string.");
                return SplitCells(item.Value<string>()!);
            }).ToList();

            AssertCanonicalRows(rows, $"{SampleFile} Server.SharedHost.{CommentMember}");
        }

        [Fact]
        public void EnvironmentVariablesDoc_TablesEveryNewSetting()
        {
            string[] lines = RepoSource.Read("docs", "environment_variables.md").Replace("\r\n", "\n").Split('\n');

            int[] headers = Enumerable.Range(0, lines.Length)
                .Where(i => lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal)
                    && SplitCells(lines[i]).SequenceEqual(DocsHeader, StringComparer.Ordinal))
                .ToArray();
            Assert.True(headers.Length == 1,
                $"docs/environment_variables.md must hold exactly one table headed '| {string.Join(" | ", DocsHeader)} |'; found {headers.Length}.");

            int separator = headers[0] + 1;
            Assert.True(separator < lines.Length && SplitCells(lines[separator]).All(c => c.Length > 0 && c.Trim('-', ':').Length == 0),
                "The New_Settings table header must be followed by a Markdown separator row.");

            var rows = new List<string[]>();
            for (int i = separator + 1; i < lines.Length && lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal); i++)
                rows.Add(SplitCells(lines[i]));

            AssertCanonicalRows(rows, "docs/environment_variables.md");
        }

        [Theory]
        [MemberData(nameof(RowKeys))]
        public void CanonicalRows_MatchTheResolverVariableAndRange(string key)
        {
            // Ties the documented variable and range to the code: the bounds resolve, one step past them is
            // rejected. Green on the PR 1 resolver; it guards the canonical rows the documents must copy.
            Row row = CanonicalRows.Single(r => r.Key == key);
            int max = row.Max ?? SharedHostSettingsResolver.DefaultQueueDepth;

            Assert.Empty(ResolveWithVariable(row.Variable, row.Min).Errors);
            Assert.Empty(ResolveWithVariable(row.Variable, max).Errors);
            Assert.NotEmpty(ResolveWithVariable(row.Variable, row.Min - 1).Errors);
            Assert.NotEmpty(ResolveWithVariable(row.Variable, max + 1).Errors);
            Assert.Equal("env:" + row.Variable, ResolveWithVariable(row.Variable, row.Min).Sources[row.ShortKey]);
        }

        [Fact]
        public void NeutralSample_StaysByteIdenticalToThePreviousReleaseCorpusCopy()
        {
            byte[] current = File.ReadAllBytes(RepoSource.PathOf("config.neutral.sample.json"));
            byte[] previous = File.ReadAllBytes(RepoSource.PathOf("src", "GxMcp.Gateway.Tests", "Fixtures", "BackCompat", "v4.3.0", "config.neutral.sample.json"));

            Assert.Equal(previous, current);
        }

        private static SharedHostSettingsResult ResolveWithVariable(string variable, int value) =>
            SharedHostSettingsResolver.Resolve(null, strictDocument: true, "stdio-isolated",
                name => name == variable ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);

        private static void AssertDefaults(SharedHostSettingsResult result)
        {
            int Expected(string shortKey) => CanonicalRows.Single(r => r.ShortKey == shortKey).DefaultValue;
            Assert.Equal(Expected(SharedHostSettingsResolver.QueueWaitMsKey), result.QueueWaitMs);
            Assert.Equal(Expected(SharedHostSettingsResolver.QueueDepthKey), result.QueueDepth);
            Assert.Equal(Expected(SharedHostSettingsResolver.SessionQueueCapKey), result.SessionQueueCap);
            Assert.Equal(Expected(SharedHostSettingsResolver.MaxWorkersKey), result.MaxWorkers);
            Assert.Equal(Expected(SharedHostSettingsResolver.MemoryFloorMbKey), result.MemoryFloorMb);
            Assert.Equal(Expected(SharedHostSettingsResolver.ColdStartConcurrencyKey), result.ColdStartConcurrency);
            Assert.Equal(Expected(SharedHostSettingsResolver.AttachReadyTimeoutSecondsKey), result.AttachReadyTimeoutSeconds);
        }

        private static void AssertCanonicalRows(IReadOnlyList<string[]> rows, string where)
        {
            var keys = rows.Select(r => r.Length > 0 ? r[0] : string.Empty).ToList();
            Assert.True(keys.Distinct(StringComparer.Ordinal).Count() == keys.Count, $"{where} lists a New_Setting more than once.");

            var expected = CanonicalRows.Select(r => string.Join(" | ", r.Cells)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var actual = rows.Select(r => string.Join(" | ", r)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, actual);
        }

        // Splits "| a | b |" or "a | b" into trimmed cells with backticks removed.
        private static string[] SplitCells(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(0, trimmed.Length - 1);
            return trimmed.Split('|').Select(c => c.Replace("`", string.Empty).Trim()).ToArray();
        }

        private static object? UnwrapInt(object? value) => value is int i ? i : value;

        private static JObject ReadSample() => JObject.Parse(RepoSource.Read(SampleFile));

        private static (Configuration Config, string[] Warnings) LoadSampleInCleanEnvironment()
        {
            string path = RepoSource.PathOf(SampleFile);
            var saved = ControlledVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            try
            {
                foreach (string name in ControlledVariables)
                    Environment.SetEnvironmentVariable(name, null);

                string marker = Guid.NewGuid().ToString("N");
                Program.Log($"[SampleConfigTest] begin {marker}");
                Configuration config = ParseConfig(path);
                Program.Log($"[SampleConfigTest] end {marker}");

                return (config, ValidationWarningsBetween($"[SampleConfigTest] begin {marker}", $"[SampleConfigTest] end {marker}"));
            }
            finally
            {
                foreach (var entry in saved)
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        // Same seam as SharedHostBackCompatTests: ParseConfig reports warnings only through Program.Log, so the
        // marked window of the Gateway log is read back, and missing markers fail instead of reading as "none".
        private static string[] ValidationWarningsBetween(string beginMarker, string endMarker)
        {
            var field = typeof(Program).GetField("_logPath", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            string logPath = (string)field!.GetValue(null)!;

            string text;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();

            int begin = text.LastIndexOf(beginMarker, StringComparison.Ordinal);
            int end = text.LastIndexOf(endMarker, StringComparison.Ordinal);
            Assert.True(begin >= 0 && end > begin, $"The Gateway log '{logPath}' did not record the capture markers.");

            return text.Substring(begin, end - begin)
                .Split('\n')
                .Where(line => ValidationWarnings.Any(w => line.Contains(w, StringComparison.Ordinal)))
                .Select(line => line.Trim())
                .ToArray();
        }

        private static Configuration ParseConfig(string path)
        {
            var method = typeof(Configuration).GetMethod("ParseConfig", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            try
            {
                var config = method!.Invoke(null, new object[] { path }) as Configuration;
                Assert.NotNull(config);
                return config!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
