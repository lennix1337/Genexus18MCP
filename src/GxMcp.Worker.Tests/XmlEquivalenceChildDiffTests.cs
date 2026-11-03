using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #830: when the SDK dropped a child node, the verification said "Child count differs (1 vs 2)" and its
    /// verifyDiff block came back empty, so the caller learned that something under the grid was lost, not what. The
    /// diff now names the child nodes each side has and the other does not, as it already did for attributes.
    /// </summary>
    public sealed class XmlEquivalenceChildDiffTests
    {
        private const string Persisted =
            "<GxMultiForm><Form type=\"layout\"><detail><layout><grid controlName=\"GridProva\"><table controlName=\"GridTable\"/></grid></layout></detail></Form></GxMultiForm>";

        private const string RequestedWithCondition =
            "<GxMultiForm><Form type=\"layout\"><detail><layout><grid controlName=\"GridProva\"><condition>NomeCliente like 'A%'</condition><table controlName=\"GridTable\"/></grid></layout></detail></Form></GxMultiForm>";

        private static XmlEquivalenceDiff Diff(string persisted, string requested, out string summary)
        {
            Assert.False(XmlEquivalence.AreEquivalent(persisted, requested, XmlEquivalenceOptions.Layout, out summary, out var structured));
            Assert.NotNull(structured);
            return structured;
        }

        [Fact]
        public void ADroppedChildIsNamedInTheStructuredDiffAndInTheSummary()
        {
            var diff = Diff(Persisted, RequestedWithCondition, out string summary);

            Assert.Equal("grid", diff.ElementName);
            Assert.EndsWith("/grid", diff.Path);
            Assert.Equal(new[] { "condition" }, diff.RejectedChildren);
            Assert.Empty(diff.AddedChildren);
            Assert.Equal(new[] { "table" }, diff.LeftChildren);
            Assert.Equal(new[] { "condition", "table" }, diff.RightChildren);
            Assert.Contains("Child count differs at", summary);
            Assert.Contains("(1 vs 2)", summary);
            Assert.Contains("onlyInRight=[condition]", summary);
            Assert.DoesNotContain("onlyInLeft", summary);
        }

        [Fact]
        public void AChildTheSdkInjectedIsReportedAsAdded()
        {
            var diff = Diff(RequestedWithCondition, Persisted, out string summary);

            Assert.Equal(new[] { "condition" }, diff.AddedChildren);
            Assert.Empty(diff.RejectedChildren);
            Assert.Contains("onlyInLeft=[condition]", summary);
            Assert.DoesNotContain("onlyInRight", summary);
        }

        // The callers of XmlEquivalence do not all pass (persisted, requested): the transfer and the layout-edit routes pass
        // (expected, actual), the typed Conditions and parameters routes pass (read back, before). A summary that said which
        // side the SDK dropped it from read backwards for them, so it says only which side has the child.
        [Fact]
        public void TheSummaryNeverClaimsWhoDroppedWhat_WhicheverOrderTheCallerUses()
        {
            Diff(Persisted, RequestedWithCondition, out string forward);
            Diff(RequestedWithCondition, Persisted, out string backward);

            Assert.DoesNotContain("ByPersist", forward);
            Assert.DoesNotContain("ByPersist", backward);
            Assert.Contains("onlyInRight=[condition]", forward);
            Assert.Contains("onlyInLeft=[condition]", backward);
        }

        [Fact]
        public void RepeatedChildrenAreCountedNotCollapsed()
        {
            var persisted = "<table><row/></table>";
            var requested = "<table><row/><row/><row/></table>";

            var diff = Diff(persisted, requested, out string summary);

            Assert.Equal(new[] { "row", "row" }, diff.RejectedChildren);
            Assert.Contains("onlyInRight=[row,row]", summary);
        }

        [Fact]
        public void ADroppedTextNodeIsLabelled()
        {
            var diff = Diff("<caption/>", "<caption>Hello</caption>", out string summary);

            Assert.Equal(new[] { "#text" }, diff.RejectedChildren);
            Assert.Contains("onlyInRight=[#text]", summary);
        }

        [Fact]
        public void TheSummaryNamesAtMostTenChildrenAndCountsTheRest()
        {
            var requested = "<t>" + string.Concat(Enumerable.Repeat("<c/>", 25)) + "</t>";

            var diff = Diff("<t/>", requested, out string summary);

            Assert.Equal(25, diff.RejectedChildren.Length);
            Assert.Contains("onlyInRight=[c,c,c,c,c,c,c,c,c,c,+15 more]", summary);
        }

        [Fact]
        public void ChildrenInADifferentOrderWithTheSameCountAreNotACountDifference()
        {
            // Same count: the existing positional comparison decides (a different element name), not the child-count branch.
            Assert.False(XmlEquivalence.AreEquivalent("<r><a/><b/></r>", "<r><b/><a/></r>", out string summary, out var structured));
            Assert.DoesNotContain("Child count differs", summary);
            Assert.Null(structured?.RejectedChildren);
        }

        [Fact]
        public void ADifferenceDeeperDownKeepsTheNestedElementsDiff()
        {
            var persisted = "<r><grid><table/></grid></r>";
            var requested = "<r><grid><table><row/></table></grid></r>";

            var diff = Diff(persisted, requested, out _);

            Assert.Equal("table", diff.ElementName);
            Assert.Equal(new[] { "row" }, diff.RejectedChildren);
        }

        // ---------------------------------------------------------------------------- verifyDiff

        [Fact]
        public void VerifyDiff_NamesTheDroppedChildNode()
        {
            var diff = Diff(Persisted, RequestedWithCondition, out _);

            JObject verifyDiff = WriteService.BuildVerifyDiff(diff);

            Assert.Equal("grid", (string)verifyDiff["element"]);
            Assert.Equal(new[] { "condition" }, ((JArray)verifyDiff["rejectedChildren"]).Select(t => (string)t));
            Assert.Equal(new[] { "table" }, ((JArray)verifyDiff["persistedChildren"]).Select(t => (string)t));
            Assert.Equal(new[] { "condition", "table" }, ((JArray)verifyDiff["requestedChildren"]).Select(t => (string)t));
            Assert.Null(verifyDiff["addedChildren"]);
            Assert.Null(verifyDiff["rejectedAttributes"]);
        }

        [Fact]
        public void VerifyDiff_AListWithinTheCapCarriesNoTotal()
        {
            JObject verifyDiff = WriteService.BuildVerifyDiff(Diff(Persisted, RequestedWithCondition, out _));

            Assert.Null(verifyDiff["rejectedChildrenTotal"]);
            Assert.Null(verifyDiff["persistedChildrenTotal"]);
            Assert.Null(verifyDiff["requestedChildrenTotal"]);
        }

        [Fact]
        public void VerifyDiff_CapsEachChildListAndStatesItsTotal()
        {
            // A table can hold hundreds of rows: the lists of a verification failure must not grow with it.
            var requested = "<t>" + string.Concat(Enumerable.Repeat("<c/>", 45)) + "</t>";
            var persisted = "<t>" + string.Concat(Enumerable.Repeat("<p/>", 30)) + "</t>";

            JObject verifyDiff = WriteService.BuildVerifyDiff(Diff(persisted, requested, out _));

            Assert.Equal(WriteService.MaxVerifyDiffChildren, ((JArray)verifyDiff["rejectedChildren"]).Count);
            Assert.Equal(45, (int)verifyDiff["rejectedChildrenTotal"]);
            Assert.Equal(WriteService.MaxVerifyDiffChildren, ((JArray)verifyDiff["requestedChildren"]).Count);
            Assert.Equal(45, (int)verifyDiff["requestedChildrenTotal"]);
            Assert.Equal(WriteService.MaxVerifyDiffChildren, ((JArray)verifyDiff["persistedChildren"]).Count);
            Assert.Equal(30, (int)verifyDiff["persistedChildrenTotal"]);
            Assert.Equal(WriteService.MaxVerifyDiffChildren, ((JArray)verifyDiff["addedChildren"]).Count);
            Assert.Equal(30, (int)verifyDiff["addedChildrenTotal"]);
        }

        [Fact]
        public void VerifyDiff_StillNamesADroppedAttribute()
        {
            Assert.False(XmlEquivalence.AreEquivalent("<grid controlName=\"G\"/>", "<grid controlName=\"G\" condition=\"x\"/>", out _, out var structured));

            JObject verifyDiff = WriteService.BuildVerifyDiff(structured);

            Assert.Equal(new[] { "condition" }, ((JArray)verifyDiff["rejectedAttributes"]).Select(t => (string)t));
            Assert.Null(verifyDiff["rejectedChildren"]);
        }

        [Fact]
        public void VerifyDiff_OfAnUnstructuredDifferenceStaysEmpty()
        {
            Assert.Empty(WriteService.BuildVerifyDiff(new XmlEquivalenceDiff { Summary = "parse error" }));
            Assert.Empty(WriteService.BuildVerifyDiff(null));
        }

        [Fact]
        public void Wiring_TheWriteErrorBuildsItsVerifyDiffThroughTheSharedBuilder()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "WriteService.cs"));

            Assert.Contains("structuredDiff != null ? BuildVerifyDiff(structuredDiff) : null", source);
        }
    }
}
