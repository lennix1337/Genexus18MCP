using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #595: add_table and move_control on the XML projection - the plan a dryRun shows,
    // the refusals, the childrenOrderedList maintenance and the post-save verification. The
    // native command sequence is covered by WwpFormLayoutNativeCommandTests; whether the
    // DVelop package accepts the result on a real KB is not proven here.
    public class WwpFormLayoutTests
    {
        private const string WebPanel =
            "<instance><WPRoot><table name='TableMain' type='Responsive' childrenOrderedList='2;01;TableContent'>" +
            "<table name='TableContent' type='Responsive' childrenOrderedList='2;28;ErrorViewer-2;27;TxtTitle'>" +
            "<errorViewer/><textBlock controlName='TxtTitle' caption='Title'/><variable name='FromDate' dataType='Basic' basicType='Date'/>" +
            "</table></table></WPRoot></instance>";

        private const string Transaction =
            "<instance><transaction><table name='TableMain' childrenOrderedList='2;01;TableContent-2;01;TableActions'>" +
            "<table name='TableContent' childrenOrderedList='2;28;ErrorViewer-2;01;TableAttributes'><errorViewer/>" +
            "<table name='TableAttributes' childrenOrderedList='2;22;A-2;27;Legend-2;22;B'>" +
            "<attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-A' description='A' cellThemeClass='Cell'/>" +
            "<textBlock controlName='Legend' caption='[A] Altera'/>" +
            "<attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-B'/>" +
            "</table></table><table name='TableActions' childrenOrderedList='2;18;Trn_Enter'><standardAction name='Trn_Enter'/></table>" +
            "</table></transaction></instance>";

        private static JObject AddTable(XDocument doc, string extra = null) =>
            WwpActionService.PlanAddTableXml(doc, Args("{tableName:'tableOp',numberOfColumns:3" + (extra == null ? "" : "," + extra) + "}"));

        private static JObject Args(string json) => JObject.Parse(json);

        private static XElement Table(XDocument doc, string name) =>
            doc.Descendants("table").Single(t => (string)t.Attribute("name") == name);

        [Fact]
        public void AddTable_DefaultsToTableContentAndMaintainsItsOrderedList()
        {
            var doc = XDocument.Parse(WebPanel);
            JObject plan = AddTable(doc);

            Assert.Null(plan["error"]);
            XElement table = Table(doc, "tableOp");
            Assert.Equal("TableContent", (string)table.Parent.Attribute("name"));
            Assert.Equal(3, table.Parent.Elements().ToList().IndexOf(table));
            Assert.Equal(3, (int)plan["position"]);
            Assert.Equal("TableMain/TableContent", (string)plan["containerPath"]);
            Assert.Equal("Responsive", (string)table.Attribute("type"));
            Assert.Equal("Responsive", (string)table.Attribute("defaultType"));
            Assert.Equal("3", (string)table.Attribute("numberOfColumns"));
            Assert.Null(table.Attribute("childrenOrderedList"));
            Assert.Null(table.Attribute("includeInPanel"));
            Assert.Equal("2;28;ErrorViewer-2;27;TxtTitle-2;01;tableOp", (string)table.Parent.Attribute("childrenOrderedList"));
            Assert.Equal("2;01;tableOp", (string)plan["childrenOrderedList"]["entry"]);
            Assert.True((bool)plan["childrenOrderedList"]["changed"]);
            Assert.Contains("name=\"tableOp\"", (string)plan["typedDiff"]["table"]);
            Assert.DoesNotContain("tableOp", (string)plan["typedDiff"]["before"]);
            Assert.Contains("tableOp", (string)plan["typedDiff"]["after"]);
        }

        [Fact]
        public void AddTable_InTransaction_WithTextBlocksBeforeTheAttributesTable()
        {
            var doc = XDocument.Parse(Transaction);
            JObject plan = AddTable(doc, "before:'TableAttributes',children:[" +
                "{type:'textBlock',controlName:'opcoes1',caption:'[C] Consulta'}," +
                "{type:'textBlock',controlName:'opcoes2',caption:'[D] Detalhes'}," +
                "{type:'textBlock',controlName:'opcoes3',caption:'[V] Devolvido'}]");

            Assert.Null(plan["error"]);
            XElement table = Table(doc, "tableOp");
            Assert.Equal("TableContent", (string)table.Parent.Attribute("name"));
            Assert.Equal(1, (int)plan["position"]);
            Assert.Equal(new[] { "opcoes1", "opcoes2", "opcoes3" }, table.Elements("textBlock").Select(t => (string)t.Attribute("controlName")));
            Assert.Equal("[D] Detalhes", (string)table.Elements("textBlock").ElementAt(1).Attribute("caption"));
            Assert.Equal("2;28;ErrorViewer-2;01;tableOp-2;01;TableAttributes", (string)table.Parent.Attribute("childrenOrderedList"));
            Assert.Equal("TableAttributes", (string)plan["childrenOrderedList"]["insertedBefore"]);
            Assert.Empty((JArray)plan["movedAttributes"]);
            Assert.Equal(3, plan["children"].Count());
            Assert.Equal("opcoes1", (string)plan["children"][0]["controlName"]);
        }

        [Fact]
        public void AddTable_AttributeChild_MovesTheAttributeAndDropsItFromTheSourceList()
        {
            var doc = XDocument.Parse(Transaction);
            JObject plan = AddTable(doc, "children:[{type:'attribute',attribute:'A',description:'Renamed'}]");

            Assert.Null(plan["error"]);
            XElement moved = Table(doc, "tableOp").Elements("attribute").Single();
            Assert.Equal("adbb33c9-0906-4971-833c-998de27e0676-A", (string)moved.Attribute("attribute"));
            Assert.Equal("Cell", (string)moved.Attribute("cellThemeClass"));
            Assert.Equal("Renamed", (string)moved.Attribute("description"));
            XElement source = Table(doc, "TableAttributes");
            Assert.Equal("2;27;Legend-2;22;B", (string)source.Attribute("childrenOrderedList"));
            Assert.Single(doc.Descendants("attribute"), a => ((string)a.Attribute("attribute")).EndsWith("-A"));
            JObject entry = (JObject)plan["movedAttributes"].Single();
            Assert.Equal("TableMain/TableContent/TableAttributes", (string)entry["from"]);
            Assert.Equal("2;22;A", (string)entry["childrenOrderedList"]["entry"]);
            Assert.Equal("A", (string)entry["orderingIdentifier"]);
            Assert.Equal(new[] { 0, 0, 0, 1 }, entry["sourceIndexPath"].Select(t => (int)t));
        }

        [Fact]
        public void AddTable_ExplicitContainerPathAndPanel()
        {
            var doc = XDocument.Parse(Transaction);
            JObject plan = AddTable(doc, "containerName:'TableMain/TableActions',tableType:'Regular',panelTitle:'Options'");

            Assert.Null(plan["error"]);
            XElement table = Table(doc, "tableOp");
            Assert.Equal("TableActions", (string)table.Parent.Attribute("name"));
            Assert.Equal("Regular", (string)table.Attribute("type"));
            Assert.Equal("True", (string)table.Attribute("includeInPanel"));
            Assert.Equal("True", (string)table.Attribute("defaultIncludeInPanel"));
            Assert.Equal("Options", (string)table.Attribute("panelTitle"));
            Assert.Equal("2;18;Trn_Enter-2;01;tableOp", (string)table.Parent.Attribute("childrenOrderedList"));
        }

        [Theory]
        [InlineData("{tableName:'TableContent'}", "TableAlreadyExists")]
        [InlineData("{tableName:'tableOp',children:[{type:'textBlock',controlName:'Legend',caption:'x'}]}", "ControlNameAlreadyExists")]
        [InlineData("{tableName:'tableOp',children:[{type:'table',name:'TableActions'}]}", "TableAlreadyExists")]
        [InlineData("{tableName:'tableOp',children:[{type:'textBlock',controlName:'T1'},{type:'variable',name:'T1'}]}", "InvalidWwpChild")]
        [InlineData("{tableName:'tableOp',children:[{type:'textBlock'}]}", "InvalidWwpChild")]
        [InlineData("{tableName:'tableOp',children:[{type:'gridVariable',name:'x'}]}", "InvalidWwpChildType")]
        [InlineData("{}", "MissingTableName")]
        [InlineData("{tableName:'1op'}", "InvalidTableName")]
        [InlineData("{tableName:'tableOp',numberOfColumns:0}", "InvalidNumberOfColumns")]
        [InlineData("{tableName:'tableOp',tableType:'Fluid'}", "InvalidTableType")]
        [InlineData("{tableName:'tableOp',panelTitle:'x',includeInPanel:false}", "InvalidPanelTitle")]
        [InlineData("{tableName:'tableOp',position:0,before:'TableAttributes'}", "ConflictingPlacement")]
        [InlineData("{tableName:'tableOp',before:'Nope'}", "PlacementSiblingNotFound")]
        public void AddTable_RefusesWithoutTouchingTheForm(string args, string code)
        {
            var doc = XDocument.Parse(Transaction);
            JObject plan = WwpActionService.PlanAddTableXml(doc, Args(args));

            Assert.Equal(code, (string)plan["code"]);
            Assert.True(XNode.DeepEquals(XDocument.Parse(Transaction), doc), "the form must be untouched after a refusal");
        }

        [Fact]
        public void AddTable_CollisionAndPlacementErrorsAreActionable()
        {
            JObject collision = WwpActionService.PlanAddTableXml(XDocument.Parse(Transaction),
                Args("{tableName:'tableOp',children:[{type:'textBlock',controlName:'Legend'}]}"));
            Assert.Contains("move_control", (string)collision["hint"]);

            JObject placement = WwpActionService.PlanAddTableXml(XDocument.Parse(Transaction), Args("{tableName:'tableOp',before:'Nope'}"));
            Assert.Equal(new[] { "ErrorViewer", "TableAttributes" }, placement["availableSiblings"].Values<string>());
        }

        [Fact]
        public void AddTable_DoesNotPickAnArbitraryTable()
        {
            var doc = XDocument.Parse("<instance><selection><table name='TableFilters'/></selection></instance>");
            JObject plan = AddTable(doc);

            Assert.Equal("TableContainerNotFound", (string)plan["code"]);
            Assert.Contains("TableFilters", plan["availablePaths"].Values<string>());
            Assert.Contains("containerName", (string)plan["hint"]);
            Assert.DoesNotContain(doc.Descendants("table"), t => (string)t.Attribute("name") == "tableOp");

            Assert.Equal("TableContainerNotFound", (string)AddTable(doc, "containerName:'Nope'")["code"]);
            Assert.Null(AddTable(doc, "containerName:'TableFilters'")["error"]);
            Assert.Equal("TableFilters", (string)Table(doc, "tableOp").Parent.Attribute("name"));
        }

        [Fact]
        public void AddTable_AmbiguousDefaultContainerListsEachPath()
        {
            var doc = XDocument.Parse("<instance><tab name='A'><table name='TableContent'/></tab><tab name='B'><table name='TableContent'/></tab></instance>");
            JObject plan = AddTable(doc);

            Assert.Equal("TableContainerAmbiguous", (string)plan["code"]);
            Assert.Equal(new[] { "A/TableContent", "B/TableContent" }, plan["matchingContainers"].Select(c => (string)c["path"]));
            Assert.Null(AddTable(doc, "containerName:'B/TableContent'")["error"]);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("2;28;ErrorViewer-2;01;tableOp")]
        public void AddTable_OrderedContainerTheMcpCannotMaintain_IsRefused(string list)
        {
            string xml = WebPanel.Replace("childrenOrderedList='2;28;ErrorViewer-2;27;TxtTitle'", "childrenOrderedList='" + list + "'");
            var doc = XDocument.Parse(xml);
            JObject plan = AddTable(doc);

            Assert.Equal("WwpOrderedContainerUnsupported", (string)plan["code"]);
            Assert.Equal("TableMain/TableContent", (string)plan["containerPath"]);
            Assert.True(XNode.DeepEquals(XDocument.Parse(xml), doc));
        }

        [Fact]
        public void AddTable_ContainerWithoutOrderedList_IsLeftWithoutOne()
        {
            var doc = XDocument.Parse("<instance><table name='TableContent'><variable name='X'/></table></instance>");
            JObject plan = AddTable(doc, "position:0");

            Assert.Null(plan["error"]);
            Assert.Null(doc.Root.Element("table").Attribute("childrenOrderedList"));
            Assert.Equal(JTokenType.Null, plan["childrenOrderedList"].Type);
            Assert.Equal("tableOp", (string)doc.Root.Element("table").Elements().First().Attribute("name"));
        }

        [Theory]
        [InlineData("before:'TxtTitle'", 1)]
        [InlineData("after:'TxtTitle'", 2)]
        [InlineData("after:'ErrorViewer'", 1)]
        [InlineData("position:0", 0)]
        [InlineData("position:99", 3)]
        public void AddTable_PlacementAddressesSiblingsByIdentity(string placement, int expected)
        {
            var doc = XDocument.Parse(WebPanel);
            JObject plan = AddTable(doc, placement);

            Assert.Null(plan["error"]);
            Assert.Equal(expected, (int)plan["position"]);
            XElement table = Table(doc, "tableOp");
            Assert.Equal(expected, table.Parent.Elements().ToList().IndexOf(table));
            string list = (string)table.Parent.Attribute("childrenOrderedList");
            int listedIndex = PatternChildOrderEdit.Entries(list).ToList().IndexOf("2;01;tableOp");
            Assert.Equal(expected == 0 ? 0 : expected == 1 ? 1 : 2, listedIndex);
        }

        [Fact]
        public void DryRunReceipt_ShowsTheProjectedXmlDiffWithoutPersisting()
        {
            var doc = XDocument.Parse(WebPanel);
            JObject plan = AddTable(doc, "children:[{type:'textBlock',controlName:'opcoes1',caption:'[C]'}]");
            JObject receipt = WwpActionService.BuildFormLayoutReceipt("WorkWithPlusSample", "add_table", plan, "v1", false);

            Assert.Equal("add_table", (string)receipt["operation"]);
            Assert.False((bool)receipt["persisted"]);
            Assert.False((bool)receipt["patternReReadConfirmed"]);
            Assert.Equal("v1", (string)receipt["versionToken"]);
            Assert.Equal("native-pattern-sdk", (string)receipt["mutationMode"]);
            Assert.Contains("<table name=\"tableOp\"", (string)receipt["typedDiff"]["after"]);
            Assert.Contains("controlName=\"opcoes1\"", (string)receipt["typedDiff"]["table"]);
            Assert.DoesNotContain("opcoes1", (string)receipt["typedDiff"]["before"]);
            Assert.Null(receipt["changed"]);
        }

        [Fact]
        public void VerifyAddedTable_AcceptsTheReReadWithSdkDefaultsAndRefusesAnyOtherDrift()
        {
            var planned = XDocument.Parse(Transaction);
            JObject plan = AddTable(planned, "before:'TableAttributes',children:[{type:'attribute',attribute:'A'},{type:'textBlock',controlName:'opcoes1',caption:'[C]'}]");
            Assert.Null(plan["error"]);

            XDocument persisted = Persisted(planned);
            Table(persisted, "tableOp").SetAttributeValue("defaultIncludeInPanel", "False");
            JObject ok = WwpActionService.VerifyAddedTable(planned, persisted, plan);
            Assert.True((bool)ok["confirmed"], (string)ok["message"]);
            Assert.True((bool)ok["orderedListsExact"]);

            XDocument missing = Persisted(planned);
            Table(missing, "tableOp").Remove();
            string missingMessage = (string)WwpActionService.VerifyAddedTable(planned, missing, plan)["message"];
            Assert.Contains("0 time(s)", missingMessage);
            // The refusal says whether the SDK dropped the table or kept it under another name.
            Assert.Contains("Tables expected from the plan: 5; tables in the re-read PatternInstance: 4", missingMessage);

            XDocument renamed = Persisted(planned);
            Table(renamed, "tableOp").SetAttributeValue("name", "tableOp_1");
            string renamedMessage = (string)WwpActionService.VerifyAddedTable(planned, renamed, plan)["message"];
            Assert.Contains("0 time(s)", renamedMessage);
            Assert.Contains("tableOp_1 under 'TableMain/TableContent'", renamedMessage);

            XDocument elsewhere = Persisted(planned);
            XElement table = Table(elsewhere, "tableOp");
            table.Remove();
            Table(elsewhere, "TableActions").Add(table);
            Assert.Contains("instead of 'TableMain/TableContent'", (string)WwpActionService.VerifyAddedTable(planned, elsewhere, plan)["message"]);

            XDocument duplicated = Persisted(planned);
            Table(duplicated, "TableAttributes").AddFirst(new XElement("attribute", new XAttribute("attribute", "adbb33c9-0906-4971-833c-998de27e0676-A")));
            Assert.Contains("2 time(s)", (string)WwpActionService.VerifyAddedTable(planned, duplicated, plan)["message"]);

            XDocument drifted = Persisted(planned);
            drifted.Descendants("textBlock").Single(t => (string)t.Attribute("controlName") == "Legend").SetAttributeValue("caption", "changed");
            Assert.Contains("beyond the requested table", (string)WwpActionService.VerifyAddedTable(planned, drifted, plan)["message"]);

            XDocument unlisted = Persisted(planned);
            Table(unlisted, "TableContent").SetAttributeValue("childrenOrderedList", "2;28;ErrorViewer-2;01;TableAttributes");
            Assert.Contains("does not reference 'tableOp'", (string)WwpActionService.VerifyAddedTable(planned, unlisted, plan)["message"]);

            XDocument stale = Persisted(planned);
            Table(stale, "TableAttributes").SetAttributeValue("childrenOrderedList", "2;22;A-2;27;Legend-2;22;B");
            Assert.Contains("still references 'A'", (string)WwpActionService.VerifyAddedTable(planned, stale, plan)["message"]);

            XDocument reordered = Persisted(planned);
            Table(reordered, "TableContent").SetAttributeValue("childrenOrderedList", "2;01;tableOp-2;28;ErrorViewer-2;01;TableAttributes");
            JObject lenient = WwpActionService.VerifyAddedTable(planned, reordered, plan);
            Assert.True((bool)lenient["confirmed"]);
            Assert.False((bool)lenient["orderedListsExact"]);
        }

        [Fact]
        public void VerifyAddedTableProjection_RequiresTheTableAndItsTextBlocks()
        {
            var doc = XDocument.Parse(WebPanel);
            JObject plan = AddTable(doc, "children:[{type:'textBlock',controlName:'opcoes1',caption:'[C]'}]");

            Assert.True((bool)WwpActionService.VerifyAddedTableProjection(
                "<form><table ControlName='tableOp'><td><textblock ControlName='opcoes1'/></td></table></form>", plan)["confirmed"]);
            JObject noText = WwpActionService.VerifyAddedTableProjection("<form><table ControlName='tableOp'/></form>", plan);
            Assert.False((bool)noText["confirmed"]);
            Assert.Equal(new[] { "opcoes1" }, noText["missingControls"].Values<string>());
            Assert.False((bool)WwpActionService.VerifyAddedTableProjection("<form/>", plan)["targetPresent"]);
            Assert.False((bool)WwpActionService.VerifyAddedTableProjection("", plan)["confirmed"]);
        }

        // ---- move_control ----------------------------------------------------------------

        private const string WithTarget =
            "<instance><transaction><table name='TableMain' childrenOrderedList='2;01;TableContent'>" +
            "<table name='TableContent' childrenOrderedList='2;28;ErrorViewer-2;01;tableOp-2;01;TableAttributes'><errorViewer/>" +
            "<table name='tableOp' numberOfColumns='3' type='Responsive' childrenOrderedList='2;27;opcoes1'><textBlock controlName='opcoes1' caption='[C]'/></table>" +
            "<table name='TableAttributes' childrenOrderedList='2;22;A-2;27;Legend-2;22;B'>" +
            "<attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-A' description='A'/>" +
            "<textBlock controlName='Legend' caption='[A] Altera' themeClass='TextBlock'/>" +
            "<attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-B'/>" +
            "<variable name='Note' dataType='Basic' basicType='VarChar'/>" +
            "</table></table></table></transaction></instance>";

        private static JObject Move(XDocument doc, string args) => WwpActionService.PlanMoveControlXml(doc, Args(args));

        [Fact]
        public void MoveControl_MovesTheTextBlockIntactAndMaintainsBothLists()
        {
            var doc = XDocument.Parse(WithTarget);
            JObject plan = Move(doc, "{controlName:'Legend',tableName:'tableOp'}");

            Assert.Null(plan["error"]);
            XElement target = Table(doc, "tableOp");
            XElement moved = target.Elements("textBlock").Single(t => (string)t.Attribute("controlName") == "Legend");
            Assert.Equal(1, target.Elements().ToList().IndexOf(moved));
            Assert.Equal("TextBlock", (string)moved.Attribute("themeClass"));
            Assert.Empty(Table(doc, "TableAttributes").Elements("textBlock"));
            Assert.Equal("2;22;A-2;22;B", (string)Table(doc, "TableAttributes").Attribute("childrenOrderedList"));
            Assert.Equal("2;27;opcoes1-2;27;Legend", (string)target.Attribute("childrenOrderedList"));

            JObject control = (JObject)plan["movedControls"].Single();
            Assert.Equal("textBlock", (string)control["type"]);
            Assert.Equal("Legend", (string)control["identity"]);
            Assert.Equal("TableMain/TableContent/TableAttributes", (string)control["from"]);
            Assert.Equal("TableMain/TableContent/tableOp", (string)control["to"]);
            Assert.Equal(1, (int)control["position"]);
            Assert.Equal("2;27;Legend", (string)plan["childrenOrderedList"]["source"]["entry"]);
            Assert.Equal("2;27;Legend", (string)plan["childrenOrderedList"]["target"]["entry"]);
            Assert.False((bool)plan["childrenOrderedList"]["sameContainer"]);
            Assert.Contains("Legend", (string)plan["typedDiff"]["sourceBefore"]);
            Assert.DoesNotContain("Legend", (string)plan["typedDiff"]["sourceAfter"]);
            Assert.Contains("Legend", (string)plan["typedDiff"]["targetAfter"]);
        }

        [Fact]
        public void MoveControl_AttributeByBareNameAndVariableByName()
        {
            var doc = XDocument.Parse(WithTarget);
            Assert.Null(Move(doc, "{controlName:'A',tableName:'tableOp',position:0}")["error"]);
            Assert.Equal("adbb33c9-0906-4971-833c-998de27e0676-A", (string)Table(doc, "tableOp").Elements().First().Attribute("attribute"));
            Assert.Equal("2;22;A-2;27;opcoes1", (string)Table(doc, "tableOp").Attribute("childrenOrderedList"));

            JObject variable = Move(doc, "{controlName:'Note',tableName:'tableOp'}");
            Assert.Null(variable["error"]);
            Assert.Equal("variable", (string)variable["control"]["type"]);
            // Variables are not listed in childrenOrderedList; neither list changes.
            Assert.False((bool)variable["childrenOrderedList"]["source"]["changed"]);
            Assert.False((bool)variable["childrenOrderedList"]["target"]["changed"]);
        }

        [Fact]
        public void MoveControl_RepositionsWithinTheSameTableAsOneListChange()
        {
            var doc = XDocument.Parse(WithTarget);
            Assert.Equal("ControlAlreadyInTable", (string)Move(doc, "{controlName:'Legend',tableName:'TableAttributes'}")["code"]);

            JObject plan = Move(doc, "{controlName:'Legend',tableName:'TableAttributes',position:0}");
            Assert.Null(plan["error"]);
            Assert.Equal("Legend", (string)Table(doc, "TableAttributes").Elements().First().Attribute("controlName"));
            Assert.Equal("2;27;Legend-2;22;A-2;22;B", (string)Table(doc, "TableAttributes").Attribute("childrenOrderedList"));
            Assert.True((bool)plan["childrenOrderedList"]["sameContainer"]);
            Assert.Equal("2;22;A-2;27;Legend-2;22;B", (string)plan["childrenOrderedList"]["target"]["before"]);
            Assert.Equal("2;27;Legend-2;22;A-2;22;B", (string)plan["childrenOrderedList"]["target"]["after"]);
        }

        [Theory]
        [InlineData("{controlName:'Nope',tableName:'tableOp'}", "ControlNotFound")]
        [InlineData("{controlName:'Legend',tableName:'Nope'}", "WwpTableNotFound")]
        [InlineData("{controlName:'Legend',tableName:'Actions'}", "MoveTargetNotTable")]
        [InlineData("{controlName:'Legend',tableName:'tableOp',controlKind:'button'}", "InvalidControlKind")]
        [InlineData("{controlName:'Legend',tableName:'tableOp',position:0,after:'opcoes1'}", "ConflictingPlacement")]
        [InlineData("{controlName:'Legend',tableName:'tableOp',before:'Nope'}", "PlacementSiblingNotFound")]
        [InlineData("{tableName:'tableOp'}", "MissingControlName")]
        [InlineData("{controlName:'Legend'}", "MissingTableName")]
        public void MoveControl_RefusesWithoutTouchingTheForm(string args, string code)
        {
            string xml = WithTarget.Replace("<errorViewer/>", "<errorViewer/><actionGroup name='Actions'/>");
            var doc = XDocument.Parse(xml);
            JObject plan = Move(doc, args);

            Assert.Equal(code, (string)plan["code"]);
            Assert.True(XNode.DeepEquals(XDocument.Parse(xml), doc));
        }

        [Fact]
        public void MoveControl_NotFoundAndAmbiguityAreActionable()
        {
            JObject notFound = Move(XDocument.Parse(WithTarget), "{controlName:'Nope',tableName:'tableOp'}");
            Assert.Contains(notFound["availableControls"], c => (string)c["identity"] == "Legend" && (string)c["type"] == "textBlock");

            string xml = WithTarget.Replace("<variable name='Note'", "<variable name='Legend'");
            JObject ambiguous = Move(XDocument.Parse(xml), "{controlName:'Legend',tableName:'tableOp'}");
            Assert.Equal("ControlAmbiguous", (string)ambiguous["code"]);
            Assert.Equal(new[] { "textBlock", "variable" }, ambiguous["matchingControls"].Select(c => (string)c["type"]));
            JObject typed = Move(XDocument.Parse(xml), "{controlName:'Legend',tableName:'tableOp',controlKind:'variable'}");
            Assert.Equal("variable", (string)typed["control"]["type"]);

            JObject tables = Move(XDocument.Parse(WithTarget.Replace("<errorViewer/>", "<errorViewer/><table name='Other'><table name='tableOp'/></table>")),
                "{controlName:'Legend',tableName:'tableOp'}");
            Assert.Equal("WwpTableAmbiguous", (string)tables["code"]);
            Assert.Contains("TableMain/TableContent/Other/tableOp", tables["matchingContainers"].Select(c => (string)c["path"]));
        }

        [Fact]
        public void MoveControl_OrderedSourceTheMcpCannotMaintain_IsRefused()
        {
            string xml = WithTarget.Replace("childrenOrderedList='2;22;A-2;27;Legend-2;22;B'", "childrenOrderedList='broken'");
            var doc = XDocument.Parse(xml);
            JObject plan = Move(doc, "{controlName:'Legend',tableName:'tableOp'}");

            Assert.Equal("WwpOrderedContainerUnsupported", (string)plan["code"]);
            Assert.True(XNode.DeepEquals(XDocument.Parse(xml), doc));
        }

        [Fact]
        public void VerifyMovedControl_RequiresOneIntactControlInTheTargetAndCleanLists()
        {
            var planned = XDocument.Parse(WithTarget);
            JObject plan = Move(planned, "{controlName:'Legend',tableName:'tableOp'}");
            Assert.Null(plan["error"]);

            JObject ok = WwpActionService.VerifyMovedControl(planned, Persisted(planned), plan);
            Assert.True((bool)ok["confirmed"], (string)ok["message"]);

            XDocument stripped = Persisted(planned);
            stripped.Descendants("textBlock").Single(t => (string)t.Attribute("controlName") == "Legend").Attribute("themeClass").Remove();
            Assert.Contains("lost attributes", (string)WwpActionService.VerifyMovedControl(planned, stripped, plan)["message"]);

            XDocument copied = Persisted(planned);
            Table(copied, "TableAttributes").Add(new XElement("textBlock", new XAttribute("controlName", "Legend")));
            Assert.Contains("2 time(s)", (string)WwpActionService.VerifyMovedControl(planned, copied, plan)["message"]);

            XDocument stale = Persisted(planned);
            Table(stale, "TableAttributes").SetAttributeValue("childrenOrderedList", "2;22;A-2;27;Legend-2;22;B");
            Assert.Contains("still references 'Legend'", (string)WwpActionService.VerifyMovedControl(planned, stale, plan)["message"]);

            XDocument unlisted = Persisted(planned);
            Table(unlisted, "tableOp").SetAttributeValue("childrenOrderedList", "2;27;opcoes1");
            Assert.Contains("does not reference 'Legend'", (string)WwpActionService.VerifyMovedControl(planned, unlisted, plan)["message"]);

            XDocument drifted = Persisted(planned);
            Table(drifted, "tableOp").SetAttributeValue("numberOfColumns", "4");
            Assert.Contains("beyond the moved control", (string)WwpActionService.VerifyMovedControl(planned, drifted, plan)["message"]);
        }

        [Fact]
        public void VerifyMovedControlProjection_RequiresTextBlocksOnly()
        {
            var text = JObject.Parse("{control:{type:'textBlock',identity:'Legend'}}");
            Assert.True((bool)WwpActionService.VerifyMovedControlProjection("<form><textblock ControlName='Legend'/></form>", text)["confirmed"]);
            Assert.False((bool)WwpActionService.VerifyMovedControlProjection("<form/>", text)["confirmed"]);
            var attribute = JObject.Parse("{control:{type:'attribute',identity:'guid-A'}}");
            JObject lenient = WwpActionService.VerifyMovedControlProjection("<form/>", attribute);
            Assert.True((bool)lenient["confirmed"]);
            Assert.False((bool)lenient["targetPresent"]);
            Assert.False((bool)WwpActionService.VerifyMovedControlProjection("", attribute)["confirmed"]);
        }

        [Theory]
        [InlineData("add_table", "add_table")]
        [InlineData("move_control", "move_control")]
        public void PublishedLayoutActionsNormalizeToThemselves(string action, string expected)
        {
            Assert.Equal(expected, WwpActionService.NormalizeOperation(action));
        }

        // ---- childrenOrderedList editor ----------------------------------------------------

        [Fact]
        public void OrderedListEdit_InsertsBeforeTheNextListedSiblingAndKeepsGhostEntries()
        {
            var doc = XDocument.Parse("<transaction><table name='T' childrenOrderedList='2;27;A-2;27;Ghost-2;27;C'><textBlock controlName='A'/><textBlock controlName='New'/><textBlock controlName='C'/></table></transaction>");
            XElement parent = doc.Root.Element("table");
            Assert.True(PatternChildOrderEdit.TryInsert(parent, parent.Elements().ElementAt(1), out var change, out string reason), reason);
            Assert.Equal("2;27;A-2;27;Ghost-2;27;New-2;27;C", change.After);
            Assert.Equal("C", change.InsertedBefore);
            Assert.True(PatternChildOrderEdit.References(change.After, "Ghost"));

            Assert.True(PatternChildOrderEdit.TryRemove(parent, parent.Elements().ElementAt(2), out change, out reason), reason);
            Assert.Equal("2;27;A-2;27;Ghost-2;27;New", change.After);
            Assert.Equal("2;27;C", change.Entry);
        }

        [Fact]
        public void OrderedListEdit_ReusesTheTypeCodeOfAListedSiblingOfTheSameKind()
        {
            var doc = XDocument.Parse("<selection><table name='T' childrenOrderedList='4;02;Other'><table name='Other'/><table name='New'/></table></selection>");
            XElement parent = doc.Root.Element("table");
            Assert.True(PatternChildOrderEdit.TryInsert(parent, parent.Elements().ElementAt(1), out var change, out _));
            Assert.Equal("4;02;Other-4;02;New", change.After);

            var unlisted = XDocument.Parse("<transaction><table name='T'><table name='New'/></table></transaction>");
            Assert.True(PatternChildOrderEdit.TryInsert(unlisted.Root.Element("table"), unlisted.Descendants("table").Last(), out change, out _));
            Assert.Null(change.Before);
            Assert.Null(unlisted.Root.Element("table").Attribute("childrenOrderedList"));
        }

        private static XDocument Persisted(XDocument planned) =>
            XDocument.Parse(planned.ToString(SaveOptions.DisableFormatting), LoadOptions.PreserveWhitespace);
    }
}
