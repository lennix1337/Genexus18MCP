using System;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Element = GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element;
using Part = GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Part;

namespace GxMcp.Worker.Tests
{
    // Issue #595: the native add_table / move_control command sequence against the fake
    // element tree the other WWP native tests use. It proves the commands, the ordering-list
    // writes and the refusals, not the WorkWithPlus package.
    public class WwpFormLayoutNativeCommandTests
    {
        private const string Xml =
            "<instance><transaction><table name='TableMain' childrenOrderedList='2;01;TableContent'>" +
            "<table name='TableContent' childrenOrderedList='2;28;ErrorViewer-2;01;TableAttributes'><errorViewer/>" +
            "<table name='TableAttributes' childrenOrderedList='2;22;A-2;27;Legend'>" +
            "<attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-A' description='A'/>" +
            "<textBlock controlName='Legend' caption='[A] Altera'/>" +
            "</table></table></table></transaction></instance>";

        private static Element Child(Element parent, string type, params (string, object)[] attributes)
        {
            Element child = parent.Children.CreateChildElement(type);
            foreach ((string name, object value) in attributes) child.Attributes.SetPropertyValue(name, value);
            parent.Children.Items.Add(child);
            return child;
        }

        // The native twin of Xml.
        private static Part Form(out Element content, out Element attributes)
        {
            var part = new Part();
            Element transaction = Child(part.RootElement, "transaction");
            Element main = Child(transaction, "table", ("name", "TableMain"), ("childrenOrderedList", "2;01;TableContent"));
            content = Child(main, "table", ("name", "TableContent"), ("childrenOrderedList", "2;28;ErrorViewer-2;01;TableAttributes"));
            Child(content, "errorViewer");
            attributes = Child(content, "table", ("name", "TableAttributes"), ("childrenOrderedList", "2;22;A-2;27;Legend"));
            Child(attributes, "attribute", ("attribute", "adbb33c9-0906-4971-833c-998de27e0676-A"), ("description", "A"));
            Child(attributes, "textBlock", ("controlName", "Legend"), ("caption", "[A] Altera"));
            return part;
        }

        private static JObject Plan(JObject args, Func<XDocument, JObject, JObject> planner)
        {
            JObject plan = planner(XDocument.Parse(Xml), args);
            Assert.Null(plan["error"]);
            return plan;
        }

        [Fact]
        public void NativeAddTable_CreatesTheTableInsideTheUpdateWithChildrenPlacementAndOrdering()
        {
            Part part = Form(out Element content, out Element attributes);
            JObject args = JObject.Parse("{tableName:'tableOp',numberOfColumns:3,panelTitle:'Options',before:'TableAttributes',children:[" +
                "{type:'textBlock',controlName:'opcoes1',caption:'[C] Consulta'},{type:'attribute',attribute:'A',description:'Renamed'}]}");
            JObject plan = Plan(args, WwpActionService.PlanAddTableXml);
            Element existing = attributes.Children.Items[0];

            JObject result = WwpActionService.ApplyNativeAddTable(part, args, plan);

            Assert.Null(result["error"]);
            Assert.Equal(1, part.Updates);
            Assert.Contains("InsertElementCommand", (string)result["sdkOperation"]);
            Element table = content.Children.Items[1];
            Assert.Equal("table", table.Type);
            Assert.Equal("tableOp", table.Attributes.GetPropertyValueString("name"));
            Assert.Equal("Responsive", table.Attributes.GetPropertyValueString("type"));
            Assert.Equal("Responsive", table.Attributes.GetPropertyValueString("defaultType"));
            Assert.IsType<int>(table.Attributes.GetPropertyValue("numberOfColumns"));
            Assert.Equal(3, table.Attributes.GetPropertyValue("numberOfColumns"));
            // The pattern declares includeInPanel as bool: a string value makes the WorkWithPlus table
            // element fail its cast when it reads the property back (issue #638).
            Assert.IsType<bool>(table.Attributes.GetPropertyValue("includeInPanel"));
            Assert.Equal(true, table.Attributes.GetPropertyValue("includeInPanel"));
            Assert.IsType<bool>(table.Attributes.GetPropertyValue("defaultIncludeInPanel"));
            Assert.Equal("Options", table.Attributes.GetPropertyValueString("panelTitle"));
            Assert.Equal(new[] { "textBlock", "attribute" }, table.Children.Items.Select(c => c.Type));
            Assert.Equal("[C] Consulta", table.Children.Items[0].Attributes.GetPropertyValueString("caption"));
            Assert.Same(existing, table.Children.Items[1]);
            Assert.Equal("Renamed", existing.Attributes.GetPropertyValueString("description"));
            Assert.Equal(new[] { "textBlock" }, attributes.Children.Items.Select(c => c.Type));
            Assert.Equal("2;28;ErrorViewer-2;01;tableOp-2;01;TableAttributes", content.Attributes.GetPropertyValueString("childrenOrderedList"));
            Assert.Equal("2;27;Legend", attributes.Attributes.GetPropertyValueString("childrenOrderedList"));
        }

