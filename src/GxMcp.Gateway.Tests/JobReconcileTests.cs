using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Issue #27 item 1: the async-build background poller is the only thing that
    // flips a JobEntry to terminal, and it can wedge (stale worker pipe, STA
    // serialization, worker recycle) — leaving a finished build stuck "running"
    // forever. The read path now reconciles against the worker's live build-task
    // status via McpRouter.ClassifyWorkerBuildStatus. These cover that classifier
    // (the pure decision) and the resulting JobRegistry.Complete transition.
    public class JobReconcileTests
    {
        [Fact]
        public void StillRunning_ReturnsNull_JobStaysRunning()
        {
            var ws = new JObject { ["status"] = "Running", ["ErrorCount"] = 0 };
            Assert.Null(McpRouter.ClassifyWorkerBuildStatus(ws));
        }

        [Fact]
        public void NullOrTransientError_ReturnsNull()
        {
            Assert.Null(McpRouter.ClassifyWorkerBuildStatus(null));
            Assert.Null(McpRouter.ClassifyWorkerBuildStatus(new JObject { ["error"] = "reconcile timeout" }));
            Assert.Null(McpRouter.ClassifyWorkerBuildStatus(new JObject())); // no status field
        }

        [Fact]
        public void Succeeded_ResolvesSuccess_WithSummary()
        {
            var ws = new JObject { ["status"] = "Succeeded", ["errorCount"] = 0, ["warningCount"] = 3 };
            var v = McpRouter.ClassifyWorkerBuildStatus(ws);
            Assert.NotNull(v);
            Assert.True(v!.Value.success);
            Assert.Contains("succeeded", v.Value.summary);
            Assert.Contains("3 warnings", v.Value.summary);
        }

        [Fact]
        public void Failed_ResolvesFailure_PascalCaseCounts()
        {
            // Worker serializes BuildTaskStatus with PascalCase property names.
            var ws = new JObject { ["Status"] = "Failed", ["ErrorCount"] = 4, ["WarningCount"] = 1 };
            var v = McpRouter.ClassifyWorkerBuildStatus(ws);
            Assert.NotNull(v);
            Assert.False(v!.Value.success);
            Assert.Contains("4 errors", v.Value.summary);
        }

        [Fact]
        public void ReorgRequired_IsTerminalFailureWithExplicitRecoverySummary()
        {
            var ws = new JObject
            {
                ["status"] = "ReorgRequired",
                ["buildMode"] = "BuildAll",
                ["reorgRequired"] = true,
                ["buildAllDone"] = false
            };

            var v = McpRouter.ClassifyWorkerBuildStatus(ws);

            Assert.NotNull(v);
            Assert.False(v!.Value.success);
            Assert.Contains("reorganization", v.Value.summary);
            Assert.Equal("ReorgRequired", v.Value.result["status"]?.ToString());
        }

        [Fact]
        public void BuildAll_SucceededWithoutCompletionEvidence_IsNotSuccessful()
        {
            var worker = new JObject
            {
                ["status"] = "Succeeded",
                ["action"] = "BuildAll",
                ["buildMode"] = "BuildAll",
                ["buildAllDone"] = false,
                ["ExitCode"] = 0,
                ["ErrorCount"] = 0
            };

            var result = McpRouter.ClassifyWorkerBuildStatus(worker);

            Assert.NotNull(result);
            Assert.False(result.Value.success);
        }

        [Fact]
        public void TrackingLost_WorkerNotFound_ResolvesFailureNotBuildError()
        {
            // Worker recycled and dropped its in-memory task map: GetStatus returns
            // {"status":"Error","message":"Task ID not found"}. This is tracking loss,
            // not a build error — resolve terminally with a re-run hint so the job
            // doesn't hang forever.
            var ws = new JObject { ["status"] = "Error", ["message"] = "Task ID not found" };
            var v = McpRouter.ClassifyWorkerBuildStatus(ws);
            Assert.NotNull(v);
            Assert.False(v!.Value.success);
            Assert.Contains("tracking lost", v.Value.summary.ToLowerInvariant());
            Assert.Equal("TrackingLost", v.Value.result["status"]!.ToString());
        }

        [Fact]
        public void RealBuildError_TreatedAsTerminalFailure_NotTrackingLost()
        {
            var ws = new JObject { ["status"] = "Error", ["message"] = "spc0022: something", ["ErrorCount"] = 2 };
            var v = McpRouter.ClassifyWorkerBuildStatus(ws);
            Assert.NotNull(v);
            Assert.False(v!.Value.success);
            Assert.DoesNotContain("tracking lost", v.Value.summary.ToLowerInvariant());
        }

        [Fact]
        public void Reconcile_FlipsRunningJobToTerminal_ViaComplete()
        {
            // End-to-end of the reconcile transition against the real registry:
            // a running job + a Succeeded worker status must resolve to a terminal
            // envelope, closing the "hangs at running forever" symptom.
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            job.WorkerTaskId = "task-123";

            var ws = new JObject { ["status"] = "Succeeded", ["errorCount"] = 0, ["warningCount"] = 0 };
            var v = McpRouter.ClassifyWorkerBuildStatus(ws);
            Assert.NotNull(v);
            registry.Complete(job.Id, v!.Value.success, v.Value.summary, v.Value.result);

            var (env, isErr) = McpRouter.BuildJobResultEnvelope(registry.Get(job.Id)!);
            Assert.False(isErr);
            Assert.Equal("succeeded", env["status"]!.ToString());
        }

        [Fact]
        public void WorkerTaskId_SurvivesSaveLoadRoundTrip()
        {
            var registry = new BackgroundJobRegistry(retentionSeconds: 60);
            var job = registry.Start("sess", "lifecycle/build", 60);
            job.WorkerTaskId = "task-persist";

            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxmcp-jobs-" + job.Id + ".json");
            try
            {
                registry.SaveTo(tmp);
                var reloaded = new BackgroundJobRegistry(retentionSeconds: 60);
                reloaded.LoadFrom(tmp, deleteAfterRead: false);
                Assert.Equal("task-persist", reloaded.Get(job.Id)!.WorkerTaskId);
            }
            finally
            {
                try { System.IO.File.Delete(tmp); } catch { }
            }
        }
    }
}
