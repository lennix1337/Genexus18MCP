#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Unit tests for SharedHostSettingsResolver (design: "Shared pure core", "New settings").
    // The environment is injected through the delegate; the process environment is never read or mutated.
    //
    // Contract pinned by these tests:
    // - SharedHostSettingsResult exposes IsValid, Errors (one message per problem), the seven effective
    //   integer values, and Sources keyed by the Server.SharedHost key name (for example "QueueDepth").
    // - Source strings: "env:<VARIABLE>", "config:Server.SharedHost.<Key>", "legacy-env:GXMCP_BUSY_WAIT_MS", "default".
    // - A setting is named in errors as "Server.SharedHost.<Key>" when it came from the file and as the
    //   environment variable name when it came from the environment. Ranges are written "<min>-<max>".
    public class SharedHostSettingsResolverTests
    {
        private const string IsolatedGatewayMode = "stdio-isolated";
        private const string HttpSharedGatewayMode = "http-shared";
        private const string LegacyBusyWait = "GXMCP_BUSY_WAIT_MS";

        public static IEnumerable<object[]> NewSettings()
        {
            yield return new object[] { "QueueWaitMs", "GXMCP_SHARED_QUEUE_WAIT_MS", 1000, 600000 };
            yield return new object[] { "QueueDepth", "GXMCP_SHARED_QUEUE_DEPTH", 1, 64 };
            yield return new object[] { "SessionQueueCap", "GXMCP_SHARED_SESSION_QUEUE_CAP", 1, 8 };
            yield return new object[] { "MaxWorkers", "GXMCP_SHARED_MAX_WORKERS", 1, 16 };
            yield return new object[] { "MemoryFloorMB", "GXMCP_SHARED_MEMORY_FLOOR_MB", 256, 8192 };
            yield return new object[] { "ColdStartConcurrency", "GXMCP_SHARED_COLD_START_CONCURRENCY", 1, 4 };
            yield return new object[] { "AttachReadyTimeoutSeconds", "GXMCP_SHARED_ATTACH_READY_TIMEOUT_S", 10, 600 };
        }

        // ---- Defaults ----

        [Fact]
        public void Resolve_NoSectionAndNoEnvironment_AppliesDocumentedDefaults()
        {
            var result = Resolve(section: null);

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Empty(result.Errors);
            Assert.Equal(30000, result.QueueWaitMs);
            Assert.Equal(8, result.QueueDepth);
            Assert.Equal(4, result.SessionQueueCap);
            Assert.Equal(4, result.MaxWorkers);
            Assert.Equal(1024, result.MemoryFloorMb);
            Assert.Equal(1, result.ColdStartConcurrency);
            Assert.Equal(120, result.AttachReadyTimeoutSeconds);
        }

        [Fact]
        public void Resolve_EmptySection_ReportsDefaultSourceForEverySetting()
        {
            var result = Resolve(new JObject());

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            foreach (var key in NewSettings().Select(row => (string)row[0]))
            {
                Assert.Equal("default", result.Sources[key]);
            }
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(4, 4)]
        [InlineData(5, 4)]
        [InlineData(64, 4)]
        public void Resolve_SessionQueueCapUnset_DefaultsToLowerOfFourAndEffectiveDepth(int depth, int expectedCap)
        {
            var result = Resolve(new JObject { ["QueueDepth"] = depth });

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(depth, result.QueueDepth);
            Assert.Equal(expectedCap, result.SessionQueueCap);
            Assert.Equal("default", result.Sources["SessionQueueCap"]);
        }

        // ---- Precedence and source reporting ----

        [Fact]
        public void Resolve_FileValue_IsUsedAndReportedAsConfigSource()
        {
            var result = Resolve(new JObject { ["MaxWorkers"] = 6 });

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(6, result.MaxWorkers);
            Assert.Equal("config:Server.SharedHost.MaxWorkers", result.Sources["MaxWorkers"]);
        }

        [Fact]
        public void Resolve_EnvironmentValue_IsUsedAndReportedAsEnvSource()
        {
            var result = Resolve(null, env: Env(("GXMCP_SHARED_MEMORY_FLOOR_MB", "2048")));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(2048, result.MemoryFloorMb);
            Assert.Equal("env:GXMCP_SHARED_MEMORY_FLOOR_MB", result.Sources["MemoryFloorMB"]);
        }

        [Fact]
        public void Resolve_LegacyDocument_EnvironmentOverridesFileValue()
        {
            var result = Resolve(
                new JObject { ["QueueDepth"] = 10 },
                strictDocument: false,
                env: Env(("GXMCP_SHARED_QUEUE_DEPTH", "20")));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(20, result.QueueDepth);
            Assert.Equal("env:GXMCP_SHARED_QUEUE_DEPTH", result.Sources["QueueDepth"]);
        }

        [Fact]
        public void Resolve_StrictDocument_EnvironmentFillsSettingAbsentFromFile()
        {
            var result = Resolve(
                new JObject { ["QueueDepth"] = 10 },
                strictDocument: true,
                env: Env(("GXMCP_SHARED_COLD_START_CONCURRENCY", "2")));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(10, result.QueueDepth);
            Assert.Equal(2, result.ColdStartConcurrency);
            Assert.Equal("config:Server.SharedHost.QueueDepth", result.Sources["QueueDepth"]);
            Assert.Equal("env:GXMCP_SHARED_COLD_START_CONCURRENCY", result.Sources["ColdStartConcurrency"]);
        }

        [Fact]
        public void Resolve_StrictDocument_EnvironmentEqualToExplicitFileValue_IsAccepted()
        {
            var result = Resolve(
                new JObject { ["QueueDepth"] = 10 },
                strictDocument: true,
                env: Env(("GXMCP_SHARED_QUEUE_DEPTH", "10")));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(10, result.QueueDepth);
        }

        [Fact]
        public void Resolve_StrictDocument_EnvironmentConflictingWithExplicitFileValue_IsRejected()
        {
            var result = Resolve(
                new JObject { ["QueueDepth"] = 10 },
                strictDocument: true,
                env: Env(("GXMCP_SHARED_QUEUE_DEPTH", "20")));

            Assert.False(result.IsValid);
            var error = Assert.Single(result.Errors);
            Assert.Contains("GXMCP_SHARED_QUEUE_DEPTH", error);
            Assert.Contains("Server.SharedHost.QueueDepth", error);
            Assert.Contains("20", error);
            Assert.Contains("10", error);
        }

        // ---- Legacy GXMCP_BUSY_WAIT_MS ----

        [Theory]
        [InlineData("1000", 1000)]
        [InlineData("45000", 45000)]
        [InlineData("600000", 600000)]
        public void Resolve_LegacyBusyWaitInRange_IsUsedWhenQueueWaitUnset(string legacy, int expected)
        {
            var result = Resolve(null, env: Env((LegacyBusyWait, legacy)));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(expected, result.QueueWaitMs);
            Assert.Equal("legacy-env:GXMCP_BUSY_WAIT_MS", result.Sources["QueueWaitMs"]);
        }

        [Theory]
        [InlineData("999")]
        [InlineData("600001")]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("abc")]
        [InlineData("1500.5")]
        public void Resolve_LegacyBusyWaitOutOfRangeOrNonInteger_FallsBackToDefaultWithoutError(string legacy)
        {
            var result = Resolve(null, env: Env((LegacyBusyWait, legacy)));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Empty(result.Errors);
            Assert.Equal(30000, result.QueueWaitMs);
            Assert.Equal("default", result.Sources["QueueWaitMs"]);
        }

        [Fact]
        public void Resolve_FileQueueWait_TakesPrecedenceOverLegacyBusyWait()
        {
            var result = Resolve(new JObject { ["QueueWaitMs"] = 20000 }, env: Env((LegacyBusyWait, "45000")));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(20000, result.QueueWaitMs);
            Assert.Equal("config:Server.SharedHost.QueueWaitMs", result.Sources["QueueWaitMs"]);
        }

        [Fact]
        public void Resolve_EnvironmentQueueWait_TakesPrecedenceOverLegacyBusyWait()
        {
            var result = Resolve(null, env: Env(("GXMCP_SHARED_QUEUE_WAIT_MS", "5000"), (LegacyBusyWait, "45000")));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(5000, result.QueueWaitMs);
            Assert.Equal("env:GXMCP_SHARED_QUEUE_WAIT_MS", result.Sources["QueueWaitMs"]);
        }

        // ---- Range and type validation for each of the seven New_Settings ----

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_FileValueAtRangeBounds_IsAccepted(string key, string envName, int min, int max)
        {
            _ = envName;
            foreach (var value in new[] { min, max })
            {
                var result = Resolve(new JObject { [key] = value });

                Assert.True(result.IsValid, key + "=" + value + ": " + string.Join("; ", result.Errors));
                Assert.Equal(value, EffectiveValue(result, key));
            }
        }

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_FileValueBelowRange_IsRejectedNamingSettingValueAndRange(string key, string envName, int min, int max)
        {
            _ = envName;
            var rejected = min - 1;
            var result = Resolve(new JObject { [key] = rejected });

            AssertSingleRejection(result, "Server.SharedHost." + key, rejected.ToString(), min, max);
        }

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_FileValueAboveRange_IsRejectedNamingSettingValueAndRange(string key, string envName, int min, int max)
        {
            _ = envName;
            var rejected = max + 1;
            var result = Resolve(new JObject { [key] = rejected });

            AssertSingleRejection(result, "Server.SharedHost." + key, rejected.ToString(), min, max);
        }

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_FileValueNonInteger_IsRejectedNamingSettingValueAndRange(string key, string envName, int min, int max)
        {
            _ = envName;
            var stringResult = Resolve(new JObject { [key] = "abc" });
            AssertSingleRejection(stringResult, "Server.SharedHost." + key, "abc", min, max);

            var fractionResult = Resolve(new JObject { [key] = 2.5 });
            AssertSingleRejection(fractionResult, "Server.SharedHost." + key, "2.5", min, max);
        }

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_EnvironmentValueAtRangeBounds_IsAccepted(string key, string envName, int min, int max)
        {
            foreach (var value in new[] { min, max })
            {
                var result = Resolve(null, env: Env((envName, value.ToString())));

                Assert.True(result.IsValid, envName + "=" + value + ": " + string.Join("; ", result.Errors));
                Assert.Equal(value, EffectiveValue(result, key));
                Assert.Equal("env:" + envName, result.Sources[key]);
            }
        }

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_EnvironmentValueOutOfRange_IsRejectedNamingSettingValueAndRange(string key, string envName, int min, int max)
        {
            _ = key;
            var below = Resolve(null, env: Env((envName, (min - 1).ToString())));
            AssertSingleRejection(below, envName, (min - 1).ToString(), min, max);

            var above = Resolve(null, env: Env((envName, (max + 1).ToString())));
            AssertSingleRejection(above, envName, (max + 1).ToString(), min, max);
        }

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_EnvironmentValueNonInteger_IsRejectedNamingSettingValueAndRange(string key, string envName, int min, int max)
        {
            _ = key;
            foreach (var rejected in new[] { "abc", "2.5", "12ms" })
            {
                var result = Resolve(null, env: Env((envName, rejected)));
                AssertSingleRejection(result, envName, rejected, min, max);
            }
        }

        // ---- Session_Queue_Cap against the effective queue depth ----

        [Fact]
        public void Resolve_SessionQueueCapAboveEffectiveDepth_IsRejectedNamingRangeUpToDepth()
        {
            var result = Resolve(new JObject { ["QueueDepth"] = 2, ["SessionQueueCap"] = 3 });

            AssertSingleRejection(result, "Server.SharedHost.SessionQueueCap", "3", 1, 2);
        }

        [Fact]
        public void Resolve_SessionQueueCapAboveDepthFromEnvironment_IsRejected()
        {
            var result = Resolve(
                new JObject { ["SessionQueueCap"] = 5 },
                env: Env(("GXMCP_SHARED_QUEUE_DEPTH", "4")));

            AssertSingleRejection(result, "Server.SharedHost.SessionQueueCap", "5", 1, 4);
        }

        [Fact]
        public void Resolve_SessionQueueCapEqualToEffectiveDepth_IsAccepted()
        {
            var result = Resolve(new JObject { ["QueueDepth"] = 12, ["SessionQueueCap"] = 12 });

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(12, result.SessionQueueCap);
            Assert.Equal("config:Server.SharedHost.SessionQueueCap", result.Sources["SessionQueueCap"]);
        }

        // ---- GatewayMode="http-shared" ----

        [Fact]
        public void Resolve_HttpSharedWithoutExplicitNewSetting_IsAccepted()
        {
            var result = Resolve(new JObject(), gatewayMode: HttpSharedGatewayMode);

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
        }

        [Fact]
        public void Resolve_HttpSharedWithOnlyLegacyBusyWait_IsAccepted()
        {
            var result = Resolve(null, gatewayMode: HttpSharedGatewayMode, env: Env((LegacyBusyWait, "45000")));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
        }

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_HttpSharedWithExplicitFileSetting_IsRejectedNamingGatewayModeAndSetting(string key, string envName, int min, int max)
        {
            _ = envName;
            _ = max;
            var result = Resolve(new JObject { [key] = min }, gatewayMode: HttpSharedGatewayMode);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("GatewayMode") && e.Contains("Server.SharedHost." + key));
        }

        [Theory]
        [MemberData(nameof(NewSettings))]
        public void Resolve_HttpSharedWithExplicitEnvironmentSetting_IsRejectedNamingGatewayModeAndSetting(string key, string envName, int min, int max)
        {
            _ = key;
            _ = max;
            var result = Resolve(null, gatewayMode: HttpSharedGatewayMode, env: Env((envName, min.ToString())));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("GatewayMode") && e.Contains(envName));
        }

        [Fact]
        public void Resolve_HttpSharedWithSeveralExplicitSettings_NamesEachSetting()
        {
            var result = Resolve(
                new JObject { ["QueueDepth"] = 8, ["MaxWorkers"] = 2 },
                gatewayMode: HttpSharedGatewayMode,
                env: Env(("GXMCP_SHARED_MEMORY_FLOOR_MB", "2048")));

            Assert.False(result.IsValid);
            var allErrors = string.Join("\n", result.Errors);
            Assert.Contains("GatewayMode", allErrors);
            Assert.Contains("Server.SharedHost.QueueDepth", allErrors);
            Assert.Contains("Server.SharedHost.MaxWorkers", allErrors);
            Assert.Contains("GXMCP_SHARED_MEMORY_FLOOR_MB", allErrors);
        }

        [Fact]
        public void Resolve_HttpSharedCheckIsCaseInsensitive()
        {
            var result = Resolve(new JObject { ["QueueDepth"] = 8 }, gatewayMode: "HTTP-SHARED");

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("GatewayMode"));
        }

        // ---- Helpers ----

        private static SharedHostSettingsResult Resolve(
            JObject? section,
            bool strictDocument = false,
            string gatewayMode = IsolatedGatewayMode,
            Func<string, string?>? env = null)
        {
            return SharedHostSettingsResolver.Resolve(section, strictDocument, gatewayMode, env ?? Env());
        }

        private static Func<string, string?> Env(params (string Name, string Value)[] values)
        {
            var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
            return name => map.TryGetValue(name, out var value) ? value : null;
        }

        private static int EffectiveValue(SharedHostSettingsResult result, string key)
        {
            switch (key)
            {
                case "QueueWaitMs": return result.QueueWaitMs;
                case "QueueDepth": return result.QueueDepth;
                case "SessionQueueCap": return result.SessionQueueCap;
                case "MaxWorkers": return result.MaxWorkers;
                case "MemoryFloorMB": return result.MemoryFloorMb;
                case "ColdStartConcurrency": return result.ColdStartConcurrency;
                case "AttachReadyTimeoutSeconds": return result.AttachReadyTimeoutSeconds;
                default: throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown New_Setting key.");
            }
        }

        private static void AssertSingleRejection(SharedHostSettingsResult result, string settingName, string rejectedValue, int min, int max)
        {
            Assert.False(result.IsValid, settingName + "=" + rejectedValue + " should be rejected");
            var error = Assert.Single(result.Errors);
            Assert.Contains(settingName, error);
            Assert.Contains(rejectedValue, error);
            Assert.Contains(min + "-" + max, error);
        }
    }
}
