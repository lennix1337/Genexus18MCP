using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // The apply_pattern help states the validate verdict that ApplyPatchValidation computes.
    public class ApplyPatchHelpContractTests
    {
        [Fact]
        public void Help_StatesThatValidationIsOkOnlyForASucceededBuild()
        {
            string help = ToolHelpCatalog.Get("genexus_apply_pattern") ?? string.Empty;

            Assert.Contains("`validation.status` is `ok` only when the build ends `Succeeded`", help);
            Assert.Contains("`validation.buildStatus`", help);
            Assert.Contains("`validation.reorgRequired`", help);
            Assert.Contains("marks the apply response as an error", help);
            Assert.Contains("A build that is still `Queued` or `Running` is awaited.", help);
            Assert.Contains("a build that reports no end state, or does not finish within 180 seconds, is `timeout`", help);
        }
    }
}
