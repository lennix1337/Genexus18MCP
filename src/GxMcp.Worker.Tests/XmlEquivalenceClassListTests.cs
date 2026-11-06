using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The SDK resolves symbolic theme-class names to <c>&lt;guid&gt;-&lt;int&gt;</c> when it persists a layout. A
    /// class attribute may list several classes, so the tolerance must work per class, not on the whole value.
    /// Synthetic GUIDs only.
    /// </summary>
    public class XmlEquivalenceClassListTests
    {
        private const string Ds = "aaaaaaaa-1111-4222-8333-bbbbbbbbbbbb";

        private static string Text(string attribute, string value) =>
            "<layout><textblock " + attribute + "=\"" + value + "\" name=\"x\"/></layout>";

        private static bool Equivalent(string attribute, string left, string right) =>
            XmlEquivalence.AreEquivalent(Text(attribute, left), Text(attribute, right), out _);

        [Theory]
        [InlineData("Attribute", Ds + "-4")]
        [InlineData(Ds + "-131", "TableGrid")]
        public void ASingleSymbolicClassAgainstItsResolvedFormIsEquivalent(string left, string right)
        {
            Assert.True(Equivalent("class", left, right));
        }

        [Fact]
        public void SeveralClassesWithTheTrailingOnesResolvedAreEquivalent()
        {
            // Projected keeps the names; the reread resolved them (first class already resolved on both sides).
            Assert.True(Equivalent("class",
                Ds + "-1466 IconBase IconKey",
                Ds + "-1466 " + Ds + "-12 " + Ds + "-13"));
        }

        [Fact]
        public void TheResolutionMayRunInEitherDirectionAtDifferentPositions()
        {
            Assert.True(Equivalent("themeClass",
                "Title " + Ds + "-7 Footer",
                Ds + "-3 Body " + Ds + "-9"));
        }

        [Fact]
        public void AnUnchangedPositionIsComparedIgnoringCase()
        {
            Assert.True(Equivalent("class", "Title icon", "title " + Ds + "-5"));
        }

        [Theory]
        [InlineData(Ds + "-1 IconBase", Ds + "-1 " + Ds + "-2 " + Ds + "-3")]
        [InlineData(Ds + "-1 " + Ds + "-2", Ds + "-1")]
        public void ADifferentNumberOfClassesIsADifference(string left, string right)
        {
            Assert.False(Equivalent("class", left, right));
        }

        [Fact]
        public void TwoDifferentResolvedClassesAtTheSamePositionAreADifference()
        {
            Assert.False(Equivalent("class", Ds + "-1 " + Ds + "-2", Ds + "-1 " + Ds + "-3"));
        }

        [Fact]
        public void TwoDifferentSymbolicNamesAtTheSamePositionAreADifference()
        {
            Assert.False(Equivalent("class", "IconBase " + Ds + "-2", "IconOther " + Ds + "-2"));
        }

        [Fact]
        public void OneMismatchedPositionIsEnoughToRejectEvenWhenAnotherPositionResolved()
        {
            Assert.False(Equivalent("class", "IconBase Other", Ds + "-4 Different"));
        }

        [Fact]
        public void ADifferenceOnlyInCaseIsStillADifference()
        {
            Assert.False(Equivalent("class", "Title Body", "title body"));
        }

        [Fact]
        public void TheToleranceStaysLimitedToTheClassFamilyOfAttributes()
        {
            Assert.False(Equivalent("caption", "Title " + Ds + "-4", Ds + "-3 Body"));
            Assert.False(Equivalent("caption", "Attribute", Ds + "-4"));
        }

        // Issue #880: design-system utility classes ("u-w-14", "u-border-gray-80") carry dashes. They are
        // symbolic names like any other, and the reread resolves them to <guid>-<int> the same way.
        [Fact]
        public void DashedUtilityClassNamesAgainstTheirResolvedFormAreEquivalent()
        {
            Assert.True(Equivalent("themeClass",
                "u-w-14 u-border u-border-gray-80",
                Ds + "-1 " + Ds + "-2 " + Ds + "-3"));
        }

        [Fact]
        public void ADashedNameAgainstADifferentDashedNameIsADifference()
        {
            Assert.False(Equivalent("themeClass", "u-w-14 u-border", "u-w-16 " + Ds + "-2"));
        }

        [Fact]
        public void AResolvedFormIsNeverTakenForASymbolicName()
        {
            Assert.False(Equivalent("themeClass", Ds + "-1", "bbbbbbbb-1111-4222-8333-cccccccccccc-2"));
        }
    }
}
