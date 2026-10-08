using System;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdtStructurePlanTests
    {
        private static JArray Existing() => new JArray(Enumerable.Range(1, 240)
            .Select(i => new JObject { ["name"] = "Field" + i, ["type"] = "VARCHAR", ["length"] = 50 }));

        [Fact]
        public void ReplacementPreviewReportsEveryOmittedMember()
        {
            var before = Existing();
            var request = new JArray(new JObject { ["name"] = "NewStatus", ["type"] = "VARCHAR" });
            var projected = SdtStructurePlan.Project(before, request, false);
            var diff = SdtStructurePlan.Diff(before, projected);
            Assert.Equal(241, diff.Count);
            Assert.Equal(240, diff.Count(x => x["change"]?.ToString() == "removed"));
            Assert.Single(diff, x => x["change"]?.ToString() == "added");
            Assert.Single(projected);
        }

        [Fact]
        public void AddPreviewPreservesEveryExistingMember()
        {
            var before = Existing();
            var request = new JArray(new JObject { ["name"] = "NewStatus", ["type"] = "VARCHAR" });
            var projected = SdtStructurePlan.Project(before, request, true);
            var diff = SdtStructurePlan.Diff(before, projected);
            Assert.Equal(241, projected.Count);
            Assert.Single(diff);
            Assert.Equal("added", diff[0]["change"]?.ToString());
            Assert.True(SDTService.MatchesPlan(before, request, projected, true));
            projected.RemoveAt(100);
            Assert.False(SDTService.MatchesPlan(before, request, projected, true));
        }

        [Fact]
        public void AddRejectsDuplicateMember()
        {
            var before = Existing();
            Assert.Throws<ArgumentException>(() => SdtStructurePlan.Project(before,
                new JArray(new JObject { ["name"] = "field2" }), true));
        }

        [Fact]
        public void AddRequiresTypeAndRejectsWrongPersistedType()
        {
            var before = Existing();
            Assert.Throws<ArgumentException>(() => SdtStructurePlan.Project(before,
                new JArray(new JObject { ["name"] = "NewStatus" }), true));
            Assert.Throws<ArgumentException>(() => SdtStructurePlan.Project(before,
                new JArray(new JObject { ["name"] = "NewStatus" }), false));
            var request = new JArray(new JObject { ["name"] = "NewStatus", ["type"] = "Numeric" });
            var persisted = SdtStructurePlan.Project(before, request, true);
            Assert.True(SDTService.MatchesPlan(before, request, persisted, true));
            persisted[240]["type"] = "VARCHAR";
            Assert.False(SDTService.MatchesPlan(before, request, persisted, true));
        }

        [Fact]
        public void NestedReplacementReportsRemovedMember()
        {
            var before = new JArray(new JObject { ["name"] = "Group", ["isLevel"] = true,
                ["children"] = new JArray(new JObject { ["name"] = "Keep", ["type"] = "VARCHAR" },
                    new JObject { ["name"] = "Remove", ["type"] = "VARCHAR" }) });
            var after = new JArray(new JObject { ["name"] = "Group", ["isLevel"] = true,
                ["children"] = new JArray(new JObject { ["name"] = "Keep", ["type"] = "VARCHAR" }) });
            Assert.Contains(SdtStructurePlan.Diff(before, after), change =>
                change["path"]?.ToString() == "children/Group/children/Remove"
                && change["change"]?.ToString() == "removed");
        }
    }
}
