using System;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class WwpTemplatePropertyRulesTests
    {
        private static readonly Guid TemplateObjectType = WwpTemplateXml.ObjectType;
        private static readonly Guid OtherObjectType = new Guid("3f0c2a5e-9b1d-4c7a-8e61-0d2b4f9a7c15");

        [Theory]
        [InlineData("TemplateType", "WWPTemplate_Type")]
        [InlineData("TemplateCategory", "WWPTemplate_Category")]
        [InlineData("TemplateCategoryOrder", "WWPTemplate_CategoryOrder")]
        [InlineData("TemplateXml", "WWPTemplate_TemplateXml")]
        [InlineData("MainTemplate", "WWPTemplate_MainTemplate")]
        [InlineData("InstanceType", "WWPTemplate_InstanceType")]
        [InlineData("PreviewImage", "WWPTemplate_PreviewImage")]
        public void EachOfficialNameStandsForItsSdkPropertyWhateverItsCase(string alias, string sdkName)
        {
            Assert.Equal(sdkName, WwpTemplatePropertyRules.NativeName(TemplateObjectType, alias));
            Assert.Equal(sdkName, WwpTemplatePropertyRules.NativeName(TemplateObjectType, alias.ToLowerInvariant()));
        }

        [Theory]
        [InlineData("WWPTemplate_Type")]
        [InlineData("Description")]
        [InlineData("MainProgram")]
        public void OtherNamesComeBackUnchanged(string name)
        {
            Assert.Equal(name, WwpTemplatePropertyRules.NativeName(TemplateObjectType, name));
        }

        [Theory]
        [InlineData("TemplateType")]
        [InlineData("TemplateXml")]
        [InlineData("MainTemplate")]
        public void AnAliasIsNotAnameOfAnyOtherObjectType(string alias)
        {
            Assert.Equal(alias, WwpTemplatePropertyRules.NativeName(OtherObjectType, alias));
        }

        [Fact]
        public void AnAliasAndTheSdkNameItStandsForAreOneConflict()
        {
            string message = WwpTemplatePropertyRules.FindConflict(TemplateObjectType, new[] { "TemplateType", "WWPTemplate_Type" });

            Assert.NotNull(message);
            Assert.Contains("WWPTemplate_Type", message);
        }

        [Fact]
        public void TheSameNameTwiceInAnyCaseIsOneConflict()
        {
            Assert.NotNull(WwpTemplatePropertyRules.FindConflict(TemplateObjectType, new[] { "TemplateXml", "wwptemplate_templatexml" }));
        }

        [Fact]
        public void DistinctTemplatePropertiesDoNotConflict()
        {
            Assert.Null(WwpTemplatePropertyRules.FindConflict(TemplateObjectType, new[] { "TemplateType", "TemplateXml", "Description" }));
        }

        [Fact]
        public void AnotherObjectTypeNeverConflicts()
        {
            Assert.Null(WwpTemplatePropertyRules.FindConflict(OtherObjectType, new[] { "TemplateType", "WWPTemplate_Type" }));
        }

        [Fact]
        public void ARequestedAliasReadsTheStoredPropertyUnderItsOwnName()
        {
            JObject full = StoredTemplate();

            WwpTemplatePropertyRules.AddAliasReadItems(TemplateObjectType, full, new[] { "TemplateType" });
            JObject property = JObject.Parse(PropertyService.ShapeGetPropertiesResult(full, "SynthTemplate", propertyName: "TemplateType"))
                ["result"]?["property"] as JObject;

            Assert.NotNull(property);
            Assert.Equal("TemplateType", property["name"]?.ToString());
            Assert.Equal("WWPTemplate_Type", property["nativeName"]?.ToString());
            Assert.Equal("Trn.Selection", property["value"]?.ToString());
        }

        [Fact]
        public void TheRequestedNameIsTrimmedBeforeItIsAdded()
        {
            JObject full = StoredTemplate();

            WwpTemplatePropertyRules.AddAliasReadItems(TemplateObjectType, full, new[] { "  TemplateXml " });
            JObject property = JObject.Parse(PropertyService.ShapeGetPropertiesResult(full, "SynthTemplate", propertyName: "TemplateXml"))
                ["result"]?["property"] as JObject;

            Assert.Equal("TemplateXml", property?["name"]?.ToString());
            Assert.Equal("WWPTemplate_TemplateXml", property?["nativeName"]?.ToString());
        }

        [Fact]
        public void AnAliasWithNoStoredPropertyStaysNotFound()
        {
            JObject full = new JObject { ["properties"] = new JArray() };

            WwpTemplatePropertyRules.AddAliasReadItems(TemplateObjectType, full, new[] { "TemplateType" });
            JObject json = JObject.Parse(PropertyService.ShapeGetPropertiesResult(full, "SynthTemplate", propertyName: "TemplateType"));

            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void AnAliasOnAnotherObjectTypeStaysNotFound()
        {
            JObject full = StoredTemplate();

            WwpTemplatePropertyRules.AddAliasReadItems(OtherObjectType, full, new[] { "TemplateType" });
            JObject json = JObject.Parse(PropertyService.ShapeGetPropertiesResult(full, "SynthObject", propertyName: "TemplateType"));

            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void TheSdkNameStillReadsAsItAlwaysDid()
        {
            JObject full = StoredTemplate();

            WwpTemplatePropertyRules.AddAliasReadItems(TemplateObjectType, full, new[] { "WWPTemplate_Type" });

            Assert.Equal(1, full["properties"].Count(p => string.Equals(p["name"]?.ToString(), "WWPTemplate_Type", StringComparison.Ordinal)));
            Assert.Equal(2, full["properties"].Count());
        }

        private static JObject StoredTemplate() => new JObject
        {
            ["properties"] = new JArray
            {
                new JObject { ["name"] = "WWPTemplate_Type", ["value"] = "Trn.Selection", ["type"] = "System.String", ["readOnly"] = false },
                new JObject { ["name"] = "WWPTemplate_TemplateXml", ["value"] = "<selection />", ["type"] = "System.String", ["readOnly"] = false }
            }
        };
    }
}
