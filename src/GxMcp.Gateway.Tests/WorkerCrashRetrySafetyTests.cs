using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerCrashRetrySafetyTests
    {
        [Theory]
        [InlineData("genexus_read")]
        [InlineData("genexus_query")]
        [InlineData("genexus_inspect")]
        [InlineData("genexus_analyze")]
        [InlineData("genexus_list_objects")]
        [InlineData("genexus_search_source")]
        [InlineData("genexus_whoami")]
        [InlineData("genexus_doctor")]
        public void PureReadTools_AreRetrySafe(string toolName)
        {
            Assert.True(Program.IsRetrySafeOperation(toolName, new JObject()));
        }

        [Theory]
        [InlineData("get_visual")]
        [InlineData("get_indexes")]
        [InlineData("get_logic")]
        [InlineData("check_subtypes")]
        public void GenexusStructure_ReadActions_AreRetrySafe(string action)
        {
            var args = new JObject { ["action"] = action };
            Assert.True(Program.IsRetrySafeOperation("genexus_structure", args));
        }

        [Theory]
        [InlineData("update_visual")]
        [InlineData("create_index")]
        [InlineData("drop_index")]
        [InlineData("set_attribute")]
        [InlineData("remove_attribute")]
        [InlineData("set_level")]
        [InlineData("set_domain")]
        [InlineData("update_group")]
        [InlineData("move_attribute")]
        public void GenexusStructure_MutatingActions_AreNeverRetrySafe(string action)
        {
            var args = new JObject { ["action"] = action };
            Assert.False(Program.IsRetrySafeOperation("genexus_structure", args));
        }

        [Fact]
        public void GenexusStructure_WithoutAction_IsNotRetrySafe()
        {
            Assert.False(Program.IsRetrySafeOperation("genexus_structure", new JObject()));
        }

        [Theory]
        [InlineData("genexus_edit")]
        [InlineData("genexus_delete_object")]
        [InlineData("genexus_create")]
        public void MutatingTools_AreNeverRetrySafe(string toolName)
        {
            Assert.False(Program.IsRetrySafeOperation(toolName, new JObject()));
        }

        [Fact]
        public void ModeDependentMutationsFollowOperationClassifierForRetry()
        {
            Assert.True(Program.IsRetrySafeOperation("genexus_analyze", new JObject
            {
                ["mode"] = "linter",
                ["fix"] = false
            }));
            Assert.False(Program.IsRetrySafeOperation("genexus_analyze", new JObject
            {
                ["mode"] = "linter",
                ["fix"] = true
            }));

            Assert.True(Program.IsRetrySafeOperation("genexus_sdk_probe", new JObject
            {
                ["mode"] = "capabilities"
            }));
            Assert.False(Program.IsRetrySafeOperation("genexus_sdk_probe", new JObject
            {
                ["mode"] = "surface"
            }));
        }

        [Fact]
        public void ActionMutationsFollowOperationClassifierForRetry()
        {
            var args = new JObject { ["action"] = "view" };

            Assert.Equal(OperationClassifier.OperationKind.Mutating,
                OperationClassifier.Describe("genexus_navigation", args).Kind);
            Assert.False(Program.IsRetrySafeOperation("genexus_navigation", args));
        }

        [Fact]
        public void ShouldRetryWorkerCrash_HonorsAttemptAndEnvelope()
        {
            var crashEnvelope = new JObject
            {
                ["error"] = new JObject
                {
                    ["message"] = "Worker process crashed/exited unexpectedly."
                }
            };
            var nonCrashEnvelope = new JObject
            {
                ["error"] = new JObject
                {
                    ["message"] = "Object not found."
                }
            };

            var readArgs = new JObject { ["name"] = "Invoice" };
            var mutatingStructureArgs = new JObject { ["action"] = "set_attribute", ["name"] = "Invoice" };

            // Attempt 1 + crash + read tool => true
            Assert.True(Program.ShouldRetryWorkerCrash(crashEnvelope, "genexus_read", readArgs, 1));

            // Attempt 2 => false (only single retry)
            Assert.False(Program.ShouldRetryWorkerCrash(crashEnvelope, "genexus_read", readArgs, 2));

            // Non-crash error => false
            Assert.False(Program.ShouldRetryWorkerCrash(nonCrashEnvelope, "genexus_read", readArgs, 1));

            // Mutating structure => false
            Assert.False(Program.ShouldRetryWorkerCrash(crashEnvelope, "genexus_structure", mutatingStructureArgs, 1));
        }
    }
}
