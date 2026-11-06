using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Unit test for MachineMemory, the available physical memory reading.
    // Requirements 6.1, 6.10 (memory reading).
    public class MachineMemoryTests
    {
        [Fact]
        public void MachineMemory_TryGetAvailablePhysicalMb_ReturnsPositiveValueOrNamedError()
        {
            var ok = MachineMemory.TryGetAvailablePhysicalMb(out var mb, out var error);

            if (ok)
            {
                Assert.True(mb > 0, "available physical memory must be positive, got " + mb);
                Assert.Null(error);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(error));
            }
        }
    }
}
