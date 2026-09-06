using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class ContextBundleServiceTests
    {
        [Fact]
        public void SmallBundleCarriesStableHashAndCompleteness()
        {
            var envelope = new JObject
            {
                ["status"] = "ok",
                ["code"] = "360ContextRead",
                ["result"] = new JObject
                {
                    ["object"] = new JObject
                    {
                        ["name"] = "POrders",
                        ["type"] = "Procedure",
                        ["signature"] = "parm(in:&OrderId);"
                    },
                    ["calledSignatures"] = new JArray()
                }
            };

            var result = JObject.Parse(new ContextBundleService().Apply(envelope.ToString(), "POrders", 12000));
            var context = (JObject)result["result"]["context"];

            Assert.Equal(ContextBundleService.SchemaVersion, context["schemaVersion"]?.ToString());
            Assert.StartsWith("sha256:", context["contentHash"]?.ToString());
            Assert.Equal(context["contentHash"]?.ToString(), context["revision"]?.ToString());
            Assert.True(context["complete"]?.Value<bool>());
            Assert.Null(context["nextCursor"]);
            Assert.Equal("POrders", result["result"]["object"]["name"]?.ToString());
        }

        [Fact]
        public void LargePartsBecomeAddressableAndCollectionsPageAtItemBoundaries()
        {
            string source = new string('x', 20000);
            var called = new JArray(Enumerable.Range(0, 40)
                .Select(i => new JObject { ["name"] = "P" + i, ["type"] = "Procedure" }));
            var envelope = new JObject
            {
                ["status"] = "ok",
                ["result"] = new JObject
                {
                    ["object"] = new JObject
                    {
                        ["name"] = "POrders",
                        ["type"] = "Procedure",
                        ["parts"] = new JObject { ["Source"] = source, ["Rules"] = "parm(in:&OrderId);" }
                    },
                    ["calledSignatures"] = called,
                    ["referencedTables"] = new JArray(),
                    ["referencedSDTs"] = new JArray(),
                    ["callers"] = new JArray()
                }
            };

            var result = JObject.Parse(new ContextBundleService().Apply(envelope.ToString(), "POrders", 5000));
            var context = (JObject)result["result"]["context"];

            Assert.NotNull(context["resources"]);
            Assert.Contains(((JArray)context["resources"]).Values<JObject>(),
                item => item["uri"]?.ToString().Contains("/part/Source") == true);
            Assert.NotNull(context["omittedSections"]);
            Assert.True(context["complete"]?.Value<bool>() == false);
            Assert.NotNull(result["result"]["object"]["parts"]["Rules"]);
            Assert.Null(result["result"]["object"]["parts"]["Source"]);
            Assert.True(context["returnedBytes"]?.Value<int>() > 0);
            JObject.Parse(result.ToString());
        }

        [Fact]
        public void CursorReturnsOnlyTheRequestedCollectionPage()
        {
            var entries = new JArray(Enumerable.Range(0, 25)
                .Select(i => new JObject { ["name"] = "P" + i }));
            var envelope = new JObject
            {
                ["status"] = "ok",
                ["result"] = new JObject
                {
                    ["object"] = new JObject { ["name"] = "POrders", ["type"] = "Procedure" },
                    ["calledSignatures"] = entries
                }
            };

            var result = JObject.Parse(new ContextBundleService().Apply(envelope.ToString(), "POrders", 12000, "calledSignatures:20"));
            var page = (JArray)result["result"]["calledSignatures"];
            var context = (JObject)result["result"]["context"];

            Assert.Equal(5, page.Count);
            Assert.Equal("P20", page[0]["name"]?.ToString());
            Assert.Null(context["nextCursor"]);
            Assert.False(context["complete"]?.Value<bool>() ?? true);
        }
    }
}
