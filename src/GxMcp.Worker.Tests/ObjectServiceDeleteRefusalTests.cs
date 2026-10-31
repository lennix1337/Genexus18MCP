using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ObjectServiceDeleteRefusalTests
    {
        [Theory]
        [InlineData("WorkWithPlus for Web Template")]
        [InlineData("workwithplus for web template")]
        public void WorkWithPlus_template_type_is_refused_whatever_the_case(string typeName)
        {
            Assert.True(ObjectService.IsDeleteRefusedForType(typeName));
        }

        [Theory]
        [InlineData("Transaction")]
        [InlineData("WebPanel")]
        [InlineData("WorkWithPlus for Web Panel")]
        [InlineData(null)]
        public void Other_types_are_not_refused(string typeName)
        {
            Assert.False(ObjectService.IsDeleteRefusedForType(typeName));
        }

        [Fact]
        public void Refusal_envelope_is_an_error_that_says_nothing_was_deleted()
        {
            var json = JObject.Parse(ObjectService.DeleteNotSupportedForTypeResponse("SynthTemplate", "WorkWithPlus for Web Template"));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("DeleteNotSupportedForType", json["error"]?["code"]?.ToString());
            Assert.Equal("SynthTemplate", json["target"]?.ToString());
            Assert.False(json["wouldDelete"]?.ToObject<bool>());
            Assert.False(json["persisted"]?.ToObject<bool>());
            Assert.Contains("Nothing was deleted", json["error"]?["message"]?.ToString());
            Assert.Contains("Settings Editor", json["error"]?["hint"]?.ToString());
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Template_delete_is_refused_before_the_kb_is_read(bool dryRun, bool confirm)
        {
            var service = new ObjectService(new KbService(new IndexCacheService()), new BuildService());

            var json = JObject.Parse(service.DeleteObject("SynthTemplate", "WorkWithPlus for Web Template", confirm, dryRun));

            Assert.Equal("DeleteNotSupportedForType", json["error"]?["code"]?.ToString());
            Assert.False(json["wouldDelete"]?.ToObject<bool>());
        }

        [Fact]
        public void Other_types_still_reach_the_kb_check()
        {
            var service = new ObjectService(new KbService(new IndexCacheService()), new BuildService());

            string json = service.DeleteObject("SynthTemplate", "Transaction", confirm: false, dryRun: true);

            Assert.Contains("KbNotOpen", json);
        }
    }
}
