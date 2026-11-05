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

        [Theory]
        [InlineData("AddReportControl")]
        [InlineData("MoveReportControl")]
        [InlineData("RemoveReportControl")]
        public void ReportFailuresCannotPerformASecondWrite(string methodName)
        {
            // Covers both unsafe interleavings: IDE edit before the first post-save read,
            // and IDE edit after a fence check but before opening the restore transaction.
            // No SDK atomic compare-and-restore exists here; removing the second write is the guard.
            var source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "LayoutService.ReportControls.cs");
            var body = GxMcp.TestSupport.SourceAssert.MethodBody(source, "public string " + methodName + "(");
            Assert.Equal(1, GxMcp.TestSupport.SourceAssert.Count(body, "PersistVisualXml("));
            Assert.DoesNotContain("TryRestoreReportBaseline", source);
            Assert.DoesNotContain("rolledBack: rolledBack", body);
            Assert.Contains("baselineXml: baseline", body);
        }

        [Theory]
        [InlineData("external-edit-before-read", "<Report><Control Name='ExternalBeforeRead'/></Report>")]
        [InlineData("external-edit-after-check", "<Report><Control Name='ExternalAfterCheck'/></Report>")]
        public void UnownedObservationsReturnRecoveryEvidenceNotRollbackSuccess(string observedVersion, string observedXml)
        {
            var method = typeof(LayoutService).GetMethod("ReportMutationFailure", BindingFlags.Static | BindingFlags.NonPublic);
            var response = JObject.Parse((string)method.Invoke(null, new object[] {
                "Report", "add_report_control", "A", "header", "diff", "verification failed", true, false, true,
                "<Report/>", "<Report><Control Name='A'/></Report>", observedXml, observedVersion }));
            Assert.False((bool)response["rolledBack"]);
            Assert.True((bool)response["recoveryRequired"]);
            Assert.Contains("Atomic ownership-proven", (string)response["rollbackUnavailableReason"]);
            Assert.Equal(observedXml, (string)response["recoveryEvidence"]["observedXml"]);
            Assert.Equal(observedVersion, (string)response["recoveryEvidence"]["observedVersion"]);
            Assert.False((bool)response["recoveryEvidence"]["observationProvesOwnership"]);
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
        public void CaptionVerificationAcceptsTheCaptionSpellingOfTheReadBack()
        {
            // add_report_control writes Text; the SDK layout read-back carries Caption.
            var method = typeof(LayoutService).GetMethod("ReportCaptionMatches",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var readBack = XElement.Parse("<Control TypeName=\"ReportLabel\" Name=\"lblTotal\" Caption=\"Total\" />");
            Assert.True((bool)method.Invoke(null, new object[] { readBack, "Total" })!);
            Assert.False((bool)method.Invoke(null, new object[] { readBack, "Subtotal" })!);

            var written = XElement.Parse("<Control TypeName=\"ReportLabel\" Name=\"lblTotal\" Text=\"Total\" />");
            Assert.True((bool)method.Invoke(null, new object[] { written, "Total" })!);
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

        [Theory]
        [InlineData("ReportRectangle", "ReportLabel")]
        [InlineData("ReportLine", "ReportAttribute")]
        [InlineData("ReportLabel", "ReportRectangle")]
        public void FramesAndLinesAreNotOverlapObstacles(string existingType, string candidateType)
        {
            var block = XElement.Parse(
                "<PrintBlock Name=\"pbTeste\"><Control TypeName=\"" + existingType + "\" ControlName=\"Box1\" Left=\"10\" Top=\"10\" Width=\"400\" Height=\"200\" /></PrintBlock>");
            var candidate = XElement.Parse(
                "<Control TypeName=\"" + candidateType + "\" ControlName=\"Lbl1\" Left=\"50\" Top=\"50\" Width=\"150\" Height=\"20\" />");

            Assert.Null(InvokeFindReportOverlap(block, candidate, "Lbl1"));
        }

        [Fact]
        public void OverlappingContentControlsAreStillRefused()
        {
            var block = XElement.Parse(
                "<PrintBlock Name=\"pbTeste\"><Control TypeName=\"ReportLabel\" ControlName=\"Lbl0\" Left=\"10\" Top=\"10\" Width=\"400\" Height=\"200\" /></PrintBlock>");
            var candidate = XElement.Parse(
                "<Control TypeName=\"ReportAttribute\" ControlName=\"nome1\" Left=\"50\" Top=\"100\" Width=\"150\" Height=\"20\" />");

            Assert.Contains("'Lbl0'", InvokeFindReportOverlap(block, candidate, "nome1"));
        }

        private static string InvokeFindReportOverlap(XElement block, XElement candidate, string name)
        {
            var method = typeof(LayoutService).GetMethod("FindReportOverlap",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (string)method.Invoke(null, new object[] { block, candidate, name });
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
