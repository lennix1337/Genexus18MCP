using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatternXmlEditPlanTests
    {
        // Deliberately uses an order list that differs from document order.
        private const string Original = "<instance templateId='sample' DefaultMode='true'><table name='Main' childrenOrderedList='2;27;B|2;27;A' class='Table'><textBlock name='A' caption='Alpha'/><textBlock name='B' caption='Beta'/></table></instance>";

        [Fact]
        public void OriginalXmlIsNoOpEvenWhenReconcilerWouldChangeIt()
        {
            var doc = XDocument.Parse(Original);
            Assert.True(PatternChildOrderReconciler.Reconcile(doc).ParentsUpdated > 0);
            var plan = PatternXmlEditPlan.Create(Original, Original);
            Assert.True(plan.IsNoChange);
            Assert.Equal(Original, plan.Xml);
            Assert.Empty(plan.Changes);
        }

        [Fact]
        public void DoesNotInventListsOrRemoveEntriesWithoutDirectChildren()
        {
            const string xml = "<instance><table childrenOrderedList='2;27;SyntheticGhost'><table><textBlock name='Message' caption='Old'/></table></table></instance>";
            var requested = xml.Replace("Old", "New");
            var plan = PatternXmlEditPlan.Create(xml, requested);
            Assert.Null(plan.ErrorCode);
            Assert.Equal(requested, plan.Xml);
            Assert.Single(XDocument.Parse(plan.Xml).Descendants().Attributes("childrenOrderedList"));
            Assert.Single(plan.Changes);
        }

        [Theory]
        [InlineData("class='Table'", "class='Table entry'")]
        [InlineData("caption='Alpha'", "caption='Hello'")]
        public void PropertyChangeHasExactDiffAndDoesNotNormalizePayload(string from, string to)
        {
            var requested = Original.Replace(from, to);
            var plan = PatternXmlEditPlan.Create(Original, requested);
            Assert.Null(plan.ErrorCode);
            Assert.False(plan.IsNoChange);
            Assert.Equal(requested, plan.Xml);
            Assert.Single(plan.Changes);
            Assert.Equal(from.Split('\'')[1], (string)plan.Changes[0]["before"]);
            Assert.Equal(to.Split('\'')[1], (string)plan.Changes[0]["after"]);
            Assert.Contains("childrenOrderedList='2;27;B|2;27;A'", plan.Xml);
        }

        [Theory]
        [InlineData("DefaultMode='true'", "DefaultMode='false'")]
        [InlineData("templateId='sample'", "templateId='other'")]
        [InlineData("name='A'", "name='Renamed'")]
        [InlineData("childrenOrderedList='2;27;B|2;27;A'", "childrenOrderedList='2;27;A|2;27;B'")]
        [InlineData("DefaultMode='true'", "")]
        [InlineData("<table name", "<table id='new' name")]
        public void MetadataChangesAreRejectedWithoutProducingAPartialPlan(string from, string to)
        {
            var plan = PatternXmlEditPlan.Create(Original, Original.Replace("class='Table'", "class='Changed'").Replace(from, to));
            Assert.Equal("PatternMetadataChangeUnsupported", plan.ErrorCode);
            Assert.Empty(plan.Changes);
        }

        [Theory]
        [InlineData("<textBlock name='A' caption='Alpha'/>", "")]
        [InlineData("</table>", "<table name='New'/></table>")]
        [InlineData("<textBlock name='A' caption='Alpha'/><textBlock name='B' caption='Beta'/>", "<textBlock name='B' caption='Beta'/><textBlock name='A' caption='Alpha'/>")]
        public void StructuralEditsAreRejected(string from, string to)
        {
            var plan = PatternXmlEditPlan.Create(Original, Original.Replace(from, to));
            Assert.NotNull(plan.ErrorCode);
            Assert.Empty(plan.Changes);
        }

        [Fact]
        public void NamespacesAndTextRemainUntouchedForPropertyEdits()
        {
            const string xml = "<p:instance xmlns:p='urn:pattern'><p:table name='Main' p:class='Old'>keep &amp; preserve<![CDATA[ literal ]]><!-- note --></p:table></p:instance>";
            var requested = xml.Replace("p:class='Old'", "p:class='New'");
            var plan = PatternXmlEditPlan.Create(xml, requested);
            Assert.Null(plan.ErrorCode);
            Assert.Equal(requested, plan.Xml);
            Assert.Contains("{urn:pattern}class", (string)plan.Changes[0]["path"]);
            Assert.NotNull(PatternXmlEditPlan.Create(xml, xml.Replace("urn:pattern", "urn:other")).ErrorCode);
            Assert.NotNull(PatternXmlEditPlan.Create(xml, xml.Replace("preserve", "change")).ErrorCode);
        }

        [Fact]
        public void MetadataRepresentedAsElementsIsProtected()
        {
            const string xml = "<instance><DefaultValues><property value='old'/></DefaultValues></instance>";
            Assert.Equal("PatternMetadataChangeUnsupported", PatternXmlEditPlan.Create(xml, xml.Replace("old", "new")).ErrorCode);
        }

        [Fact]
        public void AttributeNodeCaptionCanChangeWithoutChangingItsIdentity()
        {
            const string xml = "<instance><attribute attribute='field-ref' caption='Old'/></instance>";
            Assert.Null(PatternXmlEditPlan.Create(xml, xml.Replace("Old", "New")).ErrorCode);
            Assert.Equal("PatternMetadataChangeUnsupported", PatternXmlEditPlan.Create(xml, xml.Replace("field-ref", "other-ref")).ErrorCode);
        }

        [Fact]
        public void FormattingOnlyIsNoOpButSignificantWhitespaceIsNot()
        {
            Assert.True(PatternXmlEditPlan.Create(Original, Original.Replace("><", ">\n  <")).IsNoChange);
            const string text = "<instance><value> </value></instance>";
            Assert.NotNull(PatternXmlEditPlan.Create(text, text.Replace("> <", ">  <")).ErrorCode);
        }

        [Theory]
        [InlineData("label")]
        [InlineData("<![CDATA[ ]]>")]
        public void MixedContentWhitespaceCannotChangeAlongsideProperty(string text)
        {
            var xml = "<instance>" + text + "<table class='Old'/> </instance>";
            var propertyOnly = xml.Replace("Old", "New");
            Assert.Null(PatternXmlEditPlan.Create(xml, propertyOnly).ErrorCode);
            var plan = PatternXmlEditPlan.Create(xml, propertyOnly.Replace("/> </", "/>  </"));
            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Empty(plan.Changes);
        }

        [Theory]
        [InlineData("<?change command?>")]
        [InlineData("<!-- altered -->")]
        [InlineData("<!DOCTYPE instance [<!ENTITY example 'value'>]>")]
        public void AddedDocumentMetadataIsRejected(string prefix)
        {
            Assert.Equal("PatternStructureChangeUnsupported", PatternXmlEditPlan.Create(Original, prefix + Original).ErrorCode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<broken")]
        public void UnreadableCurrentXmlFailsClosed(string current)
        {
            Assert.Equal("PatternReadFailed", PatternXmlEditPlan.Create(current, Original).ErrorCode);
        }

        [Fact]
        public void InvalidInputIsRejected()
        {
            Assert.Equal("PatternInvalidXml", PatternXmlEditPlan.Create(Original, "<broken").ErrorCode);
        }
    }
}
