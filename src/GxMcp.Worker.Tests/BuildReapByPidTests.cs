using System;
using System.Diagnostics;
using System.Management;
using System.Threading;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Plan 012 — ReapByPidIfAlive is the PID-based replacement for dereferencing
    /// the already-disposed Process instance in BuildService's guaranteed-cleanup
    /// finally block. These cover the no-op paths and the real reap path.
    /// </summary>
    [Trait("Category", "ProcessSmoke")]
    public class BuildReapByPidTests
    {
        [Fact]
        public void ReapByPidIfAlive_ZeroPid_NoThrowNoOp()
        {
            var svc = new BuildService();
            var ex = Record.Exception(() => svc.ReapByPidIfAlive(0, DateTime.MinValue));
            Assert.Null(ex);
        }

        [Fact]
        public void ReapByPidIfAlive_DeadPid_NoThrowNoOp()
        {
            var svc = new BuildService();
            // A PID that's virtually guaranteed not to be a running process on the
            // test box. Process.GetProcessById throws ArgumentException for it,
            // which ReapByPidIfAlive swallows as a no-op.
            int deadPid = int.MaxValue - 1;
            var ex = Record.Exception(() => svc.ReapByPidIfAlive(deadPid, DateTime.MinValue));
            Assert.Null(ex);
        }

        [Fact]
        public void ReapByPidIfAlive_LiveProcess_KillsIt()
        {
            var svc = new BuildService();
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping 127.0.0.1 -n 30 >nul",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process process = null;
            try
            {
                process = Process.Start(psi);
                Assert.NotNull(process);
                int pid = process.Id;
                DateTime start;
                try { start = process.StartTime; } catch { start = DateTime.MinValue; }

                svc.ReapByPidIfAlive(pid, start);

                bool exited = process.WaitForExit(5000);
                Assert.True(exited, "process should have been reaped within 5s");
            }
            finally
            {
                try
                {
                    if (process != null && !process.HasExited) process.Kill();
                }
                catch { /* best-effort cleanup */ }
                process?.Dispose();
            }
        }

        // #830 item 3b: a failed MSBuild.exe can exit on its own while its /m worker nodes keep running and
        // hold the KB. Their WMI ParentProcessId still names the exited root, so the reaper finds them through
        // the root's Process object (still open, so its PID cannot be recycled).
        [Fact]
        public void KillOrphanedChildren_RootExitedWithLiveChild_KillsTheOrphanedChild()
        {
            using var root = StartRootThatLeavesChildRunning(out int rootPid, out DateTime rootStart);
            int childPid = WaitForChildOf(rootPid, "PING.EXE");
            Assert.NotEqual(0, childPid);
            using var child = Process.GetProcessById(childPid);
            try
            {
                int killed = BuildService.KillOrphanedChildren(root, rootStart);

                Assert.True(killed >= 1, "at least the orphaned ping should be killed, got " + killed);
                Assert.True(child.WaitForExit(5000), "the orphaned child should be reaped with its exited root");
            }
            finally
            {
                KillQuietly(child);
            }
        }

        // Without the root's start time the children cannot be bound to our build, so none are touched.
        [Fact]
        public void KillOrphanedChildren_RootStartUnknown_LeavesChildrenAlone()
        {
            using var root = StartRootThatLeavesChildRunning(out int rootPid, out _);
            int childPid = WaitForChildOf(rootPid, "PING.EXE");
            Assert.NotEqual(0, childPid);
            using var child = Process.GetProcessById(childPid);
            try
            {
                int killed = BuildService.KillOrphanedChildren(root, DateTime.MinValue);

                Assert.Equal(0, killed);
                Assert.False(child.WaitForExit(1000), "a child of a root with an unknown start must not be killed");
            }
            finally
            {
                KillQuietly(child);
            }
        }

        // A root that is still running is the job of KillProcessTree / ReapByPidIfAlive, not this reaper.
        [Fact]
        public void KillOrphanedChildren_RootStillRunning_LeavesChildrenAlone()
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 60 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var root = Process.Start(psi);
            Assert.NotNull(root);
            DateTime rootStart = root.StartTime;
            int childPid = WaitForChildOf(root.Id, "PING.EXE");
            Assert.NotEqual(0, childPid);
            using var child = Process.GetProcessById(childPid);
            try
            {
                int killed = BuildService.KillOrphanedChildren(root, rootStart);

                Assert.Equal(0, killed);
                Assert.False(child.WaitForExit(1000), "a running root's children belong to its live tree, not this reaper");
            }
            finally
            {
                KillQuietly(child);
                KillQuietly(root);
            }
        }

        // cmd.exe starts a long ping through `start /B` and exits at once, leaving the ping running with the
        // exited cmd as its parent: the same shape as an MSBuild worker node outliving a failed build.
        private static Process StartRootThatLeavesChildRunning(out int rootPid, out DateTime rootStart)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c start \"\" /B ping 127.0.0.1 -n 60 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var root = Process.Start(psi);
            Assert.NotNull(root);
            rootPid = root.Id;
            rootStart = root.StartTime;
            Assert.True(root.WaitForExit(10000), "cmd.exe should exit after starting the child");
            Assert.NotEqual(DateTime.MinValue, rootStart);
            return root;
        }

        private static int WaitForChildOf(int parentPid, string imageName)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + parentPid
                    + " AND Name = '" + imageName + "'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        using (mo) return Convert.ToInt32(mo["ProcessId"]);
                    }
                }
                Thread.Sleep(100);
            }
            return 0;
        }

        private static void KillQuietly(Process process)
        {
            try { if (!process.HasExited) process.Kill(); } catch { /* already gone */ }
        }
    }
}
