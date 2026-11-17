using System;
using Artech.Architecture.Common.Objects;
using Artech.Udm.Framework;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WorkerMemoryMaintenanceTests
    {
        private sealed class FakeCacheConfig : IKBModelObjectsCacheConfiguration
        {
            public bool ThrowOnReset { get; set; }
            public int ResetCount { get; private set; }

            public void ResetCaches()
            {
                if (ThrowOnReset) throw new InvalidOperationException("simulated reset failure");
                ResetCount++;
            }

            public void RemoveFromCaches(KBObject obj) { }
            public void Invalidate(KBObject obj) { }
            public KBObject GetFromCache(EntityKey key) => null;
        }

        [Fact]
        public void ResetSdkObjectCaches_CallsResetCachesWhenSupported()
        {
            var fake = new FakeCacheConfig();
            Assert.True(WorkerMemoryMaintenance.ResetSdkObjectCaches(fake));
            Assert.Equal(1, fake.ResetCount);
        }

        [Fact]
        public void ResetSdkObjectCaches_ReturnsFalseForNullOrUnsupported()
        {
            Assert.False(WorkerMemoryMaintenance.ResetSdkObjectCaches(null));
            Assert.False(WorkerMemoryMaintenance.ResetSdkObjectCaches(new object()));
        }

        [Fact]
        public void ResetSdkObjectCaches_SwallowsExceptionAndReturnsFalse()
        {
            var fake = new FakeCacheConfig { ThrowOnReset = true };
            Assert.False(WorkerMemoryMaintenance.ResetSdkObjectCaches(fake));
            Assert.Equal(0, fake.ResetCount);
        }
    }
}
