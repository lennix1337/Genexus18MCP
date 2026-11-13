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
    /// column is identified by its name, as WorkWithPlus keys a grid variable, and
    /// typed from the declaration or from explicit arguments (#638).
    /// </summary>
    public class WwpGridColumnPlanTests
    {
        private const string Machine = "11111111-1111-1111-1111-111111111111-Machine";
        private const string Processing = "22222222-2222-2222-2222-222222222222-Processing";
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

        // Issue #638: the grid add_grid created is addressed by the name it was given, which is how the reporter named it.
        private const string NamedGrids =
            "<instance><WPRoot><table name='TableMain'>" +
            "<grid name='Grid1'><gridVariable name='A' basicType='VarChar' basicCLength='5'/></grid>" +
            "<grid controlName='Totals'><gridVariable name='B' basicType='VarChar' basicCLength='5'/></grid>" +
            "</table></WPRoot></instance>";

        [Theory]
        [InlineData("Grid1", "A")]
        [InlineData("grid1", "A")]
        [InlineData("Totals", "B")]
        public void AGridIsAddressedByItsNameOrControlName(string gridPath, string firstColumn)
        {
            var document = XDocument.Parse(NamedGrids);
            var result = WwpActionService.ApplyAddGridVariableXml(document, new JObject
            {
                ["gridPath"] = gridPath, ["variable"] = "Added", ["basicType"] = "VarChar", ["length"] = 3
            });

            Assert.Null(result["error"]);
            var grid = document.Descendants("grid").Single(g => g.Elements().Any(e => (string)e.Attribute("name") == "Added"));
            Assert.Equal(firstColumn, (string)grid.Elements().First().Attribute("name"));
            Assert.Equal(1, document.Descendants("gridVariable").Count(e => (string)e.Attribute("name") == "Added"));
        }

        // Issue #961: a client writes "<Container>/<Grid>"; the last segment names the grid, so it resolves when unique.
        [Fact]
        public void AContainerPrefixedGridNameResolvesByItsLastSegment()
        {
            var document = XDocument.Parse(NamedGrids);
            var result = WwpActionService.ApplyAddGridVariableXml(document, new JObject
            {
                ["gridPath"] = "TableMain/Totals", ["variable"] = "Added", ["basicType"] = "VarChar", ["length"] = 3
            });

            Assert.Null(result["error"]);
            Assert.Equal(1, document.Descendants("gridVariable").Count(e => (string)e.Attribute("name") == "Added"));
        }

        [Fact]
        public void AnUnknownGridNameListsTheNamedGridsAndAnAmbiguousOneIsRefused()
        {
            var document = XDocument.Parse(NamedGrids);
            var refusal = WwpActionService.ApplyAddGridVariableXml(document, new JObject
            {
                ["gridPath"] = "Nope", ["variable"] = "Added", ["basicType"] = "VarChar", ["length"] = 3
            });
            Assert.Equal("GridNotFound", (string)refusal["code"]);
            Assert.Contains("Grid1", (string)refusal["error"]);

            var twice = XDocument.Parse("<instance><grid name='G'/><grid name='G'/></instance>");
            Assert.Equal("AmbiguousGrid", WwpActionService.ApplyAddGridVariableXml(twice, new JObject
            {
                ["gridPath"] = "G", ["variable"] = "Added", ["basicType"] = "VarChar", ["length"] = 3
            })["code"]?.ToString());
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
        public void AnUndeclaredVariableNeedsAnExplicitType()
        {
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);
            var args = Add();
            args.Remove("basicType");
            args.Remove("length");

            JObject refusal = WwpActionService.ApplyAddGridVariableXml(document, args);
            Assert.Equal("GridVariableTypeRequired", refusal["code"]?.ToString());
            Assert.Contains("not declared", refusal["error"]?.ToString());
            Assert.True(XNode.DeepEquals(before, document), "a refused add must not mutate the document");
        }

        [Fact]
        public void AddWritesAPresentationColumnWithoutTableAttributes()
        {
            var document = XDocument.Parse(Xml);
            var result = WwpActionService.ApplyAddGridVariableXml(document, Add());

            Assert.Null(result["error"]);
            Assert.Equal("arguments", (string)result["typeSource"]);
            var variable = document.Descendants("gridVariable").Single();
            // WorkWithPlus defines no reference attribute on a grid variable: the column is keyed by name.
            Assert.Null(variable.Attribute("variable"));
            Assert.Equal("ReinicioHistorico", (string)variable.Attribute("name"));
            Assert.Equal("Reinicio", (string)variable.Attribute("description"));
            Assert.Equal("Basic", (string)variable.Attribute("dataType"));
            Assert.Equal("VarChar", (string)variable.Attribute("basicType"));
            Assert.Equal("80", (string)variable.Attribute("basicCLength"));
            Assert.Equal("True", (string)variable.Attribute("readOnly"));
            // A presentation column must not grow a table-backed binding or any
            // baseline-derived default.
            Assert.Null(variable.Element("binding"));
            Assert.DoesNotContain(variable.Attributes(), a => a.Name.LocalName.StartsWith("default"));
        }

        [Fact]
        public void TheColumnIsTheOneAVariableGridWouldHaveWritten()
        {
            // add_grid with columns[] is proved on a real WorkWithPlus package; a column added to an existing grid must carry
            // exactly the same attributes.
            var grid = XDocument.Parse(Xml);
            WwpActionService.ApplyAddGridVariableXml(grid, Add());
            JObject variableGrid = WwpActionService.ValidateVariableGridArgs(new JObject
            {
                ["containerName"] = "T",
                ["columns"] = new JArray(new JObject
                {
                    ["variable"] = "ReinicioHistorico", ["description"] = "Reinicio", ["basicType"] = "VarChar", ["length"] = 80
                })
            }, out var columns);

            Assert.Null(variableGrid);
            Assert.Equal(
                columns[0].Variable.Attributes().Select(a => a.Name.LocalName + "=" + a.Value),
                grid.Descendants("gridVariable").Single().Attributes().Select(a => a.Name.LocalName + "=" + a.Value));
        }

        [Fact]
        public void ReadOnlyCanBeTurnedOff()
        {
            var document = XDocument.Parse(Xml);
            var args = Add();
            args["readOnly"] = false;

            Assert.Null(WwpActionService.ApplyAddGridVariableXml(document, args)["error"]);
            Assert.Equal("False", (string)document.Descendants("gridVariable").Single().Attribute("readOnly"));
            args["readOnly"] = "no";
            Assert.Equal("InvalidGridVariableReadOnly",
                WwpActionService.ApplyAddGridVariableXml(XDocument.Parse(Xml), args)["code"]?.ToString());
        }

        private static WwpActionService.DeclaredVariable Declared(string name, string dbType, int length = 0, int decimals = 0,
            bool isCollection = false, string basedOn = null)
            => new WwpActionService.DeclaredVariable
            {
                Name = name, DbType = dbType, Length = length, Decimals = decimals, IsCollection = isCollection,
                BasicType = WwpActionService.BasicTypeOf(dbType), BasedOnReference = basedOn
            };

        private static JObject OnlyTheName(string name = "tstnom")
            => new JObject { ["gridPath"] = GridPath, ["variable"] = name, ["caption"] = "Nome" };

        [Fact]
        public void ADeclaredVariableTypesTheColumnWithoutAnyTypeArgument()
        {
            // The issue #638 repro: `add_grid_variable variable=tstnom caption=Nome` for a declared &tstnom CHARACTER(30).
            var document = XDocument.Parse(Xml);
            var result = WwpActionService.ApplyAddGridVariableXml(document, OnlyTheName(), Declared("tstnom", "CHARACTER", 30));

            Assert.Null(result["error"]);
            Assert.Equal("declaration", (string)result["typeSource"]);
            var variable = document.Descendants("gridVariable").Single();
            Assert.Equal("tstnom", (string)variable.Attribute("name"));
            Assert.Equal("Nome", (string)variable.Attribute("description"));
            Assert.Equal("Basic", (string)variable.Attribute("dataType"));
            Assert.Equal("Character", (string)variable.Attribute("basicType"));
            Assert.Equal("30", (string)variable.Attribute("basicCLength"));
        }

        [Fact]
        public void ADeclaredNumericKeepsItsLengthAndDecimals()
        {
            var document = XDocument.Parse(Xml);
            Assert.Null(WwpActionService.ApplyAddGridVariableXml(document, OnlyTheName("tstcod"), Declared("tstcod", "NUMERIC", 12, 2))["error"]);

            var variable = document.Descendants("gridVariable").Single();
            Assert.Equal("Numeric", (string)variable.Attribute("basicType"));
            Assert.Equal("12", (string)variable.Attribute("basicLength"));
            Assert.Equal("2", (string)variable.Attribute("basicDecimals"));
            Assert.Null(variable.Attribute("basicCLength"));
        }

        [Fact]
        public void AnUnsizedDeclarationWritesNoLength()
        {
            var document = XDocument.Parse(Xml);
            Assert.Null(WwpActionService.ApplyAddGridVariableXml(document, OnlyTheName("started"), Declared("started", "DATE", 8))["error"]);

            var variable = document.Descendants("gridVariable").Single();
            Assert.Equal("Date", (string)variable.Attribute("basicType"));
            Assert.DoesNotContain(variable.Attributes(), a => a.Name.LocalName.StartsWith("basic") && a.Name.LocalName != "basicType");
        }

        [Fact]
        public void ADeclaredVariableBasedOnAnAttributeKeepsTheLink()
        {
            var document = XDocument.Parse(Xml);
            const string reference = "adbb33c9-0906-4971-833c-998de27e0676-CustomerName";
            var result = WwpActionService.ApplyAddGridVariableXml(document, OnlyTheName("cust"),
                Declared("cust", "CHARACTER", 40, basedOn: reference));

            Assert.Null(result["error"]);
            var variable = document.Descendants("gridVariable").Single();
            Assert.Equal("Based on", (string)variable.Attribute("dataType"));
            Assert.Equal(reference, (string)variable.Attribute("domain"));
            Assert.Null(variable.Attribute("basicType"));
            Assert.Equal(reference, (string)result["domain"]);
        }

        [Theory]
        [InlineData("basicType", "Numeric")]
        [InlineData("length", 31)]
        public void AnExplicitTypeThatDisagreesWithTheDeclarationIsRefusedNamingIt(string property, object value)
        {
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);
            JObject args = OnlyTheName();
            args[property] = JToken.FromObject(value);

            JObject refusal = WwpActionService.ApplyAddGridVariableXml(document, args, Declared("tstnom", "CHARACTER", 30));
            Assert.Equal("GridVariableTypeMismatch", refusal["code"]?.ToString());
            Assert.Contains("CHARACTER(30)", refusal["error"]?.ToString());
            Assert.Equal("Character", (string)refusal["declared"]["basicType"]);
            Assert.True(XNode.DeepEquals(before, document), "a refused add must not mutate the document");
        }

        [Fact]
        public void ABasedOnDeclarationAlsoRefusesAnExplicitTypeThatDisagrees()
        {
            var document = XDocument.Parse(Xml);
            JObject args = OnlyTheName("cust");
            args["basicType"] = "Numeric";
            args["length"] = 9;

            JObject refusal = WwpActionService.ApplyAddGridVariableXml(document, args,
                Declared("cust", "CHARACTER", 40, basedOn: "adbb33c9-0906-4971-833c-998de27e0676-CustomerName"));

            Assert.Equal("GridVariableTypeMismatch", refusal["code"]?.ToString());
            Assert.Empty(document.Descendants("gridVariable"));
        }

        // A client that sends an explicit null for an optional argument means the same as leaving it out: the declaration
        // decides the type (a null length is not a length that disagrees) and the defaults apply.
        [Fact]
        public void AnExplicitNullForAnOptionalArgumentMeansTheSameAsOmittingIt()
        {
            JObject declaredArgs = OnlyTheName();
            foreach (string key in new[] { "basicType", "length", "decimals", "readOnly" }) declaredArgs[key] = JValue.CreateNull();
            var declaredDocument = XDocument.Parse(Xml);

            Assert.Null(WwpActionService.ApplyAddGridVariableXml(declaredDocument, declaredArgs, Declared("tstnom", "CHARACTER", 30))["error"]);
            var declaredColumn = declaredDocument.Descendants("gridVariable").Single();
            Assert.Equal("30", (string)declaredColumn.Attribute("basicCLength"));
            Assert.Equal("True", (string)declaredColumn.Attribute("readOnly"));

            JObject undeclaredArgs = Add();
            undeclaredArgs["basicType"] = "Numeric";
            undeclaredArgs["length"] = 6;
            undeclaredArgs["decimals"] = JValue.CreateNull();
            undeclaredArgs["readOnly"] = JValue.CreateNull();
            var undeclaredDocument = XDocument.Parse(Xml);

            Assert.Null(WwpActionService.ApplyAddGridVariableXml(undeclaredDocument, undeclaredArgs)["error"]);
            var undeclaredColumn = undeclaredDocument.Descendants("gridVariable").Single();
            Assert.Equal("6", (string)undeclaredColumn.Attribute("basicLength"));
            Assert.Equal("0", (string)undeclaredColumn.Attribute("basicDecimals"));
            Assert.Equal("True", (string)undeclaredColumn.Attribute("readOnly"));
        }

        [Fact]
        public void AnExplicitTypeThatAgreesWithTheDeclarationIsAccepted()
        {
            var document = XDocument.Parse(Xml);
            JObject args = OnlyTheName();
            args["basicType"] = "character";
            args["length"] = 30;

            Assert.Null(WwpActionService.ApplyAddGridVariableXml(document, args, Declared("tstnom", "CHARACTER", 30))["error"]);
        }

        [Theory]
        [InlineData("GX_SDT", false)]
        [InlineData("GX_BUSCOMP", false)]
        [InlineData("CHARACTER", true)]
        public void AVariableWithoutAGridTypeIsRefused(string dbType, bool collection)
        {
            var document = XDocument.Parse(Xml);
            var before = new XDocument(document);

            JObject refusal = WwpActionService.ApplyAddGridVariableXml(document, OnlyTheName("x"), Declared("x", dbType, 10, isCollection: collection));
            Assert.Equal("GridVariableTypeUnsupported", refusal["code"]?.ToString());
            Assert.True(XNode.DeepEquals(before, document), "a refused add must not mutate the document");
        }

        [Fact]
        public void AVariableReferenceIsOptionalAndOnlyHasToNameTheVariable()
        {
            var args = Add();
            args["variableReference"] = "33333333-3333-3333-3333-333333333333-ReinicioHistorico";
            Assert.Null(WwpActionService.ApplyAddGridVariableXml(XDocument.Parse(Xml), args)["error"]);

            args["variableReference"] = "33333333-3333-3333-3333-333333333333-Outro";
            JObject refusal = WwpActionService.ApplyAddGridVariableXml(XDocument.Parse(Xml), args);
            Assert.Equal("GridVariableIdentityMismatch", refusal["code"]?.ToString());
            Assert.Contains("identifies a grid variable by its name", refusal["error"]?.ToString());

            // The reference never reaches the XML.
            var document = XDocument.Parse(Xml);
            args["variableReference"] = "ReinicioHistorico";
            WwpActionService.ApplyAddGridVariableXml(document, args);
            Assert.Null(document.Descendants("gridVariable").Single().Attribute("variable"));
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
        [InlineData("Varchar(10)")]
        [InlineData("Text")]
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
        [InlineData("Numeric", 6, "basicLength")]
        [InlineData("Character", 20, "basicCLength")]
        [InlineData("LongVarChar", 4000, "basicLVCLength")]
        public void EveryTypeTheVariableGridKnowsCanBeAddedWithoutADeclaration(string basicType, int length, string lengthAttribute)
        {
            var document = XDocument.Parse(Xml);
            var args = Add();
            args["basicType"] = basicType;
            args["length"] = length;

            Assert.Null(WwpActionService.ApplyAddGridVariableXml(document, args)["error"]);
            Assert.Equal(length.ToString(), (string)document.Descendants("gridVariable").Single().Attribute(lengthAttribute));
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
        public void ANameIsCaseInsensitivelyUniqueAmongTheColumns()
        {
            var document = XDocument.Parse(Xml);
            document.Descendants("grid").Single().Add(
                new XElement("gridVariable", new XAttribute("name", "reiniciohistorico")));

            Assert.Equal("GridVariableAlreadyExists", WwpActionService.ApplyAddGridVariableXml(document, Add())["code"]?.ToString());
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
