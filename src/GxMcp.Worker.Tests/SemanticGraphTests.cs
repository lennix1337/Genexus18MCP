using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class SemanticGraphTests
    {
        [Fact]
        public void GraphRevisionChangesWhenTheIndexIsUpdated()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "A", Type = "Procedure", Calls = new List<string> { "B" } },
                new SearchIndex.IndexEntry { Name = "B", Type = "Procedure", Calls = new List<string>() }
            });
            long before = index.GetIndex().GraphRevision;

            index.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "C", Type = "Procedure", Calls = new List<string>() }
            });

            Assert.True(index.GetIndex().GraphRevision > before);
        }

        [Fact]
        public void HomonymousBareNameDoesNotMergeTypedGraphEdges()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Shared", Type = "Procedure", Calls = new List<string>() },
                new SearchIndex.IndexEntry { Name = "Shared", Type = "WebPanel", Calls = new List<string>() },
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure", Calls = new List<string> { "Shared" } }
            });

            var graph = new CallerGraphService(index);

            Assert.Empty(graph.GetCallers("Shared"));
            Assert.Empty(graph.GetCallees("Shared"));
        }

        [Fact]
        public void CyclicAdjacencyIsDeduplicatedAndTransitiveWalkTerminates()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "A", Type = "Procedure", Calls = new List<string> { "B" } },
                new SearchIndex.IndexEntry { Name = "B", Type = "Procedure", Calls = new List<string> { "C" } },
                new SearchIndex.IndexEntry { Name = "C", Type = "Procedure", Calls = new List<string> { "A" } }
            });

            var result = new CallerGraphService(index).GetCalleesTransitive("A", 20);

            Assert.False(result.Truncated);
            Assert.Equal(2, result.Nodes.Count);
            Assert.Equal(2, result.Nodes.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains("B", result.Nodes);
            Assert.Contains("C", result.Nodes);
        }
    }
}
