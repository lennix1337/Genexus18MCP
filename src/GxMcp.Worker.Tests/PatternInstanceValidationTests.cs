using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // When the pattern's own validation refuses a first apply, the engine generates nothing
    // and returns nothing. The refusal must reach the caller in the pattern's words, not as a
    // generic no-op that suggests another template or a package update.
    public class PatternInstanceValidationTests
    {
        private const string NoTheme =
            "Before applying WorkWithPlus Pattern for the first time, a Theme must be imported (Tools -> WorkWithPlus -> Startup Wizard)";

        [Fact]
        public void SplitErrors_KeepsEachMessageOnceAndDropsBlankLines()
        {
            Assert.Equal(new[] { NoTheme }, PatternInstanceValidation.SplitErrors(NoTheme + "\r\n\r\n" + NoTheme + "\n"));
            Assert.Empty(PatternInstanceValidation.SplitErrors(null));
            Assert.Empty(PatternInstanceValidation.SplitErrors("  \r\n"));
        }

        [Fact]
        public void Collect_IsEmptyWithoutAParentOrDefinition()
        {
            Assert.Empty(PatternInstanceValidation.Collect(null, new object()));
            Assert.Empty(PatternInstanceValidation.Collect(null, null));
        }

        [Fact]
        public void ValidationRecommendation_RepeatsThePatternAndRulesOutRetries()
        {
            var errors = new[] { NoTheme };
            string reason = PatternApplyService.BuildPatternValidationReason("WorkWithPlus", "Customer", errors);
            Assert.Contains("'Customer'", reason);
            Assert.Contains("Startup Wizard", reason);

            string recommendation = PatternApplyService.BuildPatternValidationRecommendation(errors);
            Assert.Contains("Startup Wizard", recommendation);
            Assert.Contains("GeneXus IDE", recommendation);
            Assert.Contains("will not help", recommendation);
            Assert.DoesNotContain("Update the WorkWithPlus pattern package", recommendation);
        }
    }
}
