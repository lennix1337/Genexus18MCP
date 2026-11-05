using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class EngineEditContractTests
    {
        [Fact]
        public void TryPlanAdmitsValidSinglePartEdit()
        {
            var args = new JObject
            {
                ["name"] = "ProcTest",
                ["part"] = "Source",
                ["content"] = "// hello world"
            };
            string? error = EngineEditContract.TryPlan(args, out var plan);
            Assert.Null(error);
            Assert.NotNull(plan);
            Assert.Equal("ProcTest", plan.Name);
            Assert.Equal("Source", plan.Part);
            Assert.Equal("// hello world", plan.Content);
        }

        [Fact]
        public void TryPlanDefaultsPartToSource()
        {
            var args = new JObject
            {
                ["name"] = "ProcTest",
                ["content"] = "// hello world"
            };
            string? error = EngineEditContract.TryPlan(args, out var plan);
            Assert.Null(error);
            Assert.NotNull(plan);
            Assert.Equal("Source", plan.Part);
        }

        [Theory]
        [InlineData("Events")]
        [InlineData("Rules")]
        [InlineData("Conditions")]
        public void TryPlanAdmitsSupportedParts(string part)
        {
            var args = new JObject
            {
                ["name"] = "ProcTest",
                ["part"] = part,
                ["content"] = "// content"
            };
            string? error = EngineEditContract.TryPlan(args, out var plan);
            Assert.Null(error);
            Assert.NotNull(plan);
            Assert.Equal(part, plan.Part);
        }

        [Fact]
        public void TryPlanRejectsTargetsBatch()
        {
            var args = new JObject
            {
                ["targets"] = new JArray(new JObject { ["name"] = "A" }),
                ["content"] = "//"
            };
            string? error = EngineEditContract.TryPlan(args, out var plan);
            Assert.NotNull(error);
            Assert.Null(plan);
            Assert.Contains("targets[]", error);
        }

        [Fact]
        public void TryPlanRejectsDryRun()
        {
            var args = new JObject
            {
                ["name"] = "ProcTest",
                ["content"] = "//",
                ["dryRun"] = true
            };
            string? error = EngineEditContract.TryPlan(args, out var plan);
            Assert.NotNull(error);
            Assert.Null(plan);
            Assert.Contains("dryRun", error);
        }

        [Fact]
        public void TryPlanRejectsPatchMode()
        {
            var args = new JObject
            {
                ["name"] = "ProcTest",
                ["content"] = "//",
                ["mode"] = "patch"
            };
            string? error = EngineEditContract.TryPlan(args, out var plan);
            Assert.NotNull(error);
            Assert.Null(plan);
            Assert.Contains("mode", error);
        }

        [Fact]
        public void TryShapeProducesValidPayload()
        {
            var plan = new EngineEditPlan("ProcTest", "Source", "// code", "Procedure");
            var engineJson = new JObject
            {
                ["type"] = "Procedure",
                ["bytes"] = 7,
                ["versionId"] = 4
            };
            var engineInfo = new JObject { ["backend"] = "gxengine" };

            bool ok = EngineEditContract.TryShape(plan, engineJson, engineInfo, out var payload, out var refusal);
            Assert.True(ok, refusal);
            Assert.NotNull(payload);
            Assert.Equal("success", payload!["status"]?.ToString());
            Assert.Equal("ProcTest", payload["name"]?.ToString());
            Assert.Equal("Source", payload["part"]?.ToString());
            Assert.True(payload["persisted"]?.Value<bool>());
            Assert.Equal(7, payload["bytes"]?.Value<int>());
            Assert.Equal(4, payload["versionId"]?.Value<int>());
            Assert.Equal("gxengine", payload["engine"]?["backend"]?.ToString());
        }
    }
}
