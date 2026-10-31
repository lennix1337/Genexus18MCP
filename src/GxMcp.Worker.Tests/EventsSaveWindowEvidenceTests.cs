using System;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The window of one verdict is made from the save's own evidence by EventsSaveWindow.FromSaveEvidence, and the
    // committed stamp is checked by ConfirmPersistedStamp. The SDK half only records the clocks and the target's stamps;
    // the offline double calls the same two functions, so each reason is tested here once, at its boundary.
    public sealed class EventsSaveWindowEvidenceTests
    {
        private static readonly long Origin = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc).Ticks;
        private static readonly long LocalOffset = TimeSpan.FromHours(-3).Ticks;

        private static long At(double seconds) => Origin + TimeSpan.FromSeconds(seconds).Ticks;

        // The nominal save: the transaction opens at 100 s, the write span runs from 101 s to 103 s, the target is stamped
        // 102 s, and before the save the target carried 50 s.
        private static EventsSaveWindow.SaveEvidence Evidence(long? stampAfterObjectSave, long lastUpdateBefore, long? transactionEnd = null)
            => new EventsSaveWindow.SaveEvidence
            {
                TransactionStartTicks = At(100),
                TransactionEndTicks = transactionEnd,
                WriteSpanStartTicks = At(101),
                ObjectSavedTicks = At(103),
                StampAfterObjectSave = stampAfterObjectSave,
                LastUpdateBeforeTicks = lastUpdateBefore,
                LocalUtcOffsetTicks = LocalOffset
            };

        [Fact]
        public void ACalibratedWindow_FromItsEvidence_IsTheWindowCalibrateGives()
        {
            var fromEvidence = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), At(160)), transactionClosed: true);
            var direct = EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(102), LocalOffset);

            Assert.True(fromEvidence.Calibrated);
            Assert.Null(fromEvidence.Reason);
            Assert.Equal(direct.LowTicks, fromEvidence.LowTicks);
            Assert.Equal(direct.HighTicks, fromEvidence.HighTicks);
            Assert.Equal(direct.OffsetTicks, fromEvidence.OffsetTicks);
        }

        [Fact]
        public void ATargetThatWasNotSaved_IsUnavailable_TargetNotSaved()
        {
            var window = EventsSaveWindow.FromSaveEvidence(Evidence(null, At(50), At(160)), transactionClosed: true);

            Assert.False(window.Calibrated);
            Assert.Equal("TargetNotSaved", window.Reason);
        }

        [Fact]
        public void ATargetStampEqualToTheStampBeforeTheSave_IsNotAdvanced()
        {
            var equal = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(102), At(160)), transactionClosed: true);

            Assert.False(equal.Calibrated);
            Assert.Equal("TargetStampNotAdvanced", equal.Reason);
        }

        [Fact]
        public void ATargetStampOneTickAfterTheStampBeforeTheSave_IsStillCalibrated()
        {
            var oneTickLater = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(102) - 1, At(160)), transactionClosed: true);

            Assert.True(oneTickLater.Calibrated);
            Assert.Null(oneTickLater.Reason);
        }

        [Fact]
        public void ATargetStampThatWentBackwards_IsNotAdvanced()
        {
            var backwards = EventsSaveWindow.FromSaveEvidence(Evidence(At(60), At(102), At(160)), transactionClosed: true);

            Assert.Equal("TargetStampNotAdvanced", backwards.Reason);
        }

        [Fact]
        public void AClosedTransactionWhoseEndWasNotRecorded_IsUnavailable_TransactionNotClosed()
        {
            var window = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), transactionEnd: null), transactionClosed: true);

            Assert.False(window.Calibrated);
            Assert.Equal("TransactionNotClosed", window.Reason);
        }

        [Fact]
        public void AnOpenTransaction_NeedsNoEnd_AndHasNoUpperBound()
        {
            var open = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), transactionEnd: null), transactionClosed: false);

            Assert.True(open.Calibrated);
            Assert.Null(open.HighTicks);
        }

        [Fact]
        public void AnOpenTransaction_WithAnEndRecordedAfterwards_IgnoresThatEnd()
        {
            var open = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), At(160)), transactionClosed: false);

            Assert.True(open.Calibrated);
            Assert.Null(open.HighTicks);
        }

        [Fact]
        public void ACommittedStampThatDiffers_FromTheSaveStamp_IsUnavailable()
        {
            var calibrated = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), At(160)), transactionClosed: true);

            var window = EventsSaveWindow.ConfirmPersistedStamp(calibrated, committed: true, persistedStampTicks: At(102) + 1, saveStampTicks: At(102));

            Assert.False(window.Calibrated);
            Assert.Equal("PersistedStampDiffersFromSaveStamp", window.Reason);
        }

        [Fact]
        public void ACommittedStampEqualToTheSaveStamp_KeepsTheWindow()
        {
            var calibrated = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), At(160)), transactionClosed: true);

            var window = EventsSaveWindow.ConfirmPersistedStamp(calibrated, committed: true, persistedStampTicks: At(102), saveStampTicks: At(102));

            Assert.True(window.Calibrated);
            Assert.Equal(calibrated.LowTicks, window.LowTicks);
            Assert.Equal(calibrated.HighTicks, window.HighTicks);
        }

        [Fact]
        public void AStampThatDiffers_WithoutACommit_KeepsTheWindow()
        {
            var calibrated = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), At(160)), transactionClosed: true);

            var window = EventsSaveWindow.ConfirmPersistedStamp(calibrated, committed: false, persistedStampTicks: At(90), saveStampTicks: At(102));

            Assert.True(window.Calibrated);
        }

        [Fact]
        public void ACommittedStampCheck_OnAnUncalibratedWindow_KeepsItsOwnReason()
        {
            var notAdvanced = EventsSaveWindow.FromSaveEvidence(Evidence(At(50), At(50), At(160)), transactionClosed: true);

            var window = EventsSaveWindow.ConfirmPersistedStamp(notAdvanced, committed: true, persistedStampTicks: At(90), saveStampTicks: At(50));

            Assert.Equal("TargetStampNotAdvanced", window.Reason);
        }

        [Fact]
        public void ACommittedStampCheck_WithoutASaveStamp_IsUnavailable()
        {
            var calibrated = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), At(160)), transactionClosed: true);

            var window = EventsSaveWindow.ConfirmPersistedStamp(calibrated, committed: true, persistedStampTicks: At(102), saveStampTicks: null);

            Assert.Equal("PersistedStampDiffersFromSaveStamp", window.Reason);
        }

        [Fact]
        public void TheDescribedWindow_CarriesTheAnchorAgainstTheWriteStart()
        {
            var evidence = Evidence(At(102), At(50), At(160));
            evidence.AnchorKind = "Utc";

            var described = EventsSaveWindow.FromSaveEvidence(evidence, transactionClosed: true).Describe();

            Assert.Equal(EventsSaveWindow.Iso(At(102)), described["anchorStampUtc"]!.ToString());
            Assert.Equal(EventsSaveWindow.Iso(At(101)), described["writeStartUtc"]!.ToString());
            Assert.Equal(EventsSaveWindow.Iso(At(103)), described["objectSavedUtc"]!.ToString());
            Assert.Equal(EventsSaveWindow.Iso(At(50)), described["lastUpdateBeforeUtc"]!.ToString());
            Assert.Equal(1d, described["anchorMinusWriteStartSeconds"]!.Value<double>());
            Assert.Equal("Utc", described["anchorKind"]!.ToString());
        }

        [Fact]
        public void AnUnavailableWindowFromASave_StillCarriesTheAnchorThatMadeItUnavailable()
        {
            var described = EventsSaveWindow.FromSaveEvidence(Evidence(At(50), At(50), At(160)), transactionClosed: true).Describe();

            Assert.False(described["calibrated"]!.Value<bool>());
            Assert.Equal("TargetStampNotAdvanced", described["reason"]!.ToString());
            Assert.Equal(EventsSaveWindow.Iso(At(50)), described["anchorStampUtc"]!.ToString());
        }

        // Contract item 5, rows (i) and (ii). Row (iii) is not decided by the function: it stops the item and is reported.

        [Fact]
        public void APreCommitReadThatShowsTheSaveStamp_KeepsTheWindow()
        {
            var calibrated = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), null), transactionClosed: false);

            var window = EventsSaveWindow.ConfirmPreCommitStamp(calibrated, At(102), At(102), At(50));

            Assert.True(window.Calibrated);
            Assert.Equal(calibrated.LowTicks, window.LowTicks);
        }

        [Fact]
        public void APreCommitReadInAnotherRepresentation_LaterThanTheStampBeforeTheSave_IsUnavailable()
        {
            // The save stamp is in the SDK's UTC representation; the reading inside the transaction and the stamp before the save
            // both use the local wall-clock ticks, so the reading is later than the stamp before the save and differs from the save.
            var calibrated = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50) + LocalOffset, null), transactionClosed: false);

            var window = EventsSaveWindow.ConfirmPreCommitStamp(calibrated, At(102), At(102) + LocalOffset, At(50) + LocalOffset);

            Assert.False(window.Calibrated);
            Assert.Equal("PreCommitStampRepresentationMismatch", window.Reason);
        }

        [Fact]
        public void APreCommitReadThatEqualsTheStampBeforeTheSave_IsNotDecidedHere()
        {
            var calibrated = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), null), transactionClosed: false);

            var window = EventsSaveWindow.ConfirmPreCommitStamp(calibrated, At(102), At(50), At(50));

            Assert.True(window.Calibrated);
        }

        [Fact]
        public void APreCommitReadThatFailed_LeavesTheCalibratedWindowUnavailable()
        {
            var calibrated = EventsSaveWindow.FromSaveEvidence(Evidence(At(102), At(50), null), transactionClosed: false);

            var window = EventsSaveWindow.ConfirmPreCommitStamp(calibrated, At(102), null, At(50));

            Assert.False(window.Calibrated);
            Assert.Equal("PreCommitStampNotRead", window.Reason);
        }

        [Fact]
        public void AnUncalibratedWindow_StaysUnavailableWithItsReason_WhateverTheReading()
        {
            var notSaved = EventsSaveWindow.FromSaveEvidence(Evidence(null, At(50), null), transactionClosed: false);

            var window = EventsSaveWindow.ConfirmPreCommitStamp(notSaved, null, At(102) + LocalOffset, At(50) + LocalOffset);

            Assert.Equal("TargetNotSaved", window.Reason);
        }

        // The pre-commit check as the production steps and the double both make it (ForPreCommit): a reading equal to the save's
        // stamp (102 s) or to the stamp before the save (50 s) keeps the window; any other reading is a mismatch.
        [Theory]
        [InlineData(102, null)]
        [InlineData(50, null)]
        [InlineData(200, "PreCommitStampRepresentationMismatch")]
        [InlineData(-10698, "PreCommitStampRepresentationMismatch")] // the save's stamp three hours behind, as a local clock reads it
        public void ThePreCommitCheck_DecidesEachReadingAgainstTheSaveStampAndTheStampBeforeIt(double readingSeconds, string reason)
        {
            var window = EventsSaveWindow.ForPreCommit(Evidence(At(102), At(50), null), At(102), At(readingSeconds));

            Assert.Equal(reason, window.Reason);
            Assert.Equal(reason == null, window.Calibrated);
        }

        [Fact]
        public void ThePreCommitCheck_AFailedReading_IsUnavailable()
        {
            var window = EventsSaveWindow.ForPreCommit(Evidence(At(102), At(50), null), At(102), null);

            Assert.False(window.Calibrated);
            Assert.Equal("PreCommitStampNotRead", window.Reason);
        }

        // The final window after a rollback keeps the pre-commit guard's reason; any other save keeps the window it always had.
        [Fact]
        public void AFinalWindow_AfterARollbackThatTheGuardRefused_KeepsTheGuardReasonAndItsEvidence()
        {
            var window = EventsSaveWindow.ForFinal(Evidence(At(102), At(50), At(160)), committed: false,
                persistedStampTicks: At(50), saveStampTicks: At(102), preCommitReason: "PreCommitStampRepresentationMismatch");

            Assert.False(window.Calibrated);
            Assert.Equal("PreCommitStampRepresentationMismatch", window.Reason);
            Assert.Equal(EventsSaveWindow.Iso(At(102)), window.Describe()["anchorStampUtc"]!.ToString());
        }

        [Fact]
        public void AFinalWindow_WithoutARefusingGuard_IsTheCalibratedWindowOfTheSave()
        {
            var rolledBack = EventsSaveWindow.ForFinal(Evidence(At(102), At(50), At(160)), committed: false,
                persistedStampTicks: At(50), saveStampTicks: At(102), preCommitReason: null);
            var committed = EventsSaveWindow.ForFinal(Evidence(At(102), At(50), At(160)), committed: true,
                persistedStampTicks: At(102), saveStampTicks: At(102), preCommitReason: "PreCommitStampRepresentationMismatch");
            var otherReason = EventsSaveWindow.ForFinal(Evidence(At(102), At(50), At(160)), committed: false,
                persistedStampTicks: At(50), saveStampTicks: At(102), preCommitReason: "TargetNotSaved");

            Assert.True(rolledBack.Calibrated);
            Assert.True(committed.Calibrated);
            Assert.True(otherReason.Calibrated);
        }
    }
}
