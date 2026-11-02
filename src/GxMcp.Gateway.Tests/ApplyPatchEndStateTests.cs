using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // The validate poll stops only on a worker end state. Queued and Running are waiting states: a build that has not
    // started yet is not a failed verdict, and a poll that stops on it reports a validation nobody ran.
    public class ApplyPatchEndStateTests
    {
        [Theory]
        [InlineData("Succeeded")]
        [InlineData("succeeded")]
        [InlineData("Failed")]
        [InlineData("Cancelled")]
        [InlineData("Error")]
        [InlineData("error")]
        [InlineData("ReorgRequired")]
        public void WorkerEndStates_StopTheWait(string status)
        {
            Assert.True(ApplyPatchValidation.IsEndState(status));
        }

        [Theory]
        [InlineData("Queued")]
        [InlineData("Running")]
        [InlineData("")]
        [InlineData(null)]
        public void WaitingAndMissingStates_KeepTheWait(string? status)
        {
            Assert.False(ApplyPatchValidation.IsEndState(status));
        }
    }
}