        [Fact]
        public void NativeAddTable_RefusesWhenTheNativeOrderingDiffersFromThePersistedXml()
        {
            Part part = Form(out Element content, out _);
            content.Attributes.SetPropertyValue("childrenOrderedList", "2;28;ErrorViewer");
            JObject args = JObject.Parse("{tableName:'tableOp'}");

            JObject result = WwpActionService.ApplyNativeAddTable(part, args, Plan(args, WwpActionService.PlanAddTableXml));

            Assert.Equal("WwpOrderingMetadataMismatch", (string)result["code"]);
            Assert.Equal(0, part.Updates);
            Assert.Equal(2, content.Children.Items.Count);
        }

        [Theory]
        [InlineData("{tableName:'TableContent'}", "TableAlreadyExists")]
        [InlineData("{tableName:'tableOp',containerName:'Nope'}", "TableContainerNotFound")]
        public void NativeAddTable_RefusesBeforeAnyUpdate(string json, string code)
        {
            Part part = Form(out Element content, out _);
            JObject result = WwpActionService.ApplyNativeAddTable(part, JObject.Parse(json), null);

            Assert.Equal(code, (string)result["code"]);
            Assert.Equal(0, part.Updates);
            Assert.Equal(2, content.Children.Items.Count);
        }

        [Fact]
        public void NativeAddTable_WithoutAListOnTheContainer_AppendsAndWritesNoList()
        {
            var part = new Part();
            Element content = Child(part.RootElement, "table", ("name", "TableContent"));
            Child(content, "variable", ("name", "X"));
            JObject args = JObject.Parse("{tableName:'tableOp'}");
            JObject plan = WwpActionService.PlanAddTableXml(XDocument.Parse("<instance><table name='TableContent'><variable name='X'/></table></instance>"), args);

            JObject result = WwpActionService.ApplyNativeAddTable(part, args, plan);

            Assert.Null(result["error"]);
            Assert.Contains("AddElementCommand", (string)result["sdkOperation"]);
            Assert.Equal("tableOp", content.Children.Items[1].Attributes.GetPropertyValueString("name"));
            Assert.Null(content.Attributes.GetPropertyValue("childrenOrderedList"));
        }

