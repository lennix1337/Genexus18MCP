using System;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class LifecycleOperationTests
    {
        [Fact]
        public void NormalizedLifecycleKey_IgnoresTargetOrderAndWaitFields()
        {
            var first = Program.BuildLifecycleRequestKey("specify", new JObject
            {
                ["target"] = "Beta, Alpha",
                ["wait_until_done"] = true,
                ["wait_seconds"] = 300
            });
            var second = Program.BuildLifecycleRequestKey("SPECIFY", new JObject
            {
                ["target"] = "alpha;beta",
                ["wait_until_done"] = false
            });

            Assert.Equal(first, second);
        }

        [Fact]
        public async Task LifecycleAdmission_IsFifoAndCoalescesEquivalentRequests()
        {
            var registry = new BackgroundJobRegistry(600);
            var fence = new OwnershipFence("session-a", "kb-a", 1);
            var first = registry.AdmitLifecycle("session-a", "lifecycle/specify", 60,
                fence, "worker-a", "specify|A");
            var second = registry.AdmitLifecycle("session-b", "lifecycle/specify", 60,
                fence, "worker-a", "specify|B");
            var duplicate = registry.AdmitLifecycle("session-c", "lifecycle/specify", 60,
                fence, "worker-a", "specify|A");

            Assert.Equal("running", first.Job.Status);
            Assert.Equal("queued", second.Job.Status);
            Assert.Equal(1, second.Job.QueuePosition);
            Assert.True(duplicate.Coalesced);
            Assert.Same(first.Job, duplicate.Job);
            var admissionProbe = registry.WaitForLifecycleAdmissionAsync(second.Job.Id);
            var probe = await Task.WhenAny(admissionProbe, Task.Delay(50));
            Assert.NotSame(admissionProbe, probe);

            registry.Complete(first.Job.Id, true, "done");
            Assert.True(await registry.WaitForLifecycleAdmissionAsync(second.Job.Id).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("running", second.Job.Status);
            Assert.True(second.Job.QueuedMs >= 0);
            registry.Cancel(second.Job.Id, "test cleanup");
        }

        [Fact]
        public async Task CancelledQueuedAdmissionDoesNotBecomeDispatchable()
        {
            var registry = new BackgroundJobRegistry(600);
            var fence = new OwnershipFence("session", "kb", 1);
            var active = registry.AdmitLifecycle("session", "lifecycle/build", 60,
                fence, "worker", "A");
            var queued = registry.AdmitLifecycle("session", "lifecycle/build", 60,
                fence, "worker", "B");

            registry.Cancel(queued.Job.Id, "cancel before admission");

            Assert.False(await registry.WaitForLifecycleAdmissionAsync(queued.Job.Id)
                .WaitAsync(TimeSpan.FromSeconds(1)));
            registry.Complete(active.Job.Id, true, "cleanup");
        }

        [Fact]
        public async Task GatewayMetricsStatusTargetIsNotTreatedAsMalformedOperationId()
        {
            var response = await Program.ProcessMcpRequest(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "gateway-metrics",
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_lifecycle",
                    ["arguments"] = new JObject
                    {
                        ["action"] = "status",
                        ["target"] = "gateway:metrics"
                    }
                }
            }, "metrics-" + Guid.NewGuid().ToString("N"));

            Assert.Null(response?["error"]);
            var payload = JObject.Parse(response!["result"]!["content"]![0]!["text"]!.Value<string>()!);
            Assert.True(string.Equals("Success", payload["status"]?.ToString(), StringComparison.OrdinalIgnoreCase), payload.ToString());
            Assert.NotNull(payload["tools"]);
        }

        [Fact]
        public void AliasResolver_ResolvesGatewayAndWorkerIdsThroughOnePath()
        {
            var registry = new BackgroundJobRegistry(600);
            var job = registry.Start("session", "lifecycle/build", 60);
            registry.SetWorkerTaskId(job.Id, "abcd1234");

            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string trackerId = tracker.StartOperation("request", "genexus_edit", null, "cid");

            var workerAlias = LifecycleOperationAliasResolver.Resolve("op:abcd1234", registry, tracker);
            Assert.Equal(LifecycleAliasKind.Job, workerAlias.Kind);
            Assert.Equal(job.Id, workerAlias.Job!.Id);

            var gatewayAlias = LifecycleOperationAliasResolver.Resolve(job.Id, registry, tracker);
            Assert.Equal(LifecycleAliasKind.Job, gatewayAlias.Kind);

            var trackerAlias = LifecycleOperationAliasResolver.Resolve("op:" + trackerId, registry, tracker);
            Assert.Equal(LifecycleAliasKind.Tracker, trackerAlias.Kind);
            Assert.Equal(trackerId, trackerAlias.TrackerOperationId);

            var malformed = LifecycleOperationAliasResolver.Resolve("op:not-an-id", registry, tracker);
            Assert.True(malformed.IsMalformed);
        }

        [Fact]
        public async Task LongPollJob_QueuedOperationCanBeAwaitedUntilTerminal()
        {
            var registry = new BackgroundJobRegistry(600);
            var fence = new OwnershipFence("session", "kb", 1);
            var first = registry.AdmitLifecycle("session", "lifecycle/build", 60, fence, "worker", "A");
            var second = registry.AdmitLifecycle("session", "lifecycle/build", 60, fence, "worker", "B");

            var waiting = McpRouter.LongPollJob(registry, second.Job.Id, 2, until: "terminal");
            registry.Complete(first.Job.Id, true, "first");
            var secondAdmission = registry.WaitForLifecycleAdmissionAsync(second.Job.Id);
            var promoted = await Task.WhenAny(secondAdmission, Task.Delay(1000));
            Assert.Same(secondAdmission, promoted);
            registry.Complete(second.Job.Id, true, "second");
            var payload = await waiting;

            Assert.Equal("succeeded", payload["status"]?.ToString());
            Assert.Equal(second.Job.Id, payload["operationId"]?.ToString());
        }
    }
}
