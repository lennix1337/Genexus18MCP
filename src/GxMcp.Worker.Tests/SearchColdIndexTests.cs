using System.Reflection;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SearchColdIndexTests
    {
        [Fact]
        public void AnalyzeImpact_ColdIndex_ExplainsHowToWait()
        {
            var index = new IndexCacheService();
            var response = JObject.Parse(new AnalyzeService(index, objSvc: null, graph: null).ImpactAnalysis("Missing", waitForIndex: false));

            Assert.Equal("cold", response["indexState"]?.ToString());
            Assert.Contains("genexus_lifecycle action=status wait=", response["retry_hint"]?.ToString());
        }

        [Fact]
        public void AnalyzeImpact_ColdIndexWaitTimeout_ExplainsHowToWait()
        {
            var index = new IndexCacheService();
            var response = JObject.Parse(new AnalyzeService(index, objSvc: null, graph: null)
                .ImpactAnalysis("Missing", waitForIndex: true, waitTimeoutMs: 0));

            Assert.Equal("Timeout", response["status"]?.ToString());
            Assert.Equal("cold", response["indexState"]?.ToString());
            Assert.Contains("genexus_lifecycle action=status wait=", response["retry_hint"]?.ToString());
        }

        [Theory]
        [InlineData("usedby:Missing")]
        [InlineData("type:Procedure Missing")]
        public void Search_IndexDependentQueries_OnColdIndex_MarkIncomplete(string query)
        {
            var index = new IndexCacheService();
            typeof(IndexCacheService).GetField("_index", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(index, new SearchIndex());
            var response = JObject.Parse(new SearchService(index).Search(query));

            Assert.Equal("cold", response["_meta"]?["indexState"]?.ToString());
            Assert.Contains("genexus_lifecycle action=status wait=", response["_meta"]?["retry_hint"]?.ToString());
        }

        [Fact]
        public void Search_ColdIndex_ExplainsEmptySnapshotAndHowToWait()
        {
            var index = new IndexCacheService();
            typeof(IndexCacheService).GetField("_index", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(index, new SearchIndex());
            var service = new SearchService(index);
            var response = JObject.Parse(service.Search("missing object"));

            Assert.Equal("cold", response["_meta"]?["indexState"]?.ToString());
            Assert.Contains("genexus_lifecycle action=status wait=", response["_meta"]?["retry_hint"]?.ToString());
        }
    }
}