        [Fact]
        public void NativeMoveControl_MovesTheSameElementAndUpdatesBothLists()
        {
            Part part = Form(out Element content, out Element attributes);
            Element target = Child(content, "table", ("name", "tableOp"), ("childrenOrderedList", "2;27;opcoes1"));
            Child(target, "textBlock", ("controlName", "opcoes1"), ("caption", "[C]"));
            content.Attributes.SetPropertyValue("childrenOrderedList", "2;28;ErrorViewer-2;01;TableAttributes-2;01;tableOp");
            string xml = Xml.Replace("</table></table></table></transaction>",
                "</table><table name='tableOp' childrenOrderedList='2;27;opcoes1'><textBlock controlName='opcoes1' caption='[C]'/></table></table></table></transaction>")
                .Replace("childrenOrderedList='2;28;ErrorViewer-2;01;TableAttributes'", "childrenOrderedList='2;28;ErrorViewer-2;01;TableAttributes-2;01;tableOp'");
            JObject args = JObject.Parse("{controlName:'Legend',tableName:'tableOp',position:0}");
            JObject plan = WwpActionService.PlanMoveControlXml(XDocument.Parse(xml), args);
            Assert.Null(plan["error"]);
            Element legend = attributes.Children.Items[1];

            JObject result = WwpActionService.ApplyNativeMoveControl(part, args, plan);

            Assert.Null(result["error"]);
            Assert.Equal(1, part.Updates);
            Assert.Same(legend, target.Children.Items[0]);
            // Between two containers the element changes parent through MoveElement; Remove + Add alone
            // leave Parent on the old table and the SDK refuses the add (issue #638).
            Assert.Same(target, legend.Parent);
            Assert.Equal("PatternInstanceElement.MoveElement + MoveElementCommand", (string)result["sdkOperation"]);
            Assert.Equal(new[] { "Legend", "opcoes1" }, target.Children.Items.Select(c => c.Attributes.GetPropertyValueString("controlName")));
            Assert.Equal(new[] { "attribute" }, attributes.Children.Items.Select(c => c.Type));
            Assert.Equal("2;22;A", attributes.Attributes.GetPropertyValueString("childrenOrderedList"));
            Assert.Equal("2;27;Legend-2;27;opcoes1", target.Attributes.GetPropertyValueString("childrenOrderedList"));
        }

        [Fact]
        public void NativeMoveControl_WithoutPlacementBetweenContainers_AppendsTheReparentedElement()
        {
            Part part = Form(out Element content, out Element attributes);
            Element target = Child(content, "table", ("name", "tableOp"));
            Child(target, "textBlock", ("controlName", "opcoes1"), ("caption", "[C]"));
            string xml = Xml.Replace("</table></table></table></transaction>",
                "</table><table name='tableOp'><textBlock controlName='opcoes1' caption='[C]'/></table></table></table></transaction>");
            JObject args = JObject.Parse("{controlName:'Legend',tableName:'tableOp'}");
            JObject plan = WwpActionService.PlanMoveControlXml(XDocument.Parse(xml), args);
            Assert.Null(plan["error"]);
            Element legend = attributes.Children.Items[1];

            JObject result = WwpActionService.ApplyNativeMoveControl(part, args, plan);

            Assert.Null(result["error"]);
            Assert.Same(target, legend.Parent);
            Assert.Same(legend, target.Children.Items.Last());
            Assert.Equal("PatternInstanceElement.MoveElement", (string)result["sdkOperation"]);
            Assert.DoesNotContain(legend, attributes.Children.Items);
        }

        [Fact]
        public void NativeMoveControl_InsideOneContainer_RepositionsWithoutChangingTheParent()
        {
            Part part = Form(out _, out Element attributes);
            JObject args = JObject.Parse("{controlName:'Legend',tableName:'TableAttributes',position:0}");
            JObject plan = WwpActionService.PlanMoveControlXml(XDocument.Parse(Xml), args);
            Assert.Null(plan["error"]);
            Element legend = attributes.Children.Items[1];

            JObject result = WwpActionService.ApplyNativeMoveControl(part, args, plan);

            Assert.Null(result["error"]);
            Assert.Same(attributes, legend.Parent);
            Assert.Same(legend, attributes.Children.Items[0]);
            Assert.Equal("RemoveElementCommand + InsertElementCommand", (string)result["sdkOperation"]);
        }

        [Theory]
        [InlineData("{controlName:'Nope',tableName:'TableContent'}", "ControlNotFound")]
        [InlineData("{controlName:'Legend',tableName:'Nope'}", "WwpTableNotFound")]
        [InlineData("{controlName:'Legend',tableName:'Actions'}", "MoveTargetNotTable")]
        [InlineData("{controlName:'Legend',tableName:'TableAttributes'}", "ControlAlreadyInTable")]
        public void NativeMoveControl_RefusesBeforeAnyUpdate(string json, string code)
        {
            Part part = Form(out Element content, out Element attributes);
            Child(content, "actionGroup", ("name", "Actions"));

            JObject result = WwpActionService.ApplyNativeMoveControl(part, JObject.Parse(json), null);

            Assert.Equal(code, (string)result["code"]);
            Assert.Equal(0, part.Updates);
            Assert.Equal(2, attributes.Children.Items.Count);
        }
    }
}
