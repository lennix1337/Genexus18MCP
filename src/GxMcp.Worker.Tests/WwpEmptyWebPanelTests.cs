using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpEmptyWebPanelTests
    {
        private static XDocument Empty() => XDocument.Parse("<instance><table name='TableMain' childrenOrderedList='baseline'><table name='TableContent'><variable name='FromDate' dataType='Basic' basicType='Date'/></table></table></instance>");
        private static JObject GridArgs() => JObject.Parse("{containerName:'TableContent',gridName:'Results',columns:[{variable:'&Code',basicType:'Character',length:13,description:'Code',readOnly:true},{variable:'Op',basicType:'Numeric',length:2,decimals:0,description:'Select',readOnly:false},{variable:'Date',basicType:'Date'}]}");

        [Fact]
        public void EmptyTemplateAddsAnActionBarAndButtonWithoutChangingExistingContent()
        {
            var doc = Empty();
            string content = doc.Descendants("table").Last().ToString();
            var result = WwpActionService.Apply(doc, "add_user_action", JObject.Parse("{actionName:'Confirm',caption:'Confirm'}"), null);
            Assert.Null(result["error"]);
            Assert.Equal(content, doc.Descendants("table").Single(e => (string)e.Attribute("name") == "TableContent").ToString());
            Assert.Equal("baseline", doc.Descendants("table").First().Attribute("childrenOrderedList")!.Value);
            var actions = doc.Descendants("table").Single(e => (string)e.Attribute("name") == "TableActions");
            Assert.Equal("Responsive", actions.Attribute("type")!.Value);
            Assert.Equal("DoConfirm", result["event"]!.ToString());
            Assert.Equal("Confirm", actions.Element("userAction")!.Attribute("caption")!.Value);
            Assert.Null(WwpActionService.Apply(doc, "add_user_action", JObject.Parse("{actionName:'Cancel',caption:'Cancel'}"), null)["error"]);
            Assert.Equal(2, actions.Elements("userAction").Count());
            Assert.Single(doc.Descendants("table"), e => (string)e.Attribute("name") == "TableActions");
        }

        [Theory]
        [InlineData("{actionName:'Confirm',caption:'Confirm',containerName:'Missing'}")]
        [InlineData("{actionName:'Invalid name',caption:'Confirm'}")]
        [InlineData("{actionName:'Confirm'}")]
        public void InvalidButtonDoesNotCreateAContainer(string json)
        {
            var doc = Empty();
            string before = doc.ToString();
            Assert.NotNull(WwpActionService.Apply(doc, "add_user_action", JObject.Parse(json), null)["error"]);
            Assert.Equal(before, doc.ToString());
        }

        [Fact]
        public void VariableGridHasOrderedTypedEditableColumnsAndNoSdtPlumbing()
        {
            var doc = Empty();
            var args = GridArgs();
            Assert.Null(WwpActionService.ApplyAddGridXml(doc, args)["error"]);
            var grid = doc.Descendants("grid").Single();
            Assert.Equal("Results", grid.Attribute("name")!.Value);
            Assert.Null(grid.Attribute("SDTCollection"));
            Assert.Equal(new[] { "Code", "Op", "Date" }, grid.Elements("gridVariable").Select(e => (string)e.Attribute("name")));
            Assert.Equal("False", grid.Elements("gridVariable").ElementAt(1).Attribute("readOnly")!.Value);
            Assert.Equal("13", grid.Elements("gridVariable").First().Attribute("basicCLength")!.Value);
            Assert.DoesNotContain(grid.DescendantsAndSelf().Attributes(), a => a.Name.LocalName.StartsWith("default") || a.Name.LocalName == "childrenOrderedList" || a.Name.LocalName == "domain" || a.Name.LocalName == "sdtItem");
            WwpActionService.ValidateVariableGridArgs(args, out var columns);
            Assert.True((bool)WwpActionService.VerifyVariableGrid(doc, args, columns)["confirmed"]!);
            Assert.Equal("GridAlreadyExists", WwpActionService.ApplyAddGridXml(doc, args)["code"]!.ToString());
        }

        [Theory]
        [InlineData("{variable:'Code',basicType:'Character'}", "InvalidGridVariableLength")]
        [InlineData("{variable:'Code',basicType:'Numeric',length:2,decimals:2}", "InvalidGridVariableDecimals")]
        [InlineData("{variable:'Code',basicType:'Date',length:10}", "InvalidGridVariableLength")]
        [InlineData("{variable:'Code',basicType:'Date',readOnly:'false'}", "InvalidGridVariableReadOnly")]
        [InlineData("{variable:'Code',basicType:'Wrong'}", "InvalidGridVariableType")]
        [InlineData("{variable:'Code',basicType:'Date',defaultBasicType:'Date'}", "InvalidGridColumn")]
        [InlineData("3", "InvalidGridColumn")]
        [InlineData("{variable:false,basicType:'Date'}", "InvalidGridColumn")]
        [InlineData("{variable:'Code',basicType:'Date',description:{text:'Code'}}", "InvalidGridColumn")]
        [InlineData("{variable:'Bad name',basicType:'Date'}", "InvalidGridColumn")]
        public void InvalidColumnLeavesInstanceUnchanged(string column, string code)
        {
            var doc = Empty();
            string before = doc.ToString();
            var args = GridArgs(); args["columns"] = new JArray(JToken.Parse(column));
            Assert.Equal(code, WwpActionService.ApplyAddGridXml(doc, args)["code"]!.ToString());
            Assert.Equal(before, doc.ToString());
        }

        [Theory]
        [InlineData("name", "Other")]
        [InlineData("basicCLength", "20")]
        [InlineData("dataType", "Based on")]
        [InlineData("readOnly", "False")]
        [InlineData("description", "Other")]
        public void RereadRejectsDroppedOrChangedColumnProperties(string property, string value)
        {
            var doc = Empty(); var args = GridArgs();
            WwpActionService.ApplyAddGridXml(doc, args);
            WwpActionService.ValidateVariableGridArgs(args, out var columns);
            doc.Descendants("gridVariable").First().SetAttributeValue(property, value);
            Assert.Equal("WwpGridNotPersisted", WwpActionService.VerifyVariableGrid(doc, args, columns)["code"]!.ToString());
        }

        [Fact]
        public void RereadRejectsReorderingAndRelocation()
        {
            var doc = Empty(); var args = GridArgs();
            WwpActionService.ApplyAddGridXml(doc, args);
            WwpActionService.ValidateVariableGridArgs(args, out var columns);
            var first = doc.Descendants("gridVariable").First(); first.Remove(); doc.Descendants("grid").Single().Add(first);
            Assert.NotNull(WwpActionService.VerifyVariableGrid(doc, args, columns)["error"]);
            var grid = doc.Descendants("grid").Single(); grid.Remove(); doc.Root!.Add(grid);
            Assert.NotNull(WwpActionService.VerifyVariableGrid(doc, args, columns)["error"]);
        }

        [Fact]
        public void AmbiguousActionBarParentDoesNotChangeAnyTable()
        {
            var doc = Empty(); doc.Root!.Add(new XElement(doc.Descendants("table").First()));
            string before = doc.ToString();
            Assert.NotNull(WwpActionService.Apply(doc, "add_user_action", JObject.Parse("{actionName:'Confirm',caption:'Confirm'}"), null)["error"]);
            Assert.Equal(before, doc.ToString());
        }

        [Theory]
        [InlineData("columns", "[{variable:'Code',basicType:'Date'},{variable:'&code',basicType:'Date'}]", "DuplicateGridColumn")]
        [InlineData("gridName", "'Bad grid'", "InvalidGridName")]
        [InlineData("gridName", "true", "InvalidGridName")]
        [InlineData("containerName", "'Missing'", "GridContainerNotFound")]
        [InlineData("deleteAction", "true", "InvalidDeleteAction")]
        public void InvalidGridSelectorDoesNotChangeTheInstance(string property, string value, string code)
        {
            var doc = Empty(); string before = doc.ToString(); var args = GridArgs(); args[property] = JToken.Parse(value);
            Assert.Equal(code, WwpActionService.ApplyAddGridXml(doc, args)["code"]!.ToString());
            Assert.Equal(before, doc.ToString());
        }

        [Fact]
        public void DefaultNameAndNamespaceAreRetainedAndExtraSdtBindingsAreRejected()
        {
            var doc = XDocument.Parse("<instance xmlns='urn:synthetic'><table name='TableContent'/></instance>");
            var args = GridArgs(); args.Remove("gridName");
            Assert.Null(WwpActionService.ApplyAddGridXml(doc, args)["error"]);
            var grid = doc.Descendants().Single(e => e.Name.LocalName == "grid");
            Assert.Equal("Grid", grid.Attribute("name")!.Value);
            Assert.Equal("urn:synthetic", grid.Name.NamespaceName);
            WwpActionService.ValidateVariableGridArgs(args, out var columns);
            grid.Elements().First().SetAttributeValue("sdtItem", "Other");
            Assert.NotNull(WwpActionService.VerifyVariableGrid(doc, args, columns)["error"]);
        }
    }
}
