using System;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ObjectServiceDeleteRefusalTests
    {
        private static ObjectService NewService()
        {
            return new ObjectService(new KbService(new IndexCacheService()), new BuildService());
        }

        [Theory]
        [InlineData("WorkWithPlus for Web Template")]
        [InlineData("workwithplus for web template")]
        [InlineData("WorkWithPlusforWebTemplate")]
        [InlineData("WORKWITHPLUS FOR WEB TEMPLATE")]
        [InlineData(" WorkWithPlus For Web Template ")]
        [InlineData("WorkWithPlus\tfor Web  Template")]
        public void WorkWithPlus_template_type_is_refused_whatever_the_spelling(string typeName)
        {
            Assert.True(ObjectService.IsDeleteRefusedForType(typeName));
        }

        [Theory]
        [InlineData("Transaction")]
        [InlineData("WebPanel")]
        [InlineData("WorkWithPlus for Web Panel")]
        [InlineData("WorkWithPlus for Web Template 2")]
        [InlineData("")]
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
        [InlineData("WorkWithPlus for Web Template", true, false)]
        [InlineData("WorkWithPlusforWebTemplate", false, true)]
        [InlineData("workwithplus for web template", true, true)]
        public void Named_template_is_refused_before_the_kb_is_read(string typeName, bool dryRun, bool confirm)
        {
            // If the resolution ran, it would throw: the refusal must come before it.
            var service = NewService();
            service.SetDeleteTargetResolverForTest((target, typeFilter, dry, sure) =>
                throw new InvalidOperationException("the KB must not be read for a named template"));

            var json = JObject.Parse(service.DeleteObject("SynthTemplate", typeName, confirm, dryRun));

            Assert.Equal("DeleteNotSupportedForType", json["error"]?["code"]?.ToString());
            Assert.False(json["wouldDelete"]?.ToObject<bool>());
        }

        [Theory]
        [InlineData("WorkWithPlus for Web Template", true, false)]
        [InlineData("WorkWithPlusforWebTemplate", false, true)]
        [InlineData("workwithplus for web template", true, true)]
        public void Template_found_without_a_type_filter_is_refused_after_resolution(string sdkTypeName, bool dryRun, bool confirm)
        {
            // The resolution stands in for the KB and reports the object as the SDK names its type. Nothing reads an SDK
            // object before the refusal, so none is supplied.
            var service = NewService();
            service.SetDeleteTargetResolverForTest((target, typeFilter, dry, sure) => new ObjectService.DeleteResolution
            {
                Found = true,
                Name = target,
                TypeName = sdkTypeName,
                Guid = Guid.NewGuid()
            });

            var json = JObject.Parse(service.DeleteObject("SynthTemplate", null, confirm, dryRun));

            Assert.Equal("DeleteNotSupportedForType", json["error"]?["code"]?.ToString());
            Assert.Equal("SynthTemplate", json["target"]?.ToString());
            Assert.False(json["wouldDelete"]?.ToObject<bool>());
            Assert.False(json["persisted"]?.ToObject<bool>());
        }

        [Fact]
        public void Found_object_of_another_type_is_not_refused_after_resolution()
        {
            var service = NewService();
            service.SetDeleteTargetResolverForTest((target, typeFilter, dry, sure) => new ObjectService.DeleteResolution
            {
                Found = true,
                Name = target,
                TypeName = "Transaction",
                Guid = Guid.NewGuid()
            });

            // The flow goes on past the refusal point, with no SDK object to act on; the only claim here is that no
            // refusal is produced for a type that is not a template.
            string json = service.DeleteObject("SynthTransaction", null, confirm: true);

            Assert.DoesNotContain("DeleteNotSupportedForType", json);
        }

        [Fact]
        public void Template_delete_is_refused_before_the_kb_is_read_with_the_real_resolution()
        {
            var service = NewService();

            var json = JObject.Parse(service.DeleteObject("SynthTemplate", "WorkWithPlus for Web Template", confirm: false, dryRun: true));

            Assert.Equal("DeleteNotSupportedForType", json["error"]?["code"]?.ToString());
            Assert.False(json["wouldDelete"]?.ToObject<bool>());
        }

        [Fact]
        public void Other_types_still_reach_the_kb_check()
        {
            var service = NewService();

            string json = service.DeleteObject("SynthTemplate", "Transaction", confirm: false, dryRun: true);

            Assert.Contains("KbNotOpen", json);
        }
    }
}
