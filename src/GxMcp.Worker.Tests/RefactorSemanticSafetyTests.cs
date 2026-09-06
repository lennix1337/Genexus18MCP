using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class RefactorSemanticSafetyTests
    {
        [Fact]
        public void Rewrite_PreservesCommentsStringsAndLargerIdentifiers()
        {
            string source =
                "// OldName\r\n" +
                "msg('OldName'); /* OldName */\r\n" +
                "Module.OldName(); OldNameId = OldName;\r\n";

            string rewritten = SymbolRenameTokenizer.Rewrite(source, "OldName", "NewName", out int replacements);

            Assert.Equal(2, replacements);
            Assert.Contains("// OldName", rewritten);
            Assert.Contains("msg('OldName')", rewritten);
            Assert.Contains("/* OldName */", rewritten);
            Assert.Contains("Module.NewName();", rewritten);
            Assert.Contains("OldNameId = NewName;", rewritten);
        }

        [Fact]
        public void Find_ReturnsStableLineAndColumnForExecutableReferences()
        {
            var matches = SymbolRenameTokenizer.Find("A();\r\n  Module.A();", "A");

            Assert.Equal(2, matches.Count);
            Assert.Equal(1, matches[0].Line);
            Assert.Equal(1, matches[0].Column);
            Assert.Equal(2, matches[1].Line);
            Assert.Equal(10, matches[1].Column);
            Assert.Equal(15, matches[1].Offset);
        }

        [Fact]
        public void Rewrite_DoesNotTreatEscapedOrDoubledQuotesAsCode()
        {
            string source = "msg(\"OldName\\\"\"); msg('OldName''still string'); OldName();";

            string rewritten = SymbolRenameTokenizer.Rewrite(source, "OldName", "NewName", out int replacements);

            Assert.Equal(1, replacements);
            Assert.Contains("OldName\\\"", rewritten);
            Assert.Contains("'OldName''still string'", rewritten);
            Assert.EndsWith("NewName();", rewritten);
        }

        [Fact]
        public void RewritePrefixed_OnlyRenamesGeneXusVariablesOutsideCommentsAndStrings()
        {
            string source = "// &Old\nmsg('&Old'); &Old = &OldId;";

            string rewritten = SymbolRenameTokenizer.RewritePrefixed(source, "Old", "New", out int replacements);

            Assert.Equal(1, replacements);
            Assert.Contains("// &Old", rewritten);
            Assert.Contains("msg('&Old')", rewritten);
            Assert.Contains("&New = &OldId", rewritten);
        }
    }
}
