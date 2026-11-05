#nullable enable

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GxMcp.Common.SharedHost
{
    // Production IProcessProbe: PID plus process start time through System.Diagnostics.Process, and the process
    // image path through QueryFullProcessImageName, which works across bitness (x64 Gateway, x86 Worker).
    internal sealed class ProcessIdentity : IProcessProbe
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInsufficientBuffer = 122;
        private const int MaxImagePathChars = 32_768;

        public ProcessProbeResult Probe(int pid)
        {
            if (pid <= 0)
            {
                return ProcessProbeResult.NotRunning;
            }

            Process process;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return ProcessProbeResult.NotRunning;
            }
            catch (InvalidOperationException)
            {
                return ProcessProbeResult.NotRunning;
            }

            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        return ProcessProbeResult.NotRunning;
                    }
                }
                catch (InvalidOperationException)
                {
                    return ProcessProbeResult.NotRunning;
                }
                catch (Win32Exception)
                {
                    // Access denied for the exit check; the start time read below decides.
                }
                catch (NotSupportedException)
                {
                    // Not a local process handle; the start time read below decides.
                }

                try
                {
                    return ProcessProbeResult.Running(process.StartTime.ToUniversalTime().Ticks);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the lookup and the start time read.
                    return ProcessProbeResult.NotRunning;
                }
                catch (Win32Exception)
                {
                    return ProcessProbeResult.RunningStartTimeUnreadable;
                }
                catch (NotSupportedException)
                {
                    return ProcessProbeResult.RunningStartTimeUnreadable;
                }
            }
        }

        internal static bool TryGetImagePath(int pid, out string? imagePath, out string? error)
        {
            imagePath = null;
            if (pid <= 0)
            {
                error = "pid must be positive, got " + pid.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            using (var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, pid))
            {
                if (handle.IsInvalid)
                {
                    error = "OpenProcess failed for pid " + pid.ToString(CultureInfo.InvariantCulture) + ": " +
                        DescribeWin32Error(Marshal.GetLastWin32Error());
                    return false;
                }

                var capacity = 1024;
                while (true)
                {
                    var buffer = new StringBuilder(capacity);
                    var size = capacity;
                    if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                    {
                        imagePath = buffer.ToString(0, size);
                        error = null;
                        return true;
                    }

                    var lastError = Marshal.GetLastWin32Error();
                    if (lastError == ErrorInsufficientBuffer && capacity < MaxImagePathChars)
                    {
                        capacity = Math.Min(capacity * 4, MaxImagePathChars);
                        continue;
                    }

                    error = "QueryFullProcessImageName failed for pid " + pid.ToString(CultureInfo.InvariantCulture) +
                        ": " + DescribeWin32Error(lastError);
                    return false;
                }
            }
        }

        private static string DescribeWin32Error(int code) =>
            "win32 error " + code.ToString(CultureInfo.InvariantCulture) + " (" + new Win32Exception(code).Message + ")";

        private sealed class ProcessQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private ProcessQueryHandle() : base(ownsHandle: true)
            {
            }

            protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern ProcessQueryHandle OpenProcess(uint desiredAccess,
                [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool QueryFullProcessImageName(ProcessQueryHandle process, uint flags,
                StringBuilder exeName, ref int size);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr handle);
        }
    }
}
