using System;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// genexus_create refuses a WorkWithPlus template by name. These pin the predicate that decides the refusal, the hint
    /// that sends the caller to the IDE, the atomic spec refusal in every mode, and the supported-type list against the SDK
    /// lookup. The two create methods are exercised for real in ObjectServiceCreateEnvelopeTests.
    /// </summary>
    public class ObjectCreateTypesTests
    {
        [Theory]
        [InlineData("WorkWithPlus for Web Template")]
        [InlineData("WorkWithPlusforWebTemplate")]
        [InlineData("workwithplus for web template")]
        [InlineData("WorkWithPlus Template")]
        [InlineData("  WorkWithPlus   for Web Template  ")]
        [InlineData("WorkWithPlus for Web Template")]
        [InlineData("WorkWithPlus\tfor Web Template")]
        [InlineData("Work With\tPlus Template")]
        [InlineData("WorkWithPlus_for_Web_Template")]
        [InlineData("WorkWithPlus for Smart Devices Template")]
        [InlineData("WWPTemplate")]
        [InlineData("WWP Template")]
        [InlineData("DVelop.Extensions.WWPPackagesCommon.Model.WWPTemplate")]
        public void WorkWithPlusTemplate_IsRefusedUnderEverySpelling(string type)
        {
            Assert.True(ObjectCreateTypes.IsWorkWithPlusTemplate(type));
        }

        [Theory]
        [InlineData("WorkWithPlus")]
        [InlineData("WorkWithPlusDS")]
        [InlineData("WorkWithWeb")]
        [InlineData("WebPanel")]
        [InlineData("DesignSystem")]
        [InlineData("Template")]
        [InlineData("WWP")]
        [InlineData("   ")]
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

            Assert.Contains("WorkWithPlus Settings Editor", hint);
            Assert.Contains("genexus_create neither creates nor updates it", hint);
            Assert.EndsWith(ObjectCreateTypes.SupportedTypesHint, hint, StringComparison.Ordinal);
            Assert.StartsWith("Supported types: Transaction, Procedure, WebPanel", ObjectCreateTypes.SupportedTypesHint, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("create")]
        [InlineData("update")]
        [InlineData("auto")]
        public void AtomicSpec_RefusesTheTemplateInEveryMode(string mode)
        {
            AtomicCreateService.ParsedSpec spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "WorkWithPlus for Web Template",
                ["name"] = "SynthTemplate",
                ["mode"] = mode,
            });

            JObject typeError = TypeErrorOf(spec);
            Assert.NotNull(typeError);
            Assert.Equal(ObjectCreateTypes.WorkWithPlusTemplateHint, (string)typeError["errors"][0]);
        }

        [Theory]
        [InlineData("create")]
        [InlineData("update")]
        [InlineData("auto")]
        public void AtomicSpec_DoesNotRefuseAnOrdinaryTypeInAnyMode(string mode)
        {
            AtomicCreateService.ParsedSpec spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "SynthProcedure",
                ["mode"] = mode,
            });

            Assert.Null(TypeErrorOf(spec));
        }

        [Fact]
        public void SupportedTypes_CoverTheResolverTable_AndEveryOtherNameResolvesThroughObjClass()
        {
            // Loading the SDK assembly first lets the ObjClass lookup see it. The typed descriptors themselves need a
            // live SDK state, which a unit test does not have, so only their names are pinned here.
            _ = typeof(Artech.Genexus.Common.Objects.Procedure);
            string[] listed = ObjectCreateTypes.SupportedTypes.Split(new[] { ", " }, StringSplitOptions.None);

            var missingFromList = ObjectService.TypedDescriptorIds.Keys
                .Where(name => !listed.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(missingFromList);

            var unresolved = listed
                .Where(name => !ObjectService.TypedDescriptorIds.ContainsKey(name))
                .Where(name => ObjectService.ResolveObjectTypeGuid(name) == Guid.Empty)
                .ToList();
            Assert.Empty(unresolved);
        }

        [Fact]
        public void SupportedTypes_NamesEveryTypeTheSchemaNames_AndNoneTwice()
        {
            // The genexus_create schema names these types in its summary and in its type property.
            string[] schemaNames = { "Transaction", "Procedure", "WebPanel", "SDT", "API", "Domain", "DesignSystem", "Dashboard", "Folder", "Module" };
            string[] listed = ObjectCreateTypes.SupportedTypes.Split(new[] { ", " }, StringSplitOptions.None);

            Assert.Empty(schemaNames.Except(listed));
            Assert.Equal(listed.Length, listed.Distinct().Count());
        }

        private static JObject TypeErrorOf(AtomicCreateService.ParsedSpec spec)
        {
            return spec.Errors.OfType<JObject>().FirstOrDefault(e => (string)e["field"] == "type");
        }
    }
}
