using System;
using System.Threading;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Friction 2026-05-22: parallel genexus_edit calls on the same target raced
    // — the first applied, the rest hit "Context block not found". WriteService
    // now tracks per-target write timestamps so PatchService can distinguish
    // "match truly absent" from "file modified during patch".
    public class ConcurrentWriteTrackerTests
    {
        [Fact]
        public void NotePerTargetWrite_ThenWasTargetWrittenSince_BeforeStamp_ReturnsTrue()
        {
            string target = "TgtA_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var entered = DateTime.UtcNow;
            Thread.Sleep(5);
            WriteService.NotePerTargetWrite(target);

            Assert.True(WriteService.WasTargetWrittenSince(target, entered));
        }

        [Fact]
        public void WasTargetWrittenSince_NoWriteRecorded_ReturnsFalse()
        {
            string target = "TgtB_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Assert.False(WriteService.WasTargetWrittenSince(target, DateTime.UtcNow));
        }

        [Fact]
        public void WasTargetWrittenSince_StampAfterWrite_ReturnsFalse()
        {
            string target = "TgtC_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            WriteService.NotePerTargetWrite(target);
            Thread.Sleep(20);
            var entered = DateTime.UtcNow;

            Assert.False(WriteService.WasTargetWrittenSince(target, entered));
        }

        [Fact]
        public void StampPerTargetWrite_UpdatesTimestampWithoutMarkingDirty()
        {
            string target = "TgtStamp_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var entered = DateTime.UtcNow;
            Thread.Sleep(5);

            WriteService.StampPerTargetWrite(target);

            Assert.True(WriteService.WasTargetWrittenSince(target, entered));
            Assert.DoesNotContain(target, EditDirtyTracker.GetDirty(null));
        }

        [Fact]
        public void AcquirePerTargetLock_SameTarget_SameInstance()
        {
            string target = "TgtD_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var a = WriteService.AcquirePerTargetLock(target);
            var b = WriteService.AcquirePerTargetLock(target);
            Assert.Same(a, b);
        }

        [Fact]
        public void AcquirePerTargetLock_DifferentTargets_DifferentInstances()
        {
            var a = WriteService.AcquirePerTargetLock("TgtE_one");
            var b = WriteService.AcquirePerTargetLock("TgtE_two");
            Assert.NotSame(a, b);
        }
    }
}
