using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Shared-child write-conflict contract of the Worker (issue #854, Requirement 9). Every test
    /// runs with <c>GXMCP_SHARED_CHILD=1</c>, which is how a Worker_Host starts its Worker child.
    /// <para>
    /// "Across Gateways": in shared mode one Worker process serves every Gateway attached to the
    /// KB, and each Gateway reaches it with its own owner id (<c>_gxmcpOwnerId</c>). The tests
    /// therefore simulate several Gateways as distinct owner ids (<c>lease-a</c>, <c>lease-b</c>)
    /// on the same Worker process and the same KB lock directory.
    /// </para>
    /// <para>
    /// The SDK save itself needs an open KB, so the tests drive the real lock primitives the write
    /// path calls (<c>WritePipeline.AdvisoryLockCheck</c>, <c>WritePipeline.HasActiveOwnedLock</c>,
    /// <c>WriteService.TryAcquireOperationLock</c>, <c>WriteService.CheckStaleVersion</c>,
    /// <c>MultiAgentLockService.DispatchCore</c>) and pin the ordering inside
    /// <c>WriteService.WriteObject</c> ("before any SDK save", "released in finally") as source
    /// shape. Fields that the Gateway <c>ConcurrencyErrorNormalizer</c> adds (tasks 6.4 and 6.7:
    /// <c>part</c>, <c>retryable</c> and <c>retryAfterMs</c> on <c>TargetLockedByOtherAgent</c>,
    /// <c>retryable</c> on <c>StaleObject</c> and <c>VersionCheckUnavailable</c>) are not
    /// asserted here.
    /// </para>
    /// <para>
    /// Pinned seam for task 6.10 (the force-override audit entry, Requirement 9.7). When
    /// <c>GXMCP_SHARED_CHILD</c> is <c>"1"</c> and
    /// <c>WritePipeline.AdvisoryLockCheck(kbPath, target, part, ownerId, force: true)</c> finds an
    /// unexpired lock held by a different owner, it still returns <c>null</c> (the write goes on)
    /// and emits exactly one <c>Logger.Warn</c> line, level <c>WARN</c>, whose message is
    /// <c>"[WritePipeline] force-override audit "</c> followed by compact JSON with exactly these
    /// properties in this order:
    /// <c>{"event":"forceOverride","overridingOwner":&lt;ownerId&gt;,"previousHolder":&lt;lock ownerId&gt;,"target":&lt;target&gt;,"part":&lt;part as passed&gt;,"atUtc":&lt;DateTime.UtcNow.ToString("o")&gt;}</c>.
    /// That line replaces the Isolated_Mode warning in shared mode, so it is the only line that
    /// names the target. No line is emitted when there is no foreign unexpired lock. A corrupt or
    /// unreadable lock still returns <c>LockCheckFailed</c> and is never audited as an override.
    /// The Isolated_Mode warning and envelope are pinned by
    /// <see cref="WritePipelineIsolatedRegressionTests"/>, which shares this collection.
    /// </para>
    /// <para>
    /// Second pinned seam (also Requirement 9.7, "SHALL perform the write despite that lock"):
    /// <c>WriteService.TryAcquireOperationLock(kbPath, target, part, ownerId, force: true, out error)</c>
    /// over an unexpired foreign lock returns a <c>null</c> lease and a <c>null</c> error and leaves
    /// the holder's lock file byte-for-byte unchanged. Today it reads the top-level <c>code</c> of the
    /// <c>MultiAgentLockService</c> reply, while the <c>AlreadyHeld</c> refusal carries its code under
    /// <c>error.code</c>, so the force exemption never matches and the forced write is refused with
    /// <c>AlreadyHeld</c>. That path is not shared-child specific; task 6.10 or a separate fix owns it.
    /// </para>
    /// </summary>
    [Collection("WritePipelineIsolatedEnvironment")]
    [Trait("Feature", "concurrent-multi-session-workers")]
    public sealed class SharedWriteConflictTests : IDisposable
    {
        private const string SharedChildVariable = "GXMCP_SHARED_CHILD";
        private const string LeaseA = "lease-a";
        private const string LeaseB = "lease-b";
        private const string AuditPrefix = "[WritePipeline] force-override audit ";

        private readonly string _savedSharedChild;
        private readonly string _kbPath;
        private readonly string _target;
        private readonly List<KeyValuePair<string, string>> _captured = new List<KeyValuePair<string, string>>();

        public SharedWriteConflictTests()
        {
            _savedSharedChild = Environment.GetEnvironmentVariable(SharedChildVariable);
            Environment.SetEnvironmentVariable(SharedChildVariable, "1");
            _kbPath = Path.Combine(Path.GetTempPath(), "gxmcp_wp_shared_" + Guid.NewGuid().ToString("N"));
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

        // --- helpers -----------------------------------------------------------------

        private List<KeyValuePair<string, string>> Captured()
        {
            lock (_captured) return _captured.ToList();
        }

        private static JObject ParseNoDates(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private string LockPath(string target, string part)
        {
            return Path.Combine(_kbPath, ".gx", "locks", LockFileKey.Sanitize(target, part) + ".lock");
        }

        private byte[] LockBytes(string target, string part)
        {
            return File.ReadAllBytes(LockPath(target, part));
        }

        private void AcquireExplicit(string target, string part, string owner)
        {
            JObject r = ParseNoDates(MultiAgentLockService.DispatchCore(_kbPath, "acquire", target, part, owner, 300));
            Assert.Equal("LockAcquired", (string)r["code"]);
        }

        private void WriteLockEntry(string target, string part, string owner, DateTime atUtc, int ttlSec)
        {
            Directory.CreateDirectory(Path.Combine(_kbPath, ".gx", "locks"));
            var entry = new JObject
            {
                ["ownerId"] = owner,
                ["atUtc"] = atUtc.ToString("o"),
                ["ttlSec"] = ttlSec,
                ["target"] = target,
                ["part"] = part ?? string.Empty
            };
            File.WriteAllText(LockPath(target, part), entry.ToString(Formatting.None));
        }

        private static IDisposable TryAcquireOperationLock(
            string kbPath, string target, string part, string owner, bool force, out string error)
        {
            MethodInfo method = typeof(WriteService).GetMethod("TryAcquireOperationLock", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var args = new object[] { kbPath, target, part, owner, force, null };
            var lease = (IDisposable)method.Invoke(null, args);
            error = (string)args[5];
            return lease;
        }

        private static string CheckStaleVersionWithUnreadableToken(string target, string baseVersion)
        {
            // No ObjectService: the fresh read throws inside CheckStaleVersion, which is the
            // "cannot read the current version token" case.
            var writer = (WriteService)FormatterServices.GetUninitializedObject(typeof(WriteService));
            MethodInfo method = typeof(WriteService).GetMethod("CheckStaleVersion", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (string)method.Invoke(writer, new object[] { target, "Source", null, baseVersion });
        }

        private static string WriteObjectBody()
        {
            string source = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.cs"));
            return SourceAssert.MethodBody(source,
                "public string WriteObject(string target, string partName, string code,");
        }

        private static string CheckStaleVersionBody()
        {
            string source = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.cs"));
            return SourceAssert.MethodBody(source,
                "private string CheckStaleVersion(string target, string partName, string typeFilter, string baseVersion)");
        }

        private static int IndexOf(string body, string needle)
        {
            int at = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found in WriteObject: " + needle);
            return at;
        }

        // --- 9.1 operation-scoped lock -------------------------------------------------

        [Fact]
        public void OperationLock_IsHeldByTheWriter_AndReleasedOnDispose()
        {
            IDisposable lease = TryAcquireOperationLock(_kbPath, _target, "Source", LeaseA, false, out string error);

            Assert.Null(error);
            Assert.NotNull(lease);
            JObject held = ParseNoDates(File.ReadAllText(LockPath(_target, "Source")));
            Assert.Equal(LeaseA, (string)held["ownerId"]);
            // While held, another Gateway's write is refused.
            Assert.Equal("TargetLockedByOtherAgent",
                (string)WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, false)?["code"]);

            lease.Dispose();

            Assert.False(File.Exists(LockPath(_target, "Source")));
            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, false));
        }

        [Fact]
        public void WriteObject_TakesEveryConflictCheckBeforeTheSave_AndReleasesTheOperationLockInFinally()
        {
            string body = WriteObjectBody();

            int perTarget = IndexOf(body, "lock (AcquirePerTargetLock(target))");
            int stale = IndexOf(body, "CheckStaleVersion(target, partName, typeFilter, baseVersion)");
            int staleReturn = IndexOf(body, "return staleErr;");
            int advisory = IndexOf(body, "WritePipeline.AdvisoryLockCheck(");
            int advisoryReturn = IndexOf(body, "return lockError.ToString(");
            int opLock = IndexOf(body, "operationLock = TryAcquireOperationLock(");
            int opLockReturn = IndexOf(body, "return operationLockError;");
            int save = IndexOf(body, "raw = WriteObjectInternal(");
            int success = IndexOf(body, "return wrapped;");
            int release = IndexOf(body, "operationLock?.Dispose();");

            Assert.True(perTarget < stale, "per-target lock is taken before the version check");
            Assert.True(stale < staleReturn && staleReturn < advisory, "stale rejection returns before the lock checks");
            Assert.True(advisory < advisoryReturn && advisoryReturn < opLock, "foreign-lock rejection returns before the operation lock");
            Assert.True(opLock < opLockReturn && opLockReturn < save, "operation lock is acquired before the SDK save");
            // Success, validation rejection (an error string from WriteObjectInternal) and a thrown
            // save failure all leave through the same finally.
            Assert.True(save < success && success < release, "release follows every exit of the pipeline");
            Assert.Contains("finally", body.Substring(success, release - success));
            Assert.Equal(1, SourceAssert.Count(body, "operationLock?.Dispose();"));
        }

        // --- 9.2 foreign lock from another Gateway ----------------------------------------

        [Fact]
        public void ForeignLock_SameObjectNameFromAnotherGateway_IsRejected_AndLeavesTheLockUnchanged()
        {
            DateTime before = DateTime.UtcNow;
            AcquireExplicit(_target, "Source", LeaseA);
            DateTime after = DateTime.UtcNow;
            byte[] lockBefore = LockBytes(_target, "Source");

            JObject rejected = WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, false);

            Assert.NotNull(rejected);
            Assert.Equal("TargetLockedByOtherAgent", (string)rejected["code"]);
            Assert.Equal(_target, (string)rejected["target"]);
            Assert.Equal(LeaseA, (string)rejected["lockHolder"]);
            DateTime expires = DateTime.ParseExact((string)rejected["lockExpiresAt"], "o",
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, expires.Kind);
            Assert.InRange(expires, before.AddSeconds(300).AddSeconds(-1), after.AddSeconds(300).AddSeconds(1));
            // No source content of any object in the refusal.
            Assert.Null(rejected["source"]);
            Assert.Null(rejected["content"]);

            // The operation lock refuses too and does not touch the holder's lock.
            IDisposable lease = TryAcquireOperationLock(_kbPath, _target, "Source", LeaseB, false, out string error);
            Assert.Null(lease);
            Assert.NotNull(error);
            Assert.Equal("AlreadyHeld", (string)ParseNoDates(error)["error"]["code"]);
            Assert.Equal(lockBefore, LockBytes(_target, "Source"));
            Assert.Empty(Captured());
        }

        // --- 9.3 different objects or parts --------------------------------------------

        [Fact]
        public async Task DifferentObjectsOrParts_FromTwoGateways_BothProceed()
        {
            string other = _target + "Other";
            AcquireExplicit(_target, "Source", LeaseA);

            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "WebForm", LeaseB, false));
            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, other, "Source", LeaseB, false));

            var errors = new string[2];
            var leases = new IDisposable[2];
            using (var start = new ManualResetEventSlim(false))
            {
                Task first = Task.Run(() =>
                {
                    start.Wait();
                    leases[0] = TryAcquireOperationLock(_kbPath, _target, "WebForm", LeaseB, false, out errors[0]);
                });
                Task second = Task.Run(() =>
                {
                    start.Wait();
                    leases[1] = TryAcquireOperationLock(_kbPath, other, "Source", LeaseA, false, out errors[1]);
                });
                start.Set();
                Task both = Task.WhenAll(first, second);
                Assert.Same(both, await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(30))));
                await both;
            }

            Assert.Null(errors[0]);
            Assert.Null(errors[1]);
            Assert.NotNull(leases[0]);
            Assert.NotNull(leases[1]);
            leases[0].Dispose();
            leases[1].Dispose();
            Assert.False(File.Exists(LockPath(_target, "WebForm")));
            Assert.False(File.Exists(LockPath(other, "Source")));
            // The explicit lock on the other part is untouched.
            Assert.True(WritePipeline.HasActiveOwnedLock(_kbPath, _target, "Source", LeaseA));

            // One Worker serializes writers to the same object name and keeps different
            // objects on different monitors.
            Assert.Same(WriteService.AcquirePerTargetLock(_target), WriteService.AcquirePerTargetLock(_target));
            Assert.NotSame(WriteService.AcquirePerTargetLock(_target), WriteService.AcquirePerTargetLock(other));
        }

        // --- 9.4 and 9.5 version token ----------------------------------------------------

        [Fact]
        public void StaleCheck_ComparesTheFreshVersionTokenOrdinally_AndReturnsBothTokens()
        {
            string body = CheckStaleVersionBody();

            // A fresh SDK read, not the read cache, so an IDE change is seen.
            Assert.Contains("_objectService.ReadObjectSourceForVerification(target, partName, typeFilter)", body);
            Assert.Contains("response[\"versionToken\"]", body);
            Assert.Contains("string.Equals(current, baseVersion, StringComparison.Ordinal)", body);
            Assert.Contains("code: \"StaleObject\"", body);
            Assert.Contains("[\"expectedVersion\"] = baseVersion, [\"currentVersion\"] = current", body);
        }

        // --- 9.6 explicit lock after its owner Gateway exits -------------------------------

        [Fact]
        public void ExplicitLock_WhoseGatewayExited_BlocksUntilExpiry_ThenAllowsWithoutCleanup()
        {
            // lease-a acquired the lock and its Gateway exited without releasing it.
            WriteLockEntry(_target, "Source", LeaseA, DateTime.UtcNow.AddSeconds(-290), 300);

            Assert.Equal("TargetLockedByOtherAgent",
                (string)WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, false)?["code"]);
            Assert.Null(TryAcquireOperationLock(_kbPath, _target, "Source", LeaseB, false, out string blocked));
            Assert.NotNull(blocked);

            // Past expiry: the write proceeds and the operation lock takes over the file.
            WriteLockEntry(_target, "Source", LeaseA, DateTime.UtcNow.AddSeconds(-310), 300);

            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, false));
            IDisposable lease = TryAcquireOperationLock(_kbPath, _target, "Source", LeaseB, false, out string error);
            Assert.Null(error);
            Assert.NotNull(lease);
            Assert.Equal(LeaseB, (string)ParseNoDates(File.ReadAllText(LockPath(_target, "Source")))["ownerId"]);
            lease.Dispose();
            Assert.False(File.Exists(LockPath(_target, "Source")));
        }

        // --- 9.7 force=true audit entry (fails until task 6.10) ----------------------------

        [Theory]
        [InlineData("Source")]
        [InlineData("WebForm")]
        public void SharedChild_ForceOverForeignLock_WritesExactlyOneAuditEntry(string part)
        {
            Assert.Equal("1", Environment.GetEnvironmentVariable(SharedChildVariable));
            AcquireExplicit(_target, part, LeaseA);
            byte[] lockBefore = LockBytes(_target, part);

            DateTime before = DateTime.UtcNow;
            JObject result = WritePipeline.AdvisoryLockCheck(_kbPath, _target, part, LeaseB, force: true);
            DateTime after = DateTime.UtcNow;

            Assert.Null(result);
            var only = Assert.Single(Captured());
            Assert.Equal("WARN", only.Key);
            Assert.StartsWith(AuditPrefix, only.Value);

            JObject audit = ParseNoDates(only.Value.Substring(AuditPrefix.Length));
            Assert.Equal(new[] { "event", "overridingOwner", "previousHolder", "target", "part", "atUtc" },
                audit.Properties().Select(p => p.Name).ToArray());
            Assert.Equal("forceOverride", (string)audit["event"]);
            Assert.Equal(LeaseB, (string)audit["overridingOwner"]);
            Assert.Equal(LeaseA, (string)audit["previousHolder"]);
            Assert.Equal(_target, (string)audit["target"]);
            Assert.Equal(part, (string)audit["part"]);
            string atRaw = (string)audit["atUtc"];
            DateTime at = DateTime.ParseExact(atRaw, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, at.Kind);
            Assert.EndsWith("Z", atRaw);
            Assert.InRange(at, before, after);

            // The override neither releases nor rewrites the holder's lock.
            Assert.Equal(lockBefore, LockBytes(_target, part));
        }

        [Fact]
        public void SharedChild_ForceWithoutForeignUnexpiredLock_WritesNoAuditEntry()
        {
            // No lock at all.
            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, force: true));
            // The caller's own lock.
            AcquireExplicit(_target, "Source", LeaseB);
            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, force: true));
            // A foreign lock that has expired.
            WriteLockEntry(_target, "WebForm", LeaseA, DateTime.UtcNow.AddSeconds(-400), 300);
            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "WebForm", LeaseB, force: true));

            Assert.Empty(Captured());
        }

        [Fact]
        public void Force_StillRejectsAStaleBaseToken()
        {
            // The version check has no force input and runs before any lock decision.
            MethodInfo check = typeof(WriteService).GetMethod("CheckStaleVersion", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(check);
            Assert.DoesNotContain(check.GetParameters(), p => p.ParameterType == typeof(bool));
            string body = WriteObjectBody();
            int staleGuard = IndexOf(body, "if (!string.IsNullOrWhiteSpace(baseVersion))");
            int forceRead = IndexOf(body, "bool advisoryForce");
            Assert.True(staleGuard < forceRead, "the stale check is decided before force is read");
            string staleBlock = SourceAssert.MethodBody(body, "if (!string.IsNullOrWhiteSpace(baseVersion))");
            Assert.DoesNotContain("advisoryForce", staleBlock);
            Assert.DoesNotContain("forceWrite", staleBlock);
        }

        [Fact]
        public void Force_OperationLockOverForeignLock_LetsTheWriteProceed_AndLeavesTheHolderInPlace()
        {
            // Fails today: TryAcquireOperationLock reads the top-level "code", but the AlreadyHeld
            // refusal is an McpResponse.Err with the code under error.code, so the force exemption
            // never matches and the forced write is refused with AlreadyHeld.
            AcquireExplicit(_target, "Source", LeaseA);
            byte[] lockBefore = LockBytes(_target, "Source");
            IDisposable lease = TryAcquireOperationLock(_kbPath, _target, "Source", LeaseB, true, out string error);
            Assert.Null(error);
            Assert.Null(lease);
            Assert.Equal(lockBefore, LockBytes(_target, "Source"));
        }

        // --- 9.8 same-owner write ------------------------------------------------------------

        [Fact]
        public void SameOwnerWrite_ProceedsWithoutConflict_AndKeepsTheExplicitLock()
        {
            AcquireExplicit(_target, "Source", LeaseA);
            byte[] lockBefore = LockBytes(_target, "Source");

            Assert.Null(WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseA, false));
            IDisposable lease = TryAcquireOperationLock(_kbPath, _target, "Source", LeaseA, false, out string error);
            Assert.Null(error);
            // No operation lease: disposing the write's scope must not release the explicit lock.
            Assert.Null(lease);

            Assert.Equal(lockBefore, LockBytes(_target, "Source"));
            Assert.True(WritePipeline.HasActiveOwnedLock(_kbPath, _target, "Source", LeaseA));
            Assert.Empty(Captured());
        }

        // --- 9.9 conflict check could not be completed ---------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnparseableLockState_IsLockCheckFailed_EvenWithForce(bool force)
        {
            Directory.CreateDirectory(Path.Combine(_kbPath, ".gx", "locks"));
            File.WriteAllText(LockPath(_target, "Source"), "{not json");
            byte[] lockBefore = LockBytes(_target, "Source");

            JObject result = WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, force);

            Assert.NotNull(result);
            Assert.Equal("LockCheckFailed", (string)result["code"]);
            Assert.True((bool)result["retryable"]);
            Assert.Equal(_target, (string)result["target"]);
            Assert.Equal(lockBefore, LockBytes(_target, "Source"));
            // A lock-check failure is not an override, so it is never audited.
            Assert.DoesNotContain(Captured(), line => line.Value.StartsWith(AuditPrefix, StringComparison.Ordinal));
        }

        [Fact]
        public void UnreadableLockFile_IsLockCheckFailed()
        {
            AcquireExplicit(_target, "Source", LeaseA);
            JObject result;
            using (new FileStream(LockPath(_target, "Source"), FileMode.Open, FileAccess.Read, FileShare.None))
                result = WritePipeline.AdvisoryLockCheck(_kbPath, _target, "Source", LeaseB, false);

            Assert.NotNull(result);
            Assert.Equal("LockCheckFailed", (string)result["code"]);
            Assert.True((bool)result["retryable"]);
        }

        [Fact]
        public void UnreadableCurrentVersionToken_IsVersionCheckUnavailable()
        {
            JObject result = ParseNoDates(CheckStaleVersionWithUnreadableToken(_target, "ver-1"));

            Assert.Equal("error", (string)result["status"]);
            Assert.Equal("VersionCheckUnavailable", (string)result["error"]["code"]);
            Assert.Equal(_target, (string)result["target"]);
            Assert.Equal("ver-1", (string)result["expectedVersion"]);
            Assert.Equal(JTokenType.Null, result["currentVersion"].Type);
        }

        // --- 9.10 no base token ----------------------------------------------------------------

        [Fact]
        public void NoBaseToken_SkipsOnlyTheVersionCheck()
        {
            string body = WriteObjectBody();

            string staleBlock = SourceAssert.MethodBody(body, "if (!string.IsNullOrWhiteSpace(baseVersion))");
            Assert.Contains("CheckStaleVersion(", staleBlock);
            Assert.DoesNotContain("AdvisoryLockCheck(", staleBlock);
            Assert.DoesNotContain("TryAcquireOperationLock(", staleBlock);

            // The lock checks are gated by the owner id only, never by the base token.
            string lockBlock = SourceAssert.MethodBody(body, "if (!string.IsNullOrWhiteSpace(advisoryOwnerId))");
            Assert.Contains("AdvisoryLockCheck(", lockBlock);
            Assert.Contains("TryAcquireOperationLock(", lockBlock);
            Assert.DoesNotContain("baseVersion", lockBlock);
            Assert.Equal(1, SourceAssert.Count(body, "CheckStaleVersion("));
        }
    }
}
