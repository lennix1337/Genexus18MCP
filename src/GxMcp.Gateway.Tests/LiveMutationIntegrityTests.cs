using System;
using System.Threading.Tasks;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

[Trait("Category", "LiveE2E")]
[Trait("Category", "ProcessSmoke")]
public sealed class LiveMutationIntegrityTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
{
    private readonly LiveGatewayHarness harness;
    public LiveMutationIntegrityTests(LiveGatewayHarness harness) => this.harness = harness;
    public Task InitializeAsync() => harness.InitializeAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JObject> Call(string tool, JObject args)
    {
        JObject response = await harness.CallToolAsync(tool, args, timeoutMs: 120_000);
        JObject? payload = LiveGatewayHarness.ParseToolPayload(response);
        Assert.False(LiveGatewayHarness.IsToolError(response), tool + " failed (" + (response["error"]?["code"]?.ToString() ?? payload?["code"]?.ToString() ?? payload?["error"]?["code"]?.ToString()) + "): "
            + (payload?["message"]?.ToString() ?? payload?["error"]?["message"]?.ToString() ?? response["error"]?["message"]?.ToString()) + "; " + harness.DiagnosticsSummary());
        return payload!;
    }

    private async Task<JObject> Read(string name, string part)
    {
        JObject payload = await Call("genexus_read", new JObject { ["name"] = name, ["part"] = part, ["refresh"] = true });
        return payload["result"] as JObject ?? payload;
    }

