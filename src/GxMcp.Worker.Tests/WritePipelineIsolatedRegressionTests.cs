using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // GXMCP_SHARED_CHILD and Logger.TestSink are process-wide; this collection runs alone so no
    // other test changes either of them while these tests read them.
    [CollectionDefinition("WritePipelineIsolatedEnvironment", DisableParallelization = true)]
    public sealed class WritePipelineIsolatedEnvironmentCollection { }

    /// <summary>
    /// Regression pin for the Isolated_Mode <c>WritePipeline.AdvisoryLockCheck</c> path (issue #854,
    /// Requirements 9.7, 10.1 and 13.9). It runs with <c>GXMCP_SHARED_CHILD</c> unset and fixes the
    /// exact <c>force=true</c> warning line and the exact <c>TargetLockedByOtherAgent</c> envelope,
    /// so the later shared-child audit entry cannot change either of them.
    /// </summary>
    [Collection("WritePipelineIsolatedEnvironment")]
    [Trait("Feature", "concurrent-multi-session-workers")]
    public sealed class WritePipelineIsolatedRegressionTests : IDisposable
    {
        private const string SharedChildVariable = "GXMCP_SHARED_CHILD";
        private const string Holder = "lease-a";
        private const string Caller = "lease-b";

        private readonly string _savedSharedChild;
        private readonly string _kbPath;
        private readonly string _target;
        private readonly List<KeyValuePair<string, string>> _captured = new List<KeyValuePair<string, string>>();

        public WritePipelineIsolatedRegressionTests()
        {
            _savedSharedChild = Environment.GetEnvironmentVariable(SharedChildVariable);
            Environment.SetEnvironmentVariable(SharedChildVariable, null);
            _kbPath = Path.Combine(Path.GetTempPath(), "gxmcp_wp_isolated_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_kbPath);
            // A unique object name lets the sink ignore lines from any other code path.
            _target = "Invoice" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Logger.TestSink = (level, message) =>
            {
                if (message != null && message.IndexOf(_target, StringComparison.Ordinal) >= 0)
                    lock (_captured) _captured.Add(new KeyValuePair<string, string>(level, message));
            };
        }

        public void Dispose()
        {
            Logger.TestSink = null;
            Environment.SetEnvironmentVariable(SharedChildVariable, _savedSharedChild);
            try { Directory.Delete(_kbPath, true); } catch { }
        }

        private static DateTime RecentWholeSecondUtc()
        {
            DateTime now = DateTime.UtcNow.AddSeconds(-30);
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
        }

        private void WriteLock(string part, JObject entry)
        {
            string locksDir = Path.Combine(_kbPath, ".gx", "locks");
            Directory.CreateDirectory(locksDir);
            File.WriteAllText(Path.Combine(locksDir, LockFileKey.Sanitize(_target, part) + ".lock"),
                entry.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void WriteActiveLock(string part, DateTime atUtc, int? ttlSec)
        {
            var entry = new JObject
            {
                ["ownerId"] = Holder,
                ["atUtc"] = atUtc.ToString("o"),
                ["target"] = _target,
                ["part"] = part ?? string.Empty
            };
            if (ttlSec.HasValue) entry["ttlSec"] = ttlSec.Value;
            WriteLock(part, entry);
        }

        private List<KeyValuePair<string, string>> Captured()
        {
            lock (_captured) return _captured.ToList();
        }

        private static void AssertExactEnvelope(JObject expected, JObject actual)
        {
            Assert.NotNull(actual);
            // Exact field set and order, then exact values.
            Assert.Equal(expected.Properties().Select(p => p.Name).ToArray(),
                actual.Properties().Select(p => p.Name).ToArray());
            Assert.True(JToken.DeepEquals(expected, actual),
                "Envelope changed:\nexpected " + expected.ToString(Newtonsoft.Json.Formatting.None) +
                "\nactual   " + actual.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void Force_OnForeignActiveLock_ReturnsNull_AndLogsExactWarningOnce()
        {
            Assert.Null(Environment.GetEnvironmentVariable(SharedChildVariable));
            WriteActiveLock("Source", RecentWholeSecondUtc(), 300);

            JObject result = WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", Caller, force: true);

            Assert.Null(result);
            var lines = Captured();
            var only = Assert.Single(lines);
            Assert.Equal("WARN", only.Key);
            Assert.Equal(
                "[WritePipeline] force=true: bypassing advisory lock on '" + _target + "/Source' held by 'lease-a'.",
                only.Value);
        }

        [Fact]
        public void Force_WithNullPart_KeepsEmptyPartInWarningText()
        {
            WriteActiveLock(null, RecentWholeSecondUtc(), 300);

            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, null, Caller, force: true));

            var only = Assert.Single(Captured());
            Assert.Equal("WARN", only.Key);
            Assert.Equal(
                "[WritePipeline] force=true: bypassing advisory lock on '" + _target + "/' held by 'lease-a'.",
                only.Value);
        }

        [Fact]
        public void Force_OnOwnLockOrNoLock_LogsNothing()
        {
            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", Caller, force: true));
            WriteActiveLock("Source", RecentWholeSecondUtc(), 300);
            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", Holder, force: true));

            Assert.Empty(Captured());
        }

        [Fact]
        public void NoForce_OnForeignActiveLock_ReturnsExactTargetLockedEnvelope()
        {
            Assert.Null(Environment.GetEnvironmentVariable(SharedChildVariable));
            DateTime at = RecentWholeSecondUtc();
            WriteActiveLock("Source", at, 300);

            JObject result = WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", Caller, force: false);

            string expiresAt = at.AddSeconds(300).ToString("o");
            Assert.EndsWith(".0000000Z", expiresAt);
            AssertExactEnvelope(new JObject
            {
                ["status"] = "Error",
                ["code"] = "TargetLockedByOtherAgent",
                ["message"] = "Object '" + _target + "' part 'Source' is locked by agent 'lease-a'.",
                ["hint"] = "Wait for the lock to expire or use force=true to override. Acquire the lock with genexus_multi_agent_lock action=acquire.",
                ["lockHolder"] = Holder,
                ["lockExpiresAt"] = expiresAt,
                ["target"] = _target
            }, result);
            // Isolated_Mode carries no retry fields and no part field today.
            Assert.Null(result["retryable"]);
            Assert.Null(result["retryAfterMs"]);
            Assert.Null(result["part"]);
            Assert.Empty(Captured());
        }

        [Fact]
        public void NoForce_NullPartAndMissingTtl_UsesSourceInMessage_AndDefault300sExpiry()
        {
            DateTime at = RecentWholeSecondUtc();
            WriteActiveLock(null, at, ttlSec: null);

            JObject result = WritePipeline.AdvisoryLockCheck(_kbPath, _target, null, Caller, force: false);

            AssertExactEnvelope(new JObject
            {
                ["status"] = "Error",
                ["code"] = "TargetLockedByOtherAgent",
                ["message"] = "Object '" + _target + "' part 'Source' is locked by agent 'lease-a'.",
                ["hint"] = "Wait for the lock to expire or use force=true to override. Acquire the lock with genexus_multi_agent_lock action=acquire.",
                ["lockHolder"] = Holder,
                ["lockExpiresAt"] = at.AddSeconds(300).ToString("o"),
                ["target"] = _target
            }, result);
        }

        [Fact]
        public void NoForce_UnparseableAtUtc_IsStillBlocking_WithNullExpiry()
        {
            WriteLock("Source", new JObject
            {
                ["ownerId"] = Holder,
                ["atUtc"] = "not-a-time",
                ["ttlSec"] = 300,
                ["target"] = _target,
                ["part"] = "Source"
            });

            JObject result = WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", Caller, force: false);

            AssertExactEnvelope(new JObject
            {
                ["status"] = "Error",
                ["code"] = "TargetLockedByOtherAgent",
                ["message"] = "Object '" + _target + "' part 'Source' is locked by agent 'lease-a'.",
                ["hint"] = "Wait for the lock to expire or use force=true to override. Acquire the lock with genexus_multi_agent_lock action=acquire.",
                ["lockHolder"] = Holder,
                ["lockExpiresAt"] = JValue.CreateNull(),
                ["target"] = _target
            }, result);
            Assert.Equal(JTokenType.Null, result["lockExpiresAt"].Type);
        }

        [Fact]
        public void LockExpiresAt_IsRoundTripUtc()
        {
            DateTime at = RecentWholeSecondUtc();
            WriteActiveLock("Source", at, 120);

            JObject result = WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", Caller, force: false);

            string raw = (string)result["lockExpiresAt"];
            DateTime parsed = DateTime.ParseExact(raw, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
            Assert.Equal(at.AddSeconds(120), parsed);
        }
    }
}
