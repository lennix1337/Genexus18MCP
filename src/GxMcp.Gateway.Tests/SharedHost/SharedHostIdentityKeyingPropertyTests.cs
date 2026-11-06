#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GxMcp.Common.SharedHost;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 11: Identity keying
    //
    // For any KB directory and any variant of its path that differs only by letter case, trailing directory
    // separator, '.\' or 'x\..\' segments, or relative versus absolute form, the KB key and the identity key are
    // equal. Identities of one KB directory that differ in any other component (installation, driver, major,
    // Worker executable, profile config path) have different identity keys and the same KB key. Different KB
    // directories have different KB keys. For any set of aliases, the number of distinct identity keys equals the
    // number of distinct physical identities, and aliases group by identity key exactly as they group by identity.
    //
    // **Validates: Requirements 1.5, 2.1, 5.2**
    //
    // Scope: this file covers the keying contract that exists today: SharedWorkerIdentity.Create, its Key, KbKey
    // and KbPath, and SharedHostKbKey.Compute. The second half of the design statement, that the shared-mode pool
    // counts one open KB per distinct identity key toward Server.MaxOpenKbs, needs the pool seam pinned by task 5.3
    // and implemented by task 5.8 (WorkerPool shared-mode keying with aliasesByKey). That seam does not exist yet,
    // so the alias property below stops at the identity-key level; the pool-level count waits for 5.8.
    //
    // Oracle: every KB directory and component starts from a canonical value that is already in normalized form
    // (absolute, lowercase, no trailing separator, no dot segments). Raw spellings are derived from it by
    // construction, so the expected normalized value is the canonical value and the expected KB key is the SHA-256
    // of its UTF-8 bytes. The oracle never calls the normalization code under test.
    public class SharedHostIdentityKeyingPropertyTests
    {
        private const string PropertyName = "Feature: concurrent-multi-session-workers, Property 11: Identity keying";

        private const int Iterations = 200;

        // Fixed base seeds keep every run deterministic; GXMCP_PBT_SEED still replays one reported case.
        private const int VariantBaseSeed = 1_100_011;
        private const int ComponentBaseSeed = 1_100_012;
        private const int OtherKbBaseSeed = 1_100_013;
        private const int AliasBaseSeed = 1_100_014;

        // Canonical (normalized) values. Neutral placeholders: they name no real KB, installation or Worker build.
        private static readonly string[] KbDirectories =
        {
            @"c:\kbs\kba", @"c:\kbs\kbb", @"c:\kbs\kbc", @"c:\kbs\team\kbd", @"c:\kbs\kba-copy"
        };
        private static readonly string[] Installations = { @"c:\gx\install18", @"c:\gx\install17", @"c:\gx\install16" };
        private static readonly string[] Drivers = { "native-sdk", "dotnet-reflection", "com-gxpublic" };
        private static readonly string[] Majors = { "18", "17", "16" };
        private static readonly string[] Workers =
        {
            @"c:\gx\mcp-a\worker\gxmcp.worker.exe", @"c:\gx\mcp-b\worker\gxmcp.worker.exe"
        };
        // "" is the canonical form of an absent profile config path.
        private static readonly string[] Profiles = { "", @"c:\gx\profiles\a\config.json", @"c:\gx\profiles\b\config.json" };

        private static readonly string[] ComponentNames =
        {
            "installationPath", "driver", "major", "workerExecutable", "profileConfigPath"
        };

        // One physical identity in canonical form.
        private sealed class CanonicalIdentity
        {
            internal string Kb = string.Empty;
            internal string Installation = string.Empty;
            internal string Driver = string.Empty;
            internal string Major = string.Empty;
            internal string Worker = string.Empty;
            internal string Profile = string.Empty;

            internal string Tuple => string.Join("|", Kb, Installation, Driver, Major, Worker, Profile);

            internal CanonicalIdentity With(string component, string value)
            {
                var copy = (CanonicalIdentity)MemberwiseClone();
                switch (component)
                {
                    case "installationPath": copy.Installation = value; break;
                    case "driver": copy.Driver = value; break;
                    case "major": copy.Major = value; break;
                    case "workerExecutable": copy.Worker = value; break;
                    default: copy.Profile = value; break;
                }
                return copy;
            }
        }

        [Fact]
        public void Property11_PathVariantsOfOneKbDirectoryShareKbKeyAndIdentityKey()
        {
            Run(" (path variants)", VariantBaseSeed, c =>
            {
                var identity = GenerateIdentity(c);
                string expectedKbKey = OracleKbKey(identity.Kb);

                var first = Create(c, identity, "first");
                var second = Create(c, identity, "second");

                Assert.Equal(identity.Kb, first.KbPath);
                Assert.Equal(identity.Kb, second.KbPath);
                Assert.Equal(expectedKbKey, first.KbKey);
                Assert.Equal(expectedKbKey, second.KbKey);
                Assert.Equal(first.Key, second.Key);

                // The shared kbKey rule gives the same key for a raw variant as for the canonical directory.
                string rawKb = SpellPath(c, identity.Kb, allowRelative: true);
                c.Log("SharedHostKbKey.Compute raw=" + Show(rawKb));
                Assert.Equal(expectedKbKey, SharedHostKbKey.Compute(rawKb));
            });
        }

        [Fact]
        public void Property11_IdentitiesThatDifferInAnyComponentHaveDifferentKeysAndTheSameKbKey()
        {
            Run(" (component differs)", ComponentBaseSeed, c =>
            {
                var requested = GenerateIdentity(c);

                // Change a non-empty subset of the five components to a truly different canonical value.
                var changed = ComponentNames.Where(_ => c.Bool()).ToList();
                if (changed.Count == 0) changed.Add(c.Pick(ComponentNames));
                var other = requested;
                foreach (string component in changed)
                    other = other.With(component, PickOther(c, PoolOf(component), ValueOf(requested, component)));
                c.Log("changed components: " + string.Join(",", changed));

                var a = Create(c, requested, "requested");
                var b = Create(c, other, "other");

                Assert.NotEqual(a.Key, b.Key);
                Assert.Equal(a.KbKey, b.KbKey);
                Assert.Equal(OracleKbKey(requested.Kb), b.KbKey);
            });
        }

        [Fact]
        public void Property11_DifferentKbDirectoriesHaveDifferentKbKeys()
        {
            Run(" (different KB directory)", OtherKbBaseSeed, c =>
            {
                var requested = GenerateIdentity(c);
                var other = new CanonicalIdentity
                {
                    Kb = PickOther(c, KbDirectories, requested.Kb),
                    // The other components are either the same or independently generated.
                    Installation = c.Bool() ? requested.Installation : c.Pick(Installations),
                    Driver = c.Bool() ? requested.Driver : c.Pick(Drivers),
                    Major = c.Bool() ? requested.Major : c.Pick(Majors),
                    Worker = c.Bool() ? requested.Worker : c.Pick(Workers),
                    Profile = c.Bool() ? requested.Profile : c.Pick(Profiles)
                };

                var a = Create(c, requested, "requested");
                var b = Create(c, other, "other");

                Assert.NotEqual(a.KbKey, b.KbKey);
                Assert.NotEqual(a.Key, b.Key);
                Assert.Equal(OracleKbKey(other.Kb), b.KbKey);
            });
        }

        [Fact]
        public void Property11_AliasesCollapseToOneIdentityKeyPerPhysicalIdentity()
        {
            Run(" (alias set)", AliasBaseSeed, c =>
            {
                // A small pool of physical identities, so that several aliases often resolve to the same one.
                int poolSize = c.SmallInt(1, 4);
                var pool = Enumerable.Range(0, poolSize).Select(_ => GenerateIdentity(c)).ToList();
                int aliasCount = c.Int(2, 8);

                var aliases = new List<(string Alias, CanonicalIdentity Canonical, SharedWorkerIdentity Created)>();
                for (int i = 0; i < aliasCount; i++)
                {
                    string alias = "alias" + i.ToString(CultureInfo.InvariantCulture);
                    var canonical = c.Pick(pool);
                    aliases.Add((alias, canonical, Create(c, canonical, alias)));
                }

                var expectedGroups = aliases
                    .GroupBy(entry => entry.Canonical.Tuple, StringComparer.Ordinal)
                    .Select(group => string.Join(",", group.Select(entry => entry.Alias).OrderBy(n => n, StringComparer.Ordinal)))
                    .OrderBy(text => text, StringComparer.Ordinal)
                    .ToArray();
                var actualGroups = aliases
                    .GroupBy(entry => entry.Created.Key, StringComparer.Ordinal)
                    .Select(group => string.Join(",", group.Select(entry => entry.Alias).OrderBy(n => n, StringComparer.Ordinal)))
                    .OrderBy(text => text, StringComparer.Ordinal)
                    .ToArray();

                c.Log("expected groups: " + string.Join(" | ", expectedGroups));
                c.Log("actual groups:   " + string.Join(" | ", actualGroups));
                Assert.Equal(expectedGroups.Length, aliases.Select(entry => entry.Created.Key).Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(expectedGroups, actualGroups);

                // KB keys group by KB directory alone.
                int expectedKbCount = aliases.Select(entry => entry.Canonical.Kb).Distinct(StringComparer.Ordinal).Count();
                Assert.Equal(expectedKbCount, aliases.Select(entry => entry.Created.KbKey).Distinct(StringComparer.Ordinal).Count());
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

        private static string Show(string? value) => value == null ? "<null>" : "'" + value + "'";

        // ---------------------------------------------------------------
        // Generators
        // ---------------------------------------------------------------

        private static CanonicalIdentity GenerateIdentity(PbtCase c) =>
            new CanonicalIdentity
            {
                Kb = c.Pick(KbDirectories),
                Installation = c.Pick(Installations),
                Driver = c.Pick(Drivers),
                Major = c.Pick(Majors),
                Worker = c.Pick(Workers),
                Profile = c.Pick(Profiles)
            };

        private static string[] PoolOf(string component)
        {
            switch (component)
            {
                case "installationPath": return Installations;
                case "driver": return Drivers;
                case "major": return Majors;
                case "workerExecutable": return Workers;
                default: return Profiles;
            }
        }

        private static string ValueOf(CanonicalIdentity identity, string component)
        {
            switch (component)
            {
                case "installationPath": return identity.Installation;
                case "driver": return identity.Driver;
                case "major": return identity.Major;
                case "workerExecutable": return identity.Worker;
                default: return identity.Profile;
            }
        }

        private static string PickOther(PbtCase c, string[] values, string excluded) =>
            c.Pick(values.Where(value => !string.Equals(value, excluded, StringComparison.Ordinal)).ToArray());

        // Creates the identity from raw spellings of every canonical component and logs them.
        private static SharedWorkerIdentity Create(PbtCase c, CanonicalIdentity canonical, string label)
        {
            string kb = SpellPath(c, canonical.Kb, allowRelative: true);
            string installation = SpellPath(c, canonical.Installation, allowRelative: false);
            string driver = SpellToken(c, canonical.Driver);
            string major = SpellToken(c, canonical.Major);
            string worker = SpellPath(c, canonical.Worker, allowRelative: false);
            string? profile = canonical.Profile.Length == 0
                ? c.Pick(new string?[] { null, string.Empty, "  " })
                : SpellPath(c, canonical.Profile, allowRelative: false);

            var identity = SharedWorkerIdentity.Create(worker, kb, installation, driver, major, profile);
            c.Log(label + ": canonical=" + canonical.Tuple
                + " raw kb=" + Show(kb) + " installation=" + Show(installation) + " driver=" + Show(driver)
                + " major=" + Show(major) + " worker=" + Show(worker) + " profile=" + Show(profile)
                + " -> kbPath=" + Show(identity.KbPath) + " kbKey=" + identity.KbKey + " key=" + identity.Key);
            return identity;
        }

        // ---------------------------------------------------------------
        // Spellings of a canonical value (all of them normalize back to it)
        // ---------------------------------------------------------------

        // Applies a random combination of: a '.\' segment, an 'x\..\' segment, forward slashes, relative form
        // against the current directory, letter case, and trailing separators.
        private static string SpellPath(PbtCase c, string canonical, bool allowRelative)
        {
            string root = canonical.Substring(0, 3);              // "c:\"
            var segments = canonical.Substring(3).Split('\\').ToList();

            if (c.Chance(0.3)) segments.Insert(c.Int(0, segments.Count - 1), ".");
            if (c.Chance(0.3)) segments.Insert(c.Int(0, segments.Count - 1), "x\\..");
            string path = root + string.Join("\\", segments);
            if (c.Chance(0.2)) path = path.Replace('\\', '/');

            if (allowRelative && c.Chance(0.35))
            {
                string current = Directory.GetCurrentDirectory();
                string relative = Path.GetRelativePath(current, path);
                if (Path.IsPathRooted(relative))
                    c.Log("relative form unavailable from " + current + " (different root)");
                else
                    path = relative;
            }

            if (c.Chance(0.5)) path = ChangeCase(c, path);
            if (c.Chance(0.4)) path += c.Pick(new[] { "\\", "/", "\\\\", "\\/" });
            return path;
        }

        private static string SpellToken(PbtCase c, string canonical)
        {
            string token = c.Chance(0.5) ? ChangeCase(c, canonical) : canonical;
            if (c.Chance(0.3)) token = c.Pick(new[] { " ", "\t", "  " }) + token + c.Pick(new[] { "", " ", "\t" });
            return token;
        }

        // Upper-cases a random subset of letters; falls back to all upper case so a value with letters always changes.
        private static string ChangeCase(PbtCase c, string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char ch in value)
                builder.Append(char.IsLetter(ch) && c.Bool() ? char.ToUpperInvariant(ch) : ch);
            string variant = builder.ToString();
            return variant == value ? value.ToUpperInvariant() : variant;
        }

        // ---------------------------------------------------------------
        // Oracle
        // ---------------------------------------------------------------

        // Design decision A3: lowercase hex SHA-256 of the UTF-8 bytes of the normalized KB directory.
        private static string OracleKbKey(string canonicalKb)
        {
            using var sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(new UTF8Encoding(false).GetBytes(canonicalKb));
            var key = new StringBuilder(digest.Length * 2);
            foreach (byte value in digest)
                key.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return key.ToString();
        }
    }
}
