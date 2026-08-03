using System;
using System.IO;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #67: PatternInstance dryRun returns ok without exercising the save path
    // and returned Ok even when current pattern read failed in catch block.
    public class PatternDryRunPrecheckTests
    {
        [Fact]
        public void WriteService_DryRunPrecheck_PatternAndVisual_Vetted_ViaConvention()
        {
            DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string servicesDir = null;
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "GxMcp.Worker", "Services");
                if (Directory.Exists(candidate)) { servicesDir = candidate; break; }
                dir = dir.Parent;
            }
            Assert.NotNull(servicesDir);

            string patternSrc = File.ReadAllText(Path.Combine(servicesDir, "WriteService.PatternWrite.cs"));
            string visualSrc = File.ReadAllText(Path.Combine(servicesDir, "WriteService.VisualWrite.cs"));
            string baseWriteSrc = File.ReadAllText(Path.Combine(servicesDir, "WriteService.cs"));

            // 1. PatternWrite catch block returns error code PatternReadFailed, not McpResponse.Ok
            Assert.Contains("code: \"PatternReadFailed\"", patternSrc);
            Assert.DoesNotContain("current pattern read failed (\") + ex.Message + \"). Save skipped.\";\r\n                    AttachReconcileReport(dryResp, reconcileReport);\r\n                    return Models.McpResponse.Ok(target: target, code: \"WriteDryRun\", result: dryResp);", patternSrc);

            // 2. VisualWrite catch block returns error code VisualReadFailed, not McpResponse.Ok
            Assert.Contains("code: \"VisualReadFailed\"", visualSrc);

            // 3. PatternWrite dryRun includes verified array, savePathExercised = false, and PatternInstance warning
            Assert.Contains("[\"verified\"] = new JArray(\"xmlParse\", \"childrenOrderedList\", \"diffVsCurrent\")", patternSrc);
            Assert.Contains("[\"savePathExercised\"] = false", patternSrc);
            Assert.Contains("WorkWithPlus pattern saves can still be rejected by the WWP validator on save", patternSrc);

            // 4. VisualWrite dryRun includes verified array and savePathExercised = false
            Assert.Contains("[\"verified\"] = new JArray(\"xmlParse\", \"layoutGotchas\", \"diffVsCurrent\")", visualSrc);
            Assert.Contains("[\"savePathExercised\"] = false", visualSrc);

            // 5. Generic WriteService dryRun includes verified array and savePathExercised = false
            Assert.Contains("[\"verified\"] = new JArray(\"inputReceived\")", baseWriteSrc);
            Assert.Contains("[\"savePathExercised\"] = false", baseWriteSrc);
        }
    }
}
