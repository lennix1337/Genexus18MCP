using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The lifecycle result and warning payloads are the same envelope around a
    /// different collection - one keyed <c>items</c>, one keyed <c>warnings</c> - and
    /// each computed the page window itself: the same clamps, the same
    /// <c>skip</c>/<c>hasMore</c> arithmetic, the same bounds-checked loop, and the
    /// same <c>_meta.pagination</c> block.
    ///
    /// The arithmetic decides which page a client sees and whether
    /// <c>has_more</c> tells it to ask again, so two copies is two chances for the
    /// two payloads to disagree about the page they are showing. That is a contract
    /// a client can observe, and these tests hold it by driving both builders
    /// through the same inputs and comparing the windows they report.
    ///
    /// The batch-edit completion envelope gets the same treatment for the same
    /// reason: three call sites, one shape, and the empty-change-list case already
    /// reported a literal <c>0</c> for the duration where the other two read the
    /// stopwatch - a difference that looks deliberate and is not, because no
    /// stopwatch has run at that point.
    /// </summary>
    public class BatchServicePaginationTests
    {
        private static List<string> Items(int count) =>
            Enumerable.Range(1, count).Select(i => "item-" + i).ToList();

        private static JObject Pagination(JObject payload) => (JObject)payload["_meta"]!["pagination"]!;

        [Theory]
        [InlineData(1, 10)]
        [InlineData(2, 10)]
        [InlineData(3, 5)]
        [InlineData(1, 1)]
        [InlineData(5, 10)]   // past the end: empty page, has_more false
        [InlineData(0, 10)]   // page 0 clamps to 1
        [InlineData(-3, 10)]  // negative page clamps to 1
        [InlineData(1, 0)]    // page size 0 clamps to 1
        [InlineData(1, -7)]   // negative page size clamps to 1
        [InlineData(1, 5000)] // oversized page size clamps to 200
        public void BothPayloadsReportTheSamePageWindow(int page, int pageSize)
        {
            List<string> source = Items(25);

            var items = BatchService.BuildResultPayload(source, page, pageSize);
            var warnings = BatchService.BuildStatusPayload(source, page, pageSize);

            // Identical window metadata, whatever the input.
            Assert.Equal(
                Pagination(items).ToString(Newtonsoft.Json.Formatting.None),
                Pagination(warnings).ToString(Newtonsoft.Json.Formatting.None));

            // And identical slices: the two builders differ only in the key.
            Assert.Equal(
                ((JArray)items["items"]!).ToString(Newtonsoft.Json.Formatting.None),
                ((JArray)warnings["warnings"]!).ToString(Newtonsoft.Json.Formatting.None));
        }

        [Theory]
        [InlineData(1, 10, 25, true)]   // 10 of 25, more remain
        [InlineData(2, 10, 25, true)]   // 20 of 25, one more page
        [InlineData(3, 10, 25, false)]  // last page, nothing beyond
        [InlineData(4, 10, 25, false)]  // past the end: empty, no more
        [InlineData(1, 200, 25, false)] // everything on one page
        [InlineData(1, 10, 0, false)]   // nothing to page
        [InlineData(1, 10, 10, false)]  // exactly one full page
        public void HasMoreIsTrueExactlyWhenAPageFollows(int page, int pageSize, int total, bool expectedHasMore)
        {
            var payload = BatchService.BuildResultPayload(Items(total), page, pageSize);
            var pagination = Pagination(payload);

            Assert.Equal(total, pagination["total"]!.ToObject<int>());
            Assert.Equal(expectedHasMore, pagination["has_more"]!.Value<bool>());

            // has_more is not decoration: it must agree with whether more items
            // actually exist beyond the returned page.
            int returned = ((JArray)payload["items"]!).Count;
            int skip = (pagination["page"]!.ToObject<int>() - 1) * pagination["page_size"]!.ToObject<int>();
            bool moreActuallyExist = skip + returned < total;
            Assert.Equal(moreActuallyExist, pagination["has_more"]!.Value<bool>());
        }

        [Fact]
        public void AnEmptyOrNullListPagesCleanly()
        {
            foreach (IList<string> source in new IList<string>[] { null, new List<string>() })
            {
                var items = BatchService.BuildResultPayload(source!, 1, 10);
                var warnings = BatchService.BuildStatusPayload(source!, 1, 10);

                Assert.Empty((JArray)items["items"]!);
                Assert.Empty((JArray)warnings["warnings"]!);
                Assert.Equal(0, Pagination(items)["total"]!.ToObject<int>());
                Assert.False(Pagination(items)["has_more"]!.Value<bool>());
            }
        }

        [Fact]
        public void TheTwoPayloadsDifferOnlyInTheCollectionKey()
        {
            // Stated so the builders cannot be collapsed into one that answers to a
            // single key: the keys are part of what each caller reads.
            List<string> source = Items(3);

            var items = BatchService.BuildResultPayload(source, 1, 10);
            var warnings = BatchService.BuildStatusPayload(source, 1, 10);

            Assert.NotNull(items["items"]);
            Assert.Null(items["warnings"]);
            Assert.NotNull(warnings["warnings"]);
            Assert.Null(warnings["items"]);
        }

        [Fact]
        public void ThePageWindowIsComputedOnceForBothPayloads()
        {
            // Source-level, because the behavioural tests above would still pass if a
            // second copy of the arithmetic reappeared and happened to agree today.
            string source = RepoSource.WithoutComments(
                GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Worker", "Services", "BatchService.cs"));

            Assert.Equal(1, SourceAssert.Count(source, "private static void ClampPage(ref int page, ref int pageSize)"));
            Assert.Equal(1, SourceAssert.Count(source, "private static JArray PageOf(IList<string> source, int page, int pageSize, out int total, out bool hasMore)"));
            Assert.Equal(1, SourceAssert.Count(source, "private static JObject PaginationMeta(int total, int page, int pageSize, bool hasMore)"));

            // The bounds are pinned, not just the method that applies them. Raising
            // the page-size cap to 500 passed every behavioural test above, because
            // none of the cases requests a page large enough to tell 200 from 500 -
            // so the cap is a source-level contract here, and the cases above cover
            // the clamping *behaviour* while this covers the *number*.
            string clamp = SourceAssert.MethodBody(source, "private static void ClampPage(ref int page, ref int pageSize)");
            Assert.Contains("page = Math.Max(page, 1);", clamp);
            Assert.Contains("pageSize = Math.Min(Math.Max(pageSize, 1), 200);", clamp);

            // And the window arithmetic, which decides which page a client sees.
            string window = SourceAssert.MethodBody(source,
                "private static JArray PageOf(IList<string> source, int page, int pageSize, out int total, out bool hasMore)");
            Assert.Contains("int skip = (page - 1) * pageSize;", window);
            Assert.Contains("hasMore = skip + pageSize < total;", window);
            Assert.Contains("int end = Math.Min(skip + pageSize, total);", window);

            Assert.Equal(2, SourceAssert.Count(source, "ClampPage(ref page, ref pageSize);"));
            Assert.Equal(1, SourceAssert.Count(source, @"[""has_more""] = hasMore"));

            // Both payloads go through the window helper and the meta builder.
            Assert.Equal(1, SourceAssert.Count(source, "PageOf(warnings, page, pageSize, out int total, out bool hasMore)"));
            Assert.Equal(1, SourceAssert.Count(source, "PageOf(items, page, pageSize, out int total, out bool hasMore)"));
            Assert.Equal(2, SourceAssert.Count(source, @"[""pagination""] = PaginationMeta(total, page, pageSize, hasMore)"));
        }

        [Fact]
        public void TheBatchEditCompletionEnvelopeIsBuiltInOnePlace()
        {
            string source = RepoSource.WithoutComments(
                GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Worker", "Services", "BatchService.cs"));

            // Three producers - empty list, transactional path, per-change path -
            // and one envelope, so a client reading any of them sees the same shape.
            Assert.Equal(1, SourceAssert.Count(source, "private static string BatchEditCompleted(string target, int count, JArray results, System.Diagnostics.Stopwatch sw, bool dryRun = false)"));
            Assert.Equal(3, SourceAssert.Count(source, "return BatchEditCompleted("));
            Assert.Equal(1, SourceAssert.Count(source, @"[""count""] = count,"));

            // The envelope reads the stopwatch, and it does so in exactly one place:
            // no producer inlines its own duration. The empty-list case used to,
            // with a literal 0 standing in for a stopwatch that had not started.
            // The expression appears three times because three other operations in
            // this file report their own elapsed time the same way.
            Assert.Equal(3, SourceAssert.Count(source, @"[""duration""] = sw.ElapsedMilliseconds"));
            Assert.Equal(0, SourceAssert.Count(source, @"[""duration""] = 0"));
        }

    }
}
