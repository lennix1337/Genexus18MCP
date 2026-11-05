using System.Linq;
using System.Xml.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// #867: a layout write addressed to one of two same-named report controls
    /// (two "&amp;vendat" in one print block) must reach only that SDK item.
    /// </summary>
    public class Issue867ReportHomonymWriteTests
    {
        public sealed class FakeItem
        {
            public string Name { get; set; }
            public string ControlName { get; set; }
            public int Left { get; set; }
        }

        private const string BlockXml =
            "<Report><PrintBlock Name=\"PB4\">" +
            "<Control ControlName=\"&amp;vendat\" Left=\"481\"/>" +
            "<Control ControlName=\"lblOther\" Left=\"10\"/>" +
            "<Control ControlName=\"&amp;VENDAT\" Left=\"64\"/>" +
            "</PrintBlock></Report>";

        [Fact]
        public void HomonymOrdinalCountsOnlySameNamedSiblings()
        {
            var controls = XDocument.Parse(BlockXml).Descendants("Control").ToList();
            Assert.Equal(0, ReportLayoutHelper.HomonymOrdinal(controls[0], "&vendat"));
            Assert.Equal(0, ReportLayoutHelper.HomonymOrdinal(controls[1], "lblOther"));
            Assert.Equal(1, ReportLayoutHelper.HomonymOrdinal(controls[2], "&VENDAT"));
        }

        [Fact]
        public void SelectReportItemReturnsTheHomonymAtThatOrdinal()
        {
            var first = new FakeItem { Name = "&vendat", Left = 481 };
            var other = new FakeItem { Name = "lblOther", Left = 10 };
            var second = new FakeItem { ControlName = "&vendat", Left = 481 };
            var items = new object[] { first, other, second };

            Assert.Same(first, ReportLayoutHelper.SelectReportItem(items, "&vendat", 0));
            Assert.Same(second, ReportLayoutHelper.SelectReportItem(items, "&VENDAT", 1));
            Assert.Same(other, ReportLayoutHelper.SelectReportItem(items, "lblOther", 0));
            Assert.Null(ReportLayoutHelper.SelectReportItem(items, "&vendat", 2));
            Assert.Null(ReportLayoutHelper.SelectReportItem(items, "missing", 0));
            Assert.Null(ReportLayoutHelper.SelectReportItem(null, "&vendat", 0));
        }

        [Fact]
        public void WriteLayoutAppliesEachIncomingControlToOneSdkItemOnly()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Helpers", "ReportLayoutHelper.cs");

            Assert.Contains("SelectReportItem(items, elName, HomonymOrdinal(elXml, elName))", source);
            // The only remaining name match is the new-control existence check.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"string\.Equals\(currentControlName, elName").Cast<object>());
        }
    }
}
