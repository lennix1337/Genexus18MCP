using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

/// <summary>
/// Cold-start usefulness of the native engine: engine-served index-class reads say the Worker
/// index is still warming (existing <c>_meta.index</c> block), and the IndexNotReady refusal names
/// the gated reads gx-engine answers without the index, only when that is true.
/// Routing order (engine before the index gate) is in Program.ToolDispatch.cs.
/// </summary>
public sealed class EngineColdStartTests : EngineRoutingTestBase, IDisposable
{
    private const string QueryArgs = "{\"query\":\"type:Procedure\",\"limit\":10,\"fields\":[\"name\",\"type\"]}";
    private const string QueryAnswer = "{\"objects\":[{\"name\":\"Alpha\",\"type\":\"Procedure\"}],\"total\":1,\"truncated\":false}";

    public EngineColdStartTests() => Program.ResetIndexStateMirrorForTest();

    public new void Dispose()
    {
        Program.ResetIndexStateMirrorForTest();
        base.Dispose();
    }

    private async Task<JObject> ServeQueryAsync()
    {
        string tempDir = NewTempDir();
        try
        {
            string stub = CreateStub(tempDir, "engine.cmd",
                "@echo off\r\necho " + QueryAnswer + "\r\n");
            SetEngineEnv(EngineBackendSettings.ExecutableEnvVar, stub);
            JObject? result = await DispatchAsync("genexus_query", null, QueryArgs, tempDir);
            Assert.NotNull(result);
            Assert.Equal("gxengine", PayloadOf(result!)["engine"]?["backend"]?.ToString());
            return result!;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EngineServedQuery_WhileIndexWarms_CarriesTheExistingIndexMeta()
    {
        Program.UpdateLastKnownIndexStateForTest("", "Reindexing", 0, null, "stale");

        JObject result = await ServeQueryAsync();

        JObject? index = result["_meta"]?["index"] as JObject;
        Assert.NotNull(index);
        Assert.Equal("Reindexing", index!["status"]?.ToString());
        Assert.Equal("stale", index["freshness"]?.ToString());
    }

    [Fact]
    public async Task EngineServedQuery_WithUsableIndex_AddsNothing()
    {
        Program.UpdateLastKnownIndexStateForTest("", "Ready", 10, DateTime.UtcNow, "current");

        JObject result = await ServeQueryAsync();

        Assert.Null(result["_meta"]?["index"]);
    }

    [Fact]
    public async Task EngineServedQuery_WithNeverRefreshedMirror_DoesNotClaimWarming()
    {
        // The default snapshot is a placeholder Cold: unknown, not "warming".
        JObject result = await ServeQueryAsync();

        Assert.Null(result["_meta"]?["index"]);
    }

    [Fact]
    public void IndexMeta_IsLimitedToIndexClassReads()
    {
        Program.UpdateLastKnownIndexStateForTest("", "Reindexing", 0, null, "stale");

        var build = new JObject { ["isError"] = false };
        Program.AttachNativeServedIndexMetadataForTest(build, "genexus_lifecycle");
        Assert.Null(build["_meta"]);

        var list = new JObject { ["isError"] = false };
        Program.AttachNativeServedIndexMetadataForTest(list, "genexus_list_objects");
        Assert.Equal("Reindexing", list["_meta"]?["index"]?["status"]?.ToString());
    }

    private static readonly Func<EngineBackendSettings, string?> EnginePresent = _ => "gx-engine.exe";
    private static readonly Func<EngineBackendSettings, string?> EngineAbsent = _ => null;

    private EngineBackendSettings Settings(string backend, string fallback = "sdk")
    {
        SetEngineEnv(EngineBackendSettings.BackendEnvVar, backend);
        SetEngineEnv(EngineBackendSettings.FallbackEnvVar, fallback);
        return EngineBackendSettings.Resolve(null);
    }

    [Theory]
    [InlineData("gxengine", "sdk")]
    [InlineData("gxengine", "error")]
    public void NativeReads_AreNamedWhenTheEngineWouldRouteThemAndExists(string backend, string fallback)
    {
        JArray? reads = Program.BuildNativeReadsAvailableDuringIndexing(Settings(backend, fallback), EnginePresent);

        Assert.NotNull(reads);
        Assert.Equal(2, reads!.Count);
        Assert.Contains(reads, r => r.ToString().StartsWith("genexus_query ", StringComparison.Ordinal));
        Assert.Contains(reads, r => r.ToString().StartsWith("genexus_list_objects ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("auto")]
    public void NativeReads_AreOmittedWhenTheBackendDoesNotRouteReads(string backend)
    {
        Assert.Null(Program.BuildNativeReadsAvailableDuringIndexing(Settings(backend), EnginePresent));
    }

    [Fact]
    public void NativeReads_AreOmittedWhenTheEngineExecutableIsMissing()
    {
        Assert.Null(Program.BuildNativeReadsAvailableDuringIndexing(Settings("gxengine"), EngineAbsent));
    }

    [Fact]
    public void NativeReads_OnlyNameOperationsTheGateRefusesAndTheEngineRoutes()
    {
        foreach (string operation in Program.NativeReadOperationsDuringIndexingForTest)
        {
            Assert.Contains(operation, Program.IndexDependentTools);
            Assert.NotNull(EngineRoutedOperations.EngineCapabilityIdForOperation(operation));
        }
    }
}
