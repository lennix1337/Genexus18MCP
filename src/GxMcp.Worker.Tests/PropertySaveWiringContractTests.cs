using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Every save of a property write, and the rollback of one, runs with the pattern-on-save suppression its write decides.
    /// The save calls need the SDK, so these tests pin the wiring in the source: each call site keeps its decision, the rollback
    /// keeps its verification and its reset, and a reset is chosen only for a value written to the object itself.
    /// </summary>
    public class PropertySaveWiringContractTests
    {
        private static string PropertySource()
            => RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"));

        [Fact]
        public void EveryPropertySaveDecidesItsSuppressionThroughThePropertyRule()
        {
            // The single write, the batch write, the reference write and the output SDT write decide with Begin; the rollback
            // decides with BeginForRollback.
            Assert.Equal(4, SourceAssert.Count(PropertySource(), "suppression = PropertyApplyOnSaveRule.Begin("));
            Assert.Equal(1, SourceAssert.Count(PropertySource(), "suppression = PropertyApplyOnSaveRule.BeginForRollback("));
        }

        [Fact]
        public void TheRollbackVerifiesWithThePartsComparisonAndResetsWithTheSdkReset()
        {
            string src = PropertySource();

            Assert.Equal(1, SourceAssert.Count(src, "snapshot.ComparePropertyVerification(restored)"));
            Assert.Equal(1, SourceAssert.Count(src, "ResetOrWriteBack(rollback.RequestedNames"));
            Assert.Equal(1, SourceAssert.Count(src, "CanResetObjectProperty(current, rollback.NativeNames[name])"));
            Assert.Equal(1, SourceAssert.Count(src, "((dynamic)obj).CanResetValue(nativeName)"));
            Assert.Equal(0, SourceAssert.Count(src, "CanResetProperty"));
        }

        [Fact]
        public void AnUnconfirmedRollbackLeavesItsResidueForTheLaterWritesToReport()
        {
            string src = PropertySource();

            // The refused rollback, the unverified rollback and the failed rollback each keep the state before the write.
            Assert.Equal(3, SourceAssert.Count(src, "_residue.Remember(ObjectKey(written), snapshot)"));
            // A later write reports its preserved parts against that first state, and names the residue.
            Assert.Equal(1, SourceAssert.Count(src, "_residue.TryGetOriginal(residueKey, out ObjectMoveSnapshot original)"));
            Assert.Equal(1, SourceAssert.Count(src, "PropertyResidue.PreservedNames("));
        }

        [Fact]
        public void AResetIsChosenOnlyForAValueWrittenToTheObjectItself()
        {
            Assert.Equal(1, SourceAssert.Count(PropertySource(), "ReferenceEquals(propertyContainer, obj) && IsPropertyDefaultSafe(obj, nativeName)"));
        }
    }
}
