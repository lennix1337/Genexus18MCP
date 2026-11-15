using System;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class VariableDomainTests
    {
        [Fact]
        public void TypeBindingEngine_ResolvesPrimitiveWithDimensions()
        {
            var engine = new TypeBindingEngine();
            var result = engine.Bind("Numeric(12, 4)");

            Assert.True(result.Success);
            Assert.Equal(VariableKind.Primitive, result.Kind);
            Assert.Equal("Numeric", result.CanonicalType);
            Assert.Equal(12, result.Length);
            Assert.Equal(4, result.Decimals);
            Assert.False(result.IsCollection);
        }

        [Fact]
        public void TypeBindingEngine_ResolvesSynonymsAndCollections()
        {
            var engine = new TypeBindingEngine();
            var result = engine.Bind("String(60)", isCollection: true);

            Assert.True(result.Success);
            Assert.Equal(VariableKind.Primitive, result.Kind);
            Assert.Equal("Character", result.CanonicalType);
            Assert.Equal(60, result.Length);
            Assert.True(result.IsCollection);
        }

        [Fact]
        public void TypeBindingEngine_CategorizesStructuredAndDomainTypes()
        {
            var engine = new TypeBindingEngine();

            var sdtResult = engine.Bind("SdtInvoiceHeader");
            Assert.True(sdtResult.Success);
            Assert.Equal(VariableKind.Sdt, sdtResult.Kind);
            Assert.Equal("SdtInvoiceHeader", sdtResult.TargetReferenceName);

            var dottedResult = engine.Bind("Invoice.LineItem");
            Assert.True(dottedResult.Success);
            Assert.Equal(VariableKind.DottedSdtItem, dottedResult.Kind);
            Assert.Equal("Invoice.LineItem", dottedResult.TargetReferenceName);

            var bcResult = engine.Bind("Customer_BC");
            Assert.True(bcResult.Success);
            Assert.Equal(VariableKind.BusinessComponent, bcResult.Kind);
            Assert.Equal("Customer_BC", bcResult.TargetReferenceName);

            var domainResult = engine.Bind("&InvoiceStatus");
            Assert.True(domainResult.Success);
            Assert.Equal(VariableKind.Domain, domainResult.Kind);
            Assert.Equal("InvoiceStatus", domainResult.TargetReferenceName);
        }

        [Fact]
        public void TypeBindingEngine_EnforcesFrameworkProtectionRules()
        {
            var engine = new TypeBindingEngine();

            Assert.True(engine.IsFrameworkProtected("&IsAuthorized", out string gamOwner));
            Assert.Equal("GAM", gamOwner);

            Assert.True(engine.IsFrameworkProtected("DiasSemanaFin", out string wwpOwner));
            Assert.Equal("WWP+", wwpOwner);

            Assert.False(engine.IsFrameworkProtected("CustomUserVar", out _));
            Assert.True(engine.ShouldSkipUnusedCheck("&Today"));
        }

        [Theory]
        [InlineData("Line")]
        [InlineData("Output")]
        [InlineData("Page")]
        [InlineData("&Msg")]
        [InlineData("Err")]
        [InlineData("Errmsg")]
        [InlineData("GxRemove")]
        [InlineData("Mr")]
        [InlineData("Op")]
        [InlineData("Outdev")]
        [InlineData("RestMethod")]
        [InlineData("RestCode")]
        public void SdkStandardVariables_AreNeverReportedAsUnused(string name)
        {
            Assert.True(GxMcp.Worker.Helpers.FrameworkManagedVariables.ShouldSkipUnusedCheck(name));
        }

        [Theory]
        [InlineData("I")]
        [InlineData("Hits")]
        [InlineData("Abc")]
        public void UserVariables_StillGetTheUnusedCheck(string name)
        {
            Assert.False(GxMcp.Worker.Helpers.FrameworkManagedVariables.ShouldSkipUnusedCheck(name));
        }
    }
}