    [LiveKbFact]
    public async Task PropertyBatchPreviewVersionFenceAndPersistedReadback()
    {
        await Call("genexus_whoami", new JObject());
        string name = "McpProperties" + Guid.NewGuid().ToString("N").Substring(0, 10);
        bool created = false;
        try
        {
            await Call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = "WebPanel" });
            created = true;
            JObject initial = await Call("genexus_properties", new JObject { ["action"] = "get", ["name"] = name, ["reconcileTimedOutWrite"] = true });
            JObject before = initial["result"] as JObject ?? initial;
            string version = before["versionToken"]!.ToString();
            await Task.Delay(1100);
            var properties = new JObject { ["Description"] = "Synthetic property write", ["MainProgram"] = "True" };
            JObject preview = await Call("genexus_properties", new JObject { ["action"] = "set", ["name"] = name, ["properties"] = properties, ["expectedVersion"] = version, ["dryRun"] = true });
            Assert.False(preview["result"]?["persisted"]?.Value<bool>() ?? true);
            await Call("genexus_properties", new JObject { ["action"] = "set", ["name"] = name, ["properties"] = properties, ["expectedVersion"] = version });
            JObject read = await Call("genexus_properties", new JObject { ["action"] = "get", ["name"] = name, ["propertyNames"] = new JArray("Description", "MainProgram"), ["reconcileTimedOutWrite"] = true });
            JObject after = read["result"] as JObject ?? read;
            Assert.Contains("Synthetic property write", after.ToString());
            JObject stale = await harness.CallToolAsync("genexus_properties", new JObject { ["action"] = "set", ["name"] = name,
                ["properties"] = new JObject { ["Description"] = "Must not overwrite" }, ["expectedVersion"] = version }, timeoutMs: 120_000);
            Assert.True(LiveGatewayHarness.IsToolError(stale), "Stale version was accepted; before=" + version + "; after=" + after["versionToken"]);
            Assert.Contains("VersionConflict", LiveGatewayHarness.ParseToolPayload(stale)!.ToString());
        }
        finally
        {
            if (created) await Call("genexus_delete_object", new JObject { ["name"] = name, ["type"] = "WebPanel", ["confirm"] = true });
        }
    }

    [LiveKbFact]
    public async Task ExternalMethodSavePreservesContractAndRejectsStaleVersion()
    {
        await Call("genexus_whoami", new JObject());
        string name = "McpExternal" + Guid.NewGuid().ToString("N").Substring(0, 10);
        bool created = false;
        try
        {
            await Call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = "ExternalObject" });
            created = true;
            JObject before = await Read(name, "EXOStructure");
            var payload = new JObject { ["name"] = "SampleMethod", ["expectedVersion"] = before["versionToken"],
                ["parameters"] = new JArray(new JObject { ["name"] = "Id", ["type"] = "GUID", ["inout"] = "in" },
                    new JObject { ["name"] = "Success", ["type"] = "Boolean", ["inout"] = "out" }) };
            await Call("genexus_authoring", new JObject { ["action"] = "add_external_method", ["name"] = name, ["payload"] = payload });
            JObject persisted = await Read(name, "EXOStructure");
            JObject method = ((JArray)persisted["externalMethods"]!).OfType<JObject>().Single(m => m["name"]?.ToString() == "SampleMethod");
            Assert.Equal("out", method["parameters"]?[1]?["inout"]?.ToString());
            payload["name"] = "StaleMethod";
            JObject stale = await harness.CallToolAsync("genexus_authoring", new JObject { ["action"] = "add_external_method", ["name"] = name, ["payload"] = payload }, timeoutMs: 120_000);
            Assert.True(LiveGatewayHarness.IsToolError(stale));
            Assert.Contains("VersionConflict", LiveGatewayHarness.ParseToolPayload(stale)!.ToString());
        }
        finally
        {
            if (created) await Call("genexus_delete_object", new JObject { ["name"] = name, ["type"] = "ExternalObject", ["confirm"] = true });
        }
    }

    [LiveKbFact]
    public async Task SdtAddPreservesExistingMemberAndReplacementRequiresConsent()
    {
        await Call("genexus_whoami", new JObject());
        string name = "McpStructure" + Guid.NewGuid().ToString("N").Substring(0, 10);
        bool created = false;
        try
        {
            await Call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = "SDT", ["firstItem"] = "Original", ["firstItemType"] = "Numeric(4)" });
            created = true;
            JObject read = await Call("genexus_structure", new JObject { ["action"] = "get_visual", ["name"] = name, ["type"] = "SDT" });
            JObject before = read["result"] as JObject ?? read;
            var add = new JObject { ["mode"] = "add", ["children"] = new JArray(new JObject { ["name"] = "Added", ["type"] = "Numeric", ["length"] = 8 }) };
            await Call("genexus_structure", new JObject { ["action"] = "update_visual", ["name"] = name, ["payload"] = add, ["expectedVersion"] = before["versionToken"] });
            read = await Call("genexus_structure", new JObject { ["action"] = "get_visual", ["name"] = name, ["type"] = "SDT" });
            JObject after = read["result"] as JObject ?? read;
            var children = (JArray)after["children"]!;
            Assert.Contains(children, x => x["name"]?.ToString() == "Original");
            Assert.Contains(children, x => x["name"]?.ToString() == "Added");
            JObject rejected = await harness.CallToolAsync("genexus_structure", new JObject { ["action"] = "update_visual", ["name"] = name,
                ["expectedVersion"] = after["versionToken"], ["payload"] = new JObject { ["children"] = new JArray(new JObject { ["name"] = "Added", ["type"] = "Numeric", ["length"] = 8 }) } }, timeoutMs: 120_000);
            Assert.True(LiveGatewayHarness.IsToolError(rejected));
            Assert.Contains("RemovalRequiresConfirmation", LiveGatewayHarness.ParseToolPayload(rejected)!.ToString());
        }
        finally
        {
            if (created) await Call("genexus_delete_object", new JObject { ["name"] = name, ["type"] = "SDT", ["confirm"] = true });
        }
    }

    [LiveKbFact]
    public async Task MultipartPreviewAndVariablesDeltaPreservePersistedState()
    {
        JObject identity = await Call("genexus_whoami", new JObject());
        JObject context = identity["result"] as JObject ?? identity;
        Assert.Equal("valid", context["selectionState"]?.ToString() ?? context["kb"]?["selectionState"]?.ToString());
        string name = "McpIntegrity" + Guid.NewGuid().ToString("N").Substring(0, 10);
        bool created = false;
        try
        {
            await Call("genexus_create", new JObject { ["action"] = "object", ["name"] = name, ["type"] = "Procedure" });
            created = true;
            await Call("genexus_edit", new JObject { ["name"] = name, ["part"] = "Source", ["mode"] = "full", ["content"] = "// original source" });
            await Call("genexus_edit", new JObject { ["name"] = name, ["part"] = "Rules", ["mode"] = "full", ["content"] = "// original rules" });
            JObject sourceBefore = await Read(name, "Source");
            JObject rulesBefore = await Read(name, "Rules");
            foreach (string mode in new[] { "full", "patch" })
            {
                var parts = new JArray(
                    new JObject { ["part"] = "Source", ["mode"] = mode, ["content"] = "// proposed source", ["operation"] = "Append", ["dryRun"] = false },
                    new JObject { ["part"] = "Rules", ["mode"] = "full", ["content"] = "// proposed rules" });
                JObject preview = await Call("genexus_edit", new JObject { ["name"] = name, ["dryRun"] = true, ["parts"] = parts });
                Assert.False(preview["result"]?["savePathExercised"]?.Value<bool>() ?? true);
                Assert.Equal(sourceBefore["source"]?.ToString(), (await Read(name, "Source"))["source"]?.ToString());
                Assert.Equal(rulesBefore["source"]?.ToString(), (await Read(name, "Rules"))["source"]?.ToString());
                Assert.Equal(sourceBefore["versionToken"]?.ToString(), (await Read(name, "Source"))["versionToken"]?.ToString());
            }
            await Call("genexus_variable", new JObject { ["action"] = "add", ["name"] = name, ["varName"] = "Preserved", ["typeName"] = "Numeric(8,2)" });
            await Call("genexus_variable", new JObject { ["action"] = "modify", ["name"] = name, ["varName"] = "Preserved", ["description"] = "Metadata outside the textual DSL" });
            JObject variablesBefore = await Read(name, "Variables");
            string before = variablesBefore["source"]!.ToString();
            JObject propertiesBefore = await Call("genexus_properties", new JObject { ["action"] = "get", ["name"] = name, ["control"] = "&Preserved", ["projection"] = "full", ["reconcileTimedOutWrite"] = true });
            JObject added = await Call("genexus_edit", new JObject { ["name"] = name, ["part"] = "Variables", ["mode"] = "full", ["content"] = before + "\n&Added : Numeric(4)" });
            JObject addedResult = added["result"] as JObject ?? added;
            Assert.False(addedResult["metadataVerified"]?.Value<bool>() ?? true);
            JObject propertiesAfter = await Call("genexus_properties", new JObject { ["action"] = "get", ["name"] = name, ["control"] = "&Preserved", ["projection"] = "full", ["reconcileTimedOutWrite"] = true });
            var beforeProperties = propertiesBefore["result"]?["properties"] ?? propertiesBefore["properties"];
            var afterProperties = propertiesAfter["result"]?["properties"] ?? propertiesAfter["properties"];
            Assert.IsType<JArray>(beforeProperties);
            Assert.NotEmpty((JArray)beforeProperties!);
            Assert.Contains("Metadata outside the textual DSL", beforeProperties!.ToString());
            Assert.True(JToken.DeepEquals(beforeProperties, afterProperties), "Exposed metadata of the unchanged variable changed.");
            JObject current = await Read(name, "Variables");
            JObject noOp = await Call("genexus_edit", new JObject { ["name"] = name, ["part"] = "Variables", ["mode"] = "full", ["content"] = current["source"] });
            Assert.Equal("WriteNoChange", noOp["code"]?.ToString());
            Assert.Equal(current["versionToken"]?.ToString(), (await Read(name, "Variables"))["versionToken"]?.ToString());
            JObject invalid = await harness.CallToolAsync("genexus_edit", new JObject { ["name"] = name, ["parts"] = new JArray(
                new JObject { ["part"] = "Source", ["mode"] = "full", ["content"] = "// must not save" },
                new JObject { ["part"] = "Variables", ["mode"] = "full", ["content"] = "not a declaration" }) }, timeoutMs: 120_000);
            Assert.True(LiveGatewayHarness.IsToolError(invalid));
            Assert.Equal(sourceBefore["source"]?.ToString(), (await Read(name, "Source"))["source"]?.ToString());
            await Call("genexus_edit", new JObject { ["name"] = name, ["dryRun"] = false, ["parts"] = new JArray(
                new JObject { ["part"] = "Source", ["mode"] = "full", ["content"] = "// authorized source" },
                new JObject { ["part"] = "Rules", ["mode"] = "full", ["content"] = "// authorized rules" }) });
            Assert.Contains("authorized source", (await Read(name, "Source"))["source"]?.ToString());
            Assert.Contains("authorized rules", (await Read(name, "Rules"))["source"]?.ToString());
        }
        finally
        {
            if (created)
            {
                await Call("genexus_delete_object", new JObject { ["name"] = name, ["type"] = "Procedure", ["confirm"] = true });
                JObject missing = await harness.CallToolAsync("genexus_read", new JObject { ["name"] = name, ["refresh"] = true }, timeoutMs: 60_000);
                Assert.True(LiveGatewayHarness.IsToolError(missing), "Synthetic object still exists after cleanup.");
            }
        }
    }
}
