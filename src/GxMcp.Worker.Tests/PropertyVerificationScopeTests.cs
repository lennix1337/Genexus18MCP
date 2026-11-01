using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The parts a property rollback compares. WebForm and Documentation are restored with the object but not compared: their
    /// CDATA text carries layout ids and empty default entries that a byte comparison cannot separate from a change. Every
    /// other part keeps the exact comparison.
    /// </summary>
    public class PropertyVerificationScopeTests
    {
        [Theory]
        [InlineData("WebForm")]
        [InlineData("Documentation")]
        [InlineData("documentation")]
        [InlineData(" WebForm ")]
        public void WebFormAndDocumentationAreNotCompared(string part)
        {
            Assert.False(PropertyVerificationScope.IsCompared(part));
        }

        [Theory]
        [InlineData("Events")]
        [InlineData("Rules")]
        [InlineData("Variables")]
        [InlineData("Help")]
        [InlineData("PatternInstance-inst")]
        public void TextPartsKeepTheExactComparison(string part)
        {
            Assert.True(PropertyVerificationScope.IsCompared(part));
        }

        [Fact]
        public void TheNotComparedPartsAreListedOnceInTheOrderGiven()
        {
            string[] listed = PropertyVerificationScope.NotComparedOf(new[] { "Events", "WebForm", "", "Documentation", "WebForm", "Rules" });

            Assert.Equal(new[] { "WebForm", "Documentation" }, listed);
        }
    }
}
