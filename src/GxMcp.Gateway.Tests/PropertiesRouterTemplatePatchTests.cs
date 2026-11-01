using GxMcp.Gateway;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The Gateway side of a WorkWithPlus template patch: the patch reaches the worker inside the batch, a propertyName or
    // value sent with it is refused, and the help names the keys and the codes of the patch.
    public sealed class PropertiesRouterTemplatePatchTests
    {
        [Fact]
        public void ATemplateXmlPatchReachesTheWorkerInsideTheBatchUnchanged()
        {
            var routed = JObject.FromObject(new PropertiesRouter().ConvertToolCall("genexus_properties", JObject.Parse(@"{
                ""action"": ""set"", ""name"": ""SynthTemplate"", ""rollbackOnFailure"": true,
                ""properties"": { ""TemplateXml"": { ""find"": ""SynthOld"", ""replace"": ""SynthNew"", ""expectedCount"": 1 } } }"))!);

            Assert.Equal("Set", routed["action"]?.ToString());
            Assert.Equal("SynthOld", routed["properties"]?["TemplateXml"]?["find"]?.ToString());
            Assert.Equal("SynthNew", routed["properties"]?["TemplateXml"]?["replace"]?.ToString());
            Assert.Equal(1, routed["properties"]?["TemplateXml"]?["expectedCount"]?.ToObject<int>());
        }

        [Fact]
        public void APatchSentWithAPropertyNameOrAValueIsRefused()
        {
            var router = new PropertiesRouter();
            var withPropertyName = JObject.Parse(@"{ ""action"": ""set"", ""name"": ""SynthTemplate"", ""propertyName"": ""TemplateXml"",
                ""properties"": { ""TemplateXml"": { ""find"": ""SynthOld"", ""replace"": ""SynthNew"" } } }");
            var withValue = JObject.Parse(@"{ ""action"": ""set"", ""name"": ""SynthTemplate"", ""value"": ""<view />"",
                ""properties"": { ""WWPTemplate_TemplateXml"": { ""find"": ""SynthOld"", ""replace"": ""SynthNew"" } } }");

            foreach (var args in new[] { withPropertyName, withValue })
            {
                var refusal = Assert.Throws<UsageException>(() => router.ConvertToolCall("genexus_properties", args));
                Assert.Equal("InvalidPropertyBatch", refusal.Code);
            }
        }

        [Fact]
        public void AJsonNullPropertyNameIsNotSentSoThePatchIsRouted()
        {
            var routed = JObject.FromObject(new PropertiesRouter().ConvertToolCall("genexus_properties", JObject.Parse(@"{
                ""action"": ""set"", ""name"": ""SynthTemplate"", ""propertyName"": null, ""value"": null,
                ""properties"": { ""TemplateXml"": { ""find"": ""SynthOld"", ""replace"": ""SynthNew"" } } }"))!);

            Assert.Equal("SynthOld", routed["properties"]?["TemplateXml"]?["find"]?.ToString());
        }

        [Theory]
        [InlineData("\"\"")]
        [InlineData("\"   \"")]
        public void ABlankPropertyNameIsNotSentSoThePatchIsRouted(string propertyName)
        {
            var routed = JObject.FromObject(new PropertiesRouter().ConvertToolCall("genexus_properties", JObject.Parse(
                "{ \"action\": \"set\", \"name\": \"SynthTemplate\", \"propertyName\": " + propertyName + ", " +
                "\"properties\": { \"TemplateXml\": { \"find\": \"SynthOld\", \"replace\": \"SynthNew\" } } }"))!);

            Assert.Equal("SynthOld", routed["properties"]?["TemplateXml"]?["find"]?.ToString());
        }

        [Fact]
        public void AnEmptyValueSentWithAPatchIsRefused()
        {
            var args = JObject.Parse(@"{ ""action"": ""set"", ""name"": ""SynthTemplate"", ""value"": """",
                ""properties"": { ""TemplateXml"": { ""find"": ""SynthOld"", ""replace"": ""SynthNew"" } } }");

            var refusal = Assert.Throws<UsageException>(() => new PropertiesRouter().ConvertToolCall("genexus_properties", args));

            Assert.Equal("InvalidPropertyBatch", refusal.Code);
        }

        [Fact]
        public void AnObjectValueOnAnotherPropertyWithAPropertyNameIsNotRefusedAsAPatch()
        {
            var routed = JObject.FromObject(new PropertiesRouter().ConvertToolCall("genexus_properties", JObject.Parse(@"{
                ""action"": ""set"", ""name"": ""SynthObject"", ""propertyName"": ""Description"",
                ""properties"": { ""Description"": { ""find"": ""SynthOld"", ""replace"": ""SynthNew"" } } }"))!);

            Assert.Equal("SynthOld", routed["properties"]?["Description"]?["find"]?.ToString());
        }

        [Fact]
        public void ATextValueWithAPropertyNameIsNotRefusedAsAPatch()
        {
            var routed = JObject.FromObject(new PropertiesRouter().ConvertToolCall("genexus_properties", JObject.Parse(@"{
                ""action"": ""set"", ""name"": ""SynthObject"", ""propertyName"": ""Description"",
                ""properties"": { ""Description"": ""SynthText"" } }"))!);

            Assert.Equal("SynthText", routed["properties"]?["Description"]?.ToString());
        }

        [Fact]
        public void TheHelpNamesTheKeysOfTheTemplateXmlPatchAndTheCodesItReturns()
        {
            string help = ToolHelpCatalog.Get("genexus_properties") ?? string.Empty;

            foreach (string expected in new[]
            {
                "`replaceAll: true` every match is replaced", "`patch` block", "`NoMatch`", "`Ambiguous`", "`WwpTemplateInvalid`",
                "send the `versionToken` of the `dryRun`", "other properties may share the call"
            })
                Assert.Contains(expected, help);
            Assert.DoesNotContain("goes alone", help);
        }
    }
}
