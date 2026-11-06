using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Shared-child branch of <c>SingleInstanceLock</c> (issue #854, Requirements 7.6 and 7.10).
    /// Every test runs with <c>GXMCP_SHARED_CHILD=1</c>, which is how a Worker_Host starts its
    /// Worker child. The contract adds no public member: <c>TryAcquire</c> reads the variable
    /// when it is called, and in that mode
    /// <list type="bullet">
    /// <item>the payload is <c>{"pid":N,"startedUtc":"&lt;o&gt;","processStartUtcTicks":T,"exe":"..."}</c>,
    /// where <c>T</c> is the UTC tick count of the current process start time;</item>
    /// <item>a record is live only when its PID is running and that process started at the
    /// recorded time (millisecond precision); any other record is stale, is overwritten, and
    /// the lock is acquired;</item>
    /// <item>a live record is refused with <c>ExistingPid</c> set and the file left byte-for-byte
    /// unchanged, including after <c>Dispose</c> of the refused instance (today its final
    /// cleanup deletes any file at <c>LockPath</c> that no open handle protects).</item>
    /// </list>
    /// The child exit is pinned in <c>Program.cs</c>: the refusal and <c>Environment.Exit</c>
    /// come before the KB is opened. No dedicated decision method exists yet (task 4.13 may add
    /// one; the lock result remains the observable contract). The Isolated_Mode path is pinned
    /// separately by <see cref="SingleInstanceLockIsolatedRegressionTests"/>.
    /// </summary>
    [Collection("SingleInstanceLockEnvironment")]
    [Trait("Feature", "concurrent-multi-session-workers")]
    public sealed class SingleInstanceLockSharedChildTests : IDisposable
    {
        private const string SharedChildVariable = "GXMCP_SHARED_CHILD";
        private const string WorkerExe = @"C:\Tools\GxMcp\worker\GxMcp.Worker.exe";

        private readonly string _savedSharedChild;
        private readonly string _lockDir;
        private readonly string _kbPath;

        public SingleInstanceLockSharedChildTests()
        {
            _savedSharedChild = Environment.GetEnvironmentVariable(SharedChildVariable);
            Environment.SetEnvironmentVariable(SharedChildVariable, "1");
            _lockDir = Path.Combine(Path.GetTempPath(), "gxmcp-sil-shared-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_lockDir);
            // A unique KB path keeps the Global\ mutex name unique to this test.
            _kbPath = @"C:\KBs\KbShared-" + Guid.NewGuid().ToString("N");
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

        private static long StartUtcTicks(Process p) => p.StartTime.ToUniversalTime().Ticks;

        private static int ExitedProcessId()
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.Id;
            }
        }

        private static string Payload(int pid, long startTicks)
        {
            return "{\"pid\":" + pid +
                ",\"startedUtc\":\"2001-01-01T00:00:00.0000000Z\"" +
                ",\"processStartUtcTicks\":" + startTicks.ToString(CultureInfo.InvariantCulture) +
                ",\"exe\":\"C:\\\\Other\\\\GxMcp.Worker.exe\"}";
        }

        private static void AssertOwnSharedChildPayload(string text, DateTime notBeforeUtc, DateTime notAfterUtc)
        {
            const string startedPrefix = ",\"startedUtc\":\"";
            const string ticksPrefix = "\",\"processStartUtcTicks\":";
            const string exePrefix = ",\"exe\":\"";
            int startedAt = text.IndexOf(startedPrefix, StringComparison.Ordinal);
            int ticksAt = text.IndexOf(ticksPrefix, StringComparison.Ordinal);
            int exeAt = text.IndexOf(exePrefix, StringComparison.Ordinal);
            Assert.True(startedAt > 0 && ticksAt > startedAt && exeAt > ticksAt,
                "Shared-child payload must be pid, startedUtc, processStartUtcTicks, exe: " + text);

            string started = text.Substring(startedAt + startedPrefix.Length, ticksAt - startedAt - startedPrefix.Length);
            string ticksText = text.Substring(ticksAt + ticksPrefix.Length, exeAt - ticksAt - ticksPrefix.Length);

            using (var self = Process.GetCurrentProcess())
            {
                string exe = self.MainModule?.FileName ?? "";
                string expected = "{\"pid\":" + self.Id +
                    ",\"startedUtc\":\"" + started + "\"" +
                    ",\"processStartUtcTicks\":" + ticksText +
                    ",\"exe\":\"" + exe.Replace("\\", "\\\\") + "\"}";
                Assert.Equal(expected, text);

                long ticks = long.Parse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture);
                Assert.Equal(StartUtcTicks(self) / TimeSpan.TicksPerMillisecond, ticks / TimeSpan.TicksPerMillisecond);
            }

            // startedUtc keeps its Isolated_Mode meaning: acquisition time in "o" format.
            DateTime parsed = DateTime.ParseExact(started, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
            Assert.InRange(parsed, notBeforeUtc, notAfterUtc);
        }

        [Fact]
        public void Acquire_PayloadAddsProcessStartUtcTicks()
        {
            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                DateTime after = DateTime.UtcNow.AddSeconds(1);
                Assert.True(l.Acquired);
                Assert.Null(l.ExistingPid);
                Assert.Equal(ExpectedLockPath(), l.LockPath);
                AssertOwnSharedChildPayload(ReadLockText(l.LockPath), before, after);
            }
        }

        [Fact]
        public void StaleFile_NoRunningHolder_IsOverwritten_AndAcquired()
        {
            string lockPath = ExpectedLockPath();
            int deadPid = ExitedProcessId();
            File.WriteAllText(lockPath, Payload(deadPid, DateTime.UtcNow.Ticks), new UTF8Encoding(false));

            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                DateTime after = DateTime.UtcNow.AddSeconds(1);
                Assert.True(l.Acquired);
                Assert.Null(l.ExistingPid);
                AssertOwnSharedChildPayload(ReadLockText(lockPath), before, after);
            }
        }

        [Fact]
        public void StaleFile_PidReusedWithDifferentStartTime_IsOverwritten_AndAcquired()
        {
            string lockPath = ExpectedLockPath();
            using (var self = Process.GetCurrentProcess())
            {
                // Same running PID, but a start time this process cannot have: a reused PID.
                long wrongStart = StartUtcTicks(self) - TimeSpan.FromHours(1).Ticks;
                File.WriteAllText(lockPath, Payload(self.Id, wrongStart), new UTF8Encoding(false));
            }

            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                DateTime after = DateTime.UtcNow.AddSeconds(1);
                Assert.True(l.Acquired, "A live PID with a different start time is a stale record in shared-child mode");
                Assert.Null(l.ExistingPid);
                AssertOwnSharedChildPayload(ReadLockText(lockPath), before, after);
            }
        }

        [Fact]
        public void HeldByRunningProcess_IsRefused_AndFileIsUnchanged()
        {
            using (var holder = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
            {
                Assert.True(holder.Acquired);
                byte[] payload = ReadLockBytes(holder.LockPath);
                Assert.Contains("\"processStartUtcTicks\":", Encoding.UTF8.GetString(payload));

                var second = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir);
                Assert.False(second.Acquired);
                Assert.Equal(Process.GetCurrentProcess().Id, second.ExistingPid);
                second.Dispose();

                Assert.True(File.Exists(holder.LockPath));
                Assert.Equal(payload, ReadLockBytes(holder.LockPath));
            }
        }

        [Fact]
        public void LiveRecordOfAnotherRunningProcess_IsRefused_AndFileIsUnchanged()
        {
            string lockPath = ExpectedLockPath();
            var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true };
            using (var other = Process.Start(psi))
            {
                try
                {
                    string record = Payload(other.Id, StartUtcTicks(other));
                    byte[] bytes = new UTF8Encoding(false).GetBytes(record);
                    File.WriteAllBytes(lockPath, bytes);
                    DateTime writeTime = File.GetLastWriteTimeUtc(lockPath);

                    using (var l = SingleInstanceLock.TryAcquire(_kbPath, WorkerExe, _lockDir))
                    {
                        Assert.False(l.Acquired);
                        Assert.Equal(other.Id, l.ExistingPid);
                    }

                    Assert.Equal(bytes, File.ReadAllBytes(lockPath));
                    Assert.Equal(writeTime, File.GetLastWriteTimeUtc(lockPath));
                }
                finally
                {
                    try { other.Kill(); } catch { }
                    other.WaitForExit(5000);
                }
            }
        }

        [Fact]
        public void Program_RefusedLock_ExitsBeforeOpeningTheKb()
        {
            string program = RepoSource.Read("src", "GxMcp.Worker", "Program.cs");
            int acquire = program.IndexOf("InstanceLock = SingleInstanceLock.TryAcquire(", StringComparison.Ordinal);
            int refusal = program.IndexOf("if (!InstanceLock.Acquired)", acquire, StringComparison.Ordinal);
            int exit = program.IndexOf("Environment.Exit(ExitCodeBusy);", refusal, StringComparison.Ordinal);
            int sdkOpen = program.IndexOf("GetKbService().OpenKB(kbPath)", StringComparison.Ordinal);
            int comOpen = program.IndexOf("ComGxPublicDriver.Instance.OpenKB(kbPath", StringComparison.Ordinal);
            Assert.True(acquire > 0, "Program.cs no longer acquires SingleInstanceLock");
            Assert.True(refusal > acquire && exit > refusal, "A refused lock must exit with ExitCodeBusy");
            Assert.True(sdkOpen > exit && comOpen > exit, "The KB must not be opened before the refused-lock exit");
            // The refusal is not bypassed for shared children.
            string refusalBlock = program.Substring(acquire, exit - acquire);
            Assert.DoesNotContain("GXMCP_SHARED_CHILD", refusalBlock);
        }
    }
}
