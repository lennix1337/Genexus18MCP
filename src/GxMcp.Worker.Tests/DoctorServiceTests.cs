using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Doctor is a triage probe — its contract is "never throw, always return an
    /// envelope". We exercise the all-null path (no KB, no cache, no tracker),
    /// the envelope shape contract, the KB-not-open warning path, and the
    /// telemetry-sort path via a tiny stub tracker.
    /// </summary>
    public class DoctorServiceTests
    {
        [Fact]
        public void Diagnose_AllNullDependencies_DoesNotThrowAndReturnsSuccess()
        {
            var svc = new DoctorService(null, null, null);
            string raw = svc.Diagnose();
            Assert.False(string.IsNullOrWhiteSpace(raw));
            var json = JObject.Parse(raw);
            Assert.Equal("ok", (string)json["status"]);
        }

        [Fact]
        public void Diagnose_ReturnsEnvelopeWithAllRequiredBlocks()
        {
            var svc = new DoctorService(null, null, null);
            var json = JObject.Parse(svc.Diagnose());

            Assert.NotNull(json["status"]);
            Assert.NotNull(json["result"]?["checkedAt"]);
            Assert.NotNull(json["result"]?["version"]);
            Assert.NotNull(json["result"]?["geneXus"]);
            Assert.NotNull(json["result"]?["kb"]);
            Assert.NotNull(json["result"]?["worker"]);
            Assert.NotNull(json["result"]?["cache"]);
            Assert.NotNull(json["result"]?["telemetry"]);
            Assert.NotNull(json["result"]?["warnings"]);
            // 'hint' is either null or a string — must be present.
            Assert.True(((JObject)json["result"]!).ContainsKey("hint"));

            // Worker block must always have a real pid (the running test host).
            Assert.True((int)json["result"]!["worker"]!["pid"] > 0);

            // Telemetry shape contract: zero-state but well-formed.
            Assert.Equal(0L, (long)json["result"]!["telemetry"]!["totalToolCalls"]);
            Assert.IsType<JArray>(json["result"]!["telemetry"]!["slowestTools"]);
            Assert.IsType<JArray>(json["result"]!["telemetry"]!["mostCalled"]);
        }

        [Fact]
        public void Diagnose_NullKbService_EmitsCallOpenKbWarning()
        {
            var svc = new DoctorService(null, null, null);
            var json = JObject.Parse(svc.Diagnose());
            var warnings = (JArray)json["result"]!["warnings"];
            bool found = false;
            foreach (var w in warnings)
            {
                if (((string)w).IndexOf("genexus_open_kb", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    break;
                }
            }
            Assert.True(found, "Expected a 'Call genexus_open_kb first.' warning when KB is unopened. Got: " + warnings.ToString());
            // Hint should mirror first warning when any present.
            Assert.NotEqual(JTokenType.Null, json["result"]!["hint"].Type);
        }

        [Fact]
        public void Diagnose_TelemetryFromTracker_SortsSlowestByP95Desc()
        {
            // Tracker stub exposing BuildMetricsPayload(): { tools: [...] } — reflection
            // discovers the method just as the Gateway's OperationTracker exposes.
            var stub = new FakeTracker(new JObject
            {
                ["tools"] = new JArray
                {
                    new JObject { ["toolName"] = "fast",   ["count"] = 50, ["errors"] = 0,  ["p95Ms"] = 12 },
                    new JObject { ["toolName"] = "slow",   ["count"] = 10, ["errors"] = 0,  ["p95Ms"] = 9000 },
                    new JObject { ["toolName"] = "medium", ["count"] = 20, ["errors"] = 3,  ["p95Ms"] = 400 },
                }
            });
            var svc = new DoctorService(null, null, stub);
            var json = JObject.Parse(svc.Diagnose());

            var slowest = (JArray)json["result"]!["telemetry"]!["slowestTools"];
            Assert.Equal(3, slowest.Count);
            Assert.Equal("slow",   (string)slowest[0]["name"]);
            Assert.Equal(9000L,    (long)slowest[0]["p95Ms"]);
            Assert.Equal("medium", (string)slowest[1]["name"]);
            Assert.Equal("fast",   (string)slowest[2]["name"]);

            // mostCalled sorted by count desc.
            var most = (JArray)json["result"]!["telemetry"]!["mostCalled"];
            Assert.Equal("fast", (string)most[0]["name"]);

            // Totals.
            Assert.Equal(80L, (long)json["result"]!["telemetry"]!["totalToolCalls"]);
            // error rate = 3 / 80 = 0.0375 → 0.038 after rounding to 3dp.
            double er = (double)json["result"]!["telemetry"]!["errorRate"];
            Assert.True(er > 0.03 && er < 0.05, "errorRate should be ~0.038, got " + er);
        }

        // ---- v2.8.5 regression: doctor must agree with whoami -----------------

        [Fact]
        public void Version_PrefersGatewayServerVersionFromEnv()
        {
            string prev = Environment.GetEnvironmentVariable("GXMCP_SERVER_VERSION");
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_SERVER_VERSION", "9.9.9-test");
                var json = JObject.Parse(new DoctorService(null, null, null).Diagnose());
                Assert.Equal("9.9.9-test", (string)json["result"]!["version"]!["current"]);
                Assert.Equal("gateway", (string)json["result"]!["version"]!["source"]);
            }
            finally { Environment.SetEnvironmentVariable("GXMCP_SERVER_VERSION", prev); }
        }

        [Fact]
        public void Version_FallsBackToWorkerAssemblyWhenEnvUnset()
        {
            string prev = Environment.GetEnvironmentVariable("GXMCP_SERVER_VERSION");
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_SERVER_VERSION", null);
                var json = JObject.Parse(new DoctorService(null, null, null).Diagnose());
                Assert.Equal("worker-assembly", (string)json["result"]!["version"]!["source"]);
                Assert.False(string.IsNullOrWhiteSpace((string)json["result"]!["version"]!["current"]));
            }
            finally { Environment.SetEnvironmentVariable("GXMCP_SERVER_VERSION", prev); }
        }

        [Fact]
        public void Sdk_DetectedViaGxProgramDir_NoFalseCritical()
        {
            // The gateway sets GX_PROGRAM_DIR, not GX_PATH. doctor must resolve the
            // SDK from it and NOT raise the false "SDK not found / CRITICAL" warning.
            string prevPath = Environment.GetEnvironmentVariable("GX_PATH");
            string prevProg = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR");
            string tmp = Path.Combine(Path.GetTempPath(), "gxdoctor_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                File.WriteAllText(Path.Combine(tmp, "Artech.Fake.Sdk.dll"), "stub");
                Environment.SetEnvironmentVariable("GX_PATH", null);
                Environment.SetEnvironmentVariable("GX_PROGRAM_DIR", tmp);

                var json = JObject.Parse(new DoctorService(null, null, null).Diagnose());
                var gx = json["result"]!["geneXus"]!;
                Assert.True((bool)gx["found"]!);
                Assert.True((int)gx["sdkDllCount"]! >= 1);
                Assert.Equal("GX_PROGRAM_DIR", (string)gx["source"]!);

                var warnings = (JArray)json["result"]!["warnings"]!;
                Assert.DoesNotContain(warnings.Select(w => (string)w),
                    s => s != null && s.Contains("SDK install not found"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_PATH", prevPath);
                Environment.SetEnvironmentVariable("GX_PROGRAM_DIR", prevProg);
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        [Fact]
        public void Diagnose_ColdIndexCache_DoesNotTriggerLoadAndCompletesImmediately()
        {
            string kbPath = Path.Combine(Path.GetTempPath(), "gxdoctor-cold-" + Guid.NewGuid().ToString("N"));
            var writer = new IndexCacheService();
            writer.Initialize(kbPath, proactiveLoad: false);
            try
            {
                var entries = Enumerable.Range(1, 500)
                    .Select(i => new SearchIndex.IndexEntry
                    {
                        Name = "ColdProc" + i,
                        Type = "Procedure",
                        Guid = Guid.NewGuid().ToString(),
                        Description = "Sample description for cold index load benchmark " + i
                    })
                    .ToArray();
                writer.ReplaceAll(entries);
                Assert.True(writer.FlushNow());

                var coldCache = new IndexCacheService();
                coldCache.Initialize(kbPath, proactiveLoad: false);
                var kb = new KbService(coldCache);
                Assert.Null(coldCache.TryGetLoadedIndex());
                Assert.Equal(0, coldCache.LoadInvocationCountForTest);

                // Warm JIT for DoctorService/JObject/Process probes so the timing measures the call itself.
                _ = new DoctorService(null, null, null).Diagnose();

                var sw = Stopwatch.StartNew();
                var json = JObject.Parse(new DoctorService(kb, coldCache, null).Diagnose());
                sw.Stop();

                Assert.Equal("ok", (string)json["status"]);
                Assert.Equal(0, coldCache.LoadInvocationCountForTest);
                Assert.Null(coldCache.TryGetLoadedIndex());
                Assert.Equal(0, (int)json["result"]!["kb"]!["objectCount"]!);
                Assert.Equal(0, (int)json["result"]!["cache"]!["indexEntries"]!);
                Assert.True(sw.ElapsedMilliseconds < 1000, $"Expected non-blocking Diagnose (<1000ms), took {sw.ElapsedMilliseconds}ms");
            }
            finally
            {
                writer.DeleteOnDiskSnapshot();
                try { if (Directory.Exists(kbPath)) Directory.Delete(kbPath, true); } catch { }
            }
        }

        [Fact]
        public void Diagnose_WarmIndexCache_ReportsLoadedCounts()
        {
            string kbPath = Path.Combine(Path.GetTempPath(), "gxdoctor-warm-" + Guid.NewGuid().ToString("N"));
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            var kb = new KbService(cache);
            try
            {
                cache.ReplaceAll(new[]
                {
                    new SearchIndex.IndexEntry { Name = "WarmProc1", Type = "Procedure", Guid = Guid.NewGuid().ToString() },
                    new SearchIndex.IndexEntry { Name = "WarmProc2", Type = "Procedure", Guid = Guid.NewGuid().ToString() }
                });
                Assert.NotNull(cache.TryGetLoadedIndex());

                var json = JObject.Parse(new DoctorService(kb, cache, null).Diagnose());

                Assert.Equal("ok", (string)json["status"]);
                Assert.Equal(2, (int)json["result"]!["kb"]!["objectCount"]!);
                Assert.Equal(2, (int)json["result"]!["cache"]!["indexEntries"]!);
            }
            finally
            {
                cache.DeleteOnDiskSnapshot();
                try { if (Directory.Exists(kbPath)) Directory.Delete(kbPath, true); } catch { }
            }
        }

        // Reflection target — DoctorService looks up "BuildMetricsPayload"
        // and invokes with no args. This mirrors OperationTracker's surface
        // without dragging the Gateway dep in.
        private sealed class FakeTracker
        {
            private readonly JObject _payload;
            public FakeTracker(JObject payload) { _payload = payload; }
            public JObject BuildMetricsPayload() => _payload;
        }
    }
}
