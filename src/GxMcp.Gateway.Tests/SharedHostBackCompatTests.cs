using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Mutates process environment variables that Configuration.ParseConfig and the Worker read.
    // DisableParallelization makes xUnit run this collection on its own, after the parallel ones,
    // so no other test observes the temporary values and no other test writes the Gateway log
    // while a load is being captured.
    [CollectionDefinition("SharedHostBackCompatEnvironment", DisableParallelization = true)]
    public sealed class SharedHostBackCompatEnvironmentCollection { }

    /// <summary>
    /// Backward-compatibility corpus for RFC #854 (Requirement 10 criteria 1, 3, 6 and 8; Requirement 3
    /// criterion 14). The corpus under Fixtures/BackCompat/&lt;tag&gt;/ holds byte-identical copies of
    /// the Previous_Release configuration documents. Each one must keep loading with exactly the
    /// validation outcome the Previous_Release produced (no error, no new warning) and must resolve
    /// every pre-existing setting to the Previous_Release value. The expected values below were
    /// recorded from the Previous_Release Configuration.cs, which this test is green against unchanged.
    /// </summary>
    [Collection("SharedHostBackCompatEnvironment")]
    public sealed class SharedHostBackCompatTests
    {
        private const string PreviousReleaseTag = "v4.3.0";
        private const string BusyWaitVariable = "GXMCP_BUSY_WAIT_MS";
        private const string BusyRejectVariable = "GXMCP_BUSY_REJECT_MS";

        // The warnings ParseConfig can write to the Gateway log, identified by a stable fragment.
        private const string InvalidValueWarning = "WARNING: invalid config.json value";
        private const string UndeserializableWarning = "WARNING: config.json could not be deserialized";
        private const string MissingKbPathWarning = "WARNING: Environment.KBPath is missing";
        private static readonly string[] ConfigurationWarnings =
        {
            InvalidValueWarning, UndeserializableWarning, MissingKbPathWarning
        };

        // Variables that change what ParseConfig resolves or rejects. They are cleared for every load
        // so the corpus is resolved as the file alone defines it, then restored.
        private static readonly string[] ControlledVariables =
        {
            BusyWaitVariable, BusyRejectVariable, "GX_MCP_PORT", "GX_MCP_STDIO",
            "GXMCP_SHARED_GATEWAY", "GX_MCP_SHARED_GATEWAY", "GXMCP_PROFILE",
            "GXMCP_NO_STRUCTURED_CONTENT", "GXMCP_EMIT_STRUCTURED_CONTENT", "GXMCP_TERSE",
            "GXMCP_ENGINE_BACKEND", "GXMCP_ENGINE_FALLBACK", "GXMCP_ENGINE_EXE", "GXMCP_ENGINE_TIMEOUT_SECONDS"
        };

        private sealed record Baseline(
            string WorkerSharingMode,
            string GatewayMode,
            string ResolutionPolicy,
            int MaxOpenKbs,
            int WorkerIdleTimeoutMinutes,
            int WorkerHeapRecycleMB,
            string[] Warnings);

        // Recorded from the Previous_Release. config.sample.json is a legacy (non-strict) document, so
        // its effective GatewayMode falls back to Server.TransportMode ("legacy"); the neutral sample
        // is strict v2 and has no Environment.KBPath, which the Previous_Release already warned about.
        private static readonly IReadOnlyDictionary<string, Baseline> Baselines = new Dictionary<string, Baseline>(StringComparer.Ordinal)
        {
            ["config.sample.json"] = new Baseline("isolated", "legacy", "strict", 3, 30, 1500, Array.Empty<string>()),
            ["config.neutral.sample.json"] = new Baseline("isolated", "stdio-isolated", "strict", 3, 30, 1500, new[] { MissingKbPathWarning }),
        };

        public static IEnumerable<object[]> CorpusFiles() =>
            Baselines.Keys.OrderBy(name => name, StringComparer.Ordinal).Select(name => new object[] { name });

        // (GXMCP_BUSY_WAIT_MS, GXMCP_BUSY_REJECT_MS, expected wait, expected reject) under the
        // Previous_Release Worker rules: wait takes a positive integer, else 15000; reject takes any
        // integer (0 or negative disables it), else 3000.
        public static IEnumerable<object?[]> BusyEnvironmentCases()
        {
            var cases = new (string? Wait, string? Reject, int ExpectedWait, int ExpectedReject)[]
            {
                (null, null, 15000, 3000),
                ("20000", "5000", 20000, 5000),
                ("not-a-number", "0", 15000, int.MaxValue),
                ("0", "not-a-number", 15000, 3000),
            };
            foreach (var file in Baselines.Keys.OrderBy(name => name, StringComparer.Ordinal))
            {
                foreach (var c in cases)
                    yield return new object?[] { file, c.Wait, c.Reject, c.ExpectedWait, c.ExpectedReject };
            }
        }

        [Fact]
        public void Corpus_HoldsExactlyThePreviousReleaseConfigurationDocuments()
        {
            string directory = CorpusDirectory();
            string[] files = Directory.GetFiles(directory).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            Assert.Equal(Baselines.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray(), files);
            foreach (string file in files)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, file));
                Assert.DoesNotContain((byte)'\r', bytes);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    $"{file} must stay byte-identical to {PreviousReleaseTag}, which has no BOM.");
            }
        }

        [Theory]
        [MemberData(nameof(CorpusFiles))]
        public void Corpus_LoadsWithoutErrorAndWithOnlyThePreviousReleaseWarnings(string file)
        {
            var (_, warnings) = LoadWithControlledEnvironment(file, busyWait: null, busyReject: null);

            Assert.Equal(Baselines[file].Warnings, warnings);
        }

        [Theory]
        [MemberData(nameof(CorpusFiles))]
        public void Corpus_ResolvesPreviousReleaseValues(string file)
        {
            var (config, _) = LoadWithControlledEnvironment(file, busyWait: null, busyReject: null);
            var expected = Baselines[file];
            var server = config.Server ?? new ServerConfig();

            Assert.Equal(expected.WorkerSharingMode, server.WorkerSharingMode);
            // Program.Main derives the effective mode as GatewayMode ?? Server.TransportMode.
            Assert.Equal(expected.GatewayMode, config.GatewayMode ?? server.TransportMode);
            Assert.Equal(expected.ResolutionPolicy, (config.Environment ?? new EnvironmentConfig()).ResolutionPolicy);
            Assert.Equal(expected.MaxOpenKbs, server.MaxOpenKbs);
            Assert.Equal(expected.WorkerIdleTimeoutMinutes, server.WorkerIdleTimeoutMinutes);
            Assert.Equal(expected.WorkerHeapRecycleMB, server.WorkerHeapRecycleMB);
        }

        [Theory]
        [MemberData(nameof(BusyEnvironmentCases))]
        public void Corpus_KeepsBusyVariablesAndTheirPreviousReleaseEffectiveValues(
            string file, string? busyWait, string? busyReject, int expectedWait, int expectedReject)
        {
            // The Gateway does not resolve these two variables itself: it forwards every GXMCP_*
            // variable to the Worker unchanged, and the Worker resolves them. Loading a corpus document
            // must therefore leave both exactly as the user set them.
            var (_, _, waitAfterLoad, rejectAfterLoad) = LoadCapturingBusyVariables(file, busyWait, busyReject);

            Assert.Equal(busyWait, waitAfterLoad);
            Assert.Equal(busyReject, rejectAfterLoad);
            Assert.Equal(expectedWait, PreviousReleaseBusyWaitMs(waitAfterLoad));
            Assert.Equal(expectedReject, PreviousReleaseBusyRejectMs(rejectAfterLoad));
        }

        [Fact]
        public void WorkerBusyResolvers_KeepThePreviousReleaseRules()
        {
            // Pins the Worker-side rules that PreviousReleaseBusyWaitMs/RejectMs mirror, so a change to
            // a default or to the parse rule fails here instead of drifting from the corpus baseline.
            string wait = SourceAssert.MethodBody(RepoSource.Read("src", "GxMcp.Worker", "Services", "StaScheduler.cs"), "public static int ResolveBusyWaitMs(");
            Assert.Contains("Environment.GetEnvironmentVariable(\"GXMCP_BUSY_WAIT_MS\")", wait);
            Assert.Contains("int.TryParse(env, out int envParsed) && envParsed > 0", wait);
            Assert.Contains("return 15000;", wait);

            string reject = SourceAssert.MethodBody(RepoSource.Read("src", "GxMcp.Worker", "Program.cs"), "private static int ResolveBusyRejectThresholdMs(");
            Assert.Contains("Environment.GetEnvironmentVariable(\"GXMCP_BUSY_REJECT_MS\")", reject);
            Assert.Contains("return v <= 0 ? int.MaxValue : v;", reject);
            Assert.Contains("return 3000;", reject);
        }

        private static int PreviousReleaseBusyWaitMs(string? value) =>
            int.TryParse(value, out int parsed) && parsed > 0 ? parsed : 15000;

        private static int PreviousReleaseBusyRejectMs(string? value)
        {
            if (value != null && int.TryParse(value, out int parsed)) return parsed <= 0 ? int.MaxValue : parsed;
            return 3000;
        }

        private static (Configuration Config, string[] Warnings) LoadWithControlledEnvironment(string file, string? busyWait, string? busyReject)
        {
            var (config, warnings, _, _) = LoadCapturingBusyVariables(file, busyWait, busyReject);
            return (config, warnings);
        }

        private static (Configuration Config, string[] Warnings, string? WaitAfterLoad, string? RejectAfterLoad) LoadCapturingBusyVariables(
            string file, string? busyWait, string? busyReject)
        {
            string path = Path.Combine(CorpusDirectory(), file);
            var saved = ControlledVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            try
            {
                foreach (string name in ControlledVariables)
                    Environment.SetEnvironmentVariable(name, null);
                Environment.SetEnvironmentVariable(BusyWaitVariable, busyWait);
                Environment.SetEnvironmentVariable(BusyRejectVariable, busyReject);

                string marker = Guid.NewGuid().ToString("N");
                Program.Log($"[BackCompatTest] begin {marker}");
                Configuration config = ParseConfig(path);
                Program.Log($"[BackCompatTest] end {marker}");

                string[] warnings = ConfigurationWarningsBetween($"[BackCompatTest] begin {marker}", $"[BackCompatTest] end {marker}");
                return (config, warnings, Environment.GetEnvironmentVariable(BusyWaitVariable), Environment.GetEnvironmentVariable(BusyRejectVariable));
            }
            finally
            {
                foreach (var entry in saved)
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        // ParseConfig reports warnings only through Program.Log, which has no in-memory seam, so the
        // captured window of the Gateway log file is read back. Both markers must be present, so a log
        // that could not be written fails the test instead of passing as "no warnings".
        private static string[] ConfigurationWarningsBetween(string beginMarker, string endMarker)
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
                .Select(line => ConfigurationWarnings.FirstOrDefault(w => line.Contains(w, StringComparison.Ordinal)))
                .Where(w => w != null)
                .Select(w => w!)
                .ToArray();
        }

        private static string CorpusDirectory() =>
            Path.GetDirectoryName(RepoSource.PathOf("src", "GxMcp.Gateway.Tests", "Fixtures", "BackCompat", PreviousReleaseTag, "config.neutral.sample.json"))!;

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
