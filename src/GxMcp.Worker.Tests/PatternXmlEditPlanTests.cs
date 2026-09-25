using System.Linq;
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
        public void AllowsOnlyAWebComponentInsertionIntoAnExistingTable()
        {
            const string current = "<instance><table name='Header' childrenOrderedList='2;18;Admin'><userAction name='Admin' gxobject='old'/></table></instance>";
            const string requested = "<instance><table name='Header' childrenOrderedList='2;18;Admin'><userAction name='Admin' gxobject='old'/><webComponent name='SelectorComponent' gxobject='type-SelectorWebComponent'/></table></instance>";

            var plan = PatternXmlEditPlan.Create(current, requested);

            Assert.Null(plan.ErrorCode);
            Assert.Single(plan.Changes);
            Assert.Equal("Insert", (string)plan.Changes[0]["operation"]);
            Assert.Contains("webComponent", (string)plan.Changes[0]["path"]);
            Assert.Equal(requested, plan.Xml);
        }

        [Fact]
        public void GridColumnReorderIsAllowedOnlyThroughTheGridAuthoringMode()
        {
            const string current = "<instance><grid name='Main' childrenOrderedList='A,B'><gridAttribute attribute='A'/><gridAttribute attribute='B'/></grid></instance>";
            const string requested = "<instance><grid name='Main' childrenOrderedList='B,A'><gridAttribute attribute='B'/><gridAttribute attribute='A'/></grid></instance>";

            Assert.NotNull(PatternXmlEditPlan.Create(current, requested).ErrorCode);
            var plan = PatternXmlEditPlan.Create(current, requested, allowGridStructure: true);
            Assert.Null(plan.ErrorCode);
            Assert.Contains(plan.Changes, change => (string)change["operation"] == "GridOrder");
        }

        [Fact]
        public void GridColumnReorderAllowsNonColumnChildrenAndPreservesTheirContent()
        {
            const string current =
                "<instance><grid name='Main'>" +
                "<gridAttribute attribute='1-First'/>" +
                "<action name='Keep'/>" +
                "<gridAttribute attribute='2-Second'/>" +
                "<filter value='Keep'/>" +
                "</grid></instance>";
            const string requested =
                "<instance><grid name='Main'>" +
                "<gridAttribute attribute='2-Second'/>" +
                "<gridAttribute attribute='1-First'/>" +
                "<action name='Keep'/>" +
                "<filter value='Keep'/>" +
                "</grid></instance>";

            var plan = PatternXmlEditPlan.Create(current, requested, allowGridStructure: true);
            Assert.Null(plan.ErrorCode);
            Assert.Equal(requested, plan.Xml);
            Assert.Contains(plan.Changes, change => (string)change["operation"] == "GridOrder");

            var altered = requested.Replace("value='Keep'", "value='Changed'");
            var rejected = PatternXmlEditPlan.Create(current, altered, allowGridStructure: true);
            Assert.NotNull(rejected.ErrorCode);
            Assert.Empty(rejected.Changes);
            var changedUnselectedColumn = requested.Replace(
                "<gridAttribute attribute='1-First'/>",
                "<gridAttribute attribute='1-First' description='Changed'/>");
            Assert.NotNull(PatternXmlEditPlan.Create(
                current, changedUnselectedColumn, allowGridStructure: true).ErrorCode);
            Assert.NotNull(PatternXmlEditPlan.Create(current, requested).ErrorCode);

            // Moving the first column to the same relative column position can
            // still move it across an interleaved action child.
            const string forwardCurrent =
                "<instance><grid><gridAttribute attribute='1-First'/>" +
                "<action name='Keep'/><gridAttribute attribute='2-Second'/></grid></instance>";
            const string forwardRequested =
                "<instance><grid><action name='Keep'/>" +
                "<gridAttribute attribute='1-First'/><gridAttribute attribute='2-Second'/></grid></instance>";
            var forward = PatternXmlEditPlan.Create(forwardCurrent, forwardRequested, allowGridStructure: true);
            Assert.Null(forward.ErrorCode);
            Assert.Contains(forward.Changes, change => (string)change["operation"] == "GridOrder");
        }

        [Fact]
        public void GridVariableInsertionIsNarrowlyAllowedButArbitraryGridEditsRemainRejected()
        {
            const string current = "<instance><grid name='Main'><gridAttribute attribute='A'/></grid></instance>";
            const string requested = "<instance><grid name='Main'><gridAttribute attribute='A'/><gridVariable name='V' variable='11111111-1111-1111-1111-111111111111-V' basicType='VarChar' basicCLength='10'/></grid></instance>";
            var allowed = PatternXmlEditPlan.Create(current, requested, allowGridStructure: true);
            Assert.Null(allowed.ErrorCode);
            Assert.Contains(allowed.Changes, change => (string)change["operation"] == "Insert");

            var rejected = PatternXmlEditPlan.Create(current, requested);
            Assert.Equal("PatternStructureChangeUnsupported", rejected.ErrorCode);
            Assert.Empty(rejected.Changes);

            const string arbitrary = "<instance><grid name='Main'><gridAttribute attribute='A'/><footer name='Changed'/></grid></instance>";
            Assert.NotNull(PatternXmlEditPlan.Create(current, arbitrary, allowGridStructure: true).ErrorCode);
        }

        [Fact]
        public void RejectsAWebComponentWithUnsupportedStructureOrMetadata()
        {
            const string current = "<instance><table name='Header'><userAction name='Admin'/></table></instance>";
            const string unsupported = "<instance><table name='Header'><userAction name='Admin'/><webComponent name='SelectorComponent' gxobject='type-SelectorWebComponent' childrenOrderedList='x'/></table></instance>";

            var plan = PatternXmlEditPlan.Create(current, unsupported);

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
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
