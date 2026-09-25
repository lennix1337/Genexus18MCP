using Newtonsoft.Json.Linq;
using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class Issue201IndexMirrorTests
    {
        [Fact]
        public void Index_mirror_is_scoped_to_kb_alias()
        {
            Program.ResetIndexStateMirrorForTest();
            Program.UpdateLastKnownIndexStateForTest("KB-A", "Ready", 11, DateTime.UtcNow.AddMinutes(-2), "current");
            Program.UpdateLastKnownIndexStateForTest("KB-B", "Cold", 0, null, "stale");

            JObject a = Program.BuildIndexBlockForTest("KB-A");
            JObject b = Program.BuildIndexBlockForTest("KB-B");

            Assert.Equal("KB-A", a["kbAlias"]?.ToString());
            Assert.Equal("Ready", a["status"]?.ToString());
            Assert.Equal(11, a["totalObjects"]?.ToObject<int>());
            Assert.Equal("current", a["freshness"]?.ToString());
            Assert.Equal("KB-B", b["kbAlias"]?.ToString());
            Assert.Equal("Cold", b["status"]?.ToString());
            Assert.Equal(0, b["totalObjects"]?.ToObject<int>());
            Assert.Equal("stale", b["freshness"]?.ToString());
        }

        [Fact]
        public void Applying_worker_state_preserves_freshness_and_normalizes_utc()
        {
            Program.ResetIndexStateMirrorForTest();
            bool applied = Program.ApplyIndexStateFromWorkerResult(new JObject
            {
                ["indexStatus"] = "Ready",
                ["totalObjects"] = 3,
                ["freshness"] = "current",
                ["lastSuccessfulScanAt"] = "2026-01-02T03:04:05-03:00"
            });

            Assert.True(applied);
            JObject block = Program.BuildIndexBlockForTest(null);
            Assert.Equal("current", block["freshness"]?.ToString());
            Assert.Equal("2026-01-02T06:04:05.0000000Z", block["lastSuccessfulScanAt"]?.ToString());
        }

        [Fact]
        public void Applying_worker_state_preserves_cache_and_checkpoint_diagnostics()
        {
            Program.ResetIndexStateMirrorForTest();
            try
            {
                Assert.True(Program.ApplyIndexStateFromWorkerResult(new JObject
                {
                    ["indexStatus"] = "Reindexing",
                    ["totalObjects"] = 2000,
                    ["freshness"] = "refreshing",
                    ["resumedFrom"] = 1000,
                    ["checkpointActive"] = true,
                    ["checkpointCapturedAtUtc"] = "2026-09-21T20:00:00Z",
                    ["cacheValidation"] = new JObject
                    {
                        ["bodyPresent"] = true,
                        ["metaPresent"] = false,
                        ["rejectionReason"] = "missing-meta",
                        ["slotGeneration"] = 7
                    }
                }, "diagnostics-kb"));

                JObject block = Program.BuildIndexBlockForTest("diagnostics-kb");
                Assert.Equal(1000, block["resumedFrom"]?.ToObject<int>());
                Assert.True(block["checkpoint"]?["active"]?.ToObject<bool>());
                Assert.Equal("missing-meta", block["cacheValidation"]?["rejectionReason"]?.ToString());
                Assert.Equal(7, block["cacheValidation"]?["slotGeneration"]?.ToObject<long>());
            }
            finally
            {
                Program.ResetIndexStateMirrorForTest();
            }
        }

        [Fact]
        public void Applying_worker_state_mirrors_source_store_backfill_progress()
        {
            Program.ResetIndexStateMirrorForTest();
            try
            {
                Assert.True(Program.ApplyIndexStateFromWorkerResult(new JObject
                {
                    ["indexStatus"] = "Ready",
                    ["freshness"] = "current",
                    ["totalObjects"] = 10,
                    ["sourceStore"] = new JObject
                    {
                        ["storedObjects"] = 7,
                        ["staleObjects"] = 2,
                        ["totalObjects"] = 10,
                        ["state"] = "running",
                        ["etaMs"] = 1200
                    }
                }, "source-store-kb"));

                var block = Program.BuildIndexBlockForTest("source-store-kb");
                Assert.Equal(7, block["sourceStore"]?["storedObjects"]?.ToObject<int>());
                Assert.Equal("running", block["sourceStore"]?["state"]?.ToString());
                Assert.Equal(1200, block["sourceStore"]?["etaMs"]?.ToObject<int>());
            }
            finally
            {
                Program.ResetIndexStateMirrorForTest();
            }
        }

        [Fact]
        public void Applying_worker_state_preserves_jsonnet_date_tokens()
        {
            Program.ResetIndexStateMirrorForTest();
            try
            {
                var workerResult = JObject.Parse(
                    "{\"indexStatus\":\"Ready\",\"freshness\":\"current\",\"totalObjects\":1,"
                    + "\"lastSuccessfulScanAt\":\"2026-09-15T02:52:44.2451889Z\","
                    + "\"lastIndexedAt\":\"2026-09-15T02:52:44.2451889Z\"}");

                Assert.Equal(JTokenType.Date, workerResult["lastSuccessfulScanAt"]?.Type);
                Assert.True(Program.ApplyIndexStateFromWorkerResult(workerResult, "DateTokenKb"));

                var block = Program.BuildIndexBlockForTest("DateTokenKb");
                Assert.Equal("2026-09-15T02:52:44.2451889Z", block["lastSuccessfulScanAt"]?.Value<string>());
                Assert.Equal("2026-09-15T02:52:44.2451889Z", block["lastIndexedAt"]?.Value<string>());

                DateTime unspecified = DateTime.SpecifyKind(
                    new DateTime(2026, 9, 15, 2, 52, 44).AddTicks(2_451_889),
                    DateTimeKind.Unspecified);
                var unspecifiedResult = new JObject
                {
                    ["indexStatus"] = "Ready",
                    ["freshness"] = "current",
                    ["totalObjects"] = 1,
                    ["lastSuccessfulScanAt"] = new JValue(unspecified),
                    ["lastIndexedAt"] = new JValue(unspecified)
                };
                Assert.True(Program.ApplyIndexStateFromWorkerResult(unspecifiedResult, "UnspecifiedDateKb"));
                var unspecifiedBlock = Program.BuildIndexBlockForTest("UnspecifiedDateKb");
                Assert.Equal("2026-09-15T02:52:44.2451889Z", unspecifiedBlock["lastSuccessfulScanAt"]?.Value<string>());
                Assert.Equal("2026-09-15T02:52:44.2451889Z", unspecifiedBlock["lastIndexedAt"]?.Value<string>());
            }
            finally
            {
                Program.ResetIndexStateMirrorForTest();
            }
        }
    }
}
