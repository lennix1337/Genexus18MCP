using System;
using System.IO;
using Xunit;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    // Tests that read or mutate Program._lastKnownIndexState (a process-wide static mirror)
    // must not run in parallel with each other, or one test's transient status value leaks
    // into another's assertions. Membership in this collection serializes them.
    [CollectionDefinition("IndexStateMirror", DisableParallelization = true)]
    public sealed class IndexStateMirrorCollection { }

    [Collection("IndexStateMirror")]
    public class WhoamiVersionTests
    {
        [Fact]
        public void DetectGeneXusVersion_ReadsVersionTxt()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx-version-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "version.txt"), "18.0.4\nOther line");
                string? detected = Program.DetectGeneXusVersion(tmp);
                Assert.Equal("18.0.4", detected);
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void DetectGeneXusVersion_ReturnsNullWhenMissing()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx-version-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Assert.Null(Program.DetectGeneXusVersion(tmp));
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void DetectGeneXusVersion_ReturnsNullForNullOrEmptyPath()
        {
            Assert.Null(Program.DetectGeneXusVersion(null));
            Assert.Null(Program.DetectGeneXusVersion(""));
            Assert.Null(Program.DetectGeneXusVersion("   "));
        }

        [Fact]
        public void DetectGeneXusVersion_AcceptsVersionWithCapitalV()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx-version-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "Version.txt"), "18.1.0");
                Assert.Equal("18.1.0", Program.DetectGeneXusVersion(tmp));
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void BuildWhoamiPayload_ShapeIsStable_WhenNoConfig()
        {
            var payload = Program.BuildWhoamiPayload();
            Assert.NotNull(payload["connected"]);
            Assert.NotNull(payload["kb"]);
            Assert.NotNull(payload["geneXus"]);
            Assert.NotNull(payload["config"]);
            Assert.NotNull(payload["mcp"]);
            Assert.NotNull(payload["mcp"]?["serverVersion"]);
            Assert.NotNull(payload["mcp"]?["protocolVersion"]);
            Assert.NotNull(payload["geneXus"]?["supportedMajor"]);
            Assert.Equal("18", payload["geneXus"]?["supportedMajor"]?.ToString());

            var supportedMajors = Assert.IsType<JArray>(payload["geneXus"]?["supportedMajors"]);
            var supportedMajorValues = supportedMajors.ToObject<string[]>() ?? Array.Empty<string>();
            Assert.Contains("16", supportedMajorValues);
            Assert.Contains("17", supportedMajorValues);
            Assert.Contains("18", supportedMajorValues);
            Assert.Equal("18", payload["geneXus"]?["catalog"]?["primaryMajor"]?.ToString());
            var legacyMajors = Assert.IsType<JArray>(payload["geneXus"]?["catalog"]?["legacyMajors"]);
            Assert.Contains("8", legacyMajors.ToObject<string[]>() ?? Array.Empty<string>());
            Assert.NotNull(payload["geneXus"]?["catalog"]?["source"]);
        }

        [Fact]
        public void BuildWhoamiPayload_PreservesCatalogEntryDetailInTheDefaultHealthCheck()
        {
            // Existing clients may consume the catalog entries from the default health
            // payload. Keep that contract stable; the internal slim projection remains
            // available for callers that explicitly opt into it.
            var standard = Program.BuildWhoamiPayload();
            var standardCatalog = Assert.IsType<JObject>(standard["geneXus"]?["catalog"]);
            Assert.NotNull(standardCatalog["entries"]);
            Assert.NotNull(standardCatalog["legacyEntries"]);
            Assert.Equal("18", standardCatalog["primaryMajor"]?.ToString());
            Assert.NotNull(standardCatalog["source"]);
            Assert.NotNull(standardCatalog["supportedMajors"]);
            Assert.NotNull(standardCatalog["legacyMajors"]);

            var verbose = Program.BuildWhoamiPayload(verbose: true);
            var verboseCatalog = Assert.IsType<JObject>(verbose["geneXus"]?["catalog"]);
            Assert.NotNull(verboseCatalog["entries"]);
            Assert.NotNull(verboseCatalog["legacyEntries"]);
        }

        [Fact]
        public void ToDiagnosticObject_KeepsEntryDetailForCallersThatResolveInstallPaths()
        {
            // KbCreateHelper resolves a declared major to its default install path from
            // catalog.entries, so the default projection must keep the entry detail.
            var full = GeneXusVersionCatalog.ToDiagnosticObject();
            Assert.NotNull(full["entries"]);
            Assert.NotNull(full["legacyEntries"]);

            var slim = GeneXusVersionCatalog.ToDiagnosticObject(includeEntryDetail: false);
            Assert.Null(slim["entries"]);
            Assert.NotNull(slim["source"]);
            Assert.Equal(full["primaryMajor"]?.ToString(), slim["primaryMajor"]?.ToString());
        }

        [Theory]
        [InlineData("16.0.11.144151", "16", true)]
        [InlineData("17.0.11.163677", "17", true)]
        [InlineData("18.0.6", "18", true)]
        [InlineData("19.0.0", "19", false)]
        [InlineData("170.0.0", "170", false)]
        public void GeneXusVersionCatalog_MatchesOnlyExplicitlySupportedMajors(
            string version, string expectedMajor, bool expectedSupported)
        {
            Assert.Equal(expectedMajor, GeneXusVersionCatalog.GetMajor(version));
            Assert.Equal(expectedSupported, GeneXusVersionCatalog.IsSupported(version));
        }

        [Fact]
        public void GeneXusVersionCatalog_RecognizesLegacyMajorsAndDrivers()
        {
            Assert.True(GeneXusVersionCatalog.IsLegacyMajor("10.3"));
            Assert.True(GeneXusVersionCatalog.IsLegacyMajor("10.2"));
            Assert.True(GeneXusVersionCatalog.IsLegacyMajor("10.1"));
            Assert.True(GeneXusVersionCatalog.IsLegacyMajor("9"));
            Assert.True(GeneXusVersionCatalog.IsLegacyMajor("8"));
            Assert.False(GeneXusVersionCatalog.IsLegacyMajor("18"));
            Assert.False(GeneXusVersionCatalog.IsLegacyMajor("unknown"));
            Assert.False(GeneXusVersionCatalog.IsLegacyMajor(null));
            Assert.False(GeneXusVersionCatalog.IsLegacyMajor(""));
            Assert.False(GeneXusVersionCatalog.IsLegacyMajor("   "));

            Assert.Equal("dotnet-reflection", GeneXusVersionCatalog.GetDriverProfile("10.3"));
            Assert.Equal("dotnet-reflection", GeneXusVersionCatalog.GetDriverProfile("10.3.0.86550"));
            Assert.Equal("dotnet-reflection", GeneXusVersionCatalog.GetDriverProfile("15"));
            Assert.Equal("com-gxpublic", GeneXusVersionCatalog.GetDriverProfile("9"));
            Assert.Equal("com-gxpublic", GeneXusVersionCatalog.GetDriverProfile("9.0.123"));
            Assert.Equal("com-gxpublic", GeneXusVersionCatalog.GetDriverProfile("8"));
            Assert.Equal("com-gxpublic", GeneXusVersionCatalog.GetDriverProfile("8.0.456"));
            Assert.Equal("native-sdk", GeneXusVersionCatalog.GetDriverProfile("18"));
            Assert.Equal("native-sdk", GeneXusVersionCatalog.GetDriverProfile("18.0.4.180000"));
            Assert.Equal("native-sdk", GeneXusVersionCatalog.GetDriverProfile("17"));
            Assert.Equal("native-sdk", GeneXusVersionCatalog.GetDriverProfile("16"));
            Assert.Null(GeneXusVersionCatalog.GetDriverProfile("unknown"));
            Assert.Null(GeneXusVersionCatalog.GetDriverProfile(null));
            Assert.Null(GeneXusVersionCatalog.GetDriverProfile(""));

            Assert.True(GeneXusVersionCatalog.IsSupportedOrLegacy("10.3.0"));
            Assert.True(GeneXusVersionCatalog.IsSupportedOrLegacy("18.0.4"));
            Assert.True(GeneXusVersionCatalog.IsSupportedOrLegacy("8.0.0"));
            Assert.False(GeneXusVersionCatalog.IsSupportedOrLegacy("19.0.0"));
            Assert.False(GeneXusVersionCatalog.IsSupportedOrLegacy(null));
            Assert.False(GeneXusVersionCatalog.IsSupportedOrLegacy(""));

            Assert.Contains("10.3", GeneXusVersionCatalog.LegacyMajors);
            Assert.Contains("10.2", GeneXusVersionCatalog.LegacyMajors);
            Assert.Contains("10.1", GeneXusVersionCatalog.LegacyMajors);
            Assert.Contains("9", GeneXusVersionCatalog.LegacyMajors);
            Assert.Contains("8", GeneXusVersionCatalog.LegacyMajors);
        }

        [Fact]
        public void Whoami_ExposesMultiKbSelectionContext()
        {
            var payload = Program.BuildWhoamiPayload();
            var kb = Assert.IsType<Newtonsoft.Json.Linq.JObject>(payload["kb"]);

            Assert.NotNull(kb["active"]);
            Assert.NotNull(kb["openKbs"]);
            Assert.NotNull(kb["knownKbs"]);
            Assert.NotNull(kb["declaredKbs"]);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Array, kb["openKbs"]!.Type);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Array, kb["knownKbs"]!.Type);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Array, kb["declaredKbs"]!.Type);
        }

        [Fact]
        public void Whoami_Exposes_the_session_selected_alias_separately()
        {
            const string sessionId = "whoami-session-test";
            Program.SetSessionSelectedKb(sessionId, "orders");
            try
            {
                var payload = Program.BuildWhoamiPayload(false, sessionId);
                var kb = Assert.IsType<Newtonsoft.Json.Linq.JObject>(payload["kb"]);

                Assert.Equal("orders", kb["selected"]?.ToString());
                Assert.Equal("orders", kb["active"]?.ToString());
                Assert.Equal("session-select", kb["selectionSource"]?.ToString());
                Assert.Equal("orders", kb["sessionSelection"]?.ToString());
                Assert.Equal("invalid", kb["selectionState"]?.ToString());
                Assert.True(kb["contextRequired"]?.ToObject<bool>());
                Assert.True(kb.ContainsKey("persistedFallback"));
            }
            finally
            {
                Program.ClearSessionSelectedKb(sessionId);
            }
        }

        // v2.3.8 Task 1.2: whoami surfaces index readiness so the agent can know
        // whether it should call `lifecycle action=index` before relying on
        // `search_source` / `analyze` results.
        [Fact]
        public void Whoami_IncludesIndexBlock()
        {
            var payload = Program.BuildWhoamiPayload();
            var index = payload["index"] as Newtonsoft.Json.Linq.JObject;
            Assert.NotNull(index);

            var status = index!["status"]?.ToString();
            Assert.Contains(status, new[] { "Cold", "Reindexing", "Ready" });

            var totalObjects = index["totalObjects"];
            Assert.NotNull(totalObjects);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Integer, totalObjects!.Type);

            // Optional fields must at least be present as JSON keys (may be null).
            Assert.True(index.ContainsKey("lastIndexedAt"));
            Assert.True(index.ContainsKey("progress"));
            Assert.True(index.ContainsKey("etaMs"));
        }

        // v2.3.8 Task 1.2: whoami must reflect worker-reported index state when one is
        // available. Unit tests don't spin up a real WorkerPool, so we exercise the
        // gateway-side fallback path via UpdateLastKnownIndexState: the live fetch in
        // BuildWhoamiPayloadAsync gracefully falls back to this cached snapshot when
        // the worker is unreachable, which is the same code-path that serves stale
        // reads after a worker outage. End-to-end coverage with a real worker
        // round-trip is tracked under Task 8.1 (end-to-end smoke test).
        [Fact]
        public async System.Threading.Tasks.Task Whoami_IndexBlock_ReflectsWorkerState()
        {
            var lastIndexed = new DateTime(2026, 5, 15, 10, 30, 0, DateTimeKind.Utc);
            Program.UpdateLastKnownIndexState("Ready", 42, lastIndexed, null, null);

            var payload = await Program.BuildWhoamiPayloadAsync();
            var index = payload["index"] as Newtonsoft.Json.Linq.JObject;
            Assert.NotNull(index);
            Assert.Equal("Ready", index!["status"]?.ToString());
            Assert.Equal(42, index["totalObjects"]?.ToObject<int>());
            Assert.Equal(lastIndexed.ToString("o"), index["lastIndexedAt"]?.ToString());

            // Reset so we don't pollute other tests that rely on default Cold/0.
            Program.UpdateLastKnownIndexState("Cold", 0, null, null, null);
        }
    }
}
