using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #373: the lite walk wrote the whole accumulated snapshot every 2000 objects,
    /// so a walk of N objects wrote about O(N^2 / C) bytes - many times the final index
    /// size, all on the indexing path. The interval now doubles after each write.
    ///
    /// <para>
    /// The growth property is asserted here directly instead of through a walk that would
    /// need a live SDK enumerator, and a byte-counting model of the writer shows the total
    /// scales linearly with the walk length.
    /// </para>
    /// </summary>
    public class LiteCheckpointScheduleTests
    {
        /// <summary>
        /// Bytes a walk of <paramref name="n"/> objects writes, modelling each checkpoint
        /// as serializing everything accumulated so far (what WriteLiteWalkCheckpoint does)
        /// at a fixed per-entry size.
        /// </summary>
        private static long TotalCheckpointBytes(int n)
        {
            var schedule = new LiteCheckpointSchedule();
            const long bytesPerEntry = 256;
            long total = 0;
            for (long processed = 1; processed <= n; processed++)
            {
                if (!schedule.IsDue(processed)) continue;
                total += processed * bytesPerEntry;
                schedule.Record();
            }
            return total;
        }

        [Fact]
        public void Checkpoint_Total_Bytes_Grow_Linearly_Not_Quadratically()
        {
            long small = TotalCheckpointBytes(10_000);
            long large = TotalCheckpointBytes(40_000);

            Assert.True(small > 0, "the model must actually write checkpoints");
            // 4x the objects must cost far less than 16x the bytes. A fixed interval
            // gives ~16x here, which is the defect.
            double ratio = (double)large / small;
            Assert.True(ratio < 8, $"checkpoint bytes grew {ratio:F1}x when the walk grew 4x");
        }

        [Fact]
        public void A_Fixed_Interval_Would_Be_Quadratic_By_The_Same_Measure()
        {
            // The control: the same measure against the previous fixed-interval policy,
            // so the guard above is discriminating rather than trivially satisfied.
            long Fixed(int n)
            {
                const long bytesPerEntry = 256;
                long total = 0;
                for (long processed = 2000; processed <= n; processed += 2000)
                    total += processed * bytesPerEntry;
                return total;
            }

            double ratio = (double)Fixed(40_000) / Fixed(10_000);
            Assert.True(ratio > 10, $"the fixed-interval control only grew {ratio:F1}x, so the model is not sensitive");
        }

        [Fact]
        public void The_Interval_Doubles_After_Each_Checkpoint()
        {
            var schedule = new LiteCheckpointSchedule();

            Assert.Equal(2000, schedule.Interval);
            Assert.True(schedule.IsDue(1999) == false);
            Assert.True(schedule.IsDue(2000));

            schedule.Record();
            Assert.Equal(4000, schedule.Interval);
            Assert.Equal(4000, schedule.NextAt);

            schedule.Record();
            Assert.Equal(8000, schedule.Interval);
            Assert.Equal(8000, schedule.NextAt);

            // 2000, 4000, 8000, ... - O(log N) writes over a walk of N objects, each
            // covering twice the previous span, which is what bounds the total bytes.
            Assert.Equal(2, schedule.Writes);
        }

        [Fact]
        public void A_Deeper_Resume_Continues_The_Doubling()
        {
            // Resuming at 20k must not re-checkpoint the same state on the first flush.
            var schedule = new LiteCheckpointSchedule(alreadyProcessed: 20_000);

            Assert.False(schedule.IsDue(20_500));
            Assert.True(schedule.IsDue(40_000));
        }

        [Fact]
        public void A_Resumed_Walk_Writes_A_Logarithmic_Number_Of_Checkpoints()
        {
            int WritesFor(int n)
            {
                var schedule = new LiteCheckpointSchedule();
                while (schedule.NextAt <= n) schedule.Record();
                return (int)schedule.Writes;
            }

            int small = WritesFor(10_000);
            int large = WritesFor(40_000);

            Assert.Equal(3, small);   // 2000, 4000, 8000
            Assert.Equal(5, large);  // + 16000, 32000
            // The fixed interval wrote 5 and 20 for the same walks.
            Assert.True(large - small <= 2, "four times the walk must not add many checkpoints");
        }

        [Fact]
        public void The_Interval_Is_Capped_So_A_Long_Walk_Cannot_Overflow()
        {
            var schedule = new LiteCheckpointSchedule();
            for (int i = 0; i < 200; i++)
            {
                if (!schedule.IsDue(schedule.NextAt)) break;
                schedule.Record();
            }

            Assert.True(schedule.Interval <= LiteCheckpointSchedule.MaxInterval);
            Assert.True(schedule.NextAt > 0);
        }

        [Fact]
        public void The_Walk_Uses_The_Schedule_Rather_Than_A_Modulo()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "KbService.cs");

            Assert.Contains("new GxMcp.Worker.Helpers.LiteCheckpointSchedule(_lastResumedFrom)", source, StringComparison.Ordinal);
            Assert.Contains("checkpointSchedule.IsDue(cumulative)", source, StringComparison.Ordinal);
            Assert.Contains("checkpointSchedule.Record()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("checkpointInterval", source, StringComparison.Ordinal);
        }
    }
}
