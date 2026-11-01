using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The receipt of a property write whose batch transaction committed: <c>committed</c> is always true, <c>persisted</c> is a
    /// boolean that says whether the requested values are in the object, and <c>persistenceStateKnown</c> says whether a read settled
    /// it. These decisions are plain values, pinned here without a Knowledge Base. The production paths that build the receipt are
    /// exercised by PropertyRollbackProductionTests.
    /// </summary>
    public class PropertyWriteReceiptTests
    {
        [Fact]
        public void AVerifiedProofWritesPersistedTrueAndAKnownState()
        {
            var receipt = new JObject();

            PropertyRollbackRestore.MarkCommitted(receipt, PropertyRollbackRestore.PersistenceProof.Verified);

            Assert.True((bool)receipt["committed"]);
            Assert.Equal(JTokenType.Boolean, receipt["persisted"].Type);
            Assert.True((bool)receipt["persisted"]);
            Assert.True((bool)receipt["persistenceStateKnown"]);
        }

        [Fact]
        public void ANotPersistedProofWritesPersistedFalseAndAKnownState()
        {
            var receipt = new JObject();

            PropertyRollbackRestore.MarkCommitted(receipt, PropertyRollbackRestore.PersistenceProof.NotPersisted);

            Assert.False((bool)receipt["persisted"]);
            Assert.True((bool)receipt["persistenceStateKnown"]);
        }

        [Fact]
        public void AnUnknownStateWritesPersistedTrueAndAnUnsettledState()
        {
            // The value may be in the object, so persisted is true; persistenceStateKnown says that no read settled it.
            var receipt = new JObject();

            PropertyRollbackRestore.MarkCommitted(receipt, PropertyRollbackRestore.PersistenceProof.Unknown);

            Assert.Equal(JTokenType.Boolean, receipt["persisted"].Type);
            Assert.True((bool)receipt["persisted"]);
            Assert.False((bool)receipt["persistenceStateKnown"]);
        }

        [Fact]
        public void AVerifiedRollbackProvesTheRequestedValuesAreGone()
        {
            var nothingLeft = new PropertyRollbackRestore.Report(new[] { "MasterPage" }, new string[0]);

            Assert.Equal(PropertyRollbackRestore.PersistenceProof.NotPersisted,
                PropertyRollbackRestore.ProofAfterRollback(rollbackVerified: true, report: null));
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.NotPersisted,
                PropertyRollbackRestore.ProofAfterRollback(rollbackVerified: false, report: nothingLeft));
        }

        [Fact]
        public void AnUnverifiedRollbackWithAPropertyNotRestoredLeavesTheStateUnknown()
        {
            var someLeft = new PropertyRollbackRestore.Report(new string[0], new[] { "SOAP" });

            Assert.Equal(PropertyRollbackRestore.PersistenceProof.Unknown,
                PropertyRollbackRestore.ProofAfterRollback(rollbackVerified: false, report: someLeft));
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.Unknown,
                PropertyRollbackRestore.ProofAfterRollback(rollbackVerified: false, report: null));
        }

        [Fact]
        public void ARollbackThatFailsBeforeItsCommitKeepsTheProofOfTheVerificationItRanFor()
        {
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.Verified,
                PropertyRollbackRestore.ProofAfterFailedRollback(restoreCommitted: false, earlier: PropertyRollbackRestore.PersistenceProof.Verified));
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.NotPersisted,
                PropertyRollbackRestore.ProofAfterFailedRollback(restoreCommitted: false, earlier: PropertyRollbackRestore.PersistenceProof.NotPersisted));
        }

        [Fact]
        public void ARollbackThatCommittedButCouldNotBeConfirmedIsUnknownWhateverTheEarlierProof()
        {
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.Unknown,
                PropertyRollbackRestore.ProofAfterFailedRollback(restoreCommitted: true, earlier: PropertyRollbackRestore.PersistenceProof.Verified));
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.Unknown,
                PropertyRollbackRestore.ProofAfterFailedRollback(restoreCommitted: true, earlier: PropertyRollbackRestore.PersistenceProof.NotPersisted));
        }
    }
}
