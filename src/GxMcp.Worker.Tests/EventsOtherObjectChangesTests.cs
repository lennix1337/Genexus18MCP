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
        // stamped 102 s by the same clock. The window is then 97 s (100 - 1 - 2 margin) to 163 s (160 + 1 + 2).
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
            Assert.Equal(At(97), window.LowTicks);
            Assert.Equal(At(163), window.HighTicks);
            Assert.Equal(0, window.OffsetTicks);
        }

        [Fact]
        public void Window_FromTheLocalWallClockLabelledUtc_ShiftsWithThatOffset()
        {
            // The SDK stamps in a clock that is the Worker's local time: the target's stamp sits three hours before.
            var window = EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(102) + LocalOffset, LocalOffset);

            Assert.True(window.Calibrated);
            Assert.Equal(At(97) + LocalOffset, window.LowTicks);
            Assert.Equal(At(163) + LocalOffset, window.HighTicks);
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
            Assert.Equal(At(97), window.LowTicks);
            Assert.Null(window.HighTicks);
        }

        [Fact]
        public void Window_Describe_SaysWhetherItIsCalibratedAndWhyNot()
        {
            var calibrated = Closed().Describe();
            Assert.True(calibrated["calibrated"]!.Value<bool>());
            Assert.Equal(0d, calibrated["stampClockOffsetSeconds"]!.Value<double>());
            Assert.Equal(66d, calibrated["seconds"]!.Value<double>());
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

            Assert.Equal("InsideSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(97)), window)));
            Assert.Equal("BeforeSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(97) - 1), window)));
            Assert.Equal("InsideSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(163)), window)));
            Assert.Equal("AfterSaveWindow", OnlyReason(Classify(Entry(3, At(10)), Entry(4, At(163) + 1), window)));
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
