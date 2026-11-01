using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The proof that each refusal of a write gives the receipt. The production paths call these functions, so a change in the mapping
    // changes what the receipt says about the persisted value.
    public sealed class PropertyProofMappingTests
    {
        [Fact]
        public void AReadBackThatFoundAnotherValueProvesTheRequestedValueIsNotInTheObject()
        {
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.NotPersisted,
                PropertyRollbackRestore.ProofOfVerificationRefusal("PropertyNotPersisted"));
        }

        [Theory]
        [InlineData("PropertyVerificationUnavailable")]
        [InlineData("PropertyWriteFailed")]
        [InlineData("")]
        [InlineData(null)]
        public void AnyOtherRefusalLeavesTheStateUnknown(string code)
        {
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.Unknown, PropertyRollbackRestore.ProofOfVerificationRefusal(code));
        }

        [Fact]
        public void AReadThatSaysTheValueIsNotThereProvesIt()
        {
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.NotPersisted, PropertyRollbackRestore.ProofOfUnconfirmedRead(false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(null)]
        public void AReadThatConfirmedOrGaveNoAnswerLeavesTheStateUnknown(bool? persistedVerified)
        {
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.Unknown, PropertyRollbackRestore.ProofOfUnconfirmedRead(persistedVerified));
        }

        [Fact]
        public void AChangedUnrelatedPartAfterEveryValueWasReadBackLeavesTheValuesInTheObject()
        {
            Assert.Equal(PropertyRollbackRestore.PersistenceProof.Verified, PropertyRollbackRestore.ProofOfChangedParts());
        }
    }
}
