using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class OperationTrackerTests
    {
        [Fact]
        public void RegisterSpawnSample_ReturnsP50AndP95_AfterEnoughSamples()
        {
            var tracker = new OperationTracker(System.TimeSpan.FromMinutes(5));

            for (int i = 1; i <= 100; i++)
            {
                tracker.RegisterSpawnSample("test-kb", 100.0 + i);
            }

            var (count, p50, p95) = tracker.GetSpawnStats("test-kb");
            Assert.Equal(100, count);
            Assert.InRange(p50, 145, 155);
            Assert.InRange(p95, 190, 200);
        }

        [Fact]
        public void GetSpawnStats_ReturnsZeros_ForUnknownKb()
        {
            var tracker = new OperationTracker(System.TimeSpan.FromMinutes(5));
            var (count, p50, p95) = tracker.GetSpawnStats("never-seen");
            Assert.Equal(0, count);
            Assert.Equal(0, p50);
            Assert.Equal(0, p95);
        }

        // issue #44 regression: the gateway must only relay a worker progress frame while its
        // operation is live. A retired/terminal/unknown token (classically an async build still
        // emitting "Build phase: OpeningKB" after its RPC returned) must NOT be relayed, or the
        // client errors the transport on "progress notification for an unknown token".
        [Fact]
        public void IsProgressTokenActive_TrueWhileRunning_FalseOnceTerminalOrUnknown()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string requestId = "req-progress";
            string opId = tracker.StartOperation(requestId, "genexus_lifecycle", null, "cid");

            // Live operation → progress is relayed.
            Assert.True(tracker.IsProgressTokenActive(opId));

            // Unknown / literal tokens (e.g. the bulk-index constant) are never relayed.
            Assert.False(tracker.IsProgressTokenActive("genexus-mcp-bulk-index"));
            Assert.False(tracker.IsProgressTokenActive(null));
            Assert.False(tracker.IsProgressTokenActive(""));

            // Once the op completes (final response sent, client token retired), stale
            // progress for the same token is dropped.
            tracker.CompleteFromWorker(requestId, new JObject
            {
                ["id"] = requestId,
                ["result"] = new JObject { ["status"] = "Success" }
            });
            Assert.False(tracker.IsProgressTokenActive(opId));
        }

        [Fact]
        public void IsProgressTokenActive_FalseAfterCancel()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string opId = tracker.StartOperation("req-cancel", "genexus_lifecycle", null, "cid");
            Assert.True(tracker.IsProgressTokenActive(opId));
            tracker.MarkCancelled(opId, "cancelled by client");
            Assert.False(tracker.IsProgressTokenActive(opId));
        }

        [Fact]
        public void CancellationRequest_RemainsNonTerminal_UntilWorkerCompletes()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            const string requestId = "req-cancel-requested";
            string opId = tracker.StartOperation(requestId, "genexus_edit", null, "cid");

            Assert.True(tracker.MarkCancellationRequested(opId, "waiting for SDK"));
            Assert.Equal("CancellationRequested", tracker.BuildOperationStatus(opId)["status"]?.ToString());

            tracker.CompleteFromWorker(requestId, new JObject
            {
                ["id"] = requestId,
                ["result"] = new JObject { ["status"] = "ok" }
            });

            Assert.Equal("Completed", tracker.BuildOperationStatus(opId)["status"]?.ToString());
        }

        // BUG-05 regression: on JSON-RPC id reuse within the retention window,
        // StartOperation overwrites _requestToOperation to the new op. CleanupExpired
        // of the OLD op must not then delete the mapping now pointing at the NEW op.
        // The old age-based sweep could also expire a RUNNING op when the thread was
        // descheduled past the tiny 1ms retention — a genuine CI flake. Running ops
        // are now immune to time-based expiry (only completed ops age out).
        [Fact]
        public void CleanupExpired_DoesNotDropMappingForReusedRequestId()
        {
            var tracker = new OperationTracker(TimeSpan.FromMilliseconds(1));
            string requestId = "reused-request-id";

            string op1 = tracker.StartOperation(requestId, "genexus_first", null, "cid1");
            System.Threading.Thread.Sleep(30); // age op1 past the 1ms retention window
            string op2 = tracker.StartOperation(requestId, "genexus_second", null, "cid2");

            tracker.CleanupExpired(); // op1 expired, op2 still fresh

            // The mapping requestId -> op2 must survive so this completion reaches op2.
            tracker.CompleteFromWorker(requestId, new JObject
            {
                ["id"] = requestId,
                ["result"] = new JObject { ["ok"] = true }
            });

            Assert.Equal("Completed", (string)tracker.BuildOperationStatus(op2)["status"]!);
            Assert.NotEqual(op1, op2);
        }

        // Running operations must never be swept by time-based expiry, even when their
        // age exceeds the retention window (tiny retention + thread descheduling used to
        // drop the request->operation mapping mid-flight, making the op NotFound).
        [Fact]
        public void CleanupExpired_DoesNotSweepRunningOperation_PastRetentionWindow()
        {
            var tracker = new OperationTracker(TimeSpan.FromMilliseconds(1));
            string requestId = "long-running-op";
            string opId = tracker.StartOperation(requestId, "genexus_edit", null, "cid");

            System.Threading.Thread.Sleep(30); // far past the 1ms retention
            tracker.CleanupExpired();

            // Still running and still resolvable via its request mapping.
            Assert.Equal("Running", (string)tracker.BuildOperationStatus(opId)["status"]!);
            tracker.CompleteFromWorker(requestId, new JObject
            {
                ["id"] = requestId,
                ["result"] = new JObject { ["status"] = "ok" }
            });
            Assert.Equal("Completed", (string)tracker.BuildOperationStatus(opId)["status"]!);
        }

        [Fact]
        public void Timeout_KeepsOperationLiveAndExposesAmbiguousState()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string requestId = "timeout-request";
            string opId = tracker.StartOperation(requestId, "genexus_edit", null, "cid");

            tracker.MarkTimeout(opId);

            var status = tracker.BuildOperationStatus(opId);
            Assert.Equal("Running", status["status"]?.ToString());
            Assert.True(status["timedOut"]?.ToObject<bool>());
            Assert.True(status["timeoutCount"]?.ToObject<int>() > 0);
            Assert.Contains("may still be finishing", status["hint"]?.ToString());

            tracker.CompleteFromWorker(requestId, new JObject
            {
                ["id"] = requestId,
                ["result"] = new JObject { ["status"] = "ok" }
            });

            Assert.Equal("Completed", tracker.BuildOperationStatus(opId)["status"]?.ToString());
        }

        [Fact]
        public async Task WaitForOperationAsync_WaitsForAProgressChange()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string requestId = "wait-progress-request";
            string operationId = tracker.StartOperation(requestId, "genexus_edit", null, "cid");

            Task<JObject> waiting = tracker.WaitForOperationAsync(operationId, 2, "change");
            await Task.Delay(100);
            tracker.TouchProgress(operationId, "Saving", "worker progress");

            JObject payload = await waiting;
            Assert.Equal("Saving", payload["phase"]?.ToString());
            Assert.True(payload["waitSatisfied"]?.ToObject<bool>());
            Assert.Equal("change", payload["waitUntil"]?.ToString());
        }

        [Fact]
        public async Task WaitForOperationAsync_TerminalModeDoesNotReturnOnProgressOnly()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string requestId = "wait-terminal-request";
            string operationId = tracker.StartOperation(requestId, "genexus_edit", null, "cid");

            Task<JObject> waiting = tracker.WaitForOperationAsync(operationId, 1, "terminal");
            await Task.Delay(100);
            tracker.TouchProgress(operationId, "Saving", "still running");

            Task completedProbe = await Task.WhenAny(waiting, Task.Delay(250));
            Assert.NotSame(waiting, completedProbe);
            Assert.False(waiting.IsCompleted);

            tracker.CompleteFromWorker(requestId, new JObject
            {
                ["id"] = requestId,
                ["result"] = new JObject { ["status"] = "ok" }
            });
            JObject payload = await waiting;
            Assert.Equal("Completed", payload["status"]?.ToString());
            Assert.Equal("terminal", payload["waitUntil"]?.ToString());
        }

        [Fact]
        public void ToolStats_ExposeCacheHitsAndErrorCodes()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            tracker.RecordCacheHit("genexus_query");
            string requestId = "error-metric-request";
            tracker.StartOperation(requestId, "genexus_query", null, "cid");
            tracker.CompleteFromWorker(requestId, new JObject
            {
                ["id"] = requestId,
                ["error"] = new JObject { ["code"] = "IndexCold", ["message"] = "cold" }
            });

            var tools = (JObject)tracker.BuildToolStatsBlock()["tools"]!;
            var stats = (JObject)tools["genexus_query"]!;
            Assert.Equal(1L, stats["cacheHits"]!.ToObject<long>());
            Assert.Equal(1L, stats["errorsByCode"]!["IndexCold"]!.ToObject<long>());
        }

        // Completed operations DO age out after the retention window (memory bound).
        [Fact]
        public void CleanupExpired_SweepsCompletedOperation_PastRetentionWindow()
        {
            var tracker = new OperationTracker(TimeSpan.FromMilliseconds(1));
            string requestId = "completed-op";
            string opId = tracker.StartOperation(requestId, "genexus_read", null, "cid");
            tracker.CompleteFromWorker(requestId, new JObject
            {
                ["id"] = requestId,
                ["result"] = new JObject { ["ok"] = true }
            });

            System.Threading.Thread.Sleep(30); // past the 1ms retention
            tracker.CleanupExpired();

            Assert.Equal("NotFound", (string)tracker.BuildOperationStatus(opId)["status"]!);
        }

        // Regression: a worker-crash retry drives both MarkFailedByRequest (the crash) and,
        // via LinkRequest, CompleteFromWorker (the successful retry) against ONE operation.
        // Final status must be Completed, and the tool must be metric-counted exactly once
        // (count == 1) — not twice — so whoami stats stay trustworthy.
        [Fact]
        public void CrashThenRetrySuccess_UpdatesStatus_ButCountsMetricOnce()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string firstId = "req-attempt-1";
            string opId = tracker.StartOperation(firstId, "genexus_read", null, "cid");

            // Attempt 1 crashes (OnWorkerExited path).
            tracker.MarkFailedByRequest(firstId, "Worker crashed/exited.");

            // Retry under a fresh request id, linked back to the same operation.
            string retryId = "req-attempt-2";
            tracker.LinkRequest(retryId, opId);
            tracker.CompleteFromWorker(retryId, new JObject
            {
                ["id"] = retryId,
                ["result"] = new JObject { ["status"] = "Success" }
            });

            Assert.Equal("Completed", (string)tracker.BuildOperationStatus(opId)["status"]!);

            var tools = (JObject)tracker.BuildToolStatsBlock()["tools"]!;
            var readStats = (JObject)tools["genexus_read"]!;
            Assert.Equal(1, (long)readStats["count"]!);   // one logical call, not two
            Assert.Equal(1, (long)readStats["errorCount"]!); // the crash, counted once
        }

        [Fact]
        public void CompleteFromWorker_ShouldHandleArrayResultPayload()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string requestId = Guid.NewGuid().ToString("N");
            string operationId = tracker.StartOperation(
                requestId,
                "genexus_list_objects",
                new JObject { ["limit"] = 20 },
                Guid.NewGuid().ToString("N"));

            var workerPayload = new JObject
            {
                ["id"] = requestId,
                ["result"] = new JArray(
                    new JObject
                    {
                        ["name"] = "ACADEMICOS",
                        ["type"] = "Folder"
                    })
            };

            tracker.CompleteFromWorker(requestId, workerPayload);
            JObject status = tracker.BuildOperationStatus(operationId);

            Assert.Equal("Completed", status["status"]?.ToString());
            Assert.False(status["timedOut"]?.Value<bool>() ?? true);
        }
        [Fact]
        public void BuildWorkerRpcRequest_IncludesMetaProgressToken_WhenOperationIdPresent()
        {
            var workerCommand = JObject.Parse(@"{
                ""module"": ""Build"",
                ""action"": ""Build"",
                ""target"": ""InvoiceProc""
            }");

            var method = typeof(GxMcp.Gateway.Program).GetMethod(
                "BuildWorkerRpcRequest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.NotNull(method);

            // Signature: BuildWorkerRpcRequest(JObject workerCommand, string requestId, string operationId, string sessionId)
            var built = (JObject)method!.Invoke(null, new object?[] { workerCommand, "req-1", "op-xyz", null })!;

            Assert.Equal("op-xyz", built["_meta"]?["progressToken"]?.ToString());
            Assert.Equal("Build", built["method"]?.ToString());
        }

        // Wave-3 item 94 — heatmap block surfaces totalMs / percentOfSession / lastUsedAt.
        [Fact]
        public void BuildHeatmapBlock_RanksToolsByTotalMs_AndComputesPercent()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            tracker.RecordSyntheticCompletion("genexus_read", 100, isError: false);
            tracker.RecordSyntheticCompletion("genexus_read", 200, isError: false);
            tracker.RecordSyntheticCompletion("genexus_edit", 50, isError: false);

            var heat = tracker.BuildHeatmapBlock();

            Assert.Equal(2, heat.Count);
            Assert.Equal("genexus_read", heat[0]["tool"]?.ToString()); // higher totalMs first
            Assert.True(heat[0]["totalMs"]!.ToObject<long>() >= 300);
            Assert.True(heat[0]["percentOfSession"]!.ToObject<double>() > heat[1]["percentOfSession"]!.ToObject<double>());
            Assert.NotNull(heat[0]["lastUsedAt"]);
        }

        [Fact]
        public void BuildHeatmapBlock_Empty_WhenNoCompletionsRecorded()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            var heat = tracker.BuildHeatmapBlock();
            Assert.Empty(heat);
        }

        // Wave-3 item 36 — execution history filtered by target.
        [Fact]
        public void BuildExecutionHistory_FiltersByTarget_AndClampsLast()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 60; i++)
            {
                tracker.RecordSyntheticCompletion("genexus_read", 10, isError: false,
                    new JObject { ["target"] = "InvoiceProc" });
            }
            tracker.RecordSyntheticCompletion("genexus_read", 10, isError: false,
                new JObject { ["target"] = "OtherObj" });

            JObject result = tracker.BuildExecutionHistory("InvoiceProc", last: 100);
            var runs = (JArray)result["runs"]!;
            Assert.Equal(50, runs.Count); // clamped to 50
            Assert.Equal(60, result["totalMatches"]?.ToObject<int>());
            foreach (var run in runs)
            {
                Assert.Equal("InvoiceProc", run["params"]?["target"]?.ToString());
                Assert.NotNull(run["outcome"]);
                Assert.NotNull(run["durationMs"]);
            }
        }

        [Fact]
        public void BuildExecutionHistory_ReturnsEmptyRuns_WhenTargetUnknown()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            tracker.RecordSyntheticCompletion("genexus_read", 10, isError: false,
                new JObject { ["target"] = "Existing" });

            JObject result = tracker.BuildExecutionHistory("Missing", last: 10);
            Assert.Empty((JArray)result["runs"]!);
            Assert.Equal(0, result["totalMatches"]?.ToObject<int>());
        }

        // Wave-3 item 35 — watch_event filters by target + event name in payload.
        [Fact]
        public void BuildWatchEvent_FiltersByTargetAndToolWhitelist()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            // matching tool + target + event substring in args
            tracker.RecordSyntheticCompletion("genexus_edit", 10, isError: false,
                new JObject { ["target"] = "InvoicePanel", ["body"] = "set OnClick = 'MyEvent'" });
            // matching tool but wrong target
            tracker.RecordSyntheticCompletion("genexus_edit", 10, isError: false,
                new JObject { ["target"] = "OtherPanel", ["body"] = "MyEvent" });
            // matching target but non-event tool
            tracker.RecordSyntheticCompletion("genexus_read", 10, isError: false,
                new JObject { ["target"] = "InvoicePanel", ["body"] = "MyEvent" });

            JObject result = tracker.BuildWatchEvent("InvoicePanel", "MyEvent", last: 50);
            var runs = (JArray)result["runs"]!;
            Assert.Single(runs);
            Assert.Equal("genexus_edit", runs[0]["tool"]?.ToString());
            Assert.Equal("MyEvent", result["event"]?.ToString());
        }

        [Fact]
        public void BuildWatchEvent_NoMatch_ReturnsEmptyRuns()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            tracker.RecordSyntheticCompletion("genexus_edit", 10, isError: false,
                new JObject { ["target"] = "Panel" });
            JObject result = tracker.BuildWatchEvent("Panel", "NotPresentEvent", last: 10);
            Assert.Empty((JArray)result["runs"]!);
            Assert.Equal(0, result["totalMatches"]?.ToObject<int>());
        }

        [Fact]
        public void BuildWatchEvent_ClampsLastTo50()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 80; i++)
            {
                tracker.RecordSyntheticCompletion("genexus_edit", 5, isError: false,
                    new JObject { ["target"] = "Panel", ["body"] = "tick" });
            }
            JObject result = tracker.BuildWatchEvent("Panel", "tick", last: 100);
            Assert.Equal(50, ((JArray)result["runs"]!).Count);
            Assert.Equal(80, result["totalMatches"]?.ToObject<int>());
        }
    }
}

