using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WriteDirtyOutcomeTests
    {
        [Theory]
        [InlineData("{\"status\":\"ok\",\"code\":\"WriteApplied\",\"persisted\":true,\"changed\":true}", true)]
        [InlineData("{\"status\":\"ok\",\"code\":\"WriteNoChange\",\"persisted\":true,\"changed\":false}", false)]
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"ValidationFailed\"}}", false)]
        [InlineData("{\"status\":\"error\",\"partialPersistenceDetected\":true}", true)]
        [InlineData("{\"status\":\"error\",\"rollbackFailed\":true}", true)]
        public void DirtyClassificationUsesFinalPersistedOutcome(string response, bool expected)
        {
            Assert.Equal(expected, WriteService.ShouldMarkTargetDirty(response));
        }

        [Fact]
        public void ConfirmedRollbackDoesNotLeaveNewDirtyState()
        {
            const string response = "{\"status\":\"error\",\"rollback\":{\"rolledBack\":true,\"reReadConfirmed\":true},\"persisted\":false}";
            Assert.False(WriteService.ShouldMarkTargetDirty(response));
        }

        [Fact]
        public void InvalidResponseFailsClosedWithoutDirtyMark()
        {
            Assert.False(WriteService.ShouldMarkTargetDirty("not-json"));
        }
    }
}
