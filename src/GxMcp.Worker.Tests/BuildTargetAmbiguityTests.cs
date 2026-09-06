using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BuildTargetAmbiguityTests
    {
        [Fact]
        public void ExpandTargetsFailsClosedWhenBareNameHasMultipleTypes()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Shared", Type = "Procedure", Calls = new List<string>() },
                new SearchIndex.IndexEntry { Name = "Shared", Type = "WebPanel", Calls = new List<string>() }
            });
            index.MarkIndexComplete(2);
            var build = new BuildService();
            build.SetIndexCacheService(index);
            build.SetCallerGraphService(new CallerGraphService(index));

            var plan = build.ExpandTargets(new[] { "Shared" }, "none", 20);
            Assert.Contains("Shared", plan.AmbiguousTargets);
            Assert.Equal(new[] { "Shared" }, plan.Expanded);
        }
    }
}
