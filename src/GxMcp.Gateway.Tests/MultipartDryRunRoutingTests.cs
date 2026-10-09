using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public sealed class MultipartDryRunRoutingTests
{
    [Theory]
    [InlineData(true, "full", "full", 1)]
    [InlineData(true, "full", "full", 2)]
    [InlineData(true, "patch", "patch", 2)]
    [InlineData(true, "full", "patch", 2)]
    [InlineData(false, "full", "full", 2)]
    [InlineData(false, "patch", "full", 2)]
    public void MultipartEditPreservesRequestDryRun(bool dryRun, string firstMode, string secondMode, int count)
    {
        var parts = new JArray(new JObject { ["part"] = "Source", ["mode"] = firstMode, ["content"] = "// preview", ["dryRun"] = false });
        if (count == 2)
            parts.Add(new JObject { ["part"] = "Rules", ["mode"] = secondMode, ["content"] = "// rules" });
        var args = new JObject { ["name"] = "SyntheticProcedure", ["dryRun"] = dryRun, ["parts"] = parts };
        var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args)!);
        Assert.Equal("Batch", routed["module"]?.ToString());
        Assert.Equal("BatchEdit", routed["action"]?.ToString());
        Assert.Equal(dryRun, routed["dryRun"]?.Value<bool>());
        Assert.True(JToken.DeepEquals(parts, routed["changes"]));
    }
}
