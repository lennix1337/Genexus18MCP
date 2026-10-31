using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The parameter list of a layout-form Dynamic Combo Box: the text rules around the SDK's
    /// language engine, the custom-properties payload that carries the stored list, and the
    /// request checks that run before anything reaches the SDK.
    /// </summary>
    public class ControlParametersTests
    {
        // ------------------------------------------------------------ splitting

        [Fact]
        public void Split_SeparatesAtTopLevelCommasInOrder()
        {
            var parts = ParameterListText.Split("McpCbFilterA, McpCbFilterB ,&Var,  'x'");

            Assert.Equal(new[] { "McpCbFilterA", "McpCbFilterB", "&Var", "'x'" }, parts);
        }

        [Fact]
        public void Split_KeepsCommasInsideCallsBracketsAndQuotes()
        {
            var parts = ParameterListText.Split("Fn(a, b), \"x, y\", 'p,q', Arr[1,2]");

            Assert.Equal(new[] { "Fn(a, b)", "\"x, y\"", "'p,q'", "Arr[1,2]" }, parts);
        }

        [Fact]
        public void Split_OfNothing_IsAnEmptyList()
        {
            Assert.Empty(ParameterListText.Split(null));
            Assert.Empty(ParameterListText.Split("   \r\n"));
        }

        [Fact]
        public void Split_KeepsAnEmptyEntryInsteadOfDroppingAParameter()
        {
            var parts = ParameterListText.Split("a,,b");

            Assert.Equal(new[] { "a", "", "b" }, parts);
        }

        [Fact]
        public void Join_UsesTheSdkSeparatorAndTrims()
        {
            Assert.Equal("a, b, c", ParameterListText.Join(new[] { " a", "b ", "c" }));
            Assert.Equal(string.Empty, ParameterListText.Join(null));
        }

        [Theory]
        [InlineData("McpCbFilterA", true)]
        [InlineData("Fn(a, b)", true)]
        [InlineData("a, b", false)]
        [InlineData("", false)]
        [InlineData("  ", false)]
        public void IsSingleExpression_IsExactlyOneNonEmptyEntry(string expression, bool expected)
        {
            Assert.Equal(expected, ParameterListText.IsSingleExpression(expression));
        }

        // ------------------------------------------------------- att:<id> -> name

        private static string Lookup(int id) => id == 226 ? "McpCbId" : id == 232 ? "McpCbCategory" : null;

        [Fact]
        public void ResolveAttributeReferences_ReplacesAnAttIdWithTheAttributeName()
        {
            var resolved = ParameterListText.ResolveAttributeReferences(
                new[] { "att:226", "&Var", "Fn(att:232, 1)" }, Lookup, out var unresolved);

            Assert.Empty(unresolved);
            Assert.Equal(new[] { "McpCbId", "&Var", "Fn(McpCbCategory, 1)" }, resolved);
        }

        [Fact]
        public void ResolveAttributeReferences_ReportsAnIdTheKbDoesNotKnow_WithItsIndex()
        {
            var resolved = ParameterListText.ResolveAttributeReferences(
                new[] { "att:226", "att:953" }, Lookup, out var unresolved);

            var problem = Assert.Single(unresolved);
            Assert.Equal(1, problem.Index);
            Assert.Equal(953, problem.AttributeId);
            Assert.Equal("att:953", problem.Expression);
            Assert.Equal("att:953", resolved[1]); // never guessed
        }

        [Fact]
        public void ResolveAttributeReferences_ALookupThatFails_IsNotReportedAsAnUnknownAttribute()
        {
            var resolved = ParameterListText.ResolveAttributeReferences(
                new[] { "att:226" }, id => throw new InvalidOperationException("model is closed"), out var unresolved);

            var problem = Assert.Single(unresolved);
            Assert.Equal("model is closed", problem.LookupFailure);
            Assert.Contains("could not be looked up: model is closed", problem.Message);
            Assert.Equal("att:226", resolved[0]);
        }

        [Fact]
        public void UnresolvedAttributeReference_WithoutAFailure_SaysTheAttributeDoesNotResolve()
        {
            var problem = new UnresolvedAttributeReference { AttributeId = 953 };

            Assert.Equal("att:953 does not resolve in this KB.", problem.Message);
        }

        [Fact]
        public void ResolveAttributeReferences_LeavesQuotedTextAlone()
        {
            var resolved = ParameterListText.ResolveAttributeReferences(
                new[] { "'att:226'", "\"see att:232\"", "att:226 + 'att:232'" }, Lookup, out var unresolved);

            Assert.Empty(unresolved);
            Assert.Equal("'att:226'", resolved[0]);
            Assert.Equal("\"see att:232\"", resolved[1]);
            Assert.Equal("McpCbId + 'att:232'", resolved[2]);
        }

        [Fact]
        public void ResolveAttributeReferences_ALookupThatThrows_IsAnUnresolvedReference()
        {
            ParameterListText.ResolveAttributeReferences(
                new[] { "att:1" }, id => throw new InvalidOperationException("model not ready"), out var unresolved);

            Assert.Single(unresolved);
        }

        // ------------------------------------------------------------ the request

        private static string Normalize(JArray expressions, out List<string> list)
        {
            list = ControlParametersService.NormalizeRequest(expressions, "McpComboTrn", out string error);
            return error;
        }

        [Fact]
        public void Request_WithoutAList_AsksForTheCompleteList()
        {
            string error = Normalize(null, out var list);

            Assert.Null(list);
            Assert.Equal("ExpressionsRequired", JObject.Parse(error)["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Request_AnEmptyListIsValid_ItClearsTheParameters()
        {
            string error = Normalize(new JArray(), out var list);

            Assert.Null(error);
            Assert.Empty(list);
        }

        [Fact]
        public void Request_TrimsAndKeepsOrder()
        {
            Assert.Null(Normalize(new JArray(" A ", "&B"), out var list));
            Assert.Equal(new[] { "A", "&B" }, list);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a, b")]
        [InlineData("a\nb")]
        public void Request_RefusesAnEntryThatIsNotExactlyOneExpression(string expression)
        {
            string error = Normalize(new JArray("ok", expression), out var list);

            Assert.Null(list);
            var envelope = JObject.Parse(error);
            Assert.Equal("InvalidExpression", envelope["error"]?["code"]?.ToString());
            Assert.Contains("expressions[1]", envelope["error"]?["message"]?.ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Request_NamesAnEmptyEntryAsEmpty_NotAsSomethingElse(string expression)
        {
            string error = Normalize(new JArray("ok", expression), out _);

            var message = JObject.Parse(error)["error"]?["message"]?.ToString();
            Assert.Contains("expressions[1] is empty", message);
        }

        [Fact]
        public void Request_RefusesANonStringEntry()
        {
            string error = Normalize(new JArray("ok", 5), out _);

            Assert.Equal("InvalidExpression", JObject.Parse(error)["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Request_RefusesMoreThanTheLimit()
        {
            var many = new JArray(Enumerable.Range(0, 65).Select(i => "A" + i));

            Assert.Equal("TooManyExpressions", JObject.Parse(Normalize(many, out _))["error"]?["code"]?.ToString());
        }

        // ------------------------------------------------------------- the diff

        [Fact]
        public void Changes_ShowsAddedRemovedChangedAndUnchangedByPosition()
        {
            var changes = ControlParametersService.BuildChanges(
                new[] { "A", "B", "C" }, new[] { "A", "b2", "C", "D" });

            Assert.Equal(new[] { "unchanged", "changed", "unchanged", "added" },
                changes.Select(c => c["change"]?.ToString()).ToArray());
            Assert.Equal("D", changes[3]["after"]?.ToString());
            Assert.Equal(JTokenType.Null, changes[3]["before"].Type);

            var shorter = ControlParametersService.BuildChanges(new[] { "A", "B" }, new[] { "A" });
            Assert.Equal("removed", shorter[1]["change"]?.ToString());
        }

        [Fact]
        public void Changes_TreatsSpacingOnlyDifferencesAsUnchanged()
        {
            var changes = ControlParametersService.BuildChanges(new[] { "Fn(a,  b)" }, new[] { "Fn(a, b)" });

            Assert.Equal("unchanged", changes[0]["change"]?.ToString());
        }

        [Fact]
        public void SameExpressions_ComparesOrderCountAndSpelling()
        {
            Assert.True(ControlParametersService.SameExpressions(new[] { "A", "B" }, new[] { "A", "B" }));
            Assert.False(ControlParametersService.SameExpressions(new[] { "A", "B" }, new[] { "B", "A" }));
            Assert.False(ControlParametersService.SameExpressions(new[] { "A" }, new[] { "A", "B" }));
            Assert.True(ControlParametersService.SameExpressions(new[] { "Fn(a,  b)" }, new[] { "Fn(a, b)" }));
        }

        [Fact]
        public void SameStoredTokens_IgnoresFormattingOfTheTokenXmlOnly()
        {
            string a = "<TokenDataList><TokenData><Token>2</Token><Word>A</Word><Id>228</Id></TokenData></TokenDataList>";
            string spaced = "<TokenDataList>\r\n  <TokenData><Token>2</Token><Word>A</Word><Id>228</Id></TokenData>\r\n</TokenDataList>";
            string other = "<TokenDataList><TokenData><Token>2</Token><Word>B</Word><Id>229</Id></TokenData></TokenDataList>";

            Assert.True(ControlParametersService.SameStoredTokens(a, spaced));
            Assert.False(ControlParametersService.SameStoredTokens(a, other));
            Assert.True(ControlParametersService.SameStoredTokens(null, ""));
        }

        // ------------------------------------------------------------- payload

        private const string Tokens =
            "<TokenDataList><TokenData><Token>2</Token><Word>McpCbFilterA</Word><Id>228</Id></TokenData></TokenDataList>";

        private static string Payload(params (string name, string value)[] properties)
            => new CustomPropertiesPayload { }.WithAll(properties);

        [Fact]
        public void Payload_ReadsPropertiesByNameInStoredOrder()
        {
            string text = Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", Tokens));

            Assert.True(CustomPropertiesPayload.TryParse(text, out var payload));
            Assert.Equal(new[] { "ControlType", "DataProviderParameters" }, payload.Properties.Select(p => p.Key).ToArray());
            Assert.True(payload.TryGet("dataproviderparameters", out string value)); // names are case-insensitive
            Assert.Equal(Tokens, value);
            Assert.False(payload.TryGet("Missing", out _));
        }

        [Fact]
        public void Payload_SettingOneValueKeepsEveryOtherPropertyAndOrder()
        {
            CustomPropertiesPayload.TryParse(
                Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", Tokens), ("DPItemValuesFieldSpecifier", "ItemId")),
                out var payload);

            string updated = payload.ToAttributeValue("DataProviderParameters", "<TokenDataList />");

            CustomPropertiesPayload.TryParse(updated, out var reread);
            Assert.Equal(new[] { "ControlType", "DataProviderParameters", "DPItemValuesFieldSpecifier" },
                reread.Properties.Select(p => p.Key).ToArray());
            reread.TryGet("DataProviderParameters", out string value);
            Assert.Equal("<TokenDataList />", value);
            reread.TryGet("DPItemValuesFieldSpecifier", out string untouched);
            Assert.Equal("ItemId", untouched);
        }

        [Fact]
        public void Payload_SettingAMissingPropertyAppendsIt()
        {
            CustomPropertiesPayload.TryParse(Payload(("ControlType", "Dynamic Combo Box")), out var payload);

            CustomPropertiesPayload.TryParse(payload.ToAttributeValue("DataProviderParameters", Tokens), out var reread);

            Assert.Equal(new[] { "ControlType", "DataProviderParameters" }, reread.Properties.Select(p => p.Key).ToArray());
        }

        [Fact]
        public void Payload_PreservesCarriageReturnsInsideATokenValue()
        {
            string tokensWithNewline = "<TokenDataList><TokenData><Token>25</Token><Word>\r\n</Word><Id>0</Id></TokenData></TokenDataList>";
            CustomPropertiesPayload.TryParse(Payload(("ControlType", "Dynamic Combo Box")), out var payload);

            CustomPropertiesPayload.TryParse(payload.ToAttributeValue("DataProviderParameters", tokensWithNewline), out var reread);

            reread.TryGet("DataProviderParameters", out string value);
            Assert.Contains("<Word>", value);
        }

        [Fact]
        public void Payload_Unparseable_IsRefusedNotInvented()
        {
            Assert.False(CustomPropertiesPayload.TryParse("<Properties><Property>", out var payload));
            Assert.Null(payload);
            Assert.False(CustomPropertiesPayload.TryParse("<Other/>", out _));
            Assert.True(CustomPropertiesPayload.TryParse(null, out var empty));
            Assert.Empty(empty.Properties);
        }

        [Fact]
        public void Payload_SemanticEquality_IgnoresPropertyOrderAndTokenFormatting()
        {
            string original = Payload(("ControlType", "Dynamic Combo Box"), ("DPItemValuesFieldSpecifier", "ItemId"), ("DataProviderParameters", Tokens));
            string reordered = Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", "\r\n" + Tokens + "\r\n"), ("DPItemValuesFieldSpecifier", "ItemId"));

            Assert.True(CustomPropertiesPayload.SemanticallyEqual(original, reordered));
        }

        [Fact]
        public void Payload_SemanticEquality_StillSeesARealDifference()
        {
            string original = Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", Tokens));
            string differentValue = Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", "<TokenDataList />"));
            string missing = Payload(("ControlType", "Dynamic Combo Box"));
            string extra = Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", Tokens), ("AddEmptyItem", "True"));

            Assert.False(CustomPropertiesPayload.SemanticallyEqual(original, differentValue));
            Assert.False(CustomPropertiesPayload.SemanticallyEqual(original, missing));
            Assert.False(CustomPropertiesPayload.SemanticallyEqual(original, extra));
            Assert.False(CustomPropertiesPayload.SemanticallyEqual(original, "<Properties>"));
        }

        // -------------------------------------------- the form compare (item 4)

        private static string Form(string attributeValue)
            => "<GxMultiForm><Form type=\"layout\"><detail><layout><table controlName=\"T\"><row><cell>"
               + "<data attribute=\"att:232\" PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"" + System.Security.SecurityElement.Escape(attributeValue) + "\" />"
               + "<data attribute=\"att:226\" labelCaption=\"=McpCbId.ContextualTitle\" />"
               + "</cell></row></table></layout></detail></Form></GxMultiForm>";

        [Fact]
        public void FormCompare_APayloadTheSdkRespelledInItsOwnOrderIsTheSameForm()
        {
            string requested = Form(Payload(("ControlType", "Dynamic Combo Box"), ("DPItemValuesFieldSpecifier", "ItemId"), ("DataProviderParameters", Tokens)));
            string persisted = Form(Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", Tokens), ("DPItemValuesFieldSpecifier", "ItemId")));

            Assert.True(XmlEquivalence.AreEquivalent(persisted, requested, XmlEquivalenceOptions.Layout, out string diff), diff);
        }

        [Fact]
        public void FormCompare_ADifferentParameterListIsStillADifference()
        {
            string requested = Form(Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", Tokens)));
            string persisted = Form(Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", "<TokenDataList />")));

            Assert.False(XmlEquivalence.AreEquivalent(persisted, requested, XmlEquivalenceOptions.Layout, out string diff));
            Assert.Contains("PATTERN_ELEMENT_CUSTOM_PROPERTIES", diff);
        }

        [Fact]
        public void FormCompare_AnotherAttributeOfTheSameElementIsStillADifference()
        {
            string a = Form(Payload(("ControlType", "Dynamic Combo Box")));
            string b = a.Replace("=McpCbId.ContextualTitle", "=Other.ContextualTitle");

            Assert.False(XmlEquivalence.AreEquivalent(a, b, XmlEquivalenceOptions.Layout, out _));
        }

        [Fact]
        public void LayoutDifference_IgnoresTheParametersButSeesEverythingElse()
        {
            string before = Form(Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", Tokens)));
            string afterOnlyParametersChanged = Form(Payload(("ControlType", "Dynamic Combo Box"), ("DataProviderParameters", "<TokenDataList />")));
            string afterOtherControlChanged = afterOnlyParametersChanged.Replace("=McpCbId.ContextualTitle", "=Other.ContextualTitle");

            Assert.Null(ControlParametersService.DescribeLayoutDifference(before, afterOnlyParametersChanged));
            Assert.NotNull(ControlParametersService.DescribeLayoutDifference(before, afterOtherControlChanged));
            Assert.NotNull(ControlParametersService.DescribeLayoutDifference(before, null));
            // A restore is compared in full, parameters included.
            Assert.NotNull(ControlParametersService.DescribeLayoutDifference(before, afterOnlyParametersChanged, includeTarget: true));
            Assert.Null(ControlParametersService.DescribeLayoutDifference(before, before, includeTarget: true));
        }

        // -------------------------------------------------------- layout model

        [Fact]
        public void LayoutOnlyReference_IsAnAttIdOrAGuid()
        {
            Assert.True(LayoutFormModel.IsLayoutOnlyReference("att:232"));
            Assert.True(LayoutFormModel.IsLayoutOnlyReference(" ATT:7 "));
            Assert.True(LayoutFormModel.IsLayoutOnlyReference("3f03d6d2-2817-417e-880e-71053516f32e"));
            Assert.False(LayoutFormModel.IsLayoutOnlyReference("McpCbCategory"));
            Assert.False(LayoutFormModel.IsLayoutOnlyReference("&Var"));
            Assert.False(LayoutFormModel.IsLayoutOnlyReference("att:abc"));
            Assert.False(LayoutFormModel.IsLayoutOnlyReference(null));
        }

        [Fact]
        public void DetailElement_IsFoundOnlyForALayoutForm()
        {
            var layout = new XmlDocument();
            layout.LoadXml("<GxMultiForm><Form id=\"2\" type=\"layout\"><detail><layout/></detail></Form></GxMultiForm>");
            var html = new XmlDocument();
            html.LoadXml("<GxMultiForm><Form id=\"1\" type=\"html\"><detail/><BODY/></Form></GxMultiForm>"); // a detail under an html form is still not a layout form

            Assert.Equal("detail", LayoutFormModel.FindDetailElement(layout)?.LocalName);
            Assert.True(LayoutFormModel.IsLayoutForm(layout));
            Assert.Null(LayoutFormModel.FindDetailElement(html));
            Assert.False(LayoutFormModel.IsLayoutForm(html));
            Assert.Null(LayoutFormModel.FindDetailElement(null));
        }

        private static LayoutPanel Panel(params LayoutControl[] controls)
        {
            var panel = new LayoutPanel();
            panel.Controls.AddRange(controls);
            return panel;
        }

        private static LayoutControl DataItem(string controlName, string binding, string bindingName, string id = null)
            => new LayoutControl { ElementName = "data", ControlName = controlName, Binding = binding, BindingName = bindingName, Id = id };

        [Fact]
        public void Find_ResolvesByAttIdAttributeNameControlNameAndGuid()
        {
            var category = DataItem("McpCbCategory", "att:232", "McpCbCategory");
            var named = DataItem("Combo1", "att:226", "McpCbId", id: "3f03d6d2-2817-417e-880e-71053516f32e");
            var panel = Panel(category, named, new LayoutControl { ElementName = "textblock", ControlName = "Title" });

            Assert.Same(category, Assert.Single(LayoutFormModel.Find(panel, "att:232")));
            Assert.Same(category, Assert.Single(LayoutFormModel.Find(panel, "McpCbCategory")));
            Assert.Same(category, Assert.Single(LayoutFormModel.Find(panel, "mcpcbcategory")));
            Assert.Same(named, Assert.Single(LayoutFormModel.Find(panel, "Combo1")));
            Assert.Same(named, Assert.Single(LayoutFormModel.Find(panel, "McpCbId")));     // by the attribute behind a custom control name
            Assert.Same(named, Assert.Single(LayoutFormModel.Find(panel, "3F03D6D2-2817-417E-880E-71053516F32E")));
            Assert.Empty(LayoutFormModel.Find(panel, "att:999"));
            Assert.Empty(LayoutFormModel.Find(panel, ""));
            Assert.Empty(LayoutFormModel.Find(null, "att:232"));
        }

        [Fact]
        public void Find_ReportsEverySecondControlBoundToTheSameAttribute()
        {
            var panel = Panel(DataItem("A", "att:232", "McpCbCategory"), DataItem("B", "att:232", "McpCbCategory"));

            Assert.Equal(2, LayoutFormModel.Find(panel, "att:232").Count);
        }

        [Fact]
        public void Find_AControlNameWinsOverAnAttributeNameSpelledTheSame()
        {
            var byName = DataItem("Total", "att:1", "Other");
            var byAttribute = DataItem("Third", "att:2", "Total");
            var panel = Panel(byName, byAttribute);

            Assert.Same(byName, Assert.Single(LayoutFormModel.Find(panel, "Total")));
        }

        [Fact]
        public void CustomProperty_IsReadFromTheControlsPayload()
        {
            var control = DataItem("McpCbCategory", "att:232", "McpCbCategory");
            control.CustomPropertiesXml = Payload(("ControlType", "Dynamic Combo Box"));

            Assert.Equal("Dynamic Combo Box", LayoutFormModel.ReadCustomProperty(control, "ControlType"));
            Assert.Null(LayoutFormModel.ReadCustomProperty(control, "DataProvider"));
            Assert.Null(LayoutFormModel.ReadCustomProperty(DataItem("x", null, null), "ControlType"));
        }

        // ------------------------------------------- failures that must not be swallowed

        [Fact]
        public void TryParseWriteResponse_AnswerThatIsNotJson_IsRefusedWithTheParseFailure()
        {
            Assert.False(ControlParametersService.TryParseWriteResponse("<html>502</html>", out var write, out string failure));
            Assert.Null(write);
            Assert.False(string.IsNullOrWhiteSpace(failure));
            Assert.False(ControlParametersService.TryParseWriteResponse(null, out _, out _));
            Assert.True(ControlParametersService.TryParseWriteResponse("{\"status\":\"ok\"}", out var ok, out _));
            Assert.Equal("ok", ok["status"]?.ToString());
        }

        [Fact]
        public void ReadErrorMessage_OfAnUnreadableEnvelope_SaysItIsNotJson()
        {
            Assert.Equal("boom", ControlParametersService.ReadErrorMessage("{\"error\":{\"message\":\"boom\"}}"));
            Assert.Contains("not valid JSON", ControlParametersService.ReadErrorMessage("garbage"));
        }

        [Fact]
        public void DescribeLayoutDifference_OfAnUnparseableForm_ReportsTheParseErrorInsteadOfMaskingIt()
        {
            string valid = "<GxMultiForm><Form type=\"layout\"/></GxMultiForm>";

            Assert.Contains("parse error", ControlParametersService.DescribeLayoutDifference("<broken", valid));
            Assert.Contains("parse error", ControlParametersService.DescribeLayoutDifference(valid, "<broken"));
        }

        [Fact]
        public void DescribeStoredDataProvider_ALookupThatFails_IsReportedBesideTheStoredValue()
        {
            const string stored = "11111111-2222-3333-4444-555555555555-42";

            var failed = ControlParametersService.DescribeStoredDataProvider(stored, (guid, id) => throw new InvalidOperationException("model is closed"));
            var found = ControlParametersService.DescribeStoredDataProvider(stored, (guid, id) => id == 42 ? "SampleProvider" : null);
            var unknown = ControlParametersService.DescribeStoredDataProvider(stored, (guid, id) => null);

            Assert.Equal(stored, failed["stored"]?.ToString());
            Assert.Equal("model is closed", failed["nameLookupError"]?.ToString());
            Assert.Null(failed["name"]);
            Assert.Equal("SampleProvider", found["name"]?.ToString());
            Assert.Null(found["nameLookupError"]);
            Assert.Null(unknown["name"]);
            Assert.Null(unknown["nameLookupError"]);
        }

        public sealed class FakeVariable
        {
            private readonly string _name;
            public FakeVariable(string name) { _name = name; }
            public string Name => _name ?? throw new InvalidOperationException("variable name is unreadable");
        }

        [Fact]
        public void ReadVariableNames_StripsTheAmpersand_AndRefusesAListWithAnUnreadableName()
        {
            var names = ControlParametersService.ReadVariableNames(new[] { new FakeVariable("&Total"), new FakeVariable("Count") });

            Assert.True(names.SetEquals(new[] { "Total", "Count" }));
            Assert.Throws<InvalidOperationException>(() =>
                ControlParametersService.ReadVariableNames(new[] { new FakeVariable("Total"), new FakeVariable(null) }));
        }

        // -------------------------------------- the pending EditableToStored flag

        private sealed class FakeWebFormPart
        {
            private bool m_EditableToStoredNeeded = true;
            private string m_EditableContent = "stale editable text";
            private XmlDocument m_Document = Create("<GxMultiForm><Form type=\"layout\"/></GxMultiForm>");
            public XmlDocument Document => m_Document;
            public bool Needed => m_EditableToStoredNeeded;
            public string Editable => m_EditableContent;

            private static XmlDocument Create(string xml) { var d = new XmlDocument(); d.LoadXml(xml); return d; }
        }

        [Fact]
        public void StoredFormXml_NoLongerLeavesTheEditableToStoredReplayPending()
        {
            var part = new FakeWebFormPart();

            WebFormXmlHelper.ClearPendingEditableToStored(part);

            Assert.False(part.Needed);
            Assert.Equal(part.Document.OuterXml, part.Editable);
        }

        [Fact]
        public void StoredFormXml_OnlyALayoutFormClearsTheReplay()
        {
            var layout = new XmlDocument();
            layout.LoadXml("<GxMultiForm><Form id=\"2\" type=\"layout\"><detail><layout/></detail></Form></GxMultiForm>");
            var html = new XmlDocument();
            html.LoadXml("<GxMultiForm><Form id=\"1\" type=\"html\"><BODY/></Form></GxMultiForm>");

            Assert.True(WebFormXmlHelper.IsLayoutFormPart(new FakePartWith(layout)));
            Assert.False(WebFormXmlHelper.IsLayoutFormPart(new FakePartWith(html)));
            Assert.False(WebFormXmlHelper.IsLayoutFormPart(new object()));
            Assert.False(WebFormXmlHelper.IsLayoutFormPart(null));
        }

        private sealed class FakePartWith
        {
            private readonly XmlDocument m_Document;
            public FakePartWith(XmlDocument document) { m_Document = document; }
            public XmlDocument Document => m_Document;
        }

        [Fact]
        public void StoredFormXml_APartWithoutTheFlagIsLeftAlone()
        {
            WebFormXmlHelper.ClearPendingEditableToStored(new object());
            WebFormXmlHelper.ClearPendingEditableToStored(null);
        }

        // ------------------------------------------------------ property value text

        [Fact]
        public void PropertyValue_OfAParameterList_IsItsText_NotTheClrTypeName()
        {
            var value = new Artech.Genexus.Common.CustomTypes.ObjectParametersType(
                new Artech.Genexus.Common.CustomTypes.ParseableString(null, "McpCbFilterA, McpCbFilterB"));

            Assert.Equal("McpCbFilterA, McpCbFilterB", PropertyService.RenderPropertyValue(value, null));
        }

        [Fact]
        public void PropertyValue_OfAnEmptyParameterList_IsEmpty()
        {
            var value = new Artech.Genexus.Common.CustomTypes.ParseableString(null, null);

            Assert.Equal(string.Empty, PropertyService.RenderPropertyValue(value, null));
        }

        // ----------------------------------------------- the dispatcher routing

        [Fact]
        public void Parameters_AreRoutedByPropertyName_NotByAGuessedSpelling()
        {
            Assert.True(ControlParametersService.IsParametersProperty("DataProviderParameters"));
            Assert.True(ControlParametersService.IsParametersProperty(" dataproviderparameters "));
            Assert.False(ControlParametersService.IsParametersProperty("DataProvider"));
            Assert.False(ControlParametersService.IsParametersProperty(null));
        }

        // ------------------------------------------------------------ variables

        [Fact]
        public void VariableNames_AreTakenOutsideQuotedLiterals_InOrderAndDistinct()
        {
            var names = ParameterListText.ExtractVariableNames("Fn(&Pgmname, &today) + '&Quoted' + \"&AlsoQuoted\" + &Pgmname + &Mode");

            Assert.Equal(new[] { "Pgmname", "today", "Mode" }, names);
        }

        [Fact]
        public void VariableNames_OfAnExpressionWithoutThem_AreEmpty()
        {
            Assert.Empty(ParameterListText.ExtractVariableNames("McpCbId"));
            Assert.Empty(ParameterListText.ExtractVariableNames(null));
            Assert.Empty(ParameterListText.ExtractVariableNames("'&x'"));
        }

        [Fact]
        public void UndeclaredVariables_AreReportedWithTheirIndex_AndDeclaredOnesAreNot()
        {
            var declared = new HashSet<string>(new[] { "Today", "Mode" }, StringComparer.OrdinalIgnoreCase);

            var problems = ControlParametersService.FindUndeclaredVariables(
                new[] { "A", "&today", "&NoSuchVar" }, new[] { "A", "&today", "&NoSuchVar" }, declared);

            var problem = Assert.Single(problems);
            Assert.Equal(2, problem.Index);
            Assert.Equal("UnknownVariableReference", problem.Code);
            Assert.Contains("&NoSuchVar", problem.Message);
        }

        [Fact]
        public void UndeclaredVariables_WhenTheVariablesPartCannotBeRead_AreNotGuessed()
        {
            Assert.Empty(ControlParametersService.FindUndeclaredVariables(new[] { "&Whatever" }, new[] { "&Whatever" }, null));
        }

        // ----------------------------------------------------------- the token

        [Fact]
        public void Version_AcceptsTheFormTokenAndTheObjectLevelTokenOnly()
        {
            const string form = "639280960040000000:afa47377:62ee948c";
            Func<string> objectToken = () => "639280960040000000:afa47377";

            Assert.True(ControlParametersService.VersionMatches(form, form, objectToken));
            Assert.True(ControlParametersService.VersionMatches("639280960040000000:afa47377", form, objectToken));
            Assert.False(ControlParametersService.VersionMatches("639280952760000000:afa47377:62ee948c", form, objectToken)); // obsolete form token
            Assert.False(ControlParametersService.VersionMatches("639280952760000000:afa47377", form, objectToken));          // obsolete object token
            Assert.False(ControlParametersService.VersionMatches("garbage", form, objectToken));
            Assert.False(ControlParametersService.VersionMatches("", form, objectToken));
            Assert.False(ControlParametersService.VersionMatches(null, form, objectToken));
        }

        [Fact]
        public void Version_AnObjectTokenThatCannotBeComputed_MatchesNothing()
        {
            Assert.False(ControlParametersService.VersionMatches("1:abc", "1:abc:def", () => null));
        }

        // ---------------------------------------------------- the failure seam

        [Fact]
        public void VerificationSeam_IsOffByDefault()
        {
            Assert.Null(ControlParametersService.InjectedVerificationFailure);
        }

        // ------------------------------------------------- wiring (by convention)

        private static string Source(params string[] relative)
        {
            var parts = new[] { TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker" }.Concat(relative).ToArray();
            return System.IO.File.ReadAllText(System.IO.Path.Combine(parts));
        }

        [Fact]
        public void Wiring_TheDispatcherRoutesParameterReadsAndWritesToTheTypedService()
        {
            string dispatcher = Source("Services", "CommandDispatcher.cs");

            Assert.Contains("return _controlParametersService.Set(", dispatcher);
            Assert.Contains("return _controlParametersService.Get(", dispatcher);
            // an explicit "propertyNames": null from the Gateway is not a name list
            Assert.Contains("!Helpers.JsonUtil.IsSupplied(args?[\"propertyNames\"])", dispatcher);
            // a scalar value for the parameter list is refused, not written through the generic setter
            Assert.Contains("\"ExpressionsRequired\"", dispatcher);
        }

        [Fact]
        public void Wiring_ARawRewriteClearsThePendingEditableToStoredReplay()
        {
            string helper = Source("Helpers", "WebFormXmlHelper.cs");
            int push = helper.IndexOf("private static void PushDocumentToStoredModel", StringComparison.Ordinal);
            Assert.True(push > 0);

            Assert.Contains("if (IsLayoutFormPart(part)) ClearPendingEditableToStored(part);", helper.Substring(push));
        }

        [Fact]
        public void Wiring_LayoutControlsAreListedAndResolvedThroughTheLayoutModel()
        {
            string ui = Source("Services", "UIService.cs");
            string properties = Source("Services", "PropertyService.cs");

            Assert.Contains("LayoutFormModel.IsLayoutForm(part.Document)", ui);
            Assert.Contains("AddLayoutControls(obj, part.Document, arr);", ui);
            Assert.Contains("LayoutFormModel.IsLayoutOnlyReference(trimmed)", properties);
            Assert.Contains("return ResolveLayoutControl(obj, trimmed);", properties);
        }

        [Fact]
        public void Wiring_AScalarWriteThroughALayoutControlIsRefused_InBothSetters()
        {
            string properties = Source("Services", "PropertyService.cs");

            // Only the Conditions of a layout control (issue #770) are delegated; everything else stays refused.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(properties, @"IsLayoutControlContainer\(\(object\)container\)\)").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(properties, @"LayoutControlWriteUnsupported\(target, controlName,").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(properties, @"ControlConditionsService\.IsConditionsProperty\(").Count);
        }

        [Fact]
        public void Wiring_TheStalePropertyCacheIsDroppedAfterAWriteAndAfterARestore()
        {
            string service = Source("Services", "ControlParametersService.cs");

            // genexus_properties get answers from a short-lived cache keyed by object and control
            Assert.True(System.Text.RegularExpressions.Regex.Matches(service, @"PropertyService\.InvalidatePropertyCache\(").Count >= 3);
        }

        [Fact]
        public void Wiring_TheWriteGoesThroughTheVersionCheckedRollbackPipeline()
        {
            string service = Source("Services", "ControlParametersService.cs");

            Assert.Contains("rollbackOnFailure: true", service);
            Assert.Contains("baseVersion: before.CurrentVersion", service);
            Assert.Contains("forceWrite: false", service);
            Assert.DoesNotContain("forceWrite: true", service);
        }
    }

    internal static class PayloadTestExtensions
    {
        internal static string WithAll(this CustomPropertiesPayload _, params (string name, string value)[] properties)
        {
            var root = new System.Xml.Linq.XElement("Properties");
            foreach (var (name, value) in properties)
                root.Add(new System.Xml.Linq.XElement("Property",
                    new System.Xml.Linq.XElement("Name", name), new System.Xml.Linq.XElement("Value", value)));
            return root.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        }
    }
}
