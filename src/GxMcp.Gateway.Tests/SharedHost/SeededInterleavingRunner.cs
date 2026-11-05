#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // No-dependency property test runner (decision D11). Each case gets its own System.Random seed, derived from a
    // base seed. A failing case reports its case seed and the events it recorded; setting GXMCP_PBT_SEED to that
    // seed runs exactly that one case again, with the same generated values and the same events.
    // There is no shrinking, so generators should prefer small sizes (see PbtCase.SmallInt).
    internal static class SeededInterleavingRunner
    {
        internal const string SeedEnvironmentVariable = "GXMCP_PBT_SEED";
        internal const int DefaultIterations = 100;

        // Upper bound on the event lines printed in a failure message; the rest are summarized as a count.
        internal const int MaxReportedEvents = 400;

        internal static void Run(string propertyName, int iterations, Action<PbtCase> property)
        {
            var replaySeed = ParseSeed(Environment.GetEnvironmentVariable(SeedEnvironmentVariable));
            Run(propertyName, iterations, property, replaySeed, baseSeed: null);
        }

        // Test seam: replaySeed and baseSeed are passed explicitly so the runner's own tests do not mutate the
        // process environment, which xUnit test classes share.
        internal static void Run(string propertyName, int iterations, Action<PbtCase> property, int? replaySeed, int? baseSeed)
        {
            if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentException("A property name is required.", nameof(propertyName));
            if (property == null) throw new ArgumentNullException(nameof(property));
            if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "At least one iteration is required.");

            if (replaySeed.HasValue)
            {
                RunCase(propertyName, property, replaySeed.Value, iteration: 1, iterations: 1, replay: true);
                return;
            }

            var seedSource = new Random(baseSeed ?? Environment.TickCount);
            for (var i = 1; i <= iterations; i++)
            {
                RunCase(propertyName, property, seedSource.Next(), i, iterations, replay: false);
            }
        }

        internal static int? ParseSeed(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (int.TryParse(value!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)) return seed;
            throw new ArgumentException(
                SeedEnvironmentVariable + " must be a 32-bit integer seed reported by a failing property test; got '" + value + "'.");
        }

        private static void RunCase(string propertyName, Action<PbtCase> property, int seed, int iteration, int iterations, bool replay)
        {
            var testCase = new PbtCase(seed);
            try
            {
                property(testCase);
            }
            catch (Exception ex)
            {
                throw new PropertyFailedException(BuildFailureMessage(propertyName, seed, iteration, iterations, replay, testCase.Events, ex), seed, ex);
            }
        }

        private static string BuildFailureMessage(string propertyName, int seed, int iteration, int iterations, bool replay,
            IReadOnlyList<string> events, Exception failure)
        {
            var sb = new StringBuilder();
            sb.Append("Property '").Append(propertyName).Append("' failed");
            sb.Append(replay ? " on replay" : " at case " + iteration.ToString(CultureInfo.InvariantCulture) + " of " + iterations.ToString(CultureInfo.InvariantCulture));
            sb.Append(".\nSeed: ").Append(seed.ToString(CultureInfo.InvariantCulture));
            sb.Append("\nReplay: set ").Append(SeedEnvironmentVariable).Append('=').Append(seed.ToString(CultureInfo.InvariantCulture)).Append(" and run this test again.");
            sb.Append("\nFailure: ").Append(failure.GetType().Name).Append(": ").Append(failure.Message);
            sb.Append("\nEvents (").Append(events.Count.ToString(CultureInfo.InvariantCulture)).Append("):");
            var shown = Math.Min(events.Count, MaxReportedEvents);
            for (var i = 0; i < shown; i++)
            {
                sb.Append("\n  ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(events[i]);
            }
            if (events.Count > shown)
            {
                sb.Append("\n  ... ").Append((events.Count - shown).ToString(CultureInfo.InvariantCulture)).Append(" more events not shown.");
            }
            return sb.ToString();
        }
    }

    // One generated case: a seeded random source plus the event log that the failure message prints.
    internal sealed class PbtCase
    {
        private readonly Random _random;
        private readonly List<string> _events = new List<string>();

        internal PbtCase(int seed)
        {
            Seed = seed;
            _random = new Random(seed);
        }

        internal int Seed { get; }

        internal IReadOnlyList<string> Events => _events;

        internal void Log(string evt) => _events.Add(evt ?? string.Empty);

        // Uniform integer in [min, max], both inclusive.
        internal int Int(int min, int max)
        {
            if (max < min) throw new ArgumentOutOfRangeException(nameof(max), max, "max must be at least min.");
            return min + (int)(_random.NextDouble() * ((long)max - min + 1));
        }

        // Integer in [min, max] biased toward min: the lower of two uniform draws. Without shrinking, small
        // Session and request counts keep failing cases short enough to read.
        internal int SmallInt(int min, int max) => Math.Min(Int(min, max), Int(min, max));

        internal bool Bool() => _random.Next(2) == 1;

        // True with the given probability in [0, 1].
        internal bool Chance(double probability)
        {
            if (probability < 0 || probability > 1) throw new ArgumentOutOfRangeException(nameof(probability), probability, "probability must be in [0, 1].");
            return _random.NextDouble() < probability;
        }

        internal T Pick<T>(IReadOnlyList<T> items)
        {
            if (items == null || items.Count == 0) throw new ArgumentException("Cannot pick from an empty list.", nameof(items));
            return items[_random.Next(items.Count)];
        }
    }

    internal sealed class PropertyFailedException : Exception
    {
        internal PropertyFailedException(string message, int seed, Exception inner) : base(message, inner)
        {
            Seed = seed;
        }

        internal int Seed { get; }
    }
}
