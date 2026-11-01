using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The decision on the object's type and the shape of the template's reply, without the SDK: a value the SDK holds is
    // written as text, a value it does not hold is left out, and an object of another type is not a template.
    public sealed class WwpTemplateReplyTests
    {
        private static readonly string[] OfficialOrder =
        {
            "Name", "Description", "TemplateCategory", "TemplateCategoryOrder", "TemplateType",
            "TemplateXml", "MainTemplate", "InstanceType", "PreviewImage", "IsDefault"
        };

        [Fact]
        public void The_values_come_in_the_official_order_and_booleans_and_numbers_are_written_as_text()
        {
            Dictionary<string, object> values = WwpTemplateTestFixtures.SdkValues();

            JObject reply = JObject.Parse(WwpTemplateReply.Shape(WwpTemplateXml.ObjectType, "SynthTemplate", WwpTemplateTestFixtures.TemplateType,
                name => values.TryGetValue(name, out object value) ? value : null));

            Assert.Equal("SynthTemplate", (string)reply["objectName"]!);
            Assert.Equal(WwpTemplateTestFixtures.TemplateType, (string)reply["objectType"]!);
            JObject properties = (JObject)reply["properties"]!;
            Assert.Equal(OfficialOrder, properties.Properties().Select(p => p.Name).ToArray());
            Assert.Equal("False", (string)properties["IsDefault"]!);
            Assert.Equal("0", (string)properties["TemplateCategoryOrder"]!);
            Assert.Equal("SynthTemplate", (string)properties["Name"]!);
        }

        [Fact]
        public void An_object_of_another_type_is_not_a_template_whatever_its_values()
        {
            Dictionary<string, object> values = WwpTemplateTestFixtures.SdkValues();
            Func<string, object> read = name => values.TryGetValue(name, out object value) ? value : null;

            Assert.Equal(WwpTemplateReply.NotTemplateCode, CodeOf(WwpTemplateReply.Shape(Guid.NewGuid(), "SynthImage", "Image", read)));
            Assert.Equal(WwpTemplateReply.NotTemplateCode, CodeOf(WwpTemplateReply.Shape(null, "SynthImage", "Image", read)));
        }

        [Fact]
        public void A_value_the_sdk_does_not_hold_is_left_out_and_an_empty_value_is_kept()
        {
            Dictionary<string, object> values = WwpTemplateTestFixtures.SdkValues();
            values["Description"] = null;
            values["WWPTemplate_PreviewImage"] = string.Empty;

            JObject properties = (JObject)JObject.Parse(WwpTemplateReply.Shape(WwpTemplateXml.ObjectType, "SynthTemplate", WwpTemplateTestFixtures.TemplateType,
                name => values.TryGetValue(name, out object value) ? value : null))["properties"]!;

            Assert.Null(properties["Description"]);
            Assert.Equal(string.Empty, (string)properties["PreviewImage"]!);
            Assert.Equal(OfficialOrder.Where(name => name != "Description"), properties.Properties().Select(p => p.Name));
        }

        [Fact]
        public void An_unread_type_or_xml_fails_the_read_of_that_template()
        {
            Dictionary<string, object> noType = WwpTemplateTestFixtures.SdkValues();
            noType["WWPTemplate_Type"] = null;
            Dictionary<string, object> noXml = WwpTemplateTestFixtures.SdkValues();
            noXml["WWPTemplate_TemplateXml"] = null;

            Assert.Equal(WwpTemplateReply.ReadFailedCode, CodeOf(WwpTemplateReply.Shape(WwpTemplateXml.ObjectType, "SynthTemplate", WwpTemplateTestFixtures.TemplateType,
                name => noType.TryGetValue(name, out object value) ? value : null)));
            Assert.Equal(WwpTemplateReply.ReadFailedCode, CodeOf(WwpTemplateReply.Shape(WwpTemplateXml.ObjectType, "SynthTemplate", WwpTemplateTestFixtures.TemplateType,
                name => noXml.TryGetValue(name, out object value) ? value : null)));
        }

        [Fact]
        public void The_fixture_reply_is_the_one_the_production_shape_gives()
        {
            JObject reply = JObject.Parse(WwpTemplateTestFixtures.PropertiesOf("SynthTemplate"));

            Assert.Equal("SynthTemplate", (string)reply["objectName"]!);
            Assert.Equal(WwpTemplateTestFixtures.TemplateXml, (string)((JObject)reply["properties"]!)["TemplateXml"]!);
        }

        // An error reply nests its code under "error"; a plain reply has none.
        private static string CodeOf(string reply)
        {
            JObject parsed = JObject.Parse(reply);
            return (string)(parsed["error"]?["code"] ?? parsed["code"]);
        }
    }
}
