using System;
using System.Linq;
using System.Security;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #770: the Conditions (ControlWhere) of a layout-form control are written to both
    /// carriers, read back normalized, and the form is compared with the Conditions masked.
    /// </summary>
    public class ControlConditionsTests
    {
        private const string Expression = "Cw770Flag = true;";

        private static string Bag(string controlWhere)
        {
            var root = new XElement("Properties",
                new XElement("Property", new XElement("Name", "ControlType"), new XElement("Value", "Dynamic Combo Box")));
            if (controlWhere != null)
                root.Add(new XElement("Property", new XElement("Name", "ControlWhere"), new XElement("Value", controlWhere)));
            return root.ToString(SaveOptions.DisableFormatting);
        }

        private static string Form(string pecpWhere, string wucpWhere, bool wucpBag)
        {
            string wucp = !wucpBag ? string.Empty
                : " WebUserControlProperties=\"" + SecurityElement.Escape(Bag(wucpWhere)) + "\"";
            return "<GxMultiForm><Form type=\"layout\"><layout><table controlName=\"MainTable\"><row><cell>"
                + "<data attribute=\"var:5\"" + wucp
                + " PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"" + SecurityElement.Escape(Bag(pecpWhere)) + "\" />"
                + "</cell></row></table></layout></Form></GxMultiForm>";
        }

        private static XElement Data(XDocument document) => document.Descendants("data").Single();

        [Fact]
        public void BothNames_AreTheConditionsProperty()
        {
            Assert.True(ControlConditionsService.IsConditionsProperty("ControlWhere"));
            Assert.True(ControlConditionsService.IsConditionsProperty(" conditions "));
            Assert.False(ControlConditionsService.IsConditionsProperty("DataProviderParameters"));
            Assert.False(ControlConditionsService.IsConditionsProperty(null));
        }

        [Fact]
        public void SetExpression_ReplacesTheCustomPropertiesValue_AndKeepsTheOtherProperties()
        {
            var document = XDocument.Parse(Form("Old = 1;", null, wucpBag: false));
            Assert.True(ControlConditionsService.SetExpression(Data(document), Expression, out string failure), failure);

            Assert.Equal(Expression, ControlConditionsService.ReadExpression(Data(document)));
            Assert.True(ControlConditionCarriers.TryRead(Data(document), CustomPropertiesPayload.AttributeName, "ControlType", out string type));
            Assert.Equal("Dynamic Combo Box", type);
            Assert.Null(Data(document).Attribute(ControlConditionCarriers.WebUserControlPropertiesAttribute));
        }

        [Fact]
        public void SetExpression_AddsTheProperty_WhenTheControlHadNoConditions()
        {
            var document = XDocument.Parse(Form(null, null, wucpBag: false));
            Assert.True(ControlConditionsService.SetExpression(Data(document), Expression, out _));

            Assert.Equal(Expression, ControlConditionsService.ReadExpression(Data(document)));
        }

        [Fact]
        public void SetExpression_KeepsTheTwoCarriersInOneExpression()
        {
            var document = XDocument.Parse(Form("Old = 1;", "Old = 1;", wucpBag: true));
            Assert.True(ControlConditionsService.SetExpression(Data(document), Expression, out _));

            string xml = document.ToString(SaveOptions.DisableFormatting);
            // a write that left the carriers apart would be refused by the visual writer: none is introduced
            Assert.Empty(ControlConditionCarriers.FindIntroduced(Form("Old = 1;", "Old = 1;", wucpBag: true), xml));
            Assert.True(ControlConditionCarriers.TryRead(Data(document), ControlConditionCarriers.WebUserControlPropertiesAttribute, "ControlWhere", out string userControl));
            Assert.Equal(Expression, ControlExpressionText.Of(userControl));
        }

        [Fact]
        public void SetExpression_DoesNotCreateTheUserControlProperty_WhenThatBagLacksIt()
        {
            var document = XDocument.Parse(Form("Old = 1;", null, wucpBag: true));
            Assert.True(ControlConditionsService.SetExpression(Data(document), Expression, out _));

            Assert.False(ControlConditionCarriers.TryRead(Data(document), ControlConditionCarriers.WebUserControlPropertiesAttribute, "ControlWhere", out _));
        }

        [Fact]
        public void SetExpression_RefusesAnUnreadableBag_WithoutChangingIt()
        {
            var document = XDocument.Parse("<data attribute=\"var:5\" PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"not xml\" />");
            string before = document.ToString();
            Assert.False(ControlConditionsService.SetExpression(document.Root, Expression, out string failure));
            Assert.Contains("could not be parsed", failure);
            Assert.Equal(before, document.ToString());
        }

        [Fact]
        public void ReadExpression_ReadsATokenListAsTheExpressionItSpells()
        {
            string tokens = new XElement("TokenDataList",
                new XElement("TokenData", new XElement("Token", 2), new XElement("Word", "Cw770Flag")),
                new XElement("TokenData", new XElement("Token", 25), new XElement("Word", " ")),
                new XElement("TokenData", new XElement("Token", 10), new XElement("Word", "=")),
                new XElement("TokenData", new XElement("Token", 25), new XElement("Word", " ")),
                new XElement("TokenData", new XElement("Token", 41), new XElement("Word", "true")),
                new XElement("TokenData", new XElement("Token", 18), new XElement("Word", ";")),
                new XElement("TokenData", new XElement("Token", 25), new XElement("Word", "\r\n"))).ToString(SaveOptions.DisableFormatting);
            var document = XDocument.Parse(Form(tokens, null, wucpBag: false));

            Assert.Equal(Expression, ControlConditionsService.ReadExpression(Data(document)));
        }

        [Fact]
        public void ReadExpression_OfAControlWithoutConditions_IsNull()
        {
            Assert.Null(ControlConditionsService.ReadExpression(Data(XDocument.Parse(Form(null, null, wucpBag: false)))));
        }

        [Fact]
        public void LayoutDifference_IgnoresTheConditions_ButSeesAnyOtherChange()
        {
            string before = Form("Old = 1;", "Old = 1;", wucpBag: true);
            string after = Form(Expression, Expression, wucpBag: true);
            Assert.Null(ControlConditionsService.DescribeLayoutDifference(before, after));

            string otherChange = after.Replace("controlName=\"MainTable\"", "controlName=\"Renamed\"");
            Assert.NotNull(ControlConditionsService.DescribeLayoutDifference(before, otherChange));
        }

        [Fact]
        public void LayoutDifference_WhenTheControlHadNoConditionsBefore_IgnoresTheNewConditions_ButSeesAnyOtherChange()
        {
            // Live KBTeste (#770): the first Conditions written to a Dynamic Combo Box that had none
            // was rolled back because only the saved side carried a ControlWhere to mask.
            string before = Form(null, null, wucpBag: false);
            string after = Form(Expression, null, wucpBag: false);
            Assert.Null(ControlConditionsService.DescribeLayoutDifference(before, after));

            string otherChange = after.Replace("controlName=\"MainTable\"", "controlName=\"Renamed\"");
            Assert.NotNull(ControlConditionsService.DescribeLayoutDifference(before, otherChange));
        }

        [Fact]
        public void LayoutDifference_OfAnUnreadableSave_IsReported()
        {
            Assert.NotNull(ControlConditionsService.DescribeLayoutDifference(Form("a", null, false), ""));
        }

        [Fact]
        public void ValidateRequest_NormalizesSpacing_AllowsClearing_AndBoundsTheLength()
        {
            Assert.Null(ControlConditionsService.ValidateRequest("  Cw770Flag   =\r\n true;  ", out string expression));
            Assert.Equal(Expression, expression);

            Assert.Null(ControlConditionsService.ValidateRequest("", out string cleared));
            Assert.Equal(string.Empty, cleared);
            Assert.Null(ControlConditionsService.ValidateRequest(null, out _));

            Assert.Contains("limit", ControlConditionsService.ValidateRequest(new string('x', 4001), out _));
        }

        private static string Source(params string[] relative)
        {
            var parts = new[] { TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker" }.Concat(relative).ToArray();
            return System.IO.File.ReadAllText(System.IO.Path.Combine(parts));
        }

        [Fact]
        public void Wiring_TheWriteGoesThroughTheVersionCheckedRollbackPipeline()
        {
            string service = Source("Services", "ControlConditionsService.cs");

            Assert.Contains("rollbackOnFailure: true", service);
            Assert.Contains("baseVersion: before.CurrentVersion", service);
            Assert.Contains("strictVerify: true", service);
            Assert.DoesNotContain("forceWrite: true", service);
            Assert.Contains("ObjectMoveSnapshot.Capture(", service);
            Assert.Contains("ValidateProspectiveLayout(", service);
            Assert.True(System.Text.RegularExpressions.Regex.Matches(service, @"PropertyService\.InvalidatePropertyCache\(").Count >= 2);
        }

        [Fact]
        public void Wiring_TheDispatcherAndThePropertyServiceReachTheConditionsService()
        {
            string dispatcher = Source("Services", "CommandDispatcher.cs");
            string properties = Source("Services", "PropertyService.cs");

            Assert.Contains("_propertyService.ConditionsWriter = _controlConditionsService;", dispatcher);
            Assert.Contains("_controlConditionsService.Get(", dispatcher);
            Assert.Contains("ConditionsWriter.Set(target, controlName, value, null, false, true, typeFilter)", properties);
            Assert.Contains("ConditionsWriter.Set(target, controlName, only.Value?.ToString() ?? string.Empty, expectedVersion, dryRun, rollbackOnFailure, typeFilter)", properties);
        }
    }
}
