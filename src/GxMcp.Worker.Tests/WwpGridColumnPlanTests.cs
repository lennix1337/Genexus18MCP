using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Grid-column mutation plan coverage for the projection that runs before any
    /// SDK call, adapted from the cases proposed in PR #269. The mutation is
    /// reached through the two per-operation seams rather than a dispatcher, and
    /// the argument contract is the one the server actually enforces: columns are
    /// selected by exact identity (never a name suffix), and a presentation
    /// column is only added against a verified GUID-Name reference rather than an
    /// invented SDK identity.
    /// </summary>
    public class WwpGridColumnPlanTests
    {
        private const string Machine = "11111111-1111-1111-1111-111111111111-Machine";
        private const string Processing = "22222222-2222-2222-2222-222222222222-Processing";
        private const string Reference = "33333333-3333-3333-3333-333333333333-ReinicioHistorico";
        private const string GridPath = "/instance/level/selection/table/grid";

        private const string Xml =
            "<instance baseline='keep'><level><selection><table><grid childrenOrderedList='keep'>" +
            "<gridAttribute attribute='" + Machine + "' description='Machine'/>" +
            "<action name='Keep'/>" +
            "<gridAttribute attribute='" + Processing + "' description='Old' defaultDescription='Baseline' export='True'>" +
            "<binding value='keep'/></gridAttribute>" +
            "</grid><filter value='keep'/></table></selection></level><!-- protected --></instance>";

        private static JObject Move()
            => new JObject
            {
                ["gridPath"] = GridPath,
                ["attribute"] = Processing,
                ["before"] = Machine,
                ["caption"] = "Processamento"
            };

        private static JObject Add()
            => new JObject
            {
                ["gridPath"] = GridPath,
                ["variable"] = "ReinicioHistorico",
                ["variableReference"] = Reference,
                ["caption"] = "Reinicio",
                ["basicType"] = "VarChar",
                ["length"] = 80,
                ["before"] = Machine
            };

        private static XElement Column(XDocument document, string identity)
            => document.Descendants("grid").Single().Elements()
                .First(e => (string)e.Attribute("attribute") == identity);

        private static XElement OwningGrid(XDocument document, string identity)
            => document.Descendants("grid").Single(g =>
                g.Elements().Any(e => (string)e.Attribute("attribute") == identity));

        [Fact]
        public void MovePreservesBindingMetadataAndSiblings()
        {
            var document = XDocument.Parse(Xml);
            var result = WwpActionService.ApplyMoveGridColumnXml(document, Move());

            Assert.Null(result["error"]);
            var column = Column(document, Processing);
            Assert.Equal("Processamento", (string)column.Attribute("description"));
            Assert.Equal("Baseline", (string)column.Attribute("defaultDescription"));
            Assert.Equal("True", (string)column.Attribute("export"));
            Assert.Equal("keep", (string)column.Element("binding").Attribute("value"));
        }

        [Fact]
        public void ForwardMoveKeepsInterleavedChildrenAndPlacesColumnBeforeAnchor()
        {
            var document = XDocument.Parse(Xml);
            var args = Move();
            args["attribute"] = Machine;
            args["before"] = Processing;
            args.Remove("caption");

            Assert.Null(WwpActionService.ApplyMoveGridColumnXml(document, args)["error"]);

            var children = document.Descendants("grid").Single().Elements().ToList();
            // The <action> child is interleaved between the two columns and must
            // survive the move untouched.
            Assert.Equal("action", children[0].Name.LocalName);
            Assert.Equal(Machine, (string)children[1].Attribute("attribute"));
            Assert.Equal(Processing, (string)children[2].Attribute("attribute"));
            Assert.Equal("Baseline", (string)children[2].Attribute("defaultDescription"));
        }

        [Fact]
        public void CaptionOnlyKeepsColumnPosition()
        {
            var document = XDocument.Parse(Xml);
            var original = document.Descendants("grid").Single().Elements().ToList();

            var args = Move();
            args.Remove("before");
            Assert.Null(WwpActionService.ApplyMoveGridColumnXml(document, args)["error"]);

            var after = document.Descendants("grid").Single().Elements().ToList();
            Assert.Equal(original.Count, after.Count);
            for (var i = 0; i < original.Count; i++)
            {
                Assert.Equal((string)original[i].Attribute("attribute"), (string)after[i].Attribute("attribute"));
                Assert.Equal(original[i].Name.LocalName, after[i].Name.LocalName);
            }
            Assert.Equal("Processamento", (string)Column(document, Processing).Attribute("description"));
        }

        [Fact]
        public void MoveLeavesRejectedRequestsWithoutMutation()
        {
            // A refused request must not half-apply: the caption is only written
            // after identity, grid and anchor resolution all succeed.
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);
            var args = Move();
            args["before"] = "99999999-9999-9999-9999-999999999999-Absent";

            Assert.NotNull(WwpActionService.ApplyMoveGridColumnXml(document, args)["error"]);
            Assert.True(XNode.DeepEquals(before, document));
            Assert.Equal("Old", (string)Column(document, Processing).Attribute("description"));
        }

        [Theory]
        [InlineData("/instance//grid", "InvalidGridPath")]
        [InlineData("/instance/level/selection/table", "InvalidGridPath")]
        [InlineData("instance/level", "InvalidGridPath")]
        [InlineData("/1instance/level", "InvalidGridPath")]
        [InlineData("/instance/level/selection/table/grid[9]", "GridNotFound")]
        [InlineData("/nope/level", "GridNotFound")]
        [InlineData("/instance/missing/grid", "GridNotFound")]
        public void InvalidPathNeverMutates(string path, string code)
        {
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);
            var args = Move();
            args["gridPath"] = path;

            Assert.Equal(code, WwpActionService.ApplyMoveGridColumnXml(document, args)["code"]?.ToString());
            Assert.True(XNode.DeepEquals(before, document), "a rejected path must not mutate the document");
        }

        [Fact]
        public void ZeroBasedIndexSelectsAmongSiblingsOfTheSameType()
        {
            var document = XDocument.Parse(Xml);
            var table = document.Descendants("table").Single();
            table.AddFirst(new XElement("grid", new XAttribute("name", "First")));
            table.Add(new XElement("grid", new XAttribute("name", "Third")));

            var args = Move();
            args["gridPath"] = GridPath + "[1]";
            Assert.Null(WwpActionService.ApplyMoveGridColumnXml(document, args)["error"]);

            // grid[1] is the original grid that owns the Processing column; the
            // two sibling grids added for disambiguation must be untouched.
            var grid = OwningGrid(document, Processing);
            Assert.Equal("keep", (string)grid.Attribute("childrenOrderedList"));
            Assert.Equal("Processamento", (string)grid.Elements()
                .First(e => (string)e.Attribute("attribute") == Processing).Attribute("description"));
            Assert.Equal(3, document.Descendants("grid").Count());
        }

        [Fact]
        public void UnindexedSegmentMatchingTwoSiblingsIsRefused()
        {
            var document = XDocument.Parse(Xml);
            document.Descendants("table").Single().Add(new XElement("grid", new XAttribute("name", "Other")));

            var args = Move();
            args["gridPath"] = GridPath;
            Assert.Equal("AmbiguousGrid", WwpActionService.ApplyMoveGridColumnXml(document, args)["code"]?.ToString());
        }

        [Fact]
        public void MultipleGridsWithoutAPathAreRefusedInsteadOfGuessed()
        {
            var document = XDocument.Parse(Xml);
            document.Descendants("table").Single().Add(new XElement("grid"));
            var args = Move();
            args.Remove("gridPath");

            Assert.Equal("AmbiguousGrid", WwpActionService.ApplyMoveGridColumnXml(document, args)["code"]?.ToString());
        }

        [Fact]
        public void SingleGridWithoutAPathIsAccepted()
        {
            var document = XDocument.Parse(Xml);
            var args = Move();
            args.Remove("gridPath");

            Assert.Null(WwpActionService.ApplyMoveGridColumnXml(document, args)["error"]);
            Assert.Equal("Processamento", (string)Column(document, Processing).Attribute("description"));
        }

        [Theory]
        [InlineData("sing")]
        [InlineData("Processing")]
        [InlineData("22222222-2222-2222-2222-222222222222")]
        [InlineData("22222222-2222-2222-2222-222222222222-ProcessingX")]
        public void PartialOrUnrelatedIdentityCannotSelectAColumn(string requested)
        {
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);
            var args = Move();
            args["attribute"] = requested;

            Assert.Equal("GridColumnNotFound", WwpActionService.ApplyMoveGridColumnXml(document, args)["code"]?.ToString());
            Assert.True(XNode.DeepEquals(before, document));
        }

        [Fact]
        public void AttributeSelectorMustMatchTheSelectedColumn()
        {
            var document = XDocument.Parse(Xml);
            var args = Move();
            args["attribute"] = Machine;
            args["variable"] = "ReinicioHistorico";

            Assert.Equal("GridColumnIdentityMismatch", WwpActionService.ApplyMoveGridColumnXml(document, args)["code"]?.ToString());
        }

        [Fact]
        public void BeforeAnchorMustResolveExactly()
        {
            var document = XDocument.Parse(Xml);
            var args = Move();
            args["before"] = "sing";

            Assert.Equal("GridColumnBeforeNotFound", WwpActionService.ApplyMoveGridColumnXml(document, args)["code"]?.ToString());
        }

        [Fact]
        public void ColumnCannotBeMovedBeforeItself()
        {
            var document = XDocument.Parse(Xml);
            var args = Move();
            args["before"] = Processing;

            Assert.Equal("GridColumnPositionUnchanged", WwpActionService.ApplyMoveGridColumnXml(document, args)["code"]?.ToString());
        }

        [Fact]
        public void MoveRequiresAColumnIdentity()
        {
            var document = XDocument.Parse(Xml);
            var args = Move();
            args.Remove("attribute");

            Assert.Equal("MissingGridColumn", WwpActionService.ApplyMoveGridColumnXml(document, args)["code"]?.ToString());
        }

        [Fact]
        public void AddRequiresAVerifiedVariableReference()
        {
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);
            var args = Add();
            args.Remove("variableReference");

            Assert.Equal("GridVariableIdentityRequired", WwpActionService.ApplyAddGridVariableXml(document, args)["code"]?.ToString());
            Assert.True(XNode.DeepEquals(before, document), "a refused add must not mutate the document");
        }

        [Fact]
        public void AddWritesAPresentationColumnWithoutTableAttributes()
        {
            var document = XDocument.Parse(Xml);
            var result = WwpActionService.ApplyAddGridVariableXml(document, Add());

            Assert.Null(result["error"]);
            var variable = document.Descendants("gridVariable").Single();
            Assert.Equal(Reference, (string)variable.Attribute("variable"));
            Assert.Equal("ReinicioHistorico", (string)variable.Attribute("name"));
            Assert.Equal("Reinicio", (string)variable.Attribute("description"));
            Assert.Equal("VarChar", (string)variable.Attribute("basicType"));
            Assert.Equal("80", (string)variable.Attribute("basicCLength"));
            // A presentation column must not grow a table-backed binding or any
            // baseline-derived default.
            Assert.Null(variable.Element("binding"));
            Assert.DoesNotContain(variable.Attributes(), a => a.Name.LocalName.StartsWith("default"));
        }

        [Fact]
        public void AddPlacesTheColumnBeforeTheAnchorAndKeepsSiblings()
        {
            var document = XDocument.Parse(Xml);
            Assert.Null(WwpActionService.ApplyAddGridVariableXml(document, Add())["error"]);

            var children = document.Descendants("grid").Single().Elements().ToList();
            // The new column lands before the Machine anchor, ahead of the
            // interleaved <action> child, which must keep its position.
            Assert.Equal("gridVariable", children[0].Name.LocalName);
            Assert.Equal(Machine, (string)children[1].Attribute("attribute"));
            Assert.Equal("action", children[2].Name.LocalName);
            Assert.Equal(Processing, (string)children[3].Attribute("attribute"));
        }

        [Fact]
        public void AddWithoutAnchorAppendsWithoutDisturbingExistingChildren()
        {
            var document = XDocument.Parse(Xml);
            var args = Add();
            args.Remove("before");

            Assert.Null(WwpActionService.ApplyAddGridVariableXml(document, args)["error"]);
            var children = document.Descendants("grid").Single().Elements().ToList();
            Assert.Equal("gridVariable", children[children.Count - 1].Name.LocalName);
        }

        [Theory]
        [InlineData("Numeric")]
        [InlineData("")]
        [InlineData("Varchar(10)")]
        public void InvalidVariableTypeIsPure(string basicType)
        {
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);
            var args = Add();
            args["basicType"] = basicType;

            Assert.Equal("InvalidGridVariableType", WwpActionService.ApplyAddGridVariableXml(document, args)["code"]?.ToString());
            Assert.True(XNode.DeepEquals(before, document), "a refused add must not mutate the document");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void NonPositiveVariableLengthIsRefused(int length)
        {
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);
            var args = Add();
            args["length"] = length;

            Assert.Equal("InvalidGridVariableLength", WwpActionService.ApplyAddGridVariableXml(document, args)["code"]?.ToString());
            Assert.True(XNode.DeepEquals(before, document), "a refused add must not mutate the document");
        }

        [Fact]
        public void ProjectionEnforcesNoUpperBoundOnLengthAndSaysSo()
        {
            // Locks the current contract: the projection only guards the lower
            // bound. A GeneXus-specific maximum must be enforced against the SDK,
            // not invented here, so this asserts the behaviour rather than hiding it.
            var document = XDocument.Parse(Xml);
            var args = Add();
            args["length"] = 10000;

            Assert.Null(WwpActionService.ApplyAddGridVariableXml(document, args)["error"]);
            Assert.Equal("10000", (string)document.Descendants("gridVariable").Single().Attribute("basicCLength"));
        }

        [Fact]
        public void ExistingVariableNameIsNeverRebound()
        {
            var document = XDocument.Parse(Xml);
            document.Descendants("grid").Single().Add(
                new XElement("gridVariable", new XAttribute("variable", "99999999-9999-9999-9999-999999999999-Outro"), new XAttribute("name", "ReinicioHistorico")));

            Assert.Equal("GridVariableAlreadyExists", WwpActionService.ApplyAddGridVariableXml(document, Add())["code"]?.ToString());
        }

        [Fact]
        public void ExistingVariableReferenceIsNeverReused()
        {
            var document = XDocument.Parse(Xml);
            document.Descendants("grid").Single().Add(
                new XElement("gridVariable", new XAttribute("variable", Reference), new XAttribute("name", "Outro")));

            Assert.Equal("GridVariableReferenceInUse", WwpActionService.ApplyAddGridVariableXml(document, Add())["code"]?.ToString());
        }

        [Fact]
        public void AddRequiresAVariableName()
        {
            var document = XDocument.Parse(Xml);
            var args = Add();
            args.Remove("variable");

            Assert.Equal("MissingGridVariable", WwpActionService.ApplyAddGridVariableXml(document, args)["code"]?.ToString());
        }

        [Fact]
        public void NullDocumentIsRefusedInsteadOfThrowing()
        {
            Assert.Equal("InvalidPatternInstance", WwpActionService.ApplyMoveGridColumnXml(null, Move())["code"]?.ToString());
            Assert.Equal("InvalidPatternInstance", WwpActionService.ApplyAddGridVariableXml(null, Add())["code"]?.ToString());
        }

        [Theory]
        [InlineData("move_grid_column", true)]
        [InlineData("add_grid_variable", true)]
        [InlineData("MOVE_GRID_COLUMN", true)]
        [InlineData("set_table_type", false)]
        [InlineData("add_grid_variables", false)]
        [InlineData("", false)]
        public void OnlyTheTwoGridColumnOperationsAreClaimedByThisSurface(string operation, bool expected)
        {
            Assert.Equal(expected, WwpActionService.IsGridColumnOperation(operation));
        }
    }
}
