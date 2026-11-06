using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // GXMCP_SHARED_CHILD is process-wide; the tests below clear it and restore it afterwards.
    [CollectionDefinition("SingleInstanceLockEnvironment", DisableParallelization = true)]
    public sealed class SingleInstanceLockEnvironmentCollection { }

    /// <summary>
    /// Regression pin for the Isolated_Mode <c>SingleInstanceLock</c> path (issue #854,
    /// Requirements 10.1 and 13.9). It runs with <c>GXMCP_SHARED_CHILD</c> unset and fixes the
    /// lock-file payload, the PID-only liveness rule, acquire/release/held-by-other behavior,
    /// and the busy exit contract, so the later shared-child branch cannot change any of them.
    /// </summary>
    [Collection("SingleInstanceLockEnvironment")]
    [Trait("Feature", "concurrent-multi-session-workers")]
    public sealed class SingleInstanceLockIsolatedRegressionTests : IDisposable
    {
        private const string SharedChildVariable = "GXMCP_SHARED_CHILD";
        private const string WorkerExe = @"C:\Tools\GxMcp\worker\GxMcp.Worker.exe";

        private readonly string _savedSharedChild;
        private readonly string _lockDir;
        private readonly string _kbPath;

        public SingleInstanceLockIsolatedRegressionTests()
        {
            _savedSharedChild = Environment.GetEnvironmentVariable(SharedChildVariable);
            Environment.SetEnvironmentVariable(SharedChildVariable, null);
            _lockDir = Path.Combine(Path.GetTempPath(), "gxmcp-sil-isolated-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_lockDir);
            // A unique KB path keeps the Global\ mutex name unique to this test.
            _kbPath = @"C:\KBs\KbA-" + Guid.NewGuid().ToString("N");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SharedChildVariable, _savedSharedChild);
            try { Directory.Delete(_lockDir, true); } catch { }
        }

        private string ExpectedLockPath()
        {
            string hash = SingleInstanceLock.Hash(SingleInstanceLock.BuildKey(_kbPath, WorkerExe));
            return Path.Combine(_lockDir, ".worker-" + hash.Substring(0, 16) + ".lock");
        }

        // Same sharing as the production reader: the holder keeps a write + DeleteOnClose handle.
        private static byte[] ReadLockBytes(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static string ReadLockText(string path) => Encoding.UTF8.GetString(ReadLockBytes(path));

        private static int ExitedProcessId()
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.Id;
            }
        }

        private static void AssertCurrentProcessPayload(string text, DateTime notBeforeUtc, DateTime notAfterUtc)
        {
            const string startedPrefix = ",\"startedUtc\":\"";
            const string exePrefix = "\",\"exe\":\"";
            int startedAt = text.IndexOf(startedPrefix, StringComparison.Ordinal);
            int exeAt = text.IndexOf(exePrefix, StringComparison.Ordinal);
            Assert.True(startedAt > 0 && exeAt > startedAt, "Unexpected payload shape: " + text);
            string started = text.Substring(startedAt + startedPrefix.Length, exeAt - startedAt - startedPrefix.Length);

            string exe;
            using (var self = Process.GetCurrentProcess())
            {
                exe = self.MainModule?.FileName ?? "";
                // Exact field set, order, and format: {"pid":N,"startedUtc":"<o>","exe":"<escaped>"}.
                string expected = "{\"pid\":" + self.Id +
                    ",\"startedUtc\":\"" + started + "\"" +
                    ",\"exe\":\"" + exe.Replace("\\", "\\\\") + "\"}";
                Assert.Equal(expected, text);
            }

            // startedUtc is DateTime.UtcNow at acquisition in round-trip ("o") format, not the
            // process start time.
            DateTime parsed = DateTime.ParseExact(started, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
            Assert.Equal(parsed.ToString("o"), started);
            Assert.InRange(parsed, notBeforeUtc, notAfterUtc);
            Assert.DoesNotContain("processStartUtcTicks", text);
        }

        [Fact]
        public void BuildKey_And_LockPath_KeepTheirFormat()
        {
            Assert.Equal(@"kb=c:\kbs\kba|exe=c:\tools\gxmcp\worker\gxmcp.worker.exe",
                SingleInstanceLock.BuildKey(@"C:\KBs\KbA\", WorkerExe));

            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes("kb=x|exe=y"));
                var sb = new StringBuilder();
                foreach (byte b in digest) sb.Append(b.ToString("x2"));
                Assert.Equal(sb.ToString(), SingleInstanceLock.Hash("kb=x|exe=y"));
            }

            using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                Assert.True(l.Acquired);
                Assert.Equal(SingleInstanceLock.BuildKey(_kbPath, WorkerExe), l.Key);
                Assert.Equal(ExpectedLockPath(), l.LockPath);
            }
        }

        [Fact]
        public void Acquire_WritesExactPayload_WithoutBomOrStartTimeField()
        {
            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                DateTime after = DateTime.UtcNow.AddSeconds(1);
                Assert.True(l.Acquired);
                Assert.Null(l.ExistingPid);

                byte[] bytes = ReadLockBytes(l.LockPath);
                Assert.Equal((byte)'{', bytes[0]);
                AssertCurrentProcessPayload(Encoding.UTF8.GetString(bytes), before, after);
            }
        }

        [Fact]
        public void HeldByOther_IsRefusedWithHolderPid_AndHolderFileSurvives()
        {
            using (var holder = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                Assert.True(holder.Acquired);
                string payload = ReadLockText(holder.LockPath);

                var second = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir);
                Assert.False(second.Acquired);
                Assert.Equal(Process.GetCurrentProcess().Id, second.ExistingPid);
                Assert.Equal(holder.LockPath, second.LockPath);
                second.Dispose();

                // The refused instance (and its Dispose) leaves the holder's file intact.
                Assert.True(File.Exists(holder.LockPath));
                Assert.Equal(payload, ReadLockText(holder.LockPath));
            }
        }

        [Fact]
        public void Liveness_IsPidOnly_LivePidWithMismatchedStartTimeBlocks_AndFileIsUntouched()
        {
            string lockPath = ExpectedLockPath();
            int livePid = Process.GetCurrentProcess().Id;
            // The recorded times cannot belong to this process; Isolated_Mode must not care.
            string foreign = "{\"pid\":" + livePid +
                ",\"startedUtc\":\"2001-01-01T00:00:00.0000000Z\"" +
                ",\"processStartUtcTicks\":1" +
                ",\"exe\":\"C:\\\\Other\\\\GxMcp.Worker.exe\"}";
            File.WriteAllText(lockPath, foreign, new UTF8Encoding(false));

            using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                Assert.False(l.Acquired);
                Assert.Equal(livePid, l.ExistingPid);
                Assert.Equal(foreign, File.ReadAllText(lockPath));
            }
        }

        [Fact]
        public void Liveness_DeadPid_IsStale_FileIsReplacedWithOwnPayload()
        {
            string lockPath = ExpectedLockPath();
            int deadPid = ExitedProcessId();
            File.WriteAllText(lockPath,
                "{\"pid\":" + deadPid + ",\"startedUtc\":\"2001-01-01T00:00:00.0000000Z\",\"exe\":\"\"}",
                new UTF8Encoding(false));

            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                DateTime after = DateTime.UtcNow.AddSeconds(1);
                Assert.True(l.Acquired);
                Assert.Null(l.ExistingPid);
                AssertCurrentProcessPayload(ReadLockText(lockPath), before, after);
            }
        }

        [Fact]
        public void Liveness_FileWithoutPid_IsStale_AndAcquired()
        {
            string lockPath = ExpectedLockPath();
            File.WriteAllText(lockPath, "not a lock payload", new UTF8Encoding(false));

            using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                Assert.True(l.Acquired);
                Assert.Contains("\"pid\":" + Process.GetCurrentProcess().Id + ",", ReadLockText(lockPath));
            }
        }

        [Fact]
        public void Release_DeletesFile_IsIdempotent_AndAllowsReacquire()
        {
            var first = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir);
            Assert.True(first.Acquired);
            string lockPath = first.LockPath;
            first.Dispose();
            Assert.False(File.Exists(lockPath));
            first.Dispose();

            using (var second = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                Assert.True(second.Acquired);
                Assert.Equal(lockPath, second.LockPath);
                Assert.True(File.Exists(lockPath));
            }
        }

        [Fact]
        public void BusyRefusal_KeepsExitCode17_AndHandshakeMarker()
        {
            Assert.Equal(17, Program.ExitCodeBusy);

            string program = RepoSource.Read("src", "GxMcp.Worker", "Program.cs");
            int refusal = program.IndexOf("if (!InstanceLock.Acquired)", StringComparison.Ordinal);
            Assert.True(refusal > 0, "Program.cs no longer checks InstanceLock.Acquired");
            int marker = program.IndexOf("\"WORKER_HANDSHAKE_REJECT_BUSY pid=\" + existing", refusal, StringComparison.Ordinal);
            int exit = program.IndexOf("Environment.Exit(ExitCodeBusy);", refusal, StringComparison.Ordinal);
            Assert.True(marker > refusal, "Busy handshake marker missing after the refusal check");
            Assert.True(exit > marker, "Busy exit must follow the handshake marker");
        }
    }
}
