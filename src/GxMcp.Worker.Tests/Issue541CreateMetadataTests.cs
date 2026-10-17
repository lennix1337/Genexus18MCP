using System;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #541: genexus_create type=Attribute must apply dataType/length/decimals/basedOn
    /// or refuse before Save(), and the SDT seed must report the size actually kept. These
    /// drive the pure plan, the fail-closed seam (with fakes standing in for the SDK) and
    /// the seed-type resolver without a Knowledge Base.
    /// </summary>
    public class Issue541CreateMetadataTests
    {
        public sealed class FakeDomain
        {
            public string Name { get; set; }
        }

        // Writable surface that keeps what it is given (the happy SDK).
        public sealed class WritableAttribute
        {
            public string Type { get; set; } = "NUMERIC";
            public int Length { get; set; } = 4;
            public int Decimals { get; set; }
            public object DomainBasedOn { get; set; }
        }

        // Setters return normally but keep the SDK default — the silent drop the issue reports.
        public sealed class StubbornAttribute
        {
            public string Type { get { return "NUMERIC"; } set { } }
            public int Length { get { return 4; } set { } }
            public int Decimals { get { return 0; } set { } }
            public object DomainBasedOn { get { return null; } set { } }
        }

        [Fact]
        public void Plan_OmitsDataType_WhenOnlyObjectTypeIsPresent()
        {
            // options["type"] is the OBJECT type; it must never become the data type.
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["type"] = "Attribute", ["name"] = "X" });

            Assert.False(plan.Requested);
            Assert.Null(plan.Error);
            Assert.Null(plan.ToJson()["dataType"]);
            Assert.Null(ObjectService.ApplyAttributeCreatePlan(new WritableAttribute(), plan, _ => null));
        }

        [Fact]
        public void Plan_VarCharWithLengthOverride()
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["type"] = "Attribute", ["dataType"] = "VarChar", ["length"] = 100 });

            Assert.True(plan.Requested);
            Assert.Null(plan.Error);
            Assert.Equal("VarChar", plan.CanonicalType);
            Assert.Equal(100, plan.Length);
            Assert.Null(plan.Decimals);
            Assert.Equal("VarChar", plan.ToJson()["dataType"]?.ToString());
            Assert.Equal(100, (int)plan.ToJson()["length"]);
        }

        [Fact]
        public void Plan_NumericWithInlineLengthDecimals()
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["dataType"] = "Numeric(12,2)" });

            Assert.Equal("Numeric", plan.CanonicalType);
            Assert.Equal(12, plan.Length);
            Assert.Equal(2, plan.Decimals);
        }

        [Fact]
        public void Plan_Blob_MapsToBinaryOnReadBack()
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["dataType"] = "Blob" });

            Assert.Equal("Blob", plan.CanonicalType);
            Assert.Null(plan.Length);
            Assert.Empty(plan.Mismatches(new JObject { ["dataType"] = "BINARY", ["length"] = 4 }));
            Assert.Contains(plan.Mismatches(new JObject { ["dataType"] = "NUMERIC", ["length"] = 4 }), m => m.StartsWith("dataType:"));
        }

        [Fact]
        public void Plan_BasedOn_IsDomainPlan()
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["basedOn"] = "Email" });

            Assert.True(plan.IsDomain);
            Assert.Equal("Email", plan.DomainName);
            Assert.Equal("Email", plan.ToJson()["basedOn"]?.ToString());
            Assert.Null(plan.ToJson()["dataType"]);
        }

        [Theory]
        [InlineData("NotATypeAtAll(1,2)")]
        [InlineData("(40)")]
        public void Plan_UnknownDataType_ReportsError(string dataType)
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["dataType"] = dataType });

            Assert.True(plan.Requested);
            Assert.NotNull(plan.Error);
            Assert.Contains(dataType, plan.Error);
        }

        [Fact]
        public void Plan_LengthWithoutDataType_ReportsError()
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["type"] = "Attribute", ["length"] = 10 });

            Assert.True(plan.Requested);
            Assert.Contains("require dataType", plan.Error);
        }

        [Fact]
        public void Plan_BasedOnWithDataType_ReportsError()
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["dataType"] = "VarChar", ["basedOn"] = "Email" });

            Assert.Contains("mutually exclusive", plan.Error);
        }

        [Fact]
        public void Apply_PrimitiveLandsOnWritableAttribute_ReportsApplied()
        {
            var attr = new WritableAttribute();
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["dataType"] = "VarChar", ["length"] = 100 });

            JObject meta = ObjectService.ApplyAttributeCreatePlan(attr, plan, _ => null);

            Assert.Equal("VARCHAR", attr.Type);
            Assert.Equal(100, attr.Length);
            Assert.Equal("VarChar", meta["requested"]?["dataType"]?.ToString());
            Assert.Equal("VARCHAR", meta["applied"]?["dataType"]?.ToString());
            Assert.Equal(100, (int)meta["applied"]["length"]);
        }

        [Fact]
        public void Apply_RefusesWhenPrimitiveDoesNotStick()
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["dataType"] = "VarChar", ["length"] = 100 });

            var ex = Assert.Throws<AttributeCreateMetadataException>(
                () => ObjectService.ApplyAttributeCreatePlan(new StubbornAttribute(), plan, _ => null));

            Assert.Equal(AttributeCreatePlan.NotAppliedCode, ex.Code);
            Assert.Contains("dataType: requested VarChar (VARCHAR), applied NUMERIC", ex.Message);
            Assert.Contains("length: requested 100, applied 4", ex.Message);
            Assert.Equal("NUMERIC", ex.Details["applied"]?["dataType"]?.ToString());
        }

        [Fact]
        public void Apply_RefusesInvalidPlan_BeforeTouchingTheAttribute()
        {
            var attr = new WritableAttribute();
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["type"] = "Attribute", ["length"] = 10 });

            var ex = Assert.Throws<AttributeCreateMetadataException>(
                () => ObjectService.ApplyAttributeCreatePlan(attr, plan, _ => null));

            Assert.Equal(AttributeCreatePlan.NotAppliedCode, ex.Code);
            Assert.Contains("require dataType", ex.Message);
            Assert.Equal("NUMERIC", attr.Type);
            Assert.Equal(4, attr.Length);
        }

        [Fact]
        public void Apply_UnknownDomain_IsNamedRefusal()
        {
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["basedOn"] = "Missing" });

            var ex = Assert.Throws<AttributeCreateMetadataException>(
                () => ObjectService.ApplyAttributeCreatePlan(new WritableAttribute(), plan, _ => null));

            Assert.Equal(AttributeCreatePlan.DomainNotFoundCode, ex.Code);
            Assert.Contains("'Missing'", ex.Message);
        }

        [Fact]
        public void Apply_DomainLandsAndIsVerified()
        {
            var attr = new WritableAttribute();
            var plan = AttributeCreatePlan.FromOptions(new JObject { ["basedOn"] = "Email" });

            JObject meta = ObjectService.ApplyAttributeCreatePlan(attr, plan,
                name => string.Equals(name, "Email", StringComparison.Ordinal) ? new FakeDomain { Name = "Email" } : null);

            Assert.Equal("Email", Assert.IsType<FakeDomain>(attr.DomainBasedOn).Name);
            Assert.Equal("Email", meta["applied"]?["basedOn"]?.ToString());

            var stubborn = Assert.Throws<AttributeCreateMetadataException>(
                () => ObjectService.ApplyAttributeCreatePlan(new StubbornAttribute(), plan, _ => new FakeDomain { Name = "Email" }));
            Assert.Equal(AttributeCreatePlan.NotAppliedCode, stubborn.Code);
            Assert.Contains("basedOn: requested 'Email', applied '(none)'", stubborn.Message);
        }

        [Theory]
        [InlineData("Numeric(7)", "NUMERIC", 7, null)]
        [InlineData("Numeric(12,2)", "NUMERIC", 12, 2)]
        [InlineData("VarChar(100)", "VARCHAR", 100, null)]
        [InlineData("Character(20)", "CHARACTER", 20, null)]
        [InlineData("Blob", "BINARY", null, null)]
        [InlineData("VARCHAR", "VARCHAR", null, null)]
        [InlineData("NotAType!", "VARCHAR", null, null)]
        public void ResolveSeedItemType_MapsTypeAndInlineSize(string requested, string edb, int? length, int? decimals)
        {
            string resolved = ObjectService.ResolveSeedItemType(requested, out int? len, out int? dec);

            Assert.Equal(edb, resolved);
            Assert.Equal(length, len);
            Assert.Equal(decimals, dec);
        }

        [Fact]
        public void SeedItemSize_ReportsOnlyWhatTheItemKept()
        {
            var writable = new WritableAttribute();
            ObjectService.ApplySeedItemSize(writable, 7, null, out int? len, out int? dec);
            Assert.Equal(7, writable.Length);
            Assert.Equal("NUMERIC(7)", ObjectService.DescribeSeedItemType("NUMERIC", len, dec));

            ObjectService.ApplySeedItemSize(writable, 12, 2, out len, out dec);
            Assert.Equal("NUMERIC(12,2)", ObjectService.DescribeSeedItemType("NUMERIC", len, dec));

            // The SDK kept its own value: the description says 4, never the requested 7.
            ObjectService.ApplySeedItemSize(new StubbornAttribute(), 7, null, out len, out dec);
            Assert.Equal("NUMERIC(4)", ObjectService.DescribeSeedItemType("NUMERIC", len, dec));

            // No Length surface at all: no size is claimed.
            ObjectService.ApplySeedItemSize(new object(), 7, null, out len, out dec);
            Assert.Equal("NUMERIC", ObjectService.DescribeSeedItemType("NUMERIC", len, dec));
        }

        // Native proof on GX18 U16: the Attribute branch resolved basedOn with
        // kb.DesignModel.Objects.GetByName(...)?.OfType<Domain>() on the dynamic KB handle.
        // A LINQ extension cannot bind dynamically ('object' does not contain a definition
        // for 'OfType'), so every existing Domain came back DomainNotFound — and, before the
        // fail-closed seam, silently became NUMERIC(4). The lookup must go through the typed
        // Domain.ResolveName wrapper the variable writers already use. Source guard: the
        // project cannot instantiate a KnowledgeBase handle to exercise the lambda itself.
        [Fact]
        public void AttributeCreate_ResolvesBasedOnDomainThroughTheTypedSdkResolver()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");
            string body = SourceAssert.MethodBody(source, "public KBObject CreateObjectInstance(");

            Assert.Contains("ApplyAttributeCreatePlan(attrObj, attrPlan, domainName =>", body);
            Assert.Contains("VariableInjector.ResolveDomain(kb.DesignModel, domainName)", body);
            Assert.DoesNotContain(".OfType<Artech.Genexus.Common.Objects.Domain>()", body);
        }
    }
}
