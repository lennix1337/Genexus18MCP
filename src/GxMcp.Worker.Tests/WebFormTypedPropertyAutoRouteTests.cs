using System.IO;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WebFormTypedPropertyAutoRouteTests
    {
        [Fact]
        public void OnClickEvent_OnGxButton_RoutesToEvent()
        {
            Assert.Equal("Event", WebFormTypedPropertyWriter.ResolveCanonicalAttr("OnClickEvent", "gxButton"));
        }

        [Fact]
        public void OnClickEvent_OnGxAttribute_RoutesToEventGX()
        {
            Assert.Equal("eventGX", WebFormTypedPropertyWriter.ResolveCanonicalAttr("OnClickEvent", "gxAttribute"));
            Assert.Equal("eventGX", WebFormTypedPropertyWriter.ResolveCanonicalAttr("OnClickEvent", "gxImage"));
            Assert.Equal("eventGX", WebFormTypedPropertyWriter.ResolveCanonicalAttr("OnClickEvent", "gxBitmap"));
        }

        [Fact]
        public void OnClickEvent_UnknownElement_FallsBackToEvent()
        {
            Assert.Equal("Event", WebFormTypedPropertyWriter.ResolveCanonicalAttr("OnClickEvent", "gxFoo"));
        }

        [Fact]
        public void CaptionExpression_RoutesToCaption()
        {
            Assert.Equal("Caption", WebFormTypedPropertyWriter.ResolveCanonicalAttr("CaptionExpression", "gxButton"));
            Assert.Equal("Caption", WebFormTypedPropertyWriter.ResolveCanonicalAttr("CaptionExpression", "gxTextBlock"));
        }

        [Fact]
        public void OnEnterEvent_RoutesToEventGX()
        {
            Assert.Equal("eventGX", WebFormTypedPropertyWriter.ResolveCanonicalAttr("OnEnterEvent", "gxAttribute"));
        }

        [Fact]
        public void UnknownDescriptor_ReturnedUnchanged()
        {
            Assert.Equal("Class", WebFormTypedPropertyWriter.ResolveCanonicalAttr("Class", "gxButton"));
            Assert.Equal("Visible", WebFormTypedPropertyWriter.ResolveCanonicalAttr("Visible", "gxButton"));
        }

        [Fact]
        public void DrainAutoRoutes_ReturnsEmptyByDefault()
        {
            // Clear any prior thread-local state first.
            WebFormTypedPropertyWriter.DrainAutoRoutes();
            var routes = WebFormTypedPropertyWriter.DrainAutoRoutes();
            Assert.NotNull(routes);
            Assert.Empty(routes);
        }

        [Fact]
        public void LegacyBodyFixture_ProjectionPreservesUntouchedCaptionExpressions()
        {
            string before = ReadLegacyFixture();
            var beforeDoc = XDocument.Parse(before);
            var afterDoc = new XDocument(beforeDoc);
            var anchor = afterDoc.Descendants().Single(e => (string)e.Attribute("id") == "btnA");
            anchor.AddAfterSelf(new XElement("gxButton",
                new XAttribute("id", "btnB"),
                new XAttribute("Event", "'EventB'"),
                new XAttribute("CaptionExpression", BuildTokens("Label B"))));

            var changedControls = WebFormTypedPropertyWriter.GetChangedControlNames(before, afterDoc.ToString());
            var projection = WebFormTypedPropertyWriter.ProjectDescriptorFixupOntoDetachedXml(
                before, afterDoc.ToString());

            Assert.Contains("btnB", changedControls);
            Assert.DoesNotContain("txtExisting", changedControls);
            Assert.True(projection.IsLegacyHtml);
            Assert.Contains(projection.AttributeChanges, d => d.ControlId == "btnB" && d.Attribute == "CaptionExpression" && d.Kind == "added");
            Assert.DoesNotContain(projection.Notices, n => n.Source == "CaptionExpression" && n.Action == "removed-source");
            var projected = XDocument.Parse(projection.Xml);
            var beforeButtons = beforeDoc.Descendants("gxButton").ToDictionary(
                e => (string)e.Attribute("id"), e => (string)e.Attribute("CaptionExpression"));
            var projectedButtons = projected.Descendants("gxButton").ToDictionary(
                e => (string)e.Attribute("id"), e => (string)e.Attribute("CaptionExpression"));
            Assert.Equal(beforeButtons["btnA"], projectedButtons["btnA"]);
            Assert.Equal(BuildTokens("Label B"), projectedButtons["btnB"]);
            var beforeText = beforeDoc.Descendants("gxTextBlock").Single();
            var projectedText = projected.Descendants("gxTextBlock").Single();
            Assert.Equal((string)beforeText.Attribute("CaptionExpression"),
                (string)projectedText.Attribute("CaptionExpression"));
        }

        [Fact]
        public void DescriptorProjection_RemovesSourceOnlyWhenCanonicalIsObserved()
        {
            const string before = "<GxMultiForm><Form type=\"layout\"><body><gxButton id=\"b\" /></body></Form></GxMultiForm>";
            const string afterWithoutCanonical = "<GxMultiForm><Form type=\"layout\"><body><gxButton id=\"b\" OnClickEvent=\"'E'\" /></body></Form></GxMultiForm>";
            const string afterWithCanonical = "<GxMultiForm><Form type=\"layout\"><body><gxButton id=\"b\" OnClickEvent=\"'E'\" Event=\"'E'\" /></body></Form></GxMultiForm>";

            var pending = WebFormTypedPropertyWriter.ProjectDescriptorFixupOntoDetachedXml(
                before, afterWithoutCanonical);
            Assert.Contains(pending.Notices, n => n.Source == "OnClickEvent" && n.Action == "would-route");
            Assert.NotNull(XDocument.Parse(pending.Xml).Descendants("gxButton").Single().Attribute("OnClickEvent"));

            var completed = WebFormTypedPropertyWriter.ProjectDescriptorFixupOntoDetachedXml(
                before, afterWithCanonical);
            Assert.Contains(completed.Notices, n => n.Source == "OnClickEvent" && n.Action == "removed-source");
            Assert.Null(XDocument.Parse(completed.Xml).Descendants("gxButton").Single().Attribute("OnClickEvent"));
        }

        private static string ReadLegacyFixture()
        {
            string path = Path.Combine(
                TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker.Tests", "Fixtures",
                "LegacyWebFormCaptionExpression.xml");
            return File.ReadAllText(path);
        }

        private static string BuildTokens(string value)
        {
            return "<Tokens><Token><Type>Constant</Type><Data><![CDATA[" + value + "]]></Data></Token></Tokens>";
        }
    }
}
