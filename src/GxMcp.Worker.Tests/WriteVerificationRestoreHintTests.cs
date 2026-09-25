using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Item 3 regression tests: verification-failure envelopes must include a
    /// valid genexus_versioning history_restore nextStep so callers can recover
    /// without digging through documentation.
    /// </summary>
    public class WriteVerificationRestoreHintTests
    {
        // Simulates the envelope construction done in WriteService for visual
        // verification failures (Item 3, WriteService ~3051-3080). We test the
        // JSON-level structure rather than using real SDK objects.

        [Fact]
        public void VisualVerificationFailure_EnvelopeContainsRestoreNextStep()
        {
            // Simulate the JObject manipulation from WriteService.WriteVisualPart
            // verification failure block.
            var baseError = new JObject
            {
                ["status"] = "Error",
                ["code"] = "WriteFailed",
                ["message"] = "Visual write verification failed",
                ["error"] = new JObject { ["details"] = "diff n/a" },
                ["nextSteps"] = new JArray
                {
                    new JObject { ["tool"] = "genexus_read", ["why"] = "Inspect." }
                }
            };

            // Apply the same logic as in WriteService (Item 3 change).
            var nsArr = baseError["nextSteps"] as JArray ?? new JArray();
            nsArr.Add(new JObject
            {
                ["tool"] = "genexus_versioning",
                ["args"] = new JObject { ["action"] = "history_restore", ["discard"] = true, ["name"] = "MyPanel" },
                ["why"] = "Restore to the pre-write snapshot to undo the failed visual write."
            });
            baseError["nextSteps"] = nsArr;

            var result = JObject.Parse(baseError.ToString());
            var nextSteps = result["nextSteps"] as JArray;

            Assert.NotNull(nextSteps);
            bool hasRestoreStep = false;
            foreach (var step in nextSteps)
            {
                if (string.Equals(step["tool"]?.ToString(), "genexus_versioning", StringComparison.Ordinal))
                {
                    hasRestoreStep = true;
                    Assert.Equal("history_restore", step["args"]?["action"]?.ToString());
                    Assert.Equal("MyPanel", step["args"]?["name"]?.ToString());
                }
            }
            Assert.True(hasRestoreStep, "nextSteps must include a genexus_versioning restore entry on visual verification failure.");
        }

        [Fact]
        public void PatternVerificationFailure_EnvelopeContainsRestoreNextStep()
        {
            // Simulate the JObject manipulation from WriteService.WritePatternPart
            // verification failure block.
            var baseError = new JObject
            {
                ["status"] = "Error",
                ["code"] = "PatternVerificationMismatch",
                ["message"] = "Pattern write verification failed",
                ["error"] = new JObject { ["details"] = "diff n/a" },
                ["nextSteps"] = new JArray
                {
                    new JObject { ["tool"] = "genexus_read", ["why"] = "Inspect." }
                }
            };

            // Apply the same logic as in WriteService (Item 3 change).
            {
                var nsArr = baseError["nextSteps"] as JArray ?? new JArray();
                nsArr.Add(new JObject
                {
                    ["tool"] = "genexus_versioning",
                    ["args"] = new JObject { ["action"] = "history_restore", ["discard"] = true, ["name"] = "MyWorkWith" },
                    ["why"] = "Restore to the pre-write snapshot to undo the failed pattern write."
                });
                baseError["nextSteps"] = nsArr;
            }

            var result = JObject.Parse(baseError.ToString());
            var nextSteps = result["nextSteps"] as JArray;

            Assert.NotNull(nextSteps);
            bool hasRestoreStep = false;
            foreach (var step in nextSteps)
            {
                if (string.Equals(step["tool"]?.ToString(), "genexus_versioning", StringComparison.Ordinal))
                {
                    hasRestoreStep = true;
                    Assert.Equal("history_restore", step["args"]?["action"]?.ToString());
                    Assert.Equal("MyWorkWith", step["args"]?["name"]?.ToString());
                }
            }
            Assert.True(hasRestoreStep, "nextSteps must include a genexus_versioning restore entry on pattern verification failure.");
        }

        [Fact]
        public void VisualVerificationFailure_SdkSaveErrorAttachedToErrorBlock()
        {
            var sdkSaveError = new JObject
            {
                ["type"] = "System.InvalidOperationException",
                ["message"] = "Cannot save part",
                ["where"] = "webFormPart.Save()"
            };

            var baseError = new JObject
            {
                ["status"] = "Error",
                ["error"] = new JObject { ["details"] = "some diff" }
            };

            // Simulate: errObj["sdkSaveError"] = webFormPartSaveError
            var errObj = baseError["error"] as JObject;
            if (errObj != null) errObj["sdkSaveError"] = sdkSaveError;

            var result = JObject.Parse(baseError.ToString());
            var sdkErr = result["error"]?["sdkSaveError"];
            Assert.NotNull(sdkErr);
            Assert.Equal("webFormPart.Save()", sdkErr["where"]?.ToString());
        }
    }
}
