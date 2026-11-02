using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The sentence a rollback says when its compared parts match their snapshot. It names the parts that were restored but not
    /// compared, and it never claims that the whole object was compared.
    /// </summary>
    public class PropertyRollbackMessageTests
    {
        [Fact]
        public void AVerifiedRollbackWithoutExclusionsSaysOnlyWhatWasCompared()
        {
            Assert.Equal(" The compared parts were restored and verified.", PropertyRollbackRestore.DescribeVerified(new string[0]));
        }

        [Fact]
        public void AVerifiedRollbackNamesThePartsItDidNotCompare()
        {
            string sentence = PropertyRollbackRestore.DescribeVerified(new[] { "WebForm", "Documentation" });

            Assert.Equal(" The compared parts were restored and verified; WebForm, Documentation were restored but not compared.", sentence);
            Assert.DoesNotContain("complete object", sentence);
        }

        [Fact]
        public void TheProductionMessageNoLongerClaimsACompleteObjectComparison()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs");

            Assert.Contains("PropertyRollbackRestore.DescribeVerified(restoredComparison?.NotComparedParts)", src);
            Assert.DoesNotContain("The complete object snapshot was restored and verified.", src);
        }
    }
}
