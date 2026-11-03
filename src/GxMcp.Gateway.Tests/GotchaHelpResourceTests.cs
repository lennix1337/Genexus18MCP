using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Friction 2026-05-22 #62: warnings carry docUrl=genexus://kb/tool-help/gotchas/<code>.
    // ToolHelpCatalog.GetGotchaHelp must return non-empty markdown for every code the
    // worker emits, and a stub for unknown codes so the agent always gets a payload.
    public class GotchaHelpResourceTests
    {
        [Theory]
        [InlineData("LintKbCharsetLossy")]
        [InlineData("LintSpc0150ForEachAttributeWrite")]
        [InlineData("GotchaGxButtonHtmlFormCustomEvent")]
        [InlineData("GotchaGxAttributeHtmlFormDiscreteReadOnly")]
        [InlineData("GotchaGxAttributeMissingDataField")]
        [InlineData("GotchaUnknownControlType")]
        [InlineData("GotchaWebComponentMissingObjectCall")]
        [InlineData("GotchaHtmlFormatScriptStripped")]
        [InlineData("GotchaCellOutsideTable")]
        [InlineData("GotchaDuplicateControlName")]
        [InlineData("GotchaGridConditionIgnored")]
        [InlineData("GotchaIdeObjectOpenInEditor")]
        [InlineData("GotchaIdeActiveOnKb")]
        public void EveryRegisteredCode_HasNonEmptyMarkdown(string code)
        {
            string text = ToolHelpCatalog.GetGotchaHelp(code);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("#", text); // markdown heading present
            Assert.Contains(code, text); // body mentions the code so the agent can grep
        }

        [Fact]
        public void UnknownCode_ReturnsGenericStub_NotNull()
        {
            // Defensive: even if a new emit site lands without a doc, the resource
            // must respond so the agent's resources/read call doesn't fail.
            string text = ToolHelpCatalog.GetGotchaHelp("GotchaSomethingNobodyDocumentedYet");
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("GotchaSomethingNobodyDocumentedYet", text);
        }
    }
}
