using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Configuration wiring of the Server.SharedHost New_Settings (RFC #854; Requirement 2 criterion 9,
    /// Requirement 3 criterion 12, Requirement 5 criterion 8, Requirement 6 criterion 11, Requirement 10
    /// criteria 1, 2, 5, 7 and 9).
    ///
    /// Contract these tests pin for Configuration.cs (task 2.5 implements exactly these names):
    /// - <c>ServerConfig.SharedHost</c>: public, type <c>SharedHostConfig?</c>, JSON key "SharedHost". It is
    ///   null when the document has no Server.SharedHost object.
    /// - <c>SharedHostConfig</c>: public class in namespace GxMcp.Gateway with one <c>int?</c> property per
    ///   New_Setting, named exactly as its key: QueueWaitMs, QueueDepth, SessionQueueCap, MaxWorkers,
    ///   MemoryFloorMB, ColdStartConcurrency, AttachReadyTimeoutSeconds. A property holds the file value,
    ///   not the effective one.
    /// - <c>Configuration.EffectiveSharedHost</c>: internal, not serialized, type
    ///   <c>SharedHostSettingsResult</c>. ParseConfig sets it for every document it returns (strict and
    ///   legacy, any WorkerSharingMode) from <c>SharedHostSettingsResolver.Resolve</c> run on the raw JSON
    ///   Server.SharedHost object, so it is never null after a successful load and is always valid.
    /// - Load errors: ParseConfig throws <c>InvalidDataException</c> whose message contains every resolver
    ///   error. Program loads the configuration before it creates the Worker pool or any Worker_Host, so a
    ///   throw from ParseConfig is a rejection before any start.
    /// - Isolated_Mode with an allowed New_Setting writes exactly one Gateway log entry that contains the
    ///   literal "not in effect" and, for each such setting, its config name (Server.SharedHost.&lt;Key&gt;)
    ///   when it came from the file or its environment variable name when it came from the environment.
    ///   Shared_Host_Mode writes no such entry.
    /// </summary>
    [Collection("SharedHostBackCompatEnvironment")]
    public sealed class SharedHostConfigurationTests : IDisposable
    {
        private const string NotInEffect = "not in effect";

        // Every New_Setting: key, environment variable, and its allowed range at the default queue depth.
        private static readonly (string Key, string Env, string Range)[] NewSettings =
        {
            ("QueueWaitMs", "GXMCP_SHARED_QUEUE_WAIT_MS", "1000-600000"),
            ("QueueDepth", "GXMCP_SHARED_QUEUE_DEPTH", "1-64"),
            ("SessionQueueCap", "GXMCP_SHARED_SESSION_QUEUE_CAP", "1-8"),
            ("MaxWorkers", "GXMCP_SHARED_MAX_WORKERS", "1-16"),
            ("MemoryFloorMB", "GXMCP_SHARED_MEMORY_FLOOR_MB", "256-8192"),
            ("ColdStartConcurrency", "GXMCP_SHARED_COLD_START_CONCURRENCY", "1-4"),
            ("AttachReadyTimeoutSeconds", "GXMCP_SHARED_ATTACH_READY_TIMEOUT_S", "10-600"),
        };

        // Variables that change what ParseConfig resolves or rejects. They are cleared before each load so
        // a document is resolved from the file plus the variables a test sets, then restored.
        private static readonly string[] ControlledVariables = new[]
        {
            "GXMCP_BUSY_WAIT_MS", "GXMCP_BUSY_REJECT_MS", "GX_MCP_PORT", "GX_MCP_STDIO",
            "GXMCP_SHARED_GATEWAY", "GX_MCP_SHARED_GATEWAY", "GXMCP_PROFILE",
            "GXMCP_NO_STRUCTURED_CONTENT", "GXMCP_EMIT_STRUCTURED_CONTENT", "GXMCP_TERSE",
            "GXMCP_ENGINE_BACKEND", "GXMCP_ENGINE_FALLBACK", "GXMCP_ENGINE_EXE", "GXMCP_ENGINE_TIMEOUT_SECONDS"
        }.Concat(NewSettings.Select(s => s.Env)).ToArray();

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gxmcp-sharedhost-config-" + Guid.NewGuid().ToString("N"));

        public SharedHostConfigurationTests()
        {
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public static IEnumerable<object[]> DocumentKinds() => new[] { new object[] { true }, new object[] { false } };

        // (strict, key, source "file"/"env", raw value, rejected text, range). File values are JSON tokens.
        public static IEnumerable<object[]> InvalidValueCases()
        {
            var bad = new Dictionary<string, (string OutOfRange, string File, string Env)>(StringComparer.Ordinal)
            {
                ["QueueWaitMs"] = ("999", "600001", "abc"),
                ["QueueDepth"] = ("0", "65", "8.5"),
                ["SessionQueueCap"] = ("9", "0", "x"),
                ["MaxWorkers"] = ("17", "0", "four"),
                ["MemoryFloorMB"] = ("255", "8193", "1e3"),
                ["ColdStartConcurrency"] = ("5", "0", "one"),
                ["AttachReadyTimeoutSeconds"] = ("9", "601", "120s"),
            };
            foreach (bool strict in new[] { true, false })
            {
                foreach (var setting in NewSettings)
                {
                    var values = bad[setting.Key];
                    yield return new object[] { strict, setting.Key, "file", values.OutOfRange, values.OutOfRange, setting.Range };
                    yield return new object[] { strict, setting.Key, "file", values.File, values.File, setting.Range };
                    yield return new object[] { strict, setting.Key, "file", "\"abc\"", "abc", setting.Range };
                    yield return new object[] { strict, setting.Key, "file", "1.5", "1.5", setting.Range };
                    yield return new object[] { strict, setting.Key, "env", values.OutOfRange, values.OutOfRange, setting.Range };
                    yield return new object[] { strict, setting.Key, "env", values.Env, values.Env, setting.Range };
                }
            }
        }

        [Theory]
        [MemberData(nameof(DocumentKinds))]
        public void SharedHost_WithNoNewSetting_AppliesEveryDefault(bool strict)
        {
            var (config, log) = Load(Document(strict, "shared-host", sharedHost: null));

            Assert.Equal("shared-host", config.Server!.WorkerSharingMode);
            Assert.Null(config.Server.SharedHost);
            SharedHostSettingsResult effective = config.EffectiveSharedHost;
            Assert.NotNull(effective);
            Assert.True(effective.IsValid, string.Join(" | ", effective.Errors));
            Assert.Equal(30000, effective.QueueWaitMs);
            Assert.Equal(8, effective.QueueDepth);
            Assert.Equal(4, effective.SessionQueueCap);
            Assert.Equal(4, effective.MaxWorkers);
            Assert.Equal(1024, effective.MemoryFloorMb);
            Assert.Equal(1, effective.ColdStartConcurrency);
            Assert.Equal(120, effective.AttachReadyTimeoutSeconds);
            foreach (var setting in NewSettings)
                Assert.Equal("default", effective.Sources[setting.Key]);
            Assert.DoesNotContain(log, line => line.Contains(NotInEffect, StringComparison.Ordinal));
        }

        [Theory]
        [MemberData(nameof(DocumentKinds))]
        public void IsolatedMode_WithAllowedNewSettings_LoadsAndLogsOneNotInEffectEntry(bool strict)
        {
            var sharedHost = new JObject { ["MaxWorkers"] = 2 };
            var (config, log) = Load(Document(strict, "isolated", sharedHost),
                ("GXMCP_SHARED_QUEUE_DEPTH", "16"));

            Assert.Equal("isolated", config.Server!.WorkerSharingMode);
            Assert.NotNull(config.Server.SharedHost);
            Assert.Equal(2, config.Server.SharedHost!.MaxWorkers);
            Assert.True(config.EffectiveSharedHost.IsValid, string.Join(" | ", config.EffectiveSharedHost.Errors));

            string[] entries = log.Where(line => line.Contains(NotInEffect, StringComparison.Ordinal)).ToArray();
            Assert.Single(entries);
            Assert.Contains("Server.SharedHost.MaxWorkers", entries[0], StringComparison.Ordinal);
            Assert.Contains("GXMCP_SHARED_QUEUE_DEPTH", entries[0], StringComparison.Ordinal);
            Assert.Contains("isolated", entries[0], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void IsolatedMode_WithNoNewSetting_LogsNoNotInEffectEntry()
        {
            var (_, log) = Load(Document(strict: true, "isolated", sharedHost: null));

            Assert.DoesNotContain(log, line => line.Contains(NotInEffect, StringComparison.Ordinal));
        }

        [Fact]
        public void StrictAllowList_AcceptsSharedHostWithEveryNewSetting()
        {
            var sharedHost = new JObject
            {
                ["QueueWaitMs"] = 45000,
                ["QueueDepth"] = 16,
                ["SessionQueueCap"] = 6,
                ["MaxWorkers"] = 3,
                ["MemoryFloorMB"] = 2048,
                ["ColdStartConcurrency"] = 2,
                ["AttachReadyTimeoutSeconds"] = 300,
            };
            var (config, _) = Load(Document(strict: true, "shared-host", sharedHost));

            SharedHostConfig file = config.Server!.SharedHost!;
            Assert.Equal(45000, file.QueueWaitMs);
            Assert.Equal(16, file.QueueDepth);
            Assert.Equal(6, file.SessionQueueCap);
            Assert.Equal(3, file.MaxWorkers);
            Assert.Equal(2048, file.MemoryFloorMB);
            Assert.Equal(2, file.ColdStartConcurrency);
            Assert.Equal(300, file.AttachReadyTimeoutSeconds);

            SharedHostSettingsResult effective = config.EffectiveSharedHost;
            Assert.True(effective.IsValid, string.Join(" | ", effective.Errors));
            Assert.Equal(16, effective.QueueDepth);
            Assert.Equal(300, effective.AttachReadyTimeoutSeconds);
            Assert.Equal("config:Server.SharedHost.AttachReadyTimeoutSeconds", effective.Sources["AttachReadyTimeoutSeconds"]);
        }

        [Fact]
        public void StrictAllowList_RejectsUnknownSharedHostKey()
        {
            var sharedHost = new JObject { ["QueueDepth"] = 8, ["QueueDepthh"] = 8 };

            var ex = Assert.Throws<InvalidDataException>(() => Load(Document(strict: true, "shared-host", sharedHost)));

            Assert.Contains("Server.SharedHost.QueueDepthh", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HttpShared_WithExplicitFileSettings_IsRejectedNamingGatewayModeAndEachSetting()
        {
            var sharedHost = new JObject { ["QueueDepth"] = 8, ["MaxWorkers"] = 4 };

            var ex = Assert.Throws<InvalidDataException>(() => Load(HttpSharedDocument(sharedHost)));

            Assert.Contains("GatewayMode", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Server.SharedHost.QueueDepth", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Server.SharedHost.MaxWorkers", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HttpShared_WithExplicitEnvironmentSetting_IsRejectedNamingGatewayModeAndTheVariable()
        {
            var ex = Assert.Throws<InvalidDataException>(() => Load(HttpSharedDocument(sharedHost: null),
                ("GXMCP_SHARED_MEMORY_FLOOR_MB", "1024")));

            Assert.Contains("GatewayMode", ex.Message, StringComparison.Ordinal);
            Assert.Contains("GXMCP_SHARED_MEMORY_FLOOR_MB", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HttpShared_WithNoNewSetting_StillLoads()
        {
            var (config, _) = Load(HttpSharedDocument(sharedHost: null));

            Assert.Equal("http-shared", config.GatewayMode);
            Assert.True(config.EffectiveSharedHost.IsValid, string.Join(" | ", config.EffectiveSharedHost.Errors));
        }

        [Theory]
        [MemberData(nameof(InvalidValueCases))]
        public void InvalidNewSettingValue_IsRejectedAtLoadNamingSettingValueAndRange(
            bool strict, string key, string source, string raw, string rejected, string range)
        {
            var setting = NewSettings.Single(s => s.Key == key);
            JObject? sharedHost = null;
            var env = new List<(string, string?)>();
            string expectedName;
            if (source == "file")
            {
                sharedHost = new JObject { [key] = JToken.Parse(raw) };
                expectedName = "Server.SharedHost." + key;
            }
            else
            {
                env.Add((setting.Env, raw));
                expectedName = setting.Env;
            }

            var ex = Assert.Throws<InvalidDataException>(() => Load(Document(strict, "shared-host", sharedHost), env.ToArray()));

            Assert.Contains(expectedName, ex.Message, StringComparison.Ordinal);
            Assert.Contains("'" + rejected + "'", ex.Message, StringComparison.Ordinal);
            Assert.Contains(range, ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(DocumentKinds))]
        public void SessionQueueCapAboveEffectiveDepth_IsRejectedWithTheDepthBoundRange(bool strict)
        {
            var sharedHost = new JObject { ["QueueDepth"] = 2, ["SessionQueueCap"] = 3 };

            var ex = Assert.Throws<InvalidDataException>(() => Load(Document(strict, "shared-host", sharedHost)));

            Assert.Contains("Server.SharedHost.SessionQueueCap", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'3'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("1-2", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(DocumentKinds))]
        public void InvalidNewSettingValue_InIsolatedMode_IsAlsoRejected(bool strict)
        {
            var sharedHost = new JObject { ["AttachReadyTimeoutSeconds"] = 5 };

            var ex = Assert.Throws<InvalidDataException>(() => Load(Document(strict, "isolated", sharedHost)));

            Assert.Contains("Server.SharedHost.AttachReadyTimeoutSeconds", ex.Message, StringComparison.Ordinal);
            Assert.Contains("10-600", ex.Message, StringComparison.Ordinal);
        }

        // A strict v2 document (ConfigSchemaVersion and GatewayMode present) or a legacy document (neither).
        private static JObject Document(bool strict, string sharingMode, JObject? sharedHost)
        {
            var server = new JObject { ["WorkerSharingMode"] = sharingMode };
            if (strict)
            {
                server.AddFirst(new JProperty("McpStdio", true));
                server.AddFirst(new JProperty("HttpPort", 0));
            }
            if (sharedHost != null) server["SharedHost"] = sharedHost;

            if (!strict) return new JObject { ["Server"] = server };

            return new JObject
            {
                ["ConfigSchemaVersion"] = 2,
                ["GatewayMode"] = "stdio-isolated",
                ["GeneXus"] = StrictGeneXus(),
                ["Server"] = server,
                ["Environment"] = new JObject { ["ResolutionPolicy"] = "strict" },
            };
        }

        private static JObject HttpSharedDocument(JObject? sharedHost)
        {
            var server = new JObject { ["HttpPort"] = 5100, ["McpStdio"] = false, ["WorkerSharingMode"] = "isolated" };
            if (sharedHost != null) server["SharedHost"] = sharedHost;
            return new JObject
            {
                ["ConfigSchemaVersion"] = 2,
                ["GatewayMode"] = "http-shared",
                ["GeneXus"] = StrictGeneXus(),
                ["Server"] = server,
                ["Environment"] = new JObject { ["ResolutionPolicy"] = "strict" },
            };
        }

        private static JObject StrictGeneXus() => new JObject
        {
            ["InstallationPath"] = @"C:\Program Files (x86)\GeneXus\GeneXus18",
            ["WorkerExecutable"] = @"worker\GxMcp.Worker.exe",
        };

        // Writes the document, loads it through ParseConfig with only the given variables set, and returns
        // the Gateway log lines written during the load.
        private (Configuration Config, string[] Log) Load(JObject document, params (string Name, string? Value)[] environment)
        {
            string path = Path.Combine(_directory, "config-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, document.ToString());

            var saved = ControlledVariables.Concat(environment.Select(e => e.Name)).Distinct(StringComparer.Ordinal)
                .ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            try
            {
                foreach (string name in saved.Keys)
                    Environment.SetEnvironmentVariable(name, null);
                foreach (var (name, value) in environment)
                    Environment.SetEnvironmentVariable(name, value);

                string marker = Guid.NewGuid().ToString("N");
                string begin = $"[SharedHostConfigTest] begin {marker}";
                string end = $"[SharedHostConfigTest] end {marker}";
                Program.Log(begin);
                Configuration config;
                try
                {
                    config = ParseConfig(path);
                }
                finally
                {
                    Program.Log(end);
                }
                return (config, LogLinesBetween(begin, end));
            }
            finally
            {
                foreach (var entry in saved)
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        // Program.Log has no in-memory seam, so the captured window of the Gateway log file is read back,
        // as SharedHostBackCompatTests does. Both markers must be present, so an unwritable log fails the
        // test instead of passing as "no entry".
        private static string[] LogLinesBetween(string beginMarker, string endMarker)
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

            return text.Substring(begin + beginMarker.Length, end - begin - beginMarker.Length)
                .Split('\n')
                .Where(line => !line.Contains(endMarker, StringComparison.Ordinal) && line.Trim().Length > 0)
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
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
