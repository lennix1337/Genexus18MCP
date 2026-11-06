using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // GeneXus X Evolution 3 keeps an empty Documentation part as "<Properties />" and rewrites it on save,
    // so writing the source's empty part into the clone never reads back as written. A part the new
    // object already holds verbatim needs no write.
    public sealed class SaveAsUnchangedPartTests
    {
        private static string Read(string source) => new JObject { ["part"] = "Documentation", ["source"] = source }.ToString();

        [Fact]
        public void A_part_the_target_already_holds_verbatim_is_not_written_again()
        {
            Assert.True(SdkObjectCloner.TargetAlreadyHolds("<Properties />", Read("<Properties />")));
        }

        [Fact]
        public void A_part_that_differs_or_cannot_be_read_is_written()
        {
            Assert.False(SdkObjectCloner.TargetAlreadyHolds("<Properties><Property /></Properties>", Read("<Properties />")));
            Assert.False(SdkObjectCloner.TargetAlreadyHolds("<Properties />", "{\"status\":\"error\"}"));
            Assert.False(SdkObjectCloner.TargetAlreadyHolds("<Properties />", "not json"));
            Assert.False(SdkObjectCloner.TargetAlreadyHolds("x", new JObject { ["source"] = "x", ["truncated"] = true }.ToString()));
        }
    }
}
