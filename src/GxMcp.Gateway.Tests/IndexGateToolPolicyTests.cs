using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class IndexGateToolPolicyTests
    {
        [Theory]
        [InlineData("genexus_list_objects")]
        [InlineData("genexus_query")]
        [InlineData("genexus_read")]
        [InlineData("genexus_search_source")]
        [InlineData("genexus_analyze")]
        [InlineData("genexus_navigation")]
        public void IndexDependentReads_AreBlockedUntilIndexIsUsable(string toolName)
        {
            Assert.True(Program.IsIndexDependentToolForTest(toolName));
        }

        [Theory]
        [InlineData("genexus_edit")]
        [InlineData("genexus_edit_form")]
        [InlineData("genexus_edit_and_build")]
        [InlineData("genexus_create_object")]
        [InlineData("genexus_create_popup")]
        [InlineData("genexus_apply_pattern")]
        public void Mutations_RemainAvailableWhileIndexBuilds(string toolName)
        {
            Assert.False(Program.IsIndexDependentToolForTest(toolName));
        }

        [Fact]
        public void IndexNotReadyResponses_AreNeverSemanticCached()
        {
            Assert.True(Program.IsTransientResponseForCacheForTest(
                JObject.Parse("{'status':'Indexing','code':'IndexNotReady'}")));
        }
    }
}