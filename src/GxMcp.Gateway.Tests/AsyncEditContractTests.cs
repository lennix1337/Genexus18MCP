using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class AsyncEditContractTests
    {
        [Fact]
        public void BuildAsyncEditAcceptedPayload_ReturnsLifecycleFriendlyFields()
        {
            var job = new JobEntry
            {
                Id = "abc123",
                EstimatedSeconds = 45
            };

            JObject payload = Program.BuildAsyncEditAcceptedPayload(job);

            Assert.Equal("abc123", payload["job_id"]?.ToString());
            Assert.Equal("abc123", payload["operationId"]?.ToString());
            Assert.Equal("running", payload["status"]?.ToString());
            Assert.Equal(45, payload["estimated_seconds"]?.ToObject<int>());
            Assert.Equal("op:abc123", payload["pollTarget"]?.ToString());
            Assert.Contains("genexus_lifecycle", payload["hint"]?.ToString());
        }

        [Fact]
        public void BuildAsyncLifecycleAcceptedPayload_ReturnsLifecycleFriendlyFields()
        {
            var job = new JobEntry
            {
                Id = "build123",
                EstimatedSeconds = 60
            };

            JObject payload = Program.BuildAsyncLifecycleAcceptedPayload(job, "build");

            Assert.Equal("build123", payload["job_id"]?.ToString());
            Assert.Equal("build123", payload["operationId"]?.ToString());
            Assert.Equal("running", payload["status"]?.ToString());
            Assert.Equal(60, payload["estimated_seconds"]?.ToObject<int>());
            Assert.Equal("op:build123", payload["pollTarget"]?.ToString());
            Assert.Contains("Build accepted;", payload["hint"]?.ToString());
        }

        [Fact]
        public void IsAsyncMutationTool_AcceptsCanonicalAndLegacyVariableNames()
        {
            Assert.True(Program.IsAsyncMutationTool("genexus_edit"));
            Assert.True(Program.IsAsyncMutationTool("genexus_variable"));
            Assert.True(Program.IsAsyncMutationTool("genexus_add_variable"));
            Assert.True(Program.IsAsyncMutationTool("genexus_delete_variable"));
            Assert.True(Program.IsAsyncMutationTool("genexus_modify_variable"));
            Assert.False(Program.IsAsyncMutationTool("genexus_read"));
        }

        [Fact]
        public void ShouldRunMutationAsync_DryRunNeverBecomesBackgroundWrite()
        {
            var args = new JObject
            {
                ["async"] = true,
                ["dryRun"] = true
            };

            Assert.False(Program.ShouldRunMutationAsync("genexus_edit", args));
            Assert.False(Program.ShouldRunMutationAsync("genexus_variable", args));
        }

        [Fact]
        public void ShouldRunMutationAsync_ValidateOnlyNeverBecomesBackgroundWrite()
        {
            Assert.False(Program.ShouldRunMutationAsync("genexus_edit", new JObject
            {
                ["async"] = true,
                ["validate"] = "only"
            }));
        }

        [Fact]
        public void ChangeSetPreviewAndValidateRemainReadOnlyForRecoveryGates()
        {
            Assert.True(Program.IsMutationPreview(new JObject
            {
                ["changeSet"] = new JObject { ["action"] = "preview" }
            }));
            Assert.True(Program.IsMutationPreview(new JObject
            {
                ["changeSet"] = new JObject { ["action"] = "validate" }
            }));
            Assert.False(Program.IsMutationPreview(new JObject
            {
                ["changeSet"] = new JObject { ["action"] = "apply" }
            }));
        }

        [Fact]
        public void ShouldRunMutationAsync_RealEditRemainsAsyncWhenRequested()
        {
            Assert.True(Program.ShouldRunMutationAsync("genexus_edit", new JObject
            {
                ["async"] = true,
                ["dryRun"] = false
            }));
        }

        [Fact]
        public void BuildWorkerRpcRequest_UsesTheLifecycleOperationIdentity()
        {
            JObject rpc = Program.BuildWorkerRpcRequest(
                new JObject { ["module"] = "Write", ["action"] = "Source", ["target"] = "SyntheticProcedure" },
                requestId: "transport-request",
                operationId: "one-operation-id");

            Assert.Equal("transport-request", rpc["id"]?.ToString());
            Assert.Equal("one-operation-id", rpc["_meta"]?["progressToken"]?.ToString());
        }

        [Fact]
        public void BuildAsyncMutationCompletionSummary_UsesVariableSpecificLabel()
        {
            Assert.Equal("Variable update succeeded", Program.BuildAsyncMutationCompletionSummary("genexus_variable", success: true));
            Assert.Equal("Variable update failed", Program.BuildAsyncMutationCompletionSummary("genexus_modify_variable", success: false));
            Assert.Equal("Edit succeeded", Program.BuildAsyncMutationCompletionSummary("genexus_edit", success: true));
        }

        [Fact]
        public void NormalizeEditAndBuildPayload_AddsPollTargetFromTaskId()
        {
            var payload = new JObject
            {
                ["build"] = new JObject
                {
                    ["status"] = "Accepted",
                    ["taskId"] = "T-123"
                }
            };

            Program.NormalizeEditAndBuildPayload(payload);

            Assert.Equal("T-123", payload["build"]?["pollTarget"]?.ToString());
            Assert.Contains("T-123", payload["build"]?["hint"]?.ToString());
        }

        [Fact]
        public void IsSuccessfulBackgroundToolCompletion_RejectsRunningInnerResult()
        {
            var workerEnvelope = new JObject
            {
                ["result"] = new JObject
                {
                    ["status"] = "Running"
                }
            };

            Assert.False(Program.IsSuccessfulBackgroundToolCompletion(workerEnvelope));
        }

        [Fact]
        public void IsSuccessfulBackgroundToolCompletion_AcceptsSuccessfulInnerResult()
        {
            var workerEnvelope = new JObject
            {
                ["result"] = new JObject
                {
                    ["status"] = "Success",
                    ["details"] = "ok"
                }
            };

            Assert.True(Program.IsSuccessfulBackgroundToolCompletion(workerEnvelope));
        }

        [Fact]
        public void IsSuccessfulBackgroundToolCompletion_RejectsProtocolErrorEnvelope()
        {
            var workerEnvelope = new JObject
            {
                ["error"] = new JObject
                {
                    ["code"] = -32603,
                    ["message"] = "boom"
                }
            };

            Assert.False(Program.IsSuccessfulBackgroundToolCompletion(workerEnvelope));
        }
    }
}
