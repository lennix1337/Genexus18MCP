using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The residue of an earlier property write whose rollback was not confirmed. The state before that write is kept for the
    /// object and reported against, but it never refuses a write. A later write reports its preserved parts against that state.
    /// </summary>
    public class PropertyResidueTests
    {
        [Fact]
        public void TheFirstUnconfirmedStateIsTheOneKept()
        {
            var ledger = new PropertyResidueLedger<string>();

            ledger.Remember("obj", "first");
            ledger.Remember("obj", "second");

            Assert.True(ledger.TryGetOriginal("obj", out string original));
            Assert.Equal("first", original);
            Assert.Equal(1, ledger.Count);
        }

        [Fact]
        public void ForgetClearsTheResidueOfAnObject()
        {
            var ledger = new PropertyResidueLedger<string>();
            ledger.Remember("obj", "first");

            ledger.Forget("obj");

            Assert.False(ledger.TryGetOriginal("obj", out _));
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void PreservedPartsAreTheOriginalPartsThatTheResidueDoesNotTouch()
        {
            string[] preserved = PropertyResidue.PreservedNames(new[] { "Documentation", "Events", "Rules", "Variables" }, new[] { "events" });

            Assert.Equal(new[] { "Documentation", "Rules", "Variables" }, preserved);
        }

        [Fact]
        public void TheWarningNamesThePartsThatStillDiffer()
        {
            string warning = PropertyResidue.Warning(new[] { "Events", "Properties: MasterPage" });

            Assert.Contains("Events", warning);
            Assert.Contains("Properties: MasterPage", warning);
        }
    }
}
