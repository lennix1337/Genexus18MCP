using System;
using System.Xml.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReportTextModeTests
    {
        public sealed class Bag
        {
            public object Value = true;
            public bool IgnoreWrite;
            public object GetPropertyValue(string name) => name == "RPT_TEXT_MODE" ? Value : null;
            public void SetPropertyValue(string name, object value) { if (!IgnoreWrite) Value = value; }
        }
        public sealed class Part
        {
            public Bag Layout { get; set; }
            public object Value;
            public object GetPropertyValue(string name) => name == "RPT_TEXT_MODE" ? Value : null;
        }

        [Fact]
        public void LayoutBagValueIsProjectedAndChangedWithoutClrProperty()
        {
            var bag = new Bag();
            var container = ReportLayoutHelper.GetTextModeContainer(new Part { Layout = bag });
            Assert.Same(bag, container);
            var root = new XElement("Report");
            ReportLayoutHelper.AppendTextModeProjection(root, container);
            Assert.Equal("True", root.Attribute("RPT_TEXT_MODE")?.Value);
            ReportLayoutHelper.SetTextMode(container, "False");
            ReportLayoutHelper.AppendTextModeProjection(root, container);
            Assert.Equal("False", root.Attribute("RPT_TEXT_MODE")?.Value);
            Assert.IsType<bool>(bag.Value);
        }

        [Theory]
        [InlineData("minimal")]
        [InlineData("standard")]
        [InlineData("all")]
        public void PropertyReadProjectionRetainsTextMode(string projection)
        {
            var props = new JObject { ["properties"] = new JArray(new JObject { ["name"] = "RPT_TEXT_MODE", ["value"] = "True" }) };
            var shaped = JObject.Parse(PropertyService.ShapeGetPropertiesResult(props, "SyntheticReport", projection: projection));
            Assert.Equal("True", shaped["result"]?["values"]?["RPT_TEXT_MODE"]?.ToString());
        }

        [Fact]
        public void MissingOrIgnoredOrInvalidWritesFailClosed()
        {
            Assert.Throws<InvalidOperationException>(() => ReportLayoutHelper.SetTextMode(null, "False"));
            Assert.Throws<InvalidOperationException>(() => ReportLayoutHelper.SetTextMode(new Bag { IgnoreWrite = true }, "False"));
            Assert.Throws<InvalidOperationException>(() => ReportLayoutHelper.SetTextMode(new Bag(), "not-a-bool"));
            var part = new Part { Layout = new Bag { Value = null }, Value = true };
            Assert.Same(part, ReportLayoutHelper.GetTextModeContainer(part));
        }

        [Fact]
        public void ServiceRoutesOnlyObjectScopedReportPropertyAndRequiresFreshVerification()
        {
            Assert.Null(PropertyService.ResolveReportPropertyContainer(null, "RPT_TEXT_MODE", "Control1"));
            Assert.Null(PropertyService.ResolveReportPropertyContainer(null, "Caption", null));
            Assert.NotNull(PropertyService.ValidateDirectPropertyWrite("RPT_TEXT_MODE"));
            Assert.Null(PropertyService.ValidateDirectPropertyWrite("Description"));
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"));
            Assert.Contains("ResolvePropertyContainer(obj, propName, controlName)", source);
            Assert.Contains("ResolveReportPropertyContainer(obj, nativeName, controlName)", source);
            Assert.Contains("ResolveReportPropertyContainer(fresh, propName, controlName)", source);
            Assert.Contains("FindObjectFreshByIdentity(original)", source);
            Assert.Contains("directWriteValidation = ValidateDirectPropertyWrite(pName)", source);
            Assert.Contains("if (reportProperty && (original == null || fresh.Guid != original.Guid))", source);
            Assert.Contains("PropertyVerificationUnavailable", source);
            Assert.Contains("reportProperty ? ReportPropertyVerificationUnavailable(target) : null", source);
            string layout = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "ReportLayoutHelper.cs"));
            Assert.Contains("SetTextMode(GetTextModeContainer(part), textMode.Value)", layout);
        }
    }
}
