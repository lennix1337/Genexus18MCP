using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.TestSupport;
using GxMcp.Worker.Parsers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #955: a Transaction with a sub-level could not be edited through the Structure
    // DSL. The text the tool itself serialized came back with the level's attributes at the
    // top level and the level empty, so the write was rejected with
    // "O nível 'Level1' está vazio" and rolled back.
    public class Issue955MultiLevelTransactionDslTests
    {
        private const string TwoLevels =
            "TrnId* : NUMERIC(4) // \"Trn Id\"\n" +
            "TrnName : VARCHAR(40)\n" +
            "    Level1\n" +
            "    {\n" +
            "        L1Seq* : NUMERIC(3)\n" +
            "        L1Txt : VARCHAR(20)\n" +
            "    }\n" +
            "    Level2\n" +
            "    {\n" +
            "        L2Seq* : NUMERIC(3)\n" +
            "    }";

        private static DslParserUtils.ParsedNode Level(System.Collections.Generic.List<DslParserUtils.ParsedNode> nodes, string name) =>
            nodes.Single(n => n.IsCompound && n.Name == name);

        [Fact]
        public void IndentedLevel_KeepsItsAttributesInsideTheLevel()
        {
            var nodes = TransactionDslParser.FlattenRoot(TransactionDslParser.ParseNodes(TwoLevels));

            Assert.Equal(new[] { "TrnId", "TrnName", "Level1", "Level2" }, nodes.Select(n => n.Name).ToArray());
            Assert.Equal(new[] { "L1Seq", "L1Txt" }, Level(nodes, "Level1").Children.Select(n => n.Name).ToArray());
            Assert.Equal(new[] { "L2Seq" }, Level(nodes, "Level2").Children.Select(n => n.Name).ToArray());
        }

        [Fact]
        public void LevelWithoutIndentation_IsDelimitedByItsBraces()
        {
            // Text written by hand or by a client usually carries no indentation: the braces
            // alone say where the level begins and ends.
            var text = "TrnId* : NUMERIC(4)\nLevel1\n{\nL1Seq* : NUMERIC(3)\nL1Txt : VARCHAR(20)\n}\nTrnName : VARCHAR(40)";

            var nodes = TransactionDslParser.FlattenRoot(TransactionDslParser.ParseNodes(text));

            Assert.Equal(new[] { "TrnId", "Level1", "TrnName" }, nodes.Select(n => n.Name).ToArray());
            Assert.Equal(new[] { "L1Seq", "L1Txt" }, Level(nodes, "Level1").Children.Select(n => n.Name).ToArray());
        }

        [Fact]
        public void NestedLevels_CloseOneBraceAtATime()
        {
            var text = "TrnId* : NUMERIC(4)\nOuter\n{\nOuterId* : NUMERIC(3)\nInner\n{\nInnerId* : NUMERIC(3)\n}\nOuterTxt : VARCHAR(10)\n}\nTail : VARCHAR(5)";

            var nodes = TransactionDslParser.FlattenRoot(TransactionDslParser.ParseNodes(text));

            Assert.Equal(new[] { "TrnId", "Outer", "Tail" }, nodes.Select(n => n.Name).ToArray());
            var outer = Level(nodes, "Outer");
            Assert.Equal(new[] { "OuterId", "Inner", "OuterTxt" }, outer.Children.Select(n => n.Name).ToArray());
            Assert.Equal(new[] { "InnerId" }, Level(outer.Children, "Inner").Children.Select(n => n.Name).ToArray());
        }

        [Fact]
        public void TabIndentedLevel_KeepsItsAttributesInsideTheLevel()
        {
            var text = "TrnId* : NUMERIC(4)\n\tLevel1\n\t{\n\t\tL1Seq* : NUMERIC(3)\n\t}";

            var nodes = TransactionDslParser.FlattenRoot(TransactionDslParser.ParseNodes(text));

            Assert.Equal(new[] { "L1Seq" }, Level(nodes, "Level1").Children.Select(n => n.Name).ToArray());
        }

        [Fact]
        public void SplitDslLines_KeepsTheIndentationOfUnwrappedLines()
        {
            var lines = TransactionDslParser.SplitDslLines("A : NUMERIC(1)\n    Level1\n    {\n        B : NUMERIC(1)\n    }");

            Assert.Equal("    Level1", lines[1]);
            Assert.Equal("        B : NUMERIC(1)", lines[3]);
        }

        [Fact]
        public void StructureDiffNamesTheMembersThatDidNotPersist()
        {
            var diff = new JArray
            {
                new JObject { ["path"] = "children/P955Root", ["requested"] = new JObject { ["name"] = "P955Root" }, ["persisted"] = JValue.CreateNull() },
                new JObject { ["path"] = "children/Level1/children/L1Txt/type", ["requested"] = "VARCHAR(30)", ["persisted"] = "VARCHAR(20)" }
            };

            string text = StructureService.DescribeStructureDiff(diff);

            Assert.Contains("children/P955Root (requested member, not found after save)", text);
            Assert.Contains("children/Level1/children/L1Txt/type (requested VARCHAR(30), persisted VARCHAR(20))", text);
        }

        [Fact]
        public void VisualUpdateCreatesMissingAttributes()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "Structure", "VisualStructureService.cs");

            Assert.Contains("CreateGlobalAttribute(name, vItem)", source);
            Assert.Contains("_problems.Add(", source);
        }

        [Fact]
        public void VisualUpdateReportsUnappliedMembersAndTheValidatorMessage()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "StructureService.cs");
            int start = source.IndexOf("_visualStructureService.SyncVisualStructure(locked, children);");
            Assert.True(start >= 0, "SyncVisualStructure call not found");
            string flow = source.Substring(start, 1800);

            Assert.Contains("_visualStructureService.Problems", flow);
            Assert.Contains("WithValidationDetail(", flow);
            Assert.Contains("DescribeStructureDiff(diff)", source);
        }
    }
}
