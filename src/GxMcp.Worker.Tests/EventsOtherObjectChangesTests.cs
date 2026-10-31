using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The save window and the split of "other objects that differ" into the ones the protected Events save can have
    // written (blocking) and the ones another process wrote outside its window (concurrent). No SDK and no clock:
    // every instant is a number of seconds from a fixed origin.
    public sealed class EventsOtherObjectChangesTests
    {
        private static readonly long Origin = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc).Ticks;
        private static readonly long LocalOffset = TimeSpan.FromHours(-3).Ticks;

        private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

        private static long At(double seconds) => Origin + TimeSpan.FromSeconds(seconds).Ticks;

        private static EventsInventoryEntry Entry(int version, long stamp, string name = "Procedure01", string parent = "root",
            string typeId = "type-p", string typeName = "Procedure", string by = "dev")
            => new EventsInventoryEntry(version, stamp, parent, name, typeId, typeName, by);

        // The transaction is open from 100 s to 160 s, the writes happen between 101 s and 103 s and the target is
        // stamped 102 s by the same clock. The measured offset is one second either way; the clock tolerance widens it to
        // five seconds, so the window is 93 s (100 - 5 - 2 margin) to 167 s (160 + 5 + 2).
        private static EventsSaveWindow Closed() => EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(102), LocalOffset);
        private static EventsSaveWindow Open() => EventsSaveWindow.Calibrate(At(100), null, At(101), At(103), At(102), LocalOffset);

        private static EventsOtherObjectChanges Classify(
            EventsInventoryEntry before, EventsInventoryEntry after, EventsSaveWindow window)
            => EventsOtherObjectChanges.Classify(
                new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(7, At(50), "Panel"), [Other] = before },
                new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(8, At(102), "Panel"), [Other] = after },
                Target, window);

        private static string OnlyReason(EventsOtherObjectChanges changes)
            => Assert.Single(changes.Blocking.Concat(changes.Concurrent)).Reason;

        // ---- the window

        [Fact]
        public void Window_FromTheSameClock_IsTheTransactionPlusTheWriteSpanAndTheMargin()
        {
            var window = Closed();

            Assert.True(window.Calibrated);
            Assert.Null(window.Reason);
            Assert.Equal(At(93), window.LowTicks);
            Assert.Equal(At(167), window.HighTicks);
            Assert.Equal(0, window.OffsetTicks);
        }

        [Fact]
        public void AStampClockThreeSecondsAhead_IsReportedAsThreeSeconds_AndTheWindowStaysWidened()
        {
            // The target is stamped 105 s with the writes at 101 s to 103 s: the stamp clock reads three seconds ahead.
            var window = EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(105), LocalOffset);

            Assert.True(window.Calibrated);
            Assert.Equal(TimeSpan.FromSeconds(3).Ticks, window.OffsetTicks);
            Assert.Equal(3d, window.Describe()["stampClockOffsetSeconds"]!.Value<double>());
            Assert.Equal(At(93), window.LowTicks);
            Assert.Equal(At(167), window.HighTicks);
        }

        [Fact]
        public void Window_FromTheLocalWallClockLabelledUtc_ShiftsWithThatOffset()
        {
            // The SDK stamps in a clock that is the Worker's local time: the target's stamp sits three hours before.
            var window = EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(102) + LocalOffset, LocalOffset);

            Assert.True(window.Calibrated);
            Assert.Equal(At(93) + LocalOffset, window.LowTicks);
            Assert.Equal(At(167) + LocalOffset, window.HighTicks);
        }

        [Theory]
        [InlineData(60)]
        [InlineData(-600)]
        [InlineData(14400)]
        public void Window_WhoseStampClockIsNotOneTheWorkerKnows_IsUnavailable(double offsetSeconds)
        {
            var window = EventsSaveWindow.Calibrate(
                At(100), At(160), At(101), At(103), At(102) + TimeSpan.FromSeconds(offsetSeconds).Ticks, LocalOffset);

            Assert.False(window.Calibrated);
            Assert.Equal("StampClockNotComparable", window.Reason);
        }

        [Theory]
        [InlineData(103, 101)] // the write span ends before it starts
        [InlineData(99, 103)]  // the writes start before the transaction opened
        public void Window_WithAnImpossibleTimeline_IsUnavailable(double spanStart, double spanEnd)
        {
            var window = EventsSaveWindow.Calibrate(At(100), At(160), At(spanStart), At(spanEnd), At(102), LocalOffset);

            Assert.False(window.Calibrated);
            Assert.Equal("SaveTimelineInvalid", window.Reason);
        }

        [Fact]
        public void Window_ClosingBeforeTheLastWriteEnded_IsUnavailable()
        {
            var window = EventsSaveWindow.Calibrate(At(100), At(102), At(101), At(103), At(102), LocalOffset);

            Assert.False(window.Calibrated);
            Assert.Equal("SaveTimelineInvalid", window.Reason);
        }

        [Fact]
        public void Window_WhileTheTransactionIsOpen_HasNoUpperBound()
        {
            var window = Open();

            Assert.True(window.Calibrated);
            Assert.Equal(At(93), window.LowTicks);
            Assert.Null(window.HighTicks);
        }

        [Fact]
        public void Window_Describe_SaysWhetherItIsCalibratedAndWhyNot()
        {
            var calibrated = Closed().Describe();
            Assert.True(calibrated["calibrated"]!.Value<bool>());
            Assert.Equal(0d, calibrated["stampClockOffsetSeconds"]!.Value<double>());
            Assert.Equal(74d, calibrated["seconds"]!.Value<double>());
            Assert.NotNull(calibrated["from"]);
            Assert.NotNull(calibrated["to"]);

            var open = Open().Describe();
            Assert.True(open["open"]!.Value<bool>());
            Assert.Null(open["to"]);

            var unavailable = EventsSaveWindow.Unavailable("TargetStampNotAdvanced").Describe();
            Assert.False(unavailable["calibrated"]!.Value<bool>());
            Assert.Equal("TargetStampNotAdvanced", unavailable["reason"]!.ToString());
            Assert.Null(unavailable["from"]);
        }

        // ---- the classification

        [Fact]
        public void EditBeforeTheWindow_IsConcurrent()
        {
            var changes = Classify(Entry(3, At(10)), Entry(4, At(60)), Closed());

            Assert.Empty(changes.Blocking);
            var change = Assert.Single(changes.Concurrent);
            Assert.Equal("BeforeSaveWindow", change.Reason);
            Assert.Equal(Other, change.Guid);
        }

        [Fact]
        public void EditAfterTheClosedWindow_IsConcurrent()
        {
            var changes = Classify(Entry(3, At(10)), Entry(4, At(190)), Closed());

            Assert.Empty(changes.Blocking);
            Assert.Equal("AfterSaveWindow", Assert.Single(changes.Concurrent).Reason);
        }

        [Fact]
        public void TheEdgesOfTheWindow_AreInside_AndOneTickBeyondThemIsOutside()
        {
            var window = Closed();

            Assert.Equal("InsideSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(93)), window)));
            Assert.Equal("BeforeSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(93) - 1), window)));
            Assert.Equal("InsideSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(167)), window)));
            Assert.Equal("AfterSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(167) + 1), window)));
        }

        // The anchor is the target's stamp after the object save. The review's probes assumed it could come a little before
        // the write span, or that the Worker's clock could step inside the span; the tolerance keeps the own writes inside.

        [Fact]
        public void AnAnchorStampedBeforeTheWriteSpan_DoesNotClearTheWritesOfTheSave()
        {
            // The anchor is 2.5 s before the write span and the transaction closes 0.05 s after the last write.
            var window = EventsSaveWindow.Calibrate(At(100), At(100.6) + TimeSpan.FromSeconds(0.05).Ticks, At(100.1), At(100.6), At(97), LocalOffset);

            Assert.True(window.Calibrated);
            foreach (double write in new[] { 100.1, 100.35, 100.6 })
                Assert.Equal("InsideSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(write)), window)));
        }

        [Fact]
        public void ABackwardClockStepInsideTheSpan_DoesNotClearAnOwnWrite()
        {
            // The Worker's clock steps back 5 s after the target is stamped: a side effect written after the step carries 97 s.
            var window = EventsSaveWindow.Calibrate(At(100), At(130), At(101), At(101), At(102), LocalOffset);

            Assert.True(window.Calibrated);
            Assert.Equal("InsideSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(97)), window)));
        }

        [Fact]
        public void ALocalOffsetChangeBetweenASideEffectAndTheStamp_IsStillConcurrent_AsADocumentedLimit()
        {
            // LIMIT, documented in docs/events-object-save-isolation.md: the SDK stamps the local wall clock labelled as UTC,
            // and the offset changes by one hour between the side effect and the target's stamp. The tolerance is five seconds,
            // so this write still reads as concurrent.
            var window = EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(102) + LocalOffset, LocalOffset);
            long sideEffect = At(101) + TimeSpan.FromHours(-2).Ticks;

            Assert.True(window.Calibrated);
            Assert.Equal("AfterSaveWindow", OnlyReason(Classify(Entry(3, At(-20000)), Entry(4, sideEffect), window)));
        }

        [Fact]
        public void TheWidenedWindow_ContainsTheMeasuredOne_ForEveryAnchorInTheTolerance()
        {
            // The measured window is the transaction with the measured offsets and the margin on both ends. Widening can only
            // move its edges outwards, so no write that the measured window contained is lost.
            foreach (double anchor in new double[] { 96, 97, 99, 101, 102, 103, 104, 106 })
            {
                var window = EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(anchor), LocalOffset);
                Assert.True(window.Calibrated, anchor.ToString());

                long measuredLow = At(100) + (At(anchor) - At(103)) - EventsSaveWindow.Margin.Ticks;
                long measuredHigh = At(160) + (At(anchor) - At(101)) + EventsSaveWindow.Margin.Ticks;
                Assert.True(window.LowTicks <= measuredLow, anchor.ToString());
                Assert.True(window.HighTicks >= measuredHigh, anchor.ToString());
            }
        }

        [Fact]
        public void EditInsideTheWindow_Blocks_ItCouldBeAnEffectOfTheSave()
        {
            var changes = Classify(Entry(3, At(10)), Entry(4, At(130)), Closed());

            Assert.Empty(changes.Concurrent);
            Assert.Equal("InsideSaveWindow", Assert.Single(changes.Blocking).Reason);
        }

        [Fact]
        public void WhileTheTransactionIsOpen_OnlyAnEditBeforeItClearsAnObject()
        {
            var open = Open();

            Assert.Equal("BeforeSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(60)), open)));
            // A stamp after the transaction opened is inside it: it has not closed, so nothing can be "after" yet.
            Assert.Equal("InsideSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(130)), open)));
            Assert.Equal("InsideSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(500)), open)));
        }

        [Theory]
        [InlineData(5, 60, "VersionNotAdvancedByOne")]  // two saves: ours and someone else's cannot be told apart
        [InlineData(3, 60, "VersionNotAdvancedByOne")]  // a stamp moved but the version did not
        [InlineData(2, 60, "VersionNotAdvancedByOne")]  // the version went backwards
        [InlineData(4, 10, "StampNotAdvanced")]         // the stamp did not move
        [InlineData(4, 5, "StampNotAdvanced")]          // the stamp went backwards
        public void AnythingBeyondASingleNewerSave_Blocks_EvenOutsideTheWindow(int versionAfter, double stampAfter, string reason)
        {
            var changes = Classify(Entry(3, At(10)), Entry(versionAfter, At(stampAfter)), Closed());

            Assert.Empty(changes.Concurrent);
            Assert.Equal(reason, Assert.Single(changes.Blocking).Reason);
        }

        [Theory]
        [InlineData("OtherParent", "Procedure01", "type-p")]
        [InlineData("root", "Procedure02", "type-p")]
        [InlineData("root", "Procedure01", "type-w")]
        public void AMovedRenamedOrRetypedObject_Blocks_EvenOutsideTheWindow(string parent, string name, string typeId)
        {
            var changes = Classify(Entry(3, At(10)), Entry(4, At(60), name, parent, typeId), Closed());

            Assert.Empty(changes.Concurrent);
            Assert.Equal("MovedRenamedOrRetyped", Assert.Single(changes.Blocking).Reason);
        }

        [Fact]
        public void ACreatedOrDeletedObject_Blocks_EvenOutsideTheWindow()
        {
            var created = EventsOtherObjectChanges.Classify(
                new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(7, At(50), "Panel") },
                new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(8, At(102), "Panel"), [Other] = Entry(1, At(10)) },
                Target, Closed());
            var deleted = EventsOtherObjectChanges.Classify(
                new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(7, At(50), "Panel"), [Other] = Entry(1, At(10)) },
                new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(8, At(102), "Panel") },
                Target, Closed());

            Assert.Equal("Created", Assert.Single(created.Blocking).Reason);
            Assert.Equal("created", created.Blocking[0].Kind);
            Assert.Equal("Deleted", Assert.Single(deleted.Blocking).Reason);
            Assert.Equal("deleted", deleted.Blocking[0].Kind);
            Assert.Empty(created.Concurrent.Concat(deleted.Concurrent));
        }

        [Fact]
        public void WithoutACalibratedWindow_NoObjectIsCleared_WhateverItsStamp()
        {
            var unavailable = EventsSaveWindow.Unavailable("TargetStampNotAdvanced");

            foreach (var window in new[] { unavailable, null })
            {
                var changes = Classify(Entry(3, At(10)), Entry(4, At(60)), window);

                Assert.Empty(changes.Concurrent);
                Assert.Equal("SaveWindowUnavailable", Assert.Single(changes.Blocking).Reason);
            }
        }

        [Fact]
        public void TheTargetIsNeverReported_AndAnUnchangedObjectIsNotEither()
        {
            var changes = EventsOtherObjectChanges.Classify(
                new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(7, At(50), "Panel"), [Other] = Entry(3, At(10)) },
                new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(99, At(500), "Renamed"), [Other] = Entry(3, At(10)) },
                Target, Closed());

            Assert.Empty(changes.Blocking);
            Assert.Empty(changes.Concurrent);
        }

        [Fact]
        public void WhatCountsAsDifferent_IsStillTheInventoryFingerprint_NotTheAuthor()
        {
            // Only the author differs: the audit has never compared it, so the object is not a change.
            var changes = Classify(Entry(3, At(10), by: "dev-a"), Entry(3, At(10), by: "dev-b"), Closed());

            Assert.Empty(changes.Blocking);
            Assert.Empty(changes.Concurrent);
        }

        [Fact]
        public void ChangesAreOrderedByNameThenGuid_SoTheReceiptIsStable()
        {
            var changes = EventsOtherObjectChanges.Classify(
                new Dictionary<Guid, EventsInventoryEntry>
                {
                    [Target] = Entry(7, At(50), "Panel"), [Other] = Entry(3, At(10), "Zeta"), [Second] = Entry(3, At(10), "Alpha")
                },
                new Dictionary<Guid, EventsInventoryEntry>
                {
                    [Target] = Entry(8, At(102), "Panel"), [Other] = Entry(4, At(60), "Zeta"), [Second] = Entry(4, At(61), "Alpha")
                },
                Target, Closed());

            Assert.Equal(new[] { Second, Other }, changes.Concurrent.Select(c => c.Guid).ToArray());
        }

        [Fact]
        public void ChangeDetails_NameTheObjectItsVersionsItsStampsAndItsAuthor()
        {
            var changes = Classify(Entry(3, At(10), "Billing", by: "dev-a"), Entry(4, At(190), "Billing", by: "dev-b"), Closed());

            JObject json = Assert.Single(changes.ConcurrentDetails()).ToObject<JObject>();
            Assert.Equal(Other.ToString(), json["guid"]!.ToString());
            Assert.Equal("Billing", json["name"]!.ToString());
            Assert.Equal("Procedure", json["type"]!.ToString());
            Assert.Equal("modified", json["change"]!.ToString());
            Assert.Equal("concurrent", json["verdict"]!.ToString());
            Assert.Equal("AfterSaveWindow", json["reason"]!.ToString());
            Assert.Equal(3, json["versionBefore"]!.Value<int>());
            Assert.Equal(4, json["versionAfter"]!.Value<int>());
            Assert.Equal("dev-b", json["lastModifiedBy"]!.ToString());
            Assert.Equal(new DateTime(At(10), DateTimeKind.Utc), ParseUtc(json["lastUpdateBefore"]!.ToString()));
            Assert.Equal(new DateTime(At(190), DateTimeKind.Utc), ParseUtc(json["lastUpdateAfter"]!.ToString()));
        }

        private static DateTime ParseUtc(string iso)
            => DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

        [Fact]
        public void BlockingGuids_ListsOnlyTheObjectsThatCouldNotBeCleared()
        {
            var changes = EventsOtherObjectChanges.Classify(
                new Dictionary<Guid, EventsInventoryEntry>
                {
                    [Target] = Entry(7, At(50), "Panel"), [Other] = Entry(3, At(10)), [Second] = Entry(3, At(10), "Other")
                },
                new Dictionary<Guid, EventsInventoryEntry>
                {
                    [Target] = Entry(8, At(102), "Panel"), [Other] = Entry(4, At(190)), [Second] = Entry(4, At(130), "Other")
                },
                Target, Closed());

            Assert.Equal(new[] { Second.ToString() }, changes.BlockingGuids().ToObject<string[]>());
            Assert.Equal(new[] { Other }, changes.Concurrent.Select(c => c.Guid).ToArray());
        }

        [Fact]
        public void ChangeDetails_AreCapped_WhileTheListsKeepEveryChange()
        {
            var before = new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(7, At(50), "Panel") };
            var after = new Dictionary<Guid, EventsInventoryEntry> { [Target] = Entry(8, At(102), "Panel") };
            for (int i = 0; i < EventsOtherObjectChanges.MaxDescribed + 5; i++)
            {
                var id = Guid.NewGuid();
                before[id] = Entry(1, At(10), "Obj" + i.ToString("D3"));
                after[id] = Entry(2, At(190), "Obj" + i.ToString("D3"));
            }

            var changes = EventsOtherObjectChanges.Classify(before, after, Target, Closed());

            Assert.Equal(EventsOtherObjectChanges.MaxDescribed + 5, changes.Concurrent.Count);
            Assert.Equal(EventsOtherObjectChanges.MaxDescribed, changes.ConcurrentDetails().Count);
        }
    }
}
