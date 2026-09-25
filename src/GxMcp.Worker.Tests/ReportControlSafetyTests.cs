using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReportControlSafetyTests
    {
        [Fact]
        public void AttributeBindingUsesTheAttributeReferenceProjection()
        {
            var method = typeof(LayoutService).GetMethod("CreateReportControl",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var control = (XElement)method.Invoke(null, new object[]
            {
                "ReportAttribute", "Filter", "attribute", "&CustomerId", "Customers", new JObject()
            })!;

            Assert.Equal("&CustomerId", (string)control.Attribute("AttributeReference"));
            Assert.Null(control.Attribute("ControlSource"));
        }

        [Fact]
        public void ReportBaseVersionFailureIsStructuredAndCarriesCurrentVersion()
        {
            var method = typeof(LayoutService).GetMethod("ReportBaseVersionRequired",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var response = JObject.Parse((string)method.Invoke(null, new object[] { "Report", "v-current" })!);
            Assert.Equal("ReportBaseVersionRequired", response["error"]?["code"]?.ToString());
            Assert.Equal("v-current", response["currentVersion"]?.ToString());
        }

        [Fact]
        public void ReportRollbackFenceRejectsNewerIndependentLayout()
        {
            var method = typeof(LayoutService).GetMethod(
                "IsReportRollbackFenceCurrent",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            string attempted = "<Report><PrintBlock Name=\"header\"><Control ControlName=\"A\" /></PrintBlock></Report>";
            string independent = "<Report><PrintBlock Name=\"header\"><Control ControlName=\"A\" /><Control ControlName=\"B\" /></PrintBlock></Report>";

            Assert.True((bool)method.Invoke(null, new object[] { "write-v1", "write-v1", attempted, attempted })!);
            Assert.False((bool)method.Invoke(null, new object[] { "write-v1", "write-v1", attempted, independent })!);
            Assert.False((bool)method.Invoke(null, new object[] { "write-v1", "write-v2", attempted, independent })!);
            Assert.False((bool)method.Invoke(null, new object[] { "write-v1", "write-v2", attempted, attempted })!);
        }

        [Theory]
        [InlineData("after")]
        [InlineData("below")]
        public void RelativeMoveReordersControlAndExpectedOrderVerificationSeesIt(string placementKind)
        {
            var document = XDocument.Parse(
                "<Report><PrintBlock Name=\"header\">" +
                "<Control ControlName=\"A\" /><Control ControlName=\"B\" /><Control ControlName=\"C\" />" +
                "</PrintBlock></Report>");
            var block = document.Descendants("PrintBlock").Single();
            var control = block.Elements("Control").Single(e => (string)e.Attribute("ControlName") == "C");
            var args = new JObject { [placementKind] = "A" };

            var reorder = typeof(LayoutService).GetMethod("ApplyReportControlOrder",
                BindingFlags.Static | BindingFlags.NonPublic);
            var capture = typeof(LayoutService).GetMethod("CaptureExpectedReportOrder",
                BindingFlags.Static | BindingFlags.NonPublic);
            var verify = typeof(LayoutService).GetMethod("VerifyExpectedReportOrder",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(reorder);
            Assert.NotNull(capture);
            Assert.NotNull(verify);

            Assert.True((bool)reorder.Invoke(null, new object[] { block, control, args })!);
            Assert.Equal(new[] { "A", "C", "B" },
                block.Elements("Control").Select(e => (string)e.Attribute("ControlName")));

            capture.Invoke(null, new object[] { document, "header", args });
            Assert.True((bool)verify.Invoke(null, new object[] { block, args })!);
        }

        [Theory]
        [InlineData("ReportAttribute")]
        [InlineData("ReportVariable")]
        public void ControlTypeWithoutKindStillRequiresBinding(string controlType)
        {
            var method = typeof(LayoutService).GetMethod("ValidateReportControlRequest",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var response = JObject.Parse((string)method.Invoke(null, new object[]
            {
                "Report", "header", string.Empty, "Field", string.Empty, string.Empty, controlType
            })!);

            Assert.Equal("ReportControlBindingRequired", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ReportAttributeWithoutKindUsesAttributeReferenceBinding()
        {
            var method = typeof(LayoutService).GetMethod("CreateReportControl",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var control = (XElement)method.Invoke(null, new object[]
            {
                "ReportAttribute", "Filter", string.Empty, "&CustomerId", string.Empty, new JObject()
            })!;

            Assert.Equal("&CustomerId", (string)control.Attribute("AttributeReference"));
            Assert.Null(control.Attribute("ControlSource"));
        }

        [Fact]
        public void RemoveVerificationIgnoresSameNamedControlInAnotherPrintBlock()
        {
            var document = XDocument.Parse(
                "<Report>" +
                "<PrintBlock Name=\"header\"><Control ControlName=\"Keep\" /></PrintBlock>" +
                "<PrintBlock Name=\"footer\"><Control ControlName=\"Target\" /></PrintBlock>" +
                "</Report>");
            var method = typeof(LayoutService).GetMethod("VerifyReportControlRemoved",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            string error = (string)method.Invoke(null, new object[]
            {
                document, "header", "Target"
            })!;

            Assert.Null(error);

            document.Descendants("PrintBlock").First().Add(
                new XElement("Control", new XAttribute("ControlName", "Target")));
            string sameBlockError = (string)method.Invoke(null, new object[]
            {
                document, "header", "Target"
            })!;
            Assert.Contains("print block 'header'", sameBlockError);
        }

        private sealed class FakeReportControl
        {
            public FakeReportControl(string name) { Name = name; }
            public string Name { get; set; }
        }

        [Fact]
        public void ReportHelperAppliesRequestedSiblingOrderWhenCollectionSupportsIt()
        {
            var items = new List<FakeReportControl>
            {
                new FakeReportControl("A"),
                new FakeReportControl("B"),
                new FakeReportControl("C")
            };
            var block = XElement.Parse("<PrintBlock><Control ControlName='C'/><Control ControlName='A'/><Control ControlName='B'/></PrintBlock>");
            var method = typeof(ReportLayoutHelper).GetMethod("ApplyRequestedControlOrder",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var changed = (bool)method.Invoke(null, new object[] { items, block })!;
            Assert.True(changed);
            Assert.Equal(new[] { "C", "A", "B" }, items.Select(item => item.Name));
        }
    }
}
