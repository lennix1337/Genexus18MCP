using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // #961: a WWP write that failed and rolled back exactly reported persisted:false next to
    // partialPersistenceDetected:true. Partial persistence is only real while the state is not restored.
    public class WwpFailedWritePartialPersistenceTests
    {
        [Theory]
        [InlineData(true, "{exact:true}", false)]
        [InlineData(true, "{exact:false}", true)]
        [InlineData(true, "{performed:false,skipped:true,exact:false}", true)]
        [InlineData(true, "{}", true)]
        [InlineData(false, "{exact:false}", false)]
        [InlineData(false, "{exact:true}", false)]
        public void OnlyAnUnrestoredWriteThatStartedPersistingIsPartial(bool started, string rollback, bool expected)
        {
            Assert.Equal(expected, WwpActionService.PartialPersistenceAfterRollback(started, JObject.Parse(rollback)));
        }

        [Fact]
        public void AMissingRollbackReportStaysConservative()
        {
            Assert.True(WwpActionService.PartialPersistenceAfterRollback(true, null));
        }
    }
}
