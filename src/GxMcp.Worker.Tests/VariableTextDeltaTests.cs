using System;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class VariableTextDeltaTests
    {
        [Theory]
        [InlineData("&Amount : Numeric(10)", "amount : NUMERIC(10,0)", true)]
        [InlineData("&Data : Blob(12)", "&Data : BINARY(12)", true)]
        [InlineData("&Amount : Numeric(10,2)", "&Amount : Numeric(10)", false)]
        [InlineData("&Code : Attribute:Code", "&Code : Character(12)", false)]
        [InlineData("&Rows : RowSdt Collection", "&Rows : RowSdt", false)]
        [InlineData("&Vector(3) : Numeric(4)", "&Vector(4) : Numeric(4)", false)]
        public void EqualityIncludesTypeBindingsCollectionAndDimensions(string before, string after, bool equal)
        {
            Assert.True(VariableDeclarationParser.TryParse(before, out var a));
            Assert.True(VariableDeclarationParser.TryParse(after, out var b));
            Assert.Equal(equal, VariableDeclarationSet.AreEquivalent(a, b));
        }

        [Theory]
        [InlineData("&A : Numeric(4)\nnot a declaration")]
        [InlineData("&A : Numeric(4)\n&a : Numeric(8)")]
        [InlineData("&A(2) : Numeric(4) Collection")]
        [InlineData("&A : Numeric(4,8)")]
        public void WholeRequestParsingRejectsInvalidOrDuplicateDeclarations(string text)
        {
            Assert.Throws<ArgumentException>(() => VariableDeclarationSet.ParseAll(text));
        }

        [Fact]
        public void InjectorPrevalidatesThenSkipsExistingEquivalentDeclarationsBeforeSetters()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Helpers", "VariableInjector.cs");
            string body = SourceAssert.MethodBody(source, "public static void SetVariablesFromText(");
            int validate = body.IndexOf("ValidateVariablesText(part, text)", StringComparison.Ordinal);
            int skip = body.IndexOf("VariableDeclarationSet.AreEquivalent", StringComparison.Ordinal);
            int set = body.IndexOf("v.Type = dbType", StringComparison.Ordinal);
            Assert.True(validate >= 0 && validate < skip && skip < set);
            Assert.Contains("continue;", body.Substring(skip, set - skip));
        }
    }
}
