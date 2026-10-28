using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #714: v4.0.4 (#692) refuses a printblock mutation whose cache-invalidated
    /// read-back does not show the change. The <c>genexus_layout</c> help must name the
    /// three refusal codes and the routing facts a caller needs to decide between retrying,
    /// reconciling or stopping: the error carries no persistence fields, add/rename already
    /// retried and may have rewritten Source, delete reads once, and the read may not be fresh.
    /// </summary>
    public class PrintBlockVerificationRefusalHelpTests
    {
        private static string Help() => ToolHelpCatalog.Get("genexus_layout") ?? string.Empty;

        [Theory]
        [InlineData("AddPrintBlockVerificationFailed")]
        [InlineData("RenamePrintBlockVerificationFailed")]
        [InlineData("DeletePrintBlockVerificationFailed")]
        public void Help_NamesEachVerificationRefusalCode(string code)
        {
            Assert.Contains("`" + code + "`", Help(), StringComparison.Ordinal);
        }

        [Fact]
        public void Help_SeparatesTheRefusalEnvelopeFromTheSuccessResult()
        {
            string help = Help();

            Assert.Contains("error envelope does not carry `saved`", help, StringComparison.Ordinal);
            Assert.Contains("the read error is returned instead", help, StringComparison.Ordinal);
            Assert.Contains("On success, `saved=true`", help, StringComparison.Ordinal);
        }

        [Fact]
        public void Help_StatesTheRoutingConsequencesOfARefusal()
        {
            string help = Help();

            Assert.Contains("`delete_printblock` re-reads once and does not retry", help, StringComparison.Ordinal);
            Assert.Contains("may modify the Procedure Source even when the call is refused", help, StringComparison.Ordinal);
            Assert.Contains("does not prove what the store holds", help, StringComparison.Ordinal);
            Assert.Contains("compared case-insensitively", help, StringComparison.Ordinal);
            Assert.Contains("do not attempt a second write automatically", help, StringComparison.Ordinal);
        }
    }
}
