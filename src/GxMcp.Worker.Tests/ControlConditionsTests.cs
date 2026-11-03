using System;
using System.Collections.Generic;
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
                + "<data id=\"D1\" attribute=\"var:5\"" + wucp
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

        // ------------------------------------------- issue #830: a grid has no custom properties until it gets its first

        private static string BagOf(params (string Name, string Value)[] properties)
        {
            var root = new XElement("Properties");
            foreach (var property in properties)
                root.Add(new XElement("Property", new XElement("Name", property.Name), new XElement("Value", property.Value)));
            return root.ToString(SaveOptions.DisableFormatting);
        }

        // A grid element; customProperties == null is a grid that carries no PATTERN_ELEMENT_CUSTOM_PROPERTIES.
        private static string GridElement(string customProperties, string id)
            => "<grid id=\"" + id + "\" controlName=\"Grid" + id + "\""
               + (customProperties == null ? string.Empty : " PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"" + SecurityElement.Escape(customProperties) + "\"")
               + "><table controlName=\"GridTable" + id + "\" /></grid>";

        // A layout form with the given grids.
        private static string FormOf(params string[] gridElements)
            => "<GxMultiForm><Form type=\"layout\"><layout><table controlName=\"MainTable\"><row><cell>"
               + string.Concat(gridElements) + "</cell></row></table></layout></Form></GxMultiForm>";

        private static string GridForm(string customProperties, string id = "G1") => FormOf(GridElement(customProperties, id));

        // The control that is written, as the typed routes name it for the comparison.
        private static readonly Func<XElement, bool> GridTarget = ControlParametersService.MatchControlElement(new LayoutControl { Id = "G1", ControlName = "GridG1" });
        private static readonly Func<XElement, bool> DataTarget = ControlParametersService.MatchControlElement(new LayoutControl { Id = "D1" });

        [Fact]
        public void LayoutDifference_WhenTheControlHadNoCustomPropertiesAtAll_IgnoresTheFirstConditions()
        {
            // The saved form gets an attribute that holds nothing but the Conditions; the form before it has no attribute.
            // That is the same layout, and read as a change it reverted the Conditions typed onto a grid that had never had one.
            string before = GridForm(null);
            string after = GridForm(BagOf(("ControlWhere", Expression)));

            Assert.Null(ControlConditionsService.DescribeLayoutDifference(before, after, GridTarget));
            Assert.NotNull(ControlConditionsService.DescribeLayoutDifference(before, after.Replace("controlName=\"GridG1\"", "controlName=\"Renamed\""), GridTarget));
        }

        [Fact]
        public void LayoutDifference_WithoutTheElementOfTheControl_StaysStrict()
        {
            // No element to match: nothing is dropped from the comparison, and the new attribute is a difference.
            Assert.NotNull(ControlConditionsService.DescribeLayoutDifference(GridForm(null), GridForm(BagOf(("ControlWhere", Expression)))));
        }

        [Fact]
        public void LayoutDifference_ClearingTheOnlyConditions_IsNoLayoutChange()
        {
            Assert.Null(ControlConditionsService.DescribeLayoutDifference(GridForm(BagOf(("ControlWhere", Expression))), GridForm(null), GridTarget));
            Assert.Null(ControlConditionsService.DescribeLayoutDifference(
                GridForm(BagOf(("ControlWhere", Expression))), GridForm(BagOf(("ControlWhere", ""))), GridTarget));
        }

        [Fact]
        public void LayoutDifference_AnotherPropertyOfTheSameAttribute_IsStillAChange()
        {
            string before = GridForm(BagOf(("MaxRows", "10")));

            // Only the Conditions were added: no change.
            Assert.Null(ControlConditionsService.DescribeLayoutDifference(before, GridForm(BagOf(("MaxRows", "10"), ("ControlWhere", Expression))), GridTarget));
            // The Conditions were added and MaxRows changed too: a change.
            Assert.NotNull(ControlConditionsService.DescribeLayoutDifference(before, GridForm(BagOf(("MaxRows", "20"), ("ControlWhere", Expression))), GridTarget));
            // MaxRows appeared beside the Conditions on a grid that had no custom properties: a change.
            Assert.NotNull(ControlConditionsService.DescribeLayoutDifference(GridForm(null), GridForm(BagOf(("MaxRows", "10"), ("ControlWhere", Expression))), GridTarget));
        }

        [Fact]
        public void LayoutDifference_AnotherControlLosingItsOnlyConditions_IsStillAChange()
        {
            // Only the control that was written may gain or lose the attribute that holds nothing but its Conditions: a save that
            // takes it from a control the request never touched has changed the layout, and must not be taken for a success.
            string before = FormOf(GridElement(BagOf(("ControlWhere", "A = 0;")), "G1"), GridElement(BagOf(("ControlWhere", "B = 1;")), "G2"));
            string after = FormOf(GridElement(BagOf(("ControlWhere", "A = 1;")), "G1"), GridElement(null, "G2"));

            string diff = ControlConditionsService.DescribeLayoutDifference(before, after, GridTarget);

            Assert.NotNull(diff);
            Assert.Contains("PATTERN_ELEMENT_CUSTOM_PROPERTIES", diff);
        }

        // A data control whose WebUserControlProperties carries nothing but the Conditions (wucp == null: no such attribute).
        private static string DataFormWithConditionsOnlyCarrier(string wucpConditions, string pecpConditions)
        {
            string wucp = wucpConditions == null ? string.Empty
                : " WebUserControlProperties=\"" + SecurityElement.Escape(BagOf(("ControlWhere", wucpConditions))) + "\"";
            return "<GxMultiForm><Form type=\"layout\"><layout><table controlName=\"MainTable\"><row><cell>"
                + "<data id=\"D1\" attribute=\"var:5\"" + wucp
                + " PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"" + SecurityElement.Escape(BagOf(("ControlType", "Dynamic Combo Box"), ("ControlWhere", pecpConditions))) + "\" />"
                + "</cell></row></table></layout></Form></GxMultiForm>";
        }

        [Fact]
        public void LayoutDifference_TheCarrierThatTheWriteNeverCreates_IsNotDroppedFromTheComparison()
        {
            // WebUserControlProperties is never created by the write: a control that loses it has been changed by the save, even
            // when it holds nothing but the Conditions and the control is the one that was written.
            string before = DataFormWithConditionsOnlyCarrier("a", "a");
            string after = DataFormWithConditionsOnlyCarrier(null, "b");

            string diff = ControlConditionsService.DescribeLayoutDifference(before, after, DataTarget);

            Assert.NotNull(diff);
            Assert.Contains("WebUserControlProperties", diff);
            // The same carrier kept, with another expression, is only a change of the Conditions.
            Assert.Null(ControlConditionsService.DescribeLayoutDifference(before, DataFormWithConditionsOnlyCarrier("b", "b"), DataTarget));
        }

        [Fact]
        public void LayoutDifference_NamesWhatTheSaveDropped_BecauseTheFormReadBackGoesFirst()
        {
            // XmlEquivalence labels (persisted, requested): the form read back after the save is the persisted side, so what it
            // calls "rejected" was in the form before and is gone, and "added" is what the save put there.
            string withClass = GridForm(null).Replace("<grid id=\"G1\"", "<grid id=\"G1\" class=\"Grid\"");
            string afterWithoutClass = GridForm(BagOf(("ControlWhere", Expression)));

            string dropped = ControlConditionsService.DescribeLayoutDifference(withClass, afterWithoutClass, GridTarget);
            Assert.Contains("rejectedByPersist=[class]", dropped);
            Assert.DoesNotContain("addedByPersist", dropped);

            string added = ControlConditionsService.DescribeLayoutDifference(GridForm(null),
                GridForm(BagOf(("ControlWhere", Expression))).Replace("<grid id=\"G1\"", "<grid id=\"G1\" class=\"Grid\""), GridTarget);
            Assert.Contains("addedByPersist=[class]", added);
            Assert.DoesNotContain("rejectedByPersist", added);
        }

        [Fact]
        public void MaskConditions_DropsTheAttributeThatHoldsNothingButTheConditions_OfTheControlThatWasWritten()
        {
            Assert.DoesNotContain("PATTERN_ELEMENT_CUSTOM_PROPERTIES",
                ControlConditionsService.MaskConditions(GridForm(BagOf(("ControlWhere", Expression))), GridTarget));
            Assert.DoesNotContain("PATTERN_ELEMENT_CUSTOM_PROPERTIES", ControlConditionsService.MaskConditions(GridForm(null), GridTarget));
        }

        [Fact]
        public void MaskConditions_DropsAnEmptyPayloadOfTheControlThatWasWritten()
        {
            Assert.DoesNotContain("PATTERN_ELEMENT_CUSTOM_PROPERTIES", ControlConditionsService.MaskConditions(GridForm("<Properties />"), GridTarget));
        }

        [Fact]
        public void MaskConditions_KeepsTheAttributeWhenItHoldsMore_OrWhenNoControlIsGiven_OrForAnotherControl()
        {
            string masked = ControlConditionsService.MaskConditions(GridForm(BagOf(("MaxRows", "10"), ("ControlWhere", Expression))), GridTarget);
            Assert.Contains("PATTERN_ELEMENT_CUSTOM_PROPERTIES", masked);
            Assert.Contains("MaxRows", masked);
            Assert.Contains("(masked)", masked);
            Assert.DoesNotContain("Cw770Flag", masked);

            // No control given, or a different one: the Conditions are masked and the attribute stays.
            Assert.Contains("PATTERN_ELEMENT_CUSTOM_PROPERTIES", ControlConditionsService.MaskConditions(GridForm(BagOf(("ControlWhere", Expression)))));
            Assert.Contains("PATTERN_ELEMENT_CUSTOM_PROPERTIES",
                ControlConditionsService.MaskConditions(GridForm(BagOf(("ControlWhere", Expression)), "G2"), GridTarget));
        }

        [Fact]
        public void MaskConditions_NeverDropsTheWebUserControlPropertiesCarrier()
        {
            // Even where it holds nothing but the Conditions and the control is the one that was written.
            string masked = ControlConditionsService.MaskConditions(DataFormWithConditionsOnlyCarrier("a", "a"), DataTarget);

            Assert.Contains("WebUserControlProperties", masked);
            Assert.Contains("PATTERN_ELEMENT_CUSTOM_PROPERTIES", masked);   // it holds ControlType too
        }

        // ---------------------------------------------------- the element of the control that was written (issue #830)

        [Theory]
        [InlineData("<grid id=\"G1\" controlName=\"Other\" />", true)]
        [InlineData("<grid id=\"g1\" />", true)]                                // the id ignores case
        [InlineData("<grid id=\"changed\" controlName=\"GridG1\" />", true)]    // a layout that a pattern generates: new ids, same name
        [InlineData("<grid id=\"changed\" controlName=\"gridg1\" />", false)]   // the name is exact
        [InlineData("<grid id=\"G2\" controlName=\"GridG2\" />", false)]
        [InlineData("<grid />", false)]
        public void TheElementOfTheControl_IsMatchedByItsIdOrByItsName(string xml, bool expected)
        {
            Assert.Equal(expected, GridTarget(XElement.Parse(xml)));
        }

        [Fact]
        public void NothingMatchesWithoutAControlOrWithoutItsIdAndName()
        {
            var element = XElement.Parse("<grid id=\"G1\" controlName=\"GridG1\" />");

            Assert.False(ControlParametersService.MatchControlElement(null)(element));
            Assert.False(ControlParametersService.MatchControlElement(new LayoutControl())(element));
        }

        // ------------------------------------------------------------ the singular spelling (issue #830)

        [Theory]
        [InlineData("Condition", true)]
        [InlineData(" condition ", true)]
        [InlineData("CONDITION", true)]
        [InlineData("Conditions", false)]
        [InlineData("ControlWhere", false)]
        [InlineData("Where", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void TheSingularAlias_IsOnlyCondition(string name, bool expected)
        {
            Assert.Equal(expected, ControlConditionsService.IsSingularAlias(name));
        }

        [Fact]
        public void TheAliasApplies_ToAControlThatListsControlWhereAndHasNoConditionOfItsOwn()
        {
            Assert.True(ControlConditionsService.AliasApplies(new[] { "id", "ControlWhere", "MaxRows" }));
            Assert.True(ControlConditionsService.AliasApplies(new[] { "controlwhere" }));
        }

        [Fact]
        public void TheAliasDoesNotApply_WithoutControlWhere_OrWhereTheControlHasItsOwnCondition()
        {
            Assert.False(ControlConditionsService.AliasApplies(new[] { "id", "MaxRows" }));
            Assert.False(ControlConditionsService.AliasApplies(new[] { "ControlWhere", "Condition" }));
            Assert.False(ControlConditionsService.AliasApplies(new[] { "ControlWhere", "condition" }));
            Assert.False(ControlConditionsService.AliasApplies(null));
            Assert.False(ControlConditionsService.AliasApplies(new string[0]));
            Assert.False(ControlConditionsService.AliasApplies(new string[] { null, string.Empty }));
        }

        [Theory]
        [InlineData("Condition", true)]
        [InlineData(" CONDITION ", true)]
        [InlineData("Conditions", false)]      // not the alias: ControlWhere and Conditions have their own route
        [InlineData("ControlWhere", false)]
        [InlineData("Where", false)]
        [InlineData(null, false)]
        public void TheSetsTreatConditionAsTheConditions_OnlyOnAControlThatListsControlWhere(string name, bool expected)
        {
            Assert.Equal(expected, PropertyService.IsConditionsAlias(name, new[] { "id", "ControlWhere" }));
        }

        [Fact]
        public void TheSetsDoNotTreatConditionAsTheConditions_OnAControlWithoutControlWhereOrWithItsOwnCondition()
        {
            Assert.False(PropertyService.IsConditionsAlias("Condition", new[] { "id", "MaxRows" }));
            Assert.False(PropertyService.IsConditionsAlias("Condition", new[] { "ControlWhere", "Condition" }));
            Assert.False(PropertyService.IsConditionsAlias("Condition", null));
            Assert.False(PropertyService.IsConditionsAlias("Condition", new string[0]));
        }

        // The read decides without touching the object unless it has to: ControlWhere and Conditions ask whether it is a layout
        // control, Condition asks for the control's property names, and nothing else asks for anything.
        private static Func<T> Never<T>() => () => throw new InvalidOperationException("must not be asked");

        [Theory]
        [InlineData("ControlWhere")]
        [InlineData("Conditions")]
        [InlineData(" conditions ")]
        public void ReadingTheConditionsRoutesWhenTheControlIsALayoutControl(string name)
        {
            Assert.True(ControlConditionsService.ReadRoutesToConditions(name, () => true, Never<IEnumerable<string>>()));
            Assert.False(ControlConditionsService.ReadRoutesToConditions(name, () => false, Never<IEnumerable<string>>()));
        }

        [Fact]
        public void ReadingConditionRoutesOnlyWhereTheAliasApplies()
        {
            Assert.True(ControlConditionsService.ReadRoutesToConditions("Condition", Never<bool>(), () => new[] { "id", "ControlWhere" }));
            Assert.False(ControlConditionsService.ReadRoutesToConditions("Condition", Never<bool>(), () => new[] { "id", "MaxRows" }));
            Assert.False(ControlConditionsService.ReadRoutesToConditions("Condition", Never<bool>(), () => new[] { "ControlWhere", "Condition" }));
            Assert.False(ControlConditionsService.ReadRoutesToConditions("Condition", Never<bool>(), () => new string[0]));
        }

        [Theory]
        [InlineData("MaxRows")]
        [InlineData("Where")]
        [InlineData("")]
        [InlineData(null)]
        public void ReadingAnythingElseNeverRoutesAndNeverAsks(string name)
        {
            Assert.False(ControlConditionsService.ReadRoutesToConditions(name, Never<bool>(), Never<IEnumerable<string>>()));
        }

        [Fact]
        public void Wiring_TheAliasIsRoutedOnTheReadAndOnBothSets()
        {
            string dispatcher = Source("Services", "CommandDispatcher.cs");
            string properties = Source("Services", "PropertyService.cs");

            Assert.Contains("_controlConditionsService.HandlesRead(target, args?[\"control\"]?.ToString(), propType, requestedProperty)", dispatcher);
            Assert.Contains("(ControlConditionsService.IsConditionsProperty(propName) || IsConditionsAlias(propName, LayoutPropertyNames((object)container)))", properties);
            Assert.Contains("(ControlConditionsService.IsConditionsProperty(only.Name) || IsConditionsAlias(only.Name, LayoutPropertyNames((object)container)))", properties);
            // The re-resolution to the layout control, taken only when the alias applies to it.
            Assert.Contains("layout != null && (!ControlConditionsService.IsSingularAlias(propName) || IsConditionsAlias(propName, LayoutPropertyNames(layout)))", properties);
            Assert.Contains("layout != null && (!ControlConditionsService.IsSingularAlias(only.Name) || IsConditionsAlias(only.Name, LayoutPropertyNames(layout)))", properties);
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

        [Fact]
        public void Find_MatchesControlByVariableReferenceAndBindingName()
        {
            var panel = new LayoutPanel();
            var control = new LayoutControl
            {
                Id = Guid.NewGuid().ToString(),
                ControlName = "ComboValue",
                Binding = "var:5",
                BindingName = "ComboValue"
            };
            panel.Controls.Add(control);

            var byRef = LayoutFormModel.Find(panel, "&ComboValue");
            var byBare = LayoutFormModel.Find(panel, "ComboValue");
            var byBinding = LayoutFormModel.Find(panel, "var:5");

            Assert.Single(byRef);
            Assert.Same(control, byRef[0]);
            Assert.Single(byBare);
            Assert.Same(control, byBare[0]);
            Assert.Single(byBinding);
            Assert.Same(control, byBinding[0]);
        }
    }
}
