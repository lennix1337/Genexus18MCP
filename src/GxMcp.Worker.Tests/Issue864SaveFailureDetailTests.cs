using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #864: a failed variable modify reported only "Web Panel 'X' validation failed."
    public class Issue864SaveFailureDetailTests
    {
        [Fact]
        public void SaveFailureCarriesTheValidatorMessages()
        {
            string message = PersistenceExtensions.ComposeSaveFailureMessage(
                "Web Panel 'WPTeste' validation failed.",
                "src0216: &valor picture ZZZZZZZ9.99 does not fit NUMERIC(12,2)");

            Assert.StartsWith("Web Panel 'WPTeste' validation failed.", message);
            Assert.Contains("Detailed Messages: src0216: &valor picture", message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("validation failed.")]
        public void SaveFailureIsUnchangedWhenValidationAddsNothing(string detail)
        {
            Assert.Equal("Web Panel 'WPTeste' validation failed.",
                PersistenceExtensions.ComposeSaveFailureMessage("Web Panel 'WPTeste' validation failed.", detail));
        }

        [Fact]
        public void ValidationTextThatAlreadyContainsTheSaveMessageIsUsedAlone()
        {
            Assert.Equal("Web Panel 'WPTeste' validation failed. &valor: invalid picture",
                PersistenceExtensions.ComposeSaveFailureMessage(
                    "Web Panel 'WPTeste' validation failed.",
                    "Web Panel 'WPTeste' validation failed. &valor: invalid picture"));
        }

        [Fact]
        public void VariableOwnerSaveAttachesValidationDetail()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");
            int start = source.IndexOf("private static void ForceSaveVariableOwner(");
            Assert.True(start >= 0, "ForceSaveVariableOwner not found");
            int end = source.IndexOf("internal static string RequestedTypeForVerify(", start);
            Assert.True(end > start, "ForceSaveVariableOwner body not delimited");

            Assert.Contains("WithValidationDetail(", source.Substring(start, end - start));
        }
    }
}
