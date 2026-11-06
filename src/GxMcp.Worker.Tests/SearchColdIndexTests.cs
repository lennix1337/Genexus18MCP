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
