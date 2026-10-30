using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // What the isolated Events save concludes from its evidence: the target's own checks, and the other objects that
    // differ between the inventories. SDK-free; the same code decides the receipt of a real save.
    public sealed class IsolatedEventsSaveVerdictTests
    {
        private static readonly long Origin = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc).Ticks;
        private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

        private static long At(double seconds) => Origin + TimeSpan.FromSeconds(seconds).Ticks;

        private static EventsInventoryEntry Entry(int version, long stamp)
            => new EventsInventoryEntry(version, stamp, "root", "Procedure01", "type-p", "Procedure", "dev");

        private static EventsSaveWindow Closed()
            => EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(102), TimeSpan.FromHours(-3).Ticks);

        // The window as the pre-commit inventory sees it: the transaction is still open.
        private static EventsSaveWindow Open()
            => EventsSaveWindow.Calibrate(At(100), null, At(101), At(103), At(102), TimeSpan.FromHours(-3).Ticks);

        private static EventsOtherObjectChanges OtherEditedAt(double seconds, EventsSaveWindow window)
            => EventsOtherObjectChanges.Classify(
                new Dictionary<Guid, EventsInventoryEntry> { [Other] = Entry(3, At(10)) },
                new Dictionary<Guid, EventsInventoryEntry> { [Other] = Entry(4, At(seconds)) },
                Target, window);

        private static EventsOtherObjectChanges NothingChanged()
            => EventsOtherObjectChanges.Classify(
                new Dictionary<Guid, EventsInventoryEntry> { [Other] = Entry(3, At(10)) },
                new Dictionary<Guid, EventsInventoryEntry> { [Other] = Entry(3, At(10)) },
                Target, Closed());

        private static IsolatedEventsTargetEvidence Committed() => new IsolatedEventsTargetEvidence
        {
            Committed = true, PartPersisted = true, MetadataAdvanced = true, OtherPartsIntact = true
        };

        private static IsolatedEventsTargetEvidence RolledBack() => new IsolatedEventsTargetEvidence
        {
            Committed = false, PartPersisted = false, MetadataAdvanced = false, OtherPartsIntact = true, RestoredToOriginal = true
        };

        [Fact]
        public void NothingChanged_IsVerified_AndReportsNoOtherObject()
        {
            var receipt = new JObject();

            bool verified = IsolatedEventsSaveVerdict.Apply(receipt, Committed(), NothingChanged(), Closed(), out string message);

            Assert.True(verified);
            Assert.Null(message);
            Assert.True(receipt["verified"]!.Value<bool>());
            Assert.True(receipt["writeVerifiedForTarget"]!.Value<bool>());
            Assert.True(receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Empty((JArray)receipt["unexpectedChangedObjects"]!);
            Assert.Null(receipt["unexpectedChangedObjectDetails"]);
            Assert.Null(receipt["concurrentChanges"]);
            Assert.Null(receipt["reasonCode"]);
            Assert.True(receipt["saveWindow"]!["calibrated"]!.Value<bool>());
        }

        [Fact]
        public void AnEditByAnotherProcessOutsideTheWindow_IsReported_AndDoesNotFailTheSave()
        {
            var receipt = new JObject();
            var others = OtherEditedAt(190, Closed());

            bool verified = IsolatedEventsSaveVerdict.Apply(receipt, Committed(), others, Closed(), out string message);

            Assert.True(verified);
            Assert.Null(message);
            Assert.True(receipt["verified"]!.Value<bool>());
            Assert.True(receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Empty((JArray)receipt["unexpectedChangedObjects"]!);
            Assert.Equal(1, receipt["concurrentChangeCount"]!.Value<int>());
            var change = (JObject)Assert.Single((JArray)receipt["concurrentChanges"]!);
            Assert.Equal(Other.ToString(), change["guid"]!.ToString());
            Assert.Equal("AfterSaveWindow", change["reason"]!.ToString());
            Assert.Null(receipt["reasonCode"]);
            Assert.Null(receipt["retrySafe"]);
        }

        [Fact]
        public void AnEditInsideTheWindow_FailsTheSave_ButStatesThatTheTargetIsVerified()
        {
            var receipt = new JObject();
            var others = OtherEditedAt(130, Closed());

            bool verified = IsolatedEventsSaveVerdict.Apply(receipt, Committed(), others, Closed(), out string message);

            Assert.False(verified);
            Assert.False(receipt["verified"]!.Value<bool>());
            Assert.True(receipt["writeVerifiedForTarget"]!.Value<bool>());
            Assert.False(receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Equal(new[] { Other.ToString() }, receipt["unexpectedChangedObjects"]!.ToObject<string[]>());
            var detail = (JObject)Assert.Single((JArray)receipt["unexpectedChangedObjectDetails"]!);
            Assert.Equal("InsideSaveWindow", detail["reason"]!.ToString());
            Assert.Equal("blocking", detail["verdict"]!.ToString());
            Assert.Null(receipt["concurrentChanges"]);
            Assert.Equal(IsolatedEventsSaveVerdict.OtherObjectsChangedCode, receipt["reasonCode"]!.ToString());
            Assert.False(receipt["retrySafe"]!.Value<bool>());
            Assert.StartsWith("The Events source was committed and read back", message);
            Assert.Contains("1 other object(s)", message);
            Assert.Contains("unexpectedChangedObjectDetails", message);
            Assert.Contains("do not repeat the edit", message);
            Assert.DoesNotContain("\n", message);
        }

        [Fact]
        public void WithoutACalibratedWindow_AnyEditOfAnotherObjectFailsTheSave_ExactlyAsBefore()
        {
            var window = EventsSaveWindow.Unavailable("TargetStampNotAdvanced");
            var receipt = new JObject();
            var others = OtherEditedAt(190, window);

            bool verified = IsolatedEventsSaveVerdict.Apply(receipt, Committed(), others, window, out string message);

            Assert.False(verified);
            Assert.False(receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Equal("SaveWindowUnavailable", receipt["unexpectedChangedObjectDetails"]![0]!["reason"]!.ToString());
            Assert.False(receipt["saveWindow"]!["calibrated"]!.Value<bool>());
            Assert.Equal("TargetStampNotAdvanced", receipt["saveWindow"]!["reason"]!.ToString());
            Assert.NotNull(message);
        }

        [Theory]
        [InlineData(false, true, true, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, true, false, true)]
        [InlineData(true, true, true, false)]
        public void WhenTheTargetItselfIsNotVerified_NoReasonAboutOtherObjectsIsOffered(
            bool committed, bool partPersisted, bool metadataAdvanced, bool otherPartsIntact)
        {
            var receipt = new JObject();
            var target = new IsolatedEventsTargetEvidence
            {
                Committed = committed, PartPersisted = partPersisted, MetadataAdvanced = metadataAdvanced, OtherPartsIntact = otherPartsIntact
            };

            bool verified = IsolatedEventsSaveVerdict.Apply(receipt, target, OtherEditedAt(130, Closed()), Closed(), out string message);

            Assert.False(verified);
            Assert.False(receipt["writeVerifiedForTarget"]!.Value<bool>());
            Assert.Null(message);
            Assert.Null(receipt["reasonCode"]);
            Assert.Null(receipt["retrySafe"]);
        }

        [Fact]
        public void ARollback_IsVerified_WhenAnotherProcessOnlyEditedOutsideTheWindow()
        {
            var receipt = new JObject();

            bool verified = IsolatedEventsSaveVerdict.Apply(receipt, RolledBack(), OtherEditedAt(190, Closed()), Closed(), out _);

            Assert.False(verified);
            Assert.True(receipt["rollbackVerified"]!.Value<bool>());
            Assert.False(receipt["writeVerifiedForTarget"]!.Value<bool>());
        }

        [Fact]
        public void ARollback_IsNotVerified_WhenAnotherObjectChangedInsideTheWindow_OrTheTargetIsNotRestored()
        {
            var inside = new JObject();
            IsolatedEventsSaveVerdict.Apply(inside, RolledBack(), OtherEditedAt(130, Closed()), Closed(), out _);
            Assert.False(inside["rollbackVerified"]!.Value<bool>());

            var notRestored = new JObject();
            var target = RolledBack();
            target.RestoredToOriginal = false;
            IsolatedEventsSaveVerdict.Apply(notRestored, target, NothingChanged(), Closed(), out _);
            Assert.False(notRestored["rollbackVerified"]!.Value<bool>());

            var committed = new JObject();
            IsolatedEventsSaveVerdict.Apply(committed, Committed(), NothingChanged(), Closed(), out _);
            Assert.False(committed["rollbackVerified"]!.Value<bool>());
        }

        [Fact]
        public void WhatThePreCommitInventoryShowed_SurvivesAFinalInventoryThatNoLongerShowsIt()
        {
            // The refusal rolled the transaction back and the rollback undid the change: the final inventory is clean.
            var receipt = new JObject();
            IsolatedEventsSaveVerdict.RecordPreCommitOthers(receipt, OtherEditedAt(130, Open()));

            IsolatedEventsSaveVerdict.Apply(receipt, RolledBack(), NothingChanged(), Closed(), out _);

            Assert.Empty((JArray)receipt["unexpectedChangedObjects"]!);
            Assert.Null(receipt["unexpectedChangedObjectDetails"]);
            Assert.True(receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.True(receipt["rollbackVerified"]!.Value<bool>());
            Assert.Equal(new[] { Other.ToString() }, receipt["preCommitUnexpectedChangedObjects"]!.ToObject<string[]>());
            var detail = (JObject)Assert.Single((JArray)receipt["preCommitUnexpectedChangedObjectDetails"]!);
            Assert.Equal(Other.ToString(), detail["guid"]!.ToString());
            Assert.Equal("Procedure01", detail["name"]!.ToString());
            Assert.Equal("InsideSaveWindow", detail["reason"]!.ToString());
        }

        [Fact]
        public void TheVerdict_ReportsTheFinalInventoryBesideThePreCommitOne_WithoutRewritingEither()
        {
            var receipt = new JObject();
            IsolatedEventsSaveVerdict.RecordPreCommitOthers(receipt, OtherEditedAt(130, Open()));

            IsolatedEventsSaveVerdict.Apply(receipt, Committed(), OtherEditedAt(131, Closed()), Closed(), out _);

            var preCommit = (JObject)Assert.Single((JArray)receipt["preCommitUnexpectedChangedObjectDetails"]!);
            var final = (JObject)Assert.Single((JArray)receipt["unexpectedChangedObjectDetails"]!);
            Assert.Equal(EventsSaveWindow.Iso(At(130)), preCommit["lastUpdateAfter"]!.ToString());
            Assert.Equal(EventsSaveWindow.Iso(At(131)), final["lastUpdateAfter"]!.ToString());
        }

        [Fact]
        public void RecordPreCommitOthers_WithNothingInDoubt_ListsNoObjectAndDescribesNone()
        {
            var receipt = new JObject();

            IsolatedEventsSaveVerdict.RecordPreCommitOthers(receipt, NothingChanged());

            Assert.Empty((JArray)receipt["unexpectedChangedObjects"]!);
            Assert.Empty((JArray)receipt["preCommitUnexpectedChangedObjects"]!);
            Assert.Null(receipt["preCommitUnexpectedChangedObjectDetails"]);
            Assert.Null(receipt["preCommitUnexpectedChangedObjectDetailsTruncated"]);
        }

        [Fact]
        public void RecordPreCommitOthers_SaysWhenItDescribesFewerChangesThanItFound()
        {
            var before = new Dictionary<Guid, EventsInventoryEntry>();
            var after = new Dictionary<Guid, EventsInventoryEntry>();
            for (int i = 0; i < EventsOtherObjectChanges.MaxDescribed + 3; i++)
            {
                var id = Guid.NewGuid();
                before[id] = Entry(1, At(10));
                after[id] = Entry(2, At(130));
            }
            var receipt = new JObject();

            IsolatedEventsSaveVerdict.RecordPreCommitOthers(receipt, EventsOtherObjectChanges.Classify(before, after, Target, Open()));

            Assert.Equal(EventsOtherObjectChanges.MaxDescribed + 3, ((JArray)receipt["preCommitUnexpectedChangedObjects"]!).Count);
            Assert.Equal(EventsOtherObjectChanges.MaxDescribed, ((JArray)receipt["preCommitUnexpectedChangedObjectDetails"]!).Count);
            Assert.True(receipt["preCommitUnexpectedChangedObjectDetailsTruncated"]!.Value<bool>());
        }

        [Fact]
        public void TheReceipt_SaysWhenItDescribesFewerChangesThanItFound()
        {
            var before = new Dictionary<Guid, EventsInventoryEntry>();
            var after = new Dictionary<Guid, EventsInventoryEntry>();
            for (int i = 0; i < EventsOtherObjectChanges.MaxDescribed + 3; i++)
            {
                var id = Guid.NewGuid();
                before[id] = Entry(1, At(10));
                after[id] = Entry(2, At(190));
            }
            var receipt = new JObject();

            IsolatedEventsSaveVerdict.Apply(receipt, Committed(), EventsOtherObjectChanges.Classify(before, after, Target, Closed()), Closed(), out _);

            Assert.Equal(EventsOtherObjectChanges.MaxDescribed + 3, receipt["concurrentChangeCount"]!.Value<int>());
            Assert.Equal(EventsOtherObjectChanges.MaxDescribed, ((JArray)receipt["concurrentChanges"]!).Count);
            Assert.True(receipt["concurrentChangesTruncated"]!.Value<bool>());
        }
    }
}
