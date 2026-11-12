using System;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Element = GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element;

namespace GxMcp.Worker.Tests
{
    // Issue #638: the native write of add_grid_variable and the check made on the re-read grid. The native side runs against the
    // element model shared with the other typed writers (it proves the command sequence and the typed values, not the
    // WorkWithPlus package: the real engine accepted the same elements in the proofs recorded in the pull request).
    public class WwpGridVariableWriteTests
    {
        private const string Xml =
            "<instance><WPRoot><table name='TableMain'>" +
            "<grid name='Grid1'>" +
            "<gridVariable name='First' dataType='Basic' basicType='VarChar' basicCLength='6'/>" +
            "<userAction name='Open'/>" +
            "<gridVariable name='Second' dataType='Basic' basicType='VarChar' basicCLength='6'/>" +
            "</grid></table></WPRoot></instance>";

        private static JObject Args(string variable = "Added", params (string Key, object Value)[] extra)
        {
            var args = new JObject { ["gridPath"] = "Grid1", ["variable"] = variable, ["basicType"] = "VarChar", ["length"] = 20, ["caption"] = "Added column" };
            foreach (var (key, value) in extra) args[key] = value == null ? null : JToken.FromObject(value);
            return args;
        }

        private static WwpActionService.GridVariablePlan Plan(JObject args, WwpActionService.DeclaredVariable declared = null)
        {
            JObject error = WwpActionService.PlanGridVariable(args, declared, out var plan);
            Assert.Null(error);
            return plan;
        }

        // The element tree of Xml: instance > WPRoot > table > grid > [gridVariable, userAction, gridVariable].
        private static GridPart NativeTree(out Element grid)
        {
            var part = new GridPart();
            Element root = part.RootElement.Children.CreateChildElement("WPRoot");
            part.RootElement.Children.Items.Add(root);
            Element table = root.Children.CreateChildElement("table");
            table.Attributes.SetPropertyValue("name", "TableMain");
            root.Children.Items.Add(table);
            grid = table.Children.CreateChildElement("grid");
            grid.Attributes.SetPropertyValue("name", "Grid1");
            table.Children.Items.Add(grid);
            foreach (string name in new[] { "First", "Open", "Second" })
            {
                Element child = grid.Children.CreateChildElement(name == "Open" ? "userAction" : "gridVariable");
                child.Attributes.SetPropertyValue("name", name);
                grid.Children.Items.Add(child);
            }
            return part;
        }

        [Fact]
        public void TheColumnIsAppendedWithTypedValues()
        {
            GridPart part = NativeTree(out Element grid);
            JObject args = Args();
            WwpActionService.GridVariablePlan plan = Plan(args);

            JObject result = WwpActionService.ApplyNativeAddGridVariable(part, XDocument.Parse(Xml), args, plan);

            Assert.Null(result["error"]);
            Assert.Equal(1, part.Updates);
            Element added = grid.Children.Items.Last();
            Assert.Equal("gridVariable", added.Type);
            Assert.Equal("Added", added.Attributes.GetPropertyValueString("name"));
            Assert.Equal("Added column", added.Attributes.GetPropertyValueString("description"));
            Assert.Equal("Basic", added.Attributes.GetPropertyValueString("dataType"));
            Assert.Equal("VarChar", added.Attributes.GetPropertyValueString("basicType"));
            // The sizes and the flag are typed: the text form fails its cast when the instance is loaded.
            Assert.Equal(20, added.Attributes.GetPropertyValue("basicCLength"));
            Assert.Equal(true, added.Attributes.GetPropertyValue("readOnly"));
            Assert.Equal(4, grid.Children.Items.Count);
        }

        [Fact]
        public void ABeforeAnchorInsertsAtTheAnchorsPositionAmongAllChildren()
        {
            GridPart part = NativeTree(out Element grid);
            JObject args = Args("Added", ("before", "Second"));

            Assert.Null(WwpActionService.ApplyNativeAddGridVariable(part, XDocument.Parse(Xml), args, Plan(args))["error"]);

            Assert.Equal(new[] { "First", "Open", "Added", "Second" },
                grid.Children.Items.Select(e => e.Attributes.GetPropertyValueString("name")));
        }

        [Fact]
        public void AnUnknownBeforeAnchorOrAnExistingNameChangesNothing()
        {
            GridPart missing = NativeTree(out Element grid);
            JObject args = Args("Added", ("before", "Nope"));
            Assert.Equal("GridColumnBeforeNotFound",
                (string)WwpActionService.ApplyNativeAddGridVariable(missing, XDocument.Parse(Xml), args, Plan(args))["code"]);
            Assert.Equal(0, missing.Updates);
            Assert.Equal(3, grid.Children.Items.Count);

            GridPart duplicate = NativeTree(out Element other);
            JObject again = Args("second");
            Assert.Equal("GridVariableAlreadyExists",
                (string)WwpActionService.ApplyNativeAddGridVariable(duplicate, XDocument.Parse(Xml), again, Plan(again))["code"]);
            Assert.Equal(0, duplicate.Updates);
            Assert.Equal(3, other.Children.Items.Count);
        }

