#nullable enable

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GxMcp.Common.SharedHost
{
    // Available_Physical_Memory through GlobalMemoryStatusEx (ullAvailPhys). No performance counters: their
    // first read is slow, and the admission check reads memory last, right before a Worker start.
    internal static class MachineMemory
    {
        private const long BytesPerMb = 1024L * 1024L;

        internal static bool TryGetAvailablePhysicalMb(out long mb, out string? error)
        {
            mb = 0;
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref status))
            {
                var code = Marshal.GetLastWin32Error();
                error = "GlobalMemoryStatusEx failed: win32 error " + code.ToString(CultureInfo.InvariantCulture) +
                    " (" + new Win32Exception(code).Message + ")";
                return false;
            }

            mb = (long)(status.AvailPhys / (ulong)BytesPerMb);
            error = null;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            internal uint Length;
            internal uint MemoryLoad;
            internal ulong TotalPhys;
            internal ulong AvailPhys;
            internal ulong TotalPageFile;
            internal ulong AvailPageFile;
            internal ulong TotalVirtual;
            internal ulong AvailVirtual;
            internal ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
