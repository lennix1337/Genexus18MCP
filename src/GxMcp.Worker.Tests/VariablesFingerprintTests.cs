using System;
using System.Runtime.Serialization;
using Artech.Architecture.Common.Objects;
using GxMcp.TestSupport;
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
        public void AStandardVariableIsLeftOutSoItsRemovalOnSaveIsNotAChange()
        {
            string before = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"), new VariableFact("Pad", " : Character(40)", "", standard: true));
            string after = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"));

            Assert.Equal(after, before);
        }

        [Fact]
        public void AStandardVariableDoesNotAppearInTheText()
        {
            Assert.Equal(
                "&Count : Numeric(4) // Contador\n",
                Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"), new VariableFact("Pad", " : Character(40)", "Padrao", standard: true)));
        }

        [Fact]
        public void AStandardVariableWithAnOutdatedDescriptionIsNotADifference()
        {
            string before = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"), new VariableFact("Pad", " : Character(40)", "Antiga", standard: true));
            string after = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"), new VariableFact("Pad", " : Character(40)", "Nova", standard: true));

            Assert.Equal(before, after);
        }

        [Fact]
        public void ADeletedVariableStillChangesTheFingerprintWhenAStandardVariableIsPresent()
        {
            string before = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"), new VariableFact("Name", " : Character(40)", ""), new VariableFact("Pad", " : Character(40)", "", standard: true));
            string after = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"), new VariableFact("Pad", " : Character(40)", "", standard: true));

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

        [Fact]
        public void ADescriptionThatDiffersOnlyInLineEndingsOrTrailingSpacesIsTheSameText()
        {
            string plain = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador"));

            Assert.Equal(plain, Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador\r\n")));
            Assert.Equal(plain, Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador\n")));
            Assert.Equal(plain, Fingerprint(new VariableFact("Count", " : Numeric(4)", "Contador   ")));
        }

        [Fact]
        public void AMultiLineDescriptionIsOneLineWithTheSameTextInEitherLineEnding()
        {
            string windows = Fingerprint(new VariableFact("Count", " : Numeric(4)", "Linha um\r\nLinha dois"));

            Assert.Equal(windows, Fingerprint(new VariableFact("Count", " : Numeric(4)", "Linha um\nLinha dois")));
            Assert.Equal("&Count : Numeric(4) // Linha um Linha dois\n", windows);
        }

        [Fact]
        public void TheDeclarationIsWrittenFromTheStoredTypeLengthDecimalsAndCollection()
        {
            Assert.Equal(" : Numeric(8)", VariablesFingerprint.DeclarationOf("Numeric", 8, 0, false));
            Assert.Equal(" : Numeric(8,2)", VariablesFingerprint.DeclarationOf("Numeric", 8, 2, false));
            Assert.Equal(" : SDT(10) Collection", VariablesFingerprint.DeclarationOf("SDT", 10, 0, true));
        }

        [Fact]
        public void TheSdkReaderGivesNoTextWhenTheObjectCannotBeRead()
        {
            // The production reader is the default one: an object whose parts cannot be read is unknown, never an empty text.
            var unreadable = (KBObject)FormatterServices.GetUninitializedObject(typeof(KBObject));

            Assert.Null(VariablesFingerprint.Capture(unreadable));
        }

        [Fact]
        public void TheSdkReaderReadsEveryVariableAndItsDescription()
        {
            string src = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "VariablesFingerprint.cs"));

            Assert.Equal(1, SourceAssert.Count(src, "part.Variables"));
            Assert.Equal(1, SourceAssert.Count(src, "v.Description"));
        }

        [Fact]
        public void TheSdkReaderReadsTheStandardFlagOfEachVariable()
        {
            string src = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "VariablesFingerprint.cs"));

            Assert.Equal(1, SourceAssert.Count(src, "v.IsStandard"));
        }
    }
}
