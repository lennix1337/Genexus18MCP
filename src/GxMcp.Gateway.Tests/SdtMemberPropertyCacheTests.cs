using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // get_item_property and the set_item_property preview hand out the versionToken a write has to present and the value a
    // change is judged against. A copy served from the 30-minute semantic cache would keep returning the token and the
    // "before" of an SDT edited meanwhile outside this Gateway, so they are never cached.
    public class SdtMemberPropertyCacheTests
    {
        private static JObject Args(string action, bool dryRun = false) => new JObject
        {
            ["action"] = action,
            ["name"] = "SampleResponse",
            ["itemPath"] = new JArray("debug"),
            ["property"] = "JsonNullSerialization",
            ["dryRun"] = dryRun
        };

        [Theory]
        [InlineData("get_item_property", false)]
        [InlineData("set_item_property", true)]
        public void TheReadAndThePreviewAreNeverLookedUpInOrStoredIntoTheCache(string action, bool dryRun)
        {
            var args = Args(action, dryRun);

            Assert.Null(Program.CreateSemanticCacheKey("kb1", "genexus_structure", args, false, false));
            Assert.Null(Program.CreateSemanticCacheKey("kb1", "genexus_structure", args, false, false, 1, null, null));
            Assert.True(Program.IsLiveSdtMemberProperty("genexus_structure", args));
        }

        [Fact]
        public void OtherReadsOfTheSameToolStillUseTheCache()
        {
            var args = new JObject { ["action"] = "get_visual", ["name"] = "SampleResponse" };

            Assert.NotNull(Program.CreateSemanticCacheKey("kb1", "genexus_structure", args, false, false));
            Assert.NotNull(Program.CreateSemanticCacheKey("kb1", "genexus_structure", args, false, false, 1, null, null));
            Assert.False(Program.IsLiveSdtMemberProperty("genexus_structure", args));
        }

        [Fact]
        public void OnlyTheStructureToolIsAffected()
        {
            Assert.False(Program.IsLiveSdtMemberProperty("genexus_properties", Args("get_item_property")));
            Assert.False(Program.IsLiveSdtMemberProperty("genexus_structure", null));
        }
    }
}
