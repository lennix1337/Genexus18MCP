using System;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// genexus_create refuses a WorkWithPlus template by name, before the type lookup, and the refusal sends the caller
    /// to the GeneXus IDE. The predicate and the hint are checked directly. The source-order guards keep both create
    /// paths refusing the template before the lookup, because the lookup itself needs an open Knowledge Base.
    /// </summary>
    public class ObjectCreateTypesTests
    {
        [Theory]
        [InlineData("WorkWithPlus for Web Template")]
        [InlineData("WorkWithPlusforWebTemplate")]
        [InlineData("workwithplus for web template")]
        [InlineData("WorkWithPlus Template")]
        [InlineData("  WorkWithPlus   for Web Template  ")]
        public void WorkWithPlusTemplate_IsRefusedUnderEverySpelling(string type)
        {
            Assert.True(ObjectCreateTypes.IsWorkWithPlusTemplate(type));
        }

        [Theory]
        [InlineData("WorkWithPlus")]
        [InlineData("WorkWithWeb")]
        [InlineData("WebPanel")]
        [InlineData("DesignSystem")]
        [InlineData("Template")]
        [InlineData("")]
        [InlineData(null)]
        public void OtherTypes_AreNotRefusedAsTemplates(string type)
        {
            Assert.False(ObjectCreateTypes.IsWorkWithPlusTemplate(type));
        }

        [Fact]
        public void RefusalHint_SendsTheCallerToTheIde_AndNamesTheSupportedTypes()
        {
            string hint = ObjectCreateTypes.WorkWithPlusTemplateHint;

            Assert.Contains("GeneXus IDE", hint);
            Assert.Contains(ObjectCreateTypes.SupportedTypesHint, hint);
            Assert.StartsWith("Supported types: Transaction, Procedure, WebPanel", ObjectCreateTypes.SupportedTypesHint, StringComparison.Ordinal);
        }

        [Fact]
        public void CreateObject_RefusesTheTemplateBeforeTheTypeLookup()
        {
            string body = SourceAssert.MethodBody(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs"),
                "public string CreateObject(string type, string name, JObject options)");

            AssertRefusalComesBeforeLookup(body);
        }

        [Fact]
        public void CreateObjectInstance_RefusesTheTemplateBeforeTheTypeLookup()
        {
            // The atomic create path reaches the same template through this method.
            string body = SourceAssert.MethodBody(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs"),
                "public KBObject CreateObjectInstance(");

            AssertRefusalComesBeforeLookup(body);
        }

        private static void AssertRefusalComesBeforeLookup(string body)
        {
            int refusal = body.IndexOf("ObjectCreateTypes.IsWorkWithPlusTemplate(type)", StringComparison.Ordinal);
            int lookup = body.IndexOf("ResolveObjectTypeGuid(type)", StringComparison.Ordinal);

            Assert.True(refusal >= 0, "The WorkWithPlus template refusal is missing.");
            Assert.True(lookup < 0 || refusal < lookup, "The template refusal must come before the SDK type lookup.");
        }
    }
}
