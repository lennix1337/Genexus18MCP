using System.Threading;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WorkerCancellationRegistryNestableTests
    {
        public WorkerCancellationRegistryNestableTests() { WorkerCancellationRegistry.Reset(); }

        [Fact]
        public void Register_NullToken_ReturnsNoneAndIsNoop()
        {
            using (WorkerCancellationRegistry.Register(null, out var ct))
            {
                Assert.Equal(CancellationToken.None, ct);
            }
            Assert.Equal(0, WorkerCancellationRegistry.ActiveCount);
        }

        [Fact]
        public void Cancel_BeforeAnyRegister_ReturnsFalse()
        {
            Assert.False(WorkerCancellationRegistry.Cancel("unknown"));
        }

        [Fact]
        public void Cancel_BeforeRegister_CancelsTheNextRegistration()
        {
            const string cancellationId = "pre-cancelled" + "-job";
            Assert.False(WorkerCancellationRegistry.Cancel(cancellationId));
            using (WorkerCancellationRegistry.Register(cancellationId, out var ct))
            {
                Assert.True(ct.IsCancellationRequested);
            }
            Assert.Equal(0, WorkerCancellationRegistry.ActiveCount);
            WorkerCancellationRegistry.Reset();
        }

        [Fact]
        public void Register_Then_Cancel_SignalsToken()
        {
            using (WorkerCancellationRegistry.Register("job-1", out var ct))
            {
                Assert.False(ct.IsCancellationRequested);
                Assert.True(WorkerCancellationRegistry.Cancel("job-1"));
                Assert.True(ct.IsCancellationRequested);
            }
        }

        [Fact]
        public void NestedRegister_SharesCts_OuterCancelStillReachableAfterInnerDispose()
        {
            // This is the v2.6.2 (Item B) regression: an inner Register must not strip the
            // outer registration when disposed first. Otherwise the gateway's blanket
            // dispatcher-level Register becomes unreachable for any handler-level Register
            // that happens to wrap a sub-call.
            CancellationToken outerCt;
            using (WorkerCancellationRegistry.Register("job-2", out outerCt))
            {
                using (WorkerCancellationRegistry.Register("job-2", out var innerCt))
                {
                    Assert.Equal(outerCt, innerCt);
                }
                Assert.Equal(1, WorkerCancellationRegistry.ActiveCount);
                Assert.True(WorkerCancellationRegistry.Cancel("job-2"));
                Assert.True(outerCt.IsCancellationRequested);
            }
            Assert.Equal(0, WorkerCancellationRegistry.ActiveCount);
        }

        [Fact]
        public void Register_OutermostDispose_RemovesEntry()
        {
            using (WorkerCancellationRegistry.Register("job-3", out _))
            {
                Assert.Equal(1, WorkerCancellationRegistry.ActiveCount);
            }
            Assert.Equal(0, WorkerCancellationRegistry.ActiveCount);
            Assert.False(WorkerCancellationRegistry.Cancel("job-3"));
        }
    }
}
