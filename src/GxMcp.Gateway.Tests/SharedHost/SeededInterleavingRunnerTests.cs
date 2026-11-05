using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    public class SeededInterleavingRunnerTests
    {
        [Fact]
        public void Run_ExecutesTheConfiguredNumberOfCases()
        {
            var seeds = new List<int>();

            SeededInterleavingRunner.Run("count", 37, c => seeds.Add(c.Seed), replaySeed: null, baseSeed: 12345);

            Assert.Equal(37, seeds.Count);
        }

        [Fact]
        public void Run_SameBaseSeedProducesTheSameCases()
        {
            var first = Generate(baseSeed: 99);
            var second = Generate(baseSeed: 99);

            Assert.Equal(first, second);
        }

        [Fact]
        public void Failure_ReportsSeedAndEvents_AndReplayReproducesTheSameEvents()
        {
            List<string>? failingEvents = null;

            void Property(PbtCase c)
            {
                var sessions = c.SmallInt(1, 4);
                var requests = c.SmallInt(1, 64);
                for (var i = 0; i < requests; i++) c.Log("submit s" + c.Int(1, sessions) + " r" + i);
                failingEvents = c.Events.ToList();
                if (requests >= 3) throw new InvalidOperationException("planted failure");
            }

            var ex = Assert.Throws<PropertyFailedException>(
                () => SeededInterleavingRunner.Run("planted", 1000, Property, replaySeed: null, baseSeed: 7));
            var reportedEvents = failingEvents!;

            Assert.Contains("Seed: " + ex.Seed, ex.Message);
            Assert.Contains(SeededInterleavingRunner.SeedEnvironmentVariable + "=" + ex.Seed, ex.Message);
            Assert.Contains("planted failure", ex.Message);
            Assert.Contains("1. " + reportedEvents[0], ex.Message);

            var replay = Assert.Throws<PropertyFailedException>(
                () => SeededInterleavingRunner.Run("planted", 1000, Property, replaySeed: ex.Seed, baseSeed: null));

            Assert.Equal(ex.Seed, replay.Seed);
            Assert.Equal(reportedEvents, failingEvents);
            Assert.Contains("on replay", replay.Message);
        }

        [Fact]
        public void Replay_RunsExactlyOneCaseWithTheGivenSeed()
        {
            var seeds = new List<int>();

            SeededInterleavingRunner.Run("replay", 500, c => seeds.Add(c.Seed), replaySeed: 424242, baseSeed: null);

            Assert.Equal(new[] { 424242 }, seeds);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData(" 17 ", 17)]
        [InlineData("-5", -5)]
        public void ParseSeed_AcceptsIntegersAndEmpty(string? value, int? expected)
        {
            Assert.Equal(expected, SeededInterleavingRunner.ParseSeed(value));
        }

        [Fact]
        public void ParseSeed_RejectsNonInteger()
        {
            var ex = Assert.Throws<ArgumentException>(() => SeededInterleavingRunner.ParseSeed("abc"));
            Assert.Contains(SeededInterleavingRunner.SeedEnvironmentVariable, ex.Message);
        }

        [Fact]
        public void Run_RejectsZeroIterations()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SeededInterleavingRunner.Run("zero", 0, _ => { }, replaySeed: null, baseSeed: 1));
        }

        [Fact]
        public void SmallInt_StaysInRange_ReachesBothEnds_AndIsBiasedLow()
        {
            var c = new PbtCase(2024);
            var values = Enumerable.Range(0, 20000).Select(_ => c.SmallInt(1, 64)).ToList();

            Assert.All(values, v => Assert.InRange(v, 1, 64));
            Assert.Contains(1, values);
            Assert.Contains(64, values);
            Assert.True(values.Average() < 32.5, "mean " + values.Average() + " should sit below the midpoint 32.5");
        }

        private static List<string> Generate(int baseSeed)
        {
            var output = new List<string>();
            SeededInterleavingRunner.Run("determinism", 50, c =>
            {
                output.Add(c.Seed + ":" + c.SmallInt(1, 4) + ":" + c.Int(0, 1000) + ":" + c.Bool() + ":" + c.Pick(new[] { "a", "b", "c" }));
            }, replaySeed: null, baseSeed: baseSeed);
            return output;
        }
    }
}
