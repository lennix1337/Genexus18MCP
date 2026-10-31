using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The receipts a template write returns: the single-set receipt and the nativeNames of a batch. The decision that
    // fills them is tested in WwpTemplateWiringTests; these tests read the shape the caller receives.
    public sealed class WwpTemplateReceiptTests
    {
        private static readonly Guid OtherObjectType = new Guid("3f0c2a5e-9b1d-4c7a-8e61-0d2b4f9a7c15");

        [Fact]
        public void ASingleSetOfAnAliasReportsTheSdkNameBothAsNativeNameAndInNativeNames()
        {
            JObject receipt = PropertyService.SingleSetReceipt("TemplateType", "WWPTemplate_Type", "Trn.View");

            Assert.Equal("TemplateType", receipt["property"]?.ToString());
            Assert.Equal("Trn.View", receipt["value"]?.ToString());
            Assert.Equal("WWPTemplate_Type", receipt["nativeName"]?.ToString());
            Assert.Equal("WWPTemplate_Type", receipt["nativeNames"]?["TemplateType"]?.ToString());
        }

        [Fact]
        public void ASingleSetOfAnSdkNameReportsNoNativeNames()
        {
            JObject receipt = PropertyService.SingleSetReceipt("WWPTemplate_Type", "WWPTemplate_Type", "Trn.View");

            Assert.Equal("WWPTemplate_Type", receipt["property"]?.ToString());
            Assert.Null(receipt["nativeName"]);
            Assert.Null(receipt["nativeNames"]);
        }

        [Fact]
        public void ABatchReportsTheSdkNameOfEachAliasUnderTheNameAsked()
        {
            var properties = new JObject { ["TemplateType"] = "Trn.View", ["Description"] = "x" };
            var nativeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TemplateType"] = "WWPTemplate_Type",
                ["Description"] = "Description"
            };
            var result = new JObject();

            PropertyService.NoteNativeNames(result, WwpTemplateXml.ObjectType, properties, nativeNames);

            Assert.Equal("WWPTemplate_Type", result["nativeNames"]?["TemplateType"]?.ToString());
            Assert.Null(result["nativeNames"]?["Description"]);
        }

        [Fact]
        public void ABatchOfAnotherObjectTypeReportsNoNativeNames()
        {
            var properties = new JObject { ["TemplateType"] = "Trn.View" };
            var nativeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TemplateType"] = "WWPTemplate_Type" };
            var result = new JObject();

            PropertyService.NoteNativeNames(result, OtherObjectType, properties, nativeNames);

            Assert.Null(result["nativeNames"]);
        }

        [Fact]
        public void ABatchWithoutAliasesReportsNoNativeNames()
        {
            var properties = new JObject { ["WWPTemplate_Type"] = "Trn.View" };
            var nativeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WWPTemplate_Type"] = "WWPTemplate_Type" };
            var result = new JObject();

            PropertyService.NoteNativeNames(result, WwpTemplateXml.ObjectType, properties, nativeNames);

            Assert.Null(result["nativeNames"]);
        }
    }
}
