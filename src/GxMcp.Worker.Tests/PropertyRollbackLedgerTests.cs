using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// An unconfirmed property rollback leaves the object in a ledger. The state kept for the object is the first one, and a
    /// later write waits until a fresh comparison finds the object at that state. These tests pin the ledger and the gate
    /// decision without a Knowledge Base.
    /// </summary>
    public class PropertyRollbackLedgerTests
    {
        [Fact]
        public void AnObjectNeverRememberedHasNoEntry()
        {
            var ledger = new PropertyRollbackLedger<string>();

            Assert.False(ledger.TryGetEntry("a", out _));
        }

        [Fact]
        public void ASecondUnconfirmedRollbackKeepsTheFirstStateAndItsParts()
        {
            var ledger = new PropertyRollbackLedger<string>();
            ledger.Remember("object", "first", new[] { "Events" });
            ledger.Remember("object", "second", new[] { "Variables" });

            Assert.True(ledger.TryGetEntry("object", out var entry));
            Assert.Equal("first", entry.Original);
            Assert.Equal(new[] { "Events" }, entry.ChangedParts);
        }

        [Fact]
        public void ForgetRemovesTheEntry()
        {
            var ledger = new PropertyRollbackLedger<string>();
            ledger.Remember("object", "first", new string[0]);

            ledger.Forget("object");

            Assert.False(ledger.TryGetEntry("object", out _));
        }

        [Fact]
        public void KeysIgnoreCase()
        {
            var ledger = new PropertyRollbackLedger<string>();
            ledger.Remember("ABC", "first", new string[0]);

            Assert.True(ledger.TryGetEntry("abc", out _));
        }

        [Fact]
        public void TheGateIsOpenWhenTheObjectHasNoEntry()
        {
            Assert.Equal(PropertyWriteGateOutcome.Open, PropertyWriteGate.Decide(false, false));
            Assert.Equal(PropertyWriteGateOutcome.Open, PropertyWriteGate.Decide(false, true));
        }

        [Fact]
        public void TheGateReconcilesAnEntryWhoseObjectIsBackAtTheKeptState()
        {
            Assert.Equal(PropertyWriteGateOutcome.Reconciled, PropertyWriteGate.Decide(true, true));
        }

        [Fact]
        public void TheGateLocksAnEntryWhoseObjectStillDiffers()
        {
            Assert.Equal(PropertyWriteGateOutcome.Locked, PropertyWriteGate.Decide(true, false));
        }
    }
}
