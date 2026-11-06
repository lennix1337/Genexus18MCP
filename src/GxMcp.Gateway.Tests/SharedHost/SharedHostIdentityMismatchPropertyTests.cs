#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 12: Identity mismatch reports the differing components
    //
    // For any two identities with the same KB directory, the mismatch lists exactly the components whose normalized
    // values differ, each with its requested and live value, in the fixed component order. Case-only and
    // trailing-separator differences in paths, and case-only and surrounding-whitespace differences in the driver
    // and major tokens, are not differences. Identities of different KB directories are rejected.
    //
    // **Validates: Requirements 2.5**
    //
    // Uses only the contract pinned in the header of SharedWorkerRegistryV2Tests.cs (task 4.5):
    // SharedWorkerIdentity.Create, KbKey, ToRecordIdentity, BuildMismatch, SharedWorkerIdentityDifference and
    // SharedHostRecordIdentity. The SHARED_WORKER_IDENTITY_MISMATCH envelope shape (the Details key that carries the
    // differences) is not pinned by any test or design text, so the property is checked on BuildMismatch only.
    //
    // Oracle: every generated value starts from a canonical string that is already in normalized form (absolute,
    // lowercase, no trailing separator, no surrounding whitespace). A variant is derived from the canonical value by
    // construction, so the expected normalized value is the canonical value itself and the expected verdict
    // ("differs" or "equal") is the variant kind. The oracle never calls the normalization code under test.
    public class SharedHostIdentityMismatchPropertyTests
    {
        private const string PropertyName =
            "Feature: concurrent-multi-session-workers, Property 12: Identity mismatch reports the differing components";

        private const int Iterations = 200;

        // Fixed base seeds keep every run deterministic; GXMCP_PBT_SEED still replays one reported case.
        private const int MismatchBaseSeed = 1_200_012;
        private const int IdenticalBaseSeed = 1_200_013;
        private const int OtherKbBaseSeed = 1_200_014;

        // The fixed component order of Requirement 2 criterion 5 and design decision D6, restated literally.
        private static readonly string[] OracleOrder =
        {
            "installationPath", "driver", "major", "workerExecutable", "profileConfigPath"
        };

        // Neutral placeholder roots: they name no real KB, installation or Worker build.
        private static readonly string[] KbNames = { "kb-alpha", "kb-beta", "kb-gamma", "kb-delta" };
        private static readonly string[] InstallationNames = { "gx-install-a", "gx-install-b", "gx-install-c" };
        private static readonly string[] WorkerNames = { "worker-a", "worker-b", "worker-c" };
        private static readonly string[] ProfileNames = { "profile-a", "profile-b", "profile-c" };
        private static readonly string[] Drivers = { "native-sdk", "dotnet-reflection", "com-gxpublic" };
        private static readonly string[] Majors = { "18", "17", "16", "15", "ev3", "ev2" };

        private enum VariantKind
        {
            Equal,
            CaseVariant,
            TrailingSeparatorVariant,   // for driver and major tokens: surrounding whitespace instead
            Different
        }

        private enum ComponentShape { Path, Token, OptionalPath }

        // One generated component: the canonical requested and live values, the raw strings handed to the code under
        // test, and the variant kind that decides the oracle verdict.
        private sealed class GeneratedComponent
        {
            internal string Name = string.Empty;
            internal VariantKind Kind;
            internal string CanonicalRequested = string.Empty;
            internal string CanonicalLive = string.Empty;
            internal string? RawRequested;
            internal string? RawLive;
        }

        private sealed class GeneratedPair
        {
            internal string CanonicalKb = string.Empty;
            internal string RawRequestedKb = string.Empty;
            internal string RawLiveKb = string.Empty;
            internal List<GeneratedComponent> Components = new List<GeneratedComponent>();

            internal GeneratedComponent this[string name] => Components.Single(component => component.Name == name);
        }

        [Fact]
        public void Property12_ReportsExactlyTheTrulyDifferentComponentsInFixedOrderWithNormalizedValues()
        {
            Run(" (mixed variants)", MismatchBaseSeed, c =>
            {
                var pair = Generate(c, allowDifferent: true);
                var requested = CreateRequested(pair);
                var live = CreateLive(pair, pair.RawLiveKb);

                var actual = SharedWorkerIdentity.BuildMismatch(requested, live);
                LogActual(c, actual);

                var expected = pair.Components
                    .Where(component => component.Kind == VariantKind.Different)
                    .OrderBy(component => Array.IndexOf(OracleOrder, component.Name))
                    .ToList();

                Assert.Equal(
                    expected.Select(component => component.Name).ToArray(),
                    actual.Select(difference => difference.Component).ToArray());

                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].CanonicalRequested, actual[i].Requested);
                    Assert.Equal(expected[i].CanonicalLive, actual[i].Live);
                    Assert.NotEqual(actual[i].Requested, actual[i].Live);
                }
            });
        }

        [Fact]
        public void Property12_AnIdenticalIdentityYieldsAnEmptyList()
        {
            Run(" (identical identity)", IdenticalBaseSeed, c =>
            {
                var pair = Generate(c, allowDifferent: false);
                var requested = CreateRequested(pair);

                // The same identity read back from its own record, and the same identity spelled with case,
                // trailing-separator and whitespace variants: neither is a mismatch.
                var fromRecord = SharedWorkerIdentity.BuildMismatch(requested, requested.ToRecordIdentity());
                c.Log("ToRecordIdentity differences: " + fromRecord.Count.ToString(CultureInfo.InvariantCulture));
                Assert.Empty(fromRecord);

                var variants = SharedWorkerIdentity.BuildMismatch(requested, CreateLive(pair, pair.RawLiveKb));
                LogActual(c, variants);
                Assert.Empty(variants);
            });
        }

        [Fact]
        public void Property12_DifferentKbDirectoriesThrowArgumentException()
        {
            Run(" (different KB directory)", OtherKbBaseSeed, c =>
            {
                var pair = Generate(c, allowDifferent: true);
                var requested = CreateRequested(pair);

                string otherKb = pair.CanonicalKb + "-" + c.Pick(new[] { "other", "copy", "live" });
                string rawOtherKb = ApplyPathVariant(c, otherKb, PickEquivalentKind(c));
                c.Log("live KB directory: canonical=" + otherKb + " raw=" + Show(rawOtherKb));
                var live = CreateLive(pair, rawOtherKb);

                Assert.Throws<ArgumentException>(() => SharedWorkerIdentity.BuildMismatch(requested, live));
            });
        }

        // ---------------------------------------------------------------
        // Runner and logging
        // ---------------------------------------------------------------

        private static void Run(string suffix, int baseSeed, Action<PbtCase> property)
        {
            int? replaySeed = SeededInterleavingRunner.ParseSeed(
                Environment.GetEnvironmentVariable(SeededInterleavingRunner.SeedEnvironmentVariable));
            SeededInterleavingRunner.Run(PropertyName + suffix, Iterations, property, replaySeed, baseSeed);
        }

        private static void LogActual(PbtCase c, IReadOnlyList<SharedWorkerIdentityDifference> actual)
        {
            c.Log("BuildMismatch returned " + actual.Count.ToString(CultureInfo.InvariantCulture) + " entries");
            foreach (var difference in actual)
                c.Log("  " + difference.Component + ": requested=" + Show(difference.Requested) + " live=" + Show(difference.Live));
        }

        private static string Show(string? value) => value == null ? "<null>" : "'" + value + "'";

        // ---------------------------------------------------------------
        // Generators
        // ---------------------------------------------------------------

        private static GeneratedPair Generate(PbtCase c, bool allowDifferent)
        {
            var pair = new GeneratedPair { CanonicalKb = @"c:\gxmcp-pbt\kbs\" + c.Pick(KbNames) };
            pair.RawRequestedKb = ApplyPathVariant(c, pair.CanonicalKb, PickEquivalentKind(c));
            pair.RawLiveKb = ApplyPathVariant(c, pair.CanonicalKb, PickEquivalentKind(c));
            c.Log("KB directory: canonical=" + pair.CanonicalKb + " requested=" + Show(pair.RawRequestedKb) + " live=" + Show(pair.RawLiveKb));

            pair.Components.Add(GeneratePath(c, "installationPath", @"c:\gxmcp-pbt\installations\", InstallationNames, allowDifferent));
            pair.Components.Add(GenerateToken(c, "driver", Drivers, allowDifferent));
            pair.Components.Add(GenerateToken(c, "major", Majors, allowDifferent));
            pair.Components.Add(GenerateWorker(c, allowDifferent));
            pair.Components.Add(GenerateProfile(c, allowDifferent));

            foreach (var component in pair.Components)
            {
                c.Log(component.Name + ": kind=" + component.Kind
                    + " canonicalRequested=" + Show(component.CanonicalRequested)
                    + " canonicalLive=" + Show(component.CanonicalLive)
                    + " rawRequested=" + Show(component.RawRequested)
                    + " rawLive=" + Show(component.RawLive));
            }

            return pair;
        }

        private static GeneratedComponent GeneratePath(PbtCase c, string name, string root, string[] names, bool allowDifferent)
        {
            string requestedName = c.Pick(names);
            return BuildPathComponent(c, name, root + requestedName, root + PickOther(c, names, requestedName), allowDifferent);
        }

        private static GeneratedComponent GenerateWorker(PbtCase c, bool allowDifferent)
        {
            const string root = @"c:\gxmcp-pbt\workers\";
            const string leaf = @"\worker\gxmcp.worker.exe";
            string requestedName = c.Pick(WorkerNames);
            string requested = root + requestedName + leaf;
            string live = root + PickOther(c, WorkerNames, requestedName) + leaf;
            return BuildPathComponent(c, "workerExecutable", requested, live, allowDifferent);
        }

        private static GeneratedComponent GenerateProfile(PbtCase c, bool allowDifferent)
        {
            const string root = @"c:\gxmcp-pbt\profiles\";
            bool requestedAbsent = c.Chance(0.25);
            var kind = PickKind(c, allowDifferent);

            if (!requestedAbsent)
            {
                string requestedName = c.Pick(ProfileNames);
                string requested = root + requestedName + @"\config.json";
                string different = c.Chance(0.25)
                    ? string.Empty
                    : root + PickOther(c, ProfileNames, requestedName) + @"\config.json";
                var component = BuildPathComponent(c, "profileConfigPath", requested, different, kind);
                // A truly different live profile may be absent: null in the record means "" after normalization.
                if (component.Kind == VariantKind.Different && component.CanonicalLive.Length == 0)
                    component.RawLive = c.Bool() ? null : string.Empty;
                return component;
            }

            // An absent requested profile (null -> ""). Equivalent live spellings are null, "" and whitespace.
            var absent = new GeneratedComponent
            {
                Name = "profileConfigPath",
                Kind = kind,
                CanonicalRequested = string.Empty,
                RawRequested = c.Pick(new string?[] { null, string.Empty, "   " })
            };
            if (kind == VariantKind.Different)
            {
                absent.CanonicalLive = root + c.Pick(ProfileNames) + @"\config.json";
                absent.RawLive = ApplyPathVariant(c, absent.CanonicalLive, PickEquivalentKind(c));
            }
            else
            {
                absent.CanonicalLive = string.Empty;
                absent.RawLive = c.Pick(new string?[] { null, string.Empty, "  " });
            }
            return absent;
        }

        private static GeneratedComponent BuildPathComponent(PbtCase c, string name, string requested, string different, bool allowDifferent) =>
            BuildPathComponent(c, name, requested, different, PickKind(c, allowDifferent));

        private static GeneratedComponent BuildPathComponent(PbtCase c, string name, string requested, string different, VariantKind kind)
        {
            var component = new GeneratedComponent
            {
                Name = name,
                Kind = kind,
                CanonicalRequested = requested,
                RawRequested = ApplyPathVariant(c, requested, PickEquivalentKind(c))
            };

            if (kind == VariantKind.Different)
            {
                component.CanonicalLive = different;
                component.RawLive = different.Length == 0 ? different : ApplyPathVariant(c, different, PickEquivalentKind(c));
            }
            else
            {
                component.CanonicalLive = requested;
                component.RawLive = ApplyPathVariant(c, requested, kind);
            }
            return component;
        }

        private static GeneratedComponent GenerateToken(PbtCase c, string name, string[] values, bool allowDifferent)
        {
            var kind = PickKind(c, allowDifferent);
            string requested = c.Pick(values);
            var component = new GeneratedComponent
            {
                Name = name,
                Kind = kind,
                CanonicalRequested = requested,
                RawRequested = ApplyTokenVariant(c, requested, PickEquivalentKind(c))
            };

            if (kind == VariantKind.Different)
            {
                component.CanonicalLive = PickOther(c, values, requested);
                component.RawLive = ApplyTokenVariant(c, component.CanonicalLive, PickEquivalentKind(c));
            }
            else
            {
                component.CanonicalLive = requested;
                component.RawLive = ApplyTokenVariant(c, requested, kind);
            }
            return component;
        }

        private static VariantKind PickKind(PbtCase c, bool allowDifferent) =>
            allowDifferent
                ? c.Pick(new[] { VariantKind.Equal, VariantKind.CaseVariant, VariantKind.TrailingSeparatorVariant, VariantKind.Different })
                : PickEquivalentKind(c);

        private static VariantKind PickEquivalentKind(PbtCase c) =>
            c.Pick(new[] { VariantKind.Equal, VariantKind.CaseVariant, VariantKind.TrailingSeparatorVariant });

        // A value that differs from the excluded one beyond case, separators and whitespace (all lists are lowercase
        // and distinct).
        private static string PickOther(PbtCase c, string[] values, string excluded) =>
            c.Pick(values.Where(value => !string.Equals(value, excluded, StringComparison.Ordinal)).ToArray());

        // ---------------------------------------------------------------
        // Variants of a canonical value (all of them normalize back to it)
        // ---------------------------------------------------------------

        private static string ApplyPathVariant(PbtCase c, string canonical, VariantKind kind)
        {
            switch (kind)
            {
                case VariantKind.CaseVariant:
                    return ChangeCase(c, canonical);
                case VariantKind.TrailingSeparatorVariant:
                    return canonical + c.Pick(new[] { "\\", "/", "\\\\" });
                default:
                    return canonical;
            }
        }

        private static string ApplyTokenVariant(PbtCase c, string canonical, VariantKind kind)
        {
            switch (kind)
            {
                case VariantKind.CaseVariant:
                    return ChangeCase(c, canonical);
                case VariantKind.TrailingSeparatorVariant:
                    // Tokens have no separators; their formatting variant is surrounding whitespace.
                    return c.Pick(new[] { " ", "\t", "  " }) + canonical + c.Pick(new[] { "", " ", "\t" });
                default:
                    return canonical;
            }
        }

        // Upper-cases a random subset of letters; falls back to all upper case so a value with letters always changes.
        private static string ChangeCase(PbtCase c, string canonical)
        {
            var builder = new StringBuilder(canonical.Length);
            foreach (char ch in canonical)
                builder.Append(char.IsLetter(ch) && c.Bool() ? char.ToUpperInvariant(ch) : ch);
            string variant = builder.ToString();
            return variant == canonical ? canonical.ToUpperInvariant() : variant;
        }

        // ---------------------------------------------------------------
        // Construction through the pinned contract
        // ---------------------------------------------------------------

        private static SharedWorkerIdentity CreateRequested(GeneratedPair pair) =>
            SharedWorkerIdentity.Create(
                pair["workerExecutable"].RawRequested,
                pair.RawRequestedKb,
                pair["installationPath"].RawRequested,
                pair["driver"].RawRequested,
                pair["major"].RawRequested,
                pair["profileConfigPath"].RawRequested);

        // The live record carries raw, unnormalized spellings, as another Gateway or an older host may have written
        // them. Its key is the identity hash of those live values, as a real record would carry.
        private static SharedHostRecordIdentity CreateLive(GeneratedPair pair, string rawLiveKb)
        {
            var liveIdentity = SharedWorkerIdentity.Create(
                pair["workerExecutable"].RawLive,
                rawLiveKb,
                pair["installationPath"].RawLive,
                pair["driver"].RawLive,
                pair["major"].RawLive,
                pair["profileConfigPath"].RawLive);

            return new SharedHostRecordIdentity
            {
                Key = liveIdentity.Key,
                KbPath = rawLiveKb,
                WorkerExecutable = pair["workerExecutable"].RawLive ?? string.Empty,
                InstallationPath = pair["installationPath"].RawLive ?? string.Empty,
                Driver = pair["driver"].RawLive ?? string.Empty,
                Major = pair["major"].RawLive ?? string.Empty,
                ProfileConfigPath = pair["profileConfigPath"].RawLive
            };
        }
    }
}
