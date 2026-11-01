using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The receipt of a property write whose batch transaction committed. Once the commit is in, an error says so, and its
    /// <c>persisted</c> value says whether the requested values are still in the object: a boolean when a read settled it,
    /// <c>"unknown"</c> when none could. These are plain decisions, so they are pinned here without a Knowledge Base.
    /// </summary>
    public class PropertyWriteReceiptTests
    {
        [Fact]
        public void MarkCommittedSaysTheBatchCommittedAndKeepsThePersistedValueAsAJsonBoolean()
        {
            var receipt = new JObject();

            PropertyRollbackRestore.MarkCommitted(receipt, true);

            Assert.True((bool)receipt["committed"]);
            Assert.Equal(JTokenType.Boolean, receipt["persisted"].Type);
            Assert.True((bool)receipt["persisted"]);
        }

        [Fact]
        public void AVerifiedRollbackAfterAFailedVerificationSaysTheValuesAreNotPersisted()
        {
            object persisted = PropertyRollbackRestore.PersistedAfterVerificationFailure(rollbackVerified: true);

            Assert.Equal(false, persisted);
        }

        [Fact]
        public void AnUnverifiedRollbackAfterAFailedVerificationSaysUnknown()
        {
            object persisted = PropertyRollbackRestore.PersistedAfterVerificationFailure(rollbackVerified: false);

            Assert.Equal("unknown", persisted);
        }

        [Fact]
        public void ARollbackThatNeverCommittedLeavesTheValuesPersisted()
        {
            object persisted = PropertyRollbackRestore.PersistedAfterRollbackFailure(restoreCommitted: false);

            Assert.Equal(true, persisted);
        }

        [Fact]
        public void ARollbackThatCommittedButCouldNotBeConfirmedSaysUnknown()
        {
            object persisted = PropertyRollbackRestore.PersistedAfterRollbackFailure(restoreCommitted: true);

            Assert.Equal("unknown", persisted);
        }

        [Fact]
        public void TheUnknownValueIsTheStringTheReceiptCarries()
        {
            var receipt = new JObject();

            PropertyRollbackRestore.MarkCommitted(receipt, PropertyRollbackRestore.UnknownPersisted);

            Assert.Equal(JTokenType.String, receipt["persisted"].Type);
            Assert.Equal("unknown", (string)receipt["persisted"]);
        }
    }
}