        [Fact]
        public void ABasedOnColumnCarriesTheKbObjectNotItsText()
        {
            GridPart part = NativeTree(out Element grid);
            var declared = new WwpActionService.DeclaredVariable
            {
                Name = "cust", DbType = "CHARACTER", Length = 40, BasicType = "Character",
                BasedOnReference = "adbb33c9-0906-4971-833c-998de27e0676-CustomerName"
            };
            JObject args = new JObject { ["gridPath"] = "Grid1", ["variable"] = "cust" };
            WwpActionService.GridVariablePlan plan = Plan(args, declared);

            Assert.Null(WwpActionService.ApplyNativeAddGridVariable(part, XDocument.Parse(Xml), args, plan)["error"]);

            Element added = grid.Children.Items.Last();
            Assert.Equal("Based on", added.Attributes.GetPropertyValueString("dataType"));
            // Without a KB object (a fake has none) the reference text is what is written; the real call hands over the object.
            Assert.Equal("adbb33c9-0906-4971-833c-998de27e0676-CustomerName", added.Attributes.GetPropertyValueString("domain"));
            Assert.Null(added.Attributes.GetPropertyValue("basicType"));
        }

        [Fact]
        public void TheGridTheXmlResolvedMustBeTheNativeOne()
        {
            GridPart part = NativeTree(out Element grid);
            grid.Attributes.SetPropertyValue("name", "Other");
            JObject args = Args();

            JObject result = WwpActionService.ApplyNativeAddGridVariable(part, XDocument.Parse(Xml), args, Plan(args));

            Assert.Equal("WwpGridNotResolvable", (string)result["code"]);
            Assert.Equal(0, part.Updates);
            Assert.Equal(3, grid.Children.Items.Count);
        }

        [Fact]
        public void ADifferentNumberOfGridsOrColumnsInTheNativeTreeIsRefusedBeforeAnythingIsWritten()
        {
            // One more grid than the XML has: the ordinal cannot be trusted.
            GridPart moreGrids = NativeTree(out _);
            Element table = moreGrids.RootElement.Children.Items.Single().Children.Items.Single();
            Element extra = table.Children.CreateChildElement("grid");
            extra.Attributes.SetPropertyValue("name", "Grid2");
            table.Children.Items.Add(extra);
            JObject args = Args();

            JObject grids = WwpActionService.ApplyNativeAddGridVariable(moreGrids, XDocument.Parse(Xml), args, Plan(args));

            Assert.Equal("WwpGridNotResolvable", (string)grids["code"]);
            Assert.Contains("1 in the XML, 2 native", (string)grids["error"]);
            Assert.Equal(0, moreGrids.Updates);

            // The same grid with a column the XML does not have.
            GridPart moreColumns = NativeTree(out Element sameGrid);
            Element stray = sameGrid.Children.CreateChildElement("gridVariable");
            stray.Attributes.SetPropertyValue("name", "Stray");
            sameGrid.Children.Items.Add(stray);

            JObject columns = WwpActionService.ApplyNativeAddGridVariable(moreColumns, XDocument.Parse(Xml), args, Plan(args));

            Assert.Equal("WwpGridNotResolvable", (string)columns["code"]);
            Assert.Equal(0, moreColumns.Updates);
            Assert.Equal(4, sameGrid.Children.Items.Count);
        }

        [Fact]
        public void UnnamedXmlGridMatchesNativeGridWithDefaultGridName()
        {
            const string unnamedXml =
                "<instance><WPRoot><table name='TableMain'><table name='TableGridHeader'>" +
                "<grid><gridVariable name='GridDummyVar' dataType='Basic' basicType='VarChar' basicCLength='6'/></grid>" +
                "</table></table></WPRoot></instance>";
            var part = new GridPart();
            Element root = part.RootElement.Children.CreateChildElement("WPRoot");
            part.RootElement.Children.Items.Add(root);
            Element main = root.Children.CreateChildElement("table");
            main.Attributes.SetPropertyValue("name", "TableMain");
            root.Children.Items.Add(main);
            Element header = main.Children.CreateChildElement("table");
            header.Attributes.SetPropertyValue("name", "TableGridHeader");
            main.Children.Items.Add(header);
            Element grid = header.Children.CreateChildElement("grid");
            grid.Attributes.SetPropertyValue("name", "Grid");
            header.Children.Items.Add(grid);
            Element dummy = grid.Children.CreateChildElement("gridVariable");
            dummy.Attributes.SetPropertyValue("name", "GridDummyVar");
            grid.Children.Items.Add(dummy);

            JObject args = new JObject
            {
                ["gridPath"] = "Grid",
                ["variable"] = "gOp",
                ["basicType"] = "VarChar",
                ["length"] = 20,
                ["before"] = "GridDummyVar"
            };
            JObject result = WwpActionService.ApplyNativeAddGridVariable(part, XDocument.Parse(unnamedXml), args, Plan(args));

            Assert.Null(result["error"]);
            Assert.Equal(new[] { "gOp", "GridDummyVar" },
                grid.Children.Items.Select(e => e.Attributes.GetPropertyValueString("name")));
        }

