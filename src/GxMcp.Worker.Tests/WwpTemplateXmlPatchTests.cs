using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The decision a TemplateXml patch takes for a batch, run through the production function on the XML the call reads.
    public sealed class WwpTemplateXmlPatchTests
    {
        private const string Xml = "<selection Name=\"SynthTemplate\" instanceType=\"WebPanel\">"
            + "<table name=\"TableMain\" class=\"SynthOld\" /><table name=\"TableSide\" class=\"SynthOld\" /></selection>";

        private static readonly Guid OtherObjectType = new Guid("3f0c2a5e-9b1d-4c7a-8e61-0d2b4f9a7c15");

        private static WwpTemplateXmlPatch.Outcome Apply(JObject batch, string current = Xml)
            => WwpTemplateXmlPatch.Apply(WwpTemplateXml.ObjectType, batch, () => current);

        private static JObject Batch(string find, string replace, bool? replaceAll = null, int? expectedCount = null)
        {
            var patch = new JObject { ["find"] = find, ["replace"] = replace };
            if (replaceAll.HasValue) patch["replaceAll"] = replaceAll.Value;
            if (expectedCount.HasValue) patch["expectedCount"] = expectedCount.Value;
            return new JObject { ["TemplateXml"] = patch };
        }

        [Fact]
        public void OneMatchIsReplacedAndSummarised()
        {
            var outcome = Apply(Batch("name=\"TableMain\" class=\"SynthOld\"", "name=\"TableMain\" class=\"SynthNew\""));

            Assert.Null(outcome.RefusalCode);
            string patched = outcome.Properties["TemplateXml"]?.ToString() ?? string.Empty;
            Assert.Contains("name=\"TableMain\" class=\"SynthNew\"", patched);
            Assert.Contains("name=\"TableSide\" class=\"SynthOld\"", patched);
            var summary = outcome.Patches["TemplateXml"];
            Assert.Equal(1, summary?["occurrences"]?.Value<int>());
            Assert.Equal(1, summary?["replaced"]?.Value<int>());
            Assert.Equal(Xml.Length, summary?["beforeLength"]?.Value<int>());
            Assert.Equal(patched.Length, summary?["afterLength"]?.Value<int>());
            Assert.Contains("class=\"SynthNew\"", summary?["context"]?.ToString());
            Assert.False(outcome.Unchanged);
        }

        [Fact]
        public void AFindWithNoMatchIsRefusedWithTheReadHintAndNextStep()
        {
            var outcome = Apply(Batch("class=\"SynthAbsent\"", "class=\"SynthNew\""));

            Assert.Equal("NoMatch", outcome.RefusalCode);
            Assert.Equal("read_template_xml", outcome.RefusalExtra?["suggested_next_step"]?["action"]?.ToString());
            Assert.Contains("genexus_properties action=get propertyName=TemplateXml", outcome.RefusalHint);
        }

        [Fact]
        public void TwoMatchesWithoutReplaceAllAreAmbiguousAndNothingIsReplaced()
        {
            var outcome = Apply(Batch("class=\"SynthOld\"", "class=\"SynthNew\""));

            Assert.Equal("Ambiguous", outcome.RefusalCode);
            Assert.Equal("The find text matched 2 location(s) but expectedCount is 1. Nothing was written.", outcome.RefusalMessage);
            Assert.Contains("replaceAll=true", outcome.RefusalHint);
        }

        [Fact]
        public void ReplaceAllReplacesEveryMatch()
        {
            var outcome = Apply(Batch("class=\"SynthOld\"", "class=\"SynthNew\"", replaceAll: true));

            Assert.Null(outcome.RefusalCode);
            string patched = outcome.Properties["TemplateXml"]?.ToString() ?? string.Empty;
            Assert.Equal(0, CountOf(patched, "SynthOld"));
            Assert.Equal(2, CountOf(patched, "SynthNew"));
            Assert.Equal(2, outcome.Patches["TemplateXml"]?["occurrences"]?.Value<int>());
        }

        [Fact]
        public void ReplaceAllChecksExpectedCountWhenItIsSent()
        {
            var outcome = Apply(Batch("class=\"SynthOld\"", "class=\"SynthNew\"", replaceAll: true, expectedCount: 3));

            Assert.Equal("Ambiguous", outcome.RefusalCode);
            Assert.Equal("The find text matched 2 location(s) but expectedCount is 3. Nothing was written.", outcome.RefusalMessage);
        }

        [Fact]
        public void ReplaceAllWithTheExpectedCountReplacesEveryMatch()
        {
            var outcome = Apply(Batch("class=\"SynthOld\"", "class=\"SynthNew\"", replaceAll: true, expectedCount: 2));

            Assert.Null(outcome.RefusalCode);
            Assert.Equal(2, CountOf(outcome.Properties["TemplateXml"]?.ToString() ?? string.Empty, "SynthNew"));
        }

        [Fact]
        public void AnExpectedCountOfTwoWithoutReplaceAllReplacesTheTwoMatches()
        {
            var outcome = Apply(Batch("class=\"SynthOld\"", "class=\"SynthNew\"", expectedCount: 2));

            Assert.Null(outcome.RefusalCode);
            Assert.Equal(2, CountOf(outcome.Properties["TemplateXml"]?.ToString() ?? string.Empty, "SynthNew"));
        }

        [Fact]
        public void AnEmptyReplaceDeletesTheFindText()
        {
            var outcome = Apply(Batch("<table name=\"TableSide\" class=\"SynthOld\" />", ""));

            Assert.Null(outcome.RefusalCode);
            Assert.Equal("<selection Name=\"SynthTemplate\" instanceType=\"WebPanel\"><table name=\"TableMain\" class=\"SynthOld\" /></selection>",
                outcome.Properties["TemplateXml"]?.ToString());
        }

        [Fact]
        public void ReplacingTheTextWithItselfChangesNothingAndIsUnchanged()
        {
            var outcome = Apply(Batch("name=\"TableMain\"", "name=\"TableMain\""));

            Assert.Null(outcome.RefusalCode);
            Assert.True(outcome.Unchanged);
            Assert.Equal(1, outcome.Patches["TemplateXml"]?["occurrences"]?.Value<int>());
        }

        [Fact]
        public void AMixedBatchWithAChangeIsNotUnchangedEvenWhenItsPatchChangesNothing()
        {
            var batch = Batch("name=\"TableMain\"", "name=\"TableMain\"");
            batch["TemplateCategory"] = "SynthCategory";

            var outcome = Apply(batch);

            Assert.Null(outcome.RefusalCode);
            Assert.False(outcome.Unchanged);
            Assert.Equal("SynthCategory", outcome.Properties["TemplateCategory"]?.ToString());
        }

        [Fact]
        public void TheMatchIsByteForByteWithNoCultureRules()
        {
            // A soft hyphen is ignored by culture-sensitive comparison, so "ab" would match "a­b" there.
            var outcome = Apply(Batch("ab", "x"), current: "<selection Name=\"a­b\" />");

            Assert.Equal("NoMatch", outcome.RefusalCode);
        }

        [Theory]
        [InlineData("{\"find\": 1, \"replace\": \"x\"}")]
        [InlineData("{\"find\": \"class\"}")]
        [InlineData("{\"find\": \"class\", \"replace\": \"klass\", \"replaceAll\": \"yes\"}")]
        [InlineData("{\"find\": \"class\", \"replace\": \"klass\", \"expectedCount\": 0}")]
        [InlineData("{\"find\": \"class\", \"replace\": \"klass\", \"expectedCount\": \"1\"}")]
        [InlineData("{\"find\": \"class\", \"replace\": \"klass\", \"expectedCount\": 1.5}")]
        [InlineData("{\"find\": \"class\", \"replace\": \"klass\", \"replaceall\": true}")]
        public void AMalformedPatchIsRefusedBeforeAnyMatch(string patchJson)
        {
            var outcome = Apply(JObject.Parse("{\"TemplateXml\": " + patchJson + "}"));

            Assert.Equal("InvalidPropertyBatch", outcome.RefusalCode);
            Assert.StartsWith("The TemplateXml patch is not valid: ", outcome.RefusalMessage);
        }

        [Fact]
        public void AnEmptyFindIsRefusedBecauseItMatchesEverywhere()
        {
            var outcome = Apply(Batch("", "x"));

            Assert.Equal("InvalidPropertyBatch", outcome.RefusalCode);
            Assert.Equal("The TemplateXml patch is not valid: find must not be empty.", outcome.RefusalMessage);
        }

        [Fact]
        public void AMissingReplaceIsRefusedSoAPatchNeverDeletesByAccident()
        {
            var outcome = Apply(new JObject { ["TemplateXml"] = new JObject { ["find"] = "class" } });

            Assert.Equal("InvalidPropertyBatch", outcome.RefusalCode);
            Assert.Equal("The TemplateXml patch is not valid: replace must be a string; use \"\" to delete the find text.", outcome.RefusalMessage);
        }

        [Fact]
        public void TheAliasAndTheSdkNameOfTheXmlInOneBatchAreRefused()
        {
            var batch = Batch("class=\"SynthOld\"", "class=\"SynthNew\"");
            batch["WWPTemplate_TemplateXml"] = "<selection />";

            var outcome = Apply(batch);

            Assert.Equal("PropertyAliasConflict", outcome.RefusalCode);
        }

        [Fact]
        public void AXmlThatCannotBeReadRefusesThePatchInsteadOfMatchingNothing()
        {
            var outcome = WwpTemplateXmlPatch.Apply(WwpTemplateXml.ObjectType, Batch("class", "klass"), () => null);

            Assert.Equal("FreshReadUnavailable", outcome.RefusalCode);
            Assert.DoesNotContain("retry", outcome.RefusalHint);
            Assert.Contains("action=get propertyName=TemplateXml", outcome.RefusalHint);
        }

        [Fact]
        public void TheStoredXmlIsReadOnceAndOnlyForABatchWithAPatch()
        {
            int reads = 0;
            var batch = Batch("name=\"TableMain\"", "name=\"TableMain\" id=\"1\"");
            batch["TemplateCategory"] = "SynthCategory";

            WwpTemplateXmlPatch.Apply(WwpTemplateXml.ObjectType, batch, () => { reads++; return Xml; });
            Assert.Equal(1, reads);

            reads = 0;
            WwpTemplateXmlPatch.Apply(WwpTemplateXml.ObjectType, new JObject { ["TemplateCategory"] = "SynthCategory" },
                () => { reads++; return Xml; });
            Assert.Equal(0, reads);
        }

        [Theory]
        [InlineData("TemplateXml", true)]
        [InlineData("WWPTemplate_TemplateXml", true)]
        [InlineData(" templatexml ", true)]
        [InlineData("wwptemplate_templatexml", true)]
        [InlineData(" WWPTemplate_TemplateXml ", true)]
        [InlineData("TemplateCategory", false)]
        public void OnlyTheTemplateXmlOfTheTemplateTypeTakesAPatch(string name, bool expected)
        {
            Assert.Equal(expected, WwpTemplateXmlPatch.IsPatch(WwpTemplateXml.ObjectType, name, new JObject()));
        }

        [Fact]
        public void AnotherObjectTypeKeepsItsObjectValueAndIsNotRead()
        {
            int reads = 0;

            var outcome = WwpTemplateXmlPatch.Apply(OtherObjectType, Batch("class", "klass"), () => { reads++; return Xml; });

            Assert.Null(outcome.RefusalCode);
            Assert.Equal(0, reads);
            Assert.Empty(outcome.Patches.Properties());
            Assert.True(outcome.Properties["TemplateXml"] is JObject);
            Assert.False(WwpTemplateXmlPatch.IsPatch(OtherObjectType, "WWPTemplate_TemplateXml", new JObject()));
        }

        [Fact]
        public void TextOnTheTemplateXmlIsNotAPatch()
        {
            Assert.False(WwpTemplateXmlPatch.IsPatch(WwpTemplateXml.ObjectType, "TemplateXml", JValue.CreateString("<view />")));
        }

        [Fact]
        public void OmitDropsThePatchedNamesFromAResponse()
        {
            var batch = new JObject
            {
                ["TemplateXml"] = new JObject { ["find"] = "a", ["replace"] = "b" },
                ["TemplateCategory"] = "SynthCategory"
            };
            var patches = new JObject { ["TemplateXml"] = new JObject() };

            var kept = WwpTemplateXmlPatch.Omit(batch, patches);

            Assert.Equal(new[] { "TemplateCategory" }, kept.Properties().Select(p => p.Name));
        }

        [Fact]
        public void TheContextSnippetIsBoundedAroundTheReplacement()
        {
            var outcome = Apply(Batch("name=\"TableMain\"", new string('x', 500)));

            string context = outcome.Patches["TemplateXml"]?["context"]?.ToString() ?? string.Empty;
            Assert.True(context.Length <= 200, context.Length.ToString());
            Assert.Contains("xxxx", context);
        }

        [Fact]
        public void NullOptionalKeysCountAsAbsent()
        {
            var patch = new JObject
            {
                ["find"] = "name=\"TableMain\" class=\"SynthOld\"",
                ["replace"] = "name=\"TableMain\" class=\"SynthNew\"",
                ["replaceAll"] = JValue.CreateNull(),
                ["expectedCount"] = JValue.CreateNull()
            };

            var outcome = Apply(new JObject { ["TemplateXml"] = patch });

            Assert.Null(outcome.RefusalCode);
            Assert.Equal(1, outcome.Patches["TemplateXml"]?["occurrences"]?.Value<int>());
        }

        [Fact]
        public void OccurrencesOfAnEmptyFindIsEmpty()
        {
            Assert.Empty(WwpTemplateXmlPatch.Occurrences(Xml, string.Empty));
        }

        [Fact]
        public void TheReceiptNamesTheCheckedValuesAndLeavesOutAPatch()
        {
            var receipt = WwpTemplateXmlPatch.VerifiedReceipt(new[]
            {
                ("TemplateCategory", "SynthOld", "SynthCategory", "SynthCategory"),
                ("TemplateXml", "<old />", "<new />", "<new />")
            }, new JObject { ["TemplateXml"] = new JObject() });

            Assert.Equal(new[] { "TemplateCategory" }, Names(receipt["before"]));
            Assert.Equal("SynthOld", receipt["before"]?["TemplateCategory"]?.ToString());
            Assert.Equal(new[] { "TemplateCategory" }, Names(receipt["requested"]));
            Assert.Equal(new[] { "TemplateCategory" }, Names(receipt["persisted"]));
            Assert.True(receipt["persistedVerified"]?.Value<bool>());
        }

        [Fact]
        public void TheReceiptOfAPlainWriteNamesEveryCheckedProperty()
        {
            var receipt = WwpTemplateXmlPatch.VerifiedReceipt(new[]
            {
                ("TemplateCategory", "SynthOld", "SynthCategory", "SynthCategory"),
                ("TemplateCategoryOrder", "1", "2", "2")
            }, new JObject());

            Assert.Equal(new[] { "TemplateCategory", "TemplateCategoryOrder" }, Names(receipt["persisted"]));
        }

        private static string[] Names(JToken block) => (block as JObject)?.Properties().Select(p => p.Name).ToArray() ?? Array.Empty<string>();

        private static int CountOf(string text, string value)
        {
            int count = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
