using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The protected Events save (requireObjectSave=true) when ANOTHER process, for example a developer with the IDE
    // open, saves a DIFFERENT object while the save runs. The real flow runs over a double of the SDK half; the
    // classification and the verdict are the production ones. Every instant is a number of seconds from a fixed
    // origin: the transaction is open from 100 s to 160 s and the target is stamped 102 s, so the save window is
    // 97 s to 163 s (see EventsOtherObjectChangesTests).
    public sealed class EventsSaveConcurrentEditTests
    {
        private static readonly long Origin = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc).Ticks;
        private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid Bystander = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

        private static long At(double seconds) => Origin + TimeSpan.FromSeconds(seconds).Ticks;

        private static EventsInventoryEntry Entry(int version, long stamp, string name = "Procedure01")
            => new EventsInventoryEntry(version, stamp, "root", name, "type-p", "Procedure", "dev");

        private static Dictionary<Guid, EventsInventoryEntry> Inventory(EventsInventoryEntry target, EventsInventoryEntry other)
            => new Dictionary<Guid, EventsInventoryEntry>
            {
                [Target] = target, [Other] = other, [Bystander] = Entry(5, At(20), "Bystander01")
            };

        private static readonly EventsInventoryEntry TargetBefore = Entry(7, At(50), "Panel");
        private static readonly EventsInventoryEntry TargetAfter = Entry(8, At(102), "Panel");
        private static readonly EventsInventoryEntry OtherUntouched = Entry(3, At(10));

        private static EventsSaveWindow OpenWindow()
            => EventsSaveWindow.Calibrate(At(100), null, At(101), At(103), At(102), TimeSpan.FromHours(-3).Ticks);

        private static EventsSaveWindow ClosedWindow()
            => EventsSaveWindow.Calibrate(At(100), At(160), At(101), At(103), At(102), TimeSpan.FromHours(-3).Ticks);

        // The SDK half of the save cannot run offline. This double reads three inventories it was given and turns
        // them into the receipt through the production classification and verdict; only the glue is its own.
        private sealed class InventoryAuditedSteps : IIsolatedEventsSaveSteps
        {
            private readonly JObject _receipt;
            private readonly Dictionary<Guid, EventsInventoryEntry> _baseline;
            private readonly Dictionary<Guid, EventsInventoryEntry> _preCommit;
            private readonly Dictionary<Guid, EventsInventoryEntry> _final;
            private readonly EventsSaveWindow _preCommitWindow;
            private readonly EventsSaveWindow _finalWindow;

            internal readonly List<string> Calls = new List<string>();
            internal bool FailObjectSave;

            internal InventoryAuditedSteps(JObject receipt, Dictionary<Guid, EventsInventoryEntry> baseline,
                Dictionary<Guid, EventsInventoryEntry> preCommit, Dictionary<Guid, EventsInventoryEntry> final,
                EventsSaveWindow preCommitWindow, EventsSaveWindow finalWindow)
            {
                _receipt = receipt;
                _baseline = baseline;
                _preCommit = preCommit;
                _final = final;
                _preCommitWindow = preCommitWindow;
                _finalWindow = finalWindow;
            }

            public string OriginalToken => "token-1";
            public bool WorkerRestartRequired => false;
            public bool Verified { get; private set; }
            public string UnconfirmedMessage { get; private set; }

            public void ReadOriginal() => Calls.Add("ReadOriginal");
            public void CaptureBaseline() => Calls.Add("CaptureBaseline");
            public void BeginTransaction() => Calls.Add("BeginTransaction");
            public string ReadFreshInTransaction() { Calls.Add("ReadFreshInTransaction"); return "token-1"; }
            public void RecheckTarget() => Calls.Add("RecheckTarget");
            public void AssignSource() { Calls.Add("AssignSource"); _receipt["writeAttempted"] = true; }
            public void SavePart() => Calls.Add("SavePart");

            public void SaveObject()
            {
                Calls.Add("SaveObject");
                if (FailObjectSave) throw new InvalidOperationException("object save refused");
            }

            public void EndSaveIsolation() => Calls.Add("EndSaveIsolation");

            public void VerifyPreCommit()
            {
                Calls.Add("VerifyPreCommit");
                var others = EventsOtherObjectChanges.Classify(_baseline, _preCommit, Target, _preCommitWindow);
                IsolatedEventsSaveVerdict.RecordPreCommitOthers(_receipt, others);
                string mismatch = IsolatedEventsSaveDiagnostics.DescribePreCommitMismatch(
                    eventsSourceMatches: true, changedParts: null, partChangesAllowed: true,
                    otherObjectsChanged: others.Blocking.Count, placementMatches: true);
                if (mismatch != null) throw new InvalidOperationException(mismatch);
            }

            public void Commit() => Calls.Add("Commit");
            public void Rollback() => Calls.Add("Rollback");
            public void EndTransaction() => Calls.Add("EndTransaction");
            public void RereadAfterCommit() => Calls.Add("RereadAfterCommit");

            public void VerifyAfterCommit(bool committed)
            {
                Calls.Add("VerifyAfterCommit");
                var others = EventsOtherObjectChanges.Classify(_baseline, _final, Target, _finalWindow);
                _receipt["persisted"] = committed;
                _receipt["saved"] = committed;
                _receipt["verificationCompleted"] = true;
                Verified = IsolatedEventsSaveVerdict.Apply(
                    _receipt,
                    new IsolatedEventsTargetEvidence
                    {
                        Committed = committed, PartPersisted = committed, MetadataAdvanced = committed,
                        OtherPartsIntact = true, RestoredToOriginal = !committed
                    },
                    others, _finalWindow, out string message);
                UnconfirmedMessage = message;
            }

            public IEnumerable<KeyValuePair<string, object>> SdkMessageSources() => Enumerable.Empty<KeyValuePair<string, object>>();
            public void Dispose() { }
        }

        private sealed class Run
        {
            internal readonly JObject Receipt = new JObject();
            internal readonly InventoryAuditedSteps Steps;
            internal readonly IsolatedEventsSaveOutcome Outcome;

            // preCommit and final describe the other object as the pre-commit and the post-commit inventories read it.
            internal Run(EventsInventoryEntry otherAtPreCommit, EventsInventoryEntry otherAtFinal,
                EventsSaveWindow preCommitWindow, EventsSaveWindow finalWindow, bool failObjectSave = false)
            {
                Steps = new InventoryAuditedSteps(Receipt,
                    Inventory(TargetBefore, OtherUntouched),
                    Inventory(TargetAfter, otherAtPreCommit),
                    Inventory(failObjectSave ? TargetBefore : TargetAfter, otherAtFinal),
                    preCommitWindow, finalWindow) { FailObjectSave = failObjectSave };
                var trace = new IsolatedEventsSaveTrace(Receipt, "SamplePanel");
                Outcome = IsolatedEventsSaveFlow.Run(Steps, trace, "token-1", () => new NoopGate());
            }

            private sealed class NoopGate : IDisposable { public void Dispose() { } }
        }

        [Fact]
        public void Control_NoOtherObjectChanged_IsVerified()
        {
            var run = new Run(OtherUntouched, OtherUntouched, OpenWindow(), ClosedWindow());

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Verified, run.Outcome.Kind);
            Assert.True(run.Receipt["verified"]!.Value<bool>());
            Assert.True(run.Receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Null(run.Receipt["concurrentChanges"]);
            Assert.Empty((JArray)run.Receipt["preCommitUnexpectedChangedObjects"]!);
            Assert.Null(run.Receipt["preCommitUnexpectedChangedObjectDetails"]);
            Assert.Contains("Commit", run.Steps.Calls);
            Assert.DoesNotContain("Rollback", run.Steps.Calls);
        }

        [Fact]
        public void EditByAnotherProcessAfterTheSave_IsReported_AndTheSaveStaysVerified()
        {
            // The case seen on a large KB: the Events source was written, read back and the sibling parts were intact,
            // then a developer saved another object while the post-commit inventory was running.
            var run = new Run(OtherUntouched, Entry(4, At(190)), OpenWindow(), ClosedWindow());

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Verified, run.Outcome.Kind);
            Assert.True(run.Receipt["verified"]!.Value<bool>());
            Assert.True(run.Receipt["persisted"]!.Value<bool>());
            Assert.True(run.Receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Empty((JArray)run.Receipt["unexpectedChangedObjects"]!);
            var change = (JObject)Assert.Single((JArray)run.Receipt["concurrentChanges"]!);
            Assert.Equal(Other.ToString(), change["guid"]!.ToString());
            Assert.Equal("AfterSaveWindow", change["reason"]!.ToString());
            Assert.DoesNotContain("Rollback", run.Steps.Calls);
        }

        [Fact]
        public void EditByAnotherProcessBeforeTheTransaction_DoesNotRollBackTheSave()
        {
            // The object was read by the first inventory, then edited before the transaction opened: the pre-commit
            // inventory sees it already, and it is not an effect of the save.
            var run = new Run(Entry(4, At(60)), Entry(4, At(60)), OpenWindow(), ClosedWindow());

            Assert.Equal(IsolatedEventsSaveOutcomeKind.Verified, run.Outcome.Kind);
            Assert.Contains("Commit", run.Steps.Calls);
            Assert.DoesNotContain("Rollback", run.Steps.Calls);
            Assert.Equal("BeforeSaveWindow", run.Receipt["concurrentChanges"]![0]!["reason"]!.ToString());
        }

        [Fact]
        public void EditInsideTheTransactionSeenBeforeTheCommit_RollsBackTheEvents()
        {
            var edited = Entry(4, At(130));
            var run = new Run(edited, edited, OpenWindow(), ClosedWindow());

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, run.Outcome.Kind);
            Assert.Equal("pre-commit-verification", run.Outcome.Phase);
            Assert.Equal(
                "Isolated Events save failed during pre-commit-verification: The pre-commit SDK object state did not match "
                + "the requested isolated change: 1 other object(s) in the Knowledge Base changed (see preCommitUnexpectedChangedObjectDetails).",
                run.Outcome.Message);
            Assert.DoesNotContain("Commit", run.Steps.Calls);
            Assert.Contains("Rollback", run.Steps.Calls);
            Assert.True(run.Receipt["transactionRolledBack"]!.Value<bool>());
            Assert.False(run.Receipt["persisted"]!.Value<bool>());
            Assert.False(run.Receipt["rollbackVerified"]!.Value<bool>());
            Assert.True(WriteService.ShouldMarkIsolatedEventsDirty(run.Receipt));
            // The change is still there after the rollback: the final fields and the pre-commit ones agree.
            Assert.Equal(new[] { Other.ToString() }, run.Receipt["unexpectedChangedObjects"]!.ToObject<string[]>());
            Assert.Equal(new[] { Other.ToString() }, run.Receipt["preCommitUnexpectedChangedObjects"]!.ToObject<string[]>());
            Assert.Equal(Other.ToString(), run.Receipt["unexpectedChangedObjectDetails"]![0]!["guid"]!.ToString());
            Assert.Equal(Other.ToString(), run.Receipt["preCommitUnexpectedChangedObjectDetails"]![0]!["guid"]!.ToString());
        }

        [Fact]
        public void EditInsideTheTransactionUndoneByTheRollback_StillNamesTheObjectThatCausedTheRefusal()
        {
            // The other object's change belonged to the transaction: the rollback removed it, so the inventory read
            // after the rollback is clean. The refusal must still say which object it was and what changed in it.
            var run = new Run(Entry(4, At(130)), OtherUntouched, OpenWindow(), ClosedWindow());

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, run.Outcome.Kind);
            Assert.Equal("pre-commit-verification", run.Outcome.Phase);
            Assert.DoesNotContain("Commit", run.Steps.Calls);
            Assert.Contains("Rollback", run.Steps.Calls);
            Assert.Contains("preCommitUnexpectedChangedObjectDetails", run.Outcome.Message);

            // What the final inventory proves keeps its meaning.
            Assert.True(run.Receipt["rollbackVerified"]!.Value<bool>());
            Assert.True(run.Receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Empty((JArray)run.Receipt["unexpectedChangedObjects"]!);
            Assert.Null(run.Receipt["unexpectedChangedObjectDetails"]);
            Assert.False(WriteService.ShouldMarkIsolatedEventsDirty(run.Receipt));

            // What the pre-commit inventory showed survives it.
            Assert.Equal(new[] { Other.ToString() }, run.Receipt["preCommitUnexpectedChangedObjects"]!.ToObject<string[]>());
            var cause = (JObject)Assert.Single((JArray)run.Receipt["preCommitUnexpectedChangedObjectDetails"]!);
            Assert.Equal(Other.ToString(), cause["guid"]!.ToString());
            Assert.Equal("Procedure01", cause["name"]!.ToString());
            Assert.Equal("modified", cause["change"]!.ToString());
            Assert.Equal("blocking", cause["verdict"]!.ToString());
            Assert.Equal("InsideSaveWindow", cause["reason"]!.ToString());
            Assert.Equal(3, cause["versionBefore"]!.Value<int>());
            Assert.Equal(4, cause["versionAfter"]!.Value<int>());
        }

        [Fact]
        public void EditInsideTheTransactionSeenOnlyAfterTheCommit_FailsTheOperation_AndSaysTheTargetIsVerified()
        {
            var run = new Run(OtherUntouched, Entry(4, At(130)), OpenWindow(), ClosedWindow());

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, run.Outcome.Kind);
            Assert.Equal("post-commit-verification", run.Outcome.Phase);
            Assert.StartsWith("The Events source was committed and read back", run.Outcome.Message);
            Assert.Contains("1 other object(s)", run.Outcome.Message);
            Assert.NotEqual(IsolatedEventsSaveFlow.NotConfirmedMessage, run.Outcome.Message);
            Assert.Contains("Commit", run.Steps.Calls);
            Assert.DoesNotContain("Rollback", run.Steps.Calls);
            Assert.True(run.Receipt["persisted"]!.Value<bool>());
            Assert.True(run.Receipt["writeVerifiedForTarget"]!.Value<bool>());
            Assert.False(run.Receipt["verified"]!.Value<bool>());
            Assert.False(run.Receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Equal(new[] { Other.ToString() }, run.Receipt["unexpectedChangedObjects"]!.ToObject<string[]>());
            Assert.Equal(IsolatedEventsSaveVerdict.OtherObjectsChangedCode, run.Receipt["reasonCode"]!.ToString());
            Assert.True(WriteService.ShouldMarkIsolatedEventsDirty(run.Receipt));
        }

        [Fact]
        public void WithoutACalibratedWindow_AnEditAfterTheSaveStillFailsTheOperation_AsItAlwaysDid()
        {
            var unavailable = EventsSaveWindow.Unavailable("TargetStampNotAdvanced");
            var run = new Run(OtherUntouched, Entry(4, At(190)), unavailable, unavailable);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, run.Outcome.Kind);
            Assert.Equal("post-commit-verification", run.Outcome.Phase);
            Assert.False(run.Receipt["otherObjectMetadataIntact"]!.Value<bool>());
            Assert.Equal("SaveWindowUnavailable", run.Receipt["unexpectedChangedObjectDetails"]![0]!["reason"]!.ToString());
            Assert.Null(run.Receipt["concurrentChanges"]);
        }

        [Fact]
        public void ObjectCreatedByAnotherProcess_FailsTheOperation_EvenOutsideTheWindow()
        {
            var steps = new JObject();
            var baseline = Inventory(TargetBefore, OtherUntouched);
            var final = Inventory(TargetAfter, OtherUntouched);
            final[Guid.Parse("00000000-0000-0000-0000-0000000000d4")] = Entry(1, At(190), "Created01");
            var fake = new InventoryAuditedSteps(steps, baseline, Inventory(TargetAfter, OtherUntouched), final, OpenWindow(), ClosedWindow());

            var outcome = IsolatedEventsSaveFlow.Run(fake, new IsolatedEventsSaveTrace(steps, "SamplePanel"), "token-1", () => new NoopGate());

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, outcome.Kind);
            Assert.Equal("Created", steps["unexpectedChangedObjectDetails"]![0]!["reason"]!.ToString());
            Assert.Null(steps["concurrentChanges"]);
        }

        private sealed class NoopGate : IDisposable { public void Dispose() { } }

        [Fact]
        public void RolledBackSave_WhileAnotherProcessEditedOutsideTheWindow_StillHasAVerifiedRollback()
        {
            var run = new Run(Entry(4, At(60)), Entry(4, At(60)), OpenWindow(), ClosedWindow(), failObjectSave: true);

            Assert.Equal(IsolatedEventsSaveOutcomeKind.NotConfirmed, run.Outcome.Kind);
            Assert.Equal("object-save", run.Outcome.Phase);
            Assert.DoesNotContain("Commit", run.Steps.Calls);
            Assert.False(run.Receipt["persisted"]!.Value<bool>());
            Assert.True(run.Receipt["rollbackVerified"]!.Value<bool>());
            Assert.False(WriteService.ShouldMarkIsolatedEventsDirty(run.Receipt));
        }

        [Fact]
        public void RolledBackSave_WhileAnotherObjectChangedInsideTheWindow_HasNoVerifiedRollback()
        {
            var edited = Entry(4, At(130));
            var run = new Run(edited, edited, OpenWindow(), ClosedWindow(), failObjectSave: true);

            Assert.Equal("object-save", run.Outcome.Phase);
            Assert.False(run.Receipt["rollbackVerified"]!.Value<bool>());
            Assert.True(WriteService.ShouldMarkIsolatedEventsDirty(run.Receipt));
        }

        [Fact]
        public void DirtyTracking_KeepsACommittedSaveDirty_WhenAnotherObjectCouldNotBeCleared()
        {
            var committedWithOtherChange = JObject.Parse(
                "{writeAttempted:true, verificationCompleted:true, objectSaved:true, partPersisted:true, otherPartsIntact:true, "
                + "otherObjectMetadataIntact:false, sourceChanged:false}");
            var committedClean = JObject.Parse(
                "{writeAttempted:true, verificationCompleted:true, objectSaved:true, partPersisted:true, otherPartsIntact:true, "
                + "otherObjectMetadataIntact:true, sourceChanged:false}");

            Assert.True(WriteService.ShouldMarkIsolatedEventsDirty(committedWithOtherChange));
            Assert.False(WriteService.ShouldMarkIsolatedEventsDirty(committedClean));
        }
    }
}
