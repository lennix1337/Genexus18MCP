using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// What the property write and its rollback compare: the variables text is captured with the snapshot, and compared both after the
    /// rollback and after the write. These checks pin that wiring in the snapshot source, since a live object is needed to run it; the
    /// comparison rules themselves are run by PropertyVerificationSeamTests.
    /// </summary>
    public class PropertyVerificationCoverageContractTests
    {
        private static string SnapshotSource()
            => RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "ObjectMoveSnapshot.cs"));

        [Fact]
        public void TheVariablesTextIsCapturedWithTheSnapshot()
        {
            Assert.Equal(1, SourceAssert.Count(SnapshotSource(), "snapshot.VariablesText = VariablesFingerprint.Capture(obj);"));
        }

        [Fact]
        public void TheVariablesTextIsComparedByTheVerificationAndTheWriteCheck()
        {
            string src = SnapshotSource();

            // One comparison in the property verification and one in the write check, both guarded by the variables being readable.
            Assert.Equal(2, SourceAssert.Count(src, "if (variablesCompared && !string.Equals(VariablesText, current.VariablesText, StringComparison.Ordinal))"));
            Assert.Equal(2, SourceAssert.Count(src, "changed.Add(\"Variables\");"));
        }
    }
}
