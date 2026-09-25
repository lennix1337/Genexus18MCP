using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class LifecycleWarningDeltaTests
    {
        [Fact]
        public void StatusWarningCursor_ReturnsOnlyNewWarnings_ResultKeepsFullList()
        {
            var service = new BuildService();
            string taskId = "delta" + Guid.NewGuid().ToString("N").Substring(0, 3);
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = taskId,
                Action = "Build",
                Target = "Object",
                Status = "Running",
                Phase = "Specifying",
                Warnings = new List<string> { "warning-1", "warning-2", "warning-3" },
                WarningCount = 3
            };
            BuildService.InjectBuildTaskForTest(taskId, status);
            try
            {
                // The first status is itself a delta: the full warning list is reserved
                // for action=result, even when the caller has not supplied a cursor yet.
                string initialJson = service.GetStatusWait(taskId, 0, sinceBaseline: null);
                var initialPayload = JObject.Parse(initialJson);
                Assert.Equal(new[] { "warning-1", "warning-2", "warning-3" },
                    initialPayload["newWarnings"]!.Values<string>().ToArray());
                Assert.Empty((JArray)initialPayload["warnings"]!);
                Assert.Null(initialPayload["Warnings"]);
                Assert.Equal(3, initialPayload["_meta"]!["warningCursor"]!.ToObject<int>());

                string statusJson = service.GetStatusWait(taskId, 0, "warnings:2");
                var statusPayload = JObject.Parse(statusJson);
                Assert.Equal(new[] { "warning-3" },
                    statusPayload["newWarnings"]!.Values<string>().ToArray());
                Assert.Empty((JArray)statusPayload["warnings"]!);
                Assert.Equal(3, statusPayload["warningCount"]!.ToObject<int>());
                Assert.Equal(3, statusPayload["_meta"]!["warningCursor"]!.ToObject<int>());

                string resultJson = service.GetResult(taskId);
                var resultPayload = JObject.Parse(resultJson);
                Assert.Equal(new[] { "warning-1", "warning-2", "warning-3" },
                    resultPayload["Warnings"]!.Values<string>().ToArray());
            }
            finally
            {
                BuildService.RemoveBuildTaskForTest(taskId);
            }
        }

        [Fact]
        public void StatusWarningDelta_CompactDoesNotRepeatFullWarningSurface()
        {
            var service = new BuildService();
            string taskId = "compact" + Guid.NewGuid().ToString("N").Substring(0, 3);
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = taskId,
                Action = "Build",
                Status = "Running",
                Phase = "Specifying",
                Warnings = new List<string> { "warning-1", "warning-2" },
                WarningCount = 2
            };
            BuildService.InjectBuildTaskForTest(taskId, status);
            try
            {
                var payload = JObject.Parse(service.GetStatusWait(taskId, 0, "warnings:1", compact: true));
                Assert.Equal(new[] { "warning-2" }, payload["newWarnings"]!.Values<string>().ToArray());
                Assert.Empty((JArray)payload["warnings"]!);
                Assert.Null(payload["warningsAggregated"]);
            }
            finally
            {
                BuildService.RemoveBuildTaskForTest(taskId);
            }
        }

        [Fact]
        public async Task StatusWait_TerminalModeDoesNotReturnOnWarningOnlyChange()
        {
            var service = new BuildService();
            string taskId = "term" + Guid.NewGuid().ToString("N").Substring(0, 3);
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = taskId,
                Action = "Build",
                Status = "Running",
                Phase = "Specifying",
                StartedAt = DateTime.UtcNow
            };
            BuildService.InjectBuildTaskForTest(taskId, status);
            try
            {
                string baseline = status.ComputeBaseline();
                var wait = Task.Run(() => service.GetStatusWait(taskId, 1, baseline, until: "terminal"));
                await Task.Delay(100);
                status.WarningCount++;
                status.Warnings.Add("new-warning");
                status.StateChangeSignal.Set();
                Assert.NotSame(wait, await Task.WhenAny(wait, Task.Delay(200)));
                status.Status = "Succeeded";
                status.Phase = "Done";
                status.StateChangeSignal.Set();
                Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(2000)));
                await wait;
            }
            finally
            {
                BuildService.RemoveBuildTaskForTest(taskId);
            }
        }
    }
}
