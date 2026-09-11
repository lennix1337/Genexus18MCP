using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class XmlEquivalenceTests
    {
        [Fact]
        public void IdenticalXmlIsEquivalent()
        {
            var a = "<root a=\"1\"><c/></root>";
            Assert.True(XmlEquivalence.AreEquivalent(a, a, out _));
        }

        [Fact]
        public void AttributeOrderIsIgnored()
        {
            var a = "<r a=\"1\" b=\"2\"/>";
            var b = "<r b=\"2\" a=\"1\"/>";
            Assert.True(XmlEquivalence.AreEquivalent(a, b, out _));
        }

        [Fact]
        public void InsignificantWhitespaceIsIgnored()
        {
            var a = "<r><c/></r>";
            var b = "<r>\r\n  <c/>\r\n</r>";
            Assert.True(XmlEquivalence.AreEquivalent(a, b, out _));
        }

        [Fact]
        public void LineEndingDifferencesIgnored()
        {
            var a = "<r>\r\n<c x=\"1\"/>\r\n</r>";
            var b = "<r>\n<c x=\"1\"/>\n</r>";
            Assert.True(XmlEquivalence.AreEquivalent(a, b, out _));
        }

        [Fact]
        public void AttributeValueDifferenceIsDetected()
        {
            var a = "<r x=\"1\"/>";
            var b = "<r x=\"2\"/>";
            Assert.False(XmlEquivalence.AreEquivalent(a, b, out var diff));
            Assert.Contains("x", diff);
        }

        [Fact]
        public void ChildCountDifferenceIsDetected()
        {
            var a = "<r><c/></r>";
            var b = "<r><c/><c/></r>";
            Assert.False(XmlEquivalence.AreEquivalent(a, b, out var diff));
            Assert.Contains("Child count", diff);
        }

        [Fact]
        public void EmptyWebComponentParametersInjectedBySdkAreEquivalent()
        {
            var persisted = "<instance><table><webComponent name='SelectorComponent' gxobject='type-WC'><parameters /></webComponent></table></instance>";
            var requested = "<instance><table><webComponent name='SelectorComponent' gxobject='type-WC' /></table></instance>";
            Assert.True(XmlEquivalence.AreEquivalent(persisted, requested, out var diff), diff);
        }

        [Fact]
        public void NonEmptyWebComponentParametersRemainStrict()
        {
            var persisted = "<instance><table><webComponent name='SelectorComponent' gxobject='type-WC'><parameters><parameter name='&amp;Id' /></parameters></webComponent></table></instance>";
            var requested = "<instance><table><webComponent name='SelectorComponent' gxobject='type-WC' /></table></instance>";
            Assert.False(XmlEquivalence.AreEquivalent(persisted, requested, out _));
        }

        [Fact]
        public void ElementNameDifferenceIsDetected()
        {
            var a = "<r><a/></r>";
            var b = "<r><b/></r>";
            Assert.False(XmlEquivalence.AreEquivalent(a, b, out var diff));
            Assert.Contains("name differs", diff);
        }

        [Fact]
        public void TextContentTrimmedComparison()
        {
            var a = "<r>hello</r>";
            var b = "<r>  hello  </r>";
            Assert.True(XmlEquivalence.AreEquivalent(a, b, out _));
        }

        [Fact]
        public void MissingAttributeIsDetected()
        {
            var a = "<r x=\"1\" y=\"2\"/>";
            var b = "<r x=\"1\"/>";
            Assert.False(XmlEquivalence.AreEquivalent(a, b, out var diff));
            Assert.Contains("Attribute count", diff);
        }

        [Fact]
        public void RealisticPatternConditionChangeDetected()
        {
            var a = "<instance>\r\n  <gridAttribute name=\"DocCod\" conditions=\"CodTipOri = 24;\"/>\r\n</instance>";
            var b = "<instance>\r\n  <gridAttribute name=\"DocCod\" conditions=\"DocTipOri = 24;\"/>\r\n</instance>";
            Assert.False(XmlEquivalence.AreEquivalent(a, b, out var diff));
            Assert.Contains("conditions", diff);
        }

        [Fact]
        public void BothEmptyEquivalent()
        {
            Assert.True(XmlEquivalence.AreEquivalent(null, null, out _));
            Assert.True(XmlEquivalence.AreEquivalent("", "", out _));
        }
    }
}
