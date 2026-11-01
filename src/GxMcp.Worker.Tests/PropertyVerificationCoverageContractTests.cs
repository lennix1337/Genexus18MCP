using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// What the property rollback compares: the Variables text is captured with the snapshot and compared with the state after the
    /// rollback. These checks pin that wiring in the snapshot source, since a live object is needed to run it.
    /// </summary>
    public class PropertyVerificationCoverageContractTests
    {
        private static string SnapshotSource()
            => RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "ObjectMoveSnapshot.cs"));

        [Fact]
        public void TheVariablesTextIsCapturedWithTheSnapshot()
        {
            Assert.Equal(1, SourceAssert.Count(SnapshotSource(), "snapshot.VariablesText = CaptureVariablesText(obj);"));
        }

        [Fact]
        public void TheVariablesTextIsComparedByTheVerification()
        {
            string src = SnapshotSource();

            Assert.Equal(1, SourceAssert.Count(src, "if (!string.Equals(VariablesText, current.VariablesText, StringComparison.Ordinal))"));
            Assert.Equal(1, SourceAssert.Count(src, "changed.Add(\"Variables\");"));
        }
    }
}