        private static (XDocument Before, XDocument After, JObject Args, WwpActionService.GridVariablePlan Plan) Rewritten(
            Action<XElement> persist = null, string before = null)
        {
            JObject args = before == null ? Args() : Args("Added", ("before", before));
            var beforeDoc = XDocument.Parse(Xml);
            var afterDoc = XDocument.Parse(Xml);
            Assert.Null(WwpActionService.ApplyAddGridVariableXml(afterDoc, args)["error"]);
            persist?.Invoke(afterDoc.Descendants("grid").Single());
            return (beforeDoc, afterDoc, args, Plan(args));
        }

        [Fact]
        public void AReReadGridWithTheColumnInPlaceIsConfirmed()
        {
            var r = Rewritten();
            Assert.True(WwpActionService.VerifyAddGridVariable(r.Before, r.After, r.Args, r.Plan)["confirmed"].Value<bool>());

            var anchored = Rewritten(before: "Second");
            Assert.True(WwpActionService.VerifyAddGridVariable(anchored.Before, anchored.After, anchored.Args, anchored.Plan)["confirmed"].Value<bool>());
        }

        [Fact]
        public void AColumnThatLostAnAttributeIsNotConfirmed()
        {
            var r = Rewritten(grid => grid.Elements("gridVariable").Single(e => (string)e.Attribute("name") == "Added").SetAttributeValue("basicCLength", "5"));
            JObject result = WwpActionService.VerifyAddGridVariable(r.Before, r.After, r.Args, r.Plan);

            Assert.Equal("WwpGridVariableNotPersisted", (string)result["code"]);
            Assert.Contains("basicCLength", (string)result["error"]);
        }

        [Fact]
        public void AColumnAtTheWrongPositionOrADisturbedSiblingIsNotConfirmed()
        {
            var wrong = Rewritten(grid =>
            {
                XElement added = grid.Elements().Last();
                added.Remove();
                grid.AddFirst(added);
            });
            Assert.Contains("position", (string)WwpActionService.VerifyAddGridVariable(wrong.Before, wrong.After, wrong.Args, wrong.Plan)["error"]);

            var disturbed = Rewritten(grid => grid.Elements("gridVariable").First().SetAttributeValue("basicCLength", "9"));
            Assert.Contains("not part of the request", (string)WwpActionService.VerifyAddGridVariable(disturbed.Before, disturbed.After, disturbed.Args, disturbed.Plan)["error"]);

            var twice = Rewritten(grid => grid.Add(new XElement("gridVariable", new XAttribute("name", "Added"))));
            Assert.Contains("2 columns", (string)WwpActionService.VerifyAddGridVariable(twice.Before, twice.After, twice.Args, twice.Plan)["error"]);
        }

        // The engine stores no value that is the pattern's default (Numeric for basicType, Based on for dataType); a column
        // typed that way is confirmed without the attribute, and no other missing attribute is forgiven.
        [Fact]
        public void ADefaultValueTheEngineDoesNotStoreIsNotAFailure()
        {
            JObject args = new JObject { ["gridPath"] = "Grid1", ["variable"] = "Amount" };
            var declared = new WwpActionService.DeclaredVariable { Name = "Amount", DbType = "NUMERIC", Length = 8, Decimals = 2, BasicType = "Numeric" };
            WwpActionService.GridVariablePlan plan = Plan(args, declared);
            var beforeDoc = XDocument.Parse(Xml);
            var afterDoc = XDocument.Parse(Xml);
            WwpActionService.ApplyAddGridVariableXml(afterDoc, args, declared);
            XElement column = afterDoc.Descendants("gridVariable").Single(e => (string)e.Attribute("name") == "Amount");

            column.Attribute("basicType").Remove();
            Assert.True(WwpActionService.VerifyAddGridVariable(beforeDoc, afterDoc, args, plan)["confirmed"].Value<bool>());

            column.Attribute("basicLength").Remove();
            Assert.Contains("basicLength", (string)WwpActionService.VerifyAddGridVariable(beforeDoc, afterDoc, args, plan)["error"]);
        }

        internal sealed class GridPart
        {
            public Element RootElement { get; } = new Element("instance");
            public int Updates { get; private set; }
            public void ExecuteUpdate(string label, Action mutation) { Updates++; mutation(); }
        }
    }
}
