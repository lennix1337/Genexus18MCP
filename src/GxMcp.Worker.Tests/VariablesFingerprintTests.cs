using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The text of the variables that a property write and its rollback compare: one line per variable, with its declaration and its
    /// description, in name order. The SDK reader needs a Knowledge Base, so these tests give the capture a fake reader and check the
    /// production text it builds. A reader that fails gives no text at all, which the comparison leaves out rather than reports.
    /// </summary>
    public class VariablesFingerprintTests
    {
        private static string Fingerprint(params VariableFact[] variables)
            => VariablesFingerprint.Capture(null, _ => variables);

        [Fact]
        public void EachVariableIsWrittenWithItsDeclarationAndItsDescription()
        {
            string text = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"));

            Assert.Equal("&Count : Numeric(4) // Contador\n", text);
        }

        [Fact]
        public void ADeletedVariableChangesTheFingerprint()
        {
            string before = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"), new VariableFact("Name", " : Character(40)", ""));
            string after = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"));

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void ADeletedDescriptionChangesTheFingerprint()
        {
            string before = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"));
            string after = Fingerprint(new VariableFact("Count", " : Numeric(4)", ""));

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void TheOrderOfTheVariablesDoesNotChangeTheFingerprint()
        {
            string ordered = Fingerprint(new VariableFact("Count", " : Numeric(4)", "A"), new VariableFact("Name", " : Character(40)", "B"));
            string reversed = Fingerprint(new VariableFact("Name", " : Character(40)", "B"), new VariableFact("Count", " : Numeric(4)", "A"));

            Assert.Equal(ordered, reversed);
        }

        [Fact]
        public void ANullDescriptionIsTheSameAsAnEmptyOne()
        {
            Assert.Equal(
                Fingerprint(new VariableFact("Count", " : Numeric(4)", null)),
                Fingerprint(new VariableFact("Count", " : Numeric(4)", "")));
        }

        [Fact]
        public void AnObjectWithoutVariablesHasAnEmptyFingerprint()
        {
            Assert.Equal(string.Empty, Fingerprint());
        }

        [Fact]
        public void AReadThatFailsGivesNoFingerprintAtAll()
        {
            string text = VariablesFingerprint.Capture(null, _ => throw new InvalidOperationException("the variables cannot be read"));

            Assert.Null(text);
        }
    }
}
